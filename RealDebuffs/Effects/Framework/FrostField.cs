using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Everything RegionFrost needs to draw one cast of the freezing-over, built by FrostEffect and handed
/// to the material through RegionPrimitive.State. The effect owns it and updates the animated values
/// (Progress, Fog, Age, Alpha) each frame; the material only reads.
/// </summary>
internal sealed class FrostField
{
    public readonly ScreenGrowth Growth = new();
    public readonly Dendrite Ferns = new();

    /// <summary>Static per-vertex mottling (0..1): frost is never an even coat.</summary>
    public float[] Mottle = Array.Empty<float>();

    /// <summary>Static per-vertex cold rim (1 at the screen border, falling smoothly to 0 inward).</summary>
    public float[] Rim = Array.Empty<float>();

    /// <summary>Static per-vertex thinning toward the middle of the glass (1 near the borders, 0 deep inside).</summary>
    public float[] Cap = Array.Empty<float>();

    /// <summary>The haze vertex colors, same layout as Growth.Positions. Rebuilt by the material only when they would change.</summary>
    public uint[] Haze = Array.Empty<uint>();

    /// <summary>Overall opacity and clock time the haze was last built at (see RegionFrost).</summary>
    public float HazeAlpha, HazeTime = float.NegativeInfinity;

    // ---- per-cast layout of the things that sit on the frost, all placed from the arrival field ----

    /// <summary>A static six-fold crystal on the glass.</summary>
    public readonly record struct Flower(Vector2 Pos, float Size, float Start, int Seed);

    /// <summary>A facet that catches the light now and then.</summary>
    public readonly record struct Glint(Vector2 Pos, float Size, float Start, float Phase, float Rate, float Sharp, int Seed);

    public Flower[] Flowers { get; private set; } = Array.Empty<Flower>();
    public Glint[]  Glints  { get; private set; } = Array.Empty<Glint>();

    /// <summary>Spots just behind the ice front where condensation hangs.</summary>
    public Vector2[] MistSpots { get; private set; } = Array.Empty<Vector2>();

    // ---- animated each frame by the effect ----
    public float Age;        // seconds since the cast began
    public float Progress;   // how far the ice front has come, in ScreenGrowth arrival units
    public float Fog;        // how far the condensation has come (leads Progress)
    public float Alpha;      // overall opacity including fades

    /// <summary>How far the ice front has come, in arrival units: a fast sweep, then a slow creep.</summary>
    public static float ProgressAt(float age)
    {
        float sweep = 1f - MathF.Exp(-MathF.Max(0f, age - 0.05f) / 1.5f);
        float creep = 1f - MathF.Exp(-age / 9f);
        return 0.95f * sweep + 0.30f * creep;
    }

    /// <summary>Roughly the inverse of <see cref="ProgressAt"/>: when does the front reach <paramref name="arrival"/>?</summary>
    public static float AgeFor(float arrival)
    {
        float k = arrival > 0f ? MathF.Min(arrival / 0.95f, 0.93f) : 0f;       // NaN or negative -> 0
        return 0.05f - 1.5f * MathF.Log(1f - k);
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
        HazeTime = float.NegativeInfinity;                       // a rebuilt field must rebuild its haze

        float ss = MathF.Max(1f, MathF.Min(size.X, size.Y));
        float off = (seed & 255) * 2.3f;
        for (int i = 0; i < nv; i++)
        {
            var p = Growth.Positions[i];
            // Two octaves at a wavelength of at least ~5 grid cells: anything finer than that can't be
            // drawn by a 20 px grid and only shows up as blocks. The ferns carry the fine detail.
            float n = Noise.Fbm(p.X / ss * 7f + off, p.Y / ss * 7f - off, 2);
            Mottle[i] = Math.Clamp(0.5f + 0.5f * n * 1.1f, 0f, 1f);
            float t = Math.Clamp((Growth.Arrival[i] - 0.25f) / 0.80f, 0f, 1f);
            Cap[i] = 1f - t * t * (3f - 2f * t);

            float dEdge = MathF.Min(MathF.Min(p.X, size.X - p.X), MathF.Min(p.Y, size.Y - p.Y)) / ss;
            float k = 1f - Math.Clamp(dEdge / 0.20f, 0f, 1f);
            Rim[i] = k * k;
        }

        BuildFerns(seed, size, ss);
        BuildDecor(seed, size, ss);
    }

