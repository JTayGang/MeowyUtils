using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using RealDebuffs.Effects;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs;

/// <summary>Maps the player's statuses to DebuffKinds each frame and draws enabled effects in DrawOrder.</summary>
public sealed class EffectManager
{
    private readonly ISceneEffect[] _order;

    /// <summary>How long an effect takes to fade fully in from nothing.</summary>
    private const float FadeInPerSecond = 1f / CastTracker.FadeInSeconds;
    private const float FadeOutPerSecond = 1f / 0.6f;

    /// <summary>Effects that have thrown this session; skipped until reload. Config is left untouched.</summary>
    private readonly HashSet<DebuffKind> _crashedKinds = new();

    private readonly Dictionary<DebuffKind, float> _currentAlpha = new();
    private readonly HashSet<DebuffKind> _activeScratch = new();
    private readonly Dictionary<DebuffKind, float> _targetStrength = new();
    private readonly Dictionary<DebuffKind, int> _stacks = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private float _lastTime;

    // Per-heartbeat inputs; dictionaries are replaced, never mutated (the scene caches by instance).
    private static readonly IReadOnlyDictionary<string, string> NoOverrides = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<DebuffKind, Vector4> NoModifierColors = new Dictionary<DebuffKind, Vector4>();
    private int _resolvedTick = -1;
    private volatile bool _inputsStale = true;
    private CustomStatusSnapshot _snapshot = CustomStatusSnapshot.Empty;
    private DebuffKind[] _ruleKinds = Array.Empty<DebuffKind>();
    private IReadOnlyDictionary<string, string> _materialOverrides = NoOverrides;
    private IReadOnlyDictionary<DebuffKind, Vector4> _modifierColors = NoModifierColors;
    private int _seenDebugVersion;
    private Dictionary<DebuffKind, Vector4> _configColors = new();

    /// <summary>Per-frame buffer effects emit into, then the shed and renderer work through.</summary>
    private readonly EffectScene _scene = new();
    private readonly StrokeShed _shed = new();
    private readonly SceneRenderer _renderer = new();

    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly IGameGui _gameGui;
    private readonly StatusCatalog _catalog;
    private readonly Configuration _config;
    private readonly ChatBlocker _chatBlocker;
    private int _seenBlocked;
    private readonly CustomStatusWatcher _customStatuses;
    private readonly IPluginLog _log;

