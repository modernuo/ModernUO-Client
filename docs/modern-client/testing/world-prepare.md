# Testing: world prepare (W0/W1)

How to measure ClassicUO's world prepare (`GameScene.FillGameObjectList` and the Gpu path's collect and capture) and how to check a change to it for pixel and picking parity. Written 2026-10-24 alongside W0/W1 of world-prepare-ecs-plan.md. Follow the ground rules in [../testing-playbook.md](../testing-playbook.md): encrypted password in a throwaway settings.json, the `kbatman.lock` / `owner-hold` protocol, no topmost window, short runs, cleanup.

## Measuring

- **Switches:**
  - `CUO_FRAMESTATS=1` logs every 5 s a table plus a `FRAMESTATS {json}` line.
  - The world-prepare sub-phases are `prep_*`:
    - setup, walk, gpu masks, hidden collect, foliage and finish, text;
    - capture of multis, statics, items, mobiles and other;
    - rest = world prepare minus the others.
  - Counters:
    - tiles and objects walked, baked land and statics, unmeshed and dynamic objects;
    - pick tests, override calls, hidden-collect tiles, lights, foliage, captures;
    - for the W1 path: `gpu_full_tiles`, `gpu_walk_tiles`, `gpu_edge_tiles`, `gpu_pick_tiles`, `gpu_mask_rebuilds` and `gpu_skip_active`.
- **Detail mode** (`CUO_FRAMESTATS_PREP=1`, or the plan entries `prepdetail:on|off`):
  - It splits the walk into exclusive `walk_*` buckets (baked land, baked statics, unmeshed, items/mobiles/effects, screen position, picking, overrides, lights, foliage) and times `bare_walk`, a visit of the same tile lists with no work. The bare walk runs outside world prepare.
  - It costs about 20 ns per bucket switch, so it inflates world prepare by 70–100% at 4K. Never quote detail-mode totals.
  - **To correct the buckets:**
    1. Run the same spot with detail off and on.
    2. c = (prepare on − prepare off) / Σ `bucket_entries` (in the JSON).
    3. Subtract entries × c from each bucket.
- **Plans** (DevBench). One login covers a whole comparison; spot names become the `FRAMESTATS` tags:
  ```
  CUO_FRAMESTATS=1 CUO_BENCH_EXIT=1 CUO_BENCH_SIZE=1920x1080 CUO_BENCH_WINDOWS=2
  CUO_BENCH_PLAN="vsync:off;world:gpu;gpuskip:off;britain_off:1495 1629:1;gpuskip:on;britain_on:1495 1629:1;
    gpuskip:off;zo_off:1495 1629:2.5;gpuskip:on;zo_on:1495 1629:2.5;size:max;gpuskip:off;zl_off:1495 1629:2.5;gpuskip:on;zl_on:1495 1629:2.5"
  ```
- **Spots:**
  - Britain `1495 1629:1`;
  - the bank `1430 1690:1` (under a roof: roof hiding);
  - zoomed out `1495 1629:2.5`;
  - the large window: `size:max` gives 3840×2054, the heaviest view;
  - walk `walk:walk:1` (streams chunks and moves the camera);
  - for W0 also run Legacy (`world:legacy`) at the same spots.
- **Time:** about 18 s per spot at 2 windows (8 s settle + 10 s), plus about 25 s for login. Ten spots take about 4 minutes.
- **Summaries:** average the windows' `phases.<key>[0]` (avg) and `[1]` (p50) per tag. Skip windows under 2 s: `StopWindow` reports a sliver at the end of each spot.

## Reading results (noise)

- **Compare within one session,** alternating off and on, at least twice at the spots you care about. Across sessions, frame times move by 10–30%.
- **The machine is shared.** Other agents' clients raise Present, and occasionally the frame p50, mostly while walking. Use world prepare, draw and the counters; treat FPS as a sanity check. Absolute numbers are higher than the owner's AOT FPS test (JIT, not topmost, other clients running).
- **Counters don't move with noise.** Tiles walked and pick tests show whether a change took effect: at 4K, tiles walked go 92416 → about 1565 with W1.

## Parity for changes to world prepare

- **Screenshots in one session:** `CUO_BENCH_SHOTS=<dir>`. After each spot, run `gpuskip:off;shot:X_off;gpuskip:on;shot:X_on;world:legacy;shot:X_leg;world:gpu;gpuskip:on` (substitute your own switch). Each `shot:` saves `X_*_gpu.png` (world RT) and `_lights.png`.
- **Spots to cover:**
  - Britain;
  - inside the bank (roof hidden) and zoomed out there (roof plus many overrides);
  - the bank's front wall `1435 1692` with `cot:on` (circle of transparency);
  - Yew `542 985:1` (trees and foliage);
  - zoomed out at Britain;
  - a walk.
  - The shard has no player house to test multis in yet.
- **Diff:** count pixels whose max channel differs by more than 8, and group them into regions (16 px cells). Then **look at every region:**
  - **expected:** moving NPCs and animals, the player's idle frame, rain drops (1–4 px blue dashes), lights within ±8;
  - **anything static is a bug.** Example: black land at the bottom of the view came from stale screen positions after turning skipping off.
- **Base build vs new:** run the base build's plan with the same spots in another session and compare `X_gpu.png`. Only moving creatures may differ.
- **Gpu vs Legacy:** expect the known filtering differences: Britain about 7% of pixels > 8, bank about 16%, zoomed out about 75%.
- **Invariant checker:** `CUO_GPU_SKIP_CHECK=1`. Every 30th skipping frame it verifies each skipped baked object is settled and drawn exactly as the full walk would draw it. It logs `[gpu-skip-check] … 0 unsettled, 0 drawn differently` every 5 s, plus the reasons tiles are walked in full. It costs a full walk every 30 frames, so don't measure with it on.
- **Picking:**
  - `CUO_BENCH_PICK=1` (no plan) hovers and clicks a static, a land tile, a ground item and the player in Legacy, then in Gpu. The objects must match. A `TextObject` from the earlier clicks can win a few samples in the second half; that's expected.
  - The plan entry `pickscan:8x6` hovers an 8×6 grid of the view, comparing the selection with skipping off and on at each point. It must log `0 different`. About 13 s per scan; it warps the OS mouse inside the client window.

## Cleanup

- The run script removes the temp settings/profile folder, the lock file and any `Data/` the client created in the bin folder.
- Close the client gracefully: `taskkill` without `/F`, then `/F` after 15 s, and only on your own PID.
- After a failed run, check for leftover `cuo.exe` processes of yours. A `VK_ERROR_DEVICE_LOST` or socket-closed error means the run is invalid; rerun it. Check the System log for nvlddmkm event 153 at that time.
