namespace RealDebuffs.Effects.Framework;

internal static partial class LiquidDraw
{
    const float WaterIndex = 1.333f;
    const int Planes = 8;  // depth slices the light is traced to

    // the key light's travel across the screen (y down), its tilt, and how far the water bends it
    static readonly Vector2 AirDir = Vector2.Normalize(new(-StudioLighting.Key.X, -StudioLighting.Key.Y));
    static readonly float AirTilt = AirDir.X / AirDir.Y;
    static readonly float WaterTilt = FlatTilt();

    static float FlatTilt() { var t = Refract(0f); return t.X / t.Y; }

    static float[] _y = new float[512], _slope = new float[512];

    /// Surface height and slope at every column.
    static void Sample(LiquidField f)
    {
        int n = f.Columns;
        if (_y.Length < n) { _y = new float[n]; _slope = new float[n]; }
        for (int i = 0; i < n; i++) _y[i] = f.SurfaceY(i * f.Spacing);
        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(i - 1, 0), b = Math.Min(i + 1, n - 1);
            _slope[i] = (_y[b] - _y[a]) / (f.Spacing * (b - a));
        }
    }

    static float SurfaceAt(float x, LiquidField f)
    {
        float u = Math.Clamp(x / f.Spacing, 0f, f.Columns - 1.001f);
        int i = (int)u;
        return Lerp(_y[i], _y[i + 1], (u - i));
    }

    /// The key light after the water bends it, for a surface that falls s px per px to the right (zero if it cannot enter).
    static Vector2 Refract(float s)
    {
        Vector2 nrm = new Vector2(s, -1f) / Sqrt(1f + s * s);
        float cosI = -Vector2.Dot(nrm, AirDir), eta = 1f / WaterIndex;
        if (cosI <= 0.02f) return default;
        return eta * AirDir + (eta * cosI - Sqrt(1f - eta * eta * (1f - cosI * cosI))) * nrm;
    }

    // the pool

    static void Body(LiquidField f, in Look k)
    {
        var look = k.L.Look; var ph = k.L.Phys;
        int n = f.Columns; float dx = f.Spacing, px = k.Px, H = f.Size.Y;
        const int R = 8;
        ReadOnlySpan<float> rows = [0f, 3f, 9f, 20f, 38f, 64f, 100f, 1e5f];

        // light is absorbed with depth, so the tint closes on the deep colour exponentially
        float reach = Lerp(80f, 30f, look.Opacity) * px;
        Vector3 skin = Lit(look.Thin * (StudioLighting.FillColor * 1.1f + StudioLighting.KeyColor * 0.30f + new Vector3(0.05f)));
        Vector3 deep = Lit(look.Deep * 0.9f);
        float aTop = 0.12f + 0.50f * ph.Viscosity, aDeep = look.Opacity;

        Span<float> zr = stackalloc float[R];
        for (int r = 0; r < R; r++) zr[r] = 1f - Exp(-rows[r] * px / reach);

        Mb.Reserve(n * R, (n - 1) * (R - 1) * 6);
        int first = Mb.Vertices;
        for (int i = 0; i < n; i++)
        {
            float y0 = _y[i], x = i * dx, fade = Smooth((H - y0) / (7f * px));
            for (int r = 0; r < R; r++)
            {
                float y = y0 + rows[r] * px, z = zr[r];
                if (y > H) { y = H; z = 1f - Exp(-(H - y0) / reach); }
                Mb.VertexReserved(new(x, y), Col(in k, Vector3.Lerp(skin, deep, z), Lerp(aTop, aDeep, z) * fade));
            }
        }
        Mb.StitchGrid(first, n, R);
    }

    // light shafts: the key light is traced through the real surface to a few depth slices, so waves focus bright filaments and starve gaps

    static float[] _lux = new float[Planes * 800], _rayX = new float[800], _rayQ = new float[800], _rayW = new float[800], _rayE = new float[800], _row = new float[800];
    static readonly float[] PlaneD = new float[Planes];
    static int _luxCols, _luxExt;
    static float _luxRest, _luxLevel, _luxReach;
    static bool _luxOn;

    static void Shafts(LiquidField f, in Look k)
    {
        _luxOn = false;
        var look = k.L.Look;
        float gain = look.Caustic * 0.20f * (1f - 0.8f * k.L.Phys.Viscosity);
        float px = k.Px, level = f.Level;
        if (gain < 0.02f || level < 10f * px) return;

        int n = f.Columns; float dx = f.Spacing, W = f.Size.X, rest = f.Size.Y - level;
        int ext = (int)Ceiling(level * WaterTilt / dx) + 2, cols = n + 2 * ext;
        if (_rayX.Length < cols)
        {
            int m = cols + 64;
            _rayX = new float[m]; _rayQ = new float[m]; _rayW = new float[m]; _rayE = new float[m]; _row = new float[m];
        }
        if (_lux.Length < Planes * cols) _lux = new float[Planes * (cols + 64)];
        Array.Clear(_lux, 0, Planes * cols);

        // each ray: where it enters, which way it is bent, and how much light the tilted surface takes in
        for (int e = 0; e < cols; e++) _rayE[e] = SurfaceAt(Math.Clamp((e - ext) * dx, 0f, W), f) - rest;
        for (int e = 0; e < cols; e++)
        {
            float s = (_rayE[Math.Min(e + 1, cols - 1)] - _rayE[Math.Max(e - 1, 0)]) / (dx * (Math.Min(e + 1, cols - 1) - Math.Max(e - 1, 0)));
            Vector2 t = Refract(s);
            _rayX[e] = (e - ext) * dx;
            _rayQ[e] = t.Y > 0.05f ? t.X / t.Y : 0f;
            _rayW[e] = t.Y > 0.05f ? Max(0f, 1f - AirTilt * s) : 0f;
        }

        for (int j = 0; j < Planes; j++)
        {
            float d = level * Pow(j / (Planes - 1f), 1.35f);
            PlaneD[j] = d;
            int o = j * cols;
            for (int e = 0; e < cols; e++)
            {
                float b = (_rayX[e] + d * (_rayQ[e] - WaterTilt) - _rayE[e] * _rayQ[e]) / dx + ext, w = _rayW[e];
                int c = (int)Floor(b);
                if (c < 0 || c + 1 >= cols) continue;
                float fr = b - c;
                _lux[o + c] += w * (1f - fr); _lux[o + c + 1] += w * fr;
            }
            for (int pass = 0; pass < 2; pass++)  // soften: the rays are only so dense, and real shafts are not razors
            {
                for (int c = 0; c < cols; c++) _row[c] = _lux[o + c];
                for (int c = 1; c + 1 < cols; c++) _lux[o + c] = 0.25f * (_row[c - 1] + _row[c + 1]) + 0.5f * _row[c];
            }
        }
        _luxCols = cols; _luxExt = ext; _luxRest = rest; _luxLevel = level;
        _luxReach = Lerp(80f, 30f, look.Opacity) * px;
        _luxOn = true;

        Vector3 glow = Lit(Vector3.Lerp(look.Thin, Vector3.One, 0.55f) * StudioLighting.KeyColor * 1.3f);
        Vector3 shade = Lit(look.Deep * 0.5f);
        int lo = Math.Max(1, ext - (int)Ceiling(level * WaterTilt / dx) - 1), hi = Math.Min(cols - 2, ext + n), span = (hi - lo) / 2 + 2;  // every second bin: the light is soft enough
        Mb.Reserve(Planes * span, (span - 1) * (Planes - 1) * 6);
        int first = Mb.Vertices;
        for (int j = 0; j < Planes; j++)
        {
            float d = PlaneD[j], sink = 1f - d / level;
            float strength = gain * Exp(-d / (0.9f * _luxReach)) * Smooth(d / (4f * px));
            for (int q = 0; q < span; q++)
            {
                int c = Math.Min(lo + 2 * q, cols - 2);
                float x = (c - ext) * dx + d * WaterTilt, lux = _lux[j * cols + c];
                float over = Min(Max(lux - 1f, 0f), 2.5f);
                float lift = Min(0.32f, strength * 0.42f * over / (1f + 0.3f * over));
                float dim = strength * 0.30f * Min(Max(1f - lux, 0f), 1f);
                Mb.VertexReserved(new(x, rest + d + (SurfaceAt(x, f) - rest) * sink), lift >= dim ? Col(in k, glow, lift) : Col(in k, shade, dim));
            }
        }
        Mb.StitchGrid(first, Planes, span);
    }

    /// How much more (or less) than the plain key light reaches a point in the water; 1 when the shafts are off.
    static float LightAt(float x, float y, float dx)
    {
        float d = y - _luxRest;
        if (!_luxOn || d <= 0f || d >= _luxLevel) return 1f;
        int j = 0;
        while (j + 2 < Planes && PlaneD[j + 1] < d) j++;
        float tj = Saturate((d - PlaneD[j]) / Max(PlaneD[j + 1] - PlaneD[j], 1e-3f));
        float b = Math.Clamp((x - d * WaterTilt) / dx + _luxExt, 0f, _luxCols - 1.001f);
        int c = (int)b; float fr = b - c;
        float a = _lux[j * _luxCols + c] * (1f - fr) + _lux[j * _luxCols + c + 1] * fr;
        float z = _lux[(j + 1) * _luxCols + c] * (1f - fr) + _lux[(j + 1) * _luxCols + c + 1] * fr;
        return Lerp(a, z, tj);
    }

    /// Air dragged under where a stream lands: a streaky white column that widens, slows and thins with depth.
    static void Plunges(LiquidField f, in Look k)
    {
        var ph = k.L.Phys;
        if (ph.Bubbles <= 0.05f || f.Level < 6f * k.Px) return;
        const int Rz = 14, Cx = 7;
        float px = k.Px;
        Vector3 milk = Lit(Vector3.Lerp(new(0.85f, 0.93f, 1f), k.L.Look.Thin * 1.5f, 0.3f + 0.6f * ph.Viscosity) * (StudioLighting.FillColor * 1.2f + StudioLighting.KeyColor * 0.4f));
        for (int i = 0; i < f.Jets.Count; i++)
        {
            var j = f.Jets[i];
            if (!j.Wet) continue;
            float w0 = j.HitR * 1.25f + 6f * px;
            float reach = Min(f.Size.Y - j.Hit.Y, j.HitR * 3.2f + 42f * px);
            if (reach < 8f * px) continue;
            float strength = Math.Clamp(j.HitR / (12f * px), 0.3f, 1f) * 0.42f * ph.Bubbles;
            Vector2 v = j.PathV[Math.Max(j.Rows - 1, 0)];
            float lean = v.Y > 1f ? Math.Clamp(v.X / v.Y, -0.8f, 0.8f) : 0f;

            Mb.Reserve(Rz * Cx, (Rz - 1) * (Cx - 1) * 6);
            int first = Mb.Vertices;
            for (int r = 0; r < Rz; r++)
            {
                float u = r / (Rz - 1f), z = reach * Pow(u, 1.2f);
                float w = w0 * (0.8f + 0.9f * u), cx = j.Hit.X + lean * reach * 0.5f * (1f - Exp(-u * 2.5f));
                float fade = Exp(-1.9f * u) * (1f - u) * Smooth(z / (3f * px));
                for (int c = 0; c < Cx; c++)
                {
                    float q = c / (Cx - 1f) * 2f - 1f;
                    float streak = 0.55f + 0.45f * Noise.Value(q * 2.6f + j.Seed * 0.37f, z / (16f * px) - k.Time * 1.5f);  // bubbles stream down the column
                    float a = strength * fade * Pow(1f - q * q, 1.3f) * streak;
                    Mb.VertexReserved(new(cx + q * w, j.Hit.Y + z), Col(in k, milk, a));
                }
            }
            Mb.StitchGrid(first, Rz, Cx);
        }
    }

    // bubbles carried under

    static void Bubbles(ImDrawListPtr dl, LiquidField f, in Look k)
    {
        if (f.BubbleCount == 0) return;
        Span<float> rr = stackalloc float[3];
        Span<uint> cc = stackalloc uint[3];
        Vector3 rim = Lit(Vector3.Lerp(new(0.8f, 0.92f, 1f), k.L.Look.Thin, 0.35f) * 1.1f);
        Vector3 core = Lit(k.L.Look.Deep * 0.5f);
        for (int i = 0; i < f.BubbleCount; i++)
        {
            ref readonly var b = ref f.Bubbles[i];
            float r = b.R, a = Smooth(b.Age / 0.12f) * (0.55f + 0.45f * k.L.Phys.Bubbles);

            // an air bubble is nearly clear in the middle, with a bright rim where light is totally reflected inside it
            rr[0] = r * 0.70f; rr[1] = r * 0.98f; rr[2] = r * 1.20f;
            cc[0] = Col(in k, core, 0.10f * a); cc[1] = Col(in k, rim, 0.62f * a); cc[2] = Col(in k, rim, 0f);
            MeshDraw.Radial(dl, b.P, k.Uv, Col(in k, core, 0.04f * a), 10, rr, cc, 0f, 1f, 0, 0f);

            if (r < 1.4f * k.Px) continue;
            rr[0] = r * 0.14f; rr[1] = r * 0.28f; rr[2] = r * 0.42f;
            cc[0] = Col(in k, Vector3.One, 0.95f * a); cc[1] = Col(in k, Vector3.One, 0.40f * a); cc[2] = Col(in k, Vector3.One, 0f);
            MeshDraw.Radial(dl, b.P + new Vector2(-0.38f, -0.40f) * r, k.Uv, Col(in k, Vector3.One, 0.98f * a), 6, rr, cc, 0f, 1f, 0, 0f);  // the key light
            if (r < 2.2f * k.Px) continue;
            rr[0] = r * 0.10f; rr[1] = r * 0.22f; rr[2] = r * 0.32f;
            cc[0] = Col(in k, rim, 0.45f * a); cc[1] = Col(in k, rim, 0.18f * a); cc[2] = Col(in k, rim, 0f);
            MeshDraw.Radial(dl, b.P + new Vector2(0.40f, 0.42f) * r, k.Uv, Col(in k, rim, 0.5f * a), 6, rr, cc, 0f, 1f, 0, 0f);  // the fill, faint, on the far side
        }
    }

    /// Specks hanging in clear water: they drift on a slow current, and flash as they cross a shaft of light.
    static void Motes(ImDrawListPtr dl, LiquidField f, in Look k)
    {
        var look = k.L.Look;
        float clear = (1f - look.Opacity) * (1f - k.L.Phys.Viscosity), px = k.Px, level = f.Level, W = f.Size.X;
        if (clear < 0.1f || level < 24f * px) return;
        int count = (int)(W / (62f * px) * Math.Clamp(level / (120f * px), 0.3f, 1f));
        Span<float> rr = stackalloc float[2];
        Span<uint> cc = stackalloc uint[2];
        Vector3 dust = Lit(Vector3.Lerp(look.Thin, Vector3.One, 0.6f) * (StudioLighting.FillColor * 1.2f + StudioLighting.KeyColor * 0.9f));
        for (int i = 0; i < count; i++)
        {
            int h = i * 131 + 17;
            float t = k.Time, p = Hash01(h + 2) * Tau;
            float x = Wrap(Hash01(h) * W + t * (5f + 9f * Hash01(h + 3)) * px + 30f * px * Sin(t * 0.11f + p), W);
            float s = SurfaceAt(x, f), depth = f.Size.Y - s;
            float d = 0.10f + 0.84f * Hash01(h + 1);
            float y = s + depth * d + 4f * px * Sin(t * 0.37f + p);
            float lit = Math.Clamp(LightAt(x, y, f.Spacing) - 0.4f, 0f, 1.6f) / 1.6f;
            float a = clear * (0.10f + 0.36f * lit) * Smooth(d / 0.14f) * Smooth(depth / (30f * px));
            float r = (0.7f + 1.1f * Hash01(h + 4)) * px * (0.8f + 0.5f * lit);
            rr[0] = r * 0.5f; rr[1] = r;
            cc[0] = Col(in k, dust, a * 0.5f); cc[1] = Col(in k, dust, 0f);
            MeshDraw.Radial(dl, new(x, y), k.Uv, Col(in k, dust, a), 6, rr, cc, 0f, 1f, 0, 0f);
        }
    }

    // the waterline: the meniscus, and under it the silvery band where the surface mirrors the water from below

    static void Surface(LiquidField f, in Look k)
    {
        var look = k.L.Look; var ph = k.L.Phys;
        int n = f.Columns; float dx = f.Spacing, px = k.Px;
        if (f.Level < 0.5f * px) return;

        Vector3 sky = Lit(look.Thin * 1.15f + Matcap.EnvAverage * 2f);
        Vector3 key = Lit(StudioLighting.KeyColor * (0.9f + look.Gloss * 0.7f));
        Vector3 under = Lit(Vector3.Lerp(look.Thin, Vector3.One, 0.40f) * (StudioLighting.FillColor * 1.3f + StudioLighting.KeyColor * 0.5f));
        float sheen = 0.45f + 0.5f * look.Gloss, clear = 1f - 0.6f * ph.Viscosity;

        const int R = 6;
        Mb.Reserve(n * R, (n - 1) * (R - 1) * 6);
        int first = Mb.Vertices;
        for (int i = 0; i < n; i++)
        {
            float x = i * dx, y0 = _y[i];
            float micro = 0.14f * Noise.Value(x * 0.55f / px, k.Time * 2.6f);  // ripples too fine for the mesh still sparkle
            float s = _slope[i] + micro;
            float glint = Exp(-Pow((s + 0.30f) / 0.20f, 2f)) + 0.10f;
            float lit = Math.Clamp((1f - AirTilt * s - 0.5f) / 1.0f, 0f, 1.2f);  // a face turned to the light is brighter
            float broken = 0.55f + 0.45f * Smooth(-0.2f, 0.7f, Noise.Value(x * 0.03f / px, k.Time * 0.35f));
            float fade = Smooth((f.Size.Y - y0) / (30f * px));

            Vector3 line = Vector3.Lerp(sky, key, Min(1f, glint * sheen));
            float aLine = Math.Clamp(0.26f + 0.50f * glint * sheen + 0.18f * lit, 0f, 0.95f);
            float aBand = (0.10f + 0.22f * sheen * lit) * clear * broken * fade;

            Mb.VertexReserved(new(x, y0 - 3.2f * px), Col(in k, line, 0f));
            Mb.VertexReserved(new(x, y0 - 0.5f * px), Col(in k, line, aLine));
            Mb.VertexReserved(new(x, y0 + 1.0f * px), Col(in k, Vector3.Lerp(line, under, 0.5f), aLine * 0.75f));
            Mb.VertexReserved(new(x, y0 + 2.6f * px), Col(in k, under, aBand));
            Mb.VertexReserved(new(x, y0 + 6.5f * px), Col(in k, under, aBand * 0.45f));
            Mb.VertexReserved(new(x, y0 + 14f * px), Col(in k, under, 0f));
        }
        Mb.StitchGrid(first, n, R);
    }

    /// Foam: ragged patches that sit on the surface and thin out below it, never an even glow.
    static void Foam(LiquidField f, in Look k)
    {
        var look = k.L.Look; var ph = k.L.Phys;
        if (ph.Foam <= 0.01f || f.Level < 0.5f * k.Px) return;
        int n = f.Columns; float dx = f.Spacing, px = k.Px;
        Vector3 white = Lit(Vector3.Lerp(new(0.90f, 0.95f, 0.98f), look.Thin * 1.6f, Min(0.95f, 0.30f + 1.1f * ph.Viscosity)) * (StudioLighting.FillColor * 1.3f + StudioLighting.KeyColor * 0.55f));

        const int R = 5;
        Mb.Reserve(n * R, (n - 1) * (R - 1) * 6);
        int first = Mb.Vertices;
        for (int i = 0; i < n; i++)
        {
            float x = i * dx, y0 = _y[i];
            if (f.Foam[i] < 0.02f)
            {
                for (int r = 0; r < R; r++) Mb.VertexReserved(new(x, y0), Col(in k, white, 0f));
                continue;
            }
            float cell = 0.5f + 0.35f * Noise.Value(x * 0.035f / px, k.Time * 0.6f) + 0.15f * Noise.Value(x * 0.09f / px, k.Time * 1.3f);
            float body = Smooth(0.04f, 0.95f, Min(f.Foam[i], 1.4f) * (0.45f + 0.8f * cell));
            float th = (1.4f + 9f * body) * px, a = body * 0.78f;
            Mb.VertexReserved(new(x, y0 - th), Col(in k, white, 0f));
            Mb.VertexReserved(new(x, y0 - th * 0.55f), Col(in k, white, a * 0.85f));
            Mb.VertexReserved(new(x, y0 + th * 0.15f), Col(in k, white, a));
            Mb.VertexReserved(new(x, y0 + th * 1.10f), Col(in k, white * 0.85f, a * 0.25f));
            Mb.VertexReserved(new(x, y0 + th * 2.00f), Col(in k, white * 0.85f, 0f));
        }
        Mb.StitchGrid(first, n, R);
    }

    /// Beads of air riding on thick foam: each one forms, lasts a moment and pops, at its own time.
    static void FoamBubbles(ImDrawListPtr dl, LiquidField f, in Look k)
    {
        float px = k.Px; int n = f.Columns;
        Span<float> rr = stackalloc float[3];
        Span<uint> cc = stackalloc uint[3];
        Vector3 rim = Lit(Vector3.Lerp(new(0.8f, 0.9f, 1f), k.L.Look.Thin, 0.4f) * 0.9f);
        int drawn = 0;
        for (int i = 1; i < n - 1 && drawn < 110; i++)
        {
            float amt = f.Foam[i];
            if (amt < 0.25f) continue;
            for (int s = 0; s < 2; s++)
            {
                int h = i * 91 + s * 577;
                float life = HashRange(h + 1, 0.8f, 1.7f), u = k.Time / life + Hash01(h + 2);
                int cycle = (int)u;
                float born = u - cycle;
                if (Hash01(h + cycle * 31) > amt * 0.8f) continue;
                float env = Sqrt(Sin(PI * born));
                float r = HashRange(h + cycle * 17, 0.9f, 2.6f) * px;
                var at = new Vector2(i * f.Spacing + HashRange(h + cycle * 29, -3.5f, 3.5f) * px, _y[i] - r * 0.3f - Hash01(h + cycle * 7) * 4f * px);
                rr[0] = r * 0.7f; rr[1] = r; rr[2] = r * 1.25f;
                cc[0] = Col(in k, rim, 0.10f * env); cc[1] = Col(in k, rim, 0.55f * env); cc[2] = Col(in k, rim, 0f);
                MeshDraw.Radial(dl, at, k.Uv, Col(in k, rim, 0.03f * env), 8, rr, cc, 0f, 1f, 0, 0f);
                drawn++;
            }
        }
    }

    /// Water thrown up round a landing stream or a big drop: a thin bright-edged sheet that rises and falls back.
    static void Sheets(LiquidField f, in Look k)
    {
        var look = k.L.Look; var ph = k.L.Phys;
        if (f.Level < 0.5f * k.Px || ph.Splash <= 0.02f) return;
        float px = k.Px, amp = 0.25f + 0.75f * ph.Splash;
        Vector3 wet = Lit(Vector3.Lerp(new(0.88f, 0.95f, 1f), look.Thin * 1.6f, Min(0.9f, 0.2f + 1.1f * ph.Viscosity)) * (StudioLighting.FillColor * 1.4f + StudioLighting.KeyColor * 0.6f));

        for (int i = 0; i < f.Jets.Count; i++)
        {
            var j = f.Jets[i];
            if (!j.Wet) continue;
            float strength = Math.Clamp(j.HitR / (10f * px), 0.25f, 1.2f);
            float height = (j.HitR * 1.05f + 8f * px) * (0.8f + 0.4f * Noise.Value(k.Time * 4f + j.Seed, 1.3f)) * amp;
            Sheet(f, in k, j.Hit.X, j.HitR * 0.95f, j.HitR * 1.6f + 10f * px, height, j.Seed, 0.9f * strength, wet);
        }
        for (int i = 0; i < f.CrownCount; i++)
        {
            ref readonly var c = ref f.Crowns[i];
            float u = c.Age / c.Life, env = Pow(Sin(PI * u), 0.8f);
            Sheet(f, in k, c.X, c.R * 0.8f, c.R * (1.6f + 2.2f * u), c.Height * env * amp, c.Seed, 0.9f * env, wet);
        }
    }

    // one sheet either side of x: tallest against the stream, lower and raggeder further out
    static void Sheet(LiquidField f, in Look k, float x, float halfW, float reach, float height, int seed, float alpha, Vector3 col)
    {
        if (height < 0.8f * k.Px || alpha < 0.02f) return;
        const int M = 7;
        float px = k.Px;
        for (int side = -1; side <= 1; side += 2)
        {
            Mb.Reserve(M * 4, (M - 1) * 3 * 6);
            int first = Mb.Vertices;
            for (int c = 0; c < M; c++)
            {
                float u = c / (float)(M - 1);
                float xx = x + side * (halfW + reach * u), yb = f.SurfaceY(xx);
                float h = height * Pow(1f - u, 0.9f) * (0.75f + 0.25f * Noise.Value(seed * 0.1f + c * 1.7f + side * 5f, k.Time * 5f));
                Mb.VertexReserved(new(xx, yb + px), Col(in k, col, 0.10f * alpha));
                Mb.VertexReserved(new(xx, yb - h * 0.55f), Col(in k, col, 0.30f * alpha));
                Mb.VertexReserved(new(xx, yb - h), Col(in k, col, 0.70f * alpha));
                Mb.VertexReserved(new(xx, yb - h - 1.6f * px), Col(in k, col, 0f));
            }
            for (int c = 0; c + 1 < M; c++)
                for (int r = 0; r < 3; r++) { int a = first + c * 4 + r, b = a + 4; Mb.QuadReserved(a, a + 1, b, b + 1); }
        }
    }
}
