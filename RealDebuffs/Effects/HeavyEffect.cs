using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Heavy: iron chains are shot across the screen like arrows trailing a cable: a near-straight line
/// that crosses in a fraction of a second, then goes slack and hangs under its own weight.
///
/// MOTION. The shot is kinematic (a taut, almost straight cable paid out behind a fast head), so it
/// cannot fold. At the far anchor the chain is released into VerletStrand physics: the slack drops
/// out of it, a shock rings down it, and from then on wind sways it.
///
/// CHAINS and IMPACTS are hero slots: this effect owns layout, choreography and mood; the material
/// answers what a chain looks like and what flies off it. "heavy made of rope" swaps stroke.chain
/// for a rope and gets the same physics throwing fibres instead of sparks.
///
/// Everything is reseeded from the cast start time: chain count (4-5), anchors, slack, near/far,
/// shot timing and wind phase, plus each chain's metal and twist inside the material.
/// </summary>
public sealed class HeavyEffect : ISceneEffect, IHasHeroSlots, IHasSwappableSlots
{
    public DebuffKind Kind => DebuffKind.Heavy;
    public string DisplayName => "Heavy";
    public string Description => "Iron chains are flung across the screen and snap taut, then strain under their own weight.";
    public int DrawOrder => 2;

    public IReadOnlyDictionary<string, float> TriggerStatuses { get; } = new Dictionary<string, float>
    {
        ["Heavy"] = 1.0f,
    };

    public IReadOnlyList<string> TriggerKeywords { get; } = new[]
    {
        "chains", "shackle", "shackled"
    };

