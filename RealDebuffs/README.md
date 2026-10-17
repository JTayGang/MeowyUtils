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

**Viscous liquids** (slime now; blood, sludge, venom later) share one system instead of
each reinventing drips. A stroke material opts in by declaring an emission with a `Drip`
block (`StrokeDripOptions`). `GoopEmitter` then grows drops on the strand's underside: a
drop swells, stretches into a neck, pinches off and falls, and the string snaps back to the
surface, sometimes leaving a satellite droplet. It hands the scene ordinary particles whose
optional thread (`Tether` / `Anchor` / `ThreadEnd` on `ParticlePrimitive`) the particle
material draws. `ParticleGoop` draws them as lit geometry, shaded by a `LiquidSpec`, so a
liquid is a colour/gloss/translucency preset plus a few lines naming it. Sites hang off the
strand's real surface, because a material can override `IStrokeMaterial.RadiusAt`.
Liquid is drawn right after the stroke it hangs from (`ParticlePrimitive.HasHost`/`HostSeed`, honoured when the particle material returns `DrawsWithHost`), so a nearer stroke drawn later covers a farther one's slime; every other particle is still drawn over all strokes.

**Layered light (2.5D)** — Silence's seal is built from reusable parts so the next curse,
charm or hex doesn't start from scratch. `LayerCamera` stacks flat layers at different
heights behind one tilted perspective camera; slowly precessing the tilt makes the layers
slide over one another and the near side of every ring grow, which is all the depth the
screen needs. `SigilGeometry` fills `StrandPath`s with rings and polygons on those layers,
and `RuneGlyphs` is a small hand-drawn rune alphabet. `stroke.sigil` and `stroke.glow` draw
lines of light (honouring `Reveal`, `Dash`, `Depth` and `Agitation`, with pulses running
along them); `particle.rune` draws a glyph (it reads `ParticlePrimitive.Rotation`);
`particle.mote` draws soft arcane light, going out of focus when large. `CurseColor` is the
palette. A closed ring that may be swapped for a chain should set its `WidthHint` from its
own length, so the link count stays fixed however the ring zooms or tilts (see
`SilenceEffect`).

**Themed light** — the light materials aren't tied to the violet curse palette. `StrokeGlow`
and `ParticleMote` take a `Palette` (and a name and vocabulary) in an internal constructor,
and `MaterialRegistry` registers one per theme: `stroke.glow` / `particle.mote` are
`CurseColor`, `stroke.corona` / `particle.static` are `StormColor`. A new theme is a palette
in `Palette.cs` plus one line each in the registry. The default-constructed instances
reproduce the original violet output exactly.

**Electricity** — Paralysis is built from parts any shock, stun or lightning effect can
reuse. `Bolt` holds the shape language (a large-scale meander, a drifting wander, a mid kink
and a fast crackle as lattice noise, plus bent twigs for forks); it is stateless, so a bolt
re-rolls its fine shape without allocating. `stroke.lightning` turns *any* smooth strand into
a live bolt and is drawn the way a camera sees one: a blown-out white core that thickens as
the light overexposes, a pale-yellow body, flared hot spots where the channel kinks hard, a
smooth falloff built from several stacked widening layers (that fades into open ends rather
than stopping flat), and two-level forks. It honours `Reveal` (a dim leader racing along the
strand), the new `SurgeAt`/`SurgeGain` stroke hints (a return stroke: a blinding run of
current along the channel), `Dash`, `Depth` and `Agitation`. Because the effect owns a smooth
path and the material owns the jaggedness, the same strand can be swapped to a chain, rope or
tentacle and keep its anchors and twitch, and the lightning can be swapped onto Heavy's chains
or Bind's ropes. `particle.arc` is a small crackle that re-rolls about sixteen times a second
(a cheap zigzag when small, branching when large) and declares `Emissions`, so it crawls along
any stroke or replaces a flower or ember slot. `particle.flare` is a soft white-hot burst with
an exponential-looking falloff, for where a discharge touches down. `SigilGeometry.Span`
builds an open arc between two anchors on a layer (a bow plus a wobble pinned at both ends).
`StormColor` is the palette, and `particle.thunderhead` is a `ParticlePuff` look for a dark
cloud deck that only shows when the effect lights it (through `Brightness`).
`PrimitiveRole.Arc`, `Haze` and `Flash` are the roles for the last three, and `Edge` is for a strand hugging the screen's border. `particle.static`
is `particle.mote` in storm colours with its cross-shaped sparkle switched off.

