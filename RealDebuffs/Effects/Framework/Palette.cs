using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>A colour ramp baked to a 256-entry opaque table; index with 0..1 (WithAlpha applies the override).</summary>
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

/// <summary>Black-body fire ramp: 0 = dull ember red, 1 = near-white. Baked once into a 256-entry table of opaque colours.</summary>
internal static class FireColor
{
    private static readonly float[] StopT = { 0.00f, 0.16f, 0.36f, 0.56f, 0.76f, 0.91f, 1.00f };

    private static readonly Vector3[] StopC =
    {
        new(0.30f, 0.040f, 0.010f),   // dull ember
        new(0.70f, 0.110f, 0.020f),   // deep red
        new(0.96f, 0.290f, 0.040f),   // red-orange
        new(1.00f, 0.520f, 0.090f),   // orange
        new(1.00f, 0.760f, 0.260f),   // yellow-orange
        new(1.00f, 0.920f, 0.620f),   // pale yellow
        new(1.00f, 0.985f, 0.880f),   // near-white
    };

    private const int N = 256;
    private static readonly uint[] Lut = Build();

    public static uint Heat(float t)
    {
        t = t > 0f ? (t < 1f ? t : 1f) : 0f;      // NaN lands on 0
        return Lut[(int)(t * (N - 1) + 0.5f)];
    }

    private static uint[] Build()
    {
        var lut = new uint[N];
        for (int i = 0; i < N; i++)
        {
            float t = i / (float)(N - 1);
            int s = 0;
            while (s < StopT.Length - 2 && t > StopT[s + 1]) s++;
            float k = (t - StopT[s]) / (StopT[s + 1] - StopT[s]);
            var c = Vector3.Lerp(StopC[s], StopC[s + 1], Math.Clamp(k, 0f, 1f));
            lut[i] = DrawHelpers.Pack(c.X, c.Y, c.Z);
        }
        return lut;
    }
}

/// <summary>Cold palettes and colour helpers: the ice counterpart of FireColor.</summary>
internal static class IceColor
{
    /// <summary>The override fully saturates near-grey colours (< ~0.05), splitting pale-blue-to-white ramps into steps; this lifts near-white to saturation 0.16.</summary>
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

    /// <summary>Override pulled toward white for white-by-nature things: keeps hue but stays pastel (orange snow would read as embers).</summary>
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

    public static readonly uint Cold  = DrawHelpers.Pack(0.68f, 0.84f, 1.00f);   // pale blue glow
    public static readonly uint Core  = DrawHelpers.Pack(0.98f, 1.00f, 1.00f);   // near-white (greyscale under a colour override)
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
