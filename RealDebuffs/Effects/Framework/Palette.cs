using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>A colour ramp baked to a 256-entry opaque table; index with 0..1 (WithAlpha applies the override).</summary>
internal sealed class Palette
{
    private const int N = 256;
    private readonly uint[] _lut = new uint[N];
    private readonly uint[] _lutTint = new uint[N];  // the same ramp, lifted off grey (see IceColor.Tintable)

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
            _lut[i] = DrawHelpers.Pack(a.R + (b.R - a.R) * k, a.G + (b.G - a.G) * k, a.B + (b.B - a.B) * k);
            _lutTint[i] = IceColor.Tintable(_lut[i]);
        }
    }

    /// <summary>Opaque colour at a 0..1 position (NaN = 0); lifted off grey under a colour override so it recolours smoothly.</summary>
    public uint Sample(float t)
    {
        t = t > 0f ? (t < 1f ? t : 1f) : 0f;
        int i = (int)(t * (N - 1) + 0.5f);
        return DrawHelpers.ColorOverrideActive ? _lutTint[i] : _lut[i];
    }
}

/// <summary>Black-body fire ramp: 0 = dull ember red, 1 = near-white.</summary>
internal static class FireColor
{
    public static readonly Palette Flame = new(
        (0.00f, 0.30f, 0.040f, 0.010f),  // dull ember
        (0.16f, 0.70f, 0.110f, 0.020f),  // deep red
        (0.36f, 0.96f, 0.290f, 0.040f),  // red-orange
        (0.56f, 1.00f, 0.520f, 0.090f),  // orange
        (0.76f, 1.00f, 0.760f, 0.260f),  // yellow-orange
        (0.91f, 1.00f, 0.920f, 0.620f),  // pale yellow
        (1.00f, 1.00f, 0.985f, 0.880f));  // near-white
}

/// <summary>Cold palettes and colour helpers: the ice counterpart of FireColor.</summary>
internal static class IceColor
{
    /// <summary>A colour override saturates near-grey colours (&lt; ~0.05), which would split pale ramps into steps.</summary>
    public static uint Tintable(uint c)
    {
        uint r = c & 255, g = (c >> 8) & 255, b = (c >> 16) & 255;
        uint max = Math.Max(r, Math.Max(g, b));
        uint cap = (uint)(max * 0.84f);  // saturation >= 0.16
        // Pull down the smallest channel (red for frost, whose tint is blue) if it is above the cap.
        if (r <= g && r <= b) { if (r > cap) r = cap; }
        else if (g <= b)      { if (g > cap) g = cap; }
        else                  { if (b > cap) b = cap; }
        return (c & 0xFF000000u) | r | (g << 8) | (b << 16);
    }

    /// <summary>Override pulled toward white for white-by-nature things: keeps hue but stays pastel.</summary>
    public static Vector4? Pastel(Vector4? c, float keep = 0.45f)
    {
        if (c is not { } v) return null;
        var rgb = Vector4.Lerp(Vector4.One, v, keep);
        return new Vector4(rgb.X, rgb.Y, rgb.Z, v.W);
    }

    /// <summary>DrawHelpers.Pack for a colour that may be near-white: lifted off grey while a colour override is active.</summary>
    public static uint Pack(float r, float g, float b)
    {
        uint c = DrawHelpers.Pack(r, g, b);
        return DrawHelpers.ColorOverrideActive ? Tintable(c) : c;
    }

    public static readonly uint Cold  = DrawHelpers.Pack(0.68f, 0.84f, 1.00f);  // pale blue glow
    public static readonly uint Core  = DrawHelpers.Pack(0.98f, 1.00f, 1.00f);  // near-white (greyscale under a colour override)
    public const uint White = 0xFFFFFFFFu;

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

/// <summary>Curse magic: plum shadow through violet and orchid to hot pink and a pale pink-white core.</summary>
internal static class CurseColor
{
    /// <summary>Light, dim to hot: 0 = deep violet, 0.55 = orchid, 0.8 = hot pink, 1 = pale pink-white.</summary>
    public static readonly Palette Glow = new(
        (0.00f, 0.20f, 0.030f, 0.38f),
        (0.30f, 0.50f, 0.100f, 0.78f),
        (0.55f, 0.82f, 0.240f, 0.92f),
        (0.78f, 1.00f, 0.480f, 0.86f),
        (1.00f, 1.00f, 0.880f, 0.97f));

    /// <summary>Edge tint for the screen's vignette: very dark plum.</summary>
    public static readonly uint Vignette = DrawHelpers.Pack(0.030f, 0.008f, 0.052f);

    /// <summary>The white-hot flash a curse lands with (pink-tinged, so it stays chromatic under a colour override).</summary>
    public static readonly uint Flash = DrawHelpers.Pack(0.95f, 0.62f, 1.00f);
}

/// <summary>Haunting: ashen pallor, cold slate through bone to a faintly warm white.</summary>
internal static class HauntColor
{
    /// <summary>Light, dim to hot: 0 = cold slate, 0.3 = grey teal, 0.55 = verdigris, 0.78 = bone, 1 = pale warm white.</summary>
    public static readonly Palette Glow = new(
        (0.00f, 0.060f, 0.085f, 0.115f),
        (0.30f, 0.200f, 0.290f, 0.340f),
        (0.55f, 0.520f, 0.660f, 0.640f),
        (0.78f, 0.840f, 0.900f, 0.840f),
        (1.00f, 0.985f, 0.990f, 0.940f));

