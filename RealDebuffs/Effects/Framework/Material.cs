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
    /// Particles this material naturally sheds along its strokes. Empty (the default) means no
    /// ambient emission. StrokeAutoEmitter reads this for every stroke using this material and
    /// spawns the listed particles each frame.
    /// </summary>
    ReadOnlySpan<StrokeEmission> Emissions => ReadOnlySpan<StrokeEmission>.Empty;
}

public interface IParticleMaterial
{
    string Name { get; }
    void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx);

    /// <summary>
    /// How this material looks when emitted as ambient particles along a stroke. Null (the
    /// default) means this material can't be used as a stroke emitter. Populated for the visual
    /// particle materials (ember, snowflake, snow, fog, spark, drip).
    /// </summary>
    StrokeEmission? Emission => null;
}

public interface IRegionMaterial
{
    string Name { get; }
    void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx);
}