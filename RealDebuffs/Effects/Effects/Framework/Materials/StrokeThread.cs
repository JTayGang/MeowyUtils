using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>What a hit on a thread of each liquid throws. A new liquid's thread adds its spray here.</summary>
public static class ThreadSprays
{
    /// <summary>A spray of fine droplets: the splat of a spit and the pop of a bubble both use it.</summary>
    public static readonly ImpactEmission[] Acid =
    {
        new(Role: PrimitiveRole.Goop, CountMin: 4, CountMax: 9,
            SpeedMin: 200f, SpeedMax: 620f, LifespanMin: 0.9f, LifespanMax: 1.6f,
            SizeMin: 1.7f, SizeMax: 4.4f, ConeRadians: 1.3f, Gravity: new Vector2(0f, 1900f)),
    };
}

/// <summary>
/// A thread of viscous liquid stretched between where it clings and where it hangs: the string a thick liquid pulls out as it is drawn away from itself. It is a tube lit
/// through a <see cref="LiquidMatcap"/>, so it shades exactly as the drops and the mass of the same liquid do, and it carries the physics of a real thread: it flares into a
/// meniscus where it joins the surface, necks down as it stretches, breaks into beads along its length once it is long and thin (the Plateau-Rayleigh instability) and swells to a
/// drop at its free end. The stroke's WidthHint is the thread's thinnest diameter; the effect thins it as it stretches. TipFlare (0..1) swells the end drop. Reveal draws it
/// from its root; a thread that is being pulled out simply gets a longer path.
/// </summary>
public sealed class StrokeThread : IStrokeMaterial
{
    private readonly string _name;
    private readonly string[] _words;
    private readonly LiquidMatcap _liquid;
    private readonly ImpactEmission[] _hit;
    private readonly MeshBuilder _mb = new();

    public StrokeThread(string name, in LiquidSpec liquid, ImpactEmission[] hit, params string[] words)
    {
        _name = name; _liquid = new LiquidMatcap(in liquid); _hit = hit; _words = words;
    }

    public string Name => _name;
    public string[] NaturalLanguageWords => _words;
    public ReadOnlySpan<ImpactEmission> ImpactEmissions => _hit;

    public float RadiusAt(in StrokePrimitive s, float arc, float shortSide) => MathF.Max(1f, s.WidthHint * 0.5f);

    private const int Across = 5;                         // columns across the tube
    private static readonly float[] Lateral = { -1f, -0.52f, 0f, 0.52f, 1f };

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        StrandPath path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        float reveal = Math.Clamp(s.Reveal, 0f, 1f);
        if (path.Count < 2 || alpha <= 0.005f || reveal <= 0.001f) return;

        float px = ctx.ScreenScale;
        float visible = path.Length * reveal;
        if (visible < 2f * px) return;

        float r0 = MathF.Max(0.9f * px, s.WidthHint * 0.5f);
        bool overridden = DrawHelpers.ColorOverrideActive;
        float dye = DrawHelpers.ColorOverrideChroma;
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        float feather = MathF.Max(0.8f, 1.0f * px);

        // Stretch: how long the thread is for how fat. Long and thin threads break into beads; short fat ones stay a smooth neck.
        float slender = visible / MathF.Max(1f, r0 * 2f);
        float beadAmp = 0.62f * DrawHelpers.Smooth((slender - 22f) / 70f);
        float beadLen = MathF.Max(10f * px, r0 * 10.5f);
        float beadPhase = s.Phase + s.Seed * 0.37f;
        float tipR = r0 * (1.9f + 3.0f * Math.Clamp(s.TipFlare, 0f, 1f));
        float tipLen = tipR * 2.6f;

        int rows = Math.Clamp((int)(visible / (4.5f * px)), 6, 80);
        Span<float> radius = stackalloc float[rows + 1];
        Span<Vector2> pos = stackalloc Vector2[rows + 1];
        Span<Vector2> tan = stackalloc Vector2[rows + 1];
        Span<float> arcs = stackalloc float[rows + 1];

        for (int j = 0; j <= rows; j++)
        {
            float arc = visible * j / rows;
            path.SampleAtArc(arc, out pos[j], out tan[j]);
            arcs[j] = arc;

            // The meniscus where it leaves the surface: a cone that falls to the thread's own width.
            float root = 1f + 2.6f * MathF.Exp(-arc / (r0 * 3.4f + 5f * px));
            // Beads, sharpened so they are round bulbs between thin necks rather than a gentle swell.
            float b = 0.5f + 0.5f * MathF.Cos(MathF.Tau * arc / beadLen + beadPhase);
            float bead = 1f + beadAmp * (b * b * b - 0.22f);
            float r = r0 * root * bead;
            // The drop swelling at the free end.
            float tipK = DrawHelpers.Smooth((arc - (visible - tipLen)) / tipLen);
            r = MathF.Max(r, r0 + (tipR - r0) * MathF.Sqrt(tipK) * (reveal >= 0.999f ? 1f : 0f));
            // ...rounded off at the very end.
            float endRound = visible - arc;
            if (endRound < tipR) r *= MathF.Sqrt(MathF.Max(0.02f, 1f - MathF.Pow(1f - endRound / tipR, 2f) * (reveal >= 0.999f ? 1f : 0f)));
            radius[j] = r;
        }

