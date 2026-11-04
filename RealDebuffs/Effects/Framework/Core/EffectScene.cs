using System.Numerics;

namespace RealDebuffs.Effects.Framework;

public sealed class EffectScene
{
    public readonly List<StrokePrimitive>   Strokes   = new(256);
    public readonly List<ParticlePrimitive> Particles = new(2048);
    public readonly List<RegionPrimitive>   Regions   = new(64);
    public readonly List<ImpactPrimitive>   Impacts   = new(8);

    /// <summary>Display size in pixels, set by the host each frame. Zero means unknown.</summary>
    public Vector2 ScreenSize;

    /// <summary>Resolves slots to materials under this frame's overrides, for the renderer, the shed and effects whose physics depends on a slot.</summary>
    public readonly MaterialResolver Materials = new();

    /// <summary>The material overrides in force this frame; replace the instance to change them.</summary>
    public IReadOnlyDictionary<string, string>? Overrides
    {
        get => Materials.Overrides;
        set => Materials.Overrides = value;
    }

    public VignetteRequest Vignette;
    public DebuffKind CurrentOwner;

    public void Clear()
    {
        Strokes.Clear();
        Particles.Clear();
        Regions.Clear();
        Impacts.Clear();
        Vignette = default;
        CurrentOwner = default;
    }

    public void AddStroke(in StrokePrimitive s)  { var c = s; c.Owner = CurrentOwner; Strokes.Add(c); }
    public void AddParticle(in ParticlePrimitive p) { var c = p; c.Owner = CurrentOwner; Particles.Add(c); }
    public void AddRegion(in RegionPrimitive r) { var c = r; c.Owner = CurrentOwner; Regions.Add(c); }
    public void AddImpact(in ImpactPrimitive i) { var c = i; c.Owner = CurrentOwner; Impacts.Add(c); }

    /// <summary>For post-effect emission passes where CurrentOwner isn't the right effect anymore.</summary>
    public void AddParticleForOwner(in ParticlePrimitive p, DebuffKind owner)
    {
        var copy = p; copy.Owner = owner; Particles.Add(copy);
    }

    public void RequestVignette(uint tint, float thicknessFrac, float alpha, int priority, Vector4? colorOverride)
    {
        if (!Vignette.Active || priority > Vignette.Priority ||
            (priority == Vignette.Priority && alpha > Vignette.Alpha))
        {
            Vignette = new VignetteRequest
            {
                Active = true, Tint = tint, ThicknessFrac = thicknessFrac,
                Alpha = alpha, Priority = priority, ColorOverride = colorOverride,
            };
        }
    }
}
