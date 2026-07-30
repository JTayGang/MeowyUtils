using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

// Wire shapes for two optional RP-status plugins' public IPC (Moodles, Loci) — merged into the
// target status row in RenderTargetStatuses. Dalamud IPC matches by tuple shape, not assembly
// identity, so a same-shaped local alias works with no project reference to either plugin; field
// order/types copied from each plugin's own IPC contract (Moodles/IPCTypedef.cs, Loci/LociTypeDef.cs
// — Loci's README explicitly invites third parties to copy these). Their StatusType/ChainType/
// ChainTrigger enums are substituted with plain int/byte of the same underlying width: we never
// read those fields, so only the shape has to line up
using MoodlesStatusInfo = (
    int Version, System.Guid GUID, int IconID, string Title, string Description, string CustomVFXPath,
    long ExpireTicks, int Type, int Stacks, int StackSteps, uint Modifiers, System.Guid ChainedStatus,
    int ChainTrigger, string Applier, string Dispeller, bool Permanent);
using LociStatusInfo = (
    int Version, System.Guid GUID, uint IconID, string Title, string Description, string CustomVFXPath,
    long ExpireTicks, byte Type, int Stacks, int StackSteps, int StackToChain, uint Modifiers,
    System.Guid ChainedGUID, byte ChainType, int ChainTrigger, string Applier, string Dispeller);

namespace SkyrimCompass;

// Skyrim-style compass bar: ImGui foreground draw list, fisheye/lens projection
public sealed class CompassHud : IDisposable
{
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly ITargetManager targetManager;
    private readonly INamePlateGui namePlateGui;
    private readonly ITextureProvider textureProvider;
    private readonly IFateTable fateTable;
    private readonly ICondition condition;
    private readonly IGameGui gameGui;
    private readonly Configuration config;
    private readonly IPluginLog log;
    private readonly IFontHandle jupiterFont;
    private readonly IDalamudPluginInterface pluginInterface;

    // Moodles/Loci status bridges (see the tuple aliases above). Subscribers are cheap to hold
    // even when the other plugin's absent; InstalledPlugins is not (allocates a wrapper per
    // installed plugin per access), so "is it active" is cached and only rechecked periodically
    private readonly ICallGateSubscriber<nint, List<MoodlesStatusInfo>> moodlesGetStatusesByPtr;
    private readonly ICallGateSubscriber<nint, List<LociStatusInfo>>    lociGetStatusesByPtr;
    private bool  moodlesActive;
    private float moodlesActiveCheckedAt = -1000f;
    private bool  lociActive;
    private float lociActiveCheckedAt   = -1000f;
    private const float PluginActiveCacheSeconds = 5f;

    // LB fade-out state (see UpdateFadeOut): freezes + centre→edge wipes on a big gauge drop
    private float lbTrackedProgress  = 0f;
    private float lbFrozenProgress   = 0f;
    private float lbFadeOutStartTime = -1f;   // -1 = not fading
    private const float LbFadeOutDuration = 2f;
    private const float LbDropThreshold   = 0.4f;

    // Cast-ribbon fade-out state (see UpdateFadeOut), keyed to target; castWipeTargetId resets on
    // target switch, so an old target's wipe cant bleed onto the new one
    private ulong castWipeTargetId     = 0;
    private float castTrackedProgress  = 0f;
    private float castFrozenProgress   = 0f;
    private float castFadeOutStartTime = -1f;
    private const float CastFadeOutDuration = 0.4f; // shorter than LB — casts end more often

    // Target HP bar: exponential ease toward real HP + decaying flash on damage taken
    private ulong lastTargetBarObjectId = 0;
    private float displayedTargetHpFrac = 1f;
    private float lastRawTargetHpFrac   = 1f;
    private float targetBarFlashAlpha   = 0f;

    // Target status icons: reused every frame, no per-frame List<> allocation
    private readonly List<(float RemainingTime, int Icon, string Name, string Description)> targetStatusBuffer = new();

    // Native context menus render above ImGui, so we dim the bar instead of hiding it
    private bool  contextMenuWasOpen;
    private float contextMenuFadeChangeTime = -1000f;  // time of last open/close flip
    private const float ContextMenuFadeSeconds = 0.15f;
    private const float ContextMenuDimmedAlpha = 0.33f; // alpha floor while menu is open

    // GameObjectId → nameplate marker icon; refreshed every nameplate update, 0/absent = none
    private readonly Dictionary<ulong, int> npcMarkerIcons = new();

    private readonly Dictionary<uint, int> gatheringIconCache = new();   // BaseId → icon ID (static data)
    private readonly ExcelSheet<GatheringPoint>     gatheringPointSheet;
    private readonly ExcelSheet<GatheringPointBase> gatheringPointBaseSheet;
    private readonly ExcelSheet<GatheringType>      gatheringTypeSheet;

    // BaseId → English Title/Singular cache. Named NPCs: vocation in Title, name in Singular;
    // flavor NPCs: vocation in Singular, Title empty; English forced so keywords match any client language
    private readonly Dictionary<uint, string> titleCache = new();
    private readonly Dictionary<uint, string> singularCache = new();
    private readonly ExcelSheet<ENpcResident> npcSheet;
    private readonly ExcelSheet<ClassJob>     classJobSheet;

    // Fully-qualified: unqualified "Action" collides with System.Action
    private readonly ExcelSheet<Lumina.Excel.Sheets.Action> actionSheet;

    // Keyword lists matched against npcSheet Title/Singular (see MatchesKeyword). Extend as new
    // vocation words turn up — /compass debug near an NPC shows TitleEN/SingularEN
    private static readonly string[] MenderKeywords = { "Mender", "Tinker", "Repairman" };
    private static readonly string[] ShopKeywords =
    {
        "Merchant", "Vendor", "Trader", "Sutler", "Supplier", "Junkmonger",
        "Fishmonger", "Dyemonger", "Jeweler", "Apothecary", "Culinarian",
        "Salvager", "Exchange", "Clothier", "Outfitter", "Peddler", "Dealer", "Armorer",
        "Shopkeep", "Stallkeeper", "Pawnbroker", "Provisioner", "Broker", "Proprietor",
        "Proprietress", "Marketeer", "Weaponsmith", "Tailor", "Herbalist", "Craftsman",
        "Appraiser",
    };
    // 3 icon variants share config.ShowFastTravelIcons (see TryGetNpcIcon); Falcon Porters use
    // Chocobo Keep's keyword/icon rather than their own category
    private static readonly string[] SkipperKeywords  = { "Skipper", "Ferryman" };
    // "Attendant" alone excluded — collides with non-airship titles (Lift/Ceremony/Rival Wings)
    private static readonly string[] TicketerKeywords = { "Ticketer", "Pilot", "Crewman", "Steward" };
    private static readonly string[] ChocoboKeepKeywords = { "Chocobokeep", "Falcon Porter" };

    // Reused every frame, no per-frame alloc. Obj/Fate: exactly one is set. T = normalized distance
    private readonly List<(IGameObject? Obj, IFate? Fate, float Dist, float Delta, float T, uint Col)> allCandidates = new();

    // Static: avoids a new Comparison<> alloc per-frame Sort
    private static readonly Comparison<(IGameObject? Obj, IFate? Fate, float Dist, float Delta, float T, uint Col)>
        DistFarFirst = (a, b) => b.Dist.CompareTo(a.Dist);

    // Compensates transparent icon padding (quest/Mender/Shop/job/override). Not Gathering/Aetheryte
    private const float IconSizeMultiplier          = 1.5f;
    private const float AetheryteIconSizeMultiplier = 1.75f;

    private static readonly (float Deg, string Label, bool IsMajor)[] Directions =
    [
        (0f,   "N",  true),
        (45f,  "NE", false),
        (90f,  "E",  true),
        (135f, "SE", false),
        (180f, "S",  true),
        (225f, "SW", false),
        (270f, "W",  true),
        (315f, "NW", false),
    ];

    public CompassHud(
        IClientState clientState,
        IObjectTable objectTable,
        ITargetManager targetManager,
        INamePlateGui namePlateGui,
        ITextureProvider textureProvider,
        IFateTable fateTable,
        ICondition condition,
        IGameGui gameGui,
        IDataManager dataManager,
        Configuration config,
        IPluginLog log,
        IFontHandle jupiterFont,
        IDalamudPluginInterface pluginInterface)
    {
        this.clientState     = clientState;
        this.objectTable     = objectTable;
        this.targetManager   = targetManager;
        this.namePlateGui    = namePlateGui;
        this.textureProvider = textureProvider;
        this.fateTable       = fateTable;
        this.condition       = condition;
        this.gameGui         = gameGui;
        this.config          = config;
        this.log             = log;
        this.jupiterFont     = jupiterFont;
        this.pluginInterface = pluginInterface;

        moodlesGetStatusesByPtr = pluginInterface.GetIpcSubscriber<nint, List<MoodlesStatusInfo>>(
            "Moodles.GetStatusManagerInfoByPtrV2");
        lociGetStatusesByPtr = pluginInterface.GetIpcSubscriber<nint, List<LociStatusInfo>>(
            "Loci.GetManagerInfoByPtr");

        gatheringPointSheet     = dataManager.GetExcelSheet<GatheringPoint>();
        gatheringPointBaseSheet = dataManager.GetExcelSheet<GatheringPointBase>();
        gatheringTypeSheet      = dataManager.GetExcelSheet<GatheringType>();
        npcSheet                = dataManager.GetExcelSheet<ENpcResident>(ClientLanguage.English);
        classJobSheet           = dataManager.GetExcelSheet<ClassJob>();
        actionSheet             = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();

        // OnDataUpdate fires every frame with ALL current nameplates (not just deltas)
        this.namePlateGui.OnDataUpdate += OnNamePlateDataUpdate;
    }

    public void Dispose()
    {
        namePlateGui.OnDataUpdate -= OnNamePlateDataUpdate;
    }

    private void OnNamePlateDataUpdate(
        INamePlateUpdateContext context, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        npcMarkerIcons.Clear();
        foreach (var h in handlers)
            if (h.MarkerIconId > 0)
                npcMarkerIcons[h.GameObjectId] = h.MarkerIconId;
    }

    // ── Public entry ──

