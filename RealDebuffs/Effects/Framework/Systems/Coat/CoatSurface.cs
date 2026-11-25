namespace RealDebuffs.Effects.Framework;

/// One chart point: position, orientation, and screen directions of rising U/V scaled by each 3D step; flow reads their Y as slope.
public struct CoatSurfaceSample
{
    public Vector2 Pos, Du, Dv, Anchor;  // Anchor: where a drop hangs from if one forms here
    public Vector3 Normal;  // the host's shading normal (z toward the viewer)
    public float Side, GravityDown, Radius;  // painter key -1 back..+1 front; how squarely the exposed downhill edge faces down (0 inside); host half-thickness
    public float Z;  // depth of Pos toward the viewer, px (0 on a flat host); with Normal and Radius it fixes the host's axis, which contact is judged on
    public bool Visible, Holds;  // on screen; the host can hold matter here
    public float Slick;  // 0..1, 0 if the host has none: how badly this surface holds liquid arriving on it (1 = none sticks). Supply here is scaled by 1 - Slick, so the substrate stays bare where it is high.
    public float Pool;  // -1..1, 0 if the host has none: +1 a groove (concave), -1 a ridge (convex); liquid settles thicker where it is higher, by the matter's PoolDepth in hold thicknesses per unit
}

public enum CoatEdge : byte { UStart, UEnd, VStart, VEnd }  // a chart's four sides: the first and last row, the first and last sector

/// A chart side that may touch another's: the host proposes, the coat accepts only on real contact; a Seam is one surface cut in two (across leaves this chart).
public struct CoatContact { public CoatEdge Edge, OtherEdge; public int OtherId; public bool Seam; }

/// A patch of host surface the coat lies on: U along it (rows), V around or across (sectors), filled by hosts from the geometry they draw.
public sealed class CoatChart
{
    public int ChartId, UCells, VSectors, Live;  // stable id (host-assigned, 1+; 0 is a whole-strand tube); sizes; rows laid this frame
    public bool SeamU, SeamV;  // a ring along U (closed strand); around V (a tube)
    public float Ds;  // mean row length along U
    public float SupplyScale = 1f;  // share of a strand's per-length supply this surface takes, when it covers a stretch of strand twice or only partly
    public float[] UPos = [], UStep = [];  // per row: arc position and length of the row's cell, px
    public float[] VAngle = [], VStep = [];  // per sector: angle across the host, width of the sector, px
    public CoatSurfaceSample[] S = [];  // UCells * VSectors, row-major: [u * VSectors + v]
    public Vector2 TipP, TipT, HeadP, HeadT;  // a strand's two ends (tubes only): either can be the low one
    public const int MaxContacts = 6;
    public readonly CoatContact[] Contacts = new CoatContact[MaxContacts];
    public int ContactCount;

    /// Proposes that this chart's side touches another chart's; false if the chart has no room for more.
    public bool AddContact(CoatEdge mine, int otherId, CoatEdge theirs, bool seam)
    {
        if (ContactCount >= MaxContacts) return false;
        Contacts[ContactCount++] = new() { Edge = mine, OtherId = otherId, OtherEdge = theirs, Seam = seam };
        return true;
    }

    internal void Begin(int id, int u, int v)
    {
        ChartId = id; UCells = u; VSectors = v; Live = u; SeamU = SeamV = false; SupplyScale = 1f; ContactCount = 0;
        if (UPos.Length < u) { UPos = new float[u]; UStep = new float[u]; }
        if (VAngle.Length < v) { VAngle = new float[v]; VStep = new float[v]; }
        if (S.Length < u * v) S = new CoatSurfaceSample[u * v];
    }
}

/// A reusable list of this frame's charts for one stroke: the provider requests and fills them, nothing allocates once warm.
public sealed class CoatCharts
{
    readonly List<CoatChart> _pool = new();
    int _n;
    public int Count => _n;
    public CoatChart this[int i] => _pool[i];
    public int CellsLeft, Share;  // cells the frame may still spend; and this stroke's fair share (budget over chart-using strokes): a provider coarsens before it skips
    public int Skipped;  // charts a provider left out for want of cells (diagnostic)

