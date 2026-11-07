using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>One frame of a spiral tunnel: set by the effect, drawn by RegionVortex, and the place both agree on (see At).</summary>
internal sealed class VortexField : IRegionState
{
    /// <summary>Heights are px toward the viewer at 1080p (LayerCamera's); the far end is a light, the near end is past the lens.</summary>
    public const float FarHeight = -6000f, NearHeight = 700f;

    /// <summary>The wall's radius at the lens and at the far end, as fractions of the short side at the focal plane.</summary>
    public const float WallRadius = 0.90f, Narrow = 0.50f;

    /// <summary>Length of one swell of the throat: the tunnel's whole length divided by seven, so a lap never jumps.</summary>
    public const float RippleEvery = (NearHeight - FarHeight) / 7f;

    public readonly TunnelField.WallTexture Wall = new();
    public TunnelFlight Flight = null!;
    public LayerCamera Cam;
    public int Seed, Arms = 3;
    public float Px, Short, Time;

    /// <summary>Twist of the walls per px of height, and roll of the whole view, radians; so the arms are helices.</summary>
    public float Swirl, Roll;

    public float Breath = 1f, Alpha = 1f;

    /// <summary>0..1: the tunnel builds out from its far end; how much of the fog is left (a lapse thins it).</summary>
    public float Appear = 1f, Thin = 1f;

    /// <summary>The fog thins to nothing inside the eye (EyeIn, a fraction of the short side from the centre), reaching full strength by EyeOut.</summary>
    public const float EyeIn = 0.24f, EyeOut = 0.62f, VeilMax = 0.60f;

    /// <summary>The spiral behind the fog: an even number of arms, the radial gap between bands (fraction of the short side), which way it winds, how far round it has turned, and how much of it is drawn (0..1, from the centre out).</summary>
    public int SpiralArms = 2;
    public float SpiralPitch = 0.09f, SpiralHand = 1f, SpiralTurn, SpiralReach = 1f;

    /// <summary>A bright ring racing down the tunnel: its height and strength.</summary>
    public float Front, FrontGain;

    private float _bendPhase, _bendDir = 1f;

    public void Begin(int seed)
    {
        Seed = seed;
        _bendPhase = DrawHelpers.Hash01(seed + 14) * MathF.Tau;
        _bendDir = DrawHelpers.Sign(seed + 15);
    }

    /// <summary>Where the tunnel's middle is at this height (px at 1080p): it wanders as it recedes.</summary>
    public Vector2 Bend(float hu)
    {
        float s = Math.Clamp((NearHeight - hu) / (NearHeight - FarHeight), 0f, 1.25f);
        float phi = _bendPhase + _bendDir * 0.14f * Time;
        var d = new Vector2(MathF.Cos(phi), MathF.Sin(phi));
        return d * (Short * 0.16f * s * s) + new Vector2(-d.Y, d.X) * (Short * 0.04f * s * MathF.Sin(2.4f * s + 0.33f * Time + _bendPhase * 3f));
    }

    /// <summary>The wall's radius in px at this height: a funnel that narrows toward the light, swelling and easing as it passes.</summary>
    public float RadiusAt(float hu)
    {
        float funnel = Narrow + (1f - Narrow) * MathF.Pow(Flight.Near(hu), 0.6f);
        float swell = 1f + 0.035f * MathF.Sin(MathF.Tau * (hu - Flight.Travel) / RippleEvery);
        return WallRadius * Short * Breath * funnel * swell;
    }

    /// <summary>Where the point at angle theta, this fraction of the wall's radius, at height hu lands on screen, and its magnification.</summary>
    public Vector2 At(float theta, float hu, float frac, out float scale)
    {
        float a = theta + Swirl * hu + Roll, r = RadiusAt(hu) * frac;
        Vector2 b = Bend(hu);
        return Cam.Project(r * MathF.Cos(a) + b.X, r * MathF.Sin(a) + b.Y, hu * Px, out scale);
    }
}

