# Real Debuffs

A Dalamud plugin for Final Fantasy XIV that turns your debuffs into screen effects: Blind darkens the view, Burns licks fire up from the bottom edge, Bind throws ropes that pull taut. Effects layer and fade in and out smoothly.

Effects so far: Amnesia, Bind, Blind, Burns, Charm, Disease, Doom, Dropsy, Frost, Heavy, Hysteria, Paralysis, Silence, Vulnerability.

Commands:
- `/realdebuffs` opens settings.
- `/realdebuffs toggle` turns every effect on or off.
- `/realdebuffs statuses` logs your current statuses (including Moodles/Loci) to the Dalamud log.

## Features

- Per-effect toggles and a master intensity slider.
- Hidden during cutscenes, while the game UI is hidden, and in GPose.
- Optional chat blocking while Silenced (off by default).
- Moodles/Loci support: match a custom status by title to show an effect.
- Tooltip keywords: a status description containing "burning" triggers Burns, and so on.
- Natural wording: *"red chains made of flames"*, *"red flaming chains"* and *"red, burning chains"* all give red chains that shed red flames; *"black chains with white flames"* colors the two separately; *"rope made of red, flaming tentacles"* swaps Bind's rope for tentacles too.
- An effect generator panel to test a description, build a look (body, modifier, two colors), and copy it as a phrase or an exact code.

## How it works

Every effect derives from `SceneEffect` (an `ISceneEffect`). Effects don't draw; they push primitives into an `EffectScene`, `StrokeShed` adds what strokes shed, and `SceneRenderer` draws the frame once (vignette, regions, strokes, particles; a vignette requested with `over: true` goes after the regions, and `BelowVeil` particles go between the plain regions and the `Veil` ones). `EffectDiscovery` finds every public, non-abstract effect with a parameterless constructor, so a new effect is one class. An effect that owns more than its own file gets a folder (`Effects/Doom/`) holding its strokes, particles, fields and theme; anything two effects use lives in `Effects/Framework`. `Effects/` needs nothing from the host except `DebuffKind` (`Effects/DebuffKind.cs`), which is why the harness can compile it alone.

Each frame `EffectManager` maps your statuses to `DebuffKind`s (via `StatusCatalog`), adds kinds requested by Moodles/Loci rules or tooltip keywords, steps each fade, calls `Emit`, and renders. It skips drawing below 64px.

### Primitives

- `StrokePrimitive`: a strand along a `StrandPath` (ropes, chains, rings, tendrils).
- `ParticlePrimitive`: one free particle; its `PrimitiveRole` picks the material.
- `RegionPrimitive`: a screen rectangle with an optional edge mask.
- `ImpactPrimitive`: a one-frame event; the owner's stroke material decides what it throws.

### Materials

Looked up by name (config stores strings), in three families: `IStrokeMaterial`, `IParticleMaterial`, `IRegionMaterial` (`RegionMaterial<TState>` for ones that draw an `IRegionState`). `MaterialRegistry` finds them by reflection: every material with a public parameterless constructor, plus every `IMaterialPack` (an effect's palette variants live in its `<Name>Theme.cs`). Strokes may declare `Emissions` (shed along the strand) and `ImpactEmissions`; particles may declare `Emissions` for when they're used as a stroke emitter.

`EffectScene.Materials` (a `MaterialResolver`) resolves each primitive in this order: user override (settings or a "made of X" phrase), the effect's declared default (`SwappableSlot`), then a fallback (a role's `[Fallback]` particle) so an unknown name never crashes. Results are cached until the override dictionary instance changes.

`StrokeShed` owns what strokes shed and carry: free-flying particles (a `ParticleSystem`), particles flowing along the strand, coats (below), and impact bursts. A swapped-in material sheds along the whole strand rather than only inside the effect's `EmitEdgeReach` / `EmitEndReach` windows.

`RegistryAudit` cross-checks slots, defaults, role fallbacks, shed targets and vocabulary; Plugin logs its findings at startup, and the harness runs it with `audit`.

### Tooltip language

Status text becomes one `Look` per effect (`Effects/Framework/Language/`): an optional `Body` (what "made of X" swaps), one `Modifier` (what "with X" or an adjective like "flaming" adds), and two colors. `PhraseParser` tags each word as an effect trigger, material, color, link ("made of", "with", "covered in"...) or filler. A color sticks to the next noun, an adjective form or a linked noun modifies the effect noun it belongs to, a word that is both an effect and a material is consumed as the material when attached to another effect, and anything left over triggers its own effect. A modifier with no color of its own inherits the body's. `Lexicon` generates plural, `-ed` and `-ing` forms from each effect's `keywords` and each material's `NaturalLanguageWords`, so lists stay short.

