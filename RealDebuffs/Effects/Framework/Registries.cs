using RealDebuffs.Effects.Framework.Materials;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// One swappable material slot an effect exposes to the Effect generator UI. DefaultMaterial is
/// both what the UI shows as the current value when no override is set, and what the renderer
/// falls back to when resolving a primitive of this type/role for this effect.
///
/// PrimitiveType is "Stroke", "Particle", or "Region". For Stroke and Particle, Role selects
/// which primitive role within that type; for Region, Role is unused (pass PrimitiveRole.MainStroke
/// as a placeholder) and RegionKind ("EdgeGlow" or "FlatFill") keys the slot instead, matching the
/// renderer's own region distinction.
/// </summary>
public readonly record struct SwappableSlot(
    string PrimitiveType,
    PrimitiveRole Role,
    string Label,
    string DefaultMaterial,
    string? RegionKind = null);

/// <summary>
/// Optional capability: an ISceneEffect that exposes customizable material slots implements this.
/// The Effect generator panel reads Slots to build its editor; the renderer reads DefaultMaterial
/// to know what each slot defaults to. Effects with no customizable material choices don't
/// implement this.
/// </summary>
public interface IHasSwappableSlots
{
    IReadOnlyList<SwappableSlot> Slots { get; }
}

/// <summary>
/// The "hero" primitive slot(s) of one effect - the thing the effect is about, and the thing a
/// "made of snow" phrase in a status description replaces.
/// </summary>
public readonly record struct EffectHeroSlot(string PrimitiveType, PrimitiveRole Role);

/// <summary>
/// Optional capability: an ISceneEffect that has one or more hero slots implements this so its
/// slots can be discovered alongside the effect itself.
/// </summary>
public interface IHasHeroSlots
{
    EffectHeroSlot[] HeroSlots { get; }
}

/// <summary>
/// Registry of every effect's declared metadata. Populated once by Plugin at startup from the
/// effect roster discovered by EffectDiscovery.
///
/// Two kinds of data are collated here:
///   - Hero slots: read by the tooltip substitution system (CustomStatusSnapshot) and the
///     export-phrase builder (EffectStylePanel) to know which primitives a "made of X" phrase
///     replaces.
///   - Swappable slots: read by the Effect generator UI to build its editor, and by the renderer
///     to know each slot's default material.
///
/// Effects self-declare both via IHasHeroSlots / IHasSwappableSlots, so no table outside the
/// effect file needs to change when a new effect is added.
///
/// Fallback* methods provide type-appropriate defaults for any primitive whose slot isn't
/// declared - a stroke falls back to stroke.simple, a particle to its role default, a region to
/// edge-glow or flat-fill. These exist so an unregistered material name never crashes the renderer.
/// </summary>
public static class EffectRegistry
{
    private static readonly Dictionary<(DebuffKind, string, string), string> _defaults = new();
    private static readonly Dictionary<DebuffKind, EffectHeroSlot[]> _heroSlots = new();
    private static readonly Dictionary<DebuffKind, SwappableSlot[]> _slots = new();
    private static readonly List<DebuffKind> _kindsWithSlots = new();

    // Cached alphabetical view of _kindsWithSlots; invalidated on Register.
    private static DebuffKind[]? _sortedKindsWithSlots;

    public static void Register(ISceneEffect effect)
    {
        if (effect is IHasHeroSlots withHeroes)
            _heroSlots[effect.Kind] = withHeroes.HeroSlots;

        if (effect is IHasSwappableSlots withSlots)
        {
            var slots = withSlots.Slots.ToArray();
            if (slots.Length == 0) return;

            _slots[effect.Kind] = slots;
            if (!_kindsWithSlots.Contains(effect.Kind))
                _kindsWithSlots.Add(effect.Kind);
            _sortedKindsWithSlots = null;

            foreach (var slot in slots)
            {
                string roleKey = slot.RegionKind ?? slot.Role.ToString();
                _defaults[(effect.Kind, slot.PrimitiveType, roleKey)] = slot.DefaultMaterial;
            }
        }
    }

