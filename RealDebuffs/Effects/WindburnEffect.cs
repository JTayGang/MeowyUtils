using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Windburn: constant abrasion, a wind that won't let up. A visible gust slams across the frame
/// as the intro, then the steady state settles into layered horizontal motion: a slow heavy haze
/// drifting one way, fast thin streaks whipping past, and small dust motes carried along. Every
/// few seconds a stronger gust pushes through, sweeping the whole effect faster and brighter.
///
/// Wind direction is fixed per cast (chosen from the cast seed), so the whole thing moves
/// coherently left-to-right or right-to-left - the way real wind does.
///
/// Layers, back to front:
///   1. pale desaturated wash, breathing slightly
///   2. warm-tinted vignette that thickens during gusts
///   3. a background drift of large slow haze streaks
///   4. the main streak field (fast, thin, bright)
///   5. dust motes riding the wind
///   6. the intro gust wave (only during the first ~1.2s)
/// </summary>
public sealed class WindburnEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Windburn;

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f;

    // ---- palette: desaturated warm gray / sand ----
    private static readonly uint Dusk    = DrawHelpers.ToU32(0.28f, 0.26f, 0.24f, 1f); // dark dusty base
    private static readonly uint Sand    = DrawHelpers.ToU32(0.62f, 0.60f, 0.56f, 1f); // body streak / dust
    private static readonly uint Bright  = DrawHelpers.ToU32(0.85f, 0.84f, 0.82f, 1f); // bright streak edge
    private static readonly uint Hot     = DrawHelpers.ToU32(0.96f, 0.95f, 0.92f, 1f); // near-white peak

    // ---- streaks ----
    private readonly EdgeParticleField _streaks = new(maxParticles: 60, seedSalt: 0x10000D);

    // ---- dust motes ----
    private readonly EdgeParticleField _dust = new(maxParticles: 40, seedSalt: 0x10000E);

    // ---- cached delegates ----
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _streakPos;
    private readonly Func<int, Vector2> _streakVel;
    private readonly Func<int, Vector2> _dustPos;
    private readonly Func<int, Vector2> _dustVel;
    private readonly Func<int, string>  _noGlyph;

    // ---- per-cast wind direction ----
    private float _windDir;

    // ---- intro ----
    private float _lastDrawTime = -100f;
    private float _castStart;

    public WindburnEffect()
    {
        _streakPos = StreakSpawnPos;
        _streakVel = StreakSpawnVel;
        _dustPos   = DustSpawnPos;
        _dustVel   = DustSpawnVel;
        _noGlyph   = static _ => "";
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
            _windDir = DrawHelpers.Hash01(unchecked((int)(_castStart * 1000f))) < 0.5f ? 1f : -1f;
        }
        _lastDrawTime = time;
        float age = time - _castStart;

        // Intro shape:
        //   0.00 - 1.20s: a gust wave sweeps across in the wind direction (bright band)
        //   0.00 - 0.60s: base wash and vignette ramp in
        //   0.20s+:       steady streak emission
        //   0.60s+:       dust motes start
        float gustWave  = IntroGust(age);
        float baseAlpha = Math.Clamp(age / 0.60f, 0f, 1f);

        // Occasional gusts: 3 strong pulses per 10s, offset so they don't align.
        float gustPulse = AmbientGust(time);

        // Total current wind intensity - how fast/bright everything moves. 1.0 = calm, up to ~1.8 at gust peak.
        float wind = 1f + 0.8f * MathF.Max(gustWave * 0.5f, gustPulse);

        // Breathing wash, oscillating gently with gusts.
        float breath = 0.5f + 0.5f * DrawHelpers.Pulse(time, 4.5f);

        // 1) flat wash
        float washA = baseAlpha * (0.16f + 0.03f * breath + 0.05f * gustPulse);
        dl.AddRectFilled(DrawHelpers.V(0, 0), screenSize,
            DrawHelpers.WithAlpha(Dusk, alpha * washA));

        // 2) vignette - a bit stronger on gust peaks
        float vigT = 0.10f + 0.03f * gustPulse;
        float vigA = baseAlpha * (0.55f + 0.20f * gustPulse);
        DrawHelpers.DrawVignette(dl, screenSize, Dusk, vigT, alpha * vigA);

        // 3) background haze streaks - large, slow, dim, drifting the same direction.
        DrawHazeLayer(dl, screenSize, shortSide, alpha, time, wind);

        // 4) main streak field
        if (age > 0.20f)
        {
            _streaks.Update(
                time, dt,
                spawnIntervalMin: 0.020f / wind, spawnIntervalMax: 0.075f / wind,
                spawnPos: _streakPos, spawnVelocity: _streakVel,
                pickGlyph: _noGlyph,
                lifespanMin: 0.5f, lifespanMax: 1.3f,
                sizeMin: shortSide * 0.045f, sizeMax: shortSide * 0.160f);

            DrawStreaks(dl, alpha, time, wind);
        }

        // 5) dust motes
        if (age > 0.60f)
        {
            _dust.Update(
                time, dt,
                spawnIntervalMin: 0.03f / wind, spawnIntervalMax: 0.12f / wind,
                spawnPos: _dustPos, spawnVelocity: _dustVel,
                pickGlyph: _noGlyph,
                lifespanMin: 0.8f, lifespanMax: 2.0f,
                sizeMin: 1.2f, sizeMax: 3.2f);

            DrawDust(dl, alpha, time, wind);
        }

        // 6) intro gust wave
        if (gustWave > 0.001f)
            DrawGustWave(dl, screenSize, shortSide, alpha, gustWave);
    }

    // =====================================================================================
    // Intro / gust curves
    // =====================================================================================

    /// <summary>
    /// Intro gust wave: a quick brightening spike at t≈0.15 then a slow decay - the gust slams
    /// in suddenly then takes a moment to blow through.
    /// </summary>
    private static float IntroGust(float age)
    {
        if (age < 0f || age > 1.20f) return 0f;
        float t = age / 1.20f;
        if (t < 0.12f) return t / 0.12f;
        return 1f - (t - 0.12f) / 0.88f;
    }

    /// <summary>Periodic ambient gusts, one every ~3.3s with slight jitter, each ~0.7s long.</summary>
    private static float AmbientGust(float time)
    {
        const float period = 3.3f;
        float p = (time % period) / period;
        // A single smooth bump in the middle of each period.
        return Bump(p, 0.4f, 0.10f);
    }

    private static float Bump(float x, float center, float width)
    {
        float d = (x - center) / width;
        return MathF.Exp(-d * d);
    }

    // =====================================================================================
    // Haze layer (background slow streaks)
    // =====================================================================================

    private void DrawHazeLayer(ImDrawListPtr dl, Vector2 screenSize, float shortSide,
                               float alpha, float time, float wind)
    {
        // 8 evenly distributed long streaks, each with its own phase drift, all moving the same way.
        const int count = 8;
        for (int i = 0; i < count; i++)
        {
            int s = unchecked(0x1A2E + i * 4271);
            float y = (0.08f + (i / (float)count) * 0.84f) * screenSize.Y;
            float speed = 0.10f + 0.03f * DrawHelpers.Hash01(s);
            float len = shortSide * DrawHelpers.HashRange(s + 1, 0.35f, 0.65f);

            // Cycle the head off-screen and wrap.
            float cycle = (time * speed * wind * _windDir + DrawHelpers.Hash01(s + 2));
            float headX = Wrap(cycle, screenSize.X + 2f * len) - len;

            Vector2 head = new(headX, y);
            Vector2 tail = head - new Vector2(_windDir * len, 0f);

            float a = alpha * DrawHelpers.HashRange(s + 3, 0.10f, 0.20f);
            dl.AddLine(tail, head, DrawHelpers.WithAlpha(Sand, a), shortSide * 0.020f);
        }
    }

    /// <summary>Keeps a value in [0, range) - wrap helper for the haze layer.</summary>
    private static float Wrap(float v, float range)
    {
        v %= range;
        if (v < 0f) v += range;
        return v;
    }

    // =====================================================================================
    // Streaks
    // =====================================================================================

    private void DrawStreaks(ImDrawListPtr dl, float alpha, float time, float wind)
    {
        for (int i = 0; i < _streaks.Count; i++)
        {
            ref readonly var p = ref _streaks[i];
            float age = time - p.Born;
            float fade = EdgeParticleField.FadeFor(age / p.Lifespan);
            float a = alpha * fade * (0.70f + 0.30f * MathF.Min(1f, wind - 1f));
            if (a < 0.005f) continue;

            // Slight vertical drift so streaks don't ride a perfect rail.
            float yWobble = MathF.Sin(age * 2.5f + p.Born * 3.1f) * 2.5f;
            Vector2 head = p.Pos + new Vector2(0f, yWobble);
            Vector2 tail = head - new Vector2(_windDir * p.Size, 0f);

            // Layered: dark outline, mid body, bright leading edge, hot tip.
            dl.AddLine(tail, head, DrawHelpers.WithAlpha(Dusk,   a * 0.35f), 3.4f);
            dl.AddLine(tail, head, DrawHelpers.WithAlpha(Sand,   a * 0.75f), 1.7f);
            dl.AddLine(tail, head, DrawHelpers.WithAlpha(Bright, a * 0.95f), 0.9f);

            // A small bright "head" where the streak is brightest.
            Vector2 headPoint = head + new Vector2(_windDir * 3f, 0f);
            dl.AddCircleFilled(headPoint, 1.4f, DrawHelpers.WithAlpha(Hot, a * 0.85f));
        }
    }

    // =====================================================================================
    // Dust
    // =====================================================================================

    private void DrawDust(ImDrawListPtr dl, float alpha, float time, float wind)
    {
        for (int i = 0; i < _dust.Count; i++)
        {
            ref readonly var p = ref _dust[i];
            float age = time - p.Born;
            float fade = EdgeParticleField.FadeFor(age / p.Lifespan);
            float a = alpha * fade;
            if (a < 0.01f) continue;

            float yWobble = MathF.Sin(age * 3.2f + p.Born * 2.7f) * 3f;
            Vector2 pos = p.Pos + new Vector2(0f, yWobble);

            dl.AddCircleFilled(pos, p.Size * 1.7f, DrawHelpers.WithAlpha(Sand,   a * 0.20f));
            dl.AddCircleFilled(pos, p.Size,        DrawHelpers.WithAlpha(Sand,   a * 0.70f));
            dl.AddCircleFilled(pos, p.Size * 0.4f, DrawHelpers.WithAlpha(Bright, a * 0.90f));
        }
    }

    // =====================================================================================
    // Intro gust wave
    // =====================================================================================

    /// <summary>
    /// The intro gust: a broad bright band that sweeps across the frame in the wind direction
    /// once, from the windward edge to the leeward edge, over ~1.2s. Sells the moment the gust
    /// first hits.
    /// </summary>
    private void DrawGustWave(ImDrawListPtr dl, Vector2 screenSize, float shortSide,
                              float alpha, float gust)
    {
        // Position of the wave head: from windward off-screen to leeward off-screen.
        float progress = 1f - gust; // 0 at start, 1 at end
        float headX = _windDir > 0f
            ? -screenSize.X * 0.3f + progress * screenSize.X * 1.6f
            :  screenSize.X * 1.3f - progress * screenSize.X * 1.6f;

        float bandWidth = screenSize.X * 0.35f;
        float tailX = headX - _windDir * bandWidth;

        // Draw as a horizontal gradient strip that fades to nothing at both ends.
        uint mid  = DrawHelpers.WithAlpha(Bright, alpha * gust * 0.35f);
        uint edge = DrawHelpers.WithAlpha(Bright, 0f);

        float x0 = MathF.Min(tailX, headX);
        float x1 = MathF.Max(tailX, headX);

        // Fade in/out along the direction of travel.
        dl.AddRectFilledMultiColor(
            new Vector2(x0, 0f),
            new Vector2(x1, screenSize.Y),
            _windDir > 0f ? edge : mid,  _windDir > 0f ? mid : edge,
            _windDir > 0f ? mid : edge,  _windDir > 0f ? edge : mid);

        // A brighter leading edge line at the head of the wave.
        dl.AddLine(new Vector2(headX, 0f), new Vector2(headX, screenSize.Y),
                   DrawHelpers.WithAlpha(Hot, alpha * gust * 0.55f),
                   shortSide * 0.008f);
    }

    // =====================================================================================
    // Spawn
    // =====================================================================================

    private Vector2 StreakSpawnPos(int seed)
    {
        // Spawn on the windward vertical edge, at a random height.
        float y = DrawHelpers.HashRange(seed, -0.05f, 1.05f) * _screenSize.Y;
        return _windDir > 0f
            ? new Vector2(-_screenSize.X * 0.06f, y)
            : new Vector2(_screenSize.X * 1.06f, y);
    }

    private Vector2 StreakSpawnVel(int seed)
    {
        // Big and slow, or thin and fast - bimodal so the streak field has depth.
        bool big = DrawHelpers.Hash01(seed + 40) < 0.35f;
        float speed = big
            ? DrawHelpers.HashRange(seed + 1, 280f, 480f)
            : DrawHelpers.HashRange(seed + 1, 700f, 1400f);
        float vy = DrawHelpers.HashRange(seed + 2, -30f, 30f);
        return new Vector2(_windDir * speed, vy);
    }

    private Vector2 DustSpawnPos(int seed)
    {
        float y = DrawHelpers.HashRange(seed, -0.05f, 1.05f) * _screenSize.Y;
        return _windDir > 0f
            ? new Vector2(-_screenSize.X * 0.05f, y)
            : new Vector2(_screenSize.X * 1.05f, y);
    }

    private Vector2 DustSpawnVel(int seed)
    {
        float speed = DrawHelpers.HashRange(seed + 1, 200f, 500f);
        float vy = DrawHelpers.HashRange(seed + 2, -40f, 40f);
        return new Vector2(_windDir * speed, vy);
    }
}