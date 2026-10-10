using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Bind: ropes are flung across the screen, made fast on the far side, and hauled taut around you.
/// The inverse of Heavy.
///
/// BEATS (per rope, staggered):
///   THROW      Head flies an arc, paying out a deep loose sag behind it.
///   MAKE FAST  Head hits the far anchor; stops dead. The slack does not - it swings, bunches, settles.
///   HAUL       Line is reeled in, sag drains out, accelerating, until straight.
///   SNAP       Standing wave runs along it, dust shakes out, vignette closes in.
///   HANG       Held taut: anchors sway slowly, rope twists one way and back, never slackens.
///
/// MOTION. The throw is kinematic (strand DRIVEN along a slack guide so it can't fold, and the
/// nodes pick up the velocity that motion implies). From landing it's free VerletStrand physics.
/// The haul is physical too: length is reeled in toward the straight-line distance.
///
/// The snap's standing wave, the anchor sway, and the twist are LAYERED ON the simulated path
/// rather than simulated, because an inextensible taut strand can't move sideways and so can't
/// ring or sway by physics alone. Once the ring has died away the sim is frozen and only the
/// overlay runs.
///
/// ROPES and IMPACTS are hero slots; "bind made of chains" swaps stroke.rope for chains and gets
/// the same choreography, with sparks. This effect sets no emit limits, so a swapped-in material
/// sheds along the full strand.
///
/// COVERAGE. Four ropes cut the four corners (screen always bound evenly, middle stays clear);
/// one or two partners cross a corner rope in an X. Everything else is reseeded per cast.
/// </summary>
public sealed class BindEffect : ISceneEffect, IHasHeroSlots, IHasSwappableSlots
{
    public DebuffKind Kind => DebuffKind.Bind;
    public string DisplayName => "Bind";
    public string Description => "Ropes are thrown across the screen, then hauled taut around you.";

    // Shares Heavy's layer: both are strand effects.
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
    private const float VignetteDepth = 0.11f;
    private const float VignetteTighten = 0.05f;

    // ---- rope sizes: diameter as a fraction of the short side ----
    private const float NearDiameterMin = 0.019f, NearDiameterMax = 0.026f;
    private const float FarDiameterMin  = 0.013f, FarDiameterMax  = 0.017f;

    // ---- the throw ----
    private const float MaxThrowDelay = 0.40f;
    private const float ThrowSpeedMin = 2300f, ThrowSpeedMax = 3100f;
    private const float MinFlight = 0.40f, MaxFlight = 0.85f;
    private const float ThrowArc = 0.10f;
    private const float SlackMin = 0.20f, SlackMax = 0.32f;
    private const float SlackSkew = 0.30f;
    private const float RippleAmp = 0.028f;

    // ---- the settle ----
    private const float Gravity = 2200f;
    private const float HangDrag = 1.5f;
    private const float TautDrag = 24f;

    // ---- the haul ----
    private const float HoldMin = 0.60f, HoldMax = 1.00f;
    private const float HaulMin = 0.38f, HaulMax = 0.58f;
    private const float HaulEase = 2.0f;
    private const float TautExtraMin = -0.0004f, TautExtraMax = -0.0002f;   // a taut rope is a hair SHORTER than its span
    private const float TautGravityMin = 0.10f, TautGravityMax = 0.40f;      // tension swamps gravity once hauled

    // ---- the snap ----
    private const float RingAmp = 12f;
    private const float RingHzMin = 3.2f, RingHzMax = 5.0f;
    private const float RingDecay = 3.2f;
    private const float FreezeAfterTaut = 0.9f;

    // ---- the hang ----
    private const float SwayAmp = 7f;
    private const float SwayPeriodMin = 7f, SwayPeriodMax = 10f;
    private const float TwistAmp = 1.6f;
    private const float TwistPeriodMin = 7f, TwistPeriodMax = 10f;
    private const float HangDelay = 0.8f;
    private const float HangEaseIn = 1.5f;

