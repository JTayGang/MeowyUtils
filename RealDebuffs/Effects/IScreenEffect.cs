using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// One self-contained screen-space debuff effect. Implementations should treat every coordinate
/// as relative to <paramref name="screenSize"/> below (never a hardcoded pixel value) so the
/// effect scales correctly at any window size or resolution.
/// </summary>
public interface IScreenEffect
{
    DebuffKind Kind { get; }

    /// <summary>
    /// Draws one frame of the effect. Called every frame while <paramref name="alpha"/> is above
    /// zero, which includes the fade-in/fade-out window - it is NOT only called while the debuff
    /// is fully active.
    /// </summary>
    /// <param name="dl">The foreground draw list to draw into (drawn above the game's own UI).</param>
    /// <param name="screenSize">Current game window size in pixels, read fresh every frame - this is what makes effects resolution/window-size independent.</param>
    /// <param name="alpha">
    /// 0..1 fade/intensity multiplier. EffectManager smoothly ramps this up when the debuff is
    /// applied and back down when it's cleared (and folds in the user's global intensity slider),
    /// so effects should scale their own opacity by this value rather than assuming they're always
    /// at full strength.
    /// </param>
    /// <param name="time">Seconds since the plugin loaded - a shared clock so animations stay in sync with each other.</param>
    void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time);
}
