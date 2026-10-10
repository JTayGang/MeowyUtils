namespace RealDebuffs.Effects.Framework;

/// <summary>Cheap stateless 2D noise, roughly [-1, 1]. Value is the per-vertex hot path; Perlin is for slow, low-count uses.</summary>
internal static class Noise
{
    public static float Perlin(float x, float y)
    {
        float fx = MathF.Floor(x), fy = MathF.Floor(y);
        int xi = (int)fx, yi = (int)fy;
        float xf = x - fx, yf = y - fy;

        float u = xf * xf * xf * (xf * (xf * 6f - 15f) + 10f);
        float v = yf * yf * yf * (yf * (yf * 6f - 15f) + 10f);

        float n00 = Grad(xi,     yi,     xf,      yf);
        float n10 = Grad(xi + 1, yi,     xf - 1f, yf);
        float n01 = Grad(xi,     yi + 1, xf,      yf - 1f);
        float n11 = Grad(xi + 1, yi + 1, xf - 1f, yf - 1f);

        float nx0 = n00 + u * (n10 - n00);
        float nx1 = n01 + u * (n11 - n01);
        return (nx0 + v * (nx1 - nx0)) * 1.4f;
    }

    // 256x256 lattice of random values in [-1, 1]; sampling is four reads and a few multiplies. Wraps every 256 units.
    private const int TableSize = 256;
    private static readonly float[] Table = BuildTable();

    private static float[] BuildTable()
    {
        var t = new float[TableSize * TableSize];
        for (int i = 0; i < t.Length; i++)
            t[i] = DrawHelpers.Hash01(i * 7 + 12345) * 2f - 1f;
        return t;
    }

    public static float Value(float x, float y)
    {
        float fx = MathF.Floor(x), fy = MathF.Floor(y);
        int xi = (int)fx & (TableSize - 1), yi = (int)fy & (TableSize - 1);
        float xf = x - fx, yf = y - fy;
        float u = xf * xf * xf * (xf * (xf * 6f - 15f) + 10f);
        float v = yf * yf * yf * (yf * (yf * 6f - 15f) + 10f);

        int x1 = (xi + 1) & (TableSize - 1), y1 = (yi + 1) & (TableSize - 1);
        float a = Table[(yi << 8) | xi], b = Table[(yi << 8) | x1];
        float c = Table[(y1 << 8) | xi], d = Table[(y1 << 8) | x1];
        float top = a + u * (b - a), bot = c + u * (d - c);
        return (top + v * (bot - top)) * 1.25f;
    }

    private static float Grad(int ix, int iy, float dx, float dy)
    {
        uint h = unchecked((uint)ix * 0x27d4eb2du ^ (uint)iy * 0x165667b1u);
        h ^= h >> 15; h = unchecked(h * 0x85ebca6bu); h ^= h >> 13;
        switch (h & 7u)
        {
            case 0:  return  dx + dy;
            case 1:  return -dx + dy;
            case 2:  return  dx - dy;
            case 3:  return -dx - dy;
            case 4:  return  dx;
            case 5:  return -dx;
            case 6:  return  dy;
            default: return -dy;
        }
    }

    /// <summary>Fractal sum of value noise, ~[-1, 1]. Each octave's domain is rotated so the lattice creases don't stack into a grid.</summary>
    public static float Fbm(float x, float y, int octaves)
    {
        float sum = 0f, amp = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            float rx = 0.8f * x - 0.6f * y;
            float ry = 0.6f * x + 0.8f * y;
            sum += amp * Value(rx, ry);
            norm += amp;
            x = rx * 2.03f + 11.7f;
            y = ry * 2.03f - 7.3f;
            amp *= 0.5f;
        }
        return sum / norm;
    }
}
