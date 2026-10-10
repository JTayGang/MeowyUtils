using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using RealDebuffs.Effects;
using RealDebuffs.Effects.Framework;

// Moodles/Loci return a status over IPC as (Version, GUID, IconID, Title, Description, ...); Description stays raw for the keyword parser.
using StatusHead = (int Version, System.Guid GUID, long IconID, string Title, string Description);
namespace RealDebuffs;

/// <summary>Visual debuff families; several vanilla statuses can share a kind. Numeric values are serialized: add new kinds at the END.</summary>
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

/// <summary>Resolves vanilla status IDs to DebuffKinds via each effect's TriggerStatuses and the English Status sheet.</summary>
public sealed class StatusCatalog
{
    // Kind and strength together: looked up per status per frame, so one hash lookup instead of two.
    private readonly Dictionary<uint, (DebuffKind Kind, float Strength)> _idToInfo = new();
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
                float strength = strengths.TryGetValue(name, out var s) ? s : 1f;
                _idToInfo[row.RowId] = (kind, strength);
            }
        }

        int found = _idToInfo.Values.Select(v => v.Kind).Distinct().Count();
        int expected = nameMap.Values.Distinct().Count();
        log.Debug($"RealDebuffs: resolved {_idToInfo.Count} row(s) covering {found}/{expected} kinds.");

        if (found < expected)
            log.Warning("RealDebuffs: not every name in the effect roster's TriggerStatuses was found " +
                         "in the Status sheet, so some effects may never trigger. A status's English " +
                         "name may have changed in a recent patch.");
    }

    public bool TryGetEffect(uint statusId, out DebuffKind kind, out float strength)
    {
        if (_idToInfo.TryGetValue(statusId, out var info))
        {
            kind = info.Kind;
            strength = info.Strength;
            return true;
        }
        kind = default;
        strength = 1f;
        return false;
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

    /// <summary>Material substitutions from description text, keyed as the renderer expects.</summary>
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

    /// <summary>Merges both plugins' statuses by name, then resolves tooltip keywords over the merged descriptions.</summary>
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
            // A non-empty description already on record wins (Loci and Moodles may duplicate a name).
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

                // Type-check against the hero slots: a stroke can't fill a particle slot; a particle on a stroke hero goes to the emit axis.
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

/// <summary>Reads Moodles/Loci over IPC into a CustomStatusSnapshot about once a second (sooner on settings change); a missing plugin is re-probed slowly.</summary>
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

    /// <summary>Advances once per heartbeat; read BEFORE Snapshot so a beat in between re-syncs next frame.</summary>
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

    /// <summary>Never throws; logs one warning per failure streak.</summary>
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

/// <summary>Status title comparison: strip markup, collapse whitespace, lowercase (Key). Clean has a fast path (called per frame).</summary>
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

        // Fast path: no markup char or whitespace means both regexes and Trim would be no-ops.
        bool maybe = false;
        for (int i = 0; i < title.Length; i++)
        {
            char c = title[i];
            if (c == '[' || char.IsWhiteSpace(c)) { maybe = true; break; }
        }
        if (!maybe) return title;

        return Spaces.Replace(Markup.Replace(title, ""), " ").Trim();
    }

    /// <summary>Comparison form: Clean plus lowercasing. Empty never matches.</summary>
    public static string Key(string? title) => Clean(title).ToLowerInvariant();
}