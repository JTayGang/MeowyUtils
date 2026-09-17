using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Your eyes just stopped working. Near-total darkness with a heavier vignette at the edges, and a slow breathing flicker so it doesn't read as a flat static overlay.</summary>
public sealed class BlindEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Blind;

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        float breathe = 0.92f + 0.08f * DrawHelpers.Pulse(time, 2.6f);
        float a = alpha * breathe;

        dl.AddRectFilled(DrawHelpers.V(0, 0), screenSize, DrawHelpers.WithAlpha(0xFF000000, a * 0.55f));
        DrawHelpers.DrawVignette(dl, screenSize, 0xFF000000, 0.22f, a);
    }
}
