using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>Screen edges numbered clockwise from the top. Values are pinned (effects pick edges by number; FromPerimeter maps onto it).</summary>
public enum ScreenEdge : byte
{
    Top = 0,
    Right = 1,
    Bottom = 2,
    Left = 3,
}

/// <summary>Geometry for strands anchored to screen edges, so every effect agrees on "anchor" and "inward".</summary>
public static class ScreenEdges
{
    /// <summary>Point `along` (0..1) an edge, pushed `overhang` px outside the screen. Top/Bottom run left to right; Right/Left top to bottom.</summary>
    public static Vector2 Anchor(Vector2 size, ScreenEdge edge, float along, float overhang) => edge switch
    {
        ScreenEdge.Top    => new Vector2(along * size.X, -overhang),
        ScreenEdge.Right  => new Vector2(size.X + overhang, along * size.Y),
        ScreenEdge.Bottom => new Vector2(along * size.X, size.Y + overhang),
        _                 => new Vector2(-overhang, along * size.Y),
    };

    /// <summary>Unit vector pointing from the edge into the screen.</summary>
    public static Vector2 Inward(ScreenEdge edge) => edge switch
    {
        ScreenEdge.Top    => new Vector2(0f, 1f),
        ScreenEdge.Right  => new Vector2(-1f, 0f),
        ScreenEdge.Bottom => new Vector2(0f, -1f),
        _                 => new Vector2(1f, 0f),
    };

    /// <summary>Maps a perimeter position (0..4, wrapping, clockwise from top-left, one unit per edge) to an anchor with the same overhang as Anchor.</summary>
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

    /// <summary>The edge nearest a point, by plain distance. Works for points outside the screen too.</summary>
    public static ScreenEdge Nearest(Vector2 size, Vector2 p)
    {
        float top = p.Y, bottom = size.Y - p.Y, left = p.X, right = size.X - p.X;
        float best = top; var edge = ScreenEdge.Top;
        if (right  < best) { best = right;  edge = ScreenEdge.Right; }
        if (bottom < best) { best = bottom; edge = ScreenEdge.Bottom; }
        if (left   < best) {                edge = ScreenEdge.Left; }
        return edge;
    }

    /// <summary>Where a strand last crosses the screen boundary leaving it (scanned from the far end); false if it never does.</summary>
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
            if (outside.X < 0f)       t = MathF.Min(t, (0f - inside.X) / d.X);
            if (outside.X > size.X)   t = MathF.Min(t, (size.X - inside.X) / d.X);
            if (outside.Y < 0f)       t = MathF.Min(t, (0f - inside.Y) / d.Y);
            if (outside.Y > size.Y)   t = MathF.Min(t, (size.Y - inside.Y) / d.Y);

            point = inside + d * Math.Clamp(t, 0f, 1f);
            float len = d.Length();
            outward = len > 1e-4f ? d / len : Inward(Nearest(size, point)) * -1f;
            return true;
        }
        return false;
    }

    /// <summary>Mirror of TryFindExit: where a strand first comes on screen, scanning from its start.</summary>
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
            if (outside.X < 0f && d.X > 0f)       t = MathF.Max(t, (0f - outside.X) / d.X);
            if (outside.X > size.X && d.X < 0f)   t = MathF.Max(t, (size.X - outside.X) / d.X);
            if (outside.Y < 0f && d.Y > 0f)       t = MathF.Max(t, (0f - outside.Y) / d.Y);
            if (outside.Y > size.Y && d.Y < 0f)   t = MathF.Max(t, (size.Y - outside.Y) / d.Y);

            point = outside + d * Math.Clamp(t, 0f, 1f);
            float len = d.Length();
            inward = len > 1e-4f ? d / len : Inward(Nearest(size, point));
            return true;
        }
        return false;
    }

    private static bool Inside(Vector2 p, Vector2 size) => p.X >= 0f && p.X <= size.X && p.Y >= 0f && p.Y <= size.Y;
}

/// <summary>Precomputed arrival times of a spreading front over a coarse screen grid; coverage at progress p is p - Arrival (no simulation).</summary>
internal sealed class ScreenGrowth
{
    /// <summary>Depth (front reach, fraction of short side), SpreadRate (higher = slower), DepthWeight (inward share), Warp (0 smooth), Sites.</summary>
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

    /// <summary>Arrival time per vertex, same layout as Positions.</summary>
    public float[] Arrival { get; private set; } = Array.Empty<float>();

    public Site[] Sites { get; private set; } = Array.Empty<Site>();

