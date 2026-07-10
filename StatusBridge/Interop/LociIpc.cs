using Dalamud.Plugin.Ipc;

namespace StatusBridge.Interop;

/// <summary>
/// Thin wrapper around Loci's IPC surface. Same defensive try/catch philosophy as MoodlesIpc -
/// every call assumes Loci might not be there, might not be loaded, or might be reloading.
/// </summary>
internal sealed class LociIpc : IDisposable
{
    private const string RequiredLabel = "Loci";

    private readonly ICallGateSubscriber<(int Major, int Minor)> _apiVersion;
    private readonly ICallGateSubscriber<bool> _isEnabled;
    private readonly ICallGateSubscriber<nint, int, object?> _managerChanged;
    private readonly ICallGateSubscriber<List<LociStatusInfo>> _getManagerInfo;
    private readonly ICallGateSubscriber<LociStatusInfo, uint, int> _applyStatusInfo;
    private readonly ICallGateSubscriber<Guid, uint, int> _removeStatus;

    private bool _subscribedToChanges;

    /// <summary>True once Loci has answered a version probe successfully and reports itself enabled.</summary>
    public bool Available { get; private set; }

    /// <summary>Fires when the local player's live Loci status manager changes.</summary>
    public event Action? LocalStatusesChanged;

    public LociIpc()
    {
        _apiVersion = Svc.PluginInterface.GetIpcSubscriber<(int, int)>($"{RequiredLabel}.ApiVersion");
        _isEnabled = Svc.PluginInterface.GetIpcSubscriber<bool>($"{RequiredLabel}.IsEnabled");
        _managerChanged = Svc.PluginInterface.GetIpcSubscriber<nint, int, object?>($"{RequiredLabel}.ManagerChanged");
        _getManagerInfo = Svc.PluginInterface.GetIpcSubscriber<List<LociStatusInfo>>($"{RequiredLabel}.GetManagerInfo");
        _applyStatusInfo = Svc.PluginInterface.GetIpcSubscriber<LociStatusInfo, uint, int>($"{RequiredLabel}.ApplyStatusInfo");
        _removeStatus = Svc.PluginInterface.GetIpcSubscriber<Guid, uint, int>($"{RequiredLabel}.RemoveStatus");

        TrySubscribeToChanges();
        RefreshAvailability();
    }

    /// <summary>Cheap probe, safe to call regularly (e.g. once per reconciliation tick).</summary>
    public void RefreshAvailability()
    {
        var wasAvailable = Available;
        try
        {
            _ = _apiVersion.InvokeFunc(); // just needs to not throw
            Available = _isEnabled.InvokeFunc();
        }
        catch
        {
            Available = false;
        }

        if (Available && !wasAvailable)
        {
            Svc.Log.Information("[StatusBridge] Loci IPC became available.");
            TrySubscribeToChanges();
        }
        else if (!Available && wasAvailable)
        {
            Svc.Log.Information("[StatusBridge] Loci IPC became unavailable.");
        }
    }

    private void TrySubscribeToChanges()
    {
        if (_subscribedToChanges)
            return;

        try
        {
            _managerChanged.Subscribe(OnManagerChanged);
            _subscribedToChanges = true;
        }
        catch (Exception e)
        {
            Svc.Log.Debug(e, "[StatusBridge] Could not subscribe to Loci.ManagerChanged yet.");
        }
    }

    private void OnManagerChanged(nint address, int changeTypeRaw)
    {
        var local = Svc.ObjectTable.LocalPlayer;
        if (local != null && address == local.Address)
            LocalStatusesChanged?.Invoke();
    }

    /// <summary>Fetches the local player's currently-applied Loci statuses. Empty list on any failure.</summary>
    public List<LociStatusInfo> GetLocalStatuses()
    {
        if (!Available)
            return [];

        try
        {
            return _getManagerInfo.InvokeFunc() ?? [];
        }
        catch (Exception e)
        {
            Svc.Log.Warning(e, "[StatusBridge] Failed to read Loci statuses.");
            return [];
        }
    }

    /// <summary>
    /// Applies or updates a single status on the local player by data. Unlike Moodles, this has
    /// no broadcast/whitelist gate to worry about - Loci applies client-targeted data directly.
    /// key=0 means "don't lock it", so the user (or us) can freely remove/replace it later.
    /// </summary>
    public bool TryApply(LociStatusInfo status)
    {
        if (!Available)
            return false;

        try
        {
            var ec = (LociApiEc)_applyStatusInfo.InvokeFunc(status, 0);
            if (ec is LociApiEc.Success or LociApiEc.NoChange)
                return true;

            Svc.Log.Warning($"[StatusBridge] Loci rejected a mirrored status: {ec}");
            return false;
        }
        catch (Exception e)
        {
            Svc.Log.Warning(e, "[StatusBridge] Failed to apply a mirrored Loci status.");
            return false;
        }
    }

    public bool TryRemove(Guid guid)
    {
        if (!Available)
            return false;

        try
        {
            var ec = (LociApiEc)_removeStatus.InvokeFunc(guid, 0);
            if (ec is LociApiEc.Success or LociApiEc.NoChange or LociApiEc.DataNotFound)
                return true;

            Svc.Log.Warning($"[StatusBridge] Loci rejected removing a mirrored status: {ec}");
            return false;
        }
        catch (Exception e)
        {
            Svc.Log.Warning(e, "[StatusBridge] Failed to remove a mirrored Loci status.");
            return false;
        }
    }

    public void Dispose()
    {
        if (_subscribedToChanges)
        {
            try { _managerChanged.Unsubscribe(OnManagerChanged); }
            catch { /* best effort */ }
        }
    }
}
