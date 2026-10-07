using System.Numerics;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs.Effects;

/// <summary>
/// Heavy: iron chains shot across the screen like arrows trailing a cable - a near-straight line
/// crossing in a fraction of a second, then slack and hanging under its own weight.
///
/// MOTION. The shot is kinematic (a taut, almost straight cable paid out behind a fast head), so
/// it cannot fold. At the far anchor the chain is released into VerletStrand physics: slack drops
/// out, a shock rings down it, wind sways it.
///
/// CHAINS and IMPACTS are hero slots; "heavy made of rope" swaps stroke.chain for a rope and gets
/// the same physics throwing fibres instead of sparks. Everything is reseeded from the cast start
/// time: chain count (4-5), anchors, slack, near/far, shot timing and wind phase.
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
    private const int StrandNodes = 12;
    private const int PathSamples = 56;

    // ---- mood (0 removes it) ----
    private const float VignetteAlpha = 0.12f;
    private const float GloomAlpha = 0.28f;
    private const float GloomDepth = 0.12f;

    // ---- the shot ----
    private const float MaxThrowDelay = 0.3f;
    private const float FlightBow = 0.25f;
    private const float FlightSag = 0.0012f;
    private const float ReleaseSeconds = 0.18f;
    private const float HangDrag = 1.85f;

    // Once a chain has been hanging this long, its simulation is frozen: the sway is applied to
    // the published path, so the visual is unchanged, but the solver iterations per frame are
    // skipped. The chain lands, rings down, and sways forever after; the physics is done.
    private const float SettlePhysicsSeconds = 5.0f;

    // Debris shows only this close to a screen edge (px at 1080p)...
    private const float EmitEdgeReach = 190f;
    // ...and within this arc distance of a chain's origin or landing point.
    private const float EmitEndReach = 300f;

    // ---- wind: a slow swell per chain plus gusts, so no chain is ever still ----
    private const float SwayAmp = 15f;
    private const float SwayGustHz = 0.30f;

    private static readonly uint Gloom = DrawHelpers.ToU32(0.012f, 0.016f, 0.024f, 1f);
    private static readonly uint Vignette = DrawHelpers.ToU32(0.010f, 0.014f, 0.022f, 1f);

    // Sway shape factor sin(pi*u) is fixed for each sample index, so precompute once instead of
    // calling MathF.Sin per sample per chain per frame in ApplySway.
    private static readonly float[] SwayShape = BuildSwayShape();

    private static float[] BuildSwayShape()
    {
        var t = new float[PathSamples];
        for (int i = 0; i < PathSamples; i++)
            t[i] = MathF.Sin(MathF.PI * i / (PathSamples - 1f));
        return t;
    }

    // Composition is fixed so the screen is always covered evenly. Each row is (origin edge +
    // range, destination edge + range), ranges as 0..1 along their edge. Origin ranges are held
    // away from the destination's corner so no chain becomes a stub in the corner it aimed at.
    private static readonly (ScreenEdge From, float FromLo, float FromHi,
                             ScreenEdge To,   float ToLo,   float ToHi)[] Archetypes =
    {
        (ScreenEdge.Top,    0.08f, 0.62f, ScreenEdge.Right,  0.20f, 0.85f),   // top → right
        (ScreenEdge.Top,    0.38f, 0.92f, ScreenEdge.Left,   0.20f, 0.85f),   // top → left
        (ScreenEdge.Right,  0.10f, 0.62f, ScreenEdge.Bottom, 0.15f, 0.80f),   // right → bottom
        (ScreenEdge.Left,   0.10f, 0.62f, ScreenEdge.Bottom, 0.20f, 0.85f),   // left → bottom
        // Index 4 is the wildcard, handled separately in BuildRig.
    };

    private sealed class Rig
    {
        public readonly VerletStrand Strand = new(StrandNodes);
        public readonly StrandPath Path = new(PathSamples);

        public Vector2 Start, End;
        public Vector2 ChordDir, ChordPerp;   // cached unit chord; fixed for the whole rig
        public float   ChordLen;
        public float   Slack;
        public float   Delay, Flight;
        public float   ArcSide;
        public float   LinkFrac;
        public float   Depth;
        public int     Seed;
        public float   Phase;                 // cached from Seed; constant for the rig's life

        public readonly StrandPath BasePath = new(PathSamples);
        public bool PathFrozen;

        public bool    Launched, Landed;
        public float   LandTime;
        public float   Agitation;
        public float   SwayPhase;
        public float   SwayHz;
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
            strand.Iterations = 8;
            strand.BendStiffness = 0.75f;
        }

        // Head: an arrow. Constant speed after a short launch ramp, stopped dead by the far anchor.
        float tau = DrawHelpers.Saturate(t / rig.Flight);
        Vector2 chord = rig.End - rig.Start;
        float chordLen = rig.ChordLen;
        Vector2 perp = rig.ChordPerp;

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

        bool settled = rig.Landed && (time - rig.LandTime) > SettlePhysicsSeconds;

        if (!settled)
        {
            float dist = Vector2.Distance(head, rig.Start);
            float flightExtra = dist * FlightSag;
            float extra = flightExtra;
            if (rig.Landed)
            {
                float k = DrawHelpers.Saturate((time - rig.LandTime) / ReleaseSeconds);
                float release = k * k * (3f - 2f * k);
                extra = flightExtra + (chordLen * rig.Slack - flightExtra) * release;
            }

            strand.Length = MathF.Max(2f, dist + extra);

            if (!rig.Landed) GuideShape(rig, head, extra);

            strand.SetEnds(rig.Start, head);

            if (!rig.Landed) strand.Drive(strand.Target, dt);
            else             strand.Step(dt);
        }

        // Path is refreshed every frame so ApplySway can write onto a clean base.
        if (settled)
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
        if (rig.Landed) ApplySway(rig, time, px);

        if (landedNow)
        {
            Shock(rig, px, strength: 0.1f, time);
            ReportImpact(scene, rig, size, strength: 1f, alpha, time, colorOverride);
        }

        rig.Agitation *= MathF.Exp(-dt / 0.95f);
    }

    /// <summary>
    /// Wind: a slow swell plus gusts displace the published path sideways, delayed along its
    /// length so they travel down it, zero at the anchors. Layered on the path after the sim
    /// rather than applied as a force: a simulated chain's resistance to sideways pushes varies
    /// several-fold with its sag and tension, so a force would give some chains a swing and
    /// others none, whereas this moves every chain the same and stacks on the landing ring-down.
    /// </summary>
    private static void ApplySway(Rig rig, float time, float px)
    {
        var path = rig.Path;
        int n = path.Count;
        float amp = SwayAmp * px * DrawHelpers.Saturate((time - rig.LandTime) / 1.2f);
        if (amp <= 0.01f || n < 3) return;

        // u-independent phases hoisted out of the loop.
        float invN = 1f / (n - 1f);
        float swellPhase0 = MathF.Tau * rig.SwayHz * time + rig.SwayPhase;
        float gustPhase0 = time * SwayGustHz + rig.SwayPhase;
        float gustSeed = rig.Seed * 0.013f;
        bool useShapeTable = n == PathSamples;

        Span<Vector2> offset = stackalloc Vector2[PathSamples];
        for (int i = 0; i < n; i++)
        {
            Vector2 tan = path.Points[Math.Min(n - 1, i + 1)] - path.Points[Math.Max(0, i - 1)];
            float len = tan.Length();
            if (len < 1e-3f) { offset[i] = default; continue; }

            float u = i * invN;
            float shape = useShapeTable ? SwayShape[i] : MathF.Sin(MathF.PI * u);
            float swell = MathF.Sin(swellPhase0 - u * 1.6f);
            float gust = FireNoise.Value(gustPhase0 - u * 1.2f, gustSeed);
            offset[i] = new Vector2(-tan.Y, tan.X) / len * (amp * (0.6f * swell + 0.7f * gust) * shape);
        }

        for (int i = 0; i < n; i++) path.Points[i] += offset[i];
        path.BuildArc();
    }

    /// <summary>
    /// Fills the strand's guide with the drape a strand with <paramref name="extra"/> px of spare
    /// length would take between its ends: a parabola sagging downhill whose arc length is exactly
    /// chord + extra. Used only in flight (so the chain lays out as a very slightly sagging taut
    /// cable). A near-vertical span has no downhill side, so it bows to whichever side was seeded.
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

        float sag = MathF.Sqrt(3f * c * extra / 8f);   // small-sag estimate

        Span<Vector2> pts = stackalloc Vector2[Samples + 1];
        Span<float> cum = stackalloc float[Samples + 1];

        // Sag grows with the square root of spare length, so a couple of secant steps converge.
        for (int pass = 0; pass < 3; pass++)
        {
            Trace(rig.Start, chord, perp, sag, pts, cum);
            float got = cum[Samples] - c;
            if (extra < 1e-3f || got < 1e-3f) break;
            sag *= MathF.Sqrt(extra / got);
        }
        Trace(rig.Start, chord, perp, sag, pts, cum);

        // Equal arc-length spacing, matching the strand's equal-length links.
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

        // A hard stop sends a TRANSVERSE shock down a taut chain. Never push ALONG the chain: it
        // carries tension but not compression, so an axial push buckles into a loop, which is
        // the one thing this effect must not do.
        Vector2 along = Vector2.Normalize(strand.Pos[StrandNodes - 1] - strand.Pos[StrandNodes - 4]);
        if (float.IsNaN(along.X)) along = new Vector2(1f, 0f);
        Vector2 side = new(-along.Y, along.X);
        float sign = DrawHelpers.Hash01(rig.Seed + 710 + (int)(time * 10f)) < 0.5f ? -1f : 1f;
        float v = 520f * px * strength * (1f - 0.35f * rig.Depth);

        // One smooth bump of sideways velocity, strongest a few links in from the stop and fading
        // out along the chain. (Alternating signs on neighbours would fold the end into a zigzag.)
        const int Reach = 6;
        for (int k = 1; k <= Reach; k++)
        {
            float bump = MathF.Sin(MathF.PI * k / (Reach + 1f));
            strand.Impulse(StrandNodes - 1 - k, side * (v * bump * sign));
        }
    }

    /// <summary>A chain just hit the edge it hangs from; debris flies back along it and into the screen.</summary>
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
    /// Lays out this cast's chains via <see cref="Archetypes"/> (one top-to-side per side, one
    /// side-to-bottom per side, and on 5-chain casts one wildcard), so the screen is always
    /// covered evenly. Per cast: anchor positions within their edges, which chain is far, and
    /// each rig's slack, depth and flight.
    /// </summary>
    private void BuildLayout(int castSeed, Vector2 size)
    {
        float shortSide = MathF.Min(size.X, size.Y);
        float overhang = shortSide * 0.07f;

        _count = 4 + (int)(DrawHelpers.Hash01(castSeed + 1) * 2f);   // 4 or 5

        // One random chain per cast is the far one: smaller links, hazier, painted first.
        int farIdx = (int)(DrawHelpers.Hash01(castSeed + 200) * _count);
        if (farIdx >= _count) farIdx = _count - 1;

        for (int i = 0; i < _count; i++)
            BuildRig(_rigs[i], i, castSeed, size, overhang, farIdx == i);

        for (int i = 0; i < _count; i++) _drawOrder[i] = i;
        Array.Sort(_drawOrder, 0, _count, Comparer<int>.Create((a, b) => _rigs[b].Depth.CompareTo(_rigs[a].Depth)));
    }

    private static void BuildRig(Rig r, int index, int castSeed, Vector2 size, float overhang, bool far)
    {
        int s = unchecked(castSeed + index * 977 + 17);
        float H(int salt, float lo, float hi) => DrawHelpers.HashRange(s + salt, lo, hi);

        Vector2 a, b;
        if (index >= Archetypes.Length)
        {
            // Wildcard: any two perimeter points at least one full edge apart — no corner stubs.
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

        r.Start = a;
        r.End = b;

        Vector2 chord = b - a;
        r.ChordLen = MathF.Max(chord.Length(), 1e-3f);
        r.ChordDir = chord / r.ChordLen;
        r.ChordPerp = new Vector2(-r.ChordDir.Y, r.ChordDir.X);

        r.Seed = s;
        r.Phase = DrawHelpers.HashRange(s + 999, 0f, MathF.Tau);   // constant for the rig; no per-frame hash
        r.Depth = far ? H(3, 0.55f, 0.90f) : H(3, 0.00f, 0.18f);
        r.LinkFrac = far ? H(4, 0.034f, 0.044f) : H(4, 0.052f, 0.072f);
        float slack = far ? H(5, 0.04f, 0.09f) : H(5, 0.03f, 0.13f);
        // Spare length has to go somewhere. Across a wide span it becomes a sag; on a steep one
        // it has nowhere sensible to go and a real chain would just hang straight. Steeper = taut.
        float horizontal = chord.LengthSquared() > 1f ? MathF.Abs(chord.X) / r.ChordLen : 1f;
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
        r.PathFrozen = false;
        r.Path.Count = 0;
    }
}