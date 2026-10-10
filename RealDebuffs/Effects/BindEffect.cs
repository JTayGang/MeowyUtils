using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Bind: ropes flung across the screen, made fast on the far side, hauled taut around you (Heavy's inverse): throw, haul, snap, hang. The snap's ring, sway and twist are laid over the sim path, which freezes once the ring dies.</summary>
public sealed class BindEffect : ISceneEffect, IHasHeroSlots, IHasSwappableSlots
{
    public DebuffKind Kind => DebuffKind.Bind;
    public string DisplayName => "Bind";
    public string Description => "Ropes are thrown across the screen, then hauled taut around you.";
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

    private const int MaxRopes = 6;
    private const int StrandNodes = 14;
    private const int PathSamples = 56;

    private const float VignetteAlpha = 0.12f;
    private const float VignetteDepth = 0.11f;
    private const float VignetteTighten = 0.05f;

    // Rope diameter as a fraction of the short side.
    private const float NearDiameterMin = 0.019f, NearDiameterMax = 0.026f;
    private const float FarDiameterMin  = 0.013f, FarDiameterMax  = 0.017f;

    // The throw.
    private const float MaxThrowDelay = 0.40f;
    private const float ThrowSpeedMin = 2300f, ThrowSpeedMax = 3100f;
    private const float MinFlight = 0.40f, MaxFlight = 0.85f;
    private const float ThrowArc = 0.10f;
    private const float SlackMin = 0.20f, SlackMax = 0.32f;
    private const float SlackSkew = 0.30f;
    private const float RippleAmp = 0.028f;

    // The settle.
    private const float Gravity = 2200f;
    private const float HangDrag = 1.5f;
    private const float TautDrag = 24f;

    // The haul.
    private const float HoldMin = 0.60f, HoldMax = 1.00f;
    private const float HaulMin = 0.38f, HaulMax = 0.58f;
    private const float HaulEase = 2.0f;
    private const float TautExtraMin = -0.0004f, TautExtraMax = -0.0002f;   // a taut rope is a hair shorter than its span
    private const float TautGravityMin = 0.10f, TautGravityMax = 0.40f;      // tension swamps gravity once hauled

    // The snap.
    private const float RingAmp = 12f;
    private const float RingHzMin = 3.2f, RingHzMax = 5.0f;
    private const float RingDecay = 3.2f;
    private const float FreezeAfterTaut = 0.9f;

    // The hang.
    private const float SwayAmp = 7f;
    private const float SwayPeriodMin = 7f, SwayPeriodMax = 10f;
    private const float TwistAmp = 1.6f;
    private const float TwistPeriodMin = 7f, TwistPeriodMax = 10f;
    private const float HangDelay = 0.8f;
    private const float HangEaseIn = 1.5f;

    private static readonly uint Vignette = DrawHelpers.Pack(0.022f, 0.015f, 0.010f);

    // Standing-wave shapes per path sample (u = i / (PathSamples - 1) is fixed), so Hang needs no per-sample sines.
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

    private sealed class Rig : StrandRig
    {
        public Vector2 SagDir;
        public float SagSign, DiameterFrac;

        public float Hold, Haul, Slack, TautExtra, TautGravity;
        public float RippleCycles, RippleHz, RipplePhase;
        public float RingHz, RingSign;

        public float SwayOmegaA, SwayOmegaB, SwayPhaseA, SwayPhaseB, SwayScaleA, SwayScaleB;
        public float TwistOmega, TwistPhase, TwistScale, TwistNow;

        public bool Taut;
        public float TautAt, Haulness;

        public Rig() : base(StrandNodes, PathSamples) { }

        public override void Reset(int seed)
        {
            base.Reset(seed);
            Taut = false;
            TautAt = Haulness = TwistNow = 0f;
        }
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

        // The vignette closes in as the ropes are hauled taut.
        float castIn = DrawHelpers.Saturate(age / 0.7f);
        float tension = _count > 0 ? haul / _count : 0f;
        scene.RequestVignette(Vignette, VignetteDepth + VignetteTighten * tension,
                              alpha * VignetteAlpha * castIn * (0.7f + 0.3f * tension), priority: 20, colorOverride);
    }

    private static void Step(EffectScene scene, Rig rig, Vector2 size, float px,
                             float alpha, float dt, float t, Vector4? colorOverride)
    {
        var strand = rig.Strand;

        if (!rig.Launched) rig.Launch(Gravity * px, HangDrag);

        float tHaul = rig.Flight + rig.Hold;
        float tTaut = tHaul + rig.Haul;

        // Head: constant speed after a short launch ramp, stopped dead by the far anchor. The loose line behind it keeps going.
        float tau = DrawHelpers.Saturate(t / rig.Flight);
        float arc = ThrowArc * rig.ChordLen * MathF.Sin(MathF.PI * tau);
        Vector2 head = rig.Start + (rig.End - rig.Start) * StrandRig.Travel(tau) - rig.SagDir * arc;

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
            // Length makes a rope loose or taut: span plus slack while paid out, then reeled in toward the span.
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

            // Through the haul: heavier damping, tighter constraints, and less visible weight (sag is weight over tension).
            float tighten = DrawHelpers.Smooth(haulK);
            strand.Drag = HangDrag + (TautDrag - HangDrag) * tighten;
            strand.Gravity = new Vector2(0f, Gravity * px * (1f - (1f - rig.TautGravity) * tighten));
            strand.Iterations = haulK > 0f ? 24 : 8;

            if (!rig.Landed)
            {
                var style = new DrapeStyle(rig.SagSign, Skew: SlackSkew,
                                           Ripple: new Ripple(RippleAmp, rig.RippleCycles, rig.RippleHz, rig.RipplePhase));
                StrandGuide.Drape(rig.Start, head, rig.ChordDir, strand.Length - dist, style, t, strand.Target);
            }

            strand.SetEnds(rig.Start, head);

            if (!rig.Landed) strand.Drive(strand.Target, dt);
            else             strand.Step(dt);
        }
        rig.Haulness = rig.Taut ? 1f : haulK * haulK;

        rig.Publish(PathSamples, frozen);

        if (rig.Taut) Hang(rig, t, px);

        if (landedNow) ReportEnd(scene, rig, size, fromStart: false, 0.55f, alpha, colorOverride);
        if (snappedNow) Snap(scene, rig, size, alpha, colorOverride);

        rig.Agitation *= MathF.Exp(-dt / 0.95f);
    }

    // A taut rope is a weight on a line: the snap's ring and both anchors' sway are laid over the simulated path (zero extra length); twist goes to the material.
    private static void Hang(Rig rig, float t, float px)
    {
        float since = t - rig.TautAt;
        float ease = DrawHelpers.Smooth((since - HangDelay) / HangEaseIn);

        float amp = ease * SwayAmp * px;
        float swayA = amp * rig.SwayScaleA * MathF.Sin(rig.SwayOmegaA * since + rig.SwayPhaseA);
        float swayB = amp * rig.SwayScaleB * MathF.Sin(rig.SwayOmegaB * since + rig.SwayPhaseB);
        rig.TwistNow = ease * TwistAmp * rig.TwistScale * MathF.Sin(rig.TwistOmega * since + rig.TwistPhase);

        float decay = MathF.Exp(-RingDecay * since);
        float ring = RingAmp * px * decay;

        // A plucked string: modes are integer multiples of the fundamental; higher modes die faster.
        float mul1 = 0f, mul2 = 0f, mul3 = 0f;
        if (ring > 0.05f)
        {
            float w1 = MathF.Tau * rig.RingHz * since;
            float sign = ring * rig.RingSign;
            mul1 = sign * MathF.Sin(w1);
            mul2 = sign * 0.30f * MathF.Sin(2f * w1) * decay;
            mul3 = sign * 0.12f * MathF.Sin(3f * w1) * (decay * decay);
        }

        var path = rig.Path;
        Vector2 perp = rig.ChordPerp;
        float swayDelta = swayB - swayA;

        for (int i = 0; i < PathSamples; i++)
        {
            float u = i / (PathSamples - 1f);
            float lateral = swayA + swayDelta * u
                          + mul1 * RingMode1[i] + mul2 * RingMode2[i] + mul3 * RingMode3[i];
            path.Points[i] += perp * lateral;
        }
        path.BuildArc();
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

    private static void ReportEnd(EffectScene scene, Rig rig, Vector2 size, bool fromStart,
                                  float strength, float alpha, Vector4? colorOverride) =>
        rig.ReportEnd(scene, size, fromStart, strength, alpha,
                      unchecked(rig.Seed + (fromStart ? 31 : 17) + (int)(rig.TautAt * 1000f)), colorOverride);

    // One rope cutting each corner, plus one or two partners crossing a corner rope in an X; anchors, throw end, depth, slack and timing are seeded per cast.
    private void BuildLayout(int castSeed, Vector2 size)
    {
        float overhang = MathF.Min(size.X, size.Y) * 0.07f;

        _count = Math.Min(5 + (int)(DrawHelpers.Hash01(castSeed + 1) * 2f), MaxRopes);

        // Partners go on corners whose rope they can cross; never the same corner twice.
        int firstPartner = (int)(DrawHelpers.Hash01(castSeed + 2) * 4f) & 3;
        int secondPartner = (firstPartner + 1 + (int)(DrawHelpers.Hash01(castSeed + 3) * 3f)) & 3;

        // Each corner rope's legs, so a partner can be made steeper than the rope it crosses.
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
                // Shorter along the horizontal edge, longer down the vertical than the rope it crosses, so the two cross at a clear angle.
                a = MathF.Max(0.08f, legA[corner] * H(1, 0.30f, 0.55f));
                b = MathF.Min(0.66f, MathF.Max(legB[corner] * H(2, 1.50f, 1.90f), legB[corner] + 0.10f));
            }

            // Keep the span out of the middle of the screen.
            float reach = a + b;
            if (reach > 0.88f) { a *= 0.88f / reach; b *= 0.88f / reach; }

            Vector2 p = Anchor(size, (Corner)corner, a, b, overhang, out Vector2 q);
            BuildRig(_rigs[i], s, p, q, size, partner);
        }

        StrandRig.SortFarFirst(_rigs, _drawOrder, _count);
    }

    /// <summary>The two anchors of a rope cutting <paramref name="corner"/>: one on the horizontal edge (returned), one on the vertical.</summary>
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

        r.Reset(s);
        r.SetChord(a, b);

        // Slack hangs downhill; a near-vertical span takes the seeded side.
        float side = StrandGuide.DownhillSide(r.ChordDir, H(4, 0f, 1f) < 0.5f ? 1f : -1f);
        r.SagSign = side;
        r.SagDir = r.ChordPerp * side;

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
        r.RingSign = -side;   // the rope comes up from the side it hung on, so it overshoots to the other

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
    }
}
