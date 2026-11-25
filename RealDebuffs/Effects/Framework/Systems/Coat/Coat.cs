namespace RealDebuffs.Effects.Framework;

/// A particle material that can be swapped onto a strand as what it is made of.
public interface ICoatMatter { CoatMatter Matter { get; } }

/// Matter on one host chart: an amount per cell (U rows x V sectors; V = 1 for a strand), run by gravity and supply, then the matter's rules.
public sealed class Coat
{
    public const int MaxCells = 576, MaxPendants = 14;  // 48 rows x 12 sectors; a strand may have up to TubeChart.MaxU rows of one sector

    internal struct Pendant { public bool Live; public int Cell, Salt, Cycles; public float M, L, Neck, Idle; }

    internal DebuffKind Owner;
    internal int Seed, ChartId, V = 1;  // ChartId: which part of the host this coat lies on (0 = the whole strand); V: sectors across it
    internal PrimitiveRole Role;
    internal CoatMatter Matter = null!;
    internal CoatOptions Opt;
    internal Vector4? Color;
    internal CoatSet Set = null!;
    internal List<Coat>? Siblings;  // every chart of this stroke tended this frame (chart hosts); null for a tube
    internal readonly CoatChart Own = new();  // the chart a tube is cut into

    // state per cell, row-major (cell = row * V + sector): matter (px²), and the burning matter's heat and char (0..1)
    internal float[] M = [], Heat = [], Char = [];
    internal float[] Patch = [];  // each cell's share of what arrives

    // geometry per cell, from this frame's chart
    internal Vector2[] P = [], T = [], Nm = [], Sd = [], Anch = [];  // centre, direction of rising U, underside normal, direction of rising V, where a drop hangs
    internal float[] R = [], Face = [];  // surface radius; how squarely the exposed edge faces down
    internal float[] A = [], Dc = [];  // a cell's metric: row length x sector share (thickness = M / A); its row length
    internal Vector3[] Ctr = [];  // where the host's own axis runs under each cell (charts that declare contacts): surface point less normal x radius
    internal readonly CoatContact[] Contacts = new CoatContact[CoatChart.MaxContacts];
    internal int ContactCount;
    internal float[] CW = [];  // scratch: the weights of one touch, cell pairs of its two sides
    internal bool[] Vis = [];  // on screen: matter that runs off the edge is gone
    internal bool[] Holds = [];  // the host can hold matter here (a tapered end, say, can't): no supply, no drops
    internal float[] Pool = [];  // per cell, the host's pooling potential (see CoatSurfaceSample)
    internal bool HasPool;  // some cell of this frame's chart has one; if none does, levelling is exactly what it always was
    internal float[] Slick = [];  // per cell, how badly the host holds arriving liquid (see CoatSurfaceSample)
    internal bool HasSlick;  // some cell has some; if none does, supply is exactly what it always was
    internal readonly float[] VStep = new float[12];  // width of each sector, px
    internal Vector2 TipP, TipT, HeadP, HeadT;  // both ends of a strand: either can be the low one
    internal int Salt => unchecked(Seed + ChartId * 40503);  // the stroke's seed, told apart per chart so every link doesn't repeat the same stamp (a strand is chart 0: unchanged)
    internal Vector2 BoundsMin, BoundsMax;  // box around this frame's cells, for a cheap broadphase
    internal float BoundsR;  // largest surface radius in it
    internal int N, Live, SibIndex;  // rows; rows laid this frame; its place among its stroke's charts
    internal float Ds, Px, Time, Born, LastSeen, Bright, SupplyScale = 1f;
    internal bool Closed, Dissolved, SeamV;  // a ring along the strand; sectors that wrap around the host

    internal readonly Pendant[] Pend = new Pendant[MaxPendants];
    internal sbyte[] PendAt = [];

    float[] _v = [], _w = [];
    double[] _dm = [];  // this step's net flux per cell, exact in pairs
    internal float[] Rem = [];  // what rounding a cell to a float left over, carried to the next step so a thick cell never swallows small fluxes

    /// Matter that has arrived and matter that has left (dried, dropped, shed); In - Out is what the coat holds.
    public double In, Out, Seeded, Shed, Bridged;  // Shed: the part of Out that left as drops
    public int Released;  // drops let go so far

    public double Held
    {
        get
        {
            double t = 0;
            for (int i = 0, n = N * V; i < n; i++) t += (double)M[i] + Rem[i];
            for (int k = 0; k < MaxPendants; k++) if (Pend[k].Live) t += Pend[k].M;
            return t + Bridged;
        }
    }

    internal void Reset(CoatMatter matter, float time)
    {
        Matter = matter;
        Array.Clear(M); Array.Clear(Rem); Array.Clear(Heat); Array.Clear(Char); Array.Clear(Pend);
        Array.Fill(PendAt, (sbyte)-1);
        In = Out = Bridged = Shed = Seeded = 0;
        Released = Serial = 0;
        Dissolved = false;
        Born = time;
        matter.Seed(this);
        Seeded = Held;
        Set.Entered += Seeded;
    }

    void Reserve(int cells)
    {
        if (M.Length >= cells) return;
        int n = Math.Max(cells, 32);
        M = new float[n]; Heat = new float[n]; Char = new float[n]; Patch = new float[n]; R = new float[n]; Face = new float[n]; A = new float[n]; Dc = new float[n];
        P = new Vector2[n]; T = new Vector2[n]; Nm = new Vector2[n]; Sd = new Vector2[n]; Anch = new Vector2[n];
        Ctr = new Vector3[n];
        Vis = new bool[n]; Holds = new bool[n]; Pool = new float[n]; Slick = new float[n]; PendAt = new sbyte[n]; Array.Fill(PendAt, (sbyte)-1);
        _v = new float[n]; _w = new float[n]; _dm = new double[n]; Rem = new float[n];
    }

