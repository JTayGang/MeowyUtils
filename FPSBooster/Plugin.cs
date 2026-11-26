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
    public NativeTextureLifecycleProbe NativeTextureLifecycle { get; }
    public NativeProgressiveTexturePreview NativeProgressiveTexturePreview { get; }
    public FluidSimulationProbe FluidSimulation { get; }
    public CollisionProbe Collision { get; }
    public GroundedFluidProbe GroundedFluid { get; }
    public PenumbraTextureSourceProbe PenumbraTextureSource { get; }
    public PenumbraAvfxReplacementProbe PenumbraAvfxReplacement { get; }
    public PerformanceProfiler PerformanceProfiler { get; }
    public NativeVfxProbe NativeVfx { get; }
    public VfxAssetCatalog VfxAssetCatalog { get; }

    private readonly ICommandManager _cmd;
    private readonly IChatGui _chat;
    private readonly WindowSystem _ws = new("FPSBooster");
    private readonly ConfigWindow _cfgWin;

    public Plugin(
        IDalamudPluginInterface pi, ICommandManager cmd, IChatGui chat,
        IClientState cs, ICondition cond, IFramework fw, IObjectTable ot, IDataManager data, IGameInteropProvider interop, ISigScanner sigScanner, IPluginLog log)
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
        NativeTextureLifecycle = new NativeTextureLifecycleProbe(interop, sigScanner);
        NativeProgressiveTexturePreview = new NativeProgressiveTexturePreview(pi);
        FluidSimulation = new FluidSimulationProbe(pi);
        Collision = new CollisionProbe(ot);
        GroundedFluid = new GroundedFluidProbe(FluidSimulation, ot, interop);
        PenumbraTextureSource = new PenumbraTextureSourceProbe(pi);
        PenumbraAvfxReplacement = new PenumbraAvfxReplacementProbe(pi, data, ot, fw, log);
        PerformanceProfiler = new PerformanceProfiler(fw, Culling, NativeTextureLifecycle);
        VfxAssetCatalog = new VfxAssetCatalog(data, log);
        NativeVfx = new NativeVfxProbe(data, ot, fw, interop, sigScanner, log, PenumbraAvfxReplacement);
        _cfgWin = new ConfigWindow(this);
        _ws.AddWindow(_cfgWin);

        cmd.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens settings. Add 'on' or 'off' to toggle culling.",
        });

        // Scene-depth snapshots run after the 3D scene pass but before FPSBooster emits ImGui
        // commands, so replacing a captured SRV cannot invalidate an image queued this frame.
        pi.UiBuilder.Draw += GroundedFluid.ProcessSceneDepthFrame;
        pi.UiBuilder.Draw += _ws.Draw;
        pi.UiBuilder.Draw += GroundedFluid.DrawOverlay;
        pi.UiBuilder.OpenConfigUi += OpenConfig;
        pi.UiBuilder.OpenMainUi += OpenConfig;
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= GroundedFluid.ProcessSceneDepthFrame;
        PluginInterface.UiBuilder.Draw -= _ws.Draw;
        PluginInterface.UiBuilder.Draw -= GroundedFluid.DrawOverlay;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
        PluginInterface.UiBuilder.OpenMainUi -= OpenConfig;
        _cmd.RemoveHandler(CommandName);
        _cfgWin.Shutdown();
        _ws.RemoveAllWindows();
        PerformanceProfiler.Dispose();
        VfxAssetCatalog.Dispose();
        NativeVfx.Dispose();
        PenumbraAvfxReplacement.Dispose();
        NativeTextureLifecycle.Dispose();
        NativeProgressiveTexturePreview.Dispose();
        GroundedFluid.Dispose();
        FluidSimulation.Dispose();
        Collision.Dispose();
        NativeTextureHandle.Dispose();
        PenumbraTextureSource.Clear();
        Config.Save(PluginInterface);
        Culling.Dispose();
        TextureStreaming.Dispose();
        TextureGpuUpload.Dispose();
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
