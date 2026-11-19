using System;

namespace FPSBooster;

/// <summary>
/// A one-shot, explicitly requested copy of the mip payload supplied to FFXIV's native
/// CreateTexture2D call. The native source pointers are copied while the call is active;
/// this object contains managed byte arrays only and never retains native pointers.
/// </summary>
public sealed class NativeTextureMipPayloadCapture
{
    public NativeTextureMipPayloadCapture(
        DateTime capturedUtc,
        uint resourceId,
        string resourceFileName,
        uint width,
        uint height,
        uint format,
        ulong sourcePayloadLength,
        double copyMilliseconds,
        NativeTextureMipPayload[] mips)
    {
        CapturedUtc = capturedUtc;
        ResourceId = resourceId;
        ResourceFileName = resourceFileName;
        Width = width;
        Height = height;
        Format = format;
        SourcePayloadLength = sourcePayloadLength;
        CopyMilliseconds = copyMilliseconds;
        Mips = mips;
    }

    public DateTime CapturedUtc { get; }
    public uint ResourceId { get; }
    public string ResourceFileName { get; }
    public uint Width { get; }
    public uint Height { get; }
    public uint Format { get; }
    public ulong SourcePayloadLength { get; }
    public double CopyMilliseconds { get; }
    public NativeTextureMipPayload[] Mips { get; }

    public long TotalCopiedBytes
    {
        get
        {
            long total = 0;
            foreach (NativeTextureMipPayload mip in Mips)
                total = checked(total + mip.ByteLength);
            return total;
        }
    }
}

/// <summary>One copied BC7 mip payload and its validated D3D11 upload layout.</summary>
public sealed class NativeTextureMipPayload
{
    public NativeTextureMipPayload(
        int mipIndex,
        uint width,
        uint height,
        uint rowPitch,
        uint slicePitch,
        ulong sourcePayloadOffset,
        byte[] data)
    {
        MipIndex = mipIndex;
        Width = width;
        Height = height;
        RowPitch = rowPitch;
        SlicePitch = slicePitch;
        SourcePayloadOffset = sourcePayloadOffset;
        Data = data;
        ByteLength = data.LongLength;
    }

    public int MipIndex { get; }
    public uint Width { get; }
    public uint Height { get; }
    public uint RowPitch { get; }
    public uint SlicePitch { get; }
    public ulong SourcePayloadOffset { get; }
    public byte[]? Data { get; private set; }
    public long ByteLength { get; }

    public void ReleaseData() => Data = null;
}