    /// Takes this frame's chart: re-cuts the matter if its rows or sectors changed (nothing lost), then copies the cells' geometry.
    internal void Bind(CoatChart ch, float px)
    {
        Px = px; Closed = ch.SeamU; SeamV = ch.SeamV; ChartId = ch.ChartId;
        int n = ch.UCells, v = ch.VSectors;
        if (N == 0) { Reserve(n * v); N = n; V = v; NewPatch(); }
        else if (n != N || v != V) Regrid(n, v, ch.VStep);

        Ds = ch.Ds; Live = ch.Live; SupplyScale = ch.SupplyScale;
        float tot = 0f;
        for (int s = 0; s < v; s++) { VStep[s] = ch.VStep[s]; tot += VStep[s]; }
        for (int i = 0; i < n; i++)
            for (int s = 0; s < v; s++) { Dc[i * v + s] = ch.UStep[i]; A[i * v + s] = ch.UStep[i] * (VStep[s] / tot); }
        bool pooled = false;
        bool slick = false;
        for (int i = 0; i < Live; i++)
            for (int s = 0; s < v; s++)
            {
                int c = i * v + s;
                ref readonly var q = ref ch.S[c];
                Pool[c] = q.Pool; pooled |= q.Pool != 0f;
                Slick[c] = q.Slick; slick |= q.Slick > 0f;
                P[c] = q.Pos; T[c] = q.Du; Sd[c] = q.Dv; Nm[c] = new(q.Normal.X, q.Normal.Y);
                R[c] = q.Radius; Face[c] = q.GravityDown; Vis[c] = q.Visible; Holds[c] = q.Holds; Anch[c] = q.Anchor;
            }
        HasPool = pooled;
        HasSlick = slick;
        Vector2 lo = new(float.MaxValue), hi = new(float.MinValue); float rr = 0f;
        for (int c = 0, cells = Live * v; c < cells; c++) { lo = Vector2.Min(lo, P[c]); hi = Vector2.Max(hi, P[c]); rr = Max(rr, R[c]); }
        BoundsMin = lo; BoundsMax = hi; BoundsR = rr;
        ContactCount = ch.ContactCount;
        for (int k = 0; k < ContactCount; k++) Contacts[k] = ch.Contacts[k];
        if (v > 1)  // a chart that only receives is touched too: every chart of a provider has its axis laid
            for (int i = 0, cells = Live * v; i < cells; i++)
            {
                ref readonly var q = ref ch.S[i];
                Vector3 nrm = q.Normal; float nl = nrm.Length();
                Ctr[i] = new Vector3(q.Pos.X, q.Pos.Y, q.Z) - (nl > 1e-4f ? nrm / nl : Vector3.UnitZ) * q.Radius;
            }
        TipP = ch.TipP; TipT = ch.TipT; HeadP = ch.HeadP; HeadT = ch.HeadT;
    }

    /// How many cells run along a side, and which cell is the k-th of them.
    internal int EdgeLen(CoatEdge e) => e is CoatEdge.UStart or CoatEdge.UEnd ? V : N;
    internal int EdgeCell(CoatEdge e, int k) => e switch { CoatEdge.UStart => k, CoatEdge.UEnd => (N - 1) * V + k, CoatEdge.VStart => k * V, _ => k * V + V - 1 };

    /// The unit screen direction that leads out of the chart across a side.
    internal Vector2 Outward(CoatEdge e, int cell) => e switch { CoatEdge.UStart => -T[cell], CoatEdge.UEnd => T[cell], CoatEdge.VStart => -Sd[cell], _ => Sd[cell] };

    /// The cell a chart-space anchor (u, v in 0..1 across the chart) falls in now; false if the rows there are not laid yet.
    internal int CellAt(float u, float v, out bool laid)
    {
        int row = Math.Clamp((int)(u * N), 0, N - 1), sec = Math.Clamp((int)(v * V), 0, V - 1);
        laid = row < Live;
        return row * V + sec;
    }

    /// The chart-space anchor of a cell: its row and sector as fractions of the chart, stable under regridding.
    internal void AnchorFor(int cell, out float u, out float v) { u = (cell / V + 0.5f) / N; v = (cell % V + 0.5f) / V; }

    /// The edge point a drop hangs from and how squarely it faces down: a strand's underside or downward end, a chart's host-given downhill edge.
    internal float AnchorOf(int i, out Vector2 anchor)
    {
        if (V > 1) { anchor = Anch[i]; return Face[i]; }
        if (!Closed)
        {
            if (i == Live - 1 && TipT.Y > Face[i]) { anchor = TipP; return TipT.Y; }  // a strand whose end points down hangs from it, whichever end that is
            if (i == 0 && -HeadT.Y > Face[i]) { anchor = HeadP; return -HeadT.Y; }
        }
        anchor = P[i] + Nm[i] * (R[i] * 0.78f);
        return Face[i];
    }

    public void Draw(ImDrawListPtr dl, float alpha, in MaterialContext ctx)
    {
        Matter.Draw(dl, this, alpha, in ctx);
        Set.DrawBridges(this, dl, alpha, in ctx);
    }

    /// Draws the coat as a layer of its host (the host's own colour override is not the coat's).
    public void DrawLayer(ImDrawListPtr dl, float alpha, in MaterialContext ctx)
    {
        PushColorOverride(Color);
        try { Draw(dl, alpha, in ctx); }
        finally { PopColorOverride(); }
    }

    /// Mean film thickness (px) over the stretch of a strand from arcFrom to arcTo (a one-sector coat).
    public float MeanThickness(float arcFrom, float arcTo)
    {
        if (N == 0 || Live == 0 || Ds <= 0f) return 0f;
        int i0 = Math.Clamp((int)(arcFrom / Ds), 0, Live - 1), i1 = Math.Clamp((int)(arcTo / Ds), i0, Live - 1);
        float sum = 0f;
        for (int i = i0; i <= i1; i++) sum += M[i];
        return sum / ((i1 - i0 + 1) * Ds);
    }

    internal int Serial;  // salts every pendant this coat grows

    void NewPatch()
    {
        for (int i = 0, n = N * V; i < n; i++)
        {
            float h = Hash01(Salt * 31 + i * 7 + 1);
            Patch[i] = (1f - Opt.Clump) + Opt.Clump * 2.4f * h * h;
        }
    }

