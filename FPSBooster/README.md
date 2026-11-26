# FPSBooster 0.3.0.62

## 0.3.0.62

- Fixed a likely source of the remaining Grounded Fluid depth mismatch: the world projection used to generate the liquid depth map was sampled later in the UI frame than the scene depth resource. Camera zoom/jitter updates between the depth pass and UI callback could therefore make projected liquid depth disagree with the captured DSV even though screen-space placement looked correct.
- The `OMSetRenderTargets` observation hook now snapshots the validated `Scene.ViewMatrix × Render.ProjectionMatrix` whenever a DSV is bound. The automatic viewport-matched DSV candidate retains its own projection snapshot for that frame.
- Grounded Fluid now projects its mesh using the exact camera snapshot associated with the selected depth resource before comparing raw reversed-Z device depth. If a matching snapshot is unavailable, it does not run an unsynchronized depth comparison; it clearly reports the condition and uses the existing depthless fallback.
- Added selected-DSV projection snapshot status to the Scene Depth report/UI.
- Kept automatic DSV selection, exact-pixel sampling, the existing raw reversed-Z comparison, and the tiny device-depth epsilon. No changes to collision sampling, fluid simulation, or the grounded mesh renderer.
- The hook remains read-only with respect to game render targets; it only samples camera structs and AddRefs DSVs already observed on the immediate context.
- Build and in-game behavior require validation on the user's client.

## 0.3.0.61 (superseded)

- Reverted the active Grounded Fluid occlusion comparison from 0.3.0.60's view-space reconstruction to direct reversed-Z device-depth ordering. The view-space version produced an all-red depth-difference view and failed to hide foreground geometry reliably on the live client, so the unsupported conversion was removed from the active path.
- Uses `sceneDepth - projectedLiquidDepth` in the same device-depth space. A larger reversed-Z value is closer, so foreground scene geometry hides liquid when its value exceeds the liquid depth by the configured epsilon.
- The `Reversed-Z depth bias` is a device-depth epsilon, not yalms. The automatic viewport-matched DSV remains the only supported selection because alternate candidates flickered in the user's client.

## 0.3.0.60 (superseded)

- Changed Grounded Fluid occlusion from a fixed normalized reversed-Z bias to a physical view-space distance tolerance in yalms. Both the projected liquid device depth and sampled scene device depth are reconstructed using the active `RenderCamera.ProjectionMatrix` before comparison, so the tolerance no longer changes meaning with camera zoom.
- Preserved screen-space affine interpolation of projected device depth in the CPU patch map; this matches how depth is represented across rasterized pixels. The shader reconstructs both sampled values after interpolation instead of incorrectly interpolating view-space Z.
- Added projection depth coefficients to the compositor parameter texture and expanded it to four float4 texels.
- Locked scene-depth selection to the automatic viewport-matching candidate. Manual DSV selection is removed because alternate candidates produced camera-dependent artifacts in the user's client.
- Renamed the UI control to `Depth tolerance (yalms)`, changed the initial tolerance to 0.010 yalms, and expanded the allowed physical tolerance to 0.25 yalms.
- Retained exact-pixel sampling as default and retained diagnostic modes. The red/blue depth-difference view now visualizes view-space distance differences in yalms.
- Build and in-game depth-occlusion behavior require validation on the user's client.

## 0.3.0.57

- Added depth-aware Grounded Fluid compositing. The grounded world grid is projected with the validated Scene ViewMatrix × Render ProjectionMatrix, CPU-rasterized into a compact plugin-owned UV/depth map, then composited by a D3D11 pixel shader against the selected scene-depth SRV.
- Uses the observed reversed-Z convention: scene depth greater than the projected liquid depth (plus configurable bias) hides the liquid fragment. A conservative 2×2 maximum depth sample reduces foreground silhouette leaks when the depth texture is lower resolution than the viewport.
- Keeps the scene depth resource read-only. The compositor writes only to plugin-owned RGBA32F mapping, parameter, and RGBA8 output textures; it never binds or modifies FFXIV's DSV.
- Added a Grounded Fluid toggle for scene-depth occlusion and a depth comparison bias slider. If capture is stopped or a compatible single-sample SRV is unavailable, the original depthless ImGui mesh remains as a visible fallback and the status explains why.
- Added reference-counted acquisition of the selected scene-depth SRV, with the compositor releasing its independent COM wrapper after each pass.
- The first implementation is a compositing proof-of-concept: depth correctness, edge behavior, camera alignment, and performance need in-game validation. It is not a native game scene mesh and does not use FFXIV's depth buffer as an output.

## 0.3.0.56

- Replaced the raw single-channel depth image (displayed by ImGui as red) with a plugin-owned RGBA8 grayscale conversion shader. It samples R16/R24/R32 single-sample depth SRVs and applies a sublinear contrast curve for reversed-Z visualization.
- Added an eligible-candidate selector with automatic viewport matching and manual per-DSV selection. Auto mode prefers viewport aspect ratio and matching dimensions rather than blindly selecting the largest texture, which can favor shadow or auxiliary targets.
- Keeps the last converted preview texture after capture stops, while releasing the captured game-owned depth SRV.
- Kept the conversion read-only: the game depth texture is only sampled; the grayscale output is a plugin-owned texture and is not the final liquid-occlusion path yet.

