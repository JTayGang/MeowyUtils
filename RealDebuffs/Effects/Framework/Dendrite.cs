using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>Branching growing lines (frost fern, lightning, roots): built once with arrival times, then Draw(age) shows what has grown.</summary>
internal sealed class Dendrite
{
    /// <summary>Shape and pace of one fern.</summary>
    public readonly record struct FernStyle(
        int StemPieces,        // line pieces the stem is split into (more = smoother curve)
        int Pairs,             // side-branch pairs along the stem
        float BranchAngle,     // radians between stem and branch
        float BranchLength,    // branch length as a fraction of stem length (at its fullest)
        int Twigs,             // twigs per branch
        float TwigLength,      // twig length as a fraction of its branch
        float Curl,            // random bend per stem piece, radians
        float Duration);       // seconds for the stem to reach full length

    private const int MaxChainPoints = 8;

    /// <summary>Number of chains (polylines).</summary>
    public int Count { get; private set; }

    /// <summary>Total line segments across all chains: the number that drives draw cost.</summary>

    // Flat arrays: points (x, y, arrival time, strip normal) and per-chain metadata; normals are computed once when a chain is added.
    private float[] _px = new float[4096], _py = new float[4096], _pt = new float[4096];
    private Vector2[] _nm = new Vector2[4096];
    private int _pn;
    private int[] _start = new int[1024];
    private byte[] _len = new byte[1024], _gen = new byte[1024];
    private float[] _width = new float[1024], _sinP = new float[1024], _cosP = new float[1024];
    private Vector2 _min, _max;

    public void Clear(Vector2 bounds)
    {
        Count = 0;
        _pn = 0;
        _min = new Vector2(-48f, -48f);
        _max = bounds + new Vector2(48f, 48f);
    }

    private static Vector2 Dir(float a) => new(MathF.Cos(a), MathF.Sin(a));

    /// <summary>Adds a polyline that starts growing at <paramref name="t0"/> and extends at <paramref name="speed"/> px/s.</summary>
    public void AddChain(ReadOnlySpan<Vector2> pts, float t0, float speed, float width, byte gen, float phase)
    {
        int n = Math.Min(pts.Length, MaxChainPoints);
        if (n < 2) return;

        // Anything that never comes near the screen would only cost draw calls.
        bool left = true, right = true, above = true, below = true;
        for (int i = 0; i < n; i++)
        {
            left &= pts[i].X < _min.X; right &= pts[i].X > _max.X;
            above &= pts[i].Y < _min.Y; below &= pts[i].Y > _max.Y;
        }
        if (left || right || above || below) return;

        if (Count == _start.Length)
        {
            int c = Count * 2;
            Array.Resize(ref _start, c); Array.Resize(ref _len, c); Array.Resize(ref _gen, c);
            Array.Resize(ref _width, c); Array.Resize(ref _sinP, c); Array.Resize(ref _cosP, c);
        }
        if (_pn + n > _px.Length)
        {
            int c = _px.Length * 2;
            Array.Resize(ref _px, c); Array.Resize(ref _py, c); Array.Resize(ref _pt, c); Array.Resize(ref _nm, c);
        }

        int k = Count++;
        _start[k] = _pn; _len[k] = (byte)n; _gen[k] = gen; _width[k] = width;
        _sinP[k] = MathF.Sin(phase); _cosP[k] = MathF.Cos(phase);
        Span<Vector2> nrm = stackalloc Vector2[MaxChainPoints];
        MeshBuilder.MiterNormals(pts[..n], nrm);
        float t = t0, invSpeed = 1f / MathF.Max(1f, speed);
        for (int i = 0; i < n; i++)
        {
            if (i > 0) t += Vector2.Distance(pts[i], pts[i - 1]) * invSpeed;
            _px[_pn] = pts[i].X; _py[_pn] = pts[i].Y; _pt[_pn] = t; _nm[_pn] = nrm[i];
            _pn++;
        }
    }

