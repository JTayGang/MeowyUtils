using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Heavy: iron chains flung across the screen like arrows, then hanging.</summary>
public sealed class HeavyEffect : SceneEffect, IHasSwappableSlots
{
    public IReadOnlyList<SwappableSlot> Slots { get; } = new SwappableSlot[]
    {
        new("Stroke",   PrimitiveRole.MainStroke, "Chains", "stroke.chain", Hero: true),
        new("Particle", PrimitiveRole.Dust,       "Dust",   "particle.dust"),
        new("Region",   PrimitiveRole.MainStroke, "Gloom",  "region.edge-glow", "EdgeGlow"),
    };

    private const int MaxChains = 5;
    private const int StrandNodes = 12;
    private const int PathSamples = 56;


    private const float MaxThrowDelay = 0.3f;
    private const float FlightBow = 0.25f;
    private const float FlightSag = 0.0012f;
    private const float ReleaseSeconds = 0.18f;
    private const float HangDrag = 1.85f;

    // After this long hanging the sim is frozen and only the sway moves the published path.
    private const float SettlePhysicsSeconds = 5.0f;

    // Debris shows only this close to a screen edge, and within this arc distance of a chain's ends (px at 1080p).
    private const float EmitEdgeReach = 190f;
    private const float EmitEndReach = 300f;

    // Wind: a slow swell per chain plus gusts, so no chain is ever still.
    private const float SwayAmp = 15f;
    private const float SwayGustHz = 0.30f;

    private static readonly MurkStyle Mood = new(DrawHelpers.Pack(0.010f, 0.014f, 0.022f), 0.12f, DrawHelpers.Pack(0.012f, 0.016f, 0.024f), 0.28f, 0.12f, 0.7f, 0.15f, 4.6f);

    private static readonly float[] SwayShape = BuildSwayShape();

    private static float[] BuildSwayShape()
    {
        var t = new float[PathSamples];
        for (int i = 0; i < PathSamples; i++)
            t[i] = MathF.Sin(MathF.PI * i / (PathSamples - 1f));
        return t;
    }

    // Fixed composition, so the screen is always covered evenly: (origin edge + range, destination edge + range)
    private static readonly (ScreenEdge From, float FromLo, float FromHi,
                             ScreenEdge To,   float ToLo,   float ToHi)[] Archetypes =
    {
        (ScreenEdge.Top,    0.08f, 0.62f, ScreenEdge.Right,  0.20f, 0.85f),
        (ScreenEdge.Top,    0.38f, 0.92f, ScreenEdge.Left,   0.20f, 0.85f),
        (ScreenEdge.Right,  0.10f, 0.62f, ScreenEdge.Bottom, 0.15f, 0.80f),
        (ScreenEdge.Left,   0.10f, 0.62f, ScreenEdge.Bottom, 0.20f, 0.85f),
    };

    private sealed class Rig : StrandRig
    {
        public float Slack, ArcSide, LinkFrac, LandTime, SwayPhase, SwayHz;

        public Rig() : base(StrandNodes, PathSamples) { }
    }

    private readonly RigSet<Rig> _set = new(MaxChains);

    private readonly CastTracker _cast = new();

    public HeavyEffect() : base(DebuffKind.Heavy, "Heavy",
        "Iron chains are flung across the screen and snap taut, then strain under their own weight.",
        2, "Heavy",
        keywords: ["chains", "shackle", "shackled"]) { }

    public override void Emit(EffectScene scene, Vector2 screenSize, float alpha, float time, float dt, Vector4? colorOverride)
    {
        var f = _cast.Frame(screenSize, alpha, time, dt, colorOverride);
        if (f.Fresh || f.Resized) BuildLayout(f.Seed, screenSize);

        scene.Murk(in f, in Mood);

        for (int n = 0; n < _set.Count; n++)
        {
            var rig = _set.At(n);
            if (f.Age < rig.Delay) continue;

            Step(scene, rig, in f);

            if (rig.Path.Count < 2) continue;

            scene.AddStroke(new StrokePrimitive
            {
                Path = rig.Path,
                Role = PrimitiveRole.MainStroke,
                Reveal = 1f,
                WidthHint = f.Short * rig.LinkFrac,
                Brightness = f.Alpha,
                Seed = rig.Seed,
                Phase = rig.Phase,
                Depth = rig.Depth,
                Agitation = rig.Agitation,
                EmitEdgeReach = EmitEdgeReach,
                EmitEndReach = EmitEndReach,
                ColorOverride = f.Color,
            });
        }
    }


