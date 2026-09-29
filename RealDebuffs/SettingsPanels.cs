using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using RealDebuffs.Effects;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs;

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

        _kinds = effects
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.Kind)
            .ToArray();
        _kindNames = effects
            .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.DisplayName)
            .ToArray();

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
/// "Tooltip keywords" section: master toggle and the keyword rule list (with an in-header add
/// row). The paste-in tester now lives in the Effect generator section, where it sits next to
/// the material slots it exercises.
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
            _selectedKind = kinds[idx];

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
    /// Emit dropdown for stroke slots. Options: "(from material)", "(none)", and every particle
    /// material that declares at least one stroke emission.
    /// </summary>
    private bool DrawEmitRow(DebuffKind kind, in SwappableSlot slot)
    {
        var particleNames = new List<string>();
        var particleLabels = new List<string>();
        foreach (var name in MaterialRegistry.ParticleNames)
        {
            IParticleMaterial mat;
            try { mat = MaterialRegistry.GetParticle(name); } catch { continue; }
            if (mat.Emissions.Length == 0) continue;
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