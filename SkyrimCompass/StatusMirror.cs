using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System.Text.Json;

// Same external-contract wire shapes CompassHud.cs declares at its own top (see the comment there
// for the full rationale). Redeclared here rather than shared because `using X = (...)` tuple
// aliases are file-scoped in C#, not `global using` — this file needs its own copy, field-for-field
// identical, the same way every plugin in this ecosystem redeclares them rather than taking a
// project reference on Moodles/Loci themselves
using MoodlesStatusInfo = (
    int Version, System.Guid GUID, int IconID, string Title, string Description, string CustomVFXPath,
    long ExpireTicks, int Type, int Stacks, int StackSteps, uint Modifiers, System.Guid ChainedStatus,
    int ChainTrigger, string Applier, string Dispeller, bool Permanent);
using LociStatusInfo = (
    int Version, System.Guid GUID, uint IconID, string Title, string Description, string CustomVFXPath,
    long ExpireTicks, byte Type, int Stacks, int StackSteps, int StackToChain, uint Modifiers,
    System.Guid ChainedGUID, byte ChainType, int ChainTrigger, string Applier, string Dispeller);

namespace SkyrimCompass;

// Mirrors Loci.Api's LociApiEc member-for-member (see CompassHud.cs's own copy of this same enum
// for the fuller rationale on duplicating it per-file instead of sharing it: it's a fixed external
// contract, not logic, and Dalamud's wire type for it is a plain int either way)
internal enum LociApiEc
{
    Success = 0, NoChange = 1, PartialSuccess = 2, TargetNotFound = 3, TargetInvalid = 4,
    DataNotFound = 5, DataInvalid = 6, ItemLocked = 7, InvalidKey = 8, ItemIsPersistent = 9,
    ClientForbidden = 10, FSPathFaulted = 11, UnkError = int.MaxValue,
}

/// <summary>
/// Field-level snapshot of a status, used purely for cheap "did anything meaningful change since
/// we last pushed this" comparisons. Deliberately excludes GUID (that's the tracking dictionary's
/// key already), Version (unvalidated by either plugin), and every chain-related field — chain
/// data references GUIDs from the *source* plugin's own saved-status library, so copying it across
/// verbatim would point at nothing in the destination plugin (or, astronomically unlikely, at an
/// unrelated status that happens to share a GUID) — it's stripped on every mirror instead.
/// </summary>
internal readonly record struct MirrorSignature(
    long IconID, string Title, string Description, string CustomVFXPath, long ExpireTicks,
    int Type, int Stacks, int StackSteps, uint Modifiers, string Applier, string Dispeller)
{
    public static MirrorSignature FromMoodles(MoodlesStatusInfo m) => new(
        m.IconID, m.Title, m.Description, m.CustomVFXPath, m.ExpireTicks,
        m.Type, m.Stacks, m.StackSteps, m.Modifiers, m.Applier, m.Dispeller);

    public static MirrorSignature FromLoci(LociStatusInfo l) => new(
        l.IconID, l.Title, l.Description, l.CustomVFXPath, l.ExpireTicks,
        l.Type, l.Stacks, l.StackSteps, l.Modifiers, l.Applier, l.Dispeller);
}

/// <summary>
/// Converts statuses between Moodles' and Loci's tuple shapes for mirroring. Both agree closely on
/// units and enum values (StatusType and the first 7 Modifiers flags are numerically identical,
/// ExpireTicks is milliseconds-with-(-1)-meaning-permanent in both), so most fields are a straight
/// copy. Two deliberate gaps: chain-trigger data is always stripped (see MirrorSignature above),
/// and Moodles' "Permanent" (Sticky) flag has no Loci equivalent, so mirroring Loci -> Moodles
/// always produces Permanent = false.
/// </summary>
internal static class MirrorConverter
{
    public static LociStatusInfo ToLoci(MoodlesStatusInfo m) => (
        Version: 1,
        GUID: m.GUID,
        IconID: (uint)Math.Max(0, m.IconID),
        Title: m.Title,
        Description: m.Description,
        CustomVFXPath: m.CustomVFXPath,
        ExpireTicks: m.ExpireTicks,
        Type: (byte)Math.Clamp(m.Type, 0, 2),
        Stacks: m.Stacks,
        StackSteps: m.StackSteps,
        StackToChain: 0,
        Modifiers: m.Modifiers,
        ChainedGUID: System.Guid.Empty,
        ChainType: (byte)0,
        ChainTrigger: 0,
        Applier: m.Applier,
        Dispeller: m.Dispeller);

