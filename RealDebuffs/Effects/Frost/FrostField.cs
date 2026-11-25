namespace RealDebuffs.Effects.Framework;

/// Everything RegionFrost draws for one cast (built by FrostEffect, passed via RegionPrimitive.State).
internal sealed class FrostField : IRegionState
{
    public readonly ScreenGrowth Growth = new();
    public readonly Dendrite Ferns = new();

    /// Static per-vertex mottling (0..1): frost is never an even coat.
    public float[] Mottle = [];

    /// Static per-vertex cold rim (1 at the screen border, falling smoothly to 0 inward).
    public float[] Rim = [];

    /// Static per-vertex thinning toward the middle of the glass (1 near the borders, 0 deep inside).
    public float[] Cap = [];

    /// Haze vertex colors, laid out like Growth.Positions; rebuilt only when they would change.
    public uint[] Haze = [];

    /// Overall opacity and clock time the haze was last built at (see RegionFrost).
    public float HazeAlpha, HazeTime = float.NegativeInfinity;

    // per-cast layout of the things that sit on the frost, all placed from the arrival field

    /// A static six-fold crystal on the glass.
    public readonly record struct Flower(Vector2 Pos, float Size, float Start, int Seed);

    /// A facet that catches the light now and then.
    public readonly record struct Glint(Vector2 Pos, float Size, float Start, float Phase, float Rate, float Sharp, int Seed);

    public Flower[] Flowers { get; private set; } = [];
    public Glint[]  Glints  { get; private set; } = [];

    /// Spots just behind the ice front where condensation hangs.
    public Vector2[] MistSpots { get; private set; } = [];

    // animated each frame by the effect
    public float Age;  // seconds since the cast began
    public float Progress;  // how far the ice front has come, in ScreenGrowth arrival units
    public float Fog;  // how far the condensation has come (leads Progress)
    public float Alpha;  // overall opacity including fades

    /// How far the ice front has come, in arrival units: a fast sweep, then a slow creep.
    public static float ProgressAt(float age)
    {
        float sweep = 1f - Exp(-Max(0f, age - 0.05f) / 1.5f);
        float creep = 1f - Exp(-age / 9f);
        return 0.95f * sweep + 0.30f * creep;
    }

    /// Roughly the inverse of ProgressAt: when does the front reach arrival?
    public static float AgeFor(float arrival)
    {
        float k = arrival > 0f ? Min(arrival / 0.95f, 0.93f) : 0f;  // NaN or negative -> 0
        return 0.05f - 1.5f * Log(1f - k);
    }

    public void Build(int seed, Vector2 size)
    {
        Growth.Build(seed, size, new ScreenGrowth.Settings(Depth: 0.30f, SpreadRate: 0.55f, DepthWeight: 0.75f, Warp: 0.85f, Sites: 8));

        int nv = (Growth.Cols + 1) * (Growth.Rows + 1);
        if (Mottle.Length != nv)
        {
            Mottle = new float[nv];
            Rim = new float[nv];
            Cap = new float[nv];
            Haze = new uint[nv];
        }
        HazeTime = float.NegativeInfinity;  // a rebuilt field must rebuild its haze

        float ss = Max(1f, Min(size.X, size.Y));
        float off = (seed & 255) * 2.3f;
        for (int i = 0; i < nv; i++)
        {
            var p = Growth.Positions[i];
            // Two octaves at wavelength >= ~5 cells: finer shows as blocks on a 20 px grid (the ferns carry fine detail).
            float n = Noise.Fbm(p.X / ss * 7f + off, p.Y / ss * 7f - off, 2);
            Mottle[i] = Saturate(0.5f + 0.5f * n * 1.1f);
            Cap[i] = 1f - Smooth((Growth.Arrival[i] - 0.25f) / 0.80f);

            float dEdge = Min(Min(p.X, size.X - p.X), Min(p.Y, size.Y - p.Y)) / ss;
            float k = 1f - Saturate(dEdge / 0.20f);
            Rim[i] = k * k;
        }

        BuildFerns(seed, size, ss);
        BuildDecor(seed, size, ss);
    }

