using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Laid rope: three strands twisted about one another, drawn as a single ribbon whose every vertex is
/// shaded as if it were a point on a real, round, ridged surface, through the shared matcap, so a
/// rope, a chain and a stone statue drawn in one frame appear to sit under the same lights.
///
/// THE SURFACE. The rope is a cylinder of radius R. Its three strands show as ridges that spiral
/// round it, one full turn every <see cref="LayLength"/> diameters. A ridge is a height field over
/// (angle round the axis, distance along it), so the normal at any vertex is the cylinder's own
/// normal tilted by that field's slope, both round the rope and along it. Going round: that is what
/// makes the strands read as round and separate. Going along: that is what makes them read as
/// diagonal. The same field nudges the silhouette in and out, so the rope's edge scallops
/// alternately on each side as the strands pass, the one cue that says "twisted" even on a thin rope.
///
/// Because it is a height field there is nothing to sort: strands occlude each other by construction
/// (only the front half of the cylinder is meshed), so unlike a chain the whole rope is one
/// mesh pass.
///
/// Stroke contract: any polyline; WidthHint is the rope's DIAMETER in pixels (clamped to a sensible
/// range, and slimmed a little by Depth); Closed wraps the seam and rounds the lay to a whole number
/// of turns so the strands join up; Reveal grows the rope from its start, ending in a rounded tip;
/// FlushStart gives the start a flat cut (it sits against a screen edge) instead of a rounded end;
/// Depth fogs, softens and slims; Agitation brightens the rope and shivers its strands; TipFlare
/// weights a live tip with a monkey's fist (the ball of wound rope on a heaving line), so a rope that
/// is flying or growing has a head.
///
/// Performance follows StrokeChain: tier tables are built once, scratch buffers are preallocated,
/// the path is walked with a cursor rather than searched, off-screen runs of the mesh are skipped,
/// vertices are written straight into the draw list, and the colour pack bypasses WithAlpha unless a
/// colour override is active.
/// </summary>
public sealed class StrokeRope : IStrokeMaterial
{
    public string Name => "stroke.rope";
    public string[] NaturalLanguageWords { get; } = { "rope", "ropes", "cord", "cords", "twine" };

    // ---- size, as fractions of the short side ----
    private const float MinDiameterFrac = 0.006f;
    private const float MaxDiameterFrac = 0.045f;

    // ---- the lay ----
    private const float LayLength   = 3.6f;    // distance for a strand to go once round the rope, in diameters
    private const float RidgeShape  = 0.10f;   // how far a ridge bulges the silhouette, as a fraction of the radius
    private const float RidgeTilt   = 0.62f;   // how hard a ridge tilts the shading normal (before detail scaling)
    private const float GrooveDark  = 0.42f;   // how dark the valleys between strands get

    // ---- buffers ----
    private const int MaxRows = 1200;
    private const int MaxPathPoints = 512;

    private static readonly Matcap Hemp   = new(SurfacePresets.Hemp);
    private static readonly Matcap Manila = new(SurfacePresets.Manila);
    private static readonly Matcap Tarred = new(SurfacePresets.Tarred);

    // ---- strand profile, baked ----
    // G(phi) is the height of the surface above the rope's core as a function of the angle round it,
    // in strand-periods: 1 at the crown of a strand, 0 in the valley between two. Rounded crowns,
    // narrow valleys. D(phi) is its slope, so a vertex costs one lookup rather than a trig call.
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

    /// <summary>
    /// A level of detail: how many columns span the visible half of the cylinder, how finely the rope
    /// is cut along its length, and how much of the lay is worth showing at that size. Columns are
    /// evenly spaced in ANGLE round the rope (not across its width), which puts vertices where the
    /// surface turns fastest, at the silhouette.
    /// </summary>
    private sealed class Tier
    {
        public readonly int Cols;
        public readonly float StepFrac;      // row spacing, in diameters
        public readonly float Detail;        // scales the ridge tilt
        public readonly float[] Turns3, Sin, Cos;

        public Tier(int cols, float stepFrac, float detail)
        {
            Cols = cols; StepFrac = stepFrac; Detail = detail;
            Turns3 = new float[cols]; Sin = new float[cols]; Cos = new float[cols];
            for (int c = 0; c < cols; c++)
            {
                float theta = (c / (cols - 1f) - 0.5f) * MathF.PI;   // -90..+90 degrees
                Turns3[c] = 3f * theta / MathF.Tau;                  // pre-multiplied: the lay has three strands
                Sin[c] = MathF.Sin(theta);
                Cos[c] = MathF.Max(0f, MathF.Cos(theta));
            }
        }
    }

