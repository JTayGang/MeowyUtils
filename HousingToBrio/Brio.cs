using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MessagePack;

namespace HousingToBrio;

// Mirrors Brio's own scene DTOs (GPL-3.0 format, not code) so MessagePack
// output is byte-compatible with what Brio reads:
// https://github.com/Etheirys/Brio (Core/Transform.cs, Services/Models/*DTO.cs, Services/SceneService.cs)

public enum BrioWorldObjectType
{
    BgObject = 0,
    StaticVfx = 1,
    Prop = 2,
    Furniture = 3,
}

[MessagePackObject(keyAsPropertyName: true)]
public struct BrioTransform
{
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 Scale { get; set; }
}

[MessagePackObject(keyAsPropertyName: true)]
public sealed class BrioWorldObjectDto
{
    public string FriendlyName { get; set; } = string.Empty;
    public BrioWorldObjectType ObjectType { get; set; }
    public string Path { get; set; } = string.Empty;

    // Only used by ObjectType.Prop; always null for Furniture.
    public object? PropModel { get; set; }

    public BrioTransform Transform { get; set; }
    public Vector3 RelativePosition { get; set; }
    public string? ParentFolderId { get; set; }

    public uint StainID { get; set; }
    public Vector4? Color { get; set; }
}

[MessagePackObject(keyAsPropertyName: true)]
public sealed class BrioSceneMetaDataDto
{
    public uint Map { get; set; }
    public ushort Territory { get; set; }
    public string? World { get; set; }
}

[MessagePackObject(keyAsPropertyName: true)]
public sealed class BrioSceneManifestDto
{
    public int Version { get; set; } = 1;
    public string? Author { get; set; }
    public string? Description { get; set; }
    public string? Base64Image { get; set; }
    public BrioSceneMetaDataDto? MetaData { get; set; }
}

/// <summary>
/// Builds Brio's .brioproj/.brioscn container format (GPL-3.0 format, not code):
/// https://github.com/Etheirys/Brio/blob/main/Brio/Services/SceneService.cs
///
/// Layout: "BRIOSCN" magic + Int32 version + Int32 chunk count, then per
/// chunk: Int32 type (Manifest=1, WorldObjects=6), Int32 version, Int32
/// length, payload bytes. Missing chunk types default to empty on read, so
/// only Manifest + WorldObjects need to be written here.
/// </summary>
public static class BrioSceneWriter
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BRIOSCN");
    private const int FormatVersion = 1;

    private enum ChunkType
    {
        Manifest = 1,
        WorldObjects = 6,
    }

    public static byte[] Build(BrioSceneManifestDto manifest, IReadOnlyList<BrioWorldObjectDto> worldObjects)
    {
        var chunks = new List<(ChunkType Type, byte[] Payload)>
        {
            (ChunkType.Manifest, MessagePackSerializer.Serialize(manifest)),
            (ChunkType.WorldObjects, MessagePackSerializer.Serialize(new List<BrioWorldObjectDto>(worldObjects))),
        };

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(chunks.Count);

        foreach (var (type, payload) in chunks)
        {
            writer.Write((int)type);
            writer.Write(1); // per-chunk version
            writer.Write(payload.Length);
            writer.Write(payload);
        }

        writer.Flush();
        return stream.ToArray();
    }
}

// Mirrors Brio's Project/BrioProjects registry (GPL-3.0 format, not code) so
// "brio.data" can be read-modified-written safely:
// https://github.com/Etheirys/Brio/blob/main/Brio/Game/Core/ProjectSystem.cs
// Uses numbered [Key(N)] (not keyAsPropertyName) to match Brio's own layout.

[MessagePackObject]
public sealed class BrioProjectEntry
{
    [Key(0)] public int Version { get; set; } = 2;
    [Key(1)] public string Name { get; set; } = string.Empty;
    [Key(2)] public string? Description { get; set; }
    [Key(3)] public string Path { get; set; } = string.Empty;
    [Key(4)] public string? ImagePath { get; set; }
    [Key(5)] public DateTime? Created { get; set; }
    [Key(6)] public DateTime? LastModified { get; set; }
}

[MessagePackObject]
public sealed class BrioProjectRegistry
{
    [Key(0)] public List<BrioProjectEntry> Projects { get; set; } = new();
}

/// <summary>
/// Brio's "Import Scene" picker is disabled until Brio 0.8.1, and "Load
/// Project" only lists Brio's own registry with no file browser - so this
/// writes a .brioproj into Brio's Data/Projects folder directly and appends
/// an entry to "brio.data". Backs up the registry first and aborts if it
/// can't be parsed with confidence.
/// </summary>
public static class BrioProjectInstaller
{
    private const string BrioInternalName = "Brio";

