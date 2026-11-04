using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>The shape language of electricity, shared by stroke.lightning and particle.arc.</summary>
internal static class Bolt
{
    /// <summary>One random lattice value in -1..1, fixed for a given (seed, cell, tick).</summary>
    public static float Lattice(int seed, int cell, int tick)
    {
        unchecked
        {
            return DrawHelpers.Hash01(seed * 747796405 + cell * 1597334677 + tick * (int)0x9E3779B1 + 0x2545F491) * 2f - 1f;
        }
    }

    /// <summary>Linear-interpolated lattice noise at sample index k</summary>
    public static float Level(int seed, int k, int m, float tt, bool smooth)
    {
        int cell = k / m;
        float f = (k - cell * m) / (float)m;
        int t0 = (int)MathF.Floor(tt);

        float v0 = DrawHelpers.Lerp(Lattice(seed, cell, t0), Lattice(seed, cell + 1, t0), f);
        if (!smooth) return v0;

        float v1 = DrawHelpers.Lerp(Lattice(seed, cell, t0 + 1), Lattice(seed, cell + 1, t0 + 1), f);
        return DrawHelpers.Lerp(v0, v1, DrawHelpers.Smooth(tt - t0));
    }

    /// <summary>Lateral kink in px along each sample's normal: a slow meander plus a drifting wander.</summary>
    public static void Crackle(Span<float> hard, Span<float> soft, int n, float stepPx, int seed, float time, float violence)
    {
        float a_1 = 2.0f * stepPx * (0.7f + 0.3f * violence);
        float a0 = 1.2f * stepPx * violence;
        float a1 = 0.95f * stepPx * violence;
        float a2 = 0.50f * stepPx * violence;

        // Tick clocks are offset per seed so a crowd of bolts never re-rolls in step.
        float ph = DrawHelpers.Hash01(seed + 11) * 50f;
        float tm = time * 0.7f + ph * 0.6f;
        float t0 = time * 2.2f + ph;
        float t1 = time * 7.0f + ph * 1.7f;
        float t2 = time * (17f + 9f * MathF.Max(0f, violence - 1f)) + ph * 2.3f;

        for (int k = 0; k < n; k++)
        {
            float w = a_1 * Level(seed + 53, k, 28, tm, true) + a0 * Level(seed, k, 12, t0, true);
            soft[k] = w;
            hard[k] = w + a1 * Level(seed + 101, k, 4, t1, true) + a2 * Level(seed + 211, k, 2, t2, false);
        }
    }

    /// <summary>Fills a bent twig of out.Length points leaving root along dir (unit)</summary>
    public static void Twig(Span<Vector2> outPts, Vector2 root, Vector2 dir, float length, int seed, int tick, float bend)
    {
        int n = outPts.Length;
        outPts[0] = root;
        float seg = length / MathF.Max(1, n - 1);
        float ang = 0f;
        for (int j = 1; j < n; j++)
        {
            ang += Lattice(seed, j, tick) * bend;
            float c = MathF.Cos(ang), s = MathF.Sin(ang);
            Vector2 d = new(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            outPts[j] = outPts[j - 1] + d * (seg * (0.75f + 0.5f * DrawHelpers.Hash01(seed + j * 53 + tick)));
        }
    }
}
