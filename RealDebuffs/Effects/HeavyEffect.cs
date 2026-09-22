using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Heavy: a fan of massive iron chains slams out of the screen and locks across it, settling into a
/// slow, weighted sway while the ground darkens under their pull.
///
/// How it's built, since nothing is stored between frames:
///  - The cast layout is fixed at five chains: two anchor to the top edge (one in each half, running
///    down to a random point on the left / right edge), two anchor to the bottom edge the same way,
///    and one free chain whose two endpoints can be anywhere on the perimeter as long as they are at
///    least a quarter of the perimeter apart - so it can never collapse into a short hop along a
///    single edge. The layout is reseeded from the cast start time, so it is stable within one
///    application of the debuff and different the next time.
///  - Chains anchored to the bottom edge are flagged AnchorBottom so the pre-start overshoot flat
///    link is skipped: without that, an extra face-on loop could poke up past the bottom edge as the
///    chain sways.
///  - Each chain's path is a quadratic bezier between its start, control and end points, plus a
///    parabolic sag (always positive Y, so the middle bows downward) and a slow travelling wave.
///    All coordinates are normalized and expanded to pixels fresh every frame, so the effect scales
///    cleanly with the window.
///  - The path is sampled into a polyline and parameterized by arc length. Links are placed at even
///    spacing along the arc, alternating between "flat" (face-on, wide stadium) and "edge-on" (thin
///    stadium, i.e. the side of a link viewed from the side). The path is linearly extrapolated past
///    both endpoints, and link placement runs from a couple of positions before the start to a couple
///    past the end, so the chain visibly continues off-screen instead of terminating on the frame.
///  - Links are stadiums, not ellipses: two straight parallel rails with semicircular caps. Flat links
///    are drawn as a fat metal band (layered polylines: dark silhouette, dark grey, mid-tone grey)
///    with a transparent hole in the middle so you can see through the loop. Edge-on links are drawn
///    FILLED solid, and the whole chain is drawn in TWO passes - flats first, then edges - so every
///    edge-on link sits on top of the flat links on either side. That's what makes it read as a chain
///    instead of a string of beads: you see the loop, then the solid side of the next link in front
///    of it, then the next loop.
///  - The cast-in is pure timing. Each chain has a stagger delay; its reveal length is
///    EaseOutCubic(age - delay) of its total length; links whose far edge sits beyond the reveal
///    length are not drawn. The growing tip carries a hot flare while it runs and a brief settle
///    flash where it locks in. After the cast-in the whole fan sways on a travelling wave whose
///    period matches the ground pulse, so the effect breathes as one unit.
///
/// Nothing allocates per frame; scratch arrays are sized once at construction.
/// </summary>
public sealed class HeavyEffect : IScreenEffect
{
    public DebuffKind Kind => DebuffKind.Heavy;

    // ---- timing ----
    private const float NewCastGapSeconds   = 1.0f; // long enough that an ordinary frame hitch mid-fight never replays the cast-in
    private const float ChainExtendSeconds  = 0.55f;
    private const float SettleStart         = 0.55f;
    private const float SettleEnd           = 1.80f;
    private const float SettleFlashSeconds  = 0.30f;

    // ---- geometry ----
    private const int   ChainSamples    = 16;  // polyline resolution per chain (arc-length interpolated between these)
    private const int   StadiumCapSegs  = 6;   // segments in each semicircular cap of a link
    private const int   StadiumPoints   = 2 * StadiumCapSegs + 3; // total points in a closed link loop
    private const int   ChainCount      = 5;   // 4 edge-anchored + 1 free
    private const float Tau             = MathF.PI * 2f;

    private uint _ink, _dark, _mid, _highlight, _glow;
    private bool _paletteReady;

    private float _lastDrawTime = -100f;
    private float _castStart;