    /// Re-cuts the chart to n rows x v sectors, sharing matter by overlap so totals hold (heat and char average, drops keep their place).
    void Regrid(int n, int v, float[] step)
    {
        int old = N, oldV = V;
        Span<float> m = stackalloc float[MaxCells], h = stackalloc float[MaxCells], c = stackalloc float[MaxCells];
        Span<float> m2 = stackalloc float[MaxCells];
        m.Clear(); h.Clear(); c.Clear(); m2.Clear();

        // along U: every sector's column is shared out by row overlap
        for (int s = 0; s < oldV; s++)
            for (int j = 0; j < n; j++)
            {
                float a = (float)j / n, b = (float)(j + 1) / n;
                for (int i = (int)(a * old); i < old && (float)i / old < b; i++)
                {
                    float lo = Max(a, (float)i / old), hi = Min(b, (float)(i + 1) / old), ov = hi - lo;
                    if (ov <= 0f) continue;
                    m[j * oldV + s] += M[i * oldV + s] * ov * old;  // matter is per cell: a cell's share is the overlap over its width
                    h[j * oldV + s] += Heat[i * oldV + s] * ov * n; c[j * oldV + s] += Char[i * oldV + s] * ov * n;
                }
            }

        // across V: each new sector takes the overlap of the old sectors' shares of the perimeter (they sum to 1, so a seam loses nothing)
        if (v != oldV)
        {
            Span<float> oldEdge = stackalloc float[13], newEdge = stackalloc float[13];
            float tot = 0f; for (int s = 0; s < oldV; s++) tot += VStep[s];
            oldEdge[0] = 0f; for (int s = 0; s < oldV; s++) oldEdge[s + 1] = oldEdge[s] + VStep[s] / tot;
            float tot2 = 0f; for (int s = 0; s < v; s++) tot2 += step[s];
            newEdge[0] = 0f; for (int s = 0; s < v; s++) newEdge[s + 1] = newEdge[s] + step[s] / tot2;
            for (int j = 0; j < n; j++)
                for (int t = 0; t < v; t++)
                    for (int s = 0; s < oldV; s++)
                    {
                        float ov = Min(newEdge[t + 1], oldEdge[s + 1]) - Max(newEdge[t], oldEdge[s]);
                        if (ov <= 0f) continue;
                        float w = oldEdge[s + 1] - oldEdge[s];
                        m2[j * v + t] += m[j * oldV + s] * (w > 0f ? ov / w : 1f / oldV);
                    }
            m2.Slice(0, n * v).CopyTo(m);
        }

        // matter is conserved exactly: renormalise away rounding at the ends
        double before = 0, after = 0;
        for (int i = 0; i < old * oldV; i++) before += (double)M[i] + Rem[i];
        for (int j = 0; j < n * v; j++) after += m[j];
        float fix = after > 0 ? (float)(before / after) : 1f;

        Span<int> pc = stackalloc int[MaxPendants];
        for (int k = 0; k < MaxPendants; k++) pc[k] = Pend[k].Cell;

        N = n; V = v;
        Reserve(n * v);
        Array.Clear(M); Array.Clear(Rem); Array.Clear(Heat); Array.Clear(Char);
        for (int j = 0; j < n * v; j++) { M[j] = m[j] * fix; if (v == 1) { Heat[j] = Min(1f, h[j]); Char[j] = Min(1f, c[j]); } }

        Array.Fill(PendAt, (sbyte)-1);
        for (int k = 0; k < MaxPendants; k++)
        {
            if (!Pend[k].Live) continue;
            int row = Math.Min(n - 1, (int)((pc[k] / oldV + 0.5f) * n / old)), sec = Math.Min(v - 1, (int)((pc[k] % oldV + 0.5f) * v / oldV));
            int cell = row * v + sec;
            if (PendAt[cell] >= 0) { Pend[k].Cell = cell; Retire(k); continue; }  // two merged into one cell: the later gives its matter back
            Pend[k].Cell = cell; PendAt[cell] = (sbyte)k;
        }
        NewPatch();
    }

    internal void Retire(int k)
    {
        ref var p = ref Pend[k];
        Credit(p.Cell, p.M);
        if (PendAt[p.Cell] == k) PendAt[p.Cell] = -1;  // not another pendant's slot after a regrid merged two
        p.Live = false;
    }

    internal void Advance(float time, float dt, float alpha)
    {
        Time = time; Bright = alpha;
        dt = Min(dt, 0.05f);
        if (dt <= 0f) return;
        int steps = Math.Clamp((int)Ceiling(dt * 60f), 1, 3);
        float h = dt / steps;
        for (int k = 0; k < steps; k++) Step(h);
    }

    /// Exact mass in or out of a cell: the float keeps its rounding remainder, so nothing is absorbed or invented.
    internal void Credit(int cell, double amount)
    {
        double want = (double)M[cell] + Rem[cell] + amount;
        if (want <= 0.0) { M[cell] = 0f; Rem[cell] = 0f; }
        else { float m = (float)want; M[cell] = m; Rem[cell] = (float)(want - m); }
    }

    internal void Debit(int cell, double amount) => Credit(cell, -amount);

    /// Folds the step's net flux into the cells, carrying each cell's float rounding remainder forward so moved mass is neither lost nor made.
    void Apply(int cells)
    {
        for (int i = 0; i < cells; i++)
        {
            double want = (double)M[i] + Rem[i] + _dm[i];
            if (want <= 0.0) { M[i] = 0f; Rem[i] = 0f; }
            else { float m = (float)want; M[i] = m; Rem[i] = (float)(want - m); }
            _dm[i] = 0.0;
        }
    }

    // a cell less its neighbour, scaled to thickness: mass difference for equal cells (old strand law, bit for bit), else what moving would level
    internal static float Diff(float mi, float mj, float ai, float aj) => ai == aj ? mi - mj : (mi - mj * (ai / aj)) * (2f * aj / (ai + aj));

