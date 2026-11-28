namespace RealDebuffs.Effects.Framework;

/// One band of film along a coat: where it lies across the strand and what colour each point is.
internal interface IBand
{
    /// For cell i: side (+1 underside, -1 upper), where the band starts and ends in px from the strand's centre, and how strongly it shows (0..1).
    float Extent(Coat c, int i, out float uIn, out float uOut, out float sign);

    /// Colour at x across the band (-1 inner edge .. 1 outer edge); dir is the screen direction of the side.
    uint Color(Coat c, int i, float x, Vector2 dir, float show);

    /// True for a band that spans the strand: it takes the side that varies smoothly, not the underside.
    bool Continuous => false;
}

internal static class CoatDraw
{
    const int Cols = 7;
    internal static readonly MeshBuilder Mb = new();

    /// Lays a band of vertices along the cells and stitches them into one lit strip.
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

    /// Smooths the drawn strand (positions, then side and underside) so bands don't show polyline corners; the simulation keeps the host's samples.
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

    /// A lit colour and opacity as a vertex colour, recoloured when the effect carries a colour override.
    public static uint Pack(Vector3 rgb, float alpha)
    {
        float dye = ColorOverrideChroma;
        if (dye > 0f) rgb = BoostSaturation(rgb, dye, SoftDye);
        return Tint(DrawHelpers.Pack(rgb), alpha, ColorOverrideActive);
    }

    /// 1 across the middle of a band falling to `edge` at its sides.
    public static float Soft(float x, float edge)
    {
        float x2 = x * x;
        return Lerp(1f, edge, x2 * x2);
    }

    /// The strand row the chart's r-th of `rows` rows sits on, among `bodyRows` distinct body rows.
    internal static int PickRow(int r, int rows, int bodyRows, bool closed)
        => closed ? (int)((long)r * bodyRows / rows) : (rows == 1 ? 0 : (int)Round(r * (bodyRows - 1f) / (rows - 1f)));

    /// Du, Dv and GravityDown of every cell of a tube chart whose positions, normals and depths are set. Du and Dv are the screen direction of rising U and V scaled by the true 3D step, so flow sees the true slope (0 at the silhouette). A drop hangs where the surface faces down; an open strand's end that points down is the chart's downhill edge. axis: each row's screen tangent, for rows whose neighbours coincide.
    internal static void Slopes(CoatChart ch, int R, int V, bool closed, ReadOnlySpan<Vector2> axis)
    {
        for (int r = 0; r < R; r++)
            for (int v = 0; v < V; v++)
            {
                ref var q = ref ch.S[r * V + v];
                int rp = r > 0 ? r - 1 : (closed ? R - 1 : r), rn = r < R - 1 ? r + 1 : (closed ? 0 : r);
                int vp = v > 0 ? v - 1 : V - 1, vn = v < V - 1 ? v + 1 : 0;
                q.Du = Slope(in ch.S[rp * V + v], in ch.S[rn * V + v], axis[r]);
                q.Dv = Slope(in ch.S[r * V + vp], in ch.S[r * V + vn], default);

                float best = Max(0f, q.Normal.Y);
                if (!closed) { if (r == 0 && -q.Du.Y > best) best = -q.Du.Y; if (r == R - 1 && q.Du.Y > best) best = q.Du.Y; }
                q.GravityDown = best;
            }
    }

    static Vector2 Slope(in CoatSurfaceSample a, in CoatSurfaceSample b, Vector2 fallback)
    {
        Vector2 d = b.Pos - a.Pos;
        float dz = b.Z - a.Z, len = Sqrt(d.X * d.X + d.Y * d.Y + dz * dz);
        return len > 1e-4f ? d / len : fallback;
    }
}
