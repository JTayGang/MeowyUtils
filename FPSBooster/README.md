# FPSBooster 0.2.7.2

A performance-focused FFXIV Dalamud plugin. Indoors, it reduces unnecessary housing-furniture rendering with camera-view culling and optional distance/height limits. Outdoors, it defers to the game's native background-culling pipeline when available, and can add optional distance/height limits without repeating the game's view-culling pass.

Indoor furniture is hidden as a complete `SharedGroupLayoutInstance` using its native bounding sphere and active state. Direct-render furniture uses a combined visibility method, and its original state is restored when culling stops.

Furniture whose shared-group prefab contains an active, loaded `Spot` light (`LightType.Spot`) is kept active. Nearby furniture is protected when its bounding sphere comes within 0.75 yalms of the spotlight position. The check uses a simple sphere around each spotlight, expanded by the furniture bound radius so actual geometry near the light is protected. The whole intersecting furnishing stays visible; individual meshes are not culled separately.

Other light types (point, directional, line, plane, specular, and none) do not trigger this protection. If an active spotlight's graphics object or position cannot be read safely, the plugin uses that furnishing's full bounds (or a small fallback area around its origin) as a conservative possible-spotlight zone. If even that location cannot be established, normal culling fails open for the pass rather than risking a missing shadow.

## Use

- `/fpsbooster` opens settings.
- `/fpsbooster on` turns culling on.
- `/fpsbooster off` turns culling off and restores furniture.
- `/fpsbooster test` opens settings and explains the test buttons.
- Hold **Hold to hide ALL furniture** on the **Housing** tab to temporarily hide every loaded furnishing, including spotlights and nearby shadow-protected furniture. Release to restore normal behavior.
- Hold **Hold to hide ALL (keep spotlight protection)** to hide furniture as if it were all outside the camera view, while keeping spotlight furnishings and nearby shadow-protected objects visible. Release to restore normal behavior.
- Both tests are momentary and are never saved. Switching tabs, closing the window, or releasing the button stops the test.

## Settings

| Setting | Default | What it does |
|---|---:|---|
| Enable furniture culling | On | Turns furniture hiding on or off. |
| Hide furniture outside the screen | On | Hides items outside the camera view. |
| Extra room around the screen | 12° | Keeps nearby off-screen furniture visible a little longer to avoid popping. |
| Always show nearby furniture | 6 yalms | Adds extra protection around the player. Off removes this extra distance. |
| Maximum distance | Off | Hides furniture beyond the selected distance, even if a spotlight or nearby-item rule would otherwise protect it. |
| Maximum height difference | Off | Can hide furniture on other floors, including spotlight-protected furniture. |
| Show all furniture in GPose | On | Temporarily stops culling while in GPose. |
| Pause if my character is off screen | On | Safety check; turn off when using a free camera. |

Zero values on the three distance sliders are displayed as **Off**.

## Outdoor culling and native background visibility

- Outdoors, FPSBooster checks whether the game's native `CullingManager` has an initialized `CullingGrid` (live cells and cell storage). That grid/callback pipeline already handles view culling for background objects, so FPSBooster does not repeat its own camera-frustum pass for yard furniture while the native pipeline is available.
- The game's optional depth-buffer occlusion is a second layer inside that native pipeline: `GraphicsConfig.OcclusionCulling` gates `OcclusionTestJobs`, which test object bounds against the software occlusion depth buffer and are skipped when there are no registered occluders. FPSBooster leaves this entire native pipeline alone.
- If no extra distance/height limits are enabled, FPSBooster skips the outdoor furniture culling pass entirely. It only refreshes the displayed furniture count about once per second and restores anything it had previously hidden.
- If maximum distance or height is enabled, FPSBooster applies only those additional user-requested limits. It does not repeat the camera-frustum test or scan spotlight trees in this mode; the game's background-culling pipeline continues to handle normal visibility.
- If the native culling grid is unavailable, FPSBooster falls back to its own camera-frustum culling. The press-and-hold test buttons intentionally bypass this automatic handoff so they remain useful for testing.
- `Hidden right now` counts only objects hidden by FPSBooster. When the native game pipeline handles outdoor visibility and no extra limits are enabled, this count correctly remains zero.

## Performance

