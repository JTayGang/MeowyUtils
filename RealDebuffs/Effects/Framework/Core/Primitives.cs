using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>Slot key for strokes and particles; values are pinned (they seed particle randomness), so give a new role an unused number. [Fallback] names the particle drawn when a slot has no material.</summary>
public enum PrimitiveRole
{
    MainStroke = 0,

    Spark = 3,
    [Fallback("particle.ember")] Ember = 7,
    [Fallback("particle.snowflake")] Snowflake = 10,
    [Fallback("particle.snow")] Snow = 11,
    [Fallback("particle.smoke")] Smoke = 18,
    [Fallback("particle.cinder")] Cinder = 19,
    [Fallback("particle.dust")] Dust = 20,
    [Fallback("particle.flake")] Flake = 21,
    [Fallback("particle.fibre")] Fibre = 22,
    [Fallback("particle.glint")] Glint = 23,
    [Fallback("particle.mist")] Mist = 24,
    [Fallback("particle.slime")] Goop = 25,
    [Fallback("particle.rune")] Rune = 26,
    Inscription = 27,
    Halo = 28,
    [Fallback("particle.mote")] Mote = 29,
    [Fallback("particle.miasma")] Miasma = 30,
    [Fallback("particle.arc")] Arc = 31,
    [Fallback("particle.thunderhead")] Haze = 32,
    [Fallback("particle.flare")] Flash = 33,
    Edge = 34,
    [Fallback("particle.candle")] Candle = 35,
    Horn = 36,
    [Fallback("particle.eye")] Eye = 37,
    Trail = 38,
    [Fallback("particle.whisper")] Whisper = 39,
    [Fallback("particle.shard")] Shard = 40,
}

[AttributeUsage(AttributeTargets.Field)]
public sealed class FallbackAttribute(string material) : Attribute
{
    public string Material { get; } = material;
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

    // optional hints; zero is neutral

    /// <summary>The path is a closed loop (last point == first), so repeating elements (links, beads) wrap cleanly.</summary>
    public bool  Closed;

    /// <summary>0 = at the focal plane (crisp, full contrast); 1 = far (hazier, lower contrast, softer edges, shorter shadow).</summary>
    public float Depth;

    /// <summary>0..1: how hard the strand is being shaken right now (decays after an impact or a tug).</summary>
    public float Agitation;

    /// <summary>0 = shed along the whole strand; above 0, trickles only spawn within this many px (at 1080p) of a screen edge.</summary>
    public float EmitEdgeReach;

    /// <summary>0 = unrestricted; above 0, trickles only spawn within this arc distance (px at 1080p) of either end.</summary>
    public float EmitEndReach;

    /// <summary>Rotation about the strand's axis at its middle, radians (zero at anchored ends).</summary>
    public float Twist;

    /// <summary>0 = solid; above 0, the line is dashed, this many px (at 1080p) from the start of one dash to the next.</summary>
    public float Dash;

    /// <summary>Where a bolt of current is racing along the strand: 0 at the start, 1 at the end (needs SurgeGain above 0).</summary>
    public float SurgeAt;

    /// <summary>How brightly that surge lights the strand: 0 none, ~1 a clear run, 2+ blinding.</summary>
    public float SurgeGain;
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

    /// <summary>Position with the horizontal sway applied: where the particle is actually drawn.</summary>
    public readonly Vector2 DrawPos => new(Position.X + Sway, Position.Y);

    /// <summary>Which way the particle's own "up" points, in radians clockwise from screen-up.</summary>
    public float Rotation;

    /// <summary>Seconds since it came loose, for materials that animate a free particle's shape (Goop's Free variant).</summary>
    public float Age;

    // Optional thread. Zero is neutral, so a particle that never sets these draws as it always did.

    /// <summary>Filament thickness joining the particle to a point it is attached to</summary>
    public float Tether;

    /// <summary>The thread's fixed end in screen px, where it clings to the surface (read only when Tether is above 0).</summary>
    public Vector2 Anchor;

    /// <summary>The thread's free end: equals Position while attached</summary>
    public Vector2 ThreadEnd;

    /// <summary>Set when the particle belongs to a stroke (identified by owner and HostSeed).</summary>
    public bool HasHost;
    public int  HostSeed;
}

public struct RegionPrimitive
{
    public DebuffKind Owner;
    public Vector2 Min, Max;
    public Vector4? ColorOverride;
    public uint Tint;
    public float Alpha;
    public bool Top, Bottom, Left, Right;

    /// <summary>Optional effect-built data too large for a primitive; materials ignore state they don't recognise.</summary>
    public IRegionState? State;

    public readonly bool HasEdge => Top || Bottom || Left || Right;

    public readonly RegionKind Kind => HasEdge ? RegionKind.EdgeGlow : RegionKind.FlatFill;
}

/// <summary>A one-frame impact: the effect says where and how hard; the stroke material decides what it throws.</summary>
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

public struct VignetteRequest
{
    public bool  Active;
    public uint  Tint;
    public float ThicknessFrac;
    public float Alpha;
    public int   Priority;
    public Vector4? ColorOverride;
}
