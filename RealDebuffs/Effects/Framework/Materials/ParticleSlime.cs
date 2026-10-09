using System.Numerics;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Thick, translucent, sickly-green mucus: hangs from the underside of whatever sheds it in long
/// stretching strings, lets go, and falls as a glossy teardrop. The look of Disease's tentacles
/// dripping, and available to any strand as "made of slime" / "... with slime".
/// </summary>
public sealed class ParticleSlime : ParticleGoop
{
    public ParticleSlime() : base(LiquidPresets.Slime, Spec) { }

    public override string Name => "particle.slime";
    public override string[] NaturalLanguageWords { get; } =
        { "slime", "slimy", "ooze", "oozing", "goo", "goop", "mucus", "gunk" };

    /// <summary>Thick and slow: long cycles, long necks, drops that hang for a while before they let go.</summary>
    private static readonly StrokeEmission[] Spec =
    {
        new(
            Role: PrimitiveRole.Goop,
            DensityPer100px: 0f,                       // ignored: a drip block spaces its own sites
            SpeedMin: 60f, SpeedMax: 130f,             // the drop's speed the instant it lets go
            LifespanMin: 2.4f, LifespanMax: 3.2f,
            SizeMin: 3.4f, SizeMax: 5.6f,
            SpreadRadians: 0f,
            BiasVelocity: Vector2.Zero,
            Gravity: new Vector2(0f, 1500f),
            Drip: new StrokeDripOptions(
                SiteSpacingPx: 150f,
                CycleSecondsMin: 3.0f, CycleSecondsMax: 5.4f,
                MinSlope: 0.42f,
                Stringiness: 0.85f,
                SatelliteChance: 0.55f)),
    };
}
