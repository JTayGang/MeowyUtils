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
    /// Everything this material sheds along its strokes. Each entry may be free-flying only,
    /// or a mixed field (with its own Flow block) that spawns both free-flying and path-following
    /// particles. See StrokeEmission.
    /// </summary>
    ReadOnlySpan<StrokeEmission> Emissions => ReadOnlySpan<StrokeEmission>.Empty;
}

public interface IParticleMaterial
{
    string Name { get; }
    void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx);

    /// <summary>
    /// How this material looks when emitted along a stroke. Null means "can't be used as a
    /// stroke emitter". The returned emission may itself carry a Flow block, in which case
    /// "made of X" via a tooltip description will produce both falling and flowing particles.
    /// </summary>
    StrokeEmission? Emission => null;
}

public interface IRegionMaterial
{
    string Name { get; }
    void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx);
}