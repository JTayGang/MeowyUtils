using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs;

/// <summary>
/// Preview an effect without needing a matching debuff to actually be active. Two callers: the
/// dev-only test panel at the bottom of the settings window, and the tooltip-keyword tester's
/// "preview on screen" button.
///
/// Visual only - hooked in after EffectManager tells ChatBlocker whether you're silenced, so a
/// test never blocks real chat. Always temporary (some effects, like Blind at high intensity,
/// could hide the checkbox you'd need to switch them off). Respects the master Enabled, per-effect
/// toggles, cutscene/GPose hiding, and the intensity slider. Nothing is saved; reload clears it.
///
/// To remove just the dev panel: delete the TEST-TOOLS line in ConfigWindow.Draw and DrawUi below,
/// leaving Force/IsForced/GetForcedColor for the tooltip-tester preview button. To remove the file
/// entirely, also delete the two remaining TEST-TOOLS lines (one in EffectManager.Draw, one in
/// ConfigWindow.Draw); the color fallback line just above EffectManager's active check only needs
/// trimming. The compiler will point at all of them either way.
/// </summary>
internal static class DebugTester
{
    public static float Seconds { get; set; } = 15f;

    private static readonly DebuffKind[] Kinds = Enum.GetValues<DebuffKind>();

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
    /// Draws the panel. <paramref name="isShowing"/> says whether an effect is allowed to appear at
    /// all right now; it's only used to add an "(off in settings)" hint so a test that shows
    /// nothing explains itself.
    /// </summary>
    public static void DrawUi(Func<DebuffKind, bool> isShowing)
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
            foreach (var kind in Kinds)
            {
                bool on = IsForced(kind);
                if (ImGui.Checkbox(kind.ToString(), ref on))
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