using System;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// The "hero" primitive slot(s) of each effect - the thing the effect is *about*, and the thing
/// a "made of snow" / "made of lightning" phrase in a status description replaces.
///
/// Effects with no clear hero (purely atmospheric washes, edge accents) return an empty list;
/// substitutions targeting them are silently dropped. Effects with more than one hero role
/// (Frost's three particle fields, for instance) can list several, though a single material
/// usually ends up on the first that type-matches.
///
/// This is a central table rather than a per-effect declaration on purpose: it plays the same
/// role as EffectManager._order and StatusCatalog.NameMap - a single place to look when tracing
/// "which slot does a substitution target for kind X." Kept small and stable.
/// </summary>
public static class EffectHeroSlots
{
    public readonly record struct HeroSlot(string PrimitiveType, PrimitiveRole Role);

    private static readonly HeroSlot[] None = Array.Empty<HeroSlot>();
    private static readonly HeroSlot[] BurnsHeroes   = { new("Particle", PrimitiveRole.Ember) };
    private static readonly HeroSlot[] FrostHeroes   = { new("Particle", PrimitiveRole.Snowflake) };
    private static readonly HeroSlot[] BlindHeroes   = { new("Region",   PrimitiveRole.MainStroke) };
    private static readonly HeroSlot[] SilenceHeroes = { new("Particle", PrimitiveRole.Rune) };

    public static HeroSlot[] For(DebuffKind kind) => kind switch
    {
        DebuffKind.Burns   => BurnsHeroes,
        DebuffKind.Frost   => FrostHeroes,
        DebuffKind.Blind   => BlindHeroes,
        DebuffKind.Silence => SilenceHeroes,
        _                  => None,
    };
}