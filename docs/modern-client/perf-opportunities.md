# Performance Opportunities (backlog)

This is a living list. Add to it whenever work turns up a likely performance win. We tackle these **after** the PoC is proven, measuring each one with the per-phase timing hooks (integration plan step 7b) to see whether it moves FPS.

Format: **what**, where (file:line), why it should help, evidence or estimate, and source.

**Measured (step 7b, 2026-10-24):** classicuo-perf-baseline.md has the per-phase numbers. At 1920×1080 JIT on the sparse local shard, Update ≈ 0.012 ms, Draw 0.20–1.20 ms (world prepare plus draw dominates) and Present 0.12 ms p50. At 4K, Present plus composite reach about 0.57 ms, and zoomed out at 4K reaches 5.7 ms per frame. "(measured: …)" notes below come from that doc.

## Frame loop / Update

- **World.Update walks every mobile and every item, including items inside containers, every frame** (World.cs:259-398; `Entity.Update` → `UpdateHits`, `Item.ProcessAnimation`).
  - Fix: skip contained items; keep active lists for animating items and mobiles; drive it through ECS systems.
  - Source: cuo-2.0-perf-comparison.md.
  - Measured: about 1 µs for 6 mobiles and 70 items, and Ecs.Update <0.1 µs. Not worth attacking until a populated scene (≥100 mobiles, a full backpack) shows otherwise.
- **More ClassicUO update-path findings** (ECS agent, 2026-10-24):
  - `WMapManager.AddOrUpdate` runs for every ally or party mobile every frame (World.cs:322/335).
  - `RootContainer` walks the dictionary per item and is called for every item on map change, so map changes are O(n·depth) (World.cs:846).
  - LINQ `Reverse()` allocates on every FindNext (World.cs:703/727).
  - Every 0x78 destroys and reallocates all non-backpack equipment as new `Item`s (PacketHandlers.cs:2883).
  - ConfirmWalk calls `Player.AddToTile()` (unlink plus a sorted re-insert) per step even when the tile doesn't change, and SetInWorldTile always re-adds (PacketHandlers.cs:1218).
  - The HP percentage is recomputed every frame for every entity with HitsMax > 0 (Entity.cs:150).
  - The ECS mirror's `Sync` runs twice per SetInWorldTile; measure it before optimising.
- **Screen position recomputed for every object every frame** in `FillGameObjectList`. Fix: recompute only when the world position changes, via an ECS change query. Source: impl/ecs 827e54185.
- **UIManager.Update sorts and recursively updates every gump control every frame** (UIManager.cs:335, Control.cs:259). Fix: update only dirty controls; add a hit-test cache. Source: 2.0 `UiPickCachePlugin`, `GumpPositionCachePlugin`.
- **Done (B1–B5, `poc/batcher-ui-alloc`): about 17–28 KB allocated per frame is now about 0.1–0.25 KB, with no gen0 GCs in steady state.** Measured with paperdoll, backpack, journal and status open: 27.6 KB → 70–250 B per frame, gen0 1.6–2.2/s → 0, JIT and NativeAOT. What remains is asset streaming (animation frame decode in `PackPages.DecodeChunk` and `PackSprites.DecodeIndexed` when a mobile shows a new direction) and the FrameStats report string every 5 s.
  - Original finding: about 17–18 KB allocated per frame, roughly 4 gen0 GCs every 5 s (classicuo-aot-baseline.md). Fix: hunt allocations down to zero, and keep allocation and flush probes permanently in the build. Source: 2.0 `EcPerfProbe`, `BatcherProbe`.
  - Measured: 17.7 KB per frame at idle and 21 KB while travelling; GC pause 0.3–1.5 ms/s. AllocationTick sampling puts about 90% in the UI draw path; the three quick wins follow.
