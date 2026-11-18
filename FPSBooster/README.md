# FPSBooster — Progressive Texture Loading Investigation

## 0.2.9.9 — elapsed-budget hybrid with dominant-mip isolation

- Keeps elapsed-time budgeting as the default because the controlled 0.2.9.8 comparison was fastest: 129.09 ms / 6 UI frames, versus 178.35 ms / 8 frames for predictive budgeting and 327.21 ms / 14 frames for one-mip-per-frame. These are user-reported individual runs, not repeated statistical benchmarks.
- Adds an experimental fourth mode, **Elapsed + isolate largest mip**. It retains elapsed-time batching for ordinary mips but, when another mip has already been uploaded during the current UI frame, defers a mip whose payload is at least `max(2 MiB, 50% of the texture's total mip payload)` to a fresh frame. This is intended to prevent the dominant full-resolution mip from being combined with earlier uploads and creating a larger multi-mip submission spike.
- The new mode does not split an individual mip. A single large `UpdateSubresource` call can still exceed the configured CPU submission budget; the mode only isolates that call from other mip uploads in the same frame.
- Adds a separate large-mip-isolation deferral count and includes the threshold in the final scheduler summary. The existing predictive and elapsed modes remain available unchanged for comparison. The two-entry prefetch channel is unchanged.
- In the controlled 0.2.9.8 comparison, elapsed-time budgeting recorded 6.82 ms direct source read, 3.417 ms CPU upload submission, 25.43 ms to first preview, and 129.09 ms total (6 UI frames); it had one upload-starved frame, one multi-mip over-budget frame, and a maximum frame submission of 3.173 ms.
- Predictive budgeting recorded 7.54 ms direct source read, 4.058 ms CPU upload submission, 25.06 ms to first preview, and 178.35 ms total (8 UI frames); it deferred Mips 1 and 0, had one single-mip over-budget frame, and a maximum frame submission of 2.683 ms. The Mip 1 estimate (0.949 ms) exceeded the 0.674 ms remaining budget, although its actual submission was 0.779 ms; the policy therefore added a frame for a relatively small over-budget risk.
- One-mip-per-frame recorded 8.56 ms direct source read, 4.230 ms CPU upload submission, 24.60 ms to first preview, and 327.21 ms total (14 UI frames). It demonstrated the cost of strict one-mip-per-frame pacing.
- Interpretation: elapsed-time batching is currently the best measured throughput policy. Predictive scheduling lowers batch overrun risk but increases frame count, while one-mip-per-frame is substantially slower. The new large-mip-isolation mode is an untested compromise between elapsed batching and avoiding a large mip being combined with smaller uploads.


## 0.2.9.8 — controlled upload-scheduler comparison and restored two-mip prefetch

- Adds three selectable upload scheduling modes that all use the same two-entry background prefetch queue: one mip per UI frame, elapsed-time budget, and predictive budget. This makes scheduler comparisons controlled with respect to read-ahead depth.
- Makes elapsed-time budget the default because the user's previous 4K run using this policy completed in 151.97 ms / 7 UI frames, versus 202.30 ms / 9 UI frames in the first predictive-budget run. These are individual runs, not repeated statistical results.
- Restores the prefetch channel capacity from one completed mip to two. Version 0.2.9.7 changed the queue capacity at the same time as introducing predictive scheduling, so its A/B result was confounded. This release holds queue capacity constant across all three modes and lets the user compare elapsed-time and predictive scheduling directly.
- Adds two diagnostics: prefetch-empty polls (frames where the UI checked for a mip while the producer was still working) and upload-starved UI frames (empty polls where no mip could be uploaded that frame). These help distinguish scheduling deferrals from background-reader starvation.
- Elapsed-time mode submits at least one ready mip, then keeps submitting ready mips while the elapsed UI-thread upload phase remains under the selected budget. The final indivisible mip may overshoot the budget. Predictive mode estimates the next mip's submission cost and defers it when the estimate exceeds the remaining budget; it may be more conservative and increase frame count.
- One-mip-per-frame mode remains available as a baseline. All modes continue to use one background producer; D3D11 operations remain on the UI thread. Readback-verification mode remains one mip per UI frame because staging `Map(Read)` is synchronous.
- The prefetch queue remains bounded at two completed payloads. In addition, the UI scheduler can hold one pending mip, the producer can hold one mip being read, and the GPU probe retains the most recently uploaded payload for optional verification. This is a bounded pipeline, not a zero-copy pipeline; large 4K sources can temporarily require several mip buffers in CPU memory.
- Adds prefetch-starvation reporting alongside per-frame batch counts, budget deferrals, over-budget frame counts, and maximum frame submission time. The CPU budget remains a CPU-side `UpdateSubresource` submission budget, not a GPU execution-time or completion budget.
- No FFXIV-owned texture resources are modified. The full mip chain is still allocated in the plugin-owned test texture, so this experiment does not demonstrate reduced VRAM residency or native FFXIV texture streaming.

