using System.Collections.Generic;
using System.Numerics;

namespace RealDebuffs.Effects.Framework;

public sealed class EffectScene
{
    public readonly List<StrokePrimitive>   Strokes   = new(256);
    public readonly List<ParticlePrimitive> Particles = new(2048);
    public readonly List<RegionPrimitive>   Regions   = new(64);

    public VignetteRequest Vignette;

    /// <summary>
    /// The DebuffKind currently emitting. EffectManager sets this before each effect's Emit call;
    /// AddStroke/AddParticle/AddRegion stamp it onto whatever primitive they receive, so the
    /// renderer can look up per-effect material overrides without every effect having to set it.
    /// </summary>
    public DebuffKind CurrentOwner;

    public void Clear()
    {
        Strokes.Clear();
        Particles.Clear();
        Regions.Clear();
        Vignette = default;
        CurrentOwner = default;
    }

    public void AddStroke(in StrokePrimitive s)
    {
        var copy = s;
        copy.Owner = CurrentOwner;
        Strokes.Add(copy);
    }

    public void AddParticle(in ParticlePrimitive p)
    {
        var copy = p;
        copy.Owner = CurrentOwner;
        Particles.Add(copy);
    }

    public void AddRegion(in RegionPrimitive r)
    {
        var copy = r;
        copy.Owner = CurrentOwner;
        Regions.Add(copy);
    }

    public void RequestVignette(uint tint, float thicknessFrac, float alpha, int priority, Vector4? colorOverride)
    {
        if (!Vignette.Active || priority > Vignette.Priority ||
            (priority == Vignette.Priority && alpha > Vignette.Alpha))
        {
            Vignette = new VignetteRequest
            {
                Active = true,
                Tint = tint,
                ThicknessFrac = thicknessFrac,
                Alpha = alpha,
                Priority = priority,
                ColorOverride = colorOverride,
            };
        }
    }
}

public struct VignetteRequest
{
    public bool  Active;
    public uint  Tint;
    public float ThicknessFrac;
    public float Alpha;
    public int   Priority;
    public Vector4? ColorOverride;
}