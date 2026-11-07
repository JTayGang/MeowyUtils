using System.Text;

namespace RealDebuffs.Effects.Framework;

/// <summary>A Look as text: a stable one-line description (tests, logs) and the natural phrase the generator exports.</summary>
public static class LookText
{
    public static string Describe(Look l)
    {
        var sb = new StringBuilder();
        sb.Append(l.Kind).Append(" body=").Append(Mat(l.Body)).Append(" mod=").Append(Mat(l.Modifier))
          .Append(" c=").Append(Col(l.BodyColor)).Append(" mc=").Append(Col(l.ModifierColor));
        if (l.Details is { Count: > 0 } d)
            foreach (var kv in d.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(" x:").Append(kv.Key).Append('=').Append(Mat(kv.Value));
        return sb.ToString();
    }

    /// <summary>The Look as a sentence the parser reads back identically, or null when only a code can say it.</summary>
    public static string? Phrase(Look look, Lexicon lex)
    {
        if (look.Details is { Count: > 0 } || look.Modifier == Look.NoShed) return null;
        if (lex.TriggerWord(look.Kind) is not { } trigger) return null;

        string? bodyWord = null, modWord = null;
        if (look.Body is { } b && (bodyWord = lex.CanonicalWord(b)) is null) return null;
        if (look.Modifier is { } m && (modWord = lex.CanonicalWord(m)) is null) return null;

        var sb = new StringBuilder();
        if (bodyWord is not null) sb.Append(trigger).Append(" made of ").Append(Prefix(look.BodyColor)).Append(bodyWord);
        else sb.Append(Prefix(look.BodyColor)).Append(trigger);
        if (modWord is not null) sb.Append(" with ").Append(Prefix(look.ModifierColor)).Append(modWord);
        return sb.ToString();
    }

    private static string Prefix(LookColor? c) => c is { } v ? ColorLexicon.NameOf(v) + " " : "";
    private static string Mat(string? m) => m is null ? "-" : m == Look.NoShed ? "none" : LookCode.Compact(m);
    private static string Col(LookColor? c) => c is { } v ? (v.Rainbow ? "rainbow" : ColorLexicon.HexOf(v.Rgb)) : "-";
}
