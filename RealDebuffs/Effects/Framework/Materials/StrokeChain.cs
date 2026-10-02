using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Iron chain, drawn as what it is: a string of oval rings of round bar, each one a real 3D object
/// with a twist angle, lit by the shared studio rig (StudioLighting), shadowed on the scene behind
/// it, and sorted so every interlock occludes the way a photograph of one does.
///
/// HOW A LINK IS DRAWN
///  - Geometry: a link is a stadium-shaped ring in 3D. Its long axis follows the strand; it is
///    ROLLED about that axis by an angle. Roll 0 is face-on (a fat loop with a see-through hole), 90
///    degrees is edge-on (a solid bar). Anything between is a squashed loop whose hole narrows to a
///    slit. Adjacent links are always a quarter turn apart because that is what interlocking forces,
///    and the strand as a whole slowly twists (a smooth noise along its length, seeded per strand),
///    so no two chains and no two casts look alike.
///  - Surface: the ring is split at its two tips into a near half and a far half and each is a
///    ribbon whose cross-section is a true half-cylinder. Every vertex gets a surface normal from
///    the tube's 3D frame, and a colour from a Matcap (physically-motivated lighting baked into a
///    table), so highlights, a cold rim, and reflections of the surroundings come out of the normal
///    alone. The far half shows the INSIDE of its tube, as it would through the loop's hole.
///  - Depth: halves are drawn back to front by their depth, so an edge-on link passes IN FRONT of
///    its neighbour's near side and BEHIND its far side. That is the single thing that makes a
///    chain read as interlocked rather than as a row of overlapping ovals.
///  - Grounding: ambient occlusion darkens the inside of each tip where its neighbour bears on it
///    and polishes the outside where it rubs; per-link tone and rust patches (anchored to the link,
///    not the screen, so they travel with it); and a soft cast shadow on the scene beneath.
///  - Edges are feathered by a one-pixel transparent fringe because mesh triangles are not
///    anti-aliased.
///
/// STROKE CONTRACT (so any effect can use this material, not only Heavy)
///  - Path: any polyline. Links are placed by arc length, so spacing is even; the chain runs on
///    past either end of the path (an anchor hidden off-screen needs no special handling).
///  - WidthHint: the OUTER LENGTH of one link, in pixels, clamped to a legible range.
///  - Closed: the path is a loop. The link count is rounded to an even number so the alternating
///    pattern and the seam both close, which is how a ring-shaped stroke becomes a chain ring.
///  - Reveal, Brightness, Seed, Phase, ColorOverride: as for every stroke.
///  - Depth: fogs and softens. Agitation: lights up the metal and rattles the links.
///
/// Sheds rust flakes and dust as it moves, and throws sparks, flakes and dust when hit.
/// </summary>
public sealed class StrokeChain : IStrokeMaterial
{
    public string Name => "stroke.chain";
    public string[] NaturalLanguageWords { get; } = { "chain", "chains", "links" };

    // Link size range, as a fraction of the shorter screen side. Too small and the interlock stops
    // being legible; too large and one link swallows the frame.
    private const float MinLinkFrac = 0.030f;   // ~32px at 1080p
    private const float MaxLinkFrac = 0.085f;   // ~92px at 1080p

    private const int MaxLinks = 200;

    // ---- link proportions, as fractions of the outer length L (a standard short-link chain) ----
    // A link is 5 bar-diameters long and 3.4 wide, and consecutive links sit 3 bar-diameters apart,
    // which is the inside length of a link: that overlap is exactly what makes them interlock.
    private const float BarDiameter = 0.200f;
    private const float Pitch       = 0.600f;
    private const float HalfAxis    = 0.400f;   // centreline half-length  (L - d) / 2
    private const float HalfLateral = 0.240f;   // centreline half-width   (3.4d - d) / 2 = cap radius
    private const float CapCentre   = HalfAxis - HalfLateral;

