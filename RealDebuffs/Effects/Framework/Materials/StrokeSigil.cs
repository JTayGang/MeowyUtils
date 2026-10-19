using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>Shared by the strokes drawn out of light: resamples a path evenly by arc length and works out how bright the light is at each sample (a shimmer, energy pulses running along it, dashes). Scratch only, UI thread only.</summary>
internal sealed class LightStrand
{
    public const int MaxSamples = 1100;

    public readonly Vector2[] Pts = new Vector2[MaxSamples];
    public readonly Vector2[] Nrm = new Vector2[MaxSamples];
    public readonly float[]   Arc = new float[MaxSamples];
    public readonly float[]   Lum = new float[MaxSamples];
    public readonly float[]   Half = new float[MaxSamples];
    public readonly uint[]    Col = new uint[MaxSamples];
    public int Count;

    /// <summary>Samples along the first visibleLen px of the path: every original corner is kept, and long segments are split to stepPx or less (light varies faster than the geometry). A fully shown closed loop ends exactly where it began.</summary>
    public void Resample(StrandPath path, float visibleLen, float stepPx)
    {
        float total = path.Length;
        visibleLen = MathF.Min(visibleLen, total);
        stepPx = MathF.Max(1f, stepPx);

        // Coarsen rather than overflow: a very long path just gets a larger step.
        if (visibleLen / stepPx + path.Count > MaxSamples - 2) stepPx = visibleLen / (MaxSamples - 2 - path.Count);

        int n = 0;
        Pts[n] = path.Points[0]; Arc[n] = 0f; n++;
        for (int i = 1; i < path.Count && n < MaxSamples - 1; i++)
        {
            float a0 = path.Arc[i - 1], a1 = path.Arc[i];
            float len = a1 - a0;
            if (len < 1e-4f) continue;

            bool last = a1 >= visibleLen;
            if (last) { a1 = visibleLen; len = a1 - a0; if (len < 1e-3f) break; }

            int pieces = Math.Max(1, (int)MathF.Ceiling(len / stepPx));
            Vector2 p0 = path.Points[i - 1], p1 = path.Points[i];
            float full = path.Arc[i] - a0;
            for (int k = 1; k <= pieces && n < MaxSamples; k++)
            {
                float t = k / (float)pieces;
                Pts[n] = Vector2.Lerp(p0, p1, t * len / full);
                Arc[n] = a0 + len * t;
                n++;
            }
            if (last) break;
        }
        Count = n;
        MeshBuilder.MiterNormals(Pts.AsSpan(0, Count), Nrm.AsSpan(0, Count));
    }

    /// <summary>Fills Lum (about 0.5 to 1.7): a slow shimmer, three pulses running along the line, a flicker while the strand is agitated, and an optional dash mask.</summary>
    public void Light(in StrokePrimitive s, float time, float total, float width, float px, float dashPeriod)
    {
        bool closed = s.Closed;
        float agit = Math.Clamp(s.Agitation, 0f, 1f);
        float pulseW = MathF.Max(34f * px, width * 2.4f);

        Span<float> at = stackalloc float[3];
        Span<float> gain = stackalloc float[3];
        for (int k = 0; k < 3; k++)
        {
            float h = DrawHelpers.Hash01(s.Seed * 13 + k * 101 + 5);
            float speed = (110f + 120f * DrawHelpers.Hash01(s.Seed + k * 37 + 9)) * px * (h < 0.5f ? 1f : -1f);
            float p = (DrawHelpers.Hash01(s.Seed + k * 17 + 3) + s.Phase * 0.173f + time * speed / MathF.Max(total, 1f)) % 1f;
            at[k] = (p < 0f ? p + 1f : p) * total;
            gain[k] = 0.62f + 0.38f * DrawHelpers.Hash01(s.Seed + k * 29 + 1);
        }

        float shimmerPhase = s.Phase * 2.1f + time * 1.15f;
        for (int i = 0; i < Count; i++)
        {
            float a = Arc[i];
            float lum = 0.60f + 0.10f * MathF.Sin(shimmerPhase + a * 0.0042f) + 0.05f * MathF.Sin(time * 3.1f + a * 0.011f);

            for (int k = 0; k < 3; k++)
            {
                float d = MathF.Abs(a - at[k]);
                if (closed) d = MathF.Min(d, total - d);
                float g = d / pulseW;
                if (g < 3f) lum += gain[k] * 0.95f * MathF.Exp(-g * g);
            }

            if (agit > 0.001f) lum += agit * (0.35f + 0.25f * MathF.Sin(time * 37f + a * 0.05f));

            if (dashPeriod > 0f)
            {
                float u = a / dashPeriod;
                u -= MathF.Floor(u);
                const float duty = 0.58f, soft = 0.10f;
                float mask = DrawHelpers.Smooth(u / soft) * (1f - DrawHelpers.Smooth((u - (duty - soft)) / soft));
                lum *= mask;
            }
            Lum[i] = lum;
        }
    }

