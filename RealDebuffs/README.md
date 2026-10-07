# Real Debuffs

## What is this?

Real Debuffs is a plugin for Final Fantasy XIV (via Dalamud) that turns your debuffs into
actual things on your screen.

When you get Blinded, your screen goes dark. When you're Burning, fire licks up from the
bottom of the screen and smoke drifts across it. When you're Bound, ropes fly in and pull
taut around you. Every debuff has its own look, they layer on top of each other, and they
fade in and out smoothly instead of popping on.

You can also make your own versions. If you use Moodles or Loci, you can write a status
description like *"white chains made of snow"* and the game will show you white chains that
shed snowflakes. No settings menu required — just text in the status.

Commands:
- `/realdebuffs` — open the settings window.
- `/realdebuffs toggle` — turn every effect on or off.
- `/realdebuffs statuses` — log your current statuses (including custom Moodles/Loci ones)
  to the Dalamud log, for troubleshooting.

---

## Features

- **Per-debuff effects** covering ~28 debuff families, from a red edge outline for
  Vulnerability Up to layered fire for Burns and drifting fog for Frostbite.
- **Master intensity slider** and per-effect toggles, so you can dial things back or shut
  off individual effects you don't care about.
- **Hide during cutscenes** and automatic suppression while the game UI is hidden or you're
  in GPose.
- **Silence can actually block chat** (optional, off by default) via a game hook, rather
  than only showing the visual effect.
- **Moodles and Loci support** — show an effect while a custom status of yours is active,
  by matching its title.
- **Tooltip keyword scanning** — read a status's description and show effects based on the
  words in it. `"burning"` triggers Burns; `"frost"` triggers Frostbite; and so on.
- **"Made of X" material swaps** — a description like *"bound with chains"* renders Bind's
  ropes as chains. A description like *"red flame"* tints the Burns effect red.
- **Effect generator panel** in settings — paste a description to see what it triggers, and
  swap any effect's materials or color by hand.

---

## Technical overview

### How effects are organized

Every visual effect is an `ISceneEffect`. Effects do not draw directly — they push
primitives into an `EffectScene`, and the framework renders the whole frame at once after
every effect has had its turn. That's what makes layering, opacity and vignette priority
work across effects without any of them knowing about the others.

`EffectDiscovery` finds every `ISceneEffect` in the assembly at plugin load (public,
non-abstract, parameterless constructor), sorted by `DrawOrder`. Adding a new effect means
writing one class; nothing else in the project needs to change.

Every frame, `EffectManager`:

1. Reads the local player's status list and maps each status ID to a `DebuffKind` via
   `StatusCatalog` (which is built at startup by matching the English Status sheet against
   each effect's declared `TriggerStatuses`).
2. Adds any kinds that a Moodles/Loci rule or tooltip keyword match has asked for.
3. Steps every effect's fade toward its target, skipping effects that have thrown.
4. Calls `Emit` on each active effect.
5. Hands the filled scene to `EffectSceneRenderer`, which draws vignette → regions →
   strokes → particles in that order.

### Primitives

Effects emit four kinds of primitive into the scene:

- **`StrokePrimitive`** — a strand along a `StrandPath` (a polyline + arc-length table).
  Ropes, chains, tendrils, any effect that owns a "shape" and hands it to a material to
  draw.
- **`ParticlePrimitive`** — a single free particle with position, velocity, age, size and
  a `PrimitiveRole` that decides which material draws it.
- **`RegionPrimitive`** — a screen-space rectangle with an optional edge mask. Flat fills,
  edge glows, firelight, screen tints.
- **`ImpactPrimitive`** — a one-frame event (not drawn). "Something just hit something":
  the framework asks the owner's stroke material what it throws, so a chain shows sparks
  and rust, a rope shows dust and fibres, without either effect knowing which it is.

### Materials

Materials are the "what does this look like" half of the system. Three families live in
`MaterialRegistry`:

- **`IStrokeMaterial`** — draws a stroke. May also declare `Emissions` (things it sheds
  along its length, like rust flakes) and `ImpactEmissions` (things it throws on impact).
- **`IParticleMaterial`** — draws one particle. May also declare `Emissions` for when it's
  used as a stroke emitter.
- **`IRegionMaterial`** — draws one region.

Materials are looked up by name, not by enum. Config stores them as strings, so nothing
breaks when a new material is added.

### The "made of X" and color override system

Status descriptions go through `TooltipKeywordParser`, which:

- Matches each enabled `TooltipKeywordRule`'s keywords as whole words inside the tooltip.
- Resolves colors from `[color=...]` tags or from a color word in the same clause.
- Resolves `"made of X"` phrases per clause, so a description naming several effects
  attaches each clause's own material to the match(es) in that clause.
- Skips keywords inside a material phrase, so `"flames"` in `"made of flames"` modifies
  rather than activates.

The result is a `TooltipEffectMatch` per kind, which `EffectManager` turns into a color
override and (via `CustomStatusSnapshot.TooltipMaterialOverrides`) a material override for
the frame.

The same parser drives the Effect generator panel's tester, so what you see in the panel is
what a real Moodle with that description will do.

### Override resolution

For each primitive, `EffectSceneRenderer` resolves its material in this order:

1. User override (settings panel, or a tooltip "made of X" phrase).
2. The effect's declared default (`EffectRegistry`, populated from each effect's
   `SwappableSlot` list at load).
