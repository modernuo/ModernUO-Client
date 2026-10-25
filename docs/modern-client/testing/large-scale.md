# Testing: the large-scale scene (2,000 mobiles, 20,000 ground items)

A repeatable busy scene on the local ModernUO shard, for measuring how world prepare, update and the GPU world scale, and for parity runs at scale. Written 2026-10-25 by the large-scale agent. Read [../testing-playbook.md](../testing-playbook.md) first: accounts, lock files, `DOTNET_GCHeapHardLimit`, the encrypted password, cleanup.

## The scene

- **Where:** Felucca (map 0), centred on **5156 2150**: an empty grass field in the unused strip east of Britannia (x ≥ 5120). No region, no statics, land z 0 for 36+ tiles around the centre, water about 50 tiles out. It is far from Britain, the bank, Green Acres (where the parity scripts spawn and wipe) and kbatman.
- **What:** everything within 18 tiles of the centre, so a client standing there holds all of it.
  - **1,984 mobiles:** a 32×32 block (5140..5171, 2134..2165), two per tile, 4-row strips of Brigand, SavageRider (mounted), Horse, Orc, EvilMage, Cow, Mongbat, Skeleton, TownCrier (plain `Mobile`, no AI), Ninja, Gargoyle, Llama, Zombie, Dragon. All `Frozen` and `Blessed`, creatures also `CantWalk`: they never move, path or fight. Their stand animations still loop on the client, and the humans wear equipment (about 5,600 worn items).
  - **19,476 ground items:** a 36×36 block (5138..5173, 2132..2167), 15 full layers at z 0..7 (gold and arrow stacks, bandages, a backpack, chest, sword, axe, spellbook, robe, logs, apples, a bottle, single coins) plus four 12×12 blocks of lit or animated art (brazier 0xE31, candle 0xA0F, 0x3996, fire 0x19AB). All non-movable, so they never decay.
  - **A walking lane:** row y 2150 is free of mobiles and items from 5138 to 5173. DevBench `lswalk` walks it end to end.
- **Server load:** creatures' AI runs only while a player is near (sector activation). With a client standing in the scene ModernUO used about 4% of one core (11 s of CPU in 5 minutes); with nobody there, nothing measurable. ModernUO's working set grew from 563 to 711 MB over the session that built (and once rebuilt) it.
- **Left in place** after 2026-10-25 (frozen and cheap). Wipe it with the script below if it is in the way.

## Create and wipe

`tools/large-scale/make_scene.py` writes `-ecsdriver` scripts. They need an account with GameMaster access or above (kbatman2–4; never kbatman).

```
python tools/large-scale/make_scene.py create > <scratch>/ls_create.txt
python tools/large-scale/make_scene.py wipe   > <scratch>/ls_wipe.txt
```

- **create** `[go`es to the centre and issues `[TileXYZ x y w h z <type> [args] set Frozen true Blessed true …` per strip and per item layer: absolute coordinates, no targeting, so it is deterministic. About 95 s. The driver's last `[ECS] check` line should read `mobiles=1985` (with yours) and `items` about 25,100 (ground items plus worn ones).
- **wipe** `[go`es to the centre and runs `[wipe` with a targeted box of ±20 tiles. About 30 s. The last check should read `mobiles=1` and only your own worn items. Never run it anywhere else: `[wipe` deletes every item and NPC in the box.
- An optional `x y` after `create`/`wipe` moves the centre; check the new spot is empty and flat first.
- The driver only runs when `-ecscheck` is set (any interval), e.g. with `tools/ecs-parity/run_parity.py` (`--script`, `--parity 0`) or your own launcher.
- Don't create it twice: `[TileXYZ` adds, it doesn't replace. Wipe first.

## Measuring

