using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

// Moodles and Loci both hand a status back over IPC as one long tuple whose first five fields are
// (Version, GUID, IconID, Title, Description). Dalamud converts an IPC result to the type the caller
// declares by round-tripping it through JSON, and JSON -> tuple simply ignores any field the target
// tuple doesn't have - so declaring just the leading fields we read is enough, and it keeps working
// if either plugin appends more fields later. (SkyrimCompass has to declare the full tuples because
// it also sends statuses back - see its StatusMirror.cs, which confirms Description really is field
// 5 on both sides.) IconID is a long so it accepts Moodles' int and Loci's uint alike.
//
// Description is new here (RealDebuffs used to only read Title): it's a status's full tooltip body,
// and reading it is what makes the tooltip-keyword feature (TooltipKeywordParser) possible. It's
// kept RAW (unlike Title, which immediately goes through StatusNames.Clean below) because the
// parser needs the [color=] tags still in place to know what color a tooltip is asking for - it
// does its own stripping once it's done reading them.
using StatusHead = (int Version, System.Guid GUID, long IconID, string Title, string Description);

namespace RealDebuffs;

/// <summary>Which of the two plugins is currently reporting a status.</summary>
[Flags]
public enum StatusSource
{
    None = 0,
    Moodles = 1,
    Loci = 2,
}

/// <summary>
/// One distinct custom status currently on the player. <see cref="Key"/> is the comparison form of
/// <see cref="Name"/>. <see cref="Description"/> is the raw tooltip body (see the remarks on
/// <c>StatusHead</c> above for why it's raw) - empty if the status has none, which is common for a
/// quickly-made Moodle/Loci that's just a title and an icon. <see cref="TooltipMatches"/> is what
/// <see cref="TooltipKeywordParser"/> found in that description the last time this snapshot was
/// built (empty if the status has no description, no rule matched, or tooltip parsing is off) - see
/// <see cref="CustomStatusSnapshot"/>'s remarks for why it's precomputed here rather than on demand.
/// </summary>
public sealed record ActiveCustomStatus(
    string Key, string Name, string Description, StatusSource Sources, IReadOnlyList<TooltipEffectMatch> TooltipMatches);

/// <summary>
/// An immutable picture of "which custom statuses does the player have right now", merged across
/// Moodles and Loci. The watcher swaps in a fresh one whenever it re-reads, so the draw code and
/// the settings window can hold a reference for a whole frame without any locking.
///
/// Statuses are merged BY NAME (see <see cref="StatusNames"/>). That's what makes a mirrored status
/// count once: SkyrimCompass copies a Moodle into Loci (and vice versa) under the same title, so
/// both plugins report it - and it collapses into a single entry here, tagged with both sources.
/// The same goes for two separate statuses that happen to share a title. If the two sides disagree
/// on the description text for what merges into one entry (only possible for a status that predates
/// SkyrimCompass's mirroring, or was independently created twice), whichever non-empty description
/// was seen FIRST wins - simple, and a mismatch here is already an edge case neither this plugin nor
/// SkyrimCompass can fully resolve on your behalf.
///
/// TOOLTIP KEYWORDS ARE RESOLVED HERE, ONCE, AT BUILD TIME - not on demand in EffectManager.Draw.
/// <see cref="TooltipKeywordRule"/> matching (unlike the plain name-based <see cref="CustomStatusRule"/>)
/// involves real text processing - stripping markup, splitting clauses, scanning for color words -
/// and the text it runs on is already only as fresh as the last IPC read (see
/// <see cref="CustomStatusWatcher"/>'s remarks on its read cadence), so redoing that work 60 times a
/// second in the draw loop would just repeat the same answer 59 extra times. Instead the aggregate
/// result (<see cref="TooltipKinds"/>/<see cref="TooltipStrengths"/>/<see cref="TooltipColors"/>) is
/// computed once per <see cref="Build"/> call, and each status's own
/// <see cref="ActiveCustomStatus.TooltipMatches"/> is cached alongside it too, so
/// EffectManager.Draw's per-frame job is just cheap dictionary/set merging over an answer that's
/// already sitting there, and the "/realdebuffs statuses" diagnostic can show exactly what's
/// actually driving the screen right now rather than a fresh (and potentially momentarily
/// different) re-parse. The trade-off: editing a keyword, its color, or the on/off checkbox in the
/// settings window takes effect on the next read (up to about a second), same as a real change to
/// the Moodle/Loci status itself would - name-based rules are still matched fresh every frame
/// (see <see cref="AddActiveKinds"/>), since a plain "is this key present" check is cheap enough
/// that there's no reason to make IT wait too.
/// </summary>
public sealed class CustomStatusSnapshot
{
    public static readonly CustomStatusSnapshot Empty = new(
        Array.Empty<ActiveCustomStatus>(), new HashSet<DebuffKind>(), new Dictionary<DebuffKind, float>(), new Dictionary<DebuffKind, Vector4>());

