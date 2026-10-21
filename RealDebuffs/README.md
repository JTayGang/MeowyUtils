# Real Debuffs

A Dalamud plugin for Final Fantasy XIV that turns your debuffs into screen effects: Blind darkens the view, Burns licks fire up from the bottom edge, Bind throws ropes that pull taut. Effects layer and fade in and out smoothly.

Effects so far: Bind, Blind, Burns, Disease, Doom, Frost, Heavy, Paralysis, Poison, Silence, Vulnerability.

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

Every effect is an `ISceneEffect`. Effects don't draw; they push primitives into an `EffectScene`, and `EffectSceneRenderer` draws the frame once (vignette, regions, strokes, particles). `EffectDiscovery` finds every public, non-abstract effect with a parameterless constructor, so a new effect is one class.

Each frame `EffectManager` maps your statuses to `DebuffKind`s (via `StatusCatalog`), adds kinds requested by Moodles/Loci rules or tooltip keywords, steps each fade, calls `Emit`, and renders. It skips drawing below 64px.

### Primitives

- `StrokePrimitive`: a strand along a `StrandPath` (ropes, chains, rings, tendrils).
- `ParticlePrimitive`: one free particle; its `PrimitiveRole` picks the material.
- `RegionPrimitive`: a screen rectangle with an optional edge mask.
- `ImpactPrimitive`: a one-frame event; the owner's stroke material decides what it throws.

### Materials

Looked up by name (config stores strings), in three families: `IStrokeMaterial`, `IParticleMaterial`, `IRegionMaterial`. Strokes may declare `Emissions` (shed along the strand) and `ImpactEmissions`; particles may declare `Emissions` for when they're used as a stroke emitter.

For each primitive the renderer resolves a material in this order: user override (settings or a "made of X" phrase), the effect's declared default (`SwappableSlot`), then a universal fallback so an unknown name never crashes.

`StrokeAutoEmitter` spawns whatever a stroke's material declares, either free-flying or flowing along the strand. A swapped-in material sheds along the whole strand rather than only inside the effect's `EmitEdgeReach` / `EmitEndReach` windows.

### Tooltip keywords

`TooltipKeywordParser` matches keywords as whole words, reads colors from `[color=...]` tags or color words in the same clause, and resolves "made of X" per clause. The settings tester uses the same parser, so it shows exactly what a real status would do.

### Chat blocking

`ChatBlocker` hooks the chat-input processor and swallows messages while Silenced. If a game patch breaks the signature it logs once and does nothing. Which statuses count is set by an effect's `TriggerStatuses`, independent of whether the visual is enabled. Silence implements `IReactsToBlockedChat` to show the rejection.

## Shared building blocks (`Effects/Framework`)

| File | Provides |
|---|---|
| `Cast.cs` | `CastTracker` and `CastFrame` (per-frame cast values, incl. `Fresh` and `Resized`), the optional alpha envelope, `ShockFront` (rings of excitement) |
| `Stage.cs` | Scene helpers (`Wash`, `EdgeSpill`, `Pool`, `Ring`, `HeroRing`) and `MoteField` |
| `Fire.cs` | `Flames` (stateless flame tongues) and `FireDrift` (cinders and smoke) |
| `Sigil.cs` | `LayerCamera` (tilted 2.5D stack), `SigilSway` (how a stack leans and breathes), `SigilGeometry` (rings, polygons), `RuneGlyphs` |
| `VerletStrand.cs` | Strand physics, `StrandRig`, `RigSet` |
| `Goop.cs` | Viscous liquids (slime, blood, acid): `GoopEmitter`, `GoopDraw` |
| `LiquidField.cs`, `Materials/RegionLiquid.cs`, `Materials/StrokeThread.cs` | Poison’s pooled acid field, etched-glass liquid mass, threads, and drip lines |
| `Mesh.cs`, `Shading.cs` | Triangle-mesh drawing (`MeshBuilder`, `PathRows` for tube materials) and lighting |
| `Screen.cs` | Screen-edge anchors, growth, wind |
| `Bolt.cs`, `Dendrite.cs`, `FrostField.cs`, `Noise.cs` | Lightning, branching growth, frost, noise |

## Building

`RealDebuffs.csproj` uses `Dalamud.NET.Sdk`, which resolves Dalamud, ImGui and Lumina from your local Dalamud dev install (`DALAMUD_HOME`, normally `%APPDATA%\XIVLauncher\addon\Hooks\dev`).

```
dotnet build -c Release
```

No `<TargetFramework>` is set on purpose; the SDK picks the one Dalamud targets.

## Adding things

- **Effect**: implement `ISceneEffect` (`Kind`, `DisplayName`, `Description`, `DrawOrder`, `TriggerStatuses`). Call `CastTracker.Frame(...)` once per `Emit` for a `CastFrame` (size, `Px`, `Age`, `Seed`, `Fresh`, alpha, color); `new CastTracker(envelope: true)` adds a faster fade-in plus `Gate`/`Leave`/`Leaving` (see `DoomEffect`). Implement `IHasSwappableSlots` to expose materials, and mark the slots a "made of X" phrase replaces with `Hero: true`. Scale sizes by `f.Px` (it is `DrawHelpers.PixelScale`, the same scale materials use), and re-lay anything anchored to the screen size when `f.Fresh || f.Resized`.
- **Material**: implement a material interface and add it in `MaterialRegistry`'s static constructor. Give it `NaturalLanguageWords` to make it selectable via "made of X".
- **Color of light**: add a `Palette` in `Palette.cs`, then register `StrokeGlow` / `ParticleMote` (and `StrokeSigil`, `ParticleRune`, `ParticleEmber`, `RegionFirelight` for a full theme) with it. Add new rune glyph sets after the existing ones; don't change `LegacyCount`.
- **Viscous liquid**: add a `LiquidSpec` to `LiquidPresets`, subclass `ParticleGoop` (see `ParticleSlime`) and register it. Any stroke can shed it with a `Drip` emission.
- **`DebuffKind`**: add at the END of the enum (values are serialized).
