using System.Numerics;

namespace RealDebuffs.Effects.Framework;

public enum PrimitiveRole
{
    // Strokes
    MainStroke,
    BranchStroke,
    DetailStroke,

    // Particles
    Spark,
    Drip,
    Flow,
    Mote,
    Ember,
    Rune,
    Word,
    Snowflake,
    Snow,
    Fog,
    IceCrystal,

    // Decorative
    Ring,
    Flare,
    Tip,
    Node,

    // Added later (appended so existing numeric values never shift).
    Smoke,
    Cinder,
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