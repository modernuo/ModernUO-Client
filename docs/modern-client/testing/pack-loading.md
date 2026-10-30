# Pack loading: hitch, parity and micro tests

How the pack-loading work (perf-opportunities.md, "ClassicUO pack-loading path" and "Uo.Render / slice") was measured, written 2026-10-24 by the pack-hitch agent. Read [testing-playbook.md](../testing-playbook.md) first for accounts, locks and cleanup.

## What it tests

- **Loading hitches:** frames that stall while art, map regions or animation bodies load for the first time (travel, teleports, new bodies on screen).
- **Waste:** allocations per chunk, region and direction decode; time per operation.
- **Parity:** the faster paths (picker masks and real-art bounds from the trim rect, pooled decodes, header-only page reads, cached land z) give the same results as the code they replaced.

## 1. Unit tests (no client, about 35 s)

`dotnet test tests/ClassicUO.UnitTests -c Release`. The pack-path tests are in `tests/ClassicUO.UnitTests/Assets/PackLoadingTests.cs`:

| Test | Checks |
|---|---|
| `SetTrimmed_Matches_Set_On_Random_Sprites_Simd_And_Scalar` | 400 random sprites (sparse, dense, striped, solid): `PixelPicker.SetTrimmed` (SIMD, and scalar via `PixelPicker.ForceScalar`) answers `Get(x, y)` and `Get(x, y, 1)` exactly like the old `Set` on every texel plus a 1-texel border |
| `VisibleBounds_Matches_Full_Scan_On_Random_Sprites` | `PixelPicker.VisibleBounds` vs the old full-image loop from Art.cs, including empty and non-tight trims |
| `Picker_And_Bounds_Match_Old_Path_On_Pack_Art_And_Anim_Samples` | The same on real data: about 600 statics and 600 animation frames from the pack |
| `DecodeColor_Into_Dirty_Buffer_Matches_New_Array` | Decoding into a reused (dirty) buffer gives the same image as a fresh array |
| `PackPages_Parallel_Reads_With_Tiny_Cache_Match_Source` | 2,000 random rect reads from 8 threads on Kind 0/1/2 pages with 4 KiB chunks and a 3-chunk cache (constant eviction and buffer reuse) match the source bytes |
| `ReadHeader_Matches_OpenChain_*` | Header-only page reads equal the full `OpenChain` header, synthetic Kind 0/1/2 and real pack pages |
| `DecompressInto_*` | Region decode into a pooled buffer equals the old MemoryStream path; a small buffer grows, a big one is reused |
| `GetLandZ_Matches_TryGetBlock_Across_Region_Evictions` | The cached land z lookup over 40 regions twice (more than the 24-region LRU, so buffers are recycled) |
| `LruCache_*` | Palette LRU order and eviction |

Tests that need the real pack return early when `C:\Repositories\uo-packs\classic-1.0` is missing.

## 2. Micro benchmarks (no client, about 1 minute)

A throwaway console project in the session scratchpad (`packhitch\bench`), referencing the worktree's ClassicUO.Assets, ClassicUO.Renderer and Uo.Render by path (`-p:Wt=<worktree>`). Build it once against the old commit and once with `-p:DefineConstants=NEW` against the new one, so both measure the same operations:

- chunk decode (ms and bytes per 1 MiB chunk), art and anim frame decode plus picker plus bounds, a body direction's first sight (the old synchronous cost and the new main-thread cost), region loads through the cached loader (60 regions, so evictions happen), 13 land-z lookups per cell over 64×64 cells, and page header reads;
- `bench.exe picker`: picker masks on preloaded images, old `Set` vs `SetTrimmed` SIMD vs scalar, 5 repeats. Keep a SIMD path only if it wins here (owner rule: SIMD only with a measured gain, always with a scalar fallback and a parity test).

Run each twice and use the second repetition: the first includes JIT and cold OS file cache.

## 3. In-client travel and first-sight test (about 3 minutes per run)

**Harness:** `packhitch\travel_run.py` (scratchpad). It:
- takes the shared `kbatman4.lock` (waits while another agent holds it; a lock older than 15 minutes is stale);
- writes the password encrypted into a throwaway settings.json; it reads the password from `CUO_TEST_PASSWORD` or a `pw.txt` next to the script, never from the command line;
- runs the client JIT Release, Gpu world renderer, 1920×1080 (`CUO_BENCH_SIZE`), VSync off, `CUO_FRAMESTATS=1`, `CUO_BENCH_EXIT=1`, `CUO_BENCH_NOSHOT=1`, `CUO_BENCH_WINDOWS=4`;
- closes the client, deletes the temp folder and any `Data/` it created, releases the lock, copies the client log to `<tag>-client.log` and prints one line per FrameStats window.

