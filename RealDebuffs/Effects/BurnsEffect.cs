using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Burns: fire damage over time. The fire is built from a fixed roster of ~30 particle emitters,
/// each with its own position along the bottom or lower sides, its own base spawn rate, and its
/// own rate-oscillation clock. Combined, they produce the three properties that make a fire read
/// as a fire rather than a curtain of motes:
///
///  - Cluster: emitters are jittered off a grid, with a per-emitter "pull toward a neighbor"
///    bias. Some regions get dense triple-emitters, other regions get gaps.
///
///  - Varying rate: each emitter's baseline rate is randomized across a 5x range. Combined with
///    the middle-boost that runs center emitters hottest, this is what gives the fire its shape
///    (short and hot in the middle, sparse and lazy at the edges).
///
///  - Oscillation: each emitter's effective rate is modulated by its own slow sine wave, out of
///    phase with every other emitter. At any moment some spots are raging and others are calm,
///    and that balance drifts over seconds.
///
/// INTRO (0.0 - ~1.0s): each emitter has an ignition delay based on distance from the bottom
/// center, so the middle catches first, then the fire spreads outward along the bottom edge, then
/// the lower side corners climb. The moment an emitter catches, its rate is boosted ~3x and
/// decays back to steady over ~0.4s - the "catch and flare" that sells the ignition.
///
/// HERO VISUAL: the fire particle fields themselves, all emitted as Role.Ember and rendered by
/// whichever particle material is assigned to that role (currently particle.ember). The vignette
/// and ground band are ambient staging, deliberately quieter.
/// </summary>
public sealed class BurnsEffect : ISceneEffect
{
    public DebuffKind Kind => DebuffKind.Burns;

    // ---- palette (only what the effect itself owns; fire colors live in particle.ember) ----
    private static readonly uint Soot  = DrawHelpers.ToU32(0.05f, 0.01f, 0.005f, 1f);
    private static readonly uint Ember = DrawHelpers.ToU32(0.95f, 0.18f, 0.02f, 1f);

    // ---- timing ----
    private const float NewCastGapSeconds = 1.0f;

    // ---- emitter model ----
    // A single emission point plus everything it needs to run independently. The effect owns an
    // array of these; ParticleEmitter underneath handles the per-particle bookkeeping.
    private sealed class FireEmitter
    {
        public ParticleEmitter Pool = null!;
        public byte  Edge;              // 0 = bottom, 1 = left, 2 = right
        public float Along;             // 0..1 position along that edge
        public float IgnitionDelay;     // seconds after cast before this emitter starts
        public float RateBoost;         // baseline rate multiplier, varies per emitter
        public float OscPeriod;         // seconds per rate-oscillation cycle
        public float OscPhase;
        public float JitterX;           // spawn jitter as a fraction of screen width
        public float SizeBoost;         // per-emitter size multiplier
        public float SpeedBoost;        // per-emitter velocity multiplier
        public float InwardPush;        // px/s horizontal velocity added at spawn (side emitters)
        public float BaseIntervalMin;   // base spawn interval, before rate boost / oscillation
        public float BaseIntervalMax;
        public float LifespanMin;
        public float LifespanMax;
        public float SizeMinFrac;       // size band as a fraction of shortSide
        public float SizeMaxFrac;
        public float MinSpeed;          // px/s upward (positive number; sign flipped at spawn)
        public float MaxSpeed;
        public float Sway;              // horizontal wobble amplitude in px
        public bool  IsPuff;            // informational; the material decides shape from Size
    }

    private readonly List<FireEmitter> _emitters = new();
    private readonly ParticleEmitter _embers = new(maxParticles: 60, seedSalt: 0x8107);

    // Set every Emit call before any emitter runs - spawn lambdas read it via SpawnPos.
    private Vector2 _screenSize;

    // ---- cast state ----
    private float _castStart = -1f;
    private float _lastDrawTime = -100f;

