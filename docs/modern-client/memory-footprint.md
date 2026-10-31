# Memory footprint per client

What one client costs in memory, what each large contributor buys, and whether it could start small and grow. Measurement and recommendations only: no defaults were changed on this branch.

**Owner's framing.** Pools, caches, prefill and atlases that lower GC work or raise performance are welcome. This page logs their cost per client so the trade-offs can be reviewed. It recommends a cut only for waste: memory that buys nothing measurable, a bug or leak, or a cost far out of proportion to its benefit. Several clients (3–4+) run at once, so every number here is per client.

**Status (2026-10-31).** Our client's matrix is measured (jobs bench-000014, 000015, 000017). Three follow-up jobs are queued:
- bench-000030: heap dumps of a JIT build (Gpu and Legacy), with live/dead attribution of the large object heap, and an ordinary land spot.
- bench-000031: the upstream baseline. Upstream ClassicUO 9a23aa253 is retargeted to net10.0 in a scratch build.
- bench-000032: two GC arms, `CUO_WARMUP_GC=2` and `DOTNET_GCConserveMemory=5`.

Sections marked *pending* will be filled from those jobs.

## Method

- **Machine:** RTX 3090 Ti (Vulkan, NVIDIA 617.42), 64 GB RAM, Windows 11. The machine is shared with other agents' jobs, so expect a few percent of noise.
- **Builds:** `poc/mem-footprint` fd5450021, which is poc/modern-client 88da64e3e plus `MemLog`.
  - NativeAOT publish (`bin-aot`) and a framework-dependent JIT build (`bin-jit`), both net10.
  - Pack `classic-1.0`. Text and gumps come from the pack here: the runs' logs show `Gumps: pack`.
- **Session:** login (local ModernUO), 1920×1080 window, uncapped fps, `CUO_FRAMESTATS=1`.
  - Plan: Britain 1495 1629 for 3 × 60 s, a 60 s walk, then 60 s at 1600 1950.
  - **1600 1950 is open ocean.** Treat it as the floor of a minimal scene, not as an ordinary quiet spot. An ordinary land spot (Britain moongate, 1336 1997) is in bench-000030/31.
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
| upstream 9a23aa253 (net10, FNA) | JIT | – | off | *pending (bench-000031)* | | | | |

- **Task Manager shows 0.65–0.85 GB per client.** Commit is 1.25–2.0 GB.
- **Gpu world vs Legacy:** about +85 MB pws and about +660 MB commit, on both AOT and JIT. Nearly all of the commit difference is GPU memory that is never resident (see below).
- **AOT vs JIT:** within about 20 MB pws. AOT has about 30 MB less image commit and about 105 MB less mapped memory.
- **The hard limit (`DOTNET_GCHeapHardLimit=0x200000000`) makes no difference.** jit_gpu_pack and jit_gpu_pack_nolimit agree within run-to-run spread, since the heap is far below 8 GB. Users run without it.
- **Warm-up prefill (`CUO_WARMUP=0`):** no pws saving (817 vs 800–815). Without the warm-up's induced collection the LOH ends larger: 425 vs 303–369 MB, with 85 MB fragmented.

## Where the private working set goes

Address-space walk at 170 s (standing in Britain), MB, as commit / pws:

| | aot_gpu_pack | aot_legacy_pack |
|---|---|---|
| GC heap regions (one 128 GB reservation) | 439 / 351 | 373 / 341 |
| other ≥1 GB private reservations (runtime and driver) | 84 / 59 | 103 / 71 |
| write-combine private (`PAGE_READWRITE\|PAGE_WRITECOMBINE`, GPU-mapped) | 1116 / 153 | 600 / 153 |
| other private read-write (native heaps: SDL, drivers, native buffers) | 268 / 229 | 172 / 140 |
| image (exe and dlls) | 540 / 8 | 542 / 10 |
| mapped files (pack, MUL/UOP, `soundLegacyMUL.uop` 161 MB) | 262 / 0 | 262 / 0 |
| **total private** | **1911 / 792** | **1252 / 705** |

