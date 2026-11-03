using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using static SkyrimCompass.Gfx;

namespace SkyrimCompass;

// A soft, flowing ribbon beside the target name. It keeps the old single-line motion,
// with a little extra body, a restrained highlight, and cast-progress coloration.
internal static class Ribbon
{
    private const int MaxSamples = 28;
    private const int Glints = 2;
    private const float Amp = 3.6f, WaveLen = 36f;

    // One coherent ribbon: dark separation, broad glow, colored body, and a fine bright core.
    private static readonly (float Thick, float Alpha, int Mode)[] Layers =
    [
        (7.5f, 0.13f, 0),
        (5.0f, 0.12f, 1),
        (2.8f, 0.34f, 1),
        (1.25f, 0.78f, 2),
    ];

    // from = anchor at the name ornament, to = far end; phase advances faster while casting.
    public static void Draw(ImDrawListPtr dl, Vector2 from, Vector2 to, uint baseCol, uint castCol, float alpha,
        float phase, float grow, float energy, float cast, float wipe, float seed, float shadowPhase)
    {
        float len = MathF.Abs(to.X - from.X);
        if (len < 8f || grow <= 0f) return;

        float dirX = to.X >= from.X ? 1f : -1f;
        int n = Math.Clamp((int)(len / 6f), 10, MaxSamples);
        float amp = Amp * (0.85f + 0.35f * energy);
        // shadowPhase is advanced independently by the caller using the old side-specific timing.
        float glint = Frac(phase * 0.10f + seed) * 1.15f - 0.08f;
        float wc = Lerp(1.2f, -0.2f, wipe);

        Span<Vector2> pts = stackalloc Vector2[MaxSamples];
        Span<Vector2> shadowPts = stackalloc Vector2[MaxSamples];
        Span<float> lum = stackalloc float[MaxSamples];
        Span<uint> colors = stackalloc uint[MaxSamples];

        for (int i = 0; i < n; i++)
        {
            float u = i / (float)(n - 1);
            float along = len * u;
            float x = from.X + dirX * along;
            float mask = CastMask(u, cast, wc);
            float g = (u - glint) / 0.085f;

            pts[i] = V(x, from.Y + Wave(along, u, phase, amp));
            // A thin, dark counter-wave sits behind the ribbon. The inverted wave and
            // offset timing create a subtle sense of motion without reading as a braid.
            shadowPts[i] = V(x, from.Y + ShadowWave(along, u, shadowPhase, amp * 1f) + 0.65f);
            colors[i] = MixColor(baseCol, castCol, mask);

            // Let the ribbon emerge cleanly from the gem, then taper toward its far end.
            float visible = MathF.Max(TipFade(u), mask) * GrowMask(u, grow);
            float energyBoost = 0.88f + 0.12f * energy;
            float glintBoost = 1f + 0.48f * MathF.Exp(-g * g);
            lum[i] = visible * energyBoost * glintBoost * (1f + 0.25f * mask);
        }

        // Render the independent shadow first so the main ribbon stays visually dominant.
        for (int i = 0; i < n - 1; i++)
        {
            float light = (lum[i] + lum[i + 1]) * 0.5f;
            if (light <= 0.02f) continue;
            float a = 0.17f * alpha * MathF.Min(light, 1.25f);
            dl.AddLine(shadowPts[i], shadowPts[i + 1], WithAlpha(0xFF000000u, Math.Min(1f, a * 1.4f)), 2.8f);
        }

        // A very faint connecting filament gives the ribbon continuity without reading as a rigid rail.
        float spineEnd = from.X + dirX * len * grow;
        uint spine = WithAlpha(baseCol, 0.075f);
        dl.AddLine(V(from.X, from.Y), V(spineEnd, from.Y), spine, 0.8f);

        foreach (var (thick, layerAlpha, mode) in Layers)
        {
            for (int i = 0; i < n - 1; i++)
            {
                float light = (lum[i] + lum[i + 1]) * 0.5f;
                if (light <= 0.01f) continue;

                float a = layerAlpha * MathF.Min(light, 1.45f);
                uint c = mode == 0
                    ? WithAlpha(0xFF000000u, a * alpha)
                    : WithAlpha(mode == 1 ? colors[i] : Brighten(colors[i], 55u), a);
                dl.AddLine(pts[i], pts[i + 1], c, thick);
            }
        }

        // A fine offset edge catches the light and makes the main stroke feel like a ribbon.
        for (int i = 0; i < n - 1; i++)
        {
            float light = (lum[i] + lum[i + 1]) * 0.5f;
            if (light <= 0.02f) continue;
            var a = V(pts[i].X, pts[i].Y - 1.25f);
            var b = V(pts[i + 1].X, pts[i + 1].Y - 1.25f);
            dl.AddLine(a, b, WithAlpha(Brighten(colors[i], 75u), 0.24f * light), 0.9f);
        }

        // Randomly assign the motes to opposite paths so both ribbons get a little motion.
        // Their start positions and speeds are independently varied for less synchronized travel.
        bool firstGlintUsesShadow = Random01(seed, 0, 3u) >= 0.5f;
        for (int m = 0; m < Glints; m++)
        {
            float start = Random01(seed, m, 1u);
            float speed = Lerp(0.14f, 0.30f, Random01(seed, m, 2u));
            bool useShadow = (m & 1) == 0 ? firstGlintUsesShadow : !firstGlintUsesShadow;
            float u = Frac(start + phase * speed);

            float visible = MathF.Max(TipFade(u), CastMask(u, cast, wc)) * GrowMask(u, grow);
            if (visible <= 0.04f) continue;

            float along = len * u;
            float waveY = useShadow
                ? ShadowWave(along, u, shadowPhase, amp) + 0.65f
                : Wave(along, u, phase, amp);
            var pos = V(from.X + dirX * along, from.Y + waveY);
            uint c = Brighten(MixColor(baseCol, castCol, CastMask(u, cast, wc)), 65u);
            float size = 1.1f + 0.35f * energy;
            float a = MathF.Sin(MathF.PI * u) * visible;
            dl.AddCircleFilled(pos, size * 2.3f, WithAlpha(c, 0.10f * a));
            dl.AddCircleFilled(pos, size, WithAlpha(c, 0.62f * a));
        }

        // The cast front is a small glow and point, not a second decorative ornament.
        if (cast > 0f)
        {
            float vis = GrowMask(cast, grow)
                * (1f - SmoothStep(Math.Clamp(((1f - cast) - (wc - 0.2f)) / 0.4f, 0f, 1f)));
            var head = V(from.X + dirX * len * cast, from.Y + Wave(len * cast, cast, phase, amp));
            float pulse = 0.82f + 0.18f * MathF.Sin(phase * 2.1f);
            dl.AddCircleFilled(head, 5.0f, WithAlpha(castCol, 0.10f * vis * pulse));
            dl.AddCircleFilled(head, 2.2f, WithAlpha(Brighten(castCol, 75u), 0.76f * vis * pulse));
        }
    }

