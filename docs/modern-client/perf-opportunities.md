# Performance opportunities (backlog)

Open items only, one line each: what, where, estimated benefit, status. Done items are in the compact list at the bottom. Accepted costs are in [accepted-costs.md](accepted-costs.md). New items come from agent reports ("Perf opportunities"); we take them one at a time as small, measured changes. Per-phase numbers: classicuo-perf-baseline.md (1080p JIT: Update ~0.012 ms, Draw 0.2-1.2 ms, Present 0.12 ms; at 4K Present plus composite ~0.57 ms, zoomed out 5.7 ms/frame).

"Goes with T9" marks items the T9 deletions (hot caches, `World` tables, GpuItemCache signatures, off paths) remove: do not fix them separately.

## Frame loop and UI

| What | Where | Estimate | Status |
|---|---|---|---|
| UIManager.Update sorts and recursively updates every control each frame | UIManager.cs:335, Control.cs:259 | update only dirty controls, hit-test cache | open |
| UI pass walk + vertex generation (~50 us with static UI) | batcher UI pass | needs dirty tracking; draw-time side effects (`HealthBarGump`, `ContainerGump` set `SelectedObject`) block it | open |
| `Control.InvokeMouseOver/Enter/Exit` allocate `MouseEventArgs` before checking listeners | Control.cs | ~32-40 B/frame while hovering | open |
| `UIManager.SortControlsByInfo` allocates a list node per Over gump (`AddFirst(c)`) | UIManager | use `AddFirst(first)` | open |
| `HandleKeyboardInput` walks every gump every frame | UIManager | CPU | open |
| `ItemGump` calls `World.Items.Get` per container item per frame; `ItemGumpFixed` per paperdoll slot | ItemGump.cs ~188/236, PaperdollGump.cs ~866 | use `EntityRef` | open; the lookup goes with T9 tables |
| Container/paperdoll item art decodes on the main thread when a gump first opens | request in `WorldWriter.PlaceInContainer` | ~20-50 us per sprite, first-open hitch | open |
| `HealthLinesManager` with `ShowMobilesHP` on still walks every mobile; hoist `HitsTexture`/`CurrentProfile` | HealthLinesManager.cs | CPU | open |
| `NameOverHeadManager.TypeAllowed` reads `CurrentProfile` per object per frame | NameOverHeadManager.cs:35 | tiny | open |
| `Party.Contains` per guild entity per frame (world map); `RequestServerPartyGuildInfo` builds a buffer every 250 ms (unverified); `HouseCustomizationGump` LINQ | perf-alloc-4 | low | open |
| Season change reads the race per object | Map.cs:338, World.cs:324 | minor, rare | open |
| Blocking main-thread waits (Present, limiter sleep) lose their quantum under load | frame limiter | 2 ms wait ~500 ms under 40 busy loops; ~4 ms with raised priority | decided 2026-11-08 (raise only around sleep/Present, lower when minimized); not started, needs timing under load |
| Event-driven main loop: today the frame cap is SuppressDraw + `Thread.Sleep(1)`, so Update runs every ~1-2 ms even when focused; wait instead until the earliest of draw deadline, timer due, network data and input (one high-resolution wait; sticky wake; late-wake backoff), PoC inline wait vs receive thread vs ring-based receive | GameController.cs:574-600, NetClient.Drain | wakeups/CPU per client (multi-instance), no added latency | planned project; design cuo-poc-state surveys/event-loop-2026-11-11.md |
| Networking buffers: ModernUO `SpanReader`/`SpanWriter` (BSD reuse approved by the owner) instead of `CircularBuffer` and `StackDataReader`/`StackDataWriter`; double-mapped receive buffer from IORingGroup (approved) so `ReadSlice` sees contiguous spans; RIO/io_uring receive for fewer syscalls; delete the WebSocket path (`WebSocketWrapper`, `OrderedAsyncWriter`: ClassicUO Web/WASM leftovers; owner 2026-11-11), TCP only, `NetClient` may be rewritten (a WASM transport comes later behind it) | Network/CircularBuffer.cs, ClassicUO.IO | per-packet copies and syscalls | with the event-loop project |
| Collections audit: custom collections and BCL collections in per-frame paths (boxing enumerators, resize churn, LinkedList nodes, Dictionary lookups per object) vs ModernUO's pooled/ref collections | client-wide | unknown; rows only for measured hot spots | open (owner 2026-11-11) |
| `FirstSightDecoder.RequestAndWait` overshoots its stall slice on a loaded machine | FirstSightDecoder.cs:217, DecodeQueue.cs ~136 | 2 ms slice measured 1.2-3.5 s with starved workers | open, check the `ManualResetEventSlim` spin and worker priority |
| Pause FAudio mixing while minimized | audio | CPU | open, with the minimized-priority work |

