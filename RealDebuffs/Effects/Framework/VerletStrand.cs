using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>A rope or chain as point masses joined by fixed-length links (Verlet); ends are pinned unless PinStart/PinEnd say otherwise. Set the ends, Step (or Drive), then FillPath.</summary>
public sealed class VerletStrand
{
    // Many small steps beat one big step with many iterations: a long strand otherwise stretches like a spring and zigzags into loops.
    private const float SubstepSeconds = 1f / 360f;
    private const int   MaxSubstepsPerFrame = 12;

    public readonly int Count;
    public readonly Vector2[] Pos;
    public readonly Vector2[] Prev;

    /// <summary>Caller-owned guide pose: Drive follows it, and with ShapeStiffness above zero each node is also sprung toward it.</summary>
    public readonly Vector2[] Target;

    /// <summary>Spring toward <see cref="Target"/> in 1/s^2 (acceleration per px of error); 0 = off. Stable up to a few thousand.</summary>
    public float ShapeStiffness;

    /// <summary>Whether each end is held at its SetEnds position; an unpinned end swings free.</summary>
    public bool PinStart = true, PinEnd = true;

    /// <summary>Total length in pixels; may change every frame.</summary>
    public float Length = 100f;

    /// <summary>px/s^2, screen down is +Y.</summary>
    public Vector2 Gravity = new(0f, 2400f);

    /// <summary>Velocity damping in 1/s.</summary>
    public float Drag = 1.0f;

    /// <summary>Constraint passes per sub-step.</summary>
    public int Iterations = 12;

    /// <summary>0..1 resistance to folding into a hairpin.</summary>
    public float BendStiffness = 0.35f;

    private Vector2 _startFrom, _startTo, _endFrom, _endTo;
    private float _accum;
    private bool _anchored;

    public VerletStrand(int nodeCount)
    {
        if (nodeCount < 3) throw new ArgumentOutOfRangeException(nameof(nodeCount), "A strand needs at least 3 nodes.");
        Count = nodeCount;
        Pos = new Vector2[nodeCount];
        Prev = new Vector2[nodeCount];
        Target = new Vector2[nodeCount];
    }

    /// <summary>Drops every node onto one point, at rest.</summary>
    public void Reset(Vector2 at)
    {
        for (int i = 0; i < Count; i++) { Pos[i] = at; Prev[i] = at; }
        _startFrom = _startTo = _endFrom = _endTo = at;
        _accum = 0f;
        _anchored = false;
    }

    /// <summary>Where the pinned ends should be by the end of this frame (they travel there in a line through every sub-step).</summary>
    public void SetEnds(Vector2 start, Vector2 end)
    {
        if (!_anchored)
        {
            _startFrom = start; _endFrom = end;
            _anchored = true;
        }
        else
        {
            _startFrom = _startTo; _endFrom = _endTo;
        }
        _startTo = start; _endTo = end;
    }

    /// <summary>Puts every node on the pose with the velocity that implies, so a later Step carries on without a pop.</summary>
    public void Drive(ReadOnlySpan<Vector2> pose, float dt)
    {
        float k = dt > 1e-5f ? SubstepSeconds / dt : 0f;
        for (int i = 0; i < Count; i++)
        {
            Vector2 was = Pos[i];
            Pos[i] = pose[i];
            Prev[i] = Pos[i] - (Pos[i] - was) * k;
        }
        _accum = 0f;
    }

    /// <summary>Instantly changes a node's velocity (Verlet keeps velocity implicitly in Prev).</summary>
    public void Impulse(int node, Vector2 deltaVelocity)
    {
        if ((uint)node >= (uint)Count) return;
        Prev[node] -= deltaVelocity * SubstepSeconds;
    }

    public void Step(float dt)
    {
        _accum += MathF.Max(0f, dt);
        int steps = Math.Min(MaxSubstepsPerFrame, (int)(_accum / SubstepSeconds));
        _accum -= steps * SubstepSeconds;
        if (steps == MaxSubstepsPerFrame) _accum = 0f;     // a stall: drop the debt rather than spiral

        for (int s = 0; s < steps; s++)
        {
            float k = (s + 1f) / steps;
            Substep(SubstepSeconds, Vector2.Lerp(_startFrom, _startTo, k), Vector2.Lerp(_endFrom, _endTo, k));
        }
    }

