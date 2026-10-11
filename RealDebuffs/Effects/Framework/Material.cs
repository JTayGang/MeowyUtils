using System.Numerics;
using Dalamud.Bindings.ImGui;
namespace RealDebuffs.Effects.Framework;

/// <summary>Context passed to every material Draw call.</summary>
public readonly struct MaterialContext
{
    public readonly float Time;
    public readonly float ScreenScale;
    public readonly float Alpha;
    public readonly int   ScreenW;
    public readonly int   ScreenH;

    public MaterialContext(float time, float screenScale, float alpha, int screenW, int screenH)
    {
        Time = time; ScreenScale = screenScale; Alpha = alpha; ScreenW = screenW; ScreenH = screenH;
    }

    public float ShortSide => ScreenW < ScreenH ? ScreenW : ScreenH;
}

/// <summary>Base of every material. NaturalLanguageWords are what a "made of X" phrase can select; empty = none (regions, stroke.simple).</summary>
public interface IMaterial
{
    string Name { get; }
    string[] NaturalLanguageWords => Array.Empty<string>();
}

public interface IStrokeMaterial : IMaterial
{
    void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx);

    /// <summary>What this material sheds along its strokes: a steady trickle, free-flying or path-following (see StrokeEmission).</summary>
    ReadOnlySpan<StrokeEmission> Emissions => ReadOnlySpan<StrokeEmission>.Empty;

    /// <summary>What this material throws when hit (EffectScene.AddImpact), each entry spawned independently; empty = nothing visible.</summary>
    ReadOnlySpan<ImpactEmission> ImpactEmissions => ReadOnlySpan<ImpactEmission>.Empty;

    /// <summary>Strand radius in px at arc length arc, so anything hanging off a strand (a drip) finds the surface, not the centreline; default is a plain tube of WidthHint, tapering materials override it.</summary>
    float RadiusAt(in StrokePrimitive s, float arc, float shortSide) => MathF.Max(1f, s.WidthHint * 0.5f);
}

public interface IParticleMaterial : IMaterial
{
    void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx);

    /// <summary>Emissions this material contributes as a stroke emitter; empty means it can't be one.</summary>
    ReadOnlySpan<StrokeEmission> Emissions => ReadOnlySpan<StrokeEmission>.Empty;
}

public interface IRegionMaterial : IMaterial
{
    void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx);
}

/// <summary>One thing a stroke material (or a particle material used as a stroke emitter) sheds along its length.</summary>
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
    float ClusterWindowSeconds = 0f,
    float ClusterConeRadians = 0f,
    StrokeDripOptions? Drip = null);

/// <summary>One burst a stroke material throws when hit. Counts scale with Strength (never zero if Strength > 0); ConeRadians is the fan half-angle.</summary>
public readonly record struct ImpactEmission(
    PrimitiveRole Role,
    int CountMin,
    int CountMax,
    float SpeedMin,
    float SpeedMax,
    float LifespanMin,
    float LifespanMax,
    float SizeMin,
    float SizeMax,
    float ConeRadians,
    Vector2 Gravity = default,
    string? RenderMaterial = null);

/// <summary>The path-following half of a stroke emission.</summary>
public readonly record struct StrokeFlowOptions(
    float Share,
    float SpeedMin,
    float SpeedMax,
    float WobbleAmplitude,
    float WobbleFrequencyHz,
    float ObstacleSpacingPx,
    float LateralOffsetFrac);

/// <summary>Viscous half of a stroke emission: grows drops instead of scattering particles (Size = drop radius px at 1080p, Gravity = fall px/s^2, DensityPer100px ignored). SiteSpacingPx: gap between sites; CycleSeconds Min/Max: form-and-fall time (longer = thicker); MinSlope 0..1: how far the surface must face downhill to hang a drop; Stringiness 0..1: water to honey; SatelliteChance 0..1.</summary>
public readonly record struct StrokeDripOptions(
    float SiteSpacingPx,
    float CycleSecondsMin,
    float CycleSecondsMax,
    float MinSlope,
    float Stringiness,
    float SatelliteChance,
    bool  TipSite = true);
