using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using RealDebuffs.Effects;

namespace RealDebuffs;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;
    public bool HideDuringCutscenes { get; set; } = true;

    /// <summary>Multiplies every effect's alpha/intensity. 1.0 = as-authored.</summary>
    public float GlobalIntensity { get; set; } = MaxIntensity;

    public const float MinIntensity = 0.1f;
    public const float MaxIntensity = 1.75f;

    /// <summary>
    /// Effects the user has toggled off. Membership means disabled; absence means enabled. A
    /// set rather than one bool per kind, so adding a new DebuffKind requires no change here -
    /// a new kind is simply "not in the set" and therefore on by default. The per-kind label
    /// and description still live in the settings panel.
    ///
    /// NOTE: the enum's numeric values are what get serialized. Under the existing convention
    /// (CustomStatusRule.Kind already stores the enum number the same way), new kinds must be
    /// added at the END of DebuffKind, never inserted or reordered.
    /// </summary>
    public HashSet<DebuffKind> DisabledKinds { get; set; } = new();

    public bool IsEnabled(DebuffKind kind) => !DisabledKinds.Contains(kind);

    public void SetEnabled(DebuffKind kind, bool enabled)
    {
        if (enabled) DisabledKinds.Remove(kind);
        else         DisabledKinds.Add(kind);
    }

    /// <summary>
    /// Per-slot material overrides for ported effects. Key format is
    /// "{DebuffKind}.{Stroke|Particle}.{PrimitiveRole}" or "{DebuffKind}.Region.{EdgeGlow|FlatFill}";
    /// value is a material name from MaterialRegistry (e.g. "particle.ember"). Missing entries
    /// fall back to BuiltInDefaults. See Effect Styles in the Moodles/Loci Support tab.
    /// </summary>
    public Dictionary<string, string> MaterialOverrides { get; set; } = new();

    /// <summary>
    /// Per-effect color overrides applied when no tooltip-derived or dev-test color is present.
    /// Key is the DebuffKind; value is a color word from
    /// <see cref="TooltipKeywordParser.NamedColors"/> (e.g. "azure"). Resolved to a Vector4 at
    /// render time via <see cref="TooltipKeywordParser.TryResolveColorToken"/>. Set from the
    /// Effect generator panel's Color dropdown; cleared by that panel's Reset button.
    /// </summary>
    public Dictionary<DebuffKind, string> ColorOverrides { get; set; } = new();

    /// <summary>"While I have this custom status, show this effect" links - see <see cref="CustomStatusRule"/>.</summary>
    public List<CustomStatusRule> CustomStatusRules { get; set; } = new();

    /// <summary>Also scan each active status's tooltip text for <see cref="TooltipKeywordRules"/>. Off by default.</summary>
    public bool ParseCustomStatusTooltips { get; set; } = false;

    /// <summary>
    /// "If a status's tooltip contains this word, show this effect" links - see
    /// <see cref="TooltipKeywordRule"/>. Only consulted while <see cref="ParseCustomStatusTooltips"/>
    /// is on. Seeded at load from each effect's TriggerKeywords; see
    /// <see cref="TooltipKeywordRule.SeedNewEffects"/>.
    /// </summary>
    public List<TooltipKeywordRule> TooltipKeywordRules { get; set; } = new();

    /// <summary>
    /// Kinds whose default tooltip-keyword rule has already been seeded into TooltipKeywordRules.
    /// New effects are added automatically on load; a kind already in here is never re-seeded, so a
    /// user who deliberately deleted a rule keeps it deleted. "Reset to defaults" clears this.
    /// </summary>
    public HashSet<DebuffKind> SeededKinds { get; set; } = new();

    /// <summary>OFF by default: actually stop outgoing chat while silenced. See ChatBlocker.cs.</summary>
    public bool SilenceBlocksChat { get; set; } = false;

    public void Save(IDalamudPluginInterface pi) => pi.SavePluginConfig(this);
}

