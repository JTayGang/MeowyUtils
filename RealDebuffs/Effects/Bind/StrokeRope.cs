using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>Laid rope: three twisted strands, one ribbon shaded via the shared matcap.</summary>
public sealed class StrokeRope : IStrokeMaterial, ICoatLayeredHost, ICoatSurfaceProvider
{
    public string Name => "stroke.rope";
    public string[] NaturalLanguageWords { get; } = { "rope", "ropes", "cord", "cords", "twine" };

    // size, as fractions of the short side
    private const float MinDiameterFrac = 0.006f;
    private const float MaxDiameterFrac = 0.045f;

    // the lay
    private const float LayLength   = 3.6f;  // distance for a strand to go once round the rope, in diameters
    private const float RidgeShape  = 0.10f;  // how far a ridge bulges the silhouette, as a fraction of the radius
    private const float RidgeTilt   = 0.62f;  // how hard a ridge tilts the shading normal (before detail scaling)
    private const float GrooveDark  = 0.42f;  // how dark the valleys between strands get

    // buffers
    private const int MaxRows = 1200;
    private const int MaxPathPoints = 512;

    private static readonly Matcap Hemp   = new(SurfacePresets.Hemp);
    private static readonly Matcap Manila = new(SurfacePresets.Manila);
    private static readonly Matcap Tarred = new(SurfacePresets.Tarred);

    // strand profile (baked): G = height by angle (1 crown, 0 valley), D = its slope
    private const int RidgeN = 512;
    private static readonly float[] RidgeH = new float[RidgeN];
    private static readonly float[] RidgeD = new float[RidgeN];

    static StrokeRope()
    {
        const float k = 0.80f;
        for (int i = 0; i < RidgeN; i++)
        {
            float phi = i / (float)RidgeN * MathF.Tau;
            float b = MathF.Max(0.03f, 0.5f + 0.5f * MathF.Cos(phi));
            RidgeH[i] = MathF.Pow(b, k);
            RidgeD[i] = k * MathF.Pow(b, k - 1f) * (-0.5f * MathF.Sin(phi));
        }
    }

    /// <summary>Level of detail: columns over the visible half (even in angle), rows along the length, and how much lay to show.</summary>
    private sealed class Tier
    {
        public readonly int Cols;
        public readonly float StepFrac;  // row spacing, in diameters
        public readonly float Detail;  // scales the ridge tilt
        public readonly float[] Turns3, Sin, Cos;

        public Tier(int cols, float stepFrac, float detail)
        {
            Cols = cols; StepFrac = stepFrac; Detail = detail;
            Turns3 = new float[cols]; Sin = new float[cols]; Cos = new float[cols];
            for (int c = 0; c < cols; c++)
            {
                float theta = (c / (cols - 1f) - 0.5f) * MathF.PI;  // -90..+90 degrees
                Turns3[c] = 3f * theta / MathF.Tau;  // pre-multiplied: the lay has three strands
                Sin[c] = MathF.Sin(theta);
                Cos[c] = MathF.Max(0f, MathF.Cos(theta));
            }
        }
    }

    // Tier by on-screen width (~4 columns per strand): >= 21 px Full, >= 15 Fine, >= 8 Medium, else Coarse.
    private static readonly Tier Full   = new(9, 0.21f, 1.00f);
    private static readonly Tier Fine   = new(7, 0.21f, 1.00f);
    private static readonly Tier Medium = new(5, 0.27f, 0.85f);
    private static readonly Tier Coarse = new(3, 0.42f, 0.55f);

    /// <summary>One cross-section of the rope: everything the vertices on it share.</summary>
    private struct Row
    {
        public Vector2 P, T;  // centreline and unit tangent
        public float Arc;  // distance along the path
        public float Psi;  // lay phase, in turns
        public float Width;  // radius multiplier: wobble along the length, and the rounded end caps
        public float CapW;  // 1 on the body; the dome's radius factor on a rounded end
        public float CapAxial;  // 0 on the body; how far the normal leans along the rope on a rounded end
        public float Tone0, Tone1, Tone2;  // brightness of each strand here: mottling x its tone x its fibre grain
        public float Feather;  // soft edge, in pixels
        public bool  On;  // any part of this cross-section can be on screen
    }

    private readonly Row[] _rows = new Row[MaxRows];
    private readonly PathRows _path = new(MaxPathPoints);