- **About 45% of what Task Manager shows is the managed heap** (350 of 790 MB), not GPU memory. That makes the large object heap the biggest single lever (next section).
- **The ~1.1 GB of private commit that isn't resident is GPU-mapped memory.**
  - It sits in write-combine regions, 32 MB each: the driver's allocations behind SDL_GPU and FNA3D textures and buffers.
  - It counts against the system commit limit, not in Task Manager's "Memory" column. VRAM backs it as well.
  - The Gpu world adds 516 MB of it (1116 vs 600), and none of that is resident. That matches the computed Uo.Render allocations (inventory below): 256 + 128 + 64 + 18.5 + 8 + ~50 MiB ≈ 525 MiB.
  - Both renderers have exactly 153 MB of write-combine memory resident. That's FNA3D's share: swapchain, render targets, and the atlases' upload memory.
- **Mapped files are shared** (0 pws): the pack, the facet MULs and the sound UOP. Several clients share these pages through the file cache.
- **Native, unattributed:** the Gpu world adds about 89 MB of resident native memory over Legacy (229 vs 140). That's SDL_GPU and Vulkan CPU-side state plus Uo.Render's native buffers. It isn't broken down further here: that would need a native heap snapshot.

## The large object heap lead (303–425 MB of "gen3")

What the logs show:
- **The LOH is 303 MB about 2 s after start (AOT Gpu).** That point is already past login, because the local login takes under a second. The smoke run measured 413 MB, and the no-warm-up run 425 MB.
  - It then grows in steps when chunks and regions load: 369 MB after the walk.
  - Legacy: 177 MB at login, rising to 298 MB.
  - JIT Gpu: 335 → 424 MB.
- **Only 3–4 gen2 collections happen in a whole session, all before or at login.** Every later collection is a gen0 or gen1, and those never collect the LOH.
  - So "gen3 after the last GC" counts every large array allocated since the last gen2, dead or alive.
  - The poc-log ("Login warm-up collections") already measured about 125 MB of LOH `byte[]` in the first 40 s: region decompression in `MapLoader.LoadRegion` and Uo.Render `Region.Parse`, plus pack page buffers. Most of those buffers are transient.
- **Working hypothesis (to confirm with bench-000030/32):** a large share of the 300–425 MB is dead buffers that no gen2 has reclaimed. Nothing pressures the GC to run one: the machine has 64 GB, and the LOH budget grows with the heap. If the hypothesis holds, this is the largest piece of waste in the footprint. A cheap fix would be a full blocking gen2 with LOH compaction at a moment nobody sees, such as the login hold. `CUO_WARMUP_GC=2` already does that once, and bench-000032 measures it.

**Known live LOH contents** (computed from source; sizes in MiB):