## Pacing-gap items (2026-11-12)

From a comparison with a reference client (reports in cuo-poc-state/audits (pacing gap)). An external user measured ours at ~1362 FPS, ping 2-7 ms, and a reference client at 1750-1850 FPS, ping 1-2 ms (it counts draws and presents only at the display rate). Order of work: M1 and M2 now, then M3-M5. Each lands behind a switch (default off) with a checker, plus a counts job (draws, presents, updates, objects walked, upload calls/bytes, alloc/frame) run now and a PARKED timing job for the quiet pass.

| What | Where | Estimate | Status |
|---|---|---|---|
| M1: uncapped and no vsync: draw and present at the display rate, run Update (network, input) every loop; report loop rate separately from presented FPS. Probably the ping jitter (see "Present stalls" under Rendering) | GameController.cs:445 (frame delay), :972 (EndDraw) | removes draw+present of unseen frames; steadier ping | merged 8b68f0495 (`present_at_display_rate`); default on since a885ab222a368ad4edf40d4c0f2245f086e5d36c; ping RTT GPU 8.8 -> 0.84 ms, Legacy 7.9 -> 0.55 ms (busy spot); quiet timing pending |
| M2: skip world prep (`FillGameObjectList`) when the view and the world are unchanged; `WorldWriter` gives a change epoch; The reference client also throttles its pick/text walk to ~30 Hz | GameScene.cs:599, GameSceneGpuWorld.cs:377 | largest per-frame CPU item when standing still | parked (bg/prep-reuse, 3 rounds): quiet reuse ~98 %, busy 0 % (ChunkRetry forces a full fill every draw); gate never ran (no >=500-object quiet spot found) |
| M3: Uo.Render per-frame maintenance (streaming marks, eviction scan, visible chunk table, page touches) reused while the view rect and chunks are unchanged | WorldRenderer.cs:261, ChunkStore.cs:93/471 | CPU per frame | merged ad77d6654 (`render_view_reuse`, default off); chunk table reused 100 %, eviction scans 0; quiet timing pending |
| M4: skip unchanged per-frame uploads (chunk table, dynamic instances, translucent list) and keep the sorted translucent order until something changes | Renderer.cs:703-751, WorldRenderer.cs:521/570 | upload calls/bytes per frame; see "dynamic instances re-upload" under Rendering | merged ad77d6654 (`upload_skip_unchanged`, default off); dynamic upload skipped 99.7 % quiet / 77 % busy, translucent order reused 97-100 %; quiet timing pending |
| M5: UI cache still walks gumps, builds glyph quads and compares the recording every frame; per-gump dirty version to skip recording; one quad per glyph in RenderedText | GameControllerUiCache.cs:114, RenderedText.cs:668 | CPU with text-heavy UI | merged 5ead9ecae (`text_quad_cache`, `ui_gump_dirty`, default off); glyph quads reused 100 % while unchanged; gump replay 0 on real UIs (common gumps always-dirty); GPU fps drop with both on to check |

## Allocation (per-frame and scene-scaling)

