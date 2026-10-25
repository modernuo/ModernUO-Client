# Owner review: what happened while you were away

Running notes for your return, newest at the top within each section. Started 2026-10-25 ~01:20. The full detail is in [poc-log.md](poc-log.md), and the open bugs are in [known-issues.md](known-issues.md).

## Needs you

- **FPS test**, when you're back: see "Your FPS test" below. The build is ready.
- **Decision: an FNA3D fork like the SDL one?** Two FNA3D bugs are open. (1) A cycled index buffer isn't rebound; the batcher works around it. (2) After `FreeTextureHandle`, `nextRenderPass*` references dangle (from the Codex audit; lower likelihood). Our FNA3D.dll in `external/x64` is already a local patch with no fork behind it. With your OK I'd create github.com/modernuo/FNA3D (or use a branch on an FNA fork), wire it like SDL (submodule, build script, committed DLL), and fix both properly. Until then the workaround holds.
- **Optional:** the fork-built SDL3.dll has no libusb HIDAPI support (the old upstream DLL had it). That only matters for unusual controllers. Say if you want it built in.

## Decisions you made tonight (recorded in README "Decisions so far" and in memory)

- **Legacy is a developer-only comparison mode** (2026-10-24). There's no automatic fallback to it. It's kept mainly to catch Gpu bugs, and it will be deleted once parity uses saved reference shots. *Implemented in a6110f967.*
- **SDL fork:** agents may push fixes to github.com/modernuo/SDL without asking first. The client may build SDL3.dll from the fork. Upstream (libsdl-org) contributions stay forbidden. *In progress (page-fault agent).*
- **Codex audits** go through rf-agent with the `gpt-6-astra` model; the recipe is in memory.

## Merged into `poc/modern-client` tonight

