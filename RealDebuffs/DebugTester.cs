using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs;

/// <summary>
/// TEST-TOOLS - a dev-only helper for previewing effects without hunting down a mob to apply the debuff.
///
/// Adds a collapsed "Test effects (dev only)" section to the bottom of the settings window with one
/// checkbox per <see cref="DebuffKind"/> (new kinds show up on their own). Ticking one makes
/// EffectManager draw that effect as if you had the debuff, for <see cref="Seconds"/> seconds, then it
/// switches itself off.
///
/// TO REMOVE IT: delete this file, then delete the two lines tagged "TEST-TOOLS" (one in
/// EffectManager.Draw, one in ConfigWindow.Draw) - the compiler will point straight at them.
///
/// Choices worth knowing about:
///  - Visual only. It's hooked in AFTER EffectManager has told ChatBlocker whether you're silenced, so a
///    test can never swallow your real chat messages, even with "Silence also blocks sending chat" on.
///  - Always temporary. Some effects (Blind at high intensity is nearly a black screen) can hide the very
///    checkbox you'd need to switch them off, so a test always ends by itself.
///  - Same pipeline as a real debuff. A forced effect still respects the master "Enabled" box, the
///    per-effect boxes, cutscene/GPose hiding and the intensity slider - and the "an effect that throws
///    gets disabled" safety net, which bypassing those would defeat (it'd re-throw every frame).
///  - Nothing is saved. Every test is gone after a reload.
/// </summary>
internal static class DebugTester
{
    /// <summary>How long a test plays before it switches itself off.</summary>
    public static float Seconds { get; set; } = 15f;

    private static readonly DebuffKind[] Kinds = Enum.GetValues<DebuffKind>();

    // kind -> Environment.TickCount64 (ms) when its test ends. Missing, or in the past = not being tested.
    private static readonly Dictionary<DebuffKind, long> EndsAt = new();

    /// <summary>EffectManager asks this for each effect: should it draw as if the debuff were on right now?</summary>
    public static bool IsForced(DebuffKind kind) =>
        EndsAt.TryGetValue(kind, out long end) && end > Environment.TickCount64;

    /// <summary>Starts (or stops) a test.</summary>
    public static void Force(DebuffKind kind, bool on) =>
        EndsAt[kind] = on ? Environment.TickCount64 + (long)(Seconds * 1000f) : 0L;

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
