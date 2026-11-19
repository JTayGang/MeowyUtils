namespace RealDebuffs.Effects.Framework;

/// <summary>A material whose Draw throws loses that one primitive, not the frame. After Strikes failures in a row it is quarantined for the session (per owner), logged once, and every other primitive keeps drawing.</summary>
internal static class DrawGuard
{
    public const int Strikes = 3;
    private static readonly Dictionary<(object, DebuffKind), int> Fails = new();
    private static readonly HashSet<(object, DebuffKind)> Out = new();

    /// <summary>Where a quarantine is reported; the host points it at its log.</summary>
    public static Action<string>? Log;
    /// <summary>Test hook: the harness throws from here to prove a failing material is contained.</summary>
    internal static Action<object>? Probe;
    public static int Faults, Quarantined;

    public static bool Allowed(object? material, DebuffKind owner) => material != null && (Out.Count == 0 || !Out.Contains((material, owner)));

    public static void Ok(object material, DebuffKind owner) { if (Fails.Count > 0) Fails.Remove((material, owner)); }

    public static void Fail(object material, DebuffKind owner, Exception e)
    {
        Faults++;
        var key = (material, owner);
        Fails.TryGetValue(key, out int n);
        Fails[key] = ++n;
        if (n < Strikes) return;
        Out.Add(key); Fails.Remove(key); Quarantined = Out.Count;
        Log?.Invoke($"RealDebuffs: {material.GetType().Name} for {owner} threw {Strikes} draws in a row ({e.GetType().Name}: {e.Message}) - leaving it out for the rest of this session; everything else keeps drawing.");
    }

    public static void Reset() { Fails.Clear(); Out.Clear(); Faults = Quarantined = 0; Probe = null; }
}
