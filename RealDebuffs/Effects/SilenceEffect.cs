using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// A curse seal locks onto you and your words leak out of it as raw magic. A large magic circle
/// (see <see cref="MagicCircle"/>) closes around the centre of the screen, and runes - the seal's own
/// script - drift up out of the screen edges, plus a few peeled straight off the seal's rune band
/// (see <see cref="RuneParticles"/>). Pair with Configuration.SilenceBlocksChat / ChatBlocker for an
/// actual chat lockout, not just the visual. MagicCircle/RuneParticles/VectorRunes below live in this
/// file because nothing else uses them - each is still a self-contained class to pull back out later.
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

/// <summary>
/// One animated "magic circle" seal, built entirely from lines: a graduated bezel, a band of rune
/// glyphs, a dashed ring, a big star polygon with glowing nodes (and sparks orbiting its ring), and
/// a small inner ring. Neighbouring layers spin in opposite directions at different speeds so the whole
/// thing feels like clockwork rather than a flat decal.
///
/// Everything is driven by two clocks:
///   <c>age</c>  - seconds since the seal was cast. The first ~1s is a "cast-in": the seal contracts
///                 onto the player, the rings sweep on, the star is traced in one stroke, the runes are
///                 written clockwise, and it all locks with a flash. After that it settles into...
///   <c>time</c> - the shared plugin clock: slow counter-rotation, a heartbeat throb, and runes that
///                 fade out and get rewritten as different glyphs.
///
/// Nothing allocates per frame, and every size is a fraction of <c>radius</c>, so it scales with
/// resolution. The centre (inside the inner ring) is deliberately left empty so the player's
/// character stays clearly visible in the eye of the seal.
/// </summary>
internal sealed class MagicCircle
{
    /// <summary>The four colors a seal is painted with. Swap these to reuse the same seal for a different debuff.</summary>
    public readonly struct Palette
    {
        public readonly uint Halo; // wide soft glow around the lines
        public readonly uint Main; // the body color of the lines
        public readonly uint Core; // small hot highlights (spark heads, node hearts)
        public readonly uint Ink;  // dark under-stroke so thin lines stay readable on bright scenes

        public Palette(uint halo, uint main, uint core, uint ink)
        {
            Halo = halo; Main = main; Core = core; Ink = ink;
        }
    }

    private const float Tau = MathF.PI * 2f;
    private const float Deg = MathF.PI / 180f;
    private const float Top = -MathF.PI / 2f; // rings start their sweep at 12 o'clock

    // ---- layout: every radius is a fraction of the seal's outer radius ----
    private const float RBezel = 1.000f;    // outer ring
    private const float RBezelIn = 0.955f;  // inner edge of the tick band
    private const float RRuneOut = 0.915f;  // rune band, outer border
    private const float RRuneMid = 0.860f;  // rune band, where glyphs are centred
    private const float RRuneIn = 0.805f;   // rune band, inner border
    private const float RDash = 0.752f;     // dashed ring
    private const float RStar = 0.690f;     // circle the star's tips touch (the sparks orbit on it too)
    private const float RCoreOut = 0.345f;  // inner ring; nothing is drawn inside this
    private const float RCoreIn = 0.318f;

    private const int RuneCount = 52;
    private const int DashCount = 12;
    // The star is the polygon {StarPoints/StarSkip}: 7/2 is a heptagram, the classic occult star.
    // Two rules if you change these:
    //  - they must be coprime (7/2, 5/2, 9/2, 8/3...). The star is traced as ONE continuous stroke,
    //    so something like 6/2 - which is really two separate triangles - would redraw one triangle
    //    and skip the other.
    //  - keep the star's inner lines outside the eye: RStar * cos(pi * StarSkip / StarPoints) should
    //    stay above RCoreOut. 7/2 gives 0.43 (fine); a pentagram (5/2) gives 0.21 and would cut
    //    through the middle of the screen.
    private const int StarPoints = 7;
    private const int StarSkip = 2;

    private static readonly float[] CometSpeed = { 38f * Deg, -26f * Deg, 64f * Deg };
    private static readonly float[] CometPhase = { 0f, 2.1f, 4.2f };

    private readonly Palette _pal;
    private readonly int _seed;

    // Per-frame state, set at the top of Draw so the layer helpers don't each need a dozen parameters.
    private ImDrawListPtr _dl;
    private Vector2 _c;
    private float _r;     // current outer radius in px (includes the cast-in scale)
    private float _px;    // line-weight scale, so strokes get proportionally thicker on 4K
    private float _a;     // master alpha (already includes the user's intensity slider)
    private float _beat;  // 0..1 heartbeat throb
    private float _flash; // 0..1 "locked in" flash right after the cast-in
    private float _glowA; // how strong the halos are (throbs with the heartbeat, spikes on the lock)
    private float _glowW; // how wide the halos are (moves far less than strength, or the flash turns into fat tubes)

