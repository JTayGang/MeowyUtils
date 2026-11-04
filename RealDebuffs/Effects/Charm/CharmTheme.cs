using System.Numerics;
using RealDebuffs.Effects.Framework.Materials;

namespace RealDebuffs.Effects.Framework;

/// <summary>Charm: rose and blush light, a pearl core, and pink-lit fog from plum shadow to peach highlight.</summary>
internal static class CharmColor
{
    /// <summary>Light, dim to hot: 0 = deep plum, 0.3 = raspberry, 0.55 = rose, 0.78 = blush, 1 = pearl.</summary>
    public static readonly Palette Glow = new(
        (0.00f, 0.300f, 0.050f, 0.240f),
        (0.30f, 0.740f, 0.200f, 0.500f),
        (0.55f, 1.000f, 0.420f, 0.660f),
        (0.78f, 1.000f, 0.700f, 0.800f),
        (1.00f, 1.000f, 0.940f, 0.950f));

    /// <summary>Fog lit by that light: 0 = shadowed mauve, 0.4 = rose, 0.75 = lit pink, 1 = peach-pearl.</summary>
    public static readonly Palette Fog = new(
        (0.00f, 0.380f, 0.090f, 0.340f),
        (0.40f, 0.920f, 0.380f, 0.620f),
        (0.75f, 1.000f, 0.640f, 0.790f),
        (1.00f, 1.000f, 0.800f, 0.850f));

    /// <summary>Edge tint for the screen's vignette: deep rose-plum.</summary>
    public static readonly uint Vignette = DrawHelpers.Pack(0.360f, 0.050f, 0.240f);

    /// <summary>The flush a charm lands with (rose-white, so it stays chromatic under a colour override).</summary>
    public static readonly uint Flash = DrawHelpers.Pack(1.000f, 0.700f, 0.860f);
}

internal sealed class CharmMaterials : IMaterialPack
{
    public IEnumerable<IMaterial> Materials { get; } = new IMaterial[]
    {
        // Perfumed haze: a billow lit from within, plum in its folds and peach at its rims
        new ParticlePuff("particle.rosefog", new[] { "rosefog", "haze", "perfume", "incense", "blush" },
            new ParticlePuff.Look(
                Warm: DrawHelpers.Pack(0.72f, 0.26f, 0.47f), Cool: 0, CoolSpan: 0f,
                GrowFrom: 0.80f, GrowBy: 0.28f, FadeIn: 0.20f, FadeOutAt: 0.72f,
                Alpha: 0.50f, Squash: 0.74f, Irregular: 0.30f, MinSize: 6f, Ramp: CharmColor.Fog),
            new StrokeEmission(Role: PrimitiveRole.Mist,
                DensityPer100px: 0.30f,
                SpeedMin: 5f, SpeedMax: 12f,
                LifespanMin: 3.0f, LifespanMax: 5.0f,
                SizeMin: 40f, SizeMax: 80f,
                SpreadRadians: 1.4f,
                BiasVelocity: new Vector2(0f, -6f),
                PrimaryDirection: new Vector2(0f, -1f))),
        new ParticleWhisper("particle.whisper", CharmColor.Glow, CharmColor.Vignette, CharmWhispers.Phrases, "whisper", "whispers", "murmur", "murmurs", "words", "script"),
        new ParticleGlint("particle.twinkle", CharmColor.Glow.Sample(0.99f), CharmColor.Glow.Sample(0.62f), "twinkle", "twinkles", "starlight"),
        new ParticleMote("particle.stardust", CharmColor.Glow, true, "stardust", "pixie", "pixies", "faerie", "faeries"),
    };
}
