using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Slow: everything you do takes longer. A faint amber tint with the occasional slow drip at the
/// bottom edge, like wading through syrup - deliberately the most understated effect in the plugin,
/// since Slow is common enough that anything louder would get exhausting fast. Capped well below
/// full strength even at maximum global intensity, on purpose - see the cap on `capped` below.
/// </summary>
public sealed class SlowEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Slow;

    private static readonly uint Tint = DrawHelpers.ToU32(0.55f, 0.40f, 0.10f, 1f);

    private readonly EdgeParticleField _drips = new(maxParticles: 4, seedSalt: 0x10000B);
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _spawnPos;
    private readonly Func<int, Vector2> _spawnVel;
    private readonly Func<int, string> _noGlyph;

    public SlowEffect()
    {
        _spawnPos = SpawnPos;
        _spawnVel = SpawnVel;
        _noGlyph = static _ => "";
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float dt = ImGui.GetIO().DeltaTime;
        _screenSize = screenSize;

        float capped = alpha * 0.45f;
        DrawHelpers.DrawVignette(dl, screenSize, Tint, 0.06f, capped);

        _drips.Update(
            time, dt,
            spawnIntervalMin: 2.6f, spawnIntervalMax: 4.5f,
            spawnPos: _spawnPos, spawnVelocity: _spawnVel,
            pickGlyph: _noGlyph,
            lifespanMin: 2.6f, lifespanMax: 3.6f, sizeMin: 4f, sizeMax: 7f);

        for (int i = 0; i < _drips.Count; i++)
        {
            ref readonly var p = ref _drips[i];
            float age = time - p.Born;
            float fade = EdgeParticleField.FadeFor(age / p.Lifespan);
            float a = capped * fade;
            if (a < 0.003f) continue;

            var tail = p.Pos - new Vector2(0, p.Size * 2.2f);
            dl.AddLine(tail, p.Pos, DrawHelpers.WithAlpha(Tint, a * 0.6f), p.Size * 0.5f);
            dl.AddCircleFilled(p.Pos, p.Size, DrawHelpers.WithAlpha(Tint, a));
        }
    }

    private Vector2 SpawnPos(int seed) => new(DrawHelpers.HashRange(seed, 0.2f, 0.8f) * _screenSize.X, -6f);

    private Vector2 SpawnVel(int seed) => new(0f, DrawHelpers.HashRange(seed + 1, 22f, 38f));
}
