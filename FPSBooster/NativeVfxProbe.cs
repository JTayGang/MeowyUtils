using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Lumina.Data;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;
using FFXIVClientStructs.FFXIV.Client.Graphics.Vfx;
using VfxObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject;
using GameQuaternion = FFXIVClientStructs.FFXIV.Common.Math.Quaternion;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;

namespace FPSBooster;

/// <summary>
/// Opt-in native AVFX experiments. Static and actor VFX use the game's own scheduler and
/// resource manager. Paths are restricted to existing vanilla game files because asking the
/// native loader to open an invalid path can crash the client.
/// </summary>
public sealed unsafe class NativeVfxProbe : IDisposable
{
    private const string StaticVfxRunSignature = "E8 ?? ?? ?? ?? B0 02 EB 02";
    private const string StaticVfxRemoveSignature = "40 53 48 83 EC 20 48 8B D9 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 28 33 D2 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9";
    private const string ActorVfxCreateSignature = "40 53 55 56 57 48 81 EC ?? ?? ?? ?? 0F 29 B4 24 ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 0F B6 AC 24 ?? ?? ?? ?? 0F 28 F3 49 8B F8";
    private const string ActorVfxRemoveSignature = "0F 11 48 10 48 8D 05";
    private const string StaticVfxPoolName = "Client.System.Scheduler.Instance.VfxObject";
    private const int MaximumTrackedInstances = 16;
    private const int EmbeddedSceneCameraOffset = 0x10;

    private delegate IntPtr StaticVfxRunDelegate(VfxObject* vfx, float a1, uint a2);
    private delegate IntPtr StaticVfxRemoveDelegate(VfxObject* vfx);
    private delegate VfxData* ActorVfxCreateDelegate(byte* path, GameObject* caster, GameObject* target, float a4, char a5, ushort a6, char a7);
    private delegate IntPtr ActorVfxRemoveDelegate(VfxData* vfx, char a2);

    private sealed class ActiveVfx
    {
        public required nint Address { get; init; }
        public required string Path { get; init; }
        public required bool IsActor { get; init; }
        public bool IsNativeCameraBinder { get; init; }
        public bool IsPenumbraModifiedCameraBinder { get; init; }
    }

    public enum CameraTechniqueTestStage
    {
        Idle,
        NativeCameraBinderControl,
        InjectedCameraBinder,
        Completed,
        Failed,
        Cancelled,
    }

    private sealed class CameraTechniqueTrial
    {
        public required string Label { get; init; }
        public required string Path { get; init; }
        public nint VfxAddress { get; set; }
        public long StartTicks { get; set; }
        public int Samples { get; set; }
        public int OtherParentSamples { get; set; }
        public int NullParentSamples { get; set; }
        public int MissingSamples { get; set; }
        public bool HasInitialCamera { get; set; }
        public System.Numerics.Vector3 InitialCameraPosition { get; set; }
        public System.Numerics.Vector3 LastCameraForward { get; set; }
        public float MaximumCameraTranslation { get; set; }
        public double AccumulatedCameraRotationDegrees { get; set; }
        public bool HasInitialVfxPosition { get; set; }
        public System.Numerics.Vector3 InitialVfxPosition { get; set; }
        public System.Numerics.Vector3 LastVfxPosition { get; set; }
        public float MaximumVfxPositionDrift { get; set; }
        public string Summary { get; set; } = "Not run.";
    }

    private sealed class CameraTechniqueAutoTest
    {
        public CameraTechniqueTestStage Stage { get; set; }
        public required string InjectionTargetPath { get; init; }
        public required string TemplatePath { get; init; }
        public required int TemplateBinderIndex { get; init; }
        public required long PhaseDurationTicks { get; init; }
        public required CameraTechniqueTrial NativeBinderTrial { get; init; }
        public required CameraTechniqueTrial InjectedBinderTrial { get; init; }
        public CameraTechniqueTrial? CurrentTrial { get; set; }
        public string Status { get; set; } = "Preparing test.";
        public string ValidationSummary { get; set; } = string.Empty;
        public string PenumbraReportAtInjection { get; set; } = "Not prepared.";
        public string PenumbraCleanupStatus { get; set; } = "Not cleaned up yet.";
    }

    /// <summary>
    /// Minimal camera sample used only to measure camera movement during the native-versus-
    /// injected AVFX Camera-binder comparison. It is never used to position or update VFX.
    /// </summary>
    private readonly struct CameraMotionSample
    {
        public readonly System.Numerics.Vector3 Position;
        public readonly System.Numerics.Vector3 Forward;

        public CameraMotionSample(System.Numerics.Vector3 position, System.Numerics.Vector3 forward)
        {
            Position = position;
            Forward = forward;
        }
    }

    private readonly IDataManager _dataManager;
    private readonly IObjectTable _objectTable;
    private readonly IFramework _framework;
    private readonly PenumbraAvfxReplacementProbe _penumbraAvfxReplacement;
    private readonly IGameInteropProvider _interop;
    private readonly ISigScanner _sigScanner;
    private readonly IPluginLog _log;
    private readonly ConcurrentDictionary<nint, ActiveVfx> _active = new();

    private StaticVfxRunDelegate? _staticVfxRun;
    private Hook<StaticVfxRemoveDelegate>? _staticVfxRemoveHook;
    private ActorVfxCreateDelegate? _actorVfxCreate;
    private Hook<ActorVfxRemoveDelegate>? _actorVfxRemoveHook;
    private bool _initializationAttempted;
    private bool _disposed;
    private bool _subscribedToUpdate;
    private string _binderInspectionPath = string.Empty;
    private string _lastInspectedPath = string.Empty;
    private AvfxBinderInspector.Result? _binderInspection;
    private CameraTechniqueAutoTest? _cameraTechniqueAutoTest;

