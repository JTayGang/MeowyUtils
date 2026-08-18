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

using MoodlesStatusInfo = (int Version, System.Guid GUID, int IconID, string Title, string Description, string CustomVFXPath,
    long ExpireTicks, int Type, int Stacks, int StackSteps, uint Modifiers, System.Guid ChainedStatus,
    int ChainTrigger, string Applier, string Dispeller);
using LociStatusInfo = (int Version, System.Guid GUID, uint IconID, string Title, string Description, string CustomVFXPath,
    long ExpireTicks, byte Type, int Stacks, int StackSteps, int StackToChain, uint Modifiers,
    System.Guid ChainedGUID, byte ChainType, int ChainTrigger, string Applier, string Dispeller);

namespace SkyrimCompass;

public sealed class CompassHud : IDisposable
{
    private readonly IClientState _cs;
    private readonly IObjectTable _ot;
    private readonly ITargetManager _tm;
    private readonly INamePlateGui _npg;
    private readonly ITextureProvider _tp;
    private readonly IFateTable _ft;
    private readonly ICondition _cond;
    private readonly IGameGui _gg;
    private readonly Configuration _cfg;
    private readonly IPluginLog _log;
    private readonly IFontHandle _font;
    private readonly IDalamudPluginInterface _pi;

    private readonly ICallGateSubscriber<nint, List<MoodlesStatusInfo>> _moodlesGet;
    private readonly ICallGateSubscriber<nint, List<LociStatusInfo>> _lociGet;
    private readonly ICallGateSubscriber<int> _moodlesVer;
    private readonly ICallGateSubscriber<(int, int)> _lociVer;

    private PluginIpcState _moodlesIpc = new("Moodles", 4, PluginIpcKind.Moodles);
    private PluginIpcState _lociIpc = new("Loci", 0, PluginIpcKind.Loci);

    private float _lbTracked = 0f, _lbFrozen = 0f, _lbFadeStart = -1f;
    private const float LbFadeDur = 2f, LbDropThresh = 0.4f;

    private readonly (float bar, float tMul, float tOff, Vector4 col)[] _lbLayers = new (float, float, float, Vector4)[3];
    private readonly (float edge, float target, uint col, float tMul, float tOff)[] _ribbons = new (float, float, uint, float, float)[4];

    private ulong _castTarget = 0;
    private float _castTracked = 0f, _castFrozen = 0f, _castFadeStart = -1f;
    private const float CastFadeDur = 0.4f;
    private const int MaxTooltipCache = 200;

    private ulong _lastTargetId = 0;
    private float _dispTargetHp = 1f, _lastRawHp = 1f, _flashAlpha = 0f, _dispShield = 0f;

    private readonly List<(float Remaining, int Icon, string Name, string Desc, System.Guid Guid, int Stacks, int MaxStacks)> _statusBuf = new();

    private readonly Dictionary<System.Guid, (float FirstSeen, long TotalMs)> _moodleTrack = new();
    private readonly Dictionary<System.Guid, (float FirstSeen, long TotalMs)> _lociTrack = new();
    private readonly HashSet<System.Guid> _presentScratch = new();
    private readonly List<System.Guid> _staleGuids = new();
    private float _nextPruneAt;

    private List<MoodlesStatusInfo>? _cachedMoodles;
    private nint _cachedMoodlesTarget = IntPtr.Zero;
    private float _cachedMoodlesAt = -1000f;
    private List<LociStatusInfo>? _cachedLoci;
    private nint _cachedLociTarget = IntPtr.Zero;
    private float _cachedLociAt = -1000f;
    private const float StatusCacheSecs = 0.12f;

    private readonly Dictionary<string, byte[]> _tooltipCache = new();
    private readonly Dictionary<int, string> _durationCache = new();

    // Cache for icon → status text (used to fix tooltips for stacked vanilla statuses)
    private readonly Dictionary<int, (string Name, string Desc)> _iconToStatusText = new();

    private bool _ctxMenuWasOpen;
    private float _ctxFadeChange = -1000f;
    private const float CtxFadeSecs = 0.15f, CtxDimAlpha = 0.33f;

    private bool _isDraggingCompass;
    private Vector2 _dragStartMouse;
    private float _dragStartXOffset;
    private float _dragStartYOffset;

    private readonly Dictionary<ulong, int> _npcMarkers = new();

    private readonly Dictionary<uint, int> _gathIconCache = new();
    private readonly Dictionary<uint, NpcCategory> _npcCatCache = new();
    private readonly Dictionary<uint, string> _titleCache = new();
    private readonly Dictionary<uint, string> _singularCache = new();
    private readonly Dictionary<uint, uint> _roleColorCache = new();
    private readonly Dictionary<uint, string> _actionNameCache = new();
    private readonly Dictionary<uint, (string Name, string Desc)> _statusTextCache = new();
    private readonly Dictionary<int, float> _iconAspectCache = new();

    private Dictionary<string, PlayerIconOverride>? _overrideDict;
    private int _overrideDictVer = -1;

    private readonly ExcelSheet<GatheringPoint> _gathPtSheet;
    private readonly ExcelSheet<GatheringPointBase> _gathPtBaseSheet;
    private readonly ExcelSheet<GatheringType> _gathTypeSheet;
    private readonly ExcelSheet<ENpcResident> _npcSheet;
    private readonly ExcelSheet<ClassJob> _classJobSheet;
    private readonly ExcelSheet<Lumina.Excel.Sheets.Action> _actionSheet;

    private static readonly string[] MenderKw = { "Mender", "Tinker", "Repairman" };
    private static readonly string[] ShopKw = { "Merchant", "Vendor", "Trader", "Sutler", "Supplier", "Junkmonger",
        "Fishmonger", "Dyemonger", "Jeweler", "Apothecary", "Culinarian", "Salvager", "Exchange", "Clothier",
        "Outfitter", "Peddler", "Dealer", "Armorer", "Shopkeep", "Stallkeeper", "Pawnbroker", "Provisioner",
        "Broker", "Proprietor", "Proprietress", "Marketeer", "Weaponsmith", "Tailor", "Herbalist", "Craftsman", "Appraiser" };
    private static readonly string[] SkipperKw = { "Skipper", "Ferryman" };
    private static readonly string[] TicketerKw = { "Ticketer", "Pilot", "Crewman", "Steward" };
    private static readonly string[] ChocoboKeepKw = { "Chocobokeep", "Falcon Porter" };

    private readonly List<(IGameObject? Obj, IFate? Fate, float Dist, float Delta, float T, uint Col, AetheryteNameKind Kind)> _candidates = new();
    private static readonly Comparison<(IGameObject? Obj, IFate? Fate, float Dist, float Delta, float T, uint Col, AetheryteNameKind Kind)> _cmpDistFar = (a, b) => b.Dist.CompareTo(a.Dist);

    private const float IconSizeMul = 1.5f;
    private const float AetheryteIconSizeMul = 1.75f;
    private const float MainBarShareWithTot = 0.65f;
    private const float TargetBarRowGapFrac = 0.03f;

    private static readonly (float Deg, string Label, bool IsMajor)[] Directions =
    [
        (0f, "N", true), (45f, "NE", false), (90f, "E", true), (135f, "SE", false),
        (180f, "S", true), (225f, "SW", false), (270f, "W", true), (315f, "NW", false),
    ];

    private static readonly (float dx, float dy)[] ShadowOffsets8 =
    [
        (-1f,-1f), (0f,-1f), (1f,-1f), (-1f,0f), (1f,0f), (-1f,1f), (0f,1f), (1f,1f),
    ];

    private static readonly (float alpha, float thickness)[] GlowLayers =
    [
        (0.05f, 14f), (0.10f, 10f), (0.18f, 6f), (0.32f, 3.5f), (0.70f, 1.8f),
    ];

    private enum PluginIpcKind { Moodles, Loci }
    private class PluginIpcState
    {
        public string Name;
        public int MinVer;
        public PluginIpcKind Kind;
        public bool Active;
        public float ActiveCheckedAt = -1000f;
        public bool VerOk;
        public float VerCheckedAt = -1000f;
        public PluginIpcState(string name, int minVer, PluginIpcKind kind) { Name = name; MinVer = minVer; Kind = kind; }
    }

    private const float PluginActiveCacheSecs = 5f;

    public CompassHud(IClientState cs, IObjectTable ot, ITargetManager tm, INamePlateGui npg,
        ITextureProvider tp, IFateTable ft, ICondition cond, IGameGui gg, IDataManager dm,
        Configuration cfg, IPluginLog log, IFontHandle font, IDalamudPluginInterface pi)
    {
        _cs = cs; _ot = ot; _tm = tm; _npg = npg; _tp = tp; _ft = ft;
        _cond = cond; _gg = gg; _cfg = cfg; _log = log; _font = font; _pi = pi;

        _moodlesGet = pi.GetIpcSubscriber<nint, List<MoodlesStatusInfo>>("Moodles.GetStatusManagerInfoByPtrV2");
        _lociGet = pi.GetIpcSubscriber<nint, List<LociStatusInfo>>("Loci.GetManagerInfoByPtr");
        _moodlesVer = pi.GetIpcSubscriber<int>("Moodles.Version");
        _lociVer = pi.GetIpcSubscriber<(int, int)>("Loci.ApiVersion");

        _gathPtSheet = dm.GetExcelSheet<GatheringPoint>();
        _gathPtBaseSheet = dm.GetExcelSheet<GatheringPointBase>();
        _gathTypeSheet = dm.GetExcelSheet<GatheringType>();
        _npcSheet = dm.GetExcelSheet<ENpcResident>(ClientLanguage.English);
        _classJobSheet = dm.GetExcelSheet<ClassJob>();
        _actionSheet = dm.GetExcelSheet<Lumina.Excel.Sheets.Action>();

        _npg.OnDataUpdate += OnNamePlateUpdate;

        // Build icon → status text cache for vanilla statuses
        var statusSheet = dm.GetExcelSheet<Lumina.Excel.Sheets.Status>();
        if (statusSheet != null)
        {
            foreach (var row in statusSheet)
            {
                var icon = (int)row.Icon;
                if (icon > 0 && !_iconToStatusText.ContainsKey(icon))
                {
                    var name = row.Name.ToString();
                    var desc = row.Description.ToString();
                    if (!string.IsNullOrEmpty(name))
                        _iconToStatusText[icon] = (name, desc);
                }
            }
        }
    }