User-reported 4096×4096 BC7 Penumbra loose-file results:
- Version 0.2.9.6 elapsed-time batching: 8.10 ms direct source read, 4.195 ms summed CPU upload submission, first preview in 29.76 ms / 2 UI frames, and 324.21 ms / 14 UI frames in an initial baseline run; a later elapsed-time-budget Run A completed in 151.97 ms / 7 UI frames, with per-frame mip counts `0, 1, 3, 3, 3, 2, 1`.
- Version 0.2.9.7 predictive scheduling: 8.19 ms direct source read, 3.923 ms summed CPU upload submission, first preview in 34.53 ms / 2 UI frames, and 202.30 ms / 9 UI frames. Mip 0 was estimated at 3.431 ms with 0.221 ms remaining, deferred, then submitted alone in 2.315 ms. The estimate was therefore conservative for that 16 MiB mip. The run recorded one budget deferral and one single-mip over-budget frame.
- The 0.2.9.6 and 0.2.9.7 runs changed both scheduler policy and prefetch capacity (two queued mips versus one), so their wall-time difference cannot be attributed to prediction alone. Version 0.2.9.8 restores a shared queue capacity of two and exposes all three modes under the same conditions for a cleaner comparison.

## 0.2.9.6 — bounded mip prefetch and frame-budgeted upload scheduling

- Replaces the serial one-read/one-UI-frame sequence driver with one background producer that reads mip payloads coarse-to-fine and writes them into a bounded channel. The channel holds at most two completed payloads; the producer can additionally hold the payload currently being read, while the GPU probe retains the most recently uploaded payload for optional readback verification. This bounds prefetch memory rather than decoding the entire texture up front.
- The UI thread consumes ready payloads in mip order and remains the only thread that calls the D3D11 upload API. Source reads and SqPack decompression stay on the background worker.
- Adds an upload-only frame-budgeted mode, enabled by default, with a configurable 0.25–4.00 ms CPU-side `UpdateSubresource` submission budget. Multiple already-prefetched mips may be submitted in one UI frame until the budget is reached. An individual mip upload is indivisible, so a large mip may exceed the selected budget by itself.
- Preserves a one-mip-per-UI-frame baseline mode for comparison. The first coarse mip is always given a full UI frame to be drawn before batching later refinements.
- Readback-verification mode deliberately remains limited to one mip per UI frame because its staging `Map(Read)` path is synchronous and can stall the UI.
- Refreshes the progressive preview SRV once per uploaded batch instead of rebuilding it after every mip within that batch. The first coarse mip still receives its own view immediately and is given a full UI frame to be drawn.
- Adds sequence status reporting for the number of mips uploaded in the current UI frame, the per-frame batch counts, and the scheduling mode/budget used in the final totals.
- The prefetch queue is cancelled when the sequence is stopped, the inspected source changes, the window closes, or the plugin is unloaded. It does not change Penumbra settings or touch game-owned texture resources.
- The CPU budget is not a GPU-time budget: `UpdateSubresource` timing measures CPU submission, not the time the GPU spends executing the copy. The plugin-owned test texture still reserves its complete mip chain up front and does not demonstrate reduced VRAM residency or native FFXIV texture streaming.

User-reported 4096×4096 BC7 loose Penumbra replacement baseline (0.2.9.5): 13/13 mip ranges validated; 21.33 MiB read directly in 8.10 ms; summed CPU upload submission 4.195 ms; first preview SRV ready in 29.76 ms (2 UI frames); end-to-end sequence 324.21 ms (14 UI frames). These are one-machine measurements; 0.2.9.6 adds bounded prefetch and frame batching to test whether frame cadence and serial read scheduling can be reduced.

## 0.2.9.5 — direct absolute loose TEX file input

- The texture input now accepts either a game-relative asset key (for vanilla SqPack inspection and optional Penumbra `ResolvePlayerPath` lookup) or an absolute filesystem path to a loose `.tex` file.
- Absolute paths are inspected directly. This path does not call Penumbra IPC, attempt to derive a game-relative key from mod-folder layout, or acquire a native `ResourceHandle`.
- Direct-file inspection reads filesystem metadata and the bounded TEX/container header, builds the same per-mip `OffsetToSurface` range report, and allows individual raw loose-mip reads through the existing reader.
- Valid direct-file mip payloads can feed the existing plugin-owned BC7 D3D11 upload, readback-verification, and progressive preview experiments. Full-chain upload requires every mip range to be in-bounds and non-overlapping; gaps and trailing padding remain allowed.
- The native FFXIV resource-handle observer remains available only for game-relative paths, because an arbitrary absolute mod-file path is not a valid native game resource key.

