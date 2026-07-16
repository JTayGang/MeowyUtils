using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using StatusBridge.Bridge;
using StatusBridge.Experimental;

namespace StatusBridge.Windows;

internal sealed class ConfigWindow : Window, IDisposable
{
    private static readonly Vector4 Green = new(0.3f, 0.9f, 0.3f, 1f);
    private static readonly Vector4 Red = new(0.9f, 0.3f, 0.3f, 1f);
    private static readonly Vector4 Muted = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Vector4 Warning = new(0.95f, 0.65f, 0.15f, 1f);

    private readonly Configuration _config;
    private readonly BridgeEngine _engine;
    private readonly MoodlesOffsetDiagnostics _diagnostics = new();
    private string _lastDiagnosticResult = "";

    public ConfigWindow(Configuration config, BridgeEngine engine) : base("StatusBridge##ConfigWindow")
    {
        _config = config;
        _engine = engine;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(420, 320),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public override void Draw()
    {
        ImGui.TextWrapped("Mirrors your own live Moodles and Loci statuses onto each other, " +
                           "so whichever sync plugin you're using picks up a unified status " +
                           "regardless of which backend it natively reads.");
        ImGui.Separator();

        DrawStatusLine("Moodles", _engine.MoodlesAvailable, _engine.MirroredIntoMoodlesCount, "mirrored in from Loci");
        DrawStatusLine("Loci", _engine.LociAvailable, _engine.MirroredIntoLociCount, "mirrored in from Moodles");

        if (!_engine.MoodlesAvailable || !_engine.LociAvailable)
        {
            ImGui.Spacing();
            ImGui.TextColored(Muted, "Mirroring is paused until both plugins are detected.");
        }

        if (_engine.LockedMirrorCount > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(Warning, $"{_engine.LockedMirrorCount} mirrored status(es) can't be auto-removed - locked in Loci.");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Loci won't remove a status locked with a non-zero key unless the exact same\n" +
                    "key is provided, and this plugin's mirrors always use key=0 (\"don't lock\") so\n" +
                    "it can never supply the right one for a lock it didn't create. Unlock it in\n" +
                    "Loci's own UI, or try \"Force-clear stuck pairs\" below - that can still fail\n" +
                    "against a real lock too, but it'll get everything else.");
        }

        ImGui.Separator();

        var enabled = _config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            _config.Enabled = enabled;
            _config.Save();
        }

        ImGui.Indent();

        var moodlesToLoci = _config.MirrorMoodlesToLoci;
        if (ImGui.Checkbox("Mirror Moodles -> Loci", ref moodlesToLoci))
        {
            _config.MirrorMoodlesToLoci = moodlesToLoci;
            _config.Save();
        }

        var lociToMoodles = _config.MirrorLociToMoodles;
        if (ImGui.Checkbox("Mirror Loci -> Moodles", ref lociToMoodles))
        {
            _config.MirrorLociToMoodles = lociToMoodles;
            _config.Save();
        }

        var skipTitles = _config.SkipIfMatchingTitleExists;
        if (ImGui.Checkbox("Skip if a same-named status already exists on the destination", ref skipTitles))
        {
            _config.SkipIfMatchingTitleExists = skipTitles;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Extra de-duplication heuristic for statuses you created independently\non both sides. Off by default since it's a fuzzy title match.");

        var verbose = _config.VerboseLogging;
        if (ImGui.Checkbox("Verbose logging", ref verbose))
        {
            _config.VerboseLogging = verbose;
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Logs every mirror/remove action to the Dalamud log (/xllog).");

        ImGui.Unindent();

        ImGui.Separator();
        if (ImGui.Button("Remove all mirrored statuses"))
            _engine.ClearAllMirrors();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Removes every status this plugin has created on both sides.\nDoes not touch statuses you created yourself.");

        if (ImGui.Button("Force-clear stuck pairs (also removes native side)"))
            _engine.ForceClearBridgeLinkedStatuses();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "More aggressive than the button above: also removes the native original, not just\n" +
                "the mirror, for anything currently tracked as a pair. Use this one if the plain\n" +
                "button keeps 'not working' - clicking it, watching the status vanish, then seeing\n" +
                "it come right back a moment later means the native source is still alive, so the\n" +
                "plain button can only ever remove half of it (and correctly recreates that half,\n" +
                "since as far as it can tell, a native status just lost its mirror). This one\n" +
                "removes both halves, but that means it can delete content you created yourself\n" +
                "and still want, if it's currently part of a tracked pair - try the plain button first.");
        ImGui.TextColored(Red, "Can delete statuses you created yourself, not just mirrors - see tooltip.");

        ImGui.Separator();
        ImGui.TextColored(Muted, "Requires \"Allow applying moodles from everyone\" enabled");
        ImGui.TextColored(Muted, "in Moodles' own settings for the Loci -> Moodles direction");
        ImGui.TextColored(Muted, "to work (off by default in Moodles).");

        ImGui.Spacing();
        ImGui.Separator();
        DrawExperimentalSection();
    }

