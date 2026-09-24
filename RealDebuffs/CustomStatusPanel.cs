using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs;

/// <summary>
/// The "Custom statuses" section of the settings window: the list of name -> effect rules, plus a
/// row for adding one. Kept in its own file so Configuration.cs only has to call <see cref="Draw"/>.
///
/// To add a rule you can either type the status's name, or pick it from a dropdown of the Moodles /
/// Loci statuses you have on right now (a status mirrored between the two shows up once). Picking
/// just fills in the name box, so you can still choose the effect before hitting Add.
/// </summary>
internal sealed class CustomStatusPanel
{
    private const float NameWidth = 160f;
    private const float KindWidth = 110f;

    // Every effect kind, in enum order - new kinds show up in the dropdowns on their own.
    private static readonly DebuffKind[] Kinds = Enum.GetValues<DebuffKind>();
    private static readonly string[] KindNames = Array.ConvertAll(Kinds, k => k.ToString());
    private static readonly Vector4 ActiveColor = new(0.4f, 1f, 0.4f, 1f);

    private readonly Configuration _config;
    private readonly CustomStatusWatcher _watcher;

    // State of the "add" row; only lives as long as the window does.
    private string _newName = "";
    private int _newKind = Array.IndexOf(Kinds, DebuffKind.Bind);
    private int _pick = -1;

    // The picker's dropdown labels, rebuilt only when the watcher publishes a new snapshot.
    private CustomStatusSnapshot? _labelsFor;
    private string[] _labels = Array.Empty<string>();

    public CustomStatusPanel(Configuration config, CustomStatusWatcher watcher)
    {
        _config = config;
        _watcher = watcher;
    }

    /// <summary>Draws the section. Returns true if any rule changed, so the caller knows to save.</summary>
    public bool Draw()
    {
        bool changed = false;
        var snapshot = _watcher.Snapshot;
        var rules = _config.CustomStatusRules;

        // Own ID scope: rows reuse the same short labels, and ImGui treats same-label widgets in one window as one widget.
        ImGui.PushID("CustomStatuses");
        try
        {
            ImGui.TextDisabled("Custom statuses (Moodles / Loci)");
            ImGui.TextWrapped(
                "Show an effect for as long as a specific Moodles or Loci status is on you, matched by its " +
                "name. Case doesn't matter, and Moodles' color/glow/italic tags are ignored. A status " +
                "mirrored between Moodles and Loci counts once, and if the same effect is also coming " +
                "from a real debuff it just stays on - it never doubles up.");
            ImGui.TextDisabled(
                $"Moodles: {(_watcher.MoodlesAvailable ? "connected" : "not found")}   " +
                $"Loci: {(_watcher.LociAvailable ? "connected" : "not found")}");
            ImGui.Spacing();

            // ---- existing rules ----
            if (rules.Count == 0)
                ImGui.TextDisabled("  (no custom status rules yet)");

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

                // Live feedback: this rule's status is on you right now.
                if (snapshot.Contains(rule.GetKey()))
                {
                    ImGui.SameLine();
                    ImGui.TextColored(ActiveColor, "active");
                }

                ImGui.PopID();
            }
            if (removeAt >= 0) { rules.RemoveAt(removeAt); changed = true; }

            // ---- add a rule ----
            ImGui.Spacing();
            ImGui.TextDisabled("Add a status");

            if (snapshot.Statuses.Count == 0)
            {
                ImGui.TextDisabled("  No Moodles/Loci statuses are on you right now. Apply one to pick it here, or just type its name.");
            }
            else
            {
                RebuildLabels(snapshot);
                if (_pick >= _labels.Length) _pick = -1;

                ImGui.SetNextItemWidth(NameWidth + KindWidth);
                if (ImGui.Combo("Pick from what's on you##pick", ref _pick, _labels, _labels.Length)
                    && _pick >= 0 && _pick < snapshot.Statuses.Count)
                {
                    _newName = snapshot.Statuses[_pick].Name;
                    _pick = -1; // it's an action, not a setting - go back to the empty preview
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
            bool duplicate = false;
            foreach (var r in rules)
            {
                if (r.Kind == newKind && r.GetKey() == key) { duplicate = true; break; }
            }

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
                ImGui.TextDisabled("  That status already has that effect.");

            ImGui.TextDisabled("Not triggering? '/realdebuffs statuses' logs exactly what Moodles/Loci report and which rules match.");
        }
        finally
        {
            ImGui.PopID();
        }

        return changed;
    }

    private void RebuildLabels(CustomStatusSnapshot snapshot)
    {
        if (ReferenceEquals(_labelsFor, snapshot)) return;

        var statuses = snapshot.Statuses;
        var labels = new string[statuses.Count];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = $"{statuses[i].Name}   ({SourceLabel(statuses[i].Sources)})";

        _labels = labels;
        _labelsFor = snapshot;
    }

    private static string SourceLabel(StatusSource sources) => sources switch
    {
        StatusSource.Moodles => "Moodles",
        StatusSource.Loci => "Loci",
        _ => "Moodles + Loci",
    };
}
