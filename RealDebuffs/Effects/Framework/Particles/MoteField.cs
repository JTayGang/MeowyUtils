using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>Motes filling the camera's frustum: far ones small and sharp, near ones big soft discs; they rise and sway.</summary>
internal sealed class MoteField
{
    private struct Mote { public float Fx, Fy, Z, Speed, Sway, Phase, Size; public int Seed; }

    private readonly Mote[] _m;
    private readonly float _near, _far, _speedLo, _speedHi, _swayLo, _swayHi, _sizeLo, _sizeHi;

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
            _m[i] = new Mote
            {
                Fx = DrawHelpers.HashRange(s + 2, -1.12f, 1.12f),
                Fy = DrawHelpers.HashRange(s + 3, -1.12f, 1.12f),
                Z = _far + (_near - _far) * MathF.Pow(DrawHelpers.Hash01(s + 1), 1.5f),
                Speed = DrawHelpers.HashRange(s + 4, _speedLo, _speedHi),
                Sway = DrawHelpers.HashRange(s + 5, _swayLo, _swayHi),
                Phase = DrawHelpers.Hash01(s + 6) * MathF.Tau,
                Size = DrawHelpers.HashRange(s + 7, _sizeLo, _sizeHi),
                Seed = s,
            };
        }
    }

    /// <summary>dist = camera distance px; level scales brightness; blurDim dims near motes; twinkle (0..1) flickers.</summary>
    public void Emit(EffectScene scene, in LayerCamera cam, float dist, Vector2 size, float px, float age,
                     float level, float blurDim, float twinkle, Vector4? color)
    {
        if (level <= 0.002f) return;
        float halfH = size.Y * 0.5f, halfW = size.X * 0.5f;

        foreach (var m in _m)
        {
            float z = m.Z * px;
            float s = dist / MathF.Max(dist * 0.12f, dist - z);

            // Rise at a world speed (near ones cross faster), wrapped in screen fractions.
            float fy = m.Fy - m.Speed * px * s * age / halfH;
            fy = DrawHelpers.Wrap(fy + 1.12f, 2.24f) - 1.12f;
            float fx = m.Fx + (m.Sway * px * s / halfW) * 0.5f * MathF.Sin(age * 0.43f + m.Phase);

            Vector2 at = cam.Project(fx * halfW / s, fy * halfH / s, z, out float scale);
            if (at.X < -80f || at.Y < -80f || at.X > size.X + 80f || at.Y > size.Y + 80f) continue;

            float blur = DrawHelpers.Smooth(150f, _near, m.Z);
            float far = DrawHelpers.Smooth(_far, 0f, m.Z);
            float flicker = 1f - twinkle + twinkle * DrawHelpers.Hash01(m.Seed * 13 + (int)(age * 11f + m.Phase * 3f));

            scene.AddParticle(new ParticlePrimitive
            {
                Position = at, Size = m.Size * px * scale * (1f + 3.4f * blur), AgeRatio = 0.5f,
                Brightness = level * flicker * (0.35f + 0.65f * far) * (1f - blurDim * blur),
                Seed = m.Seed, Role = PrimitiveRole.Mote, ColorOverride = color,
            });
        }
    }
}