    // Chosen by the rope's width on screen (see Draw): the lay needs about four columns per strand to read,
    // so a rope has to be wide enough to show them.  >= 21 px: Full.  >= 15: Fine.  >= 8: Medium.  Below: Coarse.
    private static readonly Tier Full   = new(9, 0.21f, 1.00f);
    private static readonly Tier Fine   = new(7, 0.21f, 1.00f);
    private static readonly Tier Medium = new(5, 0.27f, 0.85f);
    private static readonly Tier Coarse = new(3, 0.42f, 0.55f);

    /// <summary>One cross-section of the rope: everything the vertices on it share.</summary>
    private struct Row
    {
        public Vector2 P, T;        // centreline and unit tangent
        public float Arc;           // distance along the path
        public float Psi;           // lay phase, in turns
        public float Width;         // radius multiplier: wobble along the length, and the rounded end caps
        public float CapW;          // 1 on the body; the dome's radius factor on a rounded end
        public float CapAxial;      // 0 on the body; how far the normal leans along the rope on a rounded end
        public float Tone0, Tone1, Tone2;   // brightness of each of the three strands at this cross-section:
                                            // length-wise mottling x that strand's own tone x its fibre grain
        public float Feather;       // soft edge, in pixels
        public bool  On;            // any part of this cross-section can be on screen
    }

    private readonly Row[] _rows = new Row[MaxRows];
    private readonly Vector2[] _pointTan = new Vector2[MaxPathPoints];

    /// <summary>Per-rope constants, so the vertex loop has one thing to be handed.</summary>
    private struct Look
    {
        public Matcap Body;
        public float Radius, Shape, Tilt, Groove;
        public float Fog, Agit;
        public float Grain, GrainRate;          // fibre grain: amplitude, and how fast it varies down a strand (1/px)
        public float GrainOffset;               // per-rope, so two ropes never share a grain
        public float TonePrimary, ToneSecondary, ToneTertiary;   // each strand is a slightly different batch of fibre
        public uint  AlphaBits;
        public float Alpha;
        public bool  Overridden;
    }

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        if (path.Count < 2 || alpha <= 0.004f) return;

        float reveal = Math.Clamp(s.Reveal, 0f, 1f);
        if (reveal <= 0.001f) return;

        float px = ctx.ScreenScale;
        float depth = Math.Clamp(s.Depth, 0f, 1f);
        float agit = Math.Clamp(s.Agitation, 0f, 1f);
        bool closed = s.Closed;
        int seed = s.Seed;

        float diameter = Math.Clamp(s.WidthHint, ctx.ShortSide * MinDiameterFrac, ctx.ShortSide * MaxDiameterFrac)
                         * (1f - 0.18f * depth);
        float radius = diameter * 0.5f;

        float total = path.Length;
        float visibleLen = total * reveal;
        if (visibleLen < diameter) return;

        // ---- level of detail, by how many pixels the rope is wide ----
        Tier tier = diameter >= 21f * px ? Full : (diameter >= 15f * px ? Fine : (diameter >= 8f * px ? Medium : Coarse));

        // ---- this rope's own character, reseeded from the stroke's seed ----
        float pick = DrawHelpers.Hash01(seed + 41);
        Matcap body = pick < 0.55f ? Hemp : (pick < 0.85f ? Manila : Tarred);
        float layLen = diameter * LayLength * DrawHelpers.HashRange(seed + 43, 0.92f, 1.08f);
        float psi0 = DrawHelpers.Hash01(seed + 44);
        float ph1 = DrawHelpers.HashRange(seed + 45, 0f, MathF.Tau);
        float ph2 = DrawHelpers.HashRange(seed + 46, 0f, MathF.Tau);
        float ph3 = DrawHelpers.HashRange(seed + 47, 0f, MathF.Tau);

        float detail = tier.Detail * Math.Clamp(diameter / (16f * px), 0.5f, 1f);

