using System;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using Vortice.Direct3D11;

namespace FPSBooster;

/// <summary>
/// Displays FluidSimulationProbe's plugin-owned water texture over a terrain-sampled world patch.
/// BGCollision supplies the patch vertices; the validated Scene.ViewMatrix × Render.ProjectionMatrix
/// projects each textured cell into the ImGui background draw list. When a scene-depth SRV is
/// available, a plugin-owned D3D11 composite shader rejects liquid fragments hidden by foreground geometry.
/// </summary>
public sealed class GroundedFluidProbe : IDisposable
{
    private struct SurfaceVertex
    {
        public bool Found;
        public Vector3 Position;
    }

    private readonly FluidSimulationProbe _fluid;
    private readonly IObjectTable _objectTable;
    private readonly SceneDepthCaptureProbe _sceneDepth;
    private readonly GroundedFluidDepthRenderer _depthRenderer;
    private SurfaceVertex[] _vertices = Array.Empty<SurfaceVertex>();
    private ProjectedFluidVertex[] _projectedVertices = Array.Empty<ProjectedFluidVertex>();
    private bool _enabled;
    private bool _depthOcclusionEnabled = true;
    private float _depthBias = 0.0000001f;
    private int _depthDebugMode;
    private int _depthSampleMode;
    private bool _hasAnchor;
    private bool _followPlayer;
    private bool _showMeshLines;
    private bool _rejectLargeSteps = true;
    private Vector3 _anchor;
    private Vector3 _surfaceNormal = Vector3.UnitY;
    private Vector3 _downhill = Vector3.UnitZ;
    private Vector3 _right = Vector3.UnitX;
    private float _surfaceGravityScale;
    private float _patchWidth = 3.5f;
    private float _patchDepth = 2.0f;
    private float _surfaceOffset = 0.025f;
    private float _maxCellHeightDelta = 0.30f;
    private float _castStartHeight = 3.5f;
    private float _castMaxDistance = 12f;
    private float _sampleIntervalSeconds = 0.35f;
    private float _opacity = 0.92f;
    private int _subdivisions = 8;
    private int _layerMask = 1;
    private DateTime _lastSampleUtc;
    private int _sampleHits;
    private int _drawnCells;
    private int _rejectedCells;
    private string _status = "No grounded patch placed.";
    private string _projectionStatus = "Not sampled.";
    private double _lastSampleMilliseconds;

    public GroundedFluidProbe(FluidSimulationProbe fluid, IObjectTable objectTable, IGameInteropProvider interop)
    {
        _fluid = fluid;
        _objectTable = objectTable;
        _sceneDepth = new SceneDepthCaptureProbe(interop, fluid);
        _depthRenderer = new GroundedFluidDepthRenderer(fluid);
    }

