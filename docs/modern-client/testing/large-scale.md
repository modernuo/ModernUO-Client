# Testing: the large-scale scene (2,000 mobiles, 20,000 ground items)

A repeatable busy scene on the local ModernUO shard, for measuring how world prepare, update and the GPU world scale, and for parity runs at scale. Written 2026-10-25 by the large-scale agent. Read [../testing-playbook.md](../testing-playbook.md) first: accounts, lock files, `DOTNET_GCHeapHardLimit`, the encrypted password, cleanup.

## The scene

- **Where:** Felucca (map 0), centred on **5156 2150**: an empty grass field in the unused strip east of Britannia (x ≥ 5120). No region, no statics, land z 0 for 36+ tiles around the centre, water about 50 tiles out. It is far from Britain, the bank, Green Acres (where the parity scripts spawn and wipe) and kbatman.
- **What:** everything within 18 tiles of the centre, so a client standing there holds all of it.
  - **1,984 mobiles:** a 32×32 block (5140..5171, 2134..2165), two per tile, 4-row strips of Brigand, SavageRider (mounted), Horse, Orc, EvilMage, Cow, Mongbat, Skeleton, TownCrier (plain `Mobile`, no AI), Ninja, Gargoyle, Llama, Zombie, Dragon. All `Frozen` and `Blessed`, creatures also `CantWalk`: they never move, path or fight. Their stand animations still loop on the client, and the humans wear equipment (about 5,600 worn items).
  - **19,476 ground items:** a 36×36 block (5138..5173, 2132..2167), 15 full layers at z 0..7 (gold and arrow stacks, bandages, a backpack, chest, sword, axe, spellbook, robe, logs, apples, a bottle, single coins) plus four 12×12 blocks of lit or animated art (brazier 0xE31, candle 0xA0F, 0x3996, fire 0x19AB). All non-movable, so they never decay.
  - **A walking lane:** row y 2150 is free of mobiles and items from 5138 to 5173. DevBench `lswalk` walks it end to end.
- **Server load:** creatures' AI runs only while a player is near (sector activation). With a client standing in the scene ModernUO used about 4% of one core (11 s of CPU in 5 minutes); with nobody there, nothing measurable. ModernUO's working set grew from 563 to 711 MB over the session that built (and once rebuilt) it.
- **Left in place** (frozen and cheap). Since 2026-10-25 11:01 it is the server-built copy from `[TestScene create` below; the client-built one from before was wiped.

## Create and wipe: ModernUO's `[TestScene`

The scene is a server feature on the local ModernUO branch `modernuo-client/test-scene` (worktree `C:\Repositories\ModernUO\.wt-testscene`, file `Projects/UOContent/Commands/ClientPoc/TestScene.cs`; local only, not pushed). The running server loads it since 2026-10-25 11:00. It needs GameMaster access or above (kbatman and kbatman2–4 are Owner).

| Command | What it does |
|---|---|
| `[TestScene create` | Builds exactly the layout above in one server tick (strips, layers, blocks and lane as the old `[TileXYZ` script, same order). It refuses if the scene exists or anything but players is within 20 tiles of the centre |
| `[TestScene wipe` | Deletes exactly the objects `create` made (riders' mounts and worn items go with them). They are tracked by serial in `Saves/TestScene/TestScene.bin`, written with the world save, so this works after a restart. Nothing else in the area is touched |
| `[TestScene go` | Moves you to Felucca 5156 2150 0 from any facet and remembers where you were. A second `go` from inside the scene keeps the first return point |
| `[TestScene return` | Takes you back to that location and facet |
| `[TestScene status` | Tracked mobiles and items, plus the NPCs and ground items within 20 tiles |

- **Rebuilding** is `[TestScene wipe` then `[TestScene create`, from anywhere.
- **`make_scene.py`** now only writes `-ecsdriver` scripts that issue these commands from a client (`create`, `wipe`, `status`; each one does `go` first and `return` at the end). The `[TileXYZ`/`[wipe` generator is gone: it took about 95 s through a client.
- **The driver's check lines** with the scene in range read `mobiles=1985` (with yours) and `items` about 25,100 (ground items plus worn ones; the creatures' equipment is random).

**Replacing the client-built scene (2026-10-25 11:00, kbatman4):**

| Step | Server (`[TestScene status`) | Client check at the centre |
|---|---|---|
| Before, old client-built scene | 0 tracked; 1,984 NPCs, 19,476 ground items in the area | mobiles=1985, items=25,142 |
| After `[wipe` of the ±20 box (old scene only) | 0; 0 NPCs, 0 items | mobiles=1, items=24 (own) |
| After `[TestScene create` | 1,984 mobiles and 19,476 items tracked; 1,984 NPCs, 19,476 ground items in the area | mobiles=1985, items=25,136 |
| After `go` from inside the scene, then `return` | | back at Trammel 5156 2150, where kbatman4 started |

