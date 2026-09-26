using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Charm family (Infatuated / Seduced): lust, and a will that isn't quite your own anymore.
///
/// INTRO: a warm flush rushes up from the bottom edge while hearts burst in from the bottom AND
/// both lower sides, filling the frame with a visible wave that then dissipates.
///
/// STEADY STATE, back to front:
///   1. wine vignette, breathing on a slow warm double-pulse
///   2. rose mist - a scatter of many small drifting sub-circles per blob, hugging the frame's
///      RECTANGULAR edges
///   3. warmth from below
///   4. ambient hearts - same size and three-edge spawn pattern as the intro burst
///   5. suggestion HALOS - all suggestion discs, drawn in one pass
///   6. suggestion WORDS - all words, drawn in a second pass so every word sits on top of every
///      halo, including the halos of words that spawned after it.
/// </summary>
public sealed class CharmEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Charm;

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f;
    private const float IntroBurstSeconds = 2.1f;

    // ---- palette: deep wine -> rose -> hot pink -> pale blush ----
    private static readonly uint Wine  = DrawHelpers.ToU32(0.20f, 0.02f, 0.10f, 1f);
    private static readonly uint Rose  = DrawHelpers.ToU32(0.72f, 0.12f, 0.32f, 1f);
    private static readonly uint Hot   = DrawHelpers.ToU32(0.98f, 0.30f, 0.50f, 1f);
    private static readonly uint Blush = DrawHelpers.ToU32(1.00f, 0.82f, 0.86f, 1f);

    // The halo behind each suggestion: a PALE, near-white pink so the word (which is always
    // leaning toward Rose or Hot) contrasts against it.
    private static readonly uint HaloColor = DrawHelpers.ToU32(1.00f, 0.86f, 0.90f, 1f);

    // A wide spread of short, charm-appropriate phrases. They're drawn as glow-text so length
    // matters (longer = wider). Theming is deliberately consistent - commands, praise, gentle
    // pressure - so any two that appear near each other feel like they belong to one voice.
    private static readonly string[] Phrases =
    {
        // ---- one-word commands ----
        "obey", "submit", "surrender", "drop", "kneel", "stay", "listen", "focus",
        "behave", "beg", "please", "beg me", "obey me", "hush", "quiet", "stay put",
        "hush", "don't think", "breathe", "relax", "let go", "give in", "come here", "closer",

        // ---- praise / approval ----
        "good girl", "good toy", "good pet", "good thing", "such a good girl", "such a good toy",
        "that's it", "that's right", "perfect", "so perfect", "so good", "so pretty",
        "so sweet", "so obedient", "pretty thing", "sweet thing", "sweetheart", "darling",
        "precious", "my treasure", "my pet",

        // ---- possessive / claiming ----
        "mine", "all mine", "you're mine", "mine to keep", "mine alone", "only me",
        "no one else", "all for me", "just for me", "only for me",

        // ---- suggestions of surrender ----
        "don't resist", "don't think", "don't look away", "don't fight it", "don't pull away",
        "let me", "let it happen", "trust me", "leave it to me", "leave it all to me",
        "let yourself", "let yourself go", "stop thinking", "stop fighting", "stop resisting",
        "give yourself", "give yourself to me", "just breathe", "just feel", "just let go",

        // ---- appeals / coaxing ----
        "closer", "come closer", "look at me", "eyes on me", "look here", "here",
        "stay with me", "stay here", "with me", "stay still", "hold still", "be still",
        "be good", "be sweet", "be mine", "for me", "please me", "satisfy me",

        // ---- affirmations in the charm's voice ----
        "say yes", "say yes please", "say yes miss", "say please", "say please miss", "more", "again",
        "so close", "almost there", "just a little more", "drop", "drop deeper",

        // ---- softer / dreamier ----
        "so warm", "melting", "so blushy", "feel this", "feel me", "feel it", "sink",
        "sink in", "sink down", "deep", "deeper", "down", "fall deeper", "fall into trance",
        "mine now", "you love your cage", "with me now", "collars~", "leashes~", "cages~",
    };

    private const float FadeInFrac  = 0.20f;
    private const float FadeOutFrac = 0.30f;

    // ---- spawn edge distribution ----
    private const float BottomSpawnChance = 0.70f;
    private const float SideSpawnChance   = 0.15f;

    // =====================================================================================
    // Rose mist
    // =====================================================================================
    private const int MistCount      = 20;
    private const int MistSubCircles = 9;

    private struct MistBlob
    {
        public float BaseX, BaseY;
        public float SizeFrac;
        public float DriftX, DriftY;
        public float PhaseX, PhaseY;
        public float FreqX, FreqY;
        public float Alpha;
        public bool  Lit;
        public int   Seed;
    }

    private readonly MistBlob[] _mist = new MistBlob[MistCount];

    // =====================================================================================
    // Intro burst hearts
    // =====================================================================================
    private const int BurstBottom = 20;
    private const int BurstSide   = 5;
    private const int BurstHeartCount = BurstBottom + BurstSide * 2;

    private struct BurstHeart
    {
        public byte  Edge;
        public Vector2 Start;
        public Vector2 Velocity;
        public float Size;
        public float Rotation;
        public float RotSpeed;
        public float Hue;
        public float Phase;
        public float Delay;
        public int   Seed;
    }

    private readonly BurstHeart[] _burst = new BurstHeart[BurstHeartCount];

    // =====================================================================================
    // Ambient hearts + suggestions
    // =====================================================================================
    private readonly EdgeParticleField _suggestions = new(maxParticles: 6, seedSalt: 0x100004);
    private readonly EdgeParticleField _hearts      = new(maxParticles: 34, seedSalt: 0x100104);

    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _suggestionPos;
    private readonly Func<int, Vector2> _suggestionVel;
    private readonly Func<int, string>  _pickPhrase;
    private readonly Func<int, Vector2> _heartPos;
    private readonly Func<int, Vector2> _heartVel;
    private readonly Func<int, string>  _noGlyph;

    // ---- cast-in state ----
    private float _lastDrawTime = -100f;
    private float _castStart;

    public CharmEffect()
    {
        _suggestionPos = SuggestionSpawnPos;
        _suggestionVel = SuggestionSpawnVel;
        _pickPhrase    = PickPhrase;
        _heartPos      = HeartSpawnPos;
        _heartVel      = HeartSpawnVel;
        _noGlyph       = static _ => "";

        // ---- bake rose mist ----
        for (int i = 0; i < MistCount; i++)
        {
            int s = unchecked(0x100C00 + i * 7919);

            int edge = (int)(DrawHelpers.Hash01(s) * 4f);
            if (edge > 3) edge = 3;

            float along = DrawHelpers.HashRange(s + 1, 0f, 1f);
            float depth = DrawHelpers.HashRange(s + 2, 0.05f, 0.35f);

            float bx, by;
            switch (edge)
            {
                default:
                case 0: bx = along;         by = depth;         break;
                case 1: bx = 1f - depth;    by = along;         break;
                case 2: bx = along;         by = 1f - depth;    break;
                case 3: bx = depth;         by = along;         break;
            }

            _mist[i] = new MistBlob
            {
                BaseX    = bx,
                BaseY    = by,
                SizeFrac = DrawHelpers.HashRange(s + 3, 0.12f, 0.24f),
                DriftX   = DrawHelpers.HashRange(s + 4, 0.020f, 0.055f),
                DriftY   = DrawHelpers.HashRange(s + 5, 0.016f, 0.045f),
                PhaseX   = DrawHelpers.HashRange(s + 6, 0f, MathF.PI * 2f),
                PhaseY   = DrawHelpers.HashRange(s + 7, 0f, MathF.PI * 2f),
                FreqX    = DrawHelpers.HashRange(s + 8, 0.04f, 0.11f),
                FreqY    = DrawHelpers.HashRange(s + 9, 0.04f, 0.11f),
                Alpha    = DrawHelpers.HashRange(s + 10, 0.05f, 0.11f),
                Lit      = DrawHelpers.Hash01(s + 11) < 0.40f,
                Seed     = s,
            };
        }
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;
        _screenSize = screenSize;

        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        float dt = ImGui.GetIO().DeltaTime;

        if (time - _lastDrawTime > NewCastGapSeconds)
        {
            _castStart = time;
            BuildBurstHearts(unchecked((int)(_castStart * 1000f)), screenSize);
        }
        _lastDrawTime = time;
        float age = time - _castStart;

        float introFlash = age < 0.55f ? 1f - age / 0.55f : 0f;

        float pulse = 0.60f + 0.25f * DrawHelpers.Pulse(time, 2.6f) + 0.15f * DrawHelpers.Pulse(time, 4.1f, 0.3f);

        // 1) wine vignette.
        float vigT = 0.13f + 0.03f * introFlash;
        float vigA = pulse * (1f + 0.35f * introFlash);
        DrawHelpers.DrawVignette(dl, screenSize, Wine, vigT, alpha * vigA);

        // 2) rose mist
        if (age > 0.10f)
        {
            float mistFade = Math.Clamp((age - 0.10f) / 0.60f, 0f, 1f);
            DrawMist(dl, screenSize, shortSide, time, alpha * mistFade);
        }

        // 3) warmth from below
        DrawWarmthFromBelow(dl, screenSize, alpha * (1f + 0.7f * introFlash), time);

        // 4) intro burst hearts
        if (age < IntroBurstSeconds)
            DrawBurstHearts(dl, alpha, age);

        // 5) ambient hearts
        if (age > 0.90f)
        {
            _hearts.Update(
                time, dt,
                spawnIntervalMin: 0.22f, spawnIntervalMax: 0.55f,
                spawnPos: _heartPos, spawnVelocity: _heartVel,
                pickGlyph: _noGlyph,
                lifespanMin: 3.0f, lifespanMax: 4.8f, sizeMin: 18f, sizeMax: 50f);

            for (int i = 0; i < _hearts.Count; i++)
                DrawAmbientHeart(dl, in _hearts[i], alpha, time);
        }

        // 6) suggestions, drawn in TWO PASSES.
        //
        // Each suggestion's halo is drawn first, then its word on top. But an individual
        // suggestion's own halo could still cover an EARLIER suggestion's word if they overlap,
        // since halos and words are interleaved in the original single-pass loop. Drawing all
        // halos first, then all words on top, guarantees every word sits above every halo -
        // including halos belonging to suggestions that spawned after it.
        if (age > 0.40f)
        {
            _suggestions.Update(
                time, dt,
                spawnIntervalMin: 0.85f, spawnIntervalMax: 1.70f,
                spawnPos: _suggestionPos, spawnVelocity: _suggestionVel,
                pickGlyph: _pickPhrase,
                lifespanMin: 4.6f, lifespanMax: 6.4f, sizeMin: 21f, sizeMax: 30f);

            // Pass 1: every halo, back to front.
            for (int i = 0; i < _suggestions.Count; i++)
                DrawSuggestionHalo(dl, in _suggestions[i], alpha, time);

            // Pass 2: every word, on top of every halo.
            for (int i = 0; i < _suggestions.Count; i++)
                DrawSuggestionWord(dl, in _suggestions[i], alpha, time);
        }
    }

    // =====================================================================================
    // Intro burst
    // =====================================================================================

    private void BuildBurstHearts(int castSeed, Vector2 screenSize)
    {
        for (int i = 0; i < BurstHeartCount; i++)
        {
            int s = unchecked(castSeed + 0xC4A2 + i * 7919);

            byte edge;
            Vector2 start;
            Vector2 vel;

            if (i < BurstBottom)
            {
                edge = 0;
                float x = DrawHelpers.HashRange(s,     0.06f, 0.94f) * screenSize.X;
                float y = DrawHelpers.HashRange(s + 1, 0.78f, 1.04f) * screenSize.Y;
                start = new Vector2(x, y);
                vel = new Vector2(
                    DrawHelpers.HashRange(s + 3, -90f, 90f),
                    -DrawHelpers.HashRange(s + 2, 340f, 620f));
            }
            else if (i < BurstBottom + BurstSide)
            {
                edge = 1;
                float x = DrawHelpers.HashRange(s,     -6f, 6f);
                float y = DrawHelpers.HashRange(s + 1, 0.40f, 0.98f) * screenSize.Y;
                start = new Vector2(x, y);
                vel = new Vector2(
                    DrawHelpers.HashRange(s + 3, 110f, 260f),
                    -DrawHelpers.HashRange(s + 2, 240f, 480f));
            }
            else
            {
                edge = 2;
                float x = screenSize.X + DrawHelpers.HashRange(s,     -6f, 6f);
                float y = DrawHelpers.HashRange(s + 1, 0.40f, 0.98f) * screenSize.Y;
                start = new Vector2(x, y);
                vel = new Vector2(
                    -DrawHelpers.HashRange(s + 3, 110f, 260f),
                    -DrawHelpers.HashRange(s + 2, 240f, 480f));
            }

            _burst[i] = new BurstHeart
            {
                Edge     = edge,
                Start    = start,
                Velocity = vel,
                Size     = DrawHelpers.HashRange(s + 4, 18f, 50f),
                Rotation = DrawHelpers.HashRange(s + 5, 0f, MathF.PI * 2f),
                RotSpeed = DrawHelpers.HashRange(s + 6, -1.4f, 1.4f),
                Hue      = DrawHelpers.HashRange(s + 7, 0f, 1f),
                Phase    = DrawHelpers.HashRange(s + 8, 0f, MathF.PI * 2f),
                Delay    = DrawHelpers.HashRange(s + 9, 0f, 0.45f),
                Seed     = s,
            };
        }
    }

    private void DrawBurstHearts(ImDrawListPtr dl, float alpha, float age)
    {
        for (int i = 0; i < BurstHeartCount; i++)
        {
            ref readonly var h = ref _burst[i];
            float local = age - h.Delay;
            if (local <= 0f) continue;

            float life = 1.6f;
            if (local > life) continue;
            float t01 = local / life;

            float env = t01 < 0.15f
                ? t01 / 0.15f
                : 1f - (t01 - 0.15f) / 0.85f;
            env = Math.Clamp(env, 0f, 1f);
            if (env <= 0.003f) continue;

            float decay = 1f - 0.45f * t01 * t01;
            Vector2 pos = h.Start + h.Velocity * (local * decay);

            float sway = MathF.Sin(local * 2.2f + h.Phase) * 14f;
            pos.X += sway;

            float rot = h.Rotation + h.RotSpeed * local;

            uint col = DrawHelpers.LerpColor(Rose, Hot, h.Hue);

            DrawHaloCluster(dl, pos, h.Size * 0.9f, col, alpha * env * 0.20f, h.Seed, local);

            DrawHeart(dl, pos, h.Size, rot, DrawHelpers.WithAlpha(col, alpha * env));
        }
    }

    // =====================================================================================
    // Rose mist
    // =====================================================================================

    private void DrawMist(ImDrawListPtr dl, Vector2 screenSize, float shortSide, float time, float alpha)
    {
        for (int i = 0; i < MistCount; i++)
        {
            ref readonly var m = ref _mist[i];

            float dx = MathF.Sin(time * m.FreqX + m.PhaseX) * shortSide * m.DriftX;
            float dy = MathF.Cos(time * m.FreqY + m.PhaseY) * shortSide * m.DriftY;

            Vector2 p = new(m.BaseX * screenSize.X + dx, m.BaseY * screenSize.Y + dy);
            float r = shortSide * m.SizeFrac;

            uint col = m.Lit ? Hot : Rose;

            for (int k = 0; k < MistSubCircles; k++)
            {
                int ks = unchecked(m.Seed + 100 + k * 71);
                float ox = DrawHelpers.HashRange(ks,     -1f, 1f) * r * 0.95f;
                float oy = DrawHelpers.HashRange(ks + 1, -1f, 1f) * r * 0.95f;
                float sr = r * DrawHelpers.HashRange(ks + 2, 0.26f, 0.58f);

                float wfX = DrawHelpers.HashRange(ks + 3, 0.35f, 0.85f);
                float wfY = DrawHelpers.HashRange(ks + 5, 0.35f, 0.85f);
                float wpX = DrawHelpers.HashRange(ks + 4, 0f, MathF.PI * 2f);
                float wpY = DrawHelpers.HashRange(ks + 6, 0f, MathF.PI * 2f);

                float wobX = MathF.Sin(time * wfX + wpX) * sr * 0.45f;
                float wobY = MathF.Cos(time * wfY + wpY) * sr * 0.45f;

                Vector2 sp = p + new Vector2(ox + wobX, oy + wobY);
                dl.AddCircleFilled(sp, sr, DrawHelpers.WithAlpha(col, alpha * m.Alpha * 0.42f));
            }
        }
    }

    // =====================================================================================
    // Ambient hearts
    // =====================================================================================

    private static void DrawAmbientHeart(ImDrawListPtr dl, in EdgeParticleField.Particle p, float alpha, float time)
    {
        float age = time - p.Born;
        if (age < 0f || age >= p.Lifespan) return;
        float t01 = age / p.Lifespan;
        float fade = EdgeParticleField.FadeFor(t01);
        if (fade <= 0.005f) return;

        int h = unchecked((int)(p.Born * 10007f));
        float hue = DrawHelpers.Hash01(h);
        float rotBase = DrawHelpers.HashRange(h + 1, 0f, MathF.PI * 2f);
        float rotSpeed = DrawHelpers.HashRange(h + 2, -0.6f, 0.6f);

        float swayX = MathF.Sin(age * 1.4f + p.Born * 2f) * 12f;
        Vector2 pos = p.Pos + new Vector2(swayX, 0f);

        float rot = rotBase + rotSpeed * age;

        uint col = DrawHelpers.LerpColor(Rose, Hot, hue);
        uint drawCol = DrawHelpers.WithAlpha(col, alpha * fade * 0.62f);

        DrawHeart(dl, pos, p.Size, rot, drawCol);
    }

    private Vector2 HeartSpawnPos(int seed)
    {
        float roll = DrawHelpers.Hash01(seed);

        if (roll < BottomSpawnChance)
        {
            float x = DrawHelpers.HashRange(seed + 1, 0.05f, 0.95f) * _screenSize.X;
            float y = DrawHelpers.HashRange(seed + 2, 0.90f, 1.06f) * _screenSize.Y;
            return new Vector2(x, y);
        }

        if (roll < BottomSpawnChance + SideSpawnChance)
        {
            float x = -6f;
            float y = DrawHelpers.HashRange(seed + 2, 0.55f, 1.00f) * _screenSize.Y;
            return new Vector2(x, y);
        }

        return new Vector2(
            _screenSize.X + 6f,
            DrawHelpers.HashRange(seed + 2, 0.55f, 1.00f) * _screenSize.Y);
    }

    private Vector2 HeartSpawnVel(int seed)
    {
        float roll = DrawHelpers.Hash01(seed);
        float vy = DrawHelpers.HashRange(seed + 4, -46f, -26f);

        if (roll < BottomSpawnChance)
        {
            float vx = DrawHelpers.HashRange(seed + 3, -10f, 10f);
            return new Vector2(vx, vy);
        }

        if (roll < BottomSpawnChance + SideSpawnChance)
        {
            float vx = DrawHelpers.HashRange(seed + 3, 22f, 55f);
            return new Vector2(vx, vy);
        }

        float vxr = -DrawHelpers.HashRange(seed + 3, 22f, 55f);
        return new Vector2(vxr, vy);
    }

    // =====================================================================================
    // Suggestions
    // =====================================================================================
    //
    // Each suggestion is drawn as two separate passes (halo, then word) so words always land
    // above every halo. Both passes recompute the same envelope from the particle's own data,
    // so they stay perfectly in sync without any shared state.

    /// <summary>Shared envelope: rises to 1 over the first FadeInFrac of life, holds, falls over the last FadeOutFrac.</summary>
    private static float SuggestionEnvelope(float t01)
    {
        float fadeIn  = DrawHelpers.EaseOutCubic(t01 / FadeInFrac);
        float fadeOut = 1f - DrawHelpers.EaseOutCubic((t01 - (1f - FadeOutFrac)) / FadeOutFrac);
        return MathF.Min(fadeIn, fadeOut);
    }

    /// <summary>Bloom flare for a suggestion: peaks right as the word finishes arriving, decays over ~0.3s.</summary>
    private static float SuggestionBloom(float age, float lifespan)
    {
        float fadeInSeconds = lifespan * FadeInFrac;
        return age > fadeInSeconds ? MathF.Exp(-(age - fadeInSeconds) / 0.30f) : 0f;
    }

    /// <summary>
    /// Pass 1: the halo. ONE large, PALE pink circle (much lighter than the word colors, which
    /// lean toward Rose/Hot) that closely trails the word for the word's whole lifetime.
    ///
    /// Spawn position = where the word spawned. The word's current position is recoverable as
    /// `p.Pos - p.Velocity * age` because EdgeParticleField integrates position linearly with
    /// no acceleration.
    /// </summary>
    private static void DrawSuggestionHalo(ImDrawListPtr dl, in EdgeParticleField.Particle p, float alpha, float time)
    {
        float age = time - p.Born;
        if (age < 0f || age >= p.Lifespan) return;
        float t01 = age / p.Lifespan;

        float envelope = SuggestionEnvelope(t01);
        if (envelope <= 0.003f) return;

        float bloom = SuggestionBloom(age, p.Lifespan);
        float wordSize = p.Size * (1f + bloom * 0.12f);
        float a = alpha * envelope;

        int h = unchecked((int)(p.Born * 10007f));

        Vector2 wordSpawn = p.Pos - p.Velocity * age;

        float angleJitter = DrawHelpers.HashRange(h + 20, -0.30f, 0.30f);
        float speedJitter = DrawHelpers.HashRange(h + 21, 0.85f, 1.15f);

        float c = MathF.Cos(angleJitter), s = MathF.Sin(angleJitter);
        Vector2 haloVel = new(
            (p.Velocity.X * c - p.Velocity.Y * s) * speedJitter,
            (p.Velocity.X * s + p.Velocity.Y * c) * speedJitter);

        float extraMag = DrawHelpers.HashRange(h + 22, 4f, 12f);
        float extraAng = DrawHelpers.HashRange(h + 23, 0f, MathF.PI * 2f);
        haloVel += new Vector2(MathF.Cos(extraAng), MathF.Sin(extraAng)) * extraMag;

        Vector2 haloPos = wordSpawn + haloVel * age;

        float haloR = wordSize * (2.5f + 0.4f * bloom);
        dl.AddCircleFilled(haloPos, haloR, DrawHelpers.WithAlpha(HaloColor, a * 0.22f));
    }

    /// <summary>
    /// Pass 2: the word itself. Runs in a separate loop AFTER every halo has been drawn, so a
    /// word is never covered by any halo - including halos belonging to suggestions that
    /// spawned after it.
    /// </summary>
    private static void DrawSuggestionWord(ImDrawListPtr dl, in EdgeParticleField.Particle p, float alpha, float time)
    {
        float age = time - p.Born;
        if (age < 0f || age >= p.Lifespan) return;
        float t01 = age / p.Lifespan;

        float envelope = SuggestionEnvelope(t01);
        if (envelope <= 0.003f) return;

        int h = unchecked((int)(p.Born * 10007f));
        float heat = DrawHelpers.Hash01(h);
        float sway = MathF.Sin(age * 0.65f + DrawHelpers.HashRange(h + 1, 0f, MathF.PI * 2f)) * 7f;

        float bloom = SuggestionBloom(age, p.Lifespan);

        uint color = DrawHelpers.LerpColor(Rose, Hot, heat);
        var pos = p.Pos + new Vector2(sway, 0);
        float size = p.Size * (1f + bloom * 0.12f);
        float a = alpha * envelope;

        DrawHelpers.DrawGlowText(dl, pos, p.Glyph,
            DrawHelpers.WithAlpha(color, a * 0.90f),
            size, glow: 0.9f + bloom * 0.6f);
    }

    private string PickPhrase(int seed) => Phrases[Math.Min(Phrases.Length - 1, (int)(DrawHelpers.Hash01(seed) * Phrases.Length))];

    private Vector2 SuggestionSpawnPos(int seed)
    {
        float angle = DrawHelpers.HashRange(seed, 0f, MathF.PI * 2f);
        float shortSide = MathF.Min(_screenSize.X, _screenSize.Y);
        float radius = shortSide * DrawHelpers.HashRange(seed + 1, 0.20f, 0.40f);
        float cx = _screenSize.X * 0.5f + MathF.Cos(angle) * radius;
        float cy = _screenSize.Y * 0.40f + MathF.Sin(angle) * radius * 0.6f;
        return new Vector2(cx, cy);
    }

    private Vector2 SuggestionSpawnVel(int seed) => new(0f, DrawHelpers.HashRange(seed + 2, -6f, -2f));

    // =====================================================================================
    // Heart drawing (rotated)
    // =====================================================================================

    private static void DrawHeart(ImDrawListPtr dl, Vector2 center, float size, float rotation, uint color)
    {
        float r = size * 0.34f;
        float cosR = MathF.Cos(rotation);
        float sinR = MathF.Sin(rotation);

        Vector2 Rotate(Vector2 v) => new(v.X * cosR - v.Y * sinR, v.X * sinR + v.Y * cosR);

        Vector2 lobeL = center + Rotate(new Vector2(-r * 0.85f, -r * 0.5f));
        Vector2 lobeR = center + Rotate(new Vector2( r * 0.85f, -r * 0.5f));
        Vector2 tip   = center + Rotate(new Vector2(0f, size * 0.62f));
        Vector2 baseL = center + Rotate(new Vector2(-r * 1.7f, -r * 0.1f));
        Vector2 baseR = center + Rotate(new Vector2( r * 1.7f, -r * 0.1f));

        dl.AddCircleFilled(lobeL, r, color);
        dl.AddCircleFilled(lobeR, r, color);
        dl.AddTriangleFilled(baseL, baseR, tip, color);
    }

    // =====================================================================================
    // Cloudy bloom helper (still used by the intro burst hearts)
    // =====================================================================================

    private static void DrawHaloCluster(ImDrawListPtr dl, Vector2 center, float radius, uint color,
                                        float baseAlpha, int seed, float age)
    {
        if (baseAlpha <= 0.001f || radius <= 0.5f) return;

        const int subCount = 7;

        for (int k = 0; k < subCount; k++)
        {
            int ks = unchecked(seed + 500 + k * 71);

            float offX = DrawHelpers.HashRange(ks,     -1f, 1f) * radius * 0.60f;
            float offY = DrawHelpers.HashRange(ks + 1, -1f, 1f) * radius * 0.60f;
            float sr   = radius * DrawHelpers.HashRange(ks + 2, 0.30f, 0.65f);

            float wfX = DrawHelpers.HashRange(ks + 3, 0.4f, 1.0f);
            float wfY = DrawHelpers.HashRange(ks + 5, 0.4f, 1.0f);
            float wpX = DrawHelpers.HashRange(ks + 4, 0f, MathF.PI * 2f);
            float wpY = DrawHelpers.HashRange(ks + 6, 0f, MathF.PI * 2f);

            float wobX = MathF.Sin(age * wfX + wpX) * sr * 0.40f;
            float wobY = MathF.Cos(age * wfY + wpY) * sr * 0.40f;

            Vector2 p = center + new Vector2(offX + wobX, offY + wobY);
            dl.AddCircleFilled(p, sr, DrawHelpers.WithAlpha(color, baseAlpha));
        }
    }

    // =====================================================================================
    // Warmth from below
    // =====================================================================================

    private static void DrawWarmthFromBelow(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float breathe = 0.7f + 0.3f * DrawHelpers.Pulse(time, 3.3f, 0.15f);
        float bandHeight = screenSize.Y * 0.28f;
        uint edge = DrawHelpers.WithAlpha(Rose, alpha * 0.16f * breathe);
        const uint clear = 0u;

        dl.AddRectFilledMultiColor(
            new Vector2(0, screenSize.Y - bandHeight), new Vector2(screenSize.X, screenSize.Y),
            clear, clear, edge, edge);
    }
}