using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// A precomputed "when does this effect reach this part of the screen" field: for every vertex of a
/// coarse screen grid, the moment (in abstract progress units) a front spreading from a few
/// nucleation points and creeping in from the edges arrives there. Built once per cast, then each
/// frame the question "is this spot covered yet, and for how long?" is just <c>progress - Arrival</c>:
/// no simulation, no per-frame allocation, and the front keeps its irregular shape however fast
/// or slow the caller moves the progress value.
///
/// Frost uses it for ice creeping in from the edges; the same field would drive poison seeping in,
/// darkness closing in, or burn-in spreading from the corners (anything that covers the screen).
/// </summary>
internal sealed class ScreenGrowth
{
    /// <param name="Depth">Fraction of the short side the front reaches inward from an edge, roughly.</param>
    /// <param name="SpreadRate">How slowly it travels ALONG the edges away from a nucleation site (higher = slower).</param>
    /// <param name="DepthWeight">How much of the arrival time is spent creeping inward.</param>
    /// <param name="Warp">Irregularity of the front (0 = smooth, ~0.5 = strongly lobed).</param>
    /// <param name="Sites">Number of nucleation points placed around the screen border.</param>
    public readonly record struct Settings(float Depth, float SpreadRate, float DepthWeight, float Warp, int Sites);

    /// <summary>A nucleation point on the screen border.</summary>
    public readonly record struct Site(Vector2 Pos, Vector2 Inward, float Delay, float Strength);

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public Vector2 Size { get; private set; }
    public float CellW { get; private set; }
    public float CellH { get; private set; }

    /// <summary>Row-major vertex positions, (Cols+1) x (Rows+1).</summary>
    public Vector2[] Positions { get; private set; } = Array.Empty<Vector2>();

    /// <summary>Arrival time per vertex, same layout as <see cref="Positions"/>.</summary>
    public float[] Arrival { get; private set; } = Array.Empty<float>();

    public Site[] Sites { get; private set; } = Array.Empty<Site>();

    public void Build(int seed, Vector2 size, in Settings s)
    {
        Size = size;
        float ss = MathF.Max(1f, MathF.Min(size.X, size.Y));
        float cell = ss / 52f;                       // ~20 px at 1080p: fine enough that contours don't show the grid
        Cols = Math.Clamp((int)MathF.Round(size.X / cell), 16, 192);
        Rows = Math.Clamp((int)MathF.Round(size.Y / cell), 16, 128);
        CellW = size.X / Cols;
        CellH = size.Y / Rows;

        int nv = (Cols + 1) * (Rows + 1);
        if (Positions.Length != nv)
        {
            Positions = new Vector2[nv];
            Arrival = new float[nv];
        }

        BuildSites(seed, size, s.Sites);

        // Each border gets its own thickness: frost never covers a screen evenly.
        float wL = 0.80f + 0.50f * DrawHelpers.Hash01(seed + 101);
        float wR = 0.80f + 0.50f * DrawHelpers.Hash01(seed + 102);
        float wT = 0.80f + 0.50f * DrawHelpers.Hash01(seed + 103);
        float wB = 0.80f + 0.50f * DrawHelpers.Hash01(seed + 104);

        float nOff = (seed & 255) * 1.7f;
        float invSs = 1f / ss;
        float invDepth = 1f / MathF.Max(0.01f, s.Depth);

        for (int r = 0; r <= Rows; r++)
        {
            float py = r * size.Y / Rows;
            for (int c = 0; c <= Cols; c++)
            {
                float px = c * size.X / Cols;
                int i = r * (Cols + 1) + c;
                Positions[i] = new Vector2(px, py);

                float dEdge = MathF.Max(0f, MathF.Min(MathF.Min(px * wL, (size.X - px) * wR), MathF.Min(py * wT, (size.Y - py) * wB))) * invSs;
                float depth = MathF.Pow(dEdge * invDepth, 1.25f) * s.DepthWeight;

                float spread = float.MaxValue;
                for (int k = 0; k < Sites.Length; k++)
                {
                    ref readonly var site = ref Sites[k];
                    float dx = px - site.Pos.X, dy = py - site.Pos.Y;
                    float d = MathF.Sqrt(dx * dx + dy * dy) * invSs;
                    spread = MathF.Min(spread, site.Delay + d * s.SpreadRate / site.Strength);
                }

                float a = spread + depth;
                float lobes = Noise.Fbm(px * invSs * 3.1f + nOff, py * invSs * 3.1f, 4);
                float crinkle = Noise.Fbm(px * invSs * 11f + 5.5f + nOff, py * invSs * 11f, 3);
                a = a * (1f + s.Warp * lobes) + 0.08f * crinkle;
                Arrival[i] = MathF.Max(0f, a);
            }
        }
    }

    /// <summary>Bilinear arrival time at an arbitrary screen position (clamped to the screen).</summary>
    public float ArrivalAt(float x, float y)
    {
        float fx = Math.Clamp(x / CellW, 0f, Cols - 0.001f);
        float fy = Math.Clamp(y / CellH, 0f, Rows - 0.001f);
        int c = (int)fx, r = (int)fy;
        float u = fx - c, v = fy - r;
        int i = r * (Cols + 1) + c, st = Cols + 1;
        float top = Arrival[i] + (Arrival[i + 1] - Arrival[i]) * u;
        float bot = Arrival[i + st] + (Arrival[i + st + 1] - Arrival[i + st]) * u;
        return top + (bot - top) * v;
    }

    private void BuildSites(int seed, Vector2 size, int count)
    {
        count = Math.Clamp(count, 1, 16);
        if (Sites.Length != count) Sites = new Site[count];

        for (int k = 0; k < count; k++)
        {
            var edge = (ScreenEdge)((int)(DrawHelpers.Hash01(seed + 200 + k * 7) * 4f) & 3);
            float t = DrawHelpers.Hash01(seed + 300 + k * 11);
            // Push positions toward the corners: frost nucleates where two cold borders meet.
            t = t < 0.5f ? 0.5f * MathF.Pow(2f * t, 1.8f) : 1f - 0.5f * MathF.Pow(2f * (1f - t), 1.8f);

            Vector2 pos = ScreenEdges.Anchor(size, edge, t, 0f);
            Vector2 inward = ScreenEdges.Inward(edge);

            float delay = k == 0 ? 0f : 0.42f * DrawHelpers.Hash01(seed + 400 + k * 13);
            float strength = 0.70f + 0.30f * DrawHelpers.Hash01(seed + 500 + k * 17);
            Sites[k] = new Site(pos, inward, delay, strength);
        }
    }
}