    public static MoodlesStatusInfo ToMoodles(LociStatusInfo l) => (
        Version: 1,
        GUID: l.GUID,
        IconID: (int)l.IconID,
        Title: l.Title,
        Description: l.Description,
        CustomVFXPath: l.CustomVFXPath,
        ExpireTicks: l.ExpireTicks,
        Type: Math.Clamp((int)l.Type, 0, 2),
        Stacks: l.Stacks,
        StackSteps: l.StackSteps,
        Modifiers: l.Modifiers,
        ChainedStatus: System.Guid.Empty,
        ChainTrigger: 0,
        Applier: l.Applier,
        Dispeller: l.Dispeller,
        Permanent: false);
}

/// <summary>
/// Persisted record of which GUIDs the mirror engine has created, in which direction, and with
/// what signature — a small JSON file in the plugin's own config directory, loaded on startup and
/// saved whenever it changes. Exists specifically to avoid a "ghost status" failure mode: if this
/// tracking were in-memory only, every game restart would start identity-tracking from a blank
/// slate, with nothing to go on except whether both sides currently happen to agree — which breaks
/// the moment only one side survives a restart correctly. The GUID-matching "adopt" fallback further
/// down in StatusMirrorEngine still exists for a missing/corrupted state file, but this is the
/// primary mechanism, not the guess.
/// </summary>
internal sealed class MirrorState
{
    public Dictionary<System.Guid, MirrorSignature> MirroredIntoLoci    { get; set; } = new();
    public Dictionary<System.Guid, MirrorSignature> MirroredIntoMoodles { get; set; } = new();

    private static string FilePath(IDalamudPluginInterface pluginInterface) =>
        Path.Combine(pluginInterface.ConfigDirectory.FullName, "status_mirror_state.json");

    /// <summary>Never throws. A missing, corrupt, or unreadable file just returns a fresh, empty state.</summary>
    public static MirrorState Load(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        try
        {
            var path = FilePath(pluginInterface);
            if (!File.Exists(path))
                return new MirrorState();

            var loaded = JsonSerializer.Deserialize<MirrorState>(File.ReadAllText(path));
            return loaded ?? new MirrorState();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to load persisted status-mirror state — starting fresh.");
            return new MirrorState();
        }
    }

    /// <summary>Never throws — a failed save just means falling back to the GUID-matching guess next restart.</summary>
    public void Save(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        try
        {
            var path = FilePath(pluginInterface);
            Directory.CreateDirectory(pluginInterface.ConfigDirectory.FullName);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to save status-mirror state.");
        }
    }
}

/// <summary>
/// EXPERIMENTAL. Reaches into Moodles' own loaded assembly via reflection and flips an internal
/// flag controlling how Moodles decides where to draw its own status icons relative to the game's
/// native ones.
///
/// Background, confirmed against Moodles' own source (GameGuiProcessors/TargetInfoProcessor.cs and
/// four sibling processor files): Moodles has two ways to compute that offset. The default
/// (CommonProcessor.NewMethod = true) visually scans the shared native icon UI region and counts
/// populated slots, assuming anything visible there is a vanilla game status — which miscounts any
/// icon another plugin has already drawn into that same region, specifically Loci, if Loci's render
/// hook happens to run first on a given client session. The other path (NewMethod = false) asks
/// Dalamud's own IPlayerCharacter.StatusList for the real, authoritative vanilla status count
/// instead, which sidesteps the problem entirely. Both paths already exist and are already wired up
/// inside Moodles' own code — this does not add new behavior to Moodles, it just flips which of
/// Moodles' own existing paths gets used. This is very likely the exact mechanism behind "removing
/// one status makes an unrelated one fall off": a miscounted offset means Moodles' own icon
/// positions were already wrong before anything got removed, and removing something shifts the
/// count, which just moves which icon the wrong position happens to land on next.
///
/// Stays labeled Experimental because every step below touches Moodles' internal, unversioned
/// implementation details — a public static self-reference and two public instance fields, with no
/// compatibility contract. It fails soft (see below) the moment Moodles renames or restructures any
/// of them, and it's broader than the name suggests: flipping this changes Moodles' icon placement
/// everywhere, for every character, not just the Loci-overlap case.
///
/// Failure mode by design: every reflection step is individually null-checked, with a specific
/// <see cref="Status"/> message per failure point, wrapped in try/catch besides. If Moodles changes
/// shape, this leaves Moodles at its own default behavior and reports why — it never throws into
/// caller code or leaves anything partially modified.
/// </summary>
internal sealed class MoodlesOffsetPatch
{
    private const string AssemblyName            = "Moodles";
    private const string PluginTypeName          = "Moodles.Moodles";
    private const string StaticInstanceFieldName = "P";
    private const string ProcessorFieldName      = "CommonProcessor";
    private const string FlagFieldName           = "NewMethod";

