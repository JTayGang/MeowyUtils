using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;
using GameAction = Lumina.Excel.Sheets.Action;

namespace FPSBooster;

/// <summary>
/// Builds a searchable AVFX catalogue from the same game-data relationships used by VFXEditor.
/// Timeline/scene files are scanned for null-terminated AVFX references on a worker thread.
/// </summary>
public sealed class VfxAssetCatalog : IDisposable
{
    public sealed record Entry(string Category, string Name, string Path);

    private static readonly Regex AvfxReferenceRegex = new(@"\x00([a-zA-Z0-9/_]*?)\.avfx", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] CategoryOrder =
    [
        "Item", "Accessory", "Status", "Action", "Non-Player Action", "Emote", "Zone",
        "Gimmick", "Furniture", "NPC", "NPC ID", "Mount", "Cutscene", "Journal Cutscene", "Common",
    ];

    // Curated named entries from VFXEditor's Common tab; the VFX Excel sheet is also loaded below.
    private static readonly (string Name, string Path)[] CuratedCommonAssets =
    [
        ("Passage of Arms", "vfx/action/ab_swd_abl020/eff/abi_swd020c1t.avfx"),
        ("Flamethrower", "vfx/action/ab_2gn026/eff/ab_2gn026c0c.avfx"),
        ("Meditate", "vfx/action/ab_2kt008/eff/ab_2kt008c1t.avfx"),
        ("Ley Lines", "vfx/common/eff/abi_thm_abl012m.avfx"),
        ("Salted Earth", "vfx/common/eff/2sw_dd_field01s.avfx"),
        ("Doton", "vfx/common/eff/mgc_dg05d1d0t.avfx"),
        ("Ten Chi Jin", "vfx/common/eff/ab_dgr025c1t.avfx"),
        ("Anatman", "vfx/action/ab_clw_abl026/eff/ab_clw026c0t.avfx"),
        ("Earthly Star", "chara/monster/m7103/obj/body/b0001/vfx/eff/vm0001.avfx"),
        ("Stellar Detonation", "vfx/monster/m7103/eff/m7103sp_01c0h.avfx"),
        ("Collective Unconscious", "vfx/common/eff/abi_bari1c0h.avfx"),
        ("Asylum", "vfx/common/eff/abi_cnj022g.avfx"),
        ("Sacred Soil", "vfx/common/eff/abi_swl053g.avfx"),
        ("Earthen Fury", "vfx/common/eff/m7004sp_05d0t.avfx"),
        ("Slipstream", "vfx/common/eff/m7005sp_32d0t.avfx"),
        ("Improvisation", "vfx/common/eff/ab_chk012c0c.avfx"),
        ("Starry Muse", "vfx/common/eff/obj_rpm02t.avfx"),
        ("Chelonian Gate", "vfx/aoz/mgc_rod084/eff/mgc_rod084c0d1.avfx"),
        ("Cold Fog", "vfx/common/eff/mgc_rod077_c0v.avfx"),
    ];

    private readonly IDataManager _dataManager;
    private readonly IPluginLog _log;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private List<Entry> _entries = new();
    private string[] _categories = [];
    private Task? _loadTask;
    private bool _isLoading;
    private bool _isLoaded;
    private bool _disposed;
    private string _status = "Not loaded.";

    public VfxAssetCatalog(IDataManager dataManager, IPluginLog log)
    {
        _dataManager = dataManager;
        _log = log;
    }

    public bool IsLoading { get { lock (_gate) return _isLoading; } }
    public bool IsLoaded { get { lock (_gate) return _isLoaded; } }
    public string Status { get { lock (_gate) return _status; } }
    public IReadOnlyList<Entry> Entries { get { lock (_gate) return _entries; } }
    public IReadOnlyList<string> Categories { get { lock (_gate) return _categories; } }

