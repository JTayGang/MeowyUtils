using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using RealDebuffs.Effects.Framework;

// Moodles and Loci hand a status back over IPC as a tuple whose first five fields are
// (Version, GUID, IconID, Title, Description). Declaring only what we read lets either plugin
// append fields later without breaking us. Description is kept raw for the keyword parser, which
// needs the [color=] tags still in place.
using StatusHead = (int Version, System.Guid GUID, long IconID, string Title, string Description);

namespace RealDebuffs;

/// <summary>Which plugin(s) are currently reporting a status.</summary>
[Flags]
public enum StatusSource { None = 0, Moodles = 1, Loci = 2 }

/// <summary>One distinct custom status currently on the player.</summary>
public sealed record ActiveCustomStatus(
    string Key, string Name, string Description, StatusSource Sources,
    IReadOnlyList<TooltipEffectMatch> TooltipMatches);

/// <summary>
/// Immutable snapshot of "which Moodles/Loci statuses are on the player right now", merged across
/// both plugins by name (a mirrored status counts once). The watcher swaps in a fresh one whenever
/// it re-reads; draw code can hold a reference for a frame without any locking.
///
/// Tooltip keyword matching is done ONCE per Build, not per frame: it's real text processing on
/// text that's already only as fresh as the last IPC read, so caching matches the read cadence.
/// The Tooltip* aggregates are what EffectManager merges per frame; each status's own
/// TooltipMatches is for the diagnostic view. Name-based rules are cheap, so they keep matching
/// fresh every frame - see AddActiveKinds.
/// </summary>
public sealed class CustomStatusSnapshot
{
    public static readonly CustomStatusSnapshot Empty = new(
        Array.Empty<ActiveCustomStatus>(),
        new HashSet<DebuffKind>(),
        new Dictionary<DebuffKind, Vector4>(),
        new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly HashSet<string> _keys;

    /// <summary>Every distinct active status, sorted by name.</summary>
    public IReadOnlyList<ActiveCustomStatus> Statuses { get; }

    /// <summary>Every kind any active status's tooltip asks for. Empty when keyword parsing is off.</summary>
    public IReadOnlyCollection<DebuffKind> TooltipKinds { get; }

    /// <summary>Per-kind color from whichever tooltip match resolved one first.</summary>
    public IReadOnlyDictionary<DebuffKind, Vector4> TooltipColors { get; }

    /// <summary>
    /// Per-kind material substitutions resolved from description text, keyed by the same
    /// "{Kind}.{Type}.{Role}" format the renderer uses. Populated only for phrases that type-match
    /// the target effect's hero slots (see EffectHeroSlots); phrases on non-hero types, or on
    /// effects with no hero slots, are silently dropped.
    /// </summary>
    public IReadOnlyDictionary<string, string> TooltipMaterialOverrides { get; }

    private CustomStatusSnapshot(
        ActiveCustomStatus[] statuses,
        HashSet<DebuffKind> tooltipKinds,
        Dictionary<DebuffKind, Vector4> tooltipColors,
        Dictionary<string, string> tooltipMaterials)
    {
        Statuses = statuses;
        _keys = new HashSet<string>(statuses.Select(s => s.Key), StringComparer.Ordinal);
        TooltipKinds = tooltipKinds;
        TooltipColors = tooltipColors;
        TooltipMaterialOverrides = tooltipMaterials;
    }

    /// <summary>True if a status with this comparison key (see <see cref="StatusNames.Key"/>) is active.</summary>
    public bool Contains(string key) => key.Length > 0 && _keys.Contains(key);

    /// <summary>
    /// Merges the two plugins' status lists by name (a non-empty description already on record wins
    /// over a conflicting later one), then resolves tooltip keywords once over the merged
    /// descriptions. Pass an empty rule list to skip keyword parsing.
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

        var built = new List<ActiveCustomStatus>(merged.Count);
        var tooltipKinds = new HashSet<DebuffKind>();
        var tooltipColors = new Dictionary<DebuffKind, Vector4>();
        var tooltipMaterials = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var kv in merged)
        {
            var matches = tooltipRules.Count > 0 && kv.Value.Description.Length > 0
                ? TooltipKeywordParser.Parse(kv.Value.Description, tooltipRules)
                : Array.Empty<TooltipEffectMatch>();

            built.Add(new ActiveCustomStatus(kv.Key, kv.Value.Name, kv.Value.Description, kv.Value.Sources, matches));

            foreach (var m in matches)
            {
                tooltipKinds.Add(m.Kind);

                if (m.Color is { } c && !tooltipColors.ContainsKey(m.Kind))
                    tooltipColors[m.Kind] = c;

                if (m.MaterialSubstitution is not { } matName) continue;

                // Resolve the substitution against the effect's hero slots. Type must match: a
                // stroke material can't fill a particle slot, so "made of lightning" on Burns is
                // dropped rather than producing nonsense (Burns still gets its color).
                string matType = PrefixOf(matName);
                foreach (var hero in EffectRegistry.HeroSlotsFor(m.Kind))
                {
                    // Same-type match: material axis (stroke material on stroke hero, etc.).
                    if (string.Equals(hero.PrimitiveType, matType, StringComparison.OrdinalIgnoreCase))
                    {
                        string key = MaterialOverrideKey.For(m.Kind, hero.PrimitiveType, hero.Role);
                        if (!tooltipMaterials.ContainsKey(key))
                            tooltipMaterials[key] = matName;
                    }
                    // Cross-type on a stroke: a particle material means "emit this along the
                    // stroke", routed to the emit axis. This is what makes "chains made of
                    // flames" work: chains keep their material, but shed fire particles.
                    else if (string.Equals(hero.PrimitiveType, "Stroke", StringComparison.OrdinalIgnoreCase)
                             && string.Equals(matType, "particle", StringComparison.OrdinalIgnoreCase))
                    {
                        string key = MaterialOverrideKey.ForStrokeEmit(m.Kind, hero.Role);
                        if (!tooltipMaterials.ContainsKey(key))
                            tooltipMaterials[key] = matName;
                    }
                    // Any other cross-type (stroke material on particle hero, etc.) is dropped.
                }
            }
        }

