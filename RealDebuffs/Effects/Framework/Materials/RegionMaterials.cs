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

/// <summary>Firelight: warm light thrown on the surroundings as a vertex grid; alpha ~(1-s)^2.2 down the rows, per-column drifting brightness.</summary>
public sealed class RegionFirelight : IRegionMaterial
{
    private readonly string _name;
    private readonly Palette? _ramp;  // null = FireColor.Heat, the orange firelight every existing effect uses

    /// <summary>Orange firelight (region.firelight).</summary>
    public RegionFirelight() : this("region.firelight", null) { }

    /// <summary>The same thrown light on another heat ramp: register one per theme (see region.hellglow in MaterialRegistry).</summary>
    internal RegionFirelight(string name, Palette? ramp)
    {
        _name = name;
        _ramp = ramp;
    }

    public string Name => _name;

    private uint Heat(float t) => _ramp is null ? FireColor.Heat(t) : _ramp.Sample(t);

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

        // bottom glow
        int cols = Math.Clamp((int)(width / 46f), 10, 40);

        // Brighter mid-screen (flames are tallest there) plus per-column drifting flicker; both depend only on the column
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
            uint baseCol = Heat(0.60f - 0.34f * s);

            for (int c = 0; c <= cols; c++)
            {
                MeshDraw.P[i] = new Vector2(left + c / (float)cols * width, y);
                MeshDraw.C[i] = DrawHelpers.WithAlpha(baseCol, a * fall * mid[c] * flick[c]);
                i++;
            }
        }
        MeshDraw.Grid(dl, cols, Rows, uv);

        // lower side glow (both sides, mirrored)
        float sideW = MathF.Min(width * 0.20f, ctx.ShortSide * 0.34f);
        float sideH = MathF.Min(reach * 1.25f, ctx.ScreenH * 0.55f);
        const int sCols = 5, sRows = 8;
        Span<float> hFall = stackalloc float[sCols + 1];
        for (int c = 0; c <= sCols; c++)
            hFall[c] = MathF.Pow(1f - c / (float)sCols, 2.0f);  // c/sCols: 0 at the screen edge -> 1 inward

        for (int side = 0; side < 2; side++)
        {
            i = 0;
            for (int row = 0; row <= sRows; row++)
            {
                float sy = row / (float)sRows;
                float y = bottom - sy * sideH;
                float vFall = MathF.Pow(1f - sy, 1.6f);
                float rowFlick = 0.7f + 0.3f * (0.5f + 0.5f * Noise.Perlin(sy * 3f + side * 11f, t * 1.3f));
                uint col = Heat(0.55f - 0.30f * sy);

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

/// <summary>The frost: a milky coating creeping in from the edges as one sparse vertex mesh over a FrostField; ignores any other State.</summary>
public sealed class RegionFrost : IRegionMaterial
{
    public string Name => "region.frost-cover";

    // look
    private const float FilmAlpha = 0.05f;  // the thin film that coats even the middle of the glass
    private const float IceAlpha  = 0.80f;  // extra opacity of thick ice
    private const float SettleAge = 8f;  // after this the front has all but stopped, and the haze is rebuilt only now and then
    private const float HazeRefresh = 0.12f;  // seconds between rebuilds once settled (also how fast a color-override change shows)

    public void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx)
    {
        if (r.State is not FrostField f) return;

        float a = r.Alpha * ctx.Alpha * f.Alpha;
        if (a <= 0.002f) return;

        // Everything looks colder as the glass freezes: one very faint blue wash across the screen.
        float wash = a * 0.07f * Math.Clamp(f.Progress, 0f, 1f);
        if (wash > 0.003f)
            dl.AddRectFilled(Vector2.Zero, new Vector2(ctx.ScreenW, ctx.ScreenH), DrawHelpers.WithAlpha(DrawHelpers.Pack(0.45f, 0.65f, 0.95f), wash));

        DrawHaze(dl, f, a, ctx);

        var style = new Dendrite.DrawStyle(
            IceColor.Crystal,
            Shadow: DrawHelpers.Pack(0.07f, 0.17f, 0.36f),
            ShadowOffset: StudioLighting.ShadowDirection * (1.3f * ctx.ScreenScale),
            ShadowAlpha: 0.22f,
            Body: IceColor.Pack(0.88f, 0.94f, 1.00f), BodyAlpha: 0.20f,
            WidthScale: ctx.ScreenScale, Alpha: a, Time: ctx.Time);
        f.Ferns.Draw(dl, MeshDraw.WhiteUv(ctx.Time), f.Age, style);
    }

    private static void DrawHaze(ImDrawListPtr dl, FrostField f, float a, in MaterialContext ctx)
    {
        var g = f.Growth;

        // Once settled the haze barely moves, so it's rebuilt a few times a second, or at once when overall opacity changes.
        bool fresh = f.Age <= SettleAge || a != f.HazeAlpha || MathF.Abs(ctx.Time - f.HazeTime) >= HazeRefresh;
        if (fresh)
        {
            BuildHaze(f, a);
            f.HazeAlpha = a;
            f.HazeTime = ctx.Time;
        }

        MeshDraw.SparseGrid(dl, g.Cols, g.Rows, g.Positions, f.Haze, MeshDraw.WhiteUv(ctx.Time));
    }

    private static void BuildHaze(FrostField f, float a)
    {
        var g = f.Growth;
        int nv = (g.Cols + 1) * (g.Rows + 1);
        float ice = f.Progress, fog = f.Fog;

        float slam = MathF.Exp(-f.Age / 0.45f);  // the border darkens hard at first, then settles
        float rimAlpha = a * (0.30f + 0.45f * slam);
        bool overridden = DrawHelpers.ColorOverrideActive;  // no override: skip the per-vertex remap entirely

        float[] arrivalArr = g.Arrival, mottle = f.Mottle, rimArr = f.Rim, cap = f.Cap;
        uint[] haze = f.Haze;

        for (int i = 0; i < nv; i++)
        {
            float arrival = arrivalArr[i];
            float mott = mottle[i];
            float rim = rimArr[i];

            // Bottom layer: the cold rim, there from the first frame.
            float cr = 0.09f, cg = 0.20f, cb = 0.42f, ca = rimAlpha * rim;

            // Condensation: a thin cool film that is already on the glass before the ice arrives.
            float fogLead = fog - arrival;
            if (fogLead > 0f)
                Over(ref cr, ref cg, ref cb, ref ca, 0.55f, 0.72f, 0.95f, a * 0.12f * Math.Clamp(fogLead * 1.8f, 0f, 1f) * (0.7f + 0.3f * mott));

            // Ice: a fairly crisp front, then thickening and whitening behind it.
            float lead = ice - arrival;
            if (lead > 0f)
            {
                // A front soft enough for the grid to show cleanly (a sharper one aliases into steps).
                float edge = DrawHelpers.Smooth(lead / 0.26f);
                float thick = Math.Clamp(lead / 0.75f, 0f, 1f);
                // Frost is thickest at the border and thins toward the middle, which stays mostly clear: the ferns reach in, the coat doesn't.
                float dens = Math.Clamp(thick * cap[i] * (0.60f + 0.40f * mott) + 0.55f * rim * thick, 0f, 1f);
                uint c = IceColor.Frost.Sample(0.15f + 0.85f * dens);
                Over(ref cr, ref cg, ref cb, ref ca,
                     (c & 255) / 255f, ((c >> 8) & 255) / 255f, ((c >> 16) & 255) / 255f,
                     a * edge * (FilmAlpha + IceAlpha * dens));

                // A faint bright band just behind the front, where the ice is still forming.
                float d = (lead - 0.14f) / 0.12f;
                if (d * d < 6f)
                    Over(ref cr, ref cg, ref cb, ref ca, 0.96f, 0.99f, 1f, a * 0.14f * MathF.Exp(-d * d));
            }

            // Vertex colours aren't premultiplied: a transparent vertex must still carry frost-coloured RGB
            if (ca <= 0.002f) { cr = 0.55f; cg = 0.72f; cb = 0.92f; ca = 0f; }
            haze[i] = Pack(cr, cg, cb, ca, overridden);
        }
    }

    /// <summary>Opaque colour plus alpha: WithAlpha(Pack(rgb), alpha) minus the override lookup</summary>
    private static uint Pack(float r, float g, float b, float alpha, bool overridden)
    {
        uint rgb = DrawHelpers.Pack(r, g, b);
        if (overridden) return DrawHelpers.WithAlpha(IceColor.Tintable(rgb), alpha);
        return (rgb & 0x00FFFFFFu) | DrawHelpers.AlphaBits(alpha);
    }

    /// <summary>Straight-alpha "over": layer (r, g, b, a) on top of the accumulated color.</summary>
    private static void Over(ref float r, ref float g, ref float b, ref float a, float r2, float g2, float b2, float a2)
    {
        float outA = a2 + a * (1f - a2);
        if (outA <= 1e-5f) return;
        float k1 = a2 / outA, k0 = 1f - k1;
        r = r2 * k1 + r * k0;
        g = g2 * k1 + g * k0;
        b = b2 * k1 + b * k0;
        a = outA;
    }
}
