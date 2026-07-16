using Dalamud.Plugin.Services;
using StatusBridge.Experimental;
using StatusBridge.Interop;

namespace StatusBridge.Bridge;

/// <summary>
/// Keeps the local player's Moodles and Loci statuses mirrored onto each other. Deliberately
/// does not know or care which sync plugin (Snowcloak, Sundouleia, anything else) is installed -
/// it only ever touches your own local Moodles/Loci state. Whichever sync tool you have reads
/// from whichever backend it natively supports, same as it always did; this just makes sure both
/// backends agree with each other.
///
/// Identity trick: a mirrored status keeps the exact same GUID in both systems. That single
/// choice gives us three things for free:
///   - A cheap way to tell "is this status one I created as a mirror" vs "the user made this
///     natively" (look it up in the relevant tracking dictionary below).
///   - Feedback-loop prevention: mirroring a status out marks its GUID as tracked, so the change
///     event that mirror's own creation fires is recognized as "one of ours" and not re-mirrored
///     back again.
///   - Natural de-duplication: re-mirroring an already-mirrored pair becomes an update-in-place
///     rather than a second, disconnected copy.
///
/// The tracking dictionaries themselves are persisted (see <see cref="BridgeState"/>) - they
/// used to be in-memory only, which was the actual root cause behind essentially every "ghost
/// status" bug found while building this (see README "Known issues"): every game restart started
/// from a blank slate and had to guess identity from scratch. The GUID-matching "adopt" fallback
/// below still exists for the narrower case of a missing/corrupted state file, but persistence
/// is now the primary mechanism, not the guess.
/// </summary>
internal sealed class BridgeEngine : IDisposable
{
    private readonly Configuration _config;
    private readonly MoodlesIpc _moodles = new();
    private readonly LociIpc _loci = new();
    private readonly MoodlesOffsetPatch _moodlesOffsetPatch = new();
    private readonly MoodlesLiveStateReader _moodlesLiveState = new();
    private readonly LociLiveStateReader _lociLiveState = new();

    private readonly BridgeState _state = BridgeState.Load();
    private bool _stateDirty;

    // Guid -> signature of the *source* status, as of the last time we pushed it.
    // Keys in _mirroredIntoLoci are GUIDs the bridge wrote into Loci (native side: Moodles).
    // Keys in _mirroredIntoMoodles are GUIDs the bridge wrote into Moodles (native side: Loci).
    // Both delegate to _state so every mutation site below is automatically persisted-backed;
    // MarkStateDirty() still needs calling explicitly at each mutation (see call sites) since a
    // Dictionary indexer/Remove call has no hook to observe from out here.
    private Dictionary<Guid, BridgeSignature> _mirroredIntoLoci => _state.MirroredIntoLoci;
    private Dictionary<Guid, BridgeSignature> _mirroredIntoMoodles => _state.MirroredIntoMoodles;

    // GUIDs we've already logged a warning about being stuck locked on Loci's side, so we don't
    // spam the log every tick for something that will keep failing the same way until someone
    // manually unlocks it. Cleared whenever a GUID stops being locked (removed successfully, or
    // its native source reappears) so a genuinely new lock situation still gets reported.
    private readonly HashSet<Guid> _knownLockedInLoci = new();

    private DateTime _nextPeriodicReconcile = DateTime.MinValue;
    private volatile bool _dirty = true;

    public bool MoodlesAvailable => _moodles.Available;
    public bool LociAvailable => _loci.Available;
    public int MirroredIntoLociCount => _mirroredIntoLoci.Count;
    public int MirroredIntoMoodlesCount => _mirroredIntoMoodles.Count;
    public bool MoodlesOffsetPatchApplied => _moodlesOffsetPatch.IsApplied;
    public string MoodlesOffsetPatchStatus => _moodlesOffsetPatch.Status;

    /// <summary>Mirrored statuses currently stuck because Loci reports them as locked (see remarks on ItemLocked handling below).</summary>
    public int LockedMirrorCount => _knownLockedInLoci.Count;

    /// <summary>Best-effort diagnostic, not used for any control-flow decision. Null = couldn't read it right now.</summary>
    public bool? MoodlesEphemeral { get; private set; }

    /// <summary>Official (non-reflection) diagnostic: other plugins Loci reports as currently driving your own status manager.</summary>
    public List<string> LociHostsForLocalPlayer => _loci.GetHostsForLocalPlayer();

    public BridgeEngine(Configuration config)
    {
        _config = config;
        _moodles.LocalStatusesChanged += () => _dirty = true;
        _loci.LocalStatusesChanged += () => _dirty = true;
        Svc.Framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (!_dirty && now < _nextPeriodicReconcile)
            return;

        _dirty = false;
        _nextPeriodicReconcile = now + TimeSpan.FromSeconds(1);

        try
        {
            Reconcile();
        }
        catch (Exception e)
        {
            Svc.Log.Error(e, "[StatusBridge] Reconciliation pass threw unexpectedly.");
        }

        if (_stateDirty)
        {
            _state.Save();
            _stateDirty = false;
        }
    }

