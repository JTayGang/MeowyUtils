using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Dropsy: water damage over time. The screen is drenched - a cool teal vignette holds a heavy
/// rain of droplets falling straight down, each stretching into a motion trail as it accelerates,
/// and each landing in a splash ring on the surface of the water that's pooling at the bottom.
///
/// INTRO: a wave splashes up from the bottom edge and falls back, then the steady rain begins and
/// the puddle at the bottom fills in over the first couple of seconds. Droplets and splashes
/// arrive together, so the water feels like it's accumulating rather than appearing all at once.
///
/// STEADY STATE, back to front:
///   1. cool teal vignette with a slow breath
///   2. mist - rectangular-edge-anchored plus interior blobs, sub-circle scatter
///   3. falling droplets - bimodal (big+slow near, small+fast far), each with a stretch trail
///   4. splash rings - short-lived expanding rings where droplets land
///   5. bottom puddle with a gently waving surface and occasional ripple highlights
/// </summary>
public sealed class DropsyEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Dropsy;

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f;

    // ---- palette: cool teal -> bright cyan -> near-white spray ----
    private static readonly uint Deep   = DrawHelpers.ToU32(0.04f, 0.18f, 0.28f, 1f);
    private static readonly uint Mid    = DrawHelpers.ToU32(0.20f, 0.45f, 0.55f, 1f);
    private static readonly uint Bright = DrawHelpers.ToU32(0.45f, 0.72f, 0.85f, 1f);
    private static readonly uint Spray  = DrawHelpers.ToU32(0.88f, 0.96f, 1.00f, 1f);

    // =====================================================================================
    // Mist
    // =====================================================================================
    private const int MistCount      = 18;
    private const int MistSubCircles = 8;

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
    // Intro splash wave
    // =====================================================================================
    private const int IntroSplashCount = 20;

    private struct IntroSplash
    {
        public Vector2 Start;
        public Vector2 Velocity;
        public float Size;
        public float Delay;
        public float Hue;       // 0 leans Deep, 1 leans Spray
        public int   Seed;
    }

    private readonly IntroSplash[] _introSplashes = new IntroSplash[IntroSplashCount];

    // =====================================================================================
    // Falling droplets
    // =====================================================================================
    private readonly EdgeParticleField _drops = new(maxParticles: 44, seedSalt: 0x1D2006);
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _dropPos;
    private readonly Func<int, Vector2> _dropVel;
    private readonly Func<int, string>  _noGlyph;

    // =====================================================================================
    // Splash rings (manual pool)
    // =====================================================================================
    private const int MaxSplashes = 24;

    private struct SplashRing
    {
        public bool    Active;
        public Vector2 Pos;
        public float   Born;
        public float   Lifespan;
        public float   MaxRadius;
        public int     Hue;      // 0 = Bright, 1 = Spray
    }

    private readonly SplashRing[] _splashes = new SplashRing[MaxSplashes];
    private float _nextSplashAt;

    // =====================================================================================
    // Puddle ripples (procedural)
    // =====================================================================================
    // Each ripple is just (age offset) - they're reused forever and their timing is entirely
    // driven by `time + offset` so nothing needs to be stored per ripple.
    private const int PuddleRippleCount = 4;

    // =====================================================================================
    // Intro state
    // =====================================================================================
    private float _lastDrawTime = -100f;
    private float _castStart;

    public DropsyEffect()
    {
        _dropPos  = DropSpawnPos;
        _dropVel  = DropSpawnVel;
        _noGlyph  = static _ => "";

        // ---- bake mist ----
        for (int i = 0; i < MistCount; i++)
        {
            int s = unchecked(0x1D2E00 + i * 7919);

            float bx, by;
            bool edgeAnchored = DrawHelpers.Hash01(s + 12) < 0.65f;
            if (edgeAnchored)
            {
                int edge = (int)(DrawHelpers.Hash01(s) * 4f);
                if (edge > 3) edge = 3;

                float along = DrawHelpers.HashRange(s + 1, 0f, 1f);
                float depth = DrawHelpers.HashRange(s + 2, 0.05f, 0.30f);

                switch (edge)
                {
                    default:
                    case 0: bx = along;         by = depth;         break;
                    case 1: bx = 1f - depth;    by = along;         break;
                    case 2: bx = along;         by = 1f - depth;    break;
                    case 3: bx = depth;         by = along;         break;
                }
            }
            else
            {
                bx = DrawHelpers.HashRange(s + 1, 0.10f, 0.90f);
                by = DrawHelpers.HashRange(s + 2, 0.10f, 0.90f);
            }

            _mist[i] = new MistBlob
            {
                BaseX    = bx,
                BaseY    = by,
                SizeFrac = DrawHelpers.HashRange(s + 3, 0.13f, 0.26f),
                DriftX   = DrawHelpers.HashRange(s + 4, 0.015f, 0.045f),
                DriftY   = DrawHelpers.HashRange(s + 5, 0.012f, 0.040f),
                PhaseX   = DrawHelpers.HashRange(s + 6, 0f, MathF.PI * 2f),
                PhaseY   = DrawHelpers.HashRange(s + 7, 0f, MathF.PI * 2f),
                FreqX    = DrawHelpers.HashRange(s + 8, 0.03f, 0.09f),
                FreqY    = DrawHelpers.HashRange(s + 9, 0.03f, 0.09f),
                Alpha    = DrawHelpers.HashRange(s + 10, 0.05f, 0.13f),
                Lit      = DrawHelpers.Hash01(s + 11) < 0.35f,
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
            BuildIntroSplashes(unchecked((int)(_castStart * 1000f)), screenSize);
            _nextSplashAt = time + 0.35f;
            for (int i = 0; i < MaxSplashes; i++) _splashes[i].Active = false;
        }
        _lastDrawTime = time;
        float age = time - _castStart;

        // Intro timings:
        //   0.00-0.40s: cold blue flash
        //   0.00-0.80s: vignette ramps in
        //   0.10-0.80s: mist fades in
        //   0.00-1.20s: intro splash wave
        //   0.30s+ :     puddle starts filling
        //   0.50s+ :     steady droplets begin
        //   0.50s+ :     splash rings begin
        float introFlash   = age < 0.40f ? 1f - age / 0.40f : 0f;
        float vigFade      = Math.Clamp(age / 0.80f, 0f, 1f);
        float mistFade     = Math.Clamp((age - 0.10f) / 0.70f, 0f, 1f);
        float puddleFade   = Math.Clamp((age - 0.30f) / 1.20f, 0f, 1f);

        // Slow breathing pulse, felt like the surface of water rising and settling.
        float breath = 0.5f + 0.5f * DrawHelpers.Pulse(time, 4.5f);

        // 1) vignette.
        float vigT = 0.14f + 0.03f * breath;
        float vigA = vigFade * (0.62f + 0.15f * breath);
        DrawHelpers.DrawVignette(dl, screenSize, Deep, vigT, alpha * vigA);

        // 1b) intro flash.
        if (introFlash > 0.001f)
        {
            dl.AddRectFilled(DrawHelpers.V(0, 0), screenSize,
                DrawHelpers.WithAlpha(Bright, alpha * introFlash * 0.30f));
        }

        // 2) mist.
        if (mistFade > 0.001f)
            DrawMist(dl, screenSize, shortSide, time, alpha * mistFade);

        // 3) intro splash wave.
        if (age < 1.20f)
            DrawIntroSplashes(dl, alpha, age);

        // 4) falling droplets.
        if (age > 0.50f)
        {
            _drops.Update(
                time, dt,
                spawnIntervalMin: 0.045f, spawnIntervalMax: 0.13f,
                spawnPos: _dropPos, spawnVelocity: _dropVel,
                pickGlyph: _noGlyph,
                lifespanMin: 1.4f, lifespanMax: 2.6f,
                sizeMin: 3f, sizeMax: 9f);

            for (int i = 0; i < _drops.Count; i++)
                DrawDrop(dl, in _drops[i], alpha, time);
        }

        // 5) splash rings.
        if (age > 0.50f)
        {
            // Fire a new ring on its own cadence, positioned randomly along the puddle.
            while (time >= _nextSplashAt)
            {
                SpawnSplashRing(time);
                int hs = unchecked((int)(time * 977f) + 0x5D2B);
                _nextSplashAt += DrawHelpers.HashRange(hs, 0.08f, 0.22f);
            }

            for (int i = 0; i < MaxSplashes; i++)
            {
                ref var s = ref _splashes[i];
                if (!s.Active) continue;
                if (time - s.Born > s.Lifespan) { s.Active = false; continue; }
                DrawSplashRing(dl, in s, alpha, time);
            }
        }

        // 6) bottom puddle.
        if (puddleFade > 0.001f)
            DrawPuddle(dl, screenSize, shortSide, time, alpha * puddleFade, breath);
    }

    // =====================================================================================
    // Mist
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

            uint col = m.Lit ? Bright : Mid;

            for (int k = 0; k < MistSubCircles; k++)
            {
                int ks = unchecked(m.Seed + 100 + k * 71);
                float ox = DrawHelpers.HashRange(ks,     -1f, 1f) * r * 0.95f;
                float oy = DrawHelpers.HashRange(ks + 1, -1f, 1f) * r * 0.95f;
                float sr = r * DrawHelpers.HashRange(ks + 2, 0.28f, 0.58f);

                float wfX = DrawHelpers.HashRange(ks + 3, 0.10f, 0.25f);
                float wfY = DrawHelpers.HashRange(ks + 5, 0.10f, 0.25f);
                float wpX = DrawHelpers.HashRange(ks + 4, 0f, MathF.PI * 2f);
                float wpY = DrawHelpers.HashRange(ks + 6, 0f, MathF.PI * 2f);

                float wobX = MathF.Sin(time * wfX + wpX) * sr * 0.45f;
                float wobY = MathF.Cos(time * wfY + wpY) * sr * 0.45f;

                Vector2 sp = p + new Vector2(ox + wobX, oy + wobY);
                dl.AddCircleFilled(sp, sr, DrawHelpers.WithAlpha(col, alpha * m.Alpha * 0.45f));
            }
        }
    }

    // =====================================================================================
    // Intro splash wave
    // =====================================================================================

    private void BuildIntroSplashes(int castSeed, Vector2 screenSize)
    {
        for (int i = 0; i < IntroSplashCount; i++)
        {
            int s = unchecked(castSeed + 0x5D1A + i * 7919);

            float x = DrawHelpers.HashRange(s, 0.06f, 0.94f) * screenSize.X;
            float y = screenSize.Y + DrawHelpers.HashRange(s + 1, 4f, 20f);

            _introSplashes[i] = new IntroSplash
            {
                Start    = new Vector2(x, y),
                Velocity = new Vector2(
                    DrawHelpers.HashRange(s + 2, -180f, 180f),
                    -DrawHelpers.HashRange(s + 3, 380f, 720f)),
                Size     = DrawHelpers.HashRange(s + 4, 3f, 9f),
                Delay    = DrawHelpers.HashRange(s + 5, 0f, 0.45f),
                Hue      = DrawHelpers.HashRange(s + 6, 0f, 1f),
                Seed     = s,
            };
        }
    }

    private void DrawIntroSplashes(ImDrawListPtr dl, float alpha, float age)
    {
        for (int i = 0; i < IntroSplashCount; i++)
        {
            ref readonly var s = ref _introSplashes[i];
            float local = age - s.Delay;
            if (local <= 0f) continue;

            float life = 1.15f;
            if (local > life) continue;
            float t01 = local / life;

            // Simple decelerating rise and fall.
            float env = t01 < 0.15f
                ? t01 / 0.15f
                : 1f - (t01 - 0.15f) / 0.85f;
            env = Math.Clamp(env, 0f, 1f);

            float decay = 1f - 0.55f * t01 * t01;
            Vector2 pos = s.Start + s.Velocity * (local * decay);

            uint col = DrawHelpers.LerpColor(Bright, Spray, s.Hue);

            // Motion trail behind each splash droplet.
            Vector2 dir = s.Velocity * (local * decay - 0.03f * life);
            Vector2 tail = s.Start + dir;
            Vector2 toTail = tail - pos;
            if (toTail.LengthSquared() > 4f)
            {
                dl.AddLine(tail, pos, DrawHelpers.WithAlpha(Mid, alpha * env * 0.55f), s.Size * 0.6f);
            }

            // Body.
            dl.AddCircleFilled(pos, s.Size, DrawHelpers.WithAlpha(col, alpha * env * 0.95f));
            dl.AddCircleFilled(pos, s.Size * 0.45f, DrawHelpers.WithAlpha(Spray, alpha * env * 0.90f));
        }
    }

    // =====================================================================================
    // Falling droplets
    // =====================================================================================

    private static void DrawDrop(ImDrawListPtr dl, in EdgeParticleField.Particle p, float alpha, float time)
    {
        float age = time - p.Born;
        if (age < 0f || age >= p.Lifespan) return;
        float t01 = age / p.Lifespan;
        float fade = EdgeParticleField.FadeFor(t01);
        if (fade <= 0.005f) return;

        float a = alpha * fade;

        // Motion stretch grows with the square of progress - droplets start as round beads and
        // elongate into streaks as they accelerate.
        float stretch = 1f + 4.5f * t01 * t01;
        float tailLen = p.Size * stretch;

        Vector2 pos = p.Pos;
        Vector2 tail = pos - new Vector2(0f, tailLen);

        // Trail: a soft underline and a brighter core, giving the drop a sense of speed.
        dl.AddLine(tail, pos, DrawHelpers.WithAlpha(Deep,   a * 0.35f), p.Size * 0.65f);
        dl.AddLine(tail, pos, DrawHelpers.WithAlpha(Bright, a * 0.75f), p.Size * 0.32f);

        // Body and specular highlight.
        dl.AddCircleFilled(pos, p.Size, DrawHelpers.WithAlpha(Bright, a * 0.90f));
        dl.AddCircleFilled(pos + new Vector2(-p.Size * 0.22f, -p.Size * 0.22f),
                           p.Size * 0.42f, DrawHelpers.WithAlpha(Spray, a * 0.95f));
    }

    private Vector2 DropSpawnPos(int seed) =>
        new(DrawHelpers.HashRange(seed, -0.02f, 1.02f) * _screenSize.X,
            DrawHelpers.HashRange(seed + 5, -10f, -4f));

    private Vector2 DropSpawnVel(int seed)
    {
        // Bimodal: big+slow (near, blurry) vs small+fast (far, crisp).
        bool big = DrawHelpers.Hash01(seed + 6) < 0.35f;
        float speed = big
            ? DrawHelpers.HashRange(seed + 2, 180f, 320f)
            : DrawHelpers.HashRange(seed + 2, 320f, 560f);
        float vx = DrawHelpers.HashRange(seed + 1, -4f, 4f);
        return new Vector2(vx, speed);
    }

    // =====================================================================================
    // Splash rings
    // =====================================================================================

    private void SpawnSplashRing(float time)
    {
        int free = -1;
        for (int i = 0; i < MaxSplashes; i++)
        {
            if (!_splashes[i].Active) { free = i; break; }
        }
        if (free < 0) return;

        int seed = unchecked((int)(time * 977f) + 0x5D2B);

        // Positioned along the top of the puddle band, biased slightly inward from the edges.
        float x = DrawHelpers.HashRange(seed, 0.04f, 0.96f) * _screenSize.X;
        float y = _screenSize.Y - DrawHelpers.HashRange(seed + 1, 0.030f, 0.075f) * MathF.Min(_screenSize.X, _screenSize.Y);

        _splashes[free] = new SplashRing
        {
            Active    = true,
            Pos       = new Vector2(x, y),
            Born      = time,
            Lifespan  = DrawHelpers.HashRange(seed + 2, 0.55f, 0.95f),
            MaxRadius = DrawHelpers.HashRange(seed + 3, 0.018f, 0.040f) * MathF.Min(_screenSize.X, _screenSize.Y),
            Hue       = DrawHelpers.Hash01(seed + 4) < 0.5f ? 0 : 1,
        };
    }

    private static void DrawSplashRing(ImDrawListPtr dl, in SplashRing s, float alpha, float time)
    {
        float age = time - s.Born;
        if (age < 0f || age >= s.Lifespan) return;
        float t01 = age / s.Lifespan;

        // Quick ring expansion, ease-out.
        float expand = 1f - (1f - t01) * (1f - t01);
        float radius = s.MaxRadius * expand;

        // Fade out over the life.
        float fade = 1f - t01 * t01;
        if (fade <= 0.005f) return;

        // Squashed vertically so the ring reads as lying on the water's surface, not as a
        // floating circle.
        const float squash = 0.34f;
        const int segs = 26;

        uint col = s.Hue == 0 ? Bright : Spray;
        uint shadowCol = Deep;

        Span<Vector2> pts = stackalloc Vector2[segs];
        for (int i = 0; i < segs; i++)
        {
            float ang = MathF.Tau * i / segs;
            pts[i] = s.Pos + new Vector2(MathF.Cos(ang) * radius, MathF.Sin(ang) * radius * squash);
        }

        // Shadow ring underneath + bright ring on top, so the ring reads as a raised edge.
        for (int i = 0; i < segs; i++)
        {
            Vector2 a = pts[i] + new Vector2(0f, 1.2f);
            Vector2 b = pts[(i + 1) % segs] + new Vector2(0f, 1.2f);
            dl.AddLine(a, b, DrawHelpers.WithAlpha(shadowCol, alpha * fade * 0.35f), 2.2f);
        }

        for (int i = 0; i < segs; i++)
        {
            Vector2 a = pts[i];
            Vector2 b = pts[(i + 1) % segs];
            dl.AddLine(a, b, DrawHelpers.WithAlpha(col, alpha * fade * 0.85f), 1.6f);
        }

        // A small bright point at the center in the first moment, like the droplet still sinking.
        if (t01 < 0.35f)
        {
            float k = 1f - t01 / 0.35f;
            dl.AddCircleFilled(s.Pos, 2.4f * k + 0.8f, DrawHelpers.WithAlpha(Spray, alpha * k * 0.9f));
        }
    }

    // =====================================================================================
    // Bottom puddle
    // =====================================================================================

    private void DrawPuddle(ImDrawListPtr dl, Vector2 screenSize, float shortSide,
                            float time, float alpha, float breath)
    {
        // The puddle's top edge waves gently across the screen, and the whole band's height
        // pulses a little with the breath, so the surface reads as a liquid level rather than a
        // static bar.
        float baseHeight = shortSide * 0.085f * (0.92f + 0.14f * breath);
        float topY = screenSize.Y - baseHeight;

        const int samples = 64;
        float waveAmp = baseHeight * 0.10f;

        // Build the puddle's shape: a curve along the top, filled down to the bottom of the screen.
        Span<Vector2> poly = stackalloc Vector2[samples + 2];

        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / (samples - 1);
            float x = t * screenSize.X;

            // Two out-of-phase waves so the surface never sits on a single clean sine.
            float w1 = MathF.Sin(t * 6.5f + time * 0.55f) * 0.6f;
            float w2 = MathF.Sin(t * 14.0f - time * 0.85f + 1.7f) * 0.4f;
            float y = topY + (w1 + w2) * waveAmp;

            poly[i] = new Vector2(x, y);
        }
        poly[samples]     = new Vector2(screenSize.X, screenSize.Y);
        poly[samples + 1] = new Vector2(0f, screenSize.Y);

        // Body of the puddle - a solid mid-teal fill.
        dl.AddConvexPolyFilled(ref poly[0], samples + 2, DrawHelpers.WithAlpha(Mid, alpha * 0.55f));

        // A brighter surface highlight along the top edge - a polyline of the same wave.
        uint surfaceCol = DrawHelpers.WithAlpha(Bright, alpha * 0.55f);
        for (int i = 0; i < samples - 1; i++)
            dl.AddLine(poly[i], poly[i + 1], surfaceCol, 1.6f);

        // Ripples: a few faint expanding ellipse outlines along the puddle that cycle
        // independently, so the surface has motion without needing per-ripple state.
        for (int r = 0; r < PuddleRippleCount; r++)
        {
            int rs = unchecked(0x5D2B00 + r * 7919);

            float period = DrawHelpers.HashRange(rs, 2.2f, 4.0f);
            float phase  = DrawHelpers.HashRange(rs + 1, 0f, 1f);
            float cyc    = ((time / period) + phase) % 1f;

            // Only draw during the first 60% of the cycle - the ripple fades and then waits.
            if (cyc > 0.60f) continue;

            float rT = cyc / 0.60f;
            float expand = 1f - (1f - rT) * (1f - rT);
            float fade = 1f - rT * rT;
            if (fade <= 0.01f) continue;

            float x   = DrawHelpers.HashRange(rs + 2, 0.10f, 0.90f) * screenSize.X;
            float rMax = shortSide * DrawHelpers.HashRange(rs + 3, 0.030f, 0.060f);
            float rx  = rMax * expand;
            float ry  = rx * 0.28f;

            float y = topY + waveAmp * 0.4f;

            // Elliptical ring as a closed polyline.
            const int segs = 20;
            uint col = DrawHelpers.WithAlpha(Bright, alpha * fade * 0.35f);

            for (int i = 0; i < segs; i++)
            {
                float a0 = MathF.Tau * i / segs;
                float a1 = MathF.Tau * (i + 1) / segs;
                Vector2 p0 = new(x + MathF.Cos(a0) * rx, y + MathF.Sin(a0) * ry);
                Vector2 p1 = new(x + MathF.Cos(a1) * rx, y + MathF.Sin(a1) * ry);
                dl.AddLine(p0, p1, col, 1.3f);
            }
        }
    }
}