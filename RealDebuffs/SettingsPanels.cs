using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using RealDebuffs.Effects;
using RealDebuffs.Effects.Framework;
namespace RealDebuffs;

/// <summary>Settings window: Effects tab, and Moodles/Loci Support tab (custom statuses, tooltip keywords, effect styles).</summary>
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
        var roster = new EffectRoster(effects);
        _sortedEffects = roster.Sorted;
        _customStatuses = new CustomStatusPanel(config, customStatuses, roster);
        _tooltipKeywords = new TooltipKeywordPanel(config, roster, effects);
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

        // Alphabetical for scanning; EffectManager's own order is a separate (layering) concern.
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

/// <summary>The effect roster sorted by display name, plus the kind pickers the rule panels share.</summary>
internal sealed class EffectRoster
{
    public readonly ISceneEffect[] Sorted;
    private readonly DebuffKind[] _kinds;
    private readonly string[] _names;

    /// <summary>Index of Bind (the add-row's starting pick), or 0.</summary>
    public readonly int DefaultIndex;

    public EffectRoster(IReadOnlyList<ISceneEffect> effects)
    {
        Sorted = effects.OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        _kinds = Sorted.Select(e => e.Kind).ToArray();
        _names = Sorted.Select(e => e.DisplayName).ToArray();
        DefaultIndex = Math.Max(0, Array.IndexOf(_kinds, DebuffKind.Bind));
    }

    /// <summary>Kind picker for an existing rule; returns the new kind when the user changed it.</summary>
    public DebuffKind? RuleCombo(DebuffKind current)
    {
        int i = Math.Max(0, Array.IndexOf(_kinds, current));
        return ImGui.Combo("##kind", ref i, _names, _names.Length) ? _kinds[i] : null;
    }

    /// <summary>Kind picker for an add-row; index is the caller's selection state.</summary>
    public DebuffKind NewKindCombo(ref int index)
    {
        ImGui.Combo("##newkind", ref index, _names, _names.Length);
        return _kinds[Math.Clamp(index, 0, _kinds.Length - 1)];
    }
}

/// <summary>"Custom statuses" section: name -&gt; effect rules.</summary>
internal sealed class CustomStatusPanel
{
    private const float NameWidth = 180f;
    private const float KindWidth = 130f;

    private readonly Configuration _config;
    private readonly CustomStatusWatcher _watcher;

    private readonly EffectRoster _roster;

    private string _newName = "";
    private int _newKind;
    private int _pick = -1;

    private CustomStatusSnapshot? _labelsFor;
    private string[] _labels = Array.Empty<string>();

    public CustomStatusPanel(Configuration config, CustomStatusWatcher watcher, EffectRoster roster)
    {
        _config = config;
        _watcher = watcher;
        _roster = roster;
        _newKind = roster.DefaultIndex;
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
                ImGui.SetNextItemWidth(KindWidth);
                if (_roster.RuleCombo(rule.Kind) is { } picked) { rule.Kind = picked; changed = true; }

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
            var newKind = _roster.NewKindCombo(ref _newKind);

            string cleaned = StatusNames.Clean(_newName);
            string key = StatusNames.Key(cleaned);
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

    // Snapshot reference is stable between heartbeats, so this only rebuilds when it actually changes.
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

/// <summary>"Tooltip keywords" section: master toggle, each effect's built-in words (switch off a whole effect or single words), and the user's extra words.</summary>
internal sealed class TooltipKeywordPanel
{
    private const float KeywordWidth = 240f;
    private const float KindWidth = 130f;

    private readonly Configuration _config;
    private readonly EffectRoster _roster;
    private readonly IReadOnlyList<ISceneEffect> _effects;

    private string _newKeywords = "";
    private int _newKind;

    private bool _clearAllRequested;
    private bool _resetDefaultsRequested;

    public TooltipKeywordPanel(Configuration config, EffectRoster roster, IReadOnlyList<ISceneEffect> effects)
    {
        _config = config;
        _roster = roster;
        _effects = effects;
        _newKind = roster.DefaultIndex;
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

            ImGui.TextWrapped("Look for words inside a status's tooltip - useful when the title alone isn't telling enough. " +
                              "Every effect already understands its own words (and their plurals, -ed and -ing forms); add your own below.");

            ImGui.Spacing();

            changed |= DrawBuiltIn();

            ImGui.Spacing();

            string rulesLabel = rules.Count == 1 ? "Extra words (1)###rules" : $"Extra words ({rules.Count})###rules";
            if (ImGui.CollapsingHeader(rulesLabel))
            {
                ImGui.BeginDisabled(rules.Count == 0);
                if (ImGui.Button("Clear all##rulesclear")) _clearAllRequested = true;
                ImGui.EndDisabled();
                ImGui.SameLine();
                if (ImGui.Button("Reset all word settings##rulesreset")) _resetDefaultsRequested = true;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Removes your extra words and turns every built-in word back on.");

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
                    ImGui.SetNextItemWidth(KindWidth);
                    if (_roster.RuleCombo(rule.Kind) is { } picked) { rule.Kind = picked; changed = true; }

                    ImGui.SameLine();
                    if (ImGui.Button("X##rm")) removeAt = i;

                    ImGui.PopID();
                }
                if (removeAt >= 0) { rules.RemoveAt(removeAt); changed = true; }

                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
                ImGui.TextDisabled("Add");

                ImGui.SetNextItemWidth(KeywordWidth);
                ImGui.InputTextWithHint("##newkeywords", "flame, burning, scorch...", ref _newKeywords, 512);

                ImGui.SameLine();
                ImGui.SetNextItemWidth(KindWidth);
                var newKind = _roster.NewKindCombo(ref _newKind);

                var parsedNew = TooltipKeywordRule.ParseKeywords(_newKeywords);

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
                    ImGui.TextDisabled("  One of those words is already in your extras for this effect.");
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
                TooltipKeywordRule.ResetToDefaults(_config);
                changed = true;
            }
        }
        finally { ImGui.PopID(); }

        return changed;
    }

