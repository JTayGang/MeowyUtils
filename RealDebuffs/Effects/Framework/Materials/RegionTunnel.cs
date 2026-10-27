using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>A dusty tunnel seen from inside; with no TunnelField it is a flat fill.</summary>
public sealed class RegionTunnel : IRegionMaterial
{
    public string Name => "region.haunt-tunnel";

    public void Draw(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx)
    {
        float a = r.Alpha * ctx.Alpha;
        if (r.State is not TunnelField f)
        {
            if (a > 0.001f) dl.AddRectFilled(r.Min, r.Max, DrawHelpers.WithAlpha(r.Tint, a));
            return;
        }

        a *= f.Alpha;
        if (a <= 0.003f) return;

        f.Wall.Ensure(f.Seed);
        var env = new Env(f, a, DrawHelpers.ColorOverrideActive, MeshDraw.WhiteUv(ctx.Time));
        DrawGlow(dl, f, in env);
        DrawWalls(dl, f, in env);
        DrawGrit(dl, f, in env);
        DrawHaze(dl, f, in env);
        DrawShafts(dl, f, in env);
        DrawDust(dl, f, in env);
        DrawShudder(dl, f, in env);
    }

    // The frame's shared numbers

    private readonly struct Env
    {
        public readonly float A, Light, Dusty;
        public readonly bool Override;
        public readonly Vector2 Uv, VanishingPoint;

        public Env(TunnelField f, float a, bool overridden, Vector2 uv)
        {
            A = a * f.Appear; Override = overridden; Uv = uv;

            // A shudder fills the air with dust: more of it, bigger, and thicker haze.
            Dusty = f.Vision == 5 ? f.Env : 0f;

            // How much pale light there is: pulses with the heart, flares when something is shown, dims when the fall stops.
            float light = 1f + 0.30f * f.Beat;
            light += f.Vision switch
            {
                3 => 0.55f * f.Ghost + 0.40f * f.Kick,
                4 => 0.6f * f.Env + 0.35f * f.Kick,
                5 => 0.40f * f.Env,
                _ => 0f,
            };
            if (f.Vision is 1 or 2) light *= 1f - 0.45f * f.Env;
            Light = light;

            Vector2 b = f.Bend(f.Flight.FarHeight);
            VanishingPoint = f.Cam.Project(b.X, b.Y, f.Flight.FarHeight * f.Px, out _);
        }
    }

    private static uint Col(Vector3 rgb, float alpha, bool overridden)
        => DrawHelpers.VertexColor(rgb, alpha, DrawHelpers.AlphaBits(alpha), overridden);

    // The tone curve is the same for every vertex: look it up instead of exp and pow each time.
    private const int LutSize = 512;
    private const float LutMax = 3f;
    private static readonly float[] DisplayLut = BuildDisplayLut();

    private static float[] BuildDisplayLut()
    {
        var lut = new float[LutSize + 2];
        for (int i = 0; i < lut.Length; i++)
        {
            float x = i * LutMax / LutSize;
            lut[i] = MathF.Pow(1f - MathF.Exp(-1.55f * x), 1f / 2.2f);
        }
        return lut;
    }

    private static float Display(float x)
    {
        float p = Math.Clamp(x, 0f, LutMax) * (LutSize / LutMax);
        int i = (int)p;
        return DisplayLut[i] + (DisplayLut[i + 1] - DisplayLut[i]) * (p - i);
    }

    private static Vector3 Display(Vector3 lin) => new(Display(lin.X), Display(lin.Y), Display(lin.Z));

    // Unit vectors round a turn per ring segment count: rotating one by the twist is four multiplies, not cos and sin.
    private static readonly Dictionary<int, (float[] C, float[] S)> Turns = new();

    private static (float[] C, float[] S) TurnTable(int segs)
    {
        if (Turns.TryGetValue(segs, out var t)) return t;
        var c = new float[segs + 1]; var s = new float[segs + 1];
        for (int j = 0; j <= segs; j++) { c[j] = MathF.Cos(MathF.Tau * j / segs); s[j] = MathF.Sin(MathF.Tau * j / segs); }
        return Turns[segs] = (c, s);
    }

    // One tessellation for every strip and lip: a changing vertex count would resample the grime and shift the wall.
    private const int WallSegments = 64;
    private static int SegmentsFor(float scale) => WallSegments;

    /// <summary>Fades a detail in as a rib comes near (0 below lo, 1 above hi); a hard threshold popped.</summary>
    private static float Near(float scale, float lo, float hi) => DrawHelpers.Smooth((scale - lo) / (hi - lo));

    private static readonly Vector3 FarLight = new(0.62f, 0.90f, 1.00f);
    private static readonly Vector3 Ambient = new(0.40f, 0.52f, 0.64f);
    private static readonly Vector3 DustTint = new(0.86f, 0.88f, 0.84f);
    private static readonly Vector3 ShaftTint = new(0.80f, 0.90f, 0.88f);

    // The glow at the far end

