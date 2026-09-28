using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using RealDebuffs.Effects;

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
    private readonly Dictionary<uint, string> _idToName = new();
    private readonly IPluginLog _log;

    public StatusCatalog(IDataManager dataManager, IReadOnlyList<ISceneEffect> effects, IPluginLog log)
    {
        _log = log;

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
                    _log.Warning($"RealDebuffs: status name \"{name}\" claimed by both " +
                                 $"{existing} and {effect.Kind}; using {existing}.");
                    continue;
                }
                nameMap[name] = effect.Kind;
                if (strength < 0.999f)
                    strengths[name] = strength;
            }
        }

        var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>(ClientLanguage.English);
        if (sheet == null)
        {
            _log.Error("RealDebuffs: could not load the Status sheet - no debuff effects will trigger.");
            return;
        }

        foreach (var row in sheet)
        {
            var name = row.Name.ToString();
            if (string.IsNullOrEmpty(name)) continue;
            _idToName[row.RowId] = name;
            if (nameMap.TryGetValue(name, out var kind))
            {
                _idToKind[row.RowId] = kind;
                if (strengths.TryGetValue(name, out var strength))
                    _idToStrength[row.RowId] = strength;
            }
        }

        int found = _idToKind.Values.Distinct().Count();
        int expected = nameMap.Values.Distinct().Count();
        _log.Debug($"RealDebuffs: resolved {_idToKind.Count} row(s) covering {found}/{expected} kinds.");

        if (found < expected)
            _log.Warning("RealDebuffs: not every name in the effect roster's TriggerStatuses was found " +
                         "in the Status sheet, so some effects may never trigger. A status's English " +
                         "name may have changed in a recent patch.");
    }

    public bool TryGetKind(uint statusId, out DebuffKind kind) => _idToKind.TryGetValue(statusId, out kind);

    /// <summary>Like <see cref="TryGetKind"/>, plus how strong this particular status should look (1.0 = full).</summary>
    public bool TryGetEffect(uint statusId, out DebuffKind kind, out float strength)
    {
        strength = 1f;
        if (!_idToKind.TryGetValue(statusId, out kind)) return false;
        if (_idToStrength.TryGetValue(statusId, out var s)) strength = s;
        return true;
    }

    /// <summary>Human-readable name for any status ID the sheet knows about, for /realdebuffs statuses.</summary>
    public string GetName(uint statusId) => _idToName.TryGetValue(statusId, out var name) ? name : $"#{statusId}";
}