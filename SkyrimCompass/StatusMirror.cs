using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using System.Text.Json;

using MoodlesStatusInfo = (int Version, System.Guid GUID, int IconID, string Title, string Description, string CustomVFXPath,
    long ExpireTicks, int Type, int Stacks, int StackSteps, uint Modifiers, System.Guid ChainedStatus,
    int ChainTrigger, string Applier, string Dispeller, bool Permanent);
using LociStatusInfo = (int Version, System.Guid GUID, uint IconID, string Title, string Description, string CustomVFXPath,
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
    public Dictionary<System.Guid, MirrorSignature> MirroredIntoLoci { get; set; } = new();
    public Dictionary<System.Guid, MirrorSignature> MirroredIntoMoodles { get; set; } = new();

    private static string FilePath(IDalamudPluginInterface pi) =>
        Path.Combine(pi.ConfigDirectory.FullName, "status_mirror_state.json");

    public static MirrorState Load(IDalamudPluginInterface pi, IPluginLog log)
    {
        try
        {
            var path = FilePath(pi);
            if (!File.Exists(path)) return new MirrorState();
            var loaded = JsonSerializer.Deserialize<MirrorState>(File.ReadAllText(path));
            return loaded ?? new MirrorState();
        }
        catch
        {
            log.Warning("[SkyrimCompass] Failed to load persisted status-mirror state — starting fresh.");
            return new MirrorState();
        }
    }

    public void Save(IDalamudPluginInterface pi, IPluginLog log)
    {
        try
        {
            var path = FilePath(pi);
            Directory.CreateDirectory(pi.ConfigDirectory.FullName);
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
    private const int MinApiVersion = 4;
    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<int> _ver;
    private readonly ICallGateSubscriber<nint, object> _modified;
    private readonly ICallGateSubscriber<List<MoodlesStatusInfo>> _get;
    private readonly ICallGateSubscriber<MoodlesStatusInfo, IPlayerCharacter, object> _add;
    private readonly ICallGateSubscriber<System.Guid, IPlayerCharacter, object> _remove;
    private bool _subscribed;
    public bool Available { get; private set; }
    public event Action? LocalStatusesChanged;

    public MoodlesMirrorIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        _ver = pi.GetIpcSubscriber<int>("Moodles.Version");
        _modified = pi.GetIpcSubscriber<nint, object>("Moodles.StatusManagerModified");
        _get = pi.GetIpcSubscriber<List<MoodlesStatusInfo>>("Moodles.GetClientStatusManagerInfoV2");
        _add = pi.GetIpcSubscriber<MoodlesStatusInfo, IPlayerCharacter, object>("Moodles.AddOrUpdateMoodleByDataByPlayerV2");
        _remove = pi.GetIpcSubscriber<System.Guid, IPlayerCharacter, object>("Moodles.RemoveMoodleByPlayerV2");
        TrySubscribe();
        Refresh();
    }

    public void RefreshAvailability() => Refresh();
    private void Refresh()
    {
        var was = Available;
        try { Available = _ver.InvokeFunc() >= MinApiVersion; } catch { Available = false; }
        if (Available && !was) { _log.Information("[SkyrimCompass] Moodles IPC became available."); TrySubscribe(); }
        else if (!Available && was) _log.Information("[SkyrimCompass] Moodles IPC became unavailable.");
    }

    private void TrySubscribe()
    {
        if (_subscribed) return;
        try { _modified.Subscribe(OnModified); _subscribed = true; }
        catch (Exception ex) { _log.Debug(ex, "[SkyrimCompass] Could not subscribe to Moodles.StatusManagerModified yet."); }
    }

    private void OnModified(nint addr) => LocalStatusesChanged?.Invoke();

    public List<MoodlesStatusInfo> GetLocalStatuses()
    {
        if (!Available) return [];
        try { return _get.InvokeFunc() ?? []; }
        catch (Exception ex) { _log.Warning(ex, "[SkyrimCompass] Failed to read local Moodles statuses."); return []; }
    }

    public bool TryApply(MoodlesStatusInfo status, IPlayerCharacter local)
    {
        if (!Available) return false;
        try { _add.InvokeAction(status, local); return true; }
        catch (Exception ex) { _log.Warning(ex, "[SkyrimCompass] Failed to apply a mirrored Moodle."); return false; }
    }

    public bool TryRemove(System.Guid guid, IPlayerCharacter local)
    {
        if (!Available) return false;
        try { _remove.InvokeAction(guid, local); return true; }
        catch (Exception ex) { _log.Warning(ex, "[SkyrimCompass] Failed to remove a mirrored Moodle."); return false; }
    }

    public void Dispose()
    {
        if (_subscribed) try { _modified.Unsubscribe(OnModified); } catch { }
    }
}

internal sealed class LociMirrorIpc : IDisposable
{
    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<(int, int)> _ver;
    private readonly ICallGateSubscriber<bool> _enabled;
    private readonly ICallGateSubscriber<nint, int, object?> _modified;
    private readonly ICallGateSubscriber<List<LociStatusInfo>> _get;
    private readonly ICallGateSubscriber<LociStatusInfo, uint, int> _apply;
    private readonly ICallGateSubscriber<System.Guid, uint, int> _remove;
    private bool _subscribed;
    public bool Available { get; private set; }
    public event Action? LocalStatusesChanged;

    public LociMirrorIpc(IDalamudPluginInterface pi, IPluginLog log)
    {
        _log = log;
        _ver = pi.GetIpcSubscriber<(int, int)>("Loci.ApiVersion");
        _enabled = pi.GetIpcSubscriber<bool>("Loci.IsEnabled");
        _modified = pi.GetIpcSubscriber<nint, int, object?>("Loci.ManagerChanged");
        _get = pi.GetIpcSubscriber<List<LociStatusInfo>>("Loci.GetManagerInfo");
        _apply = pi.GetIpcSubscriber<LociStatusInfo, uint, int>("Loci.ApplyStatusInfo");
        _remove = pi.GetIpcSubscriber<System.Guid, uint, int>("Loci.RemoveStatus");
        TrySubscribe();
        Refresh();
    }

    public void RefreshAvailability() => Refresh();
    private void Refresh()
    {
        var was = Available;
        try { _ver.InvokeFunc(); Available = _enabled.InvokeFunc(); }
        catch { Available = false; }
        if (Available && !was) { _log.Information("[SkyrimCompass] Loci IPC became available."); TrySubscribe(); }
        else if (!Available && was) _log.Information("[SkyrimCompass] Loci IPC became unavailable.");
    }

    private void TrySubscribe()
    {
        if (_subscribed) return;
        try { _modified.Subscribe(OnModified); _subscribed = true; }
        catch (Exception ex) { _log.Debug(ex, "[SkyrimCompass] Could not subscribe to Loci.ManagerChanged yet."); }
    }

    private void OnModified(nint addr, int type) => LocalStatusesChanged?.Invoke();

    public List<LociStatusInfo> GetLocalStatuses()
    {
        if (!Available) return [];
        try { return _get.InvokeFunc() ?? []; }
        catch (Exception ex) { _log.Warning(ex, "[SkyrimCompass] Failed to read local Loci statuses."); return []; }
    }

    public LociApiEc TryApply(LociStatusInfo status)
    {
        if (!Available) return LociApiEc.TargetInvalid;
        try
        {
            var ec = (LociApiEc)_apply.InvokeFunc(status, 0u);
            if (ec is not (LociApiEc.Success or LociApiEc.NoChange))
                _log.Warning($"[SkyrimCompass] Loci rejected a mirrored status: {ec}");
            return ec;
        }
        catch (Exception ex) { _log.Warning(ex, "[SkyrimCompass] Failed to apply a mirrored Loci status."); return LociApiEc.UnkError; }
    }

    public LociApiEc TryRemove(System.Guid guid)
    {
        if (!Available) return LociApiEc.TargetInvalid;
        try
        {
            var ec = (LociApiEc)_remove.InvokeFunc(guid, 0u);
            if (ec is not (LociApiEc.Success or LociApiEc.NoChange or LociApiEc.DataNotFound))
                _log.Warning($"[SkyrimCompass] Loci rejected removing a mirrored status: {ec}");
            return ec;
        }
        catch (Exception ex) { _log.Warning(ex, "[SkyrimCompass] Failed to remove a mirrored Loci status."); return LociApiEc.UnkError; }
    }

    public void Dispose()
    {
        if (_subscribed) try { _modified.Unsubscribe(OnModified); } catch { }
    }
}

public sealed class StatusMirrorEngine : IDisposable
{
    private readonly Configuration _cfg;
    private readonly IDalamudPluginInterface _pi;
    private readonly IObjectTable _ot;
    private readonly IPluginLog _log;
    private readonly IFramework _fw;
    private readonly MoodlesMirrorIpc _moodles;
    private readonly LociMirrorIpc _loci;
    private readonly MirrorState _state;
    private bool _dirty;
    private Dictionary<System.Guid, MirrorSignature> MirroredIntoLoci => _state.MirroredIntoLoci;
    private Dictionary<System.Guid, MirrorSignature> MirroredIntoMoodles => _state.MirroredIntoMoodles;
    private readonly HashSet<System.Guid> _locked = new();
    private readonly Dictionary<System.Guid, DateTime> _recentRemoved = new();
    private const double RemovedGraceSecs = 1;
    private readonly HashSet<System.Guid> _supersededMoodleGhosts = new();
    private readonly HashSet<System.Guid> _supersededLociGhosts = new();
    private const float MinReconcileInterval = 0.333f;
    private DateTime _nextSave = DateTime.UtcNow;
    private const float SaveIntervalSecs = 5.0f;
    private bool _mirrorsNeedRefresh;
    private DateTime _nextReconcile = DateTime.MinValue;
    private volatile bool _pendingReconcile = true;

    public bool MoodlesAvailable => _moodles.Available;
    public bool LociAvailable => _loci.Available;
    public int MirroredIntoLociCount => MirroredIntoLoci.Count;
    public int MirroredIntoMoodlesCount => MirroredIntoMoodles.Count;
    public int LockedMirrorCount => _locked.Count;

    public StatusMirrorEngine(IDalamudPluginInterface pi, IFramework fw, IObjectTable ot, IPluginLog log, Configuration cfg)
    {
        _pi = pi; _fw = fw; _ot = ot; _log = log; _cfg = cfg;
        _moodles = new MoodlesMirrorIpc(pi, log);
        _loci = new LociMirrorIpc(pi, log);
        _state = MirrorState.Load(pi, log);
        _moodles.LocalStatusesChanged += () => { _pendingReconcile = true; _mirrorsNeedRefresh = true; };
        _loci.LocalStatusesChanged += () => { _pendingReconcile = true; _mirrorsNeedRefresh = true; };
        fw.Update += OnUpdate;
    }

    private bool IsRecentlyRemoved(System.Guid g)
    {
        if (!_recentRemoved.TryGetValue(g, out var t)) return false;
        if ((DateTime.UtcNow - t).TotalSeconds <= RemovedGraceSecs) return true;
        _recentRemoved.Remove(g);
        return false;
    }

    private void PruneRecent()
    {
        if (_recentRemoved.Count == 0) return;
        var now = DateTime.UtcNow;
        var expired = new List<System.Guid>();
        foreach (var kv in _recentRemoved)
            if ((now - kv.Value).TotalSeconds > RemovedGraceSecs)
                expired.Add(kv.Key);
        foreach (var g in expired) _recentRemoved.Remove(g);
    }

    private void MarkDirty() => _dirty = true;

    private void OnUpdate(IFramework fw)
    {
        var now = DateTime.UtcNow;
        if (now < _nextReconcile) return;
        if (!_pendingReconcile && now < _nextReconcile + TimeSpan.FromSeconds(1)) return;
        _pendingReconcile = false;
        _nextReconcile = now + TimeSpan.FromSeconds(MinReconcileInterval);
        try { Reconcile(); }
        catch (Exception ex) { _log.Error(ex, "[SkyrimCompass] Reconcile threw."); }
        if (_dirty && now >= _nextSave)
        {
            _state.Save(_pi, _log);
            _dirty = false;
            _nextSave = now + TimeSpan.FromSeconds(SaveIntervalSecs);
        }
    }

    private void Reconcile()
    {
        _moodles.RefreshAvailability();
        _loci.RefreshAvailability();
        PruneRecent();
        if (!_cfg.MirrorMoodlesLoci) return;
        if (_ot.LocalPlayer is not { } local) return;
        if (!_moodles.Available || !_loci.Available) return;
        var moodleList = _moodles.GetLocalStatuses();
        var lociList = _loci.GetLocalStatuses();
        SyncMoodlesToLoci(moodleList, lociList, _mirrorsNeedRefresh);
        SyncLociToMoodles(lociList, moodleList, local, _mirrorsNeedRefresh);
        _mirrorsNeedRefresh = false;
    }

    private void SyncMoodlesToLoci(List<MoodlesStatusInfo> moodleList, List<LociStatusInfo> lociExisting, bool force)
    {
        var allMoodleGuids = new HashSet<Guid>(moodleList.Count);
        var moodleDict = new Dictionary<Guid, MoodlesStatusInfo>(moodleList.Count);
        foreach (var m in moodleList) { allMoodleGuids.Add(m.GUID); moodleDict[m.GUID] = m; }
        var lociGuids = new HashSet<Guid>(lociExisting.Count);
        foreach (var l in lociExisting) lociGuids.Add(l.GUID);
        _supersededMoodleGhosts.RemoveWhere(g => !allMoodleGuids.Contains(g));

        if (force)
        {
            var keys = new List<Guid>(MirroredIntoLoci.Keys);
            foreach (var g in keys)
            {
                if (!allMoodleGuids.Contains(g)) continue;
                var src = moodleDict[g];
                var sig = MirrorSignature.FromMoodles(src);
                if (MirroredIntoLoci.TryGetValue(g, out var existing) && existing == sig) continue;
                var conv = MirrorConverter.ToLoci(src);
                var ec = _loci.TryApply(conv);
                if (ec is LociApiEc.Success or LociApiEc.NoChange)
                { MirroredIntoLoci[g] = sig; MarkDirty(); }
                else if (ec == LociApiEc.ItemLocked) _locked.Add(g);
            }
        }

        var native = new List<MoodlesStatusInfo>();
        foreach (var m in moodleList)
            if (!MirroredIntoMoodles.ContainsKey(m.GUID) && !IsRecentlyRemoved(m.GUID) && !_supersededMoodleGhosts.Contains(m.GUID))
                native.Add(m);

        var staleTwins = new List<Guid>();
        foreach (var m in native)
        {
            var sig = MirrorSignature.FromMoodles(m);
            bool already = MirroredIntoLoci.TryGetValue(m.GUID, out var known);
            if (already && known == sig && lociGuids.Contains(m.GUID)) continue;
            if (!already && lociGuids.Contains(m.GUID)) { MirroredIntoLoci[m.GUID] = sig; MarkDirty(); continue; }
            if (!already)
            {
                foreach (var kv in MirroredIntoLoci)
                    if (kv.Key != m.GUID && kv.Value == sig && allMoodleGuids.Contains(kv.Key))
                    { staleTwins.Add(kv.Key); break; }
            }
            var conv = MirrorConverter.ToLoci(m);
            var ec = _loci.TryApply(conv);
            if (ec is LociApiEc.Success or LociApiEc.NoChange)
            { MirroredIntoLoci[m.GUID] = sig; MarkDirty(); }
        }

        var staleSet = new HashSet<Guid>(staleTwins);
        foreach (var g in staleSet)
        { _loci.TryRemove(g); _recentRemoved[g] = DateTime.UtcNow; _supersededMoodleGhosts.Add(g); }

        var toRemove = new List<Guid>();
        foreach (var kv in MirroredIntoLoci)
            if (!allMoodleGuids.Contains(kv.Key)) toRemove.Add(kv.Key);
        foreach (var g in toRemove)
        {
            if (lociGuids.Contains(g))
            {
                var ec = _loci.TryRemove(g);
                if (ec == LociApiEc.ItemLocked) { if (_locked.Add(g)) _log.Warning($"[SkyrimCompass] Loci status locked: {g}"); }
                else _locked.Remove(g);
                continue;
            }
            MirroredIntoLoci.Remove(g); MarkDirty(); _locked.Remove(g); _recentRemoved[g] = DateTime.UtcNow;
        }
    }

    private void SyncLociToMoodles(List<LociStatusInfo> lociList, List<MoodlesStatusInfo> moodlesExisting,
                                   IPlayerCharacter local, bool force)
    {
        var allLociGuids = new HashSet<Guid>(lociList.Count);
        var lociDict = new Dictionary<Guid, LociStatusInfo>(lociList.Count);
        foreach (var l in lociList) { allLociGuids.Add(l.GUID); lociDict[l.GUID] = l; }
        var moodleGuids = new HashSet<Guid>(moodlesExisting.Count);
        foreach (var m in moodlesExisting) moodleGuids.Add(m.GUID);
        _supersededLociGhosts.RemoveWhere(g => !allLociGuids.Contains(g));

        if (force)
        {
            var keys = new List<Guid>(MirroredIntoMoodles.Keys);
            foreach (var g in keys)
            {
                if (!allLociGuids.Contains(g)) continue;
                var src = lociDict[g];
                var sig = MirrorSignature.FromLoci(src);
                if (MirroredIntoMoodles.TryGetValue(g, out var existing) && existing == sig) continue;
                var conv = MirrorConverter.ToMoodles(src);
                if (_moodles.TryApply(conv, local)) { MirroredIntoMoodles[g] = sig; MarkDirty(); }
            }
        }

        var native = new List<LociStatusInfo>();
        foreach (var l in lociList)
            if (!MirroredIntoLoci.ContainsKey(l.GUID) && !IsRecentlyRemoved(l.GUID) && !_supersededLociGhosts.Contains(l.GUID))
                native.Add(l);

        var staleTwins = new List<Guid>();
        foreach (var l in native)
        {
            var sig = MirrorSignature.FromLoci(l);
            bool already = MirroredIntoMoodles.TryGetValue(l.GUID, out var known);
            if (already && known == sig && moodleGuids.Contains(l.GUID)) continue;
            if (!already && moodleGuids.Contains(l.GUID)) { MirroredIntoMoodles[l.GUID] = sig; MarkDirty(); continue; }
            if (!already)
            {
                foreach (var kv in MirroredIntoMoodles)
                    if (kv.Key != l.GUID && kv.Value == sig && allLociGuids.Contains(kv.Key))
                    { staleTwins.Add(kv.Key); break; }
            }
            var conv = MirrorConverter.ToMoodles(l);
            if (_moodles.TryApply(conv, local)) { MirroredIntoMoodles[l.GUID] = sig; MarkDirty(); }
        }

        var staleSet = new HashSet<Guid>(staleTwins);
        foreach (var g in staleSet)
        { _moodles.TryRemove(g, local); _recentRemoved[g] = DateTime.UtcNow; _supersededLociGhosts.Add(g); }

        var toRemove = new List<Guid>();
        foreach (var kv in MirroredIntoMoodles)
            if (!allLociGuids.Contains(kv.Key)) toRemove.Add(kv.Key);
        foreach (var g in toRemove)
        {
            if (moodleGuids.Contains(g)) { _moodles.TryRemove(g, local); continue; }
            MirroredIntoMoodles.Remove(g); MarkDirty(); _recentRemoved[g] = DateTime.UtcNow;
        }
    }

    public void Dispose()
    {
        _fw.Update -= OnUpdate;
        if (_dirty) _state.Save(_pi, _log);
        _moodles.Dispose();
        _loci.Dispose();
    }
}