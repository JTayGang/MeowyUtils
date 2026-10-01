using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using RealDebuffs.Effects;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs;

/// <summary>
/// Reads the local player's statuses every frame, maps them to DebuffKinds via StatusCatalog plus
/// any active Moodles/Loci statuses linked via CustomStatusWatcher, and dispatches every enabled
/// effect's Emit call in DrawOrder. Effects fade in/out smoothly rather than popping.
///
/// Timing: the in-game status list is scanned every frame (it's tiny, and a stun that appeared a
/// second late would feel broken). Everything derived from the Moodles/Loci snapshot and the
/// settings is resolved once per CustomStatusWatcher heartbeat, or immediately after a settings
/// edit (Invalidate), rather than every frame.
///
/// The effect roster comes in from Plugin, which gets it from EffectDiscovery (reflection over
/// the assembly, filtered to ISceneEffect implementations, sorted by DrawOrder). EffectManager
/// owns the roster for the session; nothing else constructs effect instances.
/// </summary>
public sealed class EffectManager
{
    private readonly ISceneEffect[] _order;

    private const float FadeInPerSecond = 1f / 0.35f;
    private const float FadeOutPerSecond = 1f / 0.6f;

    /// <summary>
    /// Effects that have thrown during this session. They're skipped for the rest of the session
    /// but left enabled in config - an effect that crashed isn't the user's problem, and mutating
    /// config from inside the draw loop previously leaked the disable across sessions.
    /// </summary>
    private readonly HashSet<DebuffKind> _crashedKinds = new();

    private readonly Dictionary<DebuffKind, float> _currentAlpha = new();
    private readonly HashSet<DebuffKind> _activeScratch = new();
    private readonly Dictionary<DebuffKind, float> _targetStrength = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private float _lastTime;

    // ---- inputs resolved per heartbeat (see ResolveInputs) ----
    // The dictionaries are replaced, never mutated, once published: EffectSceneRenderer keys its
    // material cache on the instance, so a stale in-place edit would go unnoticed.
    private static readonly IReadOnlyDictionary<string, string> NoOverrides = new Dictionary<string, string>();
    private int _resolvedTick = -1;
    private volatile bool _inputsStale = true;
    private CustomStatusSnapshot _snapshot = CustomStatusSnapshot.Empty;
    private DebuffKind[] _ruleKinds = Array.Empty<DebuffKind>();
    private IReadOnlyDictionary<string, string> _materialOverrides = NoOverrides;
    private Dictionary<DebuffKind, Vector4> _configColors = new();

    /// <summary>Per-frame buffer effects emit into. Cleared at the start of Draw, rendered after the loop.</summary>
    private readonly EffectScene _scene = new();

    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly ICondition _condition;
    private readonly IGameGui _gameGui;
    private readonly StatusCatalog _catalog;
    private readonly Configuration _config;
    private readonly ChatBlocker _chatBlocker;
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
                if (_catalog.TryGetEffect(status.StatusId, out var kind, out var strength))
                {
                    _activeScratch.Add(kind);
                    if (!_targetStrength.TryGetValue(kind, out var soFar) || strength > soFar)
                        _targetStrength[kind] = strength;
                }
            }

            // Custom Moodles/Loci rules feed the same _activeScratch set real debuffs do, so an
            // effect already on from either source is never doubled or restarted. Runs BEFORE the
            // chat-block line on purpose: a custom Silence rule then drives the hard chat lockout too.
            foreach (var kind in _ruleKinds)
            {
                _activeScratch.Add(kind);
                _targetStrength[kind] = 1f;
            }

            foreach (var kind in _snapshot.TooltipKinds)
                _activeScratch.Add(kind);
        }

        var materialOverrides = live ? _materialOverrides : NoOverrides;

        _chatBlocker.SetSilenced(_config.SilenceBlocksChat && _activeScratch.Contains(DebuffKind.Silence));

        var screenSize = ImGui.GetIO().DisplaySize;
        if (screenSize.X <= 0 || screenSize.Y <= 0) return;

        var dl = ImGui.GetForegroundDrawList();
        _scene.Clear();

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
                effect.Emit(_scene, screenSize, current * strength, time, dt, color);
            }
            catch (Exception ex)
            {
                _crashedKinds.Add(effect.Kind);
                _log.Error(ex, $"RealDebuffs: {effect.Kind} effect threw during Emit - skipping it for the rest of this session.");
            }
        }

        // Ambient stroke emissions: path-following first (behind), free-flying second (on top).
        StrokeAutoEmitter.Emit(_scene, time, dt, materialOverrides);
        EffectSceneRenderer.Render(dl, _scene, screenSize, time, _config.GlobalIntensity, materialOverrides);
    }

    /// <summary>Call after any settings edit so the next frame re-resolves instead of waiting for the heartbeat.</summary>
    public void Invalidate() => _inputsStale = true;

    /// <summary>
    /// Rebuilds everything derived from the Moodles/Loci snapshot and the settings: which kinds the
    /// name rules light up, the merged material overrides (settings first, tooltip phrases layered
    /// on top), and the settings' color choices. Runs once per heartbeat or edit, not per frame.
    /// </summary>
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
        _materialOverrides = overrides;

        var colors = new Dictionary<DebuffKind, Vector4>();
        foreach (var (kind, name) in _config.ColorOverrides)
        {
            if (TooltipKeywordParser.TryResolveColorToken(name, out var rgb))
                colors[kind] = rgb;
        }
        _configColors = colors;
    }

    /// <summary>
    /// Priority order for an effect's color override:
    ///   1. Tooltip-derived color (context-specific; only while custom statuses are live).
    ///   2. Dev-tester forced color (explicit "show me this, now").
    ///   3. Config override (the Effect generator's Color dropdown, a persistent baseline).
    /// Returning null means the effect shows in its own authored palette.
    /// </summary>
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

    /// <summary>
    /// Backs /realdebuffs statuses: logs every status currently on the local player, its real
    /// name, and whether RealDebuffs maps it to an effect.
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
            string mapped;
            if (_catalog.TryGetEffect(status.StatusId, out var kind, out var strength))
                mapped = strength < 0.999f ? $"{kind} ({strength:P0} intensity)" : kind.ToString();
            else
                mapped = "unmapped";
            lines.Add($"  #{status.StatusId} \"{_catalog.GetName(status.StatusId)}\" -> {mapped}");
        }

        _log.Information(lines.Count == 0
            ? "RealDebuffs: no active statuses on the local player right now."
            : $"RealDebuffs: {lines.Count} active status(es):\n{string.Join("\n", lines)}");

        LogCustomStatuses();
    }

    /// <summary>
    /// The Moodles/Loci half of /realdebuffs statuses: what the two plugins report after
    /// name-merging, which name-rule(s) each status matches, and - when tooltip parsing is on -
    /// what TooltipKeywordRules find in its description.
    /// </summary>
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

            if (status.TooltipMatches.Count == 0)
            {
                lines.Add("    tooltip: no keyword matches");
                continue;
            }

            foreach (var m in status.TooltipMatches)
            {
                var colorText = m.Color is { } col
                    ? $"color #{(int)(col.X * 255):X2}{(int)(col.Y * 255):X2}{(int)(col.Z * 255):X2} (from {m.ColorSource})"
                    : "no color override";
                var matText = m.MaterialSubstitution is { } mat ? $", material {mat}" : "";
                lines.Add($"    tooltip -> {m.Kind}, {colorText}{matText}");
            }
        }

        _log.Information($"RealDebuffs: {lines.Count} custom status line(s) ({sources}):\n{string.Join("\n", lines)}");
    }
}