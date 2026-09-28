using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Parasite tentacle: an organic, segmented tendril with ridges, suckers, a wet sheen, and a
/// hooked tip. Layered from back to front as:
///
///   1. Dark halo — wide soft silhouette so the tentacle separates from the background.
///   2. Body — sickly green tube, tapered non-linearly (base stays full-bodied longer than a
///      linear taper would).
///   3. Ridges — short perpendicular bands every ~12px of arc length, fading out near both ends.
///      This is what makes the surface read as segmented creature flesh instead of a smooth tube.
///   4. Lit side — a highlight on whichever side faces the (fixed) light direction, so the tube
///      reads as round in three dimensions.
///   5. Suckers — small pale circles along the shaded underside, spaced at intervals. Reads as
///      a gripping surface.
///   6. Traveling sheen — a slow bright streak walking base-to-tip, offset per tendril via
///      StrokePrimitive.Phase so neighbouring tentacles don't sync up.
///   7. Bright core — a thin nerve-line running the length, glinting near-white.
///   8. Hooked tip — a crescent fang + glow bulb, shown only while TipFlare is above zero (the
///      strand is still extending). This is what replaces the old bulb tip and sells "parasite".
///
/// Uses arc-length sampling throughout (StrandPath.SampleAtArc), so decoration placement stays
/// even regardless of how the incoming path was built. Works as a hero material for any
/// stroke-based effect, not just Disease.
/// </summary>
public sealed class StrokeParasite : IStrokeMaterial
{
    public string Name => "stroke.parasite";

    // ---- palette: sickly green parasite flesh ----
    private static readonly uint Halo    = DrawHelpers.ToU32(0.015f, 0.030f, 0.010f, 1f);
    private static readonly uint Void    = DrawHelpers.ToU32(0.030f, 0.055f, 0.020f, 1f);
    private static readonly uint Body    = DrawHelpers.ToU32(0.72f,  0.88f,  0.26f,  1f);
    private static readonly uint Ridge   = DrawHelpers.ToU32(0.050f, 0.095f, 0.030f, 1f);
    private static readonly uint Lit     = DrawHelpers.ToU32(0.240f, 0.360f, 0.120f, 1f);
    private static readonly uint Rim     = DrawHelpers.ToU32(0.560f, 0.740f, 0.260f, 1f);
    private static readonly uint Core    = DrawHelpers.ToU32(0.860f, 0.940f, 0.520f, 1f);
    private static readonly uint CoreHot = DrawHelpers.ToU32(0.980f, 1.000f, 0.820f, 1f);
    private static readonly uint Sucker  = DrawHelpers.ToU32(0.760f, 0.850f, 0.400f, 1f);
    private static readonly uint Ooze    = DrawHelpers.ToU32(0.850f, 0.900f, 0.320f, 1f);

    private static readonly Vector2 LightDir = Vector2.Normalize(new Vector2(-0.65f, -0.75f));

    private const int   MaxSteps      = 96;
    private const float RidgeSpacing  = 12f;  // pixels at 1080p scale
    private const float SuckerSpacing = 45f;
    private const float SheenSpeed    = 0.32f;

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        if (path.Count < 2) return;

        float alpha = s.Brightness * ctx.Alpha;
        if (alpha <= 0.003f) return;

        float reveal = Math.Clamp(s.Reveal, 0f, 1f);
        if (reveal <= 0.001f) return;

        float baseWidth = MathF.Max(2f, s.WidthHint);
        float px = ctx.ScreenScale;
        float visibleLen = path.Length * reveal;

        // Dense arc-length sampling: one sample every ~baseWidth * 0.4px, clamped so short arcs
        // still get a few samples and long ones don't blow past MaxSteps.
        float step = MathF.Max(3f, baseWidth * 0.4f);
        int stepCount = Math.Clamp((int)(visibleLen / step), 4, MaxSteps);
        if (stepCount < 2) return;

        Span<Vector2> positions = stackalloc Vector2[MaxSteps];
        Span<Vector2> tangents  = stackalloc Vector2[MaxSteps];

        for (int i = 0; i < stepCount; i++)
        {
            float t = stepCount == 1 ? 0f : (float)i / (stepCount - 1);
            float arc = visibleLen * t;
            path.SampleAtArc(arc, out Vector2 pos, out Vector2 tan);
            positions[i] = pos;
            tangents[i] = tan;
        }

