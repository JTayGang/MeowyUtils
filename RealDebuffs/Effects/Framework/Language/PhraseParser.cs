using System.Text;
using System.Text.RegularExpressions;

namespace RealDebuffs.Effects.Framework;

public sealed class ParseResult
{
    public static readonly ParseResult Empty = new(Array.Empty<Look>(), Array.Empty<string>(), Array.Empty<string>());

    /// <summary>One Look per effect the text asks for, ordered by kind.</summary>
    public IReadOnlyList<Look> Looks { get; }

    /// <summary>Parts that were understood but have nowhere to go ("eyes can't be shed by Heavy").</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>Typo suggestions and code warnings, for the settings tester only.</summary>
    public IReadOnlyList<string> Hints { get; }

    public ParseResult(IReadOnlyList<Look> looks, IReadOnlyList<string> notes, IReadOnlyList<string> hints)
    {
        Looks = looks; Notes = notes; Hints = hints;
    }
}

/// <summary>Turns status text into Looks: a colour sticks to the next noun, an adjective or linked noun modifies its effect noun, leftovers trigger their own effect.</summary>
public static class PhraseParser
{
    private enum IK { Word, Colour, Link, Comma, And, Stop }

    private sealed class Item
    {
        public IK Kind;
        public string Text = "";
        public LookColor? Colour;
        public LookColor? Tag;
        public LookColor? Attached;
        public bool Made;
        public EffectHit[] Effects = Array.Empty<EffectHit>();
        public MaterialHit? Mat;
        public bool Det, Breaker;
        public int Owner = -1;

        public bool Meaningful => Kind == IK.Word && (Effects.Length > 0 || Mat is not null);
        public bool Adj => Mat is { Adj: true };
        public LookColor? Paint => Attached ?? Tag;
    }

    private sealed class Np
    {
        public int Head, Start, LinkTo = -1, LinkedFrom = -1;
        public bool Made;
        public readonly List<int> Attrs = new();
    }

    private readonly record struct Raw(string Text, int Start, char Punct, bool HyphenNext);

