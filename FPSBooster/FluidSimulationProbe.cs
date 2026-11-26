using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FPSBooster;

/// <summary>
/// Isolated GPU fluid-field experiment. It owns every texture and view it creates; it never
/// writes to an FFXIV-owned render target. Two floating-point textures alternate as the
/// previous/current state, and fixed 120 Hz fullscreen pixel-shader steps advance the field.
///
/// The pass is recorded on a deferred D3D11 context and executed with immediate-context state
/// restoration so the experiment does not leave its render pipeline bound for the game/UI.
/// </summary>
public sealed class FluidSimulationProbe : IDisposable
{
    private const uint SimulationWidth = 256;
    private const uint SimulationHeight = 144;
    private const float DefaultGravity = 24f;
    private const float DefaultDiffusion = 0.02f;
    private const float DefaultViscosity = 0.15f;
    private const float DefaultSurfaceTension = 0.35f;
    private const float DefaultWettingStrength = 0.85f;
    private const float DefaultContactAngleDegrees = 35f;
    private const float DefaultDropImpactSpeed = 14f;
    private const float DefaultSurfaceDetail = 12f;
    private const float DefaultSimulationSpeed = 1f;
    private const float FixedStepSeconds = 1f / 120f;
    private const float MaxFrameDeltaSeconds = 1f / 30f;
    private const float MaxSimulationSpeed = 4f;
    private const int MaxSubstepsPerFrame = 16;
    private const int MaximumQueuedDrops = 32;

