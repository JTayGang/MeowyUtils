using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Burns: fire damage over time. A warm orange vignette that flickers unevenly like heat haze, with embers rising from the bottom edge and guttering out partway up.</summary>
public sealed class BurnsEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Burns;

    private static readonly uint Glow = DrawHelpers.ToU32(0.55f, 0.12f, 0.02f, 1f);
    private static readonly uint Core = DrawHelpers.ToU32(1.00f, 0.55f, 0.12f, 1f);

    private readonly EdgeParticleField _embers = new(maxParticles: 40, seedSalt: 0x100003);
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _spawnPos;
    private readonly Func<int, Vector2> _spawnVel;
    private readonly Func<int, string> _noGlyph;

    public BurnsEffect()
    {
        _spawnPos = SpawnPos;
        _spawnVel = SpawnVel;
        _noGlyph = static _ => "";
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float dt = ImGui.GetIO().DeltaTime;
        _screenSize = screenSize;

        // Two overlapping flicker rates, like real heat haze never settling into one steady rhythm.
        float flicker = 0.8f + 0.2f * DrawHelpers.Pulse(time, 0.35f) * DrawHelpers.Pulse(time, 0.9f, 0.2f);
        DrawHelpers.DrawVignette(dl, screenSize, Glow, 0.10f, alpha * flicker);

        _embers.Update(
            time, dt,
            spawnIntervalMin: 0.03f, spawnIntervalMax: 0.10f,
            spawnPos: _spawnPos, spawnVelocity: _spawnVel,
            pickGlyph: _noGlyph,
            lifespanMin: 1.2f, lifespanMax: 2.2f, sizeMin: 2f, sizeMax: 5f);

        for (int i = 0; i < _embers.Count; i++)
        {
            ref readonly var p = ref _embers[i];
            float age = time - p.Born;
            float fade = EdgeParticleField.FadeFor(age / p.Lifespan);
            float wob = MathF.Sin(age * 6f + p.Born * 3f) * 8f; // side-to-side flutter as it rises
            var pos = p.Pos + new Vector2(wob, 0);
            float a = alpha * fade;
            if (a < 0.003f) continue;

            dl.AddCircleFilled(pos, p.Size * 2.0f, DrawHelpers.WithAlpha(Glow, a * 0.4f));
            dl.AddCircleFilled(pos, p.Size, DrawHelpers.WithAlpha(Core, a * 0.9f));
        }
    }

    private Vector2 SpawnPos(int seed) => new(DrawHelpers.HashRange(seed, 0.03f, 0.97f) * _screenSize.X, _screenSize.Y + 6f);

    private Vector2 SpawnVel(int seed) => new(0f, DrawHelpers.HashRange(seed + 1, -160f, -90f));
}