## Measuring

- **The owner's FPS test** (`tools/fps-test`) measures the scene as its last spot, "Large scene" (zoom 1, 1920x1080, Legacy and Gpu), between `say:[TestScene go` and `say:[TestScene return`.
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

**W4 (ground-item cache, 2026-10-25, base f793e1bb4 → 80a3db4f2):** Gpu world prepare in the scene 1.67–2.01 → 1.32–1.45 ms at zoom 1, 2.19–2.53 → 1.80–1.97 at zoom 2.5, walking 1.46–1.78 → 1.25–1.44; objects walked 21k → 8.8k; about 11.3k of the 14.2k items in view are served from the cache. Full table and checks in poc-log ("W4: ground-item chunk cache"). With the cache on, the scene's `[PARITY] items=` covers only walked items; run `CUO_GPU_ITEM_CHECK` for the rest.

## Reading results

- **The creatures animate** (stand loops, Mongbats and Dragons flap), so two shots of the scene always differ on the creatures. Compare items and land, and use the parity harness for the creatures.
- **The scene sits on two limits** that existed before 2026-10-25: about 33k dynamic instances and 82 anim pages zoomed out. With the old 32,768-instance / 64-layer limits the north tip of the scene went missing and anim pages were evicted and re-uploaded every few frames. Check the `GPU world:` trace line: `dynamic dropped` must stay 0 and the anim-page evictions must stop growing after the first seconds.
- **Allocation:** about 345 B per frame standing anywhere in the scene, the same as Britain. Walking the lane allocates 9–11 KB per frame: objects leaving and entering range are destroyed and re-created from the server's packets.
- **Noise:** frame times move 10–15% between sessions on the shared machine; alternate base and new builds and compare the same tags.

## Stress scene: 7,500 Balrons (network and update stress, 2026-10-31)

The owner's network stress test for ModernUO: 7,500 Balrons with their AI on in a 32×32 square. On upstream ClassicUO it stalls and then crashes the client. This is a temporary spawn, not a standing scene: spawn it, measure, wipe it.

- **Where:** Felucca (map 0), the block **5140..5171 × 2214..2245** (centre **5156 2230**), grass at z 0 with no statics, 47 tiles south of the TestScene's edge (y 2167) and out of its range. Staging spot for teleports: **5230 2230** (empty grass, out of range of both). Check the block first (`[go 5156 2230 0` then a driver `check`: `mobiles=1`).
- **Spawn** (GameMaster or above, from 5230 2230 so the spawning client never sees it; `[TileXYZ` uses your facet, so `[TestScene go` first): seven `[TileXYZ 5140 2214 32 32 0 Balron`, then `[TileXYZ 5140 2214 32 10 0 Balron` and `[TileXYZ 5140 2224 12 1 0 Balron` (7,168 + 320 + 12 = 7,500; each command builds 1,024 in one server tick). The ModernUO command log (`Distribution/Logs/Commands/Owner/<account>.log`) records each `building from … : Balron` line. The driver script is `spawn.txt` in the balron-stress scratch folder; copy it from the poc-log entry if that is gone.
- **Server load:** with a client among them the Balrons' AI runs (sector activation) and ModernUO uses about one core; with nobody near, nothing measurable. The Balrons wander: a client at the centre counts about 7,390–7,450 of the 7,500 in range after 20 s.
- **Wipe:** from the centre, `[wipe mobiles` and `[wipe items` with ground targets at −40 −40 and +40 +40 (box 5116..5196 × 2190..2270; players are never wiped, nothing else lives there). Then check the centre and points 30 tiles out (`mobiles=1`). Definitive check after the next world save: the Balron count in `Saves/Mobiles/Mobiles.idx` returns to its value before the spawn (38 on 2026-10-31; `savecount.py Balron` in the scratch folder reads the idx type table).
- **Our client:** DevBench plans, no `-ecscheck` (the ECS driver needs `-ecscheck > 0`, which also runs the ECS extract every frame; use the driver only for walking). Teleport run: `say:[TestScene go;say:[go 5230 2230 0;say:[go 5230 2230 0;say:[go 5156 2230 0;bal:5156 2230:1;out:5230 2230:1;…` with `CUO_BENCH_WINDOWS=12` (60 s per spot). The teleport-in stall is in the 8 s before `bal` measures: read it from the `[gcs] slow frame` lines after `said [go 5156 2230 0`. Login run: park the character at 5156 2230 and log in (`bal:5156 2230:1`).
- **Upstream ClassicUO** has no DevBench: park the character at the centre with our client, then let upstream autologin there (throwaway settings.json with the encrypted password, `plugins: []`), sample its private bytes and window responsiveness (`SendMessageTimeout(WM_NULL)` every second) from the launcher, and close it after a hard cap.
- **Packets per second:** our client's FrameStats counter `net_packets` (summed over the `bal` windows, divided by their seconds). `poc/Uo.Net.Cli --no-go --steps 0 --stay 60` was meant as a client-independent count, but in bench-000071 the server closed its connection at the server list (it started right after a client logged out; not investigated).

