using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Windburn: wind damage over time. Pale streaks gust across the screen edges - the one DoT here that moves sideways instead of falling or rising, like being scoured by a constant wind.</summary>
public sealed class WindburnEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Windburn;

    private static readonly uint Tint = DrawHelpers.ToU32(0.78f, 0.80f, 0.78f, 1f);

    private readonly EdgeParticleField _streaks = new(maxParticles: 18, seedSalt: 0x10000D);
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _spawnPos;
    private readonly Func<int, Vector2> _spawnVel;
    private readonly Func<int, string> _noGlyph;

    public WindburnEffect()
    {
        _spawnPos = SpawnPos;
        _spawnVel = SpawnVel;
        _noGlyph = static _ => "";
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float dt = ImGui.GetIO().DeltaTime;
        _screenSize = screenSize;

        DrawHelpers.DrawVignette(dl, screenSize, Tint, 0.07f, alpha * 0.6f);

        _streaks.Update(
            time, dt,
            spawnIntervalMin: 0.06f, spawnIntervalMax: 0.18f,
            spawnPos: _spawnPos, spawnVelocity: _spawnVel,
            pickGlyph: _noGlyph,
            lifespanMin: 0.5f, lifespanMax: 0.9f, sizeMin: 40f, sizeMax: 90f);

        for (int i = 0; i < _streaks.Count; i++)
        {
            ref readonly var p = ref _streaks[i];
            float age = time - p.Born;
            float fade = EdgeParticleField.FadeFor(age / p.Lifespan);
            float a = alpha * fade;
            if (a < 0.003f) continue;

            var dir = Vector2.Normalize(p.Velocity);
            var tail = p.Pos - dir * p.Size;
            dl.AddLine(tail, p.Pos, DrawHelpers.WithAlpha(Tint, a * 0.8f), 2.2f);
        }
    }

    // Spawn edge (left/right) is re-derived from the SAME seed in both delegates (EdgeParticleField
    // calls spawnPos(seed) and spawnVelocity(seed) with one shared seed per particle) so a streak
    // spawned on the left always travels right, and vice versa - never spawns and instantly exits.
    private Vector2 SpawnPos(int seed)
    {
        bool fromLeft = DrawHelpers.Hash01(seed) < 0.5f;
        float y = DrawHelpers.HashRange(seed + 1, 0.05f, 0.95f) * _screenSize.Y;
        return new Vector2(fromLeft ? -20f : _screenSize.X + 20f, y);
    }

    private Vector2 SpawnVel(int seed)
    {
        bool fromLeft = DrawHelpers.Hash01(seed) < 0.5f;
        float speed = DrawHelpers.HashRange(seed + 2, 500f, 900f);
        float vertJitter = DrawHelpers.HashRange(seed + 3, -0.15f, 0.15f) * speed;
        return new Vector2(fromLeft ? speed : -speed, vertJitter);
    }
}
