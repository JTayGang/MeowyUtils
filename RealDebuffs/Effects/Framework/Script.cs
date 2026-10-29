using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>A single-stroke cursive alphabet: lowercase, a little punctuation and a heart (♥). Units: x-height 1, baseline 0, +Y down.</summary>
internal static class ScriptFont
{
    /// <summary>Ascender to descender, in x-heights.</summary>
    public const float Height = 2.7f;

    /// <summary>Space between letters, in x-heights.</summary>
    public const float Tracking = 0.14f;

    /// <summary>One letter, flattened: per stroke the points, the outward normals, a width factor (broad-nib pen, tapered ends) and the radius of the curve at each point.</summary>
    public sealed class Glyph
    {
        public float Advance;
        public Vector2[][] Points = Array.Empty<Vector2[]>();
        public Vector2[][] Normals = Array.Empty<Vector2[]>();
        public float[][] Width = Array.Empty<float[]>();
        public float[][] Room = Array.Empty<float[]>();
    }

    // The bowl shared by a, d, g and q.
    private static readonly float[] Bowl = S(0.90f, -0.80f, 0.62f, -1.00f, 0.27f, -0.92f, 0.05f, -0.60f, 0.10f, -0.22f, 0.40f, 0.00f, 0.72f, -0.14f, 0.92f, -0.50f);
    // The bowl shared by b and p.
    private static readonly float[] Belly = S(0.05f, -0.58f, 0.34f, -0.98f, 0.74f, -0.88f, 0.96f, -0.52f, 0.80f, -0.14f, 0.42f, 0.00f, 0.06f, -0.20f);

    private static readonly Glyph Blank = new() { Advance = 0.55f };
    private static readonly Glyph[] Table = Build();

    public static Glyph Of(char c) => c == '\u2665' ? Table[1] : c > 32 && c < 127 ? Table[c] : Blank;

    public static float Width(string s)
    {
        float w = 0f;
        foreach (char c in s) w += Of(c).Advance + Tracking;
        return MathF.Max(0f, w - Tracking);
    }

    private static float[] S(params float[] xy) => xy;

