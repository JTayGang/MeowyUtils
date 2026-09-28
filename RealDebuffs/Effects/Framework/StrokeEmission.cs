using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// One thing a stroke material sheds along its length, or (for a particle material used as a
/// stroke emitter) how that particle looks when spawned along a stroke.
///
/// Launch direction: if PrimaryDirection is set, particles launch in that direction with a cone
/// spread, then get BiasVelocity added on top. If PrimaryDirection is null (the default),
/// particles launch perpendicular to the strand — right for sparks flying off a struck chain, or
/// anything else that reads as "given off by" the strand in a direction that depends on its
/// local orientation.
///
/// Fire, snow, mist, and other things that should move in a *world* direction regardless of
/// which way the strand happens to point use PrimaryDirection. A vertical chain shedding fire
/// still has its fire rise; without PrimaryDirection, that fire would shoot sideways.
///
/// Density is per 100 pixels of arc length per second, so longer strands shed more.
/// </summary>
public readonly record struct StrokeEmission(
    PrimitiveRole Role,
    float DensityPer100px,
    float SpeedMin,
    float SpeedMax,
    float LifespanMin,
    float LifespanMax,
    float SizeMin,
    float SizeMax,
    float SpreadRadians,
    Vector2 BiasVelocity,
    Vector2? PrimaryDirection = null);