| item | where | size | allocated | what it buys | verdict |
|---|---|---|---|---|---|
| anim tables (`AnimBodyEntry`/`AnimDirEntry`/`AnimFrameEntry`) | `poc/Uo.Pack/PackReaders.cs:462` (anim.bin) | 26 | up front | O(1) frame lookup for every mobile and item anim | keep. Could be memory-mapped from the pack instead (shared between clients); low priority |
| anim palettes CPU copy (`ushort[]`) + `FramePalette` (`uint[]`) | `PackSet.cs:129`, upload at `Renderer.cs:170` | 18.5 + 4.8 | up front, kept for the pack's lifetime | `FramePalette` is read per frame (`GpuWorldRenderer.cs:721`, `MobileSpriteExtract.cs:715`). The 18.5 MiB `Palettes` array is only read for the GPU upload | **waste, 18.5 MiB:** drop it after upload, re-read on renderer re-creation (23 MB read) |
| unicode font table `FontCharacterDataUnicode[20, 0x10000]` | `src/ClassicUO.Assets/FontsLoader.cs:209` | 20 | up front | per-character metrics | grow on demand: one row per font on first use. Only touched pages are resident, so the commit saving is larger than the pws saving |
| CPU instance mirror `SpriteInstance[512k]` + keys `uint[512k]` | `poc/Uo.Render/ChunkStore.cs:82-83` | 16 + 2 | up front, doubles past 512k | baked instances on the CPU for re-upload | grow on demand: about 4.3k used in a normal session. Start at 64k (2 MiB) and keep the doubling |
| page-job compress buffers `byte[1 MiB]` × 12 | `poc/Uo.Render/PageResidency.cs:24` | 12 | up front | page uploads without allocation | keep (pool, no GC) |
| `_dynamic SpriteInstance[MaxDynamic=131072]` | `WorldRenderer.cs:167` | 4 | up front | large scene's ~33k dynamic sprites, zoomed out | keep (sized for the large scene, cheap) |
| `PackPages` decoded 1 MiB chunk LRUs (art 48, tex 8, anim 32) | `src/ClassicUO.Assets/PackAssets.cs:26-28` | ≤ 88 | grows to the cap | decoded sprite pages for CPU-side reads (gumps, UI, hit tests) | already grows on demand. Occupancy: *pending (dump)* |
| region data (decompressed regions, up to `MaxRegions=24`) | `poc/Uo.Render/Region.cs:24`, `ChunkStore.cs:62` | ~1–4 per region | grows with the camera | the Gpu world's map data | already on demand |
| `Map._terrainChunks` | `Game/Map/Map.cs:83` | 3.5 per map | up front | chunk lookup | keep |

Those live items add up to roughly 110–200 MiB. The rest of the 300–425 MB is either dead (the hypothesis above) or not yet attributed. The heap dump (`dumpheap -stat -min 85000` with and without `-live`, plus `dumpheap -min 1000000`) settles it.

**Small waste found on the way: the Gpu world is built twice at login.** The renderer creates the facet-0 world at startup, then disposes it and builds facet 1 when the player arrives on Trammel (log: `GPU world renderer: facet 0` … `facet 1`). The first world's managed arrays become dead LOH: instance mirror, page jobs, `_dynamic` and chunk tables, about 34 MiB. Its GPU allocations are freed and allocated again, a few hundred MiB of churn. Recommendation: create the Gpu world only once the server has sent the map. Saves about 34 MiB of dead LOH until the next gen2, plus the startup time.

## Inventory, and the owner's question: grow on demand?

*Allocated* = size at full allocation. *Visible* = counted in Task Manager's private working set (resident), as opposed to committed but untouched. *Peak, normal session* = the most used in any of the four Gpu runs (Britain, walk, ocean); the large-scene high-water mark is from `testing/large-scale.md`. *Time to fill* = seconds after process start until that peak.

