using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

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

    /// <summary>Saved as the enum's number, so new kinds must go at the END of DebuffKind.</summary>
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
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// A modest starting set covering common RP flavor-text vocabulary. Deliberately not
    /// exhaustive - the more mechanical kinds (Vulnerability, Slow, Pacification, etc.) have no
    /// universal vocabulary to guess at.
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
/// Material substitution: any "<connector> <material>" phrase ("made of snow", "of lightning",
/// "with chains") anywhere in the text attaches a material name to every matched kind. The
/// target effect's hero slots determine where it actually lands at application time; if the
/// material type doesn't match (a stroke material on a particle hero, say), it's dropped and the
/// color still applies.
///
/// Same-kind ties: prefer a match with a color over one without, else first match in rule order.
/// </summary>
public static class TooltipKeywordParser
{
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
    /// </summary>
    private static readonly Dictionary<string, Vector4> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = Rgb(0.85f, 0.15f, 0.15f),
        ["crimson"] = Rgb(0.75f, 0.05f, 0.15f),
        ["scarlet"] = Rgb(0.90f, 0.15f, 0.05f),
        ["maroon"] = Rgb(0.50f, 0.08f, 0.12f),
        ["orange"] = Rgb(0.95f, 0.50f, 0.10f),
        ["amber"] = Rgb(0.95f, 0.65f, 0.10f),
        ["yellow"] = Rgb(0.95f, 0.85f, 0.15f),
        ["gold"] = Rgb(0.90f, 0.75f, 0.20f),
        ["green"] = Rgb(0.20f, 0.80f, 0.25f),
        ["emerald"] = Rgb(0.10f, 0.70f, 0.40f),
        ["jade"] = Rgb(0.30f, 0.75f, 0.55f),
        ["olive"] = Rgb(0.45f, 0.50f, 0.15f),
        ["teal"] = Rgb(0.10f, 0.65f, 0.65f),
        ["cyan"] = Rgb(0.20f, 0.85f, 0.90f),
        ["turquoise"] = Rgb(0.15f, 0.75f, 0.70f),
        ["blue"] = Rgb(0.20f, 0.45f, 0.90f),
        ["azure"] = Rgb(0.15f, 0.55f, 0.95f),
        ["sapphire"] = Rgb(0.10f, 0.30f, 0.80f),
        ["navy"] = Rgb(0.08f, 0.15f, 0.45f),
        ["indigo"] = Rgb(0.30f, 0.15f, 0.65f),
        ["violet"] = Rgb(0.55f, 0.25f, 0.85f),
        ["purple"] = Rgb(0.55f, 0.20f, 0.75f),
        ["lavender"] = Rgb(0.70f, 0.60f, 0.90f),
        ["magenta"] = Rgb(0.85f, 0.15f, 0.75f),
        ["pink"] = Rgb(0.95f, 0.45f, 0.70f),
        ["rose"] = Rgb(0.90f, 0.35f, 0.55f),
        ["fuchsia"] = Rgb(0.90f, 0.15f, 0.80f),
        ["brown"] = Rgb(0.45f, 0.30f, 0.15f),
        ["tan"] = Rgb(0.70f, 0.55f, 0.35f),
        ["white"] = Rgb(0.95f, 0.95f, 0.95f),
        ["ivory"] = Rgb(0.95f, 0.93f, 0.85f),
        ["silver"] = Rgb(0.75f, 0.75f, 0.78f),
        ["gray"] = Rgb(0.55f, 0.55f, 0.55f),
        ["grey"] = Rgb(0.55f, 0.55f, 0.55f),
        ["black"] = Rgb(0.06f, 0.06f, 0.06f),
        ["bronze"] = Rgb(0.60f, 0.40f, 0.20f),
        ["copper"] = Rgb(0.72f, 0.45f, 0.25f),
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
    /// effect's hero visuals. Values are material names from MaterialRegistry. Because the
    /// substitution is type-checked at the point of application (see EffectHeroSlots), a phrase
    /// like "made of lightning" on a Burns effect - whose hero is a particle role - is silently
    /// dropped rather than producing nonsense; Burns still gets its color from the phrase.
    /// </summary>
    private static readonly Dictionary<string, string> MaterialWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Particle materials
        ["snow"]        = "particle.snow",
        ["snowflake"]   = "particle.snowflake",
        ["snowflakes"]  = "particle.snowflake",
        ["fog"]         = "particle.fog",
        ["mist"]        = "particle.fog",
        ["fire"]        = "particle.ember",
        ["flame"]       = "particle.ember",
        ["flames"]      = "particle.ember",
        ["embers"]      = "particle.ember",
        ["spark"]       = "particle.spark",
        ["sparks"]      = "particle.spark",
        ["droplet"]     = "particle.drip",
        ["droplets"]    = "particle.drip",
        ["drip"]        = "particle.drip",
        ["drips"]       = "particle.drip",

        // Stroke materials
        ["lightning"]   = "stroke.lightning",
        ["electricity"] = "stroke.lightning",
        ["bolt"]        = "stroke.lightning",
        ["bolts"]       = "stroke.lightning",
        ["tentacle"]    = "stroke.parasite",
        ["tentacles"]   = "stroke.parasite",
        ["tendril"]     = "stroke.parasite",
        ["tendrils"]    = "stroke.parasite",
        ["chain"]       = "stroke.chain",
        ["chains"]      = "stroke.chain",
        ["links"]       = "stroke.chain",
        ["parasite"]    = "stroke.parasite",
        ["parasites"]   = "stroke.parasite",
    };

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
    /// The material phrase is found FIRST, and any keyword falling inside its span is skipped, so
    /// "frost made of flames" activates only Frost (not Burns via the "flames" rule) while still
    /// attaching the ember material to Frost's hero slot.
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

        // Locate the material phrase before keyword matching. Its span is used below to suppress
        // any keyword rule that would otherwise fire on a word inside it.
        string? material = null;
        int matStart = -1, matEnd = -1;
        foreach (Match m in MaterialPhrase.Matches(plain))
        {
            if (MaterialWords.TryGetValue(m.Groups["material"].Value, out var matName))
            {
                material = matName;
                matStart = m.Index;
                matEnd = m.Index + m.Length;
                break;
            }
        }

        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            var pattern = rule.GetPattern();
            if (pattern == null) continue;

            foreach (Match m in pattern.Matches(plain))
            {
                // Skip keywords that fall inside the material phrase ("flames" in "made of
                // flames"). Those words are modifiers, not activation keywords.
                if (matStart >= 0 && m.Index >= matStart && m.Index < matEnd) continue;

                Vector4? tagColor = FindTagColor(colorRuns, m.Index);
                Vector4? clauseColor = tagColor == null ? FindClauseColor(clauses, colorWords, m.Index) : null;
                Vector4? resolved = tagColor ?? clauseColor;

                var source = tagColor != null ? TooltipColorSource.Tag
                    : clauseColor != null ? TooltipColorSource.Clause
                    : TooltipColorSource.None;

                var candidate = new TooltipEffectMatch(rule.Kind, resolved, source);
                if (!best.TryGetValue(rule.Kind, out var existing) || (candidate.Color != null && existing.Color == null))
                    best[rule.Kind] = candidate;
            }
        }

        if (best.Count == 0) return Array.Empty<TooltipEffectMatch>();

        var result = new List<TooltipEffectMatch>(best.Count);
        foreach (var kv in best)
        {
            var match = kv.Value;
            if (material != null)
                match = match with { MaterialSubstitution = material };
            result.Add(match);
        }
        return result;
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