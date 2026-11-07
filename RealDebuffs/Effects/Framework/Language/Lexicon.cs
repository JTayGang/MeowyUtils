namespace RealDebuffs.Effects.Framework;

public readonly record struct EffectHit(DebuffKind Kind, bool Noun);
public readonly record struct MaterialHit(string Name, SlotType Type, bool Adj);

internal enum PhraseKind { Effect, Link }
internal sealed record Phrase(string[] Words, PhraseKind Kind, bool Made, EffectHit[] Effects);

/// <summary>Every word the parser knows: effect triggers, material words (with generated inflections), links, fillers.</summary>
public sealed class Lexicon
{
    private static readonly HashSet<string> Det = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "this", "these", "those", "your", "his", "her", "their", "its", "my", "our",
        "some", "many", "several", "countless", "every", "each", "all", "any", "another", "one", "two", "three",
    };

    private static readonly HashSet<string> Breaker = new(StringComparer.OrdinalIgnoreCase)
    {
        "by", "around", "over", "under", "into", "onto", "upon", "through", "across", "between", "against", "toward", "towards",
        "near", "beside", "behind", "above", "below", "along", "within", "without", "which", "who", "that", "when", "while", "until",
        "you", "is", "are", "was", "were", "be", "been", "being", "has", "have", "had", "will", "can", "may", "to", "for", "at",
        "not", "no", "it", "they", "them", "he", "she", "i", "we", "if", "then", "than", "so", "or",
    };

    private static readonly (string[] Words, bool Made)[] LinkPhrases =
    {
        (new[] { "made", "of" }, true), (new[] { "made", "from" }, true), (new[] { "made", "out", "of" }, true), (new[] { "made", "with" }, true),
        (new[] { "covered", "in" }, false), (new[] { "covered", "with" }, false), (new[] { "covered", "by" }, false),
        (new[] { "coated", "in" }, false), (new[] { "coated", "with" }, false), (new[] { "wreathed", "in" }, false), (new[] { "wreathed", "with" }, false),
        (new[] { "wrapped", "in" }, false), (new[] { "wrapped", "with" }, false), (new[] { "laced", "with" }, false),
        (new[] { "dripping", "with" }, false), (new[] { "dripping", "in" }, false), (new[] { "engulfed", "in" }, false), (new[] { "engulfed", "by" }, false),
        (new[] { "enveloped", "in" }, false), (new[] { "enveloped", "by" }, false), (new[] { "shrouded", "in" }, false), (new[] { "cloaked", "in" }, false),
        (new[] { "bathed", "in" }, false), (new[] { "soaked", "in" }, false), (new[] { "studded", "with" }, false), (new[] { "crawling", "with" }, false),
        (new[] { "adorned", "with" }, false), (new[] { "out", "of" }, true),
    };

    private static readonly (string Word, bool Made)[] LinkWords =
    {
        ("of", true), ("from", true), ("as", true), ("with", false), ("in", false), ("on", false),
        ("dripping", false), ("shedding", false), ("spewing", false), ("spitting", false), ("leaking", false), ("trailing", false),
    };

    /// <summary>Generated forms that mean something else in plain speech, so they never trigger an effect.</summary>
    private static readonly HashSet<string> NeverTrigger = new(StringComparer.OrdinalIgnoreCase) { "icing", "frosting", "charming", "charms" };

    private readonly Dictionary<string, List<EffectHit>> _effects = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MaterialHit> _materials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Phrase>> _phrases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DebuffKind, string> _trigger = new();
    private readonly Dictionary<string, string> _canonical = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _problems = new();
    private string[] _suggest = Array.Empty<string>();

    public IReadOnlyList<string> Problems => _problems;

    public Lexicon(
        IEnumerable<ISceneEffect> effects,
        IEnumerable<(DebuffKind Kind, string Word)>? extras = null,
        ISet<DebuffKind>? offKinds = null,
        IEnumerable<(DebuffKind Kind, string Word)>? muted = null)
    {
        var mutedSet = new HashSet<(DebuffKind, string)>();
        if (muted is not null)
            foreach (var (k, w) in muted) mutedSet.Add((k, w.Trim().ToLowerInvariant()));

        foreach (var e in effects)
        {
            bool off = offKinds?.Contains(e.Kind) == true;
            foreach (var raw in e.TriggerKeywords)
            {
                var kw = raw.Trim().ToLowerInvariant();
                if (kw.Length == 0) continue;
                if (!_trigger.ContainsKey(e.Kind) && !kw.Contains(' ') && !Inflect.LooksParticiple(kw)) _trigger[e.Kind] = kw;
                if (off || mutedSet.Contains((e.Kind, kw))) continue;
                AddKeyword(e.Kind, kw, inflect: true);
            }
        }

        if (extras is not null)
            foreach (var (k, w) in extras)
            {
                var kw = w.Trim().ToLowerInvariant();
                if (kw.Length > 0) AddKeyword(k, kw, inflect: false);
            }

        BuildMaterials();
        BuildLinks();
        CheckVocabulary();
        BuildSuggestions();
    }

    public IReadOnlyList<EffectHit> EffectsFor(string word) =>
        _effects.TryGetValue(word, out var l) ? l : Array.Empty<EffectHit>();

    public bool TryMaterial(string word, out MaterialHit hit) => _materials.TryGetValue(word, out hit);
    public bool IsDet(string word) => Det.Contains(word);
    public bool IsBreaker(string word) => Breaker.Contains(word);
    public bool TryLink(string word, out bool made) => _links.TryGetValue(word, out made);
    internal IReadOnlyList<Phrase> PhrasesStarting(string word) =>
        _phrases.TryGetValue(word, out var l) ? l : Array.Empty<Phrase>();

    /// <summary>A noun the status text can use to trigger this effect (the first non-participle keyword), or null if it has none.</summary>
    public string? TriggerWord(DebuffKind kind) => _trigger.GetValueOrDefault(kind);

    /// <summary>The shortest plural noun for a material (else shortest noun), for phrases the generator writes.</summary>
    public string? CanonicalWord(string materialName) => _canonical.GetValueOrDefault(materialName);

    /// <summary>The nearest known word to a typo, or null; suggestions are only ever shown, never applied.</summary>
    public string? Suggest(string word)
    {
        if (word.Length < 4) return null;
        int limit = word.Length >= 8 ? 2 : 1;
        string? best = null;
        int bestD = limit + 1;
        foreach (var c in _suggest)
        {
            if (Math.Abs(c.Length - word.Length) > limit) continue;
            int d = Distance(word, c, bestD);
            if (d < bestD || (d == bestD && best is not null && string.CompareOrdinal(c, best) < 0)) { best = c; bestD = d; }
        }
        return bestD <= limit ? best : null;
    }

    private void AddKeyword(DebuffKind kind, string kw, bool inflect)
    {
        if (kw.Contains(' '))
        {
            AddPhrase(new Phrase(kw.Split(' ', StringSplitOptions.RemoveEmptyEntries), PhraseKind.Effect, false, new[] { new EffectHit(kind, true) }));
            return;
        }

        if (!inflect) { AddEffect(kind, kw, !Inflect.LooksParticiple(kw)); return; }

        AddEffect(kind, kw, !Inflect.LooksParticiple(kw));
        if (Inflect.LooksParticiple(kw) || kw.EndsWith("is") || kw.EndsWith("us") || kw.EndsWith("ss")) return;

        string b = Inflect.Singular(kw);
        AddEffect(kind, b, true);
        AddEffect(kind, Inflect.Plural(b), true);
        AddEffect(kind, Inflect.Past(b), false);
        AddEffect(kind, Inflect.Ing(b), false);
    }

    private void AddEffect(DebuffKind kind, string word, bool noun)
    {
        if (NeverTrigger.Contains(word)) return;
        if (!_effects.TryGetValue(word, out var list)) _effects[word] = list = new List<EffectHit>();
        foreach (var h in list) if (h.Kind == kind) return;
        list.Add(new EffectHit(kind, noun));
    }

    private void AddPhrase(Phrase p)
    {
        if (!_phrases.TryGetValue(p.Words[0], out var list)) _phrases[p.Words[0]] = list = new List<Phrase>();
        list.Add(p);
        list.Sort((a, b) => b.Words.Length.CompareTo(a.Words.Length));
    }

    private void BuildLinks()
    {
        foreach (var (w, made) in LinkWords) _links[w] = made;
        foreach (var (words, made) in LinkPhrases) AddPhrase(new Phrase(words, PhraseKind.Link, made, Array.Empty<EffectHit>()));
    }

    private void BuildMaterials()
    {
        var words = new List<(string Word, IMaterial M, SlotType Type)>();
        foreach (var m in MaterialRegistry.AllMaterials)
        {
            SlotType? type = m switch { IStrokeMaterial => SlotType.Stroke, IParticleMaterial => SlotType.Particle, _ => null };
            if (type is null) continue;
            foreach (var w in m.NaturalLanguageWords)
            {
                var lw = w.Trim().ToLowerInvariant();
                if (lw.Length > 0 && !lw.Contains(' ')) words.Add((lw, m, type.Value));
            }
        }

        // Authored words first so a generated form can never take one over.
        foreach (var (w, m, type) in words)
            _materials.TryAdd(w, new MaterialHit(m.Name, type, Inflect.LooksAdjectival(w)));

        foreach (var (w, m, type) in words)
        {
            string b = Inflect.Singular(w);
            if (b != w) { Gen(b, m, type, false); continue; }

            Gen(Inflect.Plural(w), m, type, false);
            if (type != SlotType.Particle || w.Length < 4 || Inflect.LooksAdjectival(w)) continue;
            Gen(Inflect.Past(w), m, type, true);
            Gen(Inflect.Ing(w), m, type, true);
            Gen(Inflect.Y(w), m, type, true);
        }

        foreach (var g in words.GroupBy(x => x.M.Name))
        {
            var nouns = g.Select(x => x.Word).Where(w => !Inflect.LooksAdjectival(w)).ToList();
            var plurals = nouns.Where(w => Inflect.Singular(w) != w).ToList();
            string? best = (plurals.Count > 0 ? plurals : nouns)
                .OrderBy(w => w.Length).ThenBy(w => w, StringComparer.Ordinal).FirstOrDefault();
            if (best is not null) _canonical[g.Key] = best;
        }

        void Gen(string w, IMaterial m, SlotType type, bool adj) => _materials.TryAdd(w, new MaterialHit(m.Name, type, adj));
    }

    private void CheckVocabulary()
    {
        foreach (var w in ColorLexicon.Named.Keys)
        {
            if (_materials.ContainsKey(w)) _problems.Add($"colour word \"{w}\" is also a material word");
            if (_effects.ContainsKey(w)) _problems.Add($"colour word \"{w}\" is also an effect trigger");
        }
        foreach (var (w, _) in LinkWords)
            if (_effects.ContainsKey(w) || _materials.ContainsKey(w)) _problems.Add($"link word \"{w}\" is also an effect or material word");
        foreach (var (words, _) in LinkPhrases)
            if (_effects.ContainsKey(words[0]) || _materials.ContainsKey(words[0])) _problems.Add($"link phrase \"{string.Join(' ', words)}\" starts with an effect or material word");
        foreach (var w in Det)
            if (_effects.ContainsKey(w) || _materials.ContainsKey(w)) _problems.Add($"filler word \"{w}\" is also an effect or material word");
        foreach (var w in Breaker)
            if (_effects.ContainsKey(w) || _materials.ContainsKey(w)) _problems.Add($"filler word \"{w}\" is also an effect or material word");
        _problems.AddRange(LookCode.VocabularyProblems());
    }

    private void BuildSuggestions()
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in _effects.Keys) all.Add(w);
        foreach (var w in _materials.Keys) all.Add(w);
        foreach (var w in ColorLexicon.Named.Keys) all.Add(w);
        _suggest = all.Where(w => w.Length >= 4).OrderBy(w => w, StringComparer.Ordinal).ToArray();
    }

    private static int Distance(string a, string b, int cap)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            int rowMin = cur[0];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                rowMin = Math.Min(rowMin, cur[j]);
            }
            if (rowMin >= cap) return cap;
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

