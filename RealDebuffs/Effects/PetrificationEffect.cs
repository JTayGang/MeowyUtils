using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Color drains out and stone cracks spread in from the edges - you're turning to stone. Uses DrawHelpers' shared jagged-loop primitive, reseeded far less often than a typical crackle so it reads as solid fracture lines rather than crackling energy.</summary>
public sealed class PetrificationEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Petrification;

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        DrawHelpers.DrawVignette(dl, screenSize, DrawHelpers.ToU32(0.55f, 0.55f, 0.53f, 1f), 0.20f, alpha);

        float inset = MathF.Min(screenSize.X, screenSize.Y) * 0.01f;
        uint crackHi = DrawHelpers.WithAlpha(0xFFE8E8E0, alpha * 0.6f);
        uint crackCore = DrawHelpers.WithAlpha(0xFF1A1A1A, alpha);

        // reseed every couple of seconds - enough to feel alive without looking like it's twitching
        int seed = (int)(time / 2.0f);
        DrawHelpers.AddJaggedRectLoop(dl, screenSize, inset, 6f, seed, crackHi, 3f);
        DrawHelpers.AddJaggedRectLoop(dl, screenSize, inset + 2f, 5f, seed + 1, crackCore, 1.5f);
    }
}