    public MagicCircle(Palette palette, int seed)
    {
        _pal = palette;
        _seed = seed;
    }

    public void Draw(ImDrawListPtr dl, Vector2 center, float radius, float age, float time, float alpha)
    {
        if (radius < 8f || alpha <= 0.001f) return;

        _dl = dl;
        _c = center;
        _a = alpha;
        _px = Math.Clamp(radius / 480f, 0.8f, 2.4f);
        _beat = Heartbeat(time);
        _flash = LockFlash(age);

        // Cast-in: starts ~16% too big and closes onto the player. Contracting reads as "descending
        // onto you"; expanding would read as a burst going away from you.
        _r = radius * (1f + 0.16f * (1f - EaseOutCubic(Saturate(age / 1.0f))));

        // The layers spin up fast and decelerate into their steady speed, like tumblers locking.
        float settle = 1f - MathF.Exp(-age / 0.55f);
        float spin = 1.1f * settle;

        _glowA = 1f + 0.4f * _beat + 0.6f * _flash;
        _glowW = 1f + 0.2f * _beat + 0.35f * _flash;

        // ---- outer bezel: double ring with a graduated tick band between ----
        GlowRing(RBezel, EaseOutCubic(Seg(age, 0.00f, 0.60f)), Top, 2.3f * _px, 1f, 1f);
        Stroke(_r * RBezelIn, EaseOutCubic(Seg(age, 0.10f, 0.55f)), Top, C(_pal.Main, 0.6f), 1.1f * _px);
        Ticks(RBezelIn, RBezel, 72, 2f * Deg * time + spin * 0.3f, C(_pal.Main, 0.5f), 1f * _px, 6, 0.02f, Seg(age, 0.15f, 0.5f));

        // ---- rune band ----
        float pRune = EaseOutCubic(Seg(age, 0.10f, 0.6f));
        Stroke(_r * RRuneOut, pRune, Top, C(_pal.Main, 0.7f), 1.2f * _px);
        Stroke(_r * RRuneIn, pRune, Top, C(_pal.Main, 0.7f), 1.2f * _px);
        Runes(-5.5f * Deg * time - spin, age, time);

        // ---- dashed ring with orbiting sparks ----
        DashRing(9f * Deg * time + spin * 1.3f, age);
        Comets(age, time);

        // ---- the star ----
        Star(-7f * Deg * time - spin * 1.6f, age);

        // ---- inner ring: the clear "eye" of the seal ----
        float pCore = EaseOutCubic(Seg(age, 0.50f, 0.45f));
        GlowRing(RCoreOut, pCore, Top, 1.6f * _px, 0.7f, 0.9f);
        Stroke(_r * RCoreIn, pCore, Top, C(_pal.Main, 0.5f), 1f * _px);
        Ticks(RCoreIn, RCoreOut, 36, 16f * Deg * time + spin * 2f, C(_pal.Main, 0.55f), 1f * _px, 0, 0f, Seg(age, 0.55f, 0.4f));

        // ---- lock ripple: one thin ring breathes outward as the seal snaps shut ----
        float k = Saturate((age - 1f) / 0.75f);
        if (k > 0f && k < 1f)
        {
            float rr = _r * (1f + 0.12f * EaseOutCubic(k));
            _dl.AddCircle(_c, rr, C(_pal.Main, 0.7f * (1f - k)), SegsFor(rr), 1.6f * _px);
        }
    }

    // =====================================================================================
    // Layers
    // =====================================================================================

    private void Runes(float rot, float age, float time)
    {
        float rMid = _r * RRuneMid;
        float size = _r * 0.066f; // glyph height in px; glyphs are ~0.6x as wide as they are tall

        for (int i = 0; i < RuneCount; i++)
        {
            // Cast-in: written one after another, clockwise from the top.
            float appear = Saturate((age - (0.35f + 0.65f * i / RuneCount)) / 0.22f);
            if (appear <= 0f) continue;

            // Each rune runs its own life cycle: fade in fast, hold, fade out slower, then come back as a
            // different glyph. The swap happens while it's fully faded out, so you never see it pop.
            int h = unchecked(_seed + i * 7919);
            float period = DrawHelpers.HashRange(h, 3.4f, 7.2f);
            float cyc = time / period + DrawHelpers.Hash01(h + 1);
            float cycFloor = MathF.Floor(cyc);
            float u = cyc - cycFloor;
            float life = Saturate(u / 0.12f) * Saturate((1f - u) / 0.28f);

            float ang = rot + Tau * i / RuneCount;

            // A slow highlight sweeps around the band, like power running through the inscription.
            float dd = MathF.Abs(MathF.IEEERemainder(ang - time * 0.8f, Tau));
            float sweep = MathF.Exp(-dd * dd / 0.16f);

            float bright = life * appear * (0.6f + 0.4f * sweep);

            int glyph = (int)(DrawHelpers.Hash01(unchecked(h + 2 + (int)cycFloor * 131)) * VectorRunes.Glyphs.Length);
            DrawRune(ang, rMid, size, glyph % VectorRunes.Glyphs.Length, bright);
        }
    }

