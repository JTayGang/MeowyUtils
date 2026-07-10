# StatusBridge

A Dalamud plugin that mirrors your own live [Moodles](https://github.com/kawaii/Moodles) and
[Loci](https://github.com/CordeliaMist/Loci) statuses onto each other, so whichever sync plugin
you use picks up a unified status regardless of which backend it natively reads.

- Have a Loci status applied, but your partner's sync (e.g. Snowcloak Sync) only reads Moodles?
  StatusBridge copies it into Moodles too, so Snowcloak picks it up.
- Have a Moodles status, but you're on a sync that reads Loci (e.g. Sundouleia)? StatusBridge
  copies it into Loci.
- Have both installed and a sync that reads both? StatusBridge won't create a disconnected
  duplicate - see **How it avoids duplicates** below.

## How it works

StatusBridge never talks to Snowcloak, Sundouleia, or any other sync plugin directly, and it
doesn't need to. Sync plugins already work by reading your local Moodles and/or Loci state and
broadcasting it to your pairs; on the receiving end they apply incoming data back into your
pair's local Moodles and/or Loci. StatusBridge's whole job is to make sure both of *your own*
local backends agree with each other. Whatever sync tool you're running keeps working exactly as
it always has, reading from whichever backend it supports - it'll just also see the mirrored copy
of whatever you created in the *other* one.

## How it avoids duplicates

A mirrored status keeps the **exact same GUID** in both systems. This does three things at once:

1. Lets the bridge tell "a status I mirrored" apart from "a status you created natively" (native
   ones get mirrored out; mirrors themselves don't get re-mirrored back).
2. Prevents feedback loops - mirroring a Moodle into Loci can't turn back around and get mirrored
   from Loci back into Moodles as if it were new.
3. Means a sync plugin that supports both backends and happens to key by GUID sees one status,
   not two.

There's also an optional "skip if a same-named status already exists on the destination" setting
(off by default) for the case where you made two similar statuses independently in both plugins
before ever installing this bridge.

## Requirements

- [Dalamud](https://github.com/goatcorp/Dalamud) / XIVLauncher, with Moodles and Loci both
  installed (the bridge is only useful if both are present - it no-ops otherwise).
- **In Moodles' own settings, enable "Allow other plugins apply Moodles."** This is off by
  default. Moodles gates any externally-supplied status data behind this setting (or being in
  your party/friends list), and there's no way for another plugin to detect or override it - if
  it's off, the Loci -> Moodles direction will silently do nothing. Loci has no equivalent
  setting; the Moodles -> Loci direction works out of the box.
- A recent .NET SDK to build. Whatever version Dalamud itself currently targets is required -
  `Dalamud.NET.Sdk` selects this automatically, so if `dotnet build` reports a `CS1705` assembly
  version error, the fix is a newer .NET SDK, not a change to the project file (neither
  Moodles' nor Loci's own `.csproj` hardcodes a `TargetFramework` for exactly this reason, and
  this project now follows the same approach).

## Building

```
dotnet build StatusBridge.sln -c Release
```

This is a personal/unpublished plugin, so it isn't going through Dalamud's official plugin
repository - you'll load it as a dev plugin:

1. In-game, run `/xlsettings` -> **Experimental** -> **Dev Plugin Locations** -> add the folder
   `StatusBridge\bin\Release` (the folder containing the built `StatusBridge.dll` and
   `StatusBridge.json`).
2. Open the plugin installer (`/xlplugins`) -> **Dev Tools** tab -> **StatusBridge** -> Load.
3. After any rebuild, use the reload button in the same tab (or `/xlplugins` -> Dev Tools ->
   StatusBridge -> Reload) to pick up the new DLL.

## Usage

- `/statusbridge` opens the settings window, which also shows whether Moodles/Loci are currently
  detected and how many statuses are currently mirrored in each direction.
- Everything runs automatically in the background once both plugins are detected - there's
  nothing to trigger manually day-to-day.
- "Remove all mirrored statuses" in the settings window is a manual escape hatch that clears
  every status the bridge itself created, on both sides, without touching anything you made
  natively.
- Turn on "Verbose logging" in settings and watch `/xllog` if you want to confirm it's actually
  doing something.

## Known limitations

- **Chain-trigger data isn't mirrored.** Both plugins let a status trigger another status (on
  dispel, on max stacks, etc.) by referencing that other status's GUID in your saved library.
  That GUID is only meaningful inside the plugin that owns it, so chain relationships are
  stripped on every mirror rather than copied somewhere they'd point at nothing.
- **Moodles' "Sticky" (Permanent) flag has no Loci equivalent.** Loci only has the "no expiry
  timer" concept (which *is* mirrored). Mirroring Loci -> Moodles always produces a non-sticky
  result.
- **Reapplying a status resets its remaining duration** in the destination plugin (this is how
  both plugins' own "apply" functions work, not something StatusBridge adds) - so a status will
  briefly appear to refresh to full duration right when it's first mirrored, and again on plugin
  reload for any pair that predates the reload. Steady-state mirroring afterwards only pushes
  updates when something actually changed, so this isn't an ongoing issue.
- Only your own local player's statuses are mirrored - not other players', which sync plugins
  handle on their own via the exact same backend-native mechanism they always have.

## Project layout

```
StatusBridge/
  Plugin.cs                 entry point
  Svc.cs                    Dalamud service locator
  Configuration.cs          persisted settings
  Interop/
    MoodlesTypes.cs         local tuple/enum mirror for Moodles' IPC shape
    LociTypes.cs             local tuple/enum mirror for Loci's IPC shape
    MoodlesIpc.cs            thin wrapper around Moodles' IPC calls
    LociIpc.cs                thin wrapper around Loci's IPC calls
  Bridge/
    StatusConverter.cs       field mapping between the two tuple shapes
    BridgeEngine.cs           the actual mirroring/de-dup logic
  Windows/
    ConfigWindow.cs           settings UI
```

No dependency on Moodles' or Loci's own assemblies (see the comment at the top of
`Interop/MoodlesTypes.cs` and `Interop/LociTypes.cs` for why, and why that's actually safe) -
just the Dalamud SDK.
