using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Bindings.ImGui;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FPSBooster;

/// <summary>
/// Rasterizes the grounded liquid's projected mesh into a small plugin-owned mapping texture,
/// then composites the fluid display texture against the captured scene depth SRV. The game
/// depth resource is read-only; all render targets and mapping textures are plugin-owned.
/// FFXIV's observed depth is reversed-Z, so a larger scene depth value is closer to the camera.
/// </summary>
internal sealed class GroundedFluidDepthRenderer : IDisposable
{
    private const int MaximumTargetDimension = 1024;

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

        Texture2D<float4> PatchMap : register(t0);   // liquid U, liquid V, projected device depth, coverage
        Texture2D<float4> FluidInput : register(t1);
        Texture2D<float> SceneDepth : register(t2);
        Texture2D<float4> Parameters : register(t3);

        float4 LoadFluidClamped(int2 pixel, uint width, uint height)
        {
            int2 maximum = int2(width, height) - 1;
            return FluidInput.Load(int3(clamp(pixel, int2(0, 0), maximum), 0));
        }

        float4 SampleFluidLinear(float2 uv)
        {
            uint width;
            uint height;
            FluidInput.GetDimensions(width, height);
            float2 texel = saturate(uv) * float2(width, height) - 0.5;
            int2 basePixel = int2(floor(texel));
            float2 blend = frac(texel);
            float4 a = LoadFluidClamped(basePixel, width, height);
            float4 b = LoadFluidClamped(basePixel + int2(1, 0), width, height);
            float4 c = LoadFluidClamped(basePixel + int2(0, 1), width, height);
            float4 d = LoadFluidClamped(basePixel + int2(1, 1), width, height);
            return lerp(lerp(a, b, blend.x), lerp(c, d, blend.x), blend.y);
        }