    public void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_disposed || _isLoaded || _loadTask != null)
                return;

            _isLoading = true;
            _status = "Loading VFX paths from game data...";
            var cancellationToken = _shutdown.Token;
            _loadTask = Task.Run(() => Load(cancellationToken), cancellationToken);
        }
    }

    private void Load(CancellationToken cancellationToken)
    {
        var entries = new List<Entry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileReferenceCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var imcVfxCache = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            LoadCommon(entries, seen, cancellationToken);
            LoadStatuses(entries, seen, cancellationToken);
            LoadActions(entries, seen, fileReferenceCache, cancellationToken);
            LoadEmotes(entries, seen, fileReferenceCache, cancellationToken);
            LoadZones(entries, seen, fileReferenceCache, cancellationToken);
            LoadGimmicks(entries, seen, fileReferenceCache, cancellationToken);
            LoadFurniture(entries, seen, fileReferenceCache, cancellationToken);
            LoadItems(entries, seen, imcVfxCache, cancellationToken);
            LoadNpcModels(entries, seen, imcVfxCache, cancellationToken);
            LoadNpcIds(entries, seen, imcVfxCache, cancellationToken);
            LoadMounts(entries, seen, imcVfxCache, cancellationToken);
            LoadCutscenes(entries, seen, fileReferenceCache, cancellationToken);
            LoadJournalCutscenes(entries, seen, fileReferenceCache, cancellationToken);

            entries.Sort(static (left, right) =>
            {
                static int Order(string category)
                {
                    int index = Array.IndexOf(CategoryOrder, category);
                    return index < 0 ? int.MaxValue : index;
                }

                int category = Order(left.Category).CompareTo(Order(right.Category));
                if (category != 0) return category;
                int name = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
                return name != 0 ? name : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
            });

            lock (_gate)
            {
                if (_disposed)
                    return;

                _entries = entries;
                _categories = CategoryOrder.Where(category => entries.Any(entry => entry.Category == category)).ToArray();
                _isLoaded = true;
                _isLoading = false;
                _loadTask = null;
                _status = BuildStatus(entries);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _isLoading = false;
                _loadTask = null;
                _status = "Catalogue loading cancelled.";
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "FPSBooster: VFX asset catalogue loading failed.");
            lock (_gate)
            {
                _isLoading = false;
                _loadTask = null;
                _status = "Catalogue load failed: " + ex.Message;
            }
        }
    }

    private void LoadCommon(List<Entry> entries, HashSet<string> seen, CancellationToken token)
    {
        foreach (var row in _dataManager.GetExcelSheet<VFX>())
        {
            token.ThrowIfCancellationRequested();
            string location = row.Location.ExtractText();
            Add(entries, seen, "Common", location, $"vfx/common/eff/{location}.avfx");
        }

        foreach (var asset in CuratedCommonAssets)
        {
            token.ThrowIfCancellationRequested();
            Add(entries, seen, "Common", asset.Name, asset.Path);
        }
    }

    private void LoadStatuses(List<Entry> entries, HashSet<string> seen, CancellationToken token)
    {
        foreach (var row in _dataManager.GetExcelSheet<Status>())
        {
            token.ThrowIfCancellationRequested();
            string name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            string hit = row.HitEffect.ValueNullable?.Location.ValueNullable?.Location.ExtractText() ?? string.Empty;
            Add(entries, seen, "Status", name + " · Hit", ToCommonPath(hit));

            var loops = row.VFX.ValueNullable?.VFX;
            if (loops == null)
                continue;

            int loopIndex = 0;
            foreach (var loop in loops)
            {
                token.ThrowIfCancellationRequested();
                string location = loop.ValueNullable?.Location.ExtractText() ?? string.Empty;
                Add(entries, seen, "Status", name + $" · Loop {loopIndex + 1}", ToCommonPath(location));
                loopIndex++;
            }
        }
    }

    private void LoadActions(List<Entry> entries, HashSet<string> seen, Dictionary<string, List<string>> fileCache, CancellationToken token)
    {
        foreach (var row in _dataManager.GetExcelSheet<GameAction>())
        {
            token.ThrowIfCancellationRequested();
            string name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            string category = row.IsPlayerAction || row.ClassJob.ValueNullable != null ? "Action" : "Non-Player Action";
            string start = row.AnimationStart.ValueNullable?.VFX.ValueNullable?.Location.ExtractText() ?? string.Empty;
            string cast = row.VFX.ValueNullable?.VFX.ValueNullable?.Location.ExtractText() ?? string.Empty;
            Add(entries, seen, category, name + " · Start", ToCommonPath(start));
            Add(entries, seen, category, name + " · Cast", ToCommonPath(cast));

            // VFXEditor exposes animation-start, animation-end, hit, and weapon timelines.
            string startKey = row.AnimationStart.ValueNullable?.Name.ValueNullable?.Key.ToString() ?? string.Empty;
            AddFileReferences(entries, seen, category, name + " · Start Timeline", ToTmbPath(startKey), fileCache, token);

            string timelineKey = row.AnimationEnd.ValueNullable?.Key.ToString() ?? string.Empty;
            AddFileReferences(entries, seen, category, name + " · End Timeline", ToTmbPath(timelineKey), fileCache, token);

            string hitKey = row.ActionTimelineHit.ValueNullable?.Key.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(hitKey) && !hitKey.Contains("normal_hit", StringComparison.OrdinalIgnoreCase))
                AddFileReferences(entries, seen, category, name + " · Target Timeline", ToTmbPath(hitKey), fileCache, token);

            string weaponKey = row.AnimationEnd.ValueNullable?.WeaponTimeline.ValueNullable?.File.ToString() ?? string.Empty;
            AddFileReferences(entries, seen, category, name + " · Weapon Timeline", ToTmbPath(weaponKey), fileCache, token);
        }
    }

    private void LoadEmotes(List<Entry> entries, HashSet<string> seen, Dictionary<string, List<string>> fileCache, CancellationToken token)
    {
        foreach (var row in _dataManager.GetExcelSheet<Emote>())
        {
            token.ThrowIfCancellationRequested();
            string name = row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            string command = row.TextCommand.ValueNullable?.Command.ToString() ?? string.Empty;
            string displayName = string.IsNullOrWhiteSpace(command) ? name : name + " (" + command + ")";
            foreach (var timeline in row.ActionTimeline)
            {
                token.ThrowIfCancellationRequested();
                string key = timeline.ValueNullable?.Key.ToString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(key) || !key.Contains("emote_sp", StringComparison.OrdinalIgnoreCase))
                    continue;

                string papPath = $"chara/human/c0101/animation/a0001/bt_common/{key}.pap";
                AddFileReferences(entries, seen, "Emote", displayName, papPath, fileCache, token);
            }
        }
    }

    private void LoadZones(List<Entry> entries, HashSet<string> seen, Dictionary<string, List<string>> fileCache, CancellationToken token)
    {
        foreach (var row in _dataManager.GetExcelSheet<TerritoryType>())
        {
            token.ThrowIfCancellationRequested();
            string background = row.Bg.ToString();
            if (string.IsNullOrWhiteSpace(background))
                continue;

            var parts = background.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                continue;
            parts[^1] = "vfx.lgb";

            string placeName = row.PlaceName.ValueNullable?.Name.ToString() ?? row.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(placeName))
                placeName = "Territory " + row.RowId;

            string lgbPath = "bg/" + string.Join('/', parts);
            AddFileReferences(entries, seen, "Zone", placeName, lgbPath, fileCache, token);
        }
    }

    private void LoadGimmicks(List<Entry> entries, HashSet<string> seen, Dictionary<string, List<string>> fileCache, CancellationToken token)
    {
        var territoryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var territory in _dataManager.GetExcelSheet<TerritoryType>())
        {
            string name = territory.Name.ToString();
            string place = territory.PlaceName.ValueNullable?.Name.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(place))
                territoryNames[name] = place;
        }

        foreach (var row in _dataManager.GetExcelSheet<ActionTimeline>())
        {
            token.ThrowIfCancellationRequested();
            string key = row.Key.ToString();
            if (!key.Contains("gimmick", StringComparison.OrdinalIgnoreCase))
                continue;

            string label = key.Replace("mon_sp/gimmick/", string.Empty, StringComparison.OrdinalIgnoreCase);
            string firstSegment = key.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            if (territoryNames.TryGetValue(firstSegment, out string? placeName))
                label = "(" + placeName + ") " + label;

            AddFileReferences(entries, seen, "Gimmick", label, ToTmbPath(key), fileCache, token);
        }
    }

    private void LoadFurniture(List<Entry> entries, HashSet<string> seen, Dictionary<string, List<string>> fileCache, CancellationToken token)
    {
        foreach (var row in _dataManager.GetExcelSheet<HousingFurniture>())
        {
            token.ThrowIfCancellationRequested();
            if (row.ModelKey <= 0)
                continue;

            string name = row.Item.Value.Name.ToString();
            string model = row.ModelKey.ToString("D4");
            string sgbPath = $"bgcommon/hou/indoor/general/{model}/asset/fun_b0_m{model}.sgb";
            AddFileReferences(entries, seen, "Furniture", name, sgbPath, fileCache, token);
        }

        foreach (var row in _dataManager.GetExcelSheet<HousingYardObject>())
        {
            token.ThrowIfCancellationRequested();
            if (row.ModelKey <= 0)
                continue;

            string name = row.Item.Value.Name.ToString();
            string model = row.ModelKey.ToString("D4");
            string sgbPath = $"bgcommon/hou/outdoor/general/{model}/asset/gar_b0_m{model}.sgb";
            AddFileReferences(entries, seen, "Furniture", name, sgbPath, fileCache, token);
        }
    }

    private void LoadItems(List<Entry> entries, HashSet<string> seen, Dictionary<string, List<int>> imcCache, CancellationToken token)
    {
        foreach (var item in _dataManager.GetExcelSheet<Item>())
        {
            token.ThrowIfCancellationRequested();
            string name = item.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var slot = item.EquipSlotCategory.ValueNullable;
            bool weapon = slot?.MainHand == 1 || slot?.OffHand == 1;
            bool armor = slot?.Head == 1 || slot?.Body == 1 || slot?.Gloves == 1 || slot?.Legs == 1 || slot?.Feet == 1;
            bool accessory = slot?.Neck == 1 || slot?.FingerR == 1 || slot?.FingerL == 1 || slot?.Wrists == 1 || slot?.Ears == 1;
            if (!weapon && !armor && !accessory)
                continue;

            var model = ReadModelIds(Convert.ToUInt64(item.ModelMain));
            if (model.Id <= 0)
                continue;

            string category = accessory ? "Accessory" : "Item";
            if (weapon)
            {
                if (model.Body <= 0 || model.Variant <= 0)
                    continue;
                string weaponId = "w" + model.Id.ToString("D4");
                string bodyId = "b" + model.Body.ToString("D4");
                string root = $"chara/weapon/{weaponId}/obj/body/{bodyId}/";
                string imcPath = root + bodyId + ".imc";
                AddImcVfx(entries, seen, category, name, imcPath, root + "vfx/eff/vw", model.Variant, imcCache);

                // VFXEditor exposes the secondary weapon model as a separate selectable item.
                var subModel = ReadModelIds(Convert.ToUInt64(item.ModelSub));
                if (subModel.Id > 0)
                {
                    uint itemUiCategory = item.ItemUICategory.ValueNullable?.RowId ?? 0;
                    bool sharesMainModel = itemUiCategory == 1 || itemUiCategory == 84 || itemUiCategory == 107 || itemUiCategory == 110;
                    int offhandId = sharesMainModel ? model.Id + 50 : subModel.Id;
                    int offhandBody = sharesMainModel ? model.Body : subModel.Body;
                    int offhandVariant = sharesMainModel ? model.Variant : subModel.Variant;
                    if (offhandId > 0 && offhandBody > 0 && offhandVariant > 0)
                    {
                        string offhandModelId = "w" + offhandId.ToString("D4");
                        string offhandBodyId = "b" + offhandBody.ToString("D4");
                        string offhandRoot = $"chara/weapon/{offhandModelId}/obj/body/{offhandBodyId}/";
                        string offhandImcPath = sharesMainModel ? imcPath : offhandRoot + offhandBodyId + ".imc";
                        AddImcVfx(entries, seen, category, name + " (Offhand)", offhandImcPath,
                            offhandRoot + "vfx/eff/vw", offhandVariant, imcCache);
                    }
                }
            }
            else
            {
                int variant = model.Body;
                if (variant <= 0)
                    continue;
                bool isAccessory = accessory;
                string modelId = (isAccessory ? "a" : "e") + model.Id.ToString("D4");
                string prefix = isAccessory ? "accessory" : "equipment";
                string root = $"chara/{prefix}/{modelId}/";
                string imcPath = root + modelId + ".imc";
                string vfxPrefix = root + "vfx/eff/" + (isAccessory ? "va" : "ve");
                AddImcVfx(entries, seen, category, name, imcPath, vfxPrefix, variant, imcCache);
            }
        }

        foreach (var glasses in _dataManager.GetExcelSheet<Glasses>())
        {
            token.ThrowIfCancellationRequested();
            string name = glasses.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var model = ReadModelIds(Convert.ToUInt64(glasses.Model));
            if (model.Id <= 0 || model.Body <= 0)
                continue;

            string modelId = "e" + model.Id.ToString("D4");
            string root = $"chara/equipment/{modelId}/";
            AddImcVfx(entries, seen, "Accessory", name, root + modelId + ".imc", root + "vfx/eff/ve", model.Body, imcCache);
        }
    }

    private void LoadNpcModels(List<Entry> entries, HashSet<string> seen, Dictionary<string, List<int>> imcCache, CancellationToken token)
    {
        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var modelChara in _dataManager.GetExcelSheet<ModelChara>())
        {
            token.ThrowIfCancellationRequested();
            if ((modelChara.Type != 2 && modelChara.Type != 3) || modelChara.Model <= 0 || modelChara.Base <= 0 || modelChara.Variant <= 0)
                continue;

            bool monster = modelChara.Type == 3;
            string modelId = (monster ? "m" : "d") + modelChara.Model.ToString("D4");
            string baseId = modelChara.Base.ToString("D4");
            string folder = monster ? "monster" : "demihuman";
            string part = monster ? "body" : "equipment";
            string partId = (monster ? "b" : "e") + baseId;
            string root = $"chara/{folder}/{modelId}/obj/{part}/{partId}/";
            string imcName = partId + ".imc";
            string imcPath = root + imcName;
            string dedupeKey = imcPath + "|" + modelChara.Variant;
            if (!loaded.Add(dedupeKey))
                continue;

            string label = (monster ? "Monster " : "Demihuman ") + modelId + " · " + partId;
            AddImcVfx(entries, seen, "NPC", label, imcPath,
                root + "vfx/eff/" + (monster ? "vm" : "ve"), modelChara.Variant, imcCache);
        }
    }

    private void LoadNpcIds(List<Entry> entries, HashSet<string> seen, Dictionary<string, List<int>> imcCache, CancellationToken token)
    {
        // BNpcBase adds the game-facing battle-NPC ID while ModelChara supplies the model/IMC path.
        foreach (var bnpc in _dataManager.GetExcelSheet<BNpcBase>())
        {
            token.ThrowIfCancellationRequested();
            var modelRef = bnpc.ModelChara.ValueNullable;
            if (modelRef == null)
                continue;

            var model = modelRef.Value;
            if ((model.Type != 2 && model.Type != 3) || model.Model <= 0 || model.Base <= 0 || model.Variant <= 0)
                continue;

            bool monster = model.Type == 3;
            string modelId = (monster ? "m" : "d") + model.Model.ToString("D4");
            string baseId = model.Base.ToString("D4");
            string folder = monster ? "monster" : "demihuman";
            string part = monster ? "body" : "equipment";
            string partId = (monster ? "b" : "e") + baseId;
            string root = $"chara/{folder}/{modelId}/obj/{part}/{partId}/";
            string imcPath = root + partId + ".imc";
            string name = $"BNpc #{bnpc.RowId} · {modelId}/{partId}";
            AddImcVfx(entries, seen, "NPC ID", name, imcPath,
                root + "vfx/eff/" + (monster ? "vm" : "ve"), model.Variant, imcCache);
        }
    }

    private void LoadMounts(List<Entry> entries, HashSet<string> seen, Dictionary<string, List<int>> imcCache, CancellationToken token)
    {
        foreach (var mount in _dataManager.GetExcelSheet<Mount>())
        {
            token.ThrowIfCancellationRequested();
            string name = mount.Singular.ExtractText();
            var modelRef = mount.ModelChara.ValueNullable;
            if (string.IsNullOrWhiteSpace(name) || modelRef == null)
                continue;

            var model = modelRef.Value;
            if ((model.Type != 2 && model.Type != 3) || model.Model <= 0 || model.Base <= 0 || model.Variant <= 0)
                continue;

            bool monster = model.Type == 3;
            string modelId = (monster ? "m" : "d") + model.Model.ToString("D4");
            string baseId = model.Base.ToString("D4");
            string folder = monster ? "monster" : "demihuman";
            string part = monster ? "body" : "equipment";
            string partId = (monster ? "b" : "e") + baseId;
            string root = $"chara/{folder}/{modelId}/obj/{part}/{partId}/";
            string imcPath = root + partId + ".imc";
            AddImcVfx(entries, seen, "Mount", name, imcPath,
                root + "vfx/eff/" + (monster ? "vm" : "ve"), model.Variant, imcCache);
        }
    }

    private void LoadCutscenes(List<Entry> entries, HashSet<string> seen, Dictionary<string, List<string>> fileCache, CancellationToken token)
    {
        foreach (var row in _dataManager.GetExcelSheet<Cutscene>())
        {
            token.ThrowIfCancellationRequested();
            string rawPath = row.Path.ToString();
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;
            string[] segments = rawPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string name = segments.Length > 1 ? segments[0] + "/" + segments[^1] : rawPath;
            AddFileReferences(entries, seen, "Cutscene", name, "cut/" + rawPath + ".cutb", fileCache, token);
        }
    }

    private void LoadJournalCutscenes(List<Entry> entries, HashSet<string> seen, Dictionary<string, List<string>> fileCache, CancellationToken token)
    {
        foreach (var journal in _dataManager.GetExcelSheet<CompleteJournal>())
        {
            token.ThrowIfCancellationRequested();
            string name = journal.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            int index = 0;
            foreach (var cutscene in journal.Cutscene)
            {
                token.ThrowIfCancellationRequested();
                string path = cutscene.ValueNullable?.Path.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(path))
                    AddFileReferences(entries, seen, "Journal Cutscene", name + $" · Scene {index + 1}", "cut/" + path + ".cutb", fileCache, token);
                index++;
            }
        }
    }

    private void AddImcVfx(
        List<Entry> entries,
        HashSet<string> seen,
        string category,
        string name,
        string imcPath,
        string vfxPrefix,
        int variant,
        Dictionary<string, List<int>> cache)
    {
        if (variant <= 0)
            return;

        string cacheKey = imcPath + "|" + variant;
        if (!cache.TryGetValue(cacheKey, out var ids))
        {
            ids = new List<int>();
            try
            {
                if (_dataManager.FileExists(imcPath))
                {
                    var imc = _dataManager.GetFile<ImcFile>(imcPath);
                    if (imc != null)
                    {
                        int variantIndex = variant - 1;
                        foreach (var part in imc.GetParts())
                        {
                            if (part.Variants == null || variantIndex < 0 || variantIndex >= part.Variants.Length)
                                continue;
                            int id = part.Variants[variantIndex].VfxId;
                            if (id > 0 && !ids.Contains(id))
                                ids.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Verbose(ex, $"FPSBooster: skipped IMC VFX catalogue source {imcPath}.");
            }
            cache[cacheKey] = ids;
        }

        foreach (int id in ids)
            Add(entries, seen, category, name + $" · VFX {id:D4}", vfxPrefix + id.ToString("D4") + ".avfx");
    }

    private void AddFileReferences(
        List<Entry> entries,
        HashSet<string> seen,
        string category,
        string sourceName,
        string sourcePath,
        Dictionary<string, List<string>> cache,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return;

        if (!cache.TryGetValue(sourcePath, out var paths))
        {
            paths = new List<string>();
            try
            {
                token.ThrowIfCancellationRequested();
                if (_dataManager.FileExists(sourcePath))
                {
                    var resource = _dataManager.GetFile(sourcePath);
                    if (resource?.Data is { Length: > 0 } data)
                    {
                        string raw = Encoding.Latin1.GetString(data);
                        foreach (Match match in AvfxReferenceRegex.Matches(raw))
                        {
                            string path = match.Groups[1].Value + ".avfx";
                            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase) && _dataManager.FileExists(path))
                                paths.Add(path);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Verbose(ex, $"FPSBooster: skipped VFX references in {sourcePath}.");
            }
            cache[sourcePath] = paths;
        }

        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            string leaf = Path.GetFileNameWithoutExtension(path);
            string name = string.IsNullOrWhiteSpace(sourceName) ? leaf : sourceName + " · " + leaf;
            Add(entries, seen, category, name, path);
        }
    }

    private bool Add(List<Entry> entries, HashSet<string> seen, string category, string name, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name) || !path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            string key = $"{category}\0{name}\0{path}";
            if (!_dataManager.FileExists(path) || !seen.Add(key))
                return false;
        }
        catch
        {
            return false;
        }

        entries.Add(new Entry(category, name, path));
        return true;
    }

    private static (int Id, int Body, int Variant) ReadModelIds(ulong raw)
    {
        byte[] bytes = BitConverter.GetBytes(raw);
        return (BitConverter.ToInt16(bytes, 0), BitConverter.ToInt16(bytes, 2), BitConverter.ToInt16(bytes, 4));
    }

    private static string ToCommonPath(string location) =>
        string.IsNullOrWhiteSpace(location) ? string.Empty : $"vfx/common/eff/{location}.avfx";

    private static string ToTmbPath(string key) =>
        string.IsNullOrWhiteSpace(key) || key.Contains("[SKL_ID]", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $"chara/action/{key}.tmb";

    private static string BuildStatus(List<Entry> entries)
    {
        var counts = entries.GroupBy(entry => entry.Category)
            .OrderBy(group => Array.IndexOf(CategoryOrder, group.Key))
            .Select(group => $"{group.Key}: {group.Count():N0}");
        return $"Loaded {entries.Count:N0} AVFX paths · " + string.Join(" · ", counts) + ".";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
