using Dalamud.Configuration;
using RealDebuffs.Effects;

namespace RealDebuffs;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

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

    /// <summary>Material overrides: "{Kind}.{Stroke|Particle}.{Role}" or "{Kind}.Region.{EdgeGlow|FlatFill}" to a registry name.</summary>
    public Dictionary<string, string> MaterialOverrides { get; set; } = new();

    /// <summary>Per-effect color overrides, keyed by DebuffKind; value is a color word from ColorLexicon.Named.</summary>
    public Dictionary<DebuffKind, string> ColorOverrides { get; set; } = new();

    /// <summary>Per-effect color for the modifier layer (the generator's second colour); absent means it follows ColorOverrides.</summary>
    public Dictionary<DebuffKind, string> ModifierColorOverrides { get; set; } = new();

    /// <summary>"While I have this custom status, show this effect" links.</summary>
    public List<CustomStatusRule> CustomStatusRules { get; set; } = new();

    /// <summary>Also scan each active status's tooltip for TooltipKeywordRules. Off by default.</summary>
    public bool ParseCustomStatusTooltips { get; set; }

    /// <summary>Extra trigger words on top of each effect's built-in ones ("If a status's tooltip contains this word, show this effect").</summary>
    public List<TooltipKeywordRule> TooltipKeywordRules { get; set; } = new();

    /// <summary>Effects whose built-in trigger words are switched off; their extra words still apply.</summary>
    public HashSet<DebuffKind> TooltipOffKinds { get; set; } = new();

    /// <summary>Built-in trigger words the user switched off, per effect.</summary>
    public Dictionary<DebuffKind, List<string>> MutedWords { get; set; } = new();

    /// <summary>Version 1 only: kinds whose default rule was seeded into TooltipKeywordRules; read once by TooltipKeywordRule.Migrate.</summary>
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

/// <summary>One "if a status's tooltip contains any of these words, show that effect" link: the user's extras on top of the built-in words.</summary>
public sealed class TooltipKeywordRule
{
    private string _keywords = "";
    private string[]? _parsed;

    /// <summary>Comma/newline/semicolon-separated whole-word, case-insensitive matches; blanks and duplicates dropped.</summary>
    public string Keywords
    {
        get => _keywords;
        set { _keywords = value ?? ""; _parsed = null; }
    }

    public DebuffKind Kind { get; set; } = DebuffKind.Bind;

    public bool Enabled { get; set; } = true;

    public IReadOnlyList<string> ParsedKeywords => _parsed ??= ParseKeywords(_keywords);

    public static string[] ParseKeywords(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        return raw
            .Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Version 1 seeded each effect's words into editable rules; carry over deleted/disabled rules as switched off, removed words as muted, added words as extras.</summary>
    public static bool Migrate(Configuration config, IReadOnlyList<ISceneEffect> effects)
    {
        if (config.Version >= 2) return false;

        foreach (var effect in effects)
        {
            if (effect.TriggerKeywords.Count == 0 || !config.SeededKinds.Contains(effect.Kind)) continue;

            var mine = config.TooltipKeywordRules.Where(r => r.Kind == effect.Kind).ToList();
            var builtIn = new HashSet<string>(effect.TriggerKeywords, StringComparer.OrdinalIgnoreCase);
            config.TooltipKeywordRules.RemoveAll(r => r.Kind == effect.Kind);

            bool off = mine.Count == 0 || mine.All(r => !r.Enabled);
            if (off) config.TooltipOffKinds.Add(effect.Kind);

            var kept = new HashSet<string>(mine.Where(r => r.Enabled || off).SelectMany(r => r.ParsedKeywords), StringComparer.OrdinalIgnoreCase);
            var extras = kept.Where(w => !builtIn.Contains(w)).ToList();
            if (extras.Count > 0)
                config.TooltipKeywordRules.Add(new TooltipKeywordRule { Keywords = string.Join(", ", extras), Kind = effect.Kind, Enabled = !mine.All(r => !r.Enabled) });

            if (!off && builtIn.Where(w => !kept.Contains(w)).ToList() is { Count: > 0 } removed)
                config.MutedWords[effect.Kind] = removed;
        }

        config.SeededKinds.Clear();
        config.Version = 2;
        return true;
    }

    /// <summary>Drops every extra word, switch-off and mute, leaving only the built-in words.</summary>
    public static void ResetToDefaults(Configuration config)
    {
        config.TooltipKeywordRules.Clear();
        config.TooltipOffKinds.Clear();
        config.MutedWords.Clear();
    }
}