**Next test:** keep capture running, verify the grayscale preview, and compare the auto-selected viewport-sized candidate with the available `3440×1440` and `1720×720` R24 candidates. Use the manual selector to inspect each candidate if auto selection does not show recognizable scene geometry.

## 0.3.0.55

- Added D16 depth support for `R16_TYPELESS` resources with `D16_UNORM` depth views.
- Creates a read-only `R16_UNORM` shader-resource view when the resource includes `D3D11_BIND_SHADER_RESOURCE`.
- Tightened supported-format matching to require the expected DSV format for each typeless resource family.
- Added explicit DXGI format names for the R16/D16 family to diagnostic reports.
- Increased per-frame DSV candidate capacity from 12 to 32 and report when additional DSV bind events exceed the cap.
- Kept depth capture read-only; no game render target is rebound or modified.


## 0.3.0.54

- Added opt-in scene-depth capture diagnostics to Grounded Fluid. The probe observes the immediate D3D11 context's `OMSetRenderTargets`, snapshots the frame's DSV candidates before FPSBooster emits ImGui commands, inspects underlying texture dimensions/formats/sample counts/bind flags, and attempts a read-only SRV for supported typeless depth formats. A single-sample SRV can be previewed in the UI. No game render target is rebound or modified; the later 0.3.0.57 Grounded Fluid compositor uses the selected SRV for per-pixel occlusion.
- Added Grounded Fluid, combining the existing plugin-owned GPU fluid simulation with BGCollision terrain sampling.
- Reuses the Fluid Lab display texture and maps it onto a subdivided grid of ground-raycast vertices. Each cell is projected with the validated active Scene.Camera.ViewMatrix × RenderCamera.ProjectionMatrix combination.
- The local simulation's downward axis follows projected world gravity (downhill) on slopes; on nearly flat ground it falls back to the player's projected forward direction. Tangential gravity is scaled by the slope steepness, so a level floor does not receive an arbitrary forward gravity force.
- Added a fixed world placement, optional player-follow placement, configurable patch width/depth, 3–10 mesh subdivisions, follow-player refresh interval, surface offset, opacity, and abrupt-step cell rejection. Fixed world patches sample only at placement/manual refresh to avoid needless repeated collision queries.
- The overlay is driven from the plugin UI draw hook, so it remains visible with the FPSBooster window closed after being enabled and placed. The shared Fluid Lab field advances through that same hook while Grounded Fluid is enabled, preventing a double simulation step when the Fluid Lab tab is also open.
- Important limitation: the first integration is an ImGui textured projection, not a native 3D scene mesh. It follows sampled ground and camera movement but is not depth-tested against walls, characters, or other scene geometry. It is a visual feasibility test, not a replacement renderer.
- Build and in-game behavior require validation on the user's client.

# FPSBooster 0.3.0.49

## 0.3.0.49
- Fixed both BGCollision calls to write into a local unmanaged `RaycastHit` and then copy the result into the `out` parameter. This avoids taking the address of an `out` parameter, which caused CS0212.
- Made the collision report's `AppendHit` helper an instance method so it can include the measured `_sweepCenterAtHit` field, fixing CS0120.
- No collision-query behavior or probe controls intentionally changed. Build and in-game collision behavior still need verification on the user's client.

## 0.3.0.48
- Added Collision Lab as a separate research tab using FFXIVClientStructs BGCollisionModule queries.
- Added a vertical world raycast, configurable-radius sphere sweep, and a 3×3 grid of ground probes around a world-X/Z offset from the local player.
- Reports hit point, distance, native normal (with triangle-normal fallback), triangle vertices, material value, collider pointer, and local slope; reports can be copied for later analysis.
- Live sampling is rate-limited to 5 Hz and only runs while the Collision Lab tab is drawn. Closing the FPSBooster window stops live sampling.
- The tab does not draw geometry or modify game objects. It exposes physical contact data for later adaptation into RealDebuffs' existing 3D renderer.
- Sphere sweep passes a Vector4 origin with radius in W, matching the ClientStructs RaycastParams layout; the currently generated member wrapper types the pointer as Vector3*. This low-level argument detail requires in-game validation.
- Native collision calls and layer-mask behavior require in-game validation; no build or runtime query could be performed in this environment.

## 0.3.0.47
- Added an approximate contact-angle boundary condition to the thin-film solver using a bottom-wall ghost phase value. Lower angles encourage wetting/spreading along the bottom boundary; higher angles encourage beading. This is an experimental 2D boundary model, not a complete multiphase wetting model.
- Replaced the previous curvature-gradient smoothing force with a continuum-surface-force-style term based on interface normals, curvature, and the phase-field gradient.
- Added initial downward momentum to injected droplets so impacts carry more energy and can excite the solver's shallow-water waves.
- Reworked the water preview to reconstruct screen-facing normals from liquid depth and shade with diffuse, specular, Fresnel, and edge highlights. Dry areas now render transparent; the high-contrast dye view remains available.
- Added wetting strength, contact angle, drop impact speed, and surface-detail controls. Surface detail is display-only; the other new controls affect the simulation's boundary or injection behavior.
- Expanded the settings texture from two to three RGBA32F parameters and updated its row pitch. All simulation and display textures remain plugin-owned.
- Build and GPU shader execution still require in-game validation.