    private void Substep(float h, Vector2 start, Vector2 end)
    {
        float dragK = MathF.Max(0f, 1f - Drag * h);
        float hh = h * h;

        for (int i = 0; i < Count; i++)
        {
            Vector2 p = Pos[i];
            Vector2 v = (p - Prev[i]) * dragK;
            Prev[i] = p;
            Vector2 a = Gravity;
            if (ShapeStiffness > 0f) a += (Target[i] - p) * ShapeStiffness;
            Pos[i] = p + v + a * hh;
        }

        if (PinStart) Pos[0] = start;
        if (PinEnd)   Pos[Count - 1] = end;

        float rest = Length / (Count - 1);
        float bendMin = rest * 2f * 0.90f;

        for (int it = 0; it < Iterations; it++)
        {
            // Alternate sweep direction so the correction doesn't bias toward one end.
            bool fwd = (it & 1) == 0;
            for (int n = 0; n < Count - 1; n++)
            {
                int i = fwd ? n : Count - 2 - n;
                Solve(i, i + 1, rest);
            }

            if (BendStiffness > 0f)
            {
                for (int i = 0; i < Count - 2; i++)
                {
                    Vector2 d = Pos[i + 2] - Pos[i];
                    float len = d.Length();
                    if (len >= bendMin || len < 1e-4f) continue;

                    Vector2 push = d / len * ((bendMin - len) * 0.5f * BendStiffness);
                    if (!(PinStart && i == 0))            Pos[i] -= push;
                    if (!(PinEnd && i + 2 == Count - 1))  Pos[i + 2] += push;
                }
            }

            if (PinStart) Pos[0] = start;
            if (PinEnd)   Pos[Count - 1] = end;
        }
    }

    private void Solve(int a, int b, float rest)
    {
        Vector2 d = Pos[b] - Pos[a];
        float len = d.Length();
        if (len < 1e-5f) return;

        float diff = (len - rest) / len;
        bool aPinned = PinStart && a == 0;
        bool bPinned = PinEnd && b == Count - 1;

        if (aPinned && bPinned) return;
        if (aPinned)      Pos[b] -= d * diff;
        else if (bPinned) Pos[a] += d * diff;
        else
        {
            Vector2 corr = d * (diff * 0.5f);
            Pos[a] += corr;
            Pos[b] -= corr;
        }
    }

    /// <summary>Publishes the strand as a Catmull-Rom path resampled to <paramref name="samples"/> points, arc table rebuilt.</summary>
    public void FillPath(StrandPath path, int samples)
    {
        samples = Math.Clamp(samples, 2, path.Points.Length);
        StrandPath.CatmullRomResample(Pos, Count, path.Points, samples);
        path.Count = samples;
        path.BuildArc();
    }
}

/// <summary>A sine ripple travelling along a draped strand.</summary>
public readonly record struct Ripple(float Frac, float Cycles, float Hz, float Phase);

/// <summary>Drape style: sag side (+-1), or the downhill side when Downhill (Side is then the near-vertical fallback); Skew shifts the sag toward the start.</summary>
public readonly record struct DrapeStyle(float Side, bool Downhill = false, float Skew = 0f, Ripple Ripple = default);

/// <summary>Guide-curve helper for a strand being paid out between two ends.</summary>
public static class StrandGuide
{
    /// <summary>+1/-1 perpendicular side that points downhill; near-vertical spans take the fallback.</summary>
    public static float DownhillSide(Vector2 dir, float fallback) =>
        MathF.Abs(dir.X) < 0.30f ? fallback : (dir.X >= 0f ? 1f : -1f);   // dir.X is the perpendicular's y

    /// <summary>Fills <paramref name="target"/> at equal arc spacing with the drape of a strand carrying <paramref name="extra"/> px of spare length from start to head: a parabola (optionally skewed and rippled) of arc length chord + extra.</summary>
    public static void Drape(Vector2 start, Vector2 head, Vector2 fallbackDir, float extra, in DrapeStyle style, float t, Vector2[] target)
    {
        const int Samples = 48;

        Vector2 chord = head - start;
        float c = chord.Length();
        Vector2 dir = c > 1e-3f ? chord / c : fallbackDir;

        float side = style.Downhill ? DownhillSide(dir, style.Side) : style.Side;
        Vector2 perp = new Vector2(-dir.Y, dir.X) * side;

        float skew = style.Skew;
        Ripple ripple = style.Ripple;
        float rippleAmp = ripple.Frac * c;

        Span<Vector2> pts = stackalloc Vector2[Samples + 1];
        Span<float> cum = stackalloc float[Samples + 1];

        // The ripple has length of its own; the sag gets the rest.
        float rippleExtra = 0f;
        if (rippleAmp > 0f)
        {
            Trace(start, chord, perp, 0f, skew, rippleAmp, in ripple, t, pts, cum);
            rippleExtra = cum[Samples] - c;
        }
        float want = rippleAmp > 0f ? MathF.Max(extra - rippleExtra, extra * 0.25f) : extra;

        // Sag grows with the square root of spare length, so a couple of secant steps converge.
        float sag = MathF.Sqrt(3f * c * want / 8f);
        for (int pass = 0; pass < 3; pass++)
        {
            Trace(start, chord, perp, sag, skew, rippleAmp, in ripple, t, pts, cum);
            float got = cum[Samples] - c - rippleExtra;
            if (want < 1e-3f || got < 1e-3f) break;
            sag *= MathF.Sqrt(want / got);
        }
        Trace(start, chord, perp, sag, skew, rippleAmp, in ripple, t, pts, cum);

        float total = cum[Samples];
        int seg = 0;
        int n = target.Length;
        for (int i = 0; i < n; i++)
        {
            float wantArc = total * i / (n - 1f);
            while (seg < Samples - 1 && cum[seg + 1] < wantArc) seg++;
            float span = cum[seg + 1] - cum[seg];
            float f = span > 1e-4f ? (wantArc - cum[seg]) / span : 0f;
            target[i] = Vector2.Lerp(pts[seg], pts[seg + 1], f);
        }
    }