| What | Where | Estimate | Status |
|---|---|---|---|
| Pool `Item`s entering range | `World.GetOrCreateItem` (280 B each) | ~4.3 MB per 15 s of ls_walk | switch `item_pool` (default off, `bg/item-pool`); alloc jobs pending, then flip |
| Pre-size the item pool's queue, or prefill the pool at login | `ItemPool` (`Queue<Entry>` grows by doubling to 8,192 entries, a 128 KB LOH array once) | one LOH allocation; first-walk reuse | open (after the item-pool alloc results) |
| Measure text without rasterizing for the `RenderedText` atlas path | RenderedText / FontsLoader | part of ~40 % of ls_walk allocation | open |
| Presize or pool `GpuItemCache` arrays | `Array.Resize` | ~1 MB per 15 s Gpu ls_walk | open (signatures go with T9) |
| Cache `ClilocLoader.Translate` + `CapitalizeAllWords` for empty args | ClilocLoader | ~2.2 MB per 15 s ls_walk | open |
| Create the journal `RenderedText` for `DisplayClilocString` lazily | journal | part of text allocation | open |
| One-off ~677 KB allocation once per Legacy run, 20-30 s after login | unknown | 1 frame; raised town_walk tipon avg to 92 B/frame | open; not reproduced in 3 JIT runs, likely NativeAOT-only (lazy static init/type loader), needs an AOT-side method |
| Name-gump creation ~600 B/frame for first windows of the handles walk | ui_update | one-time per new mobile | open (low) |
| Remaining NativeAOT per-frame floor and scene-scaling allocations | FrameStats alloc/frame | keep flat per mobile, gump, text line | check on every branch (alloc jobs) |

## Rendering (Gpu world, FNA, Uo.Render)

