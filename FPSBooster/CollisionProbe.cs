using System;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using ControlStruct = FFXIVClientStructs.FFXIV.Client.Game.Control.Control;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;
using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using NativeRaycastHit = FFXIVClientStructs.FFXIV.Common.Component.BGCollision.RaycastHit;

namespace FPSBooster;

/// <summary>
/// Experimental world-grounding probe. It asks the game's BGCollision scene for surface
/// contact points and normals. Its optional ImGui overlay visualizes projected query results;
/// it does not draw or move VFX. The world-space data is intended for RealDebuffs' 3D renderer.
/// </summary>
public sealed unsafe class CollisionProbe : IDisposable
{
    private const float DefaultStartHeight = 2.0f;
    private const float DefaultMaxDistance = 8.0f;
    private const float DefaultSweepRadius = 0.25f;
    private const float DefaultGridSpacing = 0.35f;
    private const double LiveSampleIntervalSeconds = 0.20d;
    private const int EmbeddedSceneCameraOffset = 0x10;

    private readonly IObjectTable _objectTable;
    private readonly GroundSample[] _grid = new GroundSample[9];
    private bool _liveSampling;
    private float _offsetX;
    private float _offsetZ;
    private float _startHeight = DefaultStartHeight;
    private float _maxDistance = DefaultMaxDistance;
    private float _sweepRadius = DefaultSweepRadius;
    private float _gridSpacing = DefaultGridSpacing;
    private int _layerMask = 1;
    private long _lastLiveSampleTimestamp;
    private bool _hasResult;
    private string _status = "No query yet.";
    private Vector3 _playerPosition;
    private Vector3 _targetPosition;
    private Vector3 _castOrigin;
    private NativeRaycastHit _rayHit;
    private bool _rayFound;
    private NativeRaycastHit _sweepHit;
    private bool _sweepFound;
    private float _lastSweepRadius;
    private Vector3 _sweepCenterAtHit;
    private DateTime _lastQueryUtc;
    private string? _lastError;
    private bool _showWorldOverlay;
    private bool _showPatchOverlay = true;
    private bool _showSweepOverlay = true;
    private bool _showSampleNormals = true;
    private bool _showGroundFrame = true;
    private float _groundFrameLength = 0.90f;
    private int _groundFrameNormalSource;
    private float _playerRotation;
    private bool _hasCenterNormal;
    private Vector3 _centerNormal;
    private string _centerNormalSource = "unavailable";
    private bool _hasPlaneFit;
    private Vector3 _planeNormal;
    private Vector3 _planeAnchor;
    private float _planeRmsError;
    private float _planeHeightRange;
    private int _planeSampleCount;
    private bool _hasGroundFrame;
    private Vector3 _groundFrameOrigin;
    private Vector3 _groundUp;
    private Vector3 _groundForward;
    private Vector3 _groundRight;
    private string _groundFrameNormalSourceDescription = "unavailable";
    private string _cameraDiagnosticStatus = "Scene-camera projection has not been sampled yet.";
    private CameraMatrixSnapshot _frameCameraMatrices;
    private bool _hasFrameCameraMatrices;

    private struct CameraMatrixSnapshot
    {
        public Matrix4x4 SceneViewProjection;
        public Matrix4x4 SceneViewMatrix;
        public Matrix4x4 RenderProjectionMatrix;
        public nint GameCameraAddress;
        public nint SceneCameraAddress;
        public nint RenderCameraAddress;
    }

    private struct GroundSample
    {
        public Vector2 Offset;
        public bool Found;
        public NativeRaycastHit Hit;
    }

    public CollisionProbe(IObjectTable objectTable)
    {
        _objectTable = objectTable;
    }