    /// <summary>A single short hair at <paramref name="at"/>; cheap texture for the area between the ferns.</summary>
    public void AddNeedle(Vector2 at, float angle, float length, float t0, float width, float phase)
    {
        Span<Vector2> p = stackalloc Vector2[2];
        p[0] = at;
        p[1] = at + Dir(angle) * length;
        AddChain(p, t0, length / 0.25f, width, 3, phase);
    }

    /// <summary>Grows a fern from root heading along angle for length pixels, starting at t0 seconds.</summary>
    public void AddFern(Vector2 root, float angle, float length, float t0, float width, int seed, in FernStyle st)
    {
        int pieces = Math.Clamp(st.StemPieces, 2, MaxChainPoints - 1);
        Span<Vector2> p = stackalloc Vector2[pieces + 1];
        Span<float> tt = stackalloc float[pieces + 1];
        Span<float> ang = stackalloc float[pieces];

        float speed = length / MathF.Max(0.1f, st.Duration);
        float pieceLen = length / pieces;
        p[0] = root;
        tt[0] = t0;
        float a = angle;
        for (int i = 0; i < pieces; i++)
        {
            a += (DrawHelpers.Hash01(seed + i * 3) - 0.5f) * st.Curl;
            ang[i] = a;
            float len = pieceLen * (0.9f + 0.2f * DrawHelpers.Hash01(seed + i * 3 + 1));
            p[i + 1] = p[i] + Dir(a) * len;
            tt[i + 1] = tt[i] + len / speed;
        }
        AddChain(p, t0, speed, width, 0, DrawHelpers.Hash01(seed) * 6.28f);

        // Side branches in pairs along the stem, longest around the lower-middle and tapering to the tip.
        for (int k = 0; k < st.Pairs; k++)
        {
            float s = (k + 0.7f) / (st.Pairs + 0.4f);                 // 0..1 along the stem
            float f = s * pieces;
            int pi = Math.Min(pieces - 1, (int)f);
            float u = f - pi;
            Vector2 pos = p[pi] + (p[pi + 1] - p[pi]) * u;
            float tAttach = tt[pi] + (tt[pi + 1] - tt[pi]) * u;
            float stemAng = ang[pi];

            float profile = MathF.Pow(1f - s, 0.85f) * (0.55f + 0.45f * MathF.Sin(MathF.Min(1f, s * 2.4f) * 1.5708f));
            for (int side = -1; side <= 1; side += 2)
            {
                int bs = seed + 1000 + k * 17 + (side + 1) * 5;
                float blen = length * st.BranchLength * profile * (0.75f + 0.5f * DrawHelpers.Hash01(bs));
                if (blen < 6f) continue;
                float bang = stemAng + side * st.BranchAngle * (0.9f + 0.2f * DrawHelpers.Hash01(bs + 1));
                AddBranch(pos, bang, blen, tAttach + 0.04f, width * 0.62f, side, bs, st, speed * 0.85f);
            }
        }
    }

    private void AddBranch(Vector2 start, float angle, float length, float t0, float width, int side, int seed, in FernStyle st, float speed)
    {
        const int Pieces = 2;
        Span<Vector2> p = stackalloc Vector2[Pieces + 1];
        Span<float> tt = stackalloc float[Pieces + 1];
        float pieceLen = length / Pieces;
        p[0] = start;
        tt[0] = t0;
        float a = angle;
        Span<float> ang = stackalloc float[Pieces];
        for (int i = 0; i < Pieces; i++)
        {
            // Branches curve back toward the direction the stem is growing: the feathery look.
            a -= side * 0.26f;
            ang[i] = a;
            p[i + 1] = p[i] + Dir(a) * pieceLen;
            tt[i + 1] = tt[i] + pieceLen / speed;
        }
        AddChain(p, t0, speed, width, 1, DrawHelpers.Hash01(seed) * 6.28f);

        // Twigs, alternating sides, spread along the branch and shortening toward its tip.
        Span<Vector2> tw = stackalloc Vector2[2];
        for (int k = 0; k < st.Twigs; k++)
        {
            float s = (k + 0.5f) / st.Twigs;
            float f = s * Pieces;
            int pi = Math.Min(Pieces - 1, (int)f);
            float u = f - pi;
            Vector2 tp = p[pi] + (p[pi + 1] - p[pi]) * u;
            float tlen = length * st.TwigLength * (1f - 0.55f * s) * (0.7f + 0.6f * DrawHelpers.Hash01(seed + 50 + k));
            if (tlen < 3f) continue;
            int ts = seed + 70 + k * 3;
            float tdir = (k & 1) == 0 ? 1f : -1f;
            float tang = ang[pi] + tdir * (0.85f + 0.3f * DrawHelpers.Hash01(ts)) - side * 0.12f;
            tw[0] = tp;
            tw[1] = tp + Dir(tang) * tlen;
            AddChain(tw, tt[pi] + (tt[pi + 1] - tt[pi]) * u + 0.02f, speed * 0.8f, width * 0.6f, 2, DrawHelpers.Hash01(ts + 2) * 6.28f);
        }
    }