- **Plan entries** (DevBench): `ls_z1:5156 2150:1`, `ls_zo:5156 2150:2.5`, and partial views `ls_e12:5168 2150:1` (about two thirds of the scene in range) and `ls_e24:5180 2150:1` (about a third). `name:lswalk:<zoom>` `[go`es to the centre and walks the lane back and forth (objects leave and enter range at the ends).
- **Environment:** `CUO_FRAMESTATS=1 CUO_BENCH_SIZE=1920x1080 CUO_BENCH_WINDOWS=2 CUO_BENCH_EXIT=1 CUO_BENCH_NOSHOT=1`. `world:legacy` for the Legacy columns. `prepdetail:on` for the walk buckets (see [world-prepare.md](world-prepare.md) for correcting them).
- **Summaries:** `python tools/large-scale/summarize.py <log> [--keys a,b] [--counts c,d]` prints one row per plan tag: fps, alloc per frame, gen0, the phases (ms, averages of the windows) and counts.
- **Private bytes:** not in FrameStats; sample the process from your launcher (`GetProcessMemoryInfo`, `PrivateUsage`).
- **Time:** about 18 s per spot at 2 windows plus about 30 s for login. Teleporting into the scene holds the frame for up to about 650 ms (27,000 objects arrive).

## Checks that ran here (2026-10-25)

| Check | How | Result |
|---|---|---|
| ECS parity | `CUO_ECS_PARITY=1 CUO_ANIM_SYNC=1 -ecscheck 120`, driver: `[go 5156 2150`, wait, walk the lane east, west and back running | 0 mismatches: 9.46M mobile comparisons (892k mounted, 2.1M equipped), 75M item comparisons |
| World prepare shortcuts | `CUO_PREP_CHECK=1` with `lswalk`, `pickscan:8x6`, `walk`, `glide`, zoom 2.5 | Kept screen positions: 0 wrong; item picks rejected by the rectangle: 0 the pixel test would hit |
| Baked skipping | `CUO_GPU_SKIP_CHECK=1`, same plan | 0 unsettled, 0 drawn differently |
| Picking | `pickscan:8x6` at the scene (zoom 1 and 2.5) and Britain; `CUO_BENCH_PICK=1` base vs new | 0 different; the same objects in both builds |
| Teleport settle | `travel` and spot entries between Britain and the scene | 0 timeouts |

## Results (2026-10-25, JIT Release, 1920×1080 at 1.5 DPI scale, no other clients)

Base = poc/modern-client 507dc2995, new = the large-scale branch (60d18112d). Averages of 2 alternated sessions × 2 windows; ms per frame. `objects` = objects the world prepare walked (Gpu) or visited (Legacy). The base build has no `lswalk`, so walk rows are the new build only.

