using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Ipc;

namespace StatusBridge.Interop;

/// <summary>
/// Thin wrapper around Moodles' IPC surface. Every call is defensively try/caught - Moodles
/// might not be installed, might not be loaded yet, or might be mid-reload, and none of that
/// should ever crash or corrupt anything on our end.
/// </summary>
internal sealed class MoodlesIpc : IDisposable
{
    private const string RequiredLabel = "Moodles";
    private const int MinimumApiVersion = 4;

    private readonly ICallGateSubscriber<int> _version;
    private readonly ICallGateSubscriber<nint, object> _statusManagerModified;
    private readonly ICallGateSubscriber<List<MoodlesStatusInfo>> _getClientStatusManagerInfo;
    private readonly ICallGateSubscriber<MoodlesStatusInfo, IPlayerCharacter, object> _addOrUpdateByPlayer;
    private readonly ICallGateSubscriber<Guid, IPlayerCharacter, object> _removeByPlayer;

    private bool _subscribedToChanges;

    /// <summary>True once Moodles has answered a version probe successfully.</summary>
    public bool Available { get; private set; }

    /// <summary>Fires when the local player's live Moodles status manager changes.</summary>
    public event Action? LocalStatusesChanged;

    public MoodlesIpc()
    {
        _version = Svc.PluginInterface.GetIpcSubscriber<int>($"{RequiredLabel}.Version");
        _statusManagerModified = Svc.PluginInterface.GetIpcSubscriber<nint, object>($"{RequiredLabel}.StatusManagerModified");
        _getClientStatusManagerInfo = Svc.PluginInterface.GetIpcSubscriber<List<MoodlesStatusInfo>>($"{RequiredLabel}.GetClientStatusManagerInfoV2");
        _addOrUpdateByPlayer = Svc.PluginInterface.GetIpcSubscriber<MoodlesStatusInfo, IPlayerCharacter, object>($"{RequiredLabel}.AddOrUpdateMoodleByDataByPlayerV2");
        _removeByPlayer = Svc.PluginInterface.GetIpcSubscriber<Guid, IPlayerCharacter, object>($"{RequiredLabel}.RemoveMoodleByPlayerV2");

        TrySubscribeToChanges();
        RefreshAvailability();
    }

    /// <summary>Cheap probe, safe to call regularly (e.g. once per reconciliation tick).</summary>
    public void RefreshAvailability()
    {
        var wasAvailable = Available;
        try
        {
            Available = _version.InvokeFunc() >= MinimumApiVersion;
        }
        catch
        {
            Available = false;
        }

        if (Available && !wasAvailable)
        {
            Svc.Log.Information("[StatusBridge] Moodles IPC became available.");
            TrySubscribeToChanges();
        }
        else if (!Available && wasAvailable)
        {
            Svc.Log.Information("[StatusBridge] Moodles IPC became unavailable.");
        }
    }

    private void TrySubscribeToChanges()
    {
        if (_subscribedToChanges)
            return;

        try
        {
            _statusManagerModified.Subscribe(OnStatusManagerModified);
            _subscribedToChanges = true;
        }
        catch (Exception e)
        {
            Svc.Log.Debug(e, "[StatusBridge] Could not subscribe to Moodles.StatusManagerModified yet.");
        }
    }

    private void OnStatusManagerModified(nint address)
    {
        var local = Svc.ObjectTable.LocalPlayer;
        if (local != null && address == local.Address)
            LocalStatusesChanged?.Invoke();
    }

    /// <summary>Fetches the local player's currently-applied Moodles. Empty list on any failure.</summary>
    public List<MoodlesStatusInfo> GetLocalStatuses()
    {
        if (!Available)
            return [];

        try
        {
            return _getClientStatusManagerInfo.InvokeFunc() ?? [];
        }
        catch (Exception e)
        {
            Svc.Log.Warning(e, "[StatusBridge] Failed to read Moodles statuses.");
            return [];
        }
    }

    /// <summary>
    /// Applies or updates a single status on the local player by data (not by saved-library GUID).
    /// Requires the user to have "Allow applying moodles from everyone" (broadcast) enabled in
    /// Moodles' own settings - without it Moodles silently rejects data-based applies from other
    /// plugins. We can't detect that setting via IPC, so we just document it.
    /// </summary>
    public bool TryApply(MoodlesStatusInfo status)
    {
        var local = Svc.ObjectTable.LocalPlayer;
        if (!Available || local == null)
            return false;

        try
        {
            _addOrUpdateByPlayer.InvokeAction(status, local);
            return true;
        }
        catch (Exception e)
        {
            Svc.Log.Warning(e, "[StatusBridge] Failed to apply a mirrored Moodle.");
            return false;
        }
    }

    public bool TryRemove(Guid guid)
    {
        var local = Svc.ObjectTable.LocalPlayer;
        if (!Available || local == null)
            return false;

        try
        {
            _removeByPlayer.InvokeAction(guid, local);
            return true;
        }
        catch (Exception e)
        {
            Svc.Log.Warning(e, "[StatusBridge] Failed to remove a mirrored Moodle.");
            return false;
        }
    }

    public void Dispose()
    {
        if (_subscribedToChanges)
        {
            try { _statusManagerModified.Unsubscribe(OnStatusManagerModified); }
            catch { /* best effort */ }
        }
    }
}
