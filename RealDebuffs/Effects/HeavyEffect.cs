using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Heavy: a fan of massive chains slams out of the screen and locks across it, settling into a
/// slow weighted sway while the ground darkens under their pull.
///
/// CAST LAYOUT (fixed per cast, reseeded from cast start time):
///  - 5 chains total. Two anchor to the top edge, two to the bottom, one is a free chain whose
///    endpoints can be anywhere on the perimeter but at least a quarter apart.
///  - Each chain is a quadratic bezier between its two edge endpoints, plus a parabolic sag and a
///    slow travelling sway wave.
///
/// CAST-IN AND IMPACT:
///  - Each chain extends from its anchor over ~0.55s after its own stagger delay.
///  - The moment a chain reaches its full length, it BURSTS: a one-shot spray of spark particles
///    fires off the tip, and a damped tension shake is applied to the chain body. Both use the
///    same `sinceLock` timer, so they always read as one continuous event: SLAM, spark spray,
///    chain whipping, settle.
///  - After the shake decays (~1s), the chain settles into steady sway.
///
/// HERO SLOT: the chains, emitted as Stroke/MainStroke. The ground darkening is a Region that
/// stays with the effect regardless of which material the chains use - it's the scene's mood, not
/// the strand's.
///
/// Both the impact sparks and the chain itself can be swapped via config or a Moodle description
/// ("chains made of lightning"): the sparks use Particle/Spark and the chains use Stroke/MainStroke,
/// and EffectHeroSlots exposes both as Hero slots so a single substitution phrase reaches whichever
/// type matches.
/// </summary>
public sealed class HeavyEffect : ISceneEffect
{
    public DebuffKind Kind => DebuffKind.Heavy;

    // ---- timing ----
    private const float NewCastGapSeconds   = 1.0f;
    private const float ChainExtendSeconds  = 0.55f;
    private const float SettleStart         = 0.55f;
    private const float SettleEnd           = 1.80f;
    private const float SettleFlashSeconds  = 0.30f;

    // Tension shake: peaks at lock-in, decays exponentially. Much higher frequency than the
    // steady sway so it reads as a distinct "plucked string" jolt rather than a bigger sway.
    private const float ShakeDecaySeconds = 0.85f;
    private const float ShakeFrequency    = 11.0f;
    private const float ShakeAmplitudeFrac = 0.022f; // of screen height

    // ---- geometry ----
    private const int   ChainSamples = 16;
    private const int   ChainCount   = 5;
    private const float Tau          = MathF.PI * 2f;

    private static readonly uint GroundShade = DrawHelpers.ToU32(0.03f, 0.03f, 0.04f, 1f);

    private float _lastDrawTime = -100f;
    private float _castStart;

    // One StrandPath per chain, sized once.
    private readonly StrandPath[] _strandPaths = new StrandPath[ChainCount];

    // One spark emitter per chain. Burst-only: fires exactly once when its chain locks in.
    private readonly ParticleEmitter[] _chainSparks = new ParticleEmitter[ChainCount];

    // Per-chain: time the burst fired, -1 if not yet. Reset on recast.
    private readonly float[] _burstAt = new float[ChainCount];

    private readonly Blueprint[] _blueprints = new Blueprint[ChainCount];

    private readonly struct Blueprint
    {
        public readonly Vector2 Start, End, Control;
        public readonly float Sag;
        public readonly float Delay;
        public readonly float Scale;
        public readonly int   Seed;
        public readonly bool  AnchorBottom;

        public Blueprint(Vector2 start, Vector2 end, Vector2 control,
                         float sag, float delay, int seed, float scale, bool anchorBottom)
        {
            Start = start; End = end; Control = control;
            Sag = sag; Delay = delay; Seed = seed; Scale = scale;
            AnchorBottom = anchorBottom;
        }
    }

    public HeavyEffect()
    {
        for (int i = 0; i < ChainCount; i++)
        {
            _strandPaths[i] = new StrandPath(ChainSamples);
            _chainSparks[i] = new ParticleEmitter(maxParticles: 40, seedSalt: 0x8EA0 + i * 37);
        }
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, Vector4? colorOverride)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;

