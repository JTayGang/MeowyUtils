using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// The numeric values are pinned: StrokeAutoEmitter mixes them into its per-particle seeds, so
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
    public DebuffKind Owner;         // stamped by EffectScene; do not set in effects
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

    /// <summary>
    /// Forces this particle to render with a specific material name, bypassing role lookup.
    /// Set by StrokeAutoEmitter when a user override specified "emit particle X" so the spawned
    /// particles carry their emitter's material with them. Null = resolve via role.
    /// </summary>
    public string? MaterialName;

    /// <summary>
    /// Material-defined sub-kind, 0 by default. A material that renders several related looks from
    /// one role reads this instead of guessing from Size (particle.ember uses it for flame layers:
    /// 0 = body, 1 = back/cool, 2 = front/hot, 3 = low wide bed). Materials that don't care ignore it.
    /// </summary>
    public int Variant;
}

public struct RegionPrimitive
{
    public DebuffKind Owner;         // stamped by EffectScene
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

    /// <summary>
    /// For post-effect emission passes (see StrokeAutoEmitter) where CurrentOwner isn't set to
    /// the right effect anymore. Caller supplies the owner explicitly.
    /// </summary>
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
