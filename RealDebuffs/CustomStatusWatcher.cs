using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

// Moodles and Loci both hand a status back over IPC as one long tuple whose first four fields are
// (Version, GUID, IconID, Title). Dalamud converts an IPC result to the type the caller declares by
// round-tripping it through JSON, and JSON -> tuple simply ignores any field the target tuple
// doesn't have - so declaring just the leading fields we read is enough, and it keeps working if
// either plugin appends more fields later. (SkyrimCompass has to declare the full tuples because it
// also sends statuses back.) IconID is a long so it accepts Moodles' int and Loci's uint alike.
using StatusHead = (int Version, System.Guid GUID, long IconID, string Title);

namespace RealDebuffs;

/// <summary>Which of the two plugins is currently reporting a status.</summary>
[Flags]
public enum StatusSource
{
    None = 0,
    Moodles = 1,
    Loci = 2,
}

/// <summary>One distinct custom status currently on the player. <see cref="Key"/> is the comparison form of <see cref="Name"/>.</summary>
public sealed record ActiveCustomStatus(string Key, string Name, StatusSource Sources);

/// <summary>
/// An immutable picture of "which custom statuses does the player have right now", merged across
/// Moodles and Loci. The watcher swaps in a fresh one whenever it re-reads, so the draw code and
/// the settings window can hold a reference for a whole frame without any locking.
///
/// Statuses are merged BY NAME (see <see cref="StatusNames"/>). That's what makes a mirrored status
/// count once: SkyrimCompass copies a Moodle into Loci (and vice versa) under the same title, so
/// both plugins report it - and it collapses into a single entry here, tagged with both sources.
/// The same goes for two separate statuses that happen to share a title.
/// </summary>
public sealed class CustomStatusSnapshot
{
    public static readonly CustomStatusSnapshot Empty = new(Array.Empty<ActiveCustomStatus>());

    private readonly HashSet<string> _keys;

    /// <summary>Every distinct active status, sorted by name.</summary>
    public IReadOnlyList<ActiveCustomStatus> Statuses { get; }

    private CustomStatusSnapshot(ActiveCustomStatus[] statuses)
    {
        Statuses = statuses;
        _keys = new HashSet<string>(statuses.Select(s => s.Key), StringComparer.Ordinal);
    }

    /// <summary>True if a status with this comparison key (see <see cref="StatusNames.Key"/>) is active.</summary>
    public bool Contains(string key) => key.Length > 0 && _keys.Contains(key);

    /// <summary>Merges the titles reported by each plugin into one deduped snapshot.</summary>
    internal static CustomStatusSnapshot Build(IEnumerable<string?> moodlesTitles, IEnumerable<string?> lociTitles)
    {
        var merged = new Dictionary<string, (string Name, StatusSource Sources)>(StringComparer.Ordinal);

        void Add(string? title, StatusSource source)
        {
            var name = StatusNames.Clean(title);
            if (name.Length == 0) return;

            var key = StatusNames.Key(name);
            merged[key] = merged.TryGetValue(key, out var seen)
                ? (seen.Name, seen.Sources | source)
                : (name, source);
        }

        foreach (var title in moodlesTitles) Add(title, StatusSource.Moodles);
        foreach (var title in lociTitles) Add(title, StatusSource.Loci);

        if (merged.Count == 0) return Empty;

        return new CustomStatusSnapshot(merged
            .Select(kv => new ActiveCustomStatus(kv.Key, kv.Value.Name, kv.Value.Sources))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Key, StringComparer.Ordinal)
            .ToArray());
    }

    /// <summary>
    /// Adds the effect kind of every enabled rule whose status is currently active to
    /// <paramref name="into"/>. It's a SET on purpose: if the same effect is already active - from a
    /// real debuff, another rule, or the same status arriving twice - adding it again is a no-op, so
    /// nothing ever stacks, doubles up, or restarts. The effect simply stays on until the LAST thing
    /// asking for it goes away.
    /// </summary>
    public void AddActiveKinds(IReadOnlyList<CustomStatusRule> rules, ISet<DebuffKind> into)
    {
        if (Statuses.Count == 0) return;

        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            if (rule.Enabled && Contains(rule.GetKey()))
                into.Add(rule.Kind);
        }
    }
}

