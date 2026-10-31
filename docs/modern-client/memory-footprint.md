# Memory footprint per client

What one client costs in memory, what each large contributor buys, and whether it could start small and grow. Measurement and recommendations only: no defaults were changed on this branch.

**Owner's framing.** Pools, caches, prefill and atlases that lower GC work or raise performance are welcome. This page logs their cost per client so the trade-offs can be reviewed. It recommends a cut only for waste: memory that buys nothing measurable, a bug or leak, or a cost far out of proportion to its benefit. Several clients (3–4+) run at once, so every number here is per client.

**Status (2026-10-31).** Complete. Our client's matrix: jobs bench-000014, 000015, 000017. Follow-ups:
- bench-000030: heap dumps of a JIT build (Gpu and Legacy) with live/dead attribution of the large object heap, and an ordinary land spot.
- bench-000031: the upstream baseline. Upstream ClassicUO 9a23aa253 is retargeted to net10.0 in a scratch build.
- bench-000032: two GC arms, `CUO_WARMUP_GC=2` and `DOTNET_GCConserveMemory=5`.

The waste this page found (the anim palette CPU copy, the second Gpu world at login) and the grow-on-demand GPU arrays are implemented on `poc/mem-reduce` (poc-log "Memory per client: palettes, facet switch, grow on demand", measured by bench-000035). This page keeps the numbers from before that branch; the measured effect is summarised under "Short answer" and at the end of "Where the memory goes". Round 2 (`poc/mem-reduce-2`, bench-000044, two interleaved runs per arm): the art array starts at 24 layers and the palette upload stages through 4 MiB. The login commit transient drops by 128 MiB, and the 18.5 MiB palette staging region is gone. Every steady window is within noise. The 25 MiB "unattributed" from bench-000035 is most likely the art staging containers, which hold 6–8 cycled 10.7 MiB buffers per run (poc-log "Measured: bench-000044"). Round 3 (`poc/mem-reduce-3`, bench-000048, two interleaved runs per arm): the staging slots stop cycling. Art staging holds exactly 4 buffers (42.8 MiB) in every run instead of 7–8, which saves 26–37 MiB private WS and 29–39 MiB commit per client from login through the large scene (beyond noise), with 0 slot waits and no measurable login delay (poc-log "Measured: bench-000048").

## Method

- **Machine:** RTX 3090 Ti (Vulkan, NVIDIA 617.42), 64 GB RAM, Windows 11. The machine is shared with other agents' jobs, so expect a few percent of noise.
- **Builds:** `poc/mem-footprint` fd5450021, which is poc/modern-client 88da64e3e plus `MemLog`.
  - NativeAOT publish (`bin-aot`) and a framework-dependent JIT build (`bin-jit`), both net10.
  - Pack `classic-1.0`. Text and gumps come from the pack here: the runs' logs show `Gumps: pack`.
- **Session:** login (local ModernUO), 1920×1080 window, uncapped fps, `CUO_FRAMESTATS=1`.
  - Plan: Britain 1495 1629 for 3 × 60 s, a 60 s walk, then 60 s at 1600 1950.
  - **1600 1950 is open ocean.** Treat it as the floor of a minimal scene, not as an ordinary quiet spot.
  - **The ordinary land spot is the Britain moongate, 1336 1997.** The bench-000030 screenshots (`land_gpu.png`, `land_legacy.png`) show grass, forest and the moongate stones, the same in both renderers. It's in bench-000030 and 000031.
- **External sampler** (`run.py`, 1 Hz):
  - private working set: Task Manager's "Memory" column, shown below as *pws*;
  - private bytes (commit), working set, and peaks;
  - an address-space walk at 170 s (`VirtualQueryEx` + `QueryWorkingSet`): private memory by protection, by reservation size, and the top allocations.
