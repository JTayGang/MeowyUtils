using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A single soft fog blob. Fog as a whole is a FIELD of these: many independent particles, each
/// with its own spawn, position, velocity, sway, and lifespan, drifting and dying on its own
/// clock. The only thing that makes fog look like fog rather than snow is that these particles
/// are large, slow, long-lived, and drawn very softly.
///
/// SHAPE: each particle draws as several small same-alpha sub-circles at fixed per-seed offsets.
/// They do NOT move relative to each other - they're the particle's silhouette, not animation.
/// The reason for many small rather than one large circle (or two concentric circles) is that
/// concentric layers of different radii always produce a visible "rim with a core," no matter
/// how soft each edge is, because the eye pattern-matches the fixed radii. Several sub-circles
/// at the SAME alpha overlap into an irregular aggregate with a smooth falloff from the middle
/// - no distinct radius anywhere for a ring to form at.
///
/// The individual sub-lobe offsets and radii are seed-derived, so no two particles have the same
/// outline. This is the same reason snow and sparks read correctly: each particle is small,
/// soft, and slightly different from its neighbors, and the aggregate is what makes the field.
/// </summary>
public sealed class ParticleFog : IParticleMaterial
{
    public string Name => "particle.fog";
    public string[] NaturalLanguageWords { get; } = { "fog", "mist" };

    private static readonly uint Tint = DrawHelpers.ToU32(0.72f, 0.82f, 0.94f, 1f);

    private const int SubLobes = 5;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 1f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.003f) return;

        // Slow envelope - fog drifts in, hangs, drifts out. Broader hold than other particles
        // so a blob's appearance and disappearance are gradual.
        float sizeT;
        if      (p.AgeRatio < 0.30f) sizeT = 0.55f + 0.45f * (p.AgeRatio / 0.30f);
        else if (p.AgeRatio < 0.70f) sizeT = 1f;
        else                          sizeT = MathF.Max(0.15f, 1f - (p.AgeRatio - 0.70f) / 0.30f);

        float size = p.Size * sizeT;
        if (size <= 1f) return;

        var pos = p.Position + new Vector2(p.Sway, 0f);

        // Same alpha on every sub-lobe: no concentric structure, no rim. Overlap does the
        // softening. Offsets sit well inside the parent radius so the aggregate silhouette is
        // roughly circular but visibly irregular, not a cluster of distinct circles.
        for (int i = 0; i < SubLobes; i++)
        {
            int s = p.Seed + i * 197;

            float ang  = DrawHelpers.HashRange(s,     0f, MathF.Tau);
            float dist = DrawHelpers.HashRange(s + 1, 0.10f, 0.40f) * size;
            float r    = DrawHelpers.HashRange(s + 2, 0.55f, 0.80f) * size;

            Vector2 offset = new(MathF.Cos(ang) * dist, MathF.Sin(ang) * dist);
            dl.AddCircleFilled(pos + offset, r, DrawHelpers.WithAlpha(Tint, alpha * 0.045f));
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