    public string StaticStatus { get; private set; } = "Not initialized.";
    public string ActorStatus { get; private set; } = "Not initialized.";
    public string LastAction { get; private set; } = "No VFX test has been run.";
    public int ActiveCount => _active.Count;
    public string BinderInspectionReport => _binderInspection?.Report ?? "No AVFX asset inspected yet.";
    public int InspectedBinderCount => _binderInspection?.BinderCount ?? -1;
    public int InspectedTimelineCount => _binderInspection?.Timelines.Count ?? 0;
    public string BinderInspectionStatus => _binderInspection == null
        ? "Not inspected."
        : _binderInspection.Success
            ? $"Inspected {_binderInspectionPath} · {_binderInspection.BinderCount} binder(s), {_binderInspection.CameraBinderCount} Camera binder(s) · count {(_binderInspection.DeclaredBinderCount.HasValue ? (_binderInspection.BinderCountMatchesDeclaration ? "validated" : "mismatch") : "missing")}."
            : _binderInspection.Error;
    public bool CanSpawnNativeCameraBinderFor(string? path) => _binderInspection?.Success == true
        && _binderInspection.CameraBinderCount > 0
        && string.Equals(_binderInspectionPath, _lastInspectedPath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(path?.Trim(), _binderInspectionPath, StringComparison.OrdinalIgnoreCase);
    public bool TryGetFirstCameraBinderIndex(string? path, out int binderIndex)
    {
        binderIndex = -1;
        if (_binderInspection?.Success != true
            || !string.Equals(_binderInspectionPath, _lastInspectedPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(path?.Trim(), _binderInspectionPath, StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var binder in _binderInspection.Binders)
        {
            if (binder.TypeValue != 3)
                continue;
            binderIndex = binder.Index;
            return true;
        }
        return false;
    }
    public bool StaticReady => _staticVfxRun != null && _staticVfxRemoveHook != null;
    public bool ActorReady => _actorVfxCreate != null && _actorVfxRemoveHook != null;
    public bool CameraTechniqueAutoTestRunning => _cameraTechniqueAutoTest != null
        && (_cameraTechniqueAutoTest.Stage is CameraTechniqueTestStage.NativeCameraBinderControl
            or CameraTechniqueTestStage.InjectedCameraBinder);
    public bool CameraTechniqueAutoTestCompleted => _cameraTechniqueAutoTest?.Stage == CameraTechniqueTestStage.Completed;
    public CameraTechniqueTestStage CameraTechniqueAutoTestCurrentStage => _cameraTechniqueAutoTest?.Stage ?? CameraTechniqueTestStage.Idle;
    public string CameraTechniqueAutoTestStatus => _cameraTechniqueAutoTest?.Status ?? "No automated camera-technique test has been run.";
    public string CameraTechniqueAutoTestSummaries => _cameraTechniqueAutoTest == null
        ? "No automated test results."
        : _cameraTechniqueAutoTest.NativeBinderTrial.Summary + "\n\n"
          + _cameraTechniqueAutoTest.InjectedBinderTrial.Summary;

    public NativeVfxProbe(
        IDataManager dataManager,
        IObjectTable objectTable,
        IFramework framework,
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        IPluginLog log,
        PenumbraAvfxReplacementProbe penumbraAvfxReplacement)
    {
        _dataManager = dataManager;
        _objectTable = objectTable;
        _framework = framework;
        _penumbraAvfxReplacement = penumbraAvfxReplacement;
        _interop = interop;
        _sigScanner = sigScanner;
        _log = log;
    }

    /// <summary>Scans and installs only the removal hooks needed to safely track our instances.</summary>
    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initializationAttempted)
            return;
        _initializationAttempted = true;

        try
        {
            nint runCall = ResolveUniqueTextSignature(StaticVfxRunSignature, "StaticVfxRun call site");
            nint runAddress = ResolveRelativeCallTarget(runCall, "StaticVfxRun");
            nint removeAddress = ResolveUniqueTextSignature(StaticVfxRemoveSignature, "StaticVfxRemove");
            _staticVfxRun = Marshal.GetDelegateForFunctionPointer<StaticVfxRunDelegate>(runAddress);
            _staticVfxRemoveHook = _interop.HookFromAddress<StaticVfxRemoveDelegate>(removeAddress, StaticVfxRemoveDetour);
            _staticVfxRemoveHook.Enable();
            StaticStatus = $"Ready · Run 0x{(nuint)runAddress:X} · Remove hook 0x{(nuint)removeAddress:X}";
        }
        catch (Exception ex)
        {
            _staticVfxRun = null;
            try { _staticVfxRemoveHook?.Dispose(); } catch { }
            _staticVfxRemoveHook = null;
            StaticStatus = "Unavailable: " + ex.Message;
            _log.Warning(ex, "FPSBooster: native static VFX interop could not be initialized.");
        }

        try
        {
            nint createAddress = ResolveUniqueTextSignature(ActorVfxCreateSignature, "ActorVfxCreate");
            nint removeReference = ResolveUniqueTextSignature(ActorVfxRemoveSignature, "ActorVfxRemove reference");
            nint removeAddress = ResolveActorVfxRemoveAddress(removeReference);
            nint textBase = _sigScanner.Module.BaseAddress + (nint)_sigScanner.TextSectionOffset;
            nint textEnd = textBase + _sigScanner.TextSectionSize;
            if (removeAddress < textBase || removeAddress >= textEnd)
                throw new InvalidOperationException("The resolved ActorVfxRemove pointer is outside the game .text section.");
            _actorVfxCreate = Marshal.GetDelegateForFunctionPointer<ActorVfxCreateDelegate>(createAddress);
            _actorVfxRemoveHook = _interop.HookFromAddress<ActorVfxRemoveDelegate>(removeAddress, ActorVfxRemoveDetour);
            _actorVfxRemoveHook.Enable();
            ActorStatus = $"Ready · Create 0x{(nuint)createAddress:X} · Remove hook 0x{(nuint)removeAddress:X}";
        }
        catch (Exception ex)
        {
            _actorVfxCreate = null;
            try { _actorVfxRemoveHook?.Dispose(); } catch { }
            _actorVfxRemoveHook = null;
            ActorStatus = "Unavailable: " + ex.Message;
            _log.Warning(ex, "FPSBooster: native actor VFX interop could not be initialized.");
        }

        if (StaticReady && !_subscribedToUpdate)
        {
            _framework.Update += OnFrameworkUpdate;
            _subscribedToUpdate = true;
        }

        LastAction = StaticReady || ActorReady
            ? "Native VFX interop initialized."
            : "No native VFX route initialized; inspect the status messages above.";
    }

    public bool ValidateGamePath(string? input, out string normalizedPath, out string error)
    {
        normalizedPath = input?.Trim() ?? string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            error = "Enter a game .avfx path.";
            return false;
        }

        if (normalizedPath.Length >= 260 || System.IO.Path.IsPathRooted(normalizedPath)
            || normalizedPath.Contains('\\') || normalizedPath.Contains(':')
            || normalizedPath.Contains("..", StringComparison.Ordinal)
            || normalizedPath.Contains('\0'))
        {
            error = "Use a relative game path with forward slashes; absolute paths and traversal are rejected.";
            return false;
        }

        if (!normalizedPath.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase))
        {
            error = "The path must end in .avfx.";
            return false;
        }

