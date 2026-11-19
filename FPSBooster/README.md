# FPSBooster 0.3.0.19

## 0.3.0.19 — profiling snapshot compile fix

- Fixes the duplicate positional parameter in `PerformanceProfileSnapshot` by naming the histogram bucket `FramesAtLeast100Milliseconds`, while retaining `FramesOver100Milliseconds` for the strictly-greater-than-100-ms count. The UI and copied report now use the corresponding property for each metric.
- Bumps the plugin version to 0.3.0.19.

# FPSBooster 0.3.0.18

## 0.3.0.18 — opt-in frame-pacing profiler and culling CPU timing

- Adds a **Performance** tab with a bounded capture of time intervals between `IFramework.Update` callbacks. The sample is a framework frame-pacing proxy; it is not a direct measurement of game main-thread execution, GPU execution, or presentation latency.
- Reports average frame interval/FPS, P50/P95/P99 intervals, worst interval, 1% and 0.1% lows (computed from the average interval of the slowest 1%/0.1% of samples), and counts of intervals above 33.3, 50, and 100 ms.
- Times only `CullingService.OnUpdate/Tick` while a performance capture is active. Timing is opt-in, resets for each capture, and is disabled when the capture stops. This helps determine whether FPSBooster's existing housing-culling update is itself contributing meaningful CPU cost.
- Tracks whether the native texture lifecycle hooks were enabled during each sampled frame, so a report warns when hook instrumentation may have affected the capture.
- Adds clipboard export for the complete performance report. Captures continue with the configuration window closed, are capped at 18,000 framework updates (approximately five minutes at 60 FPS), and stop automatically at the limit.
- Recommended comparison: capture the same scene/camera/activity with housing culling off and on, with native texture lifecycle capture disabled for both baseline runs. Run a separate instrumented pass when measuring individual texture operations.
- This release adds diagnostics only. It does not change texture loading, D3D11 resource creation, GPU uploads, or housing-culling behavior.

# FPSBooster 0.3.0.17

## 0.3.0.17 — native D3D11 resource-association audit

- Extends the correlated `Texture.InitializeContents` snapshot with a read-only check of the persistent `Texture.D3D11Texture2D` and `Texture.D3D11ShaderResourceView` fields against the successful `CreateTexture2D` and `CreateShaderResourceView` outputs observed inside that same initialization.
- Calls `ID3D11ShaderResourceView::GetResource` to inspect the resource actually backing the stored SRV, then compares canonical `IUnknown` identity with the persistent `Texture.D3D11Texture2D` resource. This avoids relying only on raw interface-pointer equality.
- Calls `ID3D11DeviceChild::GetDevice` for the stored texture and SRV and compares canonical device identity. It also compares those devices with the devices used by the matching creation calls.
- All `GetResource` and `GetDevice` results are temporary COM references and are released before the audit returns. `QueryInterface(IID_IUnknown)` references used for identity checks are also released. Diagnostics retain copied numeric addresses and booleans only; no game-owned COM reference is retained, and no resource, SRV, descriptor, or refcount is modified.
- The audit establishes resource/view/device relationships only. It does **not** infer the game's ownership/refcount policy, prove a safe pointer-swap location, or enable progressive updates to the live game texture. The existing shadow preview remains separate and unchanged.
- Copy diagnostics and the lifecycle event view now show the association results, returned backing-resource pointer, device pointers, and which identity checks succeeded.

---

# FPSBooster 0.3.0.16

## 0.3.0.16 — selected-resource capture and complete preview timing

- Progressive shadow capture is now filtered by the `ResourceHandle.Id` of the manually selected native handle. Other compatible BC7 texture loads are skipped rather than stealing the one-shot capture.
- The captured report includes the selected/captured resource ID and the actual redirected resource filename.
- Payload-copy time is measured separately from the wait for the UI thread to create the coarse preview.
- Capture-to-first-coarse-preview and capture-to-full-resolution timings are distinct.
- UI frame counts now include the initial coarse-preview frame as well as subsequent refinement frames.
- The original game texture remains untouched; the shadow preview is still a separate plugin-owned `DEFAULT` resource.

## 0.3.0.15 — native-payload coarse-to-fine shadow preview

- Adds an explicitly armed, one-shot capture of native BC7 2D mip payloads while the correlated `ID3D11Device::CreateTexture2D` call is active. Capture is limited to immutable, single-array, single-sample BC7 resources with matching pitches and a contiguous payload layout, and is capped at 64 MiB. The UI identifies the actual `ResourceHandle.FileName` captured so the user can verify which Penumbra replacement was selected.
- Copies each validated `pSysMem` mip into managed memory before the original game `CreateTexture2D` call is invoked. This is an intentional diagnostic copy and can add CPU time and temporary memory pressure to that one native load; it occurs only after explicit user arming. Native source pointers are not retained after the callback.
- Creates a separate plugin-owned `D3D11_USAGE_DEFAULT` texture with the complete mip chain allocated but no initial data. The coarsest mip is uploaded first, and an SRV exposes only initialized contiguous mips. Finer mips are uploaded on subsequent UI draw frames with a 1 ms elapsed submission budget and isolated largest-mip policy. The SRV is expanded only after the newly visible mip range has been initialized.
- Keeps the live FFXIV `Texture`, D3D11 texture, SRV, descriptors, and native resource ownership untouched. This is a shadow proof-of-concept only: the game still performs its normal full-payload read and immutable texture creation, the shadow texture consumes additional VRAM, and no native loading/stutter improvement is claimed yet.
- Adds a compact Native Textures UI section to arm/cancel capture, display the shadow preview and source path, show capture-to-preview and per-frame refinement metrics, and discard the plugin-owned preview. **Copy diagnostics** now includes the shadow preview state and scheduler measurements. All immediate-context calls run on the Dalamud UI draw thread.
- Uses a capture-generation guard so Stop/Cancel invalidates a native callback that has copied data but has not yet handed the managed payload to the UI. This avoids publishing a stale capture after the user cancels or closes the window.

