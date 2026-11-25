namespace RealDebuffs.Effects.Framework;

/// Screen edges numbered clockwise from the top; values are pinned (effects pick edges by number).
public enum ScreenEdge : byte
{
    Top = 0,
    Right = 1,
    Bottom = 2,
    Left = 3,
}

/// Geometry for strands anchored to screen edges, so every effect agrees on "anchor" and "inward".
public static class ScreenEdges
{
    /// Point `along` (0..1) an edge, `overhang` px outside the screen (Top/Bottom run left to right).
    public static Vector2 Anchor(Vector2 size, ScreenEdge edge, float along, float overhang) => edge switch
    {
        ScreenEdge.Top    => new Vector2(along * size.X, -overhang),
        ScreenEdge.Right  => new Vector2(size.X + overhang, along * size.Y),
        ScreenEdge.Bottom => new Vector2(along * size.X, size.Y + overhang),
        _                 => new Vector2(-overhang, along * size.Y),
    };

    /// Unit vector pointing from the edge into the screen.
    public static Vector2 Inward(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Top    => new Vector2(0f, 1f),
        ScreenEdge.Right  => new Vector2(-1f, 0f),
        ScreenEdge.Bottom => new Vector2(0f, -1f),
        _                 => new Vector2(1f, 0f),
    };

    /// Maps a perimeter position (0..4, clockwise from top-left, one unit per edge) to an anchor.
    public static Vector2 FromPerimeter(Vector2 size, float p, float overhang, out ScreenEdge edge)
    {
        p %= 4f;
        if (p < 0f) p += 4f;

        edge = (ScreenEdge)Math.Min(3, (int)p);
        float along = p - (int)p;

        // Bottom and Left run against the clockwise direction of travel.
        if (edge == ScreenEdge.Bottom || edge == ScreenEdge.Left) along = 1f - along;
        return Anchor(size, edge, along, overhang);
    }

    /// The edge nearest a point, by plain distance. Works for points outside the screen too.
    public static ScreenEdge Nearest(Vector2 size, Vector2 p)
    {
        float top = p.Y, bottom = size.Y - p.Y, left = p.X, right = size.X - p.X;
        float best = top; var edge = ScreenEdge.Top;
        if (right  < best) { best = right;  edge = ScreenEdge.Right; }
        if (bottom < best) { best = bottom; edge = ScreenEdge.Bottom; }
        if (left   < best) {                edge = ScreenEdge.Left; }
        return edge;
    }

    /// Where a strand last leaves the screen (scanned from the far end); false if it never does.
    public static bool TryFindExit(StrandPath path, Vector2 size, out Vector2 point, out Vector2 outward)
    {
        point = default;
        outward = default;

        for (int i = path.Count - 1; i >= 1; i--)
        {
            Vector2 outside = path.Points[i], inside = path.Points[i - 1];
            if (Inside(outside, size) || !Inside(inside, size)) continue;

            // Walk from the inside point toward the outside one and stop at the first violated slab.
            Vector2 d = outside - inside;
            float t = 1f;
            if (outside.X < 0f)       t = Min(t, (0f - inside.X) / d.X);
            if (outside.X > size.X)   t = Min(t, (size.X - inside.X) / d.X);
            if (outside.Y < 0f)       t = Min(t, (0f - inside.Y) / d.Y);
            if (outside.Y > size.Y)   t = Min(t, (size.Y - inside.Y) / d.Y);

            point = inside + d * Saturate(t);
            float len = d.Length();
            outward = len > 1e-4f ? d / len : Inward(Nearest(size, point)) * -1f;
            return true;
        }
        return false;
    }

    /// Mirror of TryFindExit: where a strand first comes on screen, scanning from its start.
    public static bool TryFindEntry(StrandPath path, Vector2 size, out Vector2 point, out Vector2 inward)
    {
        point = default;
        inward = default;

        for (int i = 0; i < path.Count - 1; i++)
        {
            Vector2 outside = path.Points[i], inside = path.Points[i + 1];
            if (Inside(outside, size) || !Inside(inside, size)) continue;

            Vector2 d = inside - outside;
            float t = 0f;
            if (outside.X < 0f && d.X > 0f)       t = Max(t, (0f - outside.X) / d.X);
            if (outside.X > size.X && d.X < 0f)   t = Max(t, (size.X - outside.X) / d.X);
            if (outside.Y < 0f && d.Y > 0f)       t = Max(t, (0f - outside.Y) / d.Y);
            if (outside.Y > size.Y && d.Y < 0f)   t = Max(t, (size.Y - outside.Y) / d.Y);

            point = outside + d * Saturate(t);
            float len = d.Length();
            inward = len > 1e-4f ? d / len : Inward(Nearest(size, point));
            return true;
        }
        return false;
    }

    static bool Inside(Vector2 p, Vector2 size) => p.X >= 0f && p.X <= size.X && p.Y >= 0f && p.Y <= size.Y;
}

