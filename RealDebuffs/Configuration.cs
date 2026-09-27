using System;
using System.Collections.Generic;
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

    // Which IStrandSkin material to render each reskinnable effect's strands with - see
    // IReskinnableEffect and Effects/IStrandSkin.cs. Defaults preserve each effect's original,
    // as-shipped look: Bind's tendrils stay tentacles, Heavy's chains stay chains, until the user
    // deliberately swaps them in Settings.
    public StrandSkinKind BindSkin { get; set; } = StrandSkinKind.Tentacle;
    public StrandSkinKind HeavySkin { get; set; } = StrandSkinKind.Chain;

    // Added later, alongside DebuffKind's "Added later" block - same order as that enum.
    public bool AmnesiaEnabled { get; set; } = true;
    public bool BleedingEnabled { get; set; } = true;
    public bool WeaknessEnabled { get; set; } = true;
    public bool BurnsEnabled { get; set; } = true;
    public bool CharmEnabled { get; set; } = true;
    public bool FrostEnabled { get; set; } = true;
    public bool DiseaseEnabled { get; set; } = true;
    public bool DoomEnabled { get; set; } = true;
    public bool DropsyEnabled { get; set; } = true;
    public bool ElectrocutionEnabled { get; set; } = true;
    public bool HysteriaEnabled { get; set; } = true;
    public bool InfirmityEnabled { get; set; } = true;
    public bool MiseryEnabled { get; set; } = true;
    public bool PacificationEnabled { get; set; } = true;
    public bool SlowEnabled { get; set; } = true;
    public bool SludgeEnabled { get; set; } = true;
    public bool VulnerabilityEnabled { get; set; } = true;
    public bool WindburnEnabled { get; set; } = true;

    /// <summary>
    /// "While I have this custom Moodles/Loci status, show this effect" links - see
    /// <see cref="CustomStatusRule"/>. These add to the real-debuff effects above rather than
    /// replacing them, and the per-effect toggles above still act as the master switch for each effect.
    /// </summary>
    public List<CustomStatusRule> CustomStatusRules { get; set; } = new();

    /// <summary>
    /// The checkbox next to the custom-status section: whether to ALSO scan each active Moodles/Loci
    /// status's tooltip text for <see cref="TooltipKeywordRules"/>, on top of matching its name
    /// above. Off by default - this is a heuristic, text-matching feature, and worth reviewing the
    /// seeded keyword list against your own moodles before turning it on.
    /// </summary>
    public bool ParseCustomStatusTooltips { get; set; } = false;

    /// <summary>
    /// "If a status's tooltip contains this word, show this effect (in this color, at this
    /// strength)" links - see <see cref="TooltipKeywordRule"/>. Only consulted while
    /// <see cref="ParseCustomStatusTooltips"/> is on. Seeded with a starting set (see
    /// <see cref="TooltipKeywordRule.Defaults"/>) rather than empty, since - unlike a status NAME,
    /// which is arbitrary per-user RP flavor - common English color/effect words are predictable
    /// enough to ship a useful default for. This also means an existing save from before this
    /// feature existed picks up the same defaults the first time it loads post-update: Dalamud's
    /// config load only overwrites a property the saved JSON actually contains, so a property this
    /// old JSON never had keeps whatever this field initializer set it to - exactly how
    /// CustomStatusRules above has always defaulted an old save to an empty list, just with a
    /// non-empty default this time.
    /// </summary>
    public List<TooltipKeywordRule> TooltipKeywordRules { get; set; } = TooltipKeywordRule.Defaults();

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
        DebuffKind.Amnesia => AmnesiaEnabled,
        DebuffKind.Bleeding => BleedingEnabled,
        DebuffKind.Weakness => WeaknessEnabled,
        DebuffKind.Burns => BurnsEnabled,
        DebuffKind.Charm => CharmEnabled,
        DebuffKind.Frost => FrostEnabled,
        DebuffKind.Disease => DiseaseEnabled,
        DebuffKind.Doom => DoomEnabled,
        DebuffKind.Dropsy => DropsyEnabled,
        DebuffKind.Electrocution => ElectrocutionEnabled,
        DebuffKind.Hysteria => HysteriaEnabled,
        DebuffKind.Infirmity => InfirmityEnabled,
        DebuffKind.Misery => MiseryEnabled,
        DebuffKind.Pacification => PacificationEnabled,
        DebuffKind.Slow => SlowEnabled,
        DebuffKind.Sludge => SludgeEnabled,
        DebuffKind.Vulnerability => VulnerabilityEnabled,
        DebuffKind.Windburn => WindburnEnabled,
        _ => false,
    };

    /// <summary>Which IStrandSkin material to render this kind's strands with, if it's reskinnable at all - see IReskinnableEffect. Kinds that aren't strand-based never read this.</summary>
    public StrandSkinKind GetSkin(DebuffKind kind) => kind switch
    {
        DebuffKind.Bind => BindSkin,
        DebuffKind.Heavy => HeavySkin,
        _ => StrandSkinKind.Tentacle,
    };

    /// <summary>Used by the config window's per-effect skin picker.</summary>
    public void SetSkin(DebuffKind kind, StrandSkinKind skin)
    {
        switch (kind)
        {
            case DebuffKind.Bind: BindSkin = skin; break;
            case DebuffKind.Heavy: HeavySkin = skin; break;
        }
    }

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
            case DebuffKind.Amnesia: AmnesiaEnabled = enabled; break;
            case DebuffKind.Bleeding: BleedingEnabled = enabled; break;
            case DebuffKind.Weakness: WeaknessEnabled = enabled; break;
            case DebuffKind.Burns: BurnsEnabled = enabled; break;
            case DebuffKind.Charm: CharmEnabled = enabled; break;
            case DebuffKind.Frost: FrostEnabled = enabled; break;
            case DebuffKind.Disease: DiseaseEnabled = enabled; break;
            case DebuffKind.Doom: DoomEnabled = enabled; break;
            case DebuffKind.Dropsy: DropsyEnabled = enabled; break;
            case DebuffKind.Electrocution: ElectrocutionEnabled = enabled; break;
            case DebuffKind.Hysteria: HysteriaEnabled = enabled; break;
            case DebuffKind.Infirmity: InfirmityEnabled = enabled; break;
            case DebuffKind.Misery: MiseryEnabled = enabled; break;
            case DebuffKind.Pacification: PacificationEnabled = enabled; break;
            case DebuffKind.Slow: SlowEnabled = enabled; break;
            case DebuffKind.Sludge: SludgeEnabled = enabled; break;
            case DebuffKind.Vulnerability: VulnerabilityEnabled = enabled; break;
            case DebuffKind.Windburn: WindburnEnabled = enabled; break;
        }
    }

    public void Save(IDalamudPluginInterface pi) => pi.SavePluginConfig(this);
}

