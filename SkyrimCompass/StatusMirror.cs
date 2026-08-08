using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System.Text.Json;

using MoodlesStatusInfo = (
    int Version, System.Guid GUID, int IconID, string Title, string Description, string CustomVFXPath,
    long ExpireTicks, int Type, int Stacks, int StackSteps, uint Modifiers, System.Guid ChainedStatus,
    int ChainTrigger, string Applier, string Dispeller, bool Permanent);
using LociStatusInfo = (
    int Version, System.Guid GUID, uint IconID, string Title, string Description, string CustomVFXPath,
    long ExpireTicks, byte Type, int Stacks, int StackSteps, int StackToChain, uint Modifiers,
    System.Guid ChainedGUID, byte ChainType, int ChainTrigger, string Applier, string Dispeller);

namespace SkyrimCompass;

internal enum LociApiEc
{
    Success = 0, NoChange = 1, PartialSuccess = 2, TargetNotFound = 3, TargetInvalid = 4,
    DataNotFound = 5, DataInvalid = 6, ItemLocked = 7, InvalidKey = 8, ItemIsPersistent = 9,
    ClientForbidden = 10, FSPathFaulted = 11, UnkError = int.MaxValue,
}

internal readonly record struct MirrorSignature(
    long IconID, string Title, string Description, string CustomVFXPath, long ExpireTicks,
    int Type, int Stacks, int StackSteps, uint Modifiers, string Applier, string Dispeller)
{
    public static MirrorSignature FromMoodles(MoodlesStatusInfo m) => new(
        m.IconID, m.Title, m.Description, m.CustomVFXPath, m.ExpireTicks,
        m.Type, m.Stacks, m.StackSteps, m.Modifiers, m.Applier, m.Dispeller);

    public static MirrorSignature FromLoci(LociStatusInfo l) => new(
        l.IconID, l.Title, l.Description, l.CustomVFXPath, l.ExpireTicks,
        l.Type, l.Stacks, l.StackSteps, l.Modifiers, l.Applier, l.Dispeller);
}

internal static class MirrorConverter
{
    public static LociStatusInfo ToLoci(MoodlesStatusInfo m) => (
        Version: 1,
        GUID: m.GUID,
        IconID: (uint)Math.Max(0, m.IconID),
        Title: m.Title,
        Description: m.Description,
        CustomVFXPath: m.CustomVFXPath,
        ExpireTicks: m.ExpireTicks,
        Type: (byte)Math.Clamp(m.Type, 0, 2),
        Stacks: m.Stacks,
        StackSteps: m.StackSteps,
        StackToChain: 0,
        Modifiers: m.Modifiers,
        ChainedGUID: System.Guid.Empty,
        ChainType: (byte)0,
        ChainTrigger: 0,
        Applier: m.Applier,
        Dispeller: m.Dispeller);

    public static MoodlesStatusInfo ToMoodles(LociStatusInfo l) => (
        Version: 1,
        GUID: l.GUID,
        IconID: (int)l.IconID,
        Title: l.Title,
        Description: l.Description,
        CustomVFXPath: l.CustomVFXPath,
        ExpireTicks: l.ExpireTicks,
        Type: Math.Clamp((int)l.Type, 0, 2),
        Stacks: l.Stacks,
        StackSteps: l.StackSteps,
        Modifiers: l.Modifiers,
        ChainedStatus: System.Guid.Empty,
        ChainTrigger: 0,
        Applier: l.Applier,
        Dispeller: l.Dispeller,
        Permanent: false);
}

internal sealed class MirrorState
{
    public Dictionary<System.Guid, MirrorSignature> MirroredIntoLoci    { get; set; } = new();
    public Dictionary<System.Guid, MirrorSignature> MirroredIntoMoodles { get; set; } = new();

    private static string FilePath(IDalamudPluginInterface pluginInterface) =>
        Path.Combine(pluginInterface.ConfigDirectory.FullName, "status_mirror_state.json");

