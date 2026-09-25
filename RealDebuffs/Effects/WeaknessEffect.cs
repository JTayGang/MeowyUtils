using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Weakness family (Weakness / Brush with Death / Brink of Death): your body is failing you. A slow,
/// heavy pulse darkens the edges like a labored heartbeat - EffectManager scales how strong it looks
/// per status (see DebuffKind.Strengths), so this reads as a faint flicker for Weakness and a deep,
/// lingering pulse for Brink of Death, with no special-casing needed in here.
/// </summary>
public sealed class WeaknessEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Weakness;

    private static readonly uint Tint = DrawHelpers.ToU32(0.35f, 0.03f, 0.05f, 1f);
    private const float Period = 1.7f;

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        // A heartbeat isn't a smooth sine - it's a quick double-thump then a long rest. Two tight
        // pulses close together, then a gap, gives that lub-dub shape instead of a generic breathing
        // effect (which Sleep and Poison already use).
        float phase = (time % Period) / Period;
        float thump = MathF.Max(Beat(phase, 0.06f), Beat(phase, 0.20f) * 0.75f);

        DrawHelpers.DrawVignette(dl, screenSize, Tint, 0.13f + 0.05f * thump, alpha * (0.35f + 0.65f * thump));
    }

    private static float Beat(float phase, float center)
    {
        float d = MathF.Abs(phase - center);
        const float w = 0.05f;
        return d > w ? 0f : 1f - d / w;
    }
}
