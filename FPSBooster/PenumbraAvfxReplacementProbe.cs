using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Data;
using Penumbra.Api.Enums;
using Penumbra.Api.IpcSubscribers;

namespace FPSBooster;

/// <summary>
/// Creates modified loose AVFX files and exposes them through individually tagged Penumbra
/// temporary mods in the local player's current collection. Multiple distinct game paths can
/// remain redirected simultaneously. No installed mod or game file is modified; each mapping
/// is spawn-ready only after ResolvePlayerPath confirms its exact generated file.
/// </summary>
public sealed class PenumbraAvfxReplacementProbe : IDisposable
{
    private const string TemporaryModTagPrefix = "FPSBooster.NativeAvfxCameraBinder";
    private const int TemporaryModPriority = 0;

    private sealed class ActiveReplacement
    {
        public required string Tag { get; init; }
        public required Guid CollectionId { get; init; }
        public required string CollectionName { get; init; }
        public required string GamePath { get; init; }
        public required string FilePath { get; init; }
        public required string ChangeDescription { get; init; }
        public bool RedirectVerified { get; set; }
    }
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IDataManager _dataManager;
    private readonly IObjectTable _objectTable;
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly ApiVersion _apiVersion;
    private readonly GetCollectionForObject _getCollectionForObject;
    private readonly ResolvePlayerPath _resolvePlayerPath;
    private readonly AddTemporaryMod _addTemporaryMod;
    private readonly RemoveTemporaryMod _removeTemporaryMod;

    private readonly Dictionary<string, ActiveReplacement> _replacements = new(StringComparer.OrdinalIgnoreCase);
    private ActiveReplacement? _lastReplacement;
    private bool _disposed;

    public string Status { get; private set; } = "No temporary AVFX replacement is active.";
    public string Report { get; private set; } = "No temporary AVFX replacement has been created.";
    public bool IsRegistered => _replacements.Count > 0;
    public bool IsRedirectVerified => _lastReplacement?.RedirectVerified == true;
    public int ActiveReplacementCount => _replacements.Count;
    public string ActiveGamePath => _lastReplacement?.GamePath ?? string.Empty;
    public string ReplacementFilePath => _lastReplacement?.FilePath ?? string.Empty;

    public void ReportNotice(string message)
    {
        Status = message;
        Report = "Penumbra temporary AVFX replacement report\n" + message;
    }

    public PenumbraAvfxReplacementProbe(
        IDalamudPluginInterface pluginInterface,
        IDataManager dataManager,
        IObjectTable objectTable,
        IFramework framework,
        IPluginLog log)
    {
        _pluginInterface = pluginInterface;
        _dataManager = dataManager;
        _objectTable = objectTable;
        _framework = framework;
        _log = log;
        _apiVersion = new ApiVersion(pluginInterface);
        _getCollectionForObject = new GetCollectionForObject(pluginInterface);
        _resolvePlayerPath = new ResolvePlayerPath(pluginInterface);
        _addTemporaryMod = new AddTemporaryMod(pluginInterface);
        _removeTemporaryMod = new RemoveTemporaryMod(pluginInterface);
    }

    public bool CanSpawnFor(string? gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
            return false;
        return _replacements.TryGetValue(NormalizeGamePath(gamePath), out var replacement)
            && replacement.RedirectVerified;
    }

    public bool PrepareAppendCameraBinder(string targetGamePath, string templateGamePath, int templateBinderIndex)
    {
        if (!TryReadVanillaAvfx(targetGamePath, out string targetNormalized, out byte[] target, out string error))
            return Fail("Target: " + error);
        if (!TryReadVanillaAvfx(templateGamePath, out string templateNormalized, out byte[] template, out error))
            return Fail("Template: " + error);

        if (!AvfxBinderInspector.TryAppendCameraBinderFromTemplate(target, template, templateBinderIndex,
                out byte[] rewritten, out int previousCount, out string rewriteDetail))
            return Fail("Could not append the template Camera binder: " + rewriteDetail);

        return RegisterReplacement(targetNormalized, rewritten,
            $"Copied Camera binder #{templateBinderIndex} from {templateNormalized} into {targetNormalized} (binder count {previousCount} → {previousCount + 1}). {rewriteDetail}");
    }

