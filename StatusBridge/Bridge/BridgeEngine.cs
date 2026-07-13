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
/// </summary>
internal sealed class BridgeEngine : IDisposable
{
    private readonly Configuration _config;
    private readonly MoodlesIpc _moodles = new();
    private readonly LociIpc _loci = new();
    private readonly MoodlesOffsetPatch _moodlesOffsetPatch = new();

    // Guid -> signature of the *source* status, as of the last time we pushed it.
    // Keys in _mirroredIntoLoci are GUIDs the bridge wrote into Loci (native side: Moodles).
    // Keys in _mirroredIntoMoodles are GUIDs the bridge wrote into Moodles (native side: Loci).
    private readonly Dictionary<Guid, BridgeSignature> _mirroredIntoLoci = new();
    private readonly Dictionary<Guid, BridgeSignature> _mirroredIntoMoodles = new();

    private DateTime _nextPeriodicReconcile = DateTime.MinValue;
    private volatile bool _dirty = true;

    public bool MoodlesAvailable => _moodles.Available;
    public bool LociAvailable => _loci.Available;
    public int MirroredIntoLociCount => _mirroredIntoLoci.Count;
    public int MirroredIntoMoodlesCount => _mirroredIntoMoodles.Count;
    public bool MoodlesOffsetPatchApplied => _moodlesOffsetPatch.IsApplied;
    public string MoodlesOffsetPatchStatus => _moodlesOffsetPatch.Status;

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
    }

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

        // Order matters: running Moodles->Loci first means anything it just mirrored is
        // immediately recorded in _mirroredIntoLoci, so the Loci->Moodles pass below correctly
        // sees it as "not native to Loci" and doesn't try to mirror it straight back. See the
        // class remarks for why this identity-sharing approach prevents feedback loops.
        if (_config.MirrorMoodlesToLoci)
            SyncMoodlesToLoci(moodleList, lociList);

        if (_config.MirrorLociToMoodles)
            SyncLociToMoodles(lociList, moodleList);
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

    private void SyncMoodlesToLoci(List<MoodlesStatusInfo> moodles, List<LociStatusInfo> lociExisting)
    {
        var nativeMoodles = moodles.Where(m => !_mirroredIntoMoodles.ContainsKey(m.GUID)).ToList();
        var nativeGuids = nativeMoodles.Select(m => m.GUID).ToHashSet();

        foreach (var m in nativeMoodles)
        {
            var sig = BridgeSignature.FromMoodles(m);
            var alreadyTracked = _mirroredIntoLoci.TryGetValue(m.GUID, out var knownSig);

            if (alreadyTracked && knownSig == sig)
                continue; // nothing changed since we last pushed it

            if (!alreadyTracked && lociExisting.Any(l => l.GUID == m.GUID))
            {
                // A Loci entry with this exact GUID already exists (typically: this pair was
                // already mirrored before a plugin reload cleared our in-memory tracking).
                // Adopt it instead of pushing a redundant overwrite.
                _mirroredIntoLoci[m.GUID] = sig;
                continue;
            }

            if (!alreadyTracked && _config.SkipIfMatchingTitleExists &&
                lociExisting.Any(l => !_mirroredIntoLoci.ContainsKey(l.GUID)
                                       && string.Equals(l.Title, m.Title, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // looks like the user already has an equivalent Loci status
            }

            var converted = StatusConverter.ToLoci(m);
            if (_loci.TryApply(converted))
            {
                _mirroredIntoLoci[m.GUID] = sig;
                if (_config.VerboseLogging)
                    Svc.Log.Information($"[StatusBridge] Mirrored Moodle '{m.Title}' -> Loci.");
            }
        }

        // Clean up mirrors whose native Moodles source is gone.
        foreach (var guid in _mirroredIntoLoci.Keys.Where(g => !nativeGuids.Contains(g)).ToList())
        {
            if (lociExisting.Any(l => l.GUID == guid))
                _loci.TryRemove(guid);

            _mirroredIntoLoci.Remove(guid);
            if (_config.VerboseLogging)
                Svc.Log.Information("[StatusBridge] Removed a Loci mirror (source Moodle expired or was removed).");
        }
    }

    private void SyncLociToMoodles(List<LociStatusInfo> loci, List<MoodlesStatusInfo> moodlesExisting)
    {
        var nativeLoci = loci.Where(l => !_mirroredIntoLoci.ContainsKey(l.GUID)).ToList();
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
                if (_config.VerboseLogging)
                    Svc.Log.Information($"[StatusBridge] Mirrored Loci status '{l.Title}' -> Moodles.");
            }
        }

        foreach (var guid in _mirroredIntoMoodles.Keys.Where(g => !nativeGuids.Contains(g)).ToList())
        {
            if (moodlesExisting.Any(m => m.GUID == guid))
                _moodles.TryRemove(guid);

            _mirroredIntoMoodles.Remove(guid);
            if (_config.VerboseLogging)
                Svc.Log.Information("[StatusBridge] Removed a Moodles mirror (source Loci status expired or was removed).");
        }
    }

    /// <summary>Removes every status the bridge has ever created, on both sides. Manual escape hatch.</summary>
    public void ClearAllMirrors()
    {
        foreach (var guid in _mirroredIntoLoci.Keys.ToList())
            _loci.TryRemove(guid);
        _mirroredIntoLoci.Clear();

        foreach (var guid in _mirroredIntoMoodles.Keys.ToList())
            _moodles.TryRemove(guid);
        _mirroredIntoMoodles.Clear();

        Svc.Log.Information("[StatusBridge] Cleared all mirrored statuses.");
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;

        if (_moodlesOffsetPatch.IsApplied)
            _moodlesOffsetPatch.TryRevert();

        _moodles.Dispose();
        _loci.Dispose();
    }
}
