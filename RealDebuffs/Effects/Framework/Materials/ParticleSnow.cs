using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A fine snow speck. Small bright white core with a soft pale-blue halo, drawn as a plain dot
/// most of the time - that's what fine snow should look like, and turning every speck into a
/// star would overwhelm the field. But roughly a quarter get a tiny four-point crystalline
/// glint cross, which reads as light catching an ice grain and gives the field visible variance
/// as it falls.
///
/// When used as a stroke emitter ("frosty tentacles", "chains made of snow"), it declares TWO
/// emissions side by side: dense specks as the primary field, plus a sparse layer of crystalline
/// snowflakes rendered by particle.snowflake.
/// </summary>
public sealed class ParticleSnow : IParticleMaterial
{
    public string Name => "particle.snow";
    public string[] NaturalLanguageWords { get; } = { "snow" };

    private static readonly uint Halo = DrawHelpers.ToU32(0.72f, 0.86f, 1.00f, 1f);
    private static readonly uint Core = DrawHelpers.ToU32(0.98f, 1.00f, 1.00f, 1f);

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 0.3f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f) return;

        float sizeT;
        if      (p.AgeRatio < 0.15f) sizeT = 0.30f + 0.70f * (p.AgeRatio / 0.15f);
        else if (p.AgeRatio < 0.80f) sizeT = 1f;
        else                          sizeT = MathF.Max(0.10f, 1f - (p.AgeRatio - 0.80f) / 0.20f);

        float size = p.Size * sizeT;
        if (size <= 0.3f) return;

        var pos = p.Position + new Vector2(p.Sway, 0f);

        dl.AddCircleFilled(pos, size * 2.2f, DrawHelpers.WithAlpha(Halo, alpha * 0.22f));
        dl.AddCircleFilled(pos, size,        DrawHelpers.WithAlpha(Core, alpha * 0.92f));

        // ~25% of specks get a glint cross. Deterministic per particle (seed-based), so a given
        // speck keeps its glint for its whole life rather than flickering between the two.
        if ((p.Seed & 3) == 0)
        {
            float crossR = size * 2.6f;
            uint glint = DrawHelpers.WithAlpha(Core, alpha * 0.55f);
            dl.AddLine(pos + new Vector2(-crossR, 0f), pos + new Vector2(crossR, 0f), glint, 0.7f);
            dl.AddLine(pos + new Vector2(0f, -crossR), pos + new Vector2(0f, crossR), glint, 0.7f);
        }
    }

    public ReadOnlySpan<StrokeEmission> Emissions => EmissionSpecs;

    private static readonly StrokeEmission[] EmissionSpecs =
    {
        // Specks — the existing single-emission behavior, unchanged.
        new(Role: PrimitiveRole.Snow,
            DensityPer100px: 4f,
            SpeedMin: 18f, SpeedMax: 45f,
            LifespanMin: 0.8f, LifespanMax: 1.4f,
            SizeMin: 1f, SizeMax: 2.4f,
            SpreadRadians: 0.7f,
            BiasVelocity: new Vector2(0f, 12f),
            PrimaryDirection: new Vector2(0f, 1f),
            RenderMaterial: "particle.snow"),

        // Flakes — sparse crystalline layer.
        new(Role: PrimitiveRole.Snowflake,
            DensityPer100px: 0.6f,
            SpeedMin: 10f, SpeedMax: 25f,
            LifespanMin: 1.6f, LifespanMax: 2.8f,
            SizeMin: 5f, SizeMax: 9f,
            SpreadRadians: 0.9f,
            BiasVelocity: new Vector2(0f, 6f),
            PrimaryDirection: new Vector2(0f, 1f),
            RenderMaterial: "particle.snowflake"),
    };
}