    private static Glyph[] Build()
    {
        var t = new Glyph[128];
        void G(char c, float adv, params float[][] strokes) => t[c] = Make(adv, strokes);

        G('a', 1.22f, Bowl, S(0.96f, -1.02f, 0.94f, -0.55f, 0.95f, -0.14f, 1.14f, 0.00f));
        G('b', 1.15f, S(0.06f, -1.84f, 0.05f, -0.90f, 0.05f, 0.00f), Belly);
        G('c', 1.00f, S(0.90f, -0.82f, 0.58f, -1.00f, 0.22f, -0.86f, 0.04f, -0.50f, 0.22f, -0.14f, 0.58f, 0.00f, 0.90f, -0.20f));
        G('d', 1.22f, Bowl, S(0.97f, -1.84f, 0.95f, -0.90f, 0.94f, -0.14f, 1.14f, 0.00f));
        G('e', 1.05f, S(0.06f, -0.52f, 0.50f, -0.58f, 0.88f, -0.64f, 0.80f, -0.90f, 0.50f, -1.00f, 0.16f, -0.82f, 0.04f, -0.48f, 0.18f, -0.15f, 0.52f, 0.00f, 0.88f, -0.18f));
        G('f', 0.70f, S(0.84f, -1.72f, 0.56f, -1.88f, 0.32f, -1.62f, 0.24f, -1.00f, 0.20f, -0.20f, 0.06f, 0.52f, -0.14f, 0.74f), S(-0.08f, -1.02f, 0.62f, -1.04f));
        G('g', 1.22f, Bowl, S(0.96f, -1.02f, 0.95f, -0.10f, 0.86f, 0.46f, 0.52f, 0.80f, 0.12f, 0.70f));
        G('h', 1.15f, S(0.06f, -1.84f, 0.05f, -0.90f, 0.05f, 0.00f), S(0.05f, -0.54f, 0.34f, -0.94f, 0.72f, -0.94f, 0.90f, -0.52f, 0.90f, 0.00f));
        G('i', 0.52f, S(0.12f, -1.02f, 0.12f, -0.14f, 0.30f, 0.00f), S(0.16f, -1.48f, 0.18f, -1.43f));
        G('j', 0.55f, S(0.28f, -1.02f, 0.28f, 0.30f, 0.14f, 0.72f, -0.14f, 0.78f), S(0.32f, -1.48f, 0.34f, -1.43f));
        G('k', 1.05f, S(0.06f, -1.84f, 0.05f, 0.00f), S(0.82f, -1.02f, 0.50f, -0.76f, 0.06f, -0.40f), S(0.28f, -0.58f, 0.58f, -0.30f, 0.88f, 0.00f));
        G('l', 0.56f, S(0.04f, -1.40f, 0.18f, -1.80f, 0.34f, -1.86f, 0.34f, -1.55f, 0.16f, -0.90f, 0.10f, -0.20f, 0.30f, 0.00f));
        G('m', 1.58f, S(0.05f, -1.02f, 0.05f, 0.00f), S(0.05f, -0.56f, 0.28f, -0.96f, 0.58f, -0.94f, 0.70f, -0.50f, 0.70f, 0.00f), S(0.70f, -0.56f, 0.94f, -0.96f, 1.24f, -0.94f, 1.36f, -0.50f, 1.36f, 0.00f));
        G('n', 1.12f, S(0.05f, -1.02f, 0.05f, 0.00f), S(0.05f, -0.56f, 0.30f, -0.96f, 0.68f, -0.96f, 0.86f, -0.52f, 0.86f, 0.00f));
        G('o', 1.10f, Closed(0.50f, -1.00f, 0.14f, -0.86f, 0.02f, -0.50f, 0.14f, -0.14f, 0.50f, 0.00f, 0.86f, -0.14f, 0.98f, -0.50f, 0.86f, -0.86f));
        G('p', 1.15f, S(0.06f, -1.02f, 0.05f, 0.00f, 0.05f, 0.84f), Belly);
        G('q', 1.22f, Bowl, S(0.96f, -1.02f, 0.96f, 0.80f, 1.14f, 0.90f));
        G('r', 0.82f, S(0.05f, -1.02f, 0.05f, 0.00f), S(0.05f, -0.56f, 0.28f, -0.94f, 0.60f, -1.02f, 0.78f, -0.88f));
        G('s', 0.95f, S(0.80f, -0.94f, 0.52f, -1.02f, 0.22f, -0.90f, 0.18f, -0.64f, 0.50f, -0.50f, 0.82f, -0.34f, 0.80f, -0.10f, 0.46f, 0.02f, 0.10f, -0.10f));
        G('t', 0.78f, S(0.26f, -1.55f, 0.22f, -0.32f, 0.40f, 0.00f, 0.64f, -0.10f), S(-0.02f, -1.02f, 0.62f, -1.04f));
        G('u', 1.15f, S(0.05f, -1.02f, 0.06f, -0.30f, 0.30f, 0.00f, 0.62f, -0.10f, 0.88f, -0.52f), S(0.92f, -1.02f, 0.92f, -0.10f, 1.10f, 0.00f));
        G('v', 0.95f, S(0.00f, -1.00f, 0.22f, -0.42f, 0.42f, 0.00f, 0.66f, -0.50f, 0.88f, -1.02f));
        G('w', 1.28f, S(0.00f, -1.00f, 0.20f, -0.40f, 0.36f, 0.00f, 0.56f, -0.52f, 0.76f, 0.00f, 0.94f, -0.40f, 1.12f, -1.02f));
        G('x', 0.98f, S(0.00f, -1.00f, 0.45f, -0.50f, 0.92f, 0.00f), S(0.92f, -1.00f, 0.45f, -0.50f, 0.00f, 0.00f));
        G('y', 1.18f, S(0.05f, -1.02f, 0.08f, -0.30f, 0.32f, 0.00f, 0.62f, -0.10f, 0.90f, -0.52f), S(0.94f, -1.02f, 0.94f, 0.10f, 0.80f, 0.56f, 0.46f, 0.82f, 0.10f, 0.72f));
        G('z', 0.98f, S(0.06f, -0.98f, 0.50f, -1.02f, 0.86f, -1.00f, 0.50f, -0.50f, 0.06f, -0.04f, 0.50f, 0.00f, 0.90f, -0.04f));
        G('\'', 0.34f, S(0.10f, -1.84f, 0.02f, -1.44f));
        G(',', 0.38f, S(0.10f, -0.06f, 0.12f, 0.12f, 0.00f, 0.38f));
        G('.', 0.34f, S(0.10f, -0.04f, 0.12f, 0.00f));
        G('!', 0.36f, S(0.10f, -1.84f, 0.10f, -0.60f), S(0.10f, -0.06f, 0.11f, 0.00f));
        G('?', 0.90f, S(0.02f, -1.45f, 0.22f, -1.84f, 0.64f, -1.80f, 0.78f, -1.44f, 0.54f, -1.06f, 0.32f, -0.70f, 0.30f, -0.46f), S(0.30f, -0.06f, 0.31f, 0.00f));
        G('-', 0.60f, S(0.00f, -0.55f, 0.46f, -0.55f));
        // The heart (♥) lives at index 1.
        t[1] = Make(1.10f, new[] { S(0.50f, 0.02f, 0.18f, -0.34f, 0.02f, -0.68f, 0.06f, -0.98f, 0.27f, -1.10f, 0.50f, -0.86f, 0.73f, -1.10f, 0.94f, -0.98f, 0.98f, -0.68f, 0.82f, -0.34f, 0.50f, 0.02f) });

        for (int i = 0; i < t.Length; i++) t[i] ??= Blank;
        return t;
    }

