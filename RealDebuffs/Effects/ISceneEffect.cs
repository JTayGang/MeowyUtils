namespace RealDebuffs.Effects;

/// One debuff effect.
public interface ISceneEffect
{
    DebuffKind Kind { get; }

    /// User-facing label. Shown in the settings list, dev tester, and rule editor combos.
    string DisplayName { get; }

    /// One-line description shown as the settings toggle's tooltip.
    string Description { get; }

    /// Layering: lower draws first. Blind sits near 0, Silence near the top; ties break by Kind.
    int DrawOrder { get; }

    /// FFXIV status names (case-insensitive, English sheet) mapped to a strength in (0, 1].
    IReadOnlyDictionary<string, float> TriggerStatuses { get; }

    /// Words a status tooltip can contain to trigger this effect; seeds the default TooltipKeywordRule.
    IReadOnlyList<string> TriggerKeywords => [];

    /// Emit this frame's primitives; called only while alpha &gt; 0 (including fades); dt is clamped to [0, 0.25] s.
    void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride);
}

/// Base for effects: holds identity and trigger data so a subclass declares them in its constructor call.
public abstract class SceneEffect : ISceneEffect
{
    readonly CastTracker _cast;

    /// <param name="envelope">Time the effect's alpha ramp against the cast (see CastEnvelope).</param>
    protected SceneEffect(DebuffKind kind, string name, string description, int drawOrder, IReadOnlyDictionary<string, float> statuses, string[]? keywords = null, bool envelope = false)
    {
        Kind = kind; DisplayName = name; Description = description; DrawOrder = drawOrder;
        TriggerStatuses = statuses;
        TriggerKeywords = keywords ?? [];
        _cast = new(envelope);
    }

    protected SceneEffect(DebuffKind kind, string name, string description, int drawOrder, string status, string[]? keywords = null, bool envelope = false)
        : this(kind, name, description, drawOrder, new Dictionary<string, float> { [status] = 1f }, keywords, envelope) { }

    public DebuffKind Kind { get; }
    public string DisplayName { get; }
    public string Description { get; }
    public int DrawOrder { get; }
    public IReadOnlyDictionary<string, float> TriggerStatuses { get; }
    public IReadOnlyList<string> TriggerKeywords { get; }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride) =>
        Emit(scene, _cast.Frame(screenSize, alpha, time, Min(dt, MaxStep), colorOverride));

    /// Emit this frame's primitives; f carries the clock, screen, cast age and the tint.
    protected abstract void Emit(EffectScene scene, in CastFrame f);

    /// Longest step an effect integrates in one go, so a hitch can't become a leap.
    protected virtual float MaxStep => float.PositiveInfinity;
}

/// Optional: reacts when the chat blocker swallows a message, so the player can see why nothing was sent.
public interface IReactsToBlockedChat
{
    /// One or more messages were swallowed since the last frame. time is the same clock Emit gets.
    void OnChatBlocked(float time);
}

/// Optional: wants its status's stack count; called before each Emit while the effect is on. 1 for a status that doesn't stack.
public interface IReceivesStacks
{
    void SetStacks(int stacks);
}
