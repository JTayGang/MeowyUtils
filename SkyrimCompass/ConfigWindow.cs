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

        bool enabled = cfg.Enabled;
        if (ImGui.Checkbox("##enabled", ref enabled)) { cfg.Enabled = enabled; changed = true; }
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

        // Y/X bounds track live display size so the bar stays fully on-screen at any resolution.
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

        bool sh = cfg.ShowHeadingText;
        if (DrawToggle("Show numeric heading below bar", ref sh)) { cfg.ShowHeadingText = sh; changed = true; }

        bool hdc = cfg.HideDuringCutscenes;
        if (DrawToggle("Hide during cutscenes", ref hdc,
            "Skips drawing during story cutscenes, skippable cinematics, and group pose —\n" +
            "there's nothing to navigate to while the camera isn't yours anyway."))
        { cfg.HideDuringCutscenes = hdc; changed = true; }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool ucd = cfg.UseCameraDirection;
        if (DrawToggle("Use camera direction instead of character facing", ref ucd,
            "On: follows where your CAMERA looks (free camera, screenshots, sightseeing).\n" +
            "Off: follows your CHARACTER's facing, like Skyrim's compass (recommended for combat)."))
        { cfg.UseCameraDirection = ucd; changed = true; }

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.UseCameraDirection);
        bool ucp = cfg.UseCameraPosition;
        if (DrawToggle("Also use camera location for distances##ucp", ref ucp,
            "Measures bearings/distances from your CAMERA's position instead of your character's.\n" +
            "Useful zoomed way out or with a camera offset mod. Needs 'Use camera direction' above."))
        { cfg.UseCameraPosition = ucp; changed = true; }
        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.TextDisabled("Rotation Offset  (set to 180 if N and S are swapped)");
        changed |= DrawSliderInt("##rotoff", -180, 180, () => (int)cfg.RotationOffset, v => cfg.RotationOffset = v);

        ImGui.EndTabItem();
        return changed;
    }

    // ── Color theme data (consumed by DrawGeneralTab below) ────────────────────

    private sealed class ColorTheme
    {
        public string  Name          = "";
        public Vector4 Background, Border, Cardinal, Intercardinal, Tick;
        public Vector4 Player, Enemy, Npc, Gathering, Treasure, Aetheryte, Fate;
    }

    // "Original" mirrors Configuration's defaults exactly — picking it restores the out-of-box look.
    private static readonly ColorTheme[] ColorThemes =
    {
        new ColorTheme
        {
            Name          = "Original",
            Background    = new(0.05f, 0.04f, 0.03f, 0.82f),
            Border        = new(0.48f, 0.42f, 0.27f, 0.92f),
            Cardinal      = new(1.00f, 0.97f, 0.88f, 1.00f),
            Intercardinal = new(0.72f, 0.70f, 0.65f, 0.88f),
            Tick          = new(0.58f, 0.56f, 0.52f, 0.72f),
            Player        = new(0.40f, 0.65f, 1.00f, 0.92f),
            Enemy         = new(1.00f, 0.25f, 0.25f, 0.92f),
            Npc           = new(0.95f, 0.88f, 0.35f, 0.92f),
            Gathering     = new(0.30f, 0.92f, 0.40f, 0.92f),
            Treasure      = new(1.00f, 0.80f, 0.15f, 0.95f),
            Aetheryte     = new(0.55f, 0.85f, 0.95f, 0.92f),
            Fate          = new(0.82f, 0.35f, 0.95f, 0.95f),
        },
        new ColorTheme
        {
            Name          = "Frostfall",
            Background    = new(0.03f, 0.06f, 0.10f, 0.84f),
            Border        = new(0.55f, 0.75f, 0.88f, 0.92f),
            Cardinal      = new(0.92f, 0.97f, 1.00f, 1.00f),
            Intercardinal = new(0.68f, 0.82f, 0.90f, 0.88f),
            Tick          = new(0.55f, 0.68f, 0.78f, 0.72f),
            Player        = new(0.50f, 0.85f, 1.00f, 0.92f),
            Enemy         = new(1.00f, 0.35f, 0.40f, 0.92f),
            Npc           = new(0.85f, 0.95f, 1.00f, 0.92f),
            Gathering     = new(0.40f, 0.95f, 0.85f, 0.92f),
            Treasure      = new(0.95f, 0.92f, 0.65f, 0.95f),
            Aetheryte     = new(0.60f, 0.90f, 1.00f, 0.92f),
            Fate          = new(0.75f, 0.55f, 1.00f, 0.95f),
        },
        new ColorTheme
        {
            Name          = "Inferno",
            Background    = new(0.08f, 0.03f, 0.02f, 0.85f),
            Border        = new(0.75f, 0.32f, 0.10f, 0.92f),
            Cardinal      = new(1.00f, 0.88f, 0.60f, 1.00f),
            Intercardinal = new(0.88f, 0.58f, 0.32f, 0.88f),
            Tick          = new(0.65f, 0.38f, 0.22f, 0.72f),
            Player        = new(0.45f, 0.75f, 1.00f, 0.92f),
            Enemy         = new(1.00f, 0.18f, 0.10f, 0.95f),
            Npc           = new(1.00f, 0.78f, 0.30f, 0.92f),
            Gathering     = new(0.55f, 0.90f, 0.35f, 0.92f),
            Treasure      = new(1.00f, 0.70f, 0.10f, 0.95f),
            Aetheryte     = new(0.95f, 0.55f, 0.85f, 0.92f),
            Fate          = new(1.00f, 0.40f, 0.85f, 0.95f),
        },
        new ColorTheme
        {
            Name          = "Verdant",
            Background    = new(0.03f, 0.06f, 0.03f, 0.84f),
            Border        = new(0.38f, 0.58f, 0.32f, 0.92f),
            Cardinal      = new(0.92f, 1.00f, 0.85f, 1.00f),
            Intercardinal = new(0.68f, 0.82f, 0.60f, 0.88f),
            Tick          = new(0.50f, 0.62f, 0.45f, 0.72f),
            Player        = new(0.45f, 0.80f, 1.00f, 0.92f),
            Enemy         = new(1.00f, 0.30f, 0.25f, 0.92f),
            Npc           = new(0.92f, 0.85f, 0.40f, 0.92f),
            Gathering     = new(0.45f, 1.00f, 0.50f, 0.95f),
            Treasure      = new(1.00f, 0.85f, 0.25f, 0.95f),
            Aetheryte     = new(0.55f, 0.92f, 0.80f, 0.92f),
            Fate          = new(0.78f, 0.95f, 0.35f, 0.95f),
        },
        new ColorTheme
        {
            Name          = "Void",
            Background    = new(0.04f, 0.02f, 0.08f, 0.85f),
            Border        = new(0.58f, 0.38f, 0.78f, 0.92f),
            Cardinal      = new(0.92f, 0.85f, 1.00f, 1.00f),
            Intercardinal = new(0.72f, 0.62f, 0.85f, 0.88f),
            Tick          = new(0.55f, 0.48f, 0.65f, 0.72f),
            Player        = new(0.55f, 0.72f, 1.00f, 0.92f),
            Enemy         = new(1.00f, 0.30f, 0.55f, 0.92f),
            Npc           = new(0.88f, 0.78f, 1.00f, 0.92f),
            Gathering     = new(0.55f, 0.95f, 0.65f, 0.92f),
            Treasure      = new(1.00f, 0.75f, 0.95f, 0.95f),
            Aetheryte     = new(0.68f, 0.55f, 1.00f, 0.92f),
            Fate          = new(0.85f, 0.40f, 1.00f, 0.95f),
        },
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

    // ── General tab (bar colors, theme presets, shared detection range/fade — cross-cutting, doesn't belong to one tab) ──

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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

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

    // One editable override row (name/icon/border/fill/clip/multiplier); shared by existing entries and the "add new" form.
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

        bool border = ov.ShowBorder;
        if (ImGui.Checkbox($"B##{idSuffix}b", ref border)) { ov.ShowBorder = border; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Draw a solid outer ring around the icon");
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowBorder);
        changed |= DrawColorEdit($"##{idSuffix}bc", ov.BorderColor, v => ov.BorderColor = v, ColorPickerFlags);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Border ring color");
        ImGui.EndDisabled();
        ImGui.SameLine();

        bool fill = ov.ShowFill;
        if (ImGui.Checkbox($"F##{idSuffix}f", ref fill)) { ov.ShowFill = fill; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Inward-fading fill behind the icon (same effect as party role icon backgrounds)");
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowFill);
        changed |= DrawColorEdit($"##{idSuffix}fc", ov.FillColor, v => ov.FillColor = v, ColorPickerFlags);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fill color");
        ImGui.EndDisabled();
        ImGui.SameLine();

        bool clip = ov.ClipToCircle;
        if (ImGui.Checkbox($"○##{idSuffix}circ", ref clip)) { ov.ClipToCircle = clip; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clips the icon to a circle so square textures fit neatly inside the border ring\n" +
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
        bool    b = cfg.ShowPlayers;
        Vector4 c = cfg.PlayerColor;
        bool changed = DrawEnableAndColor("players", "Players", ref b, ref c);
        cfg.ShowPlayers = b; cfg.PlayerColor = c;

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowPlayers);

        float prMin = cfg.PartyRoleIconMinSize, prMax = cfg.PartyRoleIconMaxSize;
        if (DrawSizeSliders(ref prMin, ref prMax, 50, 60, "pr", tooltip:
            "Controls the size of every player marker — hollow ring, solid friend dot, and\n" +
            "party role icon below — together."))
        { cfg.PartyRoleIconMinSize = prMin; cfg.PartyRoleIconMaxSize = prMax; changed = true; }

        ImGui.Spacing();

        bool sfr = cfg.SolidFriendDots;
        if (DrawToggle("Solid dot for friends##sfr", ref sfr,
            "Friends render as a solid dot instead of a hollow ring — overridden by party\n" +
            "role icons and named overrides below."))
        { cfg.SolidFriendDots = sfr; changed = true; }

        bool pri = cfg.ShowPartyRoleIcons;
        if (DrawToggle("Show job icon for party members##pri", ref pri,
            "Party members show their class/job icon on a role-colored dot (Tank=blue,\n" +
            "Healer=green, DPS=red), taking priority over the friend dot and named\n" +
            "overrides (see 'duty/PvP only' below). Shares the size slider above."))
        { cfg.ShowPartyRoleIcons = pri; changed = true; }

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowPartyRoleIcons);
        bool priDuty = cfg.PartyRoleIconsOnlyInDuty;
        if (DrawToggle("Only in duty / PvP##pridonly", ref priDuty,
            "Limits the job icon above to duty content and PvP, where party role actually\n" +
            "matters. Elsewhere, party members fall through to their named override, then\n" +
            "the friend/hollow dot below. Off = always show for any party member."))
        { cfg.PartyRoleIconsOnlyInDuty = priDuty; changed = true; }
        ImGui.EndDisabled();
        ImGui.Unindent();

        // ── Named player icon overrides ───────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

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
            // Carry over border/fill/clip/multiplier; reset name/icon for the next entry.
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

        ImGui.EndDisabled(); // !cfg.ShowPlayers
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // ── Combat tab ───────────────────────────────────────────────────────────

    private static bool DrawCombatTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Combat")) return false;
        bool    b = cfg.ShowEnemies;
        Vector4 c = cfg.EnemyColor;
        bool changed = DrawEnableAndColor("enemies", "Enemies", ref b, ref c);
        cfg.ShowEnemies = b; cfg.EnemyColor = c;

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowEnemies);
        bool eng = cfg.EnemiesOnlyIfEngaged;
        if (DrawToggle("Only show enemies I'm engaged with##eng", ref eng,
            "Only shows hostiles targeting you or that you're targeting, instead of every\n" +
            "hostile in range — handy for big pulls and hunt trains."))
        { cfg.EnemiesOnlyIfEngaged = eng; changed = true; }

        float enMin = cfg.EnemyMinSize, enMax = cfg.EnemyMaxSize;
        if (DrawSizeSliders(ref enMin, ref enMax, 50, 60, "en", tooltip: "Controls the size of every enemy marker."))
        { cfg.EnemyMinSize = enMin; cfg.EnemyMaxSize = enMax; changed = true; }

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool    lbB = cfg.ShowLimitBreakGlow;
        Vector4 lbC = cfg.LimitBreakGlowColor;
        if (DrawEnableAndColor("lbglow", "Limit break glow (bar 1 color)", ref lbB, ref lbC,
            "A glowing border creeps in from each end as limit break charges — one layer\n" +
            "per bar, stacked as each fills. Full layers lit = bars charged, at a glance."))
        { cfg.ShowLimitBreakGlow = lbB; cfg.LimitBreakGlowColor = lbC; changed = true; }

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowLimitBreakGlow);

        changed |= DrawColorEdit("Bar 2 color##lbc2", cfg.LimitBreakGlowColor2, v => cfg.LimitBreakGlowColor2 = v);
        changed |= DrawColorEdit("Bar 3 color##lbc3", cfg.LimitBreakGlowColor3, v => cfg.LimitBreakGlowColor3 = v);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Each layer waves at its own speed so bars 2/3 never ripple in lockstep with\n" +
                              "bar 1 or each other — meant to look chaotic at a full break.");

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool tbB = cfg.ShowTargetBar;
        if (DrawToggle("Target Health Bar", ref tbB,
            "Name + HP readout for your target, docked beneath the compass. Fill follows\n" +
            "the hostile/friendly scheme below; background/border/text reuse the compass's\n" +
            "own General-tab colors so the two always match."))
        { cfg.ShowTargetBar = tbB; changed = true; }

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowTargetBar);

        changed |= DrawSliderFloat("Width  (fraction of compass)##tbwf", 0.3f, 1.0f,
            () => cfg.TargetBarWidthFraction, v => cfg.TargetBarWidthFraction = v);
        changed |= DrawSliderInt("Bar thickness##tbh", 6, 30, () => (int)cfg.TargetBarHeight, v => cfg.TargetBarHeight = v);
        changed |= DrawSliderFloat("Name font scale##tbfs", 0.5f, 2.5f,
            () => cfg.TargetBarFontScale, v => cfg.TargetBarFontScale = v);

        bool lvl = cfg.ShowTargetLevel;
        if (DrawToggle("Show target level##tblvl", ref lvl)) { cfg.ShowTargetLevel = lvl; changed = true; }

        bool shd = cfg.ShowTargetBarShield;
        if (DrawToggle("Show shield overlay##tbshd", ref shd,
            "A light sheen over the shielded portion of the bar when your target has an\n" +
            "active damage shield (Sacred Soil, etc.)."))
        { cfg.ShowTargetBarShield = shd; changed = true; }

        bool ribbons = cfg.ShowTargetBarRibbons;
        if (DrawToggle("Show name ribbons##tbrib", ref ribbons,
            "Two glowing ribbons (reusing the Limit Break glow technique) flying outward\n" +
            "from the name's flanking ornaments, colored to match the border above."))
        { cfg.ShowTargetBarRibbons = ribbons; changed = true; }

        ImGui.Spacing();
        changed |= DrawColorEdit("Hostile##tbhc", cfg.TargetBarHostileColor, v => cfg.TargetBarHostileColor = v, ColorPickerFlags);
        changed |= DrawColorEdit("Friendly##tbfc", cfg.TargetBarFriendlyColor, v => cfg.TargetBarFriendlyColor = v, ColorPickerFlags);

        ImGui.BeginDisabled(!cfg.ShowTargetBarShield);
        changed |= DrawColorEdit("Shield overlay##tbsc", cfg.TargetBarShieldColor, v => cfg.TargetBarShieldColor = v, ColorPickerFlags);
        ImGui.EndDisabled();

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool totB = cfg.ShowTargetOfTargetBar;
        if (DrawToggle("Target-of-target", ref totB,
            "Shows who/what YOUR target has itself targeted — FF14's target-of-target,\n" +
            "restyled. Hidden when that's nobody, or your target itself."))
        { cfg.ShowTargetOfTargetBar = totB; changed = true; }

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowTargetOfTargetBar);

        bool aggro = cfg.HighlightIfTargetingMe;
        if (DrawToggle("Highlight if targeting me##aggro", ref aggro,
            "When your target's target is YOU, this tier lights up in a warning color\n" +
            "and shows your own HP — so aggro is hard to miss out of the corner of your eye."))
        { cfg.HighlightIfTargetingMe = aggro; changed = true; }

        ImGui.BeginDisabled(!cfg.HighlightIfTargetingMe);
        changed |= DrawColorEdit("Warning color##aggroc", cfg.AggroWarningColor, v => cfg.AggroWarningColor = v, ColorPickerFlags);
        ImGui.EndDisabled();

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // ── NPCs tab ─────────────────────────────────────────────────────────────

    private static bool DrawNpcsTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("NPCs")) return false;
        bool    b = cfg.ShowNpcs;
        Vector4 c = cfg.NpcColor;
        bool changed = DrawEnableAndColor("npcs", "NPCs", ref b, ref c);
        cfg.ShowNpcs = b; cfg.NpcColor = c;

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowNpcs);
        bool tgt = cfg.NpcsOnlyIfTargetable;
        if (DrawToggle("Hide non-targetable \"ghost\" NPCs##tgt", ref tgt,
            "Filters out placeholder NPCs the game tracks even when nothing's there\n" +
            "(e.g. an empty chocobo stable slot). Recommended to leave on."))
        { cfg.NpcsOnlyIfTargetable = tgt; changed = true; }

        bool qIcon = cfg.ShowNpcQuestIcons;
        if (DrawToggle("Show quest marker icons##qicon", ref qIcon,
            "NPCs with an active quest marker (MSQ, side quest \"!\", in-progress \"?\", etc.)\n" +
            "show that exact icon instead of a plain dot."))
        { cfg.ShowNpcQuestIcons = qIcon; changed = true; }

        bool mIcon = cfg.ShowMenderIcons;
        if (DrawToggle("Show Mender icon##micon", ref mIcon,
            "Icon for Mender NPCs (gear repair vendors), matched in English\n" +
            "regardless of client language. Shares the size sliders below."))
        { cfg.ShowMenderIcons = mIcon; changed = true; }

        bool sIcon = cfg.ShowShopIcons;
        if (DrawToggle("Show Shop/Trader icon##sicon", ref sIcon,
            "Icon for Shop/Trader NPCs (\"Merchant\", \"Vendor\", etc), matched the same\n" +
            "way as Mender above and sharing its size sliders."))
        { cfg.ShowShopIcons = sIcon; changed = true; }

        bool ftIcon = cfg.ShowFastTravelIcons;
        if (DrawToggle("Show Fast Travel icons##fticon", ref ftIcon,
            "Icon for ferry skippers, airship/other ticketers, and Chocobo\n" +
            "Keeps/Falcon Porters (different icon per type, one toggle). Matched\n" +
            "the same way as Mender above."))
        { cfg.ShowFastTravelIcons = ftIcon; changed = true; }

        float qMin = cfg.NpcQuestIconMinSize, qMax = cfg.NpcQuestIconMaxSize;
        if (DrawSizeSliders(ref qMin, ref qMax, 50, 60, "q", tooltip:
            "Controls the size of every NPC marker — all icons above and the plain dot\n" +
            "shown when none apply."))
        { cfg.NpcQuestIconMinSize = qMin; cfg.NpcQuestIconMaxSize = qMax; changed = true; }

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // ── Gathering tab ────────────────────────────────────────────────────────

    private static bool DrawGatheringTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Gathering")) return false;
        bool    b = cfg.ShowGatheringNodes;
        Vector4 c = cfg.GatheringColor;
        bool changed = DrawEnableAndColor("gath", "Gathering Nodes", ref b, ref c);
        cfg.ShowGatheringNodes = b; cfg.GatheringColor = c;

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowGatheringNodes);
        bool gTgt = cfg.GatheringOnlyIfTargetable;
        if (DrawToggle("Hide non-targetable \"ghost\" nodes##gtgt", ref gTgt,
            "Filters out depleted or not-yet-spawned nodes the game still tracks even\n" +
            "when nothing's interactable there."))
        { cfg.GatheringOnlyIfTargetable = gTgt; changed = true; }

        bool gIcon = cfg.ShowGatheringIcons;
        if (DrawToggle("Show Mining/Botany icons##gicon", ref gIcon,
            "Shows the node's Mining/Quarrying/Logging/Botany icon instead of a plain dot."))
        { cfg.ShowGatheringIcons = gIcon; changed = true; }

        ImGui.BeginDisabled(!gIcon);
        ImGui.Indent();
        float gMin = cfg.GatheringIconMinSize, gMax = cfg.GatheringIconMaxSize;
        if (DrawSizeSliders(ref gMin, ref gMax, 50, 60, "g"))
        { cfg.GatheringIconMinSize = gMin; cfg.GatheringIconMaxSize = gMax; changed = true; }
        ImGui.Unindent();
        ImGui.EndDisabled();

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // ── Treasure tab ─────────────────────────────────────────────────────────

    private static bool DrawTreasureTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Treasure")) return false;
        bool    b = cfg.ShowTreasure;
        Vector4 c = cfg.TreasureColor;
        bool changed = DrawEnableAndColor("tres", "Treasure", ref b, ref c);
        cfg.ShowTreasure = b; cfg.TreasureColor = c;

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowTreasure);

        float trMin = cfg.TreasureMinSize, trMax = cfg.TreasureMaxSize;
        if (DrawSizeSliders(ref trMin, ref trMax, 50, 60, "tr", tooltip:
            "Controls the size of every treasure marker — the chest icon below and the\n" +
            "plain dot fallback."))
        { cfg.TreasureMinSize = trMin; cfg.TreasureMaxSize = trMax; changed = true; }

        ImGui.Spacing();

        bool trIcon = cfg.ShowTreasureIcons;
        if (DrawToggle("Show chest icon##tricon", ref trIcon,
            "No game-data sheet exposes a chest's visual type from its BaseId, so every\n" +
            "coffer shows the same icon (below)."))
        { cfg.ShowTreasureIcons = trIcon; changed = true; }

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowTreasureIcons);

        int trIconId = cfg.TreasureIconId;
        ImGui.SetNextItemWidth(90f);
        if (ImGui.InputInt("Icon ID##triconid", ref trIconId, 0, 0))
        { cfg.TreasureIconId = Math.Max(0, trIconId); changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Game icon ID for every treasure coffer — 60354/60355/60356 are known\n" +
                              "chest-icon variants.");

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // ── Aetherytes tab ───────────────────────────────────────────────────────

    private static bool DrawAetherytesTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("Aetherytes")) return false;
        bool    b = cfg.ShowAetherytes;
        Vector4 c = cfg.AetheryteColor;
        bool changed = DrawEnableAndColor("aeth", "Aetherytes", ref b, ref c);
        cfg.ShowAetherytes = b; cfg.AetheryteColor = c;

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowAetherytes);

        bool showShards = cfg.ShowAethernetShards;
        if (DrawToggle("Show Aethernet shards##aethshards", ref showShards,
            "Aethernet shards are the smaller waypoints in housing wards, the Firmament,\n" +
            "etc — as opposed to a city's one main aetheryte. Off shows only main ones."))
        { cfg.ShowAethernetShards = showShards; changed = true; }

        bool aIcon = cfg.ShowAetheryteIcons;
        if (DrawToggle("Show aetheryte icon##aicon", ref aIcon,
            "Falls back to the colour dot only if an icon doesn't resolve."))
        { cfg.ShowAetheryteIcons = aIcon; changed = true; }

        float aMin = cfg.AetheryteIconMinSize, aMax = cfg.AetheryteIconMaxSize;
        if (DrawSizeSliders(ref aMin, ref aMax, 50, 60, "a", tooltip:
            "Controls the size of every aetheryte marker — the icon above and the plain\n" +
            "dot shown when icons are off or fail to load."))
        { cfg.AetheryteIconMinSize = aMin; cfg.AetheryteIconMaxSize = aMax; changed = true; }

        string shardName = cfg.AethernetShardName;
        if (ImGui.InputText("Aethernet shard name##shardname", ref shardName, 64))
        { cfg.AethernetShardName = shardName; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A word in every Aethernet shard's name, in your game's language — matched\n" +
                              "as a substring, so \"Aethernet\" catches all shard names at once. Non-matching\n" +
                              "aetherytes are assumed to be main ones.");

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // ── FATEs tab ────────────────────────────────────────────────────────────

    private static bool DrawFatesTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("FATEs")) return false;
        bool    fateB = cfg.ShowFates;
        Vector4 fateC = cfg.FateColor;

        ImGui.TextDisabled("Independent of every other tab's toggles.");
        ImGui.Spacing();

        bool changed = DrawEnableAndColor("fates", "Show FATEs", ref fateB, ref fateC,
            "Shows active/about-to-start FATEs with their icon, sorted in the same\n" +
            "pass as every marker (closer paints on top). Range = General tab's range ×\n" +
            "the multiplier below. Works even with everything else off.");
        cfg.ShowFates = fateB; cfg.FateColor = fateC;

        ImGui.Indent();
        ImGui.BeginDisabled(!fateB);

        changed |= DrawSliderFloat("Distance multiplier##fatemul", 0.5f, 5.0f,
            () => cfg.FateDistanceMultiplier, v => cfg.FateDistanceMultiplier = MathF.Max(0.5f, v), "%.1f×");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("FATEs are detected up to (General tab's range × this) yalms — at 2.5× with\n" +
                              "the default 100y range, that's 250y: zone-wide, long before you're near them.");
        ImGui.TextDisabled($"Effective FATE range: {cfg.MaxMarkerDistance * cfg.FateDistanceMultiplier:F0} yalms");

        float fateMin = cfg.FateIconMinSize, fateMax = cfg.FateIconMaxSize;
        if (DrawSizeSliders(ref fateMin, ref fateMax, 50, 64, "fate",
                             "Min icon size (far away)", "Max icon size (close up)"))
        { cfg.FateIconMinSize = fateMin; cfg.FateIconMaxSize = fateMax; changed = true; }

        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.EndTabItem();
        return changed;
    }

    // Compact colour-edit flags: show only the small swatch, not text inputs.
    private static readonly ImGuiColorEditFlags ColorPickerFlags =
        ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar;

    // ── Shared tab building blocks ────────────────────────────────────────────

    // Checkbox + optional hover tooltip in one call — the shape behind most toggles below.
    private static bool DrawToggle(string label, ref bool value, string? tooltip = null)
    {
        bool changed = ImGui.Checkbox(label, ref value);
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return changed;
    }

    private static bool DrawEnableAndColor(
        string idPrefix, string label, ref bool enabled, ref Vector4 color, string? tooltip = null)
    {
        bool changed = false;
        if (ImGui.Checkbox($"##{idPrefix}_en", ref enabled)) changed = true;
        ImGui.SameLine();
        if (ImGui.ColorEdit4($"{label}##{idPrefix}_c", ref color, ColorPickerFlags)) changed = true;
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return changed;
    }

    private static bool DrawSizeSliders(
        ref float min, ref float max, int minHi, int maxHi, string idPrefix,
        string minLabel = "Min size (far away)", string maxLabel = "Max size (close up)",
        int lo = 8, string? tooltip = null)
    {
        bool changed = false;
        int mn = (int)min;
        if (ImGui.SliderInt($"{minLabel}##{idPrefix}min", ref mn, lo, minHi)) { min = mn; changed = true; }
        int mx = (int)max;
        if (ImGui.SliderInt($"{maxLabel}##{idPrefix}max", ref mx, lo, maxHi)) { max = mx; changed = true; }
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return changed;
    }

    // Slider bound to a getter/setter pair — avoids the temp-variable dance a property (can't pass by ref) would need.
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
