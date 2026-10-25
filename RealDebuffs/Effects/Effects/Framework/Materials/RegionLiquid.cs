using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>What a liquid mass leaves round it and how its edge shines, apart from the liquid itself (a <see cref="LiquidSpec"/>). Authored in display colour.</summary>
/// <param name="EtchDark">The glass next to the mass, eaten dark.</param>
/// <param name="EtchFringe">The faint stain further out, where the liquid's fumes have only just reached.</param>
/// <param name="RimGlow">Light that spills off the mass's edge (acid glows; blood does not: alpha 0).</param>
/// <param name="RimGlowAlpha">How strong that spill is at its brightest.</param>
/// <param name="EtchAlpha">How opaque the eaten glass is at its darkest.</param>
public readonly record struct LiquidMassStyle(Vector3 EtchDark, Vector3 EtchFringe, Vector3 RimGlow, float RimGlowAlpha, float EtchAlpha);

/// <summary>Ready-made looks for a liquid mass. A new viscous effect (blood, sludge) adds a LiquidPreset, an entry here, and one line in MaterialRegistry.</summary>
public static class LiquidMasses
{
    /// <summary>Acid: the glass round it eaten to a near-black olive char, a sickly stain beyond, and a lime light spilling off the edge.</summary>
    public static readonly LiquidMassStyle Acid = new(
        EtchDark: new(0.018f, 0.020f, 0.007f), EtchFringe: new(0.130f, 0.165f, 0.022f),
        RimGlow: new(0.62f, 1.00f, 0.16f), RimGlowAlpha: 0.20f, EtchAlpha: 0.78f);
}

/// <summary>
/// A mass of viscous liquid lying on the glass, drawn from a <see cref="LiquidField"/> (passed as <see cref="RegionPrimitive.State"/>; any other State draws nothing): the glass
/// eaten dark round it, then the liquid itself as one lit, translucent surface with a crisp meniscus. The surface is a height field shaded per vertex through a
/// <see cref="LiquidMatcap"/>, so it is lit exactly as a drop of the same liquid is: a steep wet rim that falls flat toward the middle, undulations that
/// slide light over it, a caustic on the shadow side. Its outline is cut exactly at the field's iso-line (marching squares), so the edge is as sharp as a drop's however
/// coarse the grid; narrow highlights are laid analytically over the vertices that find the key light.
/// </summary>
public sealed class RegionLiquid : IRegionMaterial
{
    private readonly string _name;
    private readonly LiquidSpec _spec;
    private readonly LiquidMatcap _liquid;
    private readonly LiquidMassStyle _style;

    public RegionLiquid(string name, in LiquidSpec liquid, in LiquidMassStyle style)
    {
        _name = name; _spec = liquid; _liquid = new LiquidMatcap(in liquid); _style = style;
    }

    public string Name => _name;

    // ---- look ----
    private const float RimWidth = 5.6f;             // px at 1080p over which the meniscus falls flat
    private const float LumpHeight = 30f;            // px at 1080p: how tall the broad undulations are, which is what tilts the shading
    private const float RippleHeight = 6.5f;         // px at 1080p: and the fine ripples riding on them, which is what streaks the highlights
    private const float MaxTilt = 0.985f;
    private const float NoRim = 9999f;               // a rim distance meaning "nowhere near an edge"
    private const float EtchIso = 0.13f;
    private const float HiddenBelow = 1.0f;          // the potential past which the liquid is deep enough to hide the glass beneath it            // where the eaten glass ends, on the etch value
    private const float GlintCutoff = 0.20f;
    private const int   MaxGlints = 64;
    private static readonly float[] RimRows = { 0f, 0.55f, 1.30f, 2.70f };    // distance in from the edge, in rim widths, of each row of the meniscus ribbon