## 0.2.9.4 — per-mip loose TEX range diagnostics and partial reads

- Correctly interprets `TexHeader.OffsetToSurface[i] == 0` as an absent mip entry, following Lumina's `TexHeader` definition, rather than treating zero as a corrupt range that invalidates every mip.
- Adds a per-mip status table showing the raw source offset, expected payload bytes, computed end, and whether each mip is readable, absent, before the header, empty, overflowing, or beyond EOF. The table is visible even when the overall mip chain is incomplete.
- Enables an individual selective read and manual upload for any mip whose own nonzero `OffsetToSurface` range is in-bounds. A missing or malformed mip no longer blocks reading a different valid mip.
- Keeps the automated coarse-to-fine full-chain sequence disabled unless every declared mip has a valid nonzero range. Partial chains are never reported as complete or streamed through missing mip indices.
- The GPU experiment now targets the resolved Penumbra replacement whenever at least one mip is individually readable, so manual uploads use the effective replacement dimensions instead of silently falling back to the vanilla SqPack texture. The full sequence still requires complete-chain validation.
- Overlapping nonzero source ranges remain visible as a warning; each manual read uses exactly the selected mip's header offset and calculated payload size. No offsets are inferred or rewritten.

## 0.2.9.3 — Honor loose TEX per-mip offsets

- Fixed an overly strict loose-file validation rule. `TexHeader.OffsetToSurface[i]` is the authoritative absolute byte offset for mip `i`; loose TEX files are not required to store every mip in one gap-free chain immediately after the 80-byte header, and file length need not equal header size plus the sum of mip payload sizes.
- Validates every mip range independently: offset must be at or after the 80-byte header, payload size must be positive, and offset + payload size must fit inside the resolved file. Per-mip gaps, padding, and trailing bytes are allowed.
- Displays a per-mip offset/payload/range table in the Penumbra source inspector. Potential overlaps are reported as a warning rather than silently inferred away; every read uses the exact offset supplied by the header.
- Loose-file mip reads and progressive GPU upload now use each mip's explicit source offset and retain file-length / last-write-time snapshot checks.


## 0.2.9.2 — Penumbra loose-file selective mip reads and progressive GPU upload

- Added a selective reader for Penumbra-resolved loose `.tex` replacements. It reads the 80-byte `TexHeader`'s `OffsetToSurface[13]` table and uses the absolute per-mip file offsets recorded by the texture itself; it does not infer locations by summing mip sizes.
- Enables the loose-file progressive path when every active mip has an independently valid absolute source range within the file. Gaps, alignment padding, trailing bytes, and even unusual overlaps are surfaced rather than assumed away; each selected mip is read from its exact header-provided offset.
- Revalidates the source file length and last-write timestamp immediately before and after every mip read, rejects non-2D textures and any payload range outside the validated file, and computes a SHA-256 fingerprint for the selected raw mip. Loose-file data is already decompressed; no SqPack block parsing or decompression is performed.
- Added **Read resolved mip only** to the Penumbra source panel. It reports bytes read, elapsed read time, payload size, and SHA-256 independently from the vanilla SqPack reader.
- The plugin-owned BC7 GPU experiment automatically targets the validated Penumbra loose replacement when one is resolved. The existing manual upload, progressive coarse-to-fine upload-only, and upload-plus-readback verification paths now accept that source as well as validated vanilla SqPack groups.
- The progressive sequence still reads and uploads one mip per UI frame, exposes only the contiguous range of initialized mips through the preview SRV, and uses a plugin-owned D3D11 texture. It does not modify the effective native FFXIV texture or Penumbra's files/settings.
- Type-4 replacement containers remain metadata-only in this build; selective reading from their compressed groups has not yet been implemented. Non-BC7 formats and non-2D textures remain unsupported by the GPU upload probe.

## 0.2.9.1 — Penumbra effective-source path and metadata inspection

