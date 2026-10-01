using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using RealDebuffs.Effects;
using RealDebuffs.Effects.Framework;
namespace RealDebuffs;

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
    private readonly ISceneEffect[] _sortedEffects;

    public ConfigWindow(Configuration config, Action save, CustomStatusWatcher customStatuses, IReadOnlyList<ISceneEffect> effects)
        : base("Real Debuffs Settings###RealDebuffsConfig")
    {
        _config = config;
        _save = save;
        _effects = effects;
        _sortedEffects = effects.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
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
        foreach (var effect in _sortedEffects)
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

/// <summary>
/// "Custom statuses" section: name -> effect rules.
/// </summary>
internal sealed class CustomStatusPanel
{
    private const float NameWidth = 180f;
    private const float KindWidth = 130f;

    private readonly Configuration _config;
    private readonly CustomStatusWatcher _watcher;
    private readonly IReadOnlyList<ISceneEffect> _effects;

    // Built from the effect roster: only kinds that have an effect behind them. Sorted
    // alphabetically for scanning. Same list drives both the display in existing rules and
    // the picker in the add row.
    private readonly DebuffKind[] _kinds;
    private readonly string[] _kindNames;

    private string _newName = "";
    private int _newKind = 0;
    private int _pick = -1;

    private CustomStatusSnapshot? _labelsFor;
    private string[] _labels = Array.Empty<string>();

    public CustomStatusPanel(Configuration config, CustomStatusWatcher watcher, IReadOnlyList<ISceneEffect> effects)
    {
        _config = config;
        _watcher = watcher;
        _effects = effects;

        var sorted = effects
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _kinds = sorted.Select(e => e.Kind).ToArray();
        _kindNames = sorted.Select(e => e.DisplayName).ToArray();

        _newKind = Array.IndexOf(_kinds, DebuffKind.Bind);
        if (_newKind < 0) _newKind = 0;
    }

    public bool Draw()
    {
        bool changed = false;
        var snapshot = _watcher.Snapshot;
        var rules = _config.CustomStatusRules;

        ImGui.PushID("CustomStatuses");
        try
        {
            ImGui.TextDisabled("Custom statuses");
            ImGui.TextWrapped("Show an effect while a Moodles or Loci status is on you.");

            if (!_watcher.MoodlesAvailable || !_watcher.LociAvailable)
            {
                var missing = !_watcher.MoodlesAvailable ? "Moodles" : "Loci";
                ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), $"  {missing} not detected.");
            }

            ImGui.Spacing();

            if (rules.Count == 0)
                ImGui.TextDisabled("  (no rules yet)");

            int removeAt = -1;
            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                ImGui.PushID(i);

                bool enabled = rule.Enabled;
                if (ImGui.Checkbox("##on", ref enabled)) { rule.Enabled = enabled; changed = true; }

                ImGui.SameLine();
                string name = rule.Name;
                ImGui.SetNextItemWidth(NameWidth);
                if (ImGui.InputTextWithHint("##name", "Status name", ref name, 128)) { rule.Name = name; changed = true; }

                ImGui.SameLine();
                int kind = Math.Max(0, Array.IndexOf(_kinds, rule.Kind));
                ImGui.SetNextItemWidth(KindWidth);
                if (ImGui.Combo("##kind", ref kind, _kindNames, _kindNames.Length)) { rule.Kind = _kinds[kind]; changed = true; }

                ImGui.SameLine();
                if (ImGui.Button("X##rm")) removeAt = i;

                if (snapshot.Contains(rule.GetKey()))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "active");
                }

                ImGui.PopID();
            }
            if (removeAt >= 0) { rules.RemoveAt(removeAt); changed = true; }

            ImGui.Spacing();
            ImGui.TextDisabled("Add");

            if (snapshot.Statuses.Count > 0)
            {
                RebuildLabels(snapshot);
                if (_pick >= _labels.Length) _pick = -1;

                ImGui.SetNextItemWidth(NameWidth + KindWidth + 30f);
                if (ImGui.Combo("Pick a status you have##pick", ref _pick, _labels, _labels.Length)
                    && _pick >= 0 && _pick < snapshot.Statuses.Count)
                {
                    _newName = snapshot.Statuses[_pick].Name;
                    _pick = -1;
                }
            }

            ImGui.SetNextItemWidth(NameWidth);
            ImGui.InputTextWithHint("##newname", "Status name", ref _newName, 128);

            ImGui.SameLine();
            ImGui.SetNextItemWidth(KindWidth);
            ImGui.Combo("##newkind", ref _newKind, _kindNames, _kindNames.Length);

            string cleaned = StatusNames.Clean(_newName);
            string key = StatusNames.Key(cleaned);
            var newKind = _kinds[Math.Clamp(_newKind, 0, _kinds.Length - 1)];
            bool duplicate = rules.Any(r => r.Kind == newKind && r.GetKey() == key);

            ImGui.SameLine();
            ImGui.BeginDisabled(key.Length == 0 || duplicate);
            if (ImGui.Button("Add##add"))
            {
                rules.Add(new CustomStatusRule { Name = cleaned, Kind = newKind });
                _newName = "";
                changed = true;
            }
            ImGui.EndDisabled();

            if (duplicate && key.Length > 0)
                ImGui.TextDisabled("  Already set to that effect.");
        }
        finally { ImGui.PopID(); }

        return changed;
    }

    private void RebuildLabels(CustomStatusSnapshot snapshot)
    {
        if (ReferenceEquals(_labelsFor, snapshot)) return;

        var statuses = snapshot.Statuses;
        var labels = new string[statuses.Count];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = statuses[i].Name;

        _labels = labels;
        _labelsFor = snapshot;
    }
}

