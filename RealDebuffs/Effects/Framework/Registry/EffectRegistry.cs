namespace RealDebuffs.Effects.Framework;

public enum SlotType { Stroke, Particle, Region }

/// <summary>Which region layer a slot drives: regions with an edge mask are EdgeGlow, the rest FlatFill.</summary>
public enum RegionKind { FlatFill, EdgeGlow }

/// <summary>One material slot an effect exposes to the UI; Hero is the body "made of X" replaces, Modifier the one particle layer "with X" replaces.</summary>
public readonly record struct SwappableSlot(SlotType Type, PrimitiveRole Role, string Label, string DefaultMaterial, RegionKind Region = default, bool Hero = false, bool Modifier = false)
{
    /// <summary>Role for strokes and particles, region layer for regions.</summary>
    public int Key => Type == SlotType.Region ? (int)Region : (int)Role;
}

/// <summary>Slot declarations: Slot.Particle(PrimitiveRole.Ember, "Flames", "particle.ember", hero: true); at most one hero and one modifier per effect.</summary>
public static class Slot
{
    public static SwappableSlot Stroke(PrimitiveRole role, string label, string material, bool hero = false) => new(SlotType.Stroke, role, label, material, Hero: hero);
    public static SwappableSlot Particle(PrimitiveRole role, string label, string material, bool hero = false, bool modifier = false) => new(SlotType.Particle, role, label, material, Hero: hero, Modifier: modifier);
    public static SwappableSlot Region(RegionKind kind, string label, string material, bool hero = false) => new(SlotType.Region, PrimitiveRole.MainStroke, label, material, kind, hero);
}

public interface IHasSwappableSlots
{
    IReadOnlyList<SwappableSlot> Slots { get; }
}

/// <summary>Every effect's slots and defaults, registered once by Plugin.</summary>
public static class EffectRegistry
{
    private static readonly Dictionary<(DebuffKind, SlotType, int), string> Defaults = new();
    private static readonly Dictionary<DebuffKind, SwappableSlot[]> SlotsByKind = new();
    private static readonly Dictionary<DebuffKind, SwappableSlot> BodyByKind = new();
    private static readonly Dictionary<DebuffKind, SwappableSlot> ModifierByKind = new();
    private static DebuffKind[]? _kinds;

    public static void Register(ISceneEffect effect)
    {
        if (effect is not IHasSwappableSlots withSlots) return;

        var slots = withSlots.Slots.ToArray();
        if (slots.Length == 0) return;

        SlotsByKind[effect.Kind] = slots;
        BodyByKind.Remove(effect.Kind);
        ModifierByKind.Remove(effect.Kind);
        foreach (var s in slots)
        {
            if (s.Hero) BodyByKind.TryAdd(effect.Kind, s);
            if (s.Modifier) ModifierByKind.TryAdd(effect.Kind, s);
        }
        _kinds = null;

        foreach (var slot in slots)
            Defaults[(effect.Kind, slot.Type, slot.Key)] = slot.DefaultMaterial;
    }

    public static string? DefaultFor(DebuffKind kind, SlotType type, PrimitiveRole role) =>
        Defaults.GetValueOrDefault((kind, type, (int)role));

    public static string? DefaultFor(DebuffKind kind, RegionKind region) =>
        Defaults.GetValueOrDefault((kind, SlotType.Region, (int)region));

    /// <summary>The slot "made of X" replaces.</summary>
    public static SwappableSlot? BodyFor(DebuffKind kind) => BodyByKind.TryGetValue(kind, out var s) ? s : null;

    /// <summary>The particle slot "with X" replaces; null means a stroke body's shed (emit axis) takes the modifier instead.</summary>
    public static SwappableSlot? ModifierFor(DebuffKind kind) => ModifierByKind.TryGetValue(kind, out var s) ? s : null;

    public static SwappableSlot[] SlotsFor(DebuffKind kind) =>
        SlotsByKind.GetValueOrDefault(kind) ?? Array.Empty<SwappableSlot>();

    public static DebuffKind[] KindsWithSlots =>
        _kinds ??= SlotsByKind.Keys.OrderBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase).ToArray();
}