    private void DrawRune(float ang, float rMid, float size, int glyph, float bright)
    {
        if (bright <= 0.03f) return;

        float ca = MathF.Cos(ang), sa = MathF.Sin(ang);
        var radial = new Vector2(ca, sa);   // glyph "up" = away from the centre
        var tangent = new Vector2(-sa, ca); // glyph "right" = clockwise along the ring
        var origin = new Vector2(_c.X + ca * rMid, _c.Y + sa * rMid);

        uint ink = C(_pal.Ink, 0.26f * bright);
        uint halo = C(_pal.Halo, 0.22f * bright);
        uint core = C(_pal.Main, 0.95f * bright);
        float inkW = 3.0f * _px, haloW = 4.2f * _px, coreW = 1.5f * _px;

        var strokes = VectorRunes.Glyphs[glyph];
        for (int s = 0; s < strokes.Length; s++)
        {
            var pts = strokes[s];
            Vector2 prev = Map(origin, tangent, radial, size, pts[0], pts[1]);
            for (int k = 2; k < pts.Length; k += 2)
            {
                Vector2 p = Map(origin, tangent, radial, size, pts[k], pts[k + 1]);
                _dl.AddLine(prev, p, ink, inkW);
                _dl.AddLine(prev, p, halo, haloW);
                _dl.AddLine(prev, p, core, coreW);
                prev = p;
            }
        }
    }

    private void DashRing(float rot, float age)
    {
        float r = _r * RDash;
        float p = EaseOutCubic(Seg(age, 0.20f, 0.60f));
        if (p <= 0f) return;

        // a faint continuous track for the dashes to ride on
        Stroke(r, p, Top, C(_pal.Main, 0.28f), 1f * _px);

        float span = Tau / DashCount;
        float on = span * 0.60f;
        for (int i = 0; i < DashCount; i++)
        {
            float vis = Saturate(p * DashCount - i);
            if (vis <= 0f) break;

            float a0 = rot + span * i;
            Arc(r, a0, a0 + on * vis, C(_pal.Halo, 0.18f), 7f * _px);
            Arc(r, a0, a0 + on * vis, C(_pal.Main, 0.92f), 2.4f * _px);

            // a small bead in each gap
            if (vis >= 1f)
                _dl.AddCircleFilled(Polar(r, a0 + on + (span - on) * 0.5f), 2.2f * _px, C(_pal.Core, 0.75f));
        }
    }

    private void Comets(float age, float time)
    {
        float appear = Saturate((age - 0.8f) / 0.3f);
        if (appear <= 0f) return;

        float r = _r * RStar; // the star ring is thin and bare, so a comet reads clearly on it (and slips behind the nodes)
        const int trail = 13;
        const float step = 0.05f; // radians between trail samples

        for (int k = 0; k < CometSpeed.Length; k++)
        {
            float head = CometPhase[k] + CometSpeed[k] * time;
            float dir = CometSpeed[k] >= 0f ? 1f : -1f;

            for (int j = trail; j >= 1; j--)
            {
                float f = 1f - (float)j / (trail + 1);
                Vector2 a = Polar(r, head - dir * j * step);
                Vector2 b = Polar(r, head - dir * (j - 1) * step);
                _dl.AddLine(a, b, C(_pal.Main, 0.85f * f * f * appear), (1f + 3f * f) * _px);
            }

            Vector2 hp = Polar(r, head);
            _dl.AddCircleFilled(hp, 9f * _px, C(_pal.Halo, 0.30f * appear));
            _dl.AddCircleFilled(hp, 5f * _px, C(_pal.Main, 0.55f * appear));
            _dl.AddCircleFilled(hp, 3f * _px, C(_pal.Core, 1f * appear));
        }
    }