`seq.py tag tag ...` runs several back to back (tags starting with `base` use `bin-base`, others `bin-new`; `sync*` sets `CUO_ANIM_SYNC=1`). `summarize.py tag ...` aggregates the windows per plan entry. Alternate base and new runs (base, new, base, new) so drift from other agents' load hits both.

**Plan** (`CUO_BENCH_PLAN`): `vsync:off;world:gpu;travel:travel;ltravel:ltravel;glide:glide;bodies:bodies`.

| Entry | What happens | Needs |
|---|---|---|
| `travel` | Real `[go` teleports every 2 s through Britain-area towns: (1823,2821) (2899,676) (2498,392) (542,985) (596,2138) (4442,1172) (1495,1629). The first visit to each spot is cold; the list repeats after 7 | Staff access (kbatman4 has it since 2026-10-24) |
| `ltravel` | The same route as client-only teleports (`DevBench.LocalTeleport` does what the 0x20 player update does). Map, statics and art load as on a real teleport; no server mobiles come along | Nothing; works on a player account |
| `glide` | Client-only travel east from (1300,1640) at 25 tiles/s: a new chunk column every 8 tiles, a new region every 512 | Nothing |
| `bodies` | First sight of many bodies: the player's body is swapped (locally) to the next body with a walk animation every 50 ms, about 390 bodies in 20 s; equipment-type bodies are skipped. Restored at the end | Nothing |

Each client is fresh, so every first visit is a true first sight; within a run, a second visit to a town is warm.

**How hitches are counted** (FrameStats, every 5 s window):
- `frame max`: the worst frame; `p99`: the 99th percentile frame time;
- `h>8` and `h>16`: frames over 8 ms and 16 ms;
- the same three **without Present** (`cpu_max`, `cpu_hitch8`, `cpu_hitch16`: frame time minus Present). Present waits come from the GPU and the compositor, mostly other agents' uncapped clients on the same GPU, and swing 10–60 ms between identical runs;
- **Update+Draw** (`work_max`, `work_hitch8`, `work_hitch16`): only the client's own work. Loading hitches land here (world prepare, update). **Judge loading on these columns.** Frame-minus-Present still contains stalls outside every phase: frames of 250–750 ms where no phase took more than 1 ms, seen in both builds (the OS not scheduling the window while other clients run);
- `worst` / `worst_work`: the three biggest phases of the worst frame and of the worst Update+Draw frame (for example `world_prepare=52.8` = loading inside the world walk; `present=45` = GPU wait, not ours).

`summarize.py` prints one line per plan entry: worst-window p99, frame max, hitch sums for all three definitions, allocation per frame, GC counts and the worst Update+Draw frame's phases.

## 4. Teleport hitches: chunk streaming and region prefetch (added 2026-10-25, teleport agent)

The harness is the same, in the scratchpad's `teleport` folder. It adds:
- `seq2.py`, which logs to `runs.txt` how many other `cuo.exe` processes were running before each run;
- a 30 s wait after a GPU fault (exit 0xDEAD), and a stop after two faults;
- `counters.py tag ...`, which prints each window's map-loading counters.

**Build every test binary, base and new, with the Tier 0 login mitigations** (A and C from `poc/gpu-pagefault` 2ad0e20ec, with the GpuWatchTemp block removed). Apply them as an uncommitted patch (`teleport\tier0.patch`) and revert after building. Without them, about 3% of logins hit the art-atlas defrag page fault, and 3 of 8 runs did on 2026-10-25 while other clients were on the GPU.

**Switches:**
- `CUO_CHUNK_SYNC=1` restores the old path: every chunk loads when first asked for, and nothing is prefetched.
- `CUO_CHUNK_BUDGET_MS=<ms>` sets the per-frame chunk budget, which covers load plus mesh build (default 2).

**FrameStats counters** (per frame; `counters.py` shows avg/max per window):

| Counter | Meaning |
|---|---|
| `chunk_loads`, `chunk_load_us` | Chunks loaded this frame, and the time spent loading them. Counts both `Map.UpdateLoading` and synchronous game-logic loads |
| `chunk_mesh_us` | Mesh builds done by the loader inside the budget |
| `chunk_queue` | Chunks still queued at the end of the frame |
| `region_wait_us` | Time the main thread waited for a region prefetch that was still decoding (should be 0) |
| `region_sync` | Regions decoded on the main thread (not prefetched; should be 0 once in game) |
| `region_prefetch` | Region prefetches started |
| `art_sync`, `art_sync_us` | First-sight art and texmap decodes on the main thread (a lookup that had to decode), and their time. Chunk meshes built by the loader should add none |
| `art_async`, `art_upload_us` | Worker art decodes uploaded to the atlases this frame, and the upload time (inside the chunk budget) |
| `art_pending` | Worker art decodes requested and not uploaded yet |
| `art_obj` | Ground items and multi components placed on the map whose art was not in the atlas yet, so a worker decode was requested (`FirstSightArt`) |

