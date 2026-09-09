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

// ---------------------------------------------------------------------------
// The types below intentionally mirror the *shape* of Brio's own scene
// data-transfer objects, so that MessagePackSerializer.Serialize(...) produces
// output that is byte-compatible with what Brio itself reads. This plugin does
// not reference, link against, or embed any of Brio's code - it only targets
// Brio's own scene container format so the result can be written into Brio's
// project folder and loaded from Brio's own "Load Project" window.
//
// Shapes cross-referenced from Brio (GPL-3.0-licensed) source:
//   Brio/Core/Transform.cs
//   Brio/Services/Models/WorldObjectDTO.cs
//   Brio/Services/Models/SceneManifestDTO.cs
//   Brio/Services/SceneService.cs
//   https://github.com/Etheirys/Brio
// ---------------------------------------------------------------------------

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

    // Only relevant for ObjectType.Prop (weapon/held-item props) - always null
    // for the Furniture objects this plugin produces.
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
/// Builds Brio's own scene container format in memory (used to produce the
/// bytes written into a .brioproj file by BrioProjectInstaller). This is the
/// same chunked format Brio's own SceneService reads and writes (GPL-3.0):
///   https://github.com/Etheirys/Brio/blob/main/Brio/Services/SceneService.cs
///
/// Container layout (all integers little-endian, written via BinaryWriter):
///   7 bytes  "BRIOSCN" magic
///   Int32    format version (1)
///   Int32    chunk count
///   for each chunk:
///     Int32  chunk type   (Manifest = 1, WorldObjects = 6, ...)
///     Int32  chunk version
///     Int32  payload length in bytes
///     bytes  MessagePack-serialized payload
///
/// Brio treats every chunk type as optional on import (missing chunks just
/// default to empty), so we only need to emit the two chunks we actually
/// populate: Manifest and WorldObjects.
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

// ---------------------------------------------------------------------------
// Mirrors the *shape* of Brio's own Project / BrioProjects registry classes
// (GPL-3.0), purely so we can safely read-modify-write Brio's own "brio.data"
// project index file and have a generated scene show up in Brio's existing
// "Load Project" window. This plugin does not reference or embed Brio's code.
//
// Cross-referenced from:
//   https://github.com/Etheirys/Brio/blob/main/Brio/Game/Core/ProjectSystem.cs
//
// Unlike the WorldObjectDTO family (which uses keyAsPropertyName: true),
// Brio's Project/BrioProjects use classic numbered [Key(N)] members - this
// mirrors that exactly so the array layout lines up byte-for-byte.
// ---------------------------------------------------------------------------

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
/// Brio's own "Import Scene" file picker is currently disabled inside Brio
/// itself (its tooltip literally says "until 0.8.1"), so a standalone
/// .brioscn file can't be opened through Brio's UI yet. Brio's "Load Project"
/// window works today, but only lists entries from Brio's own project
/// registry (its own UI has no "browse for an existing file" option - project
/// files are only ever created via Brio's own "New Project", which always
/// captures the *live* GPose scene, never a file).
///
/// So to make a generated scene appear there, this writes the scene bytes
/// straight into Brio's own Data/Projects folder using the same
/// SceneService.Serialize format (a .brioproj file, byte-identical in layout
/// to a .brioscn file - the extension is the only difference) and appends a
/// matching entry to Brio's own project index file ("brio.data").
///
/// This pokes at another plugin's private data on disk, so it's treated as
/// opt-in and defensive: the existing registry is backed up before being
/// touched, and if it can't be parsed with confidence, nothing is written at
/// all.
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

    /// <summary>
    /// Guesses Brio's plugin config directory from our own, using the standard
    /// Dalamud convention that every plugin's config folder is a sibling
    /// under the same "pluginConfigs" root (e.g. "...\pluginConfigs\Brio").
    /// Returns null if that folder doesn't exist (Brio not installed, or
    /// never loaded yet).
    /// </summary>

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

            // Read it straight back to confirm our own write is internally
            // correct right now. This can't detect Brio later overwriting the
            // file from its own stale in-memory copy (see the class summary
            // and the in-app warning) - it only rules out a bug on our side.

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
/// Drives Brio through a disable/re-enable cycle via Dalamud's own
/// "/xldisableplugin" and "/xlenableplugin" commands, so it re-reads its
/// project registry from disk without the user having to do it by hand.
///
/// Confirmed by reading Dalamud's own PluginManagementCommandHandler: these
/// commands don't act instantly - they queue the enable/disable to be
/// processed shortly after, and they match a plugin by InternalName (or a
/// case-insensitive display Name). So this is driven as a small state
/// machine, polled once per frame via Tick(), checking the public
/// IDalamudPluginInterface.InstalledPlugins/IExposedPlugin.IsLoaded state
/// until each step actually takes effect (or times out). Tick() is meant to
/// be called from MainWindow.Draw(), i.e. always on the same thread Dalamud
/// calls Draw() on - never from a background thread or a Task continuation.
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