## 0.3.0.46
- Added a second Fluid Lab solver: a 2D thin-film/shallow-water model that stores liquid depth and horizontal/vertical momentum in each RGBA32F cell.
- Uses conservative Rusanov finite-volume fluxes with reflected no-through-flow boundaries, downward gravity forcing, velocity-based viscosity, and a curvature-gradient capillary force intended to reduce breakup.
- Increased the physics clock to fixed 120 Hz (up to sixteen substeps per rendered frame), while retaining the old simple mass-transfer solver as a side-by-side baseline.
- Added a separate plugin-owned display target and display shader so simulation state can store depth/momentum without sacrificing the blue-water and high-contrast dye previews.
- Added solver selection, viscosity, and surface-tension controls. Changing solver clears the field to avoid mixing incompatible solver states.
- CPU reference validation over 1,200 steps (10 simulated seconds) kept total mass within approximately 0.0002 units for tested parameter combinations; GPU shader execution/build still requires in-game verification.

## 0.3.0.45
- Expanded Fluid Lab with fixed 60 Hz simulation steps, a 0.25×–4× simulation-speed control, independent lateral diffusion, and high-contrast dye visualization.
- Increased the per-step downward transfer range so gravity-driven movement is easier to observe; blue-channel pooled depth may exceed 1.0 rather than being clipped and losing mass. Dye visualization does not alter the scalar mass.
- Added simulated-time and per-frame substep counters for comparing behavior at different UI/game frame rates.

## 0.3.0.44
- Added the Fluid Lab tab: a plugin-owned D3D11 GPU fluid-field experiment using two alternating RGBA32F textures, shader-resource views, and render-target views.
- Added a fullscreen HLSL simulation pass with gravity-biased downward transfer, viscosity-scaled lateral equalization, one-shot droplet injection, pause, clear, and demo seeding controls.
- Records the pass on a deferred D3D11 context and executes it with immediate-context state restoration; it does not modify FFXIV-owned textures or render targets.
- Compiles the small embedded HLSL shaders with the Windows D3DCompiler runtime when the user creates the simulation. The UI reports shader/deferred-context errors instead of silently falling back to changing the game's immediate pipeline.
- GPU timing currently reports CPU submission time only; GPU execution timing and readback validation are not implemented yet.

## 0.3.0.43
- Removed the actor-bound timeline-to-Camera conversion experiment, including timeline selection UI, selective `TmLn.BnNo` rewriting, and the related Penumbra preparation/validation APIs.
- Automatic Camera-binder injection is now explicitly restricted to zero-binder AVFX files; assets with existing actor/world binders are left untouched.
- Kept read-only timeline inspection/reporting because zero-binder injection still assigns every timeline to its appended Camera binder.
- Kept actor-attached/world-space spawning, existing native Camera-binder playback, Penumbra replacement reuse, and the native-versus-injected comparison.

## 0.3.0.42
- Removed the retired ViewProjection and inverse-ViewMatrix plugin-managed camera-placement spawn modes and their UI/transform-update paths.
- Kept actor-attached and player-position/world-space spawning, the native AVFX Camera-binder route, Penumbra injection, and the native-versus-injected comparison.
- Retained Camera distance/horizontal/vertical offset controls as reserved values; they are not currently applied to native Camera-binder placement.
- Kept the native-versus-injected Camera-binder comparison. Its camera sampling is diagnostic-only and never writes VFX transforms.

## 0.3.0.41
- Fixed Camera-follow reselection after clearing a preview: clearing now removes native VFX instances but retains the Penumbra AVFX replacements needed to recreate the same resource during the current session.
- The clear-previous selector option now clears spawned instances only; it no longer removes cached replacement assets.
- Failed native spawn attempts retain their verified replacement for retry because the game may already have cached the resource path.
- Explicit replacement removal, window close, and plugin unload still clean up temporary Penumbra mappings.

## 0.3.0.40
- Fixed missing `System.Text` import in `PenumbraAvfxReplacementProbe` (`StringBuilder`).
- No runtime behavior changes.


Dalamud research plugin for FFXIV. It combines housing-furniture culling with performance, native texture, and native AVFX experiments. The research features are experimental; they are not all production optimizations.

## 0.3.0.39

Camera-follow previews persist when browsing by default. Multiple distinct Penumbra-injected AVFX paths coexist through unique temporary-mod tags. Clear controls remove spawned VFX instances but retain those replacements for same-session reuse; mappings are explicitly removed with the replacement-removal control, when the VFX window closes, or when the plugin unloads.

## Build

Requires the .NET 10 SDK and Dalamud development files (`DALAMUD_HOME`). From the project directory:

```powershell
dotnet restore
dotnet build -c Release
```

Open the plugin with `/fpsbooster`. `/fpsbooster on` and `/fpsbooster off` toggle housing culling. `/fpsbooster test` opens the Housing tab's temporary visibility controls.

## Tabs

### Performance

Measures the interval between Dalamud framework updates and FPSBooster's housing-culling CPU time. Capture a repeatable scene with culling off and on to compare results. Native texture hooks add measurement overhead, so keep them disabled for a clean baseline.

