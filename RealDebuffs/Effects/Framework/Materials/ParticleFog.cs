using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A creeping cold mist wisp. The key insight: a symmetric radial cluster of circles always
/// reads as a blob no matter how many lobes it has - the eye pattern-matches "circular." Real
/// fog is stretched along the direction it drifts, so each wisp here gets its own elongation
/// axis and length, and the lobes are distributed ALONG that axis rather than around a center.
/// The result is a spindle-shaped silhouette: narrow at the tips, ragged through the middle.
///
/// ANIMATION: each lobe has its own drift - its own X speed, Y speed, phase, and amplitude,
/// all seeded from the particle's own seed. Lobes wander in small ellipses (different
/// frequencies on each axis, so the paths aren't closed circles), and each lobe's radius
/// breathes slightly out of phase with its neighbors. The wisp as a whole still translates
/// and sways via p.Position and p.Sway; the per-lobe motion is layered ON TOP of that, so the
/// cloud moves as one thing while its interior visibly churns.
///
/// The per-lobe alpha is very low (0.038), because with nine body lobes plus three outliers,
/// heavy overlap is what turns individual soft circles into a continuous hazy mass.
/// </summary>
public sealed class ParticleFog : IParticleMaterial
{
    public string Name => "particle.fog";
    public string[] NaturalLanguageWords { get; } = { "fog", "mist" };

    private static readonly uint Tint = DrawHelpers.ToU32(0.72f, 0.82f, 0.94f, 1f);