- **In process:** `CUO_MEM_LOG=1` (dev builds only; `src/ClassicUO.Client/MemLog.cs`) logs once a second: GC heap, committed, fragmented, the per-generation size after the last GC, and the collection counts.
- **Occupancy:** from the existing `GPU world:` trace line, every 5 s. It gives art pages resident/24, anim pages resident/128, baked and dynamic instances, and evictions.
- **Invalid arm:** the "MUL" runs (`aot_gpu_mul`, `aot_legacy_mul`, `aot_legacy_mul_nolimit`) are invalid and are left out.
  - Our client needs a pack: art, maps and anims come only from the pack (`PackAssets.cs:72`). Without `-pack` these runs exited at startup.
  - The closest valid "original files" arm is pack + `CUO_PACK_TEXT=0 CUO_PACK_GUMPS=0`.
  - The real "original files" comparison is upstream itself.

## Headline numbers

Medians over each phase, MB. "Commit" is private bytes. "GC heap" is the in-process GC heap size.

| run | build | world | `GCHeapHardLimit` | pws Britain / walk / ocean | peak pws | commit Britain | peak commit | GC heap Britain → ocean |
|---|---|---|---|---|---|---|---|---|
| aot_gpu_pack | AOT | Gpu | on | 800 / 808 / 815 | 816 | 1975 | 2190 | 363 → 436 |
| aot_gpu_pack_nowarm (`CUO_WARMUP=0`) | AOT | Gpu | on | 817 / 817 / 818 | 822 | 1947 | 2075 | 467 → 471 |
| aot_legacy_pack | AOT | Legacy | on | 715 / 719 / 720 | 720 | 1314 | 1399 | 351 → 356 |
| jit_gpu_pack | JIT | Gpu | on | 820 / 833 / 839 | 842 | 2024 | 2118 | 399 → 490 |
| jit_gpu_pack_nolimit | JIT | Gpu | off | 841 / 853 / 854 | 855 | 1985 | 2125 | 400 → 493 |
| jit_legacy_pack | JIT | Legacy | on | 652 / 668 / 666 | 672 | 1253 | 1289 | 197 → 323 |
| upstream 9a23aa253 (net10, FNA) | JIT | FNA (like Legacy) | off | 576 / land 575 / 572 | 586 (982 after the heap dump: an artifact) | 1060 | 1064 | 219 at 210 s (from the dump) |

- **Task Manager shows 0.65–0.85 GB per client.** Commit is 1.25–2.0 GB.
- **Gpu world vs Legacy:** about +85 MB pws and about +660 MB commit, on both AOT and JIT. Nearly all of the commit difference is GPU memory that is never resident (see below).
- **AOT vs JIT:** within about 20 MB pws. AOT has about 30 MB less image commit and about 105 MB less mapped memory.
- **The hard limit (`DOTNET_GCHeapHardLimit=0x200000000`) makes no difference.** jit_gpu_pack and jit_gpu_pack_nolimit agree within run-to-run spread, since the heap is far below 8 GB. Users run without it.
- **Warm-up prefill (`CUO_WARMUP=0`):** no pws saving (817 vs 800–815). Without the warm-up's induced collection the LOH ends larger: 425 vs 303–369 MB, with 85 MB fragmented.
- **Upstream uses 575 MB per client, flat at every spot.** Our client uses about +80–95 MB more on JIT Legacy (652–668), +140 MB on AOT Legacy, and +225–265 MB on Gpu. Commit: upstream 1060 MB vs our 1250–1310 (Legacy) and 1950–2020 (Gpu). The next sections say where the difference goes.
- **The land spot** (our AOT, 90 s runs in bench-000031, each including login and the teleport): 882 MB pws at the moongate, 836 in Britain, 792 on the ocean. Upstream, standing at the same spots afterwards: 575 / 576 / 572. These short runs aren't comparable to the 420 s matrix above; they only show that an ordinary forest spot costs our client about as much as Britain, or a little more.

## Where the private working set goes

Address-space walk at 170 s (standing in Britain), MB, as commit / pws:

| | aot_gpu_pack | aot_legacy_pack | upstream (Britain, 200 s) | ours Gpu − upstream (pws) |
|---|---|---|---|---|
| GC heap regions (one 128 GB reservation) | 439 / 351 | 373 / 341 | 227 / 225 | +126 |
| other ≥1 GB private reservations (runtime and driver) | 84 / 59 | 103 / 71 | 69 / 48 | +11 |
| write-combine private (`PAGE_READWRITE\|PAGE_WRITECOMBINE`, GPU-mapped) | 1116 / 153 | 600 / 153 | 532 / 153 | 0 |
| other private read-write (native heaps: SDL, drivers, native buffers) | 268 / 229 | 172 / 140 | 164 / 139 | +90 |
| image (exe and dlls) | 540 / 8 | 542 / 10 | 569 / 9 | 0 |
| mapped files (pack, MUL/UOP, `soundLegacyMUL.uop` 161 MB) | 262 / 0 | 262 / 0 | 2379 / 2 | – |
| **total private** | **1911 / 792** | **1252 / 705** | **991 / 565** | **+227** |

