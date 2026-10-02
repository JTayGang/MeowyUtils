using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// A rope / chain / cable, simulated as point masses joined by fixed-length links (position-based
/// Verlet integration). It knows nothing about chains: it only knows about weight, damping, a
/// fixed total length and two ends that the caller drags around, which makes it equally good for a
/// chain, a rope (Bind), a vine, or a tendril that should sag and swing instead of following a
/// formula.
///
/// WHY SIMULATE: a parametric curve with a sine on top (the old chain) moves in a way the eye
/// reads as animation. A real hanging strand has inertia: it swings, lags behind its anchors,
/// whips when an end is yanked, and rings down over about a second. All of that falls out of
/// "points joined by sticks under gravity" for free, and so does variety: no two casts settle
/// the same way.
///
/// Usage per frame: set the end targets, optionally fill <see cref="Accel"/> with a
/// per-node push, call <see cref="Step"/>, then <see cref="FillPath"/> to publish the result as a
/// StrandPath (a smooth Catmull-Rom through the nodes) for any stroke material to draw.
///
/// Integration runs on a fixed sub-step independent of frame rate, so a long frame can't make the
/// strand explode and a 144 Hz display doesn't change how it behaves.
/// </summary>
public sealed class VerletStrand
{
    // Many small steps rather than one big step with many iterations: for position-based dynamics
    // it is by far the better trade, and it is what stops a long strand under its own weight from
    // stretching like a soft spring (a stretched strand has no tension, and a strand with no
    // tension zigzags into loops). At 16 nodes the cost is negligible.
    private const float SubstepSeconds = 1f / 360f;
    private const int   MaxSubstepsPerFrame = 12;

    public readonly int Count;
    public readonly Vector2[] Pos;
    public readonly Vector2[] Prev;

    /// <summary>Extra acceleration per node (px/s^2), persisted between frames. Caller-owned; start at zero.</summary>
    public readonly Vector2[] Accel;

    /// <summary>Total length of the strand in pixels. May change every frame (a chain being paid out).</summary>
    public float Length = 100f;

    /// <summary>Constant acceleration in px/s^2 (screen down is +Y).</summary>
    public Vector2 Gravity = new(0f, 2400f);

    /// <summary>Velocity damping in 1/s. Around 1 gives a heavy strand that rings down in about a second.</summary>
    public float Drag = 1.0f;

    /// <summary>Constraint-relaxation passes per sub-step. With small sub-steps a dozen is plenty.</summary>
    public int Iterations = 12;

    /// <summary>
    /// 0..1: resistance to folding sharply. Links can't actually bend tighter than a link length,
    /// so this keeps a slack chain from kinking into a hairpin where real links would lock up.
    /// </summary>
    public float BendStiffness = 0.35f;

    public bool PinStart = true, PinEnd = true;

    /// <summary>
    /// Optional guide shape. With <see cref="ShapeStiffness"/> above zero, each node is pulled toward
    /// <c>Target[i]</c> by a spring, so a caller can say "behave like a rope, but drape roughly like
    /// THIS". It is how an effect gets believable dynamics (lag, whip, ringing) without ever
    /// letting free physics tie the strand in a knot, which a whipped rope will happily do. Fade the
    /// stiffness to zero (or a small residual) and the strand is on its own.
    /// </summary>
    public readonly Vector2[] Target;

    /// <summary>Spring strength toward <see cref="Target"/>, in 1/s^2 (acceleration per pixel of error). 0 = off. Stable up to a few thousand.</summary>
    public float ShapeStiffness;

    private Vector2 _startFrom, _startTo, _endFrom, _endTo;
    private float _accum;
    private bool _anchored;

    public VerletStrand(int nodeCount)
    {
        if (nodeCount < 3) throw new ArgumentOutOfRangeException(nameof(nodeCount), "A strand needs at least 3 nodes.");
        Count = nodeCount;
        Pos = new Vector2[nodeCount];
        Prev = new Vector2[nodeCount];
        Accel = new Vector2[nodeCount];
        Target = new Vector2[nodeCount];
    }

    /// <summary>
    /// Drops every node onto one point, at rest. With a paid-out strand (Length tracks the
    /// distance between the ends) this is the natural starting state: nothing exists yet.
    /// </summary>
    public void Reset(Vector2 at)
    {
        for (int i = 0; i < Count; i++) { Pos[i] = at; Prev[i] = at; Accel[i] = default; }
        _startFrom = _startTo = _endFrom = _endTo = at;
        _accum = 0f;
        _anchored = false;
    }

    /// <summary>
    /// Where the pinned ends should be by the end of this frame. The ends travel in a straight
    /// line from where they were, through every sub-step, so a fast-moving end (a thrown chain's
    /// head) is dragged smoothly rather than teleporting once per frame.
    /// </summary>
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

    /// <summary>
    /// Puts every node exactly on <paramref name="pose"/> and gives it the velocity that motion
    /// implies, so a following <see cref="Step"/> carries on from there without a pop. Use it to
    /// drive the strand kinematically for a while (a thrown rope being paid out along a drape, where
    /// letting free physics sort out the spacing risks the nodes ending up in the wrong order,
    /// which is a fold that no amount of tension can undo) and hand over to the simulation after.
    /// Call it instead of Step, not as well as it.
    /// </summary>
    public void Drive(ReadOnlySpan<Vector2> pose, float dt)
    {
        float k = dt > 1e-5f ? SubstepSeconds / dt : 0f;      // frame displacement -> per-substep displacement
        for (int i = 0; i < Count; i++)
        {
            Vector2 was = Pos[i];
            Pos[i] = pose[i];
            Prev[i] = Pos[i] - (Pos[i] - was) * k;
        }
        _accum = 0f;
    }

    /// <summary>Instantly changes a node's velocity (Verlet stores velocity implicitly in Prev).</summary>
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
            Vector2 a = Gravity + Accel[i];
            if (ShapeStiffness > 0f) a += (Target[i] - p) * ShapeStiffness;
            Pos[i] = p + v + a * hh;
        }

        if (PinStart) { Pos[0] = start; }
        if (PinEnd)   { Pos[Count - 1] = end; }

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
                    if (!(PinStart && i == 0))             Pos[i] -= push;
                    if (!(PinEnd && i + 2 == Count - 1))   Pos[i + 2] += push;
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

    /// <summary>
    /// Publishes the strand as a smooth path: a Catmull-Rom spline through the nodes, resampled to
    /// <paramref name="samples"/> points (capped at the path's capacity), with its arc table
    /// rebuilt and ready for any stroke material.
    /// </summary>
    public void FillPath(StrandPath path, int samples)
    {
        samples = Math.Clamp(samples, 2, path.Points.Length);
        StrandPath.CatmullRomResample(Pos, Count, path.Points, samples);
        path.Count = samples;
        path.BuildArc();
    }
}