    void BuildDecor(int seed, Vector2 size, float ss)
    {
        float px = ss / 1080f;

        // Crystals on the glass, spaced apart so they don't pile up.
        var flowers = new List<Flower>(24);
        for (int i = 0; i < 400 && flowers.Count < 14; i++)
        {
            var at = new Vector2(Hash01(seed + 12000 + i * 3) * size.X, Hash01(seed + 12001 + i * 3) * size.Y);
            float a = Growth.ArrivalAt(at.X, at.Y);
            if (a < 0.14f || a > 0.80f) continue;
            bool crowded = false;
            foreach (var f in flowers) if (Vector2.DistanceSquared(f.Pos, at) < Pow(150f * px, 2f)) { crowded = true; break; }
            if (crowded) continue;
            flowers.Add(new(at, (15f + 20f * Hash01(seed + 12002 + i * 3)) * px, AgeFor(a * 0.9f) + 0.25f + 0.6f * Hash01(seed + 12003 + i), seed + 12004 + i));
        }
        Flowers = flowers.ToArray();

        // Glints scattered over the frost, denser where it is thicker.
        var glints = new List<Glint>(160);
        for (int i = 0; i < 1400 && glints.Count < 150; i++)
        {
            var at = new Vector2(Hash01(seed + 13000 + i * 3) * size.X, Hash01(seed + 13001 + i * 3) * size.Y);
            float a = Growth.ArrivalAt(at.X, at.Y);
            if (a > 1.0f || Hash01(seed + 13002 + i * 3) > Pow(1.05f - a, 1.2f)) continue;
            glints.Add(new(at, (10f + 14f * Hash01(seed + 13003 + i)) * px, AgeFor(a * 0.95f) + 0.2f,
                                 Hash01(seed + 13004 + i) * 6.28f, 0.35f + 0.9f * Hash01(seed + 13005 + i),
                                 5f + 9f * Hash01(seed + 13006 + i), seed + 13007 + i));
        }
        Glints = glints.ToArray();

        // Mist hangs just inside the front, where the glass is still fogging.
        var mist = new List<Vector2>(32);
        for (int i = 0; i < 300 && mist.Count < 24; i++)
        {
            var at = new Vector2(Hash01(seed + 14000 + i * 2) * size.X, Hash01(seed + 14001 + i * 2) * size.Y);
            float a = Growth.ArrivalAt(at.X, at.Y);
            if (a >= 0.35f && a <= 0.95f) mist.Add(at);
        }
        MistSpots = mist.ToArray();
    }