    void Step(float h)
    {
        var mt = Matter;
        float px = Px, hold = mt.Hold * px, capTh = mt.MaxThick * px;
        int V = this.V, cells = Live * V, rows = Live;
        bool wrapU = Closed && Live == N;

        // arrival: scaled by what the cell takes (patchiness) and, for matter that settles, by how squarely it faces up
        float gate = mt.Supply * Opt.Supply * px * Ds * 0.01f * h * Bright;
        if (gate > 0f)
            for (int i = 0; i < cells; i++)
            {
                float perCell = mt.Supply * Opt.Supply * px * A[i] * 0.01f * h * Bright * SupplyScale;
                float room = 1f - Saturate((M[i] / A[i] - capTh * 0.7f) / (capTh * 0.3f));
                float face = mt.Settles ? Saturate(Face[i] * 1.5f) : 1f;
                float add = Vis[i] && Holds[i] ? perCell * Patch[i] * room * face * (HasSlick ? 1f - Slick[i] : 1f) : 0f, was = M[i];
                M[i] += add;
                double got = (double)M[i] - was;  // book what the float cell took, not what was offered: the ledger then never drifts
                In += got; Set.Entered += got;
            }

        // running: thick film runs down the chart, thin film clings; mass moves cell to cell and none is made or lost
        if (mt.Run > 0f)
        {
            for (int i = 0; i < cells; i++)
            {
                float th = M[i] / A[i];
                float run = Smooth((th - hold) / (hold * 1.2f + 0.3f * px));
                float rf = mt.Run * px * run * Math.Clamp(th / (2f * hold), 0.4f, 2.2f);
                // power-law film runs as slope^(1/n): n = 1 is linear; a thinning liquid creeps on gentle slopes and runs on steep ones (same speed at 0.7)
                float sl = T[i].Y, law = sl * Pow(Max(Abs(sl), 1e-4f) / 0.7f, 1f / mt.Index - 1f);
                float lim = 0.45f * Dc[i] / h, v = rf * law;
                _v[i] = Math.Clamp(v, -lim, lim);
                if (V > 1)
                {
                    float sw = Sd[i].Y, lw = sw * Pow(Max(Abs(sw), 1e-4f) / 0.7f, 1f / mt.Index - 1f), limW = 0.45f * VStep[i % V] / h;
                    _w[i] = Math.Clamp(rf * lw, -limW, limW);
                }
            }
            int lastRow = wrapU ? rows : rows - 1;
            for (int s = 0; s < V; s++)
                for (int i = 0; i < lastRow; i++)
                {
                    int j = i + 1 == rows ? 0 : i + 1, ca = i * V + s, cb = j * V + s;
                    float vf = 0.5f * (_v[ca] + _v[cb]);
                    float amt = (vf > 0f ? M[ca] : M[cb]) * vf / (0.5f * (Dc[ca] + Dc[cb])) * h;
                    amt = amt > 0f ? Min(amt, M[ca] * 0.5f) : Max(amt, -M[cb] * 0.5f);
                    _dm[ca] -= amt; _dm[cb] += amt;
                }
            if (V > 1)
                for (int i = 0; i < rows; i++)
                    for (int s = 0, ls = SeamV ? V : V - 1; s < ls; s++)
                    {
                        int s2 = s + 1 == V ? 0 : s + 1, ca = i * V + s, cb = i * V + s2;
                        float vf = 0.5f * (_w[ca] + _w[cb]);
                        float amt = (vf > 0f ? M[ca] : M[cb]) * vf / (0.5f * (VStep[s] + VStep[s2])) * h;
                        amt = amt > 0f ? Min(amt, M[ca] * 0.5f) : Max(amt, -M[cb] * 0.5f);
                        _dm[ca] -= amt; _dm[cb] += amt;
                    }
            Apply(cells);
        }

        // levelling: a lump spreads over the chart, thick faster than thin; flux between neighbours, so nothing is made or lost
        if (mt.Level > 0f && cells > 1)
        {
            // pooling: the film levels toward a thickness offset by the host's potential, so a groove holds more than a ridge. Still a flux between neighbours, so the books balance.
            // A pool can ask for more than a cell has: with one, no cell gives up more than a quarter of what it holds in a step.
            float pk = HasPool ? mt.PoolDepth * hold : 0f;
            int lastRow = wrapU ? rows : rows - 1;
            for (int s = 0; s < V; s++)
                for (int i = 0; i < lastRow; i++)
                {
                    int j = i + 1 == rows ? 0 : i + 1, ca = i * V + s, cb = j * V + s;
                    float w = Math.Clamp(0.5f * (M[ca] + M[cb]) / (0.5f * (A[ca] + A[cb])) / Max(hold, 1e-3f), 0.3f, 3f);
                    float diff = Diff(M[ca], M[cb], A[ca], A[cb]);
                    if (pk > 0f) diff -= pk * 0.5f * (A[ca] + A[cb]) * (Pool[ca] - Pool[cb]);
                    float amt = Min(0.25f, mt.Level * w * w * h) * diff;
                    if (pk > 0f) amt = amt > 0f ? Min(amt, 0.25f * M[ca]) : Max(amt, -0.25f * M[cb]);
                    _dm[ca] -= amt; _dm[cb] += amt;
                }
            if (V > 1)
                for (int i = 0; i < rows; i++)
                    for (int s = 0, ls = SeamV ? V : V - 1; s < ls; s++)
                    {
                        int ca = i * V + s, cb = i * V + (s + 1 == V ? 0 : s + 1);
                        float w = Math.Clamp(0.5f * (M[ca] + M[cb]) / (0.5f * (A[ca] + A[cb])) / Max(hold, 1e-3f), 0.3f, 3f);
                        float diff = Diff(M[ca], M[cb], A[ca], A[cb]);
                        if (pk > 0f) diff -= pk * 0.5f * (A[ca] + A[cb]) * (Pool[ca] - Pool[cb]);
                        float amt = Min(0.25f, mt.Level * w * w * h) * diff;
                        if (pk > 0f) amt = amt > 0f ? Min(amt, 0.25f * M[ca]) : Max(amt, -0.25f * M[cb]);
                        _dm[ca] -= amt; _dm[cb] += amt;
                    }
            Apply(cells);
        }

        for (int i = 0; i < cells; i++)
        {
            float d = M[i] * (Vis[i] ? mt.Dry : 4f) * h, was = M[i];
            M[i] -= d;
            if (M[i] > 0f && M[i] < 1e-20f) M[i] = 0f;  // a film this thin is nothing, and denormals make the tick slow; the ledger below books it
            double lost = (double)was - M[i];
            Out += lost; Set.Sunk[(int)(Vis[i] ? CoatSet.Sink.Dried : CoatSet.Sink.RanOff)] += lost;
        }

        mt.Tick(this, h);
    }
}

