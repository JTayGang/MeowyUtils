using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// The "flesh and slime" material originally authored for Bind: a tapered, layered organic line
/// (dark void flesh, a bright violet magic core, a rim-lit highlight, and a wet sheen that travels
/// along the length) with drips that hang-and-fall or slide slowly toward the base, and a small
/// glowing bulb at the tip while <paramref name="tipFlare"/> is above zero. Purely a function of
/// the StrandPath and StrandVisual it's handed - it has no idea whether it's drawing one of Bind's
/// own searching tendrils or some other effect's strand that's been reskinned to look like one.
///
/// The taper and the traveling sheen are parameterized against however much of the strand is
/// CURRENTLY revealed, not its eventual full length - so a growing tip always reads as a proper
/// tapered tip (thin, and where the sheen currently sits), rather than a uniform-thickness noodle
/// that suddenly finishes tapering once fully grown. See <see cref="DrawTaperedPath"/>.
///
/// Stateless singleton: the palette below is baked once into static readonly fields (the same
/// pattern BindEffect itself used before this was extracted), so the shared <see cref="Instance"/>
/// is safe across every effect and every strand that picks this skin, including more than one call
/// in a single frame.
/// </summary>
public sealed class TentacleSkin : IStrandSkin
{
    public static readonly TentacleSkin Instance = new();
    private TentacleSkin() { }

    // ---- palette: near-black void flesh, violet magic core, pale lavender hot glint ----
    private static readonly uint Halo      = DrawHelpers.ToU32(0.010f, 0.002f, 0.022f, 1f);
    private static readonly uint Void      = DrawHelpers.ToU32(0.014f, 0.006f, 0.028f, 1f);
    private static readonly uint Body      = DrawHelpers.ToU32(0.052f, 0.018f, 0.100f, 1f);
    private static readonly uint BodyLit   = DrawHelpers.ToU32(0.145f, 0.055f, 0.235f, 1f);
    private static readonly uint Core      = DrawHelpers.ToU32(0.580f, 0.180f, 0.780f, 1f);
    private static readonly uint CoreHot   = DrawHelpers.ToU32(0.960f, 0.800f, 1.000f, 1f);
    private static readonly uint SlimeBody = DrawHelpers.ToU32(0.190f, 0.055f, 0.340f, 1f);
    private static readonly uint SlimeCore = DrawHelpers.ToU32(0.720f, 0.320f, 0.960f, 1f);

    private static readonly Vector2 LightDir = Vector2.Normalize(new Vector2(-0.7f, -0.7f));

    public void DrawStrand(ImDrawListPtr dl, StrandPath path, in StrandVisual visual,
                           float reveal, float tipFlare, float alpha, float px, float time,
                           out Vector2 tipPos, out bool tipVisible)
    {
        tipPos = default;
        tipVisible = false;
        if (path.Count < 2 || alpha <= 0.002f || reveal <= 0.001f) return;

        float baseW = visual.Thickness;

        DrawTaperedPath(dl, path, baseW, alpha, px, reveal, time, visual.Phase, out tipPos, out tipVisible);
        DrawSlimeDrips(dl, path, baseW, alpha, reveal, time, visual.Seed);

        if (tipVisible && tipFlare > 0.001f)
        {
            float f = Math.Clamp(tipFlare, 0f, 1f);
            float r = baseW * 1.4f;
            dl.AddCircleFilled(tipPos, r * 3.0f,  DrawHelpers.WithAlpha(Core,    alpha * 0.30f * f));
            dl.AddCircleFilled(tipPos, r * 1.6f,  DrawHelpers.WithAlpha(Core,    alpha * 0.70f * f));
            dl.AddCircleFilled(tipPos, r * 0.70f, DrawHelpers.WithAlpha(CoreHot, alpha * 0.95f * f));
        }
    }

    // =====================================================================================
    // Tapered path drawing
    // =====================================================================================

