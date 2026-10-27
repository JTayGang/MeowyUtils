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

    /// <summary>Words a status tooltip can contain to trigger this effect; seeds the default TooltipKeywordRule.</summary>
    IReadOnlyList<string> TriggerKeywords => System.Array.Empty<string>();

    /// <summary>Emit this frame's primitives; called only while alpha &gt; 0 (including fades); dt is clamped to [0, 0.25] s.</summary>
    void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride);
}

/// <summary>Base for effects: holds identity and trigger data so a subclass declares them in its constructor call.</summary>
public abstract class SceneEffect : ISceneEffect
{
    protected SceneEffect(DebuffKind kind, string name, string description, int drawOrder, IReadOnlyDictionary<string, float> statuses, string[]? keywords = null)
    {
        Kind = kind; DisplayName = name; Description = description; DrawOrder = drawOrder;
        TriggerStatuses = statuses;
        TriggerKeywords = keywords ?? System.Array.Empty<string>();
    }

    protected SceneEffect(DebuffKind kind, string name, string description, int drawOrder, string status, string[]? keywords = null)
        : this(kind, name, description, drawOrder, new Dictionary<string, float> { [status] = 1f }, keywords) { }

    public DebuffKind Kind { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public int DrawOrder { get; }
    public IReadOnlyDictionary<string, float> TriggerStatuses { get; }
    public IReadOnlyList<string> TriggerKeywords { get; }

    public abstract void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride);
}

/// <summary>Optional: reacts when the chat blocker swallows a message, so the player can see why nothing was sent.</summary>
public interface IReactsToBlockedChat
{
    /// <summary>One or more messages were swallowed since the last frame. time is the same clock Emit gets.</summary>
    void OnChatBlocked(float time);
}