    private static void DrawGlow(ImDrawListPtr dl, TunnelField f, in Env e)
    {
        float s = f.Short, k = e.A * e.Light;
        Span<float> rr = stackalloc float[3] { 0.06f * s, 0.20f * s, 0.46f * s };
        Span<uint> rc = stackalloc uint[3]
        {
            Col(new Vector3(0.50f, 0.74f, 0.80f), 0.085f * k, e.Override),
            Col(new Vector3(0.34f, 0.52f, 0.60f), 0.040f * k, e.Override),
            Col(new Vector3(0.20f, 0.32f, 0.40f), 0f, e.Override),
        };
        MeshDraw.Radial(dl, e.VanishingPoint, e.Uv, Col(new Vector3(0.62f, 0.86f, 0.90f), 0.10f * k, e.Override), 36, rr, rc, 0f, 1f, 0, 0f);
    }

    // The wall

    private struct Rib { public float H, W; public int Seed; }
    private static readonly Rib[] Ribs = new Rib[TunnelField.MaxRibs];

    private const int WallKind = 0, BandKind = 1, FrontKind = 2;
    private const int MaxCols = 100;
    private static readonly MeshBuilder Mb = new();
    private static readonly Vector2[] LipPts = new Vector2[MaxCols + 1];
    private static readonly Vector2[] LipNrm = new Vector2[MaxCols + 1];
    private static readonly float[] LipHalf = new float[MaxCols + 1];
    private static readonly uint[] LipCol = new uint[MaxCols + 1];

    private static void DrawWalls(ImDrawListPtr dl, TunnelField f, in Env e)
    {
        var fl = f.Flight;
        int n = 0;
        for (int i = 0; i < f.RibAt.Length && n < TunnelField.MaxRibs; i++)
        {
            float hu = fl.HeightOf(f.RibAt[i], out int lap);
            int s = f.Seed + 2000 + i * 7919 + lap * 104729;
            Ribs[n++] = new Rib { H = hu, W = DrawHelpers.HashRange(s + 20, 55f, 100f), Seed = s };
        }
        for (int i = 1; i < n; i++)        // far to near
        {
            Rib x = Ribs[i]; int j = i - 1;
            while (j >= 0 && Ribs[j].H > x.H) { Ribs[j + 1] = Ribs[j]; j--; }
            Ribs[j + 1] = x;
        }

        Mb.Clear();
        float far = fl.FarHeight, near = fl.NearHeight, reach = far;
        Span<Row> rows = stackalloc Row[12];
        for (int i = 0; i <= n; i++)
        {
            // The panel of bare wall before this rib (or the last stretch, after the final one).
            float end = i < n ? Ribs[i].H - Ribs[i].W : near;
            if (end > reach + 8f)
            {
                int steps = Math.Clamp((int)MathF.Ceiling((end - reach) / 150f), 1, 8);
                for (int k = 0; k <= steps; k++) rows[k] = new Row(TunnelField.WallRadius, reach + (end - reach) * k / steps, 1f);
                Strip(f, in e, rows[..(steps + 1)], WallKind, 0, reach);
                Seams(f, in e, rows[..(steps + 1)], i);
            }
            if (i == n) break;

            // The rib: its inner face (a narrow band seen almost edge on), then its front face (an annulus).
            ref Rib rib = ref Ribs[i];
            float back = MathF.Max(reach, rib.H - rib.W);
            // The inner face is a sliver seen edge-on: it only shows once the rib is close, and fades up as it comes.
            float bandShow = Near(f.Cam.ScaleAt(rib.H * f.Px), 0.62f, 0.95f);
            if (bandShow > 0.02f)
            {
                rows[0] = new Row(TunnelField.RibRadius, back, bandShow); rows[1] = new Row(TunnelField.RibRadius, rib.H, bandShow);
                Strip(f, in e, rows[..2], BandKind, rib.Seed);
            }
            rows[0] = new Row(TunnelField.RibRadius, rib.H, 1f); rows[1] = new Row(TunnelField.WallRadius, rib.H, 1f);
            Strip(f, in e, rows[..2], FrontKind, rib.Seed);
            reach = rib.H;

            // Painter's order: this rib's lip goes in after its own faces and before anything nearer.
            Lip(f, in e, rib.H, rib.Seed);
        }
        Cracks(f, in e);
        Mb.Flush(dl, e.Uv);
    }

    private readonly struct Row
    {
        public readonly float Radius, Hu, AlphaMul;
        public Row(float radius, float hu, float alphaMul) { Radius = radius; Hu = hu; AlphaMul = alphaMul; }
    }

