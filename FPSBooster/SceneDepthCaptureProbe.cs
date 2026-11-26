using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Vortice.Direct3D11;

namespace FPSBooster;

/// <summary>
/// Opt-in, read-only capture of D3D11 scene depth candidates. The hook only observes
/// OMSetRenderTargets calls on Dalamud's immediate context, AddRefs candidate DSVs,
/// and later inspects their resources on the UI/render thread. It never binds a game
/// render target or depth view. A shader-resource view is created only for diagnostic
/// preview; actual depth-aware liquid rendering is a separate follow-up step.
/// </summary>
public sealed unsafe class SceneDepthCaptureProbe : IDisposable
{
    private const int OmSetRenderTargetsVtableIndex = 33;
    private const int Id3D11ViewGetResourceVtableIndex = 7;
    private const int Id3D11ViewGetDescriptionVtableIndex = 8;
    private const int Id3D11DeviceChildGetDeviceVtableIndex = 3;
    private const int Id3D11Texture2DGetDescriptionVtableIndex = 10;
    private const int Id3D11DeviceCreateShaderResourceViewVtableIndex = 7;
    private const int MaximumCandidates = 32;
    private const uint D3D11BindShaderResource = 0x8;
    private const uint DxgiFormatR32Typeless = 39;
    private const uint DxgiFormatD32Float = 40;
    private const uint DxgiFormatR32Float = 41;
    private const uint DxgiFormatR24G8Typeless = 44;
    private const uint DxgiFormatD24UnormS8Uint = 45;
    private const uint DxgiFormatR24UnormX8Typeless = 46;
    private const uint DxgiFormatR16Typeless = 53;
    private const uint DxgiFormatD16Unorm = 55;
    private const uint DxgiFormatR16Unorm = 56;
    private const uint D3D11SrvDimensionTexture2D = 4;
    private const uint D3D11SrvDimensionTexture2DMultisampled = 6;

    private static readonly Guid Id3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private delegate void OmSetRenderTargetsDelegate(nint context, uint renderTargetCount, nint* renderTargetViews, nint depthStencilView);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeTexture2DDescription
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeDepthStencilViewDescription
    {
        public uint Format;
        public uint ViewDimension;
        public uint Flags;
        public uint Union0;
        public uint Union1;
        public uint Union2;
    }

    // D3D11_SHADER_RESOURCE_VIEW_DESC is 24 bytes. The last four UINTs are its dimension union.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeShaderResourceViewDescription
    {
        public uint Format;
        public uint ViewDimension;
        public uint MostDetailedMip;
        public uint MipLevels;
        public uint FirstArraySlice;
        public uint ArraySize;
    }

    private readonly IGameInteropProvider _interop;
    private readonly FluidSimulationProbe _fluid;
    private nint _immediateContext;
    private readonly nint[] _candidatePointers = new nint[MaximumCandidates];
    // Snapshot the camera transform at the exact DSV bind. The UI-stage camera may already
    // have advanced by the time the overlay is emitted, so it is not necessarily the matrix
    // that produced the captured depth texture.
    private readonly Matrix4x4[] _candidateViewProjections = new Matrix4x4[MaximumCandidates];
    private readonly bool[] _candidateProjectionValid = new bool[MaximumCandidates];
    private readonly List<string> _lastReports = new(MaximumCandidates);
    private readonly SceneDepthPreviewRenderer _previewRenderer;
    private Hook<OmSetRenderTargetsDelegate>? _hook;
    private OmSetRenderTargetsDelegate? _original;
    private int _candidateCount;
    private int _captureActive;
    private bool _disposed;
    private bool _forceRecreateView;
    private string _lastSrvCreationStatus = "No depth SRV has been created yet.";
    private Vector2 _lastViewportSize;
    private nint _selectedDepthSrv;
    private nint _selectedDsvKey;
    private Matrix4x4 _selectedViewProjection;
    private bool _selectedProjectionValid;
    private string _selectedProjectionStatus = "No camera snapshot has been captured for the selected depth resource.";
    private uint _selectedWidth;
    private uint _selectedHeight;
    private uint _selectedSampleCount;
    private string _hookStatus = "Not installed. Capture is opt-in.";
    private string _captureStatus = "No scene-depth candidates captured yet.";
    private string _selectedViewStatus = "No shader-resource view created.";
    private long _completedFrames;
    private long _captureFailures;
    private int _eligibleCandidateCount;
    private int _overflowBindEvents;
    private int _lastOverflowBindEvents;
    private int _selectedMissingFrames;
    private string _lastError = string.Empty;

