# Housing To Brio

A Dalamud plugin that reads a ReMakePlace-style housing layout file and saves
it as a **Brio Project**, so every piece of furniture shows up as a Brio
"Furniture" world object - letting you recreate someone's whole house for a
GPose photo without owning the house, the materials, or any of the furniture.

## Why this saves as a "Project" and not a portable file

Brio has two file-based ways to load a scene:

- **"Import Scene"** (a standalone file) - the button exists in Brio's code,
  but it's wrapped in a hardcoded disable with the tooltip **"Importing/
  Exporting disabled until 0.8.1"**. It isn't clickable in the current build,
  no matter what file you point it at. That's a decision in Brio itself, not
  something fixable from outside it - which is why this plugin no longer
  tries to produce that kind of file at all.
- **"Load Project"** - works today, but only lists entries from Brio's own
  project registry. There's no "browse for a file" option in it - Brio only
  ever adds to that list via its own "Save as new...", which captures your
  *current live* GPose scene, not a file.

So this plugin writes straight into Brio's own project folder and registers
the result in Brio's own project index, making it show up in the Load
Project window directly.

## Read this before you use it - a one-time gotcha

**Brio only reads its project list once, when it starts.** Its "New
Project" / "Save Project" / "Delete Project" actions never re-read the file
first - they just take whatever's currently in Brio's memory, apply the
change, and write the whole list back to disk. That means if you do any of
those three things in Brio *after* this plugin writes its entry but *before*
Brio has reloaded and picked it up, Brio will silently overwrite the file
with its stale in-memory list - erasing the entry this plugin just added,
even though the write itself worked perfectly.

This is exactly what happened during testing: a "test project" saved from
inside Brio wiped out an entry this plugin had already written, because Brio
never re-read the file in between.

**So, every time, in this exact order:**

1. Click **Save as Brio Project** in this plugin.
2. Reload Brio (Dalamud's Plugin Installer -> Installed Plugins -> Brio ->
   the reload icon) or fully relog.
3. Open Brio's **Load Project** window and load it.

Don't click New/Save/Delete Project in Brio between steps 1 and 3. The
in-app warning repeats this right above the save button.

## How it actually works

This plugin does not talk to Brio over IPC and does not link against Brio's
DLL - Brio's public IPC only covers spawning duplicate *character* actors,
posing, and a few other things, not furniture/world objects.

1. Parses the layout JSON (`interiorFurniture` / `exteriorFurniture`).
2. For each item's `itemId`, looks up the game's own `HousingFurniture` /
   `HousingYardObject` Excel sheets (via Dalamud/Lumina, at runtime) to find
   its model, and builds the `.sgb` asset path the same way the game does.
3. Converts the layout's stored transform into the game's coordinate
   convention, and optionally converts the stored dye hex color into an RGBA
   color.
4. Builds the result into Brio's own scene container format (a small binary
   layout: magic header + MessagePack-encoded chunks for a manifest and a
   list of world objects - confirmed by reading Brio's own
   `SceneService.Serialize()`/`Deserialize()`, which is the same code path
   both "Export/Import Scene" *and* "New/Load Project" use internally).
5. Finds Brio's own plugin config folder (guessed as the sibling folder
   `.../pluginConfigs/Brio` next to this plugin's own config folder - the
   standard Dalamud layout; there's a manual override field and a Browse
   button if auto-detection doesn't find it).
6. Makes a timestamped backup of Brio's existing project registry
   (`brio.data`) if one exists, and only proceeds if that registry parses
   with confidence - if it can't, nothing is written, and you're told to
   check things by hand instead.
7. Writes the scene as a new `.brioproj` file in Brio's `Data/Projects`
   folder, appends a matching entry to the registry, and immediately reads
   the registry back to confirm the new entry is actually there before
   reporting success - so a bug on this plugin's side shows up right away,
   distinct from Brio overwriting it later per the gotcha above.

Because this reaches into another plugin's own data folder, it's treated
carefully: backup-first, verify-after, and it refuses to touch anything it
can't parse confidently.

## Requirements to build

- A normal Dalamud plugin development setup (Visual Studio / `dotnet build`,
  XIVLauncher installed so the Dalamud dev SDK can be resolved).
- .NET SDK matching `net10.0-windows` (whatever your current Dalamud
  installation uses - check the `Dalamud.NET.SDK` version if `dotnet build`
  fails to resolve the SDK, and adjust the version in
  `HousingToBrio.csproj` to match).
