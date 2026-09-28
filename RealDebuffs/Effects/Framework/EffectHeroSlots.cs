using System;

namespace RealDebuffs.Effects.Framework;

public static class EffectHeroSlots
{
    public readonly record struct HeroSlot(string PrimitiveType, PrimitiveRole Role);

    private static readonly HeroSlot[] None = Array.Empty<HeroSlot>();
    private static readonly HeroSlot[] BurnsHeroes    = { new("Particle", PrimitiveRole.Ember) };
    private static readonly HeroSlot[] FrostHeroes    = { new("Particle", PrimitiveRole.Snowflake) };
    private static readonly HeroSlot[] BlindHeroes    = { new("Region",   PrimitiveRole.MainStroke) };
    private static readonly HeroSlot[] SilenceHeroes  = { new("Particle", PrimitiveRole.Rune) };
    private static readonly HeroSlot[] HeavyHeroes    = { new("Stroke",   PrimitiveRole.MainStroke) };
    private static readonly HeroSlot[] DiseaseHeroes  = { new("Stroke",   PrimitiveRole.MainStroke) };

    public static HeroSlot[] For(DebuffKind kind) => kind switch
    {
        DebuffKind.Burns   => BurnsHeroes,
        DebuffKind.Frost   => FrostHeroes,
        DebuffKind.Blind   => BlindHeroes,
        DebuffKind.Silence => SilenceHeroes,
        DebuffKind.Heavy   => HeavyHeroes,
        DebuffKind.Disease => DiseaseHeroes,
        _                  => None,
    };
}