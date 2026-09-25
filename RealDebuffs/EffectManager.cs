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
/// via <see cref="StatusCatalog"/> (plus any custom Moodles/Loci statuses the user has linked to an
/// effect, via <see cref="CustomStatusWatcher"/>), and draws every enabled effect in a fixed order - so layering
/// (which effect renders "on top" of which) is always consistent no matter which combination of
/// debuffs is currently active. Effects fade in/out smoothly rather than popping on/off, so
/// gaining or losing a status doesn't cause a jarring instant flip.
///
/// A few kinds cover more than one status at different severities (Weakness/Brush with
/// Death/Brink of Death, Frostbite/Deep Freeze, Infatuated/Seduced - see <see cref="DebuffKind.Strengths"/>).
/// Rather than each of those effects needing its own "how strong am I" logic, the SAME 0..1 alpha
/// every effect already takes care of it: EffectManager folds the active status's strength into that
/// alpha before calling Draw, so a fainter-tier status just arrives as a smaller number - the effect
/// itself never needs to know which specific status is behind it. Custom Moodles/Loci rules don't
/// carry a tier of their own, so they always ask for full strength (1.0).
/// </summary>
public sealed class EffectManager
{
    /// <summary>
    /// Fixed draw order = fixed stacking order. Later entries draw on top of earlier ones. Blind is
    /// pinned first/bottom because it covers more of the screen than anything else (a near-total
    /// vignette) - drawn any later it would sit on top of and wash out every other effect. Silence
    /// stays near the end so it renders over Blind - matching "silence on top of the blindfold"
    /// from the spec. The DoT/tint cluster (Poison through Misery below) is grouped together since
    /// they're all a similar "edge vignette + drifting particles" shape and rarely land in ways where
    /// their exact relative order matters. Reorder this list to change layering; add a new
    /// IScreenEffect instance here (plus a DebuffKind and a StatusCatalog.NameMap entry) to extend.
    /// </summary>
    private readonly IScreenEffect[] _order =
    {
        new BlindEffect(),
        new PoisonEffect(),
        new BleedingEffect(),
        new BurnsEffect(),
        new DiseaseEffect(),
        new DropsyEffect(),
        new SludgeEffect(),
        new WindburnEffect(),
        new WeaknessEffect(),
        new InfirmityEffect(),
        new MiseryEffect(),
        new HeavyEffect(),
        new BindEffect(),
        new PacificationEffect(),
        new DoomEffect(),
        new FrostEffect(),
        new PetrificationEffect(),
        new SleepEffect(),
        new AmnesiaEffect(),
        new CharmEffect(),
        new HysteriaEffect(),
        new StunEffect(),
        new ElectrocutionEffect(),
        new ParalysisEffect(),
        new SlowEffect(),
        new VulnerabilityEffect(),
        new SilenceEffect(),
    };

    private const float FadeInPerSecond = 1f / 0.35f;  // ~350ms to fully appear
    private const float FadeOutPerSecond = 1f / 0.6f;  // ~600ms to fully disappear

    private readonly Dictionary<DebuffKind, float> _currentAlpha = new();
    private readonly HashSet<DebuffKind> _activeScratch = new();
    private readonly Dictionary<DebuffKind, float> _targetStrength = new(); // this frame's max severity per kind; see the class doc
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private float _lastTime;

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
                    // If two statuses somehow share a kind at once (e.g. Weakness AND Brink of
                    // Death, however unlikely), show it at whichever is currently more severe.
                    if (!_targetStrength.TryGetValue(kind, out var soFar) || strength > soFar)
                        _targetStrength[kind] = strength;
                }
            }
        }

        // Custom Moodles/Loci statuses feed the very same set of active effects as the real debuffs above, so an
        // effect that's already on from either source is never doubled or restarted - it just stays on until the
        // last thing asking for it goes away.
        //
        // This runs BEFORE the chat-block line below, deliberately: a custom Silence rule now drives the hard
        // chat lockout too, exactly like a real Silence debuff would, because the block is derived from the
        // same _activeScratch set that both sources write into. A custom rule for any OTHER effect still only
        // affects the visual - only Silence has a chat-block consequence.
        if (!suppressed && player != null)
        {
            var snapshot = _customStatuses.Snapshot;
            snapshot.AddActiveKinds(_config.CustomStatusRules, _activeScratch);

            // Custom rules don't carry a severity tier of their own (there's no "faint" vs. "full"
            // version of an arbitrary Moodle) - they always ask for their effect at full strength,
            // same as any real status not listed in DebuffKind.Strengths.
            foreach (var rule in _config.CustomStatusRules)
            {
                if (rule.Enabled && snapshot.Contains(rule.GetKey()))
                    _targetStrength[rule.Kind] = 1f;
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

            float strength = _targetStrength.TryGetValue(effect.Kind, out var targetStrength) ? targetStrength : 1f;

            try
            {
                effect.Draw(dl, screenSize, current * _config.GlobalIntensity * strength, time);
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
    /// The custom (Moodles/Loci) half of /realdebuffs statuses: what the two plugins are reporting
    /// after name-merging, and which effect(s) each status currently maps to - or "no rule". Answers
    /// "why isn't my rule firing" directly: either the name isn't in this list (Moodles/Loci aren't
    /// reporting it, or it's spelled differently) or it is and no rule matches it.
    /// Reflects the most recent once-a-second read, so it can be up to a second behind.
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
        }

        _log.Information($"RealDebuffs: {lines.Count} custom status(es) active ({sources}):\n{string.Join("\n", lines)}");
    }
}