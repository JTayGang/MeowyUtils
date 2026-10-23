using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A wispy streamer: a soft, tapered trail of light that fades to nothing behind its head and wanders sideways a little, like smoke.
/// The path runs from the tail (first point) to the head (last); WidthHint is the width at the head. Meant for things in motion.
/// </summary>
public sealed class StrokeWisp : IStrokeMaterial
{
    private readonly string _name;
    private readonly Palette _palette;

    /// <summary>The ashen streamer (stroke.wisp).</summary>
    public StrokeWisp() : this("stroke.wisp", HauntColor.Glow, "streamer", "streamers", "trail", "trails") { }

    /// <summary>The same streamer in another palette: register one per theme.</summary>
    internal StrokeWisp(string name, Palette palette, params string[] words)
    {
        _name = name;
        _palette = palette;
        NaturalLanguageWords = words;
    }

    public string Name => _name;
    public string[] NaturalLanguageWords { get; }

    private static readonly LightStrand Strand = new();
    private static readonly MeshBuilder Mb = new();

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        if (path.Count < 2 || alpha <= 0.004f) return;

        float total = path.Length;
        if (total < 6f) return;

        float px = ctx.ScreenScale;
        float width = MathF.Max(2f * px, s.WidthHint);
        float depth = Math.Clamp(s.Depth, 0f, 1f);
        float haze = 1f - 0.5f * depth;

        // Soft, so a coarse step will do.
        var st = Strand;
        st.Resample(path, total, Math.Clamp(width * 1.1f, 12f * px, 26f * px));
        int n = st.Count;

        var pts = st.Pts.AsSpan(0, n);
        var nrm = st.Nrm.AsSpan(0, n);
        var half = st.Half.AsSpan(0, n);
        var col = st.Col.AsSpan(0, n);

        bool overridden = DrawHelpers.ColorOverrideActive;
        float shimmer = ctx.Time * 1.7f + s.Phase;
        float slow = ctx.Time * 0.8f + s.Phase * 2.3f;

        // Smoke does not hold a line: the tail wanders, more when the strand is agitated. Then the normals again, for the new line.
        float wander = width * (0.55f + 0.8f * Math.Clamp(s.Agitation, 0f, 1f));
        for (int i = 0; i < n; i++)
        {
            float u = st.Arc[i] / total;
            pts[i] += nrm[i] * (wander * (1f - u) * MathF.Sin(st.Arc[i] * 0.017f / px + slow));
        }
        MeshBuilder.MiterNormals(pts, nrm);

        // A second strand twines about the first, so it reads as wisps of smoke and not as a stroke.
        var twin = TwinPts.AsSpan(0, n);
        var twinN = TwinNrm.AsSpan(0, n);
        for (int i = 0; i < n; i++)
        {
            float u = st.Arc[i] / total;
            twin[i] = pts[i] + nrm[i] * (width * 0.55f * (0.25f + 0.75f * (1f - u)) * MathF.Sin(st.Arc[i] * 0.011f / px + slow * 1.3f + 2.1f));
        }
        MeshBuilder.MiterNormals(twin, twinN);

        Mb.Clear();

        // 1. Bloom, under the main strand.
        for (int i = 0; i < n; i++)
        {
            float u = st.Arc[i] / total;
            half[i] = Width(u, width) * 3.2f;
            col[i] = DrawHelpers.Tint(_palette.Sample(0.22f), alpha * 0.12f * haze * Profile(u, st.Arc[i], shimmer), overridden);
        }
        Mb.Tent(pts, nrm, half, col);

        // 2. Body: the two strands, soft-edged and translucent.
        float feather = MathF.Max(1.5f * px, width * 0.45f);
        for (int strand = 0; strand < 2; strand++)
        {
            for (int i = 0; i < n; i++)
            {
                float u = st.Arc[i] / total;
                half[i] = Width(u, width) * (strand == 0 ? 0.30f : 0.20f);
                col[i] = DrawHelpers.Tint(_palette.Sample(0.50f + 0.28f * u), alpha * (strand == 0 ? 0.42f : 0.28f) * haze * Profile(u, st.Arc[i], shimmer), overridden);
            }
            if (strand == 0) Mb.Ribbon(pts, nrm, half, col, feather);
            else Mb.Ribbon(twin, twinN, half, col, feather);
        }

        // 3. Core: a hot hair of light along the main strand, only where it is bright.
        if (alpha * haze > 0.22f)
        {
            for (int i = 0; i < n; i++)
            {
                float u = st.Arc[i] / total;
                half[i] = MathF.Max(0.45f * px, Width(u, width) * 0.07f);
                col[i] = DrawHelpers.Tint(_palette.Sample(0.92f), alpha * 0.85f * haze * MathF.Pow(Profile(u, st.Arc[i], shimmer), 1.4f), overridden);
            }
            Mb.Ribbon(pts, nrm, half, col, 0.8f * px);
        }

        Mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));
    }

    private static readonly Vector2[] TwinPts = new Vector2[LightStrand.MaxSamples];
    private static readonly Vector2[] TwinNrm = new Vector2[LightStrand.MaxSamples];

    /// <summary>Width along the streamer: a hair at the tail, full toward the head, and drawn in again at the very end so it has no blunt tip.</summary>
    private static float Width(float u, float width)
        => width * (0.08f + 0.92f * MathF.Pow(u, 0.9f)) * (1f - 0.65f * DrawHelpers.Smooth((u - 0.88f) / 0.12f));

    /// <summary>Brightness along the streamer: rising to the head, with a slow shimmer running down it.</summary>
    private static float Profile(float u, float arc, float shimmer)
        => MathF.Pow(u, 1.4f) * (1f - 0.6f * DrawHelpers.Smooth((u - 0.93f) / 0.07f)) * (0.82f + 0.18f * MathF.Sin(arc * 0.021f + shimmer));
}