    /// <summary>Chain drawing (colours go through WithAlpha, so overrides apply): shifted shadow, wide faint Body, bright core.</summary>
    public readonly record struct DrawStyle(
        Palette Core, uint Shadow, Vector2 ShadowOffset, float ShadowAlpha,
        uint Body, float BodyAlpha, float WidthScale, float Alpha, float Time);

    /// <summary>Static scratch (single-threaded UI draw), so a Dendrite allocates nothing per frame.</summary>
    private static readonly MeshBuilder Mesh = new();

    // Crystal colours for this frame by [generation][shimmer step]: 4 x 17 lookups a frame instead of one per chain.
    /// <summary>Deepest generation that gets a shadow underlay (0 = stems, 1 = branches); 0 saves about a fifth of the geometry.</summary>
    private const int ShadowMaxGen = 1;

    private const int ShimmerSteps = 16;
    private static readonly uint[] CoreLut = new uint[4 * (ShimmerSteps + 1)];

    /// <summary>Draws everything grown by <paramref name="age"/> seconds, batched into a few meshes.</summary>
    public void Draw(ImDrawListPtr dl, Vector2 uv, float age, in DrawStyle st)
    {
        if (st.Alpha <= 0.002f) return;

        MeshBuilder mesh = Mesh;
        mesh.Clear();
        Span<Vector2> buf = stackalloc Vector2[MaxChainPoints];
        Span<Vector2> nrm = stackalloc Vector2[MaxChainPoints];
        Span<float> hw = stackalloc float[MaxChainPoints];
        float px = st.WidthScale;

        // Layers are written bottom to top; MeshBuilder keeps triangle order, so one pass per layer.

        // Layer 1: shadow under stems and branches gives the crystals relief.
        if (st.ShadowAlpha > 0.002f)
        {
            uint shade = DrawHelpers.WithAlpha(st.Shadow, st.ShadowAlpha * st.Alpha);
            for (int c = 0; c < Count; c++)
            {
                if (_gen[c] > ShadowMaxGen) continue;
                int n = Visible(c, age, buf, nrm, out _);
                if (n < 2) continue;
                for (int i = 0; i < n; i++) buf[i] += st.ShadowOffset;
                Taper(hw, n, _width[c] * px * 0.95f + 1.3f * px, 0.55f);
                mesh.Tent(buf[..n], nrm[..n], hw[..n], shade);
                if (mesh.Full) mesh.Flush(dl, uv);
            }
        }

        // Layer 2: the milky body around stems and branches.
        if (st.BodyAlpha > 0.002f)
        {
            uint body0 = DrawHelpers.WithAlpha(st.Body, st.BodyAlpha * st.Alpha);
            uint body1 = DrawHelpers.WithAlpha(st.Body, st.BodyAlpha * 0.8f * st.Alpha);
            for (int c = 0; c < Count; c++)
            {
                if (_gen[c] > 1) continue;
                int n = Visible(c, age, buf, nrm, out _);
                if (n < 2) continue;
                float w = _width[c] * px;
                Taper(hw, n, MathF.Max(1.1f * px, w * 1.5f) + (1.4f + w * 0.8f) * px, 0.45f);
                mesh.Tent(buf[..n], nrm[..n], hw[..n], _gen[c] == 0 ? body0 : body1);
                if (mesh.Full) mesh.Flush(dl, uv);
            }
        }

        // Layer 3: the crystal itself - bright and thin, brightest while it is still growing.
        for (int g = 0; g < 4; g++)
        {
            float genA = g == 0 ? 0.95f : (g == 1 ? 0.85f : (g == 2 ? 0.65f : 0.45f));
            for (int k = 0; k <= ShimmerSteps; k++)
            {
                float bright = 0.6f + 0.4f * k / ShimmerSteps;
                CoreLut[g * (ShimmerSteps + 1) + k] = DrawHelpers.WithAlpha(st.Core.Sample(0.55f + 0.45f * bright), genA * st.Alpha);
            }
        }
        // sin(t*1.3 + phase) from the chain's stored sin/cos: two multiplies instead of a sin per chain.
        float sinT = MathF.Sin(st.Time * 1.3f), cosT = MathF.Cos(st.Time * 1.3f);

        for (int c = 0; c < Count; c++)
        {
            int n = Visible(c, age, buf, nrm, out bool growing);
            if (n < 2) continue;

            int step = ShimmerSteps;                                     // growing chains are at full brightness
            if (!growing)
            {
                float shimmer = 0.80f + 0.20f * (sinT * _cosP[c] + cosT * _sinP[c]);
                step = Math.Clamp((int)((shimmer - 0.6f) * (ShimmerSteps / 0.4f) + 0.5f), 0, ShimmerSteps);
            }
            int gen = _gen[c];
            uint col = CoreLut[gen * (ShimmerSteps + 1) + step];
            float half = MathF.Max(0.5f, _width[c] * px * 0.5f);

            if (n == 2 && gen >= 2)
            {
                // A twig or needle: one tapering spike, half the vertices of a strip.
                uint tip = (col & 0x00FFFFFFu) | (((col >> 24) * 3u / 5u) << 24);
                mesh.Sliver(buf[0], buf[1], half + 0.55f, col, tip);
            }
            else if (gen == 0)
            {
                Taper(hw, n, half, 0.5f);
                mesh.Ribbon(buf[..n], nrm[..n], hw[..n], col, 0.8f);     // the stem is thick enough to need a flat core
            }
            else
            {
                Taper(hw, n, half + 0.55f, 0.55f);
                mesh.Tent(buf[..n], nrm[..n], hw[..n], col);
            }
            if (mesh.Full) mesh.Flush(dl, uv);
        }

        mesh.Flush(dl, uv);
    }

