using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>Cartoon "seeing stars": a few stars orbit near the top of the screen over a soft white flash vignette. Covers Stun, Deep Freeze, and Down for the Count - mechanically identical (can't act, can't move), just different sources.</summary>
public sealed class StunEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Stun;
    private const int StarCount = 4;

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        // soft, not meant to obscure vision - just the "you just got hit" flash feeling
        DrawHelpers.DrawVignette(dl, screenSize, 0xFFFFFFFF, 0.05f, alpha * 0.5f);

        float cx = screenSize.X * 0.5f;
        float cy = screenSize.Y * 0.12f;
        float radiusX = screenSize.X * 0.14f;
        float radiusY = screenSize.Y * 0.04f;

        for (int i = 0; i < StarCount; i++)
        {
            float t = time * 1.6f + i * (MathF.PI * 2f / StarCount);
            var pos = DrawHelpers.V(cx + MathF.Cos(t) * radiusX, cy + MathF.Sin(t) * radiusY);
            float twinkle = 0.6f + 0.4f * DrawHelpers.Pulse(time * 2f, 1f, i * 0.3f);
            DrawStar(dl, pos, 9f + 3f * twinkle, DrawHelpers.WithAlpha(0xFFFFE07F, alpha * twinkle));
        }
    }

    /// <summary>Drawn as an outline + center dot rather than a filled polygon - a 5-point star silhouette is concave, so a naive convex fill would render wrong.</summary>
    private static void DrawStar(ImDrawListPtr dl, Vector2 center, float radius, uint color)
    {
        const int points = 5;
        Span<Vector2> verts = stackalloc Vector2[points * 2];
        for (int i = 0; i < verts.Length; i++)
        {
            float r = (i % 2 == 0) ? radius : radius * 0.42f;
            float ang = MathF.PI * i / points - MathF.PI / 2f;
            verts[i] = center + DrawHelpers.V(MathF.Cos(ang) * r, MathF.Sin(ang) * r);
        }
        for (int i = 0; i < verts.Length; i++)
            dl.AddLine(verts[i], verts[(i + 1) % verts.Length], color, 2f);
        dl.AddCircleFilled(center, radius * 0.3f, color);
    }
}
