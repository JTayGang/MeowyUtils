using System;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// The "hero" primitive slot(s) of each effect - the thing the effect is about, and the thing a
/// "made of snow" / "made of lightning" phrase in a status description replaces.
///
/// Effects with no clear hero (purely atmospheric washes, edge accents) return an empty list;
/// substitutions targeting them are silently dropped. Effects with more than one hero role can
/// list several: a single material substitution applies to whichever slot type-matches it.
/// </summary>
public static class EffectHeroSlots
{
    public readonly record struct HeroSlot(string PrimitiveType, PrimitiveRole Role);

    private static readonly HeroSlot[] None = Array.Empty<HeroSlot>();
    private static readonly HeroSlot[] BurnsHeroes   = { new("Particle", PrimitiveRole.Ember) };
    private static readonly HeroSlot[] FrostHeroes   = { new("Particle", PrimitiveRole.Snowflake) };
    private static readonly HeroSlot[] BlindHeroes   = { new("Region",   PrimitiveRole.MainStroke) };
    private static readonly HeroSlot[] SilenceHeroes = { new("Particle", PrimitiveRole.Rune) };
    private static readonly HeroSlot[] HeavyHeroes   = { new("Stroke",   PrimitiveRole.MainStroke) };

    public static HeroSlot[] For(DebuffKind kind) => kind switch
    {
        DebuffKind.Burns   => BurnsHeroes,
        DebuffKind.Frost   => FrostHeroes,
        DebuffKind.Blind   => BlindHeroes,
        DebuffKind.Silence => SilenceHeroes,
        DebuffKind.Heavy   => HeavyHeroes,
        _                  => None,
    };
}