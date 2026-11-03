using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Interface.ManagedFontAtlas;
using Lumina.Excel;
using static SkyrimCompass.Gfx;

namespace SkyrimCompass;

// Target bar, target-of-target bar, and the name row between them.
public sealed partial class CompassHud
{
    private const float MainBarShareWithTot = 0.65f;
    private const float TargetBarRowGapFrac = 0.03f;
    private const float CastFadeDur = 0.4f;

    private ulong _castTarget = 0;
    private float _castTracked = 0f, _castFrozen = 0f, _castFadeStart = -1f;
    private float _ribbonPhase, _ribbonEnergy, _ribbonBorn = -1000f;

    private ulong _lastTargetId = 0;
    private float _dispTargetHp = 1f, _lastRawHp = 1f, _flashAlpha = 0f, _dispShield = 0f;

    private ulong _tbLabelTargetId = 0;
    private string? _tbLabelCastName;
    private int _tbLabelLevel = -1;
    private IFontHandle? _tbLabelFont;
    private string _tbLabelText = "";
    private Vector2 _tbLabelSize;

    private static readonly string[] PctLabels = BuildPctLabels();
    private readonly Vector2[] _pctSizeCache = new Vector2[101];
    private IFontHandle? _pctSizeCacheFont;

    private readonly ExcelSheet<Lumina.Excel.Sheets.Action> _actionSheet;
    private readonly Dictionary<uint, string> _actionNameCache = new();

    private static string[] BuildPctLabels()
    {
        var a = new string[101];
        for (int i = 0; i <= 100; i++) a[i] = $"{i}%";
        return a;
    }

    private (float x, float w, float totX, float totW, float rowW) SplitTargetRow(float cx, float cw, bool hasTot)
    {
        float rowW = cw * Math.Clamp(_cfg.TargetBarWidthFraction, 0.1f, 1f);
        float rowX = cx + cw*0.5f - rowW*0.5f;
        if (!hasTot) return (rowX,rowW,0f,0f,rowW);
        float mainW = rowW * MainBarShareWithTot;
        float gap = MathF.Max(10f, rowW * TargetBarRowGapFrac);
        float totW = rowW - mainW - gap;
        return (rowX, mainW, rowX + mainW + gap, totW, rowW);
    }

    private uint TargetFillColor(IGameObject obj, bool inDuty)
    {
        if (obj is ICharacter ch && (ch.StatusFlags & StatusFlags.PartyMember) != 0
            && _cfg.ShowPartyRoleIcons && (!_cfg.PartyRoleIconsOnlyInDuty || inDuty))
            return GetRoleColor(ch);
        uint baseCol = MarkerBaseColor(obj);
        return baseCol != 0u ? baseCol : C(_cfg.NpcColor);
    }

    private (Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl) TrapSlice(float x,float y,float w,float h,float taper,float lo,float hi)
    {
        lo=Math.Clamp(lo,0f,1f); hi=Math.Clamp(hi,0f,1f);
        float botX=x+taper, botSpan=w-2f*taper;
        float topA=x+w*lo, topB=x+w*hi, botA=botX+botSpan*lo, botB=botX+botSpan*hi;
        return (new Vector2(topA,y), new Vector2(topB,y), new Vector2(botB,y+h), new Vector2(botA,y+h));
    }

    private (Vector2 tl,Vector2 tr,Vector2 br,Vector2 bl) TrapFill(float x,float y,float w,float h,float taper,float frac,bool fromRight=false)
    {
        frac=Math.Clamp(frac,0f,1f);
        return fromRight ? TrapSlice(x,y,w,h,taper,1f-frac,1f) : TrapSlice(x,y,w,h,taper,0f,frac);
    }

