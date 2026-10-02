using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Spawns and advances everything a stroke material sheds along its length: free-flying particles
/// and path-following particles. Both come from a single StrokeEmission list; both resolve through
/// the same material lookup, so an emit-axis override reaches both.
/// </summary>
public static class StrokeAutoEmitter
{
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

    public static void Emit(EffectScene scene, float time, float dt,
                            IReadOnlyDictionary<string, string>? overrides)
    {
        CullFreeFly(time);
        IntegrateFreeFly(dt);

        CullFlows(time);
        AdvanceFlows(scene, time, dt);

        for (int i = 0; i < scene.Strokes.Count; i++)
        {
            var s = scene.Strokes[i];
            if (s.Path.Count < 2 || s.Reveal <= 0.001f) continue;

            // Emit-axis override forces every emission to use the override material's spec.
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

        // Impacts are spawned after the strokes so a burst born this frame is drawn this frame.
        for (int i = 0; i < scene.Impacts.Count; i++)
            SpawnImpact(scene.Impacts[i], overrides, time);

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

        // Cluster direction is computed once per call and shared by every particle this frame.
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

    /// <summary>
    /// Resolves what the impacted owner's stroke material throws and spawns it. Resolution mirrors
    /// the trickle emitters exactly, so a user override or a "made of X" phrase changes impacts
    /// along with everything else: a swapped-in material throws ITS debris, and a particle
    /// override on the emit axis ("chains made of flames") bursts that particle instead.
    /// </summary>
    private static void SpawnImpact(in ImpactPrimitive hit, IReadOnlyDictionary<string, string>? overrides, float time)
    {
        if (hit.Strength <= 0.001f) return;

        Vector2 dir = hit.Direction.LengthSquared() > 1e-6f
            ? Vector2.Normalize(hit.Direction)
            : new Vector2(0f, -1f);

        string? emitOverride = overrides?.GetValueOrDefault(
            MaterialOverrideKey.ForStrokeEmit(hit.Owner, hit.StrokeRole));

        if (emitOverride is not null)
        {
            var emitter = MaterialRegistry.TryGetParticle(emitOverride);
            if (emitter is null) return;

            // A particle material only declares a trickle, so derive a burst from it: a handful of
            // particles on the trickle's own speeds, lifespans and sizes, fanned at least ~40 deg.
            var specs = emitter.Emissions;
            for (int e = 0; e < specs.Length; e++)
            {
                ref readonly var t = ref specs[e];
                var burst = new ImpactEmission(
                    t.Role,
                    CountMin: 3, CountMax: 3 + Math.Clamp((int)(t.DensityPer100px * 2f), 1, 10),
                    t.SpeedMin, t.SpeedMax, t.LifespanMin, t.LifespanMax, t.SizeMin, t.SizeMax,
                    ConeRadians: MathF.Max(t.SpreadRadians, 0.7f),
                    Gravity: t.Gravity,
                    RenderMaterial: t.RenderMaterial ?? emitOverride);
                SpawnBurst(in hit, dir, in burst, e, time);
            }
            return;
        }

        var material = MaterialRegistry.TryGetStroke(
            MaterialOverrideKey.ResolveStroke(hit.Owner, hit.StrokeRole, overrides));
        if (material is null) return;

        var impacts = material.ImpactEmissions;
        for (int e = 0; e < impacts.Length; e++)
            SpawnBurst(in hit, dir, in impacts[e], e, time);
    }

    private static void SpawnBurst(in ImpactPrimitive hit, Vector2 dir, in ImpactEmission e, int salt, float time)
    {
        float k = Math.Clamp(hit.Strength, 0f, 1f);

        int seed0 = unchecked(hit.Seed + (int)e.Role * 7919 + salt * 4421);
        float countF = DrawHelpers.HashRange(seed0, e.CountMin, e.CountMax + 0.999f);
        int count = Math.Max(1, (int)(countF * (0.35f + 0.65f * k)));

        float brightness = hit.Brightness > 0f ? hit.Brightness : 1f;

        for (int i = 0; i < count; i++)
        {
            if (_freeFlyCount >= FreeFlyCapacity) return;

            int seed = unchecked(seed0 + i * 131);
            Vector2 d = Rotate(dir, DrawHelpers.HashRange(seed + 1, -e.ConeRadians, e.ConeRadians));
            float speed = DrawHelpers.HashRange(seed + 2, e.SpeedMin, e.SpeedMax) * (0.55f + 0.45f * k);

            Vector2 jitter = new(DrawHelpers.HashRange(seed + 5, -3f, 3f), DrawHelpers.HashRange(seed + 6, -3f, 3f));

            FreeFlyPool[_freeFlyCount++] = new FreeFlyParticle
            {
                Pos = hit.Position + jitter,
                Vel = d * speed,
                Gravity = e.Gravity,
                Born = time,
                Lifespan = DrawHelpers.HashRange(seed + 3, e.LifespanMin, e.LifespanMax),
                Size = DrawHelpers.HashRange(seed + 4, e.SizeMin, e.SizeMax),
                Brightness = brightness,
                Seed = seed,
                Role = e.Role,
                MaterialName = e.RenderMaterial,
                Owner = hit.Owner,
                ColorOverride = hit.ColorOverride,
            };
        }
    }

    private static void SpawnFreeFly(in StrokePrimitive s, in StrokeEmission e,
                                     string? particleMaterial, int seed, float visibleLen,
                                     float time, Vector2? clusterBaseDir)
    {
        if (_freeFlyCount >= FreeFlyCapacity) return;

        float arc = visibleLen * DrawHelpers.Hash01(seed);
        s.Path.SampleAtArc(arc, out Vector2 pos, out Vector2 tan);

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