        try
        {
            if (!_dataManager.FileExists(normalizedPath))
            {
                error = "That path is not present in the vanilla game index. Custom/Penumbra-only paths are not enabled in this first test because the native loader must not receive an unverified path.";
                return false;
            }
        }
        catch (Exception ex)
        {
            error = "Game-file validation failed: " + ex.Message;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads the vanilla AVFX file through Lumina and inspects its binder metadata.
    /// This is a read-only parser; it does not rewrite or patch the asset.
    /// </summary>
    public bool InspectAvfxBinders(string path)
    {
        if (!ValidateGamePath(path, out string normalized, out string error))
        {
            _binderInspection = null;
            _binderInspectionPath = string.Empty;
            _lastInspectedPath = string.Empty;
            return Fail(error);
        }

        try
        {
            FileResource? file = _dataManager.GetFile(normalized);
            if (file == null)
            {
                _binderInspection = null;
                _binderInspectionPath = string.Empty;
                _lastInspectedPath = string.Empty;
                return Fail("Lumina could not load that AVFX file from the game index.");
            }

            var result = AvfxBinderInspector.Inspect(file.Data);
            _binderInspection = result;
            _binderInspectionPath = normalized;
            _lastInspectedPath = result.Success ? normalized : string.Empty;
            LastAction = result.Success
                ? $"Inspected AVFX metadata · {result.BinderCount} binder(s), {result.CameraBinderCount} Camera binder(s)."
                : "AVFX inspection failed: " + result.Error;
            return result.Success;
        }
        catch (Exception ex)
        {
            _binderInspection = null;
            _binderInspectionPath = string.Empty;
            _lastInspectedPath = string.Empty;
            _log.Warning(ex, "FPSBooster: AVFX binder metadata inspection failed.");
            return Fail("AVFX metadata could not be read: " + ex.Message);
        }
    }

    /// <summary>
    /// Reads and inspects a vanilla game AVFX without changing the selector's current
    /// inspection state. Used by the automated native-versus-injected binder comparison.
    /// </summary>
    private bool TryInspectGameAvfx(
        string path,
        out string normalizedPath,
        out AvfxBinderInspector.Result inspection,
        out string error)
    {
        normalizedPath = string.Empty;
        inspection = null!;

        if (!ValidateGamePath(path, out normalizedPath, out error))
            return false;

        try
        {
            FileResource? file = _dataManager.GetFile(normalizedPath);
            if (file == null)
            {
                error = "Lumina could not load that AVFX file from the game index.";
                return false;
            }

            inspection = AvfxBinderInspector.Inspect(file.Data);
            if (!inspection.Success)
            {
                error = inspection.Error;
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = "AVFX metadata could not be read: " + ex.Message;
            _log.Warning(ex, "FPSBooster: automated AVFX inspection failed.");
            return false;
        }
    }

    /// <summary>
    /// Spawns a file that was just verified to contain a native Camera binder. The plugin
    /// deliberately does not write Position/Rotation/Scale or update transforms: the AVFX
    /// definition and game scheduler own placement through the native binder.
    /// </summary>
    public bool SpawnNativeCameraBinder(string path)
    {
        if (!EnsureReadyForStatic() || !EnsureCapacity())
            return false;
        if (!ValidateGamePath(path, out string normalized, out string error))
            return Fail(error);
        if (!CanSpawnNativeCameraBinderFor(normalized))
            return Fail("Inspect this exact AVFX path first. The original asset must contain at least one BnVr=3 Camera binder.");

        VfxObject* vfx = null;
        try
        {
            vfx = VfxObject.Create(normalized, StaticVfxPoolName);
            if (vfx == null)
                return Fail("VfxObject.Create returned null for the Camera-binder asset.");

            // Intentionally do not set Position, Rotation, Scale, or call UpdateTransforms.
            // This test asks the native AVFX binder and scheduler to own placement.
            _staticVfxRun!(vfx, 0f, 0xFFFFFFFF);

            _active[(nint)vfx] = new ActiveVfx
            {
                Address = (nint)vfx,
                Path = normalized,
                IsActor = false,
                IsNativeCameraBinder = true,
            };

            LastAction = $"Spawned native AVFX Camera-binder without plugin transform updates · {normalized}";
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "FPSBooster: native Camera-binder spawn failed.");
            if (vfx != null)
            {
                try { _staticVfxRemoveHook?.Original(vfx); }
                catch (Exception cleanupEx) { _log.Warning(cleanupEx, "FPSBooster: failed to clean up Camera-binder VFX after spawn error."); }
            }
            return Fail("Native Camera-binder spawn failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Spawns the original game resource path while Penumbra redirects it to the prepared
    /// modified loose AVFX file. The native VFX object is left transform-untouched so the
    /// rewritten Camera binder is responsible for placement.
    /// </summary>
    public bool SpawnPenumbraModifiedCameraBinder(string path)
    {
        if (!EnsureReadyForStatic() || !EnsureCapacity())
            return false;
        if (!ValidateGamePath(path, out string normalized, out string error))
            return Fail(error);
        if (!_penumbraAvfxReplacement.CanSpawnFor(normalized))
            return Fail("The Penumbra replacement is not active and ResolvePlayerPath-verified for this exact game path. Prepare the replacement and verify it before spawning.");
        // Recheck on the click itself so a collection switch since the last UI draw cannot
        // accidentally spawn the unmodified base resource while the stale button looked enabled.
        if (!_penumbraAvfxReplacement.RecheckResolution(normalized))
            return Fail("The Penumbra redirect stopped resolving to the generated file. Spawn was blocked; see the replacement report.");

        VfxObject* vfx = null;
        try
        {
            // Keep passing the original game path. Penumbra should redirect this request to
            // the modified loose AVFX file in the local player's active collection.
            vfx = VfxObject.Create(normalized, StaticVfxPoolName);
            if (vfx == null)
                return Fail("VfxObject.Create returned null for the Penumbra-redirected AVFX path.");

            _staticVfxRun!(vfx, 0f, 0xFFFFFFFF);
            _active[(nint)vfx] = new ActiveVfx
            {
                Address = (nint)vfx,
                Path = normalized,
                IsActor = false,
                IsNativeCameraBinder = true,
                IsPenumbraModifiedCameraBinder = true,
            };

            LastAction = $"Spawned native VFX from Penumbra-modified AVFX · {normalized}";
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "FPSBooster: Penumbra-modified Camera-binder VFX spawn failed.");
            if (vfx != null)
            {
                try { _staticVfxRemoveHook?.Original(vfx); }
                catch (Exception cleanupEx) { _log.Warning(cleanupEx, "FPSBooster: failed to clean up a redirected Camera-binder VFX after spawn error."); }
            }
            return Fail("Penumbra-modified Camera-binder spawn failed: " + ex.Message);
        }
    }

    public bool SpawnAtPlayer(string path, System.Numerics.Vector3 offset, System.Numerics.Vector3 scale, float rotation)
    {
        if (!EnsureReadyForStatic())
            return false;
        if (!EnsureCapacity())
            return false;
        if (!ValidateGamePath(path, out string normalized, out string error))
            return Fail(error);

        var player = _objectTable.LocalPlayer;
        if (player == null)
            return Fail("The local player is not available.");

        try
        {
            var position = player.Position + offset;
            var vfx = CreateStatic(normalized, position, scale, player.Rotation + rotation);
            if (vfx == null)
                return Fail("VfxObject.Create returned null. No instance was registered.");

            _active[(nint)vfx] = new ActiveVfx
            {
                Address = (nint)vfx,
                Path = normalized,
                IsActor = false,
            };
            LastAction = $"Spawned static AVFX at player position · {normalized}";
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "FPSBooster: static AVFX spawn failed.");
            return Fail("Static spawn failed: " + ex.Message);
        }
    }

    public bool SpawnAttachedToPlayer(string path)
    {
        if (!EnsureReadyForActor())
            return false;
        if (!EnsureCapacity())
            return false;
        if (!ValidateGamePath(path, out string normalized, out string error))
            return Fail(error);

        var player = _objectTable.LocalPlayer;
        if (player == null)
            return Fail("The local player is not available.");

        try
        {
            var nativePlayer = (GameObject*)player.Address;
            if (nativePlayer == null)
                return Fail("The local player native object pointer is null.");

            byte[] pathBytes = Encoding.UTF8.GetBytes(normalized + "\0");
            VfxData* vfx;
            fixed (byte* pathPointer = pathBytes)
                vfx = _actorVfxCreate!(pathPointer, nativePlayer, nativePlayer, -1f, (char)0, 0, (char)0);

            if (vfx == null)
                return Fail("ActorVfxCreate returned null. No instance was registered.");

            _active[(nint)vfx] = new ActiveVfx
            {
                Address = (nint)vfx,
                Path = normalized,
                IsActor = true,
            };
            LastAction = $"Attached native AVFX to the local player · {normalized}";
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "FPSBooster: actor AVFX spawn failed.");
            return Fail("Actor attachment failed: " + ex.Message);
        }
    }

    public bool StartCameraTechniqueAutoTest(
        string injectionTargetPath,
        string templatePath,
        int templateBinderIndex,
        float phaseDurationSeconds)
    {
        if (CameraTechniqueAutoTestRunning)
            return Fail("A Camera-binder comparison is already running.");
        if (_active.Count != 0)
            return Fail("Clear all tracked VFX before starting the automated test.");
        if (_penumbraAvfxReplacement.IsRegistered)
            return Fail("Remove the existing temporary Penumbra replacement before starting the automated test.");
        if (!EnsureReadyForStatic())
            return false;

        if (!TryInspectGameAvfx(templatePath, out string templateNormalized, out var templateInfo, out string error))
            return Fail("Native Camera-binder control / template asset: " + error);
        if (!TryInspectGameAvfx(injectionTargetPath, out string targetNormalized, out var targetInfo, out error))
            return Fail("Injection target asset: " + error);

        if (string.Equals(templateNormalized, targetNormalized, StringComparison.OrdinalIgnoreCase))
            return Fail("Choose different paths for the existing native Camera-binder control/template and the injection target.");
        if (templateInfo.CameraBinderCount < 1)
            return Fail("The native Camera-binder control/template must already contain at least one Camera binder.");
        if (targetInfo.BinderCount != 0)
            return Fail("The injection target already contains Binder blocks. Automatic injection currently supports zero-binder targets only.");
        if (targetInfo.Timelines.Count == 0)
            return Fail("The injection target has no TmLn blocks. Choose a timeline-based AVFX so the injected Camera binder can be assigned through TmLn.BnNo.");
        if (templateBinderIndex < 0 || templateBinderIndex >= templateInfo.Binders.Count
            || templateInfo.Binders[templateBinderIndex].TypeValue != 3)
            return Fail($"Template binder index {templateBinderIndex} is not a Camera binder. Choose a valid Camera binder index from the template report.");

        phaseDurationSeconds = Math.Clamp(phaseDurationSeconds, 3f, 30f);
        var state = new CameraTechniqueAutoTest
        {
            Stage = CameraTechniqueTestStage.Idle,
            InjectionTargetPath = targetNormalized,
            TemplatePath = templateNormalized,
            TemplateBinderIndex = templateBinderIndex,
            PhaseDurationTicks = (long)(phaseDurationSeconds * Stopwatch.Frequency),
            NativeBinderTrial = new CameraTechniqueTrial { Label = "Existing native Camera-binder control", Path = templateNormalized },
            InjectedBinderTrial = new CameraTechniqueTrial { Label = "Penumbra-injected Camera binder", Path = targetNormalized },
            ValidationSummary = $"Native control and injection target are distinct. Injection target has zero binders and {targetInfo.Timelines.Count} timeline(s). Native control/template contains {templateInfo.CameraBinderCount} Camera binder(s); template binder #{templateBinderIndex} is Camera (BnVr=3).",
        };

        _cameraTechniqueAutoTest = state;
        if (!StartCameraTechniqueTrial(state, CameraTechniqueTestStage.NativeCameraBinderControl))
            return FailCameraTechniqueTest(state, "Could not start the native Camera-binder control phase: " + LastAction);

        state.Status = "Phase 1/2 · Existing native Camera-binder control. Rotate the camera continuously.";
        LastAction = "Started native-versus-injected Camera-binder comparison.";
        return true;
    }

    public void CancelCameraTechniqueAutoTest()
    {
        var state = _cameraTechniqueAutoTest;
        if (state == null || !CameraTechniqueAutoTestRunning)
            return;

        ClearAll();
        bool replacementRemoved = !_penumbraAvfxReplacement.IsRegistered
            || _penumbraAvfxReplacement.RemoveTemporaryReplacement();
        if (state.CurrentTrial != null && state.CurrentTrial.Summary == "Running...")
        {
            double elapsed = (double)(Stopwatch.GetTimestamp() - state.CurrentTrial.StartTicks) / Stopwatch.Frequency;
            state.CurrentTrial.Summary = BuildCameraTrialSummary(state.CurrentTrial, elapsed) + "\nTrial stopped early by user cancellation.";
            state.CurrentTrial = null;
        }
        state.Stage = CameraTechniqueTestStage.Cancelled;
        state.PenumbraCleanupStatus = _penumbraAvfxReplacement.Status;
        state.Status = replacementRemoved
            ? "Automated camera-technique test cancelled; tracked VFX and temporary replacement were cleaned up."
            : "Test cancelled, but Penumbra could not remove the temporary replacement. See its report; the generated file was retained safely.";
    }

    public string BuildCameraTechniqueAutoTestReport(int nativeBinderVisualVerdict, int injectedBinderVisualVerdict)
    {
        var state = _cameraTechniqueAutoTest;
        if (state == null)
            return "No automated Camera-binder comparison has been run.";

        var sb = new StringBuilder();
        sb.AppendLine("FPSBooster native Camera-binder comparison");
        sb.AppendLine($"UTC: {DateTime.UtcNow:O}");
        sb.AppendLine($"Final state: {state.Stage}");
        sb.AppendLine(state.ValidationSummary);
        sb.AppendLine();
        sb.AppendLine(state.NativeBinderTrial.Summary);
        sb.AppendLine($"Visual review: {FormatVisualVerdict(nativeBinderVisualVerdict)}");
        sb.AppendLine();
        sb.AppendLine(state.InjectedBinderTrial.Summary);
        sb.AppendLine($"Visual review: {FormatVisualVerdict(injectedBinderVisualVerdict)}");
        sb.AppendLine();
        sb.AppendLine("Interpretation notes:");
        sb.AppendLine("- The native control confirms that an existing Camera-bound asset can be created through the native scheduler.");
        sb.AppendLine("- The injected phase checks Penumbra ResolvePlayerPath and the generated AVFX structure before calling VfxObject.Create with the original game path.");
        sb.AppendLine("- A fresh target path is required to reduce ambiguity from the game's native resource cache.");
        sb.AppendLine("- Visual follow behavior is recorded separately from native pointer and local-transform diagnostics.");
        if (nativeBinderVisualVerdict == 3 || injectedBinderVisualVerdict == 3)
            sb.AppendLine("- At least one phase recorded no visible VFX. That phase cannot establish follow/no-follow behavior; verify the original asset renders through static/world spawn, then restart before retesting the injected path.");
        sb.AppendLine("- Both phases use framework-update sampling. This is a controlled technique comparison, not a frame-perfect renderer capture.");
        sb.AppendLine();
        sb.AppendLine("Penumbra redirect evidence at injection time:");
        sb.AppendLine(state.PenumbraReportAtInjection);
        sb.AppendLine();
        sb.AppendLine("Penumbra cleanup: " + state.PenumbraCleanupStatus);
        return sb.ToString();
    }

    private bool StartCameraTechniqueTrial(CameraTechniqueAutoTest state, CameraTechniqueTestStage stage)
    {
        ClearAll();
        CameraTechniqueTrial trial;
        bool spawned;
        switch (stage)
        {
            case CameraTechniqueTestStage.NativeCameraBinderControl:
                trial = state.NativeBinderTrial;
                if (!InspectAvfxBinders(trial.Path))
                    return Fail("Could not inspect native Camera-binder control: " + BinderInspectionStatus);
                spawned = SpawnNativeCameraBinder(trial.Path);
                break;
            case CameraTechniqueTestStage.InjectedCameraBinder:
                trial = state.InjectedBinderTrial;
                spawned = SpawnPenumbraModifiedCameraBinder(trial.Path);
                break;
            default:
                return Fail("Unknown Camera-binder comparison phase.");
        }

        if (!spawned)
            return false;

        var instance = _active.Values.FirstOrDefault(x =>
            stage == CameraTechniqueTestStage.InjectedCameraBinder
                ? x.IsPenumbraModifiedCameraBinder
                : x.IsNativeCameraBinder && !x.IsPenumbraModifiedCameraBinder);
        if (instance == null)
        {
            ClearAll();
            return Fail("The phase spawned but its native VFX instance was not found in the tracking registry.");
        }

        trial.VfxAddress = instance.Address;
        trial.StartTicks = Stopwatch.GetTimestamp();
        trial.Samples = 0;
        trial.OtherParentSamples = 0;
        trial.NullParentSamples = 0;
        trial.MissingSamples = 0;
        trial.HasInitialCamera = false;
        trial.MaximumCameraTranslation = 0f;
        trial.AccumulatedCameraRotationDegrees = 0d;
        trial.HasInitialVfxPosition = false;
        trial.MaximumVfxPositionDrift = 0f;
        trial.Summary = "Running...";
        state.CurrentTrial = trial;
        state.Stage = stage;
        state.Status = stage switch
        {
            CameraTechniqueTestStage.NativeCameraBinderControl => "Phase 1/2 · Existing native Camera-binder control. Rotate the camera continuously.",
            _ => "Phase 2/2 · Penumbra-injected Camera binder. Rotate the camera continuously.",
        };
        return true;
    }

    private bool FailCameraTechniqueTest(CameraTechniqueAutoTest state, string message)
    {
        if (state.CurrentTrial != null && state.CurrentTrial.Summary == "Running...")
        {
            double elapsed = (double)(Stopwatch.GetTimestamp() - state.CurrentTrial.StartTicks) / Stopwatch.Frequency;
            state.CurrentTrial.Summary = BuildCameraTrialSummary(state.CurrentTrial, elapsed) + "\nTrial stopped early because the test failed.";
            state.CurrentTrial = null;
        }
        ClearAll();
        if (_penumbraAvfxReplacement.IsRegistered)
            _penumbraAvfxReplacement.RemoveTemporaryReplacement();
        state.Stage = CameraTechniqueTestStage.Failed;
        state.PenumbraCleanupStatus = _penumbraAvfxReplacement.Status;
        if (_penumbraAvfxReplacement.IsRegistered && state.PenumbraReportAtInjection == "Not prepared.")
            state.PenumbraReportAtInjection = _penumbraAvfxReplacement.Report;
        state.Status = message;
        _cameraTechniqueAutoTest = state;
        LastAction = message;
        return false;
    }

    private void UpdateCameraTechniqueAutoTest(long nowTicks)
    {
        var state = _cameraTechniqueAutoTest;
        if (state == null || !CameraTechniqueAutoTestRunning || state.CurrentTrial == null)
            return;

        var trial = state.CurrentTrial;
        if (TryGetCameraMotionSample(out var frame, out _))
        {
            if (!trial.HasInitialCamera)
            {
                trial.InitialCameraPosition = frame.Position;
                trial.LastCameraForward = frame.Forward;
                trial.HasInitialCamera = true;
            }
            else
            {
                trial.MaximumCameraTranslation = MathF.Max(trial.MaximumCameraTranslation,
                    System.Numerics.Vector3.Distance(trial.InitialCameraPosition, frame.Position));
                float dot = Math.Clamp(System.Numerics.Vector3.Dot(trial.LastCameraForward, frame.Forward), -1f, 1f);
                trial.AccumulatedCameraRotationDegrees += Math.Acos(dot) * (180d / Math.PI);
                trial.LastCameraForward = frame.Forward;
            }
        }

        if (!_active.TryGetValue(trial.VfxAddress, out var active))
        {
            trial.MissingSamples++;
        }
        else if (!active.IsActor)
        {
            var vfx = (VfxObject*)trial.VfxAddress;
            SceneObject* parent = vfx->ParentObject;
            if (parent == null)
                trial.NullParentSamples++;
            else
                trial.OtherParentSamples++;

            var position = new System.Numerics.Vector3(vfx->Position.X, vfx->Position.Y, vfx->Position.Z);
            if (!trial.HasInitialVfxPosition)
            {
                trial.InitialVfxPosition = position;
                trial.HasInitialVfxPosition = true;
            }
            trial.LastVfxPosition = position;
            trial.MaximumVfxPositionDrift = MathF.Max(trial.MaximumVfxPositionDrift,
                System.Numerics.Vector3.Distance(trial.InitialVfxPosition, position));
            trial.Samples++;
        }

        long elapsedTicks = nowTicks - trial.StartTicks;
        double elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
        double totalSeconds = (double)state.PhaseDurationTicks / Stopwatch.Frequency;
        state.Status = $"{StageName(state.Stage)} · {elapsedSeconds:0.0}/{totalSeconds:0.0}s · {trial.Samples} VFX samples · rotate the camera during this phase.";

        if (elapsedTicks >= state.PhaseDurationTicks)
        {
            trial.Summary = BuildCameraTrialSummary(trial, elapsedSeconds);
            AdvanceCameraTechniqueAutoTest(state);
        }
    }

    private void AdvanceCameraTechniqueAutoTest(CameraTechniqueAutoTest state)
    {
        ClearAll();
        switch (state.Stage)
        {
            case CameraTechniqueTestStage.NativeCameraBinderControl:
                if (!_penumbraAvfxReplacement.PrepareAppendCameraBinder(state.InjectionTargetPath, state.TemplatePath, state.TemplateBinderIndex))
                {
                    FailCameraTechniqueTest(state, "Could not prepare the Penumbra-injected Camera binder: " + _penumbraAvfxReplacement.Status);
                    return;
                }
                if (!_penumbraAvfxReplacement.ValidatePreparedCameraBinder(out string detail))
                {
                    FailCameraTechniqueTest(state, "Generated Camera binder failed validation: " + detail);
                    return;
                }
                state.ValidationSummary += " " + detail;
                state.PenumbraReportAtInjection = _penumbraAvfxReplacement.Report;
                if (!StartCameraTechniqueTrial(state, CameraTechniqueTestStage.InjectedCameraBinder))
                {
                    FailCameraTechniqueTest(state, "Penumbra-injected Camera-binder phase could not start: " + LastAction);
                    return;
                }
                break;
            case CameraTechniqueTestStage.InjectedCameraBinder:
                bool removed = !_penumbraAvfxReplacement.IsRegistered
                    || _penumbraAvfxReplacement.RemoveTemporaryReplacement();
                state.Stage = CameraTechniqueTestStage.Completed;
                state.CurrentTrial = null;
                state.PenumbraCleanupStatus = _penumbraAvfxReplacement.Status;
                state.Status = removed
                    ? "Both Camera-binder phases completed. VFX and temporary Penumbra replacement were cleaned up. Record the visual verdicts below and copy the report."
                    : "Both phases ran, but Penumbra could not remove the temporary replacement. See its report; generated file was retained safely.";
                LastAction = "Native-versus-injected Camera-binder comparison completed.";
                break;
        }
    }

    private static string StageName(CameraTechniqueTestStage stage) => stage switch
    {
        CameraTechniqueTestStage.NativeCameraBinderControl => "Phase 1/2 · Native Camera-binder control",
        CameraTechniqueTestStage.InjectedCameraBinder => "Phase 2/2 · Injected Camera binder",
        _ => stage.ToString(),
    };

    private static string BuildCameraTrialSummary(CameraTechniqueTrial trial, double elapsedSeconds)
    {
        double averageHz = elapsedSeconds > 0d ? trial.Samples / elapsedSeconds : 0d;
        string parentRatio = $"non-null-parent={trial.OtherParentSamples}, null-parent={trial.NullParentSamples}";
        bool enoughMotion = trial.AccumulatedCameraRotationDegrees >= 90d || trial.MaximumCameraTranslation >= 1f;
        string motionStatus = enoughMotion ? "sufficient movement observed" : "LOW CAMERA MOVEMENT — repeat this phase before drawing a conclusion";
        return $"{trial.Label}\nPath: {trial.Path}\nVFX: 0x{(nuint)trial.VfxAddress:X}\nDuration: {elapsedSeconds:0.00}s · samples: {trial.Samples} ({averageHz:0.0} Hz) · missing samples: {trial.MissingSamples}\nParentObject samples: {parentRatio}\nCamera movement: {motionStatus} · max translation {trial.MaximumCameraTranslation:0.000} yalms · accumulated rotation {trial.AccumulatedCameraRotationDegrees:0.0}°\nVFX Position: first {Format(trial.InitialVfxPosition)} · last {Format(trial.LastVfxPosition)} · max local-position drift {trial.MaximumVfxPositionDrift:0.000} yalms";
    }

    private static string FormatVisualVerdict(int verdict) => verdict switch
    {
        0 => "VFX was visible, but did not follow",
        1 => "VFX followed visually",
        2 => "Unclear / effect not visible enough",
        3 => "No VFX appeared; camera-follow behavior could not be evaluated",
        _ => "Not recorded",
    };

    private static GameCamera* GetActiveCamera()
    {
        var control = Control.Instance();
        return control == null ? null : control->CameraManager.GetActiveCamera();
    }

    // Game.Camera inherits CameraBase, whose SceneCamera member is embedded at offset 0x10.
    // This read is used only to measure camera movement in the Camera-binder comparison.
    private static SceneCamera* GetSceneCamera(GameCamera* gameCamera)
    {
        return gameCamera == null ? null : (SceneCamera*)((byte*)gameCamera + EmbeddedSceneCameraOffset);
    }

    private bool TryGetCameraMotionSample(out CameraMotionSample sample, out string error)
    {
        sample = default;
        GameCamera* gameCamera = GetActiveCamera();
        SceneCamera* camera = GetSceneCamera(gameCamera);
        if (gameCamera == null || camera == null)
        {
            error = "CameraManager.GetActiveCamera returned null or had no embedded scene camera.";
            return false;
        }

        // The client view matrix has a zero homogeneous column. Restore M44 in a local copy
        // before inversion; never modify the live game matrix.
        System.Numerics.Matrix4x4 viewMatrix = camera->ViewMatrix;
        var affineViewMatrix = viewMatrix;
        if (MathF.Abs(affineViewMatrix.M44) < 1e-6f
            && MathF.Abs(affineViewMatrix.M14) < 1e-6f
            && MathF.Abs(affineViewMatrix.M24) < 1e-6f
            && MathF.Abs(affineViewMatrix.M34) < 1e-6f)
        {
            affineViewMatrix.M44 = 1f;
        }
        if (!System.Numerics.Matrix4x4.Invert(affineViewMatrix, out var worldMatrix))
        {
            error = "Scene.Camera.ViewMatrix is singular and could not be inverted.";
            return false;
        }

        var position = new System.Numerics.Vector3(worldMatrix.M41, worldMatrix.M42, worldMatrix.M43);
        var right = new System.Numerics.Vector3(worldMatrix.M11, worldMatrix.M12, worldMatrix.M13);
        var up = new System.Numerics.Vector3(worldMatrix.M21, worldMatrix.M22, worldMatrix.M23);
        var backward = new System.Numerics.Vector3(worldMatrix.M31, worldMatrix.M32, worldMatrix.M33);
        if (!TryNormalize(right, out right))
        {
            error = "Inverse ViewMatrix produced an invalid camera-right vector.";
            return false;
        }

        up -= right * System.Numerics.Vector3.Dot(up, right);
        if (!TryNormalize(up, out up) || !TryNormalize(backward, out backward))
        {
            error = "Inverse ViewMatrix produced an invalid camera orientation.";
            return false;
        }

        var expectedBackward = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(right, up));
        if (System.Numerics.Vector3.Dot(expectedBackward, backward) < 0f)
            up = -up;
        var forward = -System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(right, up));
        if (!IsFinite(position) || !IsFinite(forward))
        {
            error = "Inverse ViewMatrix produced non-finite camera data.";
            return false;
        }

