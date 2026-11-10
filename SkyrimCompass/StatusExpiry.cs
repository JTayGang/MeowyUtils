using System;
using System.Collections.Generic;
using MemoryPack;

namespace SkyrimCompass;

    // Timers come from the MemoryPack manager payload; the mirror classes must match Moodles 1.1.3.5 / Loci 0.1.3.12 exactly.

/// <summary>Mirror of Moodles.Data.MyStatus (serialized members only, same order).</summary>
[MemoryPackable]
internal sealed partial class MoodlesWireStatus
{
    public Guid GUID;
    public int IconID;
    public string Title = "";
    public string Description = "";
    public string CustomFXPath = "";
    public long ExpiresAt;          // unix ms; long.MaxValue == no expiry
    public int Type;                // Moodles.Data.StatusType (int-backed enum)
    public uint Modifiers;          // Moodles.Data.Modifiers : uint
    public int Stacks;
    public int StackSteps;
    public Guid ChainedStatus;
    public int ChainTrigger;        // Moodles.Data.ChainTrigger (int-backed enum)
    public string Applier = "";
    public string Dispeller = "";
}

/// <summary>Mirror of Loci.Data.LociStatus (serialized members only, same order).</summary>
[MemoryPackable]
internal sealed partial class LociWireStatus
{
    public Guid GUID;
    public uint IconID;
    public string Title = "";
    public string Description = "";
    public string CustomFXPath = "";
    public long ExpiresAt;          // unix ms; long.MaxValue == no expiry
    public byte Type;               // LociApi.Enums.StatusType : byte
    public uint Modifiers;          // LociApi.Enums.Modifiers : uint
    public int Stacks;
    public int StackSteps;
    public int StackToChain;
    public Guid ChainedGUID;
    public byte ChainedType;        // LociApi.Enums.ChainType : byte
    public int ChainTrigger;        // LociApi.Enums.ChainTrigger : int
    public string Applier = "";
    public string Dispeller = "";
}

internal enum ExpiryState
{
    /// <summary>No usable ExpiresAt for this status (payload unavailable, undecodable, or GUID missing).</summary>
    Unknown,
    NoExpiry,
    /// <summary>ExpiresAt is in the past. Moodles/Loci hide these on their own bars.</summary>
    Expired,
    Active,
}

internal static class StatusExpiryCodec
{
    // Both plugins serialize with StringEncoding.Utf16.
    private static readonly MemoryPackSerializerOptions Opts = new() { StringEncoding = StringEncoding.Utf16 };

    public const long NoExpiry = long.MaxValue;

    /// <summary>Decodes a Moodles manager payload to GUID -> ExpiresAt; "" is valid (no statuses), false if undecodable.</summary>
    public static bool TryDecodeMoodles(string base64, Dictionary<Guid, long> into)
    {
        into.Clear();
        if (base64.Length == 0) return true;
        try
        {
            var list = MemoryPackSerializer.Deserialize<List<MoodlesWireStatus>>(Convert.FromBase64String(base64), Opts);
            if (list == null) return false;
            foreach (var s in list) into[s.GUID] = s.ExpiresAt;
            return true;
        }
        catch { into.Clear(); return false; }
    }

    /// <summary>Same as <see cref="TryDecodeMoodles"/> for Loci's manager payload.</summary>
    public static bool TryDecodeLoci(string base64, Dictionary<Guid, long> into)
    {
        into.Clear();
        if (base64.Length == 0) return true;
        try
        {
            var list = MemoryPackSerializer.Deserialize<List<LociWireStatus>>(Convert.FromBase64String(base64), Opts);
            if (list == null) return false;
            foreach (var s in list) into[s.GUID] = s.ExpiresAt;
            return true;
        }
        catch { into.Clear(); return false; }
    }
}

/// <summary>ExpiresAt per status on the current target: Refresh on the fetch cadence, Get every frame.</summary>
internal sealed class ExpiryFeed
{
    private readonly string _name;
    private readonly Func<nint, string?> _fetch;
    private readonly Func<string, Dictionary<Guid, long>, bool> _decode;
    private readonly Action<string> _warn;
    private readonly Func<long> _nowMs;
    private readonly Dictionary<Guid, long> _expiresAt = new();
    private bool _warned;

    /// <summary>True when the last <see cref="Refresh"/> produced a decoded payload for the current target.</summary>
    public bool Ok { get; private set; }

    public ExpiryFeed(string name, Func<nint, string?> fetch, Func<string, Dictionary<Guid, long>, bool> decode,
        Action<string> warn, Func<long>? nowMs = null)
    {
        _name = name; _fetch = fetch; _decode = decode; _warn = warn;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public void Clear() { _expiresAt.Clear(); Ok = false; }

    public void Refresh(nint addr)
    {
        _expiresAt.Clear();
        Ok = false;
        string? b64;
        // IPC not ready / plugin unloading: stay quiet, the HUD already guards on plugin presence.
        try { b64 = _fetch(addr); } catch { return; }
        // null = the plugin doesn't track this actor (not rendered / not registered). Nothing to read.
        if (b64 == null) return;
        Ok = _decode(b64, _expiresAt);
        if (!Ok && !_warned)
        {
            _warned = true;
            _warn($"Couldn't decode {_name}'s status data - its layout probably changed. " +
                  "Status icons still work, but timers are hidden until SkyrimCompass is updated.");
        }
    }

    public ExpiryState Get(Guid guid, out float remainingSeconds)
    {
        remainingSeconds = 0f;
        if (!Ok || !_expiresAt.TryGetValue(guid, out var exp)) return ExpiryState.Unknown;
        if (exp == StatusExpiryCodec.NoExpiry) return ExpiryState.NoExpiry;
        var remMs = exp - _nowMs();
        if (remMs <= 0) return ExpiryState.Expired;
        remainingSeconds = remMs / 1000f;
        return ExpiryState.Active;
    }
}
