using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// A storm trapped inside the frame of the screen. Lightning arcs between points on the border, strikes
/// inward from it and forks as it goes, while small arcs hop along the edge and currents race around the
/// frame between strikes. The moment the debuff lands, four corner strikes fire in a clockwise volley -
/// the difference between "you're being zapped" and "you just got zapped".
///
/// How it's built, since none of it is stored between frames:
///  - The border is a rounded rectangle you can walk along by distance (PerimeterPoint), and every arc,
///    bolt and racer is placed by that distance, so they follow the corners for free.
///  - A bolt is a fractal: start with a straight line and repeatedly nudge each midpoint sideways by a
///    hashed amount that halves every level (BuildOffsets). Coarse levels use a seed that's fixed for the
///    whole strike and fine levels use a seed that re-rolls every ~50ms, so a bolt keeps its overall path
///    but its fine jaggedness crackles.
///  - Each strike is a pure function of "seconds since it started": it shoots along its path in ~60ms
///    (the leader), holds bright for a moment, then decays with a flicker. Strike start times come from
///    a small schedule of slots (CollectStrikes), so the whole effect is deterministic, allocation-free
///    and needs no bookkeeping.
///  - Every bolt is drawn as native polylines (AddPolyline) - one mitered stroke per glow layer - rather
///    than a chain of separate line segments, which would leave a notch at every kink.
///
/// Photosensitivity: bolts are thin, so their flicker is a small-area effect. The one large-area element
/// is the warm glow along the edges, and that is deliberately a smoothed, capped function of recent
/// strikes (not of each individual flicker), so rapid strikes merge into a glow rather than strobing.
///
/// The palette (hot yellow-white / amber / orange) is the one thing kept from the previous version.
/// </summary>
public sealed class ParalysisEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Paralysis;

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f; // long enough that an ordinary frame hitch mid-fight never replays the opening volley
    private const float StrikeLife = 0.62f;       // seconds until a strike has fully died away
    private const float VolleyStart = 0.10f;      // the opening volley waits for EffectManager's fade-in to get going, so it lands with some weight
    private const float VolleyStagger = 0.05f;    // gap between the four corner strikes
    private const float VolleySeconds = 0.45f;    // the volley owns the start; steady strikes take over after it

    // ---- how busy ----
    private const int SteadySlots = 6;   // independent strike "slots"; each fires roughly every 1.3-2.3s
    private const int MaxStrikes = 16;   // 4 volley + 6 slots (x2 for a tail overlapping the next cycle) fits comfortably
    private const int HumSlots = 10;     // small arcs popping along the edge
    private const int Racers = 3;        // currents running around the frame
    private const int TailSlices = 4;    // each racer's tail is drawn in this many fading pieces

    // Biggest bolt we ever build is 2^6 segments. Scratch arrays are sized once so Draw never allocates.
    private const int MaxPoints = 65;
    private const int MaxBranchPoints = 17;

    private uint _hot, _mid, _glow, _ink;
    private bool _paletteReady;

    private float _lastDrawTime = -100f;
    private float _castStart;

    // Border geometry, recomputed every frame (a handful of adds/multiplies - the window can be resized at any time).
    private float _w, _h, _m, _px, _ins, _cr, _len, _qa;
    private float _e0, _e1, _e2, _e3, _e4, _e5, _e6; // running distance at the end of each of the 8 border pieces

    private readonly Vector2[] _trunk = new Vector2[MaxPoints];
    private readonly Vector2[] _branch = new Vector2[MaxBranchPoints];
    private readonly Vector2[] _draw = new Vector2[MaxPoints + 1]; // the visible part of whatever is being drawn (+1 for a growing tip)
    private readonly float[] _off = new float[MaxPoints];
    private readonly Strike[] _strikes = new Strike[MaxStrikes];

    private struct Strike
    {
        public int Seed;     // everything about the strike's shape comes from this
        public int Corner;   // 0..3 for the opening volley (TL, TR, BR, BL); -1 for an ordinary strike
        public float Since;  // seconds since it began
        public float Weight; // how much it lights up the edges
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        // Too small to lay a frame out in.
        if (screenSize.X < 64f || screenSize.Y < 64f) return;

        // EffectManager stops calling Draw once the effect has fully faded out, so a gap since the last
        // call means the debuff was just (re)applied: restart from age zero (and the opening volley).
        if (time - _lastDrawTime > NewCastGapSeconds) _castStart = time;
        _lastDrawTime = time;
        float age = time - _castStart;
        int castSeed = unchecked((int)(_castStart * 1000f)); // a different storm each time you're hit

        EnsurePalette();
        SetupFrame(screenSize);

        int count = CollectStrikes(age, castSeed);

        // The edges light up with the strikes - summed from timing alone, before any geometry is built.
        float energy = 0f;
        for (int i = 0; i < count; i++)
            energy += _strikes[i].Weight * GlowEnvelope(_strikes[i].Since);

        DrawEdgeGlow(dl, alpha, MathF.Min(1f, energy));
        DrawHum(dl, age, alpha);
        DrawRacers(dl, age, alpha);
        for (int i = 0; i < count; i++)
            DrawStrike(dl, in _strikes[i], alpha);
    }

    // =====================================================================================
    // Strikes
    // =====================================================================================

    /// <summary>Works out which strikes are alive right now. Pure timing: no geometry is built here.</summary>
    private int CollectStrikes(float age, int castSeed)
    {
        int n = 0;

        // The opening volley: one strike from each corner, clockwise, a beat apart.
        if (age < VolleyStart + 3f * VolleyStagger + StrikeLife)
        {
            for (int c = 0; c < 4; c++)
            {
                float since = age - (VolleyStart + c * VolleyStagger);
                if (since >= 0f && since < StrikeLife)
                    _strikes[n++] = new Strike { Seed = Mix(castSeed, 1000 + c), Corner = c, Since = since, Weight = 1f };
            }
        }

        // Steady storm: each slot has its own rhythm and strikes once somewhere in each of its cycles.
        // A cycle's strike can outlive the cycle boundary, so the previous cycle is checked too.
        for (int s = 0; s < SteadySlots; s++)
        {
            float period = 1.3f + 0.95f * DrawHelpers.Hash01(Mix(s, 11));
            float phase = DrawHelpers.Hash01(Mix(s, 12)) * period;
            int k = (int)MathF.Floor((age - phase) / period);

            for (int c = k - 1; c <= k; c++)
            {
                if (c < 0 || n >= MaxStrikes) continue;
                int seed = Mix(Mix(castSeed, s), c);
                float start = phase + c * period + DrawHelpers.Hash01(seed + 3) * period * 0.45f;
                float since = age - start;
                if (since >= 0f && since < StrikeLife && start >= VolleySeconds)
                    _strikes[n++] = new Strike { Seed = seed, Corner = -1, Since = since, Weight = 0.75f };
            }
        }

        return n;
    }

    /// <summary>How brightly a strike lights the edges over its life: a quick rise, then a slower fall (peaks ~0.6 at about 0.09s).</summary>
    private static float GlowEnvelope(float since) =>
        (1f - MathF.Exp(-since / 0.04f)) * MathF.Exp(-since / 0.24f);

    private void DrawStrike(ImDrawListPtr dl, in Strike st, float alpha)
    {
        float since = st.Since;
        int seed = st.Seed;
        bool volley = st.Corner >= 0;

        // ---- brightness: full for an instant, then a flickering decay ----
        float env = since < 0.05f ? 1f : MathF.Exp(-(since - 0.05f) / 0.17f);
        int roll = since < 0.2f ? (int)(since / 0.05f) : 4 + (int)((since - 0.2f) / 0.09f); // the crackle tick
        float b = env * (0.66f + 0.34f * DrawHelpers.Hash01(Mix(seed, roll * 31 + 7)));
        if (b < 0.03f) return;
        int fine = Mix(seed, roll + 100); // re-rolled every tick: the fine jaggedness crackles while the overall path holds still
        float wScale = volley ? 1.3f : 1f;

        // ---- what kind of strike: 0 = an arc along the frame, 1 = a bolt in from the frame, 2 = a bolt out of a corner ----
        float pick = DrawHelpers.Hash01(seed + 5);
        int type = volley ? 2 : pick < 0.42f ? 0 : pick < 0.86f ? 1 : 2;

        float s0 = 0f, chord = 0f, reach = 0f;
        Vector2 anchorA, anchorB = default, inwardA, inwardB = default;
        int points;

        if (type == 0)
        {
            s0 = DrawHelpers.Hash01(seed + 8) * _len;
            chord = _m * DrawHelpers.HashRange(seed + 6, 0.17f, 0.40f) * (DrawHelpers.Hash01(seed + 7) < 0.5f ? -1f : 1f);
            float bulge = _m * DrawHelpers.HashRange(seed + 9, 0.030f, 0.075f);
            points = BuildArc(_trunk, s0, chord, bulge, 5, seed, fine, 0.10f);
            anchorA = PerimeterPoint(s0, out inwardA);
            anchorB = PerimeterPoint(s0 + chord, out inwardB);
        }
        else
        {
            int corner = volley ? st.Corner : (int)(DrawHelpers.Hash01(seed + 8) * 4f);
            s0 = type == 2 ? CornerDistance(corner) : DrawHelpers.Hash01(seed + 8) * _len;
            anchorA = PerimeterPoint(s0, out inwardA);

            float spread = type == 2 ? DrawHelpers.HashRange(seed + 6, -0.30f, 0.30f) : DrawHelpers.HashRange(seed + 6, -0.55f, 0.55f);
            float ang = MathF.Atan2(inwardA.Y, inwardA.X) + spread;
            var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));

            reach = _m * (type == 2
                ? (volley ? DrawHelpers.HashRange(seed + 10, 0.30f, 0.38f) : DrawHelpers.HashRange(seed + 10, 0.20f, 0.30f))
                : DrawHelpers.HashRange(seed + 10, 0.11f, 0.26f));
            points = BuildLine(_trunk, anchorA, dir, reach, reach > 0.19f * _m ? 5 : 4, seed, fine, 0.17f);
        }

        int segs = points - 1;
        float length = type == 0 ? MathF.Abs(chord) : reach;
        float growTime = 0.04f + 0.12f * length / _m;             // the leader shoots along the path in ~50-90ms
        float g = Saturate(since / growTime);

        // ---- forks: each appears when the leader reaches the point it leaves from ----
        int forks = volley ? 5 : type == 0 ? 2 + (DrawHelpers.Hash01(seed + 11) < 0.5f ? 1 : 0) : 3 + (DrawHelpers.Hash01(seed + 12) < 0.5f ? 1 : 0);
        for (int f = 0; f < forks; f++)
        {
            int fs = Mix(seed, 200 + f);
            int at = (int)(segs * DrawHelpers.HashRange(fs, 0.16f, 0.84f));
            float attach = growTime * at / segs;
            if (since < attach) continue;

            float side = DrawHelpers.Hash01(fs + 1) < 0.5f ? -1f : 1f;
            Vector2 fdir;
            float flen;
            if (type == 0)
            {
                // an arc's forks lash inward from wherever they leave it
                PerimeterPoint(s0 + chord * at / segs, out Vector2 inw);
                fdir = Rotate(inw, side * DrawHelpers.HashRange(fs + 2, 0f, 0.8f));
                flen = _m * DrawHelpers.HashRange(fs + 3, 0.05f, 0.13f);
            }
            else
            {
                Vector2 tangent = Normalize(_trunk[Math.Min(at + 1, segs)] - _trunk[Math.Max(at - 1, 0)]);
                fdir = Rotate(tangent, side * DrawHelpers.HashRange(fs + 2, 0.35f, 0.95f));
                flen = reach * DrawHelpers.HashRange(fs + 3, 0.24f, 0.50f);
            }

            int fpoints = BuildLine(_branch, _trunk[at], fdir, flen, 3, fs, Mix(fs, roll + 100), 0.20f);
            float gf = Saturate((since - attach) / (growTime * 0.7f + 0.02f));
            int visible = CopyVisible(_branch, fpoints, gf, _draw);
            Stroke(dl, _draw, visible, b * 0.78f, alpha, 0.55f * wScale, major: false);
        }

        // ---- the trunk, over its forks ----
        int shown = CopyVisible(_trunk, points, g, _draw);
        Stroke(dl, _draw, shown, b, alpha, wScale, major: true);
        if (g < 1f && shown >= 2)
            Flare(dl, _draw[shown - 1], 0.55f, alpha); // the leader's tip glows as it runs

        // ---- where it touches the frame: a flash and a spray of sparks ----
        float flash = Saturate(1f - since / 0.20f);
        if (flash > 0f)
        {
            Flare(dl, anchorA, flash, alpha);
            if (type == 0) Flare(dl, anchorB, flash * 0.85f, alpha);
        }
        if (since < 0.5f)
        {
            Sparks(dl, anchorA, inwardA, Mix(seed, 300), since, alpha);
            if (type == 0) Sparks(dl, anchorB, inwardB, Mix(seed, 310), since, alpha);
        }
    }

    // =====================================================================================
    // Between strikes: small arcs on the edge, and currents running around the frame
    // =====================================================================================

    /// <summary>Little arcs that pop up along the edge for a tenth of a second and vanish - the constant crackle under the big strikes.</summary>
    private void DrawHum(ImDrawListPtr dl, float age, float alpha)
    {
        for (int i = 0; i < HumSlots; i++)
        {
            float x = age * (7.5f + 4f * DrawHelpers.Hash01(Mix(i, 21))) + 10f * DrawHelpers.Hash01(Mix(i, 22));
            int epoch = (int)MathF.Floor(x);
            float u = x - epoch; // 0..1 through this arc's short life
            int seed = Mix(Mix(i, 23), epoch);
            if (DrawHelpers.Hash01(seed) > 0.62f) continue; // most of the time a slot is quiet

            float life = u < 0.12f ? u / 0.12f : 1f - (u - 0.12f) / 0.88f;
            float len = _m * DrawHelpers.HashRange(seed + 1, 0.05f, 0.14f) * (DrawHelpers.Hash01(seed + 2) < 0.5f ? -1f : 1f);
            float s0 = DrawHelpers.Hash01(seed + 3) * _len;
            float bulge = _m * DrawHelpers.HashRange(seed + 4, 0.004f, 0.018f);
            int points = BuildArc(_branch, s0, len, bulge, 3, seed, seed + 5, 0.07f);
            Stroke(dl, _branch, points, 0.55f * life, alpha, 0.5f, major: false);
        }
    }

    /// <summary>Bright heads with fading, jittering tails that run around the frame in both directions - motion between the strikes.</summary>
    private void DrawRacers(ImDrawListPtr dl, float age, float alpha)
    {
        float slice = _len * 0.022f;
        int tick = (int)(age * 30f);
        float fadeIn = Saturate(age / 0.4f);

        for (int j = 0; j < Racers; j++)
        {
            float dir = (j & 1) == 0 ? 1f : -1f;
            float speed = _len * (0.19f + 0.045f * j); // a lap in roughly 5, 4 and 3.5 seconds
            float head = DrawHelpers.Hash01(Mix(j, 31)) * _len + dir * speed * age;
            // each racer breathes on its own slow clock, so together they don't read as a constant outline
            float surge = fadeIn * (0.55f + 0.45f * DrawHelpers.Pulse(age, 2.3f + 0.7f * j, DrawHelpers.Hash01(Mix(j, 32))));

            for (int k = 0; k < TailSlices; k++)
            {
                float fade = k == 0 ? 1f : k == 1 ? 0.55f : k == 2 ? 0.28f : 0.12f;
                int points = BuildArc(_branch, head - dir * (k + 1) * slice, dir * slice, 0f, 2, Mix(j, 33), Mix(Mix(j, 34 + k), tick), 0.14f, coarseLevels: 0);
                Stroke(dl, _branch, points, 0.85f * fade * surge, alpha, 0.75f, major: false);
            }

            Vector2 tip = PerimeterPoint(head, out _);
            dl.AddCircleFilled(tip, 8f * _px, C(_glow, 0.22f * surge, alpha));
            dl.AddCircleFilled(tip, 2.6f * _px, C(_hot, 0.9f * surge, alpha));
        }
    }

    /// <summary>
    /// A warm glow along all four edges that follows the strikes. It sums the strikes' smooth envelopes
    /// (not their flicker) and is capped, so a burst of strikes reads as one glow swelling and settling,
    /// never as the whole border strobing. The fade goes to the SAME color at zero alpha - ImGui blends
    /// vertex colors without premultiplying, and fading to plain transparent black would turn it grey.
    /// </summary>
    private void DrawEdgeGlow(ImDrawListPtr dl, float alpha, float energy)
    {
        float a = MathF.Min(0.16f, alpha * (0.030f + 0.070f * energy));
        if (a <= 0.002f) return;

        float t = _m * 0.10f;
        uint edge = DrawHelpers.WithAlpha(_glow, a);
        uint clear = DrawHelpers.WithAlpha(_glow, 0f);
        dl.AddRectFilledMultiColor(new Vector2(0f, 0f), new Vector2(_w, t), edge, edge, clear, clear);           // top
        dl.AddRectFilledMultiColor(new Vector2(0f, _h - t), new Vector2(_w, _h), clear, clear, edge, edge);     // bottom
        dl.AddRectFilledMultiColor(new Vector2(0f, 0f), new Vector2(t, _h), edge, clear, clear, edge);          // left
        dl.AddRectFilledMultiColor(new Vector2(_w - t, 0f), new Vector2(_w, _h), clear, edge, edge, clear);     // right
    }

    // =====================================================================================
    // Bolts and how they're drawn
    // =====================================================================================

    /// <summary>
    /// Fills <see cref="_off"/> with a fractal sideways offset for each of a bolt's 2^levels + 1 points: the
    /// ends stay put, each level nudges every midpoint by a hashed amount, and the amount shrinks by
    /// <paramref name="rough"/> per level. Levels up to <paramref name="coarseLevels"/> use the fixed seed;
    /// finer ones use the re-rolling one.
    /// </summary>
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

    /// <summary>A jagged bolt that starts at <paramref name="p0"/> and heads along <paramref name="dir"/>. Returns the point count.</summary>
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

    /// <summary>
    /// A jagged arc that follows the frame from distance <paramref name="s0"/> for <paramref name="length"/>
    /// (negative = the other way), bowing inward by <paramref name="bulge"/> in the middle. It never
    /// wanders out past the screen edge. Returns the point count.
    /// </summary>
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

    /// <summary>The first <paramref name="g"/> (0..1) of a bolt's length, with a partial last segment, copied into <paramref name="dst"/>. Returns 0 if nothing is visible yet.</summary>
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

    /// <summary>
    /// One polyline, drawn as a stack of strokes: a dark under-stroke (so it stays visible on bright scenes),
    /// a wide faint haze, an orange glow, an amber body, and a hot core. Minor bolts skip the two outermost.
    /// </summary>
    private void Stroke(ImDrawListPtr dl, Vector2[] pts, int count, float b, float alpha, float widthScale, bool major)
    {
        if (count < 2 || b <= 0.01f) return;
        float w = _px * widthScale;
        ref Vector2 first = ref pts[0];
        if (major)
        {
            dl.AddPolyline(ref first, count, C(_ink, 0.24f * b, alpha), ImDrawFlags.None, 6.2f * w);
            dl.AddPolyline(ref first, count, C(_glow, 0.075f * b, alpha), ImDrawFlags.None, 17f * w);
        }
        dl.AddPolyline(ref first, count, C(_glow, 0.18f * b, alpha), ImDrawFlags.None, 8.5f * w);
        dl.AddPolyline(ref first, count, C(_mid, 0.60f * b, alpha), ImDrawFlags.None, 4.2f * w);
        dl.AddPolyline(ref first, count, C(_hot, 0.97f * b, alpha), ImDrawFlags.None, 1.8f * w);
    }

    private void Flare(ImDrawListPtr dl, Vector2 p, float k, float alpha)
    {
        dl.AddCircleFilled(p, (6f + 12f * k) * _px, C(_glow, 0.22f * k, alpha));
        dl.AddCircleFilled(p, (2.2f + 3.5f * k) * _px, C(_hot, 0.95f * k, alpha));
    }

    /// <summary>Five sparks thrown inward from a contact point, each with its own angle, speed and lifetime.</summary>
    private void Sparks(ImDrawListPtr dl, Vector2 from, Vector2 inward, int seed, float since, float alpha)
    {
        float baseAngle = MathF.Atan2(inward.Y, inward.X);
        for (int j = 0; j < 5; j++)
        {
            int s = Mix(seed, j);
            float life = DrawHelpers.HashRange(s, 0.20f, 0.44f);
            float t = since - DrawHelpers.HashRange(s + 1, 0f, 0.05f);
            if (t < 0f || t >= life) continue;

            float angle = baseAngle + DrawHelpers.HashRange(s + 2, -1.25f, 1.25f);
            var v = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (DrawHelpers.HashRange(s + 3, 170f, 430f) * _px);
            float k = 1f - t / life;
            Vector2 pos = from + v * (t * (1f - 0.45f * t / life)); // slows as it goes
            dl.AddLine(pos, pos - v * 0.032f, C(_hot, 0.9f * k * k, alpha), 1.5f * _px);
            dl.AddCircleFilled(pos, 1.5f * _px, C(_hot, 0.95f * k, alpha));
        }
    }

    // =====================================================================================
    // The border: a rounded rectangle you can walk along by distance
    // =====================================================================================

    private void SetupFrame(Vector2 size)
    {
        _w = size.X;
        _h = size.Y;
        _m = MathF.Min(_w, _h);
        _px = Math.Clamp(_m / 1080f, 0.75f, 2.4f); // line weights grow with resolution
        _ins = _m * 0.009f;                        // the frame sits just inside the screen edge so nothing is clipped

        float w2 = _w - 2f * _ins, h2 = _h - 2f * _ins;
        _cr = MathF.Min(_m * 0.055f, MathF.Min(w2, h2) * 0.5f);
        float straightW = MathF.Max(0f, w2 - 2f * _cr), straightH = MathF.Max(0f, h2 - 2f * _cr);
        _qa = MathF.PI * 0.5f * _cr; // one rounded corner's length

        _e0 = straightW;       // top edge ends
        _e1 = _e0 + _qa;       // top-right corner ends
        _e2 = _e1 + straightH; // right edge ends
        _e3 = _e2 + _qa;       // bottom-right corner ends
        _e4 = _e3 + straightW; // bottom edge ends
        _e5 = _e4 + _qa;       // bottom-left corner ends
        _e6 = _e5 + straightH; // left edge ends
        _len = _e6 + _qa;      // top-left corner ends: the whole loop
    }

    /// <summary>A point on the frame <paramref name="s"/> pixels along it (wraps in both directions), and the direction pointing into the screen there.</summary>
    private Vector2 PerimeterPoint(float s, out Vector2 inward)
    {
        s %= _len;
        if (s < 0f) s += _len;

        if (s < _e0) { inward = new Vector2(0f, 1f); return new Vector2(_ins + _cr + s, _ins); }                                               // top, heading right
        if (s < _e1) return RoundCorner(new Vector2(_w - _ins - _cr, _ins + _cr), -MathF.PI * 0.5f + (s - _e0) / _cr, out inward);             // top-right
        if (s < _e2) { inward = new Vector2(-1f, 0f); return new Vector2(_w - _ins, _ins + _cr + (s - _e1)); }                                 // right, heading down
        if (s < _e3) return RoundCorner(new Vector2(_w - _ins - _cr, _h - _ins - _cr), (s - _e2) / _cr, out inward);                           // bottom-right
        if (s < _e4) { inward = new Vector2(0f, -1f); return new Vector2(_w - _ins - _cr - (s - _e3), _h - _ins); }                            // bottom, heading left
        if (s < _e5) return RoundCorner(new Vector2(_ins + _cr, _h - _ins - _cr), MathF.PI * 0.5f + (s - _e4) / _cr, out inward);              // bottom-left
        if (s < _e6) { inward = new Vector2(1f, 0f); return new Vector2(_ins, _h - _ins - _cr - (s - _e5)); }                                  // left, heading up
        return RoundCorner(new Vector2(_ins + _cr, _ins + _cr), MathF.PI + (s - _e6) / _cr, out inward);                                       // top-left
    }

    private Vector2 RoundCorner(Vector2 centre, float angle, out Vector2 inward)
    {
        var outward = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        inward = -outward;
        return centre + outward * _cr;
    }

    /// <summary>The distance along the frame of the middle of a corner: 0 = top-left, 1 = top-right, 2 = bottom-right, 3 = bottom-left.</summary>
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
        _hot = DrawHelpers.ToU32(1.00f, 0.98f, 0.72f, 1f);  // yellow-white hot core
        _mid = DrawHelpers.ToU32(1.00f, 0.86f, 0.28f, 1f);  // amber body
        _glow = DrawHelpers.ToU32(1.00f, 0.62f, 0.10f, 1f); // warm orange haze
        _ink = DrawHelpers.ToU32(0.16f, 0.08f, 0.00f, 1f);  // dark under-stroke
        _paletteReady = true;
    }

    private static uint C(uint color, float amount, float alpha) => DrawHelpers.WithAlpha(color, amount * alpha);

    /// <summary>Combines two ints into a new seed. (DrawHelpers.Hash01 does the actual scrambling.)</summary>
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