    public static MirrorState Load(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        try
        {
            var path = FilePath(pluginInterface);
            if (!File.Exists(path))
                return new MirrorState();

            var loaded = JsonSerializer.Deserialize<MirrorState>(File.ReadAllText(path));
            return loaded ?? new MirrorState();
        }
        catch
        {
            log.Warning("[SkyrimCompass] Failed to load persisted status-mirror state — starting fresh.");
            return new MirrorState();
        }
    }

    public void Save(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        try
        {
            var path = FilePath(pluginInterface);
            Directory.CreateDirectory(pluginInterface.ConfigDirectory.FullName);
            File.WriteAllText(path, JsonSerializer.Serialize(this));
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to save status-mirror state.");
        }
    }
}

internal sealed class MoodlesMirrorIpc : IDisposable
{
    private const int MinimumApiVersion = 4;

    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<int> version;
    private readonly ICallGateSubscriber<nint, object> statusManagerModified;
    private readonly ICallGateSubscriber<List<MoodlesStatusInfo>> getClientStatusManagerInfo;
    private readonly ICallGateSubscriber<MoodlesStatusInfo, IPlayerCharacter, object> addOrUpdateByPlayer;
    private readonly ICallGateSubscriber<System.Guid, IPlayerCharacter, object> removeByPlayer;

    private bool subscribedToChanges;

    public bool Available { get; private set; }
    public event Action? LocalStatusesChanged;

    public MoodlesMirrorIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        version = pluginInterface.GetIpcSubscriber<int>("Moodles.Version");
        statusManagerModified = pluginInterface.GetIpcSubscriber<nint, object>("Moodles.StatusManagerModified");
        getClientStatusManagerInfo = pluginInterface.GetIpcSubscriber<List<MoodlesStatusInfo>>("Moodles.GetClientStatusManagerInfoV2");
        addOrUpdateByPlayer = pluginInterface.GetIpcSubscriber<MoodlesStatusInfo, IPlayerCharacter, object>("Moodles.AddOrUpdateMoodleByDataByPlayerV2");
        removeByPlayer = pluginInterface.GetIpcSubscriber<System.Guid, IPlayerCharacter, object>("Moodles.RemoveMoodleByPlayerV2");

        TrySubscribeToChanges();
        RefreshAvailability();
    }

    public void RefreshAvailability()
    {
        var wasAvailable = Available;
        try { Available = version.InvokeFunc() >= MinimumApiVersion; }
        catch { Available = false; }

        if (Available && !wasAvailable)
        {
            log.Information("[SkyrimCompass] Moodles IPC became available.");
            TrySubscribeToChanges();
        }
        else if (!Available && wasAvailable)
        {
            log.Information("[SkyrimCompass] Moodles IPC became unavailable.");
        }
    }

    private void TrySubscribeToChanges()
    {
        if (subscribedToChanges) return;
        try
        {
            statusManagerModified.Subscribe(OnStatusManagerModified);
            subscribedToChanges = true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[SkyrimCompass] Could not subscribe to Moodles.StatusManagerModified yet.");
        }
    }

    private void OnStatusManagerModified(nint address) => LocalStatusesChanged?.Invoke();

    public List<MoodlesStatusInfo> GetLocalStatuses()
    {
        if (!Available) return [];
        try { return getClientStatusManagerInfo.InvokeFunc() ?? []; }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to read local Moodles statuses.");
            return [];
        }
    }

    public bool TryApply(MoodlesStatusInfo status, IPlayerCharacter localPlayer)
    {
        if (!Available) return false;
        try
        {
            addOrUpdateByPlayer.InvokeAction(status, localPlayer);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to apply a mirrored Moodle.");
            return false;
        }
    }

    public bool TryRemove(System.Guid guid, IPlayerCharacter localPlayer)
    {
        if (!Available) return false;
        try
        {
            removeByPlayer.InvokeAction(guid, localPlayer);
            return true;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to remove a mirrored Moodle.");
            return false;
        }
    }

    public void Dispose()
    {
        if (subscribedToChanges)
        {
            try { statusManagerModified.Unsubscribe(OnStatusManagerModified); }
            catch { }
        }
    }
}

