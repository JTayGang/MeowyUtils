using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>What an eye is like. Carried in ParticlePrimitive.Variant (see ParticleEye.Held and ParticleEye.Styled).</summary>
public enum EyeStyle
{
    /// <summary>Pale and bloodless: a cold iris on grey sclera, a round pupil.</summary>
    Watcher = 0,
    /// <summary>A dark socket with a burning iris and a slit pupil, like eyeshine caught in torchlight.</summary>
    Feral = 1,
    /// <summary>Milk-white, the iris all but gone and the pupil a pinprick: something wearing a person.</summary>
    Hollow = 2,
    /// <summary>Veined red, a wide amber iris and a pupil blown open: it has not slept, and it has seen something.</summary>
    Bloodshot = 3,
    /// <summary>Black all through, only a thin ring of cold light where the iris was.</summary>
    Void = 4,
    /// <summary>A pale gold iris with a flat, sideways slab of a pupil.</summary>
    Goat = 5,
    /// <summary>The pupil winds away into a slowly turning spiral.</summary>
    Spiral = 6,
    /// <summary>Three small pupils circling in one iris.</summary>
    Triple = 7,
    /// <summary>A watcher with a black tear running down from it.</summary>
    Weeping = 8,
    /// <summary>Stitched shut. It never opens.</summary>
    Sewn = 9,
    /// <summary>A lidless eyeball with its nerve trailing behind it.</summary>
    Orb = 10,
}

/// <summary>
/// A watching eye, shaded like a real one: sclera darkening toward the lids, a ringed and fibrous iris clipped by them, lid shadow,
/// a wet tear line and a specular window. Size is the eye's half-width in px (an Orb's radius); Rotation rolls it. It always looks
/// toward the middle of the screen, with the odd darting glance. Openness follows AgeRatio (open, hold, close) unless Variant holds
/// it (see Held). Small and far eyes are drawn with fewer vertices and less detail.
/// </summary>
public sealed partial class ParticleEye : IParticleMaterial
{
    private readonly string _name;
    private readonly Palette _palette;

    /// <summary>The ashen eye (particle.eye).</summary>
    public ParticleEye() : this("particle.eye", HauntColor.Glow, "eye", "eyes", "eyeball", "eyeballs", "watcher", "watchers") { }

    /// <summary>The same eye in another palette: register one per theme.</summary>
    internal ParticleEye(string name, Palette palette, params string[] words)
    {
        _name = name;
        _palette = palette;
        NaturalLanguageWords = words;
    }

    public string Name => _name;
    public string[] NaturalLanguageWords { get; }

    // Variant: bits 0-7 hold the openness (0..255) when HeldBit is set; bits 9-12 hold the EyeStyle.
    private const int HeldBit = 0x100;
    private const int StyleShift = 9, StyleMask = 0xF;

    /// <summary>Variant for an eye whose lids the effect drives itself (0 = shut, 1 = wide) instead of leaving them to its age.</summary>
    public static int Held(float open, EyeStyle style = EyeStyle.Watcher)
        => (int)(255f * DrawHelpers.Saturate(open)) | HeldBit | ((int)style << StyleShift);

    /// <summary>Variant for an eye that opens and closes over its own life.</summary>
    public static int Styled(EyeStyle style) => (int)style << StyleShift;

    private const int MaxCols = 16, MaxRows = 5, MaxIrisSegs = 26, IrisRows = 6;

    private static readonly MeshBuilder Mb = new();
    private static readonly Vector2[] Up = new Vector2[MaxCols + 1];
    private static readonly Vector2[] Lo = new Vector2[MaxCols + 1];
    private static readonly Vector2[] Line = new Vector2[MaxCols + 1];
    private static readonly Vector2[] Nrm = new Vector2[MaxCols + 1];
    private static readonly float[] Half = new float[MaxCols + 1];
    private static readonly Vector2[] VeinCtl = new Vector2[4];
    private const int VeinPts = 7;
    private static readonly Vector2[] Vein = new Vector2[VeinPts];
    private static readonly Vector2[] VeinN = new Vector2[VeinPts];
    private static readonly float[] VeinHalf = new float[VeinPts];
    private static readonly Vector2[] IrisPos = new Vector2[IrisRows * (MaxIrisSegs + 1)];
    private static readonly uint[] IrisCol = new uint[IrisRows * (MaxIrisSegs + 1)];

    private static readonly uint Ink = DrawHelpers.Pack(0.008f, 0.010f, 0.014f);
    private static readonly uint Flesh = DrawHelpers.Pack(0.60f, 0.27f, 0.27f);
    private static readonly uint VeinCol = DrawHelpers.Pack(0.52f, 0.16f, 0.19f);

