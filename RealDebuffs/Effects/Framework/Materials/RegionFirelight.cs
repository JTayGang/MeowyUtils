using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Firelight: the warm light a fire throws onto everything around it, drawn as a vertex-colored
/// grid instead of a linear gradient. Two things make a linear edge band look fake and this doesn't:
///
///  - FALLOFF: light dies off quickly near the source and lingers faintly far away, so alpha
///    follows roughly (1-s)^2.2 across the rows instead of a straight ramp, and color shifts from
///    orange at the base toward deep red at the reach;
///  - IRREGULARITY: the glow is stronger under the tall flames and weaker under the short ones, and
///    it shifts as they flicker, so each column gets its own slowly drifting brightness.
///
/// The rectangle it is given (Min/Max) is the reach of the BOTTOM glow: its height is the glow height.
/// The lower side corners get a matching, weaker glow, since Burns also burns up the sides.
/// Only the Bottom flag is needed; Top/Left/Right are ignored. If someone swaps this slot to
/// region.edge-glow the same primitive still degrades to a plain bottom band.
/// </summary>
public sealed class RegionFirelight : IRegionMaterial
{
    public string Name => "region.firelight";

    private const int Rows = 8;

    public void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx)
    {
        float a = r.Alpha * ctx.Alpha;
        if (a <= 0.002f) return;

        float t = ctx.Time;
        Vector2 uv = MeshDraw.WhiteUv(t);

        float left = r.Min.X, right = r.Max.X, bottom = r.Max.Y;
        float width = right - left;
        float reach = r.Max.Y - r.Min.Y;
        if (width < 8f || reach < 4f) return;

        // ---- bottom glow ----
        int cols = Math.Clamp((int)(width / 46f), 10, 40);
        int i = 0;
        for (int row = 0; row <= Rows; row++)
        {
            float s = row / (float)Rows;
            float fall = MathF.Pow(1f - s, 2.2f);
            float y = bottom - s * reach;
            uint baseCol = FireColor.Heat(0.60f - 0.34f * s);

            for (int c = 0; c <= cols; c++)
            {
                float x01 = c / (float)cols;
                // brighter mid-screen (flames are tallest there), plus per-column flicker that drifts
                float mid = 1f - 0.32f * MathF.Abs(x01 - 0.5f) * 2f;
                float flick = 0.68f + 0.32f * (0.5f + 0.5f * FireNoise.Perlin(x01 * 7.5f + 3.1f, t * 1.15f));
                flick *= 0.90f + 0.10f * (0.5f + 0.5f * FireNoise.Perlin(x01 * 19f, t * 3.4f));

                MeshDraw.P[i] = new Vector2(left + x01 * width, y);
                MeshDraw.C[i] = DrawHelpers.WithAlpha(baseCol, a * fall * mid * flick);
                i++;
            }
        }
        MeshDraw.Grid(dl, cols, Rows, uv);

        // ---- lower side glow (both sides, mirrored) ----
        float sideW = MathF.Min(width * 0.20f, ctx.ShortSide * 0.34f);
        float sideH = MathF.Min(reach * 1.25f, ctx.ScreenH * 0.55f);
        const int sCols = 5, sRows = 8;
        for (int side = 0; side < 2; side++)
        {
            i = 0;
            for (int row = 0; row <= sRows; row++)
            {
                float sy = row / (float)sRows;
                float y = bottom - sy * sideH;
                float vFall = MathF.Pow(1f - sy, 1.6f);
                float flick = 0.7f + 0.3f * (0.5f + 0.5f * FireNoise.Perlin(sy * 3f + side * 11f, t * 1.3f));

                for (int c = 0; c <= sCols; c++)
                {
                    float sx = c / (float)sCols;                 // 0 at the screen edge -> 1 inward
                    float hFall = MathF.Pow(1f - sx, 2.0f);
                    float x = side == 0 ? left + sx * sideW : right - sx * sideW;
                    uint col = FireColor.Heat(0.55f - 0.30f * sy);
                    MeshDraw.P[i] = new Vector2(x, y);
                    MeshDraw.C[i] = DrawHelpers.WithAlpha(col, a * 0.62f * hFall * vFall * flick);
                    i++;
                }
            }
            MeshDraw.Grid(dl, sCols, sRows, uv);
        }
    }
}
