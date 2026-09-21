using System;
using System.Collections.Generic;
using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using RealDebuffs.Effects;

namespace RealDebuffs;

/// <summary>
/// Reads the local player's active statuses every frame, maps them to <see cref="DebuffKind"/>s
/// via <see cref="StatusCatalog"/>, and draws every enabled effect in a fixed order - so layering
/// (which effect renders "on top" of which) is always consistent no matter which combination of
/// debuffs is currently active. Effects fade in/out smoothly rather than popping on/off, so
/// gaining or losing a status doesn't cause a jarring instant flip.
/// </summary>
public sealed class EffectManager
{
    /// <summary>
    /// Fixed draw order = fixed stacking order. Later entries draw on top of earlier ones. Blind is
    /// pinned first/bottom because it covers more of the screen than anything else (a near-total
    /// vignette) - drawn any later it would sit on top of and wash out every other effect. Silence
    /// stays near the end so it renders over Blind - matching "silence on top of the blindfold"
    /// from the spec. Reorder this list to change layering; add a new IScreenEffect instance here
    /// (plus a DebuffKind and a StatusCatalog.NameMap entry) to extend.
    /// </summary>
    private readonly IScreenEffect[] _order =
    {
        new BlindEffect(),
        new PoisonEffect(),
        new HeavyEffect(),
        new BindEffect(),
        new PetrificationEffect(),
        new SleepEffect(),
        new StunEffect(),
        new ParalysisEffect(),
        new SilenceEffect(),
    };

    private const float FadeInPerSecond = 1f / 0.35f;  // ~350ms to fully appear
    private const float FadeOutPerSecond = 1f / 0.6f;  // ~600ms to fully disappear

    private readonly Dictionary<DebuffKind, float> _currentAlpha = new();
    private readonly HashSet<DebuffKind> _activeScratch = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private float _lastTime;

    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly IGameGui _gameGui;
    private readonly StatusCatalog _catalog;
    private readonly Configuration _config;
    private readonly ChatBlocker _chatBlocker;
    private readonly IPluginLog _log;

    public EffectManager(
        IClientState clientState, IObjectTable objectTable, ICondition condition, IGameGui gameGui,
        StatusCatalog catalog, Configuration config, ChatBlocker chatBlocker, IPluginLog log)
    {
        _clientState = clientState;
        _objectTable = objectTable;
        _condition = condition;
        _gameGui = gameGui;
        _catalog = catalog;
        _config = config;
        _chatBlocker = chatBlocker;
        _log = log;

        foreach (var effect in _order)
            _currentAlpha[effect.Kind] = 0f;
    }

    public void Draw()
    {
        float time = (float)_clock.Elapsed.TotalSeconds;
        float dt = _lastTime <= 0f ? 0f : Math.Clamp(time - _lastTime, 0f, 0.25f);
        _lastTime = time;

        _activeScratch.Clear();

        bool suppressed = !_config.Enabled
            || _gameGui.GameUiHidden
            || _clientState.IsGPosing
            || (_config.HideDuringCutscenes && (
                _condition[ConditionFlag.WatchingCutscene] ||
                _condition[ConditionFlag.WatchingCutscene78] ||
                _condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                _condition[ConditionFlag.CreatingCharacter]));

        var player = _objectTable.LocalPlayer;
        if (!suppressed && player != null)
        {
            foreach (var status in player.StatusList)
            {
                if (status.StatusId == 0) continue;
                if (_catalog.TryGetKind(status.StatusId, out var kind))
                    _activeScratch.Add(kind);
            }
        }

        _chatBlocker.SetSilenced(_config.SilenceBlocksChat && _activeScratch.Contains(DebuffKind.Silence));

        var screenSize = ImGui.GetIO().DisplaySize;
        if (screenSize.X <= 0 || screenSize.Y <= 0) return;

        var dl = ImGui.GetForegroundDrawList();

        foreach (var effect in _order)
        {
            bool active = !suppressed && _config.IsEnabled(effect.Kind) && _activeScratch.Contains(effect.Kind);
            active |= !suppressed && _config.IsEnabled(effect.Kind) && DebugTester.IsForced(effect.Kind); // TEST-TOOLS: delete this line (and DebugTester.cs) to remove the test panel
            float target = active ? 1f : 0f;
            float rate = active ? FadeInPerSecond : FadeOutPerSecond;
            float current = MoveTowards(_currentAlpha[effect.Kind], target, rate * dt);
            _currentAlpha[effect.Kind] = current;

            if (current <= 0.001f) continue;

            try
            {
                effect.Draw(dl, screenSize, current * _config.GlobalIntensity, time);
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"RealDebuffs: {effect.Kind} effect threw during Draw - disabling it for the rest of this session.");
                _config.SetEnabled(effect.Kind, false); // in-memory only, not saved - a real game update fix shouldn't require a settings reset
            }
        }
    }

    private static float MoveTowards(float current, float target, float maxDelta)
    {
        if (MathF.Abs(target - current) <= maxDelta) return target;
        return current + MathF.Sign(target - current) * maxDelta;
    }

    /// <summary>
    /// Backs the /realdebuffs statuses command: logs every status currently on the local player,
    /// its real name, and whether RealDebuffs maps it to an effect. Point of this is to answer
    /// "is the debuff I'm looking at actually being detected, under what name" directly instead of
    /// guessing from the visual result alone.
    /// </summary>
    public void LogCurrentStatuses()
    {
        var player = _objectTable.LocalPlayer;
        if (player == null)
        {
            _log.Information("RealDebuffs: no local player right now (not logged in / zoning?).");
            return;
        }

        var lines = new List<string>();
        foreach (var status in player.StatusList)
        {
            if (status.StatusId == 0) continue;
            var mapped = _catalog.TryGetKind(status.StatusId, out var kind) ? kind.ToString() : "unmapped";
            lines.Add($"  #{status.StatusId} \"{_catalog.GetName(status.StatusId)}\" -> {mapped}");
        }

        _log.Information(lines.Count == 0
            ? "RealDebuffs: no active statuses on the local player right now."
            : $"RealDebuffs: {lines.Count} active status(es):\n{string.Join("\n", lines)}");
    }
}
