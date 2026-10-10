using System.Numerics;
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

    /// <summary>A single tapering spike from root (soft cross-section rootReach px either side) to a point tip: four vertices. For twigs, needles, thorns.</summary>
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
