using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// A curse seal locks onto you and your words leak out of it as raw magic. A large magic circle
/// (see <see cref="MagicCircle"/>) closes around the centre of the screen, and purple glyphs drift up
/// out of the screen edges - plus a few peeled straight off the seal's rune band. Pair with
/// Configuration.SilenceBlocksChat / ChatBlocker for an actual chat lockout, not just the visual.
/// </summary>
public sealed class SilenceEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Silence;

    // Plain ASCII letters, not exotic Unicode runes - guarantees they render in any font Dalamud
    // ships, with zero risk of showing up as missing-glyph tofu boxes. (The seal's own runes are
    // drawn as vector strokes instead - see VectorRunes - so they can rotate around the ring.)
    private static readonly string[] Glyphs = { "R", "X", "Z", "V", "K", "N", "M", "S", "H", "Y", "Q" };

    // Text is drawn top-left-anchored, so spawn positions need to stay at least a glyph's worth of
    // pixels away from the true screen edge on every side - otherwise particles born right at the
    // edge render mostly (or entirely) clipped off-screen, which reads as "nothing shows up" even
    // though particles genuinely are spawning. FootprintPx comfortably covers the largest glyph
    // (26px) plus its glow padding.
    private const float FootprintPx = 40f;
    private const float EdgeZone = 90f; // how deep into the screen, from each edge, particles can land

    // ---- the seal ----
    private const float SealRadiusFrac = 0.46f; // outer radius as a fraction of the SHORTER screen side (lower = smaller seal)
    private const float SealCenterY = 0.50f;    // 0 = top of screen, 1 = bottom
    private const float NewCastGapSeconds = 1.0f; // long enough that an ordinary frame hitch mid-fight never replays the cast-in

    private readonly EdgeParticleField _particles = new(maxParticles: 22, seedSalt: 0x511ECE);
    private readonly EdgeParticleField _shed = new(maxParticles: 10, seedSalt: 0x5E41ED);

    private MagicCircle? _seal;
    private float _lastDrawTime = -100f;
    private float _castStart;

    // Where the seal currently is - read by the cached spawn delegates below.
    private Vector2 _sealCenter;
    private float _sealRadius;
    private readonly Func<int, Vector2> _shedPos;
    private readonly Func<int, Vector2> _shedVel;

    public SilenceEffect()
    {
        _shedPos = ShedSpawnPos;
        _shedVel = ShedSpawnVelocity;
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        // EffectManager stops calling Draw once the effect has fully faded out, so a gap since the
        // last call means the debuff was just (re)applied: restart the cast-in animation from zero.
        if (time - _lastDrawTime > NewCastGapSeconds) _castStart = time;
        _lastDrawTime = time;
        float age = time - _castStart;

        // ---- the seal ----
        _sealRadius = MathF.Min(screenSize.X, screenSize.Y) * SealRadiusFrac;
        _sealCenter = DrawHelpers.V(screenSize.X * 0.5f, screenSize.Y * SealCenterY);

        // Built lazily on first draw (not in the constructor) so we never touch ImGui before the game is up.
        _seal ??= new MagicCircle(
            new MagicCircle.Palette(
                halo: DrawHelpers.ToU32(0.62f, 0.16f, 0.95f, 1f),  // deep violet haze
                main: DrawHelpers.ToU32(0.95f, 0.32f, 0.88f, 1f),  // the same pink the letters use
                core: DrawHelpers.ToU32(1.00f, 0.78f, 0.97f, 1f),  // hot highlights
                ink: DrawHelpers.ToU32(0.13f, 0.02f, 0.24f, 1f)),  // dark under-stroke for contrast on bright scenes
            seed: 0x5EA1);
        _seal.Draw(dl, _sealCenter, _sealRadius, age, time, alpha);

        // ---- letters drifting up out of the screen edges (unchanged) ----
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

        // ---- letters peeling off the seal's rune band, once it has locked in ----
        if (age > 0.9f)
        {
            _shed.Update(
                time, ImGui.GetIO().DeltaTime,
                spawnIntervalMin: 0.45f, spawnIntervalMax: 0.9f,
                spawnPos: _shedPos, spawnVelocity: _shedVel,
                pickGlyph: static seed => Glyphs[(int)(DrawHelpers.Hash01(seed) * Glyphs.Length) % Glyphs.Length],
                lifespanMin: 1.6f, lifespanMax: 2.4f,
                sizeMin: 16f, sizeMax: 24f);
            _shed.DrawGlyphs(dl, time, pink, alpha, glow: 1.4f);
        }
    }

    // A shed letter is born on the rune band at a random angle and drifts outward across the bezel.
    // EdgeParticleField calls both delegates with the same seed, so they agree on the angle.
    private Vector2 ShedSpawnPos(int seed)
    {
        float ang = DrawHelpers.HashRange(seed, 0f, MathF.PI * 2f);
        float r = _sealRadius * DrawHelpers.HashRange(seed + 5, 0.82f, 0.90f);
        float size = DrawHelpers.HashRange(seed + 2, 16f, 24f); // same range/seed the field uses for this particle's size
        // text is top-left anchored, so nudge back by roughly half a glyph to centre it on the point
        return _sealCenter + DrawHelpers.V(MathF.Cos(ang) * r - size * 0.3f, MathF.Sin(ang) * r - size * 0.5f);
    }

    private Vector2 ShedSpawnVelocity(int seed)
    {
        float ang = DrawHelpers.HashRange(seed, 0f, MathF.PI * 2f);
        float speed = DrawHelpers.HashRange(seed + 6, 16f, 30f); // seed+1 is the lifespan's hash; a different offset keeps speed independent of it
        return DrawHelpers.V(MathF.Cos(ang) * speed, MathF.Sin(ang) * speed - 6f); // slight upward bias, like the edge letters
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
