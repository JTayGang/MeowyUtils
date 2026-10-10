using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// The pieces every lit, photographic strand material shares, so a chain, a rope and anything that
/// follows them agree on how a strand sits in the scene: the haze that far strands fade into, the
/// soft shadow cast away from the key light, and the glint at a growing tip.
///
/// Nothing here knows what the strand is made of; <c>scale</c> is the strand's characteristic size in
/// pixels (a chain's link length; roughly three rope diameters), which sets the shadow's width and
/// how far it falls.
/// </summary>
internal static class StrandShading
{
    /// <summary>Colour a strand hazes toward as its Depth approaches 1.</summary>
    public static readonly Vector3 DepthFog = new(0.105f, 0.125f, 0.165f);

    public static readonly uint ShadowTint = FireColor.Pack(0.015f, 0.016f, 0.022f);
    public static readonly uint FlareWarm  = FireColor.Pack(1.00f, 0.80f, 0.52f);

    /// <summary>Soft cast band under the strand, displaced away from the key light.</summary>
    public static void DrawShadow(ImDrawListPtr dl, StrandPath path, float scale, float depth, float alpha,
                                  in MaterialContext ctx, bool closed, float visibleLen)
    {
        float a = alpha * 0.40f * (1f - 0.45f * depth);
        if (a <= 0.004f) return;

        float total = path.Length;
        bool full = closed || visibleLen >= total;
        float s0 = closed ? 0f : (full ? -scale * 0.5f : 0f);
        float s1 = closed ? total : (full ? total + scale * 0.5f : visibleLen);
        int rows = Math.Clamp((int)((s1 - s0) / (scale * 0.5f)), 3, 40);
        float reach = scale * 0.60f * (1f - 0.55f * depth);
        Vector2 offset = StudioLighting.ShadowDirection * reach;
        float hw = scale * 0.34f;
        float soft = hw * (0.9f + 1.1f * depth);

        ReadOnlySpan<float> across = stackalloc float[5] { -1f, -0.55f, 0f, 0.55f, 1f };
        ReadOnlySpan<float> prof = stackalloc float[5] { 0f, 0.62f, 1f, 0.62f, 0f };

        int v = 0;
        for (int r = 0; r <= rows; r++)
        {
            float sArc = s0 + (s1 - s0) * r / rows;
            Vector2 p, t;
            if (closed) SampleLoop(path, sArc, total, (s1 - s0) / rows * 0.5f, out p, out t);
            else        path.SampleAtArc(sArc, out p, out t);
            Vector2 nrm = new(-t.Y, t.X);
            float edgeFade = closed ? 1f : MathF.Min(1f, MathF.Min(r, rows - r) / 1.5f);

            for (int c = 0; c < 5; c++)
            {
                float w = across[c];
                MeshDraw.P[v] = p + offset + nrm * (w * (hw + soft * 0.5f * MathF.Abs(w)));
                MeshDraw.C[v] = DrawHelpers.WithAlpha(ShadowTint, a * prof[c] * edgeFade);
                v++;
            }
        }
        MeshDraw.Grid(dl, 4, rows, MeshDraw.WhiteUv(ctx.Time));
    }

    /// <summary>
    /// Position and tangent at arc length s on a closed loop. The tangent is a central difference
    /// that wraps, so the loop's first and last rows (the same point) get the same cross-section and
    /// the seam can't be seen; a one-sided difference there would leave a hairline crack.
    /// </summary>
    private static void SampleLoop(StrandPath path, float s, float total, float h, out Vector2 p, out Vector2 t)
    {
        path.SampleAtArc(Wrap(s, total), out p, out _);
        path.SampleAtArc(Wrap(s + h, total), out Vector2 ahead, out _);
        path.SampleAtArc(Wrap(s - h, total), out Vector2 behind, out _);
        Vector2 d = ahead - behind;
        float len = d.Length();
        t = len > 1e-4f ? d / len : new Vector2(1f, 0f);
    }

    private static float Wrap(float s, float total)
    {
        s %= total;
        return s < 0f ? s + total : s;
    }

    /// <summary>A bright pip with a warm halo, for the tip of a strand that is still growing or flying.</summary>
    public static void DrawFlare(ImDrawListPtr dl, Vector2 tip, float bar, float k, in MaterialContext ctx)
    {
        if (k <= 0.004f) return;
        Span<float> rr = stackalloc float[2] { bar * 1.1f, bar * 3.0f };
        Span<uint> cc = stackalloc uint[2]
        {
            DrawHelpers.WithAlpha(FlareWarm, 0.30f * k),
            DrawHelpers.WithAlpha(FlareWarm, 0f),
        };
        MeshDraw.Radial(dl, tip, MeshDraw.WhiteUv(ctx.Time), DrawHelpers.WithAlpha(0xFFFFFFFFu, 0.85f * k),
                        10, rr, cc, 0f, 1f, 0, 0f);
    }
}