    public BurnsEffect()
    {
        // ---- Bottom emitters: 12 jittered positions, each spawning BOTH a puff and a spark ----
        const int BottomPositions = 12;
        for (int i = 0; i < BottomPositions; i++)
        {
            int s = unchecked(0xB001 + i * 6427);

            // Position: even grid, plus a per-emitter "cluster pull" (some pull toward a
            // neighbor, producing density variation) plus fine jitter. Clamped away from the
            // exact screen corners.
            float alongBase = (i + 0.5f) / BottomPositions;
            float clusterPull = DrawHelpers.Hash01(s + 40) < 0.35f
                ? DrawHelpers.HashRange(s + 41, -0.06f, 0.06f)
                : 0f;
            float along = Math.Clamp(alongBase + clusterPull + DrawHelpers.HashRange(s, -0.035f, 0.035f), 0.02f, 0.98f);

            // Rate: 0.5x..2.5x baseline, times a middle-boost so the center runs hotter.
            float rateBase   = DrawHelpers.HashRange(s + 60, 0.5f, 2.5f);
            float middleBoost = 1f - 0.35f * MathF.Abs(along - 0.5f) * 2f;
            float rate       = rateBase * middleBoost;

            // Ignition: distance from the bottom center → middle catches first.
            float ignitionDelay = MathF.Abs(along - 0.5f) * 0.70f;

            float oscPeriod = DrawHelpers.HashRange(s + 6, 0.55f, 1.85f);
            float oscPhase  = DrawHelpers.HashRange(s + 7, 0f, MathF.PI * 2f);
            float jitterX   = DrawHelpers.HashRange(s + 1, 0.010f, 0.035f);
            float speedBoost = DrawHelpers.HashRange(s + 2, 0.65f, 1.35f);
            float sizeBoost  = DrawHelpers.HashRange(s + 3, 0.55f, 1.55f);

            // Puff emitter: slower, larger, gives the fire its volume.
            _emitters.Add(new FireEmitter
            {
                Pool = new ParticleEmitter(maxParticles: 90, seedSalt: 0x8105 + i * 17),
                Edge = 0, Along = along,
                IgnitionDelay = ignitionDelay,
                RateBoost = rate,
                OscPeriod = oscPeriod, OscPhase = oscPhase,
                JitterX = jitterX, SizeBoost = sizeBoost, SpeedBoost = speedBoost,
                InwardPush = 0f,
                BaseIntervalMin = 0.06f, BaseIntervalMax = 0.18f,
                LifespanMin = 1.0f, LifespanMax = 2.4f,
                SizeMinFrac = 0.018f, SizeMaxFrac = 0.045f,
                MinSpeed = 130f, MaxSpeed = 280f,
                Sway = 3f,
                IsPuff = true,
            });

            // Spark emitter: same position, faster, smaller, hotter. Different osc phase so
            // puff and spark don't both surge on the same frame.
            _emitters.Add(new FireEmitter
            {
                Pool = new ParticleEmitter(maxParticles: 90, seedSalt: 0x8205 + i * 17),
                Edge = 0, Along = along,
                IgnitionDelay = ignitionDelay,
                RateBoost = rate * 1.5f,
                OscPeriod = oscPeriod * 0.85f, OscPhase = oscPhase + 1.7f,
                JitterX = jitterX, SizeBoost = sizeBoost, SpeedBoost = speedBoost,
                InwardPush = 0f,
                BaseIntervalMin = 0.02f, BaseIntervalMax = 0.06f,
                LifespanMin = 0.5f, LifespanMax = 1.3f,
                SizeMinFrac = 0.003f, SizeMaxFrac = 0.010f,
                MinSpeed = 380f, MaxSpeed = 700f,
                Sway = 4f,
                IsPuff = false,
            });
        }

        // ---- Side emitters: 2 per side, climbing the lower corners ----
        for (int side = 0; side < 2; side++)
        {
            for (int i = 0; i < 2; i++)
            {
                int idx = BottomPositions + side * 2 + i;
                int s = unchecked(0xB001 + idx * 6427);

                byte edge = (byte)(side == 0 ? 1 : 2);
                float along = (i + 0.5f) / 2f * 0.55f + DrawHelpers.HashRange(s, -0.04f, 0.04f);
                along = Math.Clamp(along, 0.02f, 0.60f);

                // Inward horizontal push so side plumes curl toward the screen rather than
                // hugging the edge.
                float inwardPush = side == 0 ? 40f : -40f;

                float oscPeriod = DrawHelpers.HashRange(s + 6, 0.65f, 1.95f);
                float oscPhase  = DrawHelpers.HashRange(s + 7, 0f, MathF.PI * 2f);
                float jitterX   = DrawHelpers.HashRange(s + 1, 0.008f, 0.024f);
                float speedBoost = DrawHelpers.HashRange(s + 2, 0.55f, 0.95f);
                float sizeBoost  = DrawHelpers.HashRange(s + 3, 0.55f, 1.05f);
                float ignitionDelay = 0.55f + along * 0.85f;

                _emitters.Add(new FireEmitter
                {
                    Pool = new ParticleEmitter(maxParticles: 40, seedSalt: 0x8105 + idx * 17),
                    Edge = edge, Along = along,
                    IgnitionDelay = ignitionDelay,
                    RateBoost = DrawHelpers.HashRange(s, 0.5f, 1.5f),
                    OscPeriod = oscPeriod, OscPhase = oscPhase,
                    JitterX = jitterX, SizeBoost = sizeBoost, SpeedBoost = speedBoost,
                    InwardPush = inwardPush,
                    BaseIntervalMin = 0.08f, BaseIntervalMax = 0.20f,
                    LifespanMin = 0.9f, LifespanMax = 2.0f,
                    SizeMinFrac = 0.012f, SizeMaxFrac = 0.032f,
                    MinSpeed = 100f, MaxSpeed = 220f,
                    Sway = 3f,
                    IsPuff = true,
                });

                _emitters.Add(new FireEmitter
                {
                    Pool = new ParticleEmitter(maxParticles: 40, seedSalt: 0x8205 + idx * 17),
                    Edge = edge, Along = along,
                    IgnitionDelay = ignitionDelay,
                    RateBoost = DrawHelpers.HashRange(s, 0.5f, 1.5f),
                    OscPeriod = oscPeriod * 0.85f, OscPhase = oscPhase + 1.7f,
                    JitterX = jitterX, SizeBoost = sizeBoost, SpeedBoost = speedBoost,
                    InwardPush = inwardPush,
                    BaseIntervalMin = 0.03f, BaseIntervalMax = 0.08f,
                    LifespanMin = 0.5f, LifespanMax = 1.2f,
                    SizeMinFrac = 0.003f, SizeMaxFrac = 0.008f,
                    MinSpeed = 320f, MaxSpeed = 600f,
                    Sway = 4f,
                    IsPuff = false,
                });
            }
        }
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, Vector4? colorOverride)
    {
        _screenSize = screenSize;

        // Gap since last Emit = fresh application. Reset ignition and drop any lingering particles
        // from the previous cast, so the fire always starts from the middle.
        if (time - _lastDrawTime > NewCastGapSeconds)
        {
            _castStart = time;
            foreach (var e in _emitters) e.Pool.Clear();
            _embers.Clear();
        }
        _lastDrawTime = time;
        float age = time - _castStart;

        float dt = ImGui.GetIO().DeltaTime;
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);