    // Per-frame scratch. _linkPts needs StadiumPoints slots; leave one extra for safety.
    private readonly Vector2[] _path    = new Vector2[ChainSamples];
    private readonly float[]   _arc     = new float[ChainSamples];
    private readonly Vector2[] _linkPts = new Vector2[StadiumPoints + 1];

    // Layout for the current cast, re-rolled each time the debuff is (re)applied.
    private readonly Blueprint[] _blueprints = new Blueprint[ChainCount];

    private readonly struct Blueprint
    {
        public readonly Vector2 Start, End, Control;
        public readonly float Sag;    // fraction of screen height added at the middle (always positive)
        public readonly float Delay;  // seconds after the cast-in starts that this chain begins extending
        public readonly float Scale;  // link-size multiplier, for depth between chains
        public readonly int   Seed;
        public readonly bool  AnchorBottom; // true for the two chains that start on the bottom edge

        public Blueprint(Vector2 start, Vector2 end, Vector2 control,
                         float sag, float delay, int seed, float scale, bool anchorBottom)
        {
            Start        = start;
            End          = end;
            Control      = control;
            Sag          = sag;
            Delay        = delay;
            Seed         = seed;
            Scale        = scale;
            AnchorBottom = anchorBottom;
        }
    }

    public void Draw(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;

        // EffectManager stops calling Draw once the effect has fully faded out, so a gap since the
        // last call means the debuff was just (re)applied: restart the cast-in from age zero, and
        // re-roll the chain layout.
        if (time - _lastDrawTime > NewCastGapSeconds)
        {
            _castStart = time;
            BuildBlueprints(unchecked((int)(_castStart * 1000f)));
        }
        _lastDrawTime = time;
        float age = time - _castStart;

        EnsurePalette();

        float minDim   = MathF.Min(screenSize.X, screenSize.Y);
        float px       = Math.Clamp(minDim / 1080f, 0.75f, 2.4f);
        float linkBase = minDim * 0.044f;

        DrawGround(dl, screenSize, alpha, time, age);

        for (int i = 0; i < ChainCount; i++)
            DrawChain(dl, in _blueprints[i], screenSize, px, linkBase, alpha, time, age);
    }

