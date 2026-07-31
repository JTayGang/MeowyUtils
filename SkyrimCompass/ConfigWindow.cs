using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace SkyrimCompass;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private PlayerIconOverride _newOverride = new();   // "add new override" form state — not persisted
    private int _selectedThemeIndex = 0;               // theme dropdown selection — not persisted

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
        var  cfg     = plugin.Config;
        bool changed = false;

        changed |= DrawToggle("##enabled", () => cfg.Enabled, v => cfg.Enabled = v);
        ImGui.SameLine();
        ImGui.Text("Enable Compass");
        ImGui.Separator();

        if (ImGui.BeginTabBar("##tabs"))
        {
            changed |= DrawLayoutTab(cfg);
            changed |= DrawGeneralTab(cfg);
            changed |= DrawPlayersTab(cfg);
            changed |= DrawCombatTab(cfg);
            changed |= DrawNpcsTab(cfg);
            changed |= DrawGatheringTab(cfg);
            changed |= DrawTreasureTab(cfg);
            changed |= DrawAetherytesTab(cfg);
            changed |= DrawFatesTab(cfg);
            ImGui.EndTabBar();
        }

        ImGui.Separator();
        if (ImGui.Button("Close", new Vector2(80, 0)))
            IsOpen = false;

        if (changed)
            cfg.Save(plugin.PluginInterface);
    }

    // ── Layout tab ───────────────────────────────────────────────────────────

    private static bool DrawLayoutTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Layout")) return false;
        bool changed = false;

        changed |= DrawSliderInt("Width##w",  200, 1400, () => (int)cfg.CompassWidth,  v => cfg.CompassWidth  = v);
        changed |= DrawSliderInt("Height##h", 20,  80,   () => (int)cfg.CompassHeight, v => cfg.CompassHeight = v);

        // Y/X bounds track live display size so the bar stays fully on-screen at any resolution
        var io   = ImGui.GetIO();
        int yMax = (int)MathF.Max(0f, io.DisplaySize.Y - cfg.CompassHeight);
        changed |= DrawSliderInt("Y Offset (from top)##yo", 0, yMax, () => (int)cfg.YOffset, v => cfg.YOffset = v);

        int xRange = (int)MathF.Max(0f, (io.DisplaySize.X - cfg.CompassWidth) * 0.5f);
        changed |= DrawSliderInt("X Offset (from center)##xo", -xRange, xRange, () => (int)cfg.XOffset, v => cfg.XOffset = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Shifts the compass left(-)/right(+) of center; range auto-adjusts\n" +
                              "to your screen width so the bar stays fully on-screen.");

        ImGui.Spacing();

        changed |= DrawSliderInt("Visible Degrees##vd", 30, 180, () => (int)cfg.VisibleDegrees, v => cfg.VisibleDegrees = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Degrees visible in the linear center zone. The lens effect adds more at the edges.");

        changed |= DrawSliderFloat("Lens Strength##ls", 1.0f, 3.0f, () => cfg.LensStrength, v => cfg.LensStrength = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fisheye distortion at the edges. 1.0 = linear (off). 2.0 ≈ double the edge degrees.");

        changed |= DrawSliderFloat("Font Scale##fs", 0.5f, 2.5f, () => cfg.FontScale, v => cfg.FontScale = v);

        ImGui.Spacing();

        changed |= DrawToggle("Show numeric heading below bar", () => cfg.ShowHeadingText, v => cfg.ShowHeadingText = v);

        changed |= DrawToggle("Hide during cutscenes", () => cfg.HideDuringCutscenes, v => cfg.HideDuringCutscenes = v,
            "Skips drawing during story cutscenes, skippable cinematics, and group pose —\n" +
            "there's nothing to navigate to while the camera isn't yours anyway.");

        DrawSectionBreak();

        changed |= DrawToggle("Use camera direction instead of character facing",
            () => cfg.UseCameraDirection, v => cfg.UseCameraDirection = v,
            "On: follows where your CAMERA looks (free camera, screenshots, sightseeing).\n" +
            "Off: follows your CHARACTER's facing, like Skyrim's compass (recommended for combat).");

        BeginIndentedDisabled(cfg.UseCameraDirection);
        changed |= DrawToggle("Also use camera location for distances##ucp",
            () => cfg.UseCameraPosition, v => cfg.UseCameraPosition = v,
            "Measures bearings/distances from your CAMERA's position instead of your character's.\n" +
            "Useful zoomed way out or with a camera offset mod. Needs 'Use camera direction' above.");
        EndIndentedDisabled();

        ImGui.Spacing();
        ImGui.TextDisabled("Rotation Offset  (set to 180 if N and S are swapped)");
        changed |= DrawSliderInt("##rotoff", -180, 180, () => (int)cfg.RotationOffset, v => cfg.RotationOffset = v);

        ImGui.EndTabItem();
        return changed;
    }

    // ── Color theme data (consumed by DrawGeneralTab below) ────────────────────
    // Positional order: Name, Background, Border, Cardinal, Intercardinal, Tick, Player, Enemy, Npc, Gathering, Treasure, Aetheryte, Fate
    private sealed record ColorTheme(
        string Name, Vector4 Background, Vector4 Border, Vector4 Cardinal, Vector4 Intercardinal, Vector4 Tick,
        Vector4 Player, Vector4 Enemy, Vector4 Npc, Vector4 Gathering, Vector4 Treasure, Vector4 Aetheryte, Vector4 Fate);

    // "Original" mirrors Configuration's defaults exactly — picking it restores the out-of-box look
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
        cfg.BackgroundColor    = t.Background;
        cfg.BorderColor        = t.Border;
        cfg.CardinalColor      = t.Cardinal;
        cfg.IntercardinalColor = t.Intercardinal;
        cfg.TickColor          = t.Tick;
        cfg.PlayerColor        = t.Player;
        cfg.EnemyColor         = t.Enemy;
        cfg.NpcColor           = t.Npc;
        cfg.GatheringColor     = t.Gathering;
        cfg.TreasureColor      = t.Treasure;
        cfg.AetheryteColor     = t.Aetheryte;
        cfg.FateColor          = t.Fate;
    }

    // ── General tab (bar colors, theme presets, shared detection range/fade — cross-cutting, no single tab owns it) ──

    private bool DrawGeneralTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("General")) return false;
        bool changed = false;

        ImGui.TextDisabled("Bar colors");
        changed |= DrawColorEdit("Background##bgc",                        cfg.BackgroundColor,    v => cfg.BackgroundColor    = v);
        changed |= DrawColorEdit("Border##bdc",                            cfg.BorderColor,        v => cfg.BorderColor        = v);
        changed |= DrawColorEdit("Cardinal labels  (N / S / E / W)##cdc",  cfg.CardinalColor,      v => cfg.CardinalColor      = v);
        changed |= DrawColorEdit("Intercardinal labels  (NE / SW …)##icc", cfg.IntercardinalColor, v => cfg.IntercardinalColor = v);
        changed |= DrawColorEdit("Tick marks##tkc",                        cfg.TickColor,          v => cfg.TickColor          = v);

        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("Theme preset##colortheme", ref _selectedThemeIndex, ColorThemeNames, ColorThemeNames.Length))
        {
            ApplyColorTheme(cfg, ColorThemes[_selectedThemeIndex]);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Overwrites every compass/marker color in one click. Pick \"Original\" to restore\n" +
                              "defaults — colors can still be hand-tweaked after; a theme is a starting point.");

        DrawSectionBreak();

        ImGui.TextDisabled("Detection range  (shared by every marker category, incl. FATEs — see FATEs tab for its multiplier)");
        changed |= DrawSliderInt("yalms##maxd", 10, 200, () => (int)cfg.MaxMarkerDistance, v => cfg.MaxMarkerDistance = v);

        ImGui.Spacing();
        ImGui.TextDisabled("Dot distance-fade curve");

        changed |= DrawSliderFloat("Full opacity zone##nz", 0.5f, 1.0f, () => cfg.DotNearZone, v => cfg.DotNearZone = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Dots are fully opaque closer than this fraction of max range;\n" +
                              "1.00 = always opaque (disables the fade).");

        changed |= DrawSliderFloat("Fade-to-zero zone##fz", 0.0f, 0.5f, () => cfg.DotFarZone, v => cfg.DotFarZone = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Dots fade to invisible below this fraction of max range; 0.00 = no fade-to-zero\n" +
                              "(dots stay at mid opacity until max range).");

        changed |= DrawSliderFloat("Mid-range opacity##ma", 0.0f, 1.0f, () => cfg.DotMidAlpha, v => cfg.DotMidAlpha = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opacity of dots in the middle distance band. 0 = invisible, 1 = opaque.");

        ImGui.EndTabItem();
        return changed;
    }

    // ── Players tab ──────────────────────────────────────────────────────────

    // One editable override row (name/icon/border/fill/clip/multiplier); shared by existing entries and the "add new" form
    private static bool DrawOverrideRow(PlayerIconOverride ov, string idSuffix, float nameWidth)
    {
        bool changed = false;

        string name = ov.PlayerName;
        ImGui.SetNextItemWidth(nameWidth);
        if (ImGui.InputText($"##{idSuffix}name", ref name, 64)) { ov.PlayerName = name; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Player display name (exact, case-insensitive)");
        ImGui.SameLine();

        int iconId = ov.IconBaseId;
        ImGui.SetNextItemWidth(68f);
        if (ImGui.InputInt($"##{idSuffix}id", ref iconId, 0, 0)) { ov.IconBaseId = Math.Max(0, iconId); changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Game icon base ID (e.g. 62007 Paladin, 60453 Aetheryte, 61802 FC emblem).\n" +
                              "Browse all icons with: /xldata icons");
        ImGui.SameLine();

        changed |= DrawToggle($"B##{idSuffix}b", () => ov.ShowBorder, v => ov.ShowBorder = v, "Draw a solid outer ring around the icon");
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowBorder);
        changed |= DrawColorEdit($"##{idSuffix}bc", ov.BorderColor, v => ov.BorderColor = v, ColorPickerFlags);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Border ring color");
        ImGui.EndDisabled();
        ImGui.SameLine();

        changed |= DrawToggle($"F##{idSuffix}f", () => ov.ShowFill, v => ov.ShowFill = v,
            "Inward-fading fill behind the icon (same effect as party role icon backgrounds)");
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowFill);
        changed |= DrawColorEdit($"##{idSuffix}fc", ov.FillColor, v => ov.FillColor = v, ColorPickerFlags);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fill color");
        ImGui.EndDisabled();
        ImGui.SameLine();

        changed |= DrawToggle($"○##{idSuffix}circ", () => ov.ClipToCircle, v => ov.ClipToCircle = v,
            "Clips the icon to a circle so square textures fit neatly inside the border ring\n" +
            "(built-in rounded rendering — no extra cost).");
        ImGui.SameLine();

        float mul = ov.SizeMultiplier;
        ImGui.SetNextItemWidth(58f);
        if (ImGui.DragFloat($"##{idSuffix}mul", ref mul, 0.05f, 0.5f, 3.0f, "%.2fx"))
        { ov.SizeMultiplier = Math.Clamp(mul, 0.5f, 3.0f); changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Per-icon size multiplier on top of the global 1.5× padding compensation. 1.0 =\n" +
                              "same apparent size as a party role icon; drag right for icons with heavy padding,\n" +
                              "left for icons that look oversized.");

        return changed;
    }

    private bool DrawPlayersTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Players")) return false;
        bool changed = DrawEnableAndColor("players", "Players", () => cfg.ShowPlayers, v => cfg.ShowPlayers = v,
            () => cfg.PlayerColor, v => cfg.PlayerColor = v);

        BeginIndentedDisabled(cfg.ShowPlayers);

        changed |= DrawSizeSliders(
            () => cfg.PartyRoleIconMinSize, v => cfg.PartyRoleIconMinSize = v,
            () => cfg.PartyRoleIconMaxSize, v => cfg.PartyRoleIconMaxSize = v, 50, 60, "pr", tooltip:
            "Controls the size of every player marker — hollow ring, solid friend dot, and\n" +
            "party role icon below — together.");

        ImGui.Spacing();

        changed |= DrawToggle("Solid dot for friends##sfr", () => cfg.SolidFriendDots, v => cfg.SolidFriendDots = v,
            "Friends render as a solid dot instead of a hollow ring — overridden by party\n" +
            "role icons and named overrides below.");

        changed |= DrawToggle("Show job icon for party members##pri", () => cfg.ShowPartyRoleIcons, v => cfg.ShowPartyRoleIcons = v,
            "Party members show their class/job icon on a role-colored dot (Tank=blue,\n" +
            "Healer=green, DPS=red), taking priority over the friend dot and named\n" +
            "overrides (see 'duty/PvP only' below). Shares the size slider above.");

        BeginIndentedDisabled(cfg.ShowPartyRoleIcons);
        changed |= DrawToggle("Only in duty / PvP##pridonly", () => cfg.PartyRoleIconsOnlyInDuty, v => cfg.PartyRoleIconsOnlyInDuty = v,
            "Limits the job icon above to duty content and PvP, where party role actually\n" +
            "matters. Elsewhere, party members fall through to their named override, then\n" +
            "the friend/hollow dot below. Off = always show for any party member.");
        EndIndentedDisabled();

        // ── Named player icon overrides ───────────────────────────────────────
        DrawSectionBreak();

        ImGui.TextDisabled("Named player overrides");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Replace specific players' markers (exact, case-insensitive name) with a custom\n" +
                              "icon — browse IDs with /xldata icons. Party role icons still win priority while\n" +
                              "shown (see 'Only in duty / PvP' above); otherwise your override wins.\n" +
                              "B = border ring.  F = inward-fading fill.");

        if (cfg.PlayerIconOverrides.Count == 0)
            ImGui.TextDisabled("  (no overrides — add one below)");

        int removeAt = -1;
        for (int i = 0; i < cfg.PlayerIconOverrides.Count; i++)
        {
            ImGui.PushID(i);
            if (ImGui.Button("X##rmov")) removeAt = i;
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove this override");
            ImGui.SameLine();
            changed |= DrawOverrideRow(cfg.PlayerIconOverrides[i], "ov", 110f);
            ImGui.PopID();
        }

        if (removeAt >= 0)
        { cfg.PlayerIconOverrides.RemoveAt(removeAt); changed = true; }

        // ── Add new override ──────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.TextDisabled("Add override:");
        ImGui.SameLine();

        DrawOverrideRow(_newOverride, "newov", 120f);
        ImGui.SameLine();

        bool canAdd = !string.IsNullOrWhiteSpace(_newOverride.PlayerName) && _newOverride.IconBaseId > 0;
        ImGui.BeginDisabled(!canAdd);
        if (ImGui.Button("Add##addov"))
        {
            _newOverride.PlayerName = _newOverride.PlayerName.Trim();
            cfg.PlayerIconOverrides.Add(_newOverride);
            // Carry over border/fill/clip/multiplier; reset name/icon for the next entry
            _newOverride = new PlayerIconOverride
            {
                ShowBorder     = _newOverride.ShowBorder,
                BorderColor    = _newOverride.BorderColor,
                ShowFill       = _newOverride.ShowFill,
                FillColor      = _newOverride.FillColor,
                ClipToCircle   = _newOverride.ClipToCircle,
                SizeMultiplier = _newOverride.SizeMultiplier,
            };
            changed = true;
        }
        ImGui.EndDisabled();
        if (!canAdd && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Enter a player name and a non-zero icon ID to enable");

        EndIndentedDisabled(); // !cfg.ShowPlayers

        ImGui.EndTabItem();
        return changed;
    }

    // ── Combat tab ───────────────────────────────────────────────────────────

    private static bool DrawCombatTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Combat")) return false;
        bool changed = DrawEnableAndColor("enemies", "Enemies", () => cfg.ShowEnemies, v => cfg.ShowEnemies = v,
            () => cfg.EnemyColor, v => cfg.EnemyColor = v);

        BeginIndentedDisabled(cfg.ShowEnemies);
        changed |= DrawToggle("Only show enemies I'm engaged with##eng", () => cfg.EnemiesOnlyIfEngaged, v => cfg.EnemiesOnlyIfEngaged = v,
            "Only shows hostiles actually in combat with you or your party, instead of every\n" +
            "hostile in range — handy for big pulls and hunt trains.");

        changed |= DrawSizeSliders(
            () => cfg.EnemyMinSize, v => cfg.EnemyMinSize = v, () => cfg.EnemyMaxSize, v => cfg.EnemyMaxSize = v,
            50, 60, "en", tooltip: "Controls the size of every enemy marker.");

        EndIndentedDisabled();

        DrawSectionBreak();

        changed |= DrawEnableAndColor("lbglow", "Limit break glow (bar 1 color)",
            () => cfg.ShowLimitBreakGlow, v => cfg.ShowLimitBreakGlow = v,
            () => cfg.LimitBreakGlowColor, v => cfg.LimitBreakGlowColor = v,
            "A glowing border creeps in from each end as limit break charges — one layer\n" +
            "per bar, stacked as each fills. Full layers lit = bars charged, at a glance.");

        BeginIndentedDisabled(cfg.ShowLimitBreakGlow);

        changed |= DrawColorEdit("Bar 2 color##lbc2", cfg.LimitBreakGlowColor2, v => cfg.LimitBreakGlowColor2 = v);
        changed |= DrawColorEdit("Bar 3 color##lbc3", cfg.LimitBreakGlowColor3, v => cfg.LimitBreakGlowColor3 = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Each layer waves at its own speed so bars 2/3 never ripple in lockstep with\n" +
                              "bar 1 or each other — meant to look chaotic at a full break.");

        EndIndentedDisabled();

        DrawSectionBreak();

        changed |= DrawToggle("Target Health Bar", () => cfg.ShowTargetBar, v => cfg.ShowTargetBar = v,
            "Name + HP readout for your target, docked beneath the compass. Fill reuses\n" +
            "the compass's own marker colors (player/enemy/NPC), swapping to the party role\n" +
            "color when your target is a party member and role icons are showing;\n" +
            "background/border/text reuse the compass's own General-tab colors so the two\n" +
            "always match.");

        BeginIndentedDisabled(cfg.ShowTargetBar);

        changed |= DrawSliderFloat("Width  (fraction of compass)##tbwf", 0.3f, 1.0f,
            () => cfg.TargetBarWidthFraction, v => cfg.TargetBarWidthFraction = v);
        changed |= DrawSliderInt("Bar thickness##tbh", 6, 30, () => (int)cfg.TargetBarHeight, v => cfg.TargetBarHeight = v);
        changed |= DrawSliderFloat("Name font scale##tbfs", 0.5f, 2.5f,
            () => cfg.TargetBarFontScale, v => cfg.TargetBarFontScale = v);

        changed |= DrawToggle("Show target level##tblvl", () => cfg.ShowTargetLevel, v => cfg.ShowTargetLevel = v);

        changed |= DrawToggle("Show shield overlay##tbshd", () => cfg.ShowTargetBarShield, v => cfg.ShowTargetBarShield = v,
            "A light sheen over the shielded portion of the bar when your target has an\n" +
            "active damage shield (Sacred Soil, etc.).");

        changed |= DrawToggle("Show name ribbons##tbrib", () => cfg.ShowTargetBarRibbons, v => cfg.ShowTargetBarRibbons = v,
            "Two glowing ribbons (reusing the Limit Break glow technique) flying outward\n" +
            "from the name's flanking ornaments, colored to match the border above.");

        ImGui.Spacing();
        ImGui.BeginDisabled(!cfg.ShowTargetBarShield);
        changed |= DrawColorEdit("Shield overlay##tbsc", cfg.TargetBarShieldColor, v => cfg.TargetBarShieldColor = v, ColorPickerFlags);
        ImGui.EndDisabled();

        EndIndentedDisabled();

        DrawSectionBreak();

        changed |= DrawToggle("Target status icons", () => cfg.ShowTargetStatuses, v => cfg.ShowTargetStatuses = v,
            "Buff/debuff icons for your target, in a row beneath its name. Native game\n" +
            "order — no sorting, no filtering; just whatever the vanilla frame would show.");

        BeginIndentedDisabled(cfg.ShowTargetStatuses);
        changed |= DrawSliderInt("Icon size##tssize", 12, 40, () => (int)cfg.TargetStatusIconSize, v => cfg.TargetStatusIconSize = v);
        changed |= DrawSliderInt("Max icons##tsmax", 3, 20, () => cfg.TargetStatusMaxIcons, v => cfg.TargetStatusMaxIcons = v);
        changed |= DrawToggle("Include Moodles##tsmoodles", () => cfg.ShowMoodlesStatuses, v => cfg.ShowMoodlesStatuses = v,
            "Adds the target's active Moodles into the row above, sharing its size and max-icons\n" +
            "limit. Does nothing if the Moodles plugin isn't installed. No duration shown — Moodles\n" +
            "only reports each status's total length, not time left.");
        changed |= DrawToggle("Include Loci##tsloci", () => cfg.ShowLociStatuses, v => cfg.ShowLociStatuses = v,
            "Adds the target's active Loci statuses into the row above, sharing its size and\n" +
            "max-icons limit. Does nothing if the Loci plugin isn't installed. No duration shown —\n" +
            "same reason as Moodles.");
        EndIndentedDisabled();

        DrawSectionBreak();

        changed |= DrawToggle("Player status bar", () => cfg.ShowPlayerStatusBar, v => cfg.ShowPlayerStatusBar = v,
            "A separate, freely-positioned row of YOUR OWN active statuses (native + Moodles/Loci),\n" +
            "so you can see them without having to target yourself. Independent size, icon cap,\n" +
            "and Moodles/Loci toggles from the target row above.");

        BeginIndentedDisabled(cfg.ShowPlayerStatusBar);
        changed |= DrawSliderInt("Icon size##pssize", 12, 40, () => (int)cfg.PlayerStatusIconSize, v => cfg.PlayerStatusIconSize = v);
        changed |= DrawSliderInt("Max icons##psmax", 3, 20, () => cfg.PlayerStatusMaxIcons, v => cfg.PlayerStatusMaxIcons = v);
        changed |= DrawToggle("Include Moodles##psmoodles", () => cfg.PlayerStatusShowMoodles, v => cfg.PlayerStatusShowMoodles = v,
            "Adds your active Moodles into the row above. Does nothing if the Moodles plugin isn't installed.");
        changed |= DrawToggle("Include Loci##psloci", () => cfg.PlayerStatusShowLoci, v => cfg.PlayerStatusShowLoci = v,
            "Adds your active Loci statuses into the row above. Does nothing if the Loci plugin isn't installed.");

        // Row width varies with how many icons are currently active, so this range is an estimate
        // (max icons at roughly their on-screen spacing) rather than an exact on-screen guarantee
        // like the compass's own X/Y sliders get — same idea, just can't be pixel-exact here
        var   io       = ImGui.GetIO();
        float psWEst   = cfg.PlayerStatusMaxIcons * (cfg.PlayerStatusIconSize * 1.25f);
        int   psXRange = (int)MathF.Max(0f, (io.DisplaySize.X - psWEst) * 0.5f);
        changed |= DrawSliderInt("X Offset (from center)##psxo", -psXRange, psXRange, () => (int)cfg.PlayerStatusXOffset, v => cfg.PlayerStatusXOffset = v);

        float psHEst = cfg.PlayerStatusIconSize * 1.6f;   // icon + duration label, roughly
        int   psYMax = (int)MathF.Max(0f, io.DisplaySize.Y - psHEst);
        changed |= DrawSliderInt("Y Offset (from top)##psyo", 0, psYMax, () => (int)cfg.PlayerStatusYOffset, v => cfg.PlayerStatusYOffset = v);
        EndIndentedDisabled();

        DrawSectionBreak();

        changed |= DrawToggle("Target-of-target", () => cfg.ShowTargetOfTargetBar, v => cfg.ShowTargetOfTargetBar = v,
            "Shows who/what YOUR target has itself targeted — FF14's target-of-target,\n" +
            "restyled. Hidden when that's nobody, or your target itself.");

        BeginIndentedDisabled(cfg.ShowTargetOfTargetBar);

        changed |= DrawToggle("Highlight if targeting me##aggro", () => cfg.HighlightIfTargetingMe, v => cfg.HighlightIfTargetingMe = v,
            "When your target's target is YOU, this tier lights up in a warning color\n" +
            "and shows your own HP — so aggro is hard to miss out of the corner of your eye.");

        ImGui.BeginDisabled(!cfg.HighlightIfTargetingMe);
        changed |= DrawColorEdit("Warning color##aggroc", cfg.AggroWarningColor, v => cfg.AggroWarningColor = v, ColorPickerFlags);
        ImGui.EndDisabled();

        EndIndentedDisabled();

        ImGui.EndTabItem();
        return changed;
    }

    // ── NPCs tab ─────────────────────────────────────────────────────────────

    private static bool DrawNpcsTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("NPCs")) return false;
        bool changed = DrawEnableAndColor("npcs", "NPCs", () => cfg.ShowNpcs, v => cfg.ShowNpcs = v,
            () => cfg.NpcColor, v => cfg.NpcColor = v);

        BeginIndentedDisabled(cfg.ShowNpcs);
        changed |= DrawToggle("Hide non-targetable \"ghost\" NPCs##tgt", () => cfg.NpcsOnlyIfTargetable, v => cfg.NpcsOnlyIfTargetable = v,
            "Filters out placeholder NPCs the game tracks even when nothing's there\n" +
            "(e.g. an empty chocobo stable slot). Recommended to leave on.");

        changed |= DrawToggle("Show quest marker icons##qicon", () => cfg.ShowNpcQuestIcons, v => cfg.ShowNpcQuestIcons = v,
            "NPCs with an active quest marker (MSQ, side quest \"!\", in-progress \"?\", etc.)\n" +
            "show that exact icon instead of a plain dot.");

        changed |= DrawToggle("Show Mender icon##micon", () => cfg.ShowMenderIcons, v => cfg.ShowMenderIcons = v,
            "Icon for Mender NPCs (gear repair vendors), matched in English\n" +
            "regardless of client language. Shares the size sliders below.");

        changed |= DrawToggle("Show Shop/Trader icon##sicon", () => cfg.ShowShopIcons, v => cfg.ShowShopIcons = v,
            "Icon for Shop/Trader NPCs (\"Merchant\", \"Vendor\", etc), matched the same\n" +
            "way as Mender above and sharing its size sliders.");

        changed |= DrawToggle("Show Fast Travel icons##fticon", () => cfg.ShowFastTravelIcons, v => cfg.ShowFastTravelIcons = v,
            "Icon for ferry skippers, airship/other ticketers, and Chocobo\n" +
            "Keeps/Falcon Porters (different icon per type, one toggle). Matched\n" +
            "the same way as Mender above.");

        changed |= DrawSizeSliders(
            () => cfg.NpcQuestIconMinSize, v => cfg.NpcQuestIconMinSize = v,
            () => cfg.NpcQuestIconMaxSize, v => cfg.NpcQuestIconMaxSize = v, 50, 60, "q", tooltip:
            "Controls the size of every NPC marker — all icons above and the plain dot\n" +
            "shown when none apply.");

        EndIndentedDisabled();

        ImGui.EndTabItem();
        return changed;
    }

    // ── Gathering tab ────────────────────────────────────────────────────────

    private static bool DrawGatheringTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Gathering")) return false;
        bool changed = DrawEnableAndColor("gath", "Gathering Nodes", () => cfg.ShowGatheringNodes, v => cfg.ShowGatheringNodes = v,
            () => cfg.GatheringColor, v => cfg.GatheringColor = v);

        BeginIndentedDisabled(cfg.ShowGatheringNodes);
        changed |= DrawToggle("Hide non-targetable \"ghost\" nodes##gtgt", () => cfg.GatheringOnlyIfTargetable, v => cfg.GatheringOnlyIfTargetable = v,
            "Filters out depleted or not-yet-spawned nodes the game still tracks even\n" +
            "when nothing's interactable there.");

        changed |= DrawToggle("Show Mining/Botany icons##gicon", () => cfg.ShowGatheringIcons, v => cfg.ShowGatheringIcons = v,
            "Shows the node's Mining/Quarrying/Logging/Botany icon instead of a plain dot.");

        BeginIndentedDisabled(cfg.ShowGatheringIcons);
        changed |= DrawSizeSliders(
            () => cfg.GatheringIconMinSize, v => cfg.GatheringIconMinSize = v,
            () => cfg.GatheringIconMaxSize, v => cfg.GatheringIconMaxSize = v, 50, 60, "g");
        EndIndentedDisabled();

        EndIndentedDisabled();

        ImGui.EndTabItem();
        return changed;
    }

    // ── Treasure tab ─────────────────────────────────────────────────────────

    private static bool DrawTreasureTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Treasure")) return false;
        bool changed = DrawEnableAndColor("tres", "Treasure", () => cfg.ShowTreasure, v => cfg.ShowTreasure = v,
            () => cfg.TreasureColor, v => cfg.TreasureColor = v);

        BeginIndentedDisabled(cfg.ShowTreasure);

        changed |= DrawSizeSliders(
            () => cfg.TreasureMinSize, v => cfg.TreasureMinSize = v, () => cfg.TreasureMaxSize, v => cfg.TreasureMaxSize = v,
            50, 60, "tr", tooltip:
            "Controls the size of every treasure marker — the chest icon below and the\n" +
            "plain dot fallback.");

        ImGui.Spacing();

        changed |= DrawToggle("Show chest icon##tricon", () => cfg.ShowTreasureIcons, v => cfg.ShowTreasureIcons = v,
            "No game-data sheet exposes a chest's visual type from its BaseId, so every\n" +
            "coffer shows the same icon (below).");

        BeginIndentedDisabled(cfg.ShowTreasureIcons);

        int trIconId = cfg.TreasureIconId;
        ImGui.SetNextItemWidth(90f);
        if (ImGui.InputInt("Icon ID##triconid", ref trIconId, 0, 0))
        { cfg.TreasureIconId = Math.Max(0, trIconId); changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Game icon ID for every treasure coffer — 60354/60355/60356 are known\n" +
                              "chest-icon variants.");

        EndIndentedDisabled();

        EndIndentedDisabled();

        ImGui.EndTabItem();
        return changed;
    }

    // ── Aetherytes tab ───────────────────────────────────────────────────────

    private static bool DrawAetherytesTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Aetherytes")) return false;
        bool changed = DrawEnableAndColor("aeth", "Aetherytes", () => cfg.ShowAetherytes, v => cfg.ShowAetherytes = v,
            () => cfg.AetheryteColor, v => cfg.AetheryteColor = v);

        BeginIndentedDisabled(cfg.ShowAetherytes);

        changed |= DrawToggle("Show Aethernet shards##aethshards", () => cfg.ShowAethernetShards, v => cfg.ShowAethernetShards = v,
            "Aethernet shards are the smaller waypoints in housing wards, the Firmament,\n" +
            "etc — as opposed to a city's one main aetheryte. Off shows only main ones.");

        changed |= DrawToggle("Show aetheryte icon##aicon", () => cfg.ShowAetheryteIcons, v => cfg.ShowAetheryteIcons = v,
            "Falls back to the colour dot only if an icon doesn't resolve.");

        changed |= DrawSizeSliders(
            () => cfg.AetheryteIconMinSize, v => cfg.AetheryteIconMinSize = v,
            () => cfg.AetheryteIconMaxSize, v => cfg.AetheryteIconMaxSize = v, 50, 60, "a", tooltip:
            "Controls the size of every aetheryte marker — the icon above and the plain\n" +
            "dot shown when icons are off or fail to load.");

        string shardName = cfg.AethernetShardName;
        if (ImGui.InputText("Aethernet shard name##shardname", ref shardName, 64))
        { cfg.AethernetShardName = shardName; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A word in every Aethernet shard's name, in your game's language — matched\n" +
                              "as a substring, so \"Aethernet\" catches all shard names at once. Non-matching\n" +
                              "aetherytes are assumed to be main ones.");

        EndIndentedDisabled();

        ImGui.EndTabItem();
        return changed;
    }

    // ── FATEs tab ────────────────────────────────────────────────────────────

    private static bool DrawFatesTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("FATEs")) return false;

        ImGui.TextDisabled("Independent of every other tab's toggles.");
        ImGui.Spacing();

        bool changed = DrawEnableAndColor("fates", "Show FATEs", () => cfg.ShowFates, v => cfg.ShowFates = v,
            () => cfg.FateColor, v => cfg.FateColor = v,
            "Shows active/about-to-start FATEs with their icon, sorted in the same\n" +
            "pass as every marker (closer paints on top). Range = General tab's range ×\n" +
            "the multiplier below. Works even with everything else off.");

        BeginIndentedDisabled(cfg.ShowFates);

        changed |= DrawSliderFloat("Distance multiplier##fatemul", 0.5f, 5.0f,
            () => cfg.FateDistanceMultiplier, v => cfg.FateDistanceMultiplier = MathF.Max(0.5f, v), "%.1f×");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("FATEs are detected up to (General tab's range × this) yalms — at 2.5× with\n" +
                              "the default 100y range, that's 250y: zone-wide, long before you're near them.");
        ImGui.TextDisabled($"Effective FATE range: {cfg.MaxMarkerDistance * cfg.FateDistanceMultiplier:F0} yalms");

        changed |= DrawSizeSliders(
            () => cfg.FateIconMinSize, v => cfg.FateIconMinSize = v, () => cfg.FateIconMaxSize, v => cfg.FateIconMaxSize = v,
            50, 64, "fate", "Min icon size (far away)", "Max icon size (close up)");

        EndIndentedDisabled();

        ImGui.EndTabItem();
        return changed;
    }

    // Compact colour-edit flags: show only the small swatch, not text inputs
    private static readonly ImGuiColorEditFlags ColorPickerFlags =
        ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar;

    // ── Shared tab building blocks ────────────────────────────────────────────

    // Spacing/Separator/Spacing — the gap between one setting group and the next, throughout
    private static void DrawSectionBreak()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    // Indent+BeginDisabled / EndDisabled+Unindent — wraps a setting group that only makes sense
    // with its own enable toggle on, throughout
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

    // Checkbox + optional hover tooltip in one call — the shape behind most toggles below
    private static bool DrawToggle(string label, Func<bool> get, Action<bool> set, string? tooltip = null)
    {
        bool v       = get();
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

    // Bound to a getter/setter pair — skips the temp-variable dance a property (no ref) would need
    private static bool DrawSliderInt(string label, int lo, int hi, Func<int> get, Action<int> set)
    {
        int v = get();
        if (ImGui.SliderInt(label, ref v, lo, hi)) { set(v); return true; }
        return false;
    }

    private static bool DrawSliderFloat(string label, float lo, float hi, Func<float> get, Action<float> set, string? fmt = null)
    {
        float v = get();
        bool changed = fmt is null ? ImGui.SliderFloat(label, ref v, lo, hi) : ImGui.SliderFloat(label, ref v, lo, hi, fmt);
        if (changed) set(v);
        return changed;
    }

    private static bool DrawColorEdit(string label, Vector4 val, Action<Vector4> set, ImGuiColorEditFlags flags = ImGuiColorEditFlags.None)
    {
        if (ImGui.ColorEdit4(label, ref val, flags)) { set(val); return true; }
        return false;
    }
}
