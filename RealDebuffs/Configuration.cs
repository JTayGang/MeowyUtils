using System;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace RealDebuffs;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;
    public bool HideDuringCutscenes { get; set; } = true;

    /// <summary>Multiplies every effect's alpha/intensity. 1.0 = as-authored, lower = subtler, higher = more intense.</summary>
    public float GlobalIntensity { get; set; } = MaxIntensity;

    public const float MinIntensity = 0.1f;
    public const float MaxIntensity = 1.75f;

    public bool BlindEnabled { get; set; } = true;
    public bool ParalysisEnabled { get; set; } = true;
    public bool SilenceEnabled { get; set; } = true;
    public bool StunEnabled { get; set; } = true;
    public bool SleepEnabled { get; set; } = true;
    public bool PoisonEnabled { get; set; } = true;
    public bool BindEnabled { get; set; } = true;
    public bool HeavyEnabled { get; set; } = true;
    public bool PetrificationEnabled { get; set; } = true;

    /// <summary>
    /// Advanced/optional and OFF by default: actually stops outgoing chat while Silenced, via a
    /// game hook, instead of just showing the visual effect. See ChatBlocker.cs.
    /// </summary>
    public bool SilenceBlocksChat { get; set; } = false;

    public bool IsEnabled(DebuffKind kind) => kind switch
    {
        DebuffKind.Blind => BlindEnabled,
        DebuffKind.Paralysis => ParalysisEnabled,
        DebuffKind.Silence => SilenceEnabled,
        DebuffKind.Stun => StunEnabled,
        DebuffKind.Sleep => SleepEnabled,
        DebuffKind.Poison => PoisonEnabled,
        DebuffKind.Bind => BindEnabled,
        DebuffKind.Heavy => HeavyEnabled,
        DebuffKind.Petrification => PetrificationEnabled,
        _ => false,
    };

    /// <summary>
    /// Used by the config window's checkboxes, and by EffectManager as a session-only (not saved)
    /// safety net if an effect ever throws - see EffectManager.Draw.
    /// </summary>
    public void SetEnabled(DebuffKind kind, bool enabled)
    {
        switch (kind)
        {
            case DebuffKind.Blind: BlindEnabled = enabled; break;
            case DebuffKind.Paralysis: ParalysisEnabled = enabled; break;
            case DebuffKind.Silence: SilenceEnabled = enabled; break;
            case DebuffKind.Stun: StunEnabled = enabled; break;
            case DebuffKind.Sleep: SleepEnabled = enabled; break;
            case DebuffKind.Poison: PoisonEnabled = enabled; break;
            case DebuffKind.Bind: BindEnabled = enabled; break;
            case DebuffKind.Heavy: HeavyEnabled = enabled; break;
            case DebuffKind.Petrification: PetrificationEnabled = enabled; break;
        }
    }

    public void Save(IDalamudPluginInterface pi) => pi.SavePluginConfig(this);
}
