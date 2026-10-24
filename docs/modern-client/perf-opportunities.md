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
- **About 17–18 KB allocated per frame**, roughly 4 gen0 GCs every 5 s (classicuo-aot-baseline.md). Fix: hunt allocations down to zero, and keep allocation and flush probes permanently in the build. Source: 2.0 `EcPerfProbe`, `BatcherProbe`.
  - Measured: 17.7 KB per frame at idle and 21 KB while travelling; GC pause 0.3–1.5 ms/s. AllocationTick sampling puts about 90% in the UI draw path; the three quick wins follow.
- **`Control.DrawDebug` allocates a closure display class on every call, even with debug off** (Control.cs:328). C# hoists the captured-variable class to method entry, so every visible control allocates one per frame: about 20% of all per-frame allocation. Fix: early-return before the lambda, or move the lambda into a separate method. Trivial. Source: step 7b allocation trace.
- **UI controls allocate a `Func<UltimaBatcher2D,bool>` plus a display class per draw per frame** (`GumpPic`/`GumpPicTiled`/`Button`/`HtmlControl.AddToRenderLists` → `RenderLists.AddGumpWithAtlas/NoAtlas`). This is about 60% of per-frame allocation. Fix: a struct command (texture, rects, hue, depth) in a pooled list instead of closures. Source: step 7b allocation trace.
- **`ResizePic` and `StbTextBox` render lambdas create a new `RenderLists` (seven `List<>`s) every frame** (ResizePic.cs:249). Fix: reuse a cached instance per control. Source: step 7b allocation trace.
- **`PaperDollGump.EquipmentSlot.Update` builds `$"{Layer} slot"` and calls SetTooltip every frame for each empty slot** (PaperDollGump.cs:743). Fix: set it only when the slot becomes empty. Source: step 7b allocation trace.
- **Done (step 7b): the 250 FPS cap is removed.** `fps: 0` (uncapped, no Sleep limiter) is now the default, with a VSync setting and options-gump checkbox mapped to `PresentationInterval` at runtime. "Reduce FPS when inactive" defaults off and is forced off under `CUO_FRAMESTATS`.

## Rendering

- **World prepare plus draw is about 0.35 ms at Britain, zoom 1** (more when busy or zoomed out). Replaced by Uo.Render: chunks baked once on the GPU, one draw call. Integration plan step 8.
  - Measured: 0.22 ms at Britain at 1080p, 1.09 ms zoomed out (12k render-list entries, 326 draw calls) and 5.2 ms zoomed out at 4K (40k entries, 1,041 draw calls). The top cost in every heavy scene.
- **The UI render target is cleared and redrawn every frame** (GameController.cs:528-549). Fix: cache it and redraw only on dirty. Measured: UIManager.Draw is 0.05 ms with 4 gumps; the allocation items above come first.
- **FNA per-draw overhead:** C# state application, MojoShader uniforms and several FNA3D calls per batch flush. Fix if significant: a record-then-submit command-list device, which is also the WASM and WebGPU path. Source: 2.0 `IGpuDevice`.
  - Measured: 16–18 flushes plus 46–1,041 draw calls per frame. Time inside FNA calls is 0.08–0.11 ms at zoom 1, 0.29 ms zoomed out and 1.07 ms zoomed out at 4K. In CPU samples, `Effect.INTERNAL_applyEffect` is 9.4% and `ApplyStates` 8.1%.