    internal void Clear() { _n = 0; }

    /// A chart of u x v cells, or null if there is no room for it this frame.
    public CoatChart? Add(int chartId, int u, int v)
    {
        if (u * v > CellsLeft) { Skipped++; return null; }
        CellsLeft -= u * v;
        if (_n == _pool.Count) _pool.Add(new());
        var c = _pool[_n++];
        c.Begin(chartId, u, v);
        return c;
    }
}

public readonly struct CoatBuildContext
{
    public readonly Vector2 Screen;
    public readonly float ShortSide, Px, Time;
    public CoatBuildContext(Vector2 screen, float shortSide, float px, float time) { Screen = screen; ShortSide = shortSide; Px = px; Time = time; }
}

/// A stroke material that lays its coat on the geometry it draws. Without it a stroke is coated as a tube along its path.
public interface ICoatSurfaceProvider : IStrokeMaterial
{
    /// True if this host builds charts for that matter (others fall back to the tube).
    bool Charts(CoatMatter matter);

    /// Once a frame, before the host draws: fills the charts from the host's own geometry snapshot.
    void BuildCoatSurfaces(in StrokePrimitive stroke, in CoatBuildContext ctx, CoatCharts charts);
}

/// The fallback chart: one sector wrapped round the strand, cut straight from the host path's raw samples.
internal static class TubeChart
{
    internal const int MaxU = 192;

    /// Lays one chart along the path: even arc-step rows with ends, tangent and side from the path; prevU stops the row count flickering.
    internal static void Build(CoatChart ch, in StrokePrimitive s, IStrokeMaterial? mat, in CoatBuildContext ctx)
    {
        var path = s.Path;
        float len = path.Length, px = ctx.Px;
        int want = Math.Clamp((int)(len / (10f * px)), 10, MaxU), prev = ch.UCells;
        int n = prev == 0 || (want <= prev * 5 / 4 && want >= prev * 4 / 5) ? (prev == 0 ? want : prev) : want;
        ch.Begin(0, n, 1);
        ch.SeamU = s.Closed;
        float ds = len / n;
        ch.Ds = ds;
        float vis = len * s.Reveal;
        int live = Math.Clamp((int)(vis / ds + 0.5f), 0, n);
        ch.Live = live;
        ch.VAngle[0] = 0f; ch.VStep[0] = 1f;

        for (int i = 0; i < n; i++) { ch.UPos[i] = (i + 0.5f) * ds; ch.UStep[i] = ds; }
        for (int i = 0; i < live; i++)
        {
            ref var c = ref ch.S[i];
            path.SampleAtArc((i + 0.5f) * ds, out c.Pos, out c.Du);
        }

        Vector2 prevSide = default;
        for (int i = 0; i < live; i++)
        {
            ref var c = ref ch.S[i];
            Vector2 n2 = new(-c.Du.Y, c.Du.X);
            if (i == 0 ? n2.Y < 0f : Vector2.Dot(n2, prevSide) < 0f) n2 = -n2;  // a side that never flips along the strand, so strips never twist
            c.Dv = prevSide = n2;
            Vector2 under = n2.Y >= 0f ? n2 : -n2;  // the underside faces the floor
            c.Normal = new(under.X, under.Y, 0f);
            c.GravityDown = Abs(n2.Y);
            c.Radius = mat?.RadiusAt(in s, (i + 0.5f) * ds, ctx.ShortSide) ?? Max(1f, s.WidthHint * 0.5f);
            float m = c.Radius + 20f * px;
            c.Holds = mat?.CanDripAt(in s, (i + 0.5f) * ds, ctx.ShortSide) ?? true;
            c.Visible = ctx.Screen.X <= 0f || (c.Pos.X > -m && c.Pos.X < ctx.Screen.X + m && c.Pos.Y > -m && c.Pos.Y < ctx.Screen.Y + m);
            c.Anchor = c.Pos + under * (c.Radius * 0.78f);
        }
        path.SampleAtArc(vis, out ch.TipP, out ch.TipT);
        path.SampleAtArc(0f, out ch.HeadP, out ch.HeadT);
    }
}
