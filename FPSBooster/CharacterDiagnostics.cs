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
    public int DrawObjectsVisible;
    public int DrawObjectsNotVisible;
    public int GameVisibilityHidden;
    public int CharacterBases;
    public int Skeletons;
    public int ModelSlots;
    public int LoadedModelSlots;

    public readonly int WithoutDrawObject => Math.Max(0, Objects - DrawObjects);
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

            // Object-manager counts and its current drawable-candidate budget are useful context,
            // but the budget is not a count of objects actually hidden this frame.
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
                switch (wrapper.ObjectKind)
                {
                    case DalamudObjectKind.Pc:
                        Add(ref snapshot.Players, go);
                        break;
                    case DalamudObjectKind.BattleNpc:
                        Add(ref snapshot.BattleNpcs, go);
                        break;
                    case DalamudObjectKind.EventNpc:
                        Add(ref snapshot.EventNpcs, go);
                        break;
                    case DalamudObjectKind.Retainer:
                        Add(ref snapshot.Retainers, go);
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

    private static void Add(ref ActorKindStats stats, FfxGameObject* go)
    {
        stats.Objects++;

        // The documented game visibility byte uses 0 for shown and 1 for hidden.
        // This is a state indicator only; it does not identify why the game hid the object.
        if (go->Visibility != 0)
            stats.GameVisibilityHidden++;

        DrawObject* draw = go->DrawObject;
        if (draw == null)
            return;

        stats.DrawObjects++;
        if (draw->IsVisible)
            stats.DrawObjectsVisible++;
        else
            stats.DrawObjectsNotVisible++;

        // CharacterBase is the render-side character model container. A skeleton and non-null
        // model slots tell us more than simply having a GameObject.DrawObject pointer.
        CharacterBase* characterBase = go->GetCharacterBase();
        if (characterBase == null)
            return;

        stats.CharacterBases++;
        if (characterBase->Skeleton != null)
            stats.Skeletons++;

        int slotCount = characterBase->SlotCount;
        if (slotCount <= 0 || slotCount > MaxModelSlotsToInspect || characterBase->Models == null)
            return;

        stats.ModelSlots += slotCount;
        for (int slot = 0; slot < slotCount; slot++)
        {
            if (characterBase->Models[slot] != null)
                stats.LoadedModelSlots++;
        }
    }
}
