using System.Numerics;
using RealDebuffs.Effects.Framework.Materials;

namespace RealDebuffs.Effects.Framework;

/// <summary>Hellfire and blood: dried-blood black through crimson and arterial red to ember orange and a pale gold core.</summary>
internal static class HellColor
{
    /// <summary>Dim to hot: 0 dried-blood black, 0.25 crimson, 0.5 blood red, 0.72 red-orange, 0.88 orange, 1 pale gold.</summary>
    public static readonly Palette Glow = new(
        (0.00f, 0.170f, 0.008f, 0.010f),
        (0.25f, 0.520f, 0.024f, 0.020f),
        (0.50f, 0.920f, 0.095f, 0.026f),
        (0.72f, 1.000f, 0.300f, 0.046f),
        (0.88f, 1.000f, 0.560f, 0.130f),
        (1.00f, 1.000f, 0.900f, 0.600f));

    /// <summary>A flame's temperature ramp: smouldering maroon at the cool edge to pale gold-white at the heart.</summary>
    public static readonly Palette Flame = new(
        (0.00f, 0.140f, 0.006f, 0.008f),
        (0.16f, 0.440f, 0.020f, 0.014f),
        (0.38f, 0.860f, 0.100f, 0.020f),
        (0.60f, 1.000f, 0.300f, 0.040f),
        (0.80f, 1.000f, 0.560f, 0.120f),
        (1.00f, 1.000f, 0.880f, 0.520f));

    /// <summary>Edge tint for the screen's vignette: near-black blood.</summary>
    public static readonly uint Vignette = DrawHelpers.Pack(0.022f, 0.003f, 0.004f);

    /// <summary>The flash an infernal cast lands with (orange-red, so it stays chromatic under a colour override).</summary>
    public static readonly uint Flash = DrawHelpers.Pack(1.00f, 0.36f, 0.12f);
}

internal sealed class HellMaterials : IMaterialPack
{
    public IEnumerable<IMaterial> Materials { get; } = new IMaterial[]
    {
        // Smoke over a hellfire: ember-lit oxblood while it is young, soot once it has risen out of the light
        new ParticlePuff("particle.brimstone", new[] { "brimstone", "sulphur", "sulfur", "hellsmoke" },
            new ParticlePuff.Look(
                Warm: DrawHelpers.Pack(0.46f, 0.075f, 0.030f), Cool: DrawHelpers.Pack(0.030f, 0.020f, 0.024f), CoolSpan: 0.55f,
                GrowFrom: 0.60f, GrowBy: 1.05f, FadeIn: 0.22f, FadeOutAt: 0.58f,
                Alpha: 0.46f, Squash: 0.84f, Irregular: 0.55f, MinSize: 2f,
                Highlight: DrawHelpers.Pack(0.95f, 0.28f, 0.05f), Thin: 0.28f, LitFromBelow: true),
            new StrokeEmission(Role: PrimitiveRole.Smoke,
                DensityPer100px: 0.45f,
                SpeedMin: 8f, SpeedMax: 22f,
                LifespanMin: 2.4f, LifespanMax: 4.2f,
                SizeMin: 14f, SizeMax: 28f,
                SpreadRadians: 1.3f,
                BiasVelocity: new Vector2(0f, -12f),
                PrimaryDirection: new Vector2(0f, -1f))),
        new RegionFirelight("region.hellglow", HellColor.Flame),
        new StrokeSigil("stroke.brand", HellColor.Glow, "particle.hellmote", "brand", "brands", "branded", "scorched"),
        new StrokeGlow("stroke.hellglow", HellColor.Glow, "hellglow", "hellglare", "brimglow"),
        new ParticleEmber("particle.hellfire", HellColor.Flame, "hellfire", "hellflame", "hellflames", "inferno", "infernal"),
        new ParticleRune("particle.hellrune", HellColor.Glow, RuneGlyphs.FirstInfernal, RuneGlyphs.InfernalCount, "occult", "hex", "hexes", "hexglyph"),
        new ParticleMote("particle.hellmote", HellColor.Glow, false, "emberlight", "fireflies", "firefly"),
    };
}
