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
    private bool _configWasOpen;
    private bool _drawFailed;

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

        // Generator overrides present at load are leftovers from a previous preview (see ClearGeneratorOverrides).
        ClearGeneratorOverrides();

        // One discovery pass builds the roster; cached so every consumer shares the same stateful effect instances.
        IReadOnlyList<ISceneEffect> effects = EffectDiscovery.Discover();

        foreach (var effect in effects)
            EffectRegistry.Register(effect);

        if (TooltipKeywordRule.SeedNewEffects(_config, effects))
            SaveConfig();

        var catalog = new StatusCatalog(dataManager, effects, log);
        _chatBlocker = new ChatBlocker(hooks, log);
        _customStatuses = new CustomStatusWatcher(_pi, framework, objectTable, log, _config);
        _effects = new EffectManager(effects, clientState, objectTable, condition, gameGui, catalog, _config, _chatBlocker, _customStatuses, log);

        _configWindow = new ConfigWindow(_config, OnConfigEdited, _customStatuses, effects);
        _windowSystem.AddWindow(_configWindow);

        _cmd.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Opens Real Debuffs settings. '/realdebuffs toggle' to enable/disable everything, '/realdebuffs statuses' to log your current statuses (including custom Moodles/Loci ones) for troubleshooting.",
        });

        _pi.UiBuilder.Draw += OnDraw;
        _pi.UiBuilder.OpenConfigUi += OnOpenConfig;

        // Materials with no NaturalLanguageWords can't be used in "made of X" phrases: fine for regions and stroke.simple, a warning otherwise.
        foreach (var material in MaterialRegistry.AllMaterials)
        {
            if (material.NaturalLanguageWords.Length == 0
                && !material.Name.StartsWith("region.", StringComparison.Ordinal)
                && material.Name != "stroke.simple")
            {
                _log.Warning($"RealDebuffs: material \"{material.Name}\" declares no NaturalLanguageWords - " +
                             "it can't be referenced by a \"made of X\" phrase.");
            }
        }

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

    /// <summary>Settings-window save callback: persist, then re-read statuses and settings next frame rather than at the heartbeat.</summary>
    private void OnConfigEdited()
    {
        SaveConfig();
        _customStatuses.RequestRefresh();
        _effects.Invalidate();
    }

    /// <summary>Wipes the live-preview generator overrides (they'd silently re-skin a vanilla debuff); runs on window close and at load.</summary>
    private bool ClearGeneratorOverrides()
    {
        if (_config.MaterialOverrides.Count == 0 && _config.ColorOverrides.Count == 0)
            return false;

        _config.MaterialOverrides.Clear();
        _config.ColorOverrides.Clear();
        SaveConfig();
        return true;
    }

    private void OnDraw()
    {
        try
        {
            _windowSystem.Draw();

            // On the settings window's open -> closed edge, clear the preview overrides (before effects draw, so a failure there can't skip it).
            bool isOpen = _configWindow.IsOpen;
            if (_configWasOpen && !isOpen && ClearGeneratorOverrides())
                _effects.Invalidate();
            _configWasOpen = isOpen;

            _effects.Draw();
            _drawFailed = false;
        }
        catch (Exception ex)
        {
            // Once per failure streak, not once per frame.
            if (!_drawFailed)
                _log.Error(ex, "RealDebuffs: unhandled exception in draw (repeats are suppressed until a draw succeeds).");
            _drawFailed = true;
        }
    }
}