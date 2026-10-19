using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A candle: a wax pillar lit by the shared key light and by its own flame, a pool of molten wax glowing at the top, a few runs of wax set hard down the side, a wick, and a flame
/// (a blue base, a pale core, an orange skirt) that flickers and leans. Position is the middle of the base and Size the pillar's half-width in px; it stands up the screen, or leans by Rotation
/// (radians clockwise from up), so a candle can float and rock.
///
/// AgeRatio is how far the wick has caught: 0 = a dark wick, 1 = burning steadily; the flame flares as it takes, so a row of candles lit one after another reads as lighting.
/// Sway leans the flame sideways (px), the draught. Variant is the flame's vigour: 0 or 255 = full, less = guttering (smaller, lower, leaning); lit candles brighten the wax and throw a pool of warm light round them.
/// As a stroke emitter it sheds candles that light as they drift up.
/// </summary>
public sealed class ParticleCandle : IParticleMaterial
{
    public string Name => "particle.candle";
    public string[] NaturalLanguageWords { get; } = { "candle", "candles", "taper", "tapers", "wax" };

    private static readonly Matcap Wax = new(SurfacePresets.Tallow);

    // Columns across the pillar: the outer two are a zero-alpha fringe that keeps the silhouette smooth.
    private static readonly float[] Across = { -1.12f, -1.00f, -0.62f, -0.20f, 0.20f, 0.62f, 1.00f, 1.12f };
    private const int Cols = 8, Rows = 5;

    private const int FlameCols = 4, FlameRows = 8;

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        float R = p.Size;
        if (k <= 0.004f || R <= 1.5f) return;

        float px = ctx.ScreenScale;
        float time = ctx.Time;
        bool overridden = DrawHelpers.ColorOverrideActive;
        Vector2 uv = MeshDraw.WhiteUv(time);

        int seed = p.Seed;
        float H = R * (5.4f + 3.4f * DrawHelpers.Hash01(seed + 1));

        float lit = DrawHelpers.Saturate(p.AgeRatio);
        float vigor = p.Variant == 0 ? 1f : DrawHelpers.Saturate((p.Variant & 0xFF) / 255f);
        float burn = DrawHelpers.Smooth(lit) * vigor;                                       // how much flame there is

        float ph = DrawHelpers.Hash01(seed + 2) * 40f;
        float flick = 0.5f + 0.5f * Noise.Value(time * 7.3f + ph, 1.3f);                    // 0..1, quick
        float slow = Noise.Value(time * 1.9f + ph, 4.1f);                                   // -1..1, slow lean
        float glow = burn * (0.80f + 0.20f * flick);

        Vector2 up = new(MathF.Sin(p.Rotation), -MathF.Cos(p.Rotation));
        Vector2 right = new(-up.Y, up.X);                                                   // screen-right when upright
        Vector2 b = p.DrawPos;
        Vector2 top = b + up * H;

        // The pool of light round a burning candle, over whatever is behind it.
        if (glow > 0.01f)
        {
            Vector2 fc = top + up * (R * 2.0f);
            Span<float> rr = stackalloc float[3] { R * 2.2f, R * 6.0f, R * 14f };
            Span<uint> rc = stackalloc uint[3]
            {
                DrawHelpers.WithAlpha(HellColor.Glow.Sample(0.80f), 0.30f * glow * k),
                DrawHelpers.WithAlpha(HellColor.Glow.Sample(0.62f), 0.11f * glow * k),
                DrawHelpers.WithAlpha(HellColor.Glow.Sample(0.45f), 0f),
            };
            MeshDraw.Radial(dl, fc, uv, DrawHelpers.WithAlpha(HellColor.Glow.Sample(0.92f), 0.55f * glow * k), 16, rr, rc, 0f, 1f, 0, 0f);
        }

        // A soft contact shadow where it stands.
        {
            Span<float> rr = stackalloc float[2] { R * 1.0f, R * 2.2f };
            Span<uint> rc = stackalloc uint[2]
            {
                DrawHelpers.Tint(0x00000000u, 0.30f * k, overridden),
                DrawHelpers.Tint(0x00000000u, 0f, overridden),
            };
            MeshDraw.Radial(dl, b + up * (R * 0.1f), uv, DrawHelpers.Tint(0x00000000u, 0.42f * k, overridden), 10, rr, rc, 0f, 0.32f, 0, 0f);
        }