        // Three-octave flicker drives vignette and band together, so the whole effect breathes
        // as one rather than each layer pulsing on its own clock.
        float flicker = 0.55f * DrawHelpers.Pulse(time, 0.31f)
                      + 0.30f * DrawHelpers.Pulse(time, 0.17f, 0.35f)
                      + 0.15f * DrawHelpers.Pulse(time, 0.53f, 0.60f);

        // ---- 1. soot vignette ----
        scene.RequestVignette(Soot, 0.15f, alpha * (0.55f + 0.35f * flicker), priority: 30, colorOverride);

        // ---- 2. ground band ----
        float bandHeight = shortSide * 0.14f * Math.Clamp(age / 0.5f, 0f, 1f);
        if (bandHeight > 1f)
        {
            // During ignition the band is stronger, so the first "catch" moment reads.
            float bandAlpha = alpha * (0.28f + 0.25f * MathF.Exp(-age / 0.55f)) * (0.70f + 0.30f * flicker);
            scene.AddRegion(new RegionPrimitive
            {
                Min = new Vector2(0f, screenSize.Y - bandHeight),
                Max = screenSize,
                Tint = Ember,
                Alpha = bandAlpha,
                Bottom = true,
                ColorOverride = colorOverride,
            });
        }

        // ---- 3. drive every fire emitter ----
        foreach (var e in _emitters)
        {
            if (age < e.IgnitionDelay) continue;

            // Catch-and-flare: right after this emitter's ignition, its rate runs ~3.5x and
            // decays back to 1x over ~0.4s. Sells "the fire just caught here."
            float ignTime  = age - e.IgnitionDelay;
            float ignBoost = 1f + 2.5f * MathF.Exp(-ignTime / 0.40f);

            // Per-emitter rate oscillation.
            float osc = 0.55f + 0.90f * DrawHelpers.Pulse(time, e.OscPeriod, e.OscPhase);

            float effectiveRate = e.RateBoost * ignBoost * osc;
            float intervalMin = e.BaseIntervalMin / effectiveRate;
            float intervalMax = e.BaseIntervalMax / effectiveRate;

            // Tighten the interval so it doesn't collapse to zero under extreme multipliers.
            if (intervalMin < 0.005f) intervalMin = 0.005f;
            if (intervalMax < 0.010f) intervalMax = 0.010f;

            var captured = e;
            e.Pool.Update(
                time, dt,
                spawnIntervalMin: intervalMin, spawnIntervalMax: intervalMax,
                spawnPos: seed => SpawnPos(captured, seed),
                spawnVelocity: seed => SpawnVel(captured, seed),
                lifespanMin: e.LifespanMin, lifespanMax: e.LifespanMax,
                sizeMin: shortSide * e.SizeMinFrac * e.SizeBoost,
                sizeMax: shortSide * e.SizeMaxFrac * e.SizeBoost);

            e.Pool.Emit(scene, time, PrimitiveRole.Ember,
                        brightnessMul: alpha, colorOverride, swayPerParticle: e.Sway);
        }

