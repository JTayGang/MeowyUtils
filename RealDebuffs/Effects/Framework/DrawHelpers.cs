using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>Colour packing, the per-primitive colour override, easing, hashing and the shared vignette.</summary>
internal static class DrawHelpers
{
    /// <summary>Opaque packed colour. Ignores the colour override; WithAlpha is where that applies.</summary>
    public static uint Pack(float r, float g, float b)
        => 0xFF000000u | (Q(b) << 16) | (Q(g) << 8) | Q(r);

    // Signed conversion: float -> uint is several times slower on x64. NaN lands on 0.
    private static uint Q(float v) => (uint)(int)((v > 0f ? (v < 1f ? v : 1f) : 0f) * 255f + 0.5f);

    // ---- colour override ----

    private readonly struct HueOverride
    {
        public readonly float Hue, Saturation, ValuePower, ValueScale;

        public HueOverride(float hue, float saturation, float valuePower = 1f, float valueScale = 1f)
        {
            Hue = hue; Saturation = saturation; ValuePower = valuePower; ValueScale = valueScale;
        }

        public bool SameAs(in HueOverride o) =>
            Hue == o.Hue && Saturation == o.Saturation && ValuePower == o.ValuePower && ValueScale == o.ValueScale;
    }

    private static readonly Stack<HueOverride?> ColorOverrideStack = new();
    private static HueOverride? _colorOverride;

    // Memo for ApplyColorOverride (HSV round trip + Pow per vertex): direct-mapped, keyed on the override's id, not "the current push".
    private const int OverrideCacheBits = 11;
    private static readonly uint[] OverrideIn  = new uint[1 << OverrideCacheBits];
    private static readonly uint[] OverrideOut = new uint[1 << OverrideCacheBits];
    private static readonly int[]  OverrideTag = new int[1 << OverrideCacheBits];
    private static readonly HueOverride[] KnownOverrides = new HueOverride[15];
    private static int _knownCount;
    private static int _overrideId;

    private static void SetOverride(HueOverride? next)
    {
        _colorOverride = next;
        if (next is not { } ov) { _overrideId = 0; return; }

        for (int i = 0; i < _knownCount; i++)
        {
            if (KnownOverrides[i].SameAs(ov)) { _overrideId = i + 1; return; }
        }

        if (_knownCount == KnownOverrides.Length)
        {
            _knownCount = 0;
            Array.Clear(OverrideTag);
        }
        KnownOverrides[_knownCount] = ov;
        _overrideId = ++_knownCount;
    }

    /// <summary>Until the matching Pop, WithAlpha re-hues colours toward rgb (chromatic: hue replaced, saturation kept; achromatic: hue stripped).</summary>
    public static void PushColorOverride(Vector4? rgb)
    {
        ColorOverrideStack.Push(_colorOverride);

        if (rgb is not { } c)
        {
            SetOverride(null);
            return;
        }

        var (h, s, v) = RgbToHsv(c.X, c.Y, c.Z);

        if (s >= 0.05f)
            SetOverride(new HueOverride(h, s));
        else if (v >= 0.72f)
            SetOverride(new HueOverride(h, s, valuePower: 0.35f));                    // white / silver
        else if (v <= 0.20f)
            SetOverride(new HueOverride(h, s, valuePower: 4.0f, valueScale: 0.55f));  // black
        else
            SetOverride(new HueOverride(h, s));                                       // grey
    }

    public static void PopColorOverride() =>
        SetOverride(ColorOverrideStack.Count > 0 ? ColorOverrideStack.Pop() : null);

    private static uint ApplyColorOverride(uint color)
    {
        if (_colorOverride is not { } ov) return color;

        int id = _overrideId;
        int slot = (int)(unchecked((color ^ (uint)(id * 0x9E3779B1)) * 2654435761u) >> (32 - OverrideCacheBits));
        if (OverrideTag[slot] == id && OverrideIn[slot] == color) return OverrideOut[slot];

        uint result = ComputeColorOverride(color, ov);
        OverrideIn[slot] = color;
        OverrideOut[slot] = result;
        OverrideTag[slot] = id;
        return result;
    }