    private void Star(float rot, float age)
    {
        float r = _r * RStar;
        float px = _px;

        // the circle the star's tips touch, sweeping on just before the star is traced
        Stroke(r, EaseOutCubic(Seg(age, 0.30f, 0.50f)), Top, C(_pal.Main, 0.55f), 1.2f * px);

        float p = Seg(age, 0.40f, 0.65f);
        if (p <= 0f) return;

        Span<Vector2> v = stackalloc Vector2[StarPoints];
        for (int i = 0; i < StarPoints; i++)
            v[i] = Polar(r, rot + Tau * i / StarPoints);

        // One continuous stroke: each edge jumps StarSkip vertices ahead, so the pen visits every
        // vertex once and returns home. It's traced in that order during the cast-in.
        for (int j = 0; j < StarPoints; j++)
        {
            float e = Saturate(p * StarPoints - j);
            if (e <= 0f) break;

            Vector2 a = v[(j * StarSkip) % StarPoints];
            Vector2 b = v[((j + 1) * StarSkip) % StarPoints];
            Vector2 tip = Vector2.Lerp(a, b, EaseOutCubic(e));

            _dl.AddLine(a, tip, C(_pal.Ink, 0.28f), 4.4f * px);
            _dl.AddLine(a, tip, C(_pal.Halo, 0.035f * _glowA), 19f * px * _glowW);
            _dl.AddLine(a, tip, C(_pal.Halo, 0.06f * _glowA), 11f * px * _glowW);
            _dl.AddLine(a, tip, C(_pal.Halo, 0.10f * _glowA), 6f * px * _glowW);
            _dl.AddLine(a, tip, C(_pal.Main, 0.95f), 2.1f * px);
            _dl.AddLine(a, tip, C(_pal.Core, 0.45f), 0.9f * px);
        }

        // Spokes from the inner ring out to the star's waist, tying the two together.
        float rWaist = r * MathF.Cos(MathF.PI * StarSkip / StarPoints) / MathF.Cos(MathF.PI * (StarSkip - 1) / StarPoints);
        float spokeVis = Saturate((p - 0.6f) / 0.4f);
        if (spokeVis > 0f)
        {
            for (int i = 0; i < StarPoints; i++)
            {
                float ang = rot + Tau * (i + 0.5f) / StarPoints;
                Vector2 a = Polar(_r * RCoreOut, ang);
                Vector2 b = Polar(rWaist, ang);
                _dl.AddLine(a, b, C(_pal.Main, 0.32f * spokeVis), 1f * px);
                _dl.AddCircleFilled(b, 2.4f * px, C(_pal.Core, 0.7f * spokeVis));
            }
        }

        // Nodes on every tip: a ring, a heart that throbs with the heartbeat, and (at the lock) a ping.
        float ping = Saturate((age - 1f) / 0.6f);
        for (int j = 0; j < StarPoints; j++)
        {
            float vis = Saturate((p * StarPoints - (j - 0.1f)) * 4f);
            if (vis <= 0f) continue;

            Vector2 nc = v[(j * StarSkip) % StarPoints];
            float nr = _r * 0.026f;
            _dl.AddCircle(nc, nr * 1.9f, C(_pal.Halo, 0.35f * vis), 0, 1.2f * px);
            _dl.AddCircleFilled(nc, nr, C(_pal.Ink, 0.55f * vis));
            _dl.AddCircle(nc, nr, C(_pal.Main, 0.95f * vis), 0, 1.7f * px);
            _dl.AddCircleFilled(nc, nr * (0.42f + 0.2f * _beat), C(_pal.Core, vis));

            if (ping > 0f && ping < 1f)
                _dl.AddCircle(nc, nr * (1f + 3.2f * EaseOutCubic(ping)), C(_pal.Main, 0.8f * (1f - ping)), 0, 1.5f * px);
        }
    }

    // =====================================================================================
    // Drawing primitives
    // =====================================================================================

    /// <summary>
    /// A ring with the full treatment: a dark under-stroke, a haze built from three stacked translucent
    /// bands (wide+faint -> narrow+stronger, which approximates a soft falloff), then the colored core.
    /// <paramref name="scale"/> shrinks the whole treatment for secondary rings.
    /// </summary>
    private void GlowRing(float rFrac, float progress, float startAng, float coreThick, float scale, float aBase)
    {
        if (progress <= 0.001f) return;

        float r = _r * rFrac;
        float ga = _glowA * scale * aBase, gw = _glowW * scale;
        Stroke(r, progress, startAng, C(_pal.Ink, 0.30f * aBase), coreThick + 2.6f * _px);
        Stroke(r, progress, startAng, C(_pal.Halo, 0.035f * ga), coreThick + 22f * _px * gw);
        Stroke(r, progress, startAng, C(_pal.Halo, 0.06f * ga), coreThick + 13f * _px * gw);
        Stroke(r, progress, startAng, C(_pal.Halo, 0.10f * ga), coreThick + 7f * _px * gw);
        Stroke(r, progress, startAng, C(_pal.Main, 0.14f * ga), coreThick + 3.6f * _px);
        Stroke(r, progress, startAng, C(_pal.Main, aBase), coreThick);
    }

