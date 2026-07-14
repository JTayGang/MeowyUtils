using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using StatusBridge.Bridge;
using StatusBridge.Windows;

namespace StatusBridge;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/statusbridge";

    private readonly Configuration _config;
    private readonly BridgeEngine _engine;
    private readonly WindowSystem _windowSystem = new("StatusBridge");
    private readonly ConfigWindow _configWindow;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Svc>();

        _config = Svc.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        _config.MigrateIfNeeded();
        _engine = new BridgeEngine(_config);

        _configWindow = new ConfigWindow(_config, _engine);
        _windowSystem.AddWindow(_configWindow);

        Svc.PluginInterface.UiBuilder.Draw += _windowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

        Svc.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens the StatusBridge settings window."
        });
    }

    private void OpenConfigUi() => _configWindow.IsOpen = true;

    private void OnCommand(string command, string args) => _configWindow.IsOpen = true;

    public void Dispose()
    {
        Svc.CommandManager.RemoveHandler(CommandName);

        Svc.PluginInterface.UiBuilder.Draw -= _windowSystem.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        _windowSystem.RemoveAllWindows();

        _configWindow.Dispose();
        _engine.Dispose();
    }
}