        _mb.Clear();
        int prevBase = -1;
        for (int j = 0; j <= rows; j++)
        {
            Vector2 across = new(-tan[j].Y, tan[j].X);
            int a = Math.Max(0, j - 1), c = Math.Min(rows, j + 1);
            float dr = (radius[c] - radius[a]) / MathF.Max(1e-3f, arcs[c] - arcs[a]);       // the surface leans along the axis where the radius changes
            float rootFade = DrawHelpers.Smooth(arcs[j] / (r0 * 1.2f + 4f * px));       // it comes out of the surface, not off it: nothing at the root, all of it a few px on
            float thin = 0.68f + 0.32f * DrawHelpers.Smooth(radius[j] / (3.2f * px));

            _mb.Reserve(Across + 2, 0);
            int rowBase = -1;
            for (int k = 0; k < Across + 2; k++)
            {
                // Columns -1..1 across the tube, plus a feather one pixel past each side.
                int lk = Math.Clamp(k - 1, 0, Across - 1);
                float u = Lateral[lk];
                bool outer = k == 0 || k == Across + 1;
                Vector2 at = pos[j] + across * (u * radius[j] + (outer ? MathF.Sign(u) * feather : 0f));

                float nx = across.X * u * 0.97f - tan[j].X * dr * 1.2f, ny = across.Y * u * 0.97f - tan[j].Y * dr * 1.2f;
                float nl = MathF.Sqrt(nx * nx + ny * ny);
                if (nl > 0.985f) { nx *= 0.985f / nl; ny *= 0.985f / nl; }
                Vector4 lit = _liquid.Sample(nx, ny);
                Vector3 rgb = new(lit.X, lit.Y, lit.Z);
                if (dye > 0f) rgb = DrawHelpers.BoostSaturation(rgb, dye, DrawHelpers.SoftDye);
                float al = lit.W * alpha * rootFade * thin * (outer ? 0f : 1f);
                uint col = DrawHelpers.Tint(DrawHelpers.Pack(rgb.X, rgb.Y, rgb.Z), al, overridden);
                int v = _mb.VertexReserved(at, col);
                if (k == 0) rowBase = v;
            }

            if (prevBase >= 0)
            {
                _mb.Reserve(0, (Across + 1) * 6);
                for (int k = 0; k < Across + 1; k++)
                {
                    int p0 = prevBase + k, p1 = prevBase + k + 1, q0 = rowBase + k, q1 = rowBase + k + 1;
                    _mb.TriReserved(p0, p1, q1); _mb.TriReserved(p0, q1, q0);
                }
            }
            prevBase = rowBase;
        }
        _mb.Flush(dl, uv);

        // A crisp highlight down the lit side of the thread: the tube's own lighting is too coarse across a few pixels to carry it.
        if (r0 > 1.2f * px)
        {
            Vector2 key = new(-0.52f, -0.70f);
            uint hi = DrawHelpers.Tint(HighlightColour, alpha * 0.55f, overridden);
            for (int j = 1; j < rows - 1; j += 2)
            {
                Vector2 across = new(-tan[j].Y, tan[j].X);
                float side = Vector2.Dot(across, key) >= 0f ? 1f : -1f;
                float lit = MathF.Abs(Vector2.Dot(across, Vector2.Normalize(key)));
                if (lit < 0.25f) continue;
                Vector2 a = pos[j] + across * (side * radius[j] * 0.45f), b = pos[j + 1] + new Vector2(-tan[j + 1].Y, tan[j + 1].X) * (side * radius[j + 1] * 0.45f);
                dl.AddLine(a, b, DrawHelpers.WithAlpha(hi, lit), MathF.Max(0.9f, radius[j] * 0.34f));
            }
        }
    }

    private static readonly uint HighlightColour = DrawHelpers.Pack(1.00f, 0.99f, 0.90f);
}

/// <summary>
/// An invisible host for liquid that hangs and falls: it draws nothing itself, but declares the drip, so a path laid along the underside of a surface that something else draws
/// (a region of liquid, a ledge) grows drops from it. Lay the path on the surface, give it a Seed that stays the same from frame to frame, and Reveal it; the drops are the same
/// ones a tentacle or a thread would shed, the same swappable liquid.
/// </summary>
public sealed class StrokeDripLine : IStrokeMaterial
{
    private readonly string _name;
    private readonly StrokeEmission[] _emissions;

    public StrokeDripLine(string name, StrokeEmission[] emissions) { _name = name; _emissions = emissions; }

    public string Name => _name;
    public ReadOnlySpan<StrokeEmission> Emissions => _emissions;
    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx) { }
}
