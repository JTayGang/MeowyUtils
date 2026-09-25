using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Infirmity: healing magic can't quite reach you. A pale, washed-out tint with fine dust drifting slowly downward, like the color and vitality are settling out of the air.</summary>
public sealed class InfirmityEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Infirmity;

    private static readonly uint Tint = DrawHelpers.ToU32(0.65f, 0.65f, 0.62f, 1f);
    private static readonly uint Dust = DrawHelpers.ToU32(0.80f, 0.80f, 0.76f, 1f);

    private readonly EdgeParticleField _dust = new(maxParticles: 30, seedSalt: 0x100009);
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _spawnPos;
    private readonly Func<int, Vector2> _spawnVel;
    private readonly Func<int, string> _noGlyph;

    public InfirmityEffect()
    {
        _spawnPos = SpawnPos;
        _spawnVel = SpawnVel;
        _noGlyph = static _ => "";
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float dt = ImGui.GetIO().DeltaTime;
        _screenSize = screenSize;

        DrawHelpers.DrawVignette(dl, screenSize, Tint, 0.10f, alpha * 0.7f);

        _dust.Update(
            time, dt,
            spawnIntervalMin: 0.12f, spawnIntervalMax: 0.35f,
            spawnPos: _spawnPos, spawnVelocity: _spawnVel,
            pickGlyph: _noGlyph,
            lifespanMin: 3.5f, lifespanMax: 5.5f, sizeMin: 1.2f, sizeMax: 2.8f);

        for (int i = 0; i < _dust.Count; i++)
        {
            ref readonly var p = ref _dust[i];
            float age = time - p.Born;
            float fade = EdgeParticleField.FadeFor(age / p.Lifespan);
            float drift = MathF.Sin(age * 0.5f + p.Born) * 10f;
            dl.AddCircleFilled(p.Pos + new Vector2(drift, 0), p.Size, DrawHelpers.WithAlpha(Dust, alpha * fade * 0.6f));
        }
    }

    private Vector2 SpawnPos(int seed) => new(DrawHelpers.HashRange(seed, 0f, 1f) * _screenSize.X, -4f);

    private Vector2 SpawnVel(int seed) => new(0f, DrawHelpers.HashRange(seed + 1, 14f, 28f));
}
