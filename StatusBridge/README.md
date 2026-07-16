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
  natively. "Force-clear stuck pairs" next to it is the more aggressive version - it also removes
  the native original for anything currently tracked as a pair, which is what you actually want
  if the plain button seems to "toggle" (clears, then comes right back a moment later) - see
  **Known issues** for why that happens and why the two buttons are deliberately separate.
- If the settings window shows a count of statuses stuck because they're locked in Loci, that's
  not this plugin failing silently - it's Loci correctly refusing a removal that doesn't supply
  the matching lock key, which this bridge never has for a lock it didn't create itself. Unlock
  it in Loci's own UI.
- Turn on "Verbose logging" in settings and watch `/xllog` if you want to confirm it's actually
  doing something.

## Verifying it's actually working

Layer these roughly in order - each one only matters if the layer before it checks out.

**1. Plugin loads and detects both backends.** `/statusbridge` should show both Moodles and Loci
as "detected" in green. If either shows red, nothing downstream will work - check `/xllog` for
load errors first.

**2. Core mirroring, both directions.** With Verbose logging on: apply a Loci-only status to
yourself, confirm `/xllog` shows a "Mirrored Loci status '...' -> Moodles" line, then confirm it
actually shows up in Moodles' own status list (not just the log line - open Moodles' UI or hover
your own status bar). Repeat the other direction with a Moodles-only status. Then remove the
original and confirm the mirrored copy disappears rather than being orphaned. If this layer
doesn't work, check the Moodles setting called out under Requirements above before anything else
- it's the single most common reason the Loci -> Moodles direction silently does nothing.

**3. The experimental patch, mechanically.** It's on by default (Settings -> Experimental) -
existing installs get migrated to this automatically the first time they load after updating,
including anyone who'd previously opted out on purpose (see **Known issues** below for exactly
what that migration does and why). Check the "Patch status" line: it should read `Applied
(confirmed: NewMethod = false)` - that specific wording means the reflection write was read back
and confirmed, not just that the call didn't throw. Anything else (an assembly/type/field-not-found
message) tells you exactly which step failed, which is itself useful if a Moodles update ever
breaks this.

**4. The experimental patch, behaviorally.** This is the layer that actually confirms the visual
bug is fixed, and it's the hardest to test on demand because the trigger condition - Moodles'
render hook running after Loci's for a given character - depends on session-specific hook
ordering between the two plugins that isn't something you can force from outside either plugin.
Two ways to get a real answer:

- Use the "Compare counts for current target" button right below the patch toggle. Target a
  character who has both a real game buff/debuff *and* a Loci status showing at the same time,
  and click it. It reads Moodles' own (potentially miscounted) scan result and the real vanilla
  count from Dalamud's API side by side for that exact target. If they differ, the bug's
  precondition is active for that target on your client right now - toggle the patch off and on
  and click compare again; the mismatch should track the toggle. If they always match no matter
  who you target, either you haven't found a character exhibiting it, or your client's hook
  ordering happens to be the lucky one this session and there's nothing for the patch to fix
  locally regardless of whether it's enabled.
- The most reliable confirmation is going back to whoever originally reported the ghost icons
  (or anyone else who's reproduced it reliably) and having them enable the patch specifically.
  Their setup is a confirmed-reproducing environment; yours may not be, and "it looks fine on my
  client" either way isn't strong evidence unless you already know which case you're in via the
  compare tool above.

## Known issues

**Statuses that are hoverable (tooltip shows) but visually invisible on a character.** This
happens on characters that have both a Moodles icon and a Loci icon showing at the same time,
and it's not actually a StatusBridge bug - it's a pre-existing Moodles/Loci coexistence gap that
StatusBridge's whole purpose (making both backends agree) makes much more likely to surface,
because it multiplies the number of characters that end up with icons from *both* systems
simultaneously.

The actual mechanism, confirmed against both plugins' source: Moodles decides where to draw its
own icons by scanning the shared native status-icon UI region and counting populated slots,
assuming everything visible there is a vanilla game status
(`Moodles/GameGuiProcessors/TargetInfoProcessor.cs` and four sibling processor files, all gated
behind a field called `CommonProcessor.NewMethod`). If Loci's icons are already rendered into
that same region by the time Moodles scans - which depends on session-specific hook/render
ordering between the two plugins, not something either plugin's user can control - Moodles counts
Loci's icons as vanilla too, throwing off where it positions its own. Loci does not have this
problem: it has a dedicated watcher (`Loci/Services/MoodlesWatcher.cs`) that reads Moodles' icon
count via IPC and correctly offsets around it regardless of render order. Moodles has no
equivalent awareness of Loci anywhere in its code. The bug is therefore asymmetric: a
Moodles-origin icon mirrored into Loci is safe; a Loci-origin icon mirrored into Moodles is the
one exposed to this.

Moodles' own code already contains a fix for this - `CommonProcessor.NewMethod`, when `false`,
computes the offset from Dalamud's own `IPlayerCharacter.StatusList` (the real, authoritative
status count) instead of visually scanning shared UI, which sidesteps the problem entirely. It's
just hardcoded to `true`. This has been raised with Moodles' maintainer; at time of writing they
aren't reviewing outside changes, so the fix isn't available upstream yet.