        sample = new CameraMotionSample(position, forward);
        error = string.Empty;
        return true;
    }

    private static bool TryNormalize(System.Numerics.Vector3 value, out System.Numerics.Vector3 normalized)
    {
        float lengthSquared = value.LengthSquared();
        if (!(lengthSquared > 1e-10f) || !float.IsFinite(lengthSquared))
        {
            normalized = default;
            return false;
        }
        normalized = value / MathF.Sqrt(lengthSquared);
        return IsFinite(normalized);
    }

    private static bool IsFinite(System.Numerics.Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static string Format(System.Numerics.Vector3 value) =>
        $"({value.X:0.000}, {value.Y:0.000}, {value.Z:0.000})";

    public IReadOnlyList<string> GetActiveDescriptions()
    {
        return _active.Values
            .OrderBy(x => x.Address)
            .Select(x => $"{(x.IsActor ? "Actor-attached" : x.IsPenumbraModifiedCameraBinder ? "Penumbra-modified Camera binder" : x.IsNativeCameraBinder ? "Native AVFX Camera binder" : "World-space")} · 0x{(nuint)x.Address:X} · {x.Path}")
            .ToArray();
    }

    public void ClearAll()
    {
        var instances = _active.Values.ToArray();
        _active.Clear();

        foreach (var instance in instances)
        {
            try
            {
                if (instance.IsActor)
                {
                    var hook = _actorVfxRemoveHook;
                    if (hook != null)
                        hook.Original((VfxData*)instance.Address, (char)1);
                }
                else
                {
                    var hook = _staticVfxRemoveHook;
                    if (hook != null)
                        hook.Original((VfxObject*)instance.Address);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, $"FPSBooster: failed to remove tracked VFX 0x{(nuint)instance.Address:X} ({instance.Path}).");
            }
        }

        LastAction = instances.Length == 0
            ? "No tracked VFX instances to clear."
            : $"Requested removal of {instances.Length} tracked native VFX instance(s).";
    }

    private bool EnsureReadyForStatic()
    {
        if (!_initializationAttempted)
            Initialize();
        return StaticReady || Fail("The static VFX route is unavailable. See Static VFX interop status.");
    }

    private bool EnsureReadyForActor()
    {
        if (!_initializationAttempted)
            Initialize();
        return ActorReady || Fail("The actor-attached VFX route is unavailable. See Actor VFX interop status.");
    }

    private bool EnsureCapacity()
    {
        return _active.Count < MaximumTrackedInstances
            || Fail($"The research probe limits active native VFX to {MaximumTrackedInstances}. Clear the current instances before spawning more.");
    }

    private bool Fail(string message)
    {
        LastAction = message;
        return false;
    }

    private VfxObject* CreateStatic(
        string path,
        System.Numerics.Vector3 position,
        System.Numerics.Vector3 scale,
        float rotation)
    {
        // The game path is checked before reaching this function. The pool name mirrors VFXEditor.
        VfxObject* vfx = VfxObject.Create(path, StaticVfxPoolName);
        if (vfx == null)
            return null;

        // StaticVfxRun registers the object with the native scheduler; Create alone is insufficient.
        _staticVfxRun!(vfx, 0f, 0xFFFFFFFF);
        vfx->Position = position;
        vfx->Rotation = GameQuaternion.CreateFromYawPitchRoll(rotation, 0f, 0f);
        vfx->Scale = scale;
        vfx->UpdateTransforms(true);
        return vfx;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (_disposed)
            return;

        // Only the native-vs-injected Camera-binder comparison needs framework sampling.
        // The callback never moves or updates a VFX instance.
        UpdateCameraTechniqueAutoTest(Stopwatch.GetTimestamp());
    }

    private IntPtr StaticVfxRemoveDetour(VfxObject* vfx)
    {
        if (vfx != null)
            _active.TryRemove((nint)vfx, out _);
        var hook = _staticVfxRemoveHook;
        return hook?.Original(vfx) ?? IntPtr.Zero;
    }

    private IntPtr ActorVfxRemoveDetour(VfxData* vfx, char a2)
    {
        if (vfx != null)
            _active.TryRemove((nint)vfx, out _);
        var hook = _actorVfxRemoveHook;
        return hook?.Original(vfx, a2) ?? IntPtr.Zero;
    }

    private nint ResolveUniqueTextSignature(string signature, string label)
    {
        nint[] matches = _sigScanner.ScanAllText(signature);
        if (matches.Length != 1)
            throw new InvalidOperationException($"Could not uniquely resolve {label}: signature matched {matches.Length} locations.");
        return matches[0];
    }

    private nint ResolveRelativeCallTarget(nint callSite, string label)
    {
        if (callSite == 0 || *(byte*)callSite != 0xE8)
            throw new InvalidOperationException($"The {label} signature did not resolve to an E8 relative CALL instruction.");

        int displacement = *(int*)((byte*)callSite + 1);
        nint target = _sigScanner.ResolveRelativeAddress(callSite + 5, displacement);
        nint textBase = _sigScanner.Module.BaseAddress + (nint)_sigScanner.TextSectionOffset;
        nint textEnd = textBase + _sigScanner.TextSectionSize;
        if (target < textBase || target >= textEnd)
            throw new InvalidOperationException($"The resolved {label} target is outside the game .text section.");
        return target;
    }

    private static nint ResolveActorVfxRemoveAddress(nint signatureAddress)
    {
        // VFXEditor resolves this signature's RIP-relative LEA to a global function pointer.
        nint displacementAddress = signatureAddress + 7;
        int displacement = Marshal.ReadInt32(displacementAddress);
        nint pointerAddress = displacementAddress + 4 + displacement;
        nint functionAddress = Marshal.ReadIntPtr(pointerAddress);
        if (functionAddress == 0)
            throw new InvalidOperationException("The ActorVfxRemove function pointer resolved to null.");
        return functionAddress;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (CameraTechniqueAutoTestRunning)
            CancelCameraTechniqueAutoTest();
        _disposed = true;

        if (_subscribedToUpdate)
        {
            _framework.Update -= OnFrameworkUpdate;
            _subscribedToUpdate = false;
        }

        ClearAll();
        try { _staticVfxRemoveHook?.Disable(); } catch { }
        try { _actorVfxRemoveHook?.Disable(); } catch { }
        _staticVfxRemoveHook?.Dispose();
        _actorVfxRemoveHook?.Dispose();
        _staticVfxRemoveHook = null;
        _actorVfxRemoveHook = null;
        _staticVfxRun = null;
        _actorVfxCreate = null;
    }
}