    private readonly IPluginLog log;

    public MoodlesOffsetPatch(IPluginLog log) => this.log = log;

    public string Status { get; private set; } = "Not yet attempted";
    public bool IsApplied { get; private set; }

    public bool TryApply()
    {
        try
        {
            var field = FindFlagField(out var processorInstance);
            if (field == null || processorInstance == null)
            {
                IsApplied = false;
                return false;
            }

            field.SetValue(processorInstance, false);

            var confirmedValue = (bool)field.GetValue(processorInstance)!;
            if (!confirmedValue)
            {
                IsApplied = true;
                Status = "Applied (confirmed: NewMethod = false)";
                log.Information("[SkyrimCompass] Experimental Moodles offset patch applied and confirmed.");
                return true;
            }

            IsApplied = false;
            Status = "Set the value but read-back still shows true — something is intercepting the write";
            log.Warning("[SkyrimCompass] Experimental Moodles offset patch: read-back mismatch after apply.");
            return false;
        }
        catch (Exception ex)
        {
            IsApplied = false;
            Status = $"Failed: {ex.GetType().Name} - {ex.Message}";
            log.Warning(ex, "[SkyrimCompass] Experimental Moodles offset patch threw while applying.");
            return false;
        }
    }

    /// <summary>Restores Moodles' own default. Safe to call even if never applied.</summary>
    public bool TryRevert()
    {
        try
        {
            var field = FindFlagField(out var processorInstance);
            if (field == null || processorInstance == null)
                return false;

            field.SetValue(processorInstance, true);
            IsApplied = false;
            Status = "Reverted to Moodles' default";
            log.Information("[SkyrimCompass] Experimental Moodles offset patch reverted.");
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Experimental Moodles offset patch threw while reverting.");
            return false;
        }
    }

    private FieldInfo? FindFlagField(out object? processorInstance)
    {
        processorInstance = null;

        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == AssemblyName);
        if (assembly == null)
        {
            Status = "Moodles assembly not found (not loaded, or not installed)";
            return null;
        }

        var pluginType = assembly.GetType(PluginTypeName);
        if (pluginType == null)
        {
            Status = $"Moodles' plugin type has changed shape (expected '{PluginTypeName}') — patch is stale";
            return null;
        }

        var instanceField = pluginType.GetField(StaticInstanceFieldName, BindingFlags.Public | BindingFlags.Static);
        var pluginInstance = instanceField?.GetValue(null);
        if (pluginInstance == null)
        {
            Status = "Moodles hasn't finished initializing yet";
            return null;
        }

        var processorField = pluginType.GetField(ProcessorFieldName, BindingFlags.Public | BindingFlags.Instance);
        processorInstance = processorField?.GetValue(pluginInstance);
        if (processorInstance == null)
        {
            Status = $"Moodles' plugin class no longer has a '{ProcessorFieldName}' field — patch is stale";
            return null;
        }

        var flagField = processorInstance.GetType().GetField(FlagFieldName, BindingFlags.Public | BindingFlags.Instance);
        if (flagField == null || flagField.FieldType != typeof(bool))
        {
            Status = $"Moodles' processor no longer has a bool '{FlagFieldName}' field — patch is stale";
            return null;
        }

        return flagField;
    }
}

