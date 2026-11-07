using System.Text;
using System.Text.RegularExpressions;

namespace RealDebuffs.Effects.Framework;

/// <summary>The exact, copyable form of a Look, e.g. RD1-heavy-m.ember-c.D92626 (b body, m modifier or none, c / k colours, x raw slot); unknown parts are skipped and reported.</summary>
public static class LookCode
{
    public const string Prefix = "RD1";

    private static readonly Regex Rx = new(@"(?<![A-Za-z0-9])RD1(?:-[A-Za-z0-9]+(?:\.[A-Za-z0-9]+)*)+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Dictionary<string, string>? _stroke, _particle, _region;

    public static IEnumerable<Match> Find(string text) => Rx.Matches(text);

    public static string Compact(string materialName)
    {
        int dot = materialName.IndexOf('.');
        return (dot >= 0 ? materialName[(dot + 1)..] : materialName).Replace("-", "").ToLowerInvariant();
    }

    public static string Encode(Look look)
    {
        var sb = new StringBuilder(Prefix).Append('-').Append(look.Kind.ToString().ToLowerInvariant());
        if (look.Body is { } b) sb.Append("-b.").Append(Compact(b));
        if (look.Modifier is { } m) sb.Append("-m.").Append(m == Look.NoShed ? "none" : Compact(m));
        if (look.BodyColor is { } c) sb.Append("-c.").Append(ColorText(c));
        if (look.ModifierColor is { } k) sb.Append("-k.").Append(ColorText(k));

        if (look.Details is { Count: > 0 } d)
            foreach (var slot in EffectRegistry.SlotsFor(look.Kind))
            {
                if (d.TryGetValue(MaterialOverrideKey.For(look.Kind, slot), out var v))
                    sb.Append("-x.").Append(slot.Type switch { SlotType.Stroke => "s", SlotType.Particle => "p", _ => "r" })
                      .Append(slot.Type == SlotType.Region ? slot.Region.ToString() : slot.Role.ToString()).Append('.').Append(Compact(v));
                if (slot.Type == SlotType.Stroke && d.TryGetValue(MaterialOverrideKey.ForStrokeEmit(look.Kind, slot.Role), out var e))
                    sb.Append("-x.e").Append(slot.Role.ToString()).Append('.').Append(Compact(e));
            }
        return sb.ToString();
    }

    /// <summary>Decodes one code; Look is null only when the effect name is unknown. Warnings name every part that was skipped.</summary>
    public static (Look? Look, List<string> Warnings) Decode(string code)
    {
        var warn = new List<string>();
        var segs = code.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length < 2 || !segs[0].Equals(Prefix, StringComparison.OrdinalIgnoreCase)) { warn.Add($"\"{code}\" isn't a Real Debuffs code"); return (null, warn); }
        if (!char.IsLetter(segs[1][0]) || !Enum.TryParse<DebuffKind>(segs[1], true, out var kind) || !Enum.IsDefined(kind))
        { warn.Add($"code names an unknown effect \"{segs[1]}\""); return (null, warn); }

        string? body = null, modifier = null;
        LookColor? bodyColor = null, modColor = null;
        Dictionary<string, string>? details = null;

        for (int i = 2; i < segs.Length; i++)
        {
            var parts = segs[i].Split('.');
            string tag = parts[0].ToLowerInvariant();
            switch (tag)
            {
                case "b" when parts.Length == 2:
                {
                    var slot = EffectRegistry.BodyFor(kind);
                    body = slot is null ? null : Material(slot.Value.Type, parts[1]);
                    if (body is null) warn.Add($"body \"{parts[1]}\" isn't available for {kind}");
                    else if (EffectRegistry.DefaultFor(kind, slot!.Value.Type, slot.Value.Role) is { } def && string.Equals(def, body, StringComparison.OrdinalIgnoreCase) && slot.Value.Type != SlotType.Region) body = null;
                    break;
                }
                case "m" when parts.Length == 2:
                {
                    if (parts[1].Equals("none", StringComparison.OrdinalIgnoreCase)) { modifier = Look.NoShed; break; }
                    modifier = Material(SlotType.Particle, parts[1]);
                    if (modifier is null) warn.Add($"modifier \"{parts[1]}\" isn't a known particle material");
                    break;
                }
                case "c" when parts.Length == 2:
                    if (TryColor(parts[1], out var c)) bodyColor = c; else warn.Add($"colour \"{parts[1]}\" not understood");
                    break;
                case "k" when parts.Length == 2:
                    if (TryColor(parts[1], out var k)) modColor = k; else warn.Add($"modifier colour \"{parts[1]}\" not understood");
                    break;
                case "x" when parts.Length == 3 && parts[1].Length > 1:
                {
                    var key = DetailKey(kind, parts[1], parts[2], out var mat);
                    if (key is null || mat is null) warn.Add($"slot \"{parts[1]}={parts[2]}\" isn't available for {kind}");
                    else (details ??= new Dictionary<string, string>(StringComparer.Ordinal))[key] = mat;
                    break;
                }
                default:
                    warn.Add($"skipped \"{segs[i]}\"");
                    break;
            }
        }
        return (new Look(kind, body, modifier, bodyColor, modColor, details), warn);
    }

