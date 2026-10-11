using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>A living tentacle as lit 3D geometry: tapered tube, pale belly, dark back, mottled skin, flowing slime, and two rows of real 3D suckers placed in material coordinates so they ride the skin steadily. Honours the full stroke contract, so anything that can be a rope or chain can be a tentacle. Drips come from GoopEmitter via RadiusAt.</summary>
public sealed class StrokeParasite : IStrokeMaterial
{
    public string Name => "stroke.parasite";
    public string[] NaturalLanguageWords { get; } = { "tentacle", "tentacles", "tendril", "tendrils", "parasite", "parasites" };

    // ---- size ---- wide enough for a limb a sixth of the screen thick, narrow enough to be a thread
    private const float MinDiameterFrac = 0.012f;
    private const float MaxDiameterFrac = 0.180f;
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

    /// <summary>Level of detail: columns across the visible half (evenly spaced in ANGLE, putting vertices at the silhouette), rows along the length, and the finest a cup is cut (a smaller cup is cut coarser).</summary>
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

    private static readonly Tier Thick  = new(17, 0.20f, 24);          // a limb thick enough that 11 columns would show as blocks
    private static readonly Tier Full   = new(11, 0.20f, 16);
    private static readonly Tier Fine   = new(9,  0.24f, 12);
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

    /// <summary>Per-tentacle, per-frame constants a cross-section needs. Kept apart from the rows so one can be evaluated at ANY arc length: a sucker sits exactly where it belongs, not on the nearest row.</summary>
    private struct RowParams
    {
        public float Total, VisibleLen, Reveal, Growing;
        public bool Closed;
        public float Diameter, Radius, Px, Depth, Time, Twist, Margin, ScreenW, ScreenH;
        public float FieldScale;                              // size of the slime patches and highlight breakup, capped in pixels
        public float Ph1, Ph2, Ph3, Psi0, OffA, OffB, OffC, Scroll;
        public float WobbleRate, ToneRateA, ToneRateB, PitchRate, TwistRate;
    }

    private RowParams _rp;
    private readonly Row[] _rows = new Row[MaxRows];
    private readonly Vector2[] _pointTan = new Vector2[MaxPathPoints];
    private float _screenW, _screenH;

    // ---- The strand's profile: shared by the drawing and by RadiusAt ----

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

    // ---- Draw ----

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