        var statuses = built
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Key, StringComparer.Ordinal)
            .ToArray();

        return new CustomStatusSnapshot(statuses, tooltipKinds, tooltipColors, tooltipMaterials);
    }

    /// <summary>
    /// Adds the kind of every enabled rule whose status is currently active. A set on purpose -
    /// a kind already active from any source is a no-op, so effects never stack or restart.
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

    /// <summary>"particle.snow" → "particle". Used to type-check a material against a hero slot.</summary>
    private static string PrefixOf(string materialName)
    {
        int dot = materialName.IndexOf('.');
        return dot >= 0 ? materialName[..dot] : materialName;
    }
}

/// <summary>
/// Reads Moodles and Loci over IPC at most once a second and publishes a CustomStatusSnapshot.
/// Presence in the plugin's own list is the only "is it active" signal available (duration is
/// configured length, not time-remaining) - which is fine, because both plugins drop expired
/// statuses themselves. A missing plugin just reports nothing and is re-probed on a slow timer, so
/// installing mid-session picks up without a reload.
/// </summary>
public sealed class CustomStatusWatcher : IDisposable
{
    private const int ReadIntervalMs = 1000;
    private const int ProbeMs = 2000;
    private const int MoodlesMinVersion = 4;

    private sealed class Source
    {
        public required string Name { get; init; }
        public required Func<bool> Probe { get; init; }
        public required Func<List<StatusHead>> Read { get; init; }
        public bool Available;
        public bool Failing;
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

    /// <summary>The current picture (at most about a second old); never null.</summary>
    public CustomStatusSnapshot Snapshot => _snapshot;
    public bool MoodlesAvailable => _moodles.Available;
    public bool LociAvailable => _loci.Available;

    public CustomStatusWatcher(IDalamudPluginInterface pi, IFramework framework, IObjectTable objectTable, IPluginLog log, Configuration config)
        : this(pi, framework, objectTable, log, config, static () => Environment.TickCount64) { }

    /// <summary>Same as the public constructor, with the clock swappable so timing can be tested.</summary>
    internal CustomStatusWatcher(
        IDalamudPluginInterface pi, IFramework framework, IObjectTable objectTable, IPluginLog log, Configuration config, Func<long> nowMs)
    {
        _framework = framework;
        _objectTable = objectTable;
        _log = log;
        _config = config;
        _nowMs = nowMs;

        // Same IPC endpoints SkyrimCompass uses for its mirroring. Plain calls only - no event
        // subscriptions to register or clean up.
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
            Probe = () => { lociApiVersion.InvokeFunc(); return lociEnabled.InvokeFunc(); },
            Read = () => lociGet.InvokeFunc(),
        };

        _framework.Update += OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        long now = _nowMs();
        if (now < _nextReadAt) return; // every frame, but only actually reads once per interval
        _nextReadAt = now + ReadIntervalMs;

