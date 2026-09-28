using System;
using System.Collections.Generic;
using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Spawns ambient particles along every stroke in the scene, using each stroke's resolved
/// material's declared emissions (or an emit-axis override). Runs after all effects have emitted,
/// before the renderer.
///
/// Owns a persistent pool: strokes are spawned fresh each frame based on arc position, and those
/// particles live out their full lifespan across many frames, integrating their own velocity.
/// The pool is a fixed-size static buffer; when it fills up, spawning gracefully stops until
/// slots free up. Brightness fades with age using ParticleEmitter.FadeFor, matching the ambient
/// particle pipeline that effects already use.
/// </summary>
public static class StrokeAutoEmitter
{
    private const int PoolCapacity = 2000;

    private struct PooledParticle
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

    private static readonly PooledParticle[] Pool = new PooledParticle[PoolCapacity];
    private static int _count;

    public static void Emit(EffectScene scene, float time, float dt,
                            IReadOnlyDictionary<string, string>? overrides)
    {
        // 1. Cull expired particles.
        int w = 0;
        for (int i = 0; i < _count; i++)
        {
            if (time - Pool[i].Born < Pool[i].Lifespan)
                Pool[w++] = Pool[i];
        }
        _count = w;

        // 2. Integrate live particles (with gravity).
        for (int i = 0; i < _count; i++)
        {
            Pool[i].Vel += Pool[i].Gravity * dt;
            Pool[i].Pos += Pool[i].Vel * dt;
        }

        // 3. Spawn new particles along every stroke in the scene.
        int strokeCount = scene.Strokes.Count;
        for (int i = 0; i < strokeCount; i++)
        {
            var s = scene.Strokes[i];
            if (s.Path.Count < 2 || s.Reveal <= 0.001f) continue;

            // Emit-axis override wins: use that particle material's spec and stamp its name onto
            // every spawned particle so the renderer uses it directly.
            string? emitOverride = overrides?.GetValueOrDefault(
                MaterialOverrideKey.ForStrokeEmit(s.Owner, s.Role));

            if (emitOverride is not null)
            {
                IParticleMaterial emitter;
                try { emitter = MaterialRegistry.GetParticle(emitOverride); }
                catch { continue; }

                if (emitter.Emission is not { } spec) continue;
                SpawnFromStroke(in s, spec, emitOverride, time, dt);
                continue;
            }

            // No override: use the stroke material's own declared emissions.
            string materialName = ResolveStrokeMaterial(in s, overrides);
            IStrokeMaterial material;
            try { material = MaterialRegistry.GetStroke(materialName); }
            catch { continue; }

            var emissions = material.Emissions;
            for (int e = 0; e < emissions.Length; e++)
                SpawnFromStroke(in s, emissions[e], forcedMaterial: null, time, dt);
        }

        // 4. Push every live particle into the scene for this frame's render.
        for (int i = 0; i < _count; i++)
        {
            ref readonly var p = ref Pool[i];
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
        float frac = expected - toSpawn;

        int slotSeed = unchecked(s.Seed + (int)(time * 90f) + (int)e.Role * 7919);
        if (DrawHelpers.Hash01(slotSeed) < frac) toSpawn++;
        if (toSpawn > 24) toSpawn = 24;
        if (toSpawn <= 0) return;

        for (int i = 0; i < toSpawn && _count < PoolCapacity; i++)
        {
            int seed = unchecked(s.Seed + (int)(time * 977f) + i * 131 + (int)e.Role * 7919);

            float t01 = DrawHelpers.Hash01(seed);
            float arc = visibleLen * t01;
            s.Path.SampleAtArc(arc, out Vector2 pos, out Vector2 tan);

            Vector2 baseDir;
            if (e.PrimaryDirection is { } pd && pd.LengthSquared() > 1e-6f)
                baseDir = Vector2.Normalize(pd);
            else
                baseDir = new Vector2(-tan.Y, tan.X);

            float spread = DrawHelpers.HashRange(seed + 1, -e.SpreadRadians, e.SpreadRadians);
            float ca = MathF.Cos(spread), sa = MathF.Sin(spread);
            Vector2 dir = new(baseDir.X * ca - baseDir.Y * sa, baseDir.X * sa + baseDir.Y * ca);

            float speed = DrawHelpers.HashRange(seed + 2, e.SpeedMin, e.SpeedMax);

            Pool[_count++] = new PooledParticle
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
                MaterialName = forcedMaterial,
                Owner = s.Owner,
                ColorOverride = s.ColorOverride,
            };
        }
    }

    private static string ResolveStrokeMaterial(in StrokePrimitive s, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides != null &&
            overrides.TryGetValue(MaterialOverrideKey.For(s.Owner, "Stroke", s.Role), out var name))
            return name;
        return BuiltInDefaults.Get(s.Owner, "Stroke", s.Role.ToString())
            ?? BuiltInDefaults.FallbackStroke();
    }
}