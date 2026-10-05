using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Cheap 2D gradient noise for CPU-shaded meshes. Range is roughly [-1, 1]. Stateless and
/// allocation-free; there's no permutation table, the lattice gradient comes from an integer hash.
/// </summary>
internal static class FireNoise
{
    public static float Perlin(float x, float y)
    {
        float fx = MathF.Floor(x), fy = MathF.Floor(y);
        int xi = (int)fx, yi = (int)fy;
        float xf = x - fx, yf = y - fy;

        float u = xf * xf * xf * (xf * (xf * 6f - 15f) + 10f);
        float v = yf * yf * yf * (yf * (yf * 6f - 15f) + 10f);

        float n00 = Grad(xi,     yi,     xf,      yf);
        float n10 = Grad(xi + 1, yi,     xf - 1f, yf);
        float n01 = Grad(xi,     yi + 1, xf,      yf - 1f);
        float n11 = Grad(xi + 1, yi + 1, xf - 1f, yf - 1f);

        float nx0 = n00 + u * (n10 - n00);
        float nx1 = n01 + u * (n11 - n01);
        return (nx0 + v * (nx1 - nx0)) * 1.4f;
    }

    // 256x256 lattice of random values in [-1, 1], filled once. Sampling it is four array reads and a
    // few multiplies - no hashing, no branches - which is why the per-vertex flame shading uses this
    // instead of Perlin. Coordinates wrap every 256 units, far beyond anything on screen.
    private const int TableSize = 256;
    private static readonly float[] Table = BuildTable();

    private static float[] BuildTable()
    {
        var t = new float[TableSize * TableSize];
        for (int i = 0; i < t.Length; i++)
            t[i] = DrawHelpers.Hash01(i * 7 + 12345) * 2f - 1f;
        return t;
    }

    /// <summary>Smooth value noise in roughly [-1, 1]. Hot-path replacement for Perlin.</summary>
    public static float Value(float x, float y)
    {
        float fx = MathF.Floor(x), fy = MathF.Floor(y);
        int xi = (int)fx & (TableSize - 1), yi = (int)fy & (TableSize - 1);
        float xf = x - fx, yf = y - fy;
        float u = xf * xf * xf * (xf * (xf * 6f - 15f) + 10f);
        float v = yf * yf * yf * (yf * (yf * 6f - 15f) + 10f);

        int x1 = (xi + 1) & (TableSize - 1), y1 = (yi + 1) & (TableSize - 1);
        float a = Table[(yi << 8) | xi], b = Table[(yi << 8) | x1];
        float c = Table[(y1 << 8) | xi], d = Table[(y1 << 8) | x1];
        float top = a + u * (b - a), bot = c + u * (d - c);
        return (top + v * (bot - top)) * 1.25f;
    }

    private static float Grad(int ix, int iy, float dx, float dy)
    {
        uint h = unchecked((uint)ix * 0x27d4eb2du ^ (uint)iy * 0x165667b1u);
        h ^= h >> 15; h = unchecked(h * 0x85ebca6bu); h ^= h >> 13;
        switch (h & 7u)
        {
            case 0:  return  dx + dy;
            case 1:  return -dx + dy;
            case 2:  return  dx - dy;
            case 3:  return -dx - dy;
            case 4:  return  dx;
            case 5:  return -dx;
            case 6:  return  dy;
            default: return -dy;
        }
    }

    public static float Smooth(float t)
    {
        t = t < 0f ? 0f : (t > 1f ? 1f : t);
        return t * t * (3f - 2f * t);
    }
}

/// <summary>
/// Black-body-flavored fire palette baked into a 256-entry table of opaque packed colors. Index it
/// with a 0..1 "temperature": 0 is dull ember red, 1 is near-white. Build once, index forever - no
/// native calls (ImGui.ColorConvertFloat4ToU32 is a P/Invoke) on the per-vertex path.
///
/// Callers turn these into final vertex colors with DrawHelpers.WithAlpha, which is also where the
/// user's color override is applied, so palette lookups stay override-correct for free.
/// </summary>
internal static class FireColor
{
    private static readonly float[] StopT =
        { 0.00f, 0.16f, 0.36f, 0.56f, 0.76f, 0.91f, 1.00f };

