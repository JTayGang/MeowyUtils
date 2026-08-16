using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

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
        if (!plugin.Config.HasCompletedFirstTimeSetup)
        {
            plugin.Config.HasCompletedFirstTimeSetup = true;
            plugin.Config.Save(plugin.PluginInterface);
        }
    }

    public override void PreDraw()
    {
        var vp = ImGui.GetMainViewport();
        var center = new Vector2(vp.Pos.X + vp.Size.X * 0.5f, vp.Pos.Y + vp.Size.Y * 0.5f);
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    }

    public override void Draw()
    {
        var cfg = plugin.Config;

        ImGui.TextWrapped(
            "Quick one-time setup. Pick how you want to use Skyrim Compass - you can change " +
            "this (or anything else) later from /compass config.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool everything  = cfg.ShowCompassBar && cfg.ShowTargetBar;
        bool moodlesOnly = !cfg.ShowCompassBar && !cfg.ShowTargetBar;

        if (DrawModeCard(
                "Give me everything", everything,
                "Full compass strip, target health bar, and Moodles/Loci status icons. This is the current default."))
        {
            cfg.ShowCompassBar = true;
            cfg.ShowTargetBar = true;
        }

        ImGui.Spacing();

        if (DrawModeCard(
                "I'm just here for Moodles + Loci", moodlesOnly,
                "Turns off the compass strip and target health bar. Only the Moodles/Loci status icons stay on screen."))
        {
            cfg.ShowCompassBar = false;
            cfg.ShowTargetBar = false;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawMoodlesNotice();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        float footerWidth = ImGui.GetContentRegionAvail().X;
        if (ImGui.Button("Get Started", new Vector2(footerWidth, 32f)))
        {
            cfg.HasCompletedFirstTimeSetup = true;
            cfg.Save(plugin.PluginInterface);
            IsOpen = false;
        }
    }

    private static bool DrawModeCard(string title, bool selected, string description)
    {
        float width = ImGui.GetContentRegionAvail().X;
        var accent = selected ? AccentGreen : NeutralGrey;

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(accent.X, accent.Y, accent.Z, selected ? 0.30f : 0.10f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(accent.X, accent.Y, accent.Z, selected ? 0.42f : 0.22f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(accent.X, accent.Y, accent.Z, 0.50f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(accent.X, accent.Y, accent.Z, selected ? 0.95f : 0.35f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, selected ? 2.0f : 1.0f);

        bool clicked = ImGui.Button(title, new Vector2(width, 34f));

        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);

        if (selected)
        {
            ImGui.SameLine();
            ImGui.TextColored(AccentGreen, "<- current");
        }

        ImGui.PushStyleColor(ImGuiCol.Text, DimText);
        ImGui.TextWrapped(description);
        ImGui.PopStyleColor();

        return clicked;
    }

    private void DrawMoodlesNotice()
    {
        var mirror = plugin.StatusMirror;

        ImGui.TextColored(AccentAmber, "IMPORTANT - one-time Moodles setting");

        ImGui.PushStyleColor(ImGuiCol.Text, DimText);
        ImGui.TextWrapped(
            "Skyrim Compass mirrors Moodles and Loci statuses to draw its own status icons. " +
            "If Moodles is left on its default settings, every status will flash and pop up " +
            "TWICE - once from Moodles, once from here.");
        ImGui.PopStyleColor();

        ImGui.Spacing();
        ImGui.TextDisabled(
            $"Detected right now  -  Moodles: {(mirror.MoodlesAvailable ? "yes" : "not running")}" +
            $"    Loci: {(mirror.LociAvailable ? "yes" : "not running")}");
        ImGui.Spacing();

        ImGui.TextWrapped("Fix it once, in Moodles itself:");
        ImGui.BulletText("Run /moodles to open its settings window.");
        ImGui.BulletText("Open the Settings tab.");

        DrawSettingLine("Allow other plugins apply Moodles", shouldBeOn: true);
        DrawSettingLine("Enable Moodle VFX", shouldBeOn: false);
        DrawSettingLine("Enable Fly/Popup Text", shouldBeOn: false);
    }

    private static void DrawSettingLine(string settingName, bool shouldBeOn)
    {
        ImGui.Bullet();
        ImGui.SameLine();
        ImGui.TextColored(shouldBeOn ? AccentGreen : AccentRed, shouldBeOn ? "Turn ON " : "Turn OFF");
        ImGui.SameLine();
        ImGui.TextWrapped($"\"{settingName}\"");
    }
}