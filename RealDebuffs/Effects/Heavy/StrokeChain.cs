namespace RealDebuffs.Effects.Framework.Materials;

/// Iron chain: oval links rolled about the strand, matcap-shaded, far halves first so edge-on links interlock.
public sealed class StrokeChain : IStrokeMaterial, ICoatLayeredHost, ICoatSurfaceProvider
{
    public string Name => "stroke.chain";
    public string[] NaturalLanguageWords { get; } = { "chain", "chains", "links" };

    const float MinLinkFrac = 0.030f, MaxLinkFrac = 0.085f;
    const int MaxLinks = 200;

    // Link proportions as fractions of outer length L: 5 bar-diameters long, 3.4 wide
    const float BarDiameter = 0.200f, Pitch       = 0.600f, HalfAxis    = 0.400f, HalfLateral = 0.240f;
    const float CapCentre   = HalfAxis - HalfLateral;

    static readonly Matcap Iron  = new(SurfacePresets.BlackIron);
    static readonly Matcap Steel = new(SurfacePresets.WornSteel);
    static readonly Matcap Aged  = new(SurfacePresets.AgedIron);
    static readonly Matcap Rust  = new(SurfacePresets.Rust);

    internal sealed class RingTable
    {
        public int M;
        public float[] X = null!, Y = null!, NX = null!, NY = null!, Cap = null!;
        public float[] Arc = null!, Frac = null!;  // distance along the ring to each station, in link lengths; and as a fraction of the whole half
    }

    internal static readonly RingTable[] Rings = { null!, null!, BuildRing(2), BuildRing(3), BuildRing(4) };

    static RingTable BuildRing(int k)
    {
        int m = 2 * (k + 1);
        var t = new RingTable
        {
            M = m, X = new float[m], Y = new float[m], NX = new float[m], NY = new float[m], Cap = new float[m],
        };

        for (int q = 0; q <= k; q++)
        {
            float phi = q / (float)k * (PI * 0.5f);
            t.X[q] = CapCentre + HalfLateral * Cos(phi);
            t.Y[q] = HalfLateral * Sin(phi);
            t.NX[q] = Cos(phi); t.NY[q] = Sin(phi);

            int j = k + 1 + q;
            float phi2 = PI * 0.5f + q / (float)k * (PI * 0.5f);
            t.X[j] = -CapCentre + HalfLateral * Cos(phi2);
            t.Y[j] = HalfLateral * Sin(phi2);
            t.NX[j] = Cos(phi2); t.NY[j] = Sin(phi2);
        }

        for (int i = 0; i < m; i++)
        {
            float q = Abs(t.X[i]) / HalfAxis;
            t.Cap[i] = Smooth((q - 0.55f) / 0.45f);
        }
        t.Arc = new float[m]; t.Frac = new float[m];
        for (int i = 1; i < m; i++) t.Arc[i] = t.Arc[i - 1] + Sqrt((t.X[i] - t.X[i - 1]) * (t.X[i] - t.X[i - 1]) + (t.Y[i] - t.Y[i - 1]) * (t.Y[i] - t.Y[i - 1]));
        for (int i = 0; i < m; i++) t.Frac[i] = t.Arc[i] / t.Arc[m - 1];
        return t;
    }

    // Column offsets across the bar (-1..1; first/last are the AA fringe)
    static readonly float[] Cols7   = { -1f, -1f, -0.62f, 0f, 0.62f, 1f, 1f };
    static readonly float[] Cols5   = { -1f, -1f, 0f, 1f, 1f };
    static readonly float[] Cols7Sq = BuildSq(Cols7);
    static readonly float[] Cols5Sq = BuildSq(Cols5);

    static float[] BuildSq(float[] us)
    {
        var r = new float[us.Length];
        for (int i = 0; i < us.Length; i++)
            r[i] = Sqrt(Max(0f, 1f - us[i] * us[i]));
        return r;
    }

    internal struct Link
    {
        public Vector2 C, T, N;
        public float Cos, Sin, Tone, Rust, Alpha;
        public int   Index, BucketPlus, BucketMinus;
        public float Arc, Span;  // where along the strand this link sits and how much of it it covers
    }

    /// One chain stroke's geometry at one moment (links, pose, roll, depth buckets, LOD), built once a frame; drawing and charts both read it.
    internal sealed class Snapshot
    {
        public readonly Link[] Links = new Link[MaxLinks];
        public StrandPath? Path; public float Time, Reveal, Width;  // what it was built from: a memo, rebuilt if any of these differ
        public bool Built, Valid;
        public int Count, LinkCount, RingK, Tier, Seed;  // Count: links laid; LinkCount: links the strand has, laid or not
        public float[]?[] Adh = [];  // each link half's adhesion by chart cell (rule 9), kept while the chart's tier and ring stay
        public int AdhKey = -1;
        public float L, Total, Pitch, VisibleLen, Bar, Feather, Depth, Agit;
        public bool Closed, FullyRevealed;
        public float[] Cols = Cols7, ColsSq = Cols7Sq;
        public Matcap Body = Iron;
    }

