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
/// The "hero" primitive slot(s) of one effect - the thing the effect is about, and the thing a
/// "made of snow" phrase in a status description replaces.
///
/// PrimitiveType is the type key the renderer uses ("Stroke", "Particle", "Region"), matching
/// the value passed to MaterialOverrideKey.For. Role is the primitive role within that type.
/// A single effect can list several slots; a substitution applies to whichever one type-matches
/// the material being substituted in.
/// </summary>
public readonly record struct EffectHeroSlot(string PrimitiveType, PrimitiveRole Role);

/// <summary>
/// Optional capability: an ISceneEffect that has one or more hero slots implements this so its
/// slots can be discovered alongside the effect itself. Effects with no clear hero (purely
/// atmospheric washes, edge accents) don't implement this, and their substitutions are silently
/// dropped.
/// </summary>
public interface IHasHeroSlots
{
    EffectHeroSlot[] HeroSlots { get; }
}

/// <summary>
/// Registry of every effect's hero slots, keyed by DebuffKind. Populated once by Plugin at
/// startup from the effect roster discovered by EffectDiscovery. Effects self-describe their
/// slots via IHasHeroSlots; this class just collates them so the tooltip substitution system
/// (CustomStatusSnapshot) and the export-phrase builder (EffectStylePanel) can look them up by
/// kind without carrying a reference to the roster.
/// </summary>
public static class EffectHeroSlots
{
    private static readonly Dictionary<DebuffKind, EffectHeroSlot[]> _byKind = new();

    public static void Register(DebuffKind kind, EffectHeroSlot[] slots) => _byKind[kind] = slots;

    public static EffectHeroSlot[] For(DebuffKind kind) =>
        _byKind.TryGetValue(kind, out var slots) ? slots : Array.Empty<EffectHeroSlot>();
}