    public void Draw()
    {
        ImGui.TextWrapped("Probe the game's native background-collision scene beneath or around the local player. The raycast and sphere sweep return real world-space contact points and surface data. The optional projected overlay helps inspect those results; the world-space values are the grounding inputs that RealDebuffs' existing 3D renderer could consume.");
        ImGui.Separator();

        var player = _objectTable.LocalPlayer;
        if (player == null)
        {
            ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), "Local player is not available. Enter the world before running collision queries.");
            _liveSampling = false;
            return;
        }

        ImGui.TextDisabled($"Player position: {Format(player.Position)}   Rotation: {player.Rotation:0.000} rad");
        ImGui.TextDisabled("Target offsets are world X/Z offsets from the player's current position.");

        ImGui.Separator();
        ImGui.Text("Probe target");
        ImGui.SliderFloat("Target offset X", ref _offsetX, -5f, 5f, "%+.2f yalms");
        ImGui.SliderFloat("Target offset Z", ref _offsetZ, -5f, 5f, "%+.2f yalms");
        ImGui.SliderFloat("Cast start height", ref _startHeight, 0.25f, 8f, "%.2f yalms");
        ImGui.SliderFloat("Maximum cast distance", ref _maxDistance, 1f, 30f, "%.1f yalms");
        ImGui.SliderFloat("Sphere sweep radius", ref _sweepRadius, 0.05f, 1.5f, "%.2f yalms");
        ImGui.SliderFloat("Ground sample spacing", ref _gridSpacing, 0.05f, 1.5f, "%.2f yalms");
        ImGui.InputInt("Collision layer mask", ref _layerMask);
        if (_layerMask == 0)
            ImGui.TextColored(new Vector4(1f, .7f, .3f, 1f), "A zero layer mask can reject all mesh colliders. The default mask is 1.");

        if (ImGui.Button("Probe now"))
            RunProbe();
        ImGui.SameLine();
        ImGui.Checkbox("Live sample (5 Hz)", ref _liveSampling);
        ImGui.SameLine();
        if (ImGui.SmallButton("Copy report") && _hasResult)
            ImGui.SetClipboardText(new ImU8String(BuildReport()).Span);

        ImGui.Separator();
        ImGui.Checkbox("Draw projected world overlay", ref _showWorldOverlay);
        ImGui.SameLine();
        ImGui.Checkbox("3×3 patch", ref _showPatchOverlay);
        ImGui.SameLine();
        ImGui.Checkbox("Sweep sphere", ref _showSweepOverlay);
        ImGui.Checkbox("Per-sample surface normals", ref _showSampleNormals);
        ImGui.SameLine();
        ImGui.Checkbox("Grounded orientation frame", ref _showGroundFrame);
        ImGui.SliderFloat("Ground-frame axis length", ref _groundFrameLength, 0.20f, 2.50f, "%.2f yalms");
        string[] normalSources =
        {
            "Center hit normal (local contact)",
            "3×3 fitted plane (neighborhood)",
        };
        ImGui.Combo("Ground-frame up source", ref _groundFrameNormalSource, normalSources, normalSources.Length);
        _groundFrameNormalSource = Math.Clamp(_groundFrameNormalSource, 0, normalSources.Length - 1);
        _playerRotation = player.Rotation;
        if (_hasResult)
            ComputeSurfaceAnalysis();
        ImGui.TextDisabled("Overlay projection is fixed to the tested Scene Camera ViewMatrix × Render Camera ProjectionMatrix combination. It is sampled live each frame; no world-position smoothing is applied. The overlay remains an ImGui diagnostic and is not depth-tested.");

        _hasFrameCameraMatrices = false;
        if (_showWorldOverlay)
        {
            _hasFrameCameraMatrices = TryCaptureCameraMatrices(out _frameCameraMatrices, out _cameraDiagnosticStatus);
            if (_hasFrameCameraMatrices)
                ImGui.TextDisabled($"Projection: Scene.ViewMatrix × Render.ProjectionMatrix · scene 0x{_frameCameraMatrices.SceneCameraAddress:X} · render 0x{_frameCameraMatrices.RenderCameraAddress:X}");
        }

        if (_liveSampling)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastLiveSampleTimestamp == 0 || Stopwatch.GetElapsedTime(_lastLiveSampleTimestamp, now).TotalSeconds >= LiveSampleIntervalSeconds)
            {
                RunProbe();
                _lastLiveSampleTimestamp = now;
            }
        }
        else
        {
            _lastLiveSampleTimestamp = 0;
        }

        ImGui.Separator();
        if (!_hasResult)
        {
            ImGui.TextDisabled(_status);
            return;
        }

        ImGui.TextColored(new Vector4(.45f, .85f, .45f, 1f), $"Last query: {_status} · {_lastQueryUtc.ToLocalTime():HH:mm:ss.fff}");
        if (!string.IsNullOrEmpty(_lastError))
            ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), _lastError);

        ImGui.TextDisabled($"Player: {Format(_playerPosition)}");
        ImGui.TextDisabled($"Target: {Format(_targetPosition)}   Cast origin: {Format(_castOrigin)}");

        ImGui.Separator();
        ImGui.Text("1. Vertical raycast");
        DrawHitSummary(_rayFound, _rayHit, "Raycast");

        ImGui.Separator();
        ImGui.Text("2. Vertical sphere sweep");
        ImGui.TextDisabled($"Sphere radius: {_lastSweepRadius:0.00} yalms");
        DrawHitSummary(_sweepFound, _sweepHit, "Sphere sweep");
        if (_sweepFound)
            ImGui.Text($"Estimated sphere-center position at impact: {Format(_sweepCenterAtHit)}");

        ImGui.Separator();
        ImGui.Text("3. 3×3 ground patch");
        ImGui.TextDisabled("Nine downward rays around the target. Columns are world X− / X0 / X+; rows are world Z− / Z0 / Z+.");
        int hitCount = 0;
        for (int i = 0; i < _grid.Length; i++)
            if (_grid[i].Found) hitCount++;
        ImGui.Text($"Hits: {hitCount}/9");
        for (int z = 0; z < 3; z++)
        {
            for (int x = 0; x < 3; x++)
            {
                var sample = _grid[z * 3 + x];
                if (x != 0) ImGui.SameLine(0f, 12f);
                if (!sample.Found)
                {
                    ImGui.TextDisabled("  --  ");
                    continue;
                }
                float slope = TryGetSurfaceNormal(sample.Hit, out var normal, out _)
                    ? MathF.Acos(Math.Clamp(normal.Y, -1f, 1f)) * (180f / MathF.PI)
                    : float.NaN;
                ImGui.Text($"{sample.Hit.Point.Y,7:0.00}y / {slope,5:0.0}°");
            }
        }

        ImGui.Separator();
        ImGui.Text("4. Surface and orientation analysis");
        if (_hasPlaneFit)
        {
            ImGui.TextDisabled($"Plane fit: {_planeSampleCount}/9 samples · height range {_planeHeightRange:0.000} yalms · RMS deviation {_planeRmsError:0.000} yalms");
            ImGui.Text($"Neighborhood plane normal: {Format(_planeNormal)} · slope {SlopeDegrees(_planeNormal):0.0}°");
        }
        else
        {
            ImGui.TextDisabled($"Plane fit unavailable: {_planeSampleCount}/9 hits (need at least three non-collinear points).");
        }
        if (_hasCenterNormal)
        {
            ImGui.Text($"Center contact normal ({_centerNormalSource}): {Format(_centerNormal)} · slope {SlopeDegrees(_centerNormal):0.0}°");
            if (_hasPlaneFit)
                ImGui.TextDisabled($"Center ↔ neighborhood normal difference: {AngleBetweenDegrees(_centerNormal, _planeNormal):0.00}°");
        }
        if (_hasGroundFrame)
        {
            ImGui.TextDisabled($"Ground frame origin: {Format(_groundFrameOrigin)}");
            ImGui.TextDisabled($"Up: {Format(_groundUp)} · Forward: {Format(_groundForward)} · Right: {Format(_groundRight)}");
        }
        else
        {
            ImGui.TextDisabled("Ground frame unavailable: no usable center or neighborhood normal.");
        }

        ImGui.Separator();
        ImGui.TextWrapped("Interpretation: the center triangle normal describes the exact contact triangle, while the 3×3 fitted plane averages the neighborhood. On a smooth ramp they should agree. On stairs or irregular terrain, the fit error and normal difference reveal that one broad planar orientation may not describe the whole patch. The grounded frame projects the player's heading onto the selected surface plane and builds an orthogonal up/forward/right basis for a future RealDebuffs mesh attachment.");

        if (_showWorldOverlay)
        {
            if (_hasFrameCameraMatrices)
                DrawWorldOverlay(_frameCameraMatrices);
            else
                ImGui.TextColored(new Vector4(1f, .55f, .35f, 1f), "World overlay skipped: Scene.ViewMatrix × Render.ProjectionMatrix is unavailable. " + _cameraDiagnosticStatus);
        }
    }

    public void StopLiveSampling()
    {
        _liveSampling = false;
        _lastLiveSampleTimestamp = 0;
    }

    private void RunProbe()
    {
        try
        {
            var player = _objectTable.LocalPlayer;
            if (player == null)
            {
                _status = "No local player.";
                _hasResult = false;
                return;
            }

            var framework = Framework.Instance();
            if (framework == null)
            {
                _status = "Framework.Instance() returned null.";
                _hasResult = false;
                return;
            }

            var module = framework->BGCollisionModule;
            if (module == null || module->ShuttingDown)
            {
                _status = "BGCollisionModule is unavailable or shutting down.";
                _hasResult = false;
                return;
            }

            _playerPosition = player.Position;
            _playerRotation = player.Rotation;
            _targetPosition = _playerPosition + new Vector3(_offsetX, 0f, _offsetZ);
            _castOrigin = _targetPosition + Vector3.UnitY * _startHeight;
            Vector3 direction = -Vector3.UnitY;
            _lastSweepRadius = _sweepRadius;

            _rayFound = Raycast(module, _castOrigin, direction, _maxDistance, _layerMask, out _rayHit);
            _sweepFound = SphereSweep(module, _castOrigin, direction, _maxDistance, _layerMask, _sweepRadius, out _sweepHit);
            _sweepCenterAtHit = _sweepFound ? _castOrigin + direction * _sweepHit.Distance : default;

            int index = 0;
            for (int z = -1; z <= 1; z++)
            {
                for (int x = -1; x <= 1; x++)
                {
                    var offset = new Vector2(x * _gridSpacing, z * _gridSpacing);
                    var sampleOrigin = _castOrigin + new Vector3(offset.X, 0f, offset.Y);
                    bool found;
                    NativeRaycastHit hit;
                    if (x == 0 && z == 0)
                    {
                        found = _rayFound;
                        hit = _rayHit;
                    }
                    else
                    {
                        found = Raycast(module, sampleOrigin, direction, _maxDistance, _layerMask, out hit);
                    }

                    _grid[index++] = new GroundSample { Offset = offset, Found = found, Hit = hit };
                }
            }

            ComputeSurfaceAnalysis();

            int count = 0;
            for (int i = 0; i < _grid.Length; i++)
                if (_grid[i].Found) count++;

            _status = $"Ray: {(_rayFound ? "hit" : "miss")} · sweep: {(_sweepFound ? "hit" : "miss")} · patch: {count}/9";
            _lastQueryUtc = DateTime.UtcNow;
            _lastError = null;
            _hasResult = true;
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _status = "Collision query failed.";
            _hasResult = false;
        }
    }

    /// <summary>Draws saved collision results as a screen-space debug overlay using the validated live Scene×Render projection.</summary>
    private void DrawWorldOverlay(in CameraMatrixSnapshot matrices)
    {
        if (!_hasResult)
            return;

        try
        {
            Matrix4x4 viewProjection = matrices.SceneViewProjection;
            var viewport = ImGui.GetMainViewport();
            Vector2 viewportPos = viewport.Pos;
            Vector2 viewportSize = viewport.Size;
            if (viewportSize.X <= 1f || viewportSize.Y <= 1f)
                return;

            var drawList = ImGui.GetBackgroundDrawList();
            uint rayColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.20f, 1.00f, 0.35f, 0.95f));
            uint sweepColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 0.68f, 0.16f, 0.95f));
            uint gridColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.78f, 1.00f, 0.88f));

            if (_rayFound && TryProjectWorld(_castOrigin, viewProjection, viewportPos, viewportSize, out var castScreen)
                && TryProjectWorld(_rayHit.Point, viewProjection, viewportPos, viewportSize, out var rayScreen))
            {
                drawList.AddLine(castScreen, rayScreen, rayColor, 2.0f);
                drawList.AddCircle(rayScreen, 9f, rayColor, 20, 2.0f);
                drawList.AddCircleFilled(rayScreen, 3.5f, rayColor, 12);
            }

            if (_hasGroundFrame && _showGroundFrame
                && TryProjectWorld(_groundFrameOrigin, viewProjection, viewportPos, viewportSize, out var frameOriginScreen))
            {
                uint upColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 0.20f, 0.85f, 0.98f));
                uint forwardColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1.00f, 0.78f, 0.18f, 0.98f));
                uint rightColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.15f, 0.90f, 1.00f, 0.98f));

                if (TryProjectWorld(_groundFrameOrigin + _groundUp * _groundFrameLength, viewProjection, viewportPos, viewportSize, out var upEnd))
                {
                    drawList.AddLine(frameOriginScreen, upEnd, upColor, 3.0f);
                    drawList.AddCircleFilled(upEnd, 3.0f, upColor, 10);
                }
                if (TryProjectWorld(_groundFrameOrigin + _groundForward * _groundFrameLength, viewProjection, viewportPos, viewportSize, out var forwardEnd))
                {
                    drawList.AddLine(frameOriginScreen, forwardEnd, forwardColor, 3.0f);
                    drawList.AddCircleFilled(forwardEnd, 3.0f, forwardColor, 10);
                }
                if (TryProjectWorld(_groundFrameOrigin + _groundRight * _groundFrameLength, viewProjection, viewportPos, viewportSize, out var rightEnd))
                {
                    drawList.AddLine(frameOriginScreen, rightEnd, rightColor, 3.0f);
                    drawList.AddCircleFilled(rightEnd, 3.0f, rightColor, 10);
                }
                drawList.AddCircleFilled(frameOriginScreen, 4.5f, upColor, 12);
            }

            if (_sweepFound && _showSweepOverlay
                && TryProjectWorld(_sweepCenterAtHit, viewProjection, viewportPos, viewportSize, out var sweepScreen))
            {
                drawList.AddCircle(sweepScreen, 6f, sweepColor, 18, 2f);
                drawList.AddCircleFilled(sweepScreen, 2.5f, sweepColor, 10);

                if (ViewFrustum.FromViewProjection(viewProjection, 0f, out var frustum, out _)
                    && TryProjectWorld(_sweepCenterAtHit + frustum.Right * _lastSweepRadius, viewProjection, viewportPos, viewportSize, out var radiusScreen))
                {
                    float pixelRadius = Vector2.Distance(sweepScreen, radiusScreen);
                    if (float.IsFinite(pixelRadius) && pixelRadius >= 2f && pixelRadius <= 250f)
                        drawList.AddCircle(sweepScreen, pixelRadius, sweepColor, 32, 1.5f);
                }
            }

            if (_showPatchOverlay)
            {
                Vector2[] projected = new Vector2[_grid.Length];
                bool[] visible = new bool[_grid.Length];
                for (int i = 0; i < _grid.Length; i++)
                {
                    var sample = _grid[i];
                    if (!sample.Found)
                        continue;
                    visible[i] = TryProjectWorld(sample.Hit.Point, viewProjection, viewportPos, viewportSize, out projected[i]);
                }

                for (int z = 0; z < 3; z++)
                {
                    for (int x = 0; x < 3; x++)
                    {
                        int index = z * 3 + x;
                        if (!visible[index])
                            continue;

                        if (x < 2 && visible[index + 1])
                            drawList.AddLine(projected[index], projected[index + 1], gridColor, 1.5f);
                        if (z < 2 && visible[index + 3])
                            drawList.AddLine(projected[index], projected[index + 3], gridColor, 1.5f);

                        drawList.AddCircleFilled(projected[index], 4f, gridColor, 12);
                    }
                }
            }

            if (_showSampleNormals)
            {
                uint sampleNormalColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.72f, 0.48f, 1.00f, 0.92f));
                for (int i = 0; i < _grid.Length; i++)
                {
                    var sample = _grid[i];
                    if (!sample.Found || !TryGetSurfaceNormal(sample.Hit, out var normal, out _))
                        continue;
                    if (TryProjectWorld(sample.Hit.Point, viewProjection, viewportPos, viewportSize, out var start)
                        && TryProjectWorld(sample.Hit.Point + normal * 0.38f, viewProjection, viewportPos, viewportSize, out var end))
                    {
                        drawList.AddLine(start, end, sampleNormalColor, 1.8f);
                        drawList.AddCircleFilled(end, 2.2f, sampleNormalColor, 8);
                    }
                }
            }

            if (_hasPlaneFit && TryProjectWorld(_planeAnchor, viewProjection, viewportPos, viewportSize, out var planeStart)
                && TryProjectWorld(_planeAnchor + _planeNormal * _groundFrameLength, viewProjection, viewportPos, viewportSize, out var planeEnd))
            {
                uint planeColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.88f, 0.95f, 0.98f, 0.92f));
                drawList.AddLine(planeStart, planeEnd, planeColor, 2.0f);
            }
        }
        catch
        {
            // Debug visualization must never interrupt the collision query or plugin UI.
        }
    }

    /// <summary>Returns the validated scene view-projection and the exact render projection used to encode device depth.</summary>
    public static bool TryCaptureWorldProjection(out Matrix4x4 viewProjection, out Matrix4x4 projectionMatrix, out string status)
    {
        if (TryCaptureCameraMatrices(out var snapshot, out status))
        {
            viewProjection = snapshot.SceneViewProjection;
            projectionMatrix = snapshot.RenderProjectionMatrix;
            return true;
        }

        viewProjection = default;
        projectionMatrix = default;
        return false;
    }

    /// <summary>Returns the validated Scene.ViewMatrix × Render.ProjectionMatrix used by the Collision Lab overlay.</summary>
    public static bool TryCaptureWorldProjection(out Matrix4x4 viewProjection, out string status)
    {
        if (TryCaptureWorldProjection(out viewProjection, out _, out status))
            return true;

        viewProjection = default;
        return false;
    }

    /// <summary>Projects a world point using the same clip convention and viewport mapping as Collision Lab.</summary>
    public static bool TryProjectWorldPoint(Vector3 world, Matrix4x4 viewProjection, Vector2 viewportPos, Vector2 viewportSize, out Vector2 screen)
        => TryProjectWorld(world, viewProjection, viewportPos, viewportSize, out screen);

    /// <summary>Projects a point using the validated scene-camera matrix and returns its 0..1 reversed-Z depth.</summary>
    public static bool TryProjectWorldPoint(Vector3 world, Matrix4x4 viewProjection, Vector2 viewportPos,
        Vector2 viewportSize, out Vector2 screen, out float depth)
    {
        screen = default;
        depth = 0f;
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        if (!float.IsFinite(clip.W) || clip.W <= 0.001f)
            return false;

        float inverseW = 1f / clip.W;
        float ndcX = clip.X * inverseW;
        float ndcY = clip.Y * inverseW;
        float ndcZ = clip.Z * inverseW;
        if (!float.IsFinite(ndcX) || !float.IsFinite(ndcY) || !float.IsFinite(ndcZ)
            || ndcZ < 0f || ndcZ > 1f || ndcX < -1.2f || ndcX > 1.2f || ndcY < -1.2f || ndcY > 1.2f)
            return false;

        screen = viewportPos + new Vector2(
            (ndcX * 0.5f + 0.5f) * viewportSize.X,
            (0.5f - ndcY * 0.5f) * viewportSize.Y);
        depth = ndcZ;
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    /// <summary>Samples the BGCollision scene directly below a world X/Z position and returns its usable up-facing normal.</summary>
    public static bool TrySampleGroundPoint(Vector3 position, float startHeight, float maxDistance, int layerMask, out Vector3 hitPoint, out Vector3 surfaceNormal)
    {
        hitPoint = default;
        surfaceNormal = default;
        try
        {
            var framework = Framework.Instance();
            if (framework == null)
                return false;

            BGCollisionModule* module = framework->BGCollisionModule;
            if (module == null || module->ShuttingDown)
                return false;

            Vector3 origin = position + Vector3.UnitY * Math.Max(startHeight, 0.1f);
            if (!Raycast(module, origin, -Vector3.UnitY, Math.Max(maxDistance, 0.1f), layerMask, out var hit))
                return false;

            if (!TryGetSurfaceNormal(hit, out surfaceNormal, out _))
                return false;

            hitPoint = hit.Point;
            return float.IsFinite(hitPoint.X) && float.IsFinite(hitPoint.Y) && float.IsFinite(hitPoint.Z);
        }
        catch
        {
            hitPoint = default;
            surfaceNormal = default;
            return false;
        }
    }

    private static bool TryCaptureCameraMatrices(out CameraMatrixSnapshot snapshot, out string status)
    {
        snapshot = default;
        status = "";
        try
        {
            ControlStruct* control = ControlStruct.Instance();
            if (control == null)
            {
                status = "Control.Instance() is unavailable.";
                return false;
            }

            GameCamera* gameCamera = control->CameraManager.GetActiveCamera();
            if (gameCamera == null)
            {
                status = "CameraManager.GetActiveCamera() returned null.";
                return false;
            }

            // The active Game.Camera embeds Scene.Camera at +0x10. The validated
            // projection is Scene.Camera.ViewMatrix × Scene.Camera.RenderCamera.ProjectionMatrix.
            SceneCamera* sceneCamera = (SceneCamera*)((byte*)gameCamera + EmbeddedSceneCameraOffset);
            if (sceneCamera == null)
            {
                status = "The active camera's embedded Scene.Camera is unavailable.";
                return false;
            }

            RenderCamera* renderCamera = sceneCamera->RenderCamera;
            if (renderCamera == null)
            {
                status = "Scene.Camera.RenderCamera is null.";
                return false;
            }

            snapshot.SceneViewMatrix = sceneCamera->ViewMatrix;
            snapshot.RenderProjectionMatrix = renderCamera->ProjectionMatrix;
            snapshot.SceneViewProjection = snapshot.SceneViewMatrix * snapshot.RenderProjectionMatrix;
            snapshot.GameCameraAddress = (nint)gameCamera;
            snapshot.SceneCameraAddress = (nint)sceneCamera;
            snapshot.RenderCameraAddress = (nint)renderCamera;
            status = "Using validated Scene.ViewMatrix × Render.ProjectionMatrix.";
            return true;
        }
        catch (Exception ex)
        {
            status = "Scene-camera projection sampling failed: " + ex.Message;
            return false;
        }
    }

    private void ComputeSurfaceAnalysis()
    {
        _hasCenterNormal = _rayFound && TryGetSurfaceNormal(_rayHit, out _centerNormal, out _centerNormalSource);
        if (!_hasCenterNormal)
        {
            _centerNormal = default;
            _centerNormalSource = "unavailable";
        }

        _hasPlaneFit = TryFitNeighborhoodPlane(out _planeNormal, out _planeAnchor, out _planeRmsError, out _planeHeightRange, out _planeSampleCount);
        _hasGroundFrame = false;

        Vector3 up;
        if (_groundFrameNormalSource == 1 && _hasPlaneFit)
        {
            up = _planeNormal;
            _groundFrameNormalSourceDescription = "3x3 fitted plane";
        }
        else if (_hasCenterNormal)
        {
            up = _centerNormal;
            _groundFrameNormalSourceDescription = _groundFrameNormalSource == 1
                ? "center contact normal (plane-fit fallback)"
                : "center contact normal";
        }
        else if (_hasPlaneFit)
        {
            up = _planeNormal;
            _groundFrameNormalSourceDescription = "3x3 fitted plane (center-normal fallback)";
        }
        else
        {
            _groundFrameNormalSourceDescription = "unavailable";
            return;
        }

        Vector3 heading = new(MathF.Sin(_playerRotation), 0f, MathF.Cos(_playerRotation));
        Vector3 forward = heading - up * Vector3.Dot(heading, up);
        if (forward.LengthSquared() < 1e-6f)
        {
            Vector3 fallback = MathF.Abs(up.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitZ;
            forward = fallback - up * Vector3.Dot(fallback, up);
        }
        if (forward.LengthSquared() < 1e-6f)
            return;

        forward = Vector3.Normalize(forward);
        Vector3 right = Vector3.Cross(up, forward);
        if (right.LengthSquared() < 1e-6f)
            return;
        right = Vector3.Normalize(right);
        forward = Vector3.Normalize(Vector3.Cross(right, up));

        _groundFrameOrigin = _rayFound ? _rayHit.Point : _planeAnchor;
        _groundUp = up;
        _groundForward = forward;
        _groundRight = right;
        _hasGroundFrame = true;
    }

    private bool TryFitNeighborhoodPlane(out Vector3 normal, out Vector3 anchor, out float rmsError, out float heightRange, out int sampleCount)
    {
        normal = default;
        anchor = default;
        rmsError = 0f;
        heightRange = 0f;
        sampleCount = 0;

        double sumX = 0, sumZ = 0, sumY = 0;
        double sumXX = 0, sumXZ = 0, sumZZ = 0;
        double sumXY = 0, sumZY = 0;
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;

        foreach (var sample in _grid)
        {
            if (!sample.Found)
                continue;

            double x = sample.Offset.X;
            double z = sample.Offset.Y;
            double y = sample.Hit.Point.Y - _targetPosition.Y;
            sumX += x;
            sumZ += z;
            sumY += y;
            sumXX += x * x;
            sumXZ += x * z;
            sumZZ += z * z;
            sumXY += x * y;
            sumZY += z * y;
            minY = Math.Min(minY, sample.Hit.Point.Y);
            maxY = Math.Max(maxY, sample.Hit.Point.Y);
            sampleCount++;
        }

        heightRange = sampleCount > 0 ? (float)(maxY - minY) : 0f;
        if (sampleCount < 3)
            return false;

        // Solve the least-squares plane y = a*x + b*z + c using a 3x3 augmented matrix.
        Span<double> matrix = stackalloc double[12]
        {
            sumXX, sumXZ, sumX, sumXY,
            sumXZ, sumZZ, sumZ, sumZY,
            sumX,  sumZ,  sampleCount, sumY,
        };

        for (int column = 0; column < 3; column++)
        {
            int pivotRow = column;
            double pivotMagnitude = Math.Abs(matrix[column * 4 + column]);
            for (int row = column + 1; row < 3; row++)
            {
                double candidate = Math.Abs(matrix[row * 4 + column]);
                if (candidate > pivotMagnitude)
                {
                    pivotMagnitude = candidate;
                    pivotRow = row;
                }
            }
            if (pivotMagnitude < 1e-10)
                return false;

            if (pivotRow != column)
            {
                for (int c = column; c < 4; c++)
                {
                    double temp = matrix[column * 4 + c];
                    matrix[column * 4 + c] = matrix[pivotRow * 4 + c];
                    matrix[pivotRow * 4 + c] = temp;
                }
            }

            double pivot = matrix[column * 4 + column];
            for (int c = column; c < 4; c++)
                matrix[column * 4 + c] /= pivot;

            for (int row = 0; row < 3; row++)
            {
                if (row == column)
                    continue;
                double factor = matrix[row * 4 + column];
                for (int c = column; c < 4; c++)
                    matrix[row * 4 + c] -= factor * matrix[column * 4 + c];
            }
        }

        double a = matrix[3];
        double b = matrix[7];
        double c0 = matrix[11];
        Vector3 fittedNormal = new((float)-a, 1f, (float)-b);
        if (!IsUsableNormal(fittedNormal))
            return false;

        normal = Vector3.Normalize(fittedNormal);
        if (normal.Y < 0f)
            normal = -normal;
        anchor = new Vector3(_targetPosition.X, _targetPosition.Y + (float)c0, _targetPosition.Z);

        double squaredError = 0;
        foreach (var sample in _grid)
        {
            if (!sample.Found)
                continue;
            double predicted = a * sample.Offset.X + b * sample.Offset.Y + c0;
            double actual = sample.Hit.Point.Y - _targetPosition.Y;
            double error = actual - predicted;
            squaredError += error * error;
        }
        rmsError = (float)Math.Sqrt(squaredError / sampleCount);
        return float.IsFinite(rmsError) && float.IsFinite(normal.X) && float.IsFinite(normal.Y) && float.IsFinite(normal.Z);
    }

    private static float SlopeDegrees(Vector3 normal) =>
        MathF.Acos(Math.Clamp(Vector3.Normalize(normal).Y, -1f, 1f)) * (180f / MathF.PI);

    private static float AngleBetweenDegrees(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * (180f / MathF.PI);

    // The game matrix uses row-vector convention: clip = worldPosition * ViewProjectionMatrix.
    private static bool TryProjectWorld(Vector3 world, Matrix4x4 viewProjection, Vector2 viewportPos, Vector2 viewportSize, out Vector2 screen)
    {
        screen = default;
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        if (!float.IsFinite(clip.W) || clip.W <= 0.001f)
            return false;

        float inverseW = 1f / clip.W;
        float ndcX = clip.X * inverseW;
        float ndcY = clip.Y * inverseW;
        float ndcZ = clip.Z * inverseW;
        if (!float.IsFinite(ndcX) || !float.IsFinite(ndcY) || !float.IsFinite(ndcZ))
            return false;
        if (ndcZ < 0f || ndcZ > 1f || ndcX < -1.2f || ndcX > 1.2f || ndcY < -1.2f || ndcY > 1.2f)
            return false;

        screen = viewportPos + new Vector2(
            (ndcX * 0.5f + 0.5f) * viewportSize.X,
            (0.5f - ndcY * 0.5f) * viewportSize.Y);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    private static bool Raycast(BGCollisionModule* module, Vector3 origin, Vector3 direction, float maxDistance, int layerMask, out NativeRaycastHit hit)
    {
        NativeRaycastHit nativeHit = default;
        int* flags = stackalloc int[] { 0x4000, 0, 0x4000, 0 };
        bool found = module->RaycastMaterialFilter(&nativeHit, &origin, &direction, maxDistance, layerMask, flags);
        hit = nativeHit;
        return found;
    }

    private static bool SphereSweep(BGCollisionModule* module, Vector3 origin, Vector3 direction, float maxDistance, int layerMask, float radius, out NativeRaycastHit hit)
    {
        NativeRaycastHit nativeHit = default;
        // ClientStructs documents RaycastParams.Origin as Vector4 for a sphere sweep,
        // with W containing the sphere radius. The interop wrapper currently types the
        // native argument as Vector3*, so pass the same four-float layout through that pointer.
        Vector4 sphereOrigin = new(origin, radius);
        Vector3* originPointer = (Vector3*)&sphereOrigin;
        int* flags = stackalloc int[] { 0x4000, 0, 0x4000, 0 };
        bool found = module->SweepSphereMaterialFilter(&nativeHit, originPointer, &direction, maxDistance, layerMask, flags);
        hit = nativeHit;
        return found;
    }

    private static void DrawHitSummary(bool found, in NativeRaycastHit hit, string label)
    {
        if (!found)
        {
            ImGui.TextColored(new Vector4(1f, .65f, .35f, 1f), $"{label}: no collision found within the cast distance.");
            return;
        }

        ImGui.TextColored(new Vector4(.45f, .85f, .45f, 1f), $"{label}: HIT at {Format(hit.Point)}");
        ImGui.Text($"Distance: {hit.Distance:0.000} yalms   Material: 0x{hit.Material:X16}");
        ImGui.Text($"Native normal: {Format(hit.Normal)}");
        if (TryGetSurfaceNormal(hit, out var normal, out string source))
        {
            float slope = MathF.Acos(Math.Clamp(normal.Y, -1f, 1f)) * (180f / MathF.PI);
            ImGui.Text($"Usable up-facing normal ({source}): {Format(normal)}   Slope from up: {slope:0.0}°");
        }
        else
        {
            ImGui.TextDisabled("No usable normal was returned (native normal and triangle fallback were degenerate).");
        }
        ImGui.TextDisabled($"Collider pointer: 0x{(nint)hit.Object:X}");

        if (ImGui.TreeNode($"{label} triangle data"))
        {
            ImGui.Text($"V1: {Format(hit.V1)}");
            ImGui.Text($"V2: {Format(hit.V2)}");
            ImGui.Text($"V3: {Format(hit.V3)}");
            ImGui.TreePop();
        }
    }

    private static bool TryGetSurfaceNormal(in NativeRaycastHit hit, out Vector3 normal, out string source)
    {
        normal = hit.Normal;
        source = "native";
        if (IsUsableNormal(normal))
        {
            normal = Vector3.Normalize(normal);
            if (normal.Y < 0f) normal = -normal;
            source = "native, up-oriented";
            return true;
        }

        normal = Vector3.Cross(hit.V2 - hit.V1, hit.V3 - hit.V1);
        source = "triangle fallback";
        if (!IsUsableNormal(normal))
        {
            normal = default;
            return false;
        }

        normal = Vector3.Normalize(normal);
        if (normal.Y < 0f) normal = -normal;
        source = "triangle fallback, up-oriented";
        return true;
    }

    private static bool IsUsableNormal(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z)
        && value.LengthSquared() > 1e-6f;

    private string BuildReport()
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("FPSBooster Collision Lab");
        sb.AppendLine($"Timestamp: {_lastQueryUtc:O}");
        sb.AppendLine($"Status: {_status}");
        sb.AppendLine($"Player: {Format(_playerPosition)}");
        sb.AppendLine($"Target: {Format(_targetPosition)}");
        sb.AppendLine($"Cast origin: {Format(_castOrigin)}");
        sb.AppendLine($"Start height: {_startHeight.ToString("0.###", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Max distance: {_maxDistance.ToString("0.###", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Layer mask: 0x{_layerMask:X8}");
        sb.AppendLine("Camera projection:");
        sb.AppendLine("  Source: active Scene.Camera.ViewMatrix × Scene.Camera.RenderCamera.ProjectionMatrix");
        sb.AppendLine($"  Status: {_cameraDiagnosticStatus}");
        if (_hasFrameCameraMatrices)
            sb.AppendLine($"  Game camera: 0x{_frameCameraMatrices.GameCameraAddress:X} · Scene camera: 0x{_frameCameraMatrices.SceneCameraAddress:X} · Render camera: 0x{_frameCameraMatrices.RenderCameraAddress:X}");
        sb.AppendLine("Surface analysis:");
        sb.AppendLine($"  Center contact normal: {(_hasCenterNormal ? Format(_centerNormal) : "unavailable")} ({_centerNormalSource})");
        sb.AppendLine($"  Neighborhood plane fit: {(_hasPlaneFit ? "available" : "unavailable")} · samples {_planeSampleCount}/9 · normal {(_hasPlaneFit ? Format(_planeNormal) : "unavailable")}");
        if (_hasPlaneFit)
            sb.AppendLine($"  Plane slope: {SlopeDegrees(_planeNormal):0.000}° · RMS deviation: {_planeRmsError:0.000000} · height range: {_planeHeightRange:0.000000}");
        if (_hasCenterNormal && _hasPlaneFit)
            sb.AppendLine($"  Center-to-plane normal angle: {AngleBetweenDegrees(_centerNormal, _planeNormal):0.000}°");
        sb.AppendLine($"  Ground frame: {(_hasGroundFrame ? "available" : "unavailable")} · normal source: {_groundFrameNormalSourceDescription}");
        if (_hasGroundFrame)
        {
            sb.AppendLine($"  Origin: {Format(_groundFrameOrigin)}");
            sb.AppendLine($"  Up: {Format(_groundUp)}");
            sb.AppendLine($"  Forward: {Format(_groundForward)}");
            sb.AppendLine($"  Right: {Format(_groundRight)}");
        }
        AppendHit(sb, "Raycast", _rayFound, _rayHit);
        AppendHit(sb, "Sphere sweep", _sweepFound, _sweepHit);
        sb.AppendLine("3x3 patch (offsetX, offsetZ, hitY, normalY):");
        foreach (var sample in _grid)
        {
            if (!sample.Found)
            {
                sb.AppendLine($"  {sample.Offset.X:0.###}, {sample.Offset.Y:0.###}: MISS");
                continue;
            }
            bool hasNormal = TryGetSurfaceNormal(sample.Hit, out var normal, out _);
            sb.AppendLine($"  {sample.Offset.X:0.###}, {sample.Offset.Y:0.###}: Y={sample.Hit.Point.Y:0.###}, normal={(hasNormal ? Format(normal) : "n/a")}, slope={(hasNormal ? SlopeDegrees(normal).ToString("0.0", CultureInfo.InvariantCulture) + "°" : "n/a")}");
        }
        return sb.ToString();
    }

    private void AppendHit(StringBuilder sb, string label, bool found, in NativeRaycastHit hit)
    {
        if (!found)
        {
            sb.AppendLine($"{label}: MISS");
            return;
        }
        sb.AppendLine($"{label}: HIT");
        sb.AppendLine($"  Point: {Format(hit.Point)}");
        sb.AppendLine($"  Distance: {hit.Distance:0.###}");
        sb.AppendLine($"  Material: 0x{hit.Material:X16}");
        sb.AppendLine($"  Native normal: {Format(hit.Normal)}");
        sb.AppendLine($"  Collider: 0x{(nint)hit.Object:X}");
        if (label == "Sphere sweep")
            sb.AppendLine($"  Estimated swept-sphere center at impact: {Format(_sweepCenterAtHit)} (origin + direction × hit distance)");
        if (TryGetSurfaceNormal(hit, out var normal, out string source))
            sb.AppendLine($"  Surface normal ({source}): {Format(normal)}");
        sb.AppendLine($"  Triangle: {Format(hit.V1)} | {Format(hit.V2)} | {Format(hit.V3)}");
    }

    private static string Format(Vector2 value) => $"({value.X:+0.000;-0.000;0.000}, {value.Y:+0.000;-0.000;0.000})";
    private static string Format(Vector3 value) => $"({value.X:0.000}, {value.Y:0.000}, {value.Z:0.000})";

    public void Dispose() => StopLiveSampling();
}