    private static void Trace(Vector2 start, Vector2 chord, Vector2 perp, float sag, float skew, float rippleAmp,
                              in Ripple ripple, float t, Span<Vector2> pts, Span<float> cum)
    {
        int n = pts.Length - 1;
        for (int j = 0; j <= n; j++)
        {
            float f = j / (float)n;
            // The two branches keep each caller's original floating-point association, so results stay bit-identical.
            float lateral = skew == 0f
                ? 4f * sag * f * (1f - f)
                : sag * (4f * f * (1f - f) * (1f + skew * (1f - 2f * f)));
            if (rippleAmp > 0f)
                lateral += rippleAmp * MathF.Sin(MathF.PI * f)
                           * MathF.Sin(MathF.Tau * (ripple.Cycles * f - ripple.Hz * t) + ripple.Phase);
            pts[j] = start + chord * f + perp * lateral;
            cum[j] = j == 0 ? 0f : cum[j - 1] + Vector2.Distance(pts[j], pts[j - 1]);
        }
    }
}

/// <summary>What Heavy's chains and Bind's ropes share: strand, published path, chord, and the throw/freeze/impact plumbing.</summary>
public abstract class StrandRig
{
    public readonly VerletStrand Strand;
    public readonly StrandPath Path, BasePath;

    public Vector2 Start, End, ChordDir, ChordPerp;
    public float ChordLen, Depth, Phase, Delay, Flight, Agitation;
    public int Seed;
    public bool Launched, Landed, PathFrozen;

    protected StrandRig(int nodes, int samples)
    {
        Strand = new VerletStrand(nodes);
        Path = new StrandPath(samples);
        BasePath = new StrandPath(samples);
    }

    /// <summary>Sets both anchors and the chord facts derived from them.</summary>
    public void SetChord(Vector2 a, Vector2 b)
    {
        Start = a; End = b;
        Vector2 chord = b - a;
        ChordLen = MathF.Max(chord.Length(), 1e-3f);
        ChordDir = chord / ChordLen;
        ChordPerp = new Vector2(-ChordDir.Y, ChordDir.X);
    }

    /// <summary>Clears per-cast state and takes this rig's seed.</summary>
    public virtual void Reset(int seed)
    {
        Seed = seed;
        Phase = DrawHelpers.HashRange(seed + 999, 0f, MathF.Tau);
        Launched = Landed = PathFrozen = false;
        Agitation = 0f;
        Path.Count = 0;
    }

    /// <summary>First frame of flight: every node on the start anchor, heavy and damped.</summary>
    public void Launch(float gravity, float drag)
    {
        Launched = true;
        Strand.Reset(Start);
        Strand.Gravity = new Vector2(0f, gravity);
        Strand.Drag = drag;
        Strand.Iterations = 8;
        Strand.BendStiffness = 0.75f;
    }

    /// <summary>Head position along the throw, 0..1: a short launch ramp, then constant speed (never eased to a stop).</summary>
    public static float Travel(float tau) => (tau < 0.1f ? 5f * tau * tau : tau - 0.05f) / 0.95f;

    /// <summary>Publishes the strand to Path; once frozen the shape is restored from BasePath each frame so overlays land on a clean base.</summary>
    public void Publish(int samples, bool frozen)
    {
        if (!frozen)
        {
            Strand.FillPath(Path, samples);
            PathFrozen = false;
        }
        else if (!PathFrozen)
        {
            Strand.FillPath(Path, samples);
            Array.Copy(Path.Points, BasePath.Points, samples);
            BasePath.Count = samples;
            PathFrozen = true;
        }
        else
        {
            Array.Copy(BasePath.Points, Path.Points, samples);
            Path.Count = samples;
        }
    }

    /// <summary>An anchor took a hit: debris is thrown back along the strand and into the screen.</summary>
    public void ReportEnd(EffectScene scene, Vector2 size, bool fromStart, float strength, float alpha, int seed, Vector4? colorOverride)
    {
        Vector2 point, inward;
        if (fromStart)
        {
            if (!ScreenEdges.TryFindEntry(Path, size, out point, out inward)) return;
        }
        else
        {
            if (!ScreenEdges.TryFindExit(Path, size, out point, out Vector2 outward)) return;
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
            Strength = Math.Clamp(strength * (1f - 0.4f * Depth), 0f, 1f),
            Brightness = alpha,
            Seed = seed,
            ColorOverride = colorOverride,
        });
    }

    /// <summary>Fills <paramref name="order"/> with rig indices, farthest first, so far strands paint underneath.</summary>
    public static void SortFarFirst<T>(T[] rigs, int[] order, int count) where T : StrandRig
    {
        for (int i = 0; i < count; i++) order[i] = i;
        Array.Sort(order, 0, count, Comparer<int>.Create((a, b) => rigs[b].Depth.CompareTo(rigs[a].Depth)));
    }
}
