using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>Draws a LiquidField (pool, surface, foam, bubbles, streams) shaded by its liquid's table; the pool half is in LiquidDraw.Pool.cs.</summary>
internal static partial class LiquidDraw
{
    private const int Half = 8;  // a stream's cross-section: Half + 1 visible vertices, and a soft-edge vertex each side

    private struct Look
    {
        public Liquid L;
        public float Alpha, Dye, Px, Time, Feather;
        public bool Over;
        public Vector2 Uv;
    }

    private static readonly MeshBuilder Mb = new();

    private static readonly float[] HalfSin = HalfTable(false), HalfCos = HalfTable(true);
    private static float[] HalfTable(bool cos)
    {
        var t = new float[Half + 1];
        for (int c = 0; c <= Half; c++) { float th = MathF.PI * (c / (float)Half - 0.5f); t[c] = cos ? MathF.Cos(th) : MathF.Sin(th); }
        return t;
    }

    private static readonly Vector3 KeyH = Vector3.Normalize(StudioLighting.Key + Vector3.UnitZ);
    private static readonly Vector3 FillH = Vector3.Normalize(StudioLighting.Fill + Vector3.UnitZ);

    public static void Draw(ImDrawListPtr dl, LiquidField f, float alpha, in MaterialContext ctx)
    {
        if (alpha <= 0.01f || f.Columns < 3) return;
        var k = new Look
        {
            L = f.Liquid, Alpha = alpha > 1f ? 1f : alpha, Px = f.Px, Time = f.Time,
            Feather = MathF.Max(0.8f, ctx.ScreenScale * 0.9f),
            Over = DrawHelpers.ColorOverrideActive, Dye = DrawHelpers.ColorOverrideChroma, Uv = MeshDraw.WhiteUv(ctx.Time),
        };
        Sample(f);

        Mb.Clear();
        Body(f, in k);
        Shafts(f, in k);
        Plunges(f, in k);
        Mb.Flush(dl, k.Uv);

        Bubbles(dl, f, in k);
        Motes(dl, f, in k);
        Trails(f, in k);
        for (int i = 0; i < f.Jets.Count; i++) Stream(f.Jets[i], in k);
        Mb.Flush(dl, k.Uv);

        Surface(f, in k);
        Foam(f, in k);
        Sheets(f, in k);
        Mb.Flush(dl, k.Uv);
        FoamBubbles(dl, f, in k);
    }

    private static uint Col(in Look k, Vector3 display, float a)
    {
        if (k.Dye > 0f) display = DrawHelpers.BoostSaturation(display, k.Dye, DrawHelpers.SoftDye);
        return DrawHelpers.Tint(DrawHelpers.Pack(display.X, display.Y, display.Z), a * k.Alpha, k.Over);
    }

    private static Vector3 Lit(Vector3 lin) => Matcap.ToDisplay(lin);

    /// <summary>Wet streaks left on the lens by running beads: a faint ribbon that follows their path and dries from the old end.</summary>
    private static void Trails(LiquidField f, in Look k)
    {
        Vector3 wet = Lit(Vector3.Lerp(k.L.Look.Thin, Vector3.One, 0.35f) * (StudioLighting.FillColor * 1.2f + StudioLighting.KeyColor * 0.4f));
        for (int i = 0; i < f.Trails.Length; i++)
        {
            ref readonly var t = ref f.Trails[i];
            int n = t.Count;
            if (t.Fade <= 0.01f || n < 2) continue;
            float aMax = 0.28f * t.Fade * (0.4f + 0.6f * k.L.Look.Opacity);
            Mb.Reserve(n * 3, (n - 1) * 12);
            int v = Mb.Vertices;
            for (int p = 0; p < n; p++)
            {
                Vector2 at = new(t.Pts[p].X, t.Pts[p].Y);
                Vector2 dv = new(t.Pts[Math.Min(p + 1, n - 1)].X - t.Pts[Math.Max(p - 1, 0)].X, t.Pts[Math.Min(p + 1, n - 1)].Y - t.Pts[Math.Max(p - 1, 0)].Y);
                Vector2 d = dv.LengthSquared() > 1e-4f ? Vector2.Normalize(dv) : Vector2.UnitY;
                Vector2 perp = new(-d.Y, d.X);
                float u = p / (float)(n - 1);
                float w = MathF.Max(t.Pts[p].Z, 0.7f * k.Px) * (0.45f + 0.55f * u);
                Mb.VertexReserved(at - perp * w, Col(in k, wet, 0f));
                Mb.VertexReserved(at, Col(in k, wet, aMax * DrawHelpers.Smooth(u * 1.4f)));
                Mb.VertexReserved(at + perp * w, Col(in k, wet, 0f));
            }
            for (int p = 0; p + 1 < n; p++)
            {
                int a0 = v + p * 3, b0 = a0 + 3;
                Mb.QuadReserved(a0, a0 + 1, b0, b0 + 1);
                Mb.QuadReserved(a0 + 1, a0 + 2, b0 + 1, b0 + 2);
            }
        }
    }

