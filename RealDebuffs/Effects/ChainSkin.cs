using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// The "interlocking iron" material originally authored for Heavy: discrete stadium-shaped links
/// alternating flat/face-on (a fat metal band with a see-through hole) and edge-on (a solid filled
/// bar), drawn in two passes - flats first, then edges on top - so every edge-on link visibly sits
/// in front of the flat loops on either side, which is what makes it read as one continuous chain
/// rather than a string of beads. A hot spark marks the growing tip while <paramref name="tipFlare"/>
/// is above zero. Purely a function of the StrandPath and StrandVisual it's handed - it has no idea
/// whether it's drawing one of Heavy's own anchored chains or some other effect's strand that's
/// been reskinned to look like one.
///
/// Because links are discrete and evenly spaced, this is the skin that most needs the path's
/// arc-length table rather than raw sample indices (see <see cref="StrandPath.SampleAtArc"/>), so
/// link placement stays even and the "continues past the edge of the screen" extrapolation still
/// works even on a path that wasn't authored with links in mind at all, like a tendril's curl-and-
/// wave curve.
///
/// Stateless singleton in the same sense as TentacleSkin: the palette is baked once into static
/// readonly fields, and the small link-outline scratch buffer below is fully overwritten and fully
/// consumed within each single call, so it never carries state between calls or between strands -
/// sharing one <see cref="Instance"/> across many strands in the same frame is safe.
/// </summary>
public sealed class ChainSkin : IStrandSkin
{
    public static readonly ChainSkin Instance = new();
    private ChainSkin() { }

    private const int StadiumCapSegs = 6;                    // segments in each semicircular cap of a link
    private const int StadiumPoints  = 2 * StadiumCapSegs + 3; // total points in a closed link loop

    private static readonly uint Ink       = DrawHelpers.ToU32(0.03f, 0.03f, 0.04f, 1f); // near-black silhouette / link rims
    private static readonly uint Dark      = DrawHelpers.ToU32(0.24f, 0.24f, 0.24f, 1f); // iron body
    private static readonly uint Mid       = DrawHelpers.ToU32(0.34f, 0.34f, 0.34f, 1f); // mid-tone rail down the centre of a flat loop
    private static readonly uint Highlight = DrawHelpers.ToU32(0.86f, 0.90f, 0.95f, 1f); // pale specular glint / hot tip
    private static readonly uint Glow      = DrawHelpers.ToU32(0.85f, 0.82f, 0.75f, 1f); // warm off-white halo around the tip

    // Per-call scratch (needs StadiumPoints slots; one extra for safety). Fully overwritten and
    // fully consumed inside DrawLink every time, so it never leaks state between calls.
    private readonly Vector2[] _linkPts = new Vector2[StadiumPoints + 1];

    public void DrawStrand(ImDrawListPtr dl, StrandPath path, in StrandVisual visual,
                           float reveal, float tipFlare, float alpha, float px, float time,
                           out Vector2 tipPos, out bool tipVisible)
    {
        tipPos = default;
        tipVisible = false;

        float totalLength = path.Length;
        float linkLength = visual.Thickness;
        // No alpha guard here on purpose, matching the original HeavyEffect exactly: it always drew
        // full link geometry regardless of how small alpha got, relying on the alpha channel alone
        // to make it invisible, rather than skipping draw calls. path.Count/linkLength guards are
        // pure divide-by-zero/degenerate-path safety, not a behavior original Heavy ever exercised.
        if (path.Count < 2 || linkLength <= 0f || totalLength < linkLength) return;

        reveal = Math.Clamp(reveal, 0f, 1f);
        float revealLen = totalLength * reveal;
        bool fullyRevealed = reveal >= 1f;

        float spacing       = linkLength * 0.60f; // links overlap ~40% so they read as interlocked
        float halfLen       = linkLength * 0.5f;
        float flatHalfWidth = linkLength * 0.30f; // face-on: a proper stadium with a visible hole
        float edgeHalfWidth = linkLength * 0.11f; // edge-on: a solid narrow bar
        float drawUpTo      = MathF.Min(revealLen, totalLength);

        // Link indices run from startFlat/-1 (a couple of positions before the path's start, or
        // just -1 if the base sits flush on a hard edge - see StrandVisual.FlushStart) through
        // maxLinks (a couple past the end), so the chain visibly continues off-screen at both ends
        // instead of terminating on the frame. SampleAtArc extrapolates past the path's own
        // endpoints for those off-screen placements.
        int startFlat = visual.FlushStart ? 0 : -2;
        int maxLinks  = (int)(totalLength / spacing) + 2;

        // Pass 1: flat (face-on) links, drawn as a fat metal band with a transparent hole inside.
        for (int i = startFlat; i <= maxLinks; i += 2)
        {
            float linkEnd = i * spacing + linkLength;
            if (!fullyRevealed && i >= 0 && linkEnd > drawUpTo) break;

            float s = i * spacing + halfLen;
            path.SampleAtArc(s, out Vector2 pos, out Vector2 tan);
            DrawLink(dl, pos, tan, halfLen, flatHalfWidth, px, alpha, filled: false);
        }

        // Pass 2: edge-on links, drawn AFTER all flats so they always sit on top. Filled solid, so
        // each reads as the side of the link passing in front of the flat loops on either side.
        for (int i = -1; i <= maxLinks; i += 2)
        {
            float linkEnd = i * spacing + linkLength;
            if (!fullyRevealed && i >= 0 && linkEnd > drawUpTo) break;

            float s = i * spacing + halfLen;
            path.SampleAtArc(s, out Vector2 pos, out Vector2 tan);
            DrawLink(dl, pos, tan, halfLen, edgeHalfWidth, px, alpha, filled: true);
        }

        if (revealLen > 0f || fullyRevealed)
        {
            float tipS = fullyRevealed ? totalLength : revealLen;
            path.SampleAtArc(tipS, out tipPos, out _);
            tipVisible = true;

            if (tipFlare > 0.001f)
                DrawTip(dl, tipPos, px, alpha, Math.Clamp(tipFlare, 0f, 1f));
        }
    }

