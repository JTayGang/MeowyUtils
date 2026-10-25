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

    // Variant: bits 0-7 hold the openness (0..255) when HeldBit is set; bits 9-12 hold the EyeStyle; bit 13 mirrors the eye (its inner
    // corner, with the caruncle, on the other side); bits 14-19 hold how hazy the air in front of it is (0..63); bits 20-24 how far it is looking straight out at you rather than toward the middle (0..31).
    /// <summary>
    /// The upper lid's outline: the dark lash line, the margin of flesh outside it, the crease and fold above, and the lashes. Off, the eye is
    /// bounded above only by the soft shadow its lid throws on it, and the lower lid (margin, wet line, shadow, lashes) is untouched.
    /// (A shut eye still shows its closing seam, or blinking and sewn eyes would vanish.)
    /// </summary>
    private static readonly bool ShowUpperLid = false;

    private const int HeldBit = 0x100;
    private const int StyleShift = 9, StyleMask = 0xF, MirrorBit = 1 << 13, HazeShift = 14, HazeMask = 0x3F, ForwardShift = 20, ForwardMask = 0x1F;

    /// <summary>Variant for an eye whose lids the effect drives itself (0 = shut, 1 = wide) instead of leaving them to its age. Haze (0..1) is how much dusty air lies between it and you.</summary>
    public static int Held(float open, EyeStyle style = EyeStyle.Watcher, bool mirror = false, float haze = 0f, float forward = 0f)
        => (int)(255f * DrawHelpers.Saturate(open)) | HeldBit | ((int)style << StyleShift) | (mirror ? MirrorBit : 0) | ((int)(63f * DrawHelpers.Saturate(haze)) << HazeShift) | ((int)(31f * DrawHelpers.Saturate(forward)) << ForwardShift);

    /// <summary>Variant for an eye that opens and closes over its own life.</summary>
    public static int Styled(EyeStyle style, bool mirror = false, float haze = 0f, float forward = 0f)
        => ((int)style << StyleShift) | (mirror ? MirrorBit : 0) | ((int)(63f * DrawHelpers.Saturate(haze)) << HazeShift) | ((int)(31f * DrawHelpers.Saturate(forward)) << ForwardShift);

    private const int MaxCols = 16, MaxRows = 7, MaxIrisSegs = 44, IrisRows = 9;

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

    // Skin, as the dark gives it up: cold and waxy where the far light reaches it, nearly black where it doesn't.
    private static readonly uint SkinLit = DrawHelpers.Pack(0.55f, 0.45f, 0.45f);
    private static readonly uint SkinShade = DrawHelpers.Pack(0.10f, 0.085f, 0.10f);
    private static readonly uint LidFlesh = DrawHelpers.Pack(0.36f, 0.25f, 0.26f);
    private static readonly uint Caruncle = DrawHelpers.Pack(0.74f, 0.40f, 0.40f);

    /// <summary>What dusty air lifts a far eye toward: the colour of the tunnel's haze, not black.</summary>
    private static readonly uint HazeRgb = DrawHelpers.Pack(0.40f, 0.52f, 0.54f);

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
        public uint PupilRing, Collarette, Mid, Limbus;   // iris colours, pupil edge to rim
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
                look.Pupil = Pupil.Slit; look.Collarette = S(0.98f); look.Mid = S(0.80f); look.Limbus = S(0.30f); look.PupilRing = look.Collarette;
                break;
            case EyeStyle.Hollow:
                look.ScleraTone = 0.12f; look.VeinAlpha = 0.42f; look.IrisR = 0.27f; look.PupilR = 0.14f;
                look.Collarette = S(0.52f); look.Mid = S(0.40f); look.Limbus = S(0.22f); look.PupilRing = look.Collarette;
                break;
            case EyeStyle.Bloodshot:
                look.ScleraTone = -0.04f; look.Flesh = 0.42f; look.VeinAlpha = 0.58f; look.VeinCount = 6; look.IrisR = 0.30f; look.PupilR = 0.46f;
                look.Collarette = DrawHelpers.Pack(0.86f, 0.54f, 0.16f); look.Mid = DrawHelpers.Pack(0.56f, 0.24f, 0.09f); look.Limbus = DrawHelpers.Pack(0.14f, 0.03f, 0.02f); look.PupilRing = DrawHelpers.Pack(0.92f, 0.70f, 0.30f);
                break;
            case EyeStyle.Void:
                look.ScleraScale = 0.10f; look.CornerFlesh = 0f; look.VeinAlpha = 0f; look.IrisR = 0.30f; look.PupilR = 0.64f; look.Glow = 0.30f;
                look.Collarette = S(0.95f); look.Mid = S(0.75f); look.Limbus = S(0.35f); look.PupilRing = look.Collarette;
                break;
            case EyeStyle.Goat:
                look.VeinAlpha = 0.22f; look.VeinCount = 2; look.IrisR = 0.40f; look.Glow = 0.20f; look.Pupil = Pupil.Slab;
                look.Collarette = DrawHelpers.Pack(0.96f, 0.86f, 0.36f); look.Mid = DrawHelpers.Pack(0.66f, 0.54f, 0.16f); look.Limbus = DrawHelpers.Pack(0.20f, 0.14f, 0.04f); look.PupilRing = DrawHelpers.Pack(0.98f, 0.92f, 0.50f);
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
        // Cold irises have a warm ring round the pupil, as real blue and green ones do.
        if (look.PupilRing == 0) look.PupilRing = DrawHelpers.LerpColor(look.Collarette, DrawHelpers.Pack(0.70f, 0.52f, 0.30f), 0.34f);
        return look;
    }

    /// <summary>Everything one eye's builders share: where it is, how it is turned, how open, how bright, how detailed, and what it is like.</summary>
    private readonly struct Pose
    {
        public readonly Vector2 Pos, Light;
        public readonly float Cos, Sin, W, K, Open, Px, Haze;
        public readonly int Cols, Rows, IrisSegs;
        public readonly EyeStyle Style;
        public readonly Look Look;
        public readonly bool Overridden, Mirror;

        /// <param name="toLight">Which way the light is, on screen: the tunnel's far end is lit, so this is toward the middle of the screen.</param>
        public Pose(Vector2 pos, float rotation, float w, float k, float open, float px, EyeStyle style, in Look look, bool overridden, bool mirror, float haze, Vector2 toLight)
        {
            Pos = pos; Cos = MathF.Cos(rotation); Sin = MathF.Sin(rotation);
            W = w; K = k; Open = open; Px = px; Style = style; Look = look; Overridden = overridden; Mirror = mirror; Haze = haze;
            Light = new Vector2(toLight.X * Cos + toLight.Y * Sin, -toLight.X * Sin + toLight.Y * Cos);

            // Far and small eyes get fewer vertices; at that size nobody can tell.
            float size = w / px;
            (Cols, Rows, IrisSegs) = size >= 70f ? (16, 7, 40) : size >= 36f ? (14, 5, 28) : size >= 14f ? (12, 4, 20) : (8, 3, 12);
        }

        public bool Detailed => W >= 20f * Px;
        public bool Large => W >= 36f * Px;

        /// <summary>
        /// A detail fading in with size (half-width in px at 1080p: 0 below lo, 1 above hi). Lashes, creases and the rest used to switch on
        /// when an eye crossed a size on its way in, which showed as a pop; now they come up as it grows.
        /// </summary>
        public float Grow(float lo, float hi) => DrawHelpers.Smooth((W / Px - lo) / (hi - lo));
        /// <summary>Big enough on screen that lashes, veins that fork and every fold of the lids can be seen.</summary>
        public bool Huge => W >= 70f * Px;
        public Vector2 ToScreen(Vector2 local) => Pos + new Vector2(local.X * Cos - local.Y * Sin, local.X * Sin + local.Y * Cos);
        /// <summary>A screen-axis direction expressed in the eye's own frame.</summary>
        public Vector2 FromScreenDir(Vector2 d) => new(d.X * Cos + d.Y * Sin, -d.X * Sin + d.Y * Cos);

        /// <summary>Dusty air lifts things toward its own colour with distance; it does not just dim them.</summary>
        public uint Tint(uint rgb, float alpha)
        {
            if (Haze > 0.01f) rgb = DrawHelpers.LerpColor(rgb, HazeRgb, 0.55f * Haze);
            return DrawHelpers.Tint(rgb, alpha * K, Overridden);
        }

        public float IrisRadius => W * Look.IrisR;

        /// <summary>The side of the eye its inner corner (with the caruncle) is on: +1 or -1 along the eye's own x.</summary>
        public float InnerSide => Mirror ? -1f : 1f;

        /// <summary>Edge softness: far eyes are out of focus.</summary>
        public float Soft(float px) => px * (1f + 2.5f * Haze);
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
        bool mirror = (p.Variant & MirrorBit) != 0;
        float haze = ((p.Variant >> HazeShift) & HazeMask) / 63f;
        float forward = ((p.Variant >> ForwardShift) & ForwardMask) / 31f;

        // The only light in the tunnel is at the far end, which is the middle of the screen: eyes are lit from that side and look toward it.
        Vector2 toMid = new Vector2(ctx.ScreenW * 0.5f, ctx.ScreenH * 0.5f) - p.DrawPos;
        float len = toMid.Length();
        Vector2 toLight = len > 1f ? toMid / len : new Vector2(0f, -1f);

        Look look = LookOf(style);
        var e = new Pose(p.DrawPos, p.Rotation, w, k, open, ctx.ScreenScale, style, in look, DrawHelpers.ColorOverrideActive, mirror, haze, toLight);
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        float phase = DrawHelpers.Hash01(p.Seed + 5) * MathF.Tau;

        if (style == EyeStyle.Orb) { DrawOrb(dl, in p, in e, uv, ctx.Time + phase, ctx.ScreenW, ctx.ScreenH, forward); return; }

        DrawSocket(dl, uv, in e);

        for (int i = 0; i <= e.Cols; i++)
        {
            float x = -1f + 2f * i / e.Cols;
            Lids(x, open, w, out float yu, out float yl);
            Up[i] = new Vector2(x * w, yu);
            Lo[i] = new Vector2(x * w, yl);
        }

        // Where it is looking: toward the middle of the screen, in the eye's own frame, with a darting glance now and then.
        // When something is being shown it looks straight out at you instead: pupil centred, no glancing.
        Vector2 ic = Vector2.Zero;
        if (open >= 0.06f)
        {
            Vector2 lookDir = e.Light;
            int glance = (int)(ctx.Time * 1.3f + DrawHelpers.Hash01(p.Seed) * 10f);
            lookDir.X += (DrawHelpers.Hash01(p.Seed * 3 + glance * 17) - 0.5f) * 0.30f;
            lookDir.Y += (DrawHelpers.Hash01(p.Seed * 5 + glance * 29) - 0.5f) * 0.20f;
            ic = new Vector2(Math.Clamp(lookDir.X * 0.30f, -0.40f, 0.40f) * w, Math.Clamp(lookDir.Y * 0.15f, -0.09f, 0.09f) * w * open) * (1f - forward);
        }

        Mb.Clear();
        BuildSkin(in e);
        if (open >= 0.06f)
        {
            BuildSclera(in e);
            if (e.Detailed && look.VeinAlpha > 0f) BuildVeins(in e, ic, p.Seed);
            BuildIris(in e, ic, p.Seed, ctx.Time + phase);
            if (style == EyeStyle.Spiral) BuildSpiral(in e, ic, ctx.Time + phase);
            if (style == EyeStyle.Triple) BuildTriplePupils(in e, ic, ctx.Time + phase);
            if (e.Detailed && open >= 0.35f) BuildCaruncle(in e);
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

    /// <summary>
    /// The skin round the eye: a patch of cold, waxy flesh coming up out of the dark, brighter on the side the far light reaches and
    /// shaded under the brow and in the hollow beneath. It is what makes the eye part of a face rather than a hole in the dark. The
    /// dark sockets (feral, void) wear none.
    /// </summary>
    private void BuildSkin(in Pose e)
    {
        if (!e.Detailed || e.Look.ScleraScale < 0.5f) return;
        int segs = e.Large ? 18 : 12;
        const int rings = 3;
        float w = e.W;
        ReadOnlySpan<float> reach = stackalloc float[rings] { 0.60f, 0.82f, 1.00f };
        ReadOnlySpan<float> alpha = stackalloc float[rings] { 0.52f, 0.30f, 0f };
        float openness = 0.45f + 0.55f * e.Open;

        Mb.Reserve(1 + rings * segs, segs * 3 + (rings - 1) * segs * 6);
        int c0 = Mb.VertexReserved(e.ToScreen(new Vector2(0f, -0.08f * w)), e.Tint(DrawHelpers.LerpColor(SkinShade, SkinLit, 0.45f), 0.55f * openness));
        for (int r = 0; r < rings; r++)
        {
            for (int j = 0; j < segs; j++)
            {
                float a = MathF.Tau * j / segs, ca = MathF.Cos(a), sa = MathF.Sin(a);
                Vector2 local = new Vector2(ca * 1.42f * w * reach[r], -0.08f * w + sa * 0.95f * w * reach[r]);

                // Toward the light is lit; the brow ridge is lit on its crest and the hollow under the eye is not.
                float toward = 0.5f + 0.5f * (ca * e.Light.X + sa * e.Light.Y);
                float lit = 0.25f + 0.75f * toward;
                lit *= sa < 0f ? 0.80f + 0.20f * (1f - MathF.Abs(sa)) : 0.62f + 0.38f * (1f - sa);
                Mb.VertexReserved(e.ToScreen(local), e.Tint(DrawHelpers.LerpColor(SkinShade, SkinLit, DrawHelpers.Saturate(lit)), alpha[r] * openness));
            }
        }
        for (int j = 0; j < segs; j++) Mb.TriReserved(c0, c0 + 1 + j, c0 + 1 + (j + 1) % segs);
        for (int r = 0; r + 1 < rings; r++)
        {
            int a0 = c0 + 1 + r * segs, a1 = a0 + segs;
            for (int j = 0; j < segs; j++)
            {
                int jn = (j + 1) % segs;
                Mb.QuadReserved(a0 + j, a0 + jn, a1 + j, a1 + jn);
            }
        }
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
                float x = -1f + 2f * i / cols, ax = MathF.Abs(x);
                // A ball seen through a slit: bright across the middle, falling away to the corners, and lit more on the side the light is on.
                float lit = 0.80f - 0.44f * MathF.Pow(ax, 2.2f) + 0.07f * x * e.Light.X;
                float lid = 0.66f * MathF.Pow(1f - v, 2.2f) * (0.45f + 0.55f * e.Open);
                float under = 0.20f * MathF.Pow(v, 3f);
                float tone = (lit - lid - under + e.Look.ScleraTone) * e.Look.ScleraScale;

                // Pink where the vessels crowd the corners (and the inner one, by the caruncle, most).
                float inner = x * e.InnerSide > 0f ? 1.25f : 0.85f;
                float warm = Math.Clamp(e.Look.Flesh + e.Look.CornerFlesh * inner * MathF.Pow(ax, 2.6f), 0f, 0.95f);
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
    /// The iris and pupil as a polar grid, every vertex pinned between the lids. From the pupil out: a dark ruff at its edge, a warm
    /// ring, the bright collarette, the body of the iris, a soft limbal ring. The body is fibrous (radial streaks from smooth noise, so
    /// they flow rather than pinwheel) with a few dark crypts; the lid shades the top of it, and the light the tunnel gives it
    /// concentrates on the side opposite the light, as it does through a real cornea.
    /// </summary>
    private void BuildIris(in Pose e, Vector2 ic, int seed, float time, float flat = 1f)
    {
        float w = e.W, R = e.IrisRadius;
        bool orb = e.Style == EyeStyle.Orb;
        Vector2 gaze = ic.LengthSquared() > 1e-4f ? Vector2.Normalize(ic) : Vector2.UnitX;
        Vector2 across = new(-gaze.Y, gaze.X);
        int segs = e.IrisSegs;
        float shade = e.Look.ScleraScale < 0.5f ? 0.35f : 1f;
        float fx = segs >= 28 ? 5.2f : segs >= 20 ? 3.4f : 2.4f;
        float sx = DrawHelpers.Hash01(seed * 7 + 1) * 60f, sy = DrawHelpers.Hash01(seed * 7 + 2) * 60f;
        ref readonly Look L = ref e.Look;

        for (int j = 0; j <= segs; j++)
        {
            float th = MathF.Tau * j / segs, cs = MathF.Cos(th), sn = MathF.Sin(th);

            // Ring radii along this spoke, each kept clear of the one inside it (a slit pupil is taller than the iris is wide).
            float pupil = PupilRadius(in L, cs, sn, R, time);
            float rRuff = pupil + 0.05f * R;
            float rRing = MathF.Max(0.40f * R, rRuff + 0.06f * R);
            float rColl = MathF.Max(0.56f * R, rRing + 0.06f * R);
            float rBody = MathF.Max(0.82f * R, rColl + 0.08f * R);
            float rLimb = MathF.Max(0.96f * R, rBody + 0.05f * R);
            float rEdge = MathF.Max(R, rLimb + 0.03f * R);
            float rOut = rEdge + 0.07f * R;

            // Where the pupil outgrows the iris (a slit is taller than the iris is wide) there is no iris left on that spoke to draw.
            float squeeze = DrawHelpers.Saturate((pupil - 0.70f * R) / (0.22f * R));

            // Light on the far side of the iris from the light, as a lens throws it.
            float caustic = DrawHelpers.Saturate(-(cs * e.Light.X + sn * e.Light.Y));

            for (int r = 0; r < IrisRows; r++)
            {
                float rad = r switch { 0 => 0f, 1 => pupil, 2 => rRuff, 3 => rRing, 4 => rColl, 5 => rBody, 6 => rLimb, 7 => rEdge, _ => rOut };
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
                float lidShade = 1f - 0.62f * shade * under * under;

                // Fibres flow out from the pupil: noise round a circle (so it closes), changing slowly with radius.
                float rf = r / (float)(IrisRows - 1);
                float fibre = 0.76f + 0.36f * (0.5f + 0.5f * Noise.Value(cs * fx + sx, sn * fx + rf * 1.7f + sy))
                            + 0.10f * (DrawHelpers.Hash01(seed * 131 + (j % segs) * 17) - 0.5f);
                float crypt = DrawHelpers.Smooth((Noise.Value(cs * 1.7f + sx * 0.3f, sn * 1.7f + rf * 2.5f + sy * 0.3f) - 0.30f) / 0.35f);

                float fade = r >= 2 ? 1f - squeeze : 1f;
                uint col = r switch
                {
                    0 or 1 => e.Tint(Ink, 0.98f),
                    2 => e.Tint(Scale(DrawHelpers.LerpColor(L.PupilRing, Ink, 0.62f), fibre * lidShade), 0.98f),
                    3 => e.Tint(Scale(L.PupilRing, fibre * lidShade * (1f + 0.25f * caustic)), 0.98f),
                    4 => e.Tint(Scale(L.Collarette, fibre * lidShade * (1f + 0.40f * caustic)), 0.97f),
                    5 => e.Tint(Scale(L.Mid, fibre * lidShade * (1f - 0.32f * crypt) * (1f + 0.30f * caustic)), 0.97f),
                    6 => e.Tint(Scale(DrawHelpers.LerpColor(L.Mid, L.Limbus, 0.65f), lidShade * (1f - 0.20f * crypt)), 0.97f),
                    7 => e.Tint(Scale(L.Limbus, lidShade), 0.97f),
                    _ => e.Tint(Scale(L.Limbus, lidShade), 0f),
                };
                if (fade < 0.999f) col = (col & 0x00FFFFFFu) | ((uint)((col >> 24) * fade) << 24);   // alpha byte only: the colour already carries any override
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

    private const int FinePts = 11;
    private static readonly Vector2[] FineP = new Vector2[FinePts], FineN = new Vector2[FinePts];
    private static readonly float[] FineHalf = new float[FinePts];
    private static readonly uint[] FineCol = new uint[FinePts];

    /// <summary>Curve through four control points as one parameter, u in 0..1 over all three spans.</summary>
    private static Vector2 Along(Vector2[] ctl, float u)
    {
        float t = u * 3f;
        int span = Math.Min((int)t, 2);
        return Spline(ctl, span, t - span);
    }

    /// <summary>
    /// Veins on the white of the eye: fine red threads that run from each corner toward the iris, wandering, thinning and fading as they
    /// go, the larger ones branching. Thicker and more of them in the eyes that have not slept.
    /// </summary>
    private void BuildVeins(in Pose e, Vector2 ic, int seed)
    {
        float w = e.W, R = e.IrisRadius;
        int pts = e.Huge ? FinePts : e.Large ? 7 : 5;
        float big = e.Grow(30f, 42f), huge = e.Grow(60f, 80f);
        int veins = big > 0.02f ? e.Look.VeinCount : Math.Min(2, e.Look.VeinCount);
        for (int side = -1; side <= 1; side += 2)
        {
            for (int v = 0; v < veins; v++)
            {
                int h = seed * 11 + (side + 1) * 7 + v * 3;
                float vf = v < 2 ? 1f : big;          // the first two veins are always there; the rest come in with size
                if (vf < 0.02f) continue;
                float y0 = (DrawHelpers.Hash01(h) - 0.5f) * 0.20f * w * e.Open;
                float x3 = ic.X + side * R * (1.10f + 0.34f * DrawHelpers.Hash01(h + 3));
                VeinCtl[0] = new Vector2(side * 0.93f * w, y0);
                VeinCtl[1] = new Vector2(side * 0.72f * w, y0 * 1.5f + (DrawHelpers.Hash01(h + 1) - 0.5f) * 0.14f * w * e.Open);
                VeinCtl[2] = new Vector2(side * 0.53f * w, y0 * 1.8f + (DrawHelpers.Hash01(h + 2) - 0.5f) * 0.16f * w * e.Open);
                VeinCtl[3] = new Vector2(side > 0 ? MathF.Min(x3, VeinCtl[2].X - 0.04f * w) : MathF.Max(x3, VeinCtl[2].X + 0.04f * w),
                                         ic.Y + (DrawHelpers.Hash01(h + 4) - 0.5f) * 0.5f * R);
                float wander = 0.012f * w * e.Open, ph = DrawHelpers.Hash01(h + 5) * MathF.Tau;
                for (int i = 0; i < pts; i++)
                {
                    float u = i / (float)(pts - 1);
                    Vector2 p = Along(VeinCtl, u);
                    p.Y += wander * MathF.Sin(u * 9.5f + ph);
                    FineP[i] = e.ToScreen(BetweenLids(p, e.Open, w));
                    FineHalf[i] = MathF.Max(0.12f * e.Px, (0.50f * e.Px + 0.006f * w) * (1f - 0.72f * u));
                    FineCol[i] = e.Tint(VeinCol, e.Look.VeinAlpha * e.Open * vf * (1f - 0.55f * u * u));
                }
                MeshBuilder.MiterNormals(FineP.AsSpan(0, pts), FineN.AsSpan(0, pts));
                Mb.Ribbon(FineP.AsSpan(0, pts), FineN.AsSpan(0, pts), FineHalf.AsSpan(0, pts), FineCol.AsSpan(0, pts), e.Soft(0.5f * e.Px));

                // A side branch off the larger ones.
                if (huge < 0.02f || DrawHelpers.Hash01(h + 6) < 0.35f) continue;
                float u0 = 0.35f + 0.25f * DrawHelpers.Hash01(h + 7);
                Vector2 root = Along(VeinCtl, u0), ahead = Along(VeinCtl, u0 + 0.06f);
                Vector2 dir = Vector2.Normalize(ahead - root + new Vector2(1e-4f));
                float turn = (DrawHelpers.Hash01(h + 8) < 0.5f ? -1f : 1f) * (0.45f + 0.4f * DrawHelpers.Hash01(h + 9));
                Vector2 bd = new(dir.X * MathF.Cos(turn) - dir.Y * MathF.Sin(turn), dir.X * MathF.Sin(turn) + dir.Y * MathF.Cos(turn));
                for (int i = 0; i < 5; i++)
                {
                    float u = i / 4f;
                    Vector2 p = root + bd * (u * 0.20f * w) + new Vector2(-bd.Y, bd.X) * (0.012f * w * MathF.Sin(u * 5f + ph));
                    FineP[i] = e.ToScreen(BetweenLids(p, e.Open, w));
                    FineHalf[i] = MathF.Max(0.10f * e.Px, 0.30f * e.Px * (1f - 0.8f * u));
                    FineCol[i] = e.Tint(VeinCol, e.Look.VeinAlpha * 0.8f * e.Open * vf * huge * (1f - 0.8f * u));
                }
                MeshBuilder.MiterNormals(FineP.AsSpan(0, 5), FineN.AsSpan(0, 5));
                Mb.Ribbon(FineP.AsSpan(0, 5), FineN.AsSpan(0, 5), FineHalf.AsSpan(0, 5), FineCol.AsSpan(0, 5), e.Soft(0.4f * e.Px));
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

    /// <summary>
    /// The lids. Open: the shadow the upper lid throws on the eye, its thickness (a margin of flesh outside the lash line), the dark line
    /// the lashes grow from, the crease above with the fold that catches light over it, then the lower lid's margin and the wet line of
    /// tears along it, and lashes. Shut: a seam of light under one dark line. Small eyes get the shadow and one line.
    /// </summary>
    private void BuildLids(in Pose e, int seed)
    {
        float w = e.W, px = e.Px, soft = e.Soft(px);
        float shut = 1f - DrawHelpers.Smooth(e.Open / 0.30f);

        if (e.Open >= 0.06f)
        {
            // Soft shadow the upper lid throws on the eye.
            LidLine(in e, Up, 0f, 0f, 0f, e.Tint(Ink, 0.42f), tent: 0.24f * w * e.Open);
            if (!e.Detailed)
            {
                if (ShowUpperLid) LidLine(in e, Up, 0f, 0.022f * w + 0.4f * px, 0.008f * w, e.Tint(Ink, 0.90f), feather: 0.7f * px + 0.01f * w);
                return;
            }

            // The upper lid has thickness: flesh outside the lash line, darker where it turns away from the light.
            if (ShowUpperLid)
            {
                LidLine(in e, Up, -0.022f * w, 0.022f * w, 0.008f * w, e.Tint(LidFlesh, 0.50f), feather: 0.03f * w + 0.6f * soft);
                LidLine(in e, Up, 0f, 0.013f * w + 0.35f * px, 0.005f * w, e.Tint(Ink, 0.90f), feather: 0.5f * soft + 0.004f * w);
            }
            LidLine(in e, Lo, -0.018f * w * e.Open, 0.011f * w + 0.35f * px, 0.003f * w, e.Tint(_palette.Sample(0.95f), 0.55f * e.Open), feather: 0.5f * soft);

            float big = e.Grow(30f, 42f), huge = e.Grow(60f, 80f);
            if (big > 0.02f)
            {
                // The crease above, and the shadow under the eye.
                if (ShowUpperLid) LidLine(in e, Up, -0.20f * w * e.Open - 0.04f * w, 0.014f * w + 0.4f * px, 0.004f * w, e.Tint(Ink, 0.40f * big), feather: 1.2f * soft + 0.012f * w);
                LidLine(in e, Lo, 0.045f * w, 0.025f * w, 0.006f * w, e.Tint(Ink, 0.32f * big), feather: 0.03f * w + soft);
            }
            if (huge > 0.02f)
            {
                // The fold over the crease that catches the light, and the lower lid's own margin.
                if (ShowUpperLid) LidLine(in e, Up, -0.27f * w * e.Open - 0.05f * w, 0.020f * w, 0.004f * w, e.Tint(SkinLit, 0.16f * huge), feather: 0.03f * w + soft);
                LidLine(in e, Lo, 0.016f * w, 0.016f * w, 0.005f * w, e.Tint(LidFlesh, 0.38f * huge), feather: 0.02f * w + 0.6f * soft);
            }
            if (big > 0.02f) BuildLashes(in e, seed, big, huge);
        }

        if (shut > 0.01f)
        {
            // Shut: light leaking under a single dark lash line.
            LidLine(in e, Up, 0.02f * w, 0f, 0f, e.Tint(_palette.Sample(0.62f), 0.30f * shut), tent: 0.16f * w);
            LidLine(in e, Up, 0f, 0.026f * w + 0.8f * px, 0.008f * w, e.Tint(Ink, 0.90f * shut), feather: 0.7f * px + 0.01f * w);
            if (e.Style == EyeStyle.Sewn) BuildStitches(in e, seed);
        }
    }

    /// <summary>Lashes: sparse, uneven and curling up and away from the middle on the upper lid, a few short ones below. Some have fallen out.</summary>
    private void BuildLashes(in Pose e, int seed, float big, float huge)
    {
        float w = e.W, px = e.Px;
        Span<Vector2> p = stackalloc Vector2[3];
        Span<Vector2> nrm = stackalloc Vector2[3];
        Span<float> half = stackalloc float[3];

        for (int side = 0; side < 2; side++)
        {
            bool upper = side == 0;
            if (upper && !ShowUpperLid) continue;
            if (!upper && huge < 0.02f) continue;
            int n = upper ? 15 : 7;
            float span = upper ? 0.84f : 0.62f;
            for (int i = 0; i < n; i++)
            {
                int h = seed * 37 + side * 101 + i * 13;
                if (DrawHelpers.Hash01(h) < (upper ? 0.12f : 0.25f)) continue;     // a gap

                // Three in five of the upper lashes come with the eye being big; the rest, and the lower ones, as it gets bigger still.
                float grown = upper && i % 5 < 3 ? big : MathF.Min(big, huge);
                if (grown < 0.02f) continue;
                float x = -span + 2f * span * (i + 0.6f * DrawHelpers.Hash01(h + 1)) / n;
                Lids(x, e.Open, w, out float yu, out float yl);
                Lids(x + 0.03f, e.Open, w, out float yu2, out float yl2);
                float y = upper ? yu : yl, y2 = upper ? yu2 : yl2;
                Vector2 root = new(x * w, y);
                Vector2 tan = Vector2.Normalize(new Vector2(0.03f * w, y2 - y));
                Vector2 outward = upper ? new Vector2(tan.Y, -tan.X) : new Vector2(-tan.Y, tan.X);

                // Lengths run long and short in clumps; the curl varies; they fan out toward the outer corner.
                float clump = 0.55f + 0.45f * MathF.Sin(i * 1.9f + DrawHelpers.Hash01(seed + side) * 6f);
                float len = (upper ? 0.085f + 0.105f * DrawHelpers.Hash01(h + 2) * clump : 0.040f + 0.040f * DrawHelpers.Hash01(h + 2)) * w * (1f - 0.55f * x * x);
                float fan = x * (upper ? 0.60f : 0.30f) + (DrawHelpers.Hash01(h + 3) - 0.5f) * 0.45f;
                Vector2 dir = Vector2.Normalize(outward + tan * fan);
                float curl = (upper ? 0.10f + 0.28f * DrawHelpers.Hash01(h + 4) : 0.05f) * len;
                p[0] = e.ToScreen(root - outward * (0.004f * w));
                p[1] = e.ToScreen(root + dir * (len * 0.55f) + outward * (curl * 0.25f));
                p[2] = e.ToScreen(root + dir * len + (upper ? outward : -outward) * curl + tan * (x * 0.12f * len));
                half[0] = MathF.Max(0.25f * px, 0.0100f * w); half[1] = half[0] * 0.70f; half[2] = half[0] * 0.15f;
                MeshBuilder.MiterNormals(p, nrm);
                Mb.Ribbon(p, nrm, half, e.Tint(Ink, (upper ? 0.72f : 0.45f) * grown), e.Soft(0.35f * px));
            }
        }
    }

    /// <summary>The caruncle: the small pink bump in the inner corner of the eye, wet, with a fleck of light on it.</summary>
    private void BuildCaruncle(in Pose e)
    {
        float w = e.W, side = e.InnerSide;
        float x = 0.90f * side;
        Lids(x, e.Open, w, out float yu, out float yl);
        Vector2 c = new(x * w - side * 0.03f * w, 0.5f * (yu + yl) + 0.01f * w);
        float r = MathF.Max(0.06f * w, 0.9f * e.Px);

        const int segs = 9;
        Mb.Reserve(1 + 2 * segs, segs * 3 + segs * 6);
        int c0 = Mb.VertexReserved(e.ToScreen(c), e.Tint(Caruncle, 0.92f));
        for (int ring = 0; ring < 2; ring++)
        {
            float rr = ring == 0 ? r : r * 1.55f;
            for (int j = 0; j < segs; j++)
            {
                float a = MathF.Tau * j / segs;
                Vector2 q = c + new Vector2(MathF.Cos(a) * rr, MathF.Sin(a) * rr * 0.80f);
                // Keep it between the lids so it is not drawn over the skin.
                q = BetweenLids(q, e.Open, w);
                Mb.VertexReserved(e.ToScreen(q), e.Tint(ring == 0 ? DrawHelpers.LerpColor(Caruncle, Flesh, 0.5f) : Flesh, ring == 0 ? 0.80f : 0f));
            }
        }
        for (int j = 0; j < segs; j++) Mb.TriReserved(c0, c0 + 1 + j, c0 + 1 + (j + 1) % segs);
        for (int j = 0; j < segs; j++) Mb.QuadReserved(c0 + 1 + j, c0 + 1 + (j + 1) % segs, c0 + 1 + segs + j, c0 + 1 + segs + (j + 1) % segs);
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

    /// <summary>
    /// The wet glint on the cornea: a window, the shape of the lit opening at the end of the tunnel, set toward that light from the middle
    /// of the pupil whichever way the eye is rolled, and a faint answer on the far side.
    /// </summary>
    private void DrawHighlights(ImDrawListPtr dl, Vector2 uv, in Pose e, Vector2 ic, float time)
    {
        float R = e.IrisRadius, w = e.W;
        Vector2 hi = ic + e.Light * (0.30f * R) + new Vector2(0f, -0.10f * R);
        Vector2 lo = ic - e.Light * (0.30f * R) + new Vector2(0f, 0.08f * R);
        uint white = _palette.Sample(1f);
        float flick = 0.94f + 0.06f * MathF.Sin(time * 2.3f);
        float rot = MathF.Atan2(e.Sin, e.Cos) + MathF.PI * 0.125f;

        Span<float> rr = stackalloc float[3];
        Span<uint> rc = stackalloc uint[3];
        for (int g = 0; g < 2; g++)
        {
            Vector2 local = g == 0 ? hi : lo;
            float size = g == 0 ? 0.27f * R : 0.13f * R, gain = (g == 0 ? 1f : 0.30f) * (1f - 0.5f * e.Haze);
            Lids(Math.Clamp(local.X / w, -0.98f, 0.98f), e.Open, w, out float yu, out float yl);
            if (local.Y < yu + 0.05f * w || local.Y > yl - 0.05f * w) continue;

            // A soft-edged window: solid in the middle (eight sides, so the corners are rounded), a ring of fall-off, then nothing.
            rr[0] = size * 0.62f; rr[1] = size * 0.86f; rr[2] = size * 1.12f;
            rc[0] = e.Tint(white, 1.00f * gain * flick); rc[1] = e.Tint(white, 0.55f * gain * flick); rc[2] = e.Tint(white, 0f);
            MeshDraw.Radial(dl, e.ToScreen(local), uv, e.Tint(white, gain * flick), 8, rr, rc, g == 0 ? rot : 0f, g == 0 ? 0.86f : 1f, 0, 0f);
        }

        // The tear film along the lower lid catches the same light: a bright run of wet beside the glint.
        float wet = e.Grow(30f, 42f);
        if (wet > 0.02f)
        {
            float x = Math.Clamp(hi.X / w, -0.9f, 0.9f);
            Lids(x, e.Open, w, out _, out float yl);
            Span<float> hr = stackalloc float[2] { 0.14f * w, 0.30f * w };
            Span<uint> hc = stackalloc uint[2] { e.Tint(white, 0.20f * e.Open * wet), e.Tint(white, 0f) };
            MeshDraw.Radial(dl, e.ToScreen(new Vector2(x * w, yl - 0.02f * w)), uv, e.Tint(white, 0.30f * e.Open * wet), 10, hr, hc, 0f, 0.32f, 0, 0f);
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