    // streams

    private static void Stream(LiquidJet j, in Look k)
    {
        int rows = j.Rows;
        if (rows < 2) return;
        var lm = k.L.Matcap; var ph = k.L.Phys;
        float px = k.Px, seed = j.Seed;
        int stride = Half + 3;

        Mb.Reserve(rows * (stride + 6), (rows - 1) * ((stride - 1) + 4) * 6);
        int first = Mb.Vertices;
        Vector3 foam = Lit(Vector3.Lerp(new Vector3(0.90f, 0.95f, 0.98f), k.L.Look.Thin * 1.6f, 0.35f + 0.5f * ph.Viscosity) * (StudioLighting.FillColor * 1.4f + StudioLighting.KeyColor * 0.6f));
        Vector3 spark = Lit(StudioLighting.KeyColor * 2.4f);
        Vector3 shade = Lit(k.L.Look.Deep * 0.45f);

        for (int i = 0; i < rows; i++)
        {
            Vector2 p = j.Path[i];
            int i0 = i > 0 ? i - 1 : i, i1 = i < rows - 1 ? i + 1 : i;
            Vector2 dv = j.Path[i1] - j.Path[i0];
            float len = dv.Length();
            Vector2 d = len > 1e-3f ? dv / len : Vector2.UnitY;
            Vector2 perp = new(-d.Y, d.X);
            float rad = MathF.Max(j.Rad[i], 0.45f), born = j.Born[i];
            float slope = len > 1e-3f ? Math.Clamp((j.Rad[i1] - j.Rad[i0]) / len, -3f, 3f) : 0f;
            float age = MathF.Max(k.Time - born, 0f);

            // how rough the water is: fast, thick and churning is ragged and white; slow and thin runs clear
            float turb = j.Churn * ph.Churn * DrawHelpers.Smooth(rad / (3f * px)) * (0.35f + 0.65f * MathF.Min(1f, j.PathV[i].Length() / (900f * px)));
            float gain = ph.Foam * DrawHelpers.Smooth(age / 0.25f) * DrawHelpers.Smooth(rad / (4f * px)) * (0.25f + 0.75f * turb);
            float tail = DrawHelpers.Smooth(j.Rad[i] / (1.3f * px));

            // the two edges are not the same: noise fixed to the parcel, so it stretches as the stream speeds up
            float eL = Math.Clamp(1f + 0.24f * turb * (Noise.Value(born * 7f + seed, 0.37f) + 0.35f * Noise.Value(born * 17f + seed, 2.9f)), 0.6f, 1.5f);
            float eR = Math.Clamp(1f + 0.24f * turb * (Noise.Value(born * 7f + seed + 40f, 1.7f) + 0.35f * Noise.Value(born * 17f + seed + 9f, 4.1f)), 0.6f, 1.5f);
            uint halo = Col(in k, shade, 0.34f * tail * DrawHelpers.Smooth(rad / (3f * px)));  // the dark line where the water bends the scene behind it

            for (int c = 0; c <= Half; c++)
            {
                float sn = HalfSin[c], cs = HalfCos[c];
                Vector2 nxy = perp * sn - d * slope;
                Vector3 nrm = Vector3.Normalize(new Vector3(nxy.X, nxy.Y, cs));
                Vector4 lit = lm.Sample(nrm.X, nrm.Y);
                Vector3 col = new(lit.X, lit.Y, lit.Z);
                float a = lit.W;
                if (gain > 0.01f)
                {
                    // white water: streaks that stretch along the flow, at three scales
                    float w = 0.5f + 0.30f * Noise.Value(sn * 2.1f + seed * 0.37f, born * 4f) + 0.22f * Noise.Value(sn * 4.7f - seed * 0.21f, born * 11f) + 0.16f * Noise.Value(sn * 9.3f + seed, born * 27f);
                    float ae = DrawHelpers.Smooth(0.38f, 0.85f, w) * gain;
                    col = Vector3.Lerp(col, foam, ae * 0.85f);
                    a += (1f - a) * ae * 0.70f;
                    a *= 1f - 0.45f * DrawHelpers.Smooth(0.82f, 0.97f, 0.5f + 0.5f * Noise.Value(sn * 3.3f + seed * 0.6f, born * 19f)) * gain;  // gaps in the froth
                }
                float sp = ph.Foam > 0f ? DrawHelpers.Smooth(0.80f, 0.97f, 0.5f + 0.5f * Noise.Value(sn * 6.5f + seed * 0.3f, born * 33f + 3f)) * MathF.Min(1f, rad / (2.5f * px)) * k.L.Look.Gloss : 0f;
                if (sp > 0.01f) { col = Vector3.Lerp(col, spark, sp * 0.85f); a = MathF.Max(a, 0.55f * sp); }
                Vector2 at = p + perp * (sn * rad * (sn < 0f ? eL : eR));
                Mb.VertexReserved(at, Col(in k, col, a * tail));
            }
            float edge = rad * MathF.Max(eL, eR) + k.Feather;
            Vector2 el = p - perp * edge, er = p + perp * edge;
            Mb.VertexReserved(el, halo);
            Mb.VertexReserved(er, halo);
        }

        for (int i = 0; i + 1 < rows; i++)
        {
            if (j.Born[i] - j.Born[i + 1] > j.GapTime) continue;  // the flow was off here: no water between these rows
            int a = first + i * stride, b = a + stride;
            for (int c = 0; c < Half; c++) Mb.QuadReserved(a + c, a + c + 1, b + c, b + c + 1);
            Mb.QuadReserved(a + Half + 1, a, b + Half + 1, b);  // left skirt
            Mb.QuadReserved(a + Half, a + Half + 2, b + Half, b + Half + 2);  // right skirt
        }

        Highlights(j, in k);
    }