    public EffectHeroSlot[] HeroSlots { get; } = new EffectHeroSlot[]
    {
        new("Stroke", PrimitiveRole.MainStroke),
    };

    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke, "Chains", "stroke.chain"),
        new("Particle", PrimitiveRole.Dust,       "Dust",   "particle.dust"),
        new("Region",   PrimitiveRole.MainStroke, "Gloom",  "region.edge-glow", "EdgeGlow"),
    };

    // ---- layout ----
    private const int MaxChains = 5;
    private const int StrandNodes = 16;
    private const int PathSamples = 56;

    // ---- mood (0 removes it) ----
    private const float VignetteAlpha = 0.12f;
    private const float GloomAlpha = 0.28f;
    private const float GloomDepth = 0.12f;       // fraction of screen height

    // ---- the shot ----
    private const float MaxThrowDelay = 0.3f;
    private const float FlightBow = 0.25f;        // how far the head may curve off the straight line (scales ArcSide)
    private const float FlightSag = 0.0012f;      // spare length while flying, as a fraction of the chord: a taut cable
    private const float ReleaseSeconds = 0.18f;   // after landing, the slack comes back over this long
    private const float HangDrag = 1.85f;

    // ---- debris shows only this close to a screen edge (px at 1080p) ----
    private const float EmitEdgeReach = 190f;

    // ---- wind: a slow swell per chain plus gusts, so no chain is ever still ----
    private const float SwayAmp = 15f;             // px at 1080p, mid-span; the swell and gusts scale it
    private const float SwayGustHz = 0.30f;

    private static readonly uint Gloom = DrawHelpers.ToU32(0.012f, 0.016f, 0.024f, 1f);
    private static readonly uint Vignette = DrawHelpers.ToU32(0.010f, 0.014f, 0.022f, 1f);

    /// <summary>One chain: its anchors, its strand, and its shot state.</summary>
    private sealed class Rig
    {
        public readonly VerletStrand Strand = new(StrandNodes);
        public readonly StrandPath Path = new(PathSamples);

        public Vector2 Start, End;
        public float   Slack;              // spare length as a fraction of the straight-line distance
        public float   Delay, Flight;      // seconds (Flight is set from the distance and a throw speed)
        public float   ArcSide;            // signed; which way the head bows, and which side a vertical span sags to
        public float   LinkFrac;           // link length as a fraction of the short side
        public float   Depth;              // 0 near .. 1 far
        public int     Seed;

        public bool    Launched, Landed;
        public float   LandTime;
        public float   Agitation;
        public float   SwayPhase;
        public float   SwayHz;             // the chain's own swell frequency
    }

    private readonly Rig[] _rigs = new Rig[MaxChains];
    private readonly int[] _drawOrder = new int[MaxChains];
    private int _count;

    private readonly CastTracker _cast = new();

    public HeavyEffect()
    {
        for (int i = 0; i < MaxChains; i++) _rigs[i] = new Rig();
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;

        if (_cast.Begin(time))
            BuildLayout(unchecked((int)(_cast.Start * 1000f)), screenSize);
        float age = time - _cast.Start;
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        float px = shortSide / 1080f;

        EmitAtmosphere(scene, screenSize, alpha, time, age, colorOverride);

        // Far chains first so near ones overlap them.
        for (int n = 0; n < _count; n++)
        {
            var rig = _rigs[_drawOrder[n]];
            if (age < rig.Delay) continue;

            Step(scene, rig, screenSize, px, alpha, time, dt, age, colorOverride);

            if (rig.Path.Count < 2) continue;

            scene.AddStroke(new StrokePrimitive
            {
                Path = rig.Path,
                Role = PrimitiveRole.MainStroke,
                Reveal = 1f,
                WidthHint = shortSide * rig.LinkFrac,
                Brightness = alpha,
                Seed = rig.Seed,
                Phase = DrawHelpers.HashRange(rig.Seed + 999, 0f, MathF.Tau),
                Depth = rig.Depth,
                Agitation = rig.Agitation,
                EmitEdgeReach = EmitEdgeReach,
                ColorOverride = colorOverride,
            });
        }
    }

    // ---- Atmosphere ----

    private static void EmitAtmosphere(EffectScene scene, Vector2 size, float alpha, float time, float age, Vector4? colorOverride)
    {
        float castIn = DrawHelpers.Saturate(age / 0.7f);

        if (VignetteAlpha > 0f)
            scene.RequestVignette(Vignette, 0.12f, alpha * VignetteAlpha * castIn, priority: 20, colorOverride);

        // A gloom pooled at the bottom edge that breathes slowly, as if the weight were shifting.
        float depth = size.Y * GloomDepth * (1f + 0.15f * DrawHelpers.Pulse(time, 4.6f)) * castIn;
        if (GloomAlpha > 0f && depth > 1f)
        {
            scene.AddRegion(new RegionPrimitive
            {
                Min = new Vector2(0f, size.Y - depth),
                Max = size,
                Tint = Gloom,
                Alpha = GloomAlpha * alpha,
                Bottom = true,
                ColorOverride = colorOverride,
            });
        }
    }

    // ---- Per-chain simulation ----

    private void Step(EffectScene scene, Rig rig, Vector2 size, float px,
                      float alpha, float time, float dt, float age, Vector4? colorOverride)
    {
        var strand = rig.Strand;
        float t = age - rig.Delay;

        if (!rig.Launched)
        {
            rig.Launched = true;
            strand.Reset(rig.Start);
            strand.Gravity = new Vector2(0f, 2500f * px);
            strand.Drag = HangDrag;
            strand.Iterations = 12;
            strand.BendStiffness = 0.75f;
        }

        // ---- the head: an arrow. Constant speed after a short launch ramp, stopped dead by the far anchor ----
        float tau = DrawHelpers.Saturate(t / rig.Flight);
        Vector2 chord = rig.End - rig.Start;
        float chordLen = chord.Length();
        Vector2 dir = chordLen > 1e-3f ? chord / chordLen : new Vector2(1f, 0f);
        Vector2 perp = new(-dir.Y, dir.X);

        float travel = (tau < 0.1f ? 5f * tau * tau : tau - 0.05f) / 0.95f;
        float bow = rig.ArcSide * FlightBow * chordLen * MathF.Sin(MathF.PI * tau);
        Vector2 head = rig.Start + chord * travel + perp * bow;

        bool landedNow = false;
        if (tau >= 1f && !rig.Landed)
        {
            rig.Landed = true;
            landedNow = true;
            rig.LandTime = time;
            rig.Agitation = 1f;
        }

        if (rig.Landed)
        {
            head = rig.End;
            // The load on the far end eases and settles: slow, and mostly off-screen.
            head.Y += 5.5f * px * MathF.Sin(time * 0.85f + rig.SwayPhase);
        }

        // ---- Length and the guide must agree by construction. If Length jumps to the full
        // value while the guide still has only the flight's tiny extra, the link constraint -
        // which pins both ends - has to bulge the strand sideways to make up the difference,
        // and that bulge reads as a sudden fall faster than gravity. Grow both together over
        // ReleaseSeconds instead. ----
        float flightExtra = Vector2.Distance(head, rig.Start) * FlightSag;
        float extra = flightExtra;
        if (rig.Landed)
        {
            float k = DrawHelpers.Saturate((time - rig.LandTime) / ReleaseSeconds);
            float release = k * k * (3f - 2f * k);
            float targetExtra = chordLen * rig.Slack;
            extra = flightExtra + (targetExtra - flightExtra) * release;
        }

        strand.Length = MathF.Max(2f, Vector2.Distance(head, rig.Start) + extra);
        GuideShape(rig, head, extra);
        strand.SetEnds(rig.Start, head);

        if (!rig.Landed)
        {
            // In flight: placed on the guide, not simulated. The guide's frame-to-frame motion
            // becomes the strand's velocity, so arrival at the far anchor carries the head's
            // full speed.
            strand.Drive(strand.Target, dt);
        }
        else
        {
            // Post-impact: fully simulated. The strand keeps the flight velocity it acquired on
            // the last Drive and is then left to gravity and the link constraints, so it carries
            // forward past the anchor and clumps up against the mount before swinging back into
            // a hanging pose. Nothing pulls it toward a scripted drape anymore.
            strand.Step(dt);
        }
        strand.FillPath(rig.Path, PathSamples);
        if (rig.Landed) ApplySway(rig, time, px);

        // After Drive (which overwrites every node's velocity) and after the path is published, so
        // the impact point is found on this frame's chain.
        if (landedNow)
        {
            Shock(rig, px, strength: 0.1f, time);
            ReportImpact(scene, rig, size, strength: 1f, alpha, time, colorOverride);
        }

        rig.Agitation *= MathF.Exp(-dt / 0.95f);
    }

    /// <summary>
    /// Wind: a slow swell plus gusts displace the published path sideways, delayed along its length
    /// so they travel down it, and zero at the anchors. It is applied to the path after the
    /// simulation rather than as a force: a simulated chain's resistance to sideways pushes varies
    /// several-fold with its sag and tension, so a force gives some chains a swing and others none,
    /// whereas this moves every chain by the same amount and layers on the landing ring-down.
    /// Fades in as the landing settles.
    /// </summary>
    private static void ApplySway(Rig rig, float time, float px)
    {
        var path = rig.Path;
        int n = path.Count;
        float amp = SwayAmp * px * DrawHelpers.Saturate((time - rig.LandTime) / 1.2f);
        if (amp <= 0.01f || n < 3) return;

        Span<Vector2> offset = stackalloc Vector2[PathSamples];
        for (int i = 0; i < n; i++)
        {
            Vector2 tan = path.Points[Math.Min(n - 1, i + 1)] - path.Points[Math.Max(0, i - 1)];
            float len = tan.Length();
            if (len < 1e-3f) { offset[i] = default; continue; }

            float u = i / (n - 1f);
            float swell = MathF.Sin(MathF.Tau * rig.SwayHz * time + rig.SwayPhase - u * 1.6f);
            float gust = FireNoise.Value(time * SwayGustHz + rig.SwayPhase - u * 1.2f, rig.Seed * 0.013f);
            offset[i] = new Vector2(-tan.Y, tan.X) / len * (amp * (0.6f * swell + 0.7f * gust) * MathF.Sin(MathF.PI * u));
        }

        for (int i = 0; i < n; i++) path.Points[i] += offset[i];
        path.BuildArc();
    }

    /// <summary>
    /// Fills the strand's guide with the drape a strand with <paramref name="extra"/> px of spare
    /// length would take between its ends: a parabola sagging downhill, whose arc length is exactly
    /// chord + extra. Used only in flight (the caller hands <c>extra = distance * FlightSag</c>,
    /// so it lays the chain out as a very slightly sagging taut cable). A near-vertical span has
    /// no downhill side, so it bows to whichever side this chain was seeded with.
    /// </summary>
    private static void GuideShape(Rig rig, Vector2 head, float extra)
    {
        const int Samples = 48;

        Vector2 chord = head - rig.Start;
        float c = chord.Length();
        Vector2 dir = c > 1e-3f ? chord / c : new Vector2(1f, 0f);
        Vector2 perp = new(-dir.Y, dir.X);

        float side = perp.Y >= 0f ? 1f : -1f;
        if (MathF.Abs(perp.Y) < 0.30f) side = rig.ArcSide >= 0f ? 1f : -1f;
        perp *= side;

        float sag = MathF.Sqrt(3f * c * extra / 8f);          // small-sag estimate

        Span<Vector2> pts = stackalloc Vector2[Samples + 1];
        Span<float> cum = stackalloc float[Samples + 1];

        // Refine the sag until the parabola's true arc length matches: the sag grows with the
        // square root of the spare length, so a couple of secant steps converge.
        for (int pass = 0; pass < 3; pass++)
        {
            Trace(rig.Start, chord, perp, sag, pts, cum);
            float got = cum[Samples] - c;
            if (extra < 1e-3f || got < 1e-3f) break;
            sag *= MathF.Sqrt(extra / got);
        }
        Trace(rig.Start, chord, perp, sag, pts, cum);

        // Equal arc-length spacing, to match the strand's equal-length links.
        var target = rig.Strand.Target;
        float total = cum[Samples];
        int seg = 0;
        for (int i = 0; i < StrandNodes; i++)
        {
            float want = total * i / (StrandNodes - 1f);
            while (seg < Samples - 1 && cum[seg + 1] < want) seg++;
            float span = cum[seg + 1] - cum[seg];
            float f = span > 1e-4f ? (want - cum[seg]) / span : 0f;
            target[i] = Vector2.Lerp(pts[seg], pts[seg + 1], f);

        }
    }

    private static void Trace(Vector2 start, Vector2 chord, Vector2 perp, float sag, Span<Vector2> pts, Span<float> cum)
    {
        int n = pts.Length - 1;
        for (int j = 0; j <= n; j++)
        {
            float f = j / (float)n;
            pts[j] = start + chord * f + perp * (4f * sag * f * (1f - f));
            cum[j] = j == 0 ? 0f : cum[j - 1] + Vector2.Distance(pts[j], pts[j - 1]);
        }
    }

    /// <summary>The head reaches the far anchor: a transverse shock runs down the chain.</summary>
    private void Shock(Rig rig, float px, float strength, float time)
    {
        var strand = rig.Strand;

        // A hard stop sends a TRANSVERSE shock down a taut chain. Never push along the chain: a chain
        // carries tension but not compression, so a push along it buckles into a loop, which is the
        // one thing this effect must not do.
        Vector2 along = Vector2.Normalize(strand.Pos[StrandNodes - 1] - strand.Pos[StrandNodes - 4]);
        if (float.IsNaN(along.X)) along = new Vector2(1f, 0f);
        Vector2 side = new(-along.Y, along.X);
        float sign = DrawHelpers.Hash01(rig.Seed + 710 + (int)(time * 10f)) < 0.5f ? -1f : 1f;
        float v = 520f * px * strength * (1f - 0.35f * rig.Depth);

        // One smooth bump of sideways velocity, strongest a few links in from the stop and fading out
        // along the chain. (Alternating signs on neighbouring nodes would fold the end into a zigzag.)
        const int Reach = 6;
        for (int k = 1; k <= Reach; k++)
        {
            float bump = MathF.Sin(MathF.PI * k / (Reach + 1f));
            strand.Impulse(StrandNodes - 1 - k, side * (v * bump * sign));
        }
    }

    /// <summary>
    /// Tells the framework the chain just hit the edge it hangs from. The impact point is where the
    /// chain leaves the screen; debris is thrown back along the chain and into the screen.
    /// </summary>
    private void ReportImpact(EffectScene scene, Rig rig, Vector2 size, float strength,
                              float alpha, float time, Vector4? colorOverride)
    {
        if (!ScreenEdges.TryFindExit(rig.Path, size, out Vector2 point, out Vector2 outward)) return;

        Vector2 inward = ScreenEdges.Inward(ScreenEdges.Nearest(size, point));
        Vector2 dir = -outward * 0.65f + inward * 0.5f;
        dir = dir.LengthSquared() > 1e-4f ? Vector2.Normalize(dir) : inward;

        scene.AddImpact(new ImpactPrimitive
        {
            StrokeRole = PrimitiveRole.MainStroke,
            Position = point,
            Direction = dir,
            Strength = Math.Clamp(strength * (1f - 0.4f * rig.Depth), 0f, 1f),
            Brightness = alpha,
            Seed = unchecked(rig.Seed + (int)(time * 1000f)),
            ColorOverride = colorOverride,
        });
    }

    // ---- Layout ----

    /// <summary>
    /// Lays out this application's chains. Anchors are chosen from a handful of archetypes (dropped
    /// from the top, hauled from the bottom, spanning side to side, or any two points on the border)
    /// in a shuffled order, so every cast has a different composition but never a lopsided one.
    /// Roughly a quarter are far away: smaller links, hazier, behind the rest.
    /// </summary>
    private void BuildLayout(int castSeed, Vector2 size)
    {
        float shortSide = MathF.Min(size.X, size.Y);
        float overhang = shortSide * 0.07f;

        _count = Math.Min(MaxChains, 4 + (int)(DrawHelpers.Hash01(castSeed + 1) * 2f));    // 4 or 5

        Span<int> archetype = stackalloc int[MaxChains] { 0, 1, 2, 3, 0 };
        for (int i = MaxChains - 1; i > 0; i--)
        {
            int j = Math.Min(i, (int)(DrawHelpers.Hash01(castSeed + 100 + i) * (i + 1)));
            (archetype[i], archetype[j]) = (archetype[j], archetype[i]);
        }

        for (int i = 0; i < _count; i++)
            BuildRig(_rigs[i], i, archetype[i], castSeed, size, overhang);

        // Paint order: far (large Depth) first.
        for (int i = 0; i < _count; i++) _drawOrder[i] = i;
        Array.Sort(_drawOrder, 0, _count, Comparer<int>.Create((a, b) => _rigs[b].Depth.CompareTo(_rigs[a].Depth)));
    }

    private static void BuildRig(Rig r, int index, int archetype, int castSeed, Vector2 size, float overhang)
    {
        int s = unchecked(castSeed + index * 977 + 17);
        bool mirror = DrawHelpers.Hash01(s) < 0.5f;
        float H(int salt, float lo, float hi) => DrawHelpers.HashRange(s + salt, lo, hi);

        Vector2 a, b;
        switch (archetype)
        {
            case 0:   // dropped from the top, ends low on a side
                a = ScreenEdges.Anchor(size, ScreenEdge.Top, H(1, 0.12f, 0.88f), overhang);
                b = ScreenEdges.Anchor(size, mirror ? ScreenEdge.Left : ScreenEdge.Right, H(2, 0.42f, 0.95f), overhang);
                break;
            case 1:   // hauled up from the bottom, ends high on a side
                a = ScreenEdges.Anchor(size, ScreenEdge.Bottom, H(1, 0.10f, 0.90f), overhang);
                b = ScreenEdges.Anchor(size, mirror ? ScreenEdge.Left : ScreenEdge.Right, H(2, 0.05f, 0.58f), overhang);
                break;
            case 2:   // side to side
                a = ScreenEdges.Anchor(size, ScreenEdge.Left, H(1, 0.08f, 0.50f), overhang);
                b = ScreenEdges.Anchor(size, ScreenEdge.Right, H(2, 0.50f, 0.94f), overhang);
                if (mirror) (a, b) = (b, a);
                break;
            default:  // any two points on the border, at least a full edge apart
            {
                float p = H(1, 0f, 4f);
                a = ScreenEdges.FromPerimeter(size, p, overhang, out _);
                b = ScreenEdges.FromPerimeter(size, p + H(2, 1.0f, 3.0f), overhang, out _);
                break;
            }
        }

        bool far = index == 2;   // one far chain per cast, in the middle of the paint order

        r.Start = a;
        r.End = b;
        r.Seed = s;
        r.Depth = far ? H(3, 0.55f, 0.90f) : H(3, 0.00f, 0.18f);
        r.LinkFrac = far ? H(4, 0.034f, 0.044f) : H(4, 0.052f, 0.072f);
        float slack = far ? H(5, 0.04f, 0.09f) : H(5, 0.03f, 0.13f);
        // Spare length has to go somewhere. Across a wide span it becomes a sag; on a steep one it has
        // nowhere sensible to go and a real chain would just hang straight. So steeper means taut.
        Vector2 span = b - a;
        float horizontal = span.LengthSquared() > 1f ? MathF.Abs(span.X) / span.Length() : 1f;
        r.Slack = slack * (0.30f + 0.70f * horizontal);
        r.Delay = H(6, 0f, MaxThrowDelay);
        // Speed is what's chosen, not flight time, so a long span isn't faster than a short one.
        float px = MathF.Min(size.X, size.Y) / 1080f;
        r.Flight = Math.Clamp(Vector2.Distance(a, b) / (H(7, 4300f, 6200f) * px), 0.20f, 0.50f);
        r.ArcSide = H(8, -0.07f, 0.07f);
        r.SwayPhase = H(9, 0f, MathF.Tau);
        r.SwayHz = H(10, 0.12f, 0.20f);

        r.Launched = false;
        r.Landed = false;
        r.Agitation = 0f;
        r.Path.Count = 0;
    }
}
