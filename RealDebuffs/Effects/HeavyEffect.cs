using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace RealDebuffs.Effects;

/// <summary>
/// Heavy: a fan of massive chains slams out of the screen and locks across it, settling into a
/// slow, weighted sway while the ground darkens under their pull.
///
/// How it's built, since nothing is stored between frames:
///  - The cast layout is fixed at five chains: two anchor to the top edge (one in each half, running
///    down to a random point on the left / right edge), two anchor to the bottom edge the same way,
///    and one free chain whose two endpoints can be anywhere on the perimeter as long as they are at
///    least a quarter of the perimeter apart - so it can never collapse into a short hop along a
///    single edge. The layout is reseeded from the cast start time, so it is stable within one
///    application of the debuff and different the next time.
///  - Chains anchored to the bottom edge are flagged AnchorBottom so a skin that decorates before
///    the strand's base (see StrandVisual.FlushStart) skips it: without that, an extra face-on loop
///    could poke up past the bottom edge as the chain sways.
///  - Each chain's path is a quadratic bezier between its start, control and end points, plus a
///    parabolic sag (always positive Y, so the middle bows downward) and a slow travelling wave.
///    All coordinates are normalized and expanded to pixels fresh every frame, so the effect scales
///    cleanly with the window.
///  - The cast-in is pure timing. Each chain has a stagger delay; its reveal fraction is
///    EaseOutCubic(age - delay); a skin decides for itself how much of its own strand that reveals.
///    The growing tip carries a hot flare while it runs and a brief settle flash where it locks in -
///    both folded into one continuous 0..1 "tipFlare" value a skin renders in its own material.
///  - After the cast-in the whole fan sways on a travelling wave whose period matches the ground
///    pulse, so the effect breathes as one unit.
///
/// VISUALS: this class owns the chain's SHAPE only - how many there are, where they anchor, how
/// they sag/sway/settle. What a chain actually LOOKS like when drawn (the default interlocking
/// iron, or any other material) is a pluggable IStrandSkin - see IReskinnableEffect and the remarks
/// on StrandPath/IStrandSkin for why that split exists and how the two effects that currently use
/// it (this one and Bind) can swap materials with each other.
///
/// Nothing allocates per frame; scratch arrays are sized once at construction.
/// </summary>
public sealed class HeavyEffect : IScreenEffect, IReskinnableEffect
{
    public DebuffKind Kind => DebuffKind.Heavy;

    private StrandSkinKind _skinKind = StrandSkinKind.Chain;
    public StrandSkinKind SkinKind { set => _skinKind = value; }

    // ---- timing ----
    private const float NewCastGapSeconds   = 1.0f; // long enough that an ordinary frame hitch mid-fight never replays the cast-in
    private const float ChainExtendSeconds  = 0.55f;
    private const float SettleStart         = 0.55f;
    private const float SettleEnd           = 1.80f;
    private const float SettleFlashSeconds  = 0.30f;

    // ---- geometry ----
    private const int   ChainSamples    = 16;  // polyline resolution per chain (arc-length interpolated between these)
    private const int   ChainCount      = 5;   // 4 edge-anchored + 1 free
    private const float Tau             = MathF.PI * 2f;

    // The ground-darkening's color is Heavy's own scene mood, independent of whichever skin the
    // chains themselves are using - so it lives here, not in ChainSkin's palette.
    private static readonly uint GroundShade = DrawHelpers.ToU32(0.03f, 0.03f, 0.04f, 1f);

    private float _lastDrawTime = -100f;
    private float _castStart;

    // Shared per-frame scratch: rebuilt fresh and fully consumed within each single chain's
    // draw call, then reused for the next - same pattern the original single _path/_arc fields
    // followed, just generalized to the shared StrandPath type every skin understands.
    private readonly StrandPath _strandPath = new(ChainSamples);

    // Layout for the current cast, re-rolled each time the debuff is (re)applied.
    private readonly Blueprint[] _blueprints = new Blueprint[ChainCount];

    private readonly struct Blueprint
    {
        public readonly Vector2 Start, End, Control;
        public readonly float Sag;    // fraction of screen height added at the middle (always positive)
        public readonly float Delay;  // seconds after the cast-in starts that this chain begins extending
        public readonly float Scale;  // strand-size multiplier, for depth between chains
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

        float minDim   = MathF.Min(screenSize.X, screenSize.Y);
        float px       = Math.Clamp(minDim / 1080f, 0.75f, 2.4f);
        float linkBase = minDim * 0.044f;

        DrawGround(dl, screenSize, alpha, time, age);

        var skin = StrandSkins.Get(_skinKind);
        for (int i = 0; i < ChainCount; i++)
            DrawChain(dl, skin, in _blueprints[i], screenSize, px, linkBase, alpha, time, age);
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
    // One chain: build its path, work out this frame's reveal/flourish, hand off to the skin
    // =====================================================================================

    private void DrawChain(ImDrawListPtr dl, IStrandSkin skin, in Blueprint bp, Vector2 screenSize,
                           float px, float linkBase, float alpha, float time, float age)
    {
        BuildPath(in bp, screenSize, time, age);

        float gt = Saturate((age - bp.Delay) / ChainExtendSeconds);
        float reveal = EaseOutCubic(gt);

        // Growing-tip flare while still extending, then a brief settle flash where it locks in -
        // folded into one continuous 0..1 knob so any skin can render its own flavor of flourish.
        float tipFlare;
        if (gt > 0.001f && gt < 1f)
        {
            tipFlare = 1f - gt;
        }
        else if (gt >= 1f)
        {
            float settle = (age - bp.Delay - ChainExtendSeconds) / SettleFlashSeconds;
            tipFlare = (settle >= 0f && settle < 1f) ? 0.7f * (1f - settle) : 0f;
        }
        else
        {
            tipFlare = 0f;
        }

        var visual = new StrandVisual
        {
            Thickness  = linkBase * bp.Scale,
            Seed       = bp.Seed,
            Phase      = DrawHelpers.HashRange(bp.Seed + 999, 0f, Tau),
            FlushStart = bp.AnchorBottom,
        };

        skin.DrawStrand(dl, _strandPath, in visual, reveal, tipFlare, alpha, px, time, out _, out _);
    }

    /// <summary>Fills <see cref="_strandPath"/> for this chain (points + arc-length table).</summary>
    private void BuildPath(in Blueprint bp, Vector2 screenSize, float time, float age)
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

            _strandPath.Points[i] = p;
        }

        _strandPath.Count = ChainSamples;
        // No latch-style in-place mutation happens to a chain's path after this (unlike Bind's
        // tendrils), so the arc table can be built immediately, right here.
        _strandPath.BuildArc();
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

        uint dark  = DrawHelpers.WithAlpha(GroundShade, 0.70f * alpha);
        uint clear = DrawHelpers.WithAlpha(GroundShade, 0f);

        dl.AddRectFilledMultiColor(
            new Vector2(0f, screenSize.Y - depth),
            screenSize,
            clear, clear, dark, dark);
    }

    // =====================================================================================
    // Small helpers
    // =====================================================================================

    private static float Saturate(float x) => Math.Clamp(x, 0f, 1f);

    private static float EaseOutCubic(float t)
    {
        float u = 1f - Saturate(t);
        return 1f - u * u * u;
    }
}