| Spot | Renderer | objects | fps base → new | frame | world prepare | walk | item extract | mobile extract | text | update | world draw |
|---|---|---|---|---|---|---|---|---|---|---|---|
| Scene, zoom 1 | Gpu | 21.1k | 252 → 341 | 3.99 → 2.96 | 2.90 → 1.92 | 1.02 → 0.66 | 0.62 → 0.42 | 0.62 → 0.59 | 0.20 → 0.15 | 0.34 → 0.32 | 0.33 → 0.31 |
| Scene, zoom 2.5 | Gpu | 21.5k | 200 → 291 | 5.03 → 3.45 | 3.78 → 2.37 | 1.24 → 0.77 | 0.85 → 0.55 | 0.83 → 0.75 | 0.22 → 0.14 | 0.38 → 0.32 | 0.43 → 0.38 |
| Scene, 2/3 in range | Gpu | 14.0k | 327 → 565 | 3.07 → 1.77 | 2.09 → 1.11 | 0.76 → 0.40 | 0.42 → 0.25 | 0.42 → 0.31 | 0.17 → 0.07 | 0.30 → 0.18 | 0.27 → 0.19 |
| Scene, 1/3 in range | Gpu | 7.0k | 813 → 1100 | 1.23 → 0.91 | 0.74 → 0.44 | 0.28 → 0.17 | 0.16 → 0.10 | 0.11 → 0.09 | 0.06 → 0.03 | 0.11 → 0.09 | 0.11 → 0.10 |
| Scene, walking the lane | Gpu | 16.3k | – → 364 | – → 2.77 | – → 1.60 | – → 0.57 | – → 0.32 | – → 0.52 | – → 0.11 | – → 0.32 | – → 0.26 |
| Britain | Gpu | 254 | 1463 → 1520 | 0.68 → 0.66 | 0.09 → 0.06 | 0.03 → 0.02 | 0 | 0 | 0.02 → 0.00 | 0.04 → 0.03 | 0.07 → 0.07 |
| Bank | Gpu | 260 | 1512 → 1534 | 0.66 → 0.65 | 0.09 → 0.07 | 0.03 → 0.02 | 0 | 0.01 | 0.02 → 0.00 | 0.04 → 0.04 | 0.08 → 0.06 |
| Britain, zoom 2.5 | Gpu | 1.2k | 1369 → 1444 | 0.73 → 0.69 | 0.23 → 0.21 | 0.09 → 0.08 | 0 | 0 | 0.02 → 0.00 | 0.03 → 0.04 | 0.08 → 0.08 |
| Scene, zoom 1 | Legacy | 25.7k | 206 → 225 | 4.87 → 4.45 | 1.38 → 1.05 | 0.98 → 0.79 | – | – | 0.35 → 0.22 | 0.59 → 0.56 | 2.34 → 2.29 |
| Scene, zoom 2.5 | Legacy | 45.8k | 142 → 157 | 7.03 → 6.39 | 2.26 → 1.84 | 1.72 → 1.44 | – | – | 0.35 → 0.22 | 0.56 → 0.51 | 3.59 → 3.45 |
| Scene, walking the lane | Legacy | 20.6k | – → 245 | – → 4.15 | – → 0.96 | – → 0.75 | – | – | – → 0.17 | – → 0.54 | – → 1.86 |
| Britain | Legacy | 8.1k | 1245 → 1223 | 0.80 → 0.82 | 0.18 → 0.17 | 0.14 → 0.15 | – | – | 0.02 → 0.00 | 0.03 → 0.04 | 0.21 → 0.21 |
| Britain, zoom 2.5 | Legacy | 41.5k | 449 → 407 | 2.24 → 2.46 | 1.01 → 1.14 | 0.89 → 1.03 | – | – | 0.03 → 0.00 | 0.05 → 0.07 | 0.90 → 0.94 |

- **About 0.33 ms of the base build's scene frame was FrameStats itself** (two clock reads per captured object, `prep_rest` 0.35 → 0.02 ms; fixed in the new build). FrameStats is on in every measured run, including the owner's FPS test, so the fps column includes that saving; with FrameStats off the real gain at zoom 1 is about 0.7 ms per frame, not 1.0.
- **Legacy, Britain zoom 2.5** looked 0.13 ms slower in this series; a 3-round re-run of just those spots gave 1.05 → 1.07 and 1.04 → 0.94 ms. Noise.
- **Allocation per frame is flat with scene size:** 870–980 B in this series at every standing spot, base and new alike (Britain included; an earlier, shorter plan measured 340–350 B everywhere). Walking the lane: 7–10 KB per frame, from objects leaving and entering range.
- **Private bytes:** 1.86 → 2.01 GB over a base session, 1.93 → 2.07 GB over a new one (+64 MB: the 64 extra anim layers).
- **Update** is about 0.3 ms in the scene, two thirds of it `World.Update`'s item loop (25,100 items, about 8 ns each, most of them worn or in packs).

## Reading results

- **The creatures animate** (stand loops, Mongbats and Dragons flap), so two shots of the scene always differ on the creatures. Compare items and land, and use the parity harness for the creatures.
- **The scene sits on two limits** that existed before 2026-10-25: about 33k dynamic instances and 82 anim pages zoomed out. With the old 32,768-instance / 64-layer limits the north tip of the scene went missing and anim pages were evicted and re-uploaded every few frames. Check the `GPU world:` trace line: `dynamic dropped` must stay 0 and the anim-page evictions must stop growing after the first seconds.
- **Allocation:** about 345 B per frame standing anywhere in the scene, the same as Britain. Walking the lane allocates 9–11 KB per frame: objects leaving and entering range are destroyed and re-created from the server's packets.
- **Noise:** frame times move 10–15% between sessions on the shared machine; alternate base and new builds and compare the same tags.
