using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>Collects triangles managed-side and hands them to ImGui in one go: Clear, add, Flush when Full and at the end. Not thread-safe.</summary>
internal sealed class MeshBuilder
{
    /// <summary>Flush point. Well under MeshDraw.MaxMeshVerts so one primitive can always finish.</summary>
    private const int FlushAt = 48000;

    private Vector2[] _pos = new Vector2[4096];
    private uint[]    _col = new uint[4096];
    private ushort[]  _idx = new ushort[16384];
    private int _vn, _in;

    public bool Full => _vn >= FlushAt;

    public void Clear() { _vn = 0; _in = 0; }

    private void Room(int verts, int idx)
    {
        if (_vn + verts > _pos.Length)
        {
            int n = Math.Max(_pos.Length * 2, _vn + verts);
            Array.Resize(ref _pos, n); Array.Resize(ref _col, n);
        }
        if (_in + idx > _idx.Length) Array.Resize(ref _idx, Math.Max(_idx.Length * 2, _in + idx));
    }

    /// <summary>Makes room for this many more vertices and indices, so the *Reserved calls below, which do no capacity test of their own</summary>
    public void Reserve(int verts, int idx) => Room(verts, idx);

    /// <summary>Appends the first count entries of two parallel arrays as vertices and returns the index of the first: one block copy instead of a call per vertex.</summary>
    public int AddBlockReserved(Vector2[] pos, uint[] col, int count)
    {
        int first = _vn;
        Array.Copy(pos, 0, _pos, first, count);
        Array.Copy(col, 0, _col, first, count);
        _vn = first + count;
        return first;
    }

    public int VertexReserved(Vector2 p, uint color)
    {
        _pos[_vn] = p; _col[_vn] = color;
        return _vn++;
    }

    /// <summary>One triangle. No capacity test: Reserve first.</summary>
    public void TriReserved(int a, int b, int c)
    {
        _idx[_in] = (ushort)a; _idx[_in + 1] = (ushort)b; _idx[_in + 2] = (ushort)c;
        _in += 3;
    }

    /// <summary>The quad a b / c d as the two triangles (a, b, d) and (a, d, c). No capacity test: Reserve first.</summary>
    public void QuadReserved(int a, int b, int c, int d)
    {
        ushort[] idx = _idx; int i = _in;
        idx[i] = (ushort)a; idx[i + 1] = (ushort)b; idx[i + 2] = (ushort)d;
        idx[i + 3] = (ushort)a; idx[i + 4] = (ushort)d; idx[i + 5] = (ushort)c;
        _in = i + 6;
    }

    public void Flush(ImDrawListPtr dl, Vector2 uv)
    {
        MeshDraw.Mesh(dl, _pos, _col, _idx, _vn, _in, uv);
        Clear();
    }

    /// <summary>Per-point offset direction of a polyline: normals scaled by 1/cos(half turn) at bends (clamped) so strips keep their width.</summary>
    public static void MiterNormals(ReadOnlySpan<Vector2> pts, Span<Vector2> normals)
    {
        int n = pts.Length;
        Vector2 last = default;
        bool have = false;
        for (int k = 0; k < n; k++)
        {
            Vector2 d0 = k > 0 ? Dir(pts[k] - pts[k - 1]) : default;
            Vector2 d1 = k + 1 < n ? Dir(pts[k + 1] - pts[k]) : default;
            Vector2 t = d0 + d1;
            float tl = t.Length();
            Vector2 tan;
            if (tl > 1e-4f) tan = t / tl;
            else if (d1 != default) tan = d1;
            else if (d0 != default) tan = d0;
            else { normals[k] = have ? last : default; continue; }

            float miter = 1f;
            if (d0 != default && d1 != default)
            {
                float c = Vector2.Dot(tan, d0);
                miter = c > 0.4f ? 1f / c : 2.5f;
            }
            last = new Vector2(-tan.Y, tan.X) * miter;
            normals[k] = last;
            have = true;
        }
        // A degenerate start has no direction yet; give it the first real one.
        if (have)
        {
            int f = 0;
            while (f < n && normals[f] == default) f++;
            for (int k = 0; k < f && k < n; k++) normals[k] = normals[f];
        }
    }

