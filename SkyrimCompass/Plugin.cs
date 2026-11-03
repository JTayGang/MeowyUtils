using System;
using Dalamud.Game.Command;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using static SkyrimCompass.Loc;

namespace SkyrimCompass;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/compass";
    private const string ChatPrefix = "[Skyrim Compass] ";

    public IDalamudPluginInterface PluginInterface { get; }
    public Configuration Config { get; }
    public StatusMirrorEngine StatusMirror { get; }

    private readonly ICommandManager _cmd;
    private readonly IChatGui _chat;
    private readonly IPluginLog _log;
    private readonly WindowSystem _ws = new("SkyrimCompass");
    private readonly CompassHud _hud;
    private readonly ConfigWindow _cfgWin;
    private readonly FirstTimeSetupWindow _ftsw;
    private readonly DynamicGameFontCache _fonts;
    private DateTime _nextErrorLog = DateTime.MinValue;

    public Plugin(
        IDalamudPluginInterface pi, ICommandManager cmd, IChatGui chat,
        IClientState cs, IObjectTable ot, ITargetManager tm,
        INamePlateGui npg, ITextureProvider tp, IFateTable ft,
        ICondition cond, IGameGui gg, IDataManager dm, IFramework fw,
        IPluginLog log)
    {
        PluginInterface = pi;
        _cmd = cmd;
        _chat = chat;
        _log = log;

        bool isNew = !pi.ConfigFile.Exists;
        Config = LoadConfig(pi, log);
        Config.Sanitize();
        Refresh(Config, pi.UiLanguage);

        _fonts = new DynamicGameFontCache(pi.UiBuilder.FontAtlas, GameFontFamily.Jupiter);
        StatusMirror = new StatusMirrorEngine(pi, fw, ot, log, Config);
        _hud = new CompassHud(cs, ot, tm, npg, tp, ft, cond, gg, dm, Config, log, _fonts, pi);

        _cfgWin = new ConfigWindow(this);
        _ftsw = new FirstTimeSetupWindow(this);
        _ws.AddWindow(_cfgWin);
        _ws.AddWindow(_ftsw);
        if (isNew && !Config.HasCompletedFirstTimeSetup)
            _ftsw.IsOpen = true;

        cmd.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = T("Turns the compass on or off. Add: config (settings), setup (guide), on, off."),
        });

        pi.UiBuilder.Draw += OnDraw;
        pi.UiBuilder.OpenConfigUi += OnOpenConfig;
        pi.UiBuilder.OpenMainUi += OnOpenConfig;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OnOpenConfig;
        _cmd.RemoveHandler(CommandName);
        _ws.RemoveAllWindows();
        Config.Save(PluginInterface);
        _hud.Dispose();
        _fonts.Dispose();
        StatusMirror.Dispose();
    }

    public void OpenFirstTimeSetup() => _ftsw.IsOpen = true;

    private static Configuration LoadConfig(IDalamudPluginInterface pi, IPluginLog log)
    {
        try { return pi.GetPluginConfig() as Configuration ?? new Configuration(); }
        catch (Exception ex)
        {
            log.Warning(ex, "SkyrimCompass: could not read settings, using defaults.");
            return new Configuration();
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "": SetEnabled(!Config.Enabled); break;
            case "on": SetEnabled(true); break;
            case "off": SetEnabled(false); break;
            case "config" or "settings": _cfgWin.IsOpen = !_cfgWin.IsOpen; break;
            case "setup": _ftsw.IsOpen = true; break;
            case "debug": _hud.DumpNearbyObjects(); break;
            default: _chat.Print(ChatPrefix + T("Try /compass, /compass config, or /compass setup.")); break;
        }
    }

    private void SetEnabled(bool enabled)
    {
        Config.Enabled = enabled;
        Config.Save(PluginInterface);
        _chat.Print(ChatPrefix + (enabled ? T("Compass is on.") : T("Compass is off.")));
    }

    private void OnDraw()
    {
        Refresh(Config, PluginInterface.UiLanguage);
        try { _ws.Draw(); } catch (Exception ex) { LogDrawError(ex); }
        try { _hud.Draw(); } catch (Exception ex) { LogDrawError(ex); }
    }

    // One log line per 10s so a draw bug can't flood the log at 60 fps.
    private void LogDrawError(Exception ex)
    {
        if (DateTime.UtcNow < _nextErrorLog) return;
        _nextErrorLog = DateTime.UtcNow.AddSeconds(10);
        _log.Error(ex, "SkyrimCompass: error while drawing");
    }

    private void OnOpenConfig() => _cfgWin.IsOpen = true;
}