        var look = new Look
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
            Overridden = DrawHelpers.ColorOverrideActive,
            AlphaBits = (uint)(int)(255f * (alpha > 0f ? (alpha < 1f ? alpha : 1f) : 0f)) << 24,
        };

        _screenW = ctx.ScreenW;
        _screenH = ctx.ScreenH;

        // ---- cut the rope into cross-sections ----
        int n = BuildRows(path, total, visibleLen, closed, s.FlushStart, diameter, radius, tier, layLen,
                          psi0, ph1, ph2, ph3, agit, ctx.Time, px, depth, in look);
        if (n < 2) return;

        StrandShading.DrawShadow(dl, path, diameter * 3f, depth, alpha, ctx, closed, visibleLen);

        DrawMesh(dl, n, tier, in look, ctx);

        if (s.TipFlare > 0.001f && !closed)
        {
            path.SampleAtArc(visibleLen, out Vector2 tip, out _);
            DrawKnot(dl, tip, radius * KnotSize, Math.Clamp(s.TipFlare, 0f, 1f), seed, in look, ctx);
        }
    }

    // ---- the knot ----

    private const float KnotSize = 1.75f;       // the fist's radius, in rope radii
    private const int KnotSegs = 28;
    private static readonly float[] KnotRings = { 0f, 0.26f, 0.50f, 0.71f, 0.87f, 1.0f };

    /// <summary>
    /// A monkey's fist: a sphere of rope wound in three crossing coils. Shaded as a true sphere through
    /// the same matcap as the rope (so it takes the same light) with the coils as raised bands about
    /// three skew axes, which is what makes it read as wound rather than as a plain bead.
    /// </summary>
    private static void DrawKnot(ImDrawListPtr dl, Vector2 centre, float radius, float k, int seed,
                                 in Look look, in MaterialContext ctx)
    {
        // The three coil axes: any three directions that aren't parallel look right, so turn the basis by a per-rope amount.
        var q = Quaternion.CreateFromYawPitchRoll(DrawHelpers.HashRange(seed + 61, 0f, MathF.Tau),
                                                  DrawHelpers.HashRange(seed + 62, 0f, MathF.Tau),
                                                  DrawHelpers.HashRange(seed + 63, 0f, MathF.Tau));
        Vector3 a1 = Vector3.Transform(Vector3.UnitX, q);
        Vector3 a2 = Vector3.Transform(Vector3.UnitY, q);
        Vector3 a3 = Vector3.Transform(Vector3.UnitZ, q);

        Vector2[] outP = MeshDraw.P;
        uint[] outC = MeshDraw.C;
        float alpha = look.Alpha * k;
        uint alphaBits = (uint)(int)(255f * Math.Clamp(alpha, 0f, 1f)) << 24;

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
                if (look.Agit > 0.01f) col += col * (col * (0.50f * look.Agit));
                if (look.Fog > 0f) col = Vector3.Lerp(col, StrandShading.DepthFog, look.Fog);

                uint rgb = FireColor.Pack(col.X, col.Y, col.Z);
                outC[v] = look.Overridden ? DrawHelpers.WithAlpha(rgb, alpha) : (rgb & 0x00FFFFFFu) | alphaBits;
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

    // ---- cross-sections ----

    private int BuildRows(StrandPath path, float total, float visibleLen, bool closed, bool flushStart,
                          float diameter, float radius, Tier tier, float layLen,
                          float psi0, float ph1, float ph2, float ph3, float agit, float time, float px, float depth, in Look look)
    {
        int pn = Math.Min(path.Count, MaxPathPoints);
        PreparePointTangents(path, pn, closed);

        float step = MathF.Max(diameter * tier.StepFrac, 1.5f * px);
        float margin = diameter * 1.6f;

        // Closed ropes lay a whole number of turns round the loop, and keep every periodic term to a
        // whole number of cycles, so the seam can't be told from anywhere else.
        float turns = closed ? MathF.Max(2f, MathF.Round(total / layLen)) : 0f;
        float layRate = closed ? turns / total : 1f / layLen;
        float wobbleRate = closed ? MathF.Max(1f, MathF.Round(total / (diameter * 6.1f))) / total : 1f / (diameter * 6.1f);
        float toneRateA  = closed ? MathF.Max(1f, MathF.Round(total / (diameter * 3.1f))) / total : 1f / (diameter * 3.1f);
        float toneRateB  = closed ? MathF.Max(1f, MathF.Round(total / (diameter * 1.7f))) / total : 1f / (diameter * 1.7f);

        // How the rope's two ends finish. Closed: they don't. Open: the far end is always a dome (a
        // growing or flying tip is meant to be seen); the near end is a dome unless it is FlushStart.
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
            for (int i = 0; i < DomeT.Length - 1; i++)
                AddRow(path, pn, ref seg, ref count, cap * (1f - DomeT[i]), -1f, DomeT[i]);
        }

        // The body. For a closed loop this is everything, and its last row lands on its first (same
        // position, same lay), so the seam closes.
        int bodyRows = Math.Max(1, (int)MathF.Ceiling((bodyEnd - bodyStart) / step));
        for (int i = 0; i <= bodyRows; i++)
            AddRow(path, pn, ref seg, ref count, bodyStart + (bodyEnd - bodyStart) * i / bodyRows, 0f, 0f);

        if (domeEnd)
        {
            for (int i = DomeT.Length - 2; i >= 0; i--)
                AddRow(path, pn, ref seg, ref count, visibleLen - cap * (1f - DomeT[i]), +1f, DomeT[i]);
        }

        // ---- per-row shading inputs ----
        for (int i = 0; i < count; i++)
        {
            ref Row r = ref _rows[i];
            float sArc = r.Arc;

            Vector2 c = r.P;
            r.On = c.X > -margin && c.Y > -margin && c.X < _screenW + margin && c.Y < _screenH + margin;
            if (!r.On) continue;                       // everything below is only ever read for rows that get drawn

            float psi = psi0 + sArc * layRate;
            if (!closed) psi += 0.035f * MathF.Sin(MathF.Tau * sArc / (diameter * 7.3f) + ph1);   // a lay that isn't machine-perfect
            if (agit > 0.01f) psi += agit * 0.025f * MathF.Sin(time * 19f + sArc / diameter * 2.1f);
            r.Psi = psi;

            float wobble = 1f + 0.035f * MathF.Sin(MathF.Tau * sArc * wobbleRate + ph2);
            r.Width = wobble * r.CapW;

            float tone = 1f + 0.075f * MathF.Sin(MathF.Tau * sArc * toneRateA + ph3)
                            + 0.055f * MathF.Sin(MathF.Tau * sArc * toneRateB + ph1 * 1.7f);

            // Fibre grain streaks along a strand: it varies slowly down the strand's length, so one sample per
            // strand per cross-section is all it takes (rather than one per vertex).
            float along = sArc * look.GrainRate;
            r.Tone0 = tone * look.TonePrimary   * Grain(in look, 0, along);
            r.Tone1 = tone * look.ToneSecondary * Grain(in look, 1, along);
            r.Tone2 = tone * look.ToneTertiary  * Grain(in look, 2, along);

            // The soft edge breathes a little along the rope, which reads as fuzz rather than a hard cut.
            r.Feather = (1.0f + 2.2f * depth) * px * (1f + 0.45f * MathF.Sin(sArc * 0.37f + ph2));
        }
        return count;
    }

    /// <summary>Brightness multiplier from the fibre grain of one strand, <paramref name="along"/> its length.</summary>
    private static float Grain(in Look look, int strand, float along)
        => 1f + look.Grain * 2f * (FireNoise.Value(strand * 11.7f + look.GrainOffset, along) - 0.5f);

    // Fraction up the dome for each end-cap row, tip last. 0 is where the cap meets the body.
    private static readonly float[] DomeT = { 1.0f, 0.95f, 0.80f, 0.50f, 0.0f };

    private float _screenW, _screenH;

    private void AddRow(StrandPath path, int pn, ref int seg, ref int count, float s, float endSign, float domeT)
    {
        if (count >= MaxRows) return;
        SampleAt(path, pn, ref seg, s, out Vector2 p, out Vector2 t);

        ref Row r = ref _rows[count];
        r.Arc = s;
        r.P = p; r.T = t;
        if (endSign == 0f)
        {
            r.CapW = 1f; r.CapAxial = 0f;
        }
        else
        {
            r.CapW = MathF.Sqrt(MathF.Max(0f, 1f - domeT * domeT));
            r.CapAxial = endSign * domeT;
        }
        count++;
    }

    /// <summary>
    /// Smooth tangents at every path point. Rows are placed by linear interpolation along the path
    /// and take their direction by interpolating these, so a coarse path (56 points over 1700 px)
    /// shades without facets at its vertices.
    /// </summary>
    private void PreparePointTangents(StrandPath path, int pn, bool closed)
    {
        var pts = path.Points;
        Vector2 last = new(0f, -1f);
        for (int i = 0; i < pn; i++)
        {
            Vector2 a = pts[i > 0 ? i - 1 : (closed ? pn - 2 : 0)];
            Vector2 b = pts[i < pn - 1 ? i + 1 : (closed ? 1 : pn - 1)];
            Vector2 d = b - a;
            float len = d.Length();
            if (len > 1e-4f) last = d / len;
            _pointTan[i] = last;
        }
    }

    /// <summary>Position and direction at arc length s, advancing a cursor (s only ever increases).</summary>
    private void SampleAt(StrandPath path, int pn, ref int seg, float s, out Vector2 p, out Vector2 t)
    {
        var arc = path.Arc;
        var pts = path.Points;
        while (seg < pn - 2 && arc[seg + 1] < s) seg++;

        float span = arc[seg + 1] - arc[seg];
        float f = span > 1e-5f ? Math.Clamp((s - arc[seg]) / span, 0f, 1f) : 0f;
        p = Vector2.Lerp(pts[seg], pts[seg + 1], f);

        Vector2 d = Vector2.Lerp(_pointTan[seg], _pointTan[seg + 1], f);
        float len = d.Length();
        t = len > 1e-4f ? d / len : _pointTan[seg];
    }

    // ---- the mesh ----

    private void DrawMesh(ImDrawListPtr dl, int rowCount, Tier tier, in Look look, in MaterialContext ctx)
    {
        int nc = tier.Cols;
        int stride = nc + 2;                         // plus the two soft-edge columns
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
            r0 = r1;                                  // the seam row is shared, so chunks join exactly
        }
    }

    private static void ShadeRow(in Row row, Tier tier, in Look look, int vb)
    {
        Vector2[] outP = MeshDraw.P;
        uint[] outC = MeshDraw.C;

        int nc = tier.Cols;
        Vector2 T = row.T;
        Vector2 perp = new(-T.Y, T.X);
        float rad = look.Radius * row.Width;
        float psi3 = 3f * row.Psi;
        float capW = row.CapW, capAxial = row.CapAxial;
        float tilt = look.Tilt, shape = look.Shape;
        Matcap body = look.Body;

        // Tilting the normal along the rope is what turns round ridges into diagonal ones. Relative to
        // the tilt round the rope it is the strands' pitch: (2 pi R / lay length) / 3 strands = pi / LayLength.
        float tiltAround = tilt;
        float tiltAlong = tilt * (MathF.PI / LayLength);

        float[] turns3 = tier.Turns3, sinT = tier.Sin, cosT = tier.Cos;

        for (int c = 0; c < nc; c++)
        {
            float x = turns3[c] - psi3;
            float fl = MathF.Floor(x);
            float f = x - fl;
            int li = (int)(f * RidgeN) & (RidgeN - 1);
            float g = RidgeH[li], gd = RidgeD[li];

            float u = sinT[c], co = cosT[c];

            // Position: the silhouette is pushed in and out by the strand passing beneath it.
            Vector2 pos = row.P + perp * (u * (rad * (1f + shape * (g - 0.5f))));

            // Normal: r (the cylinder's) + tilt around (theta-hat) + tilt along (the tangent).
            float tAround = -tiltAround * gd;
            float tAlong = tiltAlong * gd;
            float a = (u + tAround * co) * capW;
            float nx = perp.X * a + T.X * (tAlong + capAxial);
            float ny = perp.Y * a + T.Y * (tAlong + capAxial);
            float nz = (co - tAround * u) * capW;
            float inv = 1f / MathF.Sqrt(nx * nx + ny * ny + nz * nz + 1e-6f);

            Vector3 col = body.Sample(nx * inv, ny * inv);

            // Valleys between strands are shadowed by the ridges either side of them.
            float vg = 1f - g;

            // Which strand this is: they come round in threes, and each is its own batch of fibre.
            int strand = ((int)fl % 3 + 3) % 3;
            float strandTone = strand == 0 ? row.Tone0 : (strand == 1 ? row.Tone1 : row.Tone2);

            col *= (1f - look.Groove * vg * vg) * strandTone;

            if (look.Agit > 0.01f) col += col * (col * (0.50f * look.Agit));
            if (look.Fog > 0f) col = Vector3.Lerp(col, StrandShading.DepthFog, look.Fog);

            outP[vb + 1 + c] = pos;
            uint rgb = FireColor.Pack(col.X, col.Y, col.Z);
            outC[vb + 1 + c] = look.Overridden ? DrawHelpers.WithAlpha(rgb, look.Alpha) : (rgb & 0x00FFFFFFu) | look.AlphaBits;
        }

        // Soft edge: the neighbouring column's colour at alpha 0, pushed outward by the feather.
        outP[vb] = outP[vb + 1] - perp * row.Feather;
        outC[vb] = outC[vb + 1] & 0x00FFFFFFu;
        outP[vb + nc + 1] = outP[vb + nc] + perp * row.Feather;
        outC[vb + nc + 1] = outC[vb + nc] & 0x00FFFFFFu;
    }

    // ---- what a rope sheds, and what happens when it is hit ----

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
