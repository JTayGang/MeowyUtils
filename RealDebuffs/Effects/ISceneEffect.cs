using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>One debuff effect. Effects emit primitives into an EffectScene rather than drawing; found by reflection (public, parameterless constructor).</summary>
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

    /// <summary>Emit this frame's primitives; called only while alpha > 0 (including fades). dt is already clamped to [0, 0.25] s.</summary>
    void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride);
}

/// <summary>Detects a fresh application: a gap over a second between Emit calls means the status was re-applied; Start is that moment.</summary>
public sealed class CastTracker
{
    private const float NewCastGapSeconds = 1.0f;
    private float _last = -100f;

    public float Start { get; private set; }

    /// <summary>Call once per Emit. Returns true when this call begins a fresh application.</summary>
    public bool Begin(float time)
    {
        bool fresh = time - _last > NewCastGapSeconds;
        if (fresh) Start = time;
        _last = time;
        return fresh;
    }
}
