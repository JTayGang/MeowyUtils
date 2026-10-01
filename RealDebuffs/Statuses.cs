using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using RealDebuffs.Effects;
using RealDebuffs.Effects.Framework;

// Moodles and Loci hand a status back over IPC as a tuple whose first five fields are
// (Version, GUID, IconID, Title, Description). Declaring only what we read lets either plugin
// append fields later without breaking us. Description is kept raw for the keyword parser, which
// needs the [color=] tags still in place.
using StatusHead = (int Version, System.Guid GUID, long IconID, string Title, string Description);
namespace RealDebuffs;

/// <summary>
/// The set of visual debuff "families" this plugin renders. Several vanilla statuses can map to
/// one kind when they're mechanically the same (Stun and Down for the Count both mean "can't
/// act"), so this is a curated list of feelings, not a 1:1 mirror of every status name.
///
/// To add a kind: add it here at the END, then write an ISceneEffect that declares it. The
/// effect's TriggerStatuses map the kind to in-game status names, and EffectDiscovery picks the
/// effect up automatically - nothing else needs to change.
///
/// New kinds must go at the END. Saved custom-status rules, the DisabledKinds set, and material
/// overrides all serialize the enum's numeric value.
/// </summary>
public enum DebuffKind
{
    Blind,
    Paralysis,
    Silence,
    Stun,
    Sleep,
    Poison,
    Bind,
    Heavy,
    Petrification,

    // Added later.
    Amnesia,
    Bleeding,
    Weakness,       // Weakness, Brush with Death, Brink of Death - one look at three strengths
    Burns,
    Charm,          // Infatuated and Seduced - one look at two strengths
    Frost,          // Frostbite and Deep Freeze - one look at two strengths
    Disease,
    Doom,
    Dropsy,
    Electrocution,
    Hysteria,
    Infirmity,
    Misery,
    Pacification,
    Slow,
    Sludge,
    Vulnerability,
    Windburn,
}

/// <summary>
/// Resolves vanilla status IDs to DebuffKinds by loading the English Status sheet at startup and
/// matching on the name. The name map is built from each effect's TriggerStatuses, so adding a
/// new effect requires no changes here - the effect declares which statuses light it up, and
/// StatusCatalog merges every effect's declarations into one table.
///
/// Always resolved against the English sheet regardless of the client's UI language: StatusIds
/// themselves are language-independent, this just makes the name matching done here reliable
/// regardless of what language the game client displays.
/// </summary>
public sealed class StatusCatalog
{
    private readonly Dictionary<uint, DebuffKind> _idToKind = new();
    private readonly Dictionary<uint, float> _idToStrength = new();
    private readonly IDataManager _dataManager;

    public StatusCatalog(IDataManager dataManager, IReadOnlyList<ISceneEffect> effects, IPluginLog log)
    {
        _dataManager = dataManager;

        // Merge every effect's TriggerStatuses into one name -> kind table. Duplicate names
        // (two effects claiming the same in-game status) are a configuration mistake worth
        // surfacing: first effect wins, and a warning is logged.
        var nameMap = new Dictionary<string, DebuffKind>(StringComparer.OrdinalIgnoreCase);
        var strengths = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        foreach (var effect in effects)
        {
            foreach (var (name, strength) in effect.TriggerStatuses)
            {
                if (nameMap.TryGetValue(name, out var existing))
                {
                    log.Warning($"RealDebuffs: status name \"{name}\" claimed by both " +
                                 $"{existing} and {effect.Kind}; using {existing}.");
                    continue;
                }
                nameMap[name] = effect.Kind;
                if (strength < 0.999f)
                    strengths[name] = strength;
            }
        }

        foreach (var row in dataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>(ClientLanguage.English))
        {
            var name = row.Name.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            if (nameMap.TryGetValue(name, out var kind))
            {
                _idToKind[row.RowId] = kind;
                if (strengths.TryGetValue(name, out var strength))
                    _idToStrength[row.RowId] = strength;
            }
        }

        int found = _idToKind.Values.Distinct().Count();
        int expected = nameMap.Values.Distinct().Count();
        log.Debug($"RealDebuffs: resolved {_idToKind.Count} row(s) covering {found}/{expected} kinds.");

        if (found < expected)
            log.Warning("RealDebuffs: not every name in the effect roster's TriggerStatuses was found " +
                         "in the Status sheet, so some effects may never trigger. A status's English " +
                         "name may have changed in a recent patch.");
    }