/// Every coat in the scene, keyed by owner, strand and role, plus the things coats let go of; one per StrokeShed.
internal sealed class CoatSet
{
    readonly record struct Key(DebuffKind Owner, int Seed, PrimitiveRole Role, int Chart = 0);  // Chart 0 is a strand coated as a tube

    /// Where matter deliberately leaves the system; everything else is held or in flight.
    internal enum Sink { Dried, RanOff, Burned, Lumped, HostGone }

    internal readonly double[] Sunk = new double[5];
    internal double Entered;  // everything that ever arrived: seeded cells and supply
    internal int BeadsDrawn;  // beads emitted since the last Emit began (diagnostic)

    readonly Dictionary<Key, Coat> _coats = new();
    readonly List<Key> _stale = new();
    readonly Stack<Coat> _spare = new();
    float _lastPrune;

    internal readonly DropPool Drops = new(192);
    internal readonly ParticleSystem Lumps = new(160);
    internal readonly ParticleSystem Cinders = new(300, 0x0C1, new ParticlePhysics(new(0f, -26f), Drag: 0.30f, Wander: 240f, WanderHz: 0.85f));
    internal readonly ParticleSystem Smoke = new(200, 0x0C2, new ParticlePhysics(Drag: 0.06f, Wander: 26f, WanderHz: 0.35f));

    public int Count => _coats.Count;

    /// The whole system's books: what came in, what the coats hold, what is falling, what left. entered = held + flying + sunk.
    public (double entered, double held, double flying, double sunk) Audit()
    {
        double held = 0, flying = 0, sunk = Drops.Left;
        foreach (var c in _coats.Values) held += c.Held;
        for (int i = 0; i < Drops.Count; i++) flying += Drops.Items[i].Amount;
        for (int i = 0; i < Sunk.Length; i++) sunk += Sunk[i];
        return (Entered, held, flying, sunk);
    }
    public IEnumerable<Coat> All => _coats.Values;

    /// Once a frame, first: moves what the coats let go of and forgets coats whose strand is gone.
    public void Advance(float time, float dt, Vector2 screen)
    {
        dt = Min(dt, 0.05f);
        _dt = Max(dt, 1e-4f); _now = time;
        _groupsLast = _groupsNow; _groupsNow = 0;
        _charts.CellsLeft = CellBudget; _charts.Share = CellBudget / Math.Max(1, _groupsLast); ChartsSkipped = 0;
        float floor = screen.Y > 0f ? screen.Y + 80f : float.MaxValue;
        var none = new DropPool.NoHook();
        Drops.Advance(time, dt, floor, ref none);
        Lumps.Step(time, dt); Cinders.Step(time, dt); Smoke.Step(time, dt);

        // A host that has gone lets what hangs from it fall at once, rather than freezing it in the air.
        foreach (var c in _coats.Values) if (!c.Dissolved && time - c.LastSeen > 0.1f) Dissolve(c, time);

        if (time - _lastPrune <= 0.5f) return;
        _lastPrune = time;
        _stale.Clear();
        foreach (var kv in _groups) if (time - kv.Value.Time > 0.6f) _stale.Add(kv.Key);
        for (int i = 0; i < _stale.Count; i++) _groups.Remove(_stale[i]);
        _stale.Clear();
        foreach (var kv in _coats) if (time - kv.Value.LastSeen > (kv.Value.ChartId != 0 ? ChartGrace : 0.6f)) _stale.Add(kv.Key);
        for (int i = 0; i < _stale.Count; i++)
        {
            var c = _coats[_stale[i]];
            Sunk[(int)Sink.HostGone] += c.Held;  // the film that was on a strand that is no longer there
            _spare.Push(c); _coats.Remove(_stale[i]);
        }
    }

    void Dissolve(Coat c, float time)
    {
        c.Dissolved = true; c.Time = time;
        DetachBridges(c);
        if (c.Matter is not LiquidMatter lm) return;
        for (int k = 0; k < Coat.MaxPendants; k++)
            if (c.Pend[k].Live && !lm.Release(c, k)) c.Retire(k);  // no room to fall: it stays in the film
    }

    internal const float ChartGrace = 8f;  // a hidden or off-frame chart keeps its film frozen this long (a link rolls back in with it); whole strands have no such part
    internal const int CellBudget = 4096;  // cells a frame may spend on charts, all hosts together
    readonly CoatCharts _charts = new();
    sealed class Group { public readonly List<Coat> Parts = new(); public readonly Dictionary<int, Coat> ById = new(); public float Time = -1f; }
    readonly Dictionary<Key, Group> _groups = new();
    internal int ChartsSkipped;  // chart requests the budget refused this frame (diagnostic)
    int _groupsNow, _groupsLast;  // strokes that used charts this frame and last: the budget is shared among them

    /// Grows one emission's coat on a stroke (no-op if its material can't coat): per chart for chart hosts, else as a tube along the path.
    public void Tend(EffectScene scene, in StrokePrimitive s, in StrokeEmission e, string? forcedMaterial,
                     IStrokeMaterial? strokeMaterial, float time, float dt)
    {
        if (e.Coat is not { } opt) return;
        IParticleMaterial? pm = forcedMaterial is not null ? MaterialRegistry.TryGetParticle(forcedMaterial) : scene.Materials.Particle(s.Owner, e.Role);
        if (pm is not ICoatMatter cm) return;

        float px = PixelScale(scene.ScreenSize);
        float shortSide = scene.ScreenSize.X > 0f ? Min(scene.ScreenSize.X, scene.ScreenSize.Y) : 1080f;
        if (s.Path.Length < 24f * px) return;
        var ctx = new CoatBuildContext(scene.ScreenSize, shortSide, px, time);

        if (strokeMaterial is ICoatSurfaceProvider prov && prov.Charts(cm.Matter)) TendCharts(prov, in s, in ctx, opt, cm.Matter, e.Role, time, dt);
        else TendTube(in s, in ctx, opt, cm.Matter, e.Role, strokeMaterial, time, dt);
    }