        float4 PSMain(VertexOutput input) : SV_Target
        {
            uint outputWidth;
            uint outputHeight;
            PatchMap.GetDimensions(outputWidth, outputHeight);
            int2 outputPixel = clamp(int2(input.Position.xy), int2(0, 0), int2(outputWidth, outputHeight) - 1);
            float4 patch = PatchMap.Load(int3(outputPixel, 0));
            if (patch.w < 0.5)
                return float4(0.0, 0.0, 0.0, 0.0);

            float4 screenRect = Parameters.Load(int3(0, 0, 0));
            float4 dimensions = Parameters.Load(int3(1, 0, 0));
            float4 options = Parameters.Load(int3(2, 0, 0));

            // Map this output pixel back to the main ImGui viewport, then into the
            // captured scene-depth resource. The viewport and depth texture may differ
            // in resolution (e.g. the half-resolution 1720x720 intermediate).
            float2 localUv = input.Position.xy / float2(outputWidth, outputHeight);
            float2 screenPixel = screenRect.xy + localUv * screenRect.zw;
            float2 depthUv = screenPixel / max(dimensions.xy, float2(1.0, 1.0));
            int2 depthSize = int2(dimensions.zw);
            int2 depthPixel = clamp(int2(depthUv * dimensions.zw), int2(0, 0), depthSize - 1);

            // Use the exact projected pixel by default. A conservative 2x2 reversed-Z
            // maximum is available as an optional mode, but can over-occlude a thin liquid
            // surface when a neighboring pixel belongs to the ground/character silhouette.
            int2 depthMaximum = depthSize - 1;
            float sceneDepth = SceneDepth.Load(int3(depthPixel, 0));
            if (options.w > 0.5)
            {
                sceneDepth = max(sceneDepth, SceneDepth.Load(int3(min(depthPixel + int2(1, 0), depthMaximum), 0)));
                sceneDepth = max(sceneDepth, SceneDepth.Load(int3(min(depthPixel + int2(0, 1), depthMaximum), 0)));
                sceneDepth = max(sceneDepth, SceneDepth.Load(int3(min(depthPixel + int2(1, 1), depthMaximum), 0)));
            }

            // Both values are in the exact same reversed-Z device-depth space:
            // the liquid depth was projected with Scene.View × Render.Projection,
            // and sceneDepth came from the matching single-sample scene DSV. In
            // reversed-Z, a larger value is closer to the camera. Comparing the raw
            // values preserves the hardware depth ordering at every camera distance;
            // the tiny bias only absorbs quantization/raster-edge noise.
            bool hasSceneSurface = sceneDepth > 0.000001;
            float depthDelta = sceneDepth - patch.z;
            bool occluded = hasSceneSurface && depthDelta > options.x;

            // Diagnostic modes intentionally bypass liquid alpha and depth rejection so
            // we can inspect the two device-depth values and their raw difference.
            int debugMode = (int)round(options.z);
            if (debugMode == 1) // Projected liquid device depth.
            {
                float gray = pow(saturate(patch.z), 0.35);
                return float4(gray, gray, gray, 0.95);
            }
            if (debugMode == 2) // Scene device depth sampled at the projected liquid pixel.
            {
                float gray = pow(saturate(sceneDepth), 0.35);
                return float4(gray, gray, gray, 0.95);
            }
            if (debugMode == 3) // Green = visible, red = occluded.
            {
                return occluded ? float4(1.0, 0.06, 0.02, 0.95) : float4(0.05, 1.0, 0.12, 0.95);
            }
            if (debugMode == 4) // Red = scene in front, blue = liquid in front; raw reversed-Z device-depth difference.
            {
                float magnitude = saturate(abs(depthDelta) * 80.0);
                return depthDelta > options.x
                    ? float4(magnitude, 0.03, 0.01, 0.95)
                    : float4(0.02, 0.08, magnitude, 0.95);
            }
            if (debugMode == 5) // Verify the fluid display texture and its alpha independently.
            {
                float4 debugFluid = SampleFluidLinear(patch.xy);
                return float4(debugFluid.rgb, max(debugFluid.a, 0.15));
            }
            if (debugMode == 6) // Fluid alpha as grayscale.
            {
                float alpha = SampleFluidLinear(patch.xy).a;
                return float4(alpha, alpha, alpha, 0.95);
            }

            if (occluded)
                return float4(0.0, 0.0, 0.0, 0.0);

            float4 liquid = SampleFluidLinear(patch.xy);
            liquid.a *= options.y;
            if (liquid.a < 0.002)
                return float4(0.0, 0.0, 0.0, 0.0);
            return liquid;
        }
        """;

    private readonly FluidSimulationProbe _fluid;
    private ID3D11DeviceContext? _deferredContext;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11Texture2D? _patchTexture;
    private ID3D11ShaderResourceView? _patchSrv;
    private ID3D11Texture2D? _outputTexture;
    private ID3D11ShaderResourceView? _outputSrv;
    private ID3D11RenderTargetView? _outputRtv;
    private ID3D11Texture2D? _parameterTexture;
    private ID3D11ShaderResourceView? _parameterSrv;
    private float[] _patchPixels = Array.Empty<float>();
    private readonly float[] _parameters = new float[12];
    private nint _devicePointer;
    private uint _targetWidth;
    private uint _targetHeight;
    private int _drawnCells;
    private int _rejectedCells;
    private string _status = "Depth compositor has not been initialized.";
    private bool _disposed;

    public GroundedFluidDepthRenderer(FluidSimulationProbe fluid) => _fluid = fluid;

    public ImTextureID TextureId
    {
        get
        {
            nint pointer = _outputSrv?.NativePointer ?? 0;
            return Unsafe.As<nint, ImTextureID>(ref pointer);
        }
    }

    public int DrawnCells => _drawnCells;
    public int RejectedCells => _rejectedCells;
    public string Status => _status;

    public unsafe bool Render(
        ID3D11ShaderResourceView sceneDepthSrv,
        uint depthWidth,
        uint depthHeight,
        Vector2 viewportSize,
        ProjectedFluidVertex[] vertices,
        int subdivisions,
        bool rejectLargeSteps,
        float maxCellHeightDelta,
        float depthBias,
        float opacity,
        int debugMode,
        int depthSampleMode,
        out Vector2 imageMin,
        out Vector2 imageMax)
    {
        imageMin = default;
        imageMax = default;
        _drawnCells = 0;
        _rejectedCells = 0;

        if (_disposed || sceneDepthSrv == null || depthWidth == 0 || depthHeight == 0
            || viewportSize.X <= 1f || viewportSize.Y <= 1f || subdivisions < 1)
        {
            _status = "Depth compositor received invalid input.";
            return false;
        }

        ID3D11Device? device = _fluid.Device;
        ID3D11DeviceContext? immediate = _fluid.ImmediateContext;
        ID3D11ShaderResourceView? fluidSrv = _fluid.DisplayShaderResourceView;
        if (device == null || immediate == null || fluidSrv == null)
        {
            _status = "Fluid Lab device, immediate context, or display SRV is unavailable.";
            return false;
        }

        try
        {
            EnsurePipeline(device);
            if (!BuildPatchMap(vertices, subdivisions, viewportSize, rejectLargeSteps, maxCellHeightDelta,
                    out Vector2 rectMin, out Vector2 rectMax))
            {
                _status = "No visible, continuous grounded mesh cells were available for depth composition.";
                return false;
            }

            imageMin = rectMin;
            imageMax = rectMax;

            // Vortice maps the native D3D11 UINT row/depth pitches to UInt32.
            // Keep these values unsigned so the intended UpdateSubresource overload is selected.
            uint patchRowPitch = checked(_targetWidth * 16u);
            uint patchDepthPitch = checked(patchRowPitch * _targetHeight);
            fixed (float* patchPointer = _patchPixels)
            {
                immediate.UpdateSubresource(_patchTexture!, 0u, null, (IntPtr)patchPointer,
                    patchRowPitch, patchDepthPitch);
            }

            float rectWidth = rectMax.X - rectMin.X;
            float rectHeight = rectMax.Y - rectMin.Y;
            _parameters[0] = rectMin.X;
            _parameters[1] = rectMin.Y;
            _parameters[2] = rectWidth;
            _parameters[3] = rectHeight;
            _parameters[4] = viewportSize.X;
            _parameters[5] = viewportSize.Y;
            _parameters[6] = depthWidth;
            _parameters[7] = depthHeight;
            _parameters[8] = Math.Clamp(depthBias, 0f, 0.00001f);
            _parameters[9] = Math.Clamp(opacity, 0f, 1f);
            _parameters[10] = Math.Clamp(debugMode, 0, 6);
            _parameters[11] = depthSampleMode == 1 ? 1f : 0f;
            fixed (float* parameterPointer = _parameters)
            {
                immediate.UpdateSubresource(_parameterTexture!, 0, null, (IntPtr)parameterPointer, 48, 48);
            }

            _deferredContext!.OMSetRenderTargets(_outputRtv!);
            _deferredContext.RSSetViewport(0f, 0f, _targetWidth, _targetHeight);
            _deferredContext.IASetInputLayout(null);
            _deferredContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _deferredContext.VSSetShader(_vertexShader!);
            _deferredContext.PSSetShader(_pixelShader!);
            _deferredContext.PSSetShaderResources(0, new[] { _patchSrv!, fluidSrv, sceneDepthSrv, _parameterSrv! });
            _deferredContext.Draw(3, 0);
            _deferredContext.PSUnsetShaderResources(0, 4);
            _deferredContext.UnsetRenderTargets();

            using ID3D11CommandList commandList = _deferredContext.FinishCommandList(false);
            immediate.ExecuteCommandList(commandList, true);
            _status = $"Depth pass submitted · {_drawnCells} cells · {_targetWidth}×{_targetHeight} patch · scene {depthWidth}×{depthHeight} · mode {debugMode} · sample {(depthSampleMode == 1 ? "2x2 max" : "point")} · reversed-Z device-depth bias {depthBias:0.0000000}.";
            return true;
        }
        catch (Exception ex)
        {
            _status = "Depth-aware composite failed: " + ex.GetBaseException().Message;
            return false;
        }
    }

    private void EnsurePipeline(ID3D11Device device)
    {
        if (_devicePointer != device.NativePointer)
        {
            ReleaseGpuResources();
            _devicePointer = device.NativePointer;
        }

        // Build each resource lazily so a transient shader/resource creation failure can
        // recover on a later frame instead of leaving a half-initialized pipeline stuck.
        _deferredContext ??= device.CreateDeferredContext();
        _vertexShader ??= device.CreateVertexShader(CompileShader("VSMain", "vs_5_0"));
        _pixelShader ??= device.CreatePixelShader(CompileShader("PSMain", "ps_5_0"));
        if (_parameterTexture == null || _parameterSrv == null)
            CreateParameterTexture(device);
    }

    private void CreateParameterTexture(ID3D11Device device)
    {
        _parameterSrv?.Dispose();
        _parameterSrv = null;
        _parameterTexture?.Dispose();
        _parameterTexture = null;
        _parameterTexture = device.CreateTexture2D(
            Format.R32G32B32A32_Float, 3, 1, arraySize: 1, mipLevels: 1, initialData: null,
            bindFlags: BindFlags.ShaderResource, usage: ResourceUsage.Default);
        _parameterSrv = device.CreateShaderResourceView(_parameterTexture,
            new ShaderResourceViewDescription(ShaderResourceViewDimension.Texture2D,
                Format.R32G32B32A32_Float, 0, 1));
    }

    private void EnsureTargets(ID3D11Device device, uint width, uint height)
    {
        if (_patchTexture != null && _patchSrv != null && _outputTexture != null
            && _outputSrv != null && _outputRtv != null
            && _targetWidth == width && _targetHeight == height)
            return;

        ReleaseTargets();
        _patchTexture = device.CreateTexture2D(
            Format.R32G32B32A32_Float, width, height, arraySize: 1, mipLevels: 1, initialData: null,
            bindFlags: BindFlags.ShaderResource, usage: ResourceUsage.Default);
        _patchSrv = device.CreateShaderResourceView(_patchTexture,
            new ShaderResourceViewDescription(ShaderResourceViewDimension.Texture2D,
                Format.R32G32B32A32_Float, 0, 1));

        _outputTexture = device.CreateTexture2D(
            Format.R8G8B8A8_UNorm, width, height, arraySize: 1, mipLevels: 1, initialData: null,
            bindFlags: BindFlags.ShaderResource | BindFlags.RenderTarget, usage: ResourceUsage.Default);
        _outputSrv = device.CreateShaderResourceView(_outputTexture,
            new ShaderResourceViewDescription(ShaderResourceViewDimension.Texture2D,
                Format.R8G8B8A8_UNorm, 0, 1));
        _outputRtv = device.CreateRenderTargetView(_outputTexture,
            new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, Format.R8G8B8A8_UNorm));

        _targetWidth = width;
        _targetHeight = height;
        _patchPixels = new float[checked((int)(width * height * 4))];
    }

    private bool BuildPatchMap(ProjectedFluidVertex[] vertices, int subdivisions, Vector2 viewportSize,
        bool rejectLargeSteps, float maxCellHeightDelta, out Vector2 rectMin, out Vector2 rectMax)
    {
        rectMin = default;
        rectMax = default;
        int side = subdivisions + 1;
        if (vertices.Length != side * side)
        {
            _status = "Projected mesh vertex count does not match its subdivision count.";
            return false;
        }

        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;
        bool any = false;
        _rejectedCells = 0;

        for (int y = 0; y < subdivisions; y++)
        {
            for (int x = 0; x < subdivisions; x++)
            {
                int i00 = y * side + x;
                int i10 = i00 + 1;
                int i01 = i00 + side;
                int i11 = i01 + 1;
                var a = vertices[i00];
                var b = vertices[i10];
                var c = vertices[i11];
                var d = vertices[i01];
                if (!a.Valid || !b.Valid || !c.Valid || !d.Valid
                    || (rejectLargeSteps && MaxY(a, b, c, d) - MinY(a, b, c, d) > maxCellHeightDelta))
                {
                    _rejectedCells++;
                    continue;
                }

                Include(a.Screen, ref minX, ref minY, ref maxX, ref maxY, ref any);
                Include(b.Screen, ref minX, ref minY, ref maxX, ref maxY, ref any);
                Include(c.Screen, ref minX, ref minY, ref maxX, ref maxY, ref any);
                Include(d.Screen, ref minX, ref minY, ref maxX, ref maxY, ref any);
            }
        }

        if (!any)
            return false;

        minX = Math.Clamp(minX, 0f, viewportSize.X);
        minY = Math.Clamp(minY, 0f, viewportSize.Y);
        maxX = Math.Clamp(maxX, 0f, viewportSize.X);
        maxY = Math.Clamp(maxY, 0f, viewportSize.Y);
        float rectWidth = maxX - minX;
        float rectHeight = maxY - minY;
        if (rectWidth < 2f || rectHeight < 2f)
        {
            _status = "Projected ground patch is smaller than two screen pixels.";
            return false;
        }

        uint width = TargetExtent(rectWidth);
        uint height = TargetExtent(rectHeight);
        EnsureTargets(_fluid.Device!, width, height);
        Array.Clear(_patchPixels, 0, _patchPixels.Length);
        rectMin = new Vector2(minX, minY);
        rectMax = new Vector2(maxX, maxY);

        _drawnCells = 0;
        float invWidth = width / rectWidth;
        float invHeight = height / rectHeight;
        for (int y = 0; y < subdivisions; y++)
        {
            for (int x = 0; x < subdivisions; x++)
            {
                int i00 = y * side + x;
                int i10 = i00 + 1;
                int i01 = i00 + side;
                int i11 = i01 + 1;
                var a = vertices[i00];
                var b = vertices[i10];
                var c = vertices[i11];
                var d = vertices[i01];
                if (!a.Valid || !b.Valid || !c.Valid || !d.Valid
                    || (rejectLargeSteps && MaxY(a, b, c, d) - MinY(a, b, c, d) > maxCellHeightDelta))
                    continue;

                RasterTriangle(a, b, c, minX, minY, rectWidth, rectHeight, invWidth, invHeight, width, height);
                RasterTriangle(a, c, d, minX, minY, rectWidth, rectHeight, invWidth, invHeight, width, height);
                _drawnCells++;
            }
        }

        return _drawnCells > 0;
    }

    private void RasterTriangle(ProjectedFluidVertex a, ProjectedFluidVertex b, ProjectedFluidVertex c,
        float rectX, float rectY, float rectWidth, float rectHeight, float invWidth, float invHeight,
        uint targetWidth, uint targetHeight)
    {
        float ax = (a.Screen.X - rectX) * invWidth;
        float ay = (a.Screen.Y - rectY) * invHeight;
        float bx = (b.Screen.X - rectX) * invWidth;
        float by = (b.Screen.Y - rectY) * invHeight;
        float cx = (c.Screen.X - rectX) * invWidth;
        float cy = (c.Screen.Y - rectY) * invHeight;
        float denominator = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
        if (MathF.Abs(denominator) < 1e-5f)
            return;

        int minPixelX = Math.Clamp((int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))), 0, (int)targetWidth - 1);
        int maxPixelX = Math.Clamp((int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))), 0, (int)targetWidth - 1);
        int minPixelY = Math.Clamp((int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))), 0, (int)targetHeight - 1);
        int maxPixelY = Math.Clamp((int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))), 0, (int)targetHeight - 1);

        for (int py = minPixelY; py <= maxPixelY; py++)
        {
            float sampleY = py + 0.5f;
            for (int px = minPixelX; px <= maxPixelX; px++)
            {
                float sampleX = px + 0.5f;
                float wa = ((by - cy) * (sampleX - cx) + (cx - bx) * (sampleY - cy)) / denominator;
                float wb = ((cy - ay) * (sampleX - cx) + (ax - cx) * (sampleY - cy)) / denominator;
                float wc = 1f - wa - wb;
                if (wa < -0.0001f || wb < -0.0001f || wc < -0.0001f)
                    continue;

                int offset = (py * (int)targetWidth + px) * 4;
                _patchPixels[offset] = a.Uv.X * wa + b.Uv.X * wb + c.Uv.X * wc;
                _patchPixels[offset + 1] = a.Uv.Y * wa + b.Uv.Y * wb + c.Uv.Y * wc;
                // Device depth (z/w) is affine in screen space and therefore the correct
                // quantity to interpolate into the patch map. The shader reconstructs it
                // to view distance using the current projection before comparison.
                _patchPixels[offset + 2] = a.Depth * wa + b.Depth * wb + c.Depth * wc;
                _patchPixels[offset + 3] = 1f;
            }
        }
    }

    private static uint TargetExtent(float pixels)
    {
        uint extent = (uint)Math.Clamp(Math.Ceiling(pixels / 64f) * 64f, 64d, MaximumTargetDimension);
        return extent;
    }

    private static void Include(Vector2 point, ref float minX, ref float minY, ref float maxX, ref float maxY, ref bool any)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            return;
        minX = Math.Min(minX, point.X);
        minY = Math.Min(minY, point.Y);
        maxX = Math.Max(maxX, point.X);
        maxY = Math.Max(maxY, point.Y);
        any = true;
    }

    private static float MinY(ProjectedFluidVertex a, ProjectedFluidVertex b, ProjectedFluidVertex c, ProjectedFluidVertex d) =>
        Math.Min(Math.Min(a.WorldY, b.WorldY), Math.Min(c.WorldY, d.WorldY));

    private static float MaxY(ProjectedFluidVertex a, ProjectedFluidVertex b, ProjectedFluidVertex c, ProjectedFluidVertex d) =>
        Math.Max(Math.Max(a.WorldY, b.WorldY), Math.Max(c.WorldY, d.WorldY));

    private void ReleaseTargets()
    {
        _outputRtv?.Dispose();
        _outputRtv = null;
        _outputSrv?.Dispose();
        _outputSrv = null;
        _outputTexture?.Dispose();
        _outputTexture = null;
        _patchSrv?.Dispose();
        _patchSrv = null;
        _patchTexture?.Dispose();
        _patchTexture = null;
        _targetWidth = 0;
        _targetHeight = 0;
        _patchPixels = Array.Empty<float>();
    }

    private void ReleaseGpuResources()
    {
        ReleaseTargets();
        _parameterSrv?.Dispose();
        _parameterSrv = null;
        _parameterTexture?.Dispose();
        _parameterTexture = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
        _deferredContext?.Dispose();
        _deferredContext = null;
        _devicePointer = 0;
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
                    result = D3DCompile((IntPtr)sourcePointer, (nuint)source.Length, IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, entryPoint, target, 1u << 11, 0, out compiledBlob, out errorBlob);
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
        byte[] bytes = new byte[checked((int)nativeSize)];
        if (bytes.Length > 0)
            Marshal.Copy(data, bytes, 0, bytes.Length);
        return bytes;
    }

    [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(IntPtr sourceData, nuint sourceDataSize, IntPtr sourceName, IntPtr defines,
        IntPtr include, [MarshalAs(UnmanagedType.LPStr)] string entryPoint, [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1, uint flags2, out IntPtr code, out IntPtr errors);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseGpuResources();
    }
}

internal struct ProjectedFluidVertex
{
    public bool Valid;
    public Vector2 Screen;
    public Vector2 Uv;
    public float Depth;
    public float WorldY;
}