    /// <summary>Full circle in one native call once it's fully drawn; a chain of short segments while it's still sweeping on.</summary>
    private void Stroke(float r, float progress, float startAng, uint col, float thick)
    {
        if (progress <= 0.001f) return;
        if (progress >= 0.999f) { _dl.AddCircle(_c, r, col, SegsFor(r), thick); return; }
        Arc(r, startAng, startAng + Tau * progress, col, thick);
    }

    private void Arc(float r, float a0, float a1, uint col, float thick)
    {
        float sweep = a1 - a0;
        if (MathF.Abs(sweep) < 0.0005f) return;

        int n = Math.Max(2, (int)MathF.Ceiling(MathF.Abs(sweep) / Tau * SegsFor(r)));

        Vector2 prev = Polar(r, a0);
        for (int i = 1; i <= n; i++)
        {
            Vector2 p = Polar(r, a0 + sweep * i / n);
            _dl.AddLine(prev, p, col, thick);
            prev = p;
        }
    }

    private void Ticks(float r0Frac, float r1Frac, int count, float rot, uint col, float thick, int longEvery, float longExtraFrac, float progress)
    {
        int n = (int)MathF.Ceiling(count * Saturate(progress));
        float r0 = _r * r0Frac, r1 = _r * r1Frac, extra = _r * longExtraFrac;

        for (int i = 0; i < n; i++)
        {
            float ang = rot + Tau * i / count;
            float start = (longEvery > 0 && i % longEvery == 0) ? r0 - extra : r0;
            _dl.AddLine(Polar(start, ang), Polar(r1, ang), col, thick);
        }
    }

    /// <summary>
    /// Segments used for a full circle of this radius. ImGui's own auto count is very conservative
    /// (a 500px ring gets ~180 segments); this keeps the error under ~0.3px, which is invisible, at
    /// roughly half the vertices. The same count is used for arcs, so a ring finishing its sweep
    /// doesn't visibly change shape when it hands over to a single circle call.
    /// </summary>
    private static int SegsFor(float r) => Math.Clamp((int)(r * 0.2f), 24, 128);

    private Vector2 Polar(float r, float ang) => new(_c.X + MathF.Cos(ang) * r, _c.Y + MathF.Sin(ang) * r);

    /// <summary>Maps a glyph-local point (unit box, see VectorRunes) into world space. Internal so RuneParticles' floating runes can share it too.</summary>
    internal static Vector2 Map(Vector2 origin, Vector2 axisX, Vector2 axisY, float size, float x, float y) =>
        origin + axisX * (x * size) + axisY * (y * size);

    /// <summary>Color with its alpha scaled by both the given amount and the seal's master alpha.</summary>
    private uint C(uint color, float a) => DrawHelpers.WithAlpha(color, a * _a);

    // =====================================================================================
    // Timing helpers
    // =====================================================================================

    /// <summary>Internal so RuneParticles can share it too, instead of keeping its own copy.</summary>
    internal static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    /// <summary>0 until <paramref name="start"/>, then ramps to 1 over <paramref name="dur"/> seconds.</summary>
    private static float Seg(float age, float start, float dur) => Saturate((age - start) / dur);

    internal static float EaseOutCubic(float t) { float u = 1f - Saturate(t); return 1f - u * u * u; }

    /// <summary>A slow "lub-dub" throb: two quick bumps, then a long rest. (Internal so RuneParticles can pulse in time with the seal.)</summary>
    internal static float Heartbeat(float time)
    {
        const float period = 1.9f;
        float p = (time % period) / period;
        return MathF.Min(1f, Bump(p, 0.06f, 0.035f) + 0.65f * Bump(p, 0.22f, 0.04f));
    }

    private static float Bump(float x, float center, float width)
    {
        float d = (x - center) / width;
        return MathF.Exp(-d * d);
    }

    /// <summary>Snaps to 1 just as the cast-in finishes (~1s), then decays: the "locked in" flash.</summary>
    private static float LockFlash(float age) =>
        Saturate((age - 0.92f) / 0.08f) * MathF.Exp(-MathF.Max(0f, age - 1f) / 0.25f);
}

