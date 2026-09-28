using System;
using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// A sampled polyline skeleton for one strand, rebuilt fresh every frame by an effect's own shape
/// logic and consumed by whichever material is drawing it. Point spacing does not need to be
/// even; materials that need true even spacing sample by arc length via SampleAtArc rather than
/// assuming index position implies distance. Sized once at construction; never reallocated.
/// </summary>
public sealed class StrandPath
{
    public readonly Vector2[] Points;
    public readonly float[] Arc;

    /// <summary>How many of Points/Arc are valid this frame. Set after writing Points, before BuildArc.</summary>
    public int Count;

    public StrandPath(int capacity)
    {
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(nameof(capacity), "A path needs at least 2 points.");
        Points = new Vector2[capacity];
        Arc = new float[capacity];
    }

    public float Length => Count > 0 ? Arc[Count - 1] : 0f;

    /// <summary>
    /// Recomputes Arc[0..Count-1] from Points[0..Count-1]. Call once per frame after the strand's
    /// FINAL points are in place (i.e. after any in-place adjustment like a latch pin) and before
    /// drawing or sampling.
    /// </summary>
    public void BuildArc()
    {
        if (Count <= 0) return;
        Arc[0] = 0f;
        for (int i = 1; i < Count; i++)
            Arc[i] = Arc[i - 1] + Vector2.Distance(Points[i - 1], Points[i]);
    }

    /// <summary>
    /// Position and unit tangent at arc length <paramref name="s"/>. Values outside [0, Length]
    /// are linearly extrapolated along the nearest endpoint's tangent, so a material can place
    /// decoration slightly before the base or past the tip and have it read as the strand
    /// continuing rather than stopping dead.
    /// </summary>
    public void SampleAtArc(float s, out Vector2 pos, out Vector2 tangent)
    {
        if (Count < 2)
        {
            pos = Count == 1 ? Points[0] : default;
            tangent = new Vector2(0f, -1f);
            return;
        }

        float total = Arc[Count - 1];

        if (s <= 0f)
        {
            Vector2 d0 = Points[1] - Points[0];
            tangent = d0.LengthSquared() > 1e-5f ? Vector2.Normalize(d0) : new Vector2(0f, -1f);
            pos = Points[0] + tangent * s;
            return;
        }

        if (s >= total)
        {
            Vector2 dN = Points[Count - 1] - Points[Count - 2];
            tangent = dN.LengthSquared() > 1e-5f ? Vector2.Normalize(dN) : new Vector2(0f, -1f);
            pos = Points[Count - 1] + tangent * (s - total);
            return;
        }

        int lo = 0, hi = Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (Arc[mid] <= s) lo = mid; else hi = mid;
        }

        float segLen = Arc[hi] - Arc[lo];
        float f = segLen > 1e-5f ? (s - Arc[lo]) / segLen : 0f;
        pos = Vector2.Lerp(Points[lo], Points[hi], f);

        Vector2 d = Points[hi] - Points[lo];
        tangent = d.LengthSquared() > 1e-5f ? Vector2.Normalize(d) : new Vector2(0f, -1f);
    }
}