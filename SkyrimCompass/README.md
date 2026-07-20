# Skyrim Compass — Dalamud Plugin for FFXIV

Renders a **Skyrim-style** horizontal compass at the top of
your screen, aiming to replace and combine the minimap and various other hud elements into one while being aesthetic.

---

DALAMUD REPO (same URL for every plugin in this repo — see the [repo root](../) for the full list):
```
https://raw.githubusercontent.com/JTayGang/MeowyUtils/main/repo.json
``` 

---

## Features

| | |
|---|---|
| 🧭 | Scrolling N / NE / E / SE / S / SW / W / NW labels with degree tick marks |
| 🔴 | Enemy markers (with in-combat only option) |
| 🔵 | Players, Friends, Party members with role/class, **Can also use any vanilla icons!** |
| 🟢 | Gathering node markers (Mining / Botany) |
| 🟡 | Treasure coffer markers |
| 🩸 | Skyrim-style target health bar + name, docked right beneath the compass |
| 🎯 | Target-of-target tier underneath that — FF14's ToT, restyled, with a dedicated warning color if it's targeting YOU |
| ⚙️ | Fully configurable size, position, colours, visible arc, marker range, and more! |

---

## Requirements

- [XIVLauncher](https://goatcorp.github.io/) with Dalamud enabled (v14+)
- **.NET 10 SDK 10.0.101 or later** — [download here](https://dotnet.microsoft.com/download/dotnet/10.0)
- FFXIV installed and launched with Dalamud at least once

> **Note:** Dalamud v14+ requires .NET 10. If `dotnet --version` prints `8.x` or `9.x`
> you must install the .NET 10 SDK before building.

---

## Building from Source

### 1 — Install .NET 10 SDK

Download **SDK 10.0.101** (or newer) from:
<https://dotnet.microsoft.com/download/dotnet/10.0>

Verify after installing:
```cmd
dotnet --version   # should print 10.x.x
```

### 2 — Set the `DALAMUD_HOME` environment variable

Point it at the Dalamud *dev* directory (the folder that contains `Dalamud.dll`).
The default XIVLauncher path is:

```
%APPDATA%\XIVLauncher\addon\Hooks\dev
```

Set it permanently (run once in a command prompt):

```cmd
setx DALAMUD_HOME "%APPDATA%\XIVLauncher\addon\Hooks\dev"
```

Restart your terminal or IDE after setting it.

### 3 — Build

```sh
dotnet build -c Release
```

The `Dalamud.NET.Sdk` NuGet package is fetched automatically on first build — no
manual DLL references needed.

Output lands in `bin\Release\net10.0-windows\`.

### 4 — Load in Dalamud

**Option A — Dev plugin (recommended for testing)**

1. Open XIVLauncher → ⚙ Dalamud Settings → *Experimental* tab.
2. Add the full path to `bin\Release\net10.0-windows\` as a dev-plugin folder.
3. Re-open the plugin installer and enable **Skyrim Compass**.

**Option B — Manual copy**

Copy `SkyrimCompass.dll` and `SkyrimCompass.json` into your Dalamud
`devPlugins` folder (same *Experimental* tab lets you open it).

---

## In-Game Usage

| Command | Effect |
|---|---|
| `/compass` | Toggle compass on / off |
| `/compass config` | Open the settings window |

The settings window is also reachable from the Dalamud plugin list → ⚙ button.

---

## Configuration

Open with `/compass config`.

### Layout tab
- **Width / Height** — size of the compass bar in screen pixels
- **Y Offset** — distance from the top of the screen (slider range auto-scales to your screen height)
- **X Offset** — distance left (negative) or right (positive) of horizontal center (slider range auto-scales to your screen width)
- **Visible Degrees** — how wide a slice of the compass is shown (30°–180°)
- **Font Scale** — scale the N/NE/E… label text
- **Show numeric heading** — shows e.g. `045°` below the bar
- **Rotation Offset** — fudge factor; set to **180** if North and South appear swapped

### Colors tab
Individually tune background, border, cardinal/intercardinal labels, and tick marks.

### Markers tab
Toggle each entity category and choose its dot colour.  
**Max distance** controls how far out (in yalms) entities are detected.

### Combat tab
- **Target Health Bar** — Skyrim-style name + HP readout for your current target,
  docked directly beneath the compass so the two read as one HUD column. The bar
  itself is an upside-down trapezoid (wide at the top, narrower at the bottom,
  no end caps) spanning a little less than the full compass width. Fill colour
  follows an enemy/friendly-player/neutral-NPC scheme; background, border, and
  name text reuse the compass's own colours from the General tab.
  - **Width** — bar width as a fraction of the compass's own width
  - **Bar thickness / Name font scale**
  - **Show target level** — prefixes the name with `Lv90`-style text
  - **Show shield overlay** — light sheen over the shielded portion of the bar
    when your target has an active damage shield
  - **Show name ribbons** — two glowing ribbons (the Limit Break glow's own
    flowing technique, reused) linking the name's flanking ornaments up to the
    bar. They cross: the right ornament flows to the left side of the bar and
    the left ornament flows to the right side.
- **Target-of-target** — a smaller tier beneath the target bar showing who or
  what your target has itself targeted (FF14's ToT, restyled). Hidden
  automatically when that's nobody, or your target itself — both are noise, not
  information. If your target is targeting **you**, this tier swaps to a
  dedicated warning colour and shows your own HP instead, so aggro is
  impossible to miss out of the corner of your eye.

---

## Troubleshooting

**`System.Runtime` version mismatch error** → Install .NET SDK 10.0.101+.

**`ImGuiNET not found`** → Make sure you're using the current `.csproj` (`Dalamud.NET.Sdk/15.0.0`, using
the `Dalamud.Bindings.ImGui` namespace), not an old one with manual `<Reference>` tags or `using ImGuiNET;`.

**N and S are swapped** → Layout tab → set *Rotation Offset* to `180`.

**Compass doesn't appear** → Make sure the plugin is enabled (type `/compass`).

**Build error: cannot find Dalamud.dll** → Confirm `DALAMUD_HOME` is set correctly
and that XIVLauncher has been run at least once (so the `dev` folder is populated).

**API level mismatch** → If Dalamud has updated since this was written, bump
`Dalamud.NET.Sdk` to the latest version on NuGet and update `DalamudApiLevel`
in `SkyrimCompass.json` to match.

---

## How it works (briefly)

- `IClientState.LocalPlayer.Rotation` gives the character's facing angle in radians
  (0 = south, π = north in FFXIV's coordinate system).
  We convert this to a standard compass bearing (0 = N, 90 = E).
- `IObjectTable` supplies nearby entities; we compute each one's bearing from the
  player via `atan2(dx, −dz)` and place a coloured dot on the compass.
- Everything is drawn with `ImGui.GetForegroundDrawList()` so it sits above all
  other UI without needing a dedicated window.

---

## License

MIT — do whatever you like with it.