internal sealed class LociMirrorIpc : IDisposable
{
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<(int Major, int Minor)> apiVersion;
    private readonly ICallGateSubscriber<bool> isEnabled;
    private readonly ICallGateSubscriber<nint, int, object?> managerChanged;
    private readonly ICallGateSubscriber<List<LociStatusInfo>> getManagerInfo;
    private readonly ICallGateSubscriber<LociStatusInfo, uint, int> applyStatusInfo;
    private readonly ICallGateSubscriber<System.Guid, uint, int> removeStatus;

    private bool subscribedToChanges;

    public bool Available { get; private set; }
    public event Action? LocalStatusesChanged;

    public LociMirrorIpc(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.log = log;
        apiVersion = pluginInterface.GetIpcSubscriber<(int, int)>("Loci.ApiVersion");
        isEnabled = pluginInterface.GetIpcSubscriber<bool>("Loci.IsEnabled");
        managerChanged = pluginInterface.GetIpcSubscriber<nint, int, object?>("Loci.ManagerChanged");
        getManagerInfo = pluginInterface.GetIpcSubscriber<List<LociStatusInfo>>("Loci.GetManagerInfo");
        applyStatusInfo = pluginInterface.GetIpcSubscriber<LociStatusInfo, uint, int>("Loci.ApplyStatusInfo");
        removeStatus = pluginInterface.GetIpcSubscriber<System.Guid, uint, int>("Loci.RemoveStatus");

        TrySubscribeToChanges();
        RefreshAvailability();
    }

    public void RefreshAvailability()
    {
        var wasAvailable = Available;
        try
        {
            _ = apiVersion.InvokeFunc();
            Available = isEnabled.InvokeFunc();
        }
        catch { Available = false; }

        if (Available && !wasAvailable)
        {
            log.Information("[SkyrimCompass] Loci IPC became available.");
            TrySubscribeToChanges();
        }
        else if (!Available && wasAvailable)
        {
            log.Information("[SkyrimCompass] Loci IPC became unavailable.");
        }
    }

    private void TrySubscribeToChanges()
    {
        if (subscribedToChanges) return;
        try
        {
            managerChanged.Subscribe(OnManagerChanged);
            subscribedToChanges = true;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[SkyrimCompass] Could not subscribe to Loci.ManagerChanged yet.");
        }
    }

    private void OnManagerChanged(nint address, int changeTypeRaw) => LocalStatusesChanged?.Invoke();

    public List<LociStatusInfo> GetLocalStatuses()
    {
        if (!Available) return [];
        try { return getManagerInfo.InvokeFunc() ?? []; }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to read local Loci statuses.");
            return [];
        }
    }

    public LociApiEc TryApply(LociStatusInfo status)
    {
        if (!Available) return LociApiEc.TargetInvalid;
        try
        {
            var ec = (LociApiEc)applyStatusInfo.InvokeFunc(status, 0u);
            if (ec is not (LociApiEc.Success or LociApiEc.NoChange))
                log.Warning($"[SkyrimCompass] Loci rejected a mirrored status: {ec}");
            return ec;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to apply a mirrored Loci status.");
            return LociApiEc.UnkError;
        }
    }

    public LociApiEc TryRemove(System.Guid guid)
    {
        if (!Available) return LociApiEc.TargetInvalid;
        try
        {
            var ec = (LociApiEc)removeStatus.InvokeFunc(guid, 0u);
            if (ec is not (LociApiEc.Success or LociApiEc.NoChange or LociApiEc.DataNotFound))
                log.Warning($"[SkyrimCompass] Loci rejected removing a mirrored status: {ec}");
            return ec;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[SkyrimCompass] Failed to remove a mirrored Loci status.");
            return LociApiEc.UnkError;
        }
    }

    public void Dispose()
    {
        if (subscribedToChanges)
        {
            try { managerChanged.Unsubscribe(OnManagerChanged); }
            catch { }
        }
    }
}

