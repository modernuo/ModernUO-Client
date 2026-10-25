# Testing: the world update shortcuts (W5)

How the W5 slices of world-prepare-ecs-plan.md are checked and measured. Written 2026-10-25 by the W5 agent. Read [../testing-playbook.md](../testing-playbook.md) first: accounts, lock files, `DOTNET_GCHeapHardLimit`, the encrypted password, cleanup.

## What W5 changed (three settings, all default on)

| Setting | What it skips | Off = |
|---|---|---|
| `world_update_active_items` | `World.Update` visits only the items whose `Update` does something (corpses, items whose object handles just opened), plus a full pass every 50 ms for the range removal. Destroyed items leave `Items` through `Entity.Destroy`. Also: the hits percentage is computed in the `Hits`/`HitsMax` setters (always), and `WMapManager.AcceptsLocalUpdates` is asked once per frame | The per-frame loop over every item |
| `gpu_mobile_frame_cache` | A mobile's action selection and sprite instances are reused while their inputs are the same (`MobileFrameCache`); position, walking offset and depth are applied again each frame | Selection and extract every frame |
| `text_skip_expired` | The overhead text pass skips the layout of mobiles and corpses whose texts have all expired (they stay in the entity's text container) | Laid out every frame |

Switches: plan entries `activeitems:on|off`, `mobcache:on|off`, `textskip:on|off`, and `w5:on|off` for all three; `-ecsdriver` commands `activeitems on|off`, `mobcache on|off`, `w5 on|off`. With all three off the client behaves as the base build, apart from the always-on parts (the hits setters, the shared `EquipmentSlots.Version` counter, `Chunk.ContentVersion`, `FindNext` without LINQ).

## Checkers (don't measure with them on)

- **`CUO_W5_CHECK=1`** (world_update_active_items and text_skip_expired). Every frame, before the active item update, every item is checked against what the full loop would have done:
  - an item that needs an update must be listed (`needing an update but unlisted`);
  - the 50 ms sweep counts items no hook had listed (`caught by the sweep`);
  - every entity with `HitsMax > 0` must have the percentage the per-frame rule gives (`hits checks, N wrong`);
  - after the update no destroyed item may be left in `Items`;
  - each skipped text layout is run anyway and must not move a text (`text layouts skipped N, M would have moved a text`).
  - Logged every 5 s as `[w5-check]`. All failure counts must be 0; the `corpse`, `handle`, `destroyed` and `skipped` counts show the paths were exercised.
- **`CUO_MOBILE_CACHE_CHECK=1`** (gpu_mobile_frame_cache). Every reused selection is compared with a fresh `Select` (the whole `AnimationState` and every slot's group), every replayed emission with a fresh `Emit` (all instance bytes, FrameInfo, lights). Logged every 5 s as `[mobile-cache-check] selections reused N, run M; checked N, 0 different | instances replayed …, emitted …; checked …, 0 different`.
- **ECS parity** (`CUO_ECS_PARITY=1 CUO_ANIM_SYNC=1`, [ecs-animation.md](ecs-animation.md)) goes through the cache: its ECS side calls the same cached selection and emission, so 0 mismatches means the cached output equals the capture. Run `CUO_GPU_ITEM_CHECK=1` alongside (W4).

## Scenario scripts

- **`tools/ecs-parity/w5items.txt`** (Green Acres): spawns four NPCs, logs their hits (`mobiles`), sets two NPCs' hits (`[set Hits 7`, `[set Hits 20`) and logs again, adds a bandage and an apple, switches object handles on and counts them (`handles on|off|count`), picks the bandage into the backpack, opens it, changes its hue twice in the pack (`[set Hue` + `target held`) with `items` after each, kills three NPCs (corpses), handles again, walks, then removes the bandage and wipes. About 60 s.
- Driver commands added for it (EcsSessionDriver): `handles on|off|count`, `mobiles` (hits, max and percentage within 8 tiles), `target held` (the item the last `pick` picked up).
- The standard parity scenarios (`bank`, `items`, `corpses`, `make_crowd.py 120`, the large scene's `ls_parity`) and the plan `tr:travel;bo:bodies;wk:walk`, all with `CUO_W5_CHECK=1 CUO_MOBILE_CACHE_CHECK=1` plus parity and the item check.

## Measuring

- **In-session alternation is the fair A/B:** `w5:off;z1_off:5156 2150:1;w5:on;z1_on:5156 2150:1;…` (twice each, zoom 1, 2.5 and `lswalk`). Base-vs-new sessions were noisy on 2026-10-25 because another agent's client ran at the same time; the in-session pairs are not affected by drift.
- **Phases:** `update` and `world_items` (World.Update's item loop), `world_mobiles` (its mobile loop), `prep_cap_mobile` (mobile selection and extract), `prep_text` (the overhead text pass), `world_prepare`.
- **The crowd:** the first 483 lines of `make_crowd.py 120` (the spawns) followed by `tools/ecs-parity/w5crowd_tail.txt` (`w5 off|on` and `window <tag>` pairs standing and walking, then the wipe); run with `CUO_FRAMESTATS=1`. The NPCs fight and wander, so mobiles walk and corpses appear; compare the pairs, not the absolute numbers.

## Reading results

- `selections reused` vs `run`: walking mobiles (steps queued) are never reused, and a frame change re-runs the selection, so the hit rate is about 95% for standing mobiles at a few hundred fps and lower in a moving crowd.
- `text layouts skipped` grows with every mobile that ever showed text (incoming names); in the large scene that is all 1,985.
- A `[w5-check]` line with `active list` > 0 while corpses exist is expected (corpses animate).
