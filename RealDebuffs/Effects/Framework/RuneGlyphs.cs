using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>A small alphabet of angular runes as polylines in a [-1, 1] box (+Y down, "up" is -Y). Hand-drawn so no two read as the same shape; pick one with Pick so a rune keeps its letter for life.</summary>
internal static class RuneGlyphs
{
    /// <summary>Every glyph there is. Glyphs 0..LegacyCount-1 are general sigil script, the last few of those sealed-mouth and muted marks that suit Silence best; from FirstInfernal on is the demonic set.</summary>
    public static int Count => All.Length;

    /// <summary>First glyph of the Silence-flavoured set (stitched seam, crossed circle, closed eye).</summary>
    public const int FirstSilence = 14;

    /// <summary>How many glyphs the general script spans. <see cref="Pick(int)"/> draws from exactly these, however many sets are added after them, so an existing effect never changes its letters.</summary>
    public const int LegacyCount = 18;

    /// <summary>First glyph of the infernal set: occult marks (a leviathan cross, a horned sigil, an inverted cross, an eye in a triangle, a ram-horn spiral, a serpent, a trident, a skull, an ouroboros, a spade, hellfire, a sealed star).</summary>
    public const int FirstInfernal = 18;

    /// <summary>How many glyphs the infernal set has.</summary>
    public const int InfernalCount = 13;

    /// <summary>The strokes of glyph <paramref name="index"/> (wrapped into range), each a polyline.</summary>
    public static Vector2[][] Strokes(int index) => All[(int)((uint)index % (uint)All.Length)];

    /// <summary>Stable glyph for a seed: the same seed always gives the same letter. Draws from the general script only (see <see cref="LegacyCount"/>).</summary>
    public static int Pick(int seed) => Pick(seed, 0, LegacyCount);

    /// <summary>Stable glyph for a seed from the <paramref name="count"/> glyphs starting at <paramref name="first"/> (a themed set such as the infernal one).</summary>
    public static int Pick(int seed, int first, int count) => first + (int)(DrawHelpers.Hash01(seed * 31 + 7) * count) % count;

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

    /// <summary>A figure eight (lemniscate of Bernoulli) centred at (cx, cy), half-width a.</summary>
    private static Vector2[] Eight(float cx, float cy, float a, int segs)
    {
        var pts = new Vector2[segs + 1];
        for (int i = 0; i <= segs; i++)
        {
            float t = MathF.Tau * i / segs;
            float sn = MathF.Sin(t), d = 1f + sn * sn;
            pts[i] = new Vector2(cx + a * MathF.Cos(t) / d, cy + a * sn * MathF.Cos(t) / d);
        }
        return pts;
    }

    /// <summary>An outward spiral from the centre (cx, cy), turns turns, ending at radius r1.</summary>
    private static Vector2[] Spiral(float cx, float cy, float r0, float r1, float turns, float a0, int segs)
    {
        var pts = new Vector2[segs + 1];
        for (int i = 0; i <= segs; i++)
        {
            float k = i / (float)segs;
            float a = a0 + MathF.Tau * turns * k, r = r0 + (r1 - r0) * k;
            pts[i] = new Vector2(cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r);
        }
        return pts;
    }

