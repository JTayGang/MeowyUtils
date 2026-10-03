using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Iron chain: a string of oval links rolled about the strand's axis, shaded by the shared
/// matcap and sorted so interlocks occlude like a photograph. The far half of each link is
/// drawn first so an edge-on link passes in front of one neighbour's near side and behind
/// the other's far side — the single detail that makes a chain read as interlocked rather
/// than as a row of overlapping ovals.
///
/// Stroke contract: any polyline; link length comes from WidthHint; Closed wraps the seam
/// (rounded to an even link count so the alternating quarter-turns close); Depth fogs and
/// softens; Agitation lights up the metal and rattles links.
/// </summary>
public sealed class StrokeChain : IStrokeMaterial
{
    public string Name => "stroke.chain";
    public string[] NaturalLanguageWords { get; } = { "chain", "chains", "links" };

    private const float MinLinkFrac = 0.030f;
    private const float MaxLinkFrac = 0.085f;
    private const int MaxLinks = 200;

    // Link proportions, as fractions of the outer length L: 5 bar-diameters long, 3.4 wide,
    // pitched 3 diameters apart (which equals the inside length — the overlap that interlocks).
    private const float BarDiameter = 0.200f;
    private const float Pitch       = 0.600f;
    private const float HalfAxis    = 0.400f;
    private const float HalfLateral = 0.240f;
    private const float CapCentre   = HalfAxis - HalfLateral;

    private static readonly Matcap Iron  = new(SurfacePresets.BlackIron);
    private static readonly Matcap Steel = new(SurfacePresets.WornSteel);
    private static readonly Matcap Aged  = new(SurfacePresets.AgedIron);
    private static readonly Matcap Rust  = new(SurfacePresets.Rust);

    private static readonly Vector3 Fog        = new(0.105f, 0.125f, 0.165f);
    private static readonly uint    ShadowTint = FireColor.Pack(0.015f, 0.016f, 0.022f);
    private static readonly uint    FlareWarm  = FireColor.Pack(1.00f, 0.80f, 0.52f);

    private sealed class RingTable
    {
        public int M;
        public float[] X = null!, Y = null!, NX = null!, NY = null!, Cap = null!;
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
            float phi = q / (float)k * (MathF.PI * 0.5f);
            t.X[q] = CapCentre + HalfLateral * MathF.Cos(phi);
            t.Y[q] = HalfLateral * MathF.Sin(phi);
            t.NX[q] = MathF.Cos(phi); t.NY[q] = MathF.Sin(phi);

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

    // Column offsets across the bar (-1..1). First/last are the AA fringe. The sqrt(1-u²) values
    // are precomputed so DrawHalf doesn't sqrt every vertex — a 10–14k sqrt/frame saving across a
    // full cast at the current mesh sizes.
    private static readonly float[] Cols7   = { -1f, -1f, -0.62f, 0f, 0.62f, 1f, 1f };
    private static readonly float[] Cols5   = { -1f, -1f, 0f, 1f, 1f };
    private static readonly float[] Cols7Sq = BuildSq(Cols7);
    private static readonly float[] Cols5Sq = BuildSq(Cols5);

    private static float[] BuildSq(float[] us)
    {
        var r = new float[us.Length];
        for (int i = 0; i < us.Length; i++)
            r[i] = MathF.Sqrt(MathF.Max(0f, 1f - us[i] * us[i]));
        return r;
    }

    private struct Link
    {
        public Vector2 C, T, N;
        public float Cos, Sin, Tone, Rust, Alpha;
        public int   Index;
        public int   BucketPlus, BucketMinus;
    }

    private readonly Link[] _links = new Link[MaxLinks];

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

        // Bar width is fixed for the whole chain; the LOD tier and the far-half cull both key
        // off it. Computed once so both sites agree and the multiply isn't done twice.
        float bar = L * BarDiameter;
        bool smallBar = bar < 9.5f * px;

        float pick = DrawHelpers.Hash01(seed + 41);
        Matcap body = pick < 0.44f ? Iron : (pick < 0.76f ? Steel : Aged);
        float rustLevel = MathF.Pow(DrawHelpers.Hash01(seed + 42), 1.7f) * 0.85f;
        float twistBase = DrawHelpers.HashRange(seed + 43, -0.75f, 0.75f);
        float twistAmp  = DrawHelpers.HashRange(seed + 44, 0.30f, 1.05f);
        float twistFreq = DrawHelpers.HashRange(seed + 45, 0.55f, 1.15f);