/// <summary>A flat spiral turning behind a tunnel of fog seen from inside: the fog is a mesh banded into helical arms, thinning to nothing at the centre; with no VortexField it is a flat fill.</summary>
internal sealed class RegionVortex : RegionMaterial<VortexField>
{
    public override string Name => "region.vortex";

    private const int Rings = 40, Segs = 144;
    private const float MaxScale = 1.75f, TextureEvery = 2600f, BandEvery = 1150f;
    private static readonly Vector2[] Pos = new Vector2[(Rings + 1) * (Segs + 1)];
    private static readonly uint[] Col = new uint[(Rings + 1) * (Segs + 1)];
    private static readonly float[] Cos = Table(MathF.Cos), Sin = Table(MathF.Sin);

    private static float[] Table(Func<float, float> f)
    {
        var t = new float[Segs + 1];
        for (int j = 0; j <= Segs; j++) t[j] = f(MathF.Tau * j / Segs);
        return t;
    }

    protected override void DrawWithoutState(ImDrawListPtr dl, in RegionPrimitive r, in MaterialContext ctx) => FlatFill(dl, in r, in ctx);

    protected override void Draw(ImDrawListPtr dl, VortexField f, in RegionPrimitive r, in MaterialContext ctx)
    {
        float a = r.Alpha * ctx.Alpha * f.Alpha;
        if (a <= 0.003f) return;

        f.Wall.Ensure(f.Seed);
        Vector2 uv = MeshDraw.WhiteUv(ctx.Time);
        DrawSpiral(dl, f, a, (r.Min + r.Max) * 0.5f, r.Max - r.Min, uv);
        DrawWall(dl, f, a, uv, (r.Min + r.Max) * 0.5f);
    }

    // The spiral: Archimedean arms, alternately ink and pearl with clear gaps between, so it reads over a dark scene and a bright one.
    private const int SpiralCols = 4, MaxSamples = 1600;
    private const float Duty = 0.5f, SpiralAlpha = 0.46f, Step = 0.06f;   // Step: radians between samples, small enough that no corner shows
    private static readonly uint Pearl = MemoryColor.Glow.Sample(0.90f);
    private static readonly MeshBuilder Mb = new();