    /// <summary>Edge tint for the screen's vignette: ink with a cold cast.</summary>
    public static readonly uint Vignette = DrawHelpers.Pack(0.010f, 0.014f, 0.020f);

    /// <summary>The cold flash a haunting lands with (pale cyan-bone, so it stays chromatic under a colour override).</summary>
    public static readonly uint Flash = DrawHelpers.Pack(0.78f, 0.96f, 0.90f);
}

/// <summary>Electricity: rust to burnt orange, amber and a pale yellow-white core (the vanilla paralysis spark).</summary>
internal static class StormColor
{
    /// <summary>Light, dim to hot: 0 = deep rust, 0.3 = burnt orange, 0.55 = vivid orange, 0.78 = amber, 1 = pale yellow-white.</summary>
    public static readonly Palette Glow = new(
        (0.00f, 0.34f, 0.080f, 0.010f),
        (0.30f, 0.78f, 0.300f, 0.020f),
        (0.55f, 1.00f, 0.560f, 0.070f),
        (0.78f, 1.00f, 0.800f, 0.200f),
        (1.00f, 1.00f, 0.975f, 0.760f));

    /// <summary>Edge tint for the screen's vignette: very dark umber.</summary>
    public static readonly uint Vignette = DrawHelpers.Pack(0.030f, 0.014f, 0.002f);

    /// <summary>The white-hot flash a strike lands with (yellow-tinged, so it stays chromatic under a colour override).</summary>
    public static readonly uint Flash = DrawHelpers.Pack(1.00f, 0.96f, 0.80f);
}

/// <summary>Hellfire and blood: dried-blood black through crimson and arterial red to ember orange and a pale gold core.</summary>
internal static class HellColor
{
    /// <summary>Dim to hot: 0 dried-blood black, 0.25 crimson, 0.5 blood red, 0.72 red-orange, 0.88 orange, 1 pale gold.</summary>
    public static readonly Palette Glow = new(
        (0.00f, 0.170f, 0.008f, 0.010f),
        (0.25f, 0.520f, 0.024f, 0.020f),
        (0.50f, 0.920f, 0.095f, 0.026f),
        (0.72f, 1.000f, 0.300f, 0.046f),
        (0.88f, 1.000f, 0.560f, 0.130f),
        (1.00f, 1.000f, 0.900f, 0.600f));

    /// <summary>A flame's temperature ramp: smouldering maroon at the cool edge to pale gold-white at the heart.</summary>
    public static readonly Palette Flame = new(
        (0.00f, 0.140f, 0.006f, 0.008f),
        (0.16f, 0.440f, 0.020f, 0.014f),
        (0.38f, 0.860f, 0.100f, 0.020f),
        (0.60f, 1.000f, 0.300f, 0.040f),
        (0.80f, 1.000f, 0.560f, 0.120f),
        (1.00f, 1.000f, 0.880f, 0.520f));

    /// <summary>Edge tint for the screen's vignette: near-black blood.</summary>
    public static readonly uint Vignette = DrawHelpers.Pack(0.022f, 0.003f, 0.004f);

    /// <summary>The flash an infernal cast lands with (orange-red, so it stays chromatic under a colour override).</summary>
    public static readonly uint Flash = DrawHelpers.Pack(1.00f, 0.36f, 0.12f);
}

/// <summary>Charm: rose and blush light, a pearl core, and pink-lit fog from plum shadow to peach highlight.</summary>
internal static class CharmColor
{
    /// <summary>Light, dim to hot: 0 = deep plum, 0.3 = raspberry, 0.55 = rose, 0.78 = blush, 1 = pearl.</summary>
    public static readonly Palette Glow = new(
        (0.00f, 0.300f, 0.050f, 0.240f),
        (0.30f, 0.740f, 0.200f, 0.500f),
        (0.55f, 1.000f, 0.420f, 0.660f),
        (0.78f, 1.000f, 0.700f, 0.800f),
        (1.00f, 1.000f, 0.940f, 0.950f));

    /// <summary>Fog lit by that light: 0 = shadowed mauve, 0.4 = rose, 0.75 = lit pink, 1 = peach-pearl.</summary>
    public static readonly Palette Fog = new(
        (0.00f, 0.380f, 0.090f, 0.340f),
        (0.40f, 0.920f, 0.380f, 0.620f),
        (0.75f, 1.000f, 0.640f, 0.790f),
        (1.00f, 1.000f, 0.800f, 0.850f));

    /// <summary>Edge tint for the screen's vignette: deep rose-plum.</summary>
    public static readonly uint Vignette = DrawHelpers.Pack(0.360f, 0.050f, 0.240f);

    /// <summary>The flush a charm lands with (rose-white, so it stays chromatic under a colour override).</summary>
    public static readonly uint Flash = DrawHelpers.Pack(1.000f, 0.700f, 0.860f);
}
