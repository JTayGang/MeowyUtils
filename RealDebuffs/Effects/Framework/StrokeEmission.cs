using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// One thing a stroke material (or a particle material used as a stroke emitter) sheds along its
/// length. This is the single unified emission type: it always describes the free-flying
/// behavior, and optionally describes a path-following behavior via the Flow block.
///
/// When Flow is null, every spawn is a free-flying particle — a spark, a falling drip, an ember.
/// When Flow is set, the emission is treated as a "field" that spawns both kinds: each spawn
/// rolls against Flow.Share to decide whether it follows the strand (running down it, wobbling,
/// catching on obstacles) or flies free (falling under gravity, or launching perpendicular).
/// That way a single declaration can express "this strand leaks drips that sometimes run down
/// its length and sometimes detach and fall" without the material needing two entries.
///
/// A material that wants genuinely independent rates for the two kinds can declare two emissions
/// — one with Flow set and one without — and they'll run side by side from the same strand.
///
/// Density is per 100 pixels of arc length per second, shared across both kinds.
/// </summary>
public readonly record struct StrokeEmission(
    PrimitiveRole Role,
    float DensityPer100px,

    // ---- free-flying behavior ----
    float SpeedMin,
    float SpeedMax,
    float LifespanMin,
    float LifespanMax,
    float SizeMin,
    float SizeMax,
    float SpreadRadians,
    Vector2 BiasVelocity,

    /// <summary>Free-flying launch direction. Null = perpendicular to the strand.</summary>
    Vector2? PrimaryDirection = null,

    /// <summary>Free-flying acceleration. Zero for coasting sparks; (0, +N) for falling drips.</summary>
    Vector2 Gravity = default,

    /// <summary>
    /// Optional path-following block. Null = emission is free-flying only. Non-null = some
    /// fraction of spawns follow the strand instead of flying free.
    /// </summary>
    StrokeFlowOptions? Flow = null);

/// <summary>
/// The path-following half of a stroke emission: drips (or anything else) that advance along the
/// parent strand, wobble perpendicular to it, and modulate their speed to catch and release on
/// obstacles (typically the material's suckers or the chain's links). See StrokeAutoEmitter for
/// how the two kinds share spawn rate and are chosen.
/// </summary>
public readonly record struct StrokeFlowOptions(
    /// <summary>Fraction of spawns that become flowing (0..1). 0 = none, 1 = all.</summary>
    float Share,

    /// <summary>Speed along the strand, in px/s. Independent of the free-flying speed range.</summary>
    float SpeedMin,
    float SpeedMax,

    /// <summary>Side-to-side wander, in px.</summary>
    float WobbleAmplitude,
    float WobbleFrequencyHz,

    /// <summary>
    /// Distance between obstacles along the strand, in px. A flowing drip slows down near each
    /// obstacle and speeds up between them, reading as the drip catching and releasing.
    /// </summary>
    float ObstacleSpacingPx,

    /// <summary>Distance from the strand's centerline, as a fraction of the strand's half-width.</summary>
    float LateralOffsetFrac);