    private static void Step(EffectScene scene, Rig rig, in CastFrame f)
    {
        var strand = rig.Strand;
        float t = f.Age - rig.Delay;

        if (!rig.Launched) rig.Launch(2500f * f.Px, HangDrag);

        // Head: an arrow. Constant speed after a short launch ramp, stopped dead by the far anchor.
        float tau = DrawHelpers.Saturate(t / rig.Flight);
        float bow = rig.ArcSide * FlightBow * rig.ChordLen * MathF.Sin(MathF.PI * tau);
        Vector2 head = rig.Start + (rig.End - rig.Start) * StrandRig.Travel(tau) + rig.ChordPerp * bow;

        bool landedNow = false;
        if (tau >= 1f && !rig.Landed)
        {
            rig.Landed = true;
            landedNow = true;
            rig.LandTime = f.Time;
            rig.Agitation = 1f;
        }

        if (rig.Landed)
        {
            head = rig.End;
            head.Y += 5.5f * f.Px * MathF.Sin(f.Time * 0.85f + rig.SwayPhase);  // the load on the far end eases and settles
        }

        bool settled = rig.Landed && (f.Time - rig.LandTime) > SettlePhysicsSeconds;

        if (!settled)
        {
            float dist = Vector2.Distance(head, rig.Start);
            float flightExtra = dist * FlightSag;
            float extra = flightExtra;
            if (rig.Landed)
            {
                float release = DrawHelpers.Smooth((f.Time - rig.LandTime) / ReleaseSeconds);
                extra = flightExtra + (rig.ChordLen * rig.Slack - flightExtra) * release;
            }

            strand.Length = MathF.Max(2f, dist + extra);

            // In flight the guide is a very slightly sagging taut cable; a near-vertical span bows to the seeded side.
            if (!rig.Landed)
                StrandGuide.Drape(rig.Start, head, rig.ChordDir, extra, new DrapeStyle(rig.ArcSide >= 0f ? 1f : -1f, Downhill: true), 0f, strand.Target);

            strand.SetEnds(rig.Start, head);

            if (!rig.Landed) strand.Drive(strand.Target, f.Dt);
            else             strand.Step(f.Dt);
        }

        rig.Publish(PathSamples, settled);
        if (rig.Landed) ApplySway(rig, f.Time, f.Px);

        if (landedNow)
        {
            Shock(rig, f.Px, strength: 0.1f, f.Time);
            rig.ReportEnd(scene, f.Size, fromStart: false, strength: 1f, f.Alpha, unchecked(rig.Seed + (int)(f.Time * 1000f)), f.Color);
        }

        rig.Agitation *= MathF.Exp(-f.Dt / 0.95f);
    }

    // Wind displaces the published path sideways, delayed along its length, zero at the anchors; applied after the sim
    private static void ApplySway(Rig rig, float time, float px)
    {
        var path = rig.Path;
        float amp = SwayAmp * px * DrawHelpers.Saturate((time - rig.LandTime) / 1.2f);
        if (amp <= 0.01f) return;

        const int n = PathSamples;
        float invN = 1f / (n - 1f);
        float swellPhase0 = MathF.Tau * rig.SwayHz * time + rig.SwayPhase;
        float gustPhase0 = time * SwayGustHz + rig.SwayPhase;
        float gustSeed = rig.Seed * 0.013f;

        Span<Vector2> offset = stackalloc Vector2[n];
        for (int i = 0; i < n; i++)
        {
            Vector2 tan = path.Points[Math.Min(n - 1, i + 1)] - path.Points[Math.Max(0, i - 1)];
            float len = tan.Length();
            if (len < 1e-3f) { offset[i] = default; continue; }

            float u = i * invN;
            float swell = MathF.Sin(swellPhase0 - u * 1.6f);
            float gust = Noise.Value(gustPhase0 - u * 1.2f, gustSeed);
            offset[i] = new Vector2(-tan.Y, tan.X) / len * (amp * (0.6f * swell + 0.7f * gust) * SwayShape[i]);
        }

        for (int i = 0; i < n; i++) path.Points[i] += offset[i];
        path.BuildArc();
    }