    /// <summary>One strip of wall: rows of vertices, each a full turn round the tunnel, stitched into quads.</summary>
    private static void Strip(TunnelField f, in Env e, ReadOnlySpan<Row> rows, int kind, int ribSeed, float spanStart = 0f)
    {
        float px = f.Px, shortSide = f.Short, flightLen = f.Flight.Length, far = f.Flight.FarHeight;
        float midH = 0.5f * (rows[0].Hu + rows[^1].Hu);
        float sc = f.Cam.ScaleAt(midH * px);

        // Nothing to draw once the whole strip is out beyond the corners of the screen.
        float halfDiag = 0.5f * MathF.Sqrt(f.Size.X * f.Size.X + f.Size.Y * f.Size.Y);
        float inner = MathF.Min(rows[0].Radius, rows[^1].Radius) * shortSide;
        if (inner * f.Cam.ScaleAt(rows[0].Hu * px) * 0.9f > halfDiag * 1.25f && inner * f.Cam.ScaleAt(rows[^1].Hu * px) * 0.9f > halfDiag * 1.25f) return;

        int segs = SegmentsFor(sc);
        var (cosT, sinT) = TurnTable(segs);
        int cols = segs + 1, count = rows.Length * cols;
        Mb.Reserve(count, (rows.Length - 1) * segs * 6);

        int first = -1;
        for (int ri = 0; ri < rows.Length; ri++)
        {
            ref readonly Row row = ref rows[ri];
            float h = row.Hu * px;
            float t = f.Flight.Near(row.Hu);
            float zl = (row.Hu - far - f.Flight.Travel) / flightLen;
            float twist = f.Swirl * row.Hu + f.Roll;
            float ct = MathF.Cos(twist), st = MathF.Sin(twist);

            // Ends of the tunnel fade into the dark in front of and behind us.
            float fade = DrawHelpers.Smooth(t / 0.24f) * (1f - DrawHelpers.Smooth((t - 0.90f) / 0.10f));
            float lightFar = MathF.Exp(-3.0f * t);
            float body = DrawHelpers.Lerp(0.04f, 0.46f, DrawHelpers.Smooth(t / 0.80f));
            float kindFar = kind == WallKind ? 1.00f : kind == BandKind ? 1.50f : 0.04f;
            float kindAlpha = kind == WallKind ? 1.00f : kind == BandKind ? 1.10f : 1.75f;
            float radius = row.Radius * shortSide * f.Breath;
            Vector2 bend = f.Bend(row.Hu);

            // The rib before a panel shades it from the far light and fills the corner with dark.
            float shade = kind == WallKind ? 1f - 0.85f * MathF.Exp(-(row.Hu - spanStart) / 210f) : 1f;

            for (int j = 0; j < cols; j++)
            {
                float turns = (float)(j % segs) / segs;
                float cx = cosT[j] * ct - sinT[j] * st, cy = sinT[j] * ct + cosT[j] * st;
                Vector2 at = f.Cam.Project(radius * cx + bend.X, radius * cy + bend.Y, h, out _);

                Vector3 tex = f.Wall.Sample(turns, zl);
                float mottle = tex.X, streak = tex.Y, pit = tex.Z;

                // Concrete in the dark: blotched, stained and pitted, lit by the far light and a breath of ambient.
                float blotch = DrawHelpers.Smooth((mottle - 0.30f) / 0.42f);
                float albedo = (0.045f + 0.36f * blotch) * (1f - 0.72f * streak) * (0.80f + 0.40f * pit);
                Vector3 lin = albedo * (Ambient * 0.060f + FarLight * (2.4f * lightFar * kindFar * shade * e.Light));
                Vector3 rgb = Display(lin);

                float radial = DrawHelpers.Smooth((Vector2.Distance(at, f.Center) / shortSide - 0.26f) / 0.45f);
                float dirt = 0.55f * streak + 0.45f * (1f - blotch);
                float alpha = body * kindAlpha * (0.30f + 1.45f * dirt) * radial * fade * row.AlphaMul * e.A;

                int vi = Mb.VertexReserved(at, Col(rgb, alpha, e.Override));
                if (first < 0) first = vi;
            }
        }
        Mb.StitchGrid(first, rows.Length, cols);
    }

    private const int SeamCount = 12, MaxSeamRows = 10;
    private static readonly Vector2[] SeamP = new Vector2[MaxSeamRows], SeamN = new Vector2[MaxSeamRows];
    private static readonly float[] SeamHalf = new float[MaxSeamRows];
    private static readonly uint[] SeamCol = new uint[MaxSeamRows];
    private static readonly Vector2[] SeamBend = new Vector2[MaxSeamRows];