- **Done (B3):** **`Control.DrawDebug` allocates a closure display class on every call, even with debug off** (Control.cs:328). C# hoists the captured-variable class to method entry, so every visible control allocates one per frame: about 20% of all per-frame allocation. Fix: early-return before the lambda, or move the lambda into a separate method. Trivial. Source: step 7b allocation trace.
- **Done (B2; `GumpCommand` structs plus `IGumpDeferredDraw`, which also covers ScrollBar, ScrollFlag, BorderControl, JournalGump, SystemChatControl, TextContainerGump, Checkbox, HitBox, Line, ItemGump and AlphaBlendControl):** **UI controls allocate a `Func<UltimaBatcher2D,bool>` plus a display class per draw per frame** (`GumpPic`/`GumpPicTiled`/`Button`/`HtmlControl.AddToRenderLists` → `RenderLists.AddGumpWithAtlas/NoAtlas`). This is about 60% of per-frame allocation. Fix: a struct command (texture, rects, hue, depth) in a pooled list instead of closures. Source: step 7b allocation trace.
- **Done (B4, HtmlControl too):** **`ResizePic` and `StbTextBox` render lambdas create a new `RenderLists` (seven `List<>`s) every frame** (ResizePic.cs:249). Fix: reuse a cached instance per control. Source: step 7b allocation trace.
- **Done (B5):** **`PaperDollGump.EquipmentSlot.Update` builds `$"{Layer} slot"` and calls SetTooltip every frame for each empty slot** (PaperDollGump.cs:743). Fix: set it only when the slot becomes empty. Source: step 7b allocation trace.
- **Done (step 7b): the 250 FPS cap is removed.** `fps: 0` (uncapped, no Sleep limiter) is now the default, with a VSync setting and options-gump checkbox mapped to `PresentationInterval` at runtime. "Reduce FPS when inactive" defaults off and is forced off under `CUO_FRAMESTATS`.

## Rendering

- **World prepare plus draw is about 0.35 ms at Britain, zoom 1** (more when busy or zoomed out). Replaced by Uo.Render: chunks baked once on the GPU, one draw call. Integration plan step 8.
  - Measured: 0.22 ms at Britain at 1080p, 1.09 ms zoomed out (12k render-list entries, 326 draw calls) and 5.2 ms zoomed out at 4K (40k entries, 1,041 draw calls). The top cost in every heavy scene.
- **The UI render target is cleared and redrawn every frame** (GameController.cs:528-549). Fix: cache it and redraw only on dirty. Measured: UIManager.Draw is 0.05 ms with 4 gumps; the allocation items above come first.
- **FNA per-draw overhead:** C# state application, MojoShader uniforms and several FNA3D calls per batch flush. Fix if significant: a record-then-submit command-list device, which is also the WASM and WebGPU path. Source: 2.0 `IGpuDevice`.
  - Measured: 16–18 flushes plus 46–1,041 draw calls per frame. Time inside FNA calls is 0.08–0.11 ms at zoom 1, 0.29 ms zoomed out and 1.07 ms zoomed out at 4K. In CPU samples, `Effect.INTERNAL_applyEffect` is 9.4% and `ApplyStates` 8.1%.
- **Done (B1): `Pass.Apply()` drops from about 16 to 3 per frame in Gpu mode at Britain; DevBench logs applies per frame.** Original finding: **`ApplyStates` re-sends the full matrix and viewport uniforms and calls `Pass.Apply()` on every flush, and `DrawDirectIndexed` calls `Pass.Apply()` once per mesh run** (Batcher2D.cs `ApplyStates`, `DrawDirectIndexed`). Fix: dirty-track the matrices and the viewport and apply the pass once per state change; draw each chunk layer's runs with one apply. Cheap, and it helps until Uo.Render replaces the world path. Source: step 7b CPU samples.
- **The faux backbuffer blit plus the RT composite at window size** (FNA3D_Driver_SDL.c:1119-1215, RenderTargets.cs:94-147). Fix: render the world straight into the swapchain image and skip the composite. Needs another FNA3D patch.
  - Measured: Present p50 0.12 ms plus composite 0.04 ms at 1080p, against 0.45 + 0.12 ms at 3840×2054. About 0.4 ms per frame to win at 4K.
