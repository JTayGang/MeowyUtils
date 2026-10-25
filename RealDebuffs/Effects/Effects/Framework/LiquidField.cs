using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// A body of viscous liquid lying on the glass, as a scalar potential over a screen grid: pools banked along the screen's edges, crawling arms (a bent capsule that swells
/// to a head), and blobs (a splat with a crown of fingers, or a streak running down). Each adds a smooth bump; where bumps overlap they merge into one mass, and where a
/// neck thins it pinches off, so a living liquid needs no special cases. The mass is where the potential is at least <see cref="Iso"/>; below it the potential
/// is the wet halo the liquid reaches. The effect animates the inputs, calls <see cref="Solve"/> once a frame, and a region material (RegionLiquid) shades and draws what it finds.
/// </summary>
/// <remarks>
/// Only the grid tiles that something can reach are evaluated, so a mass banked along the edges costs a third of the screen, not all of it. Everything is plain
/// arrays reused frame to frame: no allocation after the grid is sized. Nothing here knows what the liquid is; slime, blood and acid are a LiquidSpec at draw time.
/// </remarks>
internal sealed class LiquidField
{
    /// <summary>Potential at the liquid's edge. A lone blob of boundary radius r reaches <see cref="Iso"/> exactly r from its centre.</summary>
    public const float Iso = 0.5f;

    /// <summary>A bump's kernel radius, in boundary radii: wide enough that two neighbours merge across a neck, narrow enough to stay local.</summary>
    public const float KernelReach = 2.2f;

    /// <summary>Depth in px at which a pool has the full strength of a pool. Shallower than this it is only a film, and fainter.</summary>
    private const float FilmDepth = 10f;

    public const int PoolSamples = 96;
    public const int TileCells = 8;
    private const float KernelAtIso = 0.454f;       // x where (1 - x^2)^3 = Iso: kernel radius * this = boundary radius

    /// <summary>A bump is taken to end at this fraction of its kernel radius (where it has fallen to under half a percent), so it is not evaluated, or its tiles kept, for a tail nobody can see.</summary>
    private const float Support = 0.92f, SupportSq = Support * Support;

    /// <summary>Once the potential is this deep, adding more changes nothing anyone can see (the surface is as thick as it is shaded), so the rest of the bumps skip that vertex.</summary>
    private const float Saturated = 1.25f;

    /// <summary>A pseudopod: a bent capsule from a root (usually off-screen) through a mid point to a swollen head, with lumps running along it.</summary>
    public struct Arm
    {
        public Vector2 Root, Mid, Head;
        /// <summary>Boundary radius in px at the root, along the shaft, and in the head's bulb.</summary>
        public float RootRadius, ShaftRadius, HeadRadius;
        /// <summary>Swellings that travel along the arm: fraction of the radius, how many fit along it, and their phase (advance it to make them travel).</summary>
        public float WaveAmp, WaveCycles, WavePhase;
        /// <summary>0..1 scales the bump, so the arm grows and withdraws smoothly (its edge sweeps in and out) instead of popping.</summary>
        public float Gain;
    }

    /// <summary>A splat or a running streak. Tail equal to Center is a round blob; otherwise a capsule that thins toward the tail.</summary>
    public struct Blob
    {
        public Vector2 Center, Tail;
        public float Radius, Gain;
        /// <summary>How much the radius changes from Center to Tail: 0.45 thins toward the tail (a tapering run); negative swells (a thin trail that ends in a heavy head).</summary>
        public float TailTaper;
        /// <summary>The crown of fingers a splat throws out: amplitude as a fraction of the radius, a phase, and two finger counts.</summary>
        public float CrownAmp, CrownPhase;
        public int CrownA, CrownB;
    }

    // ---- inputs, set by the effect ----

    /// <summary>Depth of the pool along each edge (<see cref="ScreenEdge"/> order), in px, sampled at <see cref="PoolSamples"/> points along it. Zero = no pool there.</summary>
    public readonly float[][] PoolDepth = { new float[PoolSamples], new float[PoolSamples], new float[PoolSamples], new float[PoolSamples] };

    /// <summary>How soft a pool's edge is, as a fraction of its depth.</summary>
    public float PoolSoft = 0.55f;

