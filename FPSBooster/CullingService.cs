using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using CullingManager = FFXIVClientStructs.FFXIV.Client.Graphics.Culling.CullingManager;
using GraphicsConfig = FFXIVClientStructs.FFXIV.Client.Graphics.Render.GraphicsConfig;
using SharedGroupLayoutInstance = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Group.SharedGroupLayoutInstance;
using ChildNodeInstance = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Node.ChildNodeInstance;
using ILayoutInstance = FFXIVClientStructs.FFXIV.Client.LayoutEngine.ILayoutInstance;
using LayoutLightInstance = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.LightLayoutInstance;
using LayoutLightType = FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.LightType;
using LayoutInstanceType = FFXIVClientStructs.FFXIV.Client.LayoutEngine.InstanceType;
using DrawObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.DrawObject;
using BgObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.BgObject;
using SceneObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using SceneObjectType = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType;
using GameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using HousingObject = FFXIVClientStructs.FFXIV.Client.Game.Object.HousingObject;
using HousingObjectType = FFXIVClientStructs.FFXIV.Client.Game.Object.HousingObjectType;
using SphereBounds = FFXIVClientStructs.FFXIV.Common.Math.SphereBounds;
using VisibilityFlags = FFXIVClientStructs.FFXIV.Client.Game.Object.VisibilityFlags;

namespace FPSBooster;

public struct CullStats
{
    public int FurnitureItems;
    public int Hidden;
}

public sealed unsafe class CullingService : IDisposable
{
    private const ulong InvisibleBit = 0x2;       // GameObject.RenderFlags invisibility, same bit Penumbra writes
    private const byte DrawVisibleBits = 0x09;    // DrawObject.IsVisible bits preserved by the fixed combined method
    private const byte ModelLoaded = 7;
    private const float FallbackRadius = 4f;
    private const float ShadowSpotlightProtectionRadius = 0.75f;
    private const float ShadowSpotlightUnknownFallbackPadding = 1f;
    private const int PruneEvery = 600;
    private const int MaxSharedGroupDepth = 4;
    private const int MaxChildrenPerGroup = 512;
    private const int MaxInspectedGroupNodes = 4096;
    private const long StableCameraRefreshMs = 100;

    private struct Hidden
    {
        public ulong Seen;
        public nint Draw;
        public bool WasInvisible;
        public byte DrawBits;
    }

    private enum SpotlightScanResult { NoSpotlight, HasSpotlight, Unknown }

    private struct Bound
    {
        public ulong Frame;
        public nint Draw;
        public Vector3 Pos;
        public float Rot;
        public float Scale;
        public Vector3 Center;
        public float Radius;
        public bool Eligible;
        public bool SharedGroup;
    }

    private struct SpotlightScanCache
    {
        public nint Group;
        public nint First;
        public nint Last;
        public long Count;
        public Vector3 ParentPos;
        public float ParentRot;
        public float ParentScale;
        public ulong Seen;
        public SpotlightScanResult Result;
        public SpotlightProtectionSphere[]? SpotlightSpheres;
    }

    // Zone around an active spotlight; furniture within 0.75 yalms (plus its own radius) stays visible.
    private struct SpotlightProtectionSphere
    {
        public Vector3 Center;
    }

    // Fallback zone for when an active spotlight's position cannot be read safely.
    private struct SpotlightProtectionArea
    {
        public Vector3 Center;
        public float Radius;
    }

    // Indoor furniture renders via SharedGroupLayoutInstance; tracks only groups that were active before we disabled them.
    private struct HiddenGroup
    {
        public ulong Seen;
        public nint Group;
        public bool WasActive;
    }

    private readonly IFramework _fw;
    private readonly IClientState _cs;
    private readonly ICondition _cond;
    private readonly IObjectTable _ot;
    private readonly IPluginLog _log;
    private readonly Configuration _cfg;

    private readonly Dictionary<nint, Hidden> _hidden = new();
    private readonly Dictionary<nint, HiddenGroup> _hiddenGroups = new();
    private readonly Dictionary<nint, Bound> _bounds = new();
    private readonly Dictionary<nint, SpotlightScanCache> _spotlightScans = new();
    private readonly List<SpotlightProtectionSphere> _spotlightProtectionSpheres = new(32);
    private readonly List<SpotlightProtectionArea> _unknownSpotlightProtectionAreas = new(8);
    private readonly List<nint> _scratch = new();

    private ulong _frame;
    private nint _terrPtr;
    private uint _terrId;
    private DateTime _nextErrorLog = DateTime.MinValue;

    // While the camera is steady, a full pass runs only periodically to catch newly loaded or moved furniture.
    private bool _hasStablePass;
    private bool _lastPassWasTest;
    private bool _lastPassUseView;
    private bool _lastPassForce;
    private bool _lastPassForceExceptSpotlights;
    private bool _lastPassHasFrustum;
    private bool _lastPassNativeOutdoorCulling;
    private float _lastPassNear, _lastPassMaxDist, _lastPassMaxHeight, _lastPassFovMargin;
    private ViewFrustum _lastPassFrustum;
    private long _nextStablePassAt;

    // Per-pass rules, set once before the loop.
    private bool _cull, _useView, _force, _forceExceptSpotlights;
    private bool _spotlightProtectionKnown = true;
    private bool _nativeOutdoorCulling;
    private long _nextNativeOutdoorCountAt;
    private float _near, _maxDist, _maxHeight;
    private ViewFrustum _fr;
    private CullStats _st;

