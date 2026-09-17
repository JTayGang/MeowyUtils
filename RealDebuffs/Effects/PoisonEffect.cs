using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>A sickly green tint at the edges with the occasional drip falling from the top - vaguely nauseous rather than sharp or dangerous-looking. Drips are drawn as small vector teardrops (a filled circle + a triangle tail), not text glyphs, so there's no font-coverage risk at all.</summary>
public sealed class PoisonEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Poison;

    private readonly EdgeParticleField _drips = new(maxParticles: 10, seedSalt: 0x1A121234);

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float queasy = DrawHelpers.Pulse(time, 3.2f);
        DrawHelpers.DrawVignette(dl, screenSize, DrawHelpers.ToU32(0.35f, 0.55f, 0.15f, 1f), 0.1f + 0.015f * queasy, alpha * 0.8f);

        _drips.Update(
            time, ImGui.GetIO().DeltaTime,
            spawnIntervalMin: 0.5f, spawnIntervalMax: 1.0f,
            spawnPos: seed => DrawHelpers.V(DrawHelpers.HashRange(seed, 0f, screenSize.X), -10f),
            spawnVelocity: _ => DrawHelpers.V(0f, screenSize.Y * 0.55f),
            pickGlyph: _ => "", // unused - this effect draws its own shape below instead of a glyph
            lifespanMin: 1.0f, lifespanMax: 1.6f,
            sizeMin: 5f, sizeMax: 8f);

        uint dripColor = DrawHelpers.ToU32(0.55f, 0.85f, 0.25f, 1f);
        for (int i = 0; i < _drips.Count; i++)
        {
            ref readonly var p = ref _drips[i];
            float fade = EdgeParticleField.FadeFor((time - p.Born) / p.Lifespan);
            uint col = DrawHelpers.WithAlpha(dripColor, alpha * fade);

            dl.AddCircleFilled(p.Pos, p.Size, col);
            var tail = p.Pos - DrawHelpers.V(0, p.Size * 2.2f);
            dl.AddTriangleFilled(tail, p.Pos - DrawHelpers.V(p.Size, 0), p.Pos + DrawHelpers.V(p.Size, 0), col);
        }
    }
}