    public SceneDepthCaptureProbe(IGameInteropProvider interop, FluidSimulationProbe fluid)
    {
        _interop = interop;
        _fluid = fluid;
        _previewRenderer = new SceneDepthPreviewRenderer(fluid);
    }

    public bool IsCapturing => Volatile.Read(ref _captureActive) != 0;
    public string Status => _captureStatus;

    /// <summary>
    /// Acquires an independent COM reference to the currently selected single-sample scene-depth SRV.
    /// The caller owns the returned wrapper and must dispose it. The selected view remains managed by
    /// this probe and can be replaced or released independently after the caller has acquired it.
    /// </summary>
    public bool TryAcquireSelectedDepthSrv(out ID3D11ShaderResourceView? view, out uint width, out uint height,
        out Matrix4x4 depthPassViewProjection, out string projectionStatus)
    {
        view = null;
        width = 0;
        height = 0;
        depthPassViewProjection = default;
        projectionStatus = _selectedProjectionStatus;
        nint pointer = _selectedDepthSrv;
        if (pointer == 0 || _selectedSampleCount != 1 || _selectedWidth == 0 || _selectedHeight == 0
            || !_selectedProjectionValid)
            return false;

        try
        {
            Marshal.AddRef(pointer);
            view = new ID3D11ShaderResourceView(pointer);
            width = _selectedWidth;
            height = _selectedHeight;
            depthPassViewProjection = _selectedViewProjection;
            projectionStatus = "Using the Scene.ViewMatrix × Render.ProjectionMatrix snapshot captured when this DSV was bound.";
            return true;
        }
        catch
        {
            if (view != null)
            {
                view.Dispose();
                view = null;
            }
            else
            {
                try { Marshal.Release(pointer); } catch { }
            }
            width = 0;
            height = 0;
            return false;
        }
    }

