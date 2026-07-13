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
            "that isn't affected by this. It is not a supported integration: it depends on " +
            "Moodles' internal, unversioned structure and will simply stop doing anything " +
            "(safely - it won't crash or corrupt anything) if a future Moodles update changes " +
            "that structure. It's off by default because it affects Moodles' icon placement " +
            "globally, not just the Loci-overlap case, for reasons we can't fully verify " +
            "without Moodles' own source history.");

        var offsetFix = _config.EnableExperimentalMoodlesOffsetFix;
        if (ImGui.Checkbox("Enable reflection-based offset patch", ref offsetFix))
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
