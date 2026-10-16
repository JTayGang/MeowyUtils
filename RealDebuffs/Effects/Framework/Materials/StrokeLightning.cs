using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// A strand drawn as live lightning: the path you give it is the bolt's channel, and the crackle (a large-scale meander, a drifting wander, a mid kink and a fast jitter), the forks and the flicker are the
/// material's own, so any smooth strand an effect owns can be shown as electricity.
///
/// Drawn the way a camera sees a discharge: a blown-out white core that thickens as the bolt gets brighter, a pale-yellow body, hot spots where the channel kinks hard, and the surrounding air lit by a
/// smooth falloff (several stacked, widening layers rather than one band) that fades into the ends instead of stopping flat. Forks come in two levels (a twig can grow a twig).
///
/// Honours Reveal (a dim leader races along the strand, bright at its head, with a pip at the tip), SurgeAt/SurgeGain (a return stroke: a blinding run of current along the channel), Dash, Depth (far bolts
/// are dimmer, smoother, wider-glowing and cooler) and Agitation (more violent kinks, more forks, brighter flicker). WidthHint is the whole glowing width in px; 20-40 px gives a crisp bolt with a wide glow.
/// </summary>
public sealed class StrokeLightning : IStrokeMaterial
{
    public string Name => "stroke.lightning";
    public string[] NaturalLanguageWords { get; } = { "lightning", "bolt", "bolts", "arc", "arcs", "electric", "electricity", "thunder", "shock" };

    private const int MaxSamples = 640;
    private const int Stride = 3;                       // the scatter is smooth, so it is built from every third sample
    private const int MaxDecimated = MaxSamples / Stride + 2;
    private const int TwigPoints = 7, SubPoints = 4;

    private static readonly MeshBuilder Mb = new();

    // Scratch, UI thread only.
    private static readonly Vector2[] Hard = new Vector2[MaxSamples];     // the visible bolt, every kink
    private static readonly Vector2[] Soft = new Vector2[MaxSamples];     // the meander and wander alone: light follows this so it reads as light, not as a fat zigzag
    private static readonly Vector2[] NrmHard = new Vector2[MaxSamples];
    private static readonly float[] OffHard = new float[MaxSamples];
    private static readonly float[] OffSoft = new float[MaxSamples];
    private static readonly float[] Arc = new float[MaxSamples];
    private static readonly float[] Lum = new float[MaxSamples];
    private static readonly float[] Wid = new float[MaxSamples];          // width multiplier: wider at a hot spot or a surge
    private static readonly float[] Fade = new float[MaxSamples];         // how far the light has faded into the nearest end, 0..1
    private static readonly float[] Half = new float[MaxSamples];
    private static readonly uint[] Col = new uint[MaxSamples];

    private static readonly Vector2[] SoftD = new Vector2[MaxDecimated];
    private static readonly Vector2[] NrmD = new Vector2[MaxDecimated];
    private static readonly float[] HalfD = new float[MaxDecimated];
    private static readonly uint[] ColD = new uint[MaxDecimated];
    private static readonly int[] IdxD = new int[MaxDecimated];

    // The scatter around the channel: widths and strengths of four stacked layers, outermost first, and where each sits in the palette (deep orange outside, yellow near the channel).
    private static readonly float[] ScatterReach = { 1.00f, 0.56f, 0.32f, 0.17f };
    private static readonly float[] ScatterAlpha = { 0.055f, 0.075f, 0.110f, 0.160f };
    private static readonly float[] ScatterTone = { 0.44f, 0.52f, 0.62f, 0.74f };

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
        float step = (8f + 6f * depth) * px;
        int n = (int)MathF.Ceiling(visible / step) + 1;
        if (n > MaxSamples) { step = visible / (MaxSamples - 1); n = MaxSamples; }
        if (n < 2) return;

        // 1. Channel: sample the strand evenly, then kink it sideways. The ends stay pinned to the strand (an arc is anchored), the leader's head does not.
        var offHard = OffHard.AsSpan(0, n);
        var offSoft = OffSoft.AsSpan(0, n);
        Bolt.Crackle(offHard, offSoft, n, step, s.Seed, time + s.Phase * 0.37f, 1f + 0.8f * agit);

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