    private enum Pupil { Round, Slit, Slab, Triple }

    /// <summary>What sets one style apart: how its white is shaded, its veins, its iris and its pupil.</summary>
    private struct Look
    {
        public float ScleraTone;          // added to the white's shading before ScleraScale
        public float ScleraScale;         // 1 = as lit; low = a dark socket
        public float Flesh;               // warmth over the whole white
        public float CornerFlesh;         // extra warmth toward the corners
        public float VeinAlpha;
        public int VeinCount;             // per side
        public float IrisR;               // iris radius, in eye half-widths
        public float PupilR;              // round pupil radius, in iris radii
        public float Glow;                // the light it gives off
        public Pupil Pupil;
        public uint Collarette, Mid, Limbus;   // iris colours, pupil edge to rim
    }

    private Look LookOf(EyeStyle style)
    {
        uint S(float t) => _palette.Sample(t);
        var look = new Look
        {
            ScleraScale = 1f, CornerFlesh = 0.55f, VeinAlpha = 0.28f, VeinCount = 3, IrisR = 0.33f, PupilR = 0.36f, Glow = 0.17f,
            Pupil = Pupil.Round, Collarette = S(0.60f), Mid = S(0.38f), Limbus = S(0.06f),
        };

        switch (style)
        {
            case EyeStyle.Feral:
                look.ScleraScale = 0.22f; look.CornerFlesh = 0.18f; look.VeinAlpha = 0f; look.IrisR = 0.42f; look.Glow = 0.34f;
                look.Pupil = Pupil.Slit; look.Collarette = S(0.98f); look.Mid = S(0.80f); look.Limbus = S(0.30f);
                break;
            case EyeStyle.Hollow:
                look.ScleraTone = 0.12f; look.VeinAlpha = 0.42f; look.IrisR = 0.27f; look.PupilR = 0.14f;
                look.Collarette = S(0.52f); look.Mid = S(0.40f); look.Limbus = S(0.22f);
                break;
            case EyeStyle.Bloodshot:
                look.ScleraTone = -0.04f; look.Flesh = 0.42f; look.VeinAlpha = 0.58f; look.VeinCount = 6; look.IrisR = 0.30f; look.PupilR = 0.46f;
                look.Collarette = DrawHelpers.Pack(0.86f, 0.54f, 0.16f); look.Mid = DrawHelpers.Pack(0.56f, 0.24f, 0.09f); look.Limbus = DrawHelpers.Pack(0.14f, 0.03f, 0.02f);
                break;
            case EyeStyle.Void:
                look.ScleraScale = 0.10f; look.CornerFlesh = 0f; look.VeinAlpha = 0f; look.IrisR = 0.30f; look.PupilR = 0.64f; look.Glow = 0.30f;
                look.Collarette = S(0.95f); look.Mid = S(0.75f); look.Limbus = S(0.35f);
                break;
            case EyeStyle.Goat:
                look.VeinAlpha = 0.22f; look.VeinCount = 2; look.IrisR = 0.40f; look.Glow = 0.20f; look.Pupil = Pupil.Slab;
                look.Collarette = DrawHelpers.Pack(0.96f, 0.86f, 0.36f); look.Mid = DrawHelpers.Pack(0.66f, 0.54f, 0.16f); look.Limbus = DrawHelpers.Pack(0.20f, 0.14f, 0.04f);
                break;
            case EyeStyle.Spiral:
                look.ScleraTone = 0.05f; look.VeinAlpha = 0.20f; look.VeinCount = 2; look.IrisR = 0.38f; look.PupilR = 0.16f; look.Glow = 0.20f;
                look.Collarette = S(0.85f); look.Mid = S(0.55f); look.Limbus = S(0.15f);
                break;
            case EyeStyle.Triple:
                look.IrisR = 0.40f; look.Pupil = Pupil.Triple;
                break;
            case EyeStyle.Weeping:
                look.ScleraTone = -0.02f;
                break;
            case EyeStyle.Sewn:
                look.VeinAlpha = 0f;
                break;
            case EyeStyle.Orb:
                look.IrisR = 0.40f; look.VeinAlpha = 0.50f; look.VeinCount = 9; look.Glow = 0.20f;
                break;
        }
        return look;
    }

