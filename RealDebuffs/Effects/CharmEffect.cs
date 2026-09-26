using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Charm family (Infatuated / Seduced): lust, and a will that isn't quite your own anymore. A deep
/// wine-rose vignette holds a slow, warm pulse - like a racing heartbeat felt in the face - while
/// half-legible suggestions surface out of it: single words or short phrases ("obey", "good girl",
/// "submit") that fade up out of nothing, hold just long enough to read, and sink back down, the
/// way a thought you didn't choose might surface and recede. A scatter of small hand-drawn hearts
/// drifts underneath as quieter ambient texture - present, but no longer the main event.
///
/// EffectManager folds the active status's severity into the alpha this effect receives (see
/// DebuffKind.Strengths), so nothing here branches on which status it is: Infatuated's suggestions
/// arrive fainter and harder to catch, Seduced's read clearly. That's the whole difference.
///
/// The suggestions reuse Silence's animation idea (an eased rise, a hold, an eased fall, a brief
/// "bloom" flare right as one finishes arriving) but not its machinery: Silence hand-draws vector
/// runes because no font has those glyphs. These are just words, so they're drawn as real text
/// (DrawGlowText already renders it hazy/soft-edged, which suits "half-glimpsed suggestion" well)
/// rather than reinventing a font.
/// </summary>
public sealed class CharmEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Charm;

    // ---- palette: deep wine vignette -> rose -> hot pink -> pale blush for the suggestions themselves ----
    private static readonly uint Wine = DrawHelpers.ToU32(0.20f, 0.02f, 0.10f, 1f);
    private static readonly uint Rose = DrawHelpers.ToU32(0.72f, 0.12f, 0.32f, 1f);
    private static readonly uint Hot = DrawHelpers.ToU32(0.98f, 0.30f, 0.50f, 1f);
    private static readonly uint Blush = DrawHelpers.ToU32(1.00f, 0.82f, 0.86f, 1f);

    // ---- the suggestions: short, in the charm's own "voice" - commands and praise, not claims about
    // what the person secretly wants. Picked by index (see PickPhrase), not baked into the particle
    // pool, so the pool stays a plain EdgeParticleField like every other glyph-based effect. ----
    private static readonly string[] Phrases =
    {
        "obey", "submit", "surrender", "good girl", "mine", "closer", "helpless", "don't resist",
    };

    private const float FadeInFrac = 0.20f; // fraction of a suggestion's lifespan spent easing in
    private const float FadeOutFrac = 0.30f; // fraction spent easing back out, at the end

    private readonly EdgeParticleField _suggestions = new(maxParticles: 6, seedSalt: 0x100004);
    private readonly EdgeParticleField _hearts = new(maxParticles: 8, seedSalt: 0x100104);

    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _suggestionPos;
    private readonly Func<int, Vector2> _suggestionVel;
    private readonly Func<int, string> _pickPhrase;
    private readonly Func<int, Vector2> _heartPos;
    private readonly Func<int, Vector2> _heartVel;
    private readonly Func<int, string> _noGlyph;

    public CharmEffect()
    {
        _suggestionPos = SuggestionSpawnPos;
        _suggestionVel = SuggestionSpawnVel;
        _pickPhrase = PickPhrase;
        _heartPos = HeartSpawnPos;
        _heartVel = HeartSpawnVel;
        _noGlyph = static _ => "";
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float dt = ImGui.GetIO().DeltaTime;
        _screenSize = screenSize;

        // A slow warm double-pulse rather than one clean beat - felt more like a flush of heat than
        // a sharp heartbeat (that reading is Weakness's, not this one).
        float pulse = 0.60f + 0.25f * DrawHelpers.Pulse(time, 2.6f) + 0.15f * DrawHelpers.Pulse(time, 4.1f, 0.3f);
        DrawHelpers.DrawVignette(dl, screenSize, Wine, 0.13f, alpha * pulse);

        DrawWarmthFromBelow(dl, screenSize, alpha, time);

        _hearts.Update(
            time, dt,
            spawnIntervalMin: 0.8f, spawnIntervalMax: 1.7f,
            spawnPos: _heartPos, spawnVelocity: _heartVel,
            pickGlyph: _noGlyph,
            lifespanMin: 3.0f, lifespanMax: 4.4f, sizeMin: 8f, sizeMax: 15f);
        for (int i = 0; i < _hearts.Count; i++)
        {
            ref readonly var p = ref _hearts[i];
            float fade = EdgeParticleField.FadeFor((time - p.Born) / p.Lifespan);
            float sway = MathF.Sin((time - p.Born) * 1.4f + p.Born * 2f) * 10f;
            DrawHeart(dl, p.Pos + new Vector2(sway, 0), p.Size, DrawHelpers.WithAlpha(Rose, alpha * fade * 0.35f));
        }

        _suggestions.Update(
            time, dt,
            spawnIntervalMin: 0.9f, spawnIntervalMax: 1.8f,
            spawnPos: _suggestionPos, spawnVelocity: _suggestionVel,
            pickGlyph: _pickPhrase,
            lifespanMin: 4.6f, lifespanMax: 6.4f, sizeMin: 21f, sizeMax: 30f);
        for (int i = 0; i < _suggestions.Count; i++)
            DrawSuggestion(dl, in _suggestions[i], alpha, time);
    }

    // =====================================================================================
    // Suggestions
    // =====================================================================================

    private static void DrawSuggestion(ImDrawListPtr dl, in EdgeParticleField.Particle p, float alpha, float time)
    {
        float age = time - p.Born;
        if (age < 0f || age >= p.Lifespan) return;
        float t01 = age / p.Lifespan;

        // Rises, holds at 1, falls - both edges eased so it feels like it's surfacing and sinking
        // back rather than switching on and off.
        float fadeIn = DrawHelpers.EaseOutCubic(t01 / FadeInFrac);
        float fadeOut = 1f - DrawHelpers.EaseOutCubic((t01 - (1f - FadeOutFrac)) / FadeOutFrac);
        float envelope = MathF.Min(fadeIn, fadeOut);
        if (envelope <= 0.003f) return;

        // A stable per-particle random from its birth time (Particle carries no seed of its own),
        // same trick Silence's rune particles use for their own per-particle randomness.
        int h = unchecked((int)(p.Born * 10007f));
        float heat = DrawHelpers.Hash01(h); // 0 leans rose/deep, 1 leans hot pink
        float sway = MathF.Sin(age * 0.65f + DrawHelpers.HashRange(h + 1, 0f, MathF.PI * 2f)) * 7f;

        // A brief flare right as it finishes arriving - a small emphasis, then it settles.
        float fadeInSeconds = p.Lifespan * FadeInFrac;
        float bloom = age > fadeInSeconds ? MathF.Exp(-(age - fadeInSeconds) / 0.30f) : 0f;

        uint color = DrawHelpers.LerpColor(Rose, Hot, heat);
        var pos = p.Pos + new Vector2(sway, 0);
        float size = p.Size * (1f + bloom * 0.12f);

        DrawHelpers.DrawGlowText(dl, pos, p.Glyph, DrawHelpers.WithAlpha(color, alpha * envelope * 0.85f), size, glow: 0.9f + bloom * 0.5f);
    }

    private string PickPhrase(int seed) => Phrases[Math.Min(Phrases.Length - 1, (int)(DrawHelpers.Hash01(seed) * Phrases.Length))];

    // Suggestions surface in a loose ring around the upper-middle of the screen - close enough to
    // read easily, but staying clear of dead centre (where the action is) and the lower edge (where
    // a real HUD's hotbars usually sit). Practically motionless: thoughts surface, they don't fly.
    private Vector2 SuggestionSpawnPos(int seed)
    {
        float angle = DrawHelpers.HashRange(seed, 0f, MathF.PI * 2f);
        float shortSide = MathF.Min(_screenSize.X, _screenSize.Y);
        float radius = shortSide * DrawHelpers.HashRange(seed + 1, 0.20f, 0.40f);
        float cx = _screenSize.X * 0.5f + MathF.Cos(angle) * radius;
        float cy = _screenSize.Y * 0.40f + MathF.Sin(angle) * radius * 0.6f; // flattened - stays out of the very top/bottom
        return new Vector2(cx, cy);
    }

    private Vector2 SuggestionSpawnVel(int seed) => new(0f, DrawHelpers.HashRange(seed + 2, -6f, -2f));

    // =====================================================================================
    // Ambient hearts (unchanged shape from the previous version - drawn, not a font glyph, so it
    // never depends on the game's font atlas including a heart character)
    // =====================================================================================

    private static void DrawHeart(ImDrawListPtr dl, Vector2 center, float size, uint color)
    {
        float r = size * 0.34f;
        var lobeL = center + new Vector2(-r * 0.85f, -r * 0.5f);
        var lobeR = center + new Vector2(r * 0.85f, -r * 0.5f);
        dl.AddCircleFilled(lobeL, r, color);
        dl.AddCircleFilled(lobeR, r, color);

        var tip = center + new Vector2(0, size * 0.62f);
        var baseL = center + new Vector2(-r * 1.7f, -r * 0.1f);
        var baseR = center + new Vector2(r * 1.7f, -r * 0.1f);
        dl.AddTriangleFilled(baseL, baseR, tip, color);
    }

    private Vector2 HeartSpawnPos(int seed) => new(
        DrawHelpers.HashRange(seed, 0.05f, 0.95f) * _screenSize.X,
        DrawHelpers.HashRange(seed + 1, 0.85f, 1.0f) * _screenSize.Y);

    private Vector2 HeartSpawnVel(int seed) => new(0f, DrawHelpers.HashRange(seed + 2, -46f, -26f));

    // =====================================================================================
    // A soft warm glow low in frame - warmth rising, underneath everything else.
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