    private void MarkStateDirty() => _stateDirty = true;

    private void Reconcile()
    {
        // Runs before the Enabled check on purpose: availability should stay fresh for the UI
        // even while mirroring is paused, and the offset patch is an independent concern from
        // mirroring - someone might want the icon fix active without bidirectional sync running.
        _moodles.RefreshAvailability();
        _loci.RefreshAvailability();

        UpdateExperimentalMoodlesOffsetPatch();

        if (!_config.Enabled)
            return;

        if (Svc.ObjectTable.LocalPlayer == null)
            return;

        if (!_moodles.Available || !_loci.Available)
            return;

        var moodleList = _moodles.GetLocalStatuses();
        var lociList = _loci.GetLocalStatuses();

        // Best-effort, read-only cross-check against Moodles' real internal state - see
        // MoodlesLiveStateReader's remarks for exactly what this closes a gap on. Null (reader
        // failed for any reason) means "couldn't tell", and every consumer below already treats
        // null as "don't filter anything", i.e. exactly today's behavior with this class absent.
        var moodlesAliveGuids = _moodlesLiveState.TryGetGenuinelyAliveGuids(moodleList.Select(m => m.GUID));
        MoodlesEphemeral = _moodlesLiveState.TryGetEphemeral(moodleList.Select(m => m.GUID));
        var lociAliveGuids = _lociLiveState.TryGetGenuinelyAliveGuids();

        // Order matters: running Moodles->Loci first means anything it just mirrored is
        // immediately recorded in _mirroredIntoLoci, so the Loci->Moodles pass below correctly
        // sees it as "not native to Loci" and doesn't try to mirror it straight back. See the
        // class remarks for why this identity-sharing approach prevents feedback loops.
        if (_config.MirrorMoodlesToLoci)
            SyncMoodlesToLoci(moodleList, lociList, moodlesAliveGuids);

        if (_config.MirrorLociToMoodles)
            SyncLociToMoodles(lociList, moodleList, lociAliveGuids);
    }

    private void UpdateExperimentalMoodlesOffsetPatch()
    {
        if (_config.EnableExperimentalMoodlesOffsetFix)
        {
            // Idempotent-safe and cheap (a handful of reflection field lookups on a bool), so
            // just retrying every tick until it succeeds is simpler and more self-healing than
            // event-driven re-application - it recovers on its own after a Moodles reload.
            if (_moodles.Available && !_moodlesOffsetPatch.IsApplied)
                _moodlesOffsetPatch.TryApply();
        }
        else if (_moodlesOffsetPatch.IsApplied)
        {
            _moodlesOffsetPatch.TryRevert();
        }
    }

