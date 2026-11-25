namespace RealDebuffs.Effects.Framework.Materials;

/// The fallback stroke: a soft glow under a solid pale core with a small growing-tip flare.
public sealed class StrokeSimple : IStrokeMaterial
{
    public string Name => "stroke.simple";

    static readonly uint Glow = Pack(0.80f, 0.82f, 0.88f);
    static readonly uint Core = Pack(0.94f, 0.96f, 1.00f);

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        if (path.Count < 2 || alpha <= 0.005f) return;

        float width = Max(1f, s.WidthHint * 0.25f);
        float reveal = Saturate(s.Reveal);
        if (reveal <= 0.001f) return;

        // Walk visible segments and emit each as a short stroke.
        float total = path.Length;
        float visibleLen = total * reveal;
        float step = Max(2f, width * 0.8f);
        int steps = Math.Max(2, (int)(visibleLen / step));
        float lastArc = Min(visibleLen, total);

        Vector2 prevPos;
        path.SampleAtArc(0f, out prevPos, out _);

        for (int i = 1; i <= steps; i++)
        {
            float arc = visibleLen * i / steps;
            path.SampleAtArc(arc, out Vector2 pos, out _);

            dl.AddLine(prevPos, pos, WithAlpha(Glow, alpha * 0.35f), width * 3.0f);
            dl.AddLine(prevPos, pos, WithAlpha(Core, alpha * 0.95f), width);
            prevPos = pos;
        }

        if (s.TipFlare > 0.001f)
        {
            path.SampleAtArc(lastArc, out Vector2 tip, out _);
            float k = Saturate(s.TipFlare);
            dl.AddCircleFilled(tip, (6f + 10f * k) * ctx.ScreenScale, WithAlpha(Glow, alpha * 0.35f * k));
            dl.AddCircleFilled(tip, (2f + 3f * k) * ctx.ScreenScale, WithAlpha(Core, alpha * 0.95f * k));
        }
    }
}