    public unsafe void Draw()
    {
        if (!config.Enabled) return;

        // These 3 flags cover every cutscene type (story/skippable/group pose)
        if (config.HideDuringCutscenes && (
            condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            condition[ConditionFlag.WatchingCutscene] ||
            condition[ConditionFlag.WatchingCutscene78]))
            return;

        var player = objectTable.LocalPlayer;
        if (player == null) return;

        float headingRad = 0f;
        var   originPos  = player.Position;  // default: bearings/distances from character
        bool  gotHeading = false;

        if (config.UseCameraDirection)
        {
            // DirH increases counter-clockwise (tested in-game) — negate to fix
            var cm     = CameraManager.Instance();
            var camera = cm != null ? cm->Camera : null;
            if (camera != null && !float.IsNaN(camera->DirH))
            {
                headingRad = -camera->DirH;

                // First-person: DirH is a direct view angle, not orbital — exactly 180° off
                if (camera->ZoomMode == CameraZoomMode.FirstPerson)
                    headingRad += MathF.PI;

                if (config.UseCameraPosition)
                {
                    var camPos = camera->LastPosition;
                    if (!float.IsNaN(camPos.X) && !float.IsNaN(camPos.Y) && !float.IsNaN(camPos.Z))
                        originPos = camPos;
                }
                gotHeading = true;
            }
        }

        // Fallback: character facing (UseCameraDirection=false, or no camera)
        if (!gotHeading)
        {
            if (float.IsNaN(player.Rotation)) return;
            headingRad = MathF.PI - player.Rotation;  // FFXIV: rotation=0 → south, π → north
        }

        float heading = Normalize(headingRad * (180f / MathF.PI) + config.RotationOffset);

        var io  = ImGui.GetIO();
        var dl  = ImGui.GetForegroundDrawList();
        float now = (float)ImGui.GetTime();   // shared timestamp for this frame's animations

        float bw = config.CompassWidth;
        float bh = config.CompassHeight;
        float bx = (io.DisplaySize.X - bw) * 0.5f + config.XOffset;
        float by = config.YOffset;

        RenderBar(dl, bx, by, bw, bh, heading, player, originPos, now);

        // Native menus render above ImGui — fade instead of hide (see UpdateContextMenuFadeAlpha)
        float barAlpha = UpdateContextMenuFadeAlpha(now);

        // Decided up front so the main bar claims the full row when ToT won't draw, rather than always
        // reserving space for it; requires ToT to be a Character — a summoning bell or the marketboard
        // is a valid ToT but has no HP bar, so it'd just leave a blank gap
        var  curTarget = targetManager.Target;
        var  curTot    = curTarget?.TargetObject;
        bool hasTot    = config.ShowTargetOfTargetBar
            && curTarget != null && curTot != null && curTot.GameObjectId != curTarget.GameObjectId
            && curTot is ICharacter;

        var (mainX, mainW, totX, totW, rowW) = SplitTargetBarRow(bx, bw, hasTot);
        float rowGap = MathF.Max(2f, bh * 0.12f);
        float tbRowY = by + bh + rowGap;
        float nameCx = bx + bw * 0.5f;   // compass's own center, not the (possibly-narrower) trapezoid's

        float targetNameBottom = tbRowY;
        if (config.ShowTargetBar)
            targetNameBottom = RenderTargetBar(dl, mainX, mainW, tbRowY, nameCx, rowW, now, barAlpha);
        if (hasTot)
            RenderTargetOfTargetBar(dl, totX, totW, tbRowY, player, now, barAlpha);

        if (config.ShowTargetBar && config.ShowTargetStatuses && curTarget is IBattleChara targetChara)
            RenderTargetStatuses(dl, targetChara, nameCx, targetNameBottom, barAlpha);
    }

    // Eases alpha toward ContextMenuDimmedAlpha while a menu's open, back to 1 on close.
    // Call once/frame; SmoothStep over ContextMenuFadeSeconds either direction
    private float UpdateContextMenuFadeAlpha(float now)
    {
        bool menuOpenNow = IsVanillaContextMenuOpen();
        if (menuOpenNow != contextMenuWasOpen) { contextMenuFadeChangeTime = now; contextMenuWasOpen = menuOpenNow; }

        float t = ContextMenuFadeSeconds > 0f
            ? Math.Clamp((now - contextMenuFadeChangeTime) / ContextMenuFadeSeconds, 0f, 1f)
            : 1f;

        float fromAlpha = menuOpenNow ? 1f : ContextMenuDimmedAlpha;
        float toAlpha   = menuOpenNow ? ContextMenuDimmedAlpha : 1f;
        return Lerp(fromAlpha, toAlpha, SmoothStep(t));
    }

    // True while the native right-click menu (or submenu, e.g. "Emote >") is open; both bars fade
    // together — only one menu can be open at once, and we cant tell which bar its for anyway
    private bool IsVanillaContextMenuOpen() =>
        gameGui.GetAddonByName("ContextMenu").IsVisible || gameGui.GetAddonByName("AddonContextSub").IsVisible;

    // ── Lens projection ──

    // Maps a bearing offset (degrees) to a signed pixel offset from bar centre.
    // f(u) = 1-(1-u)^k, k = lensStrength. Linear at centre, compressed at edges. 1.0 = pure linear
    private static float Project(float delta, float halfVis, float barHalfW, float lensStr)
    {
        float extHalf = halfVis * lensStr;
        float absD    = MathF.Min(MathF.Abs(delta), extHalf);
        float u       = absD / extHalf;
        float f       = 1f - MathF.Pow(1f - u, lensStr);
        return (delta >= 0f ? 1f : -1f) * barHalfW * f;
    }

    // ── Main render ──

