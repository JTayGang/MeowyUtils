using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// One thing a stroke material sheds along its length as a free-flying particle.
///
/// Gravity is optional and accelerates the particle every frame; without it, particles coast at
/// their launch velocity (correct for sparks off a struck chain). With it, particles arc down
/// (or up, for negative Y) under continuous acceleration — the shape a falling drip has.
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
    Vector2? PrimaryDirection = null,
    Vector2 Gravity = default);

/// <summary>
/// A drip (or anything else) that flows ALONG a stroke's path rather than flying free of it.
/// Spawned at a random arc position, advanced along the strand each frame, wobbling perpendicular
/// to it, with speed oscillating so it visibly catches and releases as it passes each obstacle
/// (typically the material's suckers).
/// </summary>
public readonly record struct StrokeFlowEmission(
    PrimitiveRole Role,
    float DensityPer100px,
    float SpeedMin,
    float SpeedMax,
    float LifespanMin,
    float LifespanMax,
    float SizeMin,
    float SizeMax,
    float WobbleAmplitude,
    float WobbleFrequencyHz,

    /// <summary>
    /// Distance between obstacles along the strand, in pixels. Speed is modulated so the drip
    /// slows near each obstacle position and speeds up between them.
    /// </summary>
    float ObstacleSpacingPx,

    /// <summary>Lateral offset from the strand's centerline, as a fraction of the strand's half-width.</summary>
    float LateralOffsetFrac = 0.35f);