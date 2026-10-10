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

    /// <summary>Multiplies every effect's alpha/intensity, between MinIntensity and MaxIntensity.</summary>
    public float GlobalIntensity { get; set; } = MaxIntensity;

    public const float MinIntensity = 0.1f;
    public const float MaxIntensity = 1.75f;

    /// <summary>Effects the user has toggled off. Membership means disabled; absence means enabled.</summary>
    public HashSet<DebuffKind> DisabledKinds { get; set; } = new();

    public bool IsEnabled(DebuffKind kind) => !DisabledKinds.Contains(kind);

    public void SetEnabled(DebuffKind kind, bool enabled)
    {
        if (enabled) DisabledKinds.Remove(kind);
        else         DisabledKinds.Add(kind);
    }

    /// <summary>Material overrides. Key "{Kind}.{Stroke|Particle}.{Role}" or "{Kind}.Region.{EdgeGlow|FlatFill}" -> MaterialRegistry name.</summary>
    public Dictionary<string, string> MaterialOverrides { get; set; } = new();

    /// <summary>Per-effect color overrides, keyed by DebuffKind; value is a color word from NamedColors.</summary>
    public Dictionary<DebuffKind, string> ColorOverrides { get; set; } = new();

    /// <summary>"While I have this custom status, show this effect" links.</summary>
    public List<CustomStatusRule> CustomStatusRules { get; set; } = new();

    /// <summary>Also scan each active status's tooltip for <see cref="TooltipKeywordRules"/>. Off by default.</summary>
    public bool ParseCustomStatusTooltips { get; set; }

    /// <summary>"If a status's tooltip contains this word, show this effect" links.</summary>
    public List<TooltipKeywordRule> TooltipKeywordRules { get; set; } = new();

    /// <summary>Kinds whose default keyword rule was already seeded (never re-seeded, so a deleted rule stays deleted). Reset clears this.</summary>
    public HashSet<DebuffKind> SeededKinds { get; set; } = new();

    /// <summary>OFF by default: actually stop outgoing chat while silenced. See ChatBlocker.cs.</summary>
    public bool SilenceBlocksChat { get; set; }
}

/// <summary>One "while I have THIS custom status, show THAT effect" link.</summary>
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

    public bool Enabled { get; set; } = true;

    /// <summary>Cached key; EffectManager asks every frame.</summary>
    public string GetKey() => _key ??= StatusNames.Key(_name);
}

/// <summary>One "if a status's tooltip contains any of these words, show that effect" link.</summary>
public sealed class TooltipKeywordRule
{
    private string _keywords = "";
    private string[]? _parsed;
    private Regex? _pattern;

    /// <summary>Comma/newline/semicolon-separated whole-word, case-insensitive matches; blanks and duplicates dropped.</summary>
    public string Keywords
    {
        get => _keywords;
        set { _keywords = value ?? ""; _parsed = null; _pattern = null; }
    }

    public DebuffKind Kind { get; set; } = DebuffKind.Bind;

    public bool Enabled { get; set; } = true;

    public IReadOnlyList<string> ParsedKeywords => _parsed ??= ParseKeywords(_keywords);

    /// <summary>Cached; invalidated by Keywords.</summary>
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

    public static string[] ParseKeywords(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw
            .Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static TooltipKeywordRule DefaultRuleFor(ISceneEffect effect) => new()
    {
        Keywords = string.Join(", ", effect.TriggerKeywords),
        Kind = effect.Kind,
    };

    /// <summary>Once per session: seed each unseeded effect's default rule unless the user already has one for that kind. Returns true if changed.</summary>
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

    /// <summary>Wipes user rules and the seeded set, then re-seeds every effect from TriggerKeywords.</summary>
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