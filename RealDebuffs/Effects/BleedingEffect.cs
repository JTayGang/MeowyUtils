using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Bleeding: a physical wound leaking damage over time. Dark red vignette with heavy drops beading at the top edge and falling, each trailing a short streak.</summary>
public sealed class BleedingEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Bleeding;

    private static readonly uint Deep = DrawHelpers.ToU32(0.45f, 0.02f, 0.04f, 1f);
    private static readonly uint Bright = DrawHelpers.ToU32(0.75f, 0.06f, 0.08f, 1f);

    private readonly EdgeParticleField _drops = new(maxParticles: 26, seedSalt: 0x100002);
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _spawnPos;
    private readonly Func<int, Vector2> _spawnVel;
    private readonly Func<int, string> _noGlyph;

    public BleedingEffect()
    {
        _spawnPos = SpawnPos;
        _spawnVel = SpawnVel;
        _noGlyph = static _ => "";
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float dt = ImGui.GetIO().DeltaTime;
        _screenSize = screenSize;

        float pulse = 0.85f + 0.15f * DrawHelpers.Pulse(time, 1.8f);
        DrawHelpers.DrawVignette(dl, screenSize, Deep, 0.09f, alpha * pulse);

        _drops.Update(
            time, dt,
            spawnIntervalMin: 0.05f, spawnIntervalMax: 0.22f,
            spawnPos: _spawnPos, spawnVelocity: _spawnVel,
            pickGlyph: _noGlyph,
            lifespanMin: 1.0f, lifespanMax: 1.8f, sizeMin: 3f, sizeMax: 7f);

        for (int i = 0; i < _drops.Count; i++)
        {
            ref readonly var p = ref _drops[i];
            float age = time - p.Born;
            float fade = EdgeParticleField.FadeFor(age / p.Lifespan);
            float a = alpha * fade;
            if (a < 0.003f) continue;

            // A short streak trailing upward from the drop reads as motion-blurred falling, not a static dot.
            var tail = p.Pos - new Vector2(0, p.Size * 3.2f);
            dl.AddLine(tail, p.Pos, DrawHelpers.WithAlpha(Deep, a * 0.5f), p.Size * 0.6f);
            dl.AddCircleFilled(p.Pos, p.Size, DrawHelpers.WithAlpha(Bright, a));
        }
    }

    private Vector2 SpawnPos(int seed) => new(DrawHelpers.HashRange(seed, 0.02f, 0.98f) * _screenSize.X, -6f);

    private Vector2 SpawnVel(int seed) => new(DrawHelpers.HashRange(seed + 1, -6f, 6f), DrawHelpers.HashRange(seed + 2, 260f, 420f));
}
