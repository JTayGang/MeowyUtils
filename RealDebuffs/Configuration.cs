using System.Text.RegularExpressions;
using Dalamud.Configuration;
using RealDebuffs.Effects;

namespace RealDebuffs;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;
    public bool HideDuringCutscenes { get; set; } = true;

    /// <summary>Multiplies every effect's alpha/intensity, between MinIntensity and MaxIntensity. Defaults to the max.</summary>
    public float GlobalIntensity { get; set; } = MaxIntensity;

    public const float MinIntensity = 0.1f;
    public const float MaxIntensity = 1.75f;

    /// <summary>
    /// Effects the user has toggled off. Membership means disabled; absence means enabled. A
    /// set rather than one bool per kind, so adding a new DebuffKind requires no change here -
    /// a new kind is simply "not in the set" and therefore on by default. The per-kind label
    /// and description still live in the settings panel.
    /// </summary>
    public HashSet<DebuffKind> DisabledKinds { get; set; } = new();

    public bool IsEnabled(DebuffKind kind) => !DisabledKinds.Contains(kind);

    public void SetEnabled(DebuffKind kind, bool enabled)
    {
        if (enabled) DisabledKinds.Remove(kind);
        else         DisabledKinds.Add(kind);
    }

    /// <summary>
    /// Per-slot material overrides for ported effects. Key format is
    /// "{DebuffKind}.{Stroke|Particle}.{PrimitiveRole}" or "{DebuffKind}.Region.{EdgeGlow|FlatFill}";
    /// value is a material name from MaterialRegistry (e.g. "particle.ember"). Missing entries
    /// fall back to BuiltInDefaults. See Effect Styles in the Moodles/Loci Support tab.
    /// </summary>
    public Dictionary<string, string> MaterialOverrides { get; set; } = new();

    /// <summary>
    /// Per-effect color overrides applied when no tooltip-derived or dev-test color is present.
    /// Key is the DebuffKind; value is a color word from
    /// <see cref="TooltipKeywordParser.NamedColors"/> (e.g. "azure"). Resolved to a Vector4 at
    /// render time via <see cref="TooltipKeywordParser.TryResolveColorToken"/>. Set from the
    /// Effect generator panel's Color dropdown; cleared by that panel's Reset button.
    /// </summary>
    public Dictionary<DebuffKind, string> ColorOverrides { get; set; } = new();

    /// <summary>"While I have this custom status, show this effect" links - see <see cref="CustomStatusRule"/>.</summary>
    public List<CustomStatusRule> CustomStatusRules { get; set; } = new();

    /// <summary>Also scan each active status's tooltip text for <see cref="TooltipKeywordRules"/>. Off by default.</summary>
    public bool ParseCustomStatusTooltips { get; set; }

    /// <summary>
    /// "If a status's tooltip contains this word, show this effect" links - see
    /// <see cref="TooltipKeywordRule"/>. Only consulted while <see cref="ParseCustomStatusTooltips"/>
    /// is on. Seeded at load from each effect's TriggerKeywords; see
    /// <see cref="TooltipKeywordRule.SeedNewEffects"/>.
    /// </summary>
    public List<TooltipKeywordRule> TooltipKeywordRules { get; set; } = new();

    /// <summary>
    /// Kinds whose default tooltip-keyword rule has already been seeded into TooltipKeywordRules.
    /// New effects are added automatically on load; a kind already in here is never re-seeded, so a
    /// user who deliberately deleted a rule keeps it deleted. "Reset to defaults" clears this.
    /// </summary>
    public HashSet<DebuffKind> SeededKinds { get; set; } = new();

    /// <summary>OFF by default: actually stop outgoing chat while silenced. See ChatBlocker.cs.</summary>
    public bool SilenceBlocksChat { get; set; }

}

/// <summary>
/// One "while I have THIS custom status, show THAT effect" link. Matched by title - the only thing
/// a user can read off the screen and that a mirror plugin carries across unchanged. Rules can
/// overlap freely (one name -> several effects, several names -> one effect); EffectManager
/// dedupes via a set, so nothing stacks.
/// </summary>
public class CustomStatusRule
{
    private string _name = "";
    private string? _key;