    private static readonly uint Vignette = DrawHelpers.ToU32(0.022f, 0.015f, 0.010f, 1f);

    // Standing-wave shape terms. u = i / (PathSamples - 1), fixed for every path, so precompute
    // once instead of calling MathF.Sin per sample per rope per frame in Hang.
    private static readonly float[] RingMode1 = new float[PathSamples];
    private static readonly float[] RingMode2 = new float[PathSamples];
    private static readonly float[] RingMode3 = new float[PathSamples];

    static BindEffect()
    {
        for (int i = 0; i < PathSamples; i++)
        {
            float u = i / (PathSamples - 1f);
            RingMode1[i] = MathF.Sin(MathF.PI * u);
            RingMode2[i] = MathF.Sin(2f * MathF.PI * u);
            RingMode3[i] = MathF.Sin(3f * MathF.PI * u);
        }
    }

    private enum Corner { TopLeft, TopRight, BottomRight, BottomLeft }

    private sealed class Rig
    {
        public readonly VerletStrand Strand = new(StrandNodes);
        public readonly StrandPath Path = new(PathSamples);
        public readonly StrandPath BasePath = new(PathSamples);   // frozen taut shape; hang is laid over this
        public bool PathFrozen;

        public Vector2 Start, End;
        public Vector2 ChordDir, ChordPerp;
        public Vector2 SagDir;
        public float   SagSign;
        public float   ChordLen;
        public float   DiameterFrac;
        public float   Depth;
        public int     Seed;
        public float   Phase;                   // cached from Seed; constant for the rig's life

        public float Delay, Flight, Hold, Haul;
        public float Slack;
        public float TautExtra;
        public float TautGravity;

        public float RippleCycles, RippleHz, RipplePhase;
        public float RingHz, RingSign;

        public float SwayOmegaA, SwayOmegaB, SwayPhaseA, SwayPhaseB, SwayScaleA, SwayScaleB;
        public float TwistOmega, TwistPhase, TwistScale;
        public float TwistNow;

