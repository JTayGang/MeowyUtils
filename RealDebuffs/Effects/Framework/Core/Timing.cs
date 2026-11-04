namespace RealDebuffs.Effects.Framework;

/// <summary>Timing for apparitions: when one is due (FlashSchedule), how it stutters (Flicker), an uneven pulse (Heartbeat).</summary>
internal static class Flicker
{
    /// <summary>Stutter frame rate, independent of the game's: it should read as a bad signal, not a smooth fade.</summary>
    private const float Rate = 24f;

    /// <summary>Brightness of a dropped frame: the thing is still faintly there.</summary>
    private const float Dropped = 0.10f;

    /// <summary>How strongly an apparition of a given life shows t seconds in (0 outside it); it cuts, never fades.</summary>
    public static float Level(float t, float life, int seed)
    {
        if (t < 0f || t >= life) return 0f;

        float attack = MathF.Min(0.30f * life, 0.22f);
        float tail = MathF.Min(0.22f * life, 0.14f);
        float on = t < attack ? DrawHelpers.Lerp(0.25f, 0.90f, t / attack)
                 : t > life - tail ? DrawHelpers.Lerp(0.85f, 0.15f, (t - (life - tail)) / tail)
                 : 0.93f;

        int frame = (int)(t * Rate);
        return DrawHelpers.Hash01(seed * 31 + frame * 7919) < on ? 1f : Dropped;
    }

    /// <summary>A gutter: 1 most of the time with a dip now and then (chance is per frame).</summary>
    public static float Gutter(float time, int seed, float chance)
        => DrawHelpers.Hash01(seed * 17 + (int)(time * Rate) * 104729) < chance ? 0.35f : 1f;

    /// <summary>A value that jumps to a new random one in -1..1 at rate Hz: a tic, a tremor.</summary>
    public static float Tic(float time, int seed, float hz = 14f)
        => DrawHelpers.Hash01(seed * 13 + (int)(time * hz) * 15485863) * 2f - 1f;
}

/// <summary>When the next apparition is due: TryStart begins one, Finish sets the quiet before the next.</summary>
internal struct FlashSchedule
{
    private float _next;
    private int _seed, _count;

    /// <summary>Begins a cast: the first apparition comes at age firstAt seconds.</summary>
    public void Begin(int seed, float firstAt)
    {
        _seed = seed; _count = 0; _next = firstAt;
    }

    /// <summary>True once when one is due, with a seed for it. Nothing else starts until Finish.</summary>
    public bool TryStart(float age, out int eventSeed)
    {
        eventSeed = 0;
        if (age < _next) return false;
        _count++;
        eventSeed = _seed + _count * 7919;
        _next = float.MaxValue;
        return true;
    }

    /// <summary>The apparition is over at age: the next is a random gap away.</summary>
    public void Finish(float age, float gapMin, float gapMax)
        => _next = age + DrawHelpers.HashRange(_seed + _count * 104729, gapMin, gapMax);
}

/// <summary>A pulse that doesn't keep time: lub-dub at uneven intervals, with the odd doubled or half-hearted beat.</summary>
internal sealed class Heartbeat
{
    private float _next, _lub = -10f, _dub = -10f;
    private int _seed, _count;

    public void Begin(int seed, float firstAt)
    {
        _seed = seed; _count = 0; _next = firstAt; _lub = _dub = -10f;
    }

    /// <summary>The pulse at age, 0..1, peaking at each beat; stress (0..1) quickens it.</summary>
    public float Level(float age, float stress)
    {
        while (age >= _next)
        {
            _count++;
            float roll = DrawHelpers.Hash01(_seed + _count * 31);
            _lub = _next;
            _dub = roll < 0.12f ? -10f : _next + 0.24f + 0.05f * DrawHelpers.Hash01(_seed + _count * 37);   // the odd missing dub
            float gap = DrawHelpers.HashRange(_seed + _count * 41, 0.62f, 1.15f) * (1f - 0.40f * stress);
            _next += roll > 0.90f ? 0.30f : gap;                                                          // and the odd extra beat
        }

        return MathF.Max(Thump(age - _lub, 1f), Thump(age - _dub, 0.55f));
    }

    private static float Thump(float t, float gain)
        => t < 0f ? 0f : gain * MathF.Exp(-t / 0.11f) * DrawHelpers.Smooth(t / 0.02f);
}
