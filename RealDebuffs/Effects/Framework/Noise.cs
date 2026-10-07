namespace RealDebuffs.Effects.Framework;

/// <summary>Fractal helpers over FireNoise.Value. Stateless and allocation-free.</summary>
internal static class Noise
{
    /// <summary>
    /// Fractal sum of <paramref name="octaves"/> value-noise layers, normalized to roughly [-1, 1]. The
    /// sampling domain is rotated for every octave: value noise creases along its integer lattice, and
    /// octaves that all share one orientation stack those creases into a visible grid.
    /// </summary>
    public static float Fbm(float x, float y, int octaves)
    {
        float sum = 0f, amp = 1f, norm = 0f;
        for (int i = 0; i < octaves; i++)
        {
            float rx = 0.8f * x - 0.6f * y;                 // 3-4-5 rotation: about 37 degrees
            float ry = 0.6f * x + 0.8f * y;
            sum += amp * FireNoise.Value(rx, ry);
            norm += amp;
            x = rx * 2.03f + 11.7f;
            y = ry * 2.03f - 7.3f;
            amp *= 0.5f;
        }
        return sum / norm;
    }
}
