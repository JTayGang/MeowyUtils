using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FPSBooster;

/// <summary>
/// Converts a single-channel scene-depth SRV into a plugin-owned grayscale texture.
/// This avoids ImGui showing R16/R24 depth data as a red-only image. The source remains
/// read-only and is never used as an output target.
/// </summary>
internal sealed class SceneDepthPreviewRenderer : IDisposable
{
    private const string ShaderSource = """
        struct VertexOutput
        {
            float4 Position : SV_Position;
            float2 Uv : TEXCOORD0;
        };

        VertexOutput VSMain(uint vertexId : SV_VertexID)
        {
            float2 p = float2((vertexId << 1) & 2, vertexId & 2);
            VertexOutput output;
            output.Position = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
            output.Uv = p;
            return output;
        }

        Texture2D<float> DepthInput : register(t0);

        float4 PSMain(VertexOutput input) : SV_Target
        {
            uint width;
            uint height;
            DepthInput.GetDimensions(width, height);
            int2 maximum = int2(width, height) - 1;
            int2 pixel = clamp(int2(input.Uv * float2(width, height)), int2(0, 0), maximum);
            float depth = saturate(DepthInput.Load(int3(pixel, 0)));

            // FFXIV's observed path is reversed-Z: larger values are nearer. A sublinear
            // curve makes distant geometry visible without discarding the original ordering.
            float gray = pow(depth, 0.35);
            return float4(gray, gray, gray, 1.0);
        }
        """;

    private readonly FluidSimulationProbe _fluid;
    private ID3D11DeviceContext? _deferredContext;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11Texture2D? _previewTexture;
    private ID3D11ShaderResourceView? _previewSrv;
    private ID3D11RenderTargetView? _previewRtv;
    private uint _previewWidth;
    private uint _previewHeight;
    private string _status = "No depth image converted yet.";
    private bool _disposed;

    public SceneDepthPreviewRenderer(FluidSimulationProbe fluid) => _fluid = fluid;

    public bool IsAvailable => _previewSrv != null;
    public string Status => _status;

    public ImTextureID TextureId
    {
        get
        {
            nint pointer = _previewSrv?.NativePointer ?? 0;
            return Unsafe.As<nint, ImTextureID>(ref pointer);
        }
    }

    public Vector2 ImageSize(float maximumWidth)
    {
        if (_previewWidth == 0 || _previewHeight == 0)
            return new Vector2(maximumWidth, maximumWidth * 0.5625f);
        return new Vector2(maximumWidth, maximumWidth * _previewHeight / _previewWidth);
    }

    public bool Render(nint sourceSrvPointer, uint sourceWidth, uint sourceHeight)
    {
        if (_disposed || sourceSrvPointer == 0 || sourceWidth == 0 || sourceHeight == 0)
            return false;

        ID3D11Device? device = _fluid.Device;
        ID3D11DeviceContext? immediate = _fluid.ImmediateContext;
        if (device == null || immediate == null)
        {
            _status = "Fluid Lab D3D11 device is unavailable.";
            return false;
        }

        try
        {
            EnsurePipeline(device);
            EnsureOutput(device, sourceWidth, sourceHeight);

            // The capture probe owns the SRV reference. AddRef before wrapping it so the
            // temporary Vortice wrapper owns an independent COM reference for this pass.
            Marshal.AddRef(sourceSrvPointer);
            using var sourceSrv = new ID3D11ShaderResourceView(sourceSrvPointer);

            _deferredContext!.OMSetRenderTargets(_previewRtv!);
            _deferredContext.RSSetViewport(0f, 0f, _previewWidth, _previewHeight);
            _deferredContext.IASetInputLayout(null);
            _deferredContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _deferredContext.VSSetShader(_vertexShader!);
            _deferredContext.PSSetShader(_pixelShader!);
            _deferredContext.PSSetShaderResources(0, new[] { sourceSrv });
            _deferredContext.Draw(3, 0);
            _deferredContext.PSUnsetShaderResources(0, 1);
            _deferredContext.UnsetRenderTargets();

            using ID3D11CommandList commandList = _deferredContext.FinishCommandList(false);
            immediate.ExecuteCommandList(commandList, true);
            _status = $"Converted {sourceWidth}×{sourceHeight} depth to {_previewWidth}×{_previewHeight} reversed-Z grayscale.";
            return true;
        }
        catch (Exception ex)
        {
            _status = "Depth grayscale conversion failed: " + ex.GetBaseException().Message;
            return false;
        }
    }

