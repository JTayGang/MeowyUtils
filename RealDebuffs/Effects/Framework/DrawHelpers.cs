using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

internal static class DrawHelpers
{
    public static Vector2 V(float x, float y) => new(x, y);

    // ToU32 is BAKING, not painting - it's how materials build their authored palettes once. It
    // deliberately does NOT apply PushColorOverride; if it did, whichever activation happened to
    // draw first would recolor the material's palette permanently. WithAlpha is the choke point
    // instead, because every material calls it fresh on every color every frame.
    public static uint ToU32(float r, float g, float b, float a) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));

    private readonly struct HueOverride
    {
        public readonly float Hue;
        public readonly float Saturation;
        public HueOverride(float hue, float saturation) { Hue = hue; Saturation = saturation; }
    }

    private static readonly Stack<HueOverride?> ColorOverrideStack = new();
    private static HueOverride? _colorOverride;

    /// <summary>
    /// From now until the matching PopColorOverride, every color that passes through WithAlpha is
    /// re-hued toward rgb. EffectManager no longer wraps the Draw call in push/pop - the
    /// EffectSceneRenderer pushes per-primitive using that primitive's own ColorOverride.
    /// </summary>
    public static void PushColorOverride(Vector4? rgb)
    {
        ColorOverrideStack.Push(_colorOverride);
        if (rgb is { } c)
        {
            var (h, s, _) = RgbToHsv(c.X, c.Y, c.Z);
            _colorOverride = new HueOverride(h, s);
        }
        else
        {
            _colorOverride = null;
        }
    }

    public static void PopColorOverride() =>
        _colorOverride = ColorOverrideStack.Count > 0 ? ColorOverrideStack.Pop() : null;

    /// <summary>
    /// Re-hues the active override. Only HUE is replaced - Saturation and Value are preserved
    /// exactly as authored. This is what keeps a palette's light/dark AND muted/vivid structure
    /// intact under any override: a near-white hot core stays pale (its authored saturation is
    /// low), a saturated mid-tone stays saturated, a dark soot stays dark. Replacing saturation
    /// as well would collapse every tone to the same vividness and leave only a brightness
    /// gradient, which destroys exactly the "pale core, saturated body, dark edge" reading that
    /// makes a fire look like fire.
    ///
    /// The one case where a purely achromatic source tone (saturation near 0, like a gray or a
    /// pure white) would be invisible under a hue-only shift is handled by falling back to the
    /// override's saturation for those. That keeps genuinely grayscale elements (which have no
    /// hue to shift) recolored by whatever tint the user chose rather than ignoring it.
    /// </summary>
    private static uint ApplyColorOverride(uint color)
    {
        if (_colorOverride is not { } ov) return color;

        float r = (color & 0xFF) / 255f;
        float g = ((color >> 8) & 0xFF) / 255f;
        float b = ((color >> 16) & 0xFF) / 255f;
        uint a = (color >> 24) & 0xFF;

        var (_, origS, origV) = RgbToHsv(r, g, b);

        // Preserve authored saturation whenever there is one; fall back to the override's for
        // genuinely achromatic tones so they don't stay gray after being asked to go red.
        float s = origS < 0.05f ? ov.Saturation : origS;

        var (nr, ng, nb) = HsvToRgb(ov.Hue, s, origV);

        uint R = (uint)Math.Clamp((int)MathF.Round(nr * 255f), 0, 255);
        uint G = (uint)Math.Clamp((int)MathF.Round(ng * 255f), 0, 255);
        uint B = (uint)Math.Clamp((int)MathF.Round(nb * 255f), 0, 255);
        return R | (G << 8) | (B << 16) | (a << 24);
    }

    private static (float H, float S, float V) RgbToHsv(float r, float g, float b)
    {
        float max = MathF.Max(r, MathF.Max(g, b));
        float min = MathF.Min(r, MathF.Min(g, b));
        float delta = max - min;

        float h = 0f;
        if (delta > 1e-6f)
        {
            if (max == r) h = ((g - b) / delta) % 6f;
            else if (max == g) h = (b - r) / delta + 2f;
            else h = (r - g) / delta + 4f;
            h *= 60f;
            if (h < 0f) h += 360f;
        }

        float s = max <= 1e-6f ? 0f : delta / max;
        return (h, s, max);
    }

    private static (float R, float G, float B) HsvToRgb(float h, float s, float v)
    {
        h = ((h % 360f) + 360f) % 360f;
        float c = v * s;
        float x = c * (1f - MathF.Abs((h / 60f) % 2f - 1f));
        float m = v - c;

        (float r1, float g1, float b1) = h switch
        {
            < 60f => (c, x, 0f),
            < 120f => (x, c, 0f),
            < 180f => (0f, c, x),
            < 240f => (0f, x, c),
            < 300f => (x, 0f, c),
            _ => (c, 0f, x),
        };
        return (r1 + m, g1 + m, b1 + m);
    }

    public static uint WithAlpha(uint color, float mul)
    {
        color = ApplyColorOverride(color);
        mul = Math.Clamp(mul, 0f, 1f);
        uint a = (color >> 24) & 0xFF;
        a = (uint)(a * mul);
        return (color & 0x00FFFFFF) | (a << 24);
    }

    public static uint LerpColor(uint a, uint b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        byte Channel(int shift) => (byte)(((a >> shift) & 0xFF) + (((b >> shift) & 0xFF) - (int)((a >> shift) & 0xFF)) * t);
        return Channel(0) | ((uint)Channel(8) << 8) | ((uint)Channel(16) << 16) | ((uint)Channel(24) << 24);
    }

    public static float EaseOutCubic(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float inv = 1f - t;
        return 1f - inv * inv * inv;
    }

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

    public static float Pulse(float time, float periodSeconds, float phase = 0f) =>
        (MathF.Sin((time / periodSeconds + phase) * MathF.PI * 2f) + 1f) * 0.5f;

    /// <summary>
    /// Draws a soft vignette. The EffectSceneRenderer calls this once per frame for the winning
    /// vignette request; materials should never call it directly (regions handle their own fills).
    /// Fades to the same color at zero alpha - ImGui blends without premultiplying, so fading to
    /// transparent black would grey it.
    /// </summary>
    public static void DrawVignette(ImDrawListPtr dl, Vector2 size, uint color, float thicknessFrac, float alpha)
    {
        if (alpha <= 0f) return;
        float t = MathF.Min(size.X, size.Y) * thicknessFrac;
        if (t <= 0f) return;
        uint edge = WithAlpha(color, alpha);
        const uint clear = 0u;

        dl.AddRectFilledMultiColor(V(0, 0), V(size.X, t), edge, edge, clear, clear);
        dl.AddRectFilledMultiColor(V(0, size.Y - t), V(size.X, size.Y), clear, clear, edge, edge);
        dl.AddRectFilledMultiColor(V(0, 0), V(t, size.Y), edge, clear, clear, edge);
        dl.AddRectFilledMultiColor(V(size.X - t, 0), V(size.X, size.Y), clear, edge, edge, clear);

        // No separate corner fill: the top/bottom bands span the FULL width, the left/right bands
        // span the FULL height, so every corner is double-covered and alpha-composites darker on
        // its own. A flat corner fill on top of that gradient was the hard-black-squares bug.
    }

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

        float textAlpha01 = ((color >> 24) & 0xFF) / 255f;
        dl.AddText(font, size, V(pos.X + 1f, pos.Y + 1f), WithAlpha(0xFF000000, 0.6f * textAlpha01), text);
        dl.AddText(font, size, pos, color, text);
    }

    private static readonly (float dx, float dy)[] GlowOffsets =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1), (0.7f, 0.7f), (-0.7f, 0.7f), (0.7f, -0.7f), (-0.7f, -0.7f),
    };

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