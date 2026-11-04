using System.Numerics;
using RealDebuffs.Effects.Framework.Materials;

namespace RealDebuffs.Effects.Framework;

/// <summary>Electricity: rust to burnt orange, amber and a pale yellow-white core (the vanilla paralysis spark).</summary>
internal static class StormColor
{
    /// <summary>Light, dim to hot: 0 = deep rust, 0.3 = burnt orange, 0.55 = vivid orange, 0.78 = amber, 1 = pale yellow-white.</summary>
    public static readonly Palette Glow = new(
        (0.00f, 0.34f, 0.080f, 0.010f),
        (0.30f, 0.78f, 0.300f, 0.020f),
        (0.55f, 1.00f, 0.560f, 0.070f),
        (0.78f, 1.00f, 0.800f, 0.200f),
        (1.00f, 1.00f, 0.975f, 0.760f));

    /// <summary>Edge tint for the screen's vignette: very dark umber.</summary>
    public static readonly uint Vignette = DrawHelpers.Pack(0.030f, 0.014f, 0.002f);

    /// <summary>The white-hot flash a strike lands with (yellow-tinged, so it stays chromatic under a colour override).</summary>
    public static readonly uint Flash = DrawHelpers.Pack(1.00f, 0.96f, 0.80f);
}

internal sealed class StormMaterials : IMaterialPack
{
    public IEnumerable<IMaterial> Materials { get; } = new IMaterial[]
    {
        // A charged cloud deck: dark umber lit to amber by flashes the effect drives via Brightness.
        new ParticlePuff("particle.thunderhead", new[] { "thunderhead", "thundercloud", "stormcloud", "storm" },
            new ParticlePuff.Look(
                Warm: DrawHelpers.Pack(0.95f, 0.62f, 0.24f), Cool: DrawHelpers.Pack(0.075f, 0.055f, 0.045f), CoolSpan: 0.60f,
                GrowFrom: 0.60f, GrowBy: 0.50f, FadeIn: 0.30f, FadeOutAt: 0.55f,
                Alpha: 0.36f, Squash: 0.55f, Irregular: 0.35f, MinSize: 2f,
                Highlight: DrawHelpers.Pack(1.00f, 0.86f, 0.56f), Thin: 0.12f),
            new StrokeEmission(Role: PrimitiveRole.Haze,
                DensityPer100px: 0.35f,
                SpeedMin: 4f, SpeedMax: 12f,
                LifespanMin: 2.4f, LifespanMax: 4.4f,
                SizeMin: 16f, SizeMax: 30f,
                SpreadRadians: 1.4f,
                BiasVelocity: new Vector2(0f, -6f),
                PrimaryDirection: new Vector2(0f, -1f))),
        new StrokeGlow("stroke.corona", StormColor.Glow, "corona", "flare", "flares"),
        new ParticleMote("particle.static", StormColor.Glow, false, "static", "plasma", "ions"),
    };
}
