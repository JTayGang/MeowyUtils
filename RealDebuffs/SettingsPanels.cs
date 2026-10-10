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

    /// <summary>Kind picker for an add-row; <paramref name="index"/> is the caller's selection state.</summary>
    public DebuffKind NewKindCombo(ref int index)
    {
        ImGui.Combo("##newkind", ref index, _names, _names.Length);
        return _kinds[Math.Clamp(index, 0, _kinds.Length - 1)];
    }
}

/// <summary>"Custom statuses" section: name -> effect rules.</summary>
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

/// <summary>"Tooltip keywords" section: master toggle plus the keyword rule list and add row.</summary>
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

/// <summary>"Effect generator": paste-in description tester plus a per-effect material editor.</summary>
internal sealed class EffectStylePanel
{
    private readonly Configuration _config;
    private string _testText = "";
    private DebuffKind _selectedKind = DebuffKind.Blind;

    // Cached once: both registries are fully populated before the first panel is built.
    private static readonly DebuffKind[] KindValues;
    private static readonly string[] KindNames;
    private static readonly string[] StrokeNames, StrokeLabels;
    private static readonly string[] ParticleNames, ParticleLabels;
    private static readonly string[] RegionNames, RegionLabels;
    private static readonly string[] EmitOptions, EmitOptionIds;

    // Stored value for "rainbow" is the canonical spelling; the parser also accepts "rgb".
    private static readonly string[] _colorNames = TooltipKeywordParser.NamedColors.Keys
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

        // Wide gap before Reset so a misclick on Copy can't land on the destructive button.
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
            // Both are drawn every frame even if only one reports a change, so evaluate in order.
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

    /// <summary>Removes every override belonging to <paramref name="kind"/> on both the material and emit axes.</summary>
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

    /// <summary>Paste-in tester; Preview forces each matched kind on screen for 15s (material substitutions apply live, not in preview).</summary>
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

    /// <summary>The Moodle phrase for this effect's style; the material axis wins over the emit axis.</summary>
    private string BuildExportPhrase(DebuffKind kind)
    {
        string trigger = CanonicalTrigger(kind);

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
        var (names, labels) = MaterialTables(slot.PrimitiveType);
        if (names.Length == 0) return false;

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

    private static (string[] Names, string[] Labels) MaterialTables(string primitiveType) => primitiveType switch
    {
        "Stroke"   => (StrokeNames, StrokeLabels),
        "Particle" => (ParticleNames, ParticleLabels),
        "Region"   => (RegionNames, RegionLabels),
        _          => (Array.Empty<string>(), Array.Empty<string>()),
    };

    /// <summary>After a colour change, refresh the forced colour on any previewed kind.</summary>
    private void RefreshActivePreview()
    {
        foreach (var m in TooltipKeywordParser.Parse(_testText, _config.TooltipKeywordRules))
            DebugTester.UpdateForcedColor(m.Kind, m.Color);
    }

    private bool DrawColorRow(DebuffKind kind)
    {
        bool changed = false;

        string current = _config.ColorOverrides.TryGetValue(kind, out var name) ? name : "";

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

        // Read-only swatch (not ColorButton): a custom picked color has no name to store back.
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

    /// <summary>Emit dropdown for stroke slots: "(from material)", "(none)", or a particle material.</summary>
    private bool DrawEmitRow(DebuffKind kind, in SwappableSlot slot)
    {
        if (EmitOptions.Length == 2) return false;   // no particle material declares emissions

        string key = MaterialOverrideKey.ForStrokeEmit(kind, slot.Role);

        string current = _config.MaterialOverrides.TryGetValue(key, out var o) ? o : "";
        int idx = Array.IndexOf(EmitOptionIds, current);
        if (idx < 0) idx = 0;

        ImGui.Indent();
        ImGui.SetNextItemWidth(200f);
        bool changed = ImGui.Combo($"Emits##{kind}{slot.Role}", ref idx, EmitOptions, EmitOptions.Length);
        ImGui.Unindent();

        if (changed)
        {
            string picked = EmitOptionIds[idx];
            if (picked.Length == 0) _config.MaterialOverrides.Remove(key);
            else                    _config.MaterialOverrides[key] = picked;
        }
        return changed;
    }

    /// <summary>Region slots key by edge-mask tag; strokes and particles by (kind, type, role).</summary>
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

/// <summary>Preview an effect without a debuff: visual only (never triggers the chat lockout), temporary, nothing saved.</summary>
internal static class DebugTester
{
    private const float Seconds = 15f;

    private static readonly Dictionary<DebuffKind, long> EndsAt = new();
    private static readonly Dictionary<DebuffKind, Vector4?> ForcedColor = new();

    public static bool IsForced(DebuffKind kind) =>
        EndsAt.TryGetValue(kind, out long end) && end > Environment.TickCount64;

    public static Vector4? GetForcedColor(DebuffKind kind) =>
        ForcedColor.TryGetValue(kind, out var c) ? c : null;

    public static void Force(DebuffKind kind, bool on) => Force(kind, on, null);

    public static void Force(DebuffKind kind, bool on, Vector4? color)
    {
        EndsAt[kind] = on ? Environment.TickCount64 + (long)(Seconds * 1000f) : 0L;
        ForcedColor[kind] = color;
    }

    /// <summary>Recolours a running preview without restarting its timer.</summary>
    public static void UpdateForcedColor(DebuffKind kind, Vector4? color)
    {
        if (IsForced(kind))
            ForcedColor[kind] = color;
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
                EndsAt.Clear();
        }
        finally
        {
            ImGui.PopID();
        }
    }
}