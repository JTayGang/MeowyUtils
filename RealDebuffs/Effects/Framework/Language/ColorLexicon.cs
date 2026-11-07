using System.Numerics;

namespace RealDebuffs.Effects.Framework;

/// <summary>A colour as written: fixed RGB, or the cycling rainbow resolved when used.</summary>
public readonly record struct LookColor(Vector4 Rgb, bool Rainbow = false)
{
    public Vector4 Resolve(long nowMs) => Rainbow ? ColorLexicon.RainbowAt(nowMs) : Rgb;
}

/// <summary>Colour words, shades, hex and rainbow; the one table the parser, the settings dropdown and codes share.</summary>
public static class ColorLexicon
{
    public static readonly IReadOnlyDictionary<string, Vector4> Named = new Dictionary<string, Vector4>(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = Rgb(0.06f, 0.06f, 0.06f), ["blue"] = Rgb(0.20f, 0.45f, 0.90f), ["cyan"] = Rgb(0.20f, 0.85f, 0.90f),
        ["grayscale"] = Rgb(0.55f, 0.55f, 0.55f), ["green"] = Rgb(0.20f, 0.80f, 0.25f), ["magenta"] = Rgb(0.85f, 0.15f, 0.75f),
        ["navy"] = Rgb(0.08f, 0.15f, 0.45f), ["orange"] = Rgb(0.95f, 0.50f, 0.10f), ["pink"] = Rgb(0.95f, 0.45f, 0.70f),
        ["purple"] = Rgb(0.55f, 0.20f, 0.75f), ["red"] = Rgb(0.85f, 0.15f, 0.15f), ["teal"] = Rgb(0.10f, 0.65f, 0.65f),
        ["white"] = Rgb(0.95f, 0.95f, 0.95f), ["yellow"] = Rgb(0.95f, 0.85f, 0.15f),

        ["amber"] = Rgb(1.00f, 0.75f, 0.00f), ["azure"] = Rgb(0.15f, 0.55f, 0.95f), ["beige"] = Rgb(0.90f, 0.85f, 0.70f),
        ["bronze"] = Rgb(0.70f, 0.48f, 0.20f), ["brown"] = Rgb(0.55f, 0.33f, 0.15f), ["burgundy"] = Rgb(0.50f, 0.00f, 0.13f),
        ["charcoal"] = Rgb(0.20f, 0.20f, 0.22f), ["copper"] = Rgb(0.75f, 0.45f, 0.25f), ["coral"] = Rgb(0.97f, 0.45f, 0.38f),
        ["cream"] = Rgb(1.00f, 0.97f, 0.82f), ["crimson"] = Rgb(0.78f, 0.08f, 0.20f), ["emerald"] = Rgb(0.10f, 0.70f, 0.40f),
        ["fuchsia"] = Rgb(0.95f, 0.20f, 0.65f), ["gold"] = Rgb(0.95f, 0.75f, 0.15f), ["gray"] = Rgb(0.55f, 0.55f, 0.55f),
        ["grey"] = Rgb(0.55f, 0.55f, 0.55f), ["indigo"] = Rgb(0.30f, 0.10f, 0.55f), ["ivory"] = Rgb(0.97f, 0.95f, 0.85f),
        ["jade"] = Rgb(0.00f, 0.66f, 0.42f), ["lavender"] = Rgb(0.72f, 0.65f, 0.95f), ["lilac"] = Rgb(0.78f, 0.62f, 0.85f),
        ["lime"] = Rgb(0.60f, 0.95f, 0.15f), ["maroon"] = Rgb(0.50f, 0.05f, 0.10f), ["mint"] = Rgb(0.60f, 0.97f, 0.75f),
        ["olive"] = Rgb(0.50f, 0.50f, 0.12f), ["peach"] = Rgb(1.00f, 0.75f, 0.60f), ["platinum"] = Rgb(0.90f, 0.89f, 0.88f),
        ["rose"] = Rgb(0.95f, 0.30f, 0.45f), ["ruby"] = Rgb(0.88f, 0.07f, 0.30f), ["salmon"] = Rgb(0.97f, 0.55f, 0.48f),
        ["sapphire"] = Rgb(0.06f, 0.32f, 0.73f), ["scarlet"] = Rgb(0.95f, 0.15f, 0.10f), ["silver"] = Rgb(0.78f, 0.80f, 0.85f),
        ["tan"] = Rgb(0.82f, 0.70f, 0.55f), ["turquoise"] = Rgb(0.20f, 0.85f, 0.78f), ["violet"] = Rgb(0.60f, 0.30f, 0.90f),
    };

    private static readonly HashSet<string> RainbowWords = new(StringComparer.OrdinalIgnoreCase) { "rgb", "rainbow" };

