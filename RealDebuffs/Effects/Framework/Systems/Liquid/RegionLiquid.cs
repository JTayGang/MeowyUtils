namespace RealDebuffs.Effects.Framework.Materials;

/// The pool, streams, foam and bubbles of a LiquidField, drawn in the liquid the field was given.
public sealed class RegionLiquid : RegionMaterial<LiquidField>
{
    public override string Name => "region.liquid";

    protected override void Draw(ImDrawListPtr dl, LiquidField f, in RegionPrimitive r, in MaterialContext ctx) =>
        LiquidDraw.Draw(dl, f, r.Alpha * ctx.Alpha, in ctx);
}
