using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A strand drawn as live lightning: the path you give it is the bolt's channel, and the crackle (a drifting wander, a mid kink and a fast jitter), the forks and the flicker are the material's own,
/// so any smooth strand an effect owns can be shown as electricity. Honours Reveal (a leader races along the strand, bright at its head, with a pip at the tip), Dash, Depth (far bolts are dimmer,
/// smoother, wider-glowing and cooler) and Agitation (more violent kinks, more forks, brighter flicker). WidthHint is the whole glowing width in px; a 20-40 px hint gives a crisp bolt with a wide bloom.
/// </summary>
public sealed class StrokeLightning : IStrokeMaterial
{
    public string Name => "stroke.lightning";
    public string[] NaturalLanguageWords { get; } = { "lightning", "bolt", "bolts", "arc", "arcs", "electric", "electricity", "thunder", "shock" };

    private const int MaxSamples = 640;
    private const int TwigPoints = 5;

    private static readonly MeshBuilder Mb = new();

    // Scratch, UI thread only.
    private static readonly Vector2[] Hard = new Vector2[MaxSamples];     // the visible bolt, every kink
    private static readonly Vector2[] Soft = new Vector2[MaxSamples];     // the slow wander alone: bloom follows this so it reads as light, not as a fat zigzag
    private static readonly Vector2[] NrmHard = new Vector2[MaxSamples];
    private static readonly Vector2[] NrmSoft = new Vector2[MaxSamples];
    private static readonly float[] OffHard = new float[MaxSamples];
    private static readonly float[] OffSoft = new float[MaxSamples];
    private static readonly float[] Arc = new float[MaxSamples];
    private static readonly float[] Lum = new float[MaxSamples];
    private static readonly float[] Half = new float[MaxSamples];
    private static readonly float[] Fade = new float[MaxSamples];       // how far the light has faded into the nearest end, 0..1
    private static readonly uint[] Col = new uint[MaxSamples];

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        float reveal = Math.Clamp(s.Reveal, 0f, 1f);
        if (path.Count < 2 || alpha <= 0.004f || reveal <= 0.001f) return;

        float total = path.Length;
        if (total < 4f) return;

        float px = ctx.ScreenScale;
        float width = MathF.Max(4f * px, s.WidthHint);
        float depth = Math.Clamp(s.Depth, 0f, 1f);
        float agit = Math.Clamp(s.Agitation, 0f, 1f);
        float visible = total * reveal;
        float time = ctx.Time;

        // Far bolts are sampled coarsely: smoother, and cheaper where nobody can tell.
        float step = (9f + 6f * depth) * px;
        int n = (int)MathF.Ceiling(visible / step) + 1;
        if (n > MaxSamples) { step = visible / (MaxSamples - 1); n = MaxSamples; }
        if (n < 2) return;

        // 1. Channel: sample the strand evenly, then kink it sideways. The ends stay pinned to the strand (an arc is anchored), the leader's head does not.
        var offHard = OffHard.AsSpan(0, n);
        var offSoft = OffSoft.AsSpan(0, n);
        float violence = 1f + 0.8f * agit;
        Bolt.Crackle(offHard, offSoft, n, step, s.Seed, time + s.Phase * 0.37f, violence);

        float pin = 46f * px;
        for (int k = 0; k < n; k++)
        {
            float a = k == n - 1 ? visible : k * step;
            path.SampleAtArc(a, out Vector2 pos, out Vector2 tan);
            Vector2 nrm = new(-tan.Y, tan.X);
            float env = DrawHelpers.Smooth(a / pin) * DrawHelpers.Smooth((total - a) / pin);
            Arc[k] = a;
            Hard[k] = pos + nrm * (offHard[k] * env);
            Soft[k] = pos + nrm * (offSoft[k] * env);
        }
        MeshBuilder.MiterNormals(Hard.AsSpan(0, n), NrmHard.AsSpan(0, n));
        MeshBuilder.MiterNormals(Soft.AsSpan(0, n), NrmSoft.AsSpan(0, n));

        ComputeLight(in s, n, step, time, total, visible, reveal, agit, px);

