namespace RealDebuffs.Effects.Framework;

/// <summary>Cross-checks effects, slots and materials; returns what would silently misbehave at runtime.</summary>
public static class RegistryAudit
{
    public static List<string> Run(IEnumerable<ISceneEffect> effects)
    {
        var problems = new List<string>(MaterialRegistry.Problems);

        var registered = MaterialRegistry.AllMaterials.Select(m => m.GetType()).ToHashSet();
        foreach (var type in typeof(RegistryAudit).Assembly.GetTypes())
            if (type.IsClass && !type.IsAbstract && typeof(IMaterial).IsAssignableFrom(type) && !registered.Contains(type))
                problems.Add($"{type.Name} is a material nothing registers; give it a public parameterless constructor or add it to an IMaterialPack");

        foreach (var m in MaterialRegistry.AllMaterials)
        {
            if (m.NaturalLanguageWords.Length == 0 && m is not IRegionMaterial && m.Name != MaterialRegistry.FallbackStroke)
                problems.Add($"material \"{m.Name}\" declares no NaturalLanguageWords, so \"made of X\" can't select it");

            if (m is IStrokeMaterial s)
            {
                CheckEmissions(m.Name, s.Emissions, problems);
                foreach (var hit in s.ImpactEmissions) CheckRender(m.Name, hit.RenderMaterial, problems);
            }
            else if (m is IParticleMaterial p) CheckEmissions(m.Name, p.Emissions, problems);
        }

        foreach (var effect in effects)
            foreach (var slot in EffectRegistry.SlotsFor(effect.Kind))
            {
                bool exists = slot.Type switch
                {
                    SlotType.Stroke   => MaterialRegistry.TryGetStroke(slot.DefaultMaterial) is not null,
                    SlotType.Particle => MaterialRegistry.TryGetParticle(slot.DefaultMaterial) is not null,
                    _                 => MaterialRegistry.TryGetRegion(slot.DefaultMaterial) is not null,
                };
                if (!exists) problems.Add($"{effect.Kind}: slot \"{slot.Label}\" defaults to unknown {slot.Type.ToString().ToLowerInvariant()} material \"{slot.DefaultMaterial}\"");
            }

        var roster = effects.ToList();
        foreach (var effect in roster)
        {
            var slots = EffectRegistry.SlotsFor(effect.Kind);
            if (slots.Count(s => s.Hero) > 1) problems.Add($"{effect.Kind}: more than one hero (body) slot; words can only swap one");
            if (slots.Count(s => s.Modifier) > 1) problems.Add($"{effect.Kind}: more than one modifier slot; words can only swap one");
            if (slots.Any(s => s.Hero && s.Modifier)) problems.Add($"{effect.Kind}: a slot is both body and modifier");
        }

        problems.AddRange(new Lexicon(roster).Problems);

        foreach (var role in Enum.GetValues<PrimitiveRole>())
            if (MaterialRegistry.TryGetParticle(MaterialRegistry.FallbackParticle(role)) is null)
                problems.Add($"role {role} falls back to unknown particle \"{MaterialRegistry.FallbackParticle(role)}\"");

        return problems;
    }

    private static void CheckEmissions(string owner, ReadOnlySpan<StrokeEmission> emissions, List<string> problems)
    {
        foreach (var e in emissions) CheckRender(owner, e.RenderMaterial, problems);
    }

    private static void CheckRender(string owner, string? name, List<string> problems)
    {
        if (name is not null && MaterialRegistry.TryGetParticle(name) is null)
            problems.Add($"{owner} sheds unknown particle material \"{name}\"");
    }
}