    /// <summary>Soft-edged strip: a solid core (per-point half-width) fading to transparent over feather px; color is the core colour with alpha.</summary>
    public void Ribbon(ReadOnlySpan<Vector2> pts, ReadOnlySpan<Vector2> normals, ReadOnlySpan<float> halfWidth, uint color, float feather)
    {
        int n = pts.Length;
        if (n < 2 || normals.Length < n || halfWidth.Length < n || (color >> 24) == 0) return;

        Room(n * 4, (n - 1) * 18);
        uint edge = color & 0x00FFFFFFu;
        int first = _vn, v = _vn;
        Vector2[] P = _pos; uint[] C = _col;
        for (int k = 0; k < n; k++)
        {
            Vector2 p = pts[k], nm = normals[k];
            float hw = MathF.Max(0f, halfWidth[k]);
            P[v] = p - nm * (hw + feather); C[v] = edge;  v++;
            P[v] = p - nm * hw;             C[v] = color; v++;
            P[v] = p + nm * hw;             C[v] = color; v++;
            P[v] = p + nm * (hw + feather); C[v] = edge;  v++;
        }
        _vn = v;

        ushort[] I = _idx; int w = _in;
        for (int k = 0; k + 1 < n; k++)
        {
            int a = first + k * 4, b = a + 4;
            for (int q = 0; q < 3; q++)
            {
                I[w++] = (ushort)(a + q); I[w++] = (ushort)(a + q + 1); I[w++] = (ushort)(b + q + 1);
                I[w++] = (ushort)(a + q); I[w++] = (ushort)(b + q + 1); I[w++] = (ushort)(b + q);
            }
        }
        _in = w;
    }

    /// <summary>Cheaper strip: three vertices per point, alpha falling linearly to px either side; for thin lines and soft underlays.</summary>
    public void Tent(ReadOnlySpan<Vector2> pts, ReadOnlySpan<Vector2> normals, ReadOnlySpan<float> reach, uint color)
    {
        int n = pts.Length;
        if (n < 2 || normals.Length < n || reach.Length < n || (color >> 24) == 0) return;

        Room(n * 3, (n - 1) * 12);
        uint edge = color & 0x00FFFFFFu;
        int first = _vn, v = _vn;
        Vector2[] P = _pos; uint[] C = _col;
        for (int k = 0; k < n; k++)
        {
            Vector2 p = pts[k];
            Vector2 off = normals[k] * MathF.Max(0f, reach[k]);
            P[v] = p - off; C[v] = edge;  v++;
            P[v] = p;       C[v] = color; v++;
            P[v] = p + off; C[v] = edge;  v++;
        }
        _vn = v;

        ushort[] I = _idx; int w = _in;
        for (int k = 0; k + 1 < n; k++)
        {
            int a = first + k * 3, b = a + 3;
            I[w++] = (ushort)a;       I[w++] = (ushort)(a + 1); I[w++] = (ushort)(b + 1);
            I[w++] = (ushort)a;       I[w++] = (ushort)(b + 1); I[w++] = (ushort)b;
            I[w++] = (ushort)(a + 1); I[w++] = (ushort)(a + 2); I[w++] = (ushort)(b + 2);
            I[w++] = (ushort)(a + 1); I[w++] = (ushort)(b + 2); I[w++] = (ushort)(b + 1);
        }
        _in = w;
    }

