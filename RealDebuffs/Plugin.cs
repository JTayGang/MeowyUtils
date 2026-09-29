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

        // Anything sitting in the generator override dicts at load time is a leftover from a previous
        // session's preview session - see ClearGeneratorOverrides for why we don't want to keep it.
        ClearGeneratorOverrides();

        // One discovery pass produces the effect roster everything else reads from. Cached for
        // the session, so all consumers see the same instances (important: effects carry
        // per-effect state like cast-in timers).
        IReadOnlyList<ISceneEffect> effects = EffectDiscovery.Discover();

        foreach (var effect in effects)
            EffectRegistry.Register(effect);

        if (TooltipKeywordRule.SeedNewEffects(_config, effects))
            SaveConfig();

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

        // A material that has no NaturalLanguageWords isn't unreachable - the user can still
        // select it by hand in the Effect generator - but it can't be exported or referenced in a
        // "made of X" phrase. That's correct for region materials and stroke.simple; it's worth
        // a warning for anything else, so a new material author who forgets gets a signal.
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

    /// <summary>
    /// Wipes the ephemeral generator overrides (material substitutions and color tints chosen in
    /// the Effect generator panel). They are live-preview settings, not customizations: left in
    /// config they would silently re-skin a vanilla debuff long after the menu was forgotten.
    /// Runs when the settings window closes, and once at load to clear leftovers from older
    /// versions. Lasting "make Burns look like X" customization goes through a Moodle/Loci status
    /// description; those overrides live in the per-frame snapshot and apply only while active.
    /// </summary>
    private void ClearGeneratorOverrides()
    {
        if (_config.MaterialOverrides.Count == 0 && _config.ColorOverrides.Count == 0)
            return;

        _config.MaterialOverrides.Clear();
        _config.ColorOverrides.Clear();
        SaveConfig();
    }

    private void OnDraw()
    {
        try
        {
            _windowSystem.Draw();
            _effects.Draw();

            // Detect the settings window closing: the generator's live overrides are meant to be
            // preview settings, and any that survive past the menu closing would tint or re-material
            // the vanilla effect in normal gameplay without the user realizing. Watch the
            // open -> closed transition rather than just "is closed" so this runs exactly once.
            bool isOpen = _configWindow.IsOpen;
            if (_configWasOpen && !isOpen)
                ClearGeneratorOverrides();
            _configWasOpen = isOpen;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "RealDebuffs: unhandled exception in draw.");
        }
    }
}