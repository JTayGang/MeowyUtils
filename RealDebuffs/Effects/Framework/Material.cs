using System.Numerics;
using Dalamud.Bindings.ImGui;
namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Context passed to every material Draw call. Materials get everything they need to render a
/// primitive without reaching back into the effect that emitted it.
/// </summary>
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

/// <summary>
/// Common base for every material in the registry. Exists so code that just needs the material's
/// metadata - the vocabulary builder in MaterialRegistry, notably - can enumerate all materials
/// without caring which primitive type each one renders.
///
/// NaturalLanguageWords are the words a user can type inside a "made of X" phrase in a status
/// description to select this material. Empty array (the default) means the material has no
/// natural-language phrase - region materials and the plain fallback stroke are the current
/// examples. Such materials can still be assigned to a slot by hand in the Effect generator,
/// they just can't be referenced by a tooltip description.
/// </summary>
public interface IMaterial
{
    string Name { get; }
    string[] NaturalLanguageWords => Array.Empty<string>();
}

public interface IStrokeMaterial : IMaterial
{
    void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx);

    /// <summary>
    /// Everything this material sheds along its strokes. Each entry may be free-flying only, or a
    /// mixed field (with its own Flow block) that spawns both free-flying and path-following
    /// particles. See StrokeEmission.
    /// </summary>
    ReadOnlySpan<StrokeEmission> Emissions => ReadOnlySpan<StrokeEmission>.Empty;

    /// <summary>
    /// What this material throws when an effect reports an impact on it (EffectScene.AddImpact): a
    /// one-shot burst, as opposed to Emissions, which is a steady trickle along the strand. Empty
    /// (the default) means "nothing visibly happens", so a material only declares this if hitting
    /// it is a spectacle. Each entry is spawned independently, so a material can throw several
    /// different things at once (sparks AND flakes AND dust).
    /// </summary>
    ReadOnlySpan<ImpactEmission> ImpactEmissions => ReadOnlySpan<ImpactEmission>.Empty;
}

public interface IParticleMaterial : IMaterial
{
    void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx);

    /// <summary>
    /// Every emission this material contributes when used as a stroke emitter. Empty (the
    /// default) means "can't be used as a stroke emitter". A material that declares multiple
    /// emissions runs them side by side from the same strand.
    /// </summary>
    ReadOnlySpan<StrokeEmission> Emissions => ReadOnlySpan<StrokeEmission>.Empty;
}

public interface IRegionMaterial : IMaterial
{
    void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx);
}

/// <summary>
/// One thing a stroke material (or a particle material used as a stroke emitter) sheds along its
/// length.
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
    float ClusterWindowSeconds = 0f,
    float ClusterConeRadians = 0f);

/// <summary>
/// One thing a stroke material throws in a single burst when it is hit. Counts are scaled by the
/// impact's Strength (a feeble tap throws a fraction of the minimum, never zero if Strength > 0).
/// Direction comes from the impact itself; ConeRadians is the half-angle the particles fan across.
/// </summary>
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