    void TendTube(in StrokePrimitive s, in CoatBuildContext ctx, CoatOptions opt, CoatMatter matter, PrimitiveRole role, IStrokeMaterial? strokeMaterial, float time, float dt)
    {
        var key = new Key(s.Owner, s.Seed, role);
        bool fresh = !_coats.TryGetValue(key, out var c);
        if (fresh)
        {
            c = _spare.Count > 0 ? _spare.Pop() : new Coat();
            c.N = 0; c.V = 1; c.Own.UCells = 0; c.Siblings = null; c.Owner = s.Owner; c.Seed = s.Seed; c.Role = role; c.Set = this;
            _coats[key] = c;
        }
        else if (c!.LastSeen == time) return;  // a stroke seen twice in one frame is tended once

        c!.Opt = opt;
        c.Color = s.ColorOverride;
        c.LastSeen = time;
        TubeChart.Build(c.Own, in s, strokeMaterial, in ctx);
        c.Bind(c.Own, ctx.Px);
        Feed(c, fresh, matter, time, dt, s.Brightness);
    }

    void TendCharts(ICoatSurfaceProvider prov, in StrokePrimitive s, in CoatBuildContext ctx, CoatOptions opt, CoatMatter matter, PrimitiveRole role, float time, float dt)
    {
        var gk = new Key(s.Owner, s.Seed, role);
        if (!_groups.TryGetValue(gk, out var g)) _groups[gk] = g = new();
        else if (g.Time == time) return;  // a stroke seen twice in one frame is tended once
        g.Time = time;
        _groupsNow++;
        var parts = g.Parts;
        parts.Clear(); g.ById.Clear();

        _charts.Clear();
        int skipped = _charts.Skipped;
        prov.BuildCoatSurfaces(in s, in ctx, _charts);
        ChartsSkipped += _charts.Skipped - skipped;

        for (int k = 0; k < _charts.Count; k++)
        {
            var ch = _charts[k];
            var key = new Key(s.Owner, s.Seed, role, ch.ChartId);
            bool fresh = !_coats.TryGetValue(key, out var c);
            if (fresh)
            {
                c = _spare.Count > 0 ? _spare.Pop() : new Coat();
                c.N = 0; c.V = 1; c.Own.UCells = 0; c.Owner = s.Owner; c.Seed = s.Seed; c.Role = role; c.Set = this;
                _coats[key] = c;
            }
            c!.Opt = opt;
            c.Color = s.ColorOverride;
            c.LastSeen = time;
            c.Bind(ch, ctx.Px);
            c.Siblings = parts; c.SibIndex = parts.Count; parts.Add(c); g.ById[ch.ChartId] = c;
            Feed(c, fresh, matter, time, dt, s.Brightness);
        }
        Transfer(g, dt);
    }

    internal long XCand, XAct, XTicks;  // contact search: cell pairs looked at, pairs in contact, stopwatch ticks (harness builds only)
    internal double XMass;  // matter that has crossed from one chart to another, ever (diagnostic: the books see only each coat's In and Out)

    /// Cross-chart transfer: the host proposes sides, a cell pair passes only if the host's axes are within their radii in 3D; exact in both books.
    const int MaxEdge = 64;

    void Transfer(Group g, float dt)
    {
#if REALDEBUFFS_PROFILE
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
        float h = Min(dt, 0.05f);
        Span<float> sa = stackalloc float[MaxEdge], sb = stackalloc float[MaxEdge];  // how much each cell of the two sides touches in all
        if (h > 0f)
            foreach (var a in g.Parts)
                for (int k = 0; k < a.ContactCount; k++)
                {
                    ref readonly var ct = ref a.Contacts[k];
                    if (!g.ById.TryGetValue(ct.OtherId, out var b) || b == a || a.Matter != b.Matter) continue;  // that part is not laid this frame: nothing to touch
                    int na = a.EdgeLen(ct.Edge), nb = b.EdgeLen(ct.OtherEdge);
                    if (na > MaxEdge || nb > MaxEdge) continue;  // a chart side longer than a chart may be
                    if (ct.Seam)
                    {
                        if (na != nb) continue;
                        for (int i = 0; i < na; i++)
                        {
                            int ca = a.EdgeCell(ct.Edge, i), cb = b.EdgeCell(ct.OtherEdge, i);
                            Exchange(a, ca, b, cb, ct.Edge, true, h, Weight(a, ca, b, cb));
                        }
                        continue;
                    }
                    var wt = a.CW;  // this frame's weights from the geometry: cell pairs, one row per cell of this side
                    if (wt.Length < na * nb) wt = a.CW = new float[na * nb];
                    for (int i = 0; i < na; i++)
                        for (int j = 0; j < nb; j++) wt[i * nb + j] = Weight(a, a.EdgeCell(ct.Edge, i), b, b.EdgeCell(ct.OtherEdge, j));
                    // a cell touching several partners shares one contact among them, so a crowd of partners moves no more than one would
                    sa.Slice(0, na).Clear(); sb.Slice(0, nb).Clear();
                    for (int i = 0; i < na; i++)
                        for (int j = 0; j < nb; j++) { float w = wt[i * nb + j]; sa[i] += w; sb[j] += w; }
                    for (int i = 0; i < na; i++)
                    {
                        if (sa[i] <= 0f) continue;
                        for (int j = 0; j < nb; j++)
                            if (wt[i * nb + j] > 0f)
                                Exchange(a, a.EdgeCell(ct.Edge, i), b, b.EdgeCell(ct.OtherEdge, j), ct.Edge, false, h, wt[i * nb + j] / Max(1f, Max(sa[i], sb[j])));
                    }
                }
#if REALDEBUFFS_PROFILE
        XTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
#endif
    }