        // 1. Halo.
        for (int i = 0; i < stepCount - 1; i++)
        {
            float u = (i + 0.5f) / (stepCount - 1);
            float w = TaperWidth(u, baseWidth);
            dl.AddLine(positions[i], positions[i + 1],
                DrawHelpers.WithAlpha(Halo, alpha * 0.85f), w * 3.2f + 2f * px);
        }

        // 2. Body.
        for (int i = 0; i < stepCount - 1; i++)
        {
            float u = (i + 0.5f) / (stepCount - 1);
            float w = TaperWidth(u, baseWidth);
            dl.AddLine(positions[i], positions[i + 1],
                DrawHelpers.WithAlpha(Void, alpha), w);
            dl.AddLine(positions[i], positions[i + 1],
                DrawHelpers.WithAlpha(Body, alpha * 0.92f), w * 0.78f);
        }

        // 3. Ridges.
        DrawRidges(dl, positions, tangents, stepCount, baseWidth, alpha, visibleLen, px);

        // 4. Lit side.
        for (int i = 0; i < stepCount - 1; i++)
        {
            Vector2 d = positions[i + 1] - positions[i];
            float dlen = d.Length();
            if (dlen < 0.01f) continue;
            Vector2 perp = new(-d.Y / dlen, d.X / dlen);

            float u = (i + 0.5f) / (stepCount - 1);
            float w = TaperWidth(u, baseWidth);

            float lightDot = Vector2.Dot(perp, LightDir);
            if (lightDot <= 0f) continue;

            Vector2 offset = perp * (w * 0.30f * lightDot);
            dl.AddLine(positions[i] + offset, positions[i + 1] + offset,
                DrawHelpers.WithAlpha(Lit, alpha * 0.70f * lightDot), w * 0.30f);
        }

        // 5. Suckers.
        DrawSuckers(dl, positions, tangents, stepCount, baseWidth, alpha, visibleLen, px);

        // 6. Traveling sheen.
        DrawSheen(dl, positions, tangents, stepCount, baseWidth, alpha, s.Phase, ctx.Time);

        // 7. Core.
        for (int i = 0; i < stepCount - 1; i++)
        {
            float u = (i + 0.5f) / (stepCount - 1);
            float w = TaperWidth(u, baseWidth);
            float coreW = MathF.Max(0.7f, w * 0.20f);
            dl.AddLine(positions[i], positions[i + 1],
                DrawHelpers.WithAlpha(Rim, alpha * 0.75f), coreW * 2.4f);
            dl.AddLine(positions[i], positions[i + 1],
                DrawHelpers.WithAlpha(Core, alpha * 0.95f), coreW * 1.1f);
            dl.AddLine(positions[i], positions[i + 1],
                DrawHelpers.WithAlpha(CoreHot, alpha * 0.75f), MathF.Max(0.6f, coreW * 0.55f));
        }