    private static void DrawSpiral(ImDrawListPtr dl, VortexField f, float a, Vector2 centre, Vector2 size, Vector2 uv)
    {
        if (f.SpiralReach <= 0.01f) return;

        float px = f.Px, pitch = f.SpiralPitch * f.Short;
        float perRad = f.SpiralArms * pitch / MathF.Tau;                          // radius gained per radian along an arm
        float rEnd = (0.5f * size.Length() + pitch) * f.SpiralReach, tip = 0.18f * rEnd;
        float hw0 = pitch * Duty * 0.5f, hole = 0.9f * pitch;                      // arms are pointed at both ends: they grow from the middle over one hole's radius
        float k = a * SpiralAlpha;

        uint inkClear = DrawHelpers.WithAlpha(MemoryColor.Ink, 0f), pearlClear = DrawHelpers.WithAlpha(Pearl, 0f);
        Mb.Reserve(f.SpiralArms * MaxSamples * SpiralCols, f.SpiralArms * MaxSamples * 6 * (SpiralCols - 1));

        for (int arm = 0; arm < f.SpiralArms; arm++)
        {
            bool ink = arm % 2 == 0;
            uint clear = ink ? inkClear : pearlClear, solid = DrawHelpers.WithAlpha(ink ? MemoryColor.Ink : Pearl, k);
            float offset = MathF.Tau * arm / f.SpiralArms + f.SpiralTurn;
            int first = Mb.Vertices, rows = 0;

            float phi = 0f;
            while (rows < MaxSamples)
            {
                float r = MathF.Min(perRad * phi, rEnd);
                float hw = hw0 * DrawHelpers.Smooth(r / hole) * (1f - DrawHelpers.Smooth((r - (rEnd - tip)) / tip));
                float feather = MathF.Min(MathF.Max(1.5f * px, 0.08f * hw), hw);
                float ang = f.SpiralHand * phi + offset;
                var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));

                float i0 = MathF.Max(0f, r - hw - feather), i1 = MathF.Max(i0, r - hw + feather), o1 = r + hw - feather, o0 = r + hw + feather;
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

    private static void DrawWall(ImDrawListPtr dl, VortexField f, float a, Vector2 uv, Vector2 centre)
    {
        var fl = f.Flight;
        float px = f.Px, depth = f.Cam.Distance / px;
        float scaleFar = f.Cam.ScaleAt(VortexField.FarHeight * px);
        int i = 0;

        for (int ring = 0; ring <= Rings; ring++)
        {
            // Rings are spaced evenly in magnification, so on screen they are evenly spaced.
            float sc = DrawHelpers.Lerp(scaleFar, MaxScale, ring / (float)Rings);
            float hu = MathF.Max(VortexField.FarHeight, depth * (1f - 1f / sc));
            float near = fl.Near(hu), s = hu - fl.Travel;

            // The wall builds out from the light, edges soft; far fog is thin and low in contrast.
            float mask = DrawHelpers.Smooth((f.Appear * 1.4f - near) / 0.4f) * DrawHelpers.Smooth(near / 0.05f);
            float contrast = 0.35f + 0.65f * MathF.Pow(near, 0.7f);
            float glow = 0.38f * MathF.Pow(1f - near, 1.6f);
            float swell = MathF.Sin(MathF.Tau * s / VortexField.RippleEvery);
            float band = MathF.Pow(0.5f + 0.5f * MathF.Cos(MathF.Tau * s / BandEvery), 18f);
            float front = f.FrontGain > 0f ? f.FrontGain * MathF.Exp(-MathF.Pow((hu - f.Front) / 190f, 2f)) : 0f;

            float twist = f.Swirl * hu + f.Roll;
            float cs = MathF.Cos(twist), sn = MathF.Sin(twist);
            float radius = f.RadiusAt(hu);
            Vector2 bend = f.Bend(hu);

            for (int j = 0; j <= Segs; j++, i++)
            {
                // Rows and columns follow the arms (a column is a helix), so their edges never alias against the mesh.
                Pos[i] = f.Cam.Project(radius * (Cos[j] * cs - Sin[j] * sn) + bend.X, radius * (Sin[j] * cs + Cos[j] * sn) + bend.Y, hu * px, out _);

                Vector3 tex = f.Wall.Sample(j / (float)Segs, s / TextureEvery);
                float mottle = tex.X, streak = tex.Y, pit = tex.Z;
                float phase = f.Arms * (MathF.Tau * j / Segs + 0.35f * (mottle - 0.5f));
                float arm = DrawHelpers.Smooth(0.08f, 0.92f, 0.5f + 0.5f * MathF.Cos(phase));
                float ridge = -MathF.Sin(phase);

                // Lit from the far end: banded arms, streaks running down the wall, the throat's swells and thin bright rings.
                float lit = 0.52f + glow + contrast * (0.95f * (arm - 0.5f) + 0.16f * ridge + 0.24f * streak - 0.14f * (1f - mottle)) + 0.10f * swell + 0.42f * band + 0.55f * front;
                float dens = 0.80f + contrast * (0.18f * (arm - 0.5f) + 0.22f * (mottle - 0.5f)) - 0.08f * pit + 0.30f * band + 0.40f * front;

                // The fog follows the arms (milky bands, near-clear gaps) and thins to nothing toward the centre.
                float rim = DrawHelpers.Smooth(VortexField.EyeIn, VortexField.EyeOut, (Pos[i] - centre).Length() / f.Short);
                float veil = VortexField.VeilMax * rim * (0.10f + 0.90f * MathF.Pow(arm, 1.4f)) * f.Thin * (0.75f + 0.5f * (mottle - 0.5f) + 0.25f * band);
                Col[i] = DrawHelpers.WithAlpha(MemoryColor.Fog.Sample(lit), a * mask * DrawHelpers.Saturate(veil));
            }
        }

        MeshDraw.SparseGrid(dl, Segs, Rings, Pos, Col, uv);
    }
}
