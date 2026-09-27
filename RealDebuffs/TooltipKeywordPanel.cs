using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs;

/// <summary>
/// The "Tooltip keywords" section of the settings window: the master checkbox, the list of
/// keyword-rule -> effect rules (each holding a comma-separated keyword list, an optional fallback
/// tint, and a strength slider), and a paste-in tester for checking what a piece of text would
/// trigger without needing to actually apply a Moodle/Loci status in-game first. Kept in its own
/// file for the same reason <see cref="CustomStatusPanel"/> is: Configuration.cs only has to call
/// <see cref="Draw"/>.
///
/// This is intentionally a SEPARATE panel from <see cref="CustomStatusPanel"/> rather than a mode
/// bolted onto it - see <see cref="TooltipKeywordRule"/>'s remarks for why the two rule types don't
/// share one model, which is the same reason they don't share one editor.
/// </summary>
internal sealed class TooltipKeywordPanel
{
    private const float KeywordWidth = 220f;
    private const float KindWidth = 110f;
    private const float StrengthWidth = 90f;

    private static readonly DebuffKind[] Kinds = Enum.GetValues<DebuffKind>();
    private static readonly string[] KindNames = Array.ConvertAll(Kinds, k => k.ToString());
    private static readonly Vector4 ActiveColor = new(0.4f, 1f, 0.4f, 1f);
    private static readonly Vector4 DefaultTint = new(1f, 1f, 1f, 1f);

    private readonly Configuration _config;
    private readonly CustomStatusWatcher _watcher;

    // State of the "add" row; only lives as long as the window does.
    private string _newKeywords = "";
    private int _newKind = Array.IndexOf(Kinds, DebuffKind.Bind);

    // State of the tester; only lives as long as the window does.
    private string _testText = "";

    public TooltipKeywordPanel(Configuration config, CustomStatusWatcher watcher)
    {
        _config = config;
        _watcher = watcher;
    }

    /// <summary>Draws the section. Returns true if anything changed, so the caller knows to save.</summary>
    public bool Draw()
    {
        bool changed = false;
        var rules = _config.TooltipKeywordRules;

        // Own ID scope: rows reuse the same short labels as CustomStatusPanel's - without this,
        // ImGui would treat e.g. both panels' "##kind" combos as the same widget.
        ImGui.PushID("TooltipKeywords");
        try
        {
            bool enabled = _config.ParseCustomStatusTooltips;
            if (ImGui.Checkbox("Also read status tooltips for keywords", ref enabled))
            {
                _config.ParseCustomStatusTooltips = enabled;
                changed = true;
            }

            ImGui.TextWrapped(
                "On top of matching a status's NAME above, also scan its full tooltip text for the " +
                "words below. A match can show an effect the same way a name-based rule does, but can " +
                "ALSO pick up a color straight from the text itself - either an explicit color tag " +
                "(the same [color=] tag Moodles/Loci already support in titles) or a plain color word " +
                "like \"pink\" or \"green\" near the matched word. This is a heuristic over free-form " +
                "text, not an exact science - use the tester near the bottom to check a specific " +
                "tooltip before relying on it. Each rule holds several comma-separated words (e.g. " +
                "\"flame, burning, scorch\"), all driving the same effect and sharing the same tint " +
                "and strength.");

            if (!enabled)
                ImGui.TextDisabled("  (off - the rules below are kept, but nothing is matched against them yet)");

            ImGui.Spacing();

            // ---- existing rules ----
            if (rules.Count == 0)
                ImGui.TextDisabled("  (no keyword rules yet)");

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
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Separate multiple words with commas. Blank entries and duplicates are ignored; so are newlines and semicolons, so a pasted list works too.");

                ImGui.SameLine();
                int kind = Math.Max(0, Array.IndexOf(Kinds, rule.Kind));
                ImGui.SetNextItemWidth(KindWidth);
                if (ImGui.Combo("##kind", ref kind, KindNames, KindNames.Length)) { rule.Kind = Kinds[kind]; changed = true; }

                ImGui.SameLine();
                if (ImGui.Button("X##rm")) removeAt = i;

                if (IsCurrentlyMatching(rule))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ActiveColor, "active");
                }

                // Second line: the optional fallback tint, and the strength dial - kept off the
                // primary line above so it doesn't get too cramped for the panel's normal width.
                ImGui.Indent();

