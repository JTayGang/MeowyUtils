using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FPSBooster;

/// <summary>
/// Safe shadow experiment for native progressive texture loading. It creates a separate
/// plugin-owned DEFAULT D3D11 texture, uploads copied native mip data from coarse to fine,
/// and expands an SRV only after each contiguous mip range is initialized. It never replaces,
/// edits, or takes ownership of an FFXIV texture or SRV.
/// All immediate-context operations run on the Dalamud UI draw thread.
/// </summary>
public sealed class NativeProgressiveTexturePreview : IDisposable
{
    private const double InitialEstimatedUploadMillisecondsPerMiB = 0.20d;
    private const double UploadEstimateOverheadMilliseconds = 0.008d;
    private const double DefaultUploadBudgetMilliseconds = 1.0d;

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly List<ID3D11ShaderResourceView> _retiredShaderResourceViews = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _shaderResourceView;
    private NativeTextureMipPayloadCapture? _capture;
    private string _resourceFileName = string.Empty;
    private uint _width;
    private uint _height;
    private int _mipCount;
    private int _nextMipIndex = -1;
    private int _uploadedMipCount;
    private int _firstUploadedMip = -1;
    private int _viewFirstMip = -1;
    private bool _viewDirty;
    private string _status = "No native shadow texture captured.";
    private string? _error;
    private double _estimatedUploadMillisecondsPerMiB = InitialEstimatedUploadMillisecondsPerMiB;
    private int _refinementFrameCount;
    private int _totalUiFrameCount;
    private int _budgetDeferrals;
    private int _largeMipIsolationDeferrals;
    private double _maximumFrameUploadMilliseconds;
    private double _lastFrameUploadMilliseconds;
    private int _lastFrameMipCount;
    private long _lastFrameBytes;
    private long _uploadedBytes;
    private long _totalMipPayloadBytes;
    private double _captureToFirstPreviewMilliseconds = -1d;
    private double _captureToFullResolutionMilliseconds = -1d;
    private double _initialPreviewSetupMilliseconds;
    private double _initialMipUploadMilliseconds;
    private double _initialViewCreationMilliseconds;
    private double _lastDeferredEstimateMilliseconds;
    private double _lastDeferredRemainingMilliseconds;

    public NativeProgressiveTexturePreview(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
    }

    public bool IsCreated => _texture != null && _shaderResourceView != null;
    public bool IsComplete => IsCreated && _nextMipIndex < 0 && !_viewDirty;
    public string Status => _status;
    public string? Error => _error;
    public uint ResourceId => _capture?.ResourceId ?? 0;
    public string ResourceFileName => _resourceFileName;
    public uint Width => _width;
    public uint Height => _height;
    public int MipCount => _mipCount;
    public int UploadedMipCount => _uploadedMipCount;
    public int MipsRemaining => Math.Max(0, _mipCount - _uploadedMipCount);
    public int FirstVisibleMip => _viewFirstMip;
    public int RefinementFrameCount => _refinementFrameCount;
    public int TotalUiFrameCount => _totalUiFrameCount;
    public int BudgetDeferrals => _budgetDeferrals;
    public int LargeMipIsolationDeferrals => _largeMipIsolationDeferrals;
    public double LastFrameUploadMilliseconds => _lastFrameUploadMilliseconds;
    public int LastFrameMipCount => _lastFrameMipCount;
    public long LastFrameBytes => _lastFrameBytes;
    public long UploadedBytes => _uploadedBytes;
    public long TotalMipPayloadBytes => _totalMipPayloadBytes;
    public double CaptureCopyMilliseconds => _capture?.CopyMilliseconds ?? 0d;
    public double CaptureToFirstPreviewMilliseconds => _captureToFirstPreviewMilliseconds;
    public double CaptureToFullResolutionMilliseconds => _captureToFullResolutionMilliseconds;
    public double InitialPreviewSetupMilliseconds => _initialPreviewSetupMilliseconds;
    public double InitialMipUploadMilliseconds => _initialMipUploadMilliseconds;
    public double InitialViewCreationMilliseconds => _initialViewCreationMilliseconds;
    public double MaximumFrameUploadMilliseconds => _maximumFrameUploadMilliseconds;
    public double LastDeferredEstimateMilliseconds => _lastDeferredEstimateMilliseconds;
    public double LastDeferredRemainingMilliseconds => _lastDeferredRemainingMilliseconds;
    public int PreviewMipCount => _viewFirstMip < 0 ? 0 : _mipCount - _viewFirstMip;

