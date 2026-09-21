using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Plugin.Services;

namespace RealDebuffs;

/// <summary>
/// The set of "real" visual debuff families this plugin renders. Several vanilla FFXIV statuses
/// map to the same kind when they're mechanically identical (e.g. Stun / Deep Freeze / Down for
/// the Count all mean "can't act, can't move" - they just come from different sources), so this
/// is a curated list of *feelings*, not a 1:1 mirror of every status name.
///
/// To add a new kind: add it here, add its name(s) to <see cref="StatusCatalog.NameMap"/>, write
/// an <see cref="Effects.IScreenEffect"/> for it, and drop that effect into the order list in
/// <see cref="EffectManager"/>. See the README for a worked example.
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
}

/// <summary>
/// Resolves vanilla FFXIV status IDs to <see cref="DebuffKind"/>s by loading the Status Excel
/// sheet ONCE at startup and matching on the English status name. Matching by name (rather than
/// hardcoding row IDs) means this keeps working even if a status's row ID ever changes between
/// patches, and makes it trivial to extend - just add a name to <see cref="NameMap"/>.
///
/// Always resolved against the English sheet regardless of the client's UI language: StatusIds
/// themselves are language-independent, this just makes the *name matching done here* reliable no
/// matter what language the game client is actually set to display.
/// </summary>
public sealed class StatusCatalog
{
    /// <summary>
    /// English status name -&gt; the DebuffKind we render for it. Extend the plugin by adding more
    /// entries here (Amnesia, Pacification, Charm, Seduce, Doom, Weakness, ...) - see the README
    /// for the full checklist of what else needs to happen alongside a new entry here.
    /// </summary>
    public static readonly Dictionary<string, DebuffKind> NameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Blind"] = DebuffKind.Blind,
        ["Paralysis"] = DebuffKind.Paralysis,
        ["Silence"] = DebuffKind.Silence,
        ["Stun"] = DebuffKind.Stun,
        ["Deep Freeze"] = DebuffKind.Stun,
        ["Down for the Count"] = DebuffKind.Stun,
        ["Sleep"] = DebuffKind.Sleep,
        ["Poison"] = DebuffKind.Poison,
        ["Bind"] = DebuffKind.Bind,
        ["Heavy"] = DebuffKind.Heavy,
        ["Petrification"] = DebuffKind.Petrification,
    };

    private readonly Dictionary<uint, DebuffKind> _idToKind = new();
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
                _idToKind[row.RowId] = kind;
        }

        var distinctKindsFound = _idToKind.Values.Distinct().Count();
        var distinctKindsExpected = NameMap.Values.Distinct().Count();
        _log.Debug($"RealDebuffs: resolved {_idToKind.Count} status row(s) covering " +
                   $"{distinctKindsFound}/{distinctKindsExpected} configured debuff kinds.");

        if (distinctKindsFound < distinctKindsExpected)
        {
            _log.Warning("RealDebuffs: not every name in StatusCatalog.NameMap was found in the Status " +
                         "sheet, so some effects may never trigger. This usually means a status's English " +
                         "name changed in a recent patch - compare NameMap against the current sheet.");
        }
    }

    public bool TryGetKind(uint statusId, out DebuffKind kind) => _idToKind.TryGetValue(statusId, out kind);

    /// <summary>Human-readable name for any status ID the sheet knows about, for the /realdebuffs statuses diagnostic. Falls back to the raw ID if the sheet lookup ever comes up empty.</summary>
    public string GetName(uint statusId) => _idToName.TryGetValue(statusId, out var name) ? name : $"#{statusId}";
}