    public EffectManager(
        IReadOnlyList<ISceneEffect> effects,
        IClientState clientState, IObjectTable objectTable, ICondition condition, IGameGui gameGui,
        StatusCatalog catalog, Configuration config, ChatBlocker chatBlocker,
        CustomStatusWatcher customStatuses, IPluginLog log)
    {
        _order = effects.ToArray();
        _clientState = clientState;
        _objectTable = objectTable;
        _condition = condition;
        _gameGui = gameGui;
        _catalog = catalog;
        _config = config;
        _chatBlocker = chatBlocker;
        _customStatuses = customStatuses;
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
        _targetStrength.Clear();
        _stacks.Clear();

        bool suppressed = !_config.Enabled
            || _gameGui.GameUiHidden
            || _clientState.IsGPosing
            || (_config.HideDuringCutscenes && (
                _condition[ConditionFlag.WatchingCutscene] ||
                _condition[ConditionFlag.WatchingCutscene78] ||
                _condition[ConditionFlag.OccupiedInCutSceneEvent] ||
                _condition[ConditionFlag.CreatingCharacter]));

        // Tick before snapshot: if a beat lands in between we re-sync next frame, never miss one.
        int tick = _customStatuses.Tick;
        DebugTester.ExpirePreviews();
        if (DebugTester.Version != _seenDebugVersion)
        {
            _seenDebugVersion = DebugTester.Version;
            _inputsStale = true;
        }
        if (_inputsStale || tick != _resolvedTick)
        {
            _inputsStale = false;
            _resolvedTick = tick;
            ResolveInputs(_customStatuses.Snapshot);
        }

        var player = _objectTable.LocalPlayer;
        bool live = !suppressed && player != null;
        if (live)
        {
            foreach (var status in player!.StatusList)
            {
                if (status.StatusId == 0) continue;
                if (_catalog.TryGetEffect(status.StatusId, out var kind, out var strength, out var maxStacks))
                {
                    _activeScratch.Add(kind);
                    if (!_targetStrength.TryGetValue(kind, out var soFar) || strength > soFar)
                        _targetStrength[kind] = strength;

                    // Every status that maps to a kind adds its stacks: two vulnerabilities are worse than one.
                    _stacks[kind] = _stacks.GetValueOrDefault(kind) + StatusCatalog.StacksOf(status.Param, maxStacks);
                }
            }

            // Custom rules join the real debuffs before the chat-block check, so a custom Silence also locks chat.
            foreach (var kind in _ruleKinds)
            {
                _activeScratch.Add(kind);
                _targetStrength[kind] = 1f;
            }

            foreach (var kind in _snapshot.TooltipKinds)
                _activeScratch.Add(kind);
        }

        var materialOverrides = live ? _materialOverrides : NoOverrides;
        var modifierColors = live ? _modifierColors : NoModifierColors;

        _chatBlocker.SetSilenced(_config.SilenceBlocksChat && _activeScratch.Contains(DebuffKind.Silence));

        // Read even when nothing listens, so a block is never replayed later.
        int blockedNow = _chatBlocker.BlockedCount;
        bool chatBlocked = blockedNow != _seenBlocked;
        _seenBlocked = blockedNow;

        var screenSize = ImGui.GetIO().DisplaySize;
        if (screenSize.X < 64f || screenSize.Y < 64f) return;

        var dl = ImGui.GetForegroundDrawList();
        _scene.Clear();
        _scene.ScreenSize = screenSize;
        _scene.Overrides = materialOverrides;
        _scene.ModifierColors = modifierColors;

        foreach (var effect in _order)
        {
            if (_crashedKinds.Contains(effect.Kind)) continue;

            bool active = !suppressed && _config.IsEnabled(effect.Kind)
                && (_activeScratch.Contains(effect.Kind) || DebugTester.IsForced(effect.Kind));
            float target = active ? 1f : 0f;
            float rate = active ? FadeInPerSecond : FadeOutPerSecond;
            float current = MoveTowards(_currentAlpha[effect.Kind], target, rate * dt);
            _currentAlpha[effect.Kind] = current;

            if (current <= 0.001f) continue;

            float strength = _targetStrength.TryGetValue(effect.Kind, out var targetStrength) ? targetStrength : 1f;
            Vector4? color = ResolveColorOverride(effect.Kind, live);

            _scene.CurrentOwner = effect.Kind;

            try
            {
                if (chatBlocked && effect is IReactsToBlockedChat reactsToChat)
                    reactsToChat.OnChatBlocked(time);

                if (effect is IReceivesStacks stacked)
                    stacked.SetStacks(DebugTester.IsForced(effect.Kind) ? DebugTester.GetForcedStacks(effect.Kind) : Math.Max(1, _stacks.GetValueOrDefault(effect.Kind)));

                effect.Emit(_scene, screenSize, current * strength, time, dt, color);
            }
            catch (Exception ex)
            {
                _crashedKinds.Add(effect.Kind);
                _log.Error(ex, $"RealDebuffs: {effect.Kind} effect threw during Emit or OnChatBlocked - skipping it for the rest of this session.");
            }
        }

        _shed.Update(_scene, time, dt);
        _renderer.Render(dl, _scene, screenSize, time, _config.GlobalIntensity);
    }

    /// <summary>Call after any settings edit so the next frame re-resolves instead of waiting for the heartbeat.</summary>
    public void Invalidate() => _inputsStale = true;

    /// <summary>Rebuilds everything derived from the Moodles/Loci snapshot and the settings.</summary>
    private void ResolveInputs(CustomStatusSnapshot snapshot)
    {
        _snapshot = snapshot;

        var ruleKinds = new List<DebuffKind>();
        foreach (var rule in _config.CustomStatusRules)
        {
            if (rule.Enabled && snapshot.Contains(rule.GetKey()) && !ruleKinds.Contains(rule.Kind))
                ruleKinds.Add(rule.Kind);
        }
        _ruleKinds = ruleKinds.ToArray();

        var overrides = new Dictionary<string, string>(_config.MaterialOverrides, StringComparer.Ordinal);
        foreach (var kv in snapshot.TooltipMaterialOverrides)
            overrides[kv.Key] = kv.Value;

        // A preview from the generator shows its whole look (materials and both colours), over anything a status asks for.
        var previewColors = new Dictionary<DebuffKind, Vector4>();
        long now = Environment.TickCount64;
        foreach (var look in DebugTester.ActiveLooks())
        {
            LookApply.Overrides(look, overrides);
            if (look.ModifierColor is { } previewModifier) previewColors[look.Kind] = previewModifier.Resolve(now);
        }
        _materialOverrides = overrides;

        var colors = new Dictionary<DebuffKind, Vector4>();
        foreach (var (kind, name) in _config.ColorOverrides)
        {
            if (ColorLexicon.TryToken(name, out Vector4 rgb))
                colors[kind] = rgb;
        }
        _configColors = colors;

        var modifierColors = new Dictionary<DebuffKind, Vector4>();
        foreach (var (kind, name) in _config.ModifierColorOverrides)
        {
            if (ColorLexicon.TryToken(name, out Vector4 rgb))
                modifierColors[kind] = rgb;
        }
        foreach (var kv in snapshot.TooltipModifierColors)
            modifierColors[kv.Key] = kv.Value;
        foreach (var kv in previewColors)
            modifierColors[kv.Key] = kv.Value;
        _modifierColors = modifierColors;
    }