    private void BuildDecor(int seed, Vector2 size, float ss)
    {
        float px = ss / 1080f;

        // Crystals on the glass, spaced apart so they don't pile up.
        var flowers = new List<Flower>(24);
        for (int i = 0; i < 400 && flowers.Count < 14; i++)
        {
            var at = new Vector2(DrawHelpers.Hash01(seed + 12000 + i * 3) * size.X, DrawHelpers.Hash01(seed + 12001 + i * 3) * size.Y);
            float a = Growth.ArrivalAt(at.X, at.Y);
            if (a < 0.14f || a > 0.80f) continue;
            bool crowded = false;
            foreach (var f in flowers) if (Vector2.DistanceSquared(f.Pos, at) < MathF.Pow(150f * px, 2f)) { crowded = true; break; }
            if (crowded) continue;
            flowers.Add(new Flower(at, (15f + 20f * DrawHelpers.Hash01(seed + 12002 + i * 3)) * px, AgeFor(a * 0.9f) + 0.25f + 0.6f * DrawHelpers.Hash01(seed + 12003 + i), seed + 12004 + i));
        }
        Flowers = flowers.ToArray();

        // Glints scattered over the frost, denser where it is thicker.
        var glints = new List<Glint>(160);
        for (int i = 0; i < 1400 && glints.Count < 150; i++)
        {
            var at = new Vector2(DrawHelpers.Hash01(seed + 13000 + i * 3) * size.X, DrawHelpers.Hash01(seed + 13001 + i * 3) * size.Y);
            float a = Growth.ArrivalAt(at.X, at.Y);
            if (a > 1.0f || DrawHelpers.Hash01(seed + 13002 + i * 3) > MathF.Pow(1.05f - a, 1.2f)) continue;
            glints.Add(new Glint(at, (10f + 14f * DrawHelpers.Hash01(seed + 13003 + i)) * px, AgeFor(a * 0.95f) + 0.2f,
                                 DrawHelpers.Hash01(seed + 13004 + i) * 6.28f, 0.35f + 0.9f * DrawHelpers.Hash01(seed + 13005 + i),
                                 5f + 9f * DrawHelpers.Hash01(seed + 13006 + i), seed + 13007 + i));
        }
        Glints = glints.ToArray();

        // Mist hangs just inside the front, where the glass is still fogging.
        var mist = new List<Vector2>(32);
        for (int i = 0; i < 300 && mist.Count < 24; i++)
        {
            var at = new Vector2(DrawHelpers.Hash01(seed + 14000 + i * 2) * size.X, DrawHelpers.Hash01(seed + 14001 + i * 2) * size.Y);
            float a = Growth.ArrivalAt(at.X, at.Y);
            if (a >= 0.35f && a <= 0.95f) mist.Add(at);
        }
        MistSpots = mist.ToArray();
    }

