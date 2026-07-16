using System.Collections;
using System.Reflection;

namespace StatusBridge.Experimental;

/// <summary>
/// EXPERIMENTAL, READ-ONLY. Loci's side of the same gap MoodlesLiveStateReader closes for
/// Moodles - see that class's remarks for the full explanation. Confirmed from source that
/// Loci has the identical shape: <c>LociStatus.ToTuple()</c> sets the tuple's <c>ExpireTicks</c>
/// from <c>TotalMilliseconds</c> (a static configured duration), and <c>ActorSM.Cancel</c> sets
/// the live <c>ExpiresAt = 0</c> without removing the entry from the list - so the public IPC
/// tuple has the same blind spot here as it does on the Moodles side.
///
/// <para>Reflection path is simpler than Moodles' - no cross-referencing needed. Confirmed from
/// source: <c>Loci.Data.LociManager.ClientSM</c> is a direct static reference to the local
/// player's own manager (internal field, not public, but still just a field - no unsafe pointers
/// anywhere in this path).</para>
///
/// <para>Loci's own Ephemeral is a computed property (<c>EphemeralHosts.Count is not 0</c>), not
/// a settable field, and confirmed from source it does NOT gate the IPC methods this bridge
/// actually calls (<c>ApplyStatusInfo</c>/<c>RemoveStatus</c> operate on ClientSM directly and
/// only check the explicit per-status lock, never Ephemeral) - so unlike the Moodles reader,
/// there's no corresponding "is this silently blocked" question to answer here. This class only
/// reads live ExpiresAt for that reason; see BridgeEngine for how locks (the thing that actually
/// can block a removal on this side) are handled instead, via LociApiEc.ItemLocked - already
/// available from the ordinary IPC calls, no reflection needed for that part.</para>
///
/// <para>Fails soft throughout: any missing type/field/method returns null, and callers fall
/// back to treating everything as alive. Never throws into caller code.</para>
/// </summary>
internal sealed class LociLiveStateReader
{
    private const string AssemblyName = "Loci";
    private const string ManagerTypeName = "Loci.Data.LociManager";
    private const string ClientManagerFieldName = "ClientSM";
    private const string StatusesFieldName = "Statuses";
    private const string GuidFieldName = "GUID";
    private const string ExpiresAtFieldName = "ExpiresAt";

    public string Status { get; private set; } = "Not yet checked.";

    /// <summary>
    /// Returns the subset of the given GUIDs that Loci's own live data still considers active -
    /// excluding anything Cancel()-marked but not yet pruned. Null on any failure; treat null as
    /// "can't tell, don't filter anything out." Unlike the Moodles reader, this doesn't need a
    /// known-GUID set to find the manager (ClientSM is a direct reference) - the parameter here
    /// is just which GUIDs to check, so callers can pass whichever set they're deciding about.
    /// </summary>
    public HashSet<Guid>? TryGetGenuinelyAliveGuids()
    {
        if (FindClientManager() is not { } manager)
            return null;

        if (GetInstanceField(manager, StatusesFieldName)?.GetValue(manager) is not IEnumerable statuses)
        {
            Status = $"Loci's ClientSM no longer has a '{StatusesFieldName}' field - reader is stale";
            return null;
        }

        var nowMs = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        var alive = new HashSet<Guid>();

        foreach (var status in statuses)
        {
            if (GetInstanceField(status, GuidFieldName)?.GetValue(status) is not Guid guid)
                continue;
            if (GetInstanceField(status, ExpiresAtFieldName)?.GetValue(status) is not long expiresAt)
                continue;

            if (expiresAt > nowMs)
                alive.Add(guid);
        }

        Status = $"OK - {alive.Count} of the statuses currently in Loci's own ClientSM read as genuinely alive";
        return alive;
    }

    private object? FindClientManager()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == AssemblyName);
        if (assembly == null)
        {
            Status = "Loci assembly not found";
            return null;
        }

        var managerType = assembly.GetType(ManagerTypeName);
        if (managerType == null)
        {
            Status = $"Loci's manager type has changed shape (expected '{ManagerTypeName}') - reader is stale";
            return null;
        }

        var clientSm = managerType
            .GetField(ClientManagerFieldName, BindingFlags.NonPublic | BindingFlags.Static)?
            .GetValue(null);
        if (clientSm == null)
        {
            Status = $"Loci no longer has a static '{ClientManagerFieldName}' field, or hasn't initialized it yet";
            return null;
        }

        Status = "OK - found ClientSM";
        return clientSm;
    }

    private static FieldInfo? GetInstanceField(object obj, string name) =>
        obj.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance);
}
