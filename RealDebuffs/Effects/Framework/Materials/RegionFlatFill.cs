using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Uniform full-rectangle fill. Ignores the edge mask entirely - use region.edge-glow when the
/// edges should fade. Covers flat washes (Blind's darkening), solid bands, and anything that
/// just wants to paint its whole rectangle one color.
/// </summary>
public sealed class RegionFlatFill : IRegionMaterial
{
    public string Name => "region.flat-fill";

    public void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx)
    {
        float a = r.Alpha * ctx.Alpha;
        if (a <= 0.001f) return;

        dl.AddRectFilled(r.Min, r.Max, DrawHelpers.WithAlpha(r.Tint, a));
    }
}