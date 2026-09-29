using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using RealDebuffs.Effects.Framework;
using RealDebuffs.Effects;

namespace RealDebuffs;

/// <summary>Where a match's color came from - for the settings-window tester.</summary>
public enum TooltipColorSource { None, Tag, Clause }

/// <summary>
/// One resolved "this kind should be active, looking like this" result. MaterialSubstitution is
/// set when a "made of X" / "of X" / "with X" phrase appeared anywhere in the tooltip text and
/// resolved to a material name; it's type-checked at application time (see CustomStatusSnapshot
/// + EffectHeroSlots), so it may be silently dropped if it doesn't fit the target effect's hero.
/// </summary>
public readonly record struct TooltipEffectMatch(
    DebuffKind Kind,
    Vector4? Color,
    TooltipColorSource ColorSource,
    string? MaterialSubstitution = null);

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

/// <summary>
/// Scans a tooltip for every enabled rule's keywords and resolves each match's color from the
/// text. Pure and stateless apart from one wall-clock read for the "rainbow" word, so it can run
/// on the settings tester's freshly-typed text as easily as on real status descriptions.
///
/// Color priority per match, highest first:
///  1. A [color=...] tag wrapping the match.
///  2. A plain color word ("pink", "green", ..., or a rainbow word) in the same clause. Clauses
///     split on `. , ; ! ?` and standalone "and"/"but". First word in the clause wins.
///  3. Nothing - the effect shows in its own color.
///
/// Material substitution: any "connector + material" phrase ("made of snow", "of lightning",
/// "with chains") anywhere in the text attaches a material name to every matched kind. The
/// target effect's hero slots determine where it actually lands at application time; if the
/// material type doesn't match (a stroke material on a particle hero, say), it's dropped and the
/// color still applies.
///
/// Same-kind ties: prefer a match with a color over one without, else first match in rule order.
/// </summary>
public static class TooltipKeywordParser
{
    /// <summary>
    /// A canonical trigger word for a kind, drawn from the user's own enabled keyword rules so
    /// an exported phrase is guaranteed to fire against their config. Null if no enabled rule
    /// targets this kind; the caller falls back to the kind's name.
    /// </summary>
    public static string? CanonicalTriggerWord(DebuffKind kind, IReadOnlyList<TooltipKeywordRule> rules)
    {
        foreach (var rule in rules)
        {
            if (!rule.Enabled || rule.Kind != kind) continue;
            var parsed = rule.ParsedKeywords;
            if (parsed.Count > 0) return parsed[0];
        }
        return null;
    }

    /// <summary>
    /// True if the token is a recognized color word - a fixed-color name from
    /// <see cref="NamedColors"/> or one of the cycling <see cref="RainbowWords"/>. Used by the
    /// effect generator's export-phrase builder to decide whether a stored ColorOverrides value
    /// can be rendered as an adjective in a status description.
    /// </summary>
    public static bool IsColorWord(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var t = token.Trim();
        return NamedColors.ContainsKey(t) || RainbowWords.Contains(t);
    }

    /// <summary>
    /// The shortest phrase from <c>MaterialWords</c> that maps to a given material name, for
    /// producing "made of X" export text. Null when the material has no natural-language word
    /// (region materials, or any material added to the registry without a matching entry).
    /// </summary>
    public static string? CanonicalMaterialWord(string materialName)
    {
        string? best = null;
        foreach (var kv in MaterialWords)
        {
            if (!string.Equals(kv.Value, materialName, StringComparison.OrdinalIgnoreCase)) continue;
            if (best == null
                || kv.Key.Length < best.Length
                || (kv.Key.Length == best.Length && string.CompareOrdinal(kv.Key, best) < 0))
                best = kv.Key;
        }
        return best;
    }