    /// <summary>The declared default material for one slot, or null if the effect doesn't declare one.</summary>
    public static string? DefaultFor(DebuffKind kind, string type, string roleKey) =>
        _defaults.TryGetValue((kind, type, roleKey), out var name) ? name : null;

    /// <summary>Hero slots for one kind - empty if the effect has none, or doesn't implement IHasHeroSlots.</summary>
    public static EffectHeroSlot[] HeroSlotsFor(DebuffKind kind) =>
        _heroSlots.TryGetValue(kind, out var slots) ? slots : Array.Empty<EffectHeroSlot>();

    /// <summary>Swappable slots for one kind - empty if the effect has none.</summary>
    public static SwappableSlot[] SlotsFor(DebuffKind kind) =>
        _slots.TryGetValue(kind, out var slots) ? slots : Array.Empty<SwappableSlot>();

    /// <summary>Kinds with at least one swappable slot, sorted alphabetically for UI display.</summary>
    public static DebuffKind[] KindsWithSlots =>
        _sortedKindsWithSlots ??= _kindsWithSlots.OrderBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase).ToArray();

    public static string FallbackStroke() => "stroke.simple";

    public static string FallbackParticle(PrimitiveRole role) => role switch
    {
        PrimitiveRole.Ember      => "particle.ember",
        PrimitiveRole.Smoke      => "particle.smoke",
        PrimitiveRole.Cinder     => "particle.cinder",
        PrimitiveRole.Snowflake  => "particle.snowflake",
        PrimitiveRole.Snow       => "particle.snow",
        PrimitiveRole.Fog        => "particle.fog",
        PrimitiveRole.IceCrystal => "particle.ice-crystal",
        PrimitiveRole.Drip       => "particle.drip",
        PrimitiveRole.Flow       => "particle.drip",
        _                        => "particle.spark",
    };

    public static string FallbackRegion(in RegionPrimitive r) =>
        r.HasEdge ? "region.edge-glow" : "region.flat-fill";
}

/// <summary>
/// Name-keyed registry for every material. Adding a material is "write a class, add a line to the
/// static constructor"; config stores names as strings, so no enum churn. Registration runs on the
/// first touch of any member, so callers never need a separate init call.
///
/// Vocabulary merges every material's NaturalLanguageWords into a word -> material-name map (used
/// by TooltipKeywordParser to resolve "made of X" phrases). When two materials declare the same
/// word the first registered wins, so a shadowed material is worth spotting.
/// </summary>
public static class MaterialRegistry
{
    private static readonly Dictionary<string, IStrokeMaterial>   Strokes   = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IParticleMaterial> Particles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IRegionMaterial>   Regions   = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<IMaterial> All = new();

    /// <summary>Every registered material regardless of type, in registration order.</summary>
    public static IReadOnlyList<IMaterial> AllMaterials => All;

    /// <summary>Merged word -> material name, case-insensitive.</summary>
    public static IReadOnlyDictionary<string, string> Vocabulary { get; }

    public static IReadOnlyList<string> StrokeNames { get; }
    public static IReadOnlyList<string> ParticleNames { get; }
    public static IReadOnlyList<string> RegionNames { get; }

    static MaterialRegistry()
    {
        Add(Regions, new RegionFlatFill());
        Add(Regions, new RegionEdgeGlow());
        Add(Regions, new RegionFirelight());

        Add(Strokes, new StrokeSimple());
        Add(Strokes, new StrokeChain());
        Add(Strokes, new StrokeParasite());

        Add(Particles, new ParticleEmber());
        Add(Particles, new ParticleCinder());
        Add(Particles, new ParticleSmoke());
        Add(Particles, new ParticleSnowflake());
        Add(Particles, new ParticleSnow());
        Add(Particles, new ParticleFog());
        Add(Particles, new ParticleIceCrystal());
        Add(Particles, new ParticleSpark());
        Add(Particles, new ParticleDrip());

        var vocabulary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var material in All)
            foreach (var word in material.NaturalLanguageWords)
                vocabulary.TryAdd(word, material.Name);
        Vocabulary = vocabulary;

