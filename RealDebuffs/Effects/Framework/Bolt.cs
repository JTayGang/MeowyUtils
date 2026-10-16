using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// The shape language of electricity, shared by every material drawn as a crackling bolt (stroke.lightning, particle.arc).
/// The effect owns a smooth strand; this turns "where along it" into a lateral kink, so any strand (a chain's path, a ring) can be shown as live lightning.
/// Pure functions of (seed, position, time): nothing is stored, so a bolt re-rolls its fine shape without ever allocating.
/// </summary>
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

    /// <summary>
    /// Linear-interpolated lattice noise at sample index k, one value per m samples (linear, so the polyline kinks exactly on lattice samples, the way lightning does).
    /// tt is time in ticks; with smooth set the lattice crossfades between ticks (a drifting shape), otherwise it snaps (a crackle).
    /// </summary>
    public static float Level(int seed, int k, int m, float tt, bool smooth)
    {
        int cell = k / m;
        float f = (k - cell * m) / (float)m;
        int t0 = (int)MathF.Floor(tt);

        float v0 = Lerp(Lattice(seed, cell, t0), Lattice(seed, cell + 1, t0), f);
        if (!smooth) return v0;

        float v1 = Lerp(Lattice(seed, cell, t0 + 1), Lattice(seed, cell + 1, t0 + 1), f);
        return Lerp(v0, v1, DrawHelpers.Smooth(tt - t0));
    }

    /// <summary>
    /// Lateral kink, in px along each sample's normal, for n samples spaced stepPx apart: a slow large-scale meander, a drifting wander, a mid kink and a fast crackle
    /// (cells of 28, 12, 4 and 2 samples). <paramref name="violence"/> 1 is a calm arc, 2 a thrashing one. <paramref name="soft"/> is the meander plus the wander alone
    /// (a smooth centreline for light to follow).
    /// </summary>
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

    /// <summary>Fills a bent twig of out.Length points leaving root along dir (unit); bend is the per-joint turn in radians (about 0.5 is a jagged fork).</summary>
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

    /// <summary>Unit vector rotated by angle (radians).</summary>
    public static Vector2 Rotate(Vector2 v, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
