using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Frost-era additions to MeshDraw. Same rule as the rest of the class: reserve once, then plain
/// stores straight into ImGui's buffers (Begin/End), never one native call per vertex or index.
/// </summary>
internal static unsafe partial class MeshDraw
{
    /// <summary>One mesh must fit in a single draw command, whose indices are 16-bit.</summary>
    public const int MaxMeshVerts = 60000;

    private static int[] _sparseMap   = new int[4096];
    private static int[] _sparseCells = new int[4096];

    /// <summary>
    /// Draws a (cols+1) x (rows+1) vertex grid (row-major positions and packed colors, supplied by the
    /// caller) but only the cells that can be seen: a cell whose four corners are all fully transparent
    /// is skipped, and vertices no drawn cell touches are not written. A screen-sized coverage mesh
    /// that is mostly empty (an effect that has only covered the edges so far) costs only what it covers.
    /// Neighbouring cells split along opposite diagonals, so smooth gradients don't pick up a
    /// directional streak.
    /// </summary>
    public static void SparseGrid(ImDrawListPtr dl, int cols, int rows, Vector2[] pos, uint[] col, Vector2 uv)
    {
        int stride = cols + 1;
        int nv = stride * (rows + 1);
        if (cols < 1 || rows < 1 || nv > MaxMeshVerts || pos.Length < nv || col.Length < nv) return;

        if (_sparseMap.Length < nv) _sparseMap = new int[nv];
        if (_sparseCells.Length < cols * rows) _sparseCells = new int[cols * rows];
        int[] map = _sparseMap, list = _sparseCells;
        Array.Clear(map, 0, nv);                        // 0 = unused, later the 1-based new index

        // Pass 1: find the visible cells and mark (and count) the corners they touch. A listed cell
        // packs its top-left vertex index and the checkerboard parity that picks its diagonal.
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

    /// <summary>
    /// A closed triangle fan: one centre vertex and a ring of rim vertices, each with its own color.
    /// Fits any star, flare or irregular blob a plain Radial can't (alternating radii, mixed colors).
    /// </summary>
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

    /// <summary>
    /// Draws an indexed triangle list the caller built (see MeshBuilder). Indices are local to the
    /// vertex arrays. One reserve for the whole thing, so a batch of thousands of triangles costs one
    /// native call. Keep it under MaxMeshVerts; MeshBuilder flushes well before that.
    /// </summary>
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