    private void DrawExperimentalSection()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Warning);
        var expanded = ImGui.CollapsingHeader("Experimental");
        ImGui.PopStyleColor();

        if (!expanded)
            return;

        ImGui.Indent();

        ImGui.TextColored(Warning, "Force-correct Moodles' icon offset (reflection patch)");
        ImGui.TextWrapped(
            "Moodles can miscount its own icon position when Loci's icons render into the " +
            "same UI space first, which can make a status hoverable (tooltip works) but " +
            "visually invisible on characters with both plugins' icons showing. This setting " +
            "makes StatusBridge reach into Moodles' running instance via reflection and flip " +
            "an internal flag Moodles already has for a StatusList-based offset calculation " +
            "that isn't affected by this. On by default: testers ran it opt-in for a while " +
            "with consistently good results, so it's now applied automatically - uncheck the " +
            "box below to opt out. It's still not a supported integration: it depends on " +
            "Moodles' internal, unversioned structure and will simply stop doing anything " +
            "(safely - it won't crash or corrupt anything) if a future Moodles update changes " +
            "that structure. It also still affects Moodles' icon placement globally, not just " +
            "the Loci-overlap case, for reasons we can't fully verify without Moodles' own " +
            "source history - if icon positioning looks off on party members, your focus " +
            "target, or your own status bar after updating, try disabling this first.");

        var offsetFix = _config.EnableExperimentalMoodlesOffsetFix;
        if (ImGui.Checkbox("Enable reflection-based offset patch (default: on)", ref offsetFix))
        {
            _config.EnableExperimentalMoodlesOffsetFix = offsetFix;
            _config.Save();
        }

        ImGui.TextUnformatted("Patch status:");
        ImGui.SameLine();
        ImGui.TextColored(_engine.MoodlesOffsetPatchApplied ? Green : Muted, _engine.MoodlesOffsetPatchStatus);

        ImGui.Spacing();
        ImGui.TextWrapped("To directly check whether the miscount is happening right now (rather " +
                           "than guessing from icon position), target a character with both " +
                           "Moodles and Loci icons showing and compare counts:");

        if (ImGui.Button("Compare counts for current target"))
        {
            var ok = _diagnostics.TryCompare(out var moodlesCount, out var realCount, out var resultStatus);
            _lastDiagnosticResult = ok
                ? $"Moodles sees: {moodlesCount}  |  Real vanilla count: {realCount}  ->  {resultStatus}"
                : resultStatus;
        }

        if (_lastDiagnosticResult.Length > 0)
            ImGui.TextWrapped(_lastDiagnosticResult);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(Warning, "Live-state diagnostics (read-only, your own character)");
        ImGui.TextWrapped(
            "Reads state neither plugin's public IPC can expose at all - see " +
            "MoodlesLiveStateReader.cs and LociLiveStateReader.cs for exactly what and why. " +
            "Never writes anything back to either plugin; this is purely informational.");

        ImGui.TextUnformatted("Moodles reports Ephemeral (your character):");
        ImGui.SameLine();
        if (_engine.MoodlesEphemeral is { } ephemeral)
            ImGui.TextColored(ephemeral ? Warning : Green,
                ephemeral ? "true - IPC add/remove is silently doing nothing right now" : "false");
        else
            ImGui.TextColored(Muted, "couldn't read this tick");

        var hosts = _engine.LociHostsForLocalPlayer;
        ImGui.TextUnformatted("Loci hosts registered on your character:");
        ImGui.SameLine();
        ImGui.TextColored(hosts.Count > 0 ? Warning : Green, hosts.Count > 0 ? string.Join(", ", hosts) : "none");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Other plugins (e.g. a sync tool) currently telling Loci they're actively driving\n" +
                "this character's data. Informational only - confirmed from source that this does\n" +
                "NOT actually block the calls this bridge makes (only an explicit per-status lock\n" +
                "does), so a host being listed here isn't itself a sign of a problem.");

        ImGui.Unindent();
    }

    private static void DrawStatusLine(string name, bool available, int mirroredCount, string mirroredLabel)
    {
        ImGui.TextUnformatted($"{name}:");
        ImGui.SameLine();
        ImGui.TextColored(available ? Green : Red, available ? "detected" : "not detected");

        if (available)
        {
            ImGui.SameLine();
            ImGui.TextColored(Muted, $"({mirroredCount} {mirroredLabel})");
        }
    }

    public void Dispose() { }
}
