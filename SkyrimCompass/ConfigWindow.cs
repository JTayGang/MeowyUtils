using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using static SkyrimCompass.Loc;

namespace SkyrimCompass;

public sealed class ConfigWindow : Window
{
    private const float SaveDebounceSeconds = 1f;
    private static readonly Vector4 Amber = new(1f, .82f, .16f, 1f);
    private static readonly ImGuiColorEditFlags ColorFlags = ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar;

    private readonly Plugin plugin;
    private PlayerIconOverride _newOverride = new();
    private string[] _themeNames = [];
    private int _themeNamesRev = -1;
    private int _themeIndex;
    private DateTime _lastSave = DateTime.MinValue;
    private bool _dirty;
    private bool _confirmReset;

    public ConfigWindow(Plugin plugin) : base("Skyrim Compass Settings##skyrimcompasscfg",
        ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar)
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(370, 200), MaximumSize = new Vector2(620, 800) };
    }

    public override void PreDraw() => WindowName = L("Skyrim Compass Settings", "skyrimcompasscfg");

    public override void OnClose() => Flush();

    public override void Draw()
    {
        var cfg = plugin.Config;
        bool ch = false;

        if (!cfg.Enabled)
        {
            ImGui.TextColored(Amber, T("Skyrim Compass is turned off."));
            ImGui.SameLine();
            if (ImGui.Button(L("Turn on", "turnon"))) { cfg.Enabled = true; ch = true; }
        }

        ch |= Toggle(L("Compass", "topcompass"), ref cfg.ShowCompassBar, T("Shows the compass strip. Options are in the Compass tab."));
        ImGui.SameLine();
        ch |= Toggle(L("HP bars", "tophp"), ref cfg.ShowTargetBar, T("Shows name and health bars under the compass. Options are in the Target tab."));
        ImGui.SameLine();
        ch |= Toggle(L("Statuses", "topstatus"), ref cfg.ShowTargetStatuses, T("Shows your target's buff and debuff icons. Options are in the Target tab."));
        ImGui.Separator();

        if (ImGui.BeginTabBar("##tabs"))
        {
            ch |= LookTab(cfg);
            ch |= CompassTab(cfg);
            ch |= TargetTab(cfg);
            ch |= AdvancedTab(cfg);
            ImGui.EndTabBar();
        }

        ImGui.Separator();
        ch |= DrawFooter(cfg);

        if (ch) _dirty = true;
        if (_dirty && ((DateTime.UtcNow - _lastSave).TotalSeconds >= SaveDebounceSeconds || !IsOpen)) Flush();
    }

    private void Flush()
    {
        if (!_dirty) return;
        plugin.Config.Save(plugin.PluginInterface);
        _lastSave = DateTime.UtcNow;
        _dirty = false;
    }

    private bool DrawFooter(Configuration cfg)
    {
        bool ch = false;
        if (ImGui.Button(L("Close", "close"), new Vector2(80, 0))) IsOpen = false;
        ImGui.SameLine();
        if (ImGui.Button(L("Setup guide", "setup"))) plugin.OpenFirstTimeSetup();
        Tip(T("Opens the first-time setup again, with the Moodles/Loci checklist."));
        ImGui.SameLine();

        if (!_confirmReset)
        {
            if (ImGui.Button(L("Reset all settings", "reset"))) _confirmReset = true;
            Tip(T("Puts every setting back to its default. Keeps your language, custom player icons and the Moodles/Loci mirror switch."));
        }
        else
        {
            ImGui.TextColored(Amber, T("Are you sure?"));
            ImGui.SameLine();
            if (ImGui.Button(L("Yes, reset", "resetyes")))
            {
                cfg.ResetToDefaults();
                _themeIndex = 0;
                _confirmReset = false;
                ch = true;
            }
            ImGui.SameLine();
            if (ImGui.Button(L("Cancel", "resetno"))) _confirmReset = false;
        }

        int lang = (int)cfg.Language;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.Combo("Language / 言語##lang", ref lang, new[] { T("Auto"), "English", "日本語" }, 3))
        {
            cfg.Language = (LanguageSetting)lang;
            ch = true;
        }
        return ch;
    }

    private bool LookTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem(L("Look", "tabLook"))) return false;
        bool ch = false;

        if (ImGui.CollapsingHeader(L("Size & position", "hSize"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            ch |= Slider(L("Width", "w"), ref cfg.CompassWidth, 200f, 1400f, "%.0f", null, 120f);
            ImGui.SameLine();
            ch |= Slider(L("Height", "h"), ref cfg.CompassHeight, 20f, 80f, "%.0f", null, 120f);

            ch |= Toggle(L("Lock position", "lockpos"), ref cfg.LockPosition, T("Untick to move things around with your mouse."));
            ImGui.SameLine();
            if (ImGui.Button(L("Reset position", "resetpos"))) { cfg.XOffset = 0f; cfg.YOffset = Configuration.DefaultYOffset; ch = true; }
            Tip(T("Puts the compass back at the top center of the screen."));
            if (!cfg.LockPosition) ImGui.TextColored(Amber, T("Unlocked: drag the compass, health bars or status icons to move them."));

            ch |= Toggle(L("Hide in cutscenes and GPose", "hidecs"), ref cfg.HideDuringCutscenes);
        }

        if (ImGui.CollapsingHeader(L("Colors", "hColors"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (_themeNamesRev != Revision)
            {
                _themeNames = Array.ConvertAll(ColorThemes, t => T(t.Name));
                _themeNamesRev = Revision;
            }
            ImGui.SetNextItemWidth(180f);
            if (ImGui.Combo(L("Theme", "colortheme"), ref _themeIndex, _themeNames, _themeNames.Length))
            {
                ApplyColorTheme(cfg, ColorThemes[_themeIndex]);
                ch = true;
            }
            Tip(T("Changes every color at once. Pick Original to go back to the defaults."));

            ImGui.Spacing();
            ImGui.TextDisabled(T("Custom colors"));
            ch |= ColorPick(L("Background (also status tooltips)", "bgc"), ref cfg.BackgroundColor);
            ch |= ColorPick(L("Border", "bdc"), ref cfg.BorderColor);
            ch |= ColorPick(L("Main letters (N, E, S, W)", "cdc"), ref cfg.CardinalColor);
            ch |= ColorPick(L("In-between letters (NE, SW...)", "icc"), ref cfg.IntercardinalColor);
            ch |= ColorPick(L("Tick marks", "tkc"), ref cfg.TickColor);
        }

        ImGui.EndTabItem();
        return ch;
    }

    private bool CompassTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem(L("Compass", "tabCompass"))) return false;
        bool ch = false;
        bool on = cfg.ShowCompassBar;
        if (!on) ImGui.TextColored(Amber, T("The compass is hidden. Tick \"Compass\" at the top to change these."));

        Section(L("Basics", "sBasics"), on, () =>
        {
            ch |= Slider(L("Marker range (yalms)", "maxd"), ref cfg.MaxMarkerDistance, 10f, 200f, "%.0f", T("How far away something can be and still show up. FATEs show from farther away."));
            ch |= Slider(L("View width (degrees)", "vd"), ref cfg.VisibleDegrees, 30f, 180f, "%.0f", T("How much of the world fits on the compass. Smaller means more zoomed in."));
            ch |= Slider(L("Text size", "fs"), ref cfg.FontScale, .5f, 2.5f);
        }, true);

        Section(L("Players", "sPlayers"), on, () =>
        {
            ch |= EnableColor("players", T("Players"), ref cfg.ShowPlayers, ref cfg.PlayerColor);
            ImGui.BeginDisabled(!cfg.ShowPlayers);
            ch |= SizeSliders(ref cfg.PartyRoleIconMinSize, ref cfg.PartyRoleIconMaxSize, 50, 60, "pr");
            ImGui.Spacing();
            ch |= Toggle(L("Solid dot for friends", "sfr"), ref cfg.SolidFriendDots);
            ch |= Toggle(L("Show job icon for party members", "pri"), ref cfg.ShowPartyRoleIcons);
            ImGui.BeginDisabled(!cfg.ShowPartyRoleIcons);
            ch |= Toggle(L("Only in duties and PvP", "pridonly"), ref cfg.PartyRoleIconsOnlyInDuty);
            ImGui.EndDisabled();
            ImGui.EndDisabled();
        }, true);

        Section(L("Enemies", "sEnemies"), on, () =>
        {
            ch |= EnableColor("enemies", T("Enemies"), ref cfg.ShowEnemies, ref cfg.EnemyColor);
            ImGui.BeginDisabled(!cfg.ShowEnemies);
            ch |= Toggle(L("Only enemies that are fighting", "eng"), ref cfg.EnemiesOnlyIfEngaged, T("Hides enemies that haven't noticed you yet."));
            ch |= SizeSliders(ref cfg.EnemyMinSize, ref cfg.EnemyMaxSize, 50, 60, "en");
            ImGui.EndDisabled();
        });

        Section(L("NPCs", "sNpcs"), on, () =>
        {
            ch |= EnableColor("npcs", T("NPCs"), ref cfg.ShowNpcs, ref cfg.NpcColor);
            ImGui.BeginDisabled(!cfg.ShowNpcs);
            ch |= Toggle(L("Hide NPCs you can't click", "tgt"), ref cfg.NpcsOnlyIfTargetable, T("Skips invisible placeholder NPCs."));
            ch |= Toggle(L("Show quest marker icons", "qicon"), ref cfg.ShowNpcQuestIcons);
            ch |= Toggle(L("Show shop and repair icons", "sicon"), ref cfg.ShowShopIcons);
            ch |= Toggle(L("Show travel icons", "fticon"), ref cfg.ShowFastTravelIcons, T("Ferries, airships, Chocobo porters and similar."));
            ch |= SizeSliders(ref cfg.NpcQuestIconMinSize, ref cfg.NpcQuestIconMaxSize, 50, 60, "q");
            ImGui.EndDisabled();
        });

        Section(L("Gathering nodes", "sGather"), on, () =>
        {
            ch |= EnableColor("gath", T("Gathering nodes"), ref cfg.ShowGatheringNodes, ref cfg.GatheringColor);
            ImGui.BeginDisabled(!cfg.ShowGatheringNodes);
            ch |= Toggle(L("Hide nodes you can't gather from", "gtgt"), ref cfg.GatheringOnlyIfTargetable, T("Skips nodes that are used up or haven't appeared yet."));
            ch |= Toggle(L("Show node type icons", "gicon"), ref cfg.ShowGatheringIcons);
            ImGui.BeginDisabled(!cfg.ShowGatheringIcons);
            ch |= SizeSliders(ref cfg.GatheringIconMinSize, ref cfg.GatheringIconMaxSize, 50, 60, "g");
            ImGui.EndDisabled();
            ImGui.EndDisabled();
        });

        Section(L("Treasure chests", "sTreasure"), on, () =>
        {
            ch |= EnableColor("tres", T("Treasure chests"), ref cfg.ShowTreasure, ref cfg.TreasureColor);
            ImGui.BeginDisabled(!cfg.ShowTreasure);
            ch |= SizeSliders(ref cfg.TreasureMinSize, ref cfg.TreasureMaxSize, 50, 60, "tr");
            ImGui.Spacing();
            ch |= Toggle(L("Show chest icon", "tricon"), ref cfg.ShowTreasureIcons);
            ImGui.BeginDisabled(!cfg.ShowTreasureIcons);
            ImGui.SetNextItemWidth(90f);
            if (ImGui.InputInt(L("Icon number", "triconid"), ref cfg.TreasureIconId, 0, 0)) { cfg.TreasureIconId = Math.Max(0, cfg.TreasureIconId); ch = true; }
            ImGui.EndDisabled();
            ImGui.EndDisabled();
        });

        Section(L("Aetherytes", "sAeth"), on, () =>
        {
            ch |= EnableColor("aeth", T("Aetherytes"), ref cfg.ShowAetherytes, ref cfg.AetheryteColor);
            ImGui.BeginDisabled(!cfg.ShowAetherytes);
            ch |= Toggle(L("Show Aethernet shards", "aethshards"), ref cfg.ShowAethernetShards);
            ch |= Toggle(L("Show aetheryte icon", "aicon"), ref cfg.ShowAetheryteIcons);
            ch |= SizeSliders(ref cfg.AetheryteIconMinSize, ref cfg.AetheryteIconMaxSize, 50, 60, "a");
            ImGui.EndDisabled();
        });

        Section(L("FATEs", "sFates"), on, () =>
        {
            ch |= EnableColor("fates", T("Show FATEs"), ref cfg.ShowFates, ref cfg.FateColor, T("Shows FATEs that are running or about to start."));
            ImGui.BeginDisabled(!cfg.ShowFates);
            ch |= Slider(L("Range multiplier", "fatemul"), ref cfg.FateDistanceMultiplier, .5f, 5f, "%.1fx", T("FATEs show from this many times the marker range."));
            ImGui.TextDisabled(F("FATE range: {0:F0} yalms", cfg.MaxMarkerDistance * cfg.FateDistanceMultiplier));
            ch |= SizeSliders(ref cfg.FateIconMinSize, ref cfg.FateIconMaxSize, 50, 64, "fate");
            ImGui.EndDisabled();
        });

        Section(L("Limit Break glow", "sLb"), on, () =>
        {
            ch |= EnableColor("lbglow", T("Limit Break glow (bar 1 color)"), ref cfg.ShowLimitBreakGlow, ref cfg.LimitBreakGlowColor, T("The compass edges glow as your Limit Break charges. One color per bar."));
            ImGui.BeginDisabled(!cfg.ShowLimitBreakGlow);
            ch |= ColorPick(L("Bar 2 color", "lbc2"), ref cfg.LimitBreakGlowColor2);
            ch |= ColorPick(L("Bar 3 color", "lbc3"), ref cfg.LimitBreakGlowColor3);
            ImGui.EndDisabled();
        });

        ImGui.EndTabItem();
        return ch;
    }

    private bool TargetTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem(L("Target", "tabTarget"))) return false;
        bool ch = false;
        ImGui.TextDisabled(T("Tip: click something in the game to see your changes right away."));

        Section(L("Health bar", "sHp"), cfg.ShowTargetBar, () =>
        {
            ch |= Slider(L("Bar width", "tbwf"), ref cfg.TargetBarWidthFraction, .3f, 1f, "%.2f", T("How wide the bars are compared to the compass."));
            ch |= Slider(L("Bar thickness", "tbh"), ref cfg.TargetBarHeight, 6f, 30f, "%.0f");
            ch |= Slider(L("Name text size", "tbfs"), ref cfg.TargetBarFontScale, .5f, 2.5f);
            ch |= Toggle(L("Show level", "tblvl"), ref cfg.ShowTargetLevel);
            ch |= Toggle(L("Show HP percent", "tbhpp"), ref cfg.ShowTargetHealthPercent, T("Shows the percent under the health bar."));
            ImGui.BeginDisabled(!cfg.ShowTargetHealthPercent);
            ch |= Toggle(L("Also on the target's target", "tbtothpp"), ref cfg.ShowTargetOfTargetHealthPercent);
            ImGui.EndDisabled();
            ch |= EnableColor("tbshd", T("Show shield"), ref cfg.ShowTargetBarShield, ref cfg.TargetBarShieldColor, T("A light sheen over the shielded part of the bar."));
            ch |= Toggle(L("Show name ribbons", "tbrib"), ref cfg.ShowTargetBarRibbons, T("Glowing lines beside the name. They also show cast progress."));
        }, true);

        Section(L("Target's target", "sTot"), cfg.ShowTargetBar, () =>
        {
            ch |= Toggle(L("Show the target's target", "totbar"), ref cfg.ShowTargetOfTargetBar, T("A small bar for whoever your target is looking at."));
            ImGui.BeginDisabled(!cfg.ShowTargetOfTargetBar);
            ch |= Toggle(L("Warn if it's me", "aggro"), ref cfg.HighlightIfTargetingMe);
            ImGui.BeginDisabled(!cfg.HighlightIfTargetingMe);
            ch |= ColorPick(L("Warning color", "aggroc"), ref cfg.AggroWarningColor);
            ImGui.EndDisabled();
            ch |= Toggle(L("Show name", "totname"), ref cfg.ShowTargetOfTargetName);
            ImGui.BeginDisabled(!cfg.ShowTargetOfTargetName);
            ch |= Toggle(L("First name only", "totfirstname"), ref cfg.TargetOfTargetFirstNameOnly);
            ch |= Toggle(L("Show \"YOU\" for me", "totyou"), ref cfg.TargetOfTargetShowYou, T("Shows YOU instead of your character name."));
            ImGui.EndDisabled();
            ImGui.EndDisabled();
        });

        Section(L("Status icons", "sStatus"), cfg.ShowTargetStatuses, () =>
        {
            ch |= Slider(L("Icon size", "tssize"), ref cfg.TargetStatusIconSize, 12f, 40f, "%.0f");
            ch |= SliderInt(L("Most icons shown", "tsmax"), ref cfg.TargetStatusMaxIcons, 3, 20);

            int align = cfg.TargetStatusIconAlignLeft ? 1 : cfg.TargetStatusIconAlignRight ? 2 : 0;
            ImGui.SetNextItemWidth(120f);
            if (ImGui.Combo(L("Icon position", "tsalign"), ref align, new[] { T("Center"), T("Left"), T("Right") }, 3))
            {
                cfg.TargetStatusIconAlignLeft = align == 1;
                cfg.TargetStatusIconAlignRight = align == 2;
                ch = true;
            }
            Tip(T("Where the icons sit under the health bar."));

            ch |= EnableColor("tsesuna", T("Esuna marker"), ref cfg.ShowTargetStatusEsunaMarker, ref cfg.TargetStatusEsunaMarkerColor,
                T("A small bar above statuses that Esuna can remove. For Moodles and Loci it follows their own Dispellable setting."));
        }, true);

        ImGui.EndTabItem();
        return ch;
    }

    private bool AdvancedTab(Configuration cfg)
    {
        if (!ImGui.BeginTabItem(L("Advanced", "tabAdv"))) return false;
        bool ch = false, ovCh = false;

        if (ImGui.CollapsingHeader(L("Direction", "hDir"), ImGuiTreeNodeFlags.DefaultOpen))
        {
            ch |= Toggle(L("Follow the camera, not my character", "ucd"), ref cfg.UseCameraDirection);
            ImGui.BeginDisabled(!cfg.UseCameraDirection);
            ch |= Toggle(L("Measure distances from the camera", "ucp"), ref cfg.UseCameraPosition);
            ImGui.EndDisabled();
            ch |= Slider(L("Turn compass (degrees)", "rotoff"), ref cfg.RotationOffset, -180f, 180f, "%.0f", T("Turns the compass if North points the wrong way."));
            if (ImGui.Button(L("Flip North and South", "flipns"))) { cfg.RotationOffset = cfg.RotationOffset == 180f ? 0f : 180f; ch = true; }
            Tip(T("Click this if North and South look swapped."));
        }

        if (ImGui.CollapsingHeader(L("Compass shape", "hShape")))
        {
            ch |= Slider(L("Center zoom", "ls"), ref cfg.LensStrength, 1f, 3f, "%.2f", T("Higher gives the middle more room and squeezes the edges."));
            ImGui.Spacing();
            ImGui.TextDisabled(T("Markers fade as they get farther away."));
            float solid = (1f - cfg.DotNearZone) * 100f;
            if (Slider(L("Fully solid within", "nz"), ref solid, 0f, 50f, "%.1f%%", T("Markers this close are fully solid. Measured as a part of the marker range."))) { cfg.DotNearZone = 1f - solid / 100f; ch = true; }
            float fadeFrom = (1f - cfg.DotFarZone) * 100f;
            if (Slider(L("Fade out from", "fz"), ref fadeFrom, 50f, 100f, "%.1f%%", T("Markers farther than this fade away to nothing."))) { cfg.DotFarZone = 1f - fadeFrom / 100f; ch = true; }
            ch |= Slider(L("Opacity in between", "ma"), ref cfg.DotMidAlpha, 0f, 1f, "%.2f", T("How see-through markers are in the middle distance."));
        }

        if (ImGui.CollapsingHeader(L("Custom player icons", "hOverrides")))
        {
            ImGui.TextDisabled(T("Give specific players their own icon. Find icon numbers with /xldata (Icon Browser)."));
            ImGui.TextDisabled(T("Columns: name, icon number, B = border, F = fill, circle = round icon, last box = size."));
            if (cfg.PlayerIconOverrides.Count == 0) ImGui.TextDisabled(T("  (none yet)"));

            int removeAt = -1;
            for (int i = 0; i < cfg.PlayerIconOverrides.Count; i++)
            {
                ImGui.PushID(i);
                if (ImGui.Button("X##rmov")) removeAt = i;
                Tip(T("Remove"));
                ImGui.SameLine();
                if (DrawOverrideRow(cfg.PlayerIconOverrides[i], "ov", 110f)) ovCh = true;
                ImGui.PopID();
            }
            if (removeAt >= 0) { cfg.PlayerIconOverrides.RemoveAt(removeAt); ovCh = true; }

            ImGui.Separator();
            ImGui.TextDisabled(T("Add a player:"));
            ImGui.SameLine();
            ch |= DrawOverrideRow(_newOverride, "newov", 120f);
            ImGui.SameLine();

            bool canAdd = !string.IsNullOrWhiteSpace(_newOverride.PlayerName) && _newOverride.IconBaseId > 0;
            ImGui.BeginDisabled(!canAdd);
            if (ImGui.Button(L("Add", "addov")))
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
            if (mirror.MoodlesRejectedCount > 0)
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f),
                    $"Moodles rejected {mirror.MoodlesRejectedCount} mirrored status(es) - in Moodles' own settings, check " +
                    "\"Allow other plugins apply Moodles\" and \"Allow applying moodles from everyone\".");
            if (mirror.LociRejectedCount > 0)
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.25f, 1f),
                    $"Loci rejected {mirror.LociRejectedCount} mirrored status(es) as invalid data - likely a bad icon ID, empty title, or zero duration.");
            ImGui.EndDisabled();
        }

        ImGui.EndTabItem();
        return ch || ovCh;
    }

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
        Tip(T("Player name"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(68f);
        if (ImGui.InputInt($"##{idSuffix}id", ref ov.IconBaseId, 0, 0)) { ov.IconBaseId = Math.Max(0, ov.IconBaseId); ch = true; }
        Tip(T("Icon number"));
        ImGui.SameLine();

        ch |= Toggle($"B##{idSuffix}b", ref ov.ShowBorder, T("Border"));
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowBorder);
        ch |= ColorPick($"##{idSuffix}bc", ref ov.BorderColor);
        ImGui.EndDisabled();
        ImGui.SameLine();

        ch |= Toggle($"F##{idSuffix}f", ref ov.ShowFill, T("Fill"));
        ImGui.SameLine();
        ImGui.BeginDisabled(!ov.ShowFill);
        ch |= ColorPick($"##{idSuffix}fc", ref ov.FillColor);
        ImGui.EndDisabled();
        ImGui.SameLine();

        ch |= Toggle($"○##{idSuffix}circ", ref ov.ClipToCircle, T("Round icon"));
        ImGui.SameLine();

        ImGui.SetNextItemWidth(58f);
        if (ImGui.DragFloat($"##{idSuffix}mul", ref ov.SizeMultiplier, .05f, .5f, 3f, "%.2fx"))
        { ov.SizeMultiplier = Math.Clamp(ov.SizeMultiplier, .5f, 3f); ch = true; }
        Tip(T("Size"));
        return ch;
    }

    private static void Section(string label, bool enabled, Action body, bool open = false)
    {
        ImGui.BeginDisabled(!enabled);
        if (ImGui.CollapsingHeader(label, open ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None)) body();
        ImGui.EndDisabled();
    }

    private static void Tip(string? tip)
    {
        if (tip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tip);
    }

    private static bool Toggle(string label, ref bool v, string? tip = null)
    {
        bool ch = ImGui.Checkbox(label, ref v);
        Tip(tip);
        return ch;
    }

    private static bool Slider(string label, ref float v, float lo, float hi, string fmt = "%.2f", string? tip = null, float width = 0f)
    {
        if (width > 0f) ImGui.SetNextItemWidth(width);
        bool ch = ImGui.SliderFloat(label, ref v, lo, hi, fmt);
        Tip(tip);
        return ch;
    }

    private static bool SliderInt(string label, ref int v, int lo, int hi, string? tip = null)
    {
        bool ch = ImGui.SliderInt(label, ref v, lo, hi);
        Tip(tip);
        return ch;
    }

    private static bool ColorPick(string label, ref Vector4 v) => ImGui.ColorEdit4(label, ref v, ColorFlags);

    private static bool EnableColor(string id, string label, ref bool enabled, ref Vector4 color, string? tip = null)
    {
        bool ch = ImGui.Checkbox($"##{id}_en", ref enabled);
        ImGui.SameLine();
        ch |= ImGui.ColorEdit4($"{label}##{id}_c", ref color, ColorFlags);
        Tip(tip);
        return ch;
    }

    private static bool SizeSliders(ref float min, ref float max, int minHi, int maxHi, string id, int lo = 8)
    {
        bool ch = ImGui.SliderFloat($"{T("Size when far")}##{id}min", ref min, lo, minHi, "%.0f");
        ch |= ImGui.SliderFloat($"{T("Size when close")}##{id}max", ref max, lo, maxHi, "%.0f");
        return ch;
    }

    // Only used by the Moodles/Loci mirror block above.
    private static bool DrawToggle(string label, Func<bool> get, Action<bool> set, string? tooltip = null)
    {
        bool v = get();
        bool ch = ImGui.Checkbox(label, ref v);
        if (ch) set(v);
        if (tooltip != null && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return ch;
    }
}
