using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A small bright white snow speck: a soft pale-blue halo around a near-white core. Cheap (two
/// circle fills) so it can be emitted at high density without cost, and simple enough that the
/// field still reads as snow at any size. The halo is a proportion of the speck's size, so a
/// large speck looks "fluffier" and a small speck looks like a distant glint, without needing
/// per-size branches.
/// </summary>
public sealed class ParticleSnow : IParticleMaterial
{
    public string Name => "particle.snow";

    private static readonly uint Halo = DrawHelpers.ToU32(0.72f, 0.86f, 1.00f, 1f);
    private static readonly uint Core = DrawHelpers.ToU32(0.98f, 1.00f, 1.00f, 1f);

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 0.3f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f) return;

        // Quick grow-in, hold, slow fade-out — specks stay visible across most of their life.
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
}