    /// <summary>
    /// Draws the strand from its base up to <paramref name="reveal"/> (0..1) of its length. u0/u1
    /// (taper + sheen position) are normalized against however many segments are CURRENTLY being
    /// drawn (fullSegs, plus one more for the clipped partial segment at the tip) rather than the
    /// strand's eventual full sample count - so a growing tip always reads as a properly tapered
    /// tip (thin, right where the sheen currently sits), not a uniform-thickness noodle that
    /// suddenly finishes tapering once fully grown. This is index-based, exactly like the original
    /// BindEffect (which only ever walked its own evenly-stepped path) rather than arc-length-based
    /// like ChainSkin's link placement - deliberately: normalizing by actual arc length instead
    /// changes the taper's rate of change per full segment, which is a visible difference at low
    /// reveal, not just a rounding difference. Applied to a non-uniform path (e.g. Heavy's bezier,
    /// if reskinned to this material) it distributes the taper across sample-index fraction rather
    /// than true arc-length fraction - a minor approximation, not a functional problem.
    /// </summary>
    private static void DrawTaperedPath(ImDrawListPtr dl, StrandPath path, float baseW, float a, float px,
                                        float reveal, float time, float phase,
                                        out Vector2 tipPos, out bool tipVisible)
    {
        tipPos = default;
        tipVisible = false;
        int count = path.Count;
        if (count < 2 || a <= 0.002f || reveal <= 0.001f) return;

        float maxSeg = (count - 1) * reveal;
        int fullSegs = (int)maxSeg;
        float partialFrac = maxSeg - fullSegs;
        if (fullSegs >= count - 1)
        {
            fullSegs = count - 2;
            partialFrac = 1f;
        }

        int segCount = fullSegs + (partialFrac > 0.001f ? 1 : 0);
        if (segCount <= 0) return;

        float sheenRaw = time * 0.35f + phase;
        float sheenPos = sheenRaw - MathF.Floor(sheenRaw);

        var pts = path.Points;

        for (int i = 0; i < segCount; i++)
        {
            Vector2 p0 = pts[i];
            Vector2 p1 = (i < fullSegs) ? pts[i + 1] : Vector2.Lerp(pts[i], pts[i + 1], partialFrac);

            float u0 = (float)i / segCount;
            float u1 = (float)(i + 1) / segCount;

            float w0 = TaperWidth(u0, baseW);
            float w1 = TaperWidth(u1, baseW);
            float w  = 0.5f * (w0 + w1);

            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(Halo, a * 0.45f), w * 3.4f + 2f * px);

            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(Void, a),         w * 1.00f);
            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(Body, a * 0.92f), w * 0.82f);

            Vector2 dir = p1 - p0;
            float len = dir.Length();
            if (len > 1e-4f)
            {
                Vector2 perp = new(-dir.Y / len, dir.X / len);

                float lightDot = Vector2.Dot(perp, LightDir);
                if (lightDot > 0f)
                {
                    Vector2 off = perp * (w * 0.34f * lightDot);
                    dl.AddLine(p0 + off, p1 + off,
                               DrawHelpers.WithAlpha(BodyLit, a * 0.60f * lightDot),
                               w * 0.30f);
                }

                float midU = (u0 + u1) * 0.5f;
                float sDelta = midU - sheenPos;
                sDelta -= MathF.Round(sDelta);
                float sheenK = MathF.Exp(-(sDelta * sDelta) / 0.010f);
                if (sheenK > 0.02f)
                {
                    float sideK = lightDot > 0f ? 0.55f + 0.45f * lightDot : 0.45f;
                    Vector2 sheenOff = perp * (w * 0.18f * (lightDot > 0f ? lightDot : 0f));
                    dl.AddLine(p0 + sheenOff, p1 + sheenOff,
                               DrawHelpers.WithAlpha(CoreHot, a * 0.50f * sheenK * sideK),
                               w * 0.42f);
                }
            }

            float coreW = w * 0.22f;
            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(Core,    a * 0.80f), coreW * 2.4f);
            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(Core,    a * 0.95f), coreW * 1.1f);
            dl.AddLine(p0, p1, DrawHelpers.WithAlpha(CoreHot, a * 0.70f), MathF.Max(0.6f, coreW * 0.55f));

            if (i == segCount - 1)
            {
                tipPos = p1;
                tipVisible = true;
            }
        }
    }

    private static float TaperWidth(float u, float baseW)
    {
        float f = MathF.Pow(1f - u, 0.65f);
        return baseW * (0.22f + 0.78f * f);
    }

    // =====================================================================================
    // Slime drips
    // =====================================================================================

    private static void DrawSlimeDrips(ImDrawListPtr dl, StrandPath path, float baseW, float a,
                                       float reveal, float time, int seed)
    {
        int count = path.Count;
        if (count < 2 || a <= 0.002f) return;

        int dripCount = 3 + (int)(DrawHelpers.Hash01(seed + 900) * 3f); // 3..5 per strand
        float aS = a * 0.85f;

        for (int d = 0; d < dripCount; d++)
        {
            int ds = unchecked(seed + 900 + d * 131);
            float attachT = DrawHelpers.HashRange(ds, 0.15f, 0.90f);
            if (attachT > reveal) break;

            bool flowing = DrawHelpers.Hash01(ds + 50) < 0.40f;
            int at = Math.Clamp((int)(attachT * (count - 1)), 0, count - 1);

            if (flowing)
                DrawFlowingDrip(dl, path.Points, count, at, baseW, aS, time, ds);
            else
                DrawHangingDrip(dl, path.Points[at], baseW, aS, time, ds);
        }
    }

    private static void DrawHangingDrip(ImDrawListPtr dl, Vector2 onPath, float baseW, float aS,
                                        float time, int ds)
    {
        float lateral = DrawHelpers.HashRange(ds + 1, -0.6f, 0.6f);
        Vector2 dripBase = onPath + new Vector2(baseW * lateral, baseW * 0.85f);

        float phase = DrawHelpers.HashRange(ds + 2, 0f, 1f);
        float cycle = (time * 0.42f + phase) % 1f;
        if (cycle < 0f) cycle += 1f;

        float swell = MathF.Sin(cycle * MathF.PI);
        float blobR = baseW * (0.42f + 0.32f * swell);

        dl.AddCircleFilled(dripBase, blobR * 2.8f, DrawHelpers.WithAlpha(SlimeCore, aS * 0.09f));
        dl.AddCircleFilled(dripBase, blobR * 0.92f, DrawHelpers.WithAlpha(SlimeBody, aS * 0.88f));
        Vector2 teardrop = dripBase + new Vector2(0f, blobR * 0.75f * swell);
        dl.AddCircleFilled(teardrop, blobR * 0.62f, DrawHelpers.WithAlpha(SlimeBody, aS * 0.80f));
        dl.AddCircleFilled(dripBase, blobR * 0.55f, DrawHelpers.WithAlpha(SlimeCore, aS * 0.60f));
        dl.AddCircleFilled(dripBase, blobR * 0.28f, DrawHelpers.WithAlpha(CoreHot,   aS * 0.90f));

        if (cycle > 0.55f)
        {
            float fallT = (cycle - 0.55f) / 0.45f;
            float fallDist = fallT * fallT * baseW * 14f;
            Vector2 fallPos = dripBase + new Vector2(0f, fallDist);

            float fallA = 1f - fallT * fallT * fallT;
            float fallR = baseW * 0.34f * (1f - 0.35f * fallT);
            float speedFrac = 2f * fallT;
            float stretch = fallR * (1.4f + 4.0f * speedFrac);

            DrawStretchedDroplet(dl, fallPos, new Vector2(0f, 1f), fallR, stretch, aS * fallA);
        }
    }

    private static void DrawFlowingDrip(ImDrawListPtr dl, ReadOnlySpan<Vector2> path, int count,
                                        int startIndex, float baseW, float aS, float time, int ds)
    {
        if (startIndex < 4) startIndex = 4;

        float phase = DrawHelpers.HashRange(ds + 20, 0f, 1f);
        float speed = 0.22f + 0.14f * DrawHelpers.Hash01(ds + 21);
        float t = (time * speed + phase) % 1f;

        float idxFloat = startIndex * (1f - t);
        int i0 = Math.Clamp((int)idxFloat, 0, count - 2);
        float f = Math.Clamp(idxFloat - i0, 0f, 1f);

        Vector2 p = Vector2.Lerp(path[i0], path[i0 + 1], f);
        Vector2 tangent = path[i0 + 1] - path[i0];
        float tlen = tangent.Length();
        if (tlen < 1e-4f) return;
        Vector2 dir = tangent / tlen;

        float alpha = MathF.Sin(t * MathF.PI);
        float dropR  = baseW * 0.30f;
        float stretch = dropR * 2.6f;

        DrawStretchedDroplet(dl, p, dir, dropR, stretch, aS * alpha * 0.85f);
    }

    private static void DrawStretchedDroplet(ImDrawListPtr dl, Vector2 center, Vector2 dir,
                                             float radius, float totalLength, float alpha)
    {
        if (alpha <= 0.01f || radius <= 0.2f) return;

        float half = MathF.Max(0f, totalLength * 0.5f - radius);
        Vector2 a = center - dir * half;
        Vector2 b = center + dir * half;

        uint glow = DrawHelpers.WithAlpha(SlimeCore, alpha * 0.15f);
        uint body = DrawHelpers.WithAlpha(SlimeCore, alpha * 0.70f);
        uint hot  = DrawHelpers.WithAlpha(CoreHot,   alpha * 0.90f);

        dl.AddLine(a, b, glow, radius * 3.0f);
        dl.AddLine(a, b, body, radius * 2.0f);
        dl.AddLine(a, b, hot,  radius * 0.9f);
        dl.AddCircleFilled(a, radius,        body);
        dl.AddCircleFilled(b, radius,        body);
        dl.AddCircleFilled(a, radius * 0.55f, hot);
        dl.AddCircleFilled(b, radius * 0.55f, hot);
    }
}
