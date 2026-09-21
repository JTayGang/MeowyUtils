using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// A curse seal locks onto you and your words leak out of it as raw magic. A large magic circle
/// (see <see cref="MagicCircle"/>) closes around the centre of the screen, and runes - the seal's own
/// script - drift up out of the screen edges, plus a few peeled straight off the seal's rune band
/// (see <see cref="RuneParticles"/>). Pair with Configuration.SilenceBlocksChat / ChatBlocker for an
/// actual chat lockout, not just the visual.
/// </summary>
public sealed class SilenceEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Silence;

    // Particles are centred on their spawn point, so spawn positions need to stay far enough from the true
    // screen edge on every side that the whole rune fits - otherwise particles born right at the edge
    // render mostly (or entirely) clipped off-screen, which reads as "nothing shows up" even though
    // particles genuinely are spawning. FootprintPx comfortably covers the largest rune (34px tall,
    // tumbling and swaying, plus its halo).
    private const float FootprintPx = 40f;
    private const float EdgeZone = 90f; // how deep into the screen, from each edge, particles can land

    // Rune heights in px (see RuneParticles). Runes are thin strokes, so they read best a little larger
    // than letters of the same nominal size would.
    private const float EdgeRuneMin = 22f, EdgeRuneMax = 34f;
    private const float ShedRuneMin = 20f, ShedRuneMax = 30f;

    // ---- the seal ----
    private const float SealRadiusFrac = 0.46f; // outer radius as a fraction of the SHORTER screen side (lower = smaller seal)
    private const float SealCenterY = 0.50f;    // 0 = top of screen, 1 = bottom
    private const float NewCastGapSeconds = 1.0f; // long enough that an ordinary frame hitch mid-fight never replays the cast-in

    private readonly EdgeParticleField _particles = new(maxParticles: 22, seedSalt: 0x511ECE);
    private readonly EdgeParticleField _shed = new(maxParticles: 10, seedSalt: 0x5E41ED);

    private MagicCircle? _seal;
    private MagicCircle.Palette _palette; // shared by the seal and the floating runes so they always match
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
        if (_seal == null)
        {
            _palette = new MagicCircle.Palette(
                halo: DrawHelpers.ToU32(0.62f, 0.16f, 0.95f, 1f),  // deep violet haze
                main: DrawHelpers.ToU32(0.95f, 0.32f, 0.88f, 1f),  // the bright pink of the lines
                core: DrawHelpers.ToU32(1.00f, 0.78f, 0.97f, 1f),  // hot highlights (spark heads)
                ink: DrawHelpers.ToU32(0.13f, 0.02f, 0.24f, 1f));  // dark under-stroke for contrast on bright scenes
            _seal = new MagicCircle(_palette, seed: 0x5EA1);
        }
        _seal.Draw(dl, _sealCenter, _sealRadius, age, time, alpha);

        // ---- runes drifting up out of the screen edges ----
        _particles.Update(
            time, ImGui.GetIO().DeltaTime,
            spawnIntervalMin: 0.1f, spawnIntervalMax: 0.2f, // was 0.2-0.4: half the wait = twice as many
            spawnPos: seed => RandomEdgePos(screenSize, seed),
            spawnVelocity: seed => DrawHelpers.V(DrawHelpers.HashRange(seed, -8f, 8f), DrawHelpers.HashRange(seed + 1, -30f, -12f)),
            pickGlyph: static seed => RuneParticles.PickToken(seed),
            lifespanMin: 1.4f, lifespanMax: 2.6f,
            sizeMin: EdgeRuneMin, sizeMax: EdgeRuneMax);
        RuneParticles.DrawField(dl, _particles, time, _palette, alpha);

        // ---- runes peeling off the seal's rune band, once it has locked in ----
        // These start oriented outward like the band's own runes (radial: true), then tumble away.
        if (age > 0.9f)
        {
            _shed.Update(
                time, ImGui.GetIO().DeltaTime,
                spawnIntervalMin: 0.45f, spawnIntervalMax: 0.9f,
                spawnPos: _shedPos, spawnVelocity: _shedVel,
                pickGlyph: static seed => RuneParticles.PickToken(seed),
                lifespanMin: 1.6f, lifespanMax: 2.4f,
                sizeMin: ShedRuneMin, sizeMax: ShedRuneMax);
            RuneParticles.DrawField(dl, _shed, time, _palette, alpha, radial: true, radialCenter: _sealCenter);
        }
    }

    // A shed rune is born on the rune band at a random angle and drifts outward across the bezel.
    // EdgeParticleField calls both delegates with the same seed, so they agree on the angle.
    private Vector2 ShedSpawnPos(int seed)
    {
        float ang = DrawHelpers.HashRange(seed, 0f, MathF.PI * 2f);
        float r = _sealRadius * DrawHelpers.HashRange(seed + 5, 0.82f, 0.90f); // the band's runes sit at ~0.86 of the radius
        return _sealCenter + DrawHelpers.V(MathF.Cos(ang) * r, MathF.Sin(ang) * r);
    }

    private Vector2 ShedSpawnVelocity(int seed)
    {
        float ang = DrawHelpers.HashRange(seed, 0f, MathF.PI * 2f);
        float speed = DrawHelpers.HashRange(seed + 6, 16f, 30f); // seed+1 is the lifespan's hash; a different offset keeps speed independent of it
        return DrawHelpers.V(MathF.Cos(ang) * speed, MathF.Sin(ang) * speed - 6f); // slight upward bias, like the edge runes
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