    /// <summary>
    /// Re-rolls the per-cast chain layout. Everything is derived from <paramref name="castSeed"/>,
    /// so it is stable for the whole cast and different the next time.
    ///
    /// Layout, in normalized screen coordinates (0..1):
    ///   [0] Top-left half of the top edge, down to a random point on the left edge.
    ///   [1] Top-right half of the top edge, down to a random point on the right edge.
    ///   [2] Bottom-left half of the bottom edge, up to a random point on the left edge.
    ///   [3] Bottom-right half of the bottom edge, up to a random point on the right edge.
    ///   [4] Free chain: two random points on the perimeter at least 1/4 of the perimeter apart.
    /// </summary>
    private void BuildBlueprints(int castSeed)
    {
        const float cx      = 0.5f;       // screen centre (x)
        const float third   = 1f / 3f;    // the "~1/3 to the edge" band
        const float edgePad = 0.2f;       // keep edge endpoint off the corners

        for (int i = 0; i < 4; i++)
        {
            int s = unchecked(castSeed + i * 977);

            bool topHalf  = i < 2;        // 0,1 top; 2,3 bottom
            bool leftHalf = (i & 1) == 0; // 0,2 left; 1,3 right

            // Start on the top/bottom edge, inside the given half and within 1/3 of centre.
            float sy = topHalf ? 0f : 1f;
            float sx = leftHalf
                ? DrawHelpers.HashRange(s, cx - third, cx)
                : DrawHelpers.HashRange(s, cx,         cx + third);

            // End on the left/right edge, random y (padded away from the corners so the chain
            // actually spans some vertical space).
            float ex = leftHalf ? 0f : 1f;
            float ey = DrawHelpers.HashRange(s + 1, edgePad, 1f - edgePad);

            // Control point: midpoint of the two endpoints plus a little jitter for variety.
            float ccx = (sx + ex) * 0.5f + DrawHelpers.HashRange(s + 2, -0.08f, 0.08f);
            float ccy = (sy + ey) * 0.5f + DrawHelpers.HashRange(s + 3, -0.05f, 0.05f);

            float sag   = DrawHelpers.HashRange(s + 4, 0.1f, 0.2f);
            float delay = DrawHelpers.HashRange(s + 5, 0.00f, 0.22f);
            float scale = DrawHelpers.HashRange(s + 6, 0.85f, 1.15f);

            _blueprints[i] = new Blueprint(
                new Vector2(sx, sy),
                new Vector2(ex, ey),
                new Vector2(ccx, ccy),
                sag, delay, s, scale,
                anchorBottom: !topHalf);
        }

        // [4] Free chain. Pick point A anywhere on the perimeter, then point B in
        // [A + minSpan, A + perimeter - minSpan] (mod perimeter). That range guarantees the
        // SHORTER arc between A and B is at least minSpan long, so the chain can never collapse
        // into a short hop along one edge.
        {
            int s = unchecked(castSeed + 4 * 977);

            const float perimNorm = 4f;             // 2*(1 + 1) in normalized units
            const float minSpan   = perimNorm * 0.25f;

            float a       = DrawHelpers.HashRange(s,     0f, perimNorm);
            float bOffset = DrawHelpers.HashRange(s + 1, minSpan, perimNorm - minSpan);
            float b       = a + bOffset;

            Vector2 pa = PerimeterToNormalized(a);
            Vector2 pb = PerimeterToNormalized(b);

            float ccx = (pa.X + pb.X) * 0.5f + DrawHelpers.HashRange(s + 2, -0.08f, 0.08f);
            float ccy = (pa.Y + pb.Y) * 0.5f + DrawHelpers.HashRange(s + 3, -0.05f, 0.05f);

            float sag   = DrawHelpers.HashRange(s + 4, 0.06f, 0.14f);
            float delay = DrawHelpers.HashRange(s + 5, 0.00f, 0.22f);
            float scale = DrawHelpers.HashRange(s + 6, 0.85f, 1.15f);

            _blueprints[4] = new Blueprint(
                pa, pb, new Vector2(ccx, ccy),
                sag, delay, s, scale,
                anchorBottom: false);
        }
    }

    /// <summary>
    /// Walks the normalized screen perimeter clockwise from the top-left corner. Position is
    /// 0..4, one unit per edge, corners at 0 (TL), 1 (TR), 2 (BR), 3 (BL), 4 (wraps to TL).
    /// </summary>
    private static Vector2 PerimeterToNormalized(float p)
    {
        const float perim = 4f;
        p %= perim;
        if (p < 0f) p += perim;

        if (p < 1f) return new Vector2(p,         0f); // top, left -> right
        p -= 1f;
        if (p < 1f) return new Vector2(1f,        p);  // right, top -> bottom
        p -= 1f;
        if (p < 1f) return new Vector2(1f - p,    1f); // bottom, right -> left
        p -= 1f;
        return new Vector2(0f, 1f - p);                // left, bottom -> top
    }

    // =====================================================================================
    // One chain
    // =====================================================================================