    private static uint ComputeColorOverride(uint color, in HueOverride ov)
    {
        float r = (color & 0xFF) / 255f;
        float g = ((color >> 8) & 0xFF) / 255f;
        float b = ((color >> 16) & 0xFF) / 255f;
        uint a = (color >> 24) & 0xFF;

        var (_, origS, origV) = RgbToHsv(r, g, b);

        float s, hue;
        if (ov.Saturation < 0.05f)      { hue = 0f;     s = 0f; }
        else if (origS < 0.05f)         { hue = ov.Hue; s = ov.Saturation; }
        else                            { hue = ov.Hue; s = origS; }

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

    /// <summary>True while an override is pushed; lets a mesh material skip WithAlpha's per-vertex remap.</summary>
    public static bool ColorOverrideActive => _colorOverride.HasValue;

    /// <summary>Saturation of the active override if chromatic, else 0; muted palettes use it so "red" comes out as red as asked.</summary>
    public static float ColorOverrideChroma =>
        _colorOverride is { } ov && ov.Saturation >= 0.05f ? ov.Saturation : 0f;

    /// <summary>The colour with HSV saturation set to <paramref name="s"/>; hue and value unchanged.</summary>
    public static Vector3 WithSaturation(Vector3 c, float s)
    {
        float mx = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        float mn = MathF.Min(c.X, MathF.Min(c.Y, c.Z));
        float have = mx > 1e-4f ? (mx - mn) / mx : 0f;
        if (have < 1e-3f) return c;                       // grey has no hue to saturate

        float k = Math.Clamp(s, 0f, 1f) / have;
        return new Vector3(mx - (mx - c.X) * k, mx - (mx - c.Y) * k, mx - (mx - c.Z) * k);
    }

    /// <summary>Applies the colour override, then scales alpha by <paramref name="mul"/> (0..1).</summary>
    public static uint WithAlpha(uint color, float mul)
    {
        color = ApplyColorOverride(color);
        mul = mul > 0f ? (mul < 1f ? mul : 1f) : 0f;
        uint a = (color >> 24) & 0xFF;
        a = (uint)(int)(a * mul);
        return (color & 0x00FFFFFF) | (a << 24);
    }

    /// <summary>Opaque-RGB vertex colour from a lit colour: the override-aware WithAlpha, or a plain pack when no override is active.</summary>
    public static uint VertexColor(Vector3 c, float alpha, uint alphaBits, bool overridden)
    {
        uint rgb = Pack(c.X, c.Y, c.Z);
        return overridden ? WithAlpha(rgb, alpha) : (rgb & 0x00FFFFFFu) | alphaBits;
    }

    /// <summary>Packed RGB with alpha: override-aware when overridden, else a plain alpha byte.</summary>
    public static uint Tint(uint rgb, float alpha, bool overridden) =>
        overridden ? WithAlpha(rgb, alpha) : (rgb & 0x00FFFFFFu) | AlphaBits(alpha);

    /// <summary>The alpha byte of <paramref name="alpha"/> (0..1), shifted into place for VertexColor.</summary>
    public static uint AlphaBits(float alpha) =>
        (uint)(int)(255f * (alpha > 0f ? (alpha < 1f ? alpha : 1f) : 0f)) << 24;

    // ---- maths ----

    public static uint LerpColor(uint a, uint b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        byte Channel(int shift) => (byte)(((a >> shift) & 0xFF) + (((b >> shift) & 0xFF) - (int)((a >> shift) & 0xFF)) * t);
        return Channel(0) | ((uint)Channel(8) << 8) | ((uint)Channel(16) << 16) | ((uint)Channel(24) << 24);
    }

    public static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Clamped smoothstep of t.</summary>
    public static float Smooth(float t)
    {
        t = t < 0f ? 0f : (t > 1f ? 1f : t);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Smoothstep of x between two edges (edges may be reversed).</summary>
    public static float Smooth(float edge0, float edge1, float x) => Smooth((x - edge0) / (edge1 - edge0));

    public static float EaseOutCubic(float t)
    {
        float inv = 1f - Math.Clamp(t, 0f, 1f);
        return 1f - inv * inv * inv;
    }

    /// <summary>Particle size over its life: riseFrom to 1 by riseEnd, hold until fallStart, then shrink to at least floor.</summary>
    public static float LifeSize(float age, float riseEnd, float riseFrom, float fallStart, float floor) =>
        age < riseEnd ? riseFrom + (1f - riseFrom) * (age / riseEnd)
        : age < fallStart ? 1f
        : MathF.Max(floor, 1f - (age - fallStart) / (1f - fallStart));

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

    /// <summary>Pixel scale for a screen: short side over 1080, clamped to a sane range.</summary>
    public static float PixelScale(Vector2 size) =>
        size.X > 0f ? Math.Clamp(MathF.Min(size.X, size.Y) / 1080f, 0.75f, 2.4f) : 1f;

    /// <summary>Soft edge vignette. Fades to the same colour at zero alpha: ImGui blends non-premultiplied, so fading to transparent black would grey it.</summary>
    public static void DrawVignette(ImDrawListPtr dl, Vector2 size, uint color, float thicknessFrac, float alpha)
    {
        if (alpha <= 0f) return;
        float t = MathF.Min(size.X, size.Y) * thicknessFrac;
        if (t <= 0f) return;
        uint edge = WithAlpha(color, alpha);
        const uint clear = 0u;

        dl.AddRectFilledMultiColor(new(0, 0), new(size.X, t), edge, edge, clear, clear);
        dl.AddRectFilledMultiColor(new(0, size.Y - t), new(size.X, size.Y), clear, clear, edge, edge);
        dl.AddRectFilledMultiColor(new(0, 0), new(t, size.Y), edge, clear, clear, edge);
        dl.AddRectFilledMultiColor(new(size.X - t, 0), new(size.X, size.Y), clear, edge, edge, clear);
    }
}
