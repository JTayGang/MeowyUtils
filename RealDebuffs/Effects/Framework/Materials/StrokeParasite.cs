using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A living tentacle, drawn as lit 3D geometry: a tapered tube with a pale belly and a dark back,
/// mottled skin, two rows of suckers, and a coat of thick slime that flows down it, gathers into lumps,
/// and drips.
///
/// It is a hero material in the same sense as <see cref="StrokeRope"/> and <see cref="StrokeChain"/>:
/// it honours the whole stroke contract (Reveal, FlushStart, Closed, Depth, Agitation, TipFlare, Twist),
/// so anything that can be a rope or a chain can be a tentacle. Closed, it is a ring of living flesh with
/// no end to find (a magic circle); on a strand pinned at both ends it is a tentacle hauled taut between
/// two edges.
///
/// How it gets its volume. Each cross-section is a ribbon of vertices evenly spaced in angle across the
/// visible half of a cylinder. Each vertex has an analytic normal (the cylinder's, tilted along the tube
/// by a slow pitch toward or away from the viewer, and by the slime lumps) and is coloured by looking
/// that normal up in two shared <see cref="Matcap"/>s: satin flesh, and a clear wet coat laid over it.
/// A hard highlight is narrower than the vertex spacing, so it is drawn analytically as a glint strip
/// instead. The suckers are small foreshortened cups that are only visible while the belly faces the
/// viewer, so as the strand twists they sweep into and out of sight.
///
/// Drips are not drawn here. The material only declares them (see <see cref="Emissions"/>); the shared
/// <see cref="GoopEmitter"/> grows them on the underside, using <see cref="RadiusAt"/> to find the skin.
/// </summary>
public sealed class StrokeParasite : IStrokeMaterial
{
    public string Name => "stroke.parasite";
    public string[] NaturalLanguageWords { get; } = { "tentacle", "tentacles", "tendril", "tendrils", "parasite", "parasites" };

    // ---- size ----
    // Thick enough to read as flesh with suckers on it, thin enough that it is still a limb and not a trunk.
    private const float MinDiameterFrac = 0.012f;
    private const float MaxDiameterFrac = 0.060f;
    private const float TipFrac = 0.075f;               // diameter at the very tip, as a fraction of the base's
    private const int   MaxRows = 1000;
    private const int   MaxPathPoints = 512;

    private static readonly Matcap Flesh = new(SurfacePresets.ParasiteFlesh);
    private static readonly Matcap Coat  = new(SurfacePresets.WetCoat);

    // Back and belly: multipliers on the flesh matcap. The belly is paler and yellower, the back darker and greener.
    private static readonly Vector3 DorsalTint  = new(0.72f, 0.80f, 0.70f);
    private static readonly Vector3 VentralTint = new(1.22f, 1.14f, 0.92f);

    /// <summary>The half vector between the key light and the viewer: where a highlight sits on a surface.</summary>
    private static readonly Vector3 KeyHalf = Vector3.Normalize(StudioLighting.Key + Vector3.UnitZ);
    private static readonly Vector3 FillHalf = Vector3.Normalize(StudioLighting.Fill + Vector3.UnitZ);

    /// <summary>
    /// A level of detail: columns across the visible half of the tube, how finely it is cut along its
    /// length, and whether suckers are worth drawing at that size. Columns are spaced evenly in ANGLE, which
    /// puts vertices where the surface turns fastest, at the silhouette.
    /// </summary>
    private sealed class Tier
    {
        public readonly int Cols;
        public readonly float StepFrac;      // row spacing, in diameters
        public readonly int SuckerSegs;      // 0 = no suckers at this size
        public readonly float[] Sin, Cos, Theta;

        public Tier(int cols, float stepFrac, int suckerSegs)
        {
            Cols = cols; StepFrac = stepFrac; SuckerSegs = suckerSegs;
            Sin = new float[cols]; Cos = new float[cols]; Theta = new float[cols];
            for (int c = 0; c < cols; c++)
            {
                float th = MathF.PI * (c / (cols - 1f) - 0.5f);
                Theta[c] = th; Sin[c] = MathF.Sin(th); Cos[c] = MathF.Cos(th);
            }
            Sin[0] = -1f; Sin[cols - 1] = 1f; Cos[0] = 0f; Cos[cols - 1] = 0f;
        }
    }

    private static readonly Tier Full   = new(11, 0.20f, 14);
    private static readonly Tier Fine   = new(9,  0.24f, 11);
    private static readonly Tier Medium = new(7,  0.32f, 0);
    private static readonly Tier Coarse = new(5,  0.46f, 0);

    /// <summary>Everything about how this particular tentacle looks, resolved once per Draw.</summary>
    private struct Look
    {
        public float Diameter, Radius;
        public float Fog, Agit;
        public float Alpha;
        public uint  AlphaBits;
        public bool  Overridden;
        public float Dye;
        public float Detail;                    // 0..1: how much of the fine skin work is worth doing at this size
        public float MottleFreq;                // 1/px along the strand
        public float MottleOffset;              // per tentacle, so no two share a pattern
    }