    private void RenderBar(
        ImDrawListPtr dl,
        float bx, float by, float bw, float bh,
        float heading, IPlayerCharacter player, Vector3 originPos, float now)
    {
        float cx       = bx + bw * 0.5f;
        float cy       = by + bh * 0.5f;
        float barHalfW = bw * 0.5f;

        float halfVis = config.VisibleDegrees * 0.5f;
        float lensStr = config.LensStrength;
        float extHalf = halfVis * lensStr;

        uint bgCol     = C(config.BackgroundColor);
        uint borderCol = C(config.BorderColor);
        uint tickCol   = C(config.TickColor);
        uint cardCol   = C(config.CardinalColor);
        uint ixCol     = C(config.IntercardinalColor);

        // Fully-opaque background for the masking cap fills
        uint solidBgCol = (bgCol & 0x00FFFFFFu) | 0xFF000000u;

        // Diamond end-cap dimensions
        float capHW = bh * 0.44f;
        float capHH = bh * 0.64f;

        // Run unconditionally so fade-out tracks real LB usage even when glow is toggled off
        float rawLbProgress       = GetLimitBreakProgress();
        float displayedLbProgress = UpdateLimitBreakDisplay(rawLbProgress, now, out float lbWipeProgress);
        float lbProgress          = config.ShowLimitBreakGlow ? displayedLbProgress : 0f;
        if (!config.ShowLimitBreakGlow) lbWipeProgress = 0f;

        // 1. Background
        dl.AddRectFilled(V(bx, by), V(bx + bw, by + bh), bgCol);

        // Warm centre glow
        uint  warmGlow = ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.70f, 0.35f, 0.08f));
        float gw       = bw * 0.22f;
        dl.AddRectFilledMultiColor(V(cx - gw, by), V(cx,      by + bh), 0u,       warmGlow, warmGlow, 0u);
        dl.AddRectFilledMultiColor(V(cx,      by), V(cx + gw, by + bh), warmGlow, 0u,       0u,       warmGlow);

        // Edge vignette
        dl.AddRectFilledMultiColor(V(bx,              by), V(bx + bw * 0.14f, by + bh), 0xAA000000u, 0u,          0u,          0xAA000000u);
        dl.AddRectFilledMultiColor(V(bx + bw * 0.86f, by), V(bx + bw,         by + bh), 0u,          0xAA000000u, 0xAA000000u, 0u);

        // Top bevel
        dl.AddLine(V(bx + 1f, by + 1f), V(bx + bw - 1f, by + 1f), 0x1AFFFFFF, 1f);

        // 2. Border — drawn before markers so icons (often taller than the bar) paint over it
        dl.AddRect(V(bx, by), V(bx + bw, by + bh), borderCol, 0f, ImDrawFlags.None, 1.5f);

        // 3. Limit break glow — one layer per bar, each bar's own 0–1 progress, detuned to avoid lockstep waves
        if (lbProgress > 0f)
        {
            float glowT = now;
            float bar1  = Math.Clamp(lbProgress,       0f, 1f);
            float bar2  = Math.Clamp(lbProgress - 1f,  0f, 1f);
            float bar3  = Math.Clamp(lbProgress - 2f,  0f, 1f);

            (float bar, float tMul, float tOff, Vector4 color)[] lbLayers =
            {
                (bar1, 1.00f, 0.0f, config.LimitBreakGlowColor),
                (bar2, 1.60f, 3.7f, config.LimitBreakGlowColor2),
                (bar3, 0.65f, 7.1f, config.LimitBreakGlowColor3),
            };
            foreach (var (bar, tMul, tOff, lbColor) in lbLayers)
            {
                if (bar <= 0f) continue;
                float t    = glowT * tMul + tOff;
                float segW = bw * 0.5f * bar;
                uint  col  = C(lbColor);
                float i    = PulseIntensity(t);
                DrawBorderGlowBracket(dl, bx, by, bw, bh, segW, col, i, t, lbWipeProgress, bar, fromLeft: true);
                DrawBorderGlowBracket(dl, bx, by, bw, bh, segW, col, i, t, lbWipeProgress, bar, fromLeft: false);
            }
        }

        // 4. Clip to bar
        dl.PushClipRect(V(bx + 1f, by), V(bx + bw - 1f, by + bh), true);

        // Push Jupiter before tick loop (height clamp needs its metrics); Push()=null if not built yet, default font used
        using var jupiterScope = jupiterFont.Available ? jupiterFont.Push() : null;

        float fontSize = ImGui.GetFontSize() * config.FontScale;
        var   font     = ImGui.GetFont();

        float labelTop    = by + bh * 0.12f;
        float labelHeight = ImGui.CalcTextSize("N").Y * config.FontScale;
        float labelBottom = labelTop + labelHeight;

        float maxTickHeight = MathF.Max(2f, (by + bh - 1f) - labelBottom);

        // 5. Tick marks
        for (int d = 0; d < 360; d += 5)
        {
            float delta = Delta(heading, d);
            if (MathF.Abs(delta) > extHalf + 2f) continue;

            float sx   = cx + Project(delta, halfVis, barHalfW, lensStr);
            bool  is90 = d % 90 == 0;
            bool  is45 = d % 45 == 0;
            bool  is10 = d % 10 == 0;

            float th = is90 ? bh * 0.52f
                     : is45 ? bh * 0.36f
                     : is10 ? bh * 0.22f
                             : bh * 0.13f;
            th = MathF.Min(th, maxTickHeight);

            float lensA    = LensEdgeAlpha(delta, halfVis, extHalf);
            uint  tickDraw = WithAlpha(is90 ? cardCol : tickCol, lensA);
            dl.AddLine(V(sx, by + bh - th - 1f), V(sx, by + bh - 1f), tickDraw, is90 ? 2f : 1f);
        }

        // 6. Direction labels
        foreach (var (deg, label, isMajor) in Directions)
        {
            float delta = Delta(heading, deg);
            if (MathF.Abs(delta) > extHalf + 10f) continue;

            float sx  = cx + Project(delta, halfVis, barHalfW, lensStr);
            var   tsz = ImGui.CalcTextSize(label) * config.FontScale;
            float tx  = sx - tsz.X * 0.5f;

            // Labels start fading earlier than ticks (compressed text is hard to read)
            float lensA     = LensEdgeAlpha(delta, halfVis * 0.88f, extHalf);
            uint  labelCol  = WithAlpha(isMajor ? cardCol : ixCol, lensA);
            uint  shadowCol = WithAlpha(0xBB000000u, lensA);

            dl.AddText(font, fontSize, V(tx + 1f, labelTop + 1f), shadowCol, label);
            dl.AddText(font, fontSize, V(tx,       labelTop),      labelCol,  label);
        }
        // jupiterScope disposed here → Jupiter automatically popped

        // 7. Markers + FATEs (single sorted pass)
        RenderAllMarkers(dl, cx, cy, halfVis, barHalfW, lensStr, heading, player, originPos);

        dl.PopClipRect();

        // 8. End-cap fills — opaque so they mask ticks/dots at the edges
        dl.AddQuadFilled(V(bx,      cy - capHH), V(bx + capHW,      cy), V(bx,      cy + capHH), V(bx - capHW,      cy), solidBgCol);
        dl.AddQuadFilled(V(bx + bw, cy - capHH), V(bx + bw + capHW, cy), V(bx + bw, cy + capHH), V(bx + bw - capHW, cy), solidBgCol);

        // 9. End-cap outlines
        DrawEndCapOutlines(dl, bx,      cy, capHW, capHH, borderCol);
        DrawEndCapOutlines(dl, bx + bw, cy, capHW, capHH, borderCol);

        // 10. Centre notch
        const float nH = 10f, nW = 6f;
        dl.AddTriangleFilled(V(cx + 1f, by + nH + 2f), V(cx - nW + 1f, by + 1f), V(cx + nW + 1f, by + 1f), 0x55000000u);
        dl.AddTriangleFilled(V(cx,      by + nH + 1f), V(cx - nW,       by),      V(cx + nW,       by),      0xF2FFFFFFu);

        // 11. Numeric heading
        if (config.ShowHeadingText)
        {
            string txt = $"{(int)heading:000}°";
            var    sz  = ImGui.CalcTextSize(txt);
            dl.AddText(V(cx - sz.X * 0.5f, by + bh + 3f), 0xBBCCBB99u, txt);
        }
    }

    // ── End-cap outline helper ──

    private static void DrawEndCapOutlines(
        ImDrawListPtr dl, float cx, float cy, float hw, float hh, uint color, float centerDotRadius = 2.5f)
    {
        dl.AddQuad(V(cx, cy - hh), V(cx + hw, cy), V(cx, cy + hh), V(cx - hw, cy), color, 1.5f);

        uint  innerCol = (color & 0x00FFFFFFu) | (((color >> 24) * 6 / 10) << 24);
        float s        = 0.52f;
        dl.AddQuad(V(cx, cy - hh * s), V(cx + hw * s, cy), V(cx, cy + hh * s), V(cx - hw * s, cy), innerCol, 1f);

        dl.AddCircleFilled(V(cx, cy), centerDotRadius, color);
    }

    // Same diamond footprint as DrawEndCapOutlines' outer quad, but filled solid — used as a
    // backing so the ornament's interior isnt hollow/see-through to whatever's behind it
    private static void DrawFilledDiamond(ImDrawListPtr dl, float cx, float cy, float hw, float hh, uint color) =>
        dl.AddQuadFilled(V(cx, cy - hh), V(cx + hw, cy), V(cx, cy + hh), V(cx - hw, cy), color);

    // ── Limit break glow helpers ──

    // Shared "breathing" pulse for glow-ribbon intensity — two detuned sine waves so it never
    // reads as a metronome. Used by the limit break glow and the target bar's name ribbons
    private static float PulseIntensity(float t) =>
        (0.75f + 0.25f * MathF.Sin(t * 0.79f)) * (0.92f + 0.08f * MathF.Sin(t * 3.23f + 1.17f));

    // Rippling ribbon a→b: flat at u=0, chaotic at u=1. fromLeft mirrors flow so both sides
    // drift toward centre. tipFadeStart closes the tip solid as a bar fills. wipeProgress is a
    // separate "after use" drain, erasing high-u first; wipeReversed flips to low-u first
    // instead (independent of tipFadeStart)
    private static void DrawGlowLine(
        ImDrawListPtr dl, Vector2 a, Vector2 b, uint col,
        float intensity, float t, bool fromLeft, float wipeProgress, float fillProgress,
        bool wipeReversed = false)
    {
        Vector2 delta = b - a;
        float   len   = delta.Length();
        if (len < 1f) return;

        Vector2 dir  = delta / len;
        Vector2 perp = new(-dir.Y, dir.X);

        const float amplitude         = 5f;
        const float waveLen           = 26f;
        const float flowSpeed         = 2f;
        const float wipeBandHalfWidth = 0.2f;
        const float harmonic2Weight   = 0.33f;  // u=1 blend toward a 2nd, faster wave; 0 = pure single sine

        // Fade zone closes to 0 width (fully opaque) as bar reaches 1.0 — solid "ready" cue
        float tipFadeStart   = Lerp(0.6f, 1.0f, Math.Clamp(fillProgress, 0f, 1f));
        float flowDir        = fromLeft ? -1f : 1f;
        float wipeBandCentre = Lerp(1f + wipeBandHalfWidth, -wipeBandHalfWidth, wipeProgress);
        float freq           = 2f * MathF.PI / waveLen;
        float freq2          = freq * 2f;                  // 2nd wave, double frequency — still constant, no integration needed
        float timePhase      = t * flowSpeed * flowDir;
        float timePhase2     = timePhase * 1.4f + 1.3f;    // different speed + fixed offset so the two never lock in step

        // Sized off the shorter of the two wavelengths in play (freq2's), so the faster wave
        // that shows up near the tip is still resolved cleanly
        int samples = Math.Clamp((int)(len / (waveLen * 0.5f) * 4f) + 2, 3, 96);

        // stackalloc avoids per-call heap allocation (called up to 18× per frame)
        Span<Vector2> pts   = stackalloc Vector2[96];
        Span<float>   fades = stackalloc float[96];

        for (int i = 0; i < samples; i++)
        {
            float along = len * i / (samples - 1);
            float u     = fromLeft ? along / len : 1f - along / len;

            // envelope: 0 at anchor → 1 at tip — the main "minimal near origin, more toward the
            // tip" shape. Blending in a 2nd, faster wave more heavily as u→1 adds back some of
            // the non-uniform liveliness a single sine loses near the tip. Both frequencies are
            // constant (unlike the old continuously-shortening one), so no phase integration —
            // just one extra Sin call, no loop-carried state
            float envelope = u * u * (3f - 2f * u);
            float wave     = MathF.Sin(along * freq  + timePhase)  * (1f - harmonic2Weight * u)
                            + MathF.Sin(along * freq2 + timePhase2) * (harmonic2Weight * u);
            pts[i] = a + dir * along + perp * (amplitude * envelope * wave);

            float tipFade  = 1f - SmoothStep(u <= tipFadeStart ? 0f
                               : Math.Clamp((u - tipFadeStart) / (1f - tipFadeStart + 1e-4f), 0f, 1f));
            float wipeU    = wipeReversed ? 1f - u : u;
            float wipeFade = 1f - SmoothStep(Math.Clamp(
                               (wipeU - (wipeBandCentre - wipeBandHalfWidth)) / (2f * wipeBandHalfWidth), 0f, 1f));
            fades[i] = tipFade * wipeFade;
        }

        ReadOnlySpan<(float alpha, float thickness)> layers =
        [
            (0.05f, 14f),
            (0.10f, 10f),
            (0.18f,  6f),
            (0.32f,  3.5f),
            (0.70f,  1.8f),
        ];
        foreach (var (alpha, thickness) in layers)
        {
            for (int i = 0; i < samples - 1; i++)
            {
                float segFade = (fades[i] + fades[i + 1]) * 0.5f;
                if (segFade <= 0.002f) continue;
                dl.AddLine(pts[i], pts[i + 1], WithAlpha(col, alpha * intensity * segFade), thickness);
            }
        }
    }

    // Top + bottom edge segments, segW wide from one end (fromLeft selects which).
    // Two calls (true/false) trace both sides of the bar when segW = bw/2
    private static void DrawBorderGlowBracket(
        ImDrawListPtr dl, float bx, float by, float bw, float bh,
        float segW, uint col, float intensity, float t,
        float wipeProgress, float fillProgress, bool fromLeft)
    {
        float x0 = fromLeft ? bx : bx + bw - segW;
        float x1 = fromLeft ? bx + segW : bx + bw;
        DrawGlowLine(dl, V(x0, by),      V(x1, by),      col, intensity, t, fromLeft, wipeProgress, fillProgress);
        DrawGlowLine(dl, V(x0, by + bh), V(x1, by + bh), col, intensity, t, fromLeft, wipeProgress, fillProgress);
    }

    // Returns LB progress as 0.0–3.0 (integer = bars full, fraction = next bar's progress)
    private static unsafe float GetLimitBreakProgress()
    {
        var uiState = UIState.Instance();
        if (uiState == null) return 0f;
        var lb = uiState->LimitBreakController;
        return lb.BarUnits <= 0 ? 0f : Math.Clamp((float)lb.CurrentUnits / lb.BarUnits, 0f, 3f);
    }

    // Shared freeze+wipe engine behind both Update*Display methods below. On trigger, freezes at
    // the last tracked value and sweeps wipeProgress 0→1 over duration; resyncIfExceedsFrozen is
    // checked AFTER a same-call freeze (sees the fresh snapshot, not last frame's) — needed for
    // LB's magnitude-based resync; externalResync covers cast's simpler isCasting-based one
    private static float UpdateFadeOut(
        ref float tracked, ref float frozen, ref float startTime,
        float realProgress, bool trigger, bool externalResync, bool resyncIfExceedsFrozen,
        float now, float duration, out float wipeProgress)
    {
        if (startTime < 0f)
        {
            if (trigger) { frozen = tracked; startTime = now; }
            else tracked = realProgress;
        }

        if (startTime >= 0f)
        {
            float elapsed = now - startTime;
            bool  resync  = externalResync || (resyncIfExceedsFrozen && realProgress > frozen);
            if (resync || elapsed >= duration)
            {
                startTime    = -1f;
                tracked      = realProgress;
                wipeProgress = 0f;
                return tracked;
            }
            wipeProgress = elapsed / duration;
            return frozen;
        }

        wipeProgress = 0f;
        return tracked;
    }

    // Sudden big drop (gauge reset) triggers the freeze; resyncs if progress climbs back above the snapshot
    private float UpdateLimitBreakDisplay(float realProgress, float now, out float wipeProgress) =>
        UpdateFadeOut(ref lbTrackedProgress, ref lbFrozenProgress, ref lbFadeOutStartTime,
            realProgress, realProgress < lbTrackedProgress - LbDropThreshold,
            externalResync: false, resyncIfExceedsFrozen: true, now, LbFadeOutDuration, out wipeProgress);

    // Casting stopping (not a magnitude drop) triggers the freeze; a fresh cast resyncs immediately.
    // Caller resets castTrackedProgress/castFadeOutStartTime on a target switch
    private float UpdateCastDisplay(IBattleChara? caster, float now, out float wipeProgress)
    {
        // TotalCastTime (not BaseCastTime) includes the game's own display-time adjustments
        bool  isCasting    = caster != null && caster.IsCasting && caster.TotalCastTime > 0f;
        float realProgress = isCasting ? Math.Clamp(caster!.CurrentCastTime / caster.TotalCastTime, 0f, 1f) : 0f;
        return UpdateFadeOut(ref castTrackedProgress, ref castFrozenProgress, ref castFadeOutStartTime,
            realProgress, !isCasting && castTrackedProgress > 0f,
            externalResync: isCasting, resyncIfExceedsFrozen: false, now, CastFadeOutDuration, out wipeProgress);
    }

    // CastActionType 1 ("Action") covers every cast worth labeling; items/mounts use other
    // sheets and rarely show a name-worthy timer, so left null rather than add unused lookups
    private string? GetCastActionName(IBattleChara caster)
    {
        if (caster.CastActionType != 1) return null;
        if (actionSheet.GetRowOrDefault(caster.CastActionId) is not { } row) return null;
        string name = row.Name.ToString();
        return name.Length > 0 ? name : null;
    }

    // True in duty content (role matters): dungeons/trials/raids (BoundByDuty + 56/95 variants),
    // deep dungeons (own flag, BoundByDuty flickers between floors), or PvP; gates ShowPartyRoleIcons
    // when PartyRoleIconsOnlyInDuty is on
    private bool IsInDutyOrPvp() =>
        condition[ConditionFlag.BoundByDuty]   ||
        condition[ConditionFlag.BoundByDuty56] ||
        condition[ConditionFlag.BoundByDuty95] ||
        condition[ConditionFlag.InDeepDungeon] ||
        clientState.IsPvP;

    // ── Target health bar (Skyrim-style name+HP for current target) ──
    // Docked beneath the compass, sharing its X position + a fractional width, so both read as one column

    // Same color the compass dot/ring would use: role color for a party member (same gating as
    // showPartyRoleIcons in RenderAllMarkers), else MarkerBaseColor's per-kind mapping; falls back
    // to NpcColor for anything with no dot equivalent (allied BattleNpcs, EventObj, unknown kinds)
    private uint TargetBarFillColor(IGameObject obj)
    {
        if (obj is ICharacter character
            && (character.StatusFlags & StatusFlags.PartyMember) != 0
            && config.ShowPartyRoleIcons
            && (!config.PartyRoleIconsOnlyInDuty || IsInDutyOrPvp()))
            return GetRoleColor(character);

        uint baseColor = MarkerBaseColor(obj);
        return baseColor != 0u ? baseColor : C(config.NpcColor);
    }

    // Upside-down trapezoid (full width `w` at top, narrowed by `taper` at bottom), fill
    // fraction `frac` growing from left (or right if fromRight); frac=1 = full shape either
    // way, so this covers background/border, HP fill, and shield sheen in one helper —
    // just the [0,frac]/[1-frac,1] case of TrapezoidSliceQuad below
    private static (Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl) TrapezoidFillQuad(
        float x, float y, float w, float h, float taper, float frac, bool fromRight = false)
    {
        frac = Math.Clamp(frac, 0f, 1f);
        return fromRight
            ? TrapezoidSliceQuad(x, y, w, h, taper, 1f - frac, 1f)
            : TrapezoidSliceQuad(x, y, w, h, taper, 0f, frac);
    }

    // Same trapezoid, an arbitrary middle slice [lo,hi] (0..1, lo<=hi) instead of an
    // edge-anchored fill. Used for the damage-flash sliver, wherever the just-lost HP sits
    private static (Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl) TrapezoidSliceQuad(
        float x, float y, float w, float h, float taper, float lo, float hi)
    {
        lo = Math.Clamp(lo, 0f, 1f);
        hi = Math.Clamp(hi, 0f, 1f);
        float botX0 = x + taper, botSpan = w - 2f * taper;
        float topA = x + w * lo,           topB = x + w * hi;
        float botA = botX0 + botSpan * lo, botB = botX0 + botSpan * hi;
        return (new Vector2(topA, y), new Vector2(topB, y), new Vector2(botB, y + h), new Vector2(botA, y + h));
    }

    // Main bar's share of the row when ToT is also drawn (rest, minus the gap, goes to ToT).
    // Retune the split by editing just these two
    private const float MainBarShareWithTot = 0.65f;
    private const float TargetBarRowGapFraction = 0.03f;

    // Single source of truth for the main/ToT row split — computed once in Draw() and handed
    // to each renderer so they cant drift apart; no ToT = main bar keeps the whole row
    private (float mainX, float mainW, float totX, float totW, float rowW) SplitTargetBarRow(
        float compassX, float compassW, bool hasTot)
    {
        float cx       = compassX + compassW * 0.5f;
        float rowW     = compassW * Math.Clamp(config.TargetBarWidthFraction, 0.1f, 1f);
        float rowX     = cx - rowW * 0.5f;

        if (!hasTot) return (rowX, rowW, 0f, 0f, rowW);

        float mainW = rowW * MainBarShareWithTot;
        float gap   = MathF.Max(10f, rowW * TargetBarRowGapFraction);
        float totW  = rowW - mainW - gap;
        float totX  = rowX + mainW + gap;
        return (rowX, mainW, totX, totW, rowW);
    }

    // Returns the bottom Y of the name row — Draw() anchors the status icon row beneath it
    // (ToT ignores this and renders at a fixed row Y instead)
    private float RenderTargetBar(ImDrawListPtr dl, float tbX, float tbW, float tbY, float nameCx, float nameRowW, float now, float barAlpha)
    {
        var target = targetManager.Target;
        if (target == null) return tbY;

        uint borderCol = WithAlpha(C(config.BorderColor),    barAlpha);
        uint bgCol     = WithAlpha(C(config.BackgroundColor), barAlpha);
        uint nameCol   = WithAlpha(C(config.CardinalColor),   barAlpha);

        // Name/ornaments/ribbons always center on the full row (nameCx), independent of tbW —
        // the trapezoid narrows to make room for ToT beside it, but the name below it doesn't
        // need to shift over just because its bar got a neighbor

        float cx = nameCx;

        // Gathering/treasure are targetable but have no HP — still get a name row, no bar
        // (tbH collapses to 0 instead of branching the whole layout)
        bool  isChara = target is ICharacter;
        float tbH     = isChara ? MathF.Max(4f, config.TargetBarHeight) : 0f;
        uint  fillCol = WithAlpha(TargetBarFillColor(target), barAlpha);

        // Trapezoid taper tied to thickness so the slant reads the same at any width;
        // capped against tbW so an extreme thickness/width combo cant invert the shape
        float taper = MathF.Min(tbH * 0.9f, tbW * 0.35f);

        if (isChara)
        {
            var   chara   = (ICharacter)target;
            float maxHp   = chara.MaxHp;
            float curHp   = chara.CurrentHp;
            float rawFrac = maxHp > 0f ? Math.Clamp(curHp / maxHp, 0f, 1f) : 0f;

            // Snap instantly on target switch — easing in from an unrelated old target's HP would look like a bug
            float dt = ImGui.GetIO().DeltaTime;
            if (target.GameObjectId != lastTargetBarObjectId)
            {
                lastTargetBarObjectId = target.GameObjectId;
                displayedTargetHpFrac = rawFrac;
                lastRawTargetHpFrac   = rawFrac;
                targetBarFlashAlpha   = 0f;
            }
            else
            {
                if (rawFrac < lastRawTargetHpFrac - 0.001f) targetBarFlashAlpha = 1f;
                lastRawTargetHpFrac = rawFrac;
                displayedTargetHpFrac += (rawFrac - displayedTargetHpFrac) * (1f - MathF.Exp(-dt * 14f));
            }
            targetBarFlashAlpha = MathF.Max(0f, targetBarFlashAlpha - dt / 0.4f);

            var (bTl, bTr, bBr, bBl) = TrapezoidFillQuad(tbX, tbY, tbW, tbH, taper, 1f);
            dl.AddQuadFilled(bTl, bTr, bBr, bBl, bgCol);

            // Fill/flash/shield inset a couple px so the border doesnt sit on the fill's edge;
            // inner taper is rescaled to the inset box's own height — reusing the outer taper
            // would steepen the slant on a shorter box, shrinking the gap at top, widening at bottom
            const float inset      = 2f;
            float       innerH     = tbH - inset * 2f;
            float       innerTaper = taper * (innerH / tbH);
            var (fTl, fTr, fBr, fBl) = TrapezoidFillQuad(
                tbX + inset, tbY + inset, tbW - inset * 2f, innerH, innerTaper, displayedTargetHpFrac);
            dl.AddQuadFilled(fTl, fTr, fBr, fBl, fillCol);

            // Flash only the sliver just lost (rawFrac..displayedTargetHpFrac), not the whole bar —
            // self-narrows to nothing as displayedTargetHpFrac eases down to meet rawFrac
            if (targetBarFlashAlpha > 0f)
            {
                float flashLo = MathF.Min(rawFrac, displayedTargetHpFrac);
                float flashHi = MathF.Max(rawFrac, displayedTargetHpFrac);
                if (flashHi > flashLo)
                {
                    var (hTl, hTr, hBr, hBl) = TrapezoidSliceQuad(
                        tbX + inset, tbY + inset, tbW - inset * 2f, innerH, innerTaper, flashLo, flashHi);
                    dl.AddQuadFilled(hTl, hTr, hBr, hBl, WithAlpha(0xFFFFFFFFu, targetBarFlashAlpha * 0.5f * barAlpha));
                }
            }

            if (config.ShowTargetBarShield && chara.ShieldPercentage > 0)
            {
                float shieldFrac = Math.Clamp(chara.ShieldPercentage / 100f, 0f, 1f);
                var (sTl, sTr, sBr, sBl) = TrapezoidFillQuad(
                    tbX + inset, tbY + inset, tbW - inset * 2f, innerH, innerTaper, shieldFrac, fromRight: true);
                dl.AddQuadFilled(sTl, sTr, sBr, sBl, WithAlpha(C(config.TargetBarShieldColor), barAlpha));
            }

            // Top bevel matches the compass bar's highlight (top edge stays full-width — only the bottom narrows)
            dl.AddLine(V(tbX + 1f, tbY + 1f), V(tbX + tbW - 1f, tbY + 1f), WithAlpha(0x1AFFFFFFu, barAlpha), 1f);
            dl.AddQuad(bTl, bTr, bBr, bBl, borderCol, 1.5f);
        }

        // ── Name row — flanked by small versions of the compass's own diamond ornament ──
        using var jupiterScope = jupiterFont.Available ? jupiterFont.Push() : null;
        float fontSize = ImGui.GetFontSize() * config.TargetBarFontScale;
        var   font     = ImGui.GetFont();

        // Name row shows the cast action's name while IsCasting — not tied to the ribbon's
        // fade-out grace period below; popping back instantly reads fine, like native cast bars
        string? castName = target is IBattleChara castingChara && castingChara.IsCasting && castingChara.TotalCastTime > 0f
            ? GetCastActionName(castingChara)
            : null;

        string label = castName ?? target.Name.TextValue;
        if (castName == null && config.ShowTargetLevel && target is ICharacter lvlChar && lvlChar.Level > 0)
            label = $"Lv{lvlChar.Level}  {label}";

        var   tsz     = ImGui.CalcTextSize(label) * config.TargetBarFontScale;
        float nameGap = MathF.Max(6f, tbH * 0.5f);
        float nameY   = tbY + tbH + nameGap;
        float tx      = cx - tsz.X * 0.5f;

        // Shared black shadow/backing for the name, endcaps, and ribbons below — grounds all
        // three against the game world, like the compass's own background panel does
        uint shadowCol = WithAlpha(0xCC000000u, barAlpha);

        ReadOnlySpan<(float dx, float dy)> textOutline =
        [
            (-1f, -1f), (0f, -1f), (1f, -1f),
            (-1f,  0f),            (1f,  0f),
            (-1f,  1f), (0f,  1f), (1f,  1f),
        ];
        foreach (var (dx, dy) in textOutline)
            dl.AddText(font, fontSize, V(tx + dx, nameY + dy), shadowCol, label);
        dl.AddText(font, fontSize, V(tx, nameY), nameCol, label);

        float ornHH  = fontSize * 0.46f, ornHW = ornHH * 0.69f;
        float ornGap = 6f;
        float textCy = nameY + tsz.Y * 0.5f;
        float leftOrnX  = tx - ornGap - ornHW;
        float rightOrnX = tx + tsz.X + ornGap + ornHW;

        // Solid backing, a couple px larger than the ornament so it peeks out as a border too
        // (outline alone left the interior see-through)
        float shHW = ornHW + 2f, shHH = ornHH + 2f;
        DrawFilledDiamond(dl, leftOrnX,  textCy, shHW, shHH, shadowCol);
        DrawFilledDiamond(dl, rightOrnX, textCy, shHW, shHH, shadowCol);
        DrawEndCapOutlines(dl, leftOrnX,  textCy, ornHW, ornHH, borderCol, ornHW * 0.28f);
        DrawEndCapOutlines(dl, rightOrnX, textCy, ornHW, ornHH, borderCol, ornHW * 0.28f);

        // Name ribbons reuse the LB glow's flowing-line technique; each flies horizontally out
        // from its ornament at name-row height (angling up would touch the bar's bottom edge)
        if (isChara && config.ShowTargetBarRibbons)
        {
            // Ornament's outer tip, not centre — reads as continuing the point
            float leftEdgeX  = leftOrnX  - ornHW;
            float rightEdgeX = rightOrnX + ornHW;

            // Reach toward the full row's edges, not tbX/tbW (the trapezoid's own, possibly
            // narrower bounds when ToT shares the row) — the name row these flank stays full width
            float nameRowLeft  = nameCx - nameRowW * 0.5f;
            float nameRowRight = nameCx + nameRowW * 0.5f;
            float ribbonInset  = MathF.Max(8f, nameRowW * 0.06f);
            // Clamped: a long name (ornaments pushed wide) cant shrink/flip the outward travel
            float ribbonLeftX  = MathF.Min(nameRowLeft  + ribbonInset, leftEdgeX  - 24f);
            float ribbonRightX = MathF.Max(nameRowRight - ribbonInset, rightEdgeX + 24f);
            float glowT        = now;

            // 2 layers/ribbon (backing + borderCol), timed like the LB bars above (3 of these 4
            // pairs reuse those tMul/tOff) — 4 independent waves so backing/real, left/right never lockstep
            (float edgeX, float targetX, uint col, float tMul, float tOff)[] ribbonLayers =
            {
                (leftEdgeX,  ribbonLeftX,  shadowCol,  0.65f, 7.1f),
                (leftEdgeX,  ribbonLeftX,  borderCol,  1.00f, 0.0f),
                (rightEdgeX, ribbonRightX, shadowCol,  1.15f, 5.3f),
                (rightEdgeX, ribbonRightX, borderCol,  1.60f, 3.7f),
            };

            // fillProgress=0: constant fade at the outer end (not LB's "closes solid as it
            // fills"). Intensity flat 1f — steady, no pulse
            foreach (var (edgeX, targetX, col, tMul, tOff) in ribbonLayers)
            {
                float t = glowT * tMul + tOff;
                DrawGlowLine(dl, V(edgeX, textCy), V(targetX, textCy),
                    col, 1f, t, fromLeft: true, wipeProgress: 0f, fillProgress: 0f);
            }

            // Cast ribbon: 3rd ribbon/side, grows outward as the cast advances (like the LB
            // brackets). fillProgress pinned to 0 (not castProgress) so its tip fade matches the
            // ambient ribbons' constant ~40% once grown to meet them — tying it to castProgress
            // would collapse that to a hard edge at 1.0 instead. wipeReversed:true because this
            // ribbon's anchor is u=0 (ambient ribbons' anchor too), opposite an LB bracket's u=1
            // tip — without it the wipe erases the wrong (outside) end first. No config toggle;
            // AggroWarningColor (not borderCol) — same "something's coming" accent as ToT's aggro state
            if (target.GameObjectId != castWipeTargetId)
            {
                castWipeTargetId     = target.GameObjectId;
                castTrackedProgress  = 0f;
                castFadeOutStartTime = -1f;
            }
            float castProgress = UpdateCastDisplay(target as IBattleChara, glowT, out float castWipeProgress);
            if (castProgress > 0f)
            {
                float castIntensity = PulseIntensity(glowT);
                uint  castCol       = WithAlpha(C(config.AggroWarningColor), barAlpha);
                DrawGlowLine(dl, V(leftEdgeX, textCy), V(Lerp(leftEdgeX, ribbonLeftX, castProgress), textCy),
                    castCol, castIntensity, glowT, fromLeft: true, wipeProgress: castWipeProgress, fillProgress: 0f,
                    wipeReversed: true);
                DrawGlowLine(dl, V(rightEdgeX, textCy), V(Lerp(rightEdgeX, ribbonRightX, castProgress), textCy),
                    castCol, castIntensity, glowT, fromLeft: true, wipeProgress: castWipeProgress, fillProgress: 0f,
                    wipeReversed: true);
            }
        }

        // Right click → vanilla context menu (see HandleTargetFrameClick); covers the HP
        // trapezoid, name row, and flanking ornaments (can sit outside the trapezoid's width)
        float clickTop    = tbY;
        float clickBottom = nameY + tsz.Y;
        float clickLeft   = MathF.Min(tbX, leftOrnX - shHW);
        float clickRight  = MathF.Max(tbX + tbW, rightOrnX + shHW);
        HandleTargetFrameClick(V(clickLeft, clickTop), V(clickRight, clickBottom), target, allowLeftClickToTarget: false);

        return nameY + tsz.Y;
    }

    // ── Target-of-target — FF14's ToT, restyled ──
    // Hidden when your target's target is nobody, itself (noise), or not a Character (a
    // summoning bell/marketboard has no HP bar) — Draw()'s hasTot covers all of that already.
    // Exception: target targeting YOU swaps this tier to a pulsing warning color; same
    // trapezoid construction as the main bar (narrower, to fit beside it); no name row yet
    private void RenderTargetOfTargetBar(
        ImDrawListPtr dl, float tbX, float tbW, float tbY, IPlayerCharacter player, float now, float barAlpha)
    {
        var target = targetManager.Target;
        var tot    = target?.TargetObject;
        if (target == null || tot == null || tot.GameObjectId == target.GameObjectId) return;
        if (tot is not ICharacter chara) return;

        bool targetingMe = config.HighlightIfTargetingMe && target.TargetObjectId == player.GameObjectId;

        float tbH = MathF.Max(4f, config.TargetBarHeight);

        uint borderCol = WithAlpha(C(config.BorderColor),     barAlpha);
        uint bgCol     = WithAlpha(C(config.BackgroundColor), barAlpha);
        uint fillCol   = targetingMe ? C(config.AggroWarningColor) : TargetBarFillColor(tot);

        float maxHp = chara.MaxHp;
        float curHp = chara.CurrentHp;
        float frac  = maxHp > 0f ? Math.Clamp(curHp / maxHp, 0f, 1f) : 0f;

        // Same trapezoid + inset-fill construction as the main bar (see its taper/inset comment)
        float taper = MathF.Min(tbH * 0.9f, tbW * 0.35f);
        var (bTl, bTr, bBr, bBl) = TrapezoidFillQuad(tbX, tbY, tbW, tbH, taper, 1f);
        dl.AddQuadFilled(bTl, bTr, bBr, bBl, bgCol);

        const float inset      = 2f;
        float       innerH     = tbH - inset * 2f;
        float       innerTaper = taper * (innerH / tbH);
        if (frac > 0f)
        {
            float pulse = targetingMe ? 0.82f + 0.18f * MathF.Sin(now * 5f) : 1f;
            var (fTl, fTr, fBr, fBl) = TrapezoidFillQuad(
                tbX + inset, tbY + inset, tbW - inset * 2f, innerH, innerTaper, frac);
            dl.AddQuadFilled(fTl, fTr, fBr, fBl, WithAlpha(fillCol, pulse * barAlpha));
        }

        dl.AddLine(V(tbX + 1f, tbY + 1f), V(tbX + tbW - 1f, tbY + 1f), WithAlpha(0x1AFFFFFFu, barAlpha), 1f);
        dl.AddQuad(bTl, bTr, bBr, bBl, borderCol, 1.5f);

        // Same right-click → context menu handling as the main bar, for whatever your target
        // has itself targeted (or yourself, in the targetingMe case — valid in vanilla too)
        HandleTargetFrameClick(V(tbX, tbY), V(tbX + tbW, tbY + tbH), tot, allowLeftClickToTarget: true);
    }

    // ── Target status icons — native StatusList order, capped count, small duration readout,
    // hover tooltip. No sorting, no buff/debuff split: StatusList already gives us exactly what
    // the vanilla frame would show, so this only ever filters out empty slots (StatusId 0) and
    // ones whose GameData/Icon didn't resolve. Moodles/Loci active statuses (if either plugin's
    // installed and its toggle's on) are appended into this same row, same size/cap, no sorting
    // between sources — native first, then Moodles, then Loci
    private void RenderTargetStatuses(ImDrawListPtr dl, IBattleChara target, float cx, float y, float barAlpha)
    {
        float size = MathF.Max(8f, config.TargetStatusIconSize);
        float hGap = size * 0.25f;
        int   max  = Math.Max(1, config.TargetStatusMaxIcons);

        targetStatusBuffer.Clear();
        foreach (var status in target.StatusList)
        {
            if (targetStatusBuffer.Count >= max) break;
            if (status.StatusId == 0) continue;
            if (status.GameData.ValueNullable is not { } row || row.Icon == 0) continue;
            targetStatusBuffer.Add((status.RemainingTime, (int)row.Icon, row.Name.ToString(), row.Description.ToString()));
        }

        if (target.Address != IntPtr.Zero)
        {
            float now = (float)ImGui.GetTime();
            if (config.ShowMoodlesStatuses && targetStatusBuffer.Count < max
                && IsPluginActive("Moodles", ref moodlesActive, ref moodlesActiveCheckedAt, now))
                AppendMoodlesStatuses(target.Address, max);
            if (config.ShowLociStatuses && targetStatusBuffer.Count < max
                && IsPluginActive("Loci", ref lociActive, ref lociActiveCheckedAt, now))
                AppendLociStatuses(target.Address, max);
        }
        if (targetStatusBuffer.Count == 0) return;

        int   n       = targetStatusBuffer.Count;
        float startX  = cx - (n * size + (n - 1) * hGap) * 0.5f;
        float topGap  = size * 0.15f;                                        // tightened up from the name row
        float halfH   = size * 0.5f * GetIconAspect(targetStatusBuffer[0].Icon);  // status icons run taller than wide
        float scy     = y + topGap + halfH;

        float fontSize = MathF.Max(9f, size * 0.8f);   // linear in icon size — no per-frame text measurement
        var   font     = ImGui.GetFont();
        float textGap  = -size * 0.12f;   // tucked up under the icon — its texture has some built-in padding

        for (int i = 0; i < n; i++)
        {
            var (remaining, icon, name, description) = targetStatusBuffer[i];
            float sx = startX + i * (size + hGap) + size * 0.5f;
            if (!TryDrawIcon(dl, icon, sx, scy, size, barAlpha)) continue;

            // At most 3 characters: seconds/minutes/hours count up, and anything 9 days or beyond
            // (which the game itself stops tracking precisely too) just pins at "9+d"
            string? durationLabel = remaining <= 0f ? null
                : remaining < 60f ? $"{(int)remaining}"
                : remaining < 3600f ? $"{(int)(remaining / 60f)}m"
                : remaining < 86400f ? $"{(int)(remaining / 3600f)}h"
                : remaining < 777600f ? $"{(int)(remaining / 86400f)}d"
                : "9+d";

            float hoverBottom = scy + halfH;

            if (durationLabel != null)
            {
                Vector2 lsz = ImGui.CalcTextSize(durationLabel) * (fontSize / ImGui.GetFontSize());
                float   lx  = sx - lsz.X * 0.5f;
                float   ly  = scy + halfH + textGap;

                dl.AddText(font, fontSize, V(lx + 1f, ly + 1f), WithAlpha(0xCC000000u, barAlpha), durationLabel);
                dl.AddText(font, fontSize, V(lx, ly), WithAlpha(0xFFFFFFFFu, barAlpha), durationLabel);

                hoverBottom = ly + lsz.Y;   // hover/tooltip rect covers the label too, not just the icon
            }

            if (ImGui.IsMouseHoveringRect(V(sx - size * 0.5f, scy - halfH), V(sx + size * 0.5f, hoverBottom), false))
                ImGui.SetTooltip(string.IsNullOrWhiteSpace(description) ? name : $"{name}\n{description}");
        }
    }

    // True if `internalName` is installed and enabled. Rechecked at most every
    // PluginActiveCacheSeconds — InstalledPlugins builds a fresh wrapper per installed plugin on
    // every access, so polling it every frame would be a steady per-frame allocation for nothing
    private bool IsPluginActive(string internalName, ref bool cached, ref float checkedAt, float now)
    {
        if (now - checkedAt < PluginActiveCacheSeconds) return cached;
        checkedAt = now;
        cached    = false;
        foreach (var p in pluginInterface.InstalledPlugins)
        {
            if (p.IsLoaded && p.InternalName == internalName) { cached = true; break; }
        }
        return cached;
    }

    // Appends the target's active Moodles into targetStatusBuffer, up to `max` total. RemainingTime
    // is always 0 (no duration label drawn, same as a permanent native status) — Moodles' IPC only
    // reports each status's configured total length, not time actually remaining, so a countdown
    // here would just be wrong
    private void AppendMoodlesStatuses(nint targetAddress, int max)
    {
        List<MoodlesStatusInfo> statuses;
        try { statuses = moodlesGetStatusesByPtr.InvokeFunc(targetAddress); }
        catch { return; }   // not installed, wrong version, or any other IPC hiccup — skip quietly
        if (statuses == null) return;

        foreach (var s in statuses)
        {
            if (targetStatusBuffer.Count >= max) break;
            if (s.IconID <= 0) continue;
            targetStatusBuffer.Add((0f, s.IconID, s.Title, s.Description));
        }
    }

    // Same as AppendMoodlesStatuses, for Loci. Separate method rather than a shared generic: the
    // two plugins' tuple shapes genuinely differ (IconID's signedness, field count/order), so a
    // forced abstraction would obscure more than two short, near-identical loops would
    private void AppendLociStatuses(nint targetAddress, int max)
    {
        List<LociStatusInfo> statuses;
        try { statuses = lociGetStatusesByPtr.InvokeFunc(targetAddress); }
        catch { return; }
        if (statuses == null) return;

        foreach (var s in statuses)
        {
            if (targetStatusBuffer.Count >= max) break;
            if (s.IconID == 0) continue;
            targetStatusBuffer.Add((0f, (int)s.IconID, s.Title, s.Description));
        }
    }

    // ── Target frame input — draw-list rendering has no ImGui item, so no hover/click state
    // generates on its own; this wires the bar up to input ──

    // Handles both click types; bails if a native context menu is open — otherwise our
    // WantCaptureMouse claim below silently eats the click before the menu's own item sees it.
    // clip=false on IsMouseHoveringRect matters: default(true) clips to ImGui's *current window*,
    // which doesnt exist here (no Begin/End) — left true, the rect clips to nothing, click never fires
    private void HandleTargetFrameClick(Vector2 rectMin, Vector2 rectMax, IGameObject obj, bool allowLeftClickToTarget)
    {
        if (IsVanillaContextMenuOpen()) return;
        if (!ImGui.IsMouseHoveringRect(rectMin, rectMax, false)) return;

        // Local var, not chained off GetIO(): ImGuiIOPtr is a struct — mutating a property
        // straight off a struct-returning call isnt guaranteed to write back to anything real
        var io = ImGui.GetIO();
        io.WantCaptureMouse = true;   // keep the click here, not the game world underneath (camera drag)

        // Left click = new target, like clicking a vanilla ToT frame; not offered on the main
        // bar: left-clicking your own selected target is a no-op in vanilla too
        if (allowLeftClickToTarget && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) { targetManager.Target = obj; return; }

        if (!ImGui.IsMouseClicked(ImGuiMouseButton.Right)) return;

        log.Info($"[SkyrimCompass debug] Target frame right-clicked ({obj.Name.TextValue}) — opening context menu.");
        TryOpenVanillaTargetContextMenu(obj);
    }

    // Opens the same menu a vanilla target/ToT right-click would (Attack, Trade, Mark, etc.) —
    // game builds it from this call. Fully-qualified: this file's IGameObject import brings in
    // its own GameObject too, colliding with FFXIVClientStructs'; each failure below logs which
    // native call returned null
    private unsafe void TryOpenVanillaTargetContextMenu(IGameObject obj)
    {
        if (obj.Address == IntPtr.Zero) { log.Info("[SkyrimCompass debug] Target's Address was zero — can't open context menu."); return; }

        var agentModule = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance();
        if (agentModule == null) { log.Info("[SkyrimCompass debug] AgentModule.Instance() was null — can't open context menu."); return; }

        var hudAgent = (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentHUD*)
            agentModule->GetAgentByInternalId(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.Hud);
        if (hudAgent == null) { log.Info("[SkyrimCompass debug] AgentHUD agent was null — can't open context menu."); return; }

        hudAgent->OpenContextMenuFromTarget((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address);
    }

    // ── Unified marker + FATE render ──

    private void RenderAllMarkers(
        ImDrawListPtr dl,
        float cx, float cy,
        float halfVis, float barHalfW, float lensStr,
        float heading, IPlayerCharacter player, Vector3 originPos)
    {
        var   pp            = originPos;
        float maxDist       = config.MaxMarkerDistance;
        float maxDistSq     = maxDist * maxDist;
        float fateMaxDist   = maxDist * config.FateDistanceMultiplier;
        float fateMaxDistSq = fateMaxDist * fateMaxDist;
        float extHalf       = halfVis * lensStr;

        // Computed once/frame, not re-checked per party-member candidate below
        bool showPartyRoleIcons = config.ShowPartyRoleIcons
            && (!config.PartyRoleIconsOnlyInDuty || IsInDutyOrPvp());

        allCandidates.Clear();

        if (config.ShowAnyMarkers)
        {
            foreach (var obj in objectTable)
            {
                if (obj == null || obj.EntityId == player.EntityId) continue;
                uint col = MarkerColor(obj, player);
                if (col == 0) continue;
                if (!TryComputeBearing(obj.Position, pp, heading, maxDistSq, extHalf,
                                       out float dist, out float delta)) continue;
                allCandidates.Add((obj, null, dist, delta, 1f - dist / maxDist, col));
            }
        }

        if (config.ShowFates)
        {
            foreach (var fate in fateTable)
            {
                if (fate == null) continue;
                if (fate.State != FateState.Running && fate.State != FateState.Preparing) continue;
                if (!TryComputeBearing(fate.Position, pp, heading, fateMaxDistSq, extHalf,
                                       out float dist, out float delta)) continue;
                allCandidates.Add((null, fate, dist, delta, 1f - dist / fateMaxDist, 0u));
            }
        }

        if (allCandidates.Count == 0) return;

        allCandidates.Sort(DistFarFirst);

        foreach (var candidate in allCandidates)
        {
            float delta = candidate.Delta;
            float t     = candidate.T;
            float sx    = cx + Project(delta, halfVis, barHalfW, lensStr);
            float alpha = ComputeFadeAlpha(t) * LensEdgeAlpha(delta, halfVis, extHalf);

            // FATE branch
            if (candidate.Fate is { } fate)
            {
                float fateIconSize = Lerp(config.FateIconMinSize, config.FateIconMaxSize, t);
                bool  drewFateIcon = fate.IconId > 0
                                  && TryDrawIcon(dl, (int)fate.IconId, sx, cy, fateIconSize, alpha);
                if (!drewFateIcon)
                    DrawFilledDot(dl, sx, cy, (3f + 7f * t) * 2f, C(config.FateColor), alpha);
                continue;
            }

            // Game-object branch
            var  obj = candidate.Obj!;
            uint col = candidate.Col;

            int   iconId   = 0;
            float iconSize = 0f;

            bool  isAetheryteKind = ClassifyAetheryte(obj) != AetheryteNameKind.None;
            float npcIconSize     = Lerp(config.NpcQuestIconMinSize, config.NpcQuestIconMaxSize, t) * IconSizeMultiplier;

            if (config.ShowAetheryteIcons && isAetheryteKind)
            {
                iconId   = GetAetheryteIconId(obj);
                iconSize = Lerp(config.AetheryteIconMinSize, config.AetheryteIconMaxSize, t) * AetheryteIconSizeMultiplier;
            }
            else if (obj.ObjectKind == ObjectKind.EventNpc && TryGetNpcIcon(obj, out int npcIcon))
            {
                iconId   = npcIcon;
                iconSize = npcIconSize;
            }
            else if (config.ShowGatheringIcons && obj.ObjectKind == ObjectKind.GatheringPoint)
            {
                int gatherIcon = GetGatheringIconId(obj.BaseId);
                if (gatherIcon > 0)
                {
                    iconId   = gatherIcon;
                    iconSize = Lerp(config.GatheringIconMinSize, config.GatheringIconMaxSize, t);
                }
            }
            else if (config.ShowTreasureIcons && obj.ObjectKind == ObjectKind.Treasure)
            {
                iconId   = config.TreasureIconId;
                iconSize = Lerp(config.TreasureMinSize, config.TreasureMaxSize, t);
            }

            bool drewIcon = iconId > 0 && TryDrawIcon(dl, iconId, sx, cy, iconSize, alpha);

            if (!drewIcon)
            {
                if (obj.ObjectKind == ObjectKind.Pc)
                {
                    float playerSize  = Lerp(config.PartyRoleIconMinSize, config.PartyRoleIconMaxSize, t);
                    bool  drewJobIcon = false;

                    if (showPartyRoleIcons && obj is ICharacter partyChar
                        && (partyChar.StatusFlags & StatusFlags.PartyMember) != 0)
                    {
                        int jobIconId = partyChar.ClassJob.RowId > 0 ? (int)(62000 + partyChar.ClassJob.RowId) : 0;
                        if (jobIconId > 0)
                        {
                            float iconDrawSize = playerSize * IconSizeMultiplier;
                            float iconHalf     = iconDrawSize * 0.5f;
                            uint  roleCol      = GetRoleColor(partyChar);
                            DrawIconRingAndShadow(dl, sx, cy, iconHalf, roleCol, roleCol, alpha);
                            TryDrawIcon(dl, jobIconId, sx, cy, iconDrawSize, alpha);
                            drewJobIcon = true;
                        }
                    }

                    if (!drewJobIcon)
                    {
                        PlayerIconOverride? nameOverride = null;
                        if (config.PlayerIconOverrides.Count > 0)
                        {
                            var objName = obj.Name.TextValue;
                            foreach (var ov in config.PlayerIconOverrides)
                            {
                                if (ov.PlayerName.Length > 0
                                    && string.Equals(ov.PlayerName, objName, StringComparison.OrdinalIgnoreCase))
                                {
                                    nameOverride = ov;
                                    break;
                                }
                            }
                        }

                        if (nameOverride is not null)
                        {
                            float overrideSize = playerSize * IconSizeMultiplier;
                            float overrideHalf = overrideSize * 0.5f;

                            DrawIconRingAndShadow(dl, sx, cy, overrideHalf,
                                nameOverride.ShowBorder ? C(nameOverride.BorderColor) : null,
                                nameOverride.ShowFill   ? C(nameOverride.FillColor)   : null,
                                alpha);

                            bool drewOverrideIcon = nameOverride.IconBaseId > 0
                                && TryDrawIcon(dl, nameOverride.IconBaseId, sx, cy, overrideSize,
                                               alpha, nameOverride.ClipToCircle, nameOverride.SizeMultiplier);

                            if (!drewOverrideIcon)
                            {
                                uint fallbackCol = nameOverride.ShowBorder ? C(nameOverride.BorderColor) : col;
                                DrawFilledDot(dl, sx, cy, playerSize, fallbackCol, alpha);
                            }
                        }
                        else
                        {
                            bool isFriend = config.SolidFriendDots
                                && obj is ICharacter ch
                                && (ch.StatusFlags & StatusFlags.Friend) != 0;

                            if (isFriend) DrawFilledDot(dl, sx, cy, playerSize, col, alpha);
                            else          DrawHollowDot(dl, sx, cy, playerSize, col, alpha);
                        }
                    }
                }
                else
                {
                    // Every remaining kind draws via the same Draw{Filled|Hollow}Dot(Lerp(min,max,t))
                    // shape, so look the (min,max,filled) triple up once instead of repeating the
                    // call per branch; Aetheryte checked first: Firmament crystals are EventNpc-kind
                    // but should style as aetherytes, not quest NPCs
                    (float min, float max, bool filled) dot =
                        isAetheryteKind                         ? (config.AetheryteIconMinSize, config.AetheryteIconMaxSize, true)
                      : obj.ObjectKind == ObjectKind.EventNpc   ? (config.NpcQuestIconMinSize, config.NpcQuestIconMaxSize, false)
                      : obj.ObjectKind == ObjectKind.BattleNpc  ? (config.EnemyMinSize, config.EnemyMaxSize, true)
                      : obj.ObjectKind == ObjectKind.Treasure   ? (config.TreasureMinSize, config.TreasureMaxSize, true)
                      : (6f, 20f, true);   // generic fallback — was the hand-inlined r=3+7t radius, doubled

                    float dotSize = Lerp(dot.min, dot.max, t);
                    if (dot.filled) DrawFilledDot(dl, sx, cy, dotSize, col, alpha);
                    else            DrawHollowDot(dl, sx, cy, dotSize, col, alpha);
                }
            }
        }
    }

    // Three-zone distance fade: opaque inside DotNearZone, smoothstep to DotMidAlpha in the
    // middle band, smoothstep to 0 below DotFarZone; t=1 at zero distance, 0 at max range
    private float ComputeFadeAlpha(float t)
    {
        float nearZone = config.DotNearZone;
        float midEnd   = config.DotFarZone;
        float midAlpha = config.DotMidAlpha;

        if (t >= nearZone) return 1f;
        if (t >= midEnd)
            return midAlpha + (1f - midAlpha) * SmoothStep((t - midEnd) / (nearZone - midEnd));
        return midAlpha * SmoothStep(t / midEnd);
    }

    // Draws a game icon centred at (sx, cy). `size` is the icon's width; height follows the
    // source texture's real aspect ratio — a no-op for the square item/action/etc. icons used
    // everywhere else in this file, but status icons are taller than wide, and forcing those
    // into a square box squishes them. Returns false if texture not yet loaded.
    // clipToCircle=true: quad stays square at `size`, uvZoom crops the texture (fits a border ring).
    // clipToCircle=false: uvZoom scales the quad itself; uvZoom=1.0 → no zoom either way
    private bool TryDrawIcon(
        ImDrawListPtr dl, int iconId, float sx, float cy, float size, float alpha,
        bool clipToCircle = false, float uvZoom = 1.0f)
    {
        if (!textureProvider.TryGetFromGameIcon(new GameIconLookup((uint)iconId), out var sharedTex))
            return false;

        var  tex  = sharedTex.GetWrapOrEmpty();
        uint tint = WithAlpha(0xFFFFFFFFu, alpha);

        float   halfW, halfH;
        Vector2 uvMin, uvMax;

        if (clipToCircle)
        {
            halfW = halfH = size * 0.5f;
            float uvHalf = 0.5f / Math.Max(0.01f, uvZoom);
            uvMin = new(0.5f - uvHalf, 0.5f - uvHalf);
            uvMax = new(0.5f + uvHalf, 0.5f + uvHalf);
        }
        else
        {
            halfW = size * 0.5f * Math.Max(0.01f, uvZoom);
            halfH = halfW * (tex.Size.X > 0f ? tex.Size.Y / tex.Size.X : 1f);
            uvMin = new(0f, 0f);
            uvMax = new(1f, 1f);
        }

        PushUnclip(dl);
        dl.AddImageRounded(
            tex.Handle,
            V(sx - halfW, cy - halfH),
            V(sx + halfW, cy + halfH),
            uvMin, uvMax, tint,
            clipToCircle ? halfW : 0f,
            ImDrawFlags.RoundCornersAll);
        PopUnclip(dl);
        return true;
    }

    // Same aspect lookup TryDrawIcon uses internally — exposed for callers that need to know
    // an icon's real height for layout (e.g. positioning a tooltip rect) without a `tex` in hand
    private float GetIconAspect(int iconId)
    {
        if (!textureProvider.TryGetFromGameIcon(new GameIconLookup((uint)iconId), out var sharedTex)) return 1f;
        var size = sharedTex.GetWrapOrEmpty().Size;
        return size.X > 0f ? size.Y / size.X : 1f;
    }

    // GatheringPoint(BaseId) → GatheringPointBase → GatheringType → IconMain.
    // Cached permanently per BaseId; returns 0 if any link in the chain doesnt resolve
    private int GetGatheringIconId(uint baseId)
    {
        if (gatheringIconCache.TryGetValue(baseId, out int cached)) return cached;

        int icon = 0;
        if (gatheringPointSheet.GetRowOrDefault(baseId) is { } gp
            && gatheringPointBaseSheet.GetRowOrDefault(gp.GatheringPointBase.RowId) is { } gpb
            && gatheringTypeSheet.GetRowOrDefault(gpb.GatheringType.RowId) is { } gt)
            icon = gt.IconMain;

        return gatheringIconCache[baseId] = icon;
    }

    // Uses ClassJob.Role (not a per-job index) so future jobs work automatically; Tank=blue,
    // Healer=green, DPS=red, DoH/DoL=gray — matches FFXIV's role UI
    private uint GetRoleColor(ICharacter character)
    {
        if (classJobSheet.GetRowOrDefault(character.ClassJob.RowId) is not { } row)
            return C(new Vector4(0.54f, 0.54f, 0.54f, 0.85f));
        return row.Role switch
        {
            1      => C(new Vector4(0.36f, 0.48f, 0.76f, 0.90f)),   // Tank — blue
            2 or 3 => C(new Vector4(0.84f, 0.30f, 0.30f, 0.90f)),   // DPS  — red
            4      => C(new Vector4(0.30f, 0.69f, 0.49f, 0.90f)),   // Healer — green
            _      => C(new Vector4(0.54f, 0.54f, 0.54f, 0.85f)),   // DoH/DoL — gray
        };
    }

    // Resolves an NPC's English Title via ENpcResident, cached per BaseId; "" if none
    private string GetTitle(uint baseId)
    {
        if (titleCache.TryGetValue(baseId, out string? cached)) return cached;
        string v = npcSheet.GetRowOrDefault(baseId) is { } row ? row.Title.ToString() : "";
        return titleCache[baseId] = v;
    }

    private string GetSingular(uint baseId)
    {
        if (singularCache.TryGetValue(baseId, out string? cached)) return cached;
        string v = npcSheet.GetRowOrDefault(baseId) is { } row ? row.Singular.ToString() : "";
        return singularCache[baseId] = v;
    }

    private static bool HasKeyword(string text, string[] keywords)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var kw in keywords)
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Checks both Title and Singular — see the titleCache/singularCache comment above for why
    private bool MatchesKeyword(uint baseId, string[] keywords) =>
        HasKeyword(GetTitle(baseId), keywords) || HasKeyword(GetSingular(baseId), keywords);

    private bool IsMender(IGameObject o)      => MatchesKeyword(o.BaseId, MenderKeywords);
    private bool IsShop(IGameObject o)        => MatchesKeyword(o.BaseId, ShopKeywords);
    private bool IsSkipper(IGameObject o)     => MatchesKeyword(o.BaseId, SkipperKeywords);
    private bool IsTicketer(IGameObject o)    => MatchesKeyword(o.BaseId, TicketerKeywords);
    private bool IsChocoboKeep(IGameObject o) => MatchesKeyword(o.BaseId, ChocoboKeepKeywords);

    // Priority: live quest marker, then each keyword category; first match wins (data walk, not an if/else chain)
    private bool TryGetNpcIcon(IGameObject obj, out int iconId)
    {
        if (config.ShowNpcQuestIcons && npcMarkerIcons.TryGetValue(obj.GameObjectId, out iconId)) return true;
        if (config.ShowMenderIcons && IsMender(obj))          { iconId = config.MenderIconId; return true; }
        if (config.ShowShopIcons && IsShop(obj))              { iconId = config.ShopIconId; return true; }
        if (config.ShowFastTravelIcons && IsSkipper(obj))     { iconId = config.FastTravelIconId; return true; }
        if (config.ShowFastTravelIcons && IsTicketer(obj))    { iconId = config.FastTravelTicketerIconId; return true; }
        if (config.ShowFastTravelIcons && IsChocoboKeep(obj)) { iconId = config.ChocoboKeepIconId; return true; }
        iconId = 0;
        return false;
    }

    private enum AetheryteNameKind { None, Big, Shard }

    // Aetheryte kind → Big or Shard (Shard if name matches AethernetShardName). EventNpc/EventObj
    // → Shard only on match, else None; single source of truth for icon selection + visibility
    private AetheryteNameKind ClassifyAetheryte(IGameObject obj)
    {
        bool looksLikeShard = !string.IsNullOrEmpty(config.AethernetShardName)
            && obj.Name.TextValue.Contains(config.AethernetShardName, StringComparison.OrdinalIgnoreCase);

        if (obj.ObjectKind == ObjectKind.Aetheryte)
            return looksLikeShard ? AetheryteNameKind.Shard : AetheryteNameKind.Big;

        return looksLikeShard ? AetheryteNameKind.Shard : AetheryteNameKind.None;
    }

    private int GetAetheryteIconId(IGameObject obj) =>
        ClassifyAetheryte(obj) == AetheryteNameKind.Shard
            ? config.AethernetShardIconId
            : config.AetheryteIconId;

    // Returns true if obj is any aetheryte kind; color=0 if hidden by config
    private bool TryGetAetheryteMarkerColor(IGameObject obj, out uint color)
    {
        var kind = ClassifyAetheryte(obj);
        if (kind == AetheryteNameKind.None) { color = 0u; return false; }
        bool hidden = !config.ShowAetherytes
            || (kind == AetheryteNameKind.Shard && !config.ShowAethernetShards);
        color = hidden ? 0u : C(config.AetheryteColor);
        return true;
    }

    private uint MarkerColor(IGameObject obj, IPlayerCharacter player)
    {
        switch (obj.ObjectKind)
        {
            case ObjectKind.Pc:
                return config.ShowPlayers ? MarkerBaseColor(obj) : 0u;

            case ObjectKind.BattleNpc:
                if (!config.ShowEnemies) return 0u;
                if (obj is not IBattleNpc bnpc || bnpc.BattleNpcKind != BattleNpcSubKind.Combatant) return 0u;
                // "Engaged" = enemy AND player both InCombat — not "targeting me", which missed
                // enemies focusing party mates; a pull sets the whole party's InCombat together, so
                // this covers the fight, not just the player's own targeting relationship
                if (config.EnemiesOnlyIfEngaged
                    && !(bnpc.StatusFlags.HasFlag(StatusFlags.InCombat) && player.StatusFlags.HasFlag(StatusFlags.InCombat)))
                    return 0u;
                return MarkerBaseColor(obj);

            case ObjectKind.EventNpc:
                // Firmament crystals are EventNpcs — route through aetheryte path, not NPC color
                if (TryGetAetheryteMarkerColor(obj, out uint eventNpcAetherCol)) return eventNpcAetherCol;
                if (!config.ShowNpcs) return 0u;
                if (config.NpcsOnlyIfTargetable && !obj.IsTargetable) return 0u;
                return MarkerBaseColor(obj);

            case ObjectKind.EventObj:
                // Housing-ward Aethernet shards are EventObj (not EventNpc)
                return TryGetAetheryteMarkerColor(obj, out uint eventObjAetherCol)
                    ? eventObjAetherCol : 0u;

            case ObjectKind.GatheringPoint:
                if (!config.ShowGatheringNodes) return 0u;
                if (config.GatheringOnlyIfTargetable && !obj.IsTargetable) return 0u;
                return MarkerBaseColor(obj);

            case ObjectKind.Treasure:
                return config.ShowTreasure ? MarkerBaseColor(obj) : 0u;

            case ObjectKind.Aetheryte:
                TryGetAetheryteMarkerColor(obj, out uint realAetherCol); // always Big/Shard, never None
                return realAetherCol;

            default:
                return 0u;
        }
    }

    // Plain per-kind color behind MarkerColor, no Show*/OnlyIfTargetable/OnlyIfEngaged gating —
    // lets the target bar reuse the same colors without inheriting compass declutter rules (a
    // selected target shouldnt vanish just because dots are hidden); safe to call from
    // anywhere; 0u for anything with no obvious dot-color equivalent
    private uint MarkerBaseColor(IGameObject obj) => obj.ObjectKind switch
    {
        ObjectKind.Pc                                                  => C(config.PlayerColor),
        ObjectKind.BattleNpc when obj is IBattleNpc b
            && b.BattleNpcKind == BattleNpcSubKind.Combatant            => C(config.EnemyColor),
        ObjectKind.EventNpc                                            => C(config.NpcColor),
        ObjectKind.GatheringPoint                                      => C(config.GatheringColor),
        ObjectKind.Treasure                                            => C(config.TreasureColor),
        _                                                               => 0u,
    };

    // ── Helpers ──

    private static float SmoothStep(float x) => x * x * (3f - 2f * x);

    private static float Normalize(float a)
    {
        a %= 360f;
        return a < 0f ? a + 360f : a;
    }

    private static float Delta(float from, float to)
    {
        float d = to - from;
        while (d >  180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    // 3D distance for range/fade; 2D bearing (no Y) so height doesnt shift dots sideways;
    // returns false if out of range or outside the visible FOV
    private static bool TryComputeBearing(
        Vector3 targetPos, Vector3 originPos, float heading, float maxDistSq, float extHalf,
        out float dist, out float delta)
    {
        float dx  = targetPos.X - originPos.X;
        float dy  = targetPos.Y - originPos.Y;
        float dz  = targetPos.Z - originPos.Z;
        float dsq = dx * dx + dy * dy + dz * dz;

        dist = 0f; delta = 0f;
        if (dsq > maxDistSq || dsq < 0.25f) return false;

        float bearing = Normalize(MathF.Atan2(dx, -dz) * (180f / MathF.PI));
        delta = Delta(heading, bearing);
        if (MathF.Abs(delta) > extHalf) return false;

        dist = MathF.Sqrt(dsq);
        return true;
    }

    private static Vector2 V(float x, float y) => new(x, y);
    private static uint     C(Vector4 v)        => ImGui.ColorConvertFloat4ToU32(v);

    // t=1 → max, t=0 → min
    private static float Lerp(float min, float max, float t) => min + (max - min) * t;

    // Filled: solid disc + 0x66 shadow ring. Hollow: 2px ring + fainter 0x33 shadow ring
    private static void DrawFilledDot(ImDrawListPtr dl, float sx, float cy, float size, uint col, float alpha) =>
        DrawDot(dl, sx, cy, size, col, alpha, filled: true);
    private static void DrawHollowDot(ImDrawListPtr dl, float sx, float cy, float size, uint col, float alpha) =>
        DrawDot(dl, sx, cy, size, col, alpha, filled: false);

    private static void DrawDot(ImDrawListPtr dl, float sx, float cy, float size, uint col, float alpha, bool filled)
    {
        float r = size * 0.5f;
        if (filled) dl.AddCircleFilled(V(sx, cy), r, WithAlpha(col, alpha));
        else        dl.AddCircle(V(sx, cy), r, WithAlpha(col, alpha), 0, 2.0f);
        dl.AddCircle(V(sx, cy), r + 0.8f, WithAlpha(filled ? 0x66000000u : 0x33000000u, alpha));
    }

    // 3 inward-fading circles faking a soft shadow behind an icon (role icon / override fill)
    private static void DrawInwardShadow(ImDrawListPtr dl, float sx, float cy, float half, uint col, float alpha)
    {
        dl.AddCircleFilled(V(sx, cy), half * 0.85f, WithAlpha(col, alpha * 0.6f));
        dl.AddCircleFilled(V(sx, cy), half * 0.65f, WithAlpha(col, alpha * 0.4f));
        dl.AddCircleFilled(V(sx, cy), half * 0.45f, WithAlpha(col, alpha * 0.2f));
    }

    // Solid ring just outside an icon's bounding box (role icon / override border)
    private static void DrawOuterRing(ImDrawListPtr dl, float sx, float cy, float half, uint col, float alpha) =>
        dl.AddCircle(V(sx, cy), half + 1.0f, WithAlpha(col, alpha), 0, 3.0f);

    // Optional ring + inward shadow (role icon / override), Push/PopUnclip-bracketed so both
    // escape the bar's clip edge. Null skips a layer; disjoint radii, so draw order (unlike
    // most layered draws here) doesnt matter
    private static void DrawIconRingAndShadow(
        ImDrawListPtr dl, float sx, float cy, float half, uint? ringCol, uint? shadowCol, float alpha)
    {
        if (ringCol is null && shadowCol is null) return;
        PushUnclip(dl);
        if (shadowCol is { } sc) DrawInwardShadow(dl, sx, cy, half, sc, alpha);
        if (ringCol   is { } rc) DrawOuterRing(dl, sx, cy, half, rc, alpha);
        PopUnclip(dl);
    }

    // 1.0 inside linearHalf, smoothsteps to 0 at extHalf. linearHalf lets labels fade earlier than ticks
    private static float LensEdgeAlpha(float delta, float linearHalf, float extHalf)
    {
        float absD = MathF.Abs(delta);
        if (absD <= linearHalf) return 1f;
        return 1f - SmoothStep(MathF.Min(1f, (absD - linearHalf) / (extHalf - linearHalf)));
    }

    private static uint WithAlpha(uint color, float mul)
    {
        uint newA = (uint)(((color >> 24) & 0xFFu) * Math.Clamp(mul, 0f, 1f));
        return (color & 0x00FFFFFFu) | (newA << 24);
    }

    // Temporarily overrides bar-sized clip so icons/rings can render past the bar edge.
    // Icons and their rings must escape together or they visually disagree at the edge
    private static void PushUnclip(ImDrawListPtr dl) =>
        dl.PushClipRect(Vector2.Zero, ImGui.GetIO().DisplaySize, false);

    private static void PopUnclip(ImDrawListPtr dl) => dl.PopClipRect();

    // Logs nearby objects for diagnostics. View via /xllog
    public void DumpNearbyObjects(float radius = 50f)
    {
        var player = objectTable.LocalPlayer;
        if (player == null) { log.Info("[SkyrimCompass debug] No local player — are you logged in?"); return; }

        var pp     = player.Position;
        var nearby = new List<(float dist, IGameObject obj)>();

        foreach (var obj in objectTable)
        {
            if (obj == null || obj.EntityId == player.EntityId) continue;
            float dx = obj.Position.X - pp.X;
            float dy = obj.Position.Y - pp.Y;
            float dz = obj.Position.Z - pp.Z;
            float dist = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (dist <= radius)
                nearby.Add((dist, obj));
        }

        nearby.Sort((a, b) => a.dist.CompareTo(b.dist));
        log.Info($"[SkyrimCompass debug] {nearby.Count} object(s) within {radius}y — nearest first:");

        foreach (var (dist, obj) in nearby)
        {
            string extra = "";
            if (obj.ObjectKind == ObjectKind.EventNpc && npcSheet.GetRowOrDefault(obj.BaseId) is { } npcRow)
                extra = $" | Singular=\"{npcRow.Singular.ToString()}\" | Plural=\"{npcRow.Plural.ToString()}\"";

            log.Info($"[SkyrimCompass debug] {dist,6:F1}y | Kind={obj.ObjectKind,-19} | " +
                     $"BaseId={obj.BaseId,-8} | Name=\"{obj.Name.TextValue}\"{extra}");
        }
        log.Info("[SkyrimCompass debug] Done. Use /xllog in-game to view the log window.");
    }
}