    public void Build(int seed, Vector2 size, in Settings s)
    {
        Size = size;
        float ss = MathF.Max(1f, MathF.Min(size.X, size.Y));
        float cell = ss / 52f;  // ~20 px at 1080p: fine enough that contours don't show the grid
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

/// <summary>A fixed pool of wrapping flakes blown by gusting wind: steady density, constant cost, no allocation after Build.</summary>
internal sealed class WindField
{
    private struct Flake
    {
        public Vector2 Pos, Vel;
        public float Depth;  // 0 far .. 1 near
        public float Phase;
        public int Seed;
    }

    private Flake[] _flakes = Array.Empty<Flake>();

    /// <summary>Lays out count flakes across the screen. Reuses the array when the count is unchanged.</summary>
    public void Build(int count, int seed, Vector2 size)
    {
        count = Math.Max(0, count);
        if (_flakes.Length != count) _flakes = new Flake[count];

        for (int i = 0; i < count; i++)
        {
            int s = seed + i * 7919;
            _flakes[i] = new Flake
            {
                Pos = new Vector2(DrawHelpers.Hash01(s + 2) * size.X, DrawHelpers.Hash01(s + 3) * size.Y),
                Depth = MathF.Pow(DrawHelpers.Hash01(s + 1), 1.7f),  // far flakes outnumber near ones
                Phase = DrawHelpers.Hash01(s + 4) * MathF.Tau,
                Seed = s,
            };
        }
    }

    /// <summary>Gust strength 0..1 (three incommensurate waves, squashed into lulls and gales).</summary>
    public static float Gust(float time, float seed)
    {
        float g = 0.62f
                + 0.26f * MathF.Sin(time * 0.29f + seed)
                + 0.17f * MathF.Sin(time * 0.71f + seed * 1.9f)
                + 0.09f * MathF.Sin(time * 1.70f + seed * 3.7f);
        return DrawHelpers.Smooth(g);
    }

    /// <summary>Advances every flake; wind is px/s at full gust (screen-scaled), gust 0..1, px scales settling and eddy motion.</summary>
    public void Update(float time, float dt, Vector2 size, float px, Vector2 wind, float gust)
    {
        if (_flakes.Length == 0 || dt <= 0f) return;

        float margin = 48f * px;
        float w = size.X + 2f * margin, h = size.Y + 2f * margin;
        Vector2 drive = wind * (0.30f + 0.70f * gust);  // a lull still drifts

        for (int i = 0; i < _flakes.Length; i++)
        {
            ref Flake f = ref _flakes[i];
            float d = f.Depth;
            float flutter = MathF.Sin(time * (1.3f + 1.4f * d) + f.Phase);

            Vector2 v = drive * (0.40f + 1.00f * d) * (1f + 0.10f * flutter);  // parallax: near is fast
            v.Y += (18f + 46f * d) * px * (1f + 0.5f * flutter);  // settling, with a flutter
            v.Y += MathF.Sin(time * 2.1f + f.Phase * 3.1f) * (26f + 70f * d) * px * gust;  // eddies in a gust

            f.Vel = v;
            f.Pos += v * dt;
            f.Pos.X = Wrap(f.Pos.X + margin, w) - margin;
            f.Pos.Y = Wrap(f.Pos.Y + margin, h) - margin;
        }
    }

    /// <summary>Pushes flakes into the scene; density (0..1) is the fraction out, the last fading in. Off-screen flakes are skipped.</summary>
    public void Emit(EffectScene scene, Vector2 size, float px, PrimitiveRole role, float density,
                     float brightness, Vector4? colorOverride)
    {
        float active = Math.Clamp(density, 0f, 1f) * _flakes.Length;
        int n = Math.Min(_flakes.Length, (int)MathF.Ceiling(active));
        float cull = 40f * px;

        for (int i = 0; i < n; i++)
        {
            ref readonly Flake f = ref _flakes[i];
            if (f.Pos.X < -cull || f.Pos.X > size.X + cull || f.Pos.Y < -cull || f.Pos.Y > size.Y + cull) continue;

            float d = f.Depth;
            float on = Math.Clamp(active - i, 0f, 1f);
            float flakeSize = (1.3f + 5.2f * d) * (0.85f + 0.30f * DrawHelpers.Hash01(f.Seed + 5)) * px;

            scene.AddParticle(new ParticlePrimitive
            {
                Position = f.Pos,
                Velocity = f.Vel,
                AgeRatio = 0.5f,  // the steady middle of a particle's life: full size
                Size = flakeSize,
                Brightness = brightness * on * (0.60f + 0.40f * d),
                Seed = f.Seed,
                Role = role,
                ColorOverride = colorOverride,
            });
        }
    }

    private static float Wrap(float v, float len)
    {
        v %= len;
        return v < 0f ? v + len : v;
    }
}