    /// <summary>Everything one eye's builders share: where it is, how it is turned, how open, how bright, how detailed, and what it is like.</summary>
    private readonly struct Pose
    {
        public readonly Vector2 Pos;
        public readonly float Cos, Sin, W, K, Open, Px;
        public readonly int Cols, Rows, IrisSegs;
        public readonly EyeStyle Style;
        public readonly Look Look;
        public readonly bool Overridden;

        public Pose(Vector2 pos, float rotation, float w, float k, float open, float px, EyeStyle style, in Look look, bool overridden)
        {
            Pos = pos; Cos = MathF.Cos(rotation); Sin = MathF.Sin(rotation);
            W = w; K = k; Open = open; Px = px; Style = style; Look = look; Overridden = overridden;

            // Far and small eyes get fewer vertices; at that size nobody can tell.
            float size = w / px;
            (Cols, Rows, IrisSegs) = size >= 36f ? (16, 5, 26) : size >= 14f ? (12, 4, 18) : (8, 3, 12);
        }

        public bool Detailed => W >= 20f * Px;
        public Vector2 ToScreen(Vector2 local) => Pos + new Vector2(local.X * Cos - local.Y * Sin, local.X * Sin + local.Y * Cos);
        /// <summary>A screen-axis direction expressed in the eye's own frame.</summary>
        public Vector2 FromScreenDir(Vector2 d) => new(d.X * Cos + d.Y * Sin, -d.X * Sin + d.Y * Cos);
        public uint Tint(uint rgb, float alpha) => DrawHelpers.Tint(rgb, alpha * K, Overridden);
        public float IrisRadius => W * Look.IrisR;
    }

    /// <summary>The lids at x (-1..1 across the eye): the upper bulges more than the lower, and both sag together when shut.</summary>
    private static void Lids(float x, float open, float w, out float upper, out float lower)
    {
        float s = MathF.Max(0f, 1f - x * x);
        float sag = 0.07f * w * s;
        upper = DrawHelpers.Lerp(sag, -0.58f * w * MathF.Pow(s, 0.85f) * (1f + 0.10f * x), open);
        lower = DrawHelpers.Lerp(sag, 0.34f * w * s, open);
    }

    /// <summary>Pins a local point between the lids, so the iris is cut by them the way a real one is.</summary>
    private static Vector2 BetweenLids(Vector2 p, float open, float w)
    {
        float x = Math.Clamp(p.X / w, -0.985f, 0.985f);
        Lids(x, open, w, out float yu, out float yl);
        float lo = yu + 0.015f * w, hi = yl - 0.015f * w;
        return new Vector2(x * w, lo < hi ? Math.Clamp(p.Y, lo, hi) : 0.5f * (yu + yl));
    }

    private static uint Scale(uint c, float k)
    {
        uint r = (uint)Math.Clamp((c & 255) * k, 0f, 255f), g = (uint)Math.Clamp(((c >> 8) & 255) * k, 0f, 255f), b = (uint)Math.Clamp(((c >> 16) & 255) * k, 0f, 255f);
        return (c & 0xFF000000u) | r | (g << 8) | (b << 16);
    }

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        float w = p.Size;
        if (k <= 0.004f || w <= 2.5f) return;

        var style = (EyeStyle)((p.Variant >> StyleShift) & StyleMask);
        float open = (p.Variant & HeldBit) != 0
            ? (p.Variant & 0xFF) / 255f
            : DrawHelpers.Smooth(p.AgeRatio / 0.16f) * (1f - DrawHelpers.Smooth((p.AgeRatio - 0.80f) / 0.20f));
        if (style == EyeStyle.Sewn) open = 0f;

        Look look = LookOf(style);
        var e = new Pose(p.DrawPos, p.Rotation, w, k, open, ctx.ScreenScale, style, in look, DrawHelpers.ColorOverrideActive);
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        float phase = DrawHelpers.Hash01(p.Seed + 5) * MathF.Tau;

        if (style == EyeStyle.Orb) { DrawOrb(dl, in p, in e, uv, ctx.Time + phase, ctx.ScreenW, ctx.ScreenH); return; }

        DrawSocket(dl, uv, in e);

        for (int i = 0; i <= e.Cols; i++)
        {
            float x = -1f + 2f * i / e.Cols;
            Lids(x, open, w, out float yu, out float yl);
            Up[i] = new Vector2(x * w, yu);
            Lo[i] = new Vector2(x * w, yl);
        }

        // Where it is looking: toward the middle of the screen, in the eye's own frame, with a darting glance now and then.
        Vector2 ic = Vector2.Zero;
        if (open >= 0.06f)
        {
            Vector2 toMid = new Vector2(ctx.ScreenW * 0.5f, ctx.ScreenH * 0.5f) - e.Pos;
            float len = toMid.Length();
            Vector2 lookDir = e.FromScreenDir(len > 1f ? toMid / len : Vector2.Zero);
            int glance = (int)(ctx.Time * 1.3f + DrawHelpers.Hash01(p.Seed) * 10f);
            lookDir.X += (DrawHelpers.Hash01(p.Seed * 3 + glance * 17) - 0.5f) * 0.30f;
            lookDir.Y += (DrawHelpers.Hash01(p.Seed * 5 + glance * 29) - 0.5f) * 0.20f;
            ic = new Vector2(Math.Clamp(lookDir.X * 0.30f, -0.40f, 0.40f) * w, Math.Clamp(lookDir.Y * 0.15f, -0.09f, 0.09f) * w * open);
        }