- **`ApplyStates` re-sends the full matrix and viewport uniforms and calls `Pass.Apply()` on every flush, and `DrawDirectIndexed` calls `Pass.Apply()` once per mesh run** (Batcher2D.cs `ApplyStates`, `DrawDirectIndexed`). Fix: dirty-track the matrices and the viewport and apply the pass once per state change; draw each chunk layer's runs with one apply. Cheap, and it helps until Uo.Render replaces the world path. Source: step 7b CPU samples.
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
- **`ReadHeader` on Kind 2 decodes all of chunk 0 (1 MiB) per page at startup** (PackSet.cs:65,83). Fix: decode only the PageHeader bytes.
- **All 37.9k palettes (18.5 MiB VRAM) plus a 4 MB FramePalette table load at startup** (PackSet.cs:92). Fix: stream them per body.
- **Done (c18e96a6b):** per-worker Kind 2 decoders, which removed the global lock (anim pages 390 → 2590 pages/s), and an Indexed8 single-tap fast path (GPU 0.565 → 0.50 ms at 4K with 4000 mobiles).
- **The vertex shader runs the chunk-table binary search on all 6 vertices of each quad** (sprite.hlsl VSMain). An indexed 4-vertex quad would cut vertex work by about a third.
- **The Kind 2 branch reads the ChunkedHeader twice** (PageResidency.cs; `Decode` re-reads it).
- **Decoder and worker threads can oversubscribe small CPUs** (2×8 decoder threads plus 8 workers). Size the pools by core count.
- **Dynamic-sprite page requests run twice** when the extract already requests them (`WorldRenderer.BuildFrame` and Uo.Client's extract).
- **Roof hiding through per-sprite overrides can mean thousands of overrides.** A max-Z uniform plus a per-instance z test would be cheaper.
- **Dynamic instances re-upload every frame** (Renderer.cs:314), about 400 KB at 5000 mobiles. Fine for now; ties in with ground-item chunk baking.

## ClassicUO pack-loading path (pack-assets agent, 2026-10-24)

- **A new 1 MiB buffer plus a compressed buffer per chunk decode** (PackAssets.cs:234,246). Fix: pool them.
- **A full untrimmed RGBA image is built, then rescanned for the picker and real-art bounds** (PackSprites.cs:16,46; Art.cs:74,82; Animation.cs:204,207). Fix: derive both from the pack's trim and Indexed8 data.
- **A whole animation direction is decoded synchronously the first time a body is seen** (Animation.cs:204), causing a hitch. Fix: prefetch or decode on a worker.
- **Region loads copy the file 2–3 times** (MapLoader.cs:222; PackReaders.cs:260-264): a full read, then MemoryStream/ToArray, about 1.2 MB per copy. Fix: decode straight into a pooled buffer.
- **Stretch calculation takes a lock and a dictionary lookup per neighbour cell** (Chunk.cs:94 → Map.cs:114). Fix: cache the current block.
- **The palette cache is cleared wholesale at 8,192 entries** (AnimationsLoader.cs:196). Fix: LRU eviction.
- **The atlases hold RGBA8 and never evict** (TextureAtlas, which predates this work). Fix: 16-bit (`Bgra5551`) atlases at half the memory, plus eviction. Becomes moot for the world once the GPU renderer is the default.

## Pipelining

- **No evidence ClassicUO 2.0 pipelines,** apart from a host upload worker. Revisit a render thread, or overlapping the next update with submission, only if Present or submit is still ≥30% of the frame after the items above.

## GPU world in ClassicUO (step 8, 2026-10-24)

- **ClassicUO's list fill is now the world's main cost in Gpu mode:** 0.5 ms zoomed out at 1080p and 2.9 ms at 4K (classicuo-perf-baseline.md, "Step 8"). It walks a square tile range much larger than the screen, tests every object and pixel-checks every static for the mouse. In Gpu mode it only has to produce exceptions (hidden, alpha, tint) and the selection. Fixes: walk only the screen's diamond; cache per-tile decisions and redo them only when the player's max-Z, the alpha timer or the tile change; run the selection test only for tiles under the mouse.
- **`Static.CheckMouseSelection` runs for every static on screen every frame** (StaticView.cs:95, called from `TrySelectObject`, GameSceneDrawingSorting.cs:940), with a `PixelPicker` dictionary lookup each time. About 3.7% of main-thread samples zoomed out. Fix: reject by the art rectangle first. This helps Legacy as well.
- **Roof hiding as per-sprite overrides:** 478 overrides at the bank at zoom 1 and 3235 zoomed out. Each one is a `ChunkStore.FindObject` binary search on the main thread (WorldRenderer.cs:315), and the whole 16k-slot hash (128 KB) is uploaded whenever any override exists (Renderer.cs:409). Crowd zoomed out: 0.27 ms of world draw. Fix: a max-Z (and roof-flag) uniform with a per-instance z test in the shader, as noted for the slice.
- **Item and mobile capture costs what drawing them through FNA did** (120 mobiles: world prepare +0.2 ms), because the views' logic (frame selection, layer order, hue rules) runs either way. Fix: feed dynamics from the ECS `DynamicSpriteExtract` (`GpuWorldRenderer.DynamicBuffer` is the seam) or cache each mobile's sprite list until its animation frame changes.
- **Chunk meshes are still built in Gpu mode** (GameScene.cs:648), only for `InChunkMesh`/picking parity. That is a CPU and VRAM cost per dirty chunk; the art atlases are also still filled for the picker. Fix: build a picker-only path once the Legacy renderer is removed.
- **The override collection is a second walk over each tile** (`CollectGpuHidden`). It is cheap after skipping tiles beside the view (about 0.03–0.1 ms zoomed out), but it could disappear if the fill reported skipped objects itself.