    private const string ShaderSource = """
        struct VertexOutput
        {
            float4 Position : SV_Position;
        };

        VertexOutput VSMain(uint vertexId : SV_VertexID)
        {
            float2 p = float2((vertexId << 1) & 2, vertexId & 2);
            VertexOutput output;
            output.Position = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
            return output;
        }

        Texture2D<float4> PreviousState : register(t0);
        Texture2D<float4> SimulationSettings : register(t1);
        Texture2D<float4> DropletInjection : register(t2);

        // State layout: (liquid depth, horizontal momentum, vertical momentum, dye mass).
        // The simple-transport mode stores depth in R and keeps both momentum channels at zero.
        float4 ReadState(int2 pixel, uint width, uint height)
        {
            int2 maximum = int2(width, height) - 1;
            return PreviousState.Load(int3(clamp(pixel, int2(0, 0), maximum), 0));
        }

        float ReadHeight(int2 pixel, uint width, uint height)
        {
            return max(ReadState(pixel, width, height).x, 0.0);
        }

        float PhaseFromDepth(float depth)
        {
            // A smooth liquid/air indicator used only for interface geometry.
            return saturate(max(depth, 0.0) / (max(depth, 0.0) + 0.035));
        }

        float ReadPhase(int2 pixel, uint width, uint height, float contactAngleDegrees, float wettingStrength)
        {
            int x = clamp(pixel.x, 0, int(width) - 1);
            if (pixel.y < 0)
                return PhaseFromDepth(ReadHeight(int2(x, 0), width, height));

            if (pixel.y >= int(height))
            {
                // Contact-angle ghost value at the bottom wall. A 90-degree angle is
                // neutral; smaller angles encourage wall wetting, larger angles bead up.
                int bottomY = int(height) - 1;
                float center = PhaseFromDepth(ReadHeight(int2(x, bottomY), width, height));
                float left = PhaseFromDepth(ReadHeight(int2(x - 1, bottomY), width, height));
                float right = PhaseFromDepth(ReadHeight(int2(x + 1, bottomY), width, height));
                float tangentGradient = 0.5 * abs(right - left);
                float angle = clamp(contactAngleDegrees, 10.0, 170.0) * 0.01745329252;
                float cotangent = cos(angle) / max(abs(sin(angle)), 0.087);
                return saturate(center - cotangent * tangentGradient * saturate(wettingStrength));
            }

            return PhaseFromDepth(ReadHeight(int2(x, pixel.y), width, height));
        }

        float2 InterfaceNormalAt(int2 pixel, uint width, uint height, float contactAngleDegrees, float wettingStrength)
        {
            float left = ReadPhase(pixel + int2(-1, 0), width, height, contactAngleDegrees, wettingStrength);
            float right = ReadPhase(pixel + int2(1, 0), width, height, contactAngleDegrees, wettingStrength);
            float up = ReadPhase(pixel + int2(0, -1), width, height, contactAngleDegrees, wettingStrength);
            float down = ReadPhase(pixel + int2(0, 1), width, height, contactAngleDegrees, wettingStrength);
            float2 gradient = 0.5 * float2(right - left, down - up);
            return gradient / max(length(gradient), 0.0001);
        }

        float CurvatureAt(int2 pixel, uint width, uint height, float contactAngleDegrees, float wettingStrength)
        {
            float2 left = InterfaceNormalAt(pixel + int2(-1, 0), width, height, contactAngleDegrees, wettingStrength);
            float2 right = InterfaceNormalAt(pixel + int2(1, 0), width, height, contactAngleDegrees, wettingStrength);
            float2 up = InterfaceNormalAt(pixel + int2(0, -1), width, height, contactAngleDegrees, wettingStrength);
            float2 down = InterfaceNormalAt(pixel + int2(0, 1), width, height, contactAngleDegrees, wettingStrength);
            return 0.5 * ((right.x - left.x) + (down.y - up.y));
        }

        float4 FluxX(float4 state, float pressureGravity)
        {
            float depth = max(state.x, 0.0);
            float inverseDepth = rcp(max(depth, 0.00001));
            float u = state.y * inverseDepth;
            float v = state.z * inverseDepth;
            float pressure = 0.5 * pressureGravity * depth * depth;
            return float4(state.y, state.y * u + pressure, state.y * v, state.w * u);
        }

        float4 FluxY(float4 state, float pressureGravity)
        {
            float depth = max(state.x, 0.0);
            float inverseDepth = rcp(max(depth, 0.00001));
            float u = state.y * inverseDepth;
            float v = state.z * inverseDepth;
            float pressure = 0.5 * pressureGravity * depth * depth;
            return float4(state.z, state.y * v, state.z * v + pressure, state.w * v);
        }

        float4 RusanovFluxX(float4 left, float4 right, float pressureGravity)
        {
            float leftDepth = max(left.x, 0.0);
            float rightDepth = max(right.x, 0.0);
            float leftU = left.y / max(leftDepth, 0.00001);
            float rightU = right.y / max(rightDepth, 0.00001);
            float leftSpeed = abs(leftU) + sqrt(pressureGravity * leftDepth);
            float rightSpeed = abs(rightU) + sqrt(pressureGravity * rightDepth);
            float waveSpeed = max(leftSpeed, rightSpeed);
            return 0.5 * (FluxX(left, pressureGravity) + FluxX(right, pressureGravity))
                - 0.5 * waveSpeed * (right - left);
        }

        float4 RusanovFluxY(float4 up, float4 down, float pressureGravity)
        {
            float upDepth = max(up.x, 0.0);
            float downDepth = max(down.x, 0.0);
            float upV = up.z / max(upDepth, 0.00001);
            float downV = down.z / max(downDepth, 0.00001);
            float upSpeed = abs(upV) + sqrt(pressureGravity * upDepth);
            float downSpeed = abs(downV) + sqrt(pressureGravity * downDepth);
            float waveSpeed = max(upSpeed, downSpeed);
            return 0.5 * (FluxY(up, pressureGravity) + FluxY(down, pressureGravity))
                - 0.5 * waveSpeed * (down - up);
        }

        float4 PSMain(float4 position : SV_Position) : SV_Target
        {
            uint width;
            uint height;
            PreviousState.GetDimensions(width, height);

            int2 pixel = int2(position.xy);
            float4 current = ReadState(pixel, width, height);
            float4 settings = SimulationSettings.Load(int3(0, 0, 0));
            float4 displaySettings = SimulationSettings.Load(int3(1, 0, 0));
            float4 wettingSettings = SimulationSettings.Load(int3(2, 0, 0));
            float deltaSeconds = clamp(settings.z, 0.0, 0.0083334);
            float4 drop = DropletInjection.Load(int3(0, 0, 0));
            float4 next;

            // Retain the earlier mass-transfer solver as a direct visual/behavioral baseline.
            if (displaySettings.y < 0.5)
            {
                float currentMass = max(current.x, 0.0);
                float above = pixel.y > 0
                    ? ReadHeight(pixel + int2(0, -1), width, height)
                    : currentMass;
                float below = pixel.y + 1 < height
                    ? ReadHeight(pixel + int2(0, 1), width, height)
                    : 0.0;
                float left = pixel.x > 0
                    ? ReadHeight(pixel + int2(-1, 0), width, height)
                    : currentMass;
                float right = pixel.x + 1 < width
                    ? ReadHeight(pixel + int2(1, 0), width, height)
                    : currentMass;

                float gravityRate = min(saturate(settings.x * deltaSeconds * 2.2), 0.80);
                float diffusionRate = min(saturate(settings.y) * deltaSeconds * 5.0, 0.08);
                float downwardOut = pixel.y + 1 < height
                    ? min(currentMass, max(currentMass - below * 0.5, 0.0) * gravityRate)
                    : 0.0;
                float downwardIn = pixel.y > 0
                    ? min(above, max(above - currentMass * 0.5, 0.0) * gravityRate)
                    : 0.0;
                float sideTransfer = ((left - currentMass) + (right - currentMass)) * diffusionRate;
                float mass = max(currentMass - downwardOut + downwardIn + sideTransfer, 0.0);

                if (drop.w > 0.0)
                {
                    float distanceToDrop = distance(float2(pixel) + 0.5, drop.xy);
                    float shape = 1.0 - smoothstep(drop.z * 0.55, drop.z, distanceToDrop);
                    mass += shape * drop.w;
                }

                next = float4(mass, 0.0, 0.0, mass);
            }
            else
            {
                float4 left = pixel.x > 0
                    ? ReadState(pixel + int2(-1, 0), width, height)
                    : float4(current.x, -current.y, current.z, current.w);
                float4 right = pixel.x + 1 < width
                    ? ReadState(pixel + int2(1, 0), width, height)
                    : float4(current.x, -current.y, current.z, current.w);
                float4 up = pixel.y > 0
                    ? ReadState(pixel + int2(0, -1), width, height)
                    : float4(current.x, current.y, -current.z, current.w);
                float4 down = pixel.y + 1 < height
                    ? ReadState(pixel + int2(0, 1), width, height)
                    : float4(current.x, current.y, -current.z, current.w);

                // Conservative 2D shallow-water fluxes. Rusanov/Lax-Friedrichs interface
                // fluxes carry depth and both momentum components without creating mass.
                float pressureGravity = max(displaySettings.z, 0.1);
                float4 fluxRight = RusanovFluxX(current, right, pressureGravity);
                float4 fluxLeft = RusanovFluxX(left, current, pressureGravity);
                float4 fluxDown = RusanovFluxY(current, down, pressureGravity);
                float4 fluxUp = RusanovFluxY(up, current, pressureGravity);
                next = current - deltaSeconds * ((fluxRight - fluxLeft) + (fluxDown - fluxUp));

                float depth = max(next.x, 0.0);
                float gravityAcceleration = max(settings.x, 0.0) * 12.0;
                next.z += gravityAcceleration * depth * deltaSeconds;

                // Continuum-surface-force approximation: capillary force follows
                // curvature * interface gradient. Interface normals use a contact-angle
                // ghost value at the bottom wall, giving the liquid a wetting/beading control.
                float contactAngleDegrees = wettingSettings.x;
                float wettingStrength = wettingSettings.y;
                float phaseLeft = ReadPhase(pixel + int2(-1, 0), width, height, contactAngleDegrees, wettingStrength);
                float phaseRight = ReadPhase(pixel + int2(1, 0), width, height, contactAngleDegrees, wettingStrength);
                float phaseUp = ReadPhase(pixel + int2(0, -1), width, height, contactAngleDegrees, wettingStrength);
                float phaseDown = ReadPhase(pixel + int2(0, 1), width, height, contactAngleDegrees, wettingStrength);
                float2 interfaceGradient = 0.5 * float2(phaseRight - phaseLeft, phaseDown - phaseUp);
                float curvature = CurvatureAt(pixel, width, height, contactAngleDegrees, wettingStrength);
                float interfaceStrength = saturate(length(interfaceGradient) * 5.0);
                float capillaryStrength = saturate(settings.w) * 80.0;
                next.yz -= capillaryStrength * curvature * interfaceGradient * depth * interfaceStrength * deltaSeconds;

                // Viscosity diffuses velocity and damps residual motion. Depth remains
                // conserved; only momentum is modified here.
                float inverseDepth = rcp(max(depth, 0.00001));
                float2 velocity = next.yz * inverseDepth;
                float2 velocityLeft = left.yz / max(left.x, 0.00001);
                float2 velocityRight = right.yz / max(right.x, 0.00001);
                float2 velocityUp = up.yz / max(up.x, 0.00001);
                float2 velocityDown = down.yz / max(down.x, 0.00001);
                float2 velocityLaplacian = velocityLeft + velocityRight + velocityUp + velocityDown - 4.0 * velocity;
                float viscosity = saturate(settings.y);
                velocity += viscosity * 4.0 * deltaSeconds * velocityLaplacian;
                velocity *= exp(-viscosity * 2.4 * deltaSeconds);

                float speed = length(velocity);
                float maximumVelocity = max(displaySettings.w, 1.0);
                if (speed > maximumVelocity)
                    velocity *= maximumVelocity / speed;

                next.x = depth;
                next.yz = velocity * depth;
                next.w = max(next.w, 0.0);
                if (depth < 0.00001)
                {
                    next.yz = 0.0;
                    next.w = 0.0;
                }

                if (drop.w > 0.0)
                {
                    float distanceToDrop = distance(float2(pixel) + 0.5, drop.xy);
                    float shape = 1.0 - smoothstep(drop.z * 0.55, drop.z, distanceToDrop);
                    float injectedMass = shape * drop.w;
                    next.x += injectedMass;
                    next.z += injectedMass * max(wettingSettings.z, 0.0);
                    next.w += injectedMass;
                }
            }

            return next;
        }

        float4 PSDisplayMain(float4 position : SV_Position) : SV_Target
        {
            uint width;
            uint height;
            PreviousState.GetDimensions(width, height);
            int2 pixel = clamp(int2(position.xy), int2(0, 0), int2(width, height) - 1);
            float4 state = PreviousState.Load(int3(pixel, 0));
            float4 displaySettings = SimulationSettings.Load(int3(1, 0, 0));
            float4 wettingSettings = SimulationSettings.Load(int3(2, 0, 0));
            float depth = max(state.x, 0.0);
            float2 velocity = state.yz / max(depth, 0.00001);
            float flow = saturate(length(velocity) / max(displaySettings.w, 1.0));
            float alpha = smoothstep(0.006, 0.055, depth);

            if (displaySettings.x > 0.5)
            {
                float dye = pow(saturate(1.0 - exp(-depth * 0.65)), 0.55);
                return float4(dye, dye * 0.035, dye * 0.82, alpha);
            }

            // Reconstruct a screen-facing surface normal from neighboring film thickness.
            // Specular and Fresnel highlights make shallow ripples visible without adding
            // another simulation state or changing the liquid mass.
            float left = ReadHeight(pixel + int2(-1, 0), width, height);
            float right = ReadHeight(pixel + int2(1, 0), width, height);
            float up = ReadHeight(pixel + int2(0, -1), width, height);
            float down = ReadHeight(pixel + int2(0, 1), width, height);
            float2 slope = 0.5 * float2(right - left, down - up);
            float surfaceDetail = max(wettingSettings.w, 0.1);
            float3 normal = normalize(float3(-slope * surfaceDetail, 1.0));
            float3 lightDirection = normalize(float3(-0.38, -0.48, 0.79));
            float3 viewDirection = float3(0.0, 0.0, 1.0);
            float diffuse = saturate(dot(normal, lightDirection));
            float3 halfDirection = normalize(lightDirection + viewDirection);
            float specular = pow(saturate(dot(normal, halfDirection)), 56.0);
            float fresnel = pow(1.0 - saturate(dot(normal, viewDirection)), 5.0);

            float thickness = 1.0 - exp(-depth * 1.35);
            float3 shallowColor = float3(0.025, 0.24, 0.34);
            float3 deepColor = float3(0.008, 0.055, 0.16);
            float3 water = lerp(shallowColor, deepColor, saturate(thickness * 1.5));
            water *= 0.48 + diffuse * 0.52;
            water += specular * float3(0.68, 0.88, 1.0) * (0.35 + flow * 0.65);
            water += fresnel * float3(0.10, 0.38, 0.58);

            // A restrained bright rim makes the reconstructed free surface readable.
            float rim = 1.0 - smoothstep(0.045, 0.16, depth);
            water += rim * float3(0.055, 0.24, 0.34) * alpha;
            return float4(saturate(water), alpha);
        }
        """;

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly Queue<Vector4> _pendingDrops = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _immediateContext;
    private ID3D11DeviceContext? _deferredContext;
    private readonly ID3D11Texture2D?[] _stateTextures = new ID3D11Texture2D?[2];
    private readonly ID3D11ShaderResourceView?[] _stateShaderResourceViews = new ID3D11ShaderResourceView?[2];
    private readonly ID3D11RenderTargetView?[] _stateRenderTargetViews = new ID3D11RenderTargetView?[2];
    private ID3D11Texture2D? _settingsTexture;
    private ID3D11ShaderResourceView? _settingsShaderResourceView;
    private ID3D11Texture2D? _injectionTexture;
    private ID3D11ShaderResourceView? _injectionShaderResourceView;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11PixelShader? _displayPixelShader;
    private ID3D11Texture2D? _displayTexture;
    private ID3D11ShaderResourceView? _displayShaderResourceView;
    private ID3D11RenderTargetView? _displayRenderTargetView;
    private int _currentStateIndex;
    private int _simulationStepCount;
    private bool _isRunning = true;
    private float _gravity = DefaultGravity;
    private float _diffusion = DefaultDiffusion;
    private float _viscosity = DefaultViscosity;
    private float _surfaceTension = DefaultSurfaceTension;
    private float _wettingStrength = DefaultWettingStrength;
    private float _contactAngleDegrees = DefaultContactAngleDegrees;
    private float _dropImpactSpeed = DefaultDropImpactSpeed;
    private float _surfaceDetail = DefaultSurfaceDetail;
    private float _simulationSpeed = DefaultSimulationSpeed;
    private int _solverMode = 1;
    private bool _highContrastDye;
    private bool _externalDriverActive;
    private float _externalGravityScale = 1f;
    private DateTime _lastFrameUtc;
    private float _stepAccumulatorSeconds;
    private float _simulatedTimeSeconds;
    private int _stepsThisFrame;
    private double _lastSubmitMilliseconds;
    private string _status = "No GPU fluid simulation created.";
    private string? _error;

