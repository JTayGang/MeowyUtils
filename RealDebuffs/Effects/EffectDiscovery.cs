namespace RealDebuffs.Effects;

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