/// <summary>
/// The settings window. Two tabs:
///  - "Effects": the master enable switch, overall intensity, the per-debuff toggles, and the
///    Advanced section (chat lockout) - everything that applies to REAL game debuffs.
///  - "Moodles/Loci Support": the two custom-status rule editors
///    (<see cref="CustomStatusPanel"/>, <see cref="TooltipKeywordPanel"/>) - everything that
///    only fires because of a Moodles or Loci status. Kept on its own tab both because it's a
///    self-contained feature a lot of users will never touch (so it no longer sits between the
///    per-debuff list and the Advanced section), and because the two panels together are tall
///    enough that stacking them under the main list made for one long scroll.
/// ImGui remembers the selected tab per session automatically, since the tab bar is keyed by the
/// same ID every frame.
/// </summary>
public sealed class ConfigWindow : Window
{
    private readonly Configuration _config;
    private readonly Action _save;
    private readonly CustomStatusPanel _customStatuses;
    private readonly TooltipKeywordPanel _tooltipKeywords;

    public ConfigWindow(Configuration config, Action save, CustomStatusWatcher customStatuses)
        : base("Real Debuffs Settings###RealDebuffsConfig")
    {
        _config = config;
        _save = save;
        _customStatuses = new CustomStatusPanel(config, customStatuses);
        _tooltipKeywords = new TooltipKeywordPanel(config, customStatuses);
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

    /// <summary>Everything that applies to REAL game debuffs: master switches, intensity, per-debuff toggles, chat lockout, and the in-game test panel.</summary>
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

        changed |= EffectToggle(DebuffKind.Blind, "Blind", "Screen darkens with a heavy vignette.");
        changed |= EffectToggle(DebuffKind.Paralysis, "Paralysis", "Crackling electric arcs around the screen edges.");
        changed |= EffectToggle(DebuffKind.Silence, "Silence", "Floating purple glyphs drift from the edges. See the Advanced section below for an actual chat lockout.");
        changed |= EffectToggle(DebuffKind.Stun, "Stun / Down for the Count", "Twinkling stars orbit near the top of the screen.");
        changed |= EffectToggle(DebuffKind.Sleep, "Sleep", "Soft blue tint with drowsy Zs drifting up from the corners.");
        changed |= EffectToggle(DebuffKind.Poison, "Poison", "Sickly green tint with drips falling from the top.");
        changed |= EffectToggle(DebuffKind.Bind, "Bind", "Roots creep up from the bottom of the screen.");
        changed |= SkinPicker(DebuffKind.Bind, "##BindSkin");
        changed |= EffectToggle(DebuffKind.Heavy, "Heavy", "A heavy dark pull with a dragging chain at the bottom of the screen.");
        changed |= SkinPicker(DebuffKind.Heavy, "##HeavySkin");
        changed |= EffectToggle(DebuffKind.Petrification, "Petrification", "Color drains out and stone cracks spread in from the edges.");
        changed |= EffectToggle(DebuffKind.Amnesia, "Amnesia", "A hazy gray fog rolls in, with drifting question marks - like your memory's been wiped.");
        changed |= EffectToggle(DebuffKind.Bleeding, "Bleeding", "Dark red drips bead and fall from the top edge.");
        changed |= EffectToggle(DebuffKind.Weakness, "Weakness / Brush with Death / Brink of Death", "A slow, heavy red pulse - faint for Weakness, strongest for Brink of Death.");
        changed |= EffectToggle(DebuffKind.Burns, "Burns", "A warm orange glow with embers rising from the bottom edge.");
        changed |= EffectToggle(DebuffKind.Charm, "Infatuated / Seduced", "A soft pink haze with drifting hearts - fainter for Infatuated, fuller for Seduced.");
        changed |= EffectToggle(DebuffKind.Frost, "Frostbite / Deep Freeze", "Icy blue creeps in from the edges - a faint rime for Frostbite, a heavy crust for Deep Freeze.");
        changed |= EffectToggle(DebuffKind.Disease, "Disease", "A dull, sickly olive tint with slow spores drifting past.");
        changed |= EffectToggle(DebuffKind.Doom, "Doom", "Dark red cracks creep in from the edges, pulsing like a countdown.");
        changed |= EffectToggle(DebuffKind.Dropsy, "Dropsy", "Heavy blue droplets drip from the top edge.");
        changed |= EffectToggle(DebuffKind.Electrocution, "Electrocution", "A buzzing yellow-white static flicker along the edges.");
        changed |= EffectToggle(DebuffKind.Hysteria, "Hysteria", "Jittery purple-red scribbles at the edges, like your mind's coming apart.");
        changed |= EffectToggle(DebuffKind.Infirmity, "Infirmity", "A pale, washed-out tint with dust drifting slowly down.");
        changed |= EffectToggle(DebuffKind.Misery, "Misery", "A heavy dark-blue tint with slow, falling tears.");
        changed |= EffectToggle(DebuffKind.Pacification, "Pacification", "A soft restraining glow pulses along the bottom edge.");
        changed |= EffectToggle(DebuffKind.Slow, "Slow", "A faint amber syrup drips slowly from the bottom - deliberately the subtlest effect here.");
        changed |= EffectToggle(DebuffKind.Sludge, "Sludge", "Thick brown mud drips from the top edge.");
        changed |= EffectToggle(DebuffKind.Vulnerability, "Vulnerability Up", "A faint red edge outline, kept minimal since this is common in modern raids.");
        changed |= EffectToggle(DebuffKind.Windburn, "Windburn", "Pale streaks blow across the screen edges.");

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

        DebugTester.DrawUi(kind => _config.Enabled && _config.IsEnabled(kind)); // TEST-TOOLS: delete this line (and DebugTester.cs) to remove the test panel

        return changed;
    }

    /// <summary>Everything that only fires because of a Moodles or Loci status: the name-based rule list, and the tooltip-keyword rule list (with its master toggle and tester).</summary>
    private bool DrawMoodlesTab()
    {
        bool changed = false;

        changed |= _customStatuses.Draw();

        ImGui.Separator();

        changed |= _tooltipKeywords.Draw();

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

    private static readonly string[] StrandSkinNames = { "Tentacles", "Chains" };

    /// <summary>
    /// The small indented "Visual style" combo shown under a reskinnable effect's checkbox - see
    /// IReskinnableEffect and Effects/IStrandSkin.cs. Only Bind and Heavy have one today, but any
    /// future strand-based effect gets one for free just by adding a case to
    /// Configuration.GetSkin/SetSkin and a call to this here.
    /// </summary>
    private bool SkinPicker(DebuffKind kind, string hiddenId)
    {
        int idx = (int)_config.GetSkin(kind);

        ImGui.Indent();
        ImGui.SetNextItemWidth(160);
        bool changed = ImGui.Combo("Visual style" + hiddenId, ref idx, StrandSkinNames, StrandSkinNames.Length);
        ImGui.Unindent();

        if (changed) _config.SetSkin(kind, (StrandSkinKind)idx);
        return changed;
    }
}