/// <summary>
/// "Tooltip keywords" section: master toggle and the keyword rule list (with an in-header add row).
/// </summary>
internal sealed class TooltipKeywordPanel
{
    private const float KeywordWidth = 240f;
    private const float KindWidth = 130f;

    private readonly Configuration _config;
    private readonly CustomStatusWatcher _watcher;

    private readonly DebuffKind[] _kinds;
    private readonly string[] _kindNames;
    private readonly IReadOnlyList<ISceneEffect> _effects;

    private string _newKeywords = "";
    private int _newKind = 0;

    private bool _clearAllRequested;
    private bool _resetDefaultsRequested;

    public TooltipKeywordPanel(Configuration config, CustomStatusWatcher watcher, IReadOnlyList<ISceneEffect> effects)
    {
        _config = config;
        _watcher = watcher;
        _effects = effects;

        var sorted = effects
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _kinds = sorted.Select(e => e.Kind).ToArray();
        _kindNames = sorted.Select(e => e.DisplayName).ToArray();

        _newKind = Array.IndexOf(_kinds, DebuffKind.Bind);
        if (_newKind < 0) _newKind = 0;
    }

    public bool Draw()
    {
        bool changed = false;
        var rules = _config.TooltipKeywordRules;

        ImGui.PushID("TooltipKeywords");
        try
        {
            bool enabled = _config.ParseCustomStatusTooltips;
            if (ImGui.Checkbox("Also scan tooltip text for keywords", ref enabled))
            {
                _config.ParseCustomStatusTooltips = enabled;
                changed = true;
            }

            ImGui.TextWrapped("Look for words inside a status's tooltip - useful when the title alone isn't telling enough.");

            ImGui.Spacing();

            string rulesLabel = rules.Count == 1 ? "Rules (1)###rules" : $"Rules ({rules.Count})###rules";
            if (ImGui.CollapsingHeader(rulesLabel))
            {
                ImGui.BeginDisabled(rules.Count == 0);
                if (ImGui.Button("Clear all##rulesclear")) _clearAllRequested = true;
                ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("Reset to defaults##rulesreset")) _resetDefaultsRequested = true;

                ImGui.Spacing();

                if (rules.Count == 0)
                    ImGui.TextDisabled("  (none)");

                int removeAt = -1;
                for (int i = 0; i < rules.Count; i++)
                {
                    var rule = rules[i];
                    ImGui.PushID(i);

                    bool ruleEnabled = rule.Enabled;
                    if (ImGui.Checkbox("##on", ref ruleEnabled)) { rule.Enabled = ruleEnabled; changed = true; }

                    ImGui.SameLine();
                    string keywords = rule.Keywords;
                    ImGui.SetNextItemWidth(KeywordWidth);
                    if (ImGui.InputTextWithHint("##keywords", "flame, burning, scorch...", ref keywords, 512)) { rule.Keywords = keywords; changed = true; }

                    ImGui.SameLine();
                    int kind = Math.Max(0, Array.IndexOf(_kinds, rule.Kind));
                    ImGui.SetNextItemWidth(KindWidth);
                    if (ImGui.Combo("##kind", ref kind, _kindNames, _kindNames.Length)) { rule.Kind = _kinds[kind]; changed = true; }

                    ImGui.SameLine();
                    if (ImGui.Button("X##rm")) removeAt = i;

                    ImGui.PopID();
                }
                if (removeAt >= 0) { rules.RemoveAt(removeAt); changed = true; }

                // ---- add row (inside the header, below the rules list) ----
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.TextDisabled("Add");

                ImGui.SetNextItemWidth(KeywordWidth);
                ImGui.InputTextWithHint("##newkeywords", "flame, burning, scorch...", ref _newKeywords, 512);

                ImGui.SameLine();
                ImGui.SetNextItemWidth(KindWidth);
                ImGui.Combo("##newkind", ref _newKind, _kindNames, _kindNames.Length);

                var parsedNew = TooltipKeywordRule.ParseKeywords(_newKeywords);
                var newKind = _kinds[Math.Clamp(_newKind, 0, _kinds.Length - 1)];

                bool duplicate = parsedNew.Length > 0 && rules.Any(r =>
                    r.Kind == newKind &&
                    r.ParsedKeywords.Intersect(parsedNew, StringComparer.OrdinalIgnoreCase).Any());

                ImGui.SameLine();
                ImGui.BeginDisabled(parsedNew.Length == 0 || duplicate);
                if (ImGui.Button("Add##addkeyword"))
                {
                    rules.Add(new TooltipKeywordRule { Keywords = string.Join(", ", parsedNew), Kind = newKind });
                    _newKeywords = "";
                    changed = true;
                }
                ImGui.EndDisabled();

                if (duplicate)
                    ImGui.TextDisabled("  One of those words is already driving this effect.");
            }

            if (_clearAllRequested)
            {
                _clearAllRequested = false;
                rules.Clear();
                changed = true;
            }
            if (_resetDefaultsRequested)
            {
                _resetDefaultsRequested = false;
                TooltipKeywordRule.ResetToDefaults(_config, _effects);
                changed = true;
            }
        }
        finally { ImGui.PopID(); }