/// <summary>
/// Floating runes: the seal's own script (<see cref="VectorRunes"/>) drawn as drifting particles, as an
/// alternative to drifting text letters. Because a glyph here is a set of strokes rather than a font
/// character, it can do things text can't:
///   - it is INSCRIBED when it appears: a bright spark travels along the strokes, writing them, and the
///     finished rune flashes white-hot for a moment (a small echo of the seal's own lock flash);
///   - it tumbles slowly and sways as it drifts;
///   - its glow throbs in time with the seal's heartbeat, so the whole curse pulses as one;
///   - at the end of its life it is WIPED AWAY: a spark burns along the strokes from where it began.
///
/// The particles themselves (spawning, drifting, expiry) are still an <see cref="EdgeParticleField"/>;
/// this class only replaces how a particle is drawn, using the indexer that class provides for custom
/// shapes. The glyph rides along in the particle's string as a one-character token (see
/// <see cref="PickToken"/>) and every other per-particle random (tilt, spin, sway) is derived from the
/// particle's spawn time, so nothing in the shared particle code had to change.
/// </summary>
internal static class RuneParticles
{
    private const float WriteSeconds = 0.42f; // how long the inscribing takes
    private const float EraseSeconds = 0.55f; // how long the wipe at the end of a particle's life takes

    private static readonly string[] Tokens = MakeTokens();
    private static readonly float[] Lengths = MakeLengths(); // total stroke length of each glyph, in glyph units

    /// <summary>For EdgeParticleField's pickGlyph: a random rune, as a one-character token ('A' = first glyph, 'B' = second...).</summary>
    public static string PickToken(int seed) =>
        Tokens[Math.Min(Tokens.Length - 1, (int)(DrawHelpers.Hash01(seed) * Tokens.Length))];

    /// <summary>
    /// Draws every live particle in <paramref name="field"/> as a rune. Runes normally stand roughly upright
    /// with a random tilt; with <paramref name="radial"/> they instead start oriented away from
    /// <paramref name="radialCenter"/>, exactly like the runes on the seal's band, so ones that peel off
    /// the seal read as the band's own runes drifting away.
    /// </summary>
    public static void DrawField(
        ImDrawListPtr dl, EdgeParticleField field, float time, in MagicCircle.Palette pal, float alpha,
        bool radial = false, Vector2 radialCenter = default)
    {
        float beat = MagicCircle.Heartbeat(time);

        for (int i = 0; i < field.Count; i++)
        {
            ref readonly var p = ref field[i];
            float age = time - p.Born;
            if (age < 0f || age >= p.Lifespan) continue;

            // Everything random about this particle comes from its spawn time, so it's stable for its whole life.
            int h = BitConverter.SingleToInt32Bits(p.Born);

            float rot = radial
                ? MathF.Atan2(p.Pos.Y - radialCenter.Y, p.Pos.X - radialCenter.X) + MathF.PI * 0.5f // "up" = away from the seal
                : DrawHelpers.HashRange(h + 11, -0.45f, 0.45f);                                     // roughly upright, tilted

            float spin = DrawHelpers.HashRange(h + 12, 0.10f, 0.30f) * (DrawHelpers.Hash01(h + 13) < 0.5f ? -1f : 1f);
            rot += spin * age; // radians per second: a slow tumble

            float sway = MathF.Sin(age * 2.4f + DrawHelpers.HashRange(h + 14, 0f, MathF.PI * 2f)) * 5f;

            float write = MagicCircle.Saturate(age / WriteSeconds);
            float erase = MagicCircle.Saturate((age - (p.Lifespan - EraseSeconds)) / EraseSeconds);
            float bloom = age > WriteSeconds ? MathF.Exp(-(age - WriteSeconds) / 0.14f) : 0f; // 1 the instant it's fully written, then fades

            DrawOne(dl, new Vector2(p.Pos.X + sway, p.Pos.Y), p.Size, rot, GlyphIndex(p.Glyph), write, erase, bloom, beat, pal, alpha);
        }
    }

