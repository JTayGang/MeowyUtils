using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Doom: certain death, ticking down. A dark red-black vignette with jagged veins creeping in from the edges, the whole thing pulsing slowly like something counting down toward you.</summary>
public sealed class DoomEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Doom;

    private static readonly uint Tint = DrawHelpers.ToU32(0.12f, 0.0f, 0.02f, 1f);
    private static readonly uint Vein = DrawHelpers.ToU32(0.55f, 0.02f, 0.05f, 1f);

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        float pulse = 0.6f + 0.4f * DrawHelpers.Pulse(time, 2.2f);

        DrawHelpers.DrawVignette(dl, screenSize, Tint, 0.20f + 0.03f * pulse, alpha * (0.7f + 0.3f * pulse));

        for (int layer = 0; layer < 2; layer++)
        {
            float inset = shortSide * (0.01f + layer * 0.03f);
            float a = alpha * pulse * (0.6f - layer * 0.2f);
            if (a < 0.01f) continue;
            DrawHelpers.AddJaggedRectLoop(dl, screenSize, inset, shortSide * 0.03f, layer * 613 + 5, Vein, 2.2f - layer * 0.6f);
        }
    }
}
