namespace RealDebuffs.Effects.Framework.Materials;

/// A living tentacle as lit 3D geometry: tapered tube, pale belly, dark back, mottled skin, flowing slime
public sealed class StrokeParasite : IStrokeMaterial, ICoatLayeredHost, ICoatSurfaceProvider
{
    public string Name => "stroke.parasite";
    public string[] NaturalLanguageWords { get; } = { "tentacle", "tentacles", "tendril", "tendrils", "parasite", "parasites" };

    // size ---- wide enough for a limb a sixth of the screen thick, narrow enough to be a thread
    const float MinDiameterFrac = 0.012f, MaxDiameterFrac = 0.180f;
    const float TipFrac = 0.075f;  // diameter at the very tip, as a fraction of the base's
    const int   MaxRows = 1000, MaxPathPoints = 512;

    static readonly Matcap Flesh = new(SurfacePresets.ParasiteFlesh);
    static readonly Matcap Coat  = new(SurfacePresets.WetCoat);

    // Back and belly: multipliers on the flesh matcap; the belly paler and yellower, the back darker and greener.
    static readonly Vector3 DorsalTint  = new(0.72f, 0.80f, 0.70f);
    static readonly Vector3 VentralTint = new(1.22f, 1.14f, 0.92f);

    /// The half vector between the key light and the viewer: where a highlight sits on a surface.
    static readonly Vector3 KeyHalf = Vector3.Normalize(StudioLighting.Key + Vector3.UnitZ);
    static readonly Vector3 FillHalf = Vector3.Normalize(StudioLighting.Fill + Vector3.UnitZ);

    // Skin relief: warts (height as a fraction of the arm's radius, shading tilt) and folds.
    const float CupSegPerPx   = 0.90f, BumpHeightFrac = 0.16f, BumpTilt       = 1.00f;
    const float FoldDepth      = 0.02f;  // radius modulation of the folds
    const float FoldTilt       = 0.17f;  // and the tilt they give the shading
    const float CavityDark     = 0.26f;  // how dark the hollows between warts go
    const float CrestLift      = 0.11f;  // and how much the crests brighten