    /// <summary>
    /// Draws one rune. <paramref name="write"/> (0..1) is how far the inscribing spark has travelled along the
    /// glyph's strokes and <paramref name="erase"/> (0..1) is how far the wiping spark has followed it, so
    /// 1 and 0 mean "fully written, not yet erased". <paramref name="bloom"/> (0..1) briefly swells the glow
    /// as the rune completes. <paramref name="size"/> is the glyph's height in px and <paramref name="rot"/>
    /// rotates it (0 = upright, radians, clockwise on screen).
    /// </summary>
    public static void DrawOne(
        ImDrawListPtr dl, Vector2 center, float size, float rot, int glyph,
        float write, float erase, float bloom, float beat, in MagicCircle.Palette pal, float alpha)
    {
        if ((uint)glyph >= (uint)VectorRunes.Glyphs.Length || size <= 0f) return;

        float total = Lengths[glyph];
        float to = MagicCircle.EaseOutCubic(write) * total;              // the inscribing spark
        float from = erase * erase * (3f - 2f * erase) * total; // the wiping spark (smoothstep)
        if (to - from <= 0.0001f) return;

        float fade = 1f - erase * erase; // what's left of a rune dims as it's wiped
        float coreW = Math.Clamp(size * 0.056f, 1.3f, 2.0f);

        // Same three-pass treatment as the runes on the seal's band: dark under-stroke, violet halo, colored core.
        // The halo is deliberately no wider than on the band's own runes (~4px): a fatter halo swamps the thin
        // pink core and the runes turn violet, which stops them looking like the seal's script. It swells with
        // the seal's heartbeat, and blooms as a rune finishes being written.
        uint ink = Tint(pal.Ink, 0.28f * fade, alpha);
        uint halo = Tint(pal.Halo, 0.24f * (1f + 0.8f * beat + 0.6f * bloom) * fade, alpha);
        uint core = Tint(pal.Main, (0.92f + 0.08f * beat) * fade, alpha);
        uint hot = Tint(pal.Core, 0.95f * bloom * fade, alpha); // the ignition: the finished rune flashes white-hot, then settles to pink
        float inkW = coreW + 2.0f, haloW = coreW + 3.4f + 1.4f * bloom;

        float sr = MathF.Sin(rot), cr = MathF.Cos(rot);
        var right = new Vector2(cr, sr);   // glyph x axis
        var up = new Vector2(sr, -cr);     // glyph +y axis ("up" for rot = 0)

        var strokes = VectorRunes.Glyphs[glyph];
        float acc = 0f; // distance travelled along the glyph so far
        Vector2 writeTip = default, eraseTip = default;
        bool haveWrite = false, haveErase = false;

        for (int s = 0; s < strokes.Length; s++)
        {
            var pts = strokes[s];
            float ax = pts[0], ay = pts[1];
            for (int k = 2; k < pts.Length; k += 2)
            {
                float bx = pts[k], by = pts[k + 1];
                float dx = bx - ax, dy = by - ay;
                float len = MathF.Sqrt(dx * dx + dy * dy);
                float s0 = acc, s1 = acc + len;
                acc = s1;

                if (len > 1e-5f)
                {
                    // the slice of this segment that's currently visible: written by now, not yet wiped
                    float t0 = MagicCircle.Saturate((from - s0) / len);
                    float t1 = MagicCircle.Saturate((to - s0) / len);
                    if (t1 - t0 > 1e-4f)
                    {
                        Vector2 p0 = MagicCircle.Map(center, right, up, size, ax + dx * t0, ay + dy * t0);
                        Vector2 p1 = MagicCircle.Map(center, right, up, size, ax + dx * t1, ay + dy * t1);
                        dl.AddLine(p0, p1, ink, inkW);
                        dl.AddLine(p0, p1, halo, haloW);
                        dl.AddLine(p0, p1, core, coreW);
                        if (bloom > 0.04f) dl.AddLine(p0, p1, hot, coreW * 0.6f);
                    }

                    if (!haveWrite && to >= s0 && to <= s1)
                    {
                        float t = (to - s0) / len;
                        writeTip = MagicCircle.Map(center, right, up, size, ax + dx * t, ay + dy * t);
                        haveWrite = true;
                    }
                    if (!haveErase && from >= s0 && from <= s1)
                    {
                        float t = (from - s0) / len;
                        eraseTip = MagicCircle.Map(center, right, up, size, ax + dx * t, ay + dy * t);
                        haveErase = true;
                    }
                }

                ax = bx; ay = by;
            }
        }

        // The sparks: a hot core inside a soft three-step glow (a single flat disc reads as a lollipop).
        float sparkR = MathF.Max(1.4f, size * 0.055f);
        if (write < 1f && haveWrite)
            Spark(dl, writeTip, sparkR, 1f, pal, alpha);
        if (erase > 0f && erase < 1f && haveErase)
            Spark(dl, eraseTip, sparkR * 0.9f, 0.9f * fade, pal, alpha);
    }

    private static void Spark(ImDrawListPtr dl, Vector2 at, float r, float strength, in MagicCircle.Palette pal, float alpha)
    {
        dl.AddCircleFilled(at, r * 2.3f, Tint(pal.Halo, 0.14f * strength, alpha));
        dl.AddCircleFilled(at, r * 1.6f, Tint(pal.Main, 0.34f * strength, alpha));
        dl.AddCircleFilled(at, r, Tint(pal.Core, strength, alpha));
    }

    // ---------------------------------------------------------------------------------------------

    private static uint Tint(uint color, float a, float alpha) => DrawHelpers.WithAlpha(color, a * alpha);

    private static int GlyphIndex(string? token)
    {
        if (string.IsNullOrEmpty(token)) return 0;
        int i = token[0] - 'A';
        return (uint)i < (uint)Lengths.Length ? i : 0;
    }

    private static string[] MakeTokens()
    {
        var t = new string[VectorRunes.Glyphs.Length];
        for (int i = 0; i < t.Length; i++) t[i] = ((char)('A' + i)).ToString();
        return t;
    }