    public sealed class InstallResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public string? SavedScenePath { get; init; }
    }

    /// <summary>Guesses Brio's config folder as a sibling of this plugin's own (both under pluginConfigs). Null if not found.</summary>
    public static string? TryDetectBrioDataFolder(IDalamudPluginInterface pluginInterface)
    {
        var ownConfigDir = pluginInterface.GetPluginConfigDirectory();
        var parent = Directory.GetParent(ownConfigDir)?.FullName;
        if (parent is null)
            return null;

        var candidate = Path.Combine(parent, BrioInternalName);
        return Directory.Exists(candidate) ? candidate : null;
    }

    public static InstallResult InstallAsProject(
        string brioConfigDirectory,
        string projectName,
        string? description,
        BrioSceneManifestDto manifest,
        IReadOnlyList<BrioWorldObjectDto> worldObjects)
    {
        try
        {
            var projectsFolder = Path.Combine(brioConfigDirectory, "Data", "Projects");
            Directory.CreateDirectory(projectsFolder);

            var registryPath = Path.Combine(projectsFolder, "brio.data");

            BrioProjectRegistry registry;
            if (File.Exists(registryPath))
            {
                try
                {
                    var existingBytes = File.ReadAllBytes(registryPath);
                    registry = MessagePackSerializer.Deserialize<BrioProjectRegistry>(existingBytes);
                }
                catch (Exception ex)
                {
                    return new InstallResult
                    {
                        Success = false,
                        Message = $"Couldn't read Brio's registry: {ex.Message}",
                    };
                }

                // Never touch the real registry without a recovery copy first.
                var backupPath = registryPath + $".backup-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Copy(registryPath, backupPath, overwrite: true);
            }
            else
            {
                registry = new BrioProjectRegistry();
            }

            var safeName = SanitizeForFileName(projectName);
            var sceneFileName = $"{safeName}-{DateTime.Now:yyyy-MM-dd-hh-mm-ss}.brioproj";
            var scenePath = Path.Combine(projectsFolder, sceneFileName);

            var sceneBytes = BrioSceneWriter.Build(manifest, worldObjects);
            File.WriteAllBytes(scenePath, sceneBytes);

            registry.Projects.Add(new BrioProjectEntry
            {
                Version = 2,
                Name = projectName,
                Description = description,
                Path = scenePath,
                Created = DateTime.UtcNow,
            });

            var registryBytes = MessagePackSerializer.Serialize(registry);
            File.WriteAllBytes(registryPath, registryBytes);

            // Confirms our own write landed; can't detect Brio overwriting it
            // later from a stale in-memory copy (see class summary).
            try
            {
                var verifyBytes = File.ReadAllBytes(registryPath);
                var verifyRegistry = MessagePackSerializer.Deserialize<BrioProjectRegistry>(verifyBytes);
                var found = verifyRegistry.Projects.Exists(p => p.Path == scenePath);

                if (!found)
                {
                    return new InstallResult
                    {
                        Success = false,
                        Message = "Save verification failed - don't reload Brio.",
                    };
                }
            }
            catch (Exception ex)
            {
                return new InstallResult
                {
                    Success = false,
                    Message = $"Verification failed: {ex.Message}",
                };
            }

            return new InstallResult
            {
                Success = true,
                Message = $"Saved as \"{projectName}\".",
                SavedScenePath = scenePath,
            };
        }
        catch (Exception ex)
        {
            return new InstallResult
            {
                Success = false,
                Message = $"Save failed: {ex.Message}",
            };
        }
    }

    private static string SanitizeForFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }

        var result = new string(chars).Trim();
        return string.IsNullOrEmpty(result) ? "Layout" : result;
    }
}

/// <summary>
/// Cycles Brio via /xldisableplugin + /xlenableplugin so it re-reads its
/// registry. These commands queue rather than act instantly, so this polls
/// IExposedPlugin.IsLoaded once per frame via Tick() - always call from
/// MainWindow.Draw() (same thread Dalamud draws on), never a background thread.
/// </summary>
public sealed class BrioPluginControl
{
    private const string BrioInternalName = "Brio";
    private const double TimeoutSeconds = 10;

    public enum State
    {
        Idle,
        WaitingForDisable,
        WaitingForEnable,
        Succeeded,
        Failed,
    }

    public State CurrentState { get; private set; } = State.Idle;
    public string? Message { get; private set; }

    private DateTime _stepStarted;

    public bool IsRunning => CurrentState is State.WaitingForDisable or State.WaitingForEnable;

    public void Start(IDalamudPluginInterface pluginInterface, ICommandManager commandManager)
    {
        var brio = FindBrio(pluginInterface);
        if (brio is null)
        {
            CurrentState = State.Failed;
            Message = "Brio not found.";
            return;
        }

        if (!brio.IsLoaded)
        {
            CurrentState = State.Failed;
            Message = "Brio isn't enabled.";
            return;
        }

        CurrentState = State.WaitingForDisable;
        Message = "Disabling Brio...";
        _stepStarted = DateTime.UtcNow;
        commandManager.ProcessCommand($"/xldisableplugin {BrioInternalName}");
    }

    /// <summary>Call once per frame while IsRunning is true; a no-op otherwise.</summary>
    public void Tick(IDalamudPluginInterface pluginInterface, ICommandManager commandManager)
    {
        if (!IsRunning)
            return;

        var brio = FindBrio(pluginInterface);
        var elapsed = (DateTime.UtcNow - _stepStarted).TotalSeconds;

        if (CurrentState == State.WaitingForDisable)
        {
            if (brio is not null && !brio.IsLoaded)
            {
                CurrentState = State.WaitingForEnable;
                Message = "Re-enabling Brio...";
                _stepStarted = DateTime.UtcNow;
                commandManager.ProcessCommand($"/xlenableplugin {BrioInternalName}");
            }
            else if (elapsed > TimeoutSeconds)
            {
                CurrentState = State.Failed;
                Message = "Didn't disable in time - reload manually.";
            }
        }
        else if (CurrentState == State.WaitingForEnable)
        {
            if (brio is not null && brio.IsLoaded)
            {
                CurrentState = State.Succeeded;
                Message = "Reloaded - open Load Project.";
            }
            else if (elapsed > TimeoutSeconds)
            {
                CurrentState = State.Failed;
                Message = "Didn't re-enable in time - enable manually.";
            }
        }
    }

    public void Reset() => CurrentState = State.Idle;

    private static IExposedPlugin? FindBrio(IDalamudPluginInterface pluginInterface)
        => pluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == BrioInternalName);
}
