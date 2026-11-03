using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using static SkyrimCompass.Gfx;

namespace SkyrimCompass;

// Braided ribbon beside the target name; it also fills with the warning color as a cast progresses.
internal static class Ribbon
{
    private const int MaxSamples = 22, Motes = 3;
    private const float Amp = 4.2f, WaveLen = 54f;

    // Dark underlay, soft glow, body, bright core.
    private static readonly (float Thick, float Alpha, int Mode)[] Layers =
        [(5f, 0.30f, 0), (8f, 0.10f, 1), (3.2f, 0.30f, 1), (1.4f, 0.90f, 2)];

    // from = anchor at the name ornament, to = far end; phase advances faster while casting.
    public static void Draw(ImDrawListPtr dl, Vector2 from, Vector2 to, uint baseCol, uint castCol, float alpha,
        float phase, float grow, float energy, float cast, float wipe, float seed)
    {
        float len = MathF.Abs(to.X - from.X);
        if (len < 8f || grow <= 0f) return;
        float dirX = to.X >= from.X ? 1f : -1f;
        int n = Math.Clamp((int)(len / 8f), 8, MaxSamples);
        float amp = Amp * (0.7f + 0.7f * energy);
        float glint = Frac(phase * 0.12f + seed) * 1.3f - 0.15f;
        float wc = Lerp(1.2f, -0.2f, wipe);

        Span<Vector2> p0 = stackalloc Vector2[MaxSamples];
        Span<Vector2> p1 = stackalloc Vector2[MaxSamples];
        Span<float> lum = stackalloc float[MaxSamples];
        Span<uint> col = stackalloc uint[MaxSamples];
        for (int i = 0; i < n; i++)
        {
            float u = i / (float)(n - 1), along = len * u, x = from.X + dirX * along;
            p0[i] = V(x, from.Y + Wave(0, along, u, phase, amp));
            p1[i] = V(x, from.Y + Wave(1, along, u, phase, amp));
            float mask = CastMask(u, cast, wc);
            float g = (u - glint) / 0.07f;
            col[i] = MixColor(baseCol, castCol, mask);
            lum[i] = MathF.Max(TipFade(u), mask) * GrowMask(u, grow)
                * (0.75f + 0.25f * energy) * (1f + 0.8f * MathF.Exp(-g * g) + 0.5f * mask);
        }

        uint near = WithAlpha(baseCol, 0.22f), far = baseCol & 0x00FFFFFFu;
        float spineEnd = from.X + dirX * len * grow;
        if (dirX > 0f) dl.AddRectFilledMultiColor(V(from.X, from.Y - 0.5f), V(spineEnd, from.Y + 0.5f), near, far, far, near);
        else dl.AddRectFilledMultiColor(V(spineEnd, from.Y - 0.5f), V(from.X, from.Y + 0.5f), far, near, near, far);

        for (int strand = 0; strand < 2; strand++)
        {
            var pts = strand == 0 ? p0 : p1;
            foreach (var (thick, layerAlpha, mode) in Layers)
                for (int i = 0; i < n - 1; i++)
                {
                    float l = (lum[i] + lum[i + 1]) * 0.5f;
                    if (l <= 0.01f) continue;
                    float a = layerAlpha * MathF.Min(l, 1.6f);
                    uint c = mode == 0 ? WithAlpha(0xFF000000u, a * alpha)
                        : WithAlpha(mode == 1 ? col[i] : Brighten(col[i], 70u), a);
                    dl.AddLine(pts[i], pts[i + 1], c, thick);
                }
        }

        for (int m = 0; m < Motes; m++)
        {
            float u = Frac(phase * 0.3f + seed + m / (float)Motes), along = len * u;
            float mask = CastMask(u, cast, wc);
            float vis = MathF.Max(TipFade(u), mask) * GrowMask(u, grow);
            if (vis <= 0.02f) continue;
            var pos = V(from.X + dirX * along, from.Y + Wave(m & 1, along, u, phase, amp));
            uint c = Brighten(MixColor(baseCol, castCol, mask), 90u);
            float size = (1f + 1.4f * (1f - u)) * (0.85f + 0.4f * energy);
            float a = MathF.Sin(MathF.PI * u) * vis * (0.7f + 0.3f * energy);
            dl.AddCircleFilled(pos, size * 2.6f, WithAlpha(c, 0.14f * a));
            DrawDiamond(dl, pos.X, pos.Y, size, size * 1.45f, WithAlpha(c, 0.95f * a));
        }

        if (cast > 0f)
        {
            float vis = GrowMask(cast, grow) * (1f - SmoothStep(Math.Clamp(((1f - cast) - (wc - 0.2f)) / 0.4f, 0f, 1f)));
            var head = V(from.X + dirX * len * cast, from.Y);
            float pulse = 0.75f + 0.25f * MathF.Sin(phase * 2.3f);
            dl.AddCircleFilled(head, 7f, WithAlpha(castCol, 0.10f * vis * pulse));
            dl.AddCircleFilled(head, 4f, WithAlpha(castCol, 0.22f * vis * pulse));
            DrawDiamond(dl, head.X, head.Y, 2.4f, 3.6f, WithAlpha(Brighten(castCol, 110u), 0.95f * vis));
        }
    }

    private static float Wave(int strand, float along, float u, float phase, float amp)
    {
        float envelope = SmoothStep(MathF.Min(1f, u / 0.3f)) * (1f - 0.55f * SmoothStep(Math.Clamp((u - 0.55f) / 0.45f, 0f, 1f)));
        const float k = 2f * MathF.PI / WaveLen;
        float shift = strand == 0 ? 0f : MathF.PI;
        float w = MathF.Sin(along * k - phase + shift) + 0.2f * MathF.Sin(along * k * 1.6f - phase * 1.37f + shift * 0.5f + 1.3f);
        return amp * envelope * w;
    }

    private static float Frac(float x) => x - MathF.Floor(x);
    private static float TipFade(float u) => 1f - SmoothStep(Math.Clamp((u - 0.6f) / 0.4f, 0f, 1f));
    private static float GrowMask(float u, float grow) => 1f - SmoothStep(Math.Clamp((u - (grow * 1.15f - 0.15f)) / 0.15f, 0f, 1f));

    // 1 up to the cast progress, drained from the anchor outward as the wipe advances.
    private static float CastMask(float u, float cast, float wc)
    {
        if (cast <= 0f) return 0f;
        return (1f - SmoothStep(Math.Clamp((u - cast) / 0.08f, 0f, 1f)))
             * (1f - SmoothStep(Math.Clamp(((1f - u) - (wc - 0.2f)) / 0.4f, 0f, 1f)));
    }
}
