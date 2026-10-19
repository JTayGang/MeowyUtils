namespace RealDebuffs.Effects.Framework;

/// <summary>
/// A ring of excitement travelling through a stack of concentric rings: where its front is (px from the centre of the screen), how thick it is and how hard it hits. Strokes brighten and thicken as it passes.
/// Expanding for something that bursts out of the middle, collapsing for something drawn in; any number can be alive at once.
/// </summary>
internal readonly record struct ShockFront(float Front, float Width, float Gain)
{
    /// <summary>How excited a ring at <paramref name="screenRadius"/> is by every front in the set: a gaussian round each front, summed, capped.</summary>
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