    /// <summary>The joints between a panel's blocks: dark lines running out toward the far end; alternate panels shift half a block.</summary>
    private static void Seams(TunnelField f, in Env e, ReadOnlySpan<Row> rows, int panel)
    {
        int n = rows.Length;
        if (n < 2 || n > MaxSeamRows) return;
        float px = f.Px, midH = 0.5f * (rows[0].Hu + rows[n - 1].Hu);
        float sc = f.Cam.ScaleAt(midH * px);
        float seen = Near(sc, 0.50f, 0.85f);       // fades in with the rib rather than appearing all at once
        if (seen < 0.02f) return;

        float radius = rows[0].Radius * f.Short * f.Breath;
        float halfDiag = 0.5f * MathF.Sqrt(f.Size.X * f.Size.X + f.Size.Y * f.Size.Y);
        for (int r = 0; r < n; r++) SeamBend[r] = f.Bend(rows[r].Hu);

        for (int k = 0; k < SeamCount; k++)
        {
            float baseAng = MathF.Tau * (k + 0.5f * (panel & 1)) / SeamCount + 0.02f * DrawHelpers.Hash01(f.Seed + panel * 31 + k);
            bool any = false;
            for (int r = 0; r < n; r++)
            {
                float hu = rows[r].Hu, h = hu * px;
                float ang = baseAng + f.Swirl * hu + f.Roll;
                Vector2 at = f.Cam.Project(radius * MathF.Cos(ang) + SeamBend[r].X, radius * MathF.Sin(ang) + SeamBend[r].Y, h, out float s2);
                float t = f.Flight.Near(hu);
                float fade = DrawHelpers.Smooth(t / 0.24f) * (1f - DrawHelpers.Smooth((t - 0.90f) / 0.10f));
                float radial = DrawHelpers.Smooth((Vector2.Distance(at, f.Center) / f.Short - 0.26f) / 0.45f);
                float body = DrawHelpers.Lerp(0.04f, 0.56f, DrawHelpers.Smooth(t / 0.80f));
                float alpha = 0.95f * body * fade * radial * e.A * seen;
                SeamP[r] = at;
                SeamHalf[r] = Math.Clamp(1.7f * px * s2, 1.1f * px, 4.5f * px);
                SeamCol[r] = Col(new Vector3(0.01f, 0.014f, 0.02f), alpha, e.Override);
                if (alpha > 0.01f && Vector2.Distance(at, f.Center) < halfDiag * 1.3f) any = true;
            }
            if (!any) continue;
            MeshBuilder.MiterNormals(SeamP.AsSpan(0, n), SeamN.AsSpan(0, n));
            Mb.Tent(SeamP.AsSpan(0, n), SeamN.AsSpan(0, n), SeamHalf.AsSpan(0, n), SeamCol.AsSpan(0, n));
        }
    }

    private const int CrackPts = 10;
    private static readonly Vector2[] CrackP = new Vector2[CrackPts], CrackN = new Vector2[CrackPts];
    private static readonly float[] CrackHalf = new float[CrackPts];
    private static readonly uint[] CrackCol = new uint[CrackPts];

    /// <summary>Cracks in the wall: ragged dark lines that wander along it a little, worst where they start.</summary>
    private static void Cracks(TunnelField f, in Env e)
    {
        var fl = f.Flight;
        float px = f.Px;
        for (int c = 0; c < TunnelField.CrackCount; c++)
        {
            int s = f.Seed + 3000 + c * 7919;
            float ang0 = DrawHelpers.Hash01(s + 1) * MathF.Tau;
            float reachLen = DrawHelpers.HashRange(s + 2, 500f, 1000f);
            float walk = DrawHelpers.HashRange(s + 3, 0.04f, 0.10f);
            float radius = TunnelField.WallRadius * f.Short * f.Breath * 0.995f;

            int n = 0; float prevH = float.MinValue;
            float ang = ang0;
            for (int i = 0; i < CrackPts; i++)
            {
                float u = i / (CrackPts - 1f);
                float hu = fl.HeightOf(f.CrackAt[c] + u * reachLen, out _);
                if (hu < prevH) break;                         // wrapped round the end of the tunnel: stop here
                prevH = hu;
                ang += (DrawHelpers.Hash01(s + 10 + i) - 0.5f) * walk;
                float twist = f.Swirl * hu + f.Roll;
                Vector2 b = f.Bend(hu);
                Vector2 at = f.Cam.Project(radius * MathF.Cos(ang + twist) + b.X, radius * MathF.Sin(ang + twist) + b.Y, hu * px, out float sc);
                float t = fl.Near(hu);
                float fade = DrawHelpers.Smooth(t / 0.24f) * (1f - DrawHelpers.Smooth((t - 0.90f) / 0.10f));
                float radial = DrawHelpers.Smooth((Vector2.Distance(at, f.Center) / f.Short - 0.26f) / 0.45f);
                float body = DrawHelpers.Lerp(0.04f, 0.56f, DrawHelpers.Smooth(t / 0.80f));
                float taper = 1f - 0.75f * u;
                CrackP[n] = at;
                CrackHalf[n] = Math.Clamp((2.8f + 2.2f * DrawHelpers.Hash01(s + 30 + i)) * px * sc * taper, 1.0f * px, 8f * px);
                CrackCol[n] = Col(new Vector3(0.005f, 0.008f, 0.012f), 1.0f * body * fade * radial * e.A * taper, e.Override);
                n++;
            }
            if (n < 3) continue;
            MeshBuilder.MiterNormals(CrackP.AsSpan(0, n), CrackN.AsSpan(0, n));
            Mb.Tent(CrackP.AsSpan(0, n), CrackN.AsSpan(0, n), CrackHalf.AsSpan(0, n), CrackCol.AsSpan(0, n));
        }
    }