        Tier tier = diameter >= 70f * px ? Thick : (diameter >= 22f * px ? Full : (diameter >= 15f * px ? Fine : (diameter >= 9f * px ? Medium : Coarse)));

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
            MottleFreq = 1f / MathF.Min(diameter * 2.3f, 100f * px),   // skin detail is a size in pixels, not a fraction of the limb: a thicker arm has more skin, not bigger pores
            MottleOffset = DrawHelpers.HashRange(seed + 71, 0f, 300f),
        };

        _screenW = ctx.ScreenW;
        _screenH = ctx.ScreenH;

        int n = BuildRows(path, total, visibleLen, closed, s.FlushStart, reveal, tier, seed, s.Phase,
                          s.Twist, ctx.Time, px, depth, in look);
        if (n < 2) return;

        int cups = tier.SuckerSegs > 0 ? BuildSuckers(path, tier, in look, px) : 0;
        Vector2 cupUv = MeshDraw.WhiteUv(ctx.Time);

        DrawShadow(dl, n, depth, alpha, in ctx);
        if (cups > 0) FlushCups(dl, behind: true, cupUv);               // cups round the far side: the body hides them, except where they stick out past its edge
        DrawBody(dl, n, tier, in look, in ctx);
        DrawGlints(dl, n, in look, in ctx);
        if (cups > 0) FlushCups(dl, behind: false, cupUv);              // cups on this side, over the body and its highlights

        if (s.TipFlare > 0.001f && !closed)
        {
            path.SampleAtArc(visibleLen, out Vector2 tip, out _);
            DrawTip(dl, tip, MathF.Max(radius * Taper(reveal, false), 1.6f * px), Math.Clamp(s.TipFlare, 0f, 1f), in look, in ctx);
        }
    }

    // ---- Cross-sections ----

    private int BuildRows(StrandPath path, float total, float visibleLen, bool closed, bool flushStart, float reveal,
                          Tier tier, int seed, float phase, float twist, float time, float px, float depth, in Look look)
    {
        int pn = Math.Min(path.Count, MaxPathPoints);
        PreparePointTangents(path, pn, closed);

        float diameter = look.Diameter, radius = look.Radius;
        // Rows are cut finely enough to carry the highlight breakup and the skin pattern, however thick the limb:
        // a fraction of the diameter alone would leave a 150 px arm with a row every 30 px.
        float step = Math.Clamp(diameter * tier.StepFrac, 4.5f * px, 11f * px);

        // ---- how this tentacle moves and looks, reseeded from its seed ----
        path.SampleAtArc(0f, out Vector2 basePos, out _);
        path.SampleAtArc(total, out Vector2 tipPos, out _);
        float flowSign = closed || tipPos.Y >= basePos.Y ? 1f : -1f;      // slime runs down the screen: toward whichever end is lower

        // Closed loops keep every periodic term to a whole number of cycles, so the seam cannot be found.
        float Rate(float wavelength) => closed ? MathF.Max(1f, MathF.Round(total / wavelength)) / total : 1f / wavelength;

        _rp = new RowParams
        {
            Total = total, VisibleLen = visibleLen, Reveal = reveal, Closed = closed,
            Growing = 1f - DrawHelpers.Smooth((reveal - 0.90f) / 0.10f),              // the front of a growing tentacle is drawn out to a point
            Diameter = diameter, Radius = radius, Px = px, Depth = depth, Time = time, Twist = twist,
            Margin = diameter * 1.6f, ScreenW = _screenW, ScreenH = _screenH,
            FieldScale = MathF.Min(diameter, 70f * px),
            Ph1 = DrawHelpers.HashRange(seed + 81, 0f, MathF.Tau),
            Ph2 = DrawHelpers.HashRange(seed + 82, 0f, MathF.Tau),
            Ph3 = DrawHelpers.HashRange(seed + 83, 0f, MathF.Tau),
            Psi0 = DrawHelpers.HashRange(seed + 84, 0f, MathF.Tau),
            OffA = DrawHelpers.HashRange(seed + 85, 0f, 400f),
            OffB = DrawHelpers.HashRange(seed + 86, 0f, 400f),
            OffC = DrawHelpers.HashRange(seed + 87, 0f, 400f),
            Scroll = flowSign * (time + phase * 3.1f) * 24f * px,
            WobbleRate = Rate(diameter * 6.4f), ToneRateA = Rate(diameter * 4.3f), ToneRateB = Rate(diameter * 9.1f),
            PitchRate = Rate(diameter * 13f), TwistRate = Rate(diameter * 40f),
        };

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

        for (int i = 0; i < count; i++) FillRow(ref _rows[i], in _rp, in look);
        return count;
    }

    /// <summary>Everything about one cross-section that depends on where and when: width, belly line, wetness, highlights, skin coordinates. Arc, P, T and the cap fields must be set first. Shared by the body rows and every sucker, so a cup sees exactly the skin the body is drawn with.</summary>
    private static void FillRow(ref Row r, in RowParams p, in Look look)
    {
        float sArc = r.Arc;
        bool closed = p.Closed;
        float total = p.Total, diameter = p.Diameter, time = p.Time, fs = p.FieldScale;

        Vector2 c = r.P;
        r.On = c.X > -p.Margin && c.Y > -p.Margin && c.X < p.ScreenW && c.Y < p.ScreenH;
        if (!r.On) return;

        float u = total > 1f ? sArc / total : 0f;

        // ---- width: taper, a slow muscular swell, slime lumps, and perspective ----
        float swell = 1f + (0.045f + 0.03f * look.Agit) * MathF.Sin(MathF.Tau * sArc * p.WobbleRate - time * (1.7f + 3f * look.Agit) + p.Ph1);

        float lumpField = DrawHelpers.Smooth((Field(sArc, total, closed, fs * 2.4f, p.OffA, p.Scroll * 0.8f) - 0.46f) / 0.28f);
        float wetField  = 0.55f + 0.45f * DrawHelpers.Smooth((Field(sArc, total, closed, fs * 3.3f, p.OffB, p.Scroll) - 0.34f) / 0.34f);
        float lump = lumpField * wetField;

        float pitch = (0.40f + 0.22f * look.Agit) * MathF.Sin(MathF.Tau * sArc * p.PitchRate + p.Ph2 + 0.55f * time);
        r.SinA = MathF.Sin(pitch); r.CosA = MathF.Cos(pitch);
        float perspective = 1f + 0.18f * r.SinA;

        float tipThin = 1f;
        if (p.Growing > 0.001f && !closed)
        {
            float fromTip = (p.VisibleLen - sArc) / MathF.Max(1f, p.Radius * 4.5f);
            tipThin = 1f - 0.5f * p.Growing * (1f - DrawHelpers.Smooth(fromTip));
        }

        r.Radius = p.Radius * Taper(u, closed) * swell * (1f + 0.16f * lump) * perspective * tipThin * r.CapW;
        r.Wet = wetField;

        // Lump gradient tilts the normal along the tube so a lump shades as a bulge, not a stripe.
        float h = fs * 0.6f;
        float lumpAhead = DrawHelpers.Smooth((Field(sArc + h, total, closed, fs * 2.4f, p.OffA, p.Scroll * 0.8f) - 0.46f) / 0.28f);
        float lumpBehind = DrawHelpers.Smooth((Field(sArc - h, total, closed, fs * 2.4f, p.OffA, p.Scroll * 0.8f) - 0.46f) / 0.28f);
        r.LumpTilt = -(lumpAhead - lumpBehind) * 0.85f * wetField;

        // ---- the belly line: turns slowly down the strand, drifts with time, and follows Twist ----
        float psi = p.Psi0 + MathF.Tau * sArc * p.TwistRate + 0.45f * MathF.Sin(0.31f * time + p.Ph3);
        if (p.Twist != 0f) psi += p.Twist * (closed ? 1f : MathF.Sin(MathF.PI * u));
        if (look.Agit > 0.01f) psi += look.Agit * 0.05f * MathF.Sin(time * 17f + MathF.Tau * sArc * p.ToneRateA * 0.5f);
        r.Psi = psi;

        r.Tone = 1f + 0.07f * MathF.Sin(MathF.Tau * sArc * p.ToneRateA + p.Ph3) + 0.04f * MathF.Sin(MathF.Tau * sArc * p.ToneRateB + p.Ph1);
        r.Feather = (1.0f + 2.2f * p.Depth) * p.Px * (1f + 0.4f * MathF.Sin(MathF.Tau * sArc * p.ToneRateB + p.Ph2));

        // ---- highlight strips: where each light's reflection falls on this stretch of tube ----
        float breakup = DrawHelpers.Smooth((Field(sArc, total, closed, fs * 1.7f, p.OffC, p.Scroll * 1.2f) - 0.30f) / 0.30f);
        Glint(KeyHalf, r.T, r.SinA, r.CosA, r.Radius, breakup, wetField, 9f, out r.Glint, out r.GlintX);
        float breakup2 = DrawHelpers.Smooth((Field(sArc, total, closed, fs * 2.1f, p.OffC + 91f, p.Scroll * 0.9f) - 0.34f) / 0.30f);
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

    /// <summary>Where a light's reflection falls on a tube, and how strongly: a cylinder reflects along a line (brightest square to the light) that breaks up as the tube curves. Returns strength and offset across the tube.</summary>
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

    /// <summary>A slow 0..1 field along the strand, scrolling at scroll px (slime creeping downhill); sampled round a circle on a closed loop, so there is no seam.</summary>
    private static float Field(float sArc, float total, bool closed, float scale, float offset, float scroll)
    {
        float s = sArc - scroll;
        if (!closed) return Noise.Value(s / scale + offset, offset * 0.37f);
        float a = MathF.Tau * s / total;
        float rr = total / (MathF.Tau * scale);
        return Noise.Value(rr * MathF.Cos(a) + offset, rr * MathF.Sin(a) + offset * 0.37f);
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

    /// <summary>Smooth tangents at every path point, interpolated per row, so a coarse path shades without facets (as in Rope).</summary>
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

    /// <summary>Samples the strand at any arc length, by search rather than by walking: a sucker is evaluated exactly where it belongs.</summary>
    private void SampleAtFree(StrandPath path, int pn, float s, out Vector2 p, out Vector2 t)
    {
        var arc = path.Arc;
        int lo = 0, hi = pn - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (arc[mid] <= s) lo = mid; else hi = mid - 1;
        }
        int seg = lo;
        SampleAt(path, pn, ref seg, s, out p, out t);
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

    // ---- Shadow: a soft band under the tentacle, narrowing with it ----

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

    // ---- Body ----

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
            Vector3 tint = Vector3.Lerp(DorsalTint, VentralTint, DrawHelpers.Smooth((belly + 0.2f) / 1.0f));
            col *= tint * row.Tone;

            // Mottling: broad blotches and a few dark freckles, painted on the skin (so they ride round with the twist).
            if (detail > 0.45f)
            {
                // Round the tube, not along an unwrapped angle: noise is not periodic in phi, so a belly line that has
                // turned a whole number of times (a closed loop's seam) would otherwise jump to a different pattern.
                float mx = row.MX + MathF.Cos(phi) * 1.15f, my = row.MY + MathF.Sin(phi) * 1.15f;
                float m = Noise.Value(mx, my);
                float freckle = DrawHelpers.Smooth((Noise.Value(mx * 2.3f + 17f, my * 2.3f + 9f) - 0.70f) / 0.16f);
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

            outC[vb + 1 + c] = DrawHelpers.VertexColor(col, look.Alpha, look.AlphaBits, look.Overridden);
        }

        // Soft edge: the neighbouring column's colour at alpha 0, pushed outward by the feather.
        outP[vb] = outP[vb + 1] - perp * row.Feather;
        outC[vb] = outC[vb + 1] & 0x00FFFFFFu;
        outP[vb + nc + 1] = outP[vb + nc] + perp * row.Feather;
        outC[vb + nc + 1] = outC[vb + nc] & 0x00FFFFFFu;
    }

    // ---- Suckers: real geometry, placed in material coordinates ----

    private const float SuckerRadiusFrac = 0.50f;       // cup radius, in tube radii (of the static profile)
    private const float SuckerRowAngle   = 0.72f;       // each row sits this far (rad) either side of the belly line
    private const float SuckerPitch      = 0.85f;       // spacing along a row, in tube diameters
    private const float SuckerFirst      = 1.35f;       // the first cup, in pitches from the root
    private const float SuckerLift       = 1.20f;       // how tall a cup stands, in cup radii
    private const int   MaxCups          = 96;
    private const int   ProfileSteps     = 96;          // resolution of the table that places cups along the taper

    // The cup is a surface of revolution about the skin's normal. Its profile, from outside in: a soft
    // contact shadow on the skin; a skirt rising off it; the outer wall; a rounded rim; the steep inner wall;
    // and a floor with a slight dome. rho is the distance from the cup's axis and z the height above the skin,
    // both in cup radii.
    private static readonly float[] CupRho    = { 1.64f, 1.40f, 1.20f, 1.10f, 1.00f, 0.94f, 0.88f, 0.80f, 0.72f, 0.65f, 0.60f, 0.52f, 0.40f, 0.24f, 0.00f };
    private static readonly float[] CupZ      = { 0.00f, 0.00f, 0.02f, 0.14f, 0.34f, 0.54f, 0.70f, 0.80f, 0.78f, 0.66f, 0.48f, 0.30f, 0.19f, 0.15f, 0.20f };
    private static readonly float[] CupAlpha  = { 0.00f, 0.42f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f };
    // How open to the sky each ring is: the skirt sits in its own contact shadow and the cavity is deep in the cup.
    private static readonly float[] CupOpen   = { 1.00f, 1.00f, 0.72f, 0.84f, 0.93f, 1.00f, 1.00f, 1.06f, 1.00f, 0.92f, 0.76f, 0.58f, 0.46f, 0.42f, 0.50f };
    private const int FirstFlesh = 2;                   // rings before this are the contact shadow
    private const int FirstCavity = 10;                 // rings from here on are inside the cup
    private static readonly Vector3 CupTint = new(1.00f, 0.78f, 0.84f);          // inside a cup the flesh is pinker and softer

    /// <summary>The profile's outward normal at each ring, as (along rho, along z). Derived from the profile itself so shape and shading cannot disagree.</summary>
    private static readonly (float Rho, float Z)[] CupNormal = ProfileNormals();

    private static (float, float)[] ProfileNormals()
    {
        int n = CupRho.Length;
        var normals = new (float, float)[n];
        for (int j = 0; j < n; j++)
        {
            int a = Math.Max(0, j - 1), b = Math.Min(n - 1, j + 1);
            float dRho = CupRho[b] - CupRho[a], dZ = CupZ[b] - CupZ[a];
            float len = MathF.Sqrt(dRho * dRho + dZ * dZ);
            // The profile runs inward over the top of the solid, so its outward normal is (dz, -drho).
            normals[j] = len > 1e-6f ? (dZ / len, -dRho / len) : (0f, 1f);
        }
        normals[n - 1] = (0f, 1f);                       // the axis: straight out
        return normals;
    }

    // A cup's angular resolution follows its size on screen: most cups are small (they shrink along the taper) and a
    // 6 px cup does not need 24 segments. Unit-circle tables by segment count; a quad stores its stride in its index.
    private static readonly float[][] CupSinBy = CircleTables(false), CupCosBy = CircleTables(true);
    private static readonly int[] StrideOf = { 9, 11, 13, 15, 17, 19, 21, 23, 25 };      // segments 8, 10, ... 24, plus one
    private const int StrideShift = 20;                                                  // quad = (stride code << 20) | first vertex

    private static float[][] CircleTables(bool cos)
    {
        var tables = new float[25][];
        for (int segs = 8; segs <= 24; segs += 2)
        {
            tables[segs] = new float[segs + 1];
            for (int i = 0; i <= segs; i++) { float a = MathF.Tau * i / segs; tables[segs][i] = cos ? MathF.Cos(a) : MathF.Sin(a); }
        }
        return tables;
    }

    private const int MaxCupVerts = MaxCups * 15 * 25;
    private const int MaxCupQuads = MaxCups * 14 * 24;

    private readonly Vector2[] _cupPos = new Vector2[MaxCupVerts];
    private readonly uint[]    _cupCol = new uint[MaxCupVerts];
    private readonly float[]   _cupZ   = new float[MaxCupVerts];        // depth toward the viewer
    private readonly float[]   _cupNz  = new float[MaxCupVerts];        // the normal's component toward the viewer
    private int _cupVerts;
    private int[] _remap = new int[MaxCupVerts];

    // Quads that face the viewer, split by whether they lie behind the tube's silhouette plane (drawn before the
    // body, so the body hides what it should and what sticks out past its edge shows) or in front of it (drawn after).
    private readonly float[] _keyBack = new float[MaxCupQuads], _keyFront = new float[MaxCupQuads];
    private readonly int[]   _quadBack = new int[MaxCupQuads],  _quadFront = new int[MaxCupQuads];
    private int _nBack, _nFront;

    private readonly float[] _profileN = new float[ProfileSteps + 1];
    private readonly MeshBuilder _cupMeshBack = new(), _cupMeshFront = new();

    /// <summary>Builds every sucker. A cup's place is a function of material coordinates alone (fraction along the arm, angle from the belly line), evaluated exactly rather than on the nearest row, so it rides the skin steadily as the strand moves, stretches and grows; growth only scales it up.</summary>
    private int BuildSuckers(StrandPath path, Tier tier, in Look look, float px)
    {
        _cupVerts = 0; _nBack = 0; _nFront = 0;
        int segs = tier.SuckerSegs;

        ref readonly RowParams p = ref _rp;
        int pn = Math.Min(path.Count, MaxPathPoints);
        float total = p.Total, diameter = p.Diameter;
        bool closed = p.Closed;
        float scaleN = total / (SuckerPitch * diameter);          // cups per row along the whole arm, if it did not taper

        int cups = 0;
        if (closed)
        {
            // A whole number of cups round the loop, so the pattern closes on itself.
            int n = Math.Max(6, (int)MathF.Round(scaleN / Taper(0f, true)));
            for (int m = 0; m < n && cups < MaxCups; m++)
                for (int r = 0; r < 2 && cups < MaxCups; r++)
                    cups += AddSucker((m + 0.5f * r) / n, r, path, pn, tier, in look, px) ? 1 : 0;
            return cups;
        }

        // n(u): how many cups fit before fraction u of the arm, given that they shrink as it tapers.
        _profileN[0] = 0f;
        for (int i = 0; i < ProfileSteps; i++)
            _profileN[i + 1] = _profileN[i] + scaleN / (ProfileSteps * Taper((i + 0.5f) / ProfileSteps, false));
        float nTotal = _profileN[ProfileSteps];

        for (int m = 0; cups < MaxCups; m++)
        {
            for (int r = 0; r < 2; r++)
            {
                float n = SuckerFirst + m + 0.5f * r;                  // the second row sits half a pitch behind the first
                if (n >= nTotal - 0.6f) return cups;
                float u = UFromN(n);
                if (u * total > p.VisibleLen) return cups;             // beyond the growing tip; later ones are further still
                cups += AddSucker(u, r, path, pn, tier, in look, px) ? 1 : 0;
            }
        }
        return cups;
    }

    private float UFromN(float n)
    {
        int lo = 0, hi = ProfileSteps;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (_profileN[mid] <= n) lo = mid; else hi = mid;
        }
        float span = _profileN[hi] - _profileN[lo];
        float f = span > 1e-6f ? (n - _profileN[lo]) / span : 0f;
        return (lo + f) / ProfileSteps;
    }

    /// <summary>Builds one cup at fraction u of the way along the arm, in belt <paramref name="rowIndex"/> (0 or 1).</summary>
    private bool AddSucker(float u, int rowIndex, StrandPath path, int pn, Tier tier, in Look look, float px)
    {
        ref readonly RowParams p = ref _rp;
        float sArc = u * p.Total;

        // Size comes from the static profile (not the breathing, lumpy live radius), so a cup does not pulse with the swell.
        float refR = 0.5f * p.Diameter * Taper(u, p.Closed);
        float grow = p.Closed ? 1f : DrawHelpers.Smooth((p.VisibleLen - sArc) / (1.7f * refR + 1f));
        float small = DrawHelpers.Smooth((2f * refR - 6f * px) / (7f * px));       // fades out smoothly as the arm thins to nothing
        float k = grow * small;
        if (k < 0.02f) return false;
        float rc = SuckerRadiusFrac * refR * k;
        if (rc < 1f) return false;

        // The skin exactly under this cup.
        Row row = default;
        row.Arc = sArc; row.CapW = 1f; row.CapAxial = 0f;
        SampleAtFree(path, pn, sArc, out row.P, out row.T);
        FillRow(ref row, in _rp, in look);
        if (!row.On || row.Radius < 1f) return false;

        // Where round the tube it sits, as an angle from the view axis. Well behind the silhouette it cannot be seen at all.
        float belt = rowIndex == 0 ? +SuckerRowAngle : -SuckerRowAngle;
        float theta = WrapAngle(belt - row.Psi);
        if (MathF.Cos(theta) < -0.72f) return false;

        BuildCup(in row, theta, rc, k, tier, in look);
        return true;
    }

    private void BuildCup(in Row row, float theta, float rc, float visible, Tier tier, in Look look)
    {
        int segs = Math.Clamp((int)(rc * 1.1f) & ~1, 8, tier.SuckerSegs), rings = CupRho.Length, stride = segs + 1;
        float[] cupSin = CupSinBy[segs], cupCos = CupCosBy[segs];
        int strideCode = (segs - 8) >> 1;
        int vb = _cupVerts;
        if (vb + rings * stride > MaxCupVerts) return;

        Vector2 T = row.T;
        Vector2 perp = new(-T.Y, T.X);
        float sa = row.SinA, ca = row.CosA;
        float skin = row.Radius;
        float lift = SuckerLift * rc;
        Vector3 axial = new(T.X * ca, T.Y * ca, sa);                    // the tube's axis direction in view space
        float alphaK = visible * look.Alpha;
        float wet = MathF.Max(0.65f, row.Wet);
        Vector2 shadowShift = StudioLighting.ShadowDirection * (rc * 0.30f);

        for (int j = 0; j < rings; j++)
        {
            float rho = CupRho[j] * rc;
            float z = CupZ[j] * lift;
            (float nR, float nZ) = CupNormal[j];

            for (int i = 0; i < stride; i++)
            {
                float cs = cupCos[i], sn = cupSin[i];
                float x = rho * cs, y = rho * sn;                       // along the tube, and round it, in px

                // The cup lies on the tube's surface, not on a flat plane: moving round it by y turns the radial direction by y/R.
                float th = theta + y / skin;
                (float sT, float cT) = MathF.SinCos(th);
                Vector3 ncyl = new(perp.X * sT - T.X * sa * cT, perp.Y * sT - T.Y * sa * cT, ca * cT);
                Vector3 round = new(perp.X * cT + T.X * sa * sT, perp.Y * cT + T.Y * sa * sT, -ca * sT);

                Vector3 pos = new Vector3(row.P.X, row.P.Y, 0f) + axial * x + ncyl * (skin + z);
                Vector3 radial = axial * cs + round * sn;               // the cup's own outward direction at this angle
                Vector3 n3 = Vector3.Normalize(radial * nR + ncyl * nZ);

                int v = vb + j * stride + i;
                Vector2 screen = new(pos.X, pos.Y);
                if (j < FirstFlesh) screen += shadowShift;
                _cupPos[v] = screen;
                _cupZ[v] = pos.Z;
                _cupNz[v] = n3.Z;
                _cupCol[v] = CupColour(j, n3, in row, wet, alphaK, in look);
            }
        }
        _cupVerts = vb + rings * stride;

        // Quads that can be seen: those facing the viewer. Sorted by depth later, so near parts of a cup cover far ones.
        for (int j = 0; j < rings - 1; j++)
        for (int i = 0; i < segs; i++)
        {
            int v0 = vb + j * stride + i, v1 = v0 + 1, v2 = v0 + stride, v3 = v2 + 1;
            float nz = 0.25f * (_cupNz[v0] + _cupNz[v1] + _cupNz[v2] + _cupNz[v3]);
            if (nz < -0.04f) continue;                                   // faces away: always hidden behind its own front
            float z = 0.25f * (_cupZ[v0] + _cupZ[v1] + _cupZ[v2] + _cupZ[v3]);
            float height = 0.5f * (CupZ[j] + CupZ[j + 1]) * lift;
            float key = z + 0.35f * height;                              // taller parts win a tie where the depths barely differ

            if (z >= 0f) { if (_nFront < MaxCupQuads) { _keyFront[_nFront] = key; _quadFront[_nFront++] = (strideCode << StrideShift) | v0; } }
            else if (j > FirstFlesh)
            {
                // Behind the silhouette plane. The skirt and contact shadow of such a cup lie on skin the body already
                // hides; drawing them only lets a dark fringe leak through the body's soft edge. Only the cup proper can show.
                if (_nBack < MaxCupQuads) { _keyBack[_nBack] = key; _quadBack[_nBack++] = (strideCode << StrideShift) | v0; }
            }
        }
    }

    private uint CupColour(int ring, Vector3 n, in Row row, float wet, float alphaK, in Look look)
    {
        float a = CupAlpha[ring] * alphaK;

        if (ring < FirstFlesh)
        {
            // The contact shadow: the same tint as the tentacle's cast shadow, a little darker. It lies on the skin, so
            // it fades as the skin turns edge-on (n.Z is the skin's own normal here); left alone it ends in a hard dark wedge at the silhouette.
            return DrawHelpers.WithAlpha(StrandShading.ShadowTint, a * 0.9f * DrawHelpers.Smooth(n.Z / 0.45f));
        }

        Vector3 col = Flesh.Sample(n.X, n.Y) * (VentralTint * (1.04f * row.Tone * CupOpen[ring]));
        if (ring >= FirstCavity) col *= CupTint;

        // Wet: the same clear gloss as the body, so the cups and the skin around them agree.
        Vector3 gloss = Coat.Sample(n.X, n.Y);
        float w = wet * (ring >= FirstCavity ? 0.8f : 1f);
        col = col * (1f - 0.10f * w);
        col = col + gloss * w - col * gloss * w;

        if (look.Dye > 0f) col = DrawHelpers.WithSaturation(col, look.Dye);
        if (look.Agit > 0.01f) col += col * (col * (0.45f * look.Agit));
        if (look.Fog > 0f) col = Vector3.Lerp(col, StrandShading.DepthFog, look.Fog);

        return DrawHelpers.Tint(DrawHelpers.Pack(col.X, col.Y, col.Z), a, look.Overridden);
    }

    /// <summary>Draws the cup quads behind the tube's silhouette plane (call before the body) or in front of it (after), far to near.</summary>
    private void FlushCups(ImDrawListPtr dl, bool behind, Vector2 uv)
    {
        int n = behind ? _nBack : _nFront;
        if (n == 0) return;
        float[] keys = behind ? _keyBack : _keyFront;
        int[] quads = behind ? _quadBack : _quadFront;
        MeshBuilder mesh = behind ? _cupMeshBack : _cupMeshFront;

        Array.Sort(keys, quads, 0, n);                                   // far first: the painter's algorithm

        // Only the vertices this group's triangles reference: a cup's other half belongs to the other group.
        mesh.Clear();
        Array.Fill(_remap, -1, 0, _cupVerts);
        for (int q = 0; q < n; q++)
        {
            int code = quads[q];
            int v0 = code & ((1 << StrideShift) - 1), stride = StrideOf[code >> StrideShift];
            int a = Use(mesh, v0), b = Use(mesh, v0 + 1), c = Use(mesh, v0 + stride), d = Use(mesh, v0 + stride + 1);
            mesh.Tri(a, b, d);
            mesh.Tri(a, d, c);
        }
        mesh.Flush(dl, uv);
    }

    private int Use(MeshBuilder mesh, int v)
    {
        int m = _remap[v];
        if (m < 0) _remap[v] = m = mesh.Vertex(_cupPos[v], _cupCol[v]);
        return m;
    }

    private static float WrapAngle(float a)
    {
        a %= MathF.Tau;
        if (a > MathF.PI) a -= MathF.Tau;
        else if (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    // ---- Glints ----

    /// <summary>A thin hard highlight where the key light's reflection falls: narrower than the body's vertex spacing, so its own ribbon over the body; breaks up along the strand and fades where the tube turns away.</summary>
    private void DrawGlints(ImDrawListPtr dl, int rowCount, in Look look, in MaterialContext ctx)
    {
        // The key light's reflection is the hard warm one; the fill's is dimmer, cooler and broader.
        DrawGlintStrip(dl, rowCount, in look, in ctx, fill: false, DrawHelpers.Pack(1.00f, 0.97f, 0.86f));
        DrawGlintStrip(dl, rowCount, in look, in ctx, fill: true,  DrawHelpers.Pack(0.82f, 0.93f, 1.00f));
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

    private static uint GlintColour(uint rgb, float alpha, in Look look) => DrawHelpers.Tint(rgb, alpha, look.Overridden);

    // ---- Tip ----

    private static void DrawTip(ImDrawListPtr dl, Vector2 tip, float bar, float k, in Look look, in MaterialContext ctx)
    {
        if (k <= 0.004f) return;
        // A bead of slime on the end of a tentacle that is still reaching: a hard pinprick glint and the faintest sheen round it.
        uint halo = DrawHelpers.WithAlpha(DrawHelpers.Pack(0.82f, 0.95f, 0.55f), 0.11f * k * look.Alpha);
        uint clear = DrawHelpers.WithAlpha(DrawHelpers.Pack(0.82f, 0.95f, 0.55f), 0f);
        uint pip = DrawHelpers.WithAlpha(DrawHelpers.Pack(1.00f, 1.00f, 0.92f), 0.90f * k * look.Alpha);
        Span<float> rr = stackalloc float[2] { bar * 0.9f, bar * 2.3f };
        Span<uint> cc = stackalloc uint[2] { halo, clear };
        MeshDraw.Radial(dl, tip, MeshDraw.WhiteUv(ctx.Time), pip, 10, rr, cc, 0f, 1f, 0, 0f);
    }

    // ---- What a tentacle sheds, and what happens when it is hit ----

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    /// <summary>Slime, hanging off the underside in long stretching strings. Heavier than a rope's dust: this is the point of it.</summary>
    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Goop,
            DensityPer100px: 0f,
            SpeedMin: 60f, SpeedMax: 140f,
            LifespanMin: 2.4f, LifespanMax: 3.2f,
            SizeMin: 4.8f, SizeMax: 8.0f,
            SpreadRadians: 0f,
            BiasVelocity: Vector2.Zero,
            Gravity: new Vector2(0f, 1500f),
            Drip: new StrokeDripOptions(
                SiteSpacingPx: 120f,
                CycleSecondsMin: 2.6f, CycleSecondsMax: 5.0f,
                MinSlope: 0.40f,
                Stringiness: 0.85f,
                SatelliteChance: 0.55f)),

        // Beads of slime creeping along the skin: the film gathering into drops on its way down.
        new(Role: PrimitiveRole.Goop,
            DensityPer100px: 0.60f,
            SpeedMin: 0f, SpeedMax: 0f,
            LifespanMin: 4.5f, LifespanMax: 8f,
            SizeMin: 3.6f, SizeMax: 7.0f,
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
