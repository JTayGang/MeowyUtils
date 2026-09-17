using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace RealDebuffs.Windows;

public sealed class ConfigWindow : Window
{
    private readonly Configuration _config;
    private readonly Action _save;

    public ConfigWindow(Configuration config, Action save)
        : base("Real Debuffs Settings###RealDebuffsConfig")
    {
        _config = config;
        _save = save;
        Size = new Vector2(430, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        bool changed = false;

        bool enabled = _config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled)) { _config.Enabled = enabled; changed = true; }

        bool hideCutscenes = _config.HideDuringCutscenes;
        if (ImGui.Checkbox("Hide during cutscenes", ref hideCutscenes)) { _config.HideDuringCutscenes = hideCutscenes; changed = true; }

        float intensity = _config.GlobalIntensity;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("Overall intensity", ref intensity, 0.1f, 1.75f, "%.2f")) { _config.GlobalIntensity = intensity; changed = true; }

        ImGui.Separator();
        ImGui.TextDisabled("Per-debuff effects");
        ImGui.Spacing();

        changed |= EffectToggle(DebuffKind.Blind, "Blind", "Screen darkens with a heavy vignette.");
        changed |= EffectToggle(DebuffKind.Paralysis, "Paralysis", "Crackling electric arcs around the screen edges.");
        changed |= EffectToggle(DebuffKind.Silence, "Silence", "Floating purple glyphs drift from the edges. See the Advanced section below for an actual chat lockout.");
        changed |= EffectToggle(DebuffKind.Stun, "Stun / Deep Freeze / Down for the Count", "Twinkling stars orbit near the top of the screen.");
        changed |= EffectToggle(DebuffKind.Sleep, "Sleep", "Soft blue tint with drowsy Zs drifting up from the corners.");
        changed |= EffectToggle(DebuffKind.Poison, "Poison", "Sickly green tint with drips falling from the top.");
        changed |= EffectToggle(DebuffKind.Bind, "Bind", "Roots creep up from the bottom of the screen.");
        changed |= EffectToggle(DebuffKind.Heavy, "Heavy", "A heavy dark pull with a dragging chain at the bottom of the screen.");
        changed |= EffectToggle(DebuffKind.Petrification, "Petrification", "Color drains out and stone cracks spread in from the edges.");

        ImGui.Separator();
        ImGui.TextDisabled("Advanced");
        ImGui.Spacing();

        bool blockChat = _config.SilenceBlocksChat;
        if (ImGui.Checkbox("Silence also blocks sending chat", ref blockChat)) { _config.SilenceBlocksChat = blockChat; changed = true; }
        ImGui.TextWrapped(
            "Hooks the game's chat-send function so messages don't actually go out while you're " +
            "silenced, rather than only showing the visual effect. This is a deeper game hook than " +
            "any of the effects above need, so if a game update ever breaks something, this is the " +
            "first setting to try turning off - everything else is unaffected by it.");

        if (changed)
            _save();
    }

    private bool EffectToggle(DebuffKind kind, string label, string description)
    {
        bool value = _config.IsEnabled(kind);
        bool didChange = ImGui.Checkbox(label, ref value);
        if (didChange) _config.SetEnabled(kind, value);

        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(description);

        return didChange;
    }
}