- **About 45% of what Task Manager shows is the managed heap** (350 of 790 MB), not GPU memory. That makes the large object heap the biggest single lever (next section).
- **The ~1.1 GB of private commit that isn't resident is GPU-mapped memory.**
  - It sits in write-combine regions, 32 MB each: the driver's allocations behind SDL_GPU and FNA3D textures and buffers.
  - It counts against the system commit limit, not in Task Manager's "Memory" column. VRAM backs it as well.
  - The Gpu world adds 516 MB of it (1116 vs 600), and none of that is resident. That matches the computed Uo.Render allocations (inventory below): 256 + 128 + 64 + 18.5 + 8 + ~50 MiB ≈ 525 MiB.
  - Both renderers have exactly 153 MB of write-combine memory resident. That's FNA3D's share: swapchain, render targets, and the atlases' upload memory.
- **Mapped files are shared** (0 pws): the pack, the facet MULs and the sound UOP. Several clients share these pages through the file cache.
- **Native, unattributed:** the Gpu world adds about 89 MB of resident native memory over Legacy (229 vs 140). That's SDL_GPU and Vulkan CPU-side state plus Uo.Render's native buffers. It isn't broken down further here: that would need a native heap snapshot.
- **Against upstream, the difference is the managed heap plus the Gpu world's native memory.** Legacy's native heaps and GPU-mapped memory match upstream's exactly (140 vs 139 MB, 153 vs 153 MB). Our AOT Legacy client's +140 MB is the GC heap (+116) and runtime reservations (+23). The Gpu world adds the 90 MB of native memory on top.
- **Upstream maps 2.4 GB of MUL/UOP files** (anim*.mul, AnimationFrame*.uop, gumps, art, sound). That costs it nothing resident (2 MB) and nothing in commit: mapped files are shared and backed by the files themselves.

## The large object heap (303–425 MB of "gen3"): mostly live

What the logs show:
- **The LOH is 303 MB about 2 s after start (AOT Gpu).** That point is already past login, because the local login takes under a second. The smoke run measured 413 MB, and the no-warm-up run 425 MB.
  - It then grows in steps when chunks and regions load: 369 MB after the walk.
  - Legacy: 177 MB at login, rising to 298 MB.
  - JIT Gpu: 335 → 424 MB.
- **Only 3–4 gen2 collections happen in a whole session, all before or at login.** Every later collection is a gen0 or gen1, and those never collect the LOH.
  - So "gen3 after the last GC" counts every large array allocated since the last gen2, dead or alive.
  - The poc-log ("Login warm-up collections") already measured about 125 MB of LOH `byte[]` in the first 40 s: region decompression in `MapLoader.LoadRegion` and Uo.Render `Region.Parse`, plus pack page buffers.

**Verdict: the hypothesis that most of it is dead is wrong.** About 300 MB of the LOH is live at login. Dead LOH builds up after login, up to about a third of the LOH, but a collection doesn't give it back to Task Manager.
- **At login it is live.** `CUO_WARMUP_GC=2` runs a full blocking compacting gen2 in the last frame of the login hold (bench-000032). 309 MB of LOH survives it, against 303 MB after the default two gen1s. Nothing was there to reclaim.
- **Later, dead LOH builds up.** Heap dump of the JIT Gpu client at 50 s, standing in Britain (bench-000030; `dumpheap -min 85000`, with and without `-live`):

| | JIT Gpu | JIT Legacy | upstream (210 s) |
|---|---|---|---|
| GC heap allocated | 553 MB | 376 MB | 219 MB |
| objects ≥ 85,000 B (LOH) | 478 MB | 313 MB | 189 MB |
| of those, live | 311 MB | 195 MB | 170 MB |
| dead | **167 MB (35%)** | **117 MB (37%)** | 19 MB (10%) |

