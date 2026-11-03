using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Interface.ManagedFontAtlas;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using static SkyrimCompass.Gfx;

namespace SkyrimCompass;

// The compass strip: body, limit-break glow, ticks, labels, center pip and end caps.
public sealed partial class CompassHud
{
    private const float LbFadeDur = 2f, LbDropThresh = 0.4f;

    private static readonly (float Deg, string Label, bool IsMajor)[] Directions =
    [
        (0f, "N", true), (45f, "NE", false), (90f, "E", true), (135f, "SE", false),
        (180f, "S", true), (225f, "SW", false), (270f, "W", true), (315f, "NW", false),
    ];

    private float _lbTracked = 0f, _lbFrozen = 0f, _lbFadeStart = -1f;
    private readonly (float bar, float tMul, float tOff, Vector4 col)[] _lbLayers = new (float, float, float, Vector4)[3];

    private float _tickLabelH;
    private readonly Dictionary<string, Vector2> _dirLabelSizeCache = new();
    private IFontHandle? _labelMetricsFont;

    private void RenderBar(ImDrawListPtr dl, float bx, float by, float bw, float bh, float heading,
        IPlayerCharacter player, Vector3 origin, float now, bool inDuty)
    {
        float cx = bx + bw * 0.5f, cy = by + bh * 0.5f;
        float halfW = bw * 0.5f, halfVis = _cfg.VisibleDegrees * 0.5f, lens = _cfg.LensStrength;

        DrawBarBody(dl, bx, by, bw, bh);
        DrawLimitBreakGlow(dl, bx, by, bw, bh, now);
        DrawCenterPip(dl, cx, by, WithAlpha(C(_cfg.CardinalColor), 0.95f));

        dl.PushClipRect(V(bx + 1f, by), V(bx + bw - 1f, by + bh), true);
        var fontHandle = _fonts.Get(BaseFontPx * _cfg.FontScale);
        using var fontScope = fontHandle.Available ? fontHandle.Push() : null;
        DrawTicksAndLabels(dl, cx, by, bh, halfVis, halfW, lens, heading, fontHandle);
        RenderMarkers(dl, cx, cy, halfVis, halfW, lens, heading, player, origin, inDuty);
        dl.PopClipRect();

        DrawEndCaps(dl, bx, bw, cy, bh);
    }

    private void DrawBarBody(ImDrawListPtr dl, float bx, float by, float bw, float bh)
    {
        float cx = bx + bw * 0.5f;
        dl.AddRectFilled(V(bx, by), V(bx + bw, by + bh), C(_cfg.BackgroundColor));
        uint warm = C(new Vector4(0.9f, 0.70f, 0.35f, 0.08f));
        float gw = bw * 0.22f;
        dl.AddRectFilledMultiColor(V(cx - gw, by), V(cx, by + bh), 0u, warm, warm, 0u);
        dl.AddRectFilledMultiColor(V(cx, by), V(cx + gw, by + bh), warm, 0u, 0u, warm);
        dl.AddRectFilledMultiColor(V(bx, by), V(bx + bw * 0.14f, by + bh), 0xAA000000u, 0u, 0u, 0xAA000000u);
        dl.AddRectFilledMultiColor(V(bx + bw * 0.86f, by), V(bx + bw, by + bh), 0u, 0xAA000000u, 0xAA000000u, 0u);
        dl.AddLine(V(bx + 1f, by + 1f), V(bx + bw - 1f, by + 1f), 0x1AFFFFFFu, 1f);
        dl.AddRect(V(bx, by), V(bx + bw, by + bh), C(_cfg.BorderColor), 0f, ImDrawFlags.None, 1.5f);
    }

    private void DrawLimitBreakGlow(ImDrawListPtr dl, float bx, float by, float bw, float bh, float now)
    {
        float rawLb = GetLB();
        float dispLb = UpdateFadeOut(ref _lbTracked, ref _lbFrozen, ref _lbFadeStart,
            rawLb, rawLb < _lbTracked - LbDropThresh, false, true, now, LbFadeDur, out float lbWipe);
        float prog = _cfg.ShowLimitBreakGlow ? dispLb : 0f;
        if (prog <= 0f) return;

        _lbLayers[0] = (Math.Clamp(prog, 0f, 1f), 1.00f, 0f, _cfg.LimitBreakGlowColor);
        _lbLayers[1] = (Math.Clamp(prog - 1f, 0f, 1f), 1.60f, 3.7f, _cfg.LimitBreakGlowColor2);
        _lbLayers[2] = (Math.Clamp(prog - 2f, 0f, 1f), 0.65f, 7.1f, _cfg.LimitBreakGlowColor3);
        foreach (var (bar, tMul, tOff, col) in _lbLayers)
        {
            if (bar <= 0f) continue;
            float segW = bw * 0.5f * bar;
            uint c = C(col);
            float t = now * tMul + tOff;
            float intensity = PulseIntensity(t);
            DrawGlowBracket(dl, bx, by, bw, bh, segW, c, intensity, t, lbWipe, bar, true);
            DrawGlowBracket(dl, bx, by, bw, bh, segW, c, intensity, t, lbWipe, bar, false);
        }
    }

