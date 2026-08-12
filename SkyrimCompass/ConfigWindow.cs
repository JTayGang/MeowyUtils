using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace SkyrimCompass;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private PlayerIconOverride _newOverride = new();
    private int _selectedThemeIndex = 0;

    // Debounce for config saves
    private DateTime _lastConfigSave = DateTime.MinValue;
    private const float ConfigSaveDebounceSeconds = 1.0f;

    // Tracks whether a change is waiting to be written to disk.
    private bool _configDirty = false;

    public ConfigWindow(Plugin plugin)
        : base("Skyrim Compass Settings##skyrimcompasscfg",
               ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(370, 200),
            MaximumSize = new Vector2(620, 800),
        };
    }

    public override void Draw()
    {
        var cfg = plugin.Config;
        bool changed = false;

        changed |= DrawToggle("##enabled", () => cfg.Enabled, v => cfg.Enabled = v);
        ImGui.SameLine();
        ImGui.Text("Enable Compass");
        ImGui.Separator();

        if (ImGui.BeginTabBar("##tabs"))
        {
            changed |= DrawAppearanceTab(cfg);
            changed |= DrawMarkersTab(cfg);
            changed |= DrawCombatTab(cfg);
            changed |= DrawAdvancedTab(cfg);   // <-- returns bool (includes override changes)
            ImGui.EndTabBar();
        }

        ImGui.Separator();
        if (ImGui.Button("Close", new Vector2(80, 0)))
            IsOpen = false;

        if (changed)
            _configDirty = true;

        var debounceElapsed = (DateTime.UtcNow - _lastConfigSave).TotalSeconds >= ConfigSaveDebounceSeconds;
        if (_configDirty && (debounceElapsed || !IsOpen))
        {
            cfg.Save(plugin.PluginInterface);
            _lastConfigSave = DateTime.UtcNow;
            _configDirty = false;
        }
    }

    // ───────────────────────────────────────────────────────────────────────────
    // TAB 1: APPEARANCE – Compass strip, behavior, colors, camera
    // ───────────────────────────────────────────────────────────────────────────
    private bool DrawAppearanceTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Appearance")) return false;
        bool changed = false;

        // ── Compass Strip ─────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Compass Strip", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.SetNextItemWidth(120f);
            changed |= DrawSliderInt("Width##w", 200, 1400, () => (int)cfg.CompassWidth, v => cfg.CompassWidth = v);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f);
            changed |= DrawSliderInt("Height##h", 20, 80, () => (int)cfg.CompassHeight, v => cfg.CompassHeight = v);

            var io = ImGui.GetIO();
            int yMax = (int)MathF.Max(0f, io.DisplaySize.Y - cfg.CompassHeight);
            changed |= DrawSliderInt("Y Offset (from top)##yo", 0, yMax, () => (int)cfg.YOffset, v => cfg.YOffset = v);

            int xRange = (int)MathF.Max(0f, (io.DisplaySize.X - cfg.CompassWidth) * 0.5f);
            changed |= DrawSliderInt("X Offset (from center)##xo", -xRange, xRange, () => (int)cfg.XOffset, v => cfg.XOffset = v,
                "Shifts compass left/right; range auto-adjusts to screen width.");

            ImGui.Spacing();
            changed |= DrawSliderInt("Visible Degrees##vd", 30, 180, () => (int)cfg.VisibleDegrees, v => cfg.VisibleDegrees = v);
            changed |= DrawSliderFloat("Lens Strength##ls", 1.0f, 3.0f, () => cfg.LensStrength, v => cfg.LensStrength = v);
            changed |= DrawSliderFloat("Font Scale##fs", 0.5f, 2.5f, () => cfg.FontScale, v => cfg.FontScale = v);

            ImGui.Spacing();
            changed |= DrawToggle("Show Compass Bar", () => cfg.ShowCompassBar, v => cfg.ShowCompassBar = v,
                "Hide the main compass strip while keeping target bars and status icons.");
            changed |= DrawToggle("Show numeric heading", () => cfg.ShowHeadingText, v => cfg.ShowHeadingText = v);
            changed |= DrawToggle("Hide during cutscenes", () => cfg.HideDuringCutscenes, v => cfg.HideDuringCutscenes = v,
                "Skips drawing in story/skippable cinematics and group pose.");
        }

        // ── Colors & Theme ──────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Colors & Theme", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= DrawColorEdit("Background##bgc", cfg.BackgroundColor, v => cfg.BackgroundColor = v, ColorPickerFlags);
            changed |= DrawColorEdit("Border##bdc", cfg.BorderColor, v => cfg.BorderColor = v, ColorPickerFlags);
            changed |= DrawColorEdit("Cardinal (N/S/E/W)##cdc", cfg.CardinalColor, v => cfg.CardinalColor = v, ColorPickerFlags);
            changed |= DrawColorEdit("Intercardinal (NE/SW…)##icc", cfg.IntercardinalColor, v => cfg.IntercardinalColor = v, ColorPickerFlags);
            changed |= DrawColorEdit("Tick marks##tkc", cfg.TickColor, v => cfg.TickColor = v, ColorPickerFlags);

            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("Theme preset##colortheme", ref _selectedThemeIndex, ColorThemeNames, ColorThemeNames.Length))
            {
                ApplyColorTheme(cfg, ColorThemes[_selectedThemeIndex]);
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Overwrites all colors; pick \"Original\" to restore defaults.");
        }

        // ── Camera ──────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Camera & Direction", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= DrawToggle("Use camera direction (not character facing)",
                () => cfg.UseCameraDirection, v => cfg.UseCameraDirection = v,
                "Follows camera orientation instead of character facing.");
            BeginIndentedDisabled(cfg.UseCameraDirection);
            changed |= DrawToggle("Also use camera location for distances##ucp",
                () => cfg.UseCameraPosition, v => cfg.UseCameraPosition = v,
                "Measures distances from camera position; requires 'Use camera direction'.");
            EndIndentedDisabled();

            ImGui.Spacing();
            ImGui.TextDisabled("Rotation Offset (set 180 if N/S swapped)");
            changed |= DrawSliderInt("##rotoff", -180, 180, () => (int)cfg.RotationOffset, v => cfg.RotationOffset = v);
        }

        ImGui.EndTabItem();
        return changed;
    }

    // ───────────────────────────────────────────────────────────────────────────
    // TAB 2: MARKERS – Global range, fade, and all marker type toggles
    // ───────────────────────────────────────────────────────────────────────────
    private bool DrawMarkersTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Markers")) return false;
        bool changed = false;

        // ── Global settings (range & fade) ──────────────────────────────
        if (ImGui.CollapsingHeader("Global Marker Settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= DrawSliderInt("Detection range (yalms)##maxd", 10, 200, () => (int)cfg.MaxMarkerDistance, v => cfg.MaxMarkerDistance = v,
                "Maximum distance for all markers (FATEs use a multiplier).");
            ImGui.Spacing();
            ImGui.TextDisabled("Dot distance‑fade curve");
            changed |= DrawSliderFloat("Full opacity zone##nz", 0.5f, 1.0f, () => cfg.DotNearZone, v => cfg.DotNearZone = v,
                tooltip: "Dots fully opaque closer than this fraction of max range; 1.0 = always opaque.");
            changed |= DrawSliderFloat("Fade‑to‑zero zone##fz", 0.0f, 0.5f, () => cfg.DotFarZone, v => cfg.DotFarZone = v,
                tooltip: "Dots fade to invisible below this fraction of max range; 0.0 = no fade‑to‑zero.");
            changed |= DrawSliderFloat("Mid‑range opacity##ma", 0.0f, 1.0f, () => cfg.DotMidAlpha, v => cfg.DotMidAlpha = v,
                tooltip: "Opacity of dots in the middle distance band.");
        }

        // ── Players ──────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Players", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= DrawEnableAndColor("players", "Players", () => cfg.ShowPlayers, v => cfg.ShowPlayers = v,
                () => cfg.PlayerColor, v => cfg.PlayerColor = v);

            BeginIndentedDisabled(cfg.ShowPlayers);
            changed |= DrawSizeSliders(
                () => cfg.PartyRoleIconMinSize, v => cfg.PartyRoleIconMinSize = v,
                () => cfg.PartyRoleIconMaxSize, v => cfg.PartyRoleIconMaxSize = v, 50, 60, "pr");

            ImGui.Spacing();
            changed |= DrawToggle("Solid dot for friends##sfr", () => cfg.SolidFriendDots, v => cfg.SolidFriendDots = v,
                "Friends show as solid dot instead of hollow ring.");
            changed |= DrawToggle("Show job icon for party members##pri", () => cfg.ShowPartyRoleIcons, v => cfg.ShowPartyRoleIcons = v,
                "Party members show class/job icon on role‑colored dot.");
            BeginIndentedDisabled(cfg.ShowPartyRoleIcons);
            changed |= DrawToggle("Only in duty / PvP##pridonly", () => cfg.PartyRoleIconsOnlyInDuty, v => cfg.PartyRoleIconsOnlyInDuty = v,
                "Limit job icons to duties/PvP; off = always show.");
            EndIndentedDisabled();
            EndIndentedDisabled();
        }

        // ── Enemies ──────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Enemies"))
        {
            changed |= DrawEnableAndColor("enemies", "Enemies", () => cfg.ShowEnemies, v => cfg.ShowEnemies = v,
                () => cfg.EnemyColor, v => cfg.EnemyColor = v);

            BeginIndentedDisabled(cfg.ShowEnemies);
            changed |= DrawToggle("Only show engaged enemies##eng", () => cfg.EnemiesOnlyIfEngaged, v => cfg.EnemiesOnlyIfEngaged = v,
                "Only hostiles in combat with you/your party.");
            changed |= DrawSizeSliders(
                () => cfg.EnemyMinSize, v => cfg.EnemyMinSize = v, () => cfg.EnemyMaxSize, v => cfg.EnemyMaxSize = v,
                50, 60, "en");
            EndIndentedDisabled();
        }

        // ── NPCs ─────────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("NPCs"))
        {
            changed |= DrawEnableAndColor("npcs", "NPCs", () => cfg.ShowNpcs, v => cfg.ShowNpcs = v,
                () => cfg.NpcColor, v => cfg.NpcColor = v);

            BeginIndentedDisabled(cfg.ShowNpcs);
            changed |= DrawToggle("Hide non‑targetable \"ghost\" NPCs##tgt", () => cfg.NpcsOnlyIfTargetable, v => cfg.NpcsOnlyIfTargetable = v,
                "Filters placeholder/empty slot NPCs.");
            changed |= DrawToggle("Show quest marker icons##qicon", () => cfg.ShowNpcQuestIcons, v => cfg.ShowNpcQuestIcons = v,
                "Shows quest '!' / '?' icons on NPCs.");
            changed |= DrawToggle("Show Mender icon##micon", () => cfg.ShowMenderIcons, v => cfg.ShowMenderIcons = v,
                "Gear repair vendors.");
            changed |= DrawToggle("Show Shop/Trader icon##sicon", () => cfg.ShowShopIcons, v => cfg.ShowShopIcons = v,
                "Merchant/Vendor NPCs.");
            changed |= DrawToggle("Show Fast Travel icons##fticon", () => cfg.ShowFastTravelIcons, v => cfg.ShowFastTravelIcons = v,
                "Ferry, airship, Chocobo Keep, etc.");
            changed |= DrawSizeSliders(
                () => cfg.NpcQuestIconMinSize, v => cfg.NpcQuestIconMinSize = v,
                () => cfg.NpcQuestIconMaxSize, v => cfg.NpcQuestIconMaxSize = v, 50, 60, "q");
            EndIndentedDisabled();
        }

        // ── Gathering Nodes ─────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Gathering Nodes"))
        {
            changed |= DrawEnableAndColor("gath", "Gathering Nodes", () => cfg.ShowGatheringNodes, v => cfg.ShowGatheringNodes = v,
                () => cfg.GatheringColor, v => cfg.GatheringColor = v);

            BeginIndentedDisabled(cfg.ShowGatheringNodes);
            changed |= DrawToggle("Hide non‑targetable \"ghost\" nodes##gtgt", () => cfg.GatheringOnlyIfTargetable, v => cfg.GatheringOnlyIfTargetable = v,
                "Filters depleted/not‑yet‑spawned nodes.");
            changed |= DrawToggle("Show Mining/Botany icons##gicon", () => cfg.ShowGatheringIcons, v => cfg.ShowGatheringIcons = v,
                "Shows node type icon.");
            BeginIndentedDisabled(cfg.ShowGatheringIcons);
            changed |= DrawSizeSliders(
                () => cfg.GatheringIconMinSize, v => cfg.GatheringIconMinSize = v,
                () => cfg.GatheringIconMaxSize, v => cfg.GatheringIconMaxSize = v, 50, 60, "g");
            EndIndentedDisabled();
            EndIndentedDisabled();
        }

        // ── Treasure ────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Treasure Coffers"))
        {
            changed |= DrawEnableAndColor("tres", "Treasure", () => cfg.ShowTreasure, v => cfg.ShowTreasure = v,
                () => cfg.TreasureColor, v => cfg.TreasureColor = v);

            BeginIndentedDisabled(cfg.ShowTreasure);
            changed |= DrawSizeSliders(
                () => cfg.TreasureMinSize, v => cfg.TreasureMinSize = v,
                () => cfg.TreasureMaxSize, v => cfg.TreasureMaxSize = v, 50, 60, "tr");
            ImGui.Spacing();

            changed |= DrawToggle("Show chest icon##tricon", () => cfg.ShowTreasureIcons, v => cfg.ShowTreasureIcons = v,
                "Uses a single icon (below) for all treasure coffers.");
            BeginIndentedDisabled(cfg.ShowTreasureIcons);
            int trIconId = cfg.TreasureIconId;
            ImGui.SetNextItemWidth(90f);
            if (ImGui.InputInt("Icon ID##triconid", ref trIconId, 0, 0))
            { cfg.TreasureIconId = Math.Max(0, trIconId); changed = true; }
            EndIndentedDisabled();
            EndIndentedDisabled();
        }

        // ── Aetherytes ──────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Aetherytes"))
        {
            changed |= DrawEnableAndColor("aeth", "Aetherytes", () => cfg.ShowAetherytes, v => cfg.ShowAetherytes = v,
                () => cfg.AetheryteColor, v => cfg.AetheryteColor = v);

            BeginIndentedDisabled(cfg.ShowAetherytes);
            changed |= DrawToggle("Show Aethernet shards##aethshards", () => cfg.ShowAethernetShards, v => cfg.ShowAethernetShards = v,
                "Smaller waypoints in housing wards, Firmament, etc.");
            changed |= DrawToggle("Show aetheryte icon##aicon", () => cfg.ShowAetheryteIcons, v => cfg.ShowAetheryteIcons = v,
                "Falls back to dot if icon not resolved.");
            changed |= DrawSizeSliders(
                () => cfg.AetheryteIconMinSize, v => cfg.AetheryteIconMinSize = v,
                () => cfg.AetheryteIconMaxSize, v => cfg.AetheryteIconMaxSize = v, 50, 60, "a");

            string shardName = cfg.AethernetShardName;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.InputText("Aethernet shard name##shardname", ref shardName, 64))
            { cfg.AethernetShardName = shardName; changed = true; }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Substring in shard names to identify them (e.g. \"Aethernet\").");
            EndIndentedDisabled();
        }

        // ── FATEs ───────────────────────────────────────────────────────
        if (ImGui.CollapsingHeader("FATEs"))
        {
            changed |= DrawEnableAndColor("fates", "Show FATEs", () => cfg.ShowFates, v => cfg.ShowFates = v,
                () => cfg.FateColor, v => cfg.FateColor = v,
                "Shows active/about‑to‑start FATEs; range = General range × multiplier.");

            BeginIndentedDisabled(cfg.ShowFates);
            changed |= DrawSliderFloat("Distance multiplier##fatemul", 0.5f, 5.0f,
                () => cfg.FateDistanceMultiplier, v => cfg.FateDistanceMultiplier = MathF.Max(0.5f, v), "%.1f×");
            ImGui.TextDisabled($"Effective FATE range: {cfg.MaxMarkerDistance * cfg.FateDistanceMultiplier:F0} yalms");
            changed |= DrawSizeSliders(
                () => cfg.FateIconMinSize, v => cfg.FateIconMinSize = v,
                () => cfg.FateIconMaxSize, v => cfg.FateIconMaxSize = v, 50, 64, "fate");
            EndIndentedDisabled();
        }

        ImGui.EndTabItem();
        return changed;
    }

    // ───────────────────────────────────────────────────────────────────────────
    // TAB 3: TARGETING & COMBAT
    // ───────────────────────────────────────────────────────────────────────────
    private bool DrawCombatTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Targeting & Combat")) return false;
        bool changed = false;

        // ── Target Health Bar ──────────────────────────────────────────
        if (ImGui.CollapsingHeader("Target Health Bar", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= DrawToggle("Target health bar", () => cfg.ShowTargetBar, v => cfg.ShowTargetBar = v,
                "Name+HP readout docked beneath compass.");
            BeginIndentedDisabled(cfg.ShowTargetBar);
            changed |= DrawSliderFloat("Width (fraction of compass)##tbwf", 0.3f, 1.0f,
                () => cfg.TargetBarWidthFraction, v => cfg.TargetBarWidthFraction = v);
            changed |= DrawSliderInt("Bar thickness##tbh", 6, 30, () => (int)cfg.TargetBarHeight, v => cfg.TargetBarHeight = v);
            changed |= DrawSliderFloat("Name font scale##tbfs", 0.5f, 2.5f,
                () => cfg.TargetBarFontScale, v => cfg.TargetBarFontScale = v);
            changed |= DrawToggle("Show target level##tblvl", () => cfg.ShowTargetLevel, v => cfg.ShowTargetLevel = v);
            changed |= DrawToggle("Show HP percentage##tbhpp", () => cfg.ShowTargetHealthPercent, v => cfg.ShowTargetHealthPercent = v,
                "Shows percentage under the health bar.");
            BeginIndentedDisabled(cfg.ShowTargetHealthPercent);
            changed |= DrawToggle("Show on target‑of‑target##tbtothpp", () => cfg.ShowTargetOfTargetHealthPercent, v => cfg.ShowTargetOfTargetHealthPercent = v,
                "Also shows percentage on the target‑of‑target bar.");
            EndIndentedDisabled();
            changed |= DrawEnableAndColor("tbshd", "Show shield overlay",
                () => cfg.ShowTargetBarShield, v => cfg.ShowTargetBarShield = v,
                () => cfg.TargetBarShieldColor, v => cfg.TargetBarShieldColor = v,
                "Light sheen over shielded portion of the bar.");
            changed |= DrawToggle("Show name ribbons##tbrib", () => cfg.ShowTargetBarRibbons, v => cfg.ShowTargetBarRibbons = v,
                "Glowing ribbons from name ornaments.");
            EndIndentedDisabled();
        }

        // ── Target Status Icons ────────────────────────────────────────
        if (ImGui.CollapsingHeader("Target Status Icons", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= DrawToggle("Target status icons", () => cfg.ShowTargetStatuses, v => cfg.ShowTargetStatuses = v,
                "Buff/debuff icons below target name.");
            BeginIndentedDisabled(cfg.ShowTargetStatuses);
            changed |= DrawSliderInt("Icon size##tssize", 12, 40, () => (int)cfg.TargetStatusIconSize, v => cfg.TargetStatusIconSize = v);
            changed |= DrawSliderInt("Max icons##tsmax", 3, 20, () => cfg.TargetStatusMaxIcons, v => cfg.TargetStatusMaxIcons = v);
            EndIndentedDisabled();
        }

        // ── Target‑of‑Target ──────────────────────────────────────────
        if (ImGui.CollapsingHeader("Target-of-Target"))
        {
            changed |= DrawToggle("Target-of-target bar", () => cfg.ShowTargetOfTargetBar, v => cfg.ShowTargetOfTargetBar = v,
                "Shows who/what your target has targeted.");
            BeginIndentedDisabled(cfg.ShowTargetOfTargetBar);
            changed |= DrawToggle("Highlight if targeting me##aggro", () => cfg.HighlightIfTargetingMe, v => cfg.HighlightIfTargetingMe = v,
                "Warns when your target targets you.");
            ImGui.BeginDisabled(!cfg.HighlightIfTargetingMe);
            changed |= DrawColorEdit("Warning color##aggroc", cfg.AggroWarningColor, v => cfg.AggroWarningColor = v, ColorPickerFlags);
            ImGui.EndDisabled();

            changed |= DrawToggle("Show name##totname", () => cfg.ShowTargetOfTargetName, v => cfg.ShowTargetOfTargetName = v,
                "Shows the target‑of‑target's name centered over their bar.");
            BeginIndentedDisabled(cfg.ShowTargetOfTargetName);
            changed |= DrawToggle("Only show first name##totfirstname", () => cfg.TargetOfTargetFirstNameOnly, v => cfg.TargetOfTargetFirstNameOnly = v,
                "Trims multi‑word names down to just the first word.");
            changed |= DrawToggle("Show \"YOU\" for yourself##totyou", () => cfg.TargetOfTargetShowYou, v => cfg.TargetOfTargetShowYou = v,
                "Displays \"YOU\" instead of your character name when you are the target‑of‑target.");
            EndIndentedDisabled();
            EndIndentedDisabled();
        }

        // ── Limit Break Glow ────────────────────────────────────────────
        if (ImGui.CollapsingHeader("Limit Break Glow"))
        {
            changed |= DrawEnableAndColor("lbglow", "Limit break glow (bar 1 color)",
                () => cfg.ShowLimitBreakGlow, v => cfg.ShowLimitBreakGlow = v,
                () => cfg.LimitBreakGlowColor, v => cfg.LimitBreakGlowColor = v,
                "Glowing border as LB charges – one layer per bar.");
            BeginIndentedDisabled(cfg.ShowLimitBreakGlow);
            changed |= DrawColorEdit("Bar 2 color##lbc2", cfg.LimitBreakGlowColor2, v => cfg.LimitBreakGlowColor2 = v, ColorPickerFlags);
            changed |= DrawColorEdit("Bar 3 color##lbc3", cfg.LimitBreakGlowColor3, v => cfg.LimitBreakGlowColor3 = v, ColorPickerFlags);
            EndIndentedDisabled();
        }

        ImGui.EndTabItem();
        return changed;
    }

    // ───────────────────────────────────────────────────────────────────────────
    // TAB 4: ADVANCED – Player overrides, Status mirror
    // ───────────────────────────────────────────────────────────────────────────
    private bool DrawAdvancedTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Advanced")) return false;
        bool changed = false;
        bool overridesChanged = false;

        // ── Player Icon Overrides ─────────────────────────────────────
        if (ImGui.CollapsingHeader("Player Icon Overrides", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextDisabled("Override icons for specific players by name.");
            if (cfg.PlayerIconOverrides.Count == 0)
                ImGui.TextDisabled("  (none)");

            int removeAt = -1;
            for (int i = 0; i < cfg.PlayerIconOverrides.Count; i++)
            {
                ImGui.PushID(i);
                if (ImGui.Button("X##rmov")) removeAt = i;
                ImGui.SameLine();
                if (DrawOverrideRow(cfg.PlayerIconOverrides[i], "ov", 110f))
                    overridesChanged = true;
                ImGui.PopID();
            }
            if (removeAt >= 0)
            {
                cfg.PlayerIconOverrides.RemoveAt(removeAt);
                overridesChanged = true;
            }

            ImGui.Separator();
            ImGui.TextDisabled("Add new override:");
            ImGui.SameLine();
            // Editing the new override does NOT affect the list yet, so we don't set overridesChanged
            changed |= DrawOverrideRow(_newOverride, "newov", 120f);
            ImGui.SameLine();

            bool canAdd = !string.IsNullOrWhiteSpace(_newOverride.PlayerName) && _newOverride.IconBaseId > 0;
            ImGui.BeginDisabled(!canAdd);
            if (ImGui.Button("Add##addov"))
            {
                _newOverride.PlayerName = _newOverride.PlayerName.Trim();
                cfg.PlayerIconOverrides.Add(_newOverride);
                overridesChanged = true;
                // Reset the builder object for next addition
                _newOverride = new PlayerIconOverride
                {
                    ShowBorder = _newOverride.ShowBorder,
                    BorderColor = _newOverride.BorderColor,
                    ShowFill = _newOverride.ShowFill,
                    FillColor = _newOverride.FillColor,
                    ClipToCircle = _newOverride.ClipToCircle,
                    SizeMultiplier = _newOverride.SizeMultiplier,
                };
                changed = true;
            }
            ImGui.EndDisabled();

            // If any override-specific change occurred, increment the version
            if (overridesChanged)
                cfg.IncrementOverrideVersion();
        }

        // ── Moodles ↔ Loci Mirror ────────────────────────────────────
        if (ImGui.CollapsingHeader("Moodles ↔ Loci Mirror", ImGuiTreeNodeFlags.DefaultOpen))
        {
            changed |= DrawToggle("Mirror Moodles \u2194 Loci", () => cfg.MirrorMoodlesLoci, v => cfg.MirrorMoodlesLoci = v,
                "Keep your own Moodles and Loci statuses mirrored onto each other.");

            BeginIndentedDisabled(cfg.MirrorMoodlesLoci);
            var mirror = plugin.StatusMirror;
            ImGui.TextDisabled($"Moodles: {(mirror.MoodlesAvailable ? "connected" : "not found")}   " +
                               $"Loci: {(mirror.LociAvailable ? "connected" : "not found")}");
            ImGui.TextDisabled($"Mirrored into Loci: {mirror.MirroredIntoLociCount}   " +
                               $"Mirrored into Moodles: {mirror.MirroredIntoMoodlesCount}" +
                               (mirror.LockedMirrorCount > 0 ? $"   Locked: {mirror.LockedMirrorCount}" : ""));
            if (ImGui.Button("Clear stuck mirrors##mirclear"))
                mirror.ClearAllMirrors();
            EndIndentedDisabled();
        }

        ImGui.EndTabItem();
        return changed || overridesChanged;
    }

    // ───────────────────────────────────────────────────────────────────────────
    // Shared UI helpers
    // ───────────────────────────────────────────────────────────────────────────

    // ── Color themes ──────────────────────────────────────────────
    private sealed record ColorTheme(
        string Name, Vector4 Background, Vector4 Border, Vector4 Cardinal, Vector4 Intercardinal, Vector4 Tick,
        Vector4 Player, Vector4 Enemy, Vector4 Npc, Vector4 Gathering, Vector4 Treasure, Vector4 Aetheryte, Vector4 Fate);

    private static readonly ColorTheme[] ColorThemes =
    {
        new("Original", new(0.05f,0.04f,0.03f,0.82f), new(0.48f,0.42f,0.27f,0.92f), new(1.00f,0.97f,0.88f,1.00f),
            new(0.72f,0.70f,0.65f,0.88f), new(0.58f,0.56f,0.52f,0.72f), new(0.40f,0.65f,1.00f,0.92f), new(1.00f,0.25f,0.25f,0.92f),
            new(0.95f,0.88f,0.35f,0.92f), new(0.30f,0.92f,0.40f,0.92f), new(1.00f,0.80f,0.15f,0.95f), new(0.55f,0.85f,0.95f,0.92f), new(0.82f,0.35f,0.95f,0.95f)),
        new("Frostfall", new(0.03f,0.06f,0.10f,0.84f), new(0.55f,0.75f,0.88f,0.92f), new(0.92f,0.97f,1.00f,1.00f),
            new(0.68f,0.82f,0.90f,0.88f), new(0.55f,0.68f,0.78f,0.72f), new(0.50f,0.85f,1.00f,0.92f), new(1.00f,0.35f,0.40f,0.92f),
            new(0.85f,0.95f,1.00f,0.92f), new(0.40f,0.95f,0.85f,0.92f), new(0.95f,0.92f,0.65f,0.95f), new(0.60f,0.90f,1.00f,0.92f), new(0.75f,0.55f,1.00f,0.95f)),
        new("Inferno", new(0.08f,0.03f,0.02f,0.85f), new(0.75f,0.32f,0.10f,0.92f), new(1.00f,0.88f,0.60f,1.00f),
            new(0.88f,0.58f,0.32f,0.88f), new(0.65f,0.38f,0.22f,0.72f), new(0.45f,0.75f,1.00f,0.92f), new(1.00f,0.18f,0.10f,0.95f),
            new(1.00f,0.78f,0.30f,0.92f), new(0.55f,0.90f,0.35f,0.92f), new(1.00f,0.70f,0.10f,0.95f), new(0.95f,0.55f,0.85f,0.92f), new(1.00f,0.40f,0.85f,0.95f)),
        new("Verdant", new(0.03f,0.06f,0.03f,0.84f), new(0.38f,0.58f,0.32f,0.92f), new(0.92f,1.00f,0.85f,1.00f),
            new(0.68f,0.82f,0.60f,0.88f), new(0.50f,0.62f,0.45f,0.72f), new(0.45f,0.80f,1.00f,0.92f), new(1.00f,0.30f,0.25f,0.92f),
            new(0.92f,0.85f,0.40f,0.92f), new(0.45f,1.00f,0.50f,0.95f), new(1.00f,0.85f,0.25f,0.95f), new(0.55f,0.92f,0.80f,0.92f), new(0.78f,0.95f,0.35f,0.95f)),
        new("Void", new(0.04f,0.02f,0.08f,0.85f), new(0.58f,0.38f,0.78f,0.92f), new(0.92f,0.85f,1.00f,1.00f),
            new(0.72f,0.62f,0.85f,0.88f), new(0.55f,0.48f,0.65f,0.72f), new(0.55f,0.72f,1.00f,0.92f), new(1.00f,0.30f,0.55f,0.92f),
            new(0.88f,0.78f,1.00f,0.92f), new(0.55f,0.95f,0.65f,0.92f), new(1.00f,0.75f,0.95f,0.95f), new(0.68f,0.55f,1.00f,0.92f), new(0.85f,0.40f,1.00f,0.95f)),
    };

    private static readonly string[] ColorThemeNames = Array.ConvertAll(ColorThemes, t => t.Name);

    private static void ApplyColorTheme(Configuration cfg, ColorTheme t)
    {
        cfg.BackgroundColor = t.Background;
        cfg.BorderColor = t.Border;
        cfg.CardinalColor = t.Cardinal;
        cfg.IntercardinalColor = t.Intercardinal;
        cfg.TickColor = t.Tick;
        cfg.PlayerColor = t.Player;
        cfg.EnemyColor = t.Enemy;
        cfg.NpcColor = t.Npc;
        cfg.GatheringColor = t.Gathering;
        cfg.TreasureColor = t.Treasure;
        cfg.AetheryteColor = t.Aetheryte;
        cfg.FateColor = t.Fate;
    }

    // ── Override row ────────────────────────────────────────────────
    private static bool DrawOverrideRow(PlayerIconOverride ov, string idSuffix, float nameWidth)
    {
        bool changed = false;
        string name = ov.PlayerName;
        ImGui.SetNextItemWidth(nameWidth);
        if (ImGui.InputText($"##{idSuffix}name", ref name, 64)) { ov.PlayerName = name; changed = true; }
        ImGui.SameLine();
        int iconId = ov.IconBaseId;
        ImGui.SetNextItemWidth(68f);
        if (ImGui.InputInt($"##{idSuffix}id", ref iconId, 0, 0)) { ov.IconBaseId = Math.Max(0, iconId); changed = true; }
        ImGui.SameLine();

        changed |= DrawToggle($"B##{idSuffix}b", () => ov.ShowBorder, v => ov.ShowBorder = v);
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowBorder);
        changed |= DrawColorEdit($"##{idSuffix}bc", ov.BorderColor, v => ov.BorderColor = v, ColorPickerFlags);
        ImGui.EndDisabled();
        ImGui.SameLine();

        changed |= DrawToggle($"F##{idSuffix}f", () => ov.ShowFill, v => ov.ShowFill = v);
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowFill);
        changed |= DrawColorEdit($"##{idSuffix}fc", ov.FillColor, v => ov.FillColor = v, ColorPickerFlags);
        ImGui.EndDisabled();
        ImGui.SameLine();

        changed |= DrawToggle($"○##{idSuffix}circ", () => ov.ClipToCircle, v => ov.ClipToCircle = v);
        ImGui.SameLine();

        float mul = ov.SizeMultiplier;
        ImGui.SetNextItemWidth(58f);
        if (ImGui.DragFloat($"##{idSuffix}mul", ref mul, 0.05f, 0.5f, 3.0f, "%.2fx"))
        { ov.SizeMultiplier = Math.Clamp(mul, 0.5f, 3.0f); changed = true; }

        return changed;
    }

    // ── General helpers ──────────────────────────────────────────────────

    private static void BeginIndentedDisabled(bool enabled)
    {
        ImGui.Indent();
        ImGui.BeginDisabled(!enabled);
    }
    private static void EndIndentedDisabled()
    {
        ImGui.EndDisabled();
        ImGui.Unindent();
    }

    private static bool DrawToggle(string label, Func<bool> get, Action<bool> set, string? tooltip = null)
    {
        bool v = get();
        bool changed = ImGui.Checkbox(label, ref v);
        if (changed) set(v);
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return changed;
    }

    private static bool DrawEnableAndColor(
        string idPrefix, string label, Func<bool> getEnabled, Action<bool> setEnabled,
        Func<Vector4> getColor, Action<Vector4> setColor, string? tooltip = null)
    {
        bool enabled = getEnabled();
        bool changed = ImGui.Checkbox($"##{idPrefix}_en", ref enabled);
        if (changed) setEnabled(enabled);
        ImGui.SameLine();
        Vector4 color = getColor();
        if (ImGui.ColorEdit4($"{label}##{idPrefix}_c", ref color, ColorPickerFlags)) { setColor(color); changed = true; }
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return changed;
    }

    private static bool DrawSizeSliders(
        Func<float> getMin, Action<float> setMin, Func<float> getMax, Action<float> setMax,
        int minHi, int maxHi, string idPrefix,
        string minLabel = "Min size (far away)", string maxLabel = "Max size (close up)",
        int lo = 8, string? tooltip = null)
    {
        bool changed = false;
        int mn = (int)getMin();
        if (ImGui.SliderInt($"{minLabel}##{idPrefix}min", ref mn, lo, minHi)) { setMin(mn); changed = true; }
        int mx = (int)getMax();
        if (ImGui.SliderInt($"{maxLabel}##{idPrefix}max", ref mx, lo, maxHi)) { setMax(mx); changed = true; }
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return changed;
    }

    private static bool DrawSliderInt(string label, int lo, int hi, Func<int> get, Action<int> set, string? tooltip = null)
    {
        int v = get();
        if (ImGui.SliderInt(label, ref v, lo, hi)) { set(v); return true; }
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return false;
    }

    private static bool DrawSliderFloat(string label, float lo, float hi, Func<float> get, Action<float> set,
                                        string? fmt = null, string? tooltip = null)
    {
        float v = get();
        bool changed = fmt is null ? ImGui.SliderFloat(label, ref v, lo, hi) : ImGui.SliderFloat(label, ref v, lo, hi, fmt);
        if (changed) set(v);
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return changed;
    }

    private static bool DrawColorEdit(string label, Vector4 val, Action<Vector4> set, ImGuiColorEditFlags flags = ImGuiColorEditFlags.None)
    {
        if (ImGui.ColorEdit4(label, ref val, flags)) { set(val); return true; }
        return false;
    }

    private static readonly ImGuiColorEditFlags ColorPickerFlags =
        ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar;
}