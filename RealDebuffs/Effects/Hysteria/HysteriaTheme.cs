using System.Numerics;
using RealDebuffs.Effects.Framework.Materials;

namespace RealDebuffs.Effects.Framework;

/// <summary>Haunting: ashen pallor, cold slate through bone to a faintly warm white.</summary>
internal static class HauntColor
{
    /// <summary>Light, dim to hot: 0 = cold slate, 0.3 = grey teal, 0.55 = verdigris, 0.78 = bone, 1 = pale warm white.</summary>
    public static readonly Palette Glow = new(
        (0.00f, 0.060f, 0.085f, 0.115f),
        (0.30f, 0.200f, 0.290f, 0.340f),
        (0.55f, 0.520f, 0.660f, 0.640f),
        (0.78f, 0.840f, 0.900f, 0.840f),
        (1.00f, 0.985f, 0.990f, 0.940f));

    /// <summary>Edge tint for the screen's vignette: ink with a cold cast.</summary>
    public static readonly uint Vignette = DrawHelpers.Pack(0.010f, 0.014f, 0.020f);

    /// <summary>The cold flash a haunting lands with (pale cyan-bone, so it stays chromatic under a colour override).</summary>
    public static readonly uint Flash = DrawHelpers.Pack(0.78f, 0.96f, 0.90f);
}

internal sealed class HauntMaterials : IMaterialPack
{
    public IEnumerable<IMaterial> Materials { get; } = new IMaterial[]
    {
        // The dark that comes with a haunting: ink with a cold sheen where something pale catches it
        new ParticlePuff("particle.gloom", new[] { "gloom", "gloaming", "umbra" },
            new ParticlePuff.Look(
                Warm: DrawHelpers.Pack(0.16f, 0.24f, 0.26f), Cool: DrawHelpers.Pack(0.012f, 0.018f, 0.024f), CoolSpan: 0.80f,
                GrowFrom: 0.60f, GrowBy: 0.60f, FadeIn: 0.25f, FadeOutAt: 0.55f,
                Alpha: 0.42f, Squash: 0.80f, Irregular: 0.55f, MinSize: 2f,
                Highlight: DrawHelpers.Pack(0.55f, 0.72f, 0.70f), Thin: 0.30f),
            new StrokeEmission(Role: PrimitiveRole.Miasma,
                DensityPer100px: 0.30f,
                SpeedMin: 4f, SpeedMax: 12f,
                LifespanMin: 2.6f, LifespanMax: 4.4f,
                SizeMin: 16f, SizeMax: 30f,
                SpreadRadians: 1.4f,
                BiasVelocity: new Vector2(0f, -6f),
                PrimaryDirection: new Vector2(0f, -1f))),
        new StrokeSigil("stroke.haunt", HauntColor.Glow, "particle.spirit", "ghostlight", "spectral", "specter", "wraithlight"),
        new StrokeGlow("stroke.hauntglow", HauntColor.Glow, "hauntglow", "ghostglow", "pallor"),
        new ParticleRune("particle.omen", HauntColor.Glow, 0, RuneGlyphs.FirstSilence, "omen", "omens", "hallucination", "hallucinations"),
        new ParticleMote("particle.spirit", HauntColor.Glow, false, "spirit", "spirits", "soul", "souls", "wraith", "wraiths"),
    };
}
