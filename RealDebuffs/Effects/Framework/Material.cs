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

    /// <summary>
    /// How thick the strand is at arc length <paramref name="arc"/>, as a radius in pixels. Anything that
    /// has to hang off a strand's surface (a drip, a bead of liquid) asks this to find the surface rather
    /// than the centreline. The default is a plain tube of the stroke's WidthHint; a material that tapers
    /// or swells overrides it with the same profile it draws.
    /// </summary>
    float RadiusAt(in StrokePrimitive s, float arc, float shortSide) => MathF.Max(1f, s.WidthHint * 0.5f);
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
    float ClusterConeRadians = 0f,
    StrokeDripOptions? Drip = null);

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

/// <summary>
/// The viscous half of a stroke emission. An emission with this block does not scatter free particles:
/// it grows drops. A drop forms on the underside of the strand, swells, stretches into a neck, pinches off
/// and falls, trailing a string that snaps back to the surface; then the next one begins. The emission's
/// Size range is the drop's radius (px at 1080p), Gravity is how hard it falls (px/s^2), and
/// <see cref="StrokeEmission.DensityPer100px"/> is ignored in favour of <see cref="SiteSpacingPx"/>.
/// </summary>
/// <param name="SiteSpacingPx">Typical gap between drip sites along the strand (px at 1080p). Each site grows one drop at a time.</param>
/// <param name="CycleSecondsMin">Shortest time for a drop to form and fall. Longer cycles read as thicker liquid.</param>
/// <param name="CycleSecondsMax">Longest such time. Each cycle draws its own.</param>
/// <param name="MinSlope">How far the surface must face downhill for a drop to hang there, 0..1 (0 = anywhere, 1 = only the very underside). Near-vertical stretches have no underside, so they stay dry.</param>
/// <param name="Stringiness">0..1: how far the neck stretches before it lets go. 0 is water (a drop just falls), 1 is honey (a long, thinning string).</param>
/// <param name="SatelliteChance">0..1: how often the string leaves a small satellite droplet trailing the main drop.</param>
/// <param name="TipSite">Also hang a drop from the strand's free end when it points down (the most natural place for one).</param>
public readonly record struct StrokeDripOptions(
    float SiteSpacingPx,
    float CycleSecondsMin,
    float CycleSecondsMax,
    float MinSlope,
    float Stringiness,
    float SatelliteChance,
    bool  TipSite = true);
