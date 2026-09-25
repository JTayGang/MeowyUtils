using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Misery: plunged into the depths of it - damage taken is up. A heavy, dark indigo vignette with a few large, slow tears welling up and falling, far more sparse and weighty than Dropsy's quick rain.</summary>
public sealed class MiseryEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Misery;

    private static readonly uint Tint = DrawHelpers.ToU32(0.05f, 0.04f, 0.16f, 1f);
    private static readonly uint Tear = DrawHelpers.ToU32(0.30f, 0.35f, 0.60f, 1f);

    private readonly EdgeParticleField _tears = new(maxParticles: 6, seedSalt: 0x10000A);
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _spawnPos;
    private readonly Func<int, Vector2> _spawnVel;
    private readonly Func<int, string> _noGlyph;

    public MiseryEffect()
    {
        _spawnPos = SpawnPos;
        _spawnVel = SpawnVel;
        _noGlyph = static _ => "";
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float dt = ImGui.GetIO().DeltaTime;
        _screenSize = screenSize;

        float sag = 0.8f + 0.2f * DrawHelpers.Pulse(time, 6f);
        DrawHelpers.DrawVignette(dl, screenSize, Tint, 0.19f, alpha * sag);

        _tears.Update(
            time, dt,
            spawnIntervalMin: 1.4f, spawnIntervalMax: 2.6f,
            spawnPos: _spawnPos, spawnVelocity: _spawnVel,
            pickGlyph: _noGlyph,
            lifespanMin: 2.2f, lifespanMax: 3.2f, sizeMin: 5f, sizeMax: 9f);

        for (int i = 0; i < _tears.Count; i++)
        {
            ref readonly var p = ref _tears[i];
            float age = time - p.Born;
            float fade = EdgeParticleField.FadeFor(age / p.Lifespan);
            float a = alpha * fade;
            if (a < 0.003f) continue;

            var tail = p.Pos - new Vector2(0, p.Size * 4.5f);
            dl.AddLine(tail, p.Pos, DrawHelpers.WithAlpha(Tint, a * 0.7f), p.Size * 0.5f);
            dl.AddCircleFilled(p.Pos, p.Size, DrawHelpers.WithAlpha(Tear, a * 0.85f));
        }
    }

    private Vector2 SpawnPos(int seed) => new(DrawHelpers.HashRange(seed, 0.15f, 0.85f) * _screenSize.X, -8f);

    private Vector2 SpawnVel(int seed) => new(DrawHelpers.HashRange(seed + 1, -8f, 8f), DrawHelpers.HashRange(seed + 2, 90f, 140f));
}
