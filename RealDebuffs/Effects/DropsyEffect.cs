using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Dropsy: water damage over time. A cool teal-blue vignette with heavy droplets beading at the top edge and falling straight down, each trailing a short streak.</summary>
public sealed class DropsyEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Dropsy;

    private static readonly uint Deep = DrawHelpers.ToU32(0.05f, 0.20f, 0.30f, 1f);
    private static readonly uint Bright = DrawHelpers.ToU32(0.35f, 0.65f, 0.80f, 1f);

    private readonly EdgeParticleField _drops = new(maxParticles: 20, seedSalt: 0x100006);
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _spawnPos;
    private readonly Func<int, Vector2> _spawnVel;
    private readonly Func<int, string> _noGlyph;

    public DropsyEffect()
    {
        _spawnPos = SpawnPos;
        _spawnVel = SpawnVel;
        _noGlyph = static _ => "";
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float dt = ImGui.GetIO().DeltaTime;
        _screenSize = screenSize;

        DrawHelpers.DrawVignette(dl, screenSize, Deep, 0.09f, alpha * 0.85f);

        _drops.Update(
            time, dt,
            spawnIntervalMin: 0.10f, spawnIntervalMax: 0.30f,
            spawnPos: _spawnPos, spawnVelocity: _spawnVel,
            pickGlyph: _noGlyph,
            lifespanMin: 1.3f, lifespanMax: 2.1f, sizeMin: 4f, sizeMax: 8f);

        for (int i = 0; i < _drops.Count; i++)
        {
            ref readonly var p = ref _drops[i];
            float age = time - p.Born;
            float fade = EdgeParticleField.FadeFor(age / p.Lifespan);
            float a = alpha * fade;
            if (a < 0.003f) continue;

            var tail = p.Pos - new Vector2(0, p.Size * 3.5f);
            dl.AddLine(tail, p.Pos, DrawHelpers.WithAlpha(Deep, a * 0.6f), p.Size * 0.5f);
            dl.AddCircleFilled(p.Pos, p.Size, DrawHelpers.WithAlpha(Bright, a * 0.9f));
        }
    }

    private Vector2 SpawnPos(int seed) => new(DrawHelpers.HashRange(seed, 0.02f, 0.98f) * _screenSize.X, -6f);

    private Vector2 SpawnVel(int seed) => new(DrawHelpers.HashRange(seed + 1, -4f, 4f), DrawHelpers.HashRange(seed + 2, 210f, 340f));
}