### Results (bench-000071/72, 2026-10-31; NativeAOT 072f9a6f8 and upstream ee79d7ebc, RTX 3090 Ti, pinned P-cores)

Two reps per arm, interleaved on the shared machine. The scene is not stationary: the Balrons wander off, so the count in range fell from 7,320 (first run) to 6,357 (last), and fps follows it. Within-arm spread of frame p50 is 17–20 %, above the 5 % threshold: **no Gpu-vs-Legacy fps difference can be claimed from these runs.**

| run | survives | longest frame at entry (tp-in or login) | standing p50 / p95 / p99 ms | fps | mobiles in range | world prepare / draw ms | private MB peak |
|---|---|---|---|---|---|---|---|
| Gpu tp ×2 | yes, yes | 44, 39 | 5.07/6.76/8.65, 4.35/5.89/6.99 | 194, 213 | 7,320, 6,582 | 2.47/0.58, 2.29/0.55 | 3,005*, 2,291 |
| Gpu login | yes | 55 | 4.71/6.14/7.34 | 236 | 5,858 | 1.98/0.48 | 2,164 |
| Legacy tp ×2 | yes, yes | 36, 23 | 4.96/6.95/8.32, 4.12/5.83/6.74 | 192, 233 | 6,666, 6,357 | 0.73/2.52, 0.53/2.29 | 1,746, 1,649 |
| Legacy login | yes | 103 | 5.27/7.45/9.25 | 182 | 6,762 | 0.77/2.56 | 1,711 |
| upstream login ×2 | yes, yes (closed at the 150 s cap, responsive every second) | n/a (no frame stats) | n/a | n/a | n/a | n/a | 1,639, 1,466 |