- Shared-group child trees are scanned once per loaded furnishing (and rescanned if the group, its top-level child list, or the housing object's transform changes), rather than every update.
- Spotlight positions are cached with the furniture prefab scan. The cache is reused during normal camera passes and refreshed when the group or its parent transform changes.
- Shared-group and direct-render bounds are cached and rebuilt only when the render pointer or housing-object position, rotation, or scale changes.
- Distance and height checks use squared-distance comparisons to avoid square roots in the per-furniture culling loop.
- When the camera is effectively still, the furniture pass is skipped between 100 ms refreshes. Camera movement of more than 0.05 yalms or about 0.1 degrees, changed culling settings, or leaving a paused state triggers an immediate pass. The periodic refresh catches furniture that loads or moves while the camera is still.

## Need to know

- FPSBooster's own fallback is camera-frustum culling, not wall/geometry occlusion. Indoors, furniture behind a wall can remain visible if its bounding sphere is still inside the camera view. Outdoors, FPSBooster defers to the game's native background-culling pipeline when its culling grid is initialized; that pipeline can also run the game's separate depth-buffer occlusion tests when enabled and occluders are registered.
- The shadow-protection volume is approximate and applies only to active, loaded `LightType.Spot` instances. It is a sphere with a 0.75-yalm radius around each spotlight; furniture bounds are expanded so geometry close to the light is protected. Other furniture light types do not receive shadow protection.
- The **hide ALL furniture** test intentionally bypasses spotlight protection so users can verify that all furniture can be hidden temporarily.
- The **keep spotlight protection** test bypasses camera-view and nearby-player rules, but still honors enabled maximum-distance and maximum-height limits. Within those limits, it preserves spotlight-bearing furniture and furniture whose bounds come within 0.75 yalms of active, loaded spotlights.
- During normal culling, items still loading, non-model furniture, and unsupported direct-render objects are left alone.
- Turn the player-in-view safety check off when using a free camera; otherwise culling pauses if your character leaves the screen.
- The plugin restores furniture it hid when disabled or unloaded.

## Build

.NET 10 SDK with Dalamud dev hooks (`DALAMUD_HOME`), then `dotnet build -c Release`.


## Maximum distance and height limits

Maximum distance and maximum height difference are hard limits. They are checked before the nearby-player and spotlight-protection exceptions, so enabling either slider can make a furniture group eligible for culling even when it contains a spotlight or sits within 0.75 yalms of one. A spotlight only creates protection zones while its own furnishing remains within the enabled limits; a light on a furnishing beyond those limits does not protect other furniture. The press-and-hold **hide ALL furniture** test intentionally ignores these limits, while **keep spotlight protection** respects them.


## Character diagnostics (read-only)

- The **Players** tab reports live player objects, mounts, companions, and ornaments.
- The **NPCs** tab reports Battle NPCs, Event NPCs, and retainers.
- Each category shows object count, whether a `DrawObject` is present, whether its `CharacterBase` has a skeleton, and the number of model slots with non-null model pointers. A populated model slot does not guarantee all GPU resources are resident.
- The diagnostics show the four raw combinations of `DrawObject.Flags` bits `0x01` and `0x08`. The current `DrawObject.IsVisible` helper returns true when both bits are set, but FFXIVClientStructs explicitly says the purpose of these two bits is uncertain. FPSBooster therefore does not label those combinations as definitively visible or hidden.
- `GameObject.Visibility = 1` is the documented hidden value; `0` is shown. Other non-zero values are counted separately rather than assumed to mean the same thing.
- The world-distance tables use straight-line 3D distance between each object's world position and the local player's world position, grouped into 0–10, 10–20, 20–30, 30–50, and 50+ yalm ranges. This uses object origins (not model bounds), so it is intended for broad correlation rather than exact render-range measurements. The player distance table excludes the local player; the NPC table combines Battle NPCs, Event NPCs, and retainers. Each table also reports min/average/max of the game's own `CurrentDistance` byte, separately for objects with and without a `DrawObject`. FFXIVClientStructs documents `CurrentDistance` as distance to the local player, but these diagnostics display its raw values because the units are not specified. These measurements show correlation; they do not prove why the game did or did not create a `DrawObject`.
- The footer shows the game's current drawable-object limit. The limit is a budget for drawable candidates, not a count of objects hidden. Category counts come from Dalamud's live object table.
- Diagnostics refresh at most once per second and only while the Players or NPCs tab is open. They are read-only and do not modify character visibility.