        float pitch = L * Pitch;
        bool closed = s.Closed;
        int count;
        float firstCentre;
        if (closed)
        {
            // Even link count so alternation and seam both close.
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

        DrawShadow(dl, path, L, depth, alpha, ctx, closed, visibleLen);

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
            int idx = i;

            // Twist along the strand + sway + agitation + the quarter-turn interlocking forces.
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

            float zPlus = sn;
            lk.BucketPlus = (int)MathF.Round(zPlus * 2f) + 2;
            lk.BucketMinus = (int)MathF.Round(-zPlus * 2f) + 2;

            // Hide the far half entirely when it's too thin to read.
            if (MathF.Abs(cs) < 0.06f || (smallBar && MathF.Abs(cs) < 0.55f))
            {
                if (zPlus >= 0f) lk.BucketMinus = -1; else lk.BucketPlus = -1;
            }
        }

        int ringK = bar < 9.5f * px ? 2 : (bar < 14.5f * px ? 3 : 4);
        float[] cols   = bar < 9.5f * px ? Cols5 : Cols7;
        float[] colsSq = bar < 9.5f * px ? Cols5Sq : Cols7Sq;
        float feather = (1.0f + 2.4f * depth) * px;

        // Paint back to front by depth bucket.
        for (int bucket = 0; bucket <= 4; bucket++)
        {
            for (int i = 0; i < linkCount; i++)
            {
                ref Link lk = ref _links[i];
                if (lk.BucketPlus == bucket)
                    DrawHalf(dl, in lk, +1, L, ringK, cols, colsSq, body, seed, depth, agit, feather, alpha, ctx);
                if (lk.BucketMinus == bucket)
                    DrawHalf(dl, in lk, -1, L, ringK, cols, colsSq, body, seed, depth, agit, feather, alpha, ctx);
            }
        }

        if (s.TipFlare > 0.001f && !closed)
        {
            float tipS = fullyRevealed ? total : visibleLen;
            path.SampleAtArc(tipS, out Vector2 tip, out _);
            DrawFlare(dl, tip, bar, Math.Clamp(s.TipFlare, 0f, 1f) * alpha, ctx);
        }
    }

    private static void DrawHalf(ImDrawListPtr dl, in Link lk, int h, float L, int ringK,
                                 float[] cols, float[] colsSq,
                                 Matcap body, int seed, float depth, float agit, float feather,
                                 float alpha, in MaterialContext ctx)
    {
        float half = L * BarDiameter * 0.5f;
        float cos = lk.Cos, sin = lk.Sin;

        float zHalf = h > 0 ? sin : -sin;
        float far = Math.Clamp(-zHalf * 1.6f, 0f, 1f);

        // Behind-the-plane half is mostly hidden: spend fewer verts on it.
        if (zHalf < -0.35f) { ringK = Math.Max(2, ringK - 1); cols = Cols5; colsSq = Cols5Sq; }

        var ring = Rings[ringK];
        int M = ring.M;
        int nc = cols.Length;
        if (M * nc > MeshDraw.MaxVerts) return;
        float linkAlpha = alpha * lk.Alpha;

        float depthFog = 0.55f * depth;

        Vector3 prevN = Vector3.UnitZ, prevT = Vector3.UnitX;
        int v = 0;

        for (int j = 0; j < M; j++)
        {
            float x = ring.X[j] * L;
            float y = h * ring.Y[j] * L;
            float nx = ring.NX[j];
            float ny = h * ring.NY[j];

            Vector2 pos2 = lk.C + lk.T * x + lk.N * (y * cos);

            Vector3 o = new(lk.T.X * nx + lk.N.X * (ny * cos), lk.T.Y * nx + lk.N.Y * (ny * cos), ny * sin);
            Vector3 m = new(-lk.N.X * sin, -lk.N.Y * sin, cos);

            float oz = o.Z, mz = m.Z;
            float r = MathF.Sqrt(oz * oz + mz * mz);
            Vector3 nStar, tStar;
            if (r > 1e-3f)
            {
                nStar = (o * oz + m * mz) / r;
                tStar = (m * oz - o * mz) / r;
                prevN = nStar; prevT = tStar;
            }
            else { nStar = prevN; tStar = prevT; }

            float cap = ring.Cap[j];

            for (int c = 0; c < nc; c++)
            {
                float u = cols[c];
                float sq = colsSq[c];
                bool fringe = (c == 0 || c == nc - 1);

                Vector3 n = tStar * u + nStar * sq;
                Vector2 pos = pos2 + new Vector2(n.X, n.Y) * half;
                if (fringe) pos += new Vector2(tStar.X, tStar.Y) * (u * feather);

                Vector3 col = body.Sample(n.X, n.Y);

                // Rust patches anchored in link space so they travel with the link.
                float rustMask = 0f;
                if (lk.Rust > 0.02f)
                {
                    float nz = FireNoise.Value(lk.Index * 1.713f + x / L * 3.4f + u * 0.9f,
                                               h * 4.1f + y / L * 3.4f + seed * 0.0007f);
                    float rm = (nz * 0.5f + 0.5f) + (lk.Rust - 0.5f) * 0.95f;
                    float t = Math.Clamp((rm - 0.56f) / 0.24f, 0f, 1f);
                    rustMask = t * t * (3f - 2f * t) * (1f - 0.85f * cap) * 0.85f;
                }
                if (rustMask > 0.01f) col = Vector3.Lerp(col, Rust.Sample(n.X, n.Y), rustMask);

                // AO inside the tips (where the neighbour bears) / brighten the rubbed outside.
                float outer = n.X * o.X + n.Y * o.Y + n.Z * o.Z;
                float ao = (1f - 0.50f * cap * MathF.Max(0f, -outer)) * (1f - 0.20f * far);
                float wear = 1f + 0.10f * cap * MathF.Max(0f, outer) * (1f - rustMask);
                col *= ao * wear * lk.Tone;

                if (agit > 0.01f) col += col * (col * (0.55f * agit));
                if (depthFog > 0f) col = Vector3.Lerp(col, Fog, depthFog);

                float va = fringe ? 0f : 1f;
                MeshDraw.P[v] = pos;
                MeshDraw.C[v] = DrawHelpers.WithAlpha(FireColor.Pack(col.X, col.Y, col.Z), va * linkAlpha);
                v++;
            }
        }

        MeshDraw.Grid(dl, nc - 1, M - 1, MeshDraw.WhiteUv(ctx.Time));
    }