        // 8. Hooked tip (only while extending).
        if (s.TipFlare > 0.001f)
        {
            float tipK = Math.Clamp(s.TipFlare, 0f, 1f);
            DrawTipHook(dl, positions[stepCount - 1], tangents[stepCount - 1], baseWidth, alpha, tipK);
        }
    }

    public ReadOnlySpan<StrokeEmission> Emissions => FallingDrips;
    public ReadOnlySpan<StrokeFlowEmission> Flows => FlowingDrips;

    /// <summary>
    /// Sparse drips that detach from the strand and fall. Gravity accelerates them; the drip
    /// material's stretch logic converts their increasing speed into a longer teardrop, which is
    /// what makes the fall read as "under gravity" rather than "moving at a fixed rate".
    /// </summary>
    private static readonly StrokeEmission[] FallingDrips =
    {
        new(Role: PrimitiveRole.Drip,
            DensityPer100px: 0.2f,
            SpeedMin: 8f, SpeedMax: 22f,
            LifespanMin: 1.6f, LifespanMax: 2.6f,
            SizeMin: 2.0f, SizeMax: 4.5f,
            SpreadRadians: 0.18f,
            BiasVelocity: new Vector2(0f, 0f),
            PrimaryDirection: new Vector2(0f, 1f),
            Gravity: new Vector2(0f, 420f)),
    };

    /// <summary>
    /// Drips that stay attached to the strand, running along it toward whichever endpoint is
    /// lower on screen. Speed is modulated so each drip slows near a sucker (ObstacleSpacingPx
    /// matches the sucker spacing used above) and speeds up between them, reading as the drip
    /// catching on each one. Wobble adds the side-to-side wander.
    /// </summary>
    private static readonly StrokeFlowEmission[] FlowingDrips =
    {
        new(Role: PrimitiveRole.Drip,
            DensityPer100px: 0.2f,
            SpeedMin: 55f, SpeedMax: 110f,
            LifespanMin: 5.0f, LifespanMax: 7.0f,
            SizeMin: 3.5f, SizeMax: 7.5f,
            WobbleAmplitude: 1.8f,
            WobbleFrequencyHz: 0.7f,
            ObstacleSpacingPx: 55f,
            LateralOffsetFrac: 0.45f),
    };

    // =====================================================================================
    // Layers
    // =====================================================================================

    private static void DrawRidges(ImDrawListPtr dl, Span<Vector2> positions, Span<Vector2> tangents,
                                   int stepCount, float baseWidth, float alpha, float visibleLen, float px)
    {
        float spacing = RidgeSpacing * px;
        if (spacing < 2f || visibleLen < spacing) return;

        int ridgeCount = (int)(visibleLen / spacing);
        for (int r = 0; r < ridgeCount; r++)
        {
            float arc = r * spacing + spacing * 0.5f;
            float u = arc / visibleLen;
            if (u >= 0.94f) continue;

            int idx = Math.Clamp((int)(u * (stepCount - 1)), 0, stepCount - 2);
            float f = u * (stepCount - 1) - idx;
            Vector2 p = Vector2.Lerp(positions[idx], positions[idx + 1], f);
            Vector2 tanRaw = Vector2.Lerp(tangents[idx], tangents[idx + 1], f);
            if (tanRaw.LengthSquared() < 1e-5f) continue;
            Vector2 tan = Vector2.Normalize(tanRaw);
            Vector2 perp = new(-tan.Y, tan.X);

            float w = TaperWidth(u, baseWidth);

            // Fade ridges out near the anchor and the tip so they don't fight the endpoints.
            float edgeFade = MathF.Min(Smoothstep(0.05f, 0.15f, u), Smoothstep(0.94f, 0.82f, u));
            float ridgeAlpha = alpha * 0.55f * edgeFade;

            dl.AddLine(p - perp * (w * 0.50f), p + perp * (w * 0.50f),
                DrawHelpers.WithAlpha(Ridge, ridgeAlpha), MathF.Max(1f, px * 1.5f));
        }
    }

    private static void DrawSuckers(ImDrawListPtr dl, Span<Vector2> positions, Span<Vector2> tangents,
                                    int stepCount, float baseWidth, float alpha, float visibleLen, float px)
    {
        float spacing = SuckerSpacing * px;
        if (spacing < 4f || visibleLen < spacing * 1.5f) return;

        int count = (int)(visibleLen / spacing);
        for (int i = 0; i < count; i++)
        {
            // Two arc positions per spacing so the two sides don't line up as a single band.
            float arcA = i * spacing + spacing * 0.25f;
            float arcB = i * spacing + spacing * 0.75f;

            DrawOneSucker(dl, positions, tangents, stepCount, visibleLen, arcA, baseWidth, alpha);
            DrawOneSucker(dl, positions, tangents, stepCount, visibleLen, arcB, baseWidth, alpha);
        }
    }

    private static void DrawOneSucker(ImDrawListPtr dl, Span<Vector2> positions, Span<Vector2> tangents,
                                      int stepCount, float visibleLen, float arc, float baseWidth, float alpha)
    {
        if (arc < 0f || arc > visibleLen) return;

        float u = arc / visibleLen;

        // The low cutoff is intentionally tiny: the strand's base sits off-screen (see
        // EdgeAnchor in DiseaseEffect), so the first few percent of arc are not visible to the
        // player anyway. A larger low cutoff here would pop suckers out visibly as the strand
        // extended past them; at 0.01 the transition happens off-screen.
        if (u < 0.01f || u > 0.88f) return;

        int idx = Math.Clamp((int)(u * (stepCount - 1)), 0, stepCount - 2);
        float f = u * (stepCount - 1) - idx;
        Vector2 p = Vector2.Lerp(positions[idx], positions[idx + 1], f);
        Vector2 tanRaw = Vector2.Lerp(tangents[idx], tangents[idx + 1], f);
        if (tanRaw.LengthSquared() < 1e-5f) return;
        Vector2 tan = Vector2.Normalize(tanRaw);
        Vector2 perp = new(-tan.Y, tan.X);

        float w = TaperWidth(u, baseWidth);
        float suckerR = MathF.Max(1.0f, w * 0.20f);

        // Suckers sit near the visible rim of the body rather than buried near the center.
        // The tube's half-width is w / 2, so 0.52 * w places the sucker center just past the
        // rim: roughly half on the body, half protruding, like a real sucker on a tentacle.
        float suckerOffset = w * 0.75f;

        for (int s = -1; s <= 1; s += 2)
        {
            Vector2 at = p + perp * (suckerOffset * s);

            dl.AddCircleFilled(at, suckerR * 1.6f, DrawHelpers.WithAlpha(Void, alpha * 0.60f));
            dl.AddCircleFilled(at, suckerR,        DrawHelpers.WithAlpha(Sucker, alpha * 0.75f));
            dl.AddCircleFilled(at, suckerR * 0.45f, DrawHelpers.WithAlpha(Ooze, alpha * 0.65f));
        }
    }

    private static void DrawSheen(ImDrawListPtr dl, Span<Vector2> positions, Span<Vector2> tangents,
                                  int stepCount, float baseWidth, float alpha, float phase, float time)
    {
        float raw = time * SheenSpeed + phase;
        float sheenU = raw - MathF.Floor(raw);

        for (int i = 0; i < stepCount - 1; i++)
        {
            float mid = (i + 0.5f) / (stepCount - 1);

            float delta = mid - sheenU;
            delta -= MathF.Round(delta);
            float k = MathF.Exp(-(delta * delta) / 0.012f);
            if (k < 0.03f) continue;

            Vector2 d = positions[i + 1] - positions[i];
            float dlen = d.Length();
            if (dlen < 0.01f) continue;
            Vector2 perp = new(-d.Y / dlen, d.X / dlen);

            float lightDot = Vector2.Dot(perp, LightDir);
            float sideK = lightDot > 0f ? 0.55f + 0.45f * lightDot : 0.45f;
            Vector2 offset = lightDot > 0f ? perp * (baseWidth * 0.16f * lightDot) : Vector2.Zero;

            float w = TaperWidth(mid, baseWidth);
            dl.AddLine(positions[i] + offset, positions[i + 1] + offset,
                DrawHelpers.WithAlpha(CoreHot, alpha * 0.55f * k * sideK), w * 0.40f);
        }
    }

    private static void DrawTipHook(ImDrawListPtr dl, Vector2 tip, Vector2 tipTan,
                                    float baseWidth, float alpha, float k)
    {
        Vector2 tan = tipTan.LengthSquared() > 1e-5f ? Vector2.Normalize(tipTan) : new Vector2(0f, -1f);
        Vector2 perp = new(-tan.Y, tan.X);

        float hookLen = baseWidth * (1.4f + 0.8f * k);

        Vector2 mid = tip + perp * (hookLen * 0.55f);
        Vector2 end = mid + tan * (hookLen * 0.75f);

        dl.AddLine(tip, mid, DrawHelpers.WithAlpha(Rim,     alpha * 0.85f * k), MathF.Max(1.5f, baseWidth * 0.28f));
        dl.AddLine(tip, mid, DrawHelpers.WithAlpha(CoreHot, alpha * 0.75f * k), MathF.Max(0.7f, baseWidth * 0.11f));
        dl.AddLine(mid, end, DrawHelpers.WithAlpha(Rim,     alpha * 0.75f * k), MathF.Max(1.2f, baseWidth * 0.22f));
        dl.AddLine(mid, end, DrawHelpers.WithAlpha(CoreHot, alpha * 0.65f * k), MathF.Max(0.6f, baseWidth * 0.09f));

        float bulbR = baseWidth * (0.55f + 0.35f * k);
        dl.AddCircleFilled(tip, bulbR * 2.0f,   DrawHelpers.WithAlpha(Halo,    alpha * 0.45f * k));
        dl.AddCircleFilled(tip, bulbR,          DrawHelpers.WithAlpha(Core,    alpha * 0.85f * k));
        dl.AddCircleFilled(tip, bulbR * 0.45f,  DrawHelpers.WithAlpha(CoreHot, alpha * 0.95f * k));
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    /// <summary>
    /// Non-linear taper. Power curve (rather than linear) keeps the base full-bodied longer and
    /// thins the tip sharply, giving a proper creature silhouette instead of a cone.
    /// </summary>
    private static float TaperWidth(float u, float baseWidth)
    {
        float taper = MathF.Pow(1f - u, 0.55f);
        return baseWidth * (0.20f + 0.80f * taper);
    }

    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}