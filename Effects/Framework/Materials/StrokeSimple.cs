using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A plain baseline stroke: a soft outer glow under a solid core. Neutral pale line, no
/// decoration, no flourish beyond a small tip flare while growing. Exists so the framework
/// always has *something* to render a stroke with even if no material is registered for the
/// slot; if this ever appears on screen, it means a stroke is unconfigured.
/// </summary>
public sealed class StrokeSimple : IStrokeMaterial
{
    public string Name => "stroke.simple";

    private static readonly uint Glow = DrawHelpers.ToU32(0.80f, 0.82f, 0.88f, 1f);
    private static readonly uint Core = DrawHelpers.ToU32(0.94f, 0.96f, 1.00f, 1f);

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        if (path.Count < 2 || alpha <= 0.005f) return;

        float width = MathF.Max(1f, s.WidthHint * 0.25f);
        float reveal = Math.Clamp(s.Reveal, 0f, 1f);
        if (reveal <= 0.001f) return;

        // Walk visible segments and emit each as a short stroke.
        float total = path.Length;
        float visibleLen = total * reveal;
        float step = MathF.Max(2f, width * 0.8f);
        int steps = Math.Max(2, (int)(visibleLen / step));
        float lastArc = MathF.Min(visibleLen, total);

        Vector2 prevPos;
        path.SampleAtArc(0f, out prevPos, out _);

        for (int i = 1; i <= steps; i++)
        {
            float arc = visibleLen * i / steps;
            path.SampleAtArc(arc, out Vector2 pos, out _);

            dl.AddLine(prevPos, pos, DrawHelpers.WithAlpha(Glow, alpha * 0.35f), width * 3.0f);
            dl.AddLine(prevPos, pos, DrawHelpers.WithAlpha(Core, alpha * 0.95f), width);
            prevPos = pos;
        }

        if (s.TipFlare > 0.001f)
        {
            path.SampleAtArc(lastArc, out Vector2 tip, out _);
            float k = Math.Clamp(s.TipFlare, 0f, 1f);
            dl.AddCircleFilled(tip, (6f + 10f * k) * ctx.ScreenScale, DrawHelpers.WithAlpha(Glow, alpha * 0.35f * k));
            dl.AddCircleFilled(tip, (2f + 3f * k) * ctx.ScreenScale, DrawHelpers.WithAlpha(Core, alpha * 0.95f * k));
        }
    }
}