        // Fresh cast: reset state, rebuild layout, drop any lingering sparks.
        if (time - _lastDrawTime > NewCastGapSeconds)
        {
            _castStart = time;
            BuildBlueprints(unchecked((int)(_castStart * 1000f)));
            for (int i = 0; i < ChainCount; i++)
            {
                _chainSparks[i].Clear();
                _burstAt[i] = -1f;
            }
        }
        _lastDrawTime = time;
        float age = time - _castStart;

        float dt = ImGui.GetIO().DeltaTime;
        float minDim = MathF.Min(screenSize.X, screenSize.Y);
        float linkBase = minDim * 0.044f;

        // ---- ground darkening ----
        float castIn = Saturate(age / 0.6f);
        float pulse = DrawHelpers.Pulse(time, 2.2f);
        float depth = screenSize.Y * (0.14f + 0.035f * pulse) * castIn;
        if (depth > 1f)
        {
            scene.AddRegion(new RegionPrimitive
            {
                Min = new Vector2(0f, screenSize.Y - depth),
                Max = screenSize,
                Tint = GroundShade,
                Alpha = 0.70f * alpha,
                Bottom = true,
                ColorOverride = colorOverride,
            });
        }

        // ---- chains ----
        for (int i = 0; i < ChainCount; i++)
            EmitChain(scene, i, in _blueprints[i], screenSize, linkBase, dt, time, age, alpha, colorOverride);
    }

    // =====================================================================================
    // Per-chain emission
    // =====================================================================================

    private void EmitChain(EffectScene scene, int idx, in Blueprint bp, Vector2 screenSize,
                           float linkBase, float dt, float time, float age,
                           float alpha, Vector4? colorOverride)
    {
        var path = _strandPaths[idx];
        BuildPath(path, in bp, screenSize, time, age);

        float lockAt = bp.Delay + ChainExtendSeconds;
        float sinceLock = age - lockAt;

        // Impact burst: fires once on the first frame sinceLock crosses zero.
        if (sinceLock >= 0f && _burstAt[idx] < 0f)
        {
            _burstAt[idx] = time;
            FireImpactBurst(idx, screenSize, in bp, path, time);
        }

        // Cast-in reveal: 0 until this chain's delay, then eases to 1 over ChainExtendSeconds.
        float gt = Saturate((age - bp.Delay) / ChainExtendSeconds);
        float reveal = EaseOutCubic(gt);

        // Tip flare: full while extending, then a short settle flash right after lock-in.
        float tipFlare;
        if (gt > 0.001f && gt < 1f)
        {
            tipFlare = 1f - gt;
        }
        else if (gt >= 1f)
        {
            float settle = sinceLock / SettleFlashSeconds;
            tipFlare = (settle >= 0f && settle < 1f) ? 0.7f * (1f - settle) : 0f;
        }
        else
        {
            tipFlare = 0f;
        }

        var stroke = new StrokePrimitive
        {
            Path = path,
            Role = PrimitiveRole.MainStroke,
            Reveal = reveal,
            WidthHint = linkBase * bp.Scale,
            Brightness = alpha,
            TipFlare = tipFlare,
            Seed = bp.Seed,
            Phase = DrawHelpers.HashRange(bp.Seed + 999, 0f, Tau),
            FlushStart = bp.AnchorBottom,
            ColorOverride = colorOverride,
        };
        scene.AddStroke(stroke);

        // Sparks: integrate + emit. Burst-only emitter; nothing spawns on its own.
        _chainSparks[idx].UpdateBurstOnly(time, dt);
        _chainSparks[idx].Emit(scene, time, PrimitiveRole.Spark,
                               brightnessMul: alpha, colorOverride, swayPerParticle: 1.5f);
    }

    /// <summary>
    /// Fires the impact spark spray. Called once per chain, the frame it locks in. Direction is
    /// opposite to the chain's approach at the tip (sparks ricochet off the impact), with a wide
    /// angular spread and a slight downward gravity bias so the spray settles.
    /// </summary>
    private void FireImpactBurst(int idx, Vector2 screenSize, in Blueprint bp, StrandPath path, float time)
    {
        // Chain's approach direction at the tip: from second-to-last point to last.
        int n = path.Count;
        Vector2 approach = n >= 2
            ? Normalize(path.Points[n - 1] - path.Points[n - 2])
            : new Vector2(0f, 1f);

        // Sparks bounce back off the impact: opposite the approach direction.
        float baseAngle = MathF.Atan2(-approach.Y, -approach.X);

        var tip = path.Points[n - 1];

        _chainSparks[idx].Burst(
            count: 18,
            time: time,
            spawnPos: seed =>
            {
                // Small jitter around the impact point.
                float dx = DrawHelpers.HashRange(seed + 20, -4f, 4f);
                float dy = DrawHelpers.HashRange(seed + 21, -4f, 4f);
                return tip + new Vector2(dx, dy);
            },
            spawnVelocity: seed =>
            {
                // Cone spread of ±1.1 rad around the ricochet direction, speed 180-420 px/s,
                // plus a downward gravity-ish bias.
                float ang = baseAngle + DrawHelpers.HashRange(seed + 30, -1.1f, 1.1f);
                float speed = DrawHelpers.HashRange(seed + 31, 180f, 420f);
                var v = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * speed;
                return v + new Vector2(0f, 140f);
            },
            lifespanMin: 0.35f, lifespanMax: 0.75f,
            sizeMin: screenSize.X * 0.0016f, sizeMax: screenSize.X * 0.0035f);
    }

    // =====================================================================================
    // Blueprint layout
    // =====================================================================================

    private void BuildBlueprints(int castSeed)
    {
        const float cx = 0.5f;
        const float third = 1f / 3f;
        const float edgePad = 0.2f;

        for (int i = 0; i < 4; i++)
        {
            int s = unchecked(castSeed + i * 977);
            bool topHalf = i < 2;
            bool leftHalf = (i & 1) == 0;

            float sy = topHalf ? 0f : 1f;
            float sx = leftHalf
                ? DrawHelpers.HashRange(s, cx - third, cx)
                : DrawHelpers.HashRange(s, cx, cx + third);

            float ex = leftHalf ? 0f : 1f;
            float ey = DrawHelpers.HashRange(s + 1, edgePad, 1f - edgePad);

            float ccx = (sx + ex) * 0.5f + DrawHelpers.HashRange(s + 2, -0.08f, 0.08f);
            float ccy = (sy + ey) * 0.5f + DrawHelpers.HashRange(s + 3, -0.05f, 0.05f);

            float sag   = DrawHelpers.HashRange(s + 4, 0.1f, 0.2f);
            float delay = DrawHelpers.HashRange(s + 5, 0.00f, 0.22f);
            float scale = DrawHelpers.HashRange(s + 6, 0.85f, 1.15f);

            _blueprints[i] = new Blueprint(
                new Vector2(sx, sy), new Vector2(ex, ey), new Vector2(ccx, ccy),
                sag, delay, s, scale, anchorBottom: !topHalf);
        }

        {
            int s = unchecked(castSeed + 4 * 977);
            const float perimNorm = 4f;
            const float minSpan = perimNorm * 0.25f;

            float a = DrawHelpers.HashRange(s, 0f, perimNorm);
            float bOffset = DrawHelpers.HashRange(s + 1, minSpan, perimNorm - minSpan);
            float b = a + bOffset;

            Vector2 pa = PerimeterToNormalized(a);
            Vector2 pb = PerimeterToNormalized(b);

            float ccx = (pa.X + pb.X) * 0.5f + DrawHelpers.HashRange(s + 2, -0.08f, 0.08f);
            float ccy = (pa.Y + pb.Y) * 0.5f + DrawHelpers.HashRange(s + 3, -0.05f, 0.05f);

            float sag   = DrawHelpers.HashRange(s + 4, 0.06f, 0.14f);
            float delay = DrawHelpers.HashRange(s + 5, 0.00f, 0.22f);
            float scale = DrawHelpers.HashRange(s + 6, 0.85f, 1.15f);

            _blueprints[4] = new Blueprint(
                pa, pb, new Vector2(ccx, ccy),
                sag, delay, s, scale, anchorBottom: false);
        }
    }

    private static Vector2 PerimeterToNormalized(float p)
    {
        const float perim = 4f;
        p %= perim;
        if (p < 0f) p += perim;

        if (p < 1f) return new Vector2(p, 0f);
        p -= 1f;
        if (p < 1f) return new Vector2(1f, p);
        p -= 1f;
        if (p < 1f) return new Vector2(1f - p, 1f);
        p -= 1f;
        return new Vector2(0f, 1f - p);
    }

    // =====================================================================================
    // Path
    // =====================================================================================

    /// <summary>
    /// Fills the given path with this frame's chain shape: quadratic bezier between anchor and
    /// endpoint, sag bowing downward, and two superimposed motions:
    ///  - steady sway (sine wave, ramping in over the settle window, matching the ground pulse)
    ///  - tension shake (higher-frequency wave, peaks at lock-in and decays over ~1s)
    ///
    /// Both are damped to zero at the chain's endpoints via the parabolic `shape` factor, so the
    /// anchors stay pinned to their edges and the chain reads as vibrating *between* two points.
    /// </summary>
    private void BuildPath(StrandPath path, in Blueprint bp, Vector2 screenSize, float time, float age)
    {
        Vector2 start = bp.Start * screenSize;
        Vector2 end = bp.End * screenSize;
        Vector2 ctrl = bp.Control * screenSize;
        float sagBase = bp.Sag * screenSize.Y;

        float settle = Saturate((age - SettleStart) / (SettleEnd - SettleStart));
        float swayAmp = screenSize.Y * 0.014f * settle;
        float swayPhase = DrawHelpers.HashRange(bp.Seed, 0f, Tau);
        float swaySpeed = DrawHelpers.HashRange(bp.Seed + 1, 2.4f, 3.2f);

        // Tension shake: envelope starts at 1 the moment the chain locks and decays. Uses
        // sinceLock (not absolute time) so the shake starts at a predictable phase each cast.
        float sinceLock = age - (bp.Delay + ChainExtendSeconds);
        float shakeEnv = sinceLock > 0f ? MathF.Exp(-sinceLock / ShakeDecaySeconds) : 0f;
        float shakeAmp = screenSize.Y * ShakeAmplitudeFrac * shakeEnv;
        float shakePhase = sinceLock > 0f ? sinceLock : 0f;

        for (int i = 0; i < ChainSamples; i++)
        {
            float t = i / (float)(ChainSamples - 1);
            float mt = 1f - t;

            Vector2 p = mt * mt * start + 2f * mt * t * ctrl + t * t * end;

            float shape = 4f * t * (1f - t); // 0 at both ends, 1 at the middle
            p.Y += sagBase * shape;

            // Steady sway.
            p.Y += swayAmp * MathF.Sin(time * swaySpeed + t * 3.2f + swayPhase) * shape;

            // Tension shake: vertical primary, horizontal secondary (offset phase so it reads as
            // a 2D vibration, not a diagonal one).
            if (shakeEnv > 0.001f)
            {
                p.Y += shakeAmp * MathF.Cos(shakePhase * ShakeFrequency + t * 14f + swayPhase) * shape;
                p.X += shakeAmp * 0.55f * MathF.Sin(shakePhase * ShakeFrequency * 0.85f + t * 11f + swayPhase) * shape;
            }

            path.Points[i] = p;
        }

        path.Count = ChainSamples;
        path.BuildArc();
    }

    // =====================================================================================
    // Helpers
    // =====================================================================================

    private static Vector2 Normalize(Vector2 v)
    {
        float len = v.Length();
        return len > 1e-5f ? v / len : new Vector2(0f, 1f);
    }

    private static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    private static float EaseOutCubic(float t)
    {
        float u = 1f - Saturate(t);
        return 1f - u * u * u;
    }
}