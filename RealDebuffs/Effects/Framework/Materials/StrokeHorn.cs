using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>A horn: a tapering tube of near-black keratin, ringed with growth ridges that crowd toward the tip</summary>
public sealed class StrokeHorn : IStrokeMaterial
{
    public string Name => "stroke.horn";
    public string[] NaturalLanguageWords { get; } = { "horn", "horns", "antler", "antlers", "tusk", "tusks" };

    private static readonly Matcap Keratin = new(SurfacePresets.Horn);

    // Across the horn: a zero-alpha fringe either side of nine lit columns.
    private static readonly float[] Across = { -1.14f, -1.00f, -0.74f, -0.42f, -0.12f, 0.12f, 0.42f, 0.74f, 1.00f, 1.14f };
    private const int Cols = 10;

    private static readonly LightStrand Strand = new();
    private static readonly MeshBuilder Mb = new();

    private static readonly Vector3 Underlight = new(1.00f, 0.30f, 0.05f);
    private static readonly Vector3 RimFire = new(1.00f, 0.22f, 0.04f);

    /// <summary>Half-width at fraction s of the horn's length: a gentle belly near the base, then a long taper to a point.</summary>
    private static float Radius(float s, float r0) => r0 * MathF.Pow(MathF.Max(0f, 1f - s), 0.82f) * (1f + 0.07f * MathF.Sin(s * 3.1f));

    public float RadiusAt(in StrokePrimitive s, float arc, float shortSide)
    {
        float total = MathF.Max(1f, s.Path.Length);
        return MathF.Max(0.6f, Radius(arc / total, 0.5f * MathF.Max(6f, s.WidthHint)));
    }

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        float reveal = Math.Clamp(s.Reveal, 0f, 1f);
        if (path.Count < 2 || alpha <= 0.004f || reveal <= 0.001f) return;

        float total = path.Length;
        if (total < 8f) return;

        float px = ctx.ScreenScale;
        float r0 = 0.5f * MathF.Max(6f * px, s.WidthHint);
        float depth = Math.Clamp(s.Depth, 0f, 1f);
        float fire = 0.55f + 0.45f * Math.Clamp(s.Agitation, 0f, 1f);
        float visibleLen = total * reveal;
        bool overridden = DrawHelpers.ColorOverrideActive;

        var st = Strand;
        st.Resample(path, visibleLen, Math.Clamp(r0 * 0.5f, 8f * px, 22f * px));
        int n = st.Count;
        if (n < 2) return;

        var pts = st.Pts; var nrm = st.Nrm; var arc = st.Arc;

        Mb.Clear();
        Mb.Reserve(n * Cols, (n - 1) * (Cols - 1) * 6);
        int first = -1;

        for (int i = 0; i < n; i++)
        {
            float a = arc[i];
            float f = a / total;  // 0 at the base, 1 at the tip
            float r = Radius(f, r0);
            if (r < 0.2f) r = 0.2f;

            Vector2 nm = nrm[i];
            float nl = nm.Length();
            Vector2 nu = nl > 1e-4f ? nm / nl : new Vector2(0f, -1f);
            Vector2 tan = new(nu.Y, -nu.X);  // along the horn, base to tip

            // Growth ridges: close-set bands that crowd together toward the tip. Their slope tilts the surface along the horn.
            float phase = a / (0.62f * r0) * (1f + 0.9f * f);
            float ridge = 0.5f + 0.5f * MathF.Sin(phase * MathF.Tau);
            float slope = MathF.Cos(phase * MathF.Tau);
            float ridgeAmp = DrawHelpers.Smooth(0.03f, 0.18f, f) * (1f - 0.55f * f);
            float tilt = slope * 0.46f * ridgeAmp;

            // The base fades into the dark it rises from; the tip stays crisp.
            float emerge = DrawHelpers.Smooth(0.0f, 0.22f, f);
            float lowFire = fire * MathF.Exp(-f * 3.2f);  // the fire's light is strongest near where it rises

            for (int c = 0; c < Cols; c++)
            {
                float u = Across[c];
                bool fringe = c == 0 || c == Cols - 1;
                float uc = Math.Clamp(u, -1f, 1f);
                float w = uc * 0.95f;
                float nz = MathF.Sqrt(MathF.Max(0f, 1f - w * w));

                // View-space normal: across the tube, tilted along it by the ridge, renormalised.
                Vector3 nrmv = new(nu.X * w + tan.X * tilt * nz, nu.Y * w + tan.Y * tilt * nz, nz);
                nrmv = Vector3.Normalize(nrmv);
                Vector3 col = Keratin.Sample(nrmv.X, nrmv.Y);

                // The bake is display-space and lifted; a horn is nearly black
                col = col * col * 1.35f;

                // Ridged horn is paler on the crests, nearly black in the grooves.
                col *= 0.55f + 0.65f * ridge;

                // Fire beneath: light on the faces that point down, and the warm bounce near the base.
                float below = MathF.Max(0f, nrmv.Y);
                col += Underlight * (MathF.Pow(below, 1.4f) * 0.75f * fire + 0.20f * lowFire);

                // A hot rim where the tube turns away (fire behind and around it).
                float rim = MathF.Pow(1f - nz, 3f);
                col += RimFire * (rim * (0.42f + 0.70f * fire) * (0.50f + 0.50f * (1f - f)));

                if (depth > 0f) col = Vector3.Lerp(col, StrandShading.DepthFog, 0.5f * depth);

                float k = fringe ? 0f : alpha * emerge * (1f - 0.35f * depth);
                uint color = DrawHelpers.Tint(DrawHelpers.Pack(col.X, col.Y, col.Z), k, overridden);
                float off = fringe ? Math.Sign(u) * (r + 1.1f * px) : u * r;
                int v = Mb.VertexReserved(pts[i] + nm * off, color);
                if (first < 0) first = v;
            }
        }

        for (int i = 0; i + 1 < n; i++)
        {
            int a0 = first + i * Cols, b0 = a0 + Cols;
            for (int c = 0; c + 1 < Cols; c++)
                Mb.QuadReserved(a0 + c, a0 + c + 1, b0 + c, b0 + c + 1);
        }
        Mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    /// <summary>Smoke curling off a horn, and nothing else: it is stone-dead.</summary>
    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Smoke,
            DensityPer100px: 0.18f,
            SpeedMin: 6f, SpeedMax: 16f,
            LifespanMin: 2.2f, LifespanMax: 3.8f,
            SizeMin: 14f, SizeMax: 26f,
            SpreadRadians: 1.1f,
            BiasVelocity: new Vector2(0f, -12f),
            PrimaryDirection: new Vector2(0f, -1f)),
    };

    public ReadOnlySpan<ImpactEmission> ImpactEmissions => default;
}
