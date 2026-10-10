using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>Heavy: iron chains flung across the screen like arrows, then hanging. Kinematic shot (can't fold), released into Verlet at the far anchor.</summary>
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

    private const int MaxChains = 5;
    private const int StrandNodes = 12;
    private const int PathSamples = 56;

    private const float VignetteAlpha = 0.12f;
    private const float GloomAlpha = 0.28f;
    private const float GloomDepth = 0.12f;

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

    private static readonly uint Gloom = DrawHelpers.Pack(0.012f, 0.016f, 0.024f);
    private static readonly uint Vignette = DrawHelpers.Pack(0.010f, 0.014f, 0.022f);

    private static readonly float[] SwayShape = BuildSwayShape();

    private static float[] BuildSwayShape()
    {
        var t = new float[PathSamples];
        for (int i = 0; i < PathSamples; i++)
            t[i] = MathF.Sin(MathF.PI * i / (PathSamples - 1f));
        return t;
    }

    // Fixed composition, so the screen is always covered evenly: (origin edge + range, destination edge + range); index 4 is a wildcard (BuildRig).
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
                Phase = rig.Phase,
                Depth = rig.Depth,
                Agitation = rig.Agitation,
                EmitEdgeReach = EmitEdgeReach,
                EmitEndReach = EmitEndReach,
                ColorOverride = colorOverride,
            });
        }
    }

    private static void EmitAtmosphere(EffectScene scene, Vector2 size, float alpha, float time, float age, Vector4? colorOverride)
    {
        float castIn = DrawHelpers.Saturate(age / 0.7f);

        scene.RequestVignette(Vignette, 0.12f, alpha * VignetteAlpha * castIn, priority: 20, colorOverride);

        // Gloom pooled at the bottom edge, breathing slowly as if the weight were shifting.
        float depth = size.Y * GloomDepth * (1f + 0.15f * DrawHelpers.Pulse(time, 4.6f)) * castIn;
        if (depth <= 1f) return;

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

    private static void Step(EffectScene scene, Rig rig, Vector2 size, float px,
                             float alpha, float time, float dt, float age, Vector4? colorOverride)
    {
        var strand = rig.Strand;
        float t = age - rig.Delay;

        if (!rig.Launched) rig.Launch(2500f * px, HangDrag);

        // Head: an arrow. Constant speed after a short launch ramp, stopped dead by the far anchor.
        float tau = DrawHelpers.Saturate(t / rig.Flight);
        float bow = rig.ArcSide * FlightBow * rig.ChordLen * MathF.Sin(MathF.PI * tau);
        Vector2 head = rig.Start + (rig.End - rig.Start) * StrandRig.Travel(tau) + rig.ChordPerp * bow;

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
            head.Y += 5.5f * px * MathF.Sin(time * 0.85f + rig.SwayPhase);   // the load on the far end eases and settles
        }

        bool settled = rig.Landed && (time - rig.LandTime) > SettlePhysicsSeconds;

        if (!settled)
        {
            float dist = Vector2.Distance(head, rig.Start);
            float flightExtra = dist * FlightSag;
            float extra = flightExtra;
            if (rig.Landed)
            {
                float release = DrawHelpers.Smooth((time - rig.LandTime) / ReleaseSeconds);
                extra = flightExtra + (rig.ChordLen * rig.Slack - flightExtra) * release;
            }

            strand.Length = MathF.Max(2f, dist + extra);

            // In flight the guide is a very slightly sagging taut cable; a near-vertical span bows to the seeded side.
            if (!rig.Landed)
                StrandGuide.Drape(rig.Start, head, rig.ChordDir, extra, new DrapeStyle(rig.ArcSide >= 0f ? 1f : -1f, Downhill: true), 0f, strand.Target);

            strand.SetEnds(rig.Start, head);

            if (!rig.Landed) strand.Drive(strand.Target, dt);
            else             strand.Step(dt);
        }

        rig.Publish(PathSamples, settled);
        if (rig.Landed) ApplySway(rig, time, px);

        if (landedNow)
        {
            Shock(rig, px, strength: 0.1f, time);
            rig.ReportEnd(scene, size, fromStart: false, strength: 1f, alpha, unchecked(rig.Seed + (int)(time * 1000f)), colorOverride);
        }

        rig.Agitation *= MathF.Exp(-dt / 0.95f);
    }

    // Wind displaces the published path sideways, delayed along its length, zero at the anchors; applied after the sim, since a chain's response to forces varies with sag and tension.
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

    // A hard stop sends a TRANSVERSE shock down a taut chain; never push along it (tension only: an axial push buckles into a loop).
    private static void Shock(Rig rig, float px, float strength, float time)
    {
        var strand = rig.Strand;

        Vector2 along = Vector2.Normalize(strand.Pos[StrandNodes - 1] - strand.Pos[StrandNodes - 4]);
        if (float.IsNaN(along.X)) along = new Vector2(1f, 0f);
        Vector2 side = new(-along.Y, along.X);
        float sign = DrawHelpers.Hash01(rig.Seed + 710 + (int)(time * 10f)) < 0.5f ? -1f : 1f;
        float v = 520f * px * strength * (1f - 0.35f * rig.Depth);

        // One smooth bump of sideways velocity (alternating signs on neighbours would fold the end into a zigzag).
        const int Reach = 6;
        for (int k = 1; k <= Reach; k++)
        {
            float bump = MathF.Sin(MathF.PI * k / (Reach + 1f));
            strand.Impulse(StrandNodes - 1 - k, side * (v * bump * sign));
        }
    }

    // 4 or 5 chains per cast (the fifth is a wildcard); one random chain is the far one: smaller, hazier, painted first.
    private void BuildLayout(int castSeed, Vector2 size)
    {
        float overhang = MathF.Min(size.X, size.Y) * 0.07f;

        _count = 4 + (int)(DrawHelpers.Hash01(castSeed + 1) * 2f);

        int farIdx = Math.Min((int)(DrawHelpers.Hash01(castSeed + 200) * _count), _count - 1);

        for (int i = 0; i < _count; i++)
            BuildRig(_rigs[i], i, castSeed, size, overhang, farIdx == i);

        StrandRig.SortFarFirst(_rigs, _drawOrder, _count);
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
        float px = MathF.Min(size.X, size.Y) / 1080f;
        r.Flight = Math.Clamp(Vector2.Distance(a, b) / (H(7, 4300f, 6200f) * px), 0.20f, 0.50f);
        r.ArcSide = H(8, -0.07f, 0.07f);
        r.SwayPhase = H(9, 0f, MathF.Tau);
        r.SwayHz = H(10, 0.12f, 0.20f);
    }
}
