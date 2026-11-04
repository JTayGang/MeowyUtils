using System.Reflection;

namespace RealDebuffs.Effects.Framework;

/// <summary>Every material by name, found by reflection: each IMaterialPack plus each material with a public parameterless constructor. Vocabulary maps a NaturalLanguageWord to a name.</summary>
public static class MaterialRegistry
{
    public const string FallbackStroke = "stroke.simple";
    private const string FallbackParticleName = "particle.spark";

    private static readonly Dictionary<string, IStrokeMaterial>   Strokes   = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IParticleMaterial> Particles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IRegionMaterial>   Regions   = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] RoleFallbacks = BuildRoleFallbacks();

    public static IReadOnlyList<IMaterial> AllMaterials { get; }
    public static IReadOnlyDictionary<string, string> Vocabulary { get; }
    public static IReadOnlyList<string> StrokeNames { get; }
    public static IReadOnlyList<string> ParticleNames { get; }
    public static IReadOnlyList<string> RegionNames { get; }

    /// <summary>Duplicate names and words found while registering.</summary>
    public static IReadOnlyList<string> Problems { get; }

    static MaterialRegistry()
    {
        var problems = new List<string>();
        var found = new List<IMaterial>();

        foreach (var type in typeof(MaterialRegistry).Assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || type.GetConstructor(Type.EmptyTypes) is null) continue;

            if (typeof(IMaterialPack).IsAssignableFrom(type))
                found.AddRange(((IMaterialPack)Activator.CreateInstance(type)!).Materials);
            else if (typeof(IMaterial).IsAssignableFrom(type))
                found.Add((IMaterial)Activator.CreateInstance(type)!);
        }

        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var all = new List<IMaterial>(found.Count);
        foreach (var m in found)
        {
            bool added = m switch
            {
                IStrokeMaterial s   => Strokes.TryAdd(s.Name, s),
                IParticleMaterial p => Particles.TryAdd(p.Name, p),
                IRegionMaterial r   => Regions.TryAdd(r.Name, r),
                _                   => false,
            };
            if (added) all.Add(m);
            else problems.Add($"material \"{m.Name}\" is registered twice or has no material kind");
        }

        var vocabulary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in all)
            foreach (var word in m.NaturalLanguageWords)
                if (!vocabulary.TryAdd(word, m.Name))
                    problems.Add($"word \"{word}\" is claimed by both \"{vocabulary[word]}\" and \"{m.Name}\"");

        AllMaterials = all;
        Vocabulary = vocabulary;
        StrokeNames = Strokes.Keys.ToArray();
        ParticleNames = Particles.Keys.ToArray();
        RegionNames = Regions.Keys.ToArray();
        Problems = problems;
    }

    public static IStrokeMaterial? TryGetStroke(string name) => Strokes.GetValueOrDefault(name);
    public static IParticleMaterial? TryGetParticle(string name) => Particles.GetValueOrDefault(name);
    public static IRegionMaterial? TryGetRegion(string name) => Regions.GetValueOrDefault(name);

    /// <summary>The particle drawn for a role when no slot names one.</summary>
    public static string FallbackParticle(PrimitiveRole role) =>
        (uint)role < (uint)RoleFallbacks.Length && RoleFallbacks[(int)role] is { } name ? name : FallbackParticleName;

    public static string FallbackRegion(RegionKind kind) =>
        kind == RegionKind.EdgeGlow ? "region.edge-glow" : "region.flat-fill";

    private static string[] BuildRoleFallbacks()
    {
        var table = new string[(int)Enum.GetValues<PrimitiveRole>().Max() + 1];
        foreach (var field in typeof(PrimitiveRole).GetFields(BindingFlags.Public | BindingFlags.Static))
            if (field.GetCustomAttribute<FallbackAttribute>() is { } fallback)
                table[(int)field.GetValue(null)!] = fallback.Material;
        return table;
    }
}
