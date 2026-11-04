using System;
using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HousingToBrio;

public enum LanguageSetting { Auto, English, Japanese }

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string LastLayoutPath { get; set; } = string.Empty;

    // Last Brio folder that was picked or saved to; beats auto-detection when it still exists.
    public string BrioFolderPath { get; set; } = string.Empty;

    public bool IncludeInterior { get; set; } = true;
    public bool IncludeExterior { get; set; } = true;
    public bool ApplyDyeColors { get; set; } = true;
    public bool AutoReloadBrio { get; set; } = false;

    // Auto follows Dalamud's own UI language.
    public LanguageSetting Language { get; set; } = LanguageSetting.Auto;
}

public sealed class Plugin : IDalamudPlugin
{
    // Not required by current API levels, kept for compatibility.
    public string Name => "Housing To Brio";

    private const string CommandName = "/housingtobrio";
    private const string ShortCommandName = "/h2b";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IPluginLog _log;

    private readonly WindowSystem _windowSystem = new("HousingToBrio");
    private readonly MainWindow _mainWindow;

    private int _commandLanguageRevision = -1;

    public Configuration Configuration { get; }
    internal FurnitureModelResolver FurnitureResolver { get; }

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IPluginLog log,
        IDataManager dataManager,
        IClientState clientState)
    {
        _pluginInterface = pluginInterface;
        _commandManager = commandManager;
        _log = log;

        Configuration = _pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Loc.Refresh(Configuration, _pluginInterface.UiLanguage);
        FurnitureResolver = new FurnitureModelResolver(dataManager);

        _log.Information(
            "HousingToBrio loaded housing sheets: {Indoor} indoor, {Outdoor} outdoor entries",
            FurnitureResolver.IndoorEntryCount,
            FurnitureResolver.OutdoorEntryCount);

        _mainWindow = new MainWindow(this, _log, FurnitureResolver, _pluginInterface, _commandManager, clientState, dataManager);
        _windowSystem.AddWindow(_mainWindow);

        _pluginInterface.UiBuilder.Draw += OnDraw;
        _pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;

        RegisterCommands();
    }

    // Registered again whenever the language changes, so /xlhelp shows the right text.
    private void RegisterCommands()
    {
        _commandManager.RemoveHandler(CommandName);
        _commandManager.RemoveHandler(ShortCommandName);

        var help = Loc.T("Opens the Housing To Brio window.");

        _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = help,
        });

        _commandManager.AddHandler(ShortCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = help,
            ShowInHelp = false,
        });

        _commandLanguageRevision = Loc.Revision;
    }

    private void OnDraw()
    {
        Loc.Refresh(Configuration, _pluginInterface.UiLanguage);
        if (_commandLanguageRevision != Loc.Revision)
            RegisterCommands();

        // Must run even while the window is closed (see MainWindow.TickBrioReload).
        _mainWindow.TickBrioReload();

        _windowSystem.Draw();
    }

    private void OnCommand(string command, string args) => ToggleMainWindow();

    private void ToggleMainWindow() => _mainWindow.IsOpen = !_mainWindow.IsOpen;

    public void SaveConfiguration() => _pluginInterface.SavePluginConfig(Configuration);

    public void Dispose()
    {
        _commandManager.RemoveHandler(CommandName);
        _commandManager.RemoveHandler(ShortCommandName);

        _pluginInterface.UiBuilder.Draw -= OnDraw;
        _pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;

        _windowSystem.RemoveAllWindows();
        _mainWindow.Dispose();
    }
}
