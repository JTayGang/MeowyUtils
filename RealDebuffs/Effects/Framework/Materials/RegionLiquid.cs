using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>The pool, streams, foam and bubbles of a LiquidField, drawn in the liquid the field was given.</summary>
public sealed class RegionLiquid : IRegionMaterial
{
    public string Name => "region.liquid";

    public void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx)
    {
        if (r.State is LiquidField f) LiquidDraw.Draw(dl, f, r.Alpha * ctx.Alpha, in ctx);
    }
}
