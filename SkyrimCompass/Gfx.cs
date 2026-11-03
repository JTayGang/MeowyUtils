using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace SkyrimCompass;

// Shared draw + math helpers.
internal static class Gfx
{
    private static readonly (float dx, float dy)[] ShadowOffsets8 =
    [
        (-1f,-1f), (0f,-1f), (1f,-1f), (-1f,0f), (1f,0f), (-1f,1f), (0f,1f), (1f,1f),
    ];

    private static readonly (float alpha, float thickness)[] GlowLayers =
    [
        (0.05f, 14f), (0.10f, 10f), (0.18f, 6f), (0.32f, 3.5f), (0.70f, 1.8f),
    ];

    public static Vector2 V(float x, float y) => new(x, y);
    public static uint C(Vector4 v) => ImGui.ColorConvertFloat4ToU32(v);
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static float SmoothStep(float x) => x * x * (3f - 2f * x);
    public static float Normalize(float a) { a %= 360f; return a < 0f ? a + 360f : a; }

    public static float Delta(float from, float to)
    {
        float d = to - from;
        while (d > 180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    public static uint WithAlpha(uint col, float mul)
    {
        uint a = (uint)(((col >> 24) & 0xFFu) * Math.Clamp(mul, 0f, 1f));
        return (col & 0x00FFFFFFu) | (a << 24);
    }

    public static uint Brighten(uint col, uint add)
    {
        uint r = Math.Min(255u, (col & 0xFFu) + add);
        uint g = Math.Min(255u, ((col >> 8) & 0xFFu) + add);
        uint b = Math.Min(255u, ((col >> 16) & 0xFFu) + add);
        return (col & 0xFF000000u) | (b << 16) | (g << 8) | r;
    }

    public static uint MixColor(uint a, uint b, float t)
    {
        if (t <= 0f) return a;
        if (t >= 1f) return b;
        uint Ch(int shift) => (uint)Lerp((a >> shift) & 0xFFu, (b >> shift) & 0xFFu, t) << shift;
        return Ch(0) | Ch(8) | Ch(16) | Ch(24);
    }

    public static void PushUnclip(ImDrawListPtr dl) => dl.PushClipRect(Vector2.Zero, ImGui.GetIO().DisplaySize, false);
    public static void PopUnclip(ImDrawListPtr dl) => dl.PopClipRect();

    public static float LensEdgeAlpha(float d, float linearHalf, float extHalf)
    {
        float absD = MathF.Abs(d);
        if (absD <= linearHalf) return 1f;
        return 1f - SmoothStep(MathF.Min(1f, (absD - linearHalf) / (extHalf - linearHalf)));
    }

    // Degrees from view center -> pixels from bar center, squeezed toward the edges by lens.
    public static float Project(float d, float halfVis, float halfW, float lens)
    {
        float ext = halfVis * lens;
        float absD = MathF.Min(MathF.Abs(d), ext);
        float u = absD / ext;
        return (d >= 0f ? 1f : -1f) * halfW * (1f - MathF.Pow(1f - u, lens));
    }

    public static bool TryBearing(Vector3 target, Vector3 origin, float heading,
        float maxSq, float extHalf, out float dist, out float delta)
    {
        float dx = target.X - origin.X, dy = target.Y - origin.Y, dz = target.Z - origin.Z;
        float dsq = dx * dx + dy * dy + dz * dz;
        dist = 0f; delta = 0f;
        if (dsq > maxSq || dsq < 0.25f) return false;
        float bearing = Normalize(MathF.Atan2(dx, -dz) * (180f / MathF.PI));
        delta = Delta(heading, bearing);
        if (MathF.Abs(delta) > extHalf) return false;
        dist = MathF.Sqrt(dsq);
        return true;
    }

    public static void DrawDiamond(ImDrawListPtr dl, float cx, float cy, float hw, float hh, uint col) =>
        dl.AddQuadFilled(V(cx, cy - hh), V(cx + hw, cy), V(cx, cy + hh), V(cx - hw, cy), col);

    // Uses whatever font is currently pushed.
    public static void ShadowText(ImDrawListPtr dl, float size, Vector2 pos, uint col, uint shadow, string text)
    {
        var font = ImGui.GetFont();
        foreach (var (dx, dy) in ShadowOffsets8)
            dl.AddText(font, size, V(pos.X + dx, pos.Y + dy), shadow, text);
        dl.AddText(font, size, pos, col, text);
    }

    // Outlined diamond with an inner outline and a center gem; used by the compass caps and name ornaments.
    public static void DrawGem(ImDrawListPtr dl, float cx, float cy, float hw, float hh, uint edge, bool highlight)
    {
        dl.AddQuad(V(cx, cy - hh), V(cx + hw, cy), V(cx, cy + hh), V(cx - hw, cy), edge, 1.5f);
        const float s = 0.58f;
        dl.AddQuad(V(cx, cy - hh * s), V(cx + hw * s, cy), V(cx, cy + hh * s), V(cx - hw * s, cy), WithAlpha(edge, 0.6f), 1f);
        float g = MathF.Max(1.6f, hw * 0.2f);
        DrawDiamond(dl, cx, cy, g, g * 1.35f, Brighten(edge, 60u));
        if (highlight)
            dl.AddTriangleFilled(V(cx, cy - g * 1.35f), V(cx - g * 0.1f, cy - g * 0.1f), V(cx - g, cy), 0x78FFFFFFu);
    }

    public static float PulseIntensity(float t) =>
        (0.75f + 0.25f * MathF.Sin(t * 0.79f)) * (0.92f + 0.08f * MathF.Sin(t * 3.23f + 1.17f));

    public static void DrawFlowingLine(ImDrawListPtr dl, Vector2 a, Vector2 b, uint col,
        float time, float timeMul = 1f, float timeOff = 0f,
        float intensity = 1f, bool fromLeft = true, float wipe = 0f, float fill = 0f, bool wipeRev = false)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (len < 1f) return;
        Vector2 dir = d / len, perp = new(-dir.Y, dir.X);
        const float amp = 5f, waveLen = 26f, flow = 2f, wipeHalf = 0.2f, harm = 0.33f;
        float flowDir = fromLeft ? -1f : 1f;
        float phase = (time * timeMul + timeOff) * flow * flowDir;
        float phase2 = phase * 1.4f + 1.3f;
        float tipStart = Lerp(0.6f, 1.0f, Math.Clamp(fill, 0f, 1f));
        float wipeCentre = Lerp(1f + wipeHalf, -wipeHalf, wipe);
        int samples = Math.Clamp((int)(len / (waveLen * 0.5f) * 4f) + 2, 3, 24);
        Span<Vector2> pts = stackalloc Vector2[24];
        Span<float> fades = stackalloc float[24];
        float freq = 2f * MathF.PI / waveLen, freq2 = freq * 2f;
        for (int i = 0; i < samples; i++)
        {
            float along = len * i / (samples - 1);
            float u = fromLeft ? along / len : 1f - along / len;
            float env = u * u * (3f - 2f * u);
            float wave = MathF.Sin(along * freq + phase) * (1f - harm * u) + MathF.Sin(along * freq2 + phase2) * (harm * u);
            pts[i] = a + dir * along + perp * (amp * env * wave);
            float tip = 1f - SmoothStep(Math.Clamp((u - tipStart) / (1f - tipStart + 1e-4f), 0f, 1f));
            float wipeU = wipeRev ? 1f - u : u;
            float wf = 1f - SmoothStep(Math.Clamp((wipeU - (wipeCentre - wipeHalf)) / (2f * wipeHalf), 0f, 1f));
            fades[i] = tip * wf;
        }
        foreach (var (alpha, thick) in GlowLayers)
            for (int i = 0; i < samples - 1; i++)
            {
                float seg = (fades[i] + fades[i + 1]) * 0.5f;
                if (seg <= 0.002f) continue;
                dl.AddLine(pts[i], pts[i + 1], WithAlpha(col, alpha * intensity * seg), thick);
            }
    }

    // Holds a value briefly after it drops so it can wipe out instead of vanishing.
    public static float UpdateFadeOut(ref float tracked, ref float frozen, ref float start,
        float real, bool trigger, bool extResync, bool resyncIfExceeds, float now, float dur, out float wipe)
    {
        if (start < 0f)
        {
            if (trigger) { frozen = tracked; start = now; }
            else tracked = real;
        }
        if (start >= 0f)
        {
            float elapsed = now - start;
            if (extResync || (resyncIfExceeds && real > frozen) || elapsed >= dur)
            { start = -1f; tracked = real; wipe = 0f; return tracked; }
            wipe = elapsed / dur;
            return frozen;
        }
        wipe = 0f;
        return tracked;
    }
}