    /// <summary>Hard highlight lines (key and fill) that per-vertex shading cannot hold; they break up where the water is rough.</summary>
    private static void Highlights(LiquidJet j, in Look k)
    {
        int rows = j.Rows;
        float px = k.Px;
        Vector3 white = Lit(StudioLighting.KeyColor * 2.2f);
        Vector3 cool = Lit(new Vector3(0.78f, 0.92f, 1f) * 1.2f);
        float gloss = k.L.Look.Gloss, rough = k.L.Phys.Churn * j.Churn;

        for (int pass = 0; pass < 2; pass++)
        {
            Vector3 h = pass == 0 ? KeyH : FillH;
            Vector3 colr = pass == 0 ? white : cool;
            float strength = pass == 0 ? 0.92f : 0.42f * k.L.Look.Window / 0.55f;
            int first = Mb.Vertices;
            Mb.Reserve(rows * 3, (rows - 1) * 12);
            for (int i = 0; i < rows; i++)
            {
                Vector2 p = j.Path[i];
                int i0 = i > 0 ? i - 1 : i, i1 = i < rows - 1 ? i + 1 : i;
                Vector2 dv = j.Path[i1] - j.Path[i0];
                float len = dv.Length();
                Vector2 d = len > 1e-3f ? dv / len : Vector2.UnitY;
                Vector2 perp = new(-d.Y, d.X);
                float rad = j.Rad[i];

                float ax = Vector2.Dot(perp, new Vector2(h.X, h.Y));
                float u = ax / MathF.Sqrt(ax * ax + h.Z * h.Z);
                float n = Noise.Value(j.Born[i] * 14f + j.Seed + pass * 7f, k.Time * 2.3f);
                float shimmer = 1f - rough * (1f - DrawHelpers.Smooth(-0.35f, 0.55f, n));  // dashes and gaps, more of them the rougher it runs
                float a = strength * shimmer * (0.4f + 0.6f * gloss) * DrawHelpers.Smooth(rad / (2.2f * px));
                float w = MathF.Max(0.8f * px, rad * (pass == 0 ? 0.09f : 0.12f)) * (0.75f + 0.5f * (0.5f + 0.5f * n));
                Vector2 c = p + perp * (u * rad * 0.9f + rough * 0.06f * rad * n);
                Mb.VertexReserved(c - perp * w, Col(in k, colr, 0f));
                Mb.VertexReserved(c, Col(in k, colr, a));
                Mb.VertexReserved(c + perp * w, Col(in k, colr, 0f));
            }
            for (int i = 0; i + 1 < rows; i++)
            {
                if (j.Born[i] - j.Born[i + 1] > j.GapTime) continue;
                int a0 = first + i * 3, b0 = a0 + 3;
                Mb.QuadReserved(a0, a0 + 1, b0, b0 + 1);
                Mb.QuadReserved(a0 + 1, a0 + 2, b0 + 1, b0 + 2);
            }
        }
    }
}