    /// <summary>Maps a status ID to its kind, plus how strong that status should look (1.0 = full).</summary>
    public bool TryGetEffect(uint statusId, out DebuffKind kind, out float strength)
    {
        strength = 1f;
        if (!_idToKind.TryGetValue(statusId, out kind)) return false;
        if (_idToStrength.TryGetValue(statusId, out var s)) strength = s;
        return true;
    }

    /// <summary>Human-readable name for any status ID, for /realdebuffs statuses. Looked up on demand.</summary>
    public string GetName(uint statusId)
    {
        var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>(ClientLanguage.English);
        var name = sheet.TryGetRow(statusId, out var row) ? row.Name.ToString() : "";
        return name.Length > 0 ? name : $"#{statusId}";
    }
}

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
        IEnumerable<StatusHead> moodlesStatuses,
        IEnumerable<StatusHead> lociStatuses,
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

        foreach (var status in moodlesStatuses) Add(status.Title, status.Description, StatusSource.Moodles);
        foreach (var status in lociStatuses) Add(status.Title, status.Description, StatusSource.Loci);

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

    /// <summary>"particle.snow" → "particle". Used to type-check a material against a hero slot.</summary>
    private static string PrefixOf(string materialName)
    {
        int dot = materialName.IndexOf('.');
        return dot >= 0 ? materialName[..dot] : materialName;
    }
}

/// <summary>
/// Reads Moodles and Loci over IPC and publishes a CustomStatusSnapshot. This is the plugin's one
/// periodic heartbeat: once a second (or sooner when the settings change, see RequestRefresh) it
/// rebuilds the snapshot and advances Tick. Everything that only needs to be current to within a
/// second - including EffectManager's resolved settings - refreshes off Tick instead of running
/// its own timer or redoing the work every frame.
///
/// Presence in the plugin's own list is the only "is it active" signal available (duration is
/// configured length, not time-remaining) - which is fine, because both plugins drop expired
/// statuses themselves. A missing plugin just reports nothing and is re-probed on a slow timer, so
/// installing mid-session picks up without a reload.
/// </summary>
public sealed class CustomStatusWatcher : IDisposable
{
    private const int HeartbeatMs = 1000;
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
    private readonly Source _moodles;
    private readonly Source _loci;

    private volatile CustomStatusSnapshot _snapshot = CustomStatusSnapshot.Empty;
    private bool _updateErrorLogged;
    private long _nextReadAt;
    private volatile bool _refreshRequested;
    private volatile int _tick;

    /// <summary>The current picture (at most about a second old); never null.</summary>
    public CustomStatusSnapshot Snapshot => _snapshot;

    /// <summary>
    /// Advances once per heartbeat, whether or not anything changed (the snapshot stays the shared
    /// Empty instance while no custom statuses are active). Read it BEFORE Snapshot: if a beat lands
    /// in between, the caller re-syncs next frame instead of missing an update.
    /// </summary>
    public int Tick => _tick;

    /// <summary>Makes the next frame's update run now instead of waiting out the heartbeat.</summary>
    public void RequestRefresh() => _refreshRequested = true;
    public bool MoodlesAvailable => _moodles.Available;
    public bool LociAvailable => _loci.Available;

    public CustomStatusWatcher(IDalamudPluginInterface pi, IFramework framework, IObjectTable objectTable, IPluginLog log, Configuration config)
    {
        _framework = framework;
        _objectTable = objectTable;
        _log = log;
        _config = config;

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
        long now = Environment.TickCount64;
        if (now < _nextReadAt && !_refreshRequested) return; // every frame, but only beats once per interval
        _refreshRequested = false;
        _nextReadAt = now + HeartbeatMs;

        try { Refresh(now); _updateErrorLogged = false; }
        catch (Exception ex)
        {
            if (!_updateErrorLogged)
            {
                _updateErrorLogged = true;
                _log.Error(ex, "RealDebuffs: updating custom (Moodles/Loci) statuses failed.");
            }
        }
        finally { _tick++; }
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
    private IReadOnlyList<StatusHead> ReadSource(Source s, long now)
    {
        if (!s.Available)
        {
            if (now < s.NextProbeAt) return Array.Empty<StatusHead>();
            s.NextProbeAt = now + ProbeMs;

            bool found;
            try { found = s.Probe(); } catch { found = false; } // IpcNotReadyError: not loaded (yet)
            if (!found) return Array.Empty<StatusHead>();

            s.Available = true;
            if (!s.Failing)
                _log.Information($"RealDebuffs: {s.Name} detected.");
        }

        try
        {
            var heads = (IReadOnlyList<StatusHead>?)s.Read() ?? Array.Empty<StatusHead>();
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
            return Array.Empty<StatusHead>();
        }
    }

    public void Dispose() => _framework.Update -= OnUpdate;
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