| Commit | What | Verified |
|---|---|---|
| 9a35c9b9a | **W4: ground items cached per map chunk in the Gpu world** (`gpu_prepare_item_cache`, default on; off = the old path; Legacy unchanged). Settled ground items are drawn from a per-chunk cached sprite list, rebuilt only when an item on a tile changes, so they're no longer walked and re-extracted every frame. **World prepare in the large scene: 1.67–2.01 → 1.32–1.45 ms at zoom 1 and 2.19–2.53 → 1.80–1.97 ms zoomed out (about 20–30%).** Britain and the bank are slightly faster. A **Codex (Astra) audit** found two minor stale-state cases, both fixed before the merge (d1cba86ea): a container highlighted during a gump drag could keep its highlight tint in the cache, and "open container near object" could place the window from a stale screen position. | A new check mode (`CUO_GPU_ITEM_CHECK`) compares the cache with a fresh extract: 0 differences in 110 M cached-item comparisons, plus 3.3 M in a NativeAOT smoke run in the scene (by me). Scripted item changes (hue, amount, move, delete, add, pick up/drop): 0 stale frames, and cache on/off shots are pixel-identical. I re-ran that script after the audit fixes: 0 differences. Parity 0, picking identical, 0 settle timeouts, 21 GPU-world recreations, facet changes and chunk reloads clean; 263 tests. Not covered: houses/multis (none on the shard), the hidden-item hue, 4K |
| a122f2cc1 | **Large-scale test scene, and the scale bugs it found.** A repeatable scene of 1,984 frozen NPCs and 19,476 ground items on Felucca at 5156 2150, an empty field east of the continent (`tools/large-scale/make_scene.py create|wipe`; see `testing/large-scale.md`). **Two real bugs at scale, both fixed:** above 32,768 sprites the Gpu world silently dropped the rest (the north tip of the scene was missing when zoomed out), and 82 animation pages competed for 64 slots, so sprites flickered while pages re-uploaded. Capacity is now 131,072 sprites and 128 animation layers (+64 MB), and a warning is logged if sprites are ever dropped. Also faster: overhead-text positions (a list of entities that have text instead of scanning all 25,000), screen positions recomputed only when something moved, and a quicker item-picking rejection. **FrameStats was measuring itself**, costing about 0.33 ms per frame in busy scenes, so your FPS test ran slightly pessimistic too; fixed. **Scene, Gpu: 252 → 341 fps at zoom 1, 200 → 291 zoomed out (about 0.7 ms of real gain, the rest was FrameStats' overhead). Legacy: 206 → 225.** Britain and the bank are about 3–5% faster. | 0 parity mismatches (9.5 M mobile and 75 M item comparisons, standing and walking); built-in checks: 0 wrong among 852 M kept positions and 487 M rejected picks; pickscan 0 different; teleports 0 settle timeouts; Gpu, Legacy and UI shots looked at; memory flat; 263 tests. NativeAOT not re-measured |
| cacc27d44 | **W3 finished, and the settle follow-up.** Corpses and multi-tile items are now drawn from the ECS too; the old capture runs on the default path only for effects, captured statics and shadow-only draws. Fixed: when an NPC died, the ECS dropped its hair for the first frames of the death animation. The frame hold now also waits for chunks the client still has queued, and fades finish during the hold, so buildings are opaque on the first frame after a teleport (before, 10–36 chunks could still be queued at release, with translucent buildings). Cost: about +1–2 ms per teleport hold, and login black lasts about 100 ms longer. Also the **W3+teleport integration check** on the merged tip: passed. | 0 parity mismatches (about 900k corpse and 260k multi comparisons; across about 60 teleports; a deliberately broken build is caught); picking identical (pickscan 48/48); 13-minute session with memory flat at 1.9 GB, 0 objects left parked, 0 settle timeouts; world prepare slightly better (crowd 92 → 87–91 µs, corpses 42 → 34 µs); 263 tests. NativeAOT not re-measured |
| 08b952f3a | **GPU page fault fixed (Tier 0).** SDL3.dll is now built from **github.com/modernuo/SDL** branch `modernuo/3.4.19` (b2a3160aa: a 5-line patch that gives Vulkan images of 64 MiB or more their own allocation, so SDL never defragments the atlases). The fork is the `external/SDL` submodule, built with `tools/sdl/build-sdl.ps1`; the DLL is committed with `SDL3.version`. **0 faults in 220 logins vs 4 in 90 at baseline** (p ≈ 0.005% by luck). Diagnostics live on the fork's `diag/defrag` branch and the local `poc/gpu-pagefault-diag`; the fork's `main` is untouched, and nothing went upstream. Mitigations B and C dropped (the patch supersedes them); A came in with the settle merge. | 262 tests; `--external-test` on Vulkan and D3D12; Legacy and flip shots clean; the fps-test logs in |
| 28bb72308 | **Teleport hitch** (verified on its own branch first): region decode on a worker, chunk loads budgeted per frame. `[go` teleport worst frame 35–36 ms → 12–18 ms. Objects on unloaded chunks are parked until the chunk loads (7,626 parked and 7,612 placed in an 18-minute session; the other 14 moved or were destroyed first). | picking identical (pickscan 0 different); teleports with the frame hold show 0 unsettled frames and 0 timeouts; facet change Trammel↔Felucca; region-edge walks with 0 denials; memory flat over 18 minutes; 262 tests |
| e9f2505c2 | **W3: mobiles and art items in the Gpu world are drawn from the ECS** (`gpu_ecs_sprites`, default on; the old capture is kept behind it). World prepare: Britain 112→90 µs, bank 147→105, zoomed out 389→331, 120-NPC crowd 168→109. Corpses and multis still use the capture. | 0 mismatches across about 7.5 M comparisons (parity runs need `CUO_ANIM_SYNC=1`); Gpu, Legacy, UI and picking shots looked at; 255 tests |
| cededb3ee | **Land seams fixed when zoomed out** (shader only: tile coverage comes from the full-size texel, as Legacy decides it, while colour stays trilinear, so no new shimmer). Also the **scissor exception fix**: every scissor rect is clipped to the target, and UI drawing is skipped on an empty back buffer. Left over: a few short ticks where shore pieces meet water at zoom 2 (they predate the fix). The scissor crash wasn't reproducible, so 11 unit tests are its only evidence. | Shots looked at (grid gone, matches Legacy); UI and Gpu zoom-1 parity; Legacy shots; `--external-test` on Vulkan and D3D12; 255 tests. GPU cost within noise (no idle GPU available) |
| 6f2007199 | Playbook: agents must stay current with `poc/modern-client` (merge before measuring and before final checks, rebuild binaries, report the base commit) | docs |
| a5e43e94c | **Window title shows `[Legacy renderer]`** whenever Legacy draws the world, including runtime switches. You couldn't tell which renderer was drawing. | build, 244 tests |
| b62406b8e | **Fix: Legacy tile corruption.** Root cause: B1 stopped re-applying the effect per mesh run, and FNA3D doesn't rebind a cycled index buffer, so every mesh layer after the first drew with stale indices. B1's skipping is kept; Gpu-mode effect applies are unchanged (2.0–2.7 per frame). | 12 of 12 Legacy shots and 39 of 39 flip shots clean; UI parity holds; 244 tests |
| d583f65f5 | Playbook: **a Legacy world shot is now a required check** for batcher, loader, prepare and GameScene changes. B1 passed every Gpu-only check for a day. | docs |
| a6110f967 | **World settle.** The client holds the last frame after login, teleports and GPU-world recreation: 0 half-built frames shown, against 6–19 before. Login shows black, then a 250 ms fade-in. No Legacy fallback. A failed GPU setup exits 0xDEAF with a message. A saved `"world_renderer": "legacy"` is ignored. | 244 tests; per-frame shots |
| d04ddc780 | AOT vs JIT: static PGO and `OptimizationPreference=Speed` don't help, so neither is on by default (PGO is opt-in). The remaining gap is about 0.03 ms per frame inside native FNA calls; GPU-tool injection is suspected. | 233 tests |
| df2fbf8c8 | Pack-loading hitches: pooled buffers, SIMD picking masks, first-sight animation decode on a worker, region buffer reuse, palette LRU. First-sight body frames went from 25 ms to under 8 ms max. | 233 tests |

## Under verification, not merged yet

- Nothing waiting. (W3 and teleport are being checked together on the merged tip; see In progress.)


## In progress

- **W5: the ECS takes over the per-frame world update** (an agent started about 08:45). Mobile animation and extract (about 0.55 ms in the scene) is now the largest piece of world prepare, and `World.Update` walks 25,000 items, most of them in packs.

## Things you saw tonight, explained

- **"Corruption" in your KamDos and Kbatthree videos** was the Legacy renderer (the B1 bug), drawn during the test plans' `world:legacy` periods and the old automatic Legacy fallback. About 150 Gpu-pass frames checked were all clean. Both causes are now fixed (b62406b8e, a6110f967), and the title now shows when Legacy is active.
- **The first KamDos video** was a stress run (`CUO_GPU_REINIT`, which rebuilds the GPU world every 1.5 s) on top of the same Legacy bug.
- **Nsight popups** are the known login page fault. Concurrent clients seem to raise its rate: the teleport agent saw 3 faults in 8 runs, 1 of them on the plain base build. Agents now apply the login mitigations to their test builds, and stop after 2 faults.
- **Black only during teleports or login** (on Kbatfour earlier) was the pop-in that a6110f967 fixes.

## Follow-ups noted (not started)

- **The large scene is left on the shard** (frozen: about 4% of one server core while a client stands there, ModernUO memory 563 → 711 MB). Say if you want it wiped (`make_scene.py wipe`) or added to your FPS test as an extra spot. It's on Felucca, so it needs a facet change, and it isn't added by default, so your results stay comparable with 10-24.
- **W2 was half done on purpose.** The screen-position half is merged. The ECS-query half would save about 0.15 ms in the 21,000-object scene and nothing at normal scale, and it risks changing draw order and the height cut-off, so it was skipped in favour of W4. Recorded in the plan.
- **Parity runs now need the item check too:** with the cache on, cached items skip the ECS item comparison, so parity runs must add `CUO_GPU_ITEM_CHECK` (in the testing docs). `itemcache off` restores the old coverage.

- **Stray test items:** after the W3 corpse runs, one item each near kbatman3 and kbatman4 at Green Acres couldn't be identified, so it was left in place. Harmless.
- **Multi placement preview** (house placement, drawn at half alpha) is now drawn from the ECS but has no automated test scenario.

- **Your kbatman character:** W3's parity scripts kill and resurrect the character. The agent removed the leftover death robes and a test cloak, checked the backpack, and moved it back to Britain 1495,1629. Worth a glance when you log in.
- **Unexplained single-frame spikes** (16–50 ms `update_other`, 95 ms at a facet switch) were seen during verification but not compared with base. Not tied to the new loading.
- **FNA3D:** a proper fix for "a cycled index buffer isn't rebound" (worked around in the batcher). There's also a dangling `nextRenderPass*` reference after `FreeTextureHandle` that the Codex audit flagged. The page-fault agent will ask before forking FNA3D.
- **Teleport:** the remaining 10–14 ms frame comes from the server packet burst and one first-sight chunk mesh build. Next step: decode art on a worker before the mesh build.

## Your FPS test

The build at `C:\Repositories\cuo-fps-test\bin` is **9a35c9b9a** (published 08:38). It has everything above: the fork SDL, W3 (now including corpses and multis), W4's item cache, the large-scale fixes (including FrameStats' lower overhead), the teleport work, world settle and the B1 fix.

1. Make sure no agent is running clients (ask me to pause them) and the display is on.
2. From a normal PowerShell window:
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\Repositories\classicuo\tools\fps-test\run-fps-test.ps1 -Username kbatman
   ```
3. Type the password at the prompt (the script now writes it encrypted into its temp settings). Don't touch anything for about 5 minutes.
4. Send me the `C:\Repositories\cuo-fps-test\results-<timestamp>.md` path. I'll compare it with the 10-24 run.

Its Legacy column is now the developer comparison mode (the window title shows `[Legacy renderer]`).
