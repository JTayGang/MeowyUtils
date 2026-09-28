using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Disease: parasitic tendrils creep in from the edges and curl inward, searching for something
/// to grip. Most come from the bottom edge; a few from the sides and the top. As each reaches
/// the end of its reach, it may LATCH onto whatever it touched - a screen edge in this
/// implementation - and hang there, pulling taut and slowly rotating at its grip while the rest
/// of the tendril coils.
///
/// SHAPE PIPELINE — how a tentacle's curve gets built each frame:
///   1. Integrate a low-resolution heading at ControlPoints samples: base angle + curl + sway.
///      This produces a coarse skeleton.
///   2. Resample that skeleton through a Catmull-Rom spline at FinalSamples samples. The spline
///      smooths the piecewise-constant heading into a real curve, which is what eliminates the
///      "blocky when bent" look the polyline version had.
///   3. Apply the latch pin correction to the SMOOTH samples, so a latched tendril's bend stays
///      smooth through the pinning.
///   4. Rebuild the arc-length table (BuildArc) so the material samples evenly along the smooth
///      path.
///
/// SEARCHING / GRABBING:  each tendril slow-pulses its length (a "reach") on its own cycle. When
/// the free tip literally touches a screen edge (within a few pixels), a stochastic check may
/// latch it. Latching pins the tip to that point and rotates the tip region slowly around it.
/// All state transitions (bend-in, release) ramp over LatchBlendSeconds so nothing snaps.
///
/// VISUALS: the tendrils use whatever stroke material is configured for Disease. Default is
/// stroke.parasite (segmented flesh, suckers, hooked tip, ooze drips). All visual detail lives in
/// that material; this class owns only shape, timing, and the latch state machine.
/// </summary>
public sealed class DiseaseEffect : ISceneEffect
{
    public DebuffKind Kind => DebuffKind.Disease;

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f;
    private const float GrowSeconds       = 0.70f;

    // ---- layout ----
    private const int BottomCount = 10;
    private const int SideCount   = 5;
    private const int TopCount    = 3;
    private const int TotalCount  = BottomCount + SideCount * 2 + TopCount;

    // ---- curve sampling ----
    // Control points produce the SHAPE (curl, sway, reach); final samples are the SMOOTH output
    // that the material consumes. Keeping control points low means the Catmull-Rom pass has real
    // work to do; a higher control count would just be a polyline in disguise.
    private const int ControlPoints = 10;
    private const int FinalSamples  = 40;

    // ---- latch tuning ----
    private const float LatchBlendSeconds  = 5.0f;
    private const float ContactEpsilonFrac = 0.001f;
    private const float LatchInsetFrac     = 0.006f;
    private const float LatchedSwayScale   = 0.5f;
    private const float LatchedCurlBoost   = 0.65f;
    private const float LatchRotationCap   = 1.8f;
    private const float RotationStartU     = 0.05f;

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

        public float StretchAmount;
        public float StretchPeriod;
        public float StretchOffset;