    /// Level of detail: columns across the visible half (even in ANGLE), rows along the length.
    sealed class Tier
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
                float th = PI * (c / (cols - 1f) - 0.5f);
                Theta[c] = th; Sin[c] = MathF.Sin(th); Cos[c] = MathF.Cos(th);
            }
            Sin[0] = -1f; Sin[cols - 1] = 1f; Cos[0] = 0f; Cos[cols - 1] = 0f;
        }
    }

    static readonly Tier Thick  = new(17, 0.20f, 24);  // a limb thick enough that 11 columns would show as blocks
    static readonly Tier Full   = new(11, 0.20f, 16);
    static readonly Tier Fine   = new(9,  0.24f, 12);
    static readonly Tier Medium = new(7,  0.32f, 0);
    static readonly Tier Coarse = new(5,  0.46f, 0);

    /// Everything about how this particular tentacle looks, resolved once per Draw.
    struct Look
    {
        public float Diameter, Radius, Fog, Agit, Alpha;
        public uint  AlphaBits;
        public bool  Overridden;
        public float Dye;
        public float Detail;  // 0..1: how much of the fine skin work is worth doing at this size
        public float MottleFreq;  // 1/px along the strand
        public float MottleOffset;  // per tentacle, so no two share a pattern
    }

    /// One cross-section of the tentacle: everything a vertex needs that does not depend on which column it is.
    struct Row
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

    /// Per-tentacle, per-frame constants a cross-section needs.
    struct RowParams
    {
        public float Total, VisibleLen, Reveal, Growing;
        public bool Closed;
        public float Diameter, Radius, Px, Depth, Time, Twist, Margin, ScreenW, ScreenH;
        public float FieldScale;  // size of the slime patches and highlight breakup, capped in pixels
        public float Ph1, Ph2, Ph3, Psi0, OffA, OffB, OffC, Scroll, WobbleRate, ToneRateA, ToneRateB, PitchRate, TwistRate;
        public float BumpWave, BumpOff, FoldWave;  // wart wavelength/offset; fold wavelength (px); BumpWave 0 = smooth
    }

    RowParams _rp;
    readonly Row[] _rows = new Row[MaxRows];
    readonly float[] _hgt = new float[MaxRows * 17];  // wart height (noise, ~[-1,1]) per row and column
    readonly PathRows _path = new(MaxPathPoints);
    float _screenW, _screenH;

    // The strand's profile: shared by the drawing and by RadiusAt

    static float BaseDiameter(float widthHint, float depth, float shortSide) =>
        Math.Clamp(widthHint, shortSide * MinDiameterFrac, shortSide * MaxDiameterFrac) * (1f - 0.18f * depth);

    /// Radius multiplier at fraction u of the strand's full length: thick at the root, drawn out to a fine tip.
    static float Taper(float u, bool closed) =>
        closed ? 0.92f : Lerp(TipFrac, 1f, Pow(Saturate(1f - u), 0.78f));

    /// <inheritdoc/>
    public float RadiusAt(in StrokePrimitive s, float arc, float shortSide)
    {
        float total = Max(1f, s.Path.Length);
        float diameter = BaseDiameter(s.WidthHint, Saturate(s.Depth), shortSide);
        return Max(0.5f, 0.5f * diameter * Taper(arc / total, s.Closed));
    }

    // Draw

    // Frame budget: Building a tentacle's mesh is the whole cost of drawing it, and a tentacle moves slowly.

    /// Milliseconds per frame this material may spend building tentacles; 0 = build all every frame.
    public static float BudgetMs = 1.5f;

    /// The longest a tentacle's picture may be left as it was, in seconds.
    public static float MaxStaleSeconds = 0.034f;

    const int MaxInterval = 3, MaxStaleFrames = 3;

    sealed class Cached
    {
        public readonly MeshDraw.MeshCache Mesh = new();
        public float Seen, CostMs = 0.2f;  // CostMs: what building this tentacle takes, smoothed
        public int BuiltFrame, Phase;  // Phase: which of the _interval frames this one is rebuilt on
    }

    readonly Dictionary<int, Cached> _cache = new();
    readonly List<int> _cacheStale = new();
    readonly List<Cached> _planned = new();
    float _frameTime = float.NaN, _cacheSweep;
    int _frameNo, _slot, _slotsLastFrame, _interval = 1;
    double _buildMsEma = 0.2;  // per tentacle, smoothed
    float _frameDt = 1f / 60f;  // smoothed time between frames

    Coat? _layerCoat;
    float _layerAlpha;

    /// Any matter: the coat lies on the body, behind the near-side suckers and in front of the far-side ones.
    public bool Layers(Coat coat) => true;

    public void DrawWithCoat(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx, Coat coat, float alpha)
    {
        _layerCoat = coat; _layerAlpha = alpha;
        try { Draw(dl, in s, in ctx); }
        finally { _layerCoat = null; }
    }

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        float budget = BudgetMs;
        if (budget <= 0f) { DrawBuild(dl, in s, in ctx); return; }

        if (ctx.Time != _frameTime) NewFrame(ctx.Time, budget);
        _slot++;

        int key = unchecked(s.Seed * 31 + (int)s.Owner);
        if (!_cache.TryGetValue(key, out Cached? c)) { c = new(); _cache[key] = c; }
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

    void NewFrame(float time, float budget)
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
            int cap = Math.Clamp((int)(MaxStaleSeconds / Max(_frameDt, 1e-3f)), 1, MaxInterval);  // fps-aware: a picture held for N frames must not be held too long
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

    int _planInterval;

    /// Gives each live tentacle a rebuild phase so the cost is level from frame to frame: the dearest first
    void Plan()
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

    void DrawBuild(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        if (path.Count < 2 || alpha <= 0.004f) return;

        _screenW = ctx.ScreenW;
        _screenH = ctx.ScreenH;
        if (!Setup(in s, ctx.ScreenScale, ctx.ShortSide, ctx.Time, alpha, ctx.ScreenW, ctx.ScreenH, ColorOverrideActive, ColorOverrideChroma,
                   out Tier tier, out Look look, out RowParams rp, out Cut cut)) return;
        _rp = rp;
        int n = BuildRows(in cut, in look);
        if (n < 2) return;

        float px = ctx.ScreenScale, depth = Saturate(s.Depth), radius = look.Radius, reveal = cut.Reveal, total = cut.Total, visibleLen = cut.VisibleLen;
        bool closed = cut.Closed;

        int cups = tier.SuckerSegs > 0 ? BuildSuckers(path, tier, in look, px) : 0;
        Vector2 cupUv = MeshDraw.WhiteUv(ctx.Time);

        DrawShadow(dl, n, depth, alpha, in ctx);
        if (cups > 0) FlushCups(dl, behind: true, cupUv);  // far-side cups: the body hides them except past its edge
        DrawBody(dl, n, tier, in look, in ctx);
        DrawGlints(dl, n, in look, in ctx);
        if (!closed && _rp.BumpWave > 0f) DrawSparkles(dl, n, cupUv, in look);
        if (_layerCoat != null && !DrawFilm(dl, n, tier, in look, in ctx, _layerCoat, _layerAlpha, closed, total))
            _layerCoat.DrawLayer(dl, _layerAlpha, in ctx);  // over the body, under the near-side suckers
        if (cups > 0) FlushCups(dl, behind: false, cupUv);  // cups on this side, over the body and its highlights

        if (s.TipFlare > 0.001f && !closed)
        {
            path.SampleAtArc(visibleLen, out Vector2 tip, out _);
            DrawTip(dl, tip, Max(radius * Taper(reveal, false), 1.6f * px), Saturate(s.TipFlare), in look, in ctx);
        }
    }

    // Cross-sections

    /// How a tentacle is cut into cross-sections: everything drawing and the coat chart share.
    struct Cut
    {
        public StrandPath Path;
        public int Pn, BodyRows;
        public bool Closed, DomeStart, DomeEnd;
        public float Total, VisibleLen, Reveal, CapStart, CapEnd, BodyStart, BodyEnd;

        /// Arc of body row i, 0..BodyRows.
        public readonly float Arc(int i) => BodyStart + (BodyEnd - BodyStart) * i / BodyRows;
    }

    /// This tentacle's size, level of detail, look, row parameters and cut, for drawing and the coat chart alike (false = nothing to show).
    bool Setup(in StrokePrimitive s, float px, float shortSide, float time, float alpha, float screenW, float screenH, bool overridden, float dye,
               out Tier tier, out Look look, out RowParams rp, out Cut cut)
    {
        tier = Coarse; look = default; rp = default; cut = default;
        var path = s.Path;
        float reveal = Saturate(s.Reveal);
        if (reveal <= 0.001f) return false;

        float depth = Saturate(s.Depth);
        float agit = Saturate(s.Agitation);
        bool closed = s.Closed;
        int seed = s.Seed;

        float diameter = BaseDiameter(s.WidthHint, depth, shortSide);
        float radius = diameter * 0.5f;
        float total = path.Length;
        float visibleLen = total * reveal;
        if (visibleLen < diameter * 0.8f) return false;

        tier = diameter >= 70f * px ? Thick : (diameter >= 22f * px ? Full : (diameter >= 15f * px ? Fine : (diameter >= 9f * px ? Medium : Coarse)));

        look = new Look
        {
            Diameter = diameter,
            Radius = radius,
            Fog = 0.55f * depth,
            Agit = agit,
            Alpha = alpha,
            AlphaBits = (uint)(int)(255f * (alpha > 0f ? (alpha < 1f ? alpha : 1f) : 0f)) << 24,
            Overridden = overridden,
            Dye = dye,
            Detail = Math.Clamp(diameter / (22f * px), 0.4f, 1f),
            MottleFreq = 1f / Min(diameter * 2.3f, 100f * px),  // skin detail is in px, not a fraction of the limb
            MottleOffset = HashRange(seed + 71, 0f, 300f),
        };

        int pn = Math.Min(path.Count, MaxPathPoints);
        _path.Prepare(path, pn, closed);

        // Rows are cut finely enough to carry the highlight breakup and the skin pattern
        float step = Math.Clamp(diameter * tier.StepFrac, 4.5f * px, 11f * px);

        // how this tentacle moves and looks, reseeded from its seed
        path.SampleAtArc(0f, out Vector2 basePos, out _);
        path.SampleAtArc(total, out Vector2 tipPos, out _);
        float flowSign = closed || tipPos.Y >= basePos.Y ? 1f : -1f;  // slime runs down the screen: toward whichever end is lower

        // Closed loops keep every periodic term to a whole number of cycles, so the seam cannot be found.
        float Rate(float wavelength) => closed ? Max(1f, Round(total / wavelength)) / total : 1f / wavelength;

        rp = new()
        {
            Total = total, VisibleLen = visibleLen, Reveal = reveal, Closed = closed,
            Growing = 1f - Smooth((reveal - 0.90f) / 0.10f),  // the front of a growing tentacle is drawn out to a point
            Diameter = diameter, Radius = radius, Px = px, Depth = depth, Time = time, Twist = s.Twist,
            Margin = diameter * 1.6f, ScreenW = screenW, ScreenH = screenH,
            FieldScale = Min(diameter, 70f * px),
            Ph1 = HashRange(seed + 81, 0f, Tau),
            Ph2 = HashRange(seed + 82, 0f, Tau),
            Ph3 = HashRange(seed + 83, 0f, Tau),
            Psi0 = HashRange(seed + 84, 0f, Tau),
            OffA = HashRange(seed + 85, 0f, 400f),
            OffB = HashRange(seed + 86, 0f, 400f),
            OffC = HashRange(seed + 87, 0f, 400f),
            Scroll = flowSign * (time + s.Phase * 3.1f) * 24f * px,
            WobbleRate = Rate(diameter * 6.4f), ToneRateA = Rate(diameter * 4.3f), ToneRateB = Rate(diameter * 9.1f),
            PitchRate = Rate(diameter * 13f), TwistRate = Rate(diameter * 40f),
            // Skin relief.
            BumpWave = (!closed && tier.Cols >= 11) ? Max(3.9f * step, 0.25f * diameter) : 0f,
            BumpOff = HashRange(seed + 91, 0f, 200f),
            FoldWave = diameter * 0.30f,
        };

        // Open: the far end is always a dome (a growing tip is meant to be seen); the near one is flush if told to be.
        float capEnd = closed ? 0f : radius * Taper(reveal, false);
        float capStart = closed ? 0f : radius;
        bool domeStart = !closed && !s.FlushStart;
        bool domeEnd = !closed;

        float bodyStart = domeStart ? capStart : 0f;
        float bodyEnd = domeEnd ? visibleLen - capEnd : visibleLen;
        step = Max(step, (bodyEnd - bodyStart) / (MaxRows - 16));

        cut = new Cut
        {
            Path = path, Pn = pn, Closed = closed, DomeStart = domeStart, DomeEnd = domeEnd,
            Total = total, VisibleLen = visibleLen, Reveal = reveal, CapStart = capStart, CapEnd = capEnd,
            BodyStart = bodyStart, BodyEnd = bodyEnd,
            BodyRows = Math.Max(1, (int)Ceiling((bodyEnd - bodyStart) / step)),
        };
        return true;
    }

    int _bodyFirst, _bodyEnd;  // the body's rows in _rows, as BuildRows laid them
    bool _bumps;  // this build has wart heights in _hgt

    int BuildRows(in Cut c, in Look look)
    {
        var path = c.Path;
        int count = 0, seg = 0;
        if (c.DomeStart)
            for (int i = 0; i < PathRows.DomeT.Length - 1; i++)
                AddRow(path, c.Pn, ref seg, ref count, c.CapStart * (1f - PathRows.DomeT[i]), -1f, PathRows.DomeT[i]);

        _bodyFirst = count;
        for (int i = 0; i <= c.BodyRows; i++)
            AddRow(path, c.Pn, ref seg, ref count, c.Arc(i), 0f, 0f);
        _bodyEnd = count;

        if (c.DomeEnd)
            for (int i = PathRows.DomeT.Length - 2; i >= 0; i--)
                AddRow(path, c.Pn, ref seg, ref count, c.VisibleLen - c.CapEnd * (1f - PathRows.DomeT[i]), +1f, PathRows.DomeT[i]);

        for (int i = 0; i < count; i++) FillRow(ref _rows[i], in _rp, in look);
        return count;
    }

    // Coat chart

    const int ChartSectors = 12, ChartMaxRows = Framework.Coat.MaxCells / ChartSectors;
    const float ChartRowStep = 0.6f;  // chart rows are no closer than this many diameters
    readonly Dictionary<int, int> _chartRows = new();

    /// The height of the skin's warts at a body angle (cos, sin) of a row, 0..1: the one function the body's relief and the coat's pooling share.
    static float Wart(in Row r, float oy, float cosPhi, float sinPhi) => Noise.Value(r.WX + cosPhi * r.WRing, oy + sinPhi * r.WRing);

    public bool Charts(CoatMatter matter) => matter is LiquidMatter;

    /// Body-frame chart: U along the tentacle, V the body angle phi from its belly line, seen at theta = phi - Psi, so cells turn with the skin under twist.
    /// Rows are cut by the same Setup and FillRow as the drawn ones, so width, taper, pitch and twist are the renderer's own.
    public void BuildCoatSurfaces(in StrokePrimitive s, in CoatBuildContext ctx, CoatCharts charts)
    {
        if (s.Path.Count < 2) return;
        if (!Setup(in s, ctx.Px, ctx.ShortSide, ctx.Time, 1f, 1e9f, 1e9f, false, 0f, out _, out Look look, out RowParams rp, out Cut cut)) return;
        rp.Margin = 1e9f;  // every chart row is cut whole, on screen or not

        bool closed = cut.Closed;
        float total = cut.Total, diameter = look.Diameter;
        int nb = cut.BodyRows + (closed ? 0 : 1);  // distinct body rows
        if (nb < 2) return;
        float bodyLen = closed ? total : cut.Arc(cut.BodyRows) - cut.Arc(0);

        // rows the stroke's cell share affords, no finer than ChartRowStep; a count within a fifth of last frame's is kept
        int V = ChartSectors, cap = Math.Min(Math.Min(ChartMaxRows, charts.Share / V), nb);
        if (cap < 2) return;
        int want = Math.Clamp((int)Round(bodyLen / (ChartRowStep * diameter)) + (closed ? 0 : 1), 2, cap);
        _chartRows.TryGetValue(s.Seed, out int last);
        int R = last >= 2 && last <= cap && want <= last * 5 / 4 && want >= last * 4 / 5 ? last : want;
        if (_chartRows.Count > 64) _chartRows.Clear();
        _chartRows[s.Seed] = R;
        var ch = charts.Add(1, R, V);
        if (ch == null) return;
        ch.SeamU = closed; ch.SeamV = true; ch.Live = R; ch.SupplyScale = 1f;

        Span<Row> rows = stackalloc Row[R];
        Span<Vector2> axis = stackalloc Vector2[R];
        int seg = 0;
        for (int r = 0; r < R; r++)
        {
            ref Row row = ref rows[r];
            row = default;
            row.Arc = cut.Arc(CoatDraw.PickRow(r, R, nb, closed));
            _path.SampleAt(cut.Path, cut.Pn, ref seg, row.Arc, out row.P, out row.T);
            PathRows.Cap(0f, 0f, out row.CapW, out row.CapAxial);
            FillRow(ref row, in rp, in look);
            ch.UPos[r] = row.Arc; axis[r] = row.T;
        }
        float len = 0f;
        for (int r = 0; r < R; r++)
        {
            float prev = r > 0 ? ch.UPos[r - 1] : (closed ? ch.UPos[R - 1] - total : ch.UPos[0]);
            float next = r < R - 1 ? ch.UPos[r + 1] : (closed ? ch.UPos[0] + total : ch.UPos[r]);
            ch.UStep[r] = 0.5f * (next - prev); len += ch.UStep[r];
        }
        ch.Ds = len / R;
        for (int v = 0; v < V; v++) { ch.VAngle[v] = Tau * v / V; ch.VStep[v] = look.Radius * Tau / V; }

        Span<float> adh = stackalloc float[R * V], round = stackalloc float[V];
        for (int k = 0; k < V; k++) round[k] = k * (4f / V);  // four lattice cells round the tentacle, wrapping
        CoatAdhesion.Field(s.Seed, ch.UPos.AsSpan(0, R), look.Radius * 6f, round, 4, adh);
        float oy = rp.BumpOff * 0.37f + 5.3f, margin = 20f * ctx.Px;
        bool warts = rp.BumpWave > 0f && look.Detail > 0.45f;  // BuildBumps' own condition
        for (int r = 0; r < R; r++)
        {
            ref readonly Row row = ref rows[r];
            Vector2 t = row.T, perp = new(-t.Y, t.X);
            for (int v = 0; v < V; v++)
            {
                float phi = Tau * v / V;  // body angle from the belly line
                (float sn, float cs) = SinCos(phi - row.Psi);
                ref var q = ref ch.S[r * V + v];
                q.Pos = row.P + perp * (sn * row.Radius);
                float across = sn * row.CapW, along = row.CapAxial + (row.LumpTilt - row.SinA * cs) * row.CapW;
                float nx = perp.X * across + t.X * along, ny = perp.Y * across + t.Y * along, nz = row.CosA * cs * row.CapW;
                float inv = 1f / Sqrt(nx * nx + ny * ny + nz * nz + 1e-6f);
                q.Normal = new(nx * inv, ny * inv, nz * inv);
                q.Side = Math.Clamp(cs, -1f, 1f);
                q.Radius = row.Radius;
                q.Z = cs * row.Radius * row.CosA;
                float m = row.Radius + margin;
                q.Visible = ctx.Screen.X <= 0f || (q.Pos.X > -m && q.Pos.X < ctx.Screen.X + m && q.Pos.Y > -m && q.Pos.Y < ctx.Screen.Y + m);
                q.Holds = true;
                q.Anchor = q.Pos;
                // warts turned over (a hollow pools, a crest sheds), and patches of skin that hold liquid badly shed into the ones that hold it well
                float hold = adh[r * V + v];
                float relief = warts && row.BumpAmp > 0f ? 1f - 2f * Wart(in row, oy, Cos(phi), Sin(phi)) : 0f;
                q.Pool = relief + CoatAdhesion.Potential(hold);
                q.Slick = CoatAdhesion.Slick(hold);
            }
        }
        CoatDraw.Slopes(ch, R, V, closed, axis);
    }

    /// The coat's film as a mesh over the body's own columns, thickness lerped from the chart by arc and body angle (false = no chart for it: the generic band is drawn instead).
    bool DrawFilm(ImDrawListPtr dl, int rowCount, Tier tier, in Look look, in MaterialContext ctx, Coat coat, float coatAlpha, bool closed, float total)
    {
        if (coat.Matter is not LiquidMatter lm) return false;
        int R = coat.N, V = coat.V;
        if (R < 2 || V != ChartSectors || R > ChartMaxRows) return false;
        float px = coat.Px;

        Span<float> thick = stackalloc float[R * V];
        float maxTh = 0f;
        for (int c = 0; c < R * V; c++) { thick[c] = coat.M[c] / coat.A[c]; maxTh = Max(maxTh, thick[c]); }
        if (maxTh < 0.25f * lm.Physics.Hold * px) return true;  // nothing to see yet

        int nb = _bodyEnd - _bodyFirst - (closed ? 1 : 0);
        if (nb < 2) return false;
        Span<float> arc = stackalloc float[R];
        for (int r = 0; r < R; r++) arc[r] = _rows[_bodyFirst + CoatDraw.PickRow(r, R, nb, closed)].Arc;

        int nc = tier.Cols;
        int chunkRows = MeshDraw.MaxVerts / nc;
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        float a = Min(1f, coatAlpha * ctx.Alpha);
        Span<float> rowTh = stackalloc float[V], theta = stackalloc float[nc];
        for (int c = 0; c < nc; c++) theta[c] = Atan2(tier.Sin[c], tier.Cos[c]);
        float hold = lm.Physics.Hold * px, maxDraw = Min(lm.Physics.MaxThick, 2.5f * lm.Physics.Hold) * px;
        float dry = lm.Physics.WetMin * hold;  // where the film is thinner than the liquid wets, the skin shows
        float[]? hgt = _bumps ? _hgt : null;
        Vector2[] outP = MeshDraw.P;
        uint[] outC = MeshDraw.C;
#if REALDEBUFFS_PROFILE
        long verts = 0, clear = 0;
#endif

        PushColorOverride(coat.Color);
        try
        {
            // the film varies slowly, so it needs about 0.42 diameters between rows, not the body's finest; the last row is always one
            int fs = Math.Max(1, (int)Round(0.42f / tier.StepFrac));
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
                        int ri = Math.Min(j * fs, rowCount - 1);
                        ref readonly Row row = ref _rows[ri];
                        if (!row.On)
                        {
                            for (int c = 0; c < nc; c++) { outP[vb + c] = row.P; outC[vb + c] = 0u; }
                            continue;
                        }

                        // the chart rows either side of this cross-section, by arc
                        while (rr < R - 2 && row.Arc > arc[rr + 1]) rr++;
                        int ra = rr, rb = rr + 1;
                        float span = arc[rb] - arc[ra], t = span > 1e-4f ? Saturate((row.Arc - arc[ra]) / span) : 0f;
                        if (closed && row.Arc > arc[R - 1])
                        {
                            ra = R - 1; rb = 0;
                            span = arc[0] + total - arc[R - 1];
                            t = span > 1e-4f ? Saturate((row.Arc - arc[R - 1]) / span) : 0f;
                        }
                        for (int k = 0; k < V; k++) rowTh[k] = Lerp(thick[ra * V + k], thick[rb * V + k], t);

                        Vector2 T = row.T, perp = new(-T.Y, T.X);
                        for (int c = 0; c < nc; c++)
                        {
                            float u = tier.Sin[c], co = tier.Cos[c];
                            float hw = hgt != null && row.BumpAmp > 0f ? hgt[ri * nc + c] * row.BumpAmp : 0f;
                            Vector2 pos = row.P + perp * (u * (row.Radius + hw * 0.45f));  // the body's own silhouette, warts included
                            float across = u * row.CapW, along = row.CapAxial + (row.LumpTilt - row.SinA * co) * row.CapW;
                            float nx = perp.X * across + T.X * along, ny = perp.Y * across + T.Y * along, nz = row.CosA * co * row.CapW;
                            float inv = 1f / Sqrt(nx * nx + ny * ny + nz * nz + 1e-6f);
                            nx *= inv; ny *= inv; nz *= inv;

                            float q = (theta[c] + row.Psi) / Tau * V;  // this column's body angle, in sectors
                            q -= Floor(q / V) * V;
                            int k0 = Math.Min((int)q, V - 1), k1 = k0 + 1 == V ? 0 : k0 + 1;
                            float th = Lerp(rowTh[k0], rowTh[k1], q - k0);

                            outP[vb + c] = pos + new Vector2(nx, ny) * (StrokeRope.FilmOffset(th, hold, maxDraw) * 0.9f);
                            uint shade = lm.FilmShade(new(nx, ny, nz), th, px, a, CoatDraw.Soft(u, 0.6f), dry);
#if REALDEBUFFS_PROFILE
                            verts++; if ((shade >> 24) < 13) clear++;
#endif
                            outC[vb + c] = shade;
                        }
                    }
                    MeshDraw.Grid(dl, nc - 1, j1 - j0, uv);
                }

                if (j1 == j0) break;
                j0 = j1;  // the seam row is shared, so chunks join exactly
            }
        }
        finally { PopColorOverride(); }