    /// <summary>Built-in words per effect: switch an effect's words off, or mute single words.</summary>
    private bool DrawBuiltIn()
    {
        bool changed = false;

        if (!ImGui.CollapsingHeader("Built-in words###builtin")) return false;

        foreach (var effect in _effects)
        {
            if (effect.TriggerKeywords.Count == 0) continue;
            var kind = effect.Kind;

            ImGui.PushID((int)kind);
            if (ImGui.TreeNode($"{kind}###bi"))
            {
                bool on = !_config.TooltipOffKinds.Contains(kind);
                if (ImGui.Checkbox("Trigger from these words##on", ref on))
                {
                    if (on) _config.TooltipOffKinds.Remove(kind);
                    else    _config.TooltipOffKinds.Add(kind);
                    changed = true;
                }

                _config.MutedWords.TryGetValue(kind, out var muted);

                ImGui.BeginDisabled(!on);
                for (int i = 0; i < effect.TriggerKeywords.Count; i++)
                {
                    string word = effect.TriggerKeywords[i];
                    bool wordOn = muted is null || !muted.Contains(word, StringComparer.OrdinalIgnoreCase);
                    if (ImGui.Checkbox($"{word}##w{i}", ref wordOn))
                    {
                        if (wordOn)
                        {
                            muted?.RemoveAll(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase));
                            if (muted is { Count: 0 }) _config.MutedWords.Remove(kind);
                        }
                        else
                        {
                            if (muted is null) _config.MutedWords[kind] = muted = new List<string>();
                            muted.Add(word);
                        }
                        changed = true;
                    }
                    if (i % 3 != 2 && i < effect.TriggerKeywords.Count - 1) ImGui.SameLine(0f, 14f);
                }
                ImGui.EndDisabled();

                ImGui.TreePop();
            }
            ImGui.PopID();
        }

        return changed;
    }
}

/// <summary>"Effect generator": paste-in description tester plus a per-effect material editor.</summary>
internal sealed class EffectStylePanel
{
    private readonly Configuration _config;
    private string _testText = "";
    private DebuffKind _selectedKind = DebuffKind.Blind;
    private bool _advanced;

    // Cached once: both registries are fully populated before the first panel is built.
    private static readonly DebuffKind[] KindValues;
    private static readonly string[] KindNames;
    private static readonly string[] StrokeNames, StrokeLabels;
    private static readonly string[] ParticleNames, ParticleLabels;
    private static readonly string[] RegionNames, RegionLabels;
    private static readonly string[] EmitOptions, EmitOptionIds;