- **What the 167 MB of dead LOH (JIT Gpu) is.** Dead objects have no roots, so owners are inferred from type, size and source:
  - 85 MB: 81 `byte[1 MiB]` buffers. Likely sources: the page-job buffers and Brotli decoder lanes of the first, discarded Gpu world (`PageResidency.cs:24,43`, `ChunkedBrotli.cs:249-250`), plus transient page decodes. Chunks evicted from `PackPages` are reused (`RentBuffer`), so they aren't the source.
  - 34 MB: 512 KiB and 256 KiB `byte[]` (31 + 29 arrays, 24 MB) and region decode buffers (2–3.3 MB each, 10 MB). These are transient.
  - 21 MB: the first world's `SpriteInstance[]` (16 MiB mirror + 4 MiB `_dynamic`). This confirms the double build at login (below).
  - 16.6 MB: two `Color[1920×1080]`. These are the bench's screenshot buffers (`GameController.cs:1228`); they don't exist in normal play.
  - The rest (about 10 MB) is smaller `uint[]`, `ushort[]` and `int[]` arrays.
- **A collection frees the dead LOH, but Task Manager doesn't drop.** The GC keeps the freed space committed and resident for the next large allocation. In the `GCConserveMemory=5` arm, a background gen2 ran at 275 s and left 64 MB fragmented (free), and pws stayed at 803 MB. Both GC arms (next section) land within noise of the default.
- **So the way to cut it is to stop allocating it.** Avoiding the second Gpu world (implemented on `poc/mem-reduce`) removes the 21 MB of instance arrays and, very likely, the first world's page-job and decoder buffers: roughly 45–55 MB of the 85 + 21 MB. The remaining transient decode buffers, about 35–60 MB, aren't attributed to a call site yet (open: an allocation trace).

### GC arms (bench-000032, AOT Gpu, hard limit off)

Medians in MB. Baseline: `aot_gpu_pack`, bench-000014 (hard limit on, which makes no difference; see above).

| arm | pws Britain / walk / ocean | commit Britain | GC heap Britain → ocean | LOH after login | login collection | collections in play (total pause, max) |
|---|---|---|---|---|---|---|
| default (two gen1s in the login hold) | 800 / 808 / 815 | 1975 | 363 → 436 | 303 | 10.8 ms | 2 (12.5 ms, max 8.5 ms) |
| `CUO_WARMUP_GC=2` (full compacting gen2 in the login hold) | 787 / 803 / 816 | 1947 | 367 → 408 | 309 | **32.6 ms** (frame 33.7 ms, in the hold) | 2 (8.1 ms, max 4.6 ms) |
| `DOTNET_GCConserveMemory=5` | 794 / 801 / 803 | 1965 | 422 → 427 | 330 | 14.3 ms | 7, one background gen2 (15.1 ms, max 4.0 ms) |

- **`CUO_WARMUP_GC=2`:** no memory benefit. It costs 22 ms more pause, in a frame nobody sees (the login hold). The GC heap ends 28 MB lower, but pws doesn't change (−13 to +1 MB, within the run-to-run spread of about ±15 MB). **Keep the default.**
- **`GCConserveMemory=5`:** pws −6 to −12 MB, within noise. It runs more collections in play (7 vs 2), but none caused a visible hitch: the longest frame with a GC was 21 ms, and no pause passed 4 ms. **No measurable saving; don't adopt.**
- **Neither arm is a memory-for-performance trade worth logging.** No GC setting change is recommended.

**Known live LOH contents** (computed from source; sizes in MiB):

