using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

internal static class DrawHelpers
{
    public static Vector2 V(float x, float y) => new(x, y);

    public static uint ToU32(float r, float g, float b, float a) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));

    /// <summary>Returns <paramref name="color"/> with its alpha channel multiplied by <paramref name="mul"/> (0..1).</summary>
    public static uint WithAlpha(uint color, float mul)
    {
        mul = Math.Clamp(mul, 0f, 1f);
        uint a = (color >> 24) & 0xFF;
        a = (uint)(a * mul);
        return (color & 0x00FFFFFF) | (a << 24);
    }

    /// <summary>Linearly blends every channel (including alpha) between two packed colors. <paramref name="t"/> is clamped to 0..1.</summary>
    public static uint LerpColor(uint a, uint b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        byte Channel(int shift) => (byte)(((a >> shift) & 0xFF) + (((b >> shift) & 0xFF) - (int)((a >> shift) & 0xFF)) * t);
        return Channel(0) | ((uint)Channel(8) << 8) | ((uint)Channel(16) << 16) | ((uint)Channel(24) << 24);
    }

    /// <summary>Standard ease-out-cubic curve: starts fast, settles gently. <paramref name="t"/> is clamped to 0..1.</summary>
    public static float EaseOutCubic(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float inv = 1f - t;
        return 1f - inv * inv * inv;
    }

    /// <summary>Deterministic 0..1 pseudo-random from an integer seed - stable within a frame, cheap, allocation-free. Same seed always gives the same value.</summary>
    public static float Hash01(int seed)
    {
        unchecked
        {
            uint h = (uint)seed;
            h ^= h >> 16; h *= 0x7feb352d;
            h ^= h >> 15; h *= 0x846ca68b;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x0100_0000;
        }
    }

    public static float HashRange(int seed, float min, float max) => min + Hash01(seed) * (max - min);

    /// <summary>Smooth 0..1..0 pulse, one full cycle every <paramref name="periodSeconds"/>.</summary>
    public static float Pulse(float time, float periodSeconds, float phase = 0f) =>
        (MathF.Sin((time / periodSeconds + phase) * MathF.PI * 2f) + 1f) * 0.5f;

    /// <summary>
    /// Draws a soft vignette: the screen edges (and corners) fade toward <paramref name="color"/>
    /// while the center stays clear. <paramref name="thicknessFrac"/> is the band width as a
    /// fraction of the shorter screen dimension, so it scales sensibly at any resolution/aspect
    /// ratio instead of being a fixed pixel count.
    /// </summary>
    public static void DrawVignette(ImDrawListPtr dl, Vector2 size, uint color, float thicknessFrac, float alpha)
    {
        if (alpha <= 0f) return;
        float t = MathF.Min(size.X, size.Y) * thicknessFrac;
        if (t <= 0f) return;
        uint edge = WithAlpha(color, alpha);
        const uint clear = 0u;

        dl.AddRectFilledMultiColor(V(0, 0), V(size.X, t), edge, edge, clear, clear);                     // top
        dl.AddRectFilledMultiColor(V(0, size.Y - t), V(size.X, size.Y), clear, clear, edge, edge);       // bottom
        dl.AddRectFilledMultiColor(V(0, 0), V(t, size.Y), edge, clear, clear, edge);                     // left
        dl.AddRectFilledMultiColor(V(size.X - t, 0), V(size.X, size.Y), clear, edge, edge, clear);       // right

        // Deliberately no separate corner fill: the top/bottom bands above span the FULL width
        // (not just the gap between the side bands) and the left/right bands span the FULL height,
        // so every corner is already double-covered by one horizontal + one vertical gradient,
        // and alpha-composites into a naturally darker corner on its own. An earlier version drew
        // an extra flat, fully-opaque square in each corner "to be safe" - that's exactly what
        // caused the hard black squares bug: a flat fill on top of an already-correct gradient.
    }

    /// <summary>Text with a soft glow (two layers of offset copies, a wide soft haze plus a tighter bright halo) plus a dark contact shadow - reads clearly against any game background/color.</summary>
    public static void DrawGlowText(ImDrawListPtr dl, Vector2 pos, string text, uint color, float size, float glow = 1f)
    {
        var font = ImGui.GetFont();
        if (glow > 0f)
        {
            uint outerCol = WithAlpha(color, 0.18f * glow);
            float outerOffset = size * 0.22f;
            foreach (var (dx, dy) in GlowOffsets)
                dl.AddText(font, size, V(pos.X + dx * outerOffset, pos.Y + dy * outerOffset), outerCol, text);

            uint innerCol = WithAlpha(color, 0.4f * glow);
            float innerOffset = size * 0.1f;
            foreach (var (dx, dy) in GlowOffsets)
                dl.AddText(font, size, V(pos.X + dx * innerOffset, pos.Y + dy * innerOffset), innerCol, text);
        }

        // The shadow's own alpha is scaled by the text's, not fixed - otherwise a hardcoded shadow
        // strength dominates the text during a fade-in/out (or any other sub-full alpha), and the
        // word reads as a dark smudge instead of its intended color until it's nearly at full alpha.
        float textAlpha01 = ((color >> 24) & 0xFF) / 255f;
        dl.AddText(font, size, V(pos.X + 1f, pos.Y + 1f), WithAlpha(0xFF000000, 0.6f * textAlpha01), text);
        dl.AddText(font, size, pos, color, text);
    }

    private static readonly (float dx, float dy)[] GlowOffsets =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1), (0.7f, 0.7f), (-0.7f, 0.7f), (0.7f, -0.7f), (-0.7f, -0.7f),
    };

    /// <summary>
    /// Draws a closed jagged loop that follows the screen's border, inset by <paramref name="inset"/>
    /// and perturbed by up to <paramref name="jaggedness"/> pixels per sample point. Currently used
    /// by Petrification's cracks; kept here as a reusable primitive for any future effect that wants
    /// a jagged border loop without building its own perimeter walk.
    /// </summary>
    public static void AddJaggedRectLoop(ImDrawListPtr dl, Vector2 size, float inset, float jaggedness, int seedBase, uint color, float thickness)
    {
        Span<Vector2> pts = stackalloc Vector2[PerimeterPointCount];
        BuildJaggedPerimeter(pts, size, inset, jaggedness, seedBase);
        for (int i = 0; i < pts.Length; i++)
            dl.AddLine(pts[i], pts[(i + 1) % pts.Length], color, thickness);
    }

    private const int PerimeterPointCount = 48;

    private static void BuildJaggedPerimeter(Span<Vector2> pts, Vector2 size, float inset, float jaggedness, int seedBase)
    {
        // Walk the inset rectangle's perimeter at even arc-length steps, then nudge each point
        // inward/outward along its local normal by a per-point hash, so the loop reads as jagged
        // lightning/cracks rather than a smooth rounded rectangle.
        float w = MathF.Max(1f, size.X - inset * 2f);
        float h = MathF.Max(1f, size.Y - inset * 2f);
        float perim = 2f * (w + h);

        for (int i = 0; i < pts.Length; i++)
        {
            float d = perim * i / pts.Length;
            Vector2 basePos, normal;

            if (d < w) { basePos = V(inset + d, inset); normal = V(0, -1); }
            else if (d < w + h) { basePos = V(inset + w, inset + (d - w)); normal = V(1, 0); }
            else if (d < 2 * w + h) { basePos = V(inset + w - (d - w - h), inset + h); normal = V(0, 1); }
            else { basePos = V(inset, inset + h - (d - 2 * w - h)); normal = V(-1, 0); }

            float n = HashRange(seedBase + i, -1f, 1f) * jaggedness;
            pts[i] = basePos + normal * n;
        }
    }
}
