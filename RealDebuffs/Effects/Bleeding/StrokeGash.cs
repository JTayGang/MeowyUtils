namespace RealDebuffs.Effects.Framework;

/// A flat cut: pointed slit, torn lips, dark opening with blood pooled on its lower wall and a spreading bruise.
public sealed class StrokeGash : IStrokeMaterial
{
    public string Name => "stroke.gash";
    public string[] NaturalLanguageWords { get; } = { "gash", "gashes", "wound", "wounds", "laceration", "lacerations", "slash", "slashes" };

    // Columns across a cut: feather, lip, then the opening from one edge to the other (seven), lip, feather.
    const int Cols = 11;
    const float HalfOpen = 0.30f, LipFrac = 0.55f;
    static readonly float[] Across = { -1f, -0.66f, -0.33f, 0f, 0.33f, 0.66f, 1f };

    static readonly LightStrand Strand = new();
    static readonly MeshBuilder Mb = new();
    static readonly float[] HalfA = new float[LightStrand.MaxSamples], HalfB = new float[LightStrand.MaxSamples], Reach = new float[LightStrand.MaxSamples];

    /// Lens-shaped across the cut's length: sharp at both ends, fullest a little before the middle.
    static float Profile(float t)
    {
        t = Saturate(t);
        return Pow(Max(0f, Sin(PI * Pow(t, 0.76f))), 1.1f);
    }

    /// How open the cut is at an arc, 0 to 1: its lens shape, narrowed to a point at the pen's tip while it is drawn.
    static float Open(float arc, float total, float reveal, float px)
    {
        float tipLen = Max(22f * px, 0.12f * total);
        float edgeAt = reveal >= 0.999f ? total + tipLen : total * reveal;
        return Profile(arc / total) * Smooth((edgeAt - arc) / tipLen);
    }

    /// A torn edge: the half-width wobbles from place to place, differently on each side.
    static float Torn(float arc, float phase) =>
        Max(0.5f, 1f + 0.16f * (0.6f * Sin(arc * 0.09f + phase) + 0.4f * Sin(arc * 0.23f + phase * 1.7f)));

    // Drips only hang where the opening has a lip to hang from: not on the tapered ends, nor at the pen's tip.
    const float DripFloor = 0.42f;

    public bool CanDripAt(in StrokePrimitive s, float arc, float shortSide) =>
        Open(arc, Max(1f, s.Path.Length), s.Reveal, shortSide / 1080f) >= DripFloor;

    public float RadiusAt(in StrokePrimitive s, float arc, float shortSide)
    {
        float open = Max(2f * shortSide / 1080f, s.WidthHint) * HalfOpen;
        return Max(1f, open * (1f + LipFrac) * Profile(arc / Max(1f, s.Path.Length)));
    }

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        if (path.Count < 2 || alpha <= 0.004f || s.Reveal <= 0.001f) return;
        float total = path.Length;
        if (total < 3f) return;

        float px = ctx.ScreenScale;
        float visible = total * Saturate(s.Reveal);
        float open = Max(2f * px, s.WidthHint) * HalfOpen;
        float agit = Saturate(s.Agitation);
        bool overridden = ColorOverrideActive;
        Vector2 light = Vector2.Normalize(-StudioLighting.ShadowDirection);
        float phA = Hash01(s.Seed + 3) * Tau, phB = Hash01(s.Seed + 5) * Tau, phF = Hash01(s.Seed + 7) * Tau;

        var st = Strand;
        st.Resample(path, visible, Math.Clamp(9f * px, 6f, 14f));
        int n = st.Count;
        if (n < 2) return;

        for (int i = 0; i < n; i++)
        {
            float a = st.Arc[i];
            float prof = Open(a, total, s.Reveal, px);
            float h = open * prof;
            HalfA[i] = h * Torn(a, phA);
            HalfB[i] = h * Torn(a, phB);
            Reach[i] = prof * (open * (1f + LipFrac) * 2.4f + 4f * px);
        }

        Mb.Clear();
        Mb.Reserve(n * (Cols + 3), (n - 1) * (6 * (Cols - 1) + 12));

        // The bruise first, under everything.
        Mb.Tent(st.Pts.AsSpan(0, n), st.Nrm.AsSpan(0, n), Reach.AsSpan(0, n), Tint(Pack(BleedColor.Bruise), alpha * 0.32f, overridden));

