using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Fire particle: a rising glowing blob with a heat-driven color ramp. Drawn with layered soft
/// circles — sparks get a tight bright core with a small halo and a hot inner dot while young;
/// puffs get two wide low-alpha fills that blend into the surrounding fire. The distinction is
/// driven by particle size, not by a flag: small particles (sparks) render sharp, large particles
/// (puffs) render diffuse. That's what makes a puff field and a spark field emitted from the same
/// material look like two layers of one fire rather than two sizes of the same shape.
///
/// Layered translucent circles are what let the field read as fire rather than as a scatter of
/// dots: with low per-particle alpha, dozens of particles overlap and merge into a continuous
/// mass. Hard-edged geometry (triangles, high-alpha circles) preserves each particle as an
/// individual shape and the field starts to look like floating objects.
/// </summary>
public sealed class ParticleEmber : IParticleMaterial
{
    public string Name => "particle.ember";

    // ---- fire color ramp, hot to cold ----
    private static readonly uint HotWhite = DrawHelpers.ToU32(1.00f, 0.97f, 0.82f, 1f);
    private static readonly uint Yellow   = DrawHelpers.ToU32(1.00f, 0.78f, 0.20f, 1f);
    private static readonly uint Orange   = DrawHelpers.ToU32(1.00f, 0.45f, 0.05f, 1f);
    private static readonly uint Ember    = DrawHelpers.ToU32(0.95f, 0.18f, 0.02f, 1f);
    private static readonly uint DeepRed  = DrawHelpers.ToU32(0.55f, 0.06f, 0.01f, 1f);
    private static readonly uint Soot     = DrawHelpers.ToU32(0.05f, 0.01f, 0.005f, 1f);

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 0.3f) return;

        float heat = DrawHelpers.Hash01(p.Seed);
        float hue  = DrawHelpers.HashRange(p.Seed + 1, -1f, 1f);

        // ---- size envelope: grow in, hold, shrink out ----
        float sizeT;
        if      (p.AgeRatio < 0.20f) sizeT = 0.35f + 0.65f * (p.AgeRatio / 0.20f);
        else if (p.AgeRatio < 0.55f) sizeT = 1f;
        else                          sizeT = MathF.Max(0.05f, 1f - (p.AgeRatio - 0.55f) / 0.45f);

        float size = p.Size * sizeT;
        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f) return;

        uint col = ColorForAge(p.AgeRatio, heat, hue);
        Vector2 pos = p.Position + new Vector2(p.Sway, 0f);

        // ---- size ratio: 0 = smallest sparks, 1 = biggest puffs ----
        // 25/1000 of the shorter screen side is the rough midpoint of the size bands the emitters
        // produce. Below ~0.35, we're in the spark band; above, we're in the puff band.
        float sizeRatio = Math.Clamp(p.Size / (ctx.ShortSide * 0.025f), 0f, 1f);

        if (sizeRatio < 0.35f)
        {
            // ---- Spark: tight bright core with a small halo, hot inner dot while young ----
            dl.AddCircleFilled(pos, size * 1.6f, DrawHelpers.WithAlpha(col, alpha * 0.18f));
            dl.AddCircleFilled(pos, size,        DrawHelpers.WithAlpha(col, alpha * 0.80f));

            if (p.AgeRatio < 0.40f)
            {
                float innerK = 1f - p.AgeRatio / 0.40f;
                uint inner = DrawHelpers.LerpColor(col, HotWhite, innerK * 0.60f);
                dl.AddCircleFilled(pos, size * 0.50f, DrawHelpers.WithAlpha(inner, alpha * 0.95f));
            }
        }
        else
        {
            // ---- Puff: wide, soft, low-alpha volume ----
            // Two overlapping fills with very low per-particle alpha. Dozens of these overlapping
            // in a dense field is what produces the continuous glowing mass.
            dl.AddCircleFilled(pos, size * 1.35f, DrawHelpers.WithAlpha(col, alpha * 0.13f));
            dl.AddCircleFilled(pos, size * 0.80f, DrawHelpers.WithAlpha(col, alpha * 0.28f));
        }
    }

    /// <summary>
    /// Color at a given point in a particle's life. Age drives the base ramp; heat shifts the
    /// whole ramp hotter; hue adds a slight warm/cool tint so no two particles look identical.
    /// </summary>
    private static uint ColorForAge(float ageNorm, float heat, float hue)
    {
        float t = Math.Clamp(ageNorm - heat * 0.14f + hue * 0.05f, 0f, 1f);

        uint c;
        if      (t < 0.10f) c = DrawHelpers.LerpColor(HotWhite, Yellow,   t / 0.10f);
        else if (t < 0.28f) c = DrawHelpers.LerpColor(Yellow,   Orange,  (t - 0.10f) / 0.18f);
        else if (t < 0.55f) c = DrawHelpers.LerpColor(Orange,   Ember,   (t - 0.28f) / 0.27f);
        else if (t < 0.82f) c = DrawHelpers.LerpColor(Ember,    DeepRed, (t - 0.55f) / 0.27f);
        else                c = DrawHelpers.LerpColor(DeepRed,  Soot,    (t - 0.82f) / 0.18f);

        if (hue > 0f) return DrawHelpers.LerpColor(c, HotWhite, hue * 0.10f);
        return DrawHelpers.LerpColor(c, DeepRed, -hue * 0.14f);
    }

    private static readonly StrokeEmission EmissionSpec = new(
        Role: PrimitiveRole.Ember,
        DensityPer100px: 6f,
        SpeedMin: 18f, SpeedMax: 50f,
        LifespanMin: 0.65f, LifespanMax: 1.30f,
        SizeMin: 5f, SizeMax: 12f,
        SpreadRadians: 0.55f,
        BiasVelocity: new Vector2(0f, -14f),
        PrimaryDirection: new Vector2(0f, -1f));

    public StrokeEmission? Emission => EmissionSpec;
}