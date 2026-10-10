using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>One flame tongue as a CPU-shaded mesh: density (parabolic cross-section x falloff, noise-eroded) drives alpha and temperature. Layers: back, body, front, bed.</summary>
public sealed class ParticleEmber : IParticleMaterial
{
    // Straight alpha: a dim dark fringe would ink an outline round every tongue, so fringe alpha stays ~0 until density passes RimLo; TempFloor lifts the heat ramp's bottom.
    private const float RimLo = 0.12f, RimHi = 0.52f;
    private const float TempFloor = 0.20f;

    public string Name => "particle.ember";
    public string[] NaturalLanguageWords { get; } = { "fire", "flame", "flames", "ember", "embers" };

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 0.5f) return;

        float t = ctx.Time;
        int seed = p.Seed;
        float sf = (seed & 0x3FFF) * 0.0173f;           // this tongue's private offset into the noise field
        float age = p.AgeRatio;

        // Layer tuning: heat scales temperature, alphaK opacity, tall the aspect, rough how hard noise erodes the silhouette.
        float heat, alphaK, tall, rough;
        switch (p.Variant)
        {
            case 1:  heat = 0.78f; alphaK = 0.44f; tall = 1.02f; rough = 1.95f; break;   // back
            case 2:  heat = 1.22f; alphaK = 0.92f; tall = 0.86f; rough = 0.90f; break;   // front
            case 3:  heat = 1.08f; alphaK = 0.90f; tall = 0.42f; rough = 0.80f; break;   // bed: wide, low, continuous
            default: heat = 1.00f; alphaK = 0.82f; tall = 1.00f; rough = 1.00f; break;   // body
        }

        // Life envelope: catch fast, hold, then shrink and cool.
        float grow   = DrawHelpers.Smooth(age / 0.20f);
        float decay  = DrawHelpers.Smooth((age - 0.50f) / 0.50f);
        float widthK  = (0.70f + 0.30f * grow) * (1f - 0.35f * decay);
        float heightK = (0.30f + 0.70f * grow) * (1f - 0.45f * decay);
        heat *= 1f - 0.22f * decay;

        float w = p.Size * 2f * widthK;      // Size is radius-like (half-width)
        float aspect = (2.3f + 1.4f * DrawHelpers.Hash01(seed + 3)) * tall;
        float h = w * aspect * heightK;
        if (h < 1.5f) return;

        // LOD: vertex count is the cost, so soft back/bed layers and narrow tongues get fewer rows/columns.
        float px = ctx.ScreenScale;
        int rows = Math.Clamp((int)(h / 17f) + 3, 5, 11);
        if (p.Variant == 1 || p.Variant == 3) rows = Math.Max(5, rows - 3);
        int cols = w > 42f * px ? 4 : (w > 12f * px ? 3 : 2);

        // Lean: per-tongue (squared hash, usually mild), a breeze varying with screen X and time, and the tongue's own motion.
        float lr = DrawHelpers.Hash01(seed + 5) * 2f - 1f;
        float hk = DrawHelpers.Hash01(seed + 6);
        float own = lr * (0.22f + 0.42f * hk * hk);
        float breeze = Noise.Value(t * 0.42f + p.Position.X * 0.0035f, 3.7f) * 0.34f;
        float lean = Math.Clamp(own + breeze - p.Velocity.X * 0.0016f, -0.46f, 0.46f);

        Vector2 b = p.DrawPos;
        float flickerPhase = sf * 2.3f;

        // Vertices 0..cols are the skirt row (filled after the loop); the flame proper starts at 'stride'.
        int stride = cols + 1;
        int i = stride;
        for (int r = 0; r <= rows; r++)
        {
            float v = r / (float)rows;
            float vPow = v * MathF.Sqrt(v);              // v^1.5: bends grow toward the tip

            float sway = Noise.Value(sf * 3.1f + v * 1.25f, flickerPhase - t * 1.55f) * 0.60f
                       + Noise.Value(sf * 1.7f + v * 3.20f, flickerPhase * 1.7f - t * 3.60f) * 0.22f;
            float cx = b.X + lean * h * 0.50f * vPow + sway * w * 1.05f * v;
            float cy = b.Y - v * h;

            float taper = MathF.Pow(1f - v, 0.85f);
            float belly = 0.50f + 0.50f * DrawHelpers.Smooth(v / 0.16f);
            float wobble = 1f + 0.18f * MathF.Sin(v * 5.1f + t * 1.9f + flickerPhase);
            float half = 0.5f * w * taper * belly * wobble;

            float longi = MathF.Pow(1f - v, 0.55f);
            float erodeGain = (0.14f + 0.86f * DrawHelpers.Smooth(v / 0.75f)) * rough;
            float tipFade = DrawHelpers.Smooth((1f - v) / 0.10f);                 // exactly 0 at the tip vertex
            bool fine = v > 0.22f;                                                // no fine octave near the base

            for (int c = 0; c <= cols; c++)
            {
                float u = -1f + 2f * c / cols;
                float au = MathF.Abs(u);

                float cross = 1f - u * u;
                cross *= MathF.Sqrt(cross);                                       // (1-u^2)^1.5

                float n1 = 0.5f + 0.5f * Noise.Value(u * 1.5f + sf * 5.3f, v * 2.7f - t * 2.6f + flickerPhase);
                float n = n1;
                if (fine)
                {
                    float n2 = 0.5f + 0.5f * Noise.Value(u * 3.4f + sf * 2.1f, v * 5.6f - t * 4.4f);
                    n = n1 * 0.62f + n2 * 0.38f;
                }
                float erode = (n - 0.5f) * erodeGain * 1.05f;

                float d = cross * longi + erode;
                d *= 1f - DrawHelpers.Smooth((au - 0.62f) / 0.38f);              // silhouette edge is exactly 0
                d *= tipFade;
                d = d < 0f ? 0f : (d > 1f ? 1f : d);

                float temp = d * (1.30f - 0.62f * v) * heat;
                temp = TempFloor + (1f - TempFloor) * temp;

                float a = DrawHelpers.Smooth((d - RimLo) / (RimHi - RimLo));

                MeshDraw.P[i] = new Vector2(cx + u * half, cy);
                MeshDraw.C[i] = DrawHelpers.WithAlpha(FireColor.Heat(temp), a * k * alphaK);
                i++;
            }
        }

        // Skirt: the base row copied lower at zero alpha, so a lifted or mid-screen flame dissolves instead of showing a flat cut.
        float skirt = h * 0.18f;
        for (int c = 0; c <= cols; c++)
        {
            MeshDraw.P[c] = MeshDraw.P[stride + c] + new Vector2(0f, skirt);
            MeshDraw.C[c] = MeshDraw.C[stride + c] & 0x00FFFFFFu;
        }

        MeshDraw.Grid(dl, cols, rows + 1, MeshDraw.WhiteUv(t));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Ember,
            DensityPer100px: 5f,
            SpeedMin: 18f, SpeedMax: 50f,
            LifespanMin: 0.65f, LifespanMax: 1.30f,
            SizeMin: 3.5f, SizeMax: 7.5f,
            SpreadRadians: 0.55f,
            BiasVelocity: new Vector2(0f, -14f),
            PrimaryDirection: new Vector2(0f, -1f)),
    };
}