    public void Dispose() => _npg.OnDataUpdate -= OnNamePlateUpdate;

    private void OnNamePlateUpdate(INamePlateUpdateContext ctx, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        _npcMarkers.Clear();
        foreach (var h in handlers)
            if (h.MarkerIconId > 0)
                _npcMarkers[h.GameObjectId] = h.MarkerIconId;
    }

    public unsafe void Draw()
    {
        if (!_cfg.Enabled) return;
        if (_cfg.HideDuringCutscenes && (_cond[ConditionFlag.OccupiedInCutSceneEvent] ||
            _cond[ConditionFlag.WatchingCutscene] || _cond[ConditionFlag.WatchingCutscene78]))
            return;

        var player = _ot.LocalPlayer;
        if (player == null) return;

        float hRad = 0f;
        var oPos = player.Position;
        bool gotH = false;

        if (_cfg.UseCameraDirection)
        {
            var cm = CameraManager.Instance();
            var cam = cm != null ? cm->Camera : null;
            if (cam != null && !float.IsNaN(cam->DirH))
            {
                hRad = -cam->DirH;
                if (cam->ZoomMode == CameraZoomMode.FirstPerson) hRad += MathF.PI;
                if (_cfg.UseCameraPosition)
                {
                    var cp = cam->LastPosition;
                    if (!float.IsNaN(cp.X) && !float.IsNaN(cp.Y) && !float.IsNaN(cp.Z))
                        oPos = cp;
                }
                gotH = true;
            }
        }
        if (!gotH)
        {
            if (float.IsNaN(player.Rotation)) return;
            hRad = MathF.PI - player.Rotation;
        }

        float heading = Normalize(hRad * (180f / MathF.PI) + _cfg.RotationOffset);

        var io = ImGui.GetIO();
        var dl = ImGui.GetForegroundDrawList();
        float now = (float)ImGui.GetTime();

        UpdateCompassDrag();

        float bw = _cfg.CompassWidth;
        float bh = _cfg.CompassHeight;
        float bx = (io.DisplaySize.X - bw) * 0.5f + _cfg.XOffset;
        float by = _cfg.YOffset;

        bool inDuty = IsInDutyOrPvp();

        if (_cfg.ShowCompassBar)
        {
            RenderBar(dl, bx, by, bw, bh, heading, player, oPos, now, inDuty);
            TryStartCompassDrag(V(bx, by), V(bx + bw, by + bh));
        }

        float barAlpha = UpdateCtxFade(now);

        var curTarget = _tm.Target;
        var curTot = curTarget?.TargetObject;
        bool hasTot = _cfg.ShowTargetBar && _cfg.ShowTargetOfTargetBar && curTarget != null && curTot != null
            && curTot.GameObjectId != curTarget.GameObjectId && curTot is ICharacter;

        var (mainX, mainW, totX, totW, rowW) = SplitTargetRow(bx, bw, hasTot);
        float rowGap = MathF.Max(2f, bh * 0.12f);
        float tbY = by + bh + rowGap;
        float nameCx = bx + bw * 0.5f;

        float nameBottom = tbY;
        if (_cfg.ShowTargetBar)
            nameBottom = RenderTargetBar(dl, mainX, mainW, tbY, nameCx, rowW, now, barAlpha, inDuty, curTarget);
        if (hasTot)
            RenderTotBar(dl, totX, totW, tbY, player, now, barAlpha, inDuty, curTarget!, curTot!);

        if (_cfg.ShowTargetStatuses && curTarget is IBattleChara targetChara)
            RenderStatuses(dl, targetChara, nameCx, nameBottom, barAlpha);
    }

    private float UpdateCtxFade(float now)
    {
        bool open = IsCtxMenuOpen();
        if (open != _ctxMenuWasOpen) { _ctxFadeChange = now; _ctxMenuWasOpen = open; }
        float t = CtxFadeSecs > 0f ? Math.Clamp((now - _ctxFadeChange) / CtxFadeSecs, 0f, 1f) : 1f;
        float from = open ? 1f : CtxDimAlpha;
        float to = open ? CtxDimAlpha : 1f;
        return Lerp(from, to, SmoothStep(t));
    }

    private bool IsCtxMenuOpen() =>
        _gg.GetAddonByName("ContextMenu").IsVisible || _gg.GetAddonByName("AddonContextSub").IsVisible;

    private static float Project(float d, float halfVis, float halfW, float lens)
    {
        float ext = halfVis * lens;
        float absD = MathF.Min(MathF.Abs(d), ext);
        float u = absD / ext;
        float f = 1f - MathF.Pow(1f - u, lens);
        return (d >= 0f ? 1f : -1f) * halfW * f;
    }

    private void RenderBar(ImDrawListPtr dl, float bx, float by, float bw, float bh, float heading,
        IPlayerCharacter player, Vector3 origin, float now, bool inDuty)
    {
        float cx = bx + bw * 0.5f, cy = by + bh * 0.5f;
        float halfW = bw * 0.5f;
        float halfVis = _cfg.VisibleDegrees * 0.5f;
        float lens = _cfg.LensStrength;
        float ext = halfVis * lens;

        uint bg = C(_cfg.BackgroundColor);
        uint border = C(_cfg.BorderColor);
        uint tick = C(_cfg.TickColor);
        uint card = C(_cfg.CardinalColor);
        uint inter = C(_cfg.IntercardinalColor);
        uint solidBg = (bg & 0x00FFFFFFu) | 0xFF000000u;

        float capHW = bh * 0.44f, capHH = bh * 0.64f;

        float rawLb = GetLB();
        float dispLb = UpdateFadeOut(ref _lbTracked, ref _lbFrozen, ref _lbFadeStart,
            rawLb, rawLb < _lbTracked - LbDropThresh, false, true, now, LbFadeDur, out float lbWipe);
        float lbProg = _cfg.ShowLimitBreakGlow ? dispLb : 0f;
        if (!_cfg.ShowLimitBreakGlow) lbWipe = 0f;

        dl.AddRectFilled(V(bx, by), V(bx + bw, by + bh), bg);
        uint warm = ImGui.ColorConvertFloat4ToU32(new Vector4(0.9f, 0.70f, 0.35f, 0.08f));
        float gw = bw * 0.22f;
        dl.AddRectFilledMultiColor(V(cx - gw, by), V(cx, by + bh), 0u, warm, warm, 0u);
        dl.AddRectFilledMultiColor(V(cx, by), V(cx + gw, by + bh), warm, 0u, 0u, warm);
        dl.AddRectFilledMultiColor(V(bx, by), V(bx + bw * 0.14f, by + bh), 0xAA000000u, 0u, 0u, 0xAA000000u);
        dl.AddRectFilledMultiColor(V(bx + bw * 0.86f, by), V(bx + bw, by + bh), 0u, 0xAA000000u, 0xAA000000u, 0u);
        dl.AddLine(V(bx + 1f, by + 1f), V(bx + bw - 1f, by + 1f), 0x1AFFFFFF, 1f);
        dl.AddRect(V(bx, by), V(bx + bw, by + bh), border, 0f, ImDrawFlags.None, 1.5f);

        if (lbProg > 0f)
        {
            float t = now;
            _lbLayers[0] = (Math.Clamp(lbProg, 0f, 1f), 1.00f, 0.0f, _cfg.LimitBreakGlowColor);
            _lbLayers[1] = (Math.Clamp(lbProg - 1f, 0f, 1f), 1.60f, 3.7f, _cfg.LimitBreakGlowColor2);
            _lbLayers[2] = (Math.Clamp(lbProg - 2f, 0f, 1f), 0.65f, 7.1f, _cfg.LimitBreakGlowColor3);
            foreach (var (bar, tMul, tOff, col) in _lbLayers)
            {
                if (bar <= 0f) continue;
                float segW = bw * 0.5f * bar;
                uint c = C(col);
                float i = PulseIntensity(t * tMul + tOff);
                DrawGlowBracket(dl, bx, by, bw, bh, segW, c, i, t * tMul + tOff, lbWipe, bar, true);
                DrawGlowBracket(dl, bx, by, bw, bh, segW, c, i, t * tMul + tOff, lbWipe, bar, false);
            }
        }

        dl.PushClipRect(V(bx + 1f, by), V(bx + bw - 1f, by + bh), true);
        using var fontScope = _font.Available ? _font.Push() : null;
        float fontSize = ImGui.GetFontSize() * _cfg.FontScale;
        var font = ImGui.GetFont();
        float labelTop = by + bh * 0.12f;
        float labelH = ImGui.CalcTextSize("N").Y * _cfg.FontScale;
        float maxTickH = MathF.Max(2f, (by + bh - 1f) - (labelTop + labelH));

        for (int d = 0; d < 360; d += 5)
        {
            float delta = Delta(heading, d);
            if (MathF.Abs(delta) > ext + 2f) continue;
            float sx = cx + Project(delta, halfVis, halfW, lens);
            bool is90 = d % 90 == 0, is45 = d % 45 == 0, is10 = d % 10 == 0;
            float th = is90 ? bh * 0.52f : is45 ? bh * 0.36f : is10 ? bh * 0.22f : bh * 0.13f;
            th = MathF.Min(th, maxTickH);
            float la = LensEdgeAlpha(delta, halfVis, ext);
            uint tickDraw = WithAlpha(is90 ? card : tick, la);
            dl.AddLine(V(sx, by + bh - th - 1f), V(sx, by + bh - 1f), tickDraw, is90 ? 2f : 1f);
        }

        foreach (var (deg, label, isMajor) in Directions)
        {
            float delta = Delta(heading, deg);
            if (MathF.Abs(delta) > ext + 10f) continue;
            float sx = cx + Project(delta, halfVis, halfW, lens);
            var sz = ImGui.CalcTextSize(label) * _cfg.FontScale;
            float tx = sx - sz.X * 0.5f;
            float la = LensEdgeAlpha(delta, halfVis * 0.88f, ext);
            uint col = WithAlpha(isMajor ? card : inter, la);
            uint shadow = WithAlpha(0xBB000000u, la);
            dl.AddText(font, fontSize, V(tx + 1f, labelTop + 1f), shadow, label);
            dl.AddText(font, fontSize, V(tx, labelTop), col, label);
        }

        RenderMarkers(dl, cx, cy, halfVis, halfW, lens, heading, player, origin, inDuty);

        dl.PopClipRect();

        dl.AddQuadFilled(V(bx, cy - capHH), V(bx + capHW, cy), V(bx, cy + capHH), V(bx - capHW, cy), solidBg);
        dl.AddQuadFilled(V(bx + bw, cy - capHH), V(bx + bw + capHW, cy), V(bx + bw, cy + capHH), V(bx + bw - capHW, cy), solidBg);
        DrawCapOutlines(dl, bx, cy, capHW, capHH, border);
        DrawCapOutlines(dl, bx + bw, cy, capHW, capHH, border);

        const float nH = 10f, nW = 6f;
        dl.AddTriangleFilled(V(cx + 1f, by + nH + 2f), V(cx - nW + 1f, by + 1f), V(cx + nW + 1f, by + 1f), 0x55000000u);
        dl.AddTriangleFilled(V(cx, by + nH + 1f), V(cx - nW, by), V(cx + nW, by), 0xF2FFFFFFu);
    }