        ComputeLight(in s, n, time, total, visible, reveal, agit, px);

        // Far light is dimmer, softer and cooler.
        float near = 1f - 0.55f * depth;
        float soften = 1f + 1.0f * depth;
        float heat = 0.22f * depth;
        bool overridden = DrawHelpers.ColorOverrideActive;

        var hard = Hard.AsSpan(0, n);
        var nh = NrmHard.AsSpan(0, n);
        var half = Half.AsSpan(0, n);
        var col = Col.AsSpan(0, n);

        Mb.Clear();

        // 2. Scatter: the lit air. Four widening layers on a third of the samples build a smooth, exponential-looking falloff; the light fades into both ends (and into the leader's head)
        // rather than being cut off flat, which on a thick strand shows as a rectangle.
        float reach = MathF.Max(10f * px, width * 0.95f * soften);
        float cap = MathF.Max(reach * 0.9f, 24f * px);
        int m = 0;
        for (int i = 0; i < n; i += Stride) IdxD[m++] = i;
        if (IdxD[m - 1] != n - 1) IdxD[m++] = n - 1;
        for (int j = 0; j < m; j++)
        {
            int i = IdxD[j];
            SoftD[j] = Soft[i];
        }
        for (int i = 0; i < n; i++)
            Fade[i] = DrawHelpers.Smooth(Arc[i] / cap) * DrawHelpers.Smooth((visible - Arc[i]) / cap);
        MeshBuilder.MiterNormals(SoftD.AsSpan(0, m), NrmD.AsSpan(0, m));

        for (int layer = 0; layer < ScatterReach.Length; layer++)
        {
            float tone = ScatterTone[layer] - heat;
            for (int j = 0; j < m; j++)
            {
                int i = IdxD[j];
                float e = Fade[i];
                HalfD[j] = reach * ScatterReach[layer] * (0.25f + 0.75f * e) * (0.85f + 0.15f * Wid[i]);
                ColD[j] = DrawHelpers.Tint(StormColor.Glow.Sample(tone), alpha * ScatterAlpha[layer] * near * e * MathF.Min(Lum[i], 2.2f), overridden);
            }
            Mb.Tent(SoftD.AsSpan(0, m), NrmD.AsSpan(0, m), HalfD.AsSpan(0, m), ColD.AsSpan(0, m));
        }

        // 3. Glow: a soft ribbon on the real channel, so the kinks light the air right around them.
        float glowHalf = MathF.Max(0.9f * px, width * 0.07f);
        float glowFeather = MathF.Max(3f * px, width * 0.16f * soften);
        for (int i = 0; i < n; i++)
        {
            half[i] = glowHalf * Wid[i];
            col[i] = DrawHelpers.Tint(StormColor.Glow.Sample(0.66f + 0.08f * MathF.Min(Lum[i], 2f) - heat), alpha * 0.34f * near * Fade[i] * MathF.Min(Lum[i], 1.8f), overridden);
        }
        Mb.Ribbon(hard, nh, half, col, glowFeather);

        float bodyHalf = MathF.Max(0.9f * px, width * 0.040f);

        // 4. Forks: twigs thrown off the channel (and twigs off those) that flicker in and out on a faster clock than the bolt itself.
        DrawForks(in s, n, visible, time, agit, near, heat, alpha, px, bodyHalf, width, overridden);

        // 5. Body: pale yellow, going white where the light is over-exposed.
        for (int i = 0; i < n; i++)
        {
            float over = Math.Clamp((Lum[i] - 1.0f) / 1.2f, 0f, 1f);
            half[i] = bodyHalf * Wid[i] * (0.9f + 0.5f * over);
            col[i] = DrawHelpers.Tint(StormColor.Glow.Sample(0.84f + 0.14f * over - heat), alpha * 0.95f * near * MathF.Min(Lum[i], 1.4f), overridden);
        }
        Mb.Ribbon(hard, nh, half, col, 1.0f * px);

