namespace RealDebuffs.Effects.Framework;

/// Motes filling the camera's frustum: far ones small and sharp, near ones big soft discs; they rise and sway.
internal sealed class MoteField
{
    struct Mote { public float Fx, Fy, Z, Speed, Sway, Phase, Size; public int Seed; }

    readonly Mote[] _m;
    readonly float _near, _far, _speedLo, _speedHi, _swayLo, _swayHi, _sizeLo, _sizeHi;

    public MoteField(int count, float near, float far, (float lo, float hi) speed, (float lo, float hi) sway, (float lo, float hi) size)
    {
        _m = new Mote[count];
        _near = near; _far = far;
        (_speedLo, _speedHi) = speed; (_swayLo, _swayHi) = sway; (_sizeLo, _sizeHi) = size;
    }

    public void Seed(int seed)
    {
        for (int i = 0; i < _m.Length; i++)
        {
            int s = seed + i * 6151 + 11;
            _m[i] = new()
            {
                Fx = HashRange(s + 2, -1.12f, 1.12f),
                Fy = HashRange(s + 3, -1.12f, 1.12f),
                Z = Lerp(_far, _near, Pow(Hash01(s + 1), 1.5f)),
                Speed = HashRange(s + 4, _speedLo, _speedHi),
                Sway = HashRange(s + 5, _swayLo, _swayHi),
                Phase = Hash01(s + 6) * Tau,
                Size = HashRange(s + 7, _sizeLo, _sizeHi),
                Seed = s,
            };
        }
    }

    /// dist = camera distance px; level scales brightness; blurDim dims near motes; twinkle (0..1) flickers.
    public void Emit(EffectScene scene, in LayerCamera cam, float dist, Vector2 size, float px, float age,
                     float level, float blurDim, float twinkle, Vector4? color)
    {
        if (level <= 0.002f) return;
        float halfH = size.Y * 0.5f, halfW = size.X * 0.5f;

        foreach (var m in _m)
        {
            float z = m.Z * px;
            float s = dist / Max(dist * 0.12f, dist - z);

            // Rise at a world speed (near ones cross faster), wrapped in screen fractions.
            float fy = m.Fy - m.Speed * px * s * age / halfH;
            fy = Wrap(fy + 1.12f, 2.24f) - 1.12f;
            float fx = m.Fx + (m.Sway * px * s / halfW) * 0.5f * Sin(age * 0.43f + m.Phase);

            Vector2 at = cam.Project(fx * halfW / s, fy * halfH / s, z, out float scale);
            if (at.X < -80f || at.Y < -80f || at.X > size.X + 80f || at.Y > size.Y + 80f) continue;

            float blur = Smooth(150f, _near, m.Z);
            float far = Smooth(_far, 0f, m.Z);
            float flicker = 1f - twinkle + twinkle * Hash01(m.Seed * 13 + (int)(age * 11f + m.Phase * 3f));

            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, Size = m.Size * px * scale * (1f + 3.4f * blur), AgeRatio = 0.5f,
                Brightness = level * flicker * (0.35f + 0.65f * far) * (1f - blurDim * blur),
                Seed = m.Seed, Role = PrimitiveRole.Mote, ColorOverride = color,
            });
        }
    }
}