    private void DrawChain(ImDrawListPtr dl, in Blueprint bp, Vector2 screenSize,
                           float px, float linkBase, float alpha, float time, float age)
    {
        float totalLength = BuildPath(in bp, screenSize, time, age);
        if (totalLength < linkBase) return;

        // Cast-in: how far down the path the leading edge has reached.
        float gt        = Saturate((age - bp.Delay) / ChainExtendSeconds);
        float revealLen = totalLength * EaseOutCubic(gt);
        bool fullyRevealed = gt >= 1f;

        float linkLength    = linkBase * bp.Scale;
        float spacing       = linkLength * 0.60f; // links overlap ~40% so they read as interlocked
        float halfLen       = linkLength * 0.5f;
        float flatHalfWidth = linkLength * 0.30f; // face-on: a proper stadium with a visible hole
        float edgeHalfWidth = linkLength * 0.11f; // edge-on: a solid narrow bar
        float drawUpTo      = MathF.Min(revealLen, totalLength);

        // Link indices run from startFlat/-1 (a couple of positions before the path start, or
        // just -1 for bottom-anchored chains) through maxLinks (a couple past the end), so the
        // chain visibly continues off-screen at both edges instead of terminating on the frame.
        // GetPathPoint extrapolates the path past its endpoints for the off-screen placements.
        //
        // Bottom-anchored chains skip the pre-start FLAT link (i = -2) so the extra "donut" that
        // could poke up past the bottom edge as the chain sways is never drawn; the pre-start EDGE
        // link (i = -1) is kept because it reads as the chain being pinned at the bottom.
        int startFlat = bp.AnchorBottom ? 0 : -2;
        int maxLinks  = (int)(totalLength / spacing) + 2;

        // Pass 1: flat (face-on) links, drawn as a fat metal band with a transparent hole inside.
        for (int i = startFlat; i <= maxLinks; i += 2)
        {
            // During cast-in, links past the current reveal length are skipped (the loop breaks
            // because links are placed in path order). Once fully revealed, all links draw,
            // including the off-screen ones at either end.
            float linkEnd = i * spacing + linkLength;
            if (!fullyRevealed && i >= 0 && linkEnd > drawUpTo) break;

            float s = i * spacing + halfLen;
            GetPathPoint(s, totalLength, out Vector2 pos, out Vector2 tan);
            DrawLink(dl, pos, tan, halfLen, flatHalfWidth, px, alpha, filled: false);
        }

        // Pass 2: edge-on links, drawn AFTER all flats so they always sit on top. Filled solid, so
        // each reads as the side of the link passing in front of the flat loops on either side.
        for (int i = -1; i <= maxLinks; i += 2)
        {
            float linkEnd = i * spacing + linkLength;
            if (!fullyRevealed && i >= 0 && linkEnd > drawUpTo) break;

            float s = i * spacing + halfLen;
            GetPathPoint(s, totalLength, out Vector2 pos, out Vector2 tan);
            DrawLink(dl, pos, tan, halfLen, edgeHalfWidth, px, alpha, filled: true);
        }

        // Growing-tip flare, then a brief settle flash where the chain locks in.
        if (gt > 0.001f && gt < 1f)
        {
            GetPathPoint(revealLen, totalLength, out Vector2 tip, out _);
            DrawTip(dl, tip, px, alpha, 1f - gt);
        }
        else if (gt >= 1f)
        {
            float settle = (age - bp.Delay - ChainExtendSeconds) / SettleFlashSeconds;
            if (settle >= 0f && settle < 1f)
            {
                GetPathPoint(totalLength, totalLength, out Vector2 tip, out _);
                DrawTip(dl, tip, px, alpha, 0.7f * (1f - settle));
            }
        }
    }