| item | where | size | allocated | what it buys | verdict |
|---|---|---|---|---|---|
| anim tables (`AnimBodyEntry`/`AnimDirEntry`/`AnimFrameEntry`) | `poc/Uo.Pack/PackReaders.cs:462` (anim.bin) | 26 | up front | O(1) frame lookup for every mobile and item anim | keep. Could be memory-mapped from the pack instead (shared between clients); low priority |
| anim palettes CPU copy (`ushort[]`) + `FramePalette` (`uint[]`) | `PackSet.cs:129`, upload at `Renderer.cs:170` | 18.5 + 4.8 | up front, kept for the pack's lifetime | `FramePalette` is read per frame (`GpuWorldRenderer.cs:721`, `MobileSpriteExtract.cs:715`). The 18.5 MiB `Palettes` array is only read for the GPU upload | **waste, 18.5 + 4.8 MiB. Fixed on `poc/mem-reduce`** (71b1a6f46): streamed from the pack into the GPU buffer, frame table shared. The dump agrees: live `ushort[]` totals 21.5 MB in Gpu, 1 MB in Legacy |
| unicode font table `FontCharacterDataUnicode[20, 0x10000]` | `src/ClassicUO.Assets/FontsLoader.cs:209` | 20 | up front | per-character metrics | grow on demand: one row per font on first use. Only touched pages are resident, so the commit saving is larger than the pws saving |
| CPU instance mirror `SpriteInstance[512k]` + keys `uint[512k]` | `poc/Uo.Render/ChunkStore.cs:82-83` | 16 + 2 | up front, doubles past 512k | baked instances on the CPU for re-upload | grow on demand: about 4.3k used in a normal session. `poc/mem-reduce` starts it at 256k (9 MiB) and keeps the doubling |
| page-job compress buffers `byte[1 MiB]` × 12 | `poc/Uo.Render/PageResidency.cs:24` | 12 (estimate; see the measured pool row above) | up front | page uploads without allocation | keep (pool, no GC) |
| `_dynamic SpriteInstance[MaxDynamic=131072]` | `WorldRenderer.cs:167` | 4 | up front | large scene's ~33k dynamic sprites, zoomed out | keep (sized for the large scene, cheap) |
| `PackPages` decoded 1 MiB chunk LRUs (art 48, tex 8, anim 32) | `src/ClassicUO.Assets/PackAssets.cs:26-28` | ≤ 88 | grows to the cap | decoded sprite pages for CPU-side reads (gumps, UI, hit tests) | already grows on demand, and reuses evicted buffers. **Measured: 59 chunks (59 MiB) Gpu, 55 Legacy** at 50 s in Britain (dumps) |
| page-job and Brotli decoder working buffers (1 MiB compressed + 1 MiB scratch per job and per decoder lane) | `poc/Uo.Render/PageResidency.cs:24,43`, `poc/Uo.Pack/ChunkedBrotli.cs:249-250` | **~61 (measured)** | at the first page loads | page decodes and uploads without allocation | keep (a pool). Replaces the row below's earlier estimate: the dump shows 58 live arrays of about 1 MiB besides the PackPages chunks, rooted in `PageJob` and `ChunkedBrotliDecoder` |
| region data (decompressed regions, up to `MaxRegions=24`) | `poc/Uo.Render/Region.cs:24`, `ChunkStore.cs:62` | ~1–4 per region | grows with the camera | the Gpu world's map data | already on demand |
| `Map._terrainChunks` | `Game/Map/Map.cs:83` | 3.5 per map | up front | chunk lookup | keep |

The items above add up to about 230–260 MiB of the 311 MB live (JIT Gpu). The rest is many smaller tables, for example `UOFileIndex[]` 3.3, `Chunk[]` 3.7, the cliloc dictionary 3.8, `SpriteInfo[]` 5.4, `float[]` 7.2, `int[]` 11 and `uint[]` 13 MB.

**Upstream's live LOH is 170 MB, and different.** Its largest live arrays are `UOFileIndex[]` (63 MB in 20 arrays: the MUL/UOP indexes), `IndexMap[]` (51 MB, the map block index), the same 20 MiB unicode font table, and the `UOFileIndex` dictionaries (10 MB). It has no decoded-page caches, no anim tables and no decode pools: it reads sprites from the mapped files and keeps them in GPU atlases. Our extra ~140 MB of live LOH is mostly what the pack design buys: the PackPages caches (59), the decode pools (~61), the anim tables (26), plus the palette copy that `poc/mem-reduce` removes (23).