        // 6. Core: the white-hot line down the middle. It is only a hairline on a quiet bolt and swells as the light blows out.
        if (alpha * near > 0.12f)
        {
            float coreHalf = MathF.Max(0.55f * px, width * 0.018f);
            for (int i = 0; i < n; i++)
            {
                float over = Math.Clamp((Lum[i] - 0.9f) / 1.3f, 0f, 1f);
                half[i] = coreHalf * Wid[i] * (0.75f + 1.15f * over);
                float k = Math.Clamp((Lum[i] - 0.35f) / 0.9f, 0f, 1f);
                col[i] = DrawHelpers.Tint(StormColor.Glow.Sample(1.00f), alpha * near * (0.45f + 0.55f * k), overridden);
            }
            Mb.Ribbon(hard, nh, half, col, 0.8f * px);
        }

        Mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));

        // The leader: a bright pip at the head while the bolt is still travelling.
        if (reveal < 0.999f && s.TipFlare > 0.001f)
            DrawTip(dl, Hard[n - 1], width, Math.Clamp(s.TipFlare, 0f, 1f) * alpha, ctx);
    }

    /// <summary>
    /// Lum (about 0.45 to 3) and width multiplier per sample: a steady flicker in blocks of four samples, two surges drifting along the channel, the return stroke if SurgeGain is set, a hot head while the
    /// leader travels, a flare wherever the channel kinks hard, agitation, and an optional dash mask.
    /// </summary>
    private static void ComputeLight(in StrokePrimitive s, int n, float time, float total, float visible, float reveal, float agit, float px)
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
        float surgeAt = s.SurgeAt * total;
        float surgeW = 70f * px;

        for (int i = 0; i < n; i++)
        {
            float a = Arc[i];
            float flick = 0.5f + 0.5f * Bolt.Level(s.Seed + 301, i, 4, flickerTick, false);
            float lum = 0.52f + 0.26f * flick;
            float wid = 1f;

            for (int k = 0; k < 2; k++)
            {
                float g = (a - at[k]) / pulseW;
                if (g > -3f && g < 3f) lum += gain[k] * 0.95f * MathF.Exp(-g * g);
            }

            // The return stroke: current slamming along the channel.
            if (s.SurgeGain > 0.001f)
            {
                float g = (a - surgeAt) / surgeW;
                if (g > -3f && g < 3f)
                {
                    float sg = MathF.Exp(-g * g);
                    lum += s.SurgeGain * sg;
                    wid += 0.28f * sg * MathF.Min(s.SurgeGain, 2f);
                }
            }

            if (travelling)
            {
                float behind = visible - a;
                if (behind >= 0f) lum += 1.25f * MathF.Exp(-behind / head);
            }

            if (agit > 0.001f) lum += agit * (0.30f + 0.25f * MathF.Sin(time * 41f + a * 0.06f));

            // A hard kink flares: the channel doubles back and the camera sees more of it.
            if (i > 0 && i < n - 1)
            {
                Vector2 v1 = Hard[i] - Hard[i - 1], v2 = Hard[i + 1] - Hard[i];
                float cross = MathF.Abs(v1.X * v2.Y - v1.Y * v2.X);
                float dot = v1.X * v2.X + v1.Y * v2.Y;
                float turn = MathF.Atan2(cross, dot);                    // 0 straight on, pi doubling back
                float hot = Math.Clamp(turn / 0.9f, 0f, 1f);
                lum += 0.45f * hot;
                wid += 0.45f * hot;
            }

            if (dashPeriod > 0f)
            {
                float u = a / dashPeriod;
                u -= MathF.Floor(u);
                const float duty = 0.58f, soft = 0.10f;
                lum *= DrawHelpers.Smooth(u / soft) * (1f - DrawHelpers.Smooth((u - (duty - soft)) / soft));
            }
            Lum[i] = lum;
            Wid[i] = wid;
        }
    }

    private static void DrawForks(in StrokePrimitive s, int n, float visible, float time, float agit, float near, float heat,
                                  float alpha, float px, float bodyHalf, float width, bool overridden)
    {
        if (n < 8) return;
        int count = Math.Clamp((int)(visible / (150f * px)), 1, 9);
        float forkTick = time * (9f + 6f * agit) + DrawHelpers.Hash01(s.Seed + 5) * 30f;
        int tick = (int)MathF.Floor(forkTick);
        float lenScale = Math.Clamp(width / (32f * px), 0.6f, 1.4f);
        float chance = 0.40f + 0.55f * agit;

        Span<Vector2> pts = stackalloc Vector2[TwigPoints];
        Span<Vector2> sub = stackalloc Vector2[SubPoints];

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
            float ang = 0.40f + 0.60f * DrawHelpers.Hash01(fs + tick * 7 + 3);
            float len = (40f + 150f * DrawHelpers.Hash01(fs + tick * 11 + 4)) * px * lenScale * (0.45f + 1.0f * agit);
            Vector2 dir = Bolt.Rotate(tan, side * ang);
            float strength = MathF.Min(Lum[idx], 2f) * 0.7f;

            Bolt.Twig(pts, root, dir, len, fs + 17, tick, 0.75f);
            DrawTwig(pts, strength, 1f, alpha, near, heat, px, bodyHalf, overridden);

            // A twig off the twig.
            if (agit > 0.25f && DrawHelpers.Hash01(fs + tick * 19 + 6) < 0.20f + 0.5f * agit)
            {
                Vector2 td = pts[4] - pts[2];
                float tl = td.Length();
                if (tl > 1e-3f)
                {
                    float sgn = DrawHelpers.Hash01(fs + tick * 23 + 7) < 0.5f ? -1f : 1f;
                    Bolt.Twig(sub, pts[3], Bolt.Rotate(td / tl, sgn * (0.5f + 0.5f * DrawHelpers.Hash01(fs + tick * 29 + 8))), len * 0.45f, fs + 41, tick, 0.8f);
                    DrawTwig(sub, strength * 0.7f, 0.7f, alpha, near, heat, px, bodyHalf, overridden);
                }
            }
        }
    }

    // One twig: a soft bloom, a body that thins toward the tip, and a white hairline where it is bright.
    private static void DrawTwig(Span<Vector2> pts, float strength, float thick, float alpha, float near, float heat, float px, float bodyHalf, bool overridden)
    {
        int cnt = pts.Length;
        Span<Vector2> nrm = stackalloc Vector2[cnt];
        Span<float> half = stackalloc float[cnt];
        Span<float> reach = stackalloc float[cnt];
        Span<uint> body = stackalloc uint[cnt];
        Span<uint> bloom = stackalloc uint[cnt];
        MeshBuilder.MiterNormals(pts, nrm);

        for (int j = 0; j < cnt; j++)
        {
            float taper = 1f - j / (float)cnt;
            float k = alpha * near * taper * MathF.Min(strength, 1.4f);
            half[j] = MathF.Max(0.5f * px, bodyHalf * 0.60f * thick * taper);
            reach[j] = 8f * px * thick * (0.4f + 0.6f * taper);
            body[j] = DrawHelpers.Tint(StormColor.Glow.Sample(0.88f - heat), 0.80f * k, overridden);
            bloom[j] = DrawHelpers.Tint(StormColor.Glow.Sample(0.56f - heat), 0.24f * k, overridden);
        }
        Mb.Tent(pts, nrm, reach, bloom);
        Mb.Ribbon(pts, nrm, half, body, 0.8f * px);

        if (strength > 0.9f)
        {
            for (int j = 0; j < cnt; j++)
            {
                half[j] *= 0.42f;
                body[j] = DrawHelpers.Tint(StormColor.Glow.Sample(1f), alpha * near * (1f - j / (float)cnt) * 0.7f, overridden);
            }
            Mb.Ribbon(pts, nrm, half, body, 0.6f * px);
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
            DensityPer100px: 0.10f,
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
            CountMin: 3, CountMax: 7,
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