    readonly record struct SnapKey(DebuffKind Owner, int Seed, PrimitiveRole Role);
    readonly Dictionary<SnapKey, Snapshot> _snaps = new();
    readonly List<SnapKey> _dead = new();

    Snapshot? Acquire(in StrokePrimitive s, float shortSide, float px, float time, float screenW, float screenH)
    {
        var key = new SnapKey(s.Owner, s.Seed, s.Role);
        if (!_snaps.TryGetValue(key, out var sn))
        {
            if (_snaps.Count >= 32) Prune(time);
            _snaps[key] = sn = new();
        }
        if (!(sn.Built && sn.Time == time && ReferenceEquals(sn.Path, s.Path) && sn.Reveal == s.Reveal && sn.Width == s.WidthHint))
        {
            sn.Built = true; sn.Time = time; sn.Path = s.Path; sn.Reveal = s.Reveal; sn.Width = s.WidthHint;
            sn.Valid = Build(in s, shortSide, px, time, screenW, screenH, sn);
        }
        return sn.Valid ? sn : null;
    }

    void Prune(float time)
    {
        _dead.Clear();
        foreach (var kv in _snaps) if (time - kv.Value.Time > 2f) _dead.Add(kv.Key);
        foreach (var k in _dead) _snaps.Remove(k);
    }

    /// Lays out a strand's links: placement, roll and the depth buckets of each half; false if there is nothing to draw.
    bool Build(in StrokePrimitive s, float shortSide, float px, float time, float screenW, float screenH, Snapshot sn)
    {
        var path = s.Path;
        if (path.Count < 2) return false;

        float reveal = Saturate(s.Reveal);
        if (reveal <= 0.001f) return false;

        float total = path.Length;
        float L = Math.Clamp(s.WidthHint, shortSide * MinLinkFrac, shortSide * MaxLinkFrac);
        if (total < L) return false;

        float depth = Saturate(s.Depth);
        float agit = Saturate(s.Agitation);
        int seed = s.Seed;

        // Bar width is fixed per chain; the LOD tier and far-half cull both key off it.
        float bar = L * BarDiameter;
        bool smallBar = bar < 9.5f * px;

        float pick = Hash01(seed + 41);
        Matcap body = pick < 0.44f ? Iron : (pick < 0.76f ? Steel : Aged);
        float rustLevel = Pow(Hash01(seed + 42), 1.7f) * 0.85f;
        float twistBase = HashRange(seed + 43, -0.75f, 0.75f);
        float twistAmp  = HashRange(seed + 44, 0.30f, 1.05f);
        float twistFreq = HashRange(seed + 45, 0.55f, 1.15f);

        float pitch = L * Pitch;
        bool closed = s.Closed;
        int count;
        float firstCentre;
        if (closed)
        {
            // Even link count so alternation and seam both close.
            int n = Math.Max(4, 2 * (int)Round(total / (2f * pitch)));
            pitch = total / n;
            count = Math.Min(n, MaxLinks);
            firstCentre = 0f;
        }
        else
        {
            firstCentre = L * 0.5f + (s.FlushStart ? 0f : -2f * pitch);
            float spanEnd = reveal >= 1f ? total + L : total * reveal;
            count = Math.Clamp((int)((spanEnd - firstCentre) / pitch) + 2, 0, MaxLinks);
        }
        if (count <= 0) return false;

        float visibleLen = total * reveal;
        bool fullyRevealed = reveal >= 1f;

        int linkCount = 0;
        float margin = L * 1.2f;
        for (int i = 0; i < count; i++)
        {
            float sc = firstCentre + i * pitch;
            float tipFrac = 1f;
            if (!closed && !fullyRevealed)
            {
                tipFrac = (visibleLen - (sc - L * 0.5f)) / L;
                if (tipFrac <= 0f) break;
                tipFrac = Min(1f, tipFrac);
            }

            Vector2 a = SampleWrapped(path, sc - pitch * 0.5f, total, closed);
            Vector2 b = SampleWrapped(path, sc + pitch * 0.5f, total, closed);
            Vector2 chord = b - a;
            float cl = chord.Length();
            if (cl < 1e-3f) continue;

            Vector2 c = (a + b) * 0.5f;
            if (c.X < -margin || c.Y < -margin || c.X > screenW + margin || c.Y > screenH + margin) continue;

            Vector2 T = chord / cl;
            int idx = i;

            // Twist along the strand + sway + agitation + the quarter-turn interlocking forces.
            float twist = twistBase
                        + twistAmp * Noise.Value(sc / (L * 5.5f) * twistFreq + seed * 0.00137f, 7.3f + time * 0.045f)
                        + 0.16f * Sin(time * 0.55f + s.Phase + sc * 0.0031f)
                        + agit * 0.30f * Sin(time * 21f + idx * 1.9f);
            float roll = twist + ((idx & 1) == 0 ? 0f : PI * 0.5f);

            float cs = Cos(roll), sn2 = Sin(roll);
            int ls = unchecked(seed + idx * 7919);

            ref Link lk = ref sn.Links[linkCount++];
            lk.C = c; lk.T = T; lk.N = new(-T.Y, T.X);
            lk.Cos = cs; lk.Sin = sn2;
            lk.Tone = HashRange(ls + 3, 0.86f, 1.10f);
            lk.Rust = Saturate(rustLevel * HashRange(ls + 5, 0.25f, 1.45f));
            lk.Alpha = tipFrac;
            lk.Index = idx;
            lk.Arc = sc; lk.Span = pitch;

            float zPlus = sn2;
            lk.BucketPlus = (int)Round(zPlus * 2f) + 2;
            lk.BucketMinus = (int)Round(-zPlus * 2f) + 2;

            // Hide the far half entirely when it's too thin to read.
            if (Abs(cs) < 0.06f || (smallBar && Abs(cs) < 0.55f))
            {
                if (zPlus >= 0f) lk.BucketMinus = -1; else lk.BucketPlus = -1;
            }
        }

        sn.Count = linkCount; sn.LinkCount = count; sn.L = L; sn.Total = total; sn.Pitch = pitch; sn.VisibleLen = visibleLen; sn.Bar = bar;
        sn.Depth = depth; sn.Agit = agit; sn.Closed = closed; sn.FullyRevealed = fullyRevealed; sn.Seed = seed; sn.Body = body;
        sn.RingK = bar < 9.5f * px ? 2 : (bar < 14.5f * px ? 3 : 4);
        sn.Cols = smallBar ? Cols5 : Cols7; sn.ColsSq = smallBar ? Cols5Sq : Cols7Sq;
        sn.Feather = (1.0f + 2.4f * depth) * px;
        return true;
    }

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        if (path.Count < 2 || alpha <= 0.004f) return;

