using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Cartoon "seeing stars": a small halo of stars, birds and sparkle motes circles near head height,
/// swaying like someone unsteady on their feet. Covers Stun and Down for the Count -
/// mechanically identical (can't act, can't move), just different sources.
///
/// Deliberately NOT a vignette or a border. Blind already owns "darken the whole screen" and Paralysis
/// already owns "outline the whole frame" - if Stun did another full-screen wash on top, it would either
/// wash out whatever else is stacked underneath or just be redundant with it. Instead this stays a
/// compact, self-contained decoration near the top of the screen: when several debuffs land at once,
/// each keeps its own territory and the *combination* is what makes things hard to see, rather than
/// any one effect trying to do that alone.
///
/// The moment the debuff lands, a comic "bonk" - radiating spike lines and a core flash - throws a
/// handful of sparkle motes outward, and the halo eases into its steady orbit as they fade (the same
/// "jolt settling into a hum" shape as Paralysis's opening volley, at cartoon scale). After that, three
/// rings (sparkle motes, stars, birds) circle at different radii and speeds, each ring's flatness
/// breathing gently and out of sync with the others so it doesn't sit as a perfectly rigid ellipse,
/// while the anchor drifts in a slow figure-eight sway and the whole halo gently breathes in size. A
/// faint ripple washes outward from the anchor once, right as the debuff lands - a shockwave riding
/// alongside the bonk that never reaches far enough to read as a vignette, and doesn't repeat: once it's
/// done, only the orbiting halo is left for the rest of the debuff's duration.
///
/// Deliberately not a full ellipse ROTATION: a wide, flat ring rotated through 90 degrees has its long
/// axis pointing straight up and down, which would periodically balloon this halo from a compact band
/// near head height into a tall column reaching well down the screen - exactly the sprawl this effect
/// is meant to avoid. Varying only how flat each ellipse is keeps every ring's vertical reach bounded
/// at all times, however long the debuff has been up.
///
/// Palette is a fresh warm gold for the stars/sparkles (distinct from Paralysis's amber-orange electricity)
/// with a cool pale blue for the birds, since nothing else in the plugin currently uses that combination.
/// </summary>
public sealed class StunEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Stun;

    // ---- palette: warm gold stars/sparkles, cool pale-blue birds ----
    private static readonly uint Hot = DrawHelpers.ToU32(1.00f, 0.98f, 0.85f, 1f);      // near-white gold core
    private static readonly uint Mid = DrawHelpers.ToU32(1.00f, 0.88f, 0.40f, 1f);      // golden yellow
    private static readonly uint Glow = DrawHelpers.ToU32(1.00f, 0.72f, 0.22f, 1f);     // warm amber-gold haze
    private static readonly uint Ink = DrawHelpers.ToU32(0.30f, 0.16f, 0.04f, 1f);      // dark under-stroke
    private static readonly uint BirdHot = DrawHelpers.ToU32(0.90f, 0.94f, 1.00f, 1f);  // pale blue-white
    private static readonly uint BirdMid = DrawHelpers.ToU32(0.62f, 0.72f, 0.95f, 1f);  // soft periwinkle
    private static readonly uint BirdGlow = DrawHelpers.ToU32(0.32f, 0.42f, 0.75f, 1f); // dusky blue haze

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f; // long enough that an ordinary frame hitch mid-fight never replays the bonk
    private const float FlashDecay = 0.10f;       // the spike burst and core flash decay this fast
    private const float EntranceSeconds = 0.45f;  // how long the halo takes to ease into its steady orbit

    // ---- the three rings: radii/speeds are fractions of the shorter screen side, so the halo scales
    // sensibly at any resolution without ever ballooning out toward the edges ----
    private const int SparkleCount = 3, StarCount = 4, BirdCount = 3;
    private const float SparkleRX = 0.085f, SparkleRY = 0.028f, SparkleSpeed = 3.1f, SparkleWobble = 0.55f;
    private const float StarRX = 0.155f, StarRY = 0.048f, StarSpeed = 1.7f, StarWobble = 0.35f;
    private const float BirdRX = 0.225f, BirdRY = 0.070f, BirdSpeed = 1.05f, BirdWobble = 0.23f;

    // ---- the wooze ripple: a one-shot shockwave, not a recurring pulse ----
    private const float WoozeDuration = 1.1f;
    private const float WoozeMaxRadiusFrac = 0.30f;
    private const float WoozeMaxAlpha = 0.12f;

    private float _lastDrawTime = -100f;
    private float _castStart;

    // Cached per frame so the mote spawn delegates below (called by EdgeParticleField, which only
    // passes a seed) don't need the anchor/basis threaded through as extra parameters.
    private Vector2 _anchor;
    private float _short;

    // Sparkle motes shed by the spinning halo - the same "small continuous detail" role Silence's
    // shed runes and Paralysis's sparks play. Custom-drawn (see DrawMotes), not glyphs.
    private readonly EdgeParticleField _motes = new(maxParticles: 14, seedSalt: 0x5713A9);
    private readonly Func<int, Vector2> _motePos;
    private readonly Func<int, Vector2> _moteVel;
    private readonly Func<int, string> _noGlyph;

    public StunEffect()
    {
        _motePos = MoteSpawnPos;
        _moteVel = MoteSpawnVelocity;
        _noGlyph = static _ => "";
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        if (screenSize.X < 32f || screenSize.Y < 32f) return;

        // A gap since the last Draw call means the debuff was just (re)applied - EffectManager stops
        // calling Draw once an effect has fully faded out, so this only fires on a fresh hit.
        if (time - _lastDrawTime > NewCastGapSeconds) _castStart = time;
        _lastDrawTime = time;
        float age = time - _castStart;
        int castSeed = unchecked((int)(_castStart * 1000f)); // a different bonk angle each time you're hit

        _short = MathF.Max(1f, MathF.Min(screenSize.X, screenSize.Y));
        _anchor = Anchor(screenSize, time);

        float flash = MathF.Exp(-age / FlashDecay);
        float entrance = EaseOutCubic(Saturate(age / EntranceSeconds));
        float breathe = 1f + 0.04f * MathF.Sin(time * 0.7f);

        DrawWooze(dl, age, alpha);
        DrawImpact(dl, castSeed, flash, alpha);

        // Motes: a denser puff for the first instant, then a slow ambient trickle for the rest of the debuff's life.
        _motes.Update(
            time, ImGui.GetIO().DeltaTime,
            spawnIntervalMin: flash > 0.25f ? 0.025f : 0.16f, spawnIntervalMax: flash > 0.25f ? 0.07f : 0.34f,
            spawnPos: _motePos, spawnVelocity: _moteVel,
            pickGlyph: _noGlyph,
            lifespanMin: 0.35f, lifespanMax: 0.75f,
            sizeMin: 1.6f, sizeMax: 3.2f);
        DrawMotes(dl, time, alpha);

        if (entrance <= 0.002f) return;
        float scale = (0.55f + 0.45f * entrance) * breathe;
        float ringAlpha = alpha * entrance;

        DrawRing(dl, castSeed, 11, SparkleCount, SparkleRX * scale, SparkleRY * scale, SparkleSpeed, SparkleWobble, time, ringAlpha,
            static (dl2, p, sz, tw, a) => DrawSparkle(dl2, p, sz, tw, a));
        DrawRing(dl, castSeed, 23, StarCount, StarRX * scale, StarRY * scale, StarSpeed, StarWobble, time, ringAlpha,
            static (dl2, p, sz, tw, a) => DrawStar(dl2, p, sz, tw, a));
        DrawRing(dl, castSeed, 37, BirdCount, BirdRX * scale, BirdRY * scale, BirdSpeed, BirdWobble, time, ringAlpha,
            static (dl2, p, sz, tw, a) => DrawBird(dl2, p, sz, tw, a));
    }

    // =====================================================================================
    // The anchor: where the halo is centred, before any per-ring tilt is applied
    // =====================================================================================

    /// <summary>Top-of-head height, drifting in a slow figure-eight-ish sway plus a barely-there smooth jitter - someone swaying on their feet, not a random shake.</summary>
    private static Vector2 Anchor(Vector2 screenSize, float time)
    {
        float shortSide = MathF.Max(1f, MathF.Min(screenSize.X, screenSize.Y));
        float cx = screenSize.X * 0.5f;
        float cy = screenSize.Y * 0.16f;

        float swayX = shortSide * 0.018f * MathF.Sin(time * (MathF.PI * 2f / 3.6f));
        float swayY = shortSide * 0.012f * MathF.Cos(time * (MathF.PI * 2f / 2.9f));
        float jitterX = shortSide * 0.0025f * MathF.Sin(time * 11.3f);
        float jitterY = shortSide * 0.0025f * MathF.Cos(time * 9.7f);

        return new Vector2(cx + swayX + jitterX, cy + swayY + jitterY);
    }

    // =====================================================================================
    // Rings: places `count` elements evenly around an ellipse whose flatness breathes gently, and
    // calls `draw` on each
    // =====================================================================================

    private void DrawRing(
        ImDrawListPtr dl, int castSeed, int ringSalt, int count, float rxFrac, float ryFrac, float speed, float wobbleSpeed,
        float time, float alpha, Action<ImDrawListPtr, Vector2, float, float, float> draw)
    {
        float rx = _short * rxFrac;
        // ry "breathes" within a bounded range rather than the ellipse rotating freely. A rotating
        // wide-flat ellipse swings its long axis to vertical twice per turn, which would periodically
        // balloon the halo down the screen instead of keeping it a compact band near head height - so
        // this only ever varies how flat the ring is, never its orientation.
        float wobblePhase = time * wobbleSpeed + DrawHelpers.HashRange(Mix(castSeed, ringSalt), 0f, MathF.PI * 2f);
        float ry = _short * ryFrac * (1f + 0.22f * MathF.Sin(wobblePhase));

        for (int i = 0; i < count; i++)
        {
            int seed = Mix(Mix(castSeed, ringSalt), i);
            float phase = MathF.PI * 2f * i / count + DrawHelpers.HashRange(seed, -0.3f, 0.3f);
            float ang = time * speed + phase;
            var pos = _anchor + new Vector2(MathF.Cos(ang) * rx, MathF.Sin(ang) * ry);

            float sizeJitter = DrawHelpers.HashRange(seed + 1, 0.85f, 1.15f);
            float twinkle = 0.6f + 0.4f * DrawHelpers.Pulse(time * DrawHelpers.HashRange(seed + 2, 1.6f, 2.4f), 1f, DrawHelpers.Hash01(seed + 3));
            draw(dl, pos, sizeJitter, twinkle, alpha);
        }
    }

    // =====================================================================================
    // Individual glyphs
    // =====================================================================================

    /// <summary>A five-point star: a soft halo, a two-tone outline (traced as one native polyline, not ten separate segments), and a hot core.</summary>
    private static void DrawStar(ImDrawListPtr dl, Vector2 center, float sizeJitter, float twinkle, float alpha)
    {
        float r = 12f * sizeJitter * (0.85f + 0.15f * twinkle);
        Span<Vector2> pts = stackalloc Vector2[11]; // 10 points + repeat the first to close the loop
        for (int i = 0; i < 10; i++)
        {
            float rr = (i % 2 == 0) ? r : r * 0.42f;
            float ang = MathF.PI * i / 5f - MathF.PI / 2f;
            pts[i] = center + new Vector2(MathF.Cos(ang) * rr, MathF.Sin(ang) * rr);
        }
        pts[10] = pts[0];

        dl.AddCircleFilled(center, r * 1.5f, C(Glow, 0.11f * twinkle, alpha));
        ref Vector2 first = ref pts[0];
        dl.AddPolyline(ref first, 11, C(Mid, 0.55f * twinkle, alpha), ImDrawFlags.None, 2.6f);
        dl.AddPolyline(ref first, 11, C(Hot, 0.95f * twinkle, alpha), ImDrawFlags.None, 1.3f);
        dl.AddCircleFilled(center, r * 0.26f, C(Hot, twinkle, alpha));
    }

    /// <summary>A wide, shallow wingbeat traced as one open polyline - the universal cartoon-bird silhouette. `twinkle` here doubles as the flap phase, so each bird in a ring flaps slightly out of sync with the others.</summary>
    private static void DrawBird(ImDrawListPtr dl, Vector2 center, float sizeJitter, float flapPhase, float alpha)
    {
        float w = 11f * sizeJitter;
        float flap = 0.55f * MathF.Sin(flapPhase * MathF.PI * 2f) - 0.05f; // wings sweep between "M" and nearly flat

        Span<Vector2> pts = stackalloc Vector2[5];
        pts[0] = center + new Vector2(-w, 0f);
        pts[1] = center + new Vector2(-w * 0.5f, -w * flap);
        pts[2] = center + new Vector2(0f, w * 0.12f);
        pts[3] = center + new Vector2(w * 0.5f, -w * flap);
        pts[4] = center + new Vector2(w, 0f);

        ref Vector2 first = ref pts[0];
        dl.AddPolyline(ref first, 5, C(Ink, 0.35f, alpha), ImDrawFlags.None, 3.6f); // pale blue-on-blue-sky is the one palette pairing here that needs contrast insurance
        dl.AddPolyline(ref first, 5, C(BirdGlow, 0.30f, alpha), ImDrawFlags.None, 5.5f);
        dl.AddPolyline(ref first, 5, C(BirdMid, 0.85f, alpha), ImDrawFlags.None, 2.6f);
        dl.AddPolyline(ref first, 5, C(BirdHot, 0.95f, alpha), ImDrawFlags.None, 1.2f);
    }

    /// <summary>A small four-point glint - a "+" cross with a bright centre, the same visual shorthand as the sparks other effects throw off, doubling here as a background texture layer.</summary>
    private static void DrawSparkle(ImDrawListPtr dl, Vector2 center, float sizeJitter, float twinkle, float alpha)
    {
        float r = 6f * sizeJitter * (0.7f + 0.3f * twinkle);
        var h = new Vector2(r, 0f);
        var v = new Vector2(0f, r);
        uint col = C(Hot, 0.8f * twinkle, alpha);
        dl.AddLine(center - h, center + h, col, 1.3f);
        dl.AddLine(center - v, center + v, col, 1.3f);
        dl.AddCircleFilled(center, r * 0.28f, C(Hot, twinkle, alpha));
    }

    // =====================================================================================
    // The moment of impact: a comic burst of spikes and a core flash, keyed off 'flash' (age-based decay)
    // =====================================================================================

    private void DrawImpact(ImDrawListPtr dl, int castSeed, float flash, float alpha)
    {
        if (flash <= 0.02f) return;

        const int spikes = 9;
        float baseAng = DrawHelpers.HashRange(Mix(castSeed, 41), 0f, MathF.PI * 2f);
        for (int i = 0; i < spikes; i++)
        {
            int seed = Mix(castSeed, 50 + i);
            float ang = baseAng + MathF.PI * 2f * i / spikes + DrawHelpers.HashRange(seed, -0.12f, 0.12f);
            float len = _short * DrawHelpers.HashRange(seed + 1, 0.045f, 0.085f) * flash;
            var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
            Vector2 tip = _anchor + dir * len;

            dl.AddLine(_anchor, tip, C(Glow, 0.35f * flash, alpha), 4.5f);
            dl.AddLine(_anchor, tip, C(Hot, 0.9f * flash, alpha), 1.8f);
        }

        dl.AddCircleFilled(_anchor, _short * 0.05f * flash, C(Glow, 0.30f * flash, alpha));
        dl.AddCircleFilled(_anchor, _short * 0.02f * flash, C(Hot, 0.9f * flash, alpha));
    }

    // =====================================================================================
    // The wooze ripple: a faint ring that washes out from the anchor once, right as the debuff lands
    // =====================================================================================

    private void DrawWooze(ImDrawListPtr dl, float age, float alpha)
    {
        float k = Saturate(age / WoozeDuration);
        if (k <= 0f || k >= 1f) return;
        float r = _short * WoozeMaxRadiusFrac * EaseOutCubic(k);
        float a = (1f - k) * WoozeMaxAlpha;
        if (r < 1f || a <= 0.002f) return;
        int segs = Math.Clamp((int)(r * 0.15f), 16, 64);
        dl.AddCircle(_anchor, r, C(Glow, a, alpha), segs, 1.4f);
    }

    // =====================================================================================
    // Ambient sparkle motes: shed from a random point on one of the rings, flung tangentially
    // (like grinding-wheel sparks) with a light downward drift, fading as they go
    // =====================================================================================

    private Vector2 MoteSpawnPos(int seed)
    {
        (float rx, float ry) = PickRingRadii(seed);
        float ang = DrawHelpers.HashRange(seed + 4, 0f, MathF.PI * 2f);
        return _anchor + new Vector2(MathF.Cos(ang) * rx, MathF.Sin(ang) * ry);
    }

    private Vector2 MoteSpawnVelocity(int seed)
    {
        (float rx, float ry) = PickRingRadii(seed);
        float ang = DrawHelpers.HashRange(seed + 4, 0f, MathF.PI * 2f); // must match MoteSpawnPos's angle - EdgeParticleField calls both with the same seed
        var tangent = new Vector2(-MathF.Sin(ang) * ry, MathF.Cos(ang) * rx);
        float len = tangent.Length();
        var dir = len > 1e-4f ? tangent / len : new Vector2(0f, -1f);
        if (DrawHelpers.Hash01(seed + 5) < 0.5f) dir = -dir; // fling with the spin either way, since rings don't all turn the same way visually once tilted

        // seed+1/seed+2 are reserved for Lifespan/Size by EdgeParticleField itself, so this starts at +6.
        float speed = DrawHelpers.HashRange(seed + 6, 18f, 46f);
        return dir * speed + new Vector2(0f, 14f); // a light downward drift, like dust settling
    }

    private (float rx, float ry) PickRingRadii(int seed)
    {
        float pick = DrawHelpers.Hash01(seed + 7);
        return pick < 0.34f ? (_short * SparkleRX, _short * SparkleRY)
             : pick < 0.67f ? (_short * StarRX, _short * StarRY)
                             : (_short * BirdRX, _short * BirdRY);
    }

    private void DrawMotes(ImDrawListPtr dl, float time, float alpha)
    {
        for (int i = 0; i < _motes.Count; i++)
        {
            ref readonly var p = ref _motes[i];
            float fade = EdgeParticleField.FadeFor((time - p.Born) / p.Lifespan);
            if (fade * alpha < 0.003f) continue;

            dl.AddCircleFilled(p.Pos, p.Size * 2.0f, C(Glow, 0.30f * fade, alpha));
            dl.AddCircleFilled(p.Pos, p.Size * 0.55f, C(Hot, 0.9f * fade, alpha));
        }
    }

    // =====================================================================================
    // Small helpers
    // =====================================================================================

    private static uint C(uint color, float amount, float alpha) => DrawHelpers.WithAlpha(color, amount * alpha);

    /// <summary>Combines two ints into a new seed. (DrawHelpers.Hash01 does the actual scrambling.)</summary>
    private static int Mix(int a, int b) => unchecked(a * 0x27D4EB2F + b * 0x165667B1 + 0x3C6EF372);

    private static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    private static float EaseOutCubic(float t) { float u = 1f - Saturate(t); return 1f - u * u * u; }
}