/// <summary>A glowing fleck thrown off a fire: cools along the heat ramp as it ages, flickers, and smears a streak sized from its velocity.</summary>
public sealed class ParticleCinder : IParticleMaterial
{
    public string Name => "particle.cinder";
    public string[] NaturalLanguageWords { get; } = { "cinder", "cinders", "ash", "ashes" };

    private static readonly uint White = DrawHelpers.Pack(1.00f, 0.97f, 0.85f);

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        float k = p.Brightness * ctx.Alpha;
        if (k <= 0.004f || p.Size <= 0.2f) return;

        float t = ctx.Time;
        float age = p.AgeRatio;

        float flick = 0.62f + 0.38f * Noise.Perlin(p.Seed * 0.0131f, t * 10.5f);
        uint hot = FireColor.Heat(0.88f - 0.72f * DrawHelpers.Smooth(age * 1.05f));

        float r = p.Size * (1f - 0.35f * age);
        Vector2 pos = p.DrawPos;
        Vector2 uv = MeshDraw.WhiteUv(t);
        float a = k * flick;

        float speed = p.Velocity.Length();
        if (speed > 30f)
        {
            float len = MathF.Min(speed * 0.045f, 42f * ctx.ScreenScale);
            Vector2 dir = p.Velocity / speed;
            Vector2 nrm = new Vector2(-dir.Y, dir.X) * (r * 0.9f);
            Vector2 tail = pos - dir * len;

            uint head = DrawHelpers.WithAlpha(hot, a * 0.80f);
            uint none = DrawHelpers.WithAlpha(hot, 0f);
            MeshDraw.Quad(dl, uv,
                          pos + nrm, head, pos - nrm, head,
                          tail - nrm * 0.15f, none, tail + nrm * 0.15f, none);
        }

