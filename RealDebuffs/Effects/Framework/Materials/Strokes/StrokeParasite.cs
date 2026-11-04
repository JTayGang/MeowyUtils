using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>A living tentacle as lit 3D geometry: tapered tube, pale belly, dark back, mottled skin, flowing slime</summary>
public sealed class StrokeParasite : IStrokeMaterial
{
    public string Name => "stroke.parasite";
    public string[] NaturalLanguageWords { get; } = { "tentacle", "tentacles", "tendril", "tendrils", "parasite", "parasites" };

    // size ---- wide enough for a limb a sixth of the screen thick, narrow enough to be a thread
    private const float MinDiameterFrac = 0.012f;
    private const float MaxDiameterFrac = 0.180f;
    private const float TipFrac = 0.075f;  // diameter at the very tip, as a fraction of the base's
    private const int   MaxRows = 1000;
    private const int   MaxPathPoints = 512;

    private static readonly Matcap Flesh = new(SurfacePresets.ParasiteFlesh);
    private static readonly Matcap Coat  = new(SurfacePresets.WetCoat);

    // Back and belly: multipliers on the flesh matcap; the belly paler and yellower, the back darker and greener.
    private static readonly Vector3 DorsalTint  = new(0.72f, 0.80f, 0.70f);
    private static readonly Vector3 VentralTint = new(1.22f, 1.14f, 0.92f);

    /// <summary>The half vector between the key light and the viewer: where a highlight sits on a surface.</summary>
    private static readonly Vector3 KeyHalf = Vector3.Normalize(StudioLighting.Key + Vector3.UnitZ);
    private static readonly Vector3 FillHalf = Vector3.Normalize(StudioLighting.Fill + Vector3.UnitZ);

    // Skin relief: warts (height as a fraction of the arm's radius, shading tilt) and folds.
    private const float CupSegPerPx   = 0.90f;
    private const float BumpHeightFrac = 0.16f;
    private const float BumpTilt       = 1.00f;
    private const float FoldDepth      = 0.02f;  // radius modulation of the folds
    private const float FoldTilt       = 0.17f;  // and the tilt they give the shading
    private const float CavityDark     = 0.26f;  // how dark the hollows between warts go
    private const float CrestLift      = 0.11f;  // and how much the crests brighten

    /// <summary>Level of detail: columns across the visible half (even in ANGLE), rows along the length.</summary>
    private sealed class Tier
    {
        public readonly int Cols;
        public readonly float StepFrac;  // row spacing, in diameters
        public readonly int SuckerSegs;  // 0 = no suckers at this size
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

    private static readonly Tier Thick  = new(17, 0.20f, 24);  // a limb thick enough that 11 columns would show as blocks
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
        public float Detail;  // 0..1: how much of the fine skin work is worth doing at this size
        public float MottleFreq;  // 1/px along the strand
        public float MottleOffset;  // per tentacle, so no two share a pattern
    }

    /// <summary>One cross-section of the tentacle: everything a vertex needs that does not depend on which column it is.</summary>
    private struct Row
    {
        public Vector2 P, T;
        public float Arc;
        public float CapW, CapAxial;  // end-cap shaping, as in Rope: width factor and axial normal
        public float Radius;  // half-width in px, all effects included
        public float Psi;  // angle of the belly line from the view axis
        public float SinA, CosA;  // pitch toward (+) or away (-) from the viewer
        public float Tone;  // slow brightness drift along the strand
        public float Wet;  // 0..1 slime coverage
        public float LumpTilt;  // axial normal tilt from the slime lumps
        public float Feather;
        public float Glint, GlintX;  // key-light highlight strip on this row: strength
        public float Glint2, GlintX2;  // the same for the cooler fill light
        public float MX, MY;  // skin-pattern coordinates
        public float WX, WRing, BumpAmp;  // wart field: along-strand coordinate, ring radius in noise cells
        public bool  On;  // on screen: only these rows are shaded
    }

    /// <summary>Per-tentacle, per-frame constants a cross-section needs.</summary>
    private struct RowParams
    {
        public float Total, VisibleLen, Reveal, Growing;
        public bool Closed;
        public float Diameter, Radius, Px, Depth, Time, Twist, Margin, ScreenW, ScreenH;
        public float FieldScale;  // size of the slime patches and highlight breakup, capped in pixels
        public float Ph1, Ph2, Ph3, Psi0, OffA, OffB, OffC, Scroll;
        public float WobbleRate, ToneRateA, ToneRateB, PitchRate, TwistRate;
        public float BumpWave, BumpOff, FoldWave;  // wart wavelength/offset; fold wavelength (px); BumpWave 0 = smooth
    }

    private RowParams _rp;
    private readonly Row[] _rows = new Row[MaxRows];
    private readonly float[] _hgt = new float[MaxRows * 17];  // wart height (noise, ~[-1,1]) per row and column
    private readonly PathRows _path = new(MaxPathPoints);
    private float _screenW, _screenH;

    // The strand's profile: shared by the drawing and by RadiusAt

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

    // Draw

    // Frame budget: Building a tentacle's mesh is the whole cost of drawing it, and a tentacle moves slowly.

    /// <summary>Milliseconds per frame this material may spend building tentacles; 0 = build all every frame.</summary>
    public static float BudgetMs = 1.5f;

    /// <summary>The longest a tentacle's picture may be left as it was, in seconds.</summary>
    public static float MaxStaleSeconds = 0.034f;

    private const int MaxInterval = 3, MaxStaleFrames = 3;

    private sealed class Cached
    {
        public readonly MeshDraw.MeshCache Mesh = new();
        public float Seen, CostMs = 0.2f;  // CostMs: what building this tentacle takes, smoothed
        public int BuiltFrame, Phase;  // Phase: which of the _interval frames this one is rebuilt on
    }

    private readonly Dictionary<int, Cached> _cache = new();
    private readonly List<int> _cacheStale = new();
    private readonly List<Cached> _planned = new();
    private float _frameTime = float.NaN, _cacheSweep;
    private int _frameNo, _slot, _slotsLastFrame, _interval = 1;
    private double _buildMsEma = 0.2;  // per tentacle, smoothed
    private float _frameDt = 1f / 60f;  // smoothed time between frames

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        float budget = BudgetMs;
        if (budget <= 0f) { DrawBuild(dl, in s, in ctx); return; }

        if (ctx.Time != _frameTime) NewFrame(ctx.Time, budget);
        _slot++;

        int key = unchecked(s.Seed * 31 + (int)s.Owner);
        if (!_cache.TryGetValue(key, out Cached? c)) { c = new Cached(); _cache[key] = c; }
        c.Seen = ctx.Time;