\* gpu-tp1 grew the anim layers to 128 after visiting the TestScene first. Packets at the Balrons: 1,310–1,415 per second, about 15 KB/s (packets phase 0.05–0.07 ms per frame). GC while standing: at most one gen0 and alloc 400–860 B per frame. GPU world: 1,728 baked + 16.5–18k dynamic instances (3 per visible Balron), 2 draw calls; Legacy 45 draw calls. Stalls of 384–439 ms (Gpu, 2 of 3 runs) and 112 ms (Legacy, 1 of 3) while standing fall outside every measured phase (the frame's work was under 8 ms): not renderer work.

**Why Gpu does not win here** (it does in the frozen scene): nothing is static. The GPU path's gain is baking land, statics and items; here every visible object is an animated, moving mobile, so both renderers do per-mobile CPU work every frame. Gpu re-extracts every mobile (prep_cap_mobile 1.3–1.7 ms) and uploads ~18k instances (~1.3 MB per frame, inside world draw 0.5–0.6 ms); Legacy batches the same sprites (world draw 2.3–2.6 ms). Per mobile in range, world prepare + draw costs 0.42–0.43 µs on Gpu and 0.44–0.49 µs on Legacy: a tie within noise. Both also spend 0.7–1.1 ms per frame in scene DrawUI (`HealthLinesManager.Draw` and world text, which walk the mobiles) plus about 0.4 ms in prep_text and world_fx_text. The item cache plays no part (no items; prep_item_cache 0.003 ms, 0 rebuilds). The "need more data" warnings are benign (see the poc-log entry).

**Left behind (owner's decision, 2026-10-31):** the wipe (bench-000073) cleared the centre (`mobiles=1` at 5156 2230 and at 5120 2230), but about 100 Balrons that had wandered outside the wipe box stayed: 23 in range of 5156 2200, 44 of 5156 2260, 34 of 5190 2230 (AI on; they keep wandering). Expect live Balrons near 5156 2230 in later tests there.

## Stationary Balron scene (spawner, 2026-10-31)

A standing replacement for the wandering 7,500-Balron spawn: one ModernUO spawner whose Balrons have `CantWalk true`, so the scene stays the same between runs. It is **left in place** on the bench server.

- **What it is:** a `Spawner` named `BalronStressScene` at **Felucca 5156 2230 0**, count 7,500, one entry `Balron` (max 7,500, entry properties `CantWalk true`), HomeRange **16**, WalkingRange unset (falls back to 16), delays 5–10 min (a killed Balron comes back after 5–10 minutes).
- **What HomeRange 16 means** (ModernUO `BaseSpawner.HomeRange` setter): `SpawnBounds = Rectangle3D(x−16, y−16, z −128, 33, 33, depth 256)`, so a 33×33 square, x 5140..5172 × y 2214..2246, both ends inclusive (positions are drawn with `RandomMinMax(Start, End−1)`). HomeRange *n* is a (2*n*+1)² square around the spawner. Each Balron gets `RangeHome = WalkingRange` (16) and `Home` = the spawner's location.
- **Where the Balrons actually are: all 7,500 on the spawner's own tile, 5156 2230.** That is ModernUO behaviour, not a client effect. The spawner applies the entry properties *before* it picks a position. `Map.CanSpawnMobile` only counts land, or a static, as a surface when `!cantWalk` (only `CanSwim` water mobiles get a surface while CantWalk). So every random try fails as a non-transient blocker, `GetSpawnPosition` falls back to `Location`, and after 25 useless results it marks the spawner `Abandoned` and returns `Location` directly. A spread scene can't come from a spawner anyway: `CanSpawnMobile` treats each mobile as a blocker for z ±16, so at most one Balron fits per tile (1,089 in 33×33), and the rest fall back to the spawner's tile too. A spread, stationary 7,500 would take `[TileXYZ` (7 per tile, as in the stress scene above) followed by `[Area Set CantWalk true`. The owner decides whether he wants that.
- **Motion:** CantWalk Balrons never change tile (`Movement.CheckMovement` rejects land for CantWalk), but the AI still runs while a player is near: `DoMoveImpl` sets `Direction` before the step fails, so they keep turning and send direction updates, and the client animates them as usual. Measured stack after 45 s: 7,501 mobiles in range (7,500 + the player), every Balron still at 5156,2230.
- **Recipe (scriptable; the DevBench driver can't type into the spawner gump).** Entry Properties can't be set with `[Set` (SpawnerEntry has no command properties), and `[EditSpawner` cuts its `set` text wrongly (`argSpan[start..^len]`, see the poc-log), so the scene is created with `[ImportSpawners`. The file is `C:/Repositories/cuo-poc-state/scenes/balron-spawner.json`:
  ```json
  [ { "$type": "Spawner", "guid": "ba150000-7500-4000-8000-515622300016", "name": "BalronStressScene",
      "location": [5156, 2230, 0], "map": "Felucca", "count": 7500, "minDelay": "00:05:00", "maxDelay": "00:10:00",
      "team": 0, "homeRange": 16, "walkingRange": -1,
      "entries": [ { "name": "Balron", "maxCount": 7500, "probability": 100, "properties": "CantWalk true" } ] } ]
  ```
  1. Copy it to `C:/Repositories/ModernUO/Distribution/temp/balron-spawner.json`. `temp/` is git-ignored; the import resolves paths against the server's base directory. Remove the copy afterwards.
  2. As Developer or above (the test characters are Owner): `[TestScene go`, `[go 5230 2230 0`, `[go 5156 2230 0`, then `[ImportSpawners temp/balron-spawner.json`. It reports `Generated 1 spawners (0.81 seconds, 0 failures)`. Import calls `Respawn()`, which spawns the whole count in that one call, not a few per tick.
  3. Check with `[Global Count where Balron` (rises by exactly 7,500) and `[Area Count where Balron CantWalk == true`, with ground targets −16 −16 and +16 +16 from the centre (7,500). Then `[TestScene return`.
  
  Re-importing the same file is idempotent: the import deletes an existing `Spawner` at the same location, which removes its spawns, and then respawns. In game, the owner's way does the same: `[Add Spawner`, open its gump, entry `Balron`, properties `CantWalk true`, then `[Props` Count 7500 and HomeRange 16.
- **Remove it:** delete the spawner. `BaseSpawner.OnDelete` removes all its spawns. From 5156 2230: `[Area Delete where Spawner` with ground targets −1 −1 and +1 +1 (not run yet), or set `count` to 0 in the file and re-import.
- **Stragglers:** none inside the 33×33 square before the spawn (`[Area Count where Balron` = 0; the client at 5156 2230 saw only itself). 222 Balrons existed world-wide before, against 38 in the save before the stress spawn: about 184 wanderers of the earlier stress spawn (AI on, CantWalk false) remain around it. At the staging spot 5230 2230 the client saw 4 other mobiles.
- **First look (bench-000076, spawn job, Gpu, NativeAOT 072f9a6f8; no timing measured):** the client stayed responsive throughout. Private bytes peaked at 2.86 GB, and ModernUO used 56 s of CPU over the 123 s session, which includes the import.
