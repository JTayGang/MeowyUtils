namespace RealDebuffs.Effects.Framework;

/// How well each patch of a host's surface holds liquid (rule 9): smooth lattice noise on the surface's own coordinates, so a patch stays with the surface as it moves.
internal static class CoatAdhesion
{
    /// How far a patch's adhesion tips the film's levelling, in the host's groove units (a rope groove is 1): liquid drains from poor patches into good ones.
    const float PoolSwing = 1.6f;

    /// The levelling potential for adhesion a (0..1): the worst and best patches swing it by +-PoolSwing.
    internal static float Potential(float a) => PoolSwing * Math.Clamp((a - 0.5f) * 2.4f, -1f, 1f);

    /// How much of the liquid arriving here the surface refuses, for adhesion a: the worst third takes little.
    internal static float Slick(float a) => Smooth((0.42f - a) / 0.22f);

    /// <summary>The adhesion, 0..1, of every cell (row-major, rows x v.Length). u: each row's distance along the surface, ascending; patchU: pixels per lattice cell along it.
    /// v: each column's place across, in lattice cells; wrap: the lattice's period across (0 = it does not wrap, and v must be 0 or more). Each lattice column is hashed once.</summary>
    internal static void Field(int seed, ReadOnlySpan<float> u, float patchU, ReadOnlySpan<float> v, int wrap, Span<float> field)
    {
        int cols = v.Length;
        float vmax = 0f;
        for (int k = 0; k < cols; k++) vmax = Max(vmax, v[k]);
        int nj = wrap > 0 ? wrap : (int)vmax + 2;
        Span<float> lat = stackalloc float[2 * nj], fv = stackalloc float[cols];  // lattice values at column i (0..nj-1) and i + 1 (nj..2nj-1)
        for (int k = 0; k < cols; k++) fv[k] = Smooth(v[k] - (int)v[k]);
        int cached = int.MinValue;
        for (int r = 0; r < u.Length; r++)
        {
            float x = u[r] / patchU;
            int i = (int)Floor(x);
            if (i != cached)
            {
                if (i == cached + 1) for (int j = 0; j < nj; j++) lat[j] = lat[nj + j];
                else for (int j = 0; j < nj; j++) lat[j] = Hash01(seed * 3 + 211 + i * 73 + j * 1013);
                for (int j = 0; j < nj; j++) lat[nj + j] = Hash01(seed * 3 + 211 + (i + 1) * 73 + j * 1013);
                cached = i;
            }
            float fu = Smooth(x - i);
            for (int k = 0; k < cols; k++)
            {
                int j = (int)v[k], j1 = wrap > 0 ? (j + 1) % wrap : j + 1;
                field[r * cols + k] = Lerp(Lerp(lat[j], lat[nj + j], fu), Lerp(lat[j1], lat[nj + j1], fu), fv[k]);
            }
        }
    }
}