        Span<float> rr = stackalloc float[1] { r * 4.2f };
        Span<uint>  cc = stackalloc uint[1] { DrawHelpers.WithAlpha(hot, 0f) };
        MeshDraw.Radial(dl, pos, uv, DrawHelpers.WithAlpha(hot, a * 0.32f),
                        8, rr, cc, rotation: 0f, squashY: 1f, seed: p.Seed, irregular: 0f);

        uint core = DrawHelpers.LerpColor(hot, White, MathF.Max(0f, 1f - age * 2.2f) * 0.75f);
        dl.AddCircleFilled(pos, r, DrawHelpers.WithAlpha(core, a * 0.95f));
    }
}

/// <summary>Hot fleck: white core, gold halo, velocity trail; sharp grow-in and fast fade so it reads as impact.</summary>
public sealed class ParticleSpark : IParticleMaterial
{
    public string Name => "particle.spark";
    public string[] NaturalLanguageWords { get; } = { "spark", "sparks" };

    private static readonly uint White = DrawHelpers.Pack(1.00f, 1.00f, 0.96f);
    private static readonly uint Gold  = DrawHelpers.Pack(1.00f, 0.78f, 0.32f);

    public void Draw(ImDrawListPtr dl, in ParticlePrimitive p, in MaterialContext ctx)
    {
        if (p.Brightness <= 0.005f || p.Size <= 0.3f) return;

        float alpha = p.Brightness * ctx.Alpha;
        if (alpha <= 0.005f) return;

        float size = p.Size * DrawHelpers.LifeSize(p.AgeRatio, riseEnd: 0.12f, riseFrom: 0.25f, fallStart: 0.12f, floor: 0.05f);
        if (size <= 0.3f) return;

        float heat = DrawHelpers.Hash01(p.Seed);
        uint core = DrawHelpers.LerpColor(Gold, White, heat);
        uint halo = DrawHelpers.LerpColor(Gold, White, heat * 0.5f);

        Vector2 pos = p.DrawPos;

        // Stateless trail: where the spark was 60ms ago, from its current velocity.
        float speed = p.Velocity.Length();
        if (speed > 40f)
        {
            Vector2 tail = pos - p.Velocity / speed * MathF.Min(speed * 0.06f, 60f);
            float trailAlpha = alpha * 0.55f;

            dl.AddLine(tail, pos, DrawHelpers.WithAlpha(halo, trailAlpha * 0.45f), size * 2.2f);

            // Four segments of rising alpha and width: AddLine is single-colour, so this stands in for a gradient.
            const int segments = 4;
            for (int i = 0; i < segments; i++)
            {
                float t0 = (float)i / segments;
                Vector2 a = Vector2.Lerp(tail, pos, t0);
                Vector2 b = Vector2.Lerp(tail, pos, (i + 1f) / segments);
                dl.AddLine(a, b, DrawHelpers.WithAlpha(core, trailAlpha * (0.15f + 0.85f * t0)), size * (0.55f + 1.25f * t0));
            }

            dl.AddCircleFilled(tail, size * 0.45f, DrawHelpers.WithAlpha(core, trailAlpha * 0.35f));
        }

        dl.AddCircleFilled(pos, size * 2.4f,  DrawHelpers.WithAlpha(halo, alpha * 0.16f));
        dl.AddCircleFilled(pos, size,         DrawHelpers.WithAlpha(core, alpha * 0.88f));
        dl.AddCircleFilled(pos, size * 0.45f, DrawHelpers.WithAlpha(White, alpha * 0.95f));
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    // Launched hard (1000-2000 px/s), pulled down; gusts ~0.45s apart share one direction (fan +-0.12 rad). Heavy's impact bursts are separate (StrokeChain.Hit).
    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Spark,
            DensityPer100px: 0.1f,
            SpeedMin: 1000f, SpeedMax: 2000f,
            LifespanMin: 0.30f, LifespanMax: 0.55f,
            SizeMin: 1.2f, SizeMax: 3.0f,
            SpreadRadians: 0.40f,
            BiasVelocity: Vector2.Zero,
            PrimaryDirection: new Vector2(0f, -1f),
            Gravity: new Vector2(0f, 5000f),
            ClusterWindowSeconds: 0.45f,
            ClusterConeRadians: 0.12f),
    };
}