        var sn = Acquire(in s, ctx.ShortSide, ctx.ScreenScale, ctx.Time, ctx.ScreenW, ctx.ScreenH);
        if (sn == null) return;

        StrandShading.DrawShadow(dl, path, sn.L, sn.Depth, alpha, ctx, sn.Closed, sn.VisibleLen);

        int ringK = sn.RingK;
        float[] cols = sn.Cols, colsSq = sn.ColsSq;

        // Paint back to front by depth bucket.
        for (int bucket = 0; bucket <= 4; bucket++)
        {
            for (int i = 0; i < sn.Count; i++)
            {
                ref Link lk = ref sn.Links[i];
                if (lk.BucketPlus == bucket)
                {
                    DrawHalf(dl, in lk, +1, sn.L, ringK, cols, colsSq, sn.Body, sn.Seed, sn.Depth, sn.Agit, sn.Feather, alpha, ctx);
                    if (_coatOn) DrawCoatHalf(dl, in lk, +1, sn, ringK, cols, colsSq, in ctx);
                }
                if (lk.BucketMinus == bucket)
                {
                    DrawHalf(dl, in lk, -1, sn.L, ringK, cols, colsSq, sn.Body, sn.Seed, sn.Depth, sn.Agit, sn.Feather, alpha, ctx);
                    if (_coatOn) DrawCoatHalf(dl, in lk, -1, sn, ringK, cols, colsSq, in ctx);
                }
            }
        }

