using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs;

public enum TooltipColorSource { None, Tag, Clause }

/// <summary>One resolved "this kind should be active, looking like this"; MaterialSubstitution is set by a "made of X" phrase.</summary>
public readonly record struct TooltipEffectMatch(
    DebuffKind Kind,
    Vector4? Color,
    TooltipColorSource ColorSource,
    string? MaterialSubstitution = null);

/// <summary>Scans a tooltip for enabled rules' keywords; colour comes from a wrapping [color=] tag, else a colour word in the clause.</summary>
public static class TooltipKeywordParser
{
    /// <summary>The first enabled keyword for a kind, for building export phrases.</summary>
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

    /// <summary>True if the token is a recognized color word (fixed or rainbow).</summary>
    public static bool IsColorWord(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var t = token.Trim();
        return NamedColors.ContainsKey(t) || RainbowWords.Contains(t);
    }

    /// <summary>Shortest natural-language word for a material, for "made of X" export text.</summary>
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

    /// <summary>Closed vocabulary of color words. Also the source of the Effect generator's Color dropdown.</summary>
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

    /// <summary>Words that resolve to a hue cycling over time. Kept separate from NamedColors.</summary>
    private static readonly HashSet<string> RainbowWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "rgb", "rainbow",
    };

    /// <summary>Words a user can write after "made of" / "of" / "as" / "from" / "with" (from MaterialRegistry.Vocabulary).</summary>
    private static readonly IReadOnlyDictionary<string, string> MaterialWords = MaterialRegistry.Vocabulary;

    private static readonly Regex ColorWordPattern = BuildColorWordPattern();
    private static readonly Regex MaterialPhrase   = BuildMaterialPhrase();

    private static Regex BuildColorWordPattern()
    {
        var escaped = new List<string>(NamedColors.Count + RainbowWords.Count);
        foreach (var name in NamedColors.Keys) escaped.Add(Regex.Escape(name));
        foreach (var name in RainbowWords) escaped.Add(Regex.Escape(name));
        escaped.Sort((a, b) => b.Length.CompareTo(a.Length));
        return new Regex($@"\b({string.Join("|", escaped)})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static Regex BuildMaterialPhrase()
    {
        var escaped = new List<string>(MaterialWords.Count);
        foreach (var word in MaterialWords.Keys) escaped.Add(Regex.Escape(word));
        escaped.Sort((a, b) => b.Length.CompareTo(a.Length));
        var alternation = string.Join("|", escaped);
        return new Regex(
            $@"\b(?:made\s+of|of|as|from|with)\s+(?<material>{alternation})\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static Vector4 Rgb(float r, float g, float b) => new(r, g, b, 1f);

    /// <summary>Parses one tooltip: one match per kind, material phrases matched per clause, keywords inside a phrase only modify.</summary>
    public static IReadOnlyList<TooltipEffectMatch> Parse(string? tooltipText, IReadOnlyList<TooltipKeywordRule> rules)
    {
        if (string.IsNullOrWhiteSpace(tooltipText) || rules.Count == 0)
            return Array.Empty<TooltipEffectMatch>();

        var (plain, colorRuns) = StripAndMapColors(tooltipText);
        if (plain.Length == 0) return Array.Empty<TooltipEffectMatch>();

        var clauses = SplitClauses(plain);
        var colorWords = ColorWordPattern.Matches(plain);
        var best = new Dictionary<DebuffKind, TooltipEffectMatch>();

        var materialPhrases = new List<(int Start, int End, string Material)>();
        foreach (Match m in MaterialPhrase.Matches(plain))
        {
            if (MaterialWords.TryGetValue(m.Groups["material"].Value, out var matName))
                materialPhrases.Add((m.Index, m.Index + m.Length, matName));
        }

        // Resolves one keyword hit into a candidate (color from a [color=] tag or its clause) and keeps the best candidate per kind.
        void Consider(DebuffKind kind, int index, string? material)
        {
            Vector4? tagColor = FindTagColor(colorRuns, index);
            Vector4? clauseColor = tagColor == null ? FindClauseColor(clauses, colorWords, index) : null;
            Vector4? resolved = tagColor ?? clauseColor;

            var source = tagColor != null ? TooltipColorSource.Tag
                : clauseColor != null ? TooltipColorSource.Clause
                : TooltipColorSource.None;

            var candidate = new TooltipEffectMatch(kind, resolved, source, material);
            if (!best.TryGetValue(kind, out var existing) || (candidate.Color != null && existing.Color == null))
                best[kind] = candidate;
        }

        var accepted = new List<(DebuffKind Kind, int Index)>();
        var shadowed = new List<(DebuffKind Kind, int Index)>();

        foreach (var rule in rules)
        {
            if (!rule.Enabled) continue;
            var pattern = rule.GetPattern();
            if (pattern == null) continue;

            foreach (Match m in pattern.Matches(plain))
            {
                bool insideMaterial = false;
                for (int i = 0; i < materialPhrases.Count; i++)
                {
                    if (m.Index >= materialPhrases[i].Start && m.Index < materialPhrases[i].End)
                    {
                        insideMaterial = true;
                        break;
                    }
                }
                if (insideMaterial) { shadowed.Add((rule.Kind, m.Index)); continue; }

                accepted.Add((rule.Kind, m.Index));
                Consider(rule.Kind, m.Index, MaterialInSameClause(clauses, materialPhrases, m.Index));
            }
        }

        // A keyword inside "made of X" modifies another effect ("chains made of flames"); with nothing to modify ("a layer of frost") it activates.
        foreach (var (kind, index) in shadowed)
        {
            if (best.ContainsKey(kind)) continue;
            int clause = ClauseOf(clauses, index);
            bool hasHost = false;
            for (int i = 0; i < accepted.Count && !hasHost; i++)
                hasHost = accepted[i].Kind != kind && ClauseOf(clauses, accepted[i].Index) == clause;
            if (!hasHost) Consider(kind, index, null);
        }

        if (best.Count == 0) return Array.Empty<TooltipEffectMatch>();

        var result = new List<TooltipEffectMatch>(best.Count);
        foreach (var kv in best) result.Add(kv.Value);
        return result;
    }

    /// <summary>Index of the clause containing idx (-1 if none).</summary>
    private static int ClauseOf(List<(int Start, int End)> clauses, int idx)
    {
        for (int c = 0; c < clauses.Count; c++)
            if (idx >= clauses[c].Start && idx < clauses[c].End) return c;
        return -1;
    }

    /// <summary>The material phrase belonging to the clause containing idx, if any. First wins.</summary>
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

    /// <summary>Resolves one colour token: hex (with or without # / 0x, or CSS shorthand), a NamedColors name, or a rainbow word.</summary>
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

    private static bool TryResolveWord(string word, out Vector4 rgb)
    {
        if (RainbowWords.Contains(word)) { rgb = RainbowColor(Environment.TickCount64); return true; }
        return NamedColors.TryGetValue(word, out rgb);
    }

    /// <summary>Rainbow at a given time: hue advances one step per second across a 256-step wheel.</summary>
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

    /// <summary>Strips markup and records which stretch of the result each [color=] run covers, for plain-text index checks.</summary>
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
            break;
        }
        return null;
    }
}