    /// <summary>Grit on the wall: dark pits and pale mineral flecks, the finest surface scale.</summary>
    private static void DrawGrit(ImDrawListPtr dl, TunnelField f, in Env e)
    {
        var fl = f.Flight;
        float px = f.Px, s = f.Short;
        Mb.Clear();
        for (int i = 0; i < f.GritAt.Length; i++)
        {
            float hu = fl.HeightOf(f.GritAt[i], out int lap);
            float t = fl.Near(hu);
            if (t < 0.12f || t > 0.97f) continue;
            int sd = f.Seed + 20000 + i * 7919 + lap * 104729;

            float radius = DrawHelpers.Lerp(TunnelField.RibRadius, TunnelField.WallRadius, MathF.Sqrt(DrawHelpers.Hash01(sd + 1))) * s * f.Breath;
            float theta = DrawHelpers.Hash01(sd + 2) * MathF.Tau;
            float ang = theta + f.Swirl * hu + f.Roll;
            Vector2 b = f.Bend(hu);
            Vector2 at = f.Cam.Project(radius * MathF.Cos(ang) + b.X, radius * MathF.Sin(ang) + b.Y, hu * px, out float sc);

            float radial = DrawHelpers.Smooth((Vector2.Distance(at, f.Center) / s - 0.26f) / 0.45f);
            float fade = DrawHelpers.Smooth(t / 0.30f) * (1f - DrawHelpers.Smooth((t - 0.90f) / 0.07f));
            float size = DrawHelpers.HashRange(sd + 3, 1.3f, 3.6f) * px * sc;
            float k = e.A * radial * fade;
            if (k < 0.01f || size < 0.5f) continue;

            bool fleck = DrawHelpers.Hash01(sd + 4) < 0.38f;
            Vector3 rgb; float alpha;
            if (fleck)
            {
                // Mineral glints show only where light is in the air.
                float beam = Shaft(MathF.Atan2(at.Y - e.VanishingPoint.Y, at.X - e.VanishingPoint.X) - f.Roll * 0.35f, f.Time, f.Seed);
                rgb = new Vector3(0.72f, 0.90f, 0.94f);
                alpha = 0.60f * k * (0.05f + 0.95f * beam) * e.Light;
            }
            else
            {
                rgb = new Vector3(0.008f, 0.012f, 0.016f);
                alpha = 0.80f * k * DrawHelpers.HashRange(sd + 5, 0.4f, 1f);
            }
            if (alpha < 0.012f) continue;

            uint col = Col(rgb, MathF.Min(alpha, 0.9f), e.Override);
            float rot = DrawHelpers.Hash01(sd + 6) * MathF.Tau;
            Mb.Reserve(3, 3);
            int v0 = Mb.VertexReserved(at + new Vector2(MathF.Cos(rot), MathF.Sin(rot)) * size, col);
            Mb.VertexReserved(at + new Vector2(MathF.Cos(rot + 2.2f), MathF.Sin(rot + 2.2f)) * size * 0.9f, col);
            Mb.VertexReserved(at + new Vector2(MathF.Cos(rot + 4.2f), MathF.Sin(rot + 4.2f)) * size * 1.1f, col);
            Mb.TriReserved(v0, v0 + 1, v0 + 2);
            if (Mb.Full) Mb.Flush(dl, e.Uv);
        }
        Mb.Flush(dl, e.Uv);
    }

    /// <summary>The bright lip along a rib's inside edge, with a lens's red and cyan fringes at the frame edges.</summary>
    private static void Lip(TunnelField f, in Env e, float hu, int seed)
    {
        float px = f.Px, shortSide = f.Short;
        float t = f.Flight.Near(hu);
        float fade = DrawHelpers.Smooth(t / 0.24f) * (1f - DrawHelpers.Smooth((t - 0.90f) / 0.10f));
        float k = e.A * fade * (0.20f + 0.80f * MathF.Exp(-2.2f * t)) * e.Light;
        if (k < 0.006f) return;

        float h = hu * px, sc = f.Cam.ScaleAt(h);
        int segs = SegmentsFor(sc);
        var (cosT, sinT) = TurnTable(segs);
        float radius = f.RibPx, twist = f.Swirl * hu + f.Roll;
        float ct = MathF.Cos(twist), st = MathF.Sin(twist);
        Vector2 bend = f.Bend(hu);
        Vector2 centre = f.Cam.Project(bend.X, bend.Y, h, out _);
        float width = Math.Clamp(1.6f * px * sc, 1.2f * px, 6f * px);

        // A lens splits colours visibly only toward the frame edges; it fades in with rib nearness or shake.
        float fringe = MathF.Max(Near(sc, 0.65f, 1.05f), DrawHelpers.Smooth((f.Fringe - 1.8f) / 1.2f));
        int firstPass = fringe > 0.02f ? 0 : 2;
        for (int pass = firstPass; pass < 3; pass++)
        {
            // pass 0 = red outward, 1 = cyan inward, 2 = the lip itself
            Vector3 rgb = pass == 0 ? new Vector3(1.00f, 0.30f, 0.30f) : pass == 1 ? new Vector3(0.25f, 0.85f, 1.00f) : new Vector3(0.78f, 0.95f, 0.98f);
            float gain = pass == 2 ? 0.40f : 0.14f * fringe;
            for (int j = 0; j <= segs; j++)
            {
                float cx = cosT[j] * ct - sinT[j] * st, cy = sinT[j] * ct + cosT[j] * st;
                Vector2 at = f.Cam.Project(radius * cx + bend.X, radius * cy + bend.Y, h, out _);
                if (pass < 2)
                {
                    Vector2 d = at - centre; float l = d.Length();
                    if (l > 1f) at += d / l * ((pass == 0 ? 1f : -1f) * f.Fringe * px * (l / shortSide));
                }
                // The lip is chipped and dirty: it breaks up with the wall's own pitting.
                Vector3 tex = f.Wall.Sample(j % segs / (float)segs, (hu - f.Flight.FarHeight - f.Flight.Travel) / f.Flight.Length);
                float chip = DrawHelpers.Smooth((tex.Z * 1.2f + tex.X * 0.5f - 0.35f) / 0.5f);
                LipPts[j] = at;
                LipHalf[j] = width * (pass == 2 ? 1f : 0.8f);
                LipCol[j] = Col(rgb, k * gain * (0.35f + 0.65f * chip), e.Override);
            }
            MeshBuilder.MiterNormals(LipPts.AsSpan(0, segs + 1), LipNrm.AsSpan(0, segs + 1));
            Mb.Ribbon(LipPts.AsSpan(0, segs + 1), LipNrm.AsSpan(0, segs + 1), LipHalf.AsSpan(0, segs + 1), LipCol.AsSpan(0, segs + 1), 0.9f * px + width);
        }
    }

