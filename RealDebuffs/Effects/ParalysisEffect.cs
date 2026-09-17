using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Crackling electric arcs hug the screen edges, with a bright core over a soft glow and the occasional branching spark.</summary>
public sealed class ParalysisEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Paralysis;

    // Reseed the jagged path on a fixed tick rather than every frame - a continuously-reshuffled
    // line reads as "crackling"; reshuffling at 60+fps just looks like flat noise.
    private const float JitterInterval = 0.09f;
    private const int SparkSlots = 5;

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        int tick = (int)(time / JitterInterval);

        float flicker = 0.55f + 0.45f * (DrawHelpers.Hash01(tick) > 0.15f ? 1f : DrawHelpers.HashRange(tick, 0.3f, 1f));
        float a = alpha * flicker;

        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        float inset = shortSide * 0.012f;
        float jag = shortSide * 0.01f;

        uint glow = DrawHelpers.WithAlpha(DrawHelpers.ToU32(0.55f, 0.85f, 1f, 1f), a * 0.5f);
        uint core = DrawHelpers.WithAlpha(DrawHelpers.ToU32(0.85f, 0.97f, 1f, 1f), a);

        DrawHelpers.AddJaggedRectLoop(dl, screenSize, inset, jag * 1.6f, tick, glow, 5f);
        DrawHelpers.AddJaggedRectLoop(dl, screenSize, inset, jag, tick + 977, core, 1.6f);

        for (int i = 0; i < SparkSlots; i++)
        {
            int s = tick * 31 + i * 7919;
            if (DrawHelpers.Hash01(s) > 0.55f) continue; // most slots stay empty most ticks

            var edgePos = RandomEdgePoint(screenSize, inset, s);
            var dir = DrawHelpers.V(DrawHelpers.HashRange(s + 1, -1f, 1f), DrawHelpers.HashRange(s + 2, -1f, 1f));
            if (dir.LengthSquared() < 0.0001f) continue;
            float len = DrawHelpers.HashRange(s + 3, 10f, 28f);
            dl.AddLine(edgePos, edgePos + Vector2.Normalize(dir) * len, core, 2f);
        }
    }

    private static Vector2 RandomEdgePoint(Vector2 size, float inset, int seed)
    {
        float perim = 2f * (size.X + size.Y);
        float d = DrawHelpers.Hash01(seed) * perim;
        float w = size.X - inset * 2f, h = size.Y - inset * 2f;

        if (d < w) return DrawHelpers.V(inset + d, inset);
        if (d < w + h) return DrawHelpers.V(size.X - inset, inset + (d - w));
        if (d < 2 * w + h) return DrawHelpers.V(size.X - inset - (d - w - h), size.Y - inset);
        return DrawHelpers.V(inset, size.Y - inset - (d - 2 * w - h));
    }
}