- Added a manually requested call to Penumbra's public `Penumbra.ResolvePlayerPath` IPC. It resolves the inspected game path against the local player's active collection and returns the currently supplying path when Penumbra can resolve one. This is specifically player-collection scoped; it is not a universal resolver for NPC, world, interface, or arbitrary object resources.
- The probe makes no changes to Penumbra collections, mod states, priorities, or options. It does not acquire another native game resource handle and does not touch native D3D11 resources.
- If the returned path is a directly accessible file, the probe reads only filesystem metadata plus either the first 80 bytes (loose TexHeader) or the leading Type-4 file header, mip descriptors, shared block-stride table, and TexHeader. Type-4 header metadata is capped at 1 MiB to avoid unbounded synchronous reads. It never reads or decompresses mip payloads in this version.
- Recognizes two candidate source layouts: a loose decompressed texture with its 80-byte TexHeader at offset 0, or a Type-4 texture container with a leading 24-byte descriptor header and TexHeader at the declared header end. Unknown or inaccessible path forms are reported rather than guessed.
- For Type-4 containers, reports per-mip compressed offset/size, decompressed size, block-table offset/count, validates that group data begins after the TexHeader and remains in-bounds/non-overlapping, checks every signed 16-bit stride entry and each group's stride sum against its compressed size, and maps groups to mip levels only when each decompressed size matches Lumina's calculated mip payload in order. The container's declared raw size must also equal the calculated TexHeader plus mip payload size before strict mapping is accepted.
- For loose files, reports whether every `OffsetToSurface` entry describes an in-bounds mip payload. Gaps, padding, and trailing bytes are allowed; suspicious overlaps are displayed separately.
- No replacement mip payload is read or sent to the plugin-owned GPU texture yet. The next step is to validate the resolved source layout against the user's Penumbra hair-mask replacement, then add an isolated selective replacement-mip reader before reusing the already-tested upload/preview path.

## 0.2.9.0 — vanilla SqPack vs effective native texture comparison