    /// <summary>A star polygon {n/k} inscribed in radius r, one point straight down (inverted), as one closed polyline.</summary>
    private static Vector2[] Star(float cx, float cy, float r, int n, int k)
    {
        var pts = new Vector2[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float a = MathF.PI * 0.5f + MathF.Tau * ((i * k) % n) / n;
            pts[i] = new Vector2(cx + MathF.Cos(a) * r, cy + MathF.Sin(a) * r);
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

        // ---- Infernal set ----
        // 18 leviathan cross: a double cross over an infinity
        new[] { L(0f, -1f, 0f, 0.46f), L(-0.42f, -0.72f, 0.42f, -0.72f), L(-0.64f, -0.30f, 0.64f, -0.30f), Eight(0f, 0.74f, 0.66f, 20) },
        // 19 the horned one: an inverted triangle, horned, with a single eye
        new[] { L(-0.74f, -0.34f, 0.74f, -0.34f, 0f, 0.96f, -0.74f, -0.34f), L(-0.74f, -0.34f, -0.96f, -0.96f), L(0.74f, -0.34f, 0.96f, -0.96f), L(-0.22f, -0.02f, 0f, 0.1f, 0.22f, -0.02f, 0f, -0.14f, -0.22f, -0.02f) },
        // 20 inverted cross
        new[] { L(0f, -1f, 0f, 1f), L(-0.55f, 0.34f, 0.55f, 0.34f) },
        // 21 eye in a triangle
        new[] { L(0f, -0.96f, 0.88f, 0.8f, -0.88f, 0.8f, 0f, -0.96f), L(-0.46f, 0.24f, 0f, -0.02f, 0.46f, 0.24f, 0f, 0.5f, -0.46f, 0.24f), L(0f, 0.14f, 0f, 0.34f) },
        // 22 ram-horn spiral
        new[] { Spiral(0f, 0f, 0.06f, 0.92f, 1.55f, 0.6f, 22) },
        // 23 serpent: an S with a forked tongue
        new[] { L(0.5f, -0.78f, 0.1f, -0.98f, -0.4f, -0.78f, -0.4f, -0.4f, 0.4f, -0.04f, 0.5f, 0.4f, 0.2f, 0.8f, -0.4f, 0.96f), L(0.5f, -0.78f, 0.66f, -0.9f), L(0.5f, -0.78f, 0.7f, -0.7f) },
        // 24 trident
        new[] { L(0f, -0.96f, 0f, 1f), L(-0.6f, -0.96f, -0.6f, -0.22f), L(0.6f, -0.96f, 0.6f, -0.22f), Arc(0f, -0.22f, 0.6f, 0.56f, 0f, MathF.PI, 8), L(-0.8f, -0.72f, -0.6f, -0.96f, -0.4f, -0.72f), L(0.4f, -0.72f, 0.6f, -0.96f, 0.8f, -0.72f), L(-0.2f, -0.74f, 0f, -0.96f, 0.2f, -0.74f) },
        // 25 skull
        new[] { Arc(0f, -0.2f, 0.74f, 0.72f, MathF.PI, MathF.Tau, 12), L(-0.74f, -0.2f, -0.6f, 0.34f, -0.34f, 0.46f, -0.34f, 0.94f, 0.34f, 0.94f, 0.34f, 0.46f, 0.6f, 0.34f, 0.74f, -0.2f), L(-0.46f, -0.28f, -0.14f, -0.28f, -0.3f, 0.06f, -0.46f, -0.28f), L(0.46f, -0.28f, 0.14f, -0.28f, 0.3f, 0.06f, 0.46f, -0.28f), L(0f, 0.12f, -0.08f, 0.32f, 0.08f, 0.32f, 0f, 0.12f), L(-0.17f, 0.66f, -0.17f, 0.94f), L(0f, 0.62f, 0f, 0.94f), L(0.17f, 0.66f, 0.17f, 0.94f) },
        // 26 ouroboros: a ring that has begun to eat itself
        new[] { Arc(0f, 0f, 0.8f, 0.8f, 0.3f, 5.9f, 20), L(0.9f, 0.4f, 0.76f, 0.26f, 0.98f, 0.18f), L(0f, -0.06f, 0f, 0.06f) },
        // 27 spade: a barbed tail-tip on its stem
        new[] { L(0f, -0.96f, 0.52f, -0.3f, 0f, 0.14f, -0.52f, -0.3f, 0f, -0.96f), L(0f, 0.14f, 0f, 0.4f, 0.22f, 0.7f, 0f, 0.96f) },
        // 28 hellfire: three flames
        new[] { L(-0.6f, 0.96f, -0.82f, 0.4f, -0.6f, 0.0f, -0.38f, 0.4f, -0.6f, 0.96f), L(0f, 0.96f, -0.26f, 0.2f, 0f, -0.96f, 0.26f, 0.2f, 0f, 0.96f), L(0.6f, 0.96f, 0.38f, 0.4f, 0.6f, 0.0f, 0.82f, 0.4f, 0.6f, 0.96f) },
        // 29 the sealed star: a pentagram in its circle
        new[] { Arc(0f, 0f, 0.92f, 0.92f, 0f, MathF.Tau, 18), Star(0f, 0f, 0.78f, 5, 2) },
        // 30 the claw: three barbed strokes torn from one point
        new[] { L(-0.7f, -0.9f, -0.1f, 0.0f, -0.5f, 0.95f), L(0f, -0.96f, 0.14f, 0.1f, -0.06f, 0.96f), L(0.7f, -0.9f, 0.34f, 0.0f, 0.4f, 0.95f) },
    };
}
