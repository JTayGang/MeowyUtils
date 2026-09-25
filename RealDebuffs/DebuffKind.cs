using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game;
using Dalamud.Plugin.Services;

namespace RealDebuffs;

/// <summary>
/// The set of "real" visual debuff families this plugin renders. Several vanilla FFXIV statuses
/// map to the same kind when they're mechanically identical (e.g. Stun / Down for
/// the Count both mean "can't act, can't move" - they just come from different sources), so this
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

    // Added later. Add new kinds at the END: saved custom-status rules store the enum's number.
    Amnesia,
    Bleeding,
    Weakness,       // Weakness, Brush with Death and Brink of Death - one look at three strengths
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
    /// entries here - see the README for the full checklist of what else needs to happen alongside
    /// a new entry. Names are matched exactly (ignoring case) against the English Status sheet.
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
        ["Charm"] = DebuffKind.Charm,       // there's no "Charm"/"Charmed"/"Seduce" in today's English sheet;
        ["Charmed"] = DebuffKind.Charm,     // kept so they work if a patch ever adds them
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
    /// For kinds that cover several statuses of different severity: how strong each name's effect is
    /// compared to the kind's full look (1.0). A name not listed here is full strength. The effect
    /// is drawn at this fraction of its normal intensity, so Weakness reads as a faint version of
    /// what Brink of Death looks like.
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

    /// <summary>Like <see cref="TryGetKind"/>, plus how strong this particular status should look (1.0 = the kind's full effect).</summary>
    public bool TryGetEffect(uint statusId, out DebuffKind kind, out float strength)
    {
        strength = 1f;
        if (!_idToKind.TryGetValue(statusId, out kind)) return false;
        if (_idToStrength.TryGetValue(statusId, out var s)) strength = s;
        return true;
    }

    /// <summary>Human-readable name for any status ID the sheet knows about, for the /realdebuffs statuses diagnostic. Falls back to the raw ID if the sheet lookup ever comes up empty.</summary>
    public string GetName(uint statusId) => _idToName.TryGetValue(statusId, out var name) ? name : $"#{statusId}";
}