3. A universal fallback (a plain stroke, a spark, a flat fill), so an unregistered material
   name can never crash the renderer.

The resolution cache keys on the override dictionary's instance. `EffectManager` publishes
a fresh dictionary whenever overrides change, which clears the cache.

### Emission

`StrokeAutoEmitter` walks every stroke in the scene each frame and spawns whatever the
stroke's material (or its emit-axis override) declares. Two kinds of emission:

- **Free-flying** — a particle with a spawn position, a velocity, gravity, a lifespan and
  a size range. Position is sampled along the stroke's arc length.
- **Flow** — a particle that walks along the strand, wobbling laterally and slowing down
  at material-declared "obstacles", like a drip catching on each sucker of a tendril.

The stroke carries two optional windows, `EmitEdgeReach` and `EmitEndReach`, that confine
emission to near the screen edges or the strand's ends. Heavy uses these to keep its
rust and sparks around the anchor points. When a stroke's material has been swapped away
from the effect's declared default, `StrokeAutoEmitter` drops both windows so the
swapped-in material sheds along the entire strand — a "made of snow" chain covers its
whole length, not just the ends.

### Chat blocking

Optional. `ChatBlocker` hooks the game's post-Enter chat-input processor and swallows
outgoing messages while you're silenced. The signature can stop resolving after a game
patch; it fails safe by logging once and doing nothing, so the visual effect is unaffected
either way.

---

## Building

`RealDebuffs.csproj` uses `Dalamud.NET.Sdk`, which resolves Dalamud, ImGui,
FFXIVClientStructs and Lumina references automatically from your local Dalamud dev install
(`DALAMUD_HOME`, normally `%APPDATA%\XIVLauncher\addon\Hooks\dev`).

dotnet build -c Release


No explicit `<TargetFramework>` is set on purpose: the SDK injects whatever TFM the
currently-installed Dalamud build targets, so the project keeps building when Dalamud
moves to a newer .NET.

---

## Adding things

- **A new effect**: write a class implementing `ISceneEffect`. Declare `Kind`,
  `DisplayName`, `Description`, `DrawOrder` and `TriggerStatuses`; optionally implement
  `IHasHeroSlots` and `IHasSwappableSlots` to expose swappable materials and participate
  in "made of X" phrases. `EffectDiscovery` picks it up on next load.
- **A new material**: write a class implementing `IStrokeMaterial`, `IParticleMaterial` or
  `IRegionMaterial`, and add it to the appropriate list in `MaterialRegistry`'s static
  constructor. Give it a `NaturalLanguageWords` array if it should be selectable via a
  `"made of X"` phrase.
- **A new `DebuffKind`**: add it at the END of the enum (numeric values are serialized),
  then write an effect that declares it.