- **Present stalls 4–5 ms on about 4% of frames above ~1,000 fps** (SDL_GPU frames in flight and windowed IMMEDIATE present back-pressure); there are none at ≤750 fps. Not CPU work, but it makes frame avg and p99 misleading at very high FPS. Compare CPU work with Update + Draw or frame p50. If it matters later, try `FNA3D_VULKAN_FORCE_MAILBOX_VSYNC`-style present modes or more frames in flight. Source: step 7b.
- **Ground-item chunk baking:** bake rarely-changing ground items into chunk buffers and re-bake on ECS changes. Only if the dynamic extract or upload shows up in busy scenes. Source: 2.0 `BakeChunkDynamics`.
- **NativeAOT instruction set:** done (step 7b). `IlcInstructionSet=x86-64-v3` (note the spelling; `x86-x64-v3` is rejected) is set for win-x64. Measured: no difference from the default ISA.
- **AOT is 15–20% slower than JIT on Draw, UI and composite, independent of ISA** (Britain Draw 0.39 vs 0.33 ms). The likely cause is that there is no Dynamic PGO (devirtualisation of the batcher and UI virtual calls). Try static PGO (collect a `.mibc` from a JIT trace and pass it to ILC) and `<OptimizationPreference>Speed</OptimizationPreference>`. Source: step 7b.

## Assets / streaming

- **Expanding 16-bit pages to RGBA8 costs 3–5 ms per page in the renderer, against 1.2 ms in a microbenchmark.** The suspect is writes into write-combined upload memory. Fix: expand into cached scratch memory, then memcpy, or let the GPU expand with a compute shader. Only matters on the RGBA8 fallback path (WebGPU).
- **Co-occurrence page packing (v2).** Fewer pages touched per screen. Method in pack-format-v0.md.

## Uo.Render / slice (reported by the renderer agent, 2026-10-24)

- **The global `ChunkedBrotliDecoder` lock serialises Kind 2 page jobs** (PageResidency.cs:110), causing 13–17 ms waits in bursts. Fix: a decoder per staging slot with fewer threads, or chunk jobs on the WorkerPool.
- **The Indexed8 shader always does 4 index loads plus 4 palette loads** (sprite.hlsl:181). Fix: a single-tap branch when the sample is sharp and the fraction is 0, as at zoom 1.
- **Three entity lookups per mobile in the extract** (Flags, Serial, Equipment; Uo.Client RenderExtract.cs:119). Fix: a query over those components, iterating chunk spans.
- **The extract walks 23 equipment layers per human per frame** (Animation.cs:240). Fix: cache a compacted, ordered slot list when equipment changes.
- **Done (pack-hitch agent):** `ReadHeader` on Kind 2 decoded all of chunk 0 (1 MiB) per page at startup (PackSet.cs:65,83). `AtlasPage.ReadHeader` now decodes only the 16 header bytes: 2.8–4.6 → 0.36–0.39 ms per page (PackSet reads 36 headers at startup, so about 100–160 ms → 14 ms).
- **All 37.9k palettes (18.5 MiB VRAM) plus a 4 MB FramePalette table load at startup** (PackSet.cs:92). Fix: stream them per body.
- **Done (c18e96a6b):** per-worker Kind 2 decoders, which removed the global lock (anim pages 390 → 2590 pages/s), and an Indexed8 single-tap fast path (GPU 0.565 → 0.50 ms at 4K with 4000 mobiles).
- **The vertex shader runs the chunk-table binary search on all 6 vertices of each quad** (sprite.hlsl VSMain). An indexed 4-vertex quad would cut vertex work by about a third.
- **Done (pack-hitch agent):** the Kind 2 branch read the ChunkedHeader twice (PageResidency.cs). `ChunkedBrotliDecoder.Decode` has an overload that takes the header already read.
- **Decoder and worker threads can oversubscribe small CPUs** (2×8 decoder threads plus 8 workers). Size the pools by core count.
- **Dynamic-sprite page requests run twice** when the extract already requests them (`WorldRenderer.BuildFrame` and Uo.Client's extract).
- **Roof hiding through per-sprite overrides can mean thousands of overrides.** A max-Z uniform plus a per-instance z test would be cheaper. **Done in step 9** (see below).
- **Dynamic instances re-upload every frame** (Renderer.cs:314), about 400 KB at 5000 mobiles. Fine for now; ties in with ground-item chunk baking.

## ClassicUO pack-loading path (pack-assets agent, 2026-10-24)

**Done (pack-hitch agent, 2026-10-24; numbers and method in poc-log.md and [testing/pack-loading.md](testing/pack-loading.md)):**
- **Chunk decode buffers pooled** (PackAssets.cs `PackPages`): evicted 1 MiB buffers are reused and the compressed chunk is rented. Allocation per chunk decode 1.27 MB → about 60 KB; decode now runs outside the lock, so workers decode in parallel.
- **Picker masks and real-art bounds from the trim rect** (`PixelPicker.SetTrimmed`, `PixelPicker.VisibleBounds`): only the trim rect is scanned, the picker with a Vector256/128 zero test (scalar fallback, parity tests). Picker 4.7 → 3.2 µs per sprite (scalar trimmed 4.7, so SIMD kept); bounds 4.8 → 0.4–1.0 µs. The untrimmed RGBA image is still built because the legacy atlas uploads it, but into a pooled buffer.
- **First sight of a body decodes on a worker** (Animation.cs): until a body has a direction uploaded, a direction whose chunks are not cached is decoded on the thread pool and drawn as nothing until ready; uploads are budgeted (2 ms per 8 ms). Main-thread cost of a first sight 3.4–4 ms (max 11–14 ms) → 0.2 µs; `bodies` test Update+Draw max 24–25 → 4.3–8.2 ms, frames over 8 ms 43–76 → 0–1, allocation 57–122 → 19 KB per frame. `AnimationExists` no longer decodes. `CUO_ANIM_SYNC=1` restores the old behaviour.
- **Region loads decode into pooled buffers** (`RegionFile.DecompressInto`, MapLoader buffer recycling): 4.0–4.2 → 3.0–3.3 ms and 3.9 → 1.1 MB per region on average over 60 loads (0 once the pool is warm).
- **Stretch lookups cache the last block** (`MapLoader.GetLandZ`): 110–180 → 12 ns per lookup.
- **Palette cache is an LRU** (`LruCache`, evicted arrays reused).

**Still open in this path:**
- **A teleport still costs one 25–40 ms frame:** about 72–81 `Chunk.Load` calls (5–14 ms) plus a region Brotli decode (3–9 ms) in the same frame, plus the server's packet burst. Fix: load chunks incrementally over frames, and decode regions on a worker (outside MapLoader's lock, as PackPages does) with a prefetch of the neighbouring regions when walking.
- **A visible body changing to an action it has never shown** still decodes synchronously (about 3 ms, 15 in a 20 s bodies run), to avoid a blink. Prefetch covers its other directions and actions 0–4.
- **Unattributed 250–750 ms frames** with every phase under 1 ms show up in both builds while other clients run; not pack loading.
- **The atlases hold RGBA8 and never evict** (TextureAtlas, which predates this work). Fix: 16-bit (`Bgra5551`) atlases at half the memory, plus eviction. Becomes moot for the world once the GPU renderer is the default.