| What | Where | Estimate | Status |
|---|---|---|---|
| FNA per-draw overhead (C# state apply, MojoShader uniforms, FNA3D calls per flush) | FNA | 0.08-0.11 ms at zoom 1, 1.07 ms zoomed out at 4K; `applyEffect` 9.4 %, `ApplyStates` 8.1 % | open; fix is a record-then-submit command-list device (also the WASM/WebGPU path) |
| Faux backbuffer blit + RT composite at window size | FNA3D_Driver_SDL.c:1119-1215, RenderTargets.cs:94-147 | ~0.4 ms/frame at 4K | open, needs an FNA3D patch |
| Present stalls 4-5 ms on ~4 % of frames above ~1000 fps | SDL_GPU frames in flight | misleading avg/p99 at high fps | M1 avoids them when uncapped (present at the display rate); open otherwise; try mailbox-style present or more frames in flight; compare p50 meanwhile |
| NativeAOT `DirectPInvoke` for FNA3D/SDL3 | build | not measured | open, needs generated import libraries |
| AOT `RtlWalkFrameChain` 4-5 % of render-thread samples (likely GPU tool injection) | profiling | ~0.03 ms/frame | re-measure on a machine without GPU tools |
| List fill is the world's main Gpu cost (walks a square tile range, tests every object): walk the screen's diamond, cache per-tile decisions | GameScene fill | 0.5 ms zoomed out 1080p, 2.9 ms at 4K | open |
| `Static.CheckMouseSelection` for every static every frame (reject by art rect first) | StaticView.cs:95, GameSceneDrawingSorting.cs:940 | ~3.7 % of main-thread samples zoomed out; helps Legacy | open |
| Chunk meshes still built in Gpu mode (picker parity) and art atlases still filled for the picker | GameScene.cs:648 | CPU and VRAM per dirty chunk | open; picker-only path once Legacy is removed |
| `CollectGpuHidden` second walk over each tile; remaining zoomed-out overrides (182) are the fill's skips | WorldRenderer | 0.03-0.1 ms zoomed out | open; disappears with the diamond walk |
| Circle-of-transparency ring scans chunks every frame | `WorldRenderer.BuildFrame` | 8 ring instances at zoom 1, cheap | open; cache per player tile and chunk generation |
| Season change rebakes by `ChunkStore.EvictAll` (view re-streams a few frames) | ChunkStore | - | open; double-buffered slots if it matters |
| Three entity lookups per mobile in the Uo.Client extract; 23 equipment layers walked per human per frame | RenderExtract.cs:119, Animation.cs:240 | - | open (Uo.Client only) |
| All 37.9k palettes (18.5 MiB VRAM) + 4 MB FramePalette load at startup | PackSet.cs:92 | stream per body | open |
| Vertex shader runs the chunk-table binary search on all 6 vertices of each quad | sprite.hlsl VSMain | indexed 4-vertex quad, ~1/3 less vertex work | open |
| Decoder + worker threads oversubscribe small CPUs (2x8 + 8) | Uo.Render | size by core count | open |
| Dynamic-sprite page requests run twice; dynamic instances re-upload every frame (~400 KB at 5000 mobiles) | `WorldRenderer.BuildFrame`, Renderer.cs:314 | - | open; ties in with ground-item baking |
| Expanding 16-bit pages to RGBA8 costs 3-5 ms per page (suspect write-combined memory) | renderer | only the WebGPU RGBA8 fallback | open |
| Co-occurrence page packing v2 | pack-format-v0.md | fewer pages per screen | open |
| TextureAtlas holds RGBA8 and never evicts | TextureAtlas | moot once Gpu is the only path; Gpu mode fills it too (survey anim-frame-cache-2026-11-11) | open; the advance's share in Gpu mode behind `anim_frame_count_tables` (bg/anim-count-tables, results pending) |
| Gpu mode still loads Legacy anim directions for picking, health bars and overhead positions (`GetAnimationDimensions`: one direction per body) | MobileView.cs:80, Animation.cs:113 | dimensions from the tables (OrigW/OrigH, centre) need no decode; picking needs masks, not the atlas upload | open |
| Anim pages packed body-major spread crowds over ~1 page per body | AnimConvert.cs:464-495 | action-major hot actions: all equipment stand frames 357 -> ~4 pages | open (survey G1) |
| Legacy anim atlas stores untrimmed frames; TextureAtlas abandons the old page's free space | Animation.cs:327, TextureAtlas.cs:79 | -20 % atlas area | open (survey L2) |
| GPU device-lost recovery (recreate device, reload textures) instead of exit 0xDEAD | GpuDeviceLost | robustness | open |
| Visible body changing to an action it never showed decodes synchronously (~3 ms, 15 in a 20 s bodies run) | Animation | avoids a blink | open |

## Streaming, GC and loading

| What | Where | Estimate | Status |
|---|---|---|---|
| Collections remaining after login warm-up (one gen0 ~+31-36 s while teleporting 3.5-5 ms, then one every few minutes) | art/texmap decode pool misses, bake arrays, `Map._usedIndices` node per chunk load, statics mesh growth, LOH region buffers | options: pooled decode/region buffers, a node per chunk | open; `DOTNET_GCgen0size` is ignored by NativeAOT |
| `Dictionary<UInt64,Int32>.FindValue` 1.8 % of Legacy britain_z samples | caller not traced | an array may replace it | open |
| Server update range: bound the walk by range (18, 24 for houses), stream assets by range, warn about huge viewports, read range from 0xC8 | whole world path | scales with viewport tiles | deferred; edge reconciliation (known-issues "ghost mobiles") comes first; rule meanwhile: no per-frame Gpu work that scales with viewport tiles if it can be per chunk or per change |
| Equip update for a mobile the client no longer holds (0x2E from ModernUO `Item.ProcessDelta`, Item.cs:1540-1573) | client attach path | must attach on arrival or drop, never half-apply | open; ModernUO audit: should `ProcessDelta` respect `IsSentTo`? |
| Gump tooltips for objects the client never saw (0xD6 after an `itemproperty` gump) | `ObjectPropertiesListManager` keys by serial; server request handler may refuse unsent serials | owner requirement | repro first |
| `World.Update` mobile loop as a system over animating mobiles; animation clock into the ECS | World.cs | 0.035-0.06 ms at 2,000 mobiles today; scales with non-vanilla features | deferred, E4 covers the due-time schedule |
| Network: ModernUO-style double-mapped pipe (no `CircularBuffer`, no staging copy); audit string-allocating handlers | NetClient | removes one copy per packet | deferred, HIGH RISK, own project (parity tests, soak) |
| Startup: time each loader, then defer (L6); tile names decoded on first access (L8); entry views in the converter (L9) | loaders | mostly moot now that gumps/fonts/cliloc/sounds are in the pack | optional |

## ECS, extract and Legacy sprites

| What | Where | Estimate | Status |
|---|---|---|---|
| `ItemOffsets.ReadEcs` runs for every item every frame | ItemOffsets | 2.5-3.1 % of the large scene; a "has offset" flag on the item | open |
| `MobileAnimationSystem.UpdateInPlace` -> `SamplePushed`/`SelectCached` build and compare a ~15-field `SelectKey` per drawn mobile per frame | MobileAnimationSystem.cs:121-207 | 0.1-0.3 us/mobile, 1.5 % of profile; dirty bit + `NextFrameTime` check | open |
| `GpuWorldRenderer.TryDrawMobileEcs` finds the dirty flag through a second lookup (as fixed in 37237a511); `TakeDirty` reads tags before the record | GpuWorldRenderer.cs:729, ~611 | one lookup per drawn mobile | open |
| Mobile replay builds a ~80 B draw struct per entry | MobileSpriteExtract.cs:770 | 2.2 % of profile | open |
| `ItemSpriteInput` `i = default` zero-fill ~90 B/item; `HueInput` copied in `Run`; `ArtLoader.GetEntry` twice in `Art()` (~0.5 %); `new GpuSpriteTables(pack)` per item in `Emit`; `EmitCorpse` re-reads loaders; corpse-strip interface dispatch | ItemSpriteExtract.cs (~272 for tables) | small each | open |
| Corpse layer: equipment conversion looked up twice per layer; destroyed items held until reused (memory) | ItemSpriteExtract.CorpseLayer ~540; WorldState.Equipment.cs ~198 | - | open |
| Legacy sink: `GetAnimationFrames` once per strip and again per layer | LegacySpriteResolve.cs:87, `MobileSpriteExtract.Layer` | 3-5 lookups per mobile layer | open |
| Mobile pixel check redoes texture/layer/equip lookups once the rect hits | MobileView.CheckMouseSelection ~1060-1270 | use the draw records | open |
| `Mobile.IsMounted` walks the equipment list twice | Mobile.cs:206-227 | check whether `ecs_step_mount_inputs` covered it | verify |
| `EffectSystem.Animate` + `Publish` do two `EcsRead.Get` per effect per frame | EffectSystem.cs ~130 | small | open |
| `Entity.TryScalarEntity` ~4.5 ns/call (World->Ecs->Mirror->Epoch chain); `WorldEcs` per-frame pushes call `GetComponent` (2-4 ns, unconfirmed) | Entity.cs:52, WorldEcs.cs ~453/522/570 | per-frame epoch snapshot | open |
| `Chunk.Tiles` is a 2-D array (flat `[64]` like `_heads`); second head walk in the Gpu item cache | Chunk.cs:27; GameSceneGpuItemCache.cs ~357/401 | two bounds checks + multiply per anchor read | open (item-cache walk goes with T9 signatures) |
| Gpu ecs sprites: `TakeDirty` row overload; worn-light `FindItem` per draw | Codex audit gpu-ecs-sprites | small | open, in the parity fix slice |
| `BoatStepSystem._order` snapshot list copy per frame | BoatStepSystem | small | open |
| Due mobiles from the clock's handles instead of a serial lookup; `WorldState` children via a handle snapshot | World.cs:709, WorldState.cs:338 | small | open |
| `Item.RootContainer` lookup per level; `HouseManager` looks the house up twice | Item.cs, HouseManager | small | open |
| Tab scan copies all items (keypress only); ordered item table's free list as `Stack<int>` | `World.FindNextCore` | trivial | open |
| Attach-time walks (`WouldCycle`, `SubtreeDeeperThan`, `ParentOf`) | WorldWriter, WorldMirror | packets only | open (negligible) |
| FrameStats 8 ms slow-frame log line skews benches near 8 ms | FrameStats.cs:666 | bench accuracy | open: raise or disable under bench |
| Dev only: `Extract.Run` every frame while `-ecscheck` > 0; `BenchEffects.Tick` walks effects every 200 ms | WorldEcs.cs ~972 | dev builds | open |
| `MobileInputs` Female/WarMode/SpeedMode copies of `MobileTraits` | MobileInputs | - | goes with T9 (derive from the record once caches go) |
| `World.GetHeld` falls back to the other table on a miss; `World.Holds` item branch via `Items.Holds`; `FindScalarEntity`/`HoldsEntity` look the serial up twice (use `Mirror.GetHandle(serial) == this`) | World.cs ~1161, Entity.cs:60, WorldEcs.cs:255 | a dictionary lookup each | goes with T9 (world index slice 5) |
| One dictionary lookup too many per corpse per frame (`world.Items.Get`) | ItemAnimationAdvanceSystem.TryRun ~125 | - | goes with T9 tables |
| `DynamicTileIndex.Holds` three type tests per placement (kind byte); `(Maintain \|\| ListDropped)` conditions | Chunk.cs:379, TileOrder.cs:302/384 | trivial | goes with T9 (A3 deleted the off path: verify ListDropped is gone) |

## From the CrossUO scout (2026-11-11)

Ideas from reading CrossUO (AGPL-3.0: techniques only, no code taken). Notes with CrossUO file references: cuo-poc-state surveys/crossuo-scout-2026-11-11.md. Each lands behind a switch with a checker and a paired measurement, like any other item. Order = expected value.

| What | Where (ours) | Estimate | Status |
|---|---|---|---|
| Budgeted LRU eviction for decoded caches (Legacy anim atlas, art, gumps): evict only above a high-water byte budget down to a low-water mark, least recently used first, never anything used in the last ~30-60 s; Legacy atlas evicts whole 4096^2 pages (page age = newest use on the page); counters for resident bytes, evictions and reloads of entries evicted < N s ago (thrash), in FrameStats/bench tables. Not CrossUO's fixed 3 s idle timer (reloads on turn-around). Budget is dynamic (like ModernUO's network buffers): grow by a unit (Legacy: one 64 MiB page) when the thrash counter shows reloads of recently evicted entries and the machine has free memory (GC.GetGCMemoryInfo + available physical memory); shrink when the top unit sees no use for several minutes, with hysteresis; per-client ceiling (machine memory / expected instances) and a floor that covers a normal scene. Unfocused/minimized clients shrink sooner (background clients shed memory: the timer's one advantage). Avoiding reloads/allocations beats memory while it is available (owner, 2026-11-11). Scale: 3-4 Legacy pages = 192-256 MiB of a 530-940 MB working set (bench-000701). Per instance by design: decoded sprites/atlases/GPU textures are private to each process, so no shared LRU; the shared layer is the OS page cache over the pack (see the mapping row). Check Uo.Render page residency policy first, one design for both renderers | `Animations`, Legacy atlas, art/gump caches | bounds the Legacy anim atlas (64 MiB pages, unbounded today); memory per instance for 3-4 clients | open, after anim-count merges |
| User memory cap (later, after the dynamic eviction budget): one option "Memory use: Auto / Balanced / Low / Custom MB per client" mapped onto every large consumer's ceiling (Legacy anim/art/text atlases, Gpu page residency, item pool with a floor that still covers 10k+ item house recalls, GC heap: GCConserveMemory / heap hard limit are startup-only, so restart). Auto = machine memory split across the running clients (count via a small shared named object; see multi-instance). Penalty under a tight cap = reloads, near zero for idle clients (bank, house, busy dungeon on a second screen). Options page shows usage vs cap and the reload counter. Owner idea 2026-11-11 | options, eviction budgets | 5 clients without exhausting RAM | later |
| Unfocused/minimized cost: check how low `ReduceFPSWhenInactive` goes, skip prepare/draw entirely when minimized (keep packets and update), default for multi-instance | GameController.cs ~575-599, Profile.cs:261 | CPU/GPU of background clients (CrossUO: ~4-5 fps inactive, no draw minimized) | open, check current behaviour first |
| World text: lay out positions and overlap only when an owner moved, a line was added or the zoom changed; replay cached positions otherwise; skip owners not drawn last frame | `TextRenderer.ProcessWorldText`, `GameScene.UpdateTextServerEntities` | ProcessWorldText ~4 % (Legacy large scene) | open |
| Picking: skip pick tests while the mouse is not over the world (`pick_skip_off_world`, default off, `CUO_PICK_SKIP_CHECK`); stop-at-first-hit does not apply (picking runs inside the draw walk); bounds-before-mask already done. Follow-ups: Gpu prepare skips the walk of tiles under the mouse when picking is off (GameSceneGpuPrepare ~456-500, GpuItemCache ~522); depth-dominance pick skip; coarse per-tile reject before `TryItemArt`/`ItemOffsets.Read` | `FillGameObjectList`, `ItemView.CheckMouseSelection` | CheckMouseSelection ~2.5 % | switch on `bg/qw-renderlist`, jobs running |
| Tile chain early exits | `AddTileToRenderList` | none: X break and maxZ return already there (qw-renderlist); the existing `return` on item/unmeshed-static Z + height can drop later objects (PriorityZ order is not monotonic in Z + height): correctness issue for the clipping milestone | not needed |
| Per-mobile prepass: compute frame info / offset / screen rect once per frame and reuse for culling, picking and text placement | `Mobile.get_Offset`, `MobileSpriteExtract` | get_Offset ~2.4 % | open |
| Legacy depth-tested world pass: per-quad depth from tile order so batches never flush for draw order | UltimaBatcher2D, `GameScene` sort | could cut sort/flush work; alpha edges and translucent order are the risk | design experiment |
| Legacy texture array for land texmaps and small art (one bound texture for many sprites) | UltimaBatcher2D, art upload | fewer texture switches; driver ~10 % (`DrawIndexedPrimitives`) | design experiment |
| FrameStats: print max frame time next to avg per window | FrameStats | spike visibility (walk hitches) | open, small |
| Pack tables as shared read-only mappings (no heap copies) so instances share the OS page cache | PackAssets | memory across instances | open, check |
| Startup: only boot-critical tables block (tiledata, hues, indexes); the rest loads lazily or in the background | PackAssets startup | startup time | open |
| Periodic sweeps bounded per frame (range removal, cache clears, GC-like passes) | World/managers | walk hitch candidates | open, audit |

## Perf audit 2026-11-11 (Codex, profile bench-000700)

Source: cuo-poc-state audits/perf-audit-1111/out.md. Savings are provisional (exclusive samples of the main thread, busy machine); each slice is verified for correctness and allocations now and timed in the quiet pass.

| What | Where | Estimate | Status |
|---|---|---|---|
| Gpu item-cache validity by render generations instead of rebuilding every item's signature every frame | GameSceneGpuItemCache.cs:319, 565 | Gpu large scene 10-20 % (GpuItemTileValid 28-29 %) | slice pa-gpusig (switch + every-use checker) |
| Screen-shaped chunk/tile traversal (reject whole chunks before lookup, projected tile masks, separate draw/fade/pick masks) | GetViewPort, FillGameObjectList, Map.GetChunkForDraw | Britain z2.5 Legacy 5-15 %, Gpu 8-20 % | with the clipping milestone (high render-order risk) |
| Item preparation reads once per item (graphic/flags/amount/ground, tiledata once) | GameSceneDrawingSorting.cs:1187, Item.ItemData | Legacy large scene 2-4 % | slice pa-itemprep (switch + `CUO_ITEM_PREP_CHECK`) |
| Mesh hue only on input change | ApplyMeshHue, MeshLayer.SetHue | Legacy Britain 2-4 % | slice pa-meshhue (`CUO_MESH_HUE_CHECK`) |
| Text renderer tail maintained instead of scanned twice per frame | TextRenderer.cs:29, 123 | Legacy 1-3 %, Gpu 2-4 % | slice pa-small (`CUO_TEXT_ORDER_CHECK`) |
| Mobile offset read once per operation | MobileView.cs:110, EntityTextContainer.cs:264 | Legacy 0.5-1.5 %, Gpu 1-2 % | slice pa-small |
| Item pick rejection before full resolution; S5 `OnGround` probes | ItemView.cs:34, Item.cs:113 | Legacy 0.5-1.5 % (overlaps item prep) | slice pa-itemprep (`CUO_ITEM_PICK_CHECK`) |
| Packed mesh visibility bitset | MeshLayer.ResetVisibility/BuildVisibleIndices | Legacy Britain 0-1.5 % | slice pa-meshhue, only if attribution supports it |
| Tile-walk setup/continuation simplification | DynamicTileIndex.cs:323, 369 | Legacy large scene 0.3-1 % | open |
| Gpu reverse-copy handoff removed | GpuWorldRenderer.cs:875 | Gpu 0.3-1 % (upper bound) | open, measure the loop first |
| `GetAnimationDimensions` from pack metadata (no decode) | Animation.cs:113 | cold-path decodes, not steady CPU | after anim-count merges |
| Anim page packing by co-use | Uo.Converter AnimConvert.cs:461, 793 | bodies window churn only (128/128, 233 evictions); large-scene walks show none | record page reuse distances first |
| Not to do: remove Legacy prefetch, per-site cast/StelemRef tweaks, generic subpixel LOD | | | rejected by the audit |

## Done (commit ids)

- Update path: W5 active items (`world_update_active_items`; 0.28-0.33 to 0.015-0.02 ms at 25k items), `gpu_mobile_frame_cache` (0.71 to 0.35 ms at 1,611 mobiles), `text_skip_expired` (0.18 to 0.04 ms), HP percentage in setters, `WMapManager` per-frame early-out, camera generation instead of per-frame screen positions, E11 single mirror sync per placement (98654f96f).
- Allocation: B1-B5 batcher/UI (17-28 KB to 0.1-0.25 KB per frame), 250 fps cap removed, chunk loads without allocation (0069120eb), login warm-up (11174c3cf), perf-alloc-sites-2 (5c6b682a7; text pixel buffer, MegaCliloc), perf-alloc-3 (a52b1bc77, 396b960ba, 188193899: Gpu 48 B floor, name-overhead closures), perf-alloc-4 (world map gump 352 to 0), perf-alloc-5 (79e0f20e2; `Pathfinder.CanWalk`), perf-15 (8a16a5b55), perf-14 (d85a12570 console writes, 9a6bb9a98 `EffectManager.PushToBack` tail pointer).
- Rendering: B1 `Pass.Apply` 16 to 3 per frame, B8 UI render cache (794526bbd), W4 ground-item chunk cache (9a35c9b9a), E6 effects (8a2bcb876), E7 mixed tiles (a4090e9ff), step 9 roof/translucent/range-tint as shader state (b0be567aa), Indexed8 single-tap and per-worker Kind 2 decoders (c18e96a6b), per-header page read, translucent list grows on demand.
- Loading: pack-hitch buffers/SIMD picker/worker first-sight decode (df2fbf8c8), teleport decode on workers (28bb72308, 24a7e1504), item/multi art on workers (697e4c668), L1 network drain (48f970596), L7 handle cache measured negligible (593c65c1b), L2-L5 dropped (the pack replaces them).
- ECS reads: tile-head (1150d9b3a), `EcsRead` (02c0ef387), ecs-hygiene (66cf4b8e7), linked-object (eb0f70ed0), perf-6..12 (picker/texmap, paperdoll `WornView` b221d00eb, `MobileInputs` by ref, hue frame inputs, worn cache once per mobile, per-corpse layer tables, one record read per mobile), perf-13-steps (9bd1ac059; door and corpse scans), perf-cpu-1109 (456bb0606; `ItemTable` held flag, `HealthLinesManager` target walk), legacy-sprites-items (880c138ce direct art draw, b607aa19a `GatherFrame`/`ItemOffsets.ReadRow`, 106a6ff8b `SpritePickTable` no zero-fill; merged 618c63d55).
- Measured and rejected: static PGO and `OptimizationPreference=Speed` (AOT gap ~0.03 ms/frame inside native FNA calls; PGO opt-in via `-p:CuoMibcPath=`), `x86-64-v3` ISA (no difference), B9 batch sorting, flat animation pick index (up to ~12 MB per client), UI gump render cache #1889, legacy_item_draw_cache.
