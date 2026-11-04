namespace RealDebuffs.Effects.Framework;

/// <summary>Finds every ISceneEffect with a public parameterless constructor, sorted by DrawOrder, then Kind; call once from Plugin.</summary>
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

        _cached = found.OrderBy(e => e.DrawOrder).ThenBy(e => e.Kind).ToArray();
        return _cached;
    }
}