    /// <summary>Per-rope constants, so the vertex loop has one thing to be handed.</summary>
    private struct Look
    {
        public Matcap Body;
        public float Radius, Shape, Tilt, Groove;
        public float Fog, Agit;
        public float Grain, GrainRate;  // fibre grain: amplitude, and how fast it varies down a strand (1/px)
        public float GrainOffset;  // per-rope, so two ropes never share a grain
        public float TonePrimary, ToneSecondary, ToneTertiary;  // each strand is a slightly different batch of fibre
        public uint  AlphaBits;
        public float Alpha;
        public bool  Overridden;
        public float Dye;  // dye saturation under a chromatic override; 0 = the palette's own
    }

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx) => DrawCore(dl, in s, in ctx, null, 0f);

    public void DrawWithCoat(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx, Coat coat, float alpha) => DrawCore(dl, in s, in ctx, coat, alpha);

    private void DrawCore(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx, Coat? coat, float coatAlpha)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        if (path.Count < 2 || alpha <= 0.004f) return;

        int n = Prepare(in s, ctx.ScreenScale, ctx.ShortSide, ctx.Time, alpha, ctx.ScreenW, ctx.ScreenH,
                        DrawHelpers.ColorOverrideActive, DrawHelpers.ColorOverrideChroma, false,
                        out Tier tier, out Look look, out float visibleLen, out float diameter);
        if (n < 2) return;

        float depth = Math.Clamp(s.Depth, 0f, 1f);
        bool closed = s.Closed;
        float radius = diameter * 0.5f;

        StrandShading.DrawShadow(dl, path, diameter * 3f, depth, alpha, ctx, closed, visibleLen);

        DrawMesh(dl, n, tier, in look, ctx);

        if (coat != null) DrawFilm(dl, n, tier, in look, in ctx, coat, coatAlpha, closed, path.Length);

        if (s.TipFlare > 0.001f && !closed)
        {
            path.SampleAtArc(visibleLen, out Vector2 tip, out _);
            DrawKnot(dl, tip, radius * KnotSize, Math.Clamp(s.TipFlare, 0f, 1f), s.Seed, in look, ctx);
        }
    }

    /// <summary>Cuts the path into cross-sections (count; 0 = none) shared by drawing and the chart; allRows covers the whole rope, geometry only.</summary>
    private int Prepare(in StrokePrimitive s, float px, float shortSide, float time, float alpha, int screenW, int screenH, bool overridden, float dye, bool allRows,
                        out Tier tier, out Look look, out float visibleLen, out float diameter)
    {
        tier = Full; look = default; visibleLen = 0f; diameter = 0f;
        var path = s.Path;
        float reveal = Math.Clamp(s.Reveal, 0f, 1f);
        if (reveal <= 0.001f) return 0;

        float depth = Math.Clamp(s.Depth, 0f, 1f);
        float agit = Math.Clamp(s.Agitation, 0f, 1f);
        bool closed = s.Closed;
        int seed = s.Seed;

        diameter = Math.Clamp(s.WidthHint, shortSide * MinDiameterFrac, shortSide * MaxDiameterFrac)
                         * (1f - 0.18f * depth);
        float radius = diameter * 0.5f;

        float total = path.Length;
        visibleLen = total * reveal;
        if (visibleLen < diameter) return 0;

        // level of detail, by how many pixels the rope is wide
        tier = diameter >= 21f * px ? Full : (diameter >= 15f * px ? Fine : (diameter >= 8f * px ? Medium : Coarse));

        // this rope's own character, reseeded from the stroke's seed
        float pick = DrawHelpers.Hash01(seed + 41);
        Matcap body = pick < 0.55f ? Hemp : (pick < 0.85f ? Manila : Tarred);
        float layLen = diameter * LayLength * DrawHelpers.HashRange(seed + 43, 0.92f, 1.08f);
        float psi0 = DrawHelpers.Hash01(seed + 44);
        float ph1 = DrawHelpers.HashRange(seed + 45, 0f, MathF.Tau);
        float ph2 = DrawHelpers.HashRange(seed + 46, 0f, MathF.Tau);
        float ph3 = DrawHelpers.HashRange(seed + 47, 0f, MathF.Tau);

        float detail = tier.Detail * Math.Clamp(diameter / (16f * px), 0.5f, 1f);

        look = new Look
        {
            Body = body,
            Radius = radius,
            Shape = RidgeShape * detail,
            Tilt = RidgeTilt * detail,
            Groove = GrooveDark * (0.7f + 0.3f * detail) * (1f - 0.35f * depth),
            Fog = 0.55f * depth,
            Grain = 0.10f * detail,
            GrainRate = 1f / (diameter * 1.9f),
            GrainOffset = DrawHelpers.HashRange(seed + 48, 0f, 200f),
            TonePrimary = 1f,
            ToneSecondary = DrawHelpers.HashRange(seed + 49, 0.90f, 0.97f),
            ToneTertiary = DrawHelpers.HashRange(seed + 50, 1.02f, 1.08f),
            Agit = agit,
            Alpha = alpha,
            Overridden = overridden,
            Dye = dye,
            AlphaBits = DrawHelpers.AlphaBits(alpha),
        };

        _screenW = screenW;
        _screenH = screenH;

        // cut the rope into cross-sections
        return BuildRows(path, total, visibleLen, closed, s.FlushStart, diameter, radius, tier, layLen,
                          psi0, ph1, ph2, ph3, agit, time, px, depth, s.Twist / MathF.Tau, in look, allRows);
    }

    // ---- coat: one chart over the whole rope, cut from the drawn cross-sections and surface function

    // Always 12 sectors (multiple of six: three strands, alternating crowns/valleys), so MaxCells gives 48 rows and re-cuts keep the count.
    private const int Sectors = 12, MaxChartRows = Coat.MaxCells / Sectors;
    private const float RowStep = 0.6f;  // diameters per chart row, at the finest

    private readonly Dictionary<int, int> _rowsPrev = new();  // by stroke seed: the row count last frame, so a length that wobbles by a row does not re-cut the film every frame

    public bool Charts(CoatMatter matter) => matter is LiquidMatter;

    /// <summary>Liquid only: it is drawn as a film on the rope's own surface, between the body and the knot; other matters keep the generic band.</summary>
    public bool Layers(Coat coat) => coat.Matter is LiquidMatter && coat.Siblings != null && coat.V > 1;

    /// <summary>Body row offset for chart row r: ends always, the rest even; a closed rope's seam reuses its first row, so none repeats.</summary>
    private static int PickRow(int r, int rows, int bodyRows, bool closed)
        => closed ? (int)((long)r * bodyRows / rows) : (rows == 1 ? 0 : (int)MathF.Round(r * (bodyRows - 1f) / (rows - 1f)));

    /// <summary>Lay-frame chart: U along the rope, V angle phi, seen at theta = phi + tau * Psi, so cells turn with the strands under twist.</summary>
    public void BuildCoatSurfaces(in StrokePrimitive s, in CoatBuildContext ctx, CoatCharts charts)
    {
        if (s.Path.Count < 2) return;
        int n = Prepare(in s, ctx.Px, ctx.ShortSide, ctx.Time, 1f, (int)ctx.Screen.X, (int)ctx.Screen.Y, false, 0f, true, out _, out Look look, out _, out float diameter);
        if (n < 2) return;

        bool closed = s.Closed;
        float total = s.Path.Length;
        int nb = _bodyEnd - _bodyFirst - (closed ? 1 : 0);  // distinct body rows
        if (nb < 2) return;
        float bodyLen = closed ? total : _rows[_bodyEnd - 1].Arc - _rows[_bodyFirst].Arc;

        // rows the stroke's cell share affords, no finer than RowStep; a count within a fifth of last frame's is kept
        int V = Sectors, cap = Math.Min(Math.Min(MaxChartRows, charts.Share / V), nb);
        if (cap < 2) return;
        int want = Math.Clamp((int)MathF.Round(bodyLen / (RowStep * diameter)) + (closed ? 0 : 1), 2, cap);
        _rowsPrev.TryGetValue(s.Seed, out int last);
        int R = last >= 2 && last <= cap && want <= last * 5 / 4 && want >= last * 4 / 5 ? last : want;
        if (_rowsPrev.Count > 64) _rowsPrev.Clear();
        _rowsPrev[s.Seed] = R;
        var ch = charts.Add(1, R, V);
        if (ch == null) return;
        ch.SeamU = closed; ch.SeamV = true; ch.Live = R; ch.SupplyScale = 1f;

        Span<int> pick = stackalloc int[R];
        for (int r = 0; r < R; r++) { pick[r] = _bodyFirst + PickRow(r, R, nb, closed); ch.UPos[r] = _rows[pick[r]].Arc; }
        float len = 0f;
        for (int r = 0; r < R; r++)
        {
            float prev = r > 0 ? ch.UPos[r - 1] : (closed ? ch.UPos[R - 1] - total : ch.UPos[0]);
            float next = r < R - 1 ? ch.UPos[r + 1] : (closed ? ch.UPos[0] + total : ch.UPos[r]);
            ch.UStep[r] = 0.5f * (next - prev); len += ch.UStep[r];
        }
        ch.Ds = len / R;
        for (int v = 0; v < V; v++) { ch.VAngle[v] = MathF.Tau * v / V; ch.VStep[v] = look.Radius * MathF.Tau / V; }

        float margin = 20f * ctx.Px;
        for (int r = 0; r < R; r++)
        {
            ref readonly Row row = ref _rows[pick[r]];
            var f = new Frame(in row, in look);
            for (int v = 0; v < V; v++)
            {
                float phi = (float)v / V;  // body angle, in turns
                float theta = MathF.Tau * (phi + row.Psi);
                float sn = MathF.Sin(theta), cs = MathF.Cos(theta);
                ref var q = ref ch.S[r * V + v];
                Column(in f, 3f * phi + f.Psi3, sn, cs, out q.Pos, out float nx, out float ny, out float nz, out float g, out _);
                q.Normal = new Vector3(nx, ny, nz);
                q.Side = Math.Clamp(cs, -1f, 1f);
                q.Radius = f.Rad;
                q.Z = cs * f.Rad * (1f + f.Shape * (g - 0.5f));  // the surface point's depth, as its screen offset is sin theta
                float m = f.Rad + margin;
                q.Visible = ctx.Screen.X <= 0f || (q.Pos.X > -m && q.Pos.X < ctx.Screen.X + m && q.Pos.Y > -m && q.Pos.Y < ctx.Screen.Y + m);
                q.Holds = true;
                q.Anchor = q.Pos;
            }
        }

        // Du, Dv: screen direction of rising U, V scaled by the true 3D step, so flow sees the true slope (0 at the silhouette, eased by the lay).
        for (int r = 0; r < R; r++)
            for (int v = 0; v < V; v++)
            {
                ref var q = ref ch.S[r * V + v];
                int rp = r > 0 ? r - 1 : (closed ? R - 1 : r), rn = r < R - 1 ? r + 1 : (closed ? 0 : r);
                int vp = v > 0 ? v - 1 : V - 1, vn = v < V - 1 ? v + 1 : 0;
                q.Du = Slope(in ch.S[rp * V + v], in ch.S[rn * V + v], _rows[pick[r]].T);
                q.Dv = Slope(in ch.S[r * V + vp], in ch.S[r * V + vn], default);

                // a drop hangs where the surface faces down; an open rope's end that points down is the chart's downhill edge, as for a chain's
                float best = MathF.Max(0f, q.Normal.Y);
                if (!closed) { if (r == 0 && -q.Du.Y > best) best = -q.Du.Y; if (r == R - 1 && q.Du.Y > best) best = q.Du.Y; }
                q.GravityDown = best;
            }
    }

    private static Vector2 Slope(in CoatSurfaceSample a, in CoatSurfaceSample b, Vector2 fallback)
    {
        Vector2 d = b.Pos - a.Pos;
        float dz = b.Z - a.Z, len = MathF.Sqrt(d.X * d.X + d.Y * d.Y + dz * dz);
        return len > 1e-4f ? d / len : fallback;
    }

    /// <summary>Mesh standoff of a film: th up to 1.5x hold, then eased to maxDraw (deeper is a drop), so the underside throws no fins; shading keeps th.</summary>
    internal static float FilmOffset(float th, float hold, float maxDraw)
    {
        float knee = MathF.Min(1.5f * hold, 0.8f * maxDraw);
        if (th <= knee) return th;
        float room = maxDraw - knee;
        return knee + room * (1f - MathF.Exp(-(th - knee) / room));
    }

    /// <summary>The film: a second mesh on the rope's cross-sections and columns, thickness interpolated from the chart along and round the lay.</summary>
    private void DrawFilm(ImDrawListPtr dl, int rowCount, Tier tier, in Look look, in MaterialContext ctx, Coat coat, float coatAlpha, bool closed, float total)
    {
        if (coat.Matter is not LiquidMatter lm) return;
        int R = coat.N, V = coat.V;
        if (R < 2 || V != Sectors || R > MaxChartRows) return;
        float px = coat.Px;

        Span<float> thick = stackalloc float[R * V];
        float maxTh = 0f;
        for (int c = 0; c < R * V; c++) { thick[c] = coat.M[c] / coat.A[c]; maxTh = MathF.Max(maxTh, thick[c]); }
        if (maxTh < 0.25f * lm.Physics.Hold * px) return;

        int nb = _bodyEnd - _bodyFirst - (closed ? 1 : 0);
        if (nb < 2) return;
        Span<float> arc = stackalloc float[R];
        for (int r = 0; r < R; r++) arc[r] = _rows[_bodyFirst + PickRow(r, R, nb, closed)].Arc;

        int nc = tier.Cols;
        int chunkRows = MeshDraw.MaxVerts / nc;
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        float a = MathF.Min(1f, coatAlpha * ctx.Alpha);
        Span<float> rowTh = stackalloc float[V];
        float hold = lm.Physics.Hold * px, maxDraw = MathF.Min(lm.Physics.MaxThick, 2.5f * lm.Physics.Hold) * px;
        Vector2[] outP = MeshDraw.P;
        uint[] outC = MeshDraw.C;

        DrawHelpers.PushColorOverride(coat.Color);
        try
        {
            // the film varies slowly, so it needs about 0.42 diameters between rows, not the host's finest; the last row is always one
            int fs = Math.Max(1, (int)MathF.Round(0.42f / tier.StepFrac));
            int m = (rowCount - 1 + fs - 1) / fs + 1;
            int rr = 0;
            int j0 = 0;
            while (j0 < m - 1)
            {
                int j1 = Math.Min(j0 + chunkRows - 1, m - 1);
                bool any = false;
                for (int j = j0; j <= j1; j++) if (_rows[Math.Min(j * fs, rowCount - 1)].On) { any = true; break; }

                if (any)
                {
                    int vb = 0;
                    for (int j = j0; j <= j1; j++, vb += nc)
                    {
                        ref readonly Row row = ref _rows[Math.Min(j * fs, rowCount - 1)];
                        if (!row.On)
                        {
                            for (int c = 0; c < nc; c++) { outP[vb + c] = row.P; outC[vb + c] = 0u; }
                            continue;
                        }

                        // the chart rows either side of this cross-section, by arc
                        while (rr < R - 2 && row.Arc > arc[rr + 1]) rr++;
                        int ra = rr, rb = rr + 1;
                        float span = arc[rb] - arc[ra], t = span > 1e-4f ? Math.Clamp((row.Arc - arc[ra]) / span, 0f, 1f) : 0f;
                        if (closed && row.Arc > arc[R - 1])
                        {
                            ra = R - 1; rb = 0;
                            span = arc[0] + total - arc[R - 1];
                            t = span > 1e-4f ? Math.Clamp((row.Arc - arc[R - 1]) / span, 0f, 1f) : 0f;
                        }
                        for (int k = 0; k < V; k++) rowTh[k] = DrawHelpers.Lerp(thick[ra * V + k], thick[rb * V + k], t);

                        var f = new Frame(in row, in look);
                        for (int c = 0; c < nc; c++)
                        {
                            Column(in f, tier.Turns3[c], tier.Sin[c], tier.Cos[c], out Vector2 pos, out float nx, out float ny, out float nz, out _, out _);

                            float q = (tier.Turns3[c] / 3f - row.Psi) * V;  // this column's body angle, in sectors
                            q -= MathF.Floor(q / V) * V;
                            int k0 = Math.Min((int)q, V - 1), k1 = k0 + 1 == V ? 0 : k0 + 1;
                            float th = DrawHelpers.Lerp(rowTh[k0], rowTh[k1], q - k0);

                            outP[vb + c] = pos + new Vector2(nx, ny) * (FilmOffset(th, hold, maxDraw) * 0.9f);
                            outC[vb + c] = lm.FilmShade(new Vector3(nx, ny, nz), th, px, a, CoatDraw.Soft(tier.Sin[c], 0.6f));
                        }
                    }
                    MeshDraw.Grid(dl, nc - 1, j1 - j0, uv);
                }

                if (j1 == j0) break;
                j0 = j1;  // the seam row is shared, so chunks join exactly
            }
        }
        finally { DrawHelpers.PopColorOverride(); }
    }

    // the knot

    private const float KnotSize = 1.75f;  // the fist's radius, in rope radii
    private const int KnotSegs = 28;
    private static readonly float[] KnotRings = { 0f, 0.26f, 0.50f, 0.71f, 0.87f, 1.0f };

    /// <summary>Monkey's fist: a sphere of rope in three crossing coils, shaded through the same matcap.</summary>
    private static void DrawKnot(ImDrawListPtr dl, Vector2 centre, float radius, float k, int seed,
                                 in Look look, in MaterialContext ctx)
    {
        // The three coil axes: any three non-parallel directions look right, so turn the basis by a per-rope amount.
        var q = Quaternion.CreateFromYawPitchRoll(DrawHelpers.HashRange(seed + 61, 0f, MathF.Tau),
                                                  DrawHelpers.HashRange(seed + 62, 0f, MathF.Tau),
                                                  DrawHelpers.HashRange(seed + 63, 0f, MathF.Tau));
        Vector3 a1 = Vector3.Transform(Vector3.UnitX, q);
        Vector3 a2 = Vector3.Transform(Vector3.UnitY, q);
        Vector3 a3 = Vector3.Transform(Vector3.UnitZ, q);

        Vector2[] outP = MeshDraw.P;
        uint[] outC = MeshDraw.C;
        float alpha = look.Alpha * k;
        uint alphaBits = DrawHelpers.AlphaBits(alpha);

        int stride = KnotSegs + 1;
        int rings = KnotRings.Length;
        float feather = MathF.Max(1f, ctx.ScreenScale * (1f + 2.2f * look.Fog));

        for (int j = 0; j < rings; j++)
        {
            float rho = KnotRings[j];
            for (int i = 0; i <= KnotSegs; i++)
            {
                float a = MathF.Tau * i / KnotSegs;
                float ca = MathF.Cos(a), sa = MathF.Sin(a);
                int v = j * stride + i;
                outP[v] = centre + new Vector2(ca, sa) * (rho * radius);

                // A point on the sphere, as seen from the front.
                Vector3 n = new(rho * ca, rho * sa, MathF.Sqrt(MathF.Max(0f, 1f - rho * rho)));

                // Coil height: each axis wraps four bands round the ball; where coils cross, the higher one is on top.
                float h = MathF.Max(Coil(n, a1), MathF.Max(Coil(n, a2), Coil(n, a3)));
                Vector3 col = look.Body.Sample(n.X, n.Y) * (0.42f + 0.72f * h * h);
                if (look.Dye > 0f) col = DrawHelpers.WithSaturation(col, look.Dye);
                if (look.Agit > 0.01f) col += col * (col * (0.50f * look.Agit));
                if (look.Fog > 0f) col = Vector3.Lerp(col, StrandShading.DepthFog, look.Fog);

                outC[v] = DrawHelpers.VertexColor(col, alpha, alphaBits, look.Overridden);
            }
        }

        // Soft edge: one more ring, alpha 0, pushed out by the feather.
        for (int i = 0; i <= KnotSegs; i++)
        {
            int inner = (rings - 1) * stride + i;
            Vector2 dir = outP[inner] - centre;
            float len = dir.Length();
            Vector2 push = len > 1e-3f ? dir / len * feather : Vector2.Zero;
            outP[rings * stride + i] = outP[inner] + push;
            outC[rings * stride + i] = outC[inner] & 0x00FFFFFFu;
        }

        MeshDraw.Grid(dl, KnotSegs, rings, MeshDraw.WhiteUv(ctx.Time));
    }

    /// <summary>One coil's raised band at a point on the sphere: 1 along the crest of a wrap, 0 in the gap between.</summary>
    private static float Coil(Vector3 n, Vector3 axis)
    {
        float lat = MathF.Asin(Math.Clamp(Vector3.Dot(n, axis), -1f, 1f));
        return 0.5f + 0.5f * MathF.Cos(lat * 4f);
    }

    // cross-sections

    private int BuildRows(StrandPath path, float total, float visibleLen, bool closed, bool flushStart,
                          float diameter, float radius, Tier tier, float layLen,
                          float psi0, float ph1, float ph2, float ph3, float agit, float time, float px, float depth, float twistTurns, in Look look, bool allRows)
    {
        int pn = Math.Min(path.Count, MaxPathPoints);
        _path.Prepare(path, pn, closed);

        float step = MathF.Max(diameter * tier.StepFrac, 1.5f * px);
        float margin = diameter * 1.6f;

        // Closed ropes lay a whole number of turns and keep every periodic term to whole cycles, so the seam is invisible.
        float turns = closed ? MathF.Max(2f, MathF.Round(total / layLen)) : 0f;
        float layRate = closed ? turns / total : 1f / layLen;
        float wobbleRate = closed ? MathF.Max(1f, MathF.Round(total / (diameter * 6.1f))) / total : 1f / (diameter * 6.1f);
        float toneRateA  = closed ? MathF.Max(1f, MathF.Round(total / (diameter * 3.1f))) / total : 1f / (diameter * 3.1f);
        float toneRateB  = closed ? MathF.Max(1f, MathF.Round(total / (diameter * 1.7f))) / total : 1f / (diameter * 1.7f);

        // End finishes: closed has none; open always domes the far end, and the near end unless FlushStart.
        float cap = closed ? 0f : radius;
        bool domeStart = !closed && !flushStart;
        bool domeEnd = !closed;

        int count = 0;
        int seg = 0;

        // Body spacing, widened if the rope is so long it would overflow the buffer.
        float bodyStart = domeStart ? cap : 0f;
        float bodyEnd = domeEnd ? visibleLen - cap : visibleLen;
        step = MathF.Max(step, (bodyEnd - bodyStart) / (MaxRows - 16));

        if (domeStart)
        {
            // Tip first. t is how far up the dome: 1 at the very end, 0 where it meets the body.
            for (int i = 0; i < PathRows.DomeT.Length - 1; i++)
                AddRow(path, pn, ref seg, ref count, cap * (1f - PathRows.DomeT[i]), -1f, PathRows.DomeT[i]);
        }

        // The body; a closed loop's last row lands on its first (same position and lay) so the seam closes.
        int bodyRows = Math.Max(1, (int)MathF.Ceiling((bodyEnd - bodyStart) / step));
        _bodyFirst = count;
        for (int i = 0; i <= bodyRows; i++)
            AddRow(path, pn, ref seg, ref count, bodyStart + (bodyEnd - bodyStart) * i / bodyRows, 0f, 0f);
        _bodyEnd = count;

        if (domeEnd)
        {
            for (int i = PathRows.DomeT.Length - 2; i >= 0; i--)
                AddRow(path, pn, ref seg, ref count, visibleLen - cap * (1f - PathRows.DomeT[i]), +1f, PathRows.DomeT[i]);
        }

        // per-row shading inputs
        for (int i = 0; i < count; i++)
        {
            ref Row r = ref _rows[i];
            float sArc = r.Arc;

            Vector2 c = r.P;
            r.On = allRows || (c.X > -margin && c.Y > -margin && c.X < _screenW + margin && c.Y < _screenH + margin);
            if (!r.On) continue;  // everything below is only ever read for rows that get drawn

            float psi = psi0 + sArc * layRate;
            if (!closed) psi += 0.035f * MathF.Sin(MathF.Tau * sArc / (diameter * 7.3f) + ph1);  // a lay that isn't machine-perfect
            if (agit > 0.01f) psi += agit * 0.025f * MathF.Sin(time * 19f + sArc / diameter * 2.1f);
            // Twist: the whole rope turning about its axis. Fixed at both ends, it turns most in the middle.
            if (twistTurns != 0f) psi += twistTurns * (closed ? 1f : MathF.Sin(MathF.PI * sArc / total));
            r.Psi = psi;

            float wobble = 1f + 0.035f * MathF.Sin(MathF.Tau * sArc * wobbleRate + ph2);
            r.Width = wobble * r.CapW;
            if (allRows) continue;  // the chart reads only the lay and the width, not the shading inputs below

            float tone = 1f + 0.075f * MathF.Sin(MathF.Tau * sArc * toneRateA + ph3)
                            + 0.055f * MathF.Sin(MathF.Tau * sArc * toneRateB + ph1 * 1.7f);

            // Fibre grain streaks vary slowly along a strand, so one sample per strand per cross-section suffices.
            float along = sArc * look.GrainRate;
            r.Tone0 = tone * look.TonePrimary   * Grain(in look, 0, along);
            r.Tone1 = tone * look.ToneSecondary * Grain(in look, 1, along);
            r.Tone2 = tone * look.ToneTertiary  * Grain(in look, 2, along);

            // The soft edge breathes a little along the rope, which reads as fuzz rather than a hard cut.
            r.Feather = (1.0f + 2.2f * depth) * px * (1f + 0.45f * MathF.Sin(sArc * 0.37f + ph2));
        }
        return count;
    }

    /// <summary>Brightness multiplier from the fibre grain of one strand, along its length.</summary>
    private static float Grain(in Look look, int strand, float along)
        => 1f + look.Grain * 2f * (Noise.Value(strand * 11.7f + look.GrainOffset, along) - 0.5f);


    private float _screenW, _screenH;
    private int _bodyFirst, _bodyEnd;  // the body's rows among _rows: [first, end); a closed rope's last body row is its first again

    private void AddRow(StrandPath path, int pn, ref int seg, ref int count, float s, float endSign, float domeT)
    {
        if (count >= MaxRows) return;
        _path.SampleAt(path, pn, ref seg, s, out Vector2 p, out Vector2 t);

        ref Row r = ref _rows[count];
        r.Arc = s;
        r.P = p; r.T = t;
        PathRows.Cap(endSign, domeT, out r.CapW, out r.CapAxial);
        count++;
    }

    // the mesh

    private void DrawMesh(ImDrawListPtr dl, int rowCount, Tier tier, in Look look, in MaterialContext ctx)
    {
        int nc = tier.Cols;
        int stride = nc + 2;  // plus the two soft-edge columns
        int chunkRows = MeshDraw.MaxVerts / stride;  // MeshDraw's buffer holds only so many vertices at once
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);

        int r0 = 0;
        while (r0 < rowCount - 1)
        {
            int r1 = Math.Min(r0 + chunkRows - 1, rowCount - 1);

            bool any = false;
            for (int i = r0; i <= r1; i++) if (_rows[i].On) { any = true; break; }

            if (any)
            {
                int v = 0;
                for (int i = r0; i <= r1; i++)
                {
                    ShadeRow(in _rows[i], tier, in look, v);
                    v += stride;
                }
                MeshDraw.Grid(dl, stride - 1, r1 - r0, uv);
            }

            if (r1 == r0) break;
            r0 = r1;  // the seam row is shared, so chunks join exactly
        }
    }

    /// <summary>What every point of one cross-section shares: the frame its surface is cut in.</summary>
    private readonly struct Frame
    {
        public readonly Vector2 P, T, Perp;
        public readonly float Rad, Psi3, CapW, CapAxial, TiltAround, TiltAlong, Shape;

        public Frame(in Row row, in Look look)
        {
            P = row.P; T = row.T; Perp = new Vector2(-row.T.Y, row.T.X);
            Rad = look.Radius * row.Width; Psi3 = 3f * row.Psi; CapW = row.CapW; CapAxial = row.CapAxial;
            Shape = look.Shape;
            // Tilting the normal along the rope turns round ridges diagonal; relative to the tilt round it
            TiltAround = look.Tilt; TiltAlong = look.Tilt * (MathF.PI / LayLength);
        }
    }

    /// <summary>Rope surface at one angle: position, unit normal (z to viewer), strand height (1 crown, 0 valley), turn; u = sin, co = cos theta, turns3 = 3 theta/tau.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Column(in Frame f, float turns3, float u, float co, out Vector2 pos, out float nx, out float ny, out float nz, out float g, out float fl)
    {
        float x = turns3 - f.Psi3;
        fl = MathF.Floor(x);
        float fr = x - fl;
        int li = (int)(fr * RidgeN) & (RidgeN - 1);
        g = RidgeH[li];
        float gd = RidgeD[li];

        // Position: the silhouette is pushed in and out by the strand passing beneath it.
        pos = f.P + f.Perp * (u * (f.Rad * (1f + f.Shape * (g - 0.5f))));

        // Normal: r (the cylinder's) + tilt around (theta-hat) + tilt along (the tangent).
        float tAround = -f.TiltAround * gd;
        float tAlong = f.TiltAlong * gd;
        float a = (u + tAround * co) * f.CapW;
        nx = f.Perp.X * a + f.T.X * (tAlong + f.CapAxial);
        ny = f.Perp.Y * a + f.T.Y * (tAlong + f.CapAxial);
        nz = (co - tAround * u) * f.CapW;
        float inv = 1f / MathF.Sqrt(nx * nx + ny * ny + nz * nz + 1e-6f);
        nx *= inv; ny *= inv; nz *= inv;
    }

    private static void ShadeRow(in Row row, Tier tier, in Look look, int vb)
    {
        Vector2[] outP = MeshDraw.P;
        uint[] outC = MeshDraw.C;

        int nc = tier.Cols;
        var f = new Frame(in row, in look);
        Vector2 perp = f.Perp;
        Matcap body = look.Body;

        float[] turns3 = tier.Turns3, sinT = tier.Sin, cosT = tier.Cos;

        for (int c = 0; c < nc; c++)
        {
            Column(in f, turns3[c], sinT[c], cosT[c], out Vector2 pos, out float nx, out float ny, out _, out float g, out float fl);

            Vector3 col = body.Sample(nx, ny);

            // Valleys between strands are shadowed by the ridges either side of them.
            float vg = 1f - g;

            // Which strand this is: they come round in threes, and each is its own batch of fibre.
            int strand = ((int)fl % 3 + 3) % 3;
            float strandTone = strand == 0 ? row.Tone0 : (strand == 1 ? row.Tone1 : row.Tone2);

            col *= (1f - look.Groove * vg * vg) * strandTone;
            if (look.Dye > 0f) col = DrawHelpers.WithSaturation(col, look.Dye);

            if (look.Agit > 0.01f) col += col * (col * (0.50f * look.Agit));
            if (look.Fog > 0f) col = Vector3.Lerp(col, StrandShading.DepthFog, look.Fog);

            outP[vb + 1 + c] = pos;
            outC[vb + 1 + c] = DrawHelpers.VertexColor(col, look.Alpha, look.AlphaBits, look.Overridden);
        }

        // Soft edge: the neighbouring column's colour at alpha 0, pushed outward by the feather.
        outP[vb] = outP[vb + 1] - perp * row.Feather;
        outC[vb] = outC[vb + 1] & 0x00FFFFFFu;
        outP[vb + nc + 1] = outP[vb + nc] + perp * row.Feather;
        outC[vb + nc + 1] = outC[vb + nc] & 0x00FFFFFFu;
    }

    // what a rope sheds, and what happens when it is hit

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    /// <summary>Fibres working loose and a little dust sifting. Sparse on purpose.</summary>
    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Fibre,
            DensityPer100px: 0.25f,
            SpeedMin: 3f, SpeedMax: 14f,
            LifespanMin: 1.4f, LifespanMax: 2.8f,
            SizeMin: 7f, SizeMax: 14f,
            SpreadRadians: 1.4f,
            BiasVelocity: new Vector2(0f, 5f),
            PrimaryDirection: new Vector2(0f, 1f),
            Gravity: new Vector2(0f, 14f)),
        new(Role: PrimitiveRole.Dust,
            DensityPer100px: 0.07f,
            SpeedMin: 3f, SpeedMax: 14f,
            LifespanMin: 2.0f, LifespanMax: 3.6f,
            SizeMin: 5f, SizeMax: 10f,
            SpreadRadians: 1.2f,
            BiasVelocity: new Vector2(0f, 5f),
            PrimaryDirection: new Vector2(0f, 1f),
            Gravity: new Vector2(0f, 18f)),
    };

    public ReadOnlySpan<ImpactEmission> ImpactEmissions => Hit;

    /// <summary>No sparks: a puff of dust and a spray of fibres.</summary>
    private static readonly ImpactEmission[] Hit =
    {
        new(Role: PrimitiveRole.Fibre, CountMin: 7, CountMax: 13,
            SpeedMin: 70f, SpeedMax: 280f, LifespanMin: 0.9f, LifespanMax: 1.9f,
            SizeMin: 8f, SizeMax: 17f, ConeRadians: 1.3f, Gravity: new Vector2(0f, 110f)),
        new(Role: PrimitiveRole.Dust, CountMin: 2, CountMax: 4,
            SpeedMin: 30f, SpeedMax: 130f, LifespanMin: 0.9f, LifespanMax: 1.7f,
            SizeMin: 14f, SizeMax: 26f, ConeRadians: 1.5f, Gravity: new Vector2(0f, -14f)),
    };
}