Two things you can do about it:

- **Disable "Mirror Loci -> Moodles"** in settings. This doesn't fix the underlying gap (someone
  who natively applies statuses in both plugins on the same character can still hit it), but it
  stops StatusBridge from actively creating new instances of the trigger condition.
- **The experimental offset patch** (Settings -> Experimental) is on by default as of config
  Version 2 - after running opt-in for a while with consistently good tester feedback, it's now
  applied automatically instead of something you have to go turn on. It makes StatusBridge reach
  into Moodles' running instance via reflection and flip `NewMethod` to `false` itself, using the
  correct path Moodles already has but doesn't use by default. It's still clearly marked
  Experimental in the UI, and still worth being able to turn back off, because it touches
  Moodles' internal, unversioned implementation details with no compatibility contract - it will
  stop doing anything (safely - it fails soft and reports why) the moment a future Moodles update
  restructures the relevant fields, and it changes Moodles' icon placement globally rather than
  just for the Loci-overlap case, for reasons we can't fully verify without Moodles' own design
  history. Concretely: if icon positions look off on party members, your focus target, or your
  own status bar after updating, try unchecking it before assuming something else is wrong. See
  `Experimental/MoodlesOffsetPatch.cs` for the full writeup and exactly what it touches. The same
  folder also has `MoodlesOffsetDiagnostics.cs`, a read-only tool (exposed as a "Compare counts
  for current target" button in the same Experimental settings section) that reads Moodles' own
  miscounted scan result and the real vanilla status count side by side for whatever you're
  targeting - if they differ, the bug is actively happening for that target right now, which is
  a much more direct check than judging by icon position.
  
  Existing installs updating from before this change get migrated automatically to on
  (`Configuration.Version` 1 -> 2, handled once in `Plugin.cs` on load) - and that includes anyone
  who had *already* explicitly turned it off themselves. A plain on/off setting can't tell "never
  touched this" apart from "deliberately opted out" once both are saved as `false`, so the
  migration can't be selective about it: if you're in the latter group, you'll need to
  re-disable it once after updating.

**Statuses that briefly appear then vanish from the buff bar right after a game restart, and can
leave a permanently "stuck" ghost that only clears via manual right-click or repeated
interaction.** Predates the experimental patch entirely - unrelated to `NewMethod`. Two separate
things stack here: one on Moodles' side that isn't really fixable from over here, and one that
was a genuine StatusBridge bug.

The Moodles-side half, confirmed against source: removing a status (via IPC or in-game) doesn't
delete it from `MyStatusManager.Statuses` immediately - `Cancel()` just sets `ExpiresAt = 0`, and
the actual removal from the list happens whenever `CommonProcessor.Tick()` next notices it's
expired. If something reapplies a full status snapshot to your own character before that `Tick()`
pass has caught up - most plausibly your sync plugin restoring your own last-known moodle state
on login, since moodles are entirely client-local and don't otherwise survive a relog - the
reapplied snapshot can still include statuses that were mid-removal when it was captured, which
briefly renders them again until Moodles prunes them a moment later. That same reapplication path
(`SetStatusManagerByPlayerV2` and siblings, landing in `MyStatusManager.Apply`/
`SetStatusesAsEphemeral`) also flags the character's status manager `Ephemeral` for as long as
it's active, which - confirmed from source, every IPC-based add/remove/preset method checks it -
silently blocks all of them on that character with no error and no log line, until something
explicitly clears it. Native right-click removal isn't gated by this at all (`Tick()`'s
click-off handling doesn't check `Ephemeral`), which is exactly why it always worked as a manual
fix even when nothing else did.

The StatusBridge-side half, and the actual bug: both mirror-cleanup loops in `BridgeEngine.cs`,
and `ClearAllMirrors()`, stopped tracking a GUID as soon as a removal was *attempted*, regardless
of whether it actually took effect - and Moodles' remove call gives zero feedback either way
(it's a `void` IPC method), so "the call didn't throw" was never a meaningful success signal. If
a removal landed during the window described above, StatusBridge would forget about a status
that was still very much alive, and the next reconcile pass - with no memory it was ever a mirror
- would treat it as newly native and mishandle it again. This is specifically what made "Clear all
mirrored statuses" occasionally *add* statuses back instead of removing them: it cleared the
tracking dictionaries unconditionally, and the very next reconcile against the still-real
leftover looked identical to a brand new status that needed mirroring.

Fixed: all three spots now only stop tracking a GUID once a fresh poll actually confirms it's
gone, instead of assuming a same-tick attempt succeeded. A removal that's blocked or delayed just
gets retried next tick instead of being silently forgotten.

**Second follow-up: the previous two fixes were still treating a symptom of the actual root
cause.** Both `_mirroredIntoLoci`/`_mirroredIntoMoodles` were in-memory only, meaning every game
restart started identity-tracking completely from scratch, with nothing to go on except whether
both sides still happen to agree ("adopt" - see `BridgeEngine`'s class remarks) - which silently
breaks the moment only one side survives a restart correctly, which is exactly what the Ephemeral
window above can cause. Every ghost-related bug found across this whole section traces back to
that one gap. Fixed properly this time: tracking state is now persisted (`Bridge/BridgeState.cs`,
a small JSON file in the plugin's own config directory, loaded on startup and saved whenever it
changes) instead of rebuilt by guesswork every session. The GUID-matching adopt logic is still
there, but now purely as a fallback for a missing or corrupted state file, not the primary
mechanism.

Two smaller, related fixes landed alongside it, from checking both plugins more thoroughly per
your ask:

- **Loci lock handling.** Confirmed from source: Loci's `Cancel`/`AddOrUpdate` reject a status
  locked with a non-zero key unless the caller supplies that exact key, and every mirror this
  bridge creates always uses key=0 ("don't lock it"). That means a mirror that's been locked by
  *anything else* (manually, a preset, an automation) can never be removed by this plugin's
  normal calls - it would previously retry forever, silently, every tick. `LociIpc.TryApply`/
  `TryRemove` now return the real `LociApiEc` instead of collapsing it to a bool, so
  `BridgeEngine` can tell `ItemLocked` apart from every other outcome, log it once instead of
  spamming, and surface a count in the settings window telling you exactly why something looks
  stuck instead of leaving you to guess. "Force-clear stuck pairs" can still fail against a real
  lock for the same reason - unlocking it in Loci's own UI is the only real fix for that specific
  case.
- **Reading the real expiry instead of the lossy IPC one.** Confirmed from source on both sides:
  `MyStatus.ToStatusTuple()` (Moodles) and `LociStatus.ToTuple()` (Loci) both set the tuple's
  `ExpireTicks` from a *static configured duration*, never from the live `ExpiresAt` timestamp
  that actually drives removal - so the public IPC surface cannot distinguish "about to be
  pruned" from "perfectly healthy" even in principle, on either side. `Experimental/
  MoodlesLiveStateReader.cs` and `Experimental/LociLiveStateReader.cs` read the real, live
  `ExpiresAt` directly via reflection (all public fields, no unsafe pointers - see each file's
  remarks for the exact path) so `BridgeEngine` can exclude anything already Cancel()-marked
  before ever treating it as "native, needs mirroring." This means reacting to your actual
  clearing intent the moment it happens rather than waiting however many ticks Moodles' or Loci's
  own next prune pass takes, instead of just cleaning up the resulting mess afterward. The
  Moodles reader also exposes `Ephemeral` itself for the settings window, read-only, purely as a
  diagnostic - confirmed from source that Loci's equivalent concept (`EphemeralHosts`) does NOT
  actually gate the calls this bridge makes on your own character (only the explicit lock does),
  so there's no matching "is this silently blocked" question to answer on that side. Both readers
  fail soft exactly like `MoodlesOffsetPatch` - any missing type or field just means the filter
  doesn't apply that tick, never a crash.

Deliberately **not** done: forcing Moodles' `Ephemeral` flag closed via the same reflection path.
It's just as writable as it is readable, but flipping it means overriding whatever set it -
plausibly your sync plugin mid-restore - which is a meaningfully different kind of decision than
reading it for a diagnostic, and one worth making deliberately (e.g. a manual button, if a locked
mirror on the Moodles side turns out to be a recurring problem) rather than something to do
silently on your behalf every tick.

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
  Configuration.cs          persisted user-facing settings
  Interop/
    MoodlesTypes.cs         local tuple/enum mirror for Moodles' IPC shape
    LociTypes.cs             local tuple/enum mirror for Loci's IPC shape
    MoodlesIpc.cs            thin wrapper around Moodles' IPC calls
    LociIpc.cs                thin wrapper around Loci's IPC calls
  Bridge/
    StatusConverter.cs       field mapping between the two tuple shapes
    BridgeState.cs            persisted mirror-tracking state, see "Known issues" below
    BridgeEngine.cs           the actual mirroring/de-dup logic
  Windows/
    ConfigWindow.cs           settings UI
  Experimental/
    MoodlesOffsetPatch.cs         on-by-default reflection patch, see "Known issues" above
    MoodlesOffsetDiagnostics.cs   read-only compare tool, see "Verifying it's actually working"
    MoodlesLiveStateReader.cs     read-only reflection, see "Known issues" above
    LociLiveStateReader.cs         read-only reflection, see "Known issues" above
```

No dependency on Moodles' or Loci's own assemblies (see the comment at the top of
`Interop/MoodlesTypes.cs` and `Interop/LociTypes.cs` for why, and why that's actually safe) -
the `.csproj` still references only the Dalamud SDK. Four files reach into unversioned internals
via reflection instead of talking only to the official IPC surface - everything under
`Experimental/`, all isolated there for exactly that reason. `BridgeState.cs` uses
`Newtonsoft.Json` directly (no explicit PackageReference - it's resolved transitively through the
Dalamud SDK, same as every other Dalamud plugin that persists JSON without adding it themselves).
If a build ever reports `Newtonsoft.Json` as missing, that's the one new assumption this
introduced worth checking first.