- NuGet access to restore the single extra dependency: `MessagePack` 3.1.7.

```
dotnet build HousingToBrio.csproj -c Release
```

Then load it as a dev plugin the way you normally do.

## Project layout

Everything lives as flat files directly under `HousingToBrio/` (no
sub-folders), grouped by concern rather than by type:

- **`Plugin.cs`** - the Dalamud entry point (`IDalamudPlugin`) and the
  persisted `Configuration`.
- **`Layout.cs`** - the ReMakePlace-style layout JSON schema, its parser, and
  the current-interior house-size detector used for the size-mismatch check.
- **`LayoutToBrioConverter.cs`** - the actual conversion pipeline: resolving
  an item ID to its `.sgb` model path, then mapping a parsed layout's
  transforms/colors into Brio's DTOs. Check here first for anything
  position/rotation/color related.
- **`Brio.cs`** - everything specific to Brio's own on-disk formats and
  lifecycle: the scene/project DTOs, the binary scene writer, the project
  registry installer, and the disable/re-enable reload automation.
- **`MainWindow.cs`** - the ImGui window tying the above together.

## Using it

1. `/housingtobrio` opens the window.
2. Point it at a layout `.json` file and click **Load layout**. If the size
   the layout was saved from (Small/Medium/Large/Apartment) doesn't match the
   interior you're currently standing in, you'll see a heads-up - it's just
   informational, saving still goes ahead either way.
3. Choose whether to include interior/exterior furniture and whether to apply
   dye colors.
4. Under **Save as a Brio Project**, confirm the auto-detected Brio folder
   looks right (or Browse/Re-detect if not), give it a name, and optionally
   tick **Automatically reload Brio after saving** (see below).
5. Click **Save as Brio Project**, then follow whichever instructions are
   shown - automatic or manual - before opening Brio's **Load Project**
   window. Before you click **Load** there, see "Getting positions to line
   up with the room" below - it's a one-checkbox step in Brio itself and
   easy to miss.

Any items whose `itemId` isn't found in the current game's housing sheets are
skipped and listed after saving rather than silently dropped.

### House size check

The layout file records the size of house it was captured from ("Small",
"Medium", "Large", or "Apartment"). Once a layout is loaded, this plugin
compares that against whatever interior you're currently standing in (using
the same technique ReMakePlace's own code uses: decoding a size code out of
the current zone's internal name via Dalamud/Lumina - no memory reading
involved) and shows a heads-up if they differ. This never blocks saving -
some people load a layout from a different size house on purpose to see how
it looks, or to hand-adjust afterward in Brio - it's just there so a mismatch
isn't a surprise.

### Getting positions to line up with the room

Brio's own **Load Project** window has a gear icon next to its **Load**
button. That opens an "Import Options" popup with a **Positions** section
containing a **Relative Object Positions** checkbox - and it defaults to
**checked**.

With it checked, Brio places every object at *(wherever you're standing the
moment you click Load) + (the object's saved offset)* - so the whole layout
follows you around and only lines up with the room if you happen to be
standing exactly on the layout's own origin point. This is Brio's own
behavior (it's meant for recreating a relative arrangement around yourself
wherever you are, not for putting a house's furniture back in its exact
spots), and it's the single biggest cause of a layout looking like it spawned
"around the player" instead of in the room.

**Uncheck it before clicking Load.** With it off, Brio uses each object's
absolute saved position instead - the same coordinate space the game itself
uses for furniture inside any interior of a matching size/shape - so it lines
up correctly no matter where in the room you're standing. You don't need to
find any particular spot; anywhere inside a same-size-or-larger matching
interior works.

This is a Brio setting, not something this plugin can flip for you - Brio
doesn't expose an API for its own UI state, and this plugin doesn't talk to
Brio over IPC at all (see "How it actually works" above). If a layout still
looks offset after loading, this checkbox is the first thing to check.

### Automatic Brio reload

Ticking **Automatically reload Brio after saving** replaces the manual
"reload Brio yourself" step with `/xldisableplugin Brio` followed by
`/xlenableplugin Brio`, run automatically right after a successful save.

Dalamud queues these commands rather than acting on them instantly, so this
polls Brio's own `IsLoaded` state (via the public
`IDalamudPluginInterface.InstalledPlugins` list) after each step, waiting up
to 10 seconds before treating it as failed - it won't move on to re-enabling
until it's confirmed Brio actually disabled first. Progress and any failure
reason show up right under the Save button. The Save button is disabled
while this is running so a second click can't overlap with it.

