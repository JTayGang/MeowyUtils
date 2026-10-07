using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// A color ramp baked into a 256-entry table of opaque packed colors. Index with 0..1; build once,
/// index forever (no native calls on the per-vertex path). Callers turn the result into a vertex color
/// with DrawHelpers.WithAlpha, which is also where the user's color override is applied.
/// </summary>
internal sealed class Palette
{
    private const int N = 256;
    private readonly uint[] _lut = new uint[N];
    private readonly uint[] _lutTint = new uint[N];      // the same ramp, lifted off grey (see IceColor.Tintable)

    /// <param name="stops">Ascending (t, r, g, b) keyframes, t in 0..1, linearly interpolated.</param>
    public Palette(params (float T, float R, float G, float B)[] stops)
    {
        for (int i = 0; i < N; i++)
        {
            float t = i / (float)(N - 1);
            int s = 0;
            while (s < stops.Length - 2 && t > stops[s + 1].T) s++;
            var a = stops[s];
            var b = stops[s + 1];
            float k = Math.Clamp((t - a.T) / (b.T - a.T), 0f, 1f);
            _lut[i] = FireColor.Pack(a.R + (b.R - a.R) * k, a.G + (b.G - a.G) * k, a.B + (b.B - a.B) * k);
            _lutTint[i] = IceColor.Tintable(_lut[i]);
        }
    }

    /// <summary>
    /// Opaque color for a 0..1 position on the ramp (NaN maps to 0). While a color override is active the
    /// ramp comes back lifted off grey, so the override recolors all of it smoothly.
    /// </summary>
    public uint Sample(float t)
    {
        t = t > 0f ? (t < 1f ? t : 1f) : 0f;
        int i = (int)(t * (N - 1) + 0.5f);
        return DrawHelpers.ColorOverrideActive ? _lutTint[i] : _lut[i];
    }
}

/// <summary>Cold palettes: the ice counterpart of FireColor.</summary>
internal static class IceColor
{
    /// <summary>
    /// The color override keeps a color's own saturation unless the color is nearly grey (under ~0.05),
    /// in which case it swaps in the override's full saturation. Frost lives on that line - its ramps run
    /// from pale blue up to white - so a smooth ramp would split along a hard threshold into pale and
    /// fully saturated halves (stair-stepped blocks, flickering strokes). This lifts a near-white color
    /// just off grey (saturation 0.16) so the whole ramp recolors smoothly, into pastel tints. Use it only
    /// while an override is active; without one, whites should stay white.
    /// </summary>
    public static uint Tintable(uint c)
    {
        uint r = c & 255, g = (c >> 8) & 255, b = (c >> 16) & 255;
        uint max = Math.Max(r, Math.Max(g, b));
        uint cap = (uint)(max * 0.84f);                          // saturation >= 0.16
        // Pull down the smallest channel (red for frost, whose tint is blue) if it is above the cap.
        if (r <= g && r <= b) { if (r > cap) r = cap; }
        else if (g <= b)      { if (g > cap) g = cap; }
        else                  { if (b > cap) b = cap; }
        return (c & 0xFF000000u) | r | (g << 8) | (b << 16);
    }

    /// <summary>
    /// A color override pulled toward white, for things that are white by nature. The remap gives any
    /// near-white color the override's full saturation, which suits a spark but not snow: orange snow looks
    /// like embers. Handing the override a paler color keeps its hue and leaves the result pastel, like the
    /// rest of the frost. White stays white; null stays null.
    /// </summary>
    public static Vector4? Pastel(Vector4? c, float keep = 0.45f)
    {
        if (c is not { } v) return null;
        var rgb = Vector4.Lerp(Vector4.One, v, keep);
        return new Vector4(rgb.X, rgb.Y, rgb.Z, v.W);
    }

    /// <summary>FireColor.Pack for a color that may be near-white: lifted off grey while a color override is active.</summary>
    public static uint Pack(float r, float g, float b)
    {
        uint c = FireColor.Pack(r, g, b);
        return DrawHelpers.ColorOverrideActive ? Tintable(c) : c;
    }

    /// <summary>Frost body: 0 = thin bluish film, 1 = dense milky white.</summary>
    public static readonly Palette Frost = new(
        (0.00f, 0.42f, 0.60f, 0.82f),
        (0.35f, 0.66f, 0.80f, 0.94f),
        (0.70f, 0.86f, 0.93f, 1.00f),
        (1.00f, 0.97f, 0.99f, 1.00f));

    /// <summary>Crystal edges and glints: pale blue to pure white.</summary>
    public static readonly Palette Crystal = new(
        (0.00f, 0.60f, 0.78f, 0.96f),
        (0.55f, 0.84f, 0.93f, 1.00f),
        (1.00f, 1.00f, 1.00f, 1.00f));
}