A `Look` is applied by `LookApply` as ordinary override keys, so everything below it is unchanged. `LookCode` writes the exact form, which also reaches advanced slots words can't: `RD` plus packed base62 (case-sensitive, only ever pasted; a typical look is 6-9 characters, every slot of Doom 32), checksummed so a cut-short or edited code is ignored, never half-applied; a code in a description wins for its effect. Materials, colours and slot addresses are packed by their number in `CodeIds` (append-only, never reorder; the audit names any that are missing). A look that overrides many slots switches to a bitmap of addresses, which is shorter than listing them. `TooltipLanguage` (host) builds the lexicon from the effects plus the user's extra, switched-off and muted words, and caches parses. The settings tester and the generator use the same parser, so they show exactly what a real status would do.

### Chat blocking

`ChatBlocker` hooks the chat-input processor and swallows messages while Silenced. If a game patch breaks the signature it logs once and does nothing. Which statuses count is set by an effect's `TriggerStatuses`, independent of whether the visual is enabled. Silence implements `IReactsToBlockedChat` to show the rejection.

## Shared building blocks (`Effects/Framework`)

Folders don't dictate namespaces, and materials are found by reflection, so where a file lives is purely organisational.

| Folder | Provides |
|---|---|
| `Core/` | `EffectScene` and the primitives; `Cast.cs` (`CastTracker`, `CastFrame` with `Fresh`/`Resized`, the optional alpha envelope, `ShockFront`); `Material.cs` (material contracts, `RegionMaterial<T>`, `IMaterialPack`); `Timing.cs` (`Flicker`, `FlashSchedule`, `Heartbeat`); `Screen.cs` (edge anchors, `ScreenGrowth`); `DrawHelpers`, `Noise`, `Palette` |
| `Registry/` | `EffectRegistry` (slots, defaults), `MaterialRegistry`, `MaterialResolver`, `MaterialOverrideKey`, `EffectDiscovery`, `RegistryAudit` |
| `Language/` | `Lexicon`, `PhraseParser`, `Look` and `LookApply`, `LookCode`, `LookText`, `ColorLexicon` (see Tooltip language) |
| `Rendering/` | `SceneRenderer` |
| `Particles/` | `ParticleSystem` (pool: spawn, integrate, retire, emit), `StrokeShed`, `Flames` and `FireDrift` (`Fire.cs`), `MoteField` |
| `Strokes/` | `StrandPath`; `VerletStrand.cs` (strand physics, `StrandRig`, `RigSet`); `Mesh.cs` (`MeshBuilder`, `PathRows` for tube materials); `Shading.cs` (lighting); `SceneStrokes` (`Ring`, `HeroRing`) |
| `Regions/` | `SceneRegions` (`Wash`, `EdgeSpill`, `Pool`, `Murk`) |
| `Systems/` | Simulations shared by a family of effects, each with the region state its material draws: `Liquid/` (`LiquidPhysics` on one viscosity axis, `Liquid`, `DropPool`, `LiquidField` jets, drips, a standing-wave pool, light shafts, splashes, `RegionLiquid`), `Coat/` (`Coat`, `CoatSurface`, `CoatMatter`, `CoatDraw`), `Tunnel/` (`TunnelField`, `TunnelFlight`), `Sigil/` (`LayerCamera`, `SigilView`, `SigilGeometry`, `RuneGlyphs`), `Whispers/` (`ScriptFont`, `WhisperField`) |
| `Materials/` | The materials any effect can use: `Particles/`, `Strokes/`, `Regions/` |

### Coats

A `Coat` is matter lying on one chart of a host: an amount per cell (U rows along it, V sectors across it; a plain strand is one sector), fed by supply, run by gravity (thin film clings, thick runs, mass is conserved), then handed to the matter's own rules. It is how any stroke (tentacle, chain, rope, ring) is slimy, wet, bloody, burning or snowy, and it is what a swapped-in emit material becomes.

