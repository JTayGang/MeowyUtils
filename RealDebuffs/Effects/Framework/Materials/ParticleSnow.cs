using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A snow particle: a small bright white speck with a soft pale-blue halo. When used as a stroke
/// emitter ("chains made of snow", "frosty tentacles"), it declares TWO emissions side by side:
/// dense specks as the primary field, plus a sparse layer of crystalline snowflakes rendered by
/// particle.snowflake. The combined look is a proper snowfall rather than just a scatter of
/// specks, and the flake half still uses its own renderer so the visuals stay distinct.
///
/// A user wanting only specks can select this material as a stroke emitter and expect the flake
/// half to show up too — that's the point. A user wanting only flakes should select
/// particle.snowflake instead, which has no speck layer.
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
    }

    public ReadOnlySpan<StrokeEmission> Emissions => EmissionSpecs;

    /// <summary>
    /// Two emissions running side by side. The speck emission is the primary field; the flake
    /// emission is a sparse crystalline layer that renders as particle.snowflake. Each has its
    /// own RenderMaterial so the two halves don't collapse to the same visual even when this
    /// material is invoked via an emit-axis override that would otherwise force one name.
    /// </summary>
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

        // Flakes — sparse crystalline layer. Bigger, slower, longer-lived than the specks,
        // so the eye reads them as distinct objects rather than as brighter specks.
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