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

    // Decorative
    Ring,
    Flare,
    Tip,
    Node,
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
    public DebuffKind Owner;         // stamped by EffectScene
    public Vector2 Position;
    public Vector2 Velocity;
    public Vector4? ColorOverride;
    public float AgeRatio;
    public float Size;
    public float Brightness;
    public float Sway;
    public int   Seed;

    public PrimitiveRole Role;

    public int    GlyphIndex;
    public string? Text;
    public float  Rotation;

    public bool  FollowsStroke;
    public float PathT;
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
}