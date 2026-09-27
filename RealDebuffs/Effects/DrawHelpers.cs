using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

internal static class DrawHelpers
{
    public static Vector2 V(float x, float y) => new(x, y);

    // Baking, not painting: ToU32 is how every effect builds its OWN authored palette, and every
    // effect that has one builds it exactly once - either a `static readonly uint` field (most
    // effects) or an instance field set the first time Draw runs via a `EnsurePalette()`-style
    // guard (Paralysis, Electrocution, Heavy). Either way the result is cached and reused for the
    // effect's whole lifetime, so ToU32 deliberately does NOT apply PushColorOverride below - if it
    // did, whichever activation happened to be on screen the FIRST time a lazily-initialized
    // effect drew would recolor that effect's base palette PERMANENTLY, including for every later,
    // non-overridden activation (a real debuff, say) for the rest of the session. WithAlpha is the
    // choke point instead, because it's the thing every effect already calls fresh on every single
    // frame to turn its (already-baked) palette into an actual draw color - see its own remarks.
    public static uint ToU32(float r, float g, float b, float a) =>
        ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));

    // ---- ambient recolor context: what lets a keyword-matched custom status recolor a whole
    //      effect (see TooltipKeywordParser) without every one of the 25+ IScreenEffect
    //      implementations needing its own awareness of the idea. Same shape as ImGui's own
    //      Push/PopStyleColor - push a context, draw normally, pop it - which is exactly the
    //      pattern every effect in this file's callers is already steeped in. ----

    /// <summary>A hue+saturation to re-tint every color drawn while active - see <see cref="PushColorOverride"/>.</summary>
    private readonly struct HueOverride
    {
        public readonly float Hue;        // degrees, 0..360
        public readonly float Saturation; // 0..1
        public HueOverride(float hue, float saturation) { Hue = hue; Saturation = saturation; }
    }

    private static readonly Stack<HueOverride?> ColorOverrideStack = new();
    private static HueOverride? _colorOverride;

    /// <summary>
    /// From now until the matching <see cref="PopColorOverride"/>, every color that passes through
    /// <see cref="WithAlpha"/> is re-hued toward <paramref name="rgb"/> - see <see cref="ApplyColorOverride"/>
    /// for exactly what "re-hued" means and why it's built that way. Pass null to push "no
    /// override" (so a caller that always wants to push/pop in pairs doesn't need to branch on
    /// whether this particular activation actually has a color to apply). EffectManager is the only
    /// caller today, always as a push-call-Draw-pop triple around one effect at a time, but this is
    /// a real stack (matching ImGui's own PushStyleColor/PopStyleColor) rather than one flat field,
    /// so nesting is safe if a future caller ever needs it.
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

    /// <summary>Restores whatever override (or lack of one) was active before the matching <see cref="PushColorOverride"/>.</summary>
    public static void PopColorOverride() => _colorOverride = ColorOverrideStack.Count > 0 ? ColorOverrideStack.Pop() : null;

    /// <summary>
    /// Re-hues <paramref name="color"/> toward the active override, if any: Hue and Saturation are
    /// replaced outright, but Value (brightness) - and, obviously, whatever alpha the caller applies
    /// separately - is always kept exactly as authored. This is deliberate, not a simplification:
    /// every effect's palette encodes its whole look as light/dark relationships between named
    /// tones (a near-black "void" shadow, a bright "hot core" highlight, a mid-tone "body"), and
    /// preserving each tone's Value is what keeps that shape intact under any override - the void
    /// stays dark, the hot core stays pale (since a pale tone's low original saturation means a
    /// forced hue barely shows against it - it's mathematically still "mostly white/gray, tinted"),
    /// and only the mid-tones read as clearly, definitely the new color. That's what makes ONE
    /// override work for BurnsEffect's white-to-soot fire ramp exactly as well as BindEffect's
    /// violet-magic palette without either effect needing to know what its own colors mean.
    /// A fully achromatic source color (pure black/white/gray, Saturation already 0) is unaffected
    /// either way - there's no hue to override in the first place.
    /// </summary>
    private static uint ApplyColorOverride(uint color)
    {
        if (_colorOverride is not { } ov) return color;

        float r = (color & 0xFF) / 255f;
        float g = ((color >> 8) & 0xFF) / 255f;
        float b = ((color >> 16) & 0xFF) / 255f;
        uint a = (color >> 24) & 0xFF;

        float v = RgbToHsv(r, g, b).V;
        var (nr, ng, nb) = HsvToRgb(ov.Hue, ov.Saturation, v);

        uint R = (uint)Math.Clamp((int)MathF.Round(nr * 255f), 0, 255);
        uint G = (uint)Math.Clamp((int)MathF.Round(ng * 255f), 0, 255);
        uint B = (uint)Math.Clamp((int)MathF.Round(nb * 255f), 0, 255);
        return R | (G << 8) | (B << 16) | (a << 24);
    }

    /// <summary>Standard RGB-&gt;HSV, each channel 0..1 in, Hue 0..360/Sat 0..1/Val 0..1 out. Self-contained (no ImGui call) so this file has no dependency on exactly which color-space helpers this project's ImGui binding happens to expose.</summary>
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

    /// <summary>Standard HSV-&gt;RGB, inverse of <see cref="RgbToHsv"/>.</summary>
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

    /// <summary>Returns <paramref name="color"/> with its alpha channel multiplied by <paramref name="mul"/> (0..1). Also where any active <see cref="PushColorOverride"/> is applied - see <see cref="ApplyColorOverride"/> - since every effect already calls this fresh on every single color on every single frame, making it the one place that's guaranteed to see (and can safely re-tint) absolutely everything drawn, with zero changes needed anywhere else in this folder.</summary>
    public static uint WithAlpha(uint color, float mul)
    {
        color = ApplyColorOverride(color);
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
