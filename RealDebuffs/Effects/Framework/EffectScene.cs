using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// The numeric values are pinned: StrokeAutoEmitter mixes them into per-particle seeds, so
/// renumbering would reshuffle the random scatter. Add new roles with a new, unused number.
/// </summary>
public enum PrimitiveRole
{
    MainStroke = 0,

    Spark = 3,
    Drip = 4,
    Flow = 5,
    Ember = 7,
    Snowflake = 10,
    Snow = 11,
    Fog = 12,
    IceCrystal = 13,
    Smoke = 18,
    Cinder = 19,
    Dust = 20,
    Flake = 21,
}

public struct StrokePrimitive
{
    public DebuffKind Owner;
    public StrandPath Path;
    public PrimitiveRole Role;
    public Vector4? ColorOverride;
    public float Reveal;
    public float WidthHint;
    public float Brightness;
    public float TipFlare;
    public int   Seed;
    public float Phase;
    public bool  FlushStart;

    // ---- Optional, material-agnostic hints. Zero is always "neutral", so an effect that never ----
    // ---- sets these draws exactly as it did before they existed.                             ----

    /// <summary>
    /// The path is a closed loop (last point coincides with the first). Materials that repeat an
    /// element along the path (chain links, beads, rune glyphs) close the seam so the pattern wraps
    /// cleanly instead of ending in a half-element. This is what lets a ring-shaped stroke, such as
    /// a magic circle, be "made of chains".
    /// </summary>
    public bool  Closed;

    /// <summary>
    /// 0 = at the focal plane (crisp, full contrast), 1 = far away (hazier, lower contrast, softer
    /// edges, shorter shadow). Lets an effect layer several strands into a believable depth stack
    /// by setting one number, and every material that cares can respond in its own way.
    /// </summary>
    public float Depth;

    /// <summary>
    /// 0..1: how hard the strand is being shaken right now (decays after an impact or a tug).
    /// Materials use it for transient energy: specular flare, link rattle, shed rate.
    /// </summary>
    public float Agitation;
}

public struct ParticlePrimitive
{
    public DebuffKind Owner;
    public Vector2 Position;
    public Vector2 Velocity;
    public Vector4? ColorOverride;
    public float AgeRatio;
    public float Size;
    public float Brightness;
    public float Sway;
    public int   Seed;

    public PrimitiveRole Role;

    /// <summary>Forces this particle to render with a specific material name, bypassing role lookup.</summary>
    public string? MaterialName;

    /// <summary>Material-defined sub-kind. particle.ember uses it for flame layers (0 body, 1 back, 2 front, 3 bed).</summary>
    public int Variant;
}

public struct RegionPrimitive
{
    public DebuffKind Owner;
    public Vector2 Min, Max;
    public Vector4? ColorOverride;
    public uint Tint;
    public float Alpha;
    public PrimitiveRole Role;
    public bool Top, Bottom, Left, Right;

    public readonly bool HasEdge => Top || Bottom || Left || Right;
}

/// <summary>
/// "Something just hit something" - a one-frame event, not something that is drawn. An effect says
/// WHERE and HOW HARD; the framework asks the owner's stroke material (whatever it currently is,
/// after any user or tooltip override) what such an impact throws, so a chain shows sparks and
/// rust, a rope shows dust and fibres, and the effect never has to know which it is.
/// </summary>
public struct ImpactPrimitive
{
    public DebuffKind Owner;

    /// <summary>The owner's stroke role whose material should answer (almost always MainStroke).</summary>
    public PrimitiveRole StrokeRole;

    public Vector2 Position;

    /// <summary>Unit vector debris is thrown along (typically away from the surface that was hit).</summary>
    public Vector2 Direction;

    /// <summary>0..1. Scales how much is thrown, and how hard.</summary>
    public float Strength;

    public float Brightness;
    public int   Seed;
    public Vector4? ColorOverride;
}

public sealed class EffectScene
{
    public readonly List<StrokePrimitive>   Strokes   = new(256);
    public readonly List<ParticlePrimitive> Particles = new(2048);
    public readonly List<RegionPrimitive>   Regions   = new(64);
    public readonly List<ImpactPrimitive>   Impacts   = new(8);

    public VignetteRequest Vignette;
    public DebuffKind CurrentOwner;

    public void Clear()
    {
        Strokes.Clear();
        Particles.Clear();
        Regions.Clear();
        Impacts.Clear();
        Vignette = default;
        CurrentOwner = default;
    }

    public void AddStroke(in StrokePrimitive s)
    {
        var copy = s; copy.Owner = CurrentOwner; Strokes.Add(copy);
    }

    public void AddParticle(in ParticlePrimitive p)
    {
        var copy = p; copy.Owner = CurrentOwner; Particles.Add(copy);
    }

    public void AddRegion(in RegionPrimitive r)
    {
        var copy = r; copy.Owner = CurrentOwner; Regions.Add(copy);
    }

    public void AddImpact(in ImpactPrimitive i)
    {
        var copy = i; copy.Owner = CurrentOwner; Impacts.Add(copy);
    }

    /// <summary>For post-effect emission passes where CurrentOwner isn't the right effect anymore.</summary>
    public void AddParticleForOwner(in ParticlePrimitive p, DebuffKind owner)
    {
        var copy = p; copy.Owner = owner; Particles.Add(copy);
    }

    public void RequestVignette(uint tint, float thicknessFrac, float alpha, int priority, Vector4? colorOverride)
    {
        if (!Vignette.Active || priority > Vignette.Priority ||
            (priority == Vignette.Priority && alpha > Vignette.Alpha))
        {
            Vignette = new VignetteRequest
            {
                Active = true, Tint = tint, ThicknessFrac = thicknessFrac,
                Alpha = alpha, Priority = priority, ColorOverride = colorOverride,
            };
        }
    }
}

public struct VignetteRequest
{
    public bool  Active;
    public uint  Tint;
    public float ThicknessFrac;
    public float Alpha;
    public int   Priority;
    public Vector4? ColorOverride;
}

/// <summary>
/// A sampled polyline skeleton for one strand. Point spacing need not be even; materials that
/// need even spacing sample via SampleAtArc. Sized once at construction.
/// </summary>
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

    /// <summary>
    /// Resamples a coarse control polyline into <paramref name="dstCount"/> evenly parameterised
    /// points along a Catmull-Rom spline. The end points are duplicated as phantom outer control
    /// points so the curve terminates exactly on them. Does not touch Count or the arc table; call
    /// BuildArc afterwards.
    /// </summary>
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

    /// <summary>
    /// Position and unit tangent at arc length s. Values outside [0, Length] are linearly
    /// extrapolated along the nearest endpoint's tangent.
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