    public readonly Arm[] Arms = new Arm[24];
    public int ArmCount;
    public readonly Blob[] Blobs = new Blob[24];
    public int BlobCount;

    /// <summary>Lumpiness of the outline: how far (px) the noise pushes the surface, its frequency (1/px), and the finer crinkle's frequency (1/px).</summary>
    public float WarpAmp = 14f, WarpFreq = 1f / 230f, CrinkleFreq = 1f / 64f;

    /// <summary>Seconds, for the noise to drift on. Pass time since the effect began: large clocks lose precision.</summary>
    public float Time;

    /// <summary>How far the eaten glass reaches beyond the liquid, 1 = the usual. The effect grows it over time and on a beat, so the damage runs ahead of the liquid.</summary>
    public float EtchGain = 1f;

    /// <summary>0..1: the liquid flaring from within (a beat, a surge). The material reads it; it does not change the shape.</summary>
    public float Throb;

    // ---- results ----

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public float Cell { get; private set; }
    public Vector2 Size { get; private set; }

    /// <summary>Potential at each grid vertex (row-major, Cols+1 by Rows+1); zero where nothing reaches.</summary>
    public float[] F = Array.Empty<float>();

    /// <summary>Slow noise in about [-1, 1] that lumps the outline; the material reads its slope as the surface's undulation.</summary>
    public float[] Lump = Array.Empty<float>();

    /// <summary>Fine noise in about [-1, 1]: mottled density, the etched edge's ragged outline.</summary>
    public float[] Crinkle = Array.Empty<float>();

    /// <summary>The potential blurred outward: the broad reach of the liquid's damage (fumes, etching) beyond its visible edge. Valid over <see cref="HaloTiles"/>.</summary>
    public float[] Halo = Array.Empty<float>();

    /// <summary>How far the halo reaches, in grid cells (a box blur applied twice).</summary>
    public int HaloCells = 4;

    /// <summary>Tiles (TileCells x TileCells cells) something reaches: the only ones worth looking at.</summary>
    public int[] ActiveTiles = Array.Empty<int>();
    public int ActiveTileCount { get; private set; }

    /// <summary>The active tiles and the ring round them: where the halo has a value.</summary>
    public int[] HaloTiles = Array.Empty<int>();
    public int HaloTileCount { get; private set; }
    private float[] _blur = Array.Empty<float>();
    public int TileCols { get; private set; }
    public int TileRows { get; private set; }

    private bool[] _tileOn = Array.Empty<bool>();

    // Scatter bookkeeping. Stamps are monotonic counters, so none of these arrays is ever cleared.
    private int[] _tileStamp = Array.Empty<int>(), _vStamp = Array.Empty<int>(), _wStamp = Array.Empty<int>();
    private float[] _qx = Array.Empty<float>(), _qy = Array.Empty<float>();
    private int _prim, _solve;
    private readonly List<int> _primTiles = new(64);

    private struct ArmCache { public float Inv1, Inv2, MaxR; }
    private readonly ArmCache[] _armCache = new ArmCache[24];

    /// <summary>Screen resolution scale the grid was cut for; a change rebuilds the grid.</summary>
    private float _cellFor;

    // ---- solve ----

    /// <summary>Evaluates the potential everywhere something reaches. Call after the inputs are set, once a frame.</summary>
    public void Solve(Vector2 size, float cell)
    {
        EnsureGrid(size, cell);
        Array.Clear(_blur); Array.Clear(F); Array.Clear(Lump); Array.Clear(Crinkle); Array.Clear(_tileOn);
        ActiveTileCount = 0; HaloTileCount = 0;
        _solve++;

        PrepareArms();

        // Each bump visits only the tiles near it, and adds itself to the vertices there. Pools go first: where they have made the liquid deep, the rest need not look.
        for (int e = 0; e < 4; e++)
        {
            BeginPrim();
            MarkPool(e);
            if (_primTiles.Count > 0) Scatter(Kind.Pool, e);
        }
        for (int i = 0; i < ArmCount; i++)
        {
            if (Arms[i].Gain <= 0.001f) continue;
            BeginPrim();
            MarkArm(i);
            Scatter(Kind.Arm, i);
        }
        for (int i = 0; i < BlobCount; i++)
        {
            if (Blobs[i].Gain <= 0.001f || Blobs[i].Radius <= 0.5f) continue;
            BeginPrim();
            MarkBlob(i);
            Scatter(Kind.Blob, i);
        }

        BlurHalo();
        FillNoiseWhereSeen();
    }