        public bool  Launched, Landed, Taut;
        public float TautAt;
        public float Agitation;
        public float Haulness;
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
                Phase = rig.Phase,
                Depth = rig.Depth,
                Agitation = rig.Agitation,
                TipFlare = rig.Landed ? 0f : 1f,
                Twist = rig.TwistNow,
                ColorOverride = colorOverride,
            });
        }

        EmitAtmosphere(scene, alpha, age, _count, haul, colorOverride);
    }

    private static void EmitAtmosphere(EffectScene scene, float alpha, float age, int count, float haulSum, Vector4? colorOverride)
    {
        float castIn = DrawHelpers.Saturate(age / 0.7f);
        float tension = count > 0 ? haulSum / count : 0f;

        if (VignetteAlpha > 0f)
            scene.RequestVignette(Vignette, VignetteDepth + VignetteTighten * tension,
                                  alpha * VignetteAlpha * castIn * (0.7f + 0.3f * tension), priority: 20, colorOverride);
    }

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

        // Head: constant speed after a short launch ramp, stopped dead by the far anchor. NOT eased
        // to a stop - the loose line behind it is what keeps going.
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
            rig.TautAt = tTaut;   // the scheduled instant, not the frame that noticed
            rig.Agitation = 1f;
        }

        bool frozen = rig.Taut && (t - tTaut) > FreezeAfterTaut;

        float haulK = 0f;
        if (!frozen)
        {
            // Length is what makes a rope loose or taut. Loose: span plus slack, tracking the head
            // as it's paid out. Hauled: reeled in toward the span, accelerating, so sag drains out.
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

            // Through the haul the line goes from loose weight to taut stop: heavier damping,
            // tighter constraints (it's moving fastest and a fast strand stretches), and less and
            // less of its weight showing (sag is weight over tension, and tension is now enormous).
            float tighten = haulK * haulK * (3f - 2f * haulK);
            strand.Drag = HangDrag + (TautDrag - HangDrag) * tighten;
            strand.Gravity = new Vector2(0f, Gravity * px * (1f - (1f - rig.TautGravity) * tighten));
            strand.Iterations = haulK > 0f ? 24 : 8;

            if (!rig.Landed) GuideShape(rig, head, strand.Length - dist, t);

            strand.SetEnds(rig.Start, head);

            if (!rig.Landed) strand.Drive(strand.Target, dt);
            else             strand.Step(dt);
        }
        rig.Haulness = rig.Taut ? 1f : haulK * haulK;

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

        if (landedNow) ReportEnd(scene, rig, size, fromStart: false, 0.55f, alpha, colorOverride);
        if (snappedNow) Snap(scene, rig, size, alpha, colorOverride);

        rig.Agitation *= MathF.Exp(-dt / 0.95f);
    }

    /// <summary>
    /// A taut rope is a weight on a line. Laid OVER the simulated path: the snap's ring, and the
    /// sway of both anchors (which carries the whole rope, zero extra length, stays taut). The
    /// twist is not a displacement - it's handed to the material, which slides the lay.
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
        Vector2 perp = rig.ChordPerp;
        float swayDelta = swayB - swayA;

        // Ring terms that don't depend on i: hoisted out of the sample loop.
        float decay = MathF.Exp(-RingDecay * since);
        float ring = RingAmp * px * decay;
        bool ringing = ring > 0.05f;

        float mul1 = 0f, mul2 = 0f, mul3 = 0f;
        if (ringing)
        {
            float w1 = MathF.Tau * rig.RingHz * since;
            float d2 = decay * decay;
            float sign = ring * rig.RingSign;
            mul1 = sign * MathF.Sin(w1);
            mul2 = sign * 0.30f * MathF.Sin(2f * w1) * decay;
            mul3 = sign * 0.12f * MathF.Sin(3f * w1) * d2;
        }

        // Path is always filled to PathSamples (see the two FillPath call sites above), so the
        // precomputed shape tables index directly.
        if (ringing && count == PathSamples)
        {
            // A plucked string: modes are integer multiples of the fundamental; higher modes die faster.
            for (int i = 0; i < count; i++)
            {
                float u = i / (count - 1f);
                float lateral = swayA + swayDelta * u
                              + mul1 * RingMode1[i] + mul2 * RingMode2[i] + mul3 * RingMode3[i];
                path.Points[i] += perp * lateral;
            }
        }
        else
        {
            // Defensive fallback (should be unreachable): same math, computed per sample.
            float w1 = MathF.Tau * rig.RingHz * since;
            float d2 = decay * decay;
            for (int i = 0; i < count; i++)
            {
                float u = i / (count - 1f);
                float lateral = swayA + swayDelta * u;
                if (ring > 0.05f)
                {
                    lateral += ring * rig.RingSign *
                               (MathF.Sin(MathF.PI * u) * MathF.Sin(w1)
                                + 0.30f * MathF.Sin(2f * MathF.PI * u) * MathF.Sin(2f * w1) * decay
                                + 0.12f * MathF.Sin(3f * MathF.PI * u) * MathF.Sin(3f * w1) * d2);
                }
                path.Points[i] += perp * lateral;
            }
        }
        path.BuildArc();
    }

    /// <summary>
    /// Guide shape for the loose line paid out in flight: a downhill-sagging parabola (deepest
    /// nearer the throw-from end) with a ripple along it, whose arc length is exactly
    /// <paramref name="extra"/> more than the span. Used only in flight; the guide's length
    /// matches the strand's, so nothing needs correcting when the head lands and the guide lets go.
    /// Near-vertical spans have no downhill side, so they bow to whichever side was seeded.
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

        // Ripple has length of its own; what's left for the sag is the rest.
        Trace(rig, rig.Start, chord, perp, 0f, ripple, t, pts, cum);
        float rippleExtra = cum[Samples] - c;
        float want = MathF.Max(extra - rippleExtra, extra * 0.25f);

        float sag = MathF.Sqrt(3f * c * want / 8f);   // small-sag estimate, refined below

        // Sag grows with the square root of spare length, so a couple of secant steps converge.
        for (int pass = 0; pass < 3; pass++)
        {
            Trace(rig, rig.Start, chord, perp, sag, ripple, t, pts, cum);
            float got = cum[Samples] - c - rippleExtra;
            if (want < 1e-3f || got < 1e-3f) break;
            sag *= MathF.Sqrt(want / got);
        }
        Trace(rig, rig.Start, chord, perp, sag, ripple, t, pts, cum);

        // Equal arc-length spacing, matching the strand's equal-length links.
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

    /// <summary>A rope's anchor just took a hit; debris is thrown back along the rope and into the screen.</summary>
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

    /// <summary>
    /// One rope cutting each corner (screen always bound evenly), plus one or two partners that
    /// cross a corner rope in an X. What varies per cast: each anchor's place along its edge,
    /// which end a rope is thrown from, and every rope's depth, slack and timing.
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

        // The legs of each corner's rope, so a partner can be made steeper than the rope it crosses.
        Span<float> legA = stackalloc float[4];
        Span<float> legB = stackalloc float[4];

        for (int i = 0; i < _count; i++)
        {
            int s = unchecked(castSeed + i * 977 + 17);
            float H(int salt, float lo, float hi) => DrawHelpers.HashRange(s + salt, lo, hi);

            bool partner = i >= 4;
            int corner = partner ? (i == 4 ? firstPartner : secondPartner) : i;

            float a, b;
            if (!partner)
            {
                a = H(1, 0.22f, 0.50f);
                b = H(2, 0.26f, 0.55f);
                legA[corner] = a; legB[corner] = b;
            }
            else
            {
                // Shorter along the horizontal edge, longer down the vertical than the rope it
                // crosses, so the two must intersect - and by enough to cross at a clear angle.
                a = MathF.Max(0.08f, legA[corner] * H(1, 0.30f, 0.55f));
                b = MathF.Min(0.66f, MathF.Max(legB[corner] * H(2, 1.50f, 1.90f), legB[corner] + 0.10f));
            }

            // Keep the span out of the middle of the screen.
            float reach = a + b;
            if (reach > 0.88f) { a *= 0.88f / reach; b *= 0.88f / reach; }

            Vector2 p = Anchor(size, (Corner)corner, a, b, overhang, out Vector2 q);
            BuildRig(_rigs[i], s, p, q, size, partner);
        }

        for (int i = 0; i < _count; i++) _drawOrder[i] = i;
        Array.Sort(_drawOrder, 0, _count, Comparer<int>.Create((x, y) => _rigs[y].Depth.CompareTo(_rigs[x].Depth)));
    }

    /// <summary>The two anchors of a rope cutting <paramref name="corner"/>: one on the horizontal edge, one on the vertical.</summary>
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

        if (DrawHelpers.Hash01(s + 3) < 0.5f) (a, b) = (b, a);

        r.Start = a;
        r.End = b;

        Vector2 chord = b - a;
        r.ChordLen = MathF.Max(chord.Length(), 1e-3f);
        r.ChordDir = chord / r.ChordLen;
        r.ChordPerp = new Vector2(-r.ChordDir.Y, r.ChordDir.X);

        // Slack hangs downhill. A near-vertical span has no downhill side, so it takes whichever was seeded.
        float side = r.ChordPerp.Y >= 0f ? 1f : -1f;
        if (MathF.Abs(r.ChordPerp.Y) < 0.30f) side = H(4, 0f, 1f) < 0.5f ? 1f : -1f;
        r.SagSign = side;
        r.SagDir = r.ChordPerp * side;

        r.Seed = s;
        r.Phase = DrawHelpers.HashRange(s + 999, 0f, MathF.Tau);   // constant for the rig; no per-frame hash
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