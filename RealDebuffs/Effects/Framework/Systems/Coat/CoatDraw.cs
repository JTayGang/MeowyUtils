using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>One band of film along a coat: where it lies across the strand and what colour each point is.</summary>
internal interface IBand
{
    /// <summary>For cell i: which side (+1 underside, -1 upper side), how far in px from the strand's centre the band starts and ends along it, and how strongly it shows (0..1).</summary>
    float Extent(Coat c, int i, out float uIn, out float uOut, out float sign);

    /// <summary>Colour at x across the band (-1 inner edge .. 1 outer edge); dir is the screen direction of the side.</summary>
    uint Color(Coat c, int i, float x, Vector2 dir, float show);

    /// <summary>True for a band that spans the strand: it takes the side that varies smoothly, not the underside.</summary>
    bool Continuous => false;
}

internal static class CoatDraw
{
    private const int Cols = 7;
    internal static readonly MeshBuilder Mb = new();

    /// <summary>Lays a band of vertices along the cells and stitches them into one lit strip.</summary>
    public static void Band<T>(ImDrawListPtr dl, Coat c, ref T band, in MaterialContext ctx) where T : struct, IBand
    {
        int n = c.Live;
        if (n < 2) return;
        bool wrap = c.Closed && n == c.N;
        Span<Vector2> sp = stackalloc Vector2[TubeChart.MaxU], ssd = stackalloc Vector2[TubeChart.MaxU], snm = stackalloc Vector2[TubeChart.MaxU];
        Interpolate(c, n, wrap, sp, ssd, snm);
        Mb.Reserve(n * Cols, (n + 1) * Cols * 6);
        int first = Mb.Vertices;
        for (int i = 0; i < n; i++)
        {
            float show = band.Extent(c, i, out float uIn, out float uOut, out float sign);
            Vector2 d = (band.Continuous ? ssd[i] : snm[i]) * sign;
            for (int j = 0; j < Cols; j++)
            {
                float x = -1f + 2f * j / (Cols - 1);
                Mb.VertexReserved(sp[i] + d * (uIn + (uOut - uIn) * (x + 1f) * 0.5f), band.Color(c, i, x, d, show));
            }
        }
        Mb.StitchGrid(first, n, Cols);
        if (wrap)
            for (int j = 0; j + 1 < Cols; j++)
                Mb.QuadReserved(first + (n - 1) * Cols + j, first + (n - 1) * Cols + j + 1, first + j, first + j + 1);
        Mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));
    }

    /// <summary>The strand is a coarse polyline whose corners its own material hides; a band cut straight off it would show them. This smooths what is drawn (positions, then the side and underside from them) and leaves the simulation's own samples on the host path.</summary>
    internal static void Interpolate(Coat c, int n, bool wrap, Span<Vector2> p, Span<Vector2> sd, Span<Vector2> nm)
    {
        for (int i = 0; i < n; i++) p[i] = c.P[i];
        if (n >= 3)
        {
            Span<Vector2> tmp = stackalloc Vector2[TubeChart.MaxU];
            for (int pass = 0; pass < 3; pass++)
            {
                for (int i = 0; i < n; i++)
                {
                    int a = i > 0 ? i - 1 : (wrap ? n - 1 : 0), b = i < n - 1 ? i + 1 : (wrap ? 0 : n - 1);
                    tmp[i] = (p[a] + 2f * p[i] + p[b]) * 0.25f;
                }
                if (!wrap) { tmp[0] = p[0]; tmp[n - 1] = p[n - 1]; }
                for (int i = 0; i < n; i++) p[i] = tmp[i];
            }
        }
        Vector2 prev = default;
        for (int i = 0; i < n; i++)
        {
            Vector2 t = c.T[i];
            if (n >= 3)
            {
                int a = i > 0 ? i - 1 : (wrap ? n - 1 : 0), b = i < n - 1 ? i + 1 : (wrap ? 0 : n - 1);
                Vector2 dd = p[b] - p[a];
                if (dd.LengthSquared() > 1e-6f) t = Vector2.Normalize(dd);
            }
            Vector2 s = new(-t.Y, t.X);
            if (i == 0 ? s.Y < 0f : Vector2.Dot(s, prev) < 0f) s = -s;  // a side that never flips along the strand, so strips never twist
            sd[i] = prev = s;
            nm[i] = s.Y >= 0f ? s : -s;
        }
    }

    /// <summary>A lit colour and opacity as a vertex colour, recoloured when the effect carries a colour override.</summary>
    public static uint Pack(Vector3 rgb, float alpha)
    {
        float dye = DrawHelpers.ColorOverrideChroma;
        if (dye > 0f) rgb = DrawHelpers.BoostSaturation(rgb, dye, DrawHelpers.SoftDye);
        return DrawHelpers.Tint(DrawHelpers.Pack(rgb.X, rgb.Y, rgb.Z), alpha, DrawHelpers.ColorOverrideActive);
    }

    /// <summary>1 across the middle of a band falling to `edge` at its sides.</summary>
    public static float Soft(float x, float edge)
    {
        float x2 = x * x;
        return DrawHelpers.Lerp(1f, edge, x2 * x2);
    }
}
