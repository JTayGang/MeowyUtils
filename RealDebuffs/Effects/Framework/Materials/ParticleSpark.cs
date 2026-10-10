using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Impact spark: a small hot fleck with a tight white core and a warm gold halo. Sharp grow-in,
/// fast fade-out, so it reads as a brief flash of impact energy rather than a drifting ember.
/// The per-particle seed nudges the core toward white or gold so a burst doesn't look uniform.
/// </summary>
public sealed class ParticleSpark : IParticleMaterial
{
    public string Name => "particle.spark";
    public string[] NaturalLanguageWords { get; } = { "spark", "sparks" };

    private static readonly uint White = DrawHelpers.ToU32(1.00f, 1.00f, 0.96f, 1f);
    private static readonly uint Gold  = DrawHelpers.ToU32(1.00f, 0.78f, 0.32f, 1f);

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 0.3f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f) return;

        float sizeT = p.AgeRatio < 0.12f
            ? 0.25f + 0.75f * (p.AgeRatio / 0.12f)
            : MathF.Max(0.05f, 1f - (p.AgeRatio - 0.12f) / 0.88f);

        float size = p.Size * sizeT;
        if (size <= 0.3f) return;

        float heat = DrawHelpers.Hash01(p.Seed);
        uint core = DrawHelpers.LerpColor(Gold, White, heat);
        uint halo = DrawHelpers.LerpColor(Gold, White, heat * 0.5f);

        var pos = p.Position + new Vector2(p.Sway, 0f);

        // ---- trail ----
        // Draws where the spark just was: length proportional to current speed, direction
        // opposite velocity. Using a fixed time constant (0.06s here) means a spark's trail is
        // "where it was 60ms ago", so a fast spark leaves a long streak and a slow one leaves a
        // short one — which is what visually tells you it's slowing down under gravity.
        //
        // Cheap because it's stateless: no per-particle position history is tracked. Velocity is
        // already the direction and speed of motion, so it can reconstruct the recent past.
        float speed = p.Velocity.Length();
        if (speed > 40f)
        {
            float trailLen = MathF.Min(speed * 0.06f, 60f);
            Vector2 dir = p.Velocity / speed;
            Vector2 tail = pos - dir * trailLen;

            // Trailing alpha scales with the spark's own brightness fade, so both ends shrink
            // together as the spark dies.
            float trailAlpha = alpha * 0.55f;

            // Soft glow under the colored segments: same warm tone as the spark's own halo,
            // wider and fainter, so the trail blends into the surrounding glow rather than
            // sitting on a hard silhouette. This replaces an earlier black under-stroke that
            // read as a dark bar on fire-themed sparks.
            dl.AddLine(tail, pos, DrawHelpers.WithAlpha(halo, trailAlpha * 0.45f), size * 2.2f);

            // Body: four segments from tail to head with increasing alpha and width. Segmenting
            // keeps the head brightest and the tail faintest without needing a per-vertex
            // gradient (AddLine is single-color; AddRectFilledMultiColor can't be rotated).
            const int segments = 4;
            for (int i = 0; i < segments; i++)
            {
                float t0 = (float)i / segments;
                float t1 = (float)(i + 1) / segments;

                Vector2 a = Vector2.Lerp(tail, pos, t0);
                Vector2 b = Vector2.Lerp(tail, pos, t1);

                float fade = t0; // 0 at tail, ~1 at head
                float segAlpha = trailAlpha * (0.15f + 0.85f * fade);
                float segWidth = size * (0.55f + 1.25f * fade);

                dl.AddLine(a, b, DrawHelpers.WithAlpha(core, segAlpha), segWidth);
            }

            // A tiny hot speck at the tail — sells "the head left a burning bit behind" rather
            // than "a fading streak". Reads as an after-glow, gone by the time the head has
            // travelled twice its own trail length.
            dl.AddCircleFilled(tail, size * 0.45f, DrawHelpers.WithAlpha(core, trailAlpha * 0.35f));
        }

        // ---- head ----
        dl.AddCircleFilled(pos, size * 2.4f, DrawHelpers.WithAlpha(halo, alpha * 0.16f));
        dl.AddCircleFilled(pos, size,        DrawHelpers.WithAlpha(core, alpha * 0.88f));
        dl.AddCircleFilled(pos, size * 0.45f, DrawHelpers.WithAlpha(White, alpha * 0.95f));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => EmissionSpecs;

    /// <summary>
    /// Stroke-emitted sparks. Modeled on real sparks: launched hard (500-900 px/s) so each
    /// streak is visible in the frame it's born, then pulled down by gravity so the arc reads
    /// as a spark dying rather than a projectile. Gusts fire every ~0.25s, each with its own
    /// shared direction so a chain visibly spits a batch of sparks up-left, then another batch
    /// down-right, then another batch up-right — the crackle pattern a struck metal surface
    /// actually produces. Within a gust, particles fan out by only ±0.15 rad, so the shared
    /// direction is legible.
    ///
    /// Heavy's impact-burst sparks (see HeavyEffect.FireImpactBurst) are separate — they set
    /// their own burst sizes and velocities at trigger time, unaffected by this spec.
    /// </summary>
    private static readonly StrokeEmission[] EmissionSpecs =
    {
        new(Role: PrimitiveRole.Spark,
            DensityPer100px: 0.1f,
            SpeedMin: 1000f, SpeedMax: 2000f,
            LifespanMin: 0.30f, LifespanMax: 0.55f,
            SizeMin: 1.2f, SizeMax: 3.0f,
            SpreadRadians: 0.40f,       // full gust-to-gust direction range
            BiasVelocity: Vector2.Zero,
            PrimaryDirection: new Vector2(0f, -1f),
            Gravity: new Vector2(0f, 5000f),
            ClusterWindowSeconds: 0.45f,
            ClusterConeRadians: 0.12f),
    };
}