This is not a direct GPU profiler or a measurement of actual presentation latency.

### Housing

Hides furniture outside the camera view. Outdoors, the game's native background culling is used where available; FPSBooster can add distance and height limits. Nearby furniture and spotlight-protected furniture are kept visible unless an explicit distance/height limit overrides protection.

Useful controls:

- **Extra room around the screen:** visibility margin to reduce edge pop-in.
- **Always show nearby furniture:** keeps nearby items visible.
- **Maximum distance / height difference:** limits furniture across large spaces or other floors.
- **Show all furniture in GPose / pause if character is off screen:** safety controls.

Temporary test buttons only apply while held. Hidden furniture is restored when culling is disabled, paused, or the plugin unloads.

### Native Textures

This tab combines SqPack inspection with native texture-lifecycle diagnostics.

Typical workflow:

1. Enter a game-relative `.tex` path and select **Inspect path**.
2. Acquire the native handle if you need to inspect its loaded resource.
3. Start lifecycle capture, trigger a fresh texture load, then stop and copy diagnostics.

The archive inspector reads the texture header and LOD-group metadata, and can selectively read a mip group. It validates group-to-mip mapping per file; selective reads currently support ordinary 2D textures only.

The D3D11 progressive preview uses a **plugin-owned** texture. It demonstrates individual mip uploads and coarse-to-fine preview, but does not stream or replace FFXIV's game-owned textures. Native lifecycle hooks are opt-in, observational, and version-sensitive. `ChangeLod` remains read-only; temporary loading pointers are not retained.

### Fluid Lab

Fluid Lab is an isolated GPU fluid-simulation experiment using plugin-owned D3D11 textures. Select **Create GPU fluid simulation** to allocate two 256×144 `R32G32B32A32_FLOAT` ping-pong state targets, a separate display target, settings/injection textures, SRV/RTV views, and embedded HLSL shaders. The simulation pass is recorded on a deferred context and executed with immediate-context state restoration. No FFXIV-owned render target is read or modified.

**Solver selection**

- **Thin-film / shallow-water** is the new experimental solver and the default. Each cell stores liquid depth, horizontal momentum, vertical momentum, and dye mass. A conservative first-order Rusanov finite-volume update transports depth and momentum through neighboring cell interfaces. Reflected boundary states prevent liquid from escaping through the four edges. Downward gravity adds vertical momentum; viscosity diffuses/damps velocity; an interface-normal/curvature capillary-force approximation pulls the liquid interface toward smoother, more coherent shapes. A bottom-wall ghost-phase condition introduces adjustable contact-angle behavior.
- **Simple transport (baseline)** preserves the previous gravity-transfer and lateral-diffusion model for direct comparison.

The thin-film model is deliberately a 2D approximation suited to a liquid coating on a screen. It is not a full 3D free-surface solver and does not model droplets as individual particles. The current wetting behavior is an approximate contact-angle ghost condition at the bottom boundary; it is not a general contact-angle treatment for arbitrary obstacles or moving 3D surfaces. The surface renderer reconstructs normals from the depth field and shades a translucent water-like surface, but it does not sample the game scene for true refraction. The first-order Rusanov scheme can still numerically diffuse thin fronts; later work can investigate higher-order reconstruction, explicit air/liquid interface tracking, and GPU-side impact/ripple diagnostics. There is no RealDebuffs integration yet.

**Controls**

- **Solver:** switches between the thin-film solver and original baseline. Switching clears the current field because their state interpretation differs.
- **Gravity:** downward acceleration for the thin-film model; downward transfer strength for the baseline.
- **Viscosity:** diffuses and damps momentum in the thin-film solver. Higher values slow and smooth motion.
- **Surface tension:** controls the curvature-based capillary force. Higher values encourage more cohesive interfaces; extremes may over-pull small pools.
- **Wetting strength / Contact angle:** controls the bottom-wall ghost-phase boundary condition. Lower angles favor wall wetting/spreading; higher angles favor beading. This currently applies only at the bottom edge.
- **Drop impact speed:** adds initial downward momentum to newly injected thin-film droplets. It is useful for comparing impact waves and splash-like spreading.
- **Surface detail:** changes the display-only surface-normal scale used for specular and Fresnel highlights.
- **Diffusion:** lateral mass equalization in the simple baseline only.
- **Simulation speed:** 0.25×–4× fixed-step simulation speed. The physics clock runs at 120 Hz, up to sixteen substeps per rendered frame.
- **High-contrast dye view:** changes only the separate display target; simulation depth and momentum remain unchanged. The normal water view uses a reconstructed translucent surface with highlights and transparent dry areas.
- **Drop left / center / right, Seed demo drops, Single-drop test:** queue radial liquid injections. Single-drop test clears the field first for repeatable comparisons.
- **Clear field:** clears both state targets and the display target, discards pending droplets, and resets fractional accumulated time.
- **Destroy simulation:** releases every probe-owned texture, view, shader, and D3D11 context without closing FPSBooster.

