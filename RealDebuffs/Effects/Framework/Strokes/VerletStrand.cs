namespace RealDebuffs.Effects.Framework;

/// A rope or chain as Verlet point masses with fixed-length links; ends pinned unless PinStart/PinEnd say otherwise.
public sealed class VerletStrand
{
    // Many small steps beat one big step with many iterations, which stretches and zigzags a long strand.
    const float SubstepSeconds = 1f / 360f;
    const int   MaxSubstepsPerFrame = 12;

    public readonly int Count;
    public readonly Vector2[] Pos, Prev;

    /// Caller-owned guide pose: Drive follows it; with ShapeStiffness above zero each node is also sprung toward it.
    public readonly Vector2[] Target;

    /// Spring toward Target in 1/s^2 (acceleration per px of error); 0 = off. Stable up to a few thousand.
    public float ShapeStiffness;

    /// Whether each end is held at its SetEnds position; an unpinned end swings free.
    public bool PinStart = true, PinEnd = true;

    /// Total length in pixels; may change every frame.
    public float Length = 100f;

    /// px/s^2, screen down is +Y.
    public Vector2 Gravity = new(0f, 2400f);

    /// Velocity damping in 1/s.
    public float Drag = 1.0f;

    /// Constraint passes per sub-step.
    public int Iterations = 12;

    /// 0..1 resistance to folding into a hairpin.
    public float BendStiffness = 0.35f;

    Vector2 _startFrom, _startTo, _endFrom, _endTo;
    float _lastH = SubstepSeconds;  // the substep length Prev was left at: velocity is (Pos - Prev) / this
    bool _anchored;

    public VerletStrand(int nodeCount)
    {
        if (nodeCount < 3) throw new ArgumentOutOfRangeException(nameof(nodeCount), "A strand needs at least 3 nodes.");
        Count = nodeCount;
        Pos = new Vector2[nodeCount];
        Prev = new Vector2[nodeCount];
        Target = new Vector2[nodeCount];
    }

    /// Drops every node onto one point, at rest.
    public void Reset(Vector2 at)
    {
        for (int i = 0; i < Count; i++) { Pos[i] = at; Prev[i] = at; }
        _startFrom = _startTo = _endFrom = _endTo = at;
        _lastH = SubstepSeconds;
        _anchored = false;
    }

    /// Puts every node on the pose, at rest.
    public void Place(ReadOnlySpan<Vector2> pose)
    {
        for (int i = 0; i < Count; i++) { Pos[i] = pose[i]; Prev[i] = pose[i]; }
        _startFrom = _startTo = pose[0];
        _endFrom = _endTo = pose[Count - 1];
        _lastH = SubstepSeconds;
        _anchored = false;
    }

    /// Where the pinned ends should end this frame; they travel there in a line through every sub-step.
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

    /// Puts every node on the pose with the velocity that implies, so a later Step carries on without a pop.
    public void Drive(ReadOnlySpan<Vector2> pose, float dt)
    {
        float k = dt > 1e-5f ? SubstepSeconds / dt : 0f;
        for (int i = 0; i < Count; i++)
        {
            Vector2 was = Pos[i];
            Pos[i] = pose[i];
            Prev[i] = Pos[i] - (Pos[i] - was) * k;
        }
        _lastH = SubstepSeconds;
    }

    /// Instantly changes a node's velocity (Verlet keeps velocity implicitly in Prev).
    public void Impulse(int node, Vector2 deltaVelocity)
    {
        if ((uint)node >= (uint)Count) return;
        Prev[node] -= deltaVelocity * _lastH;
    }

    public void Step(float dt)
    {
        if (!(dt > 1e-6f)) return;

        // Advance by EXACTLY dt, in a whole number of near-equal substeps.
        dt = Min(dt, MaxSubstepsPerFrame * SubstepSeconds * 1.25f);  // a stall: run slow rather than leap
        int steps = Math.Clamp((int)Round(dt / SubstepSeconds), 1, MaxSubstepsPerFrame);
        float h = dt / steps;

        for (int s = 0; s < steps; s++)
        {
            float k = (s + 1f) / steps;
            Substep(h, Vector2.Lerp(_startFrom, _startTo, k), Vector2.Lerp(_endFrom, _endTo, k));
        }
    }