                bool hasColor = rule.Color.HasValue;
                if (ImGui.Checkbox("Tint##hascolor", ref hasColor))
                {
                    rule.Color = hasColor ? (rule.Color ?? DefaultTint) : null;
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Fallback color to use when this rule matches but the tooltip text itself doesn't specify one. Leave off to just use the effect's own normal color.");

                if (rule.Color is { } currentColor)
                {
                    ImGui.SameLine();
                    if (ImGui.ColorEdit4("##tint", ref currentColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
                    {
                        rule.Color = currentColor;
                        changed = true;
                    }
                }

                ImGui.SameLine();
                float strengthPercent = rule.Strength * 100f;
                ImGui.SetNextItemWidth(StrengthWidth);
                if (ImGui.SliderFloat("Strength##strength", ref strengthPercent, 10f, 100f, "%.0f%%"))
                {
                    rule.Strength = strengthPercent / 100f;
                    changed = true;
                }

                ImGui.Unindent();

                ImGui.PopID();
            }
            if (removeAt >= 0) { rules.RemoveAt(removeAt); changed = true; }

            // ---- add a rule ----
            ImGui.Spacing();
            ImGui.TextDisabled("Add a keyword rule");

            ImGui.SetNextItemWidth(KeywordWidth);
            ImGui.InputTextWithHint("##newkeywords", "flame, burning, scorch...", ref _newKeywords, 512);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Separate multiple words with commas. Blank entries and duplicates are ignored; so are newlines and semicolons, so a pasted list works too.");

            ImGui.SameLine();
            ImGui.SetNextItemWidth(KindWidth);
            ImGui.Combo("##newkind", ref _newKind, KindNames, KindNames.Length);

            var parsedNew = TooltipKeywordRule.ParseKeywords(_newKeywords);
            var newKind = Kinds[Math.Clamp(_newKind, 0, Kinds.Length - 1)];

            // Any keyword that already drives this same effect would be dead weight (and would
            // silently race the existing rule for the merge tie-break). Different kinds are fine
            // and common - "flame" driving Burns in one rule and Electrocution in another is
            // deliberate, so only same-kind collisions are flagged.
            bool duplicate = false;
            if (parsedNew.Length > 0)
            {
                foreach (var r in rules)
                {
                    if (r.Kind != newKind) continue;
                    foreach (var existing in r.ParsedKeywords)
                    {
                        if (parsedNew.Contains(existing, StringComparer.OrdinalIgnoreCase))
                        {
                            duplicate = true;
                            break;
                        }
                    }
                    if (duplicate) break;
                }
            }

            ImGui.SameLine();
            ImGui.BeginDisabled(parsedNew.Length == 0 || duplicate);
            if (ImGui.Button("Add##addkeyword"))
            {
                // Store the normalized re-join rather than the raw typed string, so a messy paste
                // ("flame,,  flames ,,,") lands in the config as exactly the parsed set the
                // duplicate check just validated.
                rules.Add(new TooltipKeywordRule { Keywords = string.Join(", ", parsedNew), Kind = newKind });
                _newKeywords = "";
                changed = true;
            }
            ImGui.EndDisabled();

            if (duplicate)
                ImGui.TextDisabled("  One or more of those words is already driving this effect.");
            else if (parsedNew.Length > 1)
                ImGui.TextDisabled($"  Will add {parsedNew.Length} keywords in one rule.");

            ImGui.TextDisabled("Not triggering? '/realdebuffs statuses' also logs tooltip matches (and why) when this is on.");

            ImGui.Spacing();
            DrawTester();
        }
        finally
        {
            ImGui.PopID();
        }

        return changed;
    }

    /// <summary>Whether <paramref name="rule"/>'s keywords are currently found in any active status's tooltip - purely a live "yes, this would fire" indicator, independent of whether tooltip parsing is switched on.</summary>
    private bool IsCurrentlyMatching(TooltipKeywordRule rule)
    {
        var pattern = rule.GetPattern();
        if (pattern == null) return false;

        var statuses = _watcher.Snapshot.Statuses;
        for (int i = 0; i < statuses.Count; i++)
        {
            if (statuses[i].Description.Length > 0 && pattern.IsMatch(statuses[i].Description))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Paste-in tester: lets you check what a piece of tooltip text would trigger against your
    /// CURRENT rules above, without needing Moodles/Loci running or a matching status actually
    /// applied. Exists mainly because this whole feature is a heuristic over free text - seeing
    /// exactly which word or color resolved a match (and why - see <see cref="TooltipColorSource"/>)
    /// is a much faster way to tune the keyword list than trial-and-error in-game.
    /// </summary>
    private void DrawTester()
    {
        if (!ImGui.CollapsingHeader("Test tooltip text")) return;

        ImGui.PushID("Tester");
        try
        {
            ImGui.TextWrapped("Paste or type a sample tooltip below to see what your rules above would detect in it right now.");

            ImGui.InputTextMultiline("##testtext", ref _testText, 1024, new Vector2(-1, 60));

            var matches = TooltipKeywordParser.Parse(_testText, _config.TooltipKeywordRules);

            if (_testText.Trim().Length == 0)
            {
                ImGui.TextDisabled("  (type something above)");
            }
            else if (matches.Count == 0)
            {
                ImGui.TextDisabled("  No rule matches this text.");
            }
            else
            {
                foreach (var m in matches)
                {
                    if (m.Color is { } c)
                    {
                        ImGui.TextColored(new Vector4(c.X, c.Y, c.Z, 1f), "\u25a0"); // a filled square swatch in the match's resolved color
                        ImGui.SameLine();
                        ImGui.Text($"{m.Kind}  ({ColorSourceLabel(m.ColorSource)})");
                    }
                    else
                    {
                        ImGui.Text($"    {m.Kind}  (no color override)");
                    }
                }
            }

            ImGui.Spacing();
            ImGui.BeginDisabled(matches.Count == 0);
            if (ImGui.Button("Preview these on screen"))
            {
                foreach (var m in matches)
                    DebugTester.Force(m.Kind, true, m.Color);
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled($"(shows for {DebugTester.Seconds:0}s, same as the test panel further down)");
        }
        finally
        {
            ImGui.PopID();
        }
    }

    private static string ColorSourceLabel(TooltipColorSource source) => source switch
    {
        TooltipColorSource.Tag => "from a [color=] tag",
        TooltipColorSource.Clause => "from a nearby color word",
        TooltipColorSource.RuleDefault => "this rule's own fallback tint",
        _ => "no color",
    };
}