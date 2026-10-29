using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>Draws a LiquidField (pool, surface, foam, bubbles, streams) shaded by its liquid's table.</summary>
internal static class LiquidDraw
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
    private static float[] _y = new float[512];

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
        if (_y.Length < f.Columns) _y = new float[f.Columns];
        for (int i = 0; i < f.Columns; i++) _y[i] = f.SurfaceY(i * f.Spacing);

        Mb.Clear();
        Body(f, in k);
        Mb.Flush(dl, k.Uv);
        Caustics(dl, f, in k);

        Bubbles(dl, f, in k);
        Trails(f, in k);

        for (int i = 0; i < f.Jets.Count; i++) Stream(f.Jets[i], in k);
        Mb.Flush(dl, k.Uv);

        Surface(f, in k);
        Mb.Flush(dl, k.Uv);
        Plumes(dl, f, in k);
        FoamBubbles(dl, f, in k);
    }

    private static uint Col(in Look k, Vector3 display, float a)
    {
        if (k.Dye > 0f) display = DrawHelpers.BoostSaturation(display, k.Dye, DrawHelpers.SoftDye);
        return DrawHelpers.Tint(DrawHelpers.Pack(display.X, display.Y, display.Z), a * k.Alpha, k.Over);
    }

    private static Vector3 Lit(Vector3 lin) => Matcap.ToDisplay(lin);

    // the pool

    private static void Body(LiquidField f, in Look k)
    {
        var look = k.L.Look; var ph = k.L.Phys;
        int n = f.Columns; float dx = f.Spacing, px = k.Px, H = f.Size.Y;
        const int R = 5;
        Span<float> off = stackalloc float[R];

        float visc = ph.Viscosity;
        Vector3 skin = Lit(look.Thin * (StudioLighting.FillColor * 1.1f + StudioLighting.KeyColor * 0.30f + new Vector3(0.05f)));
        Vector3 deep = Lit(look.Deep * 0.9f);
        float aTop = 0.20f + 0.50f * visc, aDeep = look.Opacity;

        Mb.Reserve(n * R, (n - 1) * (R - 1) * 6);
        int first = Mb.Vertices;
        for (int r = 0; r < R; r++)
        for (int i = 0; i < n; i++)
        {
            float y0 = _y[i], depth = MathF.Max(H - y0, 0f);
            off[0] = 0f; off[1] = 5f * px; off[2] = 20f * px; off[3] = depth * 0.55f; off[4] = depth;
            float y = MathF.Min(y0 + MathF.Max(off[r], r > 0 ? off[r - 1] : 0f), H);
            float z = DrawHelpers.Smooth(MathF.Min((y - y0) / (34f * px), 1f));
            Vector3 c = Vector3.Lerp(skin, deep, MathF.Pow(z, 0.7f)) * (r == 1 ? 1.14f : 1f);
            float a = DrawHelpers.Lerp(aTop, aDeep, z) * DrawHelpers.Smooth(depth / (7f * px));
            Mb.VertexReserved(new Vector2(i * dx, y), Col(in k, c, a));
        }
        Mb.StitchGrid(first, R, n);

    }

    /// <summary>Soft drifting light patches under the surface.</summary>
    private static void Caustics(ImDrawListPtr dl, LiquidField f, in Look k)
    {
        var look = k.L.Look;
        float cw = look.Caustic * 0.20f * (1f - 0.8f * k.L.Phys.Viscosity);
        if (cw < 0.02f || f.Level < 6f * k.Px) return;
        Span<float> rr = stackalloc float[3];
        Span<uint> cc = stackalloc uint[3];
        Vector3 glow = Lit(Vector3.Lerp(look.Thin, Vector3.One, 0.5f) * StudioLighting.KeyColor * 1.3f);
        int count = (int)(f.Size.X / (70f * k.Px));
        for (int i = 0; i < count; i++)
        {
            float u = (i + 0.5f) / count, t = k.Time;
            float x = f.Size.X * (u + 0.05f * Noise.Value(i * 3.7f, t * 0.20f));
            float surface = f.SurfaceY(x), depth = f.Size.Y - surface;
            float y = surface + depth * (0.35f + 0.45f * (0.5f + 0.5f * Noise.Value(i * 9.1f + 5f, t * 0.16f)));
            float w = (34f + 40f * (0.5f + 0.5f * Noise.Value(i * 1.9f, t * 0.3f))) * k.Px;
            float a = cw * DrawHelpers.Smooth(0.0f, 0.8f, 0.5f + 0.7f * Noise.Value(i * 5.3f + 11f, t * 0.35f));
            rr[0] = w * 0.35f; rr[1] = w * 0.7f; rr[2] = w;
            cc[0] = Col(in k, glow, a * 0.55f); cc[1] = Col(in k, glow, a * 0.15f); cc[2] = Col(in k, glow, 0f);
            MeshDraw.Radial(dl, new Vector2(x, y), k.Uv, Col(in k, glow, a), 12, rr, cc, 0f, MathF.Min(0.32f, depth / (w * 2.2f)), 0, 0f);
        }
    }

    private static void Surface(LiquidField f, in Look k)
    {
        var look = k.L.Look; var ph = k.L.Phys;
        int n = f.Columns; float dx = f.Spacing, px = k.Px;
        if (f.Level < 0.5f * px) return;

        Vector3 sky = Lit(look.Thin * 1.15f + Matcap.EnvAverage * 2f);
        Vector3 key = Lit(StudioLighting.KeyColor * (0.9f + look.Gloss * 0.7f));
        Vector3 cool = Lit(new Vector3(0.62f, 0.80f, 1.0f) * 0.9f);
        Vector3 dark = Lit(look.Deep * 0.35f);
        float sheen = 0.45f + 0.5f * look.Gloss;

        const int R = 4;
        Mb.Reserve(n * R, (n - 1) * (R - 1) * 6);
        int first = Mb.Vertices;
        for (int i = 0; i < n; i++)
        {
            float y0 = _y[i];
            float s = (_y[Math.Min(i + 1, n - 1)] - _y[Math.Max(i - 1, 0)]) / (dx * (Math.Min(i + 1, n - 1) - Math.Max(i - 1, 0)));
            float glint = MathF.Exp(-MathF.Pow((s + 0.30f) / 0.20f, 2f)) + 0.12f;
            float chill = MathF.Exp(-MathF.Pow((s - 0.45f) / 0.25f, 2f)) * 0.45f;
            Vector3 line = Vector3.Lerp(sky, key, MathF.Min(1f, glint * sheen)) + cool * chill * 0.5f;
            float aLine = Math.Clamp(0.28f + 0.60f * glint * sheen + 0.2f * chill, 0f, 1f);

            Mb.VertexReserved(new Vector2(i * dx, y0 - 2.6f * px), Col(in k, line, 0f));
            Mb.VertexReserved(new Vector2(i * dx, y0 - 0.2f * px), Col(in k, line, aLine));
            Mb.VertexReserved(new Vector2(i * dx, y0 + 2.4f * px), Col(in k, Vector3.Lerp(line, dark, 0.65f), 0.30f + 0.2f * ph.Viscosity));
            Mb.VertexReserved(new Vector2(i * dx, y0 + 9f * px), Col(in k, dark, 0f));
        }
        // stitched column-wise: each column's four vertices are a row of the grid
        for (int i = 0; i + 1 < n; i++)
            for (int r = 0; r + 1 < R; r++)
            {
                int a = first + i * R + r, b = a + R;
                Mb.QuadReserved(a, a + 1, b, b + 1);
            }

        // foam over the surface
        float foamAmt = ph.Foam;
        if (foamAmt <= 0.01f) return;
        Vector3 white = Lit(Vector3.Lerp(new Vector3(0.90f, 0.95f, 0.98f), look.Thin * 1.6f, MathF.Min(0.95f, 0.30f + 1.1f * ph.Viscosity)) * (StudioLighting.FillColor * 1.3f + StudioLighting.KeyColor * 0.55f));
        Mb.Reserve(n * 3, (n - 1) * 2 * 6);
        int ff = Mb.Vertices;
        for (int i = 0; i < n; i++)
        {
            float amt = MathF.Min(f.Foam[i], 1f);
            float lace = 0.62f + 0.38f * Noise.Value(i * 0.55f, k.Time * 1.3f);
            float a = amt * lace * 0.85f;
            float th = (1.8f + 13f * amt) * px;
            float y0 = _y[i];
            Mb.VertexReserved(new Vector2(i * dx, y0 - th * 0.9f), Col(in k, white, 0f));
            Mb.VertexReserved(new Vector2(i * dx, y0 - th * 0.25f), Col(in k, white, a));
            Mb.VertexReserved(new Vector2(i * dx, y0 + th * 0.7f), Col(in k, white * 0.85f, a * 0.35f));
        }
        for (int i = 0; i + 1 < n; i++)
            for (int r = 0; r < 2; r++)
            {
                int a = ff + i * 3 + r, b = a + 3;
                Mb.QuadReserved(a, a + 1, b, b + 1);
            }
    }

    /// <summary>A soft white mound where a stream lands.</summary>
    private static void Plumes(ImDrawListPtr dl, LiquidField f, in Look k)
    {
        var ph = k.L.Phys;
        if (ph.Foam <= 0.01f) return;
        Span<float> rr = stackalloc float[3];
        Span<uint> cc = stackalloc uint[3];
        Vector3 white = Lit(Vector3.Lerp(new Vector3(0.92f, 0.96f, 1f), k.L.Look.Thin * 1.8f, 0.25f) * (StudioLighting.FillColor * 1.5f + StudioLighting.KeyColor * 0.6f));
        for (int i = 0; i < f.Jets.Count; i++)
        {
            var j = f.Jets[i];
            if (!j.Wet) continue;
            float px = k.Px, w = j.HitR * 2.2f + 10f * px;
            float a = Math.Clamp(j.HitR / (12f * px), 0.35f, 1f) * 0.62f * ph.Foam * (0.8f + 0.2f * Noise.Value(k.Time * 3.1f, j.Seed));
            rr[0] = w * 0.40f; rr[1] = w * 0.75f; rr[2] = w;
            cc[0] = Col(in k, white, a * 0.55f); cc[1] = Col(in k, white, a * 0.18f); cc[2] = Col(in k, white, 0f);
            MeshDraw.Radial(dl, j.Hit + new Vector2(0f, -w * 0.12f), k.Uv, Col(in k, white, a), 14, rr, cc, 0f, 0.55f, 0, 0f);
        }
    }

    private static void FoamBubbles(ImDrawListPtr dl, LiquidField f, in Look k)
    {
        // Filled where the foam is thick, tiny beads of air riding on it.
        float px = k.Px; int n = f.Columns;
        Span<float> rr = stackalloc float[3];
        Span<uint> cc = stackalloc uint[3];
        Vector3 rim = Lit(Vector3.Lerp(new Vector3(0.8f, 0.9f, 1f), k.L.Look.Thin, 0.4f) * 0.9f);
        int drawn = 0;
        for (int i = 1; i < n - 1 && drawn < 44; i++)
        {
            float amt = f.Foam[i];
            if (amt < 0.45f || DrawHelpers.Hash01(i * 91 + (int)(k.Time * 6f) / 3) > amt * 0.55f) continue;
            float r = DrawHelpers.HashRange(i * 17 + (int)(k.Time * 6f) / 3, 0.9f, 2.4f) * px;
            var at = new Vector2(i * f.Spacing + DrawHelpers.HashRange(i * 29, -3f, 3f) * px, _y[i] - r * 0.3f - DrawHelpers.Hash01(i * 7) * 3f * px);
            rr[0] = r * 0.7f; rr[1] = r; rr[2] = r * 1.25f;
            cc[0] = Col(in k, rim, 0.10f * amt); cc[1] = Col(in k, rim, 0.55f * amt); cc[2] = Col(in k, rim, 0f);
            MeshDraw.Radial(dl, at, k.Uv, Col(in k, rim, 0.03f), 8, rr, cc, 0f, 1f, 0, 0f);
            drawn++;
        }
    }

    // bubbles carried under

    private static void Bubbles(ImDrawListPtr dl, LiquidField f, in Look k)
    {
        if (f.BubbleCount == 0) return;
        Span<float> rr = stackalloc float[3];
        Span<uint> cc = stackalloc uint[3];
        Vector3 bright = Lit(Vector3.Lerp(new Vector3(0.8f, 0.92f, 1f), k.L.Look.Thin, 0.35f) * 1.1f);
        Vector3 shade = Lit(k.L.Look.Deep * 0.5f);
        for (int i = 0; i < f.BubbleCount; i++)
        {
            ref readonly var b = ref f.Bubbles[i];
            float r = b.R, a = DrawHelpers.Smooth(b.Age / 0.12f) * (0.55f + 0.45f * k.L.Phys.Bubbles);
            rr[0] = r * 0.80f; rr[1] = r * 1.00f; rr[2] = r * 1.30f;
            cc[0] = Col(in k, bright, 0.40f * a); cc[1] = Col(in k, shade, 0.34f * a); cc[2] = Col(in k, shade, 0f);
            MeshDraw.Radial(dl, b.P, k.Uv, Col(in k, bright, 0.06f * a), 9, rr, cc, 0f, 1f, 0, 0f);

            if (r < 1.4f * k.Px) continue;
            rr[0] = r * 0.16f; rr[1] = r * 0.30f; rr[2] = r * 0.46f;
            cc[0] = Col(in k, Vector3.One, 0.95f * a); cc[1] = Col(in k, Vector3.One, 0.45f * a); cc[2] = Col(in k, Vector3.One, 0f);
            MeshDraw.Radial(dl, b.P + new Vector2(-0.38f, -0.40f) * r, k.Uv, Col(in k, Vector3.One, 0.98f * a), 6, rr, cc, 0f, 1f, 0, 0f);
        }
    }

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
                Mb.VertexReserved(p + perp * (sn * rad * (sn < 0f ? eL : eR)), Col(in k, col, a * tail));
            }
            float edge = rad * MathF.Max(eL, eR) + k.Feather;
            Mb.VertexReserved(p - perp * edge, halo);
            Mb.VertexReserved(p + perp * edge, halo);
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