        return changed;
    }
}

/// <summary>
/// "Effect generator" section: a paste-in description tester at the top, then a compact effect
/// editor below - one combo picks which effect to customize, and only that effect's slots are
/// shown. Copy exports the selected effect's style to the clipboard; Reset returns it to its
/// built-in defaults. Both buttons act on the currently-selected effect only.
///
/// The roster of effects shown in the combo, the slots each effect exposes, and every slot's
/// default material all come from EffectRegistry, which is populated once by Plugin from the
/// effects' own declarations. Nothing here is hardcoded: adding a new effect's slots means
/// adding them to that effect's Slots property, and this panel picks them up on the next draw.
/// </summary>
internal sealed class EffectStylePanel
{
    private readonly Configuration _config;

    private string _testText = "";

    /// <summary>Which effect the editor below the tester is currently showing. Defaults to the
    /// first effect in the registry; changed via the "Effect" combo. Persists for the session
    /// so switching back and forth doesn't lose what was on screen.</summary>
    private DebuffKind _selectedKind = DebuffKind.Blind;

    // Stored values, one per dropdown entry after the implicit "(default)". "rainbow" is the
    // canonical animated-hue value; the parser still accepts "rgb" (TooltipKeywordParser.RainbowWords).
    private static readonly string[] _colorNames = TooltipKeywordParser.NamedColors.Keys
        .Concat(new[] { "rainbow" })
        .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    // Parallel to _colorNames. Display names differ from stored names only for rainbow, which
    // is labelled "Rainbow/RGB" so users can see both spellings are accepted in tooltip text.
    private static readonly string[] _colorOptions = new[] { "(default)" }
        .Concat(_colorNames.Select(DisplayNameForColor))
        .ToArray();