---

# FPSBooster 0.3.0.14

## 0.3.0.14 — D3D11 shader-resource-view hook compile fixes

- Corrects the active initialization-object field name used by the `CreateShaderResourceView` detour so capture is scoped to the current texture initialization.
- Queries `ID3D11ShaderResourceView::GetDesc` into a stack-local native descriptor and then copies it into the managed `out` parameter. This avoids taking the address of a managed by-reference parameter and preserves the read-only COM query.
- No hook signatures, capture filters, native resource arguments, descriptors, or game-owned texture/SRV objects are modified.

---

## 0.3.0.13 — D3D11 shader-resource-view inspection

- Adds an optional hook on `ID3D11Device::CreateShaderResourceView`, resolved from FFXIV's `Device.D3D11Forwarder` COM vtable (slot 7). It is installed only when the correlated `Texture.InitializeContents` and `CreateTexture2D` hooks are available.
- The hook records only calls nested inside the active texture initialization whose resource pointer matches a texture created by the observed `CreateTexture2D` call. Unrelated SRV creations pass straight through without capture.
- Captures the input SRV descriptor when one is supplied, whether the caller passed a null descriptor, the HRESULT, returned SRV pointer, and the actual resulting descriptor queried through the returned `ID3D11ShaderResourceView::GetDesc` method.
- Reports the view dimension and, for 2D/cube views, the actual `MostDetailedMip` and `MipLevels` range. For array views it also reports the first array slice and array size. This establishes which mip levels the game's shader view can access.
- The hook is optional and fails closed. It never changes the resource/view descriptor, retains COM objects, or modifies the texture or SRV. The descriptor query is read-only and occurs while the newly returned view is valid in the native call.
- No native resource creation arguments, shader-resource views, texture data, or progressive-loading behavior are modified.

---

## 0.3.0.12 — D3D11 CreateTexture2D subresource instrumentation

- Adds an optional hook on `ID3D11Device::CreateTexture2D`, resolved from FFXIV's `Device.D3D11Forwarder` COM vtable (CreateTexture2D slot 5: IUnknown's three slots followed by CreateBuffer and CreateTexture1D).
- The hook is enabled only alongside the existing `Texture.InitializeContents` hook and records calls only when they occur synchronously inside an active, correlated texture initialization on the same thread. It filters to descriptions whose dimensions, mip count, and array size match the active FFXIV `Texture` object.
- Captures the D3D11 texture description, HRESULT, returned texture pointer, initial-data array pointer, and each `D3D11_SUBRESOURCE_DATA` entry's `pSysMem`, `SysMemPitch`, and `SysMemSlicePitch` values.
- Computes each subresource pointer's numeric offset from the previously observed payload buffer and whether its start address lies within the captured payload range. The report retains copied numeric address values only; it does not dereference payload memory or keep native object references.
- The report compares the created D3D11 texture pointer with the texture object after `InitializeContents`, and includes per-subresource addresses/pitches in Copy diagnostics.
- The device hook is optional and fails closed if the game device/vtable is unavailable or the hook cannot be installed. All existing lifecycle hooks continue to operate without it.
- No game-owned D3D11 resource, texture data, resource descriptor, `ChangeLod`, or progressive-loading behavior is modified.

---

## 0.3.0.11 — D3D11 texture description query compile fix

- Replaces the unavailable Vortice `ID3D11Texture2D.GetDescription()` call with the native `ID3D11Texture2D::GetDesc(D3D11_TEXTURE2D_DESC*)` COM method.
- Invokes the documented COM vtable slot on a temporary `QueryInterface` reference and releases that temporary reference in the existing `finally` block. The game's original texture reference is not changed.
- Uses a local sequential-layout representation of `D3D11_TEXTURE2D_DESC` and reports width, height, mip levels, array size, DXGI format, usage, bind flags, CPU access, misc flags, and sample description.
- The query remains read-only and does not map, copy, modify, or retain the texture resource.

---

## 0.3.0.10 — D3D11 texture description and header validation diagnostics

