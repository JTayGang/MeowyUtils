namespace RealDebuffs.Effects.Framework.Materials;

/// A wispy streamer: a soft tapered trail of light fading behind its head; the path runs tail to head.
public sealed class StrokeWisp : IStrokeMaterial
{
    readonly string _name;
    readonly Palette _palette;

    /// The ashen streamer (stroke.wisp).
    public StrokeWisp() : this("stroke.wisp", HauntColor.Glow, "streamer", "streamers", "trail", "trails") { }

    /// The same streamer in another palette: register one per theme.
    internal StrokeWisp(string name, Palette palette, params string[] words)
    {
        _name = name;
        _palette = palette;
        NaturalLanguageWords = words;
    }

    public string Name => _name;
    public string[] NaturalLanguageWords { get; }

    static readonly LightStrand Strand = new();
    static readonly MeshBuilder Mb = new();

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        if (path.Count < 2 || alpha <= 0.004f) return;

        float total = path.Length;
        if (total < 6f) return;

        float px = ctx.ScreenScale;
        float width = Max(2f * px, s.WidthHint);
        float depth = Saturate(s.Depth);
        float haze = 1f - 0.5f * depth;

        // Soft, so a coarse step will do.
        var st = Strand;
        st.Resample(path, total, Math.Clamp(width * 1.1f, 12f * px, 26f * px));
        int n = st.Count;

        var pts = st.Pts.AsSpan(0, n);
        var nrm = st.Nrm.AsSpan(0, n);
        var half = st.Half.AsSpan(0, n);
        var col = st.Col.AsSpan(0, n);

        bool overridden = ColorOverrideActive;
        float shimmer = ctx.Time * 1.7f + s.Phase;
        float slow = ctx.Time * 0.8f + s.Phase * 2.3f;

        // Smoke doesn't hold a line: the tail wanders, more when agitated; then the normals again for the new line.
        float wander = width * (0.75f + 0.9f * Saturate(s.Agitation));
        for (int i = 0; i < n; i++)
        {
            float u = st.Arc[i] / total;
            pts[i] += nrm[i] * (wander * (1f - u) * Sin(st.Arc[i] * 0.017f / px + slow));
        }
        MeshBuilder.MiterNormals(pts, nrm);

        // A second strand twines about the first, so it reads as wisps of smoke and not as a stroke.
        var twin = TwinPts.AsSpan(0, n);
        var twinN = TwinNrm.AsSpan(0, n);
        for (int i = 0; i < n; i++)
        {
            float u = st.Arc[i] / total;
            twin[i] = pts[i] + nrm[i] * (width * 0.55f * (0.25f + 0.75f * (1f - u)) * Sin(st.Arc[i] * 0.011f / px + slow * 1.3f + 2.1f));
        }
        MeshBuilder.MiterNormals(twin, twinN);

        Mb.Clear();

        // 1. Bloom, under the main strand.
        for (int i = 0; i < n; i++)
        {
            float u = st.Arc[i] / total;
            half[i] = Width(u, width) * 4.4f;
            col[i] = Tint(_palette.Sample(0.24f), alpha * 0.17f * haze * Profile(u, st.Arc[i], shimmer), overridden);
        }
        Mb.Tent(pts, nrm, half, col);

        // 2. Body: the two strands, soft-edged and translucent.
        float feather = Max(2f * px, width * 0.85f);
        for (int strand = 0; strand < 2; strand++)
        {
            for (int i = 0; i < n; i++)
            {
                float u = st.Arc[i] / total;
                half[i] = Width(u, width) * (strand == 0 ? 0.62f : 0.44f);
                col[i] = Tint(_palette.Sample(0.46f + 0.24f * u), alpha * (strand == 0 ? 0.34f : 0.22f) * haze * Profile(u, st.Arc[i], shimmer), overridden);
            }
            if (strand == 0) Mb.Ribbon(pts, nrm, half, col, feather);
            else Mb.Ribbon(twin, twinN, half, col, feather);
        }

        // 3. Core: a hot hair of light along the main strand, only where very bright (a surge or flash).
        if (alpha * haze > 0.50f)
        {
            for (int i = 0; i < n; i++)
            {
                float u = st.Arc[i] / total;
                half[i] = Max(0.45f * px, Width(u, width) * 0.07f);
                col[i] = Tint(_palette.Sample(0.92f), alpha * 0.55f * haze * Pow(Profile(u, st.Arc[i], shimmer), 1.4f), overridden);
            }
            Mb.Ribbon(pts, nrm, half, col, 0.8f * px);
        }

        Mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));
    }

    static readonly Vector2[] TwinPts = new Vector2[LightStrand.MaxSamples];
    static readonly Vector2[] TwinNrm = new Vector2[LightStrand.MaxSamples];

    /// Width along the streamer: a hair at the tail, full toward the head, drawn in at the very end (no blunt tip).
    static float Width(float u, float width)
    {
        // Fattening from the tail to a rounded head: over the last fifth the width closes to nothing along an ellipse.
        float cap = u > HeadStart ? Sqrt(Max(0f, 1f - Pow((u - HeadStart) / (1f - HeadStart), 2f))) : 1f;
        return width * (0.08f + 0.92f * Pow(u, 0.9f)) * cap;
    }

    const float HeadStart = 0.80f;

    /// Brightness along the streamer: rising from the tail, fading out over the last fifth, with a slow shimmer.
    static float Profile(float u, float arc, float shimmer)
        => Pow(u, 1.4f) * (1f - Smooth((u - HeadStart) / (1f - HeadStart))) * (0.82f + 0.18f * Sin(arc * 0.021f + shimmer));
}
