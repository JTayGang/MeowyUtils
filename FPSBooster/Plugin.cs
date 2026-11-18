using System;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace FPSBooster;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/fpsbooster";
    private const string ChatPrefix = "[FPSBooster] ";

    public IDalamudPluginInterface PluginInterface { get; }
    public Configuration Config { get; }
    public CullingService Culling { get; }
    public TextureStreamingService TextureStreaming { get; }
    public TextureGpuUploadProbe TextureGpuUpload { get; }
    public NativeTextureHandleProbe NativeTextureHandle { get; }
    public PenumbraTextureSourceProbe PenumbraTextureSource { get; }

    private readonly ICommandManager _cmd;
    private readonly IChatGui _chat;
    private readonly WindowSystem _ws = new("FPSBooster");
    private readonly ConfigWindow _cfgWin;

    public Plugin(
        IDalamudPluginInterface pi, ICommandManager cmd, IChatGui chat,
        IClientState cs, ICondition cond, IFramework fw, IObjectTable ot, IDataManager data, IPluginLog log)
    {
        PluginInterface = pi;
        _cmd = cmd;
        _chat = chat;

        Config = LoadConfig(pi, log);
        Config.Sanitize();

        Culling = new CullingService(fw, cs, cond, ot, log, Config);
        TextureStreaming = new TextureStreamingService(data);
        TextureGpuUpload = new TextureGpuUploadProbe(pi);
        NativeTextureHandle = new NativeTextureHandleProbe();
        PenumbraTextureSource = new PenumbraTextureSourceProbe(pi);
        _cfgWin = new ConfigWindow(this);
        _ws.AddWindow(_cfgWin);

        cmd.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens settings. Add 'on' or 'off' to toggle culling.",
        });

        pi.UiBuilder.Draw += _ws.Draw;
        pi.UiBuilder.OpenConfigUi += OpenConfig;
        pi.UiBuilder.OpenMainUi += OpenConfig;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= _ws.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfig;
        _cmd.RemoveHandler(CommandName);
        _cfgWin.Shutdown();
        _ws.RemoveAllWindows();
        Config.Save(PluginInterface);
        Culling.Dispose();
        TextureStreaming.Dispose();
        TextureGpuUpload.Dispose();
        NativeTextureHandle.Dispose();
        PenumbraTextureSource.Clear();
    }

    private static Configuration LoadConfig(IDalamudPluginInterface pi, IPluginLog log)
    {
        try { return pi.GetPluginConfig() as Configuration ?? new Configuration(); }
        catch (Exception ex)
        {
            log.Warning(ex, "FPSBooster: could not read settings, using defaults.");
            return new Configuration();
        }
    }

    private void OpenConfig() => _cfgWin.IsOpen = true;

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "" or "config" or "settings": _cfgWin.IsOpen = !_cfgWin.IsOpen; break;
            case "on": SetEnabled(true); break;
            case "off": SetEnabled(false); break;
            case "test":
                _cfgWin.IsOpen = true;
                _chat.Print(ChatPrefix + "Hold a test button on the Housing tab to preview culling; release to restore.");
                break;
            default: _chat.Print(ChatPrefix + "Try /fpsbooster, /fpsbooster on or /fpsbooster off."); break;
        }
    }

    private void SetEnabled(bool enabled)
    {
        Config.Enabled = enabled;
        Config.Save(PluginInterface);
        _chat.Print(ChatPrefix + (enabled ? "Culling is on." : "Culling is off."));
    }
}
