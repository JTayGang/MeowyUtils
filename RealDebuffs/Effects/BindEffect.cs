using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Bind: ropes are flung across the screen, made fast on the far side, and hauled taut around you.
/// The inverse of Heavy. Heavy's chains arrive straight and fast, then drop slack and sway under
/// their own weight. Bind's ropes arrive slack and slow, with all the weight of the loose line,
/// and it is the PULL that is the event: they come up out of their sag, straighten, and stop dead,
/// ringing like a plucked string, and then they only strain.
///
/// BEATS (per rope, staggered):
///   THROW  The head flies an arc over the span, trailing a deep, loose sag and a ripple of slack.
///   MAKE FAST  The head lands off-screen; a soft thud of dust and fibres at the anchor. The slack hangs.
///   HAUL   After a short hold, the line is taken up: the sag drains out, accelerating, until the rope is
///          straight (it reads taut, with only a hair of weight left in it).
///   SNAP   The instant it comes up straight, a standing wave runs along it and rings down, the dust
///          is shaken out of it, and the vignette closes in a little.
///   STRAIN Held taut, every few seconds a rope takes a load: a kink runs down it and its strands
///          shiver, so a bound screen is tense rather than frozen.
///
/// MOTION. Nothing is simulated. Every rope is a closed-form function of time: a skewed parabolic sag
/// whose depth is eased out, plus a few travelling and standing sine waves. That is deliberate:
/// the whole choreography is slack-to-taut, which is precisely where a Verlet strand would sag, stretch
/// and tie knots, whereas a closed form cannot fold, costs next to nothing, and can be scrubbed to any
/// moment. The path is rebuilt each frame; the material does the expensive part.
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
/// cast: which end a rope is thrown from, where each anchor sits, depth, slack, timing, ring and strain.
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
    private const float SlackSagMin = 0.20f, SlackSagMax = 0.32f;       // loose line: sag as a fraction of the span
    private const float SlackSkew = 0.30f;              // sag is deepest nearer the end it was thrown from
    private const float RippleAmp = 0.034f;             // slack ripple, as a fraction of the span

    // ---- the haul ----
    private const float HoldMin = 0.22f, HoldMax = 0.50f;
    private const float HaulMin = 0.38f, HaulMax = 0.58f;
    private const float HaulEase = 2.4f;                // >1: the take-up accelerates into the stop
    private const float TautSagMin = 0.0020f, TautSagMax = 0.0100f;   // the weight left in a taut rope, as a fraction of the span

    // ---- the snap ----
    private const float RingAmp = 20f;                  // px at 1080p
    private const float RingHzMin = 3.2f, RingHzMax = 5.0f;
    private const float RingDecay = 3.2f;               // 1/s

    // ---- strain ----
    private const float StrainAmp = 4.5f;               // px at 1080p
    private const float StrainSeconds = 0.9f;
    private const float StrainGapMin = 2.6f, StrainGapMax = 6.0f;

    private static readonly uint Vignette = DrawHelpers.ToU32(0.022f, 0.015f, 0.010f, 1f);

    private enum Corner { TopLeft, TopRight, BottomRight, BottomLeft }

    /// <summary>One rope: its anchors, its timeline, and what it has done so far.</summary>
    private sealed class Rig
    {
        public readonly StrandPath Path = new(PathSamples);

        public Vector2 Start, End;
        public Vector2 ChordDir, ChordPerp;
        public Vector2 SagDir;                  // the side the line hangs to; the head arcs the other way
        public float   ChordLen;
        public float   DiameterFrac;
        public float   Depth;
        public int     Seed;

        // timeline (seconds, from the rope's own start)
        public float Delay, Flight, Hold, Haul;
        public float SlackSag;                  // as a fraction of the span
        public float TautSag;                   // what is left once it is taut

        // the ripple in slack line
        public float RippleCycles, RippleHz, RipplePhase;

        // the ring
        public float RingHz, RingSign;

        // state
        public bool  Landed, Taut;
        public float TautAt;                    // age at which the rope came up straight
        public float Agitation;
        public float Haulness;                  // 0 slack .. 1 taut, for the vignette

        // strain
        public float StrainStart = -100f, StrainNext;
        public float StrainFrom, StrainSign;
        public float StrainEnv;                 // 0..1 envelope of the strain being applied this frame
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
                ColorOverride = colorOverride,
            });
        }

        EmitAtmosphere(scene, alpha, age, count: _count, haul, colorOverride);
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

    // ---- Per-rope choreography ----

    private void Step(EffectScene scene, Rig rig, Vector2 size, float px,
                      float alpha, float dt, float t, Vector4? colorOverride)
    {
        float tLand = rig.Flight;
        float tHaul = rig.Flight + rig.Hold;
        float tTaut = tHaul + rig.Haul;

        // ---- events ----
        bool landedNow = false, snappedNow = false;
        if (!rig.Landed && t >= tLand)
        {
            rig.Landed = true;
            landedNow = true;
            rig.Agitation = 0.5f;
        }
        if (!rig.Taut && t >= tTaut)
        {
            rig.Taut = true;
            snappedNow = true;
            rig.TautAt = tTaut;                                            // the scheduled instant, not the frame that noticed
            rig.Agitation = 1f;
            rig.StrainNext = t + DrawHelpers.HashRange(rig.Seed + 21, 1.4f, 3.4f);
        }

        // ---- the head ----
        float tau = DrawHelpers.Saturate(t / rig.Flight);
        float travel = 1f - MathF.Pow(1f - tau, 1.7f);                       // fast off the hand, easing into the anchor
        float arc = ThrowArc * rig.ChordLen * MathF.Sin(MathF.PI * tau);
        Vector2 head = rig.Start + (rig.End - rig.Start) * travel - rig.SagDir * arc;
        if (rig.Landed) head = rig.End;

        // ---- the sag: loose, then drained out by the haul ----
        float sag;           // as a fraction of the current span
        float skew;
        float haulK = 0f;    // 0..1 through the haul
        if (t < tHaul)
        {
            // Still loose. After landing the weight of the line settles with a slow swing.
            float settle = rig.Landed ? MathF.Exp(-(t - tLand) / 0.40f) * MathF.Cos(MathF.Tau * 1.7f * (t - tLand)) : 0f;
            sag = rig.SlackSag * (1f + 0.07f * settle);
            skew = SlackSkew;
        }
        else
        {
            haulK = DrawHelpers.Saturate((t - tHaul) / rig.Haul);
            float drain = 1f - MathF.Pow(haulK, HaulEase);                   // ends at a stop, not a glide: that is the snap
            sag = rig.TautSag + (rig.SlackSag - rig.TautSag) * drain;
            skew = SlackSkew * drain;
        }
        rig.Haulness = rig.Taut ? 1f : haulK * haulK;

        // ---- ripples in the slack line ----
        float span = Vector2.Distance(head, rig.Start);
        float ripple = RippleAmp * span;
        if (t < tHaul) ripple *= rig.Landed ? 0.65f * MathF.Exp(-(t - tLand) / 0.55f) : 1f - 0.35f * tau;
        else           ripple *= 0.65f * MathF.Exp(-rig.Hold / 0.55f) * (1f - haulK) * (1f - haulK);

        // ---- the ring after the snap, and the strain on a taut rope ----
        float ringT = rig.Taut ? t - rig.TautAt : -1f;
        UpdateStrain(rig, t);

        // ---- build the path ----
        Vector2 chord = head - rig.Start;
        float sagPx = sag * span;
        float ringAmp = RingAmp * px * (span / MathF.Max(rig.ChordLen, 1f));
        float strainAmp = StrainAmp * px * rig.StrainEnv;
        float strainCentre = rig.StrainFrom + (1f - 2f * rig.StrainFrom) * DrawHelpers.Saturate((t - rig.StrainStart) / StrainSeconds);

        for (int i = 0; i < PathSamples; i++)
        {
            float u = i / (PathSamples - 1f);
            float ends = MathF.Sin(MathF.PI * u);

            float shape = 4f * u * (1f - u) * (1f + skew * (1f - 2f * u));
            float lateral = 0f;

            if (ripple > 0.01f)
                lateral += ripple * ends * MathF.Sin(MathF.Tau * (rig.RippleCycles * u - rig.RippleHz * t) + rig.RipplePhase);

            if (ringT >= 0f)
            {
                // A plucked string: odd modes ring, each an integer times the fundamental, the higher ones faster to die.
                float decay = MathF.Exp(-RingDecay * ringT);
                float w1 = MathF.Tau * rig.RingHz * ringT;
                lateral += ringAmp * decay * rig.RingSign *
                           (MathF.Sin(MathF.PI * u) * MathF.Sin(w1)
                            + 0.30f * MathF.Sin(2f * MathF.PI * u) * MathF.Sin(2f * w1) * decay
                            + 0.12f * MathF.Sin(3f * MathF.PI * u) * MathF.Sin(3f * w1) * decay * decay);
            }

            if (strainAmp > 0.01f)
            {
                float d = (u - strainCentre) / 0.10f;
                lateral += strainAmp * rig.StrainSign * MathF.Exp(-d * d);
            }

            rig.Path.Points[i] = rig.Start + chord * u + rig.SagDir * (sagPx * shape) + rig.ChordPerp * lateral;
        }
        rig.Path.Count = PathSamples;
        rig.Path.BuildArc();

        // ---- impacts ----
        if (landedNow) ReportEnd(scene, rig, size, fromStart: false, 0.55f, alpha, colorOverride);
        if (snappedNow) Snap(scene, rig, size, alpha, colorOverride);

        rig.Agitation *= MathF.Exp(-dt / 0.95f);
        if (rig.StrainEnv > 0.01f) rig.Agitation = MathF.Max(rig.Agitation, 0.55f * rig.StrainEnv);
    }

    /// <summary>
    /// A taut rope takes a load every few seconds: a kink runs from one end to the other. Scheduled
    /// from the rope's own clock, so it needs no state beyond when the next one starts.
    /// </summary>
    private static void UpdateStrain(Rig rig, float t)
    {
        rig.StrainEnv = 0f;
        if (!rig.Taut) return;

        if (t >= rig.StrainNext)
        {
            int n = unchecked((int)(t * 10f));
            rig.StrainStart = t;
            rig.StrainFrom = DrawHelpers.Hash01(rig.Seed + n) < 0.5f ? 0.08f : 0.92f;   // which end the load comes from
            rig.StrainSign = DrawHelpers.Hash01(rig.Seed + n + 1) < 0.5f ? -1f : 1f;
            rig.StrainNext = t + StrainSeconds + DrawHelpers.HashRange(rig.Seed + n + 2, StrainGapMin, StrainGapMax);
        }

        float k = (t - rig.StrainStart) / StrainSeconds;
        if (k < 0f || k > 1f) return;
        rig.StrainEnv = MathF.Sin(MathF.PI * k);
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
        r.SagDir = r.ChordPerp * side;

        r.Seed = s;
        r.Depth = far ? H(5, 0.45f, 0.80f) : H(5, 0.00f, 0.12f);
        r.DiameterFrac = far ? H(6, FarDiameterMin, FarDiameterMax) : H(6, NearDiameterMin, NearDiameterMax);

        r.Delay = H(7, 0f, MaxThrowDelay);
        // Speed is what's chosen, not flight time, so a long span isn't faster than a short one.
        r.Flight = Math.Clamp(r.ChordLen / (H(8, ThrowSpeedMin, ThrowSpeedMax) * px), MinFlight, MaxFlight);
        r.Hold = H(9, HoldMin, HoldMax);
        r.Haul = H(10, HaulMin, HaulMax);
        r.SlackSag = far ? H(11, SlackSagMin * 0.8f, SlackSagMax * 0.8f) : H(11, SlackSagMin, SlackSagMax);
        r.TautSag = H(16, TautSagMin, TautSagMax);

        r.RippleCycles = H(12, 1.3f, 2.0f);
        r.RippleHz = H(13, 0.9f, 1.4f);
        r.RipplePhase = H(14, 0f, MathF.Tau);
        r.RingHz = H(15, RingHzMin, RingHzMax);
        // The rope comes up from the side it hung on, so it overshoots to the other.
        r.RingSign = -side;

        r.Landed = false;
        r.Taut = false;
        r.TautAt = 0f;
        r.Agitation = 0f;
        r.Haulness = 0f;
        r.StrainStart = -100f;
        r.StrainNext = 0f;
        r.StrainEnv = 0f;
        r.Path.Count = 0;
    }
}