    /// <summary>Ribbon with a colour (and so an alpha) per point, for light that varies along a strand: pulses, dashes, fades.</summary>
    public void Ribbon(ReadOnlySpan<Vector2> pts, ReadOnlySpan<Vector2> normals, ReadOnlySpan<float> halfWidth, ReadOnlySpan<uint> colors, float feather)
    {
        int n = pts.Length;
        if (n < 2 || normals.Length < n || halfWidth.Length < n || colors.Length < n) return;

        Room(n * 4, (n - 1) * 18);
        int first = _vn, v = _vn;
        Vector2[] P = _pos; uint[] C = _col;
        for (int k = 0; k < n; k++)
        {
            Vector2 p = pts[k], nm = normals[k];
            float hw = MathF.Max(0f, halfWidth[k]);
            uint color = colors[k];
            uint edge = color & 0x00FFFFFFu;
            P[v] = p - nm * (hw + feather); C[v] = edge;  v++;
            P[v] = p - nm * hw;             C[v] = color; v++;
            P[v] = p + nm * hw;             C[v] = color; v++;
            P[v] = p + nm * (hw + feather); C[v] = edge;  v++;
        }
        _vn = v;

        ushort[] I = _idx; int w = _in;
        for (int k = 0; k + 1 < n; k++)
        {
            // A fully clear span draws nothing: dashes cost no fill.
            if (((colors[k] | colors[k + 1]) >> 24) == 0) continue;
            int a = first + k * 4, b = a + 4;
            for (int q = 0; q < 3; q++)
            {
                I[w++] = (ushort)(a + q); I[w++] = (ushort)(a + q + 1); I[w++] = (ushort)(b + q + 1);
                I[w++] = (ushort)(a + q); I[w++] = (ushort)(b + q + 1); I[w++] = (ushort)(b + q);
            }
        }
        _in = w;
    }

    /// <summary>Tent with a colour and a reach per point: a soft underlay whose strength follows the strand's own light.</summary>
    public void Tent(ReadOnlySpan<Vector2> pts, ReadOnlySpan<Vector2> normals, ReadOnlySpan<float> reach, ReadOnlySpan<uint> colors)
    {
        int n = pts.Length;
        if (n < 2 || normals.Length < n || reach.Length < n || colors.Length < n) return;

        Room(n * 3, (n - 1) * 12);
        int first = _vn, v = _vn;
        Vector2[] P = _pos; uint[] C = _col;
        for (int k = 0; k < n; k++)
        {
            Vector2 p = pts[k];
            Vector2 off = normals[k] * MathF.Max(0f, reach[k]);
            uint color = colors[k];
            uint edge = color & 0x00FFFFFFu;
            P[v] = p - off; C[v] = edge;  v++;
            P[v] = p;       C[v] = color; v++;
            P[v] = p + off; C[v] = edge;  v++;
        }
        _vn = v;

        ushort[] I = _idx; int w = _in;
        for (int k = 0; k + 1 < n; k++)
        {
            if (((colors[k] | colors[k + 1]) >> 24) == 0) continue;
            int a = first + k * 3, b = a + 3;
            I[w++] = (ushort)a;       I[w++] = (ushort)(a + 1); I[w++] = (ushort)(b + 1);
            I[w++] = (ushort)a;       I[w++] = (ushort)(b + 1); I[w++] = (ushort)b;
            I[w++] = (ushort)(a + 1); I[w++] = (ushort)(a + 2); I[w++] = (ushort)(b + 2);
            I[w++] = (ushort)(a + 1); I[w++] = (ushort)(b + 2); I[w++] = (ushort)(b + 1);
        }
        _in = w;
    }

    /// <summary>A single tapering spike from root (soft cross-section rootReach px either side) to a point tip: four vertices.</summary>
    public void Sliver(Vector2 root, Vector2 tip, float rootReach, uint rootColor, uint tipColor)
    {
        Vector2 d = tip - root;
        float l2 = d.LengthSquared();
        if (l2 < 1e-6f || ((rootColor | tipColor) >> 24) == 0) return;
        Vector2 off = new Vector2(-d.Y, d.X) * (rootReach / MathF.Sqrt(l2));

        Room(4, 6);
        uint edge = rootColor & 0x00FFFFFFu;
        int a = _vn;
        _pos[a] = root - off; _col[a] = edge;
        _pos[a + 1] = root;   _col[a + 1] = rootColor;
        _pos[a + 2] = root + off; _col[a + 2] = edge;
        _pos[a + 3] = tip;    _col[a + 3] = tipColor;
        _vn = a + 4;

        _idx[_in++] = (ushort)a;       _idx[_in++] = (ushort)(a + 1); _idx[_in++] = (ushort)(a + 3);
        _idx[_in++] = (ushort)(a + 1); _idx[_in++] = (ushort)(a + 2); _idx[_in++] = (ushort)(a + 3);
    }