    public ImTextureID PreviewTextureId
    {
        get
        {
            if (_shaderResourceView == null)
                return default;

            nint nativePointer = _shaderResourceView.NativePointer;
            return Unsafe.As<nint, ImTextureID>(ref nativePointer);
        }
    }

    /// <summary>
    /// Creates a plugin-owned DEFAULT texture with no initial data. The coarsest mip is
    /// uploaded immediately, then the SRV is restricted to that initialized mip.
    /// </summary>
    public void Create(NativeTextureMipPayloadCapture capture)
    {
        Reset();
        ValidateCapture(capture);

        nint borrowedDevice = _pluginInterface.UiBuilder.DeviceHandle;
        if (borrowedDevice == 0)
            throw new InvalidOperationException("Dalamud has not exposed the D3D11 device yet. Try again while the UI is drawing.");

        Guid iid = typeof(ID3D11Device).GUID;
        int queryResult = Marshal.QueryInterface(borrowedDevice, in iid, out nint ownedDeviceReference);
        if (queryResult < 0)
            Marshal.ThrowExceptionForHR(queryResult);
        if (ownedDeviceReference == 0)
            throw new InvalidOperationException("QueryInterface returned an empty D3D11 device reference.");

        var previewTimer = Stopwatch.StartNew();
        try
        {
            // QueryInterface returned an owned COM reference. Vortice wraps it without adding
            // another reference; disposing the wrapper releases only the reference we acquired.
            _device = new ID3D11Device(ownedDeviceReference);
            _context = _device.ImmediateContext;
            var allocationTimer = Stopwatch.StartNew();
            _texture = _device.CreateTexture2D(
                Format.BC7_UNorm,
                capture.Width,
                capture.Height,
                arraySize: 1,
                mipLevels: checked((uint)capture.Mips.Length),
                initialData: null,
                bindFlags: BindFlags.ShaderResource,
                usage: ResourceUsage.Default);
            allocationTimer.Stop();

            _capture = capture;
            _resourceFileName = capture.ResourceFileName;
            _width = capture.Width;
            _height = capture.Height;
            _mipCount = capture.Mips.Length;
            _nextMipIndex = _mipCount - 1;
            _totalMipPayloadBytes = capture.TotalCopiedBytes;
            _estimatedUploadMillisecondsPerMiB = InitialEstimatedUploadMillisecondsPerMiB;
            _error = null;

            // First visible state: only the 1x1 coarsest mip is initialized and exposed.
            _initialMipUploadMilliseconds = UploadMip(_nextMipIndex, out long initialMipBytes);
            _uploadedBytes += initialMipBytes;
            _uploadedMipCount++;
            _firstUploadedMip = _nextMipIndex;
            _nextMipIndex--;
            var viewTimer = Stopwatch.StartNew();
            CreateOrExpandShaderResourceView(_firstUploadedMip);
            viewTimer.Stop();
            _initialViewCreationMilliseconds = viewTimer.Elapsed.TotalMilliseconds;
            previewTimer.Stop();
            _initialPreviewSetupMilliseconds = previewTimer.Elapsed.TotalMilliseconds;
            _captureToFirstPreviewMilliseconds = Math.Max(0d, (DateTime.UtcNow - capture.CapturedUtc).TotalMilliseconds);
            _totalUiFrameCount = 1; // The draw frame that creates and displays the initial coarse preview.
            _lastFrameMipCount = 1;
            _lastFrameBytes = initialMipBytes;
            _lastFrameUploadMilliseconds = _initialMipUploadMilliseconds;
            _maximumFrameUploadMilliseconds = _initialMipUploadMilliseconds;
            if (IsComplete)
                _captureToFullResolutionMilliseconds = _captureToFirstPreviewMilliseconds;
            _status = $"Shadow texture ready. Coarsest Mip {_mipCount - 1} is uploaded; the SRV exposes only that initialized mip. The original FFXIV texture was not modified. D3D11 allocation: {allocationTimer.Elapsed.TotalMilliseconds:F3} ms.";
        }
        catch
        {
            if (_device == null)
                Marshal.Release(ownedDeviceReference);
            Reset();
            throw;
        }
    }