        StrokeNames = Strokes.Keys.ToArray();
        ParticleNames = Particles.Keys.ToArray();
        RegionNames = Regions.Keys.ToArray();
    }

    private static void Add<T>(Dictionary<string, T> table, T material) where T : IMaterial
    {
        table[material.Name] = material;
        All.Add(material);
    }

    public static IStrokeMaterial? TryGetStroke(string name) =>
        Strokes.TryGetValue(name, out var m) ? m : null;

    public static IParticleMaterial? TryGetParticle(string name) =>
        Particles.TryGetValue(name, out var m) ? m : null;

    public static IRegionMaterial? TryGetRegion(string name) =>
        Regions.TryGetValue(name, out var m) ? m : null;
}

/// <summary>
/// Shared key format for material overrides.
///  - Material axis: (Kind, "Stroke"|"Particle"|"Region", Role-or-tag) -> material name.
///  - Emit axis: (Kind, "Stroke", Role) + ".Emit" -> particle material name (ambient particles
///    spawned along that stroke).
/// Settings panel and renderer/emitter are the only places that build these.
/// </summary>
public static class MaterialOverrideKey
{
    public static string For(DebuffKind kind, string type, PrimitiveRole role) =>
        $"{kind}.{type}.{role}";

    public static string ForRegion(DebuffKind kind, string regionKind) =>
        $"{kind}.Region.{regionKind}";

    public static string ForRegion(DebuffKind kind, in RegionPrimitive r) =>
        ForRegion(kind, r.HasEdge ? "EdgeGlow" : "FlatFill");

    /// <summary>Emit axis: which particle material this stroke sheds along its length.</summary>
    public static string ForStrokeEmit(DebuffKind kind, PrimitiveRole role) =>
        $"{kind}.Stroke.{role}.Emit";

    /// <summary>
    /// Which stroke material should render this stroke, considering overrides first. Shared by
    /// the renderer and StrokeAutoEmitter so the two never disagree about which material is
    /// actually on screen. Public specifically so StrokeAutoEmitter (a different file) can call it.
    /// </summary>
    public static string ResolveStroke(in StrokePrimitive s, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides != null &&
            overrides.TryGetValue(For(s.Owner, "Stroke", s.Role), out var name))
            return name;

        return EffectRegistry.DefaultFor(s.Owner, "Stroke", s.Role.ToString())
            ?? EffectRegistry.FallbackStroke();
    }
}

/// <summary>
/// Finds every ISceneEffect implementation in the assembly and instantiates them, sorted by
/// DrawOrder. Cached for the session so the effect instances (which carry per-effect state like
/// cast-in timers) survive across calls - this must be called exactly once per session, from
/// Plugin's constructor.
///
/// Filtering rules:
///   - must be a concrete class,
///   - must implement ISceneEffect,
///   - must have a public parameterless constructor.
///
/// Reflection is only used at plugin load. Failure modes are silent (a type that doesn't match
/// simply isn't discovered), so the one invariant to keep in mind when adding a new effect is:
/// it must be a public class with a public parameterless constructor.
/// </summary>
public static class EffectDiscovery
{
    private static IReadOnlyList<ISceneEffect>? _cached;

    public static IReadOnlyList<ISceneEffect> Discover()
    {
        if (_cached != null) return _cached;

        var found = new List<ISceneEffect>();
        var assembly = typeof(EffectDiscovery).Assembly;

        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || type.IsInterface) continue;
            if (!typeof(ISceneEffect).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) == null) continue;

            if (Activator.CreateInstance(type) is ISceneEffect effect)
                found.Add(effect);
        }

        _cached = found.OrderBy(e => e.DrawOrder).ToArray();
        return _cached;
    }
}