**Pacing an effect in beats** — Paralysis is a cycle, not a loop of ambient motion: an idle
that is quiet but never dead, a charge that builds for `ChargeLead` seconds, a shock every
`ShockMin`-`ShockMax` seconds, then a discharge. Each arc keeps its own timeline (`Envelope`:
leader, return stroke, re-strikes, cooling, afterglow), so the shock is a set of per-arc start
times (a few milliseconds apart) rather than a global switch, and a "stray" can fire a single
arc between shocks. The view's reaction is stepped on purpose (`Spasm`: it snaps to a new
offset at set instants, then rings out, with a shiver on top, and a depth "punch" that yanks the
camera toward the layers): a muscle, not the pendulum swing used for an effect that is denied
rather than shocked. Whole-screen light follows one smoothed function of the shock
(`ShockLight`: a flash and usually one weaker re-flash), which also lights the clouds, the edge
glow and the vignette, so nothing flickers on its own; this keeps it to at most two
full-screen flashes in any second.

**No two shocks alike** — a shock is decided in advance as a `Plan` (strength, the direction
it hits from, what share of the arcs take part, the order they strike in: by layer, a sweep
round the perimeter, a wave from the hit, or all at once; leader length, re-strike count,
flash, violence). As soon as the previous shock's arcs are all spent, the whole cage is laid
out afresh for the next plan (`RollAll`: new spans, bows, seeds and anchor positions, with
the arcs facing the hit favoured), so the idle streamers that lengthen during the charge
already point at where the shock will land. The spasm is thrown away from the hit and its
timing and gains are jittered per shock. To make a new effect's repeats vary, roll a plan, not
a random number per frame: variation that is decided early can be telegraphed and reads as
intent.

**The idle layer** — between shocks the screen is still electrified, quietly, so it never
looks switched off or like a continuous shock: bolts surge along the screen's border, small
static pops go off at the edges (`EmitPops`), short streamers flicker from the anchors of the
arcs the next shock will strike, crackles hop on the visible stretch of the arcs
(`MeasureVisible` finds it, so they are never placed off-screen), the static twinkles, and the
edges keep a faint warm rim. The edge bolts (`EmitEdgeArcs`) are real `stroke.lightning`
strands, not particles: each lies along an edge, anchored to the border at both ends and bowed
a little inward, strikes with a quick leader, then crackles while `SurgeAt`/`SurgeGain` runs a
pulse of current the length of it (each time the other way) and dies away. They use their own
`PrimitiveRole.Edge` slot ("Edge arcs"), so they stay electric when the main arcs are swapped
to chains. Everything in the layer is stateless (a hash of slot and time), gives way to the
shock (`quiet`) and returns after it, and swells with the charge.

**Anchors that stay off-screen** — an effect that hangs strands from the screen's edge must
not trust the layout to keep the anchors there: a tilted layer camera swings a point by its
distance from the centre times the tilt, so on a wide screen (or in a shake) anchors can be
dragged hundreds of pixels inside and show their ends. Paralysis scales its tilt by the
aspect ratio and then pushes each anchor outward until the camera's own projection puts it
past the edge (`KeepOffscreen`).

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

Which statuses count as "silenced" is decided the same way as for the visuals: an effect
declares them in `TriggerStatuses`, so the real Silence debuff blocks chat because
`SilenceEffect` names it (a custom Moodles/Loci rule or tooltip keyword mapped to Silence
works too). Blocking does not depend on the Silence visual being switched on, and the
debug "force effect" button never blocks your chat.

The blocker keeps a lock-free count of swallowed messages. `EffectManager` polls it each
frame and tells any live effect that implements `IReactsToBlockedChat`; Silence uses that
to reject the message, with the seal jerking sideways and a shock running out through
every ring, so the player can see why nothing was sent.

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
- **A new colour of light** (a different curse, a holy glow): add a `Palette` to
  `Palette.cs`, then register `new StrokeGlow(name, palette, words)` and/or
  `new ParticleMote(name, palette, words)` in `MaterialRegistry`. Effects name them in
  their slots exactly as they would any material.
- **A new viscous liquid** (blood, sludge, venom): add a `LiquidSpec` to `LiquidPresets`,
  then subclass `ParticleGoop` with a name, `NaturalLanguageWords` and a drip emission (see
  `ParticleSlime`, about ten lines) and register it. Any stroke can then shed it by
  declaring an emission with a `Drip` block; a colour override re-hues it for free.
- **A new `DebuffKind`**: add it at the END of the enum (numeric values are serialized),
  then write an effect that declares it.