    /// <summary>The dash period that fits whole times into a closed loop (so the seam never shows a stub), or the plain period.</summary>
    public static float FitDash(float period, float total, bool closed) =>
        closed && period > 1f ? total / MathF.Max(1f, MathF.Round(total / period)) : period;
}

/// <summary>Light drawn as a line: a wide soft bloom, an orchid glow, a hot-pink body and a pale core, brightening wherever a pulse is running through it. Honours Reveal (it draws itself on, with a spark at the tip), Dash, Depth and Agitation. WidthHint is the whole glowing width in px.</summary>
public sealed class StrokeSigil : IStrokeMaterial
{
    private readonly string _name;
    private readonly Palette _palette;
    private readonly StrokeEmission[] _shed;
    private readonly ImpactEmission[] _hit;

    /// <summary>The curse-violet sigil (stroke.sigil).</summary>
    public StrokeSigil() : this("stroke.sigil", CurseColor.Glow, "particle.mote", "sigil", "sigils", "arcane", "neon") { }

    /// <summary>The same line of light in another palette: register one per theme (see stroke.brand in MaterialRegistry). moteMaterial is what it sheds and throws off when struck, so its sparks match its colour.</summary>
    internal StrokeSigil(string name, Palette palette, string moteMaterial, params string[] words)
    {
        _name = name;
        _palette = palette;
        NaturalLanguageWords = words;

        // A faint trickle of light drifting off the line, mostly up and a little away from it; and, when a seal slams down, a burst thrown off along the surface.
        _shed = new StrokeEmission[]
        {
            new(Role: PrimitiveRole.Mote,
                DensityPer100px: 0.30f,
                SpeedMin: 6f, SpeedMax: 26f,
                LifespanMin: 1.3f, LifespanMax: 2.6f,
                SizeMin: 1.6f, SizeMax: 3.6f,
                SpreadRadians: 1.5f,
                BiasVelocity: new Vector2(0f, -16f),
                RenderMaterial: moteMaterial),
        };
        _hit = new ImpactEmission[]
        {
            new(Role: PrimitiveRole.Mote,
                CountMin: 9, CountMax: 18,
                SpeedMin: 140f, SpeedMax: 560f,
                LifespanMin: 0.7f, LifespanMax: 1.6f,
                SizeMin: 3f, SizeMax: 7f,
                ConeRadians: 1.0f,
                Gravity: new Vector2(0f, -30f),
                RenderMaterial: moteMaterial),
        };
    }

    public string Name => _name;
    public string[] NaturalLanguageWords { get; }

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

        float dash = s.Dash > 0f ? LightStrand.FitDash(s.Dash * px, total, s.Closed) : 0f;
        float step = Math.Clamp(width * 1.4f, 12f * px, 20f * px);
        if (dash > 0f) step = MathF.Min(step, dash * 0.2f);

        var st = Strand;
        st.Resample(path, visibleLen, step);
        st.Light(in s, ctx.Time, total, width, px, dash);
        int n = st.Count;

        // Far light is dimmer, softer and cooler (its hot end of the ramp is the first thing the haze takes).
        float near = 1f - 0.55f * depth;
        float soften = 1f + 0.9f * depth;
        float heat = 0.18f * depth;

        var pts = st.Pts.AsSpan(0, n);
        var nrm = st.Nrm.AsSpan(0, n);
        var half = st.Half.AsSpan(0, n);
        var col = st.Col.AsSpan(0, n);
        var lum = st.Lum.AsSpan(0, n);
        bool overridden = DrawHelpers.ColorOverrideActive;

        Mb.Clear();

        // 1. Bloom: a wide, faint tent, strongest where the light is. A thin line has no glow ribbon (below), so its bloom is stronger.
        bool fat = width >= 13f * px;
        float reach = MathF.Max(8f * px, width * 0.9f * soften);
        float bloom = fat ? 0.17f : 0.27f;
        for (int i = 0; i < n; i++)
        {
            half[i] = reach;
            col[i] = Tint(_palette.Sample(0.20f), alpha * bloom * near * Math.Min(lum[i], 1.6f), overridden);
        }
        Mb.Tent(pts, nrm, half, col);

        // 2. Glow. Only a fat line has room for one: on a thin line the bloom already does its job, and this is the dearest pass per px of line.
        if (fat)
        {
            float glowHalf = MathF.Max(0.8f * px, width * 0.10f);
            float glowFeather = MathF.Max(3f * px, width * 0.34f * soften);
            for (int i = 0; i < n; i++)
            {
                half[i] = glowHalf;
                col[i] = Tint(_palette.Sample(0.36f + 0.12f * lum[i] - heat), alpha * 0.46f * near * Math.Min(lum[i], 1.5f), overridden);
            }
            Mb.Ribbon(pts, nrm, half, col, glowFeather);
        }

