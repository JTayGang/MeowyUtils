using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.Types;
using static SkyrimCompass.Gfx;

namespace SkyrimCompass;

// Mouse handling: dragging the HUD, clicking target bars, and the context-menu dimming.
public sealed partial class CompassHud
{
    private const float CtxFadeSecs = 0.15f, CtxDimAlpha = 0.33f;

    private bool _ctxMenuWasOpen;
    private float _ctxFadeChange = -1000f;

    // null = no right-click in flight, true = we own it, false = let it pass to the game. Fixed on press, held until release.
    private bool? _rightClickCapture;

    private bool _isDraggingCompass;
    private Vector2 _dragStartMouse;
    private float _dragStartXOffset;
    private float _dragStartYOffset;

    private float UpdateCtxFade(float now)
    {
        bool open = IsCtxMenuOpen();
        if (open != _ctxMenuWasOpen) { _ctxFadeChange = now; _ctxMenuWasOpen = open; }
        float t = Math.Clamp((now - _ctxFadeChange) / CtxFadeSecs, 0f, 1f);
        return Lerp(open ? 1f : CtxDimAlpha, open ? CtxDimAlpha : 1f, SmoothStep(t));
    }

    // IsMouseHoveringRect ignores window ordering, so also require that no ImGui window is hovered.
    private static bool IsHoveringRect(Vector2 min, Vector2 max) =>
        ImGui.IsMouseHoveringRect(min, max, false) && !ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow);

    private bool IsCtxMenuOpen() => _gg.GetAddonByName("ContextMenu").IsVisible || _gg.GetAddonByName("AddonContextSub").IsVisible;

    private void HandleTargetClick(Vector2 min, Vector2 max, IGameObject obj, bool allowLeft)
    {
        var io = ImGui.GetIO();

        // Decide once per press so the release is handled the same way; a mismatch leaves the game thinking the button is held.
        if (_rightClickCapture.HasValue)
        {
            bool rightDown = ImGui.IsMouseDown(ImGuiMouseButton.Right);
            // Keep owning the press until the frame the button comes up (so the game never sees a lone release)...
            if (_rightClickCapture.Value && (rightDown || ImGui.IsMouseReleased(ImGuiMouseButton.Right))) io.WantCaptureMouse = true;
            // ...but clear on "not held", not on the release event, which is lost if Draw was skipped that frame.
            if (!rightDown) _rightClickCapture = null;
            return;
        }

        // Menu already open: pass the click through so the game closes it.
        if (_ctxMenuWasOpen)
        {
            if (IsHoveringRect(min, max) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                _rightClickCapture = false;
            return;
        }

        if (!IsHoveringRect(min, max)) return;
        io.WantCaptureMouse = true;
        if (allowLeft && ImGui.IsMouseClicked(ImGuiMouseButton.Left)) { _tm.Target = obj; return; }
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _rightClickCapture = true;
            OpenCtxMenu(obj);
        }
    }

    private void UpdateCompassDrag()
    {
        if (!_isDraggingCompass) return;
        var io = ImGui.GetIO();
        io.WantCaptureMouse = true;
        // End on "not held" (the release event is lost if Draw was skipped); checked first so a stale drag can't jump the compass.
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) { _isDraggingCompass = false; _cfg.Save(_pi); return; }
        ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
        var delta = ImGui.GetMousePos() - _dragStartMouse;
        float xRange = MathF.Max(0f, (io.DisplaySize.X - _cfg.CompassWidth) * 0.5f);
        float yMax = MathF.Max(0f, io.DisplaySize.Y - _cfg.CompassHeight);
        _cfg.XOffset = Math.Clamp(_dragStartXOffset + delta.X, -xRange, xRange);
        _cfg.YOffset = Math.Clamp(_dragStartYOffset + delta.Y, 0f, yMax);
    }

    private void TryStartCompassDrag(Vector2 min, Vector2 max)
    {
        if (_isDraggingCompass || _cfg.LockPosition) return;
        if (!IsHoveringRect(min, max)) return;
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
}