    /// <summary>
    /// Builds a closed stadium outline (running-track shape: two straight rails plus semicircular
    /// caps) into <see cref="_linkPts"/> and returns the point count. The long axis follows
    /// <paramref name="tangent"/>; the short axis is its perpendicular. Points are in draw order and
    /// the loop is closed by repeating the first point at the end.
    /// </summary>
    private int BuildStadium(Vector2 center, Vector2 tangent, float halfLen, float halfWidth)
    {
        Vector2 perp = new Vector2(-tangent.Y, tangent.X);
        float s = MathF.Max(0f, halfLen - halfWidth); // straight-rail half-length
        const int cap = StadiumCapSegs;
        int idx = 0;

        // Top rail: top-left transition -> top-right transition.
        _linkPts[idx++] = center + tangent * (-s) + perp * halfWidth;
        _linkPts[idx++] = center + tangent * ( s) + perp * halfWidth;

        // Right cap: top-right -> bottom-right, clockwise (through the rightmost point).
        for (int i = 1; i < cap; i++)
        {
            float a = MathF.PI * 0.5f * (1f - 2f * i / cap); // +pi/2 .. -pi/2, exclusive
            _linkPts[idx++] = center + tangent * (s + MathF.Cos(a) * halfWidth)
                                     + perp    * (MathF.Sin(a) * halfWidth);
        }
        _linkPts[idx++] = center + tangent * ( s) + perp * (-halfWidth); // bottom-right

        // Bottom rail: to bottom-left.
        _linkPts[idx++] = center + tangent * (-s) + perp * (-halfWidth);

        // Left cap: bottom-left -> top-left, clockwise (through the leftmost point).
        for (int i = 1; i < cap; i++)
        {
            float a = -MathF.PI * 0.5f - MathF.PI * i / cap; // -pi/2 .. -3pi/2, exclusive
            _linkPts[idx++] = center + tangent * (-s + MathF.Cos(a) * halfWidth)
                                     + perp    * (MathF.Sin(a) * halfWidth);
        }
        _linkPts[idx++] = center + tangent * (-s) + perp * halfWidth; // top-left (close)

        return idx;
    }

    /// <summary>
    /// One chain link. Flat links are drawn as a fat layered metal band (dark silhouette, dark grey,
    /// mid-tone grey) with a transparent hole through the middle so you can see through the loop.
    /// Edge-on links are drawn FILLED solid with a thin dark rim, so they read as the solid side of a
    /// link rather than a wire.
    /// </summary>
    private void DrawLink(ImDrawListPtr dl, Vector2 center, Vector2 tangent,
                          float halfLen, float halfWidth, float px, float alpha, bool filled)
    {
        int count = BuildStadium(center, tangent, halfLen, halfWidth);
        ref Vector2 first = ref _linkPts[0];

        uint ink = DrawHelpers.WithAlpha(Ink,  0.70f * alpha);
        uint drk = DrawHelpers.WithAlpha(Dark, 0.95f * alpha);
        uint mid = DrawHelpers.WithAlpha(Mid,  0.95f * alpha);

        if (filled)
        {
            dl.AddConvexPolyFilled(ref first, count, drk);
            dl.AddPolyline(ref first, count, ink, ImDrawFlags.None, 1.8f * px);
        }
        else
        {
            dl.AddPolyline(ref first, count, ink, ImDrawFlags.None, 11.0f * px);
            dl.AddPolyline(ref first, count, drk, ImDrawFlags.None,  8.5f * px);
            dl.AddPolyline(ref first, count, mid, ImDrawFlags.None,  2.4f * px);
        }
    }

    /// <summary>A hot flare at the growing/settling tip: three stacked discs so it reads as a spark, not a lollipop.</summary>
    private static void DrawTip(ImDrawListPtr dl, Vector2 p, float px, float alpha, float strength)
    {
        if (strength <= 0f) return;
        uint glow = DrawHelpers.WithAlpha(Glow,      0.55f * strength * alpha);
        uint hot  = DrawHelpers.WithAlpha(Highlight, 0.95f * strength * alpha);
        uint core = DrawHelpers.WithAlpha(0xFFFFFFFFu, 0.90f * strength * alpha);

        dl.AddCircleFilled(p, 14f * px, glow);
        dl.AddCircleFilled(p,  5f * px, hot);
        dl.AddCircleFilled(p,  2f * px, core);
    }
}
