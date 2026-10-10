using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>Mesh drawing: after PrimReserve write exactly that many vertices and indices and advance the cursors; read VtxCurrentIdx after it. UI thread only.</summary>
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

    /// <summary>Soft radial blob: a centre plus one ring per ringRadius (innermost first; end transparent for no edge). irregular wobbles slices; squashY flattens.</summary>
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
        Array.Clear(map, 0, nv);                        // 0 = unused, later the 1-based new index

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
