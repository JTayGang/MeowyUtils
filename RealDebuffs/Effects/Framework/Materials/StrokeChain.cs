using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects.Framework.Materials;

/// <summary>
/// Interlocking iron chain: discrete stadium-shaped links alternating flat (face-on, a fat metal
/// band with a see-through hole) and edge-on (a solid filled bar), drawn in two passes - flats
/// first, edges on top - so every edge-on link visibly sits in front of the flat loops on either
/// side. That alternation is what makes the strand read as one continuous chain rather than a
/// string of beads.
///
/// Ported from the original ChainSkin with no geometry changes. Uses the path's arc-length table
/// (StrandPath.SampleAtArc) so link placement is even regardless of the incoming path's own point
/// spacing, and so the chain continues past either end of the path rather than terminating on the
/// frame. FlushStart tells it to skip the off-screen link before a strand whose base sits exactly
/// on a hard edge.
/// </summary>
public sealed class StrokeChain : IStrokeMaterial
{
    public string Name => "stroke.chain";
    public string[] NaturalLanguageWords { get; } = { "chain", "chains", "links" };

    // Chain links have a natural size range: too small and the interlock pattern is illegible
    // (they read as a string of beads), too large and a single link dominates the frame instead
    // of reading as part of a longer chain. Clamped as a fraction of the shorter screen side so
    // the range scales with resolution. Both bounds are deliberately generous enough that
    // Heavy's own native hint (~0.044 shortSide) sits comfortably inside them, unaffected.
    private const float MinLinkFrac = 0.030f; // ~32px at 1080p
    private const float MaxLinkFrac = 0.075f; // ~81px at 1080p

    private const int StadiumCapSegs = 6;
    private const int StadiumPoints  = 2 * StadiumCapSegs + 3;

    private static readonly uint Ink       = DrawHelpers.ToU32(0.03f, 0.03f, 0.04f, 1f);
    private static readonly uint Dark      = DrawHelpers.ToU32(0.24f, 0.24f, 0.24f, 1f);
    private static readonly uint Mid       = DrawHelpers.ToU32(0.34f, 0.34f, 0.34f, 1f);
    private static readonly uint Highlight = DrawHelpers.ToU32(0.86f, 0.90f, 0.95f, 1f);
    private static readonly uint Glow      = DrawHelpers.ToU32(0.85f, 0.82f, 0.75f, 1f);

    private readonly Vector2[] _linkPts = new Vector2[StadiumPoints + 1];

    public void Draw(ImDrawListPtr dl, in StrokePrimitive s, in MaterialContext ctx)
    {
        var path = s.Path;
        float alpha = s.Brightness * ctx.Alpha;
        float px = ctx.ScreenScale;

        float totalLength = path.Length;
        float linkLength = s.WidthHint;

        // Clamp the effective link length to this material's viable range. A tentacle-authored
        // hint (~15-26px) lands at the floor and reads as a proper chain; a chain-authored hint
        // (~47px) passes through unchanged.
        float minLink = ctx.ShortSide * MinLinkFrac;
        float maxLink = ctx.ShortSide * MaxLinkFrac;
        if (linkLength < minLink) linkLength = minLink;
        if (linkLength > maxLink) linkLength = maxLink;

        if (path.Count < 2 || linkLength <= 0f || totalLength < linkLength) return;

        float reveal = Math.Clamp(s.Reveal, 0f, 1f);
        float revealLen = totalLength * reveal;
        bool fullyRevealed = reveal >= 1f;

        float spacing       = linkLength * 0.60f;
        float halfLen       = linkLength * 0.5f;
        float flatHalfWidth = linkLength * 0.30f;
        float edgeHalfWidth = linkLength * 0.11f;
        float drawUpTo      = MathF.Min(revealLen, totalLength);

        int startFlat = s.FlushStart ? 0 : -2;
        int maxLinks  = (int)(totalLength / spacing) + 2;

        // Pass 1: flat (face-on) links.
        for (int i = startFlat; i <= maxLinks; i += 2)
        {
            float linkEnd = i * spacing + linkLength;
            if (!fullyRevealed && i >= 0 && linkEnd > drawUpTo) break;

            float pos = i * spacing + halfLen;
            path.SampleAtArc(pos, out Vector2 at, out Vector2 tan);
            DrawLink(dl, at, tan, halfLen, flatHalfWidth, px, alpha, filled: false);
        }

        // Pass 2: edge-on links, on top.
        for (int i = -1; i <= maxLinks; i += 2)
        {
            float linkEnd = i * spacing + linkLength;
            if (!fullyRevealed && i >= 0 && linkEnd > drawUpTo) break;

            float pos = i * spacing + halfLen;
            path.SampleAtArc(pos, out Vector2 at, out Vector2 tan);
            DrawLink(dl, at, tan, halfLen, edgeHalfWidth, px, alpha, filled: true);
        }

        // Hot flare at the growing/settling tip, gated on TipFlare.
        if (s.TipFlare > 0.001f)
        {
            float tipS = fullyRevealed ? totalLength : revealLen;
            path.SampleAtArc(tipS, out Vector2 tip, out _);
            DrawTip(dl, tip, px, alpha, Math.Clamp(s.TipFlare, 0f, 1f));
        }
    }

    private int BuildStadium(Vector2 center, Vector2 tangent, float halfLen, float halfWidth)
    {
        Vector2 perp = new Vector2(-tangent.Y, tangent.X);
        float s = MathF.Max(0f, halfLen - halfWidth);
        const int cap = StadiumCapSegs;
        int idx = 0;

        _linkPts[idx++] = center + tangent * (-s) + perp * halfWidth;
        _linkPts[idx++] = center + tangent * ( s) + perp * halfWidth;

        for (int i = 1; i < cap; i++)
        {
            float a = MathF.PI * 0.5f * (1f - 2f * i / cap);
            _linkPts[idx++] = center + tangent * (s + MathF.Cos(a) * halfWidth)
                                     + perp    * (MathF.Sin(a) * halfWidth);
        }
        _linkPts[idx++] = center + tangent * ( s) + perp * (-halfWidth);
        _linkPts[idx++] = center + tangent * (-s) + perp * (-halfWidth);

        for (int i = 1; i < cap; i++)
        {
            float a = -MathF.PI * 0.5f - MathF.PI * i / cap;
            _linkPts[idx++] = center + tangent * (-s + MathF.Cos(a) * halfWidth)
                                     + perp    * (MathF.Sin(a) * halfWidth);
        }
        _linkPts[idx++] = center + tangent * (-s) + perp * halfWidth;

        return idx;
    }

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