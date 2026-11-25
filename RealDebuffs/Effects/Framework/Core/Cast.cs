namespace RealDebuffs.Effects.Framework;

/// Everything an effect needs about one frame of a cast, derived once from Emit's arguments.
public readonly struct CastFrame
{
    public readonly Vector2 Size;
    public readonly float Short, Px, Age, Time, Dt;

    /// Manager alpha, or the envelope's faster fade-in when the tracker has one.
    public readonly float Alpha;

    /// Full strength from the first frame, for the cast's own flash (envelope only).
    public readonly float Gate;

    /// 0 while held, rising to 1 as the status lifts (envelope only).
    public readonly float Leave;

    /// Stable per cast: derived from its start time.
    public readonly int Seed;

    /// Fresh: first frame of a cast. Resized: screen size changed mid-cast. Leaving: alpha has started to fall.
    public readonly bool Fresh, Resized, Leaving;

    public readonly Vector4? Color;

    public Vector2 Center => Size * 0.5f;

    public CastFrame(Vector2 size, float alpha, float gate, float leave, bool leaving, float age, float time, float dt, int seed, bool fresh, bool resized, Vector4? color)
    {
        Size = size; Short = Min(size.X, size.Y); Px = PixelScale(size);
        Alpha = alpha; Gate = gate; Leave = leave; Leaving = leaving;
        Age = age; Time = time; Dt = dt; Seed = seed; Fresh = fresh; Resized = resized; Color = color;
    }
}

/// Tracks one effect's casts: a gap over a second between Emit calls means the status was re-applied.
public sealed class CastTracker
{
    /// Seconds the host takes to fade an effect in; the cast envelope is timed against it.
    public const float FadeInSeconds = 0.35f;

    const float NewCastGapSeconds = 1.0f;
    readonly CastEnvelope? _env;
    float _last = -100f, _start;
    Vector2 _lastSize;

    /// With an envelope, Alpha fades in faster and Gate/Leave/Leaving are reported (see CastEnvelope).
    public CastTracker(bool envelope = false) => _env = envelope ? new CastEnvelope() : null;

    /// Call once per Emit.
    public CastFrame Frame(Vector2 size, float alpha, float time, float dt, Vector4? color)
    {
        bool fresh = time - _last > NewCastGapSeconds;
        if (fresh)
        {
            _start = time;
            _env?.Begin(dt);
        }
        _last = time;
        bool resized = !fresh && size != _lastSize;
        _lastSize = size;
        float age = time - _start;

        float gate = alpha, leave = 0f;
        bool leaving = false;
        if (_env != null)
        {
            _env.Update(alpha, age);
            alpha = _env.Alpha; gate = _env.Gate; leave = _env.Leave; leaving = _env.Leaving;
        }
        return new(size, alpha, gate, leave, leaving, age, time, dt, unchecked((int)(_start * 1000f)), fresh, resized, color);
    }
}

/// Turns the manager's linear alpha into a punchier fade-in plus a leave signal; Begin per cast, Update per Emit.
internal sealed class CastEnvelope
{
    float _lastAlpha, _peakAlpha, _rampLead;

    /// Manager alpha with the fade-in sped up (up to 3x).
    public float Alpha { get; private set; }

    /// Like Alpha but full strength from the first frame, for the cast's own flash.
    public float Gate { get; private set; }

    /// 0 while held, rising to 1 as the status lifts.
    public float Leave { get; private set; }

    /// True from the first frame alpha falls.
    public bool Leaving { get; private set; }

    public void Begin(float dt)
    {
        _rampLead = dt;
        Leaving = false;
        _peakAlpha = _lastAlpha = 0f;
    }

    public void Update(float alpha, float age)
    {
        if (alpha > _lastAlpha + 1e-4f) Leaving = false;
        else if (alpha < _lastAlpha - 1e-4f) Leaving = true;
        _lastAlpha = alpha;
        if (!Leaving) _peakAlpha = Max(_peakAlpha, alpha);
        Leave = Leaving && _peakAlpha > 0.01f ? Saturate(1f - alpha / _peakAlpha) : 0f;

        float gate = alpha;
        if (!Leaving && age + _rampLead < CastTracker.FadeInSeconds)
        {
            float toFull = CastTracker.FadeInSeconds / Max(age + _rampLead, 0.001f);
            gate = Min(alpha * toFull, 1f);
            alpha *= Min(3f, toFull);
        }
        Alpha = alpha;
        Gate = gate;
    }
}

/// A ring of excitement travelling through concentric rings: front radius (px), width, and strength.
internal readonly record struct ShockFront(float Front, float Width, float Gain)
{
    /// Summed, capped gaussian excitation of a ring at the given screen radius.
    public static float Excite(ReadOnlySpan<ShockFront> fronts, float screenRadius)
    {
        float e = 0f;
        foreach (var f in fronts)
        {
            float d = (screenRadius - f.Front) / f.Width;
            e += f.Gain * Exp(-d * d);
        }
        return Min(e, 1.8f);
    }
}