    private readonly HashSet<string> _keys;

    /// <summary>Every distinct active status, sorted by name.</summary>
    public IReadOnlyList<ActiveCustomStatus> Statuses { get; }

    /// <summary>Every effect kind at least one active status's tooltip currently asks for - see the class remarks. Empty whenever tooltip parsing is off.</summary>
    public IReadOnlyCollection<DebuffKind> TooltipKinds { get; }

    /// <summary>Per-kind strength from whichever tooltip match asked loudest - see <see cref="TooltipKeywordRule.Strength"/>.</summary>
    public IReadOnlyDictionary<DebuffKind, float> TooltipStrengths { get; }

    /// <summary>Per-kind color from whichever tooltip match resolved one first - see <see cref="TooltipKeywordParser"/>'s remarks on how ties resolve.</summary>
    public IReadOnlyDictionary<DebuffKind, Vector4> TooltipColors { get; }

    private CustomStatusSnapshot(
        ActiveCustomStatus[] statuses,
        HashSet<DebuffKind> tooltipKinds,
        Dictionary<DebuffKind, float> tooltipStrengths,
        Dictionary<DebuffKind, Vector4> tooltipColors)
    {
        Statuses = statuses;
        _keys = new HashSet<string>(statuses.Select(s => s.Key), StringComparer.Ordinal);
        TooltipKinds = tooltipKinds;
        TooltipStrengths = tooltipStrengths;
        TooltipColors = tooltipColors;
    }

    /// <summary>True if a status with this comparison key (see <see cref="StatusNames.Key"/>) is active.</summary>
    public bool Contains(string key) => key.Length > 0 && _keys.Contains(key);

    /// <summary>
    /// Merges the titles+descriptions reported by each plugin into one deduped snapshot, and - see
    /// the class remarks - resolves every status's tooltip against <paramref name="tooltipRules"/>
    /// right here, once. Pass an empty rule list (rather than skipping the call) when tooltip
    /// parsing is switched off; every status just ends up with empty <see cref="ActiveCustomStatus.TooltipMatches"/>
    /// and the three tooltip-aggregate properties end up empty too; that's what
    /// <see cref="CustomStatusWatcher.Refresh"/> does.
    /// </summary>
    internal static CustomStatusSnapshot Build(
        IEnumerable<(string? Title, string? Description)> moodlesStatuses,
        IEnumerable<(string? Title, string? Description)> lociStatuses,
        IReadOnlyList<TooltipKeywordRule> tooltipRules)
    {
        var merged = new Dictionary<string, (string Name, string Description, StatusSource Sources)>(StringComparer.Ordinal);

        void Add(string? title, string? description, StatusSource source)
        {
            var name = StatusNames.Clean(title);
            if (name.Length == 0) return;

            var key = StatusNames.Key(name);
            var desc = description ?? "";

            merged[key] = merged.TryGetValue(key, out var seen)
                ? (seen.Name, seen.Description.Length > 0 ? seen.Description : desc, seen.Sources | source)
                : (name, desc, source);
        }

        foreach (var (title, description) in moodlesStatuses) Add(title, description, StatusSource.Moodles);
        foreach (var (title, description) in lociStatuses) Add(title, description, StatusSource.Loci);

        if (merged.Count == 0) return Empty;

        // Second pass, now that each status's final (post name-merge) description is settled: run
        // the keyword parser over it exactly once - seeding both this status's own TooltipMatches
        // (for the settings-window/diagnostic view) and the snapshot-wide aggregates EffectManager
        // actually draws from.
        var built = new List<ActiveCustomStatus>(merged.Count);
        var tooltipKinds = new HashSet<DebuffKind>();
        var tooltipStrengths = new Dictionary<DebuffKind, float>();
        var tooltipColors = new Dictionary<DebuffKind, Vector4>();

        foreach (var kv in merged)
        {
            var matches = tooltipRules.Count > 0 && kv.Value.Description.Length > 0
                ? TooltipKeywordParser.Parse(kv.Value.Description, tooltipRules)
                : Array.Empty<TooltipEffectMatch>();

            built.Add(new ActiveCustomStatus(kv.Key, kv.Value.Name, kv.Value.Description, kv.Value.Sources, matches));

            foreach (var match in matches)
            {
                tooltipKinds.Add(match.Kind);

                if (!tooltipStrengths.TryGetValue(match.Kind, out var bestStrength) || match.Strength > bestStrength)
                    tooltipStrengths[match.Kind] = match.Strength;

                if (match.Color is { } color && !tooltipColors.ContainsKey(match.Kind))
                    tooltipColors[match.Kind] = color;
            }
        }

        var statuses = built
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Key, StringComparer.Ordinal)
            .ToArray();