    // ---- per-grid scratch ----
    private int _nv = -1;
    private Vector2[] _pos = Array.Empty<Vector2>();
    private float[] _lx = Array.Empty<float>(), _ly = Array.Empty<float>(), _spc = Array.Empty<float>(), _x = Array.Empty<float>();
    private uint[] _col = Array.Empty<uint>(), _ecol = Array.Empty<uint>(), _xcol = Array.Empty<uint>();
    private int[] _seen = Array.Empty<int>(), _vtx = Array.Empty<int>(), _vtxStamp = Array.Empty<int>();
    private struct XData
    {
        public int Stamp, SmoothStamp, N0, N1;      // N0, N1: the crossings next to this one along the contour (-1: none yet)
        public Vector2 Pos, Out;                    // where the iso-line crosses, and the way out there
        public Vector2 SPos, SOut, TPos, TOut;      // the same, smoothed once and twice along the contour: what the visible edge is drawn from
        public uint CI, C0, C1, C2, C3;             // CI: the middle's colour here, for the fill; C0..C3: the edge, for the ribbon
    }
    private XData[] _xd = Array.Empty<XData>();
    private int _frame, _epoch;
    private readonly List<int> _segs = new();            // the contour pieces found this pass, as pairs of crossing ids: drawn after every fill, so no fill covers them
    private readonly MeshBuilder _mb = new();

    /// <summary>Everything resolved once per Draw that the per-vertex work needs.</summary>
    private struct Look
    {
        public float Px, Lambda, Alpha, Dye, Throb;
        public bool Overridden;
    }

