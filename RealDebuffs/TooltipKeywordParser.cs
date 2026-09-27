using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace RealDebuffs;

/// <summary>Where a matched rule's color actually came from - purely informational, used by the settings-window tester so tuning a rule doesn't mean guessing why it did (or didn't) pick up a color.</summary>
public enum TooltipColorSource
{
    /// <summary>No color was found anywhere, and the rule has no fallback <see cref="TooltipKeywordRule.Color"/> either - the effect will show in its own normal color.</summary>
    None,
    /// <summary>An explicit [color=...] tag wrapped the matched word.</summary>
    Tag,
    /// <summary>A plain color word ("pink", "green", ...) was found in the same clause as the matched word.</summary>
    Clause,
    /// <summary>Neither of the above; this is the rule's own configured <see cref="TooltipKeywordRule.Color"/> fallback.</summary>
    RuleDefault,
}

/// <summary>One resolved "this kind should be active, looking like this" result from <see cref="TooltipKeywordParser.Parse"/>.</summary>
public readonly record struct TooltipEffectMatch(DebuffKind Kind, Vector4? Color, float Strength, TooltipColorSource ColorSource);

/// <summary>
/// Scans a single status's tooltip text (a Moodles/Loci "Description") for every enabled
/// <see cref="TooltipKeywordRule"/>'s keyword, and for each one found, works out what color (if
/// any) the text itself is asking for. Pure and stateless - no IPC, no ImGui, nothing game-related
/// - so it's exactly as easy to call from the settings window's tester (on text you just typed) as
/// from the real path (on text read from Moodles/Loci). See
/// <see cref="CustomStatusSnapshot.Build"/> for how the real path calls this once per active status,
/// per snapshot refresh, and caches the results rather than calling it fresh every frame.
///
/// COLOR RESOLUTION, per match, highest priority first:
///  1. An explicit `[color=value]...[/color]` tag (Moodles/Loci's own rich-text tag - the same one
///     <see cref="StatusNames"/> already strips from titles) that wraps the matched word. `value`
///     can be a hex triplet (`ff69b4`, `#ff69b4`, or the 3/4-digit CSS-style shorthand) or a plain
///     color name from <see cref="NamedColors"/> - see <see cref="TryResolveColorToken"/>.
///  2. A plain color WORD ("pink", "green", ...) anywhere in the same CLAUSE as the matched word.
///     "Clause" means: split the tooltip on `. , ; ! ?` and the standalone words "and"/"but", and
///     look inside whichever piece the match landed in. This is what makes
///     "the pink tentacles hold you in place, shocking you." color the tentacles (Bind) pink
///     without also tinting the shock (Paralysis) pink - they're on opposite sides of the comma.
///     If a clause happens to contain more than one color word, the first one (by position) wins -
///     a deliberately simple tie-break rather than trying to guess which word is "closer" to the
///     match in some more clever sense.
///  3. The rule's own <see cref="TooltipKeywordRule.Color"/>, if the person configured one.
///  4. Nothing - the effect shows in its own normal color, same as a real debuff.
///
/// Two different rules matching the SAME <see cref="DebuffKind"/> in one tooltip (e.g. "tentacle"
/// and "coil" both driving Bind) resolve the same way EffectManager already resolves two sources of
/// the same kind elsewhere: only one result comes out per kind, preferring whichever match actually
/// found a color over one that didn't, then whichever was found first.
/// </summary>
public static class TooltipKeywordParser
{
    // The exact tag vocabulary StatusNames.Markup strips from titles - see its remarks. Named
    // capture groups pull out the color/glow value; [glow=]/[i] and their closing tags are matched
    // only so they get removed from the plain text, same as StatusNames does for titles.
    private static readonly Regex TagToken = new(
        @"\[color=(?<color>[0-9a-z]+)\]|\[/color\]|\[glow=[0-9a-z]+\]|\[/glow\]|\[i\]|\[/i\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ClauseBreak = new(
        @"[.,;!?]+|\band\b|\bbut\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Common color words a status's flavor text might use in plain English, with no tag at all -
    /// e.g. "the pink tentacles..." in the feature request's own first example. Deliberately a
    /// fixed, curated list rather than something exposed for editing in the settings window: unlike
    /// keywords (which are inherently unpredictable RP flavor text you have to add yourself), color
    /// names are a small, well-known, closed English vocabulary, so one built-in list covers the
    /// vast majority of real tooltip text. Extend it here (it's just a dictionary) if you use a
    /// color word regularly that isn't in it yet.
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

    // One compiled alternation over every named color, rather than looping the dictionary per
    // match - built once from NamedColors' own keys so it can never drift out of sync with it.
    private static readonly Regex ColorWordPattern = BuildColorWordPattern();

    private static Regex BuildColorWordPattern()
    {
        var escaped = new List<string>(NamedColors.Count);
        foreach (var name in NamedColors.Keys)
            escaped.Add(Regex.Escape(name));
        // Longest-first so e.g. "sea green" (if ever added) would win over "green" alone; harmless
        // no-op with the current single-word list, but free to get right now.
        escaped.Sort((a, b) => b.Length.CompareTo(a.Length));
        return new Regex($@"\b({string.Join("|", escaped)})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static Vector4 Rgb(float r, float g, float b) => new(r, g, b, 1f);

    /// <summary>
    /// Parses one tooltip's text against every enabled rule. Returns at most one
    /// <see cref="TooltipEffectMatch"/> per distinct <see cref="DebuffKind"/> any rule named - see
    /// the class remarks for exactly how ties (same kind, several matches) resolve. Never throws:
    /// unresolvable color tokens and rules with a blank keyword are just skipped.
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

        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            var pattern = rule.GetPattern();
            if (pattern == null) continue;

            foreach (Match m in pattern.Matches(plain))
            {
                Vector4? tagColor = FindTagColor(colorRuns, m.Index);
                Vector4? clauseColor = tagColor == null ? FindClauseColor(clauses, colorWords, m.Index) : null;
                Vector4? resolved = tagColor ?? clauseColor ?? rule.Color;

                var source = tagColor != null ? TooltipColorSource.Tag
                    : clauseColor != null ? TooltipColorSource.Clause
                    : resolved != null ? TooltipColorSource.RuleDefault
                    : TooltipColorSource.None;

                var candidate = new TooltipEffectMatch(rule.Kind, resolved, rule.Strength, source);

                // A match WITH a color beats a same-kind match that has none; otherwise the first
                // one found (in rule-list order) wins. See the class remarks.
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
    /// Attempts to resolve one color TOKEN - the value inside a `[color=value]` tag, or (from the
    /// settings window) whatever the person typed into a manual hex box. Accepts a hex triplet/
    /// quad (`ff69b4`, `#ff69b4`, `0xff69b4`, or the 3/4-digit CSS-style shorthand `f6b`/`f6bf`) or a
    /// name from <see cref="NamedColors"/>. Returns false (leaving <paramref name="rgb"/> as
    /// default) for anything else, rather than guessing.
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

        return NamedColors.TryGetValue(t, out rgb);
    }

    private static bool IsAllHex(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    /// <summary>
    /// Strips every markup tag (see <see cref="TagToken"/>) from <paramref name="raw"/>, and
    /// records which stretch of the STRIPPED result (if any) a `[color=]` tag covered - so a later
    /// keyword match's plain-text character index can be checked directly against
    /// <paramref name="colorRuns"/> without either side needing to re-account for tag lengths.
    /// An unresolvable color value (see <see cref="TryResolveColorToken"/>) is treated as no color
    /// starting at that tag, exactly as if it had been `[/color]` - it still gets removed from the
    /// text, it just doesn't tint anything.
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
            // [glow=]/[/glow]/[i]/[/i]: no color-run bookkeeping, just already removed from `plain`.
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
        foreach (var r in runs)
            if (idx >= r.Start && idx < r.End) return r.Color;
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
                return NamedColors.TryGetValue(cw.Value, out var rgb) ? rgb : null;
            }
            break; // found the containing clause; it just has no color word in it
        }
        return null;
    }
}
