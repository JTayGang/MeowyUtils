using System;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FfxGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using FfxGameObjectManager = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectManager;

namespace FPSBooster;

/// <summary>Read-only snapshot of the current render state for one object category.</summary>
public struct ActorKindStats
{
    public int Objects;
    public int DrawObjects;
    public int DrawFlagsNeitherBitSet;
    public int DrawFlagBit0Only;
    public int DrawFlagBit3Only;
    public int DrawFlagBits0And3Set;
    public int GameVisibilityHidden;
    public int GameVisibilityOther;
    public int CharacterBases;
    public int Skeletons;
    public int ModelSlots;
    public int PopulatedModelSlots;

    public readonly int WithoutDrawObject => Math.Max(0, Objects - DrawObjects);
}

/// <summary>One range in the distance breakdown.</summary>
public struct DistanceBandStats
{
    public int Objects;
    public int WithDrawObject;

    public readonly int WithoutDrawObject => Math.Max(0, Objects - WithDrawObject);
}

/// <summary>Summary of the game's own byte-sized CurrentDistance field.</summary>
public struct ClientDistanceValueStats
{
    public int Count;
    public int Total;
    public byte Minimum;
    public byte Maximum;

    public readonly float Average => Count > 0 ? (float)Total / Count : 0f;
}

/// <summary>Objects grouped by 3D distance from the local player.</summary>
public struct DistanceDistribution
{
    public DistanceBandStats Under10;
    public DistanceBandStats From10To20;
    public DistanceBandStats From20To30;
    public DistanceBandStats From30To50;
    public DistanceBandStats Over50;
    public int DistanceUnavailable;
    public ClientDistanceValueStats CurrentDistanceWithDrawObject;
    public ClientDistanceValueStats CurrentDistanceWithoutDrawObject;
}

/// <summary>Read-only snapshot of the game's current object and character rendering state.</summary>
public struct CharacterDiagnosticsSnapshot
{
    public ActorKindStats Players;
    public ActorKindStats BattleNpcs;
    public ActorKindStats EventNpcs;
    public ActorKindStats Retainers;
    public ActorKindStats Mounts;
    public ActorKindStats Companions;
    public ActorKindStats Ornaments;

    // The local player is excluded from PlayerDistances; NPC distances include all NPC categories.
    public DistanceDistribution PlayerDistances;
    public DistanceDistribution NpcDistances;

    public uint DrawLimit;
    public bool HasObjectManager;
    public bool HasDrawLimit;
    public long UpdatedAt;
}

/// <summary>
/// Collects lightweight, read-only character diagnostics. This service never changes object
/// visibility or draw state. The snapshot is refreshed only while a diagnostics tab is viewed.
/// </summary>
public sealed unsafe class CharacterDiagnostics
{
    private const long RefreshIntervalMs = 1000;
    private const int MaxModelSlotsToInspect = 64;

    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;
    private long _nextRefreshAt;
    private long _nextErrorLogAt;

    public CharacterDiagnosticsSnapshot Snapshot { get; private set; }
    public bool HasSnapshot { get; private set; }

    public CharacterDiagnostics(IObjectTable objectTable, IPluginLog log)
    {
        _objectTable = objectTable;
        _log = log;
    }

    public void RefreshIfNeeded(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && HasSnapshot && now < _nextRefreshAt)
            return;