    private static void DrawCapOutlines(ImDrawListPtr dl, float cx, float cy, float hw, float hh, uint col, float dotR = 2.5f)
    {
        dl.AddQuad(V(cx, cy - hh), V(cx + hw, cy), V(cx, cy + hh), V(cx - hw, cy), col, 1.5f);
        uint inner = (col & 0x00FFFFFFu) | (((col >> 24) * 6 / 10) << 24);
        float s = 0.52f;
        dl.AddQuad(V(cx, cy - hh * s), V(cx + hw * s, cy), V(cx, cy + hh * s), V(cx - hw * s, cy), inner, 1f);
        dl.AddCircleFilled(V(cx, cy), dotR, col);
    }

    private static void DrawDiamond(ImDrawListPtr dl, float cx, float cy, float hw, float hh, uint col) =>
        dl.AddQuadFilled(V(cx, cy - hh), V(cx + hw, cy), V(cx, cy + hh), V(cx - hw, cy), col);

    private static float PulseIntensity(float t) =>
        (0.75f + 0.25f * MathF.Sin(t * 0.79f)) * (0.92f + 0.08f * MathF.Sin(t * 3.23f + 1.17f));

    private static void DrawGlowLine(ImDrawListPtr dl, Vector2 a, Vector2 b, uint col, float intensity, float t,
        bool fromLeft, float wipe, float fill, bool wipeRev = false)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (len < 1f) return;
        Vector2 dir = d / len;
        Vector2 perp = new(-dir.Y, dir.X);
        const float amp = 5f, waveLen = 26f, flow = 2f, wipeHalf = 0.2f, harm = 0.33f;
        float tipStart = Lerp(0.6f, 1.0f, Math.Clamp(fill, 0f, 1f));
        float flowDir = fromLeft ? -1f : 1f;
        float wipeCentre = Lerp(1f + wipeHalf, -wipeHalf, wipe);
        float freq = 2f * MathF.PI / waveLen;
        float freq2 = freq * 2f;
        float phase = t * flow * flowDir;
        float phase2 = phase * 1.4f + 1.3f;

        int samples = Math.Clamp((int)(len / (waveLen * 0.5f) * 4f) + 2, 3, 24);
        Span<Vector2> pts = stackalloc Vector2[96];
        Span<float> fades = stackalloc float[96];
        for (int i = 0; i < samples; i++)
        {
            float along = len * i / (samples - 1);
            float u = fromLeft ? along / len : 1f - along / len;
            float env = u * u * (3f - 2f * u);
            float wave = MathF.Sin(along * freq + phase) * (1f - harm * u)
                       + MathF.Sin(along * freq2 + phase2) * (harm * u);
            pts[i] = a + dir * along + perp * (amp * env * wave);
            float tip = 1f - SmoothStep(Math.Clamp((u - tipStart) / (1f - tipStart + 1e-4f), 0f, 1f));
            float wipeU = wipeRev ? 1f - u : u;
            float wf = 1f - SmoothStep(Math.Clamp((wipeU - (wipeCentre - wipeHalf)) / (2f * wipeHalf), 0f, 1f));
            fades[i] = tip * wf;
        }