**Small waste, confirmed: the Gpu world is built twice at login.** The renderer creates the facet-0 world at startup, then disposes it and builds facet 1 when the player arrives on Trammel (log: `GPU world renderer: facet 0` … `facet 1`). The dump finds the first world's `SpriteInstance[]` arrays dead (21 MB), and very likely its page-job and decoder buffers too. Its GPU allocations are freed and allocated again: a few hundred MiB of churn. **Fixed on `poc/mem-reduce`** (5e0948ad4: the facet changes in place).

## Inventory, and the owner's question: grow on demand?

*Allocated* = size at full allocation. *Visible* = counted in Task Manager's private working set (resident), as opposed to committed but untouched. *Peak, normal session* = the most used in any of the four Gpu runs (Britain, walk, ocean); the large-scene high-water mark is from `testing/large-scale.md`. *Time to fill* = seconds after process start until that peak.

| contributor | allocated | visible | peak, normal session | large scene | time to fill | recommendation |
|---|---|---|---|---|---|---|
| art page array (24 layers, 2048², B5G5R5A1, 6 mips) | 256 MiB GPU | no (write-combine, 0 resident) | **10 / 24 layers** (9 at login) | not logged (≤ 24, no evictions reported) | 9 layers by login (7–12 s), 10 by ~230 s | **grow on demand:** start 12, grow ×2 at 75% (9 layers), ceiling 24. Grow during a hold or teleport; also grow outside one rather than evict a page used in the last few seconds. Cost of a step: a new array plus a GPU copy of the old layers (128 MiB), one frame, with both arrays alive for that frame |
| anim page array (`AnimLayers=128`, Indexed8, 1 MiB each) | 128 MiB GPU | no | **10 / 128** | **82** zoomed out (64 thrashed) | 3 at login, 9–10 by 280–380 s | **grow on demand:** start 32, double at 75%, ceiling 128. Saves 96 MiB for nearly every session. The large scene reaches 82 in seconds, so grow ahead (at 75%) and during holds. Thrashing is what 64 fixed layers cost (large-scale.md), so growth must never stop short of the ceiling |
| instance buffer (`InstanceCapacity` 2M × 32 B) | 64 MiB GPU | no | **~4.3k instances (0.14 MiB, 0.2%)** | ~33k dynamic plus baked (not logged) | at login | **grow on demand:** start 256k (8 MiB), double, ceiling 64 MiB. Growth means re-creating the buffer and re-uploading the baked instances (the CPU mirror has them) |
| staging transfer buffers (4 art × 10.67 MiB chain, 8 anim × 1 MiB) | ~50 MiB by slot count; measured 64–85 MiB for art alone | **yes** (host-cached, private read-write, fully resident; bench-000044) | art: **6–8 buffers**, because each slot is mapped with `cycle=true` and the login page burst adds buffers that are never freed | – | by 5 s | keep the slots and stop cycling them: **built on `poc/mem-reduce-3`** (owner-approved). A slot is reused only once the fence of its last copy has signalled, and mapped with `cycle=false`, so art holds exactly 4 buffers and anim 8. Measured (bench-000048): exactly **4 buffers, 42.8 MiB** in both new runs at 60 and 120 s (base 7 and 8: 74.9 and 85.6 MiB); 26–37 MiB less private WS and 29–39 MiB less commit per client in the login, Britain and large-scene windows, 0 slot waits, login settle 177 / 172 ms vs 167 / 167 ms (within noise). Anim's 8 × 1 MiB is below the walk's floor (poc-log "Memory per client, round 3") |
| palettes storage buffer | 18.5 MiB GPU | no | all (any anim may use any palette) | – | at start | keep |
| frame transfer (8 MiB, cycled) | 8 MiB × frames in flight GPU | partly | per frame | – | – | keep |
| Legacy atlases: 3 × 4096² (64 MiB) + 3 × 2048² (16 MiB) RGBA | 240 MiB GPU | no | created on demand (both renderers create all six for UI, gumps and text) | – | at login | keep: already on demand, and every one was used |
| managed heap (GC) | 400–520 MB commit | **yes, 340–370 MB** | – | – | at login | about 300 MB of LOH is live at login; dead LOH builds up later (35% of the JIT Gpu LOH at 50 s). Neither a compacting gen2 at login nor `GCConserveMemory` lowers pws (see the LOH section). Cut it by not allocating: `poc/mem-reduce` |
| PackPages LRUs | ≤ 88 MiB managed | yes when filled | 59 MiB Gpu, 55 MiB Legacy (Britain, 50 s) | – | – | already on demand; keep |
| page-job and decoder working buffers | ~61 MB managed | yes | all of it from the first page loads | – | at login | keep (a pool: page decodes allocate nothing). Logged cost: ~61 MB per client |
| MapWarmup prefill (300 chunks, 19.2k lands, 16.8k statics) | 32.6 MB managed | yes | used by the first 289 chunks of a session | – | at login | keep: deliberate trade. Natural collections in the first 60 s went 7/4/4 → 4/1/0 (poc-log, "Login warm-up collections"). `CUO_WARMUP=0` didn't lower pws here |
| unicode font table | 20 MiB managed | only touched pages | – | – | – | grow on demand (one row per font) |
| anim palettes CPU copy | 18.5 MiB managed | yes | needed once, at upload | – | – | **waste:** removed on `poc/mem-reduce` |
| CPU instance mirror | 16 + 2 MiB managed | partly | ~4.3k of 512k | – | – | grow on demand: `poc/mem-reduce` starts it at 256k |