        if (s.TipFlare > 0.001f && !sn.Closed)
        {
            float tipS = sn.FullyRevealed ? sn.Total : sn.VisibleLen;
            path.SampleAtArc(tipS, out Vector2 tip, out _);
            StrandShading.DrawFlare(dl, tip, sn.Bar, Saturate(s.TipFlare) * alpha, ctx);
        }
    }

    /// One station of a link half's ring: bar centre on screen, outward direction, cross-section frame; body, charts and film all read it.
    internal static void Station(in Link lk, int h, RingTable ring, int j, float L, ref Vector3 prevN, ref Vector3 prevT,
                                out Vector2 pos2, out Vector3 o, out Vector3 nStar, out Vector3 tStar)
    {
        float cos = lk.Cos, sin = lk.Sin;
        float x = ring.X[j] * L;
        float y = h * ring.Y[j] * L;
        float nx = ring.NX[j];
        float ny = h * ring.NY[j];

        pos2 = lk.C + lk.T * x + lk.N * (y * cos);

        o = new(lk.T.X * nx + lk.N.X * (ny * cos), lk.T.Y * nx + lk.N.Y * (ny * cos), ny * sin);
        Vector3 m = new(-lk.N.X * sin, -lk.N.Y * sin, cos);

        float oz = o.Z, mz = m.Z;
        float r = Sqrt(oz * oz + mz * mz);
        if (r > 1e-3f)
        {
            nStar = (o * oz + m * mz) / r;
            tStar = (m * oz - o * mz) / r;
            prevN = nStar; prevT = tStar;
        }
        else { nStar = prevN; tStar = prevT; }
    }

    // ---- the coat: one chart per drawn link half, cut from the same stations the half is drawn from

    // rows x sectors a half's chart may have, finest first; a crowded frame takes a coarser one before it skips any link
    static readonly (int rows, int sectors)[] Ladder = { (6, 5), (5, 5), (5, 3), (4, 3), (3, 3), (3, 2), (2, 2) };

    public bool Charts(CoatMatter matter) => matter is LiquidMatter;

    // which stations / columns of the ring a chart of r rows / v sectors sits on: always both ends
    static void PickRows(int stations, int rows, Span<int> st) { for (int r = 0; r < rows; r++) st[r] = (int)Round(r * (stations - 1f) / (rows - 1f)); }
    static void PickCols(int columns, int sectors, Span<int> ct) { for (int s = 0; s < sectors; s++) ct[s] = (int)Round(s * (columns - 1f) / (sectors - 1f)); }

    public void BuildCoatSurfaces(in StrokePrimitive s, in CoatBuildContext ctx, CoatCharts charts)
    {
        int sw = (int)ctx.Screen.X, sh = (int)ctx.Screen.Y;
        var sn = Acquire(in s, sw < sh ? sw : sh, ctx.Px, ctx.Time, sw, sh);
        if (sn == null) return;

        int halves = 0;
        for (int i = 0; i < sn.Count; i++) { if (sn.Links[i].BucketPlus >= 0) halves++; if (sn.Links[i].BucketMinus >= 0) halves++; }
        if (halves == 0) return;

        var ring = Rings[sn.RingK];
        int M = ring.M, nc = sn.Cols.Length - 2;

        // the finest tier this stroke's cell share affords; it refines only with room to spare, so it does not flicker
        int want = Ladder.Length - 1;
        for (int t = 0; t < Ladder.Length; t++)
            if (Ladder[t].rows <= M && Ladder[t].sectors <= nc && Ladder[t].rows * Ladder[t].sectors * halves <= charts.Share) { want = t; break; }
        bool fits = Ladder[sn.Tier].rows <= M && Ladder[sn.Tier].sectors <= nc;
        if (!fits || want > sn.Tier || Ladder[want].rows * Ladder[want].sectors * halves * 1.3f <= charts.Share) sn.Tier = want;
        var (R, Vn) = Ladder[sn.Tier];
        int adhKey = sn.Tier * 16 + sn.RingK;
        float clump = Clump(ctx.Viscosity);
        if (sn.AdhKey != adhKey || sn.Adh.Length != sn.LinkCount * 2) { sn.Adh = new float[sn.LinkCount * 2][]; sn.AdhKey = adhKey; }

        Span<int> st = stackalloc int[R], ct = stackalloc int[Vn];
        PickRows(M, R, st); PickCols(nc, Vn, ct);
        Span<float> along = stackalloc float[R], across = stackalloc float[Vn];
        Span<Vector2> p2 = stackalloc Vector2[M];
        Span<Vector3> ns = stackalloc Vector3[M], ts = stackalloc Vector3[M];
        float half = sn.L * BarDiameter * 0.5f;
        for (int i = 0; i < sn.Count * 2; i++) _laid[i] = null;

        for (int i = 0; i < sn.Count; i++)
        {
            ref readonly Link lk = ref sn.Links[i];
            for (int h = 1; h >= -1; h -= 2)
            {
                if ((h > 0 ? lk.BucketPlus : lk.BucketMinus) < 0) continue;
                var ch = charts.Add(1 + lk.Index * 2 + (h > 0 ? 0 : 1), R, Vn);
                if (ch == null) continue;
                _laid[i * 2 + (h > 0 ? 0 : 1)] = ch;

                Vector3 prevN = Vector3.UnitZ, prevT = Vector3.UnitX;
                for (int j = 0; j < M; j++) Station(in lk, h, ring, j, sn.L, ref prevN, ref prevT, out p2[j], out _, out ns[j], out ts[j]);

                float zHalf = h > 0 ? lk.Sin : -lk.Sin;
                ch.Live = R;
                for (int r = 0; r < R; r++) ch.UPos[r] = ring.Arc[st[r]] * sn.L;
                float len = 0f;
                for (int r = 0; r < R; r++)
                {
                    ch.UStep[r] = 0.5f * ((r < R - 1 ? ch.UPos[r + 1] : ch.UPos[r]) - (r > 0 ? ch.UPos[r - 1] : ch.UPos[r]));
                    len += ch.UStep[r];
                }
                ch.Ds = len / R;
                ch.SupplyScale = sn.Pitch * 0.5f / Max(len, 1e-3f);  // both halves together carry what the strand would along one pitch
                for (int v = 0; v < Vn; v++)
                {
                    float th = Asin(sn.Cols[1 + ct[v]]);
                    float lo = v > 0 ? 0.5f * (th + Asin(sn.Cols[ct[v - 1] + 1])) : -PI * 0.5f;
                    float hi = v < Vn - 1 ? 0.5f * (th + Asin(sn.Cols[ct[v + 1] + 1])) : PI * 0.5f;
                    ch.VAngle[v] = th; ch.VStep[v] = Max(0.25f, (hi - lo) * half);
                    across[v] = (th + PI * 0.5f) / (PI * 0.5f);  // two lattice cells across the half of the bar the camera sees
                }
                // rule 9: patches of the bar that hold liquid badly, fixed to this link half, two bar diameters long; made once, then kept
                int slot = lk.Index * 2 + (h > 0 ? 0 : 1);
                float[]? kept = slot < sn.Adh.Length ? sn.Adh[slot] : null;
                if (kept == null)
                {
                    for (int r = 0; r < R; r++) along[r] = ring.Arc[st[r]];
                    kept = new float[R * Vn];
                    CoatAdhesion.Field(sn.Seed * 31 + slot, along, BarDiameter * 2f, across, 0, kept);
                    if (slot < sn.Adh.Length) sn.Adh[slot] = kept;
                }

                for (int r = 0; r < R; r++)
                    for (int v = 0; v < Vn; v++)
                    {
                        int j = st[r], col = 1 + ct[v];
                        float u = sn.Cols[col], sq = sn.ColsSq[col];
                        Vector3 n = ts[j] * u + ns[j] * sq;
                        ref var q = ref ch.S[r * Vn + v];
                        q.Pos = p2[j] + new Vector2(n.X, n.Y) * half;  // the very vertex DrawHalf puts there
                        q.Normal = n; q.Side = Math.Clamp(zHalf, -1f, 1f); q.Radius = half; q.Visible = true; q.Holds = true;
                        q.Z = h * ring.Y[j] * sn.L * lk.Sin + n.Z * half;  // the bar's own depth at that station (its plane is rolled by Sin), then out along the normal
                        float hold = kept[r * Vn + v];
                        q.Pool = CoatAdhesion.Potential(hold, clump); q.Slick = CoatAdhesion.Slick(hold);
                    }

                for (int r = 0; r < R; r++)
                    for (int v = 0; v < Vn; v++)
                    {
                        ref var q = ref ch.S[r * Vn + v];
                        Vector2 du = ch.S[Math.Min(r + 1, R - 1) * Vn + v].Pos - ch.S[Math.Max(r - 1, 0) * Vn + v].Pos;
                        Vector2 dv = ch.S[r * Vn + Math.Min(v + 1, Vn - 1)].Pos - ch.S[r * Vn + Math.Max(v - 1, 0)].Pos;
                        q.Du = du.LengthSquared() > 1e-6f ? Vector2.Normalize(du) : Vector2.UnitX;
                        q.Dv = dv.LengthSquared() > 1e-6f ? Vector2.Normalize(dv) : new Vector2(-q.Du.Y, q.Du.X);  // an edge-on link has no across: take the side of the along

                        // one downhill rule: the edge of the chart that faces most squarely down is where a drop hangs
                        float best = 0f; Vector2 outw = default;
                        void Edge(bool on, Vector2 o) { if (on && o.Y > best) { best = o.Y; outw = o; } }
                        Edge(v == 0, -q.Dv); Edge(v == Vn - 1, q.Dv); Edge(r == 0, -q.Du); Edge(r == R - 1, q.Du);
                        q.GravityDown = best; q.Anchor = q.Pos;
                    }
            }
        }

        // the chain proposes contacts: a link's halves share both tips (one bar cut in two), its +T tip hooks the next link's -T tip; the coat confirms
        for (int i = 0; i < sn.Count; i++)
        {
            CoatChart? a = _laid[i * 2], b = _laid[i * 2 + 1];
            if (a != null && b != null) { a.AddContact(CoatEdge.UStart, b.ChartId, CoatEdge.UStart, true); a.AddContact(CoatEdge.UEnd, b.ChartId, CoatEdge.UEnd, true); }
            int nx = i + 1 < sn.Count && sn.Links[i + 1].Index == sn.Links[i].Index + 1 ? i + 1
                   : (sn.Closed && i == sn.Count - 1 && sn.Links[i].Index == sn.LinkCount - 1 && sn.Links[0].Index == 0 ? 0 : -1);
            if (nx < 0 || nx == i) continue;
            for (int x = 0; x < 2; x++)
                for (int y = 0; y < 2; y++)
                {
                    CoatChart? from = _laid[i * 2 + x], to = _laid[nx * 2 + y];
                    if (from != null && to != null) from.AddContact(CoatEdge.UStart, to.ChartId, CoatEdge.UEnd, false);
                }
        }
    }

