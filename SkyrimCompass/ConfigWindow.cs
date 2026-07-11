using System;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace SkyrimCompass;

public sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;

    // "Add new override" form state — not persisted, resets on commit.
    private PlayerIconOverride _newOverride = new();

    // Dropdown selection — not persisted (applied colors themselves are).
    private int _selectedThemeIndex = 0;

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
        float v; int iv;

        iv = (int)cfg.CompassWidth;
        if (ImGui.SliderInt("Width##w", ref iv, 200, 1400))
        { cfg.CompassWidth = iv; changed = true; }

        iv = (int)cfg.CompassHeight;
        if (ImGui.SliderInt("Height##h", ref iv, 20, 80))
        { cfg.CompassHeight = iv; changed = true; }

        // Slider bounds track the live display size so the bar can be dragged
        // anywhere on screen (and always stays fully visible) on any resolution.
        var io = ImGui.GetIO();

        int yMax = (int)MathF.Max(0f, io.DisplaySize.Y - cfg.CompassHeight);
        iv = (int)cfg.YOffset;
        if (ImGui.SliderInt("Y Offset (from top)##yo", ref iv, 0, yMax))
        { cfg.YOffset = iv; changed = true; }

        int xRange = (int)MathF.Max(0f, (io.DisplaySize.X - cfg.CompassWidth) * 0.5f);
        iv = (int)cfg.XOffset;
        if (ImGui.SliderInt("X Offset (from center)##xo", ref iv, -xRange, xRange))
        { cfg.XOffset = iv; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Shifts the compass left (negative) or right (positive) of center.\n" +
                "Range auto-adjusts to your screen width so the bar stays fully on-screen.");

        ImGui.Spacing();

        iv = (int)cfg.VisibleDegrees;
        if (ImGui.SliderInt("Visible Degrees##vd", ref iv, 30, 180))
        { cfg.VisibleDegrees = iv; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "How many degrees are visible in the LINEAR centre zone.\n" +
                "The lens effect extends additional degrees beyond this at the edges.");

        v = cfg.LensStrength;
        if (ImGui.SliderFloat("Lens Strength##ls", ref v, 1.0f, 3.0f))
        { cfg.LensStrength = v; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Fisheye/lens distortion at the edges. 1.0 = linear (no effect).\n" +
                "1.6 ≈ 60%% more degrees at the edges, compressed. 2.0 ≈ double.");

        v = cfg.FontScale;
        if (ImGui.SliderFloat("Font Scale##fs", ref v, 0.5f, 2.5f))
        { cfg.FontScale = v; changed = true; }

        ImGui.Spacing();

        bool sh = cfg.ShowHeadingText;
        if (DrawToggle("Show numeric heading below bar", ref sh)) { cfg.ShowHeadingText = sh; changed = true; }

        bool hdc = cfg.HideDuringCutscenes;
        if (DrawToggle("Hide during cutscenes", ref hdc,
            "Skips drawing the compass entirely while the camera is locked to a\n" +
            "cutscene (story cutscenes, skippable cinematics, and group pose) —\n" +
            "there's nothing to navigate to while the camera isn't yours anyway."))
        { cfg.HideDuringCutscenes = hdc; changed = true; }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool ucd = cfg.UseCameraDirection;
        if (DrawToggle("Use camera direction instead of character facing", ref ucd,
            "On: the compass follows where your CAMERA is looking (third-person\n" +
            "free camera, screenshots, sightseeing).\n" +
            "Off: the compass follows your CHARACTER's facing, matching how\n" +
            "Skyrim's compass behaves (recommended for combat/navigation)."))
        { cfg.UseCameraDirection = ucd; changed = true; }

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.UseCameraDirection);
        bool ucp = cfg.UseCameraPosition;
        if (DrawToggle("Also use camera location for distances##ucp", ref ucp,
            "Measures entity bearings/distances from your CAMERA's position instead\n" +
            "of your character's. Useful if you play heavily zoomed out or use a\n" +
            "camera offset mod. Only takes effect while 'Use camera direction' above\n" +
            "is also on."))
        { cfg.UseCameraPosition = ucp; changed = true; }
        ImGui.EndDisabled();
        ImGui.Unindent();

        ImGui.Spacing();
        ImGui.TextDisabled("Rotation Offset  (set to 180 if N and S are swapped)");
        iv = (int)cfg.RotationOffset;
        if (ImGui.SliderInt("##rotoff", ref iv, -180, 180))
        { cfg.RotationOffset = iv; changed = true; }

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

    // ── General tab (bar colors + theme presets + detection range/fade curve — merged:
    // both are cross-cutting settings that apply to every marker category rather than
    // belonging to any one of them, and neither filled a full tab on its own) ─────────

    private bool DrawGeneralTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem("General")) return false;
        bool changed = false;

        // Local helper avoids 5× repeated temp-variable pattern (properties can't be passed by ref).
        void CE(string label, Vector4 val, Action<Vector4> set)
        {
            if (ImGui.ColorEdit4(label, ref val)) { set(val); changed = true; }
        }

        ImGui.TextDisabled("Bar colors");
        CE("Background##bgc",                        cfg.BackgroundColor,    v => cfg.BackgroundColor    = v);
        CE("Border##bdc",                             cfg.BorderColor,        v => cfg.BorderColor        = v);
        CE("Cardinal labels  (N / S / E / W)##cdc",  cfg.CardinalColor,      v => cfg.CardinalColor      = v);
        CE("Intercardinal labels  (NE / SW …)##icc", cfg.IntercardinalColor, v => cfg.IntercardinalColor = v);
        CE("Tick marks##tkc",                        cfg.TickColor,          v => cfg.TickColor          = v);

        ImGui.SetNextItemWidth(180);
        if (ImGui.Combo("Theme preset##colortheme", ref _selectedThemeIndex, ColorThemeNames, ColorThemeNames.Length))
        {
            ApplyColorTheme(cfg, ColorThemes[_selectedThemeIndex]);
            changed = true;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Overwrites every compass color in one click — the bar colors above,\n" +
                "AND every marker category's color over in its own tab. Pick\n" +
                "\"Original\" to restore defaults. Anything can still be hand-tweaked\n" +
                "afterward — a theme is a starting point, not a locked-in mode.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextDisabled("Detection range  (shared by every marker category, incl. FATEs — see FATEs tab for its multiplier)");
        int md = (int)cfg.MaxMarkerDistance;
        if (ImGui.SliderInt("yalms##maxd", ref md, 10, 200)) { cfg.MaxMarkerDistance = md; changed = true; }

        ImGui.Spacing();
        ImGui.TextDisabled("Dot distance-fade curve");

        float nz = cfg.DotNearZone;
        if (ImGui.SliderFloat("Full opacity zone##nz", ref nz, 0.5f, 1.0f))
        { cfg.DotNearZone = nz; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Dots are fully opaque closer than this fraction of max range.\n" +
                "1.00 = always opaque (disables distance fade).");

        float fz = cfg.DotFarZone;
        if (ImGui.SliderFloat("Fade-to-zero zone##fz", ref fz, 0.0f, 0.5f))
        { cfg.DotFarZone = fz; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Dots fade to invisible below this fraction of max range.\n" +
                "0.00 = no fade-to-zero (dots stay at mid opacity until max range).");

        float ma = cfg.DotMidAlpha;
        if (ImGui.SliderFloat("Mid-range opacity##ma", ref ma, 0.0f, 1.0f))
        { cfg.DotMidAlpha = ma; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Opacity of dots in the middle distance band. 0 = invisible, 1 = fully opaque.");

        ImGui.EndTabItem();
        return changed;
    }

    // ── Players tab ──────────────────────────────────────────────────────────

    // One editable override row: name, icon ID, border/fill/clip/multiplier.
    // Shared by existing entries and the pending "add new" form.
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
            ImGui.SetTooltip("Game icon base ID\n(e.g. 62007 Paladin, 60453 Aetheryte, 61802 FC emblem)\nBrowse all icons with: /xldata icons");
        ImGui.SameLine();

        bool border = ov.ShowBorder;
        if (ImGui.Checkbox($"B##{idSuffix}b", ref border)) { ov.ShowBorder = border; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Draw a solid outer ring around the icon");
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowBorder);
        Vector4 bc = ov.BorderColor;
        if (ImGui.ColorEdit4($"##{idSuffix}bc", ref bc, ColorPickerFlags)) { ov.BorderColor = bc; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Border ring color");
        ImGui.EndDisabled();
        ImGui.SameLine();

        bool fill = ov.ShowFill;
        if (ImGui.Checkbox($"F##{idSuffix}f", ref fill)) { ov.ShowFill = fill; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Draw an inward-fading fill behind the icon\n(same bloom effect as party role icon backgrounds)");
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowFill);
        Vector4 fc = ov.FillColor;
        if (ImGui.ColorEdit4($"##{idSuffix}fc", ref fc, ColorPickerFlags)) { ov.FillColor = fc; changed = true; }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Fill color");
        ImGui.EndDisabled();
        ImGui.SameLine();

        bool clip = ov.ClipToCircle;
        if (ImGui.Checkbox($"○##{idSuffix}circ", ref clip)) { ov.ClipToCircle = clip; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Clip icon to a circle\n" +
                "Rounds square icon textures to fit neatly inside the border ring.\n" +
                "Uses ImGui's built-in rounded image rendering — no extra cost.");
        ImGui.SameLine();

        float mul = ov.SizeMultiplier;
        ImGui.SetNextItemWidth(58f);
        if (ImGui.DragFloat($"##{idSuffix}mul", ref mul, 0.05f, 0.5f, 3.0f, "%.2fx"))
        { ov.SizeMultiplier = Math.Clamp(mul, 0.5f, 3.0f); changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Per-icon size multiplier (stacks on top of the global 1.5× padding compensation).\n" +
                "1.0 = same apparent size as a party role icon.\n" +
                "Drag right for icons with heavy transparent padding,\n" +
                "drag left for icons with minimal padding that look oversized.");

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
            "Controls the size of EVERY player marker — hollow ring, solid friend\n" +
            "dot, and party role icon below — together."))
        { cfg.PartyRoleIconMinSize = prMin; cfg.PartyRoleIconMaxSize = prMax; changed = true; }

        ImGui.Spacing();

        bool sfr = cfg.SolidFriendDots;
        if (DrawToggle("Solid dot for friends##sfr", ref sfr,
            "Friends render as a solid filled dot instead of a hollow ring.\n" +
            "Overridden by party role icons and named overrides below."))
        { cfg.SolidFriendDots = sfr; changed = true; }

        bool pri = cfg.ShowPartyRoleIcons;
        if (DrawToggle("Show job icon for party members##pri", ref pri,
            "Party members show their class/job icon on a role-colored dot:\n" +
            "Tank=blue, Healer=green, DPS=red. Takes priority over the friend\n" +
            "dot and named overrides for anyone in your party. Uses the same\n" +
            "size slider above as every other player marker."))
        { cfg.ShowPartyRoleIcons = pri; changed = true; }

        // ── Named player icon overrides ───────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextDisabled("Named player overrides");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Replace specific players' markers (by exact, case-insensitive name)\n" +
                "with a custom game icon — browse IDs with: /xldata icons\n" +
                "Party role icons still take priority for anyone in your party.\n" +
                "B = border ring.  F = inward-fading fill (same as party job icons).");

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
            "Only shows hostiles that are targeting you or that you're targeting,\n" +
            "instead of every hostile in range. Great for big pulls and hunt trains."))
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
            "Skyrim-style: a glowing border creeps in from each end as limit break\n" +
            "charges — one layer per bar, stacked as each fills. Bar 1 alone already\n" +
            "reaches the whole border once full, so the number of full layers lit\n" +
            "up tells you how many bars are charged at a glance."))
        { cfg.ShowLimitBreakGlow = lbB; cfg.LimitBreakGlowColor = lbC; changed = true; }

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowLimitBreakGlow);

        Vector4 lb2 = cfg.LimitBreakGlowColor2;
        if (ImGui.ColorEdit4("Bar 2 color##lbc2", ref lb2))
        { cfg.LimitBreakGlowColor2 = lb2; changed = true; }
        Vector4 lb3 = cfg.LimitBreakGlowColor3;
        if (ImGui.ColorEdit4("Bar 3 color##lbc3", ref lb3))
        { cfg.LimitBreakGlowColor3 = lb3; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Each layer waves at its own speed/phase — bars 2 and 3 are\n" +
                "deliberately detuned from bar 1 and each other so the three\n" +
                "never ripple in lockstep. Meant to look chaotic at a full break.");

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
            "Filters out inert placeholder NPCs the game keeps in its object table\n" +
            "even when nothing's actually there — e.g. an empty chocobo stable slot\n" +
            "in housing. Recommended to leave this on."))
        { cfg.NpcsOnlyIfTargetable = tgt; changed = true; }

        bool qIcon = cfg.ShowNpcQuestIcons;
        if (DrawToggle("Show real quest marker icons##qicon", ref qIcon,
            "NPCs with an active quest marker (MSQ, side quest \"!\", blue quest,\n" +
            "in-progress \"?\", etc.) show that exact icon instead of a plain dot."))
        { cfg.ShowNpcQuestIcons = qIcon; changed = true; }

        bool mIcon = cfg.ShowMenderIcons;
        if (DrawToggle("Show real Mender icon##micon", ref mIcon,
            "Real icon for Mender NPCs (gear repair vendors). Detected by job\n" +
            "title, checked in English regardless of your client's language —\n" +
            "works the same on EN/DE/FR/JA. Shares the size sliders below."))
        { cfg.ShowMenderIcons = mIcon; changed = true; }

        bool sIcon = cfg.ShowShopIcons;
        if (DrawToggle("Show real Shop/Trader icon##sicon", ref sIcon,
            "Real icon for Shop/Trader NPCs (\"Merchant\", \"Vendor\", \"Trader\",\n" +
            "etc). Same English-regardless-of-client-language matching as\n" +
            "Mender above, and shares the same size sliders."))
        { cfg.ShowShopIcons = sIcon; changed = true; }

        bool ftIcon = cfg.ShowFastTravelIcons;
        if (DrawToggle("Fast Travel##fticon", ref ftIcon,
            "Real icon for Fast Travel NPCs — ferry skippers, airship/other\n" +
            "ticketers, and Chocobo Keeps/Falcon Porters (different icon per\n" +
            "type, one toggle). Same English-regardless-of-client-language\n" +
            "matching as Mender above."))
        { cfg.ShowFastTravelIcons = ftIcon; changed = true; }

        float qMin = cfg.NpcQuestIconMinSize, qMax = cfg.NpcQuestIconMaxSize;
        if (DrawSizeSliders(ref qMin, ref qMax, 50, 60, "q", tooltip:
            "Controls the size of EVERY NPC marker — all icons above AND the\n" +
            "plain dot shown when none of those apply."))
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
            "Filters out depleted or not-yet-spawned nodes the game keeps in its\n" +
            "object table even when nothing's currently interactable there."))
        { cfg.GatheringOnlyIfTargetable = gTgt; changed = true; }

        bool gIcon = cfg.ShowGatheringIcons;
        if (DrawToggle("Show real Mining/Botany icons##gicon", ref gIcon,
            "Shows the node's actual Mining/Quarrying/Logging/Botany icon instead of a plain dot."))
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
            "Controls the size of EVERY treasure marker — the chest icon below\n" +
            "AND the plain dot fallback."))
        { cfg.TreasureMinSize = trMin; cfg.TreasureMaxSize = trMax; changed = true; }

        ImGui.Spacing();

        bool trIcon = cfg.ShowTreasureIcons;
        if (DrawToggle("Show real chest icon##tricon", ref trIcon,
            "No game-data sheet exposes a chest's visual type from its BaseId,\n" +
            "so every coffer currently shows the same icon (below)."))
        { cfg.ShowTreasureIcons = trIcon; changed = true; }

        ImGui.Indent();
        ImGui.BeginDisabled(!cfg.ShowTreasureIcons);

        int trIconId = cfg.TreasureIconId;
        ImGui.SetNextItemWidth(90f);
        if (ImGui.InputInt("Icon ID##triconid", ref trIconId, 0, 0))
        { cfg.TreasureIconId = Math.Max(0, trIconId); changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Game icon ID shown for every treasure coffer — 60354 / 60355 / 60356\n" +
                "are three known treasure-chest icon variants.");

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
            "Aethernet shards are the smaller waypoints in housing wards, the\n" +
            "Firmament, etc, as opposed to a city's one main aetheryte. Off\n" +
            "shows only main aetherytes."))
        { cfg.ShowAethernetShards = showShards; changed = true; }

        bool aIcon = cfg.ShowAetheryteIcons;
        if (DrawToggle("Show real aetheryte icon##aicon", ref aIcon,
            "Falls back to the colour dot only if an icon doesn't resolve."))
        { cfg.ShowAetheryteIcons = aIcon; changed = true; }

        float aMin = cfg.AetheryteIconMinSize, aMax = cfg.AetheryteIconMaxSize;
        if (DrawSizeSliders(ref aMin, ref aMax, 50, 60, "a", tooltip:
            "Controls the size of EVERY aetheryte marker — the real icon above\n" +
            "AND the plain dot shown when icons are off or a texture fails to load."))
        { cfg.AetheryteIconMinSize = aMin; cfg.AetheryteIconMaxSize = aMax; changed = true; }

        string shardName = cfg.AethernetShardName;
        if (ImGui.InputText("Aethernet shard name##shardname", ref shardName, 64))
        { cfg.AethernetShardName = shardName; changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "A word in every Aethernet shard's name, in your game's language.\n" +
                "Matched as a substring, so \"Aethernet\" catches \"Ul'dah Aethernet\n" +
                "Shard\", \"Limsa Lominsa Aethernet Shard\", etc. all at once. A real\n" +
                "aetheryte that doesn't match is assumed to be a main aetheryte.");

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
            "Shows active or about-to-start FATEs using their real game icon.\n" +
            "Sorts in the same pass as every other marker, so closer items\n" +
            "always paint on top. Range = General tab's detection range ×\n" +
            "the multiplier below. Works even with all other markers off.");
        cfg.ShowFates = fateB; cfg.FateColor = fateC;

        ImGui.Indent();
        ImGui.BeginDisabled(!fateB);

        float fateMul = cfg.FateDistanceMultiplier;
        if (ImGui.SliderFloat("Distance multiplier##fatemul", ref fateMul, 0.5f, 5.0f, "%.1f×"))
        { cfg.FateDistanceMultiplier = Math.Max(0.5f, fateMul); changed = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "FATEs are detected up to (General tab's range × this value) yalms.\n" +
                "At 2.5× with the default 100y range, FATEs appear up to 250y away —\n" +
                "zone-wide, discoverable long before you're near them.");
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
}
