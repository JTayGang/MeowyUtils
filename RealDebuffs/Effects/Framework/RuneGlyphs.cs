using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>A small alphabet of angular runes as polylines in a [-1, 1] box (+Y down, "up" is -Y). Hand-drawn so no two read as the same shape; pick one with Pick so a rune keeps its letter for life.</summary>
internal static class RuneGlyphs
{
    /// <summary>Glyphs 0..SilenceCount-1 are general sigil script; the last few are sealed-mouth and muted marks that suit Silence best.</summary>
    public static int Count => All.Length;

    /// <summary>First glyph of the Silence-flavoured set (stitched seam, crossed circle, closed eye).</summary>
    public const int FirstSilence = 14;

    /// <summary>The strokes of glyph <paramref name="index"/> (wrapped into range), each a polyline.</summary>
    public static Vector2[][] Strokes(int index) => All[(int)((uint)index % (uint)All.Length)];

    /// <summary>Stable glyph for a seed: the same seed always gives the same letter.</summary>
    public static int Pick(int seed) => (int)(DrawHelpers.Hash01(seed * 31 + 7) * All.Length) % All.Length;

    private static Vector2[] L(params float[] xy)
    {
        var pts = new Vector2[xy.Length / 2];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Vector2(xy[i * 2], xy[i * 2 + 1]);
        return pts;
    }

    private static Vector2[] Arc(float cx, float cy, float rx, float ry, float a0, float a1, int segs)
    {
        var pts = new Vector2[segs + 1];
        for (int i = 0; i <= segs; i++)
        {
            float a = a0 + (a1 - a0) * i / segs;
            pts[i] = new Vector2(cx + MathF.Cos(a) * rx, cy + MathF.Sin(a) * ry);
        }
        return pts;
    }

    private static readonly Vector2[][][] All =
    {
        // 0  trident-fork: a staff splitting into two arms
        new[] { L(0f, 1f, 0f, -1f), L(0f, -0.05f, -0.7f, -0.8f), L(0f, -0.05f, 0.7f, -0.8f) },
        // 1  horned: a staff with a triangle bowed off it
        new[] { L(-0.3f, 1f, -0.3f, -1f), L(-0.3f, -0.65f, 0.55f, -0.15f, -0.3f, 0.35f) },
        // 2  two barbs
        new[] { L(-0.3f, 1f, -0.3f, -1f), L(-0.3f, -0.9f, 0.6f, -0.3f), L(-0.3f, -0.15f, 0.6f, 0.45f) },
        // 3  open wedge with a seed
        new[] { L(0.5f, -0.75f, -0.45f, 0f, 0.5f, 0.75f), L(0.62f, -0.02f, 0.62f, 0.02f) },
        // 4  cross
        new[] { L(-0.7f, -0.8f, 0.7f, 0.8f), L(0.7f, -0.8f, -0.7f, 0.8f) },
        // 5  diamond on legs
        new[] { L(0f, -0.95f, 0.6f, -0.4f, 0f, 0.15f, -0.6f, -0.4f, 0f, -0.95f), L(0f, 0.15f, 0.6f, 0.95f), L(0f, 0.15f, -0.6f, 0.95f) },
        // 6  bowtie
        new[] { L(-0.7f, -0.8f, 0.7f, 0.8f, 0.7f, -0.8f, -0.7f, 0.8f, -0.7f, -0.8f) },
        // 7  staff with notches
        new[] { L(0f, -1f, 0f, 1f), L(-0.3f, -0.55f, 0.3f, -0.55f), L(-0.3f, 0.55f, 0.3f, 0.55f) },
        // 8  lightning
        new[] { L(0.5f, -1f, -0.4f, -0.25f, 0.4f, 0.25f, -0.5f, 1f) },
        // 9  hooked staff
        new[] { L(0f, -1f, 0f, 1f), L(0f, -1f, 0.55f, -0.5f), L(0f, 1f, -0.55f, 0.5f) },
        // 10 double bow
        new[] { L(-0.35f, -1f, -0.35f, 1f), L(-0.35f, -1f, 0.45f, -0.5f, -0.35f, 0f), L(-0.35f, 0f, 0.5f, 0.5f, -0.35f, 1f) },
        // 11 ringed slash
        new[] { Arc(0f, 0f, 0.62f, 0.62f, 0f, MathF.Tau, 14), L(-0.45f, 0.75f, 0.45f, -0.75f) },
        // 12 eye
        new[] { L(-0.85f, 0f, 0f, -0.5f, 0.85f, 0f, 0f, 0.5f, -0.85f, 0f), L(0f, -0.08f, 0f, 0.08f) },
        // 13 horned crescent
        new[] { Arc(0f, 0.1f, 0.6f, 0.6f, 0.15f * MathF.PI, 0.85f * MathF.PI, 8), L(-0.5f, -0.1f, -0.7f, -0.8f), L(0.5f, -0.1f, 0.7f, -0.8f) },

        // ---- Silence set ----
        // 14 stitched seam: a sealed mouth
        new[] { L(-0.85f, 0f, 0.85f, 0f), L(-0.6f, -0.32f, -0.6f, 0.32f), L(-0.3f, -0.32f, -0.3f, 0.32f), L(0f, -0.32f, 0f, 0.32f), L(0.3f, -0.32f, 0.3f, 0.32f), L(0.6f, -0.32f, 0.6f, 0.32f) },
        // 15 muted: a ring struck through, with a stilled wave inside
        new[] { Arc(0f, 0f, 0.8f, 0.8f, 0f, MathF.Tau, 16), L(-0.56f, -0.56f, 0.56f, 0.56f), L(-0.4f, 0.12f, 0.4f, 0.12f) },
        // 16 closed eye: a lid with lashes
        new[] { Arc(0f, -0.35f, 0.85f, 0.7f, 0.15f * MathF.PI, 0.85f * MathF.PI, 8), L(-0.55f, 0.3f, -0.65f, 0.7f), L(0f, 0.38f, 0f, 0.85f), L(0.55f, 0.3f, 0.65f, 0.7f) },
        // 17 hush: a bar over a falling mark
        new[] { L(-0.8f, -0.55f, 0.8f, -0.55f), L(0f, -0.55f, 0f, 0.9f), L(-0.35f, 0.35f, 0f, 0.9f, 0.35f, 0.35f) },
    };
}