The CPU submission measurement includes parameter uploads, simulation command-list submission, and the display pass; it is **not GPU execution time**. There is no GPU readback or GPU-time query yet. The display texture is separate from the simulation state so visualization changes cannot alter the simulation. Switching away from the Fluid Lab tab pauses steps when it is the sole driver; while Grounded Fluid is enabled, its plugin draw hook advances the shared field independently of tab visibility.

**Current research goal:** determine whether the thin-film solver produces convincing falling, merging, impact, wetting, and pooling behavior that could eventually replace Dropsy's current water-specific special cases. It is still only a research prototype; do not treat its current visual output as production-ready fluid physics. GPU shader compilation and in-game behavior must be checked on the user's client before trusting the new contact-angle and rendering changes.

### Grounded Fluid

Grounded Fluid combines the existing 256×144 plugin-owned GPU fluid display texture with native BGCollision ground queries. It samples a configurable subdivided grid around a world anchor, maps each texture cell to the corresponding sampled ground quad, and projects those quads using the validated Scene ViewMatrix × Render ProjectionMatrix. The texture Y axis maps to the local downhill direction on slopes; nearly flat surfaces use projected player-forward as a stable orientation while tangential gravity is reduced to zero, avoiding artificial forward flow on level ground. A step-height threshold can reject individual cells that cross an abrupt discontinuity. The overlay is emitted by the plugin draw hook, so it can stay visible after the configuration window closes. The shared fluid simulation advances from that same hook while the overlay is enabled, and Fluid Lab does not submit a second step. With Scene Depth Capture running, a plugin-owned D3D11 composite pass compares each liquid fragment against the selected reversed-Z scene-depth SRV and hides fragments behind foreground geometry. If capture is stopped or no compatible single-sample SRV is available, the original depthless ImGui projection remains as a fallback. This is still a screen-space compositor rather than a native game-world mesh, but it can now respect captured scene visibility.

To try it: create the GPU simulation (or reuse the existing Fluid Lab field), select **Place / reset patch at player**, and use **Seed grounded pool**. On flat ground, water spreads without a fabricated forward gravity vector; on a slope, the field is oriented downhill and receives gravity proportional to the surface incline. The patch remains at its saved world position unless **Follow player after placement** is enabled. Fixed patches sample their ground mesh once and only refresh when requested; follow-player mode refreshes at the configured interval.

### Collision Lab

Collision Lab queries the FFXIV background-collision scene through `FFXIVClientStructs.FFXIV.Common.Component.BGCollision.BGCollisionModule`. It deliberately does not implement a world renderer: RealDebuffs already has its own 3D geometry engine, and this tab is only intended to discover physical contact data that engine can use.

- **Probe now** runs one downward raycast, one downward sphere sweep, and eight additional downward rays around the target (nine rays total in the 3×3 patch).
- **Target offset X/Z** moves the sample point in world axes relative to the local player's current position. It is not player-facing-relative.
- **Cast start height / maximum cast distance** control the downward query segment.
- **Sphere sweep radius** controls the radius stored in W of the sweep's Vector4 origin. The interop method is currently declared with a Vector3 pointer, so this call is intentionally marked experimental until verified in-game.
- **Ground sample spacing** controls the 3×3 neighborhood spacing. The displayed grid reports hit height and local triangle slope; the same points are also least-squares fitted to a plane with RMS error and total height range.
- **Collision layer mask** defaults to 1, matching the ClientStructs convenience wrapper. A zero mask may reject mesh colliders; use the default unless testing layer behavior.
- A hit report includes world-space contact point, distance, material value, collider pointer, native normal, and triangle vertices. If the native normal is unusable, the probe derives a fallback normal from the returned triangle. The collider pointer is only displayed and never dereferenced.
- **Live sample** repeats at 5 Hz while this tab is visible. It stops when the main FPSBooster window closes.
- **Per-sample surface normals** draws short normal ticks at each of the nine hits. **Grounded orientation frame** draws magenta up, yellow projected player-forward, and cyan surface-right axes. **Ground-frame up source** compares the exact center triangle normal against the 3×3 fitted neighborhood plane; if the selected source is unavailable, the frame explicitly falls back to the other usable source.

The desired RealDebuffs workflow is: choose a world-space point near the player, query its ground contact, then pass the returned contact point and normal into the existing tentacle/particle/mesh placement code. The 3×3 patch is also fitted to a least-squares local plane; its normal, slope, RMS residual, and height range help distinguish a smooth ramp from stairs or irregular terrain. The exact center hit normal is kept separately because it describes the local contact face, while the fitted plane represents the broader neighborhood.

The projected overlay uses only the user-validated active Scene Camera `ViewMatrix × RenderCamera.ProjectionMatrix` combination. The alternative Control ViewProjection and Render ViewMatrix combinations were removed after testing. The overlay can draw individual sample normals and a grounded orientation triad: up is taken from either the center hit normal or the neighborhood plane; forward is the player's heading projected onto that surface; right completes an orthogonal basis. This remains debug geometry and does not replace or modify RealDebuffs' existing 3D renderer.

This probe uses the game's native collision functions and is version-sensitive. Do not assume that a miss means the location has no visible ground: the collision layer mask, collision-scene streaming state, and object type can affect results. Compare the raycast and sphere sweep in several environments, including flat indoor floors, outdoor terrain, ramps, and stairs.

