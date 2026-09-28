using System;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

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

public interface IStrokeMaterial
{
    string Name { get; }
    void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx);

    /// <summary>
    /// Everything this material sheds along its strokes. Each entry may be free-flying only, or a
    /// mixed field (with its own Flow block) that spawns both free-flying and path-following
    /// particles. See StrokeEmission.
    /// </summary>
    ReadOnlySpan<StrokeEmission> Emissions => ReadOnlySpan<StrokeEmission>.Empty;
}

public interface IParticleMaterial
{
    string Name { get; }
    void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx);

    /// <summary>
    /// Every emission this material contributes when used as a stroke emitter. Empty (the
    /// default) means "can't be used as a stroke emitter". A material that declares multiple
    /// emissions runs them side by side from the same strand — e.g. ParticleSnow declares both
    /// a speck emission and a flake emission, so a strand using it produces a proper snowfall.
    /// Each emission's optional RenderMaterial field lets the two halves render as different
    /// particle visuals.
    /// </summary>
    ReadOnlySpan<StrokeEmission> Emissions => ReadOnlySpan<StrokeEmission>.Empty;
}

public interface IRegionMaterial
{
    string Name { get; }
    void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx);
}