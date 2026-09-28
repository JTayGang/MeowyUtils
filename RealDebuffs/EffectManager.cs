using System;
using System.Collections.Generic;
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
/// effect's Emit call in a fixed order. Effects fade in/out smoothly rather than popping.
///
/// Every effect implements ISceneEffect: it writes primitives into a shared EffectScene and the
/// framework renders the whole frame at once after the loop. This means the vignette is chosen
/// once per frame (no stacking), each primitive's material can be swapped independently, and
/// legacy draw calls no longer exist anywhere in the plugin.
///
/// Strength: kinds that cover several severities fold the active status's strength into the same
/// 0..1 alpha every effect takes. Custom rules ask for 1.0.
///
/// Color: a real debuff or name-based CustomStatusRule never overrides an effect's authored color.
/// A TooltipKeywordRule match can (see TooltipKeywordParser). Effects receive it via the
/// colorOverride argument and attach it to their primitives; the renderer pushes it around each
/// material call. If two sources share a kind at once, strength and color are tracked per-KIND
/// rather than per-source - a rare edge case accepted as a simplification.
///
/// Material overrides: the settings panel exposes per-slot material choices via
/// Configuration.MaterialOverrides, and tooltip descriptions can inject substitutions via
/// TooltipMaterialOverrides (see CustomStatusSnapshot). Both are merged into one dict per frame
/// and handed to the renderer, keyed by "{Kind}.{Type}.{Role}".
/// </summary>
public sealed class EffectManager
{
    /// <summary>
    /// Fixed draw order = stacking order; earlier entries underneath, later on top. The order is
    /// chosen for the shipped effects; reorder to change layering once those exist. Suggested
    /// layering (bottom to top): Blind, DoT/tint cluster, Heavy, Bind, Pacification, Doom, Frost,
    /// Petrification, Sleep, Amnesia, Charm, Hysteria, Stun, Electrocution, Paralysis, Slow,
    /// Vulnerability, Silence.
    /// </summary>
    private readonly ISceneEffect[] _order =
    {
        new BlindEffect(),
        new BurnsEffect(),
        new HeavyEffect(),
        new FrostEffect(),
        new DiseaseEffect(),
        new VulnerabilityEffect(),
    };

    private const float FadeInPerSecond = 1f / 0.35f;
    private const float FadeOutPerSecond = 1f / 0.6f;

    private readonly Dictionary<DebuffKind, float> _currentAlpha = new();
    private readonly HashSet<DebuffKind> _activeScratch = new();
    private readonly Dictionary<DebuffKind, float> _targetStrength = new();
    private readonly Dictionary<DebuffKind, Vector4> _colorOverrides = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private float _lastTime;

