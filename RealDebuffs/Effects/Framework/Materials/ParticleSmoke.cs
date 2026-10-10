using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Smoke: one soft, dark, lazily expanding puff. Real fire is never just flame - the dark plume
/// above it is a huge part of why footage reads as a burning thing rather than a glowing shape.
///
/// Each puff is a radial gradient mesh (a center vertex and two rings whose alpha falls off to zero),
/// so it has no rim at all. Two things sell it as smoke rather than a gray disc:
///   - it is lit from below: young smoke, still near the flames, carries a warm rust tint that
///     cools to neutral charcoal as it ages;
///   - its outline is irregular (per-slice radius wobble from the seed) and it keeps growing as it
///     rises, so a column of puffs merges into a plume.
/// Drawn BEFORE the flames, so flames sit in front of the smoke they are making.
/// </summary>
public sealed class ParticleSmoke : IParticleMaterial
{
    public string Name => "particle.smoke";
    public string[] NaturalLanguageWords { get; } = { "smoke", "smog", "soot" };

    private static readonly uint Lit  = FireColor.Pack(0.30f, 0.125f, 0.055f);   // rust, still lit by the fire
    private static readonly uint Cool = FireColor.Pack(0.055f, 0.050f, 0.055f);  // cooled charcoal

    private const int Segs = 12;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.003f || p.Size <= 1f) return;

        float age = p.AgeRatio;
        float radius = p.Size * (0.65f + 1.15f * FireNoise.Smooth(age));   // billows as it rises
        if (radius <= 1f) return;

        // Warm -> cool over the first half of its life.
        uint baseCol = DrawHelpers.LerpColor(Lit, Cool, FireNoise.Smooth(age / 0.55f));

        float a = k * 0.34f;
        Span<float> rr = stackalloc float[2] { radius * 0.55f, radius };
        Span<uint>  cc = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(baseCol, a * 0.62f),
            DrawHelpers.WithAlpha(baseCol, 0f),
        };

        var pos = p.Position + new Vector2(p.Sway, 0f);
        float rot = DrawHelpers.Hash01(p.Seed + 9) * MathF.Tau;
        MeshDraw.Radial(dl, pos, MeshDraw.WhiteUv(ctx.Time),
                        DrawHelpers.WithAlpha(baseCol, a),
                        Segs, rr, cc, rot, squashY: 0.88f, seed: p.Seed, irregular: 0.55f);
    }
}
