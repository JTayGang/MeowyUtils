using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Gui.NamePlate;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using static SkyrimCompass.Gfx;

namespace SkyrimCompass;

// Everything drawn on the compass strip: markers, their icons, colors and classification.
public sealed partial class CompassHud
{
    private const float IconSizeMul = 1.5f;
    private const float AetheryteIconSizeMul = 1.75f;
    private const string ShardNameFallback = "Aethernet";

    private enum AetheryteKind { None, Big, Shard }

    private static readonly string[] MenderKw = { "Mender", "Tinker", "Repairman" };
    private static readonly string[] ShopKw = { "Merchant", "Vendor", "Trader", "Sutler", "Supplier", "Junkmonger",
        "Fishmonger", "Dyemonger", "Jeweler", "Apothecary", "Culinarian", "Salvager", "Exchange", "Clothier",
        "Outfitter", "Peddler", "Dealer", "Armorer", "Shopkeep", "Stallkeeper", "Pawnbroker", "Provisioner",
        "Broker", "Proprietor", "Proprietress", "Marketeer", "Weaponsmith", "Tailor", "Herbalist", "Craftsman", "Appraiser" };
    private static readonly string[] SkipperKw = { "Skipper", "Ferryman" };
    private static readonly string[] TicketerKw = { "Ticketer", "Pilot", "Crewman", "Steward" };
    private static readonly string[] ChocoboKeepKw = { "Chocobokeep", "Falcon Porter" };

    private readonly List<(IGameObject? Obj, IFate? Fate, float Dist, float Delta, float T, uint Col, AetheryteKind Kind)> _candidates = new();
    private static readonly Comparison<(IGameObject? Obj, IFate? Fate, float Dist, float Delta, float T, uint Col, AetheryteKind Kind)> _cmpDistFar = (a, b) => b.Dist.CompareTo(a.Dist);

    private readonly Dictionary<ulong, int> _npcMarkers = new();
    private readonly Dictionary<uint, int> _gathIconCache = new();
    private readonly Dictionary<uint, NpcCategory> _npcCatCache = new();
    private readonly Dictionary<uint, string> _titleCache = new();
    private readonly Dictionary<uint, string> _singularCache = new();
    private readonly Dictionary<uint, uint> _roleColorCache = new();
    private readonly Dictionary<uint, bool> _shardRowCache = new();

    private Dictionary<string, PlayerIconOverride>? _overrideDict;
    private int _overrideDictVer = -1;

    private readonly ExcelSheet<GatheringPoint> _gathPtSheet;
    private readonly ExcelSheet<GatheringPointBase> _gathPtBaseSheet;
    private readonly ExcelSheet<GatheringType> _gathTypeSheet;
    private readonly ExcelSheet<ENpcResident> _npcSheet;
    private readonly ExcelSheet<ClassJob> _classJobSheet;
    private readonly ExcelSheet<Aetheryte> _aetheryteSheet;

    private void OnNamePlateUpdate(INamePlateUpdateContext ctx, IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        _npcMarkers.Clear();
        foreach (var h in handlers)
            if (h.MarkerIconId > 0)
                _npcMarkers[h.GameObjectId] = h.MarkerIconId;
    }


