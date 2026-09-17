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

    private readonly EdgeParticleField _particles = new(maxParticles: 16, seedSalt: 0x511ECE);

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        _particles.Update(
            time, ImGui.GetIO().DeltaTime,
            spawnIntervalMin: 0.35f, spawnIntervalMax: 0.65f,
            spawnPos: seed => RandomEdgePos(screenSize, seed),
            spawnVelocity: seed => DrawHelpers.V(DrawHelpers.HashRange(seed, -8f, 8f), DrawHelpers.HashRange(seed + 1, -30f, -12f)),
            pickGlyph: seed => Glyphs[(int)(DrawHelpers.Hash01(seed) * Glyphs.Length) % Glyphs.Length],
            lifespanMin: 1.4f, lifespanMax: 2.6f,
            sizeMin: 16f, sizeMax: 26f);

        uint purple = DrawHelpers.ToU32(0.72f, 0.35f, 0.95f, 1f);
        _particles.DrawGlyphs(dl, time, purple, alpha);
    }

    private static Vector2 RandomEdgePos(Vector2 size, int seed)
    {
        float margin = size.Y * 0.06f;
        float side = DrawHelpers.Hash01(seed);
        float along = DrawHelpers.HashRange(seed + 10, 0f, 1f);
        float inset = margin * DrawHelpers.HashRange(seed + 2, 0.3f, 1f);

        if (side < 0.5f) return DrawHelpers.V(along * size.X, size.Y - inset);       // bottom edge (favored - words trail off downward)
        if (side < 0.75f) return DrawHelpers.V(inset, along * size.Y);              // left edge
        return DrawHelpers.V(size.X - inset, along * size.Y);                        // right edge
    }
}