This is opt-in and off by default. If Brio is set up in a way the commands
can't act on (for example, split across multiple plugin collections), it'll
report that rather than silently doing nothing - at which point reloading it
by hand works exactly as before.

## Troubleshooting

**Furniture spawns in a pile around my character, or the whole layout
shifts when I move before loading:** See "Getting positions to line up
with the room" above - uncheck **Relative Object Positions** in Brio's
Load Project import options (the gear icon next to its Load button).
This is a Brio import setting, and it defaults to on.

**Saved successfully, but it's still not in Brio's Load Project list:**
Almost certainly the ordering gotcha above - something touched Brio's
project list (New/Save/Delete) after this plugin wrote its entry but before
Brio reloaded. Fix: reload Brio right now, then click Save as Brio Project
again, then go straight to Load Project without doing anything else in Brio
first. If the plugin itself reported success ("Saved and verified..."), the
write was correct at the time it happened - so this is a session-timing
issue, not a data bug.

**The plugin itself reports the entry wasn't found immediately after
writing:** that does point to an actual bug (a mismatch between this
plugin's mirror of Brio's registry format and Brio's real one, most likely
because Brio changed that format in an update). Worth reporting with your
Brio version.

## Known limitations

- **Depends on Brio's on-disk format staying stable.** This is based on
  reading Brio's current source, not a documented/versioned public contract.
  A future Brio release could change its DTO shapes or registry layout.
- **The registry write is inherently a bit invasive**, even with the backup
  and verification. It's your own two locally-installed plugins, but it is
  still reaching into a file Brio considers its own.
- **Fixtures aren't converted.** Walls, flooring, roofs, doors, windows,
  fences, and lights don't have a placement transform in the layout format -
  they describe the house shell, not a prop you can spawn standalone.
- **"Material" swap-variant furniture isn't handled** - a small number of
  items store an alternate sub-item instead of a dye color; this plugin uses
  the base item's default look for those.

## Licensing note

Brio (<https://github.com/Etheirys/Brio>) is licensed under GPL-3.0. This
plugin doesn't reference or embed Brio's code - the DTO shapes in `Brio.cs`
are small, independently-written classes that match the *field layout* of
Brio's own DTOs purely for file-format compatibility, and the furniture
path-construction formula in `LayoutToBrioConverter.cs` documents an
asset-naming convention rather than reproducing Brio's implementation.
That said, licensing line-drawing isn't something to take a non-lawyer's word
for - if you plan to redistribute this plugin publicly, it's worth reading
Brio's GPL-3.0 terms yourself and deciding how you want to license your copy.

## Credits / references used while building this

- Brio - <https://github.com/Etheirys/Brio> (GPL-3.0) - source read to learn
  the scene container format, the `HousingFurniture`/`HousingYardObject` ->
  `.sgb` path formula, the custom-color/dye handling, and the Project
  registry format and its load/save timing. Also read
  `WorldObjectService.SpawnFromDTO()` and `ProjectWindow.cs`/
  `FileUIHelpers.cs` to confirm exactly how and why the **Relative Object
  Positions** import option (see "Getting positions to line up with the
  room" above) overrides an object's absolute position with one anchored to
  the player.
- ReMakePlace plugin - <https://github.com/RemakePlace/plugin> - source read
  to confirm the housing layout JSON schema, coordinate/unit conversion, dye
  hex-color format, and the TerritoryType-name technique used to detect the
  current house size. Also read `RotationToQuat()`/`ConvertToHousingItem()`/
  `ComputeZAngle()` in `SaveLayoutManager.cs` to confirm the layout's rotation
  quaternion is stored in the same Y/Z-swapped frame as its location, and
  round-tripped a known transform end-to-end against both to check the
  correct sign/axis conversion.
- Dalamud - <https://github.com/goatcorp/Dalamud> - source read to confirm
  the exact behavior of `/xldisableplugin`/`/xlenableplugin` (queued, not
  instant) and the public `InstalledPlugins`/`IExposedPlugin.IsLoaded` API
  used to detect when each step actually completes.