    void BuildFerns(int seed, Vector2 size, float ss)
    {
        float px = ss / 1080f;
        Ferns.Clear(size);

        // Three scales of the same shape. Frost is overlapping crystals of every size, not a few big ferns.
        var big   = new Dendrite.FernStyle(StemPieces: 6, Pairs: 12, BranchAngle: 0.85f, BranchLength: 0.22f, Twigs: 2, TwigLength: 0.36f, Curl: 0.55f, Duration: 1.7f);
        var mid   = new Dendrite.FernStyle(StemPieces: 4, Pairs: 8, BranchAngle: 0.85f, BranchLength: 0.24f, Twigs: 2, TwigLength: 0.36f, Curl: 0.65f, Duration: 1.1f);
        var small = new Dendrite.FernStyle(StemPieces: 3, Pairs: 6, BranchAngle: 0.90f, BranchLength: 0.26f, Twigs: 1, TwigLength: 0.34f, Curl: 0.70f, Duration: 0.8f);

        // Big ferns: one fanning in from each nucleation point, plus some along the border.
        int n = 0;
        foreach (var site in Growth.Sites)
        {
            float baseAng = Atan2(site.Inward.Y, site.Inward.X);
            GrowFrom(site.Pos, baseAng + (Hash01(seed + 600 + n) - 0.5f) * 0.9f, seed + 700 + n * 31, big, px, ss, 1f);
            n++;
        }

        BorderRoots(seed + 1000, size, 520f * px, big, px, ss, 1f);
        BorderRoots(seed + 3000, size, 190f * px, mid, px, ss, 0.42f);

        // Small ferns nucleate wherever the ice has reached and grow away from the border, filling gaps.
        int placed = 0;
        for (int i = 0; i < 700 && placed < 60; i++)
        {
            float x = Hash01(seed + 5000 + i * 3) * size.X;
            float y = Hash01(seed + 5001 + i * 3) * size.Y;
            float a = Growth.ArrivalAt(x, y);
            if (a < 0.04f || a > 0.95f) continue;
            if (Hash01(seed + 5002 + i * 3) > Pow(1f - a, 1.1f)) continue;

            float gx = Growth.ArrivalAt(x + 8f, y) - Growth.ArrivalAt(x - 8f, y);
            float gy = Growth.ArrivalAt(x, y + 8f) - Growth.ArrivalAt(x, y - 8f);
            float ang = Atan2(gy, gx) + (Hash01(seed + 5003 + i) - 0.5f) * 2.2f;
            float len = (0.045f + 0.075f * Hash01(seed + 5004 + i)) * ss;
            Ferns.AddFern(new(x, y), ang, len, AgeFor(a * 0.85f) + 0.25f * Hash01(seed + 5005 + i), 1.3f * px, seed + 6000 + i * 41, small);
            placed++;
        }

        // Fine hairs between everything: texture, not structure.
        int needles = 0;
        for (int i = 0; i < 2400 && needles < 700; i++)
        {
            float x = Hash01(seed + 9000 + i * 3) * size.X;
            float y = Hash01(seed + 9001 + i * 3) * size.Y;
            float a = Growth.ArrivalAt(x, y);
            if (a > 1.0f || Hash01(seed + 9002 + i * 3) > Pow(1.02f - a, 1.4f)) continue;
            Ferns.AddNeedle(new(x, y), Hash01(seed + 9003 + i) * 6.28f, (4f + 9f * Hash01(seed + 9004 + i)) * px,
                            AgeFor(a * 0.9f) + 0.5f * Hash01(seed + 9005 + i), 1f * px, Hash01(seed + 9006 + i) * 6.28f);
            needles++;
        }
    }

    /// Roots spaced about spacing pixels apart along every screen edge, pointing inward.
    void BorderRoots(int seed, Vector2 size, float spacing, in Dendrite.FernStyle style, float px, float ss, float maxLenFrac)
    {
        for (int e = 0; e < 4; e++)
        {
            var edge = (ScreenEdge)e;
            float length = edge is ScreenEdge.Top or ScreenEdge.Bottom ? size.X : size.Y;
            int count = Math.Max(2, (int)Round(length / spacing));
            Vector2 inward = ScreenEdges.Inward(edge);
            float inwardAng = Atan2(inward.Y, inward.X);

            for (int i = 0; i < count; i++)
            {
                int k = e * 64 + i;
                Vector2 pos = ScreenEdges.Anchor(size, edge, (i + Hash01(seed + k)) / count, 0f);
                float jitter = (Hash01(seed + 300 + k) - 0.5f) * 0.9f;
                GrowFrom(pos, inwardAng + jitter, seed + 600 + k * 37, style, px, ss, maxLenFrac);
            }
        }
    }

    void GrowFrom(Vector2 root, float angle, int seed, in Dendrite.FernStyle style, float px, float ss, float maxLenFrac)
    {
        // Run the fern inward until it reaches the final extent of the ice, so its tips meet the front.
        var dir = Dir(angle);
        float len = 0f, max = 0.5f * ss * maxLenFrac;
        while (len < max && Growth.ArrivalAt(root.X + dir.X * len, root.Y + dir.Y * len) < 1.05f) len += 12f * px;
        len = Math.Clamp(len * 1.05f, Min(0.08f * ss, max), max);

        float start = AgeFor(Growth.ArrivalAt(root.X + dir.X * 24f * px, root.Y + dir.Y * 24f * px) * 0.8f);
        Ferns.AddFern(root, angle, len, start, 1.7f * px, seed, style);
    }
}
