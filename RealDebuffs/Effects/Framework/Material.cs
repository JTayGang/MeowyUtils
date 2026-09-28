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

    /// <summary>Free-flying particles this material sheds along its length (sparks, falling drips, embers).</summary>
    ReadOnlySpan<StrokeEmission> Emissions => ReadOnlySpan<StrokeEmission>.Empty;

    /// <summary>Path-following particles this material sheds (drips running down the strand).</summary>
    ReadOnlySpan<StrokeFlowEmission> Flows => ReadOnlySpan<StrokeFlowEmission>.Empty;
}

public interface IParticleMaterial
{
    string Name { get; }
    void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx);
    StrokeEmission? Emission => null;
}

public interface IRegionMaterial
{
    string Name { get; }
    void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx);
}