#if REALDEBUFFS_PROFILE
    internal static long FilmVerts, FilmBare;  // film mesh vertices drawn, and those under 5% opaque (harness builds only)
#endif

    /// Steel clumps liquid more than fibre does (rule 9): the swing of a chain film's adhesion potential for water, blood and slime, calibrated so a chain exposes a little more bare steel than a rope shows fibre.
    static float Clump(float viscosity) => LiquidPhysics.At(viscosity, 1.85f, 1.8f, 2.4f);

    readonly CoatChart?[] _laid = new CoatChart?[2 * MaxLinks];  // this frame's charts by link slot and half
    readonly Coat?[] _byId = new Coat?[2 * MaxLinks + 2];
    List<Coat>? _sib;
    float _coatAlpha;
    bool _coatOn;

    /// Liquid only: it is drawn as a film on each link's own bar, in that link half's depth order; other matters keep the generic band.
    public bool Layers(Coat coat) => coat.Matter is LiquidMatter && coat.Siblings != null;

    public void DrawWithCoat(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx, Coat coat, float alpha)
    {
        _coatAlpha = alpha; _sib = coat.Siblings;
        if (_sib != null) foreach (var c in _sib) if ((uint)c.ChartId < (uint)_byId.Length) _byId[c.ChartId] = c;
        _coatOn = true;
        try { Draw(dl, in s, in ctx); }
        finally
        {
            _coatOn = false;
            if (_sib != null) foreach (var c in _sib) if ((uint)c.ChartId < (uint)_byId.Length) _byId[c.ChartId] = null;
            _sib = null;
        }
    }

    /// The film on one link half: a mesh on its own stations and columns (holes and gaps stay empty), in its depth bucket, from the chart.
    void DrawCoatHalf(ImDrawListPtr dl, in Link lk, int h, Snapshot sn, int ringK, float[] cols, float[] colsSq, in MaterialContext ctx)
    {
        var coat = _byId[1 + lk.Index * 2 + (h > 0 ? 0 : 1)];
        if (coat == null || coat.Matter is not LiquidMatter lm) return;
        int R = coat.N, Vn = coat.V;
        if (R < 2 || Vn < 2 || R * Vn > 64) return;
        float px = coat.Px;
        float dry = lm.Physics.WetMin * lm.Physics.Hold * px;  // where the film is thinner than the liquid wets, the bar shows

        Span<float> thick = stackalloc float[R * Vn];
        float maxTh = 0f;
        for (int c = 0; c < R * Vn; c++) { thick[c] = coat.M[c] / coat.A[c]; maxTh = Max(maxTh, thick[c]); }
        if (maxTh < 0.25f * lm.Physics.Hold * px) return;

        float L = sn.L, zHalf = h > 0 ? lk.Sin : -lk.Sin;
        if (zHalf < -0.35f) { ringK = Math.Max(2, ringK - 1); cols = Cols5; colsSq = Cols5Sq; }
        var ring = Rings[ringK];
        int M = ring.M, nc = cols.Length - 2;  // real columns; the fringe is the body's anti-aliasing
        if (M * nc > MeshDraw.MaxVerts) return;

        // where the chart's rows and sectors sit along the ring and across the bar, to interpolate between
        var cring = Rings[sn.RingK];
        int cnc = sn.Cols.Length - 2;
        Span<int> st = stackalloc int[R], ct = stackalloc int[Vn];
        PickRows(cring.M, R, st); PickCols(cnc, Vn, ct);
        Span<float> fr = stackalloc float[R], uc = stackalloc float[Vn];
        for (int r = 0; r < R; r++) fr[r] = cring.Frac[st[r]];
        for (int v = 0; v < Vn; v++) uc[v] = sn.Cols[1 + ct[v]];

        Span<Vector2> p2 = stackalloc Vector2[M];
        Span<Vector3> ns = stackalloc Vector3[M], ts = stackalloc Vector3[M];
        Vector3 prevN = Vector3.UnitZ, prevT = Vector3.UnitX;
        for (int j = 0; j < M; j++) Station(in lk, h, ring, j, L, ref prevN, ref prevT, out p2[j], out _, out ns[j], out ts[j]);

        float half = L * BarDiameter * 0.5f, a = Min(1f, _coatAlpha * ctx.Alpha) * lk.Alpha;
        var mb = CoatDraw.Mb;
        mb.Reserve(M * nc, M * nc * 6);
        int first = mb.Vertices;
        PushColorOverride(coat.Color);
        try
        {
            for (int j = 0; j < M; j++)
            {
                float fj = ring.Frac[j];
                int r0 = 0; while (r0 < R - 2 && fj > fr[r0 + 1]) r0++;
                float tr = fr[r0 + 1] > fr[r0] ? Saturate((fj - fr[r0]) / (fr[r0 + 1] - fr[r0])) : 0f;
                for (int c = 1; c <= nc; c++)
                {
                    float u = cols[c], sq = colsSq[c];
                    int v0 = 0; while (v0 < Vn - 2 && u > uc[v0 + 1]) v0++;
                    float tv = uc[v0 + 1] > uc[v0] ? Saturate((u - uc[v0]) / (uc[v0 + 1] - uc[v0])) : 0f;
                    float th = Lerp(Lerp(thick[r0 * Vn + v0], thick[r0 * Vn + v0 + 1], tv), Lerp(thick[(r0 + 1) * Vn + v0], thick[(r0 + 1) * Vn + v0 + 1], tv), tr);
                    Vector3 n = ts[j] * u + ns[j] * sq;
                    uint shade = lm.FilmShade(n, th, px, a, CoatDraw.Soft(u, 0.3f), dry);
#if REALDEBUFFS_PROFILE
                    FilmVerts++; if ((shade >> 24) < 13) FilmBare++;
#endif
                    mb.VertexReserved(p2[j] + new Vector2(n.X, n.Y) * (half + th * 0.9f), shade);
                }
            }
        }
        finally { PopColorOverride(); }
        mb.StitchGrid(first, M, nc);
        mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));
    }

    static void DrawHalf(ImDrawListPtr dl, in Link lk, int h, float L, int ringK,
                                 float[] cols, float[] colsSq,
                                 Matcap body, int seed, float depth, float agit, float feather,
                                 float alpha, in MaterialContext ctx)
    {
        float half = L * BarDiameter * 0.5f;
        float sin = lk.Sin;

        float zHalf = h > 0 ? sin : -sin;
        float far = Saturate(-zHalf * 1.6f);

        // Behind-the-plane half is mostly hidden: spend fewer verts on it.
        if (zHalf < -0.35f) { ringK = Math.Max(2, ringK - 1); cols = Cols5; colsSq = Cols5Sq; }

        var ring = Rings[ringK];
        int M = ring.M;
        int nc = cols.Length;
        if (M * nc > MeshDraw.MaxVerts) return;
        float linkAlpha = alpha * lk.Alpha;

        float depthFog = 0.55f * depth;

        Vector3 prevN = Vector3.UnitZ, prevT = Vector3.UnitX;
        Vector2[] outP = MeshDraw.P;
        uint[] outC = MeshDraw.C;

        // Same bytes WithAlpha would produce (Pack gives alpha 255), without a clamp and conversion per vertex.
        bool overridden = ColorOverrideActive;
        uint alphaBits = AlphaBits(linkAlpha);
        int v = 0;

        for (int j = 0; j < M; j++)
        {
            float x = ring.X[j] * L;
            float y = h * ring.Y[j] * L;
            Station(in lk, h, ring, j, L, ref prevN, ref prevT, out Vector2 pos2, out Vector3 o, out Vector3 nStar, out Vector3 tStar);

            float cap = ring.Cap[j];

            // Below this a link's rust never rises above a faint tint, so skip the noise entirely.
            float rustA = 0f, rustB = 0f;
            bool rusty = lk.Rust > 0.10f;
            if (rusty)
            {
                float nxr = lk.Index * 1.713f + x / L * 3.4f;
                float nyr = h * 4.1f + y / L * 3.4f + seed * 0.0007f;
                rustA = Noise.Value(nxr - 0.9f, nyr) * 0.5f + 0.5f;
                rustB = Noise.Value(nxr + 0.9f, nyr) * 0.5f + 0.5f;
            }

            // Shade real columns only: the fringe columns share their neighbour's normal and colour
            int vb = v;
            for (int c = 1; c < nc - 1; c++)
            {
                float u = cols[c];
                float sq = colsSq[c];

                Vector3 n = tStar * u + nStar * sq;
                Vector2 pos = pos2 + new Vector2(n.X, n.Y) * half;

                Vector3 col = body.Sample(n.X, n.Y);

                // Rust patches live in link space so they travel with the link
                float rustMask = 0f;
                if (rusty)
                {
                    float rm = Lerp(rustA, rustB, (u * 0.5f + 0.5f)) + (lk.Rust - 0.5f) * 0.95f;
                    rustMask = Smooth((rm - 0.56f) / 0.24f) * (1f - 0.85f * cap) * 0.85f;
                }
                if (rustMask > 0.01f) col = Vector3.Lerp(col, Rust.Sample(n.X, n.Y), rustMask);

                // AO inside the tips (where the neighbour bears) / brighten the rubbed outside.
                float outer = n.X * o.X + n.Y * o.Y + n.Z * o.Z;
                float ao = (1f - 0.50f * cap * Max(0f, -outer)) * (1f - 0.20f * far);
                float wear = 1f + 0.10f * cap * Max(0f, outer) * (1f - rustMask);
                col *= ao * wear * lk.Tone;

                if (agit > 0.01f) col += col * (col * (0.55f * agit));
                if (depthFog > 0f) col = Vector3.Lerp(col, StrandShading.DepthFog, depthFog);

                outP[vb + c] = pos;
                outC[vb + c] = VertexColor(col, linkAlpha, alphaBits, overridden);
            }

            // Fringe: the neighbour's colour at alpha 0, pushed outward by the feather.
            outP[vb] = outP[vb + 1] + new Vector2(tStar.X, tStar.Y) * (cols[0] * feather);
            outC[vb] = outC[vb + 1] & 0x00FFFFFFu;
            outP[vb + nc - 1] = outP[vb + nc - 2] + new Vector2(tStar.X, tStar.Y) * (cols[nc - 1] * feather);
            outC[vb + nc - 1] = outC[vb + nc - 2] & 0x00FFFFFFu;
            v += nc;
        }

        MeshDraw.Grid(dl, nc - 1, M - 1, MeshDraw.WhiteUv(ctx.Time));
    }

    static Vector2 SampleWrapped(StrandPath path, float s, float total, bool closed)
    {
        if (closed && total > 1e-3f) { s %= total; if (s < 0f) s += total; }
        path.SampleAtArc(s, out Vector2 p, out _);
        return p;
    }

    /// A link's lateral half-extent: ellipse half-width plus half the bar, so flowing particles ride the surface.
    public float RadiusAt(in StrokePrimitive s, float arc, float shortSide)
        => (HalfLateral + BarDiameter * 0.5f) * Math.Clamp(s.WidthHint, shortSide * MinLinkFrac, shortSide * MaxLinkFrac);

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    /// Rust flakes dropping; dust sifting more slowly. Sparse on purpose.
    static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Flake,
            DensityPer100px: 0.30f,
            SpeedMin: 4f, SpeedMax: 26f,
            LifespanMin: 1.1f, LifespanMax: 2.2f,
            SizeMin: 1.3f, SizeMax: 2.8f,
            SpreadRadians: 0.9f,
            BiasVelocity: new Vector2(0f, 12f),
            PrimaryDirection: new Vector2(0f, 1f),
            Gravity: new Vector2(0f, 560f)),
        new(Role: PrimitiveRole.Dust,
            DensityPer100px: 0.10f,
            SpeedMin: 3f, SpeedMax: 14f,
            LifespanMin: 2.0f, LifespanMax: 3.6f,
            SizeMin: 5f, SizeMax: 10f,
            SpreadRadians: 1.2f,
            BiasVelocity: new Vector2(0f, 5f),
            PrimaryDirection: new Vector2(0f, 1f),
            Gravity: new Vector2(0f, 18f)),
    };

    public ReadOnlySpan<ImpactEmission> ImpactEmissions => Hit;

    /// Sparks, scale, and a puff of dust on impact.
    static readonly ImpactEmission[] Hit =
    {
        new(Role: PrimitiveRole.Spark, CountMin: 16, CountMax: 28,
            SpeedMin: 420f, SpeedMax: 1400f, LifespanMin: 0.30f, LifespanMax: 0.78f,
            SizeMin: 1.6f, SizeMax: 3.8f, ConeRadians: 1.0f, Gravity: new Vector2(0f, 2600f)),
        new(Role: PrimitiveRole.Flake, CountMin: 12, CountMax: 22,
            SpeedMin: 110f, SpeedMax: 460f, LifespanMin: 0.9f, LifespanMax: 1.9f,
            SizeMin: 1.6f, SizeMax: 3.6f, ConeRadians: 1.35f, Gravity: new Vector2(0f, 900f)),
        new(Role: PrimitiveRole.Dust, CountMin: 3, CountMax: 5,
            SpeedMin: 35f, SpeedMax: 150f, LifespanMin: 0.9f, LifespanMax: 1.7f,
            SizeMin: 16f, SizeMax: 30f, ConeRadians: 1.5f, Gravity: new Vector2(0f, -14f)),
    };
}