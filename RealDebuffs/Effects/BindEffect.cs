using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Bind: dark tendrils seep in from the edges of the screen, curling inward like something
/// reaching for you. Most of them come from the bottom edge - the effect reads as being dragged
/// down - with fewer along the sides and only a couple creeping down over the top.
///
/// Like the Heavy chains, the effect is cast-in: the whole layout is re-rolled on every fresh
/// application, each tendril has its own stagger delay, and it extends from its edge anchor
/// inward over EaseOutCubic(age - delay). The tip carries a small glowing bulb while extending,
/// and the taper is applied over the VISIBLE portion so the growing tip is always thin.
///
/// Each tendril is a discrete-integrated heading path. The look is layered:
///   - the tendril body (halo, dark violet flesh, bright magic core, rim-lit highlight);
///   - a WET SHEEN that travels along the length like light catching a slick surface;
///   - SLIME DRIPS: some hang from the underside, swell, and release a droplet that accelerates
///     under gravity and stretches as it falls; the rest slide slowly toward the tendril's base,
///     which is what sells the whole thing as wet and draining rather than static.
/// </summary>
public sealed class BindEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Bind;

    // ---- timing: cast-in, matching the Heavy chains' pattern ----
    private const float NewCastGapSeconds = 1.0f;
    private const float GrowSeconds       = 0.70f;

    // ---- layout ----
    private const int BottomCount = 10;
    private const int SideCount   = 5;
    private const int TopCount    = 3;
    private const int TotalCount  = BottomCount + SideCount * 2 + TopCount;

    private const int Samples = 22;

    // ---- palette: near-black void flesh, violet magic core, pale lavender hot glint ----
    private static readonly uint Halo     = DrawHelpers.ToU32(0.010f, 0.002f, 0.022f, 1f);
    private static readonly uint Void     = DrawHelpers.ToU32(0.014f, 0.006f, 0.028f, 1f);
    private static readonly uint Body     = DrawHelpers.ToU32(0.052f, 0.018f, 0.100f, 1f);
    private static readonly uint BodyLit  = DrawHelpers.ToU32(0.145f, 0.055f, 0.235f, 1f);
    private static readonly uint Core     = DrawHelpers.ToU32(0.580f, 0.180f, 0.780f, 1f);
    private static readonly uint CoreHot  = DrawHelpers.ToU32(0.960f, 0.800f, 1.000f, 1f);

    // ---- slime palette ----
    private static readonly uint SlimeBody = DrawHelpers.ToU32(0.190f, 0.055f, 0.340f, 1f);
    private static readonly uint SlimeCore = DrawHelpers.ToU32(0.720f, 0.320f, 0.960f, 1f);

    private static readonly Vector2 LightDir = Vector2.Normalize(new Vector2(-0.7f, -0.7f));

    private struct Tendril
    {
        public byte  Edge;
        public float Along;
        public float Length;
        public float Curl;
        public float WaveAmp;
        public float WaveFreq;
        public float BaseWidth;
        public float Phase;
        public float Speed;
        public float Alpha;
        public float Delay;
        public int   Seed;
        public int   BranchCount;
    }

    private readonly Tendril[] _tendrils = new Tendril[TotalCount];
    private readonly Vector2[] _path     = new Vector2[Samples];

    private float _lastDrawTime = -100f;
    private float _castStart;

    public BindEffect()
    {
        // Tendril layout is re-rolled on every fresh application (see Draw), so nothing to bake here.
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;

        if (time - _lastDrawTime > NewCastGapSeconds)
        {
            _castStart = time;
            BuildTendrils(unchecked((int)(_castStart * 1000f)));
        }
        _lastDrawTime = time;
        float age = time - _castStart;

        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        float px = Math.Clamp(shortSide / 1080f, 0.75f, 2.4f);

        DrawBottomShade(dl, screenSize, alpha, time, age);

        for (int i = 0; i < TotalCount; i++)
            DrawTendril(dl, in _tendrils[i], screenSize, shortSide, px, alpha, time, age);
    }

    // =====================================================================================
    // Layout baking
    // =====================================================================================

    private void BuildTendrils(int castSeed)
    {
        int idx = 0;

        // ---- bottom edge ----
        for (int i = 0; i < BottomCount; i++)
        {
            int s = unchecked(castSeed + 0x71D10000 + i * 7919);
            _tendrils[idx++] = new Tendril
            {
                Edge        = 2,
                Along       = DrawHelpers.HashRange(s,      0.02f, 0.98f),
                Length      = DrawHelpers.HashRange(s + 1,  0.30f, 0.52f),
                Curl        = DrawHelpers.HashRange(s + 2, -1.7f,  1.7f),
                WaveAmp     = DrawHelpers.HashRange(s + 3,  0.35f, 0.80f),
                WaveFreq    = DrawHelpers.HashRange(s + 4,  1.6f,  3.4f),
                BaseWidth   = DrawHelpers.HashRange(s + 5,  0.010f, 0.019f),
                Phase       = DrawHelpers.HashRange(s + 6,  0f, MathF.PI * 2f),
                Speed       = DrawHelpers.HashRange(s + 7,  0.35f, 0.75f),
                Alpha       = DrawHelpers.HashRange(s + 8,  0.80f, 1.00f),
                Delay       = DrawHelpers.HashRange(s + 11, 0f, 0.40f),
                Seed        = s,
                BranchCount = 2 + (int)(DrawHelpers.Hash01(s + 9) * 3f), // 2..4
            };
        }

        // ---- side edges ----
        for (int side = 0; side < 2; side++)
        {
            byte edge = (byte)(side == 0 ? 3 : 1);
            for (int i = 0; i < SideCount; i++)
            {
                int s = unchecked(castSeed + 0x51DE0000 + side * 100000 + i * 7919);
                _tendrils[idx++] = new Tendril
                {
                    Edge        = edge,
                    Along       = DrawHelpers.HashRange(s,      0.15f, 1.00f),
                    Length      = DrawHelpers.HashRange(s + 1,  0.18f, 0.34f),
                    Curl        = DrawHelpers.HashRange(s + 2, -1.4f,  1.4f),
                    WaveAmp     = DrawHelpers.HashRange(s + 3,  0.30f, 0.65f),
                    WaveFreq    = DrawHelpers.HashRange(s + 4,  1.6f,  3.2f),
                    BaseWidth   = DrawHelpers.HashRange(s + 5,  0.007f, 0.014f),
                    Phase       = DrawHelpers.HashRange(s + 6,  0f, MathF.PI * 2f),
                    Speed       = DrawHelpers.HashRange(s + 7,  0.35f, 0.75f),
                    Alpha       = DrawHelpers.HashRange(s + 8,  0.65f, 0.90f),
                    Delay       = DrawHelpers.HashRange(s + 11, 0f, 0.45f),
                    Seed        = s,
                    BranchCount = 1 + (int)(DrawHelpers.Hash01(s + 9) * 3f), // 1..3
                };
            }
        }

        // ---- top edge ----
        for (int i = 0; i < TopCount; i++)
        {
            int s = unchecked(castSeed + 0x70B00000 + i * 7919);
            _tendrils[idx++] = new Tendril
            {
                Edge        = 0,
                Along       = DrawHelpers.HashRange(s,      0.10f, 0.90f),
                Length      = DrawHelpers.HashRange(s + 1,  0.14f, 0.26f),
                Curl        = DrawHelpers.HashRange(s + 2, -1.2f,  1.2f),
                WaveAmp     = DrawHelpers.HashRange(s + 3,  0.25f, 0.55f),
                WaveFreq    = DrawHelpers.HashRange(s + 4,  1.4f,  2.8f),
                BaseWidth   = DrawHelpers.HashRange(s + 5,  0.006f, 0.011f),
                Phase       = DrawHelpers.HashRange(s + 6,  0f, MathF.PI * 2f),
                Speed       = DrawHelpers.HashRange(s + 7,  0.30f, 0.65f),
                Alpha       = DrawHelpers.HashRange(s + 8,  0.55f, 0.80f),
                Delay       = DrawHelpers.HashRange(s + 11, 0f, 0.50f),
                Seed        = s,
                BranchCount = 1 + (int)(DrawHelpers.Hash01(s + 9) * 2f), // 1..2
            };
        }
    }

    // =====================================================================================
    // Bottom shade
    // =====================================================================================

    private void DrawBottomShade(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time, float age)
    {
        float castIn = Saturate(age / 0.6f);
        float pulse  = DrawHelpers.Pulse(time, 3.2f);
        float depth  = screenSize.Y * (0.10f + 0.03f * pulse) * castIn;
        if (depth <= 1f) return;

        const uint baseCol = 0xFF000000u;
        uint dark  = DrawHelpers.WithAlpha(baseCol, 0.55f * alpha);
        uint clear = DrawHelpers.WithAlpha(baseCol, 0f);

        dl.AddRectFilledMultiColor(
            new Vector2(0f, screenSize.Y - depth),
            screenSize,
            clear, clear, dark, dark);
    }

    // =====================================================================================
    // One tendril
    // =====================================================================================

    private void DrawTendril(ImDrawListPtr dl, in Tendril t, Vector2 screenSize,
                             float shortSide, float px, float alpha, float time, float age)
    {
        float revealT = EaseOutCubic(Saturate((age - t.Delay) / GrowSeconds));
        if (revealT <= 0.001f) return;

        int samples = BuildPath(in t, screenSize, shortSide, time);
        if (samples < 2) return;

        float baseW = shortSide * t.BaseWidth;
        float a = alpha * t.Alpha;

        // ---- main trunk (with traveling wet sheen) ----
        DrawTaperedPath(dl, _path, samples, baseW, a, px, revealT, time, t.Phase,
                        out Vector2 tipPos, out bool tipVisible);

        // ---- branches ----
        if (t.BranchCount > 0)
        {
            Span<Vector2> branch = stackalloc Vector2[Samples];

            for (int b = 0; b < t.BranchCount; b++)
            {
                int bs = unchecked(t.Seed + 400 + b * 131);
                float attachT = DrawHelpers.HashRange(bs, 0.20f, 0.85f);

                float branchReveal = Saturate((revealT - attachT) / 0.35f);
                if (branchReveal <= 0.001f) continue;

                int at = (int)(attachT * (samples - 1));

                Vector2 parentHeading = _path[Math.Min(at + 1, samples - 1)] - _path[Math.Max(at - 1, 0)];
                float parentAngle = MathF.Atan2(parentHeading.Y, parentHeading.X);

                float sideSign = DrawHelpers.Hash01(bs + 1) < 0.5f ? 1f : -1f;
                float branchAngle = parentAngle + sideSign * DrawHelpers.HashRange(bs + 2, 0.55f, 1.30f);

                float branchLen = shortSide * t.Length * DrawHelpers.HashRange(bs + 3, 0.18f, 0.40f);
                float branchW   = baseW * DrawHelpers.HashRange(bs + 4, 0.45f, 0.75f);

                Vector2 cursor = _path[at];
                float step = branchLen / (Samples - 1);
                float bPhase = bs * 0.31f;
                float bSpeed = 0.5f + 0.4f * DrawHelpers.Hash01(bs + 5);

                branch[0] = cursor;
                for (int i = 1; i < Samples; i++)
                {
                    float u = (float)i / (Samples - 1);
                    float wobble = MathF.Sin(u * 2.4f + bPhase + time * bSpeed) * 0.40f * u;
                    float ang = branchAngle + wobble;
                    cursor += new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * step;
                    branch[i] = cursor;
                }

                DrawTaperedPath(dl, branch, Samples, branchW, a * 0.82f, px, branchReveal,
                                time, bPhase, out _, out _);
            }
        }

        // ---- slime drips: hang-and-fall ones, plus flow-to-base ones ----
        DrawSlimeDrips(dl, _path, samples, baseW, a, revealT, time, t.Seed);

        // ---- growing tip bulb ----
        if (tipVisible && revealT < 0.999f)
        {
            float r = baseW * 1.4f;
            dl.AddCircleFilled(tipPos, r * 3.0f, DrawHelpers.WithAlpha(Core,    a * 0.30f));
            dl.AddCircleFilled(tipPos, r * 1.6f, DrawHelpers.WithAlpha(Core,    a * 0.70f));
            dl.AddCircleFilled(tipPos, r * 0.70f, DrawHelpers.WithAlpha(CoreHot, a * 0.95f));
        }
    }

    // =====================================================================================
    // Slime drips
    // =====================================================================================

    /// <summary>
    /// Every drip slot is one of two kinds, chosen per-slot from the seed:
    ///   - a HANGING drip: clings to the underside, swells over a slow cycle, then releases a
    ///     droplet that accelerates downward under gravity and stretches with its speed;
    ///   - a FLOWING drip: slides slowly along the tendril path toward the base, elongated along
    ///     its direction of travel - the "draining" half of the effect.
    /// Drawn after the trunk and branches so they read as sitting on the surface.
    /// </summary>
    private static void DrawSlimeDrips(ImDrawListPtr dl, ReadOnlySpan<Vector2> path, int count,
                                       float baseW, float a, float revealT, float time, int seed)
    {
        if (count < 2 || a <= 0.002f) return;

        int dripCount = 3 + (int)(DrawHelpers.Hash01(seed + 900) * 3f); // 3..5 per tendril
        float aS = a * 0.85f;

        for (int d = 0; d < dripCount; d++)
        {
            int ds = unchecked(seed + 900 + d * 131);
            float attachT = DrawHelpers.HashRange(ds, 0.15f, 0.90f);
            if (attachT > revealT) break;

            // Roughly 60% hang and fall, 40% flow toward the base.
            bool flowing = DrawHelpers.Hash01(ds + 50) < 0.40f;

            int at = (int)(attachT * (count - 1));

            if (flowing)
                DrawFlowingDrip(dl, path, count, at, baseW, aS, time, ds);
            else
                DrawHangingDrip(dl, path[at], baseW, aS, time, ds);
        }
    }

    private static void DrawHangingDrip(ImDrawListPtr dl, Vector2 onPath, float baseW, float aS,
                                        float time, int ds)
    {
        // Hang from the down side of the tendril with a bit of lateral wander so the drips don't
        // all sit dead-center on the trunk.
        float lateral = DrawHelpers.HashRange(ds + 1, -0.6f, 0.6f);
        Vector2 dripBase = onPath + new Vector2(baseW * lateral, baseW * 0.85f);

        float phase = DrawHelpers.HashRange(ds + 2, 0f, 1f);
        float cycle = (time * 0.42f + phase) % 1f;
        if (cycle < 0f) cycle += 1f;

        float swell = MathF.Sin(cycle * MathF.PI);
        float blobR = baseW * (0.42f + 0.32f * swell);

        // Cling blob: soft wet glow, viscous body, bright interior, hot pinpoint.
        dl.AddCircleFilled(dripBase, blobR * 2.8f, DrawHelpers.WithAlpha(SlimeCore, aS * 0.09f));
        dl.AddCircleFilled(dripBase, blobR * 0.92f, DrawHelpers.WithAlpha(SlimeBody, aS * 0.88f));
        Vector2 teardrop = dripBase + new Vector2(0f, blobR * 0.75f * swell);
        dl.AddCircleFilled(teardrop, blobR * 0.62f, DrawHelpers.WithAlpha(SlimeBody, aS * 0.80f));
        dl.AddCircleFilled(dripBase, blobR * 0.55f, DrawHelpers.WithAlpha(SlimeCore, aS * 0.60f));
        dl.AddCircleFilled(dripBase, blobR * 0.28f, DrawHelpers.WithAlpha(CoreHot,   aS * 0.90f));

        // Falling droplet: quadratic fall distance = real acceleration, and a stretch that grows
        // with instantaneous speed so the drop visibly elongates as it accelerates.
        if (cycle > 0.55f)
        {
            float fallT = (cycle - 0.55f) / 0.45f; // 0..1 over the release window
            float fallDist = fallT * fallT * baseW * 14f; // d = ½ g t², tuned so it exits quickly
            Vector2 fallPos = dripBase + new Vector2(0f, fallDist);

            float fallA = 1f - fallT * fallT * fallT; // hold brightness, then snap out at the end
            float fallR = baseW * 0.34f * (1f - 0.35f * fallT);
            float speedFrac = 2f * fallT; // derivative of t² -> how fast it is right now
            float stretch = fallR * (1.4f + 4.0f * speedFrac);

            DrawStretchedDroplet(dl, fallPos, new Vector2(0f, 1f), fallR, stretch, aS * fallA);
        }
    }

    private static void DrawFlowingDrip(ImDrawListPtr dl, ReadOnlySpan<Vector2> path, int count,
                                        int startIndex, float baseW, float aS, float time, int ds)
    {
        // Clamp so there is always at least a little bit of path to slide along.
        if (startIndex < 4) startIndex = 4;

        // Slow, constant slide toward index 0 (the tendril base). Different drips have different
        // speeds and phases so they don't all move in lockstep.
        float phase = DrawHelpers.HashRange(ds + 20, 0f, 1f);
        float speed = 0.22f + 0.14f * DrawHelpers.Hash01(ds + 21); // fraction of the flow span per second
        float t = (time * speed + phase) % 1f;

        // Slide along the path, index decreasing from startIndex to 0.
        float idxFloat = startIndex * (1f - t);
        int i0 = Math.Clamp((int)idxFloat, 0, count - 2);
        float f = Math.Clamp(idxFloat - i0, 0f, 1f);

        Vector2 p = Vector2.Lerp(path[i0], path[i0 + 1], f);
        Vector2 tangent = path[i0 + 1] - path[i0];
        float tlen = tangent.Length();
        if (tlen < 1e-4f) return;
        Vector2 dir = tangent / tlen;

        // Fade in at the start of the slide, fade out at the end so it never pops.
        float alpha = MathF.Sin(t * MathF.PI);
        float dropR  = baseW * 0.30f;
        float stretch = dropR * 2.6f; // elongated along the direction of travel -> "moving slowly, stretching"

        DrawStretchedDroplet(dl, p, dir, dropR, stretch, aS * alpha * 0.85f);
    }

    /// <summary>
    /// Capsule-shaped droplet: a thick body line with rounded caps, stretched along <paramref name="dir"/>.
    /// Used for falling (stretched vertically as it accelerates) and flowing (stretched along the
    /// tendril) drips alike.
    /// </summary>
    private static void DrawStretchedDroplet(ImDrawListPtr dl, Vector2 center, Vector2 dir,
                                             float radius, float totalLength, float alpha)
    {
        if (alpha <= 0.01f || radius <= 0.2f) return;

        float half = MathF.Max(0f, totalLength * 0.5f - radius);
        Vector2 a = center - dir * half;
        Vector2 b = center + dir * half;

        uint glow = DrawHelpers.WithAlpha(SlimeCore, alpha * 0.15f);
        uint body = DrawHelpers.WithAlpha(SlimeCore, alpha * 0.70f);
        uint hot  = DrawHelpers.WithAlpha(CoreHot,   alpha * 0.90f);

        dl.AddLine(a, b, glow, radius * 3.0f);
        dl.AddLine(a, b, body, radius * 2.0f);
        dl.AddLine(a, b, hot,  radius * 0.9f);
        dl.AddCircleFilled(a, radius,        body);
        dl.AddCircleFilled(b, radius,        body);
        dl.AddCircleFilled(a, radius * 0.55f, hot);
        dl.AddCircleFilled(b, radius * 0.55f, hot);
    }

    // =====================================================================================
    // Path integration
    // =====================================================================================

    private int BuildPath(in Tendril t, Vector2 screenSize, float shortSide, float time)
    {
        Vector2 start = EdgeAnchor(screenSize, shortSide, t.Edge, t.Along);
        Vector2 inward = InwardDir(t.Edge);
        float baseAngle = MathF.Atan2(inward.Y, inward.X);

        float totalLen = shortSide * t.Length;
        float step = totalLen / (Samples - 1);
        float swayTime = time * t.Speed;

        Vector2 cursor = start;
        _path[0] = cursor;

        for (int i = 1; i < Samples; i++)
        {
            float u = (float)i / (Samples - 1);
            float heading = baseAngle
                + t.Curl * u
                + t.WaveAmp * MathF.Sin(u * t.WaveFreq + t.Phase + swayTime);

            cursor += new Vector2(MathF.Cos(heading), MathF.Sin(heading)) * step;
            _path[i] = cursor;
        }

        return Samples;
    }

    // =====================================================================================
    // Tapered path drawing (with partial reveal + traveling wet sheen)
    // =====================================================================================

    private static void DrawTaperedPath(ImDrawListPtr dl, ReadOnlySpan<Vector2> path, int count,
                                        float baseW, float a, float px, float revealT,
                                        float time, float phase,
                                        out Vector2 tipPos, out bool tipVisible)
    {
        tipPos = default;
        tipVisible = false;
        if (count < 2 || a <= 0.002f || revealT <= 0.001f) return;

        float maxSeg = (count - 1) * revealT;
        int fullSegs = (int)maxSeg;
        float partialFrac = maxSeg - fullSegs;
        if (fullSegs >= count - 1)
        {
            fullSegs = count - 2;
            partialFrac = 1f;
        }

        int segCount = fullSegs + (partialFrac > 0.001f ? 1 : 0);
        if (segCount <= 0) return;

        float sheenRaw = time * 0.35f + phase;
        float sheenPos = sheenRaw - MathF.Floor(sheenRaw);

        for (int i = 0; i < segCount; i++)
        {
            Vector2 p0 = path[i];
            Vector2 p1 = (i < fullSegs) ? path[i + 1] : Vector2.Lerp(path[i], path[i + 1], partialFrac);

            float u0 = (float)i / segCount;
            float u1 = (float)(i + 1) / segCount;

            float w0 = TaperWidth(u0, baseW);
            float w1 = TaperWidth(u1, baseW);
            float w  = 0.5f * (w0 + w1);

            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(Halo, a * 0.45f), w * 3.4f + 2f * px);

            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(Void, a),         w * 1.00f);
            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(Body, a * 0.92f), w * 0.82f);

            Vector2 dir = p1 - p0;
            float len = dir.Length();
            if (len > 1e-4f)
            {
                Vector2 perp = new(-dir.Y / len, dir.X / len);

                float lightDot = Vector2.Dot(perp, LightDir);
                if (lightDot > 0f)
                {
                    Vector2 off = perp * (w * 0.34f * lightDot);
                    dl.AddLine(p0 + off, p1 + off,
                               DrawHelpers.WithAlpha(BodyLit, a * 0.60f * lightDot),
                               w * 0.30f);
                }

                float midU = (u0 + u1) * 0.5f;
                float sDelta = midU - sheenPos;
                sDelta -= MathF.Round(sDelta);
                float sheenK = MathF.Exp(-(sDelta * sDelta) / 0.010f);
                if (sheenK > 0.02f)
                {
                    float sideK = lightDot > 0f ? 0.55f + 0.45f * lightDot : 0.45f;
                    Vector2 sheenOff = perp * (w * 0.18f * (lightDot > 0f ? lightDot : 0f));
                    dl.AddLine(p0 + sheenOff, p1 + sheenOff,
                               DrawHelpers.WithAlpha(CoreHot, a * 0.50f * sheenK * sideK),
                               w * 0.42f);
                }
            }

            float coreW = w * 0.22f;
            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(Core,    a * 0.80f), coreW * 2.4f);
            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(Core,    a * 0.95f), coreW * 1.1f);
            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(CoreHot, a * 0.70f), MathF.Max(0.6f, coreW * 0.55f));

            if (i == segCount - 1)
            {
                tipPos = p1;
                tipVisible = true;
            }
        }
    }

    private static float TaperWidth(float u, float baseW)
    {
        float f = MathF.Pow(1f - u, 0.65f);
        return baseW * (0.22f + 0.78f * f);
    }

    // =====================================================================================
    // Edge geometry + helpers
    // =====================================================================================

    private static Vector2 EdgeAnchor(Vector2 size, float shortSide, byte edge, float along)
    {
        float overhang = shortSide * 0.02f;
        return edge switch
        {
            0 => new Vector2(along * size.X,  -overhang),
            1 => new Vector2(size.X + overhang, along * size.Y),
            2 => new Vector2(along * size.X,  size.Y + overhang),
            _ => new Vector2(-overhang,        along * size.Y),
        };
    }

    private static Vector2 InwardDir(byte edge) => edge switch
    {
        0 => new Vector2(0f,  1f),
        1 => new Vector2(-1f, 0f),
        2 => new Vector2(0f, -1f),
        _ => new Vector2(1f,  0f),
    };

    private static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    private static float EaseOutCubic(float t)
    {
        float u = 1f - Saturate(t);
        return 1f - u * u * u;
    }
}