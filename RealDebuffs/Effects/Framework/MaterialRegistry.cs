using System;
using System.Collections.Generic;
using RealDebuffs.Effects.Framework.Materials;

namespace RealDebuffs.Effects.Framework;

/// <summary>
/// Name-keyed registry for every material. Adding a material is "write a class, add a Register
/// call to the static constructor". Config stores names as strings, so no enum churn.
///
/// Registration runs from the static constructor, so it fires on the first touch of any
/// MaterialRegistry member (typically the first frame's renderer lookup). Callers never need
/// to invoke a separate init method.
///
/// Vocabulary is the merged map of every material's NaturalLanguageWords -> material name,
/// built lazily on first access. TooltipKeywordParser reads it to resolve "made of X" phrases
/// in status descriptions. Because the words live on the material declarations, adding a new
/// material with words is a one-file change - nothing here or in the parser needs updating.
/// </summary>
public static class MaterialRegistry
{
    private static readonly Dictionary<string, IStrokeMaterial>   StrokeMaterials   = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IParticleMaterial> ParticleMaterials = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IRegionMaterial>   RegionMaterials   = new(StringComparer.OrdinalIgnoreCase);

    private static readonly List<IMaterial> _allMaterials = new();
    private static Dictionary<string, string>? _vocabulary;

    static MaterialRegistry()
    {
        // Region materials.
        Register(new RegionFlatFill());
        Register(new RegionEdgeGlow());

        // Stroke materials.
        Register(new StrokeSimple());
        Register(new StrokeChain());
        Register(new StrokeParasite());

        // Particle materials.
        Register(new ParticleEmber());
        Register(new ParticleSnowflake());
        Register(new ParticleSnow());
        Register(new ParticleFog());
        Register(new ParticleSpark());
        Register(new ParticleDrip());
    }

    public static void Register(IStrokeMaterial m)
    {
        StrokeMaterials[m.Name] = m;
        _allMaterials.Add(m);
        _vocabulary = null;
    }

    public static void Register(IParticleMaterial m)
    {
        ParticleMaterials[m.Name] = m;
        _allMaterials.Add(m);
        _vocabulary = null;
    }

    public static void Register(IRegionMaterial m)
    {
        RegionMaterials[m.Name] = m;
        _allMaterials.Add(m);
        _vocabulary = null;
    }

    /// <summary>
    /// Every registered material, regardless of type. Exposed for the vocabulary builder and for
    /// the startup warning that flags materials with no natural-language words.
    /// </summary>
    public static IReadOnlyList<IMaterial> AllMaterials => _allMaterials;

    /// <summary>
    /// Merged word -> material-name map built from every material's NaturalLanguageWords. Built
    /// lazily on first access; invalidated whenever a new material is registered (which today
    /// only happens during the static constructor, but the invalidation is cheap insurance if
    /// that ever changes). Case-insensitive.
    ///
    /// When two materials declare the same word, the first one registered wins - the same
    /// "first declaration sticks" rule StatusCatalog applies to TriggerStatuses, and a case
    /// worth spotting if it ever happens (a material that meant to be reachable ends up
    /// shadowed by an earlier one).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Vocabulary
    {
        get
        {
            if (_vocabulary != null) return _vocabulary;

            var built = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var material in _allMaterials)
            {
                foreach (var word in material.NaturalLanguageWords)
                {
                    if (!built.ContainsKey(word))
                        built[word] = material.Name;
                }
            }

            _vocabulary = built;
            return _vocabulary;
        }
    }

    public static IStrokeMaterial GetStroke(string name) =>
        StrokeMaterials.TryGetValue(name, out var m)
            ? m
            : throw new KeyNotFoundException($"No stroke material registered as '{name}'.");

    public static IParticleMaterial GetParticle(string name) =>
        ParticleMaterials.TryGetValue(name, out var m)
            ? m
            : throw new KeyNotFoundException($"No particle material registered as '{name}'.");

    public static IRegionMaterial GetRegion(string name) =>
        RegionMaterials.TryGetValue(name, out var m)
            ? m
            : throw new KeyNotFoundException($"No region material registered as '{name}'.");

    public static IStrokeMaterial? TryGetStroke(string name) =>
        StrokeMaterials.TryGetValue(name, out var m) ? m : null;

    public static IParticleMaterial? TryGetParticle(string name) =>
        ParticleMaterials.TryGetValue(name, out var m) ? m : null;

    public static IRegionMaterial? TryGetRegion(string name) =>
        RegionMaterials.TryGetValue(name, out var m) ? m : null;

    public static IEnumerable<string> StrokeNames   => StrokeMaterials.Keys;
    public static IEnumerable<string> ParticleNames => ParticleMaterials.Keys;
    public static IEnumerable<string> RegionNames   => RegionMaterials.Keys;
}