        // ---- 4. embers: rise above the fire once it has fully caught ----
        if (age > 1.0f)
        {
            float emberFade = Math.Clamp((age - 1.0f) / 1.0f, 0f, 1f);

            _embers.Update(
                time, dt,
                spawnIntervalMin: 0.05f, spawnIntervalMax: 0.14f,
                spawnPos: seed => new Vector2(
                    DrawHelpers.HashRange(seed, 0.10f, 0.90f) * screenSize.X,
                    screenSize.Y - 6f),
                spawnVelocity: seed => new Vector2(
                    DrawHelpers.HashRange(seed + 20, -20f, 20f),
                    -DrawHelpers.HashRange(seed + 21, 90f, 160f)),
                lifespanMin: 1.4f, lifespanMax: 2.6f,
                sizeMin: shortSide * 0.006f, sizeMax: shortSide * 0.014f);

            _embers.Emit(scene, time, PrimitiveRole.Ember,
                         brightnessMul: alpha * 0.9f * emberFade, colorOverride, swayPerParticle: 6f);
        }
    }

    /// <summary>
    /// World-space spawn point for one emission. Bottom emitters sit just below the bottom edge
    /// (particles rise into view). Side emitters sit just outside the left/right edge and
    /// positions are 1-Along, so Along = 0 is the bottom corner and Along = 0.6 is 60% up the side.
    /// </summary>
    private Vector2 SpawnPos(FireEmitter e, int seed)
    {
        float jitter = DrawHelpers.HashRange(seed + 20, -e.JitterX, e.JitterX);

        return e.Edge switch
        {
            0 => new Vector2((e.Along + jitter) * _screenSize.X, _screenSize.Y + 2f),
            1 => new Vector2(-2f, (1f - e.Along + jitter) * _screenSize.Y),
            _ => new Vector2(_screenSize.X + 2f, (1f - e.Along + jitter) * _screenSize.Y),
        };
    }

    /// <summary>
    /// Launch velocity for one particle: mostly upward (sign-flipped), with a small horizontal
    /// scatter and the emitter's own inward push for side emitters.
    /// </summary>
    private Vector2 SpawnVel(FireEmitter e, int seed)
    {
        float vy = -DrawHelpers.HashRange(seed + 11, e.MinSpeed, e.MaxSpeed) * e.SpeedBoost;
        float vx = DrawHelpers.HashRange(seed + 10, -35f, 35f) + e.InwardPush;
        return new Vector2(vx, vy);
    }

}