    private static readonly Dictionary<string, string> Alias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["golden"] = "gold", ["silvery"] = "silver", ["rosy"] = "rose", ["greyish"] = "grey", ["grayish"] = "gray",
    };

    /// <summary>Shade words in front of a colour: "dark red", "pale blue".</summary>
    private static readonly Dictionary<string, Func<Vector4, Vector4>> Shades = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dark"] = c => Scale(c, 0.55f), ["deep"] = c => Scale(c, 0.65f), ["dim"] = c => Scale(c, 0.70f),
        ["light"] = c => ToWhite(c, 0.45f), ["pale"] = c => ToWhite(c, 0.55f), ["pastel"] = c => ToWhite(c, 0.60f),
        ["bright"] = c => Boost(c, 0.6f), ["vivid"] = c => Boost(c, 0.8f), ["neon"] = c => Boost(c, 1f),
    };

    /// <summary>"blood red", "ice blue": a noun in front of a colour that only names its shade.</summary>
    private static readonly HashSet<string> Prefixes = new(StringComparer.OrdinalIgnoreCase)
        { "blood", "fire", "ice", "snow", "sky", "sea", "midnight", "steel", "jet", "pitch" };

    private static Vector4 Rgb(float r, float g, float b) => new(r, g, b, 1f);
    private static Vector4 Scale(Vector4 c, float k) => new(c.X * k, c.Y * k, c.Z * k, 1f);
    private static Vector4 ToWhite(Vector4 c, float t) => new(c.X + (1f - c.X) * t, c.Y + (1f - c.Y) * t, c.Z + (1f - c.Z) * t, 1f);
    private static Vector4 Boost(Vector4 c, float t)
    {
        float m = MathF.Max(c.X, MathF.Max(c.Y, c.Z));
        if (m < 0.01f) return c;
        return new Vector4(c.X + (c.X / m - c.X) * t, c.Y + (c.Y / m - c.Y) * t, c.Z + (c.Z / m - c.Z) * t, 1f);
    }

    public static bool IsShade(string w) => Shades.ContainsKey(w);
    public static bool IsPrefix(string w) => Prefixes.Contains(w);

    /// <summary>A single colour word: named, an alias or "-ish" form of one, or rainbow.</summary>
    public static bool TryWord(string word, out LookColor color)
    {
        color = default;
        if (string.IsNullOrEmpty(word)) return false;
        if (RainbowWords.Contains(word)) { color = new LookColor(RainbowAt(0), true); return true; }
        if (Named.TryGetValue(word, out var rgb)) { color = new LookColor(rgb); return true; }
        if (Alias.TryGetValue(word, out var target) && Named.TryGetValue(target, out rgb)) { color = new LookColor(rgb); return true; }
        if (word.Length > 5 && word.EndsWith("ish", StringComparison.OrdinalIgnoreCase) && Named.TryGetValue(word[..^3], out rgb)) { color = new LookColor(rgb); return true; }
        return false;
    }

    public static LookColor ApplyShade(string shade, LookColor c) =>
        c.Rainbow || !Shades.TryGetValue(shade, out var f) ? c : new LookColor(f(c.Rgb));

    public static bool IsColorWord(string? token) =>
        !string.IsNullOrWhiteSpace(token) && TryWord(token.Trim(), out _);

    /// <summary>Hex (# / 0x / bare 3-8 digits), a colour word, or rainbow: what [color=] tags and the settings store.</summary>
    public static bool TryToken(string? token, out Vector4 rgb)
    {
        rgb = default;
        if (string.IsNullOrWhiteSpace(token)) return false;
        var t = token.Trim();
        if (t.StartsWith('#')) t = t[1..];
        else if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];

        if (t.Length is (3 or 4 or 6 or 8) && IsAllHex(t))
        {
            if (t.Length is 3 or 4)
            {
                var e = new char[t.Length * 2];
                for (int i = 0; i < t.Length; i++) { e[i * 2] = t[i]; e[i * 2 + 1] = t[i]; }
                t = new string(e);
            }
            rgb = new Vector4(Convert.ToInt32(t[..2], 16) / 255f, Convert.ToInt32(t[2..4], 16) / 255f, Convert.ToInt32(t[4..6], 16) / 255f, 1f);
            return true;
        }
        if (TryWord(t, out var c)) { rgb = c.Rainbow ? RainbowAt(Environment.TickCount64) : c.Rgb; return true; }
        return false;
    }

    public static bool TryLookColor(string? token, out LookColor color)
    {
        color = default;
        if (token is not null && TryWord(token.Trim(), out color)) return true;
        if (!TryToken(token, out Vector4 rgb)) return false;
        color = new LookColor(rgb);
        return true;
    }

    /// <summary>Hex digits only, as written in a plain description: #rgb or #rrggbb.</summary>
    public static bool TryHex(string token, out LookColor color)
    {
        color = default;
        if (token.Length is not (4 or 7) || token[0] != '#' || !IsAllHex(token[1..])) return false;
        if (!TryToken(token, out Vector4 rgb)) return false;
        color = new LookColor(rgb);
        return true;
    }

    /// <summary>The name for an exact named colour, else #RRGGBB.</summary>
    public static string NameOf(LookColor c)
    {
        if (c.Rainbow) return "rainbow";
        foreach (var kv in Named)
            if (kv.Value == c.Rgb && kv.Key is not ("grey" or "gray" or "grayscale")) return kv.Key;
        return "#" + HexOf(c.Rgb);
    }

    public static string HexOf(Vector4 c) =>
        $"{(int)MathF.Round(c.X * 255f):X2}{(int)MathF.Round(c.Y * 255f):X2}{(int)MathF.Round(c.Z * 255f):X2}";

    /// <summary>Hue advances one step per second across a 256-step wheel.</summary>
    public static Vector4 RainbowAt(long nowMs)
    {
        int hue = (int)((nowMs / 1000) % 256);
        if (hue < 0) hue += 256;
        float sector = hue / 256f * 6f;
        int i = (int)sector % 6;
        float f = sector - (int)sector, q = 1f - f;
        return i switch
        {
            0 => new Vector4(1f, f, 0f, 1f), 1 => new Vector4(q, 1f, 0f, 1f), 2 => new Vector4(0f, 1f, f, 1f),
            3 => new Vector4(0f, q, 1f, 1f), 4 => new Vector4(f, 0f, 1f, 1f), _ => new Vector4(1f, 0f, q, 1f),
        };
    }

    private static bool IsAllHex(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