    /// 0..1 contact between two cells: the host's axes within their radii (plus a little for cell spacing), fading out over half a radius.
    float Weight(Coat a, int ca, Coat b, int cb)
    {
        XCand++;
        float ra = a.R[ca], rb = b.R[cb];
        float limit = ra + rb + 0.25f * (a.Dc[ca] + b.Dc[cb]), band = 0.5f * (ra + rb);
        Vector3 dd = a.Ctr[ca] - b.Ctr[cb];
        float d2 = dd.LengthSquared();
        if (d2 >= limit * limit) return 0f;
        return Smooth((limit - Sqrt(d2)) / Max(band, 1e-3f));
    }

    void Exchange(Coat a, int ca, Coat b, int cb, CoatEdge edge, bool seam, float h, float w)
    {
        if (w <= 0f) return;
        XAct++;
        float d = Vector3.Distance(a.Ctr[ca], b.Ctr[cb]);

        // which way is down across it: out of a's side for one surface cut in two; from a's axis to b's for two surfaces touching
        float sl = seam ? a.Outward(edge, ca).Y : (d > 1e-3f ? (b.Ctr[cb].Y - a.Ctr[ca].Y) / d : 0f);
        var mt = a.Matter;
        float px = a.Px, hold = mt.Hold * px;
        bool fromA = sl >= 0f;
        Coat src = fromA ? a : b, dst = fromA ? b : a;
        int cs = fromA ? ca : cb, cd = fromA ? cb : ca;

        float th = src.M[cs] / src.A[cs];
        float run = Smooth((th - hold) / (hold * 1.2f + 0.3f * px));
        float len = 0.5f * (src.Dc[cs] + dst.Dc[cd]), flux = 0f;
        if (run > 0f)  // below its yield thickness a film does not run (and the power law is the costly part)
        {
            float rf = mt.Run * px * run * Math.Clamp(th / (2f * hold), 0.4f, 2.2f);
            float s = Abs(sl), law = s * Pow(Max(s, 1e-4f) / 0.7f, 1f / mt.Index - 1f);
            float v = Math.Clamp(rf * law, 0f, 0.45f * len / h);
            flux = src.M[cs] * v / len * h * w;  // src to dst
        }

        // levelling: thick spreads into thin across the touch as it does inside a chart
        float wk = Math.Clamp(0.5f * (src.M[cs] + dst.M[cd]) / (0.5f * (src.A[cs] + dst.A[cd])) / Max(hold, 1e-3f), 0.3f, 3f);
        flux += Min(0.25f, mt.Level * wk * wk * h) * Coat.Diff(src.M[cs], dst.M[cd], src.A[cs], dst.A[cd]) * 0.5f * w;
        if (Abs(flux) < 1e-4f) return;  // films at rest and level: nothing worth moving

        if (flux < 0f) { (src, dst) = (dst, src); (cs, cd) = (cd, cs); flux = -flux; }
        flux = Min(flux, 0.2f * src.M[cs]);  // a cell can touch several; each takes at most a fifth of what is left, so none can be emptied past zero
        if (flux <= 0f) return;
        src.Debit(cs, flux); dst.Credit(cd, flux);
        src.Out += flux; dst.In += flux; XMass += flux;
    }

    void Feed(Coat c, bool fresh, CoatMatter matter, float time, float dt, float brightness)
    {
        c.Dissolved = false;
        if (!fresh && c.Matter != matter) DetachBridges(c);  // its strings were made of the old matter
        if (fresh || c.Matter != matter) { if (!fresh) Sunk[(int)Sink.HostGone] += c.Held; c.Reset(matter, time); }
        c.Advance(time, dt, brightness);
    }


    // ---- bridges: a string between two hosts

    internal const int MaxBridges = 12;

    internal struct Bridge
    {
        public bool Live;
        public Coat A = null!, B = null!;
        public float UA, VA, UB, VB, M, L0, Sag, Thick;  // where it is anchored on each host's chart (u, v in 0..1: stable when the chart is regridded), its matter, length when it stuck
        public Vector2 Pa, Pb;
        public int Salt;
        public Bridge() { }
    }

    internal readonly Bridge[] Br = new Bridge[MaxBridges];
    float _dt = 1f / 60f, _now;

    static Vector2 Contact(Coat b, int j, Vector2 toward)
    {
        Vector2 d = toward - b.P[j];
        return b.P[j] + (d.LengthSquared() > 1e-4f ? Vector2.Normalize(d) : b.Nm[j]) * (b.R[j] * 0.7f);
    }

    internal long BridgeCalls, BridgeCells, BridgeTicks;  // broadphase cost: searches made, cells looked at, stopwatch ticks (harness builds only)