/// <summary>Thin wrapper around the handful of Moodles IPC calls the mirror engine needs, local-player-only.</summary>
internal sealed class MoodlesMirrorIpc : IDisposable
{
    private const int MinimumApiVersion = 4;

    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<int> version;
    private readonly ICallGateSubscriber<nint, object> statusManagerModified;
    private readonly ICallGateSubscriber<List<MoodlesStatusInfo>> getClientStatusManagerInfo;
    private readonly ICallGateSubscriber<MoodlesStatusInfo, IPlayerCharacter, object> addOrUpdateByPlayer;
    private readonly ICallGateSubscriber<System.Guid, IPlayerCharacter, object> removeByPlayer;

    private bool subscribedToChanges;

    public bool Available { get; private set; }
    public event Action? LocalStatusesChanged;

    public MoodlesMirrorIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        version = pluginInterface.GetIpcSubscriber<int>("Moodles.Version");
        statusManagerModified = pluginInterface.GetIpcSubscriber<nint, object>("Moodles.StatusManagerModified");
        getClientStatusManagerInfo = pluginInterface.GetIpcSubscriber<List<MoodlesStatusInfo>>("Moodles.GetClientStatusManagerInfoV2");
        addOrUpdateByPlayer = pluginInterface.GetIpcSubscriber<MoodlesStatusInfo, IPlayerCharacter, object>("Moodles.AddOrUpdateMoodleByDataByPlayerV2");
        removeByPlayer = pluginInterface.GetIpcSubscriber<System.Guid, IPlayerCharacter, object>("Moodles.RemoveMoodleByPlayerV2");

        TrySubscribeToChanges();
        RefreshAvailability();
    }

    public void RefreshAvailability()
    {
        var wasAvailable = Available;
        try { Available = version.InvokeFunc() >= MinimumApiVersion; }
        catch { Available = false; }

        if (Available && !wasAvailable)
        {
            log.Information("[SkyrimCompass] Moodles IPC became available for status mirroring.");
            TrySubscribeToChanges();
        }
        else if (!Available && wasAvailable)
        {
            log.Information("[SkyrimCompass] Moodles IPC became unavailable for status mirroring.");
        }
    }

    private void TrySubscribeToChanges()
    {
        if (subscribedToChanges) return;
        try
        {
            statusManagerModified.Subscribe(OnStatusManagerModified);
            subscribedToChanges = true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[SkyrimCompass] Could not subscribe to Moodles.StatusManagerModified yet.");
        }
    }

    private void OnStatusManagerModified(nint address) => LocalStatusesChanged?.Invoke();

    /// <summary>Fetches the local player's currently-applied Moodles. Empty list on any failure.</summary>
    public List<MoodlesStatusInfo> GetLocalStatuses()
    {
        if (!Available) return [];
        try { return getClientStatusManagerInfo.InvokeFunc() ?? []; }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to read local Moodles statuses for mirroring.");
            return [];
        }
    }

    /// <summary>
    /// Requires "Allow other plugins apply Moodles" (broadcast) enabled in Moodles' own settings —
    /// off by default, and there's no way for another plugin to detect or override it. If it's off,
    /// Loci -> Moodles mirroring silently does nothing; that's Moodles' own gate, not a bug here.
    /// </summary>
    public bool TryApply(MoodlesStatusInfo status, IPlayerCharacter localPlayer)
    {
        if (!Available) return false;
        try
        {
            addOrUpdateByPlayer.InvokeAction(status, localPlayer);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to apply a mirrored Moodle.");
            return false;
        }
    }

    public bool TryRemove(System.Guid guid, IPlayerCharacter localPlayer)
    {
        if (!Available) return false;
        try
        {
            removeByPlayer.InvokeAction(guid, localPlayer);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to remove a mirrored Moodle.");
            return false;
        }
    }

    public void Dispose()
    {
        if (subscribedToChanges)
        {
            try { statusManagerModified.Unsubscribe(OnStatusManagerModified); }
            catch { /* best effort */ }
        }
    }
}

/// <summary>Thin wrapper around the handful of Loci IPC calls the mirror engine needs, local-player-only.</summary>
internal sealed class LociMirrorIpc : IDisposable
{
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<(int Major, int Minor)> apiVersion;
    private readonly ICallGateSubscriber<bool> isEnabled;
    private readonly ICallGateSubscriber<nint, int, object?> managerChanged;
    private readonly ICallGateSubscriber<List<LociStatusInfo>> getManagerInfo;
    private readonly ICallGateSubscriber<LociStatusInfo, uint, int> applyStatusInfo;
    private readonly ICallGateSubscriber<System.Guid, uint, int> removeStatus;