    /// <summary>Half-widths easing from <paramref name="root"/> at the first point to root * <paramref name="tipFraction"/> at the last.</summary>
    private static void Taper(Span<float> hw, int n, float root, float tipFraction)
    {
        float inv = n > 1 ? 1f / (n - 1) : 0f;
        for (int i = 0; i < n; i++) hw[i] = root * (1f - (1f - tipFraction) * (i * inv));
    }

    /// <summary>Copies the grown part of chain c (positions and strip normals) into the buffers; returns the point count.</summary>
    private int Visible(int c, float age, Span<Vector2> buf, Span<Vector2> nrm, out bool growing)
    {
        growing = false;
        int off = _start[c], n = _len[c];
        if (!(age > _pt[off])) return 0;                  // also rejects a NaN start time

        int k = 0;
        while (k < n && age >= _pt[off + k])
        {
            buf[k] = new Vector2(_px[off + k], _py[off + k]);
            nrm[k] = _nm[off + k];
            k++;
        }
        if (k < n)
        {
            float t0 = _pt[off + k - 1], t1 = _pt[off + k];
            float u = t1 > t0 ? (age - t0) / (t1 - t0) : 1f;
            var a = buf[k - 1];
            buf[k] = a + (new Vector2(_px[off + k], _py[off + k]) - a) * u;
            nrm[k] = _nm[off + k];
            growing = true;
            return k + 1;
        }
        return k;
    }
}
