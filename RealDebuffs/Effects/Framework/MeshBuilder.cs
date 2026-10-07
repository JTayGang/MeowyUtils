using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Collects vertices and triangles on the managed side and hands them to ImGui in one go
/// (MeshDraw.Mesh), so an effect with thousands of little strokes issues a handful of native calls
/// instead of thousands. Not thread-safe; one instance per effect, reused every frame (no
/// per-frame allocation once the buffers have grown to the working size).
///
/// Usage: Clear, add geometry, and whenever <see cref="Full"/> turns true (checked between
/// primitives) call <see cref="Flush"/>; Flush once more at the end. Triangles are drawn in the order
/// they were added, so a layer that must sit under another is simply added first.
///
/// The strip builders take precomputed per-point normals (see <see cref="MiterNormals"/>), so a
/// shape that is built once and drawn every frame pays for its geometry math once, not per frame.
/// </summary>
internal sealed class MeshBuilder
{
    /// <summary>Flush point. Well under MeshDraw.MaxMeshVerts so one primitive can always finish.</summary>
    private const int FlushAt = 48000;

    private Vector2[] _pos = new Vector2[4096];
    private uint[]    _col = new uint[4096];
    private ushort[]  _idx = new ushort[16384];
    private int _vn, _in;
    private Vector2[] _scratch = new Vector2[16];     // normals for the convenience overloads

    public bool Full => _vn >= FlushAt;
    public int VertexCount => _vn;
    public int IndexCount => _in;

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

    public int Vertex(Vector2 p, uint color)
    {
        Room(1, 0);
        _pos[_vn] = p; _col[_vn] = color;
        return _vn++;
    }

    public void Tri(int a, int b, int c)
    {
        Room(0, 3);
        _idx[_in++] = (ushort)a; _idx[_in++] = (ushort)b; _idx[_in++] = (ushort)c;
    }

    public void Flush(ImDrawListPtr dl, Vector2 uv)
    {
        MeshDraw.Mesh(dl, _pos, _col, _idx, _vn, _in, uv);
        Clear();
    }

    /// <summary>
    /// The offset direction for each point of a polyline: the unit normal, scaled up at bends by
    /// 1/cos(half the turn) so a strip keeps its width through a corner (clamped, so a sharp bend
    /// can't spike). Compute once per shape; the strip builders below just multiply by a width.
    /// Degenerate (zero-length) stretches borrow their neighbour's direction.
    /// </summary>
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

    /// <summary>
    /// A soft-edged strip along a polyline: a solid core of the given half-width that fades to
    /// transparent over <paramref name="feather"/> pixels on each side (the same fringe trick the
    /// stroke chains use, so thin lines stay smooth without ImGui's per-line anti-aliasing).
    /// <paramref name="color"/> is the core color including alpha; half-widths are per point so a
    /// crystal arm can taper to a point.
    /// </summary>
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

    /// <summary>Convenience overload that works the normals out itself; prefer the one above for anything drawn every frame.</summary>
    public void Ribbon(ReadOnlySpan<Vector2> pts, ReadOnlySpan<float> halfWidth, uint color, float feather)
    {
        Span<Vector2> nrm = Scratch(pts.Length);
        MiterNormals(pts, nrm);
        Ribbon(pts, nrm, halfWidth, color, feather);
    }

    /// <summary>
    /// A cheaper soft strip: three vertices per point (transparent edge, solid centre, transparent
    /// edge), so the alpha falls off linearly from the centre line to <paramref name="reach"/> pixels
    /// either side. Right for thin lines and soft underlays, where a flat-topped core can't be seen
    /// anyway; costs 3/4 of the vertices and 2/3 of the indices of <see cref="Ribbon"/>.
    /// </summary>
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

    public void Tent(ReadOnlySpan<Vector2> pts, ReadOnlySpan<float> reach, uint color)
    {
        Span<Vector2> nrm = Scratch(pts.Length);
        MiterNormals(pts, nrm);
        Tent(pts, nrm, reach, color);
    }

    /// <summary>
    /// A single tapering spike from <paramref name="root"/> to <paramref name="tip"/>: four vertices,
    /// two triangles. The root is a soft-edged cross-section <paramref name="rootReach"/> pixels either
    /// side; the tip is a point. For twigs, needles and thorns.
    /// </summary>
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

    private Span<Vector2> Scratch(int n)
    {
        if (_scratch.Length < n) _scratch = new Vector2[Math.Max(n, _scratch.Length * 2)];
        return _scratch.AsSpan(0, n);
    }

    private static Vector2 Dir(Vector2 d)
    {
        float l2 = d.LengthSquared();
        return l2 > 1e-8f ? d / MathF.Sqrt(l2) : default;
    }
}