    /// <summary>
    /// Uploads coarser-to-finer mip levels from the plugin-owned copies. The normal budget is
    /// 1 ms of measured UpdateSubresource submission time. Mip 0 is isolated to a fresh frame.
    /// </summary>
    public void AdvanceFrame()
    {
        if (!IsCreated || _context == null || _texture == null || _capture == null || IsComplete)
            return;

        _refinementFrameCount++;
        _totalUiFrameCount++;
        var frameTimer = Stopwatch.StartNew();
        double frameSubmissionMilliseconds = 0d;
        int uploadsThisFrame = 0;
        long bytesThisFrame = 0;
        bool uploadedAny = false;

        try
        {
            // If an earlier SRV expansion failed, retry before adding more initialized levels.
            if (_viewDirty)
            {
                CreateOrExpandShaderResourceView(_firstUploadedMip);
                _viewDirty = false;
            }

            while (_nextMipIndex >= 0)
            {
                // Keep the largest 4K mip on a fresh UI frame rather than combining it with
                // smaller uploads. That mirrors the proven 0.2.9.9 isolation policy.
                if (_nextMipIndex == 0 && uploadsThisFrame > 0)
                {
                    _largeMipIsolationDeferrals++;
                    break;
                }

                NativeTextureMipPayload mip = _capture.Mips[_nextMipIndex];
                double estimatedCost = EstimateUploadMilliseconds(mip.ByteLength);
                double remainingBudget = Math.Max(0d, DefaultUploadBudgetMilliseconds - frameSubmissionMilliseconds);
                if (uploadsThisFrame > 0 && estimatedCost > remainingBudget)
                {
                    _budgetDeferrals++;
                    _lastDeferredEstimateMilliseconds = estimatedCost;
                    _lastDeferredRemainingMilliseconds = remainingBudget;
                    break;
                }

                double actualMilliseconds = UploadMip(_nextMipIndex, out long uploadedBytes);
                frameSubmissionMilliseconds += actualMilliseconds;
                bytesThisFrame += uploadedBytes;
                _uploadedBytes += uploadedBytes;
                uploadsThisFrame++;
                uploadedAny = true;
                _uploadedMipCount++;
                _firstUploadedMip = _nextMipIndex;
                _nextMipIndex--;

                if (uploadedBytes >= 1024 * 1024)
                {
                    double observedPerMiB = actualMilliseconds / (uploadedBytes / (1024d * 1024d));
                    if (double.IsFinite(observedPerMiB) && observedPerMiB >= 0d)
                        _estimatedUploadMillisecondsPerMiB = (_estimatedUploadMillisecondsPerMiB * 0.65d) + (observedPerMiB * 0.35d);
                }

                // Never combine the unavoidable largest mip with any other mip. It may still
                // exceed the 1 ms budget by itself because D3D11 does not split UpdateSubresource.
                if (_nextMipIndex < 0 || _firstUploadedMip == 0)
                    break;
            }

            if (uploadedAny)
            {
                try
                {
                    CreateOrExpandShaderResourceView(_firstUploadedMip);
                    _viewDirty = false;
                }
                catch
                {
                    // The old SRV remains valid and exposes only previously initialized mips.
                    // Retry the expansion on the next draw before uploading additional levels.
                    _viewDirty = true;
                    throw;
                }
            }

            frameTimer.Stop();
            if (IsComplete && _captureToFullResolutionMilliseconds < 0d && _capture != null)
                _captureToFullResolutionMilliseconds = Math.Max(0d, (DateTime.UtcNow - _capture.CapturedUtc).TotalMilliseconds);
            _lastFrameMipCount = uploadsThisFrame;
            _lastFrameBytes = bytesThisFrame;
            _lastFrameUploadMilliseconds = frameSubmissionMilliseconds;
            _maximumFrameUploadMilliseconds = Math.Max(_maximumFrameUploadMilliseconds, frameSubmissionMilliseconds);
            _status = IsComplete
                ? $"Shadow refinement complete: all {_mipCount} mips uploaded across {_totalUiFrameCount} total UI frames (1 initial preview + {_refinementFrameCount} refinement frames)."
                : $"Shadow refinement: {_uploadedMipCount}/{_mipCount} mips uploaded; SRV exposes Mip {_viewFirstMip}–{_mipCount - 1}. Last frame: {uploadsThisFrame} mip(s), {FormatBytes(bytesThisFrame)}, {frameSubmissionMilliseconds:F3} ms upload submission.";
        }
        catch (Exception ex)
        {
            frameTimer.Stop();
            _lastFrameMipCount = uploadsThisFrame;
            _lastFrameBytes = bytesThisFrame;
            _lastFrameUploadMilliseconds = frameSubmissionMilliseconds;
            _maximumFrameUploadMilliseconds = Math.Max(_maximumFrameUploadMilliseconds, frameSubmissionMilliseconds);
            _error = ex.Message;
            _status = $"Progressive shadow preview stopped: {ex.Message}";
        }
    }

