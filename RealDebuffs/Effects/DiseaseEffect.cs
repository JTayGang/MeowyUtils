using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Disease: parasitic tentacles unfurl from the edges, feel across the frame and drip slime; one may grip a border point, haul, release and search again. A sprung Verlet strand follows a target curve; goal, heading and curl are critically damped, so direction never jumps.</summary>
public sealed class DiseaseEffect : ISceneEffect, IHasHeroSlots, IHasSwappableSlots
{
    public DebuffKind Kind => DebuffKind.Disease;
    public string DisplayName => "Disease";
    public string Description => "Slimy parasite tentacles unfurl from the edges and reach for something to grip.";
    public int DrawOrder => 4;

    public IReadOnlyDictionary<string, float> TriggerStatuses { get; } = new Dictionary<string, float>
    {
        ["Disease"] = 1.0f,
    };

    public IReadOnlyList<string> TriggerKeywords { get; } = new[]
    {
        "parasite", "parasites", "infest", "infested", "tentacle", "tentacles"
    };

    public EffectHeroSlot[] HeroSlots { get; } = new EffectHeroSlot[]
    {
        new("Stroke", PrimitiveRole.MainStroke),
    };

    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke, "Tentacles", "stroke.parasite"),
        new("Particle", PrimitiveRole.Goop,       "Slime",     "particle.slime"),
    };

    // ---- layout ----
    private const int MaxTentacles = 8;
    private const int StrandNodes = 18;
    private const int PathSamples = 64;
    private const int GuideSamples = 64;           // density of the target curve before it is cut into nodes

    // ---- mood (0 removes it) ----
    private const float VignetteAlpha = 0.16f;
    private const float GloomAlpha = 0.34f;
    private const float GloomDepth = 0.15f;

    // ---- how the strand follows its target ----
    private const float ShapeSpring = 70f;         // 1/s^2: stiffness toward the target curve
    private const float StrandDrag = 8.5f;         // 1/s: underdamped enough to swing, damped enough to settle
    private const float StrandGravity = 700f;      // px/s^2 at 1080p: the slime-laden weight, a droop and no more

    // ---- wandering ----
    private const float HomeReach = 0.80f;         // home point, as a fraction of the tentacle's length from its root
    private const float WanderLateral = 0.20f;     // sweep either side of home, as a fraction of length
    private const float WanderAxial = 0.07f;

    // ---- gripping ----
    private const float GripMinReach = 0.52f;      // the border point must be at least this far from the root, as a fraction of length...
    private const float GripMaxReach = 0.84f;      // ...and no further than this
    private const float GripInsetFrac = 0.005f;    // how far inside the border the tip lands, as a fraction of the short side
    private const float GripSpacing = 240f;        // two tentacles never grip closer than this (px at 1080p)
    private const float PinSeconds = 0.45f;        // how long the tip takes to settle onto the point it has reached
    private const int   MaxGrippers = 3;

    // ---- intro ----
    private const float UnfurlCurl = 1.9f;         // radians of curl in the tip as it emerges
    private const float UnfurlRate = 2.1f;         // 1/s: how quickly it uncurls

    private static readonly uint Gloom = DrawHelpers.Pack(0.010f, 0.020f, 0.012f);
    private static readonly uint Vignette = DrawHelpers.Pack(0.012f, 0.022f, 0.012f);

    private enum Mode : byte { Wander, Reach, Grip, Release }

    /// <summary>Where each tentacle comes from and how big it is. The set is fixed so the frame is always covered evenly.</summary>
    private readonly record struct Archetype(ScreenEdge Edge, float AlongLo, float AlongHi,
                                             float LengthLo, float LengthHi, float DiameterLo, float DiameterHi, bool Far);

    private static readonly Archetype[] Archetypes =
    {
        new(ScreenEdge.Bottom, 0.08f, 0.30f, 0.64f, 0.78f, 0.047f, 0.057f, false),   // three big near ones rising from below
        new(ScreenEdge.Bottom, 0.40f, 0.62f, 0.60f, 0.74f, 0.047f, 0.057f, false),
        new(ScreenEdge.Bottom, 0.70f, 0.92f, 0.64f, 0.78f, 0.047f, 0.057f, false),
        new(ScreenEdge.Left,   0.28f, 0.66f, 0.52f, 0.66f, 0.036f, 0.045f, false),    // one from each side
        new(ScreenEdge.Right,  0.28f, 0.66f, 0.52f, 0.66f, 0.036f, 0.045f, false),
        new(ScreenEdge.Top,    0.08f, 0.38f, 0.42f, 0.54f, 0.034f, 0.042f, false),    // two hanging from the top: the tip is the lowest point, so it drips
        new(ScreenEdge.Top,    0.62f, 0.92f, 0.42f, 0.54f, 0.034f, 0.042f, false),
        new(ScreenEdge.Bottom, 0.00f, 1.00f, 0.50f, 0.62f, 0.022f, 0.028f, true),     // a hazy one behind the rest, for depth
    };

    private sealed class Rig
    {
        public readonly VerletStrand Strand = new(StrandNodes);
        public readonly StrandPath Path = new(PathSamples);

        // ---- layout: fixed for a cast ----
        public Vector2 Root, Inward, Home;
        public float Length, DiameterFrac, Depth, Delay, Grow;
        public int Seed;
        public float Phase;
        public float LatAmp, AxAmp, W1, W2, P1, P2;
        public float ShapeAngle0, ShapeRate;
        public float CurlAmp, CurlPhase, UnfurlSign, TwistAmp, TwistPhase, BreathPhase, WavePhase;
        public float FirstGripDelay;

        // ---- state ----
        public bool Started, Entered;
        public Mode Mode;
        public float ModeTime, NextGripAt, GripHold, GripStart;
        public Vector2 Goal, GoalVel, GripPoint, PinFrom, PinVel, LastTip, TipVelocity;
        public float HeadAngle, HeadVel, Curl, CurlVel, Tension, TensionVel;
        public float Agitation, Reveal;
        public float Age;                      // seconds since this tentacle started
        public float GripSide;                 // which way the tip hooks round the border
    }

    private readonly Rig[] _rigs = new Rig[MaxTentacles];
    private readonly int[] _drawOrder = new int[MaxTentacles];
    private int _count;

    private readonly CastTracker _cast = new();

    public DiseaseEffect()
    {
        for (int i = 0; i < MaxTentacles; i++) _rigs[i] = new Rig();
    }

    public void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        if (screenSize.X < 64f || screenSize.Y < 64f) return;
        dt = MathF.Min(dt, 0.05f);                      // a hitch must not become a leap

        if (_cast.Begin(time))
            BuildLayout(unchecked((int)(_cast.Start * 1000f)), screenSize);
        float age = time - _cast.Start;
        float shortSide = MathF.Min(screenSize.X, screenSize.Y);
        float px = shortSide / 1080f;

        EmitAtmosphere(scene, screenSize, alpha, time, age, colorOverride);

        for (int n = 0; n < _count; n++)
        {
            var rig = _rigs[_drawOrder[n]];
            if (age < rig.Delay) continue;

            Step(scene, rig, screenSize, px, alpha, time, dt, age, colorOverride);
            if (rig.Path.Count < 2 || rig.Reveal <= 0.002f) continue;

            float growing = DrawHelpers.Saturate(1f - rig.Reveal);
            scene.AddStroke(new StrokePrimitive
            {
                Path = rig.Path,
                Role = PrimitiveRole.MainStroke,
                Reveal = rig.Reveal,
                FlushStart = true,                       // its root is behind the edge; no cap to show
                WidthHint = shortSide * rig.DiameterFrac,
                Brightness = alpha,
                TipFlare = growing > 0.02f ? MathF.Min(1f, growing * 1.6f) : 0f,
                Seed = rig.Seed,
                Phase = rig.Phase,
                Depth = rig.Depth,
                Agitation = rig.Agitation,
                Twist = rig.TwistAmp * MathF.Sin(0.27f * time + rig.TwistPhase) + (rig.Mode == Mode.Grip ? 0.5f * rig.Tension : 0f),
                ColorOverride = colorOverride,
            });
        }
    }

    // ---- Atmosphere ----

    private static void EmitAtmosphere(EffectScene scene, Vector2 size, float alpha, float time, float age, Vector4? colorOverride)
    {
        float castIn = DrawHelpers.Saturate(age / 0.9f);

        if (VignetteAlpha > 0f)
            scene.RequestVignette(Vignette, 0.12f, alpha * VignetteAlpha * castIn, priority: 20, colorOverride);

        // Murk pooled at the bottom edge, swelling slowly, as if something were breathing under it.
        float depth = size.Y * GloomDepth * (1f + 0.18f * DrawHelpers.Pulse(time, 5.3f)) * castIn;
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

    // ---- One tentacle, one frame ----

    private void Step(EffectScene scene, Rig r, Vector2 size, float px, float alpha, float time, float dt, float age,
                      Vector4? colorOverride)
    {
        r.Age = age - r.Delay;
        float t = r.Age;
        var strand = r.Strand;

        if (!r.Started) Start(r, size, px, time);

        // ---- what it wants this frame ----
        Vector2 goalWant;
        float headWant, curlWant, tensionWant, goalOmega;
        Decide(r, size, px, time, t, out goalWant, out headWant, out curlWant, out tensionWant, out goalOmega);

        // ---- critically damped springs: every state change is a glide, never a jump ----
        Spring(ref r.Goal, ref r.GoalVel, goalWant, goalOmega, dt);
        SpringAngle(ref r.HeadAngle, ref r.HeadVel, headWant, 3.2f, dt);
        Spring(ref r.Curl, ref r.CurlVel, curlWant, r.Mode == Mode.Wander && t < 3f ? UnfurlRate : 2.6f, dt);
        Spring(ref r.Tension, ref r.TensionVel, tensionWant, 3f, dt);

        // ---- the target curve, and a strand that chases it ----
        float length = BuildTarget(r, px, time, t, out float chord);
        strand.Length = MathF.Max(8f, length);
        strand.Gravity = new Vector2(0f, StrandGravity * px);
        strand.Drag = StrandDrag;
        strand.Iterations = 8;
        strand.BendStiffness = 0.6f;
        strand.ShapeStiffness = ShapeSpring;

        bool pinned = r.Mode == Mode.Grip;
        strand.PinStart = true;
        strand.PinEnd = pinned;
        Vector2 tip = strand.Pos[StrandNodes - 1];
        Vector2 end = tip;
        if (pinned)
        {
            // Settle onto the point along a curve from the tip's own velocity to rest, so the pin takes over without a dead stop.
            float k = DrawHelpers.Saturate((time - r.GripStart) / PinSeconds);
            end = Hermite(r.PinFrom, r.PinVel * PinSeconds, r.GripPoint, k);
        }
        strand.SetEnds(r.Root, end);                    // unpinned, the end argument tracks the tip so a later pin starts from it
        r.TipVelocity = dt > 1e-4f ? (tip - r.LastTip) / dt : default;
        r.LastTip = tip;

        strand.Step(dt);
        strand.FillPath(r.Path, PathSamples);

        // ---- reveal: it surges out of the edge, fast at first and settling ----
        float grow = DrawHelpers.Saturate(t / r.Grow);
        r.Reveal = 1f - MathF.Pow(1f - grow, 2.6f);

        // ---- events ----
        if (!r.Entered && r.Reveal * r.Length > px * 1080f * 0.075f + 14f)
        {
            r.Entered = true;
            r.Agitation = 1f;
            if (ScreenEdges.TryFindEntry(r.Path, size, out Vector2 at, out Vector2 inward))
                Splat(scene, at, inward, 1f - 0.4f * r.Depth, alpha, time, r.Seed, colorOverride);
        }

        float floor = r.Mode switch { Mode.Reach => 0.30f, Mode.Grip => 0.22f + 0.14f * MathF.Sin(time * 2.6f + r.Phase), _ => 0.04f };
        r.Agitation = MathF.Max(r.Agitation * MathF.Exp(-dt / 0.9f), floor);
    }

    /// <summary>Places a tentacle that has just begun: its strand lies exactly on its first target, so nothing has to settle.</summary>
    private void Start(Rig r, Vector2 size, float px, float time)
    {
        r.Started = true;
        r.Entered = false;
        r.Mode = Mode.Wander;
        r.ModeTime = time;
        r.NextGripAt = time + r.Grow + r.FirstGripDelay;
        r.Goal = r.Home;
        r.GoalVel = default;
        r.HeadAngle = MathF.Atan2(r.Inward.Y, r.Inward.X);
        r.HeadVel = 0f;
        r.Curl = UnfurlCurl * r.UnfurlSign;
        r.CurlVel = 0f;
        r.Tension = 0f;
        r.TensionVel = 0f;
        r.Agitation = 1f;
        r.Reveal = 0f;

        var s = r.Strand;
        s.Reset(r.Root);
        float length = BuildTarget(r, px, time, 0f, out _);
        s.Length = MathF.Max(8f, length);
        s.Drive(s.Target, 1f / 60f);
    }

    // ---- Behaviour ----

    private void Decide(Rig r, Vector2 size, float px, float time, float t,
                        out Vector2 goal, out float head, out float curl, out float tension, out float goalOmega)
    {
        Vector2 perp = new(-r.Inward.Y, r.Inward.X);

        // Where it searches when it is not holding on: a slow Lissajous round its home point.
        float lat = r.LatAmp * (0.80f * MathF.Sin(r.W1 * t + r.P1) + 0.35f * MathF.Sin(r.W1 * 2.3f * t + r.P2));
        float ax = r.AxAmp * MathF.Sin(r.W2 * t + r.P2);
        Vector2 wander = r.Home + perp * lat + r.Inward * ax;
        float wanderHead = MathF.Atan2(r.Inward.Y, r.Inward.X) + 0.8f * MathF.Sin(r.W1 * 0.7f * t + r.P2);
        float wanderCurl = r.CurlAmp * (0.75f * MathF.Sin(r.W1 * 0.55f * t + r.CurlPhase) + 0.30f * MathF.Sin(r.W2 * 1.4f * t));

        goal = wander; head = wanderHead; curl = wanderCurl; tension = 0f; goalOmega = 3.0f;

        switch (r.Mode)
        {
            case Mode.Wander:
                if (time >= r.NextGripAt && t > r.Grow + 0.4f && CountGrippers() < MaxGrippers)
                {
                    if (PickGrip(r, size, px, time)) { r.Mode = Mode.Reach; r.ModeTime = time; }
                    else r.NextGripAt = time + 1.5f;
                }
                break;

            case Mode.Reach:
            {
                goal = r.GripPoint; head = GripHeading(r, size); curl = 0f; tension = 0.55f; goalOmega = 2.9f;
                float arrived = Vector2.Distance(r.Goal, r.GripPoint);
                Vector2 tipNow = r.Strand.Pos[StrandNodes - 1];
                bool near = Vector2.Distance(tipNow, r.GripPoint) < 22f * px;
                if ((arrived < 12f * px && near && time - r.ModeTime > 0.6f))
                {
                    r.Mode = Mode.Grip; r.GripStart = time; r.PinFrom = tipNow; r.ModeTime = time;
                    // Carry the tip's velocity into the pin, but never so much that the blend overshoots the point.
                    float reachDist = Vector2.Distance(tipNow, r.GripPoint);
                    float cap = 3f * reachDist / PinSeconds;
                    r.PinVel = r.TipVelocity.LengthSquared() > cap * cap ? Vector2.Normalize(r.TipVelocity) * cap : r.TipVelocity;
                    r.Agitation = 0.9f;
                }
                else if (time - r.ModeTime > 3.2f) { r.Mode = Mode.Release; r.ModeTime = time; }   // it could not get there; let go
                break;
            }

            case Mode.Grip:
                goal = r.GripPoint; head = GripHeading(r, size); curl = 0f; tension = 1f; goalOmega = 4.0f;
                if (time - r.GripStart > r.GripHold) { r.Mode = Mode.Release; r.ModeTime = time; }
                break;

            case Mode.Release:
                if (time - r.ModeTime > 1.3f)
                {
                    r.Mode = Mode.Wander; r.ModeTime = time;
                    r.NextGripAt = time + DrawHelpers.HashRange(r.Seed + (int)(time * 10f), 4.0f, 9.5f);
                }
                break;
        }
    }

    private int CountGrippers()
    {
        int n = 0;
        for (int i = 0; i < _count; i++) if (_rigs[i].Mode == Mode.Reach || _rigs[i].Mode == Mode.Grip) n++;
        return n;
    }

    /// <summary>The tip arrives heading out through the border it is about to hold, hooked a little to one side.</summary>
    private static float GripHeading(Rig r, Vector2 size)
    {
        Vector2 outward = -ScreenEdges.Inward(ScreenEdges.Nearest(size, r.GripPoint));
        return MathF.Atan2(outward.Y, outward.X) + 0.55f * r.GripSide;
    }

    /// <summary>Picks a border point to grip: within reach but a real stretch away, clear of corners and other grips, not across the middle.</summary>
    private bool PickGrip(Rig r, Vector2 size, float px, float time)
    {
        float shortSide = MathF.Min(size.X, size.Y);
        float inset = shortSide * GripInsetFrac;
        Vector2 centre = size * 0.5f;
        int salt = unchecked(r.Seed + (int)(time * 100f));

        Vector2 best = default; float bestScore = -1f;
        const int N = 48;
        for (int i = 0; i < N; i++)
        {
            float p = (i + DrawHelpers.Hash01(salt + i * 13)) * (4f / N);
            Vector2 pt = ScreenEdges.FromPerimeter(size, p, -inset, out ScreenEdge edge);
            float along = (edge == ScreenEdge.Top || edge == ScreenEdge.Bottom) ? pt.X / size.X : pt.Y / size.Y;
            if (along < 0.07f || along > 0.93f) continue;                       // not in a corner

            float d = Vector2.Distance(pt, r.Root);
            if (d < GripMinReach * r.Length || d > GripMaxReach * r.Length) continue;

            if (DistanceToSegment(centre, r.Root, pt) < 0.20f * shortSide) continue;   // keep the middle clear

            bool crowded = false;
            for (int k = 0; k < _count; k++)
            {
                var o = _rigs[k];
                if (o == r || (o.Mode != Mode.Reach && o.Mode != Mode.Grip)) continue;
                if (Vector2.Distance(o.GripPoint, pt) < GripSpacing * px) { crowded = true; break; }
            }
            if (crowded) continue;

            float score = DrawHelpers.Hash01(salt + i * 31 + 7);
            if (score > bestScore) { bestScore = score; best = pt; }
        }
        if (bestScore < 0f) return false;

        r.GripPoint = best;
        r.GripHold = DrawHelpers.HashRange(salt + 5, 2.4f, 5.2f);
        r.GripSide = DrawHelpers.Hash01(salt + 6) < 0.5f ? -1f : 1f;
        return true;
    }

    private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Vector2.Dot(p - a, ab) / MathF.Max(1e-4f, ab.LengthSquared());
        return Vector2.Distance(p, a + ab * Math.Clamp(t, 0f, 1f));
    }

    // ---- The target curve ----

    /// <summary>Fills the strand's target with this frame's shape (equal-length links) and returns its length; spare length becomes bulge, travelling wave and tip curl.</summary>
    private float BuildTarget(Rig r, float px, float time, float t, out float chord)
    {
        // The arm contracts and relaxes a little; gripping tugs it harder.
        float breath = 1f + 0.035f * MathF.Sin(0.7f * time + r.BreathPhase);
        // Weighted smoothly by how firmly it holds on: a threshold here would step the arm's length every time tension crossed it.
        float holding = DrawHelpers.Smooth((r.Tension - 0.6f) / 0.4f);
        float tug = holding * (0.5f + 0.5f * MathF.Sin(MathF.Tau * (time - r.GripStart) / 2.5f));
        float lenWant = r.Length * breath * (1f - 0.09f * tug);

        // Holding on, the arm may not contract below the distance to the point it is holding: that would stretch it.
        Vector2 root = r.Root;
        if (r.Mode == Mode.Reach || r.Mode == Mode.Grip)
            lenWant = MathF.Max(lenWant, Vector2.Distance(root, r.GripPoint) * 1.04f);

        // The goal may not be further than the arm can reach.
        Vector2 toGoal = r.Goal - root;
        float c = toGoal.Length();
        float maxReach = lenWant * 0.985f;
        if (c > maxReach) { toGoal *= maxReach / c; c = maxReach; }
        if (c < 1f) { toGoal = r.Inward; c = 1f; }
        Vector2 goal = root + toGoal;
        chord = c;
        lenWant = MathF.Max(lenWant, c * 1.012f);

        Vector2 dirChord = toGoal / c;
        Vector2 perp = new(-dirChord.Y, dirChord.X);
        Vector2 d1 = new(MathF.Cos(r.HeadAngle), MathF.Sin(r.HeadAngle));

        Vector2 p0 = root, p1 = root + r.Inward * (0.34f * c), p2 = goal - d1 * (0.30f * c), p3 = goal;

        // The bulge's profile: a blend of one arch and one S, rotating slowly through every combination.
        float ang = r.ShapeAngle0 + r.ShapeRate * time;
        float fa = MathF.Cos(ang), fb = MathF.Sin(ang);
        // Gripping calms only the S term so the hook reads as an arch (scaling the arch term by its sign would flip the whole bulge at zero).
        fb *= 1f - 0.65f * r.Tension;

        Span<Vector2> bez = stackalloc Vector2[GuideSamples + 1];
        Span<float> prof = stackalloc float[GuideSamples + 1];
        Span<Vector2> pts = stackalloc Vector2[GuideSamples + 1];
        for (int k = 0; k <= GuideSamples; k++)
        {
            float u = k / (float)GuideSamples;
            float v = 1f - u;
            bez[k] = v * v * v * p0 + 3f * v * v * u * p1 + 3f * v * u * u * p2 + u * u * u * p3;
            prof[k] = fa * MathF.Sin(MathF.PI * u) + fb * MathF.Sin(MathF.Tau * u);
        }

        // Spend spare length: choose the bulge amplitude that makes the curve as long as the tentacle. Length is CONVEX in amplitude (a wrong-side bulge first straightens a curved approach), so find the minimum and take the one root beyond it: unique, so the arm can't pop between solutions.
        float scale = SolveBulge(bez, prof, perp, lenWant, c);
        Arc(bez, prof, perp, scale, pts);

        // The tip's curl: re-integrate the last quarter with its heading turned progressively, which keeps its length.
        float curl = r.Curl;
        if (MathF.Abs(curl) > 0.002f)
        {
            int hinge = (int)(GuideSamples * 0.74f);
            Vector2 prev = pts[hinge];
            for (int k = hinge + 1; k <= GuideSamples; k++)
            {
                Vector2 seg = pts[k] - pts[k - 1];
                float segLen = seg.Length();
                float a = MathF.Atan2(seg.Y, seg.X) + curl * DrawHelpers.Smooth((k - hinge) / (float)(GuideSamples - hinge));
                Vector2 next = prev + new Vector2(MathF.Cos(a), MathF.Sin(a)) * segLen;
                pts[k] = next;
                prev = next;
            }
        }

        // A wave running down to the tip, growing toward it and calming when it is holding on.
        float arcTotal = 0f;
        Span<float> cum = stackalloc float[GuideSamples + 1];
        for (int k = 1; k <= GuideSamples; k++) { arcTotal += Vector2.Distance(pts[k], pts[k - 1]); cum[k] = arcTotal; }
        float waveAmp = 0.026f * r.Length * (1f - 0.75f * r.Tension);
        if (waveAmp > 0.5f)
        {
            Span<Vector2> shifted = stackalloc Vector2[GuideSamples + 1];
            for (int k = 0; k <= GuideSamples; k++)
            {
                Vector2 tan = pts[Math.Min(GuideSamples, k + 1)] - pts[Math.Max(0, k - 1)];
                float tl = tan.Length();
                Vector2 n = tl > 1e-4f ? new Vector2(-tan.Y, tan.X) / tl : perp;
                float u = arcTotal > 1f ? cum[k] / arcTotal : 0f;
                float amp = waveAmp * MathF.Pow(u, 1.3f);
                shifted[k] = pts[k] + n * (amp * MathF.Sin(MathF.Tau * 1.15f * u - 1.7f * time + r.WavePhase));
            }
            for (int k = 0; k <= GuideSamples; k++) pts[k] = shifted[k];
            arcTotal = 0f;
            for (int k = 1; k <= GuideSamples; k++) { arcTotal += Vector2.Distance(pts[k], pts[k - 1]); cum[k] = arcTotal; }
        }

        // Cut into equal-length links, matching the strand's.
        var target = r.Strand.Target;
        int seg2 = 0;
        for (int i = 0; i < StrandNodes; i++)
        {
            float want = arcTotal * i / (StrandNodes - 1f);
            while (seg2 < GuideSamples - 1 && cum[seg2 + 1] < want) seg2++;
            float span = cum[seg2 + 1] - cum[seg2];
            float f = span > 1e-4f ? (want - cum[seg2]) / span : 0f;
            target[i] = Vector2.Lerp(pts[seg2], pts[seg2 + 1], f);
        }
        return arcTotal;
    }

    private static float SolveBulge(ReadOnlySpan<Vector2> bez, ReadOnlySpan<float> prof, Vector2 perp, float want, float chord)
    {
        // Bracket: an amplitude long enough that the curve is at least as long as wanted.
        float hi = MathF.Max(4f, chord * 0.03f);
        for (int i = 0; i < 12 && ArcLength(bez, prof, perp, hi) < want; i++) hi *= 2f;

        // Least length, by ternary search (valid because the length is convex in the amplitude).
        float a = 0f, b = hi;
        for (int i = 0; i < 14; i++)
        {
            float m1 = a + (b - a) / 3f, m2 = b - (b - a) / 3f;
            if (ArcLength(bez, prof, perp, m1) < ArcLength(bez, prof, perp, m2)) b = m2; else a = m1;
        }
        float lo = 0.5f * (a + b);
        if (ArcLength(bez, prof, perp, lo) >= want) return lo;           // it cannot be this short: take the shortest it can be
        if (ArcLength(bez, prof, perp, hi) < want) return hi;            // or this long (cannot happen within the bracket, but never loop on it)

        for (int i = 0; i < 24; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (ArcLength(bez, prof, perp, mid) < want) lo = mid; else hi = mid;
        }
        return 0.5f * (lo + hi);
    }

    private static float ArcLength(ReadOnlySpan<Vector2> bez, ReadOnlySpan<float> prof, Vector2 perp, float scale)
    {
        float len = 0f;
        Vector2 prev = bez[0] + perp * (scale * prof[0]);
        for (int k = 1; k < bez.Length; k++)
        {
            Vector2 cur = bez[k] + perp * (scale * prof[k]);
            len += Vector2.Distance(cur, prev);
            prev = cur;
        }
        return len;
    }

    /// <summary>Builds the Bezier offset by <paramref name="scale"/> times the bulge profile into <paramref name="outPts"/>, and returns its length.</summary>
    private static float Arc(ReadOnlySpan<Vector2> bez, ReadOnlySpan<float> prof, Vector2 perp, float scale, Span<Vector2> outPts)
    {
        float len = 0f;
        for (int k = 0; k < bez.Length; k++)
        {
            outPts[k] = bez[k] + perp * (scale * prof[k]);
            if (k > 0) len += Vector2.Distance(outPts[k], outPts[k - 1]);
        }
        return len;
    }

    // ---- Springs ----

    /// <summary>Critically damped spring: reaches the target with no overshoot, at a rate set by omega.</summary>
    private static void Spring(ref Vector2 x, ref Vector2 v, Vector2 target, float omega, float dt)
    {
        Vector2 a = (target - x) * (omega * omega) - v * (2f * omega);
        v += a * dt;
        x += v * dt;
    }

    private static void Spring(ref float x, ref float v, float target, float omega, float dt)
    {
        float a = (target - x) * (omega * omega) - v * (2f * omega);
        v += a * dt;
        x += v * dt;
    }

    /// <summary>The same for an angle, taking the short way round.</summary>
    private static void SpringAngle(ref float x, ref float v, float target, float omega, float dt)
    {
        float diff = target - x;
        diff -= MathF.Tau * MathF.Round(diff / MathF.Tau);
        float a = diff * (omega * omega) - v * (2f * omega);
        v += a * dt;
        x += v * dt;
    }

    /// <summary>Cubic Hermite from <paramref name="p0"/> (leaving with tangent <paramref name="m0"/>) to <paramref name="p1"/> (arriving at rest).</summary>
    private static Vector2 Hermite(Vector2 p0, Vector2 m0, Vector2 p1, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return (2f * t3 - 3f * t2 + 1f) * p0 + (t3 - 2f * t2 + t) * m0 + (-2f * t3 + 3f * t2) * p1;
    }



    // ---- Impacts ----

    /// <summary>Slime thrown off where a tentacle breaks through an edge or lands on one.</summary>
    private static void Splat(EffectScene scene, Vector2 at, Vector2 direction, float strength,
                              float alpha, float time, int seed, Vector4? colorOverride)
    {
        scene.AddImpact(new ImpactPrimitive
        {
            StrokeRole = PrimitiveRole.MainStroke,
            Position = at,
            Direction = direction,
            Strength = Math.Clamp(strength, 0f, 1f),
            Brightness = alpha,
            Seed = unchecked(seed + (int)(time * 1000f)),
            ColorOverride = colorOverride,
        });
    }

    // ---- Layout ----

    private void BuildLayout(int castSeed, Vector2 size)
    {
        float shortSide = MathF.Min(size.X, size.Y);
        float overhang = shortSide * 0.075f;
        _count = Math.Min(MaxTentacles, Archetypes.Length);

        for (int i = 0; i < _count; i++)
            BuildRig(_rigs[i], i, castSeed, size, overhang, shortSide);

        // Far ones are painted first.
        for (int i = 0; i < _count; i++) _drawOrder[i] = i;
        Array.Sort(_drawOrder, 0, _count, Comparer<int>.Create((a, b) => _rigs[b].Depth.CompareTo(_rigs[a].Depth)));
    }

    private static void BuildRig(Rig r, int index, int castSeed, Vector2 size, float overhang, float shortSide)
    {
        var a = Archetypes[index];
        int s = unchecked(castSeed + index * 977 + 29);
        float H(int salt, float lo, float hi) => DrawHelpers.HashRange(s + salt, lo, hi);

        ScreenEdge edge = a.Far ? (ScreenEdge)(H(40, 0f, 1f) < 0.34f ? 3 : (H(41, 0f, 1f) < 0.5f ? 1 : 2)) : a.Edge;   // far one: left, right or bottom
        float along = a.Far ? H(1, 0.15f, 0.85f) : H(1, a.AlongLo, a.AlongHi);

        r.Root = ScreenEdges.Anchor(size, edge, along, overhang);
        r.Inward = ScreenEdges.Inward(edge);
        r.Length = shortSide * H(2, a.LengthLo, a.LengthHi);
        r.DiameterFrac = H(3, a.DiameterLo, a.DiameterHi);
        r.Depth = a.Far ? H(4, 0.55f, 0.85f) : H(4, 0f, 0.14f);
        r.Seed = s;
        r.Phase = H(5, 0f, MathF.Tau);

        // Home: a point out along its inward direction, off to one side a little, kept clear of the border.
        Vector2 perp = new(-r.Inward.Y, r.Inward.X);
        Vector2 home = r.Root + r.Inward * (r.Length * HomeReach) + perp * (r.Length * H(6, -0.07f, 0.07f));
        float m = shortSide * 0.04f;
        r.Home = new Vector2(Math.Clamp(home.X, m, size.X - m), Math.Clamp(home.Y, m, size.Y - m));

        r.Delay = H(7, 0f, 0.55f);
        r.Grow = H(8, 1.1f, 1.6f);
        r.FirstGripDelay = H(9, 2.0f, 8.0f);

        r.LatAmp = r.Length * WanderLateral * H(10, 0.75f, 1.1f);
        r.AxAmp = r.Length * WanderAxial * H(11, 0.7f, 1.1f);
        r.W1 = H(12, 0.45f, 0.72f);
        r.W2 = H(13, 0.55f, 0.95f);
        r.P1 = H(14, 0f, MathF.Tau);
        r.P2 = H(15, 0f, MathF.Tau);
        r.ShapeAngle0 = H(16, 0f, MathF.Tau);
        r.ShapeRate = H(17, 0.14f, 0.28f) * (H(18, 0f, 1f) < 0.5f ? -1f : 1f);
        r.CurlAmp = H(19, 0.6f, 1.3f);
        r.CurlPhase = H(20, 0f, MathF.Tau);
        r.UnfurlSign = H(21, 0f, 1f) < 0.5f ? -1f : 1f;
        r.TwistAmp = H(22, 0.5f, 1.2f);
        r.TwistPhase = H(23, 0f, MathF.Tau);
        r.BreathPhase = H(24, 0f, MathF.Tau);
        r.WavePhase = H(25, 0f, MathF.Tau);

        r.Started = false;
        r.Entered = false;
        r.Mode = Mode.Wander;
        r.Reveal = 0f;
        r.Agitation = 0f;
        r.Path.Count = 0;
    }
}