public sealed class StatusMirrorEngine : IDisposable
{
    private readonly Configuration pluginConfig;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;
    private readonly IFramework framework;

    private readonly MoodlesMirrorIpc moodles;
    private readonly LociMirrorIpc loci;

    private readonly MirrorState state;
    private bool stateDirty;

    private Dictionary<System.Guid, MirrorSignature> MirroredIntoLoci    => state.MirroredIntoLoci;
    private Dictionary<System.Guid, MirrorSignature> MirroredIntoMoodles => state.MirroredIntoMoodles;

    private readonly HashSet<System.Guid> knownLockedInLoci = new();
    private readonly Dictionary<System.Guid, DateTime> recentlyRemoved = new();
    private const double RecentlyRemovedGraceSeconds = 1;
    private readonly HashSet<System.Guid> supersededMoodleGhosts = new();
    private readonly HashSet<System.Guid> supersededLociGhosts = new();

    // Minimum time between reconciliations (even when dirty) to avoid starving upstream debounce
    private const float MinReconcileIntervalSeconds = 0.333f; // 333 ms

    // Debounce for state file saves
    private DateTime _nextStateSaveTime = DateTime.UtcNow;
    private const float StateSaveIntervalSeconds = 5.0f;

    // flag to force refresh of mirrors on change
    private bool mirrorsNeedRefresh;

    private bool IsRecentlyRemoved(System.Guid guid)
    {
        if (!recentlyRemoved.TryGetValue(guid, out var removedAt)) return false;
        if ((DateTime.UtcNow - removedAt).TotalSeconds <= RecentlyRemovedGraceSeconds) return true;
        recentlyRemoved.Remove(guid);
        return false;
    }

    private void PruneRecentlyRemoved()
    {
        if (recentlyRemoved.Count == 0) return;
        var now = DateTime.UtcNow;
        var expired = new List<System.Guid>();
        foreach (var kv in recentlyRemoved)
            if ((now - kv.Value).TotalSeconds > RecentlyRemovedGraceSeconds)
                expired.Add(kv.Key);
        foreach (var guid in expired)
            recentlyRemoved.Remove(guid);
    }

    private DateTime nextPeriodicReconcile = DateTime.MinValue;
    private volatile bool dirty = true;

    public bool MoodlesAvailable         => moodles.Available;
    public bool LociAvailable            => loci.Available;
    public int  MirroredIntoLociCount    => MirroredIntoLoci.Count;
    public int  MirroredIntoMoodlesCount => MirroredIntoMoodles.Count;
    public int  LockedMirrorCount        => knownLockedInLoci.Count;

    public StatusMirrorEngine(
        IDalamudPluginInterface pluginInterface, IFramework framework, IObjectTable objectTable,
        IPluginLog log, Configuration config)
    {
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.objectTable = objectTable;
        this.log = log;
        pluginConfig = config;

        moodles = new MoodlesMirrorIpc(pluginInterface, log);
        loci = new LociMirrorIpc(pluginInterface, log);
        state = MirrorState.Load(pluginInterface, log);

        // set mirrorsNeedRefresh on any change
        moodles.LocalStatusesChanged += () => { dirty = true; mirrorsNeedRefresh = true; };
        loci.LocalStatusesChanged += () => { dirty = true; mirrorsNeedRefresh = true; };
        framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework fw)
    {
        var now = DateTime.UtcNow;
        // Enforce minimum interval even when dirty
        if (now < nextPeriodicReconcile)
            return;

        // Only run if dirty or periodic check is due
        if (!dirty && now < nextPeriodicReconcile + TimeSpan.FromSeconds(1))
            return;

        dirty = false;
        nextPeriodicReconcile = now + TimeSpan.FromSeconds(MinReconcileIntervalSeconds);

        try { Reconcile(); }
        catch (Exception ex) { log.Error(ex, "[SkyrimCompass] Reconcile threw."); }

        // Debounced state save: at most once every 5 seconds
        if (stateDirty && now >= _nextStateSaveTime)
        {
            state.Save(pluginInterface, log);
            stateDirty = false;
            _nextStateSaveTime = now + TimeSpan.FromSeconds(StateSaveIntervalSeconds);
        }
    }