    // Stored value for "rainbow" is the canonical spelling; the parser also accepts "rgb".
    private static readonly string[] _colorNames = ColorLexicon.Named.Keys
        .Concat(new[] { "rainbow" })
        .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static readonly string[] _colorOptions = new[] { "(default)" }
        .Concat(_colorNames.Select(DisplayNameForColor))
        .ToArray();

    private static string DisplayNameForColor(string name) =>
        string.Equals(name, "rainbow", StringComparison.OrdinalIgnoreCase) ? "Rainbow/RGB" : name;

    static EffectStylePanel()
    {
        KindValues = EffectRegistry.KindsWithSlots;
        KindNames = KindValues.Select(k => k.ToString()).ToArray();

        StrokeNames = MaterialRegistry.StrokeNames.ToArray();
        StrokeLabels = StrokeNames.Select(FriendlyMaterialName).ToArray();
        ParticleNames = MaterialRegistry.ParticleNames.ToArray();
        ParticleLabels = ParticleNames.Select(FriendlyMaterialName).ToArray();
        RegionNames = MaterialRegistry.RegionNames.ToArray();
        RegionLabels = RegionNames.Select(FriendlyMaterialName).ToArray();

        var emitNames = new List<string>();
        var emitLabels = new List<string>();
        foreach (var name in ParticleNames)
        {
            var mat = MaterialRegistry.TryGetParticle(name);
            if (mat is null || mat.Emissions.Length == 0) continue;
            emitNames.Add(name);
            emitLabels.Add(FriendlyMaterialName(name));
        }
        EmitOptions = new[] { "(from material)", "(none)" }.Concat(emitLabels).ToArray();
        EmitOptionIds = new[] { "", "__none__" }.Concat(emitNames).ToArray();
    }

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
                ImGui.TextWrapped("Paste a status description to see what it triggers, or build a look: what an effect is made of, " +
                                  "what's on it, and up to two colours. Changes apply live.");

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

    /// <summary>The look being edited, read back from the live overrides.</summary>
    private Look CurrentLook(DebuffKind kind) => LookApply.FromOverrides(kind, _config.MaterialOverrides, ColorOf(_config.ColorOverrides, kind), ColorOf(_config.ModifierColorOverrides, kind));

    private static LookColor? ColorOf(Dictionary<DebuffKind, string> map, DebuffKind kind) =>
        map.TryGetValue(kind, out var name) && ColorLexicon.TryLookColor(name, out var c) ? c : null;

    /// <summary>The phrase if the words can say it, else the code; what the tester is filled with after an edit.</summary>
    private string ExportText(DebuffKind kind)
    {
        var look = CurrentLook(kind);
        return LookText.Phrase(look, TooltipLanguage.For(_config)) ?? LookCode.Encode(look);
    }

    /// <summary>Changing the effect combo doesn't reset the previous effect; Reset is an explicit button.</summary>
    private bool DrawEffectEditor()
    {
        bool changed = false;

        if (KindValues.Length == 0)
        {
            ImGui.TextDisabled("  (no effects with customizable materials yet)");
            return false;
        }

        int idx = Array.IndexOf(KindValues, _selectedKind);
        if (idx < 0) idx = 0;

        ImGui.TextDisabled("Effect");
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("##effectselect", ref idx, KindNames, KindNames.Length))
        {
            _selectedKind = KindValues[idx];
            _testText = ExportText(_selectedKind);
        };

        var kind = _selectedKind;
        var lex = TooltipLanguage.For(_config);
        var look = CurrentLook(kind);
        string? phrase = LookText.Phrase(look, lex);
        string code = LookCode.Encode(look);

        ImGui.SameLine();
        ImGui.BeginDisabled(phrase is null);
        if (ImGui.SmallButton("Copy phrase##effectcopy"))
        {
            ImGui.SetClipboardText(phrase!);
            _testText = phrase!;
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(phrase is null
                ? "This look uses settings words can't say - copy the code instead."
                : $"Copies to clipboard and fills the tester above:\n\n\"{phrase}\"");

        ImGui.SameLine();
        if (ImGui.SmallButton("Copy code##effectcode"))
        {
            ImGui.SetClipboardText(code);
            _testText = code;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Exact and always works, even for advanced slots. Paste it anywhere in a status description:\n\n{code}");

        // Wide gap before Reset so a misclick on Copy can't land on the destructive button.
        ImGui.SameLine(0f, 32f);

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.95f, 0.55f, 0.35f, 1f));
        if (ImGui.SmallButton("Reset##effectreset"))
        {
            if (ResetToDefaults(kind))
            {
                changed = true;
                _testText = ExportText(kind);
            }
        }
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Returns {kind} to its built-in defaults.\n\nOnly affects the currently-selected effect.");