    // A closed loop: marked with a leading NaN and ended on its first point, so the spline wraps without a kink.
    private static float[] Closed(params float[] xy)
    {
        var r = new float[xy.Length + 3];
        r[0] = float.NaN;
        Array.Copy(xy, 0, r, 1, xy.Length);
        r[^2] = xy[0]; r[^1] = xy[1];
        return r;
    }

    private const int Sub = 2;

    private static Glyph Make(float advance, float[][] strokes)
    {
        var g = new Glyph { Advance = advance };
        g.Points = new Vector2[strokes.Length][]; g.Normals = new Vector2[strokes.Length][]; g.Width = new float[strokes.Length][]; g.Room = new float[strokes.Length][];
        for (int s = 0; s < strokes.Length; s++)
        {
            var c = strokes[s];
            bool loop = float.IsNaN(c[0]);
            int off = loop ? 1 : 0, n = (c.Length - off) / 2;
            var ctl = new Vector2[n];
            for (int i = 0; i < n; i++) ctl[i] = new Vector2(c[off + i * 2], c[off + i * 2 + 1]);

            // Catmull-Rom through the control points, Sub steps a segment.
            Vector2 At(int j) => loop ? ctl[(j + (n - 1) * 4) % (n - 1)] : ctl[Math.Clamp(j, 0, n - 1)];
            var pts = new Vector2[(n - 1) * Sub + 1];
            for (int i = 0; i < n - 1; i++)
            {
                Vector2 p0 = At(i - 1), p1 = ctl[i], p2 = ctl[i + 1], p3 = At(i + 2);
                for (int k = 0; k < Sub; k++)
                {
                    float u = k / (float)Sub, u2 = u * u, u3 = u2 * u;
                    pts[i * Sub + k] = 0.5f * (2f * p1 + (p2 - p0) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u2 + (-p0 + 3f * p1 - 3f * p2 + p3) * u3);
                }
            }
            pts[^1] = ctl[n - 1];

            int m = pts.Length;
            var nrm = new Vector2[m]; var wid = new float[m]; var arc = new float[m]; var room = new float[m];
            for (int i = 1; i < m; i++) arc[i] = arc[i - 1] + Vector2.Distance(pts[i - 1], pts[i]);
            for (int i = 0; i < m; i++)
            {
                Vector2 d = pts[Math.Min(m - 1, i + 1)] - pts[Math.Max(0, i - 1)];
                float l = d.Length();
                Vector2 tan = l > 1e-5f ? d / l : Vector2.UnitX;
                nrm[i] = new Vector2(-tan.Y, tan.X);

                // A broad nib held up and to the right: down-strokes are full, rising hairlines thin.
                float cross = MathF.Abs(tan.X * -0.643f - tan.Y * 0.766f);
                float u = arc[m - 1] > 1e-4f ? arc[i] / arc[m - 1] : 0.5f;
                wid[i] = (0.42f + 0.58f * cross) * (0.55f + 0.45f * DrawHelpers.Smooth(MathF.Min(u, 1f - u) / 0.22f));
            }
            // How far either side of the stroke can be offset before the offset folds over itself.
            for (int i = 0; i < m; i++)
            {
                room[i] = 99f;
                if (i == 0 || i == m - 1) continue;
                Vector2 a = pts[i] - pts[i - 1], b = pts[i + 1] - pts[i];
                float la = a.Length(), lb = b.Length();
                if (la < 1e-5f || lb < 1e-5f) continue;
                float turn = MathF.Acos(Math.Clamp(Vector2.Dot(a, b) / (la * lb), -1f, 1f));
                if (turn > 1e-3f) room[i] = 0.5f * (la + lb) / turn;
            }
            g.Points[s] = pts; g.Normals[s] = nrm; g.Width[s] = wid; g.Room[s] = room;
        }
        return g;
    }
}