    private static Vector2 Dir(Vector2 d)
    {
        float l2 = d.LengthSquared();
        return l2 > 1e-8f ? d / MathF.Sqrt(l2) : default;
    }
}

/// <summary>Mesh drawing: after PrimReserve write exactly that many vertices and indices and advance the cursors; read VtxCurrentIdx after it.</summary>
internal static unsafe partial class MeshDraw
{
    public const int MaxVerts = 512;
    public static readonly Vector2[] P = new Vector2[MaxVerts];
    public static readonly uint[]    C = new uint[MaxVerts];

    private static Vector2 _whiteUv;
    private static float   _whiteStamp = float.NaN;

    /// <summary>UV of the font atlas' white pixel, which flat-coloured vertices use; refreshed once per frame (the atlas can be rebuilt).</summary>
    public static Vector2 WhiteUv(float frameTime)
    {
        if (frameTime != _whiteStamp)
        {
            _whiteUv = ImGui.GetFontTexUvWhitePixel();
            // Nudge half a texel in so bilinear sampling only sees the 2x2 white block (a fixed offset breaks above 1024 texels).
            _whiteUv += ImGui.GetIO().Fonts.TexUvScale * 0.5f;
            _whiteStamp = frameTime;
        }
        return _whiteUv;
    }

    private static void Begin(ImDrawListPtr dl, int idxCount, int vtxCount,
                              out ImDrawVert* v, out ushort* ix, out uint b)
    {
        dl.PrimReserve(idxCount, vtxCount);
        ImDrawList* h = dl.Handle;
        v = h->VtxWritePtr;
        ix = h->IdxWritePtr;
        b = h->VtxCurrentIdx;
    }

    private static void End(ImDrawListPtr dl, ImDrawVert* v, ushort* ix, int vtxCount, int idxCount, uint b)
    {
        ImDrawList* h = dl.Handle;
        h->VtxWritePtr = v + vtxCount;
        h->IdxWritePtr = ix + idxCount;
        h->VtxCurrentIdx = b + (uint)vtxCount;
        if (_rec != null) Record(v, ix, vtxCount, idxCount, b);
    }

    // Recording and replay: A finished mesh can be copied out of the draw list as it is written

    /// <summary>One recorded run of triangles.</summary>
    internal sealed class MeshCache
    {
        public ImDrawVert[] V = new ImDrawVert[8192];
        public ushort[] I = new ushort[32768];
        public int Vn, In;
        public bool Valid;
    }

    private static MeshCache? _rec;

    /// <summary>Starts copying everything drawn from now on into cache, replacing what it held.</summary>
    public static void BeginRecord(MeshCache cache)
    {
        _rec = cache;
        cache.Vn = 0; cache.In = 0; cache.Valid = true;
    }

    /// <summary>Stops recording. False when nothing was drawn, or it did not fit 16-bit indices (the cache is then not valid for replay).</summary>
    public static bool EndRecord()
    {
        MeshCache? c = _rec;
        _rec = null;
        if (c == null) return false;
        if (c.Vn == 0 || c.In == 0) c.Valid = false;
        return c.Valid;
    }

    private static void Record(ImDrawVert* v, ushort* ix, int vtxCount, int idxCount, uint b)
    {
        MeshCache c = _rec!;
        if (!c.Valid) return;
        if (c.Vn + vtxCount > 65000) { c.Valid = false; return; }
        if (c.Vn + vtxCount > c.V.Length) Array.Resize(ref c.V, Math.Max(c.V.Length * 2, c.Vn + vtxCount));
        if (c.In + idxCount > c.I.Length) Array.Resize(ref c.I, Math.Max(c.I.Length * 2, c.In + idxCount));

        fixed (ImDrawVert* dv = &c.V[c.Vn])
            Buffer.MemoryCopy(v, dv, (long)vtxCount * sizeof(ImDrawVert), (long)vtxCount * sizeof(ImDrawVert));
        fixed (ushort* di = &c.I[c.In])
            AddDelta(ix, di, idxCount, unchecked((ushort)(c.Vn - (int)b)));  // index - b = local to this batch; + Vn = local to the recording
        c.Vn += vtxCount; c.In += idxCount;
    }