        foreach (var (alpha, thick) in GlowLayers)
            for (int i = 0; i < samples - 1; i++)
            {
                float seg = (fades[i] + fades[i + 1]) * 0.5f;
                if (seg <= 0.002f) continue;
                dl.AddLine(pts[i], pts[i + 1], WithAlpha(col, alpha * intensity * seg), thick);
            }
    }

    private static void DrawGlowBracket(ImDrawListPtr dl, float bx, float by, float bw, float bh,
        float segW, uint col, float intensity, float t, float wipe, float fill, bool fromLeft)
    {
        float x0 = fromLeft ? bx : bx + bw - segW;
        float x1 = fromLeft ? bx + segW : bx + bw;
        DrawGlowLine(dl, V(x0, by), V(x1, by), col, intensity, t, fromLeft, wipe, fill);
        DrawGlowLine(dl, V(x0, by + bh), V(x1, by + bh), col, intensity, t, fromLeft, wipe, fill);
    }

    private static float UpdateFadeOut(ref float tracked, ref float frozen, ref float start,
        float real, bool trigger, bool extResync, bool resyncIfExceeds, float now, float dur, out float wipe)
    {
        if (start < 0f)
        {
            if (trigger) { frozen = tracked; start = now; }
            else tracked = real;
        }
        if (start >= 0f)
        {
            float elapsed = now - start;
            if (extResync || (resyncIfExceeds && real > frozen) || elapsed >= dur)
            {
                start = -1f;
                tracked = real;
                wipe = 0f;
                return tracked;
            }
            wipe = elapsed / dur;
            return frozen;
        }
        wipe = 0f;
        return tracked;
    }

    private static unsafe float GetLB()
    {
        var ui = UIState.Instance();
        if (ui == null) return 0f;
        var lb = ui->LimitBreakController;
        return lb.BarUnits <= 0 ? 0f : Math.Clamp((float)lb.CurrentUnits / lb.BarUnits, 0f, 3f);
    }

    private string? GetCastName(IBattleChara caster)
    {
        if (caster.CastActionType != 1) return null;
        if (!_actionNameCache.TryGetValue(caster.CastActionId, out var name))
        {
            var row = _actionSheet.GetRowOrDefault(caster.CastActionId);
            name = row.HasValue ? row.Value.Name.ToString() : string.Empty;
            _actionNameCache[caster.CastActionId] = name;
        }
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private bool IsInDutyOrPvp() =>
        _cond[ConditionFlag.BoundByDuty] || _cond[ConditionFlag.BoundByDuty56] ||
        _cond[ConditionFlag.BoundByDuty95] || _cond[ConditionFlag.InDeepDungeon] ||
        _cs.IsPvP;

    private (float x, float w, float totX, float totW, float rowW) SplitTargetRow(float cx, float cw, bool hasTot)
    {
        float rowW = cw * Math.Clamp(_cfg.TargetBarWidthFraction, 0.1f, 1f);
        float rowX = cx + cw * 0.5f - rowW * 0.5f;
        if (!hasTot) return (rowX, rowW, 0f, 0f, rowW);
        float mainW = rowW * MainBarShareWithTot;
        float gap = MathF.Max(10f, rowW * TargetBarRowGapFrac);
        float totW = rowW - mainW - gap;
        float totX = rowX + mainW + gap;
        return (rowX, mainW, totX, totW, rowW);
    }

    private uint TargetFillColor(IGameObject obj, bool inDuty)
    {
        if (obj is ICharacter ch && (ch.StatusFlags & StatusFlags.PartyMember) != 0
            && _cfg.ShowPartyRoleIcons && (!_cfg.PartyRoleIconsOnlyInDuty || inDuty))
            return GetRoleColor(ch);
        uint baseCol = MarkerBaseColor(obj);
        return baseCol != 0u ? baseCol : C(_cfg.NpcColor);
    }

    private (Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl) TrapSlice(float x, float y, float w, float h, float taper, float lo, float hi)
    {
        lo = Math.Clamp(lo, 0f, 1f);
        hi = Math.Clamp(hi, 0f, 1f);
        float botX = x + taper, botSpan = w - 2f * taper;
        float topA = x + w * lo, topB = x + w * hi;
        float botA = botX + botSpan * lo, botB = botX + botSpan * hi;
        return (new Vector2(topA, y), new Vector2(topB, y), new Vector2(botB, y + h), new Vector2(botA, y + h));
    }

    private (Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl) TrapFill(float x, float y, float w, float h, float taper, float frac, bool fromRight = false)
    {
        frac = Math.Clamp(frac, 0f, 1f);
        return fromRight
            ? TrapSlice(x, y, w, h, taper, 1f - frac, 1f)
            : TrapSlice(x, y, w, h, taper, 0f, frac);
    }

    private void DrawTrapBar(ImDrawListPtr dl, float x, float y, float w, float h, float fill,
        uint bg, uint fg, float alpha, float? shieldFrac = null, uint? shieldCol = null, bool fromRight = false)
    {
        float taper = MathF.Min(h * 0.9f, w * 0.35f);
        var (bTL, bTR, bBR, bBL) = TrapFill(x, y, w, h, taper, 1f);
        dl.AddQuadFilled(bTL, bTR, bBR, bBL, WithAlpha(bg, alpha));

        const float inset = 2f;
        float innerH = h - inset * 2f;
        float innerTaper = taper * (innerH / h);
        float cf = Math.Clamp(fill, 0f, 1f);
        if (cf > 0f)
        {
            var (fTL, fTR, fBR, fBL) = TrapFill(x + inset, y + inset, w - inset * 2f, innerH, innerTaper, cf, fromRight);
            dl.AddQuadFilled(fTL, fTR, fBR, fBL, WithAlpha(fg, alpha));
        }
        if (shieldFrac.HasValue && shieldFrac.Value > 0f && shieldCol.HasValue)
        {
            float sLo = fromRight ? MathF.Max(0f, 1f - cf - shieldFrac.Value) : cf;
            float sHi = fromRight ? 1f - cf : MathF.Min(1f, cf + shieldFrac.Value);
            if (sHi > sLo)
            {
                var (sTL, sTR, sBR, sBL) = TrapSlice(x + inset, y + inset, w - inset * 2f, innerH, innerTaper, sLo, sHi);
                dl.AddQuadFilled(sTL, sTR, sBR, sBL, WithAlpha(shieldCol.Value, alpha));
            }
        }
        dl.AddLine(V(x + 1f, y + 1f), V(x + w - 1f, y + 1f), WithAlpha(0x1AFFFFFFu, alpha), 1f);
        dl.AddQuad(bTL, bTR, bBR, bBL, WithAlpha(C(_cfg.BorderColor), alpha), 1.5f);
    }

    private void HandleTargetClick(Vector2 min, Vector2 max, IGameObject obj, bool allowLeft)
    {
        if (_ctxMenuWasOpen) return;
        if (!ImGui.IsMouseHoveringRect(min, max, false)) return;
        var io = ImGui.GetIO();
        io.WantCaptureMouse = true;
        if (allowLeft && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        { _tm.Target = obj; return; }
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            OpenCtxMenu(obj);
    }

    private void UpdateCompassDrag()
    {
        if (!_isDraggingCompass) return;

        var io = ImGui.GetIO();
        io.WantCaptureMouse = true;
        ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);

        var delta = ImGui.GetMousePos() - _dragStartMouse;
        float xRange = MathF.Max(0f, (io.DisplaySize.X - _cfg.CompassWidth) * 0.5f);
        float yMax = MathF.Max(0f, io.DisplaySize.Y - _cfg.CompassHeight);
        _cfg.XOffset = Math.Clamp(_dragStartXOffset + delta.X, -xRange, xRange);
        _cfg.YOffset = Math.Clamp(_dragStartYOffset + delta.Y, 0f, yMax);

        if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            _isDraggingCompass = false;
            _cfg.Save(_pi);
        }
    }

    private void TryStartCompassDrag(Vector2 min, Vector2 max)
    {
        if (_isDraggingCompass || _cfg.LockPosition) return;
        if (!ImGui.IsMouseHoveringRect(min, max, false)) return;

        var io = ImGui.GetIO();
        io.WantCaptureMouse = true;
        ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _isDraggingCompass = true;
            _dragStartMouse = ImGui.GetMousePos();
            _dragStartXOffset = _cfg.XOffset;
            _dragStartYOffset = _cfg.YOffset;
        }
    }

    private unsafe void OpenCtxMenu(IGameObject obj)
    {
        if (obj.Address == IntPtr.Zero) return;
        var mod = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentModule.Instance();
        if (mod == null) return;
        var hud = (FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentHUD*)
            mod->GetAgentByInternalId(FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentId.Hud);
        if (hud == null) return;
        hud->OpenContextMenuFromTarget((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address);
    }

    private float RenderTargetBar(ImDrawListPtr dl, float x, float w, float y, float nameCx,
        float nameRowW, float now, float alpha, bool inDuty, IGameObject? curTarget)
    {
        if (curTarget == null) return y;

        uint border = WithAlpha(C(_cfg.BorderColor), alpha);
        uint bg = WithAlpha(C(_cfg.BackgroundColor), alpha);
        uint nameCol = WithAlpha(C(_cfg.CardinalColor), alpha);

        float cx = nameCx;
        bool isChara = curTarget is ICharacter;
        float h = isChara ? MathF.Max(4f, _cfg.TargetBarHeight) : 0f;
        uint fill = WithAlpha(TargetFillColor(curTarget, inDuty), alpha);

        if (isChara)
        {
            var ch = (ICharacter)curTarget;
            float raw = ch.MaxHp > 0f ? Math.Clamp((float)ch.CurrentHp / ch.MaxHp, 0f, 1f) : 0f;
            float rawShield = _cfg.ShowTargetBarShield ? ch.ShieldPercentage / 100f : 0f;

            bool isMinion = curTarget.ObjectKind == ObjectKind.Companion;
            if (isMinion)
            {
                raw = 1f;
                _dispTargetHp = 1f;
                _lastRawHp = 1f;
                _flashAlpha = 0f;
                rawShield = 0f;
                _dispShield = 0f;
            }

            float dt = ImGui.GetIO().DeltaTime;
            if (curTarget.GameObjectId != _lastTargetId)
            {
                _lastTargetId = curTarget.GameObjectId;
                _dispTargetHp = raw;
                _lastRawHp = raw;
                _flashAlpha = 0f;
                _dispShield = rawShield;
            }
            else
            {
                if (!isMinion && raw < _lastRawHp - 0.001f) _flashAlpha = 1f;
                _lastRawHp = raw;
                _dispTargetHp += (raw - _dispTargetHp) * (1f - MathF.Exp(-dt * 14f));
                _dispShield += (rawShield - _dispShield) * (1f - MathF.Exp(-dt * 14f));
            }
            if (!isMinion)
                _flashAlpha = MathF.Max(0f, _flashAlpha - dt / 0.4f);

            DrawTrapBar(dl, x, y, w, h, _dispTargetHp, bg, fill, alpha,
                (!isMinion && _dispShield > 0f) ? _dispShield : null,
                (!isMinion && _dispShield > 0f) ? C(_cfg.TargetBarShieldColor) : null);

            if (!isMinion && _flashAlpha > 0f)
            {
                float lo = MathF.Min(raw, _dispTargetHp);
                float hi = MathF.Max(raw, _dispTargetHp);
                if (hi > lo)
                {
                    float taper = MathF.Min(h * 0.9f, w * 0.35f);
                    const float inset = 2f;
                    float innerH = h - inset * 2f;
                    float innerTaper = taper * (innerH / h);
                    var (fTL, fTR, fBR, fBL) = TrapSlice(x + inset, y + inset, w - inset * 2f, innerH, innerTaper, lo, hi);
                    dl.AddQuadFilled(fTL, fTR, fBR, fBL, WithAlpha(0xFFFFFFFFu, _flashAlpha * 0.5f * alpha));
                }
            }
        }

        using var fontScope = _font.Available ? _font.Push() : null;
        float fontSize = ImGui.GetFontSize() * _cfg.TargetBarFontScale;
        var font = ImGui.GetFont();

        string? castName = curTarget is IBattleChara casting && casting.IsCasting && casting.TotalCastTime > 0f
            ? GetCastName(casting) : null;

        string baseLabel = castName ?? curTarget.Name.TextValue;
        string label = (castName == null && _cfg.ShowTargetLevel && curTarget is ICharacter lv && lv.Level > 0)
            ? $"Lv{lv.Level}  {baseLabel}"
            : baseLabel;

        var sz = ImGui.CalcTextSize(label) * _cfg.TargetBarFontScale;
        float nameGap = MathF.Max(6f, h * 0.5f);
        float nameY = y + h + nameGap;
        float tx = cx - sz.X * 0.5f;

        uint shadow = WithAlpha(0xCC000000u, alpha);
        foreach (var (dx, dy) in ShadowOffsets8)
            dl.AddText(font, fontSize, V(tx + dx, nameY + dy), shadow, label);
        dl.AddText(font, fontSize, V(tx, nameY), nameCol, label);

        float ornHH = fontSize * 0.46f, ornHW = ornHH * 0.69f;
        float ornGap = 6f;
        float textCy = nameY + sz.Y * 0.5f;
        float leftX = tx - ornGap - ornHW, rightX = tx + sz.X + ornGap + ornHW;
        float shHW = ornHW + 2f, shHH = ornHH + 2f;
        DrawDiamond(dl, leftX, textCy, shHW, shHH, shadow);
        DrawDiamond(dl, rightX, textCy, shHW, shHH, shadow);
        DrawCapOutlines(dl, leftX, textCy, ornHW, ornHH, border, ornHW * 0.28f);
        DrawCapOutlines(dl, rightX, textCy, ornHW, ornHH, border, ornHW * 0.28f);

        if (isChara && _cfg.ShowTargetBarRibbons)
        {
            float leftEdge = leftX - ornHW, rightEdge = rightX + ornHW;
            float rowLeft = nameCx - nameRowW * 0.5f, rowRight = nameCx + nameRowW * 0.5f;
            float inset = MathF.Max(8f, nameRowW * 0.06f);
            float rL = MathF.Min(rowLeft + inset, leftEdge - 24f);
            float rR = MathF.Max(rowRight - inset, rightEdge + 24f);
            float t = now;

            _ribbons[0] = (leftEdge, rL, shadow, 0.65f, 7.1f);
            _ribbons[1] = (leftEdge, rL, border, 1.00f, 0.0f);
            _ribbons[2] = (rightEdge, rR, shadow, 1.15f, 5.3f);
            _ribbons[3] = (rightEdge, rR, border, 1.60f, 3.7f);
            foreach (var (edge, target, col, tMul, tOff) in _ribbons)
                DrawGlowLine(dl, V(edge, textCy), V(target, textCy), col, 1f, t * tMul + tOff, true, 0f, 0f);

            if (curTarget.GameObjectId != _castTarget)
            {
                _castTarget = curTarget.GameObjectId;
                _castTracked = 0f;
                _castFadeStart = -1f;
            }

            var bt = curTarget as IBattleChara;
            bool isCasting = bt?.IsCasting ?? false;
            float totalCast = bt?.TotalCastTime ?? 0f;
            float castReal = 0f;
            if (isCasting && totalCast > 0f)
                castReal = Math.Clamp((bt?.CurrentCastTime ?? 0f) / totalCast, 0f, 1f);

            float castDisp = UpdateFadeOut(ref _castTracked, ref _castFrozen, ref _castFadeStart,
                castReal, !isCasting && _castTracked > 0f, isCasting,
                false, t, CastFadeDur, out float castWipe);

            if (castDisp > 0f)
            {
                uint castCol = WithAlpha(C(_cfg.AggroWarningColor), alpha);
                float ci = PulseIntensity(t);
                DrawGlowLine(dl, V(leftEdge, textCy), V(Lerp(leftEdge, rL, castDisp), textCy),
                    castCol, ci, t, true, castWipe, 0f, true);
                DrawGlowLine(dl, V(rightEdge, textCy), V(Lerp(rightEdge, rR, castDisp), textCy),
                    castCol, ci, t, true, castWipe, 0f, true);
            }
        }

        if (_cfg.ShowTargetHealthPercent && isChara && h > 0f)
        {
            float taper = MathF.Min(h * 0.9f, w * 0.35f);
            float bottomX = x + taper;
            float bottomY = y + h;

            float pctSize = ImGui.GetFontSize() * _cfg.TargetBarFontScale;
            float pgap = MathF.Max(4f, pctSize * 0.25f);
            float py = bottomY + pgap;

            string pct = $"{(int)(_dispTargetHp * 100)}%";
            Vector2 psz = ImGui.CalcTextSize(pct) * (pctSize / ImGui.GetFontSize());

            uint pCol = WithAlpha(C(_cfg.CardinalColor), alpha);
            uint pShadow = WithAlpha(0xCC000000u, alpha);

            float px = bottomX;
            foreach (var (dx, dy) in ShadowOffsets8)
                dl.AddText(font, pctSize, V(px + dx, py + dy), pShadow, pct);
            dl.AddText(font, pctSize, V(px, py), pCol, pct);
        }

        float clickTop = y, clickBottom = nameY + sz.Y;
        float clickLeft = MathF.Min(x, leftX - shHW);
        float clickRight = MathF.Max(x + w, rightX + shHW);
        HandleTargetClick(V(clickLeft, clickTop), V(clickRight, clickBottom), curTarget!, false);
        TryStartCompassDrag(V(clickLeft, clickTop), V(clickRight, clickBottom));

        return nameY + sz.Y;
    }

    private void RenderTotBar(ImDrawListPtr dl, float x, float w, float y,
        IPlayerCharacter player, float now, float alpha, bool inDuty, IGameObject curTarget, IGameObject tot)
    {
        if (tot is not ICharacter ch) return;

        bool targetingMe = _cfg.HighlightIfTargetingMe && curTarget.TargetObjectId == player.GameObjectId;
        float h = MathF.Max(4f, _cfg.TargetBarHeight);
        uint fill = targetingMe ? C(_cfg.AggroWarningColor) : TargetFillColor(tot, inDuty);
        float frac = ch.MaxHp > 0f ? Math.Clamp((float)ch.CurrentHp / ch.MaxHp, 0f, 1f) : 0f;
        float pulse = targetingMe ? 0.82f + 0.18f * MathF.Sin(now * 5f) : 1f;

        DrawTrapBar(dl, x, y, w, h, frac,
            WithAlpha(C(_cfg.BackgroundColor), alpha),
            WithAlpha(fill, pulse * alpha),
            alpha);

        using var totFontScope = _font.Available ? _font.Push() : null;
        var font = ImGui.GetFont();

        if (_cfg.ShowTargetHealthPercent && _cfg.ShowTargetOfTargetHealthPercent)
        {
            float taper = MathF.Min(h * 0.9f, w * 0.35f);
            float rightX = x + w - taper;
            float bottomY = y + h;

            float pctSize = ImGui.GetFontSize() * _cfg.TargetBarFontScale;
            float pgap = MathF.Max(4f, pctSize * 0.25f);
            float py = bottomY + pgap;

            string pct = $"{(int)(frac * 100)}%";
            Vector2 psz = ImGui.CalcTextSize(pct) * (pctSize / ImGui.GetFontSize());

            uint pCol = WithAlpha(C(_cfg.CardinalColor), alpha);
            uint pShadow = WithAlpha(0xCC000000u, alpha);

            float px = rightX - psz.X;
            foreach (var (dx, dy) in ShadowOffsets8)
                dl.AddText(font, pctSize, V(px + dx, py + dy), pShadow, pct);
            dl.AddText(font, pctSize, V(px, py), pCol, pct);
        }

        if (_cfg.ShowTargetOfTargetName)
        {
            bool isPlayer = ch.GameObjectId == player.GameObjectId;
            string label = (isPlayer && _cfg.TargetOfTargetShowYou) ? "YOU" : ch.Name.TextValue;
            if (_cfg.TargetOfTargetFirstNameOnly)
            {
                int sp = label.IndexOf(' ');
                if (sp > 0) label = label[..sp];
            }

            float scale = _cfg.TargetBarFontScale;
            var baseSz = ImGui.CalcTextSize(label);
            float maxW = MathF.Max(4f, w - 4f);
            if (baseSz.X > 0f && baseSz.X * scale > maxW)
                scale = maxW / baseSz.X;

            float fSize = ImGui.GetFontSize() * scale;
            var sz = baseSz * scale;
            float tx = x + (w - sz.X) * 0.5f;
            float ty = y + (h - sz.Y) * 0.5f;

            uint nameCol = WithAlpha(C(_cfg.CardinalColor), alpha);
            uint shadow = WithAlpha(0xCC000000u, alpha);
            foreach (var (dx, dy) in ShadowOffsets8)
                dl.AddText(font, fSize, V(tx + dx, ty + dy), shadow, label);
            dl.AddText(font, fSize, V(tx, ty), nameCol, label);
        }

        HandleTargetClick(V(x, y), V(x + w, y + h), ch!, true);
        TryStartCompassDrag(V(x, y), V(x + w, y + h));
    }

private void RenderStatuses(ImDrawListPtr dl, IBattleChara ch, float cx, float y, float alpha)
{
    float size = MathF.Max(8f, _cfg.TargetStatusIconSize);
    float hGap = size * 0.25f;
    int max = Math.Max(1, _cfg.TargetStatusMaxIcons);
    float now = (float)ImGui.GetTime();

    PruneTrackers(now);
    _statusBuf.Clear();

    // Vanilla statuses
    foreach (var st in ch.StatusList)
    {
        if (_statusBuf.Count >= max) break;
        if (st.StatusId == 0) continue;
        if (st.GameData.ValueNullable is not { } row || row.Icon == 0) continue;
        float rem = (row.IsPermanent || row.IsFcBuff) ? 0f : st.RemainingTime;
        var (name, desc) = GetStatusText(row);
        _statusBuf.Add((rem, (int)row.Icon, name, desc, System.Guid.Empty, (int)st.Param, (int)row.MaxStacks));
    }

    // Moodles / Loci
    if (ch.Address != IntPtr.Zero)
    {
        if (_statusBuf.Count < max && IsPluginActive(_moodlesIpc, now) && IsVerOk(_moodlesIpc, now))
            AppendPluginStatuses(ch.Address, max, now, _moodlesGet, ref _cachedMoodles,
                ref _cachedMoodlesTarget, ref _cachedMoodlesAt, _moodleTrack, s => s.GUID, s => s.IconID,
                s => (s.Title, s.Description), s => s.ExpireTicks, s => s.Stacks);
        if (_statusBuf.Count < max && IsPluginActive(_lociIpc, now) && IsVerOk(_lociIpc, now))
            AppendPluginStatuses(ch.Address, max, now, _lociGet, ref _cachedLoci,
                ref _cachedLociTarget, ref _cachedLociAt, _lociTrack, s => s.GUID, s => (int)s.IconID,
                s => (s.Title, s.Description), s => s.ExpireTicks, s => s.Stacks);
    }

    if (_statusBuf.Count == 0) return;

    int n = _statusBuf.Count;
    float width = n * size + (n - 1) * hGap;
    float maxWidth = max * size + (max - 1) * hGap;
    float startX = _cfg.TargetStatusIconAlignLeft ? cx - maxWidth * 0.5f
                  : _cfg.TargetStatusIconAlignRight ? cx + maxWidth * 0.5f - width
                  : cx - width * 0.5f;
    float topGap = size * 0.15f;
    float halfH = size * 0.5f * GetIconAspect(_statusBuf[0].Icon);
    float scy = y + topGap + halfH;
    float fSize = MathF.Max(9f, size * 0.8f);
    var font = ImGui.GetFont();
    float textGap = -size * 0.12f;
    float tipGap = MathF.Max(4f, size * 0.15f);

    TryStartCompassDrag(V(startX, y), V(startX + width, scy + halfH + fSize + tipGap));

    for (int i = 0; i < n; i++)
    {
        var (rem, icon, name, desc, guid, stacks, maxStacks) = _statusBuf[i];
        float sx = startX + i * (size + hGap) + size * 0.5f;

        int displayIcon = icon;
        string displayName = name;
        string displayDesc = desc;

        if (stacks > 1)
        {
            if (guid != System.Guid.Empty)
            {
                // Moodles/Loci – always use the offset
                displayIcon = icon + stacks - 1;
                // Keep plugin-supplied name/desc
            }
            else if (maxStacks > 1)
            {
                int candidateIcon = icon + Math.Min(stacks, maxStacks) - 1;

                if (_iconToStatusText.TryGetValue(candidateIcon, out var data))
                {
                    // If the name matches, it's safe to use the candidate
                    if (string.Equals(data.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        displayIcon = candidateIcon;
                        displayName = data.Name;
                        displayDesc = data.Desc;
                    }
                }
                else
                {
                    displayIcon = candidateIcon;
                }
            }
        }

        // Draw the icon
        if (!TryDrawIcon(dl, displayIcon, sx, scy, size, alpha, false, 1.0f, false))
        {
            if (displayIcon != icon)
                TryDrawIcon(dl, icon, sx, scy, size, alpha, false, 1.0f, false);
        }

        // Duration label
        string? durLabel = GetDurationLabel(rem);
        float hoverBottom = scy + halfH;
        if (durLabel != null)
        {
            Vector2 lsz = ImGui.CalcTextSize(durLabel) * (fSize / ImGui.GetFontSize());
            float lx = sx - lsz.X * 0.5f;
            float ly = scy + halfH + textGap;
            dl.AddText(font, fSize, V(lx + 1f, ly + 1f), WithAlpha(0xCC000000u, alpha), durLabel);
            dl.AddText(font, fSize, V(lx, ly), WithAlpha(0xFFFFFFFFu, alpha), durLabel);
            hoverBottom = ly + lsz.Y;
        }

        // Tooltip
        if (ImGui.IsMouseHoveringRect(V(sx - size * 0.5f, scy - halfH), V(sx + size * 0.5f, hoverBottom), false))
        {
            ImGui.SetNextWindowPos(V(sx, hoverBottom + tipGap), ImGuiCond.Always, V(0.5f, 0f));
            var tipBg = _cfg.BackgroundColor;
            ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(tipBg.X, tipBg.Y, tipBg.Z, tipBg.W * alpha));
            ImGui.BeginTooltip();
            ImGuiHelpers.SeStringWrapped(GetFormattedTooltip(displayName, displayDesc), new SeStringDrawParams { WrapWidth = 345f });
            ImGui.EndTooltip();
            ImGui.PopStyleColor();
        }
    }
}

    private string? GetDurationLabel(float rem)
    {
        if (rem <= 0f) return null;
        int tier, val;
        if (rem < 60f) { tier = 0; val = (int)MathF.Ceiling(rem); }
        else if (rem < 3600f) { tier = 1; val = (int)(rem / 60f); }
        else if (rem < 86400f) { tier = 2; val = (int)(rem / 3600f); }
        else if (rem < 777600f) { tier = 3; val = (int)(rem / 86400f); }
        else return "9+d";
        int key = tier * 100_000 + val;
        if (!_durationCache.TryGetValue(key, out var s))
            _durationCache[key] = s = tier switch { 0 => $"{val}", 1 => $"{val}m", 2 => $"{val}h", _ => $"{val}d" };
        return s;
    }

    private bool IsPluginActive(PluginIpcState st, float now)
    {
        if (now - st.ActiveCheckedAt < PluginActiveCacheSecs) return st.Active;
        st.ActiveCheckedAt = now;
        st.Active = false;
        foreach (var p in _pi.InstalledPlugins)
            if (p.IsLoaded && p.InternalName == st.Name) { st.Active = true; break; }
        return st.Active;
    }

    private bool IsVerOk(PluginIpcState st, float now)
    {
        if (now - st.VerCheckedAt < PluginActiveCacheSecs) return st.VerOk;
        st.VerCheckedAt = now;
        bool was = st.VerOk;
        try
        {
            if (st.Kind == PluginIpcKind.Moodles)
                st.VerOk = _moodlesVer.InvokeFunc() >= st.MinVer;
            else
            { _lociVer.InvokeFunc(); st.VerOk = true; }
        }
        catch { st.VerOk = false; }
        if (was && !st.VerOk)
            _log.Warning($"[SkyrimCompass] {st.Name} IPC version no longer compatible – status icons disabled.");
        return st.VerOk;
    }

    private void AppendPluginStatuses<T>(
        nint addr, int max, float now,
        ICallGateSubscriber<nint, List<T>> getter,
        ref List<T>? cache, ref nint cacheTarget, ref float cacheAt,
        Dictionary<System.Guid, (float FirstSeen, long TotalMs)> tracker,
        Func<T, System.Guid> guidSel,
        Func<T, int> iconSel,
        Func<T, (string Name, string Desc)> textSel,
        Func<T, long> expireSel,
        Func<T, int> stacksSel)
    {
        if (cache == null || addr != cacheTarget || now - cacheAt >= StatusCacheSecs)
        {
            cacheTarget = addr;
            cacheAt = now;
            try { cache = getter.InvokeFunc(addr); }
            catch { cache = null; }

            if (cache != null && tracker.Count > 0)
            {
                _presentScratch.Clear();
                foreach (var s in cache) _presentScratch.Add(guidSel(s));
                _staleGuids.Clear();
                foreach (var g in tracker.Keys)
                    if (!_presentScratch.Contains(g)) _staleGuids.Add(g);
                foreach (var g in _staleGuids) tracker.Remove(g);
            }
        }
        if (cache == null) return;

        foreach (var s in cache)
        {
            if (_statusBuf.Count >= max) break;
            var g = guidSel(s);
            int icon = iconSel(s);
            if (icon <= 0) continue;
            if (GuidInBuffer(g)) continue;
            var (name, desc) = textSel(s);
            long expire = expireSel(s);
            float rem = EstimateRemaining(tracker, g, expire, now);
            int stacks = stacksSel(s);
            _statusBuf.Add((rem, icon, name, desc, g, stacks, 0));
        }
    }

    private bool GuidInBuffer(System.Guid g)
    {
        for (int i = 0; i < _statusBuf.Count; i++)
            if (_statusBuf[i].Guid == g) return true;
        return false;
    }

    private static float EstimateRemaining(Dictionary<System.Guid, (float FirstSeen, long TotalMs)> tracker,
        System.Guid g, long expireMs, float now)
    {
        if (expireMs < 0) return 0f;
        if (!tracker.TryGetValue(g, out var tracked) || tracked.TotalMs != expireMs)
        {
            tracked = (now, expireMs);
            tracker[g] = tracked;
        }
        return MathF.Max(0f, expireMs / 1000f - (now - tracked.FirstSeen));
    }

    private void PruneTrackers(float now)
    {
        if (now < _nextPruneAt) return;
        _nextPruneAt = now + 60f;
        PruneTracker(_moodleTrack, now);
        PruneTracker(_lociTrack, now);
    }

    private void PruneTracker(Dictionary<System.Guid, (float FirstSeen, long TotalMs)> tracker, float now)
    {
        if (tracker.Count == 0) return;
        _staleGuids.Clear();
        foreach (var (g, tracked) in tracker)
            if (now - tracked.FirstSeen > tracked.TotalMs / 1000f + 30f)
                _staleGuids.Add(g);
        foreach (var g in _staleGuids) tracker.Remove(g);
    }

    private static readonly Dictionary<string, ushort> _namedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WhiteNormal"] = 0, ["White"] = 1, ["Grey1"]=2, ["Grey2"]=3, ["Grey3"]=4, ["Grey4"]=5,
        ["Grey5"]=6, ["Black"]=7, ["LightYellow"]=8, ["Red"]=17, ["DarkRed"]=19, ["Green"]=45,
        ["DarkGreen"]=47, ["WarmSeaBlue"]=52, ["Orange"]=500, ["LightBlue"]=502, ["Yellow"]=514,
        ["Gold"]=540, ["DarkBlue"]=543, ["LightGreen"]=551, ["Pink"]=561,
    };
    private static readonly Regex _formatTagRegex = new(@"(\[color=[0-9a-zA-Z]+\])|(\[/color\])|(\[glow=[0-9a-zA-Z]+\])|(\[/glow\])|(\[i\])|(\[/i\])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryResolveColor(string val, out ushort key) =>
        ushort.TryParse(val, out key) || _namedColors.TryGetValue(val, out key);

    private static void AppendFormat(SeStringBuilder sb, string raw)
    {
        bool col=false, glow=false, ital=false;
        int last=0;
        foreach (Match m in _formatTagRegex.Matches(raw))
        {
            if (m.Index > last) sb.AddText(raw[last..m.Index]);
            last = m.Index + m.Length;
            var tag = m.Value;
            if (tag.StartsWith("[color=", StringComparison.OrdinalIgnoreCase))
            { if (TryResolveColor(tag[7..^1], out var id)) { sb.AddUiForeground(id); col = true; } }
            else if (tag == "[/color]") { if (col) { sb.AddUiForegroundOff(); col = false; } }
            else if (tag.StartsWith("[glow=", StringComparison.OrdinalIgnoreCase))
            { if (TryResolveColor(tag[6..^1], out var id)) { sb.AddUiGlow(id); glow = true; } }
            else if (tag == "[/glow]") { if (glow) { sb.AddUiGlowOff(); glow = false; } }
            else if (tag == "[i]") { sb.AddItalicsOn(); ital = true; }
            else if (ital) { sb.AddItalicsOff(); ital = false; }
        }
        if (last < raw.Length) sb.AddText(raw[last..]);
        if (col) sb.AddUiForegroundOff();
        if (glow) sb.AddUiGlowOff();
        if (ital) sb.AddItalicsOff();
    }

    private byte[] GetFormattedTooltip(string name, string desc)
    {
        string key = $"{name}\0{desc}";
        if (_tooltipCache.TryGetValue(key, out var cached)) return cached;
        if (_tooltipCache.Count >= MaxTooltipCache) _tooltipCache.Clear();
        var sb = new SeStringBuilder();
        AppendFormat(sb, name);
        if (!string.IsNullOrWhiteSpace(desc))
        {
            sb.AddText("\n");
            AppendFormat(sb, desc);
        }
        var bytes = sb.Encode();
        _tooltipCache[key] = bytes;
        return bytes;
    }

    private void RenderMarkers(ImDrawListPtr dl, float cx, float cy, float halfVis, float halfW,
        float lens, float heading, IPlayerCharacter player, Vector3 origin, bool inDuty)
    {
        float maxDist = _cfg.MaxMarkerDistance;
        float maxDistSq = maxDist * maxDist;
        float fateMax = maxDist * _cfg.FateDistanceMultiplier;
        float fateMaxSq = fateMax * fateMax;
        float ext = halfVis * lens;
        bool showRole = _cfg.ShowPartyRoleIcons && (!_cfg.PartyRoleIconsOnlyInDuty || inDuty);

        _candidates.Clear();

        if (_cfg.ShowAnyMarkers)
        {
            foreach (var obj in _ot)
            {
                if (obj == null || obj.EntityId == player.EntityId) continue;
                if (!TryBearing(obj.Position, origin, heading, maxDistSq, ext, out float dist, out float delta)) continue;
                uint col = MarkerColor(obj, player, out var kind, inDuty);
                if (col == 0) continue;
                _candidates.Add((obj, null, dist, delta, 1f - dist / maxDist, col, kind));
            }
        }

        if (_cfg.ShowFates)
        {
            foreach (var fate in _ft)
            {
                if (fate == null || (fate.State != FateState.Running && fate.State != FateState.Preparing)) continue;
                if (!TryBearing(fate.Position, origin, heading, fateMaxSq, ext, out float dist, out float delta)) continue;
                _candidates.Add((null, fate, dist, delta, 1f - dist / fateMax, 0u, AetheryteNameKind.None));
            }
        }

        if (_candidates.Count == 0) return;
        _candidates.Sort(_cmpDistFar);

        foreach (var cand in _candidates)
        {
            float delta = cand.Delta, t = cand.T;
            float sx = cx + Project(delta, halfVis, halfW, lens);
            float alpha = ComputeFade(t) * LensEdgeAlpha(delta, halfVis, ext);

            if (cand.Fate is { } fate)
            {
                float size = Lerp(_cfg.FateIconMinSize, _cfg.FateIconMaxSize, t);
                if (!(fate.IconId > 0 && TryDrawIcon(dl, (int)fate.IconId, sx, cy, size, alpha, false, 1.0f, true)))
                    DrawDotFilled(dl, sx, cy, (3f + 7f * t) * 2f, C(_cfg.FateColor), alpha);
                continue;
            }

            var obj = cand.Obj!;
            uint col = cand.Col;
            int iconId = 0;
            float iconSize = 0f;
            bool isAetheryte = cand.Kind != AetheryteNameKind.None;

            if (_cfg.ShowAetheryteIcons && isAetheryte)
            {
                iconId = GetAetheryteIconId(cand.Kind);
                iconSize = Lerp(_cfg.AetheryteIconMinSize, _cfg.AetheryteIconMaxSize, t) * AetheryteIconSizeMul;
            }
            else if (obj.ObjectKind == ObjectKind.EventNpc && TryGetNpcIcon(obj, out int npcIcon))
            {
                iconId = npcIcon;
                iconSize = Lerp(_cfg.NpcQuestIconMinSize, _cfg.NpcQuestIconMaxSize, t) * IconSizeMul;
            }
            else if (_cfg.ShowGatheringIcons && obj.ObjectKind == ObjectKind.GatheringPoint)
            {
                int gIcon = GetGatheringIcon(obj.BaseId);
                if (gIcon > 0) { iconId = gIcon; iconSize = Lerp(_cfg.GatheringIconMinSize, _cfg.GatheringIconMaxSize, t); }
            }
            else if (_cfg.ShowTreasureIcons && obj.ObjectKind == ObjectKind.Treasure)
            {
                iconId = _cfg.TreasureIconId;
                iconSize = Lerp(_cfg.TreasureMinSize, _cfg.TreasureMaxSize, t);
            }

            bool drew = iconId > 0 && TryDrawIcon(dl, iconId, sx, cy, iconSize, alpha, false, 1.0f, true);

            if (!drew)
            {
                if (obj.ObjectKind == ObjectKind.Pc)
                {
                    float pSize = Lerp(_cfg.PartyRoleIconMinSize, _cfg.PartyRoleIconMaxSize, t);
                    bool drewJob = false;
                    if (showRole && obj is ICharacter ch && (ch.StatusFlags & StatusFlags.PartyMember) != 0)
                    {
                        int jobIcon = ch.ClassJob.RowId > 0 ? (int)(62000 + ch.ClassJob.RowId) : 0;
                        if (jobIcon > 0)
                        {
                            float drawSize = pSize * IconSizeMul;
                            uint roleCol = GetRoleColor(ch);
                            DrawRingShadow(dl, sx, cy, drawSize * 0.5f, roleCol, roleCol, alpha);
                            TryDrawIcon(dl, jobIcon, sx, cy, drawSize, alpha, false, 1.0f, true);
                            drewJob = true;
                        }
                    }
                    if (!drewJob)
                    {
                        var ov = FindOverride(obj.Name.TextValue);
                        if (ov != null)
                        {
                            float ovSize = pSize * IconSizeMul;
                            float half = ovSize * 0.5f;
                            DrawRingShadow(dl, sx, cy, half,
                                ov.ShowBorder ? C(ov.BorderColor) : null,
                                ov.ShowFill ? C(ov.FillColor) : null, alpha);
                            if (!(ov.IconBaseId > 0 && TryDrawIcon(dl, ov.IconBaseId, sx, cy, ovSize, alpha, ov.ClipToCircle, ov.SizeMultiplier, true)))
                                DrawDotFilled(dl, sx, cy, pSize, ov.ShowBorder ? C(ov.BorderColor) : col, alpha);
                        }
                        else
                        {
                            bool isFriend = _cfg.SolidFriendDots && obj is ICharacter ch2 && (ch2.StatusFlags & StatusFlags.Friend) != 0;
                            if (isFriend) DrawDotFilled(dl, sx, cy, pSize, col, alpha);
                            else DrawDotHollow(dl, sx, cy, pSize, col, alpha);
                        }
                    }
                }
                else
                {
                    (float min, float max, bool filled) dot = isAetheryte ? (_cfg.AetheryteIconMinSize, _cfg.AetheryteIconMaxSize, true)
                        : obj.ObjectKind == ObjectKind.EventNpc ? (_cfg.NpcQuestIconMinSize, _cfg.NpcQuestIconMaxSize, false)
                        : obj.ObjectKind == ObjectKind.BattleNpc ? (_cfg.EnemyMinSize, _cfg.EnemyMaxSize, true)
                        : obj.ObjectKind == ObjectKind.Treasure ? (_cfg.TreasureMinSize, _cfg.TreasureMaxSize, true)
                        : (6f, 20f, true);
                    float ds = Lerp(dot.min, dot.max, t);
                    if (dot.filled) DrawDotFilled(dl, sx, cy, ds, col, alpha);
                    else DrawDotHollow(dl, sx, cy, ds, col, alpha);
                }
            }
        }
    }

    private float ComputeFade(float t)
    {
        float near = _cfg.DotNearZone, far = _cfg.DotFarZone, mid = _cfg.DotMidAlpha;
        if (t >= near) return 1f;
        if (t >= far) return mid + (1f - mid) * SmoothStep((t - far) / (near - far));
        return mid * SmoothStep(t / far);
    }

    private bool TryDrawIcon(ImDrawListPtr dl, int iconId, float sx, float cy, float size, float alpha,
        bool clipCircle = false, float zoom = 1.0f, bool unclip = true)
    {
        if (!_tp.TryGetFromGameIcon(new GameIconLookup((uint)iconId), out var shared)) return false;
        var tex = shared.GetWrapOrEmpty();
        uint tint = WithAlpha(0xFFFFFFFFu, alpha);
        float hw, hh;
        Vector2 uvMin, uvMax;
        if (clipCircle)
        {
            hw = hh = size * 0.5f;
            float uvHalf = 0.5f / Math.Max(0.01f, zoom);
            uvMin = new(0.5f - uvHalf, 0.5f - uvHalf);
            uvMax = new(0.5f + uvHalf, 0.5f + uvHalf);
        }
        else
        {
            hw = size * 0.5f * Math.Max(0.01f, zoom);
            hh = hw * (tex.Size.X > 0f ? tex.Size.Y / tex.Size.X : 1f);
            uvMin = Vector2.Zero; uvMax = Vector2.One;
        }
        if (unclip) PushUnclip(dl);
        dl.AddImageRounded(tex.Handle, V(sx - hw, cy - hh), V(sx + hw, cy + hh),
            uvMin, uvMax, tint, clipCircle ? hw : 0f, ImDrawFlags.RoundCornersAll);
        if (unclip) PopUnclip(dl);
        return true;
    }

    private float GetIconAspect(int iconId)
    {
        if (_iconAspectCache.TryGetValue(iconId, out float asp)) return asp;
        if (!_tp.TryGetFromGameIcon(new GameIconLookup((uint)iconId), out var shared)) return 1f;
        var size = shared.GetWrapOrEmpty().Size;
        asp = size.X > 0f ? size.Y / size.X : 1f;
        _iconAspectCache[iconId] = asp;
        return asp;
    }

    private int GetGatheringIcon(uint baseId)
    {
        if (_gathIconCache.TryGetValue(baseId, out int cached)) return cached;
        int icon = 0;
        if (_gathPtSheet.GetRowOrDefault(baseId) is { } gp &&
            _gathPtBaseSheet.GetRowOrDefault(gp.GatheringPointBase.RowId) is { } gpb &&
            _gathTypeSheet.GetRowOrDefault(gpb.GatheringType.RowId) is { } gt)
            icon = gt.IconMain;
        _gathIconCache[baseId] = icon;
        return icon;
    }

    private PlayerIconOverride? FindOverride(string name)
    {
        if (_overrideDict == null || _overrideDictVer != _cfg.PlayerIconOverridesVersion)
        {
            _overrideDict = new Dictionary<string, PlayerIconOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _cfg.PlayerIconOverrides)
                if (!string.IsNullOrEmpty(e.PlayerName))
                    _overrideDict[e.PlayerName] = e;
            _overrideDictVer = _cfg.PlayerIconOverridesVersion;
        }
        _overrideDict.TryGetValue(name, out var found);
        return found;
    }

    private uint GetRoleColor(ICharacter ch)
    {
        uint rowId = ch.ClassJob.RowId;
        if (_roleColorCache.TryGetValue(rowId, out uint packed)) return packed;
        uint col = 0u;
        if (_classJobSheet.GetRowOrDefault(rowId) is { } row)
        {
            col = row.Role switch
            {
                1 => C(new Vector4(0.36f, 0.48f, 0.76f, 0.90f)),
                2 or 3 => C(new Vector4(0.84f, 0.30f, 0.30f, 0.90f)),
                4 => C(new Vector4(0.30f, 0.69f, 0.49f, 0.90f)),
                _ => C(new Vector4(0.54f, 0.54f, 0.54f, 0.85f)),
            };
        }
        else col = C(new Vector4(0.54f, 0.54f, 0.54f, 0.85f));
        _roleColorCache[rowId] = col;
        return col;
    }

    private string GetTitle(uint baseId)
    {
        if (_titleCache.TryGetValue(baseId, out var cached)) return cached;
        string v = _npcSheet.GetRowOrDefault(baseId) is { } row ? row.Title.ToString() : "";
        _titleCache[baseId] = v;
        return v;
    }

    private string GetSingular(uint baseId)
    {
        if (_singularCache.TryGetValue(baseId, out var cached)) return cached;
        string v = _npcSheet.GetRowOrDefault(baseId) is { } row ? row.Singular.ToString() : "";
        _singularCache[baseId] = v;
        return v;
    }

    private (string Name, string Desc) GetStatusText(Lumina.Excel.Sheets.Status row)
    {
        if (_statusTextCache.TryGetValue(row.RowId, out var cached)) return cached;
        var v = (row.Name.ToString(), row.Description.ToString());
        _statusTextCache[row.RowId] = v;
        return v;
    }

    private static bool HasKw(string text, string[] kw)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var k in kw) if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private bool MatchesKw(uint baseId, string[] kw) =>
        HasKw(GetTitle(baseId), kw) || HasKw(GetSingular(baseId), kw);

    private enum NpcCategory { None, Mender, Shop, Skipper, Ticketer, ChocoboKeep }

    private NpcCategory ClassifyNpc(uint baseId)
    {
        if (_npcCatCache.TryGetValue(baseId, out var cached)) return cached;
        var cat = MatchesKw(baseId, MenderKw) ? NpcCategory.Mender
            : MatchesKw(baseId, ShopKw) ? NpcCategory.Shop
            : MatchesKw(baseId, SkipperKw) ? NpcCategory.Skipper
            : MatchesKw(baseId, TicketerKw) ? NpcCategory.Ticketer
            : MatchesKw(baseId, ChocoboKeepKw) ? NpcCategory.ChocoboKeep
            : NpcCategory.None;
        _npcCatCache[baseId] = cat;
        return cat;
    }

    private bool TryGetNpcIcon(IGameObject obj, out int iconId)
    {
        if (_cfg.ShowNpcQuestIcons && _npcMarkers.TryGetValue(obj.GameObjectId, out iconId)) return true;
        switch (ClassifyNpc(obj.BaseId))
        {
            case NpcCategory.Mender when _cfg.ShowShopIcons: iconId = _cfg.MenderIconId; return true;
            case NpcCategory.Shop when _cfg.ShowShopIcons: iconId = _cfg.ShopIconId; return true;
            case NpcCategory.Skipper when _cfg.ShowFastTravelIcons: iconId = _cfg.FastTravelIconId; return true;
            case NpcCategory.Ticketer when _cfg.ShowFastTravelIcons: iconId = _cfg.FastTravelTicketerIconId; return true;
            case NpcCategory.ChocoboKeep when _cfg.ShowFastTravelIcons: iconId = _cfg.ChocoboKeepIconId; return true;
            default: iconId = 0; return false;
        }
    }

    private enum AetheryteNameKind { None, Big, Shard }

    private AetheryteNameKind ClassifyAetheryte(IGameObject obj)
    {
        bool shard = !string.IsNullOrEmpty(_cfg.AethernetShardName)
            && obj.Name.TextValue.Contains(_cfg.AethernetShardName, StringComparison.OrdinalIgnoreCase);
        if (obj.ObjectKind == ObjectKind.Aetheryte) return shard ? AetheryteNameKind.Shard : AetheryteNameKind.Big;
        return shard ? AetheryteNameKind.Shard : AetheryteNameKind.None;
    }

    private int GetAetheryteIconId(AetheryteNameKind kind) =>
        kind == AetheryteNameKind.Shard ? _cfg.AethernetShardIconId : _cfg.AetheryteIconId;

    private uint MarkerColor(IGameObject obj, IPlayerCharacter player, out AetheryteNameKind kind, bool inDuty)
    {
        kind = AetheryteNameKind.None;
        switch (obj.ObjectKind)
        {
            case ObjectKind.Pc: return _cfg.ShowPlayers ? MarkerBaseColor(obj) : 0u;
            case ObjectKind.BattleNpc:
                if (!_cfg.ShowEnemies) return 0u;
                if (obj is not IBattleNpc bnpc || bnpc.BattleNpcKind != BattleNpcSubKind.Combatant) return 0u;
                if (_cfg.EnemiesOnlyIfEngaged && !(bnpc.StatusFlags.HasFlag(StatusFlags.InCombat) && player.StatusFlags.HasFlag(StatusFlags.InCombat)))
                    return 0u;
                return MarkerBaseColor(obj);
            case ObjectKind.EventNpc:
                if (TryAetheryteColor(obj, out uint col, out kind)) return col;
                if (!_cfg.ShowNpcs) return 0u;
                if (_cfg.NpcsOnlyIfTargetable && !obj.IsTargetable) return 0u;
                return MarkerBaseColor(obj);
            case ObjectKind.EventObj:
                if (TryAetheryteColor(obj, out col, out kind)) return col;
                return 0u;
            case ObjectKind.GatheringPoint:
                if (!_cfg.ShowGatheringNodes) return 0u;
                if (_cfg.GatheringOnlyIfTargetable && !obj.IsTargetable) return 0u;
                return MarkerBaseColor(obj);
            case ObjectKind.Treasure:
                return _cfg.ShowTreasure ? MarkerBaseColor(obj) : 0u;
            case ObjectKind.Aetheryte:
                TryAetheryteColor(obj, out uint aCol, out kind);
                return aCol;
            default: return 0u;
        }
    }

    private bool TryAetheryteColor(IGameObject obj, out uint col, out AetheryteNameKind kind)
    {
        kind = ClassifyAetheryte(obj);
        if (kind == AetheryteNameKind.None) { col = 0u; return false; }
        bool hidden = !_cfg.ShowAetherytes || (kind == AetheryteNameKind.Shard && !_cfg.ShowAethernetShards);
        col = hidden ? 0u : C(_cfg.AetheryteColor);
        return true;
    }

    private uint MarkerBaseColor(IGameObject obj) => obj.ObjectKind switch
    {
        ObjectKind.Pc => C(_cfg.PlayerColor),
        ObjectKind.BattleNpc when obj is IBattleNpc b && b.BattleNpcKind == BattleNpcSubKind.Combatant => C(_cfg.EnemyColor),
        ObjectKind.EventNpc => C(_cfg.NpcColor),
        ObjectKind.GatheringPoint => C(_cfg.GatheringColor),
        ObjectKind.Treasure => C(_cfg.TreasureColor),
        _ => 0u,
    };

    private static void DrawDotFilled(ImDrawListPtr dl, float sx, float cy, float size, uint col, float alpha) =>
        DrawDot(dl, sx, cy, size, col, alpha, true);
    private static void DrawDotHollow(ImDrawListPtr dl, float sx, float cy, float size, uint col, float alpha) =>
        DrawDot(dl, sx, cy, size, col, alpha, false);

    private static void DrawDot(ImDrawListPtr dl, float sx, float cy, float size, uint col, float alpha, bool filled)
    {
        float r = size * 0.5f;
        if (filled) dl.AddCircleFilled(V(sx, cy), r, WithAlpha(col, alpha));
        else dl.AddCircle(V(sx, cy), r, WithAlpha(col, alpha), 0, 2.0f);
        dl.AddCircle(V(sx, cy), r + 0.8f, WithAlpha(filled ? 0x66000000u : 0x33000000u, alpha));
    }

    private static void DrawRingShadow(ImDrawListPtr dl, float sx, float cy, float half,
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

    private static float SmoothStep(float x) => x * x * (3f - 2f * x);
    private static float Normalize(float a) { a %= 360f; return a < 0f ? a + 360f : a; }
    private static float Delta(float from, float to)
    {
        float d = to - from;
        while (d > 180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    private static bool TryBearing(Vector3 target, Vector3 origin, float heading,
        float maxSq, float extHalf, out float dist, out float delta)
    {
        float dx = target.X - origin.X, dy = target.Y - origin.Y, dz = target.Z - origin.Z;
        float dsq = dx*dx + dy*dy + dz*dz;
        dist = 0f; delta = 0f;
        if (dsq > maxSq || dsq < 0.25f) return false;
        float bearing = Normalize(MathF.Atan2(dx, -dz) * (180f / MathF.PI));
        delta = Delta(heading, bearing);
        if (MathF.Abs(delta) > extHalf) return false;
        dist = MathF.Sqrt(dsq);
        return true;
    }

    private static Vector2 V(float x, float y) => new(x, y);
    private static uint C(Vector4 v) => ImGui.ColorConvertFloat4ToU32(v);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float LensEdgeAlpha(float d, float linearHalf, float extHalf)
    {
        float absD = MathF.Abs(d);
        if (absD <= linearHalf) return 1f;
        return 1f - SmoothStep(MathF.Min(1f, (absD - linearHalf) / (extHalf - linearHalf)));
    }

    private static uint WithAlpha(uint col, float mul)
    {
        uint a = (uint)(((col >> 24) & 0xFFu) * Math.Clamp(mul, 0f, 1f));
        return (col & 0x00FFFFFFu) | (a << 24);
    }

    private static void PushUnclip(ImDrawListPtr dl) =>
        dl.PushClipRect(Vector2.Zero, ImGui.GetIO().DisplaySize, false);
    private static void PopUnclip(ImDrawListPtr dl) => dl.PopClipRect();

    public void DumpNearbyObjects(float radius = 50f)
    {
        var player = _ot.LocalPlayer;
        if (player == null) { _log.Info("[SkyrimCompass debug] No local player."); return; }
        var pp = player.Position;
        var nearby = new List<(float dist, IGameObject obj)>();
        foreach (var obj in _ot)
        {
            if (obj == null || obj.EntityId == player.EntityId) continue;
            float d = Vector3.Distance(obj.Position, pp);
            if (d <= radius) nearby.Add((d, obj));
        }
        nearby.Sort((a,b) => a.dist.CompareTo(b.dist));
        _log.Info($"[SkyrimCompass debug] {nearby.Count} objects within {radius}y:");
        foreach (var (dist, obj) in nearby)
        {
            string extra = "";
            if (obj.ObjectKind == ObjectKind.EventNpc && _npcSheet.GetRowOrDefault(obj.BaseId) is { } row)
                extra = $" | Singular=\"{row.Singular}\" | Plural=\"{row.Plural}\"";
            _log.Info($"[SkyrimCompass debug] {dist,6:F1}y | Kind={obj.ObjectKind,-19} | BaseId={obj.BaseId,-8} | Name=\"{obj.Name}\"{extra}");
        }
    }
}