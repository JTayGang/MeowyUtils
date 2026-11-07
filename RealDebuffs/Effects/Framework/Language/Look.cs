namespace RealDebuffs.Effects.Framework;

/// <summary>What a status asks of one effect: a body swap, one modifier, two colours, and raw slot overrides (codes only).</summary>
public sealed record Look(
    DebuffKind Kind,
    string? Body = null,
    string? Modifier = null,
    LookColor? BodyColor = null,
    LookColor? ModifierColor = null,
    IReadOnlyDictionary<string, string>? Details = null)
{
    /// <summary>Modifier value meaning "a stroke body sheds nothing".</summary>
    public const string NoShed = "__none__";

    /// <summary>How much the status specified; the richer Look wins when statuses collide.</summary>
    public int Score =>
        (Body is null ? 0 : 1) + (Modifier is null ? 0 : 1) + (BodyColor is null ? 0 : 1) + (ModifierColor is null ? 0 : 1) + (Details?.Count ?? 0);

    /// <summary>This Look with gaps filled from other (first non-null wins per field).</summary>
    public Look Fill(Look other)
    {
        IReadOnlyDictionary<string, string>? details = Details;
        if (other.Details is { Count: > 0 } od)
        {
            var merged = new Dictionary<string, string>(od, StringComparer.Ordinal);
            if (Details is not null) foreach (var kv in Details) merged[kv.Key] = kv.Value;
            details = merged;
        }
        return this with
        {
            Body = Body ?? other.Body,
            Modifier = Modifier ?? other.Modifier,
            BodyColor = BodyColor ?? other.BodyColor,
            ModifierColor = ModifierColor ?? other.ModifierColor,
            Details = details,
        };
    }
}

/// <summary>Looks to the existing override keys, and back; everything below this layer is unchanged.</summary>
public static class LookApply
{
    /// <summary>Writes the override keys a Look implies: details, then body, then modifier.</summary>
    public static void Overrides(Look look, IDictionary<string, string> into)
    {
        var kind = look.Kind;
        if (look.Details is { } d)
            foreach (var kv in d) into[kv.Key] = kv.Value;

        if (look.Body is { } body && EffectRegistry.BodyFor(kind) is { } bodySlot)
            into[MaterialOverrideKey.For(kind, bodySlot)] = body;

        if (look.Modifier is { } mod)
        {
            if (EffectRegistry.ModifierFor(kind) is { } modSlot)
            {
                if (mod != Look.NoShed) into[MaterialOverrideKey.For(kind, modSlot)] = mod;
            }
            else if (EffectRegistry.BodyFor(kind) is { Type: SlotType.Stroke } strokeBody)
                into[MaterialOverrideKey.ForStrokeEmit(kind, strokeBody.Role)] = mod;
        }
    }

    /// <summary>The override key the modifier of this kind lands on, or null if the effect has nowhere to put one.</summary>
    public static string? ModifierKey(DebuffKind kind)
    {
        if (EffectRegistry.ModifierFor(kind) is { } modSlot) return MaterialOverrideKey.For(kind, modSlot);
        if (EffectRegistry.BodyFor(kind) is { Type: SlotType.Stroke } strokeBody) return MaterialOverrideKey.ForStrokeEmit(kind, strokeBody.Role);
        return null;
    }

    /// <summary>Reads a Look back out of live overrides (the generator edits overrides; the phrase and code derive from them).</summary>
    public static Look FromOverrides(DebuffKind kind, IReadOnlyDictionary<string, string> overrides, LookColor? bodyColor, LookColor? modifierColor)
    {
        string? body = null, modifier = null;
        var consumed = new HashSet<string>(StringComparer.Ordinal);

        if (EffectRegistry.BodyFor(kind) is { } bodySlot)
        {
            var key = MaterialOverrideKey.For(kind, bodySlot);
            if (overrides.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) body = v;
            consumed.Add(key);
        }
        if (ModifierKey(kind) is { } modKey)
        {
            if (overrides.TryGetValue(modKey, out var v) && !string.IsNullOrEmpty(v)) modifier = v;
            consumed.Add(modKey);
        }

        Dictionary<string, string>? details = null;
        foreach (var slot in EffectRegistry.SlotsFor(kind))
        {
            Collect(MaterialOverrideKey.For(kind, slot));
            if (slot.Type == SlotType.Stroke) Collect(MaterialOverrideKey.ForStrokeEmit(kind, slot.Role));
        }

        return new Look(kind, body, modifier, bodyColor, modifierColor, details);

        void Collect(string key)
        {
            if (consumed.Contains(key) || !overrides.TryGetValue(key, out var v) || string.IsNullOrEmpty(v)) return;
            (details ??= new Dictionary<string, string>(StringComparer.Ordinal))[key] = v;
        }
    }
}