    private void RenderMarkers(ImDrawListPtr dl,float cx,float cy,float halfVis,float halfW,
        float lens,float heading,IPlayerCharacter player,Vector3 origin,bool inDuty)
    {
        float maxDist=_cfg.MaxMarkerDistance, maxDistSq=maxDist*maxDist;
        float fateMax=maxDist*_cfg.FateDistanceMultiplier, fateMaxSq=fateMax*fateMax;
        float ext=halfVis*lens;
        bool showRole=_cfg.ShowPartyRoleIcons && (!_cfg.PartyRoleIconsOnlyInDuty || inDuty);
        _candidates.Clear();

        if (_cfg.ShowAnyMarkers)
        {
            foreach (var obj in _ot)
            {
                if (obj==null || obj.EntityId==player.EntityId) continue;
                if (!TryBearing(obj.Position,origin,heading,maxDistSq,ext,out float dist,out float delta)) continue;
                uint col=MarkerColor(obj,player,out var kind);
                if (col==0) continue;
                _candidates.Add((obj,null,dist,delta,1f-dist/maxDist,col,kind));
            }
        }

        if (_cfg.ShowFates)
        {
            foreach (var fate in _ft)
            {
                if (fate==null || (fate.State!=FateState.Running && fate.State!=FateState.Preparing)) continue;
                if (!TryBearing(fate.Position,origin,heading,fateMaxSq,ext,out float dist,out float delta)) continue;
                _candidates.Add((null,fate,dist,delta,1f-dist/fateMax,0u,AetheryteKind.None));
            }
        }

        if (_candidates.Count==0) return;
        _candidates.Sort(_cmpDistFar);

        foreach (var cand in _candidates)
        {
            float delta=cand.Delta, t=cand.T;
            float sx=cx + Project(delta,halfVis,halfW,lens);
            float alpha=ComputeFade(t) * LensEdgeAlpha(delta,halfVis,ext);

            if (cand.Fate is { } fate)
            {
                float size=Lerp(_cfg.FateIconMinSize,_cfg.FateIconMaxSize,t);
                if (!(fate.IconId>0 && TryDrawIcon(dl,(int)fate.IconId,sx,cy,size,alpha,false,1f,true)))
                    DrawDotFilled(dl,sx,cy,(3f+7f*t)*2f,C(_cfg.FateColor),alpha);
                continue;
            }

            var obj=cand.Obj!;
            uint col=cand.Col;
            int iconId=0; float iconSize=0f;
            bool isAetheryte=cand.Kind!=AetheryteKind.None;

            if (_cfg.ShowAetheryteIcons && isAetheryte)
            {
                iconId=GetAetheryteIconId(cand.Kind);
                iconSize=Lerp(_cfg.AetheryteIconMinSize,_cfg.AetheryteIconMaxSize,t)*AetheryteIconSizeMul;
            }
            else if (obj.ObjectKind==ObjectKind.EventNpc && TryGetNpcIcon(obj,out int npcIcon))
            {
                iconId=npcIcon;
                iconSize=Lerp(_cfg.NpcQuestIconMinSize,_cfg.NpcQuestIconMaxSize,t)*IconSizeMul;
            }
            else if (_cfg.ShowGatheringIcons && obj.ObjectKind==ObjectKind.GatheringPoint)
            {
                int gIcon=GetGatheringIcon(obj.BaseId);
                if (gIcon>0) { iconId=gIcon; iconSize=Lerp(_cfg.GatheringIconMinSize,_cfg.GatheringIconMaxSize,t); }
            }
            else if (_cfg.ShowTreasureIcons && obj.ObjectKind==ObjectKind.Treasure)
            {
                iconId=_cfg.TreasureIconId;
                iconSize=Lerp(_cfg.TreasureMinSize,_cfg.TreasureMaxSize,t);
            }

            bool drew=iconId>0 && TryDrawIcon(dl,iconId,sx,cy,iconSize,alpha,false,1f,true);

            if (!drew)
            {
                if (obj.ObjectKind==ObjectKind.Pc)
                {
                    float pSize=Lerp(_cfg.PartyRoleIconMinSize,_cfg.PartyRoleIconMaxSize,t);
                    bool drewJob=false;
                    if (showRole && obj is ICharacter ch && (ch.StatusFlags & StatusFlags.PartyMember)!=0)
                    {
                        int jobIcon=ch.ClassJob.RowId>0 ? (int)(62000+ch.ClassJob.RowId) : 0;
                        if (jobIcon>0)
                        {
                            float drawSize=pSize*IconSizeMul;
                            uint roleCol=GetRoleColor(ch);
                            DrawRingShadow(dl,sx,cy,drawSize*0.5f,roleCol,roleCol,alpha);
                            TryDrawIcon(dl,jobIcon,sx,cy,drawSize,alpha,false,1f,true);
                            drewJob=true;
                        }
                    }
                    if (!drewJob)
                    {
                        var ov=FindOverride(obj.Name.TextValue);
                        if (ov!=null)
                        {
                            float ovSize=pSize*IconSizeMul;
                            float half=ovSize*0.5f;
                            DrawRingShadow(dl,sx,cy,half,
                                ov.ShowBorder ? C(ov.BorderColor) : null,
                                ov.ShowFill ? C(ov.FillColor) : null, alpha);
                            if (!(ov.IconBaseId>0 && TryDrawIcon(dl,ov.IconBaseId,sx,cy,ovSize,alpha,ov.ClipToCircle,ov.SizeMultiplier,true)))
                                DrawDotFilled(dl,sx,cy,pSize, ov.ShowBorder ? C(ov.BorderColor) : col, alpha);
                        }
                        else
                        {
                            bool isFriend=_cfg.SolidFriendDots && obj is ICharacter ch2 && (ch2.StatusFlags & StatusFlags.Friend)!=0;
                            if (isFriend) DrawDotFilled(dl,sx,cy,pSize,col,alpha);
                            else DrawDotHollow(dl,sx,cy,pSize,col,alpha);
                        }
                    }
                }
                else
                {
                    (float min,float max,bool filled) dot = isAetheryte ? (_cfg.AetheryteIconMinSize,_cfg.AetheryteIconMaxSize,true)
                        : obj.ObjectKind==ObjectKind.EventNpc ? (_cfg.NpcQuestIconMinSize,_cfg.NpcQuestIconMaxSize,false)
                        : obj.ObjectKind==ObjectKind.BattleNpc ? (_cfg.EnemyMinSize,_cfg.EnemyMaxSize,true)
                        : obj.ObjectKind==ObjectKind.Treasure ? (_cfg.TreasureMinSize,_cfg.TreasureMaxSize,true)
                        : (6f,20f,true);
                    float ds=Lerp(dot.min,dot.max,t);
                    if (dot.filled) DrawDotFilled(dl,sx,cy,ds,col,alpha);
                    else DrawDotHollow(dl,sx,cy,ds,col,alpha);
                }
            }
        }
    }

