using Dalamud.Plugin;

namespace StatusBridge;

/// <summary>
/// StatusBridge is paused pending a rework - see the plugin listing / repo README for why. This
/// build does exactly one thing on load, then goes fully inert:
///
/// Delete every file any previous version of this plugin wrote to disk for this user: the
/// standard Dalamud plugin config file, and this plugin's whole config directory (which only ever
/// held bridge_state.json, but the whole folder is removed rather than naming files one by one, so
/// nothing from an even older version gets missed either).
///
/// Earlier drafts of this build also reverted the old experimental Moodles offset patch
/// (reflecting into Moodles' own assembly to flip one of its fields back to default) as a
/// defensive same-session safety net. Dropped: the field always resets on its own the moment
/// Moodles reloads fresh, every prior version already reverted it in Dispose() before this ever
/// ships, and the upcoming server update forces a full game restart for everyone anyway - so by
/// the time this build is loaded on a live client, there is nothing left to revert. No reflection
/// calls of any kind remain in this build.
///
/// No command handler, no config window, no framework hooks, no IPC to Moodles, Loci, or anything
/// else. Reintroduce the real feature set once a proper fix is ready - see git history for the
/// last full implementation.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Svc>();

        DeleteOwnPersistedFiles(pluginInterface);
    }

    /// <summary>Never throws - a failed delete just leaves a harmless stale file behind, which is no worse than what previous versions already did.</summary>
    private static void DeleteOwnPersistedFiles(IDalamudPluginInterface pluginInterface)
    {
        try
        {
            if (pluginInterface.ConfigFile.Exists)
                pluginInterface.ConfigFile.Delete();
        }
        catch (Exception e)
        {
            Svc.Log.Warning(e, "[StatusBridge] Couldn't delete this plugin's old config file.");
        }

        try
        {
            if (pluginInterface.ConfigDirectory.Exists)
                pluginInterface.ConfigDirectory.Delete(recursive: true);
        }
        catch (Exception e)
        {
            Svc.Log.Warning(e, "[StatusBridge] Couldn't delete this plugin's old config directory.");
        }
    }

    public void Dispose()
    {
        // Nothing to clean up - no handler, no hook, no window was ever registered by this build.
    }
}
