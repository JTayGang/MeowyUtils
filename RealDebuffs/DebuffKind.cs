namespace RealDebuffs;

/// <summary>
/// The set of "real" visual debuff families this plugin renders. Several vanilla FFXIV statuses
/// map to the same kind when they're mechanically identical (e.g. Stun / Deep Freeze / Down for
/// the Count all mean "can't act, can't move" - they just come from different sources), so this
/// is a curated list of *feelings*, not a 1:1 mirror of every status name.
///
/// To add a new kind: add it here, add its name(s) to <see cref="StatusCatalog.NameMap"/>, write
/// an <see cref="Effects.IScreenEffect"/> for it, and drop that effect into the order list in
/// <see cref="EffectManager"/>. See the README for a worked example.
/// </summary>
public enum DebuffKind
{
    Blind,
    Paralysis,
    Silence,
    Stun,
    Sleep,
    Poison,
    Bind,
    Heavy,
    Petrification,
}