        // ---- the pillar ----
        int vi = 0;
        for (int row = 0; row <= Rows - 1; row++)
        {
            float v = row / (float)(Rows - 1);                                              // 0 at the foot, 1 at the top
            Vector2 centre = b + up * (H * v);
            float flameLight = glow * MathF.Exp(-(1f - v) * H / (R * 5.5f));               // the flame lights the top of its own candle most
            float ao = 0.55f + 0.45f * MathF.Pow(v, 0.7f);                                  // darker toward the foot, where it is shadowed

            for (int c = 0; c < Cols; c++)
            {
                float u = Across[c];
                bool fringe = c == 0 || c == Cols - 1;
                float uc = Math.Clamp(u, -1f, 1f);
                float nz = MathF.Sqrt(MathF.Max(0f, 1f - uc * uc));

                // A cylinder facing the viewer: its normal runs across, a little up (the candle is seen from slightly above).
                Vector3 col = Wax.Sample(uc * 0.96f, -0.10f - 0.05f * (1f - v));
                // The room is on fire: the wax is not studio-white. Pull it down and warm it, deepen the contrast, and let the fire below throw its colour up the foot.
                col = col * col * 1.05f * new Vector3(1.00f, 0.86f, 0.70f);
                col *= ao;
                col += new Vector3(1.00f, 0.30f, 0.05f) * (0.26f * MathF.Pow(1f - v, 1.6f));

                // Wax set in runs and thin places: faint streaks down the side.
                float streak = 0.5f + 0.5f * Noise.Value(uc * 3.1f + (seed & 255) * 0.37f, v * 1.7f + 2.3f);
                col *= 0.94f + 0.10f * streak;

                // The flame's light on the wax, warmest on the side nearest it; the top of the pillar glows through its own melt.
                float facing = 0.55f + 0.45f * nz;
                col += new Vector3(1.00f, 0.50f, 0.15f) * (flameLight * 0.50f * facing);
                float melt = glow * DrawHelpers.Smooth(0.70f, 1f, v);
                col = Vector3.Lerp(col, new Vector3(1.00f, 0.70f, 0.32f), melt * 0.55f);

                float a = fringe ? 0f : 1f;
                P(vi, centre + right * (u * R), DrawHelpers.Tint(DrawHelpers.Pack(col.X, col.Y, col.Z), k * a, overridden));
                vi++;
            }
        }
        MeshDraw.Grid(dl, Cols - 1, Rows - 1, uv);

        // Wax run down the side: a thin pale rope ending in a bead, set hard.
        for (int d = 0; d < 2; d++)
        {
            float side = DrawHelpers.Hash01(seed + 30 + d * 5) < 0.5f ? -1f : 1f;
            float u0 = side * DrawHelpers.HashRange(seed + 31 + d * 5, 0.45f, 0.82f);
            float len = H * DrawHelpers.HashRange(seed + 32 + d * 5, 0.16f, 0.52f) * (d == 0 ? 1f : 0.65f);
            Vector2 s0 = top + right * (u0 * R) + up * (-R * 0.04f);
            Vector2 s1 = s0 - up * len;
            Vector3 wc = Wax.Sample(u0 * 0.9f, -0.15f) * 1.08f;
            wc += new Vector3(1.00f, 0.50f, 0.15f) * (glow * 0.28f);
            uint wcol = DrawHelpers.Tint(DrawHelpers.Pack(wc.X, wc.Y, wc.Z), 0.95f * k, overridden);
            dl.AddLine(s0, s1, wcol, MathF.Max(1.2f * px, R * 0.20f));
            dl.AddCircleFilled(s1, MathF.Max(1.0f * px, R * 0.17f), wcol);
        }

        // ---- the top: a pool of melt, bright where the flame sits on it ----
        {
            Span<float> rr = stackalloc float[2] { R * 0.70f, R * 1.02f };
            Vector3 rim = Wax.Sample(0f, -0.35f) * 1.05f;
            Vector3 pool = Vector3.Lerp(new Vector3(0.86f, 0.78f, 0.60f), new Vector3(1.00f, 0.72f, 0.34f), glow * 0.9f);
            Span<uint> rc = stackalloc uint[2]
            {
                DrawHelpers.Tint(DrawHelpers.Pack(pool.X, pool.Y, pool.Z), 0.98f * k, overridden),
                DrawHelpers.Tint(DrawHelpers.Pack(rim.X, rim.Y, rim.Z), 0.98f * k, overridden),
            };
            Vector3 centre = pool * (1f + 0.25f * glow);
            MeshDraw.Radial(dl, top, uv, DrawHelpers.Tint(DrawHelpers.Pack(centre.X, centre.Y, centre.Z), k, overridden), 14, rr, rc, 0f, 0.34f, 0, 0f);
        }

