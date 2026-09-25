using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Sleep: everything slows and softens. Opens with a SLOW single blink - the eyelids sag shut
/// from the top and bottom, hold closed for a moment, then drift back open. The eyelids are
/// curved: their inner edges bow further out at the outer corners than in the middle, so the
/// lids MEET AT THE CORNERS FIRST and the gap narrows to a slit that closes last in the center.
///
/// The eyelid edges are SOFT-BLURRED: a gradient band extends from each lid's inner edge into the
/// visible area, fading from solid lid colour to transparent. That fake-blur is what sells the
/// look as real, out-of-focus eyelids rather than a hard-edged shutter.
///
/// INTRO ORDERING: clear vision first, then the eyelid closes over it, and only once the lid is
/// nearly shut does the sleep overlay (haze, stars, bubbles, Zs) fade in behind it. By the time
/// the eyelid lifts, the overlay is at full strength, so the player never sees it appear - they
/// just open their eyes onto the dream.
///
/// Steady state layers, back to front:
///   1. flat tint wash (subtle blue darkening, breathes slightly)
///   2. breathing vignette (blue, thicker on the inhale)
///   3. dream haze - large translucent blobs drifting on slow Lissajous paths
///   4. twinkling stars - scattered evenly across the frame
///   5. rising bubbles - soft translucent orbs rising with a gentle sideways wobble
///   6. "Z" glyphs - drawn BEFORE the eyelids so they're hidden by closed eyes
///   7. curved eyelids with soft blurred edges (only drawn during the intro)
/// </summary>
public sealed class SleepEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Sleep;

    // ---- intro (single slow blink) ----
    private const float NewCastGapSeconds = 1.0f;
    private const float BlinkCloseSec     = 0.90f;
    private const float BlinkHoldSec      = 0.05f;
    private const float BlinkOpenSec      = 1.10f;

    // ---- content fade-in window ----
    // Fraction of the close phase at which the sleep overlay starts fading in. 0.0 = from the
    // very first frame; 0.65 = only once the lid is ~72% shut (smoothstep of 0.65). Higher =
    // the overlay stays invisible longer, and the reveal on eye-open is more of a "cut".
    private const float ContentFadeStartFrac = 0.65f;

    // ---- eyelid shape ----
    private const float EyelidCurveDepthFrac = 0.110f;
    private const float EyelidCloseOvershoot = 8f;
    private const float EyelidOverlapPx      = 2f;

    // ---- eyelid blur (soft gradient band at the inner edge) ----
    // How far the gradient extends from each lid's inner edge into the visible area, as a
    // fraction of screen height. Higher = softer, more out-of-focus edge. Scales with the
    // blink so a barely-open eyelid doesn't leave a big soft haze hanging over the world.
    private const float EyelidFeatherFrac = 0.035f;

    // ---- palette ----
    private static readonly uint DeepBlue   = DrawHelpers.ToU32(0.030f, 0.045f, 0.130f, 1f);
    private static readonly uint HazeBody   = DrawHelpers.ToU32(0.130f, 0.180f, 0.420f, 1f);
    private static readonly uint HazeLit    = DrawHelpers.ToU32(0.300f, 0.380f, 0.720f, 1f);
    private static readonly uint Star       = DrawHelpers.ToU32(0.780f, 0.870f, 1.000f, 1f);
    private static readonly uint Bubble     = DrawHelpers.ToU32(0.400f, 0.520f, 0.850f, 1f);
    private static readonly uint BubbleLit  = DrawHelpers.ToU32(0.720f, 0.830f, 1.000f, 1f);
    private static readonly uint ZGlyph     = DrawHelpers.ToU32(0.840f, 0.900f, 1.000f, 1f);
    private static readonly uint EyelidDark = DrawHelpers.ToU32(0.006f, 0.010f, 0.028f, 1f);

    // =====================================================================================
    // Dream haze
    // =====================================================================================
    private const int HazeCount = 20;

    private struct HazeBlob
    {
        public float BaseX, BaseY;
        public float SizeFrac;
        public float DriftX, DriftY;
        public float PhaseX, PhaseY;
        public float FreqX, FreqY;
        public float Alpha;
        public bool  Lit;
    }

    private readonly HazeBlob[] _haze = new HazeBlob[HazeCount];

    // =====================================================================================
    // Stars
    // =====================================================================================
    private const int StarCount = 90;

    private struct StarDot
    {
        public float X, Y;
        public float SizeFrac;
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

    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _bubblePos;
    private readonly Func<int, Vector2> _bubbleVel;
    private readonly Func<int, string>  _noGlyph;

    // ---- intro timing ----
    private float _lastDrawTime = -100f;
    private float _castStart;

    public SleepEffect()
    {
        _bubblePos = BubbleSpawnPos;
        _bubbleVel = BubbleSpawnVelocity;
        _noGlyph   = static _ => "";

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
        float dt = ImGui.GetIO().DeltaTime;

        // EffectManager stops calling Draw once the effect has fully faded out, so a gap since the
        // last call means the debuff was just (re)applied: restart the intro from age zero.
        if (time - _lastDrawTime > NewCastGapSeconds) _castStart = time;
        _lastDrawTime = time;
        float age = time - _castStart;

        float closedness = ComputeClosedness(age);

        // The sleep overlay's own layers stay completely hidden while the eyelid is opening
        // onto clear vision, and only start fading in once the lid is nearly shut. By the time
        // the lid lifts again, they're at full strength.
        float contentStart = ContentFadeStartFrac * BlinkCloseSec;
        float contentSpan  = BlinkCloseSec - contentStart;
        float contentAlpha = Math.Clamp((age - contentStart) / contentSpan, 0f, 1f);

        // ---- everything the player would see if their eyes were open ----
        float inner = alpha * contentAlpha;

        // 1) flat blue wash
        dl.AddRectFilled(DrawHelpers.V(0, 0), screenSize,
            DrawHelpers.WithAlpha(DeepBlue, inner * 0.30f));

        // 2) vignette
        DrawHelpers.DrawVignette(dl, screenSize, DeepBlue, 0.20f, inner * 0.68f);

        // 3) dream haze
        DrawHaze(dl, screenSize, shortSide, time, inner);

        // 4) twinkling stars
        DrawStars(dl, screenSize, shortSide, time, inner);

        // 5) rising bubbles
        _bubbles.Update(
            time, dt,
            spawnIntervalMin: 0.15f, spawnIntervalMax: 0.45f,
            spawnPos: _bubblePos, spawnVelocity: _bubbleVel,
            pickGlyph: _noGlyph,
            lifespanMin: 3.5f, lifespanMax: 6.5f,
            sizeMin: 4f, sizeMax: 12f);
        DrawBubbles(dl, inner, time);

        // 6) Z glyphs. Drawn BEFORE the eyelids so closed eyes hide them.
        _zs.Update(
            time, dt,
            spawnIntervalMin: 0.55f, spawnIntervalMax: 1.10f,
            spawnPos: ZSpawnPos, spawnVelocity: ZSpawnVelocity,
            pickGlyph: static _ => "Z",
            lifespanMin: 3.2f, lifespanMax: 4.8f,
            sizeMin: 22f, sizeMax: 42f);
        DrawZs(dl, time, inner);

        // 7) Eyelids. Drawn LAST so they cover every layer above.
        DrawEyelids(dl, screenSize, alpha, closedness);
    }

    // =====================================================================================
    // Sleepy blink (intro)
    // =====================================================================================

    /// <summary>
    /// 0 = eyes wide open, 1 = eyes fully closed. A single slow blink:
    ///   - Close: smoothstep from 0 to 1 over BlinkCloseSec.
    ///   - Hold: fully closed for BlinkHoldSec.
    ///   - Open: smoothstep back to 0 over BlinkOpenSec.
    /// After the open phase finishes, this stays at 0 forever (until the debuff is reapplied).
    /// </summary>
    private static float ComputeClosedness(float age)
    {
        if (age < BlinkCloseSec)
        {
            float t = age / BlinkCloseSec;
            return t * t * (3f - 2f * t); // smoothstep
        }

        float holdEnd = BlinkCloseSec + BlinkHoldSec;
        if (age < holdEnd) return 1f;

        float openEnd = holdEnd + BlinkOpenSec;
        if (age < openEnd)
        {
            float t = (age - holdEnd) / BlinkOpenSec;
            float e = t * t * (3f - 2f * t); // smoothstep
            return 1f - e;
        }

        return 0f;
    }

    // =====================================================================================
    // Eyelids
    // =====================================================================================

    /// <summary>
    /// Two curved shapes - one descending from the top, one rising from the bottom - whose inner
    /// edges bow furthest at the OUTER CORNERS and stay closest to the middle at the screen's
    /// horizontal center. As a result the corners touch first and the gap narrows to a vertical
    /// slit that closes last in the middle, like a camera iris / a pair of curtains meeting from
    /// the sides inward.
    ///
    /// The feather (blur) is scaled with the eased closure amount, so a barely-open eyelid doesn't
    /// leave a big soft haze draped over the middle of the screen.
    /// </summary>
    private static void DrawEyelids(ImDrawListPtr dl, Vector2 screenSize, float alpha, float closedness)
    {
        if (closedness <= 0.001f || alpha <= 0.001f) return;

        float eased = closedness * closedness * (3f - 2f * closedness); // smoothstep

        float mid = screenSize.Y * 0.5f;
        float travel = mid * eased + EyelidCloseOvershoot * eased;
        float curveDepth = screenSize.Y * EyelidCurveDepthFrac * eased;
        // Feather depth also scales with eased, so the soft edge only exists as much as the
        // eyelid does.
        float feather = screenSize.Y * EyelidFeatherFrac * eased;

        uint dark = DrawHelpers.WithAlpha(EyelidDark, alpha);

        DrawLidShape(dl, screenSize, travel, curveDepth, feather, isTop: true,  dark);
        DrawLidShape(dl, screenSize, travel, curveDepth, feather, isTop: false, dark);
    }

    /// <summary>
    /// Builds and fills a single curved eyelid. Two passes:
    ///
    ///   1. The solid body, drawn as a strip of convex trapezoids from the outer screen edge to
    ///      the curved inner edge.
    ///   2. A SOFT FEATHER along the inner edge, drawn as a strip of axis-aligned gradient
    ///      rectangles that fade from solid lid colour (at the edge) to transparent (into the
    ///      visible area). That gradient is what reads as "blurred eyelids" - a hard line would
    ///      look like a shutter.
    ///
    /// Coverage details: both passes extend a couple of pixels past the screen bounds and each
    /// segment overlaps the next, so no rasterization edge can leave a hairline gap.
    /// </summary>
    private static void DrawLidShape(
        ImDrawListPtr dl, Vector2 screenSize, float travel, float curveDepth, float feather,
        bool isTop, uint color)
    {
        const int segs = 48;

        float w = screenSize.X;
        float h = screenSize.Y;

        float edgeY  = isTop ? travel : h - travel;
        float outerY = isTop ? -EyelidOverlapPx : h + EyelidOverlapPx;

        float span = w + 2f * EyelidOverlapPx;

        // ---- pass 1: solid body ----
        Span<Vector2> quad = stackalloc Vector2[4];
        for (int i = 0; i < segs; i++)
        {
            float x0 = -EyelidOverlapPx + span * i / segs;
            float x1 = -EyelidOverlapPx + span * (i + 1) / segs;
            float x1ov = x1 + EyelidOverlapPx;

            float y0 = CurveYAt(x0,   w, edgeY, curveDepth, isTop);
            float y1 = CurveYAt(x1ov, w, edgeY, curveDepth, isTop);

            quad[0] = new Vector2(x0,   outerY);
            quad[1] = new Vector2(x1ov, outerY);
            quad[2] = new Vector2(x1ov, y1);
            quad[3] = new Vector2(x0,   y0);

            ref Vector2 first = ref quad[0];
            dl.AddConvexPolyFilled(ref first, 4, color);
        }

        // ---- pass 2: soft feather along the inner edge ----
        if (feather <= 0.5f) return;

        // Same RGB as the solid body, but zero alpha. Used as the "far end" of each gradient so
        // the fade terminates cleanly instead of blending toward black.
        uint solidEdge = color;
        uint clearEdge = DrawHelpers.WithAlpha(color, 0f);

        for (int i = 0; i < segs; i++)
        {
            float x0 = -EyelidOverlapPx + span * i / segs;
            float x1 = -EyelidOverlapPx + span * (i + 1) / segs;

            // Sampling the curve at the segment's two endpoints and using their average as a
            // single flat y is fine here: within one segment the curve barely deviates from a
            // straight line, and using AddRectFilledMultiColor lets us get a real vertical
            // gradient (AddConvexPolyFilled only takes a single colour).
            float y0 = CurveYAt(x0, w, edgeY, curveDepth, isTop);
            float y1 = CurveYAt(x1, w, edgeY, curveDepth, isTop);
            float yMid = (y0 + y1) * 0.5f;

            if (isTop)
            {
                // Top lid: opaque at the eyelid edge, fading DOWN into the visible area. The
                // rectangle starts a few px inside the eyelid body so it overlaps the solid pass
                // (no seam), and extends `feather` px below the edge.
                dl.AddRectFilledMultiColor(
                    new Vector2(x0, yMid - EyelidOverlapPx),
                    new Vector2(x1, yMid + feather),
                    solidEdge, solidEdge,
                    clearEdge, clearEdge);
            }
            else
            {
                // Bottom lid: mirror image. Opaque at the eyelid edge, fading UP.
                dl.AddRectFilledMultiColor(
                    new Vector2(x0, yMid - feather),
                    new Vector2(x1, yMid + EyelidOverlapPx),
                    clearEdge, clearEdge,
                    solidEdge, solidEdge);
            }
        }
    }

    /// <summary>
    /// Y of the lid's inner edge at horizontal position <paramref name="x"/>. The bow peaks at
    /// the outer corners (u=±1) and is zero at the center (u=0), so the corners of a lid reach
    /// furthest and meet first.
    /// </summary>
    private static float CurveYAt(float x, float w, float edgeY, float curveDepth, bool isTop)
    {
        float t = Math.Clamp(x / w, 0f, 1f);
        float u = t * 2f - 1f;
        float bow = u * u;
        return isTop ? edgeY + curveDepth * bow : edgeY - curveDepth * bow;
    }

    // =====================================================================================
    // Haze
    // =====================================================================================

    private void DrawHaze(ImDrawListPtr dl, Vector2 screenSize, float shortSide, float time, float alpha)
    {
        for (int i = 0; i < HazeCount; i++)
        {
            ref readonly var h = ref _haze[i];

            float dx = MathF.Sin(time * h.FreqX + h.PhaseX) * shortSide * h.DriftX;
            float dy = MathF.Cos(time * h.FreqY + h.PhaseY) * shortSide * h.DriftY;

            Vector2 p = new(h.BaseX * screenSize.X + dx, h.BaseY * screenSize.Y + dy);
            float r = shortSide * h.SizeFrac;

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

            float k = 0.5f + 0.5f * MathF.Sin(time * s.Freq + s.Phase);
            float twinkle = k * k;

            float a = alpha * s.BaseAlpha * twinkle;
            if (a < 0.02f) continue;

            Vector2 p = new(s.X * screenSize.X, s.Y * screenSize.Y);
            float r = shortSide * s.SizeFrac;

            dl.AddCircleFilled(p, r * 3.0f, DrawHelpers.WithAlpha(Star, a * 0.15f));
            dl.AddCircleFilled(p, r * 1.4f, DrawHelpers.WithAlpha(Star, a * 0.55f));
            dl.AddCircleFilled(p, r,        DrawHelpers.WithAlpha(Star, a * 0.95f));

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

            float wobX = MathF.Sin(age * 1.7f + b.Born * 2.1f) * 6f;

            Vector2 p = b.Pos + new Vector2(wobX, 0f);
            float r = b.Size;

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
        float x = DrawHelpers.HashRange(seed,     0.05f, 0.95f) * _screenSize.X;
        float y = _screenSize.Y * DrawHelpers.HashRange(seed + 1, 0.78f, 1.00f);
        return new Vector2(x, y);
    }

    private Vector2 ZSpawnVelocity(int seed)
    {
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

            float swayX = MathF.Sin(age * 1.3f + z.Born * 1.9f) * 10f;

            Vector2 p = z.Pos + new Vector2(swayX, 0f);
            float size = z.Size * (1f + 0.20f * lifeU);

            dl.AddCircleFilled(p, size * 0.55f, DrawHelpers.WithAlpha(ZGlyph, alpha * fade * 0.18f));

            DrawHelpers.DrawGlowText(dl, p, z.Glyph,
                                     DrawHelpers.WithAlpha(ZGlyph, alpha * fade * 0.95f),
                                     size, glow: 1.2f);
        }
    }
}