    private static string DisplayNameForColor(string name) =>
        string.Equals(name, "rainbow", StringComparison.OrdinalIgnoreCase) ? "Rainbow/RGB" : name;

    public EffectStylePanel(Configuration config) { _config = config; }

    public bool Draw()
    {
        bool changed = false;

        ImGui.PushID("EffectStyles");
        try
        {
            ImGui.SetNextItemOpen(true, ImGuiCond.FirstUseEver);
            if (ImGui.CollapsingHeader("Effect generator###effectstyles"))
            {
                ImGui.TextWrapped("Paste a status description to see what it triggers, or customize what each effect is made of. Changes apply live.");

                DrawTester();

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();

                changed |= DrawEffectEditor();
            }
        }
        finally { ImGui.PopID(); }

        return changed;
    }

    /// <summary>
    /// The effect editor: a combo picks which kind to show, Copy exports that kind's style,
    /// Reset returns it to its defaults, and the slots for that kind follow below.
    ///
    /// Changing the combo does NOT reset the effect being left; Reset is an explicit button so a
    /// user exploring options doesn't lose edits to a previous effect.
    /// </summary>
    private bool DrawEffectEditor()
    {
        bool changed = false;

        var kinds = EffectRegistry.KindsWithSlots;
        if (kinds.Length == 0)
        {
            ImGui.TextDisabled("  (no effects with customizable materials yet)");
            return false;
        }

        var names = kinds.Select(k => k.ToString()).ToArray();

        int idx = Array.IndexOf(kinds, _selectedKind);
        if (idx < 0) idx = 0;

        ImGui.TextDisabled("Effect");
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("##effectselect", ref idx, names, names.Length))
        {
            _selectedKind = kinds[idx];
            _testText = BuildExportPhrase(_selectedKind);
        };

        ImGui.SameLine();
        if (ImGui.SmallButton("Copy##effectcopy"))
        {
            string phrase = BuildExportPhrase(_selectedKind);
            ImGui.SetClipboardText(phrase);
            _testText = phrase;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Copies to clipboard and fills the tester above:\n\n\"{BuildExportPhrase(_selectedKind)}\"");

