using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Frost family (Frostbite / Deep Freeze): the cold is closing in. An icy blue-white vignette with jagged frost creeping in from the edges like ice spreading across glass - a faint rime for Frostbite, a heavy crust for Deep Freeze.</summary>
public sealed class FrostEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Frost;

    private static readonly uint Tint = DrawHelpers.ToU32(0.55f, 0.75f, 0.95f, 1f);
    private static readonly uint Rime = DrawHelpers.ToU32(0.80f, 0.92f, 1.00f, 1f);

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        DrawHelpers.DrawVignette(dl, screenSize, Tint, 0.11f, alpha * 0.8f);

        // Three nested jagged loops at slightly different insets/seeds read as frost crystals
        // branching inward rather than one clean line, and a faint drift keeps it from looking like
        // a static painted-on border.
        for (int layer = 0; layer < 3; layer++)
        {
            float inset = shortSide * (0.006f + layer * 0.012f) + 4f * DrawHelpers.Pulse(time * 0.3f, 6f, layer * 0.5f);
            float a = alpha * (0.55f - layer * 0.13f);
            if (a < 0.01f) continue;
            DrawHelpers.AddJaggedRectLoop(dl, screenSize, inset, shortSide * 0.012f, layer * 97, Rime, 1.6f + layer * 0.6f);
        }
    }
}
