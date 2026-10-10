using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Uniform full-rectangle fill. Ignores the edge mask entirely - use edge-glow when the edges
/// should fade. Covers flat washes (Blind's darkening), solid bands, and anything that just
/// wants to paint its whole rectangle one color.
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

/// <summary>
/// Per-edge fading gradient: every enabled edge draws a band tinted at the outer edge of the
/// rectangle and fading to transparent toward the center. Multiple edges overlap at corners
/// (double-tinted), which is the correct vignette-like look. All four corners use the same
/// rect, so the edge mask just picks which of four AddRectFilledMultiColor calls runs.
/// </summary>
public sealed class RegionEdgeGlow : IRegionMaterial
{
    public string Name => "region.edge-glow";

    public void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx)
    {
        float a = r.Alpha * ctx.Alpha;
        if (a <= 0.001f) return;

        uint tint = DrawHelpers.WithAlpha(r.Tint, a);
        uint clear = DrawHelpers.WithAlpha(r.Tint, 0f);
        var tl = r.Min;
        var br = r.Max;

        if (r.Top)
            dl.AddRectFilledMultiColor(tl, br, tint, tint, clear, clear);

        if (r.Bottom)
            dl.AddRectFilledMultiColor(tl, br, clear, clear, tint, tint);

        if (r.Left)
            dl.AddRectFilledMultiColor(tl, br, tint, clear, clear, tint);

        if (r.Right)
            dl.AddRectFilledMultiColor(tl, br, clear, tint, tint, clear);
    }
}