### VFX

The VFX tab creates native game AVFX objects and tests the native AVFX Camera-binder route. It does not draw replacement particles through ImGui.

**Quick workflow**

1. Initialize native VFX interop.
2. Open **Browse VFX**. The modeless selector stays open after selection, so you can click through assets without reopening it.
3. Choose **On selection**:
   - **Inspect only** — inspect binder metadata without spawning.
   - **Attach to player** — actor-bound spawn; default because many status/mount effects require actor context.
   - **Spawn at player position** — static/world-space spawn.
   - **Camera follow (native AVFX binder)** — the camera-follow route; details below. Previous effects stay active by default, and multiple Penumbra-injected paths can coexist.
4. Select an asset. The path and binder report load automatically. For spawn modes, the effect is created immediately. Previous previews are retained by default (up to 16 tracked native instances); enable **Clear previous VFX before auto-spawn** to replace the current visible preview set. Camera-binder injections use a separate uniquely tagged Penumbra temporary mod per game path. Those replacements are retained for the plugin session even after the visible VFX is cleared, allowing the same asset to be selected again. To return to an injected path's original actor/world resource, remove the temporary mappings and restart FFXIV before reusing that path, because the native resource cache may retain the modified AVFX bytes.

Manual paths remain supported. **Camera follow this path** runs the same native-binder workflow on the path field.

The selector also retains **Attach to player** and **Spawn at player position** for actor-bound and world/static effects. Existing non-Camera binders are not rewritten: automatic Camera-binder injection is limited to zero-binder AVFX assets. Camera distance/horizontal/vertical offset sliders remain visible as reserved controls, but native AVFX Camera binders currently own their placement and do not consume those offsets.

**VFX browser categories**

The selector builds a searchable catalogue from game sheets and referenced resource files:

- **Common** — VFX sheet entries plus named curated examples such as Ley Lines, Salted Earth, Passage of Arms, and Flamethrower.
- **Status** — status hit and looping effects.
- **Action / Non-Player Action** — direct start/cast effects plus AVFX references in animation-start, animation-end, hit, and weapon TMB timelines.
- **Emote** — AVFX references found in the emote animation PAP files used by VFXEditor's emote selector.
- **Zone** — AVFX references in each territory's `vfx.lgb`.
- **Gimmick** — AVFX references in gimmick action timelines.
- **Furniture / Housing** — AVFX references in indoor and outdoor furniture SGB files.
- **Item / Accessory** — weapon, armor, accessory, glasses, and offhand VFX IDs resolved through their IMC files.
- **NPC** — monster/demihuman model variants resolved through `ModelChara` and their IMC files.
- **NPC ID** — battle-NPC base IDs mapped to their `ModelChara` model and corresponding IMC VFX IDs.
- **Mount** — mount model VFX IDs resolved through their IMC files.
- **Cutscene / Journal Cutscene** — AVFX references in CUTB resources.

Resource files are scanned for null-terminated `.avfx` references using the same extraction convention as VFXEditor's selector. Results are validated against the game file index, cached per source resource, and deduplicated. The NPC and NPC ID categories are **model/IMC-derived subsets**: unlike VFXEditor's bundled `npc_files.json` index, they do not enumerate every NPC animation PAP/TMB VFX reference. Common includes the VFX sheet and curated named examples; it does not bundle VFXEditor's full `common_vfx` list. Some assets may therefore remain discoverable only through manual path entry. The source tab structure and curated Common examples are based on the upstream [VFXEditor selector](https://github.com/0ceal0t/Dalamud-VFXEditor), whose current categories include Item, Accessory, Status, Action, Non-Player Action, Emote, Zone, Gimmick, Housing, Npc, NpcID, Mount, Cutscene, Journal Cutscene, and Common.

**Camera follow: native AVFX Camera binder**

This is the preferred approach for camera-relative native particles. User testing confirmed that an existing Camera binder (`BnVr=3`) follows the camera and that appending a Camera binder to a zero-binder timeline AVFX through Penumbra can also produce a visible, camera-following effect.

When **Camera follow** is selected:

- If the original AVFX already contains a Camera binder, FPSBooster spawns it unchanged through the native scheduler.
- If it has zero binders and at least one `TmLn` timeline, FPSBooster copies the known-good Camera binder from the selected template, inserts it in the canonical AVFX root order, repairs `BdCn`, and assigns the new binder index to each timeline through `TmLn.BnNo`.
- The modified loose AVFX is supplied through a temporary Penumbra replacement scoped to the local player's effective collection. `ResolvePlayerPath` is checked before the original game resource path is passed to `VfxObject.Create`.
- Each modified game path gets its own uniquely tagged Penumbra temporary mod. Different injected AVFX paths can coexist, and each is verified through `ResolvePlayerPath`. Clearing visible previews, including **Clear previous VFX before auto-spawn**, removes native VFX instances only; it deliberately retains the Penumbra mappings and generated files so those paths can be spawned again during the same session. Use **Remove all temporary Penumbra replacements** to discard the mappings explicitly. Closing the VFX window or unloading the plugin also removes them. If Penumbra cannot confirm removal, the generated file is retained rather than deleted.

