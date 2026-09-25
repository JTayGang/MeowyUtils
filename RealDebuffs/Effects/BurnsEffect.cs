using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Burns: fire damage over time. Built entirely from a particle system - there is no ribbon
/// geometry anywhere, so the fire's silhouette is whatever the particles happen to trace as they
/// rise, fork, and dissipate.
///
/// Anti-uniformity, in four parts:
///   - Emitters are placed with heavy jitter (not on a grid) and some emitters are hot spots that
///     run at many times the rate of others.
///   - Each emitter's effective rate OSCILLATES on its own period and phase, so at any moment
///     some spots are raging and others are calm, and that balance shifts over seconds.
///   - Particles are BIMODAL: about a quarter are large slow "puffs" (the volume of the fire),
///     the rest are small fast "sparks" (the bright hot core). Two visually distinct particle
///     types layering together reads as fire rather than as a bead of identical dots.
///   - Puff sizes follow a heavy-tailed distribution - most are small, a few are very large - and
///     lifespan, turbulence, colour, and launch velocity are all much more widely varied than
///     the previous version.
///
/// The base band is a soft bottom gradient, NOT a row of circles - an even grid of glow discs was
/// reading as a repeating pattern and dominating the fire.
///
/// IGNITION: on every fresh application, the fire lights from the middle of the bottom edge and
/// rushes outward. Each emitter has its own delay, so the middle catches first, then the outer
/// bottom edge, then the lower sides climb. A brief white-hot flash accompanies each new emitter.
/// </summary>
public sealed class BurnsEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Burns;

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f;

    // ---- particle budget ----
    private const int MaxParticles = 900;

    // ---- palette ----
    private static readonly uint Soot     = DrawHelpers.ToU32(0.05f, 0.010f, 0.005f, 1f);
    private static readonly uint DeepRed  = DrawHelpers.ToU32(0.55f, 0.06f, 0.010f, 1f);
    private static readonly uint Ember    = DrawHelpers.ToU32(0.95f, 0.18f, 0.020f, 1f);
    private static readonly uint Orange   = DrawHelpers.ToU32(1.00f, 0.45f, 0.050f, 1f);
    private static readonly uint Yellow   = DrawHelpers.ToU32(1.00f, 0.78f, 0.200f, 1f);
    private static readonly uint HotWhite = DrawHelpers.ToU32(1.00f, 0.97f, 0.820f, 1f);

    // =====================================================================================
    // Emission points
    // =====================================================================================
    private const int BottomEmitters      = 18;
    private const int SideEmittersPerSide = 3;
    private const int TotalEmitters       = BottomEmitters + SideEmittersPerSide * 2;

    private struct EmissionPoint
    {
        public byte  Edge;           // 0 = bottom, 1 = left, 2 = right
        public float Along;          // 0..1 along that edge
        public float Rate;           // particles/sec baseline (before oscillation)
        public float OscPeriod;      // seconds per intensity oscillation
        public float OscPhase;
        public float IgnitionDelay;
        public float XJitter;        // lateral scatter, fraction of shortSide
        public float SpeedBias;
        public float SizeBias;
        public float HeatBias;       // 0..1, biases this plume hotter/cooler
        public float PuffChance;     // 0..1, how often this emitter spawns puffs vs sparks
        public int   Seed;
        public float Accum;
    }

    private readonly EmissionPoint[] _emitters = new EmissionPoint[TotalEmitters];

    // =====================================================================================
    // Fire particles - bimodal: puff or spark
    // =====================================================================================
    private struct FireParticle
    {
        public Vector2 Pos;
        public Vector2 Vel;
        public float Born;
        public float Lifespan;
        public float BaseSize;
        public float Heat;
        public float Phase;
        public float TurbFreqX, TurbAmpX;
        public float TurbFreqY, TurbAmpY;
        public float Hue;            // -1..1, shifts the colour curve slightly warmer/cooler
        public bool  IsPuff;         // true = big soft blob, false = small bright spark
    }

    private readonly FireParticle[] _particles = new FireParticle[MaxParticles];
    private int _count;

    // =====================================================================================
    // Embers
    // =====================================================================================
    private readonly EdgeParticleField _embers = new(maxParticles: 40, seedSalt: 0x100003);
    private Vector2 _screenSize;
    private readonly Func<int, Vector2> _spawnPos;
    private readonly Func<int, Vector2> _spawnVel;
    private readonly Func<int, string>  _noGlyph;

    // =====================================================================================
    // Ignition state
    // =====================================================================================
    private float _lastDrawTime = -100f;
    private float _ignitionStart;

    public BurnsEffect()
    {
        _spawnPos = SpawnPos;
        _spawnVel = SpawnVel;
        _noGlyph  = static _ => "";

        int idx = 0;

        // ---- Bottom emitters: jittered positions, wild rate variance ----
        for (int i = 0; i < BottomEmitters; i++)
        {
            int s = unchecked(0xB001 + i * 6427);

            // Base grid position, then heavy jitter. Some emitters pull toward a neighbour,
            // producing local clusters and gaps instead of a clean even spread.
            float alongBase = (i + 0.5f) / BottomEmitters;
            float clusterPull = DrawHelpers.Hash01(s + 40) < 0.35f
                ? DrawHelpers.HashRange(s + 41, -0.06f, 0.06f)
                : 0f;
            float along = Math.Clamp(alongBase + clusterPull + DrawHelpers.HashRange(s, -0.035f, 0.035f), 0.02f, 0.98f);

            // Rate: wildly varied so some emitters are barely a candle and others are a furnace.
            // Middle emitters get a modest boost so the fire's centre still reads as hottest.
            float rateBase = DrawHelpers.HashRange(s, 6f, 55f);
            float middleBoost = 1f - 0.35f * MathF.Abs(along - 0.5f) * 2f;

            _emitters[idx++] = new EmissionPoint
            {
                Edge          = 0,
                Along         = along,
                Rate          = rateBase * middleBoost,
                OscPeriod     = DrawHelpers.HashRange(s + 6, 0.55f, 1.85f),
                OscPhase      = DrawHelpers.HashRange(s + 7, 0f, MathF.PI * 2f),
                IgnitionDelay = MathF.Abs(along - 0.5f) * 0.75f,
                XJitter       = DrawHelpers.HashRange(s + 1, 0.010f, 0.035f),
                SpeedBias     = DrawHelpers.HashRange(s + 2, 0.65f, 1.35f),
                SizeBias      = DrawHelpers.HashRange(s + 3, 0.55f, 1.55f),
                HeatBias      = DrawHelpers.HashRange(s + 4, 0.05f, 0.90f),
                PuffChance    = DrawHelpers.HashRange(s + 5, 0.15f, 0.45f),
                Seed          = s,
            };
        }

        // ---- Side emitters: sparse, cooler, catching later ----
        for (int side = 0; side < 2; side++)
        {
            for (int i = 0; i < SideEmittersPerSide; i++)
            {
                int s = unchecked(0xB001 + idx * 6427);
                float along = (i + 0.5f) / SideEmittersPerSide * 0.55f
                            + DrawHelpers.HashRange(s, -0.04f, 0.04f);
                along = Math.Clamp(along, 0.02f, 0.60f);

                _emitters[idx++] = new EmissionPoint
                {
                    Edge          = (byte)(side == 0 ? 1 : 2),
                    Along         = along,
                    Rate          = DrawHelpers.HashRange(s,     5f, 22f),
                    OscPeriod     = DrawHelpers.HashRange(s + 6, 0.65f, 1.95f),
                    OscPhase      = DrawHelpers.HashRange(s + 7, 0f, MathF.PI * 2f),
                    IgnitionDelay = 0.55f + along * 0.85f,
                    XJitter       = DrawHelpers.HashRange(s + 1, 0.008f, 0.024f),
                    SpeedBias     = DrawHelpers.HashRange(s + 2, 0.55f, 0.95f),
                    SizeBias      = DrawHelpers.HashRange(s + 3, 0.55f, 1.05f),
                    HeatBias      = DrawHelpers.HashRange(s + 4, 0.00f, 0.45f),
                    PuffChance    = DrawHelpers.HashRange(s + 5, 0.30f, 0.65f),
                    Seed          = s,
                };
            }
        }
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float dt = ImGui.GetIO().DeltaTime;
        _screenSize = screenSize;
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);

        if (time - _lastDrawTime > NewCastGapSeconds)
        {
            _ignitionStart = time;
            _count = 0;
            for (int i = 0; i < TotalEmitters; i++) _emitters[i].Accum = 0f;
        }
        _lastDrawTime = time;
        float age = time - _ignitionStart;

        // Irregular flicker on three overlapping octaves, driving the vignette and bottom band.
        float flicker = 0.55f * DrawHelpers.Pulse(time, 0.31f)
                       + 0.30f * DrawHelpers.Pulse(time, 0.17f, 0.35f)
                       + 0.15f * DrawHelpers.Pulse(time, 0.53f, 0.60f);

        // Vignette: warm soot haze around the edges.
        DrawHelpers.DrawVignette(dl, screenSize, Soot, 0.15f, alpha * (0.55f + 0.35f * flicker));

        // Bottom gradient band - a soft wash instead of a row of circles. This is what reads as
        // "the fire is anchored to the ground" without the repeating-pattern artifact.
        float bandHeight = shortSide * 0.14f * Math.Clamp(age / 0.6f, 0f, 1f);
        if (bandHeight > 1f)
        {
            uint edgeGlow  = DrawHelpers.WithAlpha(Ember, alpha * 0.30f * (0.70f + 0.30f * flicker));
            uint clearGlow = DrawHelpers.WithAlpha(Ember, 0f);
            dl.AddRectFilledMultiColor(
                new Vector2(0f, screenSize.Y - bandHeight),
                new Vector2(screenSize.X, screenSize.Y),
                clearGlow, clearGlow, edgeGlow, edgeGlow);
        }

        // Integrate, emit, then draw.
        UpdateParticles(time, dt, age, shortSide);
        for (int i = 0; i < _count; i++)
            DrawParticle(dl, in _particles[i], alpha, time);

        // Embers above everything.
        _embers.Update(
            time, dt,
            spawnIntervalMin: 0.03f, spawnIntervalMax: 0.10f,
            spawnPos: _spawnPos, spawnVelocity: _spawnVel,
            pickGlyph: _noGlyph,
            lifespanMin: 1.3f, lifespanMax: 2.4f, sizeMin: 2f, sizeMax: 5f);

        for (int i = 0; i < _embers.Count; i++)
            DrawEmber(dl, in _embers[i], alpha, time);
    }

    // =====================================================================================
    // Particle system
    // =====================================================================================

    private void UpdateParticles(float time, float dt, float age, float shortSide)
    {
        // Compaction keeps draw order = spawn order, so brighter young particles land on top.
        int w = 0;
        for (int i = 0; i < _count; i++)
        {
            if (time - _particles[i].Born < _particles[i].Lifespan)
                _particles[w++] = _particles[i];
        }
        _count = w;

        // Integrate. Velocity decays exponentially so particles "cool" and slow as they rise.
        for (int i = 0; i < _count; i++)
        {
            ref var p = ref _particles[i];
            float decay = MathF.Exp(-dt * 0.45f);
            p.Vel *= decay;
            p.Pos += p.Vel * dt;
        }

        // Emit from every ignition-passed emitter, with a per-emitter intensity oscillation so
        // different spots swell and calm independently.
        for (int e = 0; e < TotalEmitters; e++)
        {
            ref var em = ref _emitters[e];
            if (age < em.IgnitionDelay) continue;

            float local = MathF.Min(1f, (age - em.IgnitionDelay) / 0.35f);

            // Slow oscillation on the emitter's own clock: at any moment some emitters are at
            // 0.3x and others at 1.7x, and that balance drifts over seconds.
            float osc = 0.55f + 0.90f * DrawHelpers.Pulse(time, em.OscPeriod, em.OscPhase);

            float rate = em.Rate * local * osc;
            em.Accum += rate * dt;

            int toSpawn = (int)em.Accum;
            if (toSpawn > 10) toSpawn = 10; // cap a long frame hitch from dumping the pool
            em.Accum -= toSpawn;

            for (int k = 0; k < toSpawn && _count < MaxParticles; k++)
                SpawnParticle(time, in em, shortSide);
        }
    }

    private void SpawnParticle(float time, in EmissionPoint em, float shortSide)
    {
        if (_count >= MaxParticles) return;
        int s = unchecked(em.Seed + (int)(time * 977f) + _count * 113);

        // Emitter world position.
        Vector2 emitPos;
        switch (em.Edge)
        {
            default:
            case 0: emitPos = new Vector2(em.Along * _screenSize.X, _screenSize.Y + 2f); break;
            case 1: emitPos = new Vector2(-2f, _screenSize.Y * (1f - em.Along));         break;
            case 2: emitPos = new Vector2(_screenSize.X + 2f, _screenSize.Y * (1f - em.Along)); break;
        }

        // Bimodal: some big soft puffs (volume), most small bright sparks (hot core).
        bool isPuff = DrawHelpers.Hash01(s + 20) < em.PuffChance;

        float jitterX = DrawHelpers.HashRange(s + 1, -em.XJitter, em.XJitter) * shortSide;
        float jitterY = DrawHelpers.HashRange(s + 2, -3f, 3f);
        Vector2 pos = emitPos + new Vector2(jitterX, jitterY);

        // Launch velocity. Puffs go slower (they billow, they don't shoot), sparks go faster.
        float v0Y, v0X, lifespan, baseSize, heat, turbAx, turbAy;

        if (isPuff)
        {
            v0Y = DrawHelpers.HashRange(s + 3, -280f, -140f) * em.SpeedBias;
            v0X = DrawHelpers.HashRange(s + 4, -40f, 40f);
            lifespan = DrawHelpers.HashRange(s + 5, 1.1f, 2.6f);
            // Heavy-tailed puff size: most are moderate, occasional ones are huge.
            float roll = DrawHelpers.Hash01(s + 21);
            float sizeRoll = 0.30f + 1.70f * roll * roll * roll;
            baseSize = DrawHelpers.HashRange(s + 6, 0.020f, 0.038f) * shortSide * em.SizeBias * sizeRoll;
            heat = Math.Clamp(em.HeatBias - 0.15f + DrawHelpers.HashRange(s + 7, -0.30f, 0.30f), 0f, 1f);
            turbAx = DrawHelpers.HashRange(s + 10, 3f, 12f);
            turbAy = DrawHelpers.HashRange(s + 12, 2f, 6f);
        }
        else
        {
            v0Y = DrawHelpers.HashRange(s + 3, -680f, -380f) * em.SpeedBias;
            v0X = DrawHelpers.HashRange(s + 4, -45f, 45f);
            lifespan = DrawHelpers.HashRange(s + 5, 0.5f, 1.6f);
            // Heavy-tailed spark size: most are tiny, a few are noticeably larger.
            float roll = DrawHelpers.Hash01(s + 21);
            float sizeRoll = 0.35f + 1.65f * roll * roll * roll;
            baseSize = DrawHelpers.HashRange(s + 6, 0.004f, 0.011f) * shortSide * em.SizeBias * sizeRoll;
            heat = Math.Clamp(em.HeatBias + 0.15f + DrawHelpers.HashRange(s + 7, -0.25f, 0.25f), 0f, 1f);
            turbAx = DrawHelpers.HashRange(s + 10, 6f, 26f);
            turbAy = DrawHelpers.HashRange(s + 12, 3f, 12f);
        }

        // Side emitters push inward slightly so their plumes curl toward the screen.
        if (em.Edge == 1) v0X += 55f;
        if (em.Edge == 2) v0X -= 55f;

        _particles[_count++] = new FireParticle
        {
            Pos       = pos,
            Vel       = new Vector2(v0X, v0Y),
            Born      = time,
            Lifespan  = lifespan,
            BaseSize  = baseSize,
            Heat      = heat,
            Phase     = DrawHelpers.HashRange(s + 8,  0f, MathF.PI * 2f),
            TurbFreqX = DrawHelpers.HashRange(s + 9,  2.0f, 6.5f),
            TurbAmpX  = turbAx,
            TurbFreqY = DrawHelpers.HashRange(s + 11, 1.2f, 4.0f),
            TurbAmpY  = turbAy,
            Hue       = DrawHelpers.HashRange(s + 13, -1f, 1f),
            IsPuff    = isPuff,
        };
    }

    private static void DrawParticle(ImDrawListPtr dl, in FireParticle p, float alpha, float time)
    {
        float age = time - p.Born;
        if (age < 0f || age >= p.Lifespan) return;
        float ageNorm = age / p.Lifespan;

        // Size envelope. Puffs have a slower attack and longer hold than sparks.
        float sizeT;
        if (p.IsPuff)
        {
            if (ageNorm < 0.30f)      sizeT = 0.35f + 0.65f * (ageNorm / 0.30f);
            else if (ageNorm < 0.70f) sizeT = 1f;
            else                      sizeT = 1f - (ageNorm - 0.70f) / 0.30f;
        }
        else
        {
            if (ageNorm < 0.15f)      sizeT = ageNorm / 0.15f;
            else if (ageNorm < 0.45f) sizeT = 1f;
            else                      sizeT = 1f - (ageNorm - 0.45f) / 0.55f;
        }
        sizeT = MathF.Max(0.04f, sizeT);
        float size = p.BaseSize * sizeT;

        // Alpha envelope: quick fade-in, slow fade-out.
        float fade = ageNorm < 0.10f
            ? ageNorm / 0.10f
            : (1f - ageNorm) * (1f - ageNorm);
        float a = alpha * Math.Clamp(fade, 0f, 1f);
        if (a < 0.005f || size < 0.4f) return;

        // Turbulence applied at draw time, offset from the clean ballistic path. Never accumulated
        // into the position, so it can't drift the particle sideways over its life.
        float turbX = MathF.Sin(age * p.TurbFreqX + p.Phase) * p.TurbAmpX;
        float turbY = MathF.Cos(age * p.TurbFreqY + p.Phase * 1.3f) * p.TurbAmpY;
        Vector2 pos = p.Pos + new Vector2(turbX, turbY);

        uint col = ColorForAge(ageNorm, p.Heat, p.Hue);

        if (p.IsPuff)
        {
            // Big soft blobs: one wide low-alpha fill, one tighter body. Reads as fire volume.
            dl.AddCircleFilled(pos, size * 1.15f, DrawHelpers.WithAlpha(col, a * 0.20f));
            dl.AddCircleFilled(pos, size * 0.65f, DrawHelpers.WithAlpha(col, a * 0.42f));
        }
        else
        {
            // Bright sparks: outer glow, tight body, hot core while young.
            dl.AddCircleFilled(pos, size * 1.5f, DrawHelpers.WithAlpha(col, a * 0.18f));
            dl.AddCircleFilled(pos, size,        DrawHelpers.WithAlpha(col, a * 0.80f));
            if (ageNorm < 0.40f)
            {
                float innerK = 1f - ageNorm / 0.40f;
                uint inner = DrawHelpers.LerpColor(col, HotWhite, innerK * 0.60f);
                dl.AddCircleFilled(pos, size * 0.50f, DrawHelpers.WithAlpha(inner, a * 0.90f));
            }
        }
    }

    /// <summary>
    /// Colour over a particle's life, running hot at birth and cooling through orange, ember, deep
    /// red, and soot. <paramref name="heat"/> shifts the curve hotter; <paramref name="hue"/> adds
    /// a slight per-particle warm/cool tint on top so no two particles look identical.
    /// </summary>
    private static uint ColorForAge(float ageNorm, float heat, float hue)
    {
        float t = Math.Clamp(ageNorm - heat * 0.14f + hue * 0.05f, 0f, 1f);

        uint c;
        if      (t < 0.10f) c = DrawHelpers.LerpColor(HotWhite, Yellow,   t / 0.10f);
        else if (t < 0.28f) c = DrawHelpers.LerpColor(Yellow,   Orange,  (t - 0.10f) / 0.18f);
        else if (t < 0.55f) c = DrawHelpers.LerpColor(Orange,   Ember,   (t - 0.28f) / 0.27f);
        else if (t < 0.82f) c = DrawHelpers.LerpColor(Ember,    DeepRed, (t - 0.55f) / 0.27f);
        else                c = DrawHelpers.LerpColor(DeepRed,  Soot,    (t - 0.82f) / 0.18f);

        // Per-particle warm/cool nudge: pushes a fraction toward white (warmer) or red (cooler).
        if (hue > 0f) return DrawHelpers.LerpColor(c, HotWhite, hue * 0.10f);
        return DrawHelpers.LerpColor(c, DeepRed, -hue * 0.14f);
    }

    // =====================================================================================
    // Embers
    // =====================================================================================

    private void DrawEmber(ImDrawListPtr dl, in EdgeParticleField.Particle p, float alpha, float time)
    {
        float age = time - p.Born;
        float ageNorm = Math.Clamp(age / p.Lifespan, 0f, 1f);
        float fade = EdgeParticleField.FadeFor(ageNorm);
        float a = alpha * fade;
        if (a < 0.003f) return;

        int seed = unchecked((int)(p.Born * 10007f));
        float heat = DrawHelpers.Hash01(seed);

        float wobAmp = 4f + ageNorm * 11f;
        float wobX = MathF.Sin(age * 6f + p.Born * 3f) * wobAmp;
        float settle = ageNorm * ageNorm * p.Size * 9f;
        var pos = p.Pos + new Vector2(wobX, settle);

        uint mid  = DrawHelpers.LerpColor(Ember, Yellow, heat);
        uint core = DrawHelpers.LerpColor(Orange, HotWhite, heat);
        dl.AddCircleFilled(pos, p.Size * 2.0f, DrawHelpers.WithAlpha(Soot, a * 0.30f));
        dl.AddCircleFilled(pos, p.Size,        DrawHelpers.WithAlpha(mid,  a * 0.75f));
        dl.AddCircleFilled(pos, p.Size * 0.5f, DrawHelpers.WithAlpha(core, a * 0.95f));

        if (age < 0.08f)
        {
            float popFade = 1f - age / 0.08f;
            for (int k = 0; k < 3; k++)
            {
                float ang = DrawHelpers.HashRange(seed + k, 0f, MathF.PI * 2f);
                var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
                var tip = pos + dir * p.Size * DrawHelpers.HashRange(seed + k + 10, 3f, 7f);
                dl.AddLine(pos, tip, DrawHelpers.WithAlpha(HotWhite, a * popFade * 0.8f), 1.4f);
            }
        }
    }

    private Vector2 SpawnPos(int seed)
    {
        float x01 = (DrawHelpers.Hash01(seed) + DrawHelpers.Hash01(seed + 100)) * 0.5f;
        return new Vector2(x01 * _screenSize.X, _screenSize.Y - 6f);
    }

    private Vector2 SpawnVel(int seed) =>
        new(DrawHelpers.HashRange(seed + 2, -20f, 20f),
            DrawHelpers.HashRange(seed + 1, -150f, -85f));
}