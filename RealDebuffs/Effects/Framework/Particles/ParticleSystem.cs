namespace RealDebuffs.Effects.Framework;

public readonly record struct FloatRange(float Min, float Max)
{
    public static implicit operator FloatRange((float Min, float Max) t) => new(t.Min, t.Max);
}

/// How a system moves its particles: gravity (px/s^2), drag (1/s), and a meandering sideways push (px/s^2) at WanderHz.
public readonly record struct ParticlePhysics(Vector2 Gravity = default, float Drag = 0f, float Wander = 0f, float WanderHz = 0.9f);

/// Where a new particle appears and how it moves (both asked of its seed), and its lifespan (s) and size (px) ranges.
public readonly record struct ParticleSpawn(Func<int, Vector2> Position, Func<int, Vector2> Velocity, FloatRange Life, FloatRange Size);

/// How a system is shown this frame: one role, dimmed and tinted by the effect.
public readonly record struct ParticleStyle(PrimitiveRole Role, float Brightness = 1f, Vector4? Color = null, float Sway = 0f, int Variant = 0);

/// Fixed-size pool of short-lived particles; effects own one each, the stroke shed the shared one.
public sealed class ParticleSystem
{
    public struct Particle
    {
        public Vector2 Pos, Vel, Gravity;
        public float Born, Life, Size, Brightness;
        public int Seed;
        public PrimitiveRole Role;
        public DebuffKind Owner;
        public string? Material;
        public Vector4? Color;
    }

    readonly Particle[] _pool;
    readonly ParticlePhysics _physics;
    readonly int _seedSalt;
    int _count;
    float _nextSpawnAt;

    public ParticleSystem(int capacity, int seedSalt = 0, ParticlePhysics physics = default)
    {
        _pool = new Particle[capacity];
        _seedSalt = seedSalt;
        _physics = physics;
    }

    public bool Full => _count >= _pool.Length;
    public int Count => _count;

    /// 0 at spawn/despawn, 1 in the steady middle.
    public static float FadeFor(float age01) =>
        age01 < 0.25f ? age01 / 0.25f
        : age01 > 0.7f ? Max(0f, (1f - age01) / 0.3f)
        : 1f;

    /// Adds a fully specified particle; ignored when the pool is full.
    public void Add(in Particle p)
    {
        if (_count < _pool.Length) _pool[_count++] = p;
    }

    /// Steps the system and spawns one particle whenever the random interval has elapsed.
    public void Stream(float time, float dt, FloatRange interval, in ParticleSpawn spawn)
    {
        Step(time, dt);

        if (time >= _nextSpawnAt && _count < _pool.Length)
        {
            int seed = _seedSalt + (int)(time * 977f);
            _pool[_count++] = Make(seed, time, in spawn);
            _nextSpawnAt = time + HashRange(seed + 4, interval.Min, interval.Max);
        }
    }

    /// One-shot spawn; each particle gets its own seed, so two bursts at the same time differ.
    public void Burst(int count, float time, in ParticleSpawn spawn)
    {
        for (int i = 0; i < count && _count < _pool.Length; i++)
        {
            int seed = _seedSalt + (int)(time * 977f) + i * 131 + _count;
            _pool[_count++] = Make(seed, time, in spawn);
        }
    }

    /// Retires expired particles, then moves the rest.
    public void Step(float time, float dt)
    {
        int w = 0;
        for (int i = 0; i < _count; i++)
            if (time - _pool[i].Born < _pool[i].Life)
                _pool[w++] = _pool[i];
        _count = w;

        bool shaped = _physics.Wander > 0f || _physics.Drag > 0f;
        float dragK = _physics.Drag > 0f ? Max(0f, 1f - _physics.Drag * dt) : 1f;

        for (int i = 0; i < _count; i++)
        {
            ref Particle p = ref _pool[i];
            p.Vel += p.Gravity * dt;

            if (shaped)
            {
                if (_physics.Wander > 0f)
                {
                    float ph = time * _physics.WanderHz * Tau + p.Seed * 0.61f;
                    float push = Sin(ph) * 0.65f + Sin(ph * 0.37f + p.Seed * 1.7f) * 0.35f;
                    p.Vel.X += push * _physics.Wander * dt;
                }
                p.Vel *= dragK;
            }

            p.Pos += p.Vel * dt;
        }
    }

    /// Hands every particle to the scene as one role, owned by the effect currently emitting.
    public void Emit(EffectScene scene, float time, in ParticleStyle style)
    {
        for (int i = 0; i < _count; i++)
        {
            ref readonly Particle p = ref _pool[i];
            float age = time - p.Born;
            float t01 = age / p.Life;

            scene.AddParticle(new ParticlePrimitive
            {
                Position = p.Pos,
                Velocity = p.Vel,
                AgeRatio = t01,
                Size = p.Size,
                Brightness = p.Brightness * FadeFor(t01) * style.Brightness,
                Sway = style.Sway > 0f ? Sin(age * 3.4f + p.Seed * 0.37f) * style.Sway : 0f,
                Seed = p.Seed,
                Role = style.Role,
                ColorOverride = style.Color,
                Variant = style.Variant,
            });
        }
    }

    /// Hands every particle to the scene with the role, owner, material and colour it was added with.
    public void EmitOwned(EffectScene scene, float time)
    {
        for (int i = 0; i < _count; i++)
        {
            ref readonly Particle p = ref _pool[i];
            float t01 = (time - p.Born) / p.Life;

            scene.AddParticleForOwner(new ParticlePrimitive
            {
                Position = p.Pos,
                Velocity = p.Vel,
                AgeRatio = t01,
                Size = p.Size,
                Brightness = p.Brightness * FadeFor(t01),
                Seed = p.Seed,
                Role = p.Role,
                MaterialName = p.Material,
                ColorOverride = p.Color,
            }, p.Owner);
        }
    }

    /// Drops every particle and resets the spawn timer.
    public void Clear()
    {
        _count = 0;
        _nextSpawnAt = 0f;
    }

    Particle Make(int seed, float time, in ParticleSpawn s) => new()
    {
        Pos = s.Position(seed),
        Vel = s.Velocity(seed),
        Gravity = _physics.Gravity,
        Born = time,
        Life = HashRange(seed + 1, s.Life.Min, s.Life.Max),
        Size = HashRange(seed + 2, s.Size.Min, s.Size.Max),
        Brightness = 1f,
        Seed = seed,
    };
}
