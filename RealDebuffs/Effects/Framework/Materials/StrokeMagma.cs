using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>A bar of molten metal laid along a path.</summary>
public sealed class StrokeMagma : IStrokeMaterial
{
    private readonly string _name;
    private readonly Palette _palette;

    /// <summary>The hellfire magma (stroke.magma).</summary>
    public StrokeMagma() : this("stroke.magma", HellColor.Glow, "magma", "lava", "molten") { }

    /// <summary>The same molten bar in another palette: register one per theme.</summary>
    internal StrokeMagma(string name, Palette palette, params string[] words)
    {
        _name = name;
        _palette = palette;
        NaturalLanguageWords = words;
    }

    public string Name => _name;
    public string[] NaturalLanguageWords { get; }

    // Across the bar: a zero-alpha fringe either side, then rim, shoulder, core, shoulder, rim.
    private static readonly float[] Across = { -1.55f, -1.00f, -0.55f, 0f, 0.55f, 1.00f, 1.55f };
    private const int Cols = 7;

    private static readonly LightStrand Strand = new();
    private static readonly MeshBuilder Mb = new();

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        float reveal = Math.Clamp(s.Reveal, 0f, 1f);
        if (path.Count < 2 || alpha <= 0.004f || reveal <= 0.001f) return;

        float total = path.Length;
        if (total < 4f) return;

        float px = ctx.ScreenScale;
        float width = MathF.Max(3f * px, s.WidthHint);
        float depth = Math.Clamp(s.Depth, 0f, 1f);
        float visibleLen = total * reveal;
        float time = ctx.Time;
        bool overridden = DrawHelpers.ColorOverrideActive;
        float agit = Math.Clamp(s.Agitation, 0f, 1f);

        float step = Math.Clamp(width * 1.4f, 14f * px, 22f * px);  // the heat varies over tens of px, so it needs no finer
        var st = Strand;
        st.Resample(path, visibleLen, step);
        st.Light(in s, time, total, width, px, 0f);
        int n = st.Count;
        if (n < 2) return;

        float near = 1f - 0.55f * depth;
        float soften = 1f + 0.9f * depth;
        float cool = 0.16f * depth;  // far metal has lost its top end to the haze

        var pts = st.Pts;
        var nrm = st.Nrm;
        var arc = st.Arc;
        var lum = st.Lum;

        // The heat of each sample, once: it drives the width, the bloom and every column's colour.
        Span<float> heat = stackalloc float[Math.Min(n, 1200)];
        float phase = s.Seed * 0.0173f;
        for (int i = 0; i < heat.Length; i++)
        {
            float a = arc[i];
            float n1 = Noise.Along(a, 0.0105f, phase, time * 0.30f, total, s.Closed);
            float n2 = Noise.Along(a, 0.0340f, phase * 2.3f, time * 0.85f + 3.1f, total, s.Closed);
            float pulse = MathF.Max(0f, lum[i] - 0.85f);
            heat[i] = Math.Clamp(0.58f + 0.34f * n1 + 0.16f * n2 + 0.60f * pulse + 0.42f * agit - cool, 0.10f, 1.30f);
        }

        Mb.Clear();

        // 1. Bloom: a wide faint tent, strongest where the metal is hottest.
        {
            Span<float> half = stackalloc float[heat.Length];
            Span<uint> col = stackalloc uint[heat.Length];
            float reach = MathF.Max(9f * px, width * 1.7f * soften);
            for (int i = 0; i < heat.Length; i++)
            {
                half[i] = reach * (0.75f + 0.25f * heat[i]);
                col[i] = DrawHelpers.Tint(_palette.Sample(0.24f + 0.10f * heat[i]), alpha * 0.30f * near * Math.Min(heat[i], 1.2f), overridden);
            }
            Mb.Tent(pts.AsSpan(0, heat.Length), nrm.AsSpan(0, heat.Length), half, col);
        }

