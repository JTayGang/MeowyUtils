# FPSBooster

Dalamud plugin that cuts rendering work in FFXIV housing by hiding furniture you can't see.

## Use

- `/fpsbooster` opens settings.
- `/fpsbooster on` / `off` toggles culling. Off restores all furniture.
- `/fpsbooster test` opens settings and points to the hold-to-test buttons.

## Settings

| Setting | Default | Effect |
|---|---:|---|
| Enable furniture culling | On | Master switch. |
| Hide furniture outside the screen | On | Hides furniture outside the camera view. |
| Extra room around the screen | 12° | Keeps furniture just off screen visible to avoid pop-in. |
| Always show nearby furniture | 6 yalms | Keeps furniture close to the camera visible. |
| Maximum distance | Off | Hides furniture farther than this from the camera. |
| Maximum height difference | Off | Hides furniture this far above or below the camera (other floors). |
| Show all furniture in GPose | On | Pauses culling in GPose. |
| Pause if my character is off screen | On | Safety check. Turn off when using a free camera. |

Distances are measured from the camera. A value of 0 shows as Off.

## Good to know

- Indoors, each furnishing is hidden whole using its bounding sphere. Outdoors, the game's own culling is used when available and FPSBooster only adds your distance/height limits.
- Furniture with an active spotlight, and furniture within 0.75 yalms of one, stays visible so shadows don't disappear. Maximum distance/height override this.
- Culling is camera-view only, so furniture behind a wall stays visible while in view. Use the distance/height limits to cut other floors.
- The hold-to-test buttons on the Housing tab hide furniture only while held and are never saved.
- Anything hidden is restored when culling is off, paused, or the plugin unloads.

## Players / NPCs tabs

Read-only diagnostics, refreshed once a second while the tab is open. They change nothing.

- Players tab: players, mounts, minions, ornaments. NPCs tab: Battle NPCs, Event NPCs, retainers.
- Per category: object count, `DrawObject`s, skeletons, populated model slots, raw `DrawObject.Flags` bits (`0x01`/`0x08`) and `GameObject.Visibility` values.
- Distance tables group objects by 3D distance from your character (0–10, 10–20, 20–30, 30–50, 50+ yalms) and show the game's raw `CurrentDistance` byte. They use object origins, so treat them as rough correlation, not exact render ranges.
- The game's draw limit is an allowance, not a count of hidden objects. Flag bits and distances show state, not why the game hid or skipped an object.

## Build

.NET 10 SDK with Dalamud dev hooks (`DALAMUD_HOME`), then `dotnet build -c Release`.