        // Extra spacing before Reset so a misclick on Copy doesn't land on the destructive
        // button. 32px is well beyond the default item spacing (~8px), which is what makes the
        // gap read as intentional.
        ImGui.SameLine(0f, 32f);

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.55f, 0.35f, 1f));
        if (ImGui.SmallButton("Reset##effectreset"))
        {
            if (ResetToDefaults(_selectedKind))
            {
                changed = true;
                _testText = BuildExportPhrase(_selectedKind);
            }
        }
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Returns {_selectedKind} to its built-in defaults.\n\nOnly affects the currently-selected effect.");

        ImGui.Spacing();

        if (DrawColorRow(_selectedKind))
        {
            changed = true;
            _testText = BuildExportPhrase(_selectedKind);
            RefreshActivePreview();
        }

        ImGui.Spacing();

        foreach (var slot in EffectRegistry.SlotsFor(_selectedKind))
        {
            // Evaluate both in order - can't use || because we still want to draw the emit row
            // even when the slot row reports a change.
            bool slotChanged = DrawSlotRow(_selectedKind, slot);
            bool emitChanged = slot.PrimitiveType == "Stroke" && DrawEmitRow(_selectedKind, slot);

            if (slotChanged || emitChanged)
            {
                changed = true;
                _testText = BuildExportPhrase(_selectedKind);
            }
        }

        return changed;
    }

    /// <summary>
    /// Removes every material override belonging to <paramref name="kind"/> - both the material
    /// axis (stroke/particle/region) and the emit axis for any stroke slot. Returns true if
    /// anything was actually removed, so the caller can decide whether to save.
    /// </summary>
    private bool ResetToDefaults(DebuffKind kind)
    {
        bool removedAny = false;

        if (_config.ColorOverrides.Remove(kind))
            removedAny = true;

        foreach (var slot in EffectRegistry.SlotsFor(kind))
        {
            if (_config.MaterialOverrides.Remove(KeyFor(kind, slot)))
                removedAny = true;

            if (slot.PrimitiveType == "Stroke"
                && _config.MaterialOverrides.Remove(MaterialOverrideKey.ForStrokeEmit(kind, slot.Role)))
                removedAny = true;
        }

        return removedAny;
    }

    /// <summary>
    /// The paste-in tester. Type or paste a status description; the parser runs it against the
    /// current keyword rules and shows what each match resolved to. The Preview button forces
    /// every matched kind on screen for 15s (with its resolved color), so the visual result can
    /// be checked without applying a Moodle.
    ///
    /// NOTE: material substitutions ("made of snow") are shown in the match list, but the
    /// Preview button only forces the KIND with its color. The material swap that a substitution
    /// requests is applied per-frame via the effect's own config, not through DebugTester.
    /// </summary>
    private void DrawTester()
    {
        ImGui.PushID("GeneratorTester");
        try
        {
            ImGui.TextDisabled("Test a status description");

            ImGui.InputTextMultiline("##testtext", ref _testText, 1024, new Vector2(-1, 60));

            var matches = TooltipKeywordParser.Parse(_testText, _config.TooltipKeywordRules);

            if (_testText.Trim().Length == 0)
            {
                ImGui.TextDisabled("  (paste a description above)");
            }
            else if (matches.Count == 0)
            {
                ImGui.TextDisabled("  No matches.");
            }
            else
            {
                foreach (var m in matches)
                {
                    if (m.Color is { } c)
                    {
                        ImGui.TextColored(new Vector4(c.X, c.Y, c.Z, 1f), "\u25a0");
                        ImGui.SameLine();
                        ImGui.Text(m.Kind.ToString());
                    }
                    else
                    {
                        ImGui.Text("    " + m.Kind);
                    }

                    if (m.MaterialSubstitution is { } mat)
                    {
                        string? word = TooltipKeywordParser.CanonicalMaterialWord(mat);
                        string display = word != null ? $"{mat} ({word})" : mat;
                        ImGui.SameLine();
                        ImGui.TextDisabled($"  -  made of {display}");
                    }
                }
            }

            ImGui.Spacing();
            ImGui.BeginDisabled(matches.Count == 0);
            if (ImGui.Button("Preview", new Vector2(-1, 0)))
            {
                foreach (var m in matches)
                    DebugTester.Force(m.Kind, true, m.Color);
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Shows the matched effects on screen for 15 seconds.");
        }
        finally { ImGui.PopID(); }
    }

    /// <summary>
    /// The description phrase a user would paste into a Moodle/Loci status to reproduce this
    /// effect's style. Trigger word comes from the user's own rules; material word comes from
    /// the "made of X" vocabulary. Material axis wins over emit axis when both are customized.
    /// </summary>
    private string BuildExportPhrase(DebuffKind kind)
    {
        string trigger = CanonicalTrigger(kind);

        // Preset names and rainbow words are the only two families that round-trip cleanly as
        // adjectives. Hex codes and 0x-prefixed values are still accepted by the parser but
        // have no natural-language spelling, so they're skipped here. "rgb" is normalized to
        // "rainbow" so the exported phrase uses the canonical spelling.
        if (_config.ColorOverrides.TryGetValue(kind, out var colorName)
            && TooltipKeywordParser.IsColorWord(colorName))
        {
            if (string.Equals(colorName, "rgb", StringComparison.OrdinalIgnoreCase))
                colorName = "rainbow";
            trigger = $"{colorName} {trigger}";
        }

        var heroes = EffectRegistry.HeroSlotsFor(kind);
        if (heroes.Length == 0) return trigger;
        var hero = heroes[0];

        string matKey = MaterialOverrideKey.For(kind, hero.PrimitiveType, hero.Role);
        if (_config.MaterialOverrides.TryGetValue(matKey, out var matName))
        {
            string? word = TooltipKeywordParser.CanonicalMaterialWord(matName);
            if (word != null) return $"{trigger} made of {word}";
        }

        if (hero.PrimitiveType == "Stroke")
        {
            string emitKey = MaterialOverrideKey.ForStrokeEmit(kind, hero.Role);
            if (_config.MaterialOverrides.TryGetValue(emitKey, out var emitName)
                && emitName != "__none__")
            {
                string? word = TooltipKeywordParser.CanonicalMaterialWord(emitName);
                if (word != null) return $"{trigger} made of {word}";
            }
        }

        return trigger;
    }

    private string CanonicalTrigger(DebuffKind kind)
    {
        string? c = TooltipKeywordParser.CanonicalTriggerWord(kind, _config.TooltipKeywordRules);
        return c ?? kind.ToString().ToLowerInvariant();
    }

    private bool DrawSlotRow(DebuffKind kind, in SwappableSlot slot)
    {
        string[] names = slot.PrimitiveType switch
        {
            "Stroke"   => MaterialRegistry.StrokeNames.ToArray(),
            "Particle" => MaterialRegistry.ParticleNames.ToArray(),
            "Region"   => MaterialRegistry.RegionNames.ToArray(),
            _ => Array.Empty<string>(),
        };
        if (names.Length == 0) return false;

        string[] labels = names.Select(FriendlyMaterialName).ToArray();
        string key = KeyFor(kind, slot);
        string defaultName = slot.DefaultMaterial;

        string current = _config.MaterialOverrides.TryGetValue(key, out var o) ? o : defaultName;
        int idx = Array.IndexOf(names, current);
        if (idx < 0) idx = 0;

        ImGui.SetNextItemWidth(200f);
        bool changed = ImGui.Combo(slot.Label, ref idx, labels, labels.Length);
        if (changed)
        {
            string picked = names[idx];
            if (picked == defaultName) _config.MaterialOverrides.Remove(key);
            else                       _config.MaterialOverrides[key] = picked;
        }
        return changed;
    }

    /// <summary>
    /// Re-parses the current tester text and refreshes the forced color on any kind that's
    /// currently being previewed. Called after the color dropdown changes so a running preview
    /// recolors to match without needing Preview clicked again.
    ///
    /// Deliberately the same parse path Preview uses (tooltip text -> matches -> per-match color), so
    /// the forced color matches what a Moodle with that description would produce, including the
    /// same-clause color resolution ("black flame" takes its color from the word, not the dropdown).
    /// No-op when nothing is being previewed or the rebuilt phrase matches nothing.
    /// </summary>
    private void RefreshActivePreview()
    {
        foreach (var m in TooltipKeywordParser.Parse(_testText, _config.TooltipKeywordRules))
            DebugTester.UpdateForcedColor(m.Kind, m.Color);
    }

    /// <summary>
    /// Per-effect color dropdown. Sits above the material slots in the editor and shares their
    /// layout conventions: fixed-width control, label to the right, swatch to the right of that.
    /// "(default)" removes any override; picking a color stores its name in
    /// <see cref="Configuration.ColorOverrides"/>. Returns true if the config changed.
    /// </summary>
    private bool DrawColorRow(DebuffKind kind)
    {
        bool changed = false;

        string current = _config.ColorOverrides.TryGetValue(kind, out var name) ? name : "";

        // Older configs stored "rgb" for rainbow; map it so the dropdown shows the right selection.
        if (string.Equals(current, "rgb", StringComparison.OrdinalIgnoreCase))
            current = "rainbow";

        int idx = 0;
        if (current.Length > 0)
        {
            int found = Array.FindIndex(_colorNames, n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
            if (found >= 0) idx = found + 1;
        }

        ImGui.SetNextItemWidth(200f);
        if (ImGui.Combo("Color", ref idx, _colorOptions, _colorOptions.Length))
        {
            if (idx == 0)
            {
                if (_config.ColorOverrides.Remove(kind)) changed = true;
            }
            else
            {
                _config.ColorOverrides[kind] = _colorNames[idx - 1];
                changed = true;
            }
        }

        // Swatch, drawn with the window's draw list rather than ColorButton so it stays a pure
        // read-only indicator (ColorButton would open a picker, and a picked custom color has no
        // name to store back in ColorOverrides).
        if (current.Length > 0 && TooltipKeywordParser.TryResolveColorToken(current, out var swatchRgb))
        {
            ImGui.SameLine();
            Vector2 swatchMin  = ImGui.GetCursorScreenPos();
            Vector2 swatchSize = new(16f, ImGui.GetTextLineHeight());
            ImGui.GetWindowDrawList().AddRectFilled(
                swatchMin, swatchMin + swatchSize,
                ImGui.ColorConvertFloat4ToU32(swatchRgb), 3f);
            ImGui.Dummy(swatchSize);
        }

        return changed;
    }

    /// <summary>
    /// Emit dropdown for stroke slots. Options: "(from material)", "(none)", and every particle
    /// material that declares at least one stroke emission.
    /// </summary>
    private bool DrawEmitRow(DebuffKind kind, in SwappableSlot slot)
    {
        var particleNames = new List<string>();
        var particleLabels = new List<string>();
        foreach (var name in MaterialRegistry.ParticleNames)
        {
            var mat = MaterialRegistry.TryGetParticle(name);
            if (mat is null || mat.Emissions.Length == 0) continue;
            particleNames.Add(name);
            particleLabels.Add(FriendlyMaterialName(name));
        }
        if (particleNames.Count == 0) return false;

        string[] options = new[] { "(from material)", "(none)" }
            .Concat(particleLabels)
            .ToArray();
        string[] optionIds = new[] { "", "__none__" }
            .Concat(particleNames)
            .ToArray();

        string key = MaterialOverrideKey.ForStrokeEmit(kind, slot.Role);

        string current = _config.MaterialOverrides.TryGetValue(key, out var o) ? o : "";
        int idx = Array.IndexOf(optionIds, current);
        if (idx < 0) idx = 0;

        ImGui.Indent();
        ImGui.SetNextItemWidth(200f);
        bool changed = ImGui.Combo($"Emits##{kind}{slot.Role}", ref idx, options, options.Length);
        ImGui.Unindent();

        if (changed)
        {
            string picked = optionIds[idx];
            if (picked.Length == 0) _config.MaterialOverrides.Remove(key);
            else                    _config.MaterialOverrides[key] = picked;
        }
        return changed;
    }

    /// <summary>
    /// The override-dictionary key for a slot. Region slots use the edge-mask-based key the
    /// renderer actually reads; strokes and particles use the (kind, type, role) key.
    /// </summary>
    private static string KeyFor(DebuffKind kind, in SwappableSlot slot) =>
        slot.RegionKind is { } rk
            ? MaterialOverrideKey.ForRegion(kind, rk)
            : MaterialOverrideKey.For(kind, slot.PrimitiveType, slot.Role);

    private static string FriendlyMaterialName(string name)
    {
        int dot = name.IndexOf('.');
        string tail = dot >= 0 ? name[(dot + 1)..] : name;
        var parts = tail.Split('-');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        }
        return string.Join(' ', parts);
    }
}