    private const int BodyLobes    = 9;
    private const int OutlierLobes = 3;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 1f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.003f) return;

        // Very slow envelope — fog drifts in, hangs, drifts out.
        float sizeT;
        if      (p.AgeRatio < 0.30f) sizeT = 0.55f + 0.45f * (p.AgeRatio / 0.30f);
        else if (p.AgeRatio < 0.70f) sizeT = 1f;
        else                          sizeT = MathF.Max(0.15f, 1f - (p.AgeRatio - 0.70f) / 0.30f);

        float size = p.Size * sizeT;
        if (size <= 1f) return;

        var pos = p.Position + new Vector2(p.Sway, 0f);

        // Elongation: every wisp has its own stretch axis and length. Fixed per-particle (seed
        // derived), so the wisp's overall silhouette stays coherent as it drifts. The INTERNAL
        // lobes drift independently of this axis (see below); only the base layout uses it.
        float axisAngle = DrawHelpers.HashRange(p.Seed,     0f, MathF.Tau);
        float stretch   = DrawHelpers.HashRange(p.Seed + 1, 1.8f, 3.0f);
        Vector2 axisDir = new(MathF.Cos(axisAngle), MathF.Sin(axisAngle));
        Vector2 perpDir = new(-axisDir.Y, axisDir.X);

        float halfLen  = size * stretch;
        float bodyHalf = size * 0.55f; // half-width of the central body, pre-taper

        // Body lobes: distributed along the stretch axis, laterally jittered with a taper that
        // is widest at the middle and pinches at the ends. That's what produces the spindle
        // silhouette instead of a uniform oval.
        for (int i = 0; i < BodyLobes; i++)
        {
            int s = p.Seed + i * 197 + 53;

            float t = (i + 0.5f) / BodyLobes - 0.5f;   // -0.5 .. +0.5
            float along = t * halfLen * 2f;
            float taper = 1f - MathF.Abs(t) * 1.6f;
            if (taper < 0.15f) taper = 0.15f;

            float lateralSpread = bodyHalf * taper;
            float lateralJitter = DrawHelpers.HashRange(s, -0.35f, 0.35f) * bodyHalf;
            float lateral = DrawHelpers.HashRange(s + 1, -lateralSpread, lateralSpread) + lateralJitter;

            float r = size * DrawHelpers.HashRange(s + 2, 0.40f, 0.70f) * taper;

            Vector2 baseAt = pos + axisDir * along + perpDir * lateral;

            // ---- per-lobe independent motion ----
            // World-space drift (not axis-space): lobes move freely in all directions rather
            // than being constrained to slide along the wisp's own long axis, which is what
            // real fog does. Different frequencies on X and Y mean the lobe traces a small
            // open curve rather than a closed loop, so it never visibly "resets."
            float driftAmp    = size * DrawHelpers.HashRange(s + 3, 0.10f, 0.22f);
            float driftSpeedX = DrawHelpers.HashRange(s + 4, 0.35f, 0.85f);
            float driftSpeedY = DrawHelpers.HashRange(s + 5, 0.28f, 0.72f);
            float driftPhaseX = DrawHelpers.HashRange(s + 6, 0f, MathF.Tau);
            float driftPhaseY = DrawHelpers.HashRange(s + 7, 0f, MathF.Tau);

            float dx = MathF.Sin(ctx.Time * driftSpeedX + driftPhaseX) * driftAmp;
            float dy = MathF.Cos(ctx.Time * driftSpeedY + driftPhaseY) * driftAmp * 0.85f;
            Vector2 drift = new(dx, dy);

            // Slight radius breathing, at the lobe's own X frequency but offset in phase so
            // radius doesn't peak exactly when the lobe is at its extreme drift position.
            float radiusPulse = 1f + 0.18f * MathF.Sin(ctx.Time * driftSpeedX * 0.9f + driftPhaseX + 1.3f);

            // Nine lobes at 0.038 alpha overlap into a continuous haze. The individual circles
            // stop being distinguishable and only the aggregate silhouette remains.
            dl.AddCircleFilled(baseAt + drift, r * radiusPulse,
                DrawHelpers.WithAlpha(Tint, alpha * 0.038f));
        }

        // Outlier lobes: placed further out, very faint. These break up the outline so the
        // wisp's edge is ragged and organic rather than a smooth oval, and they hang off the
        // main body in a way that reads as trailing mist rather than as a separate particle.
        // Each gets its own drift too - slightly larger amplitude than the body lobes, so the
        // frayed edges of the wisp churn a bit more than its core.
        for (int i = 0; i < OutlierLobes; i++)
        {
            int s = p.Seed + 900 + i * 131;
            float t = DrawHelpers.HashRange(s, -0.85f, 0.85f);
            float along = t * halfLen * 2f;
            float lateral = DrawHelpers.HashRange(s + 1, -bodyHalf * 0.9f, bodyHalf * 0.9f);
            float r = size * DrawHelpers.HashRange(s + 2, 0.25f, 0.45f);

            Vector2 baseAt = pos + axisDir * along + perpDir * lateral;

            float driftAmp    = size * DrawHelpers.HashRange(s + 3, 0.15f, 0.30f);
            float driftSpeedX = DrawHelpers.HashRange(s + 4, 0.28f, 0.65f);
            float driftSpeedY = DrawHelpers.HashRange(s + 5, 0.24f, 0.58f);
            float driftPhaseX = DrawHelpers.HashRange(s + 6, 0f, MathF.Tau);
            float driftPhaseY = DrawHelpers.HashRange(s + 7, 0f, MathF.Tau);

            float dx = MathF.Sin(ctx.Time * driftSpeedX + driftPhaseX) * driftAmp;
            float dy = MathF.Cos(ctx.Time * driftSpeedY + driftPhaseY) * driftAmp * 0.85f;
            Vector2 drift = new(dx, dy);

            dl.AddCircleFilled(baseAt + drift, r,
                DrawHelpers.WithAlpha(Tint, alpha * 0.028f));
        }
    }

    public ReadOnlySpan<StrokeEmission> Emissions => EmissionSpecs;

    private static readonly StrokeEmission[] EmissionSpecs =
    {
        new(
        Role: PrimitiveRole.Fog,
        DensityPer100px: 0.5f,
        SpeedMin: 6f, SpeedMax: 14f,
        LifespanMin: 2.0f, LifespanMax: 4.0f,
        SizeMin: 12f, SizeMax: 24f,
        SpreadRadians: 1.4f,
        BiasVelocity: new Vector2(0f, -6f),
        PrimaryDirection: new Vector2(0f, -1f)),
    };
}