/// <summary>Deterministic word forms: no stemmer, just generated variants of listed words.</summary>
internal static class Inflect
{
    private static readonly HashSet<string> Irregular = new(StringComparer.OrdinalIgnoreCase)
    {
        "bound", "forgot", "insane", "hysterical", "forgetful", "voiceless", "demonic", "icy", "aflame", "ablaze", "damned",
    };

    private static readonly HashSet<string> NounY = new(StringComparer.OrdinalIgnoreCase) { "memory", "glory", "fury", "envy" };

    public static bool LooksParticiple(string w) =>
        w.EndsWith("ed") || w.EndsWith("ing") || (w.EndsWith("en") && w.Length > 4) || Irregular.Contains(w);

    public static bool LooksAdjectival(string w)
    {
        if (LooksParticiple(w) || w.EndsWith("ish")) return true;
        if (w.Length > 3 && w.EndsWith("y") && !NounY.Contains(w) && !"aeo".Contains(w[^2])) return true;
        return false;
    }

    public static string Singular(string w)
    {
        if (w.EndsWith("ies") && w.Length > 4) return w[..^3] + "y";
        if (w.EndsWith("ches") || w.EndsWith("shes") || w.EndsWith("xes") || w.EndsWith("zes") || w.EndsWith("sses")) return w[..^2];
        if (w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("is") && !w.EndsWith("us")) return w[..^1];
        return w;
    }

    public static string Plural(string b)
    {
        if (b.EndsWith('s') || b.EndsWith('x') || b.EndsWith('z') || b.EndsWith("ch") || b.EndsWith("sh")) return b + "es";
        if (b.EndsWith('y') && b.Length > 1 && !"aeiou".Contains(b[^2])) return b[..^1] + "ies";
        return b + "s";
    }

    public static string Past(string b) =>
        b.EndsWith('e') ? b + "d" : b.EndsWith('y') && b.Length > 1 && !"aeiou".Contains(b[^2]) ? b[..^1] + "ied" : b + "ed";

    public static string Ing(string b) => b.EndsWith('e') && !b.EndsWith("ee") ? b[..^1] + "ing" : b + "ing";

    public static string Y(string b) => b.EndsWith('e') ? b[..^1] + "y" : b.EndsWith('y') ? b : b + "y";
}
