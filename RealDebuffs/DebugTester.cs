using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using RealDebuffs.Effects;

namespace RealDebuffs;

/// <summary>
/// Preview an effect without needing a matching debuff to actually be active. Callers: the
/// dev-only test panel at the bottom of the settings window, and the tooltip-keyword tester's
/// "preview on screen" button.
///
/// Visual only - hooked in after EffectManager tells ChatBlocker whether you're silenced, so a
/// test never blocks real chat. Always temporary (some effects, like Blind at high intensity,
/// could hide the checkbox you'd need to switch them off). Respects the master Enabled, per-effect
/// toggles, cutscene/GPose hiding, and the intensity slider. Nothing is saved; reload clears it.
///
/// The panel iterates the effect roster passed by ConfigWindow, so only implemented effects get
/// a checkbox. A DebuffKind without an ISceneEffect has nothing to preview and doesn't appear.
/// </summary>
internal static class DebugTester
{
    public static float Seconds { get; set; } = 15f;

    // kind -> TickCount64 ms when its test ends. Missing or past = not being tested.
    private static readonly Dictionary<DebuffKind, long> EndsAt = new();

    // kind -> color to force, set alongside EndsAt. Only meaningful while IsForced is also true.
    private static readonly Dictionary<DebuffKind, Vector4?> ForcedColor = new();

    public static bool IsForced(DebuffKind kind) =>
        EndsAt.TryGetValue(kind, out long end) && end > Environment.TickCount64;

    /// <summary>What color a forced test wants, or null for "use the effect's own".</summary>
    public static Vector4? GetForcedColor(DebuffKind kind) =>
        ForcedColor.TryGetValue(kind, out var c) ? c : null;

    public static void Force(DebuffKind kind, bool on) => Force(kind, on, null);

    /// <summary>
    /// Starts or stops a test, optionally recolored (see DrawHelpers.PushColorOverride for what a
    /// non-null color does). Used by the dev checkboxes (always null) and the tooltip-tester's
    /// preview button (whatever it just parsed).
    /// </summary>
    public static void Force(DebuffKind kind, bool on, Vector4? color)
    {
        EndsAt[kind] = on ? Environment.TickCount64 + (long)(Seconds * 1000f) : 0L;
        ForcedColor[kind] = color;
    }

    /// <summary>
    /// Draws the panel. <paramref name="isShowing"/> says whether an effect is allowed to appear
    /// at all right now; it's only used to add an "(off in settings)" hint so a test that shows
    /// nothing explains itself. <paramref name="effects"/> is the roster to iterate; only
    /// implemented effects get a checkbox.
    /// </summary>
    public static void DrawUi(Func<DebuffKind, bool> isShowing, IReadOnlyList<ISceneEffect> effects)
    {
        ImGui.Separator();
        if (!ImGui.CollapsingHeader("Test effects (dev only)")) return;

        // Own ID scope: labels repeat the settings-window checkboxes, and ImGui would otherwise
        // treat them as the same widgets (ticking one would tick both).
        ImGui.PushID("TestTools");
        try
        {
            ImGui.TextWrapped(
                $"Shows an effect for {Seconds:0}s as if you had the debuff. Visual only - it never " +
                "triggers the chat lockout. Effects switched off above still won't show.");

            long now = Environment.TickCount64;
            foreach (var effect in effects)
            {
                var kind = effect.Kind;
                bool on = IsForced(kind);
                if (ImGui.Checkbox(effect.DisplayName, ref on))
                    Force(kind, on);

                if (EndsAt.TryGetValue(kind, out long end) && end > now)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"{(end - now + 999) / 1000}s");
                }

                if (!isShowing(kind))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("(off in settings)");
                }
            }

            if (ImGui.Button("All off"))
                EndsAt.Clear();
        }
        finally
        {
            ImGui.PopID();
        }
    }
}