using System;
using System.IO;
using System.Numerics;
using System.Threading;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using static HousingToBrio.Loc;
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
        : base("Housing To Brio###housing_to_brio_main", ImGuiWindowFlags.None)
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

        var remembered = plugin.Configuration.BrioFolderPath;
        if (!string.IsNullOrWhiteSpace(remembered) && Directory.Exists(remembered))
        {
            _brioFolderInput = remembered;
        }
        else
        {
            var detected = BrioProjectInstaller.TryDetectBrioDataFolder(_pluginInterface);
            if (detected is not null)
            {
                _brioFolderInput = detected;
                _brioFolderAutoDetected = true;
            }
        }
    }

    public override void PreDraw()
        => WindowName = L("Housing To Brio (Use /housingtobrio OR /h2b to open this menu)", "housing_to_brio_main");

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
            RememberBrioFolder();
        }

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

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawLanguageSelector();
    }

    /// <summary>
    /// Advances the Brio disable/enable cycle. The plugin calls this every frame, because
    /// Draw() doesn't run while the window is closed and Brio would be left disabled.
    /// </summary>
    public void TickBrioReload() => _brioReload.Tick(_pluginInterface, _commandManager);

    // "Copy as path" in Explorer wraps the path in quotes; stray spaces sneak in when pasting too.
    private static string CleanPath(string path) => path.Trim().Trim('"');

    private void RememberBrioFolder()
    {
        if (string.IsNullOrWhiteSpace(_brioFolderInput)
            || _plugin.Configuration.BrioFolderPath == _brioFolderInput)
            return;

        _plugin.Configuration.BrioFolderPath = _brioFolderInput;
        _plugin.SaveConfiguration();
    }

    private void DrawLanguageSelector()
    {
        var lang = (int)_plugin.Configuration.Language;
        ImGui.SetNextItemWidth(130f);
        if (ImGui.Combo("Language / 言語###language", ref lang, new[] { T("Auto"), "English", "日本語" }, 3))
        {
            _plugin.Configuration.Language = (LanguageSetting)lang;
            _plugin.SaveConfiguration();
        }
    }

    // Japanese text is wider than English, so long lines wrap instead of running off the window.
    private static void WrappedColored(Vector4 color, string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
    }

    // Grows past minWidth when the (translated) label needs it. The ### ID keeps it stable across languages.
    private static bool SizedButton(string en, string id, float minWidth)
    {
        var text = T(en);
        var width = Math.Max(minWidth, ImGui.CalcTextSize(text).X + 24f);
        return ImGui.Button($"{text}###{id}", new Vector2(width, 0));
    }

    // Sizes come from the layout file / territory sheet in English; show them in the UI language.
    private static string LocalizeHouseSize(string size) => size.Trim().ToLowerInvariant() switch
    {
        "small" => T("Small"),
        "medium" => T("Medium"),
        "large" => T("Large"),
        "apartment" => T("Apartment"),
        _ => size,
    };

    private void DrawLayoutSection()
    {
        ImGui.TextUnformatted(T("Layout file"));

        ImGui.SetNextItemWidth(-90f);
        ImGui.InputText("###layout_path", ref _layoutPathInput, 512);
        ImGui.SameLine();
        if (ImGui.Button(L("Browse...", "browse_layout")))
            BrowseForLayoutFile();

        if (SizedButton("Load layout", "load_layout", 160f))
            LoadLayout();

        if (_loadError is not null)
        {
            ImGui.Spacing();
            WrappedColored(new Vector4(1f, 0.4f, 0.4f, 1f), _loadError);
        }

        if (_loadedLayout is { } layout)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted(F("House size: {0}", string.IsNullOrEmpty(layout.HouseSize) ? T("unknown") : LocalizeHouseSize(layout.HouseSize)));
            ImGui.TextUnformatted(F("Interior furniture: {0}", layout.InteriorFurniture.Count));
            ImGui.TextUnformatted(F("Exterior furniture: {0}", layout.ExteriorFurniture.Count));
            ImGui.TextUnformatted(F("Fixtures (skipped): {0}", layout.InteriorFixture.Count + layout.ExteriorFixture.Count));

            var currentSize = HouseSizeDetector.GetCurrentIndoorHouseSize(_clientState, _dataManager);
            if (currentSize is not null
                && !string.IsNullOrEmpty(layout.HouseSize)
                && !string.Equals(currentSize, layout.HouseSize, StringComparison.OrdinalIgnoreCase))
            {
                ImGui.Spacing();
                WrappedColored(new Vector4(1f, 0.8f, 0.3f, 1f), F("Layout is {0}, you're in {1}.", LocalizeHouseSize(layout.HouseSize), LocalizeHouseSize(currentSize)));
            }
        }
    }

    private void DrawOptionsSection()
    {
        ImGui.TextUnformatted(T("Options"));

        var includeInterior = _plugin.Configuration.IncludeInterior;
        if (ImGui.Checkbox(L("Include interior furniture", "opt_interior"), ref includeInterior))
        {
            _plugin.Configuration.IncludeInterior = includeInterior;
            _plugin.SaveConfiguration();
        }

        var includeExterior = _plugin.Configuration.IncludeExterior;
        if (ImGui.Checkbox(L("Include exterior furniture", "opt_exterior"), ref includeExterior))
        {
            _plugin.Configuration.IncludeExterior = includeExterior;
            _plugin.SaveConfiguration();
        }

        var applyDye = _plugin.Configuration.ApplyDyeColors;
        if (ImGui.Checkbox(L("Apply dye colors", "opt_dye"), ref applyDye))
        {
            _plugin.Configuration.ApplyDyeColors = applyDye;
            _plugin.SaveConfiguration();
        }
    }

    private void DrawBrioProjectSection()
    {
        ImGui.TextUnformatted(T("Save as a Brio Project"));

        ImGui.SetNextItemWidth(-140f);
        ImGui.InputText("###brio_folder", ref _brioFolderInput, 512);
        ImGui.SameLine();
        if (ImGui.Button(L("Re-detect", "redetect_brio")))
            DetectBrioFolder();
        ImGui.SameLine();
        if (ImGui.Button(L("Browse...", "browse_brio_folder")))
            BrowseForBrioFolder();

        if (!_brioFolderAutoDetected && string.IsNullOrEmpty(_brioFolderInput))
            WrappedColored(new Vector4(1f, 0.8f, 0.3f, 1f), T("Brio folder not found - set manually."));

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("###project_name", T("Project name"), ref _projectNameInput, 100);

        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("###project_description", T("Description (optional)"), ref _projectDescriptionInput, 250);

        var autoReload = _plugin.Configuration.AutoReloadBrio;
        if (ImGui.Checkbox(L("Auto-reload Brio after saving", "opt_autoreload"), ref autoReload))
        {
            _plugin.Configuration.AutoReloadBrio = autoReload;
            _plugin.SaveConfiguration();
        }

        ImGui.Spacing();

        if (!autoReload)
        {
            ImGui.BulletText(T("1. Save as Brio Project"));
            ImGui.BulletText(T("2. Reload Brio (don't Save/Delete Project first)"));
            ImGui.BulletText(T("3. Load Project in Brio"));
        }

        WrappedColored(new Vector4(1f, 0.3f, 0.3f, 1f),
            T("In Load Project's options (gear icon): uncheck \"Relative Object Positions\"."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var reloadRunning = _brioReload.IsRunning;
        if (reloadRunning)
            ImGui.BeginDisabled();
        if (SizedButton("Save as Brio Project", "save_brio_project", 200f))
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
            WrappedColored(color, _brioReload.Message);
        }

        if (_projectSaveError is not null)
        {
            ImGui.Spacing();
            WrappedColored(new Vector4(1f, 0.4f, 0.4f, 1f), _projectSaveError);
        }

        if (_projectSaveMessage is not null)
        {
            ImGui.Spacing();
            WrappedColored(new Vector4(0.4f, 1f, 0.4f, 1f), _projectSaveMessage);
        }

        if (_lastProjectResult is { SkippedUnknownItemCount: > 0 } projResult)
            DrawSkippedItems(projResult);
    }

    private static void DrawSkippedItems(ConversionResult result)
    {
        ImGui.Spacing();
        WrappedColored(new Vector4(1f, 0.8f, 0.3f, 1f), F("{0} item(s) skipped:", result.SkippedUnknownItemCount));

        ImGui.Indent();
        foreach (var name in result.SkippedItemNames)
            ImGui.BulletText(name);
        if (result.SkippedListTruncated)
            ImGui.BulletText(T("...and more."));
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
            _projectSaveError = T("Not found - set manually.");
        }
    }

    private void BrowseForBrioFolder()
    {
        var description = T("Select Brio's config folder");

        RunOnStaThread(() =>
        {
            using var dialog = new WinForms.FolderBrowserDialog
            {
                Description = description,
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
        _brioFolderInput = CleanPath(_brioFolderInput);

        if (_loadedLayout is null)
        {
            _projectSaveError = T("Load a layout first.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_brioFolderInput) || !Directory.Exists(_brioFolderInput))
        {
            _projectSaveError = T("Set a valid Brio folder first.");
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
                ? F("Converted from {0}", Path.GetFileName(_layoutPathInput))
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
            _projectSaveMessage = F("{0} {1} objects.", installResult.Message, result.PlacedCount);
            RememberBrioFolder();

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
        _layoutPathInput = CleanPath(_layoutPathInput);
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
            _loadError = F("Couldn't load: {0}", ex.Message);
        }
    }

    private void BrowseForLayoutFile()
    {
        var title = T("Select a housing layout file");
        var filter = $"{T("Housing layout (*.json)")}|*.json|{T("All files (*.*)")}|*.*";

        RunOnStaThread(() =>
        {
            using var dialog = new WinForms.OpenFileDialog
            {
                Title = title,
                Filter = filter,
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
