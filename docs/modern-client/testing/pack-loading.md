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

## Noise and false positives

- Other agents' clients (AOT, GPU, ECS work) run on the same GPU and CPU; FPS and Present swing a lot between runs. Compare without-Present hitch counts and max across alternating runs, not single windows.
- The first window after a plan entry starts used to include the PNG screenshot DevBench takes (about 1.4 s once). `CUO_BENCH_NOSHOT=1` removes it; keep it set for hitch runs.
- `travel` frames also contain the server's burst of packets after a teleport (`packets` phase); that is not pack loading.
- The OS file cache is warm after the first run of the session; both base and new runs see the same warm cache.

## Cleanup

- The harness closes the client (WM_CLOSE, then terminate after 15 s), deletes its temp settings/profile folder and any `Data/` folder it created in the bin, and removes the lock file.
- Client bins (`bin-base`, `bin-new`) and logs stay in the scratchpad; nothing goes into the repo.
- No NPCs or items are spawned; the body swap and client-only teleports are local and end with the client.
