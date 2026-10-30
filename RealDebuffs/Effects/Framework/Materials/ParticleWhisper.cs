using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>A whispered phrase in glowing script. Size = x-height px; Variant = phrase + 1 (10 bits) | depth (2 bits: 0 near, 1 mid, 2 far) | how it arrives (3 bits) | how it leaves (3 bits); Velocity = the way it is breathed off.</summary>
public sealed class ParticleWhisper : IParticleMaterial
{
    public string Name => "particle.whisper";
    public string[] NaturalLanguageWords { get; } = { "whisper", "whispers", "murmur", "murmurs", "words", "script" };

    public const int PhraseBits = 10, DepthShift = 10, ArriveShift = 12, DepartShift = 15;

    // How a phrase arrives and leaves: the order its letters go in, how far that order is spread over the phrase's life, and how long each letter takes.
    // Each letter fades and moves over the same stretch, from fully transparent to settled (or back), so Spread + Span is the whole arrival (or departure).
    public const int Arrivals = 6, Departures = 6;
    private enum Order { Forward, Backward, CentreOut, EdgesIn, Random, Together }
    private static readonly (Order Order, float Spread, float Span)[] Arrive =
    {
        (Order.Forward, 0.20f, 0.20f),   // 0 rises from below, letter by letter
        (Order.Together, 0.06f, 0.34f),  // 1 blooms into focus from a heavy blur, all at once
        (Order.Random, 0.22f, 0.18f),    // 2 surfaces from the fog in no order
        (Order.CentreOut, 0.18f, 0.22f), // 3 unfolds from the middle outward
        (Order.Forward, 0.18f, 0.22f),   // 4 slides in on the wind
        (Order.Forward, 0.30f, 0.10f),   // 5 is spoken, each letter with a flare
    };
    private static readonly (Order Order, float Spread, float Span)[] Depart =
    {
        (Order.Forward, 0.16f, 0.22f),   // 0 is breathed away downwind, letter by letter
        (Order.Random, 0.16f, 0.22f),    // 1 sinks
        (Order.Together, 0.05f, 0.33f),  // 2 evaporates where it hangs
        (Order.Together, 0.06f, 0.32f),  // 3 rushes toward you and dissolves
        (Order.Random, 0.18f, 0.20f),    // 4 scatters
        (Order.EdgesIn, 0.16f, 0.22f),   // 5 folds in to the middle
    };
    private const float DepartAt = 0.62f;

    /// <summary>One letter's moment: how opaque it is, how soft, how it is displaced, the glow's slow pulse, and the flare of being spoken.</summary>
    private struct Letter { public float Focus, Soft, Pop, Glimmer, Spread, Rise; public Vector2 Shift; }

    private const float Slant = 0.20f, Weight = 0.072f;
    private static readonly MeshBuilder Mb = new();

    // Offsets across a stroke, in sigmas: a soft glowing line that falls off like a gaussian.
    private static readonly float[] LineAt = { -2.6f, -1.45f, -0.7f, 0f, 0.7f, 1.45f, 2.6f };
    private static readonly (float C, float S)[] Ring = BuildRing();

    private static (float, float)[] BuildRing()
    {
        var r = new (float, float)[24];
        for (int i = 0; i < r.Length; i++) r[i] = (MathF.Cos(MathF.Tau * i / r.Length), MathF.Sin(MathF.Tau * i / r.Length));
        return r;
    }