    // ---- surfaces ----
    private static readonly Matcap Iron  = new(SurfacePresets.BlackIron);
    private static readonly Matcap Steel = new(SurfacePresets.WornSteel);
    private static readonly Matcap Aged  = new(SurfacePresets.AgedIron);
    private static readonly Matcap Rust  = new(SurfacePresets.Rust);

    private static readonly Vector3 Fog        = new(0.105f, 0.125f, 0.165f);
    private static readonly uint    ShadowTint = FireColor.Pack(0.015f, 0.016f, 0.022f);

    // Display-space colours of the leading-tip flare.
    private static readonly uint FlareWarm = FireColor.Pack(1.00f, 0.80f, 0.52f);

    // ---- ring tables: one half of a link's centreline, from one tip over the top to the other ----
    private sealed class RingTable
    {
        public int M;                    // samples
        public float[] X = null!, Y = null!;     // centreline, in units of L (Y is for the +side half)
        public float[] NX = null!, NY = null!;   // outward in-plane unit normal
        public float[] Cap = null!;              // 0 mid-link .. 1 at a tip (ambient occlusion weight)
    }

    private static readonly RingTable[] Rings = { null!, null!, BuildRing(2), BuildRing(3), BuildRing(4) };

    private static RingTable BuildRing(int k)
    {
        int m = 2 * (k + 1);
        var t = new RingTable
        {
            M = m, X = new float[m], Y = new float[m], NX = new float[m], NY = new float[m], Cap = new float[m],
        };

        for (int q = 0; q <= k; q++)
        {
            // Right cap: from the tip (phi 0) to the top of the straight run (phi 90).
            float phi = q / (float)k * (MathF.PI * 0.5f);
            t.X[q] = CapCentre + HalfLateral * MathF.Cos(phi);
            t.Y[q] = HalfLateral * MathF.Sin(phi);
            t.NX[q] = MathF.Cos(phi); t.NY[q] = MathF.Sin(phi);

            // Left cap: from the top of the straight run (phi 90) to the tip (phi 180).
            int j = k + 1 + q;
            float phi2 = MathF.PI * 0.5f + q / (float)k * (MathF.PI * 0.5f);
            t.X[j] = -CapCentre + HalfLateral * MathF.Cos(phi2);
            t.Y[j] = HalfLateral * MathF.Sin(phi2);
            t.NX[j] = MathF.Cos(phi2); t.NY[j] = MathF.Sin(phi2);
        }

        for (int i = 0; i < m; i++)
        {
            float q = MathF.Abs(t.X[i]) / HalfAxis;
            float c = Math.Clamp((q - 0.55f) / 0.45f, 0f, 1f);
            t.Cap[i] = c * c * (3f - 2f * c);
        }
        return t;
    }

    // Cross-section columns, across the bar from one silhouette to the other (-1 .. 1). The first
    // and last are the transparent anti-aliasing fringe and share the neighbouring column's normal.
    private static readonly float[] Cols7 = { -1f, -1f, -0.62f, 0f, 0.62f, 1f, 1f };
    private static readonly float[] Cols5 = { -1f, -1f, 0f, 1f, 1f };

    // ---- per-draw scratch (drawing is single-threaded) ----
    private struct Link
    {
        public Vector2 C, T, N;
        public float Cos, Sin;
        public float Tone, Rust, Alpha;
        public int   Index;
        public int   BucketPlus, BucketMinus;   // depth bucket of each half; -1 = skipped
    }

    private readonly Link[] _links = new Link[MaxLinks];
    private readonly Vector2[] _shadowPts = new Vector2[128];

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        if (path.Count < 2 || alpha <= 0.004f) return;

        float reveal = Math.Clamp(s.Reveal, 0f, 1f);
        if (reveal <= 0.001f) return;

        float total = path.Length;
        float L = Math.Clamp(s.WidthHint, ctx.ShortSide * MinLinkFrac, ctx.ShortSide * MaxLinkFrac);
        if (total < L) return;

        float px = ctx.ScreenScale;
        float depth = Math.Clamp(s.Depth, 0f, 1f);
        float agit = Math.Clamp(s.Agitation, 0f, 1f);
        float time = ctx.Time;
        int seed = s.Seed;