/// <summary>
/// Preview an effect without needing a matching debuff to actually be active. Callers: the
/// dev-only test panel at the bottom of the settings window, and the tooltip-keyword tester's
/// "preview on screen" button.
///
/// Visual only - hooked in after EffectManager tells ChatBlocker whether you're silenced, so a
/// test never blocks real chat. Always temporary (some effects, like Blind at high intensity,
/// could hide the checkbox you'd need to switch them off). Respects the master Enabled, per-effect
/// toggles, cutscene/GPose hiding, and the intensity slider. Nothing is saved; reload clears it.
///
/// The panel iterates the effect roster passed by ConfigWindow, so only implemented effects get
/// a checkbox. A DebuffKind without an ISceneEffect has nothing to preview and doesn't appear.
/// </summary>
internal static class DebugTester
{
    private const float Seconds = 15f;

    // kind -> TickCount64 ms when its test ends. Missing or past = not being tested.
    private static readonly Dictionary<DebuffKind, long> EndsAt = new();

    // kind -> color to force, set alongside EndsAt. Only meaningful while IsForced is also true.
    private static readonly Dictionary<DebuffKind, Vector4?> ForcedColor = new();

    public static bool IsForced(DebuffKind kind) =>
        EndsAt.TryGetValue(kind, out long end) && end > Environment.TickCount64;

