using System.Collections.Concurrent;

namespace RealDebuffs.Effects.Framework;

/// <summary>Persisted override keys: "{Kind}.{Stroke|Particle}.{Role}", "{Kind}.Region.{EdgeGlow|FlatFill}", "{Kind}.Stroke.{Role}.Emit".</summary>
public static class MaterialOverrideKey
{
    private static readonly ConcurrentDictionary<(DebuffKind, SlotType, PrimitiveRole), string> ForKeys = new();
    private static readonly ConcurrentDictionary<(DebuffKind, RegionKind), string> RegionKeys = new();
    private static readonly ConcurrentDictionary<(DebuffKind, PrimitiveRole), string> EmitKeys = new();

    public static string For(DebuffKind kind, SlotType type, PrimitiveRole role) =>
        ForKeys.GetOrAdd((kind, type, role), static k => $"{k.Item1}.{k.Item2}.{k.Item3}");

    public static string ForRegion(DebuffKind kind, RegionKind region) =>
        RegionKeys.GetOrAdd((kind, region), static k => $"{k.Item1}.Region.{k.Item2}");

    public static string ForStrokeEmit(DebuffKind kind, PrimitiveRole role) =>
        EmitKeys.GetOrAdd((kind, role), static k => $"{k.Item1}.Stroke.{k.Item2}.Emit");

    /// <summary>The key a slot persists its override under.</summary>
    public static string For(DebuffKind kind, in SwappableSlot slot) =>
        slot.Type == SlotType.Region ? ForRegion(kind, slot.Region) : For(kind, slot.Type, slot.Role);
}