    /// <summary>Fills <see cref="_path"/> and <see cref="_arc"/> for this chain and returns total path length in pixels.</summary>
    private float BuildPath(in Blueprint bp, Vector2 screenSize, float time, float age)
    {
        Vector2 start = bp.Start   * screenSize;
        Vector2 end   = bp.End     * screenSize;
        Vector2 ctrl  = bp.Control * screenSize;
        float   sagBase = bp.Sag * screenSize.Y;

        // Steady sway ramps in over the settle window. Its angular frequency straddles the ground
        // pulse's, so the two motions read as one weighted breath rather than two independent wobbles.
        float settle    = Saturate((age - SettleStart) / (SettleEnd - SettleStart));
        float swayAmp   = screenSize.Y * 0.014f * settle;
        float swayPhase = DrawHelpers.HashRange(bp.Seed,     0f, Tau);
        float swaySpeed = DrawHelpers.HashRange(bp.Seed + 1, 2.4f, 3.2f);

        for (int i = 0; i < ChainSamples; i++)
        {
            float t  = i / (float)(ChainSamples - 1);
            float mt = 1f - t;

            Vector2 p = mt * mt * start + 2f * mt * t * ctrl + t * t * end;

            float shape = 4f * t * (1f - t); // 0 at both ends, 1 at the middle
            p.Y += sagBase * shape;
            p.Y += swayAmp * MathF.Sin(time * swaySpeed + t * 3.2f + swayPhase) * shape;

            _path[i] = p;
        }

        _arc[0] = 0f;
        for (int i = 1; i < ChainSamples; i++)
            _arc[i] = _arc[i - 1] + Vector2.Distance(_path[i - 1], _path[i]);
        return _arc[ChainSamples - 1];
    }

