using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Amnesia: your own skills stop making sense. A pale, uneven fog that won't settle, with faint question marks drifting up out of it as if a thought keeps almost forming and slipping away again.</summary>
public sealed class AmnesiaEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Amnesia;

    private static readonly uint Fog = DrawHelpers.ToU32(0.72f, 0.72f, 0.76f, 1f);
    private static readonly uint Glyph = DrawHelpers.ToU32(0.92f, 0.92f, 0.97f, 1f);
    private static readonly Func<int, string> QuestionMark = static _ => "?";

    private readonly EdgeParticleField _marks = new(maxParticles: 10, seedSalt: 0x100001);
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _spawnPos;
    private readonly Func<int, Vector2> _spawnVel;

    public AmnesiaEffect()
    {
        _spawnPos = SpawnPos;
        _spawnVel = SpawnVel;
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float dt = ImGui.GetIO().DeltaTime;
        _screenSize = screenSize;

        // The fog's density wanders instead of holding steady - two out-of-phase pulses so it never
        // finds a resting point, like focus keeps sliding away and almost coming back.
        float waver = 0.7f + 0.3f * DrawHelpers.Pulse(time, 5.2f) * DrawHelpers.Pulse(time, 3.1f, 0.35f);
        DrawHelpers.DrawVignette(dl, screenSize, Fog, 0.15f, alpha * waver);

        _marks.Update(
            time, dt,
            spawnIntervalMin: 1.1f, spawnIntervalMax: 2.4f,
            spawnPos: _spawnPos, spawnVelocity: _spawnVel,
            pickGlyph: QuestionMark,
            lifespanMin: 2.8f, lifespanMax: 4.0f, sizeMin: 20f, sizeMax: 32f);

        _marks.DrawGlyphs(dl, time, Glyph, alpha, glow: 0.7f);
    }

    private Vector2 SpawnPos(int seed) => new(
        DrawHelpers.HashRange(seed, 0.10f, 0.90f) * _screenSize.X,
        DrawHelpers.HashRange(seed + 1, 0.70f, 0.92f) * _screenSize.Y);

    private Vector2 SpawnVel(int seed) => new(
        DrawHelpers.HashRange(seed + 2, -5f, 5f),
        DrawHelpers.HashRange(seed + 3, -20f, -9f));
}