    public FluidSimulationProbe(IDalamudPluginInterface pluginInterface)
    {
        _pluginInterface = pluginInterface;
    }

    public bool IsRunning => _isRunning;

    /// <summary>Raw pointer to Dalamud's immediate D3D11 context, available after GPU initialization.</summary>
    internal nint ImmediateContextPointer => _immediateContext?.NativePointer ?? 0;

    internal ID3D11Device? Device => _device;
    internal ID3D11DeviceContext? ImmediateContext => _immediateContext;
    internal ID3D11ShaderResourceView? DisplayShaderResourceView => _displayShaderResourceView;

    public bool IsCreated => _device != null && _deferredContext != null
        && _stateShaderResourceViews[0] != null && _stateShaderResourceViews[1] != null
        && _displayShaderResourceView != null && _displayRenderTargetView != null;

    public string Status => _status;
    public string? Error => _error;
    public int SimulationStepCount => _simulationStepCount;
    public int QueuedDropletCount => _pendingDrops.Count;
    public double LastSubmitMilliseconds => _lastSubmitMilliseconds;

    public ImTextureID PreviewTextureId
    {
        get
        {
            ID3D11ShaderResourceView? view = _displayShaderResourceView;
            if (view == null)
                return default;

            nint nativePointer = view.NativePointer;
            return Unsafe.As<nint, ImTextureID>(ref nativePointer);
        }
    }

