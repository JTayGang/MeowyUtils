using System.Collections.Generic;

namespace RealDebuffs.Effects.Framework;

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