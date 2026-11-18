using System;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace FPSBooster;

/// <summary>
/// A manually started native resource-handle observer.
/// GetAsync may reuse a cached handle or initiate the game's normal asynchronous load.
/// The probe holds the returned reference until Release/Dispose, and never writes native fields.
/// </summary>
public sealed unsafe class NativeTextureHandleProbe : IDisposable
{
    private const int ExpectedTextureHandleSize = 0x150;
    private const int ChangeLodFlagOffset = 0x104;
    private const int ChangeLodBit = 0x01;

    private TextureResourceHandle* _handle;
    private string _path = string.Empty;

    public bool IsAcquired => _handle != null;
    public string Path => _path;
    public string Status { get; private set; } = "No native texture handle acquired.";
    public string? Error { get; private set; }

    public void Acquire(string path)
    {
        Release();

        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter a valid game .tex path before acquiring a native texture handle.", nameof(path));

        if (sizeof(TextureResourceHandle) != ExpectedTextureHandleSize)
            throw new InvalidOperationException(
                $"The current TextureResourceHandle layout is {sizeof(TextureResourceHandle):X} bytes; expected 0x{ExpectedTextureHandleSize:X}. Native inspection has been stopped for layout safety.");

        // This is an explicit native resource request. The game may return an existing cached
        // handle or begin its normal asynchronous load. The returned reference is released once
        // through DecRef when the user resets the probe or the plugin is disposed.
        ResourceHandle* resource = ResourceHandle.GetAsync(path);
        if (resource == null)
            throw new InvalidOperationException("The native ResourceHandle.GetAsync call returned null.");

        ulong runtimeHandleSize;
        try
        {
            runtimeHandleSize = resource->GetHandleSize();
        }
        catch
        {
            // GetAsync returned a reference; balance it even if the runtime type check fails.
            resource->DecRef();
            throw;
        }

        if (runtimeHandleSize != ExpectedTextureHandleSize)
        {
            resource->DecRef();
            throw new InvalidOperationException(
                $"The native resource manager returned a handle of size 0x{runtimeHandleSize:X}, not the expected TextureResourceHandle size 0x{ExpectedTextureHandleSize:X}. The returned reference was released and no cast was performed.");
        }

        _handle = (TextureResourceHandle*)resource;
        _path = path;
        Error = null;
        Status = "Native asynchronous texture request accepted. Waiting for its persistent Texture pointer...";
    }

    /// <summary>Reads only persistent handle/header/texture fields. Never reads temporary Load-only pointers.</summary>
    public NativeTextureHandleSnapshot? Refresh()
    {
        if (_handle == null)
            return null;

        // ChangeLod is currently represented in FFXIVClientStructs as bit 0 at +0x104.
        // Read and report the byte only. This probe deliberately never sets or clears it.
        byte lodFlagByte = ((byte*)_handle)[ChangeLodFlagOffset];
        var header = _handle->Header;
        Texture* texture = _handle->Texture;

        var snapshot = new NativeTextureHandleSnapshot
        {
            Path = _path,
            HandleAddress = (nint)_handle,
            RuntimeHandleSize = _handle->GetHandleSize(),
            ReferenceCount = _handle->RefCount,
            FileSize = _handle->FileSize,
            FileSize2 = _handle->FileSize2,
            FileSize3 = _handle->FileSize3,
            LastIoResult = _handle->LastIOResult,
            LodFlagByte = lodFlagByte,
            ChangeLodBit = (lodFlagByte & ChangeLodBit) != 0,
            HeaderWidth = header.Width,
            HeaderHeight = header.Height,
            HeaderDepth = header.Depth,
            HeaderMipCountAndFlag = header.MipCountAndFlag,
            HeaderArraySize = header.ArraySize,
            HeaderFormat = header.Format.ToString(),
            HeaderType = header.Type.ToString(),
            HasTextureObject = texture != null,
            TextureObjectAddress = (nint)texture,
        };

        if (texture != null)
        {
            snapshot.ActualWidth = texture->ActualWidth;
            snapshot.ActualHeight = texture->ActualHeight;
            snapshot.AllocatedWidth = texture->AllocatedWidth;
            snapshot.AllocatedHeight = texture->AllocatedHeight;
            snapshot.Depth = texture->Depth;
            snapshot.TextureMipLevelRaw = texture->MipLevel;
            snapshot.TextureFormat = texture->TextureFormat.ToString();
            snapshot.TextureFlags = texture->Flags.ToString();
            snapshot.ArraySize = texture->ArraySize;
            snapshot.D3D11TextureAddress = (nint)texture->D3D11Texture2D;
            snapshot.D3D11ShaderResourceViewAddress = (nint)texture->D3D11ShaderResourceView;
        }

        Status = texture == null
            ? "Native handle acquired; persistent Texture pointer is not available yet."
            : snapshot.D3D11TextureAddress != 0
                ? "Native Texture object and D3D11 texture pointer are available."
                : "Native Texture object is available; D3D11 texture pointer is still null.";

        return snapshot;
    }

    public void SetError(string message) => Error = message;

    public void Release()
    {
        TextureResourceHandle* handle = _handle;
        _handle = null;
        string releasedPath = _path;
        _path = string.Empty;

        if (handle != null)
        {
            try
            {
                // GetAsync returns the resource reference owned by this probe. Do not call Close:
                // DecRef is the normal reference-release operation for ResourceHandle consumers.
                ((ResourceHandle*)handle)->DecRef();
                Status = $"Released the probe's native resource reference for {releasedPath}.";
                Error = null;
            }
            catch (Exception ex)
            {
                Status = "The native handle reference was detached from the probe, but DecRef reported an error.";
                Error = ex.Message;
            }
        }
        else
        {
            Status = "No native texture handle acquired.";
            Error = null;
        }
    }

    public void Dispose() => Release();
}

public sealed class NativeTextureHandleSnapshot
{
    public string Path { get; init; } = string.Empty;
    public nint HandleAddress { get; init; }
    public ulong RuntimeHandleSize { get; init; }
    public uint ReferenceCount { get; init; }
    public uint FileSize { get; init; }
    public uint FileSize2 { get; init; }
    public uint FileSize3 { get; init; }
    public byte LastIoResult { get; init; }
    public byte LodFlagByte { get; init; }
    public bool ChangeLodBit { get; init; }
    public ushort HeaderWidth { get; init; }
    public ushort HeaderHeight { get; init; }
    public ushort HeaderDepth { get; init; }
    public byte HeaderMipCountAndFlag { get; init; }
    public byte HeaderArraySize { get; init; }
    public string HeaderFormat { get; init; } = string.Empty;
    public string HeaderType { get; init; } = string.Empty;
    public bool HasTextureObject { get; init; }
    public nint TextureObjectAddress { get; init; }
    public uint ActualWidth { get; set; }
    public uint ActualHeight { get; set; }
    public uint AllocatedWidth { get; set; }
    public uint AllocatedHeight { get; set; }
    public uint Depth { get; set; }
    public byte TextureMipLevelRaw { get; set; }
    public string TextureFormat { get; set; } = string.Empty;
    public string TextureFlags { get; set; } = string.Empty;
    public byte ArraySize { get; set; }
    public nint D3D11TextureAddress { get; set; }
    public nint D3D11ShaderResourceViewAddress { get; set; }
}
