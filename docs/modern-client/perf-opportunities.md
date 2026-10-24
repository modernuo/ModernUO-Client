# Performance Opportunities (backlog)

This is a living list. Add to it whenever work turns up a likely performance win. We tackle these **after** the PoC is proven, measuring each one with the per-phase timing hooks (integration plan step 7b) to see whether it moves FPS.

Format: **what**, where (file:line), why it should help, evidence or estimate, and source.

## Frame loop / Update

- **World.Update walks every mobile and every item, including items inside containers, every frame** (World.cs:259-398; `Entity.Update` → `UpdateHits`, `Item.ProcessAnimation`).
  - Fix: skip contained items; keep active lists for animating items and mobiles; drive it through ECS systems.
  - Source: cuo-2.0-perf-comparison.md.
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
- **The 250 FPS cap** (Constants.cs `MAX_FPS`, `Thread.Sleep(1)` limiter). Remove it; uncapped by default with a VSync toggle (owner).

## Rendering

- **World prepare plus draw is about 0.35 ms at Britain, zoom 1** (more when busy or zoomed out). Replaced by Uo.Render: chunks baked once on the GPU, one draw call. Integration plan step 8.
- **The UI render target is cleared and redrawn every frame** (GameController.cs:528-549). Fix: cache it and redraw only on dirty.
- **FNA per-draw overhead:** C# state application, MojoShader uniforms and several FNA3D calls per batch flush. Measure flushes per frame first. Fix if significant: a record-then-submit command-list device, which is also the WASM and WebGPU path. Source: 2.0 `IGpuDevice`.
- **The faux backbuffer blit plus the RT composite at window size** (3840×2054 in the baseline; FNA3D_Driver_SDL.c:1119-1215, RenderTargets.cs:94-147). Fix: render the world straight into the swapchain image and skip the composite. Needs another FNA3D patch; low-to-medium gain at 4K.
- **Ground-item chunk baking:** bake rarely-changing ground items into chunk buffers and re-bake on ECS changes. Only if the dynamic extract or upload shows up in busy scenes. Source: 2.0 `BakeChunkDynamics`.
- **NativeAOT instruction set:** build with `IlcInstructionSet=x86-x64-v3`. JIT already uses AVX2, and AOT is 10–15% slower on world draw without it.

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
