using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Charm family (Infatuated / Seduced): your will isn't your own. A soft warm-pink haze with small hand-drawn hearts drifting lazily upward - fainter for Infatuated, fuller for Seduced.</summary>
public sealed class CharmEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Charm;

    private static readonly uint Haze = DrawHelpers.ToU32(0.85f, 0.35f, 0.55f, 1f);
    private static readonly uint HeartColor = DrawHelpers.ToU32(1.00f, 0.55f, 0.70f, 1f);

    private readonly EdgeParticleField _hearts = new(maxParticles: 14, seedSalt: 0x100004);
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _spawnPos;
    private readonly Func<int, Vector2> _spawnVel;
    private readonly Func<int, string> _noGlyph;

    public CharmEffect()
    {
        _spawnPos = SpawnPos;
        _spawnVel = SpawnVel;
        _noGlyph = static _ => "";
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float dt = ImGui.GetIO().DeltaTime;
        _screenSize = screenSize;

        float breathe = 0.8f + 0.2f * DrawHelpers.Pulse(time, 3.4f);
        DrawHelpers.DrawVignette(dl, screenSize, Haze, 0.12f, alpha * breathe * 0.75f);

        _hearts.Update(
            time, dt,
            spawnIntervalMin: 0.5f, spawnIntervalMax: 1.1f,
            spawnPos: _spawnPos, spawnVelocity: _spawnVel,
            pickGlyph: _noGlyph,
            lifespanMin: 2.6f, lifespanMax: 3.8f, sizeMin: 10f, sizeMax: 18f);

        for (int i = 0; i < _hearts.Count; i++)
        {
            ref readonly var p = ref _hearts[i];
            float age = time - p.Born;
            float fade = EdgeParticleField.FadeFor(age / p.Lifespan);
            float sway = MathF.Sin(age * 1.6f + p.Born * 2f) * 14f;
            DrawHeart(dl, p.Pos + new Vector2(sway, 0), p.Size, DrawHelpers.WithAlpha(HeartColor, alpha * fade));
        }
    }

    // Drawn as a shape (two circles + a triangle) rather than a text glyph, so it doesn't depend on
    // the game's font atlas including a heart character - this is guaranteed to render everywhere.
    private static void DrawHeart(ImDrawListPtr dl, Vector2 center, float size, uint color)
    {
        float r = size * 0.34f;
        var lobeL = center + new Vector2(-r * 0.85f, -r * 0.5f);
        var lobeR = center + new Vector2(r * 0.85f, -r * 0.5f);
        dl.AddCircleFilled(lobeL, r, color);
        dl.AddCircleFilled(lobeR, r, color);

        var tip = center + new Vector2(0, size * 0.62f);
        var baseL = center + new Vector2(-r * 1.7f, -r * 0.1f);
        var baseR = center + new Vector2(r * 1.7f, -r * 0.1f);
        dl.AddTriangleFilled(baseL, baseR, tip, color);
    }

    private Vector2 SpawnPos(int seed) => new(
        DrawHelpers.HashRange(seed, 0.05f, 0.95f) * _screenSize.X,
        DrawHelpers.HashRange(seed + 1, 0.85f, 1.0f) * _screenSize.Y);

    private Vector2 SpawnVel(int seed) => new(0f, DrawHelpers.HashRange(seed + 2, -50f, -28f));
}
