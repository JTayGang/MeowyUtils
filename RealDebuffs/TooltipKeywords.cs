using System.Text;
using RealDebuffs.Effects;
using RealDebuffs.Effects.Framework;

namespace RealDebuffs;

/// <summary>The live vocabulary (each effect's built-in words plus the user's extras) and a cache of parsed descriptions.</summary>
public static class TooltipLanguage
{
    private const int CacheLimit = 256;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, ParseResult> Cache = new(StringComparer.Ordinal);
    private static IReadOnlyList<ISceneEffect> _effects = Array.Empty<ISceneEffect>();
    private static Lexicon? _lexicon;
    private static string _signature = "";

    public static void Init(IReadOnlyList<ISceneEffect> effects)
    {
        lock (Gate) { _effects = effects; _lexicon = null; Cache.Clear(); }
    }

    /// <summary>The vocabulary under the user's current extras, switched-off effects and muted words; rebuilt only when they change.</summary>
    public static Lexicon For(Configuration config)
    {
        lock (Gate) return ForLocked(config);
    }

    /// <summary>Cached by description text; a change to the vocabulary clears the cache.</summary>
    public static ParseResult Parse(string? text, Configuration config)
    {
        if (string.IsNullOrWhiteSpace(text)) return ParseResult.Empty;
        lock (Gate)
        {
            var lex = ForLocked(config);
            if (Cache.TryGetValue(text, out var hit)) return hit;
            if (Cache.Count >= CacheLimit) Cache.Clear();
            return Cache[text] = PhraseParser.Parse(text, lex);
        }
    }

    private static Lexicon ForLocked(Configuration config)
    {
        string signature = Signature(config);
        if (_lexicon is not null && signature == _signature) return _lexicon;

        var extras = config.TooltipKeywordRules.Where(r => r.Enabled).SelectMany(r => r.ParsedKeywords.Select(w => (r.Kind, w))).ToList();
        var muted = config.MutedWords.SelectMany(kv => kv.Value.Select(w => (kv.Key, w))).ToList();
        _lexicon = new Lexicon(_effects, extras, config.TooltipOffKinds, muted);
        _signature = signature;
        Cache.Clear();
        return _lexicon;
    }

    private static string Signature(Configuration config)
    {
        var sb = new StringBuilder();
        foreach (var r in config.TooltipKeywordRules)
            if (r.Enabled) sb.Append((int)r.Kind).Append('=').Append(r.Keywords).Append('|');
        sb.Append('#');
        foreach (var k in config.TooltipOffKinds.OrderBy(k => (int)k)) sb.Append((int)k).Append(',');
        sb.Append('#');
        foreach (var kv in config.MutedWords.OrderBy(kv => (int)kv.Key)) sb.Append((int)kv.Key).Append(':').AppendJoin(',', kv.Value).Append(';');
        return sb.ToString();
    }
}
