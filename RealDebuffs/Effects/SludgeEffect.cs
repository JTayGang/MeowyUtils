using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Sludge: earth damage over time. A murky brown vignette with thick globs of mud oozing down from the top edge, heavier and slower than Dropsy's clean water drops.</summary>
public sealed class SludgeEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Sludge;

    private static readonly uint Deep = DrawHelpers.ToU32(0.18f, 0.11f, 0.04f, 1f);
    private static readonly uint Bright = DrawHelpers.ToU32(0.38f, 0.25f, 0.10f, 1f);

    private readonly EdgeParticleField _globs = new(maxParticles: 14, seedSalt: 0x10000C);
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _spawnPos;
    private readonly Func<int, Vector2> _spawnVel;
    private readonly Func<int, string> _noGlyph;

    public SludgeEffect()
    {
        _spawnPos = SpawnPos;
        _spawnVel = SpawnVel;
        _noGlyph = static _ => "";
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float dt = ImGui.GetIO().DeltaTime;
        _screenSize = screenSize;

        DrawHelpers.DrawVignette(dl, screenSize, Deep, 0.10f, alpha * 0.85f);

        _globs.Update(
            time, dt,
            spawnIntervalMin: 0.35f, spawnIntervalMax: 0.75f,
            spawnPos: _spawnPos, spawnVelocity: _spawnVel,
            pickGlyph: _noGlyph,
            lifespanMin: 1.8f, lifespanMax: 2.6f, sizeMin: 6f, sizeMax: 11f);

        for (int i = 0; i < _globs.Count; i++)
        {
            ref readonly var p = ref _globs[i];
            float age = time - p.Born;
            float fade = EdgeParticleField.FadeFor(age / p.Lifespan);
            float a = alpha * fade;
            if (a < 0.003f) continue;

            var tail = p.Pos - new Vector2(0, p.Size * 2.0f);
            dl.AddLine(tail, p.Pos, DrawHelpers.WithAlpha(Deep, a * 0.7f), p.Size * 0.7f);
            dl.AddCircleFilled(p.Pos, p.Size, DrawHelpers.WithAlpha(Bright, a * 0.9f));
            dl.AddCircleFilled(p.Pos, p.Size * 0.55f, DrawHelpers.WithAlpha(Deep, a));
        }
    }

    private Vector2 SpawnPos(int seed) => new(DrawHelpers.HashRange(seed, 0.02f, 0.98f) * _screenSize.X, -8f);

    private Vector2 SpawnVel(int seed) => new(DrawHelpers.HashRange(seed + 1, -4f, 4f), DrawHelpers.HashRange(seed + 2, 70f, 130f));
}
