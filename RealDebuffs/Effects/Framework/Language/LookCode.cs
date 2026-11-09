using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace RealDebuffs.Effects.Framework;

/// <summary>The exact, copyable form of a Look: "RD" plus a few base62 characters (case-sensitive, checksummed); only ever pasted, never typed.</summary>
public static class LookCode
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private const int FormatVersion = 0;
    private const int CheckBits = 6;

    private enum Family { Stroke, Particle, Region }

    private static readonly Regex Rx = new(@"(?<![A-Za-z0-9])RD[A-Za-z0-9]{5,}(?![A-Za-z0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Test hook: write the wide (8-bit) fields as if a table had outgrown the narrow ones.</summary>
    internal static bool ForceWide;

    public static IEnumerable<Match> Find(string text) => Rx.Matches(text);

    /// <summary>A material name without its family prefix or hyphens, lowercased: how test descriptions spell materials.</summary>
    public static string Compact(string materialName)
    {
        int dot = materialName.IndexOf('.');
        return (dot >= 0 ? materialName[(dot + 1)..] : materialName).Replace("-", "").ToLowerInvariant();
    }

    // Layout, most significant bit first (a leading 1 marks where the number starts, so its length is exact):
    //   kind 5 (31 = six more bits) | wide 1 | flags 5: body, modifier, body colour, modifier colour, slots
    //   body (material) | modifier (material, all-ones = sheds nothing) | colours (id; all-ones = rainbow, one less = custom RGB follows)
    //   slots: mode 1; list: count 5, then every address, then every material; bitmap: top address, a bit per address below it, then a material per set bit
    //   check 6. The format version seeds the check, so a future layout is told apart without spending a bit.
    // Ids are positions in CodeIds. Narrow widths (address 6, stroke 5, particle 6, region 4, colour 6) become 8 bits when any id outgrows them.

    /// <summary>Null when a material, colour or slot has no number in CodeIds yet (the audit says which).</summary>
    public static string? Encode(Look look)
    {
        var kind = look.Kind;
        if ((int)kind > 63) return null;

        var bodySlot = EffectRegistry.BodyFor(kind);
        int body = -1, mod = -1, bodyColor = 0, modColor = 0;
        if (look.Body is { } b)
        {
            if (bodySlot is not { } bs || (body = IdOf(FamilyOf(bs.Type), b)) < 0) return null;
        }
        if (look.Modifier is { } m && (mod = m == Look.NoShed ? int.MaxValue : IdOf(Family.Particle, m)) < 0) return null;
        if (look.BodyColor is { } bc) bodyColor = ColorId(bc);
        if (look.ModifierColor is { } mc) modColor = ColorId(mc);

        var slots = new SortedDictionary<int, (Family Fam, int Id)>();
        if (look.Details is { Count: > 0 } d)
            foreach (var slot in EffectRegistry.SlotsFor(kind))
            {
                if (d.TryGetValue(MaterialOverrideKey.For(kind, slot), out var v))
                {
                    var addr = slot.Type == SlotType.Region ? new Addr(AddrType.Region, (int)slot.Region)
                        : new Addr(slot.Type == SlotType.Stroke ? AddrType.Stroke : AddrType.Particle, (int)slot.Role);
                    if (!Add(addr, FamilyOf(slot.Type), v)) return null;
                }
                if (slot.Type == SlotType.Stroke && d.TryGetValue(MaterialOverrideKey.ForStrokeEmit(kind, slot.Role), out var e))
                    if (!Add(new Addr(AddrType.Emit, (int)slot.Role), Family.Particle, e)) return null;
            }

        bool Add(Addr addr, Family fam, string material)
        {
            int index = Array.IndexOf(CodeIds.Slot, addr);
            int id = material == Look.NoShed ? int.MaxValue : IdOf(fam, material);
            if (index < 0 || id < 0) return false;
            slots[index] = (fam, id);
            return true;
        }

        // int.MaxValue marks "none" / rainbow / custom, which every width can say.
        static bool Over(int id, int capacity) => id != int.MaxValue && id >= capacity;
        bool wide = ForceWide
            || (body >= 0 && Over(body, Cap(FamilyOf(bodySlot!.Value.Type), false)))
            || (mod >= 0 && Over(mod, Cap(Family.Particle, false)))
            || Over(bodyColor, ColorCap(false)) || Over(modColor, ColorCap(false))
            || slots.Any(s => s.Key >= 1 << AddrBits(false) || Over(s.Value.Id, Cap(s.Value.Fam, false)));

        var w = new BitWriter();
        if ((int)kind < 31) w.Write((int)kind, 5); else { w.Write(31, 5); w.Write((int)kind, 6); }
        w.Write(wide ? 1 : 0, 1);
        w.Write((look.Body is null ? 0 : 16) | (look.Modifier is null ? 0 : 8) | (look.BodyColor is null ? 0 : 4) | (look.ModifierColor is null ? 0 : 2) | (slots.Count > 0 ? 1 : 0), 5);

        if (look.Body is not null) w.Write(body, Bits(FamilyOf(bodySlot!.Value.Type), wide));
        if (look.Modifier is not null) w.Write(mod == int.MaxValue ? Cap(Family.Particle, wide) : mod, Bits(Family.Particle, wide));
        if (look.BodyColor is { } bcol) WriteColor(w, bcol, bodyColor, wide);
        if (look.ModifierColor is { } mcol) WriteColor(w, mcol, modColor, wide);

        if (slots.Count > 0)
        {
            int ab = AddrBits(wide);
            int matBits = slots.Sum(s => Bits(s.Value.Fam, wide));
            int top = slots.Keys.Max();
            int listCost = 5 + slots.Count * ab + matBits, bitmapCost = ab + top + matBits;
            bool bitmap = slots.Count > 31 || bitmapCost < listCost;

            w.Write(bitmap ? 1 : 0, 1);
            if (bitmap)
            {
                w.Write(top, ab);
                for (int i = 0; i < top; i++) w.Write(slots.ContainsKey(i) ? 1 : 0, 1);
            }
            else w.Write(slots.Count, 5);

            if (!bitmap)
                foreach (int index in slots.Keys) w.Write(index, ab);
            foreach (var (fam, id) in slots.Values)
                w.Write(id == int.MaxValue ? Cap(fam, wide) : id, Bits(fam, wide));
        }

        int p = w.Count;
        w.Write(Check(w.Bits, p), CheckBits);
        return "RD" + ToBase62(w.Bits);
    }

    /// <summary>Decodes one code. Look is null when the code is cut short, edited, or from a newer plugin; Warnings then explain, or hold what was skipped.</summary>
    public static (Look? Look, List<string> Warnings) Decode(string code)
    {
        var warn = new List<string>();
        (Look?, List<string>) Damaged()
        {
            if (code.Length >= 10) warn.Add($"\"{code}\" looks like a Real Debuffs code but isn't valid (cut short, edited, or made by a newer version)");
            return (null, warn);
        }

        if (code.Length < 3 || !code.StartsWith("RD", StringComparison.Ordinal)) { warn.Add($"\"{code}\" isn't a Real Debuffs code"); return (null, warn); }
        var bits = FromBase62(code[2..]);
        if (bits is null) return Damaged();
        var r = new BitReader(bits);

        try
        {
            int kindId = r.Read(5);
            if (kindId == 31) kindId = r.Read(6);
            if (!Enum.IsDefined(typeof(DebuffKind), kindId)) return Damaged();
            var kind = (DebuffKind)kindId;
            bool wide = r.Read(1) == 1;
            int flags = r.Read(5);

            int body = -1, mod = -1, bodyColor = 0, modColor = 0;
            long bodyRgb = -1, modRgb = -1;
            var bodySlot = EffectRegistry.BodyFor(kind);

            if ((flags & 16) != 0)
            {
                if (bodySlot is not { } bs) return Damaged();
                body = r.Read(Bits(FamilyOf(bs.Type), wide));
            }
            if ((flags & 8) != 0) mod = r.Read(Bits(Family.Particle, wide));
            if ((flags & 4) != 0) bodyColor = ReadColor(r, wide, out bodyRgb);
            if ((flags & 2) != 0) modColor = ReadColor(r, wide, out modRgb);

            var raw = new List<(int Index, int Id)>();
            if ((flags & 1) != 0)
            {
                int ab = AddrBits(wide);
                var indices = new List<int>();
                if (r.Read(1) == 1)
                {
                    int top = r.Read(ab);
                    for (int i = 0; i < top; i++) if (r.Read(1) == 1) indices.Add(i);
                    indices.Add(top);
                }
                else
                {
                    int count = r.Read(5);
                    for (int i = 0; i < count; i++) indices.Add(r.Read(ab));
                }
                foreach (int index in indices)
                {
                    var fam = index < CodeIds.Slot.Length ? FamilyOf(CodeIds.Slot[index].Type) : Family.Particle;
                    raw.Add((index, r.Read(Bits(fam, wide))));
                }
            }

            int p = r.Position;
            if (bits.Count - p != CheckBits || r.Read(CheckBits) != Check(bits, p)) return Damaged();

            // The check held, so anything that doesn't resolve now was made against a different list of materials.
            string? bodyName = null, modName = null;
            LookColor? bodyCol = null, modCol = null;
            Dictionary<string, string>? details = null;

            if (body >= 0 && (bodyName = NameOf(FamilyOf(bodySlot!.Value.Type), body, wide)) is null) warn.Add("a material in this code isn't known to this version");
            if (mod >= 0)
            {
                modName = mod == Cap(Family.Particle, wide) ? Look.NoShed : NameOf(Family.Particle, mod, wide);
                if (modName is null) warn.Add("a modifier in this code isn't known to this version");
            }
            if ((flags & 4) != 0 && (bodyCol = ColorOf(bodyColor, bodyRgb, wide)) is null) warn.Add("a colour in this code isn't known to this version");
            if ((flags & 2) != 0 && (modCol = ColorOf(modColor, modRgb, wide)) is null) warn.Add("a colour in this code isn't known to this version");

            foreach (var (index, id) in raw)
            {
                string? key = null, mat = null;
                if (index < CodeIds.Slot.Length)
                {
                    var addr = CodeIds.Slot[index];
                    var fam = FamilyOf(addr.Type);
                    key = SlotKey(kind, addr);
                    mat = addr.Type == AddrType.Emit && id == Cap(Family.Particle, wide) ? Look.NoShed : NameOf(fam, id, wide);
                }
                if (key is null || mat is null) warn.Add($"a slot in this code isn't available for {kind}");
                else (details ??= new Dictionary<string, string>(StringComparer.Ordinal))[key] = mat;
            }

            return (new Look(kind, bodyName, modName, bodyCol, modCol, details), warn);
        }
        catch (InvalidOperationException) { return Damaged(); }
    }

    /// <summary>Problems that would make codes unstable: a material, colour or slot without a number, a repeated entry, a number too big to pack.</summary>
    public static IEnumerable<string> VocabularyProblems()
    {
        foreach (var (label, table, names) in new[] { ("Stroke", CodeIds.Stroke, MaterialRegistry.StrokeNames), ("Particle", CodeIds.Particle, MaterialRegistry.ParticleNames), ("Region", CodeIds.Region, MaterialRegistry.RegionNames) })
        {
            foreach (var n in names.Where(n => Array.FindIndex(table, t => string.Equals(t, n, StringComparison.OrdinalIgnoreCase)) < 0))
                yield return $"{n} has no short-code number: append \"{n}\" to CodeIds.{label}";
            foreach (var g in table.GroupBy(t => t, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                yield return $"CodeIds.{label} lists {g.Key} twice";
            if (table.Length > 254) yield return $"CodeIds.{label} is full (254 entries)";
        }

        foreach (var c in ColorLexicon.Named.Keys.Where(c => Array.IndexOf(CodeIds.Color, c) < 0))
            yield return $"colour {c} has no short-code number: append \"{c}\" to CodeIds.Color";
        if (CodeIds.Color.Length > 253) yield return "CodeIds.Color is full (253 entries)";

        foreach (var kind in EffectRegistry.KindsWithSlots)
            foreach (var slot in EffectRegistry.SlotsFor(kind))
            {
                var addr = slot.Type == SlotType.Region ? new Addr(AddrType.Region, (int)slot.Region) : new Addr(slot.Type == SlotType.Stroke ? AddrType.Stroke : AddrType.Particle, (int)slot.Role);
                if (Array.IndexOf(CodeIds.Slot, addr) < 0) yield return $"{kind}: a slot has no short-code number: append {AddrText(addr)} to CodeIds.Slot";
                var emit = new Addr(AddrType.Emit, (int)slot.Role);
                if (slot.Type == SlotType.Stroke && Array.IndexOf(CodeIds.Slot, emit) < 0)
                    yield return $"{kind}: a slot has no short-code number: append {AddrText(emit)} to CodeIds.Slot";
            }
        if (CodeIds.Slot.Distinct().Count() != CodeIds.Slot.Length) yield return "CodeIds.Slot lists an address twice";
        if (CodeIds.Slot.Length > 255) yield return "CodeIds.Slot is full (255 entries)";

        if (Enum.GetValues<PrimitiveRole>().Any(r => (int)r > 63)) yield return "a PrimitiveRole is above 63, which short codes can't hold";
        if (Enum.GetValues<DebuffKind>().Any(k => (int)k > 63)) yield return "a DebuffKind is above 63, which short codes can't hold";
    }

    private static string AddrText(Addr a) => a.Type switch
    {
        AddrType.Stroke => $"S(PrimitiveRole.{(PrimitiveRole)a.Role})",
        AddrType.Particle => $"P(PrimitiveRole.{(PrimitiveRole)a.Role})",
        AddrType.Emit => $"E(PrimitiveRole.{(PrimitiveRole)a.Role})",
        _ => $"R(RegionKind.{(RegionKind)a.Role})",
    };

    private static Family FamilyOf(SlotType t) => t switch { SlotType.Stroke => Family.Stroke, SlotType.Particle => Family.Particle, _ => Family.Region };

    private static Family FamilyOf(AddrType t) => t switch { AddrType.Stroke => Family.Stroke, AddrType.Region => Family.Region, _ => Family.Particle };

    private static string[] Table(Family f) => f switch { Family.Stroke => CodeIds.Stroke, Family.Particle => CodeIds.Particle, _ => CodeIds.Region };

    private static int IdOf(Family f, string name) => Array.FindIndex(Table(f), n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    private static string? NameOf(Family f, int id, bool wide) => id >= 0 && id < Table(f).Length && id != Cap(f, wide) ? Table(f)[id] : null;

    private static int AddrBits(bool wide) => wide ? 8 : 6;

    private static int Bits(Family f, bool wide) => wide ? 8 : f switch { Family.Stroke => 5, Family.Particle => 6, _ => 4 };

    /// <summary>The all-ones value is reserved ("sheds nothing"), so it is also the capacity of a width.</summary>
    private static int Cap(Family f, bool wide) => (1 << Bits(f, wide)) - 1;

    /// <summary>Named colours take 0 to this minus one; the two values above are custom and rainbow.</summary>
    private static int ColorCap(bool wide) => (wide ? 255 : 63) - 1;

    private static int ColorId(LookColor c)
    {
        if (c.Rainbow) return int.MaxValue;
        for (int i = 0; i < CodeIds.Color.Length; i++)
            if (ColorLexicon.Named.TryGetValue(CodeIds.Color[i], out var rgb) && rgb == c.Rgb) return i;
        return -1;
    }

    private static void WriteColor(BitWriter w, LookColor c, int id, bool wide)
    {
        int bits = wide ? 8 : 6, rainbow = (1 << bits) - 1, custom = rainbow - 1;
        if (id == int.MaxValue) { w.Write(rainbow, bits); return; }
        if (id >= 0 && id < custom) { w.Write(id, bits); return; }
        w.Write(custom, bits);
        w.Write((int)MathF.Round(c.Rgb.X * 255f), 8); w.Write((int)MathF.Round(c.Rgb.Y * 255f), 8); w.Write((int)MathF.Round(c.Rgb.Z * 255f), 8);
    }

    private static int ReadColor(BitReader r, bool wide, out long rgb)
    {
        int bits = wide ? 8 : 6, v = r.Read(bits);
        rgb = v == (1 << bits) - 2 ? r.Read(24) : -1;
        return v;
    }

    private static LookColor? ColorOf(int id, long rgb, bool wide)
    {
        int rainbow = (wide ? 255 : 63);
        if (id == rainbow) return new LookColor(ColorLexicon.RainbowAt(0), true);
        if (id == rainbow - 1) return new LookColor(new Vector4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f));
        return id < CodeIds.Color.Length && ColorLexicon.Named.TryGetValue(CodeIds.Color[id], out var named) ? new LookColor(named) : null;
    }

    private static string? SlotKey(DebuffKind kind, Addr addr)
    {
        foreach (var slot in EffectRegistry.SlotsFor(kind))
        {
            switch (addr.Type)
            {
                case AddrType.Stroke when slot.Type == SlotType.Stroke && (int)slot.Role == addr.Role:
                case AddrType.Particle when slot.Type == SlotType.Particle && (int)slot.Role == addr.Role:
                case AddrType.Region when slot.Type == SlotType.Region && (int)slot.Region == addr.Role:
                    return MaterialOverrideKey.For(kind, slot);
                case AddrType.Emit when slot.Type == SlotType.Stroke && (int)slot.Role == addr.Role:
                    return MaterialOverrideKey.ForStrokeEmit(kind, slot.Role);
            }
        }
        return null;
    }

    private static int Check(List<bool> bits, int p)
    {
        uint h = unchecked((2166136261u ^ (uint)FormatVersion) * 16777619u);
        h = unchecked((h ^ (uint)p) * 16777619u);
        for (int i = 0; i < p; i += 8)
        {
            int b = 0;
            for (int k = 0; k < 8; k++) b = (b << 1) | (i + k < p && bits[i + k] ? 1 : 0);
            h = unchecked((h ^ (uint)b) * 16777619u);
        }
        h ^= h >> 15; h = unchecked(h * 0x2c1b3c6du); h ^= h >> 12;
        return (int)(h & ((1u << CheckBits) - 1));
    }

    private static string ToBase62(List<bool> bits)
    {
        var n = BigInteger.One;
        foreach (bool bit in bits) n = (n << 1) | (bit ? BigInteger.One : BigInteger.Zero);

        var sb = new StringBuilder();
        while (n > 0)
        {
            n = BigInteger.DivRem(n, 62, out var digit);
            sb.Insert(0, Alphabet[(int)digit]);
        }
        return sb.ToString();
    }

    private static List<bool>? FromBase62(string s)
    {
        var n = BigInteger.Zero;
        foreach (char c in s)
        {
            int v = Alphabet.IndexOf(c);
            if (v < 0) return null;
            n = n * 62 + v;
        }
        long length = n.GetBitLength() - 1;
        if (length < 14 || length > 1200) return null;

        var bits = new List<bool>((int)length);
        for (long i = length - 1; i >= 0; i--) bits.Add(!(n & (BigInteger.One << (int)i)).IsZero);
        return bits;
    }

    private sealed class BitWriter
    {
        public readonly List<bool> Bits = new();
        public int Count => Bits.Count;
        public void Write(int value, int bits) { for (int k = bits - 1; k >= 0; k--) Bits.Add(((value >> k) & 1) == 1); }
    }

    private sealed class BitReader
    {
        private readonly List<bool> _bits;
        public int Position { get; private set; }
        public BitReader(List<bool> bits) { _bits = bits; }
        public int Read(int n)
        {
            if (Position + n > _bits.Count) throw new InvalidOperationException("out of bits");
            int v = 0;
            for (int k = 0; k < n; k++) v = (v << 1) | (_bits[Position++] ? 1 : 0);
            return v;
        }
    }
}
