using System;
using System.Numerics;

namespace RealDebuffs.Effects;

/// <summary>
/// A sampled polyline "skeleton" for one strand (a tendril, a chain link chain, or any future
/// path-based material), rebuilt fresh every frame by an effect's own shape logic - BindEffect's
/// curl-and-wave heading integration, HeavyEffect's sagging bezier - and consumed by whichever
/// IStrandSkin is currently drawing it. This is the "path" the reskinning system is built around:
/// every strand-based effect boils down to "some points, in order, plus how far along them we are",
/// and everything about how that line actually LOOKS on screen lives in the skin instead - see
/// IStrandSkin's remarks for the other half of that split.
///
/// Point spacing does not need to be even. A skin that needs true even spacing along the curve
/// (ChainSkin, placing discrete links) samples by arc length via <see cref="SampleAtArc"/> rather
/// than assuming index position implies distance - this is exactly what lets the same path work
/// for both a hand-integrated tendril curve (already close to evenly spaced) and a quadratic-bezier
/// chain (which visibly is not, especially where the sag bunches samples together).
///
/// Sized once at construction and reused every frame - Points/Arc are never reallocated - matching
/// every other per-frame scratch buffer already in this folder.
/// </summary>
public sealed class StrandPath
{
    public readonly Vector2[] Points;
    public readonly float[] Arc;

    /// <summary>How many of Points/Arc are valid this frame (always &lt;= Points.Length). Set this after writing Points, before calling <see cref="BuildArc"/>.</summary>
    public int Count;

    public StrandPath(int capacity)
    {
        if (capacity < 2)
            throw new ArgumentOutOfRangeException(nameof(capacity), "A path needs at least 2 points.");
        Points = new Vector2[capacity];
        Arc = new float[capacity];
    }

    /// <summary>Total arc length of the currently-valid points. Only meaningful after <see cref="BuildArc"/>.</summary>
    public float Length => Count > 0 ? Arc[Count - 1] : 0f;

    /// <summary>
    /// Recomputes Arc[0..Count-1] from Points[0..Count-1]. Call once per frame after this strand's
    /// FINAL points are in place (i.e. after any in-place adjustment like a latch pin) and before
    /// drawing or sampling - an arc table built before such an adjustment would describe a path
    /// that's no longer the one actually being drawn.
    /// </summary>
    public void BuildArc()
    {
        if (Count <= 0) return;
        Arc[0] = 0f;
        for (int i = 1; i < Count; i++)
            Arc[i] = Arc[i - 1] + Vector2.Distance(Points[i - 1], Points[i]);
    }

    /// <summary>
    /// Position and unit tangent at arc length <paramref name="s"/> along the path. Values outside
    /// [0, Length] are linearly extrapolated along the nearest endpoint's tangent, so a skin can
    /// place decoration slightly before the base or past the tip and have it read as the strand
    /// continuing rather than stopping dead - e.g. ChainSkin's off-screen-continuing links.
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