        ImGui.Spacing();
        ImGui.TextDisabled("Phrase");
        ImGui.SameLine();
        if (phrase is not null) ImGui.TextWrapped(phrase);
        else ImGui.TextDisabled("(code only)");
        ImGui.TextDisabled("Code  ");
        ImGui.SameLine();
        ImGui.TextWrapped(code);

        ImGui.Spacing();

        var body = EffectRegistry.BodyFor(kind);
        var modifier = EffectRegistry.ModifierFor(kind);
        string? modifierKey = LookApply.ModifierKey(kind);
        bool edited = false;

        // Both rows are drawn every frame even if only one reports a change, so evaluate in order.
        if (body is { } bodySlot)
            edited |= DrawSlotRow(kind, bodySlot, $"Made of ({bodySlot.Label})");

        if (modifier is { } modSlot)
            edited |= DrawSlotRow(kind, modSlot, $"With ({modSlot.Label})");
        else if (body is { Type: SlotType.Stroke } strokeBody)
            edited |= DrawEmitRow(kind, strokeBody, "With (what it sheds)");

        edited |= DrawColorRow(kind, "Colour", _config.ColorOverrides);
        if (modifierKey is not null)
            edited |= DrawColorRow(kind, "Modifier colour", _config.ModifierColorOverrides);

        ImGui.Spacing();
        ImGui.Checkbox("Advanced: every material slot##advanced", ref _advanced);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Shows the remaining slots. Words can't reach these - copy the code to share a look that uses them.");

        if (_advanced)
        {
            ImGui.Indent();
            foreach (var slot in EffectRegistry.SlotsFor(kind))
            {
                bool isBody = body is { } b && b.Type == slot.Type && b.Role == slot.Role && b.Region == slot.Region;
                bool isModifier = modifier is { } m && m.Type == slot.Type && m.Role == slot.Role;
                bool emitIsModifier = modifier is null && isBody;

                if (!isBody && !isModifier) edited |= DrawSlotRow(kind, slot, slot.Label);
                if (slot.Type == SlotType.Stroke && !emitIsModifier) edited |= DrawEmitRow(kind, slot, $"Emits ({slot.Label})");
            }
            ImGui.Unindent();
        }

        if (edited)
        {
            changed = true;
            _testText = ExportText(kind);
            RefreshActivePreview();
        }