    /// <summary>Soft cast band under the strand, displaced away from the key light.</summary>
    private void DrawShadow(ImDrawListPtr dl, StrandPath path, float L, float depth, float alpha,
                            in MaterialContext ctx, bool closed, float visibleLen)
    {
        float a = alpha * 0.40f * (1f - 0.45f * depth);
        if (a <= 0.004f) return;

        float total = path.Length;
        bool full = closed || visibleLen >= total;
        float s0 = closed ? 0f : (full ? -L * 0.5f : 0f);
        float s1 = closed ? total : (full ? total + L * 0.5f : visibleLen);
        int rows = Math.Clamp((int)((s1 - s0) / (L * 0.5f)), 3, 40);
        float reach = L * 0.60f * (1f - 0.55f * depth);
        Vector2 offset = StudioLighting.ShadowDirection * reach;
        float hw = L * 0.34f;
        float soft = hw * (0.9f + 1.1f * depth);

        ReadOnlySpan<float> across = stackalloc float[5] { -1f, -0.55f, 0f, 0.55f, 1f };
        ReadOnlySpan<float> prof = stackalloc float[5] { 0f, 0.62f, 1f, 0.62f, 0f };

        int v = 0;
        for (int r = 0; r <= rows; r++)
        {
            float sArc = s0 + (s1 - s0) * r / rows;
            path.SampleAtArc(sArc, out Vector2 p, out Vector2 t);
            Vector2 nrm = new(-t.Y, t.X);
            float edgeFade = closed ? 1f : MathF.Min(1f, MathF.Min(r, rows - r) / 1.5f);

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

    private static Vector2 SampleWrapped(StrandPath path, float s, float total, bool closed)
    {
        if (closed && total > 1e-3f) { s %= total; if (s < 0f) s += total; }
        path.SampleAtArc(s, out Vector2 p, out _);
        return p;
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    /// <summary>Rust flakes dropping; dust sifting more slowly. Sparse on purpose.</summary>
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

    /// <summary>Sparks, scale, and a puff of dust on impact.</summary>
    private static readonly ImpactEmission[] Hit =
    {
        new(Role: PrimitiveRole.Spark, CountMin: 16, CountMax: 28,
            SpeedMin: 420f, SpeedMax: 1400f, LifespanMin: 0.30f, LifespanMax: 0.78f,
            SizeMin: 1.6f, SizeMax: 3.8f, ConeRadians: 1.0f, Gravity: new Vector2(0f, 2600f)),
        new(Role: PrimitiveRole.Flake, CountMin: 12, CountMax: 22,
            SpeedMin: 110f, SpeedMax: 460f, LifespanMin: 0.9f, LifespanMax: 1.9f,
            SizeMin: 1.6f, SizeMax: 3.6f, ConeRadians: 1.35f, Gravity: new Vector2(0f, 900f)),
        new(Role: PrimitiveRole.Dust, CountMin: 3, CountMax: 5,
            SpeedMin: 35f, SpeedMax: 150f, LifespanMin: 0.9f, LifespanMax: 1.7f,
            SizeMin: 16f, SizeMax: 30f, ConeRadians: 1.5f, Gravity: new Vector2(0f, -14f)),
    };
}