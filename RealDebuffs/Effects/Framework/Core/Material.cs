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

/// <summary>Base of every material; NaturalLanguageWords are what a "made of X" phrase can select (empty = none).</summary>
public interface IMaterial
{
    string Name { get; }
    string[] NaturalLanguageWords => Array.Empty<string>();
}

/// <summary>A stroke drawn in passes with a coat between them, so the host's own parts cover and uncover the coat.</summary>
public interface ICoatLayeredHost : IStrokeMaterial
{
    /// <summary>True if this host draws that coat itself (it then must, in DrawWithCoat); false leaves it to be drawn over the finished stroke.</summary>
    bool Layers(Coat coat);

    /// <summary>Draws the stroke and, at the right points of its own order, the coat.</summary>
    void DrawWithCoat(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx, Coat coat, float alpha);
}

public interface IStrokeMaterial : IMaterial
{
    void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx);

    /// <summary>What it sheds along its strokes: a steady trickle, free-flying or path-following.</summary>
    ReadOnlySpan<StrokeEmission> Emissions => ReadOnlySpan<StrokeEmission>.Empty;

    /// <summary>What it throws when hit (EffectScene.AddImpact); each entry spawns independently, empty = nothing.</summary>
    ReadOnlySpan<ImpactEmission> ImpactEmissions => ReadOnlySpan<ImpactEmission>.Empty;

    /// <summary>Strand radius in px at arc length arc, so a drip finds the surface, not the centreline.</summary>
    float RadiusAt(in StrokePrimitive s, float arc, float shortSide) => MathF.Max(1f, s.WidthHint * 0.5f);

    /// <summary>False where a strand can't hold a drip, such as a tapered end with no surface left to hang from.</summary>
    bool CanDripAt(in StrokePrimitive s, float arc, float shortSide) => true;
}

/// <summary>A particle material drawing only via MeshDraw mesh primitives, so runs can be batched in order; anything drawing another way must not carry this.</summary>
public interface IBatchedParticleMaterial { }

public interface IParticleMaterial : IMaterial
{
    void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx);

    /// <summary>Emissions this material contributes as a stroke emitter; empty means it can't be one.</summary>
    ReadOnlySpan<StrokeEmission> Emissions => ReadOnlySpan<StrokeEmission>.Empty;

    /// <summary>True for matter that clings to or falls from a strand (slime, blood): drawn right after its host stroke.</summary>
    bool DrawsWithHost => false;
}

public interface IRegionMaterial : IMaterial
{
    void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx);
}

/// <summary>Effect-built data a region material draws from; carried by RegionPrimitive.State.</summary>
public interface IRegionState { }

/// <summary>A region material that draws a specific state type and skips regions carrying anything else.</summary>
public abstract class RegionMaterial<TState> : IRegionMaterial where TState : class, IRegionState
{
    public abstract string Name { get; }

    public void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx)
    {
        if (r.State is TState state) Draw(dl, state, in r, in ctx);
        else DrawWithoutState(dl, in r, in ctx);
    }

    protected abstract void Draw(ImDrawListPtr dl, TState state, in RegionPrimitive r, in MaterialContext ctx);

    /// <summary>What a region without this state draws; nothing by default.</summary>
    protected virtual void DrawWithoutState(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx) { }

    /// <summary>The region's rectangle in its tint, for materials that degrade to a plain fill.</summary>
    protected static void FlatFill(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx)
    {
        float a = r.Alpha * ctx.Alpha;
        if (a > 0.001f) dl.AddRectFilled(r.Min, r.Max, DrawHelpers.WithAlpha(r.Tint, a));
    }
}

/// <summary>Variants of a material family; packs are found by reflection like any material.</summary>
public interface IMaterialPack
{
    IEnumerable<IMaterial> Materials { get; }
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
    CoatOptions? Coat = null)
{
    /// <summary>A coat: the matter clings to the strand, runs, pools and lets go (see Coat); only the role and options matter.</summary>
    public static StrokeEmission Coated(PrimitiveRole role, float supply = 1f, float clump = 0.6f, float skin = 1f) =>
        new(role, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, Vector2.Zero, Coat: new CoatOptions(supply, clump, skin));
}

/// <summary>How an emission loads its strand: supply multiplier, patchiness (0 even, 1 clumped), thin-skin coverage (0 none; glossy hosts want little).</summary>
public readonly record struct CoatOptions(float Supply = 1f, float Clump = 0.6f, float Skin = 1f);

/// <summary>One burst a stroke material throws when hit.</summary>
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