    /// <summary>One cross-section of the tentacle: everything a vertex needs that does not depend on which column it is.</summary>
    private struct Row
    {
        public Vector2 P, T;
        public float Arc;
        public float CapW, CapAxial;            // end-cap shaping, as in Rope: width factor and axial normal
        public float Radius;                    // half-width in px, all effects included
        public float Psi;                       // angle of the belly line from the view axis
        public float SinA, CosA;                // pitch toward (+) or away (-) from the viewer
        public float Tone;                      // slow brightness drift along the strand
        public float Wet;                       // 0..1 slime coverage
        public float LumpTilt;                  // axial normal tilt from the slime lumps
        public float Feather;
        public float Glint, GlintX;             // key-light highlight strip on this row: strength, and where across the tube (px from the centre line)
        public float Glint2, GlintX2;           // the same for the cooler fill light
        public float MX, MY;                    // skin-pattern coordinates
        public bool  On;                        // on screen: only these rows are shaded
    }

    private readonly Row[] _rows = new Row[MaxRows];
    private readonly Vector2[] _pointTan = new Vector2[MaxPathPoints];
    private float _screenW, _screenH;

    // =========================================================================
    // The strand's profile: shared by the drawing and by RadiusAt.
    // =========================================================================

    private static float BaseDiameter(float widthHint, float depth, float shortSide) =>
        Math.Clamp(widthHint, shortSide * MinDiameterFrac, shortSide * MaxDiameterFrac) * (1f - 0.18f * depth);

    /// <summary>Radius multiplier at fraction u of the strand's full length: thick at the root, drawn out to a fine tip.</summary>
    private static float Taper(float u, bool closed) =>
        closed ? 0.92f : TipFrac + (1f - TipFrac) * MathF.Pow(Math.Clamp(1f - u, 0f, 1f), 0.78f);

    /// <inheritdoc/>
    public float RadiusAt(in StrokePrimitive s, float arc, float shortSide)
    {
        float total = MathF.Max(1f, s.Path.Length);
        float diameter = BaseDiameter(s.WidthHint, Math.Clamp(s.Depth, 0f, 1f), shortSide);
        return MathF.Max(0.5f, 0.5f * diameter * Taper(arc / total, s.Closed));
    }

    // =========================================================================
    // Draw
    // =========================================================================

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

        float diameter = BaseDiameter(s.WidthHint, depth, ctx.ShortSide);
        float radius = diameter * 0.5f;
        float total = path.Length;
        float visibleLen = total * reveal;
        if (visibleLen < diameter * 0.8f) return;

        Tier tier = diameter >= 22f * px ? Full : (diameter >= 15f * px ? Fine : (diameter >= 9f * px ? Medium : Coarse));

        var look = new Look
        {
            Diameter = diameter,
            Radius = radius,
            Fog = 0.55f * depth,
            Agit = agit,
            Alpha = alpha,
            AlphaBits = (uint)(int)(255f * (alpha > 0f ? (alpha < 1f ? alpha : 1f) : 0f)) << 24,
            Overridden = DrawHelpers.ColorOverrideActive,
            Dye = DrawHelpers.ColorOverrideChroma,
            Detail = Math.Clamp(diameter / (22f * px), 0.4f, 1f),
            MottleFreq = 1f / (diameter * 2.3f),
            MottleOffset = DrawHelpers.HashRange(seed + 71, 0f, 300f),
        };

        _screenW = ctx.ScreenW;
        _screenH = ctx.ScreenH;

        int n = BuildRows(path, total, visibleLen, closed, s.FlushStart, reveal, tier, seed, s.Phase,
                          s.Twist, ctx.Time, px, depth, in look);
        if (n < 2) return;

        DrawShadow(dl, n, depth, alpha, in ctx);
        DrawBody(dl, n, tier, in look, in ctx);
        if (tier.SuckerSegs > 0) DrawSuckers(dl, n, tier, in look, in ctx, visibleLen, total, closed);
        DrawGlints(dl, n, in look, in ctx);