The default template is `vfx/common/eff/z5r2_fire_stlp_t0v.avfx`, Camera binder index `1` (zero-based), which was used successfully in the camera-follow injection test. You can choose another template through **Browse template**. Template selection fills the template path and detects the first Camera binder; it does not spawn the template.

Camera-follow injection intentionally supports only zero-binder AVFX assets. FPSBooster appends the known-good Camera binder and assigns it to every timeline in that target. Assets containing existing actor/world binders are not modified; use their original actor-attached or world-space spawn modes, or choose an asset that already has a native Camera binder. Some AVFX assets still require actor/model context or may not render as static effects, so compatibility remains asset-dependent.

**Resource-cache precaution:** use a game path that has not already been spawned during the current FFXIV session for its first Camera-binder injection. FPSBooster tracks paths it has spawned and blocks a new injection attempt when the native resource cache may contain earlier bytes. Once a path has a verified Penumbra replacement, that replacement is retained across preview clears and reused for subsequent Camera-follow spawns. If you explicitly remove a replacement and later want to restore or rebuild that path, restart FFXIV first; changing Penumbra mappings cannot guarantee that the native resource cache has discarded previously loaded AVFX bytes.

**Camera-binder comparison test**

The automated comparison has two timed phases: an existing native Camera-binder control and a Penumbra-injected Camera binder. Choose a known-good Camera-binder asset/template and a separate zero-binder timeline target that was confirmed visible through normal static/world spawning in a previous game session. Restart FFXIV and do not load the injection target before the test. Rotate the camera during both phases, then record the visual verdict and copy the report. A no-VFX result cannot establish follow/no-follow behavior; it means the target did not render and must be validated separately.

The inspector reports binder types and settings, including `BnVr=3`, AVFX-wide Camera Space (`bCmS`), Draw Layer (`DwLy`), and `Dst`/`DstR` distance curves. These are distinct mechanisms: Camera binder behavior is not the same as the asset's Camera Space or draw-layer flags.

## Research boundaries

- Native client structures and signatures can change with game updates. Initialize native probes explicitly and stop capture when finished.
- Texture lifecycle hooks call the original functions and collect copied metadata/pointer values; they are not a native texture replacement system.
- The plugin-owned D3D11 preview does not prove that FFXIV can progressively load its own texture resources.
- Camera-binder injection is confirmed for the tested template/target combination, but AVFX compatibility remains asset-dependent.
- Keep tests to one or a few VFX instances and clear them before changing zones or unloading.

## Milestones

| Version | Research milestone |
|---|---|
| 0.3.0.53 | Added Grounded Fluid: maps the GPU fluid display texture onto a collision-sampled and camera-projected terrain grid. |
| 0.3.0.62 | Captured the camera view-projection snapshot at each DSV bind and uses the selected DSV's matching snapshot to project grounded liquid for depth testing. |
| 0.3.0.61 | Restored direct reversed-Z device-depth ordering after view-space reconstruction failed on the live client; introduced a much smaller device-depth epsilon. |
| 0.3.0.60 | Experimental view-space depth reconstruction; superseded in 0.3.0.61 after live-client validation failed. |
| 0.3.0.57 | Added a plugin-owned depth-aware Grounded Fluid composite pass that samples the captured reversed-Z scene-depth SRV and hides liquid fragments behind foreground geometry. |
| 0.3.0.56 | Added viewport-matched scene-depth selection, manual DSV candidate choice, and a plugin-owned reversed-Z grayscale preview pass for single-sample depth SRVs. |
| 0.3.0.55 | Added R16_TYPELESS/D16_UNORM depth SRV support and expanded DSV candidate capacity. |
| 0.3.0.54 | Added opt-in D3D11 scene-depth candidate capture, texture inspection, and depth-SRV feasibility preview. |
| 0.3.0.52 | Fixed Collision Lab projection to the tested Scene View × Render Projection source and added 3×3 plane fitting, per-sample normals, and a grounded orientation frame. |
| 0.3.0.51 | Added camera matrix comparison diagnostics to isolate projected world-overlay jitter (retired in 0.3.0.52 after selecting the stable Scene×Render source). |
| 0.3.0.50 | Added projected collision overlay for ray, sphere sweep, and 3×3 patch results. |
| 0.3.0.49 | Fixed Collision Lab compile errors involving addresses of `out` parameters and the sweep-center report field. |
| 0.3.0.48 | Added Collision Lab with native BGCollision raycasts, sphere sweeps, and 3×3 ground sampling for RealDebuffs world-space grounding research. |
| 0.3.0.47 | Added approximate bottom-wall contact-angle wetting, initial droplet impact momentum, interface-normal capillary forces, and translucent specular water rendering. |
| 0.3.0.45 | Added fixed-step fluid transport, simulation-speed and diffusion controls, and high-contrast dye visualization to Fluid Lab. |
| 0.3.0.44 | Added an isolated D3D11 ping-pong fluid simulation lab with runtime HLSL compilation and droplet controls. |
|---|---|
| 0.3.0.38 | Added selected-timeline actor/mount AVFX conversion (retired in 0.3.0.43). |
| 0.3.0.37 | Expanded the VFX browser with item/accessory, named Common examples, emote, zone, gimmick, housing, NPC and NPC-ID model/IMC categories, cutscene, journal cutscene, and complete action timeline reference coverage. |
| 0.3.0.36 | Restored the non-mutating vanilla AVFX inspection helper required by the automated Camera-binder comparison. |
| 0.3.0.34 | Camera-binder injection inserts `Bind` in canonical root order, repairs `BdCn`, links each `TmLn` through `BnNo`, and restricts injection to zero-binder targets. |
| 0.3.0.33 | Automated camera-parenting and native/injected Camera-binder comparison with sampling and visual verdicts. |
| 0.3.0.32 | VFX selector remains open after selection and uses a modeless window without a full-screen modal dim overlay. |
| 0.3.0.31 | Searchable VFX selector with automatic inspection and one-click spawn modes. |
| 0.3.0.30 | Documentation consolidation. |
| 0.3.0.28–0.29 | Penumbra temporary AVFX replacement, Camera-binder injection, and collection/redirect verification. |
| 0.3.0.24–0.25 | Inverse-ViewMatrix camera tracking and render-cadence diagnostics. |
| 0.3.0.20–0.23 | Native AVFX spawning, actor attachment, and camera-relative experiments. |
| 0.3.0.0–0.3.0.18 | Native texture-lifecycle instrumentation and performance profiling. |
| 0.2.8–0.2.9 | SqPack selective mip reads and plugin-owned progressive D3D11 upload prototypes. |



