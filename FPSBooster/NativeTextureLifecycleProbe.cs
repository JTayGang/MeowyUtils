using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.File;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Vortice.Direct3D11;

namespace FPSBooster;

/// <summary>
/// Opt-in, read-only instrumentation for the native texture resource lifecycle.
/// Hooks are derived from a manually acquired TextureResourceHandle vtable. The probe
/// only records copied metadata and pointer values; it never retains native pointers
/// beyond a callback or writes to game-owned resources.
/// </summary>
public sealed unsafe class NativeTextureLifecycleProbe : IDisposable
{
    private const int ExpectedTextureHandleSize = 0x150;
    private const int ReadVtableIndex = 32;
    private const int LoadVtableIndex = 33;
    private const int RereadVtableIndex = 34;
    private const int D3D11DeviceCreateTexture2DVtableIndex = 5;
    private const int D3D11DeviceCreateShaderResourceViewVtableIndex = 7;
    private const int MaximumD3D11CreateCallsPerInitialization = 4;
    private const int MaximumD3D11ViewCallsPerInitialization = 8;
    private const int MaximumD3D11SubresourcesToCapture = 256;
    private const ulong MaximumNativeMipPayloadCaptureBytes = 64UL * 1024UL * 1024UL;
    private const int MaximumQueuedEvents = 512;
    private const int MaximumRecentEvents = 80;

    // Current FFXIVClientStructs signatures. DoFileJob is a function-entry signature.
    // FinishLoad's signature starts at a relative CALL instruction; resolve its target
    // before installing the detour.
    private const string FileThreadDoFileJobSignature = "40 56 41 56 48 83 EC ?? 0F BE 02";
    private const string ResourceHandleFinishLoadCallSignature = "E8 ?? ?? ?? ?? 80 3F 0B 75 28";
    // FileDescriptor.Read is exposed by FFXIVClientStructs as a relative-call signature.
    // This hook is optional: if the current client has no unique match, the other lifecycle
    // hooks remain usable and the UI reports that the lower-level read was not instrumented.
    private const string FileDescriptorReadCallSignature = "E8 ?? ?? ?? ?? 8B 45 C7 89 83 ?? ?? ?? ??";
    // Texture.InitializeContents is also exposed by the current FFXIVClientStructs member signature.
    private const string TextureInitializeContentsCallSignature = "E8 ?? ?? ?? ?? EB ?? ?? ?? 25 ?? ?? ?? ?? 3D";

    // FFXIVClientStructs declares native C++ bool parameters/returns as bool. A byte
    // preserves the one-byte Windows x64 ABI representation and avoids 4-byte BOOL marshalling.
    private delegate byte ResourceReadDelegate(ResourceHandle* self, FileDescriptor* fileDescriptor, byte failedToOpen);
    private delegate byte ResourceLoadDelegate(ResourceHandle* self, void* contents, byte lastIoResult);
    private delegate byte FileJobDelegate(FileThread* self, FileDescriptor* fileDescriptor, int priority, byte isSync);
    private delegate byte FinishLoadDelegate(ResourceHandle* self, FileDescriptor* fileDescriptor, byte openFileResult, byte a4);
    private delegate byte FileDescriptorReadDelegate(FileDescriptor* self, byte* outputBuffer, ulong length, ulong start, byte resetPosition);
    private delegate byte TextureInitializeContentsDelegate(Texture* self, void* contents);
    private delegate int D3D11CreateTexture2DDelegate(void* self, NativeD3D11Texture2DDescription* description, NativeD3D11SubresourceData* initialData, void** texture2D);
    private delegate int D3D11CreateShaderResourceViewDelegate(void* self, void* resource, NativeD3D11ShaderResourceViewDescription* description, void** shaderResourceView);

    // If FileDescriptor.Read is reached inside a tracked texture job/read call, the
    // thread-local numeric addresses correlate it without retaining native pointers.
    [ThreadStatic] private static nint _activeTextureHandle;
    [ThreadStatic] private static nint _activeTextureFileDescriptor;
    // Numeric payload-buffer metadata is scoped to the enclosing FileJob call only. The buffer is
    // never dereferenced or modified; this lets InitializeContents compare pointer identity safely.
    [ThreadStatic] private static nint _activeTexturePayloadBuffer;
    [ThreadStatic] private static ulong _activeTexturePayloadLength;
    [ThreadStatic] private static ulong _activeTexturePayloadOffset;
    [ThreadStatic] private static nint _activeTexturePayloadHandle;
    [ThreadStatic] private static nint _activeTexturePayloadDescriptor;
    // CreateTexture2D is a device-wide COM method. These thread-local fields restrict
    // observations to calls nested inside the currently tracked Texture.InitializeContents.
    [ThreadStatic] private static nint _activeTextureInitializeObject;
    [ThreadStatic] private static uint _activeTextureInitializeResourceId;
    [ThreadStatic] private static nint _activeTextureInitializePayloadBuffer;
    [ThreadStatic] private static ulong _activeTextureInitializePayloadLength;
    [ThreadStatic] private static List<NativeD3D11TextureCreateSnapshot>? _activeD3D11TextureCreateSnapshots;
    [ThreadStatic] private static List<NativeD3D11ShaderResourceViewSnapshot>? _activeD3D11ShaderResourceViewSnapshots;
    [ThreadStatic] private static string? _activeTextureInitializeResourceFileName;

    private readonly IGameInteropProvider _interop;
    private readonly ISigScanner _sigScanner;
    private readonly ConcurrentQueue<NativeTextureLifecycleEvent> _pendingEvents = new();
    private readonly List<NativeTextureLifecycleEvent> _recentEvents = new(MaximumRecentEvents);
    private readonly object _nativeMipPayloadCaptureSync = new();

    private Hook<ResourceReadDelegate>? _readHook;
    private Hook<ResourceReadDelegate>? _rereadHook;
    private Hook<ResourceLoadDelegate>? _loadHook;
    private Hook<FileJobDelegate>? _fileJobHook;
    private Hook<FinishLoadDelegate>? _finishLoadHook;
    private Hook<FileDescriptorReadDelegate>? _fileDescriptorReadHook;
    private Hook<TextureInitializeContentsDelegate>? _textureInitializeContentsHook;
    private Hook<D3D11CreateTexture2DDelegate>? _d3d11CreateTexture2DHook;
    private Hook<D3D11CreateShaderResourceViewDelegate>? _d3d11CreateShaderResourceViewHook;

    // Keep disposal-safe originals for callbacks that were already in flight when
    // StopCapture/Dispose disabled the native detours.
    private ResourceReadDelegate? _readOriginal;
    private ResourceReadDelegate? _rereadOriginal;
    private ResourceLoadDelegate? _loadOriginal;
    private FileJobDelegate? _fileJobOriginal;
    private FinishLoadDelegate? _finishLoadOriginal;
    private FileDescriptorReadDelegate? _fileDescriptorReadOriginal;
    private TextureInitializeContentsDelegate? _textureInitializeContentsOriginal;
    private D3D11CreateTexture2DDelegate? _d3d11CreateTexture2DOriginal;
    private D3D11CreateShaderResourceViewDelegate? _d3d11CreateShaderResourceViewOriginal;

    private nint _vtableAddress;
    private nint _readAddress;
    private nint _loadAddress;
    private nint _rereadAddress;
    private nint _fileJobAddress;
    private nint _finishLoadAddress;
    private nint _fileDescriptorReadAddress;
    private nint _textureInitializeContentsAddress;
    private nint _d3d11CreateTexture2DAddress;
    private nint _d3d11CreateShaderResourceViewAddress;
    private bool _readAndRereadShareTarget;
    private int _captureEnabled;
    private int _nativeMipPayloadCaptureArmed;
    private int _nativeMipPayloadCaptureGeneration;
    // -1 means no resource filter; otherwise only this selected ResourceHandle.Id may claim the capture.
    private long _nativeMipPayloadCaptureExpectedResourceId = -1;
    private NativeTextureMipPayloadCapture? _pendingNativeMipPayloadCapture;
    private string _nativeMipPayloadCaptureStatus = "Not armed.";
    private int _queuedEventCount;
    private long _nextSequence;
    private long _capturedEventCount;
    private long _droppedEventCount;
    private long _captureErrorCount;
    private bool _disposed;

    public NativeTextureLifecycleProbe(IGameInteropProvider interop, ISigScanner sigScanner)
    {
        _interop = interop;
        _sigScanner = sigScanner;
    }