    private static readonly Regex TagToken = new(
        @"\[color=(?<color>[0-9a-z]+)\]|\[/color\]|\[glow=[0-9a-z]+\]|\[/glow\]|\[i\]|\[/i\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ClauseBreak = new(
        @"[.,;!?]+|\band\b|\bbut\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Common English color words that can appear in plain flavor text. A fixed list rather than a
    /// user-editable one: unlike keywords (arbitrary RP flavor text), color names are a closed
    /// English vocabulary. Add entries here if you use one regularly that's missing.
    ///
    /// This is also the source for the Effect generator's Color dropdown, which is why the list
    /// stays trimmed: past about 25-30 entries the picker stops being scannable. The greyscale
    /// family is deliberately just three words - white, grayscale, black - because they map onto
    /// the three value buckets in DrawHelpers.PushColorOverride.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Vector4> NamedColors = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = Rgb(0.06f, 0.06f, 0.06f),
        ["blue"] = Rgb(0.20f, 0.45f, 0.90f),
        ["cyan"] = Rgb(0.20f, 0.85f, 0.90f),
        ["grayscale"] = Rgb(0.55f, 0.55f, 0.55f),
        ["green"] = Rgb(0.20f, 0.80f, 0.25f),
        ["magenta"] = Rgb(0.85f, 0.15f, 0.75f),
        ["navy"] = Rgb(0.08f, 0.15f, 0.45f),
        ["orange"] = Rgb(0.95f, 0.50f, 0.10f),
        ["pink"] = Rgb(0.95f, 0.45f, 0.70f),
        ["purple"] = Rgb(0.55f, 0.20f, 0.75f),
        ["red"] = Rgb(0.85f, 0.15f, 0.15f),
        ["teal"] = Rgb(0.10f, 0.65f, 0.65f),
        ["white"] = Rgb(0.95f, 0.95f, 0.95f),
        ["yellow"] = Rgb(0.95f, 0.85f, 0.15f),
    };

