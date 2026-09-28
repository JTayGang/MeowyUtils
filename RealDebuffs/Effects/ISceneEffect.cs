using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// One self-contained debuff effect. Effects do NOT draw directly - they emit primitives into an
/// EffectScene, and the framework renders the whole frame at once after every effect has had its
/// turn. This is what lets materials be swapped per-primitive and lets vignettes resolve once per
/// frame rather than stacking.
/// </summary>
public interface ISceneEffect
{
    DebuffKind Kind { get; }

    /// <summary>
    /// Emit this frame's primitives into <paramref name="scene"/>. Called only while
    /// <paramref name="alpha"/> is above zero, which includes the fade-in/fade-out window.
    /// </summary>
    /// <param name="scene">The shared scene. Cleared once per frame before effects run.</param>
    /// <param name="screenSize">Current game window size in pixels.</param>
    /// <param name="alpha">Fade × strength. Global intensity is applied later by the renderer.</param>
    /// <param name="time">Seconds since plugin load - the shared clock.</param>
    /// <param name="colorOverride">Tooltip-match tint, or null for the effect's own colors. Attach to every primitive the effect emits.</param>
    void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, Vector4? colorOverride);
}