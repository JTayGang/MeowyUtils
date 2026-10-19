using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>Blood painted along a path as lit, wet geometry: a thick ribbon whose cross-section is a flattened tube</summary>
public sealed class StrokeBlood : IStrokeMaterial
{
    public string Name => "stroke.blood";
    public string[] NaturalLanguageWords { get; } = { "bloodpaint", "smear", "smears", "smeared", "daub", "daubed" };

    // the cross-section: Columns across the ribbon: the first and last are a zero-alpha fringe just outside the edge.
    private static readonly float[] Across = { -1.16f, -1.00f, -0.84f, -0.62f, -0.38f, -0.12f, 0.12f, 0.38f, 0.62f, 0.84f, 1.00f, 1.16f };
    private const int Cols = 12;
    private const float Crown = 0.93f;  // how round the paint's section is: 1 = a full cylinder

    private static readonly Vector3 KeyHalf = Vector3.Normalize(StudioLighting.Key + Vector3.UnitZ);
    private static readonly Vector3 FillHalf = Vector3.Normalize(StudioLighting.Fill + Vector3.UnitZ);

    // The paint's shading, baked by normal.
    private static readonly PaintLut Paint = new();

    private sealed class PaintLut
    {
        private const int N = 48;
        private readonly Vector3[] _tex = new Vector3[N * N];

        public PaintLut()
        {
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float nx = x / (N - 1f) * 2f - 1f, ny = y / (N - 1f) * 2f - 1f;
                float r2 = nx * nx + ny * ny;
                if (r2 > 1f) { float k = 1f / MathF.Sqrt(r2); nx *= k; ny *= k; r2 = 1f; }
                _tex[y * N + x] = Shade(new Vector3(nx, ny, MathF.Sqrt(MathF.Max(0f, 1f - r2))));
            }
        }

        private static Vector3 Shade(Vector3 n)
        {
            float nz = n.Z;

            // Soft wrapped light: wet paint rolls off gently from lit to dark.
            float diff = MathF.Pow(Math.Clamp((Vector3.Dot(n, StudioLighting.Key) + 0.35f) / 1.35f, 0f, 1f), 1.25f);
            Vector3 deep = new(0.060f, 0.000f, 0.008f), mid = new(0.43f, 0.018f, 0.030f), lit = new(0.80f, 0.085f, 0.070f);
            Vector3 body = diff < 0.5f ? Vector3.Lerp(deep, mid, diff * 2f) : Vector3.Lerp(mid, lit, (diff - 0.5f) * 2f);

            // Thin blood at the silhouette is ruby with light coming through it; the thick middle is the darkest part.
            float rim = MathF.Pow(1f - nz, 2.3f);
            body += new Vector3(0.34f, 0.022f, 0.026f) * (rim * (0.35f + 0.65f * diff));
            body *= 0.78f + 0.22f * nz;

            // A broad dim sheen under the hard highlight, and the surround reflected at grazing angles.
            float nh = MathF.Max(0f, Vector3.Dot(n, KeyHalf));
            body += StudioLighting.KeyColor * (MathF.Pow(nh, 22f) * 0.13f);
            float fres = 0.03f + 0.97f * MathF.Pow(1f - nz, 5f);
            body += new Vector3(0.20f, 0.10f, 0.09f) * fres;
            return body;
        }

