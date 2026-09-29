using System;
using System.Collections.Generic;
using System.Linq;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// One swappable material slot an effect exposes to the Effect generator UI. DefaultMaterial is
/// both what the UI shows as the current value when no override is set, and what the renderer
/// falls back to when resolving a primitive of this type/role for this effect.
///
/// PrimitiveType is "Stroke", "Particle", or "Region". For Stroke and Particle, Role selects
/// which primitive role within that type; for Region, Role is unused (pass PrimitiveRole.MainStroke
/// as a placeholder) and RegionKind ("EdgeGlow" or "FlatFill") keys the slot instead, matching the
/// renderer's own region distinction.
/// </summary>
public readonly record struct SwappableSlot(
    string PrimitiveType,
    PrimitiveRole Role,
    string Label,
    string DefaultMaterial,
    string? RegionKind = null);

/// <summary>
/// Optional capability: an ISceneEffect that exposes customizable material slots implements this.
/// The Effect generator panel reads Slots to build its editor; the renderer reads DefaultMaterial
/// to know what each slot defaults to. Effects with no customizable material choices don't
/// implement this.
/// </summary>
public interface IHasSwappableSlots
{
    IReadOnlyList<SwappableSlot> Slots { get; }
}

/// <summary>
/// The "hero" primitive slot(s) of one effect - the thing the effect is about, and the thing a
/// "made of snow" phrase in a status description replaces.
/// </summary>
public readonly record struct EffectHeroSlot(string PrimitiveType, PrimitiveRole Role);

/// <summary>
/// Optional capability: an ISceneEffect that has one or more hero slots implements this so its
/// slots can be discovered alongside the effect itself.
/// </summary>
public interface IHasHeroSlots
{
    EffectHeroSlot[] HeroSlots { get; }
}

/// <summary>
/// Registry of every effect's declared metadata. Populated once by Plugin at startup from the
/// effect roster discovered by EffectDiscovery.
///
/// Two kinds of data are collated here:
///   - Hero slots: read by the tooltip substitution system (CustomStatusSnapshot) and the
///     export-phrase builder (EffectStylePanel) to know which primitives a "made of X" phrase
///     replaces.
///   - Swappable slots: read by the Effect generator UI to build its editor, and by the renderer
///     to know each slot's default material.
///
/// Effects self-declare both via IHasHeroSlots / IHasSwappableSlots, so no table outside the
/// effect file needs to change when a new effect is added.
///
/// Fallback* methods provide type-appropriate defaults for any primitive whose slot isn't
/// declared - a stroke falls back to stroke.simple, a particle to its role default, a region to
/// edge-glow or flat-fill. These exist so an unregistered material name never crashes the renderer.
/// </summary>
public static class EffectRegistry
{
    private static readonly Dictionary<(DebuffKind, string, string), string> _defaults = new();
    private static readonly Dictionary<DebuffKind, EffectHeroSlot[]> _heroSlots = new();
    private static readonly Dictionary<DebuffKind, SwappableSlot[]> _slots = new();
    private static readonly List<DebuffKind> _kindsWithSlots = new();

    // Cached alphabetical view of _kindsWithSlots; invalidated on Register.
    private static DebuffKind[]? _sortedKindsWithSlots;

    public static void Register(ISceneEffect effect)
    {
        if (effect is IHasHeroSlots withHeroes)
            _heroSlots[effect.Kind] = withHeroes.HeroSlots;

        if (effect is IHasSwappableSlots withSlots)
        {
            var slots = withSlots.Slots.ToArray();
            if (slots.Length == 0) return;

            _slots[effect.Kind] = slots;
            if (!_kindsWithSlots.Contains(effect.Kind))
                _kindsWithSlots.Add(effect.Kind);
            _sortedKindsWithSlots = null;

            foreach (var slot in slots)
            {
                string roleKey = slot.RegionKind ?? slot.Role.ToString();
                _defaults[(effect.Kind, slot.PrimitiveType, roleKey)] = slot.DefaultMaterial;
            }
        }
    }

    /// <summary>The declared default material for one slot, or null if the effect doesn't declare one.</summary>
    public static string? DefaultFor(DebuffKind kind, string type, string roleKey) =>
        _defaults.TryGetValue((kind, type, roleKey), out var name) ? name : null;

    /// <summary>Hero slots for one kind - empty if the effect has none, or doesn't implement IHasHeroSlots.</summary>
    public static EffectHeroSlot[] HeroSlotsFor(DebuffKind kind) =>
        _heroSlots.TryGetValue(kind, out var slots) ? slots : Array.Empty<EffectHeroSlot>();

    /// <summary>Swappable slots for one kind - empty if the effect has none.</summary>
    public static SwappableSlot[] SlotsFor(DebuffKind kind) =>
        _slots.TryGetValue(kind, out var slots) ? slots : Array.Empty<SwappableSlot>();

    /// <summary>Kinds with at least one swappable slot, sorted alphabetically for UI display.</summary>
    public static DebuffKind[] KindsWithSlots =>
        _sortedKindsWithSlots ??= _kindsWithSlots.OrderBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase).ToArray();

    public static string FallbackStroke() => "stroke.simple";

public static string FallbackParticle(PrimitiveRole role) => role switch
{
    PrimitiveRole.Ember      => "particle.ember",
    PrimitiveRole.Snowflake  => "particle.snowflake",
    PrimitiveRole.Snow       => "particle.snow",
    PrimitiveRole.Fog        => "particle.fog",
    PrimitiveRole.IceCrystal => "particle.ice-crystal",
    PrimitiveRole.Drip       => "particle.drip",
    PrimitiveRole.Flow       => "particle.drip",
    _                        => "particle.spark",
};

    public static string FallbackRegion(in RegionPrimitive r) =>
        (r.Top || r.Bottom || r.Left || r.Right) ? "region.edge-glow" : "region.flat-fill";
}