namespace RealDebuffs.Effects.Framework.Materials;

/// One frame of a spiral tunnel: set by the effect, drawn by RegionVortex, and the place both agree on (see At).
internal sealed class VortexField : IRegionState
{
    /// Heights are px toward the viewer at 1080p (LayerCamera's); the far end is a light, the near end is past the lens.
    public const float FarHeight = -6000f, NearHeight = 700f;

    /// The wall's radius at the lens and at the far end, as fractions of the short side at the focal plane.
    public const float WallRadius = 0.90f, Narrow = 0.50f;

    /// Length of one swell of the throat: the tunnel's whole length divided by seven, so a lap never jumps.
    public const float RippleEvery = (NearHeight - FarHeight) / 7f;

    public readonly TunnelField.WallTexture Wall = new();
    public TunnelFlight Flight = null!;
    public LayerCamera Cam;
    public int Seed, Arms = 3;
    public float Px, Short, Time;

    /// Twist of the walls per px of height, and roll of the whole view, radians; so the arms are helices.
    public float Swirl, Roll;

    public float Breath = 1f, Alpha = 1f;

    /// 0..1: the tunnel builds out from its far end.
    public float Appear = 1f;

    /// The fog thins to nothing inside the eye (EyeIn, a fraction of the short side from the centre), reaching full strength by EyeOut.
    public const float EyeIn = 0.24f, EyeOut = 0.62f, VeilMax = 0.60f;

    /// Spiral: even arm count, band gap (short side), wind direction, turn, and drawn extent (0..1 from the centre).
    public int SpiralArms = 2;
    public float SpiralPitch = 0.09f, SpiralHand = 1f, SpiralTurn, SpiralReach = 1f;

    /// A bright ring racing down the tunnel: its height and strength.
    public float Front, FrontGain;

    float _bendPhase, _bendDir = 1f;

    public void Begin(int seed)
    {
        Seed = seed;
        _bendPhase = Hash01(seed + 14) * Tau;
        _bendDir = Sign(seed + 15);
    }

    /// Where the tunnel's middle is at this height (px at 1080p): it wanders as it recedes.
    public Vector2 Bend(float hu)
    {
        float s = Math.Clamp((NearHeight - hu) / (NearHeight - FarHeight), 0f, 1.25f);
        float phi = _bendPhase + _bendDir * 0.14f * Time;
        var d = Dir(phi);
        return d * (Short * 0.16f * s * s) + new Vector2(-d.Y, d.X) * (Short * 0.04f * s * Sin(2.4f * s + 0.33f * Time + _bendPhase * 3f));
    }

    /// The wall's radius in px at this height: a funnel that narrows toward the light, swelling and easing as it passes.
    public float RadiusAt(float hu)
    {
        float funnel = Narrow + (1f - Narrow) * Pow(Flight.Near(hu), 0.6f);
        float swell = 1f + 0.035f * Sin(Tau * (hu - Flight.Travel) / RippleEvery);
        return WallRadius * Short * Breath * funnel * swell;
    }

    /// Where the point at angle theta, this fraction of the wall's radius, at height hu lands on screen, and its magnification.
    public Vector2 At(float theta, float hu, float frac, out float scale)
    {
        float a = theta + Swirl * hu + Roll, r = RadiusAt(hu) * frac;
        Vector2 b = Bend(hu);
        return Cam.Project(r * Cos(a) + b.X, r * Sin(a) + b.Y, hu * Px, out scale);
    }
}

/// Flat spiral behind a banded fog tunnel: a plain region draws the spiral, a Veil one the fog; a flat fill without a VortexField.
internal sealed class RegionVortex : RegionMaterial<VortexField>
{
    public override string Name => "region.vortex";

    const int Rings = 40, Segs = 144;
    const float MaxScale = 1.75f, TextureEvery = 2600f, BandEvery = 1150f;
    static readonly Vector2[] Pos = new Vector2[(Rings + 1) * (Segs + 1)];
    static readonly uint[] Col = new uint[(Rings + 1) * (Segs + 1)];
    static readonly float[] Cos = Table(MathF.Cos), Sin = Table(MathF.Sin);

    static float[] Table(Func<float, float> f)
    {
        var t = new float[Segs + 1];
        for (int j = 0; j <= Segs; j++) t[j] = f(Tau * j / Segs);
        return t;
    }

    protected override void DrawWithoutState(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx) => FlatFill(dl, in r, in ctx);

    protected override void Draw(ImDrawListPtr dl, VortexField f, in RegionPrimitive r, in MaterialContext ctx)
    {
        float a = r.Alpha * ctx.Alpha * f.Alpha;
        if (a <= 0.003f) return;

        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        if (!r.Veil) { DrawSpiral(dl, f, a, (r.Min + r.Max) * 0.5f, r.Max - r.Min, uv); return; }

        f.Wall.Ensure(f.Seed);
        DrawWall(dl, f, a, uv, (r.Min + r.Max) * 0.5f);
    }

    // The spiral: Archimedean arms, alternately ink and pearl with clear gaps between, so it reads over a dark scene and a bright one.
    const int SpiralCols = 4, MaxSamples = 1600;
    const float Duty = 0.5f, SpiralAlpha = 0.15f, Step = 0.06f;   // Step: radians between samples, small enough that no corner shows
    static readonly MeshBuilder Mb = new();