- **Matter** is a `CoatMatter`, handed out by any particle material that implements `ICoatMatter`: `LiquidMatter` (`ParticleGoop`: film, beads, drops that neck and let go), `FlameMatter` (`ParticleEmber`: fuel, heat that spreads, flames, char, cinders, smoke), `GranularMatter` (`ParticleSnow`: banks on upward faces, sheds lumps). A new matter is a subclass: coefficients plus `Tick`, `Emit`, `Draw`.
- **Opting in**: a stroke material lists `StrokeEmission.Coated(role)`; a particle material that returns one is coatable on any stroke by the emit axis (`...Emit=particle.snow`). The matter is whatever the role's slot, or the emit override, holds.
- **Charts**: a host that implements `ICoatSurfaceProvider` fills `CoatChart`s each frame from the same geometry snapshot it draws (cells are its own vertices; it never lets the coat build a second copy), with a stable `ChartId` per part (`StrokeChain`: one per drawn link half). Any other stroke is cut into a `TubeChart` straight from its path. The frame spends at most `CoatSet.CellBudget` cells; a crowded frame gets a coarser chart before a link goes bare. A chart whose part is hidden keeps its film frozen for `ChartGrace` seconds.
- **Contacts**: a host proposes which chart sides may touch (`CoatChart.AddContact`: a link's tip and the next link's, a link's two halves as a seam); `CoatSet.Transfer` accepts a cell pair only if the host's axes under them lie within their radii in 3D (so a gap, or a surface passing in front at another depth, passes nothing), fades the weight with distance, shares it among a cell's partners, and moves film by the same slope/yield/levelling law as inside a chart, exact in both coats' books. A pendant draws from the whole cross-section of its rows, as a strand's does.
- **Draw**: a strand gets `CoatDraw.Band`, a lit strip along the cells (smoothed for drawing only; the simulation keeps the path's own samples), right after its host stroke. A chart host draws its own film (`ICoatLayeredHost`), in its own depth order, from the field.
- **Liquids** add: a power-law index `n` (1 = water-like; slime 0.25 runs only on steep slopes and strings out long), a levelling term that spreads lumps, light absorption (thick blood is darker), and bridges: a stretching drop that touches another wet host of the same liquid sticks as a drooping string that drains, stretches with the hosts and snaps back into both films.
- **Drops by size** (`GoopDraw`): under 1 px a speck, under 2.5 px a nine-vertex disc (or the reduced tube if streaking), under 4 px the tube with 3 columns and 4 bead rows, above that the full mesh; the glint is kept at every rung. Runs of mesh-only particle materials (`IBatchedParticleMaterial`) are staged by `MeshDraw.BeginBatch/EndBatch` and handed to the draw list in one reservation, same triangles in the same order.
- **Render isolation** (`DrawGuard`): a material whose `Draw` throws loses that primitive; after 3 in a row it is quarantined per owner for the session and logged once through `DrawGuard.Log` (the host sets it). Keep the order push-colour-override → look up → draw at every site (see the notes).
- **Books**: cell writes carry their rounding remainder (`Credit`/`Debit`, `Apply`), so the cells are exact to ~1e-15; pendant and drop masses are float32 (a few 1e-6 at the heaviest). `CoatSet.Audit()` returns entered = held + flying + sunk. Matter leaves only through named sinks (dried, ran off screen, burned, lumped, host gone, or a drop that fell); a full drop pool refuses a release, so the pendant keeps its matter.
- **Dropsy's pool** is a reservoir (`LiquidField.Reservoir`): it holds the water that actually lands (a stream's volume per second at its impact, a drop's volume when it lands; both in jet volume, so a stream breaking into drops changes nothing), drains as it deepens, and levels at wave speed. The owner sets only the depth it holds at a reference inflow (`PoolDepthRef`, `PoolFluxRef`) and `Leaving`. `harness pool` prints its books.
- **Checks**: `harness cointest` (114 assertions: ledgers, gravity and levelling, power-law behaviour, drip rate and drop size against viscosity, bridges, fire, snow, seam and free-end cases, frame-rate independence, the pool reservoir, a synthetic 2D plate and ring for charts, the real chain judged against what it draws, chart contacts (touch, gap, depth, moving apart and together, seams, regrid), the drop rungs and staging, and render fault injection); `harness coatstat` (size, books, bridge cost, thickness per second), `harness coat` and `harness cost` report a cast with `OVERRIDES` applied; `harness stackbench W H fps frames Kind,..|all` emits several effects into one scene and reports avg/p50/p95/p99, vertices, reservations and GC; `harness bridgeshot` renders two slimy chains; `regress.sh` ends with full-resolution close-ups (`cu_*`) of coated and bare chain, rope, tentacle and Dropsy.

## Building

`RealDebuffs.csproj` uses `Dalamud.NET.Sdk`, which resolves Dalamud, ImGui and Lumina from your local Dalamud dev install (`DALAMUD_HOME`, normally `%APPDATA%\XIVLauncher\addon\Hooks\dev`).

```
dotnet build -c Release
```

No `<TargetFramework>` is set on purpose; the SDK picks the one Dalamud targets.

## Adding things

- **Effect**: derive from `SceneEffect`, pass `kind, name, description, drawOrder, status` (plus optional `keywords`, `envelope: true`) to its constructor, and override `Emit(EffectScene scene, in CastFrame f)`. `f` carries the screen size, `Px`, `Age`, `Seed`, `Fresh`, `Resized`, alpha and color; `envelope: true` adds a faster fade-in plus `Gate`/`Leave`/`Leaving` (see `DoomEffect`); override `MaxStep` to cap the frame step (see `DiseaseEffect`). Art only this effect uses goes beside it in `Effects/<Name>/`. Implement `IHasSwappableSlots` to expose materials (`Slot.Stroke`, `Slot.Particle`, `Slot.Region`), and mark at most one slot `hero: true` (the body "made of X" replaces) and one particle slot `modifier: true` (what "with X" replaces; a stroke body with none takes the modifier as its shed). Pick the modifier from a layer that is visible on screen; the audit enforces the limits. Scale sizes by `f.Px` (it is `DrawHelpers.PixelScale`, the same scale materials use), and re-lay anything anchored to the screen size when `f.Fresh || f.Resized`. Implement `IReceivesStacks` to get the status's stack count (every status mapped to the kind adds its stacks; 1 if it doesn't stack): it arrives before each `Emit` and reads 1 once the status has gone, so hold the last count while `f.Leave` > 0 (see `VulnerabilityEffect`).
- **Material**: implement a material interface with a public parameterless constructor and it is registered. Give it `NaturalLanguageWords` to make it selectable by wording (words are unique across materials; the audit flags clashes) and append its name to `CodeIds` so short codes can carry it (a new slot role or region kind takes the next number in `CodeIds.Slot`; the audit says which). Plurals, and for particles `-ed`/`-ing`/`-y` forms, are generated; list an adjective ("fiery", "frozen") only when it isn't derived from a listed noun.
- **Role**: add a `PrimitiveRole` with an unused number; `[Fallback("particle.x")]` names the particle drawn when a slot has none.
- **Particles**: an effect owns a `ParticleSystem(capacity, seedSalt, physics)`; `Stream` spawns on a random interval, `Burst` all at once, `Emit` hands them to the scene as one `ParticleStyle`. Anything else is hand-built `scene.AddParticle` calls.
- **Color of light**: in the effect's folder, a `<Name>Theme.cs` holds its palette class and an `IMaterialPack` listing `StrokeGlow` / `ParticleMote` (and `StrokeSigil`, `ParticleRune`, `ParticleEmber`, `ParticlePuff`, `RegionFirelight` for a full theme) built with it (see `DoomTheme.cs`). A `Palette` used by shared code stays in `Palette.cs`. Add new rune glyph sets after the existing ones; don't change `LegacyCount`.
- **Eyes**: `ParticleEye` (`particle.eye`) draws a shaded eye in any of the `EyeStyle`s, including a lidless `Orb` with its nerve; `ParticleEye.Held(open, style)` lets an effect drive the lids, and small or far eyes use fewer vertices. A themed set registers another instance with its palette. `StrokeWisp` is a soft tapered streamer for things in motion (path runs tail to head).
- **Fog**: a `ParticlePuff` `Look` with a `Ramp` draws as a billow: a mesh shaded from warped noise and lit from the screen's middle (`Variant` = squash %, `Rotation` = stretch). Give any smoke or mist a `Ramp` to upgrade it.
- **Script**: `ScriptFont` (`Script.cs`) is a single-stroke cursive alphabet (lowercase, `' , . ! ? -`, `♥`); `ParticleWhisper` draws a phrase in it and `WhisperField` spawns and drifts them (two on screen as a rule, three at most). Phrases are `CharmWhispers.Phrases` and `AmnesiaWhispers.Phrases`; a themed set adds another `ParticleWhisper` with its palette and phrases to a pack.
- **Vortex**: `RegionVortex` draws a flat two-colour spiral behind a tunnel of fog from a `VortexField` (`At` places anything on the fog's wall); the fog thins to nothing at the centre. See `AmnesiaEffect`.
- **Coatable matter**: implement `ICoatMatter` on a particle material and return a `CoatMatter` (copy `GranularMatter` for the smallest); give it `Emissions` of `StrokeEmission.Coated(role)` to make it work as a swapped-in emit material.
- **Liquid**: add a `LiquidSpec` (look, plus `Viscosity` 0 water to 1 slime) to `LiquidPresets`, subclass `ParticleGoop` (see `ParticleWater`). Drips, stream breakup, pool waves, splash and cling all follow from `Viscosity`; any stroke can wear it as a coat (below).
- **Pouring liquid**: an effect owns a `LiquidField`: `Begin(..., jets, seeps)` on `f.Fresh || f.Resized` makes idle slots; `Launch` a jet when it is `Idle`, `Place` a seep, set `Flow`, `Speed`, `Angle`, `Level` each frame, then `Step` and `Emit`. `Spray`, `Fling` and `Splat` throw droplets, a drop or lens beads. See `DropsyEffect`; resolve the liquid with `Liquid.Resolve(scene, Kind, role)` so a hero slot swap changes everything.
- **`DebuffKind`**: add at the END of the enum in `Effects/DebuffKind.cs` (values are serialized); 13 kinds are already reserved.
