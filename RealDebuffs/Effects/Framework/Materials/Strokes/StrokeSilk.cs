using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>A satin ribbon that twists as it runs, indigo outside and pearl within; pointed at both ends and broad toward its root (path runs root to tip; Twist rolls it).</summary>
public sealed class StrokeSilk : IStrokeMaterial
{
    public string Name => "stroke.silk";
    public string[] NaturalLanguageWords { get; } = { "silk", "silken", "satin", "ribbon", "ribbons", "sash", "sashes" };

    private static readonly Matcap Outside = new(new SurfaceSpec(
        Albedo: new(0.010f, 0.012f, 0.058f), F0: new(0.18f, 0.18f, 0.34f),
        Metalness: 0.35f, Roughness: 0.22f, EnvStrength: 0.55f, Ambient: 0.008f,
        Wrap: 0.30f, Scatter: new(0.020f, 0.030f, 0.110f)));

    private static readonly Matcap Inside = new(new SurfaceSpec(
        Albedo: new(0.60f, 0.58f, 0.74f), F0: new(0.10f, 0.10f, 0.14f),
        Metalness: 0.05f, Roughness: 0.42f, EnvStrength: 0.30f, Ambient: 0.10f,
        Wrap: 0.50f, Scatter: new(0.20f, 0.18f, 0.32f)));

    // Seven lit columns across the ribbon, a zero-alpha fringe either side.
    private static readonly float[] Across = { -1f, -1f, -0.66f, -0.33f, 0f, 0.33f, 0.66f, 1f, 1f };
    private const int Cols = 9;

    private const float Curl = 0.85f;  // radians the cross-section bends from edge to edge
    private const float PitchWidths = 14f;  // one full twist per this many ribbon widths
    private static readonly Vector3 Haze = new(0.70f, 0.72f, 0.88f);

    private static readonly LightStrand Strand = new();
    private static readonly MeshBuilder Mb = new();

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        float reveal = Math.Clamp(s.Reveal, 0f, 1f);
        if (path.Count < 2 || alpha <= 0.004f || reveal <= 0.001f) return;

        float total = path.Length;
        float px = ctx.ScreenScale;
        float width = MathF.Max(6f * px, s.WidthHint);
        if (total < width) return;

        float depth = Math.Clamp(s.Depth, 0f, 1f);
        float visibleLen = total * reveal;
        bool overridden = DrawHelpers.ColorOverrideActive;
        float phase = s.Twist + DrawHelpers.Hash01(s.Seed) * MathF.Tau;
        float rate = MathF.Tau / (PitchWidths * width);

        StrandShading.DrawShadow(dl, path, width * 0.9f, depth, alpha, ctx, false, visibleLen);

        var st = Strand;
        st.Resample(path, visibleLen, MathF.Max(5f * px, width * 0.2f));
        int n = st.Count;
        if (n < 2) return;

        Mb.Clear();
        Mb.Reserve(n * Cols, (n - 1) * (Cols - 1) * 6);
        int first = -1;

        for (int i = 0; i < n; i++)
        {
            float a = st.Arc[i];

            // Wide at the root, narrow toward the centre of the vortex, a point at the growing tip.
            float taper = 0.30f + 0.70f * MathF.Pow(MathF.Max(0f, 1f - a / total), 0.75f);
            float tip = DrawHelpers.Smooth((visibleLen - a) / (3.5f * width));
            float half = MathF.Max(0.5f * px, 0.5f * width * taper * tip * DrawHelpers.Smooth(a / (2.5f * width)));

            Vector2 nm = st.Nrm[i];
            float nl = nm.Length();
            Vector2 nu = nl > 1e-4f ? nm / nl : new Vector2(0f, -1f);

            // The twist drifts along the ribbon, so it never repeats exactly.
            float tau = phase + a * rate + 0.6f * MathF.Sin(a * 0.9f * rate + phase * 1.7f);
            float sinTau = MathF.Sin(tau);

            for (int c = 0; c < Cols; c++)
            {
                float u = Across[c];
                bool fringe = c == 0 || c == Cols - 1;
                float theta = tau + Curl * u;
                float cosT = MathF.Cos(theta), sinT = MathF.Sin(theta);

                // The face turned to us: indigo outside (squared, as the lifted bake is too pale), pearl lining from behind.
                Vector3 col;
                if (cosT >= 0f) { col = Outside.Sample(-nu.X * sinT, -nu.Y * sinT); col = col * col * 1.35f; }
                else col = Inside.Sample(nu.X * sinT, nu.Y * sinT);
                col *= (0.80f + 0.35f * MathF.Pow(MathF.Abs(cosT), 0.5f)) * (1f - 0.30f * DrawHelpers.Smooth(0.78f, 1f, MathF.Abs(u)));  // darker at the hem
                if (depth > 0f) col = Vector3.Lerp(col, Haze, 0.55f * depth);

                float k = fringe ? 0f : alpha * (1f - 0.30f * depth);
                float off = half * (MathF.Sin(theta) - sinTau) / Curl + (fringe ? Math.Sign(u) * 1.1f * px : 0f);
                uint color = DrawHelpers.Tint(IceColor.Pack(col.X, col.Y, col.Z), k, overridden);
                int v = Mb.VertexReserved(st.Pts[i] + nm * off, color);
                if (first < 0) first = v;
            }
        }

        Mb.StitchGrid(first, n, Cols);
        Mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));
    }
}