        // ---- per-strand character, fixed by the seed ----
        float pick = DrawHelpers.Hash01(seed + 41);
        Matcap body = pick < 0.44f ? Iron : (pick < 0.76f ? Steel : Aged);
        float rustLevel = MathF.Pow(DrawHelpers.Hash01(seed + 42), 1.7f) * 0.85f;   // most strands stay clean; a few are corroded
        float twistBase = DrawHelpers.HashRange(seed + 43, -0.75f, 0.75f);
        float twistAmp  = DrawHelpers.HashRange(seed + 44, 0.30f, 1.05f);
        float twistFreq = DrawHelpers.HashRange(seed + 45, 0.55f, 1.15f);

        float pitch = L * Pitch;
        bool closed = s.Closed;
        int count;
        float firstCentre;
        if (closed)
        {
            // An even number of links, so the quarter-turn alternation and the seam both close.
            int n = Math.Max(4, 2 * (int)MathF.Round(total / (2f * pitch)));
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
        if (count <= 0) return;

        float visibleLen = total * reveal;
        bool fullyRevealed = reveal >= 1f;
        bool smallBar = L * BarDiameter < 9.5f * px;

        // ---- cast shadow on the scene behind, drawn first ----
        DrawShadow(dl, path, L, depth, alpha, ctx, closed, visibleLen);

        // ---- lay the links out along the path ----
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
                tipFrac = Math.Min(1f, tipFrac);
            }

            Vector2 a = SampleWrapped(path, sc - pitch * 0.5f, total, closed);
            Vector2 b = SampleWrapped(path, sc + pitch * 0.5f, total, closed);
            Vector2 chord = b - a;
            float cl = chord.Length();
            if (cl < 1e-3f) continue;

            Vector2 c = (a + b) * 0.5f;
            if (c.X < -margin || c.Y < -margin || c.X > ctx.ScreenW + margin || c.Y > ctx.ScreenH + margin) continue;

            Vector2 T = chord / cl;
            int idx = i;     // indexed from the path's start, so links gained at the far end never flip the others' parity

            // Roll about the strand's axis: a slow twist along the chain, a gentle sway in time, a
            // rattle when agitated, and the quarter turn that interlocking demands.
            float twist = twistBase
                        + twistAmp * FireNoise.Value(sc / (L * 5.5f) * twistFreq + seed * 0.00137f, 7.3f + time * 0.045f)
                        + 0.16f * MathF.Sin(time * 0.55f + s.Phase + sc * 0.0031f)
                        + agit * 0.30f * MathF.Sin(time * 21f + idx * 1.9f);
            float roll = twist + ((idx & 1) == 0 ? 0f : MathF.PI * 0.5f);

            float cs = MathF.Cos(roll), sn = MathF.Sin(roll);

            int ls = unchecked(seed + idx * 7919);
            ref Link lk = ref _links[linkCount++];
            lk.C = c; lk.T = T; lk.N = new Vector2(-T.Y, T.X);
            lk.Cos = cs; lk.Sin = sn;
            lk.Tone = DrawHelpers.HashRange(ls + 3, 0.86f, 1.10f);
            lk.Rust = Math.Clamp(rustLevel * DrawHelpers.HashRange(ls + 5, 0.25f, 1.45f), 0f, 1f);
            lk.Alpha = tipFrac;
            lk.Index = idx;

            // Depth: the half on the +side sits at z = +b*sin(roll). Bucket -2..2 -> 0..4, so
            // buckets are painted far to near. A face-on link has both halves in the middle bucket.
            float zPlus = sn;
            lk.BucketPlus = (int)MathF.Round(zPlus * 2f) + 2;
            lk.BucketMinus = (int)MathF.Round(-zPlus * 2f) + 2;

