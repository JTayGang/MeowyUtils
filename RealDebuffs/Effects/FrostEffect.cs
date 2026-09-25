using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Frost: the cold is closing in. A sudden blue-white flash marks the moment the chill takes
/// hold, then the field fills with two kinds of ice:
///
/// EDGE CRYSTALS (tall, edge-anchored): the classic frost spikes growing inward from the screen
/// edges. Three sub-types - needle, forked, fern - give the border its silhouette.
///
/// SNOWFLAKES (short, ring-biased): 6-armed crystalline snowflakes concentrated toward the edges
/// but present throughout the frame. Each one slowly DRIFTS around its home position (a soft
/// Lissajous wander), slowly ROTATES, and is shaped slightly differently from its neighbours -
/// three branch-pair styles, per-flake sub-branch angle, and per-flake size all vary, so no two
/// are identical.
/// </summary>
public sealed class FrostEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Frost;

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f;

    // ---- palette: cold blue-white ----
    private static readonly uint DeepCold = DrawHelpers.ToU32(0.06f, 0.16f, 0.32f, 1f);
    private static readonly uint MidIce   = DrawHelpers.ToU32(0.42f, 0.68f, 0.90f, 1f);
    private static readonly uint Bright   = DrawHelpers.ToU32(0.82f, 0.94f, 1.00f, 1f);
    private static readonly uint Hot      = DrawHelpers.ToU32(0.98f, 1.00f, 1.00f, 1f);

    // ---- crystal field ----
    private const int CrystalCount = 130;

    private const byte TypeNeedle    = 0;
    private const byte TypeForked    = 1;
    private const byte TypeFern      = 2;
    private const byte TypeSnowflake = 3;

    private const byte TierFar  = 0;
    private const byte TierNear = 1;

    private struct Crystal
    {
        // Edge crystals use Edge/Along. Snowflakes use PosX/PosY. Both use the rest.
        public byte  Edge;
        public float Along;
        public float PosX, PosY;

        // Snowflake-only: in-place rotation, and drift/wander around the home position.
        public float Rotation;
        public float RotSpeed;
        public float DriftAmpX, DriftAmpY;    // shortSide fraction
        public float DriftFreqX, DriftFreqY;  // Hz
        public float DriftPhaseX, DriftPhaseY;

        // Snowflake-only: shape variation.
        public byte  Style;              // 0/1/2 branch-pair style
        public float SubBranchAngle;     // radians off the main arm

        public float Length;
        public float Width;
        public float AngleJitter;
        public float GrowDelay;
        public byte  Type;
        public byte  DepthTier;
        public float Hue;
        public int   Seed;
    }

    private readonly Crystal[] _crystals = new Crystal[CrystalCount];

    // ---- snow particles ----
    private readonly EdgeParticleField _ice = new(maxParticles: 110, seedSalt: 0xF057);

    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _icePos;
    private readonly Func<int, Vector2> _iceVel;
    private readonly Func<int, string>  _noGlyph;

    // ---- intro ----
    private float _lastDrawTime = -100f;
    private float _castStart;

    public FrostEffect()
    {
        _icePos = IceSpawnPos;
        _iceVel = IceSpawnVel;
        _noGlyph = static _ => "";

        for (int i = 0; i < CrystalCount; i++)
        {
            int s = unchecked(0xF00500 + i * 7919);

            float typeRoll = DrawHelpers.Hash01(s + 11);
            byte type = typeRoll < 0.22f ? TypeNeedle
                      : typeRoll < 0.44f ? TypeForked
                      : typeRoll < 0.64f ? TypeFern
                      :                    TypeSnowflake;

            byte tier = DrawHelpers.Hash01(s + 10) < 0.60f ? TierFar : TierNear;

            float lenBase = DrawHelpers.HashRange(s + 2, 0.045f, 0.145f);
            float widBase = DrawHelpers.HashRange(s + 3, 0.0018f, 0.0042f);

            if (tier == TierFar)
            {
                lenBase *= 0.70f;
                widBase *= 0.82f;
            }
            else
            {
                lenBase *= 1.15f;
                widBase *= 1.18f;
            }

            if (type == TypeSnowflake)
            {
                lenBase = DrawHelpers.HashRange(s + 6, 0.020f, 0.045f) * (tier == TierFar ? 0.75f : 1.10f);
                widBase = DrawHelpers.HashRange(s + 7, 0.0009f, 0.0018f) * (tier == TierFar ? 0.85f : 1.05f);

                // ---- EDGE-BIASED POLAR PLACEMENT ----
                // Radius ranges from 0.35 (just outside the frame's inner third) to 1.0 (the edge
                // itself). The exponent 0.40 pushes the distribution firmly toward the outer band
                // while still leaving some snowflakes in the middle of the frame.
                float ang = DrawHelpers.HashRange(s + 8, 0f, MathF.PI * 2f);
                float radT = DrawHelpers.Hash01(s + 9);
                float radius = 0.35f + 0.65f * MathF.Pow(radT, 0.40f);

                // ---- shape style: three branch-pair configurations ----
                // 0: classic 2 pairs (denser look)
                // 1: sparse single pair (simple, minimal snowflake)
                // 2: rich 3 pairs (fern-like, detailed)
                float styleRoll = DrawHelpers.Hash01(s + 15);
                byte style = styleRoll < 0.45f ? (byte)0
                           : styleRoll < 0.75f ? (byte)1
                           :                     (byte)2;

                _crystals[i] = new Crystal
                {
                    Edge            = 0,
                    Along           = 0f,
                    PosX            = 0.5f + MathF.Cos(ang) * 0.5f * radius,
                    PosY            = 0.5f + MathF.Sin(ang) * 0.5f * radius,
                    Rotation        = DrawHelpers.HashRange(s + 13, 0f, MathF.PI * 2f),
                    RotSpeed        = DrawHelpers.HashRange(s + 14, -0.22f, 0.22f),

                    // Drift amplitude/frequency/phase: each flake wanders on its own slow Lissajous.
                    DriftAmpX       = DrawHelpers.HashRange(s + 16, 0.010f, 0.030f),
                    DriftAmpY       = DrawHelpers.HashRange(s + 17, 0.008f, 0.024f),
                    DriftFreqX      = DrawHelpers.HashRange(s + 18, 0.08f, 0.20f),
                    DriftFreqY      = DrawHelpers.HashRange(s + 19, 0.06f, 0.16f),
                    DriftPhaseX     = DrawHelpers.HashRange(s + 20, 0f, MathF.PI * 2f),
                    DriftPhaseY     = DrawHelpers.HashRange(s + 21, 0f, MathF.PI * 2f),

                    Style           = style,
                    SubBranchAngle  = DrawHelpers.HashRange(s + 22, 0.85f, 1.20f),

                    Length          = lenBase,
                    Width           = widBase,
                    AngleJitter     = 0f,
                    GrowDelay       = DrawHelpers.HashRange(s + 5, 0.20f, 1.40f),
                    Type            = type,
                    DepthTier       = tier,
                    Hue             = DrawHelpers.HashRange(s + 12, -1f, 1f),
                    Seed            = s,
                };
            }
            else
            {
                float roll = DrawHelpers.Hash01(s);
                byte edge = roll < 0.28f ? (byte)0
                          : roll < 0.55f ? (byte)2
                          : roll < 0.78f ? (byte)1
                          : (byte)3;

                switch (type)
                {
                    case TypeNeedle:
                        widBase *= 0.55f;
                        lenBase *= 1.10f;
                        break;
                    case TypeFern:
                        lenBase *= 0.90f;
                        break;
                }

                _crystals[i] = new Crystal
                {
                    Edge            = edge,
                    Along           = DrawHelpers.HashRange(s + 1, 0.02f, 0.98f),
                    PosX            = 0f,
                    PosY            = 0f,
                    Rotation        = 0f,
                    RotSpeed        = 0f,
                    DriftAmpX       = 0f,
                    DriftAmpY       = 0f,
                    DriftFreqX      = 0f,
                    DriftFreqY      = 0f,
                    DriftPhaseX     = 0f,
                    DriftPhaseY     = 0f,
                    Style           = 0,
                    SubBranchAngle  = 1.0f,
                    Length          = lenBase,
                    Width           = widBase,
                    AngleJitter     = DrawHelpers.HashRange(s + 4, -0.55f, 0.55f),
                    GrowDelay       = DrawHelpers.HashRange(s + 5, 0.15f, 1.15f),
                    Type            = type,
                    DepthTier       = tier,
                    Hue             = DrawHelpers.HashRange(s + 12, -1f, 1f),
                    Seed            = s,
                };
            }
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

        float flash     = IntroFlash(age);
        float baseAlpha = Math.Clamp(age / 0.70f, 0f, 1f);

        float breath = DrawHelpers.Pulse(time, 5.5f);
        float shiver = ShiverPulse(time);

        // 1) flat cold wash (+ intro flash)
        float washA = baseAlpha * (0.14f + 0.03f * breath + 0.04f * shiver);
        dl.AddRectFilled(DrawHelpers.V(0, 0), screenSize,
            DrawHelpers.WithAlpha(DeepCold, alpha * washA));

        if (flash > 0f)
            dl.AddRectFilled(DrawHelpers.V(0, 0), screenSize,
                DrawHelpers.WithAlpha(Hot, alpha * flash * 0.40f));

        // 2) vignette
        float vigT = 0.16f + 0.04f * breath;
        float vigA = baseAlpha * (0.62f + 0.14f * breath + 0.10f * shiver);
        DrawHelpers.DrawVignette(dl, screenSize, DeepCold, vigT, alpha * vigA);

        // 3) crystals - two passes so far-tier always sits behind near-tier.
        for (int i = 0; i < CrystalCount; i++)
            if (_crystals[i].DepthTier == TierFar)
                DrawCrystal(dl, in _crystals[i], screenSize, shortSide, alpha, age, time, shiver);

        for (int i = 0; i < CrystalCount; i++)
            if (_crystals[i].DepthTier == TierNear)
                DrawCrystal(dl, in _crystals[i], screenSize, shortSide, alpha, age, time, shiver);

        // 4) snow particles
        if (age > 0.80f)
        {
            _ice.Update(
                time, dt,
                spawnIntervalMin: 0.03f, spawnIntervalMax: 0.10f,
                spawnPos: _icePos, spawnVelocity: _iceVel,
                pickGlyph: _noGlyph,
                lifespanMin: 3.5f, lifespanMax: 7.0f,
                sizeMin: 0.9f, sizeMax: 4.2f);

            DrawIce(dl, alpha, time);
        }
    }

    // =====================================================================================
    // Intro + shiver
    // =====================================================================================

    private static float IntroFlash(float age)
    {
        if (age < 0f || age > 0.35f) return 0f;
        float t = age / 0.35f;
        if (t < 0.20f) return t / 0.20f;
        return 1f - (t - 0.20f) / 0.80f;
    }

    private static float ShiverPulse(float time)
    {
        const float period = 6.0f;
        float p = (time % period) / period;
        return Bump(p, 0.15f, 0.05f) + 0.5f * Bump(p, 0.35f, 0.06f);
    }

    private static float Bump(float x, float center, float width)
    {
        float d = (x - center) / width;
        return MathF.Exp(-d * d);
    }

    // =====================================================================================
    // Crystal dispatcher
    // =====================================================================================

    private static void DrawCrystal(ImDrawListPtr dl, in Crystal c, Vector2 screenSize,
                                    float shortSide, float alpha, float age, float time, float shiver)
    {
        float local = age - c.GrowDelay;
        if (local <= 0f) return;

        float grow = Math.Clamp(local / 0.55f, 0f, 1f);
        float eased = 1f - (1f - grow) * (1f - grow);
        if (eased <= 0.01f) return;

        float tierAlpha = c.DepthTier == TierFar ? 0.62f : 1.00f;

        float glint = 0.85f + 0.15f * DrawHelpers.Pulse(
            time,
            2.2f + (c.Seed & 0x7) * 0.13f,
            (c.Seed & 0x1F) * 0.17f);

        float a = alpha * glint * (0.85f + 0.15f * shiver) * tierAlpha;
        if (a < 0.01f) return;

        uint deep   = DeepCold;
        uint mid    = Shift(MidIce, DeepCold, Hot, c.Hue);
        uint bright = Shift(Bright, MidIce,   Hot, c.Hue);
        uint hot    = Shift(Hot,    Bright,   Hot, c.Hue);

        float len = shortSide * c.Length * eased;
        float wid = shortSide * c.Width;

        if (c.Type == TypeSnowflake)
        {
            DrawSnowflake(dl, in c, screenSize, shortSide, len, wid, a, deep, mid, bright, hot, time);
            return;
        }

        // ---- edge crystal (needle / forked / fern) ----
        Vector2 root = c.Edge switch
        {
            0 => new Vector2(c.Along * screenSize.X, 0f),
            1 => new Vector2(screenSize.X, c.Along * screenSize.Y),
            2 => new Vector2(c.Along * screenSize.X, screenSize.Y),
            _ => new Vector2(0f, c.Along * screenSize.Y),
        };
        Vector2 inward = c.Edge switch
        {
            0 => new Vector2(0f, 1f),
            1 => new Vector2(-1f, 0f),
            2 => new Vector2(0f, -1f),
            _ => new Vector2(1f, 0f),
        };

        float ca = MathF.Cos(c.AngleJitter), sa = MathF.Sin(c.AngleJitter);
        Vector2 dir = new(inward.X * ca - inward.Y * sa, inward.X * sa + inward.Y * ca);

        Vector2 tip = root + dir * len;

        if (c.DepthTier == TierFar)
            dl.AddLine(root, tip, DrawHelpers.WithAlpha(deep, a * 0.18f), wid * 7f);

        dl.AddLine(root, tip, DrawHelpers.WithAlpha(deep,   a * 0.40f), wid * 2.4f);
        dl.AddLine(root, tip, DrawHelpers.WithAlpha(mid,    a * 0.85f), wid * 1.1f);
        dl.AddLine(root, tip, DrawHelpers.WithAlpha(bright, a * 0.95f), wid * 0.5f);
        dl.AddLine(root, tip, DrawHelpers.WithAlpha(hot,    a * 0.70f), wid * 0.30f);

        switch (c.Type)
        {
            case TypeNeedle:
                break;
            case TypeForked:
                DrawForkBranches(dl, root, tip, dir, len, wid, a, deep, mid, bright, hot, c.Seed);
                break;
            case TypeFern:
                DrawFernBranches(dl, root, tip, dir, len, wid, a, deep, mid, bright, hot, c.Seed);
                break;
        }

        dl.AddCircleFilled(tip, wid * 0.85f, DrawHelpers.WithAlpha(bright, a * 0.85f));
        dl.AddCircleFilled(tip, wid * 0.40f, DrawHelpers.WithAlpha(hot,    a * 0.95f));
    }

    // =====================================================================================
    // Snowflake
    // =====================================================================================

    /// <summary>
    /// A 6-armed snowflake drawn as four layered passes. The flake's home position is its baked
    /// PosX/PosY, and it DRIFTS around that home on a slow 2D Lissajous - the same technique used
    /// for the frost haze in other effects - so the whole field gently wanders without any flake
    /// ever accumulating drift.
    ///
    /// Shape varies per flake:
    ///   - Style 0: two branch pairs per arm (classic)
    ///   - Style 1: one branch pair per arm (sparse, minimal)
    ///   - Style 2: three branch pairs per arm (rich, fern-like)
    ///   - SubBranchAngle: the angle each branch leaves the arm at, varying from ~49 degrees to
    ///     ~69 degrees, so some flakes have tight Vs and some have wide ones.
    /// </summary>
    private static void DrawSnowflake(ImDrawListPtr dl, in Crystal c, Vector2 screenSize,
                                      float shortSide, float armLen, float armWid, float a,
                                      uint deep, uint mid, uint bright, uint hot, float time)
    {
        // Drift around home.
        float driftX = MathF.Sin(time * c.DriftFreqX * MathF.PI * 2f + c.DriftPhaseX) * shortSide * c.DriftAmpX;
        float driftY = MathF.Cos(time * c.DriftFreqY * MathF.PI * 2f + c.DriftPhaseY) * shortSide * c.DriftAmpY;
        Vector2 center = new(c.PosX * screenSize.X + driftX, c.PosY * screenSize.Y + driftY);

        float rot = c.Rotation + time * c.RotSpeed;

        // Map style byte to (branch pairs, and a small multiplier for the branches' angle jitter).
        int pairs;
        switch (c.Style)
        {
            case 1: pairs = 1; break;
            case 2: pairs = 3; break;
            default: pairs = 2; break;
        }

        // Far-tier halo.
        if (c.DepthTier == TierFar)
            dl.AddCircleFilled(center, armLen * 1.6f, DrawHelpers.WithAlpha(deep, a * 0.10f));

        // Four layered silhouette passes.
        DrawSnowflakeSilhouette(dl, center, rot, armLen, armWid * 2.6f, a * 0.28f, deep,  pairs, c.SubBranchAngle);
        DrawSnowflakeSilhouette(dl, center, rot, armLen, armWid * 1.3f, a * 0.80f, mid,   pairs, c.SubBranchAngle);
        DrawSnowflakeSilhouette(dl, center, rot, armLen, armWid * 0.65f, a * 0.95f, bright, pairs, c.SubBranchAngle);
        DrawSnowflakeSilhouette(dl, center, rot, armLen, armWid * 0.30f, a * 0.75f, hot,    pairs, c.SubBranchAngle);

        // Bright center hex + hot dot.
        dl.AddCircleFilled(center, armWid * 2.4f, DrawHelpers.WithAlpha(bright, a * 0.55f));
        dl.AddCircleFilled(center, armWid * 1.2f, DrawHelpers.WithAlpha(bright, a * 0.90f));
        dl.AddCircleFilled(center, armWid * 0.55f, DrawHelpers.WithAlpha(hot,   a * 0.95f));

        // Glints at every arm tip.
        for (int i = 0; i < 6; i++)
        {
            float ang = rot + i * (MathF.PI / 3f);
            Vector2 tip = center + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * armLen;
            dl.AddCircleFilled(tip, armWid * 1.3f, DrawHelpers.WithAlpha(bright, a * 0.75f));
            dl.AddCircleFilled(tip, armWid * 0.55f, DrawHelpers.WithAlpha(hot,   a * 0.95f));
        }
    }

    /// <summary>One full silhouette of the snowflake at a given stroke width and colour.</summary>
    private static void DrawSnowflakeSilhouette(
        ImDrawListPtr dl, Vector2 center, float rot, float armLen, float armWid, float a,
        uint color, int pairs, float subBranchAngle)
    {
        uint col = DrawHelpers.WithAlpha(color, a);

        for (int i = 0; i < 6; i++)
        {
            float ang = rot + i * (MathF.PI / 3f);
            Vector2 dir = new(MathF.Cos(ang), MathF.Sin(ang));
            Vector2 tip = center + dir * armLen;

            dl.AddLine(center, tip, col, armWid);

            for (int b = 0; b < pairs; b++)
            {
                // Position along the arm, spread evenly over the pairs we have. Tapers the length
                // toward the tip so the sub-branches get smaller as they climb the arm.
                float t;
                switch (pairs)
                {
                    case 1:  t = 0.55f; break;
                    case 2:  t = 0.40f + 0.32f * b; break;
                    default: t = 0.32f + 0.26f * b; break;
                }
                float subLenMul = 0.34f - 0.06f * b;

                Vector2 anchor = center + dir * (armLen * t);
                float subLen = armLen * subLenMul;

                for (int side = -1; side <= 1; side += 2)
                {
                    float sAng = ang + side * subBranchAngle;
                    Vector2 sDir = new(MathF.Cos(sAng), MathF.Sin(sAng));
                    Vector2 sTip = anchor + sDir * subLen;
                    dl.AddLine(anchor, sTip, col, armWid * 0.68f);
                }
            }
        }
    }

    // =====================================================================================
    // Edge-crystal branches
    // =====================================================================================

    private static void DrawBranch(ImDrawListPtr dl, Vector2 anchor, Vector2 bdir, float blen, float bwid,
                                   float a, uint deep, uint mid, uint bright, uint hot)
    {
        Vector2 btip = anchor + bdir * blen;

        dl.AddLine(anchor, btip, DrawHelpers.WithAlpha(deep,   a * 0.30f), bwid * 1.6f);
        dl.AddLine(anchor, btip, DrawHelpers.WithAlpha(mid,    a * 0.80f), bwid * 0.75f);
        dl.AddLine(anchor, btip, DrawHelpers.WithAlpha(bright, a * 0.85f), bwid * 0.35f);

        dl.AddCircleFilled(btip, bwid * 0.60f, DrawHelpers.WithAlpha(bright, a * 0.80f));
        dl.AddCircleFilled(btip, bwid * 0.28f, DrawHelpers.WithAlpha(hot,    a * 0.95f));
    }

    private static void DrawForkBranches(ImDrawListPtr dl, Vector2 root, Vector2 tip, Vector2 dir,
                                         float len, float wid, float a,
                                         uint deep, uint mid, uint bright, uint hot, int seed)
    {
        int count = 2 + (int)(DrawHelpers.Hash01(seed + 300) * 2f);

        for (int b = 0; b < count; b++)
        {
            int bs = unchecked(seed + 300 + b * 61);
            float t = DrawHelpers.HashRange(bs, 0.50f, 0.90f);

            Vector2 anchor = Vector2.Lerp(root, tip, t);

            float side = DrawHelpers.Hash01(bs + 1) < 0.5f ? 1f : -1f;
            float bang = side * DrawHelpers.HashRange(bs + 2, 0.60f, 1.20f);
            Vector2 bdir = Rotate(dir, bang);

            float blen = len * DrawHelpers.HashRange(bs + 3, 0.25f, 0.50f) * (1f - 0.40f * t);
            float bwid = wid * 0.80f;

            DrawBranch(dl, anchor, bdir, blen, bwid, a, deep, mid, bright, hot);
        }
    }

    private static void DrawFernBranches(ImDrawListPtr dl, Vector2 root, Vector2 tip, Vector2 dir,
                                         float len, float wid, float a,
                                         uint deep, uint mid, uint bright, uint hot, int seed)
    {
        int count = 5 + (int)(DrawHelpers.Hash01(seed + 400) * 3f);

        for (int b = 0; b < count; b++)
        {
            int bs = unchecked(seed + 400 + b * 61);

            float t = 0.15f + (b / (float)(count - 1)) * 0.75f;
            float side = (b % 2 == 0) ? 1f : -1f;

            float bang = side * DrawHelpers.HashRange(bs + 1, 0.60f, 1.00f);
            Vector2 bdir = Rotate(dir, bang);

            float lenMul = 1f - t * 0.55f;
            float blen = len * 0.35f * lenMul;
            float bwid = wid * 0.65f;

            Vector2 anchor = Vector2.Lerp(root, tip, t);

            DrawBranch(dl, anchor, bdir, blen, bwid, a, deep, mid, bright, hot);
        }
    }

    private static uint Shift(uint baseCol, uint coolTarget, uint warmTarget, float hue)
    {
        if (hue < 0f) return DrawHelpers.LerpColor(baseCol, coolTarget, -hue * 0.55f);
        return DrawHelpers.LerpColor(baseCol, warmTarget, hue * 0.55f);
    }

    private static Vector2 Rotate(Vector2 v, float r)
    {
        float c = MathF.Cos(r), s = MathF.Sin(r);
        return new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    // =====================================================================================
    // Snow particles
    // =====================================================================================

    private void DrawIce(ImDrawListPtr dl, float alpha, float time)
    {
        for (int i = 0; i < _ice.Count; i++)
        {
            ref readonly var p = ref _ice[i];
            float age = time - p.Born;
            float fade = EdgeParticleField.FadeFor(age / p.Lifespan);
            float a = alpha * fade;
            if (a < 0.005f) continue;

            float wobX = MathF.Sin(age * 1.6f + p.Born * 2.4f) * 5f;
            Vector2 pos = p.Pos + new Vector2(wobX, 0f);

            float haloMul = p.Size > 2.5f ? 2.6f : 2.0f;

            dl.AddCircleFilled(pos, p.Size * haloMul, DrawHelpers.WithAlpha(Bright, a * 0.14f));
            dl.AddCircleFilled(pos, p.Size,          DrawHelpers.WithAlpha(Bright, a * 0.80f));
            dl.AddCircleFilled(pos, p.Size * 0.4f,   DrawHelpers.WithAlpha(Hot,    a * 0.95f));
        }
    }

    private Vector2 IceSpawnPos(int seed)
        => new(DrawHelpers.HashRange(seed, -0.02f, 1.02f) * _screenSize.X, -8f);

    private Vector2 IceSpawnVel(int seed)
        => new(DrawHelpers.HashRange(seed + 1, -14f, 14f),
               DrawHelpers.HashRange(seed + 2, 18f, 55f));
}