    private bool subscribedToChanges;

    public bool Available { get; private set; }
    public event Action? LocalStatusesChanged;

    public LociMirrorIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        apiVersion = pluginInterface.GetIpcSubscriber<(int, int)>("Loci.ApiVersion");
        isEnabled = pluginInterface.GetIpcSubscriber<bool>("Loci.IsEnabled");
        managerChanged = pluginInterface.GetIpcSubscriber<nint, int, object?>("Loci.ManagerChanged");
        getManagerInfo = pluginInterface.GetIpcSubscriber<List<LociStatusInfo>>("Loci.GetManagerInfo");
        applyStatusInfo = pluginInterface.GetIpcSubscriber<LociStatusInfo, uint, int>("Loci.ApplyStatusInfo");
        removeStatus = pluginInterface.GetIpcSubscriber<System.Guid, uint, int>("Loci.RemoveStatus");

        TrySubscribeToChanges();
        RefreshAvailability();
    }

    public void RefreshAvailability()
    {
        var wasAvailable = Available;
        try
        {
            _ = apiVersion.InvokeFunc(); // just needs to not throw
            Available = isEnabled.InvokeFunc();
        }
        catch { Available = false; }

        if (Available && !wasAvailable)
        {
            log.Information("[SkyrimCompass] Loci IPC became available for status mirroring.");
            TrySubscribeToChanges();
        }
        else if (!Available && wasAvailable)
        {
            log.Information("[SkyrimCompass] Loci IPC became unavailable for status mirroring.");
        }
    }

    private void TrySubscribeToChanges()
    {
        if (subscribedToChanges) return;
        try
        {
            managerChanged.Subscribe(OnManagerChanged);
            subscribedToChanges = true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[SkyrimCompass] Could not subscribe to Loci.ManagerChanged yet.");
        }
    }

    private void OnManagerChanged(nint address, int changeTypeRaw) => LocalStatusesChanged?.Invoke();

    /// <summary>Fetches the local player's currently-applied Loci statuses. Empty list on any failure.</summary>
    public List<LociStatusInfo> GetLocalStatuses()
    {
        if (!Available) return [];
        try { return getManagerInfo.InvokeFunc() ?? []; }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to read local Loci statuses for mirroring.");
            return [];
        }
    }

    /// <summary>Returns the real LociApiEc rather than collapsing to bool — callers react differently
    /// to e.g. ItemLocked (will never succeed with key=0, stop retrying) than a transient failure.</summary>
    public LociApiEc TryApply(LociStatusInfo status)
    {
        if (!Available) return LociApiEc.TargetInvalid;
        try
        {
            var ec = (LociApiEc)applyStatusInfo.InvokeFunc(status, 0u);
            if (ec is not (LociApiEc.Success or LociApiEc.NoChange))
                log.Warning($"[SkyrimCompass] Loci rejected a mirrored status: {ec}");
            return ec;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to apply a mirrored Loci status.");
            return LociApiEc.UnkError;
        }
    }

    public LociApiEc TryRemove(System.Guid guid)
    {
        if (!Available) return LociApiEc.TargetInvalid;
        try
        {
            var ec = (LociApiEc)removeStatus.InvokeFunc(guid, 0u);
            if (ec is not (LociApiEc.Success or LociApiEc.NoChange or LociApiEc.DataNotFound))
                log.Warning($"[SkyrimCompass] Loci rejected removing a mirrored status: {ec}");
            return ec;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to remove a mirrored Loci status.");
            return LociApiEc.UnkError;
        }
    }

    public void Dispose()
    {
        if (subscribedToChanges)
        {
            try { managerChanged.Unsubscribe(OnManagerChanged); }
            catch { /* best effort */ }
        }
    }
}