## Pipelining

- **No evidence ClassicUO 2.0 pipelines,** apart from a host upload worker. Revisit a render thread, or overlapping the next update with submission, only if Present or submit is still ≥30% of the frame after the items above.

## GPU world in ClassicUO (step 8, 2026-10-24)

- **ClassicUO's list fill is now the world's main cost in Gpu mode:** 0.5 ms zoomed out at 1080p and 2.9 ms at 4K (classicuo-perf-baseline.md, "Step 8"). It walks a square tile range much larger than the screen, tests every object and pixel-checks every static for the mouse. In Gpu mode it only has to produce exceptions (hidden, alpha, tint) and the selection. Fixes: walk only the screen's diamond; cache per-tile decisions and redo them only when the player's max-Z, the alpha timer or the tile change; run the selection test only for tiles under the mouse.
- **`Static.CheckMouseSelection` runs for every static on screen every frame** (StaticView.cs:95, called from `TrySelectObject`, GameSceneDrawingSorting.cs:940), with a `PixelPicker` dictionary lookup each time. About 3.7% of main-thread samples zoomed out. Fix: reject by the art rectangle first. This helps Legacy as well.
- **Done (step 9): roof hiding as per-sprite overrides:** 478 overrides at the bank at zoom 1 and 3235 zoomed out. Each one is a `ChunkStore.FindObject` binary search on the main thread (WorldRenderer.cs:315), and the whole 16k-slot hash (128 KB) is uploaded whenever any override exists (Renderer.cs:409). Crowd zoomed out: 0.27 ms of world draw. Fix: a max-Z (and roof-flag) uniform with a per-instance z test in the shader, as noted for the slice.
- **Item and mobile capture costs what drawing them through FNA did** (120 mobiles: world prepare +0.2 ms), because the views' logic (frame selection, layer order, hue rules) runs either way. Fix: feed dynamics from the ECS `DynamicSpriteExtract` (`GpuWorldRenderer.DynamicBuffer` is the seam) or cache each mobile's sprite list until its animation frame changes.
- **Chunk meshes are still built in Gpu mode** (GameScene.cs:648), only for `InChunkMesh`/picking parity. That is a CPU and VRAM cost per dirty chunk; the art atlases are also still filled for the picker. Fix: build a picker-only path once the Legacy renderer is removed.
- **The override collection is a second walk over each tile** (`CollectGpuHidden`). It is cheap after skipping tiles beside the view (about 0.03–0.1 ms zoomed out), but it could disappear if the fill reported skipped objects itself.