**First-sight item and multi art (added 2026-10-30, item art agent).** Ground items and multi components (houses, boats) request their art (the drawn graphic plus every animation frame) when they are placed on the map. The chunk scans also request the frames of animated statics and wait for the frame shown now. The item pick test sizes its reject from the pack, so only an item under the mouse needs its art at once; that one still decodes on the spot. Expect `art_sync` 0 in `travel`, `ltravel`, `glide`, Britain and the bank in both renderers. The paperdoll's first equipment icon is UI art and still decodes on the main thread. To find what still decodes on the main thread, set `CUO_ART_SYNC_LOG=1`: each main-thread art decode logs an `[artsync]` line with the sprite and its six callers (JIT builds; NativeAOT may print no method names).

**First-sight chunk art (added 2026-10-26, teleport art agent).** A queued chunk loads only once its block's land, texmap and static art is in the atlases; the missing art decodes on the thread pool (`FirstSightDecoder`). `CUO_ART_SYNC=1` restores the old path (art decodes inside `ChunkMesh.Build`). A block's first art scan counts against the chunk budget (`ChunkLoadResult.Scanned`); later checks resume at its first missing sprite and are rescanned in full after a season change or `Art.Reload`. A sprite the worker fails to decode counts as ready (a real lookup handles it as before). Check a season change with DevBench `season:winter` followed by `ltravel`: 0 settle timeouts. `chunk_mesh_us` should then stay under about 1 ms per frame; the remaining `chunk_load_us` spikes of 3–7 ms come with a GC in the same window (FrameStats `gc0`/`gc1`, `gc_pause_ms`).

**`ltravel` and the z lookup.** `DevBench.LocalTeleport` now takes z only from a region that is already decoded. Otherwise it prefetches the region, keeps the player's z, and `FixTeleportZ` corrects z once the region arrives. A real teleport gets z from the server, so the bench no longer waits for a decode that a real teleport would not wait for.

**Walk check after a teleport: plan entry `bwalk`.** It does `[go 1495 1528`, then walks to (1495,1542) and back every leg. The route crosses:
- a region edge (y 1536);
- two chunk edges;
- a z 40 → 30 land slope.