    private void DrawTrapBar(ImDrawListPtr dl, float x,float y,float w,float h,float fill,
        uint bg,uint fg,float alpha, float? shieldFrac=null, uint? shieldCol=null, bool fromRight=false)
    {
        float taper=MathF.Min(h*0.9f,w*0.35f);
        var (bTL,bTR,bBR,bBL)=TrapFill(x,y,w,h,taper,1f);
        dl.AddQuadFilled(bTL,bTR,bBR,bBL, WithAlpha(bg,alpha));
        const float inset=2f;
        float innerH=h-inset*2f, innerTaper=taper*(innerH/h);
        float cf=Math.Clamp(fill,0f,1f);
        if (cf>0f)
        {
            var (fTL,fTR,fBR,fBL)=TrapFill(x+inset,y+inset,w-inset*2f,innerH,innerTaper,cf,fromRight);
            dl.AddQuadFilled(fTL,fTR,fBR,fBL, WithAlpha(fg,alpha));
        }
        if (shieldFrac.HasValue && shieldFrac.Value>0f && shieldCol.HasValue)
        {
            float sLo = fromRight ? MathF.Max(0f,1f-cf-shieldFrac.Value) : cf;
            float sHi = fromRight ? 1f-cf : MathF.Min(1f, cf+shieldFrac.Value);
            if (sHi > sLo)
            {
                var (sTL,sTR,sBR,sBL)=TrapSlice(x+inset,y+inset,w-inset*2f,innerH,innerTaper,sLo,sHi);
                dl.AddQuadFilled(sTL,sTR,sBR,sBL, WithAlpha(shieldCol.Value,alpha));
            }
        }
        dl.AddLine(V(x+1f,y+1f), V(x+w-1f,y+1f), WithAlpha(0x1AFFFFFFu,alpha),1f);
        dl.AddQuad(bTL,bTR,bBR,bBL, WithAlpha(C(_cfg.BorderColor),alpha),1.5f);
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

    private Vector2 GetPctLabelSize(int pctInt, IFontHandle activeFont)
    {
        if (_pctSizeCacheFont != activeFont)
        {
            Array.Fill(_pctSizeCache, new Vector2(-1f,-1f));
            _pctSizeCacheFont=activeFont;
        }
        var cached=_pctSizeCache[pctInt];
        if (cached.X<0f)
        {
            cached = ImGui.CalcTextSize(PctLabels[pctInt]);
            _pctSizeCache[pctInt]=cached;
        }
        return cached;
    }

    private float RenderTargetBar(ImDrawListPtr dl, float x, float w, float y, float nameCx,
        float nameRowW, float now, float alpha, bool inDuty, IGameObject? curTarget)
    {
        if (curTarget == null) { _castTarget = 0; return y; }
        uint border = WithAlpha(C(_cfg.BorderColor), alpha), bg = WithAlpha(C(_cfg.BackgroundColor), alpha), nameCol = WithAlpha(C(_cfg.CardinalColor), alpha);
        bool isChara = curTarget is ICharacter;
        float h = isChara ? MathF.Max(4f, _cfg.TargetBarHeight) : 0f;
        uint fill = WithAlpha(TargetFillColor(curTarget, inDuty), alpha);

        if (curTarget is ICharacter ch) DrawTargetHp(dl, x, y, w, h, ch, bg, fill, alpha);

        var fontHandle = _fonts.Get(BaseFontPx * _cfg.TargetBarFontScale);
        using var fontScope = fontHandle.Available ? fontHandle.Push() : null;
        float fontSize = ImGui.GetFontSize();

        string? castName = (curTarget is IBattleChara casting && casting.IsCasting && casting.TotalCastTime > 0f)
            ? GetCastName(casting) : null;
        int shownLevel = (castName == null && _cfg.ShowTargetLevel && curTarget is ICharacter lv && lv.Level > 0) ? lv.Level : 0;

        if (_tbLabelTargetId != curTarget.GameObjectId || _tbLabelCastName != castName ||
            _tbLabelLevel != shownLevel || _tbLabelFont != fontHandle)
        {
            string baseLabel = castName ?? curTarget.Name.TextValue;
            _tbLabelText = shownLevel > 0 ? $"Lv{shownLevel}  {baseLabel}" : baseLabel;
            _tbLabelSize = ImGui.CalcTextSize(_tbLabelText);
            _tbLabelTargetId = curTarget.GameObjectId; _tbLabelCastName = castName; _tbLabelLevel = shownLevel;
            _tbLabelFont = fontHandle;
        }
        string label = _tbLabelText;
        var sz = _tbLabelSize;
        float nameGap = MathF.Max(6f, h * 0.5f);
        float nameY = y + h + nameGap, tx = nameCx - sz.X * 0.5f;
        uint shadow = WithAlpha(0xCC000000u, alpha);
        ShadowText(dl, fontSize, V(tx, nameY), nameCol, shadow, label);

        float ornHH = fontSize * 0.46f, ornHW = ornHH * 0.69f;
        float ornGap = 6f;
        float textCy = nameY + sz.Y * 0.5f;
        float leftX = tx - ornGap - ornHW, rightX = tx + sz.X + ornGap + ornHW;
        float shHW = ornHW + 2f, shHH = ornHH + 2f;
        DrawDiamond(dl, leftX, textCy, shHW, shHH, shadow);
        DrawDiamond(dl, rightX, textCy, shHW, shHH, shadow);
        DrawGem(dl, leftX, textCy, ornHW, ornHH, border, false);
        DrawGem(dl, rightX, textCy, ornHW, ornHH, border, false);

        if (isChara && _cfg.ShowTargetBarRibbons)
            DrawNameRibbons(dl, curTarget, leftX - ornHW, rightX + ornHW, nameCx, nameRowW, textCy, border, alpha, now);
        else _castTarget = 0;

        if (_cfg.ShowTargetHealthPercent && isChara)
        {
            float taper = MathF.Min(h * 0.9f, w * 0.35f);
            int pctInt = Math.Clamp((int)(_dispTargetHp * 100f), 0, 100);
            DrawPercent(dl, PctLabels[pctInt], x + taper, y + h + MathF.Max(4f, fontSize * 0.25f), fontSize, alpha);
        }

        float clickTop = y, clickBottom = nameY + sz.Y;
        float clickLeft = MathF.Min(x, leftX - shHW), clickRight = MathF.Max(x + w, rightX + shHW);
        HandleTargetClick(V(clickLeft, clickTop), V(clickRight, clickBottom), curTarget, false);
        TryStartCompassDrag(V(clickLeft, clickTop), V(clickRight, clickBottom));
        return nameY + sz.Y;
    }

    private void DrawTargetHp(ImDrawListPtr dl, float x, float y, float w, float h, ICharacter ch, uint bg, uint fill, float alpha)
    {
        float raw = ch.MaxHp > 0f ? Math.Clamp((float)ch.CurrentHp / ch.MaxHp, 0f, 1f) : 0f;
        float rawShield = _cfg.ShowTargetBarShield ? ch.ShieldPercentage / 100f : 0f;
        bool isMinion = ch.ObjectKind == ObjectKind.Companion;
        if (isMinion) { raw = 1f; _dispTargetHp = 1f; _lastRawHp = 1f; _flashAlpha = 0f; rawShield = 0f; _dispShield = 0f; }
        float dt = ImGui.GetIO().DeltaTime;
        if (ch.GameObjectId != _lastTargetId)
        { _lastTargetId = ch.GameObjectId; _dispTargetHp = raw; _lastRawHp = raw; _flashAlpha = 0f; _dispShield = rawShield; }
        else
        {
            if (!isMinion && raw < _lastRawHp - 0.001f) _flashAlpha = 1f;
            _lastRawHp = raw;
            _dispTargetHp += (raw - _dispTargetHp) * (1f - MathF.Exp(-dt * 14f));
            _dispShield += (rawShield - _dispShield) * (1f - MathF.Exp(-dt * 14f));
        }
        if (!isMinion) _flashAlpha = MathF.Max(0f, _flashAlpha - dt / 0.4f);

        bool showShield = !isMinion && _dispShield > 0f;
        DrawTrapBar(dl, x, y, w, h, _dispTargetHp, bg, fill, alpha,
            showShield ? _dispShield : null,
            showShield ? C(_cfg.TargetBarShieldColor) : null);

        // White flash over the HP just lost.
        if (isMinion || _flashAlpha <= 0f) return;
        float lo = MathF.Min(raw, _dispTargetHp), hi = MathF.Max(raw, _dispTargetHp);
        if (hi <= lo) return;
        float taper = MathF.Min(h * 0.9f, w * 0.35f);
        const float inset = 2f;
        float innerH = h - inset * 2f, innerTaper = taper * (innerH / h);
        var (fTL, fTR, fBR, fBL) = TrapSlice(x + inset, y + inset, w - inset * 2f, innerH, innerTaper, lo, hi);
        dl.AddQuadFilled(fTL, fTR, fBR, fBL, WithAlpha(0xFFFFFFFFu, _flashAlpha * 0.5f * alpha));
    }

    // Braided ribbons flank the name; they grow in on a new target and speed up and fill with the warning color while it casts.
    private void DrawNameRibbons(ImDrawListPtr dl, IGameObject curTarget, float leftEdge, float rightEdge,
        float nameCx, float rowW, float textCy, uint border, float alpha, float t)
    {
        float inset = MathF.Max(8f, rowW * 0.06f);
        float rL = MathF.Min(nameCx - rowW * 0.5f + inset, leftEdge - 24f);
        float rR = MathF.Max(nameCx + rowW * 0.5f - inset, rightEdge + 24f);

        if (curTarget.GameObjectId != _castTarget)
        { _castTarget = curTarget.GameObjectId; _castTracked = 0f; _castFadeStart = -1f; _ribbonBorn = t; }
        var bt = curTarget as IBattleChara;
        bool isCasting = bt?.IsCasting ?? false;
        float totalCast = bt?.TotalCastTime ?? 0f;
        float castReal = isCasting && totalCast > 0f ? Math.Clamp((bt?.CurrentCastTime ?? 0f) / totalCast, 0f, 1f) : 0f;
        float castDisp = UpdateFadeOut(ref _castTracked, ref _castFrozen, ref _castFadeStart,
            castReal, !isCasting && _castTracked > 0f, isCasting, false, t, CastFadeDur, out float castWipe);

        float dt = ImGui.GetIO().DeltaTime;
        _ribbonEnergy += ((castDisp > 0f ? 1f : 0f) - _ribbonEnergy) * (1f - MathF.Exp(-dt * 6f));
        _ribbonPhase += dt * (1.6f + 2.4f * _ribbonEnergy);
        float grow = SmoothStep(Math.Clamp((t - _ribbonBorn) / 0.5f, 0f, 1f));
        uint castCol = WithAlpha(C(_cfg.AggroWarningColor), alpha);

        Ribbon.Draw(dl, V(leftEdge, textCy), V(rL, textCy), border, castCol, alpha, _ribbonPhase, grow, _ribbonEnergy, castDisp, castWipe, 0f);
        Ribbon.Draw(dl, V(rightEdge, textCy), V(rR, textCy), border, castCol, alpha, _ribbonPhase + 2.1f, grow, _ribbonEnergy, castDisp, castWipe, 0.37f);
    }

    private void DrawPercent(ImDrawListPtr dl, string pct, float px, float py, float size, float alpha) =>
        ShadowText(dl, size, V(px, py), WithAlpha(C(_cfg.CardinalColor), alpha), WithAlpha(0xCC000000u, alpha), pct);

    private void RenderTotBar(ImDrawListPtr dl, float x, float w, float y,
        IPlayerCharacter player, float now, float alpha, bool inDuty, IGameObject curTarget, IGameObject tot)
    {
        if (tot is not ICharacter ch) return;
        bool targetingMe = _cfg.HighlightIfTargetingMe && curTarget.TargetObjectId == player.GameObjectId;
        float h = MathF.Max(4f, _cfg.TargetBarHeight);
        uint fill = targetingMe ? C(_cfg.AggroWarningColor) : TargetFillColor(tot, inDuty);
        bool isMinion = tot.ObjectKind == ObjectKind.Companion;
        float frac = isMinion ? 1f : (ch.MaxHp > 0f ? Math.Clamp((float)ch.CurrentHp / ch.MaxHp, 0f, 1f) : 0f);
        float pulse = targetingMe ? 0.82f + 0.18f * MathF.Sin(now * 5f) : 1f;
        DrawTrapBar(dl, x, y, w, h, frac, WithAlpha(C(_cfg.BackgroundColor), alpha), WithAlpha(fill, pulse * alpha), alpha);
        var totFontHandle = _fonts.Get(BaseFontPx * _cfg.TargetBarFontScale);
        using var totFontScope = totFontHandle.Available ? totFontHandle.Push() : null;

        if (_cfg.ShowTargetHealthPercent && _cfg.ShowTargetOfTargetHealthPercent)
        {
            float taper = MathF.Min(h * 0.9f, w * 0.35f);
            float size = ImGui.GetFontSize();
            int pctInt = Math.Clamp((int)(frac * 100f), 0, 100);
            var psz = GetPctLabelSize(pctInt, totFontHandle);
            DrawPercent(dl, PctLabels[pctInt], x + w - taper - psz.X, y + h + MathF.Max(4f, size * 0.25f), size, alpha);
        }

        if (_cfg.ShowTargetOfTargetName)
        {
            bool isPlayer = ch.GameObjectId == player.GameObjectId;
            string label = (isPlayer && _cfg.TargetOfTargetShowYou) ? Loc.T("YOU") : ch.Name.TextValue;
            if (_cfg.TargetOfTargetFirstNameOnly) { int sp = label.IndexOf(' '); if (sp > 0) label = label[..sp]; }
            float scale = 1f;
            var baseSz = ImGui.CalcTextSize(label);
            float maxW = MathF.Max(4f, w - 4f);
            if (baseSz.X > 0f && baseSz.X > maxW) scale = maxW / baseSz.X;
            var sz = baseSz * scale;
            ShadowText(dl, ImGui.GetFontSize() * scale, V(x + (w - sz.X) * 0.5f, y + (h - sz.Y) * 0.5f),
                WithAlpha(C(_cfg.CardinalColor), alpha), WithAlpha(0xCC000000u, alpha), label);
        }
        HandleTargetClick(V(x, y), V(x + w, y + h), ch, true);
        TryStartCompassDrag(V(x, y), V(x + w, y + h));
    }
}