        return new CustomStatusSnapshot(statuses, tooltipKinds, tooltipStrengths, tooltipColors);
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
///
/// This is also the ONE place <see cref="TooltipKeywordRule"/> matching happens - see
/// <see cref="CustomStatusSnapshot"/>'s remarks - which is why this class needs a
/// <see cref="Configuration"/> reference at all (it otherwise has nothing to do with settings).
/// A plain name-based <see cref="CustomStatusRule"/>, by contrast, is still matched fresh every
/// frame in EffectManager against whatever this class last read, since that comparison is cheap
/// enough not to bother caching.
/// </summary>
public sealed class CustomStatusWatcher : IDisposable
{
    private const int ReadIntervalMs = 1000; // the one knob: how often Moodles and Loci get read (and tooltips re-parsed)
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
    private readonly Configuration _config;
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

    public CustomStatusWatcher(IDalamudPluginInterface pi, IFramework framework, IObjectTable objectTable, IPluginLog log, Configuration config)
        : this(pi, framework, objectTable, log, config, static () => Environment.TickCount64)
    {
    }

    /// <summary>Same as the public constructor, with the clock swappable so the timing can be tested.</summary>
    internal CustomStatusWatcher(
        IDalamudPluginInterface pi, IFramework framework, IObjectTable objectTable, IPluginLog log, Configuration config, Func<long> nowMs)
    {
        _framework = framework;
        _objectTable = objectTable;
        _log = log;
        _config = config;
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

        // Empty (not just "skip building") when the checkbox is off, so Build still runs its normal
        // merge but every status naturally ends up with no TooltipMatches - one code path either way.
        IReadOnlyList<TooltipKeywordRule> tooltipRules = _config.ParseCustomStatusTooltips
            ? _config.TooltipKeywordRules
            : Array.Empty<TooltipKeywordRule>();

        _snapshot = CustomStatusSnapshot.Build(ReadSource(_moodles, now), ReadSource(_loci, now), tooltipRules);
    }

    /// <summary>
    /// Reads one plugin's current status titles+descriptions. Never throws: a missing or
    /// misbehaving plugin just contributes nothing. Probes for a missing plugin at a slow rate, and
    /// if reads start failing (plugin unloaded, or its IPC shape changed) logs ONE warning for the
    /// streak, then retries quietly - on the very next read in case it was a blip, then at the slow
    /// probe rate.
    /// </summary>
    private IReadOnlyList<(string? Title, string? Description)> ReadSource(Source s, long now)
    {
        if (!s.Available)
        {
            if (now < s.NextProbeAt) return Array.Empty<(string?, string?)>();
            s.NextProbeAt = now + ProbeMs;

            bool found;
            try { found = s.Probe(); }
            catch { found = false; } // IpcNotReadyError: not loaded (yet)

            if (!found) return Array.Empty<(string?, string?)>();

            s.Available = true;
            if (!s.Failing)
                _log.Information($"RealDebuffs: {s.Name} detected - its custom statuses can now trigger effects.");
        }

        try
        {
            var heads = Heads(s.Read());
            if (s.Failing)
            {
                s.Failing = false;
                _log.Information($"RealDebuffs: reading {s.Name} statuses works again.");
            }
            return heads;
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
            return Array.Empty<(string?, string?)>();
        }
    }

    private static List<(string? Title, string? Description)> Heads(List<StatusHead>? list)
    {
        var heads = new List<(string?, string?)>(list?.Count ?? 0);
        if (list == null) return heads;

        foreach (var status in list)
            heads.Add((status.Title, status.Description));
        return heads;
    }

    public void Dispose() => _framework.Update -= OnUpdate;
}