    /// <summary>Writes a recording back into the draw list. Indices are re-based on wherever the draw list is now.</summary>
    public static void Replay(ImDrawListPtr dl, MeshCache c)
    {
        if (!c.Valid || c.Vn == 0 || c.In == 0) return;
        Begin(dl, c.In, c.Vn, out ImDrawVert* v, out ushort* ix, out uint b);
        fixed (ImDrawVert* sv = c.V)
            Buffer.MemoryCopy(sv, v, (long)c.Vn * sizeof(ImDrawVert), (long)c.Vn * sizeof(ImDrawVert));
        fixed (ushort* si = c.I)
            AddDelta(si, ix, c.In, unchecked((ushort)b));
        h_End(dl, v, ix, c.Vn, c.In, b);
    }

    // End without recording (a replay must not record itself if a recording is open).
    private static void h_End(ImDrawListPtr dl, ImDrawVert* v, ushort* ix, int vtxCount, int idxCount, uint b)
    {
        ImDrawList* h = dl.Handle;
        h->VtxWritePtr = v + vtxCount;
        h->IdxWritePtr = ix + idxCount;
        h->VtxCurrentIdx = b + (uint)vtxCount;
    }

    /// <summary>dst[k] = src[k] + delta (16-bit wrap), a vector at a time.</summary>
    private static void AddDelta(ushort* src, ushort* dst, int n, ushort delta)
    {
        int k = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int w = Vector<ushort>.Count;
            var d = new Vector<ushort>(delta);
            for (; k <= n - w; k += w)
                Unsafe.WriteUnaligned(dst + k, Unsafe.ReadUnaligned<Vector<ushort>>(src + k) + d);
        }
        for (; k < n; k++) dst[k] = unchecked((ushort)(src[k] + delta));
    }

    /// <summary>Draws the (cols+1) x (rows+1) vertex grid currently in P/C (row-major) as 2*cols*rows triangles.</summary>
    public static void Grid(ImDrawListPtr dl, int cols, int rows, Vector2 uv)
    {
        int stride = cols + 1;
        int vtx = stride * (rows + 1);
        if (vtx > MaxVerts || cols < 1 || rows < 1) return;

        int idxCount = cols * rows * 6;
        Begin(dl, idxCount, vtx, out ImDrawVert* v, out ushort* ix, out uint b);

        fixed (Vector2* p = P)
        fixed (uint* c = C)
        {
            for (int i = 0; i < vtx; i++)
            {
                v[i].Pos = p[i];
                v[i].Uv = uv;
                v[i].Col = c[i];
            }
        }

        ushort* w = ix;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                ushort i0 = unchecked((ushort)(b + (uint)(r * stride + c)));
                ushort i1 = unchecked((ushort)(i0 + 1));
                ushort i2 = unchecked((ushort)(i0 + stride));
                ushort i3 = unchecked((ushort)(i2 + 1));
                w[0] = i0; w[1] = i1; w[2] = i3;
                w[3] = i0; w[4] = i3; w[5] = i2;
                w += 6;
            }
        }

        End(dl, v, ix, vtx, idxCount, b);
    }

    /// <summary>Soft radial blob: a centre plus one ring per ringRadius (innermost first; end transparent for no edge). irregular wobbles slices</summary>
    public static void Radial(ImDrawListPtr dl, Vector2 center, Vector2 uv, uint centerCol,
                              int segs, ReadOnlySpan<float> ringRadius, ReadOnlySpan<uint> ringColor,
                              float rotation, float squashY, int seed, float irregular)
    {
        int rings = ringRadius.Length;
        int vtx = 1 + rings * segs;
        if (rings < 1 || segs < 3 || vtx > MaxVerts) return;

        int idxCount = segs * 3 + (rings - 1) * segs * 6;
        Begin(dl, idxCount, vtx, out ImDrawVert* v, out ushort* ix, out uint b);

        v[0].Pos = center; v[0].Uv = uv; v[0].Col = centerCol;
        int n = 1;
        for (int r = 0; r < rings; r++)
        {
            for (int j = 0; j < segs; j++)
            {
                float wob = 1f + irregular * (DrawHelpers.Hash01(seed + j * 7919) * 2f - 1f) * 0.5f;
                float ang = rotation + MathF.Tau * j / segs;
                float rad = ringRadius[r] * wob;
                v[n].Pos = new Vector2(center.X + MathF.Cos(ang) * rad, center.Y + MathF.Sin(ang) * rad * squashY);
                v[n].Uv = uv;
                v[n].Col = ringColor[r];
                n++;
            }
        }

        ushort* w = ix;
        // center fan into ring 0
        for (int j = 0; j < segs; j++)
        {
            w[0] = unchecked((ushort)b);
            w[1] = unchecked((ushort)(b + 1u + (uint)j));
            w[2] = unchecked((ushort)(b + 1u + (uint)((j + 1) % segs)));
            w += 3;
        }
        // ring k -> ring k+1 quads
        for (int r = 0; r < rings - 1; r++)
        {
            uint a0 = b + 1u + (uint)(r * segs);
            uint a1 = a0 + (uint)segs;
            for (int j = 0; j < segs; j++)
            {
                int jn = (j + 1) % segs;
                ushort p0 = unchecked((ushort)(a0 + (uint)j));
                ushort p1 = unchecked((ushort)(a0 + (uint)jn));
                ushort p2 = unchecked((ushort)(a1 + (uint)j));
                ushort p3 = unchecked((ushort)(a1 + (uint)jn));
                w[0] = p0; w[1] = p1; w[2] = p3;
                w[3] = p0; w[4] = p3; w[5] = p2;
                w += 6;
            }
        }

        End(dl, v, ix, vtx, idxCount, b);
    }

    /// <summary>One quad with a color per corner (a-b-c-d in winding order).</summary>
    public static void Quad(ImDrawListPtr dl, Vector2 uv,
                            Vector2 a, uint ca, Vector2 b, uint cb, Vector2 c, uint cc, Vector2 d, uint cd)
    {
        Begin(dl, 6, 4, out ImDrawVert* v, out ushort* ix, out uint i);
        v[0].Pos = a; v[0].Uv = uv; v[0].Col = ca;
        v[1].Pos = b; v[1].Uv = uv; v[1].Col = cb;
        v[2].Pos = c; v[2].Uv = uv; v[2].Col = cc;
        v[3].Pos = d; v[3].Uv = uv; v[3].Col = cd;

        ushort i0 = unchecked((ushort)i), i1 = unchecked((ushort)(i + 1)),
               i2 = unchecked((ushort)(i + 2)), i3 = unchecked((ushort)(i + 3));
        ix[0] = i0; ix[1] = i1; ix[2] = i2;
        ix[3] = i0; ix[4] = i2; ix[5] = i3;
        End(dl, v, ix, 4, 6, i);
    }

    /// <summary>One mesh must fit in a single draw command, whose indices are 16-bit.</summary>
    public const int MaxMeshVerts = 60000;

    private static int[] _sparseMap   = new int[4096];
    private static int[] _sparseCells = new int[4096];

    /// <summary>Draws only the visible cells of a vertex grid (all-transparent cells and untouched vertices are skipped).</summary>
    public static void SparseGrid(ImDrawListPtr dl, int cols, int rows, Vector2[] pos, uint[] col, Vector2 uv)
    {
        int stride = cols + 1;
        int nv = stride * (rows + 1);
        if (cols < 1 || rows < 1 || nv > MaxMeshVerts || pos.Length < nv || col.Length < nv) return;

        if (_sparseMap.Length < nv) _sparseMap = new int[nv];
        if (_sparseCells.Length < cols * rows) _sparseCells = new int[cols * rows];
        int[] map = _sparseMap, list = _sparseCells;
        Array.Clear(map, 0, nv);  // 0 = unused, later the 1-based new index

        // Pass 1: find the visible cells and mark (and count) the corners they touch.
        int cells = 0, used = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int i = r * stride + c;
                if (((col[i] | col[i + 1] | col[i + stride] | col[i + stride + 1]) >> 24) == 0) continue;
                list[cells++] = (i << 1) | ((r + c) & 1);
                if (map[i] == 0)              { map[i] = 1;              used++; }
                if (map[i + 1] == 0)          { map[i + 1] = 1;          used++; }
                if (map[i + stride] == 0)     { map[i + stride] = 1;     used++; }
                if (map[i + stride + 1] == 0) { map[i + stride + 1] = 1; used++; }
            }
        }
        if (cells == 0) return;

        Begin(dl, cells * 6, used, out ImDrawVert* v, out ushort* ix, out uint b);

        // Pass 2: number the used vertices in order and write them.
        fixed (Vector2* pp = pos)
        fixed (uint* cp = col)
        {
            int n = 0;
            for (int i = 0; i < nv; i++)
            {
                if (map[i] == 0) continue;
                map[i] = ++n;
                v->Pos = pp[i]; v->Uv = uv; v->Col = cp[i];
                v++;
            }
        }
        v -= used;

        // Pass 3: two triangles per visible cell.
        ushort* w = ix;
        for (int k = 0; k < cells; k++)
        {
            int packed = list[k];
            int i = packed >> 1;
            ushort i0 = unchecked((ushort)(b + (uint)(map[i] - 1)));
            ushort i1 = unchecked((ushort)(b + (uint)(map[i + 1] - 1)));
            ushort i2 = unchecked((ushort)(b + (uint)(map[i + stride] - 1)));
            ushort i3 = unchecked((ushort)(b + (uint)(map[i + stride + 1] - 1)));
            if ((packed & 1) == 0) { w[0] = i0; w[1] = i1; w[2] = i3; w[3] = i0; w[4] = i3; w[5] = i2; }
            else                   { w[0] = i0; w[1] = i1; w[2] = i2; w[3] = i1; w[4] = i3; w[5] = i2; }
            w += 6;
        }

        End(dl, v, ix, used, cells * 6, b);
    }

    /// <summary>A closed triangle fan: a centre vertex and a ring of rim vertices with their own colours (stars, flares, alternating radii).</summary>
    public static void Fan(ImDrawListPtr dl, Vector2 uv, Vector2 center, uint centerCol,
                           ReadOnlySpan<Vector2> rim, ReadOnlySpan<uint> rimCol)
    {
        int n = rim.Length;
        if (n < 3 || rimCol.Length < n || n + 1 > MaxVerts) return;

        Begin(dl, n * 3, n + 1, out ImDrawVert* v, out ushort* ix, out uint b);

        v[0].Pos = center; v[0].Uv = uv; v[0].Col = centerCol;
        for (int i = 0; i < n; i++)
        {
            v[i + 1].Pos = rim[i]; v[i + 1].Uv = uv; v[i + 1].Col = rimCol[i];
            ix[i * 3]     = unchecked((ushort)b);
            ix[i * 3 + 1] = unchecked((ushort)(b + 1u + (uint)i));
            ix[i * 3 + 2] = unchecked((ushort)(b + 1u + (uint)(i + 1 == n ? 0 : i + 1)));
        }

        End(dl, v, ix, n + 1, n * 3, b);
    }

    /// <summary>Draws a caller-built indexed triangle list (see MeshBuilder) with one reserve; keep it under MaxMeshVerts.</summary>
    public static void Mesh(ImDrawListPtr dl, Vector2[] pos, uint[] col, ushort[] idx,
                            int vtxCount, int idxCount, Vector2 uv)
    {
        if (vtxCount < 3 || idxCount < 3 || vtxCount > MaxMeshVerts
            || pos.Length < vtxCount || col.Length < vtxCount || idx.Length < idxCount) return;

        Begin(dl, idxCount, vtxCount, out ImDrawVert* v, out ushort* ix, out uint b);

        fixed (Vector2* pp = pos)
        fixed (uint* cp = col)
        fixed (ushort* ip = idx)
        {
            for (int i = 0; i < vtxCount; i++)
            {
                v[i].Pos = pp[i]; v[i].Uv = uv; v[i].Col = cp[i];
            }
            for (int k = 0; k < idxCount; k++)
                ix[k] = unchecked((ushort)(b + ip[k]));
        }

        End(dl, v, ix, vtxCount, idxCount, b);
    }
}