| contributor | allocated | visible | peak, normal session | large scene | time to fill | recommendation |
|---|---|---|---|---|---|---|
| art page array (24 layers, 2048², B5G5R5A1, 6 mips) | 256 MiB GPU | no (write-combine, 0 resident) | **10 / 24 layers** (9 at login) | not logged (≤ 24, no evictions reported) | 9 layers by login (7–12 s), 10 by ~230 s | **grow on demand:** start 12, grow ×2 at 75% (9 layers), ceiling 24. Grow during a hold or teleport; also grow outside one rather than evict a page used in the last few seconds. Cost of a step: a new array plus a GPU copy of the old layers (128 MiB), one frame, with both arrays alive for that frame |
| anim page array (`AnimLayers=128`, Indexed8, 1 MiB each) | 128 MiB GPU | no | **10 / 128** | **82** zoomed out (64 thrashed) | 3 at login, 9–10 by 280–380 s | **grow on demand:** start 32, double at 75%, ceiling 128. Saves 96 MiB for nearly every session. The large scene reaches 82 in seconds, so grow ahead (at 75%) and during holds. Thrashing is what 64 fixed layers cost (large-scale.md), so growth must never stop short of the ceiling |
| instance buffer (`InstanceCapacity` 2M × 32 B) | 64 MiB GPU | no | **~4.3k instances (0.14 MiB, 0.2%)** | ~33k dynamic plus baked (not logged) | at login | **grow on demand:** start 256k (8 MiB), double, ceiling 64 MiB. Growth means re-creating the buffer and re-uploading the baked instances (the CPU mirror has them) |
| staging transfer buffers (4 art × chain, 8 anim) | ~50 MiB GPU | no | not logged | – | – | keep: they bound upload throughput, and a page upload needs a whole chain anyway |
| palettes storage buffer | 18.5 MiB GPU | no | all (any anim may use any palette) | – | at start | keep |
| frame transfer (8 MiB, cycled) | 8 MiB × frames in flight GPU | partly | per frame | – | – | keep |
| Legacy atlases: 3 × 4096² (64 MiB) + 3 × 2048² (16 MiB) RGBA | 240 MiB GPU | no | created on demand (both renderers create all six for UI, gumps and text) | – | at login | keep: already on demand, and every one was used |
| managed heap (GC) | 400–520 MB commit | **yes, 340–370 MB** | – | – | at login | see the LOH lead. **Pending:** live vs dead, and whether one compacting gen2 at login gives it back |
| PackPages LRUs | ≤ 88 MiB managed | yes when filled | *pending (dump)* | – | – | already on demand; keep |
| MapWarmup prefill (300 chunks, 19.2k lands, 16.8k statics) | 32.6 MB managed | yes | used by the first 289 chunks of a session | – | at login | keep: deliberate trade. Natural collections in the first 60 s went 7/4/4 → 4/1/0 (poc-log, "Login warm-up collections"). `CUO_WARMUP=0` didn't lower pws here |
| unicode font table | 20 MiB managed | only touched pages | – | – | – | grow on demand (one row per font) |
| anim palettes CPU copy | 18.5 MiB managed | yes | needed once, at upload | – | – | **waste:** drop after upload |
| CPU instance mirror | 16 + 2 MiB managed | partly | ~4.3k of 512k | – | – | grow on demand: start 64k |

**Short answer.** Yes for the three big GPU arrays: art pages, anim layers and the instance buffer. Together they are 448 MiB of GPU commit per client, and a normal session uses about 15% of it. Starting at 12 art layers, 32 anim layers and 256k instances saves about 320 MiB of commit and VRAM per client: about 1.3 GB across four clients. The large scene still reaches today's ceilings, with growth steps during holds and at 75% occupancy.

**But it doesn't lower Task Manager's "Memory" column.** These arrays were never resident. What it lowers is commit charge and VRAM. Both matter with several clients: on a smaller GPU, VRAM is what runs out first.

**What does lower the "Memory" column is the managed heap.** In order:
1. Reclaim the dead LOH, if the dumps confirm it.
2. Drop the 18.5 MiB palette copy.
3. Build the Gpu world once, not twice, at login.
4. Shrink the font table and the CPU instance mirror.

Pools, prefill, the PackPages caches and the atlases stay: they are already on demand or they buy measured GC calm.

## Upstream comparison (*pending: bench-000031*)

- Upstream ClassicUO 9a23aa253 is the real "original files" client: MUL/UOP data, FNA, no pack.
- It is built in a scratch worktree with **one change: `src/Directory.Build.props` `TargetFramework` net9.0 → net10.0.** The .NET 9 runtime isn't installed; the owner allowed the retarget, and it puts both clients on one runtime. The change is never committed.
- The job first runs a 75 s smoke, which stops the job if upstream doesn't start. Then our AOT client moves the character, and upstream stands at each spot with the same 1920×1080 window:
  - Britain: 240 s, region walk at 200 s, heap dump at 210 s;
  - the land spot: 120 s;
  - the ocean: 120 s.
- Fill in from `python -I <mem>/analyze.py <job dir>`. Upstream rows use the last 60 s as `steady`.

## Reproduce

Scripts live in the mem-footprint scratch folder (see the handoff): `run.py` (launcher and sampler, `--regions`, `--dump`), `common.sh`, `job-*.sh`, `analyze.py` (per-phase medians), and `occ.py` (occupancy peaks from the `GPU world:` lines). Every client run goes through the bench job runner.