**Short answer.** Yes for the three big GPU arrays (implemented on `poc/mem-reduce`): art pages, anim layers and the instance buffer. Together they are 448 MiB of GPU commit per client, and a normal session uses about 15% of it. Starting at 12 art layers, 32 anim layers and 256k instances was computed to save about 280 MiB of commit and VRAM per client. Measured (bench-000035): 145 MiB of private commit in Britain, because the art array grows to its old 24 layers at login in every session; 54–66 MiB after a crowded scene, where the anim array reaches its ceiling too (only the instance buffer stays small). Each growth step took 0.7–1.2 ms of main-thread time, none in a frame over 8 ms. Round 2 (bench-000044): art now starts at its 24-layer ceiling, since it always grew at login. That removes the growth step and 128 MiB of login commit transient (1981 → 1853 MiB, which base held for about 3 s; within-arm spread 0.2 % / 0.6 %). Anim and instances still grow on demand. Staging the palette upload through 4 MiB removes the 18.5 MiB read-write region that stayed resident (15.7–17.7 MiB) in both base runs. Commit and private WS in login, Britain, the large scene, after the facet round trip, idle and at peak are all within noise (differences +1 to +13 MiB against within-arm spreads of 0.5–3.4 %). The art staging buffers, which vary by 10.7 MiB steps from run to run, cancel out the palette saving in the medians. Round 3 (bench-000048) fixes those: a staging slot is reused only after its last copy's fence has signalled and is mapped without cycling, so art staging is exactly 4 buffers (42.8 MiB) in every run instead of 6–8. Private WS drops by 26–37 MiB and commit by 29–39 MiB per client from login through the large scene (beyond noise; within-arm spreads 0.1–1.8 %). After the round trip and idle the drop is 42–47 MiB but under the 2× rule, because base's own spread there is one staging buffer. There were 0 slot waits, and the login settle time (177 / 172 ms vs 167 / 167 ms) is within noise.

**But it doesn't lower Task Manager's "Memory" column.** These arrays were never resident. What it lowers is commit charge and VRAM. Both matter with several clients: on a smaller GPU, VRAM is what runs out first.

**What does lower the "Memory" column is the managed heap.** In order:
1. Drop the 18.5 MiB palette copy (and the second 4.8 MiB frame table): done on `poc/mem-reduce`.
2. Build the Gpu world once, not twice, at login: done on `poc/mem-reduce`. It avoids the 21 MB of dead instance arrays the dump found, and very likely the first world's page-job and decoder buffers.
3. Shrink the CPU instance mirror (done on `poc/mem-reduce`: 256k start) and the 20 MiB unicode font table (not done: one row per font on first use).
4. **New, measurement first:** find the call sites of the remaining transient large buffers. After the fixes above, an estimated 35–60 MB of dead LOH per client is still unattributed: 512 KiB and 256 KiB `byte[]`, region decode buffers, and the transient 1 MiB page decodes. Take an allocation trace on the `poc/mem-reduce` build (`dotnet-trace` with the `gc-verbose` profile, AllocationTick events filtered to the LOH, first 60 s after login). Where a site allocates per region or per page, give it a bounded pool. The pool's size is the memory cost to log; the benefit is LOH growth that stops, measured as pws after the walk.

