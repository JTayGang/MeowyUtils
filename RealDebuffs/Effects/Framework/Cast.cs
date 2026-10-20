using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>Everything an effect needs about one frame of a cast, derived once from Emit's arguments.</summary>
public readonly struct CastFrame
{
    public readonly Vector2 Size;
    public readonly float Short, Px, Age, Time, Dt;

    /// <summary>Manager alpha, or the envelope's faster fade-in when the tracker has one.</summary>
    public readonly float Alpha;

    /// <summary>Full strength from the first frame, for the cast's own flash (envelope only).</summary>
    public readonly float Gate;

    /// <summary>0 while held, rising to 1 as the status lifts (envelope only).</summary>
    public readonly float Leave;

    /// <summary>Stable per cast: derived from its start time.</summary>
    public readonly int Seed;

    /// <summary>True on the first frame of a cast, and true from the first frame alpha falls for Leaving.</summary>
    public readonly bool Fresh, Leaving;

    public readonly Vector4? Color;

    public Vector2 Center => Size * 0.5f;

    /// <summary>The effects' pixel scale: 1 at a 1080px short side. Layout code that has only a size uses this.</summary>
    public static float PxFor(Vector2 size) => MathF.Min(size.X, size.Y) / 1080f;

    public CastFrame(Vector2 size, float alpha, float gate, float leave, bool leaving, float age, float time, float dt, int seed, bool fresh, Vector4? color)
    {
        Size = size; Short = MathF.Min(size.X, size.Y); Px = PxFor(size);
        Alpha = alpha; Gate = gate; Leave = leave; Leaving = leaving;
        Age = age; Time = time; Dt = dt; Seed = seed; Fresh = fresh; Color = color;
    }
}

/// <summary>Tracks one effect's casts: a gap over a second between Emit calls means the status was re-applied.</summary>
public sealed class CastTracker
{
    private const float NewCastGapSeconds = 1.0f;
    private readonly CastEnvelope? _env;
    private float _last = -100f, _start;

    /// <summary>With an envelope, Alpha fades in faster and Gate/Leave/Leaving are reported (see CastEnvelope).</summary>
    public CastTracker(bool envelope = false) => _env = envelope ? new CastEnvelope() : null;

    /// <summary>Call once per Emit.</summary>
    public CastFrame Frame(Vector2 size, float alpha, float time, float dt, Vector4? color)
    {
        bool fresh = time - _last > NewCastGapSeconds;
        if (fresh)
        {
            _start = time;
            _env?.Begin(dt);
        }
        _last = time;
        float age = time - _start;

        float gate = alpha, leave = 0f;
        bool leaving = false;
        if (_env != null)
        {
            _env.Update(alpha, age);
            alpha = _env.Alpha; gate = _env.Gate; leave = _env.Leave; leaving = _env.Leaving;
        }
        return new CastFrame(size, alpha, gate, leave, leaving, age, time, dt, unchecked((int)(_start * 1000f)), fresh, color);
    }
}

/// <summary>Turns the manager's linear alpha into a punchier fade-in plus a leave signal. Call Begin on a new cast, Update once per Emit.</summary>
internal sealed class CastEnvelope
{
    private float _lastAlpha, _peakAlpha, _rampLead;

    /// <summary>Manager alpha with the fade-in sped up (up to 3x).</summary>
    public float Alpha { get; private set; }

    /// <summary>Like Alpha but full strength from the first frame, for the cast's own flash.</summary>
    public float Gate { get; private set; }

    /// <summary>0 while held, rising to 1 as the status lifts.</summary>
    public float Leave { get; private set; }

    /// <summary>True from the first frame alpha falls.</summary>
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
        if (!Leaving) _peakAlpha = MathF.Max(_peakAlpha, alpha);
        Leave = Leaving && _peakAlpha > 0.01f ? DrawHelpers.Saturate(1f - alpha / _peakAlpha) : 0f;

        float gate = alpha;
        if (!Leaving && age + _rampLead < EffectManager.FadeInSeconds)
        {
            float toFull = EffectManager.FadeInSeconds / MathF.Max(age + _rampLead, 0.001f);
            gate = MathF.Min(alpha * toFull, 1f);
            alpha *= MathF.Min(3f, toFull);
        }
        Alpha = alpha;
        Gate = gate;
    }
}

/// <summary>A ring of excitement travelling through concentric rings: front radius (px), width, and strength.</summary>
internal readonly record struct ShockFront(float Front, float Width, float Gain)
{
    /// <summary>Summed, capped gaussian excitation of a ring at the given screen radius.</summary>
    public static float Excite(ReadOnlySpan<ShockFront> fronts, float screenRadius)
    {
        float e = 0f;
        foreach (var f in fronts)
        {
            float d = (screenRadius - f.Front) / f.Width;
            e += f.Gain * MathF.Exp(-d * d);
        }
        return MathF.Min(e, 1.8f);
    }
}
