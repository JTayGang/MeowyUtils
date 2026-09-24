using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Sleep: everything slows and softens. A breathing blue haze creeps in from the edges, dream
/// bubbles rise lazily through the frame, tiny stars twinkle in and out, and drowsy "Z"s drift
/// up from across the lower half of the screen. The whole thing inhales and exhales on one slow
/// cycle, so nothing moves at a constant rate - which is what sells the "drowsy" feel.
///
/// Layers, back to front:
///   1. flat tint wash (subtle blue darkening, breathes slightly)
///   2. breathing vignette (blue, thicker on the inhale)
///   3. dream haze - large translucent blobs drifting on slow Lissajous paths
///   4. twinkling stars - scattered evenly across the frame with occasional cross flares
///   5. rising bubbles - soft translucent orbs that rise with a gentle sideways wobble
///   6. "Z" glyphs - classic sleepy Zs that grow slightly as they drift up
/// </summary>
public sealed class SleepEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Sleep;

    // ---- palette ----
    private static readonly uint DeepBlue  = DrawHelpers.ToU32(0.030f, 0.045f, 0.130f, 1f); // tint / vignette
    private static readonly uint HazeBody  = DrawHelpers.ToU32(0.130f, 0.180f, 0.420f, 1f); // dream haze
    private static readonly uint HazeLit   = DrawHelpers.ToU32(0.300f, 0.380f, 0.720f, 1f); // brighter haze blob
    private static readonly uint Star      = DrawHelpers.ToU32(0.780f, 0.870f, 1.000f, 1f); // stars
    private static readonly uint Bubble    = DrawHelpers.ToU32(0.400f, 0.520f, 0.850f, 1f); // bubble body
    private static readonly uint BubbleLit = DrawHelpers.ToU32(0.720f, 0.830f, 1.000f, 1f); // bubble rim / specular
    private static readonly uint ZGlyph    = DrawHelpers.ToU32(0.840f, 0.900f, 1.000f, 1f); // Z color

    // =====================================================================================
    // Dream haze
    // =====================================================================================
    private const int HazeCount = 20;

    private struct HazeBlob
    {
        public float BaseX, BaseY;    // normalized 0..1 screen position
        public float SizeFrac;        // radius, fraction of shortSide
        public float DriftX, DriftY;  // wander amplitude, fraction of shortSide
        public float PhaseX, PhaseY;
        public float FreqX, FreqY;
        public float Alpha;
        public bool  Lit;             // uses HazeLit instead of HazeBody
    }

    private readonly HazeBlob[] _haze = new HazeBlob[HazeCount];

    // =====================================================================================
    // Stars
    // =====================================================================================
    private const int StarCount = 90;

    private struct StarDot
    {
        public float X, Y;       // normalized
        public float SizeFrac;   // of shortSide
        public float Phase;
        public float Freq;
        public float BaseAlpha;
    }

    private readonly StarDot[] _stars = new StarDot[StarCount];

    // =====================================================================================
    // Particles
    // =====================================================================================
    private readonly EdgeParticleField _bubbles = new(maxParticles: 40, seedSalt: 0x51EEB000);
    private readonly EdgeParticleField _zs      = new(maxParticles: 12, seedSalt: 0x51335133);

    // Cached per-frame; spawn delegates read this directly so we never allocate a closure.
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _bubblePos;
    private readonly Func<int, Vector2> _bubbleVel;
    private readonly Func<int, string>  _noGlyph;

    public SleepEffect()
    {
        _bubblePos   = BubbleSpawnPos;
        _bubbleVel   = BubbleSpawnVelocity;
        _noGlyph     = static _ => "";

        // ---- bake dream haze ----
        for (int i = 0; i < HazeCount; i++)
        {
            int s = unchecked(0x51170000 + i * 7919);
            _haze[i] = new HazeBlob
            {
                BaseX    = DrawHelpers.HashRange(s,      0.00f, 1.00f),
                BaseY    = DrawHelpers.HashRange(s + 1,  0.00f, 1.00f),
                SizeFrac = DrawHelpers.HashRange(s + 2,  0.10f, 0.22f),
                DriftX   = DrawHelpers.HashRange(s + 3,  0.02f, 0.055f),
                DriftY   = DrawHelpers.HashRange(s + 4,  0.015f, 0.045f),
                PhaseX   = DrawHelpers.HashRange(s + 5,  0f, MathF.PI * 2f),
                PhaseY   = DrawHelpers.HashRange(s + 6,  0f, MathF.PI * 2f),
                FreqX    = DrawHelpers.HashRange(s + 7,  0.05f, 0.13f),
                FreqY    = DrawHelpers.HashRange(s + 8,  0.05f, 0.13f),
                Alpha    = DrawHelpers.HashRange(s + 9,  0.04f, 0.10f),
                Lit      = DrawHelpers.Hash01(s + 10) < 0.35f,
            };
        }

        // ---- bake stars (uniformly scattered; the haze already biases the frame toward the edges) ----
        for (int i = 0; i < StarCount; i++)
        {
            int s = unchecked(0x57A70000 + i * 7919);
            _stars[i] = new StarDot
            {
                X         = DrawHelpers.HashRange(s,      0.02f, 0.98f),
                Y         = DrawHelpers.HashRange(s + 1,  0.02f, 0.98f),
                SizeFrac  = DrawHelpers.HashRange(s + 2,  0.0016f, 0.0044f),
                Phase     = DrawHelpers.HashRange(s + 3,  0f, MathF.PI * 2f),
                Freq      = DrawHelpers.HashRange(s + 4,  0.40f, 1.30f),
                BaseAlpha = DrawHelpers.HashRange(s + 5,  0.35f, 1.00f),
            };
        }
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;
        _screenSize = screenSize;

        float shortSide = MathF.Min(screenSize.X, screenSize.Y);

        // One slow breathing rhythm drives the whole effect. The 5.5s cycle is long enough to
        // feel sleepy rather than pulsing.
        float breath = DrawHelpers.Pulse(time, 5.5f);

        // ---- 1) flat blue wash, breathes slightly ----
        dl.AddRectFilled(DrawHelpers.V(0, 0), screenSize,
            DrawHelpers.WithAlpha(DeepBlue, alpha * (0.30f + 0.06f * breath)));

        // ---- 2) vignette, thicker on the inhale ----
        float vigThick = 0.20f + 0.05f * breath;
        DrawHelpers.DrawVignette(dl, screenSize, DeepBlue, vigThick, alpha * (0.68f + 0.16f * breath));

        // ---- 3) dream haze ----
        DrawHaze(dl, screenSize, shortSide, time, alpha);

        // ---- 4) twinkling stars ----
        DrawStars(dl, screenSize, shortSide, time, alpha);

        // ---- 5) rising bubbles ----
        _bubbles.Update(
            time, ImGui.GetIO().DeltaTime,
            spawnIntervalMin: 0.15f, spawnIntervalMax: 0.45f,
            spawnPos: _bubblePos, spawnVelocity: _bubbleVel,
            pickGlyph: _noGlyph,
            lifespanMin: 3.5f, lifespanMax: 6.5f,
            sizeMin: 4f, sizeMax: 12f);
        DrawBubbles(dl, alpha, time);

        // ---- 6) Z glyphs ----
        _zs.Update(
            time, ImGui.GetIO().DeltaTime,
            spawnIntervalMin: 0.55f, spawnIntervalMax: 1.10f,
            spawnPos: ZSpawnPos, spawnVelocity: ZSpawnVelocity,
            pickGlyph: static _ => "Z",
            lifespanMin: 3.2f, lifespanMax: 4.8f,
            sizeMin: 22f, sizeMax: 42f);
        DrawZs(dl, time, alpha);
    }

    // =====================================================================================
    // Haze
    // =====================================================================================

    private void DrawHaze(ImDrawListPtr dl, Vector2 screenSize, float shortSide, float time, float alpha)
    {
        for (int i = 0; i < HazeCount; i++)
        {
            ref readonly var h = ref _haze[i];

            // Each blob wanders on its own slow 2D Lissajous. Independent phases and frequencies
            // mean there's no coherent flow direction - just calm, aimless drift.
            float dx = MathF.Sin(time * h.FreqX + h.PhaseX) * shortSide * h.DriftX;
            float dy = MathF.Cos(time * h.FreqY + h.PhaseY) * shortSide * h.DriftY;

            Vector2 p = new(h.BaseX * screenSize.X + dx, h.BaseY * screenSize.Y + dy);
            float r = shortSide * h.SizeFrac;

            // Two stacked circles at different radii fake a soft-edged blob without a radial
            // gradient (which ImGui doesn't have). The outer is faint, the inner is a touch brighter.
            uint col = h.Lit ? HazeLit : HazeBody;
            dl.AddCircleFilled(p, r,         DrawHelpers.WithAlpha(col, alpha * h.Alpha * 0.55f));
            dl.AddCircleFilled(p, r * 0.55f, DrawHelpers.WithAlpha(col, alpha * h.Alpha * 0.75f));
        }
    }

    // =====================================================================================
    // Stars
    // =====================================================================================

    private void DrawStars(ImDrawListPtr dl, Vector2 screenSize, float shortSide, float time, float alpha)
    {
        for (int i = 0; i < StarCount; i++)
        {
            ref readonly var s = ref _stars[i];

            // Slow twinkle with a sharpened peak (k² makes the "on" moments shorter and brighter).
            float k = 0.5f + 0.5f * MathF.Sin(time * s.Freq + s.Phase);
            float twinkle = k * k;

            float a = alpha * s.BaseAlpha * twinkle;
            if (a < 0.02f) continue;

            Vector2 p = new(s.X * screenSize.X, s.Y * screenSize.Y);
            float r = shortSide * s.SizeFrac;

            // Soft halo + tighter glow + a bright core - the classic star stack.
            dl.AddCircleFilled(p, r * 3.0f, DrawHelpers.WithAlpha(Star, a * 0.15f));
            dl.AddCircleFilled(p, r * 1.4f, DrawHelpers.WithAlpha(Star, a * 0.55f));
            dl.AddCircleFilled(p, r,        DrawHelpers.WithAlpha(Star, a * 0.95f));

            // Cross flare only at the brightest moment of the twinkle, so the stars "sparkle"
            // rather than pulse.
            if (twinkle > 0.75f)
            {
                float flare = r * (twinkle - 0.75f) * 18f;
                uint fa = DrawHelpers.WithAlpha(Star, a * 0.35f);
                dl.AddLine(new Vector2(p.X - flare, p.Y), new Vector2(p.X + flare, p.Y), fa, 1.0f);
                dl.AddLine(new Vector2(p.X, p.Y - flare), new Vector2(p.X, p.Y + flare), fa, 1.0f);
            }
        }
    }

    // =====================================================================================
    // Bubbles
    // =====================================================================================

    private void DrawBubbles(ImDrawListPtr dl, float alpha, float time)
    {
        for (int i = 0; i < _bubbles.Count; i++)
        {
            ref readonly var b = ref _bubbles[i];
            float age = time - b.Born;
            float fade = EdgeParticleField.FadeFor(age / b.Lifespan);

            // Gentle horizontal wobble, decorrelated per particle so the swarm doesn't pulse together.
            float wobX = MathF.Sin(age * 1.7f + b.Born * 2.1f) * 6f;

            Vector2 p = b.Pos + new Vector2(wobX, 0f);
            float r = b.Size;

            // Bubble: soft interior fill, a bright rim, a fainter inner rim, and a specular highlight
            // offset up-left. Reads as a soap bubble rather than a flat dot.
            dl.AddCircleFilled(p, r,         DrawHelpers.WithAlpha(Bubble,    alpha * fade * 0.30f));
            dl.AddCircle(p, r,               DrawHelpers.WithAlpha(BubbleLit, alpha * fade * 0.75f), 0, MathF.Max(1f, r * 0.18f));
            dl.AddCircle(p, r * 0.72f,       DrawHelpers.WithAlpha(BubbleLit, alpha * fade * 0.18f), 0, MathF.Max(0.8f, r * 0.10f));

            Vector2 hi = p + new Vector2(-r * 0.32f, -r * 0.32f);
            dl.AddCircleFilled(hi, MathF.Max(0.8f, r * 0.22f),
                               DrawHelpers.WithAlpha(BubbleLit, alpha * fade * 0.85f));
        }
    }

    private Vector2 BubbleSpawnPos(int seed)
    {
        float x = DrawHelpers.HashRange(seed, 0.03f, 0.97f) * _screenSize.X;
        float y = _screenSize.Y + 8f;
        return new Vector2(x, y);
    }

    private Vector2 BubbleSpawnVelocity(int seed)
    {
        float speed = DrawHelpers.HashRange(seed + 1, 14f, 32f);
        float vx    = DrawHelpers.HashRange(seed + 2, -4f, 4f);
        return new Vector2(vx, -speed);
    }

    // =====================================================================================
    // Z glyphs
    // =====================================================================================

    private Vector2 ZSpawnPos(int seed)
    {
        // Spawn across the whole lower quarter, not just the corners.
        float x = DrawHelpers.HashRange(seed,     0.05f, 0.95f) * _screenSize.X;
        float y = _screenSize.Y * DrawHelpers.HashRange(seed + 1, 0.78f, 1.00f);
        return new Vector2(x, y);
    }

    private Vector2 ZSpawnVelocity(int seed)
    {
        // Drift upward with a little sideways motion. Slower than the bubbles so Zs linger.
        float vy = -DrawHelpers.HashRange(seed + 2, 16f, 30f);
        float vx =  DrawHelpers.HashRange(seed + 3, -6f,  6f);
        return new Vector2(vx, vy);
    }

    private void DrawZs(ImDrawListPtr dl, float time, float alpha)
    {
        for (int i = 0; i < _zs.Count; i++)
        {
            ref readonly var z = ref _zs[i];
            float age   = time - z.Born;
            float lifeU = age / z.Lifespan;
            float fade  = EdgeParticleField.FadeFor(lifeU);

            // Side-to-side sway as it rises. Same seed-based phase for the whole life of the particle.
            float swayX = MathF.Sin(age * 1.3f + z.Born * 1.9f) * 10f;

            Vector2 p = z.Pos + new Vector2(swayX, 0f);
            float size = z.Size * (1f + 0.20f * lifeU); // gently grows as it drifts up

            // A small soft halo behind the glyph so it reads against busy backgrounds.
            dl.AddCircleFilled(p, size * 0.55f, DrawHelpers.WithAlpha(ZGlyph, alpha * fade * 0.18f));

            // Glyph itself. DrawGlowText already does glow + shadow + body, so we just call it.
            DrawHelpers.DrawGlowText(dl, p, z.Glyph,
                                     DrawHelpers.WithAlpha(ZGlyph, alpha * fade * 0.95f),
                                     size, glow: 1.2f);
        }
    }
}