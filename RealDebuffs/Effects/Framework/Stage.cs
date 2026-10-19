using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>Scene helpers shared by the effects: screen washes, edge light, ring strokes.</summary>
internal static class Stage
{
    /// <summary>Full-screen wash.</summary>
    public static void Wash(this EffectScene scene, Vector2 size, uint tint, float alpha, Vector4? color)
    {
        if (alpha <= 0.002f) return;
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = size, Tint = tint, Alpha = alpha, ColorOverride = color });
    }

    /// <summary>Light spilling in from all four screen edges.</summary>
    public static void EdgeSpill(this EffectScene scene, Vector2 size, float depth, uint tint, float alpha, Vector4? color)
    {
        if (alpha <= 0.002f) return;
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = new Vector2(size.X, depth), Tint = tint, Alpha = alpha, Top = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = new Vector2(0f, size.Y - depth), Max = size, Tint = tint, Alpha = alpha, Bottom = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = Vector2.Zero, Max = new Vector2(depth, size.Y), Tint = tint, Alpha = alpha, Left = true, ColorOverride = color });
        scene.AddRegion(new RegionPrimitive { Min = new Vector2(size.X - depth, 0f), Max = size, Tint = tint, Alpha = alpha, Right = true, ColorOverride = color });
    }

    /// <summary>A band of light or gloom pooled along the bottom edge.</summary>
    public static void Pool(this EffectScene scene, Vector2 size, float depth, uint tint, float alpha, Vector4? color)
    {
        if (alpha <= 0.002f || depth <= 1f) return;
        scene.AddRegion(new RegionPrimitive { Min = new Vector2(0f, size.Y - depth), Max = size, Tint = tint, Alpha = alpha, Bottom = true, ColorOverride = color });
    }

    /// <summary>A closed ring-style stroke that draws on as reveal rises; salt varies seed and phase per ring.</summary>
    public static void Ring(this EffectScene scene, StrandPath path, PrimitiveRole role, int seed, int salt, float width, float brightness,
                            float depth, float excite, float reveal, float dash, Vector4? color, float agitGain = 0.7f)
    {
        if (brightness <= 0.003f || reveal <= 0.001f) return;
        scene.AddStroke(new StrokePrimitive
        {
            Path = path, Role = role, Reveal = reveal, WidthHint = width, Brightness = brightness,
            Seed = seed + salt * 101, Phase = salt * 1.7f, Depth = depth, Agitation = DrawHelpers.Saturate(excite * agitGain),
            TipFlare = reveal < 0.999f ? 1f : 0f, Closed = true, Dash = dash, ColorOverride = color,
        });
    }

    /// <summary>Half the link count a chain gets round a hero ring, so width follows length and links never re-space.</summary>
    private const int HeroHalfLinks = 90;

    /// <summary>The hero binding ring: a glow halo under the main stroke, so any swapped material keeps the light.</summary>
    public static void HeroRing(this EffectScene scene, StrandPath path, int seed, float reveal, float brightness, float haloGain,
                                float depth, float mainDepth, float agitation, float tipFlare, Vector4? color, float haloScale = 2.4f)
    {
        float width = path.Length / (1.2f * HeroHalfLinks);
        scene.AddStroke(new StrokePrimitive
        {
            Path = path, Role = PrimitiveRole.Halo, Reveal = reveal, WidthHint = width * haloScale, Brightness = brightness * haloGain,
            Seed = seed + 21, Phase = 2f, Depth = depth, Agitation = agitation, Closed = true, ColorOverride = color,
        });
        scene.AddStroke(new StrokePrimitive
        {
            Path = path, Role = PrimitiveRole.MainStroke, Reveal = reveal, WidthHint = width, Brightness = brightness,
            Seed = seed + 22, Phase = 0.7f, Depth = mainDepth, Agitation = agitation, TipFlare = tipFlare, Closed = true, ColorOverride = color,
        });
    }
}

/// <summary>Motes filling the camera's view frustum: far ones small and sharp, near ones big soft discs; they rise and sway.</summary>
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

    /// <summary>dist is the camera distance in px; level scales brightness; blurDim dims near motes; twinkle (0..1) is how hard each one flickers.</summary>
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
            fy = ((fy + 1.12f) % 2.24f + 2.24f) % 2.24f - 1.12f;
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
