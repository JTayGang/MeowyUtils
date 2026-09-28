using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using RealDebuffs.Effects;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/realdebuffs";

    private readonly IDalamudPluginInterface _pi;
    private readonly ICommandManager _cmd;
    private readonly IPluginLog _log;

    private readonly Configuration _config;
    private readonly ChatBlocker _chatBlocker;
    private readonly CustomStatusWatcher _customStatuses;
    private readonly EffectManager _effects;
    private readonly WindowSystem _windowSystem = new("RealDebuffs");
    private readonly ConfigWindow _configWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IFramework framework,
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

        // One discovery pass produces the effect roster everything else reads from. Cached for
        // the session, so all consumers see the same instances (important: effects carry
        // per-effect state like cast-in timers).
        IReadOnlyList<ISceneEffect> effects = EffectDiscovery.Discover();

        // Effects that have hero slots register them here. Consumers (CustomStatusSnapshot,
        // EffectStylePanel) look slots up by kind.
        foreach (var effect in effects)
        {
            if (effect is IHasHeroSlots withHeroes)
                EffectHeroSlots.Register(effect.Kind, withHeroes.HeroSlots);
        }

        var catalog = new StatusCatalog(dataManager, effects, log);
        _chatBlocker = new ChatBlocker(hooks, log);
        _customStatuses = new CustomStatusWatcher(_pi, framework, objectTable, log, _config);
        _effects = new EffectManager(effects, clientState, objectTable, condition, gameGui, catalog, _config, _chatBlocker, _customStatuses, log);

        _configWindow = new ConfigWindow(_config, SaveConfig, _customStatuses, effects);
        _windowSystem.AddWindow(_configWindow);

        _cmd.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens Real Debuffs settings. '/realdebuffs toggle' to enable/disable everything, '/realdebuffs statuses' to log your current statuses (including custom Moodles/Loci ones) for troubleshooting.",
        });

        _pi.UiBuilder.Draw += OnDraw;
        _pi.UiBuilder.OpenConfigUi += OnOpenConfig;

        _log.Information($"RealDebuffs loaded. {effects.Count} effect(s) discovered.");
    }

    public void Dispose()
    {
        _pi.UiBuilder.Draw -= OnDraw;
        _pi.UiBuilder.OpenConfigUi -= OnOpenConfig;
        _windowSystem.RemoveAllWindows();
        _cmd.RemoveHandler(CommandName);
        _customStatuses.Dispose();
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