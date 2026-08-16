using System;
using Dalamud.Game.Command;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace SkyrimCompass;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/compass";

    public IDalamudPluginInterface PluginInterface { get; }
    public Configuration Config { get; }
    public StatusMirrorEngine StatusMirror { get; }

    private readonly ICommandManager _cmd;
    private readonly IPluginLog _log;
    private readonly WindowSystem _ws = new("SkyrimCompass");
    private readonly CompassHud _hud;
    private readonly ConfigWindow _cfgWin;
    private readonly FirstTimeSetupWindow _ftsw;
    private readonly IFontHandle _font;

    public Plugin(
        IDalamudPluginInterface pi, ICommandManager cmd,
        IClientState cs, IObjectTable ot, ITargetManager tm,
        INamePlateGui npg, ITextureProvider tp, IFateTable ft,
        ICondition cond, IGameGui gg, IDataManager dm, IFramework fw,
        IPluginLog log)
    {
        PluginInterface = pi;
        _cmd = cmd;
        _log = log;

        bool isNew = !pi.ConfigFile.Exists;
        Config = pi.GetPluginConfig() as Configuration ?? new Configuration();

        _font = pi.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamily.Jupiter, 18));

        StatusMirror = new StatusMirrorEngine(pi, fw, ot, log, Config);

        _hud = new CompassHud(cs, ot, tm, npg, tp, ft, cond, gg, dm, Config, log, _font, pi);
        _cfgWin = new ConfigWindow(this);
        _ws.AddWindow(_cfgWin);

        _ftsw = new FirstTimeSetupWindow(this);
        _ws.AddWindow(_ftsw);
        if (isNew && !Config.HasCompletedFirstTimeSetup)
            _ftsw.IsOpen = true;

        cmd.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "'/compass on'/'off' to set explicitly, 'config' for settings, 'setup' " +
                          "for the first-time setup wizard, 'debug' to log nearby objects (/xllog to view)."
        });

        pi.UiBuilder.Draw += OnDraw;
        pi.UiBuilder.OpenConfigUi += OnOpenConfig;
    }

    public void Dispose()
    {
        _ws.RemoveAllWindows();
        _cmd.RemoveHandler(CommandName);
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;
        _font.Dispose();
        _hud.Dispose();
        StatusMirror.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "config": _cfgWin.IsOpen = !_cfgWin.IsOpen; break;
            case "setup": _ftsw.IsOpen = true; break;
            case "debug": _hud.DumpNearbyObjects(); break;
            case "on": SetEnabled(true); break;
            case "off": SetEnabled(false); break;
            default: SetEnabled(!Config.Enabled); break;
        }
    }

    private void SetEnabled(bool enabled)
    {
        Config.Enabled = enabled;
        Config.Save(PluginInterface);
    }

    private void OnDraw()
    {
        try
        {
            _ws.Draw();
            _hud.Draw();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "SkyrimCompass: unhandled exception in draw");
        }
    }

    private void OnOpenConfig() => _cfgWin.IsOpen = true;

    public void OpenFirstTimeSetup() => _ftsw.IsOpen = true;
}