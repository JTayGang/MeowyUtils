using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>A fixed pool of wrapping flakes blown by gusting wind: steady density, constant cost, no allocation after Build. Near flakes are big and fast.</summary>
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

    /// <summary>Gust strength 0..1 (three incommensurate waves, squashed into lulls and gales). Pass seconds since the effect began: large clocks lose Sin precision.</summary>
    public static float Gust(float time, float seed)
    {
        float g = 0.62f
                + 0.26f * MathF.Sin(time * 0.29f + seed)
                + 0.17f * MathF.Sin(time * 0.71f + seed * 1.9f)
                + 0.09f * MathF.Sin(time * 1.70f + seed * 3.7f);
        return DrawHelpers.Smooth(g);
    }

    /// <summary>Advances every flake; wind is px/s at full gust (screen-scaled), gust 0..1, px scales settling and eddy motion.</summary>
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

    /// <summary>Pushes flakes into the scene; density (0..1) is the fraction out, the last fading in. Off-screen flakes are skipped.</summary>
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