        try { Refresh(now); _updateErrorLogged = false; }
        catch (Exception ex)
        {
            if (_updateErrorLogged) return;
            _updateErrorLogged = true;
            _log.Error(ex, "RealDebuffs: updating custom (Moodles/Loci) statuses failed.");
        }
    }

    private void Refresh(long now)
    {
        if (_objectTable.LocalPlayer == null) { _snapshot = CustomStatusSnapshot.Empty; return; }

        // Empty (not skipped) when the checkbox is off: Build still runs its normal merge, just
        // with no rules to match against - one code path either way.
        IReadOnlyList<TooltipKeywordRule> tooltipRules = _config.ParseCustomStatusTooltips
            ? _config.TooltipKeywordRules
            : Array.Empty<TooltipKeywordRule>();

        _snapshot = CustomStatusSnapshot.Build(ReadSource(_moodles, now), ReadSource(_loci, now), tooltipRules);
    }

    /// <summary>
    /// Reads one plugin's current statuses. Never throws: a missing or misbehaving plugin just
    /// contributes nothing. Logs one warning per failure streak, not one per read.
    /// </summary>
    private IReadOnlyList<(string? Title, string? Description)> ReadSource(Source s, long now)
    {
        if (!s.Available)
        {
            if (now < s.NextProbeAt) return Array.Empty<(string?, string?)>();
            s.NextProbeAt = now + ProbeMs;

            bool found;
            try { found = s.Probe(); } catch { found = false; } // IpcNotReadyError: not loaded (yet)
            if (!found) return Array.Empty<(string?, string?)>();

            s.Available = true;
            if (!s.Failing)
                _log.Information($"RealDebuffs: {s.Name} detected.");
        }

        try
        {
            var heads = Heads(s.Read());
            if (s.Failing) { s.Failing = false; _log.Information($"RealDebuffs: reading {s.Name} statuses works again."); }
            return heads;
        }
        catch (Exception ex)
        {
            s.Available = false;
            s.NextProbeAt = s.Failing ? now + ProbeMs : now; // one retry right away in case of a blip
            if (!s.Failing)
            {
                s.Failing = true;
                _log.Warning(ex, $"RealDebuffs: couldn't read {s.Name} statuses; will keep retrying quietly.");
            }
            return Array.Empty<(string?, string?)>();
        }
    }

    private static List<(string? Title, string? Description)> Heads(List<StatusHead>? list)
    {
        var heads = new List<(string?, string?)>(list?.Count ?? 0);
        if (list == null) return heads;
        foreach (var status in list) heads.Add((status.Title, status.Description));
        return heads;
    }

    public void Dispose() => _framework.Update -= OnUpdate;
}

/// <summary>
/// One "while I have THIS custom status, show THAT effect" link. Matched by title - the only thing
/// a user can read off the screen and that a mirror plugin carries across unchanged. Rules can
/// overlap freely (one name -> several effects, several names -> one effect); EffectManager
/// dedupes via a set, so nothing stacks.
/// </summary>
public class CustomStatusRule
{
    private string _name = "";
    private string? _key;

    /// <summary>The status title to watch for, as the user typed or picked it.</summary>
    public string Name
    {
        get => _name;
        set { _name = value ?? ""; _key = null; }
    }

    /// <summary>Saved as the enum's number, so new kinds must go at the END of DebuffKind.</summary>
    public DebuffKind Kind { get; set; } = DebuffKind.Bind;

    /// <summary>Lets a rule be switched off without deleting it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cached key; EffectManager asks every frame. Method, not property, so it isn't saved.</summary>
    public string GetKey() => _key ??= StatusNames.Key(_name);
}

/// <summary>
/// How status titles are compared. Titles are user-typed free text and may carry Moodles/Loci
/// markup ([color=..], [glow=..], [i], and their closing tags), so comparison strips tags,
/// collapses whitespace, and lowercases.
/// </summary>
public static class StatusNames
{
    private static readonly Regex Markup = new(
        @"\[color=[0-9a-z]+\]|\[/color\]|\[glow=[0-9a-z]+\]|\[/glow\]|\[i\]|\[/i\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>Readable form: tags removed, whitespace tidied, original casing kept.</summary>
    public static string Clean(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        return Spaces.Replace(Markup.Replace(title, ""), " ").Trim();
    }

    /// <summary>Comparison form: <see cref="Clean"/> plus lower-casing. Empty never matches.</summary>
    public static string Key(string? title) => Clean(title).ToLowerInvariant();
}