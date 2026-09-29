using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Spawns and advances everything a stroke material sheds along its length: free-flying particles
/// (sparks, falling drips, embers) and path-following particles (drips running down the strand).
/// Both kinds come from a single StrokeEmission list — each emission carries an optional Flow
/// block that enables the path-following half — and both are resolved through the same material
/// lookup, so an emit-axis override ("chains with drips") reaches both.
///
/// Owns two internal pools: one for free-flying particles (position + velocity + gravity), one
/// for flowing particles (arc position along a parent stroke, direction, wobble, obstacle sync).
/// Both persist across frames and are culled as lifespans expire or (for flows) as parent
/// strokes disappear from the scene.
/// </summary>
public static class StrokeAutoEmitter
{
    // ---- Free-flying pool ----

    private const int FreeFlyCapacity = 2000;

    private struct FreeFlyParticle
    {
        public Vector2 Pos;
        public Vector2 Vel;
        public Vector2 Gravity;
        public float Born;
        public float Lifespan;
        public float Size;
        public float Brightness;
        public int Seed;
        public PrimitiveRole Role;
        public string? MaterialName;
        public DebuffKind Owner;
        public Vector4? ColorOverride;
    }

    private static readonly FreeFlyParticle[] FreeFlyPool = new FreeFlyParticle[FreeFlyCapacity];
    private static int _freeFlyCount;

    // ---- Flow pool ----

    private const int FlowCapacity = 600;

    private struct FlowingParticle
    {
        public DebuffKind Owner;
        public int   StrokeSeed;
        public string? MaterialName;

        public float Born;
        public float Lifespan;
        public float Size;
        public float Brightness;
        public PrimitiveRole Role;
        public Vector4? ColorOverride;

        public float Arc;
        public float BaseSpeed;
        public bool  FlowToTip;

        public float WobblePhase;
        public float WobbleFreq;
        public float WobbleAmp;
        public float ObstacleSpacing;
        public float LateralOffsetFrac;
        public int   SideSign;
    }

    private static readonly FlowingParticle[] FlowPool = new FlowingParticle[FlowCapacity];
    private static int _flowCount;

    // ---- Public entry point ----

    public static void Emit(EffectScene scene, float time, float dt,
                            IReadOnlyDictionary<string, string>? overrides)
    {
        // 1. Free-flying: cull, integrate, and (further down) spawn.
        CullFreeFly(time);
        IntegrateFreeFly(dt);

        // 2. Flowing: cull expired, advance along parents, and (further down) spawn.
        CullFlows(time);
        AdvanceFlows(scene, time, dt);

        // 3. Spawn from every stroke in the scene.
        for (int i = 0; i < scene.Strokes.Count; i++)
        {
            var s = scene.Strokes[i];
            if (s.Path.Count < 2 || s.Reveal <= 0.001f) continue;

            // Emit-axis override: forces every emission (free-flying and flowing) to use the
            // override material's spec. Falls through to the stroke material's own emissions if
            // no override is set.
            string? emitOverride = overrides?.GetValueOrDefault(
                MaterialOverrideKey.ForStrokeEmit(s.Owner, s.Role));

            if (emitOverride is not null)
            {
                var emitter = MaterialRegistry.TryGetParticle(emitOverride);
                if (emitter is null) continue;

                var specs = emitter.Emissions;
                for (int e = 0; e < specs.Length; e++)
                    SpawnFromStroke(in s, in specs[e], emitOverride, time, dt);
                continue;
            }

            string materialName = MaterialOverrideKey.ResolveStroke(in s, overrides);
            var material = MaterialRegistry.TryGetStroke(materialName);
            if (material is null) continue;

            var emissions = material.Emissions;
            for (int e = 0; e < emissions.Length; e++)
                SpawnFromStroke(in s, in emissions[e], forcedMaterial: null, time, dt);
        }

        // 4. Push every live free-flying particle into the scene for this frame's render.
        // Flowing particles were pushed during AdvanceFlows above.
        for (int i = 0; i < _freeFlyCount; i++)
        {
            ref readonly var p = ref FreeFlyPool[i];
            float age = time - p.Born;
            float t01 = age / p.Lifespan;
            float fade = ParticleEmitter.FadeFor(t01);

            scene.AddParticleForOwner(new ParticlePrimitive
            {
                Position = p.Pos,
                Velocity = p.Vel,
                AgeRatio = t01,
                Size = p.Size,
                Brightness = p.Brightness * fade,
                Seed = p.Seed,
                Role = p.Role,
                MaterialName = p.MaterialName,
                ColorOverride = p.ColorOverride,
            }, p.Owner);
        }
    }

    // ---- Spawning from a stroke ----

