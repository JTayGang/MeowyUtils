using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using RealDebuffs.Effects;
using RealDebuffs.Effects.Framework;

// Moodles and Loci hand a status back over IPC as a tuple whose first five fields are
// (Version, GUID, IconID, Title, Description). Description is kept raw for the keyword parser.
using StatusHead = (int Version, System.Guid GUID, long IconID, string Title, string Description);
namespace RealDebuffs;

/// <summary>
/// Visual debuff families. Several vanilla statuses can map to one kind when they're mechanically
/// the same. To add a kind: add it at the END (numeric values are serialized), then write an
/// ISceneEffect that declares it; EffectDiscovery picks it up automatically.
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

    Amnesia,
    Bleeding,
    Weakness,
    Burns,
    Charm,
    Frost,
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
/// matching on name. The name map is built from each effect's TriggerStatuses, so adding a new
/// effect requires no changes here.
/// </summary>
public sealed class StatusCatalog
{
    private readonly Dictionary<uint, DebuffKind> _idToKind = new();
    private readonly Dictionary<uint, float> _idToStrength = new();
    private readonly IDataManager _dataManager;

    public StatusCatalog(IDataManager dataManager, IReadOnlyList<ISceneEffect> effects, IPluginLog log)
    {
        _dataManager = dataManager;

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

    public bool TryGetEffect(uint statusId, out DebuffKind kind, out float strength)
    {
        strength = 1f;
        if (!_idToKind.TryGetValue(statusId, out kind)) return false;
        if (_idToStrength.TryGetValue(statusId, out var s)) strength = s;
        return true;
    }

    /// <summary>Human-readable name for any status ID, for /realdebuffs statuses.</summary>
    public string GetName(uint statusId)
    {
        var sheet = _dataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>(ClientLanguage.English);
        var name = sheet.TryGetRow(statusId, out var row) ? row.Name.ToString() : "";
        return name.Length > 0 ? name : $"#{statusId}";
    }
}

[Flags]
public enum StatusSource { None = 0, Moodles = 1, Loci = 2 }

public sealed record ActiveCustomStatus(
    string Key, string Name, string Description, StatusSource Sources,
    IReadOnlyList<TooltipEffectMatch> TooltipMatches);

/// <summary>
/// Immutable snapshot of "which Moodles/Loci statuses are on the player right now", merged across
/// both plugins by name. Tooltip keyword matching is done once per Build; name-based rules are
/// matched fresh every frame.
/// </summary>
public sealed class CustomStatusSnapshot
{
    public static readonly CustomStatusSnapshot Empty = new(
        Array.Empty<ActiveCustomStatus>(),
        new HashSet<DebuffKind>(),
        new Dictionary<DebuffKind, Vector4>(),
        new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly HashSet<string> _keys;

    public IReadOnlyList<ActiveCustomStatus> Statuses { get; }
    public IReadOnlyCollection<DebuffKind> TooltipKinds { get; }
    public IReadOnlyDictionary<DebuffKind, Vector4> TooltipColors { get; }

    /// <summary>
    /// Per-kind material substitutions from description text, keyed by the "{Kind}.{Type}.{Role}"
    /// format the renderer uses. Only populated for phrases that type-match the effect's hero slots.
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

    /// <summary>True if a status with this key is active.</summary>
    public bool Contains(string key) => key.Length > 0 && _keys.Contains(key);

    /// <summary>
    /// Merges the two plugins' status lists by name (a non-empty description already on record
    /// wins), then resolves tooltip keywords once over the merged descriptions.
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

                // Type-check the substitution against the effect's hero slots. A stroke material
                // can't fill a particle slot; a particle material on a stroke hero routes to the
                // emit axis ("chains made of flames" - chains keep their material, shed fire).
                string matType = PrefixOf(matName);
                foreach (var hero in EffectRegistry.HeroSlotsFor(m.Kind))
                {
                    if (string.Equals(hero.PrimitiveType, matType, StringComparison.OrdinalIgnoreCase))
                    {
                        string key = MaterialOverrideKey.For(m.Kind, hero.PrimitiveType, hero.Role);
                        if (!tooltipMaterials.ContainsKey(key))
                            tooltipMaterials[key] = matName;
                    }
                    else if (string.Equals(hero.PrimitiveType, "Stroke", StringComparison.OrdinalIgnoreCase)
                             && string.Equals(matType, "particle", StringComparison.OrdinalIgnoreCase))
                    {
                        string key = MaterialOverrideKey.ForStrokeEmit(m.Kind, hero.Role);
                        if (!tooltipMaterials.ContainsKey(key))
                            tooltipMaterials[key] = matName;
                    }
                }
            }
        }

        var statuses = built
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Key, StringComparer.Ordinal)
            .ToArray();

        return new CustomStatusSnapshot(statuses, tooltipKinds, tooltipColors, tooltipMaterials);
    }

    private static string PrefixOf(string materialName)
    {
        int dot = materialName.IndexOf('.');
        return dot >= 0 ? materialName[..dot] : materialName;
    }
}

/// <summary>
/// Reads Moodles and Loci over IPC and publishes a CustomStatusSnapshot. This is the plugin's one
/// periodic heartbeat: once a second (or sooner when settings change) it rebuilds the snapshot and
/// advances Tick. A missing plugin just reports nothing and is re-probed on a slow timer.
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

    public CustomStatusSnapshot Snapshot => _snapshot;

    /// <summary>
    /// Advances once per heartbeat. Read BEFORE Snapshot: if a beat lands in between, the caller
    /// re-syncs next frame instead of missing an update.
    /// </summary>
    public int Tick => _tick;

    public void RequestRefresh() => _refreshRequested = true;
    public bool MoodlesAvailable => _moodles.Available;
    public bool LociAvailable => _loci.Available;

    public CustomStatusWatcher(IDalamudPluginInterface pi, IFramework framework, IObjectTable objectTable, IPluginLog log, Configuration config)
    {
        _framework = framework;
        _objectTable = objectTable;
        _log = log;
        _config = config;

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
        if (now < _nextReadAt && !_refreshRequested) return;
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

        IReadOnlyList<TooltipKeywordRule> tooltipRules = _config.ParseCustomStatusTooltips
            ? _config.TooltipKeywordRules
            : Array.Empty<TooltipKeywordRule>();

        _snapshot = CustomStatusSnapshot.Build(ReadSource(_moodles, now), ReadSource(_loci, now), tooltipRules);
    }

    /// <summary>Reads one plugin's statuses. Never throws; logs one warning per failure streak.</summary>
    private IReadOnlyList<StatusHead> ReadSource(Source s, long now)
    {
        if (!s.Available)
        {
            if (now < s.NextProbeAt) return Array.Empty<StatusHead>();
            s.NextProbeAt = now + ProbeMs;

            bool found;
            try { found = s.Probe(); } catch { found = false; }
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
            s.NextProbeAt = s.Failing ? now + ProbeMs : now;
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
/// How status titles are compared. Strips Moodles/Loci markup, collapses whitespace, lowercases.
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

    /// <summary>Comparison form: Clean plus lowercasing. Empty never matches.</summary>
    public static string Key(string? title) => Clean(title).ToLowerInvariant();
}