    public bool ForceHideAll;
    public bool ForceHideAllExceptSpotlights;
    public string Status = "Starting";
    public CullStats Stats;
    public bool Active;
    public bool PerformanceTimingEnabled;
    public double LastPerformanceUpdateMilliseconds { get; private set; }
    public bool UsingNativeOutdoorCulling { get; private set; }
    public bool NativeOutdoorExtraLimitsActive { get; private set; }
    public bool NativeOutdoorExtraLimitsWaitingForCamera { get; private set; }

    public CullingService(IFramework fw, IClientState cs, ICondition cond, IObjectTable ot, IPluginLog log, Configuration cfg)
    {
        _fw = fw;
        _cs = cs;
        _cond = cond;
        _ot = ot;
        _log = log;
        _cfg = cfg;
        _fw.Update += OnUpdate;
    }

    public void Dispose()
    {
        ForceHideAll = false;
        ForceHideAllExceptSpotlights = false;
        _fw.Update -= OnUpdate;
        try { _fw.RunOnFrameworkThread(RestoreNow).Wait(2000); }
        catch (Exception ex) { _log.Warning(ex, "FPSBooster: could not restore furniture on unload."); }
        _hidden.Clear();
        _hiddenGroups.Clear();
        _bounds.Clear();
        _spotlightScans.Clear();
    }