    /// <summary>Where and how a phrase sits: its centre, size, turn about its vertical axis and rotation on screen.</summary>
    private readonly struct Pose
    {
        public readonly Vector2 At;
        public readonly float Unit, CosYaw, SinYaw, InvDist, CosR, SinR;

        public Pose(Vector2 at, float unit, float yaw, float width, float rotation)
        {
            At = at; Unit = unit; CosYaw = MathF.Cos(yaw); SinYaw = MathF.Sin(yaw);
            InvDist = 1f / (2.4f * MathF.Max(width, 2f));
            CosR = MathF.Cos(rotation); SinR = MathF.Sin(rotation);
        }

        /// <summary>A point in letter space (x-heights) to the screen; the end turned toward us is the larger.</summary>
        public Vector2 Map(float lx, float ly, float spread, Vector2 away)
        {
            float sc = spread / (1f - lx * SinYaw * InvDist);
            float gx = lx * CosYaw * sc, gy = ly * sc;
            return At + away + new Vector2(gx * CosR - gy * SinR, gx * SinR + gy * CosR) * Unit;
        }

        public Vector2 Turn(Vector2 v) => new(v.X * CosR - v.Y * SinR, v.X * SinR + v.Y * CosR);
    }

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 2f) return;

        var phrases = CharmWhispers.Phrases;
        int pick = (p.Variant & ((1 << PhraseBits) - 1)) - 1;
        if (pick < 0) pick = (int)(DrawHelpers.Hash01(p.Seed + 3) * phrases.Length);
        string text = phrases[pick % phrases.Length];
        int depth = (p.Variant >> DepthShift) & 3;
        int n = Math.Min(text.Length, 40);

        float unit = p.Size, px = ctx.ScreenScale, t = p.AgeRatio;
        float width = ScriptFont.Width(text);
        bool overridden = DrawHelpers.ColorOverrideActive;

        int inS = ((p.Variant >> ArriveShift) & 7) % Arrivals, outS = ((p.Variant >> DepartShift) & 7) % Departures;
        float seedPhase = DrawHelpers.Hash01(p.Seed + 4) * MathF.Tau;
        bool flip = DrawHelpers.Hash01(p.Seed + 9) < 0.5f;

        Span<Letter> letters = stackalloc Letter[n];
        float cursor = -width * 0.5f;
        for (int i = 0; i < n; i++)
        {
            var g = ScriptFont.Of(text[i]);
            float mid = cursor + g.Advance * 0.5f;  // x-heights from the phrase's centre
            cursor += g.Advance + ScriptFont.Tracking;

            var (ao, asp, adu) = Arrive[inS];
            if (inS == 4 && flip) ao = Order.Backward;
            float start = asp * OrderOf(ao, i, n, p.Seed);
            float rise = DrawHelpers.Saturate((t - start) / adu);
            float arrived = DrawHelpers.Smooth(rise), come = (1f - rise) * (1f - rise);  // it fades in evenly but slows as it settles

            var (dro, dsp, ddu) = Depart[outS];
            float dis = DrawHelpers.Smooth((t - DepartAt - dsp * OrderOf(dro, i, n, p.Seed + 77)) / ddu);

            int h = p.Seed * 131 + i * 17;
            var L = new Letter { Focus = arrived * (1f - dis), Spread = 1f, Glimmer = 1f + 0.15f * MathF.Sin(ctx.Time * 2.1f + seedPhase + i * 0.7f) };

            switch (inS)
            {
                case 0: L.Rise = 0.55f * come; break;
                case 1: L.Spread += 0.20f * come; break;
                case 2: L.Shift += new Vector2(DrawHelpers.HashRange(h + 2, -1f, 1f), DrawHelpers.HashRange(h + 3, -1f, 1f)) * (0.5f * unit * come); break;
                case 3: L.Shift.X -= mid * 0.30f * unit * come; break;
                case 4: L.Shift.X += (flip ? -1f : 1f) * 1.5f * unit * come; break;
                case 5: L.Pop = t >= start ? MathF.Exp(-(t - start) / 0.05f) : 0f; break;
            }

            switch (outS)
            {
                case 0: L.Shift += p.Velocity * (dis * 1.4f) + new Vector2(0f, -0.7f * unit * dis); L.Spread += 0.14f * dis; break;
                case 1: L.Shift += new Vector2(MathF.Sin(h) * 0.12f * unit * dis, unit * dis * dis); break;
                case 2: L.Shift.Y -= 0.12f * unit * dis; L.Spread += 0.05f * dis; break;
                case 3: L.Spread += 0.60f * dis; break;
                case 4: { float a = DrawHelpers.Hash01(h + 4) * MathF.Tau; L.Shift += new Vector2(MathF.Cos(a), MathF.Sin(a) - 0.3f) * (unit * dis); break; }
                case 5: L.Shift.X -= mid * 0.60f * unit * dis; break;
            }

            // The more transparent, the softer; blooming in and evaporating out go through an extra-soft blur.
            L.Soft = MathF.Min(1.5f, MathF.Max((1f - arrived) * (inS == 1 ? 1.5f : 1f), dis * (outS == 2 ? 1.5f : 1f)));
            letters[i] = L;
        }

        // The phrase hangs a little askew, turned about its vertical axis so one end is nearer, and swivels slowly.
        var pose = new Pose(p.DrawPos, unit, DrawHelpers.HashRange(p.Seed + 5, -0.70f, 0.70f) + 0.14f * MathF.Sin(ctx.Time * 0.35f + seedPhase), width, p.Rotation);
        // Shallow depth of field: the middle distance is in focus, the near and far are soft.
        float blur = depth == 0 ? 1.9f : depth == 1 ? 1.0f : 1.6f;
        uint plumC = CharmColor.Vignette, haloC = CharmColor.Glow.Sample(0.34f), roseC = CharmColor.Glow.Sample(0.54f), blushC = CharmColor.Glow.Sample(0.80f), pearlC = CharmColor.Glow.Sample(0.99f);

        // Two passes over every letter (halos, then bodies), so one letter's glow never sits over another's core.
        Span<uint> line = stackalloc uint[7];
        Mb.Clear();
        for (int pass = 0; pass < 2; pass++)
        {
            float x = -width * 0.5f;
            for (int i = 0; i < n; i++)
            {
                var g = ScriptFont.Of(text[i]);
                float x0 = x;
                x += g.Advance + ScriptFont.Tracking;
                var L = letters[i];
                float f = L.Focus;
                if (f <= 0.02f || g.Points.Length == 0) continue;

                // A little handwriting in each letter, on top of however it is arriving or leaving.
                float lift = 0.5f + L.Rise + DrawHelpers.HashRange(p.Seed * 131 + i * 17 + 1, -0.05f, 0.05f);
                float spread = L.Spread;
                Vector2 away = L.Shift;
                float soft = L.Soft;
                float aCore = k * MathF.Pow(f, 1.5f), aBody = k * 0.92f * f * (1.1f - 0.12f * blur);
                float haloReach = unit * MathF.Min(1.1f, (0.34f + 0.55f * soft + 0.5f * L.Pop) * blur);

                if (pass == 0)
                {
                    if (depth < 2) Glow(g, in pose, x0, lift, spread, away, haloReach * 0.55f, DrawHelpers.Tint(plumC, aBody * 0.36f, overridden), DrawHelpers.Tint(plumC, aBody * 0.16f, overridden));
                    Glow(g, in pose, x0, lift, spread, away, haloReach, DrawHelpers.Tint(haloC, aBody * L.Glimmer * (0.20f + 0.5f * L.Pop), overridden), DrawHelpers.Tint(haloC, aBody * L.Glimmer * (0.09f + 0.25f * L.Pop), overridden));
                }
                else
                {
                    // Rose out at the edges, pearl at the heart; defocus fattens the line and thins its light.
                    uint clear = DrawHelpers.Tint(roseC, 0f, overridden);
                    uint o1 = DrawHelpers.Tint(roseC, aBody * 0.22f, overridden), o2 = DrawHelpers.Tint(blushC, aBody * 0.65f, overridden), mid = DrawHelpers.Tint(pearlC, aCore, overridden);
                    line[0] = line[6] = clear; line[1] = line[5] = o1; line[2] = line[4] = o2; line[3] = mid;
                    float sigma = unit * Weight * (1f + 0.9f * soft + 0.7f * L.Pop) * (0.88f + 0.16f * blur) + 0.35f * px;
                    Ribbons(g, in pose, x0, lift, spread, away, line, sigma);
                }
            }
        }
        Mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));
    }

    // Where a letter sits in the order its phrase goes in, 0 to 1.
    private static float OrderOf(Order order, int i, int n, int seed)
    {
        float s = n > 1 ? i / (float)(n - 1) : 0f, c = MathF.Abs(2f * s - 1f);
        return order switch
        {
            Order.Forward => s,
            Order.Backward => 1f - s,
            Order.CentreOut => c,
            Order.EdgesIn => 1f - c,
            Order.Random => DrawHelpers.Hash01(seed + i * 7 + 1),
            _ => DrawHelpers.Hash01(seed + i * 7 + 1) * 0.3f,
        };
    }

    // Soft round glows strung along a letter's strokes: a ribbon as wide as a glow would fold over itself at every tight curve.
    private static void Glow(ScriptFont.Glyph g, in Pose pose, float x0, float lift, float spread, Vector2 away, float radius, uint centre, uint shoulder)
    {
        int rings = radius > 36f ? 2 : 1, segs = rings == 2 ? 12 : 8, step = Ring.Length / segs;
        uint clear = centre & 0x00FFFFFFu;
        float gap = radius * 0.8f;

        for (int s = 0; s < g.Points.Length; s++)
        {
            var pts = g.Points[s];
            float walked = gap;
            for (int i = 0; i < pts.Length; i++)
            {
                if (i > 0) walked += Vector2.Distance(pts[i], pts[i - 1]) * pose.Unit;
                if (walked < gap) continue;
                walked = 0f;

                Vector2 c = pose.Map(x0 + pts[i].X - Slant * pts[i].Y, pts[i].Y + lift, spread, away);
                Mb.Reserve(1 + rings * segs, segs * 3 + (rings - 1) * segs * 6);
                int hub = Mb.VertexReserved(c, centre);
                for (int r = 0; r < rings; r++)
                {
                    float reach = radius * (r + 1) / rings;
                    uint col = r == rings - 1 ? clear : shoulder;
                    for (int j = 0; j < segs; j++) Mb.VertexReserved(c + new Vector2(Ring[j * step].C, Ring[j * step].S) * reach, col);
                }
                for (int j = 0; j < segs; j++) Mb.TriReserved(hub, hub + 1 + j, hub + 1 + (j + 1) % segs);
                for (int r = 0; r < rings - 1; r++)
                {
                    int a = hub + 1 + r * segs, b = a + segs;
                    for (int j = 0; j < segs; j++) Mb.QuadReserved(a + j, a + (j + 1) % segs, b + j, b + (j + 1) % segs);
                }
            }
        }
    }

    // One letter's strokes as a soft line, narrowed wherever the stroke curves too tightly for its full width.
    private static void Ribbons(ScriptFont.Glyph g, in Pose pose, float x0, float lift, float spread, Vector2 away, ReadOnlySpan<uint> col, float sigma)
    {
        int per = LineAt.Length;

        for (int s = 0; s < g.Points.Length; s++)
        {
            var pts = g.Points[s]; var nrm = g.Normals[s]; var wid = g.Width[s]; var room = g.Room[s];
            int m = pts.Length, first = -1;
            Mb.Reserve(m * per, (m - 1) * (per - 1) * 6);

            for (int i = 0; i < m; i++)
            {
                Vector2 c = pose.Map(x0 + pts[i].X - Slant * pts[i].Y, pts[i].Y + lift, spread, away);
                Vector2 nv = pose.Turn(nrm[i]);
                float sc = sigma * wid[i];
                sc *= Math.Clamp(room[i] * pose.Unit / (LineAt[per - 1] * sc), 0.35f, 1f);

                for (int q = 0; q < per; q++)
                {
                    int v = Mb.VertexReserved(c + nv * (LineAt[q] * sc), col[q]);
                    if (first < 0) first = v;
                }
            }

            for (int i = 0; i + 1 < m; i++)
            {
                int a = first + i * per, b = a + per;
                for (int q = 0; q < per - 1; q++) Mb.QuadReserved(a + q, a + q + 1, b + q, b + q + 1);
            }
        }
    }
}
