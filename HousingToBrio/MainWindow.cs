using System;
using System.IO;
using System.Numerics;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using WinForms = System.Windows.Forms;

namespace HousingToBrio;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin _plugin;
    private readonly IPluginLog _log;
    private readonly FurnitureModelResolver _resolver;
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IClientState _clientState;
    private readonly IDataManager _dataManager;
    private readonly BrioPluginControl _brioReload = new();

    private string _layoutPathInput = string.Empty;

    private LayoutFile? _loadedLayout;
    private string? _loadError;

    private string _brioFolderInput = string.Empty;
    private bool _brioFolderAutoDetected;
    private string _projectNameInput = string.Empty;
    private string _projectDescriptionInput = string.Empty;
    private string? _projectSaveMessage;
    private string? _projectSaveError;
    private ConversionResult? _lastProjectResult;

    // Set by a background STA thread (file dialogs); read back in Draw().
    private volatile string? _pendingLayoutPath;
    private volatile string? _pendingBrioFolderPath;

    public MainWindow(
        Plugin plugin,
        IPluginLog log,
        FurnitureModelResolver resolver,
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IDataManager dataManager)
        : base("Housing To Brio (Use /housingtobrio OR /h2b to open this menu)###housing_to_brio_main", ImGuiWindowFlags.None)
    {
        _plugin = plugin;
        _log = log;
        _resolver = resolver;
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        _clientState = clientState;
        _dataManager = dataManager;

        Size = new Vector2(560, 700);
        SizeCondition = ImGuiCond.FirstUseEver;

        _layoutPathInput = plugin.Configuration.LastLayoutPath;

        var detected = BrioProjectInstaller.TryDetectBrioDataFolder(_pluginInterface);
        if (detected is not null)
        {
            _brioFolderInput = detected;
            _brioFolderAutoDetected = true;
        }
    }

    public override void Draw()
    {
        if (_pendingLayoutPath is { } newLayoutPath)
        {
            _pendingLayoutPath = null;
            _layoutPathInput = newLayoutPath;
            LoadLayout();
        }

        if (_pendingBrioFolderPath is { } newBrioFolder)
        {
            _pendingBrioFolderPath = null;
            _brioFolderInput = newBrioFolder;
            _brioFolderAutoDetected = false;
        }

        _brioReload.Tick(_pluginInterface, _commandManager);

        DrawLayoutSection();

        if (_loadedLayout is not null)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            DrawOptionsSection();

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            DrawBrioProjectSection();
        }
    }

    private void DrawLayoutSection()
    {
        ImGui.TextUnformatted("Layout file");

        ImGui.SetNextItemWidth(-90f);
        ImGui.InputText("###layout_path", ref _layoutPathInput, 512);
        ImGui.SameLine();
        if (ImGui.Button("Browse...###browse_layout"))
            BrowseForLayoutFile();

        if (ImGui.Button("Load layout", new Vector2(160, 0)))
            LoadLayout();

        if (_loadError is not null)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), _loadError);
        }

        if (_loadedLayout is { } layout)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted($"House size: {(string.IsNullOrEmpty(layout.HouseSize) ? "unknown" : layout.HouseSize)}");
            ImGui.TextUnformatted($"Interior furniture: {layout.InteriorFurniture.Count}");
            ImGui.TextUnformatted($"Exterior furniture: {layout.ExteriorFurniture.Count}");
            ImGui.TextUnformatted($"Fixtures (skipped): {layout.InteriorFixture.Count + layout.ExteriorFixture.Count}");

            var currentSize = HouseSizeDetector.GetCurrentIndoorHouseSize(_clientState, _dataManager);
            if (currentSize is not null
                && !string.IsNullOrEmpty(layout.HouseSize)
                && !string.Equals(currentSize, layout.HouseSize, StringComparison.OrdinalIgnoreCase))
            {
                ImGui.Spacing();
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), $"Layout is {layout.HouseSize}, you're in {currentSize}.");
            }
        }
    }

    private void DrawOptionsSection()
    {
        ImGui.TextUnformatted("Options");

        var includeInterior = _plugin.Configuration.IncludeInterior;
        if (ImGui.Checkbox("Include interior furniture", ref includeInterior))
        {
            _plugin.Configuration.IncludeInterior = includeInterior;
            _plugin.SaveConfiguration();
        }

        var includeExterior = _plugin.Configuration.IncludeExterior;
        if (ImGui.Checkbox("Include exterior furniture", ref includeExterior))
        {
            _plugin.Configuration.IncludeExterior = includeExterior;
            _plugin.SaveConfiguration();
        }

        var applyDye = _plugin.Configuration.ApplyDyeColors;
        if (ImGui.Checkbox("Apply dye colors", ref applyDye))
        {
            _plugin.Configuration.ApplyDyeColors = applyDye;
            _plugin.SaveConfiguration();
        }
    }

    private void DrawBrioProjectSection()
    {
        ImGui.TextUnformatted("Save as a Brio Project");

        ImGui.SetNextItemWidth(-140f);
        ImGui.InputText("###brio_folder", ref _brioFolderInput, 512);
        ImGui.SameLine();
        if (ImGui.Button("Re-detect###redetect_brio"))
            DetectBrioFolder();
        ImGui.SameLine();
        if (ImGui.Button("Browse...###browse_brio_folder"))
            BrowseForBrioFolder();

        if (!_brioFolderAutoDetected && string.IsNullOrEmpty(_brioFolderInput))
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), "Brio folder not found - set manually.");

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("###project_name", "Project name", ref _projectNameInput, 100);

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("###project_description", "Description (optional)", ref _projectDescriptionInput, 250);

        var autoReload = _plugin.Configuration.AutoReloadBrio;
        if (ImGui.Checkbox("Auto-reload Brio after saving", ref autoReload))
        {
            _plugin.Configuration.AutoReloadBrio = autoReload;
            _plugin.SaveConfiguration();
        }

        ImGui.Spacing();

        if (!autoReload)
        {
            ImGui.BulletText("1. Save as Brio Project");
            ImGui.BulletText("2. Reload Brio (don't Save/Delete Project first)");
            ImGui.BulletText("3. Load Project in Brio");
        }

        ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f),
            "In Load Project's options (gear icon): uncheck \"Relative Object Positions\".");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var reloadRunning = _brioReload.IsRunning;
        if (reloadRunning)
            ImGui.BeginDisabled();
        if (ImGui.Button("Save as Brio Project", new Vector2(200, 0)))
            SaveAsBrioProject();
        if (reloadRunning)
            ImGui.EndDisabled();

        if (_brioReload.CurrentState != BrioPluginControl.State.Idle && _brioReload.Message is not null)
        {
            ImGui.Spacing();
            var color = _brioReload.CurrentState switch
            {
                BrioPluginControl.State.Succeeded => new Vector4(0.4f, 1f, 0.4f, 1f),
                BrioPluginControl.State.Failed => new Vector4(1f, 0.4f, 0.4f, 1f),
                _ => new Vector4(1f, 0.8f, 0.3f, 1f),
            };
            ImGui.TextColored(color, _brioReload.Message);
        }

        if (_projectSaveError is not null)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), _projectSaveError);
        }

        if (_projectSaveMessage is not null)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), _projectSaveMessage);
        }

        if (_lastProjectResult is { SkippedUnknownItemCount: > 0 } projResult)
            DrawSkippedItems(projResult);
    }

    private static void DrawSkippedItems(ConversionResult result)
    {
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), $"{result.SkippedUnknownItemCount} item(s) skipped:");

        ImGui.Indent();
        foreach (var name in result.SkippedItemNames)
            ImGui.BulletText(name);
        if (result.SkippedUnknownItemCount > result.SkippedItemNames.Count)
            ImGui.BulletText("...and more.");
        ImGui.Unindent();
    }

    private void DetectBrioFolder()
    {
        var detected = BrioProjectInstaller.TryDetectBrioDataFolder(_pluginInterface);
        if (detected is not null)
        {
            _brioFolderInput = detected;
            _brioFolderAutoDetected = true;
        }
        else
        {
            _brioFolderAutoDetected = false;
            _projectSaveError = "Not found - set manually.";
        }
    }

    private void BrowseForBrioFolder()
    {
        RunOnStaThread(() =>
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = "Select Brio's config folder",
                UseDescriptionForTitle = true,
            };

            if (!string.IsNullOrEmpty(_brioFolderInput) && Directory.Exists(_brioFolderInput))
                dialog.SelectedPath = _brioFolderInput;

            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                _pendingBrioFolderPath = dialog.SelectedPath;
        });
    }

    private void SaveAsBrioProject()
    {
        _projectSaveError = null;
        _projectSaveMessage = null;
        _brioReload.Reset();

        if (_loadedLayout is null)
        {
            _projectSaveError = "Load a layout first.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_brioFolderInput) || !Directory.Exists(_brioFolderInput))
        {
            _projectSaveError = "Set a valid Brio folder first.";
            return;
        }

        var projectName = string.IsNullOrWhiteSpace(_projectNameInput)
            ? Path.GetFileNameWithoutExtension(_layoutPathInput)
            : _projectNameInput;

        var options = new ConversionOptions
        {
            IncludeInterior = _plugin.Configuration.IncludeInterior,
            IncludeExterior = _plugin.Configuration.IncludeExterior,
            ApplyDyeColors = _plugin.Configuration.ApplyDyeColors,
        };

        var result = LayoutToBrioConverter.Convert(_loadedLayout, _resolver, options);

        var manifest = new BrioSceneManifestDto
        {
            Author = "HousingToBrio",
            Description = string.IsNullOrWhiteSpace(_projectDescriptionInput)
                ? $"Converted from {Path.GetFileName(_layoutPathInput)}"
                : _projectDescriptionInput,
        };

        var installResult = BrioProjectInstaller.InstallAsProject(
            _brioFolderInput,
            projectName,
            string.IsNullOrWhiteSpace(_projectDescriptionInput) ? null : _projectDescriptionInput,
            manifest,
            result.WorldObjects);

        _lastProjectResult = result;

        if (installResult.Success)
        {
            _projectSaveMessage = $"{installResult.Message} {result.PlacedCount} objects.";

            if (_plugin.Configuration.AutoReloadBrio)
                _brioReload.Start(_pluginInterface, _commandManager);
        }
        else
        {
            _log.Warning("Failed to save Brio project: {Message}", installResult.Message);
            _projectSaveError = installResult.Message;
        }
    }

    private void LoadLayout()
    {
        _loadError = null;
        _lastProjectResult = null;
        _projectSaveMessage = null;
        _projectSaveError = null;

        try
        {
            _loadedLayout = LayoutParser.LoadFromFile(_layoutPathInput);
            _plugin.Configuration.LastLayoutPath = _layoutPathInput;
            _plugin.SaveConfiguration();

            _projectNameInput = Path.GetFileNameWithoutExtension(_layoutPathInput);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to load housing layout from {Path}", _layoutPathInput);
            _loadedLayout = null;
            _loadError = $"Couldn't load: {ex.Message}";
        }
    }

    private void BrowseForLayoutFile()
    {
        RunOnStaThread(() =>
        {
            using var dialog = new WinForms.OpenFileDialog
            {
                Title = "Select a housing layout file",
                Filter = "Housing layout (*.json)|*.json|All files (*.*)|*.*",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                _pendingLayoutPath = dialog.FileName;
        });
    }

    private static void RunOnStaThread(Action action)
    {
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch
            {
                // Dialog was cancelled/closed abnormally - nothing to do.
            }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void Dispose()
    {
    }
}