#if REALDEBUFFS_PROFILE
        FilmVerts += verts; FilmBare += clear;
#endif
        return true;
    }

#if REALDEBUFFS_PROFILE
    internal static long FilmVerts, FilmBare;  // film mesh vertices drawn, and those under 5% opaque (harness builds only)
#endif

    /// Per cross-section state: width, belly line, wetness, highlights, skin coordinates.
    static void FillRow(ref Row r, in RowParams p, in Look look)
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
        float swellPhase = Tau * sArc * p.WobbleRate + p.Ph1;
        float swell = 1f + 0.045f * Sin(swellPhase - time * 1.7f)
                         + 0.030f * look.Agit * Sin(swellPhase - time * 4.7f);

        float lumpField = Smooth((Field(sArc, total, closed, fs * 2.4f, p.OffA, p.Scroll * 0.8f) - 0.46f) / 0.28f);
        float wetField  = 0.55f + 0.45f * Smooth((Field(sArc, total, closed, fs * 3.3f, p.OffB, p.Scroll) - 0.34f) / 0.34f);
        float lump = lumpField * wetField;

        float pitch = (0.40f + 0.22f * look.Agit) * Sin(Tau * sArc * p.PitchRate + p.Ph2 + 0.55f * time);
        r.SinA = Sin(pitch); r.CosA = Cos(pitch);
        float perspective = 1f + 0.18f * r.SinA;

        float tipThin = 1f;
        if (p.Growing > 0.001f && !closed)
        {
            float fromTip = (p.VisibleLen - sArc) / Max(1f, p.Radius * 4.5f);
            tipThin = 1f - 0.5f * p.Growing * (1f - Smooth(fromTip));
        }

        r.Radius = p.Radius * Taper(u, closed) * swell * (1f + 0.16f * lump) * perspective * tipThin * r.CapW;
        r.Wet = wetField;

        // Lump gradient tilts the normal along the tube so a lump shades as a bulge, not a stripe.
        float h = fs * 0.6f;
        float lumpAhead = Smooth((Field(sArc + h, total, closed, fs * 2.4f, p.OffA, p.Scroll * 0.8f) - 0.46f) / 0.28f);
        float lumpBehind = Smooth((Field(sArc - h, total, closed, fs * 2.4f, p.OffA, p.Scroll * 0.8f) - 0.46f) / 0.28f);
        r.LumpTilt = -(lumpAhead - lumpBehind) * 0.85f * wetField;

        // the belly line: turns slowly down the strand, drifts with time, and follows Twist
        float psi = p.Psi0 + Tau * sArc * p.TwistRate + 0.45f * Sin(0.31f * time + p.Ph3);
        if (p.Twist != 0f) psi += p.Twist * (closed ? 1f : Sin(PI * u));
        if (look.Agit > 0.01f) psi += look.Agit * 0.05f * Sin(time * 17f + Tau * sArc * p.ToneRateA * 0.5f);
        r.Psi = psi;

        r.Tone = 1f + 0.07f * Sin(Tau * sArc * p.ToneRateA + p.Ph3) + 0.04f * Sin(Tau * sArc * p.ToneRateB + p.Ph1);
        r.Feather = (1.0f + 2.2f * p.Depth) * p.Px * (1f + 0.4f * Sin(Tau * sArc * p.ToneRateB + p.Ph2));

        // highlight strips: where each light's reflection falls on this stretch of tube
        float breakup = Smooth((Field(sArc, total, closed, fs * 1.7f, p.OffC, p.Scroll * 1.2f) - 0.30f) / 0.30f);
        Glint(KeyHalf, r.T, r.SinA, r.CosA, r.Radius, breakup, wetField, 9f, out r.Glint, out r.GlintX);
        float breakup2 = Smooth((Field(sArc, total, closed, fs * 2.1f, p.OffC + 91f, p.Scroll * 0.9f) - 0.34f) / 0.30f);
        Glint(FillHalf, r.T, r.SinA, r.CosA, r.Radius, breakup2, wetField, 7f, out r.Glint2, out r.GlintX2);

        // skin relief
        if (p.BumpWave > 0f && look.Detail > 0.45f)
        {
            float gate = r.CapW * r.CapW;
            // Transverse folds: irregular rings round the arm, as a contracting limb's skin gathers.
            float fph = Tau * sArc / p.FoldWave + 1.6f * Noise.Value(sArc / (p.FoldWave * 2.7f) + p.BumpOff, 3.1f);
            (float fs0, float fc0) = SinCos(fph);
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
            float a = Tau * sArc / total;
            float rr = total * look.MottleFreq / Tau;
            r.MX = rr * Cos(a) + look.MottleOffset;
            r.MY = rr * Sin(a);
        }
        else
        {
            r.MX = sArc * look.MottleFreq + look.MottleOffset;
            r.MY = 0f;
        }
    }

    /// Where a light's reflection falls on a tube
    static void Glint(Vector3 half, Vector2 t, float sinA, float cosA, float radius, float breakup, float wet,
                              float sharpness, out float strength, out float across)
    {
        float along = half.X * t.X + half.Y * t.Y;
        float hT3 = along * cosA + half.Z * sinA;
        float hPerp = Sqrt(Max(0f, 1f - hT3 * hT3));
        if (hPerp <= 0.05f) { strength = 0f; across = 0f; return; }

        float hAcross = half.X * -t.Y + half.Y * t.X;
        across = Math.Clamp(hAcross / hPerp, -0.95f, 0.95f) * radius;
        strength = Pow(hPerp, sharpness) * (0.25f + 0.75f * breakup) * wet;
    }

    /// A slow 0..1 field along the strand scrolling at scroll px (slime creeping downhill).
    static float Field(float sArc, float total, bool closed, float scale, float offset, float scroll)
    {
        float s = sArc - scroll;
        if (!closed) return Noise.Value(s / scale + offset, offset * 0.37f);
        float a = Tau * s / total;
        float rr = total / (Tau * scale);
        return Noise.Value(rr * Cos(a) + offset, rr * Sin(a) + offset * 0.37f);
    }


    void AddRow(StrandPath path, int pn, ref int seg, ref int count, float s, float endSign, float domeT)
    {
        if (count >= MaxRows) return;
        _path.SampleAt(path, pn, ref seg, s, out Vector2 p, out Vector2 t);

        ref Row r = ref _rows[count];
        r.Arc = s;
        r.P = p; r.T = t;
        PathRows.Cap(endSign, domeT, out r.CapW, out r.CapAxial);
        count++;
    }

    /// Samples the strand at any arc length by search, so a sucker is evaluated exactly where it belongs.
    void SampleAtFree(StrandPath path, int pn, float s, out Vector2 p, out Vector2 t)
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

    static readonly float[] ShadowAcross = { -1f, -0.55f, 0f, 0.55f, 1f };
    static readonly float[] ShadowProfile = { 0f, 0.62f, 1f, 0.62f, 0f };

    void DrawShadow(ImDrawListPtr dl, int rowCount, float depth, float alpha, in MaterialContext ctx)
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
                    float edge = Min(1f, Min(i, rowCount - 1 - i) / 1.5f);

                    for (int c = 0; c < stride; c++)
                    {
                        float w = ShadowAcross[c];
                        MeshDraw.P[v] = row.P + offsetDir * reach + nrm * (w * (hw + soft * 0.5f * Abs(w)));
                        MeshDraw.C[v] = WithAlpha(StrandShading.ShadowTint, a * ShadowProfile[c] * edge);
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

    void DrawBody(ImDrawListPtr dl, int rowCount, Tier tier, in Look look, in MaterialContext ctx)
    {
        int stride = tier.Cols + 2;  // plus the two soft-edge columns
        int chunkRows = MeshDraw.MaxVerts / stride;
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);

        bool bumps = _bumps = BuildBumps(rowCount, tier, in look);
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

    /// The wart field at every vertex of the body: one noise sample each.
    bool BuildBumps(int rowCount, Tier tier, in Look look)
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
            (float sPsi, float cPsi) = SinCos(r.Psi);
            for (int c = 0; c < nc; c++)
            {
                float u = sinT[c], co = cosT[c];
                float cosPhi = co * cPsi - u * sPsi, sinPhi = u * cPsi + co * sPsi;
                hgt[o + c] = Wart(in r, oy, cosPhi, sinPhi);
            }
        }
        return true;
    }

    static void ShadeRow(in Row row, Tier tier, in Look look, int vb, float[]? hgt, int ho, int hp, int hn, float invDs)
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
        (float sPsi, float cPsi) = SinCos(row.Psi);

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
                float dTheta = (cr - cl) * (PI / (nc - 1f)) * rad;
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
            float inv = 1f / Sqrt(nx * nx + ny * ny + nz * nz + 1e-6f);
            nx *= inv; ny *= inv;

            Matcap.SamplePair(Flesh, Coat, nx, ny, out Vector3 col, out Vector3 gloss);

            // Position on the tentacle's own surface, measured from its belly line.
            float cosPhi = co * cPsi - u * sPsi;
            float belly = cosPhi;
            Vector3 tint = Vector3.Lerp(DorsalTint, VentralTint, Smooth((belly + 0.2f) / 1.0f));
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
                    float cavity = Smooth((-hn0 - 0.10f) / 0.70f);
                    float crest = Smooth((hn0 - 0.30f) / 0.70f);
                    col *= 1f + (m - 0.5f) * 0.38f * detail - cavity * CavityDark * detail + crest * CrestLift * detail;
                }
                else
                {
                    float mx = row.MX + cosPhi * 1.15f, my = row.MY + (u * cPsi + co * sPsi) * 1.15f;
                    float freckle = Smooth((Noise.Value(mx * 2.3f + 17f, my * 2.3f + 9f) - 0.70f) / 0.16f);
                    col *= 1f + (m - 0.5f) * 0.38f * detail - freckle * 0.20f * detail;
                }
            }

            // The slime coat: a clear gloss over the flesh, thicker and shinier on the underside.
            float wet = row.Wet * (0.80f + 0.20f * ny);
            col = col * (1f - 0.10f * wet);
            col = col + gloss * wet - col * gloss * wet;  // screen: gloss lightens without clipping

            if (look.Dye > 0f) col = BoostSaturation(col, look.Dye, SoftDye);
            if (look.Agit > 0.01f) col += col * (col * (0.45f * look.Agit));
            if (look.Fog > 0f) col = Vector3.Lerp(col, StrandShading.DepthFog, look.Fog);

            outC[vb + 1 + c] = VertexColor(col, look.Alpha, look.AlphaBits, look.Overridden);
        }

        // Soft edge: the neighbouring column's colour at alpha 0, pushed outward by the feather.
        outP[vb] = outP[vb + 1] - perp * row.Feather;
        outC[vb] = outC[vb + 1] & 0x00FFFFFFu;
        outP[vb + nc + 1] = outP[vb + nc] + perp * row.Feather;
        outC[vb + nc + 1] = outC[vb + nc] & 0x00FFFFFFu;
    }

    // Suckers: real geometry, placed in material coordinates

    // Spacing and size go together: pitch 4% wider than the original 0.85 and cups 2% bigger than the original 0.50
    const float SuckerRadiusFrac = 0.51f;  // cup radius, in tube radii (of the static profile)
    const float SuckerRowAngle   = 0.72f;  // each row sits this far (rad) either side of the belly line
    const float SuckerPitch      = 0.884f;  // spacing along a row, in tube diameters

    // Toward the tip the cups are tiny and a tentacle's point reads as a point whatever is on it
    const float TipThinStart = 0.50f;  // fraction of the arm at which cups start to thin out
    const float TipThinExtra = 1.00f;  // extra tip spacing: 1.0 = twice the spacing there, 0 = off
    const float SuckerFirst      = 1.35f;  // the first cup, in pitches from the root
    const float SuckerLift       = 1.20f;  // how tall a cup stands, in cup radii
    const int   MaxCups          = 96;
    const int   ProfileSteps     = 96;  // resolution of the table that places cups along the taper

    // The cup is a surface of revolution about the skin's normal.
    static readonly float[] CupRhoFull    = { 1.64f, 1.40f, 1.20f, 1.10f, 1.00f, 0.94f, 0.88f, 0.80f, 0.72f, 0.65f, 0.60f, 0.52f, 0.40f, 0.24f, 0.00f };
    static readonly float[] CupZFull      = { 0.00f, 0.00f, 0.02f, 0.14f, 0.34f, 0.54f, 0.70f, 0.80f, 0.78f, 0.66f, 0.48f, 0.30f, 0.19f, 0.15f, 0.20f };
    static readonly float[] CupAlphaFull  = { 0.00f, 0.42f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f, 1.00f };
    // Sky openness per ring: the skirt sits in its own contact shadow, the cavity deep in the cup.
    static readonly float[] CupOpenFull   = { 1.00f, 1.00f, 0.90f, 0.95f, 0.98f, 1.00f, 1.00f, 1.06f, 1.00f, 0.92f, 0.76f, 0.58f, 0.46f, 0.42f, 0.50f };
    const int FirstFleshRing = 2;  // rings before this (in the FULL profile) are the contact shadow

    /// Which of the full profile's rings are built.
    static readonly int[] CupRings = { 0, 1, 2, 4, 5, 7, 8, 10, 11, 14 };
    static readonly int FirstFlesh = Array.IndexOf(CupRings, FirstFleshRing);  // the same boundary, as an index into the rings actually built

    static float[] PickRings(float[] full) { var t = new float[CupRings.Length]; for (int i = 0; i < t.Length; i++) t[i] = full[CupRings[i]]; return t; }
    static readonly float[] CupRho = PickRings(CupRhoFull), CupZ = PickRings(CupZFull), CupAlpha = PickRings(CupAlphaFull), CupOpen = PickRings(CupOpenFull);
    static readonly int FirstCavity = Array.IndexOf(CupRings, 10);  // rings from here on are inside the cup (ring 10 of the full profile on)
    static readonly Vector3 CupTint = new(1.00f, 0.78f, 0.84f);  // inside a cup the flesh is pinker and softer

    /// The profile's outward normal at each ring, as (along rho, along z).
    static readonly (float Rho, float Z)[] CupNormal = ProfileNormals();

    static (float, float)[] NormalsOf(float[] rho, float[] z)
    {
        int n = rho.Length;
        var normals = new (float, float)[n];
        for (int j = 0; j < n; j++)
        {
            int a = Math.Max(0, j - 1), b = Math.Min(n - 1, j + 1);
            float dRho = rho[b] - rho[a], dZ = z[b] - z[a];
            float len = Sqrt(dRho * dRho + dZ * dZ);
            // The profile runs inward over the top of the solid, so its outward normal is (dz, -drho).
            normals[j] = len > 1e-6f ? (dZ / len, -dRho / len) : (0f, 1f);
        }
        normals[n - 1] = (0f, 1f);  // the axis: straight out
        return normals;
    }

    static (float, float)[] ProfileNormals()
    {
        var normals = NormalsOf(CupRhoFull, CupZFull);
        var picked = new (float, float)[CupRings.Length];
        for (int i = 0; i < picked.Length; i++) picked[i] = normals[CupRings[i]];
        return picked;
    }

    // A cup's segment count follows its screen size: a 6 px cup doesn't need 24 segments.
    static readonly float[][] CupSinBy = CircleTables(false), CupCosBy = CircleTables(true);
    static readonly int[] StrideOf = { 9, 11, 13, 15, 17, 19, 21, 23, 25 };  // segments 8, 10, ... 24, plus one
    const int StrideShift = 20;  // quad = (stride code << 20) | first vertex

    static float[][] CircleTables(bool cos)
    {
        var tables = new float[25][];
        for (int segs = 8; segs <= 24; segs += 2)
        {
            tables[segs] = new float[segs + 1];
            for (int i = 0; i <= segs; i++) { float a = Tau * i / segs; tables[segs][i] = cos ? Cos(a) : Sin(a); }
        }
        return tables;
    }

    const int MaxCupVerts = MaxCups * 15 * 25;  // cups share one vertex pool and one painter's pool
    const int MaxCupQuads = MaxCups * 14 * 24;

    readonly Vector2[] _cupPos = new Vector2[MaxCupVerts];
    readonly uint[]    _cupCol = new uint[MaxCupVerts];
    readonly float[]   _cupZ   = new float[MaxCupVerts];  // depth toward the viewer
    readonly float[]   _cupNz  = new float[MaxCupVerts];  // the normal's component toward the viewer
    readonly float[]   _cupSide = new float[MaxCupVerts];  // cos of the angle round the tube: > 0 facing the viewer
    int _cupVerts;
    int[] _remap = new int[MaxCupVerts];

    // Viewer-facing quads, split by whether they lie behind the tube's silhouette plane (drawn before the body).
    readonly float[] _keyBack = new float[MaxCupQuads], _keyFront = new float[MaxCupQuads];
    readonly int[]   _quadBack = new int[MaxCupQuads],  _quadFront = new int[MaxCupQuads];
    int _nBack, _nFront;

    readonly float[] _profileN = new float[ProfileSteps + 1];
    readonly MeshBuilder _cupMeshBack = new(), _cupMeshFront = new();

    /// Builds every sucker.
    int BuildSuckers(StrandPath path, Tier tier, in Look look, float px)
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
            int n = Math.Max(6, (int)Round(scaleN / Taper(0f, true)));
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
            float openUp = 1f + TipThinExtra * Smooth((uMid - TipThinStart) / (1f - TipThinStart));
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

    float UFromN(float n)
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

    /// Builds one cup at fraction u of the way along the arm, in belt rowIndex (0 or 1).
    bool AddSucker(float u, int rowIndex, StrandPath path, int pn, Tier tier, in Look look, float px)
    {
        ref readonly RowParams p = ref _rp;
        float sArc = u * p.Total;

        // Size comes from the static profile, so a cup doesn't pulse with the swell.
        float refR = 0.5f * p.Diameter * Taper(u, p.Closed);
        float grow = p.Closed ? 1f : Smooth((p.VisibleLen - sArc) / (1.7f * refR + 1f));
        float small = Smooth((2f * refR - 6f * px) / (7f * px));  // fades out smoothly as the arm thins to nothing
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
        if (Cos(theta) < -0.72f) return false;

        BuildCup(in row, theta, rc, k, tier, in look);
        return true;
    }

    void BuildCup(in Row row, float theta, float rc, float visible, Tier tier, in Look look)
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
        float wet = Max(0.65f, row.Wet);
        Vector2 shadowShift = StudioLighting.ShadowDirection * (rc * 0.30f);

        // Every vertex of the cup sits at the cup's own angle plus a small offset d = y / skin
        (float sTh, float cTh) = SinCos(theta);
        (float sTp, float cTp) = SinCos(theta + row.Psi);
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
                if (Abs(d) <= 1.2f)
                {
                    // Taylor series to d^9 and d^10: good to ~2e-7 over |d| <= 1.2, which every cup on a tube stays inside.
                    float d2 = d * d;
                    sD = d * (1f + d2 * (-1f / 6f + d2 * (1f / 120f + d2 * (-1f / 5040f + d2 * (1f / 362880f)))));
                    cD = 1f + d2 * (-0.5f + d2 * (1f / 24f + d2 * (-1f / 720f + d2 * (1f / 40320f + d2 * (-1f / 3628800f)))));
                }
                else (sD, cD) = SinCos(d);
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

    /// Sorts the quads of one just-built surface blob (cup or bead) into the painter's pools; zTab is height per ring.
    void PushQuads(int vb, int rings, int segs, int stride, int strideCode, float[] zTab, float lift, float theta, int firstFlesh)
    {
        // Quads that can be seen.
        float near = Smooth((Cos(theta) + 0.72f) / 0.87f);  // 0: cup well behind the arm; 1: at or in front of its edge
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

    /// What one ring of a cup needs that doesn't change vertex to vertex round it, worked out once per ring.
    readonly struct RingShade
    {
        public readonly bool Shadow, Cavity, Fixed;
        public readonly float Alpha, Blend, ToneOpen, Wet, Keep;
        public readonly Vector3 FixedTone;  // full tint once the skirt has blended into the cup's flesh

        public RingShade(int ring, in Row row, float wet, float alphaK)
        {
            Alpha = CupAlpha[ring] * alphaK;
            Shadow = ring < FirstFlesh;
            Cavity = ring >= FirstCavity;
            Blend = Smooth((CupRings[ring] - FirstFleshRing) / 3f);  // by the full profile's ring, so the skirt fades over the same distance
            ToneOpen = row.Tone * CupOpen[ring];
            Wet = wet * (Cavity ? 0.8f : 1f);  // the same clear gloss as the body, so the cups and the skin agree
            Keep = 1f - 0.10f * Wet;
            Fixed = Blend >= 0.999f;
            FixedTone = Fixed ? VentralTint * 1.04f * ToneOpen * (Cavity ? CupTint : Vector3.One) : Vector3.Zero;
        }
    }

    static uint CupColour(in RingShade r, Vector3 n, float belly, in Look look)
    {
        if (r.Shadow)
        {
            // The contact shadow: the same tint as the tentacle's cast shadow, a little darker.
            return WithAlpha(StrandShading.ShadowTint, r.Alpha * 0.9f * Smooth(n.Z / 0.45f));
        }

        // The skirt is a fillet: at its foot it takes the colour of the skin it grows from.
        Matcap.SamplePair(Flesh, Coat, n.X, n.Y, out Vector3 flesh, out Vector3 gloss);
        Vector3 col;
        if (r.Fixed) col = flesh * r.FixedTone;  // most rings: nothing left of the skin's tint to blend
        else
        {
            Vector3 skinTint = Vector3.Lerp(DorsalTint, VentralTint, Smooth(belly + 0.2f));
            Vector3 tint = Vector3.Lerp(skinTint, VentralTint * 1.04f, r.Blend);
            col = flesh * (tint * r.ToneOpen);
            if (r.Cavity) col *= CupTint;  // inside a cup the flesh is pinker and softer
        }

        col = col * r.Keep;
        col = col + gloss * r.Wet - col * gloss * r.Wet;

        if (look.Dye > 0f) col = BoostSaturation(col, look.Dye, SoftDye);
        if (look.Agit > 0.01f) col += col * (col * (0.45f * look.Agit));
        if (look.Fog > 0f) col = Vector3.Lerp(col, StrandShading.DepthFog, look.Fog);

        return Tint(Pack(col), r.Alpha, look.Overridden);
    }

    /// Draws the cup quads behind the tube's silhouette plane (before the body) or in front of it (after), far to near.
    void FlushCups(ImDrawListPtr dl, bool behind, Vector2 uv)
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

    readonly uint[] _sortKeyA = new uint[MaxCupQuads], _sortKeyB = new uint[MaxCupQuads];
    readonly int[]  _sortValB = new int[MaxCupQuads];
    readonly int[]  _sortHist = new int[3 * 2048];

    /// Sorts quads[0..n) by keys[0..n), ascending.
    void SortByKey(float[] keys, int[] quads, int n)
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

    int Use(MeshBuilder mesh, int v)
    {
        int m = _remap[v];
        if (m < 0) _remap[v] = m = mesh.VertexReserved(_cupPos[v], _cupCol[v]);
        return m;
    }

    static float WrapAngle(float a)
    {
        a %= Tau;
        if (a > PI) a -= Tau;
        else if (a < -PI) a += Tau;
        return a;
    }

    // Sparkles: the wet skin's speculars: The mesh cannot carry detail finer than its vertex spacing, and a wet

    const int   SparkleBins  = 360;  // along the arm; each bin holds at most one candidate
    const float SparkleShare = 0.90f;  // how many bins do
    static readonly float[] SparkJit = new float[SparkleBins], SparkSinB = new float[SparkleBins], SparkCosB = new float[SparkleBins],
                                    SparkTx = new float[SparkleBins], SparkTy = new float[SparkleBins], SparkSize = new float[SparkleBins];
    static readonly bool[] SparkOn = new bool[SparkleBins];
    readonly MeshBuilder _sparkMesh = new();

    static StrokeParasite()
    {
        for (int j = 0; j < SparkleBins; j++)
        {
            SparkOn[j]   = Hash01(j * 13 + 5) < SparkleShare;
            SparkJit[j]  = Hash01(j * 13 + 6);
            float beta   = HashRange(j * 13 + 7, -PI, PI);
            SparkSinB[j] = Sin(beta); SparkCosB[j] = Cos(beta);
            SparkTx[j]   = HashRange(j * 13 + 8, -0.55f, 0.55f);
            SparkTy[j]   = HashRange(j * 13 + 9, -0.55f, 0.55f);
            SparkSize[j] = HashRange(j * 13 + 10, 0.8f, 1.35f);
        }
    }

    void DrawSparkles(ImDrawListPtr dl, int rowCount, Vector2 uv, in Look look)
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
                if (!haveTrig) { (sPsi, cPsi) = SinCos(r.Psi); haveTrig = true; }

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
                float a = Smooth(spec * 2.4f) * Smooth(cT / 0.30f) * r.Wet * look.Alpha;
                if (a < 0.04f) continue;

                float along = Math.Clamp((b + SparkJit[j]) / binsPerPx - r.Arc, 0f, r2.Arc - r.Arc);
                Vector2 c = r.P + T * along + new Vector2(ncyl.X, ncyl.Y) * Max(2f, r.Radius);
                float rad = (1.0f + 1.5f * spec) * px * SparkSize[j] * 1.6f;
                float foreshorten = 0.55f + 0.45f * cT;  // a facet on a turned-away patch is a narrower sliver

                // A hexagonal fan: bright at the centre, clear at the rim (six sides read as round at this size).
                uint hot  = Tint(Pack(1.00f, 0.98f, 0.90f), a, look.Overridden);
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

    static readonly float[] SparkHexCos = { Cos(0.5f), Cos(0.5f + Tau / 6f), Cos(0.5f + 2 * Tau / 6f), Cos(0.5f + 3 * Tau / 6f), Cos(0.5f + 4 * Tau / 6f), Cos(0.5f + 5 * Tau / 6f) };
    static readonly float[] SparkHexSin = { Sin(0.5f), Sin(0.5f + Tau / 6f), Sin(0.5f + 2 * Tau / 6f), Sin(0.5f + 3 * Tau / 6f), Sin(0.5f + 4 * Tau / 6f), Sin(0.5f + 5 * Tau / 6f) };

    // Glints

    /// A thin hard highlight where the key light reflects, its own ribbon over the body.
    void DrawGlints(ImDrawListPtr dl, int rowCount, in Look look, in MaterialContext ctx)
    {
        // The key light's reflection is the hard warm one; the fill's is dimmer, cooler and broader.
        DrawGlintStrip(dl, rowCount, in look, in ctx, fill: false, Pack(1.00f, 0.97f, 0.86f));
        DrawGlintStrip(dl, rowCount, in look, in ctx, fill: true,  Pack(0.82f, 0.93f, 1.00f));
    }

    void DrawGlintStrip(ImDrawListPtr dl, int rowCount, in Look look, in MaterialContext ctx, bool fill, uint tint)
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
                    float core = Max(0.75f * px, row.Radius * (fill ? 0.14f : 0.10f));
                    float broad = row.Radius * (fill ? 0.66f : 0.55f);
                    Vector2 mid = row.P + perp * x;

                    // Across: nothing, a faint sheen, the hard core, the sheen again, nothing.
                    float aSheen = Saturate(0.32f * g * power) * look.Alpha;
                    float aCore = Saturate((1.10f * g * g + 0.30f * g) * power) * look.Alpha;
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

    static uint GlintColour(uint rgb, float alpha, in Look look) => Tint(rgb, alpha, look.Overridden);

    // Tip

    static void DrawTip(ImDrawListPtr dl, Vector2 tip, float bar, float k, in Look look, in MaterialContext ctx)
    {
        if (k <= 0.004f) return;
        // A slime bead on a still-reaching tentacle tip: a hard pinprick glint and the faintest sheen.
        uint halo = WithAlpha(Pack(0.82f, 0.95f, 0.55f), 0.11f * k * look.Alpha);
        uint clear = WithAlpha(Pack(0.82f, 0.95f, 0.55f), 0f);
        uint pip = WithAlpha(Pack(1.00f, 1.00f, 0.92f), 0.90f * k * look.Alpha);
        Span<float> rr = stackalloc float[2] { bar * 0.9f, bar * 2.3f };
        Span<uint> cc = stackalloc uint[2] { halo, clear };
        MeshDraw.Radial(dl, tip, MeshDraw.WhiteUv(ctx.Time), pip, 10, rr, cc, 0f, 1f, 0, 0f);
    }

    // What a tentacle sheds, and what happens when it is hit

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    /// Thick slime that runs, pools and hangs in long strings; the matter is whatever the Goop slot holds.
    static readonly StrokeEmission[] Shed =
    {
        StrokeEmission.Coated(PrimitiveRole.Goop, supply: 1.3f, clump: 0.7f),
    };

    public ReadOnlySpan<ImpactEmission> ImpactEmissions => Hit;

    /// A splatter of slime flung off the point of impact.
    static readonly ImpactEmission[] Hit =
    {
        new(Role: PrimitiveRole.Goop, CountMin: 4, CountMax: 7,
            SpeedMin: 150f, SpeedMax: 470f, LifespanMin: 1.3f, LifespanMax: 2.0f,
            SizeMin: 2.8f, SizeMax: 5.8f, ConeRadians: 1.25f, Gravity: new Vector2(0f, 1500f)),
    };
}
