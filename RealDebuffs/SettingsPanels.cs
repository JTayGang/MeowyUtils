using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using RealDebuffs.Effects.Framework;
using System.Collections.Generic;

namespace RealDebuffs;

/// <summary>
/// "Custom statuses" section: name → effect rules.
/// </summary>
internal sealed class CustomStatusPanel
{
    private const float NameWidth = 180f;
    private const float KindWidth = 130f;

    private static readonly DebuffKind[] Kinds = Enum.GetValues<DebuffKind>();
    private static readonly string[] KindNames = Array.ConvertAll(Kinds, k => k.ToString());

    private readonly Configuration _config;
    private readonly CustomStatusWatcher _watcher;

    private string _newName = "";
    private int _newKind = Array.IndexOf(Kinds, DebuffKind.Bind);
    private int _pick = -1;

    private CustomStatusSnapshot? _labelsFor;
    private string[] _labels = Array.Empty<string>();

    public CustomStatusPanel(Configuration config, CustomStatusWatcher watcher)
    {
        _config = config;
        _watcher = watcher;
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

            // Connection line only shows when something's missing, so the common case is quiet.
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
                int kind = Math.Max(0, Array.IndexOf(Kinds, rule.Kind));
                ImGui.SetNextItemWidth(KindWidth);
                if (ImGui.Combo("##kind", ref kind, KindNames, KindNames.Length)) { rule.Kind = Kinds[kind]; changed = true; }

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
            ImGui.Combo("##newkind", ref _newKind, KindNames, KindNames.Length);

            string cleaned = StatusNames.Clean(_newName);
            string key = StatusNames.Key(cleaned);
            var newKind = Kinds[Math.Clamp(_newKind, 0, Kinds.Length - 1)];
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
/// "Tooltip keywords" section: master toggle, keyword rules, and a tester.
/// </summary>
internal sealed class TooltipKeywordPanel
{
    private const float KeywordWidth = 240f;
    private const float KindWidth = 130f;

    private static readonly DebuffKind[] Kinds = Enum.GetValues<DebuffKind>();
    private static readonly string[] KindNames = Array.ConvertAll(Kinds, k => k.ToString());

    private readonly Configuration _config;
    private readonly CustomStatusWatcher _watcher;

    private string _newKeywords = "";
    private int _newKind = Array.IndexOf(Kinds, DebuffKind.Bind);
    private string _testText = "";

    private bool _clearAllRequested;
    private bool _resetDefaultsRequested;

    public TooltipKeywordPanel(Configuration config, CustomStatusWatcher watcher)
    {
        _config = config;
        _watcher = watcher;
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
                    int kind = Math.Max(0, Array.IndexOf(Kinds, rule.Kind));
                    ImGui.SetNextItemWidth(KindWidth);
                    if (ImGui.Combo("##kind", ref kind, KindNames, KindNames.Length)) { rule.Kind = Kinds[kind]; changed = true; }

                    ImGui.SameLine();
                    if (ImGui.Button("X##rm")) removeAt = i;

                    ImGui.PopID();
                }
                if (removeAt >= 0) { rules.RemoveAt(removeAt); changed = true; }
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
                rules.Clear();
                rules.AddRange(TooltipKeywordRule.Defaults());
                changed = true;
            }

            ImGui.Spacing();
            ImGui.TextDisabled("Add");

            ImGui.SetNextItemWidth(KeywordWidth);
            ImGui.InputTextWithHint("##newkeywords", "flame, burning, scorch...", ref _newKeywords, 512);

            ImGui.SameLine();
            ImGui.SetNextItemWidth(KindWidth);
            ImGui.Combo("##newkind", ref _newKind, KindNames, KindNames.Length);

            var parsedNew = TooltipKeywordRule.ParseKeywords(_newKeywords);
            var newKind = Kinds[Math.Clamp(_newKind, 0, Kinds.Length - 1)];

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

            ImGui.Spacing();
            DrawTester();
        }
        finally { ImGui.PopID(); }

        return changed;
    }

    private void DrawTester()
    {
        if (!ImGui.CollapsingHeader("Test tooltip text")) return;

        ImGui.PushID("Tester");
        try
        {
            ImGui.InputTextMultiline("##testtext", ref _testText, 1024, new Vector2(-1, 60));

            var matches = TooltipKeywordParser.Parse(_testText, _config.TooltipKeywordRules);

            if (_testText.Trim().Length == 0)
            {
                ImGui.TextDisabled("  (paste a tooltip above)");
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
                        ImGui.Text(m.Kind.ToString());
                    }
                }
            }

            ImGui.Spacing();
            ImGui.BeginDisabled(matches.Count == 0);
            if (ImGui.Button("Preview##testerpreview"))
            {
                foreach (var m in matches)
                    DebugTester.Force(m.Kind, true, m.Color);
            }
            ImGui.EndDisabled();
        }
        finally { ImGui.PopID(); }
    }
}