    private static float[] MakeLengths()
    {
        var glyphs = VectorRunes.Glyphs;
        var r = new float[glyphs.Length];
        for (int g = 0; g < glyphs.Length; g++)
        {
            float sum = 0f;
            foreach (var pts in glyphs[g])
                for (int k = 2; k < pts.Length; k += 2)
                {
                    float dx = pts[k] - pts[k - 2], dy = pts[k + 1] - pts[k - 1];
                    sum += MathF.Sqrt(dx * dx + dy * dy);
                }
            r[g] = sum;
        }
        return r;
    }
}

/// <summary>
/// A small alphabet of rune-like glyphs stored as line strokes instead of font characters.
///
/// Why strokes: ImGui can only draw text axis-aligned, so text can't follow a rotating ring. Strokes
/// are just points, so they rotate and scale for free - and, like the plain-ASCII particles in
/// SilenceEffect, they can never show up as missing-glyph tofu boxes whatever fonts Dalamud has loaded.
///
/// Each glyph is a list of polylines in a unit box: x runs -0.3..0.3 across the glyph, y runs
/// -0.5..0.5 with +y meaning "up" (which becomes "outward" when a glyph is set on a ring).
/// The shapes are straight-line, Elder-Futhark-flavored - deliberately angular so they read as
/// carved script rather than handwriting. Add or tweak glyphs freely; nothing else depends on the count.
/// </summary>
internal static class VectorRunes
{
    private static float[] S(params float[] xy) => xy;
    private static float[][] G(params float[][] strokes) => strokes;

    public static readonly float[][][] Glyphs =
    {
        G(S(0, -.5f, 0, .5f), S(0, -.05f, .3f, .25f), S(0, .2f, .3f, .5f)),                       // branching stem
        G(S(-.24f, -.5f, -.24f, .5f, .24f, .2f, .24f, -.5f)),                                       // gate
        G(S(-.2f, -.5f, -.2f, .5f), S(-.2f, .28f, .24f, 0, -.2f, -.28f)),                           // stem + wedge
        G(S(-.12f, -.5f, -.12f, .5f), S(-.12f, .5f, .26f, .24f), S(-.12f, .16f, .26f, -.1f)),        // stem + two flags
        G(S(-.2f, -.5f, -.2f, .5f, .22f, .24f, -.2f, 0, .24f, -.5f)),                               // flagged leg
        G(S(.24f, .42f, -.22f, 0, .24f, -.42f)),                                                    // chevron
        G(S(-.26f, .42f, .26f, -.42f), S(-.26f, -.42f, .26f, .42f)),                                // cross
        G(S(-.2f, -.5f, -.2f, .5f, .22f, .26f, -.2f, 0)),                                           // pennant
        G(S(-.22f, -.5f, -.22f, .5f), S(.22f, -.5f, .22f, .5f), S(-.22f, .18f, .22f, -.18f)),        // ladder
        G(S(0, -.5f, 0, .5f), S(-.26f, .16f, .26f, -.16f)),                                         // slashed stem
        G(S(0, -.5f, 0, .5f), S(-.22f, .2f, 0, .5f, .22f, .2f)),                                    // arrow
        G(S(0, -.5f, 0, .5f), S(0, .5f, .26f, .26f), S(0, -.5f, -.26f, -.26f)),                     // hooked stem
        G(S(-.2f, -.5f, -.2f, .5f), S(-.2f, .5f, .24f, .25f, -.2f, 0, .24f, -.25f, -.2f, -.5f)),     // double lobe
        G(S(0, -.5f, 0, .5f), S(-.28f, .46f, 0, .06f, .28f, .46f)),                                 // trident
        G(S(.2f, .5f, -.2f, .1f, .2f, -.1f, -.2f, -.5f)),                                           // bolt
        G(S(0, .46f, .26f, 0, 0, -.46f, -.26f, 0, 0, .46f)),                                        // diamond
        G(S(-.28f, .42f, -.28f, -.42f, .28f, .42f, .28f, -.42f, -.28f, .42f)),                       // bowtie
        G(S(0, .5f, .25f, .2f, 0, -.1f, -.25f, .2f, 0, .5f), S(-.26f, -.5f, 0, -.1f, .26f, -.5f)),   // diamond on legs
        G(S(-.24f, -.5f, -.24f, .5f, 0, .14f, .24f, .5f, .24f, -.5f)),                              // crown
        G(S(-.1f, -.5f, -.1f, .5f, .24f, .2f)),                                                     // flag
        G(S(-.04f, .5f, -.26f, .18f, -.04f, -.08f), S(.04f, .08f, .26f, -.18f, .04f, -.5f)),         // twin hooks
    };
}