    /// <summary>Colour priority: tooltip (live only), dev-tester, config override; null = the effect's own palette.</summary>
    private Vector4? ResolveColorOverride(DebuffKind kind, bool live)
    {
        if (live && _snapshot.TooltipColors.TryGetValue(kind, out var tooltipColor))
            return tooltipColor;

        if (DebugTester.GetForcedColor(kind) is { } forcedColor)
            return forcedColor;

        return _configColors.TryGetValue(kind, out var configColor) ? configColor : null;
    }

    private static float MoveTowards(float current, float target, float maxDelta)
    {
        if (MathF.Abs(target - current) <= maxDelta) return target;
        return current + MathF.Sign(target - current) * maxDelta;
    }

    /// <summary>Backs /realdebuffs statuses: logs every active status and whether it maps to an effect.</summary>
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
            string mapped;
            if (_catalog.TryGetEffect(status.StatusId, out var kind, out var strength, out var maxStacks))
            {
                mapped = strength < 0.999f ? $"{kind} ({strength:P0} intensity)" : kind.ToString();
                if (maxStacks > 1) mapped += $", {StatusCatalog.StacksOf(status.Param, maxStacks)} of {maxStacks} stacks (param {status.Param})";
            }
            else
                mapped = "unmapped";
            lines.Add($"  #{status.StatusId} \"{_catalog.GetName(status.StatusId)}\" -> {mapped}");
        }

        _log.Information(lines.Count == 0
            ? "RealDebuffs: no active statuses on the local player right now."
            : $"RealDebuffs: {lines.Count} active status(es):\n{string.Join("\n", lines)}");

        LogCustomStatuses();
    }

    /// <summary>Moodles/Loci half of /realdebuffs statuses: active custom statuses and their matches.</summary>
    private void LogCustomStatuses()
    {
        var sources = $"Moodles: {(_customStatuses.MoodlesAvailable ? "connected" : "not found")}, " +
                      $"Loci: {(_customStatuses.LociAvailable ? "connected" : "not found")}";

        var statuses = _customStatuses.Snapshot.Statuses;
        if (statuses.Count == 0)
        {
            _log.Information($"RealDebuffs: no custom statuses active ({sources}).");
            return;
        }

        var lines = new List<string>();
        foreach (var status in statuses)
        {
            var kinds = new List<string>();
            foreach (var rule in _config.CustomStatusRules)
            {
                var kind = rule.Kind.ToString();
                if (rule.Enabled && rule.GetKey() == status.Key && !kinds.Contains(kind))
                    kinds.Add(kind);
            }

            lines.Add($"  \"{status.Name}\" [{status.Sources}] -> {(kinds.Count == 0 ? "no rule" : string.Join(", ", kinds))}");

            if (!_config.ParseCustomStatusTooltips) continue;

            if (status.Description.Length == 0)
            {
                lines.Add("    (no tooltip text)");
                continue;
            }

            if (status.Language.Looks.Count == 0)
                lines.Add("    tooltip: nothing recognised");

            foreach (var look in status.Language.Looks)
                lines.Add($"    tooltip -> {LookText.Describe(look)}");
            foreach (var note in status.Language.Notes)
                lines.Add($"    note: {note}");
            foreach (var hint in status.Language.Hints)
                lines.Add($"    hint: {hint}");
        }

        _log.Information($"RealDebuffs: {lines.Count} custom status line(s) ({sources}):\n{string.Join("\n", lines)}");
    }
}