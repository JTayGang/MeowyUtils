namespace RealDebuffs.Effects;

/// <summary>
/// Opt-in capability for an IScreenEffect built on the StrandPath/IStrandSkin system: which skin to
/// render every strand with THIS frame. EffectManager checks for this via a plain `is` pattern and
/// sets it from the user's per-effect choice in Configuration right before calling Draw, so the
/// ~20 effects that DON'T implement this (everything that isn't strand/path-based) are completely
/// unaffected - no change to IScreenEffect itself, and no constructor dependency on Configuration
/// for BindEffect/HeavyEffect just to learn one enum value.
/// </summary>
public interface IReskinnableEffect
{
    StrandSkinKind SkinKind { set; }
}
