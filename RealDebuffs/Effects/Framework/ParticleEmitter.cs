using System;
using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// A small pool of short-lived particles. Same lifecycle as the old EdgeParticleField (spawn on
/// a timer, integrate, drop expired) but its output is now particle primitives written into an
/// EffectScene rather than direct draw calls. Materials decide how each one looks.
/// </summary>
public sealed class ParticleEmitter
{
    private struct Particle
    {
        public Vector2 Pos;
        public Vector2 Velocity;
        public float Born;
        public float Lifespan;
        public float Size;
        public int   Seed;
    }

    private readonly Particle[] _pool;
    private int _count;
    private float _nextSpawnAt;
    private readonly int _seedSalt;

    public ParticleEmitter(int maxParticles, int seedSalt)
    {
        _pool = new Particle[maxParticles];
        _seedSalt = seedSalt;
    }

    public int Count => _count;

    /// <summary>0 at spawn/despawn, 1 in the steady middle. Materials use this to fade each particle individually.</summary>
    public static float FadeFor(float age01) =>
        age01 < 0.25f ? age01 / 0.25f
        : age01 > 0.7f ? MathF.Max(0f, (1f - age01) / 0.3f)
        : 1f;

    /// <summary>Call once per frame while the owning effect is active. Spawns new particles and drops expired ones.</summary>
    public void Update(
        float time, float dt,
        float spawnIntervalMin, float spawnIntervalMax,
        Func<int, Vector2> spawnPos, Func<int, Vector2> spawnVelocity,
        float lifespanMin, float lifespanMax,
        float sizeMin, float sizeMax)
    {
        int w = 0;
        for (int i = 0; i < _count; i++)
        {
            if (time - _pool[i].Born < _pool[i].Lifespan)
                _pool[w++] = _pool[i];
        }
        _count = w;

        if (time >= _nextSpawnAt && _count < _pool.Length)
        {
            int seed = _seedSalt + (int)(time * 977f);
            _pool[_count] = new Particle
            {
                Pos = spawnPos(seed),
                Velocity = spawnVelocity(seed),
                Born = time,
                Lifespan = DrawHelpers.HashRange(seed + 1, lifespanMin, lifespanMax),
                Size = DrawHelpers.HashRange(seed + 2, sizeMin, sizeMax),
                Seed = seed,
            };
            _count++;
            _nextSpawnAt = time + DrawHelpers.HashRange(seed + 4, spawnIntervalMin, spawnIntervalMax);
        }

        for (int i = 0; i < _count; i++)
            _pool[i].Pos += _pool[i].Velocity * dt;
    }

    /// <summary>Pushes every live particle into the scene. Call after Update, before the frame's render pass.</summary>
    public void Emit(EffectScene scene, float time, PrimitiveRole role,
                     float brightnessMul = 1f, Vector4? colorOverride = null,
                     float swayPerParticle = 0f)
    {
        for (int i = 0; i < _count; i++)
        {
            ref readonly var p = ref _pool[i];
            float age = time - p.Born;
            float t01 = age / p.Lifespan;
            float fade = FadeFor(t01);

            float sway = swayPerParticle > 0f
                ? MathF.Sin(age * 3.4f + p.Seed * 0.37f) * swayPerParticle
                : 0f;

            scene.AddParticle(new ParticlePrimitive
            {
                Position = p.Pos,
                Velocity = p.Velocity,
                AgeRatio = t01,
                Size = p.Size,
                Brightness = fade * brightnessMul,
                Sway = sway,
                Seed = p.Seed,
                Role = role,
                ColorOverride = colorOverride,
            });
        }
    }

    /// <summary>
    /// Drops every live particle and resets the spawn timer. Call on effect recast so a fresh
    /// application doesn't inherit lingering particles from the previous one. Cheap; the pool
    /// array isn't reallocated.
    /// </summary>
    public void Clear()
    {
        _count = 0;
        _nextSpawnAt = 0f;
    }
}