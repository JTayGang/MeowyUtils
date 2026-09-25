using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Bleeding: a wound leaking damage over time. A sharp red flash marks the injury, then the
/// edges darken with dried blood, wet streaks start running down from the top and upper sides,
/// and fresh drips begin to bead and fall. A slow lub-dub heartbeat runs under everything - this
/// is a living wound, not a static overlay.
///
/// Layers, back to front:
///   1. flat red wash, pulsing on the heartbeat (plus a bright flash during the intro)
///   2. dark red vignette, thicker on each beat
///   3. persistent stains running down from the edges, growing during the intro then holding
///   4. blood pooling at the bottom edge, slowly deepening
///   5. falling drips with motion-blur trails, drawn last so they slide over everything
/// </summary>
public sealed class BleedingEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Bleeding;

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f;

    // ---- palette: wound red ----
    private static readonly uint Deep   = DrawHelpers.ToU32(0.38f, 0.012f, 0.020f, 1f); // dried / dark
    private static readonly uint Mid    = DrawHelpers.ToU32(0.62f, 0.030f, 0.040f, 1f); // body blood
    private static readonly uint Bright = DrawHelpers.ToU32(0.85f, 0.075f, 0.075f, 1f); // fresh blood
    private static readonly uint Wet    = DrawHelpers.ToU32(0.96f, 0.16f, 0.14f, 1f);   // hot / wet highlight

    // ---- persistent stains at the edges ----
    private const int StainCount = 16;

    private struct Stain
    {
        public byte  Edge;         // 0 = top, 1 = left, 2 = right
        public float Along;        // 0..1 along that edge
        public float TargetLen;    // shortSide fraction
        public float GrowthDelay;  // seconds after cast before this stain starts growing
        public float BaseAlpha;
        public int   Seed;
    }

    private readonly Stain[] _stains = new Stain[StainCount];

    // ---- falling drips ----
    private readonly EdgeParticleField _drips = new(maxParticles: 44, seedSalt: 0xB1EED);

    // ---- cached spawn delegates ----
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _dripPos;
    private readonly Func<int, Vector2> _dripVel;
    private readonly Func<int, string>  _noGlyph;

    // ---- intro ----
    private float _lastDrawTime = -100f;
    private float _castStart;

    public BleedingEffect()
    {
        _dripPos = DripSpawnPos;
        _dripVel = DripSpawnVel;
        _noGlyph = static _ => "";

        for (int i = 0; i < StainCount; i++)
        {
            int s = unchecked(0xB10000 + i * 7919);

            // Most stains run down from the top edge; some slide down the upper sides.
            float roll = DrawHelpers.Hash01(s);
            byte edge = roll < 0.62f ? (byte)0 : roll < 0.81f ? (byte)1 : (byte)2;

            _stains[i] = new Stain
            {
                Edge        = edge,
                Along       = DrawHelpers.HashRange(s + 1, 0.05f, 0.95f),
                TargetLen   = DrawHelpers.HashRange(s + 2, 0.06f, 0.22f),
                GrowthDelay = DrawHelpers.HashRange(s + 3, 0.30f, 1.30f),
                BaseAlpha   = DrawHelpers.HashRange(s + 4, 0.55f, 1.00f),
                Seed        = s,
            };
        }
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;
        _screenSize = screenSize;

        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        float dt = ImGui.GetIO().DeltaTime;

        if (time - _lastDrawTime > NewCastGapSeconds) _castStart = time;
        _lastDrawTime = time;
        float age = time - _castStart;

        // Intro shape:
        //   0.00 - 0.55s: sharp red flash (the moment of the wound)
        //   0.00 - 0.55s: base wash and vignette ramp in
        //   0.30 - 1.60s: stains begin growing from the edges
        //   1.20s+:       drips begin falling
        float flash     = IntroFlash(age);
        float baseAlpha = Math.Clamp(age / 0.55f, 0f, 1f);

        // Heartbeat: two quick bumps then a rest, every ~1.4s.
        float heartbeat = Heartbeat(time);

        // 1) flat red wash (+ intro flash)
        float washA = baseAlpha * (0.16f + 0.05f * heartbeat);
        dl.AddRectFilled(DrawHelpers.V(0, 0), screenSize,
            DrawHelpers.WithAlpha(Deep, alpha * washA));

        if (flash > 0f)
            dl.AddRectFilled(DrawHelpers.V(0, 0), screenSize,
                DrawHelpers.WithAlpha(Wet, alpha * flash * 0.35f));

        // 2) vignette
        float vigT = 0.18f + 0.04f * heartbeat;
        float vigA = baseAlpha * (0.65f + 0.20f * heartbeat);
        DrawHelpers.DrawVignette(dl, screenSize, Deep, vigT, alpha * vigA);

        // 3) stains
        DrawStains(dl, screenSize, shortSide, alpha, age);

        // 4) bottom pool
        DrawPool(dl, screenSize, shortSide, alpha, age, heartbeat);

        // 5) falling drips
        if (age > 1.20f)
        {
            _drips.Update(
                time, dt,
                spawnIntervalMin: 0.05f, spawnIntervalMax: 0.20f,
                spawnPos: _dripPos, spawnVelocity: _dripVel,
                pickGlyph: _noGlyph,
                lifespanMin: 1.2f, lifespanMax: 2.2f,
                sizeMin: 3f, sizeMax: 8f);

            DrawDrips(dl, alpha, time);
        }
    }

    // =====================================================================================
    // Intro curves
    // =====================================================================================

    /// <summary>Sharp rise to peak at t=0.15 of a 0.55s window, then a smooth decay to 0.</summary>
    private static float IntroFlash(float age)
    {
        if (age < 0f || age > 0.55f) return 0f;
        float t = age / 0.55f;
        if (t < 0.15f) return t / 0.15f;
        return 1f - (t - 0.15f) / 0.85f;
    }

    private static float Heartbeat(float time)
    {
        const float period = 1.4f;
        float p = (time % period) / period;
        float b1 = Bump(p, 0.10f, 0.06f);
        float b2 = 0.55f * Bump(p, 0.32f, 0.05f);
        return MathF.Min(1f, b1 + b2);
    }

    private static float Bump(float x, float center, float width)
    {
        float d = (x - center) / width;
        return MathF.Exp(-d * d);
    }

    // =====================================================================================
    // Stains
    // =====================================================================================

    private void DrawStains(ImDrawListPtr dl, Vector2 screenSize, float shortSide, float alpha, float age)
    {
        for (int i = 0; i < StainCount; i++)
        {
            ref readonly var s = ref _stains[i];
            float local = age - s.GrowthDelay;
            if (local <= 0f) continue;

            // Grow to full length over 0.8s (ease-out), then hold with a slow wet shimmer.
            float grow = Math.Clamp(local / 0.8f, 0f, 1f);
            float growEased = 1f - (1f - grow) * (1f - grow);
            float len = shortSide * s.TargetLen * growEased;
            if (len < 1f) continue;

            float shimmer = 0.88f + 0.12f * DrawHelpers.Pulse(age, 3.5f + i * 0.3f, i * 0.7f);
            float a = alpha * s.BaseAlpha * shimmer;
            if (a < 0.02f) continue;

            // Edge-anchored start position.
            float x0, y0;
            switch (s.Edge)
            {
                default:
                case 0: x0 = s.Along * screenSize.X; y0 = 0f; break;
                case 1: x0 = 0f;                     y0 = s.Along * screenSize.Y * 0.65f; break;
                case 2: x0 = screenSize.X;           y0 = s.Along * screenSize.Y * 0.65f; break;
            }

            float thick = shortSide * 0.006f + shortSide * 0.004f * DrawHelpers.Hash01(s.Seed + 5);
            float drift = DrawHelpers.HashRange(s.Seed + 6, -0.5f, 0.5f) * thick;

            Vector2 start = new(x0, y0);
            Vector2 tip = s.Edge switch
            {
                0 => new Vector2(x0 + drift, y0 + len),
                1 => new Vector2(x0 + len,   y0 + drift),
                _ => new Vector2(x0 - len,   y0 + drift),
            };

            // Layered streak: dark core with a bright wet edge.
            dl.AddLine(start, tip, DrawHelpers.WithAlpha(Deep,   a * 0.85f), thick * 1.4f);
            dl.AddLine(start, tip, DrawHelpers.WithAlpha(Mid,    a * 0.75f), thick * 0.9f);
            dl.AddLine(start, tip, DrawHelpers.WithAlpha(Bright, a * 0.55f), thick * 0.35f);

            // Beaded droplet at the leading tip.
            dl.AddCircleFilled(tip, thick * 0.70f, DrawHelpers.WithAlpha(Bright, a * 0.85f));
            dl.AddCircleFilled(tip, thick * 0.35f, DrawHelpers.WithAlpha(Wet,    a * 0.90f));
        }
    }

    // =====================================================================================
    // Bottom pool
    // =====================================================================================

    private void DrawPool(ImDrawListPtr dl, Vector2 screenSize, float shortSide,
                          float alpha, float age, float heartbeat)
    {
        float grow = Math.Clamp(age / 1.5f, 0f, 1f);
        if (grow <= 0.01f) return;

        float depth = shortSide * 0.045f * grow * (0.92f + 0.08f * heartbeat);
        float baseA = alpha * 0.55f * grow;

        // Clear at the top of the band, opaque dark red at the bottom.
        dl.AddRectFilledMultiColor(
            new Vector2(0f, screenSize.Y - depth * 2f),
            new Vector2(screenSize.X, screenSize.Y),
            DrawHelpers.WithAlpha(Deep, 0f), DrawHelpers.WithAlpha(Deep, 0f),
            DrawHelpers.WithAlpha(Deep, baseA), DrawHelpers.WithAlpha(Deep, baseA));

        // Bright wet line at the leading edge of the pool.
        dl.AddLine(
            new Vector2(0f, screenSize.Y - depth * 2f),
            new Vector2(screenSize.X, screenSize.Y - depth * 2f),
            DrawHelpers.WithAlpha(Bright, baseA * 0.5f),
            1.5f);
    }

    // =====================================================================================
    // Falling drips
    // =====================================================================================

    private void DrawDrips(ImDrawListPtr dl, float alpha, float time)
    {
        for (int i = 0; i < _drips.Count; i++)
        {
            ref readonly var p = ref _drips[i];
            float age = time - p.Born;
            float fade = EdgeParticleField.FadeFor(age / p.Lifespan);
            float a = alpha * fade;
            if (a < 0.003f) continue;

            float wobX = MathF.Sin(age * 3.4f + p.Born * 2.1f) * 2f;
            Vector2 pos = p.Pos + new Vector2(wobX, 0f);

            // Motion-stretched trail: elongates as the drop accelerates.
            float speedFrac = Math.Clamp(age / p.Lifespan, 0f, 1f);
            float stretch = 1f + 3.5f * speedFrac * speedFrac;
            Vector2 tail = pos - new Vector2(0f, p.Size * stretch);

            dl.AddLine(tail, pos, DrawHelpers.WithAlpha(Deep, a * 0.55f), p.Size * 0.55f);
            dl.AddCircleFilled(pos, p.Size * 0.85f, DrawHelpers.WithAlpha(Mid, a * 0.90f));

            Vector2 hi = pos + new Vector2(-p.Size * 0.20f, -p.Size * 0.15f);
            dl.AddCircleFilled(hi, p.Size * 0.45f, DrawHelpers.WithAlpha(Bright, a * 0.95f));
            dl.AddCircleFilled(hi, p.Size * 0.20f, DrawHelpers.WithAlpha(Wet,    a * 0.95f));
        }
    }

    // =====================================================================================
    // Spawn
    // =====================================================================================

    private Vector2 DripSpawnPos(int seed)
    {
        // 60% top, 20% left-upper, 20% right-upper.
        float roll = DrawHelpers.Hash01(seed);
        if (roll < 0.60f)
            return new Vector2(DrawHelpers.HashRange(seed + 1, 0.02f, 0.98f) * _screenSize.X, -6f);
        if (roll < 0.80f)
            return new Vector2(-6f, DrawHelpers.HashRange(seed + 1, 0.02f, 0.40f) * _screenSize.Y);
        return new Vector2(_screenSize.X + 6f, DrawHelpers.HashRange(seed + 1, 0.02f, 0.40f) * _screenSize.Y);
    }

    private Vector2 DripSpawnVel(int seed) =>
        new(DrawHelpers.HashRange(seed + 2, -6f, 6f),
            DrawHelpers.HashRange(seed + 3, 300f, 650f));
}