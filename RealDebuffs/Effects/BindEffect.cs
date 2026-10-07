using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Bind: ropes are flung across the screen, made fast on the far side, and hauled taut around you.
/// The inverse of Heavy. Heavy's chains arrive straight and fast, then drop slack and sway under
/// their own weight. Bind's ropes arrive slack, carrying all the weight of the loose line, swing
/// and settle, and only then is the pull the event: they come up out of their sag, straighten, and
/// stop dead, ringing like a plucked string. After that they hang taut and barely move.
///
/// BEATS (per rope, staggered):
///   THROW  The head flies an arc over the span, paying out a deep, loose sag behind it.
///   MAKE FAST  The head hits the far anchor, off-screen, with a soft thud of dust and fibres. It stops
///          dead; the slack does not. It carries on, swings, bunches, and settles under its own weight.
///   HAUL   Once it has settled the line is taken up: the rope is reeled in, so the sag drains out of
///          it, accelerating, until it is straight.
///   SNAP   The instant it comes up straight, a standing wave runs along it and rings down, the dust is
///          shaken out of it, and the vignette closes in a little.
///   HANG   Held taut, each rope is a weight on a line: its anchors sway slowly to and fro and the rope
///          twists, slowly, one way and then back, and it stays taut throughout.
///
/// MOTION. Like Heavy, the throw is kinematic and the rest is VerletStrand physics. The head flies a
/// constant-speed arc and the strand is DRIVEN along a slack guide shape, which cannot fold and, because
/// Drive hands the nodes the velocity that motion implies, means the loose line has real momentum at the
/// instant the head is stopped. From landing it is released to free physics: the slack swings in under
/// that momentum and settles on its own. The haul is physical too: the strand's length is reeled in
/// toward the straight-line distance, so it straightens as a real rope under tension would.
///
/// Three things are layered on the simulated path rather than simulated, as Heavy's wind is, because an
/// inextensible taut strand cannot move sideways and so cannot ring or sway by physics alone: the snap's
/// standing wave, the sway of the anchors, and the twist (which the material shows by sliding the rope's
/// lay). The last two are each a slow, continuous sine, so a rope turns one way and then back, and
/// swings to and fro, without pause. Every anchor, and the twist, has its own period, phase and size, so
/// no two ropes (or two ends of one) move alike. Once the ring has died away the simulation is frozen
/// and nothing but the overlay runs.
///
/// ROPES and IMPACTS are hero slots: this effect owns layout, choreography and mood; the material
/// answers what a rope looks like and what flies off it. "bind made of chains" swaps stroke.rope
/// for a chain and gets the same throw and haul, with sparks; "white rope made of snow" tints the
/// rope white and sheds snow along its whole length (this effect sets no emit limits, so particles
/// come off the full strand, not just near the edges as Heavy's do).
///
/// COVERAGE. Four ropes cut the four corners, so the screen is always bound evenly and the middle
/// stays clear. One or two more are partners that cross a corner's rope in an X, thinner and hazier,
/// seen behind, so there is always a crossing where the ropes lash. Everything else is reseeded per
/// cast: which end a rope is thrown from, where each anchor sits, depth, slack, timing, ring and hang.
/// </summary>
public sealed class BindEffect : ISceneEffect, IHasHeroSlots, IHasSwappableSlots
{
    public DebuffKind Kind => DebuffKind.Bind;
    public string DisplayName => "Bind";
    public string Description => "Ropes are thrown across the screen, then hauled taut around you.";

    // Shares Heavy's layer: both are strand effects, and which of the two is on top is immaterial.
    public int DrawOrder => 2;

    public IReadOnlyDictionary<string, float> TriggerStatuses { get; } = new Dictionary<string, float>
    {
        ["Bind"] = 1.0f,
    };

    public IReadOnlyList<string> TriggerKeywords { get; } = new[]
    {
        "rope", "ropes", "bound", "tied", "tether", "tethered", "snared", "entangled"
    };