    private static readonly Regex TagToken = new(
        @"\[color=(?<color>[0-9a-z]+)\]|\[/color\]|\[glow=[0-9a-z]+\]|\[/glow\]|\[i\]|\[/i\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Token = new(
        @"#[0-9A-Fa-f]{3}(?:[0-9A-Fa-f]{3})?(?![0-9A-Za-z])|[A-Za-z]+(?:'[A-Za-z]+)?(?:-[A-Za-z]+(?:'[A-Za-z]+)?)*|[,;:.!?()\n]",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static ParseResult Parse(string? text, Lexicon lex)
    {
        if (string.IsNullOrWhiteSpace(text)) return ParseResult.Empty;

        var (plain, runs) = StripTags(text);
        var hints = new List<string>();
        var notes = new List<string>();
        var coded = new Dictionary<DebuffKind, Look>();

        var blank = new StringBuilder(plain);
        foreach (Match m in LookCode.Find(plain))
        {
            var (look, warnings) = LookCode.Decode(m.Value);
            hints.AddRange(warnings);
            if (look is not null) coded.TryAdd(look.Kind, look);
            for (int i = 0; i < m.Length; i++) blank[m.Index + i] = ' ';
        }

        var items = Tokenize(blank.ToString(), runs, lex);
        var looks = new Dictionary<DebuffKind, Look>();
        Analyse(items, lex, looks, notes);
        SuggestTypos(items, lex, hints);

        foreach (var kv in coded) looks[kv.Key] = kv.Value;

        var result = new List<Look>(looks.Count);
        foreach (var kind in looks.Keys.OrderBy(k => (int)k)) result.Add(Normalise(looks[kind]));
        return result.Count == 0 && notes.Count == 0 && hints.Count == 0
            ? ParseResult.Empty
            : new ParseResult(result, notes, hints);
    }

    // ---- tokenising ------------------------------------------------------------------------------------------------

    private static List<Item> Tokenize(string plain, List<(int Start, int End, LookColor Color)> runs, Lexicon lex)
    {
        var raw = new List<Raw>();
        foreach (Match m in Token.Matches(plain))
        {
            string s = m.Value;
            if (s.Length == 1 && ",;:.!?()\n".Contains(s[0])) { raw.Add(new Raw("", m.Index, s[0], false)); continue; }
            if (s[0] == '#') { raw.Add(new Raw(s, m.Index, '#', false)); continue; }

            var parts = s.ToLowerInvariant().Split('-');
            int at = m.Index;
            for (int p = 0; p < parts.Length; p++)
            {
                string w = parts[p];
                int ap = w.IndexOf('\'');
                if (ap >= 0) w = w[..ap];
                raw.Add(new Raw(w, at, 'w', p < parts.Length - 1));
                at += parts[p].Length + 1;
            }
        }

        var items = new List<Item>(raw.Count);
        for (int i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            var tag = TagAt(runs, r.Start);

            if (r.Punct == ',') { items.Add(new Item { Kind = IK.Comma }); continue; }
            if (r.Punct != 'w' && r.Punct != '#') { items.Add(new Item { Kind = IK.Stop }); continue; }
            if (r.Punct == '#')
            {
                if (ColorLexicon.TryHex(r.Text, out var hex)) items.Add(new Item { Kind = IK.Colour, Colour = hex, Text = r.Text, Tag = tag });
                continue;
            }

            string w = r.Text;
            if (w.Length == 0) continue;

            int phraseLen = 0;
            Phrase? phrase = null;
            foreach (var ph in lex.PhrasesStarting(w))
            {
                if (i + ph.Words.Length > raw.Count) continue;
                bool ok = true;
                for (int k = 0; k < ph.Words.Length && ok; k++)
                    ok = raw[i + k].Punct == 'w' && raw[i + k].Text == ph.Words[k];
                if (ok) { phrase = ph; phraseLen = ph.Words.Length; break; }
            }
            if (phrase is not null)
            {
                items.Add(phrase.Kind == PhraseKind.Link
                    ? new Item { Kind = IK.Link, Made = phrase.Made, Text = string.Join(' ', phrase.Words), Tag = tag }
                    : new Item { Kind = IK.Word, Effects = phrase.Effects, Text = string.Join(' ', phrase.Words), Tag = tag });
                i += phraseLen - 1;
                continue;
            }

            // "dark red", "blood-red", "rust-red": a lead-in word to a colour word is part of the colour.
            if (i + 1 < raw.Count && raw[i + 1].Punct == 'w' && ColorLexicon.TryWord(raw[i + 1].Text, out var next)
                && (ColorLexicon.IsShade(w) || ColorLexicon.IsPrefix(w) || r.HyphenNext) && !ColorLexicon.TryWord(w, out _))
            {
                var c = ColorLexicon.IsShade(w) ? ColorLexicon.ApplyShade(w, next) : next;
                items.Add(new Item { Kind = IK.Colour, Colour = c, Text = w + " " + raw[i + 1].Text, Tag = tag });
                i++;
                continue;
            }

            if (ColorLexicon.TryWord(w, out var col)) { items.Add(new Item { Kind = IK.Colour, Colour = col, Text = w, Tag = tag }); continue; }
            if (w == "and") { items.Add(new Item { Kind = IK.And }); continue; }
            if (w == "but") { items.Add(new Item { Kind = IK.Stop }); continue; }
            if (lex.TryLink(w, out bool made)) { items.Add(new Item { Kind = IK.Link, Made = made, Text = w, Tag = tag }); continue; }

            var it = new Item { Kind = IK.Word, Text = w, Tag = tag, Det = lex.IsDet(w), Breaker = lex.IsBreaker(w) };
            var effects = lex.EffectsFor(w);
            if (effects.Count > 0) it.Effects = effects.ToArray();
            if (lex.TryMaterial(w, out var mat)) it.Mat = mat;
            items.Add(it);
        }
        return items;
    }

    // ---- analysis --------------------------------------------------------------------------------------------------

    private static void Analyse(List<Item> items, Lexicon lex, Dictionary<DebuffKind, Look> looks, List<string> notes)
    {
        AttachColours(items);

        var nps = new List<Np>();
        for (int h = items.Count - 1; h >= 0; h--)
        {
            var it = items[h];
            if (it.Meaningful && it.Owner < 0) nps.Add(BuildNp(items, h));
        }
        nps.Reverse();

        LinkNps(items, nps);

        for (int i = 0; i < nps.Count; i++)
        {
            if (nps[i].LinkedFrom >= 0) continue;
            var chain = new List<Np>();
            for (int j = i; j >= 0; j = nps[j].LinkTo) { chain.Add(nps[j]); if (nps[j].LinkTo < 0) break; }
            Chain(items, chain, looks, notes);
        }
    }

    private static void AttachColours(List<Item> items)
    {
        int n = items.Count;
        for (int i = 0; i < n; i++)
        {
            if (items[i].Kind != IK.Colour) continue;
            int j = i + 1, fallback = -1, target = -1, unknowns = 0;
            while (j < n)
            {
                var it = items[j];
                if (it.Kind == IK.Colour) { target = -2; break; }
                if (it.Kind is IK.Comma or IK.And) { j++; continue; }
                if (it.Kind != IK.Word) break;
                if (it.Det) { j++; continue; }
                if (it.Meaningful)
                {
                    if (it.Adj) { if (fallback < 0) fallback = j; j++; continue; }
                    target = j;
                    break;
                }
                if (it.Breaker) break;
                if (unknowns < 1) { unknowns++; j++; continue; }
                break;
            }
            if (target == -2) continue;
            if (target < 0) target = fallback;
            if (target >= 0 && items[target].Attached is null) items[target].Attached = items[i].Colour;
        }
    }

    /// <summary>The noun phrase around a head: adjective forms and adjacent noun adjuncts to its left become attributes.</summary>
    private static Np BuildNp(List<Item> items, int h)
    {
        var np = new Np { Head = h, Start = h };
        var head = items[h];
        bool nounHead = head.Mat is { Adj: false } || head.Effects.Any(e => e.Noun);
        if (!nounHead) return np;

        int unknowns = 0;
        for (int j = h - 1; j >= 0; j--)
        {
            var it = items[j];
            if (it.Owner >= 0) break;
            if (it.Kind == IK.Colour) { np.Start = j; continue; }
            if (it.Kind is IK.Comma or IK.And)
            {
                int k = j - 1;
                while (k >= 0 && items[k].Kind == IK.Word && items[k].Det) k--;
                if (k >= 0 && (items[k].Kind == IK.Colour || (items[k].Meaningful && items[k].Adj))) { np.Start = j; continue; }
                break;
            }
            if (it.Kind != IK.Word) break;
            if (it.Det) { np.Start = j; continue; }
            if (it.Meaningful)
            {
                bool adjacent = items[j + 1].Kind is IK.Word or IK.Colour;
                if (it.Adj || (it.Mat is not null && adjacent))
                {
                    np.Attrs.Insert(0, j);
                    it.Owner = h;
                    np.Start = j;
                    continue;
                }
                break;
            }
            if (it.Breaker) break;
            if (unknowns < 1) { unknowns++; np.Start = j; continue; }
            break;
        }
        return np;
    }

    /// <summary>Joins "head [,] link phrase" to the next noun phrase when only filler sits between.</summary>
    private static void LinkNps(List<Item> items, List<Np> nps)
    {
        int n = items.Count;
        for (int i = 0; i < nps.Count - 1; i++)
        {
            int k = nps[i].Head + 1;
            if (k < n && items[k].Kind == IK.Comma) k++;
            if (k >= n || items[k].Kind != IK.Link) continue;

            var next = nps[i + 1];
            if (next.Start <= k) continue;
            bool clean = next.Start - (k + 1) <= 3;
            for (int m = k + 1; m < next.Start && clean; m++)
                clean = items[m].Kind == IK.Word && !items[m].Meaningful && !items[m].Breaker;
            if (!clean) continue;

            nps[i].LinkTo = i + 1;
            next.LinkedFrom = i;
            next.Made = items[k].Made;
        }
    }

    private enum Target { None, Body, Modifier }

    private sealed class State
    {
        public DebuffKind Kind;
        public string? Body, Modifier;
        public bool BodyClaimed, ModifierClaimed;
        public LookColor? BodyColor, ModifierColor, RootColor;
    }

    private static void Chain(List<Item> items, List<Np> chain, Dictionary<DebuffKind, Look> looks, List<string> notes)
    {
        int ci = 0;
        while (ci < chain.Count)
        {
            var root = chain[ci];
            var rootItem = items[root.Head];
            if (rootItem.Effects.Length == 0) { ci++; continue; }

            var states = rootItem.Effects.Select(e => new State { Kind = e.Kind, RootColor = rootItem.Paint }).ToArray();
            var released = new List<Item>();

            foreach (int a in root.Attrs) ApplyAll(states, items[a], false, false, released, notes);

            int next = chain.Count;
            for (int j = ci + 1; j < chain.Count; j++)
            {
                var np = chain[j];
                var head = items[np.Head];
                bool fits = head.Mat is not null && ApplyAll(states, head, np.Made, true, released, notes, probe: true);
                if (!fits && head.Effects.Length > 0) { next = j; break; }
                if (head.Mat is not null) ApplyAll(states, head, np.Made, true, released, notes);
                foreach (int a in np.Attrs) ApplyAll(states, items[a], false, false, released, notes);
            }

            foreach (var st in states) Emit(st, looks);
            foreach (var it in released)
                foreach (var e in it.Effects) Merge(looks, new Look(e.Kind, BodyColor: it.Paint));

            ci = next;
        }
    }

    /// <summary>Applies a material word to every kind of a root; true if the first kind could take it (or it only repeats the effect).</summary>
    private static bool ApplyAll(State[] states, Item it, bool made, bool isHead, List<Item> released, List<string> notes, bool probe = false)
    {
        if (it.Mat is not { } mat) return true;

        bool first = true, firstOk = false;
        foreach (var st in states)
        {
            bool ok = Apply(st, it, mat, made, probe);
            if (first) { firstOk = ok; first = false; }
        }
        if (probe) return firstOk;

        if (!firstOk)
        {
            if (it.Effects.Length > 0 && !isHead) released.Add(it);
            else notes.Add($"\"{it.Text}\" has nowhere to go on {states[0].Kind}");
        }
        return firstOk;
    }

    private static bool Apply(State st, Item it, MaterialHit mat, bool made, bool probe)
    {
        foreach (var e in it.Effects)
            if (e.Kind == st.Kind) return true;

        var target = Classify(st.Kind, mat, made);
        if (target == Target.None) return false;
        if (probe) return true;

        if (target == Target.Body)
        {
            if (st.BodyClaimed) return true;
            st.BodyClaimed = true;
            st.Body = mat.Name;
            st.BodyColor = it.Paint;
        }
        else
        {
            if (st.ModifierClaimed) return true;
            st.ModifierClaimed = true;
            st.Modifier = mat.Name;
            st.ModifierColor = it.Paint;
        }
        return true;
    }

    private static Target Classify(DebuffKind kind, MaterialHit mat, bool made)
    {
        var body = EffectRegistry.BodyFor(kind);
        if (body is null) return Target.None;

        if (mat.Type == SlotType.Stroke)
            return body.Value.Type == SlotType.Stroke ? Target.Body : Target.None;

        if (mat.Type != SlotType.Particle) return Target.None;

        if (body.Value.Type == SlotType.Stroke)
        {
            if (EffectRegistry.ModifierFor(kind) is not null) return Target.Modifier;
            return MaterialRegistry.TryGetParticle(mat.Name) is { } p && p.Emissions.Length > 0 ? Target.Modifier : Target.None;
        }

        if (body.Value.Type == SlotType.Particle)
            return EffectRegistry.ModifierFor(kind) is not null && !made ? Target.Modifier : Target.Body;

        return Target.None;
    }

    private static void Emit(State st, Dictionary<DebuffKind, Look> looks)
    {
        var bodyColor = st.BodyColor ?? st.RootColor;
        Merge(looks, new Look(st.Kind, st.Body, st.Modifier, bodyColor, st.ModifierClaimed ? st.ModifierColor : null));
    }

    private static void Merge(Dictionary<DebuffKind, Look> looks, Look look) =>
        looks[look.Kind] = looks.TryGetValue(look.Kind, out var have) ? have.Fill(look) : look;

    /// <summary>A material equal to the slot's own default says nothing, so it is dropped.</summary>
    private static Look Normalise(Look look)
    {
        string? body = look.Body, mod = look.Modifier;
        if (body is not null && EffectRegistry.BodyFor(look.Kind) is { } bs
            && string.Equals(bs.DefaultMaterial, body, StringComparison.OrdinalIgnoreCase)) body = null;
        if (mod is not null && EffectRegistry.ModifierFor(look.Kind) is { } ms
            && string.Equals(ms.DefaultMaterial, mod, StringComparison.OrdinalIgnoreCase)) mod = null;
        return body == look.Body && mod == look.Modifier ? look : look with { Body = body, Modifier = mod };
    }

    private static void SuggestTypos(List<Item> items, Lexicon lex, List<string> hints)
    {
        foreach (var it in items)
        {
            if (hints.Count >= 3) return;
            if (it.Kind != IK.Word || it.Meaningful || it.Det || it.Breaker || it.Text.Length < 5) continue;
            if (lex.Suggest(it.Text) is { } s && s != it.Text) hints.Add($"\"{it.Text}\" isn't a known word - did you mean \"{s}\"?");
        }
    }

    // ---- markup ----------------------------------------------------------------------------------------------------

    private static LookColor? TagAt(List<(int Start, int End, LookColor Color)> runs, int idx)
    {
        foreach (var r in runs) if (idx >= r.Start && idx < r.End) return r.Color;
        return null;
    }

    /// <summary>Strips markup and records which stretch of the result each [color=] run covers.</summary>
    private static (string Plain, List<(int Start, int End, LookColor Color)> Runs) StripTags(string raw)
    {
        var plain = new StringBuilder(raw.Length);
        var runs = new List<(int, int, LookColor)>();
        LookColor? current = null;
        int runStart = 0, cursor = 0;

        void Flush(int upto)
        {
            if (current is { } c && upto > runStart) runs.Add((runStart, upto, c));
            runStart = upto;
        }

        foreach (Match m in TagToken.Matches(raw))
        {
            plain.Append(raw, cursor, m.Index - cursor);
            cursor = m.Index + m.Length;
            if (m.Groups["color"].Success)
            {
                Flush(plain.Length);
                current = ColorLexicon.TryLookColor(m.Groups["color"].Value, out var c) ? c : null;
            }
            else if (m.Value.Equals("[/color]", StringComparison.OrdinalIgnoreCase))
            {
                Flush(plain.Length);
                current = null;
            }
        }
        plain.Append(raw, cursor, raw.Length - cursor);
        Flush(plain.Length);
        return (plain.ToString(), runs);
    }
}
