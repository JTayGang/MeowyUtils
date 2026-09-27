namespace RealDebuffs.Effects;

/// <summary>
/// Which pluggable IStrandSkin to render a reskinnable effect's strands with - see
/// IReskinnableEffect and IStrandSkin. Add a case here (and a matching IStrandSkin implementation,
/// wired up in <see cref="StrandSkins"/>) to add a whole new "material" that any path-based effect
/// can wear, without either the effect or the skin needing to know about each other. Saved as a
/// plain enum in Configuration, same as DebuffKind.
/// </summary>
public enum StrandSkinKind
{
    Tentacle,
    Chain,
}

/// <summary>Resolves a StrandSkinKind to its (stateless, shared) IStrandSkin instance.</summary>
public static class StrandSkins
{
    public static IStrandSkin Get(StrandSkinKind kind) => kind switch
    {
        StrandSkinKind.Chain => ChainSkin.Instance,
        _ => TentacleSkin.Instance,
    };
}
