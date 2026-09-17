using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Everything goes soft and blue, and drowsy "Z"s drift up from the bottom corners.</summary>
public sealed class SleepEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Sleep;

    private readonly EdgeParticleField _zs = new(maxParticles: 6, seedSalt: 0x51335133);

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float drift = DrawHelpers.Pulse(time, 4f);
        DrawHelpers.DrawVignette(dl, screenSize, DrawHelpers.ToU32(0.20f, 0.28f, 0.55f, 1f), 0.16f + 0.02f * drift, alpha * 0.85f);

        _zs.Update(
            time, ImGui.GetIO().DeltaTime,
            spawnIntervalMin: 1.1f, spawnIntervalMax: 1.8f,
            spawnPos: seed => DrawHelpers.V(
                DrawHelpers.Hash01(seed) < 0.5f ? screenSize.X * 0.08f : screenSize.X * 0.92f,
                screenSize.Y * DrawHelpers.HashRange(seed + 1, 0.85f, 0.95f)),
            spawnVelocity: _ => DrawHelpers.V(4f, -14f),
            pickGlyph: _ => "z",
            lifespanMin: 2.5f, lifespanMax: 3.5f,
            sizeMin: 18f, sizeMax: 30f);

        _zs.DrawGlyphs(dl, time, DrawHelpers.ToU32(0.75f, 0.85f, 1f, 1f), alpha);
    }
}