/// <summary>
/// Keeps <see cref="CustomStatusSnapshot"/> up to date by reading the local player's Moodles and
/// Loci statuses over IPC, at most once per second.
///
/// Presence in the plugin's list is the ONLY "is it active" signal available: the duration field in
/// both plugins' IPC tuples is the status's configured length (or -1 for none), not a time
/// remaining. That works out fine, because both plugins drop a status from their list in their own
/// per-frame tick the moment it expires, is clicked off, or is removed.
///
/// This reads on a plain timer instead of reacting to Moodles' / Loci's change events. At one read a
/// second the events wouldn't make anything faster, and a timer gives a hard worst case even for the
/// odd change path that doesn't raise an event. The trade-off: an effect can trail a status change
/// by up to about a second, on top of its normal fade in/out - fine for a screen effect. Either
/// plugin being absent is normal: that side just reports nothing, and is re-probed every couple of
/// seconds so installing or enabling it mid-session is picked up without a reload.
/// </summary>
public sealed class CustomStatusWatcher : IDisposable
{
    private const int ReadIntervalMs = 1000; // the one knob: how often Moodles and Loci get read
    private const int ProbeMs = 2000;        // how often to look for a plugin that isn't there / isn't working
    private const int MoodlesMinVersion = 4; // first Moodles IPC version with the V2 status-info calls

    /// <summary>
    /// Everything we track per plugin. Moodles and Loci run through the exact same small state
    /// machine (probe until found, read while found, back off if reading breaks), so it's written
    /// once in <see cref="ReadSource"/> rather than twice.
    /// </summary>
    private sealed class Source
    {
        public required string Name { get; init; }
        /// <summary>True if the plugin is present and usable. Throws (IpcNotReadyError) if it isn't loaded.</summary>
        public required Func<bool> Probe { get; init; }
        public required Func<List<StatusHead>> Read { get; init; }

        public bool Available;
        public bool Failing;   // a read has failed and hasn't succeeded since; keeps the log to one line per streak
        public long NextProbeAt;
    }

    private readonly IFramework _framework;
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;
    private readonly Func<long> _nowMs;
    private readonly Source _moodles;
    private readonly Source _loci;

    private volatile CustomStatusSnapshot _snapshot = CustomStatusSnapshot.Empty;
    private bool _updateErrorLogged;
    private long _nextReadAt;

    /// <summary>The current picture (at most about a second old). Safe to hold for a whole frame; never null.</summary>
    public CustomStatusSnapshot Snapshot => _snapshot;

    /// <summary>Whether a usable Moodles was found and is being read.</summary>
    public bool MoodlesAvailable => _moodles.Available;

    /// <summary>Whether a usable, enabled Loci was found and is being read.</summary>
    public bool LociAvailable => _loci.Available;

    public CustomStatusWatcher(IDalamudPluginInterface pi, IFramework framework, IObjectTable objectTable, IPluginLog log)
        : this(pi, framework, objectTable, log, static () => Environment.TickCount64)
    {
    }

