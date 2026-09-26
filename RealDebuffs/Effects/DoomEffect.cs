using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Doom: a demonic mark of death is branded onto you. A blood-and-hellfire seal - a recolored
/// twin of <see cref="MagicCircle"/>, the very seal Silence uses, just repainted - locks onto the
/// centre of the screen while the world itself cracks open around it: jagged fractures radiate
/// outward from the seal toward every edge of the screen, glowing like exposed magma, growing
/// outward during the cast-in as if the ground were splitting apart in real time. The seal's own
/// script (<see cref="RuneParticles"/> / <see cref="VectorRunes"/>) drifts up out of the screen
/// edges in the same recolored palette, and a scattering of embers rises from the bottom, so
/// nothing here reads as "Silence but red" without also reading as its own, heavier, more
/// infernal thing.
///
/// Everything breathes together on the seal's own heartbeat: vignette strength, crack-glow, and
/// rune glow all throb in the same slow rhythm, like something counting down toward you.
///
/// Once the seal locks (~1s in), a slow shockwave "toll" starts ringing out from the seal to the
/// screen's far corners, repeating for as long as the curse holds. The first toll fires the
/// instant the seal locks and doubles as a hard, dark flash - the moment the brand is seared in -
/// and every one after is fainter: an ongoing reminder rather than a fresh shock.
/// </summary>
public sealed class DoomEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Doom;

    // ---- palette: cracks, embers and the vignette. The seal itself and its drifting runes use
    // MagicCircle.Palette (built alongside the seal below), so their colors live there instead. ----
    private static readonly uint VoidBlack    = DrawHelpers.ToU32(0.030f, 0.006f, 0.010f, 1f);
    private static readonly uint BloodDeep    = DrawHelpers.ToU32(0.260f, 0.014f, 0.018f, 1f);
    private static readonly uint BloodMain    = DrawHelpers.ToU32(0.720f, 0.055f, 0.048f, 1f);
    private static readonly uint HellfireCore = DrawHelpers.ToU32(1.000f, 0.460f, 0.100f, 1f);
    private static readonly uint HellfireHot  = DrawHelpers.ToU32(1.000f, 0.860f, 0.560f, 1f);

    // ---- the seal ----
    private const float SealRadiusFrac = 0.40f; // a touch smaller than Silence's, to leave room for the cracks around it
    private const float SealCenterY = 0.50f;
    private const float NewCastGapSeconds = 1.0f; // long enough that an ordinary frame hitch mid-fight never replays the cast-in
    private const float LockTime = 0.95f;         // roughly when the seal's own internal lock-flash fires

    // =====================================================================================
    // Cracks: a web of fractures radiating outward from the seal toward the screen's edges.
    // Rebuilt fresh on every new cast (like Bind's tendrils / Heavy's chains) so the pattern the
    // world splits along is never quite the same twice.
    // =====================================================================================
    private const int CrackCount = 10;

    private struct Crack
    {
        public float Angle;        // direction from the seal, radians
        public float Length;       // how far it reaches beyond the seal, as a fraction of shortSide
        public float AngleJitter;  // per-segment wander, radians
        public int   Segments;
        public int   Seed;
        public float Delay;        // seconds after cast-start before this crack starts growing
        public float GrowDuration;
        public float Alpha;
        public float BranchChance;
        public float Width;
    }

    private readonly Crack[] _cracks = new Crack[CrackCount];

    // =====================================================================================
    // Drifting runes: the seal's own script, recolored - see RuneParticles/VectorRunes in
    // SilenceEffect.cs. Some drift up out of the screen edges, the rest peel straight off the
    // seal's rune band once it has locked in, exactly like Silence's own two rune fields.
    // =====================================================================================
    private const float EdgeRuneMin = 24f, EdgeRuneMax = 38f;
    private const float ShedRuneMin = 22f, ShedRuneMax = 32f;

    private readonly EdgeParticleField _runes = new(maxParticles: 14, seedSalt: 0x00D00D);
    private readonly EdgeParticleField _shed  = new(maxParticles: 8,  seedSalt: 0x00D00E);

    // ---- embers rising out of the cracked ground ----
    private readonly EdgeParticleField _embers = new(maxParticles: 30, seedSalt: 0x00D00F);

    // ---- the toll: a slow shockwave, repeating, that rings out from the seal once it locks ----
    private const float TollPeriod = 2.6f;
    private const float TollTravel = 2.0f;

    private MagicCircle? _seal;
    private MagicCircle.Palette _palette; // shared by the seal and its drifting runes so they always match

    private float _lastDrawTime = -100f;
    private float _castStart;
    private Vector2 _screenSize;
    private Vector2 _sealCenter;
    private float _sealRadius;

    // ---- cached spawn delegates (no per-frame closures) ----
    private readonly Func<int, Vector2> _edgeRunePos;
    private readonly Func<int, Vector2> _edgeRuneVel;
    private readonly Func<int, Vector2> _shedPos;
    private readonly Func<int, Vector2> _shedVel;
    private readonly Func<int, Vector2> _emberPos;
    private readonly Func<int, Vector2> _emberVel;
    private readonly Func<int, string>  _pickRune;
    private readonly Func<int, string>  _noGlyph;

    public DoomEffect()
    {
        _edgeRunePos = EdgeRuneSpawnPos;
        _edgeRuneVel = EdgeRuneSpawnVelocity;
        _shedPos     = ShedSpawnPos;
        _shedVel     = ShedSpawnVelocity;
        _emberPos    = EmberSpawnPos;
        _emberVel    = EmberSpawnVelocity;
        _pickRune    = static seed => RuneParticles.PickToken(seed);
        _noGlyph     = static _ => "";
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float dt = ImGui.GetIO().DeltaTime;
        _screenSize = screenSize;

        // EffectManager stops calling Draw once the effect has fully faded out, so a gap since the
        // last call means the debuff was just (re)applied: restart the cast-in and re-fracture the world.
        if (time - _lastDrawTime > NewCastGapSeconds)
        {
            _castStart = time;
            BuildCracks(unchecked((int)(_castStart * 1000f)));
        }
        _lastDrawTime = time;
        float age = time - _castStart;

        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        float px = Math.Clamp(shortSide / 1000f, 0.8f, 2.2f); // line-weight scale, so strokes stay proportional at any resolution
        float beat = MagicCircle.Heartbeat(time);

        _sealRadius = shortSide * SealRadiusFrac;
        _sealCenter = new Vector2(screenSize.X * 0.5f, screenSize.Y * SealCenterY);

        // ---- vignette: a near-black outer edge bleeding into deep blood-red, breathing with the heartbeat ----
        float vignettePulse = 0.65f + 0.35f * beat;
        DrawHelpers.DrawVignette(dl, screenSize, VoidBlack, 0.24f, alpha * (0.55f + 0.20f * vignettePulse));
        DrawHelpers.DrawVignette(dl, screenSize, BloodDeep, 0.15f, alpha * (0.35f + 0.30f * vignettePulse));

        // ---- cracks: the world splitting open around you ----
        DrawCracks(dl, shortSide, px, alpha, age, beat);

        // ---- the seal ----
        if (_seal == null)
        {
            _palette = new MagicCircle.Palette(
                halo: DrawHelpers.ToU32(0.50f, 0.03f, 0.02f, 1f),  // deep blood-red haze
                main: DrawHelpers.ToU32(0.88f, 0.09f, 0.05f, 1f),  // the hot crimson of the lines
                core: DrawHelpers.ToU32(1.00f, 0.55f, 0.12f, 1f),  // hellfire-gold hot highlights
                ink: DrawHelpers.ToU32(0.05f, 0.00f, 0.01f, 1f));  // near-black under-stroke
            _seal = new MagicCircle(_palette, seed: 0xD00D);
        }
        _seal.Draw(dl, _sealCenter, _sealRadius, age, time, alpha);

        // ---- embers rising out of the cracked ground ----
        _embers.Update(
            time, dt,
            spawnIntervalMin: 0.05f, spawnIntervalMax: 0.16f,
            spawnPos: _emberPos, spawnVelocity: _emberVel,
            pickGlyph: _noGlyph,
            lifespanMin: 1.1f, lifespanMax: 2.3f, sizeMin: 3f, sizeMax: 8f);
        for (int i = 0; i < _embers.Count; i++)
            DrawEmber(dl, in _embers[i], alpha, time);

        // ---- runes drifting up out of the screen edges (the seal's own script, recolored) ----
        _runes.Update(
            time, dt,
            spawnIntervalMin: 0.28f, spawnIntervalMax: 0.55f,
            spawnPos: _edgeRunePos, spawnVelocity: _edgeRuneVel,
            pickGlyph: _pickRune,
            lifespanMin: 2.0f, lifespanMax: 3.4f,
            sizeMin: EdgeRuneMin, sizeMax: EdgeRuneMax);
        RuneParticles.DrawField(dl, _runes, time, _palette, alpha);

        // ---- runes peeling off the seal's rune band, once it has locked in ----
        if (age > LockTime)
        {
            _shed.Update(
                time, dt,
                spawnIntervalMin: 0.55f, spawnIntervalMax: 1.05f,
                spawnPos: _shedPos, spawnVelocity: _shedVel,
                pickGlyph: _pickRune,
                lifespanMin: 1.8f, lifespanMax: 2.6f,
                sizeMin: ShedRuneMin, sizeMax: ShedRuneMax);
            RuneParticles.DrawField(dl, _shed, time, _palette, alpha, radial: true, radialCenter: _sealCenter);
        }

        // ---- the toll: a slow shockwave rings out from the seal; the first doubles as the brand flash ----
        DrawToll(dl, screenSize, px, alpha, age);
    }

    // =====================================================================================
    // Cracks
    // =====================================================================================

    private void BuildCracks(int baseSeed)
    {
        for (int i = 0; i < CrackCount; i++)
        {
            int s = unchecked(baseSeed + i * 7919);
            float angleBase = MathF.Tau * i / CrackCount;

            _cracks[i] = new Crack
            {
                Angle        = angleBase + DrawHelpers.HashRange(s, -0.30f, 0.30f),
                Length       = DrawHelpers.HashRange(s + 1, 0.32f, 0.92f),
                AngleJitter  = DrawHelpers.HashRange(s + 2, 0.16f, 0.38f),
                Segments     = 5 + (int)(DrawHelpers.Hash01(s + 3) * 4f), // 5..8
                Seed         = s + 4,
                Delay        = DrawHelpers.HashRange(s + 5, 0f, 0.60f),
                GrowDuration = DrawHelpers.HashRange(s + 6, 0.35f, 0.68f),
                Alpha        = DrawHelpers.HashRange(s + 7, 0.72f, 1.00f),
                BranchChance = DrawHelpers.HashRange(s + 8, 0.12f, 0.28f),
                Width        = DrawHelpers.HashRange(s + 9, 0.78f, 1.30f),
            };
        }
    }

    private void DrawCracks(ImDrawListPtr dl, float shortSide, float px, float alpha, float age, float beat)
    {
        float glowPulse = 0.55f + 0.45f * beat;

        for (int i = 0; i < CrackCount; i++)
        {
            ref readonly var c = ref _cracks[i];
            float localAge = age - c.Delay;
            if (localAge <= 0f) continue;

            float growT = MagicCircle.Saturate(localAge / c.GrowDuration);
            float ease = MagicCircle.EaseOutCubic(growT);
            if (ease <= 0.002f) continue;

            float totalLen = shortSide * c.Length;
            float visibleLen = totalLen * ease;
            float segLen = totalLen / c.Segments;

            float ca = MathF.Cos(c.Angle), sa = MathF.Sin(c.Angle);
            Vector2 dir = new(ca, sa);
            Vector2 cursor = _sealCenter + dir * (_sealRadius * 1.015f);

            float crackA = alpha * c.Alpha;
            float freshGlint = growT < 1f ? (1f - growT) * 0.7f : 0f;

            float acc = 0f;
            for (int k = 0; k < c.Segments; k++)
            {
                float j = DrawHelpers.HashRange(c.Seed + k, -c.AngleJitter, c.AngleJitter);
                float cj = MathF.Cos(j), sj = MathF.Sin(j);
                Vector2 stepDir = new(dir.X * cj - dir.Y * sj, dir.X * sj + dir.Y * cj);
                Vector2 next = cursor + stepDir * segLen;

                float segStart = acc, segEnd = acc + segLen;
                acc = segEnd;
                if (segStart >= visibleLen) break;

                bool partial = segEnd > visibleLen;
                Vector2 drawEnd = partial
                    ? Vector2.Lerp(cursor, next, Math.Clamp((visibleLen - segStart) / segLen, 0f, 1f))
                    : next;

                float t = (float)k / c.Segments;
                float darkW = (3.4f - 1.6f * t) * px * c.Width;
                float glowW = (2.6f - 1.0f * t) * px * c.Width;
                float coreW = (1.3f - 0.5f * t) * px * c.Width;

                uint dark = DrawHelpers.WithAlpha(VoidBlack, crackA * 0.95f);
                uint glow = DrawHelpers.WithAlpha(HellfireCore, crackA * (0.16f + 0.20f * glowPulse) * c.Width);
                uint core = DrawHelpers.WithAlpha(BloodMain, crackA * (0.55f + 0.25f * glowPulse) * c.Width);

                dl.AddLine(cursor, drawEnd, dark, darkW);
                dl.AddLine(cursor, drawEnd, glow, glowW);
                dl.AddLine(cursor, drawEnd, core, coreW);
                if (freshGlint > 0.02f)
                    dl.AddLine(cursor, drawEnd, DrawHelpers.WithAlpha(HellfireHot, crackA * freshGlint), coreW * 0.6f);

                if (!partial && k < c.Segments - 1 && DrawHelpers.Hash01(c.Seed + k * 31 + 7) < c.BranchChance)
                {
                    float bs = (DrawHelpers.Hash01(c.Seed + k * 31 + 3) < 0.5f ? 1f : -1f)
                             * DrawHelpers.HashRange(c.Seed + k * 31 + 5, 0.7f, 1.3f);
                    float bca = MathF.Cos(bs), bsa = MathF.Sin(bs);
                    Vector2 bDir = new(stepDir.X * bca - stepDir.Y * bsa, stepDir.X * bsa + stepDir.Y * bca);
                    Vector2 bend = next + bDir * segLen * 0.55f;

                    dl.AddLine(next, bend, DrawHelpers.WithAlpha(VoidBlack, crackA * 0.71f), darkW * 0.7f);
                    dl.AddLine(next, bend, DrawHelpers.WithAlpha(HellfireCore, crackA * (0.13f + 0.16f * glowPulse) * c.Width), glowW * 0.7f);
                    dl.AddLine(next, bend, DrawHelpers.WithAlpha(BloodMain, crackA * (0.47f + 0.21f * glowPulse) * c.Width), coreW * 0.75f);
                }

                cursor = next;
                if (partial) break;
            }
        }
    }

    // =====================================================================================
    // Toll: a repeating shockwave ring, gated to start the instant the seal locks
    // =====================================================================================

    private void DrawToll(ImDrawListPtr dl, Vector2 screenSize, float px, float alpha, float age)
    {
        float clock = age - LockTime;
        if (clock < 0f) return;

        float cyc = clock / TollPeriod;
        float cycFloor = MathF.Floor(cyc);
        float u = cyc - cycFloor;
        float travelT = u * TollPeriod / TollTravel;
        if (travelT > 1f) return; // resting between tolls

        bool isFirst = cycFloor < 0.5f;
        float ease = MagicCircle.EaseOutCubic(travelT);
        float farReach = MaxCornerDistance(_sealCenter, screenSize);
        float radius = _sealRadius + (farReach - _sealRadius) * ease;

        float fadeIn = MagicCircle.Saturate(travelT / 0.06f);
        float fadeOut = MathF.Pow(1f - travelT, 1.4f);
        float ringAlpha = alpha * fadeIn * fadeOut * (isFirst ? 0.85f : 0.5f);

        if (ringAlpha >= 0.003f)
        {
            float thickness = (3.0f - 2.0f * ease) * px * (isFirst ? 1.3f : 1f);
            int segs = Math.Clamp((int)(radius * 0.06f), 32, 128);

            dl.AddCircle(_sealCenter, radius, DrawHelpers.WithAlpha(HellfireCore, ringAlpha * 0.5f), segs, thickness * 2.2f);
            dl.AddCircle(_sealCenter, radius, DrawHelpers.WithAlpha(BloodMain, ringAlpha), segs, thickness);
        }

        if (isFirst)
        {
            float brand = MagicCircle.Saturate(1f - travelT / 0.30f);
            if (brand > 0f)
                DrawHelpers.DrawVignette(dl, screenSize, VoidBlack, 0.50f, alpha * brand * 0.55f);
        }
    }

    private static float MaxCornerDistance(Vector2 point, Vector2 size)
    {
        float d0 = Vector2.Distance(point, new Vector2(0f, 0f));
        float d1 = Vector2.Distance(point, new Vector2(size.X, 0f));
        float d2 = Vector2.Distance(point, new Vector2(0f, size.Y));
        float d3 = Vector2.Distance(point, size);
        return MathF.Max(MathF.Max(d0, d1), MathF.Max(d2, d3));
    }

    // =====================================================================================
    // Embers
    // =====================================================================================

    private void DrawEmber(ImDrawListPtr dl, in EdgeParticleField.Particle p, float alpha, float time)
    {
        float age = time - p.Born;
        float ageNorm = Math.Clamp(age / p.Lifespan, 0f, 1f);
        float fade = EdgeParticleField.FadeFor(ageNorm);
        float a = alpha * fade;
        if (a < 0.003f) return;

        int seed = unchecked((int)(p.Born * 10007f));
        float heat = DrawHelpers.Hash01(seed);

        float wobAmp = 3f + ageNorm * 9f;
        float wobX = MathF.Sin(age * 5f + p.Born * 3f) * wobAmp;
        Vector2 pos = p.Pos + new Vector2(wobX, 0f);

        uint mid  = DrawHelpers.LerpColor(BloodMain, HellfireCore, heat);
        uint core = DrawHelpers.LerpColor(HellfireCore, HellfireHot, heat);
        dl.AddCircleFilled(pos, p.Size * 2.0f, DrawHelpers.WithAlpha(VoidBlack, a * 0.28f));
        dl.AddCircleFilled(pos, p.Size,        DrawHelpers.WithAlpha(mid,  a * 0.75f));
        dl.AddCircleFilled(pos, p.Size * 0.5f, DrawHelpers.WithAlpha(core, a * 0.92f));
    }

    private Vector2 EmberSpawnPos(int seed)
    {
        float x01 = (DrawHelpers.Hash01(seed) + DrawHelpers.Hash01(seed + 100)) * 0.5f;
        return new Vector2(x01 * _screenSize.X, _screenSize.Y - 4f);
    }

    private Vector2 EmberSpawnVelocity(int seed) =>
        new(DrawHelpers.HashRange(seed + 2, -18f, 18f), DrawHelpers.HashRange(seed + 1, -130f, -70f));

    // =====================================================================================
    // Rune particle spawn points
    // =====================================================================================

    private Vector2 EdgeRuneSpawnPos(int seed)
    {
        const float FootprintPx = 46f; // these runes run a little larger than Silence's, so they need more edge margin
        const float EdgeZone = 100f;

        float side = DrawHelpers.Hash01(seed);
        float along = DrawHelpers.HashRange(seed + 10, 0f, 1f);
        float depth = DrawHelpers.HashRange(seed + 2, FootprintPx, FootprintPx + EdgeZone);
        float usableW = MathF.Max(1f, _screenSize.X - FootprintPx * 2f);
        float usableH = MathF.Max(1f, _screenSize.Y - FootprintPx * 2f);

        if (side < 0.62f) // bottom edge - favored heavily, like the mark's script pouring up out of the earth
            return new Vector2(FootprintPx + along * usableW, _screenSize.Y - depth);
        if (side < 0.81f) // left edge
            return new Vector2(depth, FootprintPx + along * usableH);
        return new Vector2(_screenSize.X - depth, FootprintPx + along * usableH); // right edge
    }

    private Vector2 EdgeRuneSpawnVelocity(int seed) =>
        new(DrawHelpers.HashRange(seed, -7f, 7f), DrawHelpers.HashRange(seed + 1, -22f, -10f));

    // A shed rune is born on the rune band at a random angle and drifts outward across the bezel,
    // exactly like Silence's own shed runes - EdgeParticleField calls both delegates with the same
    // seed, so they agree on the angle.
    private Vector2 ShedSpawnPos(int seed)
    {
        float ang = DrawHelpers.HashRange(seed, 0f, MathF.Tau);
        float r = _sealRadius * DrawHelpers.HashRange(seed + 5, 0.82f, 0.90f);
        return _sealCenter + new Vector2(MathF.Cos(ang) * r, MathF.Sin(ang) * r);
    }

    private Vector2 ShedSpawnVelocity(int seed)
    {
        float ang = DrawHelpers.HashRange(seed, 0f, MathF.Tau);
        float speed = DrawHelpers.HashRange(seed + 6, 14f, 26f);
        return new Vector2(MathF.Cos(ang) * speed, MathF.Sin(ang) * speed - 5f);
    }
}