        // 2. The bar: a grid of rows along it by columns across it, so the cross-section can go from rim to core.
        Mb.Reserve(heat.Length * Cols, (heat.Length - 1) * (Cols - 1) * 6);
        int first = -1;
        for (int i = 0; i < heat.Length; i++)
        {
            float h = heat[i];
            float hw = width * 0.5f * (0.82f + 0.24f * h);

            for (int c = 0; c < Cols; c++)
            {
                float u = Across[c];
                bool fringe = c == 0 || c == Cols - 1;
                float uc = Math.Clamp(u, -1f, 1f);
                float prof = 1f - uc * uc;  // 1 at the core, 0 at the rim

                // Temperature across the section: the core carries the full heat, the rim well under it.
                float temp = h * MathF.Pow(Math.Max(prof, 0f), 0.85f) + 0.04f;
                float cv = Math.Clamp(0.10f + 0.90f * temp, 0f, 1f);
                float a = fringe ? 0f : Math.Clamp(prof * 2.4f, 0f, 1f) * (0.62f + 0.38f * MathF.Min(h, 1f));

                uint color = DrawHelpers.Tint(_palette.Sample(cv), alpha * near * a, overridden);
                float off = fringe ? Math.Sign(u) * (hw + 1.2f * px * soften) : u * hw;
                int v = Mb.VertexReserved(pts[i] + nrm[i] * off, color);
                if (first < 0) first = v;
            }
        }
        for (int i = 0; i + 1 < heat.Length; i++)
        {
            int a0 = first + i * Cols, b0 = a0 + Cols;
            for (int c = 0; c + 1 < Cols; c++)
                Mb.QuadReserved(a0 + c, a0 + c + 1, b0 + c, b0 + c + 1);
        }
        Mb.Flush(dl, MeshDraw.WhiteUv(time));

        // The pen: where it is still being laid, the metal is white-hot.
        if (reveal < 0.999f && s.TipFlare > 0.001f)
            DrawTip(dl, pts[heat.Length - 1], width, Math.Clamp(s.TipFlare, 0f, 1f) * alpha * near, in ctx, overridden);
    }

    private void DrawTip(ImDrawListPtr dl, Vector2 tip, float width, float k, in MaterialContext ctx, bool overridden)
    {
        if (k <= 0.004f) return;
        Span<float> rr = stackalloc float[2] { width * 0.9f, width * 2.8f };
        Span<uint> cc = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(_palette.Sample(0.70f), 0.40f * k),
            DrawHelpers.WithAlpha(_palette.Sample(0.30f), 0f),
        };
        MeshDraw.Radial(dl, tip, MeshDraw.WhiteUv(ctx.Time), DrawHelpers.WithAlpha(_palette.Sample(1f), 0.95f * k), 12, rr, cc, 0f, 1f, 0, 0f);
        dl.AddCircleFilled(tip, MathF.Max(1.4f, width * 0.36f), DrawHelpers.Tint(_palette.Sample(1f), 0.95f * k, overridden));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    /// <summary>Sparks lifting off the hot metal.</summary>
    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Cinder,
            DensityPer100px: 0.75f,
            SpeedMin: 18f, SpeedMax: 80f,
            LifespanMin: 0.9f, LifespanMax: 2.0f,
            SizeMin: 1.6f, SizeMax: 3.4f,
            SpreadRadians: 1.2f,
            BiasVelocity: new Vector2(0f, -34f),
            PrimaryDirection: new Vector2(0f, -1f)),
    };

    public ReadOnlySpan<ImpactEmission> ImpactEmissions => Hit;

    /// <summary>Struck, it throws a shower of sparks.</summary>
    private static readonly ImpactEmission[] Hit =
    {
        new(Role: PrimitiveRole.Cinder, CountMin: 10, CountMax: 20,
            SpeedMin: 140f, SpeedMax: 520f, LifespanMin: 0.7f, LifespanMax: 1.6f,
            SizeMin: 1.8f, SizeMax: 4.0f, ConeRadians: 1.1f, Gravity: new Vector2(0f, 320f)),
    };
}