        return changed;
    }

    /// <summary>Removes every override belonging to kind: colours, materials and emit axes.</summary>
    private bool ResetToDefaults(DebuffKind kind)
    {
        bool removedAny = false;

        if (_config.ColorOverrides.Remove(kind)) removedAny = true;
        if (_config.ModifierColorOverrides.Remove(kind)) removedAny = true;

        foreach (var slot in EffectRegistry.SlotsFor(kind))
        {
            if (_config.MaterialOverrides.Remove(KeyFor(kind, slot)))
                removedAny = true;

            if (slot.Type == SlotType.Stroke
                && _config.MaterialOverrides.Remove(MaterialOverrideKey.ForStrokeEmit(kind, slot.Role)))
                removedAny = true;
        }

        return removedAny;
    }

    /// <summary>Paste-in tester: shows what each effect was understood to be; Preview forces every matched look on screen for 15s.</summary>
    private void DrawTester()
    {
        ImGui.PushID("GeneratorTester");
        try
        {
            ImGui.TextDisabled("Test a status description");

            ImGui.InputTextMultiline("##testtext", ref _testText, 1024, new Vector2(-1, 60));

            var result = TooltipLanguage.Parse(_testText, _config);
            long now = Environment.TickCount64;

            if (_testText.Trim().Length == 0)
            {
                ImGui.TextDisabled("  (paste a description above)");
            }
            else if (result.Looks.Count == 0)
            {
                ImGui.TextDisabled("  No effect recognised.");
            }
            else
            {
                foreach (var look in result.Looks)
                    DrawLookLine(look, now);
            }

            foreach (var note in result.Notes) ImGui.TextDisabled("  " + note);
            foreach (var hint in result.Hints) ImGui.TextDisabled("  " + hint);

            ImGui.Spacing();
            ImGui.BeginDisabled(result.Looks.Count == 0);
            if (ImGui.Button("Preview", new Vector2(-1, 0)))
            {
                foreach (var look in result.Looks)
                    DebugTester.ForceLook(look);
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Shows the matched effects on screen for 15 seconds, with their materials and colours.");
        }
        finally { ImGui.PopID(); }
    }

    /// <summary>"[swatch] Heavy  made of Silk  with [swatch] Ember".</summary>
    private static void DrawLookLine(Look look, long now)
    {
        if (look.BodyColor is { } c)
        {
            var rgb = c.Resolve(now);
            ImGui.TextColored(new Vector4(rgb.X, rgb.Y, rgb.Z, 1f), "\u25a0");
            ImGui.SameLine();
            ImGui.Text(look.Kind.ToString());
        }
        else
        {
            ImGui.Text("    " + look.Kind);
        }

        if (look.Body is { } body)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"  made of {FriendlyMaterialName(body)}");
        }

        if (look.Modifier is { } mod)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(mod == Look.NoShed ? "  shedding nothing" : "  with");
            if (mod != Look.NoShed)
            {
                if (look.ModifierColor is { } mc)
                {
                    var rgb = mc.Resolve(now);
                    ImGui.SameLine();
                    ImGui.TextColored(new Vector4(rgb.X, rgb.Y, rgb.Z, 1f), "\u25a0");
                }
                ImGui.SameLine();
                ImGui.Text(FriendlyMaterialName(mod));
            }
        }

        if (look.Details is { Count: > 0 } d)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"  +{d.Count} advanced slot{(d.Count == 1 ? "" : "s")}");
        }
    }

    private bool DrawSlotRow(DebuffKind kind, in SwappableSlot slot, string label)
    {
        var (names, labels) = MaterialTables(slot.Type);
        if (names.Length == 0) return false;

        string key = KeyFor(kind, slot);
        string defaultName = slot.DefaultMaterial;

        string current = _config.MaterialOverrides.TryGetValue(key, out var o) ? o : defaultName;
        int idx = Array.IndexOf(names, current);
        if (idx < 0) idx = 0;

        ImGui.SetNextItemWidth(200f);
        bool changed = ImGui.Combo(label, ref idx, labels, labels.Length);
        if (changed)
        {
            string picked = names[idx];
            if (picked == defaultName) _config.MaterialOverrides.Remove(key);
            else                       _config.MaterialOverrides[key] = picked;
        }
        return changed;
    }

    private static (string[] Names, string[] Labels) MaterialTables(SlotType type) => type switch
    {
        SlotType.Stroke   => (StrokeNames, StrokeLabels),
        SlotType.Particle => (ParticleNames, ParticleLabels),
        _                 => (RegionNames, RegionLabels),
    };

    /// <summary>After an edit, refresh the preview of the selected effect if it is on screen.</summary>
    private void RefreshActivePreview() => DebugTester.UpdateForcedLook(CurrentLook(_selectedKind));

    private bool DrawColorRow(DebuffKind kind, string label, Dictionary<DebuffKind, string> store)
    {
        bool changed = false;

        string current = store.TryGetValue(kind, out var name) ? name : "";

        // Older configs stored "rgb"; map it so the dropdown shows the right selection.
        if (string.Equals(current, "rgb", StringComparison.OrdinalIgnoreCase))
            current = "rainbow";

        int idx = 0;
        if (current.Length > 0)
        {
            int found = Array.FindIndex(_colorNames, n => string.Equals(n, current, StringComparison.OrdinalIgnoreCase));
            if (found >= 0) idx = found + 1;
        }

        ImGui.SetNextItemWidth(200f);
        if (ImGui.Combo(label, ref idx, _colorOptions, _colorOptions.Length))
        {
            if (idx == 0)
            {
                if (store.Remove(kind)) changed = true;
            }
            else
            {
                store[kind] = _colorNames[idx - 1];
                changed = true;
            }
        }

        // Read-only swatch (not ColorButton): a custom picked color has no name to store back.
        if (current.Length > 0 && ColorLexicon.TryToken(current, out Vector4 swatchRgb))
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

    /// <summary>Emit dropdown for stroke slots: "(from material)", "(none)", or a particle material.</summary>
    private bool DrawEmitRow(DebuffKind kind, in SwappableSlot slot, string label)
    {
        if (EmitOptions.Length == 2) return false;  // no particle material declares emissions

        string key = MaterialOverrideKey.ForStrokeEmit(kind, slot.Role);

        string current = _config.MaterialOverrides.TryGetValue(key, out var o) ? o : "";
        int idx = Array.IndexOf(EmitOptionIds, current);
        if (idx < 0) idx = 0;

        ImGui.SetNextItemWidth(200f);
        bool changed = ImGui.Combo($"{label}##emit{kind}{slot.Role}", ref idx, EmitOptions, EmitOptions.Length);

        if (changed)
        {
            string picked = EmitOptionIds[idx];
            if (picked.Length == 0) _config.MaterialOverrides.Remove(key);
            else                    _config.MaterialOverrides[key] = picked;
        }
        return changed;
    }

    private static string KeyFor(DebuffKind kind, in SwappableSlot slot) => MaterialOverrideKey.For(kind, slot);

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

/// <summary>Preview an effect without a debuff: visual only (never triggers the chat lockout), temporary, nothing saved.</summary>
internal static class DebugTester
{
    private const float Seconds = 15f;

    private static readonly Dictionary<DebuffKind, long> EndsAt = new();
    private static readonly Dictionary<DebuffKind, Vector4?> ForcedColor = new();
    private static readonly Dictionary<DebuffKind, Look> ForcedLooks = new();

    /// <summary>Bumps when a forced look changes, so the manager re-resolves its inputs.</summary>
    public static int Version { get; private set; }

    /// <summary>The looks of previews still on screen.</summary>
    public static IEnumerable<Look> ActiveLooks()
    {
        foreach (var kv in ForcedLooks)
            if (IsForced(kv.Key)) yield return kv.Value;
    }

    /// <summary>Previews a full look (materials and both colours), not just a colour.</summary>
    public static void ForceLook(Look look)
    {
        Force(look.Kind, true, look.BodyColor?.Resolve(Environment.TickCount64));
        ForcedLooks[look.Kind] = look;
        Version++;
    }

    /// <summary>Re-applies a look to a running preview without restarting its timer.</summary>
    public static void UpdateForcedLook(Look look)
    {
        if (!IsForced(look.Kind)) return;
        ForcedColor[look.Kind] = look.BodyColor?.Resolve(Environment.TickCount64);
        ForcedLooks[look.Kind] = look;
        Version++;
    }

    /// <summary>Removes expired previews and invalidates cached material/modifier overrides once.</summary>
    public static void ExpirePreviews()
    {
        long now = Environment.TickCount64;
        List<DebuffKind>? expired = null;
        foreach (var (kind, end) in EndsAt)
            if (end > 0 && end <= now)
                (expired ??= new List<DebuffKind>()).Add(kind);

        if (expired is null) return;
        foreach (var kind in expired)
        {
            EndsAt.Remove(kind);
            ForcedColor.Remove(kind);
            ForcedLooks.Remove(kind);
        }
        Version++;
    }

    public static bool IsForced(DebuffKind kind) =>
        EndsAt.TryGetValue(kind, out long end) && end > Environment.TickCount64;

    public static Vector4? GetForcedColor(DebuffKind kind) =>
        IsForced(kind) && ForcedColor.TryGetValue(kind, out var c) ? c : null;

    public static void Force(DebuffKind kind, bool on) => Force(kind, on, null);

    public static void Force(DebuffKind kind, bool on, Vector4? color)
    {
        if (!on)
        {
            EndsAt.Remove(kind);
            ForcedColor.Remove(kind);
            if (ForcedLooks.Remove(kind)) Version++;
            return;
        }

        EndsAt[kind] = Environment.TickCount64 + (long)(Seconds * 1000f);
        ForcedColor[kind] = color;
        if (ForcedLooks.Remove(kind)) Version++;
    }

    public static void StopAll()
    {
        if (EndsAt.Count == 0 && ForcedColor.Count == 0 && ForcedLooks.Count == 0) return;
        EndsAt.Clear();
        ForcedColor.Clear();
        ForcedLooks.Clear();
        Version++;
    }

    public static void DrawUi(Func<DebuffKind, bool> isShowing, IReadOnlyList<ISceneEffect> effects)
    {
        ImGui.Separator();
        if (!ImGui.CollapsingHeader("Test effects (dev only)")) return;

        // Own ID scope: labels repeat the settings checkboxes and ImGui would otherwise sync them.
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
                StopAll();
        }
        finally
        {
            ImGui.PopID();
        }
    }
}