## GPU world parity (step 9, 2026-10-24)

- **Measured result of the shader max-Z/roof test:** steady-state overrides went 478 → 39 at the bank, 129 → 71 at Britain and 232 → 182 zoomed out. Bank world draw went 0.082 → 0.057 ms. Most of the remainder had been hidden-land overrides for tiles outside the view (`GpuLandVisible` had 108/64 px slack); the tight 44×44 test removed them.
- **Translucent statics are still one override per sprite per frame** (tiledata Translucent → `AlphaHue` 178, e.g. windows and some water). Fix: bake alpha 178 into the instance, and give each chunk a translucent sub-list the translucent pass can draw without a CPU walk. The override would then be needed only while an object fades.
- **The out-of-range tint (`NoColorObjectsOutOfRange`) is a per-sprite override for every object beyond the view range.** The death-screen gray had the same shape: about 2,800 overrides at zoom 1, and zoomed out it would have overflowed the 8,192-entry pending list. It is now a uniform (`TintRow`). Fix for out-of-range: a uniform with the player tile and range, tested per instance (the instance has no tile coordinates yet; derive them from X/Y and z, or bake them).
- **The remaining zoomed-out overrides (182) are the fill's other skips,** mostly land at the view edges (the top strip that `_minPixel` culls when zoomed out). They disappear with the "walk only the screen's diamond / per-tile decision cache" item above.
- **The circle-of-transparency ring is a CPU scan of the chunks inside the circle's box every frame** (`WorldRenderer.BuildFrame`), 8 ring instances at zoom 1. It is cheap, but it could be cached per player tile and chunk generation.
- **A season change rebakes by evicting every chunk** (`ChunkStore.EvictAll`), so the view re-streams for a few frames. Fix if it matters: rebake into a second range and swap when ready (double-buffered slots).
- **Effects now go through the capture path** (their views run in world prepare). Measured: FNA draw calls in Gpu mode went 24–27 → 23, and Britain world draw 0.086 → 0.059 ms.

## Robustness

- **Done (2026-10-24): after a GPU device reset the client now exits cleanly instead of running away on memory.** SDL 3.4's Vulkan backend never cleans a command buffer after `VK_ERROR_DEVICE_LOST`, and FNA3D ignores the failures, so each frame allocated new transfer, uniform and command buffers (50 GB in 3 s in the owner's run). `GpuDeviceLost` detects the loss from SDL's error text after every present, from a failed Uo.Render submit, or from FNA3D's log hook. It then saves, shows a message box and exits with code 0xDEAD, so at most a frame or two leak. Root cause and code references: [device-lost-leak.md](device-lost-leak.md).
  - Remains: **recovery** (recreate the device and reload every texture instead of exiting). Also the cause of the 2026-10-24 21:36:29 GPU page fault, which was in FNA3D's frame submit and points at SDL's defragmenter moving Uo.Render's page atlas; it is not yet reproduced on demand.

## Allocation follow-ups (after B1–B5, 2026-10-24)

- **The NativeAOT remainder of about 250–270 B per frame (JIT: 69–125 B).** Not worth chasing on its own: about one gen0 GC every few tens of seconds at 1,000 fps, and none were seen in the measurement windows. Trace it once on a quiet machine to tell real AOT allocations (boxing, generic paths) from background asset-streaming allocations counted across all threads. Low priority.
- **What matters is allocation that scales with the scene:** bytes per mobile, per gump, per overhead text line. Check that allocation per frame stays flat in the large-scale test (about 2,000 mobiles and 20,000 items). The FPS test's allocation row guards against regressions.
