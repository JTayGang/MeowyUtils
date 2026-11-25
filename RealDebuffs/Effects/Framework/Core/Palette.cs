namespace RealDebuffs.Effects.Framework;

/// A colour ramp baked to a 256-entry opaque table; index with 0..1 (WithAlpha applies the override).
internal sealed class Palette
{
    const int N = 256;
    readonly uint[] _lut = new uint[N];
    readonly uint[] _lutTint = new uint[N];  // the same ramp, lifted off grey (see IceColor.Tintable)

    public Palette(params (float T, float R, float G, float B)[] stops)
    {
        for (int i = 0; i < N; i++)
        {
            float t = i / (float)(N - 1);
            int s = 0;
            while (s < stops.Length - 2 && t > stops[s + 1].T) s++;
            var a = stops[s];
            var b = stops[s + 1];
            float k = Saturate((t - a.T) / (b.T - a.T));
            _lut[i] = DrawHelpers.Pack(Lerp(a.R, b.R, k), Lerp(a.G, b.G, k), Lerp(a.B, b.B, k));
            _lutTint[i] = IceColor.Tintable(_lut[i]);
        }
    }

    /// Opaque colour at a 0..1 position (NaN = 0); lifted off grey under a colour override so it recolours smoothly.
    public uint Sample(float t)
    {
        t = t > 0f ? (t < 1f ? t : 1f) : 0f;
        int i = (int)(t * (N - 1) + 0.5f);
        return ColorOverrideActive ? _lutTint[i] : _lut[i];
    }

    /// Sample without the override lift, for a colour baked at construction (the override is whatever happens to be pushed then).
    public uint SampleBase(float t)
    {
        t = t > 0f ? (t < 1f ? t : 1f) : 0f;
        return _lut[(int)(t * (N - 1) + 0.5f)];
    }
}

/// Black-body fire ramp: 0 = dull ember red, 1 = near-white.
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

/// Cold palettes and colour helpers: the ice counterpart of FireColor.
internal static class IceColor
{
    /// A colour override saturates near-grey colours (&lt; ~0.05), which would split pale ramps into steps.
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

    /// Override pulled toward white for white-by-nature things: keeps hue but stays pastel.
    public static Vector4? Pastel(Vector4? c, float keep = 0.45f)
    {
        if (c is not { } v) return null;
        var rgb = Vector4.Lerp(Vector4.One, v, keep);
        return new Vector4(rgb.X, rgb.Y, rgb.Z, v.W);
    }

    /// DrawHelpers.Pack for a colour that may be near-white: lifted off grey while a colour override is active.
    public static uint Pack(float r, float g, float b)
    {
        uint c = DrawHelpers.Pack(r, g, b);
        return ColorOverrideActive ? Tintable(c) : c;
    }

    public static readonly uint Cold  = DrawHelpers.Pack(0.68f, 0.84f, 1.00f);  // pale blue glow
    public static readonly uint Core  = DrawHelpers.Pack(0.98f, 1.00f, 1.00f);  // near-white (greyscale under a colour override)
    public const uint White = 0xFFFFFFFFu;

    /// Frost body: 0 = thin bluish film, 1 = dense milky white.
    public static readonly Palette Frost = new(
        (0.00f, 0.42f, 0.60f, 0.82f),
        (0.35f, 0.66f, 0.80f, 0.94f),
        (0.70f, 0.86f, 0.93f, 1.00f),
        (1.00f, 0.97f, 0.99f, 1.00f));

    /// Crystal edges and glints: pale blue to pure white.
    public static readonly Palette Crystal = new(
        (0.00f, 0.60f, 0.78f, 0.96f),
        (0.55f, 0.84f, 0.93f, 1.00f),
        (1.00f, 1.00f, 1.00f, 1.00f));
}

/// Curse magic: plum shadow through violet and orchid to hot pink and a pale pink-white core.
internal static class CurseColor
{
    /// Light, dim to hot: 0 = deep violet, 0.55 = orchid, 0.8 = hot pink, 1 = pale pink-white.
    public static readonly Palette Glow = new(
        (0.00f, 0.20f, 0.030f, 0.38f),
        (0.30f, 0.50f, 0.100f, 0.78f),
        (0.55f, 0.82f, 0.240f, 0.92f),
        (0.78f, 1.00f, 0.480f, 0.86f),
        (1.00f, 1.00f, 0.880f, 0.97f));

    /// Edge tint for the screen's vignette: very dark plum.
    public static readonly uint Vignette = DrawHelpers.Pack(0.030f, 0.008f, 0.052f);

    /// The white-hot flash a curse lands with (pink-tinged, so it stays chromatic under a colour override).
    public static readonly uint Flash = DrawHelpers.Pack(0.95f, 0.62f, 1.00f);
}