    private void MarkStateDirty() => stateDirty = true;

    private void Reconcile()
    {
        moodles.RefreshAvailability();
        loci.RefreshAvailability();
        PruneRecentlyRemoved();

        if (!pluginConfig.MirrorMoodlesLoci) return;
        if (objectTable.LocalPlayer is not { } localPlayer) return;
        if (!moodles.Available || !loci.Available) return;

        var moodleList = moodles.GetLocalStatuses();
        var lociList = loci.GetLocalStatuses();

        // pass the refresh flag
        SyncMoodlesToLoci(moodleList, lociList, mirrorsNeedRefresh);
        SyncLociToMoodles(lociList, moodleList, localPlayer, mirrorsNeedRefresh);

        // clear the flag after both directions have processed
        mirrorsNeedRefresh = false;
    }

    // ─── LINQ‑free version of SyncMoodlesToLoci ──────────────────────────
    private void SyncMoodlesToLoci(List<MoodlesStatusInfo> moodleList, List<LociStatusInfo> lociExisting, bool forceRefresh)
    {
        // Build fast lookup sets
        var allMoodleGuids = new HashSet<Guid>(moodleList.Count);
        var moodleDict = new Dictionary<Guid, MoodlesStatusInfo>(moodleList.Count);
        foreach (var m in moodleList)
        {
            allMoodleGuids.Add(m.GUID);
            moodleDict[m.GUID] = m;
        }

        var lociGuids = new HashSet<Guid>(lociExisting.Count);
        foreach (var l in lociExisting)
            lociGuids.Add(l.GUID);

        // Remove superseded ghosts
        supersededMoodleGhosts.RemoveWhere(g => !allMoodleGuids.Contains(g));

        // Force re-apply all existing mirrored Moodles that are still present
        if (forceRefresh)
        {
            // iterate over a snapshot of keys
            var keys = new List<Guid>(MirroredIntoLoci.Keys);
            foreach (var guid in keys)
            {
                if (!allMoodleGuids.Contains(guid))
                    continue;

                var src = moodleDict[guid];
                var sig = MirrorSignature.FromMoodles(src);

                // Skip if already tracked and signature unchanged
                if (MirroredIntoLoci.TryGetValue(guid, out var existingSig) && existingSig == sig)
                    continue;

                var converted = MirrorConverter.ToLoci(src);
                var ec = loci.TryApply(converted);
                if (ec is LociApiEc.Success or LociApiEc.NoChange)
                {
                    MirroredIntoLoci[guid] = sig;
                    MarkStateDirty();
                }
                else if (ec == LociApiEc.ItemLocked)
                {
                    knownLockedInLoci.Add(guid);
                }
            }
        }

        // native Moodles (not already mirrored into Loci, not recently removed, not ghosted)
        var nativeMoodles = new List<MoodlesStatusInfo>();
        foreach (var m in moodleList)
        {
            if (!MirroredIntoMoodles.ContainsKey(m.GUID) &&
                !IsRecentlyRemoved(m.GUID) &&
                !supersededMoodleGhosts.Contains(m.GUID))
            {
                nativeMoodles.Add(m);
            }
        }

        var staleTwins = new List<Guid>();

        foreach (var m in nativeMoodles)
        {
            var sig = MirrorSignature.FromMoodles(m);
            bool alreadyTracked = MirroredIntoLoci.TryGetValue(m.GUID, out var knownSig);

            if (alreadyTracked && knownSig == sig && lociGuids.Contains(m.GUID))
                continue;

            if (!alreadyTracked && lociGuids.Contains(m.GUID))
            {
                MirroredIntoLoci[m.GUID] = sig;
                MarkStateDirty();
                continue;
            }

            if (!alreadyTracked)
            {
                foreach (var kv in MirroredIntoLoci)
                {
                    if (kv.Key != m.GUID && kv.Value == sig && allMoodleGuids.Contains(kv.Key))
                    {
                        staleTwins.Add(kv.Key);
                        break;
                    }
                }
            }

            var converted = MirrorConverter.ToLoci(m);
            var ec = loci.TryApply(converted);
            if (ec is LociApiEc.Success or LociApiEc.NoChange)
            {
                MirroredIntoLoci[m.GUID] = sig;
                MarkStateDirty();
            }
        }

        // Remove stale twins (distinct)
        var staleSet = new HashSet<Guid>(staleTwins);
        foreach (var stale in staleSet)
        {
            loci.TryRemove(stale);
            recentlyRemoved[stale] = DateTime.UtcNow;
            supersededMoodleGhosts.Add(stale);
        }

        // Remove mirrors for Moodles that no longer exist
        var toRemove = new List<Guid>();
        foreach (var kv in MirroredIntoLoci)
            if (!allMoodleGuids.Contains(kv.Key))
                toRemove.Add(kv.Key);

        foreach (var guid in toRemove)
        {
            if (lociGuids.Contains(guid))
            {
                var ec = loci.TryRemove(guid);
                if (ec == LociApiEc.ItemLocked)
                {
                    if (knownLockedInLoci.Add(guid))
                        log.Warning($"[SkyrimCompass] Loci status locked: {guid}");
                }
                else
                {
                    knownLockedInLoci.Remove(guid);
                }
                continue;
            }

            MirroredIntoLoci.Remove(guid);
            MarkStateDirty();
            knownLockedInLoci.Remove(guid);
            recentlyRemoved[guid] = DateTime.UtcNow;
        }
    }