    /// <summary>What color a forced test wants, or null for "use the effect's own".</summary>
    public static Vector4? GetForcedColor(DebuffKind kind) =>
        ForcedColor.TryGetValue(kind, out var c) ? c : null;

    public static void Force(DebuffKind kind, bool on) => Force(kind, on, null);

    /// <summary>
    /// Starts or stops a test, optionally recolored (see DrawHelpers.PushColorOverride for what a
    /// non-null color does). Used by the dev checkboxes (always null) and the tooltip-tester's
    /// preview button (whatever it just parsed).
    /// </summary>
    public static void Force(DebuffKind kind, bool on, Vector4? color)
    {
        EndsAt[kind] = on ? Environment.TickCount64 + (long)(Seconds * 1000f) : 0L;
        ForcedColor[kind] = color;
    }

    /// <summary>
    /// Updates the color on an already-forced test without touching its end time. Used by the
    /// effect generator so changing the color dropdown mid-preview recolors the running preview
    /// instead of requiring the user to re-click Preview (which would restart the 15s timer).
    /// No-op when the kind isn't currently being tested.
    /// </summary>
    public static void UpdateForcedColor(DebuffKind kind, Vector4? color)
    {
        if (IsForced(kind))
            ForcedColor[kind] = color;
    }

    /// <summary>
    /// Draws the panel. <paramref name="isShowing"/> says whether an effect is allowed to appear
    /// at all right now; it's only used to add an "(off in settings)" hint so a test that shows
    /// nothing explains itself. <paramref name="effects"/> is the roster to iterate; only
    /// implemented effects get a checkbox.
    /// </summary>
    public static void DrawUi(Func<DebuffKind, bool> isShowing, IReadOnlyList<ISceneEffect> effects)
    {
        ImGui.Separator();
        if (!ImGui.CollapsingHeader("Test effects (dev only)")) return;

        // Own ID scope: labels repeat the settings-window checkboxes, and ImGui would otherwise
        // treat them as the same widgets (ticking one would tick both).
        ImGui.PushID("TestTools");
        try
        {
            ImGui.TextWrapped(
                $"Shows an effect for {Seconds:0}s as if you had the debuff. Visual only - it never " +
                "triggers the chat lockout. Effects switched off above still won't show.");

            long now = Environment.TickCount64;
            foreach (var effect in effects)
            {
                var kind = effect.Kind;
                bool on = IsForced(kind);
                if (ImGui.Checkbox(effect.DisplayName, ref on))
                    Force(kind, on);

                if (EndsAt.TryGetValue(kind, out long end) && end > now)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"{(end - now + 999) / 1000}s");
                }

                if (!isShowing(kind))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("(off in settings)");
                }
            }

            if (ImGui.Button("All off"))
                EndsAt.Clear();
        }
        finally
        {
            ImGui.PopID();
        }
    }
}
