using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiSeStringRenderer;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel;
using Lumina.Excel.Sheets;

// Moodles/Loci IPC tuples (shape-only copies)
using MoodlesStatusInfo = (int Version, System.Guid GUID, int IconID, string Title, string Description, string CustomVFXPath,
    long ExpireTicks, int Type, int Stacks, int StackSteps, uint Modifiers, System.Guid ChainedStatus,
    int ChainTrigger, string Applier, string Dispeller, bool Permanent);
using LociStatusInfo = (int Version, System.Guid GUID, uint IconID, string Title, string Description, string CustomVFXPath,
    long ExpireTicks, byte Type, int Stacks, int StackSteps, int StackToChain, uint Modifiers,
    System.Guid ChainedGUID, byte ChainType, int ChainTrigger, string Applier, string Dispeller);

namespace SkyrimCompass;

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

    // IPC subscribers & version gates (unified via generic helpers)
    private readonly ICallGateSubscriber<nint, List<MoodlesStatusInfo>> moodlesGetStatusesByPtr;
    private readonly ICallGateSubscriber<nint, List<LociStatusInfo>>    lociGetStatusesByPtr;
    private readonly ICallGateSubscriber<int>        moodlesVersion;
    private readonly ICallGateSubscriber<(int, int)> lociApiVersion;

    private PluginIpcState moodlesIpc = new("Moodles", 4, PluginIpcKind.Moodles);
    private PluginIpcState lociIpc   = new("Loci",    0, PluginIpcKind.Loci);

    // Fade-out states (LB and cast) unified by UpdateFadeOut
    private float lbTrackedProgress  = 0f;
    private float lbFrozenProgress   = 0f;
    private float lbFadeOutStartTime = -1f;
    private const float LbFadeOutDuration = 2f;
    private const float LbDropThreshold   = 0.4f;

    private ulong castWipeTargetId     = 0;
    private float castTrackedProgress  = 0f;
    private float castFrozenProgress   = 0f;
    private float castFadeOutStartTime = -1f;
    private const float CastFadeOutDuration = 0.4f;

    // Target HP bar
    private ulong lastTargetBarObjectId = 0;
    private float displayedTargetHpFrac = 1f;
    private float lastRawTargetHpFrac   = 1f;
    private float targetBarFlashAlpha   = 0f;

    // Target status buffer (reused)
    private readonly List<(float Remaining, int Icon, string Name, string Desc, System.Guid Guid)> targetStatusBuffer = new();

    // Duration tracker for Moodles/Loci
    private readonly Dictionary<System.Guid, (float FirstSeen, long TotalMs)> statusDurationTracker = new();
    private float nextDurationTrackerPruneAt;

    // Cached payloads (throttled)
    private List<MoodlesStatusInfo>? cachedMoodles;
    private nint  cachedMoodlesTarget = IntPtr.Zero;
    private float cachedMoodlesFetchedAt = -1000f;
    private List<LociStatusInfo>? cachedLoci;
    private nint  cachedLociTarget = IntPtr.Zero;
    private float cachedLociFetchedAt = -1000f;
    private const float StatusPayloadCacheSeconds = 0.12f;

    // Tooltip cache
    private readonly Dictionary<(string Name, string Desc), byte[]> formattedTooltipCache = new();

    // Context menu fade
    private bool  contextMenuWasOpen;
    private float contextMenuFadeChangeTime = -1000f;
    private const float ContextMenuFadeSeconds = 0.15f;
    private const float ContextMenuDimmedAlpha = 0.33f;

    // Nameplate marker icons
    private readonly Dictionary<ulong, int> npcMarkerIcons = new();

    // Static data caches
    private readonly Dictionary<uint, int> gatheringIconCache = new();
    private readonly Dictionary<uint, NpcCategory> npcCategoryCache = new();
    private readonly Dictionary<uint, string> titleCache = new();
    private readonly Dictionary<uint, string> singularCache = new();
    private readonly Dictionary<uint, uint> roleColorCache = new(); // RowId → RGBA (packed)
    private readonly Dictionary<uint, string> actionNameCache = new();

    private readonly ExcelSheet<GatheringPoint>     gatheringPointSheet;
    private readonly ExcelSheet<GatheringPointBase> gatheringPointBaseSheet;
    private readonly ExcelSheet<GatheringType>      gatheringTypeSheet;
    private readonly ExcelSheet<ENpcResident>        npcSheet;
    private readonly ExcelSheet<ClassJob>            classJobSheet;
    private readonly ExcelSheet<Lumina.Excel.Sheets.Action> actionSheet;

    private static readonly string[] MenderKeywords    = { "Mender", "Tinker", "Repairman" };
    private static readonly string[] ShopKeywords = { "Merchant", "Vendor", "Trader", "Sutler", "Supplier", "Junkmonger",
        "Fishmonger", "Dyemonger", "Jeweler", "Apothecary", "Culinarian", "Salvager", "Exchange", "Clothier",
        "Outfitter", "Peddler", "Dealer", "Armorer", "Shopkeep", "Stallkeeper", "Pawnbroker", "Provisioner",
        "Broker", "Proprietor", "Proprietress", "Marketeer", "Weaponsmith", "Tailor", "Herbalist", "Craftsman", "Appraiser" };
    private static readonly string[] SkipperKeywords   = { "Skipper", "Ferryman" };
    private static readonly string[] TicketerKeywords  = { "Ticketer", "Pilot", "Crewman", "Steward" };
    private static readonly string[] ChocoboKeepKeywords = { "Chocobokeep", "Falcon Porter" };

    private readonly List<(IGameObject? Obj, IFate? Fate, float Dist, float Delta, float T, uint Col, AetheryteNameKind AetheryteKind)> allCandidates = new();
    private static readonly Comparison<(IGameObject? Obj, IFate? Fate, float Dist, float Delta, float T, uint Col, AetheryteNameKind AetheryteKind)> DistFarFirst = (a, b) => b.Dist.CompareTo(a.Dist);

    private const float IconSizeMultiplier          = 1.5f;
    private const float AetheryteIconSizeMultiplier = 1.75f;
    private const float MainBarShareWithTot = 0.65f;
    private const float TargetBarRowGapFraction = 0.03f;

    private static readonly (float Deg, string Label, bool IsMajor)[] Directions =
    [
        (0f,   "N",  true), (45f,  "NE", false), (90f,  "E",  true), (135f, "SE", false),
        (180f, "S",  true), (225f, "SW", false), (270f, "W",  true), (315f, "NW", false),
    ];

    // IPC helper enums / struct
    private enum PluginIpcKind { Moodles, Loci }
    private class PluginIpcState
    {
        public string Name;
        public int MinimumVersion;
        public PluginIpcKind Kind;
        public bool Active;
        public float ActiveCheckedAt = -1000f;
        public bool VersionOk;
        public float VersionCheckedAt = -1000f;
        public PluginIpcState(string name, int minVer, PluginIpcKind kind) { Name = name; MinimumVersion = minVer; Kind = kind; }
    }

    private const float PluginActiveCacheSeconds = 5f;  // <-- added missing constant

    public CompassHud(IClientState clientState, IObjectTable objectTable, ITargetManager targetManager,
        INamePlateGui namePlateGui, ITextureProvider textureProvider, IFateTable fateTable,
        ICondition condition, IGameGui gameGui, IDataManager dataManager, Configuration config,
        IPluginLog log, IFontHandle jupiterFont, IDalamudPluginInterface pluginInterface)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.targetManager = targetManager;
        this.namePlateGui = namePlateGui;
        this.textureProvider = textureProvider;
        this.fateTable = fateTable;
        this.condition = condition;
        this.gameGui = gameGui;
        this.config = config;
        this.log = log;
        this.jupiterFont = jupiterFont;
        this.pluginInterface = pluginInterface;

        moodlesGetStatusesByPtr = pluginInterface.GetIpcSubscriber<nint, List<MoodlesStatusInfo>>("Moodles.GetStatusManagerInfoByPtrV2");
        lociGetStatusesByPtr = pluginInterface.GetIpcSubscriber<nint, List<LociStatusInfo>>("Loci.GetManagerInfoByPtr");
        moodlesVersion = pluginInterface.GetIpcSubscriber<int>("Moodles.Version");
        lociApiVersion = pluginInterface.GetIpcSubscriber<(int, int)>("Loci.ApiVersion");

        gatheringPointSheet = dataManager.GetExcelSheet<GatheringPoint>();
        gatheringPointBaseSheet = dataManager.GetExcelSheet<GatheringPointBase>();
        gatheringTypeSheet = dataManager.GetExcelSheet<GatheringType>();
        npcSheet = dataManager.GetExcelSheet<ENpcResident>(ClientLanguage.English);
        classJobSheet = dataManager.GetExcelSheet<ClassJob>();
        actionSheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();

        namePlateGui.OnDataUpdate += OnNamePlateDataUpdate;
    }

    public void Dispose() => namePlateGui.OnDataUpdate -= OnNamePlateDataUpdate;

    private void OnNamePlateDataUpdate(INamePlateUpdateContext ctx, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        npcMarkerIcons.Clear();
        foreach (var h in handlers)
            if (h.MarkerIconId > 0)
                npcMarkerIcons[h.GameObjectId] = h.MarkerIconId;
    }

    // ─── Public entry ────────────────────────────────────────────────
    public unsafe void Draw()
    {
        if (!config.Enabled) return;
        if (config.HideDuringCutscenes && (condition[ConditionFlag.OccupiedInCutSceneEvent] ||
            condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78]))
            return;

        var player = objectTable.LocalPlayer;
        if (player == null) return;

        float headingRad = 0f;
        var originPos = player.Position;
        bool gotHeading = false;

        if (config.UseCameraDirection)
        {
            var cm = CameraManager.Instance();
            var camera = cm != null ? cm->Camera : null;
            if (camera != null && !float.IsNaN(camera->DirH))
            {
                headingRad = -camera->DirH;
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
        if (!gotHeading)
        {
            if (float.IsNaN(player.Rotation)) return;
            headingRad = MathF.PI - player.Rotation;
        }

        float heading = Normalize(headingRad * (180f / MathF.PI) + config.RotationOffset);

        var io = ImGui.GetIO();
        var dl = ImGui.GetForegroundDrawList();
        float now = (float)ImGui.GetTime();

        float bw = config.CompassWidth;
        float bh = config.CompassHeight;
        float bx = (io.DisplaySize.X - bw) * 0.5f + config.XOffset;
        float by = config.YOffset;

        // Cache duty/pvp state once per frame
        bool inDutyOrPvp = IsInDutyOrPvp();

        RenderBar(dl, bx, by, bw, bh, heading, player, originPos, now, inDutyOrPvp);

        float barAlpha = UpdateContextMenuFadeAlpha(now);

        var curTarget = targetManager.Target;
        var curTot = curTarget?.TargetObject;
        bool hasTot = config.ShowTargetOfTargetBar && curTarget != null && curTot != null
            && curTot.GameObjectId != curTarget.GameObjectId && curTot is ICharacter;

        var (mainX, mainW, totX, totW, rowW) = SplitTargetBarRow(bx, bw, hasTot);
        float rowGap = MathF.Max(2f, bh * 0.12f);
        float tbRowY = by + bh + rowGap;
        float nameCx = bx + bw * 0.5f;

        float targetNameBottom = tbRowY;
        if (config.ShowTargetBar)
            targetNameBottom = RenderTargetBar(dl, mainX, mainW, tbRowY, nameCx, rowW, now, barAlpha, inDutyOrPvp);
        if (hasTot)
            RenderTargetOfTargetBar(dl, totX, totW, tbRowY, player, now, barAlpha, inDutyOrPvp);

        if (config.ShowTargetStatuses && curTarget is IBattleChara targetChara)
            RenderTargetStatuses(dl, targetChara, nameCx, targetNameBottom, barAlpha);
    }

    // ─── Context menu fade ──────────────────────────────────────────
    private float UpdateContextMenuFadeAlpha(float now)
    {
        bool menuOpen = IsVanillaContextMenuOpen();
        if (menuOpen != contextMenuWasOpen) { contextMenuFadeChangeTime = now; contextMenuWasOpen = menuOpen; }
        float t = ContextMenuFadeSeconds > 0f ? Math.Clamp((now - contextMenuFadeChangeTime) / ContextMenuFadeSeconds, 0f, 1f) : 1f;
        float from = menuOpen ? 1f : ContextMenuDimmedAlpha;
        float to   = menuOpen ? ContextMenuDimmedAlpha : 1f;
        return Lerp(from, to, SmoothStep(t));
    }

    private bool IsVanillaContextMenuOpen() =>
        gameGui.GetAddonByName("ContextMenu").IsVisible || gameGui.GetAddonByName("AddonContextSub").IsVisible;

    // ─── Lens projection ────────────────────────────────────────────
    private static float Project(float delta, float halfVis, float barHalfW, float lensStr)
    {
        float extHalf = halfVis * lensStr;
        float absD = MathF.Min(MathF.Abs(delta), extHalf);
        float u = absD / extHalf;
        float f = 1f - MathF.Pow(1f - u, lensStr);
        return (delta >= 0f ? 1f : -1f) * barHalfW * f;
    }

    // ─── Main bar render ────────────────────────────────────────────
    private void RenderBar(ImDrawListPtr dl, float bx, float by, float bw, float bh, float heading,
        IPlayerCharacter player, Vector3 originPos, float now, bool inDutyOrPvp)
    {
        float cx = bx + bw * 0.5f, cy = by + bh * 0.5f;
        float barHalfW = bw * 0.5f;
        float halfVis = config.VisibleDegrees * 0.5f;
        float lensStr = config.LensStrength;
        float extHalf = halfVis * lensStr;

        uint bgCol = C(config.BackgroundColor);
        uint borderCol = C(config.BorderColor);
        uint tickCol = C(config.TickColor);
        uint cardCol = C(config.CardinalColor);
        uint ixCol = C(config.IntercardinalColor);
        uint solidBgCol = (bgCol & 0x00FFFFFFu) | 0xFF000000u;

        float capHW = bh * 0.44f, capHH = bh * 0.64f;

        // Limit break glow
        float rawLb = GetLimitBreakProgress();
        float displayedLb = UpdateFadeOut(ref lbTrackedProgress, ref lbFrozenProgress, ref lbFadeOutStartTime,
            rawLb, rawLb < lbTrackedProgress - LbDropThreshold, false, true, now, LbFadeOutDuration, out float lbWipe);
        float lbProgress = config.ShowLimitBreakGlow ? displayedLb : 0f;
        if (!config.ShowLimitBreakGlow) lbWipe = 0f;

        // Background and vignette
        dl.AddRectFilled(V(bx, by), V(bx + bw, by + bh), bgCol);
        uint warmGlow = ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.70f, 0.35f, 0.08f));
        float gw = bw * 0.22f;
        dl.AddRectFilledMultiColor(V(cx - gw, by), V(cx, by + bh), 0u, warmGlow, warmGlow, 0u);
        dl.AddRectFilledMultiColor(V(cx, by), V(cx + gw, by + bh), warmGlow, 0u, 0u, warmGlow);
        dl.AddRectFilledMultiColor(V(bx, by), V(bx + bw * 0.14f, by + bh), 0xAA000000u, 0u, 0u, 0xAA000000u);
        dl.AddRectFilledMultiColor(V(bx + bw * 0.86f, by), V(bx + bw, by + bh), 0u, 0xAA000000u, 0xAA000000u, 0u);
        dl.AddLine(V(bx + 1f, by + 1f), V(bx + bw - 1f, by + 1f), 0x1AFFFFFF, 1f);

        dl.AddRect(V(bx, by), V(bx + bw, by + bh), borderCol, 0f, ImDrawFlags.None, 1.5f);

        // LB glow layers
        if (lbProgress > 0f)
        {
            float glowT = now;
            (float bar, float tMul, float tOff, Vector4 col)[] layers =
            {
                (Math.Clamp(lbProgress, 0f, 1f), 1.00f, 0.0f, config.LimitBreakGlowColor),
                (Math.Clamp(lbProgress - 1f, 0f, 1f), 1.60f, 3.7f, config.LimitBreakGlowColor2),
                (Math.Clamp(lbProgress - 2f, 0f, 1f), 0.65f, 7.1f, config.LimitBreakGlowColor3),
            };
            foreach (var (bar, tMul, tOff, col) in layers)
            {
                if (bar <= 0f) continue;
                float segW = bw * 0.5f * bar;
                uint c = C(col);
                float i = PulseIntensity(glowT * tMul + tOff);
                DrawBorderGlowBracket(dl, bx, by, bw, bh, segW, c, i, glowT * tMul + tOff, lbWipe, bar, true);
                DrawBorderGlowBracket(dl, bx, by, bw, bh, segW, c, i, glowT * tMul + tOff, lbWipe, bar, false);
            }
        }

        dl.PushClipRect(V(bx + 1f, by), V(bx + bw - 1f, by + bh), true);
        using var jupiterScope = jupiterFont.Available ? jupiterFont.Push() : null;
        float fontSize = ImGui.GetFontSize() * config.FontScale;
        var font = ImGui.GetFont();
        float labelTop = by + bh * 0.12f;
        float labelHeight = ImGui.CalcTextSize("N").Y * config.FontScale;
        float maxTickHeight = MathF.Max(2f, (by + bh - 1f) - (labelTop + labelHeight));

        // Ticks
        for (int d = 0; d < 360; d += 5)
        {
            float delta = Delta(heading, d);
            if (MathF.Abs(delta) > extHalf + 2f) continue;
            float sx = cx + Project(delta, halfVis, barHalfW, lensStr);
            bool is90 = d % 90 == 0, is45 = d % 45 == 0, is10 = d % 10 == 0;
            float th = is90 ? bh * 0.52f : is45 ? bh * 0.36f : is10 ? bh * 0.22f : bh * 0.13f;
            th = MathF.Min(th, maxTickHeight);
            float lensA = LensEdgeAlpha(delta, halfVis, extHalf);
            uint tickDraw = WithAlpha(is90 ? cardCol : tickCol, lensA);
            dl.AddLine(V(sx, by + bh - th - 1f), V(sx, by + bh - 1f), tickDraw, is90 ? 2f : 1f);
        }

        // Direction labels
        foreach (var (deg, label, isMajor) in Directions)
        {
            float delta = Delta(heading, deg);
            if (MathF.Abs(delta) > extHalf + 10f) continue;
            float sx = cx + Project(delta, halfVis, barHalfW, lensStr);
            var tsz = ImGui.CalcTextSize(label) * config.FontScale;
            float tx = sx - tsz.X * 0.5f;
            float lensA = LensEdgeAlpha(delta, halfVis * 0.88f, extHalf);
            uint col = WithAlpha(isMajor ? cardCol : ixCol, lensA);
            uint shadow = WithAlpha(0xBB000000u, lensA);
            dl.AddText(font, fontSize, V(tx + 1f, labelTop + 1f), shadow, label);
            dl.AddText(font, fontSize, V(tx, labelTop), col, label);
        }

        RenderAllMarkers(dl, cx, cy, halfVis, barHalfW, lensStr, heading, player, originPos, inDutyOrPvp);

        dl.PopClipRect();

        // End caps
        dl.AddQuadFilled(V(bx, cy - capHH), V(bx + capHW, cy), V(bx, cy + capHH), V(bx - capHW, cy), solidBgCol);
        dl.AddQuadFilled(V(bx + bw, cy - capHH), V(bx + bw + capHW, cy), V(bx + bw, cy + capHH), V(bx + bw - capHW, cy), solidBgCol);
        DrawEndCapOutlines(dl, bx, cy, capHW, capHH, borderCol);
        DrawEndCapOutlines(dl, bx + bw, cy, capHW, capHH, borderCol);

        // Centre notch
        const float nH = 10f, nW = 6f;
        dl.AddTriangleFilled(V(cx + 1f, by + nH + 2f), V(cx - nW + 1f, by + 1f), V(cx + nW + 1f, by + 1f), 0x55000000u);
        dl.AddTriangleFilled(V(cx, by + nH + 1f), V(cx - nW, by), V(cx + nW, by), 0xF2FFFFFFu);

        if (config.ShowHeadingText)
        {
            string txt = $"{(int)heading:000}°";
            var sz = ImGui.CalcTextSize(txt);
            dl.AddText(V(cx - sz.X * 0.5f, by + bh + 3f), 0xBBCCBB99u, txt);
        }
    }

    private static void DrawEndCapOutlines(ImDrawListPtr dl, float cx, float cy, float hw, float hh, uint color, float dotR = 2.5f)
    {
        dl.AddQuad(V(cx, cy - hh), V(cx + hw, cy), V(cx, cy + hh), V(cx - hw, cy), color, 1.5f);
        uint inner = (color & 0x00FFFFFFu) | (((color >> 24) * 6 / 10) << 24);
        float s = 0.52f;
        dl.AddQuad(V(cx, cy - hh * s), V(cx + hw * s, cy), V(cx, cy + hh * s), V(cx - hw * s, cy), inner, 1f);
        dl.AddCircleFilled(V(cx, cy), dotR, color);
    }

    private static void DrawFilledDiamond(ImDrawListPtr dl, float cx, float cy, float hw, float hh, uint color) =>
        dl.AddQuadFilled(V(cx, cy - hh), V(cx + hw, cy), V(cx, cy + hh), V(cx - hw, cy), color);

    // ─── Glow helpers ───────────────────────────────────────────────
    private static float PulseIntensity(float t) =>
        (0.75f + 0.25f * MathF.Sin(t * 0.79f)) * (0.92f + 0.08f * MathF.Sin(t * 3.23f + 1.17f));

    private static void DrawGlowLine(ImDrawListPtr dl, Vector2 a, Vector2 b, uint col, float intensity, float t,
        bool fromLeft, float wipe, float fill, bool wipeReversed = false)
    {
        Vector2 delta = b - a;
        float len = delta.Length();
        if (len < 1f) return;
        Vector2 dir = delta / len;
        Vector2 perp = new(-dir.Y, dir.X);
        const float amp = 5f, waveLen = 26f, flowSpeed = 2f, wipeHalf = 0.2f, harmWeight = 0.33f;
        float tipFadeStart = Lerp(0.6f, 1.0f, Math.Clamp(fill, 0f, 1f));
        float flowDir = fromLeft ? -1f : 1f;
        float wipeCentre = Lerp(1f + wipeHalf, -wipeHalf, wipe);
        float freq = 2f * MathF.PI / waveLen;
        float freq2 = freq * 2f;
        float phase = t * flowSpeed * flowDir;
        float phase2 = phase * 1.4f + 1.3f;

        int samples = Math.Clamp((int)(len / (waveLen * 0.5f) * 4f) + 2, 3, 96);
        Span<Vector2> pts = stackalloc Vector2[96];
        Span<float> fades = stackalloc float[96];
        for (int i = 0; i < samples; i++)
        {
            float along = len * i / (samples - 1);
            float u = fromLeft ? along / len : 1f - along / len;
            float envelope = u * u * (3f - 2f * u);
            float wave = MathF.Sin(along * freq + phase) * (1f - harmWeight * u)
                       + MathF.Sin(along * freq2 + phase2) * (harmWeight * u);
            pts[i] = a + dir * along + perp * (amp * envelope * wave);
            float tipFade = 1f - SmoothStep(Math.Clamp((u - tipFadeStart) / (1f - tipFadeStart + 1e-4f), 0f, 1f));
            float wipeU = wipeReversed ? 1f - u : u;
            float wipeFade = 1f - SmoothStep(Math.Clamp((wipeU - (wipeCentre - wipeHalf)) / (2f * wipeHalf), 0f, 1f));
            fades[i] = tipFade * wipeFade;
        }

        (float alpha, float thickness)[] layers = { (0.05f, 14f), (0.10f, 10f), (0.18f, 6f), (0.32f, 3.5f), (0.70f, 1.8f) };
        foreach (var (alpha, thick) in layers)
            for (int i = 0; i < samples - 1; i++)
            {
                float segFade = (fades[i] + fades[i + 1]) * 0.5f;
                if (segFade <= 0.002f) continue;
                dl.AddLine(pts[i], pts[i + 1], WithAlpha(col, alpha * intensity * segFade), thick);
            }
    }

    private static void DrawBorderGlowBracket(ImDrawListPtr dl, float bx, float by, float bw, float bh,
        float segW, uint col, float intensity, float t, float wipe, float fill, bool fromLeft)
    {
        float x0 = fromLeft ? bx : bx + bw - segW;
        float x1 = fromLeft ? bx + segW : bx + bw;
        DrawGlowLine(dl, V(x0, by), V(x1, by), col, intensity, t, fromLeft, wipe, fill);
        DrawGlowLine(dl, V(x0, by + bh), V(x1, by + bh), col, intensity, t, fromLeft, wipe, fill);
    }

    // ─── Fade-out engine ──────────────────────────────────────────────
    private static float UpdateFadeOut(ref float tracked, ref float frozen, ref float start,
        float real, bool trigger, bool extResync, bool resyncIfExceeds, float now, float duration, out float wipe)
    {
        if (start < 0f)
        {
            if (trigger) { frozen = tracked; start = now; }
            else tracked = real;
        }
        if (start >= 0f)
        {
            float elapsed = now - start;
            if (extResync || (resyncIfExceeds && real > frozen) || elapsed >= duration)
            {
                start = -1f;
                tracked = real;
                wipe = 0f;
                return tracked;
            }
            wipe = elapsed / duration;
            return frozen;
        }
        wipe = 0f;
        return tracked;
    }

    private static unsafe float GetLimitBreakProgress()
    {
        var uiState = UIState.Instance();
        if (uiState == null) return 0f;
        var lb = uiState->LimitBreakController;
        return lb.BarUnits <= 0 ? 0f : Math.Clamp((float)lb.CurrentUnits / lb.BarUnits, 0f, 3f);
    }

    private string? GetCastActionName(IBattleChara caster)
    {
        if (caster.CastActionType != 1) return null;
        if (!actionNameCache.TryGetValue(caster.CastActionId, out var name))
        {
            var row = actionSheet.GetRowOrDefault(caster.CastActionId);
            name = row.HasValue ? row.Value.Name.ToString() : string.Empty;
            actionNameCache[caster.CastActionId] = name;
        }
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private bool IsInDutyOrPvp() =>
        condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] ||
        condition[ConditionFlag.BoundByDuty95] || condition[ConditionFlag.InDeepDungeon] ||
        clientState.IsPvP;

    // ─── Target bar ─────────────────────────────────────────────────
    private (float x, float w, float totX, float totW, float rowW) SplitTargetBarRow(float compassX, float compassW, bool hasTot)
    {
        float cx = compassX + compassW * 0.5f;
        float rowW = compassW * Math.Clamp(config.TargetBarWidthFraction, 0.1f, 1f);
        float rowX = cx - rowW * 0.5f;
        if (!hasTot) return (rowX, rowW, 0f, 0f, rowW);
        float mainW = rowW * MainBarShareWithTot;
        float gap = MathF.Max(10f, rowW * TargetBarRowGapFraction);
        float totW = rowW - mainW - gap;
        float totX = rowX + mainW + gap;
        return (rowX, mainW, totX, totW, rowW);
    }

    private uint TargetBarFillColor(IGameObject obj, bool inDutyOrPvp)
    {
        if (obj is ICharacter ch && (ch.StatusFlags & StatusFlags.PartyMember) != 0
            && config.ShowPartyRoleIcons && (!config.PartyRoleIconsOnlyInDuty || inDutyOrPvp))
            return GetRoleColor(ch);
        uint baseCol = MarkerBaseColor(obj);
        return baseCol != 0u ? baseCol : C(config.NpcColor);
    }

    private (Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl) TrapezoidSlice(float x, float y, float w, float h, float taper, float lo, float hi)
    {
        lo = Math.Clamp(lo, 0f, 1f);
        hi = Math.Clamp(hi, 0f, 1f);
        float botX = x + taper, botSpan = w - 2f * taper;
        float topA = x + w * lo, topB = x + w * hi;
        float botA = botX + botSpan * lo, botB = botX + botSpan * hi;
        return (new Vector2(topA, y), new Vector2(topB, y), new Vector2(botB, y + h), new Vector2(botA, y + h));
    }

    private (Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl) TrapezoidFill(float x, float y, float w, float h, float taper, float frac, bool fromRight = false)
    {
        frac = Math.Clamp(frac, 0f, 1f);
        return fromRight
            ? TrapezoidSlice(x, y, w, h, taper, 1f - frac, 1f)
            : TrapezoidSlice(x, y, w, h, taper, 0f, frac);
    }

    private void DrawTrapezoidBar(ImDrawListPtr dl, float x, float y, float w, float h, float fillFrac,
        uint bg, uint fill, float alpha, float? shieldFrac = null, uint? shieldCol = null, bool fromRight = false)
    {
        float taper = MathF.Min(h * 0.9f, w * 0.35f);
        var (bTl, bTr, bBr, bBl) = TrapezoidFill(x, y, w, h, taper, 1f);
        dl.AddQuadFilled(bTl, bTr, bBr, bBl, WithAlpha(bg, alpha));

        const float inset = 2f;
        float innerH = h - inset * 2f;
        float innerTaper = taper * (innerH / h);
        if (fillFrac > 0f)
        {
            var (fTl, fTr, fBr, fBl) = TrapezoidFill(x + inset, y + inset, w - inset * 2f, innerH, innerTaper, fillFrac, fromRight);
            dl.AddQuadFilled(fTl, fTr, fBr, fBl, WithAlpha(fill, alpha));
        }
        if (shieldFrac.HasValue && shieldFrac.Value > 0f && shieldCol.HasValue)
        {
            var (sTl, sTr, sBr, sBl) = TrapezoidFill(x + inset, y + inset, w - inset * 2f, innerH, innerTaper, shieldFrac.Value, !fromRight);
            dl.AddQuadFilled(sTl, sTr, sBr, sBl, WithAlpha(shieldCol.Value, alpha));
        }
        dl.AddLine(V(x + 1f, y + 1f), V(x + w - 1f, y + 1f), WithAlpha(0x1AFFFFFFu, alpha), 1f);
        dl.AddQuad(bTl, bTr, bBr, bBl, WithAlpha(C(config.BorderColor), alpha), 1.5f);
    }

    private void HandleTargetFrameClick(Vector2 min, Vector2 max, IGameObject obj, bool allowLeftToTarget)
    {
        if (IsVanillaContextMenuOpen()) return;
        if (!ImGui.IsMouseHoveringRect(min, max, false)) return;
        var io = ImGui.GetIO();
        io.WantCaptureMouse = true;
        if (allowLeftToTarget && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        { targetManager.Target = obj; return; }
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            TryOpenVanillaTargetContextMenu(obj);
    }

    private unsafe void TryOpenVanillaTargetContextMenu(IGameObject obj)
    {
        if (obj.Address == IntPtr.Zero) return;
        var agentModule = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance();
        if (agentModule == null) return;
        var hudAgent = (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentHUD*)
            agentModule->GetAgentByInternalId(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.Hud);
        if (hudAgent == null) return;
        hudAgent->OpenContextMenuFromTarget((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address);
    }

private float RenderTargetBar(ImDrawListPtr dl, float tbX, float tbW, float tbY, float nameCx,
    float nameRowW, float now, float barAlpha, bool inDutyOrPvp)
{
    var currentTarget = targetManager.Target;
    if (currentTarget == null) return tbY;

    uint borderCol = WithAlpha(C(config.BorderColor), barAlpha);
    uint bgCol = WithAlpha(C(config.BackgroundColor), barAlpha);
    uint nameCol = WithAlpha(C(config.CardinalColor), barAlpha);

    float cx = nameCx;
    bool isChara = currentTarget is ICharacter;
    float tbH = isChara ? MathF.Max(4f, config.TargetBarHeight) : 0f;
    uint fillCol = WithAlpha(TargetBarFillColor(currentTarget, inDutyOrPvp), barAlpha);

    if (isChara)
    {
        var chara = (ICharacter)currentTarget;
        float rawFrac = chara.MaxHp > 0f ? Math.Clamp(chara.CurrentHp / chara.MaxHp, 0f, 1f) : 0f;

        // ─── Minion detection: force full health bar ────────────────
bool isMinion = currentTarget.ObjectKind == ObjectKind.Companion;
if (isMinion)
{
    rawFrac = 1f;
    displayedTargetHpFrac = 1f;
    lastRawTargetHpFrac = 1f;
    targetBarFlashAlpha = 0f;
}

        float dt = ImGui.GetIO().DeltaTime;
        if (currentTarget.GameObjectId != lastTargetBarObjectId)
        {
            lastTargetBarObjectId = currentTarget.GameObjectId;
            displayedTargetHpFrac = rawFrac;
            lastRawTargetHpFrac = rawFrac;
            targetBarFlashAlpha = 0f;
        }
        else
        {
            if (!isMinion && rawFrac < lastRawTargetHpFrac - 0.001f)
                targetBarFlashAlpha = 1f;
            lastRawTargetHpFrac = rawFrac;
            displayedTargetHpFrac += (rawFrac - displayedTargetHpFrac) * (1f - MathF.Exp(-dt * 14f));
        }
        if (!isMinion)
            targetBarFlashAlpha = MathF.Max(0f, targetBarFlashAlpha - dt / 0.4f);

        DrawTrapezoidBar(dl, tbX, tbY, tbW, tbH, displayedTargetHpFrac, bgCol, fillCol, barAlpha,
            (!isMinion && config.ShowTargetBarShield) ? chara.ShieldPercentage / 100f : null,
            (!isMinion && config.ShowTargetBarShield) ? C(config.TargetBarShieldColor) : null);

        if (!isMinion && targetBarFlashAlpha > 0f)
        {
            float lo = MathF.Min(rawFrac, displayedTargetHpFrac);
            float hi = MathF.Max(rawFrac, displayedTargetHpFrac);
            if (hi > lo)
            {
                float taper = MathF.Min(tbH * 0.9f, tbW * 0.35f);
                const float inset = 2f;
                float innerH = tbH - inset * 2f;
                float innerTaper = taper * (innerH / tbH);
                var (fTl, fTr, fBr, fBl) = TrapezoidSlice(tbX + inset, tbY + inset, tbW - inset * 2f, innerH, innerTaper, lo, hi);
                dl.AddQuadFilled(fTl, fTr, fBr, fBl, WithAlpha(0xFFFFFFFFu, targetBarFlashAlpha * 0.5f * barAlpha));
            }
        }
    }

    // ─── Name row (unchanged) ──────────────────────────────────────
    using var jupiterScope = jupiterFont.Available ? jupiterFont.Push() : null;
    float fontSize = ImGui.GetFontSize() * config.TargetBarFontScale;
    var font = ImGui.GetFont();

    string? castName = currentTarget is IBattleChara castingChara && castingChara.IsCasting && castingChara.TotalCastTime > 0f
        ? GetCastActionName(castingChara) : null;

    string label = castName ?? currentTarget.Name.TextValue;
    if (castName == null && config.ShowTargetLevel && currentTarget is ICharacter lvlChar && lvlChar.Level > 0)
        label = $"Lv{lvlChar.Level}  {label}";

    var tsz = ImGui.CalcTextSize(label) * config.TargetBarFontScale;
    float nameGap = MathF.Max(6f, tbH * 0.5f);
    float nameY = tbY + tbH + nameGap;
    float tx = cx - tsz.X * 0.5f;

    uint shadowCol = WithAlpha(0xCC000000u, barAlpha);
    foreach (var (dx, dy) in new[] { (-1f,-1f), (0f,-1f), (1f,-1f), (-1f,0f), (1f,0f), (-1f,1f), (0f,1f), (1f,1f) })
        dl.AddText(font, fontSize, V(tx + dx, nameY + dy), shadowCol, label);
    dl.AddText(font, fontSize, V(tx, nameY), nameCol, label);

    float ornHH = fontSize * 0.46f, ornHW = ornHH * 0.69f;
    float ornGap = 6f;
    float textCy = nameY + tsz.Y * 0.5f;
    float leftOrnX = tx - ornGap - ornHW, rightOrnX = tx + tsz.X + ornGap + ornHW;
    float shHW = ornHW + 2f, shHH = ornHH + 2f;
    DrawFilledDiamond(dl, leftOrnX, textCy, shHW, shHH, shadowCol);
    DrawFilledDiamond(dl, rightOrnX, textCy, shHW, shHH, shadowCol);
    DrawEndCapOutlines(dl, leftOrnX, textCy, ornHW, ornHH, borderCol, ornHW * 0.28f);
    DrawEndCapOutlines(dl, rightOrnX, textCy, ornHW, ornHH, borderCol, ornHW * 0.28f);

    // ─── Ribbons (unchanged) ────────────────────────────────────────
    if (isChara && config.ShowTargetBarRibbons)
    {
        float leftEdge = leftOrnX - ornHW, rightEdge = rightOrnX + ornHW;
        float rowLeft = nameCx - nameRowW * 0.5f, rowRight = nameCx + nameRowW * 0.5f;
        float inset = MathF.Max(8f, nameRowW * 0.06f);
        float ribbonL = MathF.Min(rowLeft + inset, leftEdge - 24f);
        float ribbonR = MathF.Max(rowRight - inset, rightEdge + 24f);
        float glowT = now;

        (float edge, float target, uint col, float tMul, float tOff)[] ribbons =
        {
            (leftEdge, ribbonL, shadowCol, 0.65f, 7.1f),
            (leftEdge, ribbonL, borderCol, 1.00f, 0.0f),
            (rightEdge, ribbonR, shadowCol, 1.15f, 5.3f),
            (rightEdge, ribbonR, borderCol, 1.60f, 3.7f),
        };
        foreach (var (edge, target, col, tMul, tOff) in ribbons)
            DrawGlowLine(dl, V(edge, textCy), V(target, textCy), col, 1f, glowT * tMul + tOff, true, 0f, 0f);

        // Cast ribbon (unchanged)
        if (currentTarget.GameObjectId != castWipeTargetId)
        {
            castWipeTargetId = currentTarget.GameObjectId;
            castTrackedProgress = 0f;
            castFadeOutStartTime = -1f;
        }

        var battleChara = currentTarget as IBattleChara;
        bool isCasting = battleChara?.IsCasting ?? false;
        float totalCastTime = battleChara?.TotalCastTime ?? 0f;
        float castReal = 0f;
        if (isCasting && totalCastTime > 0f)
        {
            castReal = Math.Clamp((battleChara?.CurrentCastTime ?? 0f) / totalCastTime, 0f, 1f);
        }

        float castDisp = UpdateFadeOut(ref castTrackedProgress, ref castFrozenProgress, ref castFadeOutStartTime,
            castReal, !isCasting && castTrackedProgress > 0f, isCasting,
            false, glowT, CastFadeOutDuration, out float castWipe);

        if (castDisp > 0f)
        {
            uint castCol = WithAlpha(C(config.AggroWarningColor), barAlpha);
            float ci = PulseIntensity(glowT);
            DrawGlowLine(dl, V(leftEdge, textCy), V(Lerp(leftEdge, ribbonL, castDisp), textCy),
                castCol, ci, glowT, true, castWipe, 0f, true);
            DrawGlowLine(dl, V(rightEdge, textCy), V(Lerp(rightEdge, ribbonR, castDisp), textCy),
                castCol, ci, glowT, true, castWipe, 0f, true);
        }
    }

    float clickTop = tbY, clickBottom = nameY + tsz.Y;
    float clickLeft = MathF.Min(tbX, leftOrnX - shHW);
    float clickRight = MathF.Max(tbX + tbW, rightOrnX + shHW);
    HandleTargetFrameClick(V(clickLeft, clickTop), V(clickRight, clickBottom), currentTarget!, false);

    return nameY + tsz.Y;
}

    private void RenderTargetOfTargetBar(ImDrawListPtr dl, float tbX, float tbW, float tbY,
        IPlayerCharacter player, float now, float barAlpha, bool inDutyOrPvp)
    {
        var currentTarget = targetManager.Target;
        var tot = currentTarget?.TargetObject;
        if (currentTarget == null || tot == null || tot.GameObjectId == currentTarget.GameObjectId || tot is not ICharacter chara) return;

        bool targetingMe = config.HighlightIfTargetingMe && currentTarget.TargetObjectId == player.GameObjectId;
        float tbH = MathF.Max(4f, config.TargetBarHeight);
        uint fillCol = targetingMe ? C(config.AggroWarningColor) : TargetBarFillColor(tot, inDutyOrPvp);
        float frac = chara.MaxHp > 0f ? Math.Clamp(chara.CurrentHp / chara.MaxHp, 0f, 1f) : 0f;
        float pulse = targetingMe ? 0.82f + 0.18f * MathF.Sin(now * 5f) : 1f;

        DrawTrapezoidBar(dl, tbX, tbY, tbW, tbH, frac,
            WithAlpha(C(config.BackgroundColor), barAlpha),
            WithAlpha(fillCol, pulse * barAlpha),
            barAlpha);

        // Pass 'chara' (non-null ICharacter) with null-forgiving operator to suppress CS8604
        HandleTargetFrameClick(V(tbX, tbY), V(tbX + tbW, tbY + tbH), chara!, true);
    }

    // ─── Status icons ──────────────────────────────────────────────
    private void RenderStatusIconRow(ImDrawListPtr dl, IBattleChara character, float cx, float y, float barAlpha,
        float iconSize, int maxIcons, bool includeMoodles, bool includeLoci)
    {
        float size = MathF.Max(8f, iconSize);
        float hGap = size * 0.25f;
        int max = Math.Max(1, maxIcons);
        float now = (float)ImGui.GetTime();

        PruneDurationTrackerIfDue(now);
        targetStatusBuffer.Clear();

        foreach (var status in character.StatusList)
        {
            if (targetStatusBuffer.Count >= max) break;
            if (status.StatusId == 0) continue;
            if (status.GameData.ValueNullable is not { } row || row.Icon == 0) continue;
            float remaining = (row.IsPermanent || row.IsFcBuff) ? 0f : status.RemainingTime;
            targetStatusBuffer.Add((remaining, (int)row.Icon, row.Name.ToString(), row.Description.ToString(), System.Guid.Empty));
        }

        if (character.Address != IntPtr.Zero)
        {
            if (includeMoodles && targetStatusBuffer.Count < max && IsPluginActive(moodlesIpc, now) && IsVersionCompatible(moodlesIpc, now))
                AppendPluginStatuses(character.Address, max, now, moodlesGetStatusesByPtr, ref cachedMoodles,
                    ref cachedMoodlesTarget, ref cachedMoodlesFetchedAt, s => s.GUID, s => s.IconID,
                    s => (s.Title, s.Description), s => s.ExpireTicks);
            if (includeLoci && targetStatusBuffer.Count < max && IsPluginActive(lociIpc, now) && IsVersionCompatible(lociIpc, now))
                AppendPluginStatuses(character.Address, max, now, lociGetStatusesByPtr, ref cachedLoci,
                    ref cachedLociTarget, ref cachedLociFetchedAt, s => s.GUID, s => (int)s.IconID,
                    s => (s.Title, s.Description), s => s.ExpireTicks);
        }

        if (targetStatusBuffer.Count == 0) return;

        int n = targetStatusBuffer.Count;
        float startX = cx - (n * size + (n - 1) * hGap) * 0.5f;
        float topGap = size * 0.15f;
        float halfH = size * 0.5f * GetIconAspect(targetStatusBuffer[0].Icon);
        float scy = y + topGap + halfH;
        float fontSize = MathF.Max(9f, size * 0.8f);
        var font = ImGui.GetFont();
        float textGap = -size * 0.12f;
        float tooltipGap = MathF.Max(4f, size * 0.15f);

        for (int i = 0; i < n; i++)
        {
            var (remaining, icon, name, desc, guid) = targetStatusBuffer[i];
            float sx = startX + i * (size + hGap) + size * 0.5f;
            if (!TryDrawIcon(dl, icon, sx, scy, size, barAlpha)) continue;

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
                float lx = sx - lsz.X * 0.5f;
                float ly = scy + halfH + textGap;
                dl.AddText(font, fontSize, V(lx + 1f, ly + 1f), WithAlpha(0xCC000000u, barAlpha), durationLabel);
                dl.AddText(font, fontSize, V(lx, ly), WithAlpha(0xFFFFFFFFu, barAlpha), durationLabel);
                hoverBottom = ly + lsz.Y;
            }

            if (ImGui.IsMouseHoveringRect(V(sx - size * 0.5f, scy - halfH), V(sx + size * 0.5f, hoverBottom), false))
            {
                ImGui.SetNextWindowPos(V(sx, hoverBottom + tooltipGap), ImGuiCond.Always, V(0.5f, 0f));
                var tooltipBg = config.BackgroundColor;
                ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(tooltipBg.X, tooltipBg.Y, tooltipBg.Z, tooltipBg.W * barAlpha));
                ImGui.BeginTooltip();
                ImGuiHelpers.SeStringWrapped(GetFormattedTooltipBytes(name, desc), new SeStringDrawParams { WrapWidth = 345f });
                ImGui.EndTooltip();
                ImGui.PopStyleColor();
            }
        }
    }

    private void RenderTargetStatuses(ImDrawListPtr dl, IBattleChara target, float cx, float y, float barAlpha) =>
        RenderStatusIconRow(dl, target, cx, y, barAlpha, config.TargetStatusIconSize, config.TargetStatusMaxIcons, true, true);

    // ─── IPC helpers (unified) ─────────────────────────────────────
    private bool IsPluginActive(PluginIpcState state, float now)
    {
        if (now - state.ActiveCheckedAt < PluginActiveCacheSeconds) return state.Active;
        state.ActiveCheckedAt = now;
        state.Active = false;
        foreach (var p in pluginInterface.InstalledPlugins)
            if (p.IsLoaded && p.InternalName == state.Name) { state.Active = true; break; }
        return state.Active;
    }

    private bool IsVersionCompatible(PluginIpcState state, float now)
    {
        if (now - state.VersionCheckedAt < PluginActiveCacheSeconds) return state.VersionOk;
        state.VersionCheckedAt = now;
        bool wasOk = state.VersionOk;
        try
        {
            if (state.Kind == PluginIpcKind.Moodles)
                state.VersionOk = moodlesVersion.InvokeFunc() >= state.MinimumVersion;
            else
            { lociApiVersion.InvokeFunc(); state.VersionOk = true; } // Loci just needs no throw
        }
        catch { state.VersionOk = false; }
        if (wasOk && !state.VersionOk)
            log.Warning($"[SkyrimCompass] {state.Name} IPC version no longer compatible – status icons disabled.");
        return state.VersionOk;
    }

    private void AppendPluginStatuses<T>(
        nint targetAddress, int max, float now,
        ICallGateSubscriber<nint, List<T>> getter,
        ref List<T>? cache, ref nint cachedTarget, ref float fetchedAt,
        Func<T, System.Guid> guidSelector,
        Func<T, int> iconSelector,
        Func<T, (string Name, string Desc)> textSelector,
        Func<T, long> expireTicksSelector)
    {
        if (cache == null || targetAddress != cachedTarget || now - fetchedAt >= StatusPayloadCacheSeconds)
        {
            cachedTarget = targetAddress;
            fetchedAt = now;
            try { cache = getter.InvokeFunc(targetAddress); }
            catch { cache = null; }
        }
        if (cache == null) return;

        foreach (var s in cache)
        {
            if (targetStatusBuffer.Count >= max) break;
            var guid = guidSelector(s);
            int icon = iconSelector(s);
            if (icon <= 0) continue;
            if (targetStatusBuffer.Exists(e => e.Guid == guid)) continue;
            var (name, desc) = textSelector(s);
            long expire = expireTicksSelector(s);
            float remaining = EstimateRemainingSeconds(guid, expire, now);
            targetStatusBuffer.Add((remaining, icon, name, desc, guid));
        }
    }

    private float EstimateRemainingSeconds(System.Guid guid, long expireMs, float now)
    {
        if (expireMs < 0) return 0f;
        if (!statusDurationTracker.TryGetValue(guid, out var tracked) || tracked.TotalMs != expireMs)
        {
            tracked = (now, expireMs);
            statusDurationTracker[guid] = tracked;
        }
        return MathF.Max(0f, expireMs / 1000f - (now - tracked.FirstSeen));
    }

    private void PruneDurationTrackerIfDue(float now)
    {
        if (now < nextDurationTrackerPruneAt) return;
        nextDurationTrackerPruneAt = now + 60f;
        if (statusDurationTracker.Count == 0) return;
        var stale = new List<System.Guid>();
        foreach (var (guid, tracked) in statusDurationTracker)
            if (now - tracked.FirstSeen > tracked.TotalMs / 1000f + 30f)
                stale.Add(guid);
        foreach (var guid in stale) statusDurationTracker.Remove(guid);
    }

    // ─── Tooltip formatting ────────────────────────────────────────
    private static readonly Dictionary<string, ushort> NamedUiColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WhiteNormal"] = 0, ["White"] = 1, ["Grey1"]=2, ["Grey2"]=3, ["Grey3"]=4, ["Grey4"]=5,
        ["Grey5"]=6, ["Black"]=7, ["LightYellow"]=8, ["Red"]=17, ["DarkRed"]=19, ["Green"]=45,
        ["DarkGreen"]=47, ["WarmSeaBlue"]=52, ["Orange"]=500, ["LightBlue"]=502, ["Yellow"]=514,
        ["Gold"]=540, ["DarkBlue"]=543, ["LightGreen"]=551, ["Pink"]=561,
    };
    private static readonly Regex FormatTagRegex = new(@"(\[color=[0-9a-zA-Z]+\])|(\[/color\])|(\[glow=[0-9a-zA-Z]+\])|(\[/glow\])|(\[i\])|(\[/i\])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryResolveUiColor(string val, out ushort key) =>
        ushort.TryParse(val, out key) || NamedUiColors.TryGetValue(val, out key);

    private static void AppendFormattedSegment(SeStringBuilder builder, string raw)
    {
        bool color=false, glow=false, italics=false;
        int last=0;
        foreach (Match m in FormatTagRegex.Matches(raw))
        {
            if (m.Index > last) builder.AddText(raw[last..m.Index]);
            last = m.Index + m.Length;
            var tag = m.Value;
            if (tag.StartsWith("[color=", StringComparison.OrdinalIgnoreCase))
            { if (TryResolveUiColor(tag[7..^1], out var id)) { builder.AddUiForeground(id); color = true; } }
            else if (tag == "[/color]") { if (color) { builder.AddUiForegroundOff(); color = false; } }
            else if (tag.StartsWith("[glow=", StringComparison.OrdinalIgnoreCase))
            { if (TryResolveUiColor(tag[6..^1], out var id)) { builder.AddUiGlow(id); glow = true; } }
            else if (tag == "[/glow]") { if (glow) { builder.AddUiGlowOff(); glow = false; } }
            else if (tag == "[i]") { builder.AddItalicsOn(); italics = true; }
            else if (italics) { builder.AddItalicsOff(); italics = false; }
        }
        if (last < raw.Length) builder.AddText(raw[last..]);
        if (color) builder.AddUiForegroundOff();
        if (glow) builder.AddUiGlowOff();
        if (italics) builder.AddItalicsOff();
    }

    private byte[] GetFormattedTooltipBytes(string name, string desc)
    {
        var key = (name, desc);
        if (formattedTooltipCache.TryGetValue(key, out var cached)) return cached;
        var b = new SeStringBuilder();
        AppendFormattedSegment(b, name);
        if (!string.IsNullOrWhiteSpace(desc)) { b.AddText("\n"); AppendFormattedSegment(b, desc); }
        var bytes = b.Encode();
        formattedTooltipCache[key] = bytes;
        return bytes;
    }

    // ─── Marker rendering ──────────────────────────────────────────
    private void RenderAllMarkers(ImDrawListPtr dl, float cx, float cy, float halfVis, float barHalfW,
        float lensStr, float heading, IPlayerCharacter player, Vector3 originPos, bool inDutyOrPvp)
    {
        float maxDist = config.MaxMarkerDistance;
        float maxDistSq = maxDist * maxDist;
        float fateMax = maxDist * config.FateDistanceMultiplier;
        float fateMaxSq = fateMax * fateMax;
        float extHalf = halfVis * lensStr;
        bool showPartyRole = config.ShowPartyRoleIcons && (!config.PartyRoleIconsOnlyInDuty || inDutyOrPvp);

        allCandidates.Clear();

        if (config.ShowAnyMarkers)
        {
            foreach (var obj in objectTable)
            {
                if (obj == null || obj.EntityId == player.EntityId) continue;
                if (!TryComputeBearing(obj.Position, originPos, heading, maxDistSq, extHalf, out float dist, out float delta)) continue;
                uint col = MarkerColor(obj, player, out var kind, inDutyOrPvp);
                if (col == 0) continue;
                allCandidates.Add((obj, null, dist, delta, 1f - dist / maxDist, col, kind));
            }
        }

        if (config.ShowFates)
        {
            foreach (var fate in fateTable)
            {
                if (fate == null || (fate.State != FateState.Running && fate.State != FateState.Preparing)) continue;
                if (!TryComputeBearing(fate.Position, originPos, heading, fateMaxSq, extHalf, out float dist, out float delta)) continue;
                allCandidates.Add((null, fate, dist, delta, 1f - dist / fateMax, 0u, AetheryteNameKind.None));
            }
        }

        if (allCandidates.Count == 0) return;
        allCandidates.Sort(DistFarFirst);

        foreach (var cand in allCandidates)
        {
            float delta = cand.Delta, t = cand.T;
            float sx = cx + Project(delta, halfVis, barHalfW, lensStr);
            float alpha = ComputeFadeAlpha(t) * LensEdgeAlpha(delta, halfVis, extHalf);

            if (cand.Fate is { } fate)
            {
                float size = Lerp(config.FateIconMinSize, config.FateIconMaxSize, t);
                if (!(fate.IconId > 0 && TryDrawIcon(dl, (int)fate.IconId, sx, cy, size, alpha)))
                    DrawFilledDot(dl, sx, cy, (3f + 7f * t) * 2f, C(config.FateColor), alpha);
                continue;
            }

            var obj = cand.Obj!;
            uint col = cand.Col;
            int iconId = 0;
            float iconSize = 0f;
            bool isAetheryte = cand.AetheryteKind != AetheryteNameKind.None;

            if (config.ShowAetheryteIcons && isAetheryte)
            {
                iconId = GetAetheryteIconId(cand.AetheryteKind);
                iconSize = Lerp(config.AetheryteIconMinSize, config.AetheryteIconMaxSize, t) * AetheryteIconSizeMultiplier;
            }
            else if (obj.ObjectKind == ObjectKind.EventNpc && TryGetNpcIcon(obj, out int npcIcon))
            {
                iconId = npcIcon;
                iconSize = Lerp(config.NpcQuestIconMinSize, config.NpcQuestIconMaxSize, t) * IconSizeMultiplier;
            }
            else if (config.ShowGatheringIcons && obj.ObjectKind == ObjectKind.GatheringPoint)
            {
                int gIcon = GetGatheringIconId(obj.BaseId);
                if (gIcon > 0) { iconId = gIcon; iconSize = Lerp(config.GatheringIconMinSize, config.GatheringIconMaxSize, t); }
            }
            else if (config.ShowTreasureIcons && obj.ObjectKind == ObjectKind.Treasure)
            {
                iconId = config.TreasureIconId;
                iconSize = Lerp(config.TreasureMinSize, config.TreasureMaxSize, t);
            }

            bool drewIcon = iconId > 0 && TryDrawIcon(dl, iconId, sx, cy, iconSize, alpha);

            if (!drewIcon)
            {
                if (obj.ObjectKind == ObjectKind.Pc)
                {
                    float playerSize = Lerp(config.PartyRoleIconMinSize, config.PartyRoleIconMaxSize, t);
                    bool drewJob = false;
                    if (showPartyRole && obj is ICharacter ch && (ch.StatusFlags & StatusFlags.PartyMember) != 0)
                    {
                        int jobIcon = ch.ClassJob.RowId > 0 ? (int)(62000 + ch.ClassJob.RowId) : 0;
                        if (jobIcon > 0)
                        {
                            float drawSize = playerSize * IconSizeMultiplier;
                            uint roleCol = GetRoleColor(ch);
                            DrawIconRingAndShadow(dl, sx, cy, drawSize * 0.5f, roleCol, roleCol, alpha);
                            TryDrawIcon(dl, jobIcon, sx, cy, drawSize, alpha);
                            drewJob = true;
                        }
                    }
                    if (!drewJob)
                    {
                        var ov = config.PlayerIconOverrides.Find(o => o.PlayerName.Length > 0 &&
                            string.Equals(o.PlayerName, obj.Name.TextValue, StringComparison.OrdinalIgnoreCase));
                        if (ov != null)
                        {
                            float overrideSize = playerSize * IconSizeMultiplier;
                            float half = overrideSize * 0.5f;
                            DrawIconRingAndShadow(dl, sx, cy, half,
                                ov.ShowBorder ? C(ov.BorderColor) : null,
                                ov.ShowFill ? C(ov.FillColor) : null, alpha);
                            if (!(ov.IconBaseId > 0 && TryDrawIcon(dl, ov.IconBaseId, sx, cy, overrideSize, alpha, ov.ClipToCircle, ov.SizeMultiplier)))
                                DrawFilledDot(dl, sx, cy, playerSize, ov.ShowBorder ? C(ov.BorderColor) : col, alpha);
                        }
                        else
                        {
                            bool isFriend = config.SolidFriendDots && obj is ICharacter ch2 && (ch2.StatusFlags & StatusFlags.Friend) != 0;
                            if (isFriend) DrawFilledDot(dl, sx, cy, playerSize, col, alpha);
                            else DrawHollowDot(dl, sx, cy, playerSize, col, alpha);
                        }
                    }
                }
                else
                {
                    (float min, float max, bool filled) dot = isAetheryte ? (config.AetheryteIconMinSize, config.AetheryteIconMaxSize, true)
                        : obj.ObjectKind == ObjectKind.EventNpc ? (config.NpcQuestIconMinSize, config.NpcQuestIconMaxSize, false)
                        : obj.ObjectKind == ObjectKind.BattleNpc ? (config.EnemyMinSize, config.EnemyMaxSize, true)
                        : obj.ObjectKind == ObjectKind.Treasure ? (config.TreasureMinSize, config.TreasureMaxSize, true)
                        : (6f, 20f, true);
                    float dotSize = Lerp(dot.min, dot.max, t);
                    if (dot.filled) DrawFilledDot(dl, sx, cy, dotSize, col, alpha);
                    else DrawHollowDot(dl, sx, cy, dotSize, col, alpha);
                }
            }
        }
    }

    private float ComputeFadeAlpha(float t)
    {
        float near = config.DotNearZone, far = config.DotFarZone, midAlpha = config.DotMidAlpha;
        if (t >= near) return 1f;
        if (t >= far) return midAlpha + (1f - midAlpha) * SmoothStep((t - far) / (near - far));
        return midAlpha * SmoothStep(t / far);
    }

    // ─── Icon drawing ──────────────────────────────────────────────
    private bool TryDrawIcon(ImDrawListPtr dl, int iconId, float sx, float cy, float size, float alpha,
        bool clipCircle = false, float uvZoom = 1.0f)
    {
        if (!textureProvider.TryGetFromGameIcon(new GameIconLookup((uint)iconId), out var sharedTex)) return false;
        var tex = sharedTex.GetWrapOrEmpty();
        uint tint = WithAlpha(0xFFFFFFFFu, alpha);
        float halfW, halfH;
        Vector2 uvMin, uvMax;
        if (clipCircle)
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
            uvMin = Vector2.Zero; uvMax = Vector2.One;
        }
        PushUnclip(dl);
        dl.AddImageRounded(tex.Handle, V(sx - halfW, cy - halfH), V(sx + halfW, cy + halfH),
            uvMin, uvMax, tint, clipCircle ? halfW : 0f, ImDrawFlags.RoundCornersAll);
        PopUnclip(dl);
        return true;
    }

    private float GetIconAspect(int iconId)
    {
        if (!textureProvider.TryGetFromGameIcon(new GameIconLookup((uint)iconId), out var sharedTex)) return 1f;
        var size = sharedTex.GetWrapOrEmpty().Size;
        return size.X > 0f ? size.Y / size.X : 1f;
    }

    // ─── Static data lookups ──────────────────────────────────────
    private int GetGatheringIconId(uint baseId)
    {
        if (gatheringIconCache.TryGetValue(baseId, out int cached)) return cached;
        int icon = 0;
        if (gatheringPointSheet.GetRowOrDefault(baseId) is { } gp &&
            gatheringPointBaseSheet.GetRowOrDefault(gp.GatheringPointBase.RowId) is { } gpb &&
            gatheringTypeSheet.GetRowOrDefault(gpb.GatheringType.RowId) is { } gt)
            icon = gt.IconMain;
        gatheringIconCache[baseId] = icon;
        return icon;
    }

    private uint GetRoleColor(ICharacter ch)
    {
        uint rowId = ch.ClassJob.RowId;
        if (roleColorCache.TryGetValue(rowId, out uint packed)) return packed;
        uint color = 0u;
        if (classJobSheet.GetRowOrDefault(rowId) is { } row)
        {
            color = row.Role switch
            {
                1 => C(new Vector4(0.36f, 0.48f, 0.76f, 0.90f)),
                2 or 3 => C(new Vector4(0.84f, 0.30f, 0.30f, 0.90f)),
                4 => C(new Vector4(0.30f, 0.69f, 0.49f, 0.90f)),
                _ => C(new Vector4(0.54f, 0.54f, 0.54f, 0.85f)),
            };
        }
        else color = C(new Vector4(0.54f, 0.54f, 0.54f, 0.85f));
        roleColorCache[rowId] = color;
        return color;
    }

    private string GetTitle(uint baseId)
    {
        if (titleCache.TryGetValue(baseId, out var cached)) return cached;
        string v = npcSheet.GetRowOrDefault(baseId) is { } row ? row.Title.ToString() : "";
        titleCache[baseId] = v;
        return v;
    }

    private string GetSingular(uint baseId)
    {
        if (singularCache.TryGetValue(baseId, out var cached)) return cached;
        string v = npcSheet.GetRowOrDefault(baseId) is { } row ? row.Singular.ToString() : "";
        singularCache[baseId] = v;
        return v;
    }

    private static bool HasKeyword(string text, string[] keywords)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var kw in keywords) if (text.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private bool MatchesKeyword(uint baseId, string[] keywords) =>
        HasKeyword(GetTitle(baseId), keywords) || HasKeyword(GetSingular(baseId), keywords);

    private enum NpcCategory { None, Mender, Shop, Skipper, Ticketer, ChocoboKeep }

    private NpcCategory ClassifyNpc(uint baseId)
    {
        if (npcCategoryCache.TryGetValue(baseId, out var cached)) return cached;
        var cat = MatchesKeyword(baseId, MenderKeywords) ? NpcCategory.Mender
            : MatchesKeyword(baseId, ShopKeywords) ? NpcCategory.Shop
            : MatchesKeyword(baseId, SkipperKeywords) ? NpcCategory.Skipper
            : MatchesKeyword(baseId, TicketerKeywords) ? NpcCategory.Ticketer
            : MatchesKeyword(baseId, ChocoboKeepKeywords) ? NpcCategory.ChocoboKeep
            : NpcCategory.None;
        npcCategoryCache[baseId] = cat;
        return cat;
    }

    private bool TryGetNpcIcon(IGameObject obj, out int iconId)
    {
        if (config.ShowNpcQuestIcons && npcMarkerIcons.TryGetValue(obj.GameObjectId, out iconId)) return true;
        switch (ClassifyNpc(obj.BaseId))
        {
            case NpcCategory.Mender when config.ShowMenderIcons: iconId = config.MenderIconId; return true;
            case NpcCategory.Shop when config.ShowShopIcons: iconId = config.ShopIconId; return true;
            case NpcCategory.Skipper when config.ShowFastTravelIcons: iconId = config.FastTravelIconId; return true;
            case NpcCategory.Ticketer when config.ShowFastTravelIcons: iconId = config.FastTravelTicketerIconId; return true;
            case NpcCategory.ChocoboKeep when config.ShowFastTravelIcons: iconId = config.ChocoboKeepIconId; return true;
            default: iconId = 0; return false;
        }
    }

    private enum AetheryteNameKind { None, Big, Shard }

    private AetheryteNameKind ClassifyAetheryte(IGameObject obj)
    {
        bool shard = !string.IsNullOrEmpty(config.AethernetShardName)
            && obj.Name.TextValue.Contains(config.AethernetShardName, StringComparison.OrdinalIgnoreCase);
        if (obj.ObjectKind == ObjectKind.Aetheryte) return shard ? AetheryteNameKind.Shard : AetheryteNameKind.Big;
        return shard ? AetheryteNameKind.Shard : AetheryteNameKind.None;
    }

    private int GetAetheryteIconId(AetheryteNameKind kind) =>
        kind == AetheryteNameKind.Shard ? config.AethernetShardIconId : config.AetheryteIconId;

    private uint MarkerColor(IGameObject obj, IPlayerCharacter player, out AetheryteNameKind kind, bool inDutyOrPvp)
    {
        kind = AetheryteNameKind.None;
        switch (obj.ObjectKind)
        {
            case ObjectKind.Pc: return config.ShowPlayers ? MarkerBaseColor(obj) : 0u;
            case ObjectKind.BattleNpc:
                if (!config.ShowEnemies) return 0u;
                if (obj is not IBattleNpc bnpc || bnpc.BattleNpcKind != BattleNpcSubKind.Combatant) return 0u;
                if (config.EnemiesOnlyIfEngaged && !(bnpc.StatusFlags.HasFlag(StatusFlags.InCombat) && player.StatusFlags.HasFlag(StatusFlags.InCombat)))
                    return 0u;
                return MarkerBaseColor(obj);
            case ObjectKind.EventNpc:
                if (TryGetAetheryteMarkerColor(obj, out uint col, out kind)) return col;
                if (!config.ShowNpcs) return 0u;
                if (config.NpcsOnlyIfTargetable && !obj.IsTargetable) return 0u;
                return MarkerBaseColor(obj);
            case ObjectKind.EventObj:
                if (TryGetAetheryteMarkerColor(obj, out col, out kind)) return col;
                return 0u;
            case ObjectKind.GatheringPoint:
                if (!config.ShowGatheringNodes) return 0u;
                if (config.GatheringOnlyIfTargetable && !obj.IsTargetable) return 0u;
                return MarkerBaseColor(obj);
            case ObjectKind.Treasure:
                return config.ShowTreasure ? MarkerBaseColor(obj) : 0u;
            case ObjectKind.Aetheryte:
                TryGetAetheryteMarkerColor(obj, out uint aetherCol, out kind);
                return aetherCol;
            default: return 0u;
        }
    }

    private bool TryGetAetheryteMarkerColor(IGameObject obj, out uint color, out AetheryteNameKind kind)
    {
        kind = ClassifyAetheryte(obj);
        if (kind == AetheryteNameKind.None) { color = 0u; return false; }
        bool hidden = !config.ShowAetherytes || (kind == AetheryteNameKind.Shard && !config.ShowAethernetShards);
        color = hidden ? 0u : C(config.AetheryteColor);
        return true;
    }

    private uint MarkerBaseColor(IGameObject obj) => obj.ObjectKind switch
    {
        ObjectKind.Pc => C(config.PlayerColor),
        ObjectKind.BattleNpc when obj is IBattleNpc b && b.BattleNpcKind == BattleNpcSubKind.Combatant => C(config.EnemyColor),
        ObjectKind.EventNpc => C(config.NpcColor),
        ObjectKind.GatheringPoint => C(config.GatheringColor),
        ObjectKind.Treasure => C(config.TreasureColor),
        _ => 0u,
    };

    // ─── Dot drawing ──────────────────────────────────────────────
    private static void DrawFilledDot(ImDrawListPtr dl, float sx, float cy, float size, uint col, float alpha) =>
        DrawDot(dl, sx, cy, size, col, alpha, true);
    private static void DrawHollowDot(ImDrawListPtr dl, float sx, float cy, float size, uint col, float alpha) =>
        DrawDot(dl, sx, cy, size, col, alpha, false);

    private static void DrawDot(ImDrawListPtr dl, float sx, float cy, float size, uint col, float alpha, bool filled)
    {
        float r = size * 0.5f;
        if (filled) dl.AddCircleFilled(V(sx, cy), r, WithAlpha(col, alpha));
        else dl.AddCircle(V(sx, cy), r, WithAlpha(col, alpha), 0, 2.0f);
        dl.AddCircle(V(sx, cy), r + 0.8f, WithAlpha(filled ? 0x66000000u : 0x33000000u, alpha));
    }

    private static void DrawIconRingAndShadow(ImDrawListPtr dl, float sx, float cy, float half,
        uint? ring, uint? shadow, float alpha)
    {
        if (ring == null && shadow == null) return;
        PushUnclip(dl);
        if (shadow is { } s) { dl.AddCircleFilled(V(sx, cy), half * 0.85f, WithAlpha(s, alpha * 0.6f));
            dl.AddCircleFilled(V(sx, cy), half * 0.65f, WithAlpha(s, alpha * 0.4f));
            dl.AddCircleFilled(V(sx, cy), half * 0.45f, WithAlpha(s, alpha * 0.2f)); }
        if (ring is { } r) dl.AddCircle(V(sx, cy), half + 1.0f, WithAlpha(r, alpha), 0, 3.0f);
        PopUnclip(dl);
    }

    // ─── Helpers ──────────────────────────────────────────────────
    private static float SmoothStep(float x) => x * x * (3f - 2f * x);
    private static float Normalize(float a) { a %= 360f; return a < 0f ? a + 360f : a; }
    private static float Delta(float from, float to)
    {
        float d = to - from;
        while (d > 180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    private static bool TryComputeBearing(Vector3 target, Vector3 origin, float heading,
        float maxDistSq, float extHalf, out float dist, out float delta)
    {
        float dx = target.X - origin.X, dy = target.Y - origin.Y, dz = target.Z - origin.Z;
        float dsq = dx*dx + dy*dy + dz*dz;
        dist = 0f; delta = 0f;
        if (dsq > maxDistSq || dsq < 0.25f) return false;
        float bearing = Normalize(MathF.Atan2(dx, -dz) * (180f / MathF.PI));
        delta = Delta(heading, bearing);
        if (MathF.Abs(delta) > extHalf) return false;
        dist = MathF.Sqrt(dsq);
        return true;
    }

    private static Vector2 V(float x, float y) => new(x, y);
    private static uint C(Vector4 v) => ImGui.ColorConvertFloat4ToU32(v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float LensEdgeAlpha(float delta, float linearHalf, float extHalf)
    {
        float absD = MathF.Abs(delta);
        if (absD <= linearHalf) return 1f;
        return 1f - SmoothStep(MathF.Min(1f, (absD - linearHalf) / (extHalf - linearHalf)));
    }

    private static uint WithAlpha(uint color, float mul)
    {
        uint a = (uint)(((color >> 24) & 0xFFu) * Math.Clamp(mul, 0f, 1f));
        return (color & 0x00FFFFFFu) | (a << 24);
    }

    private static void PushUnclip(ImDrawListPtr dl) =>
        dl.PushClipRect(Vector2.Zero, ImGui.GetIO().DisplaySize, false);
    private static void PopUnclip(ImDrawListPtr dl) => dl.PopClipRect();

    // ─── Debug dump ───────────────────────────────────────────────
    public void DumpNearbyObjects(float radius = 50f)
    {
        var player = objectTable.LocalPlayer;
        if (player == null) { log.Info("[SkyrimCompass debug] No local player."); return; }
        var pp = player.Position;
        var nearby = new List<(float dist, IGameObject obj)>();
        foreach (var obj in objectTable)
        {
            if (obj == null || obj.EntityId == player.EntityId) continue;
            float d = Vector3.Distance(obj.Position, pp);
            if (d <= radius) nearby.Add((d, obj));
        }
        nearby.Sort((a,b) => a.dist.CompareTo(b.dist));
        log.Info($"[SkyrimCompass debug] {nearby.Count} objects within {radius}y:");
        foreach (var (dist, obj) in nearby)
        {
            string extra = "";
            if (obj.ObjectKind == ObjectKind.EventNpc && npcSheet.GetRowOrDefault(obj.BaseId) is { } row)
                extra = $" | Singular=\"{row.Singular}\" | Plural=\"{row.Plural}\"";
            log.Info($"[SkyrimCompass debug] {dist,6:F1}y | Kind={obj.ObjectKind,-19} | BaseId={obj.BaseId,-8} | Name=\"{obj.Name}\"{extra}");
        }
    }
}