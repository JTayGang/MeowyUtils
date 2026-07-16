using Newtonsoft.Json;

namespace StatusBridge.Bridge;

/// <summary>
/// Persisted record of which GUIDs the bridge has mirrored, in which direction, and with what
/// signature. This is the actual root cause behind essentially every "ghost status" bug found
/// while building this plugin (see README "Known issues"): the tracking dictionaries used to be
/// in-memory only, so every plugin reload (i.e. every game restart) started from a completely
/// blank slate and had to guess "is this GUID mine" from scratch using nothing but whether both
/// sides currently happen to agree - which silently breaks the moment only one side survives a
/// restart correctly. Persisting this is what actually fixes that, rather than continuing to
/// patch the ways the guessing could go wrong.
///
/// Deliberately its own file rather than folded into <see cref="StatusBridge.Configuration"/>:
/// this isn't a user-facing setting, it's internal bookkeeping with no UI representation that
/// changes far more often than settings do, and mixing the two would mean saving this on every
/// single mirror action re-writes the user's whole settings file too.
/// </summary>
internal sealed class BridgeState
{
    public Dictionary<Guid, BridgeSignature> MirroredIntoLoci { get; set; } = new();
    public Dictionary<Guid, BridgeSignature> MirroredIntoMoodles { get; set; } = new();

    private static string FilePath =>
        Path.Combine(Svc.PluginInterface.ConfigDirectory.FullName, "bridge_state.json");

    /// <summary>
    /// Never throws. A missing, corrupt, or unreadable file just returns a fresh, empty state -
    /// the GUID-matching adoption logic already in BridgeEngine is still there as a fallback for
    /// exactly this case (first-ever run, or a deleted/corrupted state file), it just won't be
    /// doing the heavy lifting on every single restart anymore.
    /// </summary>
    public static BridgeState Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new BridgeState();

            var loaded = JsonConvert.DeserializeObject<BridgeState>(File.ReadAllText(FilePath));
            return loaded ?? new BridgeState();
        }
        catch (Exception e)
        {
            Svc.Log.Warning(e, "[StatusBridge] Failed to load persisted bridge state - starting fresh (adoption logic will re-derive what it can).");
            return new BridgeState();
        }
    }

    /// <summary>Never throws - a failed save just means we fall back to guessing again next restart.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Svc.PluginInterface.ConfigDirectory.FullName);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this));
        }
        catch (Exception e)
        {
            Svc.Log.Warning(e, "[StatusBridge] Failed to save bridge state.");
        }
    }
}
