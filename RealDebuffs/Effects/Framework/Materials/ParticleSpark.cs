using System;
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

    private static readonly uint White = DrawHelpers.ToU32(1.00f, 1.00f, 0.96f, 1f);
    private static readonly uint Gold  = DrawHelpers.ToU32(1.00f, 0.78f, 0.32f, 1f);

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 0.3f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f) return;

        // Sharp grow-in then steady fade. Sparks are brightest right after they spawn.
        float sizeT = p.AgeRatio < 0.12f
            ? 0.25f + 0.75f * (p.AgeRatio / 0.12f)
            : MathF.Max(0.05f, 1f - (p.AgeRatio - 0.12f) / 0.88f);

        float size = p.Size * sizeT;
        if (size <= 0.3f) return;

        float heat = DrawHelpers.Hash01(p.Seed);
        uint core = DrawHelpers.LerpColor(Gold, White, heat);
        uint halo = DrawHelpers.LerpColor(Gold, White, heat * 0.5f);

        var pos = p.Position + new Vector2(p.Sway, 0f);

        dl.AddCircleFilled(pos, size * 2.4f, DrawHelpers.WithAlpha(halo, alpha * 0.16f));
        dl.AddCircleFilled(pos, size,        DrawHelpers.WithAlpha(core, alpha * 0.88f));
        dl.AddCircleFilled(pos, size * 0.45f, DrawHelpers.WithAlpha(White, alpha * 0.95f));
    }
    private static readonly StrokeEmission EmissionSpec = new(
        Role: PrimitiveRole.Spark,
        DensityPer100px: 3f,
        SpeedMin: 40f, SpeedMax: 120f,
        LifespanMin: 0.25f, LifespanMax: 0.55f,
        SizeMin: 0.8f, SizeMax: 1.8f,
        SpreadRadians: 0.7f,
        BiasVelocity: new Vector2(0f, -20f)); // no PrimaryDirection: perpendicular to strand

    public StrokeEmission? Emission => EmissionSpec;
}