    private static float Wave(float along, float u, float phase, float amp)
    {
        float envelope = SmoothStep(MathF.Min(1f, u / 0.20f))
            * (1f - 0.30f * SmoothStep(Math.Clamp((u - 0.72f) / 0.28f, 0f, 1f)));
        const float k = 2f * MathF.PI / WaveLen;
        float wave = MathF.Sin(along * k - phase)
            + 0.18f * MathF.Sin(along * k * 1.8f - phase * 1.24f + 1.1f);
        return amp * envelope * wave;
    }

    private static float ShadowWave(float along, float u, float phase, float amp) =>
        -Wave(along, u, phase, amp);

    // Stable integer hash converted to [0, 1). Unlike System.Random, this has no mutable
    // state, so the same ribbon seed always produces the same mote behavior on every frame.
    private static float Random01(float seed, int index, uint channel)
    {
        unchecked
        {
            uint x = BitConverter.SingleToUInt32Bits(seed);
            x ^= (uint)(index + 1) * 0x9E3779B9u;
            x ^= channel * 0x85EBCA6Bu;
            x ^= x >> 16;
            x *= 0x7FEB352Du;
            x ^= x >> 15;
            x *= 0x846CA68Bu;
            x ^= x >> 16;
            return (x >> 8) * (1f / 16777216f);
        }
    }

    private static float Frac(float x) => x - MathF.Floor(x);
    private static float TipFade(float u) => 1f - SmoothStep(Math.Clamp((u - 0.60f) / 0.40f, 0f, 1f));
    private static float GrowMask(float u, float grow) =>
        1f - SmoothStep(Math.Clamp((u - (grow * 1.15f - 0.15f)) / 0.15f, 0f, 1f));

    // 1 up to cast progress, then drains from the anchor outward as the wipe advances.
    private static float CastMask(float u, float cast, float wc)
    {
        if (cast <= 0f) return 0f;
        return (1f - SmoothStep(Math.Clamp((u - cast) / 0.10f, 0f, 1f)))
             * (1f - SmoothStep(Math.Clamp(((1f - u) - (wc - 0.2f)) / 0.4f, 0f, 1f)));
    }
}