    public void Draw()
    {
        ImGui.TextWrapped("Combine the existing GPU fluid field with native BGCollision grounding. The fluid simulation remains 2D internally, but its display texture is mapped onto a subdivided grid of collision-sampled world points. Optional scene-depth compositing compares each liquid fragment against FFXIV's captured reversed-Z depth so characters, rocks, and other foreground geometry can hide the liquid.");
        ImGui.Separator();

        if (!_fluid.IsCreated)
        {
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.32f, 1f), "The shared Fluid Lab simulation has not been created yet.");
            if (ImGui.Button("Create GPU fluid simulation"))
                _fluid.Create();
        }
        else
        {
            ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f), "Shared Fluid Lab GPU simulation is ready.");
        }

        bool enabled = _enabled;
        if (ImGui.Checkbox("Enable grounded in-world liquid", ref enabled))
            SetEnabled(enabled);
        ImGui.SameLine();
        bool running = _fluid.IsRunning;
        if (ImGui.Checkbox("Run simulation", ref running))
            _fluid.SetRunning(running);

        if (ImGui.Button("Place / reset patch at player"))
            PlaceAtPlayer();
        ImGui.SameLine();
        if (ImGui.Button("Remove patch"))
        {
            _hasAnchor = false;
            SetEnabled(false);
            _status = "Grounded patch removed. Fluid simulation state is preserved.";
        }
        ImGui.SameLine();
        if (ImGui.Button("Refresh ground mesh"))
            SampleGroundMesh();

        if (_fluid.IsCreated)
        {
            if (ImGui.Button("Seed grounded pool"))
                _fluid.SeedGroundedPool();
            ImGui.SameLine();
            if (ImGui.Button("Clear liquid"))
                _fluid.ClearField();
        }

        ImGui.Separator();
        ImGui.Text("Ground patch");
        bool meshSettingsChanged = false;
        meshSettingsChanged |= ImGui.SliderFloat("Patch width", ref _patchWidth, 0.75f, 8f, "%.2f yalms");
        meshSettingsChanged |= ImGui.SliderFloat("Patch depth", ref _patchDepth, 0.75f, 8f, "%.2f yalms");
        meshSettingsChanged |= ImGui.SliderInt("Mesh subdivisions", ref _subdivisions, 3, 10);
        meshSettingsChanged |= ImGui.SliderFloat("Surface offset", ref _surfaceOffset, 0.005f, 0.08f, "%.3f yalms");
        meshSettingsChanged |= ImGui.SliderFloat("Ground refresh interval", ref _sampleIntervalSeconds, 0.20f, 1.0f, "%.2f s");
        meshSettingsChanged |= ImGui.SliderFloat("Overlay opacity", ref _opacity, 0.20f, 1.0f, "%.2f");
        ImGui.Checkbox("Use scene-depth occlusion", ref _depthOcclusionEnabled);
        if (_depthOcclusionEnabled)
        {
            ImGui.SliderFloat("Reversed-Z depth bias", ref _depthBias, 0f, 0.00001f, "%.8f");
            ImGui.TextDisabled("Uses the raw device-depth ordering from the matching scene projection. Larger reversed-Z values are closer. Keep this bias very small; it is not measured in yalms.");
            string[] depthModes =
            {
                "Composite liquid",
                "Projected liquid depth",
                "Scene depth at liquid pixels",
                "Occlusion mask (green visible / red hidden)",
                "Raw depth difference (red scene-front / blue liquid-front)",
                "Fluid texture RGB",
                "Fluid texture alpha",
            };
            ImGui.Combo("Depth diagnostic view", ref _depthDebugMode, depthModes, depthModes.Length);
            string[] sampleModes = { "Exact pixel sample", "2x2 maximum (conservative)" };
            ImGui.Combo("Depth sampling", ref _depthSampleMode, sampleModes, sampleModes.Length);
            ImGui.TextDisabled("Exact-pixel sampling is the default. The 2x2 maximum can hide liquid near character/terrain silhouettes. Diagnostic views bypass normal depth rejection so the comparison can be inspected.");
            ImGui.TextDisabled("Requires Scene Depth Capture to remain running with a selected single-sample SRV. If unavailable, the current depthless overlay is used as a fallback.");
        }
        if (ImGui.Checkbox("Follow player after placement", ref _followPlayer))
            _lastSampleUtc = DateTime.MinValue;
        ImGui.Checkbox("Show sampled mesh lines", ref _showMeshLines);
        ImGui.Checkbox("Reject cells across abrupt steps", ref _rejectLargeSteps);
        if (_rejectLargeSteps)
            meshSettingsChanged |= ImGui.SliderFloat("Maximum cell height jump", ref _maxCellHeightDelta, 0.05f, 1.0f, "%.2f yalms");
        ImGui.SliderFloat("Collision cast start height", ref _castStartHeight, 1f, 10f, "%.1f yalms");
        ImGui.SliderFloat("Collision max distance", ref _castMaxDistance, 4f, 30f, "%.1f yalms");
        ImGui.InputInt("Collision layer mask", ref _layerMask);
        if (_layerMask == 0)
            ImGui.TextColored(new Vector4(1f, 0.70f, 0.30f, 1f), "Layer mask 0 may reject mesh colliders; 1 is the validated starting value.");

        if (meshSettingsChanged && _hasAnchor)
            _lastSampleUtc = DateTime.MinValue;

        ImGui.Separator();
        ImGui.Text("Integration status");
        ImGui.TextWrapped(_status);
        ImGui.TextDisabled($"Ground samples: {_sampleHits}/{_vertices.Length} · textured cells: {_drawnCells} · rejected cells: {_rejectedCells}");
        ImGui.TextDisabled($"Ground sampling: {_lastSampleMilliseconds:F2} ms CPU · projection: {_projectionStatus}");
        ImGui.TextDisabled($"Anchor: {Format(_anchor)} · surface normal: {Format(_surfaceNormal)} · downhill: {Format(_downhill)}");
        ImGui.TextDisabled($"Slope: {MathF.Asin(Math.Clamp(_surfaceGravityScale, 0f, 1f)) * (180f / MathF.PI):0.0}° · tangential gravity scale: {_surfaceGravityScale:0.000}");
        ImGui.TextDisabled("The overlay is drawn from the plugin draw hook and remains visible when the FPSBooster window is closed, as long as it is enabled and a patch is placed. Fixed patches sample once at placement (or manual refresh); follow-player patches resample at the configured interval.");
        if (_depthOcclusionEnabled)
            ImGui.TextColored(new Vector4(0.45f, 0.85f, 0.45f, 1f), "Scene-depth occlusion is enabled. The plugin reads the captured scene depth and composites into its own texture; it never writes to FFXIV's depth buffer.");
        else
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.32f, 1f), "Scene-depth occlusion is disabled. The liquid is drawn as a depthless ImGui overlay and may appear through foreground geometry.");
        ImGui.TextDisabled(_depthRenderer.Status);
        if (_depthOcclusionEnabled && !_sceneDepth.IsCapturing)
            ImGui.TextColored(new Vector4(1f, 0.72f, 0.32f, 1f), "Depth capture is stopped. Start Scene Depth Capture below; until a selected SRV is available, the visible liquid uses the depthless fallback.");

        _sceneDepth.Draw();

        if (_fluid.IsCreated)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Shared simulation texture preview");
            ImGui.Image(_fluid.PreviewTextureId, new Vector2(320f, 180f));
        }
    }

    private void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        // Only take over Fluid Lab's frame driver when there is a world patch to display.
        _fluid.SetExternalDriverActive(enabled && _hasAnchor);
        if (enabled && _hasAnchor)
            _lastSampleUtc = DateTime.MinValue;
    }

    private void PlaceAtPlayer()
    {
        var player = _objectTable.LocalPlayer;
        if (player == null)
        {
            _status = "Cannot place grounded liquid: local player is unavailable.";
            return;
        }

        Vector3 playerPosition = player.Position;
        if (!CollisionProbe.TrySampleGroundPoint(playerPosition, _castStartHeight, _castMaxDistance, _layerMask,
                out Vector3 groundPoint, out Vector3 normal))
        {
            _status = "Cannot place grounded liquid: native ground raycast did not return a usable surface.";
            _hasAnchor = false;
            SetEnabled(false);
            return;
        }

        _anchor = groundPoint;
        _surfaceNormal = normal;
        Vector3 gravityOnSurface = -Vector3.UnitY - normal * Vector3.Dot(-Vector3.UnitY, normal);
        _surfaceGravityScale = Math.Clamp(gravityOnSurface.Length(), 0f, 1f);
        if (_surfaceGravityScale < 0.05f)
        {
            // On nearly level ground there is no meaningful downhill direction; use player-forward
            // to orient the texture, but remove tangential gravity so water does not flow arbitrarily.
            _surfaceGravityScale = 0f;
            Vector3 heading = new(MathF.Sin(player.Rotation), 0f, MathF.Cos(player.Rotation));
            gravityOnSurface = heading - normal * Vector3.Dot(heading, normal);
        }
        if (gravityOnSurface.LengthSquared() < 1e-6f)
        {
            _status = "Ground point found, but a stable tangent direction could not be constructed.";
            _hasAnchor = false;
            SetEnabled(false);
            return;
        }

        _downhill = Vector3.Normalize(gravityOnSurface);
        Vector3 right = Vector3.Cross(normal, _downhill);
        if (right.LengthSquared() < 1e-6f)
        {
            _status = "Ground point found, but the surface basis is degenerate.";
            _hasAnchor = false;
            SetEnabled(false);
            return;
        }
        _right = Vector3.Normalize(right);
        _surfaceNormal = normal;
        _fluid.SetExternalGravityScale(_surfaceGravityScale);
        _hasAnchor = true;
        SetEnabled(true);
        SampleGroundMesh();
        _lastSampleUtc = DateTime.UtcNow;
        _status = "Grounded liquid patch placed. Texture + ground mesh are projected at the saved world position.";
    }

    /// <summary>Called once per Dalamud UI frame, independently of the ConfigWindow visibility.</summary>
    /// <summary>Runs before the main window draw so any refreshed SRV remains alive for ImGui's deferred draw commands.</summary>
    public void ProcessSceneDepthFrame()
    {
        _sceneDepth.ProcessFrameAtUiStage();
    }

    public void DrawOverlay()
    {
        try
        {
            DrawOverlayCore();
        }
        catch (Exception ex)
        {
            _projectionStatus = "Grounded overlay draw failed: " + ex.GetBaseException().Message;
        }
    }

    private void DrawOverlayCore()
    {
        if (!_enabled || !_hasAnchor)
            return;

        if (!_fluid.IsCreated)
        {
            _projectionStatus = "Waiting for the Fluid Lab GPU field to be created.";
            return;
        }

        _fluid.AdvanceExternalFrame();

        DateTime now = DateTime.UtcNow;
        bool sampleDue = _lastSampleUtc == DateTime.MinValue
            || (_followPlayer && (now - _lastSampleUtc).TotalSeconds >= _sampleIntervalSeconds);
        if (sampleDue)
        {
            if (_followPlayer)
                RefreshAnchorFromPlayer();
            SampleGroundMesh();
        }

        ID3D11ShaderResourceView? sceneDepthSrv = null;
        uint depthWidth = 0;
        uint depthHeight = 0;
        Matrix4x4 viewProjection = default;
        string depthProjectionStatus = "Depth capture is not being used.";
        bool hasSynchronizedDepth = _depthOcclusionEnabled
            && _sceneDepth.TryAcquireSelectedDepthSrv(out sceneDepthSrv, out depthWidth, out depthHeight,
                out viewProjection, out depthProjectionStatus);

        if (hasSynchronizedDepth)
        {
            _projectionStatus = "Depth projection synchronized to the selected DSV bind; raw reversed-Z comparison. " + depthProjectionStatus;
        }
        else if (!CollisionProbe.TryCaptureWorldProjection(out viewProjection, out string projectionStatus))
        {
            sceneDepthSrv?.Dispose();
            _projectionStatus = _depthOcclusionEnabled
                ? "No depth-matched camera snapshot is available. " + projectionStatus + " " + depthProjectionStatus
                : projectionStatus;
            return;
        }
        else
        {
            _projectionStatus = _depthOcclusionEnabled
                ? "Using live camera projection for depthless fallback; waiting for a DSV-bound camera snapshot. " + depthProjectionStatus
                : "Validated Scene.ViewMatrix × Render.ProjectionMatrix; depthless projection.";
        }

        var viewport = ImGui.GetMainViewport();
        Vector2 viewportPos = viewport.Pos;
        Vector2 viewportSize = viewport.Size;
        if (viewportSize.X <= 1f || viewportSize.Y <= 1f)
        {
            sceneDepthSrv?.Dispose();
            return;
        }

        int side = _subdivisions + 1;
        if (_vertices.Length != side * side)
        {
            sceneDepthSrv?.Dispose();
            return;
        }
        if (_projectedVertices.Length != _vertices.Length)
            _projectedVertices = new ProjectedFluidVertex[_vertices.Length];

        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                int index = y * side + x;
                SurfaceVertex vertex = _vertices[index];
                float u = (float)x / _subdivisions;
                float v = (float)y / _subdivisions;
                if (vertex.Found && CollisionProbe.TryProjectWorldPoint(vertex.Position, viewProjection,
                        viewportPos, viewportSize, out Vector2 screen, out float depth))
                {
                    _projectedVertices[index] = new ProjectedFluidVertex
                    {
                        Valid = true,
                        Screen = screen - viewportPos,
                        Uv = new Vector2(u, v),
                        Depth = depth,
                        WorldY = vertex.Position.Y,
                    };
                }
                else
                {
                    _projectedVertices[index] = default;
                }
            }
        }

        var drawList = ImGui.GetBackgroundDrawList();
        _drawnCells = 0;
        _rejectedCells = 0;

        // Preferred path: map the grounded mesh into a plugin-owned patch texture, then
        // sample FFXIV's scene depth in a shader. The scene depth SRV is read-only and
        // is never used as an output target.
        if (hasSynchronizedDepth && sceneDepthSrv != null)
        {
            using (sceneDepthSrv)
            {
                if (_depthRenderer.Render(sceneDepthSrv, depthWidth, depthHeight, viewportSize,
                        _projectedVertices, _subdivisions, _rejectLargeSteps, _maxCellHeightDelta,
                        _depthBias, _opacity, _depthDebugMode, _depthSampleMode,
                        out Vector2 imageMin, out Vector2 imageMax))
                {
                    drawList.AddImage(_depthRenderer.TextureId, viewportPos + imageMin, viewportPos + imageMax,
                        Vector2.Zero, Vector2.One, 0xFFFFFFFF);
                    _drawnCells = _depthRenderer.DrawnCells;
                    _rejectedCells = _depthRenderer.RejectedCells;
                    _projectionStatus = "Depth-aware composite active · " + _depthRenderer.Status;
                    return;
                }

                _projectionStatus = "Depth compositor unavailable; using depthless fallback. " + _depthRenderer.Status;
            }
        }
        else if (_depthOcclusionEnabled)
        {
            sceneDepthSrv?.Dispose();
            _projectionStatus = _sceneDepth.IsCapturing
                ? "Waiting for a single-sample SRV with a matching DSV camera snapshot; using depthless fallback."
                : "Start Scene Depth Capture and leave it running to enable scene occlusion; using depthless fallback.";
        }
        else
        {
            _projectionStatus = "Depthless ImGui fallback selected.";
        }

        // Compatibility path: keep the original ImGui mesh visible if scene-depth capture
        // is not active or a compatible SRV cannot be acquired.
        ImTextureID texture = _fluid.PreviewTextureId;
        uint tint = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, _opacity));
        uint meshColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.10f, 0.92f, 1.0f, 0.62f));
        for (int y = 0; y < _subdivisions; y++)
        {
            for (int x = 0; x < _subdivisions; x++)
            {
                int i00 = y * side + x;
                int i10 = i00 + 1;
                int i01 = i00 + side;
                int i11 = i01 + 1;
                SurfaceVertex v00 = _vertices[i00];
                SurfaceVertex v10 = _vertices[i10];
                SurfaceVertex v11 = _vertices[i11];
                SurfaceVertex v01 = _vertices[i01];
                ProjectedFluidVertex p00v = _projectedVertices[i00];
                ProjectedFluidVertex p10v = _projectedVertices[i10];
                ProjectedFluidVertex p11v = _projectedVertices[i11];
                ProjectedFluidVertex p01v = _projectedVertices[i01];
                if (!v00.Found || !v10.Found || !v11.Found || !v01.Found
                    || !p00v.Valid || !p10v.Valid || !p11v.Valid || !p01v.Valid)
                {
                    _rejectedCells++;
                    continue;
                }

                if (_rejectLargeSteps)
                {
                    float minY = Math.Min(Math.Min(v00.Position.Y, v10.Position.Y), Math.Min(v11.Position.Y, v01.Position.Y));
                    float maxY = Math.Max(Math.Max(v00.Position.Y, v10.Position.Y), Math.Max(v11.Position.Y, v01.Position.Y));
                    if (maxY - minY > _maxCellHeightDelta)
                    {
                        _rejectedCells++;
                        continue;
                    }
                }

                drawList.AddImageQuad(texture, viewportPos + p00v.Screen, viewportPos + p10v.Screen,
                    viewportPos + p11v.Screen, viewportPos + p01v.Screen,
                    p00v.Uv, p10v.Uv, p11v.Uv, p01v.Uv, tint);
                _drawnCells++;

                if (_showMeshLines)
                {
                    drawList.AddLine(viewportPos + p00v.Screen, viewportPos + p10v.Screen, meshColor, 1f);
                    drawList.AddLine(viewportPos + p10v.Screen, viewportPos + p11v.Screen, meshColor, 1f);
                    drawList.AddLine(viewportPos + p11v.Screen, viewportPos + p01v.Screen, meshColor, 1f);
                    drawList.AddLine(viewportPos + p01v.Screen, viewportPos + p00v.Screen, meshColor, 1f);
                }
            }
        }
    }

    private void RefreshAnchorFromPlayer()
    {
        var player = _objectTable.LocalPlayer;
        if (player == null)
            return;

        Vector3 position = player.Position;
        if (!CollisionProbe.TrySampleGroundPoint(position, _castStartHeight, _castMaxDistance, _layerMask,
                out Vector3 groundPoint, out Vector3 normal))
            return;

        _anchor = groundPoint;
        _surfaceNormal = normal;
        Vector3 gravityOnSurface = -Vector3.UnitY - normal * Vector3.Dot(-Vector3.UnitY, normal);
        _surfaceGravityScale = Math.Clamp(gravityOnSurface.Length(), 0f, 1f);
        if (_surfaceGravityScale < 0.05f)
        {
            _surfaceGravityScale = 0f;
            Vector3 heading = new(MathF.Sin(player.Rotation), 0f, MathF.Cos(player.Rotation));
            gravityOnSurface = heading - normal * Vector3.Dot(heading, normal);
        }
        if (gravityOnSurface.LengthSquared() < 1e-6f)
            return;
        _downhill = Vector3.Normalize(gravityOnSurface);
        Vector3 right = Vector3.Cross(normal, _downhill);
        if (right.LengthSquared() > 1e-6f)
            _right = Vector3.Normalize(right);
        _fluid.SetExternalGravityScale(_surfaceGravityScale);
    }

    private void SampleGroundMesh()
    {
        if (!_hasAnchor)
            return;

        var timer = Stopwatch.StartNew();
        int side = _subdivisions + 1;
        if (_vertices.Length != side * side)
            _vertices = new SurfaceVertex[side * side];

        _sampleHits = 0;
        for (int y = 0; y < side; y++)
        {
            float v = (float)y / _subdivisions;
            float downOffset = (v - 0.5f) * _patchDepth;
            for (int x = 0; x < side; x++)
            {
                float u = (float)x / _subdivisions;
                float rightOffset = (u - 0.5f) * _patchWidth;
                Vector3 query = _anchor + _right * rightOffset + _downhill * downOffset;
                int index = y * side + x;
                if (CollisionProbe.TrySampleGroundPoint(query, _castStartHeight, _castMaxDistance, _layerMask,
                        out Vector3 point, out Vector3 normal))
                {
                    _vertices[index] = new SurfaceVertex
                    {
                        Found = true,
                        Position = point + normal * _surfaceOffset,
                    };
                    _sampleHits++;
                }
                else
                {
                    _vertices[index] = default;
                }
            }
        }
        timer.Stop();
        _lastSampleMilliseconds = timer.Elapsed.TotalMilliseconds;
        _lastSampleUtc = DateTime.UtcNow;
        _status = $"Ground mesh sampled: {_sampleHits}/{_vertices.Length} vertices hit. Texture is mapped over valid cells.";
    }

    public void Dispose()
    {
        _enabled = false;
        _fluid.SetExternalDriverActive(false);
        _fluid.SetExternalGravityScale(1f);
        _vertices = Array.Empty<SurfaceVertex>();
        _projectedVertices = Array.Empty<ProjectedFluidVertex>();
        _depthRenderer.Dispose();
        _sceneDepth.Dispose();
    }

    private static string Format(Vector3 value) => $"({value.X:0.000}, {value.Y:0.000}, {value.Z:0.000})";
}