    private static void SpawnFromStroke(in StrokePrimitive s, in StrokeEmission e,
                                        string? forcedMaterial, float time, float dt)
    {
        float visibleLen = s.Path.Length * s.Reveal;
        if (visibleLen < 8f) return;

        float expected = e.DensityPer100px * visibleLen / 100f * dt;
        int toSpawn = (int)expected;
        if (DrawHelpers.Hash01(unchecked(s.Seed + (int)(time * 90f) + (int)e.Role * 7919)) < expected - toSpawn)
            toSpawn++;
        if (toSpawn > 24) toSpawn = 24;
        if (toSpawn <= 0) return;

        string? particleMaterial = e.RenderMaterial ?? forcedMaterial;

        bool flowToTip = false;
        if (e.Flow.HasValue)
        {
            s.Path.SampleAtArc(0f, out Vector2 basePos, out _);
            s.Path.SampleAtArc(s.Path.Length, out Vector2 tipPos, out _);
            flowToTip = tipPos.Y > basePos.Y;
        }

        // Cluster direction: computed once per SpawnFromStroke call, then shared by every
        // particle spawned from this emission this frame. When clustering is off, or no fixed
        // launch axis is declared, this stays null and each particle picks its own direction.
        Vector2? clusterBaseDir = null;
        if (e.ClusterWindowSeconds > 0f && e.PrimaryDirection is { } axis && axis.LengthSquared() > 1e-6f)
        {
            int bucket = (int)(time / e.ClusterWindowSeconds);
            int clusterSeed = unchecked(s.Seed * 7919 + bucket * 131 + (int)e.Role * 977);
            float gustAngle = DrawHelpers.HashRange(clusterSeed, -e.SpreadRadians, e.SpreadRadians);
            clusterBaseDir = Rotate(Vector2.Normalize(axis), gustAngle);
        }

        for (int i = 0; i < toSpawn; i++)
        {
            int seed = unchecked(s.Seed + (int)(time * 977f) + i * 131 + (int)e.Role * 7919);

            bool asFlow = e.Flow is { } flowOpts
                && DrawHelpers.Hash01(seed + 7) < flowOpts.Share;

            if (asFlow)
                SpawnFlow(in s, in e, e.Flow!.Value, particleMaterial, seed, visibleLen, flowToTip, time);
            else
                SpawnFreeFly(in s, in e, particleMaterial, seed, visibleLen, time, clusterBaseDir);
        }
    }

    private static void SpawnFreeFly(in StrokePrimitive s, in StrokeEmission e,
                                     string? particleMaterial, int seed, float visibleLen,
                                     float time, Vector2? clusterBaseDir)
    {
        if (_freeFlyCount >= FreeFlyCapacity) return;

        float arc = visibleLen * DrawHelpers.Hash01(seed);
        s.Path.SampleAtArc(arc, out Vector2 pos, out Vector2 tan);

        // Direction resolution:
        //  - In a cluster: gust direction is the base; per-particle jitter is ClusterConeRadians.
        //  - Otherwise: base is PrimaryDirection or the local perpendicular; jitter is
        //    SpreadRadians.
        Vector2 baseDir;
        float directionSpread;

        if (clusterBaseDir is { } gust)
        {
            baseDir = gust;
            directionSpread = e.ClusterConeRadians;
        }
        else
        {
            baseDir = e.PrimaryDirection is { } pd && pd.LengthSquared() > 1e-6f
                ? Vector2.Normalize(pd)
                : new Vector2(-tan.Y, tan.X);
            directionSpread = e.SpreadRadians;
        }

        float spread = DrawHelpers.HashRange(seed + 1, -directionSpread, directionSpread);
        Vector2 dir = Rotate(baseDir, spread);

        float speed = DrawHelpers.HashRange(seed + 2, e.SpeedMin, e.SpeedMax);

        FreeFlyPool[_freeFlyCount++] = new FreeFlyParticle
        {
            Pos = pos,
            Vel = dir * speed + e.BiasVelocity,
            Gravity = e.Gravity,
            Born = time,
            Lifespan = DrawHelpers.HashRange(seed + 3, e.LifespanMin, e.LifespanMax),
            Size = DrawHelpers.HashRange(seed + 4, e.SizeMin, e.SizeMax),
            Brightness = s.Brightness,
            Seed = seed,
            Role = e.Role,
            MaterialName = particleMaterial,
            Owner = s.Owner,
            ColorOverride = s.ColorOverride,
        };
    }

    private static Vector2 Rotate(Vector2 v, float radians)
    {
        float c = MathF.Cos(radians), sn = MathF.Sin(radians);
        return new Vector2(v.X * c - v.Y * sn, v.X * sn + v.Y * c);
    }