            // The half behind the link's plane is seen only through the hole. It is hidden outright when
            // the link is edge-on, and on a small link it is too few pixels to be worth drawing.
            if (MathF.Abs(cs) < 0.06f || (smallBar && MathF.Abs(cs) < 0.55f))
            {
                if (zPlus >= 0f) lk.BucketMinus = -1; else lk.BucketPlus = -1;
            }
        }

        // ---- LOD from the bar's size on screen ----
        float bar = L * BarDiameter;
        int ringK = bar < 9.5f * px ? 2 : (bar < 14.5f * px ? 3 : 4);
        float[] cols = bar < 9.5f * px ? Cols5 : Cols7;
        float feather = (1.0f + 2.4f * depth) * px;

        // ---- paint back to front ----
        for (int bucket = 0; bucket <= 4; bucket++)
        {
            for (int i = 0; i < linkCount; i++)
            {
                ref Link lk = ref _links[i];
                if (lk.BucketPlus == bucket)
                    DrawHalf(dl, in lk, +1, L, ringK, cols, body, seed, depth, agit, feather, alpha, ctx);
                if (lk.BucketMinus == bucket)
                    DrawHalf(dl, in lk, -1, L, ringK, cols, body, seed, depth, agit, feather, alpha, ctx);
            }
        }

        // ---- leading-tip flare while the strand is still extending ----
        if (s.TipFlare > 0.001f && !closed)
        {
            float tipS = fullyRevealed ? total : visibleLen;
            path.SampleAtArc(tipS, out Vector2 tip, out _);
            DrawFlare(dl, tip, bar, Math.Clamp(s.TipFlare, 0f, 1f) * alpha, ctx);
        }
    }

    // ---- Half ring ----

    private static void DrawHalf(ImDrawListPtr dl, in Link lk, int h, float L, int ringK, float[] cols,
                                 Matcap body, int seed, float depth, float agit, float feather,
                                 float alpha, in MaterialContext ctx)
    {
        float half = L * BarDiameter * 0.5f;
        float cos = lk.Cos, sin = lk.Sin;

        // 0 for a half in front of (or level with) the link's plane, up to 1 for one fully behind it.
        // Smooth, so a link rolling through face-on doesn't pop.
        float zHalf = h > 0 ? sin : -sin;
        float far = Math.Clamp(-zHalf * 1.6f, 0f, 1f);

        // The half behind the plane is seen only through the hole, partly covered by its neighbours:
        // spend fewer vertices on it.
        if (zHalf < -0.35f)
        {
            ringK = Math.Max(2, ringK - 1);
            cols = Cols5;
        }

        var ring = Rings[ringK];
        int M = ring.M;
        int nc = cols.Length;
        if (M * nc > MeshDraw.MaxVerts) return;
        float linkAlpha = alpha * lk.Alpha;

        int ls = unchecked(seed + lk.Index * 7919);
        float depthFog = 0.55f * depth;

        Vector3 prevN = Vector3.UnitZ, prevT = Vector3.UnitX;
        int v = 0;

        for (int j = 0; j < M; j++)
        {
            // Centreline point and outward in-plane normal, in the link's (axis, lateral) frame.
            float x = ring.X[j] * L;
            float y = h * ring.Y[j] * L;
            float nx = ring.NX[j];
            float ny = h * ring.NY[j];

            Vector2 pos2 = lk.C + lk.T * x + lk.N * (y * cos);

            // Outward normal o and ring-plane normal m, in view space (x, y screen; z toward viewer).
            Vector3 o = new(lk.T.X * nx + lk.N.X * (ny * cos), lk.T.Y * nx + lk.N.Y * (ny * cos), ny * sin);
            Vector3 m = new(-lk.N.X * sin, -lk.N.Y * sin, cos);

            // The tube's cross-section lives in the plane spanned by o and m. Its most viewer-facing
            // direction n* and the in-screen direction t* across it form the half-cylinder we see.
            float oz = o.Z, mz = m.Z;
            float r = MathF.Sqrt(oz * oz + mz * mz);
            Vector3 nStar, tStar;
            if (r > 1e-3f)
            {
                nStar = (o * oz + m * mz) / r;
                tStar = (m * oz - o * mz) / r;
                prevN = nStar; prevT = tStar;
            }
            else { nStar = prevN; tStar = prevT; }     // a tip seen exactly end-on: keep the neighbour's frame

            float cap = ring.Cap[j];

            for (int c = 0; c < nc; c++)
            {
                float u = cols[c];
                bool fringe = (c == 0 || c == nc - 1);

                float sq = MathF.Sqrt(MathF.Max(0f, 1f - u * u));
                Vector3 n = tStar * u + nStar * sq;               // surface normal at this column
                Vector2 off = new(n.X, n.Y);
                Vector2 pos = pos2 + off * half;
                if (fringe)
                    pos += new Vector2(tStar.X, tStar.Y) * (u * feather);

                // ---- colour ----
                Vector3 col = body.Sample(n.X, n.Y);

                // Rust patches live on the link (coordinates in link space, not screen space).
                // Corrosion collects on the straight runs and in the crevices; the tips, where links
                // rub against each other, stay bare. The noise coordinates are in link space, so the
                // patches travel with the link instead of sliding over it.
                float rustMask = 0f;
                if (lk.Rust > 0.02f)
                {
                    float nz = FireNoise.Value(lk.Index * 1.713f + x / L * 3.4f + u * 0.9f,
                                               h * 4.1f + y / L * 3.4f + seed * 0.0007f);
                    float rm = (nz * 0.5f + 0.5f) + (lk.Rust - 0.5f) * 0.95f;
                    float t = Math.Clamp((rm - 0.56f) / 0.24f, 0f, 1f);
                    rustMask = t * t * (3f - 2f * t) * (1f - 0.85f * cap) * 0.85f;
                }
                if (rustMask > 0.01f)
                    col = Vector3.Lerp(col, Rust.Sample(n.X, n.Y), rustMask);

                // Occlusion: where the neighbour bears on the inside of a tip, darker; the outside
                // of a tip is rubbed bright.
                float outer = n.X * o.X + n.Y * o.Y + n.Z * o.Z;
                float ao = 1f - 0.50f * cap * MathF.Max(0f, -outer);
                ao *= 1f - 0.20f * far;
                float wear = 1f + 0.10f * cap * MathF.Max(0f, outer) * (1f - rustMask);

                col *= ao * wear * lk.Tone;

                if (agit > 0.01f)
                    col += col * (col * (0.55f * agit));          // flare the highlights, leave the shadows

                if (depthFog > 0f)
                    col = Vector3.Lerp(col, Fog, depthFog);

                float va = fringe ? 0f : 1f;
                MeshDraw.P[v] = pos;
                MeshDraw.C[v] = DrawHelpers.WithAlpha(FireColor.Pack(col.X, col.Y, col.Z), va * linkAlpha);
                v++;
            }
        }

        MeshDraw.Grid(dl, nc - 1, M - 1, MeshDraw.WhiteUv(ctx.Time));
    }

    // ---- Cast shadow ----

    /// <summary>
    /// A soft band under the whole strand, displaced away from the key light. The chain floats in
    /// front of the scene, and the shadow it throws on it is what turns a sticker into an object:
    /// the displacement says how far off the scene it hangs.
    /// </summary>
    private void DrawShadow(ImDrawListPtr dl, StrandPath path, float L, float depth, float alpha,
                            in MaterialContext ctx, bool closed, float visibleLen)
    {
        float a = alpha * 0.40f * (1f - 0.45f * depth);
        if (a <= 0.004f) return;

        float total = path.Length;

        // The chain runs on past a fully revealed path's ends, so its shadow does too.
        bool full = closed || visibleLen >= total;
        float s0 = closed ? 0f : (full ? -L * 0.5f : 0f);
        float s1 = closed ? total : (full ? total + L * 0.5f : visibleLen);
        int rows = Math.Clamp((int)((s1 - s0) / (L * 0.5f)), 3, 40);
        float reach = L * 0.60f * (1f - 0.55f * depth);
        Vector2 offset = StudioLighting.ShadowDirection * reach;
        float hw = L * 0.34f;
        float soft = hw * (0.9f + 1.1f * depth);

        // Rows run along the path; columns run across it: transparent, soft, solid, soft, transparent.
        ReadOnlySpan<float> across = stackalloc float[5] { -1f, -0.55f, 0f, 0.55f, 1f };
        ReadOnlySpan<float> prof = stackalloc float[5] { 0f, 0.62f, 1f, 0.62f, 0f };

        int v = 0;
        for (int r = 0; r <= rows; r++)
        {
            float sArc = s0 + (s1 - s0) * r / rows;
            path.SampleAtArc(sArc, out Vector2 p, out Vector2 t);
            Vector2 nrm = new(-t.Y, t.X);
            float edgeFade = closed ? 1f : MathF.Min(1f, MathF.Min(r, rows - r) / 1.5f);   // taper at both ends

            for (int c = 0; c < 5; c++)
            {
                float w = across[c];
                MeshDraw.P[v] = p + offset + nrm * (w * (hw + soft * 0.5f * MathF.Abs(w)));
                MeshDraw.C[v] = DrawHelpers.WithAlpha(ShadowTint, a * prof[c] * edgeFade);
                v++;
            }
        }
        MeshDraw.Grid(dl, 4, rows, MeshDraw.WhiteUv(ctx.Time));
    }

    // ---- Leading flare ----

    private static void DrawFlare(ImDrawListPtr dl, Vector2 tip, float bar, float k, in MaterialContext ctx)
    {
        if (k <= 0.004f) return;
        Span<float> rr = stackalloc float[2] { bar * 1.1f, bar * 3.0f };
        Span<uint> cc = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(FlareWarm, 0.30f * k),
            DrawHelpers.WithAlpha(FlareWarm, 0f),
        };
        MeshDraw.Radial(dl, tip, MeshDraw.WhiteUv(ctx.Time), DrawHelpers.WithAlpha(0xFFFFFFFFu, 0.85f * k),
                        10, rr, cc, 0f, 1f, 0, 0f);
    }

    // ---- Helpers ----

    private static Vector2 SampleWrapped(StrandPath path, float s, float total, bool closed)
    {
        if (closed && total > 1e-3f)
        {
            s %= total;
            if (s < 0f) s += total;
        }
        path.SampleAtArc(s, out Vector2 p, out _);
        return p;
    }

    // ---- Shedding ----

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    /// <summary>
    /// Rust and dust working loose from moving iron. Flakes drop; dust sifts down more slowly.
    /// Sparse on purpose: a chain at rest sheds an occasional chip, not a blizzard.
    /// </summary>
    private static readonly StrokeEmission[] Shed =
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

    /// <summary>Iron on iron: a hard shower of sparks, a spray of shaken-loose scale, and a puff of dust.</summary>
    private static readonly ImpactEmission[] Hit =
    {
        new(Role: PrimitiveRole.Spark,
            CountMin: 16, CountMax: 28,
            SpeedMin: 420f, SpeedMax: 1400f,
            LifespanMin: 0.30f, LifespanMax: 0.78f,
            SizeMin: 1.6f, SizeMax: 3.8f,
            ConeRadians: 1.0f,
            Gravity: new Vector2(0f, 2600f)),

        new(Role: PrimitiveRole.Flake,
            CountMin: 12, CountMax: 22,
            SpeedMin: 110f, SpeedMax: 460f,
            LifespanMin: 0.9f, LifespanMax: 1.9f,
            SizeMin: 1.6f, SizeMax: 3.6f,
            ConeRadians: 1.35f,
            Gravity: new Vector2(0f, 900f)),

        new(Role: PrimitiveRole.Dust,
            CountMin: 3, CountMax: 5,
            SpeedMin: 35f, SpeedMax: 150f,
            LifespanMin: 0.9f, LifespanMax: 1.7f,
            SizeMin: 16f, SizeMax: 30f,
            ConeRadians: 1.5f,
            Gravity: new Vector2(0f, -14f)),
    };
}