        public int     LatchKind;
        public Vector2 LatchPoint;
        public float   LatchBlend;
        public float   CooldownUntil;
        public float   HoldUntil;
        public float   LatchRotation;
        public float   LatchRotSpeed;
    }

    private readonly Tendril[]    _tendrils = new Tendril[TotalCount];
    private readonly StrandPath[] _paths    = new StrandPath[TotalCount];
    private readonly Vector2[]    _coarse   = new Vector2[ControlPoints];

    private float _lastDrawTime = -100f;
    private float _castStart;

    public DiseaseEffect()
    {
        for (int i = 0; i < TotalCount; i++)
            _paths[i] = new StrandPath(FinalSamples);
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, Vector4? colorOverride)
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
        float dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.25f);

        // Ground shadow: same bottom-edge darkening shape Heavy uses, but sickly green rather
        // than sooty black. It's Disease's ambience, not part of the tentacle material.
        float castIn = Saturate(age / 0.6f);
        float pulse = DrawHelpers.Pulse(time, 3.2f);
        float depth = screenSize.Y * (0.10f + 0.03f * pulse) * castIn;
        if (depth > 1f)
        {
            scene.AddRegion(new RegionPrimitive
            {
                Min = new Vector2(0f, screenSize.Y - depth),
                Max = screenSize,
                Tint = DrawHelpers.ToU32(0.02f, 0.05f, 0.01f, 1f),
                Alpha = 0.55f * alpha,
                Bottom = true,
                ColorOverride = colorOverride,
            });
        }

        for (int i = 0; i < TotalCount; i++)
        {
            float revealT = EaseOutCubic(Saturate((age - _tendrils[i].Delay) / GrowSeconds));
            if (revealT <= 0.001f) continue;

            BuildTendrilPath(i, screenSize, shortSide, time);

            UpdateLatch(i, screenSize, shortSide, time, dt, revealT);

            if (_tendrils[i].LatchBlend > 0.001f)
                ApplyLatchPin(i);

            _paths[i].BuildArc();

            var stroke = new StrokePrimitive
            {
                Path = _paths[i],
                Role = PrimitiveRole.MainStroke,
                Reveal = revealT,
                WidthHint = shortSide * _tendrils[i].BaseWidth,
                Brightness = alpha * _tendrils[i].Alpha,
                TipFlare = revealT < 0.999f ? 1f : 0f,
                Seed = _tendrils[i].Seed,
                Phase = _tendrils[i].Phase,
                FlushStart = true,
                ColorOverride = colorOverride,
            };
            scene.AddStroke(stroke);
        }
    }

    // =====================================================================================
    // Layout baking
    // =====================================================================================

    private void BuildTendrils(int castSeed)
    {
        int idx = 0;

        for (int i = 0; i < BottomCount; i++)
        {
            int s = unchecked(castSeed + 0x71D10000 + i * 7919);
            _tendrils[idx++] = new Tendril
            {
                Edge          = 2,
                Along         = DrawHelpers.HashRange(s,      0.02f, 0.98f),
                Length        = DrawHelpers.HashRange(s + 1,  0.36f, 0.68f),
                Curl          = DrawHelpers.HashRange(s + 2, -2.0f,  2.0f),
                WaveAmp       = DrawHelpers.HashRange(s + 3,  0.40f, 0.90f),
                WaveFreq      = DrawHelpers.HashRange(s + 4,  1.6f,  3.4f),
                BaseWidth     = DrawHelpers.HashRange(s + 5,  0.014f, 0.024f),
                Phase         = DrawHelpers.HashRange(s + 6,  0f, MathF.PI * 2f),
                Speed         = DrawHelpers.HashRange(s + 7,  0.15f, 0.45f),
                Alpha         = DrawHelpers.HashRange(s + 8,  0.80f, 1.00f),
                Delay         = DrawHelpers.HashRange(s + 11, 0f, 0.40f),
                StretchAmount = DrawHelpers.HashRange(s + 12, 0.01f, 0.20f),
                StretchPeriod = DrawHelpers.HashRange(s + 13, 4.5f, 8.5f),
                StretchOffset = DrawHelpers.Hash01(s + 14),
                LatchRotSpeed = MakeRotSpeed(s + 15, s + 16),
                Seed          = s,
                LatchKind     = -1,
            };
        }

        for (int side = 0; side < 2; side++)
        {
            byte edge = (byte)(side == 0 ? 3 : 1);
            for (int i = 0; i < SideCount; i++)
            {
                int s = unchecked(castSeed + 0x51DE0000 + side * 100000 + i * 7919);
                _tendrils[idx++] = new Tendril
                {
                    Edge          = edge,
                    Along         = DrawHelpers.HashRange(s,      0.15f, 1.00f),
                    Length        = DrawHelpers.HashRange(s + 1,  0.30f, 0.55f),
                    Curl          = DrawHelpers.HashRange(s + 2, -1.8f,  1.8f),
                    WaveAmp       = DrawHelpers.HashRange(s + 3,  0.35f, 0.80f),
                    WaveFreq      = DrawHelpers.HashRange(s + 4,  1.6f,  3.2f),
                    BaseWidth     = DrawHelpers.HashRange(s + 5,  0.010f, 0.018f),
                    Phase         = DrawHelpers.HashRange(s + 6,  0f, MathF.PI * 2f),
                    Speed         = DrawHelpers.HashRange(s + 7,  0.15f, 0.45f),
                    Alpha         = DrawHelpers.HashRange(s + 8,  0.65f, 0.90f),
                    Delay         = DrawHelpers.HashRange(s + 11, 0f, 0.45f),
                    StretchAmount = DrawHelpers.HashRange(s + 12, 0.15f, 0.30f),
                    StretchPeriod = DrawHelpers.HashRange(s + 13, 4.5f, 8.0f),
                    StretchOffset = DrawHelpers.Hash01(s + 14),
                    LatchRotSpeed = MakeRotSpeed(s + 15, s + 16),
                    Seed          = s,
                    LatchKind     = -1,
                };
            }
        }

        for (int i = 0; i < TopCount; i++)
        {
            int s = unchecked(castSeed + 0x70B00000 + i * 7919);
            _tendrils[idx++] = new Tendril
            {
                Edge          = 0,
                Along         = DrawHelpers.HashRange(s,      0.10f, 0.90f),
                Length        = DrawHelpers.HashRange(s + 1,  0.22f, 0.42f),
                Curl          = DrawHelpers.HashRange(s + 2, -1.6f,  1.6f),
                WaveAmp       = DrawHelpers.HashRange(s + 3,  0.30f, 0.65f),
                WaveFreq      = DrawHelpers.HashRange(s + 4,  1.4f,  2.8f),
                BaseWidth     = DrawHelpers.HashRange(s + 5,  0.008f, 0.014f),
                Phase         = DrawHelpers.HashRange(s + 6,  0f, MathF.PI * 2f),
                Speed         = DrawHelpers.HashRange(s + 7,  0.20f, 0.65f),
                Alpha         = DrawHelpers.HashRange(s + 8,  0.55f, 0.80f),
                Delay         = DrawHelpers.HashRange(s + 11, 0f, 0.50f),
                StretchAmount = DrawHelpers.HashRange(s + 12, 0.12f, 0.25f),
                StretchPeriod = DrawHelpers.HashRange(s + 13, 5.0f, 9.0f),
                StretchOffset = DrawHelpers.Hash01(s + 14),
                LatchRotSpeed = MakeRotSpeed(s + 15, s + 16),
                Seed          = s,
                LatchKind     = -1,
            };
        }
    }

    private static float MakeRotSpeed(int magSeed, int signSeed)
    {
        float mag  = DrawHelpers.HashRange(magSeed, 0.80f, 2.0f);
        float sign = DrawHelpers.Hash01(signSeed) < 0.5f ? -1f : 1f;
        return mag * sign;
    }

    // =====================================================================================
    // Path building — coarse skeleton + Catmull-Rom resample
    // =====================================================================================

    private void BuildTendrilPath(int idx, Vector2 screenSize, float shortSide, float time)
    {
        ref readonly var t = ref _tendrils[idx];
        var path = _paths[idx];

        Vector2 start = EdgeAnchor(screenSize, shortSide, t.Edge, t.Along);
        Vector2 inward = InwardDir(t.Edge);
        float baseAngle = MathF.Atan2(inward.Y, inward.X);

        float swayScale = 1f - (1f - LatchedSwayScale) * t.LatchBlend;
        float curlBoost = 1f + LatchedCurlBoost * t.LatchBlend;

        float stretchPhase = ((time / t.StretchPeriod) + t.StretchOffset) % 1f;
        float stretchPulse = stretchPhase < 0.45f
            ? MathF.Sin((stretchPhase / 0.45f) * MathF.PI)
            : 0f;
        float stretch = 1f + t.StretchAmount * stretchPulse * (1f - t.LatchBlend);

        float totalLen = shortSide * t.Length * stretch;
        float step = totalLen / (ControlPoints - 1);
        float swayTime = time * t.Speed;

        // 1. Coarse skeleton.
        Vector2 cursor = start;
        _coarse[0] = cursor;

        for (int i = 1; i < ControlPoints; i++)
        {
            float u = (float)i / (ControlPoints - 1);
            float heading = baseAngle
                + t.Curl * curlBoost * u
                + t.WaveAmp * swayScale * MathF.Sin(u * t.WaveFreq + t.Phase + swayTime);

            cursor += new Vector2(MathF.Cos(heading), MathF.Sin(heading)) * step;
            _coarse[i] = cursor;
        }

        // 2. Catmull-Rom resample to the smooth final path.
        CatmullRomResample(_coarse, ControlPoints, path.Points, FinalSamples);
        path.Count = FinalSamples;
    }

    /// <summary>
    /// Standard Catmull-Rom interpolation. Endpoints are duplicated for the phantom outer
    /// control points, which makes the curve terminate on the actual endpoints rather than
    /// overshooting past them.
    /// </summary>
    private static void CatmullRomResample(Vector2[] src, int srcCount, Vector2[] dst, int dstCount)
    {
        float scale = (float)(srcCount - 1) / (dstCount - 1);

        for (int i = 0; i < dstCount; i++)
        {
            float t = i * scale;
            int seg = (int)t;
            if (seg >= srcCount - 1) { seg = srcCount - 2; t = srcCount - 1; }
            float localT = t - seg;

            Vector2 p0 = src[Math.Max(0, seg - 1)];
            Vector2 p1 = src[seg];
            Vector2 p2 = src[Math.Min(srcCount - 1, seg + 1)];
            Vector2 p3 = src[Math.Min(srcCount - 1, seg + 2)];

            float t2 = localT * localT;
            float t3 = t2 * localT;

            dst[i] = 0.5f * (
                2f * p1 +
                (p2 - p0) * localT +
                (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
        }
    }

    // =====================================================================================
    // Latch pinning — u² correction + tip rotation, on the smooth path
    // =====================================================================================

    private void ApplyLatchPin(int idx)
    {
        ref readonly var t = ref _tendrils[idx];
        var path = _paths[idx].Points;

        Vector2 freeTip = path[FinalSamples - 1];
        Vector2 correction = t.LatchPoint - freeTip;

        for (int i = 0; i < FinalSamples; i++)
        {
            float u = (float)i / (FinalSamples - 1);
            path[i] += correction * (u * u) * t.LatchBlend;
        }

        if (t.LatchBlend > 0.02f && MathF.Abs(t.LatchRotation) > 0.001f)
        {
            for (int i = 0; i < FinalSamples; i++)
            {
                float u = (float)i / (FinalSamples - 1);
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

        float targetBlend = t.LatchKind >= 0 ? 1f : 0f;
        float blendDelta = dt / LatchBlendSeconds;
        if (MathF.Abs(targetBlend - t.LatchBlend) <= blendDelta)
            t.LatchBlend = targetBlend;
        else
            t.LatchBlend += MathF.Sign(targetBlend - t.LatchBlend) * blendDelta;

        if (t.LatchKind >= 0)
        {
            t.LatchRotation += t.LatchRotSpeed * dt;
            t.LatchRotation = Math.Clamp(t.LatchRotation, -LatchRotationCap, LatchRotationCap);
        }

        if (t.LatchKind >= 0 && time >= t.HoldUntil)
        {
            t.LatchKind = -1;
            t.CooldownUntil = time + 0.8f + 1.2f * DrawHelpers.Hash01(t.Seed + 501);
            return;
        }

        if (t.LatchKind >= 0) return;
        if (time < t.CooldownUntil) return;
        if (t.LatchBlend > 0.05f) return;
        if (revealT < 0.98f) return;

        Vector2 freeTip = _paths[idx].Points[FinalSamples - 1];
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

        int bucket = (int)(time * 6f);
        bool doLatch = DrawHelpers.Hash01(t.Seed + bucket * 131 + bestEdge * 7919) < 0.45f;
        if (!doLatch) return;

        t.LatchKind = bestEdge;
        t.LatchPoint = bestPoint;
        t.LatchRotation = 0f;
        t.HoldUntil = time + 1.8f + 1.6f * DrawHelpers.Hash01(t.Seed + 500);
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    private static Vector2 EdgeAnchor(Vector2 size, float shortSide, byte edge, float along)
    {
        float overhang = shortSide * 0.02f;
        return edge switch
        {
            0 => new Vector2(along * size.X, -overhang),
            1 => new Vector2(size.X + overhang, along * size.Y),
            2 => new Vector2(along * size.X, size.Y + overhang),
            _ => new Vector2(-overhang, along * size.Y),
        };
    }

    private static Vector2 InwardDir(byte edge) => edge switch
    {
        0 => new Vector2(0f, 1f),
        1 => new Vector2(-1f, 0f),
        2 => new Vector2(0f, -1f),
        _ => new Vector2(1f, 0f),
    };

    private static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    private static float EaseOutCubic(float t)
    {
        float u = 1f - Saturate(t);
        return 1f - u * u * u;
    }
}