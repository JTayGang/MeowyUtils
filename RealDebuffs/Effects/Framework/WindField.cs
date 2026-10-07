using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// A fixed pool of flakes blown across the screen by a gusting wind. Where ParticleEmitter spawns
/// particles that live, fade and die, these wrap around the screen, so the density is steady
/// across the whole width (nothing dims at the edges), the cost is the same every frame, the count
/// doesn't depend on the frame rate, and nothing is allocated after <see cref="Build"/>.
///
/// Each flake has a depth. Near ones are big, bright and fast; far ones small, dim and slow, which is
/// most of what makes a flat sheet of dots read as a blizzard. The wind is one vector plus a gust
/// strength (see <see cref="Gust"/>): in a lull the flakes drift and fall, in a gust they streak
/// across (the snow material stretches fast flakes along their velocity).
///
/// Not thread-safe; the owning effect updates and emits it from the draw thread.
/// </summary>
internal sealed class WindField
{
    private struct Flake
    {
        public Vector2 Pos, Vel;
        public float Depth;     // 0 far .. 1 near
        public float Phase;
        public int Seed;
    }

    private Flake[] _flakes = Array.Empty<Flake>();

    public int Count => _flakes.Length;

    /// <summary>Lays out <paramref name="count"/> flakes across the screen. Reuses the array when the count is unchanged.</summary>
    public void Build(int count, int seed, Vector2 size)
    {
        count = Math.Max(0, count);
        if (_flakes.Length != count) _flakes = new Flake[count];

        for (int i = 0; i < count; i++)
        {
            int s = seed + i * 7919;
            _flakes[i] = new Flake
            {
                Pos = new Vector2(DrawHelpers.Hash01(s + 2) * size.X, DrawHelpers.Hash01(s + 3) * size.Y),
                Depth = MathF.Pow(DrawHelpers.Hash01(s + 1), 1.7f),          // far flakes outnumber near ones
                Phase = DrawHelpers.Hash01(s + 4) * MathF.Tau,
                Seed = s,
            };
        }
    }

    /// <summary>
    /// A slowly gusting strength in 0..1: three incommensurate waves, squashed so lulls and gales each
    /// last a while rather than the wind hovering at a middling breeze (about 15% of the time it is calm, nearly half of it blowing hard). <paramref name="time"/> should be
    /// seconds since the effect began (large absolute clock values lose float precision in Sin).
    /// </summary>
    public static float Gust(float time, float seed)
    {
        float g = 0.62f
                + 0.26f * MathF.Sin(time * 0.29f + seed)
                + 0.17f * MathF.Sin(time * 0.71f + seed * 1.9f)
                + 0.09f * MathF.Sin(time * 1.70f + seed * 3.7f);
        g = Math.Clamp(g, 0f, 1f);
        return g * g * (3f - 2f * g);
    }

    /// <summary>
    /// Advances every flake. <paramref name="wind"/> is the wind's velocity in px/s at full gust (already
    /// scaled for the screen); <paramref name="gust"/> is 0..1; <paramref name="px"/> scales the settling
    /// and eddy motion with the screen.
    /// </summary>
    public void Update(float time, float dt, Vector2 size, float px, Vector2 wind, float gust)
    {
        if (_flakes.Length == 0 || dt <= 0f) return;

        float margin = 48f * px;
        float w = size.X + 2f * margin, h = size.Y + 2f * margin;
        Vector2 drive = wind * (0.30f + 0.70f * gust);          // a lull still drifts

        for (int i = 0; i < _flakes.Length; i++)
        {
            ref Flake f = ref _flakes[i];
            float d = f.Depth;
            float flutter = MathF.Sin(time * (1.3f + 1.4f * d) + f.Phase);

            Vector2 v = drive * (0.40f + 1.00f * d) * (1f + 0.10f * flutter);   // parallax: near is fast
            v.Y += (18f + 46f * d) * px * (1f + 0.5f * flutter);                // settling, with a flutter
            v.Y += MathF.Sin(time * 2.1f + f.Phase * 3.1f) * (26f + 70f * d) * px * gust;   // eddies in a gust

            f.Vel = v;
            f.Pos += v * dt;
            f.Pos.X = Wrap(f.Pos.X + margin, w) - margin;
            f.Pos.Y = Wrap(f.Pos.Y + margin, h) - margin;
        }
    }

    /// <summary>
    /// Pushes the flakes into the scene. <paramref name="density"/> (0..1) is the fraction of the pool
    /// that is out; the last one fades in rather than popping, so density can ramp smoothly. Flakes
    /// off screen are skipped.
    /// </summary>
    public void Emit(EffectScene scene, Vector2 size, float px, PrimitiveRole role, float density,
                     float brightness, Vector4? colorOverride)
    {
        float active = Math.Clamp(density, 0f, 1f) * _flakes.Length;
        int n = Math.Min(_flakes.Length, (int)MathF.Ceiling(active));
        float cull = 40f * px;

        for (int i = 0; i < n; i++)
        {
            ref readonly Flake f = ref _flakes[i];
            if (f.Pos.X < -cull || f.Pos.X > size.X + cull || f.Pos.Y < -cull || f.Pos.Y > size.Y + cull) continue;

            float d = f.Depth;
            float on = Math.Clamp(active - i, 0f, 1f);
            float flakeSize = (1.3f + 5.2f * d) * (0.85f + 0.30f * DrawHelpers.Hash01(f.Seed + 5)) * px;

            scene.AddParticle(new ParticlePrimitive
            {
                Position = f.Pos,
                Velocity = f.Vel,
                AgeRatio = 0.5f,                                // the steady middle of a particle's life: full size
                Size = flakeSize,
                Brightness = brightness * on * (0.60f + 0.40f * d),
                Seed = f.Seed,
                Role = role,
                ColorOverride = colorOverride,
            });
        }
    }

    private static float Wrap(float v, float len)
    {
        v %= len;
        return v < 0f ? v + len : v;
    }
}