    private void EnsurePipeline(ID3D11Device device)
    {
        if (_deferredContext == null)
            _deferredContext = device.CreateDeferredContext();
        if (_vertexShader == null)
            _vertexShader = device.CreateVertexShader(CompileShader("VSMain", "vs_5_0"));
        if (_pixelShader == null)
            _pixelShader = device.CreatePixelShader(CompileShader("PSMain", "ps_5_0"));
    }

    private void EnsureOutput(ID3D11Device device, uint sourceWidth, uint sourceHeight)
    {
        const uint maximumDimension = 1024;
        float scale = Math.Min(1f, maximumDimension / (float)Math.Max(sourceWidth, sourceHeight));
        uint width = Math.Max(1u, (uint)MathF.Round(sourceWidth * scale));
        uint height = Math.Max(1u, (uint)MathF.Round(sourceHeight * scale));
        if (_previewTexture != null && _previewWidth == width && _previewHeight == height)
            return;

        ReleaseOutput();
        _previewTexture = device.CreateTexture2D(
            Format.R8G8B8A8_UNorm,
            width,
            height,
            arraySize: 1,
            mipLevels: 1,
            initialData: null,
            bindFlags: BindFlags.ShaderResource | BindFlags.RenderTarget,
            usage: ResourceUsage.Default);
        var srvDescription = new ShaderResourceViewDescription(
            ShaderResourceViewDimension.Texture2D,
            Format.R8G8B8A8_UNorm,
            0,
            1);
        var rtvDescription = new RenderTargetViewDescription(
            RenderTargetViewDimension.Texture2D,
            Format.R8G8B8A8_UNorm);
        _previewSrv = device.CreateShaderResourceView(_previewTexture, srvDescription);
        _previewRtv = device.CreateRenderTargetView(_previewTexture, rtvDescription);
        _previewWidth = width;
        _previewHeight = height;
    }

    private void ReleaseOutput()
    {
        _previewRtv?.Dispose();
        _previewRtv = null;
        _previewSrv?.Dispose();
        _previewSrv = null;
        _previewTexture?.Dispose();
        _previewTexture = null;
        _previewWidth = 0;
        _previewHeight = 0;
    }

    private static byte[] CompileShader(string entryPoint, string target)
    {
        byte[] source = Encoding.UTF8.GetBytes(ShaderSource);
        IntPtr compiledBlob = IntPtr.Zero;
        IntPtr errorBlob = IntPtr.Zero;
        try
        {
            int result;
            unsafe
            {
                fixed (byte* sourcePointer = source)
                {
                    result = D3DCompile(
                        (IntPtr)sourcePointer,
                        (nuint)source.Length,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        entryPoint,
                        target,
                        1u << 11,
                        0,
                        out compiledBlob,
                        out errorBlob);
                }
            }

            if (result < 0)
            {
                string details = errorBlob != IntPtr.Zero
                    ? Encoding.UTF8.GetString(CopyCompilerBlob(errorBlob)).TrimEnd('\0', '\r', '\n')
                    : "No compiler diagnostic was returned.";
                throw new InvalidOperationException($"HLSL compilation failed for {entryPoint} ({target}): {details} (HRESULT 0x{result:X8}).");
            }
            if (compiledBlob == IntPtr.Zero)
                throw new InvalidOperationException($"D3DCompile returned no bytecode for {entryPoint} ({target}).");
            return CopyCompilerBlob(compiledBlob);
        }
        finally
        {
            if (compiledBlob != IntPtr.Zero)
                Marshal.Release(compiledBlob);
            if (errorBlob != IntPtr.Zero)
                Marshal.Release(errorBlob);
        }
    }

    private static unsafe byte[] CopyCompilerBlob(IntPtr blob)
    {
        void** vtable = *(void***)blob;
        var getBufferPointer = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)vtable[3];
        var getBufferSize = (delegate* unmanaged[Stdcall]<IntPtr, nuint>)vtable[4];
        IntPtr data = getBufferPointer(blob);
        nuint nativeSize = getBufferSize(blob);
        if (nativeSize > int.MaxValue)
            throw new InvalidOperationException("D3DCompile returned an unexpectedly large blob.");
        int size = checked((int)nativeSize);
        byte[] bytes = new byte[size];
        if (size > 0)
            Marshal.Copy(data, bytes, 0, size);
        return bytes;
    }

    [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(
        IntPtr sourceData,
        nuint sourceDataSize,
        IntPtr sourceName,
        IntPtr defines,
        IntPtr include,
        [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
        [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1,
        uint flags2,
        out IntPtr code,
        out IntPtr errorMessages);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseOutput();
        _pixelShader?.Dispose();
        _pixelShader = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
        _deferredContext?.Dispose();
        _deferredContext = null;
    }
}