        bool due = !c.Mesh.Valid || _interval <= 1 || (_frameNo + c.Phase) % _interval == 0 || _frameNo - c.BuiltFrame >= MaxStaleFrames;
        if (!due)
        {
            MeshDraw.Replay(dl, c.Mesh);
            return;
        }

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        MeshDraw.BeginRecord(c.Mesh);
        DrawBuild(dl, in s, in ctx);
        MeshDraw.EndRecord();
        c.BuiltFrame = _frameNo;
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _buildMsEma += (ms - _buildMsEma) * 0.04;
        c.CostMs += ((float)ms - c.CostMs) * 0.15f;
    }

    private void NewFrame(float time, float budget)
    {
        float step = time - _frameTime;  // NaN on the first frame
        if (step > 0f && step < 0.25f) _frameDt += (step - _frameDt) * 0.1f;
        _slotsLastFrame = _slot;
        _slot = 0;
        _frameTime = time;
        _frameNo++;

        if (_slotsLastFrame > 0)
        {
            double full = _buildMsEma * _slotsLastFrame;  // what building every tentacle this frame would cost
            int cap = Math.Clamp((int)(MaxStaleSeconds / MathF.Max(_frameDt, 1e-3f)), 1, MaxInterval);  // fps-aware: a picture held for N frames must not be held too long
            int want = Math.Clamp((int)Math.Ceiling(full / budget), 1, cap);
            if (want > _interval) _interval = want;
            else if (want < _interval && (_interval > cap || full / (_interval - 1) < budget * 0.85)) _interval--;  // relax once the cheaper plan still sits comfortably under budget
        }

        if (_interval > 1 && (_frameNo % 8 == 0 || _planInterval != _interval)) Plan();

        if (time - _cacheSweep > 1f)  // forget tentacles that have gone
        {
            _cacheSweep = time;
            _cacheStale.Clear();
            foreach (var kv in _cache) if (time - kv.Value.Seen > 1.5f) _cacheStale.Add(kv.Key);
            foreach (int k in _cacheStale) _cache.Remove(k);
        }
    }

    private int _planInterval;

    /// <summary>Gives each live tentacle a rebuild phase so the cost is level from frame to frame: the dearest first</summary>
    private void Plan()
    {
        _planInterval = _interval;
        _planned.Clear();
        foreach (var kv in _cache) if (_frameTime - kv.Value.Seen < 0.25f) _planned.Add(kv.Value);
        _planned.Sort((a, b) => b.CostMs.CompareTo(a.CostMs));
        Span<float> load = stackalloc float[MaxInterval];
        load.Clear();
        foreach (Cached c in _planned)
        {
            int best = 0;
            for (int k = 1; k < _interval; k++) if (load[k] < load[best]) best = k;
            c.Phase = best;
            load[best] += c.CostMs;
        }
    }

    private void DrawBuild(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
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
            MottleFreq = 1f / MathF.Min(diameter * 2.3f, 100f * px),  // skin detail is in px, not a fraction of the limb
            MottleOffset = DrawHelpers.HashRange(seed + 71, 0f, 300f),
        };

        _screenW = ctx.ScreenW;
        _screenH = ctx.ScreenH;

        int n = BuildRows(path, total, visibleLen, closed, s.FlushStart, reveal, tier, seed, s.Phase,
                          s.Twist, ctx.Time, px, depth, in look);
        if (n < 2) return;
        _rowCount = n;

        _obstN = 0;
        int cups = tier.SuckerSegs > 0 ? BuildSuckers(path, tier, in look, px) : 0;
        int beads = closed ? 0 : BuildFilm(unchecked(seed * 31 + (int)s.Owner), in look, ctx.Time);
        Vector2 cupUv = MeshDraw.WhiteUv(ctx.Time);

        DrawShadow(dl, n, depth, alpha, in ctx);
        if (cups + beads > 0) FlushCups(dl, behind: true, cupUv);  // far-side cups: the body hides them except past its edge
        DrawBody(dl, n, tier, in look, in ctx);
        DrawGlints(dl, n, in look, in ctx);
        if (!closed && _rp.BumpWave > 0f) DrawSparkles(dl, n, cupUv, in look);
        if (cups + beads > 0) FlushCups(dl, behind: false, cupUv);  // cups and slime on this side, over the body and its highlights
        if (beads > 0) DrawBeadGlints(dl, cupUv, in look);

        if (s.TipFlare > 0.001f && !closed)
        {
            path.SampleAtArc(visibleLen, out Vector2 tip, out _);
            DrawTip(dl, tip, MathF.Max(radius * Taper(reveal, false), 1.6f * px), Math.Clamp(s.TipFlare, 0f, 1f), in look, in ctx);
        }
    }

    // Cross-sections

    private int BuildRows(StrandPath path, float total, float visibleLen, bool closed, bool flushStart, float reveal,
                          Tier tier, int seed, float phase, float twist, float time, float px, float depth, in Look look)
    {
        int pn = Math.Min(path.Count, MaxPathPoints);
        _path.Prepare(path, pn, closed);

        float diameter = look.Diameter, radius = look.Radius;
        // Rows are cut finely enough to carry the highlight breakup and the skin pattern
        float step = Math.Clamp(diameter * tier.StepFrac, 4.5f * px, 11f * px);

        // how this tentacle moves and looks, reseeded from its seed
        path.SampleAtArc(0f, out Vector2 basePos, out _);
        path.SampleAtArc(total, out Vector2 tipPos, out _);
        float flowSign = closed || tipPos.Y >= basePos.Y ? 1f : -1f;  // slime runs down the screen: toward whichever end is lower

        // Closed loops keep every periodic term to a whole number of cycles, so the seam cannot be found.
        float Rate(float wavelength) => closed ? MathF.Max(1f, MathF.Round(total / wavelength)) / total : 1f / wavelength;

        _rp = new RowParams
        {
            Total = total, VisibleLen = visibleLen, Reveal = reveal, Closed = closed,
            Growing = 1f - DrawHelpers.Smooth((reveal - 0.90f) / 0.10f),  // the front of a growing tentacle is drawn out to a point
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
            // Skin relief.
            BumpWave = (!closed && tier.Cols >= 11) ? MathF.Max(3.9f * step, 0.25f * diameter) : 0f,
            BumpOff = DrawHelpers.HashRange(seed + 91, 0f, 200f),
            FoldWave = diameter * 0.30f,
        };

        // Open: the far end is always a dome (a growing tip is meant to be seen); the near one is flush if told to be.
        float capEnd = closed ? 0f : radius * Taper(reveal, false);
        float capStart = closed ? 0f : radius;
        bool domeStart = !closed && !flushStart;
        bool domeEnd = !closed;

        float bodyStart = domeStart ? capStart : 0f;
        float bodyEnd = domeEnd ? visibleLen - capEnd : visibleLen;
        step = MathF.Max(step, (bodyEnd - bodyStart) / (MaxRows - 16));

        int count = 0, seg = 0;
        if (domeStart)
            for (int i = 0; i < PathRows.DomeT.Length - 1; i++)
                AddRow(path, pn, ref seg, ref count, capStart * (1f - PathRows.DomeT[i]), -1f, PathRows.DomeT[i]);

        int bodyRows = Math.Max(1, (int)MathF.Ceiling((bodyEnd - bodyStart) / step));
        for (int i = 0; i <= bodyRows; i++)
            AddRow(path, pn, ref seg, ref count, bodyStart + (bodyEnd - bodyStart) * i / bodyRows, 0f, 0f);

        if (domeEnd)
            for (int i = PathRows.DomeT.Length - 2; i >= 0; i--)
                AddRow(path, pn, ref seg, ref count, visibleLen - capEnd * (1f - PathRows.DomeT[i]), +1f, PathRows.DomeT[i]);

        for (int i = 0; i < count; i++) FillRow(ref _rows[i], in _rp, in look);
        return count;
    }

    /// <summary>Per cross-section state: width, belly line, wetness, highlights, skin coordinates.</summary>
    private static void FillRow(ref Row r, in RowParams p, in Look look)
    {
        float sArc = r.Arc;
        bool closed = p.Closed;
        float total = p.Total, time = p.Time, fs = p.FieldScale;

        Vector2 c = r.P;
        // The margin is on every side: a tube centred past the bottom or right border can still reach the screen.
        r.On = c.X > -p.Margin && c.Y > -p.Margin && c.X < p.ScreenW + p.Margin && c.Y < p.ScreenH + p.Margin;
        float u = total > 1f ? sArc / total : 0f;
        if (!r.On)
        {
            // Nothing here is seen, but the body is shaded in whole chunks and this row can sit beside one that is.
            r.Radius = p.Radius * Taper(u, closed) * r.CapW;
            r.SinA = 0f; r.CosA = 1f; r.Psi = 0f; r.Tone = 1f; r.Wet = 0f; r.LumpTilt = 0f; r.Feather = 0f;
            r.Glint = r.GlintX = r.Glint2 = r.GlintX2 = 0f;
            r.MX = r.MY = 0f; r.WX = r.WRing = r.BumpAmp = 0f;
            return;
        }

        // width: taper, a slow muscular swell, slime lumps and perspective; Agitation adds a faster wave.
        float swellPhase = MathF.Tau * sArc * p.WobbleRate + p.Ph1;
        float swell = 1f + 0.045f * MathF.Sin(swellPhase - time * 1.7f)
                         + 0.030f * look.Agit * MathF.Sin(swellPhase - time * 4.7f);

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

        // the belly line: turns slowly down the strand, drifts with time, and follows Twist
        float psi = p.Psi0 + MathF.Tau * sArc * p.TwistRate + 0.45f * MathF.Sin(0.31f * time + p.Ph3);
        if (p.Twist != 0f) psi += p.Twist * (closed ? 1f : MathF.Sin(MathF.PI * u));
        if (look.Agit > 0.01f) psi += look.Agit * 0.05f * MathF.Sin(time * 17f + MathF.Tau * sArc * p.ToneRateA * 0.5f);
        r.Psi = psi;

        r.Tone = 1f + 0.07f * MathF.Sin(MathF.Tau * sArc * p.ToneRateA + p.Ph3) + 0.04f * MathF.Sin(MathF.Tau * sArc * p.ToneRateB + p.Ph1);
        r.Feather = (1.0f + 2.2f * p.Depth) * p.Px * (1f + 0.4f * MathF.Sin(MathF.Tau * sArc * p.ToneRateB + p.Ph2));

        // highlight strips: where each light's reflection falls on this stretch of tube
        float breakup = DrawHelpers.Smooth((Field(sArc, total, closed, fs * 1.7f, p.OffC, p.Scroll * 1.2f) - 0.30f) / 0.30f);
        Glint(KeyHalf, r.T, r.SinA, r.CosA, r.Radius, breakup, wetField, 9f, out r.Glint, out r.GlintX);
        float breakup2 = DrawHelpers.Smooth((Field(sArc, total, closed, fs * 2.1f, p.OffC + 91f, p.Scroll * 0.9f) - 0.34f) / 0.30f);
        Glint(FillHalf, r.T, r.SinA, r.CosA, r.Radius, breakup2, wetField, 7f, out r.Glint2, out r.GlintX2);

        // skin relief
        if (p.BumpWave > 0f && look.Detail > 0.45f)
        {
            float gate = r.CapW * r.CapW;
            // Transverse folds: irregular rings round the arm, as a contracting limb's skin gathers.
            float fph = MathF.Tau * sArc / p.FoldWave + 1.6f * Noise.Value(sArc / (p.FoldWave * 2.7f) + p.BumpOff, 3.1f);
            (float fs0, float fc0) = MathF.SinCos(fph);
            float fold = FoldDepth * gate;
            r.Radius *= 1f + fold * fs0;
            r.LumpTilt -= fold * FoldTilt * fc0;

            r.WX = sArc / p.BumpWave + p.BumpOff;
            r.WRing = 0.5f * p.Diameter * Taper(u, false) / p.BumpWave;  // the static radius, so the pattern does not breathe with the swell
            r.BumpAmp = BumpHeightFrac * r.Radius * gate;
        }
        else { r.WX = r.WRing = r.BumpAmp = 0f; }

        // skin pattern coordinates (periodic on a closed loop)
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

    /// <summary>Where a light's reflection falls on a tube</summary>
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

    /// <summary>A slow 0..1 field along the strand scrolling at scroll px (slime creeping downhill).</summary>
    private static float Field(float sArc, float total, bool closed, float scale, float offset, float scroll)
    {
        float s = sArc - scroll;
        if (!closed) return Noise.Value(s / scale + offset, offset * 0.37f);
        float a = MathF.Tau * s / total;
        float rr = total / (MathF.Tau * scale);
        return Noise.Value(rr * MathF.Cos(a) + offset, rr * MathF.Sin(a) + offset * 0.37f);
    }


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

    /// <summary>Samples the strand at any arc length by search, so a sucker is evaluated exactly where it belongs.</summary>
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
        _path.SampleAt(path, pn, ref seg, s, out p, out t);
    }

    // Shadow: a soft band under the tentacle, narrowing with it

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

    // Body

    private void DrawBody(ImDrawListPtr dl, int rowCount, Tier tier, in Look look, in MaterialContext ctx)
    {
        int stride = tier.Cols + 2;  // plus the two soft-edge columns
        int chunkRows = MeshDraw.MaxVerts / stride;
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);

        bool bumps = BuildBumps(rowCount, tier, in look);
        int nc = tier.Cols;
        float[]? hgt = bumps ? _hgt : null;

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
                    int ip = i > 0 ? i - 1 : 0, inx = i < rowCount - 1 ? i + 1 : rowCount - 1;
                    float ds = _rows[inx].Arc - _rows[ip].Arc;
                    ShadeRow(in _rows[i], tier, in look, v, hgt, i * nc, ip * nc, inx * nc, ds > 1e-3f ? 1f / ds : 0f);
                    v += stride;
                }
                MeshDraw.Grid(dl, stride - 1, r1 - r0, uv);
            }

            if (r1 == r0) break;
            r0 = r1;  // the seam row is shared, so chunks join exactly
        }
    }

    /// <summary>The wart field at every vertex of the body: one noise sample each.</summary>
    private bool BuildBumps(int rowCount, Tier tier, in Look look)
    {
        if (_rp.BumpWave <= 0f || look.Detail <= 0.45f) return false;
        int nc = tier.Cols;
        float[] sinT = tier.Sin, cosT = tier.Cos, hgt = _hgt;
        float oy = _rp.BumpOff * 0.37f + 5.3f;
        for (int i = 0; i < rowCount; i++)
        {
            ref readonly Row r = ref _rows[i];
            int o = i * nc;
            if (!r.On || r.BumpAmp <= 0f) { Array.Clear(hgt, o, nc); continue; }
            (float sPsi, float cPsi) = MathF.SinCos(r.Psi);
            for (int c = 0; c < nc; c++)
            {
                float u = sinT[c], co = cosT[c];
                float cosPhi = co * cPsi - u * sPsi, sinPhi = u * cPsi + co * sPsi;
                hgt[o + c] = Noise.Value(r.WX + cosPhi * r.WRing, oy + sinPhi * r.WRing);
            }
        }
        return true;
    }

    private static void ShadeRow(in Row row, Tier tier, in Look look, int vb, float[]? hgt, int ho, int hp, int hn, float invDs)
    {
        Vector2[] outP = MeshDraw.P;
        uint[] outC = MeshDraw.C;

        int nc = tier.Cols;
        Vector2 T = row.T;
        Vector2 perp = new(-T.Y, T.X);
        float rad = row.Radius;
        float capW = row.CapW, capAxial = row.CapAxial;
        float sa = row.SinA, ca = row.CosA;
        float[] sinT = tier.Sin, cosT = tier.Cos;
        float detail = look.Detail;
        (float sPsi, float cPsi) = MathF.SinCos(row.Psi);

        // The mottling is smooth (blotches a hundred pixels across; a column is ~14 px)
        Span<float> mott = stackalloc float[nc];
        bool mottle = detail > 0.45f;
        if (mottle)
        {
            for (int c = 0; c < nc; c += 2)
            {
                float uu = sinT[c], cc = cosT[c];
                mott[c] = Noise.Value(row.MX + (cc * cPsi - uu * sPsi) * 1.15f, row.MY + (uu * cPsi + cc * sPsi) * 1.15f);
            }
            for (int c = 1; c < nc; c += 2) mott[c] = 0.5f * (mott[c - 1] + (c + 1 < nc ? mott[c + 1] : mott[c - 1]));
        }

        for (int c = 0; c < nc; c++)
        {
            float u = sinT[c], co = cosT[c];

            // Skin relief: the wart height here and its slope along and round the arm, from neighbouring vertices.
            float hw = 0f, tiltS = 0f, tiltT = 0f;
            if (hgt != null && row.BumpAmp > 0f)
            {
                hw = hgt[ho + c];
                float amp = row.BumpAmp;
                tiltS = Math.Clamp(amp * (hgt[hn + c] - hgt[hp + c]) * invDs, -0.6f, 0.6f) * BumpTilt;
                int cl = c > 0 ? c - 1 : 0, cr = c < nc - 1 ? c + 1 : nc - 1;
                float dTheta = (cr - cl) * (MathF.PI / (nc - 1f)) * rad;
                tiltT = dTheta > 1e-3f ? Math.Clamp(amp * (hgt[ho + cr] - hgt[ho + cl]) / dTheta, -0.6f, 0.6f) * BumpTilt : 0f;
                hw = hw * amp;  // now in px
            }
            outP[vb + 1 + c] = row.P + perp * (u * (rad + hw * 0.45f));  // the silhouette is lumpy too

            // Normal: the cross-section's own, tilted along it by pitch, the end cap and the slime lumps.
            float across = u * capW;
            float along = capAxial + (row.LumpTilt - sa * co) * capW;
            float nx = perp.X * across + T.X * along;
            float ny = perp.Y * across + T.Y * along;
            float nz = ca * co * capW;
            if (tiltS != 0f || tiltT != 0f)
            {
                // n -= tiltS * axis + tiltT * round-the-tube direction: the surface tilts down-slope of each wart.
                nx -= tiltS * (T.X * ca) + tiltT * (perp.X * co + T.X * sa * u);
                ny -= tiltS * (T.Y * ca) + tiltT * (perp.Y * co + T.Y * sa * u);
                nz -= tiltS * sa - tiltT * (ca * u);
            }
            float inv = 1f / MathF.Sqrt(nx * nx + ny * ny + nz * nz + 1e-6f);
            nx *= inv; ny *= inv;

            Matcap.SamplePair(Flesh, Coat, nx, ny, out Vector3 col, out Vector3 gloss);

            // Position on the tentacle's own surface, measured from its belly line.
            float cosPhi = co * cPsi - u * sPsi;
            float belly = cosPhi;
            Vector3 tint = Vector3.Lerp(DorsalTint, VentralTint, DrawHelpers.Smooth((belly + 0.2f) / 1.0f));
            col *= tint * row.Tone;

            // Mottling: broad blotches and a few dark freckles painted on the skin (so they ride round with the twist).
            if (mottle)
            {
                // Round the tube, not along an unwrapped angle: noise is not periodic in phi
                float m = mott[c];
                if (hgt != null && row.BumpAmp > 0f)
                {
                    // The hollows between warts are darker (they are shaded and holding dirt)
                    float hn0 = hgt[ho + c];
                    float cavity = DrawHelpers.Smooth((-hn0 - 0.10f) / 0.70f);
                    float crest = DrawHelpers.Smooth((hn0 - 0.30f) / 0.70f);
                    col *= 1f + (m - 0.5f) * 0.38f * detail - cavity * CavityDark * detail + crest * CrestLift * detail;
                }
                else
                {
                    float mx = row.MX + cosPhi * 1.15f, my = row.MY + (u * cPsi + co * sPsi) * 1.15f;
                    float freckle = DrawHelpers.Smooth((Noise.Value(mx * 2.3f + 17f, my * 2.3f + 9f) - 0.70f) / 0.16f);
                    col *= 1f + (m - 0.5f) * 0.38f * detail - freckle * 0.20f * detail;
                }
            }

            // The slime coat: a clear gloss over the flesh, thicker and shinier on the underside.
            float wet = row.Wet * (0.80f + 0.20f * ny);
            col = col * (1f - 0.10f * wet);
            col = col + gloss * wet - col * gloss * wet;  // screen: gloss lightens without clipping

            if (look.Dye > 0f) col = DrawHelpers.BoostSaturation(col, look.Dye, DrawHelpers.SoftDye);
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

    // Suckers: real geometry, placed in material coordinates

    // Spacing and size go together: pitch 4% wider than the original 0.85 and cups 2% bigger than the original 0.50
    private const float SuckerRadiusFrac = 0.51f;  // cup radius, in tube radii (of the static profile)
    private const float SuckerRowAngle   = 0.72f;  // each row sits this far (rad) either side of the belly line
    private const float SuckerPitch      = 0.884f;  // spacing along a row, in tube diameters

    // Toward the tip the cups are tiny and a tentacle's point reads as a point whatever is on it
    private const float TipThinStart = 0.50f;  // fraction of the arm at which cups start to thin out
    private const float TipThinExtra = 1.00f;  // extra tip spacing: 1.0 = twice the spacing there, 0 = off
    private const float SuckerFirst      = 1.35f;  // the first cup, in pitches from the root
    private const float SuckerLift       = 1.20f;  // how tall a cup stands, in cup radii
    private const int   MaxCups          = 96;
    private const int   ProfileSteps     = 96;  // resolution of the table that places cups along the taper

    // The cup is a surface of revolution about the skin's normal.
    private static readonly float[] CupRhoFull    = { 1.64f, 1.40f, 1.20f, 1.10f, 1.00f, 0.94f, 0.88f, 0.80f, 0.72f, 0.65f, 0.60f, 0.52f, 0.40f, 0.24f, 0.00f };
    private static readonly float[] CupZFull      = { 0.00f, 0.00f, 0.02f, 0.14f, 0.34f, 0.54f, 0.70f, 0.80f, 0.78f, 0.66f, 0.48f, 0.30f, 0.19f, 0.15f, 0.20f };
    private static readonly float[] CupAlphaFull  = { 0.00f, 0.42f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f };
    // Sky openness per ring: the skirt sits in its own contact shadow, the cavity deep in the cup.
    private static readonly float[] CupOpenFull   = { 1.00f, 1.00f, 0.90f, 0.95f, 0.98f, 1.00f, 1.00f, 1.06f, 1.00f, 0.92f, 0.76f, 0.58f, 0.46f, 0.42f, 0.50f };
    private const int FirstFleshRing = 2;  // rings before this (in the FULL profile) are the contact shadow

    /// <summary>Which of the full profile's rings are built.</summary>
    private static readonly int[] CupRings = { 0, 1, 2, 4, 5, 7, 8, 10, 11, 14 };
    private static readonly int FirstFlesh = Array.IndexOf(CupRings, FirstFleshRing);  // the same boundary, as an index into the rings actually built

    private static float[] PickRings(float[] full) { var t = new float[CupRings.Length]; for (int i = 0; i < t.Length; i++) t[i] = full[CupRings[i]]; return t; }
    private static readonly float[] CupRho = PickRings(CupRhoFull), CupZ = PickRings(CupZFull), CupAlpha = PickRings(CupAlphaFull), CupOpen = PickRings(CupOpenFull);
    private static readonly int FirstCavity = Array.IndexOf(CupRings, 10);  // rings from here on are inside the cup (ring 10 of the full profile on)
    private static readonly Vector3 CupTint = new(1.00f, 0.78f, 0.84f);  // inside a cup the flesh is pinker and softer

    /// <summary>The profile's outward normal at each ring, as (along rho, along z).</summary>
    private static readonly (float Rho, float Z)[] CupNormal = ProfileNormals();

    private static (float, float)[] NormalsOf(float[] rho, float[] z)
    {
        int n = rho.Length;
        var normals = new (float, float)[n];
        for (int j = 0; j < n; j++)
        {
            int a = Math.Max(0, j - 1), b = Math.Min(n - 1, j + 1);
            float dRho = rho[b] - rho[a], dZ = z[b] - z[a];
            float len = MathF.Sqrt(dRho * dRho + dZ * dZ);
            // The profile runs inward over the top of the solid, so its outward normal is (dz, -drho).
            normals[j] = len > 1e-6f ? (dZ / len, -dRho / len) : (0f, 1f);
        }
        normals[n - 1] = (0f, 1f);  // the axis: straight out
        return normals;
    }

    private static (float, float)[] ProfileNormals()
    {
        var normals = NormalsOf(CupRhoFull, CupZFull);
        var picked = new (float, float)[CupRings.Length];
        for (int i = 0; i < picked.Length; i++) picked[i] = normals[CupRings[i]];
        return picked;
    }

    // A cup's segment count follows its screen size: a 6 px cup doesn't need 24 segments.
    private static readonly float[][] CupSinBy = CircleTables(false), CupCosBy = CircleTables(true);
    private static readonly int[] StrideOf = { 9, 11, 13, 15, 17, 19, 21, 23, 25 };  // segments 8, 10, ... 24, plus one
    private const int StrideShift = 20;  // quad = (stride code << 20) | first vertex

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

    private const int MaxBeads = 32;  // slime beads per tentacle (see the film below)
    private const int MaxCupVerts = MaxCups * 15 * 25 + MaxBeads * 5 * 11;  // cups and beads share one vertex pool and one painter's pool
    private const int MaxCupQuads = MaxCups * 14 * 24 + MaxBeads * 4 * 10;

    private readonly Vector2[] _cupPos = new Vector2[MaxCupVerts];
    private readonly uint[]    _cupCol = new uint[MaxCupVerts];
    private readonly float[]   _cupZ   = new float[MaxCupVerts];  // depth toward the viewer
    private readonly float[]   _cupNz  = new float[MaxCupVerts];  // the normal's component toward the viewer
    private readonly float[]   _cupSide = new float[MaxCupVerts];  // cos of the angle round the tube: > 0 facing the viewer
    private int _cupVerts;
    private int[] _remap = new int[MaxCupVerts];

    // Viewer-facing quads, split by whether they lie behind the tube's silhouette plane (drawn before the body).
    private readonly float[] _keyBack = new float[MaxCupQuads], _keyFront = new float[MaxCupQuads];
    private readonly int[]   _quadBack = new int[MaxCupQuads],  _quadFront = new int[MaxCupQuads];
    private int _nBack, _nFront;

    private readonly float[] _profileN = new float[ProfileSteps + 1];
    private readonly MeshBuilder _cupMeshBack = new(), _cupMeshFront = new();

    /// <summary>Builds every sucker.</summary>
    private int BuildSuckers(StrandPath path, Tier tier, in Look look, float px)
    {
        _cupVerts = 0; _nBack = 0; _nFront = 0;
        ref readonly RowParams p = ref _rp;
        int pn = Math.Min(path.Count, MaxPathPoints);
        float total = p.Total, diameter = p.Diameter;
        bool closed = p.Closed;
        float scaleN = total / (SuckerPitch * diameter);  // cups per row along the whole arm, if it did not taper

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
        {
            float uMid = (i + 0.5f) / ProfileSteps;
            float openUp = 1f + TipThinExtra * DrawHelpers.Smooth((uMid - TipThinStart) / (1f - TipThinStart));
            _profileN[i + 1] = _profileN[i] + scaleN / (ProfileSteps * Taper(uMid, false) * openUp);
        }
        float nTotal = _profileN[ProfileSteps];

        for (int m = 0; cups < MaxCups; m++)
        {
            for (int r = 0; r < 2; r++)
            {
                float n = SuckerFirst + m + 0.5f * r;  // the second row sits half a pitch behind the first
                if (n >= nTotal - 0.6f) return cups;
                float u = UFromN(n);
                if (u * total > p.VisibleLen) return cups;  // beyond the growing tip; later ones are further still
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

    /// <summary>Builds one cup at fraction u of the way along the arm, in belt rowIndex (0 or 1).</summary>
    private bool AddSucker(float u, int rowIndex, StrandPath path, int pn, Tier tier, in Look look, float px)
    {
        ref readonly RowParams p = ref _rp;
        float sArc = u * p.Total;

        // Size comes from the static profile, so a cup doesn't pulse with the swell.
        float refR = 0.5f * p.Diameter * Taper(u, p.Closed);
        float grow = p.Closed ? 1f : DrawHelpers.Smooth((p.VisibleLen - sArc) / (1.7f * refR + 1f));
        float small = DrawHelpers.Smooth((2f * refR - 6f * px) / (7f * px));  // fades out smoothly as the arm thins to nothing
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

        // Where round the tube it sits, as an angle from the view axis; well behind the silhouette it can't be seen.
        float belt = rowIndex == 0 ? +SuckerRowAngle : -SuckerRowAngle;
        float theta = WrapAngle(belt - row.Psi);
        if (MathF.Cos(theta) < -0.72f) return false;

        BuildCup(in row, theta, rc, k, tier, in look);
        if (_obstN < _obst.Length) _obst[_obstN++] = (sArc, belt, rc);  // the film flows round it
        return true;
    }

    private void BuildCup(in Row row, float theta, float rc, float visible, Tier tier, in Look look)
    {
        int segs = Math.Clamp((int)(rc * CupSegPerPx) & ~1, 8, tier.SuckerSegs), rings = CupRho.Length, stride = segs + 1;
        float[] cupSin = CupSinBy[segs], cupCos = CupCosBy[segs];
        int strideCode = (segs - 8) >> 1;
        int vb = _cupVerts;
        if (vb + rings * stride > MaxCupVerts) return;

        Vector2 T = row.T;
        Vector2 perp = new(-T.Y, T.X);
        float sa = row.SinA, ca = row.CosA;
        float skin = row.Radius;
        float lift = SuckerLift * rc;
        Vector3 axial = new(T.X * ca, T.Y * ca, sa);  // the tube's axis direction in view space
        float alphaK = visible * look.Alpha;
        float wet = MathF.Max(0.65f, row.Wet);
        Vector2 shadowShift = StudioLighting.ShadowDirection * (rc * 0.30f);

        // Every vertex of the cup sits at the cup's own angle plus a small offset d = y / skin
        (float sTh, float cTh) = MathF.SinCos(theta);
        (float sTp, float cTp) = MathF.SinCos(theta + row.Psi);
        float invSkin = 1f / skin;

        for (int j = 0; j < rings; j++)
        {
            float rho = CupRho[j] * rc;
            float z = CupZ[j] * lift;
            (float nR, float nZ) = CupNormal[j];
            RingShade shade = new(j, in row, wet, alphaK);

            // The last ring is the cup's axis: rho = 0, so every vertex on it is the same vertex. Work it out once.
            int count = j == rings - 1 ? 1 : stride;
            for (int i = 0; i < count; i++)
            {
                float cs = cupCos[i], sn = cupSin[i];
                float x = rho * cs, y = rho * sn;  // along the tube, and round it, in px

                // The cup lies on the tube's surface, not a flat plane: moving round it by y turns the radial direction by y/R.
                float d = y * invSkin, sD, cD;
                if (MathF.Abs(d) <= 1.2f)
                {
                    // Taylor series to d^9 and d^10: good to ~2e-7 over |d| <= 1.2, which every cup on a tube stays inside.
                    float d2 = d * d;
                    sD = d * (1f + d2 * (-1f / 6f + d2 * (1f / 120f + d2 * (-1f / 5040f + d2 * (1f / 362880f)))));
                    cD = 1f + d2 * (-0.5f + d2 * (1f / 24f + d2 * (-1f / 720f + d2 * (1f / 40320f + d2 * (-1f / 3628800f)))));
                }
                else (sD, cD) = MathF.SinCos(d);
                float sT = sTh * cD + cTh * sD, cT = cTh * cD - sTh * sD;
                Vector3 ncyl = new(perp.X * sT - T.X * sa * cT, perp.Y * sT - T.Y * sa * cT, ca * cT);
                Vector3 round = new(perp.X * cT + T.X * sa * sT, perp.Y * cT + T.Y * sa * sT, -ca * sT);

                Vector3 pos = new Vector3(row.P.X, row.P.Y, 0f) + axial * x + ncyl * (skin + z);
                Vector3 radial = axial * cs + round * sn;  // the cup's own outward direction at this angle
                // axial, ncyl and round are an orthonormal frame, so radial is a unit vector at right angles to ncyl
                Vector3 n3 = radial * nR + ncyl * nZ;

                int v = vb + j * stride + i;
                Vector2 screen = new(pos.X, pos.Y);
                if (j < FirstFlesh) screen += shadowShift;
                _cupPos[v] = screen;
                _cupZ[v] = pos.Z;
                _cupNz[v] = n3.Z;
                _cupSide[v] = cT;
                _cupCol[v] = CupColour(in shade, n3, cTp * cD - sTp * sD, in look);
            }
            for (int i = count; i < stride; i++)
            {
                int v = vb + j * stride + i, v0 = vb + j * stride;
                _cupPos[v] = _cupPos[v0]; _cupZ[v] = _cupZ[v0]; _cupNz[v] = _cupNz[v0]; _cupSide[v] = _cupSide[v0]; _cupCol[v] = _cupCol[v0];
            }
        }
        _cupVerts = vb + rings * stride;

        PushQuads(vb, rings, segs, stride, strideCode, CupZ, lift, theta, FirstFlesh);
    }

    /// <summary>Sorts the quads of one just-built surface blob (cup or bead) into the painter's pools; zTab is height per ring.</summary>
    private void PushQuads(int vb, int rings, int segs, int stride, int strideCode, float[] zTab, float lift, float theta, int firstFlesh)
    {
        // Quads that can be seen.
        float near = DrawHelpers.Smooth((MathF.Cos(theta) + 0.72f) / 0.87f);  // 0: cup well behind the arm; 1: at or in front of its edge
        float frontFrom = -0.55f * near;

        for (int j = 0; j < rings - 1; j++)
        for (int i = 0; i < segs; i++)
        {
            int v0 = vb + j * stride + i, v1 = v0 + 1, v2 = v0 + stride, v3 = v2 + 1;
            float nz = 0.25f * (_cupNz[v0] + _cupNz[v1] + _cupNz[v2] + _cupNz[v3]);
            if (nz < -0.35f) continue;
            float z = 0.25f * (_cupZ[v0] + _cupZ[v1] + _cupZ[v2] + _cupZ[v3]);
            float height = 0.5f * (zTab[j] + zTab[j + 1]) * lift;
            float key = z + 0.35f * height;  // taller parts win a tie where the depths barely differ

            // Which side of the body a quad is on is a question about the tube's cross-section
            float side = 0.25f * (_cupSide[v0] + _cupSide[v1] + _cupSide[v2] + _cupSide[v3]);
            int code = (strideCode << StrideShift) | v0;

            if (side >= frontFrom) { if (_nFront < MaxCupQuads) { _keyFront[_nFront] = key; _quadFront[_nFront++] = code; } }
            else if (j >= firstFlesh)  // the contact shadow lies on skin the arm hides: never behind it
            {
                if (_nBack < MaxCupQuads) { _keyBack[_nBack] = key; _quadBack[_nBack++] = code; }
            }
        }
    }

    /// <summary>What one ring of a cup needs that doesn't change vertex to vertex round it, worked out once per ring.</summary>
    private readonly struct RingShade
    {
        public readonly bool Shadow, Cavity, Fixed;
        public readonly float Alpha, Blend, ToneOpen, Wet, Keep;
        public readonly Vector3 FixedTone;  // full tint once the skirt has blended into the cup's flesh

        public RingShade(int ring, in Row row, float wet, float alphaK)
        {
            Alpha = CupAlpha[ring] * alphaK;
            Shadow = ring < FirstFlesh;
            Cavity = ring >= FirstCavity;
            Blend = DrawHelpers.Smooth((CupRings[ring] - FirstFleshRing) / 3f);  // by the full profile's ring, so the skirt fades over the same distance
            ToneOpen = row.Tone * CupOpen[ring];
            Wet = wet * (Cavity ? 0.8f : 1f);  // the same clear gloss as the body, so the cups and the skin agree
            Keep = 1f - 0.10f * Wet;
            Fixed = Blend >= 0.999f;
            FixedTone = Fixed ? VentralTint * 1.04f * ToneOpen * (Cavity ? CupTint : Vector3.One) : Vector3.Zero;
        }
    }

    private static uint CupColour(in RingShade r, Vector3 n, float belly, in Look look)
    {
        if (r.Shadow)
        {
            // The contact shadow: the same tint as the tentacle's cast shadow, a little darker.
            return DrawHelpers.WithAlpha(StrandShading.ShadowTint, r.Alpha * 0.9f * DrawHelpers.Smooth(n.Z / 0.45f));
        }

        // The skirt is a fillet: at its foot it takes the colour of the skin it grows from.
        Matcap.SamplePair(Flesh, Coat, n.X, n.Y, out Vector3 flesh, out Vector3 gloss);
        Vector3 col;
        if (r.Fixed) col = flesh * r.FixedTone;  // most rings: nothing left of the skin's tint to blend
        else
        {
            Vector3 skinTint = Vector3.Lerp(DorsalTint, VentralTint, DrawHelpers.Smooth(belly + 0.2f));
            Vector3 tint = Vector3.Lerp(skinTint, VentralTint * 1.04f, r.Blend);
            col = flesh * (tint * r.ToneOpen);
            if (r.Cavity) col *= CupTint;  // inside a cup the flesh is pinker and softer
        }

        col = col * r.Keep;
        col = col + gloss * r.Wet - col * gloss * r.Wet;

        if (look.Dye > 0f) col = DrawHelpers.BoostSaturation(col, look.Dye, DrawHelpers.SoftDye);
        if (look.Agit > 0.01f) col += col * (col * (0.45f * look.Agit));
        if (look.Fog > 0f) col = Vector3.Lerp(col, StrandShading.DepthFog, look.Fog);

        return DrawHelpers.Tint(DrawHelpers.Pack(col.X, col.Y, col.Z), r.Alpha, look.Overridden);
    }

    /// <summary>Draws the cup quads behind the tube's silhouette plane (before the body) or in front of it (after), far to near.</summary>
    private void FlushCups(ImDrawListPtr dl, bool behind, Vector2 uv)
    {
        int n = behind ? _nBack : _nFront;
        if (n == 0) return;
        float[] keys = behind ? _keyBack : _keyFront;
        int[] quads = behind ? _quadBack : _quadFront;
        MeshBuilder mesh = behind ? _cupMeshBack : _cupMeshFront;

        SortByKey(keys, quads, n);  // far first: the painter's algorithm

        mesh.Clear();
        mesh.Reserve(_cupVerts, 6 * n);  // the most this group can need: no per-vertex or per-triangle capacity test below
        if (n * 3 >= _cupVerts)
        {
            // Most of the pool is in use (the group facing the viewer): copy it whole and index it directly.
            mesh.AddBlockReserved(_cupPos, _cupCol, _cupVerts);
            for (int q = 0; q < n; q++)
            {
                int code = quads[q];
                int v0 = code & ((1 << StrideShift) - 1), stride = StrideOf[code >> StrideShift];
                mesh.QuadReserved(v0, v0 + 1, v0 + stride, v0 + stride + 1);
            }
        }
        else
        {
            // A sparse group (what the arm hides): only the vertices its triangles reference.
            Array.Fill(_remap, -1, 0, _cupVerts);
            for (int q = 0; q < n; q++)
            {
                int code = quads[q];
                int v0 = code & ((1 << StrideShift) - 1), stride = StrideOf[code >> StrideShift];
                int a = Use(mesh, v0), b = Use(mesh, v0 + 1), c = Use(mesh, v0 + stride), d = Use(mesh, v0 + stride + 1);
                mesh.QuadReserved(a, b, c, d);
            }
        }
        mesh.Flush(dl, uv);
    }

    private readonly uint[] _sortKeyA = new uint[MaxCupQuads], _sortKeyB = new uint[MaxCupQuads];
    private readonly int[]  _sortValB = new int[MaxCupQuads];
    private readonly int[]  _sortHist = new int[3 * 2048];

    /// <summary>Sorts quads[0..n) by keys[0..n), ascending.</summary>
    private void SortByKey(float[] keys, int[] quads, int n)
    {
        uint[] ka = _sortKeyA, kb = _sortKeyB;
        int[] vb = _sortValB, h = _sortHist;
        Array.Clear(h, 0, h.Length);
        for (int i = 0; i < n; i++)
        {
            uint u = BitConverter.SingleToUInt32Bits(keys[i]);
            u = (u & 0x80000000u) != 0 ? ~u : (u | 0x80000000u);  // negative floats sort below positive ones, and larger magnitude means smaller
            ka[i] = u;
            h[u & 2047]++; h[2048 + ((u >> 11) & 2047)]++; h[4096 + (u >> 22)]++;
        }
        for (int d = 0; d < 3; d++)
        {
            int sum = 0, o = d * 2048;
            for (int b = 0; b < 2048; b++) { int c = h[o + b]; h[o + b] = sum; sum += c; }
        }
        for (int i = 0; i < n; i++) { uint u = ka[i]; int pos = h[u & 2047]++;               kb[pos] = u; vb[pos] = quads[i]; }
        for (int i = 0; i < n; i++) { uint u = kb[i]; int pos = h[2048 + ((u >> 11) & 2047)]++; ka[pos] = u; quads[pos] = vb[i]; }
        for (int i = 0; i < n; i++) { uint u = ka[i]; int pos = h[4096 + (u >> 22)]++;        kb[pos] = u; vb[pos] = quads[i]; }
        Array.Copy(vb, quads, n);
    }

    private int Use(MeshBuilder mesh, int v)
    {
        int m = _remap[v];
        if (m < 0) _remap[v] = m = mesh.VertexReserved(_cupPos[v], _cupCol[v]);
        return m;
    }

    private static float WrapAngle(float a)
    {
        a %= MathF.Tau;
        if (a > MathF.PI) a -= MathF.Tau;
        else if (a < -MathF.PI) a += MathF.Tau;
        return a;
    }

    // The slime film: beads that live ON the skin

    private static readonly LiquidMatcap SlimeMatcap = new(LiquidPresets.Slime);

    // A sessile drop in profile, soft contact edge to crown: radius and height as fractions of the bead's radius.
    private static readonly float[] BeadRho   = { 1.12f, 0.966f, 0.766f, 0.42f, 0.00f };  // a spherical cap: shoulder at 75 degrees, 50, 25, then the crown
    private static readonly float[] BeadZ     = { 0.00f, 0.26f,  0.64f,  0.91f, 1.00f };
    private static readonly float[] BeadAlpha = { 0.00f, 1.00f,  1.00f,  1.00f, 1.00f };
    private static readonly (float, float)[] BeadNormal = NormalsOf(BeadRho, BeadZ);
    private const float BeadHeight   = 0.92f;  // crown height as a fraction of the radius: a flattened drop, not a ball
    private const float BeadDensity  = 0.48f;
    private const float RoundDrift   = 0.35f;
    private const float BeadRimGap   = 1.30f;  // a bead keeps this many cup radii from a cup's axis (skirt ends at 1.2)

    private struct Bead
    {
        public float U, Beta;  // material coordinates
        public float Size;  // radius in px
        public float Age, Life;  // seconds
        public float Held;  // seconds pressed against a cup's rim: the longer, the more it gathers
        public float Vs, Vr;  // velocity along the arm and round it, px/s
        public int   Seed;
    }

    private sealed class Film
    {
        public readonly Bead[] B = new Bead[MaxBeads];
        public int N, Born;
        public float Last = float.NaN, Debt;
    }

    private readonly Dictionary<int, Film> _films = new();
    private readonly List<int> _filmStale = new();
    private float _filmSweep;

    private readonly (float S, float Beta, float R)[] _obst = new (float, float, float)[MaxCups];  // cups this frame: arc length, belt angle, radius
    private int _obstN, _rowCount;

    private readonly Vector2[] _gPos = new Vector2[MaxBeads];
    private readonly float[]   _gRad = new float[MaxBeads], _gStr = new float[MaxBeads];
    private int _gN;

    /// <summary>The skin at one arc length, blended from the two rows either side of it.</summary>
    private struct Skin { public Vector2 P, T; public float R, Psi, Sa, Ca; public bool On; }

    private Skin SampleSkin(float s)
    {
        int lo = 0, hi = _rowCount - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (_rows[mid].Arc <= s) lo = mid; else hi = mid;
        }
        ref readonly Row a = ref _rows[lo];
        ref readonly Row b = ref _rows[hi];
        float span = b.Arc - a.Arc;
        float f = span > 1e-4f ? Math.Clamp((s - a.Arc) / span, 0f, 1f) : 0f;

        Vector2 t = Vector2.Lerp(a.T, b.T, f);
        float tl = t.Length();
        float sa = a.SinA + (b.SinA - a.SinA) * f, ca = a.CosA + (b.CosA - a.CosA) * f;
        float pl = MathF.Sqrt(sa * sa + ca * ca);
        return new Skin
        {
            P = Vector2.Lerp(a.P, b.P, f),
            T = tl > 1e-5f ? t / tl : a.T,
            R = a.Radius + (b.Radius - a.Radius) * f,
            Psi = a.Psi + (b.Psi - a.Psi) * f,
            Sa = pl > 1e-5f ? sa / pl : 0f,
            Ca = pl > 1e-5f ? ca / pl : 1f,
            On = a.On && b.On,
        };
    }

    /// <summary>Advances this tentacle's film and builds its beads into the cup pool. Returns how many were built.</summary>
    private int BuildFilm(int key, in Look look, float time)
    {
        ref readonly RowParams p = ref _rp;
        _gN = 0;
        if (p.VisibleLen < p.Diameter * 1.5f) return 0;

        if (!_films.TryGetValue(key, out Film? film))
        {
            film = new Film();
            _films[key] = film;
            int pre = (int)(BeadDensity * p.VisibleLen / 100f * 5f);  // a tentacle that has just appeared is not bare
            for (int i = 0; i < pre; i++) SpawnBead(film, key, aged: true);
        }

        float dt = float.IsNaN(film.Last) ? 0f : Math.Clamp(time - film.Last, 0f, 0.05f);
        film.Last = time;

        if (time - _filmSweep > 1f)  // forget films whose tentacle is gone
        {
            _filmSweep = time;
            _filmStale.Clear();
            foreach (var kv in _films) if (time - kv.Value.Last > 1.5f) _filmStale.Add(kv.Key);
            foreach (int k in _filmStale) _films.Remove(k);
        }

        film.Debt += BeadDensity * (p.VisibleLen / 100f) * dt;
        while (film.Debt >= 1f) { film.Debt -= 1f; SpawnBead(film, key, aged: false); }

        StepBeads(film, dt);

        int built = 0;
        for (int i = 0; i < film.N; i++)
            if (BuildBead(in film.B[i], in look)) built++;
        return built;
    }

    private void SpawnBead(Film f, int key, bool aged)
    {
        if (f.N >= MaxBeads) return;
        ref readonly RowParams p = ref _rp;
        int h = unchecked(key * 7919 + f.Born++ * 104729);

        float sMin = 0.6f * p.Diameter, sMax = p.VisibleLen - 0.3f * p.Diameter;
        if (sMax <= sMin) return;
        float s = DrawHelpers.HashRange(h + 1, sMin, sMax);
        Skin k = SampleSkin(s);
        if (!k.On) return;

        ref Bead b = ref f.B[f.N++];
        float thick = Math.Clamp(MathF.Sqrt(p.Diameter / (110f * p.Px)), 0.7f, 1.25f);
        b.U = s / p.Total;
        b.Beta = DrawHelpers.HashRange(h + 2, -1.25f, 0.55f) + k.Psi;  // faces the viewer, leaning to the side gravity will carry it
        b.Size = DrawHelpers.HashRange(h + 3, 4.5f, 8.75f) * p.Px * thick;
        b.Life = DrawHelpers.HashRange(h + 4, 4.5f, 8f);
        b.Age = aged ? DrawHelpers.Hash01(h + 5) * b.Life * 0.8f : 0f;
        b.Held = 0f; b.Vs = 0f; b.Vr = 0f; b.Seed = h;
    }

    private void StepBeads(Film f, float dt)
    {
        ref readonly RowParams p = ref _rp;
        float px = p.Px, total = p.Total, tipLimit = p.VisibleLen - 0.1f * p.Diameter;

        for (int i = 0; i < f.N;)
        {
            ref Bead b = ref f.B[i];
            b.Age += dt;
            float s = b.U * total;
            if (b.Age >= b.Life || s > tipLimit || s < 0f) { f.B[i] = f.B[--f.N]; continue; }
            i++;
            if (dt <= 0f) continue;

            Skin k = SampleSkin(s);
            float R = MathF.Max(4f, k.R);
            (float sT, float cT) = MathF.SinCos(b.Beta - k.Psi);
            Vector2 perp = new(-k.T.Y, k.T.X);

            // Gravity (screen-down) resolved on the skin, along and round the arm, in the frame the cups are built in.
            float gu = k.T.Y * k.Ca;
            float gt = perp.Y * cT + k.T.Y * k.Sa * sT;
            float gm = MathF.Sqrt(gu * gu + gt * gt);
            float push = DrawHelpers.Smooth((gm - 0.10f) / 0.30f);  // on a near-level patch the film is held by its own stickiness
            float speed = (24f + 2f * b.Size / px) * px;  // a heavier bead runs faster
            float relax = 1f - MathF.Exp(-dt / 0.35f);
            b.Vs += (speed * gu * push - b.Vs) * relax;
            b.Vr += (speed * RoundDrift * gt * push - b.Vr) * relax;
            if (cT < 0.18f) b.Age += dt * 1.8f;  // reached the underside edge: it runs off into the drips instead of piling up

            float sNew = s + b.Vs * dt, betaNew = b.Beta + b.Vr * dt / R;
            float rb = MathF.Min(b.Size * (1f + 0.45f * DrawHelpers.Smooth(b.Held / 1.4f)), 0.6f * R);
            bool held = false;

            // Cups are obstacles, in the skin's own (arc length, round-the-tube) plane.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int c = 0; c < _obstN; c++)
                {
                    ref readonly var o = ref _obst[c];
                    float reach = BeadRimGap * o.R + 0.7f * rb;
                    float dx = sNew - o.S;
                    if (dx > reach || dx < -reach) continue;
                    float dy = R * WrapAngle(betaNew - o.Beta);
                    float d2 = dx * dx + dy * dy;
                    if (d2 >= reach * reach) continue;

                    float d = MathF.Sqrt(d2), nx, ny;
                    if (d > 1e-3f) { nx = dx / d; ny = dy / d; }
                    else { nx = gu >= 0f ? -1f : 1f; ny = 0f; }

                    sNew = o.S + nx * reach;  // out of the cup...
                    betaNew = o.Beta + ny * reach / R + (betaNew - o.Beta - WrapAngle(betaNew - o.Beta));
                    float vin = b.Vs * nx + b.Vr * ny;  // ...and no more motion into it
                    if (vin < 0f)
                    {
                        b.Vs -= vin * nx; b.Vr -= vin * ny;
                        if (vin < -3f * px) held = true;
                        // Dead upstream of the cup the sideways part is zero: a bead would balance there for ever.
                        float side = (b.Seed & 1) == 0 ? 1f : -1f;
                        b.Vs += -ny * side * 9f * px * dt * 8f;
                        b.Vr +=  nx * side * 9f * px * dt * 8f;
                    }
                }
            }

            b.Held = held ? b.Held + dt : MathF.Max(0f, b.Held - 2f * dt);
            b.U = sNew / total;
            b.Beta = betaNew;
            if (b.Beta > 12f) b.Beta -= MathF.Tau; else if (b.Beta < -12f) b.Beta += MathF.Tau;
        }
    }

    /// <summary>One bead as a small dome on the skin, appended to the cup vertex pool and sorted with the cups.</summary>
    private bool BuildBead(in Bead b, in Look look)
    {
        ref readonly RowParams p = ref _rp;
        float s = b.U * p.Total;
        Skin k = SampleSkin(s);
        if (!k.On) return false;

        float theta = WrapAngle(b.Beta - k.Psi);
        if (MathF.Cos(theta) < -0.30f) return false;  // round the back of the arm: hidden, and nothing of it shows past the edge

        float skin = MathF.Max(4f, k.R);
        float fade = DrawHelpers.Smooth(b.Age / 0.7f) * (1f - DrawHelpers.Smooth((b.Age - (b.Life - 1.0f)) / 1.0f));
        float rb = MathF.Min(b.Size * (1f + 0.45f * DrawHelpers.Smooth(b.Held / 1.4f)), 0.6f * skin) * (0.45f + 0.55f * fade);
        if (rb < 1.4f * p.Px || fade <= 0.01f) return false;

        int segs = rb > 6.5f * p.Px ? 10 : 8, rings = BeadRho.Length, stride = segs + 1;
        float[] bsin = CupSinBy[segs], bcos = CupCosBy[segs];
        int strideCode = (segs - 8) >> 1;
        int vb = _cupVerts;
        if (vb + rings * stride > MaxCupVerts) return false;

        Vector2 T = k.T, perp = new(-T.Y, T.X);
        float sa = k.Sa, ca = k.Ca;
        float lift = BeadHeight * rb;
        Vector3 axial = new(T.X * ca, T.Y * ca, sa);
        float alphaK = fade * look.Alpha;
        (float sTh, float cTh) = MathF.SinCos(theta);
        float invSkin = 1f / skin;

        // A bead on the move is drawn out along its way and a little flatter, as a drop of a viscous liquid is.
        float speed = MathF.Sqrt(b.Vs * b.Vs + b.Vr * b.Vr);
        float stretch = 0.55f * DrawHelpers.Smooth(speed / (45f * p.Px));
        (float fx, float fy) = speed > 1e-3f ? (b.Vs / speed, b.Vr / speed) : (1f, 0f);

        for (int j = 0; j < rings; j++)
        {
            float rho = BeadRho[j] * rb, z = BeadZ[j] * lift;
            (float nR, float nZ) = BeadNormal[j];
            float ringA = BeadAlpha[j] * alphaK;
            int count = j == rings - 1 ? 1 : stride;
            for (int i = 0; i < count; i++)
            {
                float cs = bcos[i], sn = bsin[i];
                float x = rho * cs, y = rho * sn;
                float par = x * fx + y * fy;
                x += fx * par * stretch; y += fy * par * stretch;

                float d = y * invSkin, d2 = d * d;
                float sD = d * (1f + d2 * (-1f / 6f + d2 * (1f / 120f)));  // a bead is small against the tube: d stays well inside the series
                float cD = 1f + d2 * (-0.5f + d2 * (1f / 24f));
                float sT = sTh * cD + cTh * sD, cT = cTh * cD - sTh * sD;
                Vector3 ncyl = new(perp.X * sT - T.X * sa * cT, perp.Y * sT - T.Y * sa * cT, ca * cT);
                Vector3 round = new(perp.X * cT + T.X * sa * sT, perp.Y * cT + T.Y * sa * sT, -ca * sT);

                Vector3 pos = new Vector3(k.P.X, k.P.Y, 0f) + axial * x + ncyl * (skin + z);
                Vector3 n3 = (axial * cs + round * sn) * nR + ncyl * nZ;

                int v = vb + j * stride + i;
                _cupPos[v] = new Vector2(pos.X, pos.Y);
                _cupZ[v] = pos.Z;
                _cupNz[v] = n3.Z;
                _cupSide[v] = cT;
                Vector4 lit = SlimeMatcap.Sample(n3.X, n3.Y);
                Vector3 col = new(lit.X, lit.Y, lit.Z);
                if (look.Dye > 0f) col = DrawHelpers.BoostSaturation(col, look.Dye, DrawHelpers.SoftDye);
                if (look.Fog > 0f) col = Vector3.Lerp(col, StrandShading.DepthFog, look.Fog);
                _cupCol[v] = DrawHelpers.Tint(DrawHelpers.Pack(col.X, col.Y, col.Z), lit.W * ringA, look.Overridden);
            }
            for (int i = count; i < stride; i++)
            {
                int v = vb + j * stride + i, v0 = vb + j * stride;
                _cupPos[v] = _cupPos[v0]; _cupZ[v] = _cupZ[v0]; _cupNz[v] = _cupNz[v0]; _cupSide[v] = _cupSide[v0]; _cupCol[v] = _cupCol[v0];
            }
        }
        _cupVerts = vb + rings * stride;
        PushQuads(vb, rings, segs, stride, strideCode, BeadZ, lift, theta, 0);

        // The hard core of the highlight is narrower than a bead's vertex spacing
        if (rb >= 2.2f * p.Px && cTh > 0.1f && _gN < MaxBeads)
        {
            Vector3 nBase = new(perp.X * sTh - T.X * sa * cTh, perp.Y * sTh - T.Y * sa * cTh, ca * cTh);
            float face = DrawHelpers.Smooth((Vector3.Dot(nBase, KeyHalf) - 0.30f) / 0.45f);
            if (face > 0.02f)
            {
                Vector3 centre = new Vector3(k.P.X, k.P.Y, 0f) + nBase * (skin + lift * 0.6f);
                _gPos[_gN] = new Vector2(centre.X, centre.Y) + new Vector2(KeyHalf.X, KeyHalf.Y) * (rb * 0.62f);
                _gRad[_gN] = rb;
                _gStr[_gN] = face * fade;
                _gN++;
            }
        }
        return true;
    }

    private void DrawBeadGlints(ImDrawListPtr dl, Vector2 uv, in Look look)
    {
        Span<float> rr = stackalloc float[3];
        Span<uint> cc = stackalloc uint[3];
        for (int i = 0; i < _gN; i++)
        {
            float rad = _gRad[i], a = _gStr[i] * look.Alpha;
            float g = MathF.Max(0.75f, rad * 0.30f);
            rr[0] = g * 0.55f; rr[1] = g * 0.95f; rr[2] = g * 1.7f;
            cc[0] = DrawHelpers.Tint(DrawHelpers.Pack(1.00f, 0.97f, 0.88f), 0.98f * a, look.Overridden);
            cc[1] = DrawHelpers.Tint(DrawHelpers.Pack(1.00f, 0.97f, 0.88f), 0.55f * a, look.Overridden);
            cc[2] = DrawHelpers.Tint(DrawHelpers.Pack(1.00f, 0.97f, 0.88f), 0f, look.Overridden);
            MeshDraw.Radial(dl, _gPos[i], uv, cc[0], 8, rr, cc, 0f, 1f, 0, 0f);
        }
    }

    // Sparkles: the wet skin's speculars: The mesh cannot carry detail finer than its vertex spacing, and a wet

    private const int   SparkleBins  = 360;  // along the arm; each bin holds at most one candidate
    private const float SparkleShare = 0.90f;  // how many bins do
    private static readonly float[] SparkJit = new float[SparkleBins], SparkSinB = new float[SparkleBins], SparkCosB = new float[SparkleBins],
                                    SparkTx = new float[SparkleBins], SparkTy = new float[SparkleBins], SparkSize = new float[SparkleBins];
    private static readonly bool[] SparkOn = new bool[SparkleBins];
    private readonly MeshBuilder _sparkMesh = new();

    static StrokeParasite()
    {
        for (int j = 0; j < SparkleBins; j++)
        {
            SparkOn[j]   = DrawHelpers.Hash01(j * 13 + 5) < SparkleShare;
            SparkJit[j]  = DrawHelpers.Hash01(j * 13 + 6);
            float beta   = DrawHelpers.HashRange(j * 13 + 7, -MathF.PI, MathF.PI);
            SparkSinB[j] = MathF.Sin(beta); SparkCosB[j] = MathF.Cos(beta);
            SparkTx[j]   = DrawHelpers.HashRange(j * 13 + 8, -0.55f, 0.55f);
            SparkTy[j]   = DrawHelpers.HashRange(j * 13 + 9, -0.55f, 0.55f);
            SparkSize[j] = DrawHelpers.HashRange(j * 13 + 10, 0.8f, 1.35f);
        }
    }

    private void DrawSparkles(ImDrawListPtr dl, int rowCount, Vector2 uv, in Look look)
    {
        ref readonly RowParams p = ref _rp;
        float px = p.Px, total = p.Total, binsPerPx = SparkleBins / total;
        int shift = (int)(p.BumpOff * 7.31f) % SparkleBins;  // where in the table this tentacle starts
        _sparkMesh.Clear();

        for (int i = 0; i < rowCount - 1; i++)
        {
            ref readonly Row r = ref _rows[i];
            ref readonly Row r2 = ref _rows[i + 1];
            if (!r.On || r.Arc < 0.5f * p.Diameter || r2.Arc > p.VisibleLen - 0.2f * p.Diameter) continue;

            // The candidates whose bin centre falls in this row's stretch of arm.
            int b0 = (int)(r.Arc * binsPerPx + 0.5f), b1 = (int)(r2.Arc * binsPerPx + 0.5f);
            if (b1 <= b0) continue;

            bool haveTrig = false;
            float sPsi = 0f, cPsi = 1f;
            Vector2 T = r.T, perp = new(-T.Y, T.X);
            float sa = r.SinA, ca = r.CosA;

            for (int b = b0; b < b1 && b < SparkleBins; b++)
            {
                int j = b + shift; if (j >= SparkleBins) j -= SparkleBins;
                if (!SparkOn[j]) continue;
                if (!haveTrig) { (sPsi, cPsi) = MathF.SinCos(r.Psi); haveTrig = true; }

                // theta = beta - Psi, by angle subtraction.
                float cT = SparkCosB[j] * cPsi + SparkSinB[j] * sPsi;
                if (cT < 0.12f) continue;  // turned away from the viewer
                float sT = SparkSinB[j] * cPsi - SparkCosB[j] * sPsi;

                Vector3 ncyl = new(perp.X * sT - T.X * sa * cT, perp.Y * sT - T.Y * sa * cT, ca * cT);
                if (Vector3.Dot(ncyl, KeyHalf) < 0.45f) continue;  // the facet tilts at most ~0.55, so the skin must be within reach of the light

                Vector3 axial = new(T.X * ca, T.Y * ca, sa);
                Vector3 round = new(perp.X * cT + T.X * sa * sT, perp.Y * cT + T.Y * sa * sT, -ca * sT);
                Vector3 f = ncyl + axial * SparkTx[j] + round * SparkTy[j];
                float nh = Vector3.Dot(f, KeyHalf) / f.Length();
                if (nh < 0.86f) continue;

                float x2 = nh * nh, x4 = x2 * x2, x8 = x4 * x4, x16 = x8 * x8;
                float spec = x16 * x4 * x2;  // nh^22: how narrow one facet's reflection is
                float a = DrawHelpers.Smooth(spec * 2.4f) * DrawHelpers.Smooth(cT / 0.30f) * r.Wet * look.Alpha;
                if (a < 0.04f) continue;

                float along = Math.Clamp((b + SparkJit[j]) / binsPerPx - r.Arc, 0f, r2.Arc - r.Arc);
                Vector2 c = r.P + T * along + new Vector2(ncyl.X, ncyl.Y) * MathF.Max(2f, r.Radius);
                float rad = (1.0f + 1.5f * spec) * px * SparkSize[j] * 1.6f;
                float foreshorten = 0.55f + 0.45f * cT;  // a facet on a turned-away patch is a narrower sliver

                // A hexagonal fan: bright at the centre, clear at the rim (six sides read as round at this size).
                uint hot  = DrawHelpers.Tint(DrawHelpers.Pack(1.00f, 0.98f, 0.90f), a, look.Overridden);
                uint none = hot & 0x00FFFFFFu;
                _sparkMesh.Reserve(7, 18);
                int v0 = _sparkMesh.VertexReserved(c, hot), first = 0;
                for (int q = 0; q < 6; q++)
                {
                    int v = _sparkMesh.VertexReserved(c + new Vector2(SparkHexCos[q] * rad * foreshorten, SparkHexSin[q] * rad), none);
                    if (q == 0) first = v; else _sparkMesh.TriReserved(v0, v - 1, v);
                }
                _sparkMesh.TriReserved(v0, first + 5, first);
            }
        }
        _sparkMesh.Flush(dl, uv);
    }

    private static readonly float[] SparkHexCos = { MathF.Cos(0.5f), MathF.Cos(0.5f + MathF.Tau / 6f), MathF.Cos(0.5f + 2 * MathF.Tau / 6f), MathF.Cos(0.5f + 3 * MathF.Tau / 6f), MathF.Cos(0.5f + 4 * MathF.Tau / 6f), MathF.Cos(0.5f + 5 * MathF.Tau / 6f) };
    private static readonly float[] SparkHexSin = { MathF.Sin(0.5f), MathF.Sin(0.5f + MathF.Tau / 6f), MathF.Sin(0.5f + 2 * MathF.Tau / 6f), MathF.Sin(0.5f + 3 * MathF.Tau / 6f), MathF.Sin(0.5f + 4 * MathF.Tau / 6f), MathF.Sin(0.5f + 5 * MathF.Tau / 6f) };

    // Glints

    /// <summary>A thin hard highlight where the key light reflects, its own ribbon over the body.</summary>
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

    // Tip

    private static void DrawTip(ImDrawListPtr dl, Vector2 tip, float bar, float k, in Look look, in MaterialContext ctx)
    {
        if (k <= 0.004f) return;
        // A slime bead on a still-reaching tentacle tip: a hard pinprick glint and the faintest sheen.
        uint halo = DrawHelpers.WithAlpha(DrawHelpers.Pack(0.82f, 0.95f, 0.55f), 0.11f * k * look.Alpha);
        uint clear = DrawHelpers.WithAlpha(DrawHelpers.Pack(0.82f, 0.95f, 0.55f), 0f);
        uint pip = DrawHelpers.WithAlpha(DrawHelpers.Pack(1.00f, 1.00f, 0.92f), 0.90f * k * look.Alpha);
        Span<float> rr = stackalloc float[2] { bar * 0.9f, bar * 2.3f };
        Span<uint> cc = stackalloc uint[2] { halo, clear };
        MeshDraw.Radial(dl, tip, MeshDraw.WhiteUv(ctx.Time), pip, 10, rr, cc, 0f, 1f, 0, 0f);
    }

    // What a tentacle sheds, and what happens when it is hit

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    /// <summary>Slime hanging off the underside in long stretching strings; heavier than a rope's dust, which is the point.</summary>
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