/// <summary>
/// Settings window. Two tabs:
///  - "Effects": master switches, intensity, per-debuff toggles, chat lockout, dev test panel.
///  - "Moodles/Loci Support": custom-status rules, tooltip keyword rules, and effect styles.
/// </summary>
public sealed class ConfigWindow : Window
{
    private readonly Configuration _config;
    private readonly Action _save;
    private readonly CustomStatusPanel _customStatuses;
    private readonly TooltipKeywordPanel _tooltipKeywords;
    private readonly EffectStylePanel _effectStyles;
    private readonly IReadOnlyList<ISceneEffect> _effects;

    public ConfigWindow(Configuration config, Action save, CustomStatusWatcher customStatuses, IReadOnlyList<ISceneEffect> effects)
        : base("Real Debuffs Settings###RealDebuffsConfig")
    {
        _config = config;
        _save = save;
        _effects = effects;
        _customStatuses = new CustomStatusPanel(config, customStatuses, effects);
        _tooltipKeywords = new TooltipKeywordPanel(config, customStatuses, effects);
        _effectStyles = new EffectStylePanel(config);
        Size = new Vector2(470, 660);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        bool changed = false;

        if (ImGui.BeginTabBar("##RealDebuffsTabs"))
        {
            if (ImGui.BeginTabItem("Effects"))
            {
                changed |= DrawEffectsTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Moodles/Loci Support"))
            {
                changed |= DrawMoodlesTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        if (changed)
            _save();
    }

    private bool DrawEffectsTab()
    {
        bool changed = false;

        bool enabled = _config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled)) { _config.Enabled = enabled; changed = true; }

        bool hideCutscenes = _config.HideDuringCutscenes;
        if (ImGui.Checkbox("Hide during cutscenes", ref hideCutscenes)) { _config.HideDuringCutscenes = hideCutscenes; changed = true; }

        float intensity = _config.GlobalIntensity;
        float percent = (intensity - Configuration.MinIntensity) / (Configuration.MaxIntensity - Configuration.MinIntensity) * 100f;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("Overall intensity", ref percent, 0f, 100f, "%.0f%%"))
        {
            _config.GlobalIntensity = Configuration.MinIntensity + (percent / 100f) * (Configuration.MaxIntensity - Configuration.MinIntensity);
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextDisabled("Per-debuff effects");
        ImGui.Spacing();

        // Sorted alphabetically by DisplayName for scanning; EffectManager._order is a
        // separate concern (layering). Sorts the same way the old hardcoded list did, so
        // existing muscle memory still works.
        foreach (var effect in _effects.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase))
            changed |= EffectToggle(effect.Kind, effect.DisplayName, effect.Description);

        ImGui.Separator();
        ImGui.TextDisabled("Advanced");
        ImGui.Spacing();

        bool blockChat = _config.SilenceBlocksChat;
        if (ImGui.Checkbox("Silence also blocks sending chat", ref blockChat)) { _config.SilenceBlocksChat = blockChat; changed = true; }
        ImGui.TextWrapped(
            "Hooks the game's chat-send function so messages don't actually go out while you're " +
            "silenced, rather than only showing the visual effect. This is a deeper game hook than " +
            "any of the effects above need, so if a game update ever breaks something, this is the " +
            "first setting to try turning off - everything else is unaffected by it.");

        DebugTester.DrawUi(kind => _config.Enabled && _config.IsEnabled(kind), _effects);

        return changed;
    }

    private bool DrawMoodlesTab()
    {
        bool changed = false;
        changed |= _customStatuses.Draw();
        ImGui.Separator();
        changed |= _tooltipKeywords.Draw();
        ImGui.Separator();
        changed |= _effectStyles.Draw();
        return changed;
    }

    private bool EffectToggle(DebuffKind kind, string label, string description)
    {
        bool value = _config.IsEnabled(kind);
        bool didChange = ImGui.Checkbox(label, ref value);
        if (didChange) _config.SetEnabled(kind, value);

        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(description);

        return didChange;
    }
}