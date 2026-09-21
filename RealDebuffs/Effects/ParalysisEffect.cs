using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Yellow electricity crackles around the screen edge like a live cage: a jittered perimeter with
/// bright waves of current travelling around it in both directions, occasional inward forks that
/// leap toward the centre, and hot flash nodes where the arc kinks.
///
/// The shape reseeds on a fixed tick (cheap, and reads as crackling); the pulse and the travelling
/// waves ride the shared clock continuously, so the whole thing *surges* rather than just shaking.
///
/// Coverage is deliberately patchy: a handful of slow-moving "hotspot" packets plus a persistent
/// per-segment brightness bias mean only a few arcs are lit at any moment, with real dark gaps
/// between them that drift around the ring. That's what reads as lightning rather than a glowing
/// outline.
///
/// On top of that steady hum, the first instant after the debuff lands gets its own moment: the
/// whole cage flashes brighter, one markedly bigger "trunk" bolt strikes in from the perimeter,
/// and a thin shockwave ring races outward from the centre - the difference between "you're being
/// zapped" and "you just got zapped". A scatter of short-lived sparks flung off the cage (see
/// EdgeParticleField) adds depth the same way Silence's drifting runes or Sleep's Zs do.
/// </summary>
public sealed class ParalysisEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Paralysis;

    // Reseed the jittered path on a fixed tick rather than every frame - a continuously reshuffled
    // line reads as "crackling"; reshuffling at 60+fps just looks like flat noise.
    private const float JitterInterval = 0.085f;

    // Perimeter sample count. At 1920x1080 that's ~30px per segment - jagged enough to read as
    // lightning without eating vertices. Lower = blockier, higher = smoother.
    private const int PerimeterSegments = 220;

    // Wave frequencies must be whole numbers so the crests close cleanly at the seam.
    private const float WaveSpeedSlow = 3.5f;  // crests/sec of the slow wave
    private const float WaveSpeedFast = 2.2f;  // crests/sec of the fast wave
    private const float WaveCountSlow = 6f;
    private const float WaveCountFast = 11f;

    // 3 slow-moving hotspot packets on the perimeter - narrow gaussians in arc space. These are
    // what make coverage patchy: brightness concentrates in a few sectors that drift around the ring.
    private const int HotSpotCount = 3;
    private static readonly float[] HotSpeed    = {  0.11f, -0.07f,  0.19f }; // cycles/sec, signed
    private static readonly float[] HotPhase    = {  0.13f,  0.51f,  0.82f };
    private static readonly float[] HotWidth    = {  0.045f, 0.028f, 0.070f }; // sigma in perimeter-fraction
    private static readonly float[] HotStrength = {  1.00f,  1.35f,  0.65f };

    // Segments below this combined brightness are skipped entirely - the dark arcs between the
    // hotspots. Raise it for more black space, lower it for a denser cage.
    private const float PerimeterCutoff = 0.16f;

    private const int BoltSlots = 5;   // how many inward bolts we test for each tick
    private const int NodeSlots = 6;   // how many hot flash nodes we test for each tick
    private const int BranchSteps = 4;
    private const int BoltSteps = 9;

    // ---- the moment of impact: everything below decays with age, not the tick clock ----
    private const float NewCastGapSeconds = 1.0f; // long enough that an ordinary frame hitch mid-fight never replays the strike
    private const float StrikeDecaySeconds = 0.22f; // how fast the initial brightness spike fades - a jolt, not a fade
    private const float StrikeBoost = 1.2f; // steady-state brightness roughly doubles for an instant right as the cage lands
    private const float ShockRingDuration = 0.5f; // seconds for the shockwave ring to race out and fade

    // Cached per-tick geometry: regenerated when the tick or the window size changes, so the
    // expensive hash/trig walk happens ~12x/sec rather than every frame.
    private readonly Vector2[] _perimeter = new Vector2[PerimeterSegments];
    private int _cachedTick = int.MinValue;
    private Vector2 _cachedSize;

    // Baked once: each segment's permanent brightness weight, so the ring never looks uniform
    // even when the travelling waves align. Assigned lazily on first regenerate.
    private readonly float[] _segBias = new float[PerimeterSegments];
    private bool _biasReady;

    // Cast tracking, exactly like Silence's: a gap since the last Draw call means the debuff was
    // just (re)applied, so the strike/shockwave/trunk-bolt sequence restarts from age zero.
    private float _lastDrawTime = -100f;
    private float _castStart;

    // Cached per frame so the spark spawn delegates below (called by EdgeParticleField, which only
    // passes a seed) don't need screenSize threaded through as an extra parameter.
    private Vector2 _screenSize;

    // Sparks flung off the cage - a scatter of short-lived flecks for depth, the same role Silence's
    // drifting runes or Sleep's Zs play. Custom-drawn circles rather than glyphs (see DrawSparks),
    // the same way Poison draws its own bubbles.
    private readonly EdgeParticleField _sparks = new(maxParticles: 24, seedSalt: 0x54A2C5);
    private readonly Func<int, Vector2> _sparkPos;
    private readonly Func<int, Vector2> _sparkVel;
    private readonly Func<int, string> _noGlyph;

    public ParalysisEffect()
    {
        _sparkPos = SparkSpawnPos;
        _sparkVel = SparkSpawnVelocity;
        _noGlyph = static _ => "";
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        // A gap since the last Draw call means the debuff was just (re)applied - EffectManager
        // stops calling Draw once an effect has fully faded out, so this only fires on a fresh hit.
        if (time - _lastDrawTime > NewCastGapSeconds) _castStart = time;
        _lastDrawTime = time;
        float age = time - _castStart;
        _screenSize = screenSize;

        int tick = (int)(time / JitterInterval);

        // Global surge envelope: fast attack, slower echo, low hum underneath. The whole cage
        // brightens and dims together, like the vanilla paralysis VFX. On top of that, "strike"
        // is a sharp one-off spike at age zero - unlike Silence's slow ritual cast-in, a lightning
        // strike is instant, so there's no build-up, just a jolt that decays into the steady hum.
        float strike = MathF.Exp(-age / StrikeDecaySeconds);
        float a = alpha * Pulse(time) * (1f + StrikeBoost * strike);
        if (a <= 0.001f) return;

        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        float inset = shortSide * 0.009f;
        float jitter = shortSide * 0.020f;

        // Yellow-white hot core, amber mid, warm glow.
        uint hot  = DrawHelpers.ToU32(1.00f, 0.98f, 0.72f, 1f);
        uint mid  = DrawHelpers.ToU32(1.00f, 0.86f, 0.28f, 1f);
        uint glow = DrawHelpers.ToU32(1.00f, 0.62f, 0.10f, 1f);

        if (tick != _cachedTick || screenSize != _cachedSize)
        {
            RegeneratePerimeter(tick, screenSize, inset, jitter);
            _cachedTick = tick;
            _cachedSize = screenSize;
        }

        // Steady-state layers. These all take the already strike-boosted 'a', so the whole cage
        // flashes brighter for an instant on a fresh hit with no extra work in any of them.
        DrawPerimeter(dl, a, time, hot, mid, glow, tick);
        DrawHotNodes(dl, a, tick, hot, glow);
        DrawInwardForks(dl, screenSize, shortSide, a, time, hot, mid, glow, tick);

        // One-off moment-of-impact layers. These key off plain 'alpha' and their own age-based
        // envelope rather than 'a', so they stay crisp instead of also riding the ambient hum.
        DrawTrunkBolt(dl, screenSize, shortSide, alpha, strike, time, hot, mid, glow);
        DrawShockRing(dl, screenSize, age, alpha, hot, mid, glow);

        // Sparks flung off the cage - denser for the first instant, then settling to a slow trickle.
        _sparks.Update(
            time, ImGui.GetIO().DeltaTime,
            spawnIntervalMin: strike > 0.3f ? 0.02f : 0.06f, spawnIntervalMax: strike > 0.3f ? 0.06f : 0.16f,
            spawnPos: _sparkPos, spawnVelocity: _sparkVel,
            pickGlyph: _noGlyph,
            lifespanMin: 0.22f, lifespanMax: 0.5f,
            sizeMin: 2f, sizeMax: 4.5f);
        DrawSparks(dl, time, a, hot, mid, glow);
    }

    // =====================================================================================
    // The perimeter: three stacked passes, alpha modulated by waves + slow hotspots + noise
    // =====================================================================================

    private void DrawPerimeter(ImDrawListPtr dl, float a, float time, uint hot, uint mid, uint glow, int tick)
    {
        const float tau = MathF.PI * 2f;

        for (int i = 0; i < PerimeterSegments; i++)
        {
            float arcT = (float)i / PerimeterSegments;

            // Two counter-rotating waves, cubed so crests are narrow/bright and valleys dark.
            float s1 = 0.5f + 0.5f * MathF.Sin(arcT * tau * WaveCountSlow - time * WaveSpeedSlow);
            float s2 = 0.5f + 0.5f * MathF.Sin(arcT * tau * WaveCountFast + time * WaveSpeedFast);
            float wave = MathF.Max(s1 * s1 * s1, s2 * s2 * s2 * 0.7f);

            // Slow-moving hotspot packets - narrow gaussians in perimeter-arc space. Their whole
            // point is that most of the ring sits below the cutoff most of the time.
            float hotspot = 0f;
            for (int h = 0; h < HotSpotCount; h++)
            {
                float pos = Frac(time * HotSpeed[h] + HotPhase[h]);
                float d = Frac(arcT - pos + 0.5f) - 0.5f; // signed wrapped distance, -0.5..0.5
                float g = MathF.Exp(-d * d / (HotWidth[h] * HotWidth[h])) * HotStrength[h];
                if (g > hotspot) hotspot = g;
            }

            // Persistent spatial bias * per-tick noise. Persistent layer gives the ring a stable
            // asymmetry; tick layer makes it flicker like real electricity on top of that.
            float spatial  = _segBias[i];
            float temporal = DrawHelpers.Hash01(unchecked(tick * 131 + i * 977));
            float noise    = 0.55f * spatial + 0.45f * temporal;

            float bright = noise * (0.10f + 0.40f * wave + 0.95f * hotspot);

            // Hard cutoff -> real dark arcs between the hotspots. Because bright is a smooth field,
            // the skipped segments form continuous gaps, not salt-and-pepper holes.
            if (bright < PerimeterCutoff) continue;

            Vector2 p0 = _perimeter[i];
            Vector2 p1 = _perimeter[(i + 1) % PerimeterSegments];

            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(glow, a * bright * 0.32f), 11f);
            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(mid,  a * bright * 0.62f), 4.5f);
            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(hot,  a * bright * 0.95f), 1.7f);
        }
    }

    // =====================================================================================
    // Hot flash nodes: a handful of vertices bloom bright this tick, like sparks popping
    // =====================================================================================

    private void DrawHotNodes(ImDrawListPtr dl, float a, int tick, uint hot, uint glow)
    {
        int baseSeed = unchecked(tick * 4099);

        for (int k = 0; k < NodeSlots; k++)
        {
            int s = unchecked(baseSeed + k * 7919);
            if (DrawHelpers.Hash01(s) > 0.45f) continue; // most slots stay dark most ticks

            int idx = (int)(DrawHelpers.Hash01(s + 1) * PerimeterSegments);
            Vector2 p = _perimeter[idx];
            float flash = DrawHelpers.HashRange(s + 2, 0.5f, 1f);

            dl.AddCircleFilled(p, 9f * flash, DrawHelpers.WithAlpha(glow, a * 0.30f));
            dl.AddCircleFilled(p, 5f * flash, DrawHelpers.WithAlpha(hot,  a * 0.75f));
        }
    }

    // =====================================================================================
    // Inward forks: short jagged bolts leaping from the perimeter toward the centre
    // =====================================================================================

    private void DrawInwardForks(
        ImDrawListPtr dl, Vector2 screenSize, float shortSide, float a, float time,
        uint hot, uint mid, uint glow, int tick)
    {
        var centre = new Vector2(screenSize.X * 0.5f, screenSize.Y * 0.5f);

        for (int k = 0; k < BoltSlots; k++)
        {
            int s = unchecked(tick * 131 + k * 7919);
            if (DrawHelpers.Hash01(s) > 0.55f) continue; // roughly half the slots fire per tick

            int idx = (int)(DrawHelpers.Hash01(s + 1) * PerimeterSegments);
            Vector2 origin = _perimeter[idx];

            // Aim roughly inward, with a random spread so bolts don't all cross the same way.
            Vector2 toCentre = centre - origin;
            float baseAng = MathF.Atan2(toCentre.Y, toCentre.X);
            float ang = baseAng + DrawHelpers.HashRange(s + 2, -0.5f, 0.5f);
            Vector2 dir = new(MathF.Cos(ang), MathF.Sin(ang));

            float len = shortSide * DrawHelpers.HashRange(s + 3, 0.09f, 0.22f);
            DrawBolt(dl, origin, dir, len, s + 4, a * 0.9f, time, hot, mid, glow);
        }
    }

    // =====================================================================================
    // The moment of impact: a bigger bolt, a shockwave ring, and a burst of sparks - all keyed
    // off 'age' (time since this cast started) rather than the tick clock, so they happen once
    // per hit instead of recurring every reseed.
    // =====================================================================================

    /// <summary>
    /// One markedly bigger, brighter bolt right as the debuff lands - the difference between the
    /// ambient crackle and an actual strike hitting home. Anchored to _castStart rather than the
    /// regenerate tick, so it's the same strike for its whole (brief) life, not a new one every
    /// 85ms - though its exact path still re-jitters each tick along with the rest of the cage,
    /// which reads as the bolt writhing rather than snapping to a new position.
    /// </summary>
    private void DrawTrunkBolt(ImDrawListPtr dl, Vector2 screenSize, float shortSide, float alpha, float strike, float time, uint hot, uint mid, uint glow)
    {
        if (strike <= 0.03f) return;

        int seed = unchecked((int)(_castStart * 9973f) + 5153);
        int idx = (int)(DrawHelpers.Hash01(seed) * PerimeterSegments);
        Vector2 origin = _perimeter[idx];

        var centre = new Vector2(screenSize.X * 0.5f, screenSize.Y * 0.5f);
        Vector2 toCentre = centre - origin;
        float baseAng = MathF.Atan2(toCentre.Y, toCentre.X);
        float ang = baseAng + DrawHelpers.HashRange(seed + 1, -0.3f, 0.3f);
        Vector2 dir = new(MathF.Cos(ang), MathF.Sin(ang));

        // Much longer than a steady-state fork (0.09-0.22x): this one is meant to read as reaching
        // deep toward the centre, not just licking in from the edge.
        float len = shortSide * DrawHelpers.HashRange(seed + 2, 0.55f, 0.85f);
        DrawBolt(dl, origin, dir, len, seed + 3, alpha * strike * 1.1f, time, hot, mid, glow, widthScale: 1.8f);
    }

    /// <summary>A thin ring racing outward from screen centre once, right as the cage locks in - the same beat as Silence's lock ripple, sped up to suit an instant zap instead of a slow seal closing.</summary>
    private void DrawShockRing(ImDrawListPtr dl, Vector2 screenSize, float age, float alpha, uint hot, uint mid, uint glow)
    {
        float k = Saturate(age / ShockRingDuration);
        if (k <= 0f || k >= 1f) return;

        var centre = new Vector2(screenSize.X * 0.5f, screenSize.Y * 0.5f);
        float maxR = MathF.Max(screenSize.X, screenSize.Y) * 0.7f;
        float r = maxR * EaseOutCubic(k);
        float fade = 1f - k;
        int segs = Math.Clamp((int)(r * 0.03f), 24, 96);

        dl.AddCircle(centre, r, DrawHelpers.WithAlpha(glow, alpha * fade * 0.30f), segs, 16f);
        dl.AddCircle(centre, r, DrawHelpers.WithAlpha(mid,  alpha * fade * 0.55f), segs, 6f);
        dl.AddCircle(centre, r, DrawHelpers.WithAlpha(hot,  alpha * fade * 0.85f), segs, 2f);
    }

    /// <summary>Small glowing flecks flung off the cage - custom-drawn circles rather than glyphs, the same way Poison draws its own bubbles instead of using EdgeParticleField.DrawGlyphs.</summary>
    private void DrawSparks(ImDrawListPtr dl, float time, float a, uint hot, uint mid, uint glow)
    {
        for (int i = 0; i < _sparks.Count; i++)
        {
            ref readonly var p = ref _sparks[i];
            float fade = EdgeParticleField.FadeFor((time - p.Born) / p.Lifespan);
            float sparkA = a * fade;
            if (sparkA < 0.002f) continue;

            dl.AddCircleFilled(p.Pos, p.Size * 2.2f, DrawHelpers.WithAlpha(glow, sparkA * 0.35f));
            dl.AddCircleFilled(p.Pos, p.Size,        DrawHelpers.WithAlpha(mid,  sparkA * 0.70f));
            dl.AddCircleFilled(p.Pos, p.Size * 0.5f, DrawHelpers.WithAlpha(hot,  sparkA * 0.95f));
        }
    }

    // A spark is born at a random point on the CURRENT jittered perimeter (so it visually belongs
    // to the cage, not a generic screen edge) and flung outward, away from centre, with some
    // angular spread so they scatter rather than all flying the same way. EdgeParticleField calls
    // both delegates with the same seed, so they agree on which perimeter point was picked.
    private Vector2 SparkSpawnPos(int seed)
    {
        int idx = (int)(DrawHelpers.Hash01(seed) * PerimeterSegments);
        return _perimeter[idx];
    }

    private Vector2 SparkSpawnVelocity(int seed)
    {
        int idx = (int)(DrawHelpers.Hash01(seed) * PerimeterSegments);
        Vector2 p = _perimeter[idx];
        Vector2 centre = _screenSize * 0.5f;
        Vector2 outward = p - centre;
        float len = outward.Length();
        Vector2 dir = len > 1f ? outward / len : DrawHelpers.V(0f, -1f);

        // seed+1/seed+2 are reserved for Lifespan/Size by EdgeParticleField itself, so this starts at +6.
        float spread = DrawHelpers.HashRange(seed + 6, -0.6f, 0.6f);
        float c = MathF.Cos(spread), s = MathF.Sin(spread);
        Vector2 d2 = new(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);

        float speed = DrawHelpers.HashRange(seed + 7, 40f, 110f);
        return d2 * speed;
    }

    private static void DrawBolt(
        ImDrawListPtr dl, Vector2 origin, Vector2 dir, float length, int seed,
        float alpha, float time, uint hot, uint mid, uint glow, float widthScale = 1f)
    {
        Vector2 perp = new(-dir.Y, dir.X);
        Vector2 step = dir * (length / BoltSteps);
        Vector2 prev = origin;

        // Each segment's brightness travels along the bolt, so the whole thing reads as a live
        // arc with current running down it rather than a static line.
        for (int i = 1; i <= BoltSteps; i++)
        {
            float j = (DrawHelpers.Hash01(unchecked(seed + i * 977)) - 0.5f) * 2f * (length * 0.05f);
            Vector2 pos = prev + step + perp * j;

            float t = (float)i / BoltSteps;
            float s = 0.5f + 0.5f * MathF.Sin((t * 2.2f - time * 6f) * MathF.PI * 2f);
            float w = s * s;
            float segA = alpha * (0.55f + 0.45f * w);

            dl.AddLine(prev, pos, DrawHelpers.WithAlpha(glow, segA * 0.30f), 9f * widthScale);
            dl.AddLine(prev, pos, DrawHelpers.WithAlpha(mid,  segA * 0.65f), 4f * widthScale);
            dl.AddLine(prev, pos, DrawHelpers.WithAlpha(hot,  segA * 0.95f), 1.8f * widthScale);

            // Small chance per segment of a branch shooting off at a steep angle.
            if (i > 1 && i < BoltSteps - 1 && DrawHelpers.Hash01(unchecked(seed + i * 131)) > 0.72f)
            {
                float bAng = MathF.Atan2(dir.Y, dir.X) + DrawHelpers.HashRange(seed + i, -1.2f, 1.2f);
                Vector2 bDir = new(MathF.Cos(bAng), MathF.Sin(bAng));
                DrawShortBranch(dl, pos, bDir, length * 0.35f, segA * 0.8f, unchecked(seed + i * 4099), hot, mid, glow, widthScale);
            }

            prev = pos;
        }
    }

    private static void DrawShortBranch(
        ImDrawListPtr dl, Vector2 origin, Vector2 dir, float length, float alpha,
        int seed, uint hot, uint mid, uint glow, float widthScale = 1f)
    {
        Vector2 perp = new(-dir.Y, dir.X);
        Vector2 step = dir * (length / BranchSteps);
        Vector2 prev = origin;

        for (int i = 1; i <= BranchSteps; i++)
        {
            float j = (DrawHelpers.Hash01(unchecked(seed + i * 977)) - 0.5f) * 2f * (length * 0.08f);
            Vector2 pos = prev + step + perp * j;
            float fade = 1f - (float)i / BranchSteps;

            dl.AddLine(prev, pos, DrawHelpers.WithAlpha(glow, alpha * fade * 0.28f), 6f * widthScale);
            dl.AddLine(prev, pos, DrawHelpers.WithAlpha(mid,  alpha * fade * 0.60f), 2.5f * widthScale);
            dl.AddLine(prev, pos, DrawHelpers.WithAlpha(hot,  alpha * fade * 0.90f), 1.2f * widthScale);

            prev = pos;
        }
    }

    // =====================================================================================
    // Perimeter generation (once per jitter tick)
    // =====================================================================================

    private void RegeneratePerimeter(int tick, Vector2 screenSize, float inset, float jitter)
    {
        // Bake the persistent per-segment bias exactly once. It never changes, so segments have a
        // stable "personality" and the ring is never a perfect uniform outline even on frames
        // where every wave happens to align.
        if (!_biasReady)
        {
            for (int i = 0; i < PerimeterSegments; i++)
                _segBias[i] = DrawHelpers.HashRange(unchecked(i * 7919 + 13), 0.30f, 1f);
            _biasReady = true;
        }

        float w = screenSize.X - inset * 2f;
        float h = screenSize.Y - inset * 2f;
        float perim = 2f * (w + h);
        float stepLen = perim / PerimeterSegments;

        for (int i = 0; i < PerimeterSegments; i++)
        {
            float s = i * stepLen;
            Vector2 pos;
            Vector2 tangent;

            if (s < w)
            {
                pos = new Vector2(inset + s, inset);
                tangent = new Vector2(1f, 0f);
            }
            else if (s < w + h)
            {
                float u = s - w;
                pos = new Vector2(screenSize.X - inset, inset + u);
                tangent = new Vector2(0f, 1f);
            }
            else if (s < 2f * w + h)
            {
                float u = s - w - h;
                pos = new Vector2(screenSize.X - inset - u, screenSize.Y - inset);
                tangent = new Vector2(-1f, 0f);
            }
            else
            {
                float u = s - 2f * w - h;
                pos = new Vector2(inset, screenSize.Y - inset - u);
                tangent = new Vector2(0f, -1f);
            }

            // Push the point perpendicular to the edge by a hashed amount. Perp is derived from
            // the tangent directly (no atan2), so this loop stays a handful of adds per vertex.
            Vector2 perp = new(-tangent.Y, tangent.X);
            int hSeed = unchecked(tick * 977 + i * 131);
            float off = (DrawHelpers.Hash01(hSeed) - 0.5f) * 2f * jitter;
            _perimeter[i] = pos + perp * off;
        }
    }

    // =====================================================================================
    // Timing
    // =====================================================================================

    /// <summary>
    /// Fast-attack / slow-decay surge with a low hum underneath. Two peaks per cycle reads as the
    /// classic "buzz - BUZZ" of vanilla lightning rather than a single slow throb.
    /// </summary>
    private static float Pulse(float time)
    {
        const float period = 1.35f;
        float p = (time % period) / period;
        float peak1 = MathF.Exp(-p * 14f);
        float peak2 = 0.65f * MathF.Exp(-MathF.Abs(p - 0.58f) * 16f);
        float hum = 0.32f + 0.12f * MathF.Sin(time * 9f);
        return MathF.Min(1f, hum + peak1 + peak2);
    }

    private static float Frac(float x) => x - MathF.Floor(x);

    private static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    private static float EaseOutCubic(float t) { float u = 1f - Saturate(t); return 1f - u * u * u; }
}