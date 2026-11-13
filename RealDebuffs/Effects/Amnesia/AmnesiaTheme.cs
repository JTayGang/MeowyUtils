using System.Numerics;
using RealDebuffs.Effects.Framework.Materials;

namespace RealDebuffs.Effects.Framework;

/// <summary>Amnesia: milky lilac fog and pearl light over deep indigo ink.</summary>
internal static class MemoryColor
{
    /// <summary>Light, dim to hot: 0 = indigo ink, 0.3 = periwinkle, 0.55 = lavender, 0.78 = pearl lilac, 1 = warm pearl.</summary>
    public static readonly Palette Glow = new(
        (0.00f, 0.100f, 0.100f, 0.270f),
        (0.30f, 0.300f, 0.330f, 0.620f),
        (0.55f, 0.580f, 0.620f, 0.880f),
        (0.78f, 0.820f, 0.860f, 0.980f),
        (1.00f, 1.000f, 0.980f, 0.960f));

    /// <summary>Fog lit from within: 0 = slate indigo shadow, 0.4 = periwinkle, 0.75 = milky lilac, 1 = pearl.</summary>
    public static readonly Palette Fog = new(
        (0.00f, 0.420f, 0.430f, 0.620f),
        (0.40f, 0.660f, 0.680f, 0.860f),
        (0.75f, 0.880f, 0.890f, 0.980f),
        (1.00f, 0.990f, 0.980f, 0.990f));

    /// <summary>Script: soft cool grey, dim to light, so the words sit in the fog instead of glowing on it.</summary>
    public static readonly Palette Murmur = new(
        (0.00f, 0.300f, 0.310f, 0.350f),
        (0.30f, 0.480f, 0.490f, 0.550f),
        (0.55f, 0.640f, 0.650f, 0.710f),
        (0.78f, 0.780f, 0.790f, 0.840f),
        (1.00f, 0.900f, 0.900f, 0.930f));

    /// <summary>The shade behind script: slate grey.</summary>
    public static readonly uint MurmurInk = DrawHelpers.Pack(0.300f, 0.310f, 0.360f);

    /// <summary>The deep tone of the spiral's ink arms.</summary>
    public static readonly uint Ink = DrawHelpers.Pack(0.070f, 0.070f, 0.200f);

    /// <summary>Edge tint for the screen's vignette: pale lilac, so the edges bleach rather than darken.</summary>
    public static readonly uint Vignette = DrawHelpers.Pack(0.800f, 0.820f, 0.940f);
}

internal sealed class MemoryMaterials : IMaterialPack
{
    public IEnumerable<IMaterial> Materials { get; } = new IMaterial[]
    {
        // Milky haze: a billow of pearl and lilac, slate in its folds
        new ParticlePuff("particle.pearlfog", new[] { "pearlfog", "milk", "cloud", "clouds", "brume" },
            new ParticlePuff.Look(
                Warm: DrawHelpers.Pack(0.84f, 0.86f, 0.96f), Cool: 0, CoolSpan: 0f,
                GrowFrom: 0.80f, GrowBy: 0.28f, FadeIn: 0.20f, FadeOutAt: 0.72f,
                Alpha: 0.56f, Squash: 0.78f, Irregular: 0.30f, MinSize: 6f, Ramp: MemoryColor.Fog),
            new StrokeEmission(Role: PrimitiveRole.Mist,
                DensityPer100px: 0.30f,
                SpeedMin: 5f, SpeedMax: 12f,
                LifespanMin: 3.0f, LifespanMax: 5.0f,
                SizeMin: 40f, SizeMax: 80f,
                SpreadRadians: 1.4f,
                BiasVelocity: new Vector2(0f, -6f),
                PrimaryDirection: new Vector2(0f, -1f))),
        new ParticleWhisper("particle.fugue", MemoryColor.Murmur, MemoryColor.MurmurInk, AmnesiaWhispers.Phrases, "fugue", "forgotten", "oblivion", "mumbling"),
        new ParticleMote("particle.lethe", MemoryColor.Glow, true, "lethe", "memory", "memories", "recollections"),
    };
}