    private float ComputeFade(float t)
    {
        float near=_cfg.DotNearZone, far=_cfg.DotFarZone, mid=_cfg.DotMidAlpha;
        if (t>=near) return 1f;
        if (t>=far) return mid + (1f-mid)*SmoothStep((t-far)/(near-far));
        return mid * SmoothStep(t/far);
    }

    private int GetGatheringIcon(uint baseId)
    {
        if (_gathIconCache.TryGetValue(baseId,out int cached)) return cached;
        int icon=0;
        if (_gathPtSheet.GetRowOrDefault(baseId) is { } gp &&
            _gathPtBaseSheet.GetRowOrDefault(gp.GatheringPointBase.RowId) is { } gpb &&
            _gathTypeSheet.GetRowOrDefault(gpb.GatheringType.RowId) is { } gt)
            icon=gt.IconMain;
        _gathIconCache[baseId]=icon;
        return icon;
    }

    private PlayerIconOverride? FindOverride(string name)
    {
        if (_overrideDict==null || _overrideDictVer != _cfg.PlayerIconOverridesVersion)
        {
            _overrideDict=new Dictionary<string,PlayerIconOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _cfg.PlayerIconOverrides)
                if (!string.IsNullOrEmpty(e.PlayerName))
                    _overrideDict[e.PlayerName]=e;
            _overrideDictVer=_cfg.PlayerIconOverridesVersion;
        }
        _overrideDict.TryGetValue(name,out var found);
        return found;
    }

    private uint GetRoleColor(ICharacter ch)
    {
        uint rowId=ch.ClassJob.RowId;
        if (_roleColorCache.TryGetValue(rowId,out uint packed)) return packed;
        uint col=0u;
        if (_classJobSheet.GetRowOrDefault(rowId) is { } row)
            col = row.Role switch
            {
                1 => C(new Vector4(0.36f,0.48f,0.76f,0.90f)),
                2 or 3 => C(new Vector4(0.84f,0.30f,0.30f,0.90f)),
                4 => C(new Vector4(0.30f,0.69f,0.49f,0.90f)),
                _ => C(new Vector4(0.54f,0.54f,0.54f,0.85f)),
            };
        else col=C(new Vector4(0.54f,0.54f,0.54f,0.85f));
        _roleColorCache[rowId]=col;
        return col;
    }

    private string GetTitle(uint baseId)
    {
        if (_titleCache.TryGetValue(baseId,out var cached)) return cached;
        string v=_npcSheet.GetRowOrDefault(baseId) is { } row ? row.Title.ToString() : "";
        _titleCache[baseId]=v;
        return v;
    }

    private string GetSingular(uint baseId)
    {
        if (_singularCache.TryGetValue(baseId,out var cached)) return cached;
        string v=_npcSheet.GetRowOrDefault(baseId) is { } row ? row.Singular.ToString() : "";
        _singularCache[baseId]=v;
        return v;
    }

    private static bool HasKw(string text,string[] kw)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var k in kw) if (text.Contains(k,StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private bool MatchesKw(uint baseId,string[] kw) => HasKw(GetTitle(baseId),kw) || HasKw(GetSingular(baseId),kw);

    private enum NpcCategory { None, Mender, Shop, Skipper, Ticketer, ChocoboKeep }

    private NpcCategory ClassifyNpc(uint baseId)
    {
        if (_npcCatCache.TryGetValue(baseId,out var cached)) return cached;
        var cat = MatchesKw(baseId,MenderKw) ? NpcCategory.Mender
            : MatchesKw(baseId,ShopKw) ? NpcCategory.Shop
            : MatchesKw(baseId,SkipperKw) ? NpcCategory.Skipper
            : MatchesKw(baseId,TicketerKw) ? NpcCategory.Ticketer
            : MatchesKw(baseId,ChocoboKeepKw) ? NpcCategory.ChocoboKeep
            : NpcCategory.None;
        _npcCatCache[baseId]=cat;
        return cat;
    }

    private bool TryGetNpcIcon(IGameObject obj,out int iconId)
    {
        if (_cfg.ShowNpcQuestIcons && _npcMarkers.TryGetValue(obj.GameObjectId,out iconId)) return true;
        switch (ClassifyNpc(obj.BaseId))
        {
            case NpcCategory.Mender when _cfg.ShowShopIcons: iconId=_cfg.MenderIconId; return true;
            case NpcCategory.Shop when _cfg.ShowShopIcons: iconId=_cfg.ShopIconId; return true;
            case NpcCategory.Skipper when _cfg.ShowFastTravelIcons: iconId=_cfg.FastTravelIconId; return true;
            case NpcCategory.Ticketer when _cfg.ShowFastTravelIcons: iconId=_cfg.FastTravelTicketerIconId; return true;
            case NpcCategory.ChocoboKeep when _cfg.ShowFastTravelIcons: iconId=_cfg.ChocoboKeepIconId; return true;
            default: iconId=0; return false;
        }
    }

    // Shards are found by sheet row (works in any client language), with the English name as a fallback.
    private AetheryteKind ClassifyAetheryte(IGameObject obj)
    {
        bool isAetheryteObj = obj.ObjectKind == ObjectKind.Aetheryte;
        bool shard = (isAetheryteObj && IsShardRow(obj.BaseId))
            || obj.Name.TextValue.Contains(ShardNameFallback, StringComparison.OrdinalIgnoreCase);
        if (isAetheryteObj) return shard ? AetheryteKind.Shard : AetheryteKind.Big;
        return shard ? AetheryteKind.Shard : AetheryteKind.None;
    }

    private bool IsShardRow(uint baseId)
    {
        if (_shardRowCache.TryGetValue(baseId, out bool isShard)) return isShard;
        isShard = _aetheryteSheet.GetRowOrDefault(baseId) is { IsAetheryte: false };
        _shardRowCache[baseId] = isShard;
        return isShard;
    }

    private int GetAetheryteIconId(AetheryteKind kind) =>
        kind == AetheryteKind.Shard ? _cfg.AethernetShardIconId : _cfg.AetheryteIconId;


    private uint MarkerColor(IGameObject obj,IPlayerCharacter player,out AetheryteKind kind)
    {
        kind=AetheryteKind.None;
        switch (obj.ObjectKind)
        {
            case ObjectKind.Pc: return _cfg.ShowPlayers ? MarkerBaseColor(obj) : 0u;
            case ObjectKind.BattleNpc:
                if (!_cfg.ShowEnemies) return 0u;
                if (obj is not IBattleNpc bnpc || bnpc.BattleNpcKind!=BattleNpcSubKind.Combatant) return 0u;
                if (_cfg.EnemiesOnlyIfEngaged && !(bnpc.StatusFlags.HasFlag(StatusFlags.InCombat) && player.StatusFlags.HasFlag(StatusFlags.InCombat)))
                    return 0u;
                return MarkerBaseColor(obj);
            case ObjectKind.EventNpc:
                if (TryAetheryteColor(obj,out uint col,out kind)) return col;
                if (!_cfg.ShowNpcs) return 0u;
                if (_cfg.NpcsOnlyIfTargetable && !obj.IsTargetable) return 0u;
                return MarkerBaseColor(obj);
            case ObjectKind.EventObj:
                if (TryAetheryteColor(obj,out col,out kind)) return col;
                return 0u;
            case ObjectKind.GatheringPoint:
                if (!_cfg.ShowGatheringNodes) return 0u;
                if (_cfg.GatheringOnlyIfTargetable && !obj.IsTargetable) return 0u;
                return MarkerBaseColor(obj);
            case ObjectKind.Treasure:
                return _cfg.ShowTreasure ? MarkerBaseColor(obj) : 0u;
            case ObjectKind.Aetheryte:
                TryAetheryteColor(obj,out uint aCol,out kind);
                return aCol;
            default: return 0u;
        }
    }

    private bool TryAetheryteColor(IGameObject obj,out uint col,out AetheryteKind kind)
    {
        kind=ClassifyAetheryte(obj);
        if (kind==AetheryteKind.None) { col=0u; return false; }
        bool hidden=!_cfg.ShowAetherytes || (kind==AetheryteKind.Shard && !_cfg.ShowAethernetShards);
        col=hidden ? 0u : C(_cfg.AetheryteColor);
        return true;
    }

    private uint MarkerBaseColor(IGameObject obj) => obj.ObjectKind switch
    {
        ObjectKind.Pc => C(_cfg.PlayerColor),
        ObjectKind.BattleNpc when obj is IBattleNpc b && b.BattleNpcKind==BattleNpcSubKind.Combatant => C(_cfg.EnemyColor),
        ObjectKind.EventNpc => C(_cfg.NpcColor),
        ObjectKind.GatheringPoint => C(_cfg.GatheringColor),
        ObjectKind.Treasure => C(_cfg.TreasureColor),
        _ => 0u,
    };

    private static void DrawDotFilled(ImDrawListPtr dl,float sx,float cy,float size,uint col,float alpha) =>
        DrawDot(dl,sx,cy,size,col,alpha,true);
    private static void DrawDotHollow(ImDrawListPtr dl,float sx,float cy,float size,uint col,float alpha) =>
        DrawDot(dl,sx,cy,size,col,alpha,false);

    private static void DrawDot(ImDrawListPtr dl,float sx,float cy,float size,uint col,float alpha,bool filled)
    {
        float r=size*0.5f;
        if (filled) dl.AddCircleFilled(V(sx,cy), r, WithAlpha(col,alpha));
        else dl.AddCircle(V(sx,cy), r, WithAlpha(col,alpha), 0, 2f);
        dl.AddCircle(V(sx,cy), r+0.8f, WithAlpha(filled ? 0x66000000u : 0x33000000u, alpha));
    }

    private static void DrawRingShadow(ImDrawListPtr dl,float sx,float cy,float half,
        uint? ring,uint? shadow,float alpha)
    {
        if (ring==null && shadow==null) return;
        PushUnclip(dl);
        if (shadow is { } s) { dl.AddCircleFilled(V(sx,cy), half*0.85f, WithAlpha(s,alpha*0.6f));
            dl.AddCircleFilled(V(sx,cy), half*0.65f, WithAlpha(s,alpha*0.4f));
            dl.AddCircleFilled(V(sx,cy), half*0.45f, WithAlpha(s,alpha*0.2f)); }
        if (ring is { } r) dl.AddCircle(V(sx,cy), half+1f, WithAlpha(r,alpha), 0, 3f);
        PopUnclip(dl);
    }

    public void DumpNearbyObjects(float radius=50f)
    {
        var player=_ot.LocalPlayer;
        if (player==null) { _log.Info("[SkyrimCompass debug] No local player."); return; }
        var pp=player.Position;
        var nearby=new List<(float dist, IGameObject obj)>();
        foreach (var obj in _ot)
        {
            if (obj==null || obj.EntityId==player.EntityId) continue;
            float d=Vector3.Distance(obj.Position, pp);
            if (d<=radius) nearby.Add((d,obj));
        }
        nearby.Sort((a,b)=>a.dist.CompareTo(b.dist));
        _log.Info($"[SkyrimCompass debug] {nearby.Count} objects within {radius}y:");
        foreach (var (dist,obj) in nearby)
        {
            string extra="";
            if (obj.ObjectKind==ObjectKind.EventNpc && _npcSheet.GetRowOrDefault(obj.BaseId) is { } row)
                extra=$" | Singular=\"{row.Singular}\" | Plural=\"{row.Plural}\"";
            _log.Info($"[SkyrimCompass debug] {dist,6:F1}y | Kind={obj.ObjectKind,-19} | BaseId={obj.BaseId,-8} | Name=\"{obj.Name}\"{extra}");
        }
    }
}