    /// <summary>Re-reads the generated loose AVFX and verifies that every timeline references the appended Camera binder.</summary>
    public bool ValidatePreparedCameraBinder(out string detail)
        => ValidatePreparedCameraBinder(_lastReplacement?.GamePath, out detail);

    public bool ValidatePreparedCameraBinder(string? gamePath, out string detail)
    {
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(gamePath)
            || !_replacements.TryGetValue(NormalizeGamePath(gamePath), out var replacement)
            || !replacement.RedirectVerified
            || string.IsNullOrWhiteSpace(replacement.FilePath))
        {
            detail = "No active, verified Penumbra AVFX replacement exists for the selected game path.";
            return false;
        }

        try
        {
            if (!File.Exists(replacement.FilePath))
            {
                detail = "The generated AVFX replacement file no longer exists.";
                return false;
            }

            var inspection = AvfxBinderInspector.Inspect(File.ReadAllBytes(replacement.FilePath));
            if (!inspection.Success)
            {
                detail = "Generated AVFX inspection failed: " + inspection.Error;
                return false;
            }

            int expectedBinderIndex = inspection.BinderCount - 1;
            bool allTimelinesLinked = inspection.Timelines.Count > 0
                && inspection.Timelines.All(x => x.BinderIndex == expectedBinderIndex);
            if (inspection.CameraBinderCount < 1 || !inspection.BinderCountMatchesDeclaration || !allTimelinesLinked)
            {
                detail = $"Generated AVFX failed Camera-binder validation (Camera={inspection.CameraBinderCount}, binders={inspection.BinderCount}, declared={inspection.DeclaredBinderCount?.ToString() ?? "missing"}, timelines={inspection.Timelines.Count}, all linked to binder #{expectedBinderIndex}={allTimelinesLinked}).";
                return false;
            }

            detail = $"Generated replacement validated · {inspection.BinderCount} binder(s), {inspection.CameraBinderCount} Camera binder(s), BdCn matches, all {inspection.Timelines.Count} timeline(s) reference the appended binder through TmLn.BnNo.";
            return true;
        }
        catch (Exception ex)
        {
            detail = "Could not inspect generated AVFX replacement: " + ex.Message;
            _log.Warning(ex, "FPSBooster: generated AVFX validation failed.");
            return false;
        }
    }

    public bool RecheckResolution()
    {
        if (_replacements.Count == 0)
            return Fail("No temporary Penumbra replacements are active to re-check.");

        bool allVerified = true;
        foreach (string path in _replacements.Keys.ToArray())
            allVerified &= RecheckResolution(path);

        Status = allVerified
            ? $"All {ActiveReplacementCount} temporary Penumbra redirects were re-verified."
            : $"One or more temporary Penumbra redirects failed verification. See the report for individual mappings.";
        BuildReport(_lastReplacement, string.Empty, "Re-checked all active Penumbra redirects.");
        return allVerified;
    }

    public bool RecheckResolution(string gamePath)
    {
        string normalized = NormalizeGamePath(gamePath);
        if (!_replacements.TryGetValue(normalized, out var replacement))
            return Fail($"No temporary replacement is registered for {normalized}.");

        try
        {
            if (!TryGetCurrentPlayerCollection(out Guid currentCollection, out _, out string collectionError))
            {
                replacement.RedirectVerified = false;
                Status = "Could not verify the local player's Penumbra collection: " + collectionError;
                BuildReport(replacement, string.Empty, Status);
                return false;
            }

            if (currentCollection != replacement.CollectionId)
            {
                replacement.RedirectVerified = false;
                Status = "The local player's active Penumbra collection changed after the replacement was registered. Remove the old temporary replacement(s) and prepare again for the current collection.";
                BuildReport(replacement, string.Empty, Status);
                return false;
            }

            string resolved = InvokeOnFrameworkThread(() => _resolvePlayerPath.Invoke(replacement.GamePath) ?? string.Empty);
            replacement.RedirectVerified = PathsEqual(resolved, replacement.FilePath);
            _lastReplacement = replacement;
            Status = replacement.RedirectVerified
                ? $"Penumbra redirect verified for {replacement.GamePath}. {ActiveReplacementCount} replacement(s) are currently retained."
                : $"Temporary mod is registered, but ResolvePlayerPath did not return the generated file for {replacement.GamePath}. Returned: {(string.IsNullOrWhiteSpace(resolved) ? "(empty)" : resolved)}. Native spawn is blocked until this verifies.";
            BuildReport(replacement, resolved, "Rechecked Penumbra.ResolvePlayerPath.");
            return replacement.RedirectVerified;
        }
        catch (Exception ex)
        {
            replacement.RedirectVerified = false;
            Status = "Could not verify the Penumbra redirect: " + ex.Message;
            BuildReport(replacement, string.Empty, Status);
            _log.Warning(ex, "FPSBooster: Penumbra AVFX replacement verification failed.");
            return false;
        }
    }

    public bool RemoveTemporaryReplacement()
    {
        if (_replacements.Count == 0)
        {
            Status = "No temporary Penumbra replacements are active.";
            return true;
        }

        int removed = 0;
        int retained = 0;
        foreach (var pair in _replacements.ToArray())
        {
            var replacement = pair.Value;
            try
            {
                PenumbraApiEc result = InvokeOnFrameworkThread(
                    () => _removeTemporaryMod.Invoke(replacement.Tag, replacement.CollectionId, TemporaryModPriority));
                if (result is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged or PenumbraApiEc.CollectionMissing))
                {
                    retained++;
                    Status = $"Penumbra.RemoveTemporaryMod returned {result} for {replacement.GamePath}. Its generated file was retained safely.";
                    continue;
                }

                _replacements.Remove(pair.Key);
                removed++;
                TryDelete(replacement.FilePath);
            }
            catch (Exception ex)
            {
                retained++;
                Status = $"Could not remove temporary replacement for {replacement.GamePath}: {ex.Message}. Its generated file was retained safely.";
                _log.Warning(ex, "FPSBooster: Penumbra temporary AVFX replacement removal failed.");
            }
        }

        _lastReplacement = _replacements.Values.LastOrDefault();
        Status = retained == 0
            ? $"Removed {removed} temporary Penumbra replacement(s) and their generated files."
            : $"Removed {removed} temporary replacement(s); {retained} could not be removed and were retained safely.";
        BuildReport(_lastReplacement, string.Empty, "Removed all tagged temporary AVFX replacements.");
        return retained == 0;
    }

    public bool RemoveTemporaryReplacementsExcept(string gamePathToKeep)
    {
        string keep = NormalizeGamePath(gamePathToKeep);
        bool success = true;
        foreach (string path in _replacements.Keys.Where(x => !string.Equals(x, keep, StringComparison.OrdinalIgnoreCase)).ToArray())
            success &= RemoveTemporaryReplacement(path);
        if (success)
            Status = $"Retained the replacement for {keep}; {ActiveReplacementCount} temporary replacement(s) remain.";
        return success;
    }

    public bool RemoveTemporaryReplacement(string gamePath)
    {
        string normalized = NormalizeGamePath(gamePath);
        if (!_replacements.TryGetValue(normalized, out var replacement))
            return true;

        try
        {
            PenumbraApiEc result = InvokeOnFrameworkThread(
                () => _removeTemporaryMod.Invoke(replacement.Tag, replacement.CollectionId, TemporaryModPriority));
            if (result is not (PenumbraApiEc.Success or PenumbraApiEc.NothingChanged or PenumbraApiEc.CollectionMissing))
            {
                Status = $"Penumbra.RemoveTemporaryMod returned {result} for {normalized}. The generated file was retained.";
                BuildReport(replacement, string.Empty, Status);
                return false;
            }

            _replacements.Remove(normalized);
            TryDelete(replacement.FilePath);
            _lastReplacement = _replacements.Values.LastOrDefault();
            Status = $"Removed temporary Penumbra replacement for {normalized}. {ActiveReplacementCount} replacement(s) remain.";
            BuildReport(replacement, string.Empty, Status);
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Penumbra temporary replacement removal failed for {normalized}: {ex.Message}. The generated file was retained.";
            BuildReport(replacement, string.Empty, Status);
            _log.Warning(ex, "FPSBooster: Penumbra temporary AVFX replacement removal failed.");
            return false;
        }
    }

    private bool RegisterReplacement(string gamePath, byte[] bytes, string changeDescription)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_replacements.ContainsKey(gamePath))
            return Fail($"A temporary AVFX replacement is already active for {gamePath}. Remove all temporary Penumbra replacements before rebuilding this path; if it has already been spawned, restart FFXIV first so the native resource cache cannot reuse earlier AVFX bytes.");

        var player = _objectTable.LocalPlayer;
        if (player == null || player.Address == 0)
            return Fail("The local player is unavailable; cannot determine the active Penumbra collection.");

        string outputPath = string.Empty;
        Guid collectionId;
        string collectionName;
        string temporaryTag = $"{TemporaryModTagPrefix}.{Guid.NewGuid():N}";
        try
        {
            if (!TryGetCurrentPlayerCollection(out collectionId, out collectionName, out string collectionError))
                return Fail("Cannot determine the local player's active Penumbra collection: " + collectionError);

            var existing = _replacements.Values.FirstOrDefault();
            if (existing != null && existing.CollectionId != collectionId)
                return Fail("The local player's Penumbra collection changed while temporary replacements are active. Remove all temporary Penumbra replacements before adding a replacement in the new collection.");

            string directory = Path.Combine(_pluginInterface.GetPluginConfigDirectory(), "VfxReplacements");
            Directory.CreateDirectory(directory);
            outputPath = Path.Combine(directory, $"camera_binder_{Guid.NewGuid():N}.avfx");
            File.WriteAllBytes(outputPath, bytes);

            PenumbraApiEc result = InvokeOnFrameworkThread(() => _addTemporaryMod.Invoke(
                temporaryTag,
                collectionId,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [gamePath] = outputPath },
                string.Empty,
                TemporaryModPriority));

            if (result != PenumbraApiEc.Success)
            {
                TryDelete(outputPath);
                return Fail($"Penumbra.AddTemporaryMod returned {result}; the temporary replacement was not registered.");
            }
        }
        catch (Exception ex)
        {
            TryDelete(outputPath);
            _log.Warning(ex, "FPSBooster: Penumbra temporary AVFX replacement registration failed.");
            return Fail("Could not register the temporary Penumbra replacement. Confirm Penumbra is installed, enabled, and exposes its IPC API: " + ex.Message);
        }

        var entry = new ActiveReplacement
        {
            Tag = temporaryTag,
            CollectionId = collectionId,
            CollectionName = collectionName,
            GamePath = gamePath,
            FilePath = outputPath,
            ChangeDescription = changeDescription,
        };
        _replacements.Add(gamePath, entry);
        _lastReplacement = entry;
        Status = $"Registered temporary Camera-binder replacement for {gamePath}. {ActiveReplacementCount} replacement(s) are now active.";

        bool verified = RecheckResolution(gamePath);
        if (!verified)
        {
            Status += " The temporary mod remains registered so you can inspect/retry verification or remove it.";
            return false;
        }

        return true;
    }

    private bool TryGetCurrentPlayerCollection(out Guid collectionId, out string collectionName, out string error)
    {
        collectionId = Guid.Empty;
        collectionName = string.Empty;
        error = string.Empty;

        try
        {
            // Penumbra uses object-table indices, not native object pointers. Index 0 is the local player.
            var result = InvokeOnFrameworkThread(() => _getCollectionForObject.Invoke(0));
            if (!result.ObjectValid)
            {
                error = "Penumbra reports that local-player object index 0 is not valid.";
                return false;
            }

            // Penumbra.Api exposes EffectiveCollection as a non-nullable named tuple
            // (Guid Id, string Name). ObjectValid is the validity flag; the tuple itself
            // cannot be null/pattern-matched. A Guid.Empty ID is the defensive no-collection check.
            var effectiveCollection = result.EffectiveCollection;
            if (effectiveCollection.Id == Guid.Empty)
            {
                error = "Penumbra returned an empty effective-collection ID for the local player.";
                return false;
            }

            collectionId = effectiveCollection.Id;
            collectionName = effectiveCollection.Name;
            return true;
        }
        catch (Exception ex)
        {
            error = "GetCollectionForObject failed: " + ex.Message;
            return false;
        }
    }

    private T InvokeOnFrameworkThread<T>(Func<T> action)
    {
        // Penumbra expects collection/temporary-mod mutations on the framework thread.
        // UI callbacks are normally already there; avoid an unnecessary queued round-trip.
        if (_framework.IsInFrameworkUpdateThread)
            return action();

        return _framework.RunOnFrameworkThread(action).GetAwaiter().GetResult();
    }

    private bool TryReadVanillaAvfx(string path, out string normalized, out byte[] data, out string error)
    {
        normalized = NormalizeGamePath(path);
        data = Array.Empty<byte>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Contains('\\') || normalized.Contains(':')
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized.Contains('\0')
            || !normalized.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase))
        {
            error = "Use a relative vanilla .avfx game path with forward slashes; absolute paths and traversal are rejected.";
            return false;
        }

        try
        {
            if (!_dataManager.FileExists(normalized))
            {
                error = $"'{normalized}' is not in the vanilla game file index. Both the target and template must be vanilla indexed AVFX files.";
                return false;
            }

            var file = _dataManager.GetFile(normalized);
            if (file == null)
            {
                error = $"Lumina could not read '{normalized}'.";
                return false;
            }
            data = file.Data;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not read '{normalized}' from the vanilla game index: {ex.Message}";
            return false;
        }
    }

    private (int Major, int Minor) GetPenumbraApiVersion()
    {
        try
        {
            var version = _apiVersion.Invoke();
            return (version.Item1, version.Item2);
        }
        catch
        {
            return (0, 0);
        }
    }

    private void BuildReport(ActiveReplacement? replacement, string resolvedPath, string action)
    {
        (int apiMajor, int apiMinor) = GetPenumbraApiVersion();
        var sb = new StringBuilder();
        sb.AppendLine("Penumbra temporary AVFX replacement report");
        sb.AppendLine($"Penumbra API: {apiMajor}.{apiMinor}");
        sb.AppendLine($"Action: {action}");
        sb.AppendLine($"Active replacements: {_replacements.Count}");
        if (replacement != null)
        {
            sb.AppendLine($"Game resource path: {replacement.GamePath}");
            sb.AppendLine($"Temporary file: {replacement.FilePath}");
            sb.AppendLine($"Penumbra collection: {replacement.CollectionName} ({replacement.CollectionId})");
            sb.AppendLine($"Temporary mod tag: {replacement.Tag} · priority {TemporaryModPriority}");
            sb.AppendLine($"Registered: {_replacements.ContainsKey(replacement.GamePath)}");
            sb.AppendLine($"ResolvePlayerPath verified: {replacement.RedirectVerified}");
            sb.AppendLine($"Resolved path: {(string.IsNullOrWhiteSpace(resolvedPath) ? "(not available)" : resolvedPath)}");
            sb.AppendLine($"Modification: {replacement.ChangeDescription}");
        }
        if (_replacements.Count > 0)
        {
            sb.AppendLine("All active mappings:");
            foreach (var item in _replacements.Values.OrderBy(x => x.GamePath, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"- {item.GamePath} -> {item.FilePath} · verified={item.RedirectVerified} · tag={item.Tag}");
        }
        sb.AppendLine("Scope: local player's Penumbra collection only. No installed mod or game file was modified.");
        Report = sb.ToString();
    }

    private bool Fail(string message)
    {
        Status = message;
        Report = "Penumbra temporary AVFX replacement report\n" + message;
        return false;
    }

    private static string NormalizeGamePath(string path) => (path ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');

    private static bool PathsEqual(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "FPSBooster: could not delete an unregistered temporary AVFX file.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (IsRegistered)
            RemoveTemporaryReplacement();
    }
}
