using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;

namespace RealDebuffs;

/// <summary>
/// One "if a Moodles/Loci tooltip CONTAINS this word, show that effect" link - see
/// <see cref="Configuration.TooltipKeywordRules"/> and <see cref="TooltipKeywordParser"/>.
///
/// This is deliberately a separate list from <see cref="Configuration.CustomStatusRules"/> rather
/// than a mode on the same rule type: a name-rule matches a status's whole (cleaned) TITLE
/// exactly, while a keyword-rule matches any OCCURRENCE of a word inside a status's DESCRIPTION
/// (the tooltip body) - different text, different comparison (exact-equals vs. contains), and only
/// keyword-rules ever produce a color. Keeping them as two types means neither has to grow optional
/// fields that only make sense for the other.
///
/// Like <see cref="CustomStatusRule"/>, several rules can freely name the same <see cref="Kind"/>
/// (e.g. "electrocute" and "voltage" both driving Electrocution), and one tooltip matching several
/// different rules at once is exactly how the "tentacles... shocking you" example in the feature
/// request produces Bind AND Paralysis together - EffectManager still only ever asks "which kinds
/// are active" (a set), so nothing can double up.
/// </summary>
public sealed class TooltipKeywordRule
{
    private string _keyword = "";
    private Regex? _pattern;

    /// <summary>
    /// The word or short phrase to look for, as the user typed it. Matched as a whole word/phrase
    /// (word-boundaries either side, so a rule for "burn" won't fire on "sunburnt") and
    /// case-insensitively, anywhere in a status's tooltip text - see <see cref="GetPattern"/>.
    /// </summary>
    public string Keyword
    {
        get => _keyword;
        set { _keyword = value ?? ""; _pattern = null; }
    }

    /// <summary>The effect to show while this keyword is found in an active tooltip. Saved as the enum's number - see <see cref="CustomStatusRule.Kind"/>'s remarks, same reasoning.</summary>
    public DebuffKind Kind { get; set; } = DebuffKind.Bind;

    /// <summary>Lets a rule be switched off without deleting it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Fallback tint used when this rule matches but the tooltip text itself doesn't pin down a
    /// color for that match (no [color=] tag around the word, and no plain color word - "pink",
    /// "green", ...  - nearby in the same clause). Null (the default for every seeded rule - see
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
    /// Word-boundary, case-insensitive pattern for <see cref="Keyword"/>, or null if the keyword is
    /// blank. Cached exactly like <see cref="CustomStatusRule.GetKey"/> - a method, not a property,
    /// so it isn't written into the saved config - and invalidated the same way, by the
    /// <see cref="Keyword"/> setter.
    /// </summary>
    public Regex? GetPattern()
    {
        if (_pattern != null) return _pattern;

        var trimmed = _keyword.Trim();
        if (trimmed.Length == 0) return null;

        return _pattern = new Regex($@"\b{Regex.Escape(trimmed)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// A modest starting set covering the two worked examples from the feature request (tentacles
    /// -&gt; Bind, shocking -&gt; Paralysis, flames -&gt; Burns) plus the other debuffs whose real-world
    /// RP flavor text is fairly predictable in English. Deliberately NOT exhaustive - the more
    /// mechanical kinds (Vulnerability, Windburn, Dropsy, Sludge, Infirmity, Misery, Hysteria,
    /// Pacification, Slow, Disease) have no obvious universal flavor-text vocabulary to guess at, so
    /// they're left for you to add if you ever want them, exactly like an empty
    /// <see cref="Configuration.CustomStatusRules"/> starts empty for you to fill in. Every seeded
    /// rule leaves <see cref="Color"/> unset on purpose: with no fallback configured, a tooltip that
    /// doesn't mention a color at all just shows the effect's own normal look rather than this list
    /// guessing at "the" color for, say, Poison.
    /// </summary>
    public static List<TooltipKeywordRule> Defaults()
    {
        (string Keyword, DebuffKind Kind)[] seed =
        {
            ("tentacle", DebuffKind.Bind), ("tentacles", DebuffKind.Bind),
            ("bind", DebuffKind.Bind), ("bound", DebuffKind.Bind),
            ("restrain", DebuffKind.Bind), ("restrained", DebuffKind.Bind),
            ("coil", DebuffKind.Bind), ("coiled", DebuffKind.Bind),

            ("shock", DebuffKind.Paralysis), ("shocking", DebuffKind.Paralysis), ("shocked", DebuffKind.Paralysis),
            ("paralyze", DebuffKind.Paralysis), ("paralyzed", DebuffKind.Paralysis), ("paralysis", DebuffKind.Paralysis),

            ("electrocute", DebuffKind.Electrocution), ("electrocuted", DebuffKind.Electrocution),
            ("voltage", DebuffKind.Electrocution), ("live wire", DebuffKind.Electrocution),

            ("flame", DebuffKind.Burns), ("flames", DebuffKind.Burns),
            ("burning", DebuffKind.Burns), ("scorch", DebuffKind.Burns), ("ignite", DebuffKind.Burns),

            ("frost", DebuffKind.Frost), ("frozen", DebuffKind.Frost),
            ("freezing", DebuffKind.Frost), ("chill", DebuffKind.Frost),

            ("chains", DebuffKind.Heavy), ("shackle", DebuffKind.Heavy), ("shackled", DebuffKind.Heavy),

            ("sleepy", DebuffKind.Sleep), ("drowsy", DebuffKind.Sleep), ("slumber", DebuffKind.Sleep),

            ("poison", DebuffKind.Poison), ("poisoned", DebuffKind.Poison),
            ("venom", DebuffKind.Poison), ("venomous", DebuffKind.Poison),

            ("blindfold", DebuffKind.Blind), ("blinded", DebuffKind.Blind),

            ("silenced", DebuffKind.Silence), ("gagged", DebuffKind.Silence), ("muted", DebuffKind.Silence),

            ("charmed", DebuffKind.Charm), ("infatuated", DebuffKind.Charm),
            ("seduced", DebuffKind.Charm), ("enthralled", DebuffKind.Charm),

            ("petrified", DebuffKind.Petrification), ("turned to stone", DebuffKind.Petrification),

            ("bleeding", DebuffKind.Bleeding), ("gash", DebuffKind.Bleeding),

            ("stunned", DebuffKind.Stun), ("dazed", DebuffKind.Stun),

            ("cursed", DebuffKind.Doom), ("marked for death", DebuffKind.Doom),
        };

        var list = new List<TooltipKeywordRule>(seed.Length);
        foreach (var (keyword, kind) in seed)
            list.Add(new TooltipKeywordRule { Keyword = keyword, Kind = kind });
        return list;
    }
}