    private void BuildFerns(int seed, Vector2 size, float ss)
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
            float baseAng = MathF.Atan2(site.Inward.Y, site.Inward.X);
            GrowFrom(site.Pos, baseAng + (DrawHelpers.Hash01(seed + 600 + n) - 0.5f) * 0.9f, seed + 700 + n * 31, big, px, ss, 1f);
            n++;
        }

        BorderRoots(seed + 1000, size, 520f * px, big, px, ss, 1f);
        BorderRoots(seed + 3000, size, 190f * px, mid, px, ss, 0.42f);

        // Small ferns nucleate anywhere the ice has reached and grow away from the border, which is how
        // the gaps between the big ferns fill in.
        int placed = 0;
        for (int i = 0; i < 700 && placed < 60; i++)
        {
            float x = DrawHelpers.Hash01(seed + 5000 + i * 3) * size.X;
            float y = DrawHelpers.Hash01(seed + 5001 + i * 3) * size.Y;
            float a = Growth.ArrivalAt(x, y);
            if (a < 0.04f || a > 0.95f) continue;
            if (DrawHelpers.Hash01(seed + 5002 + i * 3) > MathF.Pow(1f - a, 1.1f)) continue;

            float gx = Growth.ArrivalAt(x + 8f, y) - Growth.ArrivalAt(x - 8f, y);
            float gy = Growth.ArrivalAt(x, y + 8f) - Growth.ArrivalAt(x, y - 8f);
            float ang = MathF.Atan2(gy, gx) + (DrawHelpers.Hash01(seed + 5003 + i) - 0.5f) * 2.2f;
            float len = (0.045f + 0.075f * DrawHelpers.Hash01(seed + 5004 + i)) * ss;
            Ferns.AddFern(new Vector2(x, y), ang, len, AgeFor(a * 0.85f) + 0.25f * DrawHelpers.Hash01(seed + 5005 + i), 1.3f * px, seed + 6000 + i * 41, small);
            placed++;
        }

        // Fine hairs between everything: texture, not structure.
        int needles = 0;
        for (int i = 0; i < 2400 && needles < 700; i++)
        {
            float x = DrawHelpers.Hash01(seed + 9000 + i * 3) * size.X;
            float y = DrawHelpers.Hash01(seed + 9001 + i * 3) * size.Y;
            float a = Growth.ArrivalAt(x, y);
            if (a > 1.0f || DrawHelpers.Hash01(seed + 9002 + i * 3) > MathF.Pow(1.02f - a, 1.4f)) continue;
            Ferns.AddNeedle(new Vector2(x, y), DrawHelpers.Hash01(seed + 9003 + i) * 6.28f, (4f + 9f * DrawHelpers.Hash01(seed + 9004 + i)) * px,
                            AgeFor(a * 0.9f) + 0.5f * DrawHelpers.Hash01(seed + 9005 + i), 1f * px, DrawHelpers.Hash01(seed + 9006 + i) * 6.28f);
            needles++;
        }
    }

    /// <summary>Roots spaced about <paramref name="spacing"/> pixels apart along every screen edge, pointing inward.</summary>
    private void BorderRoots(int seed, Vector2 size, float spacing, in Dendrite.FernStyle style, float px, float ss, float maxLenFrac)
    {
        for (int e = 0; e < 4; e++)
        {
            var edge = (ScreenEdge)e;
            float length = edge is ScreenEdge.Top or ScreenEdge.Bottom ? size.X : size.Y;
            int count = Math.Max(2, (int)MathF.Round(length / spacing));
            Vector2 inward = ScreenEdges.Inward(edge);
            float inwardAng = MathF.Atan2(inward.Y, inward.X);

            for (int i = 0; i < count; i++)
            {
                int k = e * 64 + i;
                Vector2 pos = ScreenEdges.Anchor(size, edge, (i + DrawHelpers.Hash01(seed + k)) / count, 0f);
                float jitter = (DrawHelpers.Hash01(seed + 300 + k) - 0.5f) * 0.9f;
                GrowFrom(pos, inwardAng + jitter, seed + 600 + k * 37, style, px, ss, maxLenFrac);
            }
        }
    }

    private void GrowFrom(Vector2 root, float angle, int seed, in Dendrite.FernStyle style, float px, float ss, float maxLenFrac)
    {
        // Run the fern inward until it reaches the final extent of the ice, so its tips meet the front.
        var dir = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        float len = 0f, max = 0.5f * ss * maxLenFrac;
        while (len < max && Growth.ArrivalAt(root.X + dir.X * len, root.Y + dir.Y * len) < 1.05f) len += 12f * px;
        len = Math.Clamp(len * 1.05f, MathF.Min(0.08f * ss, max), max);

        float start = AgeFor(Growth.ArrivalAt(root.X + dir.X * 24f * px, root.Y + dir.Y * 24f * px) * 0.8f);
        Ferns.AddFern(root, angle, len, start, 1.7f * px, seed, style);
    }
}
