using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Amnesia: memory keeps almost forming and slipping away. A pale, uneven fog that never quite
/// settles, with a faint double hypno-spiral winding through it, question marks rising out of it,
/// and half-thoughts that trail off into nothing before they finish.
///
/// INTRO: a pale, soft flash of blankness, then the fog rolls in from the edges. The spiral
/// emerges along with the fog, question marks start surfacing shortly after, and the trailing
/// thought-fragments arrive last.
///
/// THE SPIRAL: two counter-positioned arms, each a continuous line from the exact center out past
/// the corners. 22 turns over the visible radius. The line width tapers linearly from 0 at the
/// center to a constant maximum at rN = 0.55, and a constant thickness (SwirlExtraWidthFrac) is
/// added to EVERY segment of the taper so the whole line is uniformly thicker. Since both arms
/// taper to 0 in a short ramp at the very center, they don't overlap there and no mesh artifact
/// appears.
///
/// THE FOG: the sub-circles drift and wobble on very slow frequencies so the haze reads as a
/// slow, breathing mass. The spiral reveal modulates each dot's alpha moderately and its size
/// only slightly - enough for the swirl to shine through the fog, but not so much that
/// individual fog puffs visibly pulse.
///
/// STEADY STATE, back to front:
///   1. pale blue-gray vignette with an uneven waver
///   2. double hypno-spiral (faint, rotating, ~22 turns, tapered)
///   3. fog - rectangular-edge-anchored plus interior blobs, sub-circle scatter, spiral-modulated
///   4. drifting question marks
///   5. trailing thought fragments
/// </summary>
public sealed class AmnesiaEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Amnesia;

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f;

    // ---- palette: pale, cold-lavender gray ----
    private static readonly uint FogDeep  = DrawHelpers.ToU32(0.42f, 0.40f, 0.50f, 1f);
    private static readonly uint FogMid   = DrawHelpers.ToU32(0.68f, 0.66f, 0.76f, 1f);
    private static readonly uint FogLight = DrawHelpers.ToU32(0.86f, 0.85f, 0.94f, 1f);
    private static readonly uint MarkCol  = DrawHelpers.ToU32(0.94f, 0.93f, 0.98f, 1f);
    private static readonly uint FragCol  = DrawHelpers.ToU32(0.90f, 0.88f, 0.96f, 1f);

    // ---- hypno-spiral geometry ----
    private const float SwirlTurns      = 22f;
    private const float SwirlWind       = SwirlTurns * 2f * MathF.PI;
    private const float SwirlPhaseSpeed = 0.45f;

    private const float SwirlOuterR = 1.30f;

    // ---- spiral width ----
    // The width at parameter t is:
    //     (SwirlBaseWidthFrac * WidthMul(t) + SwirlExtraWidthFrac) * shortSide * RampIn(t)
    //
    // WidthMul ramps linearly from 0 at t=0 to 2 at t=SwirlFullWidthRN, then stays at 2.
    // SwirlExtraWidthFrac is the "equal size increase for all parts" - the same absolute width
    // added to every segment beyond the taper.
    // RampIn brings the total width from 0 up to full over the first ~5% of the arm length, so
    // the two arms don't overlap where they meet at the exact center.
    private const float SwirlBaseWidthFrac   = 0.007f;
    private const float SwirlExtraWidthFrac  = 0.003f;
    private const float SwirlFullWidthRN     = 0.55f;
    private const float SwirlAlpha           = 0.038f;

    // ---- question mark variants ----
    private static readonly string[] MarkGlyphs = { "?", "?", "?", "??", "??", "?!", "???" };

    // ---- trailing thought fragments ----
    private static readonly string[] Fragments =
    {
        "where...", "who was...", "what...", "when...", "I can't...",
        "remem—", "why...", "was I...", "how...", "who...",
        "wait...", "I forgot...", "where was I...", "something...",
        "I was...", "did I...", "I should...", "I need to...",
        "this isn't...", "that's not...", "I don't...", "what was I...",
        "hold on...", "so close...", "almost...", "it's gone...",
    };

    private const float FragFadeInFrac  = 0.25f;
    private const float FragFadeOutFrac = 0.30f;

    // ---- fog ----
    private const int FogBlobCount  = 22;
    private const int FogSubCircles = 9;

    private struct FogBlob
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

    private readonly FogBlob[] _fog = new FogBlob[FogBlobCount];

    // ---- particles ----
    private readonly EdgeParticleField _marks     = new(maxParticles: 14, seedSalt: 0x100001);
    private readonly EdgeParticleField _fragments = new(maxParticles: 6,  seedSalt: 0x100003);

    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _markPos;
    private readonly Func<int, Vector2> _markVel;
    private readonly Func<int, string>  _pickMark;
    private readonly Func<int, Vector2> _fragPos;
    private readonly Func<int, Vector2> _fragVel;
    private readonly Func<int, string>  _pickFragment;

    // ---- intro state ----
    private float _lastDrawTime = -100f;
    private float _castStart;

    public AmnesiaEffect()
    {
        _markPos      = MarkSpawnPos;
        _markVel      = MarkSpawnVel;
        _pickMark     = PickMark;
        _fragPos      = FragmentSpawnPos;
        _fragVel      = FragmentSpawnVel;
        _pickFragment = PickFragment;

        // ---- bake fog ----
        for (int i = 0; i < FogBlobCount; i++)
        {
            int s = unchecked(0x1A4E00 + i * 7919);

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

            _fog[i] = new FogBlob
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
                Alpha    = DrawHelpers.HashRange(s + 10, 0.05f, 0.12f),
                Lit      = DrawHelpers.Hash01(s + 11) < 0.40f,
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

        if (time - _lastDrawTime > NewCastGapSeconds) _castStart = time;
        _lastDrawTime = time;
        float age = time - _castStart;

        float introFlash = age < 0.40f ? 1f - age / 0.40f : 0f;
        float vigFade    = Math.Clamp(age / 0.80f, 0f, 1f);
        float fogFade    = Math.Clamp((age - 0.10f) / 0.80f, 0f, 1f);

        float waver = 0.70f + 0.30f * DrawHelpers.Pulse(time, 5.2f) * DrawHelpers.Pulse(time, 3.1f, 0.35f);

        Vector2 center = new(screenSize.X * 0.5f, screenSize.Y * 0.5f);

        // 1) pale vignette.
        float vigT = 0.15f + 0.03f * waver;
        float vigA = vigFade * (0.55f + 0.20f * waver);
        DrawHelpers.DrawVignette(dl, screenSize, FogDeep, vigT, alpha * vigA);

        // 1b) intro flash.
        if (introFlash > 0.001f)
        {
            dl.AddRectFilled(DrawHelpers.V(0, 0), screenSize,
                DrawHelpers.WithAlpha(FogLight, alpha * introFlash * 0.35f));
        }

        // 2) hypno-spiral.
        if (fogFade > 0.001f)
            DrawSwirlLayer(dl, center, shortSide, time, alpha * fogFade);

        // 3) fog
        if (fogFade > 0.001f)
            DrawFog(dl, screenSize, center, shortSide, time, alpha * fogFade);

        // 4) question marks
        if (age > 0.50f)
        {
            _marks.Update(
                time, dt,
                spawnIntervalMin: 0.60f, spawnIntervalMax: 1.40f,
                spawnPos: _markPos, spawnVelocity: _markVel,
                pickGlyph: _pickMark,
                lifespanMin: 3.0f, lifespanMax: 5.0f,
                sizeMin: 24f, sizeMax: 42f);

            _marks.DrawGlyphs(dl, time, MarkCol, alpha, glow: 0.9f);
        }

        // 5) trailing thought fragments
        if (age > 1.00f)
        {
            _fragments.Update(
                time, dt,
                spawnIntervalMin: 1.40f, spawnIntervalMax: 2.80f,
                spawnPos: _fragPos, spawnVelocity: _fragVel,
                pickGlyph: _pickFragment,
                lifespanMin: 4.0f, lifespanMax: 6.0f,
                sizeMin: 18f, sizeMax: 28f);

            for (int i = 0; i < _fragments.Count; i++)
                DrawFragment(dl, in _fragments[i], alpha, time);
        }
    }

    // =====================================================================================
    // Hypno spiral
    // =====================================================================================

    private static float SwirlAt(Vector2 p, Vector2 center, float time, float shortSide)
    {
        float dx = p.X - center.X;
        float dy = p.Y - center.Y;

        float r = MathF.Sqrt(dx * dx + dy * dy);
        float rN = r / shortSide;

        float theta = MathF.Atan2(dy, dx);
        float phase = time * SwirlPhaseSpeed;

        float c0 = MathF.Cos(theta - rN * SwirlWind + phase);
        float c1 = MathF.Cos(theta - MathF.PI - rN * SwirlWind + phase);

        float s0 = MathF.Max(0f, c0);
        s0 = s0 * s0 * s0 * s0;
        float s1 = MathF.Max(0f, c1);
        s1 = s1 * s1 * s1 * s1;

        return MathF.Max(s0, s1);
    }

    /// <summary>
    /// Linear taper from 0 at rN = 0 to 2 at rN = SwirlFullWidthRN, then constant.
    /// </summary>
    private static float WidthMul(float rN)
    {
        return MathF.Min(1f, rN / SwirlFullWidthRN) * 2f;
    }

    /// <summary>
    /// Short ramp that brings the width from 0 up to full over the first few percent of the arm.
    /// Prevents the two arms' initial segments from overlapping at the exact center.
    /// </summary>
    private static float RampIn(float t)
    {
        return MathF.Min(1f, t * 20f);
    }

    private static void DrawSwirlLayer(ImDrawListPtr dl, Vector2 center, float shortSide, float time, float alpha)
    {
        if (alpha <= 0.003f) return;

        float maxR = shortSide * SwirlOuterR;
        float phase = time * SwirlPhaseSpeed;
        float baseWidth  = shortSide * SwirlBaseWidthFrac;
        float extraWidth = shortSide * SwirlExtraWidthFrac;
        uint col = DrawHelpers.WithAlpha(FogLight, alpha * SwirlAlpha);

        DrawSwirlArm(dl, center, shortSide, phase, 0f,        maxR, baseWidth, extraWidth, col);
        DrawSwirlArm(dl, center, shortSide, phase, MathF.PI,  maxR, baseWidth, extraWidth, col);
    }

    private static void DrawSwirlArm(ImDrawListPtr dl, Vector2 center, float shortSide, float phase,
                                     float armOffset, float maxR, float baseWidth, float extraWidth, uint col)
    {
        const int samples = 1200;

        Span<Vector2> points = stackalloc Vector2[samples];

        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / (samples - 1);
            float r = maxR * t;
            float rN = r / shortSide;

            float theta = armOffset + rN * SwirlWind - phase;
            points[i] = center + new Vector2(MathF.Cos(theta) * r, MathF.Sin(theta) * r);
        }

        Span<Vector2> quad = stackalloc Vector2[4];

        for (int i = 0; i < samples - 1; i++)
        {
            Vector2 p0 = points[i];
            Vector2 p1 = points[i + 1];

            Vector2 d = p1 - p0;
            float len = d.Length();
            if (len < 0.05f) continue;
            Vector2 dir = d / len;
            Vector2 perp = new(-dir.Y, dir.X);

            float t0 = (float)i / (samples - 1);
            float t1 = (float)(i + 1) / (samples - 1);

            // Width = (taper + constant extra) * center ramp.
            float w0 = (baseWidth * WidthMul(t0) + extraWidth) * RampIn(t0);
            float w1 = (baseWidth * WidthMul(t1) + extraWidth) * RampIn(t1);

            if (w0 < 0.08f && w1 < 0.08f) continue;

            quad[0] = p0 - perp * (w0 * 0.5f);
            quad[1] = p1 - perp * (w1 * 0.5f);
            quad[2] = p1 + perp * (w1 * 0.5f);
            quad[3] = p0 + perp * (w0 * 0.5f);

            dl.AddConvexPolyFilled(ref quad[0], 4, col);
        }
    }

    // =====================================================================================
    // Fog
    // =====================================================================================

    private void DrawFog(ImDrawListPtr dl, Vector2 screenSize, Vector2 center, float shortSide,
                         float time, float alpha)
    {
        for (int i = 0; i < FogBlobCount; i++)
        {
            ref readonly var f = ref _fog[i];

            float dx = MathF.Sin(time * f.FreqX + f.PhaseX) * shortSide * f.DriftX;
            float dy = MathF.Cos(time * f.FreqY + f.PhaseY) * shortSide * f.DriftY;

            Vector2 p = new(f.BaseX * screenSize.X + dx, f.BaseY * screenSize.Y + dy);
            float r = shortSide * f.SizeFrac;

            uint col = f.Lit ? FogLight : FogMid;

            for (int k = 0; k < FogSubCircles; k++)
            {
                int ks = unchecked(f.Seed + 100 + k * 71);
                float ox = DrawHelpers.HashRange(ks,     -1f, 1f) * r * 0.95f;
                float oy = DrawHelpers.HashRange(ks + 1, -1f, 1f) * r * 0.95f;
                float sr = r * DrawHelpers.HashRange(ks + 2, 0.26f, 0.58f);

                float wfX = DrawHelpers.HashRange(ks + 3, 0.10f, 0.25f);
                float wfY = DrawHelpers.HashRange(ks + 5, 0.10f, 0.25f);
                float wpX = DrawHelpers.HashRange(ks + 4, 0f, MathF.PI * 2f);
                float wpY = DrawHelpers.HashRange(ks + 6, 0f, MathF.PI * 2f);

                float wobX = MathF.Sin(time * wfX + wpX) * sr * 0.45f;
                float wobY = MathF.Cos(time * wfY + wpY) * sr * 0.45f;

                Vector2 sp = p + new Vector2(ox + wobX, oy + wobY);

                float swirl = SwirlAt(sp, center, time, shortSide);
                float alphaMul = 1f + swirl * 0.90f;
                float sizeMul  = 1f + swirl * 0.06f;

                uint c2 = swirl > 0.05f
                    ? DrawHelpers.LerpColor(col, FogLight, swirl * 0.5f)
                    : col;

                dl.AddCircleFilled(
                    sp,
                    sr * sizeMul,
                    DrawHelpers.WithAlpha(c2, alpha * f.Alpha * 0.45f * alphaMul));
            }
        }
    }

    // =====================================================================================
    // Trailing thought fragments
    // =====================================================================================

    private static void DrawFragment(ImDrawListPtr dl, in EdgeParticleField.Particle p, float alpha, float time)
    {
        float age = time - p.Born;
        if (age < 0f || age >= p.Lifespan) return;
        float t01 = age / p.Lifespan;

        float fadeIn  = DrawHelpers.EaseOutCubic(t01 / FragFadeInFrac);
        float fadeOut = 1f - DrawHelpers.EaseOutCubic((t01 - (1f - FragFadeOutFrac)) / FragFadeOutFrac);
        float envelope = MathF.Min(fadeIn, fadeOut);
        if (envelope <= 0.003f) return;

        int h = unchecked((int)(p.Born * 10007f));
        float sway = MathF.Sin(age * 0.45f + DrawHelpers.HashRange(h + 1, 0f, MathF.PI * 2f)) * 8f;

        var pos = p.Pos + new Vector2(sway, 0);
        float a = alpha * envelope;

        DrawHelpers.DrawGlowText(dl, pos, p.Glyph,
            DrawHelpers.WithAlpha(FragCol, a * 0.85f),
            p.Size, glow: 0.85f);
    }

    // =====================================================================================
    // Spawn helpers
    // =====================================================================================

    private string PickMark(int seed)
    {
        int i = (int)(DrawHelpers.Hash01(seed) * MarkGlyphs.Length);
        if (i >= MarkGlyphs.Length) i = MarkGlyphs.Length - 1;
        return MarkGlyphs[i];
    }

    private string PickFragment(int seed)
    {
        int i = (int)(DrawHelpers.Hash01(seed) * Fragments.Length);
        if (i >= Fragments.Length) i = Fragments.Length - 1;
        return Fragments[i];
    }

    private Vector2 MarkSpawnPos(int seed)
    {
        float x = DrawHelpers.HashRange(seed, 0.08f, 0.92f) * _screenSize.X;
        float y = DrawHelpers.HashRange(seed + 1, 0.55f, 0.88f) * _screenSize.Y;
        return new Vector2(x, y);
    }

    private Vector2 MarkSpawnVel(int seed) => new(
        DrawHelpers.HashRange(seed + 2, -8f, 8f),
        DrawHelpers.HashRange(seed + 3, -26f, -12f));

    private Vector2 FragmentSpawnPos(int seed)
    {
        float x = DrawHelpers.HashRange(seed, 0.10f, 0.90f) * _screenSize.X;
        float y = DrawHelpers.HashRange(seed + 1, 0.45f, 0.85f) * _screenSize.Y;
        return new Vector2(x, y);
    }

    private Vector2 FragmentSpawnVel(int seed) => new(
        DrawHelpers.HashRange(seed + 2, -4f, 4f),
        DrawHelpers.HashRange(seed + 3, -12f, -5f));
}