namespace RealDebuffs.Effects.Framework;

/// A fixed pool of wrapping flakes blown by gusting wind: steady density, constant cost, no allocation after Build.
internal sealed class WindField
{
    struct Flake
    {
        public Vector2 Pos, Vel;
        public float Depth;  // 0 far .. 1 near
        public float Phase;
        public int Seed;
    }

    Flake[] _flakes = [];

    /// Lays out count flakes across the screen. Reuses the array when the count is unchanged.
    public void Build(int count, int seed, Vector2 size)
    {
        count = Math.Max(0, count);
        if (_flakes.Length != count) _flakes = new Flake[count];

        for (int i = 0; i < count; i++)
        {
            int s = seed + i * 7919;
            _flakes[i] = new()
            {
                Pos = new(Hash01(s + 2) * size.X, Hash01(s + 3) * size.Y),
                Depth = Pow(Hash01(s + 1), 1.7f),  // far flakes outnumber near ones
                Phase = Hash01(s + 4) * Tau,
                Seed = s,
            };
        }
    }

    /// Gust strength 0..1 (three incommensurate waves, squashed into lulls and gales).
    public static float Gust(float time, float seed)
    {
        float g = 0.62f
                + 0.26f * Sin(time * 0.29f + seed)
                + 0.17f * Sin(time * 0.71f + seed * 1.9f)
                + 0.09f * Sin(time * 1.70f + seed * 3.7f);
        return Smooth(g);
    }

    /// Advances every flake; wind is px/s at full gust (screen-scaled), gust 0..1, px scales settling and eddy motion.
    public void Update(float time, float dt, Vector2 size, float px, Vector2 wind, float gust)
    {
        if (_flakes.Length == 0 || dt <= 0f) return;

        float margin = 48f * px;
        float w = size.X + 2f * margin, h = size.Y + 2f * margin;
        Vector2 drive = wind * (0.30f + 0.70f * gust);  // a lull still drifts

        for (int i = 0; i < _flakes.Length; i++)
        {
            ref Flake f = ref _flakes[i];
            float d = f.Depth;
            float flutter = Sin(time * (1.3f + 1.4f * d) + f.Phase);

            Vector2 v = drive * (0.40f + 1.00f * d) * (1f + 0.10f * flutter);  // parallax: near is fast
            v.Y += (18f + 46f * d) * px * (1f + 0.5f * flutter);  // settling, with a flutter
            v.Y += Sin(time * 2.1f + f.Phase * 3.1f) * (26f + 70f * d) * px * gust;  // eddies in a gust

            f.Vel = v;
            f.Pos += v * dt;
            f.Pos.X = Wrap(f.Pos.X + margin, w) - margin;
            f.Pos.Y = Wrap(f.Pos.Y + margin, h) - margin;
        }
    }

    /// Pushes flakes into the scene; density (0..1) is the fraction out, the last fading in.
    public void Emit(EffectScene scene, Vector2 size, float px, PrimitiveRole role, float density,
                     float brightness, Vector4? colorOverride)
    {
        float active = Saturate(density) * _flakes.Length;
        int n = Math.Min(_flakes.Length, (int)Ceiling(active));
        float cull = 40f * px;

        for (int i = 0; i < n; i++)
        {
            ref readonly Flake f = ref _flakes[i];
            if (f.Pos.X < -cull || f.Pos.X > size.X + cull || f.Pos.Y < -cull || f.Pos.Y > size.Y + cull) continue;

            float d = f.Depth;
            float on = Saturate(active - i);
            float flakeSize = (1.3f + 5.2f * d) * (0.85f + 0.30f * Hash01(f.Seed + 5)) * px;

            scene.AddParticle(new ParticlePrimitive
            {
                Position = f.Pos,
                Velocity = f.Vel,
                AgeRatio = 0.5f,  // the steady middle of a particle's life: full size
                Size = flakeSize,
                Brightness = brightness * on * (0.60f + 0.40f * d),
                Seed = f.Seed,
                Role = role,
                ColorOverride = colorOverride,
            });
        }
    }
}