    // Light shafts

    /// <summary>Light in the air by angle round the far end: a few broad shafts and a lot of nothing.</summary>
    private static float Shaft(float angle, float time, int seed)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        float o = (seed & 0x3FF) * 0.173f;
        float n = 0.62f * Noise.Value(c * 1.7f + 31f + o, s * 1.7f + time * 0.045f) + 0.38f * Noise.Value(c * 3.9f + 77f + o, s * 3.9f - time * 0.07f);
        float x = DrawHelpers.Smooth((n + 0.12f) / 0.75f);

        // One shaft is always there: a key beam, drifting slowly round, that the noise adds to.
        float key = (seed & 0xFFFF) * 0.0731f + time * 0.035f;
        float d = MathF.Atan2(MathF.Sin(angle - key), MathF.Cos(angle - key));
        float lobe = MathF.Exp(-d * d / 0.10f) * (0.85f + 0.15f * MathF.Sin(time * 0.31f));
        x = MathF.Max(x * x * (0.6f + 0.4f * x), lobe);
        return x;
    }

    private static void DrawShafts(ImDrawListPtr dl, TunnelField f, in Env e)
    {
        const int Spokes = 144, Rows = 6;
        float s = f.Short, k = e.A * e.Light;
        if (k < 0.004f) return;

        // Out from the far end: nothing at the very middle, full by mid-screen, thinning past the edges.
        Span<float> radius = stackalloc float[Rows] { 0.10f * s, 0.26f * s, 0.46f * s, 0.74f * s, 1.05f * s, 1.45f * s };
        Span<float> profile = stackalloc float[Rows] { 0f, 0.55f, 0.90f, 1.0f, 0.85f, 0.45f };
        Mb.Clear();
        Mb.Reserve(Rows * (Spokes + 1), (Rows - 1) * Spokes * 6);
        int first = -1;
        for (int row = 0; row < Rows; row++)
        {
            for (int j = 0; j <= Spokes; j++)
            {
                float a = MathF.Tau * (j % Spokes) / Spokes;
                float beam = Shaft(a, f.Time, f.Seed);
                // Dust drifts through a shaft in clouds: thick, thin, thick again, slowly moving outward.
                float cloud = 0.62f + 0.38f * Noise.Value(a * 2.3f + row * 1.9f - f.Time * 0.09f + 12f, row * 0.9f + (f.Seed & 255) * 0.01f);
                float alpha = profile[row] * 0.30f * k * (0.04f + 0.96f * beam) * cloud;
                float th = MathF.Tau * j / Spokes + f.Roll * 0.35f;
                int vi = Mb.VertexReserved(e.VanishingPoint + new Vector2(MathF.Cos(th), MathF.Sin(th)) * radius[row], Col(ShaftTint, alpha, e.Override));
                if (first < 0) first = vi;
            }
        }
        Mb.StitchGrid(first, Rows, Spokes + 1);
        Mb.Flush(dl, e.Uv);
    }

    // Haze

    private static void DrawHaze(ImDrawListPtr dl, TunnelField f, in Env e)
    {
        float s = f.Short;
        Span<float> rr = stackalloc float[2];  // outside the loop: stackalloc in a loop grows the stack
        Span<uint> rc = stackalloc uint[2];
        for (int i = 0; i < TunnelField.BillowCount; i++)
        {
            float hu = f.Flight.HeightOf(f.BillowAt[i], out int lap);
            float t = f.Flight.Near(hu);
            int sd = f.Seed + 12000 + i * 7919 + lap * 104729;
            float lane = DrawHelpers.HashRange(sd + 1, 0.25f, 0.95f) * s;
            float ang = DrawHelpers.Hash01(sd + 2) * MathF.Tau + f.Swirl * hu + f.Roll;
            Vector2 bz = f.Bend(hu);
            Vector2 at = f.Cam.Project(lane * MathF.Cos(ang) + bz.X, lane * MathF.Sin(ang) + bz.Y, hu * f.Px, out float sc);

            float fade = DrawHelpers.Smooth(t / 0.20f) * (1f - DrawHelpers.Smooth((t - 0.80f) / 0.20f));
            float k = e.A * fade * DrawHelpers.HashRange(sd + 3, 0.5f, 1f) * (0.4f + 0.6f * f.Stress) * (1f + 2.6f * e.Dusty);
            float radius = DrawHelpers.HashRange(sd + 4, 220f, 420f) * f.Px * sc * (1f + 0.45f * e.Dusty);
            if (k < 0.004f || radius < 8f) continue;

            rr[0] = radius * 0.5f; rr[1] = radius;
            rc[0] = Col(DustTint * 0.62f, 0.060f * k, e.Override); rc[1] = Col(DustTint * 0.5f, 0f, e.Override);
            MeshDraw.Radial(dl, at, e.Uv, Col(DustTint * 0.70f, 0.085f * k, e.Override), 14, rr, rc, DrawHelpers.Hash01(sd + 5) * MathF.Tau, 0.82f, sd, 0.5f);
        }
    }

    // Shudder

    private static readonly Vector3 FallingDust = new(0.74f, 0.72f, 0.64f);

    /// <summary>The shudder: a warm dust curtain from the screen top and streaked grit (a pure function of shudder age).</summary>
    private static void DrawShudder(ImDrawListPtr dl, TunnelField f, in Env e)
    {
        if (f.Vision != 5 || f.Env < 0.01f) return;
        float k = e.A * f.Env;
        Vector2 size = f.Size;
        float px = f.Px;

        Mb.Clear();

        // The curtain: dust above, thickest at the top edge and thinning toward the middle, billowing a little.
        const int Cols = 12;
        Mb.Reserve(2 * (Cols + 1), Cols * 6);
        int first = -1;
        for (int row = 0; row < 2; row++)
        {
            for (int j = 0; j <= Cols; j++)
            {
                float x = size.X * j / Cols;
                float billow = 0.65f + 0.35f * Noise.Value(j * 0.8f + f.Time * 0.35f, 3f);
                float depth = size.Y * (0.34f + 0.12f * Noise.Value(j * 0.6f - f.Time * 0.2f, 9f));
                int vi = Mb.VertexReserved(new Vector2(x, row == 0 ? 0f : depth), Col(FallingDust, row == 0 ? 0.46f * k * billow : 0f, e.Override));
                if (first < 0) first = vi;
            }
        }
        Mb.StitchGrid(first, 2, Cols + 1);

        // Grit falling through it, each at its own speed, streaked like a falling speck at this shutter.
        for (int i = 0; i < 90; i++)
        {
            int sd = f.Seed + 30000 + i * 7919;
            float x0 = DrawHelpers.Hash01(sd + 1) * size.X;
            float v = DrawHelpers.HashRange(sd + 2, 110f, 480f) * px;
            float y = -DrawHelpers.Hash01(sd + 3) * 0.7f * size.Y + v * f.VisionAge;
            if (y < -20f || y > size.Y + 40f) continue;

            // Mostly fine grit, a few big soft flakes; they sway as they fall.
            float big = MathF.Pow(DrawHelpers.Hash01(sd + 4), 2.6f);
            float sz = (1.0f + 6.0f * big) * px;
            float len = v * 0.026f + sz * 1.8f;
            float x = x0 + MathF.Sin(f.VisionAge * DrawHelpers.HashRange(sd + 7, 0.8f, 2.2f) + sd) * (10f + 26f * big) * px;
            float inView = DrawHelpers.Smooth((y + 20f) / (0.12f * size.Y)) * (1f - DrawHelpers.Smooth((y - size.Y * 0.85f) / (0.2f * size.Y)));
            float alpha = MathF.Min(0.8f, 0.9f * k * DrawHelpers.HashRange(sd + 5, 0.3f, 1f) * inView * (1f - 0.55f * big));
            if (alpha < 0.02f) continue;

            uint core = Col(Vector3.Lerp(FallingDust, new Vector3(1f, 0.97f, 0.88f), DrawHelpers.Hash01(sd + 6)), alpha, e.Override), edge = Col(FallingDust, 0f, e.Override);
            Mb.Reserve(9, 24);
            int v0 = Mb.VertexReserved(new Vector2(x - sz, y - len), edge);
            Mb.VertexReserved(new Vector2(x, y - len), Col(FallingDust, alpha * 0.1f, e.Override)); Mb.VertexReserved(new Vector2(x + sz, y - len), edge);
            Mb.VertexReserved(new Vector2(x - sz * 1.2f, y - len * 0.45f), edge); Mb.VertexReserved(new Vector2(x, y - len * 0.45f), core); Mb.VertexReserved(new Vector2(x + sz * 1.2f, y - len * 0.45f), edge);
            Mb.VertexReserved(new Vector2(x - sz, y), edge); Mb.VertexReserved(new Vector2(x, y + sz * 0.5f), core); Mb.VertexReserved(new Vector2(x + sz, y), edge);
            Mb.StitchGrid(v0, 3, 3);
            if (Mb.Full) Mb.Flush(dl, e.Uv);
        }
        Mb.Flush(dl, e.Uv);
    }

    // Dust

    private static void DrawDust(ImDrawListPtr dl, TunnelField f, in Env e)
    {
        var fl = f.Flight;
        float px = f.Px, s = f.Short;
        // Exposure length: how far the fall carries a speck (a smear at cruise, a streak at a surge).
        float travel = f.FlightSpeed * 0.060f;

        Mb.Clear();
        for (int i = 0; i < f.DustAt.Length; i++)
        {
            float hu = fl.HeightOf(f.DustAt[i], out int lap);
            float t = fl.Near(hu);
            int sd = f.Seed + 8000 + i * 7919 + lap * 104729;

            float lane = (0.10f + 0.80f * MathF.Sqrt(DrawHelpers.Hash01(sd + 1))) * s;
            float theta = DrawHelpers.Hash01(sd + 2) * MathF.Tau;
            float ang = theta + f.Swirl * hu + f.Roll;
            Vector2 bh = f.Bend(hu), bt = f.Bend(hu - travel);
            Vector2 head = f.Cam.Project(lane * MathF.Cos(ang) + bh.X, lane * MathF.Sin(ang) + bh.Y, hu * px, out float sc);
            float angT = theta + f.Swirl * (hu - travel) + f.Roll;
            Vector2 tail = f.Cam.Project(lane * MathF.Cos(angT) + bt.X, lane * MathF.Sin(angT) + bt.Y, (hu - travel) * px, out _);

            // Dust only shows where you are looking away from the middle, and there it is bright inside a shaft.
            float fromMid = Vector2.Distance(head, f.Center) / s;
            float mask = DrawHelpers.Smooth((fromMid - 0.15f) / 0.18f);
            float beam = Shaft(MathF.Atan2(head.Y - e.VanishingPoint.Y, head.X - e.VanishingPoint.X) - f.Roll * 0.35f, f.Time, f.Seed);
            float fade = DrawHelpers.Smooth(t / 0.10f) * (1f - DrawHelpers.Smooth((t - 0.93f) / 0.07f));

            // Focus sits at about the middle of the tunnel; nearer or farther is a soft disc, spreading its light.
            float blur = MathF.Abs(t - 0.58f) / 0.42f;
            float soft = 1f + 3.2f * blur * blur * blur;
            float size = DrawHelpers.HashRange(sd + 5, 1.5f, 4.2f) * px * sc * soft * (1f + 0.35f * e.Dusty);
            float k = e.A * mask * fade * DrawHelpers.HashRange(sd + 3, 0.30f, 1.00f) * (0.22f + 0.78f * beam) * (1f / (0.6f + 0.4f * soft * soft)) * 2.4f * (1f + 1.2f * e.Dusty);
            if (k < 0.010f || size < 0.5f) continue;

            Vector2 dir = head - tail; float len = dir.Length();
            float minLen = size * 2.2f;
            if (len < 1e-3f) { dir = Vector2.Normalize(head - e.VanishingPoint); len = 0f; }
            else dir /= len;
            len = MathF.Max(len, minLen);
            Vector2 side = new Vector2(-dir.Y, dir.X) * size;
            Vector2 back = head - dir * len, mid = head - dir * (len * 0.45f);

            Vector3 rgb = Vector3.Lerp(DustTint * 0.85f, new Vector3(1.0f, 0.97f, 0.88f), beam);
            uint core = Col(rgb, MathF.Min(0.85f, k), e.Override), edge = Col(rgb, 0f, e.Override), tailC = Col(rgb, MathF.Min(0.85f, k) * 0.12f, e.Override);
            Mb.Reserve(9, 24);
            int v0 = Mb.VertexReserved(back - side, edge);
            Mb.VertexReserved(back, tailC); Mb.VertexReserved(back + side, edge);
            Mb.VertexReserved(mid - side * 1.15f, edge); Mb.VertexReserved(mid, core); Mb.VertexReserved(mid + side * 1.15f, edge);
            Mb.VertexReserved(head - side * 0.9f, edge); Mb.VertexReserved(head + dir * (size * 0.5f), core); Mb.VertexReserved(head + side * 0.9f, edge);
            Mb.StitchGrid(v0, 3, 3);
            if (Mb.Full) Mb.Flush(dl, e.Uv);
        }
        Mb.Flush(dl, e.Uv);
    }
}
