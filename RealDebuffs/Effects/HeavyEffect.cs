using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Heavy: iron chains flung in from the edges, whipped across, snapped taut with a shower of
/// sparks, then left hanging under their own weight.
///
/// MOTION. The throw is kinematic — each frame the chain is laid on the drape it would hang in
/// between its anchors, paid out from an off-screen anchor. A chain on a drape cannot fold.
/// The hang is simulated (VerletStrand); the stop sends a real shock down the chain, it rings
/// down, sways, and is occasionally yanked.
///
/// CHAINS and IMPACTS are hero slots. This effect owns layout, choreography, and mood; the
/// material answers what a chain looks like and what flies off it. "heavy made of rope" swaps
/// stroke.chain for a rope and gets the same physics throwing fibres instead of sparks.
///
/// Reseeds everything from the cast start time: chain count (4-5), anchors, slack, near/far,
/// throw timing, tug schedule, plus each chain's metal and twist inside the material.
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

    // ---- timing ----
    private const float MaxThrowDelay = 0.28f;

    // ---- the guide drape (see GuideShape) ----
    // The throw is kinematic: the strand is placed on its drape every frame. After the snap the
    // drape keeps pulling on it, strongly at first so the chain settles into place, then faintly,
    // just enough to keep the hanging pose coherent while it sways and rings.
    private const float SettleStiffness = 480f;
    private const float SettleSeconds = 0.25f;
    private const float HangStiffness = 24f;
    private const float HangDrag = 1.15f;

    private static readonly uint Gloom = DrawHelpers.ToU32(0.012f, 0.016f, 0.024f, 1f);
    private static readonly uint Vignette = DrawHelpers.ToU32(0.010f, 0.014f, 0.022f, 1f);

    /// <summary>One chain: its anchors, its simulated strand, and its throw/tug state.</summary>
    private sealed class Rig
    {
        public readonly VerletStrand Strand = new(StrandNodes);
        public readonly StrandPath Path = new(PathSamples);

        public Vector2 Start, End;
        public float   Slack;              // spare length as a fraction of the straight-line distance
        public float   Delay, Flight;      // seconds (Flight is set from the distance and a throw speed)
        public float   ArcSide;            // how far the head bows off the straight line, in chord lengths (signed)
        public float   LinkFrac;           // link length as a fraction of the short side
        public float   Depth;              // 0 near .. 1 far
        public int     Seed;

        public bool    Launched, Landed;
        public float   LandTime;
        public float   Agitation;
        public float   NextTug;
        public float   TugStart = -10f;
        public Vector2 TugVector;
        public float   SwayPhase;
    }

    private readonly Rig[] _rigs = new Rig[MaxChains];
    private readonly int[] _drawOrder = new int[MaxChains];
    private int _count;

    private readonly ParticleEmitter _motes = new(maxParticles: 44, seedSalt: 0x4EA7);
    private readonly Func<int, Vector2> _motePos;
    private readonly Func<int, Vector2> _moteVel;
    private Vector2 _screen;

    private readonly CastTracker _cast = new();

    public HeavyEffect()
    {
        for (int i = 0; i < MaxChains; i++) _rigs[i] = new Rig();

        // Built once so the draw loop allocates nothing.
        _motePos = seed => new Vector2(
            DrawHelpers.HashRange(seed + 20, 0f, _screen.X),
            DrawHelpers.HashRange(seed + 21, 0f, _screen.Y));
        _moteVel = seed => new Vector2(
            DrawHelpers.HashRange(seed + 22, -9f, 9f),
            DrawHelpers.HashRange(seed + 23, -6f, 7f));
        _motes.Wander = 14f;
        _motes.WanderHz = 0.22f;
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;
        _screen = screenSize;

        if (_cast.Begin(time))
        {
            BuildLayout(unchecked((int)(_cast.Start * 1000f)), screenSize);
            _motes.Clear();
        }
        float age = time - _cast.Start;
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        float px = shortSide / 1080f;

        EmitAtmosphere(scene, screenSize, shortSide, alpha, time, dt, age, colorOverride);

        // Far chains first so near ones overlap them.
        for (int n = 0; n < _count; n++)
        {
            var rig = _rigs[_drawOrder[n]];
            if (age < rig.Delay) continue;

            Step(scene, rig, screenSize, shortSide, px, alpha, time, dt, age, colorOverride);

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
                ColorOverride = colorOverride,
            });
        }
    }

    // ---- Atmosphere ----

    private void EmitAtmosphere(EffectScene scene, Vector2 size, float shortSide, float alpha,
                                float time, float dt, float age, Vector4? colorOverride)
    {
        float castIn = DrawHelpers.Saturate(age / 0.7f);

        // The whole frame is pressed down: a dark vignette, and a gloom pooled at the bottom edge
        // that breathes slowly, as if the weight were shifting.
        scene.RequestVignette(Vignette, 0.15f, alpha * 0.50f * castIn, priority: 20, colorOverride);

        float breathe = DrawHelpers.Pulse(time, 4.6f);
        float depth = size.Y * (0.17f + 0.03f * breathe) * castIn;
        if (depth > 1f)
        {
            scene.AddRegion(new RegionPrimitive
            {
                Min = new Vector2(0f, size.Y - depth),
                Max = size,
                Tint = Gloom,
                Alpha = 0.66f * alpha,
                Bottom = true,
                ColorOverride = colorOverride,
            });
        }

        // Dust hanging in the air: tiny motes, so the space between the chains has depth.
        float moteSize = shortSide * 0.0013f;
        _motes.Update(time, dt, 0.10f, 0.20f, _motePos, _moteVel,
                      lifespanMin: 4.5f, lifespanMax: 9f, sizeMin: moteSize, sizeMax: moteSize * 2.2f);
        _motes.Emit(scene, time, PrimitiveRole.Dust, brightnessMul: alpha * 0.85f * castIn,
                    colorOverride, swayPerParticle: 0f, variant: 1);
    }

    // ---- Per-chain simulation ----

    private void Step(EffectScene scene, Rig rig, Vector2 size, float shortSide, float px,
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

        // ---- the head's position: a ballistic throw, then pinned ----
        float tau = DrawHelpers.Saturate(t / rig.Flight);
        Vector2 chord = rig.End - rig.Start;
        float chordLen = chord.Length();
        Vector2 dir = chordLen > 1e-3f ? chord / chordLen : new Vector2(1f, 0f);
        Vector2 perp = new(-dir.Y, dir.X);

        // Eases out of the throw and into the stop, with a bias toward arriving fast. A pure ease-in
        // makes the head outrun a 16-node chain and the slack ties itself in a whip loop.
        float travel = 0.60f * (tau * tau * (3f - 2f * tau)) + 0.40f * MathF.Pow(tau, 1.6f);
        float bow = rig.ArcSide * chordLen * MathF.Sin(MathF.PI * tau) * (1f - tau * 0.6f);
        Vector2 head = rig.Start + chord * travel + perp * bow;

        // ---- landing ----
        bool landedNow = false;
        if (tau >= 1f && !rig.Landed)
        {
            rig.Landed = true;
            landedNow = true;
            rig.LandTime = time;
            rig.Agitation = 1f;
            rig.NextTug = time + 2.0f + 3.2f * DrawHelpers.Hash01(rig.Seed + 700);
        }

        // ---- the strand is paid out as the head travels ----
        if (!rig.Landed)
        {
            strand.Length = MathF.Max(2f, Vector2.Distance(head, rig.Start) * (1f + rig.Slack));
        }
        else
        {
            strand.Length = chordLen * (1f + rig.Slack);
            head = rig.End;
            Strain(scene, rig, size, px, alpha, time, colorOverride);
            head += TugOffset(rig, time, px);
            // A slow shift in how hard the far end pulls: the load easing and settling.
            head.Y += 5.5f * px * MathF.Sin(time * 0.85f + rig.SwayPhase);
        }

        // Faint, slow air current once the chain is hanging: a lateral push that travels down it.
        if (rig.Landed)
        {
            for (int i = 1; i < StrandNodes - 1; i++)
            {
                float ph = time * 1.05f + rig.SwayPhase + i * 0.42f;
                strand.Accel[i] = new Vector2(88f * px * MathF.Sin(ph), 40f * px * MathF.Sin(ph * 0.73f + 1.7f));
            }
        }

        GuideShape(rig, head, strand.Length);
        strand.SetEnds(rig.Start, head);

        if (!rig.Landed || landedNow)
        {
            // In the air: placed on the drape, not simulated. The landing frame is placed too; Drive
            // hands the simulation the velocity the chain had, and the shock below adds to it.
            strand.Drive(strand.Target, dt);
        }
        else
        {
            strand.ShapeStiffness = HangStiffness + SettleStiffness * MathF.Exp(-(time - rig.LandTime) / SettleSeconds);
            strand.Step(dt);
        }
        strand.FillPath(rig.Path, PathSamples);

        // The snap goes in AFTER the strand is placed (Drive overwrites every node's velocity), and
        // after the path is published so the impact point is found on this frame's chain.
        if (landedNow)
        {
            Shock(rig, px, strength: 1f, time);
            ReportImpact(scene, rig, size, strength: 1f, alpha, time, colorOverride);
        }

        rig.Agitation *= MathF.Exp(-dt / 0.95f);
    }

    /// <summary>
    /// Fills the strand's guide with the drape a strand of this length would take between its
    /// anchors: a parabola sagging downhill, whose arc length is exactly the strand's length, with
    /// the nodes spaced at equal arc length along it. Matching the length and the spacing matters:
    /// a guide that disagrees with the strand's own constraints makes the spring and the links
    /// fight, and the slack they can't agree on ends up as loops. A near-vertical span has no
    /// downhill side, so it bows to whichever side this chain was seeded with.
    /// </summary>
    private static void GuideShape(Rig rig, Vector2 head, float length)
    {
        const int Samples = 48;

        Vector2 chord = head - rig.Start;
        float c = chord.Length();
        Vector2 dir = c > 1e-3f ? chord / c : new Vector2(1f, 0f);
        Vector2 perp = new(-dir.Y, dir.X);

        float side = perp.Y >= 0f ? 1f : -1f;
        if (MathF.Abs(perp.Y) < 0.30f) side = rig.ArcSide >= 0f ? 1f : -1f;
        perp *= side;

        float extra = MathF.Max(0f, length - c);
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

    /// <summary>Schedules and fires the occasional yank on a hanging chain.</summary>
    private void Strain(EffectScene scene, Rig rig, Vector2 size, float px, float alpha, float time, Vector4? colorOverride)
    {
        if (time < rig.NextTug) return;

        int s = unchecked(rig.Seed + (int)(time * 100f));
        rig.TugStart = time;

        // The yank is toward the far anchor: the end is dragged outward and then released.
        Vector2 outward = rig.End - rig.Strand.Pos[StrandNodes - 2];
        outward = outward.LengthSquared() > 1e-4f ? Vector2.Normalize(outward) : new Vector2(0f, 1f);
        rig.TugVector = outward * (DrawHelpers.HashRange(s + 1, 9f, 24f) * px);

        rig.Agitation = MathF.Max(rig.Agitation, 0.75f);
        rig.NextTug = time + 3.4f + 4.6f * DrawHelpers.Hash01(s + 2);

        ReportImpact(scene, rig, size, strength: 0.38f, alpha, time, colorOverride);
    }

    /// <summary>How far the end is currently displaced by a yank: a quick pull out and a slower release.</summary>
    private static Vector2 TugOffset(Rig rig, float time, float px)
    {
        float x = (time - rig.TugStart) / 0.42f;
        if (x < 0f || x > 1f) return Vector2.Zero;
        float env = x < 0.30f ? x / 0.30f : MathF.Pow(1f - (x - 0.30f) / 0.70f, 1.6f);
        return rig.TugVector * env;
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
        // Throw speed is bounded, not flight time: a head moving faster than the strand can follow
        // stretches it and leaves the slack in a tangle at the far end.
        float px = MathF.Min(size.X, size.Y) / 1080f;
        r.Flight = Math.Clamp(Vector2.Distance(a, b) / (H(7, 3000f, 4200f) * px), 0.34f, 0.70f);
        r.ArcSide = H(8, -0.07f, 0.07f);
        r.SwayPhase = H(9, 0f, MathF.Tau);

        r.Launched = false;
        r.Landed = false;
        r.Agitation = 0f;
        r.TugStart = -10f;
        r.Path.Count = 0;
    }
}