    private static void SpawnFlow(in StrokePrimitive s, in StrokeEmission e, in StrokeFlowOptions f,
                                  string? particleMaterial, int seed, float visibleLen, bool flowToTip, float time)
    {
        if (_flowCount >= FlowCapacity) return;

        float arc = visibleLen * DrawHelpers.Hash01(seed);

        FlowPool[_flowCount++] = new FlowingParticle
        {
            Owner = s.Owner,
            StrokeSeed = s.Seed,
            MaterialName = particleMaterial,

            Born = time,
            Lifespan = DrawHelpers.HashRange(seed + 1, e.LifespanMin, e.LifespanMax),
            Size = DrawHelpers.HashRange(seed + 2, e.SizeMin, e.SizeMax),
            Brightness = s.Brightness,
            Role = e.Role,
            ColorOverride = s.ColorOverride,

            Arc = arc,
            BaseSpeed = DrawHelpers.HashRange(seed + 3, f.SpeedMin, f.SpeedMax),
            FlowToTip = flowToTip,

            WobblePhase = DrawHelpers.HashRange(seed + 4, 0f, MathF.Tau),
            WobbleFreq = f.WobbleFrequencyHz,
            WobbleAmp = f.WobbleAmplitude,
            ObstacleSpacing = f.ObstacleSpacingPx,
            LateralOffsetFrac = f.LateralOffsetFrac,
            SideSign = DrawHelpers.Hash01(seed + 5) < 0.5f ? -1 : 1,
        };
    }

    // ---- Free-flying integration ----

    private static void CullFreeFly(float time)
    {
        int w = 0;
        for (int i = 0; i < _freeFlyCount; i++)
        {
            if (time - FreeFlyPool[i].Born < FreeFlyPool[i].Lifespan)
                FreeFlyPool[w++] = FreeFlyPool[i];
        }
        _freeFlyCount = w;
    }

    private static void IntegrateFreeFly(float dt)
    {
        for (int i = 0; i < _freeFlyCount; i++)
        {
            FreeFlyPool[i].Vel += FreeFlyPool[i].Gravity * dt;
            FreeFlyPool[i].Pos += FreeFlyPool[i].Vel * dt;
        }
    }

    // ---- Flow advancement ----

    private static void CullFlows(float time)
    {
        int w = 0;
        for (int i = 0; i < _flowCount; i++)
        {
            if (time - FlowPool[i].Born < FlowPool[i].Lifespan)
                FlowPool[w++] = FlowPool[i];
        }
        _flowCount = w;
    }

    private static void AdvanceFlows(EffectScene scene, float time, float dt)
    {
        for (int i = 0; i < _flowCount; i++)
        {
            ref var d = ref FlowPool[i];

            var stroke = FindStroke(scene, d.Owner, d.StrokeSeed);
            if (stroke is null) continue;

            var s = stroke.Value;
            var path = s.Path;
            float totalLen = path.Length * s.Reveal;
            if (totalLen < 1f) continue;

            float obstaclePhase = d.Arc / MathF.Max(1f, d.ObstacleSpacing) * MathF.Tau;
            float speedFactor = 0.6f + 0.4f * MathF.Cos(obstaclePhase);
            float currentSpeed = d.BaseSpeed * speedFactor;
            d.Arc += (d.FlowToTip ? 1f : -1f) * currentSpeed * dt;

            if (d.Arc < 0f) d.Arc = 0f;
            if (d.Arc > totalLen) d.Arc = totalLen;

            path.SampleAtArc(d.Arc, out Vector2 pos, out Vector2 tan);
            Vector2 perp = new(-tan.Y, tan.X);

            float halfWidth = MathF.Max(1f, s.WidthHint * 0.5f);
            float wobble = MathF.Sin(time * d.WobbleFreq * MathF.Tau + d.WobblePhase) * d.WobbleAmp;
            float lateral = (d.LateralOffsetFrac * halfWidth + wobble) * d.SideSign;
            Vector2 finalPos = pos + perp * lateral;

            float age = time - d.Born;
            float ageT = Math.Clamp(age / d.Lifespan, 0f, 1f);

            scene.AddParticleForOwner(new ParticlePrimitive
            {
                Position = finalPos,
                Velocity = tan * (d.FlowToTip ? currentSpeed : -currentSpeed),
                AgeRatio = ageT,
                Size = d.Size,
                Brightness = d.Brightness * ParticleEmitter.FadeFor(ageT),
                Seed = d.StrokeSeed + i * 7919,
                Role = d.Role,
                MaterialName = d.MaterialName,
                ColorOverride = d.ColorOverride,
            }, d.Owner);
        }
    }

    private static StrokePrimitive? FindStroke(EffectScene scene, DebuffKind owner, int seed)
    {
        for (int i = 0; i < scene.Strokes.Count; i++)
        {
            var s = scene.Strokes[i];
            if (s.Owner == owner && s.Seed == seed)
                return s;
        }
        return null;
    }
}