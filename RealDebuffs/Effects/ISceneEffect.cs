using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>One debuff effect.</summary>
public interface ISceneEffect
{
    DebuffKind Kind { get; }

    /// <summary>User-facing label. Shown in the settings list, dev tester, and rule editor combos.</summary>
    string DisplayName { get; }

    /// <summary>One-line description shown as the settings toggle's tooltip.</summary>
    string Description { get; }

    /// <summary>Layering: lower draws first. Blind sits near 0, Silence near the top; ties are undefined.</summary>
    int DrawOrder { get; }

    /// <summary>FFXIV status names (case-insensitive, English sheet) mapped to a strength in (0, 1].</summary>
    IReadOnlyDictionary<string, float> TriggerStatuses { get; }

    /// <summary>Words a status tooltip can contain to trigger this effect; seeds the default TooltipKeywordRule. Empty = no default rule.</summary>
    IReadOnlyList<string> TriggerKeywords => System.Array.Empty<string>();

    /// <summary>Emit this frame's primitives; called only while alpha &gt; 0 (including fades). dt is already clamped to [0, 0.25] s.</summary>
    void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride);
}

/// <summary>Optional: an effect that reacts when the chat blocker swallows an outgoing message, so the player can see why nothing was sent.</summary>
public interface IReactsToBlockedChat
{
    /// <summary>One or more messages were swallowed since the last frame. time is the same clock Emit gets.</summary>
    void OnChatBlocked(float time);
}
