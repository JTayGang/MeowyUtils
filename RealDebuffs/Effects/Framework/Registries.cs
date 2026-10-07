using System.Collections.Concurrent;
using RealDebuffs.Effects.Framework.Materials;

namespace RealDebuffs.Effects.Framework;

/// <summary>One swappable material slot an effect exposes to the Effect generator UI.</summary>
public readonly record struct SwappableSlot(
    string PrimitiveType,
    PrimitiveRole Role,
    string Label,
    string DefaultMaterial,
    string? RegionKind = null);

public interface IHasSwappableSlots
{
    IReadOnlyList<SwappableSlot> Slots { get; }
}

/// <summary>The "hero" primitive slot(s) of one effect - what a "made of snow" phrase replaces.</summary>
public readonly record struct EffectHeroSlot(string PrimitiveType, PrimitiveRole Role);

public interface IHasHeroSlots
{
    EffectHeroSlot[] HeroSlots { get; }
}

/// <summary>
/// Registry of every effect's declared metadata, populated once by Plugin from the effect roster.
/// Effects self-declare hero and swappable slots, so no table here needs changing when a new
/// effect is added. Fallback* methods provide type-appropriate defaults so an unregistered
/// material name never crashes the renderer.
/// </summary>
public static class EffectRegistry
{
    private static readonly Dictionary<(DebuffKind, string, string), string> _defaults = new();
    private static readonly Dictionary<DebuffKind, EffectHeroSlot[]> _heroSlots = new();
    private static readonly Dictionary<DebuffKind, SwappableSlot[]> _slots = new();
    private static readonly List<DebuffKind> _kindsWithSlots = new();

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

    public static string? DefaultFor(DebuffKind kind, string type, string roleKey) =>
        _defaults.TryGetValue((kind, type, roleKey), out var name) ? name : null;

    public static EffectHeroSlot[] HeroSlotsFor(DebuffKind kind) =>
        _heroSlots.TryGetValue(kind, out var slots) ? slots : Array.Empty<EffectHeroSlot>();

    public static SwappableSlot[] SlotsFor(DebuffKind kind) =>
        _slots.TryGetValue(kind, out var slots) ? slots : Array.Empty<SwappableSlot>();

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
        PrimitiveRole.Drip       => "particle.drip",
        PrimitiveRole.Flow       => "particle.drip",
        PrimitiveRole.Dust       => "particle.dust",
        PrimitiveRole.Flake      => "particle.flake",
        PrimitiveRole.Fibre      => "particle.fibre",
        PrimitiveRole.Glint      => "particle.glint",
        PrimitiveRole.Mist       => "particle.mist",
        _                        => "particle.spark",
    };

    public static string FallbackRegion(in RegionPrimitive r) =>
        r.HasEdge ? "region.edge-glow" : "region.flat-fill";
}

/// <summary>
/// Name-keyed registry for every material. Config stores names as strings, so no enum churn.
/// Vocabulary merges every material's NaturalLanguageWords into a word -> material-name map;
/// first registered wins on conflict.
/// </summary>
public static class MaterialRegistry
{
    private static readonly Dictionary<string, IStrokeMaterial>   Strokes   = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IParticleMaterial> Particles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IRegionMaterial>   Regions   = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<IMaterial> All = new();

    public static IReadOnlyList<IMaterial> AllMaterials => All;

    public static IReadOnlyDictionary<string, string> Vocabulary { get; }

    public static IReadOnlyList<string> StrokeNames { get; }
    public static IReadOnlyList<string> ParticleNames { get; }
    public static IReadOnlyList<string> RegionNames { get; }

    static MaterialRegistry()
    {
        Add(Regions, new RegionFlatFill());
        Add(Regions, new RegionEdgeGlow());
        Add(Regions, new RegionFirelight());
        Add(Regions, new RegionFrost());

        Add(Strokes, new StrokeSimple());
        Add(Strokes, new StrokeChain());
        Add(Strokes, new StrokeParasite());
        Add(Strokes, new StrokeRope());

        Add(Particles, new ParticleEmber());
        Add(Particles, new ParticleCinder());
        Add(Particles, new ParticleSmoke());
        Add(Particles, new ParticleSnowflake());
        Add(Particles, new ParticleSnow());
        Add(Particles, new ParticleFog());
        Add(Particles, new ParticleSpark());
        Add(Particles, new ParticleDrip());
        Add(Particles, new ParticleDust());
        Add(Particles, new ParticleFlake());
        Add(Particles, new ParticleFibre());
        Add(Particles, new ParticleGlint());
        Add(Particles, new ParticleMist());

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
///  - Emit axis: (Kind, "Stroke", Role) + ".Emit" -> particle material name.
/// </summary>
public static class MaterialOverrideKey
{
    // These keys are looked up per stroke / region / particle, every frame. Building the string each
    // time allocated on every call; there are only a handful of distinct keys, so build each once.
    // (GetOrAdd with a static lambda allocates nothing on a hit; thread-safe in case a settings
    // window and the draw loop ever ask at once.)
    private static readonly ConcurrentDictionary<(DebuffKind, string, PrimitiveRole), string> ForKeys = new();
    private static readonly ConcurrentDictionary<(DebuffKind, string), string> RegionKeys = new();
    private static readonly ConcurrentDictionary<(DebuffKind, PrimitiveRole), string> EmitKeys = new();
    private static readonly ConcurrentDictionary<PrimitiveRole, string> RoleNames = new();

    /// <summary>
    /// A role's name without the boxing allocation of <c>role.ToString()</c> (an enum has to be boxed
    /// to call it), which on a per-stroke, per-frame path adds up to garbage every frame.
    /// </summary>
    public static string RoleName(PrimitiveRole role) =>
        RoleNames.GetOrAdd(role, static r => r.ToString());

    public static string For(DebuffKind kind, string type, PrimitiveRole role) =>
        ForKeys.GetOrAdd((kind, type, role), static k => $"{k.Item1}.{k.Item2}.{k.Item3}");

    public static string ForRegion(DebuffKind kind, string regionKind) =>
        RegionKeys.GetOrAdd((kind, regionKind), static k => $"{k.Item1}.Region.{k.Item2}");

    public static string ForRegion(DebuffKind kind, in RegionPrimitive r) =>
        ForRegion(kind, r.HasEdge ? "EdgeGlow" : "FlatFill");

    public static string ForStrokeEmit(DebuffKind kind, PrimitiveRole role) =>
        EmitKeys.GetOrAdd((kind, role), static k => $"{k.Item1}.Stroke.{k.Item2}.Emit");

    /// <summary>Which stroke material should render this stroke, considering overrides first.</summary>
    public static string ResolveStroke(in StrokePrimitive s, IReadOnlyDictionary<string, string>? overrides) =>
        ResolveStroke(s.Owner, s.Role, overrides);

    /// <summary>Same resolution without a primitive in hand (impacts name an owner and role only).</summary>
    public static string ResolveStroke(DebuffKind owner, PrimitiveRole role, IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides != null &&
            overrides.TryGetValue(For(owner, "Stroke", role), out var name))
            return name;

        return EffectRegistry.DefaultFor(owner, "Stroke", RoleName(role))
            ?? EffectRegistry.FallbackStroke();
    }
}

/// <summary>
/// Finds every ISceneEffect implementation in the assembly, sorted by DrawOrder. Cached for the
/// session - call exactly once per session from Plugin's constructor. Effects must be public
/// classes with a public parameterless constructor.
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