    private static void DrawGlowBracket(ImDrawListPtr dl, float bx, float by, float bw, float bh,
        float segW, uint col, float intensity, float time, float wipe, float fill, bool fromLeft)
    {
        float x0 = fromLeft ? bx : bx + bw - segW;
        float x1 = fromLeft ? bx + segW : bx + bw;
        DrawFlowingLine(dl, V(x0, by), V(x1, by), col, time, 1f, 0f, intensity, fromLeft, wipe, fill);
        DrawFlowingLine(dl, V(x0, by + bh), V(x1, by + bh), col, time, 1f, 0f, intensity, fromLeft, wipe, fill);
    }

    // Drawn before ticks, labels and markers so they all sit on top of it.
    private static void DrawCenterPip(ImDrawListPtr dl, float cx, float by, uint col)
    {
        const float h = 10f, w = 6f;
        dl.AddTriangleFilled(V(cx + 1f, by + h + 2f), V(cx - w + 1f, by + 1f), V(cx + w + 1f, by + 1f), 0x55000000u);
        dl.AddTriangleFilled(V(cx, by + h + 1f), V(cx - w, by), V(cx + w, by), col);
    }

    private void DrawTicksAndLabels(ImDrawListPtr dl, float cx, float by, float bh,
        float halfVis, float halfW, float lens, float heading, IFontHandle fontHandle)
    {
        float ext = halfVis * lens;
        uint tick = C(_cfg.TickColor), card = C(_cfg.CardinalColor), inter = C(_cfg.IntercardinalColor);
        float fontSize = ImGui.GetFontSize();
        var font = ImGui.GetFont();
        float labelTop = by + bh * 0.12f;
        if (_labelMetricsFont != fontHandle)
        {
            _tickLabelH = ImGui.CalcTextSize("N").Y;
            _dirLabelSizeCache.Clear();
            _labelMetricsFont = fontHandle;
        }
        float maxTickH = MathF.Max(2f, (by + bh - 1f) - (labelTop + _tickLabelH));

        for (int d = 0; d < 360; d += 5)
        {
            float delta = Delta(heading, d);
            if (MathF.Abs(delta) > ext + 2f) continue;
            float sx = cx + Project(delta, halfVis, halfW, lens);
            bool is90 = d % 90 == 0, is45 = d % 45 == 0, is10 = d % 10 == 0;
            float th = is90 ? bh * 0.52f : is45 ? bh * 0.36f : is10 ? bh * 0.22f : bh * 0.13f;
            th = MathF.Min(th, maxTickH);
            float la = LensEdgeAlpha(delta, halfVis, ext);
            dl.AddLine(V(sx, by + bh - th - 1f), V(sx, by + bh - 1f), WithAlpha(is90 ? card : tick, la), is90 ? 2f : 1f);
        }

        foreach (var (deg, label, isMajor) in Directions)
        {
            float delta = Delta(heading, deg);
            if (MathF.Abs(delta) > ext + 10f) continue;
            float sx = cx + Project(delta, halfVis, halfW, lens);
            if (!_dirLabelSizeCache.TryGetValue(label, out var sz))
            {
                sz = ImGui.CalcTextSize(label);
                _dirLabelSizeCache[label] = sz;
            }
            float tx = sx - sz.X * 0.5f;
            float la = LensEdgeAlpha(delta, halfVis * 0.88f, ext);
            dl.AddText(font, fontSize, V(tx + 1f, labelTop + 1f), WithAlpha(0xBB000000u, la), label);
            dl.AddText(font, fontSize, V(tx, labelTop), WithAlpha(isMajor ? card : inter, la), label);
        }
    }

    private void DrawEndCaps(ImDrawListPtr dl, float bx, float bw, float cy, float bh)
    {
        float hw = bh * 0.44f, hh = bh * 0.64f;
        uint fill = (C(_cfg.BackgroundColor) & 0x00FFFFFFu) | 0xFF000000u, edge = C(_cfg.BorderColor);
        DrawEndCap(dl, bx, cy, hw, hh, fill, edge);
        DrawEndCap(dl, bx + bw, cy, hw, hh, fill, edge);
    }

    // Faceted gem: shadow, fill, light and dark facets, then the outlined center stone.
    private static void DrawEndCap(ImDrawListPtr dl, float cx, float cy, float hw, float hh, uint fill, uint edge)
    {
        DrawDiamond(dl, cx, cy + 1.5f, hw * 1.05f, hh * 1.04f, 0x64000000u);
        DrawDiamond(dl, cx, cy, hw, hh, fill);

        uint rgb = edge & 0x00FFFFFFu;
        Vector2 top = V(cx, cy - hh), bottom = V(cx, cy + hh), left = V(cx - hw, cy), right = V(cx + hw, cy), mid = V(cx, cy);
        dl.AddTriangleFilled(top, mid, left, rgb | 0x22000000u);
        dl.AddTriangleFilled(top, right, mid, rgb | 0x0C000000u);
        dl.AddTriangleFilled(bottom, mid, right, 0x46000000u);
        DrawGem(dl, cx, cy, hw, hh, edge, true);
    }

    private static unsafe float GetLB()
    {
        var ui = UIState.Instance();
        if (ui == null) return 0f;
        var lb = ui->LimitBreakController;
        return lb.BarUnits <= 0 ? 0f : Math.Clamp((float)lb.CurrentUnits / lb.BarUnits, 0f, 3f);
    }
}
