using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using static SkyrimCompass.Loc;

namespace SkyrimCompass;

public sealed class FirstTimeSetupWindow : Window
{
    private readonly Plugin plugin;

    private static readonly Vector4 AccentGreen = new(0.30f, 0.92f, 0.40f, 1.00f);
    private static readonly Vector4 AccentRed   = new(1.00f, 0.25f, 0.25f, 1.00f);
    private static readonly Vector4 AccentAmber = new(1.00f, 0.82f, 0.16f, 1.00f);
    private static readonly Vector4 DimText     = new(0.63f, 0.63f, 0.63f, 1.00f);
    private static readonly Vector4 NeutralGrey = new(0.55f, 0.55f, 0.55f, 1.00f);

    public FirstTimeSetupWindow(Plugin plugin)
        : base("Welcome to Skyrim Compass##skyrimcompasssetup",
               ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 200),
            MaximumSize = new Vector2(560, 900),
        };
    }

    public override void OnClose()
    {
        if (plugin.Config.HasCompletedFirstTimeSetup) return;
        plugin.Config.HasCompletedFirstTimeSetup = true;
        plugin.Config.Save(plugin.PluginInterface);
    }

    public override void PreDraw()
    {
        WindowName = L("Welcome to Skyrim Compass", "skyrimcompasssetup");
        var vp = ImGui.GetMainViewport();
        var center = new Vector2(vp.Pos.X + vp.Size.X * 0.5f, vp.Pos.Y + vp.Size.Y * 0.5f);
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    }

    public override void Draw()
    {
        var cfg = plugin.Config;

        ImGui.TextWrapped(T("Pick a starting look. You can change everything later with /compass config."));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool everything  = cfg.ShowCompassBar && cfg.ShowTargetBar && cfg.ShowTargetStatuses;
        bool statusOnly = !cfg.ShowCompassBar && !cfg.ShowTargetBar && cfg.ShowTargetStatuses;

        if (DrawModeCard("all", T("Give me everything"), everything,
                T("The compass strip, target health bars and status icons.")))
        {
            cfg.ShowCompassBar = true;
            cfg.ShowTargetBar = true;
            cfg.ShowTargetStatuses = true;
        }

        ImGui.Spacing();

        if (DrawModeCard("status", T("I'm only here for Moodles + Loci"), statusOnly,
                T("Hides the compass and health bars. Keeps the status icons, which are what show Moodles and Loci.")))
        {
            cfg.ShowCompassBar = false;
            cfg.ShowTargetBar = false;
            cfg.ShowTargetStatuses = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawMoodlesNotice();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(L("Done", "done"), new Vector2(ImGui.GetContentRegionAvail().X, 32f)))
        {
            cfg.HasCompletedFirstTimeSetup = true;
            cfg.Save(plugin.PluginInterface);
            IsOpen = false;
        }
    }

    private static bool DrawModeCard(string id, string title, bool selected, string description)
    {
        float width = ImGui.GetContentRegionAvail().X;
        var accent = selected ? AccentGreen : NeutralGrey;

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(accent.X, accent.Y, accent.Z, selected ? 0.30f : 0.10f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(accent.X, accent.Y, accent.Z, selected ? 0.42f : 0.22f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(accent.X, accent.Y, accent.Z, 0.50f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(accent.X, accent.Y, accent.Z, selected ? 0.95f : 0.35f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, selected ? 2.0f : 1.0f);

        bool clicked = ImGui.Button($"{title}##card{id}", new Vector2(width, 34f));

        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);

        ImGui.PushStyleColor(ImGuiCol.Text, selected ? AccentGreen : DimText);
        ImGui.TextWrapped(selected ? $"{T("Selected")}: {description}" : description);
        ImGui.PopStyleColor();

        return clicked;
    }

    private void DrawMoodlesNotice()
    {
        var mirror = plugin.StatusMirror;
        ImGui.TextColored(AccentAmber, T("Moodles and Loci"));

        if (mirror.MoodlesAvailable || mirror.LociAvailable)
        {
            ImGui.TextDisabled(F("Found: Moodles {0}, Loci {1}",
                mirror.MoodlesAvailable ? T("yes") : T("no"), mirror.LociAvailable ? T("yes") : T("no")));
            DrawChecklist();
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, DimText);
        ImGui.TextWrapped(T("Neither Moodles nor Loci was found, so you can skip this. If you install them later, type /compass setup to come back."));
        ImGui.PopStyleColor();
        if (ImGui.CollapsingHeader(L("Show the checklist anyway", "checklist"))) DrawChecklist();
    }

    private static void DrawChecklist()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, DimText);
        ImGui.TextWrapped(T("With the default settings every status pops up twice, once from Moodles and once from Loci. Follow these steps to fix it."));
        ImGui.PopStyleColor();
        ImGui.Spacing();

        ImGui.TextWrapped(T("Step 1. In the game's own status bar:"));
        ImGui.BulletText(T("Press Escape and open HUD Layout."));
        ImGui.BulletText(T("Click \"Status Effects\", then open its settings (the gear icon)."));
        DrawSettingLine("Display as Single Element", shouldBeOn: false);
        ImGui.Spacing();

        ImGui.TextWrapped(T("Step 2. In Moodles:"));
        ImGui.BulletText(T("Type /moodles and open the Settings tab."));
        DrawSettingLine("Allow other plugins apply Moodles", shouldBeOn: true);
        DrawSettingLine("Allow applying moodles from everyone", shouldBeOn: true);
        DrawSettingLine("Enable Moodle VFX", shouldBeOn: false);
        DrawSettingLine("Enable Fly/Popup Text", shouldBeOn: false);
        ImGui.Spacing();

        ImGui.TextWrapped(T("Only if you need it:"));
        ImGui.TextDisabled(T("Still seeing double effects? In Loci settings:"));
        DrawSettingLine("Loci VFX", shouldBeOn: false);
        ImGui.TextDisabled(T("Playing hard content? In both Moodles and Loci:"));
        DrawSettingLine("Disable in combat, Disable in duties/instances", shouldBeOn: true);
    }

    // Setting names are the other plugins' own English labels, so they stay untranslated.
    private static void DrawSettingLine(string settingName, bool shouldBeOn)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextColored(shouldBeOn ? AccentGreen : AccentRed, shouldBeOn ? T("Turn ON") : T("Turn OFF"));
        ImGui.SameLine();
        ImGui.TextWrapped($"\"{settingName}\"");
    }
}
