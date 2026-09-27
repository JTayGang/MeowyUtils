using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Per-strand appearance knobs an IStrandSkin can use - the identity of ONE strand as far as any
/// skin is concerned, regardless of which effect built its path. Everything here is cheap to fill
/// in fresh every frame from whatever the effect already tracks per-strand for its OWN purposes;
/// nothing here is skin-specific, and a skin that doesn't need a particular field just ignores it.
/// </summary>
public struct StrandVisual
{
    /// <summary>
    /// The strand's own natural size in pixels, already resolved from the effect's authored
    /// fraction-of-screen (e.g. shortSide * 0.012f) and any per-strand scale - a skin never needs
    /// to know shortSide or care whether a scale was folded in. Skins are free to interpret this
    /// however suits their own material: TentacleSkin treats it as the base half-width of the
    /// tapered line; ChainSkin treats it as a link's length, deriving width from that - see each
    /// skin's own remarks. Because it's authored per-effect rather than normalized across skins,
    /// swapping a skin changes the MATERIAL, not the effect's own sense of scale: Bind's many thin
    /// tendrils read as many thin chains if reskinned, Heavy's few thick chains read as few thick
    /// tentacles - see BindEffect/HeavyEffect's remarks on why that's a deliberate choice, not a
    /// gap, and how to retune it if a particular combination ever needs to look bigger or smaller.
    /// </summary>
    public float Thickness;

    /// <summary>Stable per-strand seed for whatever a skin wants to randomize internally (e.g. drip placement/timing) - same idea as every other per-seed hash in this project.</summary>
    public int Seed;

    /// <summary>Stable per-strand phase (radians) for whatever a skin wants to animate out of sync between strands (e.g. a traveling sheen).</summary>
    public float Phase;

    /// <summary>
    /// True if the strand's base (arc length 0) sits exactly on a hard boundary (a screen edge) -
    /// a hint that a skin which extrapolates decoration BEFORE the base (e.g. a chain placing a
    /// link before its start so it reads as continuing off-screen) should skip that, or it would
    /// visibly poke past the boundary. A skin that never looks behind the base (TentacleSkin)
    /// ignores this outright.
    /// </summary>
    public bool FlushStart;
}

/// <summary>
/// One pluggable "material" a path-based effect's strands can be drawn with. This and StrandPath
/// are the two halves of the reskinning system: a strand's SHAPE (how many, where they anchor, how
/// they move and behave - owned by the effect, e.g. BindEffect's searching/latching tendrils or
/// HeavyEffect's slam-in-and-sway chains) is completely independent of its SKIN (how it actually
/// looks when drawn - tentacle flesh-and-slime, iron chain links, or any future material). An
/// effect that wants to be reskinnable builds a StrandPath every frame per strand exactly as it
/// always did internally, then hands it to whichever IStrandSkin the user picked (see
/// IReskinnableEffect for how that choice actually reaches it) instead of drawing it with a
/// hardcoded routine of its own.
///
/// Implementations are stateless singletons: a skin's authored palette is baked once into static
/// readonly fields, exactly like every non-reskinnable effect in this folder already does its own
/// palette, precisely so the same instance is safe to reuse across every effect and every strand
/// that picks it, including more than one call in a single frame (Heavy alone draws five strands
/// a frame through whichever skin it's using).
/// </summary>
public interface IStrandSkin
{
    /// <summary>
    /// Draws one strand along <paramref name="path"/>, from its base up to <paramref name="reveal"/>
    /// (0..1) of its full arc length - every effect's cast-in/growth is expressed through this one
    /// knob, generically, regardless of the skin. <paramref name="tipFlare"/> (0..1) is a second,
    /// independent knob for "how strongly should my growing/just-settled tip flourish show right
    /// now" - each skin renders its own flavor of flourish (a glowing bulb, a hot spark) scaled by
    /// it; an effect that doesn't want one at all just always passes 0. Reports the strand's
    /// current visible tip back via the out parameters (false/default if nothing is visible yet),
    /// for a caller that wants to attach something else at that exact point.
    /// </summary>
    void DrawStrand(ImDrawListPtr dl, StrandPath path, in StrandVisual visual,
                    float reveal, float tipFlare, float alpha, float px, float time,
                    out Vector2 tipPos, out bool tipVisible);
}