    /// A stretching drop sticks to another wet host of the same (sticky) liquid if its bead touches it; true if it did.
    internal bool TryBridge(Coat a, int k, Vector2 anchor, Vector2 bead, float rb, float chance, float wet)
    {
        ref var p = ref a.Pend[k];
        if (Hash01(p.Salt + 11) > chance) return false;  // most strings never get to meet anything
        int slot = -1;
        for (int s = 0; s < MaxBridges; s++) if (!Br[s].Live) { slot = s; break; }
        if (slot < 0) return false;
        for (int s = 0; s < MaxBridges; s++)  // strings keep their distance from one another
            if (Br[s].Live && Vector2.DistanceSquared(Br[s].Pa, anchor) < 3600f * a.Px * a.Px) return false;
        BridgeCalls++;
#if REALDEBUFFS_PROFILE
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try {
#endif
        foreach (var b in _coats.Values)
        {
            if (b == a || b.Matter != a.Matter || a.LastSeen - b.LastSeen > 0.25f) continue;  // the other host may not be tended yet this frame; last frame is fine
            if (a.ChartId != 0 && b.Seed == a.Seed && b.Owner == a.Owner) continue;  // a host's own parts are not two hosts
            float lim = b.BoundsR + rb;
            if (bead.X < b.BoundsMin.X - lim || bead.X > b.BoundsMax.X + lim || bead.Y < b.BoundsMin.Y - lim || bead.Y > b.BoundsMax.Y + lim) continue;  // nowhere near any of its cells
            int cells = b.Live * b.V;
            BridgeCells += cells;
            for (int j = 0; j < cells; j++)
            {
                float reach = b.R[j] + rb;
                if (!b.Vis[j] || Vector2.DistanceSquared(b.P[j], bead) > reach * reach || b.M[j] / b.A[j] < wet) continue;
                Vector2 pb = Contact(b, j, anchor);
                // its rest length is the neck it grew from, longer than the gap: slack to droop
                a.AnchorFor(p.Cell, out float ua, out float va); b.AnchorFor(j, out float ub, out float vb);
                Br[slot] = new() { Live = true, A = a, B = b, UA = ua, VA = va, UB = ub, VB = vb, M = p.M, L0 = Max(Max(Vector2.Distance(anchor, pb) * 1.25f, p.L), 6f * a.Px), Salt = p.Salt, Pa = anchor, Pb = pb };
                a.Bridged += p.M;
                a.PendAt[p.Cell] = -1; p.Live = false;
                return true;
            }
        }
        return false;
#if REALDEBUFFS_PROFILE
        } finally { BridgeTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
#endif
    }

    /// Stretches, drains and breaks every bridge against this frame's hosts.
    void StepBridges(float time)
    {
        for (int s = 0; s < MaxBridges; s++)
        {
            ref var br = ref Br[s];
            if (!br.Live) continue;
            var a = br.A; var b = br.B;
            if (a.Matter is not LiquidMatter lm) { Retract(ref br, a); continue; }
            int ia = a.CellAt(br.UA, br.VA, out bool laidA), ib = b.CellAt(br.UB, br.VB, out bool laidB);
            if (a.LastSeen != time || b.LastSeen != time || !laidA || !laidB) { Retract(ref br, a.LastSeen != time ? a : b); continue; }  // a host is gone or not yet grown there

            a.AnchorOf(ia, out br.Pa);
            br.Pb = Contact(b, ib, br.Pa);
            float len = Vector2.Distance(br.Pa, br.Pb), px = a.Px;
            var ph = lm.Physics;

            // it drains into both ends and thins; a string stretched too far, or too thin, lets go
            float drain = br.M * (1f - Exp(-_dt / Max(0.6f, 4f * ph.Thread)));
            br.M -= drain; a.Bridged -= drain;
            a.Credit(ia, drain * 0.5f);
            a.Out += drain * 0.5f; b.In += drain * 0.5f; b.Credit(ib, drain * 0.5f);
            br.Thick = br.M / Max(len, 1f);
            br.Sag = Sqrt(0.375f * len * Max(0f, br.L0 - len)) + 2f * px;  // slack hangs under its own weight
            if (len > br.L0 * (1.6f + 3f * ph.Stringiness) || br.Thick < 0.35f * px) Snap(ref br, lm);
        }
    }

    /// The string lets go: most of it recoils into the two films, a little falls (or stays in the film if the pool has no room).
    void Snap(ref Bridge br, LiquidMatter lm)
    {
        var a = br.A; var b = br.B;
        float m = br.M; a.Bridged -= m; br.Live = false;
        int ia = a.CellAt(br.UA, br.VA, out _), ib = b.CellAt(br.UB, br.VB, out _);
        float back = m * 0.35f, fall = m * 0.15f;
        a.Credit(ia, back); a.Out += back; b.In += back; b.Credit(ib, back);
        Vector2 mid = (br.Pa + br.Pb) * 0.5f + new Vector2(0f, br.Sag);
        if (lm.TryDrop(a, mid + new Vector2(-4f * a.Px, 0f), br.Pa, br.Salt + 21, fall, 0.2f)) { a.Out += fall; a.Shed += fall; } else a.Credit(ia, fall);
        if (lm.TryDrop(a, mid + new Vector2(4f * a.Px, 0f), br.Pb, br.Salt + 22, fall, 0.2f)) { a.Out += fall; a.Shed += fall; } else b.Credit(ib, fall);
    }

    bool Alive(Coat c, Coat gone) => c != gone && !c.Dissolved && _now - c.LastSeen < 0.1f;

    /// A host has gone: the string retracts into the surviving one (or is booked as lost if neither is left).
    void Retract(ref Bridge br, Coat gone)
    {
        var a = br.A; var b = br.B;
        float m = br.M; a.Bridged -= m; br.Live = false;
        bool aOk = Alive(a, gone), bOk = Alive(b, gone);
        if (aOk) { a.Credit(a.CellAt(br.UA, br.VA, out _), m); return; }
        if (bOk) { a.Out += m; b.In += m; b.Credit(b.CellAt(br.UB, br.VB, out _), m); return; }
        Sunk[(int)Sink.HostGone] += m; a.Out += m;
    }

    /// Every string on this coat lets go to the other end (used when the coat is reset or dissolved).
    void DetachBridges(Coat c)
    {
        for (int s = 0; s < MaxBridges; s++)
            if (Br[s].Live && (Br[s].A == c || Br[s].B == c)) Retract(ref Br[s], c);
    }

    static readonly Vector2[] Pts = new Vector2[16];

    internal void DrawBridges(Coat a, ImDrawListPtr dl, float alpha, in MaterialContext ctx)
    {
        for (int s = 0; s < MaxBridges; s++)
        {
            ref readonly var br = ref Br[s];
            if (br.Live && br.A == a) ((LiquidMatter)a.Matter).DrawFilament(dl, in br, Pts, Min(1f, alpha * ctx.Alpha), in ctx);
        }
    }

    /// Hands the scene this frame's films and everything hanging, burning or falling.
    public void Emit(EffectScene scene, float time)
    {
        var owner = scene.CurrentOwner;
        BeadsDrawn = 0;
        StepBridges(time);
        foreach (var c in _coats.Values)
        {
            if (c.LastSeen != time || c.Live < 2) continue;
            scene.AddCoat(new CoatPrimitive { Owner = c.Owner, HostSeed = c.Seed, Coat = c, Alpha = c.Bright, ColorOverride = c.Color });
            scene.CurrentOwner = c.Owner;
            c.Matter.Emit(scene, c, time);
        }
        scene.CurrentOwner = owner;
        Drops.Emit(scene, time, hosted: true);
        Lumps.EmitOwned(scene, time);
        Cinders.EmitOwned(scene, time);
        Smoke.EmitOwned(scene, time);
    }
}