    // ─── LINQ‑free version of SyncLociToMoodles ──────────────────────────
    private void SyncLociToMoodles(List<LociStatusInfo> lociList, List<MoodlesStatusInfo> moodlesExisting,
                                  IPlayerCharacter localPlayer, bool forceRefresh)
    {
        // Build fast lookup sets
        var allLociGuids = new HashSet<Guid>(lociList.Count);
        var lociDict = new Dictionary<Guid, LociStatusInfo>(lociList.Count);
        foreach (var l in lociList)
        {
            allLociGuids.Add(l.GUID);
            lociDict[l.GUID] = l;
        }

        var moodleGuids = new HashSet<Guid>(moodlesExisting.Count);
        foreach (var m in moodlesExisting)
            moodleGuids.Add(m.GUID);

        // Remove superseded ghosts
        supersededLociGhosts.RemoveWhere(g => !allLociGuids.Contains(g));

        // Force re-apply all existing mirrored Loci that are still present
        if (forceRefresh)
        {
            var keys = new List<Guid>(MirroredIntoMoodles.Keys);
            foreach (var guid in keys)
            {
                if (!allLociGuids.Contains(guid))
                    continue;

                var src = lociDict[guid];
                var sig = MirrorSignature.FromLoci(src);

                // Skip if already tracked and signature unchanged
                if (MirroredIntoMoodles.TryGetValue(guid, out var existingSig) && existingSig == sig)
                    continue;

                var converted = MirrorConverter.ToMoodles(src);
                if (moodles.TryApply(converted, localPlayer))
                {
                    MirroredIntoMoodles[guid] = sig;
                    MarkStateDirty();
                }
            }
        }

        // native Loci (not already mirrored into Moodles, not recently removed, not ghosted)
        var nativeLoci = new List<LociStatusInfo>();
        foreach (var l in lociList)
        {
            if (!MirroredIntoLoci.ContainsKey(l.GUID) &&
                !IsRecentlyRemoved(l.GUID) &&
                !supersededLociGhosts.Contains(l.GUID))
            {
                nativeLoci.Add(l);
            }
        }

        var staleTwins = new List<Guid>();

        foreach (var l in nativeLoci)
        {
            var sig = MirrorSignature.FromLoci(l);
            bool alreadyTracked = MirroredIntoMoodles.TryGetValue(l.GUID, out var knownSig);

            if (alreadyTracked && knownSig == sig && moodleGuids.Contains(l.GUID))
                continue;

            if (!alreadyTracked && moodleGuids.Contains(l.GUID))
            {
                MirroredIntoMoodles[l.GUID] = sig;
                MarkStateDirty();
                continue;
            }

            if (!alreadyTracked)
            {
                foreach (var kv in MirroredIntoMoodles)
                {
                    if (kv.Key != l.GUID && kv.Value == sig && allLociGuids.Contains(kv.Key))
                    {
                        staleTwins.Add(kv.Key);
                        break;
                    }
                }
            }

            var converted = MirrorConverter.ToMoodles(l);
            if (moodles.TryApply(converted, localPlayer))
            {
                MirroredIntoMoodles[l.GUID] = sig;
                MarkStateDirty();
            }
        }

        // Remove stale twins (distinct)
        var staleSet = new HashSet<Guid>(staleTwins);
        foreach (var stale in staleSet)
        {
            if (MirroredIntoMoodles.ContainsKey(stale))
            {
                moodles.TryRemove(stale, localPlayer);
                recentlyRemoved[stale] = DateTime.UtcNow;
                supersededLociGhosts.Add(stale);
            }
        }

        // Remove mirrors for Loci that no longer exist
        var toRemove = new List<Guid>();
        foreach (var kv in MirroredIntoMoodles)
            if (!allLociGuids.Contains(kv.Key))
                toRemove.Add(kv.Key);

        foreach (var guid in toRemove)
        {
            if (moodleGuids.Contains(guid))
            {
                moodles.TryRemove(guid, localPlayer);
                continue;
            }

            MirroredIntoMoodles.Remove(guid);
            MarkStateDirty();
            recentlyRemoved[guid] = DateTime.UtcNow;
        }
    }

