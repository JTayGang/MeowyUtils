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

/// <summary>Result of one UpdateSubresource call on a plugin-owned D3D11 texture.</summary>
public sealed class TextureGpuUploadReport
{
    public int MipIndex { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public long Bytes { get; init; }
    public double SubmissionMilliseconds { get; init; }
    public int UploadedMipCount { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>Result of copying one uploaded mip to a CPU-readable staging resource and comparing it.</summary>
public sealed class TextureGpuReadbackReport
{
    public int MipIndex { get; init; }
    public long ComparedBytes { get; init; }
    public long MismatchOffset { get; init; } = -1;
    public uint MappedRowPitch { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public bool Matches { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// A guarded D3D11 proof-of-concept. It creates a new texture owned by this plugin and uploads
/// individual BC7 mip subresources. It never reads or modifies an FFXIV-owned texture resource.
/// All D3D11 immediate-context operations are called from the UI/framework draw thread.
/// </summary>
public sealed class TextureGpuUploadProbe : IDisposable
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly HashSet<int> _uploadedMips = new();
    // ImGui draw commands retain the SRV pointer until the renderer consumes the draw list.
    // Keep replaced views alive until Reset/Dispose rather than releasing them mid-frame.
    private readonly List<ID3D11ShaderResourceView> _retiredShaderResourceViews = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _shaderResourceView;
    private TextureFileReport? _sourceReport;
    private byte[]? _lastUploadedMipData;
    private string _status = "No GPU test texture created.";
    private string? _error;
    private TextureGpuUploadReport? _lastUpload;
    private TextureGpuReadbackReport? _lastReadback;
    private string? _previewError;

    public TextureGpuUploadProbe(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
    }

    public bool IsCreated => _texture != null;
    public string Status => _status;
    public string? Error => _error;
    public TextureGpuUploadReport? LastUpload => _lastUpload;
    public TextureGpuReadbackReport? LastReadback => _lastReadback;
    public int UploadedMipCount => _uploadedMips.Count;
    public bool IsPreviewReady => _shaderResourceView != null;
    public int PreviewFirstMip { get; private set; } = -1;
    public int PreviewMipCount { get; private set; }
    public string? PreviewError => _previewError;

    /// <summary>Returns the native D3D11 SRV pointer in Dalamud ImGui's opaque texture-ID representation.</summary>
    public ImTextureID PreviewTextureId
    {
        get
        {
            if (_shaderResourceView == null)
                return default;

            // Dalamud's ImGui D3D11 backend expects an ID3D11ShaderResourceView pointer.
            // ImTextureID is an opaque pointer-sized identifier; reinterpret the pointer bits.
            nint nativePointer = _shaderResourceView.NativePointer;
            return Unsafe.As<nint, ImTextureID>(ref nativePointer);
        }
    }

    public void Create(TextureFileReport report)
    {
        Reset();

        if (((Lumina.Data.Files.TexFile.Attribute)report.TypeFlags & Lumina.Data.Files.TexFile.Attribute.TextureTypeMask)
            != Lumina.Data.Files.TexFile.Attribute.TextureType2D)
        {
            throw new NotSupportedException("The GPU upload prototype currently supports ordinary 2D textures only.");
        }

        // Start with BC7 because the current validated test asset is a BC7 hair-mask texture.
        // Other game formats will be added only after their DXGI mappings are verified.
        if (!string.Equals(report.Format, "BC7", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"The GPU upload prototype currently supports BC7 only; this texture is {report.Format}.");

        if (report.Width <= 0 || report.Height <= 0 || report.MipCount <= 0 || report.MipCount > 13)
            throw new InvalidOperationException("The texture dimensions or mip count are outside the supported range.");

        nint borrowedDevice = _pluginInterface.UiBuilder.DeviceHandle;
        if (borrowedDevice == 0)
            throw new InvalidOperationException("Dalamud has not exposed the game's D3D11 device yet. Try again while the UI is drawing.");

        Guid iid = typeof(ID3D11Device).GUID;
        int queryResult = Marshal.QueryInterface(borrowedDevice, in iid, out nint ownedDeviceReference);
        if (queryResult < 0)
            Marshal.ThrowExceptionForHR(queryResult);
        if (ownedDeviceReference == 0)
            throw new InvalidOperationException("QueryInterface returned an empty D3D11 device reference.");

        try
        {
            // QueryInterface returned an owned COM reference. Vortice's pointer constructor
            // wraps it without adding another reference; disposing the wrapper releases it.
            _device = new ID3D11Device(ownedDeviceReference);
            _context = _device.ImmediateContext;

            _texture = _device.CreateTexture2D(
                Format.BC7_UNorm,
                checked((uint)report.Width),
                checked((uint)report.Height),
                arraySize: 1,
                mipLevels: checked((uint)report.MipCount),
                initialData: null,
                bindFlags: BindFlags.ShaderResource);

            _sourceReport = report;
            _status = $"Plugin-owned BC7 D3D11 texture created: {report.Width}×{report.Height}, {report.MipCount} mip levels. The full mip chain is allocated; no mip payload has been uploaded yet. Estimated logical mip payload: {FormatBytes(report.TotalMipBytes)}.";
            _error = null;
            _lastUpload = null;
            _lastReadback = null;
            _previewError = null;
        }
        catch
        {
            // If the wrapper was not constructed, QueryInterface's reference still belongs to us.
            if (_device == null)
                Marshal.Release(ownedDeviceReference);

            ReleaseResources();
            throw;
        }
    }

    public TextureGpuUploadReport Upload(TextureFileReport report, TextureMipReadReport mipRead, bool refreshPreview = true)
    {
        if (_texture == null || _context == null || _sourceReport == null)
            throw new InvalidOperationException("Create the plugin-owned GPU test texture first.");

        if (!ReferenceEquals(_sourceReport, report) &&
            (!string.Equals(_sourceReport.Path, report.Path, StringComparison.OrdinalIgnoreCase) ||
             _sourceReport.Width != report.Width || _sourceReport.Height != report.Height || _sourceReport.MipCount != report.MipCount))
        {
            throw new InvalidOperationException("The GPU test texture belongs to a different inspected file. Reset it and create a new test texture.");
        }

        int mipIndex = mipRead.MipIndex;
        if (mipIndex < 0 || mipIndex >= report.Mips.Count)
            throw new InvalidOperationException("The selected mip index is outside the texture's mip chain.");

        byte[] data = mipRead.Data;
        var mip = report.Mips[mipIndex];
        if (data.LongLength != mip.Bytes || data.LongLength != mipRead.DecompressedBytes)
            throw new InvalidOperationException($"Mip {mipIndex} data length ({data.LongLength:N0}) does not match the expected payload ({mip.Bytes:N0}). Read the mip again before uploading.");

        // BC7 stores 4×4 texel blocks of 16 bytes each. D3D11 expects pitches in bytes,
        // with the row pitch measured in compressed block rows rather than texel rows.
        uint blocksWide = checked((uint)Math.Max(1, (mip.Width + 3) / 4));
        uint blocksHigh = checked((uint)Math.Max(1, (mip.Height + 3) / 4));
        uint rowPitch = checked(blocksWide * 16u);
        uint depthPitch = checked(rowPitch * blocksHigh);
        if (depthPitch != data.LongLength)
            throw new InvalidOperationException($"BC7 pitch calculation produced {depthPitch:N0} bytes for Mip {mipIndex}, but the archive reader produced {data.LongLength:N0} bytes. Upload was rejected.");

        var timer = Stopwatch.StartNew();
        unsafe
        {
            fixed (byte* dataPointer = data)
            {
                // Array size is one, so D3D11CalcSubresource(mip, 0, mipCount) == mip.
                _context.UpdateSubresource(_texture, checked((uint)mipIndex), null, (IntPtr)dataPointer, rowPitch, depthPitch);
            }
        }
        timer.Stop();

        _uploadedMips.Add(mipIndex);
        // Retain the archive reader's newly allocated mip buffer directly. The report owns
        // this immutable payload for the lifetime of the read result, so another managed
        // copy would only duplicate potentially large mip data without improving safety.
        _lastUploadedMipData = data;
        _lastReadback = null;
        _lastUpload = new TextureGpuUploadReport
        {
            MipIndex = mipIndex,
            Width = mip.Width,
            Height = mip.Height,
            Bytes = data.LongLength,
            SubmissionMilliseconds = timer.Elapsed.TotalMilliseconds,
            UploadedMipCount = _uploadedMips.Count,
            Message = $"Submitted Mip {mipIndex} ({mip.Width}×{mip.Height}, {FormatBytes(data.LongLength)}) to subresource {mipIndex}. {_uploadedMips.Count} of {report.MipCount} mip levels have been uploaded.",
        };

        _status = _lastUpload.Message;
        _error = null;
        if (refreshPreview)
            UpdateProgressivePreviewShaderView(mipIndex);
        return _lastUpload;
    }

    /// <summary>Refreshes the SRV after a batch of mip uploads, exposing only initialized contiguous levels.</summary>
    public void RefreshProgressivePreview(int newestMipIndex) => UpdateProgressivePreviewShaderView(newestMipIndex);

    /// <summary>
    /// Copies only the most recently uploaded mip to a CPU-readable staging texture and compares
    /// each BC7 block row against the exact bytes submitted to UpdateSubresource. Map(Read) is
    /// synchronous and can wait for preceding GPU work, so this is a manual diagnostic action.
    /// </summary>
    public TextureGpuReadbackReport VerifyLastUpload()
    {
        if (_device == null || _context == null || _texture == null || _sourceReport == null)
            throw new InvalidOperationException("Create the plugin-owned GPU test texture first.");
        if (_lastUpload == null || _lastUploadedMipData == null)
            throw new InvalidOperationException("Upload a mip before requesting GPU readback verification.");

        int mipIndex = _lastUpload.MipIndex;
        var mip = _sourceReport.Mips[mipIndex];
        uint blocksWide = checked((uint)Math.Max(1, (mip.Width + 3) / 4));
        uint blockRows = checked((uint)Math.Max(1, (mip.Height + 3) / 4));
        uint expectedRowPitch = checked(blocksWide * 16u);
        long expectedByteCount = checked((long)expectedRowPitch * blockRows);
        if (expectedByteCount != _lastUploadedMipData.LongLength)
            throw new InvalidOperationException("The retained upload payload no longer matches the expected BC7 mip layout.");

        _lastReadback = null;

        // Keep the staging resource's full mip-chain description valid for BC formats whose
        // smallest mip dimensions are below one 4×4 compression block. Only the selected
        // subresource is copied and mapped; the other staging subresources are left untouched.
        using ID3D11Texture2D staging = _device.CreateTexture2D(
            Format.BC7_UNorm,
            checked((uint)_sourceReport.Width),
            checked((uint)_sourceReport.Height),
            arraySize: 1,
            mipLevels: checked((uint)_sourceReport.MipCount),
            initialData: null,
            bindFlags: BindFlags.None,
            usage: ResourceUsage.Staging,
            cpuAccessFlags: CpuAccessFlags.Read);

        var timer = Stopwatch.StartNew();
        uint subresource = checked((uint)mipIndex);
        _context.CopySubresourceRegion(staging, subresource, 0, 0, 0, _texture, subresource, null);

        MappedSubresource mapped = _context.Map(staging, subresource, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        bool matches = true;
        long mismatchOffset = -1;
        try
        {
            if (mapped.DataPointer == IntPtr.Zero)
                throw new InvalidOperationException("D3D11 mapped the staging subresource but returned a null data pointer.");
            if (mapped.RowPitch < expectedRowPitch)
                throw new InvalidOperationException($"D3D11 returned a mapped row pitch of {mapped.RowPitch}, smaller than the expected BC7 row size of {expectedRowPitch} bytes.");

            unsafe
            {
                byte* mappedBase = (byte*)mapped.DataPointer.ToPointer();
                int rowBytes = checked((int)expectedRowPitch);
                for (uint row = 0; row < blockRows; row++)
                {
                    int mappedOffset = checked((int)(row * mapped.RowPitch));
                    int expectedOffset = checked((int)(row * expectedRowPitch));
                    var gpuRow = new ReadOnlySpan<byte>(mappedBase + mappedOffset, rowBytes);
                    var expectedRow = _lastUploadedMipData.AsSpan(expectedOffset, rowBytes);
                    if (gpuRow.SequenceEqual(expectedRow))
                        continue;

                    matches = false;
                    for (int column = 0; column < rowBytes; column++)
                    {
                        if (gpuRow[column] == expectedRow[column])
                            continue;
                        mismatchOffset = (long)row * expectedRowPitch + column;
                        break;
                    }
                    break;
                }
            }
        }
        finally
        {
            _context.Unmap(staging, subresource);
        }

        timer.Stop();
        string message = matches
            ? $"GPU readback verified Mip {mipIndex}: all {_lastUploadedMipData.LongLength:N0} bytes match the uploaded BC7 payload."
            : $"GPU readback mismatch in Mip {mipIndex} at byte offset 0x{mismatchOffset:X}. The GPU subresource does not match the submitted payload.";

        _lastReadback = new TextureGpuReadbackReport
        {
            MipIndex = mipIndex,
            ComparedBytes = _lastUploadedMipData.LongLength,
            MismatchOffset = mismatchOffset,
            MappedRowPitch = mapped.RowPitch,
            ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            Matches = matches,
            Message = message,
        };
        _error = null;
        _status = message;
        return _lastReadback;
    }

    public void SetError(string message)
    {
        _error = message;
    }

    public void Reset()
    {
        ReleaseResources();
        _sourceReport = null;
        _uploadedMips.Clear();
        _lastUploadedMipData = null;
        _lastUpload = null;
        _lastReadback = null;
        _previewError = null;
        _error = null;
        _status = "No GPU test texture created.";
    }

    public void Dispose() => Reset();

    private void UpdateProgressivePreviewShaderView(int newlyUploadedMip)
    {
        if (_device == null || _texture == null || _sourceReport == null)
            return;

        // Expose only a contiguous range of initialized mips. This prevents the ImGui sampler
        // from ever seeing uninitialized subresources while the coarse-to-fine sequence runs.
        // In the normal sequence, the range grows from [lastMip, lastMip] toward [0, lastMip].
        // For manual/out-of-order uploads, expose the contiguous run containing the newest mip.
        int firstMip = newlyUploadedMip;
        int lastMip = newlyUploadedMip;
        while (firstMip > 0 && _uploadedMips.Contains(firstMip - 1))
            firstMip--;
        while (lastMip < _sourceReport.MipCount - 1 && _uploadedMips.Contains(lastMip + 1))
            lastMip++;

        int mipCount = lastMip - firstMip + 1;
        if (_shaderResourceView != null && PreviewFirstMip == firstMip && PreviewMipCount == mipCount)
            return;

        try
        {
            // D3D11's SRV range is relative to the original resource. Restricting MostDetailedMip
            // and MipLevels ensures only the uploaded contiguous range can be sampled. As finer
            // mips arrive, replace the view with a wider range; the texture allocation itself is
            // unchanged and still reserves the complete mip chain.
            // Use the dimension-based constructor available in Vortice 3.8.3.
            // The texture is supplied separately to CreateShaderResourceView below.
            var viewDescription = new ShaderResourceViewDescription(
                Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                Format.BC7_UNorm,
                checked((uint)firstMip),
                checked((uint)mipCount));
            ID3D11ShaderResourceView nextView = _device.CreateShaderResourceView(_texture, viewDescription);

            // Publish the new view only after creation succeeds. Disposing our old COM reference
            // does not invalidate commands already submitted by D3D11, which retain their own refs.
            ID3D11ShaderResourceView? previousView = _shaderResourceView;
            _shaderResourceView = nextView;
            PreviewFirstMip = firstMip;
            PreviewMipCount = mipCount;
            if (previousView != null)
                _retiredShaderResourceViews.Add(previousView);

            _previewError = null;
            _status = mipCount == _sourceReport.MipCount
                ? $"Full mip chain uploaded; progressive preview now exposes Mip {firstMip} through Mip {lastMip}."
                : $"Progressive preview ready: {mipCount} initialized mip level(s), Mip {firstMip} through Mip {lastMip}.";
        }
        catch (Exception ex)
        {
            // Keep the previous valid view if expansion fails; it still references only mips
            // known to be initialized. Upload verification remains independent of preview setup.
            _previewError = ex.Message;
        }
    }

    private void ReleaseResources()
    {
        // The SRV holds a reference to the texture, so release the view before its resource.
        _shaderResourceView?.Dispose();
        _shaderResourceView = null;
        foreach (ID3D11ShaderResourceView retiredView in _retiredShaderResourceViews)
            retiredView.Dispose();
        _retiredShaderResourceViews.Clear();
        PreviewFirstMip = -1;
        PreviewMipCount = 0;
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