        public Vector3 Sample(float nx, float ny)
        {
            float fx = Math.Clamp((nx * 0.5f + 0.5f) * (N - 1), 0f, N - 1.001f);
            float fy = Math.Clamp((ny * 0.5f + 0.5f) * (N - 1), 0f, N - 1.001f);
            int x0 = (int)fx, y0 = (int)fy;
            float tx = fx - x0, ty = fy - y0;
            int i = y0 * N + x0;
            return _tex[i] * ((1f - tx) * (1f - ty)) + _tex[i + 1] * (tx * (1f - ty)) + _tex[i + N] * ((1f - tx) * ty) + _tex[i + N + 1] * (tx * ty);
        }
    }

    private static readonly LightStrand Strand = new();
    private static readonly MeshBuilder Mb = new();

    // The warm light thrown up from a fire below, and the ember glow of a pulse running through the paint.
    private static readonly Vector3 Underlight = new(1.00f, 0.30f, 0.05f);
    private static readonly Vector3 InnerGlow = new(1.00f, 0.16f, 0.03f);

    // thickness: Shared by the drawing and by RadiusAt, so a drip finds the real edge of the paint.
    private const float EdgeNoiseAmp = 0.11f;  // fraction of the half-width the edge wanders
    private const float EdgeNoiseScale = 0.0105f;  // noise cycles per px of arc

    private static float WidthScale(float arc, int seed, float total, bool closed)
    {
        float s = seed * 0.0137f;
        return 1f + EdgeNoiseAmp * Noise.Along(arc, EdgeNoiseScale, s, 1.7f, total, closed)
                  + 0.5f * EdgeNoiseAmp * Noise.Along(arc, EdgeNoiseScale * 3.1f, s * 1.9f, 5.3f, total, closed);
    }

    private static float Lean(float arc, int seed, float total, bool closed) =>
        0.16f * Noise.Along(arc, 0.017f, seed * 0.0071f, 9.1f, total, closed);

    public float RadiusAt(in StrokePrimitive s, float arc, float shortSide) =>
        MathF.Max(1f, 0.5f * MathF.Max(3f, s.WidthHint) * (1f - 0.12f * Math.Clamp(s.Depth, 0f, 1f)) * WidthScale(arc, s.Seed, s.Path.Length, s.Closed));

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        float reveal = Math.Clamp(s.Reveal, 0f, 1f);
        if (path.Count < 2 || alpha <= 0.004f || reveal <= 0.001f) return;

        float total = path.Length;
        if (total < 4f) return;

        float px = ctx.ScreenScale;
        float depth = Math.Clamp(s.Depth, 0f, 1f);
        float width = MathF.Max(4f * px, s.WidthHint) * (1f - 0.12f * depth);
        float visibleLen = total * reveal;
        float time = ctx.Time;
        bool overridden = DrawHelpers.ColorOverrideActive;
        float agit = Math.Clamp(s.Agitation, 0f, 1f);

        float step = Math.Clamp(width * 0.42f, 7f * px, 14f * px);
        var st = Strand;
        st.Resample(path, visibleLen, step);
        st.Light(in s, time, total, width, px, 0f);  // Lum: a shimmer and pulses of energy running along the line
        int n = st.Count;
        if (n < 2) return;

        // A soft shadow cast by the paint's thickness: it lifts the ring off whatever is behind it
        bool closedFull = s.Closed && reveal >= 0.999f;
        StrandShading.DrawShadow(dl, path, width * 1.05f, depth, alpha * 0.85f, in ctx, closedFull, visibleLen);

        var pts = st.Pts;
        var nrm = st.Nrm;
        var arc = st.Arc;
        var lum = st.Lum;

        float half0 = width * 0.5f;
        float fringe = 1.1f * px;
        float soften = 1f + 0.9f * depth;
        float near = 1f - 0.40f * depth;  // far paint is dimmer
        float haze = 0.50f * depth;  // and hazier

        bool capStart = !s.FlushStart && !closedFull;
        bool capEnd = !closedFull;
        float capLen = half0 * 0.95f;

        Mb.Clear();
        Mb.Reserve(n * Cols, (n - 1) * (Cols - 1) * 6);
        int first = -1;

        for (int i = 0; i < n; i++)
        {
            Vector2 p = pts[i];
            Vector2 nm = nrm[i];
            float nl = nm.Length();
            Vector2 nu = nl > 1e-4f ? nm / nl : new Vector2(0f, -1f);  // the unit normal: which way the paint's surface leans

            float a = arc[i];
            float hw = half0 * WidthScale(a, s.Seed, total, s.Closed);
            if (capStart) hw *= Cap(a, capLen);
            if (capEnd) hw *= Cap(visibleLen - a, capLen);
            hw = MathF.Max(0.01f, hw);

            // Local unevenness: a slow lean in the surface (paint is never flat), and thin or thick patches.
            float lean = Lean(a, s.Seed, total, s.Closed);
            float thickness = 0.5f + 0.5f * Noise.Along(a, 0.006f, s.Seed * 0.0029f, 2.3f, total, s.Closed);  // 0 = thin smear, 1 = pooled

            // A pulse of energy running through the sigil lights the paint from inside.
            float pulse = MathF.Max(0f, lum[i] - 0.82f);
            float glow = (0.07f + 0.55f * pulse) * (0.30f + 0.70f * agit) + 0.30f * agit * agit;

            for (int c = 0; c < Cols; c++)
            {
                float u = Across[c];
                bool edgeFringe = c == 0 || c == Cols - 1;
                float uc = Math.Clamp(u, -1f, 1f);

                // Surface normal in view space: a flattened tube across the ribbon, leaning with the paint's unevenness.
                float w = Math.Clamp(uc * Crown + lean, -0.985f, 0.985f);
                float nx = nu.X * w, ny = nu.Y * w;
                float nz = MathF.Sqrt(MathF.Max(0f, 1f - w * w));

                Vector3 col = Paint.Sample(nx, ny);
                float opacity = 0.99f;

                // Thin patches read brighter red; pooled ones go nearly black. The very edge is a darker bead: wet paint gathers at its rim.
                col *= 1.10f - 0.30f * thickness;
                col *= 1f - 0.22f * DrawHelpers.Smooth(0.80f, 1.0f, MathF.Abs(uc));

                // Light from a fire below, on the faces that point down; and the paint's own inner glow
                float below = MathF.Max(0f, ny);
                col += Underlight * (below * below * 0.34f * (0.55f + 0.45f * agit));
                float thinK = 0.55f + 0.45f * (1f - thickness);
                col += InnerGlow * (glow * thinK * (0.45f + 0.55f * (1f - nz)));

                // Distance haze: far paint is lifted toward the fog and loses contrast (the hero ring is near, so this is slight).
                if (haze > 0f) col = Vector3.Lerp(col, StrandShading.DepthFog, haze);

                float edge = edgeFringe ? 0f : 1f;
                uint color = DrawHelpers.Tint(DrawHelpers.Pack(col.X, col.Y, col.Z), MathF.Min(1f, opacity * alpha * near) * edge, overridden);

                // The offset normal already carries the miter, so the paint keeps its width round a bend.
                float off = edgeFringe ? Math.Sign(u) * (hw + fringe * soften) : u * hw;
                Vector2 pos = p + nm * off;
                int v = Mb.VertexReserved(pos, color);
                if (first < 0) first = v;
            }
        }

        for (int i = 0; i + 1 < n; i++)
        {
            int a0 = first + i * Cols, b0 = a0 + Cols;
            for (int c = 0; c + 1 < Cols; c++)
                Mb.QuadReserved(a0 + c, a0 + c + 1, b0 + c, b0 + c + 1);
        }
        Mb.Flush(dl, MeshDraw.WhiteUv(time));

        DrawGlint(dl, in s, in ctx, st, n, half0, alpha * near, overridden, capStart, capEnd, capLen, visibleLen);

        // The pen: a bead of fresh blood on a hot point, where the line is still being laid.
        if (reveal < 0.999f && s.TipFlare > 0.001f)
            DrawTip(dl, pts[n - 1], width, Math.Clamp(s.TipFlare, 0f, 1f) * alpha, in ctx, overridden);
    }

    /// <summary>Round end: the half-width falls away like a circle's over the last capLen px.</summary>
    private static float Cap(float distanceToEnd, float capLen)
    {
        if (distanceToEnd >= capLen) return 1f;
        float k = 1f - Math.Max(0f, distanceToEnd) / capLen;
        return MathF.Sqrt(MathF.Max(0f, 1f - k * k));
    }

    /// <summary>The wet highlights, each its own thin ribbon at the offset where it falls</summary>
    private static void DrawGlint(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx, LightStrand st, int n, float half0,
                                  float alpha, bool overridden, bool capStart, bool capEnd, float capLen, float visibleLen)
    {
        if (alpha <= 0.01f) return;
        float fade = alpha * (1f - 0.5f * Math.Clamp(s.Depth, 0f, 1f));
        Streak(dl, in s, in ctx, st, n, half0, fade * 0.95f * 1.15f, DrawHelpers.Pack(1.00f, 0.93f, 0.84f), KeyHalf, overridden, capStart, capEnd, capLen, visibleLen);
        Streak(dl, in s, in ctx, st, n, half0, fade * 0.85f * 0.42f, DrawHelpers.Pack(0.80f, 0.88f, 1.00f), FillHalf, overridden, capStart, capEnd, capLen, visibleLen);
    }

    /// <summary>One highlight along the strand.</summary>
    private static void Streak(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx, LightStrand st, int n, float half0,
                               float gain, uint rgb, Vector3 halfVec, bool overridden, bool capStart, bool capEnd, float capLen, float visibleLen)
    {
        const int StreakCols = 5;
        const float Exponent = 42f, Relax = 0.38f;
        Vector2 hxy = new(halfVec.X, halfVec.Y);
        float hz = halfVec.Z;
        float px = ctx.ScreenScale;

        Mb.Clear();
        Mb.Reserve(n * StreakCols, (n - 1) * (StreakCols - 1) * 6);
        int first = -1;
        bool any = false;

        for (int i = 0; i < n; i++)
        {
            Vector2 nm = st.Nrm[i];
            float nl = nm.Length();
            Vector2 nu = nl > 1e-4f ? nm / nl : new Vector2(0f, -1f);

            float a = st.Arc[i];
            float hw = half0 * WidthScale(a, s.Seed, s.Path.Length, s.Closed);
            if (capStart) hw *= Cap(a, capLen);
            if (capEnd) hw *= Cap(visibleLen - a, capLen);
            float lean = Lean(a, s.Seed, s.Path.Length, s.Closed);

            float ax = Vector2.Dot(nu, hxy);
            float root = MathF.Sqrt(ax * ax + hz * hz);
            float u = Math.Clamp((ax / root - lean) / Crown, -0.9f, 0.9f);
            float peak = 1f - Relax * (1f - root);
            float end = MathF.Min(1f, MathF.Min(i, n - 1 - i) / 2.5f);
            float ga = Math.Clamp(MathF.Pow(peak, Exponent) * gain, 0f, 1f) * end;
            if (ga > 0.004f) any = true;

            // A thin bright core inside a wider soft sheen.
            float gw = MathF.Max(0.8f * px, hw * 0.13f);
            Vector2 c = st.Pts[i] + nu * (u * hw);
            int v0 = Mb.VertexReserved(c - nu * (gw * 3.4f), DrawHelpers.Tint(rgb, 0f, overridden));
            Mb.VertexReserved(c - nu * gw, DrawHelpers.Tint(rgb, ga * 0.50f, overridden));
            Mb.VertexReserved(c, DrawHelpers.Tint(rgb, ga, overridden));
            Mb.VertexReserved(c + nu * gw, DrawHelpers.Tint(rgb, ga * 0.50f, overridden));
            Mb.VertexReserved(c + nu * (gw * 3.4f), DrawHelpers.Tint(rgb, 0f, overridden));
            if (first < 0) first = v0;
        }

        if (!any) { Mb.Clear(); return; }
        for (int i = 0; i + 1 < n; i++)
        {
            int a0 = first + i * StreakCols, b0 = a0 + StreakCols;
            for (int c = 0; c + 1 < StreakCols; c++) Mb.QuadReserved(a0 + c, a0 + c + 1, b0 + c, b0 + c + 1);
        }
        Mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));
    }

    /// <summary>A bright pip in a pool of ember light: the hot point of the pen that lays the paint down.</summary>
    private static void DrawTip(ImDrawListPtr dl, Vector2 tip, float width, float k, in MaterialContext ctx, bool overridden)
    {
        if (k <= 0.004f) return;
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        Span<float> rr = stackalloc float[2] { width * 0.55f, width * 1.9f };
        Span<uint> cc = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(HellColor.Glow.Sample(0.62f), 0.38f * k),
            DrawHelpers.WithAlpha(HellColor.Glow.Sample(0.30f), 0f),
        };
        MeshDraw.Radial(dl, tip, uv, DrawHelpers.WithAlpha(HellColor.Glow.Sample(1f), 0.95f * k), 12, rr, cc, 0f, 1f, 0, 0f);
        dl.AddCircleFilled(tip, MathF.Max(1.2f, width * 0.30f), DrawHelpers.WithAlpha(HellColor.Glow.Sample(0.55f), 0.95f * k));
        dl.AddCircleFilled(tip, MathF.Max(0.8f, width * 0.12f), DrawHelpers.WithAlpha(HellColor.Glow.Sample(1f), 0.95f * k));
    }

    // what it sheds, and what happens when it is hit

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    /// <summary>Runs, hanging off the underside of the paint and letting go.</summary>
    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Goop,
            DensityPer100px: 0f,
            SpeedMin: 70f, SpeedMax: 150f,
            LifespanMin: 2.0f, LifespanMax: 2.8f,
            SizeMin: 5.0f, SizeMax: 8.5f,
            SpreadRadians: 0f,
            BiasVelocity: Vector2.Zero,
            Gravity: new Vector2(0f, 1800f),
            Drip: new StrokeDripOptions(
                SiteSpacingPx: 120f,
                CycleSecondsMin: 1.8f, CycleSecondsMax: 3.6f,
                MinSlope: 0.40f,
                Stringiness: 0.50f,
                SatelliteChance: 0.30f)),
    };

    public ReadOnlySpan<ImpactEmission> ImpactEmissions => Hit;

    /// <summary>A splatter flung off the point of impact.</summary>
    private static readonly ImpactEmission[] Hit =
    {
        new(Role: PrimitiveRole.Goop, CountMin: 5, CountMax: 9,
            SpeedMin: 180f, SpeedMax: 520f, LifespanMin: 1.1f, LifespanMax: 1.8f,
            SizeMin: 2.2f, SizeMax: 5.0f, ConeRadians: 1.25f, Gravity: new Vector2(0f, 1800f)),
    };
}
