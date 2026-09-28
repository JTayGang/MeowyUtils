using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Plugin.Services;

namespace RealDebuffs;

/// <summary>
/// The set of visual debuff "families" this plugin renders. Several vanilla statuses can map to
/// one kind when they're mechanically the same (Stun and Down for the Count both mean "can't
/// act"), so this is a curated list of feelings, not a 1:1 mirror of every status name.
///
/// To add a kind: add it here, add its name(s) to StatusCatalog.NameMap, and write an
/// ISceneEffect. Add new kinds at the END - saved custom-status rules store the enum's number,
/// so reordering would re-point existing rules.
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
/// matching on the name. Matching by name (rather than hardcoded row IDs) survives a status's row
/// ID shifting between patches. Always uses the English sheet regardless of the client's UI
/// language, so the name matching here is stable.
/// </summary>
public sealed class StatusCatalog
{
    /// <summary>
    /// English status name -> DebuffKind. Names are matched exactly (ignoring case). See the README
    /// for the full checklist when adding a new entry.
    /// </summary>
    public static readonly Dictionary<string, DebuffKind> NameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Blind"] = DebuffKind.Blind,
        ["Paralysis"] = DebuffKind.Paralysis,
        ["Silence"] = DebuffKind.Silence,
        ["Stun"] = DebuffKind.Stun,
        ["Down for the Count"] = DebuffKind.Stun,
        ["Sleep"] = DebuffKind.Sleep,
        ["Poison"] = DebuffKind.Poison,
        ["Bind"] = DebuffKind.Bind,
        ["Heavy"] = DebuffKind.Heavy,
        ["Petrification"] = DebuffKind.Petrification,
        ["Amnesia"] = DebuffKind.Amnesia,
        ["Bleeding"] = DebuffKind.Bleeding,
        ["Weakness"] = DebuffKind.Weakness,
        ["Brush with Death"] = DebuffKind.Weakness,
        ["Brink of Death"] = DebuffKind.Weakness,
        ["Burns"] = DebuffKind.Burns,
        ["Infatuated"] = DebuffKind.Charm,
        ["Seduced"] = DebuffKind.Charm,
        ["Charm"] = DebuffKind.Charm,    // not in today's English sheet, kept so they work if a
        ["Charmed"] = DebuffKind.Charm,  // patch ever adds them
        ["Seduce"] = DebuffKind.Charm,
        ["Frostbite"] = DebuffKind.Frost,
        ["Deep Freeze"] = DebuffKind.Frost,
        ["Disease"] = DebuffKind.Disease,
        ["Doom"] = DebuffKind.Doom,
        ["Dropsy"] = DebuffKind.Dropsy,
        ["Electrocution"] = DebuffKind.Electrocution,
        ["Hysteria"] = DebuffKind.Hysteria,
        ["Infirmity"] = DebuffKind.Infirmity,
        ["Misery"] = DebuffKind.Misery,
        ["Pacification"] = DebuffKind.Pacification,
        ["Slow"] = DebuffKind.Slow,
        ["Sludge"] = DebuffKind.Sludge,
        ["Vulnerability Up"] = DebuffKind.Vulnerability,
        ["Windburn"] = DebuffKind.Windburn,
    };

    /// <summary>
    /// For kinds that cover several severities: how strong each name's effect is vs. the kind's
    /// full look. A name not listed here is full strength.
    /// </summary>
    public static readonly Dictionary<string, float> Strengths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Weakness"] = 0.55f,
        ["Brush with Death"] = 0.78f,
        ["Frostbite"] = 0.55f,
        ["Infatuated"] = 0.60f,
        ["Charm"] = 0.60f,
        ["Charmed"] = 0.60f,
    };

    private readonly Dictionary<uint, DebuffKind> _idToKind = new();
    private readonly Dictionary<uint, float> _idToStrength = new();
    private readonly Dictionary<uint, string> _idToName = new();
    private readonly IPluginLog _log;

    public StatusCatalog(IDataManager dataManager, IPluginLog log)
    {
        _log = log;

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
            if (NameMap.TryGetValue(name, out var kind))
            {
                _idToKind[row.RowId] = kind;
                if (Strengths.TryGetValue(name, out var strength))
                    _idToStrength[row.RowId] = strength;
            }
        }

        int found = _idToKind.Values.Distinct().Count();
        int expected = NameMap.Values.Distinct().Count();
        _log.Debug($"RealDebuffs: resolved {_idToKind.Count} row(s) covering {found}/{expected} kinds.");

        if (found < expected)
            _log.Warning("RealDebuffs: not every name in StatusCatalog.NameMap was found in the Status " +
                         "sheet, so some effects may never trigger. A status's English name may have " +
                         "changed in a recent patch - compare NameMap against the current sheet.");
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