/// Precomputed arrival times of a spreading front over a coarse screen grid; coverage at progress p is p - Arrival.
internal sealed class ScreenGrowth
{
    /// Depth (reach, fraction of short side), SpreadRate (higher = slower), DepthWeight (inward share), Warp, Sites.
    public readonly record struct Settings(float Depth, float SpreadRate, float DepthWeight, float Warp, int Sites);

    /// A nucleation point on the screen border.
    public readonly record struct Site(Vector2 Pos, Vector2 Inward, float Delay, float Strength);

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public Vector2 Size { get; private set; }
    public float CellW { get; private set; }
    public float CellH { get; private set; }

    /// Row-major vertex positions, (Cols+1) x (Rows+1).
    public Vector2[] Positions { get; private set; } = [];

    /// Arrival time per vertex, same layout as Positions.
    public float[] Arrival { get; private set; } = [];

    public Site[] Sites { get; private set; } = [];

    public void Build(int seed, Vector2 size, in Settings s)
    {
        Size = size;
        float ss = Max(1f, Min(size.X, size.Y));
        float cell = ss / 52f;  // ~20 px at 1080p: fine enough that contours don't show the grid
        Cols = Math.Clamp((int)Round(size.X / cell), 16, 192);
        Rows = Math.Clamp((int)Round(size.Y / cell), 16, 128);
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
        float wL = 0.80f + 0.50f * Hash01(seed + 101);
        float wR = 0.80f + 0.50f * Hash01(seed + 102);
        float wT = 0.80f + 0.50f * Hash01(seed + 103);
        float wB = 0.80f + 0.50f * Hash01(seed + 104);

        float nOff = (seed & 255) * 1.7f;
        float invSs = 1f / ss;
        float invDepth = 1f / Max(0.01f, s.Depth);

        for (int r = 0; r <= Rows; r++)
        {
            float py = r * size.Y / Rows;
            for (int c = 0; c <= Cols; c++)
            {
                float px = c * size.X / Cols;
                int i = r * (Cols + 1) + c;
                Positions[i] = new(px, py);

                float dEdge = Max(0f, Min(Min(px * wL, (size.X - px) * wR), Min(py * wT, (size.Y - py) * wB))) * invSs;
                float depth = Pow(dEdge * invDepth, 1.25f) * s.DepthWeight;

                float spread = float.MaxValue;
                for (int k = 0; k < Sites.Length; k++)
                {
                    ref readonly var site = ref Sites[k];
                    float dx = px - site.Pos.X, dy = py - site.Pos.Y;
                    float d = Sqrt(dx * dx + dy * dy) * invSs;
                    spread = Min(spread, site.Delay + d * s.SpreadRate / site.Strength);
                }

                float a = spread + depth;
                float lobes = Noise.Fbm(px * invSs * 3.1f + nOff, py * invSs * 3.1f, 4);
                float crinkle = Noise.Fbm(px * invSs * 11f + 5.5f + nOff, py * invSs * 11f, 3);
                a = a * (1f + s.Warp * lobes) + 0.08f * crinkle;
                Arrival[i] = Max(0f, a);
            }
        }
    }

    /// Bilinear arrival time at an arbitrary screen position (clamped to the screen).
    public float ArrivalAt(float x, float y)
    {
        float fx = Math.Clamp(x / CellW, 0f, Cols - 0.001f);
        float fy = Math.Clamp(y / CellH, 0f, Rows - 0.001f);
        int c = (int)fx, r = (int)fy;
        float u = fx - c, v = fy - r;
        int i = r * (Cols + 1) + c, st = Cols + 1;
        float top = Lerp(Arrival[i], Arrival[i + 1], u);
        float bot = Lerp(Arrival[i + st], Arrival[i + st + 1], u);
        return Lerp(top, bot, v);
    }

    void BuildSites(int seed, Vector2 size, int count)
    {
        count = Math.Clamp(count, 1, 16);
        if (Sites.Length != count) Sites = new Site[count];

        for (int k = 0; k < count; k++)
        {
            var edge = (ScreenEdge)((int)(Hash01(seed + 200 + k * 7) * 4f) & 3);
            float t = Hash01(seed + 300 + k * 11);
            // Push positions toward the corners: frost nucleates where two cold borders meet.
            t = t < 0.5f ? 0.5f * Pow(2f * t, 1.8f) : 1f - 0.5f * Pow(2f * (1f - t), 1.8f);

            Vector2 pos = ScreenEdges.Anchor(size, edge, t, 0f);
            Vector2 inward = ScreenEdges.Inward(edge);

            float delay = k == 0 ? 0f : 0.42f * Hash01(seed + 400 + k * 13);
            float strength = 0.70f + 0.30f * Hash01(seed + 500 + k * 17);
            Sites[k] = new(pos, inward, delay, strength);
        }
    }
}