    public EffectHeroSlot[] HeroSlots { get; } = new EffectHeroSlot[]
    {
        new("Stroke", PrimitiveRole.MainStroke),
    };

    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke, "Ropes",  "stroke.rope"),
        new("Particle", PrimitiveRole.Fibre,      "Fibres", "particle.fibre"),
        new("Particle", PrimitiveRole.Dust,       "Dust",   "particle.dust"),
    };

    // ---- layout ----
    private const int MaxRopes = 6;
    private const int StrandNodes = 14;
    private const int PathSamples = 56;

    // ---- mood (0 removes it) ----
    private const float VignetteAlpha = 0.12f;
    private const float VignetteDepth = 0.11f;          // fraction of the short side
    private const float VignetteTighten = 0.05f;        // extra depth as the ropes come taut

    // ---- rope sizes: diameter as a fraction of the short side ----
    private const float NearDiameterMin = 0.019f, NearDiameterMax = 0.026f;
    private const float FarDiameterMin  = 0.013f, FarDiameterMax  = 0.017f;

    // ---- the throw ----
    private const float MaxThrowDelay = 0.40f;
    private const float ThrowSpeedMin = 2300f, ThrowSpeedMax = 3100f;   // px/s at 1080p, averaged over the flight
    private const float MinFlight = 0.40f, MaxFlight = 0.85f;
    private const float ThrowArc = 0.10f;               // how far the head rises over the span, as a fraction of it
    private const float SlackMin = 0.20f, SlackMax = 0.32f;             // spare length, as a fraction of the span: lots
    private const float SlackSkew = 0.30f;              // the sag is deepest nearer the end it was thrown from
    private const float RippleAmp = 0.028f;             // ripple paid out along the line in flight, as a fraction of the span

    // ---- the settle: loose line under its own weight ----
    private const float Gravity = 2200f;                // px/s^2 at 1080p
    private const float HangDrag = 1.5f;                // 1/s: the swing rings down over a second or so
    private const float TautDrag = 24f;                 // 1/s: a taut line stops dead, it doesn't bounce (the ring is laid on after)

    // ---- the haul ----
    private const float HoldMin = 0.60f, HoldMax = 1.00f;               // how long the slack is left to settle before the pull
    private const float HaulMin = 0.38f, HaulMax = 0.58f;
    private const float HaulEase = 2.0f;                // >1: the take-up accelerates into the stop
    private const float TautExtraMin = -0.0004f, TautExtraMax = -0.0002f;  // a taut rope is a hair SHORTER than its span: the anchors hold it straight
    private const float TautGravityMin = 0.10f, TautGravityMax = 0.40f;     // how much of its weight still shows once hauled tight (tension swamps gravity)

    // ---- the snap ----
    private const float RingAmp = 12f;                  // px at 1080p, on top of the simulated overshoot
    private const float RingHzMin = 3.2f, RingHzMax = 5.0f;
    private const float RingDecay = 3.2f;               // 1/s
    private const float FreezeAfterTaut = 0.9f;         // the simulation has settled by now and has nothing left to do

    // ---- the hang ----
    private const float SwayAmp = 7f;                  // px at 1080p: how far an anchor swings either side of rest
    private const float SwayPeriodMin = 7f, SwayPeriodMax = 10f;     // seconds per swing, there and back
    private const float TwistAmp = 1.6f;                // radians: how far the rope turns either way, mid-rope
    private const float TwistPeriodMin = 7f, TwistPeriodMax = 10f;   // seconds per twist, there and back
    private const float HangDelay = 0.8f;               // seconds after the snap before it begins: the ring plays out first
    private const float HangEaseIn = 1.5f;              // seconds to ease up to its full swing, so it starts from rest

    private static readonly uint Vignette = DrawHelpers.ToU32(0.022f, 0.015f, 0.010f, 1f);

    private enum Corner { TopLeft, TopRight, BottomRight, BottomLeft }

    /// <summary>One rope: its anchors, its strand, its timeline, and what it has done so far.</summary>
    private sealed class Rig
    {
        public readonly VerletStrand Strand = new(StrandNodes);
        public readonly StrandPath Path = new(PathSamples);
        public readonly StrandPath BasePath = new(PathSamples);   // the frozen taut shape, which the hang is laid over
        public bool PathFrozen;

        public Vector2 Start, End;
        public Vector2 ChordDir, ChordPerp;
        public Vector2 SagDir;                  // the side the line hangs to; the head arcs the other way
        public float   SagSign;                 // ChordPerp * SagSign = SagDir; kept so it can be re-applied to the moving chord
        public float   ChordLen;
        public float   DiameterFrac;
        public float   Depth;
        public int     Seed;

        // timeline (seconds, from the rope's own start)
        public float Delay, Flight, Hold, Haul;
        public float Slack;                     // spare length as a fraction of the span, while loose
        public float TautExtra;                 // spare length left once taut
        public float TautGravity;               // fraction of gravity that still acts once taut

        // the ripple paid out along the line in flight
        public float RippleCycles, RippleHz, RipplePhase;

        // the ring
        public float RingHz, RingSign;

        // the hang: each anchor sways and the rope twists, as a slow sine of its own period, phase and size
        public float SwayOmegaA, SwayOmegaB, SwayPhaseA, SwayPhaseB, SwayScaleA, SwayScaleB;
        public float TwistOmega, TwistPhase, TwistScale;
        public float TwistNow;                  // the twist to show this frame, in radians (zero until the hang begins)

        // state
        public bool  Launched, Landed, Taut;
        public float TautAt;                    // age at which the rope came up straight
        public float Agitation;
        public float Haulness;                  // 0 slack .. 1 taut, for the vignette
    }

    private readonly Rig[] _rigs = new Rig[MaxRopes];
    private readonly int[] _drawOrder = new int[MaxRopes];
    private int _count;

    private readonly CastTracker _cast = new();

    public BindEffect()
    {
        for (int i = 0; i < MaxRopes; i++) _rigs[i] = new Rig();
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;

        if (_cast.Begin(time))
            BuildLayout(unchecked((int)(_cast.Start * 1000f)), screenSize);
        float age = time - _cast.Start;
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        float px = shortSide / 1080f;

        float haul = 0f;

        // Far ropes first so near ones overlap them.
        for (int n = 0; n < _count; n++)
        {
            var rig = _rigs[_drawOrder[n]];
            haul += rig.Haulness;

            float t = age - rig.Delay;
            if (t < 0f) continue;

            Step(scene, rig, screenSize, px, alpha, dt, t, colorOverride);

            if (rig.Path.Count < 2) continue;

            scene.AddStroke(new StrokePrimitive
            {
                Path = rig.Path,
                Role = PrimitiveRole.MainStroke,
                Reveal = 1f,
                WidthHint = shortSide * rig.DiameterFrac,
                Brightness = alpha,
                Seed = rig.Seed,
                Phase = DrawHelpers.HashRange(rig.Seed + 999, 0f, MathF.Tau),
                Depth = rig.Depth,
                Agitation = rig.Agitation,
                TipFlare = rig.Landed ? 0f : 1f,   // the head is live while it flies
                Twist = rig.TwistNow,
                ColorOverride = colorOverride,
            });
        }

        EmitAtmosphere(scene, alpha, age, _count, haul, colorOverride);
    }

    // ---- Atmosphere ----

    /// <summary>A dark vignette that closes in as the ropes come taut: the constriction.</summary>
    private static void EmitAtmosphere(EffectScene scene, float alpha, float age, int count, float haulSum, Vector4? colorOverride)
    {
        float castIn = DrawHelpers.Saturate(age / 0.7f);
        float tension = count > 0 ? haulSum / count : 0f;

        if (VignetteAlpha > 0f)
            scene.RequestVignette(Vignette, VignetteDepth + VignetteTighten * tension,
                                  alpha * VignetteAlpha * castIn * (0.7f + 0.3f * tension), priority: 20, colorOverride);
    }

    // ---- Per-rope simulation ----

    private void Step(EffectScene scene, Rig rig, Vector2 size, float px,
                      float alpha, float dt, float t, Vector4? colorOverride)
    {
        var strand = rig.Strand;

        if (!rig.Launched)
        {
            rig.Launched = true;
            strand.Reset(rig.Start);
            strand.Gravity = new Vector2(0f, Gravity * px);
            strand.Drag = HangDrag;
            strand.Iterations = 8;
            strand.BendStiffness = 0.75f;
        }

        float tLand = rig.Flight;
        float tHaul = tLand + rig.Hold;
        float tTaut = tHaul + rig.Haul;

        // ---- the head: constant speed after a short launch ramp, stopped dead by the far anchor ----
        // It is NOT eased to a stop. The line behind it is loose and heavy; it is what keeps going.
        float tau = DrawHelpers.Saturate(t / rig.Flight);
        float travel = (tau < 0.1f ? 5f * tau * tau : tau - 0.05f) / 0.95f;
        float arc = ThrowArc * rig.ChordLen * MathF.Sin(MathF.PI * tau);
        Vector2 head = rig.Start + (rig.End - rig.Start) * travel - rig.SagDir * arc;

        bool landedNow = false;
        if (tau >= 1f && !rig.Landed)
        {
            rig.Landed = true;
            landedNow = true;
            rig.Agitation = 0.5f;
        }
        if (rig.Landed) head = rig.End;

        bool snappedNow = false;
        if (!rig.Taut && t >= tTaut)
        {
            rig.Taut = true;
            snappedNow = true;
            rig.TautAt = tTaut;                                            // the scheduled instant, not the frame that noticed
            rig.Agitation = 1f;
        }

        bool frozen = rig.Taut && (t - tTaut) > FreezeAfterTaut;

        // ---- the strand ----
        float haulK = 0f;
        if (!frozen)
        {
            // Length is what makes a rope loose or taut. Loose: the span plus its slack, tracking the head as it
            // is paid out. Hauled: reeled in toward the span itself, accelerating, so the sag drains out.
            float dist = Vector2.Distance(head, rig.Start);
            float slackLength = dist * (1f + rig.Slack);
            float length = slackLength;
            if (t >= tHaul)
            {
                haulK = DrawHelpers.Saturate((t - tHaul) / rig.Haul);
                float tautLength = rig.ChordLen * (1f + rig.TautExtra);
                length = slackLength + (tautLength - slackLength) * MathF.Pow(haulK, HaulEase);
            }
            strand.Length = MathF.Max(2f, length);

            // Through the haul the line goes from a loose weight swinging to a taut one that stops dead:
            // heavier damping, tighter constraints (it is moving fastest, and a fast strand stretches), and
            // less and less of its weight showing, since sag is weight over tension and the tension is now enormous.
            float tighten = haulK * haulK * (3f - 2f * haulK);
            strand.Drag = HangDrag + (TautDrag - HangDrag) * tighten;
            strand.Gravity = new Vector2(0f, Gravity * px * (1f - (1f - rig.TautGravity) * tighten));
            strand.Iterations = haulK > 0f ? 24 : 8;

            if (!rig.Landed) GuideShape(rig, head, strand.Length - dist, t);

            strand.SetEnds(rig.Start, head);

            // Flight: driven along the guide (no folds, and the nodes pick up the velocity of that motion).
            // From landing: on its own.
            if (!rig.Landed) strand.Drive(strand.Target, dt);
            else             strand.Step(dt);
        }
        rig.Haulness = rig.Taut ? 1f : haulK * haulK;

        // ---- publish ----
        if (frozen)
        {
            if (!rig.PathFrozen)
            {
                strand.FillPath(rig.Path, PathSamples);
                Array.Copy(rig.Path.Points, rig.BasePath.Points, PathSamples);
                rig.BasePath.Count = PathSamples;
                rig.PathFrozen = true;
            }
            else
            {
                Array.Copy(rig.BasePath.Points, rig.Path.Points, PathSamples);
                rig.Path.Count = PathSamples;
            }
        }
        else
        {
            strand.FillPath(rig.Path, PathSamples);
            rig.PathFrozen = false;
        }

        if (rig.Taut) Hang(rig, t, px);

        // ---- impacts ----
        if (landedNow) ReportEnd(scene, rig, size, fromStart: false, 0.55f, alpha, colorOverride);
        if (snappedNow) Snap(scene, rig, size, alpha, colorOverride);

        rig.Agitation *= MathF.Exp(-dt / 0.95f);
    }

    /// <summary>
    /// A taut rope is a weight on a line. Laid over the simulated path: the ring left by the snap, and
    /// the sway of both anchors, which carries the whole rope with it (zero extra length, so it stays
    /// taut). The twist is not a displacement; it is handed to the material, which slides the lay.
    ///
    /// The sway of each anchor and the twist are each a slow sine on the time since the snap, so the
    /// rope turns one way and then back, and swings to and fro, without pause. They ease in from rest
    /// once the ring has died away, so there is no jump when they begin.
    /// </summary>
    private static void Hang(Rig rig, float t, float px)
    {
        float since = t - rig.TautAt;
        float ease = DrawHelpers.Saturate((since - HangDelay) / HangEaseIn);
        ease = ease * ease * (3f - 2f * ease);

        float amp = ease * SwayAmp * px;
        float swayA = amp * rig.SwayScaleA * MathF.Sin(rig.SwayOmegaA * since + rig.SwayPhaseA);
        float swayB = amp * rig.SwayScaleB * MathF.Sin(rig.SwayOmegaB * since + rig.SwayPhaseB);
        rig.TwistNow = ease * TwistAmp * rig.TwistScale * MathF.Sin(rig.TwistOmega * since + rig.TwistPhase);

        var path = rig.Path;
        int count = path.Count;
        float decay = MathF.Exp(-RingDecay * since);
        float ring = RingAmp * px * decay;
        float w1 = MathF.Tau * rig.RingHz * since;

        for (int i = 0; i < count; i++)
        {
            float u = i / (count - 1f);
            float lateral = swayA + (swayB - swayA) * u;

            if (ring > 0.05f)
            {
                // A plucked string: each mode an integer times the fundamental, the higher ones faster to die.
                lateral += ring * rig.RingSign *
                           (MathF.Sin(MathF.PI * u) * MathF.Sin(w1)
                            + 0.30f * MathF.Sin(2f * MathF.PI * u) * MathF.Sin(2f * w1) * decay
                            + 0.12f * MathF.Sin(3f * MathF.PI * u) * MathF.Sin(3f * w1) * decay * decay);
            }

            path.Points[i] += rig.ChordPerp * lateral;
        }
        path.BuildArc();
    }

    /// <summary>
    /// Fills the strand's guide with the loose line paid out behind the head: a parabola sagging
    /// downhill, deepest nearer the end it was thrown from, with a ripple running along it, whose arc
    /// length is exactly <paramref name="extra"/> more than the span. Used only in flight, where the
    /// strand is driven along it; because the guide's length matches the strand's, nothing has to be
    /// corrected when the head lands and the guide lets go. A near-vertical span has no downhill side, so it
    /// bows to whichever side this rope was seeded with.
    /// </summary>
    private static void GuideShape(Rig rig, Vector2 head, float extra, float t)
    {
        const int Samples = 48;

        Vector2 chord = head - rig.Start;
        float c = chord.Length();
        Vector2 dir = c > 1e-3f ? chord / c : rig.ChordDir;
        Vector2 perp = new Vector2(-dir.Y, dir.X) * rig.SagSign;
        float ripple = RippleAmp * c;

        Span<Vector2> pts = stackalloc Vector2[Samples + 1];
        Span<float> cum = stackalloc float[Samples + 1];

        // The ripple has length of its own; what is left for the sag is the rest.
        Trace(rig, rig.Start, chord, perp, 0f, ripple, t, pts, cum);
        float rippleExtra = cum[Samples] - c;
        float want = MathF.Max(extra - rippleExtra, extra * 0.25f);

        float sag = MathF.Sqrt(3f * c * want / 8f);        // small-sag estimate, refined below

        for (int pass = 0; pass < 3; pass++)
        {
            Trace(rig, rig.Start, chord, perp, sag, ripple, t, pts, cum);
            float got = cum[Samples] - c - rippleExtra;
            if (want < 1e-3f || got < 1e-3f) break;
            sag *= MathF.Sqrt(want / got);
        }
        Trace(rig, rig.Start, chord, perp, sag, ripple, t, pts, cum);

        // Equal arc-length spacing, to match the strand's equal-length links.
        var target = rig.Strand.Target;
        float total = cum[Samples];
        int seg = 0;
        for (int i = 0; i < StrandNodes; i++)
        {
            float wantArc = total * i / (StrandNodes - 1f);
            while (seg < Samples - 1 && cum[seg + 1] < wantArc) seg++;
            float span = cum[seg + 1] - cum[seg];
            float f = span > 1e-4f ? (wantArc - cum[seg]) / span : 0f;
            target[i] = Vector2.Lerp(pts[seg], pts[seg + 1], f);
        }
    }

    private static void Trace(Rig rig, Vector2 start, Vector2 chord, Vector2 perp, float sag, float ripple, float t,
                              Span<Vector2> pts, Span<float> cum)
    {
        int n = pts.Length - 1;
        for (int j = 0; j <= n; j++)
        {
            float f = j / (float)n;
            float shape = 4f * f * (1f - f) * (1f + SlackSkew * (1f - 2f * f));
            float wave = ripple * MathF.Sin(MathF.PI * f)
                         * MathF.Sin(MathF.Tau * (rig.RippleCycles * f - rig.RippleHz * t) + rig.RipplePhase);
            pts[j] = start + chord * f + perp * (sag * shape + wave);
            cum[j] = j == 0 ? 0f : cum[j - 1] + Vector2.Distance(pts[j], pts[j - 1]);
        }
    }

    /// <summary>The rope comes up straight: both anchors take the jerk and dust is shaken out along its length.</summary>
    private static void Snap(EffectScene scene, Rig rig, Vector2 size, float alpha, Vector4? colorOverride)
    {
        ReportEnd(scene, rig, size, fromStart: false, 0.40f, alpha, colorOverride);
        ReportEnd(scene, rig, size, fromStart: true, 0.40f, alpha, colorOverride);

        // Shaken loose along the rope, falling the way the line used to hang.
        float total = rig.Path.Length;
        for (int k = 1; k <= 3; k++)
        {
            rig.Path.SampleAtArc(total * k / 4f, out Vector2 at, out _);
            if (at.X < 0f || at.Y < 0f || at.X > size.X || at.Y > size.Y) continue;

            scene.AddImpact(new ImpactPrimitive
            {
                StrokeRole = PrimitiveRole.MainStroke,
                Position = at,
                Direction = Vector2.Normalize(rig.SagDir * 0.8f + new Vector2(0f, 0.6f)),
                Strength = 0.30f,
                Brightness = alpha,
                Seed = unchecked(rig.Seed + 7919 * k),
                ColorOverride = colorOverride,
            });
        }
    }

    /// <summary>
    /// Tells the framework a rope's anchor just took a hit. The impact point is where the rope crosses
    /// the screen edge; debris is thrown back along the rope and into the screen.
    /// </summary>
    private static void ReportEnd(EffectScene scene, Rig rig, Vector2 size, bool fromStart,
                                  float strength, float alpha, Vector4? colorOverride)
    {
        Vector2 point, inward;
        if (fromStart)
        {
            if (!ScreenEdges.TryFindEntry(rig.Path, size, out point, out inward)) return;
        }
        else
        {
            if (!ScreenEdges.TryFindExit(rig.Path, size, out point, out Vector2 outward)) return;
            inward = -outward;
        }

        Vector2 edgeIn = ScreenEdges.Inward(ScreenEdges.Nearest(size, point));
        Vector2 dir = inward * 0.65f + edgeIn * 0.5f;
        dir = dir.LengthSquared() > 1e-4f ? Vector2.Normalize(dir) : edgeIn;

        scene.AddImpact(new ImpactPrimitive
        {
            StrokeRole = PrimitiveRole.MainStroke,
            Position = point,
            Direction = dir,
            Strength = Math.Clamp(strength * (1f - 0.4f * rig.Depth), 0f, 1f),
            Brightness = alpha,
            Seed = unchecked(rig.Seed + (fromStart ? 31 : 17) + (int)(rig.TautAt * 1000f)),
            ColorOverride = colorOverride,
        });
    }

    // ---- Layout ----

    /// <summary>
    /// Lays out this application's ropes. Composition is fixed: one rope cutting each corner, so the
    /// screen is always bound evenly, then one or two partners that cross a corner rope. What varies per
    /// cast is each anchor's place along its edge, which end a rope is thrown from, and every rope's
    /// depth, slack and timing.
    /// </summary>
    private void BuildLayout(int castSeed, Vector2 size)
    {
        float shortSide = MathF.Min(size.X, size.Y);
        float overhang = shortSide * 0.07f;

        _count = 5 + (int)(DrawHelpers.Hash01(castSeed + 1) * 2f);   // 5 or 6
        if (_count > MaxRopes) _count = MaxRopes;

        // Partners go on corners whose rope they can cross; never the same corner twice.
        int firstPartner = (int)(DrawHelpers.Hash01(castSeed + 2) * 4f) & 3;
        int secondPartner = (firstPartner + 1 + (int)(DrawHelpers.Hash01(castSeed + 3) * 3f)) & 3;

        // The legs of each corner's rope, kept so a partner can be made steeper than it.
        Span<float> legA = stackalloc float[4];
        Span<float> legB = stackalloc float[4];

        for (int i = 0; i < _count; i++)
        {
            int s = unchecked(castSeed + i * 977 + 17);
            float H(int salt, float lo, float hi) => DrawHelpers.HashRange(s + salt, lo, hi);

            bool partner = i >= 4;
            int corner = partner ? (i == 4 ? firstPartner : secondPartner) : i;

            float a, b;                                   // distances from the corner along each edge, as fractions of that edge
            if (!partner)
            {
                a = H(1, 0.22f, 0.50f);
                b = H(2, 0.26f, 0.55f);
                legA[corner] = a; legB[corner] = b;
            }
            else
            {
                // Shorter along the top/bottom and longer down the side than the rope it crosses, so the two
                // must intersect, and by enough that they cross at a clear angle rather than lie alongside.
                a = MathF.Max(0.08f, legA[corner] * H(1, 0.30f, 0.55f));
                b = MathF.Min(0.66f, MathF.Max(legB[corner] * H(2, 1.50f, 1.90f), legB[corner] + 0.10f));
            }

            // Keep the span out of the middle of the screen.
            float reach = a + b;
            if (reach > 0.88f) { a *= 0.88f / reach; b *= 0.88f / reach; }

            Vector2 p = Anchor(size, (Corner)corner, a, b, overhang, out Vector2 q);
            BuildRig(_rigs[i], s, p, q, size, partner);
        }

        // Paint order: far (large Depth) first.
        for (int i = 0; i < _count; i++) _drawOrder[i] = i;
        Array.Sort(_drawOrder, 0, _count, Comparer<int>.Create((x, y) => _rigs[y].Depth.CompareTo(_rigs[x].Depth)));
    }

    /// <summary>The two anchors of a rope that cuts <paramref name="corner"/>: one on the horizontal edge, one on the vertical.</summary>
    private static Vector2 Anchor(Vector2 size, Corner corner, float a, float b, float overhang, out Vector2 vertical)
    {
        switch (corner)
        {
            case Corner.TopLeft:
                vertical = ScreenEdges.Anchor(size, ScreenEdge.Left, b, overhang);
                return ScreenEdges.Anchor(size, ScreenEdge.Top, a, overhang);
            case Corner.TopRight:
                vertical = ScreenEdges.Anchor(size, ScreenEdge.Right, b, overhang);
                return ScreenEdges.Anchor(size, ScreenEdge.Top, 1f - a, overhang);
            case Corner.BottomRight:
                vertical = ScreenEdges.Anchor(size, ScreenEdge.Right, 1f - b, overhang);
                return ScreenEdges.Anchor(size, ScreenEdge.Bottom, 1f - a, overhang);
            default:
                vertical = ScreenEdges.Anchor(size, ScreenEdge.Left, 1f - b, overhang);
                return ScreenEdges.Anchor(size, ScreenEdge.Bottom, a, overhang);
        }
    }

    private static void BuildRig(Rig r, int s, Vector2 a, Vector2 b, Vector2 size, bool far)
    {
        float H(int salt, float lo, float hi) => DrawHelpers.HashRange(s + salt, lo, hi);
        float px = MathF.Min(size.X, size.Y) / 1080f;

        // Which end it is thrown from.
        if (DrawHelpers.Hash01(s + 3) < 0.5f) (a, b) = (b, a);

        r.Start = a;
        r.End = b;

        Vector2 chord = b - a;
        r.ChordLen = MathF.Max(chord.Length(), 1e-3f);
        r.ChordDir = chord / r.ChordLen;
        r.ChordPerp = new Vector2(-r.ChordDir.Y, r.ChordDir.X);

        // Slack hangs downhill. A near-vertical span has no downhill side, so it takes whichever this rope was seeded with.
        float side = r.ChordPerp.Y >= 0f ? 1f : -1f;
        if (MathF.Abs(r.ChordPerp.Y) < 0.30f) side = H(4, 0f, 1f) < 0.5f ? 1f : -1f;
        r.SagSign = side;
        r.SagDir = r.ChordPerp * side;

        r.Seed = s;
        r.Depth = far ? H(5, 0.45f, 0.80f) : H(5, 0.00f, 0.12f);
        r.DiameterFrac = far ? H(6, FarDiameterMin, FarDiameterMax) : H(6, NearDiameterMin, NearDiameterMax);

        r.Delay = H(7, 0f, MaxThrowDelay);
        // Speed is what's chosen, not flight time, so a long span isn't faster than a short one.
        r.Flight = Math.Clamp(r.ChordLen / (H(8, ThrowSpeedMin, ThrowSpeedMax) * px), MinFlight, MaxFlight);
        r.Hold = H(9, HoldMin, HoldMax);
        r.Haul = H(10, HaulMin, HaulMax);
        r.Slack = far ? H(11, SlackMin * 0.8f, SlackMax * 0.8f) : H(11, SlackMin, SlackMax);
        r.TautExtra = H(16, TautExtraMin, TautExtraMax);
        r.TautGravity = H(17, TautGravityMin, TautGravityMax);

        r.RippleCycles = H(12, 1.3f, 2.0f);
        r.RippleHz = H(13, 0.9f, 1.4f);
        r.RipplePhase = H(14, 0f, MathF.Tau);
        r.RingHz = H(15, RingHzMin, RingHzMax);
        // The rope comes up from the side it hung on, so it overshoots to the other.
        r.RingSign = -side;

        // The hang: every period, phase and size is this rope's own.
        r.SwayOmegaA = MathF.Tau / H(20, SwayPeriodMin, SwayPeriodMax);
        r.SwayOmegaB = MathF.Tau / H(21, SwayPeriodMin, SwayPeriodMax);
        r.SwayPhaseA = H(22, 0f, MathF.Tau);
        r.SwayPhaseB = H(23, 0f, MathF.Tau);
        r.SwayScaleA = H(24, 0.6f, 1f);
        r.SwayScaleB = H(25, 0.6f, 1f);
        r.TwistOmega = MathF.Tau / H(26, TwistPeriodMin, TwistPeriodMax);
        r.TwistPhase = H(27, 0f, MathF.Tau);
        r.TwistScale = H(28, 0.7f, 1f);
        r.TwistNow = 0f;

        r.Launched = false;
        r.Landed = false;
        r.Taut = false;
        r.TautAt = 0f;
        r.Agitation = 0f;
        r.Haulness = 0f;
        r.PathFrozen = false;
        r.Path.Count = 0;
    }
}