    private void SyncMoodlesToLoci(List<MoodlesStatusInfo> moodles, List<LociStatusInfo> lociExisting, HashSet<Guid>? aliveFilter)
    {
        // aliveFilter == null means the live-state read failed this tick (see MoodlesLiveStateReader) -
        // fall back to exactly the old behavior (every native-looking GUID is a candidate). When it
        // succeeds, excluding Cancel()-marked-but-not-yet-pruned entries here means we react to the
        // user's actual clearing intent as soon as it happens rather than waiting however many ticks
        // Moodles' own Tick() takes to catch up - fewer wasted create/remove cycles on both sides.
        var nativeMoodles = moodles
            .Where(m => !_mirroredIntoMoodles.ContainsKey(m.GUID))
            .Where(m => aliveFilter == null || aliveFilter.Contains(m.GUID))
            .ToList();
        var nativeGuids = nativeMoodles.Select(m => m.GUID).ToHashSet();

        foreach (var m in nativeMoodles)
        {
            var sig = BridgeSignature.FromMoodles(m);
            var alreadyTracked = _mirroredIntoLoci.TryGetValue(m.GUID, out var knownSig);

            if (alreadyTracked && knownSig == sig)
                continue; // nothing changed since we last pushed it

            if (!alreadyTracked && lociExisting.Any(l => l.GUID == m.GUID))
            {
                // A Loci entry with this exact GUID already exists. With persisted state this
                // should mostly only come up on a missing/corrupted state file - kept as a
                // fallback rather than the primary mechanism it used to be.
                _mirroredIntoLoci[m.GUID] = sig;
                MarkStateDirty();
                continue;
            }

            if (!alreadyTracked && _config.SkipIfMatchingTitleExists &&
                lociExisting.Any(l => !_mirroredIntoLoci.ContainsKey(l.GUID)
                                       && string.Equals(l.Title, m.Title, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // looks like the user already has an equivalent Loci status
            }

            var converted = StatusConverter.ToLoci(m);
            var ec = _loci.TryApply(converted);
            if (ec is LociApiEc.Success or LociApiEc.NoChange)
            {
                _mirroredIntoLoci[m.GUID] = sig;
                MarkStateDirty();
                if (_config.VerboseLogging)
                    Svc.Log.Information($"[StatusBridge] Mirrored Moodle '{m.Title}' -> Loci.");
            }
        }

        // Clean up mirrors whose native Moodles source is gone.
        foreach (var guid in _mirroredIntoLoci.Keys.Where(g => !nativeGuids.Contains(g)).ToList())
        {
            if (lociExisting.Any(l => l.GUID == guid))
            {
                // Still there as of this tick's fresh poll - ask Loci to remove it, but don't
                // stop tracking yet. A same-tick "it didn't throw" isn't proof it actually took
                // effect. Leaving it tracked means we simply try again next tick and only
                // actually untrack it once a fresh poll confirms it's gone - self-healing the
                // same way the offset patch already retries every tick instead of trusting one
                // attempt. Letting a removal we can't verify fall out of tracking is exactly how
                // a real, still-alive mirror turns into a permanent, untracked ghost.
                var ec = _loci.TryRemove(guid);

                // ItemLocked specifically will never resolve on its own with key=0 (see
                // LociIpc.TryRemove remarks) - retrying is harmless but pointless, so this is
                // worth surfacing distinctly rather than silently spinning on it forever.
                if (ec == LociApiEc.ItemLocked)
                {
                    if (_knownLockedInLoci.Add(guid))
                        Svc.Log.Warning($"[StatusBridge] A mirrored Loci status ({guid}) is locked and can't be removed automatically - unlock it in Loci's own UI if you want it gone.");
                }
                else
                {
                    _knownLockedInLoci.Remove(guid);
                }

                continue;
            }

            _mirroredIntoLoci.Remove(guid);
            MarkStateDirty();
            _knownLockedInLoci.Remove(guid);
            if (_config.VerboseLogging)
                Svc.Log.Information("[StatusBridge] Removed a Loci mirror (source Moodle expired or was removed).");
        }
    }

    private void SyncLociToMoodles(List<LociStatusInfo> loci, List<MoodlesStatusInfo> moodlesExisting, HashSet<Guid>? aliveFilter)
    {
        var nativeLoci = loci
            .Where(l => !_mirroredIntoLoci.ContainsKey(l.GUID))
            .Where(l => aliveFilter == null || aliveFilter.Contains(l.GUID))
            .ToList();
        var nativeGuids = nativeLoci.Select(l => l.GUID).ToHashSet();

        foreach (var l in nativeLoci)
        {
            var sig = BridgeSignature.FromLoci(l);
            var alreadyTracked = _mirroredIntoMoodles.TryGetValue(l.GUID, out var knownSig);

            if (alreadyTracked && knownSig == sig)
                continue;

            if (!alreadyTracked && moodlesExisting.Any(m => m.GUID == l.GUID))
            {
                _mirroredIntoMoodles[l.GUID] = sig;
                MarkStateDirty();
                continue;
            }

            if (!alreadyTracked && _config.SkipIfMatchingTitleExists &&
                moodlesExisting.Any(m => !_mirroredIntoMoodles.ContainsKey(m.GUID)
                                          && string.Equals(m.Title, l.Title, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var converted = StatusConverter.ToMoodles(l);
            if (_moodles.TryApply(converted))
            {
                _mirroredIntoMoodles[l.GUID] = sig;
                MarkStateDirty();
                if (_config.VerboseLogging)
                    Svc.Log.Information($"[StatusBridge] Mirrored Loci status '{l.Title}' -> Moodles.");
            }
        }

        foreach (var guid in _mirroredIntoMoodles.Keys.Where(g => !nativeGuids.Contains(g)).ToList())
        {
            if (moodlesExisting.Any(m => m.GUID == guid))
            {
                // See the matching comment in SyncMoodlesToLoci's cleanup loop: don't untrack on
                // a same-tick assumption. This side is the one actually affected by Moodles'
                // fire-and-forget remove IPC and its Ephemeral gate, so it's the more important
                // half of this fix - only stop tracking once a later tick's fresh poll shows
                // Moodles genuinely no longer has it.
                _moodles.TryRemove(guid);
                continue;
            }

            _mirroredIntoMoodles.Remove(guid);
            MarkStateDirty();
            if (_config.VerboseLogging)
                Svc.Log.Information("[StatusBridge] Removed a Moodles mirror (source Loci status expired or was removed).");
        }
    }

    /// <summary>Removes every status the bridge has ever created, on both sides. Manual escape hatch.</summary>
    public void ClearAllMirrors()
    {
        // Issue every removal first, then re-poll and only forget the ones a fresh read confirms
        // are actually gone. Untracking something that's still really there doesn't just leave a
        // stray status behind: it makes the very next reconcile pass see a "new" entry nothing is
        // tracking, decide it must be native, and mirror it right back - i.e. this method could
        // otherwise turn a stuck ghost into a freshly (re-)created one instead of removing it.
        // Anything still present after this re-check stays tracked, so the normal per-tick
        // cleanup above keeps retrying it instead of losing it.
        //
        // Important limitation this does NOT solve, by design: this only ever removes the mirror
        // half of a pair, never the native half (see the tooltip in ConfigWindow.cs - "does not
        // touch statuses you created yourself" is a promise, not an oversight). If the native
        // source is still genuinely alive, the very next reconcile pass sees a native status with
        // no mirror and correctly recreates one - that's not a bug, it's the bridge doing exactly
        // what it's supposed to. Clicking this repeatedly against a still-alive native source will
        // look like a toggle (gone, back, gone, back) because that's genuinely what's happening.
        // If that's what you're hitting, ForceClearBridgeLinkedStatuses below is the tool for it.
        foreach (var guid in _mirroredIntoLoci.Keys.ToList())
            _loci.TryRemove(guid);

        var lociStillPresent = _loci.GetLocalStatuses().Select(l => l.GUID).ToHashSet();
        foreach (var guid in _mirroredIntoLoci.Keys.Where(g => !lociStillPresent.Contains(g)).ToList())
        {
            _mirroredIntoLoci.Remove(guid);
            MarkStateDirty();
            _knownLockedInLoci.Remove(guid);
        }

        foreach (var guid in _mirroredIntoMoodles.Keys.ToList())
            _moodles.TryRemove(guid);

        var moodlesStillPresent = _moodles.GetLocalStatuses().Select(m => m.GUID).ToHashSet();
        foreach (var guid in _mirroredIntoMoodles.Keys.Where(g => !moodlesStillPresent.Contains(g)).ToList())
        {
            _mirroredIntoMoodles.Remove(guid);
            MarkStateDirty();
        }

        Svc.Log.Information("[StatusBridge] Cleared all mirrored statuses.");
    }

    /// <summary>
    /// Removes every status the bridge is tracking, AND its native counterpart - both halves of
    /// every tracked pair, on both sides. Unlike <see cref="ClearAllMirrors"/>, this does touch
    /// statuses you created yourself, if they're currently part of a tracked pair. Exists
    /// specifically for a pair that's stuck: something left a native status alive when it should
    /// already be gone, so a mirror-only clear just gets rebuilt by the next reconcile pass.
    /// Removing both halves is what actually breaks that cycle. This is strictly more
    /// destructive than ClearAllMirrors, on purpose - see the ConfigWindow tooltip before wiring
    /// this to a button without a clear warning attached. Note this can still fail against a
    /// genuinely locked Loci status (key=0 can't override someone else's lock) - it'll log the
    /// same ItemLocked warning as the normal cleanup path in that case.
    /// </summary>
    public void ForceClearBridgeLinkedStatuses()
    {
        var trackedGuids = _mirroredIntoLoci.Keys.Concat(_mirroredIntoMoodles.Keys).ToHashSet();

        foreach (var guid in trackedGuids)
        {
            var lociEc = _loci.TryRemove(guid);
            if (lociEc == LociApiEc.ItemLocked)
                Svc.Log.Warning($"[StatusBridge] Force-clear couldn't remove a locked Loci status ({guid}) - unlock it in Loci's own UI.");

            _moodles.TryRemove(guid);
        }

        var lociStillPresent = _loci.GetLocalStatuses().Select(l => l.GUID).ToHashSet();
        foreach (var guid in _mirroredIntoLoci.Keys.Where(g => !lociStillPresent.Contains(g)).ToList())
        {
            _mirroredIntoLoci.Remove(guid);
            MarkStateDirty();
            _knownLockedInLoci.Remove(guid);
        }

        var moodlesStillPresent = _moodles.GetLocalStatuses().Select(m => m.GUID).ToHashSet();
        foreach (var guid in _mirroredIntoMoodles.Keys.Where(g => !moodlesStillPresent.Contains(g)).ToList())
        {
            _mirroredIntoMoodles.Remove(guid);
            MarkStateDirty();
        }

        Svc.Log.Information("[StatusBridge] Force-cleared all bridge-linked statuses (native + mirror, both sides).");
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;

        if (_moodlesOffsetPatch.IsApplied)
            _moodlesOffsetPatch.TryRevert();

        if (_stateDirty)
            _state.Save();

        _moodles.Dispose();
        _loci.Dispose();
    }
}
