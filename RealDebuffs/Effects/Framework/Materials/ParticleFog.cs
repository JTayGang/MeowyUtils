using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A large, very soft fog blob. Two overlapping low-alpha circles of pale blue-white at very
/// large sizes, so dozens of overlapping fog particles merge into a continuous creeping haze
/// rather than reading as individual bubbles. This is the same "many soft circles overlap to
/// form a mass" trick the ember material uses for fire puffs, scaled up and desaturated.
///
/// Because fog covers large regions at low alpha, a huge particle budget is not needed - twenty
/// or thirty on screen at once is plenty for a full-screen mist.
/// </summary>
public sealed class ParticleFog : IParticleMaterial
{
    public string Name => "particle.fog";

    private static readonly uint Tint = DrawHelpers.ToU32(0.72f, 0.82f, 0.94f, 1f);

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

        // Two overlapping low-alpha fills: wide outer wisp + tighter inner core.
        dl.AddCircleFilled(pos, size,        DrawHelpers.WithAlpha(Tint, alpha * 0.09f));
        dl.AddCircleFilled(pos, size * 0.62f, DrawHelpers.WithAlpha(Tint, alpha * 0.14f));
    }
}