    public bool IsInstalled => _readHook != null && _loadHook != null && _fileJobHook != null && _finishLoadHook != null;
    public bool CaptureEnabled => Volatile.Read(ref _captureEnabled) != 0;
    public bool ReadAndRereadShareTarget => _readAndRereadShareTarget;
    public nint VtableAddress => _vtableAddress;
    public nint ReadAddress => _readAddress;
    public nint LoadAddress => _loadAddress;
    public nint RereadAddress => _rereadAddress;
    public nint FileJobAddress => _fileJobAddress;
    public nint FinishLoadAddress => _finishLoadAddress;
    public nint FileDescriptorReadAddress => _fileDescriptorReadAddress;
    public bool FileDescriptorReadHookInstalled => _fileDescriptorReadHook != null;
    public string FileDescriptorReadStatus { get; private set; } = "FileDescriptor.Read hook not resolved yet.";
    public nint TextureInitializeContentsAddress => _textureInitializeContentsAddress;
    public bool TextureInitializeContentsHookInstalled => _textureInitializeContentsHook != null;
    public string TextureInitializeContentsStatus { get; private set; } = "Texture.InitializeContents hook not resolved yet.";
    public nint D3D11CreateTexture2DAddress => _d3d11CreateTexture2DAddress;
    public bool D3D11CreateTexture2DHookInstalled => _d3d11CreateTexture2DHook != null;
    public string D3D11CreateTexture2DStatus { get; private set; } = "D3D11 CreateTexture2D hook not resolved yet.";
    public nint D3D11CreateShaderResourceViewAddress => _d3d11CreateShaderResourceViewAddress;
    public bool D3D11CreateShaderResourceViewHookInstalled => _d3d11CreateShaderResourceViewHook != null;
    public string D3D11CreateShaderResourceViewStatus { get; private set; } = "D3D11 CreateShaderResourceView hook not resolved yet.";
    public bool NativeMipPayloadCaptureArmed => Volatile.Read(ref _nativeMipPayloadCaptureArmed) != 0;
    public bool HasPendingNativeMipPayloadCapture => Volatile.Read(ref _pendingNativeMipPayloadCapture) != null;
    public uint? NativeMipPayloadCaptureTargetResourceId
    {
        get
        {
            long value = Interlocked.Read(ref _nativeMipPayloadCaptureExpectedResourceId);
            return value < 0 ? null : checked((uint)value);
        }
    }
    public string NativeMipPayloadCaptureStatus => Volatile.Read(ref _nativeMipPayloadCaptureStatus);
    public long CapturedEventCount => Interlocked.Read(ref _capturedEventCount);
    public long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);
    public long CaptureErrorCount => Interlocked.Read(ref _captureErrorCount);
    public int PendingEventCount => Math.Max(0, Volatile.Read(ref _queuedEventCount));
    public IReadOnlyList<NativeTextureLifecycleEvent> RecentEvents => _recentEvents;

    public string Status { get; private set; } = "Native lifecycle hooks are not installed.";
    public string? Error { get; private set; }
    public int LastObservedUiThreadId { get; private set; }

    public void SetError(string message) => Error = message;

    /// <summary>Arms a one-shot copy for the selected resource ID, avoiding unrelated compatible BC7 loads.</summary>
    public void ArmNativeMipPayloadCapture(uint targetResourceId)
    {
        lock (_nativeMipPayloadCaptureSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!CaptureEnabled)
                throw new InvalidOperationException("Start native lifecycle capture before arming the progressive shadow preview.");
            if (_d3d11CreateTexture2DHook?.IsEnabled != true)
                throw new InvalidOperationException("The D3D11 CreateTexture2D hook is not enabled.");
            if (HasPendingNativeMipPayloadCapture)
                throw new InvalidOperationException("A native mip payload is already waiting to be previewed. Create or discard that preview first.");
            if (Volatile.Read(ref _nativeMipPayloadCaptureArmed) != 0)
                throw new InvalidOperationException("A native mip payload capture is already armed.");

            // Publish a new generation before arming. Stop/Cancel invalidates any callback that
            // already claimed the one-shot capture but has not yet handed its copy to the UI.
            Interlocked.Increment(ref _nativeMipPayloadCaptureGeneration);
            Interlocked.Exchange(ref _nativeMipPayloadCaptureExpectedResourceId, targetResourceId);
            Volatile.Write(ref _nativeMipPayloadCaptureArmed, 1);
            Volatile.Write(ref _nativeMipPayloadCaptureStatus,
                $"Armed for selected resource ID {targetResourceId}. Trigger a fresh load of that resource; unrelated BC7 textures will be skipped.");
        }
    }

    /// <summary>Transfers the one-shot managed mip copy to the UI thread.</summary>
    public bool TryTakeNativeMipPayloadCapture(out NativeTextureMipPayloadCapture? capture)
    {
        capture = Interlocked.Exchange(ref _pendingNativeMipPayloadCapture, null);
        return capture != null;
    }

    /// <summary>Cancels an armed capture and discards any queued managed mip copy.</summary>
    public void CancelNativeMipPayloadCapture()
    {
        lock (_nativeMipPayloadCaptureSync)
        {
            Interlocked.Increment(ref _nativeMipPayloadCaptureGeneration);
            Interlocked.Exchange(ref _nativeMipPayloadCaptureArmed, 0);
            Interlocked.Exchange(ref _pendingNativeMipPayloadCapture, null);
            Interlocked.Exchange(ref _nativeMipPayloadCaptureExpectedResourceId, -1);
            Volatile.Write(ref _nativeMipPayloadCaptureStatus, "Cancelled or discarded.");
        }
    }

    /// <summary>Called from the ImGui draw path so synchronous native file jobs can be compared with the UI thread.</summary>
    public void ObserveUiThread() => LastObservedUiThreadId = Environment.CurrentManagedThreadId;

    /// <summary>
    /// Installs hooks using the vtable of the already-acquired texture handle, then starts
    /// capture. The handle pointer is used only to copy vtable function addresses.
    /// </summary>
    public void StartCapture(TextureResourceHandle* seedHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (seedHandle == null)
            throw new InvalidOperationException("Acquire a native texture handle before starting lifecycle capture.");

        if (sizeof(TextureResourceHandle) != ExpectedTextureHandleSize)
            throw new InvalidOperationException(
                $"TextureResourceHandle layout is 0x{sizeof(TextureResourceHandle):X}; expected 0x{ExpectedTextureHandleSize:X}. Hooks were not installed.");

        if (((ResourceHandle*)seedHandle)->GetHandleSize() != ExpectedTextureHandleSize)
            throw new InvalidOperationException("The seed handle no longer reports the expected texture-resource size.");

        // ResourceHandle's vtable entries are documented in FFXIVClientStructs:
        // Read=32, Load=33, Reread=34. Copy addresses only; never retain the vtable pointer.
        nint* vtable = *(nint**)seedHandle;
        if (vtable == null)
            throw new InvalidOperationException("The acquired texture handle has a null vtable.");

        nint readAddress = vtable[ReadVtableIndex];
        nint loadAddress = vtable[LoadVtableIndex];
        nint rereadAddress = vtable[RereadVtableIndex];
        if (readAddress == 0 || loadAddress == 0 || rereadAddress == 0)
            throw new InvalidOperationException("One or more texture-resource vtable entries are null. No hooks were installed.");
        if (readAddress == loadAddress || loadAddress == rereadAddress)
            throw new InvalidOperationException("The texture-resource vtable contains unexpected duplicate read/load targets. No hooks were installed.");

        nint vtableAddress = (nint)vtable;

        // The file-job hook observes only descriptors that point at a TextureResourceHandle.
        // Resolve both signatures uniquely and fail closed if the current client does not match.
        nint fileJobAddress = ResolveUniqueTextSignature(FileThreadDoFileJobSignature, "FileThread.DoFileJob");
        nint finishLoadAddress = ResolveRelativeCallTarget(
            ResolveUniqueTextSignature(ResourceHandleFinishLoadCallSignature, "ResourceHandle.FinishLoad"),
            "ResourceHandle.FinishLoad");
        if (fileJobAddress == finishLoadAddress || fileJobAddress == readAddress || fileJobAddress == loadAddress || fileJobAddress == rereadAddress
            || finishLoadAddress == readAddress || finishLoadAddress == loadAddress || finishLoadAddress == rereadAddress)
            throw new InvalidOperationException("The resolved native file-job / FinishLoad target overlaps another resource hook. No hooks were installed.");

        // Lower-level FileDescriptor.Read instrumentation is deliberately optional. It is
        // useful for separating actual file-buffer reads from work elsewhere in ResourceHandle.Read.
        nint fileDescriptorReadAddress = 0;
        try
        {
            fileDescriptorReadAddress = ResolveRelativeCallTarget(
                ResolveUniqueTextSignature(FileDescriptorReadCallSignature, "FileDescriptor.Read"),
                "FileDescriptor.Read");
            if (fileDescriptorReadAddress == fileJobAddress || fileDescriptorReadAddress == finishLoadAddress
                || fileDescriptorReadAddress == readAddress || fileDescriptorReadAddress == loadAddress || fileDescriptorReadAddress == rereadAddress)
                throw new InvalidOperationException("The FileDescriptor.Read target overlaps another native hook target.");
            FileDescriptorReadStatus = "Signature resolved; hook will be installed.";
        }
        catch (Exception ex)
        {
            fileDescriptorReadAddress = 0;
            FileDescriptorReadStatus = $"Unavailable (optional hook): {ex.Message}";
        }

        // Observe the point where the renderer Texture object consumes the supplied contents.
        // This is optional and read-only; an unresolved/ambiguous signature does not block the
        // existing resource lifecycle hooks.
        nint textureInitializeContentsAddress = 0;
        try
        {
            textureInitializeContentsAddress = ResolveRelativeCallTarget(
                ResolveUniqueTextSignature(TextureInitializeContentsCallSignature, "Texture.InitializeContents"),
                "Texture.InitializeContents");
            if (textureInitializeContentsAddress == fileJobAddress || textureInitializeContentsAddress == finishLoadAddress
                || textureInitializeContentsAddress == fileDescriptorReadAddress || textureInitializeContentsAddress == readAddress
                || textureInitializeContentsAddress == loadAddress || textureInitializeContentsAddress == rereadAddress)
                throw new InvalidOperationException("The Texture.InitializeContents target overlaps another native hook target.");
            TextureInitializeContentsStatus = "Signature resolved; hook will be installed.";
        }
        catch (Exception ex)
        {
            textureInitializeContentsAddress = 0;
            TextureInitializeContentsStatus = $"Unavailable (optional hook): {ex.Message}";
        }

        // D3D11 device CreateTexture2D is a COM vtable method rather than a game .text
        // signature. Device.D3D11Forwarder is documented by FFXIVClientStructs as an
        // ID3D11Device interface. Install this only when InitializeContents is available,
        // then filter calls to the same thread-local initialization scope.
        nint d3d11CreateTexture2DAddress = 0;
        try
        {
            if (textureInitializeContentsAddress == 0)
                throw new InvalidOperationException("Requires the Texture.InitializeContents hook.");

            d3d11CreateTexture2DAddress = ResolveD3D11CreateTexture2DAddress();
            if (d3d11CreateTexture2DAddress == readAddress || d3d11CreateTexture2DAddress == loadAddress
                || d3d11CreateTexture2DAddress == rereadAddress || d3d11CreateTexture2DAddress == fileJobAddress
                || d3d11CreateTexture2DAddress == finishLoadAddress || d3d11CreateTexture2DAddress == fileDescriptorReadAddress
                || d3d11CreateTexture2DAddress == textureInitializeContentsAddress)
                throw new InvalidOperationException("The D3D11 CreateTexture2D target overlaps another native hook target.");
            D3D11CreateTexture2DStatus = "Device vtable resolved; optional hook will be installed.";
        }
        catch (Exception ex)
        {
            d3d11CreateTexture2DAddress = 0;
            D3D11CreateTexture2DStatus = $"Unavailable (optional hook): {ex.Message}";
        }

        nint d3d11CreateShaderResourceViewAddress = 0;
        try
        {
            if (textureInitializeContentsAddress == 0 || d3d11CreateTexture2DAddress == 0)
                throw new InvalidOperationException("Requires Texture.InitializeContents and CreateTexture2D hooks.");

            d3d11CreateShaderResourceViewAddress = ResolveD3D11CreateShaderResourceViewAddress();
            if (d3d11CreateShaderResourceViewAddress == readAddress
                || d3d11CreateShaderResourceViewAddress == loadAddress
                || d3d11CreateShaderResourceViewAddress == rereadAddress
                || d3d11CreateShaderResourceViewAddress == fileJobAddress
                || d3d11CreateShaderResourceViewAddress == finishLoadAddress
                || d3d11CreateShaderResourceViewAddress == fileDescriptorReadAddress
                || d3d11CreateShaderResourceViewAddress == textureInitializeContentsAddress
                || d3d11CreateShaderResourceViewAddress == d3d11CreateTexture2DAddress)
                throw new InvalidOperationException("The D3D11 CreateShaderResourceView target overlaps another native hook target.");
            D3D11CreateShaderResourceViewStatus = "Device vtable resolved; optional hook will be installed.";
        }
        catch (Exception ex)
        {
            d3d11CreateShaderResourceViewAddress = 0;
            D3D11CreateShaderResourceViewStatus = $"Unavailable (optional hook): {ex.Message}";
        }

        if (IsInstalled)
        {
            // If the optional lower-level hook was not installed during the initial attempt,
            // keep it absent on resume rather than changing the established hook set.
            if (_fileDescriptorReadHook == null)
                fileDescriptorReadAddress = 0;
            if (_textureInitializeContentsHook == null)
                textureInitializeContentsAddress = 0;
            if (_d3d11CreateTexture2DHook == null)
                d3d11CreateTexture2DAddress = 0;
            if (_d3d11CreateShaderResourceViewHook == null)
                d3d11CreateShaderResourceViewAddress = 0;
            if (_vtableAddress != vtableAddress)
                throw new InvalidOperationException(
                    "This process exposed a different TextureResourceHandle vtable. The existing hooks were left unchanged; restart FPSBooster before sampling a different vtable.");
            if (_fileJobAddress != fileJobAddress || _finishLoadAddress != finishLoadAddress
                || _fileDescriptorReadAddress != fileDescriptorReadAddress
                || _textureInitializeContentsAddress != textureInitializeContentsAddress
                || _d3d11CreateTexture2DAddress != d3d11CreateTexture2DAddress
                || _d3d11CreateShaderResourceViewAddress != d3d11CreateShaderResourceViewAddress)
                throw new InvalidOperationException(
                    "One or more native file-pipeline signatures resolved to different addresses. Existing hooks were left unchanged; restart FPSBooster before sampling again.");

            EnableHooks();
            Volatile.Write(ref _captureEnabled, 1);
            Error = null;
            Status = "Read-only native texture lifecycle capture is running, including FileJob and FinishLoad.";
            return;
        }

        Hook<ResourceReadDelegate>? readHook = null;
        Hook<ResourceReadDelegate>? rereadHook = null;
        Hook<ResourceLoadDelegate>? loadHook = null;
        Hook<FileJobDelegate>? fileJobHook = null;
        Hook<FinishLoadDelegate>? finishLoadHook = null;
        Hook<FileDescriptorReadDelegate>? fileDescriptorReadHook = null;
        Hook<TextureInitializeContentsDelegate>? textureInitializeContentsHook = null;
        Hook<D3D11CreateTexture2DDelegate>? d3d11CreateTexture2DHook = null;
        Hook<D3D11CreateShaderResourceViewDelegate>? d3d11CreateShaderResourceViewHook = null;

        try
        {
            readHook = _interop.HookFromAddress<ResourceReadDelegate>(readAddress, ReadDetour);
            if (rereadAddress != readAddress)
                rereadHook = _interop.HookFromAddress<ResourceReadDelegate>(rereadAddress, RereadDetour);
            loadHook = _interop.HookFromAddress<ResourceLoadDelegate>(loadAddress, LoadDetour);
            fileJobHook = _interop.HookFromAddress<FileJobDelegate>(fileJobAddress, FileJobDetour);
            finishLoadHook = _interop.HookFromAddress<FinishLoadDelegate>(finishLoadAddress, FinishLoadDetour);
            if (fileDescriptorReadAddress != 0)
            {
                try
                {
                    fileDescriptorReadHook = _interop.HookFromAddress<FileDescriptorReadDelegate>(fileDescriptorReadAddress, FileDescriptorReadDetour);
                    FileDescriptorReadStatus = "Installed; capturing reads associated with an active texture descriptor.";
                }
                catch (Exception ex)
                {
                    fileDescriptorReadAddress = 0;
                    FileDescriptorReadStatus = $"Unavailable (optional hook install failed): {ex.Message}";
                }
            }
            if (textureInitializeContentsAddress != 0)
            {
                try
                {
                    textureInitializeContentsHook = _interop.HookFromAddress<TextureInitializeContentsDelegate>(
                        textureInitializeContentsAddress, TextureInitializeContentsDetour);
                    TextureInitializeContentsStatus = "Installed; correlating Texture.InitializeContents with the active texture payload read.";
                }
                catch (Exception ex)
                {
                    textureInitializeContentsAddress = 0;
                    TextureInitializeContentsStatus = $"Unavailable (optional hook install failed): {ex.Message}";
                }
            }
            if (d3d11CreateTexture2DAddress != 0 && textureInitializeContentsHook != null)
            {
                try
                {
                    d3d11CreateTexture2DHook = _interop.HookFromAddress<D3D11CreateTexture2DDelegate>(
                        d3d11CreateTexture2DAddress, D3D11CreateTexture2DDetour);
                    D3D11CreateTexture2DStatus = "Installed; captures subresource descriptors only inside an active texture initialization.";
                }
                catch (Exception ex)
                {
                    d3d11CreateTexture2DAddress = 0;
                    D3D11CreateTexture2DStatus = $"Unavailable (optional hook install failed): {ex.Message}";
                }
            }
            else if (textureInitializeContentsHook == null)
            {
                d3d11CreateTexture2DAddress = 0;
                D3D11CreateTexture2DStatus = "Unavailable (optional hook): Texture.InitializeContents hook was not installed.";
            }

            if (d3d11CreateShaderResourceViewAddress != 0 && d3d11CreateTexture2DHook != null)
            {
                try
                {
                    d3d11CreateShaderResourceViewHook = _interop.HookFromAddress<D3D11CreateShaderResourceViewDelegate>(
                        d3d11CreateShaderResourceViewAddress, D3D11CreateShaderResourceViewDetour);
                    D3D11CreateShaderResourceViewStatus = "Installed; captures the created texture's actual shader-resource-view mip range.";
                }
                catch (Exception ex)
                {
                    d3d11CreateShaderResourceViewAddress = 0;
                    D3D11CreateShaderResourceViewStatus = $"Unavailable (optional hook install failed): {ex.Message}";
                }
            }
            else if (d3d11CreateTexture2DHook == null)
            {
                d3d11CreateShaderResourceViewAddress = 0;
                D3D11CreateShaderResourceViewStatus = "Unavailable (optional hook): CreateTexture2D hook was not installed.";
            }

            _readHook = readHook;
            _rereadHook = rereadHook;
            _loadHook = loadHook;
            _fileJobHook = fileJobHook;
            _finishLoadHook = finishLoadHook;
            _fileDescriptorReadHook = fileDescriptorReadHook;
            _textureInitializeContentsHook = textureInitializeContentsHook;
            _d3d11CreateTexture2DHook = d3d11CreateTexture2DHook;
            _d3d11CreateShaderResourceViewHook = d3d11CreateShaderResourceViewHook;
            _readOriginal = readHook.OriginalDisposeSafe;
            _rereadOriginal = rereadHook?.OriginalDisposeSafe;
            _loadOriginal = loadHook.OriginalDisposeSafe;
            _fileJobOriginal = fileJobHook.OriginalDisposeSafe;
            _finishLoadOriginal = finishLoadHook.OriginalDisposeSafe;
            _fileDescriptorReadOriginal = fileDescriptorReadHook?.OriginalDisposeSafe;
            _textureInitializeContentsOriginal = textureInitializeContentsHook?.OriginalDisposeSafe;
            _d3d11CreateTexture2DOriginal = d3d11CreateTexture2DHook?.OriginalDisposeSafe;
            _d3d11CreateShaderResourceViewOriginal = d3d11CreateShaderResourceViewHook?.OriginalDisposeSafe;
            _vtableAddress = vtableAddress;
            _readAddress = readAddress;
            _loadAddress = loadAddress;
            _rereadAddress = rereadAddress;
            _fileJobAddress = fileJobAddress;
            _finishLoadAddress = finishLoadAddress;
            _fileDescriptorReadAddress = fileDescriptorReadHook == null ? 0 : fileDescriptorReadAddress;
            _textureInitializeContentsAddress = textureInitializeContentsHook == null ? 0 : textureInitializeContentsAddress;
            _d3d11CreateTexture2DAddress = d3d11CreateTexture2DHook == null ? 0 : d3d11CreateTexture2DAddress;
            _d3d11CreateShaderResourceViewAddress = d3d11CreateShaderResourceViewHook == null ? 0 : d3d11CreateShaderResourceViewAddress;
            _readAndRereadShareTarget = rereadAddress == readAddress;

            EnableHooks();
            Volatile.Write(ref _captureEnabled, 1);
            Error = null;
            Status = _readAndRereadShareTarget
                ? "Capture running. Read and Reread share one native function address; FileJob and FinishLoad are also observed."
                : "Read-only native texture lifecycle capture is running, including FileJob and FinishLoad.";
        }
        catch
        {
            Volatile.Write(ref _captureEnabled, 0);
            SafeDisable(readHook);
            SafeDisable(rereadHook);
            SafeDisable(loadHook);
            SafeDisable(fileJobHook);
            SafeDisable(finishLoadHook);
            SafeDisable(fileDescriptorReadHook);
            SafeDisable(textureInitializeContentsHook);
            SafeDisable(d3d11CreateTexture2DHook);
            SafeDisable(d3d11CreateShaderResourceViewHook);
            readHook?.Dispose();
            rereadHook?.Dispose();
            loadHook?.Dispose();
            fileJobHook?.Dispose();
            finishLoadHook?.Dispose();
            fileDescriptorReadHook?.Dispose();
            textureInitializeContentsHook?.Dispose();
            d3d11CreateTexture2DHook?.Dispose();
            d3d11CreateShaderResourceViewHook?.Dispose();
            _readHook = null;
            _rereadHook = null;
            _loadHook = null;
            _fileJobHook = null;
            _finishLoadHook = null;
            _fileDescriptorReadHook = null;
            _textureInitializeContentsHook = null;
            _d3d11CreateTexture2DHook = null;
            _d3d11CreateShaderResourceViewHook = null;
            // Keep OriginalDisposeSafe delegates alive in case a callback was already in flight.
            _vtableAddress = 0;
            _readAddress = 0;
            _loadAddress = 0;
            _rereadAddress = 0;
            _fileJobAddress = 0;
            _finishLoadAddress = 0;
            _fileDescriptorReadAddress = 0;
            _textureInitializeContentsAddress = 0;
            _d3d11CreateTexture2DAddress = 0;
            _d3d11CreateShaderResourceViewAddress = 0;
            _readAndRereadShareTarget = false;
            throw;
        }
    }

    public void StopCapture()
    {
        lock (_nativeMipPayloadCaptureSync)
        {
            Volatile.Write(ref _captureEnabled, 0);
            Interlocked.Increment(ref _nativeMipPayloadCaptureGeneration);
            if (Interlocked.Exchange(ref _nativeMipPayloadCaptureArmed, 0) != 0)
            {
                Interlocked.Exchange(ref _nativeMipPayloadCaptureExpectedResourceId, -1);
                Volatile.Write(ref _nativeMipPayloadCaptureStatus, "Capture stopped before a compatible texture was found.");
            }
        }
        SafeDisable(_readHook);
        SafeDisable(_rereadHook);
        SafeDisable(_loadHook);
        SafeDisable(_fileJobHook);
        SafeDisable(_finishLoadHook);
        SafeDisable(_fileDescriptorReadHook);
        SafeDisable(_textureInitializeContentsHook);
        SafeDisable(_d3d11CreateTexture2DHook);
        SafeDisable(_d3d11CreateShaderResourceViewHook);
        if (IsInstalled)
            Status = "Native lifecycle hooks are installed but disabled.";
    }

    /// <summary>Move queued callback events to the UI-owned recent-event list.</summary>
    public int DrainEvents()
    {
        int drained = 0;
        while (_pendingEvents.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _queuedEventCount);
            _recentEvents.Insert(0, item);
            if (_recentEvents.Count > MaximumRecentEvents)
                _recentEvents.RemoveAt(_recentEvents.Count - 1);
            drained++;
        }

        return drained;
    }

    public void ClearEvents()
    {
        // Clear only the visible history. Keep cumulative counters monotonic so a callback
        // already in flight cannot race with a counter reset and make queue counts inconsistent.
        _recentEvents.Clear();
        while (_pendingEvents.TryDequeue(out _))
            Interlocked.Decrement(ref _queuedEventCount);
    }

    private void EnableHooks()
    {
        try
        {
            _readHook?.Enable();
            _rereadHook?.Enable();
            _loadHook?.Enable();
            _fileJobHook?.Enable();
            _finishLoadHook?.Enable();
            _fileDescriptorReadHook?.Enable();
            _textureInitializeContentsHook?.Enable();
            if (_d3d11CreateTexture2DHook != null)
            {
                try
                {
                    _d3d11CreateTexture2DHook.Enable();
                }
                catch (Exception ex)
                {
                    SafeDisable(_d3d11CreateTexture2DHook);
                    try { _d3d11CreateTexture2DHook.Dispose(); } catch { }
                    _d3d11CreateTexture2DHook = null;
                    _d3d11CreateTexture2DAddress = 0;
                    D3D11CreateTexture2DStatus = $"Unavailable (optional hook enable failed): {ex.Message}";
                    SafeDisable(_d3d11CreateShaderResourceViewHook);
                    try { _d3d11CreateShaderResourceViewHook?.Dispose(); } catch { }
                    _d3d11CreateShaderResourceViewHook = null;
                    _d3d11CreateShaderResourceViewAddress = 0;
                    D3D11CreateShaderResourceViewStatus = "Unavailable (optional hook): CreateTexture2D hook could not be enabled.";
                }
            }

            if (_d3d11CreateShaderResourceViewHook != null)
            {
                try
                {
                    _d3d11CreateShaderResourceViewHook.Enable();
                }
                catch (Exception ex)
                {
                    SafeDisable(_d3d11CreateShaderResourceViewHook);
                    try { _d3d11CreateShaderResourceViewHook.Dispose(); } catch { }
                    _d3d11CreateShaderResourceViewHook = null;
                    _d3d11CreateShaderResourceViewAddress = 0;
                    D3D11CreateShaderResourceViewStatus = $"Unavailable (optional hook enable failed): {ex.Message}";
                }
            }
        }
        catch
        {
            Volatile.Write(ref _captureEnabled, 0);
            SafeDisable(_readHook);
            SafeDisable(_rereadHook);
            SafeDisable(_loadHook);
            SafeDisable(_fileJobHook);
            SafeDisable(_finishLoadHook);
            SafeDisable(_fileDescriptorReadHook);
            SafeDisable(_textureInitializeContentsHook);
            SafeDisable(_d3d11CreateTexture2DHook);
            SafeDisable(_d3d11CreateShaderResourceViewHook);
            throw;
        }
    }

    private static void SafeDisable<T>(Hook<T>? hook) where T : Delegate
    {
        try
        {
            if (hook?.IsEnabled == true)
                hook.Disable();
        }
        catch
        {
            // Shutdown must still dispose the remaining hooks.
        }
    }

    private byte ReadDetour(ResourceHandle* self, FileDescriptor* fileDescriptor, byte failedToOpen)
    {
        bool capture = CaptureEnabled && IsTextureHandle(self);
        ReadCaptureContext context = default;
        NativeTextureStateSnapshot stateBefore = default;
        if (capture)
        {
            try
            {
                context = CaptureReadContext(self, fileDescriptor);
                stateBefore = CaptureTextureState(self);
            }
            catch { Interlocked.Increment(ref _captureErrorCount); }
        }

        nint previousHandle = _activeTextureHandle;
        nint previousDescriptor = _activeTextureFileDescriptor;
        if (capture)
        {
            _activeTextureHandle = (nint)self;
            _activeTextureFileDescriptor = (nint)fileDescriptor;
        }

        long started = capture ? Stopwatch.GetTimestamp() : 0;
        byte result;
        try
        {
            result = _readOriginal!(self, fileDescriptor, failedToOpen);
        }
        finally
        {
            if (capture)
            {
                _activeTextureHandle = previousHandle;
                _activeTextureFileDescriptor = previousDescriptor;
            }
        }
        double elapsed = capture ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0d;
        if (capture)
        {
            NativeTextureStateSnapshot stateAfter = default;
            ReadCaptureContext exitContext = context;
            try
            {
                stateAfter = CaptureTextureState(self);
                exitContext = CaptureReadContext(self, fileDescriptor);
            }
            catch { Interlocked.Increment(ref _captureErrorCount); }
            TryQueueReadEvent("Read", self, context, failedToOpen, result, elapsed,
                stateBefore, stateAfter, (nint)fileDescriptor, exitContext);
        }
        return result;
    }

    private byte RereadDetour(ResourceHandle* self, FileDescriptor* fileDescriptor, byte failedToOpen)
    {
        bool capture = CaptureEnabled && IsTextureHandle(self);
        ReadCaptureContext context = default;
        NativeTextureStateSnapshot stateBefore = default;
        if (capture)
        {
            try
            {
                context = CaptureReadContext(self, fileDescriptor);
                stateBefore = CaptureTextureState(self);
            }
            catch { Interlocked.Increment(ref _captureErrorCount); }
        }

        nint previousHandle = _activeTextureHandle;
        nint previousDescriptor = _activeTextureFileDescriptor;
        if (capture)
        {
            _activeTextureHandle = (nint)self;
            _activeTextureFileDescriptor = (nint)fileDescriptor;
        }

        long started = capture ? Stopwatch.GetTimestamp() : 0;
        byte result;
        try
        {
            result = _rereadOriginal!(self, fileDescriptor, failedToOpen);
        }
        finally
        {
            if (capture)
            {
                _activeTextureHandle = previousHandle;
                _activeTextureFileDescriptor = previousDescriptor;
            }
        }
        double elapsed = capture ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0d;
        if (capture)
        {
            NativeTextureStateSnapshot stateAfter = default;
            ReadCaptureContext exitContext = context;
            try
            {
                stateAfter = CaptureTextureState(self);
                exitContext = CaptureReadContext(self, fileDescriptor);
            }
            catch { Interlocked.Increment(ref _captureErrorCount); }
            TryQueueReadEvent("Reread", self, context, failedToOpen, result, elapsed,
                stateBefore, stateAfter, (nint)fileDescriptor, exitContext);
        }
        return result;
    }

    private byte LoadDetour(ResourceHandle* self, void* contents, byte lastIoResult)
    {
        bool capture = CaptureEnabled && IsTextureHandle(self);
        string resourceFileName = string.Empty;
        NativeTextureStateSnapshot stateBefore = default;
        nint textureWhileLoadingAddress = 0;
        nint dataWhileLoadingAddress = 0;
        nint contentsAddress = (nint)contents;
        nint loadDescriptorAddress = 0;
        nint loadDescriptorFileBufferAddress = 0;
        byte loadDescriptorFileModeValue = byte.MaxValue;
        bool loadContentsMatchesDescriptor = false;
        ReadCaptureContext context = default;
        if (capture)
        {
            try
            {
                resourceFileName = self->FileName.ToString();
                stateBefore = CaptureTextureState(self);

                // FFXIVClientStructs only documents these temporary fields as valid during Load.
                // Copy their numeric addresses at this hook point; never dereference or retain them.
                var textureHandle = (TextureResourceHandle*)self;
                textureWhileLoadingAddress = (nint)textureHandle->TextureWhileLoading;
                dataWhileLoadingAddress = (nint)textureHandle->DataWhileLoading;

                // FinishLoad calls Load with its FileDescriptor as the void* contents argument
                // in the observed path. Only interpret contents as a descriptor after confirming
                // it matches the descriptor address from the active, same-thread texture callback
                // and that the descriptor points back to this resource handle.
                if (_activeTextureHandle == (nint)self && _activeTextureFileDescriptor != 0
                    && contentsAddress == _activeTextureFileDescriptor)
                {
                    var descriptor = (FileDescriptor*)contents;
                    if (descriptor->ResourceHandle == self
                        && (descriptor->FileMode == FileMode.LoadUnpackedResource || descriptor->FileMode == FileMode.LoadSqPackResource))
                    {
                        loadContentsMatchesDescriptor = true;
                        loadDescriptorAddress = contentsAddress;
                        loadDescriptorFileBufferAddress = (nint)descriptor->FileBuffer;
                        loadDescriptorFileModeValue = (byte)descriptor->FileMode;
                        context = CaptureReadContext(self, descriptor);
                    }
                }

                if (context.ResourceFileName == string.Empty)
                    context = new ReadCaptureContext(resourceFileName, "n/a", byte.MaxValue, 0, 0, 0);
            }
            catch { Interlocked.Increment(ref _captureErrorCount); }
        }

        long started = capture ? Stopwatch.GetTimestamp() : 0;
        byte result = _loadOriginal!(self, contents, lastIoResult);
        double elapsed = capture ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0d;
        if (capture)
        {
            try
            {
                NativeTextureStateSnapshot stateAfter = CaptureTextureState(self);
                if (context.ResourceFileName == string.Empty)
                    context = new ReadCaptureContext(resourceFileName, "n/a", byte.MaxValue, 0, 0, 0);
                TryQueueResourceEvent("Load", self, context, lastIoResult, 0, result, elapsed,
                    includeTextureSnapshot: true,
                    stateBefore: stateBefore,
                    stateAfter: stateAfter,
                    exitContext: context,
                    fileDescriptorAddress: loadDescriptorAddress,
                    loadContentsAddress: contentsAddress,
                    textureWhileLoadingAddress: textureWhileLoadingAddress,
                    dataWhileLoadingAddress: dataWhileLoadingAddress,
                    loadContentsMatchesDescriptor: loadContentsMatchesDescriptor,
                    loadDescriptorFileModeValue: loadDescriptorFileModeValue,
                    loadDescriptorFileBufferAddress: loadDescriptorFileBufferAddress);
            }
            catch { Interlocked.Increment(ref _captureErrorCount); }
        }

        return result;
    }

    private byte FileJobDetour(FileThread* self, FileDescriptor* fileDescriptor, int priority, byte isSync)
    {
        bool capture = CaptureTextureFileJob(fileDescriptor, out ResourceHandle* handle, out ReadCaptureContext context);
        NativeTextureStateSnapshot stateBefore = default;
        if (capture)
        {
            try { stateBefore = CaptureTextureState(handle); }
            catch { Interlocked.Increment(ref _captureErrorCount); }
        }
        nint previousHandle = _activeTextureHandle;
        nint previousDescriptor = _activeTextureFileDescriptor;
        nint previousPayloadBuffer = _activeTexturePayloadBuffer;
        ulong previousPayloadLength = _activeTexturePayloadLength;
        ulong previousPayloadOffset = _activeTexturePayloadOffset;
        nint previousPayloadHandle = _activeTexturePayloadHandle;
        nint previousPayloadDescriptor = _activeTexturePayloadDescriptor;
        if (capture)
        {
            _activeTextureHandle = (nint)handle;
            _activeTextureFileDescriptor = (nint)fileDescriptor;
            _activeTexturePayloadBuffer = 0;
            _activeTexturePayloadLength = 0;
            _activeTexturePayloadOffset = 0;
            _activeTexturePayloadHandle = (nint)handle;
            _activeTexturePayloadDescriptor = (nint)fileDescriptor;
        }

        long started = capture ? Stopwatch.GetTimestamp() : 0;
        byte result;
        try
        {
            result = _fileJobOriginal!(self, fileDescriptor, priority, isSync);
        }
        finally
        {
            if (capture)
            {
                _activeTextureHandle = previousHandle;
                _activeTextureFileDescriptor = previousDescriptor;
                _activeTexturePayloadBuffer = previousPayloadBuffer;
                _activeTexturePayloadLength = previousPayloadLength;
                _activeTexturePayloadOffset = previousPayloadOffset;
                _activeTexturePayloadHandle = previousPayloadHandle;
                _activeTexturePayloadDescriptor = previousPayloadDescriptor;
            }
        }
        double elapsed = capture ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0d;

        if (capture)
        {
            NativeTextureStateSnapshot stateAfter = default;
            ReadCaptureContext exitContext = context;
            try
            {
                // Record descriptor state again after the native job returns. In live captures,
                // FileJob entry was mode 11 while nested ResourceHandle.Read/FinishLoad saw mode 0.
                stateAfter = CaptureTextureState(handle);
                exitContext = CaptureReadContext(handle, fileDescriptor);
            }
            catch { Interlocked.Increment(ref _captureErrorCount); }

            TryQueueResourceEvent("FileJob", handle, context, isSync, 0, result, elapsed, priority,
                stateBefore: stateBefore,
                stateAfter: stateAfter,
                exitContext: exitContext,
                fileDescriptorAddress: (nint)fileDescriptor);
        }
        return result;
    }

    private byte FinishLoadDetour(ResourceHandle* self, FileDescriptor* fileDescriptor, byte openFileResult, byte a4)
    {
        bool capture = CaptureEnabled && IsTextureHandle(self);
        ReadCaptureContext context = default;
        NativeTextureStateSnapshot stateBefore = default;
        if (capture)
        {
            try
            {
                context = CaptureReadContext(self, fileDescriptor);
                stateBefore = CaptureTextureState(self);
            }
            catch { Interlocked.Increment(ref _captureErrorCount); }
        }

        nint previousHandle = _activeTextureHandle;
        nint previousDescriptor = _activeTextureFileDescriptor;
        if (capture)
        {
            _activeTextureHandle = (nint)self;
            _activeTextureFileDescriptor = (nint)fileDescriptor;
        }

        long started = capture ? Stopwatch.GetTimestamp() : 0;
        byte result;
        try
        {
            result = _finishLoadOriginal!(self, fileDescriptor, openFileResult, a4);
        }
        finally
        {
            if (capture)
            {
                _activeTextureHandle = previousHandle;
                _activeTextureFileDescriptor = previousDescriptor;
            }
        }
        double elapsed = capture ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0d;

        if (capture)
        {
            NativeTextureStateSnapshot stateAfter = default;
            ReadCaptureContext exitContext = context;
            try
            {
                stateAfter = CaptureTextureState(self);
                exitContext = CaptureReadContext(self, fileDescriptor);
            }
            catch { Interlocked.Increment(ref _captureErrorCount); }
            TryQueueResourceEvent("FinishLoad", self, context, openFileResult, a4, result, elapsed,
                includeTextureSnapshot: true,
                stateBefore: stateBefore,
                stateAfter: stateAfter,
                exitContext: exitContext,
                fileDescriptorAddress: (nint)fileDescriptor);
        }
        return result;
    }

    private byte FileDescriptorReadDetour(FileDescriptor* self, byte* outputBuffer, ulong length, ulong start, byte resetPosition)
    {
        nint activeHandleAddress = _activeTextureHandle;
        bool capture = CaptureEnabled && self != null && activeHandleAddress != 0
            && _activeTextureFileDescriptor == (nint)self;
        ResourceHandle* handle = capture ? (ResourceHandle*)activeHandleAddress : null;
        if (capture && !IsTextureHandle(handle))
            capture = false;

        ReadCaptureContext context = default;
        NativeTextureStateSnapshot stateBefore = default;
        if (capture)
        {
            try
            {
                context = CaptureReadContext(handle, self);
                stateBefore = CaptureTextureState(handle);
            }
            catch { Interlocked.Increment(ref _captureErrorCount); }
        }

        long started = capture ? Stopwatch.GetTimestamp() : 0;
        byte result = _fileDescriptorReadOriginal!(self, outputBuffer, length, start, resetPosition);
        double elapsed = capture ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0d;

        if (capture)
        {
            NativeTextureStateSnapshot stateAfter = default;
            ReadCaptureContext exitContext = context;
            NativeTextureHeaderLayout? headerLayout = null;
            bool outputMatchesHeader = false;
            nint outputOffsetFromHandle = 0;
            try
            {
                stateAfter = CaptureTextureState(handle);
                exitContext = CaptureReadContext(handle, self);

                // The .tex header is 0x50 bytes and lives at TextureResourceHandle.Header.
                // Capture the two header offset tables only when the native read output points
                // exactly at that field and the read covered the complete header.
                if (result != 0 && outputBuffer != null && length == 0x50 && start == 0)
                {
                    var textureHandle = (TextureResourceHandle*)handle;
                    byte* headerAddress = (byte*)&textureHandle->Header;
                    if (outputBuffer == headerAddress)
                    {
                        outputMatchesHeader = true;
                        outputOffsetFromHandle = (nint)outputBuffer - (nint)handle;
                        headerLayout = CaptureTextureHeaderLayout(textureHandle);
                    }
                }

                // A successful large read at or after the 80-byte header is the candidate
                // payload buffer. Keep only its numeric address, length and offset in the
                // enclosing same-thread FileJob scope for InitializeContents correlation.
                if (result != 0 && outputBuffer != null && length >= 1024UL * 1024UL
                    && _activeTexturePayloadHandle == (nint)handle
                    && _activeTexturePayloadDescriptor == (nint)self)
                {
                    _activeTexturePayloadBuffer = (nint)outputBuffer;
                    _activeTexturePayloadLength = length;
                    _activeTexturePayloadOffset = start;
                }
            }
            catch { Interlocked.Increment(ref _captureErrorCount); }

            TryQueueResourceEvent("FileDescriptor.Read", handle, context, resetPosition, 0, result, elapsed,
                includeTextureSnapshot: headerLayout.HasValue,
                stateBefore: stateBefore,
                stateAfter: stateAfter,
                exitContext: exitContext,
                fileDescriptorAddress: (nint)self,
                ioOutputBufferAddress: (nint)outputBuffer,
                ioLength: length,
                ioStart: start,
                ioResetPosition: resetPosition,
                outputMatchesTextureHeader: outputMatchesHeader,
                outputOffsetFromHandle: outputOffsetFromHandle,
                textureHeaderLayout: headerLayout);
        }

        return result;
    }

    private byte TextureInitializeContentsDetour(Texture* self, void* contents)
    {
        nint activeHandleAddress = _activeTextureHandle;
        bool capture = CaptureEnabled && self != null && activeHandleAddress != 0
            && _activeTextureFileDescriptor != 0;
        ResourceHandle* handle = capture ? (ResourceHandle*)activeHandleAddress : null;
        if (capture && !IsTextureHandle(handle))
            capture = false;

        NativeTextureObjectSnapshot textureBefore = default;
        NativeTextureStateSnapshot resourceBefore = default;
        ReadCaptureContext context = default;
        nint fileDescriptorAddress = 0;
        bool matchesHandleTexture = false;
        NativeTextureHeaderLayout? headerLayoutBefore = null;
        nint payloadBufferAddress = 0;
        ulong payloadLength = 0;
        ulong payloadOffset = 0;
        bool contentsMatchesPayload = false;
        if (capture)
        {
            try
            {
                var textureHandle = (TextureResourceHandle*)handle;
                fileDescriptorAddress = _activeTextureFileDescriptor;
                var descriptor = (FileDescriptor*)fileDescriptorAddress;
                if (descriptor->ResourceHandle != handle
                    || (descriptor->FileMode != FileMode.LoadUnpackedResource && descriptor->FileMode != FileMode.LoadSqPackResource))
                {
                    capture = false;
                }
                else
                {
                    context = CaptureReadContext(handle, descriptor);
                    resourceBefore = CaptureTextureState(handle);
                    textureBefore = CaptureTextureObject(self);
                    headerLayoutBefore = CaptureTextureHeaderLayout(textureHandle);
                    matchesHandleTexture = textureHandle->TextureWhileLoading == self || textureHandle->Texture == self;

                    if (_activeTexturePayloadHandle == activeHandleAddress
                        && _activeTexturePayloadDescriptor == fileDescriptorAddress)
                    {
                        payloadBufferAddress = _activeTexturePayloadBuffer;
                        payloadLength = _activeTexturePayloadLength;
                        payloadOffset = _activeTexturePayloadOffset;
                        contentsMatchesPayload = payloadBufferAddress != 0
                            && (nint)contents == payloadBufferAddress;
                    }
                }
            }
            catch
            {
                Interlocked.Increment(ref _captureErrorCount);
                capture = false;
            }
        }

        long started = capture ? Stopwatch.GetTimestamp() : 0;
        nint previousInitializeObject = _activeTextureInitializeObject;
        uint previousInitializeResourceId = _activeTextureInitializeResourceId;
        nint previousInitializePayload = _activeTextureInitializePayloadBuffer;
        ulong previousInitializePayloadLength = _activeTextureInitializePayloadLength;
        List<NativeD3D11TextureCreateSnapshot>? previousCreateSnapshots = _activeD3D11TextureCreateSnapshots;
        List<NativeD3D11ShaderResourceViewSnapshot>? previousViewSnapshots = _activeD3D11ShaderResourceViewSnapshots;
        string? previousInitializeResourceFileName = _activeTextureInitializeResourceFileName;
        List<NativeD3D11TextureCreateSnapshot>? createSnapshots = capture && _d3d11CreateTexture2DHook != null
            ? new List<NativeD3D11TextureCreateSnapshot>(2)
            : null;
        List<NativeD3D11ShaderResourceViewSnapshot>? viewSnapshots = capture && _d3d11CreateShaderResourceViewHook != null
            ? new List<NativeD3D11ShaderResourceViewSnapshot>(2)
            : null;

        if (capture)
        {
            _activeTextureInitializeObject = (nint)self;
            _activeTextureInitializeResourceId = handle->Id;
            _activeTextureInitializePayloadBuffer = payloadBufferAddress;
            _activeTextureInitializePayloadLength = payloadLength;
            _activeD3D11TextureCreateSnapshots = createSnapshots;
            _activeD3D11ShaderResourceViewSnapshots = viewSnapshots;
            _activeTextureInitializeResourceFileName = context.ResourceFileName;
        }

        byte result;
        try
        {
            result = _textureInitializeContentsOriginal!(self, contents);
        }
        finally
        {
            if (capture)
            {
                _activeTextureInitializeObject = previousInitializeObject;
                _activeTextureInitializeResourceId = previousInitializeResourceId;
                _activeTextureInitializePayloadBuffer = previousInitializePayload;
                _activeTextureInitializePayloadLength = previousInitializePayloadLength;
                _activeD3D11TextureCreateSnapshots = previousCreateSnapshots;
                _activeD3D11ShaderResourceViewSnapshots = previousViewSnapshots;
                _activeTextureInitializeResourceFileName = previousInitializeResourceFileName;
            }
        }
        double elapsed = capture ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0d;

        if (capture)
        {
            try
            {
                NativeTextureObjectSnapshot textureAfter = CaptureTextureObject(self);
                NativeTextureStateSnapshot resourceAfter = CaptureTextureState(handle);
                NativeTextureHeaderLayout? headerLayoutAfter = CaptureTextureHeaderLayout((TextureResourceHandle*)handle);
                NativeD3D11TextureCreateSnapshot[] capturedCreateCalls =
                    createSnapshots?.ToArray() ?? Array.Empty<NativeD3D11TextureCreateSnapshot>();
                NativeD3D11ShaderResourceViewSnapshot[] capturedViewCalls =
                    viewSnapshots?.ToArray() ?? Array.Empty<NativeD3D11ShaderResourceViewSnapshot>();
                NativeD3D11ResourceRelationshipSnapshot resourceRelationship =
                    CaptureD3D11ResourceRelationship(textureAfter, capturedCreateCalls, capturedViewCalls);

                var initialization = new NativeTextureInitializeContentsSnapshot(
                    (nint)self,
                    (nint)contents,
                    payloadBufferAddress,
                    payloadLength,
                    payloadOffset,
                    contentsMatchesPayload,
                    payloadBufferAddress == 0 ? 0 : (nint)contents - payloadBufferAddress,
                    matchesHandleTexture,
                    textureBefore,
                    textureAfter,
                    capturedCreateCalls,
                    capturedViewCalls,
                    resourceRelationship);

                TryQueueResourceEvent("Texture.InitializeContents", handle, context, 0, 0, result, elapsed,
                    includeTextureSnapshot: true,
                    stateBefore: resourceBefore,
                    stateAfter: resourceAfter,
                    exitContext: context,
                    fileDescriptorAddress: fileDescriptorAddress,
                    textureHeaderLayout: headerLayoutBefore,
                    textureHeaderLayoutAfter: headerLayoutAfter,
                    textureInitialization: initialization);
            }
            catch { Interlocked.Increment(ref _captureErrorCount); }
        }

        return result;
    }

    private int D3D11CreateTexture2DDetour(void* self, NativeD3D11Texture2DDescription* description,
        NativeD3D11SubresourceData* initialData, void** texture2D)
    {
        NativeD3D11TextureCreateSnapshot? captured = null;
        NativeTextureMipPayloadCapture? progressivePayloadCapture = null;
        int progressiveCaptureGeneration = Volatile.Read(ref _nativeMipPayloadCaptureGeneration);
        List<NativeD3D11TextureCreateSnapshot>? snapshots = _activeD3D11TextureCreateSnapshots;

        // This memory copy is deliberately one-shot and opt-in. It runs only for a compatible
        // 2D BC7 immutable texture inside the active, correlated Texture.InitializeContents
        // call. No native pointer is retained after this detour returns.
        if (CaptureEnabled && Volatile.Read(ref _nativeMipPayloadCaptureArmed) != 0)
        {
            try
            {
                long targetResourceId = Interlocked.Read(ref _nativeMipPayloadCaptureExpectedResourceId);
                uint activeResourceId = _activeTextureInitializeResourceId;
                if (targetResourceId >= 0 && activeResourceId != checked((uint)targetResourceId))
                {
                    Volatile.Write(ref _nativeMipPayloadCaptureStatus,
                        $"Waiting for selected resource ID {targetResourceId}; skipped compatible resource ID {activeResourceId}.");
                }
                else if (TryPrepareNativeMipPayloadSources(description, initialData, out NativeMipPayloadSource[]? sources, out string? rejection))
                {
                    bool claimedCapture = false;
                    lock (_nativeMipPayloadCaptureSync)
                    {
                        // Only a CreateTexture2D callback that started in the currently armed
                        // generation may claim the one-shot. This prevents an older in-flight
                        // callback from stealing a capture armed moments later.
                        if (CaptureEnabled
                            && progressiveCaptureGeneration == Volatile.Read(ref _nativeMipPayloadCaptureGeneration)
                            && Volatile.Read(ref _nativeMipPayloadCaptureArmed) != 0)
                        {
                            Volatile.Write(ref _nativeMipPayloadCaptureArmed, 0);
                            claimedCapture = true;
                        }
                    }

                    if (claimedCapture)
                    {
                        try
                        {
                            progressivePayloadCapture = CopyNativeMipPayloadSources(sources!, activeResourceId);
                            Volatile.Write(ref _nativeMipPayloadCaptureStatus,
                                $"Copied {progressivePayloadCapture.TotalCopiedBytes:N0} bytes from resource ID {progressivePayloadCapture.ResourceId} ({progressivePayloadCapture.Mips.Length} mips). Waiting for the UI thread to create the shadow preview.");
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref _captureErrorCount);
                            Volatile.Write(ref _nativeMipPayloadCaptureStatus, $"Native mip payload copy failed: {ex.Message}");
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(rejection))
                {
                    Volatile.Write(ref _nativeMipPayloadCaptureStatus, $"Waiting for a compatible texture. Last candidate skipped: {rejection}");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _captureErrorCount);
                Volatile.Write(ref _nativeMipPayloadCaptureStatus, $"Native mip capture validation failed: {ex.Message}");
            }
        }

        // Existing diagnostics remain metadata-only unless the one-shot capture above is armed.
        if (CaptureEnabled && snapshots != null && snapshots.Count < MaximumD3D11CreateCallsPerInitialization
            && description != null && _activeTextureInitializeObject != 0)
        {
            try
            {
                var activeTexture = (Texture*)_activeTextureInitializeObject;
                bool dimensionsMatch = description->Width == activeTexture->ActualWidth
                    && description->Height == activeTexture->ActualHeight;
                bool mipAndArrayMatch = description->MipLevels == activeTexture->MipLevel
                    && description->ArraySize == activeTexture->ArraySize;
                if (dimensionsMatch && mipAndArrayMatch)
                {
                    ulong subresourceCount = (ulong)description->MipLevels * description->ArraySize;
                    bool subresourceMetadataCaptured = initialData != null
                        && subresourceCount > 0 && subresourceCount <= MaximumD3D11SubresourcesToCapture;
                    var subresources = subresourceMetadataCaptured
                        ? new NativeD3D11SubresourceSnapshot[checked((int)subresourceCount)]
                        : Array.Empty<NativeD3D11SubresourceSnapshot>();

                    if (subresourceMetadataCaptured)
                    {
                        for (int i = 0; i < subresources.Length; i++)
                        {
                            NativeD3D11SubresourceData data = initialData[i];
                            nint dataAddress = data.SystemMemoryAddress;
                            nint payloadBase = _activeTextureInitializePayloadBuffer;
                            nint offset = payloadBase == 0 ? 0 : unchecked((nint)((nuint)dataAddress - (nuint)payloadBase));
                            bool withinPayload = payloadBase != 0 && dataAddress != 0
                                && (nuint)dataAddress >= (nuint)payloadBase
                                && (ulong)((nuint)dataAddress - (nuint)payloadBase) < _activeTextureInitializePayloadLength;

                            uint mipIndex = (uint)(i % (int)description->MipLevels);
                            uint arraySlice = (uint)(i / (int)description->MipLevels);
                            uint mipWidth = Math.Max(1u, description->Width >> (int)mipIndex);
                            uint mipHeight = Math.Max(1u, description->Height >> (int)mipIndex);
                            ulong expectedRowPitch = 0;
                            ulong expectedSliceBytes = 0;
                            if (description->Format is 98 or 99) // BC7: 16-byte 4x4 blocks.
                            {
                                ulong blocksWide = Math.Max(1u, (mipWidth + 3u) / 4u);
                                ulong blockRows = Math.Max(1u, (mipHeight + 3u) / 4u);
                                expectedRowPitch = blocksWide * 16u;
                                expectedSliceBytes = expectedRowPitch * blockRows;
                            }

                            subresources[i] = new NativeD3D11SubresourceSnapshot(
                                i, (int)mipIndex, (int)arraySlice, mipWidth, mipHeight,
                                dataAddress, offset, withinPayload, data.SystemMemoryPitch, data.SystemMemorySlicePitch,
                                expectedRowPitch, expectedSliceBytes);
                        }
                    }

                    captured = new NativeD3D11TextureCreateSnapshot(
                        0,
                        (nint)self,
                        0,
                        description->Width,
                        description->Height,
                        description->MipLevels,
                        description->ArraySize,
                        description->Format,
                        description->SampleDescription.Count,
                        description->SampleDescription.Quality,
                        description->Usage,
                        description->BindFlags,
                        description->CPUAccessFlags,
                        description->MiscFlags,
                        (nint)initialData,
                        initialData == null,
                        _activeTextureInitializePayloadBuffer,
                        _activeTextureInitializePayloadLength,
                        subresourceMetadataCaptured,
                        subresources);
                }
            }
            catch
            {
                Interlocked.Increment(ref _captureErrorCount);
            }
        }

        int hresult = _d3d11CreateTexture2DOriginal!(self, description, initialData, texture2D);
        if (captured is { } snapshot)
        {
            try
            {
                nint createdTexture = hresult >= 0 && texture2D != null ? (nint)(*texture2D) : 0;
                snapshots!.Add(snapshot with { HResult = hresult, CreatedTextureAddress = createdTexture });
            }
            catch
            {
                Interlocked.Increment(ref _captureErrorCount);
            }
        }

        if (progressivePayloadCapture != null)
        {
            lock (_nativeMipPayloadCaptureSync)
            {
                // Stop/Cancel may race a native callback that already copied the source payload.
                // Publish only if the capture generation is still current and capture remains enabled.
                if (progressiveCaptureGeneration != Volatile.Read(ref _nativeMipPayloadCaptureGeneration) || !CaptureEnabled)
                {
                    Volatile.Write(ref _nativeMipPayloadCaptureStatus, "The in-flight native mip copy was discarded because capture was stopped or cancelled.");
                }
                else if (hresult >= 0)
                {
                    NativeTextureMipPayloadCapture? previous = Interlocked.CompareExchange(
                        ref _pendingNativeMipPayloadCapture, progressivePayloadCapture, null);
                    if (previous == null)
                    {
                        Volatile.Write(ref _nativeMipPayloadCaptureStatus,
                            $"Captured {progressivePayloadCapture.Width}×{progressivePayloadCapture.Height} BC7 with {progressivePayloadCapture.Mips.Length} mips ({progressivePayloadCapture.TotalCopiedBytes:N0} bytes). Ready to build a plugin-owned shadow texture.");
                    }
                    else
                    {
                        Volatile.Write(ref _nativeMipPayloadCaptureStatus,
                            "The native texture was copied, but another payload was already pending; this copy was discarded.");
                    }
                }
                else
                {
                    Volatile.Write(ref _nativeMipPayloadCaptureStatus,
                        $"The original game CreateTexture2D call failed (HRESULT 0x{hresult:X8}); the shadow capture was discarded.");
                }
            }
        }

        return hresult;
    }

    private bool TryPrepareNativeMipPayloadSources(
        NativeD3D11Texture2DDescription* description,
        NativeD3D11SubresourceData* initialData,
        out NativeMipPayloadSource[]? sources,
        out string? rejection)
    {
        sources = null;
        rejection = null;
        if (description == null || initialData == null || _activeTextureInitializeObject == 0)
            return false;

        var activeTexture = (Texture*)_activeTextureInitializeObject;
        if (description->Width != activeTexture->ActualWidth || description->Height != activeTexture->ActualHeight
            || description->MipLevels != activeTexture->MipLevel || description->ArraySize != 1)
            return false;

        if (description->Format != 98)
        {
            rejection = $"DXGI format {description->Format} is not the supported BC7_UNORM format 98.";
            return false;
        }
        if (description->Usage != 1 || (description->BindFlags & 0x8u) == 0
            || description->CPUAccessFlags != 0 || description->MiscFlags != 0
            || description->SampleDescription.Count != 1)
        {
            rejection = "The native resource is not the expected single-sample immutable shader-resource texture.";
            return false;
        }
        if (description->Width == 0 || description->Height == 0 || description->MipLevels == 0 || description->MipLevels > 13)
        {
            rejection = "Texture dimensions or mip count are outside the supported BC7 2D range.";
            return false;
        }
        if (_activeTextureInitializePayloadBuffer == 0 || _activeTextureInitializePayloadLength == 0
            || _activeTextureInitializePayloadLength > MaximumNativeMipPayloadCaptureBytes)
        {
            rejection = "The correlated native payload buffer is missing or exceeds the 64 MiB capture limit.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(_activeTextureInitializeResourceFileName))
        {
            rejection = "The active resource filename is unavailable.";
            return false;
        }

        int mipCount = checked((int)description->MipLevels);
        var result = new NativeMipPayloadSource[mipCount];
        nuint payloadBase = (nuint)_activeTextureInitializePayloadBuffer;
        ulong expectedPayloadOffset = 0;
        for (int mipIndex = 0; mipIndex < mipCount; mipIndex++)
        {
            NativeD3D11SubresourceData data = initialData[mipIndex];
            uint mipWidth = Math.Max(1u, description->Width >> mipIndex);
            uint mipHeight = Math.Max(1u, description->Height >> mipIndex);
            ulong expectedRowPitch = Math.Max(1u, (mipWidth + 3u) / 4u) * 16u;
            ulong expectedSlicePitch = expectedRowPitch * Math.Max(1u, (mipHeight + 3u) / 4u);

            if (data.SystemMemoryAddress == 0)
            {
                rejection = $"Mip {mipIndex} has a null source pointer.";
                return false;
            }
            if (data.SystemMemoryPitch != expectedRowPitch || data.SystemMemorySlicePitch != expectedSlicePitch)
            {
                rejection = $"Mip {mipIndex} row/slice pitch does not match the expected BC7 layout.";
                return false;
            }

            nuint sourceAddress = (nuint)data.SystemMemoryAddress;
            if (sourceAddress < payloadBase)
            {
                rejection = $"Mip {mipIndex} source pointer precedes the correlated payload buffer.";
                return false;
            }
            ulong sourceOffset = (ulong)(sourceAddress - payloadBase);
            if (sourceOffset != expectedPayloadOffset)
            {
                rejection = $"Mip {mipIndex} is not at the expected contiguous payload offset (expected 0x{expectedPayloadOffset:X}, observed 0x{sourceOffset:X}).";
                return false;
            }
            if (sourceOffset > _activeTextureInitializePayloadLength
                || expectedSlicePitch > _activeTextureInitializePayloadLength - sourceOffset)
            {
                rejection = $"Mip {mipIndex} extends beyond the correlated payload buffer.";
                return false;
            }

            result[mipIndex] = new NativeMipPayloadSource(
                data.SystemMemoryAddress,
                checked((uint)expectedRowPitch),
                checked((uint)expectedSlicePitch),
                mipWidth,
                mipHeight,
                sourceOffset);
            expectedPayloadOffset = checked(expectedPayloadOffset + expectedSlicePitch);
        }

        if (expectedPayloadOffset > MaximumNativeMipPayloadCaptureBytes)
        {
            rejection = "The calculated mip payload exceeds the 64 MiB capture limit.";
            return false;
        }

        sources = result;
        return true;
    }

    private NativeTextureMipPayloadCapture CopyNativeMipPayloadSources(NativeMipPayloadSource[] sources, uint resourceId)
    {
        var copyTimer = Stopwatch.StartNew();
        var mips = new NativeTextureMipPayload[sources.Length];
        for (int i = 0; i < sources.Length; i++)
        {
            NativeMipPayloadSource source = sources[i];
            int byteCount = checked((int)source.SlicePitch);
            byte[] data = GC.AllocateUninitializedArray<byte>(byteCount);
            Marshal.Copy(source.SourceAddress, data, 0, byteCount);
            mips[i] = new NativeTextureMipPayload(
                i,
                source.Width,
                source.Height,
                source.RowPitch,
                source.SlicePitch,
                source.PayloadOffset,
                data);
        }
        copyTimer.Stop();

        return new NativeTextureMipPayloadCapture(
            DateTime.UtcNow,
            resourceId,
            _activeTextureInitializeResourceFileName ?? string.Empty,
            ((Texture*)_activeTextureInitializeObject)->ActualWidth,
            ((Texture*)_activeTextureInitializeObject)->ActualHeight,
            98,
            _activeTextureInitializePayloadLength,
            copyTimer.Elapsed.TotalMilliseconds,
            mips);
    }

    private readonly record struct NativeMipPayloadSource(
        nint SourceAddress,
        uint RowPitch,
        uint SlicePitch,
        uint Width,
        uint Height,
        ulong PayloadOffset);

    private int D3D11CreateShaderResourceViewDetour(void* self, void* resource,
        NativeD3D11ShaderResourceViewDescription* description, void** shaderResourceView)
    {
        List<NativeD3D11ShaderResourceViewSnapshot>? snapshots = _activeD3D11ShaderResourceViewSnapshots;
        bool capture = CaptureEnabled && snapshots != null
            && snapshots.Count < MaximumD3D11ViewCallsPerInitialization
            && resource != null && _activeTextureInitializeObject != 0;

        NativeD3D11ShaderResourceViewDescription inputDescription = default;
        bool inputDescriptionWasNull = description == null;
        if (capture)
        {
            try
            {
                bool matchesCreatedTexture = false;
                List<NativeD3D11TextureCreateSnapshot>? creates = _activeD3D11TextureCreateSnapshots;
                if (creates != null)
                {
                    for (int i = 0; i < creates.Count; i++)
                    {
                        if (creates[i].CreatedTextureAddress == (nint)resource && (nint)resource != 0)
                        {
                            matchesCreatedTexture = true;
                            break;
                        }
                    }
                }
                capture = matchesCreatedTexture;
                if (capture && description != null)
                    inputDescription = *description;
            }
            catch
            {
                Interlocked.Increment(ref _captureErrorCount);
                capture = false;
            }
        }

        int hresult = _d3d11CreateShaderResourceViewOriginal!(self, resource, description, shaderResourceView);
        if (capture)
        {
            try
            {
                nint createdView = hresult >= 0 && shaderResourceView != null ? (nint)(*shaderResourceView) : 0;
                NativeD3D11ShaderResourceViewDescription actualDescription = default;
                bool actualDescriptionCaptured = TryGetShaderResourceViewDescription(createdView, out actualDescription);
                snapshots!.Add(new NativeD3D11ShaderResourceViewSnapshot(
                    hresult, (nint)self, (nint)resource, createdView, inputDescriptionWasNull, ToSnapshot(inputDescription),
                    actualDescriptionCaptured, ToSnapshot(actualDescription)));
            }
            catch
            {
                Interlocked.Increment(ref _captureErrorCount);
            }
        }

        return hresult;
    }

    private static NativeD3D11ShaderResourceViewDescriptionSnapshot ToSnapshot(NativeD3D11ShaderResourceViewDescription description) =>
        new(description.Format, description.ViewDimension, description.MipMostDetailedOrFirstElement,
            description.MipLevelsOrNumElements, description.FirstArraySliceOrFlags, description.ArraySizeOrNumElements);

    private static bool TryGetShaderResourceViewDescription(nint shaderResourceView, out NativeD3D11ShaderResourceViewDescription description)
    {
        description = default;
        if (shaderResourceView == 0)
            return false;

        try
        {
            nint* vtable = *(nint**)shaderResourceView;
            // IUnknown (0-2), ID3D11DeviceChild (3-6), ID3D11View::GetResource (7),
            // ID3D11ShaderResourceView::GetDesc (8). This is a read-only native COM method.
            if (vtable == null || vtable[8] == 0)
                return false;

            var getDesc = (delegate* unmanaged[Stdcall]<void*, NativeD3D11ShaderResourceViewDescription*, void>)vtable[8];
            // Use a stack local rather than taking the address of the managed out parameter.
            NativeD3D11ShaderResourceViewDescription localDescription = default;
            getDesc((void*)shaderResourceView, &localDescription);
            description = localDescription;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Cross-checks the persistent Texture fields against the D3D11 objects created during
    /// InitializeContents. GetResource/GetDevice return temporary COM references; each is
    /// released before this method returns. Diagnostics retain copied numeric addresses only;
    /// no game-owned COM reference is retained or modified.
    /// </summary>
    private static NativeD3D11ResourceRelationshipSnapshot CaptureD3D11ResourceRelationship(
        NativeTextureObjectSnapshot texture,
        NativeD3D11TextureCreateSnapshot[] createCalls,
        NativeD3D11ShaderResourceViewSnapshot[] viewCalls)
    {
        nint textureAddress = texture.D3D11TextureAddress;
        nint viewAddress = texture.D3D11ShaderResourceViewAddress;
        if (textureAddress == 0 && viewAddress == 0)
            return NativeD3D11ResourceRelationshipSnapshot.NotAvailable("Texture has no persistent D3D11 texture or SRV pointer after initialization.");

        NativeD3D11TextureCreateSnapshot? matchingCreate = null;
        for (int i = 0; i < createCalls.Length; i++)
        {
            if (createCalls[i].HResult >= 0 && createCalls[i].CreatedTextureAddress != 0
                && createCalls[i].CreatedTextureAddress == textureAddress)
            {
                matchingCreate = createCalls[i];
                break;
            }
        }

        NativeD3D11ShaderResourceViewSnapshot? matchingView = null;
        for (int i = 0; i < viewCalls.Length; i++)
        {
            if (viewCalls[i].HResult >= 0 && viewCalls[i].CreatedViewAddress != 0
                && viewCalls[i].CreatedViewAddress == viewAddress)
            {
                matchingView = viewCalls[i];
                break;
            }
        }

        nint resourceFromView = 0;
        nint textureDevice = 0;
        nint viewDevice = 0;
        try
        {
            bool viewGetResourceSucceeded = viewAddress != 0
                && TryGetViewResource(viewAddress, out resourceFromView);
            bool viewResourceIdentityMatchesTexture = false;
            bool viewResourceIdentityCheckSucceeded = viewGetResourceSucceeded && textureAddress != 0
                && TryCompareComIdentity(resourceFromView, textureAddress, out viewResourceIdentityMatchesTexture);

            bool textureGetDeviceSucceeded = textureAddress != 0
                && TryGetDevice(textureAddress, out textureDevice);
            bool viewGetDeviceSucceeded = viewAddress != 0
                && TryGetDevice(viewAddress, out viewDevice);

            bool textureAndViewShareDevice = false;
            bool textureViewDeviceIdentityCheckSucceeded = textureGetDeviceSucceeded && viewGetDeviceSucceeded
                && TryCompareComIdentity(textureDevice, viewDevice, out textureAndViewShareDevice);

            bool textureSharesCreateTextureDevice = false;
            bool createTextureDeviceIdentityCheckSucceeded = matchingCreate != null && textureGetDeviceSucceeded
                && matchingCreate.DeviceAddress != 0
                && TryCompareComIdentity(textureDevice, matchingCreate.DeviceAddress, out textureSharesCreateTextureDevice);

            bool viewSharesCreateViewDevice = false;
            bool createViewDeviceIdentityCheckSucceeded = matchingView != null && viewGetDeviceSucceeded
                && matchingView.DeviceAddress != 0
                && TryCompareComIdentity(viewDevice, matchingView.DeviceAddress, out viewSharesCreateViewDevice);

            bool viewCreateResourcePointerMatchesTexture = matchingView != null && textureAddress != 0
                && matchingView.ResourceAddress == textureAddress;
            bool texturePointerMatchesCreateResult = matchingCreate != null;
            bool viewPointerMatchesCreateResult = matchingView != null;

            var status = new List<string>(6);
            status.Add(texturePointerMatchesCreateResult
                ? "Persistent Texture.D3D11Texture2D matches the successful CreateTexture2D output"
                : "No matching successful CreateTexture2D output was captured");
            status.Add(viewPointerMatchesCreateResult
                ? "Persistent Texture.D3D11ShaderResourceView matches the successful CreateShaderResourceView output"
                : "No matching successful CreateShaderResourceView output was captured");
            status.Add(viewGetResourceSucceeded
                ? viewResourceIdentityCheckSucceeded
                    ? viewResourceIdentityMatchesTexture
                        ? "SRV.GetResource resolves to the same COM resource as Texture.D3D11Texture2D"
                        : "SRV.GetResource resolves to a different COM resource than Texture.D3D11Texture2D"
                    : "SRV.GetResource succeeded, but COM identity comparison was unavailable"
                : "SRV.GetResource was unavailable");
            status.Add(textureViewDeviceIdentityCheckSucceeded
                ? textureAndViewShareDevice
                    ? "Texture and SRV report the same D3D11 device"
                    : "Texture and SRV report different D3D11 devices"
                : "Texture/SRV device identity comparison was unavailable");
            if (createTextureDeviceIdentityCheckSucceeded)
                status.Add(textureSharesCreateTextureDevice
                    ? "Texture device matches the CreateTexture2D device"
                    : "Texture device differs from the CreateTexture2D device");
            if (createViewDeviceIdentityCheckSucceeded)
                status.Add(viewSharesCreateViewDevice
                    ? "SRV device matches the CreateShaderResourceView device"
                    : "SRV device differs from the CreateShaderResourceView device");

            return new NativeD3D11ResourceRelationshipSnapshot(
                true,
                textureAddress != 0,
                viewAddress != 0,
                texturePointerMatchesCreateResult,
                viewPointerMatchesCreateResult,
                viewCreateResourcePointerMatchesTexture,
                viewGetResourceSucceeded,
                resourceFromView,
                viewResourceIdentityCheckSucceeded,
                viewResourceIdentityMatchesTexture,
                textureGetDeviceSucceeded,
                textureDevice,
                viewGetDeviceSucceeded,
                viewDevice,
                textureViewDeviceIdentityCheckSucceeded,
                textureAndViewShareDevice,
                createTextureDeviceIdentityCheckSucceeded,
                textureSharesCreateTextureDevice,
                createViewDeviceIdentityCheckSucceeded,
                viewSharesCreateViewDevice,
                string.Join("; ", status));
        }
        finally
        {
            if (viewDevice != 0)
                Marshal.Release(viewDevice);
            if (textureDevice != 0)
                Marshal.Release(textureDevice);
            if (resourceFromView != 0)
                Marshal.Release(resourceFromView);
        }
    }

    private static bool TryGetViewResource(nint shaderResourceView, out nint resource)
    {
        resource = 0;
        try
        {
            nint* vtable = *(nint**)shaderResourceView;
            if (vtable == null || vtable[7] == 0)
                return false;

            // ID3D11View::GetResource is ID3D11DeviceChild's inherited vtable slot 7.
            var getResource = (delegate* unmanaged[Stdcall]<void*, void**, void>)vtable[7];
            void* output = null;
            getResource((void*)shaderResourceView, &output);
            resource = (nint)output; // GetResource returns an owned reference.
            return resource != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetDevice(nint deviceChild, out nint device)
    {
        device = 0;
        try
        {
            nint* vtable = *(nint**)deviceChild;
            if (vtable == null || vtable[3] == 0)
                return false;

            // ID3D11DeviceChild::GetDevice is inherited vtable slot 3 and AddRefs the result.
            var getDevice = (delegate* unmanaged[Stdcall]<void*, void**, void>)vtable[3];
            void* output = null;
            getDevice((void*)deviceChild, &output);
            device = (nint)output;
            return device != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCompareComIdentity(nint left, nint right, out bool sameIdentity)
    {
        sameIdentity = false;
        if (left == 0 || right == 0)
            return false;

        nint leftIdentity = 0;
        nint rightIdentity = 0;
        try
        {
            // IUnknown identity is canonical across interfaces implemented by one COM object.
            Guid iidIUnknown = new("00000000-0000-0000-C000-000000000046");
            int leftResult = Marshal.QueryInterface(left, in iidIUnknown, out leftIdentity);
            int rightResult = Marshal.QueryInterface(right, in iidIUnknown, out rightIdentity);
            if (leftResult < 0 || rightResult < 0 || leftIdentity == 0 || rightIdentity == 0)
                return false;

            sameIdentity = leftIdentity == rightIdentity;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (rightIdentity != 0)
                Marshal.Release(rightIdentity);
            if (leftIdentity != 0)
                Marshal.Release(leftIdentity);
        }
    }

    private bool CaptureTextureFileJob(FileDescriptor* fileDescriptor, out ResourceHandle* handle, out ReadCaptureContext context)
    {
        handle = null;
        context = default;
        if (!CaptureEnabled || fileDescriptor == null)
            return false;

        try
        {
            // FileDescriptor.ResourceHandle is valid for the resource-loading modes only.
            // Do not interpret the union for unrelated file operations.
            FileMode mode = fileDescriptor->FileMode;
            if (mode != FileMode.LoadUnpackedResource && mode != FileMode.LoadSqPackResource)
                return false;

            ResourceHandle* candidate = fileDescriptor->ResourceHandle;
            if (!IsTextureHandle(candidate))
                return false;

            context = CaptureReadContext(candidate, fileDescriptor);
            handle = candidate;
            return true;
        }
        catch
        {
            Interlocked.Increment(ref _captureErrorCount);
            return false;
        }
    }

    private void TryQueueResourceEvent(
        string operation,
        ResourceHandle* self,
        ReadCaptureContext context,
        byte inputFlag,
        byte secondaryInput,
        byte result,
        double elapsedMilliseconds,
        int priority = 0,
        bool includeTextureSnapshot = false,
        NativeTextureStateSnapshot stateBefore = default,
        NativeTextureStateSnapshot stateAfter = default,
        ReadCaptureContext? exitContext = null,
        nint fileDescriptorAddress = 0,
        nint loadContentsAddress = 0,
        nint textureWhileLoadingAddress = 0,
        nint dataWhileLoadingAddress = 0,
        nint ioOutputBufferAddress = 0,
        ulong ioLength = 0,
        ulong ioStart = 0,
        byte ioResetPosition = 0,
        bool loadContentsMatchesDescriptor = false,
        byte loadDescriptorFileModeValue = byte.MaxValue,
        nint loadDescriptorFileBufferAddress = 0,
        bool outputMatchesTextureHeader = false,
        nint outputOffsetFromHandle = 0,
        NativeTextureHeaderLayout? textureHeaderLayout = null,
        NativeTextureInitializeContentsSnapshot? textureInitialization = null,
        NativeTextureHeaderLayout? textureHeaderLayoutAfter = null)
    {
        try
        {
            ushort width = 0;
            ushort height = 0;
            ushort depth = 0;
            byte mipCountAndFlag = 0;
            string format = string.Empty;
            nint textureAddress = 0;

            if (includeTextureSnapshot && self != null)
            {
                var textureHandle = (TextureResourceHandle*)self;
                var header = textureHandle->Header;
                width = header.Width;
                height = header.Height;
                depth = header.Depth;
                mipCountAndFlag = header.MipCountAndFlag;
                format = header.Format.ToString();
                textureAddress = (nint)textureHandle->Texture;
            }

            ReadCaptureContext after = exitContext ?? context;
            var item = new NativeTextureLifecycleEvent(
                Interlocked.Increment(ref _nextSequence),
                DateTime.UtcNow,
                operation,
                Environment.CurrentManagedThreadId,
                self == null ? 0 : (nint)self,
                context.ResourceFileName,
                context.FileModeName,
                context.FileModeValue,
                context.FileOffset,
                context.RequestedLength,
                inputFlag,
                result,
                elapsedMilliseconds,
                self == null ? 0 : self->Id,
                self == null ? 0 : self->FileSize,
                self == null ? 0 : self->FileSize2,
                self == null ? 0 : self->FileSize3,
                width,
                height,
                depth,
                mipCountAndFlag,
                format,
                textureAddress,
                priority,
                secondaryInput,
                fileDescriptorAddress,
                after.FileModeName,
                after.FileModeValue,
                after.FileOffset,
                after.RequestedLength,
                stateBefore,
                stateAfter,
                loadContentsAddress,
                textureWhileLoadingAddress,
                dataWhileLoadingAddress,
                context.FileBufferAddress,
                after.FileBufferAddress,
                ioOutputBufferAddress,
                ioLength,
                ioStart,
                ioResetPosition,
                loadContentsMatchesDescriptor,
                loadDescriptorFileModeValue,
                loadDescriptorFileBufferAddress,
                outputMatchesTextureHeader,
                outputOffsetFromHandle,
                textureHeaderLayout,
                textureInitialization,
                textureHeaderLayoutAfter);
            TryEnqueue(item);
        }
        catch
        {
            Interlocked.Increment(ref _captureErrorCount);
        }
    }

    private static NativeTextureHeaderLayout CaptureTextureHeaderLayout(TextureResourceHandle* handle)
    {
        ref TextureResourceHandle.TexFileHeader header = ref handle->Header;
        var rawHeader = new System.Text.StringBuilder(sizeof(TextureResourceHandle.TexFileHeader) * 2);
        byte* headerBytes = (byte*)&handle->Header;
        for (int i = 0; i < sizeof(TextureResourceHandle.TexFileHeader); i++)
            rawHeader.Append(headerBytes[i].ToString("X2"));

        return new NativeTextureHeaderLayout(
            (nint)((byte*)&handle->Header - (byte*)handle),
            sizeof(TextureResourceHandle.TexFileHeader),
            FormatOffsets(header.LodOffsets),
            FormatOffsets(header.OffsetsToSurfaces),
            rawHeader.ToString());
    }

    private static string FormatOffsets(Span<uint> offsets)
    {
        var builder = new System.Text.StringBuilder(offsets.Length * 12);
        for (int i = 0; i < offsets.Length; i++)
        {
            if (i != 0)
                builder.Append(", ");
            builder.Append("0x");
            builder.Append(offsets[i].ToString("X"));
        }
        return builder.ToString();
    }

    private static NativeTextureObjectSnapshot CaptureTextureObject(Texture* texture)
    {
        if (texture == null)
            return default;

        return new NativeTextureObjectSnapshot(
            (nint)texture,
            texture->ActualWidth,
            texture->ActualHeight,
            texture->AllocatedWidth,
            texture->AllocatedHeight,
            texture->Depth,
            texture->MipLevel,
            texture->ArraySize,
            texture->TextureFormat.ToString(),
            texture->Flags.ToString(),
            (nint)texture->D3D11Texture2D,
            (nint)texture->D3D11ShaderResourceView,
            CaptureD3D11TextureDescription((nint)texture->D3D11Texture2D));
    }

    /// <summary>
    /// Reads the D3D11 resource descriptor through a temporary queried interface reference.
    /// The game's original COM reference is never released or modified.
    /// </summary>
    private static string CaptureD3D11TextureDescription(nint textureAddress)
    {
        if (textureAddress == 0)
            return "unavailable (no D3D11 texture yet)";

        nint queriedTexture = 0;
        try
        {
            Guid iid = typeof(ID3D11Texture2D).GUID;
            int queryResult = Marshal.QueryInterface(textureAddress, in iid, out queriedTexture);
            if (queryResult < 0)
                return $"unavailable (QueryInterface HRESULT 0x{queryResult:X8})";
            if (queriedTexture == 0)
                return "unavailable (QueryInterface returned null)";

            // ID3D11Texture2D inherits the ID3D11Resource vtable. GetDesc is
            // the first ID3D11Texture2D-specific method, at COM vtable slot 10:
            // IUnknown (0-2), ID3D11DeviceChild (3-6), ID3D11Resource (7-9).
            // Call the native void GetDesc(D3D11_TEXTURE2D_DESC*) method directly;
            // Vortice's managed ID3D11Texture2D wrapper does not expose GetDescription.
            nint* vtable = *(nint**)queriedTexture;
            if (vtable == null || vtable[10] == 0)
                return "unavailable (ID3D11Texture2D.GetDesc vtable entry is null)";

            NativeD3D11Texture2DDescription desc = default;
            var getDesc = (delegate* unmanaged[Stdcall]<nint, NativeD3D11Texture2DDescription*, void>)vtable[10];
            getDesc(queriedTexture, &desc);

            return $"{desc.Width}x{desc.Height}, mips {desc.MipLevels}, array {desc.ArraySize}, " +
                   $"format {DescribeDxgiFormat(desc.Format)}, usage {DescribeD3D11Usage(desc.Usage)} ({desc.Usage}), " +
                   $"bind 0x{desc.BindFlags:X}, CPU 0x{desc.CPUAccessFlags:X}, misc 0x{desc.MiscFlags:X}, " +
                   $"samples {desc.SampleDescription.Count}/{desc.SampleDescription.Quality}";
        }
        catch (Exception ex)
        {
            return $"unavailable ({ex.GetType().Name}: {ex.Message})";
        }
        finally
        {
            if (queriedTexture != 0)
                Marshal.Release(queriedTexture);
        }
    }

    private static string DescribeDxgiFormat(uint format) => format switch
    {
        98 => "BC7_UNORM (DXGI_FORMAT 98)",
        99 => "BC7_UNORM_SRGB (DXGI_FORMAT 99)",
        _ => $"DXGI_FORMAT {format}",
    };

    private static string DescribeD3D11Usage(uint usage) => usage switch
    {
        0 => "Default",
        1 => "Immutable",
        2 => "Dynamic",
        3 => "Staging",
        _ => "Unknown",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeD3D11SampleDescription
    {
        public uint Count;
        public uint Quality;
    }

    // Native D3D11_TEXTURE2D_DESC layout. All members are 32-bit fields;
    // the structure is 44 bytes with 4-byte alignment on Windows.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeD3D11Texture2DDescription
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public NativeD3D11SampleDescription SampleDescription;
        public uint Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    // Native D3D11_SUBRESOURCE_DATA on x64: pointer + UINT pitch + UINT slice pitch (16 bytes).
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeD3D11SubresourceData
    {
        public nint SystemMemoryAddress;
        public uint SystemMemoryPitch;
        public uint SystemMemorySlicePitch;
    }

    // D3D11_SHADER_RESOURCE_VIEW_DESC is 24 bytes on x64. The final 16 bytes are a union;
    // these generic fields capture the selected view's mip/array range without assuming its dimension.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeD3D11ShaderResourceViewDescription
    {
        public uint Format;
        public uint ViewDimension;
        public uint MipMostDetailedOrFirstElement;
        public uint MipLevelsOrNumElements;
        public uint FirstArraySliceOrFlags;
        public uint ArraySizeOrNumElements;
    }

    private static nint ResolveD3D11CreateTexture2DAddress()
    {
        Device* gameDevice = Device.Instance();
        if (gameDevice == null || gameDevice->D3D11Forwarder == null)
            throw new InvalidOperationException("FFXIV's D3D11Forwarder is unavailable.");

        nint deviceAddress = (nint)gameDevice->D3D11Forwarder;
        nint* vtable = *(nint**)deviceAddress;
        if (vtable == null)
            throw new InvalidOperationException("FFXIV's D3D11Forwarder has a null COM vtable.");

        nint address = vtable[D3D11DeviceCreateTexture2DVtableIndex];
        if (address == 0)
            throw new InvalidOperationException("ID3D11Device.CreateTexture2D vtable entry is null.");
        return address;
    }

    private static nint ResolveD3D11CreateShaderResourceViewAddress()
    {
        Device* gameDevice = Device.Instance();
        if (gameDevice == null || gameDevice->D3D11Forwarder == null)
            throw new InvalidOperationException("FFXIV's D3D11Forwarder is unavailable.");

        nint deviceAddress = (nint)gameDevice->D3D11Forwarder;
        nint* vtable = *(nint**)deviceAddress;
        if (vtable == null)
            throw new InvalidOperationException("FFXIV's D3D11Forwarder has a null COM vtable.");

        nint address = vtable[D3D11DeviceCreateShaderResourceViewVtableIndex];
        if (address == 0)
            throw new InvalidOperationException("ID3D11Device.CreateShaderResourceView vtable entry is null.");
        return address;
    }

    private nint ResolveUniqueTextSignature(string signature, string label)
    {
        nint[] matches = _sigScanner.ScanAllText(signature);
        if (matches.Length != 1)
            throw new InvalidOperationException(
                $"Could not uniquely resolve {label}: signature matched {matches.Length} locations. Native pipeline hooks were not installed.");

        return matches[0];
    }

    private nint ResolveRelativeCallTarget(nint callSite, string label)
    {
        if (callSite == 0 || *(byte*)callSite != 0xE8)
            throw new InvalidOperationException($"The {label} signature did not resolve to an E8 relative CALL instruction.");

        int relativeOffset = *(int*)((byte*)callSite + 1);
        nint target = _sigScanner.ResolveRelativeAddress(callSite + 5, relativeOffset);
        // ScanAllText returns real process addresses even when SigScanner scans a copy.
        // Validate against the real module image, not TextSectionBase (which can be the copy).
        nint textBase = _sigScanner.Module.BaseAddress + (nint)_sigScanner.TextSectionOffset;
        nint textEnd = textBase + _sigScanner.TextSectionSize;
        if (target < textBase || target >= textEnd)
            throw new InvalidOperationException($"The resolved {label} target is outside the game .text section. Native pipeline hooks were not installed.");

        return target;
    }

    private static bool IsTextureHandle(ResourceHandle* handle)
    {
        if (handle == null)
            return false;

        try { return handle->GetHandleSize() == ExpectedTextureHandleSize; }
        catch { return false; }
    }

    private static ReadCaptureContext CaptureReadContext(ResourceHandle* self, FileDescriptor* fileDescriptor)
    {
        string resourceFileName = self == null ? string.Empty : self->FileName.ToString();
        if (fileDescriptor == null)
            return new ReadCaptureContext(resourceFileName, "null", byte.MaxValue, 0, 0, 0);

        FileMode mode = fileDescriptor->FileMode;
        // ResourceHandle.FileName is the reliable source identity observed in live mode-0
        // callbacks. FileDescriptor.FilePathString is a separate union-backed field and
        // produced non-path data in these callbacks, so do not dereference it here.
        return new ReadCaptureContext(
            resourceFileName,
            DescribeFileMode(mode),
            (byte)mode,
            fileDescriptor->StartOffset,
            fileDescriptor->Length,
            (nint)fileDescriptor->FileBuffer);
    }

    private static NativeTextureStateSnapshot CaptureTextureState(ResourceHandle* self)
    {
        if (self == null)
            return default;

        var textureHandle = (TextureResourceHandle*)self;
        return new NativeTextureStateSnapshot(
            (nint)self->Blob,
            (nint)self->GetData(),
            self->GetLength(),
            self->FileSize,
            self->FileSize2,
            self->FileSize3,
            self->FileType,
            self->LastIOResult,
            self->ReadState,
            self->LoadState,
            self->OtherState,
            ((byte*)textureHandle)[0x104],
            (nint)textureHandle->Texture);
    }

    private void TryQueueReadEvent(
        string operation,
        ResourceHandle* self,
        ReadCaptureContext context,
        byte inputFlag,
        byte result,
        double elapsedMilliseconds,
        NativeTextureStateSnapshot stateBefore,
        NativeTextureStateSnapshot stateAfter,
        nint fileDescriptorAddress,
        ReadCaptureContext exitContext)
    {
        TryQueueResourceEvent(operation, self, context, inputFlag, 0, result, elapsedMilliseconds,
            stateBefore: stateBefore,
            stateAfter: stateAfter,
            exitContext: exitContext,
            fileDescriptorAddress: fileDescriptorAddress);
    }

    private void TryEnqueue(NativeTextureLifecycleEvent item)
    {
        int queued = Interlocked.Increment(ref _queuedEventCount);
        if (queued > MaximumQueuedEvents)
        {
            Interlocked.Decrement(ref _queuedEventCount);
            Interlocked.Increment(ref _droppedEventCount);
            return;
        }

        _pendingEvents.Enqueue(item);
        Interlocked.Increment(ref _capturedEventCount);
    }

    private static string DescribeFileMode(FileMode mode) => mode switch
    {
        FileMode.LoadUnpackedResource => "Unpacked disk resource (0)",
        FileMode.LoadSqPackResource => "SqPack archive resource (11)",
        _ => $"Other ({(byte)mode})",
    };

    public void Dispose()
    {
        if (_disposed)
            return;

        StopCapture();
        CancelNativeMipPayloadCapture();
        _readHook?.Dispose();
        _rereadHook?.Dispose();
        _loadHook?.Dispose();
        _fileJobHook?.Dispose();
        _finishLoadHook?.Dispose();
        _fileDescriptorReadHook?.Dispose();
        _textureInitializeContentsHook?.Dispose();
        _d3d11CreateTexture2DHook?.Dispose();
        _d3d11CreateShaderResourceViewHook?.Dispose();
        _readHook = null;
        _rereadHook = null;
        _loadHook = null;
        _fileJobHook = null;
        _finishLoadHook = null;
        _fileDescriptorReadHook = null;
        _textureInitializeContentsHook = null;
        _d3d11CreateTexture2DHook = null;
        _d3d11CreateShaderResourceViewHook = null;
        _vtableAddress = 0;
        _readAddress = 0;
        _loadAddress = 0;
        _rereadAddress = 0;
        _fileJobAddress = 0;
        _finishLoadAddress = 0;
        _fileDescriptorReadAddress = 0;
        _textureInitializeContentsAddress = 0;
        _d3d11CreateTexture2DAddress = 0;
        _d3d11CreateShaderResourceViewAddress = 0;
        _disposed = true;
        Status = "Native lifecycle hooks disposed.";
    }

    private readonly record struct ReadCaptureContext(
        string ResourceFileName,
        string FileModeName,
        byte FileModeValue,
        ulong FileOffset,
        ulong RequestedLength,
        nint FileBufferAddress);
}

public readonly record struct NativeTextureStateSnapshot(
    nint BlobAddress,
    nint DataAddress,
    ulong DataLength,
    uint FileSize,
    uint FileSize2,
    uint FileSize3,
    uint FileType,
    byte LastIoResult,
    byte ReadState,
    byte LoadState,
    byte OtherState,
    byte ChangeLodRaw,
    nint TextureAddress);

public sealed record NativeTextureLifecycleEvent(
    long Sequence,
    DateTime TimestampUtc,
    string Operation,
    int ThreadId,
    nint HandleAddress,
    string ResourceFileName,
    string FileModeName,
    byte FileModeValue,
    ulong FileOffset,
    ulong RequestedLength,
    byte InputFlag,
    byte Result,
    double ElapsedMilliseconds,
    uint ResourceId,
    uint FileSize,
    uint FileSize2,
    uint FileSize3,
    ushort Width,
    ushort Height,
    ushort Depth,
    byte MipCountAndFlag,
    string Format,
    nint TextureAddress,
    int Priority = 0,
    byte SecondaryInput = 0,
    nint FileDescriptorAddress = 0,
    string ExitFileModeName = "",
    byte ExitFileModeValue = byte.MaxValue,
    ulong ExitFileOffset = 0,
    ulong ExitRequestedLength = 0,
    NativeTextureStateSnapshot StateBefore = default,
    NativeTextureStateSnapshot StateAfter = default,
    nint LoadContentsAddress = 0,
    nint TextureWhileLoadingAddress = 0,
    nint DataWhileLoadingAddress = 0,
    nint FileBufferAddress = 0,
    nint ExitFileBufferAddress = 0,
    nint IoOutputBufferAddress = 0,
    ulong IoLength = 0,
    ulong IoStart = 0,
    byte IoResetPosition = 0,
    bool LoadContentsMatchesDescriptor = false,
    byte LoadDescriptorFileModeValue = byte.MaxValue,
    nint LoadDescriptorFileBufferAddress = 0,
    bool OutputMatchesTextureHeader = false,
    nint OutputOffsetFromHandle = 0,
    NativeTextureHeaderLayout? TextureHeaderLayout = null,
    NativeTextureInitializeContentsSnapshot? TextureInitialization = null,
    NativeTextureHeaderLayout? TextureHeaderLayoutAfter = null);

public readonly record struct NativeTextureHeaderLayout(
    nint HeaderOffsetFromHandle,
    int HeaderSize,
    string LodOffsets,
    string OffsetsToSurfaces,
    string RawHeaderHex);

public readonly record struct NativeTextureObjectSnapshot(
    nint TextureAddress,
    uint ActualWidth,
    uint ActualHeight,
    uint AllocatedWidth,
    uint AllocatedHeight,
    uint Depth,
    byte MipLevel,
    byte ArraySize,
    string Format,
    string Flags,
    nint D3D11TextureAddress,
    nint D3D11ShaderResourceViewAddress,
    string D3D11Description);

public readonly record struct NativeTextureInitializeContentsSnapshot(
    nint TextureObjectAddress,
    nint ContentsAddress,
    nint PayloadBufferAddress,
    ulong PayloadLength,
    ulong PayloadOffset,
    bool ContentsMatchesPayloadBuffer,
    nint ContentsOffsetFromPayloadBuffer,
    bool TextureMatchesActiveHandle,
    NativeTextureObjectSnapshot Before,
    NativeTextureObjectSnapshot After,
    NativeD3D11TextureCreateSnapshot[] D3D11TextureCreateCalls,
    NativeD3D11ShaderResourceViewSnapshot[] D3D11ShaderResourceViewCalls,
    NativeD3D11ResourceRelationshipSnapshot ResourceRelationship);

public readonly record struct NativeD3D11ResourceRelationshipSnapshot(
    bool Inspected,
    bool TexturePointerPresent,
    bool ViewPointerPresent,
    bool TexturePointerMatchesCreateResult,
    bool ViewPointerMatchesCreateResult,
    bool ViewCreateResourcePointerMatchesTexture,
    bool ViewGetResourceSucceeded,
    nint ViewGetResourceAddress,
    bool ViewResourceIdentityCheckSucceeded,
    bool ViewResourceIdentityMatchesTexture,
    bool TextureGetDeviceSucceeded,
    nint TextureDeviceAddress,
    bool ViewGetDeviceSucceeded,
    nint ViewDeviceAddress,
    bool TextureViewDeviceIdentityCheckSucceeded,
    bool TextureAndViewShareDevice,
    bool CreateTextureDeviceIdentityCheckSucceeded,
    bool TextureSharesCreateTextureDevice,
    bool CreateViewDeviceIdentityCheckSucceeded,
    bool ViewSharesCreateViewDevice,
    string Status)
{
    public static NativeD3D11ResourceRelationshipSnapshot NotAvailable(string status) =>
        new(false, false, false, false, false, false, false, 0, false, false,
            false, 0, false, 0, false, false, false, false, false, false, status);
}

public sealed record NativeD3D11TextureCreateSnapshot(
    int HResult,
    nint DeviceAddress,
    nint CreatedTextureAddress,
    uint Width,
    uint Height,
    uint MipLevels,
    uint ArraySize,
    uint Format,
    uint SampleCount,
    uint SampleQuality,
    uint Usage,
    uint BindFlags,
    uint CPUAccessFlags,
    uint MiscFlags,
    nint InitialDataArrayAddress,
    bool InitialDataWasNull,
    nint PayloadBufferAddress,
    ulong PayloadLength,
    bool SubresourceMetadataCaptured,
    NativeD3D11SubresourceSnapshot[] Subresources);

public sealed record NativeD3D11ShaderResourceViewSnapshot(
    int HResult,
    nint DeviceAddress,
    nint ResourceAddress,
    nint CreatedViewAddress,
    bool InputDescriptionWasNull,
    NativeD3D11ShaderResourceViewDescriptionSnapshot InputDescription,
    bool ActualDescriptionCaptured,
    NativeD3D11ShaderResourceViewDescriptionSnapshot ActualDescription);

public readonly record struct NativeD3D11ShaderResourceViewDescriptionSnapshot(
    uint Format,
    uint ViewDimension,
    uint MipMostDetailedOrFirstElement,
    uint MipLevelsOrNumElements,
    uint FirstArraySliceOrFlags,
    uint ArraySizeOrNumElements);

public readonly record struct NativeD3D11SubresourceSnapshot(
    int Index,
    int MipIndex,
    int ArraySlice,
    uint Width,
    uint Height,
    nint SystemMemoryAddress,
    nint OffsetFromPayloadBuffer,
    bool WithinPayloadBuffer,
    uint SystemMemoryPitch,
    uint SystemMemorySlicePitch,
    ulong ExpectedBc7RowPitch,
    ulong ExpectedBc7SliceBytes);