/// <summary>
/// "Effect styles" section: per-effect material choices. Each row corresponds to one swappable
/// slot in one effect, resolved at render time via EffectSceneRenderer's override lookup. The
/// slot list is a curated roster - when a new effect is ported to the framework, add its
/// swappable slots here.
/// </summary>
internal sealed class EffectStylePanel
{
    private readonly record struct Slot(DebuffKind Kind, string PrimitiveType, PrimitiveRole Role, string Label);

    private static readonly Slot[] Slots =
    {
        new(DebuffKind.Blind,   "Region",   PrimitiveRole.MainStroke, "Screen wash"),
        new(DebuffKind.Burns,   "Particle", PrimitiveRole.Ember,      "Fire particles"),
        new(DebuffKind.Burns,   "Region",   PrimitiveRole.MainStroke, "Ground band"),
        new(DebuffKind.Disease, "Stroke",   PrimitiveRole.MainStroke, "Tendrils"),
        new(DebuffKind.Frost,   "Particle", PrimitiveRole.Snowflake,  "Snowflakes"),
        new(DebuffKind.Frost,   "Particle", PrimitiveRole.Snow,       "Snow specks"),
        new(DebuffKind.Frost,   "Particle", PrimitiveRole.Fog,        "Fog"),
        new(DebuffKind.Frost,   "Region",   PrimitiveRole.MainStroke, "Intro flash"),
        new(DebuffKind.Heavy,   "Stroke",   PrimitiveRole.MainStroke, "Chains"),
    };

    private readonly Configuration _config;

    public EffectStylePanel(Configuration config) { _config = config; }

    public bool Draw()
    {
        bool changed = false;

        ImGui.PushID("EffectStyles");
        try
        {
            ImGui.TextDisabled("Effect styles");
            ImGui.TextWrapped("Choose what each effect is made of. Changes apply live.");

            ImGui.Spacing();

            foreach (var kind in Slots.Select(s => s.Kind).Distinct())
            {
                ImGui.Spacing();
                ImGui.TextDisabled(kind.ToString());
                ImGui.Indent();

                foreach (var slot in Slots.Where(s => s.Kind == kind))
                {
                    changed |= DrawSlotRow(slot);
                    if (slot.PrimitiveType == "Stroke")
                        changed |= DrawEmitRow(slot);
                }

                ImGui.Unindent();
            }
        }
        finally { ImGui.PopID(); }

        return changed;
    }

    private bool DrawSlotRow(Slot slot)
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
        string key = MaterialOverrideKey.For(slot.Kind, slot.PrimitiveType, slot.Role);
        string defaultName = DefaultMaterialNameFor(slot);

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
    /// material that declares a stroke emission. Selecting "(from material)" removes the key so
    /// the stroke material's own emissions apply; "(none)" pins an empty string so it stays
    /// silent regardless of what the material declares.
    /// </summary>
    private bool DrawEmitRow(Slot slot)
    {
        // Build the option list: two sentinels + every particle material with a non-null Emission.
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

        string key = MaterialOverrideKey.ForStrokeEmit(slot.Kind, slot.Role);

        // Current selection: "" default, "__none__" explicit-disable, else specific material.
        string current = _config.MaterialOverrides.TryGetValue(key, out var o) ? o : "";
        int idx = Array.IndexOf(optionIds, current);
        if (idx < 0) idx = 0;

        ImGui.Indent();
        ImGui.SetNextItemWidth(200f);
        bool changed = ImGui.Combo($"Emits##{slot.Kind}{slot.Role}", ref idx, options, options.Length);
        ImGui.Unindent();

        if (changed)
        {
            string picked = optionIds[idx];
            if (picked.Length == 0) _config.MaterialOverrides.Remove(key);
            else                    _config.MaterialOverrides[key] = picked;
        }
        return changed;
    }

    private static string DefaultMaterialNameFor(Slot slot)
    {
        string roleKey = slot.PrimitiveType == "Region"
            ? (slot.Label.Contains("wash", StringComparison.OrdinalIgnoreCase) ||
               slot.Label.Contains("flash", StringComparison.OrdinalIgnoreCase) ? "FlatFill" : "EdgeGlow")
            : slot.Role.ToString();

        return BuiltInDefaults.Get(slot.Kind, slot.PrimitiveType, roleKey)
            ?? slot.PrimitiveType switch
            {
                "Stroke"   => BuiltInDefaults.FallbackStroke(),
                "Particle" => BuiltInDefaults.FallbackParticle(slot.Role),
                "Region"   => slot.Label.Contains("wash", StringComparison.OrdinalIgnoreCase) ||
                              slot.Label.Contains("flash", StringComparison.OrdinalIgnoreCase)
                              ? "region.flat-fill" : "region.edge-glow",
                _ => "",
            };
    }

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