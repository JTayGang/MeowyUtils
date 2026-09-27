using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;

namespace RealDebuffs;

/// <summary>
/// One "if a Moodles/Loci tooltip CONTAINS any of these words, show that effect" link - see
/// <see cref="Configuration.TooltipKeywordRules"/> and <see cref="TooltipKeywordParser"/>.
///
/// A single rule can carry several keywords at once, comma-separated (e.g. "flame, burning,
/// scorch, ignite") - they all drive the same <see cref="Kind"/>, share the same fallback
/// <see cref="Color"/> and <see cref="Strength"/>, and can be edited together in one box. This is
/// the normal way to write a rule: the parser walks every keyword in one pass and produces at most
/// one result per <see cref="DebuffKind"/> regardless of how many of them hit.
///
/// Like <see cref="CustomStatusRule"/>, several rules can freely name the same <see cref="Kind"/>
/// (e.g. "electrocute" and "voltage" both driving Electrocution), and one tooltip matching several
/// different rules at once is exactly how the "tentacles... shocking you" example in the feature
/// request produces Bind AND Paralysis together - EffectManager still only ever asks "which kinds
/// are active" (a set), so nothing can double up.
/// </summary>
public sealed class TooltipKeywordRule
{
    private string _keywords = "";
    private string[]? _parsed;
    private Regex? _pattern;

    /// <summary>
    /// The words or short phrases to look for, as the user typed them, comma-separated. Each one is
    /// matched as a whole word/phrase (word-boundaries either side, so a rule for "burn" won't fire
    /// on "sunburnt") and case-insensitively, anywhere in a status's tooltip text - see
    /// <see cref="GetPattern"/>. Blank entries and duplicates (ignoring case) are ignored; newlines
    /// and semicolons work as separators too, so a pasted multi-line list just works.
    /// </summary>
    public string Keywords
    {
        get => _keywords;
        set { _keywords = value ?? ""; _parsed = null; _pattern = null; }
    }

    /// <summary>The effect to show while any of <see cref="Keywords"/> is found in an active tooltip. Saved as the enum's number - see <see cref="CustomStatusRule.Kind"/>'s remarks, same reasoning.</summary>
    public DebuffKind Kind { get; set; } = DebuffKind.Bind;

    /// <summary>Lets a rule be switched off without deleting it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Fallback tint used when this rule matches but the tooltip text itself doesn't pin down a
    /// color for that match (no [color=] tag around the word, and no plain color word - "pink",
    /// "green", ... - nearby in the same clause). Null (the default for every seeded rule - see
    /// <see cref="Defaults"/>) means "don't override anything": the effect just shows in its own
    /// normal authored color, exactly like a real debuff or a name-based custom rule.
    /// </summary>
    public Vector4? Color { get; set; }

    /// <summary>
    /// How strongly the effect should show while this rule matches, 0..1 - the same knob
    /// <see cref="DebuffKind.Strengths"/> (via <see cref="StatusCatalog"/>) uses for a fainter-tier
    /// real debuff. 1.0 (full strength) unless you deliberately lower it in the settings window.
    /// </summary>
    public float Strength { get; set; } = 1f;

    /// <summary>
    /// The individual words/phrases parsed out of <see cref="Keywords"/>, cached alongside the
    /// compiled <see cref="GetPattern"/> regex - the settings window reads this every frame, so
    /// re-splitting on each call would be wasted work. Invalidation is on the <see cref="Keywords"/>
    /// setter, same as <see cref="GetPattern"/>.
    /// </summary>
    public IReadOnlyList<string> ParsedKeywords => _parsed ??= ParseKeywords(_keywords);

    /// <summary>
    /// One word-boundary, case-insensitive pattern covering EVERY keyword in <see cref="Keywords"/>
    /// as an alternation, or null if there aren't any. Cached exactly like
    /// <see cref="CustomStatusRule.GetKey"/> - a method, not a property, so it isn't written into
    /// the saved config - and invalidated by the <see cref="Keywords"/> setter. The alternation is
    /// non-capturing and relies on regex backtracking to prefer longer matches (so "flames" still
    /// matches when "flame" is also a keyword).
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

    /// <summary>
    /// Splits a raw keyword box's contents into individual non-blank, de-duplicated (case-insensitive)
    /// entries. Public and static so the settings window can preview exactly what an "Add" row would
    /// create before committing to it.
    /// </summary>
    public static string[] ParseKeywords(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        return raw
            .Split(new[] { ',', '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// A modest starting set covering the two worked examples from the feature request (tentacles
    /// -&gt; Bind, shocking -&gt; Paralysis, flames -&gt; Burns) plus the other debuffs whose real-world
    /// RP flavor text is fairly predictable in English. Deliberately NOT exhaustive - the more
    /// mechanical kinds (Vulnerability, Windburn, Dropsy, Sludge, Infirmity, Misery, Hysteria,
    /// Pacification, Slow, Disease) have no obvious universal flavor-text vocabulary to guess at, so
    /// they're left for you to add if you ever want them. Every seeded rule leaves <see cref="Color"/>
    /// unset on purpose: with no fallback configured, a tooltip that doesn't mention a color at all
    /// just shows the effect's own normal look rather than this list guessing at "the" color for,
    /// say, Poison.
    /// </summary>
    public static List<TooltipKeywordRule> Defaults()
    {
        (string Keywords, DebuffKind Kind)[] seed =
        {
            ("tentacle, tentacles, bind, bound, restrain, restrained, coil, coiled", DebuffKind.Bind),
            ("shock, shocking, shocked, paralyze, paralyzed, paralysis", DebuffKind.Paralysis),
            ("electrocute, electrocuted, voltage, live wire", DebuffKind.Electrocution),
            ("flame, flames, burning, scorch, ignite", DebuffKind.Burns),
            ("frost, frozen, freezing, chill", DebuffKind.Frost),
            ("chains, shackle, shackled", DebuffKind.Heavy),
            ("sleepy, drowsy, slumber", DebuffKind.Sleep),
            ("poison, poisoned, venom, venomous", DebuffKind.Poison),
            ("blindfold, blinded", DebuffKind.Blind),
            ("silenced, gagged, muted", DebuffKind.Silence),
            ("charmed, infatuated, seduced, enthralled", DebuffKind.Charm),
            ("petrified, turned to stone", DebuffKind.Petrification),
            ("bleeding, gash", DebuffKind.Bleeding),
            ("stunned, dazed", DebuffKind.Stun),
            ("cursed, marked for death", DebuffKind.Doom),
        };

        var list = new List<TooltipKeywordRule>(seed.Length);
        foreach (var (keywords, kind) in seed)
            list.Add(new TooltipKeywordRule { Keywords = keywords, Kind = kind });
        return list;
    }
}