/// <summary>
/// Keeps the local player's Moodles and Loci statuses mirrored onto each other in the background,
/// so FFXIV's own native buff bar — which already shows Moodles-native icons, and is what the
/// player sees now that CompassHud.cs's hand-drawn player row was removed — shows your Loci
/// statuses too, without SkyrimCompass drawing anything itself. Ported from the user's own
/// StatusBridge project (github.com/JTayGang/MeowyUtils/tree/main/StatusBridge), trimmed for a
/// single always-local consumer: no cross-sync-plugin awareness needed (this only ever mirrors
/// your own live state, regardless of what any sync plugin does with it afterward), and no
/// live-state-reader-based staleness filtering (BridgeEngine's own comments confirm that when that
/// read fails or is absent, every consumer already falls back to treating every native-looking GUID
/// as a candidate — i.e. omitting it entirely reduces to that same fallback path, not a functional
/// gap; it's a responsiveness refinement, not something this depends on for correctness).
///
/// Identity trick, unchanged from StatusBridge: a mirrored status keeps the exact same GUID in
/// both systems, which gives three things for free — telling "a status this engine created" apart
/// from "one you made natively", preventing feedback loops (mirroring a Moodle into Loci can't loop
/// back around and get mirrored from Loci back into Moodles as if it were new), and natural
/// de-duplication (re-mirroring an already-mirrored pair updates in place, not a second copy).
/// </summary>
public sealed class StatusMirrorEngine : IDisposable
{
    private readonly Configuration pluginConfig;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly IFramework framework;

    private readonly MoodlesMirrorIpc moodles;
    private readonly LociMirrorIpc loci;
    private readonly MoodlesOffsetPatch offsetPatch;

    private readonly MirrorState state;
    private bool stateDirty;

    private Dictionary<System.Guid, MirrorSignature> MirroredIntoLoci    => state.MirroredIntoLoci;
    private Dictionary<System.Guid, MirrorSignature> MirroredIntoMoodles => state.MirroredIntoMoodles;

    // GUIDs already logged as stuck-locked on Loci's side, so the tick loop doesn't spam the log
    // for something that'll keep failing the same way until manually unlocked. Cleared once a GUID
    // stops being locked, so a genuinely new lock situation still gets reported
    private readonly HashSet<System.Guid> knownLockedInLoci = new();

    private DateTime nextPeriodicReconcile = DateTime.MinValue;
    private volatile bool dirty = true;

    public bool   MoodlesAvailable       => moodles.Available;
    public bool   LociAvailable          => loci.Available;
    public int    MirroredIntoLociCount    => MirroredIntoLoci.Count;
    public int    MirroredIntoMoodlesCount => MirroredIntoMoodles.Count;
    public int    LockedMirrorCount        => knownLockedInLoci.Count;
    public bool   OffsetPatchApplied       => offsetPatch.IsApplied;
    public string OffsetPatchStatus        => offsetPatch.Status;

    public StatusMirrorEngine(
        IDalamudPluginInterface pluginInterface, IFramework framework, IObjectTable objectTable,
        IPluginLog log, Configuration config)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.objectTable = objectTable;
        this.log = log;
        pluginConfig = config;

        moodles = new MoodlesMirrorIpc(pluginInterface, log);
        loci = new LociMirrorIpc(pluginInterface, log);
        offsetPatch = new MoodlesOffsetPatch(log);
        state = MirrorState.Load(pluginInterface, log);

        moodles.LocalStatusesChanged += () => dirty = true;
        loci.LocalStatusesChanged += () => dirty = true;
        framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        var now = DateTime.UtcNow;
        if (!dirty && now < nextPeriodicReconcile)
            return;

        dirty = false;
        nextPeriodicReconcile = now + TimeSpan.FromSeconds(1);

        try { Reconcile(); }
        catch (Exception ex) { log.Error(ex, "[SkyrimCompass] Status-mirror reconciliation pass threw unexpectedly."); }