        try
        {
            var snapshot = default(CharacterDiagnosticsSnapshot);

            // Object-manager draw budget is context only. It is not a count of objects hidden.
            var manager = FfxGameObjectManager.Instance();
            if (manager != null)
            {
                snapshot.HasObjectManager = true;
                try
                {
                    snapshot.DrawLimit = manager->GetDrawLimit();
                    snapshot.HasDrawLimit = true;
                }
                catch
                {
                    snapshot.HasDrawLimit = false;
                }
            }

            // Use the local player's native position so every category is measured in the same
            // 3D world-space coordinate system. The local player itself is omitted from the
            // player distance table, but remains included in the Players object count.
            var localPlayerWrapper = _objectTable.LocalPlayer;
            nint localPlayerAddress = localPlayerWrapper != null ? localPlayerWrapper.Address : nint.Zero;
            FfxGameObject* localPlayer = (FfxGameObject*)localPlayerAddress;
            bool hasLocalPlayer = localPlayer != null;
            float localX = hasLocalPlayer ? localPlayer->Position.X : 0f;
            float localY = hasLocalPlayer ? localPlayer->Position.Y : 0f;
            float localZ = hasLocalPlayer ? localPlayer->Position.Z : 0f;

            // Dalamud's object table is a sparse list of currently spawned game objects.
            // Indexing avoids allocating an enumerator and keeps this scan bounded by its length.
            for (int i = 0; i < _objectTable.Length; i++)
            {
                var wrapper = _objectTable[i];
                if (wrapper == null)
                    continue;

                nint address = wrapper.Address;
                if (address == 0)
                    continue;

                var go = (FfxGameObject*)address;
                bool hasDrawObject = go->DrawObject != null;
                float distanceSquared = 0f;
                bool hasDistance = hasLocalPlayer && TryGetDistanceSquared(go, localX, localY, localZ, out distanceSquared);

                switch (wrapper.ObjectKind)
                {
                    case DalamudObjectKind.Pc:
                        Add(ref snapshot.Players, go);
                        if (address != localPlayerAddress)
                            AddDistance(ref snapshot.PlayerDistances, hasDistance, distanceSquared, hasDrawObject, go->CurrentDistance);
                        break;
                    case DalamudObjectKind.BattleNpc:
                        Add(ref snapshot.BattleNpcs, go);
                        AddDistance(ref snapshot.NpcDistances, hasDistance, distanceSquared, hasDrawObject, go->CurrentDistance);
                        break;
                    case DalamudObjectKind.EventNpc:
                        Add(ref snapshot.EventNpcs, go);
                        AddDistance(ref snapshot.NpcDistances, hasDistance, distanceSquared, hasDrawObject, go->CurrentDistance);
                        break;
                    case DalamudObjectKind.Retainer:
                        Add(ref snapshot.Retainers, go);
                        AddDistance(ref snapshot.NpcDistances, hasDistance, distanceSquared, hasDrawObject, go->CurrentDistance);
                        break;
                    case DalamudObjectKind.Mount:
                    case DalamudObjectKind.FollowMount:
                        Add(ref snapshot.Mounts, go);
                        break;
                    case DalamudObjectKind.Companion:
                        Add(ref snapshot.Companions, go);
                        break;
                    case DalamudObjectKind.Ornament:
                        Add(ref snapshot.Ornaments, go);
                        break;
                }
            }

            snapshot.UpdatedAt = now;
            Snapshot = snapshot;
            HasSnapshot = true;
            _nextRefreshAt = now + RefreshIntervalMs;
        }
        catch (Exception ex)
        {
            if (now >= _nextErrorLogAt)
            {
                _nextErrorLogAt = now + 10000;
                _log.Warning(ex, "FPSBooster: character diagnostics refresh failed; the previous snapshot was kept.");
            }

            _nextRefreshAt = now + RefreshIntervalMs;
        }
    }

    private static bool Add(ref ActorKindStats stats, FfxGameObject* go)
    {
        stats.Objects++;

        // FFXIVClientStructs documents 0 as shown and 1 as hidden. Preserve any other raw values
        // separately instead of silently treating every non-zero value as the same state.
        if (go->Visibility == 1)
            stats.GameVisibilityHidden++;
        else if (go->Visibility != 0)
            stats.GameVisibilityOther++;

        DrawObject* draw = go->DrawObject;
        if (draw == null)
            return false;

        stats.DrawObjects++;

        // DrawObject.IsVisible currently checks both bits (0x01 and 0x08), but the struct source
        // explicitly says their meaning is uncertain. Report the raw bits rather than calling
        // either state visible/hidden or attributing it to native distance culling.
        byte flags = draw->Flags;
        bool bit0 = (flags & 0x01) != 0;
        bool bit3 = (flags & 0x08) != 0;
        if (bit0 && bit3)
            stats.DrawFlagBits0And3Set++;
        else if (bit0)
            stats.DrawFlagBit0Only++;
        else if (bit3)
            stats.DrawFlagBit3Only++;
        else
            stats.DrawFlagsNeitherBitSet++;

        // CharacterBase is the render-side character model container. A skeleton and non-null
        // model slots tell us more than simply having a GameObject.DrawObject pointer.
        CharacterBase* characterBase = go->GetCharacterBase();
        if (characterBase == null)
            return true;

        stats.CharacterBases++;
        if (characterBase->Skeleton != null)
            stats.Skeletons++;

        int slotCount = characterBase->SlotCount;
        if (slotCount <= 0 || slotCount > MaxModelSlotsToInspect)
            return true;

        stats.ModelSlots += slotCount;
        if (characterBase->Models == null)
            return true;

        for (int slot = 0; slot < slotCount; slot++)
        {
            // Non-null means the model slot is populated; it does not prove every GPU resource
            // for that model is resident, so the UI deliberately avoids calling it "loaded".
            if (characterBase->Models[slot] != null)
                stats.PopulatedModelSlots++;
        }

        return true;
    }

    private static bool TryGetDistanceSquared(FfxGameObject* go, float localX, float localY, float localZ, out float distanceSquared)
    {
        float dx = go->Position.X - localX;
        float dy = go->Position.Y - localY;
        float dz = go->Position.Z - localZ;
        distanceSquared = (dx * dx) + (dy * dy) + (dz * dz);
        return float.IsFinite(distanceSquared) && distanceSquared >= 0f;
    }

    private static void AddDistance(ref DistanceDistribution distribution, bool hasDistance, float distanceSquared, bool hasDrawObject, byte currentDistance)
    {
        // CurrentDistance is the game's own byte-sized distance-to-player value. Keep it raw:
        // its units are not documented, and it can still be inspected when world coordinates
        // are temporarily unavailable.
        if (hasDrawObject)
            AddClientDistance(ref distribution.CurrentDistanceWithDrawObject, currentDistance);
        else
            AddClientDistance(ref distribution.CurrentDistanceWithoutDrawObject, currentDistance);

        if (!hasDistance)
        {
            distribution.DistanceUnavailable++;
            return;
        }

        if (distanceSquared < 100f) // less than 10 yalms
        {
            AddDistance(ref distribution.Under10, hasDrawObject);
        }
        else if (distanceSquared < 400f) // 10 to less than 20 yalms
        {
            AddDistance(ref distribution.From10To20, hasDrawObject);
        }
        else if (distanceSquared < 900f) // 20 to less than 30 yalms
        {
            AddDistance(ref distribution.From20To30, hasDrawObject);
        }
        else if (distanceSquared < 2500f) // 30 to less than 50 yalms
        {
            AddDistance(ref distribution.From30To50, hasDrawObject);
        }
        else
        {
            AddDistance(ref distribution.Over50, hasDrawObject);
        }
    }

    private static void AddDistance(ref DistanceBandStats stats, bool hasDrawObject)
    {
        stats.Objects++;
        if (hasDrawObject)
            stats.WithDrawObject++;
    }

    private static void AddClientDistance(ref ClientDistanceValueStats stats, byte value)
    {
        if (stats.Count == 0)
        {
            stats.Minimum = value;
            stats.Maximum = value;
        }
        else
        {
            if (value < stats.Minimum) stats.Minimum = value;
            if (value > stats.Maximum) stats.Maximum = value;
        }

        stats.Count++;
        stats.Total += value;
    }
}