    public void ClearAllMirrors()
    {
        // Remove all MirroredIntoLoci
        var keysLoci = new List<Guid>(MirroredIntoLoci.Keys);
        foreach (var guid in keysLoci)
            loci.TryRemove(guid);

        // Get still‑present Loci GUIDs
        var lociStillPresent = new HashSet<Guid>();
        foreach (var l in loci.GetLocalStatuses())
            lociStillPresent.Add(l.GUID);

        // Remove entries whose GUID is no longer present in Loci
        var toRemoveLoci = new List<Guid>();
        foreach (var kv in MirroredIntoLoci)
            if (!lociStillPresent.Contains(kv.Key))
                toRemoveLoci.Add(kv.Key);

        foreach (var guid in toRemoveLoci)
        {
            MirroredIntoLoci.Remove(guid);
            MarkStateDirty();
            knownLockedInLoci.Remove(guid);
        }

        // Remove all MirroredIntoMoodles
        if (objectTable.LocalPlayer is { } localPlayer)
        {
            var keysMoodles = new List<Guid>(MirroredIntoMoodles.Keys);
            foreach (var guid in keysMoodles)
                moodles.TryRemove(guid, localPlayer);
        }

        // Get still‑present Moodle GUIDs
        var moodlesStillPresent = new HashSet<Guid>();
        foreach (var m in moodles.GetLocalStatuses())
            moodlesStillPresent.Add(m.GUID);

        // Remove entries whose GUID is no longer present in Moodles
        var toRemoveMoodles = new List<Guid>();
        foreach (var kv in MirroredIntoMoodles)
            if (!moodlesStillPresent.Contains(kv.Key))
                toRemoveMoodles.Add(kv.Key);

        foreach (var guid in toRemoveMoodles)
        {
            MirroredIntoMoodles.Remove(guid);
            MarkStateDirty();
        }

        recentlyRemoved.Clear();
        supersededMoodleGhosts.Clear();
        supersededLociGhosts.Clear();

        log.Information("[SkyrimCompass] Cleared all mirrored statuses.");
    }

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;

        // Save any pending state on shutdown
        if (stateDirty)
            state.Save(pluginInterface, log);

        moodles.Dispose();
        loci.Dispose();
    }
}