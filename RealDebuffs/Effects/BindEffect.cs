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
/// inward over EaseOutCubic(age - delay). The tip carries a small glowing bulb while extending.
///
/// Each tendril is a discrete-integrated heading path. The look is layered:
///   - the tendril body (halo, dark violet flesh, bright magic core, rim-lit highlight);
///   - a WET SHEEN that travels along the length like light catching a slick surface;
///   - SLIME DRIPS: some hang from the underside, swell, and release a droplet that accelerates
///     under gravity and stretches as it falls; the rest slide slowly toward the tendril's base.
///
/// SEARCHING and GRABBING:
///   - While free, each tendril slow-pulses its length (a "reach") on its own cycle, on top of
///     its sway and curl. That's what reads as the tendril SEARCHING for something to grab.
///   - A latch only happens if the tip literally touches a screen edge (within a few pixels) on
///     a given frame, and only on a stochastic per-frame check so tendrils don't all snap at once.
///   - While latched, the tip is PINNED to the latch point by a weighted correction (0 at the
///     anchor, full at the tip): the tip stays exactly on the edge while the rest of the tendril
///     bends toward it. The tip REGION then ROTATES slowly around the pin, so the tendril visibly
///     curls and pulls at the grip point while still holding it. That rotation is what makes it
///     read as "grabbing" rather than just "touching".
///   - Bend-in and release both ramp over LatchBlendSeconds, so nothing snaps.
///   - There is no grip VFX at the tip - the pinned shape, the curl, and the stilled sway are
///     the whole tell.
/// </summary>
public sealed class BindEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Bind;

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f;
    private const float GrowSeconds       = 0.70f;

    // ---- layout ----
    private const int BottomCount = 10;
    private const int SideCount   = 5;
    private const int TopCount    = 3;
    private const int TotalCount  = BottomCount + SideCount * 2 + TopCount;

    private const int Samples = 24;

    // ---- latch tuning ----
    private const float LatchBlendSeconds  = 5.0f;  // slow, deliberate bend-in and release
    private const float ContactEpsilonFrac = 0.001f; // tip must come within this of an edge to latch (~5px at 1080p)
    private const float LatchInsetFrac     = 0.006f; // latch point is inset this far from the true edge
    private const float LatchedSwayScale   = 0.5f;  // sway damped to this fraction when fully latched
    private const float LatchedCurlBoost   = 0.65f;  // extra curl while latched, so it visibly coils
    private const float LatchRotationCap   = 1.8f;  // max total tip-region rotation, radians (~69 deg)
    private const float RotationStartU     = 0.05f;  // u below this is untouched by the tip rotation

    // ---- palette: near-black void flesh, violet magic core, pale lavender hot glint ----
    private static readonly uint Halo      = DrawHelpers.ToU32(0.010f, 0.002f, 0.022f, 1f);
    private static readonly uint Void      = DrawHelpers.ToU32(0.014f, 0.006f, 0.028f, 1f);
    private static readonly uint Body      = DrawHelpers.ToU32(0.052f, 0.018f, 0.100f, 1f);
    private static readonly uint BodyLit   = DrawHelpers.ToU32(0.145f, 0.055f, 0.235f, 1f);
    private static readonly uint Core      = DrawHelpers.ToU32(0.580f, 0.180f, 0.780f, 1f);
    private static readonly uint CoreHot   = DrawHelpers.ToU32(0.960f, 0.800f, 1.000f, 1f);
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

        // ---- search-stretch: length pulses to sell the "reaching out" motion ----
        public float StretchAmount; // 0..~0.32, max extra length at the peak of the pulse
        public float StretchPeriod; // seconds per pulse cycle
        public float StretchOffset; // 0..1 phase offset

        // ---- latch state ----
        public int     LatchKind;      // -1 = free, 0..3 = screen edge index
        public Vector2 LatchPoint;     // where the tip is holding
        public float   LatchBlend;     // 0 = free path, 1 = fully pinned
        public float   CooldownUntil;  // can't latch again before this time
        public float   HoldUntil;      // time at which it releases
        public float   LatchRotation;  // current accumulated rotation, radians
        public float   LatchRotSpeed;  // rotation rate, radians/sec, signed
    }

    private readonly Tendril[]   _tendrils = new Tendril[TotalCount];
    private readonly Vector2[][] _paths    = new Vector2[TotalCount][];

    private float _lastDrawTime = -100f;
    private float _castStart;

    public BindEffect()
    {
        for (int i = 0; i < TotalCount; i++)
            _paths[i] = new Vector2[Samples];
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
        float dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.25f);

        DrawBottomShade(dl, screenSize, alpha, time, age);

        for (int i = 0; i < TotalCount; i++)
        {
            float revealT = EaseOutCubic(Saturate((age - _tendrils[i].Delay) / GrowSeconds));
            if (revealT <= 0.001f) continue;

            // 1. Build the free path (sway is damped by the current LatchBlend, and length is
            //    pulsed by the search-stretch).
            BuildTendrilPath(i, screenSize, shortSide, time);

            // 2. Update latch state from the free tip. May set LatchKind/LatchPoint and ramp
            //    LatchBlend toward its target, and accumulates tip-region rotation while latched.
            UpdateLatch(i, screenSize, shortSide, time, dt, revealT);

            // 3. If latched (or still easing out), pin the tip to the latch point and rotate the
            //    tip region around it, so the tendril visibly curls at its grip.
            if (_tendrils[i].LatchBlend > 0.001f)
                ApplyLatchPin(i, screenSize, shortSide);

            // 4. Draw.
            DrawTendril(dl, i, screenSize, shortSide, px, alpha, time, revealT);
        }
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
                Edge           = 2,
                Along          = DrawHelpers.HashRange(s,       0.02f, 0.98f),
                Length         = DrawHelpers.HashRange(s + 1,   0.36f, 0.68f),
                Curl           = DrawHelpers.HashRange(s + 2,  -2.0f,  2.0f),
                WaveAmp        = DrawHelpers.HashRange(s + 3,   0.40f, 0.90f),
                WaveFreq       = DrawHelpers.HashRange(s + 4,   1.6f,  3.4f),
                BaseWidth      = DrawHelpers.HashRange(s + 5,   0.010f, 0.019f),
                Phase          = DrawHelpers.HashRange(s + 6,   0f, MathF.PI * 2f),
                Speed          = DrawHelpers.HashRange(s + 7,   0.15f, 0.45f),
                Alpha          = DrawHelpers.HashRange(s + 8,   0.80f, 1.00f),
                Delay          = DrawHelpers.HashRange(s + 11,  0f, 0.40f),
                StretchAmount  = DrawHelpers.HashRange(s + 12,  0.01f, 0.2f),
                StretchPeriod  = DrawHelpers.HashRange(s + 13,  4.5f, 8.5f),
                StretchOffset  = DrawHelpers.Hash01(s + 14),
                LatchRotSpeed  = MakeRotSpeed(s + 15, s + 16),
                Seed           = s,
                LatchKind      = -1,
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
                    Edge           = edge,
                    Along          = DrawHelpers.HashRange(s,       0.15f, 1.00f),
                    Length         = DrawHelpers.HashRange(s + 1,   0.30f, 0.55f),
                    Curl           = DrawHelpers.HashRange(s + 2,  -1.8f,  1.8f),
                    WaveAmp        = DrawHelpers.HashRange(s + 3,   0.35f, 0.80f),
                    WaveFreq       = DrawHelpers.HashRange(s + 4,   1.6f,  3.2f),
                    BaseWidth      = DrawHelpers.HashRange(s + 5,   0.007f, 0.014f),
                    Phase          = DrawHelpers.HashRange(s + 6,   0f, MathF.PI * 2f),
                    Speed          = DrawHelpers.HashRange(s + 7,   0.15f, 0.45f),
                    Alpha          = DrawHelpers.HashRange(s + 8,   0.65f, 0.90f),
                    Delay          = DrawHelpers.HashRange(s + 11,  0f, 0.45f),
                    StretchAmount  = DrawHelpers.HashRange(s + 12,  0.15f, 0.30f),
                    StretchPeriod  = DrawHelpers.HashRange(s + 13,  4.5f, 8.0f),
                    StretchOffset  = DrawHelpers.Hash01(s + 14),
                    LatchRotSpeed  = MakeRotSpeed(s + 15, s + 16),
                    Seed           = s,
                    LatchKind      = -1,
                };
            }
        }

        // ---- top edge ----
        for (int i = 0; i < TopCount; i++)
        {
            int s = unchecked(castSeed + 0x70B00000 + i * 7919);
            _tendrils[idx++] = new Tendril
            {
                Edge           = 0,
                Along          = DrawHelpers.HashRange(s,       0.10f, 0.90f),
                Length         = DrawHelpers.HashRange(s + 1,   0.22f, 0.42f),
                Curl           = DrawHelpers.HashRange(s + 2,  -1.6f,  1.6f),
                WaveAmp        = DrawHelpers.HashRange(s + 3,   0.30f, 0.65f),
                WaveFreq       = DrawHelpers.HashRange(s + 4,   1.4f,  2.8f),
                BaseWidth      = DrawHelpers.HashRange(s + 5,   0.006f, 0.011f),
                Phase          = DrawHelpers.HashRange(s + 6,   0f, MathF.PI * 2f),
                Speed          = DrawHelpers.HashRange(s + 7,   0.20f, 0.65f),
                Alpha          = DrawHelpers.HashRange(s + 8,   0.55f, 0.80f),
                Delay          = DrawHelpers.HashRange(s + 11,  0f, 0.50f),
                StretchAmount  = DrawHelpers.HashRange(s + 12,  0.12f, 0.25f),
                StretchPeriod  = DrawHelpers.HashRange(s + 13,  5.0f, 9.0f),
                StretchOffset  = DrawHelpers.Hash01(s + 14),
                LatchRotSpeed  = MakeRotSpeed(s + 15, s + 16),
                Seed           = s,
                LatchKind      = -1,
            };
        }
    }

    /// <summary>Random but stable tip-region rotation rate: magnitude 0.4..1.1 rad/s, random sign.</summary>
    private static float MakeRotSpeed(int magSeed, int signSeed)
    {
        float mag  = DrawHelpers.HashRange(magSeed, 0.80f, 2.0f);
        float sign = DrawHelpers.Hash01(signSeed) < 0.5f ? -1f : 1f;
        return mag * sign;
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
    // Path building
    // =====================================================================================

    private void BuildTendrilPath(int idx, Vector2 screenSize, float shortSide, float time)
    {
        ref readonly var t = ref _tendrils[idx];
        var path = _paths[idx];

        Vector2 start = EdgeAnchor(screenSize, shortSide, t.Edge, t.Along);
        Vector2 inward = InwardDir(t.Edge);
        float baseAngle = MathF.Atan2(inward.Y, inward.X);

        // Sway damped while latched: a latched tendril goes still except for a faint tremble.
        float swayScale = 1f - (1f - LatchedSwayScale) * t.LatchBlend;

        // Extra curl while latched: as the pin holds the tip, this extra bend makes the body
        // visibly coil around the pin, like a rope being pulled taut.
        float curlBoost = 1f + LatchedCurlBoost * t.LatchBlend;

        // Search-stretch: a slow pulse that briefly lengthens the tendril. Suppressed while
        // latched (a latched tendril isn't reaching, it's holding).
        float stretchPhase = ((time / t.StretchPeriod) + t.StretchOffset) % 1f;
        float stretchPulse = stretchPhase < 0.45f
            ? MathF.Sin((stretchPhase / 0.45f) * MathF.PI)
            : 0f;
        float stretch = 1f + t.StretchAmount * stretchPulse * (1f - t.LatchBlend);

        float totalLen = shortSide * t.Length * stretch;
        float step = totalLen / (Samples - 1);
        float swayTime = time * t.Speed;

        Vector2 cursor = start;
        path[0] = cursor;

        for (int i = 1; i < Samples; i++)
        {
            float u = (float)i / (Samples - 1);
            float heading = baseAngle
                + t.Curl * curlBoost * u
                + t.WaveAmp * swayScale * MathF.Sin(u * t.WaveFreq + t.Phase + swayTime);

            cursor += new Vector2(MathF.Cos(heading), MathF.Sin(heading)) * step;
            path[i] = cursor;
        }
    }

    /// <summary>
    /// Pins the tendril's tip to its latch point, then rotates the tip REGION around that pin.
    ///
    /// Step 1 - pin: adds a u²-weighted positional correction so the base (u=0) stays anchored and
    /// the tip (u=1) lands exactly on the latch point. Blending by LatchBlend makes both the grab
    /// and the release animate smoothly.
    ///
    /// Step 2 - rotate: rotates samples with u &gt; RotationStartU around the latch point by an
    /// angle weighted by ((u-rotStart)/(1-rotStart))². The tip itself has zero displacement from
    /// the pin, so the rotation can't move it - what it does move is the APPROACH DIRECTION of the
    /// last segment, which is what lets the tendril visibly curl and pull at the grip point while
    /// still holding it. The base stays stiff because samples below RotationStartU aren't touched.
    /// </summary>
    private void ApplyLatchPin(int idx, Vector2 screenSize, float shortSide)
    {
        ref readonly var t = ref _tendrils[idx];
        var path = _paths[idx];

        Vector2 freeTip = path[Samples - 1];
        Vector2 correction = t.LatchPoint - freeTip;

        // ---- Step 1: pin the tip ----
        for (int i = 0; i < Samples; i++)
        {
            float u = (float)i / (Samples - 1);
            path[i] += correction * (u * u) * t.LatchBlend;
        }

        // ---- Step 2: rotate the tip region around the pin ----
        if (t.LatchBlend > 0.02f && MathF.Abs(t.LatchRotation) > 0.001f)
        {
            for (int i = 0; i < Samples; i++)
            {
                float u = (float)i / (Samples - 1);
                if (u <= RotationStartU) continue;

                float k = (u - RotationStartU) / (1f - RotationStartU);
                float angle = t.LatchRotation * k * k * t.LatchBlend;

                float ca = MathF.Cos(angle);
                float sa = MathF.Sin(angle);

                Vector2 rel = path[i] - t.LatchPoint;
                path[i] = t.LatchPoint + new Vector2(rel.X * ca - rel.Y * sa, rel.X * sa + rel.Y * ca);
            }
        }
    }

    // =====================================================================================
    // Latch state machine
    // =====================================================================================

    private void UpdateLatch(int idx, Vector2 screenSize, float shortSide, float time, float dt, float revealT)
    {
        ref var t = ref _tendrils[idx];

        // Ramp the blend toward its target (1 while latched, 0 while free).
        float targetBlend = t.LatchKind >= 0 ? 1f : 0f;
        float blendDelta = dt / LatchBlendSeconds;
        if (MathF.Abs(targetBlend - t.LatchBlend) <= blendDelta)
            t.LatchBlend = targetBlend;
        else
            t.LatchBlend += MathF.Sign(targetBlend - t.LatchBlend) * blendDelta;

        // While latched, accumulate the tip-region rotation around the pin. Capped so the tendril
        // can't wind up past a natural "grabbed and pulling" pose. The LatchBlend multiplier in
        // ApplyLatchPin means this rotation fades out automatically on release.
        if (t.LatchKind >= 0)
        {
            t.LatchRotation += t.LatchRotSpeed * dt;
            t.LatchRotation = Math.Clamp(t.LatchRotation, -LatchRotationCap, LatchRotationCap);
        }

        // Release when the hold window elapses.
        if (t.LatchKind >= 0 && time >= t.HoldUntil)
        {
            t.LatchKind = -1;
            t.CooldownUntil = time + 0.8f + 1.2f * DrawHelpers.Hash01(t.Seed + 501);
            return;
        }

        // Only a free tendril can grab.
        if (t.LatchKind >= 0) return;
        if (time < t.CooldownUntil) return;
        if (t.LatchBlend > 0.05f) return;
        if (revealT < 0.98f) return;

        // Contact test: the tip must ACTUALLY be touching an edge. ContactEpsilonFrac is tight
        // (~5px at 1080p), so a tendril can't latch from a distance.
        Vector2 freeTip = _paths[idx][Samples - 1];
        float inset = shortSide * LatchInsetFrac;
        float contact = shortSide * ContactEpsilonFrac;

        int bestEdge = -1;
        Vector2 bestPoint = default;
        float bestDist = contact;

        float dTop = freeTip.Y - inset;
        if (dTop >= 0f && dTop < bestDist) { bestDist = dTop; bestEdge = 0; bestPoint = new Vector2(freeTip.X, inset); }

        float dRight = (screenSize.X - inset) - freeTip.X;
        if (dRight >= 0f && dRight < bestDist) { bestDist = dRight; bestEdge = 1; bestPoint = new Vector2(screenSize.X - inset, freeTip.Y); }

        float dBottom = (screenSize.Y - inset) - freeTip.Y;
        if (dBottom >= 0f && dBottom < bestDist) { bestDist = dBottom; bestEdge = 2; bestPoint = new Vector2(freeTip.X, screenSize.Y - inset); }

        float dLeft = freeTip.X - inset;
        if (dLeft >= 0f && dLeft < bestDist) { bestDist = dLeft; bestEdge = 3; bestPoint = new Vector2(inset, freeTip.Y); }

        if (bestEdge < 0) return;

        // Stochastic check: even in contact, only latch sometimes, so tendrils don't all snap
        // the first frame their tip happens to graze an edge.
        int bucket = (int)(time * 6f);
        bool doLatch = DrawHelpers.Hash01(t.Seed + bucket * 131 + bestEdge * 7919) < 0.45f;
        if (!doLatch) return;

        t.LatchKind = bestEdge;
        t.LatchPoint = bestPoint;
        // Reset the tip rotation so each new grab starts from a clean orientation.
        t.LatchRotation = 0f;
        // Hold time: 1.8 to 3.4 seconds, hashed per tendril so they don't all release together.
        t.HoldUntil = time + 1.8f + 1.6f * DrawHelpers.Hash01(t.Seed + 500);
    }

    // =====================================================================================
    // One tendril
    // =====================================================================================

    private void DrawTendril(ImDrawListPtr dl, int idx, Vector2 screenSize,
                             float shortSide, float px, float alpha, float time, float revealT)
    {
        ref readonly var t = ref _tendrils[idx];
        var path = _paths[idx];

        float baseW = shortSide * t.BaseWidth;
        float a = alpha * t.Alpha;

        // ---- main trunk (with traveling wet sheen) ----
        DrawTaperedPath(dl, path, Samples, baseW, a, px, revealT, time, t.Phase,
                        out Vector2 tipPos, out bool tipVisible);

        // ---- slime drips: hang-and-fall ones, plus flow-to-base ones ----
        DrawSlimeDrips(dl, path, Samples, baseW, a, revealT, time, t.Seed);

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
        float lateral = DrawHelpers.HashRange(ds + 1, -0.6f, 0.6f);
        Vector2 dripBase = onPath + new Vector2(baseW * lateral, baseW * 0.85f);

        float phase = DrawHelpers.HashRange(ds + 2, 0f, 1f);
        float cycle = (time * 0.42f + phase) % 1f;
        if (cycle < 0f) cycle += 1f;

        float swell = MathF.Sin(cycle * MathF.PI);
        float blobR = baseW * (0.42f + 0.32f * swell);

        dl.AddCircleFilled(dripBase, blobR * 2.8f, DrawHelpers.WithAlpha(SlimeCore, aS * 0.09f));
        dl.AddCircleFilled(dripBase, blobR * 0.92f, DrawHelpers.WithAlpha(SlimeBody, aS * 0.88f));
        Vector2 teardrop = dripBase + new Vector2(0f, blobR * 0.75f * swell);
        dl.AddCircleFilled(teardrop, blobR * 0.62f, DrawHelpers.WithAlpha(SlimeBody, aS * 0.80f));
        dl.AddCircleFilled(dripBase, blobR * 0.55f, DrawHelpers.WithAlpha(SlimeCore, aS * 0.60f));
        dl.AddCircleFilled(dripBase, blobR * 0.28f, DrawHelpers.WithAlpha(CoreHot,   aS * 0.90f));

        if (cycle > 0.55f)
        {
            float fallT = (cycle - 0.55f) / 0.45f;
            float fallDist = fallT * fallT * baseW * 14f;
            Vector2 fallPos = dripBase + new Vector2(0f, fallDist);

            float fallA = 1f - fallT * fallT * fallT;
            float fallR = baseW * 0.34f * (1f - 0.35f * fallT);
            float speedFrac = 2f * fallT;
            float stretch = fallR * (1.4f + 4.0f * speedFrac);

            DrawStretchedDroplet(dl, fallPos, new Vector2(0f, 1f), fallR, stretch, aS * fallA);
        }
    }

    private static void DrawFlowingDrip(ImDrawListPtr dl, ReadOnlySpan<Vector2> path, int count,
                                        int startIndex, float baseW, float aS, float time, int ds)
    {
        if (startIndex < 4) startIndex = 4;

        float phase = DrawHelpers.HashRange(ds + 20, 0f, 1f);
        float speed = 0.22f + 0.14f * DrawHelpers.Hash01(ds + 21);
        float t = (time * speed + phase) % 1f;

        float idxFloat = startIndex * (1f - t);
        int i0 = Math.Clamp((int)idxFloat, 0, count - 2);
        float f = Math.Clamp(idxFloat - i0, 0f, 1f);

        Vector2 p = Vector2.Lerp(path[i0], path[i0 + 1], f);
        Vector2 tangent = path[i0 + 1] - path[i0];
        float tlen = tangent.Length();
        if (tlen < 1e-4f) return;
        Vector2 dir = tangent / tlen;

        float alpha = MathF.Sin(t * MathF.PI);
        float dropR  = baseW * 0.30f;
        float stretch = dropR * 2.6f;

        DrawStretchedDroplet(dl, p, dir, dropR, stretch, aS * alpha * 0.85f);
    }

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
    // Tapered path drawing
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