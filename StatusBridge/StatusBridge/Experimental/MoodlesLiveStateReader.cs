using System.Collections;
using System.Reflection;

namespace StatusBridge.Experimental;

/// <summary>
/// EXPERIMENTAL, READ-ONLY. Reaches into Moodles' own loaded assembly via reflection to read two
/// things the public IPC surface cannot expose, not even in principle. Never writes anything back.
///
/// <para><b>Gap 1 - the IPC tuple's ExpireTicks isn't the live countdown.</b> Confirmed from
/// source: <c>MyStatus.ToStatusTuple()</c> sets the tuple's <c>ExpireTicks</c> from
/// <c>TotalDurationSeconds</c> - a static, configured duration - never from the actual
/// <c>ExpiresAt</c> timestamp that drives removal. And removing a status, via IPC or natively,
/// doesn't delete it from Moodles' internal list immediately: it sets <c>ExpiresAt = 0</c> (see
/// <c>MyStatusManager.Cancel</c>), and the entry only disappears once Moodles' own per-frame
/// Tick() notices it's expired and prunes it. Put together: a status that was just cancelled, one
/// frame away from actually vanishing, reports the exact same ExpireTicks via IPC as a perfectly
/// healthy one - the public API genuinely cannot distinguish "about to be pruned" from "fine".
/// That ambiguity is the mechanical root of the appear-then-vanish ghosts in the README's Known
/// Issues. Reading ExpiresAt directly closes it: BridgeEngine can now tell the two apart and
/// simply not react to a status that's already on its way out.</para>
///
/// <para><b>Gap 2 - Ephemeral has no IPC exposure at all.</b> <c>MyStatusManager.Ephemeral</c>
/// silently blocks every IPC-based add/remove/preset call on that character (confirmed: every
/// one of them checks it) once a data-string snapshot lands on it - most plausibly your sync
/// plugin restoring your own last-known moodle state on login - and nothing in Moodles' public
/// API can read or clear it. This class can read it for diagnostics. It deliberately does NOT
/// try to clear it - see the remarks on BridgeEngine's live-state integration for why forcing
/// that open is a decision worth making deliberately rather than something to do silently every
/// tick.</para>
///
/// <para>Reflection path (all public fields, no unsafe pointers - notably simpler than it could
/// have been): Moodles.Moodles.P.Config.StatusManagers (Dictionary&lt;string, MyStatusManager&gt;)
/// -> the entry whose Statuses list contains a GUID we already know, from the ordinary IPC poll,
/// belongs to the local player right now. Cross-referencing this way deliberately avoids needing
/// to replicate Moodles' own name@world formatting convention to look the manager up directly -
/// one less assumption about exactly what Dalamud's current API surface looks like.</para>
///
/// <para>Fails soft throughout, same philosophy as MoodlesOffsetPatch: any missing type, field,
/// or unresolvable manager just returns null, and callers fall back to treating everything as
/// alive - i.e. exactly BridgeEngine's behavior without this class. Never throws into caller code.</para>
/// </summary>
internal sealed class MoodlesLiveStateReader
{
    private const string AssemblyName = "Moodles";
    private const string PluginTypeName = "Moodles.Moodles";
    private const string StaticInstanceFieldName = "P";
    private const string ConfigFieldName = "Config";
    private const string StatusManagersFieldName = "StatusManagers";
    private const string StatusesFieldName = "Statuses";
    private const string EphemeralFieldName = "Ephemeral";
    private const string GuidFieldName = "GUID";
    private const string ExpiresAtFieldName = "ExpiresAt";

    /// <summary>Human-readable outcome of the last read attempt, for display in the settings UI.</summary>
    public string Status { get; private set; } = "Not yet checked.";