    /// <summary>Same as the public constructor, with the clock swappable so the timing can be tested.</summary>
    internal CustomStatusWatcher(
        IDalamudPluginInterface pi, IFramework framework, IObjectTable objectTable, IPluginLog log, Func<long> nowMs)
    {
        _framework = framework;
        _objectTable = objectTable;
        _log = log;
        _nowMs = nowMs;

        // Same IPC endpoints SkyrimCompass already uses for its mirroring. Plain calls only - no event
        // subscriptions - so there's nothing to register with Moodles or Loci and nothing to clean up.
        var moodlesVersion = pi.GetIpcSubscriber<int>("Moodles.Version");
        var moodlesGet = pi.GetIpcSubscriber<List<StatusHead>>("Moodles.GetClientStatusManagerInfoV2");

        var lociApiVersion = pi.GetIpcSubscriber<(int, int)>("Loci.ApiVersion");
        var lociEnabled = pi.GetIpcSubscriber<bool>("Loci.IsEnabled");
        var lociGet = pi.GetIpcSubscriber<List<StatusHead>>("Loci.GetManagerInfo");

        _moodles = new Source
        {
            Name = "Moodles",
            Probe = () => moodlesVersion.InvokeFunc() >= MoodlesMinVersion,
            Read = () => moodlesGet.InvokeFunc(),
        };

        _loci = new Source
        {
            Name = "Loci",
            Probe = () =>
            {
                lociApiVersion.InvokeFunc(); // throws IpcNotReadyError if Loci isn't loaded
                return lociEnabled.InvokeFunc();
            },
            Read = () => lociGet.InvokeFunc(),
        };

        _framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        long now = _nowMs();
        if (now < _nextReadAt) return; // this runs every frame; only actually read once per interval
        _nextReadAt = now + ReadIntervalMs;

        try
        {
            Refresh(now);
            _updateErrorLogged = false;
        }
        catch (Exception ex)
        {
            if (_updateErrorLogged) return; // don't spam the log every read if something's persistently wrong
            _updateErrorLogged = true;
            _log.Error(ex, "RealDebuffs: updating custom (Moodles/Loci) statuses failed. Custom status effects won't update until this recovers.");
        }
    }

    private void Refresh(long now)
    {
        // No local player (login screen, zoning): there's nothing to show, and Moodles logs a
        // warning on every call for the local player's statuses while there isn't one.
        if (_objectTable.LocalPlayer == null)
        {
            _snapshot = CustomStatusSnapshot.Empty;
            return;
        }

        _snapshot = CustomStatusSnapshot.Build(ReadSource(_moodles, now), ReadSource(_loci, now));
    }

    /// <summary>
    /// Reads one plugin's current status titles. Never throws: a missing or misbehaving plugin just
    /// contributes nothing. Probes for a missing plugin at a slow rate, and if reads start failing
    /// (plugin unloaded, or its IPC shape changed) logs ONE warning for the streak, then retries
    /// quietly - on the very next read in case it was a blip, then at the slow probe rate.
    /// </summary>
    private IReadOnlyList<string> ReadSource(Source s, long now)
    {
        if (!s.Available)
        {
            if (now < s.NextProbeAt) return Array.Empty<string>();
            s.NextProbeAt = now + ProbeMs;

            bool found;
            try { found = s.Probe(); }
            catch { found = false; } // IpcNotReadyError: not loaded (yet)

            if (!found) return Array.Empty<string>();

            s.Available = true;
            if (!s.Failing)
                _log.Information($"RealDebuffs: {s.Name} detected - its custom statuses can now trigger effects.");
        }

        try
        {
            var titles = Titles(s.Read());
            if (s.Failing)
            {
                s.Failing = false;
                _log.Information($"RealDebuffs: reading {s.Name} statuses works again.");
            }
            return titles;
        }
        catch (Exception ex)
        {
            s.Available = false;
            s.NextProbeAt = s.Failing ? now + ProbeMs : now;
            if (!s.Failing)
            {
                s.Failing = true;
                _log.Warning(ex, $"RealDebuffs: couldn't read {s.Name} statuses (it may have been unloaded, or its IPC changed). Will keep retrying quietly.");
            }
            return Array.Empty<string>();
        }
    }

    private static List<string> Titles(List<StatusHead>? list)
    {
        var titles = new List<string>(list?.Count ?? 0);
        if (list == null) return titles;

        foreach (var status in list)
            titles.Add(status.Title);
        return titles;
    }

    public void Dispose() => _framework.Update -= OnUpdate;
}