    /// <summary>Name collisions that would make two materials share a code word.</summary>
    public static IEnumerable<string> VocabularyProblems()
    {
        foreach (var (family, names) in new[] { ("stroke", MaterialRegistry.StrokeNames), ("particle", MaterialRegistry.ParticleNames), ("region", MaterialRegistry.RegionNames) })
            foreach (var g in names.GroupBy(Compact).Where(g => g.Count() > 1))
                yield return $"{family} materials {string.Join(" and ", g)} share the code name \"{g.Key}\"";
        foreach (var g in Enum.GetValues<DebuffKind>().GroupBy(k => k.ToString().ToLowerInvariant()).Where(g => g.Count() > 1))
            yield return $"effect names collide in codes: {g.Key}";
    }

    private static string ColorText(LookColor c) => c.Rainbow ? "rainbow" : ColorLexicon.HexOf(c.Rgb);

    private static bool TryColor(string s, out LookColor c)
    {
        c = default;
        if (s.Equals("rainbow", StringComparison.OrdinalIgnoreCase)) { c = new LookColor(ColorLexicon.RainbowAt(0), true); return true; }
        if (s.Length is not (3 or 6) || !s.All(Uri.IsHexDigit)) return false;
        if (!ColorLexicon.TryToken("#" + s, out System.Numerics.Vector4 rgb)) return false;
        c = new LookColor(rgb);
        return true;
    }

    private static string? Material(SlotType type, string compact)
    {
        var map = Map(type);
        return map.TryGetValue(compact.ToLowerInvariant(), out var full) ? full : null;
    }

    private static string? DetailKey(DebuffKind kind, string slotTag, string compactMat, out string? material)
    {
        material = null;
        char t = char.ToLowerInvariant(slotTag[0]);
        string name = slotTag[1..];
        SwappableSlot? hit = null;
        foreach (var slot in EffectRegistry.SlotsFor(kind))
        {
            bool match = t switch
            {
                's' => slot.Type == SlotType.Stroke && Enum.TryParse<PrimitiveRole>(name, true, out var r1) && r1 == slot.Role,
                'p' => slot.Type == SlotType.Particle && Enum.TryParse<PrimitiveRole>(name, true, out var r2) && r2 == slot.Role,
                'r' => slot.Type == SlotType.Region && Enum.TryParse<RegionKind>(name, true, out var rk) && rk == slot.Region,
                'e' => slot.Type == SlotType.Stroke && Enum.TryParse<PrimitiveRole>(name, true, out var r3) && r3 == slot.Role,
                _ => false,
            };
            if (match) { hit = slot; break; }
        }
        if (hit is null) return null;

        var family = t switch { 's' => SlotType.Stroke, 'r' => SlotType.Region, _ => SlotType.Particle };
        material = Material(family, compactMat);
        return t == 'e' ? MaterialOverrideKey.ForStrokeEmit(kind, hit.Value.Role) : MaterialOverrideKey.For(kind, hit.Value);
    }

    private static Dictionary<string, string> Map(SlotType type)
    {
        static Dictionary<string, string> Build(IEnumerable<string> names)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var n in names) d.TryAdd(Compact(n), n);
            return d;
        }
        return type switch
        {
            SlotType.Stroke => _stroke ??= Build(MaterialRegistry.StrokeNames),
            SlotType.Particle => _particle ??= Build(MaterialRegistry.ParticleNames),
            _ => _region ??= Build(MaterialRegistry.RegionNames),
        };
    }
}
