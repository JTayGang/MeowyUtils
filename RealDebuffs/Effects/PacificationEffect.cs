using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Pacification: your hands won't do what you tell them. A soft golden restraining glow pulses along the bottom edge, roughly where the action bars sit, like something is holding your arms down.</summary>
public sealed class PacificationEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Pacification;

    private static readonly uint Glow = DrawHelpers.ToU32(0.85f, 0.70f, 0.30f, 1f);

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float pulse = 0.55f + 0.45f * DrawHelpers.Pulse(time, 2.6f);
        float bandHeight = screenSize.Y * 0.22f;
        uint edge = DrawHelpers.WithAlpha(Glow, alpha * 0.45f * pulse);
        const uint clear = 0u;

        dl.AddRectFilledMultiColor(
            new Vector2(0, screenSize.Y - bandHeight), new Vector2(screenSize.X, screenSize.Y),
            clear, clear, edge, edge);

        // A brighter, tighter line right at the very edge - the "binding" itself, not just the glow around it.
        dl.AddLine(new Vector2(0, screenSize.Y - 2f), new Vector2(screenSize.X, screenSize.Y - 2f),
            DrawHelpers.WithAlpha(Glow, alpha * 0.8f * pulse), 3f);
    }
}
