using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace SkyrimCompass;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private PlayerIconOverride _newOverride = new();
    private int _selectedThemeIndex;
    private DateTime _lastConfigSave = DateTime.MinValue;
    private const float ConfigSaveDebounceSeconds = 1f;
    private bool _configDirty;

    public ConfigWindow(Plugin plugin) : base("Skyrim Compass Settings##skyrimcompasscfg",
        ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(370, 200), MaximumSize = new Vector2(620, 800) };
    }

    public override void Draw()
    {
        var cfg = plugin.Config;
        bool dirty = false;

        dirty |= DrawToggle("Compass##topcompass", () => cfg.ShowCompassBar, v => cfg.ShowCompassBar = v, "The compass element. Full options in Compass tab.");
        ImGui.SameLine();
        dirty |= DrawToggle("HP bars##tophp", () => cfg.ShowTargetBar, v => cfg.ShowTargetBar = v, "Name+HP bars beneath the compass. Full options in HP Bars & Statuses tab.");
        ImGui.SameLine();
        dirty |= DrawToggle("Statuses##topstatus", () => cfg.ShowTargetStatuses, v => cfg.ShowTargetStatuses = v, "Target's buff/debuff icons. Full options in HP Bars & Statuses tab.");
        ImGui.Separator();

        if (ImGui.BeginTabBar("##tabs"))
        {
            dirty |= DrawAppearanceTab(cfg);
            dirty |= DrawCompassTab(cfg);
            dirty |= DrawCombatTab(cfg);
            dirty |= DrawAdvancedTab(cfg);
            ImGui.EndTabBar();
        }

        ImGui.Separator();
        if (ImGui.Button("Close", new Vector2(80, 0))) IsOpen = false;
        ImGui.SameLine();
        if (ImGui.Button("Setup Wizard")) plugin.OpenFirstTimeSetup();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Re-open the \"Give me everything\" / \"Moodles + Loci only\" quick setup.");

        if (dirty) _configDirty = true;

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
            ch |= DrawSliderInt("Width##w", 200, 1400, () => (int)cfg.CompassWidth, v => cfg.CompassWidth = v, 120f);
            ImGui.SameLine();
            ch |= DrawSliderInt("Height##h", 20, 80, () => (int)cfg.CompassHeight, v => cfg.CompassHeight = v, 120f);

            ch |= DrawToggle("Lock position##lockpos", () => cfg.LockPosition, v => cfg.LockPosition = v, "When unlocked, click and drag the compass, target bars, or target's status icons to move it all.");
            ImGui.SameLine();
            if (ImGui.Button("Center##centerpos")) { cfg.XOffset = 0f; ch = true; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Recenters the compass horizontally.");

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
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Overwrites all colors; pick \"Original\" to restore defaults.");

            ch |= DrawColorEdit("Compass Background, Status tooltips##bgc", cfg.BackgroundColor, v => cfg.BackgroundColor = v);
            ch |= DrawColorEdit("Border##bdc", cfg.BorderColor, v => cfg.BorderColor = v);
            ch |= DrawColorEdit("Cardinal (N/S/E/W)##cdc", cfg.CardinalColor, v => cfg.CardinalColor = v);
            ch |= DrawColorEdit("Intercardinal (NE/SW…)##icc", cfg.IntercardinalColor, v => cfg.IntercardinalColor = v);
            ch |= DrawColorEdit("Tick marks##tkc", cfg.TickColor, v => cfg.TickColor = v);
        }

        ImGui.EndTabItem();
        return ch;
    }

    private bool DrawCompassTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Compass")) return false;
        bool ch = false;

        // Helper to draw a standard collapsible header with a disable wrapper
        void DrawSection(string label, bool enabled, Action draw, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
        {
            ImGui.BeginDisabled(!enabled);
            if (ImGui.CollapsingHeader(label, flags)) draw();
            ImGui.EndDisabled();
        }

        DrawSection("Compass Settings", cfg.ShowCompassBar, () =>
        {
            ch |= DrawSliderInt("Detection range (yalms)##maxd", 10, 200, () => (int)cfg.MaxMarkerDistance, v => cfg.MaxMarkerDistance = v, tooltip: "Maximum distance for all markers (FATEs use a multiplier).");
            ch |= DrawSliderInt("Visible Degrees##vd", 30, 180, () => (int)cfg.VisibleDegrees, v => cfg.VisibleDegrees = v);
            ch |= DrawSliderFloat("Lens Strength##ls", 1f, 3f, () => cfg.LensStrength, v => cfg.LensStrength = v);
            ch |= DrawSliderFloat("Font Scale##fs", .5f, 2.5f, () => cfg.FontScale, v => cfg.FontScale = v);
            ImGui.Spacing();
            ImGui.TextDisabled("Marker distance fade");
            ch |= DrawSliderFloat("Full opacity zone##nz", .5f, 1f, () => cfg.DotNearZone, v => cfg.DotNearZone = v, tooltip: "Dots fully opaque closer than this fraction of max range; 1.0 = always opaque.");
            ch |= DrawSliderFloat("Fade to zero zone##fz", 0f, .5f, () => cfg.DotFarZone, v => cfg.DotFarZone = v, tooltip: "Dots fade to invisible below this fraction of max range; 0.0 = no fade‑to‑zero.");
            ch |= DrawSliderFloat("Midrange opacity##ma", 0f, 1f, () => cfg.DotMidAlpha, v => cfg.DotMidAlpha = v, tooltip: "Opacity of dots in the middle distance band.");
        }, ImGuiTreeNodeFlags.DefaultOpen);

        DrawSection("Camera & Direction", cfg.ShowCompassBar, () =>
        {
            ch |= DrawToggle("Use camera direction (not character facing)", () => cfg.UseCameraDirection, v => cfg.UseCameraDirection = v);
            ImGui.BeginDisabled(!cfg.UseCameraDirection);
            ch |= DrawToggle("Also measure distances from camera location##ucp", () => cfg.UseCameraPosition, v => cfg.UseCameraPosition = v);
            ImGui.EndDisabled();
            ImGui.Spacing();
            ImGui.TextDisabled("Rotation Offset (set 180 if N/S swapped)");
            ch |= DrawSliderInt("##rotoff", -180, 180, () => (int)cfg.RotationOffset, v => cfg.RotationOffset = v);
        }, ImGuiTreeNodeFlags.DefaultOpen);

        DrawSection("Players", cfg.ShowCompassBar, () =>
        {
            ch |= DrawEnableAndColor("players", "Players", () => cfg.ShowPlayers, v => cfg.ShowPlayers = v, () => cfg.PlayerColor, v => cfg.PlayerColor = v);
            ImGui.BeginDisabled(!cfg.ShowPlayers);
            ch |= DrawSizeSliders(() => cfg.PartyRoleIconMinSize, v => cfg.PartyRoleIconMinSize = v, () => cfg.PartyRoleIconMaxSize, v => cfg.PartyRoleIconMaxSize = v, 50, 60, "pr");
            ImGui.Spacing();
            ch |= DrawToggle("Solid dot for friends##sfr", () => cfg.SolidFriendDots, v => cfg.SolidFriendDots = v);
            ch |= DrawToggle("Show job icon for party members##pri", () => cfg.ShowPartyRoleIcons, v => cfg.ShowPartyRoleIcons = v);
            ImGui.BeginDisabled(!cfg.ShowPartyRoleIcons);
            ch |= DrawToggle("Only in duty / PvP##pridonly", () => cfg.PartyRoleIconsOnlyInDuty, v => cfg.PartyRoleIconsOnlyInDuty = v);
            ImGui.EndDisabled();
            ImGui.EndDisabled();
        }, ImGuiTreeNodeFlags.DefaultOpen);

        DrawSection("Enemies", cfg.ShowCompassBar, () =>
        {
            ch |= DrawEnableAndColor("enemies", "Enemies", () => cfg.ShowEnemies, v => cfg.ShowEnemies = v, () => cfg.EnemyColor, v => cfg.EnemyColor = v);
            ImGui.BeginDisabled(!cfg.ShowEnemies);
            ch |= DrawToggle("Only show aggro'ed enemies##eng", () => cfg.EnemiesOnlyIfEngaged, v => cfg.EnemiesOnlyIfEngaged = v);
            ch |= DrawSizeSliders(() => cfg.EnemyMinSize, v => cfg.EnemyMinSize = v, () => cfg.EnemyMaxSize, v => cfg.EnemyMaxSize = v, 50, 60, "en");
            ImGui.EndDisabled();
        });

        DrawSection("NPCs", cfg.ShowCompassBar, () =>
        {
            ch |= DrawEnableAndColor("npcs", "NPCs", () => cfg.ShowNpcs, v => cfg.ShowNpcs = v, () => cfg.NpcColor, v => cfg.NpcColor = v);
            ImGui.BeginDisabled(!cfg.ShowNpcs);
            ch |= DrawToggle("Hide untargetable \"ghost\" NPCs##tgt", () => cfg.NpcsOnlyIfTargetable, v => cfg.NpcsOnlyIfTargetable = v, "Filters placeholder/empty slot NPCs.");
            ch |= DrawToggle("Show quest marker icons##qicon", () => cfg.ShowNpcQuestIcons, v => cfg.ShowNpcQuestIcons = v);
            ch |= DrawToggle("Show Shops/Menders##sicon", () => cfg.ShowShopIcons, v => cfg.ShowShopIcons = v);
            ch |= DrawToggle("Show Fast Travel icons##fticon", () => cfg.ShowFastTravelIcons, v => cfg.ShowFastTravelIcons = v, "Ferry, airship, Chocobo Keep, etc.");
            ch |= DrawSizeSliders(() => cfg.NpcQuestIconMinSize, v => cfg.NpcQuestIconMinSize = v, () => cfg.NpcQuestIconMaxSize, v => cfg.NpcQuestIconMaxSize = v, 50, 60, "q");
            ImGui.EndDisabled();
        });

        DrawSection("Gathering Nodes", cfg.ShowCompassBar, () =>
        {
            ch |= DrawEnableAndColor("gath", "Gathering Nodes", () => cfg.ShowGatheringNodes, v => cfg.ShowGatheringNodes = v, () => cfg.GatheringColor, v => cfg.GatheringColor = v);
            ImGui.BeginDisabled(!cfg.ShowGatheringNodes);
            ch |= DrawToggle("Hide untargetable \"ghost\" nodes##gtgt", () => cfg.GatheringOnlyIfTargetable, v => cfg.GatheringOnlyIfTargetable = v, "Filters depleted/not yet spawned nodes.");
            ch |= DrawToggle("Show node type icons##gicon", () => cfg.ShowGatheringIcons, v => cfg.ShowGatheringIcons = v);
            ImGui.BeginDisabled(!cfg.ShowGatheringIcons);
            ch |= DrawSizeSliders(() => cfg.GatheringIconMinSize, v => cfg.GatheringIconMinSize = v, () => cfg.GatheringIconMaxSize, v => cfg.GatheringIconMaxSize = v, 50, 60, "g");
            ImGui.EndDisabled();
            ImGui.EndDisabled();
        });

        DrawSection("Treasure Coffers", cfg.ShowCompassBar, () =>
        {
            ch |= DrawEnableAndColor("tres", "Treasure", () => cfg.ShowTreasure, v => cfg.ShowTreasure = v, () => cfg.TreasureColor, v => cfg.TreasureColor = v);
            ImGui.BeginDisabled(!cfg.ShowTreasure);
            ch |= DrawSizeSliders(() => cfg.TreasureMinSize, v => cfg.TreasureMinSize = v, () => cfg.TreasureMaxSize, v => cfg.TreasureMaxSize = v, 50, 60, "tr");
            ImGui.Spacing();
            ch |= DrawToggle("Show chest icon##tricon", () => cfg.ShowTreasureIcons, v => cfg.ShowTreasureIcons = v);
            ImGui.BeginDisabled(!cfg.ShowTreasureIcons);
            int trIconId = cfg.TreasureIconId;
            ImGui.SetNextItemWidth(90f);
            if (ImGui.InputInt("Icon ID##triconid", ref trIconId, 0, 0)) { cfg.TreasureIconId = Math.Max(0, trIconId); ch = true; }
            ImGui.EndDisabled();
            ImGui.EndDisabled();
        });

        DrawSection("Aetherytes", cfg.ShowCompassBar, () =>
        {
            ch |= DrawEnableAndColor("aeth", "Aetherytes", () => cfg.ShowAetherytes, v => cfg.ShowAetherytes = v, () => cfg.AetheryteColor, v => cfg.AetheryteColor = v);
            ImGui.BeginDisabled(!cfg.ShowAetherytes);
            ch |= DrawToggle("Show Aethernet shards##aethshards", () => cfg.ShowAethernetShards, v => cfg.ShowAethernetShards = v);
            ch |= DrawToggle("Show Aetheryte icon##aicon", () => cfg.ShowAetheryteIcons, v => cfg.ShowAetheryteIcons = v);
            ch |= DrawSizeSliders(() => cfg.AetheryteIconMinSize, v => cfg.AetheryteIconMinSize = v, () => cfg.AetheryteIconMaxSize, v => cfg.AetheryteIconMaxSize = v, 50, 60, "a");
            string shardName = cfg.AethernetShardName;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.InputText("Aethernet shard name##shardname", ref shardName, 64)) { cfg.AethernetShardName = shardName; ch = true; }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Substring in shard names to identify them (e.g. \"Aethernet\").");
            ImGui.EndDisabled();
        });

        DrawSection("FATEs", cfg.ShowCompassBar, () =>
        {
            ch |= DrawEnableAndColor("fates", "Show FATEs", () => cfg.ShowFates, v => cfg.ShowFates = v, () => cfg.FateColor, v => cfg.FateColor = v, "Shows active/about to start FATEs; range = General range × multiplier.");
            ImGui.BeginDisabled(!cfg.ShowFates);
            ch |= DrawSliderFloat("Distance multiplier##fatemul", .5f, 5f, () => cfg.FateDistanceMultiplier, v => cfg.FateDistanceMultiplier = MathF.Max(.5f, v), "%.1f×");
            ImGui.TextDisabled($"Effective FATE range: {cfg.MaxMarkerDistance * cfg.FateDistanceMultiplier:F0} yalms");
            ch |= DrawSizeSliders(() => cfg.FateIconMinSize, v => cfg.FateIconMinSize = v, () => cfg.FateIconMaxSize, v => cfg.FateIconMaxSize = v, 50, 64, "fate");
            ImGui.EndDisabled();
        });

        DrawSection("Limit Break Glow", cfg.ShowCompassBar, () =>
        {
            ch |= DrawEnableAndColor("lbglow", "Limit break glow (bar 1 color)", () => cfg.ShowLimitBreakGlow, v => cfg.ShowLimitBreakGlow = v, () => cfg.LimitBreakGlowColor, v => cfg.LimitBreakGlowColor = v, "Glowing border as LB charges – one layer per bar.");
            ImGui.BeginDisabled(!cfg.ShowLimitBreakGlow);
            ch |= DrawColorEdit("Bar 2 color##lbc2", cfg.LimitBreakGlowColor2, v => cfg.LimitBreakGlowColor2 = v);
            ch |= DrawColorEdit("Bar 3 color##lbc3", cfg.LimitBreakGlowColor3, v => cfg.LimitBreakGlowColor3 = v);
            ImGui.EndDisabled();
        });

        ImGui.EndTabItem();
        return ch;
    }

    private bool DrawCombatTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("HP Bars & Statuses")) return false;
        bool ch = false;

        void DrawSection(string label, bool enabled, Action draw, ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.None)
        {
            ImGui.BeginDisabled(!enabled);
            if (ImGui.CollapsingHeader(label, flags)) draw();
            ImGui.EndDisabled();
        }

        DrawSection("Target Health Bar", cfg.ShowTargetBar, () =>
        {
            ch |= DrawSliderFloat("Width (fraction of compass)##tbwf", .3f, 1f, () => cfg.TargetBarWidthFraction, v => cfg.TargetBarWidthFraction = v);
            ch |= DrawSliderInt("Bar thickness##tbh", 6, 30, () => (int)cfg.TargetBarHeight, v => cfg.TargetBarHeight = v);
            ch |= DrawSliderFloat("Name font scale##tbfs", .5f, 2.5f, () => cfg.TargetBarFontScale, v => cfg.TargetBarFontScale = v);
            ch |= DrawToggle("Show target level##tblvl", () => cfg.ShowTargetLevel, v => cfg.ShowTargetLevel = v);
            ch |= DrawToggle("Show HP percentage##tbhpp", () => cfg.ShowTargetHealthPercent, v => cfg.ShowTargetHealthPercent = v, "Shows percentage under the health bar.");
            ImGui.BeginDisabled(!cfg.ShowTargetHealthPercent);
            ch |= DrawToggle("Show on target of target##tbtothpp", () => cfg.ShowTargetOfTargetHealthPercent, v => cfg.ShowTargetOfTargetHealthPercent = v);
            ImGui.EndDisabled();
            ch |= DrawEnableAndColor("tbshd", "Show shield overlay", () => cfg.ShowTargetBarShield, v => cfg.ShowTargetBarShield = v, () => cfg.TargetBarShieldColor, v => cfg.TargetBarShieldColor = v, "Light sheen over shielded portion of the bar.");
            ch |= DrawToggle("Show name ribbons##tbrib", () => cfg.ShowTargetBarRibbons, v => cfg.ShowTargetBarRibbons = v, "Glowing ribbons from name ornaments.");
        }, ImGuiTreeNodeFlags.DefaultOpen);

        DrawSection("Target-of-Target", cfg.ShowTargetBar, () =>
        {
            ch |= DrawToggle("Target-of-target bar", () => cfg.ShowTargetOfTargetBar, v => cfg.ShowTargetOfTargetBar = v, "Shows who/what your target has targeted.");
            ImGui.BeginDisabled(!cfg.ShowTargetOfTargetBar);
            ch |= DrawToggle("Highlight if targeting me##aggro", () => cfg.HighlightIfTargetingMe, v => cfg.HighlightIfTargetingMe = v);
            ImGui.BeginDisabled(!cfg.HighlightIfTargetingMe);
            ch |= DrawColorEdit("Warning color##aggroc", cfg.AggroWarningColor, v => cfg.AggroWarningColor = v);
            ImGui.EndDisabled();
            ch |= DrawToggle("Show name##totname", () => cfg.ShowTargetOfTargetName, v => cfg.ShowTargetOfTargetName = v, "Shows the target of target's name centered over their bar.");
            ImGui.BeginDisabled(!cfg.ShowTargetOfTargetName);
            ch |= DrawToggle("Only show first name##totfirstname", () => cfg.TargetOfTargetFirstNameOnly, v => cfg.TargetOfTargetFirstNameOnly = v);
            ch |= DrawToggle("Show \"YOU\" for yourself##totyou", () => cfg.TargetOfTargetShowYou, v => cfg.TargetOfTargetShowYou = v, "Displays \"YOU\" instead of your character name when you are the target of target.");
            ImGui.EndDisabled();
            ImGui.EndDisabled();
        });

        DrawSection("Target Status Icons", cfg.ShowTargetStatuses, () =>
        {
            ch |= DrawSliderInt("Icon size##tssize", 12, 40, () => (int)cfg.TargetStatusIconSize, v => cfg.TargetStatusIconSize = v);
            ch |= DrawSliderInt("Max icons##tsmax", 3, 20, () => cfg.TargetStatusMaxIcons, v => cfg.TargetStatusMaxIcons = v);
            ch |= DrawToggle("Left align##tsalignl", () => cfg.TargetStatusIconAlignLeft, v => { cfg.TargetStatusIconAlignLeft = v; if (v) cfg.TargetStatusIconAlignRight = false; }, "Icons anchor to the left edge and grow rightward, instead of staying centered.");
            ImGui.SameLine();
            ch |= DrawToggle("Right align##tsalignr", () => cfg.TargetStatusIconAlignRight, v => { cfg.TargetStatusIconAlignRight = v; if (v) cfg.TargetStatusIconAlignLeft = false; }, "Icons anchor to the right edge and grow leftward, instead of staying centered.");
            ch |= DrawEnableAndColor("tsesuna", "Esuna-able marker", () => cfg.ShowTargetStatusEsunaMarker, v => cfg.ShowTargetStatusEsunaMarker = v, () => cfg.TargetStatusEsunaMarkerColor, v => cfg.TargetStatusEsunaMarkerColor = v, "Bar above statuses that Esuna/dispel can remove: real game data for vanilla statuses, the author's own 'Dispelable' setting for Moodles/Loci. That setting depends on the target's own Moodles/Loci config to actually do anything, so treat it as intended rather than guaranteed.");
        }, ImGuiTreeNodeFlags.DefaultOpen);

        ImGui.EndTabItem();
        return ch;
    }

    private bool DrawAdvancedTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Advanced")) return false;
        bool ch = false, ovCh = false;

        if (ImGui.CollapsingHeader("Player Icon Overrides", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextDisabled("Override icons for specific players by name. (IconIDs can be found in /xldata icons)");
            if (cfg.PlayerIconOverrides.Count == 0) ImGui.TextDisabled("  (none)");

            int removeAt = -1;
            for (int i = 0; i < cfg.PlayerIconOverrides.Count; i++)
            {
                ImGui.PushID(i);
                if (ImGui.Button("X##rmov")) removeAt = i;
                ImGui.SameLine();
                if (DrawOverrideRow(cfg.PlayerIconOverrides[i], "ov", 110f)) ovCh = true;
                ImGui.PopID();
            }
            if (removeAt >= 0) { cfg.PlayerIconOverrides.RemoveAt(removeAt); ovCh = true; }

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

            if (ovCh) cfg.IncrementOverrideVersion();
        }

        if (ImGui.CollapsingHeader("Moodles Loci Mirror", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ch |= DrawToggle("Mirror Moodles <-> Loci", () => cfg.MirrorMoodlesLoci, v => cfg.MirrorMoodlesLoci = v, "Keep your own Moodles and Loci statuses mirrored onto each other.");
            ImGui.BeginDisabled(!cfg.MirrorMoodlesLoci);
            var mirror = plugin.StatusMirror;
            ImGui.TextDisabled($"Moodles: {(mirror.MoodlesAvailable ? "connected" : "not found")}   Loci: {(mirror.LociAvailable ? "connected" : "not found")}");
            ImGui.TextDisabled($"Mirrored into Loci: {mirror.MirroredIntoLociCount}   Mirrored into Moodles: {mirror.MirroredIntoMoodlesCount}" +
                (mirror.LockedMirrorCount > 0 ? $"   Locked: {mirror.LockedMirrorCount}" : ""));
            ImGui.EndDisabled();
        }

        ImGui.EndTabItem();
        return ch || ovCh;
    }

    // ----- Theme definitions (unchanged but compact) -----
    private record ColorTheme(string Name, Vector4 Background, Vector4 Border, Vector4 Cardinal,
        Vector4 Intercardinal, Vector4 Tick, Vector4 Player, Vector4 Enemy, Vector4 Npc,
        Vector4 Gathering, Vector4 Treasure, Vector4 Aetheryte, Vector4 Fate);

    private static readonly ColorTheme[] ColorThemes =
    [
        new("Original", new(.05f,.04f,.03f,.82f), new(.48f,.42f,.27f,.92f), new(1f,.97f,.88f,1f),
            new(.72f,.70f,.65f,.88f), new(.58f,.56f,.52f,.72f), new(.40f,.65f,1f,.92f), new(1f,.25f,.25f,.92f),
            new(.95f,.88f,.35f,.92f), new(.30f,.92f,.40f,.92f), new(1f,.80f,.15f,.95f), new(.55f,.85f,.95f,.92f), new(.82f,.35f,.95f,.95f)),
        new("Frostfall", new(.03f,.06f,.10f,.84f), new(.55f,.75f,.88f,.92f), new(.92f,.97f,1f,1f),
            new(.68f,.82f,.90f,.88f), new(.55f,.68f,.78f,.72f), new(.50f,.85f,1f,.92f), new(1f,.35f,.40f,.92f),
            new(.85f,.95f,1f,.92f), new(.40f,.95f,.85f,.92f), new(.95f,.92f,.65f,.95f), new(.60f,.90f,1f,.92f), new(.75f,.55f,1f,.95f)),
        new("Inferno", new(.08f,.03f,.02f,.85f), new(.75f,.32f,.10f,.92f), new(1f,.88f,.60f,1f),
            new(.88f,.58f,.32f,.88f), new(.65f,.38f,.22f,.72f), new(.45f,.75f,1f,.92f), new(1f,.18f,.10f,.95f),
            new(1f,.78f,.30f,.92f), new(.55f,.90f,.35f,.92f), new(1f,.70f,.10f,.95f), new(.95f,.55f,.85f,.92f), new(1f,.40f,.85f,.95f)),
        new("Verdant", new(.03f,.06f,.03f,.84f), new(.38f,.58f,.32f,.92f), new(.92f,1f,.85f,1f),
            new(.68f,.82f,.60f,.88f), new(.50f,.62f,.45f,.72f), new(.45f,.80f,1f,.92f), new(1f,.30f,.25f,.92f),
            new(.92f,.85f,.40f,.92f), new(.45f,1f,.50f,.95f), new(1f,.85f,.25f,.95f), new(.55f,.92f,.80f,.92f), new(.78f,.95f,.35f,.95f)),
        new("Void", new(.04f,.02f,.08f,.85f), new(.58f,.38f,.78f,.92f), new(.92f,.85f,1f,1f),
            new(.72f,.62f,.85f,.88f), new(.55f,.48f,.65f,.72f), new(.55f,.72f,1f,.92f), new(1f,.30f,.55f,.92f),
            new(.88f,.78f,1f,.92f), new(.55f,.95f,.65f,.92f), new(1f,.75f,.95f,.95f), new(.68f,.55f,1f,.92f), new(.85f,.40f,1f,.95f))
    ];

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

    // ----- Helper drawing methods (condensed) -----
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
        ch |= DrawColorEdit($"##{idSuffix}bc", ov.BorderColor, v => ov.BorderColor = v);
        ImGui.EndDisabled();
        ImGui.SameLine();

        ch |= DrawToggle($"F##{idSuffix}f", () => ov.ShowFill, v => ov.ShowFill = v);
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowFill);
        ch |= DrawColorEdit($"##{idSuffix}fc", ov.FillColor, v => ov.FillColor = v);
        ImGui.EndDisabled();
        ImGui.SameLine();

        ch |= DrawToggle($"○##{idSuffix}circ", () => ov.ClipToCircle, v => ov.ClipToCircle = v);
        ImGui.SameLine();

        float mul = ov.SizeMultiplier;
        ImGui.SetNextItemWidth(58f);
        if (ImGui.DragFloat($"##{idSuffix}mul", ref mul, .05f, .5f, 3f, "%.2fx"))
        { ov.SizeMultiplier = Math.Clamp(mul, .5f, 3f); ch = true; }
        return ch;
    }

    private static bool DrawToggle(string label, Func<bool> get, Action<bool> set, string? tooltip = null)
    {
        bool v = get();
        bool ch = ImGui.Checkbox(label, ref v);
        if (ch) set(v);
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return ch;
    }

    private static bool DrawEnableAndColor(string idPrefix, string label,
        Func<bool> getEnabled, Action<bool> setEnabled,
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

    private static bool DrawSizeSliders(Func<float> getMin, Action<float> setMin,
        Func<float> getMax, Action<float> setMax, int minHi, int maxHi, string idPrefix,
        string minLabel = "Min size (far)", string maxLabel = "Max size (close)", int lo = 8)
    {
        bool ch = false;
        int mn = (int)getMin();
        if (ImGui.SliderInt($"{minLabel}##{idPrefix}min", ref mn, lo, minHi)) { setMin(mn); ch = true; }
        int mx = (int)getMax();
        if (ImGui.SliderInt($"{maxLabel}##{idPrefix}max", ref mx, lo, maxHi)) { setMax(mx); ch = true; }
        return ch;
    }

    private static bool DrawSliderInt(string label, int lo, int hi, Func<int> get, Action<int> set,
        float width = 0f, string? tooltip = null)
    {
        if (width > 0f) ImGui.SetNextItemWidth(width);
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

    private static bool DrawColorEdit(string label, Vector4 val, Action<Vector4> set,
        ImGuiColorEditFlags flags = ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar)
    {
        if (ImGui.ColorEdit4(label, ref val, flags)) { set(val); return true; }
        return false;
    }

    private static readonly ImGuiColorEditFlags ColorPickerFlags =
        ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar;
}