Each leg logs `[bench] bwalk leg N ended at x,y,z (land z L), server denies D`. **Pass:** the legs alternate between the two ends, z matches the land z, and D stays 0 (the server's 0x21 walk denials, counted by `WalkerManager.ServerDenies`). Run it with `CUO_BENCH_SHOTS` and `shot:` to look at the screen after the teleport.

**Unit tests:** `tests/ClassicUO.UnitTests/Game/ChunkLoadingTests.cs`:
- the queue order (nearest first), the budget, the at-least-one rule, ring chunks, stale and sticky requests, and no allocation per drain;
- prefetched regions give the same blocks as synchronous ones, and `IsBlockReady` never blocks;
- land z stays correct while prefetching across more than the 24 cached regions.

## 5. Chunk loads without allocation; GC pauses (added 2026-10-26, chunk-alloc agent)

Evicted chunks' `Land` and `Static` objects are reused by later chunk loads (`MapObjectRecycler`; the lifetime rules are in its header comment and in the poc-log entry "Chunk loads without allocation"). Same harness, scratchpad `chunkalloc\` (`run.py`, `summ.py`, `tab.py`).

**Switches:**
- `CUO_RECYCLE=0` turns reuse off (chunk loads allocate as before; the rest of the build stays).
- `CUO_RECYCLE_QUARANTINE_MS=<ms>`: how long a retired object stays destroyed before it can be reused (default 1000).
- `CUO_RECYCLE_CHECK=1`: never reuses, but marks retired objects and counts every later use of one (Legacy draw and picking of land and statics, the Gpu capture, `SelectedObject.Object` and `LastLeftDownObject` at the start of Update, the Legacy mesh highlight, the inspector), plus a sweep of every loaded chunk's tile lists every 64 frames (a retired object in a list, a wrong `TileChunk`, broken links, a map object off its tile). `=reuse` runs the same checks with reuse on. Logs `[recycle-check] ... touched after retire: ... bad N` every 5 s. **Pass:** every count 0. Don't measure with it on.

**FrameStats counters and lines:**

| Counter | Meaning |
|---|---|
| `chunk_alloc` | Bytes the main thread allocated inside chunk loads (was about 25 KB per load; 0 once the recycler has objects) |
| `chunk_gc`, `chunk_gc_us` | Collections that started inside a chunk load, and their pause |
| `chunk_reused`, `chunk_retired` | Land and statics reused by loads, and retired by evictions |
| `chunk_mesh_alloc` | Bytes allocated by the loader's mesh builds (Legacy `MeshLayer` arrays growing in pooled chunks) |
| `chunk_new` | Chunks allocated because the chunk pool was empty (the first teleports after login) |

Counters in the `FRAMESTATS {` json now carry a fifth value, the window sum (`[avg, p50, p95, max, sum]`). Every garbage collection seen at the end of a recorded frame logs one `[gc]` line: generation, pause, promoted MB and each generation's size before and after. **Read GC pauses from these lines**, not from frame phases: the pause lands in whatever phase was running.

**What to expect:** the first `travel` window after login has 2 gen1 collections (5–10 ms; the chunk pool, recycler and mesh arrays fill up and the server's objects arrive). After that, steady teleporting allocates about 0.2–1 KB per frame and a collection comes every 2–4 minutes (3–6 ms). Base collects every 10–15 s of teleporting.

## 6. Gumps from the pack (added 2026-10-30, pack-gumps agent)

Gumps come from `gumps/gumps.bin` when the pack has it (pack-format.md "Gumps"). Scratchpad `pgumps\` (`run.py`, `summ.py`).

**Packs.** `C:\Repositories\uo-packs\classic-1.0` has no gump section until the main session regenerates it. To test without touching it, hard-link a copy and add the gumps (the manifest must be a real copy, since the converter rewrites it):
```
cp -al /c/Repositories/uo-packs/classic-1.0 pack-gumps && rm pack-gumps/manifest.json && cp /c/Repositories/uo-packs/classic-1.0/manifest.json pack-gumps/
Uo.Converter.exe --only gumps --out pack-gumps            (about 6 s, verify included)
Uo.Converter.exe --only gumps --raw-gumps --out pack-raw  (the run-length layout, uncompressed)
Uo.Converter.exe --only gumps --gump-def gump-hued.def --out pack-hued   (hued gump.def aliases)
```

**Switches:**
- `CUO_PACK_GUMPS=0`: read gumpart MUL/UOP even when the pack has gumps (same binary, for paired runs).
- `CUO_GUMP_SYNC=1`: no worker decodes (`Gump.Request` and the batch prefetch are off); every gump decodes on the main thread when first asked for.

**Unit tests** (`tests/ClassicUO.UnitTests/Assets/GumpPackTests.cs`; they return early without data):
- `CUO_TEST_GUMP_PACK=<pack with gumps>`: every gump id against the client's MUL/UOP `GumpsLoader` (presence, size table, pixels into a dirty pooled buffer, the UOP flag); every hue kind (valid, the last, out of range, 0x8000, 0xFFFF) on 40 gumps; picker masks; decodes from 8 threads.
- `CUO_TEST_GUMP_DEF=<gump.def>` with `CUO_TEST_GUMP_DEF_PACK=<pack converted with --gump-def it>`: hued aliases end to end (the MUL side reads the same gump.def through `UOFilesOverrideMap`).
- Codec and store tests need no data.

**In-client first open: plan mode `gumpopen`** (`g:gumpopen` with `CUO_BENCH_WINDOWS=4`; account with `[go`, it starts at Britain). Opens paperdoll, spellbook (gumps 0x08AC/0x08BA/0x08BB), status, backpack, world map, combat book and journal, one every 2.5 s, in a fresh client. `CUO_BENCH_GUMPOPEN="a;b;pics:2C00,067E"` sets the list (`pics:` = a plain gump of those pictures, prefetched like a server gump). Each open logs `[bench-gumpopen] <name>: worst frame … main-thread gump decodes N (ms), prefetched N (ms), open call ms, gumps open: …`. FrameStats counters `gump_sync`, `gump_sync_us`, `gump_async`, `gump_upload_us`, `gump_pending`.

## Noise and false positives

- Other agents' clients (AOT, GPU, ECS work) run on the same GPU and CPU; FPS and Present swing a lot between runs. Compare without-Present hitch counts and max across alternating runs, not single windows.
- The first window after a plan entry starts used to include the PNG screenshot DevBench takes (about 1.4 s once). `CUO_BENCH_NOSHOT=1` removes it; keep it set for hitch runs.
- `travel` frames also contain the server's burst of packets after a teleport (`packets` phase); that is not pack loading.
- The OS file cache is warm after the first run of the session; both base and new runs see the same warm cache.

## Cleanup

- The harness closes the client (WM_CLOSE, then terminate after 15 s), deletes its temp settings/profile folder and any `Data/` folder it created in the bin, and removes the lock file.
- Client bins (`bin-base`, `bin-new`) and logs stay in the scratchpad; nothing goes into the repo.
- No NPCs or items are spawned; the body swap and client-only teleports are local and end with the client.
