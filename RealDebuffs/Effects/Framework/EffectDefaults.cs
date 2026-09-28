using System;
using System.Collections.Generic;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Per-effect metadata tables. Two lookups that both answer "which material does this effect use":
///
///  - BuiltInDefaults: (Kind, Type, RoleKey) -> material name. The renderer falls back to this
///    when the user has no override, and the settings panel shows it as the current value.
///  - EffectHeroSlots: (Kind) -> the role(s) that make up this effect's "hero" - the thing a
///    "made of snow" / "made of lightning" phrase in a status description replaces.
///
/// Both are keyed by DebuffKind and both feed the same "made of X" phrase system, so they live
/// together: when you add a new scene effect, you update both side by side.
/// </summary>
public static class BuiltInDefaults
{
    private static readonly Dictionary<(DebuffKind, string, string), string> Table = new()
    {
        [(DebuffKind.Burns, "Particle", nameof(PrimitiveRole.Ember))]     = "particle.ember",
        [(DebuffKind.Burns, "Region",   "EdgeGlow")]                       = "region.edge-glow",

        [(DebuffKind.Frost, "Particle", nameof(PrimitiveRole.Snowflake))] = "particle.snowflake",
        [(DebuffKind.Frost, "Particle", nameof(PrimitiveRole.Snow))]      = "particle.snow",
        [(DebuffKind.Frost, "Particle", nameof(PrimitiveRole.Fog))]       = "particle.fog",
        [(DebuffKind.Frost, "Region",   "FlatFill")]                       = "region.flat-fill",

        [(DebuffKind.Heavy, "Stroke",   nameof(PrimitiveRole.MainStroke))] = "stroke.chain",
        [(DebuffKind.Heavy, "Region",   "EdgeGlow")]                       = "region.edge-glow",
        [(DebuffKind.Heavy, "Particle", nameof(PrimitiveRole.Spark))]      = "particle.spark",

        [(DebuffKind.Disease, "Stroke",   nameof(PrimitiveRole.MainStroke))] = "stroke.parasite",
        [(DebuffKind.Disease, "Region",   "EdgeGlow")]                       = "region.edge-glow",
        [(DebuffKind.Disease, "Particle", nameof(PrimitiveRole.Drip))]       = "particle.drip",

        [(DebuffKind.Blind, "Region", "FlatFill")] = "region.flat-fill",
    };

    public static string? Get(DebuffKind kind, string type, string roleKey) =>
        Table.TryGetValue((kind, type, roleKey), out var name) ? name : null;

    public static string FallbackStroke() => "stroke.simple";

    public static string FallbackParticle(PrimitiveRole role) => role switch
    {
        PrimitiveRole.Ember     => "particle.ember",
        PrimitiveRole.Snowflake => "particle.snowflake",
        PrimitiveRole.Snow      => "particle.snow",
        PrimitiveRole.Fog       => "particle.fog",
        PrimitiveRole.Drip      => "particle.drip",
        PrimitiveRole.Flow      => "particle.drip",
        _                       => "particle.spark",
    };

    public static string FallbackRegion(in RegionPrimitive r) =>
        (r.Top || r.Bottom || r.Left || r.Right) ? "region.edge-glow" : "region.flat-fill";
}

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