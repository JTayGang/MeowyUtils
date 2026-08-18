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
    private DateTime _lastConfigSave = DateTime.MinValue;
    private const float ConfigSaveDebounceSeconds = 1.0f;
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
        bool ch = false;

        ch |= DrawToggle("Compass##topcompass", () => cfg.ShowCompassBar, v => cfg.ShowCompassBar = v,
            "The compass strip itself. Full options in the Compass tab.");
        ImGui.SameLine();
        ch |= DrawToggle("HP bars##tophp", () => cfg.ShowTargetBar, v => cfg.ShowTargetBar = v,
            "Name+HP readout beneath the compass. Full options in HP Bars & Statuses tab.");
        ImGui.SameLine();
        ch |= DrawToggle("Statuses##topstatus", () => cfg.ShowTargetStatuses, v => cfg.ShowTargetStatuses = v,
            "Target's buff/debuff icons. Full options in HP Bars & Statuses tab.");
        ImGui.Separator();

        if (ImGui.BeginTabBar("##tabs"))
        {
            ch |= DrawAppearanceTab(cfg);
            ch |= DrawCompassTab(cfg);
            ch |= DrawCombatTab(cfg);
            ch |= DrawAdvancedTab(cfg);
            ImGui.EndTabBar();
        }

        ImGui.Separator();
        if (ImGui.Button("Close", new Vector2(80, 0)))
            IsOpen = false;
        ImGui.SameLine();
        if (ImGui.Button("Setup Wizard"))
            plugin.OpenFirstTimeSetup();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-open the \"Give me everything\" / \"Moodles + Loci only\" quick setup.");

        if (ch)
            _configDirty = true;

        if (_configDirty && ((DateTime.UtcNow - _lastConfigSave).TotalSeconds >= ConfigSaveDebounceSeconds || !IsOpen))
        {
            cfg.Save(plugin.PluginInterface);
            _lastConfigSave = DateTime.UtcNow;
            _configDirty = false;
        }
    }

    private bool DrawAppearanceTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Appearance")) return false;
        bool ch = false;

        if (ImGui.CollapsingHeader("Size+Position", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.SetNextItemWidth(120f);
            ch |= DrawSliderInt("Width##w", 200, 1400, () => (int)cfg.CompassWidth, v => cfg.CompassWidth = v);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120f);
            ch |= DrawSliderInt("Height##h", 20, 80, () => (int)cfg.CompassHeight, v => cfg.CompassHeight = v);

            ch |= DrawToggle("Lock position##lockpos", () => cfg.LockPosition, v => cfg.LockPosition = v,
                "When unlocked, click and drag the compass, target bars, or target's status icons to move the whole HUD group.");
            ImGui.SameLine();
            if (ImGui.Button("Center##centerpos"))
            {
                cfg.XOffset = 0f;
                ch = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Recenters the compass horizontally.");

            ImGui.Spacing();
            ch |= DrawSliderFloat("Font Scale##fs", 0.5f, 2.5f, () => cfg.FontScale, v => cfg.FontScale = v);

            ImGui.Spacing();
            ch |= DrawToggle("Hide during cutscenes and Gpose", () => cfg.HideDuringCutscenes, v => cfg.HideDuringCutscenes = v);
        }

        if (ImGui.CollapsingHeader("Colors & Theme", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("Theme preset##colortheme", ref _selectedThemeIndex, ColorThemeNames, ColorThemeNames.Length))
            {
                ApplyColorTheme(cfg, ColorThemes[_selectedThemeIndex]);
                ch = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Overwrites all colors; pick \"Original\" to restore defaults.");

            ch |= DrawColorEdit("Compass Background, Status tooltips##bgc", cfg.BackgroundColor, v => cfg.BackgroundColor = v, ColorPickerFlags);
            ch |= DrawColorEdit("Border##bdc", cfg.BorderColor, v => cfg.BorderColor = v, ColorPickerFlags);
            ch |= DrawColorEdit("Cardinal (N/S/E/W)##cdc", cfg.CardinalColor, v => cfg.CardinalColor = v, ColorPickerFlags);
            ch |= DrawColorEdit("Intercardinal (NE/SW…)##icc", cfg.IntercardinalColor, v => cfg.IntercardinalColor = v, ColorPickerFlags);
            ch |= DrawColorEdit("Tick marks##tkc", cfg.TickColor, v => cfg.TickColor = v, ColorPickerFlags);
        }

        ImGui.EndTabItem();
        return ch;
    }

    private bool DrawCompassTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Compass")) return false;
        bool ch = false;

        BeginIndentedDisabled(cfg.ShowCompassBar);

        if (ImGui.CollapsingHeader("Compass Settings", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ch |= DrawSliderInt("Detection range (yalms)##maxd", 10, 200, () => (int)cfg.MaxMarkerDistance, v => cfg.MaxMarkerDistance = v,
                "Maximum distance for all markers (FATEs use a multiplier).");
            ch |= DrawSliderInt("Visible Degrees##vd", 30, 180, () => (int)cfg.VisibleDegrees, v => cfg.VisibleDegrees = v);
            ch |= DrawSliderFloat("Lens Strength##ls", 1.0f, 3.0f, () => cfg.LensStrength, v => cfg.LensStrength = v);
            ImGui.Spacing();
            ImGui.TextDisabled("Marker distance fade");
            ch |= DrawSliderFloat("Full opacity zone##nz", 0.5f, 1.0f, () => cfg.DotNearZone, v => cfg.DotNearZone = v,
                tooltip: "Dots fully opaque closer than this fraction of max range; 1.0 = always opaque.");
            ch |= DrawSliderFloat("Fade to zero zone##fz", 0.0f, 0.5f, () => cfg.DotFarZone, v => cfg.DotFarZone = v,
                tooltip: "Dots fade to invisible below this fraction of max range; 0.0 = no fade‑to‑zero.");
            ch |= DrawSliderFloat("Midrange opacity##ma", 0.0f, 1.0f, () => cfg.DotMidAlpha, v => cfg.DotMidAlpha = v,
                tooltip: "Opacity of dots in the middle distance band.");
        }

        if (ImGui.CollapsingHeader("Camera & Direction", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ch |= DrawToggle("Use camera direction (not character facing)",
                () => cfg.UseCameraDirection, v => cfg.UseCameraDirection = v);
            BeginIndentedDisabled(cfg.UseCameraDirection);
            ch |= DrawToggle("Also measure distances from camera location##ucp",
                () => cfg.UseCameraPosition, v => cfg.UseCameraPosition = v);
            EndIndentedDisabled();

            ImGui.Spacing();
            ImGui.TextDisabled("Rotation Offset (set 180 if N/S swapped)");
            ch |= DrawSliderInt("##rotoff", -180, 180, () => (int)cfg.RotationOffset, v => cfg.RotationOffset = v);
        }

        if (ImGui.CollapsingHeader("Players", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ch |= DrawEnableAndColor("players", "Players", () => cfg.ShowPlayers, v => cfg.ShowPlayers = v,
                () => cfg.PlayerColor, v => cfg.PlayerColor = v);

            BeginIndentedDisabled(cfg.ShowPlayers);
            ch |= DrawSizeSliders(
                () => cfg.PartyRoleIconMinSize, v => cfg.PartyRoleIconMinSize = v,
                () => cfg.PartyRoleIconMaxSize, v => cfg.PartyRoleIconMaxSize = v, 50, 60, "pr");

            ImGui.Spacing();
            ch |= DrawToggle("Solid dot for friends##sfr", () => cfg.SolidFriendDots, v => cfg.SolidFriendDots = v);
            ch |= DrawToggle("Show job icon for party members##pri", () => cfg.ShowPartyRoleIcons, v => cfg.ShowPartyRoleIcons = v);
            BeginIndentedDisabled(cfg.ShowPartyRoleIcons);
            ch |= DrawToggle("Only in duty / PvP##pridonly", () => cfg.PartyRoleIconsOnlyInDuty, v => cfg.PartyRoleIconsOnlyInDuty = v);
            EndIndentedDisabled();
            EndIndentedDisabled();
        }

        if (ImGui.CollapsingHeader("Enemies"))
        {
            ch |= DrawEnableAndColor("enemies", "Enemies", () => cfg.ShowEnemies, v => cfg.ShowEnemies = v,
                () => cfg.EnemyColor, v => cfg.EnemyColor = v);

            BeginIndentedDisabled(cfg.ShowEnemies);
            ch |= DrawToggle("Only show aggro'ed enemies##eng", () => cfg.EnemiesOnlyIfEngaged, v => cfg.EnemiesOnlyIfEngaged = v);
            ch |= DrawSizeSliders(
                () => cfg.EnemyMinSize, v => cfg.EnemyMinSize = v, () => cfg.EnemyMaxSize, v => cfg.EnemyMaxSize = v,
                50, 60, "en");
            EndIndentedDisabled();
        }

        if (ImGui.CollapsingHeader("NPCs"))
        {
            ch |= DrawEnableAndColor("npcs", "NPCs", () => cfg.ShowNpcs, v => cfg.ShowNpcs = v,
                () => cfg.NpcColor, v => cfg.NpcColor = v);

            BeginIndentedDisabled(cfg.ShowNpcs);
            ch |= DrawToggle("Hide untargetable \"ghost\" NPCs##tgt", () => cfg.NpcsOnlyIfTargetable, v => cfg.NpcsOnlyIfTargetable = v,
                "Filters placeholder/empty slot NPCs.");
            ch |= DrawToggle("Show quest marker icons##qicon", () => cfg.ShowNpcQuestIcons, v => cfg.ShowNpcQuestIcons = v);
            ch |= DrawToggle("Show Shops/Menders##sicon", () => cfg.ShowShopIcons, v => cfg.ShowShopIcons = v);
            ch |= DrawToggle("Show Fast Travel icons##fticon", () => cfg.ShowFastTravelIcons, v => cfg.ShowFastTravelIcons = v,
                "Ferry, airship, Chocobo Keep, etc.");
            ch |= DrawSizeSliders(
                () => cfg.NpcQuestIconMinSize, v => cfg.NpcQuestIconMinSize = v,
                () => cfg.NpcQuestIconMaxSize, v => cfg.NpcQuestIconMaxSize = v, 50, 60, "q");
            EndIndentedDisabled();
        }

        if (ImGui.CollapsingHeader("Gathering Nodes"))
        {
            ch |= DrawEnableAndColor("gath", "Gathering Nodes", () => cfg.ShowGatheringNodes, v => cfg.ShowGatheringNodes = v,
                () => cfg.GatheringColor, v => cfg.GatheringColor = v);

            BeginIndentedDisabled(cfg.ShowGatheringNodes);
            ch |= DrawToggle("Hide untargetable \"ghost\" nodes##gtgt", () => cfg.GatheringOnlyIfTargetable, v => cfg.GatheringOnlyIfTargetable = v,
                "Filters depleted/not yet spawned nodes.");
            ch |= DrawToggle("Show node type icons##gicon", () => cfg.ShowGatheringIcons, v => cfg.ShowGatheringIcons = v);
            BeginIndentedDisabled(cfg.ShowGatheringIcons);
            ch |= DrawSizeSliders(
                () => cfg.GatheringIconMinSize, v => cfg.GatheringIconMinSize = v,
                () => cfg.GatheringIconMaxSize, v => cfg.GatheringIconMaxSize = v, 50, 60, "g");
            EndIndentedDisabled();
            EndIndentedDisabled();
        }

        if (ImGui.CollapsingHeader("Treasure Coffers"))
        {
            ch |= DrawEnableAndColor("tres", "Treasure", () => cfg.ShowTreasure, v => cfg.ShowTreasure = v,
                () => cfg.TreasureColor, v => cfg.TreasureColor = v);

            BeginIndentedDisabled(cfg.ShowTreasure);
            ch |= DrawSizeSliders(
                () => cfg.TreasureMinSize, v => cfg.TreasureMinSize = v,
                () => cfg.TreasureMaxSize, v => cfg.TreasureMaxSize = v, 50, 60, "tr");
            ImGui.Spacing();

            ch |= DrawToggle("Show chest icon##tricon", () => cfg.ShowTreasureIcons, v => cfg.ShowTreasureIcons = v);
            BeginIndentedDisabled(cfg.ShowTreasureIcons);
            int trIconId = cfg.TreasureIconId;
            ImGui.SetNextItemWidth(90f);
            if (ImGui.InputInt("Icon ID##triconid", ref trIconId, 0, 0))
            { cfg.TreasureIconId = Math.Max(0, trIconId); ch = true; }
            EndIndentedDisabled();
            EndIndentedDisabled();
        }

        if (ImGui.CollapsingHeader("Aetherytes"))
        {
            ch |= DrawEnableAndColor("aeth", "Aetherytes", () => cfg.ShowAetherytes, v => cfg.ShowAetherytes = v,
                () => cfg.AetheryteColor, v => cfg.AetheryteColor = v);

            BeginIndentedDisabled(cfg.ShowAetherytes);
            ch |= DrawToggle("Show Aethernet shards##aethshards", () => cfg.ShowAethernetShards, v => cfg.ShowAethernetShards = v);
            ch |= DrawToggle("Show Aetheryte icon##aicon", () => cfg.ShowAetheryteIcons, v => cfg.ShowAetheryteIcons = v);
            ch |= DrawSizeSliders(
                () => cfg.AetheryteIconMinSize, v => cfg.AetheryteIconMinSize = v,
                () => cfg.AetheryteIconMaxSize, v => cfg.AetheryteIconMaxSize = v, 50, 60, "a");

            string shardName = cfg.AethernetShardName;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.InputText("Aethernet shard name##shardname", ref shardName, 64))
            { cfg.AethernetShardName = shardName; ch = true; }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Substring in shard names to identify them (e.g. \"Aethernet\").");
            EndIndentedDisabled();
        }

        if (ImGui.CollapsingHeader("FATEs"))
        {
            ch |= DrawEnableAndColor("fates", "Show FATEs", () => cfg.ShowFates, v => cfg.ShowFates = v,
                () => cfg.FateColor, v => cfg.FateColor = v,
                "Shows active/about to start FATEs; range = General range × multiplier.");

            BeginIndentedDisabled(cfg.ShowFates);
            ch |= DrawSliderFloat("Distance multiplier##fatemul", 0.5f, 5.0f,
                () => cfg.FateDistanceMultiplier, v => cfg.FateDistanceMultiplier = MathF.Max(0.5f, v), "%.1f×");
            ImGui.TextDisabled($"Effective FATE range: {cfg.MaxMarkerDistance * cfg.FateDistanceMultiplier:F0} yalms");
            ch |= DrawSizeSliders(
                () => cfg.FateIconMinSize, v => cfg.FateIconMinSize = v,
                () => cfg.FateIconMaxSize, v => cfg.FateIconMaxSize = v, 50, 64, "fate");
            EndIndentedDisabled();
        }

        if (ImGui.CollapsingHeader("Limit Break Glow"))
        {
            ch |= DrawEnableAndColor("lbglow", "Limit break glow (bar 1 color)",
                () => cfg.ShowLimitBreakGlow, v => cfg.ShowLimitBreakGlow = v,
                () => cfg.LimitBreakGlowColor, v => cfg.LimitBreakGlowColor = v,
                "Glowing border as LB charges – one layer per bar.");
            BeginIndentedDisabled(cfg.ShowLimitBreakGlow);
            ch |= DrawColorEdit("Bar 2 color##lbc2", cfg.LimitBreakGlowColor2, v => cfg.LimitBreakGlowColor2 = v, ColorPickerFlags);
            ch |= DrawColorEdit("Bar 3 color##lbc3", cfg.LimitBreakGlowColor3, v => cfg.LimitBreakGlowColor3 = v, ColorPickerFlags);
            EndIndentedDisabled();
        }

        EndIndentedDisabled();

        ImGui.EndTabItem();
        return ch;
    }

    private bool DrawCombatTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("HP Bars & Statuses")) return false;
        bool ch = false;

        if (ImGui.CollapsingHeader("Target Health Bar", ImGuiTreeNodeFlags.DefaultOpen))
        {
            BeginIndentedDisabled(cfg.ShowTargetBar);
            ch |= DrawSliderFloat("Width (fraction of compass)##tbwf", 0.3f, 1.0f,
                () => cfg.TargetBarWidthFraction, v => cfg.TargetBarWidthFraction = v);
            ch |= DrawSliderInt("Bar thickness##tbh", 6, 30, () => (int)cfg.TargetBarHeight, v => cfg.TargetBarHeight = v);
            ch |= DrawSliderFloat("Name font scale##tbfs", 0.5f, 2.5f,
                () => cfg.TargetBarFontScale, v => cfg.TargetBarFontScale = v);
            ch |= DrawToggle("Show target level##tblvl", () => cfg.ShowTargetLevel, v => cfg.ShowTargetLevel = v);
            ch |= DrawToggle("Show HP percentage##tbhpp", () => cfg.ShowTargetHealthPercent, v => cfg.ShowTargetHealthPercent = v,
                "Shows percentage under the health bar.");
            BeginIndentedDisabled(cfg.ShowTargetHealthPercent);
            ch |= DrawToggle("Show on target of target##tbtothpp", () => cfg.ShowTargetOfTargetHealthPercent, v => cfg.ShowTargetOfTargetHealthPercent = v);
            EndIndentedDisabled();
            ch |= DrawEnableAndColor("tbshd", "Show shield overlay",
                () => cfg.ShowTargetBarShield, v => cfg.ShowTargetBarShield = v,
                () => cfg.TargetBarShieldColor, v => cfg.TargetBarShieldColor = v,
                "Light sheen over shielded portion of the bar.");
            ch |= DrawToggle("Show name ribbons##tbrib", () => cfg.ShowTargetBarRibbons, v => cfg.ShowTargetBarRibbons = v,
                "Glowing ribbons from name ornaments.");
            EndIndentedDisabled();
        }

        if (ImGui.CollapsingHeader("Target Status Icons", ImGuiTreeNodeFlags.DefaultOpen))
        {
            BeginIndentedDisabled(cfg.ShowTargetStatuses);
            ch |= DrawSliderInt("Icon size##tssize", 12, 40, () => (int)cfg.TargetStatusIconSize, v => cfg.TargetStatusIconSize = v);
            ch |= DrawSliderInt("Max icons##tsmax", 3, 20, () => cfg.TargetStatusMaxIcons, v => cfg.TargetStatusMaxIcons = v);
            ch |= DrawToggle("Left align##tsalignl", () => cfg.TargetStatusIconAlignLeft,
                v => { cfg.TargetStatusIconAlignLeft = v; if (v) cfg.TargetStatusIconAlignRight = false; },
                "Icons anchor to the left edge and grow rightward, instead of staying centered.");
            ImGui.SameLine();
            ch |= DrawToggle("Right align##tsalignr", () => cfg.TargetStatusIconAlignRight,
                v => { cfg.TargetStatusIconAlignRight = v; if (v) cfg.TargetStatusIconAlignLeft = false; },
                "Icons anchor to the right edge and grow leftward, instead of staying centered.");
            EndIndentedDisabled();
        }

        if (ImGui.CollapsingHeader("Target-of-Target"))
        {
            BeginIndentedDisabled(cfg.ShowTargetBar);
            ch |= DrawToggle("Target-of-target bar", () => cfg.ShowTargetOfTargetBar, v => cfg.ShowTargetOfTargetBar = v,
                "Shows who/what your target has targeted.");
            BeginIndentedDisabled(cfg.ShowTargetOfTargetBar);
            ch |= DrawToggle("Highlight if targeting me##aggro", () => cfg.HighlightIfTargetingMe, v => cfg.HighlightIfTargetingMe = v);
            ImGui.BeginDisabled(!cfg.HighlightIfTargetingMe);
            ch |= DrawColorEdit("Warning color##aggroc", cfg.AggroWarningColor, v => cfg.AggroWarningColor = v, ColorPickerFlags);
            ImGui.EndDisabled();

            ch |= DrawToggle("Show name##totname", () => cfg.ShowTargetOfTargetName, v => cfg.ShowTargetOfTargetName = v,
                "Shows the target of target's name centered over their bar.");
            BeginIndentedDisabled(cfg.ShowTargetOfTargetName);
            ch |= DrawToggle("Only show first name##totfirstname", () => cfg.TargetOfTargetFirstNameOnly, v => cfg.TargetOfTargetFirstNameOnly = v);
            ch |= DrawToggle("Show \"YOU\" for yourself##totyou", () => cfg.TargetOfTargetShowYou, v => cfg.TargetOfTargetShowYou = v,
                "Displays \"YOU\" instead of your character name when you are the target of target.");
            EndIndentedDisabled();
            EndIndentedDisabled();
            EndIndentedDisabled();
        }

        ImGui.EndTabItem();
        return ch;
    }

    private bool DrawAdvancedTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Advanced")) return false;
        bool ch = false;
        bool ovCh = false;

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
                    ovCh = true;
                ImGui.PopID();
            }
            if (removeAt >= 0)
            {
                cfg.PlayerIconOverrides.RemoveAt(removeAt);
                ovCh = true;
            }

            ImGui.Separator();
            ImGui.TextDisabled("Add new override:");
            ImGui.SameLine();
            ch |= DrawOverrideRow(_newOverride, "newov", 120f);
            ImGui.SameLine();

            bool canAdd = !string.IsNullOrWhiteSpace(_newOverride.PlayerName) && _newOverride.IconBaseId > 0;
            ImGui.BeginDisabled(!canAdd);
            if (ImGui.Button("Add##addov"))
            {
                _newOverride.PlayerName = _newOverride.PlayerName.Trim();
                cfg.PlayerIconOverrides.Add(_newOverride);
                ovCh = true;
                _newOverride = new PlayerIconOverride
                {
                    ShowBorder = _newOverride.ShowBorder,
                    BorderColor = _newOverride.BorderColor,
                    ShowFill = _newOverride.ShowFill,
                    FillColor = _newOverride.FillColor,
                    ClipToCircle = _newOverride.ClipToCircle,
                    SizeMultiplier = _newOverride.SizeMultiplier,
                };
                ch = true;
            }
            ImGui.EndDisabled();

            if (ovCh)
                cfg.IncrementOverrideVersion();
        }

        if (ImGui.CollapsingHeader("Moodles Loci Mirror", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ch |= DrawToggle("Mirror Moodles <-> Loci", () => cfg.MirrorMoodlesLoci, v => cfg.MirrorMoodlesLoci = v,
                "Keep your own Moodles and Loci statuses mirrored onto each other.");

            BeginIndentedDisabled(cfg.MirrorMoodlesLoci);
            var mirror = plugin.StatusMirror;
            ImGui.TextDisabled($"Moodles: {(mirror.MoodlesAvailable ? "connected" : "not found")}   " +
                               $"Loci: {(mirror.LociAvailable ? "connected" : "not found")}");
            ImGui.TextDisabled($"Mirrored into Loci: {mirror.MirroredIntoLociCount}   " +
                               $"Mirrored into Moodles: {mirror.MirroredIntoMoodlesCount}" +
                               (mirror.LockedMirrorCount > 0 ? $"   Locked: {mirror.LockedMirrorCount}" : ""));
            EndIndentedDisabled();
        }

        ImGui.EndTabItem();
        return ch || ovCh;
    }

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

    private static bool DrawOverrideRow(PlayerIconOverride ov, string idSuffix, float nameWidth)
    {
        bool ch = false;
        string name = ov.PlayerName;
        ImGui.SetNextItemWidth(nameWidth);
        if (ImGui.InputText($"##{idSuffix}name", ref name, 64)) { ov.PlayerName = name; ch = true; }
        ImGui.SameLine();
        int iconId = ov.IconBaseId;
        ImGui.SetNextItemWidth(68f);
        if (ImGui.InputInt($"##{idSuffix}id", ref iconId, 0, 0)) { ov.IconBaseId = Math.Max(0, iconId); ch = true; }
        ImGui.SameLine();

        ch |= DrawToggle($"B##{idSuffix}b", () => ov.ShowBorder, v => ov.ShowBorder = v);
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowBorder);
        ch |= DrawColorEdit($"##{idSuffix}bc", ov.BorderColor, v => ov.BorderColor = v, ColorPickerFlags);
        ImGui.EndDisabled();
        ImGui.SameLine();

        ch |= DrawToggle($"F##{idSuffix}f", () => ov.ShowFill, v => ov.ShowFill = v);
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowFill);
        ch |= DrawColorEdit($"##{idSuffix}fc", ov.FillColor, v => ov.FillColor = v, ColorPickerFlags);
        ImGui.EndDisabled();
        ImGui.SameLine();

        ch |= DrawToggle($"○##{idSuffix}circ", () => ov.ClipToCircle, v => ov.ClipToCircle = v);
        ImGui.SameLine();

        float mul = ov.SizeMultiplier;
        ImGui.SetNextItemWidth(58f);
        if (ImGui.DragFloat($"##{idSuffix}mul", ref mul, 0.05f, 0.5f, 3.0f, "%.2fx"))
        { ov.SizeMultiplier = Math.Clamp(mul, 0.5f, 3.0f); ch = true; }

        return ch;
    }

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
        bool ch = ImGui.Checkbox(label, ref v);
        if (ch) set(v);
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return ch;
    }

    private static bool DrawEnableAndColor(
        string idPrefix, string label, Func<bool> getEnabled, Action<bool> setEnabled,
        Func<Vector4> getColor, Action<Vector4> setColor, string? tooltip = null)
    {
        bool en = getEnabled();
        bool ch = ImGui.Checkbox($"##{idPrefix}_en", ref en);
        if (ch) setEnabled(en);
        ImGui.SameLine();
        Vector4 col = getColor();
        if (ImGui.ColorEdit4($"{label}##{idPrefix}_c", ref col, ColorPickerFlags)) { setColor(col); ch = true; }
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return ch;
    }

    private static bool DrawSizeSliders(
        Func<float> getMin, Action<float> setMin, Func<float> getMax, Action<float> setMax,
        int minHi, int maxHi, string idPrefix,
        string minLabel = "Min size (far)", string maxLabel = "Max size (close)",
        int lo = 8)
    {
        bool ch = false;
        int mn = (int)getMin();
        if (ImGui.SliderInt($"{minLabel}##{idPrefix}min", ref mn, lo, minHi)) { setMin(mn); ch = true; }
        int mx = (int)getMax();
        if (ImGui.SliderInt($"{maxLabel}##{idPrefix}max", ref mx, lo, maxHi)) { setMax(mx); ch = true; }
        return ch;
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
        bool ch = fmt is null ? ImGui.SliderFloat(label, ref v, lo, hi) : ImGui.SliderFloat(label, ref v, lo, hi, fmt);
        if (ch) set(v);
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return ch;
    }

    private static bool DrawColorEdit(string label, Vector4 val, Action<Vector4> set, ImGuiColorEditFlags flags = ImGuiColorEditFlags.None)
    {
        if (ImGui.ColorEdit4(label, ref val, flags)) { set(val); return true; }
        return false;
    }

    private static readonly ImGuiColorEditFlags ColorPickerFlags =
        ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar;
}