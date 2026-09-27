using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs;

/// <summary>
/// TEST-TOOLS-ish - originally a dev-only helper for previewing effects without hunting down a mob
/// to apply the debuff; now ALSO the mechanism behind the tooltip-keyword settings panel's "preview
/// these on screen" button (see <see cref="TooltipKeywordPanel"/>), since both are exactly the same
/// need - "show this kind, maybe in this color, for a little while, then stop" - and duplicating a
/// second expiry-tracking dictionary for the second caller would just be the same logic twice.
///
/// The original dev panel adds a collapsed "Test effects (dev only)" section to the bottom of the
/// settings window with one checkbox per <see cref="DebuffKind"/> (new kinds show up on their own).
/// Ticking one makes EffectManager draw that effect as if you had the debuff, for
/// <see cref="Seconds"/> seconds, then it switches itself off; the tooltip tester's button does the
/// same thing programmatically for whatever it just parsed, optionally with a color.
///
/// TO REMOVE JUST THE DEV PANEL (keep the tooltip tester's preview button working): delete the
/// "Test effects (dev only)" line in ConfigWindow.Draw (tagged TEST-TOOLS) and the DrawUi method
/// below; leave Force/IsForced/GetForcedColor and the DebuffKind[] Kinds field - the preview button
/// still needs them.
///
/// TO REMOVE THIS FILE ENTIRELY: first decide what happens to the tooltip tester's preview button
/// (drop the button, or give it its own small "temporarily force this kind" dictionary instead of
/// sharing this one), THEN delete this file and the two remaining lines tagged "TEST-TOOLS" (one in
/// EffectManager.Draw's `active |=` line, one in ConfigWindow.Draw) - EffectManager.Draw's color
/// line just above the `active |=` one also reads this file, but only to fall back to null, so it
/// only needs trimming, not deleting; the compiler will point at all of them either way.
///
/// Choices worth knowing about:
///  - Visual only. It's hooked in AFTER EffectManager has told ChatBlocker whether you're silenced, so a
///    test can never swallow your real chat messages, even with "Silence also blocks sending chat" on.
///  - Always temporary. Some effects (Blind at high intensity is nearly a black screen) can hide the very
///    checkbox you'd need to switch them off, so a test always ends by itself.
///  - Same pipeline as a real debuff. A forced effect still respects the master "Enabled" box, the
///    per-effect boxes, cutscene/GPose hiding and the intensity slider - and the "an effect that throws
///    gets disabled" safety net, which bypassing those would defeat (it'd re-throw every frame).
///  - Nothing is saved. Every test (dev checkbox or tooltip preview alike) is gone after a reload.
/// </summary>
internal static class DebugTester
{
    /// <summary>How long a test plays before it switches itself off.</summary>
    public static float Seconds { get; set; } = 15f;

    private static readonly DebuffKind[] Kinds = Enum.GetValues<DebuffKind>();

    // kind -> Environment.TickCount64 (ms) when its test ends. Missing, or in the past = not being tested.
    private static readonly Dictionary<DebuffKind, long> EndsAt = new();

    // kind -> color to force it to while under test, if any - set alongside EndsAt by the overload
    // below. Read even after the test ends (EffectManager only asks for it while IsForced is also
    // true), so a stale entry left over from a previous test is harmless.
    private static readonly Dictionary<DebuffKind, Vector4?> ForcedColor = new();

    /// <summary>EffectManager asks this for each effect: should it draw as if the debuff were on right now?</summary>
    public static bool IsForced(DebuffKind kind) =>
        EndsAt.TryGetValue(kind, out long end) && end > Environment.TickCount64;

    /// <summary>The color a forced test wants (from <see cref="Force(DebuffKind,bool,System.Numerics.Vector4?)"/>), or null for "use the effect's own color". Meaningless unless <see cref="IsForced"/> is also true.</summary>
    public static Vector4? GetForcedColor(DebuffKind kind) => ForcedColor.TryGetValue(kind, out var c) ? c : null;

    /// <summary>Starts (or stops) a test, in the effect's own color.</summary>
    public static void Force(DebuffKind kind, bool on) => Force(kind, on, null);

    /// <summary>Starts (or stops) a test, optionally recolored - see <see cref="Effects.DrawHelpers.PushColorOverride"/> for what a non-null color actually does. Used by the settings window's per-kind test checkboxes (always null) and the tooltip-keyword tester's "preview on screen" button (whatever it just parsed).</summary>
    public static void Force(DebuffKind kind, bool on, Vector4? color)
    {
        EndsAt[kind] = on ? Environment.TickCount64 + (long)(Seconds * 1000f) : 0L;
        ForcedColor[kind] = color;
    }

    /// <summary>
    /// Draws the panel (call it from the end of the settings window's Draw). <paramref name="isShowing"/>
    /// says whether an effect is currently allowed to appear at all; it's only used to add an
    /// "(off in settings)" hint, so a test that shows nothing explains itself.
    /// </summary>
    public static void DrawUi(Func<DebuffKind, bool> isShowing)
    {
        ImGui.Separator();
        if (!ImGui.CollapsingHeader("Test effects (dev only)")) return;

        // The labels below repeat the settings checkboxes above, and ImGui treats two same-label widgets
        // in one window as the SAME widget (ticking one would tick both) - so give this block its own ID scope.
        ImGui.PushID("TestTools");
        try
        {
            ImGui.TextWrapped(
                $"Shows an effect for {Seconds:0}s as if you had the debuff. Visual only - it never triggers " +
                "the chat lockout. Effects switched off above still won't show.");

            long now = Environment.TickCount64;
            foreach (var kind in Kinds)
            {
                bool on = IsForced(kind);
                if (ImGui.Checkbox(kind.ToString(), ref on))
                    Force(kind, on);

                if (EndsAt.TryGetValue(kind, out long end) && end > now)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled($"{(end - now + 999) / 1000}s"); // whole seconds left, rounded up
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
