using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Electrocution: you're being actively shocked, not standing in a storm. Where Paralysis spaces
/// out big dramatic strikes, Electrocution is a constant, chaotic crackle - many small bolts
/// firing from every edge continuously, dense spark spray, and a drifting ball of lightning that
/// wanders the frame and fires arcs to whichever edges are nearest to it.
///
/// Structure, like Paralysis:
///  - The frame is a rounded rectangle you can walk along by distance (PerimeterPoint).
///  - Bolts are fractal - BuildOffsets nudges each midpoint by a hashed amount that halves per
///    level, with coarse levels fixed for the strike and fine levels re-rolled on a fast tick.
///  - Strikes are pure functions of "seconds since they started" - no per-strike state stored.
///  - Polylines, not chain-of-segments, so no notches at the kinks.
///
/// What's different from Paralysis:
///  - ~2.5x as many concurrent strikes, each shorter-lived, so the edge is almost always lit.
///  - A continuous spark field: sparks constantly spray inward from edge impacts.
///  - A BALL OF LIGHTNING drifts slowly through the frame on a Lissajous path and fires arcs to
///    its two nearest edges - OR, when one edge is dramatically closer than the next, to TWO
///    separated points on that same edge. That avoids twenty-second-long bolts when the ball
///    happens to sit near one edge, without the arcs clumping into a single visible beam.
///  - The opening volley fires all four corners SIMULTANEOUSLY with a bright white flash.
///
/// Photosensitivity: bolts are thin, so small-area flicker is fine. The full-screen element is a
/// smoothed, capped function of recent strike energy - never the raw per-tick flicker - so rapid
/// strikes merge into a sustained glow rather than strobing.
/// </summary>
public sealed class ElectrocutionEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Electrocution;

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f;
    private const float StrikeLife        = 0.42f;
    private const float BallFadeInSeconds = 0.45f;
    private const float BallSpawnAt       = 0.30f;

    // ---- ball lightning range behaviour ----
    // If the second-nearest edge is more than this many times farther than the nearest, the
    // ball treats it as "out of range" and fires its second arc to the SAME nearest edge
    // instead - at a separate point along it.
    private const float SameEdgeFallbackRatio = 1.6f;

    // Half-range of the target jitter along the chosen edge, in pixels (at 1080p scale).
    private const float EdgeJitterPx = 50f;

    // When firing both arcs to the same edge, the two targets are pushed apart by this much
    // (in pixels, at 1080p scale) along the edge so they visibly read as two separate bolts.
    private const float SameEdgeSpreadMinPx = 60f;
    private const float SameEdgeSpreadMaxPx = 150f;

    // ---- how busy ----
    private const int SteadySlots   = 14;
    private const int MaxStrikes    = 32;
    private const int HumSlots      = 18;
    private const int Racers        = 4;
    private const int TailSlices    = 4;

    private const int MaxPoints       = 65;
    private const int MaxBranchPoints = 17;

    private uint _hot, _mid, _glow, _ink, _surge;
    private bool _paletteReady;

    private float _lastDrawTime = -100f;
    private float _castStart;

    private float _w, _h, _m, _px, _ins, _cr, _len, _qa;
    private float _e0, _e1, _e2, _e3, _e4, _e5, _e6;

    private readonly Vector2[] _trunk  = new Vector2[MaxPoints];
    private readonly Vector2[] _branch = new Vector2[MaxBranchPoints];
    private readonly Vector2[] _draw   = new Vector2[MaxPoints + 1];
    private readonly float[]   _off    = new float[MaxPoints];
    private readonly Strike[]  _strikes = new Strike[MaxStrikes];

    private struct Strike
    {
        public int Seed;
        public int Corner;
        public float Since;
        public float Weight;
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;

        if (time - _lastDrawTime > NewCastGapSeconds) _castStart = time;
        _lastDrawTime = time;
        float age = time - _castStart;
        int castSeed = unchecked((int)(_castStart * 1000f));

        EnsurePalette();
        SetupFrame(screenSize);

        int count = CollectStrikes(age, castSeed);

        float energy = 0f;
        for (int i = 0; i < count; i++)
            energy += _strikes[i].Weight * GlowEnvelope(_strikes[i].Since);

        float backBuzz = 0.42f + 0.30f * DrawHelpers.Hash01((int)(time * 22f));
        float totalEnergy = MathF.Min(1f, energy + backBuzz * 0.35f);

        DrawFlatFlicker(dl, screenSize, alpha, time);
        DrawEdgeGlow(dl, alpha, totalEnergy);
        DrawHum(dl, age, alpha);
        DrawRacers(dl, age, alpha);

        for (int i = 0; i < count; i++)
            DrawStrike(dl, in _strikes[i], alpha);

        DrawResidualSparks(dl, age, castSeed, alpha);
        DrawBallLightning(dl, age, castSeed, alpha);
    }

    // =====================================================================================
    // Strikes
    // =====================================================================================

    private int CollectStrikes(float age, int castSeed)
    {
        int n = 0;

        // ---- intro: two rapid-fire all-corner volleys ----
        if (age < 0.45f + StrikeLife)
        {
            for (int wave = 0; wave < 2; wave++)
            {
                float waveStart = 0.05f + wave * 0.18f;
                for (int c = 0; c < 4; c++)
                {
                    float since = age - waveStart;
                    if (since < 0f || since >= StrikeLife) continue;
                    if (n >= MaxStrikes) break;
                    _strikes[n++] = new Strike
                    {
                        Seed = Mix(castSeed, 1000 + wave * 10 + c),
                        Corner = c,
                        Since = since,
                        Weight = wave == 0 ? 1.2f : 0.85f,
                    };
                }
            }
        }

        // ---- steady: many small slots ----
        for (int s = 0; s < SteadySlots; s++)
        {
            float period = 0.55f + 0.65f * DrawHelpers.Hash01(Mix(s, 11));
            float phase  = DrawHelpers.Hash01(Mix(s, 12)) * period;
            int k = (int)MathF.Floor((age - phase) / period);

            for (int c = k - 1; c <= k; c++)
            {
                if (c < 0 || n >= MaxStrikes) continue;
                int seed = Mix(Mix(castSeed, s + 200), c);
                float start = phase + c * period + DrawHelpers.Hash01(seed + 3) * period * 0.35f;
                float since = age - start;
                if (since < 0f || since >= StrikeLife) continue;
                if (start < 0.30f) continue;
                _strikes[n++] = new Strike
                {
                    Seed = seed, Corner = -1, Since = since, Weight = 0.55f,
                };
            }
        }

        // ---- surges: occasional larger strikes ----
        for (int s = 0; s < 3; s++)
        {
            float period = 1.4f + 1.0f * DrawHelpers.Hash01(Mix(s, 41));
            float phase  = DrawHelpers.Hash01(Mix(s, 42)) * period;
            int k = (int)MathF.Floor((age - phase) / period);

            for (int c = k - 1; c <= k; c++)
            {
                if (c < 0 || n >= MaxStrikes) continue;
                int seed = Mix(Mix(castSeed, s + 500), c);
                float start = phase + c * period + DrawHelpers.Hash01(seed + 3) * period * 0.30f;
                float since = age - start;
                if (since < 0f || since >= StrikeLife) continue;
                if (start < 0.55f) continue;
                _strikes[n++] = new Strike
                {
                    Seed = seed, Corner = -1, Since = since, Weight = 1.15f,
                };
            }
        }

        return n;
    }

    private static float GlowEnvelope(float since) =>
        (1f - MathF.Exp(-since / 0.03f)) * MathF.Exp(-since / 0.32f);

    private void DrawStrike(ImDrawListPtr dl, in Strike st, float alpha)
    {
        float since = st.Since;
        int seed = st.Seed;
        bool intro = st.Corner >= 0;
        bool surge = st.Weight > 1f && !intro;

        float env = since < 0.04f ? 1f : MathF.Exp(-(since - 0.04f) / 0.14f);
        int roll = since < 0.15f ? (int)(since / 0.04f) : 4 + (int)((since - 0.15f) / 0.06f);
        float b = env * (0.66f + 0.34f * DrawHelpers.Hash01(Mix(seed, roll * 31 + 7)));
        if (b < 0.03f) return;
        int fine = Mix(seed, roll + 100);
        float wScale = (intro ? 1.35f : 1f) * (surge ? 1.4f : 1f);

        float pick = DrawHelpers.Hash01(seed + 5);
        int type;
        if (intro) type = 2;
        else if (surge) type = 1;
        else type = pick < 0.55f ? 0 : pick < 0.88f ? 1 : 2;

        float s0 = 0f, chord = 0f, reach = 0f;
        Vector2 anchorA, anchorB = default, inwardA, inwardB = default;
        int points;

        if (type == 0)
        {
            s0 = DrawHelpers.Hash01(seed + 8) * _len;
            chord = _m * DrawHelpers.HashRange(seed + 6, 0.14f, 0.34f) * (DrawHelpers.Hash01(seed + 7) < 0.5f ? -1f : 1f);
            float bulge = _m * DrawHelpers.HashRange(seed + 9, 0.025f, 0.065f);
            points = BuildArc(_trunk, s0, chord, bulge, 5, seed, fine, 0.12f);
            anchorA = PerimeterPoint(s0, out inwardA);
            anchorB = PerimeterPoint(s0 + chord, out inwardB);
        }
        else
        {
            int corner = intro ? st.Corner : (int)(DrawHelpers.Hash01(seed + 8) * 4f);
            s0 = type == 2 ? CornerDistance(corner) : DrawHelpers.Hash01(seed + 8) * _len;
            anchorA = PerimeterPoint(s0, out inwardA);

            float spread = type == 2 ? DrawHelpers.HashRange(seed + 6, -0.35f, 0.35f) : DrawHelpers.HashRange(seed + 6, -0.60f, 0.60f);
            float ang = MathF.Atan2(inwardA.Y, inwardA.X) + spread;
            var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));

            reach = _m * (type == 2
                ? (intro ? DrawHelpers.HashRange(seed + 10, 0.32f, 0.42f) : DrawHelpers.HashRange(seed + 10, 0.22f, 0.32f))
                : DrawHelpers.HashRange(seed + 10, 0.12f, 0.26f));
            points = BuildLine(_trunk, anchorA, dir, reach, reach > 0.19f * _m ? 5 : 4, seed, fine, 0.18f);
        }

        int segs = points - 1;
        float length = type == 0 ? MathF.Abs(chord) : reach;
        float growTime = 0.03f + 0.10f * length / _m;
        float g = Saturate(since / growTime);

        int forks = intro ? 6
                  : surge ? 6
                  : type == 0 ? 3 + (DrawHelpers.Hash01(seed + 11) < 0.5f ? 1 : 0)
                              : 4 + (DrawHelpers.Hash01(seed + 12) < 0.5f ? 1 : 0);
        for (int f = 0; f < forks; f++)
        {
            int fs = Mix(seed, 200 + f);
            int at = (int)(segs * DrawHelpers.HashRange(fs, 0.14f, 0.86f));
            float attach = growTime * at / segs;
            if (since < attach) continue;

            float side = DrawHelpers.Hash01(fs + 1) < 0.5f ? -1f : 1f;
            Vector2 fdir;
            float flen;
            if (type == 0)
            {
                PerimeterPoint(s0 + chord * at / segs, out Vector2 inw);
                fdir = Rotate(inw, side * DrawHelpers.HashRange(fs + 2, 0f, 0.85f));
                flen = _m * DrawHelpers.HashRange(fs + 3, 0.05f, 0.14f);
            }
            else
            {
                Vector2 tangent = Normalize(_trunk[Math.Min(at + 1, segs)] - _trunk[Math.Max(at - 1, 0)]);
                fdir = Rotate(tangent, side * DrawHelpers.HashRange(fs + 2, 0.30f, 1.05f));
                flen = reach * DrawHelpers.HashRange(fs + 3, 0.22f, 0.55f);
            }

            int fpoints = BuildLine(_branch, _trunk[at], fdir, flen, 3, fs, Mix(fs, roll + 100), 0.22f);
            float gf = Saturate((since - attach) / (growTime * 0.7f + 0.02f));
            int visible = CopyVisible(_branch, fpoints, gf, _draw);
            Stroke(dl, _draw, visible, b * 0.78f, alpha, 0.55f * wScale, major: false);
        }

        int shown = CopyVisible(_trunk, points, g, _draw);
        Stroke(dl, _draw, shown, b, alpha, wScale, major: true);
        if (g < 1f && shown >= 2)
            Flare(dl, _draw[shown - 1], 0.55f, alpha);

        float flash = Saturate(1f - since / 0.18f);
        if (flash > 0f)
        {
            Flare(dl, anchorA, flash * 1.1f, alpha);
            if (type == 0) Flare(dl, anchorB, flash * 0.9f, alpha);
        }

        int sparkCount = intro ? 10 : surge ? 8 : 5;
        if (since < 0.5f)
        {
            Sparks(dl, anchorA, inwardA, Mix(seed, 300), since, alpha, sparkCount, 1f);
            if (type == 0) Sparks(dl, anchorB, inwardB, Mix(seed, 310), since, alpha, sparkCount, 0.85f);
        }
    }

    // =====================================================================================
    // Ball lightning
    // =====================================================================================

    /// <summary>
    /// A drifting ball of lightning. Position follows a slow Lissajous path that keeps it well
    /// inside the frame, so it visibly wanders from moment to moment without ever needing to
    /// bounce or wrap.
    ///
    /// Each frame the ball picks its two nearest edges, then fires one arc to each. If the
    /// second-nearest edge is dramatically farther away than the nearest (more than
    /// SameEdgeFallbackRatio times farther), the ball treats it as out of range and instead
    /// fires BOTH arcs to the nearest edge - at two points along it spread apart so they don't
    /// read as a single doubled-up bolt. This is what stops the ball from shooting a super-long
    /// bolt to the far side of the screen when it happens to drift near one edge.
    ///
    /// The arcs re-roll their fractal shape and their landing point every ~1/12 second, so they
    /// crackle rather than being stable beams.
    /// </summary>
    private void DrawBallLightning(ImDrawListPtr dl, float age, int castSeed, float alpha)
    {
        if (age < BallSpawnAt) return;
        float fade = Saturate((age - BallSpawnAt) / BallFadeInSeconds);
        if (fade <= 0.003f) return;

        // ---- ball position: slow wandering Lissajous ----
        float phX = DrawHelpers.Hash01(Mix(castSeed, 71)) * MathF.Tau;
        float phY = DrawHelpers.Hash01(Mix(castSeed, 72)) * MathF.Tau;

        float bx = _w * (0.5f
                        + 0.32f * MathF.Sin(age * 0.33f + phX)
                        + 0.06f * MathF.Sin(age * 0.61f + phY));
        float by = _h * (0.5f
                        + 0.28f * MathF.Sin(age * 0.27f + phY)
                        + 0.05f * MathF.Sin(age * 0.44f + phX));
        Vector2 ballPos = new(bx, by);

        // ---- ball's own pulse ----
        int ballTick = (int)(age * 24f);
        float pulse = 0.80f + 0.20f * DrawHelpers.Hash01(Mix(castSeed, ballTick));
        float ballR = _m * 0.024f * pulse;

        // ---- distances to each edge (0=top, 1=right, 2=bottom, 3=left) ----
        Span<int>   order = stackalloc int[4]   { 0, 1, 2, 3 };
        Span<float> dists = stackalloc float[4] { by, _w - bx, _h - by, bx };

        for (int i = 0; i < 2; i++)
        {
            int minIdx = i;
            for (int j = i + 1; j < 4; j++)
                if (dists[order[j]] < dists[order[minIdx]]) minIdx = j;
            (order[i], order[minIdx]) = (order[minIdx], order[i]);
        }

        int edgeA = order[0];
        int edgeB = order[1];

        // ---- range check: is edge B far enough away to be worth reaching? ----
        bool sameEdgePair = dists[edgeB] > dists[edgeA] * SameEdgeFallbackRatio;
        if (sameEdgePair) edgeB = edgeA;

        // ---- arcs re-roll on this tick ----
        int arcTick = (int)(age * 12f);

        // Scale jitter/spread with resolution the same way other pixel-based values do.
        float jitter = EdgeJitterPx * _px;
        float spreadMin = SameEdgeSpreadMinPx * _px;
        float spreadMax = SameEdgeSpreadMaxPx * _px;

        for (int i = 0; i < 2; i++)
        {
            int edge = i == 0 ? edgeA : edgeB;
            int arcSeed = Mix(Mix(castSeed, 200 + i), arcTick);

            // If both arcs go to the same edge, push their targets apart along that edge so
            // they don't overlap into one doubled line.
            float spreadOffset = 0f;
            if (sameEdgePair)
            {
                float sign = i == 0 ? -1f : 1f;
                spreadOffset = sign * DrawHelpers.HashRange(arcSeed + 50, spreadMin, spreadMax);
            }

            // Target: point on the chosen edge, roughly perpendicular from the ball, with jitter
            // plus (when same-edge) the deliberate spread offset.
            float jitterOffset = DrawHelpers.HashRange(arcSeed + 1, -jitter, jitter);
            Vector2 target = edge switch
            {
                0 => new Vector2(bx + jitterOffset + spreadOffset, _ins),
                1 => new Vector2(_w - _ins, by + jitterOffset + spreadOffset),
                2 => new Vector2(bx + jitterOffset + spreadOffset, _h - _ins),
                _ => new Vector2(_ins, by + jitterOffset + spreadOffset),
            };

            // Clamp along the edge so the target stays on-frame even with the spread offset.
            target.X = Math.Clamp(target.X, _ins, _w - _ins);
            target.Y = Math.Clamp(target.Y, _ins, _h - _ins);

            float arcBright = (0.70f + 0.30f * DrawHelpers.Hash01(arcSeed + 2)) * fade;

            Vector2 d = target - ballPos;
            float len = d.Length();
            if (len < 4f) continue;
            Vector2 dir = d / len;

            int points = BuildLine(_trunk, ballPos, dir, len, 5, arcSeed, Mix(arcSeed, 30), 0.16f);

            // The ball's arcs are its main visual, so they get slightly wider glow than a
            // normal strike.
            float w = _px * 1.15f;
            ref Vector2 first = ref _trunk[0];
            dl.AddPolyline(ref first, points, C(_ink,  0.20f * arcBright, alpha), ImDrawFlags.None, 6.0f * w);
            dl.AddPolyline(ref first, points, C(_glow, 0.075f * arcBright, alpha), ImDrawFlags.None, 18f * w);
            dl.AddPolyline(ref first, points, C(_glow, 0.18f * arcBright, alpha), ImDrawFlags.None, 8.5f * w);
            dl.AddPolyline(ref first, points, C(_mid,  0.60f * arcBright, alpha), ImDrawFlags.None, 4.0f * w);
            dl.AddPolyline(ref first, points, C(_hot,  0.97f * arcBright, alpha), ImDrawFlags.None, 1.7f * w);

            // Flare at the edge endpoint.
            Flare(dl, target, 0.85f * fade, alpha);

            // Small flare at the ball side so the connection reads as a source.
            if (i == 0)
                Flare(dl, ballPos + dir * (ballR * 0.8f), 0.55f * fade, alpha);
        }

        // ---- ball body ----
        dl.AddCircleFilled(ballPos, ballR * 5.5f, C(_glow,  0.07f * fade, alpha));
        dl.AddCircleFilled(ballPos, ballR * 2.8f, C(_glow,  0.20f * fade, alpha));
        dl.AddCircleFilled(ballPos, ballR * 1.5f, C(_mid,   0.70f * fade, alpha));
        dl.AddCircleFilled(ballPos, ballR,        C(_hot,   0.95f * fade, alpha));
        dl.AddCircleFilled(ballPos, ballR * 0.5f, C(_surge, 1.00f * fade, alpha));

        // ---- surface spokes ----
        int spokeTick = (int)(age * 30f);
        for (int k = 0; k < 5; k++)
        {
            int ks = Mix(Mix(castSeed, 300 + k), spokeTick);
            float ang = DrawHelpers.HashRange(ks, 0f, MathF.Tau);
            float spokeLen = ballR * DrawHelpers.HashRange(ks + 1, 1.2f, 2.4f);
            Vector2 spokeTip = ballPos + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * spokeLen;
            dl.AddLine(ballPos, spokeTip, C(_hot, 0.60f * fade, alpha), 1.2f * _px);
        }
    }

    // =====================================================================================
    // Residual sparks
    // =====================================================================================

    private void DrawResidualSparks(ImDrawListPtr dl, float age, int castSeed, float alpha)
    {
        const int slots = 16;
        for (int s = 0; s < slots; s++)
        {
            float period = 0.25f + 0.40f * DrawHelpers.Hash01(Mix(s, 81));
            float phase  = DrawHelpers.Hash01(Mix(s, 82)) * period;
            int k = (int)MathF.Floor((age - phase) / period);

            for (int c = k - 1; c <= k; c++)
            {
                if (c < 0) continue;
                int seed = Mix(Mix(castSeed, s + 900), c);
                float start = phase + c * period + DrawHelpers.Hash01(seed + 3) * period * 0.40f;
                float since = age - start;
                if (since < 0f || since >= 0.35f) continue;
                if (start < 0.40f) continue;

                float t01 = since / 0.35f;

                float s0 = DrawHelpers.Hash01(seed + 4) * _len;
                Vector2 origin = PerimeterPoint(s0, out Vector2 inward);

                float spread = DrawHelpers.HashRange(seed + 5, -0.9f, 0.9f);
                float ang = MathF.Atan2(inward.Y, inward.X) + spread;
                var vel = new Vector2(MathF.Cos(ang), MathF.Sin(ang))
                        * (_m * DrawHelpers.HashRange(seed + 6, 0.35f, 0.75f));

                float life = 0.35f;
                float k1 = 1f - t01 / life * life;
                float a = alpha * k1 * (0.55f + 0.45f * DrawHelpers.Hash01(seed + 7));
                if (a < 0.02f) continue;

                Vector2 head = origin + vel * (t01 * 0.35f);
                Vector2 tail = head - Normalize(vel) * (6f + 10f * t01);

                dl.AddLine(tail, head, C(_hot, 0.90f * k1, alpha), 1.6f * _px);
                dl.AddCircleFilled(head, 1.4f * _px, C(_surge, 0.95f * k1, alpha));
            }
        }
    }

    // =====================================================================================
    // Crackle + currents
    // =====================================================================================

    private void DrawHum(ImDrawListPtr dl, float age, float alpha)
    {
        for (int i = 0; i < HumSlots; i++)
        {
            float x = age * (9.5f + 5f * DrawHelpers.Hash01(Mix(i, 21))) + 10f * DrawHelpers.Hash01(Mix(i, 22));
            int epoch = (int)MathF.Floor(x);
            float u = x - epoch;
            int seed = Mix(Mix(i, 23), epoch);
            if (DrawHelpers.Hash01(seed) > 0.70f) continue;

            float life = u < 0.12f ? u / 0.12f : 1f - (u - 0.12f) / 0.88f;
            float len = _m * DrawHelpers.HashRange(seed + 1, 0.04f, 0.12f) * (DrawHelpers.Hash01(seed + 2) < 0.5f ? -1f : 1f);
            float s0 = DrawHelpers.Hash01(seed + 3) * _len;
            float bulge = _m * DrawHelpers.HashRange(seed + 4, 0.004f, 0.016f);
            int points = BuildArc(_branch, s0, len, bulge, 3, seed, seed + 5, 0.08f);
            Stroke(dl, _branch, points, 0.55f * life, alpha, 0.45f, major: false);
        }
    }

    private void DrawRacers(ImDrawListPtr dl, float age, float alpha)
    {
        float slice = _len * 0.020f;
        int tick = (int)(age * 34f);
        float fadeIn = Saturate(age / 0.35f);

        for (int j = 0; j < Racers; j++)
        {
            float dir = (j & 1) == 0 ? 1f : -1f;
            float speed = _len * (0.22f + 0.050f * j);
            float head = DrawHelpers.Hash01(Mix(j, 31)) * _len + dir * speed * age;
            float surge = fadeIn * (0.55f + 0.45f * DrawHelpers.Pulse(age, 1.8f + 0.55f * j, DrawHelpers.Hash01(Mix(j, 32))));

            for (int k = 0; k < TailSlices; k++)
            {
                float fade = k == 0 ? 1f : k == 1 ? 0.55f : k == 2 ? 0.28f : 0.12f;
                int points = BuildArc(_branch, head - dir * (k + 1) * slice, dir * slice, 0f, 2, Mix(j, 33), Mix(Mix(j, 34 + k), tick), 0.15f, coarseLevels: 0);
                Stroke(dl, _branch, points, 0.90f * fade * surge, alpha, 0.75f, major: false);
            }

            Vector2 tip = PerimeterPoint(head, out _);
            dl.AddCircleFilled(tip, 9f * _px, C(_glow, 0.24f * surge, alpha));
            dl.AddCircleFilled(tip, 3.0f * _px, C(_hot, 0.92f * surge, alpha));
        }
    }

    // =====================================================================================
    // Full-frame flicker + edge glow
    // =====================================================================================

    private void DrawFlatFlicker(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        int bucket = (int)(time * 26f);
        float buzz = 0.45f + 0.55f * DrawHelpers.Hash01(bucket);
        float a = alpha * 0.055f * buzz;
        if (a <= 0.001f) return;

        dl.AddRectFilled(DrawHelpers.V(0, 0), screenSize,
            DrawHelpers.WithAlpha(_glow, a));
    }

    private void DrawEdgeGlow(ImDrawListPtr dl, float alpha, float energy)
    {
        float a = MathF.Min(0.20f, alpha * (0.055f + 0.10f * energy));
        if (a <= 0.002f) return;

        float t = _m * 0.12f;
        uint edge  = DrawHelpers.WithAlpha(_glow, a);
        uint clear = DrawHelpers.WithAlpha(_glow, 0f);
        dl.AddRectFilledMultiColor(new Vector2(0f, 0f), new Vector2(_w, t), edge, edge, clear, clear);
        dl.AddRectFilledMultiColor(new Vector2(0f, _h - t), new Vector2(_w, _h), clear, clear, edge, edge);
        dl.AddRectFilledMultiColor(new Vector2(0f, 0f), new Vector2(t, _h), edge, clear, clear, edge);
        dl.AddRectFilledMultiColor(new Vector2(_w - t, 0f), new Vector2(_w, _h), clear, edge, edge, clear);
    }

    // =====================================================================================
    // Bolt building
    // =====================================================================================

    private void BuildOffsets(int levels, int seedCoarse, int seedFine, float amp, float rough, int coarseLevels)
    {
        int n = 1 << levels;
        _off[0] = 0f;
        _off[n] = 0f;
        float a = amp;
        for (int lv = 1; lv <= levels; lv++)
        {
            int step = n >> (lv - 1), half = step >> 1;
            int seed = lv <= coarseLevels ? seedCoarse : seedFine;
            for (int i = 0; i < n; i += step)
                _off[i + half] = 0.5f * (_off[i] + _off[i + step])
                                 + (DrawHelpers.Hash01(unchecked(seed + lv * 7919 + i * 131)) * 2f - 1f) * a;
            a *= rough;
        }
    }

    private int BuildLine(Vector2[] dst, Vector2 p0, Vector2 dir, float length, int levels, int seedCoarse, int seedFine, float ampFrac, int coarseLevels = 2)
    {
        int n = 1 << levels;
        BuildOffsets(levels, seedCoarse, seedFine, length * ampFrac, 0.56f, coarseLevels);
        var perp = new Vector2(-dir.Y, dir.X);
        for (int i = 0; i <= n; i++)
        {
            Vector2 p = p0 + dir * (length * i / n) + perp * _off[i];
            dst[i] = new Vector2(Math.Clamp(p.X, 0f, _w), Math.Clamp(p.Y, 0f, _h));
        }
        return n + 1;
    }

    private int BuildArc(Vector2[] dst, float s0, float length, float bulge, int levels, int seedCoarse, int seedFine, float ampFrac, int coarseLevels = 2)
    {
        int n = 1 << levels;
        BuildOffsets(levels, seedCoarse, seedFine, MathF.Abs(length) * ampFrac, 0.56f, coarseLevels);
        for (int i = 0; i <= n; i++)
        {
            float u = (float)i / n;
            Vector2 p = PerimeterPoint(s0 + length * u, out Vector2 inward);
            float d = bulge * 4f * u * (1f - u) + _off[i];
            if (d < -_ins * 0.6f) d = -_ins * 0.6f;
            dst[i] = p + inward * d;
        }
        return n + 1;
    }

    private static int CopyVisible(Vector2[] src, int count, float g, Vector2[] dst)
    {
        if (g <= 0.001f) return 0;
        float f = g * (count - 1);
        int full = (int)f;
        if (full >= count - 1)
        {
            Array.Copy(src, dst, count);
            return count;
        }
        for (int i = 0; i <= full; i++) dst[i] = src[i];
        dst[full + 1] = Vector2.Lerp(src[full], src[full + 1], f - full);
        return full + 2;
    }

    private void Stroke(ImDrawListPtr dl, Vector2[] pts, int count, float b, float alpha, float widthScale, bool major)
    {
        if (count < 2 || b <= 0.01f) return;
        float w = _px * widthScale;
        ref Vector2 first = ref pts[0];
        if (major)
        {
            dl.AddPolyline(ref first, count, C(_ink, 0.22f * b, alpha), ImDrawFlags.None, 5.4f * w);
            dl.AddPolyline(ref first, count, C(_glow, 0.065f * b, alpha), ImDrawFlags.None, 15f * w);
        }
        dl.AddPolyline(ref first, count, C(_glow, 0.16f * b, alpha), ImDrawFlags.None, 7.5f * w);
        dl.AddPolyline(ref first, count, C(_mid, 0.60f * b, alpha), ImDrawFlags.None, 3.8f * w);
        dl.AddPolyline(ref first, count, C(_hot, 0.97f * b, alpha), ImDrawFlags.None, 1.6f * w);
    }

    private void Flare(ImDrawListPtr dl, Vector2 p, float k, float alpha)
    {
        dl.AddCircleFilled(p, (5f + 10f * k) * _px, C(_glow, 0.22f * k, alpha));
        dl.AddCircleFilled(p, (2.0f + 3.0f * k) * _px, C(_hot, 0.95f * k, alpha));
        dl.AddCircleFilled(p, (0.8f + 1.2f * k) * _px, C(_surge, 0.95f * k, alpha));
    }

    private void Sparks(ImDrawListPtr dl, Vector2 from, Vector2 inward, int seed, float since, float alpha, int count, float weightScale)
    {
        float baseAngle = MathF.Atan2(inward.Y, inward.X);
        for (int j = 0; j < count; j++)
        {
            int s = Mix(seed, j);
            float life = DrawHelpers.HashRange(s, 0.18f, 0.42f);
            float t = since - DrawHelpers.HashRange(s + 1, 0f, 0.05f);
            if (t < 0f || t >= life) continue;

            float angle = baseAngle + DrawHelpers.HashRange(s + 2, -1.35f, 1.35f);
            var v = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (DrawHelpers.HashRange(s + 3, 190f, 480f) * _px);
            float k = 1f - t / life;
            Vector2 pos = from + v * (t * (1f - 0.42f * t / life));
            dl.AddLine(pos, pos - v * 0.030f, C(_hot, 0.90f * k * k * weightScale, alpha), 1.4f * _px);
            dl.AddCircleFilled(pos, 1.4f * _px, C(_surge, 0.95f * k * weightScale, alpha));
        }
    }

    // =====================================================================================
    // Frame geometry
    // =====================================================================================

    private void SetupFrame(Vector2 size)
    {
        _w = size.X;
        _h = size.Y;
        _m = MathF.Min(_w, _h);
        _px = Math.Clamp(_m / 1080f, 0.75f, 2.4f);
        _ins = _m * 0.009f;

        float w2 = _w - 2f * _ins, h2 = _h - 2f * _ins;
        _cr = MathF.Min(_m * 0.055f, MathF.Min(w2, h2) * 0.5f);
        float straightW = MathF.Max(0f, w2 - 2f * _cr), straightH = MathF.Max(0f, h2 - 2f * _cr);
        _qa = MathF.PI * 0.5f * _cr;

        _e0 = straightW;
        _e1 = _e0 + _qa;
        _e2 = _e1 + straightH;
        _e3 = _e2 + _qa;
        _e4 = _e3 + straightW;
        _e5 = _e4 + _qa;
        _e6 = _e5 + straightH;
        _len = _e6 + _qa;
    }

    private Vector2 PerimeterPoint(float s, out Vector2 inward)
    {
        s %= _len;
        if (s < 0f) s += _len;

        if (s < _e0) { inward = new Vector2(0f, 1f); return new Vector2(_ins + _cr + s, _ins); }
        if (s < _e1) return RoundCorner(new Vector2(_w - _ins - _cr, _ins + _cr), -MathF.PI * 0.5f + (s - _e0) / _cr, out inward);
        if (s < _e2) { inward = new Vector2(-1f, 0f); return new Vector2(_w - _ins, _ins + _cr + (s - _e1)); }
        if (s < _e3) return RoundCorner(new Vector2(_w - _ins - _cr, _h - _ins - _cr), (s - _e2) / _cr, out inward);
        if (s < _e4) { inward = new Vector2(0f, -1f); return new Vector2(_w - _ins - _cr - (s - _e3), _h - _ins); }
        if (s < _e5) return RoundCorner(new Vector2(_ins + _cr, _h - _ins - _cr), MathF.PI * 0.5f + (s - _e4) / _cr, out inward);
        if (s < _e6) { inward = new Vector2(1f, 0f); return new Vector2(_ins, _h - _ins - _cr - (s - _e5)); }
        return RoundCorner(new Vector2(_ins + _cr, _ins + _cr), MathF.PI + (s - _e6) / _cr, out inward);
    }

    private Vector2 RoundCorner(Vector2 centre, float angle, out Vector2 inward)
    {
        var outward = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        inward = -outward;
        return centre + outward * _cr;
    }

    private float CornerDistance(int corner) => (corner & 3) switch
    {
        0 => _e6 + _qa * 0.5f,
        1 => _e0 + _qa * 0.5f,
        2 => _e2 + _qa * 0.5f,
        _ => _e4 + _qa * 0.5f,
    };

    // =====================================================================================
    // Small helpers
    // =====================================================================================

    private void EnsurePalette()
    {
        if (_paletteReady) return;
        _hot   = DrawHelpers.ToU32(1.00f, 0.98f, 0.72f, 1f);
        _mid   = DrawHelpers.ToU32(1.00f, 0.86f, 0.28f, 1f);
        _glow  = DrawHelpers.ToU32(1.00f, 0.62f, 0.10f, 1f);
        _ink   = DrawHelpers.ToU32(0.16f, 0.08f, 0.00f, 1f);
        _surge = DrawHelpers.ToU32(1.00f, 1.00f, 1.00f, 1f);
        _paletteReady = true;
    }

    private static uint C(uint color, float amount, float alpha) => DrawHelpers.WithAlpha(color, amount * alpha);

    private static int Mix(int a, int b) => unchecked(a * 0x27D4EB2F + b * 0x165667B1 + 0x3C6EF372);

    private static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    private static Vector2 Rotate(Vector2 v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    private static Vector2 Normalize(Vector2 v)
    {
        float len = v.Length();
        return len > 1e-5f ? v / len : new Vector2(0f, -1f);
    }
}