        Mb.Clear();
        if (open >= 0.06f)
        {
            BuildSclera(in e);
            if (e.Detailed && look.VeinAlpha > 0f) BuildVeins(in e, ic, p.Seed);
            BuildIris(in e, ic, p.Seed, ctx.Time + phase);
            if (style == EyeStyle.Spiral) BuildSpiral(in e, ic, ctx.Time + phase);
            if (style == EyeStyle.Triple) BuildTriplePupils(in e, ic, ctx.Time + phase);
        }
        BuildLids(in e, p.Seed);
        if (style == EyeStyle.Weeping && open >= 0.2f) BuildTear(in e, p.Seed, ctx.Time + phase);
        Mb.Flush(dl, uv);

        if (open >= 0.35f && e.Detailed) DrawHighlights(dl, uv, in e, ic, ctx.Time + phase);
    }

    /// <summary>The darkness the eye sits in (so it reads on a bright scene too) and the little light it gives off.</summary>
    private void DrawSocket(ImDrawListPtr dl, Vector2 uv, in Pose e)
    {
        float w = e.W, k = e.K;
        float lit = 0.35f + 0.65f * e.Open;
        Span<float> rr = stackalloc float[2] { w * 0.85f, w * 2.1f };
        Span<uint> rc = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(Ink, k * 0.46f * lit),
            DrawHelpers.WithAlpha(Ink, 0f),
        };
        MeshDraw.Radial(dl, e.Pos, uv, DrawHelpers.WithAlpha(Ink, k * 0.62f * lit), 14, rr, rc, 0f, 0.78f, 0, 0f);

        float glow = e.Look.Glow * MathF.Sqrt(e.Open);
        if (glow <= 0.01f) return;
        Span<float> gr = stackalloc float[2] { w * 0.75f, w * (e.Look.Glow > 0.3f ? 2.5f : 1.8f) };
        Span<uint> gc = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(_palette.Sample(0.42f), k * glow * 0.55f),
            DrawHelpers.WithAlpha(_palette.Sample(0.30f), 0f),
        };
        MeshDraw.Radial(dl, e.Pos, uv, DrawHelpers.WithAlpha(_palette.Sample(0.55f), k * glow), 14, gr, gc, 0f, 0.8f, 0, 0f);
    }

    /// <summary>The white of the eye: a grid between the lids, dark under the upper lid and toward the corners, warm at the corners.</summary>
    private void BuildSclera(in Pose e)
    {
        int cols = e.Cols, rows = e.Rows;
        Mb.Reserve(rows * (cols + 1), (rows - 1) * cols * 6);
        int first = -1;
        for (int r = 0; r < rows; r++)
        {
            float v = r / (float)(rows - 1);
            for (int i = 0; i <= cols; i++)
            {
                float ax = MathF.Abs(-1f + 2f * i / cols);
                float lit = 0.80f - 0.42f * MathF.Pow(ax, 2.2f);
                float lid = 0.62f * MathF.Pow(1f - v, 2f) * (0.45f + 0.55f * e.Open);
                float under = 0.20f * MathF.Pow(v, 3f);
                float tone = (lit - lid - under + e.Look.ScleraTone) * e.Look.ScleraScale;

                float warm = Math.Clamp(e.Look.Flesh + e.Look.CornerFlesh * MathF.Pow(ax, 2.6f), 0f, 0.95f);
                uint c = DrawHelpers.LerpColor(_palette.Sample(DrawHelpers.Saturate(tone)), Flesh, warm);
                float a = (e.Look.ScleraScale < 0.5f ? 0.88f : 0.97f) * (1f - 0.45f * MathF.Pow(ax, 4f));
                int vi = Mb.VertexReserved(e.ToScreen(Vector2.Lerp(Up[i], Lo[i], v)), e.Tint(c, a));
                if (first < 0) first = vi;
            }
        }
        Mb.StitchGrid(first, rows, cols + 1);
    }

    /// <summary>The pupil's radius along a spoke at angle (cs, sn): round, a slit, a sideways slab, or a pinprick for the three that sit on it.</summary>
    private static float PupilRadius(in Look look, float cs, float sn, float R, float time)
    {
        switch (look.Pupil)
        {
            case Pupil.Slit:
            {
                float rx = R * 0.13f, ry = R * 0.92f;
                return 1f / MathF.Sqrt(cs * cs / (rx * rx) + sn * sn / (ry * ry));
            }
            case Pupil.Slab:
            {
                float rx = R * 0.86f, ry = R * 0.22f;
                float q = MathF.Pow(MathF.Abs(cs) / rx, 4f) + MathF.Pow(MathF.Abs(sn) / ry, 4f);
                return 1f / MathF.Pow(q, 0.25f);
            }
            case Pupil.Triple:
                return R * 0.04f;
            default:
                return R * (look.PupilR + 0.05f * MathF.Sin(time * 1.3f));
        }
    }

    /// <summary>
    /// The iris and pupil as a polar grid, every vertex pinned between the lids: a dark limbal ring, a fibrous body (a brightness
    /// jitter per spoke), brighter nearest the pupil, and darker where the upper lid shades it.
    /// </summary>
    private void BuildIris(in Pose e, Vector2 ic, int seed, float time, float flat = 1f)
    {
        float w = e.W, R = e.IrisRadius;
        bool orb = e.Style == EyeStyle.Orb;
        Vector2 gaze = ic.LengthSquared() > 1e-4f ? Vector2.Normalize(ic) : Vector2.UnitX;
        Vector2 across = new(-gaze.Y, gaze.X);
        int segs = e.IrisSegs;
        float shade = e.Look.ScleraScale < 0.5f ? 0.35f : 1f;

        for (int j = 0; j <= segs; j++)
        {
            float th = MathF.Tau * j / segs, cs = MathF.Cos(th), sn = MathF.Sin(th);
            float fibre = 0.78f + 0.44f * DrawHelpers.Hash01(seed * 131 + (j % segs) * 17);

            // Ring radii along this spoke, each kept clear of the one inside it (a slit pupil is taller than the iris is wide).
            float pupil = PupilRadius(in e.Look, cs, sn, R, time);
            float r2 = MathF.Max(0.46f * R, pupil + 0.05f * R);
            float r3 = MathF.Max(0.80f * R, r2 + 0.08f * R);
            float r4 = MathF.Max(R, r3 + 0.08f * R);
            float r5 = r4 + 0.06f * R;

            for (int r = 0; r < IrisRows; r++)
            {
                float rad = r switch { 0 => 0f, 1 => pupil, 2 => r2, 3 => r3, 4 => r4, _ => r5 };
                Vector2 rel = new(cs * rad, sn * rad);
                Vector2 pos;
                float under;
                if (orb)
                {
                    // On a ball the iris is a circle seen at a slant: squashed along the way it is turned, and kept on the ball.
                    pos = ic + gaze * (Vector2.Dot(rel, gaze) * flat) + across * Vector2.Dot(rel, across);
                    float len = pos.Length();
                    if (len > 0.97f * w) pos *= 0.97f * w / len;
                    under = DrawHelpers.Saturate(0.5f - pos.Y / (1.6f * w));
                }
                else
                {
                    pos = BetweenLids(ic + rel, e.Open, w);

                    // How close to the upper lid this vertex sits sets how much it is shaded.
                    Lids(pos.X / w, e.Open, w, out float yu, out _);
                    under = DrawHelpers.Saturate(1f - (pos.Y - yu) / (0.55f * R + 0.12f * w));
                }
                float lidShade = 1f - 0.6f * shade * under * under;

                uint col = r switch
                {
                    0 or 1 => e.Tint(Ink, 0.98f),
                    2 => e.Tint(Scale(e.Look.Collarette, fibre * lidShade), 0.97f),
                    3 => e.Tint(Scale(e.Look.Mid, fibre * lidShade), 0.97f),
                    4 => e.Tint(Scale(e.Look.Limbus, lidShade), 0.97f),
                    _ => e.Tint(Scale(e.Look.Limbus, lidShade), 0f),
                };
                int at = r * (segs + 1) + j;
                IrisPos[at] = e.ToScreen(pos);
                IrisCol[at] = col;
            }
        }

        // The grid is stored ring-major: stitch it as rows of one ring each.
        int count = IrisRows * (segs + 1);
        Mb.Reserve(count, (IrisRows - 1) * segs * 6);
        int first = Mb.AddBlockReserved(IrisPos, IrisCol, count);
        Mb.StitchGrid(first, IrisRows, segs + 1);
    }

    /// <summary>Two dark arms winding out from the pupil, turning slowly: it is looking at you and it would like you to look back.</summary>
    private void BuildSpiral(in Pose e, Vector2 ic, float time)
    {
        float R = e.IrisRadius, w = e.W;
        const int pts = 14;
        Span<Vector2> arm = stackalloc Vector2[pts];
        Span<Vector2> normal = stackalloc Vector2[pts];
        Span<float> half = stackalloc float[pts];
        for (int a = 0; a < 2; a++)
        {
            for (int i = 0; i < pts; i++)
            {
                float t = i / (float)(pts - 1);
                float ang = time * 0.9f + a * MathF.PI + t * MathF.Tau * 1.1f;
                float rad = R * (0.16f + 0.80f * t);
                arm[i] = e.ToScreen(BetweenLids(ic + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * rad, e.Open, w));
                half[i] = MathF.Max(0.3f * e.Px, R * 0.075f * (1f - 0.6f * t));
            }
            MeshBuilder.MiterNormals(arm, normal);
            Mb.Ribbon(arm, normal, half, e.Tint(Ink, 0.78f), 0.5f * e.Px);
        }
    }

    /// <summary>Three small pupils circling the middle of the iris.</summary>
    private void BuildTriplePupils(in Pose e, Vector2 ic, float time)
    {
        float R = e.IrisRadius, w = e.W;
        const int segs = 10;
        Mb.Reserve(3 * (segs + 1), 3 * segs * 3);
        for (int k = 0; k < 3; k++)
        {
            float ang = time * 0.35f + k * MathF.Tau / 3f;
            Vector2 centre = BetweenLids(ic + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * R * 0.40f, e.Open, w);
            int c0 = Mb.VertexReserved(e.ToScreen(centre), e.Tint(Ink, 0.98f));
            for (int i = 0; i < segs; i++)
            {
                float a = MathF.Tau * i / segs;
                Mb.VertexReserved(e.ToScreen(BetweenLids(centre + new Vector2(MathF.Cos(a), MathF.Sin(a)) * R * 0.17f, e.Open, w)), e.Tint(Ink, 0.98f));
            }
            for (int i = 0; i < segs; i++) Mb.TriReserved(c0, c0 + 1 + i, c0 + 1 + (i + 1) % segs);
        }
    }

    /// <summary>Thin red veins running from each corner toward the iris, on the white of the eye.</summary>
    private void BuildVeins(in Pose e, Vector2 ic, int seed)
    {
        float w = e.W, R = e.IrisRadius;
        for (int side = -1; side <= 1; side += 2)
        {
            for (int v = 0; v < e.Look.VeinCount; v++)
            {
                int h = seed * 11 + (side + 1) * 7 + v * 3;
                float y0 = (DrawHelpers.Hash01(h) - 0.5f) * 0.16f * w * e.Open;
                float x3 = ic.X + side * R * (1.08f + 0.30f * DrawHelpers.Hash01(h + 3));
                VeinCtl[0] = new Vector2(side * 0.92f * w, y0);
                VeinCtl[1] = new Vector2(side * 0.70f * w, y0 * 1.5f + (DrawHelpers.Hash01(h + 1) - 0.5f) * 0.12f * w * e.Open);
                VeinCtl[2] = new Vector2(side * 0.52f * w, y0 * 1.8f + (DrawHelpers.Hash01(h + 2) - 0.5f) * 0.14f * w * e.Open);
                VeinCtl[3] = new Vector2(side > 0 ? MathF.Min(x3, VeinCtl[2].X - 0.04f * w) : MathF.Max(x3, VeinCtl[2].X + 0.04f * w),
                                         ic.Y + (DrawHelpers.Hash01(h + 4) - 0.5f) * 0.5f * R);
                for (int i = 0; i < VeinPts; i++)
                {
                    // Two samples per span of the spline through the control points, so each vein bends instead of kinking.
                    int span = Math.Min(i / 2, 2);
                    float t = i == VeinPts - 1 ? 1f : (i % 2) * 0.5f;
                    Vein[i] = e.ToScreen(BetweenLids(Spline(VeinCtl, span, t), e.Open, w));
                    VeinHalf[i] = MathF.Max(0.12f * e.Px, (0.45f * e.Px + 0.005f * w) * (1f - 0.7f * i / (VeinPts - 1)));
                }
                MeshBuilder.MiterNormals(Vein, VeinN);
                Mb.Ribbon(Vein, VeinN, VeinHalf, e.Tint(VeinCol, e.Look.VeinAlpha * e.Open), 0.5f * e.Px);
            }
        }
    }

    /// <summary>Catmull-Rom through four control points: the curve between ctl[span] and ctl[span + 1] at t (0..1).</summary>
    private static Vector2 Spline(Vector2[] ctl, int span, float t)
    {
        Vector2 p0 = ctl[Math.Max(0, span - 1)], p1 = ctl[span], p2 = ctl[span + 1], p3 = ctl[Math.Min(ctl.Length - 1, span + 2)];
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
    }

    /// <summary>The lids: a shadow cast down across the eye, the lash line, the lower lid, a crease above and a wet line below. Shut, a seam of light.</summary>
    private void BuildLids(in Pose e, int seed)
    {
        float w = e.W, px = e.Px;
        float shut = 1f - DrawHelpers.Smooth(e.Open / 0.30f);

        if (e.Open >= 0.06f)
        {
            // Soft shadow the upper lid throws on the eye.
            LidLine(in e, Up, 0f, 0f, 0f, e.Tint(Ink, 0.40f), tent: 0.22f * w * e.Open);
            LidLine(in e, Up, 0f, 0.028f * w + 0.45f * px, 0.01f * w, e.Tint(Ink, 0.95f), feather: 0.7f * px + 0.01f * w);
            if (!e.Detailed) return;
            LidLine(in e, Lo, 0f, 0.016f * w + 0.3f * px, 0.006f * w, e.Tint(Ink, 0.50f), feather: 0.6f * px);
            LidLine(in e, Up, -0.13f * w * e.Open, 0.5f * px, 0f, e.Tint(_palette.Sample(0.40f), 0.20f), feather: 0.8f * px);
            LidLine(in e, Lo, -0.03f * w * e.Open, 0.012f * w + 0.35f * px, 0.004f * w, e.Tint(_palette.Sample(0.95f), 0.32f * e.Open), feather: 0.6f * px);
        }

        if (shut > 0.01f)
        {
            // Shut: light leaking under a single dark lash line.
            LidLine(in e, Up, 0.02f * w, 0f, 0f, e.Tint(_palette.Sample(0.62f), 0.30f * shut), tent: 0.16f * w);
            LidLine(in e, Up, 0f, 0.026f * w + 0.8f * px, 0.008f * w, e.Tint(Ink, 0.90f * shut), feather: 0.7f * px);
            if (e.Style == EyeStyle.Sewn) BuildStitches(in e, seed);
        }
    }

    /// <summary>Thread through the lid: a row of short pale stitches across the seam, and the skin gone red round them.</summary>
    private void BuildStitches(in Pose e, int seed)
    {
        float w = e.W;
        LidLine(in e, Up, 0f, 0f, 0f, e.Tint(Flesh, 0.30f), tent: 0.13f * w);

        int stitches = 5 + (seed & 1);
        Span<Vector2> thread = stackalloc Vector2[2];
        Span<Vector2> normal = stackalloc Vector2[2];
        Span<float> half = stackalloc float[2];
        half[0] = half[1] = MathF.Max(0.5f * e.Px, 0.016f * w);
        for (int i = 0; i < stitches; i++)
        {
            float x = -0.72f + 1.44f * (i + 0.5f * DrawHelpers.Hash01(seed + i)) / stitches;
            Lids(x, 0f, w, out float seam, out _);
            float slant = (DrawHelpers.Hash01(seed + 40 + i) - 0.5f) * 0.10f * w;
            thread[0] = e.ToScreen(new Vector2(x * w - slant, seam - 0.17f * w));
            thread[1] = e.ToScreen(new Vector2(x * w + slant, seam + 0.19f * w));
            MeshBuilder.MiterNormals(thread, normal);
            Mb.Ribbon(thread, normal, half, e.Tint(_palette.Sample(0.78f), 0.88f), 0.5f * e.Px);
        }
    }

    /// <summary>A black tear running down from the lower lid, always straight down the screen, with a bead at its end.</summary>
    private void BuildTear(in Pose e, int seed, float time)
    {
        float w = e.W;
        const int pts = 7;
        float side = (seed & 1) == 0 ? -0.45f : 0.45f;
        Lids(side, e.Open, w, out _, out float lowerY);
        Vector2 start = e.ToScreen(new Vector2(side * w, lowerY));
        float len = w * (0.7f + 0.9f * (time * 0.22f - MathF.Floor(time * 0.22f)));

        Span<Vector2> path = stackalloc Vector2[pts];
        Span<Vector2> normal = stackalloc Vector2[pts];
        Span<float> half = stackalloc float[pts];
        for (int i = 0; i < pts; i++)
        {
            float t = i / (float)(pts - 1);
            path[i] = start + new Vector2(MathF.Sin(t * 5f + time) * 0.025f * w, t * len);
            half[i] = MathF.Max(0.5f * e.Px, w * (0.085f - 0.045f * t));
        }
        MeshBuilder.MiterNormals(path, normal);
        Mb.Ribbon(path, normal, half, e.Tint(Ink, 0.88f), 0.7f * e.Px);

        // Wet: a pale line down one edge, and a heavier bead at the end.
        Span<Vector2> edge = stackalloc Vector2[pts];
        for (int i = 0; i < pts; i++) edge[i] = path[i] + new Vector2(-half[i] * 0.45f, 0f);
        MeshBuilder.MiterNormals(edge, normal);
        Span<float> thin = stackalloc float[pts];
        for (int i = 0; i < pts; i++) thin[i] = MathF.Max(0.25f * e.Px, half[i] * 0.18f);
        Mb.Ribbon(edge, normal, thin, e.Tint(_palette.Sample(0.9f), 0.30f), 0.4f * e.Px);

        Vector2 bead = path[pts - 1];
        float br = MathF.Max(1.1f * e.Px, w * 0.075f);
        int b0 = Mb.VertexReserved(bead, e.Tint(Ink, 0.9f));
        const int segs = 9;
        Mb.Reserve(segs + 1, segs * 3);
        for (int i = 0; i < segs; i++)
        {
            float a = MathF.Tau * i / segs;
            Mb.VertexReserved(bead + new Vector2(MathF.Cos(a), MathF.Sin(a) * 1.25f) * br, e.Tint(Ink, 0.9f));
        }
        for (int i = 0; i < segs; i++) Mb.TriReserved(b0, b0 + 1 + i, b0 + 1 + (i + 1) % segs);
    }

    /// <summary>One ribbon (or a tent, when tent > 0) along a lid curve, optionally lifted by yShift; fattest mid-eye, thinning to hwEnds at the corners.</summary>
    private void LidLine(in Pose e, Vector2[] lid, float yShift, float hwMid, float hwEnds, uint color, float feather = 0f, float tent = 0f)
    {
        int cols = e.Cols;
        for (int i = 0; i <= cols; i++)
        {
            float x = -1f + 2f * i / cols, s = 1f - x * x;
            Line[i] = e.ToScreen(new Vector2(lid[i].X, lid[i].Y + yShift));
            Half[i] = tent > 0f ? tent * (0.35f + 0.65f * s) : DrawHelpers.Lerp(hwEnds, hwMid, s);
        }
        var pts = Line.AsSpan(0, cols + 1);
        var nrm = Nrm.AsSpan(0, cols + 1);
        var half = Half.AsSpan(0, cols + 1);
        MeshBuilder.MiterNormals(pts, nrm);
        if (tent > 0f) Mb.Tent(pts, nrm, half, color);
        else Mb.Ribbon(pts, nrm, half, color, feather);
    }

    /// <summary>The wet glint on the iris, from a window up and to the left of the screen whatever way the eye is rolled, and a faint answer opposite.</summary>
    private void DrawHighlights(ImDrawListPtr dl, Vector2 uv, in Pose e, Vector2 ic, float time)
    {
        float R = e.IrisRadius, w = e.W;
        Vector2 hi = ic + e.FromScreenDir(new Vector2(-0.34f * R, -0.36f * R));
        Vector2 lo = ic + e.FromScreenDir(new Vector2(0.30f * R, 0.34f * R));
        uint white = _palette.Sample(1f);
        float flick = 0.92f + 0.08f * MathF.Sin(time * 2.3f);

        Span<float> rr = stackalloc float[2];
        Span<uint> rc = stackalloc uint[2];
        for (int g = 0; g < 2; g++)
        {
            Vector2 local = g == 0 ? hi : lo;
            float size = g == 0 ? 0.27f * R : 0.13f * R, gain = g == 0 ? 1f : 0.38f;
            Lids(Math.Clamp(local.X / w, -0.98f, 0.98f), e.Open, w, out float yu, out float yl);
            if (local.Y < yu + 0.05f * w || local.Y > yl - 0.05f * w) continue;
            rr[0] = size * 0.5f; rr[1] = size;
            rc[0] = e.Tint(white, 0.65f * gain * flick); rc[1] = e.Tint(white, 0f);
            MeshDraw.Radial(dl, e.ToScreen(local), uv, e.Tint(white, gain * flick), 8, rr, rc, 0f, 1f, 0, 0f);
        }
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    // As a stroke emitter: small eyes opening along the line, watching, and closing again.
    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Eye,
            DensityPer100px: 0.16f,
            SpeedMin: 3f, SpeedMax: 10f,
            LifespanMin: 2.2f, LifespanMax: 4.0f,
            SizeMin: 9f, SizeMax: 17f,
            SpreadRadians: 1.4f,
            BiasVelocity: new Vector2(0f, -5f),
            PrimaryDirection: new Vector2(0f, -1f)),
    };
}