    private void OnUpdate(IFramework _)
    {
        long started = PerformanceTimingEnabled ? Stopwatch.GetTimestamp() : 0;
        try { Tick(); }
        catch (Exception ex)
        {
            if (DateTime.UtcNow >= _nextErrorLog)
            {
                _nextErrorLog = DateTime.UtcNow.AddSeconds(10);
                _log.Error(ex, "FPSBooster: pass failed.");
            }
        }
        finally
        {
            if (started != 0)
                LastPerformanceUpdateMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    public void ResetPerformanceTiming() => LastPerformanceUpdateMilliseconds = 0d;

    private void Tick()
    {
        _frame++;

        var hm = HousingManager.Instance();
        var type = hm != null ? hm->GetCurrentHousingTerritoryType() : HousingTerritoryType.None;
        if (type is not (HousingTerritoryType.Indoor or HousingTerritoryType.Outdoor))
        {
            DropAll("Not in a housing area");
            return;
        }

        // Objects are torn down around zone changes, so forget them without writing.
        if (_cond[ConditionFlag.BetweenAreas] || _cond[ConditionFlag.BetweenAreas51])
        {
            DropAll("Loading");
            return;
        }

        var terr = hm->CurrentTerritory;
        var tid = _cs.TerritoryType;
        if ((nint)terr != _terrPtr || tid != _terrId)
        {
            _hidden.Clear();
            _hiddenGroups.Clear();
            _bounds.Clear();
            _spotlightScans.Clear();
            _hasStablePass = false;
            UsingNativeOutdoorCulling = false;
            NativeOutdoorExtraLimitsActive = false;
            NativeOutdoorExtraLimitsWaitingForCamera = false;
            _nativeOutdoorCulling = false;
            Stats = default;
            _terrPtr = (nint)terr;
            _terrId = tid;
        }

        bool cull = ShouldRun(out var why);
        bool test = cull && (ForceHideAll || ForceHideAllExceptSpotlights);
        bool nativeOutdoorCulling = cull && !test
            && type == HousingTerritoryType.Outdoor
            && IsNativeOutdoorCullingActive();
        bool hasOutdoorLimits = _cfg.MaxDistance > 0f || _cfg.MaxHeightDelta > 0f;

        if (nativeOutdoorCulling != _nativeOutdoorCulling)
        {
            // Spotlight caches serve only our own culling path, not the game's native outdoor pipeline.
            _spotlightScans.Clear();
            _spotlightProtectionSpheres.Clear();
            _unknownSpotlightProtectionAreas.Clear();
            _hasStablePass = false;
        }
        _nativeOutdoorCulling = nativeOutdoorCulling;
        UsingNativeOutdoorCulling = nativeOutdoorCulling;
        NativeOutdoorExtraLimitsActive = false;
        NativeOutdoorExtraLimitsWaitingForCamera = false;

        // With no extra distance/height limits the native outdoor pipeline handles visibility, so skip our own pass.
        bool nativeOutdoorNoExtraWork = nativeOutdoorCulling && !hasOutdoorLimits;

        if (cull && !nativeOutdoorNoExtraWork)
        {
            if (ViewFrustum.TryCreate(_cfg.FovMarginDeg, out var fr, out var failure))
            {
                _fr = fr;
                // Native outdoor mode with only distance/height limits needs just the camera position, not orientation or player-in-view.
                if (!test && !nativeOutdoorCulling && _cfg.RequirePlayerInView && !PlayerInView(fr))
                {
                    cull = false;
                    why = "Player not in camera view (free-cam? untick the self-check)";
                }
            }
            else
            {
                _fr = default;
                if (!test)
                {
                    cull = false;
                    why = nativeOutdoorCulling
                        ? "Native outdoor culling active; waiting for camera for extra limits"
                        : "Camera unavailable: " + failure;
                }
            }
        }

        NativeOutdoorExtraLimitsActive = nativeOutdoorCulling && hasOutdoorLimits && cull;
        NativeOutdoorExtraLimitsWaitingForCamera = nativeOutdoorCulling && hasOutdoorLimits && !cull;
        Active = cull || nativeOutdoorCulling;
        if (nativeOutdoorNoExtraWork)
            Status = "Using native outdoor culling";
        else if (NativeOutdoorExtraLimitsWaitingForCamera)
            Status = "Native outdoor culling active; waiting for camera for extra limits";
        else if (!cull) Status = why;
        else if (ForceHideAll) Status = "Test: hiding all furniture";
        else if (ForceHideAllExceptSpotlights) Status = "Test: keeping spotlight protection";
        else if (nativeOutdoorCulling) Status = "Native outdoor culling + extra limits";
        else Status = "Active";

        var fm = type == HousingTerritoryType.Indoor
            ? &((IndoorTerritory*)terr)->FurnitureManager
            : &((OutdoorTerritory*)terr)->FurnitureManager;

        _cull = cull;
        _force = ForceHideAll;
        _forceExceptSpotlights = ForceHideAllExceptSpotlights && !_force;
        _useView = _cfg.CullOutsideView && !nativeOutdoorCulling;
        _near = _cfg.AlwaysShowRadius;
        _maxDist = _cfg.MaxDistance;
        _maxHeight = _cfg.MaxHeightDelta;

        if (nativeOutdoorNoExtraWork)
        {
            // Restore anything we hid before native culling took over, then only refresh the furniture count.
            _cull = false;
            _force = false;
            _forceExceptSpotlights = false;
            _useView = false;
            _hasStablePass = false;
            if (_hidden.Count > 0 || _hiddenGroups.Count > 0)
            {
                _st = default;
                Sweep(fm, type);
                _st.Hidden = 0;
                Stats = _st;
                _nextNativeOutdoorCountAt = Environment.TickCount64 + 1000;
            }
            else if (Environment.TickCount64 >= _nextNativeOutdoorCountAt)
            {
                Stats.FurnitureItems = CountHousingFurniture(fm, HousingObjectType.YardObject);
                Stats.Hidden = 0;
                _nextNativeOutdoorCountAt = Environment.TickCount64 + 1000;
            }
            else
            {
                Stats.Hidden = 0;
            }
            return;
        }

        // When paused or off, restore immediately if needed; otherwise refresh the furniture total only occasionally.
        if (!cull)
        {
            _hasStablePass = false;
            if (_hidden.Count == 0 && _hiddenGroups.Count == 0
                && Stats.FurnitureItems > 0 && _frame % 120 != 0)
            {
                Stats.Hidden = 0;
                return;
            }
        }

        // Camera or setting changes force a pass; a still camera polls at 10 Hz to catch loading or moved furniture.
        if (cull && CanSkipStablePass(test))
        {
            Stats.Hidden = _hidden.Count + _hiddenGroups.Count;
            return;
        }

        _st = default;
        Sweep(fm, type);
        _st.Hidden = _hidden.Count + _hiddenGroups.Count;
        Stats = _st;

        if (cull) RememberStablePass(test);
        else _hasStablePass = false;

        if (_frame % PruneEvery == 0) PruneBounds();
    }

    private static bool IsNativeOutdoorCullingActive()
    {
        try
        {
            var culling = CullingManager.Instance();
            if (culling == null)
                return false;

            // Defer to the native grid/callback culling even when there are no occluders for the depth pass.
            var grid = culling->CullingGrid;
            if (grid != null && grid->CellCount > 0 && grid->Cells != null)
                return true;

            // Depth occlusion can be ready before the grid; it is skipped when no occluders are registered.
            var graphics = GraphicsConfig.Instance();
            return graphics != null && graphics->OcclusionCulling
                && culling->Occluders.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static int CountHousingFurniture(HousingFurnitureManager* fm, HousingObjectType expectedType)
    {
        var slots = fm->FurnitureMemory;
        var objects = fm->ObjectManager.ObjectArray.Objects;
        int count = Math.Min((int)fm->ObjectManager.ObjectArray.ObjectCount, objects.Length);
        int furnitureLimit = Math.Max(0, slots.Length - 1);
        int furnitureCount = 0;

        for (int i = 0; i < count; i++)
        {
            var go = objects[i].Value;
            if (go == null) continue;
            var housing = (HousingObject*)go;
            short furnitureIndex = housing->HousingFurnitureIndex;
            if (housing->HousingObjectId.Type == expectedType
                && furnitureIndex >= 0 && furnitureIndex < furnitureLimit)
                furnitureCount++;
        }

        return furnitureCount;
    }

    private bool CanSkipStablePass(bool test)
    {
        if (!_hasStablePass) return false;

        // Every setting that changes the hide decision must invalidate the cached pass.
        if (_lastPassWasTest != test
            || _lastPassUseView != _useView
            || _lastPassForce != _force
            || _lastPassForceExceptSpotlights != _forceExceptSpotlights
            || _lastPassNativeOutdoorCulling != _nativeOutdoorCulling
            || _lastPassNear != _near
            || _lastPassMaxDist != _maxDist
            || _lastPassMaxHeight != _maxHeight
            || _lastPassFovMargin != _cfg.FovMarginDeg)
            return false;

        // Hold-to-test mode hides every loaded furnishing, so camera movement cannot change its result.
        if (!test && (!_lastPassHasFrustum
            || (_useView
                ? _fr.IsMeaningfullyDifferent(_lastPassFrustum)
                : _fr.IsPositionMeaningfullyDifferent(_lastPassFrustum))))
            return false;

        return Environment.TickCount64 < _nextStablePassAt;
    }

    private void RememberStablePass(bool test)
    {
        _hasStablePass = true;
        _lastPassWasTest = test;
        _lastPassUseView = _useView;
        _lastPassForce = _force;
        _lastPassForceExceptSpotlights = _forceExceptSpotlights;
        _lastPassNativeOutdoorCulling = _nativeOutdoorCulling;
        _lastPassNear = _near;
        _lastPassMaxDist = _maxDist;
        _lastPassMaxHeight = _maxHeight;
        _lastPassFovMargin = _cfg.FovMarginDeg;
        _lastPassFrustum = _fr;
        _lastPassHasFrustum = !test;
        _nextStablePassAt = Environment.TickCount64 + StableCameraRefreshMs;
    }

    private bool ShouldRun(out string why)
    {
        why = "";
        if (!_cfg.Enabled) { why = "Off"; return false; }
        if (_cond[ConditionFlag.OccupiedInCutSceneEvent] || _cond[ConditionFlag.WatchingCutscene] || _cond[ConditionFlag.WatchingCutscene78])
        {
            why = "Paused (cutscene)";
            return false;
        }
        if (_cfg.PauseInGpose && _cs.IsGPosing) { why = "Paused (GPose)"; return false; }
        if (_ot.LocalPlayer == null) { why = "No player"; return false; }
        return true;
    }

    // Catches a wrong frustum before it hides the whole room: the player should be on screen.
    private bool PlayerInView(in ViewFrustum fr)
    {
        var p = _ot.LocalPlayer;
        return p != null && fr.SphereVisible(p.Position + new Vector3(0f, 1f, 0f), 1.5f);
    }

    private void Sweep(HousingFurnitureManager* fm, HousingTerritoryType territoryType)
    {
        // The object array is the authoritative live list; HousingFurnitureIndex points back into FurnitureMemory.
        var slots = fm->FurnitureMemory;
        var objects = fm->ObjectManager.ObjectArray.Objects;
        int count = Math.Min((int)fm->ObjectManager.ObjectArray.ObjectCount, objects.Length);
        int furnitureLimit = Math.Max(0, slots.Length - 1); // final slot is reserved for the temporary object
        var expectedType = territoryType == HousingTerritoryType.Indoor
            ? HousingObjectType.Furniture
            : HousingObjectType.YardObject;

        // Gather all spotlights first so the protection check does not depend on object-array order.
        _spotlightProtectionSpheres.Clear();
        _unknownSpotlightProtectionAreas.Clear();
        _spotlightProtectionKnown = true;
        if (_cull && !_force && !_nativeOutdoorCulling)
        {
            for (int i = 0; i < count; i++)
            {
                var go = objects[i].Value;
                if (go == null) continue;

                var housing = (HousingObject*)go;
                short furnitureIndex = housing->HousingFurnitureIndex;
                if (housing->HousingObjectId.Type != expectedType || furnitureIndex < 0 || furnitureIndex >= furnitureLimit)
                    continue;

                var group = go->SharedGroupLayoutInstance;
                if (group == null) continue; // direct-render furniture has no shared-group spotlight tree

                // Skip groups the game disabled; groups we hid are still scanned so their cached spotlight data stays available.
                nint key = (nint)go;
                if (!group->IsActive && !_hiddenGroups.ContainsKey(key))
                    continue;

                if (!go->IsSharedGroupLoaded())
                {
                    // An unloaded group emits no shadow yet; wait for its prefab before scanning.
                    continue;
                }

                // A spotlight beyond an enabled max distance/height limit creates no protection zone.
                if ((_maxDist > 0f || _maxHeight > 0f)
                    && TryGetSharedGroupBound(go, group, out var ownerBound)
                    && ExceedsMaximumDistanceOrHeight(in ownerBound))
                    continue;

                var scan = ScanForSpotlightsCached(key, group, go->Position, go->Rotation, go->Scale, out var spotlightSpheres);
                if (scan == SpotlightScanResult.Unknown)
                {
                    // Uninspectable child tree: protect the group's bounds instead of disabling culling room-wide.
                    if (!AddUnknownSpotlightProtectionArea(group, go->Position))
                        _spotlightProtectionKnown = false;
                    continue;
                }

                for (int spotlightIndex = 0; spotlightIndex < spotlightSpheres.Length; spotlightIndex++)
                {
                    _spotlightProtectionSpheres.Add(spotlightSpheres[spotlightIndex]);
                }
            }
        }

        for (int i = 0; i < count; i++)
        {
            var go = objects[i].Value;
            if (go == null) continue;

            var housing = (HousingObject*)go;
            short furnitureIndex = housing->HousingFurnitureIndex;
            if (housing->HousingObjectId.Type != expectedType || furnitureIndex < 0 || furnitureIndex >= furnitureLimit)
            {
                Unhide(go, (nint)go);
                UnhideGroup((nint)go, go->SharedGroupLayoutInstance);
                continue;
            }

            _st.FurnitureItems++;
            var group = go->SharedGroupLayoutInstance;
            if (group != null)
            {
                // Indoor furniture usually renders via a shared group; cull it whole using its bounding sphere.
                ProcessSharedGroup(go, group);
            }
            else
            {
                Process(go);
            }
        }

        if (!_cull)
        {
            _hidden.Clear();
            _hiddenGroups.Clear();
            return;
        }

        // Anything not re-confirmed this pass is gone; forget it without writing through a stale pointer.
        if (_hidden.Count > 0)
        {
            _scratch.Clear();
            foreach (var kv in _hidden)
                if (kv.Value.Seen != _frame) _scratch.Add(kv.Key);
            foreach (var k in _scratch) _hidden.Remove(k);
        }
        if (_hiddenGroups.Count > 0)
        {
            _scratch.Clear();
            foreach (var kv in _hiddenGroups)
                if (kv.Value.Seen != _frame) _scratch.Add(kv.Key);
            foreach (var k in _scratch) _hiddenGroups.Remove(k);
        }
    }

    private void ProcessSharedGroup(GameObject* go, SharedGroupLayoutInstance* group)
    {
        nint key = (nint)go;
        Unhide(go, key); // a housing object may have switched from the direct path to a shared group
        if (!_cull)
        {
            UnhideGroup(key, group);
            return;
        }

        // Do not touch a shared group until its housing object reports it loaded.
        if (!go->IsSharedGroupLoaded())
        {
            UnhideGroup(key, group);
            _spotlightScans.Remove(key);
            _bounds.Remove(key);
            return;
        }

        // The hold-to-test button bypasses normal safety rules (spotlights included) and lasts only while held.
        if (_force)
        {
            if (!group->IsActive && !_hiddenGroups.ContainsKey(key))
                return; // Do not take ownership of a group the game already deactivated.
            HideGroup(group, key);
            return;
        }

        // Leave groups alone if the game had already disabled them before we arrived.
        if (!group->IsActive && !_hiddenGroups.ContainsKey(key))
            return;

        // Prefab contents are static while loaded, so cache the spotlight scan; Unknown results are never cached.
        var spotlightScan = _nativeOutdoorCulling
            ? SpotlightScanResult.NoSpotlight
            : ScanForSpotlightsCached(key, group, go->Position, go->Rotation, go->Scale, out _);

        Vector3 pos = go->Position;
        float rot = go->Rotation;
        float scale = go->Scale;
        ref var bound = ref CollectionsMarshal.GetValueRefOrAddDefault(_bounds, key, out var existed);
        if (bound.Frame == _frame) return; // duplicate object-array slot
        bound.Frame = _frame;
        if (!existed || !bound.SharedGroup || bound.Draw != (nint)group
            || bound.Pos != pos || bound.Rot != rot || bound.Scale != scale)
            BuildSharedGroupBound(group, pos, rot, scale, ref bound);

        if (!bound.Eligible)
        {
            UnhideGroup(key, group);
            return;
        }

        if (spotlightScan != SpotlightScanResult.NoSpotlight)
        {
            // Spotlight furniture can cast shadows off screen, but enabled max distance/height limits still win.
            if (ExceedsMaximumDistanceOrHeight(in bound))
                HideGroup(group, key);
            else
                UnhideGroup(key, group);
            return;
        }

        if (!ShouldHide(in bound))
        {
            UnhideGroup(key, group);
            return;
        }

        HideGroup(group, key);
    }

    private SpotlightScanResult ScanForSpotlightsCached(
        nint key,
        SharedGroupLayoutInstance* group,
        Vector3 parentPos,
        float parentRot,
        float parentScale,
        out SpotlightProtectionSphere[] spotlightSpheres)
    {
        var children = group->Instances.Instances;
        long childCount = children.LongCount;
        nint first = (nint)children.First;
        nint last = (nint)children.Last;

        ref var cached = ref CollectionsMarshal.GetValueRefOrNullRef(_spotlightScans, key);
        if (!Unsafe.IsNullRef(ref cached)
            && cached.Group == (nint)group
            && cached.First == first
            && cached.Last == last
            && cached.Count == childCount
            && cached.ParentPos == parentPos
            && cached.ParentRot == parentRot
            && cached.ParentScale == parentScale)
        {
            cached.Seen = _frame;
            spotlightSpheres = cached.SpotlightSpheres ?? Array.Empty<SpotlightProtectionSphere>();
            return cached.Result;
        }

        int inspectedNodes = 0;
        var foundSpotlightSpheres = new List<SpotlightProtectionSphere>(4);
        var result = ScanGroupForSpotlights(group, 0, ref inspectedNodes, foundSpotlightSpheres);
        if (result == SpotlightScanResult.Unknown)
        {
            _spotlightScans.Remove(key);
            spotlightSpheres = Array.Empty<SpotlightProtectionSphere>();
            return result;
        }

        spotlightSpheres = foundSpotlightSpheres.ToArray();
        _spotlightScans[key] = new SpotlightScanCache
        {
            Group = (nint)group,
            First = first,
            Last = last,
            Count = childCount,
            ParentPos = parentPos,
            ParentRot = parentRot,
            ParentScale = parentScale,
            Seen = _frame,
            Result = spotlightSpheres.Length > 0 ? SpotlightScanResult.HasSpotlight : SpotlightScanResult.NoSpotlight,
            SpotlightSpheres = spotlightSpheres,
        };
        return spotlightSpheres.Length > 0 ? SpotlightScanResult.HasSpotlight : SpotlightScanResult.NoSpotlight;
    }

    private static SpotlightScanResult ScanGroupForSpotlights(
        SharedGroupLayoutInstance* group,
        int depth,
        ref int inspectedNodes,
        List<SpotlightProtectionSphere> spotlightSpheres)
    {
        if (group == null || depth > MaxSharedGroupDepth)
            return SpotlightScanResult.Unknown;

        var children = group->Instances.Instances;
        long childCount = children.LongCount;
        if (childCount <= 0 || childCount > MaxChildrenPerGroup
            || (childCount > 0 && (children.First == null || children.Last == null)))
            return SpotlightScanResult.Unknown;

        for (int i = 0; i < (int)childCount; i++)
        {
            if (++inspectedNodes > MaxInspectedGroupNodes)
                return SpotlightScanResult.Unknown;

            ChildNodeInstance* childNode = children[i].Value;
            if (childNode == null || childNode->Instance == null)
                return SpotlightScanResult.Unknown;

            ILayoutInstance* instance = childNode->Instance;
            switch (instance->Id.Type)
            {
                case LayoutInstanceType.Light:
                {
                    // Only spotlights cast furniture shadows; other light types are ignored.
                    if (!instance->IsActive)
                        break;

                    var lightInstance = (LayoutLightInstance*)instance;
                    if (lightInstance->LightType != LayoutLightType.Spot)
                        break;

                    // A spotlight with no graphics object yet may still cast shadows, so use the bounds fallback.
                    if (!instance->IsPrimaryLoaded() || lightInstance->GraphicsObject == null)
                        return SpotlightScanResult.Unknown;

                    // Only the spotlight's position is read; rotation, beam angle and range are deliberately ignored.
                    Vector3 position = lightInstance->GraphicsObject->Position;
                    if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                        return SpotlightScanResult.Unknown;

                    spotlightSpheres.Add(new SpotlightProtectionSphere
                    {
                        Center = position,
                    });
                    break;
                }

                case LayoutInstanceType.SharedGroup:
                {
                    // Inactive nested groups emit no light; an active but unloaded one is Unknown, so fail open.
                    if (!instance->IsActive)
                        break;
                    if (!instance->IsPrimaryLoaded())
                        return SpotlightScanResult.Unknown;

                    var nestedResult = ScanGroupForSpotlights((SharedGroupLayoutInstance*)instance, depth + 1, ref inspectedNodes, spotlightSpheres);
                    if (nestedResult == SpotlightScanResult.Unknown)
                        return nestedResult;
                    break;
                }
                case LayoutInstanceType.BgPart:
                    break;
            }
        }

        return spotlightSpheres.Count > 0 ? SpotlightScanResult.HasSpotlight : SpotlightScanResult.NoSpotlight;
    }

    private void HideGroup(SharedGroupLayoutInstance* group, nint key)
    {
        ref var tracked = ref CollectionsMarshal.GetValueRefOrNullRef(_hiddenGroups, key);
        if (!Unsafe.IsNullRef(ref tracked) && tracked.Group == (nint)group)
        {
            tracked.Seen = _frame;
            if (group->IsActive) group->SetActive(false);
            return;
        }

        _hiddenGroups.Remove(key);
        if (!group->IsActive) return; // never take ownership of a group the game already deactivated

        var h = new HiddenGroup { Group = (nint)group, WasActive = true, Seen = _frame };
        group->SetActive(false); // hides the whole prefab, BG parts and nested groups included
        _hiddenGroups[key] = h;
    }

    private void UnhideGroup(nint key, SharedGroupLayoutInstance* currentGroup)
    {
        if (_hiddenGroups.Count == 0) return;
        if (!_hiddenGroups.Remove(key, out var h)) return;
        if (currentGroup == null || (nint)currentGroup != h.Group) return;
        if (h.WasActive && !currentGroup->IsActive)
            currentGroup->SetActive(true);
    }

    private void Process(GameObject* go)
    {
        var key = (nint)go;
        UnhideGroup(key, go->SharedGroupLayoutInstance);

        if (!_cull)
        {
            Unhide(go, key);
            return;
        }

        var draw = go->DrawObject;
        if (_force)
        {
            // The hide-all test hides direct-render objects regardless of camera position or draw-object readiness.
            Hide(go, draw, key);
            return;
        }

        if (draw == null)
        {
            // Without a DrawObject there are no bounds to check against spotlights, so leave the object alone.
            Unhide(go, key);
            return;
        }

        Vector3 pos = go->Position;
        float rot = go->Rotation;
        float scale = go->Scale;
        ref var b = ref CollectionsMarshal.GetValueRefOrAddDefault(_bounds, key, out var existed);
        if (b.Frame == _frame) return;   // duplicate slot pointing at the same object
        b.Frame = _frame;
        if (!existed || b.SharedGroup || b.Draw != (nint)draw
            || b.Pos != pos || b.Rot != rot || b.Scale != scale)
            BuildBound(draw, pos, rot, scale, ref b);

        if (!b.Eligible)
        {
            Unhide(go, key);
            return;
        }

        if (!ShouldHide(in b))
        {
            Unhide(go, key);
            return;
        }

        Hide(go, draw, key);
    }

    private static void BuildSharedGroupBound(SharedGroupLayoutInstance* group, Vector3 pos, float rot, float scale, ref Bound b)
    {
        b.Draw = (nint)group;
        b.Pos = pos;
        b.Rot = rot;
        b.Scale = scale;
        b.SharedGroup = true;
        b.Eligible = false;

        Vector4 sphere = default;
        if (group->GetBoundingSphereImpl(&sphere) == null
            || !float.IsFinite(sphere.X) || !float.IsFinite(sphere.Y) || !float.IsFinite(sphere.Z)
            || !float.IsFinite(sphere.W) || sphere.W <= 0.05f || sphere.W >= 200f)
        {
            b.Draw = 0; // retry next update if the group's bounds are not ready yet
            return;
        }

        b.Center = new Vector3(sphere.X, sphere.Y, sphere.Z);
        b.Radius = sphere.W;
        b.Eligible = true;
    }

    private static void BuildBound(DrawObject* draw, Vector3 pos, float rot, float scale, ref Bound b)
    {
        b.Draw = (nint)draw;
        b.Pos = pos;
        b.Rot = rot;
        b.Scale = scale;
        b.SharedGroup = false;
        b.Eligible = false;

        // Only plain BgObject furniture; characters/animated pieces are left alone.
        if (((SceneObject*)draw)->GetObjectType() != SceneObjectType.BgObject) return;

        var mrh = ((BgObject*)draw)->ModelResourceHandle;
        if (mrh == null || mrh->ResourceHandle.LoadState != ModelLoaded)
        {
            b.Draw = 0;   // not loaded yet, rebuild next frame
            return;
        }

        SphereBounds sb = default;
        draw->ComputeSphereBounds(&sb);
        Vector3 c = sb.CenterPoint;
        float r = sb.Radius;
        b.Eligible = true;

        // If the game's sphere looks wrong (e.g. not world-space), fall back to the object origin.
        Vector3 centerOffset = c - pos;
        float maxCenterOffset = r * 1.5f + 2f;
        bool ok = float.IsFinite(c.X) && float.IsFinite(c.Y) && float.IsFinite(c.Z)
            && r > 0.05f && r < 200f && centerOffset.LengthSquared() <= maxCenterOffset * maxCenterOffset;
        if (!ok)
        {
            c = pos + new Vector3(0f, FallbackRadius * 0.5f, 0f);
            r = FallbackRadius;
        }
        b.Center = c;
        b.Radius = r;
    }

    private bool ShouldHide(in Bound b)
    {
        if (_force) return true;

        // Max distance/height are hard limits, checked before the nearby and spotlight exceptions.
        if (ExceedsMaximumDistanceOrHeight(in b)) return true;

        // Keep furniture if a possible spotlight is unlocated or this item is within 0.75 yalms of a known one.
        if (!_spotlightProtectionKnown || IsNearSpotlightProtectionArea(in b)) return false;

        // The spotlight-aware test acts as if everything were off screen but still honors limits and spotlight protection.
        if (_forceExceptSpotlights) return true;

        var d = b.Center - _fr.Pos;
        float distanceSquared = d.LengthSquared();

        // Compare squared distances to avoid a square root for every furniture item.
        float keepRadius = _near + b.Radius;
        if (distanceSquared <= keepRadius * keepRadius) return false;

        if (_useView && !_fr.SphereVisible(b.Center, b.Radius)) return true;
        return false;
    }

    private bool ExceedsMaximumDistanceOrHeight(in Bound b)
    {
        if (_maxDist <= 0f && _maxHeight <= 0f) return false;

        var offset = b.Center - _fr.Pos;
        float distanceSquared = offset.LengthSquared();

        if (_maxDist > 0f)
        {
            float allowedRadius = _maxDist + b.Radius;
            if (distanceSquared > allowedRadius * allowedRadius) return true;
        }

        if (_maxHeight > 0f && MathF.Abs(offset.Y) > _maxHeight + b.Radius)
            return true;

        return false;
    }

    // Reuses cached bounds in the prepass so lights beyond a hard limit protect nothing, without a second sphere query.
    private bool TryGetSharedGroupBound(GameObject* go, SharedGroupLayoutInstance* group, out Bound result)
    {
        nint key = (nint)go;
        Vector3 pos = go->Position;
        float rot = go->Rotation;
        float scale = go->Scale;

        if (_bounds.TryGetValue(key, out var cached)
            && cached.Eligible && cached.SharedGroup && cached.Draw == (nint)group
            && cached.Pos == pos && cached.Rot == rot && cached.Scale == scale)
        {
            result = cached;
            return true;
        }

        ulong previousFrame = cached.Frame;
        var rebuilt = default(Bound);
        BuildSharedGroupBound(group, pos, rot, scale, ref rebuilt);
        rebuilt.Frame = previousFrame;
        result = rebuilt;
        if (!rebuilt.Eligible) return false;

        _bounds[key] = rebuilt;
        return true;
    }

    private bool AddUnknownSpotlightProtectionArea(SharedGroupLayoutInstance* group, Vector3 parentPos)
    {
        Vector4 sphere = default;
        if (group != null
            && group->GetBoundingSphereImpl(&sphere) != null
            && float.IsFinite(sphere.X) && float.IsFinite(sphere.Y) && float.IsFinite(sphere.Z)
            && float.IsFinite(sphere.W) && sphere.W > 0.05f && sphere.W < 200f)
        {
            _unknownSpotlightProtectionAreas.Add(new SpotlightProtectionArea
            {
                Center = new Vector3(sphere.X, sphere.Y, sphere.Z),
                Radius = sphere.W + ShadowSpotlightUnknownFallbackPadding,
            });
            return true;
        }

        if (!float.IsFinite(parentPos.X) || !float.IsFinite(parentPos.Y) || !float.IsFinite(parentPos.Z))
            return false;

        // Bounds not ready: protect a small area around the object origin, wider than the normal 0.75-yalm radius.
        _unknownSpotlightProtectionAreas.Add(new SpotlightProtectionArea
        {
            Center = parentPos,
            Radius = FallbackRadius + ShadowSpotlightUnknownFallbackPadding,
        });
        return true;
    }

    private bool IsNearSpotlightProtectionArea(in Bound bound)
    {
        // Fallback zones for spotlights whose graphics or transform data could not be read.
        for (int i = 0; i < _unknownSpotlightProtectionAreas.Count; i++)
        {
            var area = _unknownSpotlightProtectionAreas[i];
            float radius = area.Radius + bound.Radius;
            var offset = bound.Center - area.Center;
            if (offset.LengthSquared() <= radius * radius)
                return true;
        }

        // Expanding by the furniture radius protects geometry near a light, not just items whose center is close.
        float protectionRadius = ShadowSpotlightProtectionRadius + bound.Radius;
        float protectionRadiusSquared = protectionRadius * protectionRadius;
        for (int i = 0; i < _spotlightProtectionSpheres.Count; i++)
        {
            var offset = bound.Center - _spotlightProtectionSpheres[i].Center;
            if (offset.LengthSquared() <= protectionRadiusSquared)
                return true;
        }

        return false;
    }

    private void Hide(GameObject* go, DrawObject* draw, nint key)
    {
        ref var h = ref CollectionsMarshal.GetValueRefOrAddDefault(_hidden, key, out var existed);
        if (!existed)
            h.WasInvisible = ((ulong)go->RenderFlags & InvisibleBit) != 0;
        if (draw != null)
        {
            if (!existed || h.Draw != (nint)draw)
            {
                h.Draw = (nint)draw;
                h.DrawBits = (byte)(draw->Flags & DrawVisibleBits);
            }
        }
        else if (!existed)
        {
            h.Draw = 0;
            h.DrawBits = 0;
        }
        h.Seen = _frame;

        // Apply both visibility mechanisms; the hide-all test still sets the GameObject flag when the DrawObject is missing.
        go->RenderFlags = (VisibilityFlags)((ulong)go->RenderFlags | InvisibleBit);
        if (draw != null) draw->IsVisible = false;
    }

    private void Unhide(GameObject* go, nint key)
    {
        if (_hidden.Count > 0 && _hidden.Remove(key, out var h)) Restore(go, in h);
    }

    private static void Restore(GameObject* go, in Hidden h)
    {
        if (!h.WasInvisible)
            go->RenderFlags = (VisibilityFlags)((ulong)go->RenderFlags & ~InvisibleBit);

        var draw = go->DrawObject;
        if (draw != null && (nint)draw == h.Draw)
            draw->Flags = (byte)((draw->Flags & ~DrawVisibleBits) | h.DrawBits);
    }

    // Forget everything without touching game memory.
    private void DropAll(string status)
    {
        _hidden.Clear();
        _hiddenGroups.Clear();
        _bounds.Clear();
        _spotlightScans.Clear();
        _spotlightProtectionSpheres.Clear();
        _unknownSpotlightProtectionAreas.Clear();
        _hasStablePass = false;
        _nativeOutdoorCulling = false;
        UsingNativeOutdoorCulling = false;
        NativeOutdoorExtraLimitsActive = false;
        NativeOutdoorExtraLimitsWaitingForCamera = false;
        _terrPtr = 0;
        Active = false;
        Stats = default;
        Status = status;
    }

    private void PruneBounds()
    {
        _scratch.Clear();
        foreach (var kv in _bounds)
            if (kv.Value.Frame + PruneEvery < _frame) _scratch.Add(kv.Key);
        foreach (var k in _scratch) _bounds.Remove(k);

        _scratch.Clear();
        foreach (var kv in _spotlightScans)
            if (kv.Value.Seen + PruneEvery < _frame) _scratch.Add(kv.Key);
        foreach (var k in _scratch) _spotlightScans.Remove(k);
    }

    // Unload path: un-hide through the live furniture list so no stale pointer is written.
    private void RestoreNow()
    {
        if (_hidden.Count == 0 && _hiddenGroups.Count == 0) return;

        var hm = HousingManager.Instance();
        var type = hm != null ? hm->GetCurrentHousingTerritoryType() : HousingTerritoryType.None;
        if (type is not (HousingTerritoryType.Indoor or HousingTerritoryType.Outdoor) || (nint)hm->CurrentTerritory != _terrPtr)
        {
            _hidden.Clear();
            _hiddenGroups.Clear();
            return;
        }

        var terr = hm->CurrentTerritory;
        var fm = type == HousingTerritoryType.Indoor
            ? &((IndoorTerritory*)terr)->FurnitureManager
            : &((OutdoorTerritory*)terr)->FurnitureManager;
        _cull = false;
        Sweep(fm, type);
    }
}
