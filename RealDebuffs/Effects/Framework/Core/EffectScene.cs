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

    private IReadOnlyDictionary<DebuffKind, Vector4>? _modifierColors;
    private Vector4?[] _modColor = Array.Empty<Vector4?>();
    private ulong[] _modRoles = Array.Empty<ulong>();

    /// <summary>A second colour per effect for its modifier layer; null or absent means the modifier keeps the effect's colour. Replace the instance to change it.</summary>
    public IReadOnlyDictionary<DebuffKind, Vector4>? ModifierColors
    {
        get => _modifierColors;
        set
        {
            if (ReferenceEquals(value, _modifierColors)) return;
            _modifierColors = value;
            if (value is null || value.Count == 0) { _modColor = Array.Empty<Vector4?>(); _modRoles = Array.Empty<ulong>(); return; }

            int size = (int)value.Keys.Max() + 1;
            _modColor = new Vector4?[size];
            _modRoles = new ulong[size];
            foreach (var kv in value)
            {
                _modColor[(int)kv.Key] = kv.Value;
                if (EffectRegistry.ModifierFor(kv.Key) is { } slot) _modRoles[(int)kv.Key] = 1UL << (int)slot.Role;
            }
        }
    }

    /// <summary>The modifier colour for an effect, if its status named one.</summary>
    public Vector4? ModifierColorFor(DebuffKind owner) => (uint)owner < (uint)_modColor.Length ? _modColor[(int)owner] : null;

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
    public void AddParticle(in ParticlePrimitive p)
    {
        var c = p; c.Owner = CurrentOwner;
        int o = (int)c.Owner;
        if ((uint)o < (uint)_modColor.Length && _modColor[o] is { } mc && ((_modRoles[o] >> (int)c.Role) & 1UL) != 0) c.ColorOverride = mc;
        Particles.Add(c);
    }
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
