using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// A small pool of short-lived particles. Three usage modes:
///  - Interval emission: call Update(...) each frame.
///  - Burst-only: call Burst(...) on an event, plus UpdateBurstOnly(...) each frame.
///  - Mixed: Update for ambient, Burst for one-shot moments.
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

    public Vector2 Gravity;
    public float Drag;
    public float Wander;
    public float WanderHz = 0.9f;

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

    /// <summary>0 at spawn/despawn, 1 in the steady middle.</summary>
    public static float FadeFor(float age01) =>
        age01 < 0.25f ? age01 / 0.25f
        : age01 > 0.7f ? MathF.Max(0f, (1f - age01) / 0.3f)
        : 1f;

    /// <summary>Interval emission: spawns on a timer and integrates existing particles.</summary>
    public void Update(
        float time, float dt,
        float spawnIntervalMin, float spawnIntervalMax,
        Func<int, Vector2> spawnPos, Func<int, Vector2> spawnVelocity,
        float lifespanMin, float lifespanMax,
        float sizeMin, float sizeMax)
    {
        Integrate(time, dt);

        if (time >= _nextSpawnAt && _count < _pool.Length)
        {
            int seed = _seedSalt + (int)(time * 977f);
            _pool[_count++] = MakeParticle(seed, time,
                spawnPos(seed), spawnVelocity(seed),
                lifespanMin, lifespanMax, sizeMin, sizeMax);
            _nextSpawnAt = time + DrawHelpers.HashRange(seed + 4, spawnIntervalMin, spawnIntervalMax);
        }
    }

    /// <summary>Burst-only: integrates existing particles without spawning anything.</summary>
    public void UpdateBurstOnly(float time, float dt) => Integrate(time, dt);

    /// <summary>One-shot spawn. Each particle gets a hashed seed so two bursts at the same time differ.</summary>
    public void Burst(int count, float time,
                      Func<int, Vector2> spawnPos, Func<int, Vector2> spawnVelocity,
                      float lifespanMin, float lifespanMax,
                      float sizeMin, float sizeMax)
    {
        for (int i = 0; i < count && _count < _pool.Length; i++)
        {
            int seed = _seedSalt + (int)(time * 977f) + i * 131 + _count;
            _pool[_count++] = MakeParticle(seed, time,
                spawnPos(seed), spawnVelocity(seed),
                lifespanMin, lifespanMax, sizeMin, sizeMax);
        }
    }

    /// <summary>Pushes every live particle into the scene.</summary>
    public void Emit(EffectScene scene, float time, PrimitiveRole role,
                     float brightnessMul = 1f, Vector4? colorOverride = null,
                     float swayPerParticle = 0f, int variant = 0)
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
                Variant = variant,
            });
        }
    }

    /// <summary>Drops every live particle and resets the spawn timer.</summary>
    public void Clear()
    {
        _count = 0;
        _nextSpawnAt = 0f;
    }

    private Particle MakeParticle(int seed, float time, Vector2 pos, Vector2 vel,
                                  float lifespanMin, float lifespanMax,
                                  float sizeMin, float sizeMax) => new()
    {
        Pos = pos,
        Velocity = vel,
        Born = time,
        Lifespan = DrawHelpers.HashRange(seed + 1, lifespanMin, lifespanMax),
        Size = DrawHelpers.HashRange(seed + 2, sizeMin, sizeMax),
        Seed = seed,
    };

    private void Integrate(float time, float dt)
    {
        int w = 0;
        for (int i = 0; i < _count; i++)
        {
            if (time - _pool[i].Born < _pool[i].Lifespan)
                _pool[w++] = _pool[i];
        }
        _count = w;

        bool shaped = Wander > 0f || Drag > 0f;
        float dragK = Drag > 0f ? MathF.Max(0f, 1f - Drag * dt) : 1f;

        for (int i = 0; i < _count; i++)
        {
            _pool[i].Velocity += Gravity * dt;

            if (shaped)
            {
                if (Wander > 0f)
                {
                    float ph = time * WanderHz * MathF.Tau + _pool[i].Seed * 0.61f;
                    float push = MathF.Sin(ph) * 0.65f + MathF.Sin(ph * 0.37f + _pool[i].Seed * 1.7f) * 0.35f;
                    _pool[i].Velocity.X += push * Wander * dt;
                }
                _pool[i].Velocity *= dragK;
            }

            _pool[i].Pos += _pool[i].Velocity * dt;
        }
    }
}