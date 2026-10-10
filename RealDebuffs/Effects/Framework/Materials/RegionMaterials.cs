using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>Uniform full-rectangle fill; ignores the edge mask (flat washes like Blind's darkening, solid bands).</summary>
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

/// <summary>Per-edge fading gradient: each enabled edge draws a band fading toward the centre; corners double-tint.</summary>
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

/// <summary>Firelight: warm light thrown on the surroundings as a vertex grid; alpha ~(1-s)^2.2 down the rows, per-column drifting brightness. Min/Max is the bottom glow's reach (Bottom flag only).</summary>
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

        // Brighter mid-screen (flames are tallest there) plus per-column drifting flicker; both depend only on the column, so they are computed once per column.
        Span<float> mid = stackalloc float[cols + 1];
        Span<float> flick = stackalloc float[cols + 1];
        for (int c = 0; c <= cols; c++)
        {
            float x01 = c / (float)cols;
            mid[c] = 1f - 0.32f * MathF.Abs(x01 - 0.5f) * 2f;
            float f = 0.68f + 0.32f * (0.5f + 0.5f * Noise.Perlin(x01 * 7.5f + 3.1f, t * 1.15f));
            f *= 0.90f + 0.10f * (0.5f + 0.5f * Noise.Perlin(x01 * 19f, t * 3.4f));
            flick[c] = f;
        }

        int i = 0;
        for (int row = 0; row <= Rows; row++)
        {
            float s = row / (float)Rows;
            float fall = MathF.Pow(1f - s, 2.2f);
            float y = bottom - s * reach;
            uint baseCol = FireColor.Heat(0.60f - 0.34f * s);

            for (int c = 0; c <= cols; c++)
            {
                MeshDraw.P[i] = new Vector2(left + c / (float)cols * width, y);
                MeshDraw.C[i] = DrawHelpers.WithAlpha(baseCol, a * fall * mid[c] * flick[c]);
                i++;
            }
        }
        MeshDraw.Grid(dl, cols, Rows, uv);

        // ---- lower side glow (both sides, mirrored) ----
        float sideW = MathF.Min(width * 0.20f, ctx.ShortSide * 0.34f);
        float sideH = MathF.Min(reach * 1.25f, ctx.ScreenH * 0.55f);
        const int sCols = 5, sRows = 8;
        Span<float> hFall = stackalloc float[sCols + 1];
        for (int c = 0; c <= sCols; c++)
            hFall[c] = MathF.Pow(1f - c / (float)sCols, 2.0f);    // c/sCols: 0 at the screen edge -> 1 inward

        for (int side = 0; side < 2; side++)
        {
            i = 0;
            for (int row = 0; row <= sRows; row++)
            {
                float sy = row / (float)sRows;
                float y = bottom - sy * sideH;
                float vFall = MathF.Pow(1f - sy, 1.6f);
                float rowFlick = 0.7f + 0.3f * (0.5f + 0.5f * Noise.Perlin(sy * 3f + side * 11f, t * 1.3f));
                uint col = FireColor.Heat(0.55f - 0.30f * sy);

                for (int c = 0; c <= sCols; c++)
                {
                    float sx = c / (float)sCols;
                    float x = side == 0 ? left + sx * sideW : right - sx * sideW;
                    MeshDraw.P[i] = new Vector2(x, y);
                    MeshDraw.C[i] = DrawHelpers.WithAlpha(col, a * 0.62f * hFall[c] * vFall * rowFlick);
                    i++;
                }
            }
            MeshDraw.Grid(dl, sCols, sRows, uv);
        }
    }
}