        // Far light is dimmer, softer and cooler.
        float near = 1f - 0.55f * depth;
        float soften = 1f + 1.0f * depth;
        float heat = 0.22f * depth;
        bool overridden = DrawHelpers.ColorOverrideActive;

        var hard = Hard.AsSpan(0, n);
        var soft = Soft.AsSpan(0, n);
        var nh = NrmHard.AsSpan(0, n);
        var ns = NrmSoft.AsSpan(0, n);
        var half = Half.AsSpan(0, n);
        var col = Col.AsSpan(0, n);
        var lum = Lum.AsSpan(0, n);

        Mb.Clear();

        // 2. Bloom: a wide faint tent on the smooth wander.
        // The light fades into both ends (and into the leader's head) rather than being cut off flat, which on a thick strand shows as a rectangle.
        float reach = MathF.Max(8f * px, width * 0.60f * soften);
        float cap = MathF.Max(reach * 1.4f, 24f * px);
        for (int i = 0; i < n; i++)
        {
            float e = DrawHelpers.Smooth(Arc[i] / cap) * DrawHelpers.Smooth((visible - Arc[i]) / cap);
            Fade[i] = e;
            half[i] = reach * (0.30f + 0.70f * e);
            col[i] = DrawHelpers.Tint(StormColor.Glow.Sample(0.50f - heat), alpha * 0.22f * near * e * MathF.Min(lum[i], 1.6f), overridden);
        }
        Mb.Tent(soft, ns, half, col);

        // 3. Glow: a soft ribbon on the real channel, so the kinks light the air around them.
        float glowHalf = MathF.Max(0.9f * px, width * 0.07f);
        float glowFeather = MathF.Max(3f * px, width * 0.18f * soften);
        for (int i = 0; i < n; i++)
        {
            half[i] = glowHalf;
            col[i] = DrawHelpers.Tint(StormColor.Glow.Sample(0.62f + 0.10f * lum[i] - heat), alpha * 0.30f * near * Fade[i] * MathF.Min(lum[i], 1.5f), overridden);
        }
        Mb.Ribbon(hard, nh, half, col, glowFeather);

        float bodyHalf = MathF.Max(0.9f * px, width * 0.045f);

        // 4. Forks: thin twigs thrown off the channel that flicker in and out at a faster clock than the bolt itself.
        DrawForks(in s, n, visible, time, agit, near, depth, heat, alpha, px, bodyHalf, width, overridden);

        // 5. Body.
        for (int i = 0; i < n; i++)
        {
            half[i] = bodyHalf;
            col[i] = DrawHelpers.Tint(StormColor.Glow.Sample(0.82f + 0.20f * (lum[i] - 0.60f) - heat), alpha * 0.95f * near * MathF.Min(lum[i], 1.3f), overridden);
        }
        Mb.Ribbon(hard, nh, half, col, 1.0f * px);

        // 6. Core: the white-hot line down the middle, only where the light is up and not on a bolt too faint to show one.
        if (alpha * near > 0.30f)
        {
            float coreHalf = MathF.Max(0.55f * px, width * 0.020f);
            for (int i = 0; i < n; i++)
            {
                half[i] = coreHalf;
                float k = Math.Clamp((lum[i] - 0.35f) / 0.9f, 0f, 1f);
                col[i] = DrawHelpers.Tint(StormColor.Glow.Sample(1.00f), alpha * near * (0.45f + 0.55f * k), overridden);
            }
            Mb.Ribbon(hard, nh, half, col, 0.8f * px);
        }

        Mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));

        // The leader: a bright pip at the head while the bolt is still travelling.
        if (reveal < 0.999f && s.TipFlare > 0.001f)
            DrawTip(dl, Hard[n - 1], width, Math.Clamp(s.TipFlare, 0f, 1f) * alpha, ctx);
    }

    /// <summary>Lum (about 0.45 to 2): a steady flicker in blocks of four samples, two surges racing along the channel, a hot head while the leader travels, agitation, and an optional dash mask.</summary>
    private static void ComputeLight(in StrokePrimitive s, int n, float step, float time, float total, float visible, float reveal, float agit, float px)
    {
        float pulseW = 60f * px;
        Span<float> at = stackalloc float[2];
        Span<float> gain = stackalloc float[2];
        for (int k = 0; k < 2; k++)
        {
            float dir = DrawHelpers.Hash01(s.Seed * 13 + k * 101 + 5) < 0.5f ? 1f : -1f;
            float speed = (700f + 700f * DrawHelpers.Hash01(s.Seed + k * 37 + 9)) * px * dir;
            float p = (DrawHelpers.Hash01(s.Seed + k * 17 + 3) + s.Phase * 0.173f + time * speed / MathF.Max(total, 1f)) % 1f;
            at[k] = (p < 0f ? p + 1f : p) * total;
            gain[k] = 0.60f + 0.40f * DrawHelpers.Hash01(s.Seed + k * 29 + 1);
        }

        float dashPeriod = s.Dash > 0f ? LightStrand.FitDash(s.Dash * px, total, false) : 0f;
        float flickerTick = time * (11f + 8f * agit) + DrawHelpers.Hash01(s.Seed + 77) * 40f;
        float head = 58f * px;
        bool travelling = reveal < 0.999f;

        for (int i = 0; i < n; i++)
        {
            float a = Arc[i];
            float flick = 0.5f + 0.5f * Bolt.Level(s.Seed + 301, i, 4, flickerTick, false);
            float lum = 0.52f + 0.26f * flick;

            for (int k = 0; k < 2; k++)
            {
                float g = (a - at[k]) / pulseW;
                if (g > -3f && g < 3f) lum += gain[k] * 0.95f * MathF.Exp(-g * g);
            }

            if (travelling)
            {
                float behind = visible - a;
                if (behind >= 0f) lum += 1.25f * MathF.Exp(-behind / head);
            }

            if (agit > 0.001f) lum += agit * (0.30f + 0.25f * MathF.Sin(time * 41f + a * 0.06f));

            if (dashPeriod > 0f)
            {
                float u = a / dashPeriod;
                u -= MathF.Floor(u);
                const float duty = 0.58f, soft = 0.10f;
                lum *= DrawHelpers.Smooth(u / soft) * (1f - DrawHelpers.Smooth((u - (duty - soft)) / soft));
            }
            Lum[i] = lum;
        }
    }

    private static void DrawForks(in StrokePrimitive s, int n, float visible, float time, float agit, float near, float depth, float heat,
                                  float alpha, float px, float bodyHalf, float width, bool overridden)
    {
        if (n < 8) return;
        int count = Math.Clamp((int)(visible / (230f * px)), 1, 6);
        float forkTick = time * (9f + 6f * agit) + DrawHelpers.Hash01(s.Seed + 5) * 30f;
        int tick = (int)MathF.Floor(forkTick);
        float lenScale = Math.Clamp(width / (32f * px), 0.6f, 1.4f);
        float chance = 0.50f + 0.40f * agit;

        Span<Vector2> pts = stackalloc Vector2[TwigPoints];
        Span<Vector2> nrm = stackalloc Vector2[TwigPoints];
        Span<float> half = stackalloc float[TwigPoints];
        Span<float> reach = stackalloc float[TwigPoints];
        Span<uint> body = stackalloc uint[TwigPoints];
        Span<uint> bloom = stackalloc uint[TwigPoints];

        for (int i = 0; i < count; i++)
        {
            int fs = s.Seed * 31 + i * 977;
            if (DrawHelpers.Hash01(fs + tick * 131 + 5) > chance) continue;

            float u = (i + DrawHelpers.Hash01(fs + tick * 17 + 1)) / count;
            int idx = Math.Clamp((int)(u * (n - 1)), 2, n - 3);
            Vector2 root = Hard[idx];
            Vector2 d = Hard[idx + 1] - Hard[idx - 1];
            float dl2 = d.LengthSquared();
            if (dl2 < 1e-4f) continue;
            Vector2 tan = d / MathF.Sqrt(dl2);

            float side = DrawHelpers.Hash01(fs + tick * 3 + 2) < 0.5f ? -1f : 1f;
            float ang = 0.45f + 0.55f * DrawHelpers.Hash01(fs + tick * 7 + 3);
            float len = (46f + 100f * DrawHelpers.Hash01(fs + tick * 11 + 4)) * px * lenScale * (0.65f + 0.70f * agit);
            Bolt.Twig(pts, root, Bolt.Rotate(tan, side * ang), len, fs + 17, tick, 0.75f);
            MeshBuilder.MiterNormals(pts, nrm);

            for (int j = 0; j < TwigPoints; j++)
            {
                float taper = 1f - j / (float)TwigPoints;
                half[j] = MathF.Max(0.5f * px, bodyHalf * 0.55f * taper);
                reach[j] = 7f * px * (0.4f + 0.6f * taper);
                body[j] = DrawHelpers.Tint(StormColor.Glow.Sample(0.84f - heat), alpha * 0.62f * near * taper, overridden);
                bloom[j] = DrawHelpers.Tint(StormColor.Glow.Sample(0.50f - heat), alpha * 0.18f * near * taper, overridden);
            }
            Mb.Tent(pts, nrm, reach, bloom);
            Mb.Ribbon(pts, nrm, half, body, 0.8f * px);
        }
    }

    private static void DrawTip(ImDrawListPtr dl, Vector2 tip, float width, float k, in MaterialContext ctx)
    {
        if (k <= 0.004f) return;
        Span<float> rr = stackalloc float[2] { width * 0.30f, width * 1.0f };
        Span<uint> cc = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(StormColor.Glow.Sample(0.70f), 0.45f * k),
            DrawHelpers.WithAlpha(StormColor.Glow.Sample(0.40f), 0f),
        };
        MeshDraw.Radial(dl, tip, MeshDraw.WhiteUv(ctx.Time), DrawHelpers.WithAlpha(StormColor.Glow.Sample(1f), 0.95f * k),
                        10, rr, cc, 0f, 1f, 0, 0f);
    }

    public ReadOnlySpan<StrokeEmission> Emissions => Shed;

    // Sparks thrown off the channel and pulled down, and short crackles that hop off it and die.
    private static readonly StrokeEmission[] Shed =
    {
        new(Role: PrimitiveRole.Spark,
            DensityPer100px: 0.16f,
            SpeedMin: 70f, SpeedMax: 320f,
            LifespanMin: 0.25f, LifespanMax: 0.65f,
            SizeMin: 1.0f, SizeMax: 2.4f,
            SpreadRadians: MathF.PI,
            BiasVelocity: Vector2.Zero,
            Gravity: new Vector2(0f, 700f),
            RenderMaterial: "particle.spark"),
        new(Role: PrimitiveRole.Arc,
            DensityPer100px: 0.45f,
            SpeedMin: 0f, SpeedMax: 14f,
            LifespanMin: 0.10f, LifespanMax: 0.26f,
            SizeMin: 7f, SizeMax: 15f,
            SpreadRadians: MathF.PI,
            BiasVelocity: Vector2.Zero,
            RenderMaterial: "particle.arc"),
    };

    public ReadOnlySpan<ImpactEmission> ImpactEmissions => Hit;

    // A strike landing: a spray of sparks thrown along the surface and a few big crackles.
    private static readonly ImpactEmission[] Hit =
    {
        new(Role: PrimitiveRole.Spark,
            CountMin: 4, CountMax: 10,
            SpeedMin: 260f, SpeedMax: 1100f,
            LifespanMin: 0.30f, LifespanMax: 0.80f,
            SizeMin: 1.2f, SizeMax: 2.8f,
            ConeRadians: 1.25f,
            Gravity: new Vector2(0f, 1400f),
            RenderMaterial: "particle.spark"),
        new(Role: PrimitiveRole.Arc,
            CountMin: 2, CountMax: 4,
            SpeedMin: 40f, SpeedMax: 160f,
            LifespanMin: 0.12f, LifespanMax: 0.30f,
            SizeMin: 12f, SizeMax: 26f,
            ConeRadians: 1.6f,
            RenderMaterial: "particle.arc"),
    };
}