        // The wick: dark, and curled a little toward the draught.
        Vector2 wickTop = top + up * (R * 0.34f) + right * (R * 0.05f * slow);
        dl.AddLine(top, wickTop, DrawHelpers.Tint(DrawHelpers.Pack(0.06f, 0.05f, 0.045f), 0.95f * k, overridden), MathF.Max(1.1f * px, R * 0.13f));
        if (burn <= 0.01f)
        {
            // Unlit but not cold: a bead of red where it has just gone out, or is about to catch.
            if (lit > 0.0f)
                dl.AddCircleFilled(wickTop, MathF.Max(1.0f * px, R * 0.12f), DrawHelpers.WithAlpha(HellColor.Glow.Sample(0.70f), 0.9f * k));
            return;
        }

        DrawFlame(dl, uv, wickTop, up, right, R, burn, lit, flick, slow, p.Sway, k, overridden);
    }

    /// <summary>The flame: a teardrop whose temperature falls from a pale core out to an orange skirt, with a blue cone at its foot. Takes with a flare.</summary>
    private static void DrawFlame(ImDrawListPtr dl, Vector2 uv, Vector2 foot, Vector2 up, Vector2 right, float R,
                                  float burn, float lit, float flick, float slow, float sway, float k, bool overridden)
    {
        // Catching: it flares past its steady size and settles.
        float pop = 1f + 1.1f * lit * (1f - lit) * (1f - lit) * 2.6f;
        float size = burn * pop;
        float Hf = R * (4.6f + 0.8f * flick) * size;
        float Wf = R * 1.05f * (0.92f + 0.10f * flick) * MathF.Sqrt(size);
        float push = (sway + slow * R * 0.30f) * burn;                                   // how far the tip is blown sideways

        int i = 0;
        for (int row = 0; row <= FlameRows; row++)
        {
            float v = row / (float)FlameRows;
            Vector2 centre = foot + up * (Hf * v) + right * (push * v * v);
            // A teardrop: full a quarter of the way up, closing to a point. The tip flickers in width.
            float prof = MathF.Pow(MathF.Sin(MathF.PI * MathF.Pow(v, 0.58f)), 0.85f);
            float half = Wf * prof * (v > 0.6f ? 0.94f + 0.12f * MathF.Sin(v * 11f + flick * 6f) : 1f);

            for (int c = 0; c <= FlameCols; c++)
            {
                float u = (c / (float)FlameCols) * 2f - 1f;
                float rho = MathF.Abs(u);

                // Hot in the middle and low down; cool at the skirt and the tip.
                float temp = MathF.Pow(1f - rho, 1.15f) * (1f - 0.50f * v) * (0.80f + 0.20f * flick);
                uint col = HellColor.Flame.Sample(0.30f + 0.95f * temp);
                float a = k * (1f - MathF.Pow(rho, 2.2f)) * (1f - 0.35f * v * v * v) * DrawHelpers.Smooth(0f, 0.12f, v + 0.02f) * (v >= 1f ? 0f : 1f);

                // The blue cone at the foot, where it is burning cleanest.
                if (v < 0.30f && rho < 0.85f)
                {
                    float bl = (1f - v / 0.30f) * (1f - rho / 0.85f);
                    col = DrawHelpers.LerpColor(col, DrawHelpers.Pack(0.30f, 0.42f, 1.00f), bl * 0.75f);
                }

                MeshDraw.P[i] = centre + right * (u * half);
                MeshDraw.C[i] = DrawHelpers.WithAlpha(col, a);
                i++;
            }
        }
        MeshDraw.Grid(dl, FlameCols, FlameRows, uv);

        // The heart: a smaller, hotter teardrop of pale gold set low in the flame.
        {
            Vector2 heart = foot + up * (Hf * 0.27f);
            Span<float> hr = stackalloc float[2] { Wf * 0.34f, Wf * 0.80f };
            Span<uint> hc = stackalloc uint[2]
            {
                DrawHelpers.WithAlpha(HellColor.Flame.Sample(1f), 0.80f * k * burn),
                DrawHelpers.WithAlpha(HellColor.Flame.Sample(0.82f), 0f),
            };
            MeshDraw.Radial(dl, heart, uv, DrawHelpers.WithAlpha(HellColor.Flame.Sample(1f), 0.95f * k * burn), 10, hr, hc, 0f, 1.7f, 0, 0f);
        }
    }

    private static void P(int i, Vector2 pos, uint col)
    {
        MeshDraw.P[i] = pos;
        MeshDraw.C[i] = col;
    }

    // ---- as a stroke emitter: candles that light as they drift up ----

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Candle,
            DensityPer100px: 0.06f,
            SpeedMin: 6f, SpeedMax: 16f,
            LifespanMin: 3.4f, LifespanMax: 5.6f,
            SizeMin: 7f, SizeMax: 11f,
            SpreadRadians: 0.8f,
            BiasVelocity: new Vector2(0f, -10f),
            PrimaryDirection: new Vector2(0f, -1f)),
    };
}
