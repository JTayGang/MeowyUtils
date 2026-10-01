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

public sealed class EffectScene
{
    public readonly List<StrokePrimitive>   Strokes   = new(256);
    public readonly List<ParticlePrimitive> Particles = new(2048);
    public readonly List<RegionPrimitive>   Regions   = new(64);

    public VignetteRequest Vignette;
    public DebuffKind CurrentOwner;

    public void Clear()
    {
        Strokes.Clear();
        Particles.Clear();
        Regions.Clear();
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