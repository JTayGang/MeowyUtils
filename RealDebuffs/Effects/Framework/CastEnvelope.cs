namespace RealDebuffs.Effects.Framework;

/// <summary>
/// The two things an effect with a staged cast needs from the manager's alpha, which only ever ramps linearly in and out.
///
/// Leaving: alpha only falls while the status is lifting, so an effect can discharge or let go instead of simply dimming (<see cref="Update"/> reports how far through that it is).
///
/// Punch and gate: the manager fades alpha in over <see cref="EffectManager.FadeInSeconds"/>, which would hide the first moments of a cast. Because the fade is linear, alpha * FadeInSeconds / age
/// is the strength it will have at full ramp: <c>Alpha</c> rides that up to three times faster, and <c>Gate</c> (for the cast's own flash, which must not be dimmed by the fade at all) goes to full at once.
/// The fade-out is left alone.
///
/// Call <see cref="Begin"/> when a cast starts and <see cref="Update"/> once per Emit, before anything reads alpha.
/// </summary>
public sealed class CastEnvelope
{
    private float _lastAlpha, _peakAlpha, _rampLead;
    private bool _leaving;

    /// <summary>The effect's alpha as it should be used this frame: the manager's, with the fade-in sped up.</summary>
    public float Alpha { get; private set; }

    /// <summary>Like <see cref="Alpha"/> but at full strength from the first frame; for a flash that is the cast itself.</summary>
    public float Gate { get; private set; }

    /// <summary>0 while the status is held; rises to 1 as it lifts (the effect should be gone at 1).</summary>
    public float Leave { get; private set; }

    /// <summary>True from the first frame the manager's alpha falls.</summary>
    public bool Leaving => _leaving;

    /// <summary>A cast has just begun. dt is the frame's, which is how far into the manager's ramp this first Emit already is.</summary>
    public void Begin(float dt)
    {
        _rampLead = dt;
        _leaving = false;
        _peakAlpha = 0f;
        _lastAlpha = 0f;
    }

    /// <param name="alpha">The manager's alpha for this frame.</param>
    /// <param name="age">Seconds since the cast began.</param>
    public void Update(float alpha, float age)
    {
        if (alpha > _lastAlpha + 1e-4f) _leaving = false;
        else if (alpha < _lastAlpha - 1e-4f) _leaving = true;
        _lastAlpha = alpha;
        if (!_leaving) _peakAlpha = MathF.Max(_peakAlpha, alpha);
        Leave = _leaving && _peakAlpha > 0.01f ? DrawHelpers.Saturate(1f - alpha / _peakAlpha) : 0f;

        float gate = alpha;
        if (!_leaving && age + _rampLead < EffectManager.FadeInSeconds)
        {
            float toFull = EffectManager.FadeInSeconds / MathF.Max(age + _rampLead, 0.001f);
            gate = MathF.Min(alpha * toFull, 1f);
            alpha *= MathF.Min(3f, toFull);
        }
        Alpha = alpha;
        Gate = gate;
    }
}