    /// <summary>
    /// Per-frame merged material overrides. Global config entries are merged first, then
    /// tooltip-derived entries layered on top (more specific intent wins). Handed to the renderer.
    /// </summary>
    private readonly Dictionary<string, string> _materialOverridesScratch = new(StringComparer.Ordinal);

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
        IClientState clientState, IObjectTable objectTable, ICondition condition, IGameGui gameGui,
        StatusCatalog catalog, Configuration config, ChatBlocker chatBlocker,
        CustomStatusWatcher customStatuses, IPluginLog log)
    {
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
        _colorOverrides.Clear();
        _materialOverridesScratch.Clear();

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
                if (_catalog.TryGetEffect(status.StatusId, out var kind, out var strength))
                {
                    _activeScratch.Add(kind);
                    if (!_targetStrength.TryGetValue(kind, out var soFar) || strength > soFar)
                        _targetStrength[kind] = strength;
                }
            }
        }

        // Custom Moodles/Loci rules feed the same _activeScratch set real debuffs do, so an effect
        // already on from either source is never doubled or restarted - it stays on until the last
        // thing asking for it goes away. Runs BEFORE the chat-block line on purpose: a custom
        // Silence rule then drives the hard chat lockout exactly like a real Silence debuff.
        if (!suppressed && player != null)
        {
            var snapshot = _customStatuses.Snapshot;
            snapshot.AddActiveKinds(_config.CustomStatusRules, _activeScratch);

            foreach (var rule in _config.CustomStatusRules)
            {
                if (rule.Enabled && snapshot.Contains(rule.GetKey()))
                    _targetStrength[rule.Kind] = 1f;
            }

            // Tooltip keyword matches are precomputed once per snapshot refresh (see
            // CustomStatusSnapshot); this just merges the already-computed answer.
            foreach (var kind in snapshot.TooltipKinds)
                _activeScratch.Add(kind);

            foreach (var (kind, color) in snapshot.TooltipColors)
            {
                if (!_colorOverrides.ContainsKey(kind))
                    _colorOverrides[kind] = color;
            }

            // Merge material overrides: global config first, then tooltip-derived on top.
            // The scratch dict was cleared at the top of Draw, so this is a fresh fill each frame.
            foreach (var kv in _config.MaterialOverrides)
                _materialOverridesScratch[kv.Key] = kv.Value;
            foreach (var kv in snapshot.TooltipMaterialOverrides)
                _materialOverridesScratch[kv.Key] = kv.Value;
        }

        _chatBlocker.SetSilenced(_config.SilenceBlocksChat && _activeScratch.Contains(DebuffKind.Silence));

        var screenSize = ImGui.GetIO().DisplaySize;
        if (screenSize.X <= 0 || screenSize.Y <= 0) return;

        var dl = ImGui.GetForegroundDrawList();
        _scene.Clear();

        foreach (var effect in _order)
        {
            bool active = !suppressed && _config.IsEnabled(effect.Kind) && _activeScratch.Contains(effect.Kind);
            active |= !suppressed && _config.IsEnabled(effect.Kind) && DebugTester.IsForced(effect.Kind); // TEST-TOOLS: delete this line (and DebugTester.cs) to remove the test panel
            float target = active ? 1f : 0f;
            float rate = active ? FadeInPerSecond : FadeOutPerSecond;
            float current = MoveTowards(_currentAlpha[effect.Kind], target, rate * dt);
            _currentAlpha[effect.Kind] = current;

            if (current <= 0.001f) continue;

            float strength = _targetStrength.TryGetValue(effect.Kind, out var targetStrength) ? targetStrength : 1f;

            // A forced test (DebugTester) can also carry a preview color. A real tooltip match
            // wins if both are present for the same kind.
            Vector4? color = _colorOverrides.TryGetValue(effect.Kind, out var c) ? c : DebugTester.GetForcedColor(effect.Kind); // TEST-TOOLS: trim to `_colorOverrides.TryGetValue(...) ? color : (Vector4?)null` if you remove DebugTester.cs

            _scene.CurrentOwner = effect.Kind;

            try
            {
                // GlobalIntensity is applied once by the renderer via MaterialContext.Alpha, so
                // the effect receives fade * strength only - not multiplied by intensity a second
                // time. See EffectSceneRenderer.Render.
                effect.Emit(_scene, screenSize, current * strength, time, color);
            }
            catch (Exception ex)
            {
                _log.Error(ex, $"RealDebuffs: {effect.Kind} effect threw during Emit - disabling it for the rest of this session.");
                _config.SetEnabled(effect.Kind, false); // in-memory only, not saved
            }
        }

        // Ambient stroke emissions: path-following first (behind), free-flying second (on top).
        StrokeFlowEmitter.Emit(_scene, time, dt, _materialOverridesScratch);
        StrokeAutoEmitter.Emit(_scene, time, dt, _materialOverridesScratch);
        EffectSceneRenderer.Render(dl, _scene, screenSize, time, _config.GlobalIntensity, _materialOverridesScratch);
    }

    private static float MoveTowards(float current, float target, float maxDelta)
    {
        if (MathF.Abs(target - current) <= maxDelta) return target;
        return current + MathF.Sign(target - current) * maxDelta;
    }

    /// <summary>
    /// Backs /realdebuffs statuses: logs every status currently on the local player, its real name,
    /// and whether RealDebuffs maps it to an effect - so "is the debuff I'm looking at actually
    /// being detected, under what name" is answerable directly rather than by guessing from the
    /// visual result.
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
    /// what TooltipKeywordRules find in its description and what color (if any) that resolved to.
    /// Reflects the most recent once-a-second read.
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