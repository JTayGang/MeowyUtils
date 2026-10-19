namespace RealDebuffs.Effects.Framework;

/// <summary>Detects a fresh application: a gap over a second between Emit calls means the status was re-applied.</summary>
public sealed class CastTracker
{
    private const float NewCastGapSeconds = 1.0f;
    private float _last = -100f;

    public float Start { get; private set; }

    /// <summary>Call once per Emit; true when this call begins a fresh cast.</summary>
    public bool Begin(float time)
    {
        bool fresh = time - _last > NewCastGapSeconds;
        if (fresh) Start = time;
        _last = time;
        return fresh;
    }
}

/// <summary>Turns the manager's linear alpha into a punchier fade-in plus a leave signal. Call Begin on a new cast, Update once per Emit.</summary>
public sealed class CastEnvelope
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