    /// <summary>
    /// Position and unit tangent at arc length <paramref name="s"/> along the current path. Values
    /// outside [0, totalLength] are handled by linear extrapolation along the endpoint tangent, so
    /// links can be placed past the endpoints and the chain reads as continuing off-screen.
    /// </summary>
    private void GetPathPoint(float s, float totalLength, out Vector2 pos, out Vector2 tangent)
    {
        // Before the start: extend backward along the initial segment's direction.
        if (s <= 0f)
        {
            Vector2 d0 = _path[1] - _path[0];
            tangent = d0.LengthSquared() > 1e-5f ? Vector2.Normalize(d0) : new Vector2(0f, -1f);
            pos = _path[0] + tangent * s;
            return;
        }

        // Past the end: extend forward along the final segment's direction.
        if (s >= totalLength)
        {
            Vector2 dN = _path[ChainSamples - 1] - _path[ChainSamples - 2];
            tangent = dN.LengthSquared() > 1e-5f ? Vector2.Normalize(dN) : new Vector2(0f, -1f);
            pos = _path[ChainSamples - 1] + tangent * (s - totalLength);
            return;
        }

        int lo = 0, hi = ChainSamples - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) >> 1;
            if (_arc[mid] <= s) lo = mid; else hi = mid;
        }

        float segLen = _arc[hi] - _arc[lo];
        float f      = segLen > 1e-5f ? (s - _arc[lo]) / segLen : 0f;
        pos = Vector2.Lerp(_path[lo], _path[hi], f);

        Vector2 d = _path[hi] - _path[lo];
        tangent = d.LengthSquared() > 1e-5f ? Vector2.Normalize(d) : new Vector2(0f, -1f);
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
    ///
    /// The flat link's stroke widths are tuned so the visible grey band is roughly 3px thick per side
    /// at 1080p, which reads as a substantial piece of metal rather than a thin wire. The silhouette
    /// (ink) is the widest stroke and defines the outer AND inner black outline; because the same
    /// stroke wraps the whole path, the interior black edge is automatically the same thickness as
    /// the exterior one. The mid-tone rail sits on top and is the lightest, brightest part.
    /// </summary>
    private void DrawLink(ImDrawListPtr dl, Vector2 center, Vector2 tangent,
                          float halfLen, float halfWidth, float px, float alpha, bool filled)
    {
        int count = BuildStadium(center, tangent, halfLen, halfWidth);
        ref Vector2 first = ref _linkPts[0];

        uint ink = DrawHelpers.WithAlpha(_ink,  0.70f * alpha);
        uint drk = DrawHelpers.WithAlpha(_dark, 0.95f * alpha);
        uint mid = DrawHelpers.WithAlpha(_mid,  0.95f * alpha);

        if (filled)
        {
            // Solid iron side of a link, then a thin dark rim so its silhouette stays crisp even
            // against the dark fill of an adjacent edge link.
            dl.AddConvexPolyFilled(ref first, count, drk);
            dl.AddPolyline(ref first, count, ink, ImDrawFlags.None, 1.8f * px);
        }
        else
        {
            // Face-on link: three layered strokes on the closed stadium path. Because each stroke is
            // centred on the path, the visible band is symmetric - the same amount grows outward as
            // inward - which is what keeps the exterior and interior black outlines the same width.
            //
            //   ink (widest)  : dark silhouette; the part not covered by the strokes on top of it
            //                   is the black outline on both the outside and the inside of the loop.
            //   drk (middle)  : dark grey band; the bulk of the visible metal.
            //   mid (narrowest): light grey rail down the centre of the band.
            //
            // The three widths together set how much of the loop's interior is left transparent.
            // Wider -> thicker-looking chain link, smaller hole.
            dl.AddPolyline(ref first, count, ink, ImDrawFlags.None, 11.0f * px);
            dl.AddPolyline(ref first, count, drk, ImDrawFlags.None,  8.5f * px);
            dl.AddPolyline(ref first, count, mid, ImDrawFlags.None,  2.4f * px);
        }
    }

    /// <summary>A hot flare at the growing tip: three stacked discs so it reads as a spark, not a lollipop.</summary>
    private void DrawTip(ImDrawListPtr dl, Vector2 p, float px, float alpha, float strength)
    {
        if (strength <= 0f) return;
        uint glow = DrawHelpers.WithAlpha(_glow,      0.55f * strength * alpha);
        uint hot  = DrawHelpers.WithAlpha(_highlight, 0.95f * strength * alpha);
        uint core = DrawHelpers.WithAlpha(0xFFFFFFFFu, 0.90f * strength * alpha);

        dl.AddCircleFilled(p, 14f * px, glow);
        dl.AddCircleFilled(p,  5f * px, hot);
        dl.AddCircleFilled(p,  2f * px, core);
    }

    /// <summary>
    /// The darkening along the bottom edge that the chains are pulling against. A single smoothed,
    /// capped gradient (not a flicker) so the whole-area element never strobes; it pulses on the
    /// same clock as the sway, and fades in over the cast-in.
    /// </summary>
    private void DrawGround(ImDrawListPtr dl, Vector2 screenSize, float alpha, float time, float age)
    {
        float castIn = Saturate(age / 0.6f);
        float pulse  = DrawHelpers.Pulse(time, 2.2f);
        float depth  = screenSize.Y * (0.14f + 0.035f * pulse) * castIn;
        if (depth <= 1f) return;

        uint dark  = DrawHelpers.WithAlpha(_ink, 0.70f * alpha);
        uint clear = DrawHelpers.WithAlpha(_ink, 0f);

        dl.AddRectFilledMultiColor(
            new Vector2(0f, screenSize.Y - depth),
            screenSize,
            clear, clear, dark, dark);
    }

    // =====================================================================================
    // Palette + small helpers
    // =====================================================================================

    private void EnsurePalette()
    {
        if (_paletteReady) return;
        _ink       = DrawHelpers.ToU32(0.03f, 0.03f, 0.04f, 1f); // near-black silhouette / ground darkening / link rims
        _dark      = DrawHelpers.ToU32(0.24f, 0.24f, 0.24f, 1f); // iron body (fill of edge-on links, dark grey band of flat loops)
        _mid       = DrawHelpers.ToU32(0.34f, 0.34f, 0.34f, 1f); // mid-tone rail down the centre of a flat loop
        _highlight = DrawHelpers.ToU32(0.86f, 0.90f, 0.95f, 1f); // pale specular glint / hot tip
        _glow      = DrawHelpers.ToU32(0.85f, 0.82f, 0.75f, 1f); // warm off-white halo around the tip
        _paletteReady = true;
    }

    private static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    private static float EaseOutCubic(float t)
    {
        float u = 1f - Saturate(t);
        return 1f - u * u * u;
    }
}