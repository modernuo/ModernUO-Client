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

- **Large-scale test scene, then W2** (an agent started about 04:50; the scene is placed away from Britain and kbatman, with frozen NPCs): first a repeatable busy scene (about 2,000 mobiles and 20,000 ground items) on the local shard, measured in both renderers; then W2 (ground items and mobiles from ECS queries instead of tile lists), measured against that scene.

## Things you saw tonight, explained

- **"Corruption" in your KamDos and Kbatthree videos** was the Legacy renderer (the B1 bug), drawn during the test plans' `world:legacy` periods and the old automatic Legacy fallback. About 150 Gpu-pass frames checked were all clean. Both causes are now fixed (b62406b8e, a6110f967), and the title now shows when Legacy is active.
- **The first KamDos video** was a stress run (`CUO_GPU_REINIT`, which rebuilds the GPU world every 1.5 s) on top of the same Legacy bug.
- **Nsight popups** are the known login page fault. Concurrent clients seem to raise its rate: the teleport agent saw 3 faults in 8 runs, 1 of them on the plain base build. Agents now apply the login mitigations to their test builds, and stop after 2 faults.
- **Black only during teleports or login** (on Kbatfour earlier) was the pop-in that a6110f967 fixes.

## Follow-ups noted (not started)

- **Stray test items:** after the W3 corpse runs, one item each near kbatman3 and kbatman4 at Green Acres couldn't be identified, so it was left in place. Harmless.
- **Multi placement preview** (house placement, drawn at half alpha) is now drawn from the ECS but has no automated test scenario.

- **Your kbatman character:** W3's parity scripts kill and resurrect the character. The agent removed the leftover death robes and a test cloak, checked the backpack, and moved it back to Britain 1495,1629. Worth a glance when you log in.
- **Unexplained single-frame spikes** (16–50 ms `update_other`, 95 ms at a facet switch) were seen during verification but not compared with base. Not tied to the new loading.
- **FNA3D:** a proper fix for "a cycled index buffer isn't rebound" (worked around in the batcher). There's also a dangling `nextRenderPass*` reference after `FreeTextureHandle` that the Codex audit flagged. The page-fault agent will ask before forking FNA3D.
- **Teleport:** the remaining 10–14 ms frame comes from the server packet burst and one first-sight chunk mesh build. Next step: decode art on a worker before the mesh build.

## Your FPS test

The build at `C:\Repositories\cuo-fps-test\bin` is **cacc27d44** (published 04:43). It has everything above: the fork SDL, W3 (now including corpses and multis), the teleport work, world settle and the B1 fix.

1. Make sure no agent is running clients (ask me to pause them) and the display is on.
2. From a normal PowerShell window:
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\Repositories\classicuo\tools\fps-test\run-fps-test.ps1 -Username kbatman
   ```
3. Type the password at the prompt (the script now writes it encrypted into its temp settings). Don't touch anything for about 5 minutes.
4. Send me the `C:\Repositories\cuo-fps-test\results-<timestamp>.md` path. I'll compare it with the 10-24 run.

Its Legacy column is now the developer comparison mode (the window title shows `[Legacy renderer]`).
