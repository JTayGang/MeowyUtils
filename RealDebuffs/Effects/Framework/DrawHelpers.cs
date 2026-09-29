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

    /// <summary>
    /// A hue/saturation override plus an optional value curve. Hue and Saturation come from the
    /// override color; ValuePower and ValueScale shape the source tone's brightness. Power less
    /// than 1 lifts mid-tones (white/silver), power greater than 1 crushes them (black), and a
    /// scale under 1 caps the ceiling. All defaults are 1, so a grey override - which only strips
    /// hue - falls through untouched.
    /// </summary>
    private readonly struct HueOverride
    {
        public readonly float Hue;
        public readonly float Saturation;
        public readonly float ValuePower;   // 1 = no curve; <1 lifts mid-tones; >1 crushes them
        public readonly float ValueScale;   // 1 = no cap; <1 lowers the ceiling after the curve

        public HueOverride(float hue, float saturation,
                           float valuePower = 1f, float valueScale = 1f)
        {
            Hue = hue;
            Saturation = saturation;
            ValuePower = valuePower;
            ValueScale = valueScale;
        }
    }

    private static readonly Stack<HueOverride?> ColorOverrideStack = new();
    private static HueOverride? _colorOverride;

    /// <summary>
    /// From now until the matching PopColorOverride, every color that passes through WithAlpha is
    /// re-hued toward rgb. EffectSceneRenderer pushes per primitive, using that primitive's own
    /// ColorOverride.
    ///
    /// Chromatic overrides replace hue and preserve the source's own saturation, leaving
    /// brightness alone. Achromatic overrides strip hue and shape the value curve by bucket:
    ///   white / silver - power less than 1, lifting mid-tones and leaving the top end hot.
    ///   grey           - no value change at all; pure hue strip.
    ///   black          - strong power crush plus a scale cap, so bright cores stay readable
    ///                    (darker, but visible) while the rest collapses to near-black.
    /// </summary>
    public static void PushColorOverride(Vector4? rgb)
    {
        ColorOverrideStack.Push(_colorOverride);

        if (rgb is not { } c)
        {
            _colorOverride = null;
            return;
        }

        var (h, s, v) = RgbToHsv(c.X, c.Y, c.Z);

        if (s >= 0.05f)
        {
            // Chromatic override: re-hue only, leave value alone.
            _colorOverride = new HueOverride(h, s);
            return;
        }

        // Achromatic override. White, silver, grey, and black all strip hue - that's what "grey
        // family" means as a tint. But they do different things to brightness, bucketed here by
        // the override's own V so the user gets three distinct looks instead of three identical
        // flat-grey ones. A power curve (rather than a lerp toward a target) is what lets black
        // preserve the top of the range while crushing the rest: the core of a fire stays
        // readable as a dim hot spot, while everything below it collapses into near-black.
        if (v >= 0.72f)
            _colorOverride = new HueOverride(h, s, valuePower: 0.35f);                    // white / silver
        else if (v <= 0.20f)
            _colorOverride = new HueOverride(h, s, valuePower: 4.0f, valueScale: 0.55f);  // black
        else
            _colorOverride = new HueOverride(h, s);                                       // grey
    }

    public static void PopColorOverride() =>
        _colorOverride = ColorOverrideStack.Count > 0 ? ColorOverrideStack.Pop() : null;

    /// <summary>
    /// Re-hues the active override. Hue is replaced, Saturation and Value are preserved from the
    /// source tone - so a palette's vivid/pale/dark structure survives any override: a hot core
    /// stays pale, a saturated body stays saturated, a dark shadow stays dark.
    ///
    /// Two special cases on the saturation axis:
    ///  - Achromatic OVERRIDE (grey / white / black): force the output to be achromatic too,
    ///    preserving only brightness (which the value curve then shapes). Without this, the
    ///    override's hue of 0 (undefined, since there's no real hue) would be treated as red and
    ///    every tone would come out red.
    ///  - Achromatic SOURCE tone with a chromatic override: use the override's saturation. A
    ///    genuinely grey input has no hue of its own, so accepting the override's hue AND its
    ///    saturation is the only way to make it visibly adopt the requested color.
    /// </summary>
    private static uint ApplyColorOverride(uint color)
    {
        if (_colorOverride is not { } ov) return color;

        float r = (color & 0xFF) / 255f;
        float g = ((color >> 8) & 0xFF) / 255f;
        float b = ((color >> 16) & 0xFF) / 255f;
        uint a = (color >> 24) & 0xFF;

        var (_, origS, origV) = RgbToHsv(r, g, b);

        float s;
        float hue;

        if (ov.Saturation < 0.05f)
        {
            // Override is achromatic: flatten everything to grey at the shaped brightness.
            hue = 0f;
            s = 0f;
        }
        else if (origS < 0.05f)
        {
            // Chromatic override on a grey source tone: the source has no hue to preserve, so
            // adopt both the override's hue and its saturation.
            hue = ov.Hue;
            s = ov.Saturation;
        }
        else
        {
            // Normal case: chromatic override, chromatic source. Keep the source's saturation.
            hue = ov.Hue;
            s = origS;
        }

        // Power first (curve the range), then scale (cap the ceiling). Both defaults are 1, so a
        // chromatic override or grey falls through untouched.
        float v = MathF.Pow(origV, ov.ValuePower) * ov.ValueScale;

        var (nr, ng, nb) = HsvToRgb(hue, s, v);

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

    public static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

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
}