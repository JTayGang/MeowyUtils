using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Purple glyphs drift up out of the edges of the screen, like your words are leaking out as raw magic instead of speech. Pair with Configuration.SilenceBlocksChat / ChatBlocker for an actual chat lockout, not just the visual.</summary>
public sealed class SilenceEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Silence;

    // Plain ASCII letters, not exotic Unicode runes - guarantees they render in any font Dalamud
    // ships, with zero risk of showing up as missing-glyph tofu boxes. Swap this for a custom rune
    // font via Dalamud's IFontHandle API later if you want a more exotic look (see the README).
    private static readonly string[] Glyphs = { "R", "X", "Z", "V", "K", "N", "M", "S", "H", "Y", "Q" };

    // Text is drawn top-left-anchored, so spawn positions need to stay at least a glyph's worth of
    // pixels away from the true screen edge on every side - otherwise particles born right at the
    // edge render mostly (or entirely) clipped off-screen, which reads as "nothing shows up" even
    // though particles genuinely are spawning. FootprintPx comfortably covers the largest glyph
    // (26px) plus its glow padding.
    private const float FootprintPx = 40f;
    private const float EdgeZone = 90f; // how deep into the screen, from each edge, particles can land

    private readonly EdgeParticleField _particles = new(maxParticles: 22, seedSalt: 0x511ECE);

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        _particles.Update(
            time, ImGui.GetIO().DeltaTime,
            spawnIntervalMin: 0.2f, spawnIntervalMax: 0.4f,
            spawnPos: seed => RandomEdgePos(screenSize, seed),
            spawnVelocity: seed => DrawHelpers.V(DrawHelpers.HashRange(seed, -8f, 8f), DrawHelpers.HashRange(seed + 1, -30f, -12f)),
            pickGlyph: seed => Glyphs[(int)(DrawHelpers.Hash01(seed) * Glyphs.Length) % Glyphs.Length],
            lifespanMin: 1.4f, lifespanMax: 2.6f,
            sizeMin: 16f, sizeMax: 26f);

        // Bright, glowing pinkish-purple (leans further pink than a flat violet).
        uint pink = DrawHelpers.ToU32(0.95f, 0.32f, 0.88f, 1f);
        _particles.DrawGlyphs(dl, time, pink, alpha, glow: 1.6f);
    }

    private static Vector2 RandomEdgePos(Vector2 size, int seed)
    {
        float side = DrawHelpers.Hash01(seed);
        float along = DrawHelpers.HashRange(seed + 10, 0f, 1f);
        float depth = DrawHelpers.HashRange(seed + 2, FootprintPx, FootprintPx + EdgeZone);
        float usableW = MathF.Max(1f, size.X - FootprintPx * 2f);
        float usableH = MathF.Max(1f, size.Y - FootprintPx * 2f);

        if (side < 0.5f) // bottom edge (favored - words trail off downward)
            return DrawHelpers.V(FootprintPx + along * usableW, size.Y - depth);

        if (side < 0.75f) // left edge
            return DrawHelpers.V(depth, FootprintPx + along * usableH);

        return DrawHelpers.V(size.X - depth, FootprintPx + along * usableH); // right edge
    }
}
