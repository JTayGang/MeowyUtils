using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>A sampled polyline for one strand. Spacing is uneven; use SampleAtArc for even steps.</summary>
public sealed class StrandPath
{
    public readonly Vector2[] Points;
    public readonly float[] Arc;

    /// <summary>How many of Points/Arc are valid this frame.</summary>
    public int Count;

    public StrandPath(int capacity)
    {
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(nameof(capacity), "A path needs at least 2 points.");
        Points = new Vector2[capacity];
        Arc = new float[capacity];
    }

    public float Length => Count > 0 ? Arc[Count - 1] : 0f;

    /// <summary>Resamples a control polyline to dstCount points on a Catmull-Rom spline ending on its ends; call BuildArc after.</summary>
    public static void CatmullRomResample(Vector2[] src, int srcCount, Vector2[] dst, int dstCount)
    {
        float scale = (float)(srcCount - 1) / (dstCount - 1);

        for (int i = 0; i < dstCount; i++)
        {
            float t = i * scale;
            int seg = (int)t;
            if (seg >= srcCount - 1) { seg = srcCount - 2; t = srcCount - 1; }
            float localT = t - seg;

            Vector2 p0 = src[Math.Max(0, seg - 1)];
            Vector2 p1 = src[seg];
            Vector2 p2 = src[Math.Min(srcCount - 1, seg + 1)];
            Vector2 p3 = src[Math.Min(srcCount - 1, seg + 2)];

            float t2 = localT * localT;
            float t3 = t2 * localT;

            dst[i] = 0.5f * (
                2f * p1 +
                (p2 - p0) * localT +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }
    }

    /// <summary>Recomputes Arc from Points. Call once per frame after final points are in place.</summary>
    public void BuildArc()
    {
        if (Count <= 0) return;
        Arc[0] = 0f;
        for (int i = 1; i < Count; i++)
            Arc[i] = Arc[i - 1] + Vector2.Distance(Points[i - 1], Points[i]);
    }

    /// <summary>Position and unit tangent at arc length s; outside [0, Length] it extrapolates along the end tangent.</summary>
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