    /// <summary>The status title to watch for, as the user typed or picked it.</summary>
    public string Name
    {
        get => _name;
        set { _name = value ?? ""; _key = null; }
    }

    public DebuffKind Kind { get; set; } = DebuffKind.Bind;

    /// <summary>Lets a rule be switched off without deleting it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Cached key; EffectManager asks every frame. Method, not property, so it isn't saved.</summary>
    public string GetKey() => _key ??= StatusNames.Key(_name);
}

/// <summary>
/// One "if a status's tooltip contains any of these words, show that effect" link. A rule can
/// carry several comma-separated keywords - they all drive the same Kind. Rules can freely name
/// the same Kind; the parser still produces at most one result per kind.
/// </summary>
public sealed class TooltipKeywordRule
{
    private string _keywords = "";
    private string[]? _parsed;
    private Regex? _pattern;

    /// <summary>
    /// Comma/newline/semicolon-separated words or short phrases. Each is matched as a whole word
    /// (word boundaries both sides) case-insensitively. Blanks and duplicates are dropped.
    /// </summary>
    public string Keywords
    {
        get => _keywords;
        set { _keywords = value ?? ""; _parsed = null; _pattern = null; }
    }

    public DebuffKind Kind { get; set; } = DebuffKind.Bind;

    public bool Enabled { get; set; } = true;

    /// <summary>Cached. Invalidated by the Keywords setter.</summary>
    public IReadOnlyList<string> ParsedKeywords => _parsed ??= ParseKeywords(_keywords);

    /// <summary>
    /// One word-boundary, case-insensitive alternation over every keyword, or null if there are
    /// none. Cached (method, not property, so it isn't saved) and invalidated by Keywords.
    /// </summary>
    public Regex? GetPattern()
    {
        if (_pattern != null) return _pattern;
        var parsed = ParsedKeywords;
        if (parsed.Count == 0) return null;

        string alternation = parsed.Count == 1
            ? Regex.Escape(parsed[0])
            : "(?:" + string.Join("|", parsed.Select(Regex.Escape)) + ")";

        return _pattern = new Regex($@"\b{alternation}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>Splits a raw keyword box into non-blank, de-duplicated (case-insensitive) entries.</summary>
    public static string[] ParseKeywords(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw
            .Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>The default rule for an effect: its own declared TriggerKeywords, comma-joined.</summary>
    private static TooltipKeywordRule DefaultRuleFor(ISceneEffect effect) => new()
    {
        Keywords = string.Join(", ", effect.TriggerKeywords),
        Kind = effect.Kind,
    };

    /// <summary>
    /// Option-C merge pass, called once per session from Plugin after effect discovery. For every
    /// effect whose kind isn't already in <see cref="Configuration.SeededKinds"/>: mark it seeded,
    /// and - only if the user has no rule for that kind yet - add the effect's default rule. A rule
    /// the user deleted stays deleted (the kind is seeded, so we don't touch it); a newly-added
    /// effect gets a rule automatically (the kind isn't seeded yet). Returns true if config changed.
    /// </summary>
    public static bool SeedNewEffects(Configuration config, IReadOnlyList<ISceneEffect> effects)
    {
        bool changed = false;
        var rules = config.TooltipKeywordRules;

        foreach (var effect in effects)
        {
            if (config.SeededKinds.Contains(effect.Kind)) continue;
            config.SeededKinds.Add(effect.Kind);
            changed = true;

            if (effect.TriggerKeywords.Count == 0) continue;
            if (rules.Any(r => r.Kind == effect.Kind)) continue;

            rules.Add(DefaultRuleFor(effect));
        }

        return changed;
    }

    /// <summary>
    /// "Reset to defaults": wipe the user's rules and the seeded set, then re-seed every effect
    /// from its declared TriggerKeywords in one pass.
    /// </summary>
    public static void ResetToDefaults(Configuration config, IReadOnlyList<ISceneEffect> effects)
    {
        config.TooltipKeywordRules.Clear();
        config.SeededKinds.Clear();

        foreach (var effect in effects)
        {
            config.SeededKinds.Add(effect.Kind);
            if (effect.TriggerKeywords.Count == 0) continue;
            config.TooltipKeywordRules.Add(DefaultRuleFor(effect));
        }
    }
}