    /// <summary>Draws the Fluid Lab UI. The plugin draw hook owns simulation advancement while a world overlay is active.</summary>
    public void Draw()
    {
        ImGui.TextWrapped("GPU fluid research on plugin-owned D3D11 textures. The thin-film solver tracks liquid depth and momentum, adds an experimental bottom-wall contact angle, and renders a translucent highlighted surface. The original transport solver remains available as a comparison. No FFXIV render targets are modified.");
        ImGui.Separator();

        if (!IsCreated)
        {
            ImGui.TextDisabled("Field: 256 × 144 · RGBA32F state · two ping-pong render targets + display target");
            if (ImGui.Button("Create GPU fluid simulation", new Vector2(245f, 0f)))
                Create();

            ImGui.TextWrapped(_status);
            if (!string.IsNullOrWhiteSpace(_error))
                ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), _error);

            ImGui.Separator();
            ImGui.TextDisabled("Create allocates plugin-owned textures, compiles the embedded HLSL shaders, and requests a deferred context. If the game device does not support deferred contexts, the reason will appear here.");
            return;
        }

        string[] solverModes = { "Simple transport (baseline)", "Thin-film / shallow-water" };
        int previousSolverMode = _solverMode;
        ImGui.Combo("Solver", ref _solverMode, solverModes, solverModes.Length);
        _solverMode = Math.Clamp(_solverMode, 0, solverModes.Length - 1);
        if (_solverMode != previousSolverMode)
        {
            ClearField();
            _status = _solverMode == 1
                ? "Switched to the thin-film solver. The field was reset because the solver state now includes momentum."
                : "Switched to the simple transport baseline. The field was reset for a clean comparison.";
        }

        ImGui.Checkbox("Run simulation", ref _isRunning);
        ImGui.SameLine();
        if (ImGui.Button("Clear field"))
            ClearField();
        ImGui.SameLine();
        if (ImGui.Button("Destroy simulation"))
        {
            Reset();
            return;
        }
        ImGui.SameLine();
        if (ImGui.Button("Seed demo drops"))
            QueueDemoDrops();
        if (ImGui.Button("Single-drop test"))
        {
            ClearField();
            QueueDrop(SimulationWidth * 0.5f, 10f, 7f, 0.95f);
            _status = "Field cleared and one centered droplet queued.";
        }

        ImGui.SliderFloat("Gravity", ref _gravity, 0.5f, 24f, "%.1f");
        if (_solverMode == 1)
        {
            ImGui.SliderFloat("Viscosity", ref _viscosity, 0f, 1f, "%.2f");
            ImGui.SliderFloat("Surface tension", ref _surfaceTension, 0f, 1f, "%.2f");
            ImGui.SliderFloat("Wetting strength", ref _wettingStrength, 0f, 1f, "%.2f");
            ImGui.SliderFloat("Contact angle", ref _contactAngleDegrees, 10f, 170f, "%.0f°");
            ImGui.SliderFloat("Drop impact speed", ref _dropImpactSpeed, 0f, 30f, "%.1f");
            ImGui.SliderFloat("Surface detail", ref _surfaceDetail, 2f, 28f, "%.1f");
            ImGui.TextDisabled("90° is approximately neutral; lower angles favor bottom-wall wetting/spreading, while higher angles favor beading. Wetting strength blends this boundary effect. Surface detail affects preview highlights only.");
        }
        else
        {
            ImGui.SliderFloat("Diffusion", ref _diffusion, 0f, 1f, "%.2f");
            ImGui.TextDisabled("Baseline model: diffusion controls lateral mass equalization, independently of gravity.");
        }
        ImGui.SliderFloat("Simulation speed", ref _simulationSpeed, 0.25f, MaxSimulationSpeed, "%.2fx");
        ImGui.Checkbox("High-contrast dye view", ref _highContrastDye);
        ImGui.TextDisabled("Thin-film solver: 120 Hz fixed steps, up to 16 steps per rendered frame. Drop speed adds initial downward momentum; dye view changes only the display texture.");

        if (ImGui.Button("Drop left"))
            QueueDrop(SimulationWidth * 0.28f, 10f, 9f, 0.95f);
        ImGui.SameLine();
        if (ImGui.Button("Drop center"))
            QueueDrop(SimulationWidth * 0.50f, 10f, 9f, 0.95f);
        ImGui.SameLine();
        if (ImGui.Button("Drop right"))
            QueueDrop(SimulationWidth * 0.72f, 10f, 9f, 0.95f);

        if (_externalDriverActive)
        {
            // The Grounded Fluid tab advances this shared simulation from the plugin draw hook,
            // even when the Fluid Lab tab is closed. Avoid submitting a second step here.
            UpdateSettings();
        }
        else
        {
            AdvanceFromUi();
        }

        ImGui.Separator();
        ImGui.TextDisabled($"GPU field: {SimulationWidth} × {SimulationHeight} · steps: {_simulationStepCount:N0} · sim time: {_simulatedTimeSeconds:F1}s · steps/frame: {_stepsThisFrame} · queued drops: {_pendingDrops.Count}");
        ImGui.TextDisabled($"CPU submission: {_lastSubmitMilliseconds:F3} ms ({_stepsThisFrame} simulation step(s), not GPU execution time)");
        ImGui.TextWrapped(_status);
        if (!string.IsNullOrWhiteSpace(_error))
            ImGui.TextColored(new Vector4(1f, 0.45f, 0.35f, 1f), _error);

        if (_displayShaderResourceView != null)
        {
            ImGui.TextDisabled(_highContrastDye
                ? "Liquid depth · high-contrast dye visualization"
                : "Water surface · reconstructed normals, specular highlights, and transparent dry areas");
            ImGui.Image(PreviewTextureId, new Vector2(512f, 288f));
        }

        ImGui.TextDisabled(_solverMode == 1
            ? "Model: conservative 2D shallow-water fluxes, gravity forcing, momentum viscosity, and a curvature-based capillary force. This is a thin-film approximation, not full 3D free-surface Navier–Stokes."
            : "Model: fixed-step gravity transfer and lateral mass diffusion. Retained as the original baseline for visual comparison.");
    }


    /// <summary>Switches simulation advancement to the plugin draw hook while a world overlay is active.</summary>
    public void SetExternalDriverActive(bool active)
    {
        if (_externalDriverActive == active)
            return;

        _externalDriverActive = active;
        _lastFrameUtc = DateTime.UtcNow;
    }

    /// <summary>Scales tangential gravity for a ground-aligned simulation (1 = full, 0 = level surface).</summary>
    public void SetExternalGravityScale(float scale)
    {
        _externalGravityScale = Math.Clamp(scale, 0f, 1f);
    }

    /// <summary>Sets the shared simulation run state without requiring the Fluid Lab tab to be open.</summary>
    public void SetRunning(bool running)
    {
        _isRunning = running;
        _lastFrameUtc = DateTime.UtcNow;
    }

    /// <summary>Advances the GPU field once from the plugin draw hook, independently of the Fluid Lab window.</summary>
    public void AdvanceExternalFrame()
    {
        if (!IsCreated)
            return;

        AdvanceFromUi();
    }

    /// <summary>Clears the field and queues a small group of droplets for the grounded-pool experiment.</summary>
    public void SeedGroundedPool()
    {
        if (!IsCreated)
            return;

        ClearField();
        QueueDrop(SimulationWidth * 0.30f, 10f, 9f, 0.92f);
        QueueDrop(SimulationWidth * 0.50f, 7f, 11f, 0.96f);
        QueueDrop(SimulationWidth * 0.70f, 13f, 9f, 0.92f);
        _status = "Grounded-pool seed queued: three droplets will flow toward the texture's downhill edge.";
    }

    private void AdvanceFromUi()
    {
        try
        {
            if (_isRunning)
            {
                AdvanceFrame();
            }
            else
            {
                // Do not accumulate paused wall-clock time and then simulate a catch-up burst.
                _lastFrameUtc = DateTime.UtcNow;
                _stepsThisFrame = 0;
                _lastSubmitMilliseconds = 0d;
                UpdateSettings();
                UpdateDisplay();
            }
        }
        catch (Exception ex)
        {
            ReleaseResources();
            _isRunning = false;
            _error = ex.GetBaseException().Message;
            _status = "The GPU simulation pass failed; probe-owned resources were released. Recreate the simulation to retry.";
        }
    }

    /// <summary>Creates plugin-owned simulation resources using Dalamud's borrowed D3D11 device.</summary>
    public void Create()
    {
        if (IsCreated)
            return;

        ReleaseResources();
        _pendingDrops.Clear();
        _simulationStepCount = 0;
        _currentStateIndex = 0;
        _stepAccumulatorSeconds = 0f;
        _simulatedTimeSeconds = 0f;
        _stepsThisFrame = 0;
        _lastSubmitMilliseconds = 0d;
        _error = null;

        nint ownedDeviceReference = 0;
        try
        {
            nint borrowedDevice = _pluginInterface.UiBuilder.DeviceHandle;
            if (borrowedDevice == 0)
                throw new InvalidOperationException("Dalamud has not exposed the D3D11 device yet. Try again while the Fluid Lab tab is drawing.");

            Guid iid = typeof(ID3D11Device).GUID;
            int queryResult = Marshal.QueryInterface(borrowedDevice, in iid, out ownedDeviceReference);
            if (queryResult < 0)
                Marshal.ThrowExceptionForHR(queryResult);
            if (ownedDeviceReference == 0)
                throw new InvalidOperationException("QueryInterface returned an empty D3D11 device reference.");

            // QueryInterface returned an owned COM reference; Vortice wraps that reference.
            _device = new ID3D11Device(ownedDeviceReference);
            ownedDeviceReference = 0;
            _immediateContext = _device.ImmediateContext;

            // A deferred context lets us submit a complete private pipeline pass without
            // leaving the game's/UI immediate-context state changed.
            _deferredContext = _device.CreateDeferredContext();

            byte[] vertexBytecode = CompileShader("VSMain", "vs_5_0");
            byte[] pixelBytecode = CompileShader("PSMain", "ps_5_0");
            byte[] displayPixelBytecode = CompileShader("PSDisplayMain", "ps_5_0");
            _vertexShader = _device.CreateVertexShader(vertexBytecode);
            _pixelShader = _device.CreatePixelShader(pixelBytecode);
            _displayPixelShader = _device.CreatePixelShader(displayPixelBytecode);

            var srvDescription = new ShaderResourceViewDescription(
                Vortice.Direct3D.ShaderResourceViewDimension.Texture2D,
                Format.R32G32B32A32_Float,
                0,
                1);
            var rtvDescription = new RenderTargetViewDescription(
                RenderTargetViewDimension.Texture2D,
                Format.R32G32B32A32_Float);

            for (int i = 0; i < 2; i++)
            {
                _stateTextures[i] = _device.CreateTexture2D(
                    Format.R32G32B32A32_Float,
                    SimulationWidth,
                    SimulationHeight,
                    arraySize: 1,
                    mipLevels: 1,
                    initialData: null,
                    bindFlags: BindFlags.ShaderResource | BindFlags.RenderTarget,
                    usage: ResourceUsage.Default);
                _stateShaderResourceViews[i] = _device.CreateShaderResourceView(_stateTextures[i]!, srvDescription);
                _stateRenderTargetViews[i] = _device.CreateRenderTargetView(_stateTextures[i]!, rtvDescription);
            }

            _displayTexture = CreateDisplayTexture();
            _displayShaderResourceView = _device.CreateShaderResourceView(_displayTexture!, srvDescription);
            _displayRenderTargetView = _device.CreateRenderTargetView(_displayTexture!, rtvDescription);

            _settingsTexture = CreateSettingsTexture();
            _settingsShaderResourceView = _device.CreateShaderResourceView(_settingsTexture!, srvDescription);
            _injectionTexture = CreateSinglePixelTexture();
            _injectionShaderResourceView = _device.CreateShaderResourceView(_injectionTexture!, srvDescription);

            ClearFieldInternal();
            QueueDemoDrops();
            _lastFrameUtc = DateTime.UtcNow;
            _isRunning = true;
            _status = "Created two plugin-owned RGBA32F state targets and a separate display target. Thin-film solver is ready.";
        }
        catch (Exception ex)
        {
            if (ownedDeviceReference != 0)
                Marshal.Release(ownedDeviceReference);

            ReleaseResources();
            _error = ex.GetBaseException().Message;
            _status = "GPU fluid simulation creation failed.";
        }
    }

    /// <summary>Clears both simulation textures and discards pending droplets.</summary>
    public void ClearField()
    {
        if (_immediateContext == null || _stateRenderTargetViews[0] == null || _stateRenderTargetViews[1] == null)
            return;

        _pendingDrops.Clear();
        ClearFieldInternal();
        _status = "Both plugin-owned state textures were cleared.";
        _error = null;
    }

    /// <summary>Releases all probe-owned GPU resources. Safe to call repeatedly.</summary>
    public void Reset()
    {
        ReleaseResources();
        _pendingDrops.Clear();
        _currentStateIndex = 0;
        _simulationStepCount = 0;
        _stepAccumulatorSeconds = 0f;
        _simulatedTimeSeconds = 0f;
        _stepsThisFrame = 0;
        _lastSubmitMilliseconds = 0d;
        _status = "No GPU fluid simulation created.";
        _error = null;
    }

    public void Dispose() => Reset();

    private void AdvanceFrame()
    {
        DateTime now = DateTime.UtcNow;
        float frameDelta = _lastFrameUtc == default
            ? FixedStepSeconds
            : (float)Math.Clamp((now - _lastFrameUtc).TotalSeconds, 0d, MaxFrameDeltaSeconds);
        _lastFrameUtc = now;
        _stepAccumulatorSeconds += frameDelta * _simulationSpeed;
        UpdateSettings();

        var timer = Stopwatch.StartNew();
        int steps = 0;
        while (_stepAccumulatorSeconds + 0.000001f >= FixedStepSeconds && steps < MaxSubstepsPerFrame)
        {
            AdvanceOneStep();
            _stepAccumulatorSeconds -= FixedStepSeconds;
            _simulatedTimeSeconds += FixedStepSeconds;
            steps++;
        }

        UpdateDisplay();
        timer.Stop();
        _stepsThisFrame = steps;
        _lastSubmitMilliseconds = timer.Elapsed.TotalMilliseconds;
        if (steps > 0)
        {
            _status = "GPU thin-film ping-pong step(s) and display pass submitted successfully.";
            _error = null;
        }
    }

    private void UpdateSettings()
    {
        if (_immediateContext == null || _settingsTexture == null)
            return;

        float secondaryCoefficient = _solverMode == 0 ? _diffusion : _viscosity;
        float effectiveGravity = _gravity * (_externalDriverActive ? _externalGravityScale : 1f);
        Vector4 settings = new(effectiveGravity, secondaryCoefficient, FixedStepSeconds, _surfaceTension);
        Vector4 displaySettings = new(_highContrastDye ? 1f : 0f, _solverMode, 8f, 42f);
        Vector4 wettingSettings = new(_contactAngleDegrees, _wettingStrength, _dropImpactSpeed, _surfaceDetail);
        UpdateSettingsTexture(_immediateContext, _settingsTexture, settings, displaySettings, wettingSettings);
    }

    private void AdvanceOneStep()
    {
        if (_device == null || _immediateContext == null || _deferredContext == null
            || _injectionTexture == null || _settingsShaderResourceView == null
            || _injectionShaderResourceView == null || _pixelShader == null || _vertexShader == null)
            return;

        int writeIndex = 1 - _currentStateIndex;
        ID3D11ShaderResourceView previousState = _stateShaderResourceViews[_currentStateIndex]
            ?? throw new InvalidOperationException("The previous fluid-state SRV is missing.");
        ID3D11RenderTargetView nextState = _stateRenderTargetViews[writeIndex]
            ?? throw new InvalidOperationException("The next fluid-state RTV is missing.");
        Vector4 injection = _pendingDrops.Count > 0 ? _pendingDrops.Dequeue() : Vector4.Zero;
        UpdateSinglePixel(_immediateContext, _injectionTexture, injection);

        _deferredContext.OMSetRenderTargets(nextState);
        _deferredContext.RSSetViewport(0f, 0f, SimulationWidth, SimulationHeight);
        _deferredContext.IASetInputLayout(null);
        _deferredContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _deferredContext.VSSetShader(_vertexShader);
        _deferredContext.PSSetShader(_pixelShader);
        _deferredContext.PSSetShaderResources(0, new[]
        {
            previousState,
            _settingsShaderResourceView,
            _injectionShaderResourceView,
        });
        _deferredContext.Draw(3, 0);
        _deferredContext.PSUnsetShaderResources(0, 3);
        _deferredContext.UnsetRenderTargets();

        using ID3D11CommandList commandList = _deferredContext.FinishCommandList(false);
        _immediateContext.ExecuteCommandList(commandList, true);

        _currentStateIndex = writeIndex;
        _simulationStepCount++;
    }

    private void UpdateDisplay()
    {
        if (_immediateContext == null || _deferredContext == null || _vertexShader == null
            || _displayPixelShader == null || _displayRenderTargetView == null
            || _settingsShaderResourceView == null)
            return;

        ID3D11ShaderResourceView state = _stateShaderResourceViews[_currentStateIndex]
            ?? throw new InvalidOperationException("The current fluid-state SRV is missing.");

        _deferredContext.OMSetRenderTargets(_displayRenderTargetView);
        _deferredContext.RSSetViewport(0f, 0f, SimulationWidth, SimulationHeight);
        _deferredContext.IASetInputLayout(null);
        _deferredContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _deferredContext.VSSetShader(_vertexShader);
        _deferredContext.PSSetShader(_displayPixelShader);
        _deferredContext.PSSetShaderResources(0, new[] { state, _settingsShaderResourceView });
        _deferredContext.Draw(3, 0);
        _deferredContext.PSUnsetShaderResources(0, 2);
        _deferredContext.UnsetRenderTargets();

        using ID3D11CommandList commandList = _deferredContext.FinishCommandList(false);
        _immediateContext.ExecuteCommandList(commandList, true);
    }

    private ID3D11Texture2D CreateSettingsTexture()
    {
        if (_device == null)
            throw new InvalidOperationException("The D3D11 device has not been acquired.");

        // Pixel 0 stores simulation values, pixel 1 display/solver options, and pixel 2 wetting/injection/render values.
        return _device.CreateTexture2D(
            Format.R32G32B32A32_Float,
            3,
            1,
            arraySize: 1,
            mipLevels: 1,
            initialData: null,
            bindFlags: BindFlags.ShaderResource,
            usage: ResourceUsage.Default);
    }

    private ID3D11Texture2D CreateSinglePixelTexture()
    {
        if (_device == null)
            throw new InvalidOperationException("The D3D11 device has not been acquired.");

        return _device.CreateTexture2D(
            Format.R32G32B32A32_Float,
            1,
            1,
            arraySize: 1,
            mipLevels: 1,
            initialData: null,
            bindFlags: BindFlags.ShaderResource,
            usage: ResourceUsage.Default);
    }

    private ID3D11Texture2D CreateDisplayTexture()
    {
        if (_device == null)
            throw new InvalidOperationException("The D3D11 device has not been acquired.");

        return _device.CreateTexture2D(
            Format.R32G32B32A32_Float,
            SimulationWidth,
            SimulationHeight,
            arraySize: 1,
            mipLevels: 1,
            initialData: null,
            bindFlags: BindFlags.ShaderResource | BindFlags.RenderTarget,
            usage: ResourceUsage.Default);
    }

    private void ClearFieldInternal()
    {
        if (_immediateContext == null || _stateRenderTargetViews[0] == null || _stateRenderTargetViews[1] == null)
            return;

        var emptyState = new Color4(0f, 0f, 0f, 0f);
        var emptyDisplay = new Color4(0f, 0f, 0f, 1f);
        _immediateContext.ClearRenderTargetView(_stateRenderTargetViews[0]!, emptyState);
        _immediateContext.ClearRenderTargetView(_stateRenderTargetViews[1]!, emptyState);
        if (_displayRenderTargetView != null)
            _immediateContext.ClearRenderTargetView(_displayRenderTargetView, emptyDisplay);
        _currentStateIndex = 0;
        _simulationStepCount = 0;
        _stepAccumulatorSeconds = 0f;
        _simulatedTimeSeconds = 0f;
        _stepsThisFrame = 0;
        _lastSubmitMilliseconds = 0d;
        _lastFrameUtc = DateTime.UtcNow;
    }

    private void QueueDemoDrops()
    {
        QueueDrop(SimulationWidth * 0.30f, 12f, 8f, 0.92f);
        QueueDrop(SimulationWidth * 0.52f, 8f, 10f, 0.86f);
        QueueDrop(SimulationWidth * 0.73f, 18f, 7f, 0.96f);
        _status = "Three demo droplets queued. Start or resume the simulation to inject them.";
        _error = null;
    }

    private void QueueDrop(float x, float y, float radius, float amount)
    {
        while (_pendingDrops.Count >= MaximumQueuedDrops)
            _pendingDrops.Dequeue();

        _pendingDrops.Enqueue(new Vector4(x, y, radius, amount));
    }

    private static unsafe void UpdateSettingsTexture(
        ID3D11DeviceContext context,
        ID3D11Texture2D texture,
        Vector4 simulationSettings,
        Vector4 displaySettings,
        Vector4 wettingSettings)
    {
        // Three contiguous RGBA32F pixels: 3 * 16 bytes = 48-byte row pitch.
        Vector4* values = stackalloc Vector4[3];
        values[0] = simulationSettings;
        values[1] = displaySettings;
        values[2] = wettingSettings;
        context.UpdateSubresource(texture, 0, null, (IntPtr)values, 48, 48);
    }

    private static unsafe void UpdateSinglePixel(ID3D11DeviceContext context, ID3D11Texture2D texture, Vector4 value)
    {
        // A single RGBA32F pixel is 16 bytes; row and depth pitch are therefore both 16.
        context.UpdateSubresource(texture, 0, null, (IntPtr)(&value), 16, 16);
    }

    private void ReleaseResources()
    {
        _settingsShaderResourceView?.Dispose();
        _settingsShaderResourceView = null;
        _injectionShaderResourceView?.Dispose();
        _injectionShaderResourceView = null;

        for (int i = 0; i < 2; i++)
        {
            _stateShaderResourceViews[i]?.Dispose();
            _stateShaderResourceViews[i] = null;
            _stateRenderTargetViews[i]?.Dispose();
            _stateRenderTargetViews[i] = null;
            _stateTextures[i]?.Dispose();
            _stateTextures[i] = null;
        }

        _settingsTexture?.Dispose();
        _settingsTexture = null;
        _injectionTexture?.Dispose();
        _injectionTexture = null;
        _displayShaderResourceView?.Dispose();
        _displayShaderResourceView = null;
        _displayRenderTargetView?.Dispose();
        _displayRenderTargetView = null;
        _displayTexture?.Dispose();
        _displayTexture = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _displayPixelShader?.Dispose();
        _displayPixelShader = null;
        _deferredContext?.Dispose();
        _deferredContext = null;
        _immediateContext?.Dispose();
        _immediateContext = null;
        _device?.Dispose();
        _device = null;
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
                        flags1: 1u << 11, // D3DCOMPILE_ENABLE_STRICTNESS
                        flags2: 0,
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
                throw new InvalidOperationException($"HLSL compiler returned no bytecode for {entryPoint} ({target}).");

            return CopyCompilerBlob(compiledBlob);
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException("d3dcompiler_47.dll is unavailable; FPSBooster could not compile the Fluid Lab shaders on this system.", ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new InvalidOperationException("D3DCompile was not found in d3dcompiler_47.dll.", ex);
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
        if (blob == IntPtr.Zero)
            return Array.Empty<byte>();

        void** vtable = *(void***)blob;
        var getBufferPointer = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)vtable[3];
        var getBufferSize = (delegate* unmanaged[Stdcall]<IntPtr, nuint>)vtable[4];
        IntPtr data = getBufferPointer(blob);
        nuint nativeSize = getBufferSize(blob);
        if (nativeSize > int.MaxValue)
            throw new InvalidOperationException("The HLSL compiler returned an unexpectedly large blob.");

        int size = checked((int)nativeSize);
        byte[] copy = new byte[size];
        if (size > 0)
            Marshal.Copy(data, copy, 0, size);
        return copy;
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
}
