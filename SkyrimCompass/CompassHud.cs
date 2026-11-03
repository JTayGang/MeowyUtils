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

using static SkyrimCompass.Gfx;

using MoodlesStatusInfo = (int Version, System.Guid GUID, int IconID, string Title, string Description, string CustomVFXPath,
    long ExpireTicks, int Type, int Stacks, int StackSteps, uint Modifiers, System.Guid ChainedStatus,
    int ChainTrigger, string Applier, string Dispeller);
using LociStatusInfo = (int Version, System.Guid GUID, uint IconID, string Title, string Description, string CustomVFXPath,
    long ExpireTicks, byte Type, int Stacks, int StackSteps, int StackToChain, uint Modifiers,
    System.Guid ChainedGUID, byte ChainType, int ChainTrigger, string Applier, string Dispeller);

namespace SkyrimCompass;

public sealed partial class CompassHud : IDisposable
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
    private readonly DynamicGameFontCache _fonts;
    private readonly IDalamudPluginInterface _pi;
    private const float BaseFontPx = 18f; // FontScale/TargetBarFontScale of 1.0 maps to this many px

    private readonly ICallGateSubscriber<nint, List<MoodlesStatusInfo>> _moodlesGet;
    private readonly ICallGateSubscriber<nint, List<LociStatusInfo>> _lociGet;
    private readonly ICallGateSubscriber<int> _moodlesVer;
    private readonly ICallGateSubscriber<(int, int)> _lociVer;
    private readonly ICallGateSubscriber<nint, string?> _moodlesGetStr;       // Moodles.GetStatusManagerByPtrV2 -> base64 (null if not rendered)
    private readonly ICallGateSubscriber<nint, (int, string?)> _lociGetStr;   // Loci.GetManagerByPtr -> (LociApiEc, base64); ec 0 == Success

    private PluginIpcState _moodlesIpc = new("Moodles", 4, PluginIpcKind.Moodles);
    private PluginIpcState _lociIpc = new("Loci", 0, PluginIpcKind.Loci);

    private readonly List<(float Remaining, int Icon, string Name, string Desc, System.Guid Guid, int Stacks, int MaxStacks, bool Dispellable)> _statusBuf = new();
    // Absolute ExpiresAt per status GUID for the current target (see StatusExpiry.cs). The tuple IPC's
    // "ExpireTicks" is only the status's total configured duration, so it can't tell us time remaining.
    private readonly ExpiryFeed _moodlesExpiry;
    private readonly ExpiryFeed _lociExpiry;

    private List<MoodlesStatusInfo>? _cachedMoodles;
    private nint _cachedMoodlesTarget = IntPtr.Zero;
    private float _cachedMoodlesAt = -1000f;
    private List<LociStatusInfo>? _cachedLoci;
    private nint _cachedLociTarget = IntPtr.Zero;
    private float _cachedLociAt = -1000f;
    private const float StatusCacheSecs = 0.24f;
    // Bit 0 of the IPC "Modifiers" field == the author-set "dispellable" flag on both plugins:
    // Moodles.Data.Enums.Modifiers.CanDispel and LociApi.Enums.Modifiers.CanDispel are both
    // `1u << 0` as of the current Moodles/Loci.Api sources. Re-verify against both projects if
    // either bumps its IPC version, since this isn't a documented/stable part of either contract.
    private const uint PluginStatusCanDispelBit = 1u << 0;

    private readonly Dictionary<string, byte[]> _tooltipCache = new();
    private readonly Dictionary<int, string> _durationCache = new();
    private readonly Dictionary<int, (string Name, string Desc)> _iconToStatusText = new(); // for stacked vanilla statuses
    private readonly Dictionary<uint, (string Name, string Desc)> _statusTextCache = new();
    private readonly Dictionary<int, float> _iconAspectCache = new();
    private const int MaxTooltipCache = 200;

    private enum PluginIpcKind { Moodles, Loci }
    private class PluginIpcState { public string Name; public int MinVer; public PluginIpcKind Kind; public bool Active; public float ActiveCheckedAt = -1000f; public bool VerOk; public float VerCheckedAt = -1000f; public PluginIpcState(string n, int mv, PluginIpcKind k) { Name=n; MinVer=mv; Kind=k; } }
    private const float PluginActiveCacheSecs = 5f;

    public CompassHud(IClientState cs, IObjectTable ot, ITargetManager tm, INamePlateGui npg,
        ITextureProvider tp, IFateTable ft, ICondition cond, IGameGui gg, IDataManager dm,
        Configuration cfg, IPluginLog log, DynamicGameFontCache fonts, IDalamudPluginInterface pi)
    {
        _cs=cs; _ot=ot; _tm=tm; _npg=npg; _tp=tp; _ft=ft; _cond=cond; _gg=gg; _cfg=cfg; _log=log; _fonts=fonts; _pi=pi;
        _moodlesGet = pi.GetIpcSubscriber<nint, List<MoodlesStatusInfo>>("Moodles.GetStatusManagerInfoByPtrV2");
        _lociGet = pi.GetIpcSubscriber<nint, List<LociStatusInfo>>("Loci.GetManagerInfoByPtr");
        _moodlesVer = pi.GetIpcSubscriber<int>("Moodles.Version");
        _lociVer = pi.GetIpcSubscriber<(int,int)>("Loci.ApiVersion");
        _moodlesGetStr = pi.GetIpcSubscriber<nint, string?>("Moodles.GetStatusManagerByPtrV2");
        _lociGetStr = pi.GetIpcSubscriber<nint, (int, string?)>("Loci.GetManagerByPtr");
        _moodlesExpiry = new ExpiryFeed("Moodles", a => _moodlesGetStr.InvokeFunc(a), StatusExpiryCodec.TryDecodeMoodles, m => _log.Warning(m));
        _lociExpiry = new ExpiryFeed("Loci", a => { var (ec, str) = _lociGetStr.InvokeFunc(a); return ec == 0 ? str : null; }, StatusExpiryCodec.TryDecodeLoci, m => _log.Warning(m));
        _gathPtSheet = dm.GetExcelSheet<GatheringPoint>();
        _gathPtBaseSheet = dm.GetExcelSheet<GatheringPointBase>();
        _gathTypeSheet = dm.GetExcelSheet<GatheringType>();
        _npcSheet = dm.GetExcelSheet<ENpcResident>(ClientLanguage.English);
        _classJobSheet = dm.GetExcelSheet<ClassJob>();
        _actionSheet = dm.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        _aetheryteSheet = dm.GetExcelSheet<Aetheryte>();
        _npg.OnDataUpdate += OnNamePlateUpdate;
        var statusSheet = dm.GetExcelSheet<Lumina.Excel.Sheets.Status>();
        if (statusSheet != null)
            foreach (var row in statusSheet)
            {
                int icon = (int)row.Icon;
                if (icon>0 && !_iconToStatusText.ContainsKey(icon))
                {
                    string name = row.Name.ToString();
                    if (!string.IsNullOrEmpty(name))
                        _iconToStatusText[icon] = (name, row.Description.ToString());
                }
            }
    }

    public void Dispose() => _npg.OnDataUpdate -= OnNamePlateUpdate;

    public void Draw()
    {
        if (!_cfg.Enabled) return;
        if (_cfg.HideDuringCutscenes && (_cond[ConditionFlag.OccupiedInCutSceneEvent] ||
            _cond[ConditionFlag.WatchingCutscene] || _cond[ConditionFlag.WatchingCutscene78]))
            return;
        var player = _ot.LocalPlayer;
        if (player == null || !TryGetView(player, out float heading, out Vector3 origin)) return;

        var io = ImGui.GetIO();
        var dl = ImGui.GetForegroundDrawList();
        float now = (float)ImGui.GetTime();
        UpdateCompassDrag();

        float bw = _cfg.CompassWidth, bh = _cfg.CompassHeight;
        float bx = (io.DisplaySize.X - bw) * 0.5f + _cfg.XOffset;
        float by = _cfg.YOffset;
        bool inDuty = IsInDutyOrPvp();

        if (_cfg.ShowCompassBar)
        {
            RenderBar(dl, bx, by, bw, bh, heading, player, origin, now, inDuty);
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

    // Compass heading (degrees) and the point distances are measured from.
    private unsafe bool TryGetView(IPlayerCharacter player, out float heading, out Vector3 origin)
    {
        float hRad = 0f;
        origin = player.Position;
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
                        origin = cp;
                }
                gotH = true;
            }
        }
        if (!gotH)
        {
            heading = 0f;
            if (float.IsNaN(player.Rotation)) return false;
            hRad = MathF.PI - player.Rotation;
        }
        heading = Normalize(hRad * (180f / MathF.PI) + _cfg.RotationOffset);
        return true;
    }

    private bool IsInDutyOrPvp() => _cond[ConditionFlag.BoundByDuty] || _cond[ConditionFlag.BoundByDuty56] ||
        _cond[ConditionFlag.BoundByDuty95] || _cond[ConditionFlag.InDeepDungeon] || _cs.IsPvP;

    private void RenderStatuses(ImDrawListPtr dl, IBattleChara ch, float cx, float y, float alpha)
    {
        float size=MathF.Max(8f,_cfg.TargetStatusIconSize);
        float hGap=size*0.25f;
        int max=Math.Max(1,_cfg.TargetStatusMaxIcons);
        float now=(float)ImGui.GetTime();
        // Esuna can't target enemies, whatever a status's dispel flag says.
        bool targetIsEnemy = ch.ObjectKind==ObjectKind.BattleNpc && ch is IBattleNpc bnpc
            && bnpc.BattleNpcKind==BattleNpcSubKind.Combatant;
        _statusBuf.Clear();

        // vanilla statuses
        foreach (var st in ch.StatusList)
        {
            if (_statusBuf.Count>=max) break;
            if (st.StatusId==0) continue;
            if (st.GameData.ValueNullable is not { } row || row.Icon==0) continue;
            float rem=(row.IsPermanent || row.IsFcBuff) ? 0f : st.RemainingTime;
            var (name,desc)=GetStatusText(row);
            _statusBuf.Add((rem, (int)row.Icon, name, desc, System.Guid.Empty, (int)st.Param, (int)row.MaxStacks, row.CanDispel));
        }

        // Moodles / Loci
        if (ch.Address != IntPtr.Zero)
        {
            if (_statusBuf.Count<max && IsPluginActive(_moodlesIpc,now) && IsVerOk(_moodlesIpc,now))
                AppendPluginStatuses(ch.Address,max,now,_moodlesGet,ref _cachedMoodles,
                    ref _cachedMoodlesTarget,ref _cachedMoodlesAt,_moodlesExpiry,s=>s.GUID,s=>s.IconID,
                    s=>(s.Title,s.Description),s=>s.ExpireTicks,s=>s.Stacks,
                    s=>(s.Modifiers & PluginStatusCanDispelBit)!=0);
            if (_statusBuf.Count<max && IsPluginActive(_lociIpc,now) && IsVerOk(_lociIpc,now))
                AppendPluginStatuses(ch.Address,max,now,_lociGet,ref _cachedLoci,
                    ref _cachedLociTarget,ref _cachedLociAt,_lociExpiry,s=>s.GUID,s=>(int)s.IconID,
                    s=>(s.Title,s.Description),s=>s.ExpireTicks,s=>s.Stacks,
                    s=>(s.Modifiers & PluginStatusCanDispelBit)!=0);
        }

        if (_statusBuf.Count==0) return;
        int n=_statusBuf.Count;
        float width=n*size+(n-1)*hGap;
        float maxWidth=max*size+(max-1)*hGap;
        float startX = _cfg.TargetStatusIconAlignLeft ? cx - maxWidth*0.5f
                     : _cfg.TargetStatusIconAlignRight ? cx + maxWidth*0.5f - width
                     : cx - width*0.5f;
        float topGap=size*0.15f;
        float halfH=size*0.5f*GetIconAspect(_statusBuf[0].Icon);
        float scy=y+topGap+halfH;
        float fSize=MathF.Max(9f,size*0.8f);
        var font=ImGui.GetFont();
        float textGap=-size*0.12f, tipGap=MathF.Max(4f,size*0.15f);
        TryStartCompassDrag(V(startX,y), V(startX+width, scy+halfH+fSize+tipGap));

        for (int i=0; i<n; i++)
        {
            var (rem, icon, name, desc, guid, stacks, maxStacks, dispellable)=_statusBuf[i];
            float sx=startX + i*(size+hGap) + size*0.5f;
            int displayIcon=icon; string displayName=name, displayDesc=desc;
            if (stacks>1)
            {
                if (guid != System.Guid.Empty)
                    displayIcon = icon + stacks - 1;
                else if (maxStacks>1)
                {
                    int candidate=icon + Math.Min(stacks, maxStacks) - 1;
                    if (_iconToStatusText.TryGetValue(candidate, out var data))
                    {
                        if (string.Equals(data.Name, name, StringComparison.OrdinalIgnoreCase))
                        { displayIcon=candidate; displayName=data.Name; displayDesc=data.Desc; }
                    }
                    else displayIcon=candidate;
                }
            }
            if (!TryDrawIcon(dl,displayIcon,sx,scy,size,alpha,false,1f,false) && displayIcon!=icon)
                TryDrawIcon(dl,icon,sx,scy,size,alpha,false,1f,false);
            if (dispellable && _cfg.ShowTargetStatusEsunaMarker && !targetIsEnemy)
                DrawEsunaMarker(dl,sx,scy,halfH,size,alpha);

            string? durLabel=GetDurationLabel(rem);
            float hoverBottom=scy+halfH;
            if (durLabel != null)
            {
                Vector2 lsz=ImGui.CalcTextSize(durLabel) * (fSize / ImGui.GetFontSize());
                float lx=sx-lsz.X*0.5f, ly=scy+halfH+textGap;
                dl.AddText(font,fSize,V(lx+1f,ly+1f), WithAlpha(0xCC000000u,alpha), durLabel);
                dl.AddText(font,fSize,V(lx,ly), WithAlpha(0xFFFFFFFFu,alpha), durLabel);
                hoverBottom=ly+lsz.Y;
            }
            if (ImGui.IsMouseHoveringRect(V(sx-size*0.5f,scy-halfH), V(sx+size*0.5f,hoverBottom), false))
            {
                ImGui.SetNextWindowPos(V(sx,hoverBottom+tipGap), ImGuiCond.Always, V(0.5f,0f));
                var tipBg=_cfg.BackgroundColor;
                ImGui.PushStyleColor(ImGuiCol.PopupBg, new Vector4(tipBg.X,tipBg.Y,tipBg.Z,tipBg.W*alpha));
                ImGui.BeginTooltip();
                ImGuiHelpers.SeStringWrapped(GetFormattedTooltip(displayName,displayDesc), new SeStringDrawParams{WrapWidth=345f});
                ImGui.EndTooltip();
                ImGui.PopStyleColor();
            }
        }
    }

    private void DrawEsunaMarker(ImDrawListPtr dl, float sx, float cy, float halfH, float size, float alpha)
    {
        // Thin bar across the icon top (vanilla convention) for statuses Esuna can remove.
        float barH=MathF.Max(2f,size*0.14f);
        float inset=3f;
        float y0=cy-halfH;
        dl.AddRectFilled(V(sx-size*0.5f+inset,y0), V(sx+size*0.5f-inset,y0+barH),
            WithAlpha(C(_cfg.TargetStatusEsunaMarkerColor),alpha), barH*0.35f);
    }

    private string? GetDurationLabel(float rem)
    {
        if (rem<=0f) return null;
        int tier,val;
        if (rem<60f) { tier=0; val=(int)MathF.Ceiling(rem); }
        else if (rem<3600f) { tier=1; val=(int)(rem/60f); }
        else if (rem<86400f) { tier=2; val=(int)(rem/3600f); }
        else if (rem<777600f) { tier=3; val=(int)(rem/86400f); }
        else return "9+d";
        int key=tier*100000+val;
        if (!_durationCache.TryGetValue(key,out var s))
            _durationCache[key]=s = tier switch {0=>$"{val}",1=>$"{val}m",2=>$"{val}h",_=>$"{val}d"};
        return s;
    }

    private bool IsPluginActive(PluginIpcState st, float now)
    {
        if (now - st.ActiveCheckedAt < PluginActiveCacheSecs) return st.Active;
        st.ActiveCheckedAt=now;
        st.Active=false;
        foreach (var p in _pi.InstalledPlugins)
            if (p.IsLoaded && p.InternalName==st.Name) { st.Active=true; break; }
        return st.Active;
    }

    private bool IsVerOk(PluginIpcState st, float now)
    {
        if (now - st.VerCheckedAt < PluginActiveCacheSecs) return st.VerOk;
        st.VerCheckedAt=now;
        bool was=st.VerOk;
        try
        {
            if (st.Kind==PluginIpcKind.Moodles)
                st.VerOk = _moodlesVer.InvokeFunc() >= st.MinVer;
            else { _lociVer.InvokeFunc(); st.VerOk=true; }
        }
        catch { st.VerOk=false; }
        if (was && !st.VerOk)
            _log.Warning($"[SkyrimCompass] {st.Name} IPC version no longer compatible – status icons disabled.");
        return st.VerOk;
    }

    private void AppendPluginStatuses<T>(nint addr,int max,float now,
        ICallGateSubscriber<nint,List<T>> getter,
        ref List<T>? cache, ref nint cacheTarget, ref float cacheAt,
        ExpiryFeed expiry,
        Func<T,System.Guid> guidSel, Func<T,int> iconSel,
        Func<T,(string Name,string Desc)> textSel,
        Func<T,long> expireSel, Func<T,int> stacksSel, Func<T,bool> dispellableSel)
    {
        if (cache==null || addr!=cacheTarget || now-cacheAt>=StatusCacheSecs)
        {
            cacheTarget=addr; cacheAt=now;
            try { cache=getter.InvokeFunc(addr); } catch { cache=null; }
            // Same tick, same target: pull the absolute ExpiresAt values (only worth the call if there's something to time).
            if (cache!=null && cache.Count>0) expiry.Refresh(addr); else expiry.Clear();
        }
        if (cache==null) return;
        foreach (var s in cache)
        {
            if (_statusBuf.Count>=max) break;
            var g=guidSel(s);
            int icon=iconSel(s);
            if (icon<=0) continue;
            if (GuidInBuffer(g)) continue;
            // Tuple ExpireTicks is the TOTAL duration (-1 == no expiry), not time left, so the timer comes from
            // ExpiresAt. Unknown (payload unavailable/undecodable) shows the icon with no timer rather than a wrong one.
            float rem=0f;
            if (expireSel(s)>=0)
            {
                var state=expiry.Get(g,out rem);
                if (state==ExpiryState.Expired) continue; // Moodles/Loci hide these on their own target bars too
                if (state!=ExpiryState.Active) rem=0f;
            }
            var (name,desc)=textSel(s);
            _statusBuf.Add((rem,icon,name,desc,g,stacksSel(s),0,dispellableSel(s)));
        }
    }

    private bool GuidInBuffer(System.Guid g)
    { for (int i=0;i<_statusBuf.Count;i++) if (_statusBuf[i].Guid==g) return true; return false; }

    private static readonly Dictionary<string,ushort> _namedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WhiteNormal"]=0,["White"]=1,["Grey1"]=2,["Grey2"]=3,["Grey3"]=4,["Grey4"]=5,
        ["Grey5"]=6,["Black"]=7,["LightYellow"]=8,["Red"]=17,["DarkRed"]=19,["Green"]=45,
        ["DarkGreen"]=47,["WarmSeaBlue"]=52,["Orange"]=500,["LightBlue"]=502,["Yellow"]=514,
        ["Gold"]=540,["DarkBlue"]=543,["LightGreen"]=551,["Pink"]=561,
    };
    private static readonly Regex _formatTagRegex = new(@"(\[color=[0-9a-zA-Z]+\])|(\[/color\])|(\[glow=[0-9a-zA-Z]+\])|(\[/glow\])|(\[i\])|(\[/i\])", RegexOptions.IgnoreCase|RegexOptions.Compiled);

    private static bool TryResolveColor(string val, out ushort key) => ushort.TryParse(val,out key) || _namedColors.TryGetValue(val,out key);

    private static void AppendFormat(SeStringBuilder sb,string raw)
    {
        bool col=false,glow=false,ital=false;
        int last=0;
        foreach (Match m in _formatTagRegex.Matches(raw))
        {
            if (m.Index>last) sb.AddText(raw[last..m.Index]);
            last=m.Index+m.Length;
            var tag=m.Value;
            if (tag.StartsWith("[color=",StringComparison.OrdinalIgnoreCase))
            { if (TryResolveColor(tag[7..^1],out var id)) { sb.AddUiForeground(id); col=true; } }
            else if (tag=="[/color]") { if (col) { sb.AddUiForegroundOff(); col=false; } }
            else if (tag.StartsWith("[glow=",StringComparison.OrdinalIgnoreCase))
            { if (TryResolveColor(tag[6..^1],out var id)) { sb.AddUiGlow(id); glow=true; } }
            else if (tag=="[/glow]") { if (glow) { sb.AddUiGlowOff(); glow=false; } }
            else if (tag=="[i]") { sb.AddItalicsOn(); ital=true; }
            else if (ital) { sb.AddItalicsOff(); ital=false; }
        }
        if (last<raw.Length) sb.AddText(raw[last..]);
        if (col) sb.AddUiForegroundOff();
        if (glow) sb.AddUiGlowOff();
        if (ital) sb.AddItalicsOff();
    }

    private byte[] GetFormattedTooltip(string name,string desc)
    {
        string key=$"{name}\0{desc}";
        if (_tooltipCache.TryGetValue(key,out var cached)) return cached;
        if (_tooltipCache.Count>=MaxTooltipCache) _tooltipCache.Clear();
        var sb=new SeStringBuilder();
        AppendFormat(sb,name);
        if (!string.IsNullOrWhiteSpace(desc)) { sb.AddText("\n"); AppendFormat(sb,desc); }
        var bytes=sb.Encode();
        _tooltipCache[key]=bytes;
        return bytes;
    }

    private bool TryDrawIcon(ImDrawListPtr dl,int iconId,float sx,float cy,float size,float alpha,
        bool clipCircle=false,float zoom=1f,bool unclip=true)
    {
        if (!_tp.TryGetFromGameIcon(new GameIconLookup((uint)iconId), out var shared)) return false;
        var tex=shared.GetWrapOrEmpty();
        uint tint=WithAlpha(0xFFFFFFFFu,alpha);
        float hw,hh; Vector2 uvMin,uvMax;
        if (clipCircle)
        {
            hw=hh=size*0.5f;
            float uvHalf=0.5f/Math.Max(0.01f,zoom);
            uvMin=new(0.5f-uvHalf,0.5f-uvHalf);
            uvMax=new(0.5f+uvHalf,0.5f+uvHalf);
        }
        else
        {
            hw=size*0.5f*Math.Max(0.01f,zoom);
            hh=hw * (tex.Size.X>0f ? tex.Size.Y/tex.Size.X : 1f);
            uvMin=Vector2.Zero; uvMax=Vector2.One;
        }
        if (unclip) PushUnclip(dl);
        dl.AddImageRounded(tex.Handle, V(sx-hw,cy-hh), V(sx+hw,cy+hh),
            uvMin,uvMax,tint, clipCircle ? hw : 0f, ImDrawFlags.RoundCornersAll);
        if (unclip) PopUnclip(dl);
        return true;
    }

    private float GetIconAspect(int iconId)
    {
        if (_iconAspectCache.TryGetValue(iconId,out float asp)) return asp;
        if (!_tp.TryGetFromGameIcon(new GameIconLookup((uint)iconId), out var shared)) return 1f;
        var size=shared.GetWrapOrEmpty().Size;
        asp=size.X>0f ? size.Y/size.X : 1f;
        _iconAspectCache[iconId]=asp;
        return asp;
    }

    private (string Name,string Desc) GetStatusText(Lumina.Excel.Sheets.Status row)
    {
        if (_statusTextCache.TryGetValue(row.RowId,out var cached)) return cached;
        var v=(row.Name.ToString(),row.Description.ToString());
        _statusTextCache[row.RowId]=v;
        return v;
    }
}