    public void SetError(string message)
    {
        _error = message;
        _status = message;
    }

    public void Reset()
    {
        ReleaseResources();
        _capture = null;
        _resourceFileName = string.Empty;
        _width = 0;
        _height = 0;
        _mipCount = 0;
        _nextMipIndex = -1;
        _uploadedMipCount = 0;
        _firstUploadedMip = -1;
        _viewFirstMip = -1;
        _viewDirty = false;
        _status = "No native shadow texture captured.";
        _error = null;
        _estimatedUploadMillisecondsPerMiB = InitialEstimatedUploadMillisecondsPerMiB;
        _refinementFrameCount = 0;
        _totalUiFrameCount = 0;
        _budgetDeferrals = 0;
        _largeMipIsolationDeferrals = 0;
        _maximumFrameUploadMilliseconds = 0d;
        _lastFrameUploadMilliseconds = 0d;
        _lastFrameMipCount = 0;
        _lastFrameBytes = 0;
        _uploadedBytes = 0;
        _totalMipPayloadBytes = 0;
        _captureToFirstPreviewMilliseconds = -1d;
        _captureToFullResolutionMilliseconds = -1d;
        _initialPreviewSetupMilliseconds = 0d;
        _initialMipUploadMilliseconds = 0d;
        _initialViewCreationMilliseconds = 0d;
        _lastDeferredEstimateMilliseconds = 0d;
        _lastDeferredRemainingMilliseconds = 0d;
    }

    public void Dispose() => Reset();

    private double UploadMip(int mipIndex, out long uploadedBytes)
    {
        if (_capture == null || _context == null || _texture == null)
            throw new InvalidOperationException("The progressive shadow texture is not initialized.");
        if (mipIndex < 0 || mipIndex >= _capture.Mips.Length)
            throw new ArgumentOutOfRangeException(nameof(mipIndex));

        NativeTextureMipPayload mip = _capture.Mips[mipIndex];
        byte[] data = mip.Data ?? throw new InvalidOperationException($"Mip {mipIndex} payload was already released.");
        if (data.LongLength != mip.SlicePitch || mip.RowPitch == 0 || mip.SlicePitch == 0)
            throw new InvalidOperationException($"Mip {mipIndex} payload length or pitch no longer matches its captured metadata.");

        var timer = Stopwatch.StartNew();
        unsafe
        {
            fixed (byte* dataPointer = data)
            {
                _context.UpdateSubresource(_texture, checked((uint)mipIndex), null,
                    (IntPtr)dataPointer, mip.RowPitch, mip.SlicePitch);
            }
        }
        timer.Stop();

        uploadedBytes = data.LongLength;
        mip.ReleaseData();
        return timer.Elapsed.TotalMilliseconds;
    }