**Not recommended: GC settings.** One compacting gen2 at login (`CUO_WARMUP_GC=2`) and `DOTNET_GCConserveMemory=5` were both measured. Neither moved pws beyond noise. The compacting gen2 costs 32.6 ms in the login hold (22 ms more than the default), and conserve mode costs 5 extra collections in play. See the GC arms table.

Pools, prefill, the PackPages caches, the decode pools and the atlases stay: they are already on demand, or they buy measured GC calm.

## Upstream comparison (bench-000031)

- Upstream ClassicUO 9a23aa253 is the real "original files" client: MUL/UOP data, FNA, no pack.
- It is built in a scratch worktree with **one change: `src/Directory.Build.props` `TargetFramework` net9.0 → net10.0.** The .NET 9 runtime isn't installed; the owner allowed the retarget, and it puts both clients on one runtime. The change was never committed, and the scratch worktree has been removed.
- Method: a 75 s smoke (it passed), then, at each spot, our AOT client moves the character there (90 s) and upstream logs in and stands with the same 1920×1080 window. Britain 240 s (region walk at 200 s, heap dump at 210 s), the land spot 120 s, the ocean 120 s. JIT, default GC, no hard limit.

| MB | upstream (JIT) | ours JIT Legacy | ours JIT Gpu | ours AOT Legacy | ours AOT Gpu |
|---|---|---|---|---|---|
| pws Britain / land / ocean | 576 / 575 / 572 | 652 / – / 666 | 820 / – / 839 | 715 / – / 720 | 800 / – / 815 |
| difference to upstream (pws) | – | +76 to +94 | +244 to +267 | +139 to +148 | +224 to +243 |
| commit | 1060 | 1253 | 2024 | 1314 | 1975 |
| GC heap | 219 (dump) | 197 → 323 | 399 → 490 | 351 → 356 | 363 → 436 |
| live LOH (dump) | 170 | 195 | 311 | – | – |
| GPU-mapped resident (write-combine) | 153 | – | – | 153 | 153 |
| native heaps resident | 139 | – | – | 140 | 229 |

- **Upstream is flat:** 565–586 MB pws from the smoke through every spot, commit 1023–1064 MB. Its peak of 982 MB comes after the heap dump, which reads every committed page and makes it resident; ignore it. The same artifact affects our dump runs, so their post-dump samples are left out everywhere.
- **Where our extra memory goes** (AOT, region walks): the managed heap (+116 to +126 MB), runtime reservations (+11 to +23 MB) and, in the Gpu world only, native memory (+90 MB). GPU-mapped resident memory and Legacy's native heaps match upstream to the MB.
- **What it buys:** the pack (decoded-page caches, decode pools, anim tables; ~140 MB more live LOH than upstream's indexes) and the Gpu world. Upstream instead maps 2.4 GB of MUL/UOP files, which costs it no resident memory and no commit.
- **After `poc/mem-reduce`** (bench-000035, JIT, one run per arm): private commit 208 MiB lower per client in Britain (63 from the palettes and the single world build, 145 from growth), 298 MiB lower after a facet round trip, and 499 MiB lower at peak. The GC heap is about 50 MB smaller at login and 91 MB smaller after the round trip, but Task Manager's private WS moves by only 6–9 MiB at login (inside the ±20 MiB run-to-run noise) and 34 MiB after the round trip: other native read-write memory is 25 MiB higher on the branch, not yet attributed (poc-log "Measured: bench-000035"). bench-000044's region walks point to the art staging containers. They hold 6–8 cycled 10.67 MiB buffers, created during the login page burst and never freed, and the count varies by run within the same binary. Two extra buffers (21.3 MiB) fit most of the 25 MiB, and nothing new appears at the facet switch.

## Reproduce

Scripts live in the mem-footprint scratch folder (see the handoff): `run.py` (launcher and sampler, `--regions`, `--dump`), `common.sh`, `job-*.sh`, `analyze.py` (per-phase medians), and `occ.py` (occupancy peaks from the `GPU world:` lines). Every client run goes through the bench job runner.
