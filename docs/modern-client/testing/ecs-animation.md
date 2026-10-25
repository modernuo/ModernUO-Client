# ECS Animation Testing (capture vs ECS extract parity, `-ecscheck`)

Written 2026-10-25 by the W3 agent. It covers how the ECS mobile animation path (W3 groundwork) is checked against ClassicUO's view capture. Read [../testing-playbook.md](../testing-playbook.md) first; its ground rules (no password on a command line, memory cap, close what you start, clean up spawns) apply here.

## What it tests and why

- **Sprite parity:** the Gpu renderer still gets mobiles by capturing `MobileView.Draw` (`GpuWorldRenderer.DrawObject`). W3 adds an ECS path that must give the same result before it replaces the capture:
  - `MobileAnimationSystem` fills `AnimationState`, `MobileRenderState` and `EquipmentSlots`;
  - `MobileSpriteExtract` turns them into `SpriteInstance`s from the pack tables.
  - The harness runs both for the same mobile in the same frame and diffs the instances.
- **Mirror consistency** (`-ecscheck`): the ECS mirror still matches World (as before). It also checks each mobile's `EquipmentSlots` against MobileView's layer walk (order, item serials, mount, hand items) and its action and frame against the Mobile.

## The parity harness

- **Enable it:** `CUO_ECS_PARITY=N` compares every Nth Gpu frame (1 = every frame). It needs `-worldrenderer gpu`. Off by default; the only cost when off is one bool check per captured object.
- **What it diffs, per captured mobile:** both instance lists, in emission order, all 32 bytes:
  - X/Y;
  - w/h and flags (hued, partial, mirror, indexed);
  - UV;
  - page and hue row;
  - the depth key;
  - the Light word (alpha, hue mode, cot/additive bits);
  - Corners (Indexed8 palette).
  - It also flags a count difference. Screen position, base depth and draw offset are inputs to the extract (W2's job), taken from the capture call.
- **Mismatch lines** (`[PARITY] mismatch`, Warning): the first diff of up to 40 distinct mobiles, plus a few repeats. Each line shows:
  - the World state (serial, graphic, hue, position, direction, frame);
  - the ECS state (body, group, stored dir, facing, frame, flags, mount, seat, override hue, alpha, offsets, and each slot as `layer=body/hue/group`);
  - the instances around the first difference, capture then ECS, decoded (`xy wh fl page hue uv depth mode a pal`).
  - **How to read it:**
    - `fl` differing by 0x10 is the mirror bit;
    - a different `depth` with the same rest is the strip index or the base depth;
    - a different `page/uv` is the frame, action or direction;
    - a count difference is a layer or a strip appearing on one side only.
- **Summary** every 5 s and at `quit` (`[PARITY] frames=…`):
  - frames, mobiles compared, mismatches, instance totals;
  - cost per frame and per mobile: capture (the view's `Draw` under capture), ECS sample+select, extract;
  - the distinct mobiles seen (and how many mismatched);
  - per-case counts (mobile-frames). Use them to prove a case actually happened: `Mounted`, `Sitting`, `CharSitting`, `GargoyleBody`, `GargoyleLayerGroups`, `Flying`, `Running`, `Walking`, `StepOffset`, `Hidden`, `Dead`, `NonHuman`, `Equipped`, `Cloak`, `Converted` (equipconv), `OverrideHue`, `Translucent`, `ShadowInst`, `Spectral`, `Partial`, `Mirrored`, `ServerAnim`.
- **Prove the harness can fail** after changing it: break the extract on purpose (e.g. strip depth `i * 2` → `i * 3` in `MobileSpriteExtract.Layer`) and confirm every mobile mismatches. Then revert.

## Since the extract became the source (2026-10-25)

- **`gpu_ecs_sprites`** (default on) draws mobiles and art items from the ECS extracts. On parity frames the view is still drawn under capture first as the reference, its side effects stand, and the ECS instances replace its instances after the diff. With the setting off, the parity harness runs as before (capture drawn, ECS compared).
- **Always set `CUO_ANIM_SYNC=1` for parity.** Otherwise the view draws nothing for a body's first-seen direction until a worker decodes it, while the extract draws it at once; those first frames show up as capture-side count mismatches.
- **More lines in the summary:**
  - `view effects:` mirror bit, FrameInfo and lights compared per mobile;
  - `[PARITY] items=…` for every world item (instances, drawn result, light), ending with `corpses=N (instances M) multis=K`. Since 2026-10-25 (integration agent) corpses and item multis come from `ItemSpriteExtract` too, so check those counters are non-zero when a scenario is meant to cover them.
  - Mismatch lines are `[PARITY] view-effect mismatch` and `[PARITY] item mismatch`.
- **Scenarios added:** `lights.txt` (night; a torch, then the light-only graphic 0x1647, then a non-human body; about 50 s) and `items.txt` (night; a lit candle, an arrow stack, animated art, bandages; wiped at the end; about 40 s).
- **Driver commands added:** `light <hex>|off`, `ecssprites on|off`, `window <tag>|stop` (FrameStats windows with `CUO_FRAMESTATS=1`, for in-session A/B timing), and for cleanup `equip`, `items` and `target item <hex serial>`. Later (W4): `items` also logs hue, flags and z; `shot <name>` (a world shot into `CUO_BENCH_SHOTS`), `itemcache on|off` and `gumpok` (OK on the frontmost server gump, e.g. `[area delete`'s confirmation).
- **The ground-item cache (W4)** takes most items out of the parity harness's view: served items are not drawn through `DrawItemEcs`, so `[PARITY] items=` counts only walked items. `CUO_GPU_ITEM_CHECK` covers the served ones (cache vs a fresh extract); run both. `itemcache off` restores the old coverage.
- **The mobile frame cache (W5, `gpu_mobile_frame_cache`, default on)** is on the parity path too: the harness's ECS side uses the cached selection and emission, so parity checks what the renderer draws. `mobcache off` (driver) or `mobcache:off` (plan) compares fresh selections instead; `CUO_MOBILE_CACHE_CHECK=1` compares every reuse with a fresh one. See [world-update.md](world-update.md).
- **The cases and mixed scripts kill and resurrect the character.** That leaves a death robe worn and another in the backpack, plus the cloak. Check with `equip` and `items`, then `[remove` + `target worn 22` / `target item <serial>`. Do this on any account, and always on kbatman.

## Running it

- **Launcher:** `tools/ecs-parity/run_parity.py`. It writes a throwaway `settings.json` with the password encrypted (from `CUO_TEST_PASSWORD`), runs an `-ecsdriver` scenario with `CUO_ECS_PARITY` and `-ecscheck 120`, closes the client, copies the log and deletes the temp folder.

  ```
  export CUO_TEST_PASSWORD=...        # never on the command line
  python tools/ecs-parity/run_parity.py --exe bin/Release/net10.0/cuo.exe --script tools/ecs-parity/bank.txt --out <scratch>/bank.log
  grep -c "PARITY\] mismatch" <scratch>/bank.log; grep -A1 "PARITY\] frames" <scratch>/bank.log | tail -2; grep "ECS\] mismatch" <scratch>/bank.log
  ```

  - The account is `kbatman3` by default (Owner access; ECS work); `--user` changes it.
  - NativeAOT: `dotnet publish src/ClassicUO.Client -c Release -r win-x64 -o <scratch>/aot` (put `C:\Program Files (x86)\Microsoft Visual Studio\Installer` on PATH for `vswhere`), then `--exe <scratch>/aot/cuo.exe`.
- **Worktree agents:** the worktree guard refuses commands with computed paths (`$PWD`, `cygpath`), `cd` into the scratchpad combined with other commands, and heredoc Python. Write scripts to files and pass literal paths.

## Scenarios (`tools/ecs-parity/`, `-ecsdriver` scripts)

| Script | Where | What it drives | Run time |
|---|---|---|---|
| `bank.txt` | Britain bank (1430 1690) | Standing among the bank NPCs, walk and run east/west | ~35 s |
| `cases.txt` | Green Acres (5445 1153, empty) | Cloak equip (all facings), spectral body hue, gargoyle body 666 and 667, gargoyle flying and running, sitting on a spawned chair, death (ghost) and resurrection | ~100 s |
| `mixed.txt` | Green Acres | 11 spawned NPCs (brigands, savage riders = mounted humans, horse, orc, evil mage, ninja, gargoyle, dragon), walking/running among them, hidden, worn-item hue changes, unequip/re-equip, gargoyle body, death, chair, then cleanup | ~120 s |
| `corpses.txt` | Green Acres | Spawns 8 NPCs (brigands, savage rider, orc, horse, evil mage, ninja, gargoyle), `[kill`s each (`target mobile dx dy`), adds a HealerCamp multi, walks among the corpses, then wipes mobiles and items. Covers corpses (human with worn layers, animals, mounted), NPC death re-keys (serial \| 0x80000000) and a camp multi | ~65 s |
| `make_crowd.py N` | Green Acres | Generates a script that spawns N mixed NPCs on a grid (120 = the crowd case), walks and runs, then wipes them | ~150 s for 120 |

- **Driver commands added for this work** (EcsSessionDriver; `target mobile <dx> <dy>` added later: the nearest non-player mobile within 3 tiles of that offset, for `[kill`):
  - `target self | ground <dx> <dy> | worn <layer>` answers the target cursor of a staff command;
  - `pick near <hex graphic>` picks a ground item within 2 tiles (then `drop equip` wears it);
  - `parity` logs the parity summary.
- **Staff commands used** (kbatman3 has Owner access; the commands work as unicode speech):
  - `[go x y`;
  - `[add <Type>` + `target ground dx dy`;
  - `[set Body 666` / `[set Flying true` / `[set Hidden true` / `[set Hue 0x4001` + `target self` (or `target worn <layer>`);
  - `[mortal` then `[kill` then `[res` then `[immortal`. Staff can't be killed while blessed, and `[kill` fails silently.
  - Property names are ModernUO's (`Body`, not `BodyValue`). A wrong name fails silently, so check the case counters.
- **Cleanup:** the scripts end with `[wipe mobiles` and `[wipe items`, each with two `target ground` corners around the spawn area (±15 to ±18 tiles; NPCs wander). They don't touch the bank. No confirmation gump appeared for Owner. Check that the last `[ECS] check` line shows only your own mobile in range (`mobiles=1`). Spawn only at Green Acres: wiping near the bank would delete its NPCs.
- **Side effects on the test character:** `cases.txt` leaves a cloak equipped and may leave extra cloaks in the backpack. The scripts reset body, flying, hidden and hue; if a run is cut short, reset them by hand (`[set Body 400`, `[set Flying false`, `[set Hidden false`, `[set Hue 0x83EA`).

## Expected results (2026-10-25)

- **0 mismatches** in every scenario, under JIT and NativeAOT. Final runs:
  - bank: 43.7k mobile comparisons, 297k instances;
  - cases: 61k comparisons;
  - mixed: 486k comparisons;
  - 120-NPC crowd (129 distinct mobiles): 6.8M comparisons, 46M instances.
- **Cost** (JIT, per frame, harness timings):

  | Scene | Capture | ECS sample+select | Extract |
  |---|---|---|---|
  | Bank, 3–5 mobiles | 8–11 µs | 1.7 µs | 1.8–2.0 µs |
  | Mixed, 11–13 mobiles | 18–29 µs | 2.8–3.6 µs | 3.9–5.2 µs |
  | Crowd, ~103 in view | 70 µs | 10.7 µs | 15.6 µs |

  - Per mobile at crowd scale: capture 1.5 µs, sample+select 0.23 µs, extract 0.33 µs.
  - "Sample" reads the legacy Mobile (until W5).
- **Known false positives:**
  - DevBench's `bodies` and `ghost:` entries swap the player's body locally. Before af894ce94 they didn't sync the mirror, so every `bodies` frame was a mismatch; with parity on, use a build that has the fix.
  - Before the fixes listed in poc-log (W3 entry), the mirror bit diverged after a death re-key and after `-ecscheck` ran selection for undrawn mobiles. If mirror mismatches (`fl` 0x10) come back, look there first.
  - Bodies whose action selection uses `RandomHelper` (LowGroupExtended with server action 5/6) can mismatch for a frame, because both sides roll separately. Not seen in these runs.
- Unit tests for the pure rules are in `tests/ClassicUO.UnitTests/Ecs/AnimationRulesTests.cs`.