    private void CreateOrExpandShaderResourceView(int firstVisibleMip)
    {
        if (_device == null || _texture == null)
            throw new InvalidOperationException("The progressive shadow texture is not initialized.");
        if (firstVisibleMip < 0 || firstVisibleMip >= _mipCount)
            throw new ArgumentOutOfRangeException(nameof(firstVisibleMip));
        if (_shaderResourceView != null && firstVisibleMip == _viewFirstMip)
            return;

        var description = new ShaderResourceViewDescription(
            Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
            Format.BC7_UNorm,
            checked((uint)firstVisibleMip),
            checked((uint)(_mipCount - firstVisibleMip)));
        ID3D11ShaderResourceView nextView = _device.CreateShaderResourceView(_texture, description);

        ID3D11ShaderResourceView? previousView = _shaderResourceView;
        _shaderResourceView = nextView;
        _viewFirstMip = firstVisibleMip;
        if (previousView != null)
            _retiredShaderResourceViews.Add(previousView);
    }

    private static void ValidateCapture(NativeTextureMipPayloadCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Format != 98)
            throw new NotSupportedException($"The shadow preview currently supports BC7_UNORM only; DXGI format {capture.Format} was captured.");
        if (capture.Width == 0 || capture.Height == 0 || capture.Width > 16384 || capture.Height > 16384)
            throw new InvalidOperationException("The captured texture dimensions are outside the supported range.");
        if (capture.Mips.Length == 0 || capture.Mips.Length > 13)
            throw new InvalidOperationException("The captured mip count is outside the supported range.");

        long totalBytes = 0;
        for (int i = 0; i < capture.Mips.Length; i++)
        {
            NativeTextureMipPayload mip = capture.Mips[i];
            uint expectedWidth = Math.Max(1u, capture.Width >> i);
            uint expectedHeight = Math.Max(1u, capture.Height >> i);
            uint expectedRowPitch = Math.Max(1u, (expectedWidth + 3u) / 4u) * 16u;
            uint expectedSlicePitch = expectedRowPitch * Math.Max(1u, (expectedHeight + 3u) / 4u);
            if (mip.MipIndex != i || mip.Width != expectedWidth || mip.Height != expectedHeight
                || mip.RowPitch != expectedRowPitch || mip.SlicePitch != expectedSlicePitch
                || mip.Data == null || mip.Data.LongLength != expectedSlicePitch)
            {
                throw new InvalidOperationException($"Captured Mip {i} failed its BC7 dimension/pitch/payload validation.");
            }
            totalBytes = checked(totalBytes + mip.Data.LongLength);
        }

        if (totalBytes > 64L * 1024L * 1024L)
            throw new InvalidOperationException("The captured mip payload exceeds the 64 MiB shadow-preview safety limit.");
    }

    private double EstimateUploadMilliseconds(long bytes) =>
        (_estimatedUploadMillisecondsPerMiB * (bytes / (1024d * 1024d))) + UploadEstimateOverheadMilliseconds;

    private void ReleaseResources()
    {
        // ImGui draw commands can still reference old SRVs until the renderer consumes the draw list.
        // Keep replaced views alive until Reset/Dispose, then release every plugin-owned COM ref.
        _shaderResourceView?.Dispose();
        _shaderResourceView = null;
        foreach (ID3D11ShaderResourceView retiredView in _retiredShaderResourceViews)
            retiredView.Dispose();
        _retiredShaderResourceViews.Clear();
        _texture?.Dispose();
        _texture = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    private static string FormatBytes(long bytes)
    {
        const double KiB = 1024d;
        const double MiB = KiB * 1024d;
        return bytes >= MiB ? $"{bytes / MiB:F2} MiB" : bytes >= KiB ? $"{bytes / KiB:F1} KiB" : $"{bytes} B";
    }
}