        if (s.TipFlare > 0.001f && !closed)
        {
            path.SampleAtArc(visibleLen, out Vector2 tip, out _);
            DrawTip(dl, tip, MathF.Max(radius * Taper(reveal, false), 1.6f * px), Math.Clamp(s.TipFlare, 0f, 1f), in look, in ctx);
        }
    }

    // =========================================================================
    // Cross-sections
    // =========================================================================

    private int BuildRows(StrandPath path, float total, float visibleLen, bool closed, bool flushStart, float reveal,
                          Tier tier, int seed, float phase, float twist, float time, float px, float depth, in Look look)
    {
        int pn = Math.Min(path.Count, MaxPathPoints);
        PreparePointTangents(path, pn, closed);

        float diameter = look.Diameter, radius = look.Radius;
        float step = MathF.Max(diameter * tier.StepFrac, 1.5f * px);
        float margin = diameter * 1.6f;

        // ---- how this tentacle moves and looks, reseeded from its seed ----
        float ph1 = DrawHelpers.HashRange(seed + 81, 0f, MathF.Tau);
        float ph2 = DrawHelpers.HashRange(seed + 82, 0f, MathF.Tau);
        float ph3 = DrawHelpers.HashRange(seed + 83, 0f, MathF.Tau);
        float psi0 = DrawHelpers.HashRange(seed + 84, 0f, MathF.Tau);
        float offA = DrawHelpers.HashRange(seed + 85, 0f, 400f);
        float offB = DrawHelpers.HashRange(seed + 86, 0f, 400f);
        float offC = DrawHelpers.HashRange(seed + 87, 0f, 400f);

        // Which way slime runs: down the screen, so toward whichever end is lower.
        path.SampleAtArc(0f, out Vector2 basePos, out _);
        path.SampleAtArc(total, out Vector2 tipPos, out _);
        float flowSign = closed || tipPos.Y >= basePos.Y ? 1f : -1f;
        float scroll = flowSign * (time + phase * 3.1f) * 24f * px;

        // Closed loops keep every periodic term to a whole number of cycles, so the seam cannot be found.
        float wobbleRate  = closed ? MathF.Max(1f, MathF.Round(total / (diameter * 6.4f))) / total : 1f / (diameter * 6.4f);
        float toneRateA   = closed ? MathF.Max(1f, MathF.Round(total / (diameter * 4.3f))) / total : 1f / (diameter * 4.3f);
        float toneRateB   = closed ? MathF.Max(1f, MathF.Round(total / (diameter * 9.1f))) / total : 1f / (diameter * 9.1f);
        float pitchRate   = closed ? MathF.Max(1f, MathF.Round(total / (diameter * 13f)))  / total : 1f / (diameter * 13f);
        float twistRate   = closed ? MathF.Max(1f, MathF.Round(total / (diameter * 40f)))  / total : 1f / (diameter * 40f);

        // Ends. Open: the far end is always a dome (a growing tip is meant to be seen); the near one is flush if told to be.
        float capEnd = closed ? 0f : radius * Taper(reveal, false);
        float capStart = closed ? 0f : radius;
        bool domeStart = !closed && !flushStart;
        bool domeEnd = !closed;

        float bodyStart = domeStart ? capStart : 0f;
        float bodyEnd = domeEnd ? visibleLen - capEnd : visibleLen;
        step = MathF.Max(step, (bodyEnd - bodyStart) / (MaxRows - 16));

        int count = 0, seg = 0;
        if (domeStart)
            for (int i = 0; i < DomeT.Length - 1; i++)
                AddRow(path, pn, ref seg, ref count, capStart * (1f - DomeT[i]), -1f, DomeT[i]);

        int bodyRows = Math.Max(1, (int)MathF.Ceiling((bodyEnd - bodyStart) / step));
        for (int i = 0; i <= bodyRows; i++)
            AddRow(path, pn, ref seg, ref count, bodyStart + (bodyEnd - bodyStart) * i / bodyRows, 0f, 0f);

        if (domeEnd)
            for (int i = DomeT.Length - 2; i >= 0; i--)
                AddRow(path, pn, ref seg, ref count, visibleLen - capEnd * (1f - DomeT[i]), +1f, DomeT[i]);

        // How far through its growth the tip still is: the front of a growing tentacle is drawn out to a point.
        float growing = 1f - Smooth((reveal - 0.90f) / 0.10f);

        for (int i = 0; i < count; i++)
        {
            ref Row r = ref _rows[i];
            float sArc = r.Arc;

            Vector2 c = r.P;
            r.On = c.X > -margin && c.Y > -margin && c.X < _screenW + margin && c.Y < _screenH + margin;
            if (!r.On) continue;

            float u = total > 1f ? sArc / total : 0f;

            // ---- width: taper, a slow muscular swell, slime lumps, and perspective ----
            float swell = 1f + (0.045f + 0.03f * look.Agit) * MathF.Sin(MathF.Tau * sArc * wobbleRate - time * (1.7f + 3f * look.Agit) + ph1);

            float lumpField = Smooth((Field(sArc, total, closed, diameter * 2.4f, offA, scroll * 0.8f) - 0.46f) / 0.28f);
            float wetField  = 0.55f + 0.45f * Smooth((Field(sArc, total, closed, diameter * 3.3f, offB, scroll) - 0.34f) / 0.34f);
            float lump = lumpField * wetField;

            float pitch = (0.40f + 0.22f * look.Agit) * MathF.Sin(MathF.Tau * sArc * pitchRate + ph2 + 0.55f * time);
            r.SinA = MathF.Sin(pitch); r.CosA = MathF.Cos(pitch);
            float perspective = 1f + 0.18f * r.SinA;

            float tipThin = 1f;
            if (growing > 0.001f && !closed)
            {
                float fromTip = (visibleLen - sArc) / MathF.Max(1f, radius * 4.5f);
                tipThin = 1f - 0.5f * growing * (1f - Smooth(fromTip));
            }

            r.Radius = radius * Taper(u, closed) * swell * (1f + 0.16f * lump) * perspective * tipThin * r.CapW;
            r.Wet = wetField;

            // Lump gradient tilts the normal along the tube so a lump shades as a bulge, not a stripe.
            float h = diameter * 0.6f;
            float lumpAhead = Smooth((Field(sArc + h, total, closed, diameter * 2.4f, offA, scroll * 0.8f) - 0.46f) / 0.28f);
            float lumpBehind = Smooth((Field(sArc - h, total, closed, diameter * 2.4f, offA, scroll * 0.8f) - 0.46f) / 0.28f);
            r.LumpTilt = -(lumpAhead - lumpBehind) * 0.85f * wetField;

            // ---- the belly line: turns slowly down the strand, drifts with time, and follows Twist ----
            float psi = psi0 + MathF.Tau * sArc * twistRate + 0.45f * MathF.Sin(0.31f * time + ph3);
            if (twist != 0f) psi += twist * (closed ? 1f : MathF.Sin(MathF.PI * u));
            if (look.Agit > 0.01f) psi += look.Agit * 0.05f * MathF.Sin(time * 17f + MathF.Tau * sArc * toneRateA * 0.5f);
            r.Psi = psi;

            r.Tone = 1f + 0.07f * MathF.Sin(MathF.Tau * sArc * toneRateA + ph3) + 0.04f * MathF.Sin(MathF.Tau * sArc * toneRateB + ph1);
            r.Feather = (1.0f + 2.2f * depth) * px * (1f + 0.4f * MathF.Sin(MathF.Tau * sArc * toneRateB + ph2));

            // ---- highlight strips: where each light's reflection falls on this stretch of tube ----
            float breakup = Smooth((Field(sArc, total, closed, diameter * 1.7f, offC, scroll * 1.2f) - 0.30f) / 0.30f);
            Glint(KeyHalf, r.T, r.SinA, r.CosA, r.Radius, breakup, wetField, 9f, out r.Glint, out r.GlintX);
            float breakup2 = Smooth((Field(sArc, total, closed, diameter * 2.1f, offC + 91f, scroll * 0.9f) - 0.34f) / 0.30f);
            Glint(FillHalf, r.T, r.SinA, r.CosA, r.Radius, breakup2, wetField, 7f, out r.Glint2, out r.GlintX2);

            // ---- skin pattern coordinates (periodic on a closed loop) ----
            if (closed)
            {
                float a = MathF.Tau * sArc / total;
                float rr = total * look.MottleFreq / MathF.Tau;
                r.MX = rr * MathF.Cos(a) + look.MottleOffset;
                r.MY = rr * MathF.Sin(a);
            }
            else
            {
                r.MX = sArc * look.MottleFreq + look.MottleOffset;
                r.MY = 0f;
            }
        }
        return count;
    }

    /// <summary>
    /// Where a light's reflection falls on a tube, and how strongly. A cylinder reflects a light along a
    /// line, at the angle where its normal meets the half vector; that line is brightest where the tube runs
    /// square to the light and fades as it turns toward it, which is why a glossy tentacle's highlight
    /// breaks up as it curves. Returns the strength and the offset across the tube from its centre line.
    /// </summary>
    private static void Glint(Vector3 half, Vector2 t, float sinA, float cosA, float radius, float breakup, float wet,
                              float sharpness, out float strength, out float across)
    {
        float along = half.X * t.X + half.Y * t.Y;
        float hT3 = along * cosA + half.Z * sinA;
        float hPerp = MathF.Sqrt(MathF.Max(0f, 1f - hT3 * hT3));
        if (hPerp <= 0.05f) { strength = 0f; across = 0f; return; }

        float hAcross = half.X * -t.Y + half.Y * t.X;
        across = Math.Clamp(hAcross / hPerp, -0.95f, 0.95f) * radius;
        strength = MathF.Pow(hPerp, sharpness) * (0.25f + 0.75f * breakup) * wet;
    }

    /// <summary>
    /// A slow 0..1 field along the strand, scrolling at <paramref name="scroll"/> px: what slime does as it
    /// creeps downhill. On a closed loop it is sampled round a circle, so it has no seam.
    /// </summary>
    private static float Field(float sArc, float total, bool closed, float scale, float offset, float scroll)
    {
        float s = sArc - scroll;
        if (!closed) return FireNoise.Value(s / scale + offset, offset * 0.37f);
        float a = MathF.Tau * s / total;
        float rr = total / (MathF.Tau * scale);
        return FireNoise.Value(rr * MathF.Cos(a) + offset, rr * MathF.Sin(a) + offset * 0.37f);
    }

    // Fraction up the dome for each end-cap row, tip last. 0 is where the cap meets the body.
    private static readonly float[] DomeT = { 1.0f, 0.95f, 0.80f, 0.50f, 0.0f };

    private void AddRow(StrandPath path, int pn, ref int seg, ref int count, float s, float endSign, float domeT)
    {
        if (count >= MaxRows) return;
        SampleAt(path, pn, ref seg, s, out Vector2 p, out Vector2 t);

        ref Row r = ref _rows[count];
        r.Arc = s;
        r.P = p; r.T = t;
        if (endSign == 0f) { r.CapW = 1f; r.CapAxial = 0f; }
        else
        {
            r.CapW = MathF.Sqrt(MathF.Max(0f, 1f - domeT * domeT));
            r.CapAxial = endSign * domeT;
        }
        count++;
    }

    /// <summary>
    /// Smooth tangents at every path point; rows interpolate them, so a coarse path shades without
    /// facets at its vertices. (Same scheme as Rope.)
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

    // =========================================================================
    // Shadow: a soft band under the tentacle, narrowing with it
    // =========================================================================

    private static readonly float[] ShadowAcross = { -1f, -0.55f, 0f, 0.55f, 1f };
    private static readonly float[] ShadowProfile = { 0f, 0.62f, 1f, 0.62f, 0f };

    private void DrawShadow(ImDrawListPtr dl, int rowCount, float depth, float alpha, in MaterialContext ctx)
    {
        float a = alpha * 0.42f * (1f - 0.45f * depth);
        if (a <= 0.004f) return;

        const int stride = 5;
        int chunkRows = MeshDraw.MaxVerts / stride;
        Vector2 offsetDir = StudioLighting.ShadowDirection;
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
                    ref readonly Row row = ref _rows[i];
                    Vector2 nrm = new(-row.T.Y, row.T.X);
                    float reach = row.Radius * 1.25f * (1f - 0.55f * depth);
                    float hw = row.Radius * 0.95f;
                    float soft = hw * (0.9f + 1.1f * depth);
                    float edge = MathF.Min(1f, MathF.Min(i, rowCount - 1 - i) / 1.5f);

                    for (int c = 0; c < stride; c++)
                    {
                        float w = ShadowAcross[c];
                        MeshDraw.P[v] = row.P + offsetDir * reach + nrm * (w * (hw + soft * 0.5f * MathF.Abs(w)));
                        MeshDraw.C[v] = DrawHelpers.WithAlpha(StrandShading.ShadowTint, a * ShadowProfile[c] * edge);
                        v++;
                    }
                }
                MeshDraw.Grid(dl, stride - 1, r1 - r0, uv);
            }

            if (r1 == r0) break;
            r0 = r1;
        }
    }

    // =========================================================================
    // Body
    // =========================================================================

    private void DrawBody(ImDrawListPtr dl, int rowCount, Tier tier, in Look look, in MaterialContext ctx)
    {
        int stride = tier.Cols + 2;                      // plus the two soft-edge columns
        int chunkRows = MeshDraw.MaxVerts / stride;
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
            r0 = r1;                                     // the seam row is shared, so chunks join exactly
        }
    }

    private static void ShadeRow(in Row row, Tier tier, in Look look, int vb)
    {
        Vector2[] outP = MeshDraw.P;
        uint[] outC = MeshDraw.C;

        int nc = tier.Cols;
        Vector2 T = row.T;
        Vector2 perp = new(-T.Y, T.X);
        float rad = row.Radius;
        float capW = row.CapW, capAxial = row.CapAxial;
        float sa = row.SinA, ca = row.CosA;
        float[] sinT = tier.Sin, cosT = tier.Cos, theta = tier.Theta;
        float detail = look.Detail;

        for (int c = 0; c < nc; c++)
        {
            float u = sinT[c], co = cosT[c];
            outP[vb + 1 + c] = row.P + perp * (u * rad);

            // Normal: the cross-section's own (round the tube), tilted along it by pitch, the end cap and the slime lumps.
            float across = u * capW;
            float along = capAxial + (row.LumpTilt - sa * co) * capW;
            float nx = perp.X * across + T.X * along;
            float ny = perp.Y * across + T.Y * along;
            float nz = ca * co * capW;
            float inv = 1f / MathF.Sqrt(nx * nx + ny * ny + nz * nz + 1e-6f);
            nx *= inv; ny *= inv;

            Vector3 col = Flesh.Sample(nx, ny);

            // Position on the tentacle's own surface, measured from its belly line. This turns with Twist.
            float phi = theta[c] + row.Psi;
            float belly = MathF.Cos(phi);
            Vector3 tint = Vector3.Lerp(DorsalTint, VentralTint, Smooth((belly + 0.2f) / 1.0f));
            col *= tint * row.Tone;

            // Mottling: broad blotches and a few dark freckles, painted on the skin (so they ride round with the twist).
            if (detail > 0.45f)
            {
                // Round the tube, not along an unwrapped angle: noise is not periodic in phi, so a belly line that has
                // turned a whole number of times (a closed loop's seam) would otherwise jump to a different pattern.
                float mx = row.MX + MathF.Cos(phi) * 1.15f, my = row.MY + MathF.Sin(phi) * 1.15f;
                float m = FireNoise.Value(mx, my);
                float freckle = Smooth((FireNoise.Value(mx * 3.6f + 17f, my * 3.6f + 9f) - 0.72f) / 0.14f);
                col *= 1f + (m - 0.5f) * 0.38f * detail - freckle * 0.20f * detail;
            }

            // The slime coat: a clear gloss over the flesh, thicker and shinier on the underside.
            float wet = row.Wet * (0.80f + 0.20f * ny);
            Vector3 gloss = Coat.Sample(nx, ny);
            col = col * (1f - 0.10f * wet);
            col = col + gloss * wet - col * gloss * wet;     // screen: gloss lightens without clipping

            if (look.Dye > 0f) col = DrawHelpers.WithSaturation(col, look.Dye);
            if (look.Agit > 0.01f) col += col * (col * (0.45f * look.Agit));
            if (look.Fog > 0f) col = Vector3.Lerp(col, StrandShading.DepthFog, look.Fog);

            uint rgb = FireColor.Pack(col.X, col.Y, col.Z);
            outC[vb + 1 + c] = look.Overridden ? DrawHelpers.WithAlpha(rgb, look.Alpha) : (rgb & 0x00FFFFFFu) | look.AlphaBits;
        }

        // Soft edge: the neighbouring column's colour at alpha 0, pushed outward by the feather.
        outP[vb] = outP[vb + 1] - perp * row.Feather;
        outC[vb] = outC[vb + 1] & 0x00FFFFFFu;
        outP[vb + nc + 1] = outP[vb + nc] + perp * row.Feather;
        outC[vb + nc + 1] = outC[vb + nc] & 0x00FFFFFFu;
    }

    // =========================================================================
    // Suckers
    // =========================================================================

    private const float SuckerRadiusFrac = 0.60f;       // disc radius, in tube radii
    private const float SuckerRowAngle   = 0.66f;       // each row sits this far (rad) either side of the belly line
    private const float SuckerPitch      = 1.22f;       // spacing along a row, in tube diameters

    // Rings of a sucker, centre outward. A cup is a torus: a floor, a steep inner wall, a raised rim, and an
    // outer flank that blends back into the skin. Each ring says how far the surface tilts there (+ outward,
    // - toward the centre) and how deep in shadow it sits. The last ring is the soft edge.
    private static readonly float[] CupRho    = { 0.00f, 0.28f, 0.50f, 0.68f, 0.79f, 0.93f, 1.10f, 1.28f };
    private static readonly float[] CupTilt   = { 0.00f, -0.20f, -0.80f, -0.60f, 0.00f, 0.95f, 0.55f, 0.15f };
    private static readonly float[] CupShadow = { 0.34f, 0.34f, 0.38f, 0.16f, 0.00f, 0.00f, 0.12f, 0.00f };
    private static readonly Vector3 CupTint = new(1.00f, 0.80f, 0.86f);      // the inside of a cup is a pinker, softer flesh

    /// <summary>
    /// Two rows of cups along the belly. Each is a disc lying on the tube's surface, foreshortened by how
    /// far round the tube it is (so it narrows as it turns away) and shaded by a cup-shaped normal field: a
    /// bright raised rim, a dark steep inner wall, a glossy floor. A cup is only drawn while it faces the viewer,
    /// and since the belly line turns with Twist, cups sweep into and out of view as the tentacle twists.
    /// </summary>
    private void DrawSuckers(ImDrawListPtr dl, int rowCount, Tier tier, in Look look, in MaterialContext ctx,
                             float visibleLen, float total, bool closed)
    {
        int segs = tier.SuckerSegs;
        int rings = CupRho.Length;
        int stride = segs + 1;
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        float px = ctx.ScreenScale;

        if (closed)
        {
            // A whole number of pitches round the loop, so the pattern closes on itself.
            float diameterHere = 2f * _rows[0].Radius;
            if (diameterHere < 10f * px) return;
            int count = Math.Max(4, (int)MathF.Round(total / (SuckerPitch * diameterHere)));
            float pitch = total / count;
            for (int k = 0; k < count; k++)
            {
                float sA = k * pitch, sB = sA + pitch * 0.5f;
                int ia = FindRow(rowCount, sA), ib = FindRow(rowCount, sB >= total ? sB - total : sB);
                if (_rows[ia].On) DrawCup(dl, in _rows[ia], +SuckerRowAngle, segs, rings, stride, uv, in look);
                if (_rows[ib].On) DrawCup(dl, in _rows[ib], -SuckerRowAngle, segs, rings, stride, uv, in look);
            }
            return;
        }

        float s = look.Diameter * 1.6f;
        for (int guard = 0; guard < 400 && s < visibleLen - look.Diameter * 0.5f; guard++)
        {
            int ri = FindRow(rowCount, s);
            float diameterHere = 2f * _rows[ri].Radius;
            if (diameterHere < 10f * px) break;                          // too small to be worth a cup, and so is everything beyond
            float pitch = SuckerPitch * diameterHere;

            if (_rows[ri].On) DrawCup(dl, in _rows[ri], +SuckerRowAngle, segs, rings, stride, uv, in look);

            // The second row is half a pitch behind the first, on the other side of the belly line.
            float sB = s + pitch * 0.5f;
            if (sB < visibleLen - look.Diameter * 0.5f)
            {
                int rj = FindRow(rowCount, sB);
                if (_rows[rj].On) DrawCup(dl, in _rows[rj], -SuckerRowAngle, segs, rings, stride, uv, in look);
            }
            s += pitch;
        }
    }

    /// <summary>The row whose arc length is nearest s (rows are in arc order).</summary>
    private int FindRow(int rowCount, float s)
    {
        int lo = 0, hi = rowCount - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_rows[mid].Arc < s) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && s - _rows[lo - 1].Arc < _rows[lo].Arc - s) lo--;
        return lo;
    }

    private static void DrawCup(ImDrawListPtr dl, in Row row, float beltAngle, int segs, int rings, int stride,
                                Vector2 uv, in Look look)
    {
        // Where this cup is round the tube, as an angle from the view axis. Beyond about 80 degrees it is behind the silhouette.
        float theta = WrapAngle(beltAngle - row.Psi);
        float co = MathF.Cos(theta);
        if (co < 0.12f) return;
        float vis = Smooth((co - 0.12f) / 0.26f);
        float sn = MathF.Sin(theta);

        Vector2 T = row.T;
        Vector2 perp = new(-T.Y, T.X);
        float sa = row.SinA, ca = row.CosA;

        // The cup's frame in view space: nrm out of the skin, e1 along the tube, e2 across it.
        Vector3 nrm = new(perp.X * sn - T.X * sa * co, perp.Y * sn - T.Y * sa * co, ca * co);
        Vector3 e1 = new(T.X * ca, T.Y * ca, sa);
        Vector3 e2 = Vector3.Cross(nrm, e1);

        float rCup = SuckerRadiusFrac * row.Radius;
        Vector2 centre = row.P + perp * (sn * row.Radius);

        Vector2[] outP = MeshDraw.P;
        uint[] outC = MeshDraw.C;
        float alphaBase = look.Alpha * vis;

        for (int j = 0; j < rings; j++)
        {
            float rho = CupRho[j] * rCup;
            float tilt = CupTilt[j];
            float shadow = CupShadow[j];
            bool edge = j == rings - 1;

            for (int i = 0; i <= segs; i++)
            {
                float a = MathF.Tau * i / segs;
                float ca2 = MathF.Cos(a), sa2 = MathF.Sin(a);
                Vector3 radial = e1 * ca2 + e2 * sa2;
                int v = j * stride + i;
                outP[v] = centre + new Vector2(radial.X, radial.Y) * rho;

                Vector3 n = Vector3.Normalize(nrm + radial * tilt);
                Vector3 col = Flesh.Sample(n.X, n.Y);

                col *= VentralTint * 1.06f * row.Tone;
                col *= Vector3.Lerp(Vector3.One, CupTint, shadow * 2f) * (1f - 0.70f * shadow);

                Vector3 gloss = Coat.Sample(n.X, n.Y);
                float wet = MathF.Max(0.55f, row.Wet);
                col = col + gloss * wet - col * gloss * wet;

                if (look.Dye > 0f) col = DrawHelpers.WithSaturation(col, look.Dye);
                if (look.Agit > 0.01f) col += col * (col * (0.45f * look.Agit));
                if (look.Fog > 0f) col = Vector3.Lerp(col, StrandShading.DepthFog, look.Fog);

                uint rgb = FireColor.Pack(col.X, col.Y, col.Z);
                float al = edge ? 0f : alphaBase;
                outC[v] = look.Overridden
                    ? DrawHelpers.WithAlpha(rgb, al)
                    : (rgb & 0x00FFFFFFu) | ((uint)(int)(255f * Math.Clamp(al, 0f, 1f)) << 24);
            }
        }
        MeshDraw.Grid(dl, segs, rings - 1, uv);
    }

    private static float WrapAngle(float a)
    {
        a %= MathF.Tau;
        if (a > MathF.PI) a -= MathF.Tau;
        else if (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    // =========================================================================
    // Glints
    // =========================================================================

    /// <summary>
    /// A thin hard highlight along the tube where the key light's reflection falls. It is far narrower than
    /// the body mesh's vertex spacing, so it is its own ribbon, laid over the body. It breaks up along the
    /// strand (slime is not an even film) and fades wherever the tube is turned away from the reflection.
    /// </summary>
    private void DrawGlints(ImDrawListPtr dl, int rowCount, in Look look, in MaterialContext ctx)
    {
        // The key light's reflection is the hard warm one; the fill's is dimmer, cooler and broader.
        DrawGlintStrip(dl, rowCount, in look, in ctx, fill: false, FireColor.Pack(1.00f, 0.97f, 0.86f));
        DrawGlintStrip(dl, rowCount, in look, in ctx, fill: true,  FireColor.Pack(0.82f, 0.93f, 1.00f));
    }

    private void DrawGlintStrip(ImDrawListPtr dl, int rowCount, in Look look, in MaterialContext ctx, bool fill, uint tint)
    {
        const int stride = 5;
        int chunkRows = MeshDraw.MaxVerts / stride;
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        float px = ctx.ScreenScale;
        float fogK = 1f - 0.7f * look.Fog;
        float power = fill ? 0.55f : 1.0f;

        int r0 = 0;
        while (r0 < rowCount - 1)
        {
            int r1 = Math.Min(r0 + chunkRows - 1, rowCount - 1);
            bool any = false;
            for (int i = r0; i <= r1; i++)
                if (_rows[i].On && (fill ? _rows[i].Glint2 : _rows[i].Glint) > 0.03f) { any = true; break; }

            if (any)
            {
                int v = 0;
                for (int i = r0; i <= r1; i++)
                {
                    ref readonly Row row = ref _rows[i];
                    Vector2 perp = new(-row.T.Y, row.T.X);
                    float g = row.On ? (fill ? row.Glint2 : row.Glint) * row.CapW * fogK : 0f;
                    float x = fill ? row.GlintX2 : row.GlintX;
                    float core = MathF.Max(0.75f * px, row.Radius * (fill ? 0.14f : 0.10f));
                    float broad = row.Radius * (fill ? 0.66f : 0.55f);
                    Vector2 mid = row.P + perp * x;

                    // Across: nothing, a faint sheen, the hard core, the sheen again, nothing.
                    float aSheen = Math.Clamp(0.32f * g * power, 0f, 1f) * look.Alpha;
                    float aCore = Math.Clamp((1.10f * g * g + 0.30f * g) * power, 0f, 1f) * look.Alpha;
                    MeshDraw.P[v] = mid - perp * broad;         MeshDraw.C[v] = GlintColour(tint, 0f, in look); v++;
                    MeshDraw.P[v] = mid - perp * (core * 1.3f); MeshDraw.C[v] = GlintColour(tint, aSheen, in look); v++;
                    MeshDraw.P[v] = mid;                        MeshDraw.C[v] = GlintColour(tint, aCore, in look); v++;
                    MeshDraw.P[v] = mid + perp * (core * 1.3f); MeshDraw.C[v] = GlintColour(tint, aSheen, in look); v++;
                    MeshDraw.P[v] = mid + perp * broad;         MeshDraw.C[v] = GlintColour(tint, 0f, in look); v++;
                }
                MeshDraw.Grid(dl, stride - 1, r1 - r0, uv);
            }

            if (r1 == r0) break;
            r0 = r1;
        }
    }

    private static uint GlintColour(uint rgb, float alpha, in Look look) =>
        look.Overridden
            ? DrawHelpers.WithAlpha(rgb, alpha)
            : (rgb & 0x00FFFFFFu) | ((uint)(int)(255f * Math.Clamp(alpha, 0f, 1f)) << 24);

    // =========================================================================
    // Tip
    // =========================================================================

    private static void DrawTip(ImDrawListPtr dl, Vector2 tip, float bar, float k, in Look look, in MaterialContext ctx)
    {
        if (k <= 0.004f) return;
        // A bead of slime on the end of a tentacle that is still reaching: a hard pinprick glint and the faintest sheen round it.
        uint halo = DrawHelpers.WithAlpha(FireColor.Pack(0.82f, 0.95f, 0.55f), 0.11f * k * look.Alpha);
        uint clear = DrawHelpers.WithAlpha(FireColor.Pack(0.82f, 0.95f, 0.55f), 0f);
        uint pip = DrawHelpers.WithAlpha(FireColor.Pack(1.00f, 1.00f, 0.92f), 0.90f * k * look.Alpha);
        Span<float> rr = stackalloc float[2] { bar * 0.9f, bar * 2.3f };
        Span<uint> cc = stackalloc uint[2] { halo, clear };
        MeshDraw.Radial(dl, tip, MeshDraw.WhiteUv(ctx.Time), pip, 10, rr, cc, 0f, 1f, 0, 0f);
    }

    private static float Smooth(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // =========================================================================
    // What a tentacle sheds, and what happens when it is hit
    // =========================================================================

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    /// <summary>Slime, hanging off the underside in long stretching strings. Heavier than a rope's dust: this is the point of it.</summary>
    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Goop,
            DensityPer100px: 0f,
            SpeedMin: 60f, SpeedMax: 140f,
            LifespanMin: 2.4f, LifespanMax: 3.2f,
            SizeMin: 4.6f, SizeMax: 7.6f,
            SpreadRadians: 0f,
            BiasVelocity: Vector2.Zero,
            Gravity: new Vector2(0f, 1500f),
            Drip: new StrokeDripOptions(
                SiteSpacingPx: 92f,
                CycleSecondsMin: 2.6f, CycleSecondsMax: 5.0f,
                MinSlope: 0.40f,
                Stringiness: 0.85f,
                SatelliteChance: 0.55f)),

        // Beads of slime creeping along the skin: the film gathering into drops on its way down.
        new(Role: PrimitiveRole.Goop,
            DensityPer100px: 0.60f,
            SpeedMin: 0f, SpeedMax: 0f,
            LifespanMin: 4.5f, LifespanMax: 8f,
            SizeMin: 2.8f, SizeMax: 5.2f,
            SpreadRadians: 0f,
            BiasVelocity: Vector2.Zero,
            Flow: new StrokeFlowOptions(
                Share: 1f,
                SpeedMin: 16f, SpeedMax: 42f,
                WobbleAmplitude: 0.8f,
                WobbleFrequencyHz: 0.35f,
                ObstacleSpacingPx: 60f,
                LateralOffsetFrac: 0.50f)),
    };

    public ReadOnlySpan<ImpactEmission> ImpactEmissions => Hit;

    /// <summary>A splatter of slime flung off the point of impact.</summary>
    private static readonly ImpactEmission[] Hit =
    {
        new(Role: PrimitiveRole.Goop, CountMin: 4, CountMax: 7,
            SpeedMin: 150f, SpeedMax: 470f, LifespanMin: 1.3f, LifespanMax: 2.0f,
            SizeMin: 2.8f, SizeMax: 5.8f, ConeRadians: 1.25f, Gravity: new Vector2(0f, 1500f)),
    };
}
