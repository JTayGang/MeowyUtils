using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// One self-contained debuff effect. Effects do NOT draw directly - they emit primitives into an
/// EffectScene, and the framework renders the whole frame at once after every effect has had its
/// turn.
///
/// Every metadata concern the plugin needs to know about an effect lives on the effect itself:
///   - Kind: serialization key for saved rules, disabled-kinds set, material overrides.
///   - DisplayName / Description: shown in the settings toggle list, dev tester, rule editor combos.
///   - DrawOrder: layering position; lower numbers draw first (underneath).
///   - TriggerStatuses: in-game FFXIV status names that should light this effect up, mapped to a
///     strength multiplier (1.0 = full; below 1.0 for fainter tiers of the same kind).
///
/// Effects that have "hero" primitive slots (the thing a "made of snow" phrase replaces) also
/// implement IHasHeroSlots, which is a separate opt-in interface so effects without heroes don't
/// carry an empty property.
///
/// Effects are discovered by reflection at plugin load - they must be public, non-abstract, and
/// have a public parameterless constructor. See EffectDiscovery.
/// </summary>
public interface ISceneEffect
{
    DebuffKind Kind { get; }

    /// <summary>User-facing label. Shown in the settings list, dev tester, and rule editor combos.</summary>
    string DisplayName { get; }

    /// <summary>One-line description shown as the settings toggle's tooltip.</summary>
    string Description { get; }

    /// <summary>
    /// Layering order. Lower numbers draw first (underneath), higher numbers draw on top. Blind
    /// should be near 0 so its near-total vignette doesn't wash out everything else; Silence
    /// should be near the top so it renders over Blind. Effects with the same DrawOrder have an
    /// undefined relative order.
    /// </summary>
    int DrawOrder { get; }

    /// <summary>
    /// In-game FFXIV status names that trigger this effect, mapped to a strength multiplier in
    /// (0, 1]. Names are matched against the English Status sheet's Name column, case-insensitively.
    /// A kind that covers several statuses of different severity (Weakness, Brush with Death,
    /// Brink of Death) lists each with its own strength. A strength of 1.0 means "full effect".
    /// </summary>
    IReadOnlyDictionary<string, float> TriggerStatuses { get; }

    /// <summary>
    /// Words/phrases a status tooltip can contain to trigger this effect. Used to seed the user's
    /// TooltipKeywordRules on first load (see TooltipKeywordRule.SeedNewEffects). An effect that
    /// leaves this empty simply contributes no default rule - the user can still add one by hand.
    /// </summary>
    IReadOnlyList<string> TriggerKeywords => System.Array.Empty<string>();

    /// <summary>
    /// Emit this frame's primitives into <paramref name="scene"/>. Called only while
    /// <paramref name="alpha"/> is above zero, which includes the fade-in/fade-out window.
    /// </summary>
    void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, Vector4? colorOverride);
}