    // A hard stop sends a TRANSVERSE shock down a taut chain
    private static void Shock(Rig rig, float px, float strength, float time)
    {
        var strand = rig.Strand;

        Vector2 along = Vector2.Normalize(strand.Pos[StrandNodes - 1] - strand.Pos[StrandNodes - 4]);
        if (float.IsNaN(along.X)) along = new Vector2(1f, 0f);
        Vector2 side = new(-along.Y, along.X);
        float sign = DrawHelpers.Sign(rig.Seed + 710 + (int)(time * 10f));
        float v = 520f * px * strength * (1f - 0.35f * rig.Depth);

        // One smooth bump of sideways velocity (alternating signs on neighbours would fold the end into a zigzag).
        const int Reach = 6;
        for (int k = 1; k <= Reach; k++)
        {
            float bump = MathF.Sin(MathF.PI * k / (Reach + 1f));
            strand.Impulse(StrandNodes - 1 - k, side * (v * bump * sign));
        }
    }

    // 4 or 5 chains per cast (the fifth is a wildcard); one is the far chain: smaller, hazier, painted first.
    private void BuildLayout(int castSeed, Vector2 size)
    {
        float overhang = MathF.Min(size.X, size.Y) * 0.07f;

        _set.Count = 4 + (int)(DrawHelpers.Hash01(castSeed + 1) * 2f);

        int farIdx = Math.Min((int)(DrawHelpers.Hash01(castSeed + 200) * _set.Count), _set.Count - 1);

        for (int i = 0; i < _set.Count; i++)
            BuildRig(_set.Rigs[i], i, castSeed, size, overhang, farIdx == i);

        _set.Sort();
    }

    private static void BuildRig(Rig r, int index, int castSeed, Vector2 size, float overhang, bool far)
    {
        int s = unchecked(castSeed + index * 977 + 17);
        float H(int salt, float lo, float hi) => DrawHelpers.HashRange(s + salt, lo, hi);

        Vector2 a, b;
        if (index >= Archetypes.Length)
        {
            // Wildcard: any two perimeter points at least one full edge apart, so no corner stubs.
            float p = H(1, 0f, 4f);
            a = ScreenEdges.FromPerimeter(size, p, overhang, out _);
            b = ScreenEdges.FromPerimeter(size, p + H(2, 1.0f, 3.0f), overhang, out _);
        }
        else
        {
            var (from, fromLo, fromHi, to, toLo, toHi) = Archetypes[index];
            a = ScreenEdges.Anchor(size, from, H(1, fromLo, fromHi), overhang);
            b = ScreenEdges.Anchor(size, to,   H(2, toLo,   toHi),   overhang);
        }

        r.Reset(s);
        r.SetChord(a, b);

        r.Depth = far ? H(3, 0.55f, 0.90f) : H(3, 0.00f, 0.18f);
        r.LinkFrac = far ? H(4, 0.034f, 0.044f) : H(4, 0.052f, 0.072f);
        float slack = far ? H(5, 0.04f, 0.09f) : H(5, 0.03f, 0.13f);
        // Spare length becomes sag across a wide span; on a steep one a real chain would just hang straight.
        float horizontal = (b - a).LengthSquared() > 1f ? MathF.Abs(r.ChordDir.X) : 1f;
        r.Slack = slack * (0.30f + 0.70f * horizontal);
        r.Delay = H(6, 0f, MaxThrowDelay);
        // Speed is what's chosen, not flight time, so a long span isn't faster than a short one.
        float px = DrawHelpers.PixelScale(size);
        r.Flight = Math.Clamp(Vector2.Distance(a, b) / (H(7, 4300f, 6200f) * px), 0.20f, 0.50f);
        r.ArcSide = H(8, -0.07f, 0.07f);
        r.SwayPhase = H(9, 0f, MathF.Tau);
        r.SwayHz = H(10, 0.12f, 0.20f);
    }
}