- Extends the existing `Texture.InitializeContents` before/after snapshot with a read-only query of the actual `ID3D11Texture2D` description after the game creates it: width, height, mip levels, array size, DXGI format, usage, bind flags, CPU access flags, misc flags, and sample description.
- The query uses a temporary `QueryInterface` reference to the existing game-owned texture pointer and releases only that temporary reference. It does not alter the game's original COM reference, texture, SRV, or native `Texture` object.
- Re-captures the complete 80-byte TEX header at `Texture.InitializeContents` entry and return, including its raw bytes and parsed LOD/surface offset tables. This lets the next report compare the header that was read into `TextureResourceHandle.Header` with the header visible at texture initialization.
- The raw header bytes are included to independently inspect the offset-table representation. The probe does not assume the reported surface values are valid byte offsets for mip slicing and does not use them to read or split payload data.
- No resource behavior, file reads, native texture creation, D3D11 resources, `ChangeLod`, or progressive-upload behavior is modified.

---

# FPSBooster 0.3.0.9

## 0.3.0.9 — TEX header layout and native content-initialization diagnostics

- Extends the read-only native texture lifecycle monitor with an optional `Texture.InitializeContents(void*)` hook, resolved from the current FFXIVClientStructs call-site signature. If the signature is missing, ambiguous, outside the game `.text` section, or cannot be hooked, the optional diagnostic is disabled without preventing the existing hooks from operating.
- During an active texture `FileJob`, the probe keeps only the numeric address/length/offset of the successful large `FileDescriptor.Read` output buffer in thread-local state until that outer job returns. It does not read, write, copy, or hold a managed/native reference to the pointed-to memory.
- When `Texture.InitializeContents` runs inside the correlated texture resource flow, it records the `contents` argument and compares its numeric address with the captured large-read output-buffer address. This tests whether the renderer receives the same buffer or a different/intermediate buffer.
- Records a before/after snapshot of the `Texture` object around `InitializeContents`: dimensions, allocated dimensions, depth, mip level, array size, format/flags, D3D11 texture pointer, and SRV pointer. These are observations only; the probe does not AddRef, release, map, or modify them.
- Identifies the 80-byte header read only when `FileDescriptor.Read` writes exactly 80 bytes at offset 0 into `TextureResourceHandle.Header`. The event reports its observed offset within the handle and copies the `LodOffsets` and `OffsetsToSurfaces` values from that header.
- The header-table capture uses the documented `TexFileHeader` layout (80 bytes, three LOD offsets and thirteen surface offsets). No payload bytes are dereferenced.
- The UI and Copy diagnostics report the optional hook state/address, header offset tables, payload/input pointer comparison, and before/after texture-object state.
- All new instrumentation is optional/read-only. It never changes native resource state, `ChangeLod`, TEX data, D3D11 resources, or the progressive-upload pipeline.

---

## 0.3.0.8 — copyable native texture diagnostics