    /// <summary>
    /// Words that resolve to a hue cycling over time rather than a fixed color. Kept separate from
    /// NamedColors so that dictionary stays a plain word-&gt;RGB lookup.
    /// </summary>
    private static readonly HashSet<string> RainbowWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "rgb", "rainbow",
    };

    /// <summary>
    /// Words a user can write after "made of" / "of" / "as" / "from" / "with" to substitute an
    /// effect's hero visuals. Sourced from MaterialRegistry.Vocabulary, which is built from each
    /// material's own NaturalLanguageWords declaration - adding a new material with words is a
    /// one-file change (the material), and this field picks it up at next load.
    ///
    /// Values are material names. Because the substitution is type-checked at the point of
    /// application (see EffectRegistry.HeroSlotsFor), a phrase like "made of lightning" on a
    /// Burns effect - whose hero is a particle role - is silently dropped rather than producing
    /// nonsense; Burns still gets its color from the phrase.
    /// </summary>
    private static readonly Dictionary<string, string> MaterialWords = MaterialRegistry.Vocabulary as Dictionary<string, string>
        ?? new Dictionary<string, string>(MaterialRegistry.Vocabulary, StringComparer.OrdinalIgnoreCase);

    // Both regexes are built from their dictionaries above so they can never drift out of sync.
    // Textual ordering matters: fields initialize in declaration order, so the dictionaries must
    // be declared before these.
    private static readonly Regex ColorWordPattern = BuildColorWordPattern();
    private static readonly Regex MaterialPhrase   = BuildMaterialPhrase();

    private static Regex BuildColorWordPattern()
    {
        var escaped = new List<string>(NamedColors.Count + RainbowWords.Count);
        foreach (var name in NamedColors.Keys) escaped.Add(Regex.Escape(name));
        foreach (var name in RainbowWords) escaped.Add(Regex.Escape(name));
        // Longest-first so e.g. a hypothetical "sea green" would beat "green".
        escaped.Sort((a, b) => b.Length.CompareTo(a.Length));
        return new Regex($@"\b({string.Join("|", escaped)})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static Regex BuildMaterialPhrase()
    {
        var escaped = new List<string>(MaterialWords.Count);
        foreach (var word in MaterialWords.Keys) escaped.Add(Regex.Escape(word));
        // Longest-first: "snowflakes" before "snow", "tentacles" before "tentacle".
        escaped.Sort((a, b) => b.Length.CompareTo(a.Length));
        var alternation = string.Join("|", escaped);
        return new Regex(
            $@"\b(?:made\s+of|of|as|from|with)\s+(?<material>{alternation})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static Vector4 Rgb(float r, float g, float b) => new(r, g, b, 1f);

    /// <summary>
    /// Parses one tooltip against every enabled rule. At most one match per distinct Kind. Never
    /// throws; unresolvable color tokens and blank keyword rules are silently skipped.
    ///
    /// Material scoping: material phrases ("made of snow", "of lightning") are matched per
    /// CLAUSE, not globally. A description that names several effects - "red tentacle made of
    /// snow, black flame made of sparks" - attaches each clause's own material to the match(es)
    /// in that clause, so the two effects don't share one material. Any keyword falling inside
    /// any material phrase's span is skipped, so "flames" in "made of flames" acts as a
    /// modifier on the effect in that clause rather than firing its own rule.
    /// </summary>
    public static IReadOnlyList<TooltipEffectMatch> Parse(string? tooltipText, IReadOnlyList<TooltipKeywordRule> rules)
    {
        if (string.IsNullOrWhiteSpace(tooltipText) || rules.Count == 0)
            return Array.Empty<TooltipEffectMatch>();

        var (plain, colorRuns) = StripAndMapColors(tooltipText);
        if (plain.Length == 0) return Array.Empty<TooltipEffectMatch>();

        var clauses = SplitClauses(plain);
        var colorWords = ColorWordPattern.Matches(plain);
        var best = new Dictionary<DebuffKind, TooltipEffectMatch>();

        // Locate EVERY material phrase and its span, not just the first. The spans are used both
        // for keyword suppression (below) and for per-clause resolution (MaterialInSameClause).
        var materialPhrases = new List<(int Start, int End, string Material)>();
        foreach (Match m in MaterialPhrase.Matches(plain))
        {
            if (MaterialWords.TryGetValue(m.Groups["material"].Value, out var matName))
                materialPhrases.Add((m.Index, m.Index + m.Length, matName));
        }

        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            var pattern = rule.GetPattern();
            if (pattern == null) continue;

            foreach (Match m in pattern.Matches(plain))
            {
                // Skip keywords inside ANY material phrase. Those words are modifiers, not
                // activation keywords.
                bool insideMaterial = false;
                for (int i = 0; i < materialPhrases.Count; i++)
                {
                    if (m.Index >= materialPhrases[i].Start && m.Index < materialPhrases[i].End)
                    {
                        insideMaterial = true;
                        break;
                    }
                }
                if (insideMaterial) continue;

                Vector4? tagColor = FindTagColor(colorRuns, m.Index);
                Vector4? clauseColor = tagColor == null ? FindClauseColor(clauses, colorWords, m.Index) : null;
                Vector4? resolved = tagColor ?? clauseColor;

                var source = tagColor != null ? TooltipColorSource.Tag
                    : clauseColor != null ? TooltipColorSource.Clause
                    : TooltipColorSource.None;

                // Attach only the material phrase that lives in the same clause as this match.
                // A material in a different clause describes a different effect.
                string? matchMaterial = MaterialInSameClause(clauses, materialPhrases, m.Index);

                var candidate = new TooltipEffectMatch(rule.Kind, resolved, source, matchMaterial);
                if (!best.TryGetValue(rule.Kind, out var existing) || (candidate.Color != null && existing.Color == null))
                    best[rule.Kind] = candidate;
            }
        }

        if (best.Count == 0) return Array.Empty<TooltipEffectMatch>();

        var result = new List<TooltipEffectMatch>(best.Count);
        foreach (var kv in best) result.Add(kv.Value);
        return result;
    }

    /// <summary>
    /// The material phrase belonging to the clause that contains <paramref name="idx"/>, if any.
    /// A clause can carry only one material - the first phrase found in it wins, matching the
    /// "first declaration sticks" convention used elsewhere (StatusCatalog, MaterialRegistry).
    /// Returns null when the match's clause has no material, or the index falls in no clause.
    /// </summary>
    private static string? MaterialInSameClause(
        List<(int Start, int End)> clauses,
        List<(int Start, int End, string Material)> materialPhrases,
        int idx)
    {
        for (int c = 0; c < clauses.Count; c++)
        {
            var (start, end) = clauses[c];
            if (idx < start || idx >= end) continue;

            for (int p = 0; p < materialPhrases.Count; p++)
            {
                var mp = materialPhrases[p];
                if (mp.Start >= start && mp.Start < end) return mp.Material;
            }
            return null;
        }
        return null;
    }

    /// <summary>
    /// Resolves one color token: hex triplet/quad (with or without # / 0x, or CSS 3/4-digit
    /// shorthand), a NamedColors name, or a rainbow word. Returns false for anything else.
    /// </summary>
    public static bool TryResolveColorToken(string? token, out Vector4 rgb)
    {
        rgb = default;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var t = token.Trim();
        if (t.StartsWith('#')) t = t[1..];
        else if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];

        if ((t.Length is 3 or 4 or 6 or 8) && IsAllHex(t))
        {
            if (t.Length is 3 or 4)
            {
                var expanded = new char[t.Length * 2];
                for (int i = 0; i < t.Length; i++) { expanded[i * 2] = t[i]; expanded[i * 2 + 1] = t[i]; }
                t = new string(expanded);
            }
            int r = Convert.ToInt32(t[..2], 16);
            int g = Convert.ToInt32(t[2..4], 16);
            int b = Convert.ToInt32(t[4..6], 16);
            rgb = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
            return true;
        }

        return TryResolveWord(t, out rgb);
    }

    /// <summary>Both the clause path and TryResolveColorToken route through here so they can't drift on which words they recognize.</summary>
    private static bool TryResolveWord(string word, out Vector4 rgb)
    {
        if (RainbowWords.Contains(word)) { rgb = RainbowColor(Environment.TickCount64); return true; }
        return NamedColors.TryGetValue(word, out rgb);
    }

    /// <summary>
    /// Rainbow at a given time: hue advances one step per second across a 256-step wheel. Cached
    /// as part of a match, so it picks up the next step on the watcher's next refresh - which is
    /// why it lines up with the once-a-second re-read instead of needing its own timer.
    /// </summary>
    private static Vector4 RainbowColor(long nowMs)
    {
        int hue = (int)((nowMs / 1000) % 256);
        if (hue < 0) hue += 256;
        return HueToRgb(hue / 256f);
    }

    private static Vector4 HueToRgb(float h)
    {
        float sector = h * 6f;
        int i = (int)sector % 6;
        float f = sector - (int)sector;
        float q = 1f - f;
        return i switch
        {
            0 => new Vector4(1f, f, 0f, 1f),
            1 => new Vector4(q, 1f, 0f, 1f),
            2 => new Vector4(0f, 1f, f, 1f),
            3 => new Vector4(0f, q, 1f, 1f),
            4 => new Vector4(f, 0f, 1f, 1f),
            _ => new Vector4(1f, 0f, q, 1f),
        };
    }

    private static bool IsAllHex(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    /// <summary>
    /// Strips markup and records which stretch of the STRIPPED result each [color=] run covers,
    /// so a later keyword match's plain-text index can be checked against it directly.
    /// An unresolvable color value is treated as no color at that tag (still stripped).
    /// </summary>
    private static (string Plain, List<(int Start, int End, Vector4 Color)> ColorRuns) StripAndMapColors(string raw)
    {
        var plain = new StringBuilder(raw.Length);
        var runs = new List<(int, int, Vector4)>();
        Vector4? currentColor = null;
        int runStart = 0;
        int cursor = 0;

        void FlushRun(int uptoPlainIndex)
        {
            if (currentColor is { } c && uptoPlainIndex > runStart)
                runs.Add((runStart, uptoPlainIndex, c));
            runStart = uptoPlainIndex;
        }

        foreach (Match m in TagToken.Matches(raw))
        {
            plain.Append(raw, cursor, m.Index - cursor);
            cursor = m.Index + m.Length;

            if (m.Groups["color"].Success)
            {
                FlushRun(plain.Length);
                currentColor = TryResolveColorToken(m.Groups["color"].Value, out var rgb) ? rgb : (Vector4?)null;
            }
            else if (m.Value.Equals("[/color]", StringComparison.OrdinalIgnoreCase))
            {
                FlushRun(plain.Length);
                currentColor = null;
            }
            // [glow=]/[/glow]/[i]/[/i]: already removed from `plain`, no color-run bookkeeping.
        }

        plain.Append(raw, cursor, raw.Length - cursor);
        FlushRun(plain.Length);

        return (plain.ToString(), runs);
    }

    private static List<(int Start, int End)> SplitClauses(string plain)
    {
        var clauses = new List<(int, int)>();
        int pos = 0;
        foreach (Match m in ClauseBreak.Matches(plain))
        {
            if (m.Index > pos) clauses.Add((pos, m.Index));
            pos = m.Index + m.Length;
        }
        if (pos < plain.Length) clauses.Add((pos, plain.Length));
        return clauses;
    }

    private static Vector4? FindTagColor(List<(int Start, int End, Vector4 Color)> runs, int idx)
    {
        foreach (var r in runs) if (idx >= r.Start && idx < r.End) return r.Color;
        return null;
    }

    private static Vector4? FindClauseColor(List<(int Start, int End)> clauses, MatchCollection colorWords, int idx)
    {
        foreach (var c in clauses)
        {
            if (idx < c.Start || idx >= c.End) continue;
            foreach (Match cw in colorWords)
            {
                if (cw.Index < c.Start || cw.Index >= c.End) continue;
                return TryResolveWord(cw.Value, out var rgb) ? rgb : null;
            }
            break; // found the containing clause; it just has no color word in it
        }
        return null;
    }
}