    /// <summary>
    /// Given GUIDs already known (from the normal IPC poll) to currently belong to the local
    /// player's Moodles data, returns the subset that are genuinely alive right now - i.e. not
    /// Cancel()-marked and merely waiting on Moodles' own next prune pass. Null on any failure
    /// (see <see cref="Status"/>); treat null as "can't tell, don't filter anything out."
    /// </summary>
    public HashSet<Guid>? TryGetGenuinelyAliveGuids(IEnumerable<Guid> knownCurrentGuids)
    {
        var known = knownCurrentGuids as ICollection<Guid> ?? knownCurrentGuids.ToList();
        if (FindLocalPlayerManager(known) is not { } manager)
            return null;

        if (GetInstanceField(manager, StatusesFieldName)?.GetValue(manager) is not IEnumerable statuses)
        {
            Status = $"Moodles' manager no longer has a '{StatusesFieldName}' field - reader is stale";
            return null;
        }

        // Matches Moodles.Utils.Time exactly: DateTimeOffset.Now (local), not UtcNow.
        var nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var alive = new HashSet<Guid>();

        foreach (var status in statuses)
        {
            if (GetInstanceField(status, GuidFieldName)?.GetValue(status) is not Guid guid)
                continue;
            if (GetInstanceField(status, ExpiresAtFieldName)?.GetValue(status) is not long expiresAt)
                continue;

            // Covers a future timestamp and the long.MaxValue "never expires" sentinel alike;
            // excludes 0 (Cancel()'s mark) and anything else already in the past.
            if (expiresAt > nowMs)
                alive.Add(guid);
        }

        Status = $"OK - {alive.Count} of the statuses currently in Moodles' own list read as genuinely alive";
        return alive;
    }

    /// <summary>Null on any failure. Otherwise, Moodles' own Ephemeral flag for your own character.</summary>
    public bool? TryGetEphemeral(IEnumerable<Guid> knownCurrentGuids)
    {
        var known = knownCurrentGuids as ICollection<Guid> ?? knownCurrentGuids.ToList();
        if (FindLocalPlayerManager(known) is not { } manager)
            return null;

        if (GetInstanceField(manager, EphemeralFieldName)?.GetValue(manager) is not bool ephemeral)
        {
            Status = $"Moodles' manager no longer has a bool '{EphemeralFieldName}' field - reader is stale";
            return null;
        }

        return ephemeral;
    }

    private object? FindLocalPlayerManager(ICollection<Guid> known)
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
        if (assembly == null)
        {
            Status = "Moodles assembly not found";
            return null;
        }

        var pluginType = assembly.GetType(PluginTypeName);
        var pluginInstance = pluginType?.GetField(StaticInstanceFieldName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (pluginType == null || pluginInstance == null)
        {
            Status = "Moodles hasn't finished initializing yet, or its plugin type has changed shape";
            return null;
        }

        var config = GetInstanceField(pluginInstance, ConfigFieldName)?.GetValue(pluginInstance);
        if (config == null)
        {
            Status = $"Moodles' plugin class no longer has a '{ConfigFieldName}' field - reader is stale";
            return null;
        }

        if (GetInstanceField(config, StatusManagersFieldName)?.GetValue(config) is not IDictionary managers)
        {
            Status = $"Moodles' Config no longer has a '{StatusManagersFieldName}' field - reader is stale";
            return null;
        }

        if (known.Count == 0)
        {
            Status = "No currently-known statuses to cross-reference against - nothing to check this tick";
            return null;
        }

        foreach (var manager in managers.Values)
        {
            if (manager == null || GetInstanceField(manager, StatusesFieldName)?.GetValue(manager) is not IEnumerable statuses)
                continue;

            foreach (var status in statuses)
            {
                if (GetInstanceField(status, GuidFieldName)?.GetValue(status) is Guid g && known.Contains(g))
                {
                    Status = "OK - found the local player's manager";
                    return manager;
                }
            }
        }

        Status = "Couldn't find a manager containing any currently-known status";
        return null;
    }

    private static FieldInfo? GetInstanceField(object obj, string name) =>
        obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
}
