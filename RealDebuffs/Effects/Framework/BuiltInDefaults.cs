using System.Collections.Generic;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Every effect's authored default material, keyed by (kind, primitive type, role). This is what
/// the renderer falls back to when the user hasn't overridden a slot, and what the settings panel
/// shows as the current value for an un-configured slot.
///
/// Adding a new scene effect: add its natural-material entries here. Adding a new material
/// implementation for an existing effect: change the value here, and the settings panel updates
/// on next open.
///
/// When a slot isn't listed, the renderer uses a type-appropriate universal fallback
/// (StrokeSimple for strokes, particle.spark for particles, region.flat-fill / region.edge-glow
/// for regions), so a missing entry degrades to a visible-but-unfinished look rather than a crash.
/// </summary>
public static class BuiltInDefaults
{
    // (Kind, Type, Role) -> material name. The two special Role values:
    //  - RegionMain: a region that uses the edge-mask (edge glow).
    //  - RegionFlat: a region with no edge mask (flat fill).
    private static readonly Dictionary<(DebuffKind, string, string), string> Table = new()
    {
        // Burns: fire particles rising from a warm ground band.
        [(DebuffKind.Burns, "Particle", nameof(PrimitiveRole.Ember))]     = "particle.ember",
        [(DebuffKind.Burns, "Region",   "EdgeGlow")]                       = "region.edge-glow",

        // Frost: crystalline snowflakes, snow specks, fog; intro uses a flat flash.
        [(DebuffKind.Frost, "Particle", nameof(PrimitiveRole.Snowflake))] = "particle.snowflake",
        [(DebuffKind.Frost, "Particle", nameof(PrimitiveRole.Snow))]      = "particle.snow",
        [(DebuffKind.Frost, "Particle", nameof(PrimitiveRole.Fog))]       = "particle.fog",
        [(DebuffKind.Frost, "Region",   "FlatFill")]                       = "region.flat-fill",

        // Heavy: interlocking iron chains. The ground darkening region rides the edge glow.
        [(DebuffKind.Heavy, "Stroke",   nameof(PrimitiveRole.MainStroke))] = "stroke.chain",
        [(DebuffKind.Heavy, "Region",   "EdgeGlow")]                       = "region.edge-glow",
        [(DebuffKind.Heavy, "Particle", nameof(PrimitiveRole.Spark))]      = "particle.spark",

        // Blind: full-screen black wash.
        [(DebuffKind.Blind, "Region",   "FlatFill")]                       = "region.flat-fill",

        // Vulnerability: no primitives, vignette only - nothing to map.
    };

    /// <summary>Look up the authored default for a slot, or null if the effect has none.</summary>
    public static string? Get(DebuffKind kind, string type, string roleKey) =>
        Table.TryGetValue((kind, type, roleKey), out var name) ? name : null;

    /// <summary>Universal fallback for any unconfigured stroke.</summary>
    public static string FallbackStroke() => "stroke.simple";

    /// <summary>Universal fallback for any unconfigured particle.</summary>
    public static string FallbackParticle(PrimitiveRole role) => role switch
    {
        PrimitiveRole.Ember     => "particle.ember",
        PrimitiveRole.Snowflake => "particle.snowflake",
        PrimitiveRole.Snow      => "particle.snow",
        PrimitiveRole.Fog       => "particle.fog",
        PrimitiveRole.Drip      => "particle.spark",
        PrimitiveRole.Flow      => "particle.spark",
        PrimitiveRole.Mote      => "particle.spark",
        PrimitiveRole.Rune      => "particle.spark",
        PrimitiveRole.Word      => "particle.spark",
        PrimitiveRole.Ring      => "particle.spark",
        PrimitiveRole.Flare     => "particle.spark",
        _                       => "particle.spark",
    };

    /// <summary>Universal fallback for any unconfigured region.</summary>
    public static string FallbackRegion(in RegionPrimitive r) =>
        (r.Top || r.Bottom || r.Left || r.Right) ? "region.edge-glow" : "region.flat-fill";
}