    private enum Kind { Pool, Arm, Blob }

    private void BeginPrim() { _prim++; _primTiles.Clear(); }

    /// <summary>Adds every tile the rectangle touches to this bump's list, and to the active tiles if no bump has asked for it yet.</summary>
    private void MarkRect(float x0, float y0, float x1, float y1)
    {
        float span = TileCells * Cell;
        int tx0 = Math.Max(0, (int)MathF.Floor(x0 / span)), tx1 = Math.Min(TileCols - 1, (int)MathF.Floor(x1 / span));
        int ty0 = Math.Max(0, (int)MathF.Floor(y0 / span)), ty1 = Math.Min(TileRows - 1, (int)MathF.Floor(y1 / span));
        for (int ty = ty0; ty <= ty1; ty++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                int k = ty * TileCols + tx;
                if (_tileStamp[k] == _prim) continue;
                _tileStamp[k] = _prim;
                _primTiles.Add(k);
                if (!_tileOn[k]) { _tileOn[k] = true; ActiveTiles[ActiveTileCount++] = k; }
            }
    }

    private void Scatter(Kind kind, int index)
    {
        int stride = Cols + 1;
        for (int t = 0; t < _primTiles.Count; t++)
        {
            int tc = _primTiles[t] % TileCols, tr = _primTiles[t] / TileCols;
            int c0 = tc * TileCells, r0 = tr * TileCells;
            int c1 = Math.Min(Cols, c0 + TileCells), r1 = Math.Min(Rows, r0 + TileCells);
            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    int i = r * stride + c;
                    if (_vStamp[i] == _prim) continue;           // a vertex on a tile's edge belongs to both tiles; it takes this bump once
                    _vStamp[i] = _prim;
                    if (kind != Kind.Pool && F[i] >= Saturated) continue;

                    if (_wStamp[i] != _solve) Warp(i, c * Cell, r * Cell);
                    float qx = _qx[i], qy = _qy[i];
                    F[i] += kind switch
                    {
                        Kind.Pool => PoolAt(index, qx, qy),
                        Kind.Arm => ArmAt(index, qx, qy),
                        _ => BlobAt(index, qx, qy),
                    };
                }
            }
        }
    }

    /// <summary>The lumpy warp of the outline at one vertex, and the two noises the material shades with; computed once per vertex per frame, only where something reaches.</summary>
    private void Warp(int i, float x, float y)
    {
        float ox = Time * 0.13f, oy = Time * 0.09f;
        float wf = WarpFreq;
        // Two layers drifting against each other, so the outline evolves instead of sliding.
        float n1 = 0.62f * Noise.Value(x * wf + ox, y * wf - oy)
                 + 0.38f * Noise.Value(x * wf * 1.9f - ox * 1.4f + 7.7f, y * wf * 1.9f + oy * 1.2f);
        float n2 = Noise.Value(x * wf + 41.3f - oy, y * wf + 12.9f + ox);
        Lump[i] = n1;
        Crinkle[i] = Noise.Value(x * CrinkleFreq + 3.1f - ox * 2f, y * CrinkleFreq + oy * 2f);
        _qx[i] = x + WarpAmp * n1; _qy[i] = y + WarpAmp * n2;
        _wStamp[i] = _solve;
    }

    /// <summary>The halo's reach can pass beyond the potential's: those vertices have had no bump to warp them, but the etched edge still wants its noise.</summary>
    private void FillNoiseWhereSeen()
    {
        int stride = Cols + 1;
        float ox = Time * 0.13f, oy = Time * 0.09f, wf = WarpFreq;
        for (int t = 0; t < HaloTileCount; t++)
        {
            int tc = HaloTiles[t] % TileCols, tr = HaloTiles[t] / TileCols;
            int c0 = tc * TileCells, r0 = tr * TileCells;
            int c1 = Math.Min(Cols, c0 + TileCells), r1 = Math.Min(Rows, r0 + TileCells);
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    int i = r * stride + c;
                    if (Halo[i] < 0.004f || _wStamp[i] == _solve) continue;
                    float x = c * Cell, y = r * Cell;
                    Lump[i] = 0.62f * Noise.Value(x * wf + ox, y * wf - oy) + 0.38f * Noise.Value(x * wf * 1.9f - ox * 1.4f + 7.7f, y * wf * 1.9f + oy * 1.2f);
                    Crinkle[i] = Noise.Value(x * CrinkleFreq + 3.1f - ox * 2f, y * CrinkleFreq + oy * 2f);
                }
        }
    }

    /// <summary>The potential at any point, for placing things on the mass (bubbles, threads). Exactly what the grid would hold there (up to the cap where it is already deep).</summary>
    public float Sample(Vector2 p)
    {
        float ox = Time * 0.13f, oy = Time * 0.09f, wf = WarpFreq;
        float n1 = 0.62f * Noise.Value(p.X * wf + ox, p.Y * wf - oy) + 0.38f * Noise.Value(p.X * wf * 1.9f - ox * 1.4f + 7.7f, p.Y * wf * 1.9f + oy * 1.2f);
        float n2 = Noise.Value(p.X * wf + 41.3f - oy, p.Y * wf + 12.9f + ox);
        float qx = p.X + WarpAmp * n1, qy = p.Y + WarpAmp * n2, f = 0f;
        for (int e = 0; e < 4; e++) f += PoolAt(e, qx, qy);
        for (int i = 0; i < ArmCount; i++) if (Arms[i].Gain > 0.001f) f += ArmAt(i, qx, qy);
        for (int i = 0; i < BlobCount; i++) if (Blobs[i].Gain > 0.001f && Blobs[i].Radius > 0.5f) f += BlobAt(i, qx, qy);
        return f;
    }

    /// <summary>Outward direction at a point (unit, pointing away from the liquid), and the potential there. Zero vector where the potential is flat.</summary>
    public Vector2 Outward(Vector2 p, out float potential)
    {
        const float e = 5f;
        potential = Sample(p);
        float gx = Sample(new Vector2(p.X + e, p.Y)) - Sample(new Vector2(p.X - e, p.Y));
        float gy = Sample(new Vector2(p.X, p.Y + e)) - Sample(new Vector2(p.X, p.Y - e));
        var g = new Vector2(-gx, -gy);
        float l = g.Length();
        return l > 1e-5f ? g / l : Vector2.Zero;
    }

    /// <summary>A box blur of the potential, twice (close to a Gaussian), over the active tiles and the ring round them.</summary>
    private void BlurHalo()
    {
        // The ring of tiles round the active ones.
        Span<bool> ring = _tileOn.Length <= 4096 ? stackalloc bool[_tileOn.Length] : new bool[_tileOn.Length];
        for (int t = 0; t < ActiveTileCount; t++)
        {
            int tc = ActiveTiles[t] % TileCols, tr = ActiveTiles[t] / TileCols;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = tc + dx, y = tr + dy;
                    if (x < 0 || y < 0 || x >= TileCols || y >= TileRows) continue;
                    int k = y * TileCols + x;
                    if (ring[k]) continue;
                    ring[k] = true;
                    HaloTiles[HaloTileCount++] = k;
                }
        }

        int stride = Cols + 1, rad = Math.Max(1, HaloCells);
        float norm = 1f / (2 * rad + 1);
        for (int pass = 0; pass < 2; pass++)
        {
            float[] src = pass == 0 ? F : _blur, dst = pass == 0 ? _blur : Halo;
            if (pass == 1) Array.Clear(dst);
            for (int t = 0; t < HaloTileCount; t++)
            {
                int tc = HaloTiles[t] % TileCols, tr = HaloTiles[t] / TileCols;
                int c0 = tc * TileCells, r0 = tr * TileCells;
                int c1 = Math.Min(Cols, c0 + TileCells - 1), r1 = Math.Min(Rows, r0 + TileCells - 1);
                for (int r = r0; r <= r1; r++)
                    for (int c = c0; c <= c1; c++)
                    {
                        float sum = 0f;
                        if (pass == 0) { int a = Math.Max(0, c - rad), b = Math.Min(Cols, c + rad); for (int k = a; k <= b; k++) sum += src[r * stride + k]; }
                        else           { int a = Math.Max(0, r - rad), b = Math.Min(Rows, r + rad); for (int k = a; k <= b; k++) sum += src[k * stride + c]; }
                        dst[r * stride + c] = sum * norm;
                    }
            }
        }
    }

    /// <summary>The grid's potential at any point, interpolated: what the material draws, at no cost beyond four reads. Use it for tracking the edge every frame; <see cref="Sample"/> for exactness.</summary>
    public float Bilinear(Vector2 p)
    {
        if (F.Length == 0) return 0f;
        float gx = Math.Clamp(p.X / Cell, 0f, Cols - 0.001f), gy = Math.Clamp(p.Y / Cell, 0f, Rows - 0.001f);
        int c = (int)gx, r = (int)gy;
        float fx = gx - c, fy = gy - r;
        int i = r * (Cols + 1) + c;
        float a = F[i] + (F[i + 1] - F[i]) * fx;
        float b = F[i + Cols + 1] + (F[i + Cols + 2] - F[i + Cols + 1]) * fx;
        return a + (b - a) * fy;
    }

    private void EnsureGrid(Vector2 size, float cell)
    {
        cell = MathF.Max(3f, cell);
        if (size == Size && cell == _cellFor && F.Length > 0) return;

        Size = size; _cellFor = cell;
        Cols = Math.Clamp((int)MathF.Ceiling(size.X / cell), 8, 400);
        Rows = Math.Clamp((int)MathF.Ceiling(size.Y / cell), 8, 300);
        Cell = MathF.Max(size.X / Cols, size.Y / Rows);       // square cells; the last row/column may spill a little past the screen
        Cols = Math.Clamp((int)MathF.Ceiling(size.X / Cell), 8, 400);
        Rows = Math.Clamp((int)MathF.Ceiling(size.Y / Cell), 8, 300);

        int nv = (Cols + 1) * (Rows + 1);
        F = new float[nv]; Lump = new float[nv]; Crinkle = new float[nv]; Halo = new float[nv]; _blur = new float[nv];
        _qx = new float[nv]; _qy = new float[nv]; _vStamp = new int[nv]; _wStamp = new int[nv];
        TileCols = (Cols + TileCells - 1) / TileCells;
        TileRows = (Rows + TileCells - 1) / TileCells;
        _tileOn = new bool[TileCols * TileRows];
        _tileStamp = new int[TileCols * TileRows];
        ActiveTiles = new int[TileCols * TileRows];
        HaloTiles = new int[TileCols * TileRows];
        _prim = 0; _solve = 0;
    }

    // ---- what reaches where ----

    private void PrepareArms()
    {
        for (int i = 0; i < ArmCount; i++)
        {
            ref readonly Arm a = ref Arms[i];
            ref ArmCache c = ref _armCache[i];
            c.Inv1 = 1f / MathF.Max(1e-3f, Vector2.DistanceSquared(a.Root, a.Mid));
            c.Inv2 = 1f / MathF.Max(1e-3f, Vector2.DistanceSquared(a.Mid, a.Head));
            c.MaxR = MathF.Max(a.RootRadius, MathF.Max(a.ShaftRadius, a.HeadRadius)) * (1f + a.WaveAmp);
        }
    }

    private void MarkPool(int e)
    {
        float warp = WarpAmp * 1.35f + Cell * 2f;
        float[] d = PoolDepth[e];
        float len = (e % 2 == 0) ? Size.X : Size.Y;
        // Sample by sample, so a deep lobe marks its own stretch of the edge and not the whole of it.
        for (int k = 0; k < PoolSamples - 1; k++)
        {
            float depth = MathF.Max(d[k], d[k + 1]);
            if (depth <= 1f) continue;
            float reach = depth * (1f + PoolSoft * 0.5f * Support) + warp;
            float a = k / (float)(PoolSamples - 1) * len - warp, b = (k + 1) / (float)(PoolSamples - 1) * len + warp;
            switch ((ScreenEdge)e)
            {
                case ScreenEdge.Top:    MarkRect(a, -1f, b, reach); break;
                case ScreenEdge.Bottom: MarkRect(a, Size.Y - reach, b, Size.Y + 1f); break;
                case ScreenEdge.Left:   MarkRect(-1f, a, reach, b); break;
                default:                MarkRect(Size.X - reach, a, Size.X + 1f, b); break;
            }
        }
    }

    /// <summary>The tiles along the arm itself, each as wide as the arm is there.</summary>
    private void MarkArm(int i)
    {
        ref readonly Arm a = ref Arms[i];
        float warp = WarpAmp * 1.35f + Cell * 2f, step = TileCells * Cell * 0.5f;
        float len = Vector2.Distance(a.Root, a.Mid) + Vector2.Distance(a.Mid, a.Head);
        int n = Math.Max(2, (int)(len / step) + 1);
        for (int k = 0; k <= n; k++)
        {
            float t = k / (float)n;
            Vector2 p = t < 0.5f ? Vector2.Lerp(a.Root, a.Mid, t * 2f) : Vector2.Lerp(a.Mid, a.Head, (t - 0.5f) * 2f);
            float reach = KernelReach * Support * ArmRadius(in a, t) + warp;
            MarkRect(p.X - reach, p.Y - reach, p.X + reach, p.Y + reach);
        }
    }

    /// <summary>The tiles along the capsule, each as wide as the blob is there, crown included.</summary>
    private void MarkBlob(int i)
    {
        ref readonly Blob b = ref Blobs[i];
        float warp = WarpAmp * 1.35f + Cell * 2f, step = TileCells * Cell * 0.5f;
        float len = Vector2.Distance(b.Center, b.Tail);
        int n = Math.Max(1, (int)(len / step));
        for (int k = 0; k <= n; k++)
        {
            float t = k / (float)n;
            Vector2 p = Vector2.Lerp(b.Center, b.Tail, t);
            float reach = KernelReach * Support * b.Radius * MathF.Max(1f, 1f - b.TailTaper * t) * (1f + MathF.Max(0f, b.CrownAmp) * 1.15f) + warp;
            MarkRect(p.X - reach, p.Y - reach, p.X + reach, p.Y + reach);
        }
    }

    // ---- the bumps ----

    private float PoolAt(int e, float qx, float qy)
    {
        float w = Size.X, h = Size.Y;
        return e switch
        {
            0 => Pool(PoolDepth[0], qy, qx / w),
            1 => Pool(PoolDepth[1], w - qx, qy / h),
            2 => Pool(PoolDepth[2], h - qy, qx / w),
            _ => Pool(PoolDepth[3], qx, qy / h),
        };
    }

    private float Pool(float[] depth, float dist, float u)
    {
        float s = Math.Clamp(u, 0f, 1f) * (PoolSamples - 1);
        int i = (int)s; if (i >= PoolSamples - 1) i = PoolSamples - 2;
        float d = depth[i] + (depth[i + 1] - depth[i]) * (s - i);
        if (d <= 0.5f) return 0f;
        float soft = MathF.Max(6f, PoolSoft * d);
        float x = (d - dist) / soft + 0.5f;                 // 0.5 exactly at the pool's edge
        if (x <= 0f) return 0f;
        float p = x >= 1f ? 1f : x * x * (3f - 2f * x);
        // A film, not a switch: a pool only a few px deep is faint, so as it rises it fades in rather than appearing at the edge of the screen all at once.
        return p * DrawHelpers.Smooth(d / FilmDepth);
    }

    private float ArmAt(int index, float x, float y)
    {
        ref readonly Arm a = ref Arms[index];
        ref readonly ArmCache c = ref _armCache[index];

        // Distance to the two segments; t runs 0..1 along the whole arm.
        float t, d2;
        {
            Vector2 ab = a.Mid - a.Root; float px = x - a.Root.X, py = y - a.Root.Y;
            float u = Math.Clamp((px * ab.X + py * ab.Y) * c.Inv1, 0f, 1f);
            float dx = px - ab.X * u, dy = py - ab.Y * u;
            t = u * 0.5f; d2 = dx * dx + dy * dy;
        }
        {
            Vector2 ab = a.Head - a.Mid; float px = x - a.Mid.X, py = y - a.Mid.Y;
            float u = Math.Clamp((px * ab.X + py * ab.Y) * c.Inv2, 0f, 1f);
            float dx = px - ab.X * u, dy = py - ab.Y * u;
            float dd = dx * dx + dy * dy;
            if (dd < d2) { d2 = dd; t = 0.5f + u * 0.5f; }
        }

        // Reject on the arm's radius at this point without its travelling lumps (the sine is the expensive part), then add them.
        float baseR = ArmRadius(in a, t, lumps: false);
        float bound = KernelReach * baseR * (1f + a.WaveAmp);
        if (d2 >= bound * bound * SupportSq) return 0f;
        float r = baseR * (1f + a.WaveAmp * MathF.Sin(MathF.Tau * a.WaveCycles * t - a.WavePhase));

        float reach = KernelReach * MathF.Max(1f, r);
        float q2 = d2 / (reach * reach);
        if (q2 >= 1f) return 0f;
        float k = 1f - q2;
        return a.Gain * k * k * k;
    }

    /// <summary>The boundary radius of an arm at t (0 root to 1 head): thick where it leaves the edge, a shaft, then the head's bulb, with its lumps travelling along.</summary>
    private static float ArmRadius(in Arm a, float t, bool lumps = true)
    {
        float u = 1f - t;
        float root = u * u * u;                                                           // (1 - t)^3: thick where it leaves the edge
        float b = DrawHelpers.Smooth((t - 0.60f) / 0.40f);
        float bulb = b * b;                                                               // swells toward the head
        float r = a.ShaftRadius + (a.RootRadius - a.ShaftRadius) * root + (a.HeadRadius - a.ShaftRadius) * bulb;
        return lumps ? r * (1f + a.WaveAmp * MathF.Sin(MathF.Tau * a.WaveCycles * t - a.WavePhase)) : r;
    }

    private float BlobAt(int index, float x, float y)
    {
        ref readonly Blob b = ref Blobs[index];
        float t = 0f, d2;
        float px = x - b.Center.X, py = y - b.Center.Y;
        Vector2 ab = b.Tail - b.Center;
        float l2 = ab.LengthSquared();
        if (l2 > 1e-3f)
        {
            t = Math.Clamp((px * ab.X + py * ab.Y) / l2, 0f, 1f);
            float dx = px - ab.X * t, dy = py - ab.Y * t;
            d2 = dx * dx + dy * dy;
        }
        else d2 = px * px + py * py;

        float r = b.Radius * (1f - b.TailTaper * t);

        // Nothing here unless the point is within the furthest the crown's fingers can reach: decided before any trigonometry.
        float most = KernelReach * MathF.Max(1f, r * (1f + MathF.Max(0f, b.CrownAmp) * 1.15f));
        if (d2 >= most * most * SupportSq) return 0f;

        if (b.CrownAmp > 0.004f)
        {
            // Fingers: sharpened cosines, so a splat has points and gaps rather than a wavy outline.
            float th = MathF.Atan2(py, px);
            float fa = 0.5f + 0.5f * MathF.Cos(b.CrownA * th + b.CrownPhase); fa *= fa * fa;
            float fb = 0.5f + 0.5f * MathF.Cos(b.CrownB * th - b.CrownPhase * 1.7f + 1.3f); fb *= fb;
            r *= 1f + b.CrownAmp * (fa + 0.55f * fb - 0.40f);
        }

        float reach = KernelReach * MathF.Max(1f, r);
        float q2 = d2 / (reach * reach);
        if (q2 >= 1f) return 0f;
        float k = 1f - q2;
        return b.Gain * k * k * k;
    }

    /// <summary>The boundary radius of a bump of kernel radius <paramref name="kernelRadius"/> (a lone blob's visible radius), for sizing things to the mass.</summary>
    public static float BoundaryRadius(float kernelRadius) => kernelRadius * KernelAtIso;
}