        if (stateDirty)
        {
            state.Save(pluginInterface, log);
            stateDirty = false;
        }
    }

    private void MarkStateDirty() => stateDirty = true;

    private void Reconcile()
    {
        // Runs before the enabled check on purpose: availability should stay fresh for the config
        // UI even while mirroring itself is paused, and the offset patch is an independent concern
        // — someone might want the icon fix active without mirroring running
        moodles.RefreshAvailability();
        loci.RefreshAvailability();

        UpdateOffsetPatch();

        if (!pluginConfig.MirrorMoodlesLoci) return;
        if (objectTable.LocalPlayer is not { } localPlayer) return;
        if (!moodles.Available || !loci.Available) return;

        var moodleList = moodles.GetLocalStatuses();
        var lociList = loci.GetLocalStatuses();

        // Order matters: running Moodles->Loci first means anything it just mirrored is immediately
        // recorded in MirroredIntoLoci, so the Loci->Moodles pass below correctly sees it as "not
        // native to Loci" and doesn't try to mirror it straight back — this identity-sharing is
        // what prevents feedback loops (see class remarks)
        if (pluginConfig.MirrorMoodlesToLoci)
            SyncMoodlesToLoci(moodleList, lociList);

        if (pluginConfig.MirrorLociToMoodles)
            SyncLociToMoodles(lociList, moodleList, localPlayer);
    }

    private void UpdateOffsetPatch()
    {
        if (pluginConfig.MirrorOffsetPatchEnabled)
        {
            // Idempotent-safe and cheap (a handful of reflection field lookups on a bool), so
            // retrying every tick until it succeeds is simpler and more self-healing than
            // event-driven re-application — it recovers on its own after a Moodles reload
            if (moodles.Available && !offsetPatch.IsApplied)
                offsetPatch.TryApply();
        }
        else if (offsetPatch.IsApplied)
        {
            offsetPatch.TryRevert();
        }
    }

    private void SyncMoodlesToLoci(List<MoodlesStatusInfo> moodleList, List<LociStatusInfo> lociExisting)
    {
        var allMoodleGuids = moodleList.Select(m => m.GUID).ToHashSet();

        // "Native to Moodles" excludes anything that's itself a mirror this engine created in
        // Moodles, sourced from Loci (tracked in MirroredIntoMoodles, not MirroredIntoLoci — the
        // opposite dict from this direction's own bookkeeping). Checking the wrong dict here was
        // the root cause of a real bug: a freshly-mirrored, still-genuinely-native Moodle would
        // fall out of this filter the moment it got tracked, making the cleanup pass below
        // immediately treat its own successful mirror as orphaned and remove it again
        var nativeMoodles = moodleList.Where(m => !MirroredIntoMoodles.ContainsKey(m.GUID)).ToList();

        foreach (var m in nativeMoodles)
        {
            var sig = MirrorSignature.FromMoodles(m);
            var alreadyTracked = MirroredIntoLoci.TryGetValue(m.GUID, out var knownSig);

            if (alreadyTracked && knownSig == sig)
                continue; // nothing changed since we last pushed it

            if (!alreadyTracked && lociExisting.Any(l => l.GUID == m.GUID))
            {
                // A Loci entry with this exact GUID already exists — only expected on a
                // missing/corrupted state file; adopt it as tracked rather than re-pushing a duplicate
                MirroredIntoLoci[m.GUID] = sig;
                MarkStateDirty();
                continue;
            }

            var converted = MirrorConverter.ToLoci(m);
            var ec = loci.TryApply(converted);
            if (ec is LociApiEc.Success or LociApiEc.NoChange)
            {
                MirroredIntoLoci[m.GUID] = sig;
                MarkStateDirty();
            }
        }

        // Clean up mirrors whose native Moodles source is truly gone — "truly gone" means absent
        // from the full current Moodles list (allMoodleGuids), not merely absent from the
        // native-only subset above (a tracked GUID is *expected* to fall out of that filter; that's
        // not the same as its source having disappeared)
        foreach (var guid in MirroredIntoLoci.Keys.Where(g => !allMoodleGuids.Contains(g)).ToList())
        {
            if (lociExisting.Any(l => l.GUID == guid))
            {
                // Still there as of this tick's fresh poll — ask Loci to remove it, but don't stop
                // tracking yet; a same-tick "it didn't throw" isn't proof it actually took effect.
                // Only untrack once a later tick's fresh poll confirms it's really gone — otherwise
                // a removal that's blocked or delayed turns into a permanent, untracked ghost
                var ec = loci.TryRemove(guid);

                if (ec == LociApiEc.ItemLocked)
                {
                    if (knownLockedInLoci.Add(guid))
                        log.Warning($"[SkyrimCompass] A mirrored Loci status ({guid}) is locked and can't be removed automatically — unlock it in Loci's own UI if you want it gone.");
                }
                else
                {
                    knownLockedInLoci.Remove(guid);
                }

                continue;
            }

            MirroredIntoLoci.Remove(guid);
            MarkStateDirty();
            knownLockedInLoci.Remove(guid);
        }
    }

    private void SyncLociToMoodles(List<LociStatusInfo> lociList, List<MoodlesStatusInfo> moodlesExisting, IPlayerCharacter localPlayer)
    {
        var allLociGuids = lociList.Select(l => l.GUID).ToHashSet();

        // Mirror image of SyncMoodlesToLoci's own fix above: "native to Loci" excludes anything
        // that's itself a mirror this engine created in Loci, sourced from Moodles (tracked in
        // MirroredIntoLoci, the opposite dict from this direction's own bookkeeping)
        var nativeLoci = lociList.Where(l => !MirroredIntoLoci.ContainsKey(l.GUID)).ToList();

        foreach (var l in nativeLoci)
        {
            var sig = MirrorSignature.FromLoci(l);
            var alreadyTracked = MirroredIntoMoodles.TryGetValue(l.GUID, out var knownSig);

            if (alreadyTracked && knownSig == sig)
                continue;

            if (!alreadyTracked && moodlesExisting.Any(m => m.GUID == l.GUID))
            {
                MirroredIntoMoodles[l.GUID] = sig;
                MarkStateDirty();
                continue;
            }

            var converted = MirrorConverter.ToMoodles(l);
            if (moodles.TryApply(converted, localPlayer))
            {
                MirroredIntoMoodles[l.GUID] = sig;
                MarkStateDirty();
            }
        }

        // "Truly gone" means absent from the full current Loci list (allLociGuids), same reasoning
        // as SyncMoodlesToLoci's cleanup comment above
        foreach (var guid in MirroredIntoMoodles.Keys.Where(g => !allLociGuids.Contains(g)).ToList())
        {
            if (moodlesExisting.Any(m => m.GUID == guid))
            {
                // Same reasoning as SyncMoodlesToLoci's cleanup loop — don't untrack on a same-tick
                // assumption, only once a fresh poll confirms it's gone. This side is the one
                // actually affected by Moodles' fire-and-forget (void) remove call and its Ephemeral
                // gate, so it's the more important half of this particular check
                moodles.TryRemove(guid, localPlayer);
                continue;
            }

            MirroredIntoMoodles.Remove(guid);
            MarkStateDirty();
        }
    }

    /// <summary>
    /// Removes every status this engine has ever mirrored, on both sides — a manual escape hatch
    /// for a stuck pair. Only ever touches the mirror half of a pair, never the native original; if
    /// the native source is still genuinely alive, the next reconcile pass correctly recreates the
    /// mirror — that's not a bug, it's this doing exactly what it's supposed to.
    /// </summary>
    public void ClearAllMirrors()
    {
        foreach (var guid in MirroredIntoLoci.Keys.ToList())
            loci.TryRemove(guid);

        var lociStillPresent = loci.GetLocalStatuses().Select(l => l.GUID).ToHashSet();
        foreach (var guid in MirroredIntoLoci.Keys.Where(g => !lociStillPresent.Contains(g)).ToList())
        {
            MirroredIntoLoci.Remove(guid);
            MarkStateDirty();
            knownLockedInLoci.Remove(guid);
        }

        if (objectTable.LocalPlayer is { } localPlayer)
        {
            foreach (var guid in MirroredIntoMoodles.Keys.ToList())
                moodles.TryRemove(guid, localPlayer);
        }

        var moodlesStillPresent = moodles.GetLocalStatuses().Select(m => m.GUID).ToHashSet();
        foreach (var guid in MirroredIntoMoodles.Keys.Where(g => !moodlesStillPresent.Contains(g)).ToList())
        {
            MirroredIntoMoodles.Remove(guid);
            MarkStateDirty();
        }

        log.Information("[SkyrimCompass] Cleared all mirrored statuses.");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;

        if (offsetPatch.IsApplied)
            offsetPatch.TryRevert();

        if (stateDirty)
            state.Save(pluginInterface, log);

        moodles.Dispose();
        loci.Dispose();
    }
}