    private static readonly Vector3[] StopC =
    {
        new(0.30f, 0.040f, 0.010f),   // dull ember
        new(0.70f, 0.110f, 0.020f),   // deep red
        new(0.96f, 0.290f, 0.040f),   // red-orange
        new(1.00f, 0.520f, 0.090f),   // orange
        new(1.00f, 0.760f, 0.260f),   // yellow-orange
        new(1.00f, 0.920f, 0.620f),   // pale yellow
        new(1.00f, 0.985f, 0.880f),   // near-white
    };

    private const int N = 256;
    private static readonly uint[] Lut = Build();

    public static uint Pack(float r, float g, float b)
        => 0xFF000000u | (Q(b) << 16) | (Q(g) << 8) | Q(r);

    // Signed conversion: float -> uint is several times slower on x64 and this runs per vertex. NaN lands on 0.
    private static uint Q(float v) => (uint)(int)((v > 0f ? (v < 1f ? v : 1f) : 0f) * 255f + 0.5f);

    /// <summary>Opaque palette color for a 0..1 temperature.</summary>
    public static uint Heat(float t)
    {
        // Written so NaN lands on 0: casting NaN to int gives int.MinValue and would throw below.
        t = t > 0f ? (t < 1f ? t : 1f) : 0f;
        return Lut[(int)(t * (N - 1) + 0.5f)];
    }

    private static uint[] Build()
    {
        var lut = new uint[N];
        for (int i = 0; i < N; i++)
        {
            float t = i / (float)(N - 1);
            int s = 0;
            while (s < StopT.Length - 2 && t > StopT[s + 1]) s++;
            float k = (t - StopT[s]) / (StopT[s + 1] - StopT[s]);
            var c = Vector3.Lerp(StopC[s], StopC[s + 1], Math.Clamp(k, 0f, 1f));
            lut[i] = Pack(c.X, c.Y, c.Z);
        }
        return lut;
    }
}

/// <summary>
/// Vertex-colored mesh drawing: a true gradient instead of stacked flat translucent shapes.
///
/// One PrimReserve per mesh, then vertices and indices are stored straight into the draw list's
/// buffers. PrimWriteVtx / PrimWriteIdx are one native call each (tens of thousands a frame for a
/// chain); this is plain stores. After PrimReserve(idx, vtx) write exactly that many of each and
/// advance the cursors (Begin/End are the only place that touches them). VtxCurrentIdx is read
/// AFTER the reserve because a reserve can start a fresh command.
///
/// Scratch buffers are static; drawing is single-threaded on ImGui's UI thread.
/// </summary>
internal static unsafe class MeshDraw
{
    public const int MaxVerts = 512;
    public static readonly Vector2[] P = new Vector2[MaxVerts];
    public static readonly uint[]    C = new uint[MaxVerts];

    private static Vector2 _whiteUv;
    private static float   _whiteStamp = float.NaN;

    /// <summary>
    /// UV of the font atlas' solid white pixel - what every flat-colored ImGui vertex uses so that
    /// vertex color alone determines the output. Refreshed once per frame (the atlas can be rebuilt).
    /// </summary>
    public static Vector2 WhiteUv(float frameTime)
    {
        if (frameTime != _whiteStamp)
        {
            _whiteUv = ImGui.GetFontTexUvWhitePixel();
            _whiteUv.X += 0.0008f;   // <-- important to prevent transparency issues
            _whiteUv.Y += 0.0008f;   // <-- important to prevent transparency issues
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

    /// <summary>
    /// Draws the (cols+1) x (rows+1) vertex grid currently in P/C (row-major) as 2*cols*rows triangles.
    /// </summary>
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

    /// <summary>
    /// A soft radial blob: a center vertex plus one concentric ring of <paramref name="segs"/>
    /// vertices per entry in <paramref name="ringRadius"/>. Radii and colors per ring are given
    /// innermost-first; make the outermost ring fully transparent for a glow with no visible edge.
    /// <paramref name="irregular"/>
    /// (0..1) wobbles each angular slice's radius by a seed-derived amount, so smoke and haze don't
    /// read as perfect discs. <paramref name="squashY"/> flattens it into an ellipse.
    /// </summary>
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
}
