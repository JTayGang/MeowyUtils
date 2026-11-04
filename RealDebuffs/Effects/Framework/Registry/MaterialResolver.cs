namespace RealDebuffs.Effects.Framework;

/// <summary>How a stroke role is drawn and shed: its material, and the particle emitter that replaces what it sheds, if the user chose one.</summary>
public readonly record struct StrokeRoute(IStrokeMaterial? Material, IParticleMaterial? Emitter, string? EmitName, bool Swapped);

/// <summary>Resolves (owner, role) to a material: user override, then the effect's slot default, then the role fallback. Cached until the override set changes.</summary>
public sealed class MaterialResolver
{
    private IReadOnlyDictionary<string, string>? _overrides;
    private readonly Dictionary<(DebuffKind, PrimitiveRole), StrokeRoute> _strokes = new();
    private readonly Dictionary<(DebuffKind, PrimitiveRole), IParticleMaterial?> _particles = new();
    private readonly Dictionary<(DebuffKind, RegionKind), IRegionMaterial?> _regions = new();

    /// <summary>Replace the dictionary instance to change overrides; the same instance keeps its cache.</summary>
    public IReadOnlyDictionary<string, string>? Overrides
    {
        get => _overrides;
        set
        {
            if (ReferenceEquals(value, _overrides)) return;
            _overrides = value;
            _strokes.Clear();
            _particles.Clear();
            _regions.Clear();
        }
    }

    public StrokeRoute Stroke(DebuffKind owner, PrimitiveRole role)
    {
        if (_strokes.TryGetValue((owner, role), out var route)) return route;

        string? emitName = _overrides?.GetValueOrDefault(MaterialOverrideKey.ForStrokeEmit(owner, role));
        string? declared = EffectRegistry.DefaultFor(owner, SlotType.Stroke, role);
        string name = _overrides?.GetValueOrDefault(MaterialOverrideKey.For(owner, SlotType.Stroke, role))
                      ?? declared ?? MaterialRegistry.FallbackStroke;

        route = new StrokeRoute(
            MaterialRegistry.TryGetStroke(name) ?? MaterialRegistry.TryGetStroke(MaterialRegistry.FallbackStroke),
            emitName is null ? null : MaterialRegistry.TryGetParticle(emitName),
            emitName,
            Swapped: emitName is not null || (declared is not null && !string.Equals(name, declared, StringComparison.OrdinalIgnoreCase)));
        _strokes[(owner, role)] = route;
        return route;
    }

    public IParticleMaterial? Particle(DebuffKind owner, PrimitiveRole role)
    {
        if (_particles.TryGetValue((owner, role), out var material)) return material;

        string name = _overrides?.GetValueOrDefault(MaterialOverrideKey.For(owner, SlotType.Particle, role))
                      ?? EffectRegistry.DefaultFor(owner, SlotType.Particle, role)
                      ?? MaterialRegistry.FallbackParticle(role);
        material = Named(name, role);
        _particles[(owner, role)] = material;
        return material;
    }

    /// <summary>A particle's material: the one it names (set by emit-axis overrides), else its slot's.</summary>
    public IParticleMaterial? Particle(in ParticlePrimitive p) =>
        p.MaterialName is { } forced ? Named(forced, p.Role) : Particle(p.Owner, p.Role);

    public IRegionMaterial? Region(DebuffKind owner, RegionKind kind)
    {
        if (_regions.TryGetValue((owner, kind), out var material)) return material;

        string name = _overrides?.GetValueOrDefault(MaterialOverrideKey.ForRegion(owner, kind))
                      ?? EffectRegistry.DefaultFor(owner, kind)
                      ?? MaterialRegistry.FallbackRegion(kind);
        material = MaterialRegistry.TryGetRegion(name) ?? MaterialRegistry.TryGetRegion(MaterialRegistry.FallbackRegion(kind));
        _regions[(owner, kind)] = material;
        return material;
    }

    private static IParticleMaterial? Named(string name, PrimitiveRole role) =>
        MaterialRegistry.TryGetParticle(name) ?? MaterialRegistry.TryGetParticle(MaterialRegistry.FallbackParticle(role));
}
