namespace RealDebuffs.Effects.Framework;

internal enum AddrType { Stroke, Particle, Region, Emit }

/// <summary>Where a slot override lands: a stroke or particle role, a region kind, or a stroke role's emit axis.</summary>
internal readonly record struct Addr(AddrType Type, int Role);

/// <summary>Append-only: a material's, colour's or slot address's position in these lists is its number in short codes, so never reorder, insert or remove; add new ones at the end (the audit names any that are missing).</summary>
internal static class CodeIds
{
    public static readonly string[] Stroke =
    {
        "stroke.blood", "stroke.brand", "stroke.chain", "stroke.corona", "stroke.glow", "stroke.haunt",
        "stroke.hauntglow", "stroke.hellglow", "stroke.horn", "stroke.lightning", "stroke.magma", "stroke.parasite",
        "stroke.rope", "stroke.sigil", "stroke.simple", "stroke.wisp",
        "stroke.glasscrack", "stroke.gash",
    };

    public static readonly string[] Particle =
    {
        "particle.arc", "particle.blood", "particle.brimstone", "particle.candle", "particle.cinder",
        "particle.dust", "particle.ember", "particle.eye", "particle.fibre", "particle.flake", "particle.flare",
        "particle.fugue", "particle.glint", "particle.gloom", "particle.hellfire", "particle.hellmote",
        "particle.hellrune", "particle.lethe", "particle.miasma", "particle.mist", "particle.mote", "particle.omen",
        "particle.pearlfog", "particle.rosefog", "particle.rune", "particle.slime", "particle.smoke",
        "particle.snow", "particle.snowflake", "particle.spark", "particle.spirit", "particle.stardust",
        "particle.static", "particle.thunderhead", "particle.twinkle", "particle.water", "particle.whisper",
        "particle.shard",
    };

    public static readonly string[] Region =
    {
        "region.edge-glow", "region.firelight", "region.flat-fill", "region.frost-cover", "region.haunt-tunnel",
        "region.hellglow", "region.liquid", "region.vortex",
        "region.glass",
    };

    public static readonly string[] Color =
    {
        "black", "blue", "cyan", "grayscale", "green", "magenta", "navy", "orange", "pink", "purple", "red", "teal",
        "white", "yellow", "amber", "azure", "beige", "bronze", "brown", "burgundy", "charcoal", "copper", "coral",
        "cream", "crimson", "emerald", "fuchsia", "gold", "gray", "grey", "indigo", "ivory", "jade", "lavender",
        "lilac", "lime", "maroon", "mint", "olive", "peach", "platinum", "rose", "ruby", "salmon", "sapphire",
        "scarlet", "silver", "tan", "turquoise", "violet",
    };

    public static readonly Addr[] Slot =
    {
        S(PrimitiveRole.MainStroke), S(PrimitiveRole.Inscription), S(PrimitiveRole.Halo), S(PrimitiveRole.Edge),
        S(PrimitiveRole.Horn), S(PrimitiveRole.Trail), P(PrimitiveRole.Spark), P(PrimitiveRole.Ember),
        P(PrimitiveRole.Snowflake), P(PrimitiveRole.Snow), P(PrimitiveRole.Smoke), P(PrimitiveRole.Cinder),
        P(PrimitiveRole.Dust), P(PrimitiveRole.Fibre), P(PrimitiveRole.Glint), P(PrimitiveRole.Mist),
        P(PrimitiveRole.Goop), P(PrimitiveRole.Rune), P(PrimitiveRole.Mote), P(PrimitiveRole.Miasma),
        P(PrimitiveRole.Arc), P(PrimitiveRole.Haze), P(PrimitiveRole.Flash), P(PrimitiveRole.Candle),
        P(PrimitiveRole.Eye), P(PrimitiveRole.Whisper), R(RegionKind.FlatFill), R(RegionKind.EdgeGlow),
        E(PrimitiveRole.MainStroke), E(PrimitiveRole.Inscription), E(PrimitiveRole.Halo), E(PrimitiveRole.Edge),
        E(PrimitiveRole.Horn), E(PrimitiveRole.Trail),
        P(PrimitiveRole.Shard),
    };

    private static Addr S(PrimitiveRole r) => new(AddrType.Stroke, (int)r);
    private static Addr P(PrimitiveRole r) => new(AddrType.Particle, (int)r);
    private static Addr E(PrimitiveRole r) => new(AddrType.Emit, (int)r);
    private static Addr R(RegionKind k) => new(AddrType.Region, (int)k);
}
