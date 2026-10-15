using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A small electric discharge: a kinked line of light that re-rolls its shape about sixteen times a second and lives only a moment. Size is its half-length in px; Rotation sets its direction.
/// Small ones are a plain zigzag (cheap enough to swarm), big ones (or Variant 1) grow side branches like a Lichtenberg figure. Used as a stroke emitter it crawls along a strand; swapped onto a flower
/// or an ember slot it makes them crackle.
/// </summary>
public sealed class ParticleArc : IParticleMaterial
{
    public string Name => "particle.arc";
    public string[] NaturalLanguageWords { get; } = { "crackle", "crackles", "zaps" };

    private static readonly MeshBuilder Mb = new();

    // Below this half-length (px at 1080p) an arc is drawn as three plain lines: it can't show more.
    private const float LodSmall = 5f;
    // At or above this one grows side branches.
    private const float BranchFrom = 16f;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 1f) return;

        // A strike is sudden: up in a blink, out over the second half.
        float life = DrawHelpers.Smooth(p.AgeRatio / 0.15f) * (1f - DrawHelpers.Smooth((p.AgeRatio - 0.5f) / 0.5f));
        k *= life;
        if (k <= 0.004f) return;

        float px = ctx.ScreenScale;
        float half = p.Size * (0.75f + 0.25f * life);
        Vector2 pos = p.DrawPos;
        Vector2 dir = new(MathF.Sin(p.Rotation), -MathF.Cos(p.Rotation));
        int tick = (int)MathF.Floor(ctx.Time * 16f + DrawHelpers.Hash01(p.Seed + 3) * 8f);
        bool overridden = DrawHelpers.ColorOverrideActive;

        if (half < LodSmall * px)
        {
            DrawSmall(dl, pos, dir, half, k, p.Seed, tick);
            return;
        }

        bool branching = p.Variant == 1 || half >= BranchFrom * px;
        int m = half >= 12f * px ? 7 : 5;

        Span<Vector2> pts = stackalloc Vector2[7];
        Span<Vector2> nrm = stackalloc Vector2[7];
        Span<float> hw = stackalloc float[7];
        Span<float> reach = stackalloc float[7];
        Span<uint> body = stackalloc uint[7];
        Span<uint> core = stackalloc uint[7];
        Span<uint> bloom = stackalloc uint[7];

        Vector2 side = new(-dir.Y, dir.X);
        float amp = half * 0.30f;
        for (int j = 0; j < m; j++)
        {
            float t = j / (float)(m - 1);                       // 0..1 along the arc
            float along = (t * 2f - 1f) * half;
            float pin = DrawHelpers.Smooth(MathF.Min(t, 1f - t) / 0.22f);
            pts[j] = pos + dir * along + side * (Bolt.Lattice(p.Seed, j, tick) * amp * pin);
        }
        MeshBuilder.MiterNormals(pts[..m], nrm[..m]);

        float bodyHalf = MathF.Max(0.7f * px, half * 0.055f);
        float coreHalf = MathF.Max(0.45f * px, half * 0.022f);
        float bloomReach = MathF.Max(3.5f * px, half * 0.42f);
        for (int j = 0; j < m; j++)
        {
            float t = j / (float)(m - 1);
            float ends = 0.55f + 0.45f * DrawHelpers.Smooth(MathF.Min(t, 1f - t) / 0.30f);
            hw[j] = bodyHalf;
            reach[j] = bloomReach;
            bloom[j] = DrawHelpers.Tint(StormColor.Glow.Sample(0.50f), k * 0.28f * ends, overridden);
            body[j] = DrawHelpers.Tint(StormColor.Glow.Sample(0.84f), k * 0.95f * ends, overridden);
            core[j] = DrawHelpers.Tint(StormColor.Glow.Sample(1.00f), k * 0.90f * ends, overridden);
        }

        Mb.Clear();
        Mb.Tent(pts[..m], nrm[..m], reach[..m], bloom[..m]);

        if (branching)
        {
            // Two or three twigs off the middle, re-rolled with the rest.
            Span<Vector2> tw = stackalloc Vector2[4];
            Span<Vector2> tn = stackalloc Vector2[4];
            Span<float> th = stackalloc float[4];
            Span<float> tr = stackalloc float[4];
            Span<uint> tb = stackalloc uint[4];
            Span<uint> tbl = stackalloc uint[4];
            int twigs = 2 + (DrawHelpers.Hash01(p.Seed + tick * 5 + 1) < 0.4f ? 1 : 0);
            for (int i = 0; i < twigs; i++)
            {
                int fs = p.Seed * 7 + i * 131;
                int at = 1 + (int)(DrawHelpers.Hash01(fs + tick * 3) * (m - 2));
                at = Math.Clamp(at, 1, m - 2);
                Vector2 d = pts[at + 1] - pts[at - 1];
                float dl2 = d.LengthSquared();
                if (dl2 < 1e-4f) continue;
                Vector2 tan = d / MathF.Sqrt(dl2);
                float sgn = (i & 1) == 0 ? 1f : -1f;
                float ang = 0.55f + 0.5f * DrawHelpers.Hash01(fs + tick * 9 + 2);
                Bolt.Twig(tw, pts[at], Bolt.Rotate(tan, sgn * ang), half * (0.55f + 0.35f * DrawHelpers.Hash01(fs + 4)), fs + 29, tick, 0.5f);
                MeshBuilder.MiterNormals(tw, tn);
                for (int j = 0; j < 4; j++)
                {
                    float taper = 1f - j / 4f;
                    th[j] = MathF.Max(0.45f * px, bodyHalf * 0.6f * taper);
                    tr[j] = bloomReach * 0.55f * taper;
                    tb[j] = DrawHelpers.Tint(StormColor.Glow.Sample(0.66f), k * 0.70f * taper, overridden);
                    tbl[j] = DrawHelpers.Tint(StormColor.Glow.Sample(0.34f), k * 0.22f * taper, overridden);
                }
                Mb.Tent(tw, tn, tr, tbl);
                Mb.Ribbon(tw, tn, th, tb, 0.7f * px);
            }
        }

        for (int j = 0; j < m; j++) hw[j] = bodyHalf;
        Mb.Ribbon(pts[..m], nrm[..m], hw[..m], body[..m], 0.9f * px);
        for (int j = 0; j < m; j++) hw[j] = coreHalf;
        Mb.Ribbon(pts[..m], nrm[..m], hw[..m], core[..m], 0.7f * px);
        Mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));

        // A hot pip where the arc touches down at either end.
        uint pip = DrawHelpers.WithAlpha(StormColor.Glow.Sample(1f), k * 0.80f);
        float pr = MathF.Max(0.9f * px, half * 0.07f);
        dl.AddCircleFilled(pts[0], pr, pip);
        dl.AddCircleFilled(pts[m - 1], pr, pip);
    }

    // The cheap tier: three kinked segments and a glow dot. This is what a swarm of them costs.
    private static void DrawSmall(ImDrawListPtr dl, Vector2 pos, Vector2 dir, float half, float k, int seed, int tick)
    {
        Vector2 side = new(-dir.Y, dir.X);
        Vector2 a = pos - dir * half;
        Vector2 b = pos + side * (Bolt.Lattice(seed, 1, tick) * half * 0.5f);
        Vector2 c = pos + dir * half;
        uint glow = DrawHelpers.WithAlpha(StormColor.Glow.Sample(0.45f), k * 0.30f);
        uint line = DrawHelpers.WithAlpha(StormColor.Glow.Sample(0.92f), k * 0.95f);
        dl.AddCircleFilled(pos, half * 1.6f, glow);
        dl.AddLine(a, b, line, 1.2f);
        dl.AddLine(b, c, line, 1.2f);
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    // Crackles that hop off a strand and die: short-lived, nearly still.
    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Arc,
            DensityPer100px: 0.5f,
            SpeedMin: 0f, SpeedMax: 12f,
            LifespanMin: 0.10f, LifespanMax: 0.28f,
            SizeMin: 7f, SizeMax: 16f,
            SpreadRadians: MathF.PI,
            BiasVelocity: Vector2.Zero),
    };
}
