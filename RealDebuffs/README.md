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
- "Made of X" swaps: *"bound with chains"* draws Bind's ropes as chains; *"red flame"* tints Burns red; *"white chains made of snow"* gives white chains that shed snowflakes.
- An effect generator panel to test a description and swap materials or colors by hand.

## How it works

Every effect derives from `SceneEffect` (an `ISceneEffect`). Effects don't draw; they push primitives into an `EffectScene`, `StrokeShed` adds what strokes shed, and `SceneRenderer` draws the frame once (vignette, regions, strokes, particles). `EffectDiscovery` finds every public, non-abstract effect with a parameterless constructor, so a new effect is one class.

Each frame `EffectManager` maps your statuses to `DebuffKind`s (via `StatusCatalog`), adds kinds requested by Moodles/Loci rules or tooltip keywords, steps each fade, calls `Emit`, and renders. It skips drawing below 64px.

### Primitives

- `StrokePrimitive`: a strand along a `StrandPath` (ropes, chains, rings, tendrils).
- `ParticlePrimitive`: one free particle; its `PrimitiveRole` picks the material.
- `RegionPrimitive`: a screen rectangle with an optional edge mask.
- `ImpactPrimitive`: a one-frame event; the owner's stroke material decides what it throws.

### Materials

Looked up by name (config stores strings), in three families: `IStrokeMaterial`, `IParticleMaterial`, `IRegionMaterial` (`RegionMaterial<TState>` for ones that draw an `IRegionState`). `MaterialRegistry` finds them by reflection: every material with a public parameterless constructor, plus every `IMaterialPack` (palette variants live in `Materials/Variants.cs`). Strokes may declare `Emissions` (shed along the strand) and `ImpactEmissions`; particles may declare `Emissions` for when they're used as a stroke emitter.

`EffectScene.Materials` (a `MaterialResolver`) resolves each primitive in this order: user override (settings or a "made of X" phrase), the effect's declared default (`SwappableSlot`), then a fallback (a role's `[Fallback]` particle) so an unknown name never crashes. Results are cached until the override dictionary instance changes.

`StrokeShed` owns what strokes shed: free-flying particles (a `ParticleSystem`), particles flowing along the strand, drips, and impact bursts. A swapped-in material sheds along the whole strand rather than only inside the effect's `EmitEdgeReach` / `EmitEndReach` windows.

`RegistryAudit` cross-checks slots, defaults, role fallbacks, shed targets and vocabulary; Plugin logs its findings at startup, and the harness runs it with `audit`.

### Tooltip keywords

`TooltipKeywordParser` matches keywords as whole words, reads colors from `[color=...]` tags or color words in the same clause, and resolves "made of X" per clause. The settings tester uses the same parser, so it shows exactly what a real status would do.

### Chat blocking

`ChatBlocker` hooks the chat-input processor and swallows messages while Silenced. If a game patch breaks the signature it logs once and does nothing. Which statuses count is set by an effect's `TriggerStatuses`, independent of whether the visual is enabled. Silence implements `IReactsToBlockedChat` to show the rejection.

## Shared building blocks (`Effects/Framework`)

| Folder | Provides |
|---|---|
| `Core/` | `EffectScene` and the primitives; `Cast.cs` (`CastTracker`, `CastFrame` with `Fresh`/`Resized`, the optional alpha envelope, `ShockFront`); `Material.cs` (material contracts, `RegionMaterial<T>`, `IMaterialPack`); `Timing.cs` (`Flicker`, `FlashSchedule`, `Heartbeat`); `Screen.cs` (edge anchors, `ScreenGrowth`); `DrawHelpers`, `Noise`, `Palette` |
| `Registry/` | `EffectRegistry` (slots, defaults), `MaterialRegistry`, `MaterialResolver`, `MaterialOverrideKey`, `EffectDiscovery`, `RegistryAudit` |
| `Rendering/` | `SceneRenderer` |
| `Particles/` | `ParticleSystem` (pool: spawn, integrate, retire, emit), `StrokeShed`, `Flames` and `FireDrift` (`Fire.cs`), `MoteField`, `WindField` |
| `Strokes/` | `StrandPath`; `VerletStrand.cs` (strand physics, `StrandRig`, `RigSet`); `Mesh.cs` (`MeshBuilder`, `PathRows` for tube materials); `Shading.cs` (lighting); `SceneStrokes` (`Ring`, `HeroRing`) |
| `Regions/` | `SceneRegions` (`Wash`, `EdgeSpill`, `Pool`, `Murk`) |
| `Systems/` | Simulations shared by a family of effects, each with the region state its material draws: `Liquid/` (`LiquidPhysics` on one viscosity axis, `Liquid`, `DropPool`, `GoopEmitter`, `LiquidField` jets, drips, a standing-wave pool, light shafts, splashes), `Frost/`, `Tunnel/` (`TunnelField`, `TunnelFlight`), `Sigil/` (`LayerCamera`, `SigilView`, `SigilGeometry`, `RuneGlyphs`), `Whispers/` (`ScriptFont`, `WhisperField`), `Electric/` (`Bolt`) |
| `Materials/` | `Particles/`, `Strokes/`, `Regions/`, and `Variants.cs` |

