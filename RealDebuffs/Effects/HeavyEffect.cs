using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>A heavy, dark pull at the bottom of the screen with a slow sag and a faint dragging chain - every step feels like wading through mud.</summary>
public sealed class HeavyEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Heavy;

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float sag = DrawHelpers.Pulse(time, 1.8f);
        float t = screenSize.Y * (0.10f + 0.02f * sag);
        uint dark = DrawHelpers.WithAlpha(0xFF000000, alpha * 0.55f);
        dl.AddRectFilledMultiColor(DrawHelpers.V(0, screenSize.Y - t), screenSize, 0u, 0u, dark, dark);

        float cx = screenSize.X * 0.5f;
        float baseY = screenSize.Y - t * 0.35f;
        uint linkColor = DrawHelpers.WithAlpha(0xFF9AA0A6, alpha * 0.8f);
        for (int i = -2; i <= 2; i++)
        {
            float x = cx + i * 18f;
            float y = baseY + 6f * DrawHelpers.Pulse(time, 1.8f, i * 0.15f);
            dl.AddCircle(DrawHelpers.V(x, y), 8f, linkColor, 12, 2.5f);
        }
    }
}