- Adds a **Copy diagnostics** button to the Native Textures lifecycle section. It copies the generated report through Dalamud API 15's ImGui clipboard binding.
- The copied report includes the selected game resource path and SqPack reference metadata; the current native handle/TEX/GPU snapshot; hook installation and capture status; hook addresses; UI-thread ID; captured/queued/dropped/error counters; and every retained lifecycle event (up to the probe's existing 80-event history limit).
- Each event includes its timestamp, operation duration, resource filename and IDs, thread, result/arguments, descriptor mode/offset/length transitions, file-buffer addresses, before/after resource state, `FileDescriptor.Read` output-buffer measurements, and Load pointer correlations.
- The report is built only when the user clicks the copy control, so generating a potentially large diagnostic string adds no per-frame overhead.
- The report records pointer values only. It does not dereference or retain native memory, and explicitly notes that nested native-call timings must not be summed as independent costs.
- No native hooks, detour signatures, capture behavior, game-owned resources, or progressive-upload behavior changed.

---

# FPSBooster 0.3.0.7

## 0.3.0.7 — native capture UI compile fixes

- Wraps the `StartCapture` call in a narrow `unsafe` block because the acquired native texture handle is a pointer. The rest of the configuration-window method remains safe.
- Removes obsolete `_manualFilePathInput` and `_readMipIndex` fields left behind by the UI cleanup, along with the assignments that no longer affected rendering.
- No native hook behavior, capture semantics, or resource handling changed.

## 0.3.0.6 — focused native texture capture UI

- Simplified the Textures tab around the current workflow: inspect a game-relative resource path, acquire its native handle, start lifecycle capture, and inspect recent events.
- Moved raw handle and hook addresses into collapsed detail sections; recent events now show concise summaries with expandable per-event details.
- Removed the legacy manual mip-read, Penumbra path-resolution, and plugin-owned GPU upload test controls from the default UI. Their underlying implementation remains in the project for future reference, but is no longer presented as part of the current native integration workflow.
- Moved temporary housing visibility test buttons into a collapsed section.


## 0.3.0.5 — native file-buffer handoff instrumentation

- Added optional instrumentation for `FileDescriptor.Read`, resolved from the current FFXIVClientStructs relative-call signature. If the call site is missing or ambiguous, the lower-level hook is skipped and the existing lifecycle hooks remain available.
- The lower-level read hook only records calls whose `FileDescriptor*` matches the active texture resource's descriptor on the same thread. It records the output-buffer address, requested byte count, file offset, reset-position flag, result, and elapsed duration. It never reads or retains the pointed-to buffer.
- Added read-only snapshots of `ResourceHandle.GetData()` and `GetLength()` at existing lifecycle stages so the actual resource data pointer/length can be compared with `Blob` and the lower-level read output buffer.
- Added raw `FileDescriptor.FileBuffer` address snapshots. This field is documented primarily for non-resource file modes; it is displayed as raw data and is not assumed to be the resource payload.
- During `Load`, the `contents` pointer is interpreted as a `FileDescriptor*` only when it exactly matches the descriptor address carried by the active, same-thread texture callback and the descriptor's `ResourceHandle` points back to the current handle. The UI reports whether that validation succeeded.
- No native payload bytes are dereferenced, copied, retained, modified, or uploaded. No resource fields or D3D11 objects are changed.

The goal of this release is to determine whether the measured time in `ResourceHandle.Read` is spent in actual descriptor file reads or in other processing, and to identify the data pointer exposed by the resource handle before `FinishLoad`.

---

# FPSBooster — Progressive Texture Loading Investigation

## 0.3.0.4 — buffer handoff and descriptor-transition instrumentation

- Extends the existing opt-in, read-only lifecycle monitor; no upload or resource behavior is changed.
- `FileJob` records the `FileDescriptor` mode/offset/length both at detour entry and after the native call returns. This is specifically to investigate live captures where the outer `DoFileJob` entry reported `LoadSqPackResource` (11), while nested `ResourceHandle.Read` and `FinishLoad` callbacks reported `LoadUnpackedResource` (0) for the same handle and replacement filename.
- `Read`, `Reread`, `FileJob`, `FinishLoad`, and `Load` now snapshot documented `ResourceHandle` state before/after the original call: numeric `Blob` address, three file-size fields, `FileType`, `LastIOResult`, `ReadState`, `LoadState`, `OtherState`, raw `ChangeLod` byte, and persistent `Texture*` address. The probe stores pointer values only and never dereferences the `Blob`.
- `Load` additionally records the `contents` argument and the numeric addresses of `TextureWhileLoading` and `DataWhileLoading` at the Load hook entry. FFXIVClientStructs documents these temporary fields as only known valid during `Load`; the probe copies their addresses only and does not retain or dereference them.
- `FileJob` events include the `FileDescriptor*` address and its entry/return descriptor state. This will show whether mode 11 is transformed only during the nested call or remains changed when the outer function returns.
- The UI records the managed thread ID executing its ImGui draw path and compares it to each FileJob event. This establishes whether a synchronous file job runs on the same managed thread as the visible plugin UI; it does not label that thread as the game's main/render thread without further evidence.
- In the user's 0.3.0.3 samples, `FileJob` took 7.175 ms vs `Read` 7.052 ms for the mask, and 9.999 ms vs 9.850 ms for the normal map. `Read` therefore accounts for roughly 98.3–98.5% of the outer file-job duration in these two individual captures. `FinishLoad` (0.006–0.008 ms) and `Load` (0.001 ms) were very small. These scopes are nested; do not add their durations together.
- The `FileJob` event snapshots descriptor state before calling the original function. `Read` and `FinishLoad` snapshot their own descriptors when those nested functions are entered. Therefore the observed mode-11 vs mode-0 values are not contradictory; they show different points in the call path. The new entry/return snapshots will help locate the transition, but a mode change at the outer function boundary is not assumed.
- Hooks remain opt-in, fail closed on signature/layout mismatch, call the original function, and use the bounded event queue. No game-owned fields, file contents, reference counts, temporary pointers, D3D11 resources, or `ChangeLod` state are modified.

## 0.3.0.3 — native file-job and resource-completion instrumentation

- Extends the opt-in read-only native texture lifecycle monitor with `FileThread.DoFileJob` and `ResourceHandle.FinishLoad` hooks.
- `FileThread.DoFileJob` is resolved from the current FFXIVClientStructs function-entry signature. Its detour only records operations whose `FileDescriptor.FileMode` is `LoadUnpackedResource` or `LoadSqPackResource` and whose `FileDescriptor.ResourceHandle` reports the expected `TextureResourceHandle` size (`0x150`). It records the file mode, resource filename, offset, requested length, job priority, sync flag, thread, result, and elapsed outer file-job time.
- `ResourceHandle.FinishLoad` is identified using the current FFXIVClientStructs call-site signature. The monitor requires exactly one matching call site, resolves its `E8 rel32` destination, verifies the destination is inside the game `.text` section, and only then hooks the target. It records the arguments, result, elapsed duration, resource metadata, post-call texture header, and persistent texture pointer.
- Both hooks are installed transactionally with the existing Read/Load/Reread hooks. If either signature is missing, ambiguous, or resolves outside the expected text section, no lifecycle hooks are installed. All hooks share the existing opt-in start/stop controls and bounded event queue.
- Timing scopes are nested: `FileJob` may include the `Read` call, and `FinishLoad` includes dependent-resource handling and the `Load` call. **Do not add these durations together.** Compare them by handle address and sequence to understand which outer stage contains the time.
- The detours remain observational and always call the original native function. They do not mutate the descriptor, texture handle, temporary buffers, `ChangeLod`, D3D11 resources, or reference counts. The plugin-owned progressive-upload test pipeline remains unchanged.

## 0.3.0.2 — correct native resource filename reporting

- Renames the displayed `ResourceHandle.FileName` value from **Logical** to **Resource filename**. In the first live Penumbra test this native `std::string` contained the resolved mod file path (for example, `E:/ff14Mods/.../c0201h0184_hir_mask.tex`), not a game-relative logical key. The previous UI label was incorrect.
- Removes `FileDescriptor.FilePathString` from lifecycle capture. Although FFXIVClientStructs describes it as a wide path for disk-file modes, live mode-0 callbacks in the test returned non-path data from this union-backed field. The probe no longer dereferences or displays it. File mode, offset, requested length, and `ResourceHandle.FileName` remain captured.
- No hook targets, native delegate signatures, upload behavior, or game-owned resource behavior changed.

## 0.3.0.1 — native lifecycle probe compile fixes

- Fixes the native texture-handle size constant to use the same signed integer type as `sizeof`.
- Supplies explicit delegate type arguments to `IGameInteropProvider.HookFromAddress<T>` so each hook is created as the intended `Hook<T>` type.
- No hook targets, delegate signatures, capture behavior, or native resource behavior were changed.

## 0.3.0.0 — read-only native texture lifecycle monitor

This release starts the native-integration phase while deliberately keeping the proven 0.2.9.9 progressive upload test pipeline unchanged. The new code is an opt-in observer, not yet a native progressive uploader.

- Adds a manually enabled lifecycle monitor that takes the vtable function addresses from an already-acquired `TextureResourceHandle`, then hooks `ResourceHandle.Read` (virtual slot 32), `Load` (slot 33), and `Reread` (slot 34) through Dalamud's `IGameInteropProvider`.
- Hooks are not installed automatically. The user must acquire one game-relative `.tex` handle and press **Install hooks + start capture**. That handle is only a seed for reading the concrete texture-resource vtable; the resulting shared method hooks are global to calls dispatched through those texture-resource targets, so they can observe other player, NPC, environment, and UI textures as well. **Stop native lifecycle capture** disables the hooks; unloading the plugin disposes them.
- The detours call the original native functions and record copied metadata only. A bounded 512-event callback queue is drained by the UI, with the most recent 80 events retained for display.
- `Read` / `Reread` events record `ResourceHandle.FileName` (the native resource filename), file mode, file offset/length, result, thread, resource ID and observed native file sizes. `Load` events record the post-call TEX header and persistent `Texture*` address. All durations cover the original native virtual-function call only; metadata-copy and queue time are excluded. The observed `ResourceHandle.FileName` can contain the resolved loose-file path. The probe does not read `FileDescriptor.FilePathString`; the live test showed that this separate union-backed field did not contain a usable path in the mode-0 callbacks.
- `Load` events record the post-call TEX header dimensions, depth, mip/flag byte, format, persistent `Texture*` address, resource sizes, I/O result, result code and thread. Every event also records the numeric native handle address so its `Read`/`Reread`/`Load` records can be correlated. This is a pointer value only; it is never dereferenced later. The monitor does not inspect `TextureWhileLoading` or `DataWhileLoading`.
- The hook filters callbacks by the runtime `ResourceHandle.GetHandleSize() == 0x150` before treating the object as a `TextureResourceHandle`, because the selected virtual-function implementation could also be shared by other resource types.
- If `Read` and `Reread` share the same native target address, one hook covers both and events are labelled `Read`; otherwise they are tracked separately. The UI reports this case.
- The detours call cached `OriginalDisposeSafe` delegates, so an already-in-flight callback can still reach the original function while hooks are being disabled/disposed. **Clear events** clears the visible history and pending queue; captured/dropped/error counters remain cumulative for the current plugin session. The monitor does not retain `ResourceHandle*` or `FileDescriptor*`, access file buffers, modify `ChangeLod`, alter reference counts, replace texture pointers, or call D3D11 on game-owned resources. It does not change Penumbra settings or resolve collections itself.
- A mode-0 read plus a `ResourceHandle.FileName` that matches a known Penumbra mod file is strong evidence that the redirected loose file reached the native resource handle. A mode-11 SqPack record alone would not prove Penumbra was bypassed; the redirection might occur below this descriptor boundary.

### First live test

1. Build with `dotnet build -c Release` and load the dev plugin with Penumbra enabled.
2. In **Textures**, enter a game-relative `.tex` key that is replaced by a large loose Penumbra file, then click **Acquire native async texture handle**.
3. When the native handle snapshot appears, click **Install hooks + start capture**.
4. Trigger fresh texture loads after capture starts (for example, swap to another gear item with a different texture, change character, or visit an area with different assets). The initial manually acquired handle may already be cached, so do not expect the acquisition itself to produce a new Read/Load event.
5. Inspect the `Read`, `Reread`, and `Load` records. Correlate them using the native handle address. For a mode-0 read, compare `ResourceHandle.FileName` with the exact replacement file selected by Penumbra for the game-relative key. Remember that `ResourceHandle.FileName` is the native resource filename and may therefore be the resolved mod-disk path, not the original game-relative key. If only mode-11 SqPack records appear, do not conclude that Penumbra was bypassed; it may redirect below the descriptor boundary, which would require a second, lower-level observation point.
6. Stop capture before changing the inspected asset. Copy the event details from the UI or report the event rows, including resource filename, mode, dimensions, format, mip/flag byte, native Texture address, and durations.

### Current 0.3.0 scope and next gate

The native monitor is still an observation milestone. It does **not** make FFXIV textures appear progressively, reduce VRAM use, or speed up normal texture loading yet. The latest user test shows a 4096×2048 BC7 Penumbra replacement (13 mips, 11,184,904 bytes) with a persistent native `Texture*`, D3D11 texture, and SRV after loading. `ChangeLod` was false in that snapshot; this single observation does not establish its semantics.

The new 0.3.0.4 test should answer four questions without reading payload memory:

1. Does `ResourceHandle.Blob` become non-null after `Read`, and does it remain the same pointer until `FinishLoad`?
2. Does `FinishLoad` clear `Blob` after its original call, consistent with the documented temporary-buffer cleanup?
3. Does the `Load(contents, lastIOResult)` input address equal the captured `Blob` address, or is there an intermediate buffer? How do the `TextureWhileLoading` and `DataWhileLoading` address values relate?
4. Does the FileJob descriptor mode remain at 11 when `DoFileJob` returns, or does it change by the outer call boundary? The nested `Read`/`FinishLoad` mode-0 observations already show that the mode differs at those inner call sites.

The UI-thread comparison is only an equality check between managed thread IDs observed inside the hook and the plugin's ImGui draw path. If they match, that supports the conclusion that this synchronous job runs on the same managed thread as UI drawing; it is not by itself proof of a specific native thread role.

After these observations, the next research task is to trace the actual `ChangeLod` consumers and determine which native code allocates, populates, and consumes the texture buffers. Until resource ownership, synchronization, and texture/SRV identity are established, do not read or write payload bytes, set `ChangeLod`, replace the native `Texture*`, or update the game's D3D11 resource.

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



Dalamud plugin that cuts rendering work in FFXIV housing by hiding furniture you can't see, with an progressive texture loading research, and read-only native texture lifecycle monitoring.


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

## 0.3.0.5 — native file-buffer handoff instrumentation

- Added optional instrumentation for `FileDescriptor.Read`, resolved from the current FFXIVClientStructs relative-call signature. If the call site is missing or ambiguous, the lower-level hook is skipped and the existing lifecycle hooks remain available.
- The lower-level read hook only records calls whose `FileDescriptor*` matches the active texture resource's descriptor on the same thread. It records the output-buffer address, requested byte count, file offset, reset-position flag, result, and elapsed duration. It never reads or retains the pointed-to buffer.
- Added read-only snapshots of `ResourceHandle.GetData()` and `GetLength()` at existing lifecycle stages so the actual resource data pointer/length can be compared with `Blob` and the lower-level read output buffer.
- Added raw `FileDescriptor.FileBuffer` address snapshots. This field is documented primarily for non-resource file modes; it is displayed as raw data and is not assumed to be the resource payload.
- During `Load`, the `contents` pointer is interpreted as a `FileDescriptor*` only when it exactly matches the descriptor address carried by the active, same-thread texture callback and the descriptor's `ResourceHandle` points back to the current handle. The UI reports whether that validation succeeded.
- No native payload bytes are dereferenced, copied, retained, modified, or uploaded. No resource fields or D3D11 objects are changed.

The goal of this release is to determine whether the measured time in `ResourceHandle.Read` is spent in actual descriptor file reads or in other processing, and to identify the data pointer exposed by the resource handle before `FinishLoad`.

---

# FPSBooster — Progressive Texture Loading Investigation

## 0.3.0.4 — buffer handoff and descriptor-transition instrumentation

- Extends the existing opt-in, read-only lifecycle monitor; no upload or resource behavior is changed.
- `FileJob` records the `FileDescriptor` mode/offset/length both at detour entry and after the native call returns. This is specifically to investigate live captures where the outer `DoFileJob` entry reported `LoadSqPackResource` (11), while nested `ResourceHandle.Read` and `FinishLoad` callbacks reported `LoadUnpackedResource` (0) for the same handle and replacement filename.
- `Read`, `Reread`, `FileJob`, `FinishLoad`, and `Load` now snapshot documented `ResourceHandle` state before/after the original call: numeric `Blob` address, three file-size fields, `FileType`, `LastIOResult`, `ReadState`, `LoadState`, `OtherState`, raw `ChangeLod` byte, and persistent `Texture*` address. The probe stores pointer values only and never dereferences the `Blob`.
- `Load` additionally records the `contents` argument and the numeric addresses of `TextureWhileLoading` and `DataWhileLoading` at the Load hook entry. FFXIVClientStructs documents these temporary fields as only known valid during `Load`; the probe copies their addresses only and does not retain or dereference them.
- `FileJob` events include the `FileDescriptor*` address and its entry/return descriptor state. This will show whether mode 11 is transformed only during the nested call or remains changed when the outer function returns.
- The UI records the managed thread ID executing its ImGui draw path and compares it to each FileJob event. This establishes whether a synchronous file job runs on the same managed thread as the visible plugin UI; it does not label that thread as the game's main/render thread without further evidence.
- In the user's 0.3.0.3 samples, `FileJob` took 7.175 ms vs `Read` 7.052 ms for the mask, and 9.999 ms vs 9.850 ms for the normal map. `Read` therefore accounts for roughly 98.3–98.5% of the outer file-job duration in these two individual captures. `FinishLoad` (0.006–0.008 ms) and `Load` (0.001 ms) were very small. These scopes are nested; do not add their durations together.
- The `FileJob` event snapshots descriptor state before calling the original function. `Read` and `FinishLoad` snapshot their own descriptors when those nested functions are entered. Therefore the observed mode-11 vs mode-0 values are not contradictory; they show different points in the call path. The new entry/return snapshots will help locate the transition, but a mode change at the outer function boundary is not assumed.
- Hooks remain opt-in, fail closed on signature/layout mismatch, call the original function, and use the bounded event queue. No game-owned fields, file contents, reference counts, temporary pointers, D3D11 resources, or `ChangeLod` state are modified.

## 0.3.0.3 — native file-job and resource-completion instrumentation

- Extends the opt-in read-only native texture lifecycle monitor with `FileThread.DoFileJob` and `ResourceHandle.FinishLoad` hooks.
- `FileThread.DoFileJob` is resolved from the current FFXIVClientStructs function-entry signature. Its detour only records operations whose `FileDescriptor.FileMode` is `LoadUnpackedResource` or `LoadSqPackResource` and whose `FileDescriptor.ResourceHandle` reports the expected `TextureResourceHandle` size (`0x150`). It records the file mode, resource filename, offset, requested length, job priority, sync flag, thread, result, and elapsed outer file-job time.
- `ResourceHandle.FinishLoad` is identified using the current FFXIVClientStructs call-site signature. The monitor requires exactly one matching call site, resolves its `E8 rel32` destination, verifies the destination is inside the game `.text` section, and only then hooks the target. It records the arguments, result, elapsed duration, resource metadata, post-call texture header, and persistent texture pointer.
- Both hooks are installed transactionally with the existing Read/Load/Reread hooks. If either signature is missing, ambiguous, or resolves outside the expected text section, no lifecycle hooks are installed. All hooks share the existing opt-in start/stop controls and bounded event queue.
- Timing scopes are nested: `FileJob` may include the `Read` call, and `FinishLoad` includes dependent-resource handling and the `Load` call. **Do not add these durations together.** Compare them by handle address and sequence to understand which outer stage contains the time.
- The detours remain observational and always call the original native function. They do not mutate the descriptor, texture handle, temporary buffers, `ChangeLod`, D3D11 resources, or reference counts. The plugin-owned progressive-upload test pipeline remains unchanged.

## 0.3.0.2 — correct native resource filename reporting

- Renames the displayed `ResourceHandle.FileName` value from **Logical** to **Resource filename**. In the first live Penumbra test this native `std::string` contained the resolved mod file path (for example, `E:/ff14Mods/.../c0201h0184_hir_mask.tex`), not a game-relative logical key. The previous UI label was incorrect.
- Removes `FileDescriptor.FilePathString` from lifecycle capture. Although FFXIVClientStructs describes it as a wide path for disk-file modes, live mode-0 callbacks in the test returned non-path data from this union-backed field. The probe no longer dereferences or displays it. File mode, offset, requested length, and `ResourceHandle.FileName` remain captured.
- No hook targets, native delegate signatures, upload behavior, or game-owned resource behavior changed.

## 0.3.0.1 — native lifecycle probe compile fixes

- Fixes the native texture-handle size constant to use the same signed integer type as `sizeof`.
- Supplies explicit delegate type arguments to `IGameInteropProvider.HookFromAddress<T>` so each hook is created as the intended `Hook<T>` type.
- No hook targets, delegate signatures, capture behavior, or native resource behavior were changed.

## 0.3.0.0 — read-only native texture lifecycle monitor

This release starts the native-integration phase while deliberately keeping the proven 0.2.9.9 progressive upload test pipeline unchanged. The new code is an opt-in observer, not yet a native progressive uploader.

- Adds a manually enabled lifecycle monitor that takes the vtable function addresses from an already-acquired `TextureResourceHandle`, then hooks `ResourceHandle.Read` (virtual slot 32), `Load` (slot 33), and `Reread` (slot 34) through Dalamud's `IGameInteropProvider`.
- Hooks are not installed automatically. The user must acquire one game-relative `.tex` handle and press **Install hooks + start capture**. That handle is only a seed for reading the concrete texture-resource vtable; the resulting shared method hooks are global to calls dispatched through those texture-resource targets, so they can observe other player, NPC, environment, and UI textures as well. **Stop native lifecycle capture** disables the hooks; unloading the plugin disposes them.
- The detours call the original native functions and record copied metadata only. A bounded 512-event callback queue is drained by the UI, with the most recent 80 events retained for display.
- `Read` / `Reread` events record `ResourceHandle.FileName` (the native resource filename), file mode, file offset/length, result, thread, resource ID and observed native file sizes. `Load` events record the post-call TEX header and persistent `Texture*` address. All durations cover the original native virtual-function call only; metadata-copy and queue time are excluded. The observed `ResourceHandle.FileName` can contain the resolved loose-file path. The probe does not read `FileDescriptor.FilePathString`; the live test showed that this separate union-backed field did not contain a usable path in the mode-0 callbacks.
- `Load` events record the post-call TEX header dimensions, depth, mip/flag byte, format, persistent `Texture*` address, resource sizes, I/O result, result code and thread. Every event also records the numeric native handle address so its `Read`/`Reread`/`Load` records can be correlated. This is a pointer value only; it is never dereferenced later. The monitor does not inspect `TextureWhileLoading` or `DataWhileLoading`.
- The hook filters callbacks by the runtime `ResourceHandle.GetHandleSize() == 0x150` before treating the object as a `TextureResourceHandle`, because the selected virtual-function implementation could also be shared by other resource types.
- If `Read` and `Reread` share the same native target address, one hook covers both and events are labelled `Read`; otherwise they are tracked separately. The UI reports this case.
- The detours call cached `OriginalDisposeSafe` delegates, so an already-in-flight callback can still reach the original function while hooks are being disabled/disposed. **Clear events** clears the visible history and pending queue; captured/dropped/error counters remain cumulative for the current plugin session. The monitor does not retain `ResourceHandle*` or `FileDescriptor*`, access file buffers, modify `ChangeLod`, alter reference counts, replace texture pointers, or call D3D11 on game-owned resources. It does not change Penumbra settings or resolve collections itself.
- A mode-0 read plus a `ResourceHandle.FileName` that matches a known Penumbra mod file is strong evidence that the redirected loose file reached the native resource handle. A mode-11 SqPack record alone would not prove Penumbra was bypassed; the redirection might occur below this descriptor boundary.

### First live test

1. Build with `dotnet build -c Release` and load the dev plugin with Penumbra enabled.
2. In **Textures**, enter a game-relative `.tex` key that is replaced by a large loose Penumbra file, then click **Acquire native async texture handle**.
3. When the native handle snapshot appears, click **Install hooks + start capture**.
4. Trigger fresh texture loads after capture starts (for example, swap to another gear item with a different texture, change character, or visit an area with different assets). The initial manually acquired handle may already be cached, so do not expect the acquisition itself to produce a new Read/Load event.
5. Inspect the `Read`, `Reread`, and `Load` records. Correlate them using the native handle address. For a mode-0 read, compare `ResourceHandle.FileName` with the exact replacement file selected by Penumbra for the game-relative key. Remember that `ResourceHandle.FileName` is the native resource filename and may therefore be the resolved mod-disk path, not the original game-relative key. If only mode-11 SqPack records appear, do not conclude that Penumbra was bypassed; it may redirect below the descriptor boundary, which would require a second, lower-level observation point.
6. Stop capture before changing the inspected asset. Copy the event details from the UI or report the event rows, including resource filename, mode, dimensions, format, mip/flag byte, native Texture address, and durations.

### Current 0.3.0 scope and next gate

The native monitor is still an observation milestone. It does **not** make FFXIV textures appear progressively, reduce VRAM use, or speed up normal texture loading yet. The latest user test shows a 4096×2048 BC7 Penumbra replacement (13 mips, 11,184,904 bytes) with a persistent native `Texture*`, D3D11 texture, and SRV after loading. `ChangeLod` was false in that snapshot; this single observation does not establish its semantics.

The new 0.3.0.4 test should answer four questions without reading payload memory:

1. Does `ResourceHandle.Blob` become non-null after `Read`, and does it remain the same pointer until `FinishLoad`?
2. Does `FinishLoad` clear `Blob` after its original call, consistent with the documented temporary-buffer cleanup?
3. Does the `Load(contents, lastIOResult)` input address equal the captured `Blob` address, or is there an intermediate buffer? How do the `TextureWhileLoading` and `DataWhileLoading` address values relate?
4. Does the FileJob descriptor mode remain at 11 when `DoFileJob` returns, or does it change by the outer call boundary? The nested `Read`/`FinishLoad` mode-0 observations already show that the mode differs at those inner call sites.

The UI-thread comparison is only an equality check between managed thread IDs observed inside the hook and the plugin's ImGui draw path. If they match, that supports the conclusion that this synchronous job runs on the same managed thread as UI drawing; it is not by itself proof of a specific native thread role.

After these observations, the next research task is to trace the actual `ChangeLod` consumers and determine which native code allocates, populates, and consumes the texture buffers. Until resource ownership, synchronization, and texture/SRV identity are established, do not read or write payload bytes, set `ChangeLod`, replace the native `Texture*`, or update the game's D3D11 resource.

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



Dalamud plugin that cuts rendering work in FFXIV housing by hiding furniture you can't see, with an progressive texture loading research, and read-only native texture lifecycle monitoring.


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