## Building

`RealDebuffs.csproj` uses `Dalamud.NET.Sdk`, which resolves Dalamud, ImGui and Lumina from your local Dalamud dev install (`DALAMUD_HOME`, normally `%APPDATA%\XIVLauncher\addon\Hooks\dev`).

```
dotnet build -c Release
```

No `<TargetFramework>` is set on purpose; the SDK picks the one Dalamud targets.

## Adding things

- **Effect**: derive from `SceneEffect` and pass `kind, name, description, drawOrder, status` (plus optional `keywords`) to its constructor. Call `CastTracker.Frame(...)` once per `Emit` for a `CastFrame` (size, `Px`, `Age`, `Seed`, `Fresh`, alpha, color); `new CastTracker(envelope: true)` adds a faster fade-in plus `Gate`/`Leave`/`Leaving` (see `DoomEffect`). Implement `IHasSwappableSlots` to expose materials (`Slot.Stroke`, `Slot.Particle`, `Slot.Region`), and mark the slots a "made of X" phrase replaces with `hero: true`. Scale sizes by `f.Px` (it is `DrawHelpers.PixelScale`, the same scale materials use), and re-lay anything anchored to the screen size when `f.Fresh || f.Resized`.
- **Material**: implement a material interface with a public parameterless constructor and it is registered. Give it `NaturalLanguageWords` to make it selectable via "made of X" (words are unique across materials; the audit flags clashes).
- **Role**: add a `PrimitiveRole` with an unused number; `[Fallback("particle.x")]` names the particle drawn when a slot has none.
- **Particles**: an effect owns a `ParticleSystem(capacity, seedSalt, physics)`; `Stream` spawns on a random interval, `Burst` all at once, `Emit` hands them to the scene as one `ParticleStyle`. Anything else is hand-built `scene.AddParticle` calls.
- **Color of light**: add a `Palette` in `Palette.cs`, then add `StrokeGlow` / `ParticleMote` (and `StrokeSigil`, `ParticleRune`, `ParticleEmber`, `RegionFirelight` for a full theme) with it to an `IMaterialPack` in `Materials/Variants.cs`. Add new rune glyph sets after the existing ones; don't change `LegacyCount`.
- **Eyes**: `ParticleEye` (`particle.eye`) draws a shaded eye in any of the `EyeStyle`s, including a lidless `Orb` with its nerve; `ParticleEye.Held(open, style)` lets an effect drive the lids, and small or far eyes use fewer vertices. A themed set registers another instance with its palette. `StrokeWisp` is a soft tapered streamer for things in motion (path runs tail to head).
- **Fog**: a `ParticlePuff` `Look` with a `Ramp` draws as a billow: a mesh shaded from warped noise and lit from the screen's middle (`Variant` = squash %, `Rotation` = stretch). Give any smoke or mist a `Ramp` to upgrade it.
- **Script**: `ScriptFont` (`Script.cs`) is a single-stroke cursive alphabet (lowercase, `' , . ! ? -`, `♥`); `ParticleWhisper` draws a phrase in it and `WhisperField` spawns and drifts them. Phrases are `CharmWhispers.Phrases` and `AmnesiaWhispers.Phrases`; a themed set adds another `ParticleWhisper` with its palette and phrases to a pack.
- **Vortex**: `RegionVortex` draws a flown-through spiral tunnel from a `VortexField` (`At` places anything on its wall); `StrokeSilk` is a twisting two-tone ribbon for the arms. See `AmnesiaEffect`.
- **Liquid**: add a `LiquidSpec` (look, plus `Viscosity` 0 water to 1 slime) to `LiquidPresets`, subclass `ParticleGoop` (see `ParticleWater`). Drips, stream breakup, pool waves, splash and cling all follow from `Viscosity`; any stroke can shed it with `Liquid.Phys.Shed()`.
- **Pouring liquid**: an effect owns a `LiquidField`: `Begin(..., jets, seeps)` on `f.Fresh || f.Resized` makes idle slots; `Launch` a jet when it is `Idle`, `Place` a seep, set `Flow`, `Speed`, `Angle`, `Level` each frame, then `Step` and `Emit`. `Spray`, `Fling` and `Splat` throw droplets, a drop or lens beads. See `DropsyEffect`; resolve the liquid with `Liquid.Resolve(scene, Kind, role)` so a hero slot swap changes everything.
- **`DebuffKind`**: add at the END of the enum (values are serialized).
