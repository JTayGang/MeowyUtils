using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// One thing a stroke material (or a particle material used as a stroke emitter) sheds along its
/// length. This is the single unified emission type:
///
///  - The base fields describe the free-flying behavior (sparks, falling drips, embers, snow).
///  - Flow (optional) describes a path-following behavior; when set, a fraction of spawns follow
///    the strand instead of flying free. See StrokeFlowOptions.
///  - RenderMaterial (optional) forces a specific renderer for the spawned particles. This is
///    what lets a single material emit two different particle kinds with two different visuals —
///    e.g. ParticleSnow declaring both speck and flake emissions, so "made of snow" produces a
///    proper snowfall instead of just specks.
///
/// Density is per 100 pixels of arc length per second, per emission. A material that declares
/// multiple emissions has each one running independently, so the effective particle count on a
/// strand is the sum of its emissions' densities.
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
    Vector2 Gravity = default,
    StrokeFlowOptions? Flow = null,
    string? RenderMaterial = null,

    /// <summary>
    /// Gust window, in seconds. Zero (default) = every particle picks its own direction. Positive
    /// = all particles spawned within a window of this many seconds share a base direction, so
    /// sparks arrive in small gusts headed the same way. The window index is derived from time
    /// and the stroke's seed, so no per-particle state is tracked.
    ///
    /// Only takes effect when PrimaryDirection is set — clustering is about a shared launch axis,
    /// and "perpendicular to the strand" is inherently per-position, so without a fixed axis
    /// there's nothing for a gust to share.
    /// </summary>
    float ClusterWindowSeconds = 0f,

    /// <summary>
    /// Cone half-angle (radians) for per-particle direction jitter WITHIN a gust. Only meaningful
    /// with ClusterWindowSeconds > 0. The gust's shared base direction still spreads across the
    /// emission's full SpreadRadians; this is the much tighter fan that groups the particles
    /// inside one gust. Typical: 0.10–0.35.
    /// </summary>
    float ClusterConeRadians = 0f);
/// <summary>
/// The path-following half of a stroke emission: drips (or anything else) that advance along the
/// parent strand, wobble perpendicular to it, and modulate their speed to catch and release on
/// obstacles (typically the material's suckers or the chain's links).
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

    /// <summary>Distance between obstacles along the strand, in px.</summary>
    float ObstacleSpacingPx,

    /// <summary>Distance from the strand's centerline, as a fraction of the strand's half-width.</summary>
    float LateralOffsetFrac);