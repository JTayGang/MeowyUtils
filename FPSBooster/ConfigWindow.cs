using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace FPSBooster;

public sealed class ConfigWindow : Window
{
    private const float SaveDebounceSeconds = 1f;
    private static readonly Vector4 Green = new(.45f, .85f, .45f, 1f);
    private static readonly Vector4 Muted = new(.65f, .65f, .65f, 1f);
    private readonly Plugin _plugin;
    private DateTime _lastSave = DateTime.MinValue;
    private bool _dirty;

    public ConfigWindow(Plugin plugin) : base("FPSBooster##fpsboosterconfig", ImGuiWindowFlags.AlwaysAutoResize)
    {
        _plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(380, 180), MaximumSize = new Vector2(620, 820) };
    }

    public override void OnClose()
    {
        // The test is momentary. Closing the window must always release it.
        _plugin.Culling.ForceHideAll = false;
        _plugin.Culling.ForceHideAllExceptSpotlights = false;
        Flush();
    }

    public override void Draw()
    {
        var cfg = _plugin.Config;
        var svc = _plugin.Culling;
        bool changed = false;
        bool hideAllTestActive = false;
        bool spotlightAwareTestActive = false;

        if (ImGui.BeginTabBar("FPSBoosterTabs"))
        {
            if (ImGui.BeginTabItem("Housing"))
            {
                changed |= Toggle("Enable furniture culling", ref cfg.Enabled,
                    "Hides furniture outside the camera view. Turn this off to show everything again.");
                ImGui.SameLine();
                bool running = svc.Active;
                string stateText = svc.UsingNativeOutdoorCulling
                    ? (svc.NativeOutdoorExtraLimitsActive ? "Game culling + extra limits"
                        : svc.NativeOutdoorExtraLimitsWaitingForCamera ? "Game culling (limits waiting)" : "Game culling")
                    : running ? "Working" : GetSimpleStatus(cfg.Enabled, svc.Status);
                ImGui.TextColored(running ? Green : Muted, stateText);

                var stats = svc.Stats;
                ImGui.Separator();
                ImGui.TextDisabled("Furniture in this area");
                ImGui.SameLine(190f);
                ImGui.Text(stats.FurnitureItems.ToString());

                ImGui.TextDisabled("Hidden right now");
                ImGui.SameLine(190f);
                ImGui.Text(stats.Hidden.ToString());

                if (svc.UsingNativeOutdoorCulling)
                    ImGui.TextDisabled(svc.NativeOutdoorExtraLimitsActive
                        ? "The game handles outdoor visibility; FPSBooster only applies your extra distance/height limits."
                        : svc.NativeOutdoorExtraLimitsWaitingForCamera
                            ? "The game handles outdoor visibility; FPSBooster's extra limits are waiting for camera data."
                            : "The game's native background culling handles outdoor visibility.");

                ImGui.Separator();
                changed |= Toggle("Hide furniture outside the screen", ref cfg.CullOutsideView,
                    "Hides furniture outside the camera view. Outdoors, the game's own culling is used when available.");
                changed |= Slider("Extra room around the screen", ref cfg.FovMarginDeg, 0f, 45f, "%.0f°",
                    "Keeps furniture just past the screen edge visible to avoid pop-in. Unused when the game handles outdoor culling.");
                changed |= DistanceSlider("Always show nearby furniture", ref cfg.AlwaysShowRadius, 0f, 30f,
                    "Furniture within this distance of the camera stays visible. Off disables it.");
                changed |= DistanceSlider("Maximum distance", ref cfg.MaxDistance, 0f, 200f,
                    "Hides furniture farther than this from the camera, even if on screen or spotlight-protected. Off disables it.");
                changed |= DistanceSlider("Maximum height difference", ref cfg.MaxHeightDelta, 0f, 30f,
                    "Hides furniture this far above or below the camera, even if spotlight-protected. Useful for other floors. Off disables it.");
                changed |= Toggle("Show all furniture in GPose", ref cfg.PauseInGpose,
                    "Temporarily stops hiding furniture while you are in GPose.");
                changed |= Toggle("Pause if my character is off screen", ref cfg.RequirePlayerInView,
                    "A safety check. Turn this off when using a free camera.");

                ImGui.Separator();
                ImGui.Button("Hold to hide ALL furniture", new Vector2(-1f, 0f));
                hideAllTestActive = ImGui.IsItemActive();
                Tip("Hides every loaded furnishing, spotlights included. Release to restore.");

                ImGui.Button("Hold to hide ALL (keep spotlight protection)", new Vector2(-1f, 0f));
                spotlightAwareTestActive = ImGui.IsItemActive();
                Tip("Hides furniture as if all were off screen, but keeps spotlight protection. Release to restore.");

                if (hideAllTestActive || spotlightAwareTestActive)
                {
                    string testStatus = hideAllTestActive
                        ? "Test active — hiding ALL furniture"
                        : "Test active — keeping spotlight protection";
                    ImGui.TextColored(new Vector4(1f, .65f, .35f, 1f), testStatus);
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Players"))
            {
                var diagnostics = _plugin.Characters;
                diagnostics.RefreshIfNeeded();
                ImGui.TextDisabled("Read-only diagnostics — FPSBooster is not changing player visibility.");
                ImGui.Separator();
                DrawActorStats("Players", diagnostics.Snapshot.Players);
                ImGui.Separator();
                ImGui.TextDisabled("Other player objects");
                DrawActorStats("Mounts", diagnostics.Snapshot.Mounts);
                DrawActorStats("Minions / companions", diagnostics.Snapshot.Companions);
                DrawActorStats("Ornaments", diagnostics.Snapshot.Ornaments);
                ImGui.Separator();
                DrawDistanceDistribution("Other players by distance", diagnostics.Snapshot.PlayerDistances,
                    "3D distance from your character; you are excluded.");
                ImGui.Separator();
                DrawGameDrawBudget(diagnostics.Snapshot);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("NPCs"))
            {
                var diagnostics = _plugin.Characters;
                diagnostics.RefreshIfNeeded();
                ImGui.TextDisabled("Read-only diagnostics — FPSBooster is not changing NPC visibility.");
                ImGui.Separator();
                DrawActorStats("Battle NPCs", diagnostics.Snapshot.BattleNpcs);
                ImGui.Separator();
                DrawActorStats("Event NPCs", diagnostics.Snapshot.EventNpcs);
                ImGui.Separator();
                DrawActorStats("Retainers", diagnostics.Snapshot.Retainers);
                ImGui.Separator();
                DrawDistanceDistribution("NPCs by distance", diagnostics.Snapshot.NpcDistances,
                    "3D distance from your character. Battle NPCs, Event NPCs and retainers.");
                ImGui.Separator();
                DrawGameDrawBudget(diagnostics.Snapshot);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        // Tests stay active only while their button is visible and held; switching tabs or closing ends them.
        svc.ForceHideAll = hideAllTestActive;
        svc.ForceHideAllExceptSpotlights = spotlightAwareTestActive;

        if (changed) _dirty = true;
        if (_dirty && ((DateTime.UtcNow - _lastSave).TotalSeconds >= SaveDebounceSeconds || !IsOpen)) Flush();
    }

    private static void DrawActorStats(string label, ActorKindStats stats)
    {
        ImGui.TextDisabled(label);
        ImGui.Columns(2, $"{label}Stats", false);
        ImGui.SetColumnWidth(0, 220f);
        ImGui.Text("Objects in game"); ImGui.NextColumn(); ImGui.Text(stats.Objects.ToString()); ImGui.NextColumn();
        ImGui.Text("Have a DrawObject"); ImGui.NextColumn(); ImGui.Text($"{stats.DrawObjects} / {stats.Objects}"); ImGui.NextColumn();
        ImGui.Text("Have a character skeleton"); ImGui.NextColumn(); ImGui.Text($"{stats.Skeletons} / {stats.CharacterBases}"); ImGui.NextColumn();
        ImGui.Text("Populated model slots"); ImGui.NextColumn(); ImGui.Text($"{stats.PopulatedModelSlots} / {stats.ModelSlots}"); ImGui.NextColumn();
        ImGui.Text("Flags: neither 0x01 nor 0x08"); ImGui.NextColumn(); ImGui.Text($"{stats.DrawFlagsNeitherBitSet} / {stats.DrawObjects}"); ImGui.NextColumn();
        ImGui.Text("Flags: only 0x01 set"); ImGui.NextColumn(); ImGui.Text($"{stats.DrawFlagBit0Only} / {stats.DrawObjects}"); ImGui.NextColumn();
        ImGui.Text("Flags: only 0x08 set"); ImGui.NextColumn(); ImGui.Text($"{stats.DrawFlagBit3Only} / {stats.DrawObjects}"); ImGui.NextColumn();
        ImGui.Text("Flags: both set (0x09)"); ImGui.NextColumn(); ImGui.Text($"{stats.DrawFlagBits0And3Set} / {stats.DrawObjects}"); ImGui.NextColumn();
        ImGui.Text("GameObject.Visibility = 1"); ImGui.NextColumn(); ImGui.Text(stats.GameVisibilityHidden.ToString()); ImGui.NextColumn();
        if (stats.GameVisibilityOther > 0)
        {
            ImGui.Text("Other visibility byte values"); ImGui.NextColumn(); ImGui.Text(stats.GameVisibilityOther.ToString()); ImGui.NextColumn();
        }
        ImGui.Text("No DrawObject"); ImGui.NextColumn(); ImGui.Text(stats.WithoutDrawObject.ToString()); ImGui.NextColumn();
        ImGui.Columns(1);
    }

    private static void DrawDistanceDistribution(string label, DistanceDistribution stats, string description)
    {
        ImGui.TextDisabled(label);
        ImGui.Columns(4, $"{label}Table", false);
        ImGui.SetColumnWidth(0, 115f);
        ImGui.SetColumnWidth(1, 65f);
        ImGui.SetColumnWidth(2, 105f);
        ImGui.Text("Distance"); ImGui.NextColumn();
        ImGui.Text("Objects"); ImGui.NextColumn();
        ImGui.Text("With DrawObject"); ImGui.NextColumn();
        ImGui.Text("Without"); ImGui.NextColumn();
        DrawDistanceBand("0–10 yalms", stats.Under10);
        DrawDistanceBand("10–20 yalms", stats.From10To20);
        DrawDistanceBand("20–30 yalms", stats.From20To30);
        DrawDistanceBand("30–50 yalms", stats.From30To50);
        DrawDistanceBand("50+ yalms", stats.Over50);
        ImGui.Columns(1);
        if (stats.DistanceUnavailable > 0)
            ImGui.TextDisabled($"World-position distance unavailable: {stats.DistanceUnavailable}");
        ImGui.TextWrapped(description);
        ImGui.TextWrapped("Correlation only; this does not explain why a DrawObject exists or not.");
        DrawClientDistanceValues(stats);
    }


    private static void DrawClientDistanceValues(DistanceDistribution stats)
    {
        ImGui.TextDisabled("Game CurrentDistance value (raw)");
        ImGui.Columns(3, "ClientDistanceValues", false);
        ImGui.SetColumnWidth(0, 125f);
        ImGui.SetColumnWidth(1, 65f);
        ImGui.Text("Object state"); ImGui.NextColumn();
        ImGui.Text("Count"); ImGui.NextColumn();
        ImGui.Text("Min / avg / max"); ImGui.NextColumn();
        DrawClientDistanceRow("Has DrawObject", stats.CurrentDistanceWithDrawObject);
        DrawClientDistanceRow("No DrawObject", stats.CurrentDistanceWithoutDrawObject);
        ImGui.Columns(1);
        ImGui.TextWrapped("Raw byte from the game; units are undocumented, so these are not yalms.");
    }

    private static void DrawClientDistanceRow(string label, ClientDistanceValueStats stats)
    {
        ImGui.Text(label); ImGui.NextColumn();
        ImGui.Text(stats.Count.ToString()); ImGui.NextColumn();
        ImGui.Text(stats.Count > 0 ? $"{stats.Minimum} / {stats.Average:F1} / {stats.Maximum}" : "—"); ImGui.NextColumn();
    }

    private static void DrawDistanceBand(string label, DistanceBandStats stats)
    {
        ImGui.Text(label); ImGui.NextColumn();
        ImGui.Text(stats.Objects.ToString()); ImGui.NextColumn();
        ImGui.Text(stats.WithDrawObject.ToString()); ImGui.NextColumn();
        ImGui.Text(stats.WithoutDrawObject.ToString()); ImGui.NextColumn();
    }

    private static void DrawGameDrawBudget(CharacterDiagnosticsSnapshot snapshot)
    {
        ImGui.TextDisabled("Game rendering limits");
        if (!snapshot.HasObjectManager)
        {
            ImGui.TextDisabled("The game's object manager is not available yet.");
        }
        else
        {
            ImGui.Columns(2, "GameDrawBudget", false);
            ImGui.SetColumnWidth(0, 190f);
            ImGui.Text("Game draw limit"); ImGui.NextColumn();
            ImGui.Text(snapshot.HasDrawLimit ? snapshot.DrawLimit.ToString() : "Unavailable"); ImGui.NextColumn();
            ImGui.Columns(1);
        }

        ImGui.TextWrapped("The draw limit is the game's allowance, not a count of hidden objects. Visibility 1 = hidden. Draw flags 0x01/0x08 are shown raw because their meaning is uncertain.");
    }

    private static string GetSimpleStatus(bool enabled, string status)
    {
        if (!enabled || status == "Off") return "Off";
        if (status.Contains("housing area", StringComparison.OrdinalIgnoreCase)) return "Visit a housing area";
        if (status.Contains("GPose", StringComparison.OrdinalIgnoreCase)) return "Paused in GPose";
        if (status.Contains("cutscene", StringComparison.OrdinalIgnoreCase)) return "Paused in cutscene";
        if (status.Contains("player not in camera view", StringComparison.OrdinalIgnoreCase)) return "Paused: character off screen";
        if (status.Contains("Loading", StringComparison.OrdinalIgnoreCase)) return "Loading";
        if (status.Contains("Camera unavailable", StringComparison.OrdinalIgnoreCase)) return "Waiting for camera";
        if (status == "Starting") return "Starting";
        return "Paused";
    }

    private void Flush()
    {
        if (!_dirty) return;
        _plugin.Config.Sanitize();
        _plugin.Config.Save(_plugin.PluginInterface);
        _lastSave = DateTime.UtcNow;
        _dirty = false;
    }

    private static void Tip(string? tip)
    {
        if (tip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    private static bool Toggle(string label, ref bool value, string? tip = null)
    {
        bool changed = ImGui.Checkbox(label, ref value);
        Tip(tip);
        return changed;
    }

    private static bool DistanceSlider(string label, ref float value, float min, float max, string? tip = null)
    {
        // Show a clear Off label at zero instead of displaying "0 yalms".
        string format = value < 0.5f ? "Off" : "%.0f yalms";
        return Slider(label, ref value, min, max, format, tip);
    }

    private static bool Slider(string label, ref float value, float min, float max, string format, string? tip = null)
    {
        ImGui.SetNextItemWidth(210f);
        bool changed = ImGui.SliderFloat(label, ref value, min, max, format);
        Tip(tip);
        return changed;
    }
}
