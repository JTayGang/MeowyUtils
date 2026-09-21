using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace RealDebuffs;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/realdebuffs";

    private readonly IDalamudPluginInterface _pi;
    private readonly ICommandManager _cmd;
    private readonly IPluginLog _log;

    private readonly Configuration _config;
    private readonly ChatBlocker _chatBlocker;
    private readonly EffectManager _effects;
    private readonly WindowSystem _windowSystem = new("RealDebuffs");
    private readonly ConfigWindow _configWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IObjectTable objectTable,
        IDataManager dataManager,
        ICondition condition,
        IGameGui gameGui,
        IGameInteropProvider hooks,
        IPluginLog log)
    {
        _pi = pluginInterface;
        _cmd = commandManager;
        _log = log;

        _config = _pi.GetPluginConfig() as Configuration ?? new Configuration();

        var catalog = new StatusCatalog(dataManager, log);
        _chatBlocker = new ChatBlocker(hooks, log);
        _effects = new EffectManager(clientState, objectTable, condition, gameGui, catalog, _config, _chatBlocker, log);

        _configWindow = new ConfigWindow(_config, SaveConfig);
        _windowSystem.AddWindow(_configWindow);

        _cmd.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens Real Debuffs settings. '/realdebuffs toggle' to enable/disable everything, '/realdebuffs statuses' to log your current statuses for troubleshooting.",
        });

        _pi.UiBuilder.Draw += OnDraw;
        _pi.UiBuilder.OpenConfigUi += OnOpenConfig;

        _log.Information("RealDebuffs loaded.");
    }

    public void Dispose()
    {
        _pi.UiBuilder.Draw -= OnDraw;
        _pi.UiBuilder.OpenConfigUi -= OnOpenConfig;
        _windowSystem.RemoveAllWindows();
        _cmd.RemoveHandler(CommandName);
        _chatBlocker.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "toggle":
                _config.Enabled = !_config.Enabled;
                SaveConfig();
                break;
            case "statuses":
                _effects.LogCurrentStatuses();
                break;
            default:
                _configWindow.IsOpen = !_configWindow.IsOpen;
                break;
        }
    }

    private void OnOpenConfig() => _configWindow.IsOpen = true;

    private void SaveConfig() => _pi.SavePluginConfig(_config);

    private void OnDraw()
    {
        try
        {
            _windowSystem.Draw();
            _effects.Draw();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "RealDebuffs: unhandled exception in draw.");
        }
    }
}