    static void DrawSpiral(ImDrawListPtr dl, VortexField f, float a, Vector2 centre, Vector2 size, Vector2 uv)
    {
        if (f.SpiralReach <= 0.01f) return;

        float px = f.Px, pitch = f.SpiralPitch * f.Short;
        float perRad = f.SpiralArms * pitch / Tau;                          // radius gained per radian along an arm
        float rEnd = (0.5f * size.Length() + pitch) * f.SpiralReach, tip = 0.18f * rEnd;
        float hw0 = pitch * Duty * 0.5f, hole = 0.9f * pitch;                      // arms are pointed at both ends: they grow from the middle over one hole's radius
        float k = a * SpiralAlpha;

        uint pearl = MemoryColor.Glow.Sample(0.90f);  // sampled per draw: a static would bake whichever override was active at first use
        uint inkClear = WithAlpha(MemoryColor.Ink, 0f), pearlClear = WithAlpha(pearl, 0f);
        Mb.Reserve(f.SpiralArms * MaxSamples * SpiralCols, f.SpiralArms * MaxSamples * 6 * (SpiralCols - 1));

        for (int arm = 0; arm < f.SpiralArms; arm++)
        {
            bool ink = arm % 2 == 0;
            uint clear = ink ? inkClear : pearlClear, solid = WithAlpha(ink ? MemoryColor.Ink : pearl, k);
            float offset = Tau * arm / f.SpiralArms + f.SpiralTurn;
            int first = Mb.Vertices, rows = 0;

            float phi = 0f;
            while (rows < MaxSamples)
            {
                float r = Min(perRad * phi, rEnd);
                float hw = hw0 * Smooth(r / hole) * (1f - Smooth((r - (rEnd - tip)) / tip));
                float feather = Min(Max(1.5f * px, 0.08f * hw), hw);
                float ang = f.SpiralHand * phi + offset;
                var dir = Dir(ang);

                float i0 = Max(0f, r - hw - feather), i1 = Max(i0, r - hw + feather), o1 = r + hw - feather, o0 = r + hw + feather;
                Mb.VertexReserved(centre + dir * i0, clear);
                Mb.VertexReserved(centre + dir * i1, solid);
                Mb.VertexReserved(centre + dir * o1, solid);
                Mb.VertexReserved(centre + dir * o0, clear);
                rows++;

                if (r >= rEnd) break;
                phi += Step;
            }
            Mb.StitchGrid(first, rows, SpiralCols);
        }
        Mb.Flush(dl, uv);
    }

    static void DrawWall(ImDrawListPtr dl, VortexField f, float a, Vector2 uv, Vector2 centre)
    {
        var fl = f.Flight;
        float px = f.Px, depth = f.Cam.Distance / px;
        float scaleFar = f.Cam.ScaleAt(VortexField.FarHeight * px);
        int i = 0;

        for (int ring = 0; ring <= Rings; ring++)
        {
            // Rings are spaced evenly in magnification, so on screen they are evenly spaced.
            float sc = Lerp(scaleFar, MaxScale, ring / (float)Rings);
            float hu = Max(VortexField.FarHeight, depth * (1f - 1f / sc));
            float near = fl.Near(hu), s = hu - fl.Travel;

            // The wall builds out from the light, edges soft; far fog is thin and low in contrast.
            float mask = Smooth((f.Appear * 1.4f - near) / 0.4f) * Smooth(near / 0.05f);
            float contrast = 0.35f + 0.65f * Pow(near, 0.7f);
            float glow = 0.38f * Pow(1f - near, 1.6f);
            float swell = MathF.Sin(Tau * s / VortexField.RippleEvery);
            float band = Pow(0.5f + 0.5f * MathF.Cos(Tau * s / BandEvery), 18f);
            float front = f.FrontGain > 0f ? f.FrontGain * Exp(-Pow((hu - f.Front) / 190f, 2f)) : 0f;

            float twist = f.Swirl * hu + f.Roll;
            float cs = MathF.Cos(twist), sn = MathF.Sin(twist);
            float radius = f.RadiusAt(hu);
            Vector2 bend = f.Bend(hu);

            for (int j = 0; j <= Segs; j++, i++)
            {
                // Rows and columns follow the arms (a column is a helix), so their edges never alias against the mesh.
                Pos[i] = f.Cam.Project(radius * (Cos[j] * cs - Sin[j] * sn) + bend.X, radius * (Sin[j] * cs + Cos[j] * sn) + bend.Y, hu * px, out _);

                Vector3 tex = f.Wall.Sample(j / (float)Segs, s / TextureEvery);
                float mottle = tex.X, streak = tex.Y;
                float phase = f.Arms * (Tau * j / Segs + 0.35f * (mottle - 0.5f));
                float arm = Smooth(0.08f, 0.92f, 0.5f + 0.5f * MathF.Cos(phase));
                float ridge = -MathF.Sin(phase);

                // Lit from the far end: banded arms, streaks running down the wall, the throat's swells and thin bright rings.
                float lit = 0.52f + glow + contrast * (0.95f * (arm - 0.5f) + 0.16f * ridge + 0.24f * streak - 0.14f * (1f - mottle)) + 0.10f * swell + 0.42f * band + 0.55f * front;

                // The fog follows the arms (milky bands, near-clear gaps) and thins to nothing toward the centre.
                float rim = Smooth(VortexField.EyeIn, VortexField.EyeOut, (Pos[i] - centre).Length() / f.Short);
                float veil = VortexField.VeilMax * rim * (0.10f + 0.90f * Pow(arm, 1.4f)) * (0.75f + 0.5f * (mottle - 0.5f) + 0.25f * band);
                Col[i] = WithAlpha(MemoryColor.Fog.Sample(lit), a * mask * Saturate(veil));
            }
        }

        MeshDraw.SparseGrid(dl, Segs, Rings, Pos, Col, uv);
    }
}