        int first = Mb.Vertices;
        float fe = 1.6f * px;
        for (int i = 0; i < n; i++)
        {
            Vector2 p = st.Pts[i], nm = st.Nrm[i];
            float nl = nm.Length();
            Vector2 unit = nl > 1e-4f ? nm / nl : new Vector2(0f, 1f);
            float a = st.Arc[i];
            float prof = Open(a, total, s.Reveal, px);
            float fade = Smooth(prof * 4f);

            float sD = unit.Y >= 0f ? 1f : -1f;                       // which side of the cut is downhill
            float sL = Vector2.Dot(unit, light) >= 0f ? 1f : -1f;     // which side faces the light
            float sheenGain = 0.55f + 0.45f * Sin(a * 0.021f - ctx.Time * 1.4f + phF);
            float fill = 0.52f + 0.10f * Sin(a * 0.013f + phF) + 0.20f * agit;

            float hA = HalfA[i], hB = HalfB[i];
            float lipA = LipFrac * hA + 0.7f * px * (prof > 0.02f ? 1f : 0f), lipB = LipFrac * hB + 0.7f * px * (prof > 0.02f ? 1f : 0f);

            // lips and feathers
            Vertex(p - unit * (hA + lipA + fe), Lip(-1f, sD, sL, fill), 0f, alpha, overridden);
            Vertex(p - unit * (hA + lipA), Lip(-1f, sD, sL, fill), 0.85f * fade, alpha, overridden);

            for (int c = 0; c < Across.Length; c++)
            {
                float u = Across[c];
                float half = u < 0f ? hA : hB;
                Vertex(p + unit * (u * half), Inside(u, sD, sL, fill, agit, sheenGain), fade, alpha, overridden);
            }

            Vertex(p + unit * (hB + lipB), Lip(1f, sD, sL, fill), 0.85f * fade, alpha, overridden);
            Vertex(p + unit * (hB + lipB + fe), Lip(1f, sD, sL, fill), 0f, alpha, overridden);
        }
        Mb.StitchGrid(first, n, Cols);
        Mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));
    }

    static void Vertex(Vector2 pos, Vector3 rgb, float a, float alpha, bool overridden)
    {
        float k = a * alpha;
        Mb.VertexReserved(pos, VertexColor(rgb, k, AlphaBits(k), overridden));
    }

    /// A point in the opening at u (-1..1 across): dark and deep centrally, lit on the wall facing the light, blood pooled below.
    static Vector3 Inside(float u, float sD, float sL, float fill, float agit, float sheenGain)
    {
        float m = Abs(u);
        Vector3 c = Vector3.Lerp(BleedColor.Void, BleedColor.WallDark, Pow(m, 1.6f));
        if (u * sL < 0f) c += BleedColor.WallLit * (0.55f * m * m);
        c = Vector3.Lerp(c, BleedColor.WallDark * 0.6f, 0.6f * Smooth(0.75f, 1f, m));

        float v = u * sD;  // -1 uphill edge, +1 downhill edge
        float blood = Smooth((v - (1f - 2f * fill)) / 0.8f);
        Vector3 wet = Vector3.Lerp(BleedColor.Wet, BleedColor.WetHot, 0.30f + 0.45f * agit + 0.25f * sheenGain);
        c = Vector3.Lerp(c, wet, 0.92f * blood);

        float band = (v - 0.5f) / 0.22f;
        c += BleedColor.Sheen * (0.30f * Exp(-band * band) * sheenGain * blood);
        return c;
    }

    /// A lip on one side (-1 or 1): paler on the side the light finds, stained by blood spilling over the lower one.
    static Vector3 Lip(float side, float sD, float sL, float fill)
    {
        Vector3 c = side * sL > 0f ? BleedColor.LipLit : BleedColor.LipDark;
        return side * sD > 0f ? Vector3.Lerp(c, BleedColor.Wet, 0.45f * fill) : c;
    }

    // what it sheds, and what happens when it is hit

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    /// Blood running over the lower lip and letting go; only where the opening can hold it (CanDripAt), and a cut has no skin to gloss.
    static readonly StrokeEmission[] Shed =
    {
        StrokeEmission.Coated(PrimitiveRole.Goop, supply: 1.0f, clump: 0.5f, skin: 0f),
    };

    public ReadOnlySpan<ImpactEmission> ImpactEmissions => Hit;

    /// A splatter flung off the point of impact.
    static readonly ImpactEmission[] Hit =
    {
        new(Role: PrimitiveRole.Goop, CountMin: 5, CountMax: 9,
            SpeedMin: 180f, SpeedMax: 520f, LifespanMin: 1.1f, LifespanMax: 1.8f,
            SizeMin: 2.2f, SizeMax: 5.0f, ConeRadians: 1.25f, Gravity: new Vector2(0f, 1800f)),
    };
}
