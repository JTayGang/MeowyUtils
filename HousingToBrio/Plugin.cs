using System;
using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace HousingToBrio;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string LastLayoutPath { get; set; } = string.Empty;

    public bool IncludeInterior { get; set; } = true;
    public bool IncludeExterior { get; set; } = true;
    public bool ApplyDyeColors { get; set; } = true;
    public bool AutoReloadBrio { get; set; } = false;
}

public sealed class Plugin : IDalamudPlugin
{
    // Not required by current Dalamud API levels, but harmless to keep for
    // compatibility with older IDalamudPlugin implementations.
    public string Name => "Housing To Brio";

    private const string CommandName = "/housingtobrio";

    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly ICommandManager _commandManager;
    private readonly IPluginLog _log;

    private readonly WindowSystem _windowSystem = new("HousingToBrio");
    private readonly MainWindow _mainWindow;

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
        FurnitureResolver = new FurnitureModelResolver(dataManager);

        _log.Information(
            "HousingToBrio loaded housing sheets: {Indoor} indoor, {Outdoor} outdoor entries",
            FurnitureResolver.IndoorEntryCount,
            FurnitureResolver.OutdoorEntryCount);

        _mainWindow = new MainWindow(this, _log, FurnitureResolver, _pluginInterface, _commandManager, clientState, dataManager);
        _windowSystem.AddWindow(_mainWindow);

        _pluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;

        _commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the Housing To Brio window.",
        });
    }

    private void OnCommand(string command, string args) => ToggleMainWindow();

    private void ToggleMainWindow() => _mainWindow.IsOpen = !_mainWindow.IsOpen;

    public void SaveConfiguration() => _pluginInterface.SavePluginConfig(Configuration);

    public void Dispose()
    {
        _commandManager.RemoveHandler(CommandName);

        _pluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        _pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;

        _windowSystem.RemoveAllWindows();
        _mainWindow.Dispose();
    }
}