    public void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx)
    {
        if (r.State is not LiquidField f || f.ActiveTileCount == 0) return;
        float a = r.Alpha * ctx.Alpha;
        if (a <= 0.004f) return;

        Prepare(f);
        var look = new Look
        {
            Px = ctx.ScreenScale, Lambda = RimWidth * ctx.ScreenScale, Alpha = MathF.Min(a, 1f),
            Dye = DrawHelpers.ColorOverrideChroma, Overridden = DrawHelpers.ColorOverrideActive, Throb = f.Throb,
        };
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        _frame++;

        ShadeAll(f, in look);

        // Under everything: the faint stain, then the eaten glass (a ragged contour with a fizzing front), then the liquid itself.
        MeshDraw.SparseGrid(dl, f.Cols, f.Rows, _pos, _ecol, uv);
        Contour(dl, f, in look, uv, body: false);
        Contour(dl, f, in look, uv, body: true);
        DrawGlints(dl, f, in look, uv);
    }

    private void Prepare(LiquidField f)
    {
        int nv = (f.Cols + 1) * (f.Rows + 1);
        if (nv != _nv || _pos.Length != nv || _pos[^1] != new Vector2(f.Cols * f.Cell, f.Rows * f.Cell))
        {
            _nv = nv;
            _pos = new Vector2[nv];
            int stride = f.Cols + 1;
            for (int i = 0; i < nv; i++) _pos[i] = new Vector2((i % stride) * f.Cell, (i / stride) * f.Cell);
            _lx = new float[nv]; _ly = new float[nv]; _spc = new float[nv]; _x = new float[nv];
            _col = new uint[nv]; _ecol = new uint[nv]; _xcol = new uint[nv];
            _seen = new int[nv]; _vtx = new int[nv]; _vtxStamp = new int[nv];
            _xd = new XData[nv * 2];
            _frame = 0; _epoch = 0;
        }
        Array.Clear(_ecol);
        Array.Clear(_spc);
        Array.Clear(_x);
    }

    // ---- pass 1: per-vertex colours, over the halo's tiles ----

    private void ShadeAll(LiquidField f, in Look look)
    {
        int stride = f.Cols + 1;
        float inv = 1f / f.Cell;
        float[] F = f.F, H = f.Halo, lump = f.Lump, crinkle = f.Crinkle;
        float etchGain = f.EtchGain;

        for (int t = 0; t < f.HaloTileCount; t++)
        {
            int tile = f.HaloTiles[t];
            int tc = tile % f.TileCols, tr = tile / f.TileCols;
            int c0 = tc * LiquidField.TileCells, r0 = tr * LiquidField.TileCells;
            int c1 = Math.Min(f.Cols, c0 + LiquidField.TileCells), r1 = Math.Min(f.Rows, r0 + LiquidField.TileCells);
            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    int i = r * stride + c;
                    if (_seen[i] == _frame) continue;
                    _seen[i] = _frame;

                    float f0 = F[i], h = H[i];
                    if (h < 0.004f && f0 < 0.002f) continue;       // nothing here (the arrays were cleared for this frame)

                    float cr0 = crinkle[i];

                    // The etch value: the damage reaches much further in some places than others (the slow undulation), and its edge is ragged (the fine crinkle).
                    float patch = 0.62f + 0.62f * lump[i];
                    float x = h * 1.10f * etchGain * patch + f0 * 0.35f + 0.11f * cr0 * DrawHelpers.Smooth(0.07f, 0.26f, h + f0 * 0.5f);
                    _x[i] = x;

                    // ---- the stain: a faint sickly cast further out than the char ----
                    float hv = h * etchGain + 0.05f * cr0 * DrawHelpers.Smooth(0f, 0.04f, h);
                    float sa = DrawHelpers.Smooth(0.012f, 0.16f, hv) * (1f - DrawHelpers.Smooth(0.35f, 0.7f, f0));
                    if (sa > 0.003f)
                        _ecol[i] = Tint(_style.EtchFringe, look.Alpha * sa * 0.34f, in look);

                    // ---- the eaten glass: darker the nearer the liquid ----
                    if (x > 0.02f)
                    {
                        float dark = DrawHelpers.Smooth(EtchIso, 0.62f, x);
                        Vector3 ec = Vector3.Lerp(_style.EtchFringe * 0.55f, _style.EtchDark, dark);
                        // Gone by the depth at which cells of it are left out (HiddenBelow), so leaving them out changes nothing anyone can see.
                        float hidden = 1f - DrawHelpers.Smooth(0.60f * HiddenBelow, HiddenBelow, f0);
                        _xcol[i] = Tint(ec, look.Alpha * _style.EtchAlpha * (0.62f + 0.38f * dark) * hidden, in look);
                    }

                    // ---- the liquid: flat across the middle (the meniscus is a ribbon along the contour, not these vertices) ----
                    if (f0 < LiquidField.Iso) continue;
                    int cl = c > 0 ? c - 1 : c, cr = c < f.Cols ? c + 1 : c;
                    int rl = r > 0 ? r - 1 : r, rr = r < f.Rows ? r + 1 : r;
                    float kx = inv / (cr - cl), ky = inv / (rr - rl);
                    float lgx = (lump[r * stride + cr] - lump[r * stride + cl]) * kx * LumpHeight + (crinkle[r * stride + cr] - crinkle[r * stride + cl]) * kx * RippleHeight;
                    float lgy = (lump[rr * stride + c] - lump[rl * stride + c]) * ky * LumpHeight + (crinkle[rr * stride + c] - crinkle[rl * stride + c]) * ky * RippleHeight;
                    _lx[i] = lgx; _ly[i] = lgy;
                    float thick = DrawHelpers.Smooth((f0 - LiquidField.Iso) / 0.60f);
                    ShadeSurface(0f, 0f, NoRim, thick, lgx, lgy, cr0, lump[i], look.Throb, in look, out Vector3 rgb, out float al, out float spec);
                    _col[i] = Tint(rgb, al, in look);
                    _spc[i] = spec;
                }
            }
        }
    }

    /// <summary>Slope of a per-vertex field at a vertex (central difference, one-sided at the grid's edge). Only the few vertices a contour crosses between ever need it.</summary>
    private static void Slope(LiquidField f, float[] a, int i, float inv, out float gx, out float gy)
    {
        int stride = f.Cols + 1, c = i % stride, r = i / stride;
        int cl = c > 0 ? c - 1 : c, cr = c < f.Cols ? c + 1 : c, rl = r > 0 ? r - 1 : r, rr = r < f.Rows ? r + 1 : r;
        gx = (a[r * stride + cr] - a[r * stride + cl]) * inv / (cr - cl);
        gy = (a[rr * stride + c] - a[rl * stride + c]) * inv / (rr - rl);
    }

    /// <summary>
    /// The surface at one point. <paramref name="rimDist"/> is the true distance in px to the edge (huge for the middle), which sets how steeply the meniscus leans; <paramref name="thick"/>
    /// is 0 (a film) to 1 (deep), which sets density and opacity. They are separate because only the first is exact where it is measured: the meniscus ribbon knows its distance
    /// exactly, and the middle gets its thickness from the potential, which is smooth, rather than from a division by a noisy gradient.
    /// </summary>
    private void ShadeSurface(float ox, float oy, float rimDist, float thick, float lgx, float lgy, float crinkle, float lump, float throb,
                              in Look look, out Vector3 rgb, out float alpha, out float spec)
    {
        // Outward direction (unit in ox, oy), and how steeply the meniscus leans there: steepest at the edge, flat a few rim-widths in.
        float m = rimDist >= NoRim ? 0f : 0.95f * MathF.Exp(-rimDist / look.Lambda);
        float nx = ox * m, ny = oy * m;

        // Undulations tilt the surface: little at the very edge, fully a couple of rim-widths in.
        float inside = DrawHelpers.Smooth(rimDist / (1.6f * look.Lambda));
        float h = look.Px * (1f + 0.35f * throb);
        nx -= lgx * h * inside; ny -= lgy * h * inside;

        float nl = MathF.Sqrt(nx * nx + ny * ny);
        if (nl > MaxTilt) { nx *= MaxTilt / nl; ny *= MaxTilt / nl; }

        Vector4 lit = _liquid.Sample(nx, ny);
        rgb = new Vector3(lit.X, lit.Y, lit.Z);

        // Density: deeper toward the middle, with a faint mottling. Thin liquid at the edge lets light through and so also lets the scene through.
        float cloud = 0.5f * crinkle + 0.5f * lump;
        rgb *= 1f + 0.22f * cloud * thick - 0.30f * thick;

        // A throb is the liquid flaring from within (the effect's DoT beat): brightest where it is thick.
        if (throb > 0.001f) rgb += ThinTint * (throb * (0.12f + 0.38f * thick));

        if (look.Dye > 0f) rgb = DrawHelpers.BoostSaturation(rgb, look.Dye, DrawHelpers.SoftDye);

        alpha = lit.W * look.Alpha * (0.76f + 0.24f * thick);

        // Where the surface faces the key light's half vector: for the highlight sprites.
        Vector3 nz = new(nx, ny, MathF.Sqrt(MathF.Max(0f, 1f - nx * nx - ny * ny)));
        float nh = MathF.Max(0f, Vector3.Dot(nz, KeyHalf));
        float nh2 = nh * nh, nh4 = nh2 * nh2, nh8 = nh4 * nh4, nh16 = nh8 * nh8;
        spec = nh16 * nh16 * nh16 * (0.30f + 0.70f * inside);          // nh^48
    }

    private static readonly Vector3 KeyHalf = Vector3.Normalize(StudioLighting.Key + Vector3.UnitZ);
    private Vector3 ThinTint => _spec.Thin * 0.9f;

    private static uint Tint(Vector3 c, float alpha, in Look look) =>
        DrawHelpers.Tint(DrawHelpers.Pack(c.X, c.Y, c.Z), alpha, look.Overridden);

    // ---- pass 2: a contour mesh, cut exactly at the iso-line ----

    private static readonly int[] CornerDc = { 0, 1, 1, 0 };
    private static readonly int[] CornerDr = { 0, 0, 1, 1 };

    /// <summary>Marching squares over the active tiles: every cell inside is filled, every cell the iso-line crosses is cut along it. body: the liquid (iso <see cref="LiquidField.Iso"/> on F, with a meniscus ribbon); otherwise the eaten glass (iso <see cref="EtchIso"/> on the etch value, with a fizzing front).</summary>
    private void Contour(ImDrawListPtr dl, LiquidField f, in Look look, Vector2 uv, bool body)
    {
        int stride = f.Cols + 1;
        float[] vals = body ? f.F : _x;
        uint[] cornerCol = body ? _col : _xcol;
        float iso = body ? LiquidField.Iso : EtchIso;
        int[] tiles = body ? f.ActiveTiles : f.HaloTiles;
        int tileCount = body ? f.ActiveTileCount : f.HaloTileCount;

        float inset = (body ? 2.6f : 1.6f) * look.Px;      // the fill stops this far inside the contour; the edge, smoothed, is drawn over the gap
        Span<int> poly = stackalloc int[8];
        Span<bool> polyCross = stackalloc bool[8];
        Span<int> polyX = stackalloc int[8];            // for a crossing: its entry in the crossing cache

        _mb.Clear();
        _segs.Clear();
        _epoch++;
        for (int t = 0; t < tileCount; t++)
        {
            int tc = tiles[t] % f.TileCols, tr = tiles[t] / f.TileCols;
            int c0 = tc * LiquidField.TileCells, r0 = tr * LiquidField.TileCells;
            int c1 = Math.Min(f.Cols - 1, c0 + LiquidField.TileCells - 1), r1 = Math.Min(f.Rows - 1, r0 + LiquidField.TileCells - 1);
            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    int i0 = r * stride + c;
                    int mask = (vals[i0] >= iso ? 1 : 0) | (vals[i0 + 1] >= iso ? 2 : 0) | (vals[i0 + stride + 1] >= iso ? 4 : 0) | (vals[i0 + stride] >= iso ? 8 : 0);
                    if (mask == 0) continue;
                    // The eaten glass deep under the liquid cannot be seen through it: leave it out.
                    if (!body && mask == 15 && f.F[i0] >= HiddenBelow && f.F[i0 + 1] >= HiddenBelow && f.F[i0 + stride] >= HiddenBelow && f.F[i0 + stride + 1] >= HiddenBelow) continue;

                    // A mesh's indices are 16-bit: well before they could wrap, hand what there is to the draw list and start another (the vertex cache dies with it).
                    if (_mb.Full) { _mb.Flush(dl, uv); _epoch++; }

                    if (mask == 15)
                    {
                        int a = Corner(i0, cornerCol), b = Corner(i0 + 1, cornerCol), cc = Corner(i0 + stride + 1, cornerCol), d = Corner(i0 + stride, cornerCol);
                        _mb.Reserve(0, 6);
                        _mb.TriReserved(a, b, cc); _mb.TriReserved(a, cc, d);
                        continue;
                    }

                    // Walk the cell's corners in order, collecting the inside ones and the crossings between inside and outside.
                    int n = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        int kn = (k + 1) & 3;
                        int ik = i0 + CornerDr[k] * stride + CornerDc[k], ikn = i0 + CornerDr[kn] * stride + CornerDc[kn];
                        bool inK = (mask >> k & 1) != 0, inN = (mask >> kn & 1) != 0;
                        if (inK) { poly[n] = Corner(ik, cornerCol); polyCross[n] = false; polyX[n] = -1; n++; }
                        if (inK != inN)
                        {
                            // Canonical order (inside corner first), so the neighbouring cell finds the very same point.
                            int ia = inK ? ik : ikn, ib = inK ? ikn : ik;
                            int id = (ia < ib ? ia : ib) * 2 + (Math.Abs(ia - ib) == 1 ? 0 : 1);
                            ref XData x = ref _xd[id];
                            if (x.Stamp != _epoch) { MakeCrossing(f, ref x, ia, ib, vals, iso, body, in look); x.Stamp = _epoch; x.N0 = x.N1 = -1; }
                            _mb.Reserve(1, 0);
                            poly[n] = _mb.VertexReserved(x.Pos - x.Out * inset, body ? x.CI : x.C0); polyCross[n] = true; polyX[n] = id; n++;
                        }
                    }

                    _mb.Reserve(0, (n - 2) * 3);
                    for (int k = 1; k + 1 < n; k++) _mb.TriReserved(poly[0], poly[k], poly[k + 1]);

                    // The contour: each pair of crossings that are neighbours in the polygon.
                    for (int k = 0; k < n; k++)
                    {
                        int kn = k + 1 == n ? 0 : k + 1;
                        if (!polyCross[k] || !polyCross[kn]) continue;
                        _segs.Add(polyX[k]); _segs.Add(polyX[kn]);
                        Link(polyX[k], polyX[kn]); Link(polyX[kn], polyX[k]);
                    }
                }
            }
        }

        // Every fill is down; now the edge, over all of it, smoothed along the contour so a coarse grid does not show as facets.
        SmoothContour();
        for (int i = 0; i + 1 < _segs.Count; i += 2)
        {
            if (_mb.Full) _mb.Flush(dl, uv);
            if (body) Meniscus(in _xd[_segs[i]], in _xd[_segs[i + 1]], in look);
            else      Front(in _xd[_segs[i]], in _xd[_segs[i + 1]], in look);
        }
        _mb.Flush(dl, uv);
    }

    private void Link(int a, int b)
    {
        ref XData x = ref _xd[a];
        if (x.N0 < 0) x.N0 = b; else if (x.N1 < 0 && x.N0 != b) x.N1 = b;
    }

    /// <summary>
    /// Marching squares cuts the iso-line along grid edges, so on a coarse grid a tight curve is a few straight chords. The crossings form chains (each has a neighbour either side), so
    /// pulling each toward the mean of its neighbours, twice, rounds the chords off. Only the visible edge uses the result: the fill is inset to stay inside it.
    /// </summary>
    private void SmoothContour()
    {
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < _segs.Count; i++)
            {
                ref XData x = ref _xd[_segs[i]];
                if (x.SmoothStamp == _epoch * 4 + pass) continue;
                x.SmoothStamp = _epoch * 4 + pass;
                Vector2 p = pass == 0 ? x.Pos : x.SPos, o = pass == 0 ? x.Out : x.SOut;
                Vector2 outP = p, outO = o;
                if (x.N0 >= 0 && x.N1 >= 0)
                {
                    ref readonly XData a = ref _xd[x.N0]; ref readonly XData b = ref _xd[x.N1];
                    Vector2 pa = pass == 0 ? a.Pos : a.SPos, pb = pass == 0 ? b.Pos : b.SPos;
                    Vector2 oa = pass == 0 ? a.Out : a.SOut, ob = pass == 0 ? b.Out : b.SOut;
                    outP = p * 0.5f + (pa + pb) * 0.25f;
                    Vector2 mo = o * 0.5f + (oa + ob) * 0.25f;
                    float ml = mo.Length();
                    outO = ml > 1e-4f ? mo / ml : o;
                }
                if (pass == 0) { x.SPos = outP; x.SOut = outO; } else { x.TPos = outP; x.TOut = outO; }
            }
        }
    }

    /// <summary>The mesh vertex for a grid vertex inside, made once however many cells share it.</summary>
    private int Corner(int i, uint[] colours)
    {
        if (_vtxStamp[i] == _epoch) return _vtx[i];
        _mb.Reserve(1, 0);
        int v = _mb.VertexReserved(_pos[i], colours[i]);
        _vtx[i] = v; _vtxStamp[i] = _epoch;
        return v;
    }

    /// <summary>Where the iso-line crosses the grid edge from inside corner <paramref name="ia"/> to outside corner <paramref name="ib"/>, the way out there, and what is drawn from it.</summary>
    private void MakeCrossing(LiquidField f, ref XData x, int ia, int ib, float[] vals, float iso, bool body, in Look look)
    {
        float fa = vals[ia], fb = vals[ib];
        float t = Math.Clamp((iso - fa) / (fb - fa), 0f, 1f);
        x.Pos = Vector2.Lerp(_pos[ia], _pos[ib], t);

        // The way out: down the slope of whatever the contour is a level of, found at the two corners and blended.
        float inv = 1f / f.Cell;
        float[] level = body ? f.F : _x;
        Slope(f, level, ia, inv, out float ax, out float ay); Slope(f, level, ib, inv, out float bx, out float by);
        float ux = ax + (bx - ax) * t, uy = ay + (by - ay) * t;
        float gm = MathF.Sqrt(ux * ux + uy * uy);
        x.Out = gm > 1e-6f ? new Vector2(-ux / gm, -uy / gm) : Vector2.UnitY;

        float cr = f.Crinkle[ia] + (f.Crinkle[ib] - f.Crinkle[ia]) * t;
        if (!body)
        {
            // The eaten glass's edge: char at its darkest, and a lime fizz that breaks up with the crinkle (a front, not an outline).
            // Exactly what the fill is at its own edge (where it has not yet darkened), so the fill and the edge strip meet without a seam.
            x.C0 = Tint(_style.EtchFringe * 0.55f, look.Alpha * _style.EtchAlpha * 0.62f, in look);
            float fizz = DrawHelpers.Smooth(-0.55f, 0.35f, cr);
            x.C1 = Tint(_style.RimGlow, look.Alpha * _style.RimGlowAlpha * (0.20f + 1.45f * fizz) * (0.55f + 0.7f * look.Throb), in look);
            return;
        }

        Slope(f, f.Lump, ia, inv, out float lax, out float lay); Slope(f, f.Lump, ib, inv, out float lbx, out float lby);
        Slope(f, f.Crinkle, ia, inv, out float cax, out float cay); Slope(f, f.Crinkle, ib, inv, out float cbx, out float cby);
        float lx = (lax + (lbx - lax) * t) * LumpHeight + (cax + (cbx - cax) * t) * RippleHeight, ly = (lay + (lby - lay) * t) * LumpHeight + (cay + (cby - cay) * t) * RippleHeight;
        float lu = f.Lump[ia] + (f.Lump[ib] - f.Lump[ia]) * t;
        ShadeSurface(0f, 0f, 9999f, 0f, lx, ly, cr, lu, look.Throb, in look, out Vector3 irgb, out float ial, out _);
        x.CI = Tint(irgb, ial, in look);
        for (int k = 0; k < RimRows.Length; k++)
        {
            ShadeSurface(x.Out.X, x.Out.Y, RimRows[k] * look.Lambda, 0.30f * DrawHelpers.Smooth(RimRows[k] / 3.2f), lx, ly, cr, lu, look.Throb, in look, out Vector3 rgb, out float al, out _);
            // The ribbon sits over the middle's own colour, so it is made more opaque than the liquid is, then fades into it.
            float fade = k == RimRows.Length - 1 ? 0f : 1f;
            uint c = Tint(rgb, MathF.Min(1f, al * (k == 0 ? 1.0f : 1.35f)) * fade, in look);
            switch (k) { case 0: x.C0 = c; break; case 1: x.C1 = c; break; case 2: x.C2 = c; break; default: x.C3 = c; break; }
        }
    }

    /// <summary>One piece of the liquid's edge: the meniscus ribbon running inward from the contour, an anti-aliasing fringe outside it, and the light that spills off.</summary>
    private void Meniscus(in XData a, in XData b, in Look look)
    {
        float feather = MathF.Max(0.9f, 1.15f * look.Px);

        // The light spilling off the edge, first so everything else sits over it.
        if (_style.RimGlowAlpha > 0.004f)
        {
            float reach = 5.5f * look.Px;
            uint g = Tint(_style.RimGlow, look.Alpha * _style.RimGlowAlpha * (0.70f + 0.60f * look.Throb), in look);
            uint g0 = g & 0x00FFFFFFu;
            _mb.Reserve(4, 6);
            int p = _mb.VertexReserved(a.TPos, g), q = _mb.VertexReserved(b.TPos, g);
            int s = _mb.VertexReserved(b.TPos + b.TOut * reach, g0), u = _mb.VertexReserved(a.TPos + a.TOut * reach, g0);
            _mb.TriReserved(p, q, s); _mb.TriReserved(p, s, u);
        }

        // The meniscus: rows running inward, each a measured distance from the edge.
        _mb.Reserve(8, 18);
        int ia0 = 0, ib0 = 0, iaPrev = 0, ibPrev = 0;
        for (int k = 0; k < RimRows.Length; k++)
        {
            float d = RimRows[k] * look.Lambda;
            uint ca = k switch { 0 => a.C0, 1 => a.C1, 2 => a.C2, _ => a.C3 };
            uint cb = k switch { 0 => b.C0, 1 => b.C1, 2 => b.C2, _ => b.C3 };
            int ra = _mb.VertexReserved(a.TPos - a.TOut * d, ca), rb = _mb.VertexReserved(b.TPos - b.TOut * d, cb);
            if (k > 0) { _mb.TriReserved(iaPrev, ibPrev, rb); _mb.TriReserved(iaPrev, rb, ra); }
            else { ia0 = ra; ib0 = rb; }
            iaPrev = ra; ibPrev = rb;
        }

        // The edge's own anti-aliasing: the outermost colour fading to nothing over a pixel or so.
        _mb.Reserve(2, 6);
        int so = _mb.VertexReserved(b.TPos + b.TOut * feather, b.C0 & 0x00FFFFFFu), uo = _mb.VertexReserved(a.TPos + a.TOut * feather, a.C0 & 0x00FFFFFFu);
        _mb.TriReserved(ia0, ib0, so); _mb.TriReserved(ia0, so, uo);
    }

    /// <summary>One piece of the eaten glass's edge: a fringe to soften the char, and the lime fizz along the very front.</summary>
    private void Front(in XData a, in XData b, in Look look)
    {
        float feather = MathF.Max(1.4f, 2.4f * look.Px), reach = 4.2f * look.Px;
        // The char reaches a little past the fill's inset, to the smoothed edge, then fades.
        float inset = 1.6f * look.Px;
        _mb.Reserve(8, 12);
        int p = _mb.VertexReserved(a.TPos - a.TOut * inset, a.C0), q = _mb.VertexReserved(b.TPos - b.TOut * inset, b.C0);
        int m = _mb.VertexReserved(b.TPos, b.C0), n = _mb.VertexReserved(a.TPos, a.C0);
        _mb.TriReserved(p, q, m); _mb.TriReserved(p, m, n);
        int s = _mb.VertexReserved(b.TPos + b.TOut * feather, b.C0 & 0x00FFFFFFu), u = _mb.VertexReserved(a.TPos + a.TOut * feather, a.C0 & 0x00FFFFFFu);
        _mb.TriReserved(n, m, s); _mb.TriReserved(n, s, u);

        if ((a.C1 >> 24) == 0 && (b.C1 >> 24) == 0) return;
        // The fizz: brightest a little way out from the char's edge, fading both ways.
        _mb.Reserve(6, 12);
        int f0 = _mb.VertexReserved(a.TPos - a.TOut * (0.8f * look.Px), a.C1 & 0x00FFFFFFu), g0 = _mb.VertexReserved(b.TPos - b.TOut * (0.8f * look.Px), b.C1 & 0x00FFFFFFu);
        int f1 = _mb.VertexReserved(a.TPos + a.TOut * (0.9f * look.Px), a.C1), g1 = _mb.VertexReserved(b.TPos + b.TOut * (0.9f * look.Px), b.C1);
        int f2 = _mb.VertexReserved(a.TPos + a.TOut * reach, a.C1 & 0x00FFFFFFu), g2 = _mb.VertexReserved(b.TPos + b.TOut * reach, b.C1 & 0x00FFFFFFu);
        _mb.TriReserved(f0, g0, g1); _mb.TriReserved(f0, g1, f1);
        _mb.TriReserved(f1, g1, g2); _mb.TriReserved(f1, g2, f2);
    }

    // ---- pass 3: crisp highlights, stretched along the ridge that makes them ----

    private void DrawGlints(ImDrawListPtr dl, LiquidField f, in Look look, Vector2 uv)
    {
        int stride = f.Cols + 1;
        int drawn = 0;
        Span<Vector2> rim = stackalloc Vector2[10];
        Span<uint> rimCol = stackalloc uint[10];

        for (int t = 0; t < f.ActiveTileCount && drawn < MaxGlints; t++)
        {
            int tc = f.ActiveTiles[t] % f.TileCols, tr = f.ActiveTiles[t] / f.TileCols;
            int c0 = Math.Max(1, tc * LiquidField.TileCells), r0 = Math.Max(1, tr * LiquidField.TileCells);
            int c1 = Math.Min(f.Cols - 1, tc * LiquidField.TileCells + LiquidField.TileCells - 1), r1 = Math.Min(f.Rows - 1, tr * LiquidField.TileCells + LiquidField.TileCells - 1);
            for (int r = r0; r <= r1 && drawn < MaxGlints; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    int i = r * stride + c;
                    float s = _spc[i];
                    if (s < GlintCutoff) continue;
                    // A local maximum only, so one highlight is one sprite.
                    if (s < _spc[i - 1] || s < _spc[i + 1] || s < _spc[i - stride] || s < _spc[i + stride]
                        || s < _spc[i - stride - 1] || s < _spc[i - stride + 1] || s < _spc[i + stride - 1] || s < _spc[i + stride + 1]) continue;

                    float k = DrawHelpers.Saturate((s - GlintCutoff) / (1f - GlintCutoff));
                    // Long along the ridge (perpendicular to the slope that tilts the surface), short across it.
                    Vector2 ridge = new(-_ly[i], _lx[i]);
                    float rl = ridge.Length();
                    ridge = rl > 1e-6f ? ridge / rl : Vector2.UnitX;
                    Vector2 across = new(-ridge.Y, ridge.X);
                    float along = look.Px * (4.5f + 7.5f * k), width = look.Px * (1.5f + 1.6f * k);

                    float al = look.Alpha * (0.30f + 0.50f * k);
                    for (int pass = 0; pass < 2; pass++)
                    {
                        float sc = pass == 0 ? 1.9f : 0.85f;
                        for (int j = 0; j < 10; j++)
                        {
                            float ang = MathF.Tau * j / 10f;
                            rim[j] = _pos[i] + ridge * (MathF.Cos(ang) * along * sc) + across * (MathF.Sin(ang) * width * sc);
                            rimCol[j] = DrawHelpers.Tint(KeyWhite, pass == 0 ? 0f : al * 0.55f, look.Overridden);
                        }
                        MeshDraw.Fan(dl, uv, _pos[i], DrawHelpers.Tint(KeyWhite, pass == 0 ? al * 0.45f : al, look.Overridden), rim, rimCol);
                    }
                    drawn++;
                    if (drawn >= MaxGlints) break;
                }
            }
        }
    }

    private static readonly uint KeyWhite = DrawHelpers.Pack(1.00f, 0.97f, 0.88f);
}
