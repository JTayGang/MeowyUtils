using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Context passed to every material Draw call. Materials get everything they need to render a
/// primitive without reaching back into the effect that emitted it.
/// </summary>
public readonly struct MaterialContext
{
    public readonly float Time;
    public readonly float ScreenScale;   // shortSide / 1080f, for pixel-based sizing
    public readonly float Alpha;         // global intensity (0..1+), applied once per primitive
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
}

public interface IParticleMaterial
{
    string Name { get; }
    void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx);
}

public interface IRegionMaterial
{
    string Name { get; }
    void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx);
}