## 0.3.0.50 — Collision Lab projected overlay

- Added an optional in-game debug overlay for saved collision results.
- Reprojects world-space cast/hit positions using the current game ViewProjection matrix every UI frame, so markers follow camera movement without rerunning the collision queries.
- Draws the ray segment, ground hit ring, recovered surface-normal line, sphere-sweep center/radius indicator, and connected 3x3 ground sample grid.
- The overlay is ImGui screen-space visualization only; it does not perform scene-depth occlusion and does not replace RealDebuffs' 3D renderer.
- Added toggles for the overall overlay, 3x3 patch, and sweep sphere.


## 0.3.0.51 — Camera projection comparison

Collision Lab now compares three live projection sources: `Control.ViewProjectionMatrix`, the embedded scene camera's `ViewMatrix × RenderCamera.ProjectionMatrix`, and `RenderCamera.ViewMatrix × RenderCamera.ProjectionMatrix`. It displays current pixel-space disagreement at the saved ground anchor, matrix element differences, the graphics `JitterMultiplier` (read-only), source-to-source pixel-delta variation, and raw frame-to-frame movement of the same fixed world anchor under each projection. The latter is useful for isolating temporal jitter when the player and camera are stationary. The overlay can be switched between the three matrices for visual A/B testing. No camera matrices or graphics settings are modified, and no smoothing is applied to world-space collision points.




## 0.3.0.52 — Collision surface alignment

- Removed the Control ViewProjection and Render ViewMatrix projection alternatives. Collision overlay now exclusively uses the user-validated active Scene Camera `ViewMatrix × RenderCamera.ProjectionMatrix` combination.
- Removed camera-source comparison UI, pixel jitter counters, matrix-difference reporting, and read-only jitter multiplier diagnostics.
- Added a least-squares local ground-plane fit over the 3×3 collision samples, including normal, slope, height range, and RMS fit error.
- Added comparison between the exact center hit's triangle/native normal and the neighborhood plane normal.
- Added a grounded orientation frame with orthogonal up/forward/right axes. Up can use the center contact normal or fitted neighborhood plane; forward is the player's heading projected onto the selected surface.
- Added per-sample normal visualization and a projected orientation triad for checking ramps, stairs, and uneven surfaces. This remains debug geometry only; RealDebuffs' renderer is untouched.


## 0.3.0.56 — Scene depth visualization and candidate selection (manual selection retired in 0.3.0.60)

- Replaced the raw single-channel ImGui depth preview (which displayed as red) with a plugin-owned RGBA8 grayscale conversion pass. It samples the captured single-sample R16/R24/R32 depth SRV through a fullscreen shader and applies a sublinear contrast curve for reversed-Z visualization.
- Added an eligible-DSV selector. Auto mode ranks candidates by viewport aspect ratio and dimensions instead of simply choosing the largest texture, avoiding accidental preference for large off-screen resources. Individual candidates can be selected manually for A/B inspection.
- The preview output is retained as a plugin-owned texture after capture stops, while the game-owned depth SRV is released. The output texture is never bound as a game render target.
- This remains diagnostic only: the grayscale pass does not yet depth-test the grounded liquid.


## 0.3.0.60 — Experimental view-space depth comparison (superseded in 0.3.0.61)

- Experimental change: reconstructed scene and liquid device depth into view-space Z using the current RenderCamera projection coefficients. This was superseded in 0.3.0.61 after the live client showed invalid comparisons.
- Preserved the correctly screen-linear device-depth interpolation in the CPU-rasterized patch map and reconstructs both values in the pixel shader.
- Locked DSV selection to viewport-matching Auto mode after manual alternate candidates showed camera-dependent flicker in testing.
- Added a diagnostic depth-difference view measured in yalms; also superseded in 0.3.0.61 with raw reversed-Z depth difference.