- Expanded the native texture observer with a side-by-side comparison between the inspected vanilla SqPack `.tex` header and the effective texture returned by the game's native `ResourceHandle.GetAsync(path)` route.
- The comparison reports resolution, mip count, format, and mip payload size. For effective native BC7 2D textures, it calculates a tight block-payload estimate from the native texture dimensions, mip count, and array size. This estimate is not measured GPU allocation, residency, or driver padding.
- A detected mismatch is described as a difference between the vanilla archive entry and the effective in-game resource after active redirection hooks (including Penumbra in the user's setup). It is not treated as a SqPack parsing error, and it does not identify the exact mod or replacement file.
- User-confirmed sample: vanilla SqPack metadata reports 1024×512 BC7 with 10 mips and 682.7 KiB of mip payload; the effective native resource reports 4096×2048 BC7 with 13 mips. A tight BC7 estimate for the effective chain is 11,184,848 bytes (about 10.67 MiB), approximately 16× the vanilla mip payload. The native `FileSize` field was observed as 11,184,904 bytes; the 56-byte difference is recorded as a correlation only, not a confirmed interpretation of that raw field.
- No native D3D11 object is wrapped, AddRef'd, released, mapped, sampled, or modified. `ChangeLod` remains read-only and its semantics remain unknown.

## 0.2.8.9 — native texture-resource handle observer (baseline)

- Added a manually started native texture-handle probe for the inspected `.tex` path. It calls the game's `ResourceHandle.GetAsync(path)` API, which may reuse a cached handle or initiate the normal asynchronous native load; this is an explicit native resource request, not a passive lookup.
- Validates the returned object's runtime handle size against the current `TextureResourceHandle` layout before casting. A mismatch releases the returned reference and stops the probe.
- While the handle reference is held, the Textures tab reports its address, runtime size, raw reference count and file-size fields, last I/O result, `.tex` header metadata, the raw byte at `+0x104` (bit 0 is currently exposed by FFXIVClientStructs as `ChangeLod`), and the persistent `Texture` object's dimensions/format/flags plus its native D3D11 texture/SRV pointer values.
- The observer never writes `ChangeLod`, calls `Load`, `Reread`, or `LoadIntoKernel`, wraps/AddRefs/releases the native D3D11 pointers, or reads `TextureWhileLoading` / `DataWhileLoading` (documented as valid only during `Load`).
- The probe holds the returned resource reference until the user presses Release, changes/clears the inspected file, closes the window, or unloads the plugin; it balances the acquisition with one `ResourceHandle.DecRef()` call. No game-owned texture contents or GPU resources are modified by this observer.
- Native fields are displayed as raw observations where their semantics are undocumented. In particular, the meaning of `ChangeLod` is still unknown; seeing its value does not establish what changes it or what effect it has.

User-reported timing for the first progressive-preview run on the 1024×512 BC7 hair-mask asset: first preview SRV ready in 24.67 ms over 2 UI frames; full sequence wall time 249.73 ms over 11 UI frames. Summed archive read/decompression was 4.26 ms and summed CPU upload submission was 0.143 ms. These measurements show a large gap between summed work and total wall time, but the wall time also includes test-texture allocation, async read waits, and per-UI-frame pacing. It is not a direct measurement of in-game texture load latency. The user commonly encounters 512-pixel game textures and will supply a larger Penumbra-scale asset for later stress testing; the current sample is not representative of 2K textures.

## 0.2.8.6 — progressive mip-range preview

- The plugin-owned BC7 preview now becomes available after the first successful mip upload instead of waiting for the entire chain.
- After each upload, FPSBooster recreates its shader-resource view with `MostDetailedMip` and `MipLevels` restricted to a contiguous range of initialized subresources. The coarse-to-fine sequence therefore begins by exposing only the coarsest mip, then progressively expands the view toward Mip 0.
- Manual out-of-order uploads expose only the contiguous run containing the newest uploaded mip; gaps are never exposed to the shader sampler.
- The preview reports its currently visible mip range and updates as finer levels arrive. If expanding the view fails, the previous valid view is retained.
- The texture itself still reserves its complete mip chain at creation. This demonstrates progressive availability/sampling on a plugin-owned resource, not reduced VRAM allocation or integration with FFXIV-owned texture resources.

The first upload-only run on the 1024×512 BC7 hair-mask texture completed all ten mips and produced a visible ImGui preview. The user-reported aggregate was 454.3 KiB of logical archive bytes requested, 682.7 KiB of mip payload, 4.35 ms read/decompression, and 0.158 ms total CPU upload submission. This is one test asset and should not be generalized to other texture sizes or formats.

## 0.2.8.5 — upload-only sequence and ImGui sampling preview

- Added a coarse-to-fine **upload-only** run alongside the existing upload + byte-verification mode. Both process one mip at a time, smallest to largest, using the archive-only selective reader and the same plugin-owned BC7 D3D11 texture.
- Upload-only mode skips staging texture allocation, `CopySubresourceRegion`, `Map(Read)`, and byte comparison. Its totals separate archive read/decompression time from CPU-side `UpdateSubresource` submission time, making it the useful benchmark for the intended progressive upload path. It still does not measure GPU completion.
- Once every mip has been uploaded, the probe creates a D3D11 shader-resource view and shows a preview through Dalamud's ImGui D3D11 renderer. The preview is deliberately withheld until the complete mip chain is initialized, avoiding sampling uninitialized subresources.
- The shader-resource view is released before its texture resource. Preview creation is optional; a view-creation error does not invalidate successful mip uploads or earlier readback results.
- The preview and both sequence modes use only the plugin-owned test resource. They do not hook, replace, or modify FFXIV-owned textures.


## 0.2.8.4 — coarse-to-fine GPU upload/readback sequence

- Added a manually started **Run coarse-to-fine upload + verify** diagnostic for validated ordinary 2D BC7 textures.
- The sequence recreates a clean plugin-owned D3D11 test texture, then iterates from the highest mip index (smallest mip) down to Mip 0. For each level it independently reads only that SqPack group, uploads its bytes to the matching D3D11 subresource, copies that subresource to a temporary staging texture, and compares the mapped BC7 block rows byte-for-byte against the exact archive payload.
- The sequence refuses to start unless the archive groups have a strict one-to-one mip mapping. Unsupported formats, texture types, in-flight reads, and failed reads stop the sequence rather than guessing.
- Archive decompression stays on the existing background task. D3D11 upload, staging copy, and synchronous Map(Read) verification stay on the UI/framework draw thread. The manual sequence can briefly pause the UI during each readback; it is a diagnostic, not a frame-time feature.
- Displays per-mip upload/readback results and aggregate logical archive bytes, decompressed mip bytes, read/decompression time, CPU upload submission time, and synchronous readback/compare time.
- The test texture still allocates the full mip chain up front. This does not demonstrate reduced VRAM residency or modify FFXIV-owned textures.

## 0.2.8.3 — D3D11 readback compile fixes

- Retain the freshly allocated selective-mip byte array directly for the optional GPU readback comparison. The previous `ToArray()` call was invalid because the payload is already a `byte[]`; cloning it would also waste memory without adding safety because the read result owns the buffer and the probe does not mutate it.
- Fully qualify `Vortice.Direct3D11.MapFlags.None` to resolve the name collision with `Vortice.DXGI.MapFlags`.
- No GPU upload, copy, map, or verification behavior changed.


## 0.2.8.2 — explicit GPU subresource readback verification

- Added a manual **Read back & verify last upload** action to the plugin-owned BC7 D3D11 probe.
- The probe creates a temporary CPU-readable staging texture with the same full mip-chain layout (needed so very small BC7 mip dimensions remain valid subresources), copies only the last uploaded mip subresource, maps that subresource, and compares each BC7 block row byte-for-byte against the exact CPU payload submitted to `UpdateSubresource`. The staging allocation is temporary; other mip subresources are not copied or mapped.
- Reports the compared byte count, mapped row pitch, first differing byte offset on failure, and total copy/readback/compare time.
- Only the most recently uploaded CPU mip payload is retained, avoiding a managed copy of the entire texture's mip chain.
- Readback is intentionally manual: `Map(Read)` is synchronous and can wait for queued GPU work. It is a diagnostic, not a per-frame operation.
- No FFXIV-owned texture resources are accessed or modified.

The first live upload test submitted Mip 5 (32×16, 512 bytes) to subresource 5 in 0.013 ms of CPU-side submission time. A subsequent manual staging readback matched all 512 bytes. A full coarse-to-fine verification run then uploaded and read back all ten mip levels successfully. The user's measured aggregate for that run was 454.3 KiB of logical archive bytes requested, 682.7 KiB of decompressed mip payload, 15.08 ms summed read/decompression time, and 0.666 ms summed CPU upload submission time. Per-mip staging readbacks took roughly 2.7–11.6 ms and dominated the total diagnostic time; these are synchronous diagnostic timings, not the intended upload-only runtime path. This is a single 1024×512 BC7 hair-mask sample, not yet a general performance claim.

## 0.2.8.1 — Vortice upload-call compatibility fix

- Pass the pinned mip buffer to Vortice `UpdateSubresource` as `IntPtr`, matching the Vortice API overload used by its D3D11 samples.
- Use `in` for the COM interface IID passed to `Marshal.QueryInterface`, removing the `ref`/`in` analyzer warning.
- No upload behavior or ownership semantics changed.

## 0.2.8.0 — plugin-owned D3D11 upload prototype

The Textures tab now adds an opt-in GPU experiment for ordinary 2D BC7 textures:

1. Inspect the `.tex` file using only the SqPack entry header, LOD-group descriptors, and texture header.
2. Read an individual mip group directly from its `.dat` file. The reader decompresses only that group's blocks and retains the selected mip bytes for the optional upload step.
3. Create an **empty plugin-owned D3D11 texture** with the same dimensions and mip count.
4. Upload the last-read mip to its matching D3D11 subresource using `ID3D11DeviceContext.UpdateSubresource`.
5. Repeat for other mip indices and observe per-call CPU submission time and the number of distinct mip levels submitted.

### GPU prototype boundaries

- It uses `IUiBuilder.DeviceHandle` and queries the `ID3D11Device` interface. The device wrapper owns only the COM reference obtained by `QueryInterface`.
- The allocated texture belongs to FPSBooster. It is not an FFXIV texture, and no game-owned texture handle, resource, shader view, or loading state is modified.
- The prototype currently accepts only `TextureType2D` BC7 assets. Other game texture formats and texture types remain unsupported until their DXGI mapping and pitch rules are verified.
- Creating the test resource allocates the **complete mip chain up front**. This experiment tests independent subresource uploads; it does not yet demonstrate reduced GPU allocation or residency.
- The displayed upload time is CPU-side `UpdateSubresource` submission time, not GPU completion time. D3D11 may queue/copy the actual transfer asynchronously.
- A D3D11 SRV can be displayed through the ImGui D3D11 renderer as soon as the first (coarsest) mip is uploaded. The view is restricted to contiguous initialized mip levels and expands as finer mips arrive. The optional visual preview is only for the plugin-owned resource and is not an FFXIV texture replacement. The preview was visually confirmed to begin blurry and sharpen as finer mips arrived.
- Reset the GPU test resource when changing assets. Closing the configuration window or unloading the plugin releases the test texture, context, and device references.

The project adds `Vortice.Direct3D11` 3.8.3 for the D3D11 wrapper API. Dalamud exposes the game's active D3D device through `IUiBuilder.DeviceHandle`; the D3D11 API supports updating an individual texture subresource with a source pointer and row/depth pitch.



Dalamud plugin that cuts rendering work in FFXIV housing by hiding furniture you can't see, with an ongoing investigation into progressive texture loading.


## Native texture-resource lifecycle research (read-only)

Current FFXIVClientStructs definitions provide a useful map of the native handoff, but not enough evidence to modify it safely:

- `TextureResourceHandle` has an 80-byte `TexFileHeader` at offset `0xB0`, a `ChangeLod` bit at offset `0x104` (bit 0 of the byte at that offset), and a persistent `Texture*` at offset `0x128`.
- `TextureWhileLoading` at `0x130` and `DataWhileLoading` at `0x148` are explicitly documented as valid only during `TextureResourceHandle.Load(void*, byte)`. FPSBooster must not retain or inspect those temporary pointers outside that call.
- The renderer `Texture` structure exposes actual dimensions, a mip-level byte, format/flags, and the native `ID3D11Texture2D1` and `ID3D11ShaderResourceView1` pointers at offsets `0x68` and `0x70`.
- The base `ResourceHandle` lifecycle exposes `Read`, `Load`, `Reread`, `FinishLoad`, `LoadIntoKernel`, and `LoadDependentResources`. `FinishLoad` is documented as coordinating dependent-resource loading, freeing temporary `Blob` data, returning file handles, and then calling `Load`.

The public definitions do **not** document what setting `ChangeLod` does, when it is read, whether it triggers a reread/reallocation, or what synchronization protects the texture pointer. The next native-facing experiment should therefore be a manually requested, read-only observation of a native `.tex` handle's stable fields and state transitions. Do not set `ChangeLod`, replace its `Texture` pointer, write its D3D11 resource, or retain the temporary `TextureWhileLoading` / `DataWhileLoading` pointers until the relevant game call path and lifetime rules are verified.

Source definitions: [TextureResourceHandle.cs](https://raw.githubusercontent.com/aers/FFXIVClientStructs/main/FFXIVClientStructs/FFXIV/Client/System/Resource/Handle/TextureResourceHandle.cs), [Texture.cs](https://raw.githubusercontent.com/aers/FFXIVClientStructs/main/FFXIVClientStructs/FFXIV/Client/Graphics/Kernel/Texture.cs), and [ResourceHandle.cs](https://raw.githubusercontent.com/aers/FFXIVClientStructs/main/FFXIVClientStructs/FFXIV/Client/System/Resource/Handle/ResourceHandle.cs).

## 0.2.8.8 — progressive preview timing

The coarse-to-fine sequence now records two additional wall-clock metrics:

- **Time to first preview SRV ready:** elapsed time from starting the sequence until the coarsest mip has been read, uploaded, and exposed through a valid shader-resource view. This is not literal photon/display latency; the ImGui renderer samples it on a subsequent draw.
- **End-to-end sequence wall time:** elapsed time from pressing the sequence button until the final mip upload is submitted, including test-resource allocation, asynchronous read waits, and the one-step-per-UI-frame cadence.

These measurements are intentionally separate from summed archive read/decompression time and CPU-side `UpdateSubresource` submission time. Neither CPU upload submission nor SRV readiness proves GPU completion. The metrics are intended to quantify first-usable-detail latency and full-detail completion across larger test assets.

The first user-tested progressive preview on the 1024×512 BC7 hair-mask asset visibly appeared at low detail and sharpened as finer mips arrived. That run reported 454.3 KiB of logical archive bytes requested, 682.7 KiB of mip payload, 5.44 ms summed read/decompression, and 0.137 ms summed CPU upload submission. These are user-reported measurements for one asset; the new wall-clock metrics are needed to quantify the actual elapsed time across UI frames.

## Use

- `/fpsbooster` opens settings.
- `/fpsbooster on` / `off` toggles furniture culling. Off restores all furniture.
- `/fpsbooster test` opens settings and points to the hold-to-test buttons.

## Housing settings

| Setting | Default | Effect |
|---|---:|---|
| Enable furniture culling | On | Master switch. |
| Hide furniture outside the screen | On | Hides furniture outside the camera view. |
| Extra room around the screen | 12° | Keeps furniture just off screen visible to avoid pop-in. |
| Always show nearby furniture | 6 yalms | Keeps furniture close to the camera visible. |
| Maximum distance | Off | Hides furniture farther than this from the camera. |
| Maximum height difference | Off | Hides furniture this far above or below the camera (other floors). |
| Show all furniture in GPose | On | Pauses culling in GPose. |
| Pause if my character is off screen | On | Safety check. Turn off when using a free camera. |

Distances are measured from the camera. A value of 0 shows as Off.

## Texture investigation

The Textures tab is now an archive-only inspection and selective-read experiment.

### Metadata-only inspection

`Inspect .tex` resolves the game path through Lumina's in-memory repository/category indexes, then reads only:

- The 24-byte SqPack file-entry header.
- The entry's 20-byte-per-record LOD-group descriptor table.
- The leading 80-byte `TexFile.TexHeader` from the raw texture data region.

It does **not** call `IDataManager.GetFileAsync<TexFile>`, allocate a buffer for the complete `RawFileSize`, read mip block payloads, or decompress any mip levels. Mip dimensions and payload sizes are calculated from the header using Lumina's `TexFile.SliceSize` logic; constructing a metadata-only `TexFile` instance with its parsed header is sufficient for those calculations.

The inspector reports base dimensions, texture format/type, mip payload sizes, High / Medium / Low starting mip indices, and the archive LOD-group layout. It proposes a one-to-one group-to-mip mapping only when group count and every group's decompressed size match the calculated mip payloads in order and all data ranges are valid.

### Selective mip read

`Read mip only` reads the selected group's signed 16-bit block-stride entries and that group's compressed data blocks from the owning `.dat` file. It decompresses only that mip group, verifies the output length against the mip size calculated from the 80-byte header, and reports the SHA-256 digest, observed SqPack block types, stored bytes, block-table bytes, decompressed bytes, and read/decompression time. The byte counters represent logical bytes requested by the reader; filesystem caching and read-ahead mean they are not a measurement of physical device I/O.

The selected-group reader mirrors Lumina's current SqPack behavior:

- The 24-byte `SqPackFileInfo` is followed by one 20-byte `LodBlock` record per texture group.
- A shared table of signed 16-bit block strides follows those records. Each group's `BlockOffset` and `BlockCount` identify its entries in that table. Non-positive or header-short strides are rejected.
- `CompressedOffset` identifies the first data block relative to the start of the entry's data region.
- Each data block starts with a 16-byte `DatBlockHeader`. Lumina treats type `32000` as raw/uncompressed data and sends every other type value through Deflate decompression. FPSBooster mirrors that behavior.
- The reader validates block-table bounds, each block's stride/data bounds, total stored stride against the LOD record's `CompressedSize`, and total decompressed output against `DecompressedSize`.

The metadata-only path currently requires the Win32 texture layout to begin with at least the 80-byte `TexHeader` prefix; shorter or inconsistent prefixes are rejected. Selective mip reads currently support ordinary `TextureType2D` textures only. Cube maps, 2D arrays, volume textures, and nonstandard/reference-block cases are rejected rather than guessed.

### Validation so far

Before removing the full Lumina read from the normal Inspect path, the previous version compared selected groups against Lumina's fully loaded `TexFile.SliceSpan` reference. For the tested 1024×512 BC7 hair-mask texture, Mips 0, 5, and 9 all matched byte-for-byte. This established the selective group's byte layout for three mip levels. The current version no longer loads that full reference during normal use; it reports output size and SHA-256 so repeated selective reads can be measured without reading all other mips.

A tested ten-mip texture had SqPack expanded group sizes of 512 KiB, 128 KiB, 32 KiB, 8 KiB, 2 KiB, 512 B, 128 B, 32 B, 16 B, and 16 B, matching Mip 0 through Mip 9 in order. Lumina's `ReadTextureFile` loops through those group records in order and decompresses their blocks sequentially into the texture buffer. The inspector validates the same relationship per file instead of assuming it universally.

### Remaining work

This is not yet an end-to-end progressive texture loader. The new D3D11 experiment submits individual mips to a separate plugin-owned resource, but does not affect game-owned textures or their residency. The coarse-to-fine sequence now exercises every mip on a plugin-owned D3D11 resource. Verify mode reads each one back for byte comparison; upload-only mode avoids the staging readback cost and is the better timing baseline. The preview is sampled by the ImGui D3D11 renderer as soon as the first mip is uploaded; its SRV exposes only contiguous initialized mip levels and expands as finer mips arrive. The next step is to compare upload-only timings across larger assets and continue tracing the native `TextureResourceHandle` read/load lifecycle. Sequence telemetry now separates summed worker read/decompression and CPU upload-submission time from end-to-end wall time and time until the first preview SRV is ready; the latter includes background task scheduling and UI-frame cadence. Staging readback verifies the exact bytes stored in the selected plugin-owned GPU subresource, but it does not prove the game's own texture loader can stream mips. Its `ChangeLod` property is exposed as a bitfield at offset `0x104`, but the public struct definition does not explain when the game sets it or how it affects `Read`, `Reread`, `Load`, or GPU texture creation. Do not modify it until those semantics and resource-ownership rules are established. Any eventual runtime hook should be opt-in and retain a reliable pass-through path.

References: [Lumina SqPackStream](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/SqPackStream.cs), [Lumina TexFile / TexHeader](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Files/TexFile.cs), [Lumina SqPack file structures](https://github.com/NotAdam/Lumina/blob/master/src/Lumina/Data/Structs/SqPackFileInfo.cs), and [FFXIVClientStructs TextureResourceHandle](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/System/Resource/Handle/TextureResourceHandle.cs).

## 0.2.7.9 — archive-only inspection

Normal texture inspection no longer obtains `TexFile` through `GetFileAsync<TexFile>`. It resolves the owning `.dat` entry, reads the SqPack entry/LOD metadata plus the 80-byte texture header, and computes mip sizes from the parsed header only. `Read mip only` independently decompresses the chosen group and reports a SHA-256 digest and byte/timing counters. Counters show logical byte requests, not guaranteed physical device reads. This removes the previous full-texture read from the normal investigation loop.

## Good to know

- Indoors, each furnishing is hidden whole using its bounding sphere. Outdoors, the game's own culling is used when available and FPSBooster only adds your distance/height limits.
- Furniture with an active spotlight, and furniture within 0.75 yalms of one, stays visible so shadows don't disappear. Maximum distance/height override this.
- Culling is camera-view only, so furniture behind a wall stays visible while in view. Use the distance/height limits to cut other floors.
- The hold-to-test buttons on the Housing tab hide furniture only while held and are never saved.
- Anything hidden is restored when culling is off, paused, or the plugin unloads.

## Build

.NET 10 SDK with Dalamud dev hooks (`DALAMUD_HOME`), then `dotnet build -c Release`.