        // 3. Body.
        float bodyHalf = MathF.Max(0.8f * px, width * 0.060f);
        for (int i = 0; i < n; i++)
        {
            half[i] = bodyHalf;
            col[i] = Tint(_palette.Sample(0.50f + 0.34f * (lum[i] - 0.55f) - heat), alpha * 0.92f * near * Math.Min(lum[i], 1.25f), overridden);
        }
        Mb.Ribbon(pts, nrm, half, col, 1.0f * px);

        // 4. Core: the hot line down the middle, only where the light is up (and not on a line too faint to show one).
        if (alpha * near > 0.35f)
        {
            float coreHalf = MathF.Max(0.5f * px, width * 0.024f);
            for (int i = 0; i < n; i++)
            {
                half[i] = coreHalf;
                float k = Math.Clamp((lum[i] - 0.35f) / 0.9f, 0f, 1f);
                col[i] = Tint(_palette.Sample(0.88f), alpha * near * (0.30f + 0.70f * k), overridden);
            }
            Mb.Ribbon(pts, nrm, half, col, 0.8f * px);
        }

        Mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));

        // The pen: a bright pip where the line is still drawing itself.
        if (reveal < 0.999f && s.TipFlare > 0.001f)
            DrawTip(dl, pts[n - 1], width, Math.Clamp(s.TipFlare, 0f, 1f) * alpha, ctx);
    }

    private static uint Tint(uint rgb, float a, bool overridden) => DrawHelpers.Tint(rgb, a, overridden);

    private void DrawTip(ImDrawListPtr dl, Vector2 tip, float width, float k, in MaterialContext ctx)
    {
        if (k <= 0.004f) return;
        Span<float> rr = stackalloc float[2] { width * 0.35f, width * 1.1f };
        Span<uint> cc = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(_palette.Sample(0.62f), 0.40f * k),
            DrawHelpers.WithAlpha(_palette.Sample(0.35f), 0f),
        };
        MeshDraw.Radial(dl, tip, MeshDraw.WhiteUv(ctx.Time), DrawHelpers.WithAlpha(_palette.Sample(1f), 0.95f * k),
                        10, rr, cc, 0f, 1f, 0, 0f);
    }

    public ReadOnlySpan<StrokeEmission> Emissions => _shed;

    public ReadOnlySpan<ImpactEmission> ImpactEmissions => _hit;
}

/// <summary>A bare band of soft light along a path, with no line in it: lay it under any stroke (a chain, a rope) to wrap that stroke in glow. WidthHint is the whole band's width in px.</summary>
public sealed class StrokeGlow : IStrokeMaterial
{
    private readonly string _name;
    private readonly Palette _palette;

    /// <summary>The curse-violet glow (stroke.glow).</summary>
    public StrokeGlow() : this("stroke.glow", CurseColor.Glow, "glow", "halo", "aura") { }

    /// <summary>The same band of light in another palette: register one of these per theme (see stroke.corona in MaterialRegistry) so any effect can wrap a physical stroke in its own colour of glow.</summary>
    internal StrokeGlow(string name, Palette palette, params string[] words)
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
        if (path.Count < 2 || alpha <= 0.004f || s.Reveal <= 0.001f) return;

        float total = path.Length;
        if (total < 4f) return;

        float px = ctx.ScreenScale;
        float width = MathF.Max(6f * px, s.WidthHint);
        float depth = Math.Clamp(s.Depth, 0f, 1f);

        // The glow is soft, so it can be sampled coarsely.
        var st = Strand;
        st.Resample(path, total * Math.Clamp(s.Reveal, 0f, 1f), Math.Clamp(width * 0.6f, 12f * px, 24f * px));
        st.Light(in s, ctx.Time, total, width, px, 0f);
        int n = st.Count;

        float near = 1f - 0.5f * depth;
        var half = st.Half.AsSpan(0, n);
        var col = st.Col.AsSpan(0, n);
        bool overridden = DrawHelpers.ColorOverrideActive;

        for (int i = 0; i < n; i++)
        {
            half[i] = width * 0.5f;
            float l = Math.Min(st.Lum[i], 1.7f);
            col[i] = DrawHelpers.Tint(_palette.Sample(0.26f + 0.22f * l), alpha * 0.30f * near * l, overridden);
        }

        Mb.Clear();
        Mb.Tent(st.Pts.AsSpan(0, n), st.Nrm.AsSpan(0, n), half, col);
        Mb.Flush(dl, MeshDraw.WhiteUv(ctx.Time));
    }
}
