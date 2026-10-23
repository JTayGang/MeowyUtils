namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Faux-3D flight down a tunnel, for effects whose things stream toward the viewer. Each thing has a fixed offset along the tunnel; the
/// flight keeps one running distance, so a thing's height is that distance plus its offset, wrapped. It comes in at the far end, grows
/// and sweeps outward as it nears, and leaves past the lens; each lap (Lap) is a chance to give it a new lane, so no lap repeats.
/// Heights are px toward the viewer, the same as LayerCamera, which does the perspective: place things with its Project.
/// </summary>
internal sealed class TunnelFlight
{
    public readonly float FarHeight, NearHeight;

    /// <summary>Distance flown so far, px. Advance it each frame.</summary>
    public float Travel;

    public TunnelFlight(float farHeight, float nearHeight)
    {
        FarHeight = farHeight;
        NearHeight = nearHeight;
    }

    public float Length => NearHeight - FarHeight;

    public void Advance(float dt, float speed) => Travel += speed * dt;

    /// <summary>Where a thing at this offset is now, and which lap it is on (changes each time it comes round).</summary>
    public float HeightOf(float offset, out int lap)
    {
        float p = (offset + Travel) / Length;
        float whole = MathF.Floor(p);
        lap = (int)whole;
        return FarHeight + (p - whole) * Length;
    }

    /// <summary>0 at the far end of the tunnel, 1 at the lens.</summary>
    public float Near(float height) => DrawHelpers.Saturate((height - FarHeight) / Length);
}