    public void Draw()
    {
        ImGui.Separator();
        ImGui.Text("Scene depth diagnostics");
        ImGui.TextWrapped("Opt-in D3D11 instrumentation. Captures scene depth-stencil candidates, selects a viewport-matching resource, and creates a read-only SRV. A plugin-owned shader converts single-channel R16/R24/R32 depth into grayscale for inspection; Grounded Fluid can also sample the selected SRV for per-pixel occlusion. The game depth texture is never written.");

        if (!IsCapturing)
        {
            if (ImGui.Button("Start scene depth capture"))
                StartCapture();
        }
        else if (ImGui.Button("Stop scene depth capture"))
        {
            StopCapture();
        }
        ImGui.SameLine();
        if (ImGui.Button("Recreate depth SRV"))
            _forceRecreateView = true;
        ImGui.SameLine();
        if (ImGui.Button("Release depth SRV"))
            ReleaseSelectedView("Released by user.");

        ImGui.TextDisabled($"Hook: {_hookStatus}");
        ImGui.TextDisabled($"Capture: {_captureStatus}");
        ImGui.TextDisabled($"UI-stage depth snapshots: {_completedFrames:N0} · capture errors: {_captureFailures:N0}");
        if (!string.IsNullOrWhiteSpace(_lastError))
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), _lastError);

        ImGui.Separator();
        ImGui.Text("Depth-stencil candidates from latest scene pass");
        if (_lastReports.Count == 0)
            ImGui.TextDisabled("No candidate list available yet. Start capture and let one rendered frame complete.");
        else
            foreach (string report in _lastReports)
                ImGui.BulletText(report);

        ImGui.Separator();
        ImGui.Text("Depth source selection");
        ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f), "Automatic viewport-matching selection is locked.");
        ImGui.TextDisabled($"Eligible choices in latest frame: {_eligibleCandidateCount}. Manual candidates are intentionally disabled because the alternate passes produced unstable camera-dependent artifacts.");

        ImGui.Separator();
        ImGui.Text("Selected depth SRV");
        ImGui.TextWrapped(_selectedViewStatus);
        ImGui.TextDisabled(_selectedProjectionStatus);
        ImGui.TextDisabled(_previewRenderer.Status);
        if (_previewRenderer.IsAvailable)
        {
            ImGui.TextDisabled("Depth visualization: reversed-Z grayscale (nearer values brighter); gamma-expanded for visibility. This is a diagnostic conversion into a plugin-owned texture.");
            ImGui.Image(_previewRenderer.TextureId, _previewRenderer.ImageSize(480f));
        }
        else if (_selectedDepthSrv != 0 && _selectedSampleCount > 1)
        {
            ImGui.TextDisabled("The selected view is multisampled; the first grayscale preview supports single-sample Texture2D resources only.");
        }

        if (!string.IsNullOrWhiteSpace(_lastError) || _lastReports.Count > 0)
        {
            if (ImGui.Button("Copy depth report"))
                ImGui.SetClipboardText(new ImU8String(BuildReport()).Span);
        }
    }

    private void StartCapture()
    {
        if (_disposed)
            return;
        _immediateContext = _fluid.ImmediateContextPointer;
        if (_immediateContext == 0)
        {
            _hookStatus = "Cannot install: create the GPU fluid simulation first so FPSBooster can identify Dalamud's immediate context.";
            return;
        }

        try
        {
            nint* vtable = *(nint**)_immediateContext;
            if (vtable == null || vtable[OmSetRenderTargetsVtableIndex] == 0)
                throw new InvalidOperationException("ID3D11DeviceContext.OMSetRenderTargets vtable[33] is unavailable.");

            _hook = _interop.HookFromAddress<OmSetRenderTargetsDelegate>(vtable[OmSetRenderTargetsVtableIndex], OmSetRenderTargetsDetour);
            _original = _hook.Original;
            _hook.Enable();
            Volatile.Write(ref _captureActive, 1);
            _hookStatus = $"Installed on immediate context 0x{_immediateContext:X}; observing OMSetRenderTargets vtable[33].";
            _captureStatus = "Waiting for the next UI-stage snapshot of scene DSV binds.";
            _lastError = string.Empty;
        }
        catch (Exception ex)
        {
            try { _hook?.Dispose(); } catch { }
            _hook = null;
            _original = null;
            Volatile.Write(ref _captureActive, 0);
            _hookStatus = "Hook installation failed.";
            _lastError = ex.GetBaseException().Message;
            Interlocked.Increment(ref _captureFailures);
        }
    }

    private void StopCapture()
    {
        Volatile.Write(ref _captureActive, 0);
        try { _hook?.Disable(); } catch { }
        try { _hook?.Dispose(); } catch { }
        _hook = null;
        // Keep the original delegate rooted for any detour invocation already in flight.
        ReleasePendingCandidates();
        ReleaseSelectedView("Capture stopped; selected depth SRV released.");
        _hookStatus = "Stopped.";
        _captureStatus = "Capture stopped.";
    }

    private void OmSetRenderTargetsDetour(nint context, uint renderTargetCount, nint* renderTargetViews, nint depthStencilView)
    {
        // Always complete the game's original render-target transition before doing any observation.
        _original?.Invoke(context, renderTargetCount, renderTargetViews, depthStencilView);

        if (context != _immediateContext || Volatile.Read(ref _captureActive) == 0)
            return;

        try
        {
            if (depthStencilView == 0)
                return;

            int candidateIndex = -1;
            for (int i = 0; i < _candidateCount; i++)
            {
                if (_candidatePointers[i] == depthStencilView)
                {
                    candidateIndex = i;
                    break;
                }
            }

            if (candidateIndex < 0)
            {
                if (_candidateCount >= MaximumCandidates)
                {
                    Interlocked.Increment(ref _overflowBindEvents);
                    return;
                }

                Marshal.AddRef(depthStencilView);
                candidateIndex = _candidateCount++;
                _candidatePointers[candidateIndex] = depthStencilView;
                _candidateProjectionValid[candidateIndex] = false;
            }

            // The depth resource is bound by the game before its draw pass. Snapshot the
            // validated scene-view/render-projection pair at that exact point, and update it
            // on repeated binds of the same DSV. This keeps the world projection synchronized
            // with the depth buffer instead of sampling a potentially advanced UI-stage camera.
            if (CollisionProbe.TryCaptureWorldProjection(out Matrix4x4 viewProjection, out _))
            {
                _candidateViewProjections[candidateIndex] = viewProjection;
                _candidateProjectionValid[candidateIndex] = true;
            }
        }
        catch
        {
            Interlocked.Increment(ref _captureFailures);
        }
    }

    /// <summary>Called from the UI draw callback after the scene pass, snapshotting this frame's DSV binds.</summary>
    public void ProcessFrameAtUiStage()
    {
        if (!IsCapturing)
            return;

        int count = Math.Clamp(_candidateCount, 0, MaximumCandidates);
        _lastOverflowBindEvents = Interlocked.Exchange(ref _overflowBindEvents, 0);
        var candidates = new nint[count];
        var candidateViewProjections = new Matrix4x4[count];
        var candidateProjectionValid = new bool[count];
        Array.Copy(_candidatePointers, candidates, count);
        Array.Copy(_candidateViewProjections, candidateViewProjections, count);
        Array.Copy(_candidateProjectionValid, candidateProjectionValid, count);
        Array.Clear(_candidatePointers, 0, count);
        Array.Clear(_candidateProjectionValid, 0, count);
        _candidateCount = 0;
        _completedFrames++;

        _lastReports.Clear();
        _eligibleCandidateCount = 0;
        nint autoCandidate = 0;
        NativeTexture2DDescription autoDescription = default;
        Matrix4x4 autoViewProjection = default;
        bool autoProjectionValid = false;
        string autoReason = string.Empty;
        double bestScore = double.MaxValue;
        long bestPixels = 0;
        bool selectedSeen = false;
        Vector2 viewport = ImGui.GetMainViewport().Size;
        _lastViewportSize = viewport;
        float viewportWidth = Math.Max(viewport.X, 1f);
        float viewportHeight = Math.Max(viewport.Y, 1f);
        float viewportAspect = viewportWidth / viewportHeight;
        uint minimumWidth = (uint)Math.Max(1f, viewportWidth * 0.5f);
        uint minimumHeight = (uint)Math.Max(1f, viewportHeight * 0.5f);

        try
        {
            for (int ordinal = 0; ordinal < candidates.Length; ordinal++)
            {
                nint dsv = candidates[ordinal];
                if (!TryInspectDsv(dsv, out NativeTexture2DDescription texture, out NativeDepthStencilViewDescription view, out string detail))
                {
                    _lastReports.Add($"DSV #{ordinal + 1} 0x{dsv:X}: {detail}");
                    continue;
                }

                long pixels = (long)texture.Width * texture.Height;
                bool resolutionCandidate = texture.Width >= minimumWidth && texture.Height >= minimumHeight;
                bool formatCandidate = (texture.Format == DxgiFormatR16Typeless && view.Format == DxgiFormatD16Unorm)
                    || (texture.Format == DxgiFormatR24G8Typeless && view.Format == DxgiFormatD24UnormS8Uint)
                    || (texture.Format == DxgiFormatR32Typeless && view.Format == DxgiFormatD32Float);
                bool bindCandidate = (texture.BindFlags & D3D11BindShaderResource) != 0;
                bool dimensionCandidate = view.ViewDimension == 3; // D3D11_DSV_DIMENSION_TEXTURE2D
                bool eligible = resolutionCandidate && formatCandidate && bindCandidate && dimensionCandidate
                    && texture.ArraySize == 1;
                string report = $"DSV #{ordinal + 1} 0x{dsv:X}: {texture.Width}×{texture.Height}, resource {DescribeFormat(texture.Format)}, view {DescribeFormat(view.Format)} / dimension {view.ViewDimension}, samples {texture.SampleCount}/{texture.SampleQuality}, mips {texture.MipLevels}, array {texture.ArraySize}, bind 0x{texture.BindFlags:X}";
                if (!resolutionCandidate)
                    report += " · skipped: below half viewport resolution";
                else if (!formatCandidate)
                    report += " · skipped: resource/view format pair is unsupported";
                else if (!bindCandidate)
                    report += " · skipped: D3D11_BIND_SHADER_RESOURCE is absent";
                else if (!dimensionCandidate || texture.ArraySize != 1)
                    report += " · skipped: first preview supports Texture2D, array size 1";
                else
                {
                    report += " · eligible for depth SRV";
                    _eligibleCandidateCount++;
                }
                _lastReports.Add(report);

                if (dsv == _selectedDsvKey && texture.Width == _selectedWidth && texture.Height == _selectedHeight
                    && texture.SampleCount == _selectedSampleCount)
                    selectedSeen = true;

                if (!eligible)
                    continue;

                string reason = $"DSV #{ordinal + 1}, {texture.Width}×{texture.Height}, {DescribeFormat(texture.Format)}, {texture.SampleCount} sample(s)";
                // Prefer a candidate matching the actual viewport over large off-screen
                // resources such as shadow maps. Exact width/height matches score best.
                double candidateAspect = (double)texture.Width / Math.Max(texture.Height, 1u);
                double aspectError = Math.Abs(Math.Log(candidateAspect / viewportAspect));
                // Once aspect ratio is close, prefer the highest-resolution matching scene
                // target. This avoids choosing a half-resolution intermediate when a full
                // viewport-sized depth target is also bound in the same frame.
                double score = aspectError * 100.0 - Math.Log(Math.Max(pixels, 1L));
                if (score < bestScore || (Math.Abs(score - bestScore) < 0.0001 && pixels > bestPixels))
                {
                    autoCandidate = dsv;
                    autoDescription = texture;
                    autoViewProjection = candidateViewProjections[ordinal];
                    autoProjectionValid = candidateProjectionValid[ordinal];
                    autoReason = reason;
                    bestScore = score;
                    bestPixels = pixels;
                }
            }

            nint bestCandidate = autoCandidate;
            NativeTexture2DDescription bestDescription = autoDescription;
            string bestReason = autoReason;

            if (_selectedDepthSrv != 0 && !selectedSeen)
                _selectedMissingFrames++;
            else
                _selectedMissingFrames = 0;

            bool mustRecreate = _forceRecreateView;
            _forceRecreateView = false;
            bool newBest = bestCandidate != 0 && (bestCandidate != _selectedDsvKey
                || bestDescription.Width != _selectedWidth || bestDescription.Height != _selectedHeight
                || bestDescription.SampleCount != _selectedSampleCount);
            if (bestCandidate != 0 && (mustRecreate || _selectedDepthSrv == 0 || newBest))
            {
                if (TryCreateDepthSrv(bestCandidate, bestDescription, out nint shaderResourceView, out string createStatus))
                {
                    ReleaseSelectedView("Replaced by the selected scene depth candidate.");
                    _selectedDepthSrv = shaderResourceView;
                    _selectedDsvKey = bestCandidate;
                    _selectedWidth = bestDescription.Width;
                    _selectedHeight = bestDescription.Height;
                    _selectedSampleCount = bestDescription.SampleCount;
                    _selectedViewStatus = $"SRV creation succeeded for {bestReason}. {createStatus}";
                    _lastSrvCreationStatus = _selectedViewStatus;
                }
                else
                {
                    _selectedViewStatus = $"A scene depth candidate was found ({bestReason}), but SRV creation failed: {createStatus}";
                    _lastSrvCreationStatus = _selectedViewStatus;
                    Interlocked.Increment(ref _captureFailures);
                }
            }
            else if (bestCandidate == 0 && _lastReports.Count == 0)
            {
                _captureStatus = "No DSV candidates were bound on the captured UI frame.";
            }

            // ReleaseSelectedView() clears the old matrix snapshot when the SRV changes.
            // Associate this frame's snapshot only after candidate replacement has completed,
            // and only when the live SRV actually belongs to the candidate we selected.
            if (_selectedDepthSrv != 0 && bestCandidate != 0 && _selectedDsvKey == bestCandidate)
            {
                _selectedViewProjection = autoViewProjection;
                _selectedProjectionValid = autoProjectionValid;
                _selectedProjectionStatus = autoProjectionValid
                    ? "Camera projection snapshot captured at selected DSV bind."
                    : "Selected DSV was seen, but its camera projection snapshot could not be captured.";
            }

            if (_selectedDepthSrv != 0 && _selectedMissingFrames >= 3)
                ReleaseSelectedView("The selected DSV was absent for three completed scene captures; released the potentially stale SRV.");

            if (_selectedDepthSrv != 0 && _selectedSampleCount == 1)
            {
                if (!_previewRenderer.Render(_selectedDepthSrv, _selectedWidth, _selectedHeight))
                    _lastError = _previewRenderer.Status;
            }

            _captureStatus = _lastReports.Count == 0
                ? "No DSV candidates were bound on the latest captured UI frame."
                : $"Captured {_lastReports.Count} DSV candidate(s); {_eligibleCandidateCount} eligible for depth preview."
                    + (_lastOverflowBindEvents > 0 ? $" {_lastOverflowBindEvents} additional DSV bind event(s) were ignored after the {MaximumCandidates}-candidate cap." : string.Empty);
        }
        catch (Exception ex)
        {
            _lastError = ex.GetBaseException().Message;
            Interlocked.Increment(ref _captureFailures);
        }
        finally
        {
            foreach (nint candidate in candidates)
            {
                if (candidate != 0)
                {
                    try { Marshal.Release(candidate); } catch { }
                }
            }
        }
    }

    private static bool TryInspectDsv(nint dsv, out NativeTexture2DDescription textureDescription,
        out NativeDepthStencilViewDescription viewDescription, out string status)
    {
        textureDescription = default;
        viewDescription = default;
        status = string.Empty;
        nint resource = 0;
        nint texture2D = 0;
        try
        {
            nint* viewVtable = *(nint**)dsv;
            if (viewVtable == null || viewVtable[Id3D11ViewGetResourceVtableIndex] == 0
                || viewVtable[Id3D11ViewGetDescriptionVtableIndex] == 0)
            {
                status = "ID3D11View methods unavailable.";
                return false;
            }

            var getResource = (delegate* unmanaged[Stdcall]<void*, void**, void>)viewVtable[Id3D11ViewGetResourceVtableIndex];
            var getViewDescription = (delegate* unmanaged[Stdcall]<void*, NativeDepthStencilViewDescription*, void>)viewVtable[Id3D11ViewGetDescriptionVtableIndex];
            void* resourceOutput = null;
            getResource((void*)dsv, &resourceOutput);
            resource = (nint)resourceOutput;
            if (resource == 0)
            {
                status = "ID3D11View.GetResource returned null.";
                return false;
            }

            NativeDepthStencilViewDescription localViewDescription = default;
            getViewDescription((void*)dsv, &localViewDescription);
            viewDescription = localViewDescription;

            Guid texture2DIid = Id3D11Texture2D;
            int queryResult = Marshal.QueryInterface(resource, in texture2DIid, out texture2D);
            if (queryResult < 0 || texture2D == 0)
            {
                status = $"Underlying resource is not ID3D11Texture2D (HRESULT 0x{queryResult:X8}).";
                return false;
            }

            nint* textureVtable = *(nint**)texture2D;
            if (textureVtable == null || textureVtable[Id3D11Texture2DGetDescriptionVtableIndex] == 0)
            {
                status = "ID3D11Texture2D.GetDesc unavailable.";
                return false;
            }

            NativeTexture2DDescription localTextureDescription = default;
            var getTextureDescription = (delegate* unmanaged[Stdcall]<void*, NativeTexture2DDescription*, void>)textureVtable[Id3D11Texture2DGetDescriptionVtableIndex];
            getTextureDescription((void*)texture2D, &localTextureDescription);
            textureDescription = localTextureDescription;
            status = "Texture2D description captured.";
            return true;
        }
        catch (Exception ex)
        {
            status = $"Inspection failed: {ex.GetBaseException().Message}";
            return false;
        }
        finally
        {
            if (texture2D != 0)
                Marshal.Release(texture2D);
            if (resource != 0)
                Marshal.Release(resource);
        }
    }

    private static bool TryCreateDepthSrv(nint dsv, NativeTexture2DDescription texture, out nint shaderResourceView, out string status)
    {
        shaderResourceView = 0;
        status = string.Empty;
        nint resource = 0;
        nint texture2D = 0;
        nint device = 0;
        try
        {
            nint* viewVtable = *(nint**)dsv;
            if (viewVtable == null || viewVtable[Id3D11ViewGetResourceVtableIndex] == 0)
            {
                status = "ID3D11View.GetResource unavailable.";
                return false;
            }
            var getResource = (delegate* unmanaged[Stdcall]<void*, void**, void>)viewVtable[Id3D11ViewGetResourceVtableIndex];
            void* resourceOutput = null;
            getResource((void*)dsv, &resourceOutput);
            resource = (nint)resourceOutput;
            if (resource == 0)
            {
                status = "ID3D11View.GetResource returned null.";
                return false;
            }

            Guid texture2DIid = Id3D11Texture2D;
            int queryResult = Marshal.QueryInterface(resource, in texture2DIid, out texture2D);
            if (queryResult < 0 || texture2D == 0)
            {
                status = $"Texture2D QueryInterface failed (HRESULT 0x{queryResult:X8}).";
                return false;
            }

            nint* childVtable = *(nint**)texture2D;
            if (childVtable == null || childVtable[Id3D11DeviceChildGetDeviceVtableIndex] == 0)
            {
                status = "ID3D11DeviceChild.GetDevice unavailable.";
                return false;
            }
            var getDevice = (delegate* unmanaged[Stdcall]<void*, void**, void>)childVtable[Id3D11DeviceChildGetDeviceVtableIndex];
            void* deviceOutput = null;
            getDevice((void*)texture2D, &deviceOutput);
            device = (nint)deviceOutput;
            if (device == 0)
            {
                status = "Texture2D.GetDevice returned null.";
                return false;
            }

            uint srvFormat = texture.Format switch
            {
                DxgiFormatR32Typeless => DxgiFormatR32Float,
                DxgiFormatR24G8Typeless => DxgiFormatR24UnormX8Typeless,
                DxgiFormatR16Typeless => DxgiFormatR16Unorm,
                _ => 0,
            };
            if (srvFormat == 0)
            {
                status = $"Unsupported depth resource format {DescribeFormat(texture.Format)}.";
                return false;
            }
            if ((texture.BindFlags & D3D11BindShaderResource) == 0)
            {
                status = "Resource lacks D3D11_BIND_SHADER_RESOURCE.";
                return false;
            }
            if (texture.ArraySize != 1)
            {
                status = $"Array size {texture.ArraySize} is not supported by this first diagnostic view.";
                return false;
            }

            NativeShaderResourceViewDescription description = new()
            {
                Format = srvFormat,
                ViewDimension = texture.SampleCount > 1 ? D3D11SrvDimensionTexture2DMultisampled : D3D11SrvDimensionTexture2D,
                MostDetailedMip = 0,
                MipLevels = texture.SampleCount > 1 ? 0u : Math.Max(1u, texture.MipLevels),
            };

            nint* deviceVtable = *(nint**)device;
            if (deviceVtable == null || deviceVtable[Id3D11DeviceCreateShaderResourceViewVtableIndex] == 0)
            {
                status = "ID3D11Device.CreateShaderResourceView unavailable.";
                return false;
            }
            var createSrv = (delegate* unmanaged[Stdcall]<void*, void*, NativeShaderResourceViewDescription*, void**, int>)deviceVtable[Id3D11DeviceCreateShaderResourceViewVtableIndex];
            void* srvOutput = null;
            int hresult = createSrv((void*)device, (void*)texture2D, &description, &srvOutput);
            shaderResourceView = (nint)srvOutput;
            if (hresult < 0 || shaderResourceView == 0)
            {
                if (shaderResourceView != 0)
                {
                    Marshal.Release(shaderResourceView);
                    shaderResourceView = 0;
                }
                status = $"CreateShaderResourceView failed (HRESULT 0x{hresult:X8}).";
                return false;
            }

            status = $"Created {DescribeFormat(srvFormat)} SRV ({(texture.SampleCount > 1 ? "Texture2DMS" : "Texture2D")}) from {DescribeFormat(texture.Format)}.";
            return true;
        }
        catch (Exception ex)
        {
            if (shaderResourceView != 0)
            {
                try { Marshal.Release(shaderResourceView); } catch { }
                shaderResourceView = 0;
            }
            status = $"SRV creation threw {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}";
            return false;
        }
        finally
        {
            if (device != 0)
                Marshal.Release(device);
            if (texture2D != 0)
                Marshal.Release(texture2D);
            if (resource != 0)
                Marshal.Release(resource);
        }
    }

    private void ReleasePendingCandidates()
    {
        int count = Math.Clamp(_candidateCount, 0, MaximumCandidates);
        for (int i = 0; i < count; i++)
        {
            nint candidate = _candidatePointers[i];
            _candidatePointers[i] = 0;
            if (candidate != 0)
            {
                try { Marshal.Release(candidate); } catch { }
            }
        }
        Array.Clear(_candidateProjectionValid, 0, _candidateProjectionValid.Length);
        _candidateCount = 0;
    }

    private void ReleaseSelectedView(string reason)
    {
        nint selected = _selectedDepthSrv;
        _selectedDepthSrv = 0;
        _selectedDsvKey = 0;
        _selectedWidth = 0;
        _selectedHeight = 0;
        _selectedSampleCount = 0;
        _selectedMissingFrames = 0;
        _selectedViewProjection = default;
        _selectedProjectionValid = false;
        _selectedProjectionStatus = "No camera snapshot is associated with the selected depth resource.";
        if (selected != 0)
        {
            try { Marshal.Release(selected); } catch { }
        }
        _selectedViewStatus = reason;
    }

    private string BuildReport()
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine("FPSBooster Scene Depth Capture");
        report.AppendLine($"Timestamp UTC: {DateTime.UtcNow:O}");
        report.AppendLine($"Status: {_captureStatus}");
        report.AppendLine($"Hook: {_hookStatus}");
        report.AppendLine($"Completed frames: {_completedFrames}");
        report.AppendLine($"Capture failures: {_captureFailures}");
        report.AppendLine($"Candidate cap: {MaximumCandidates}; additional DSV bind events ignored on latest frame: {_lastOverflowBindEvents}");
        report.AppendLine($"Selected SRV: {_selectedViewStatus}");
        report.AppendLine($"Selected DSV camera projection: {_selectedProjectionStatus}");
        report.AppendLine($"Last SRV creation: {_lastSrvCreationStatus}");
        report.AppendLine($"Preview: {_previewRenderer.Status}");
        report.AppendLine("Candidate selection: Auto viewport match (manual selection disabled after alternate-pass instability)");
        report.AppendLine($"ImGui viewport: {_lastViewportSize.X:0}×{_lastViewportSize.Y:0}");
        foreach (string candidate in _lastReports)
            report.AppendLine(candidate);
        if (!string.IsNullOrEmpty(_lastError))
            report.AppendLine($"Error: {_lastError}");
        return report.ToString();
    }

    private static string DescribeFormat(uint format) => format switch
    {
        39 => "R32_TYPELESS",
        40 => "D32_FLOAT",
        41 => "R32_FLOAT",
        44 => "R24G8_TYPELESS",
        45 => "D24_UNORM_S8_UINT",
        46 => "R24_UNORM_X8_TYPELESS",
        53 => "R16_TYPELESS",
        54 => "R16_FLOAT",
        55 => "D16_UNORM",
        56 => "R16_UNORM",
        _ => $"DXGI_FORMAT_{format}",
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopCapture();
        _previewRenderer.Dispose();
    }
}