    void Substep(float h, Vector2 start, Vector2 end)
    {
        float dragK = Max(0f, 1f - Drag * h) * (h / _lastH);  // rescale keeps velocity right when the substep length changes
        float hh = h * h;
        _lastH = h;

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

    void Solve(int a, int b, float rest)
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

    /// Publishes the strand as a Catmull-Rom path resampled to samples points, arc table rebuilt.
    public void FillPath(StrandPath path, int samples)
    {
        samples = Math.Clamp(samples, 2, path.Points.Length);
        StrandPath.CatmullRomResample(Pos, Count, path.Points, samples);
        path.Count = samples;
        path.BuildArc();
    }
}

/// A sine ripple travelling along a draped strand.
public readonly record struct Ripple(float Frac, float Cycles, float Hz, float Phase);

/// Drape style: sag side (+-1), or the downhill side when Downhill (Side is then the near-vertical fallback)
public readonly record struct DrapeStyle(float Side, bool Downhill = false, float Skew = 0f, Ripple Ripple = default);

/// Guide-curve helper for a strand being paid out between two ends.
public static class StrandGuide
{
    /// +1/-1 perpendicular side that points downhill; near-vertical spans take the fallback.
    public static float DownhillSide(Vector2 dir, float fallback) =>
        Abs(dir.X) < 0.30f ? fallback : (dir.X >= 0f ? 1f : -1f);  // dir.X is the perpendicular's y

    /// Fills target at equal arc spacing with the drape (a parabola) of a strand carrying extra px of slack.
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
        float want = rippleAmp > 0f ? Max(extra - rippleExtra, extra * 0.25f) : extra;

        // Sag grows with the square root of spare length, so a couple of secant steps converge.
        float sag = Sqrt(3f * c * want / 8f);
        for (int pass = 0; pass < 3; pass++)
        {
            Trace(start, chord, perp, sag, skew, rippleAmp, in ripple, t, pts, cum);
            float got = cum[Samples] - c - rippleExtra;
            if (want < 1e-3f || got < 1e-3f) break;
            sag *= Sqrt(want / got);
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

    static void Trace(Vector2 start, Vector2 chord, Vector2 perp, float sag, float skew, float rippleAmp,
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
                lateral += rippleAmp * Sin(PI * f)
                           * Sin(Tau * (ripple.Cycles * f - ripple.Hz * t) + ripple.Phase);
            pts[j] = start + chord * f + perp * lateral;
            cum[j] = j == 0 ? 0f : cum[j - 1] + Vector2.Distance(pts[j], pts[j - 1]);
        }
    }
}

/// What Heavy's chains and Bind's ropes share: strand, published path, chord, and the throw/freeze/impact plumbing.
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
        Strand = new(nodes);
        Path = new(samples);
        BasePath = new(samples);
    }

    /// Sets both anchors and the chord facts derived from them.
    public void SetChord(Vector2 a, Vector2 b)
    {
        Start = a; End = b;
        Vector2 chord = b - a;
        ChordLen = Max(chord.Length(), 1e-3f);
        ChordDir = chord / ChordLen;
        ChordPerp = new(-ChordDir.Y, ChordDir.X);
    }

    /// Clears per-cast state and takes this rig's seed.
    public virtual void Reset(int seed)
    {
        Seed = seed;
        Phase = HashRange(seed + 999, 0f, Tau);
        Launched = Landed = PathFrozen = false;
        Agitation = 0f;
        Path.Count = 0;
    }

    /// First frame of flight: every node on the start anchor, heavy and damped.
    public void Launch(float gravity, float drag)
    {
        Launched = true;
        Strand.Reset(Start);
        Strand.Gravity = new(0f, gravity);
        Strand.Drag = drag;
        Strand.Iterations = 8;
        Strand.BendStiffness = 0.75f;
    }

    /// Head position along the throw, 0..1: a short launch ramp, then constant speed (never eased to a stop).
    public static float Travel(float tau) => (tau < 0.1f ? 5f * tau * tau : tau - 0.05f) / 0.95f;

    /// Publishes the strand to Path; once frozen it's restored from BasePath each frame so overlays land clean.
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

    /// An anchor took a hit: debris is thrown back along the strand and into the screen.
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
            Strength = Saturate(strength * (1f - 0.4f * Depth)),
            Brightness = alpha,
            Seed = seed,
            ColorOverride = colorOverride,
        });
    }
}

/// A fixed pool of rigs for one cast, drawn farthest first.
internal sealed class RigSet<T> where T : StrandRig, new()
{
    public readonly T[] Rigs;
    readonly int[] _order;

    /// Rigs in use this cast.
    public int Count;

    public RigSet(int max)
    {
        Rigs = new T[max];
        _order = new int[max];
        for (int i = 0; i < max; i++) Rigs[i] = new T();
    }

    /// The n-th rig in draw order (after Sort).
    public T At(int n) => Rigs[_order[n]];

    public void Sort()
    {
        for (int i = 0; i < Count; i++) _order[i] = i;
        Array.Sort(_order, 0, Count, Comparer<int>.Create((a, b) => Rigs[b].Depth.CompareTo(Rigs[a].Depth)));
    }
}
