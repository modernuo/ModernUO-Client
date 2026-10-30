# World Prepare → ECS Plan

Drafted 2026-10-24. **World prepare** is ClassicUO's per-frame CPU walk that decides what the world draws: `GameScene.FillGameObjectList` → `AddTileToRenderList` (GameSceneDrawingSorting.cs, 1344 lines), plus the GPU path's override collection and view capture (GameSceneGpuWorld.cs). Since the GPU renderer landed it is the largest remaining frame cost. The owner's FPS test, Gpu mode (fps-test-results-2026-10-24.md):

| Spot | World prepare | Draw total | Share |
|---|---|---|---|
| Britain 1080p | 0.18 ms | 0.41 ms | 44% |
| Zoomed out 1080p | 0.88 ms | 1.16 ms | 76% |
| Zoomed out 3840x2054 | 3.61 ms | 4.10 ms | 88% |

Changing who owns the GPU device (graphics-ownership-plan.md) doesn't move this. The ECS track does.

## What the walk does today, per frame

For every tile of every visible chunk (8×8 tiles per chunk), it walks the tile's linked list of `GameObject`s (land, statics, multis, items, mobiles, effects) and:

1. **Recomputes the screen position** (`UpdateRealScreenPosition`) when the camera moved or the object moved. Under Gpu, the baked chunks already carry positions on the GPU.
2. **Culls** against the viewport, the max Z and roof rules (`UpdateMaxDrawZ`). Under Gpu, the shader already applies max-Z and roof hiding (step 9).
3. **Alpha rules:**
   - foliage "behind a tree" fading (`CheckIfBehindATree`, `ApplyFoliageTransparency`);
   - circle of transparency (shader mode now on the GPU);
   - translucent statics (alpha 178);
   - out-of-range tint.
4. **Picking:** `TrySelectObject` → `PixelPicker` for the object under the mouse.
5. **Light sources:** gathers static and dynamic lights for the light pass.
6. **Object handles and the multi preview.**
7. **Pushes to the render lists:** under Legacy, for drawing. Under Gpu, it stamps `DrawnFrame`, sends overrides to Uo.Render, and runs item, mobile and effect views' `Draw` under the batcher's capture hook to produce dynamic sprite instances.

Under Gpu, **most of this repeats work the GPU already does or that changes only rarely.** Land and statics are baked; only dynamic objects (ground items, mobiles, effects) and a few per-frame rules really change frame to frame.

## Current ECS state

- Friflo ECS mirror (`src/ClassicUO.Ecs`, `src/ClassicUO.Client/Game/Ecs`), keyed by serial.
- Components:
  - `Serial`, `Graphic`, `Hue`, `Position`, `Facing`, `Flags`, `Amount`;
  - `EquippedOn` and `ContainedIn`, indexed by parent serial;
  - tags `MobileTag`, `ItemTag`, `PlayerTag`, `OnGround`, `MultiTag`.
- It mirrors `World` with 0 mismatches. (This section describes the state when the plan was drafted, 2026-10-24.)
- **Since then (2026-10-25):** the ECS extract is the Gpu world's source for mobiles and every world item (W3), mobile animation state and update inputs are ECS state (W3, W5), and ground items are cached per chunk (W4). `World` and its GameObjects still own the state; packet handlers sync into the ECS. See the W sections below.

## Steps

Each step is behind a switch where it changes behaviour, and each must pass this gate:
- picking parity (`CUO_BENCH_PICK`);
- screenshot parity vs the previous step;
- the owner's FPS test;
- 188+ unit tests;
- the ECS consistency check (`-ecscheck`).

### W0: measure inside world prepare
- **Goal:** split world prepare into timed sub-phases with per-frame counts. The ranked table decides the order of W1–W3.
- **Effort:** about 1 day.
- **Current state: done (2026-10-24).** Ranked table in classicuo-perf-baseline.md ("W0"); method in testing/world-prepare.md.
  - Added: sub-phase timings in `FrameStats` (tile walk, dynamic objects, alpha and foliage rules, picking, light gathering, Gpu override collection, view capture by item, mobile and effect, render-list push) and per-frame counts (tiles, objects, overrides, captures).
  - Spots: Britain, the bank, zoomed out, 4K zoomed out, the 120-NPC crowd.
  - Finding: at 4K zoomed out the tile walk is 76% and the Gpu hidden collect 17%. Visiting the objects is more than half the walk (a bare list walk costs 2.2–2.4 ms), so baked tiles must not be visited at all. Nothing in world prepare is SIMD-friendly (linked lists, branchy per-object code).
- **Details:** [W0 and W1: world prepare measured, baked tiles no longer walked (2026-10-24)](poc-log.md#w0-and-w1-world-prepare-measured-baked-tiles-no-longer-walked-2026-10-24)

### W1: stop walking baked land and statics every frame (Gpu mode)
- **Goal:** in Gpu mode the full tile walk runs only for dynamic objects. Each per-frame duty that touches land or statics moves to where it belongs (table below).
- **Effort:** 3–5 days.
- **Current state: core done (2026-10-24), setting `gpu_prepare_skip_baked`, default on.** In GameSceneGpuPrepare.cs:
  - per-chunk tile masks: only tiles with non-baked objects or per-frame duties are walked;
  - targeted picking: the tiles under the mouse are walked in full;
  - screen-edge tiles get a replay of the walk's culling;
  - a full walk for 12 alpha steps after max-Z, roof, season or fading changes and for new chunks, then alphas snap to their settled values;
  - season statics are baked (`WorldRenderer.SetStaticRemap`).
- **Result:** 4K zoomed out (JIT): world prepare 4.86 → 0.73 ms, 179 → 631 fps. Pixel and picking parity hold.
- **Still open** (from the table below):
  - translucent statics and static lights: their tiles are still walked in full;
  - foliage fading and animated statics: trees and foliage are unmeshed, so still walked;
  - the out-of-range tint and gradient circle of transparency fall back to the full walk.

Move each per-frame duty that touches land or statics to where it belongs:

| Duty | Where it goes |
|---|---|
| Screen position, max Z, roofs, circle of transparency | Already on the GPU; skip them for baked objects |
| Translucent statics (alpha 178) | A baked instance flag; the shader applies it, so no per-frame override |
| Foliage "behind a tree" fading | Only trees within a few tiles of the player can qualify: check those on movement or camera change, and send a handful of overrides |
| Animated statics (`AnimatedStaticsManager`) | Baked with an animation id; the shader or a small per-frame table picks the frame. Captures drop to 0 |
| Seasonal graphics | Already remapped at bake time (step 9) |
| Static light sources | Cached per chunk when the chunk loads; on screen = chunk visible |
| Picking | Targeted: look up only the tiles under the cursor (a 3×3 to 5×5 tile neighbourhood, deepest Z first) and run `PixelPicker` there. No full walk |
| Out-of-range tint | A camera-centred range uniform in the shader instead of per-sprite overrides |
| Roof or max-Z fades in progress | Keep the current "force show" overrides, now only for objects actually fading |

- **Expected:** zoomed out at 4K, world prepare 3.6 ms → well under 1 ms (an estimate; W0 confirms the split).
- **Details:** [W0 and W1: world prepare measured, baked tiles no longer walked (2026-10-24)](poc-log.md#w0-and-w1-world-prepare-measured-baked-tiles-no-longer-walked-2026-10-24)

### W2: dynamic objects from ECS queries instead of tile lists
- **Goal:** ground items and mobiles iterate an ECS query over in-view entities, not every tile. Components: `ScreenPos` (recomputed only on `Position` change or a camera move, via Friflo change events or a dirty tag) and `VisibleInView` (set by a chunk-range query). Item and mobile views still `Draw` under capture, but only for the entities the query returns.
- **Effort:** 3–4 days.
- **Current state (2026-10-25, large-scale agent): the screen-position half is done; the ECS query is set aside.** Measured in the large-scale scene ([testing/large-scale.md](testing/large-scale.md): 1,984 mobiles and 19,476 ground items in range).
  - Screen position only on change: a camera generation (bumped on each `UpdateDrawPosition`) replaces the per-frame recompute of every walked object in Gpu mode. Walk 0.84 → 0.65 ms. `CUO_PREP_CHECK=1` verifies it.
  - Item picking: a rectangle test before the art lookup. 0.29 → 0.11 ms.
  - Why the query waits: the walk costs about 31 ns per dynamic object, and the query would replace only the 9 ns visit. The rules (alpha and roof fades, the tile's max-Z cut-off, surface-overhead, behind-a-tree, handles) still read the GameObject, and the tile order sets both the max-Z cut-off and the equal-depth painter order the Gpu path reproduces. About 0.15 ms at 21k objects, nothing at normal scale, for a large parity risk. W4 is the bigger win (below).
- **Details:** [Large-scale scene; scale fixes; W2's screen-position half (2026-10-25)](poc-log.md#large-scale-scene-scale-fixes-w2s-screen-position-half-2026-10-25)

### W3: animation state into the ECS; `DynamicSpriteExtract` replaces view capture
- **Goal:** `AnimationState` (action, direction, frame, time, mount, layer order) and `EquipmentSlots` (a compacted, ordered slot list rebuilt on equipment change, not walked over 23 layers each frame) live in the ECS. Systems advance animation; the extract writes `SpriteInstance`s straight into `WorldRenderer.DynamicBuffer`. Parity must match ClassicUO's animation selection exactly. The existing view code is the reference: port its rules case by case (mounts, sitting, gargoyle flying, partial hues, spectral) with screenshot tests per case. Retire the batcher capture hook for mobiles and items.
- **Effort:** 1–2 weeks. The largest step, and the one ClassicUO 2.0 took with TinyEcs.
- **Current state (2026-10-25): groundwork done for mobiles, parity proven; not yet the renderer's source at that point.** Test procedure in [testing/ecs-animation.md](testing/ecs-animation.md).
  - Components: `AnimationState`, `MobileRenderState` (override hue, alpha, step offset), `EquipmentSlots` (rebuilt only on the `EquipmentDirty` tag, which the mirror sets on equip, unequip, worn-item or body changes).
  - Systems: `MobileAnimationSystem`, then `MobileSpriteExtract` (writes `SpriteInstance`s from the pack tables, no atlas, no batcher). Action selection (`AnimationGroupRules`) and the frame step (`AnimationTiming`) are shared with the legacy Mobile, not duplicated.
  - Parity: 0 mismatches against the capture in every case the harness drives: bank, walking/running, 120-NPC crowd, mounted humans, equipment changes, cloak facings, partial and spectral hues, hidden, gargoyle (both sexes, flying, per-layer actions), sitting, dead. JIT and NativeAOT.
  - Cost: the extract is 0.33 µs per mobile vs 1.5 µs for the capture.
  - Details: [W3 groundwork — mobile animation state in the ECS, sprite extract, capture parity (2026-10-25)](poc-log.md#w3-groundwork--mobile-animation-state-in-the-ecs-sprite-extract-capture-parity-2026-10-25)
- **Current state (2026-10-25, later): the extract is the Gpu world's source for mobiles and art items** (setting `gpu_ecs_sprites`, default on; off = the capture).
  - Mobiles: the view's side effects are reproduced (FrameInfo, mirror bit, worn lights, aura). Art items: `ItemSpriteExtract` (hue rules, stack, animated art, light, picking offset).
  - Parity with `CUO_ANIM_SYNC=1`: 0 mismatches in bank, items, lights and 120-NPC crowd runs (5.2M mobile, 1.5M item comparisons), including the side effects. Screenshots and picking unchanged.
  - Crowd: mobile capture 104 → 45 µs/frame, world prepare 169 → 109 µs. Bank: mobiles 23 → 10 µs, items 6.7 → 2.0 µs.
  - Details: [W3: the ECS extract is the Gpu world's source for mobiles and art items (2026-10-25)](poc-log.md#w3-the-ecs-extract-is-the-gpu-worlds-source-for-mobiles-and-art-items-2026-10-25)
- **Current state (2026-10-25, integration agent): corpses and item multis are done.** `ItemSpriteExtract` draws every world item, so the capture runs on the default path only for effects, captured statics/multi components the walk captures and shadow-only draws. `gpu_ecs_sprites off` keeps it for everything. 0 parity mismatches over about 900k corpse and 260k multi comparisons.
  - Details: [W3 + teleport integration gate; corpses and multis from the ECS; settle follow-up (2026-10-25)](poc-log.md#w3--teleport-integration-gate-corpses-and-multis-from-the-ecs-settle-follow-up-2026-10-25)
- **Next:** move the sampled inputs (steps, server action, war mode, item hue state) to the ECS with W5. Steps and war mode are done (W5 item 5); server action and item hue state are not.

### W4: ground-item chunk baking (only if W0–W3 numbers ask for it)
- **Goal:** bake rarely-changing ground items into the chunk instance buffer, the counterpart of ClassicUO 2.0's `BakeChunkDynamics`. Mobiles stay per frame. Effort: 2–3 days.
- **Why:** in the large-scale scene (Gpu, zoom 1) world prepare is 1.92 ms, of which the items' share of the walk is about 0.6 ms and the item extract 0.42 ms. Mobiles: 0.59 ms (extract) plus their walk share.
- **Suggested shape:** W1's skip machinery for settled ground items. Cache each chunk's item instances (from `ItemSpriteExtract`), re-extract a chunk only on an item change in it, and leave its tiles out of the walk masks. Keep in the walk: animated art, foliage items (behind-a-tree), corpses, multis, items with text or handles, the tiles under the mouse (picking), and everything during fades (W1's full-walk steps). Stamp `DrawnFrame` for cached items (overhead text and bars use it). The original idea was to re-bake on ECS change events; the build does not (see Invalidation).
- **Current state (2026-10-25, W4 agent): done, setting `gpu_prepare_item_cache`, default on; off = the old path.** Code: `GameSceneGpuItemCache.cs`. Procedure in [testing/world-prepare.md](testing/world-prepare.md).
  - Cache: per chunk (`Chunk.GpuItems`), the walk-order instances of every tile whose unbaked objects are only plain settled ground items (and mobiles), in world pixels so the camera doesn't matter. Served tiles leave the walk masks; a served tile's mobiles are walked alone.
  - Invalidation: objects joining or leaving a tile (`GameObject.TileChunk` → `Chunk.OnTileContentChanged`; mobiles don't re-extract), a per-item signature checked every frame (graphic, hue, amount, flags, position, alpha, text, handles, offset, tile), full-walk steps, a new GPU world pass, the setting and the dead-gray tint. It does not hook the ECS change events: the signature catches what they would, and item updates from the server always re-add the item to its tile.
  - Kept in the walk: animated, foliage, translucent, roof and internal items; corpses; multis; items with text or handles, still fading or above the max Z / ground Z; tiles with anything else unbaked; tiles whose item art box is under the mouse; the selected container's tile; every tile during fades; all items while object handles show.
  - Order: served runs join the capture at the statics-list position where the walk would have queued them, so the dynamic buffer and equal-depth ties are unchanged; their lights are added at the same point. `DrawnFrame` is stamped.
  - Result (scene, Gpu, alternated): world prepare 1.67/2.01 → 1.32/1.45 ms at zoom 1, 2.19/2.53 → 1.80/1.97 at zoom 2.5, walking 1.46/1.78 → 1.25/1.44. Normal spots 2–12% less. `CUO_GPU_ITEM_CHECK`: 0 differences over 110M+ served-item comparisons.
  - Left: items on tiles with animated art, corpses or other unbaked objects (2.9k of 14.2k in the scene at zoom 1) are still walked and extracted; mobiles (0.55 ms extract in the scene) are W5's. **E7 (2026-10-26) serves those tiles too** (below).
  - Details: [W4: ground-item chunk cache (2026-10-25)](poc-log.md#w4-ground-item-chunk-cache-2026-10-25)

### W5: the ECS becomes authoritative for the world update
- **Goal:** systems replace the per-object `Update` loops in `World.Update`: skip contained items; keep active lists for animating items and mobiles; compute HP percentage only on change. The `perf-opportunities.md` update-path items get fixed here: `WMapManager` per frame, `RootContainer`, LINQ `Reverse`, the 0x78 re-allocation, `ConfirmWalk` re-inserts.
- **Effort:** 1–2 weeks, piece by piece. Driven by the crowd numbers (the update loop is about 1 µs on the sparse shard, so it matters only in busy scenes).
- **Current state (2026-10-25): first slices and the pushed mobile inputs done, each behind a setting (default on).** Procedure in [testing/world-update.md](testing/world-update.md). Large-scale scene (Gpu, in-session off/on pairs): world prepare 1.66 → 1.17–1.25 ms at zoom 1, 2.35–2.48 → 1.80 at zoom 2.5, 1.79–1.86 → 1.27–1.48 walking; Update 0.45 → 0.17–0.19 ms; frame 2.87 → 2.12–2.20 ms.
  1. **`world_update_active_items`:** `World.Update` visits only items whose `Update` does something (corpses, items whose handles just opened) plus the 50 ms range sweep. Destroyed items leave `Items` via `Entity.Destroy`; the hits percentage is computed in the `Hits`/`HitsMax` setters. Item loop 0.28–0.33 → 0.015–0.02 ms (25,120 items).
  2. **`gpu_mobile_frame_cache`** (`MobileFrameCache`): selection and sprite instances reused while the sampled inputs, equipment version, seat tile, alpha and hues are unchanged; position, walking offset and depth reapplied. Mobile select + extract 0.71 → 0.35–0.37 ms at zoom 1 (1,611 mobiles, about 0.22 µs each), 1.01–1.05 → 0.54 at zoom 2.5.
  3. **`text_skip_expired`:** the overhead text pass skips mobiles and corpses whose texts all expired. 0.18 → 0.04 ms.
  4. **Update-path items:**
     - `WMapManager` asked once per frame: done.
     - LINQ `Reverse` in `FindNext`: done (per keypress, not per frame).
     - `RootContainer`: left. It runs only on facet change and is O(n · depth) with depth 1–3, so it is not a real problem.
     - The 0x78 equipment re-creation and the `ConfirmWalk` re-insert: left. They run per packet or step, not per frame; changing them risks tooltip and equal-depth draw-order behaviour for no frame-time gain.
  5. **`mobile_push_inputs`** (push-inputs agent): a mobile's slow-changing inputs are ECS state that their mutation sites push, instead of being read from the legacy Mobile every frame.
     - **`MobileInputs`** (new component, on every mobile entity): war mode, running, flying, dead, hidden, female, human/gargoyle body flags, has-steps and step-run, the animation body, the facing (`ProcessSteps`' rule), notoriety, the poisoned/paralyzed/yellow-hits/invulnerable bits, last step time and walking delay. `MobileInputRules.Compute` (the Mobile's own rules) runs on every push.
     - **`EquipmentSlots.Mounted`:** `Mobile.IsMounted`, computed from the mobile's own equipment list when the slots are rebuilt. The mirror marks them dirty on every attach, detach (each followed by `Sync`), worn-item change or removal. The mirror's index is keyed by container, so it also holds equipment that arrived before its mobile and was never attached; it must not decide `Mounted` (Codex audit P2).
     - **Pushes:** `WorldEcs.Sync` (graphic, flags and direction reach the ECS only there) and `WorldEcs.PushInputs` from the `IsFemale`, `IsRunning`, `NotorietyFlag`, `IsDead` and player `InWarMode` setters, `SetSAPoison`, `ClearSteps`, `EnqueueStep`, the player's `Walk` and `ProcessSteps`' step removal. They always run; the setting only picks the reader. A push from an object World no longer holds for its serial is ignored; a new mobile entity starts from the new object's state.
     - **Reader:** `SamplePushed` reads only the animation clock that `World.Update` still advances (server action, frame, frame time, from-server), the mirror history, offsets, alpha and the per-frame view context. On a selection miss, the seat test reuses the known human/mounted/flying/action inputs unless the selection rolled a random action.
     - **Result:** paired (`CUO_MOBILE_INPUT_BENCH`, large scene): sample 37.4 → 10.4 ns per mobile, seat test 58 → 28 ns. About 0.045 ms per frame at zoom 1 (1,611 mobiles drawn) and 0.055 ms at zoom 2.5. Off/on windows were too noisy to show it reliably. The legacy sample was about 17% of the per-mobile cost, not 40%. The rest is the cached emission replay (about 31%), the ECS lookups and the inlined update path.
     - **Checks:** `CUO_MOBILE_INPUT_CHECK` (every pushed sample against the legacy one, every pushed seat test against `TryGetSittingInfo`) and `-ecscheck` (pushed inputs against a fresh computation, `Mounted` against `IsMounted`): 0 differences over 4.5M samples and 3M seat tests (mount, dismount, walking and running mounted, war mode, hidden, hue, frozen, blessed, sex and body changes, seats, gargoyle, flying, death and resurrection, 731 scene mobiles at login).
  - Details: [W5: first slices — active item updates, mobile frame cache, expired-text skip (2026-10-25)](poc-log.md#w5-first-slices--active-item-updates-mobile-frame-cache-expired-text-skip-2026-10-25) and [W5: mobile inputs pushed into the ECS (2026-10-25)](poc-log.md#w5-mobile-inputs-pushed-into-the-ecs-2026-10-25)
- **Next (not done):**
  - The remaining per-mobile cost is the cached emission replay and the ECS access path, not the Mobile reads.
  - The animation clock (server action, frame, frame time, from-server) is still read from the Mobile, because `World.Update`'s mobile loop advances it. **E10 (2026-10-30) measured that moving only the clock reads is worth nothing** (1.3 ns per mobile; the Mobile is read for other inputs anyway); it pays only together with moving the advance. E4's ordering trick is replaced by an owned ordered table (poc-log "E10").
  - **E4 prototype (2026-10-26, `bg/ecs-e4-update`, setting `world_update_mobile_system`, off by default, needs `world_update_active_items`):** the due-time schedule below exists as `MobileClock` (frame and idle due times, steps and handles flags); `World.Update` updates only the mobiles it picks. Saves about 0.02 ms in the scene; the animation timer (interval, repeat state) and the frame advance itself are still the Mobile's. Details: [poc-log "E4"](poc-log.md).
  - That loop (0.035–0.06 ms at 2k mobiles in the scene) would become a system over animating mobiles only with a due-time schedule. That means a push from every write of `LastAnimationChangeTime` and of the idle timer (`SetAnimation` from many packet handlers, `SetIdleAnimation`, `ProcessAnimation`), plus mobiles with steps and handles. Not done: at most about 0.03 ms in the busiest scene, for many more push sites.

### Background backlog (2026-10-25): ECS work for cheaper models

Bounded ECS tasks that a cheaper model (Sonnet, Haiku, Codex gpt-6-luna) can do on its own branch in the background, for a stronger model to review and merge later. Each task: its own worktree branch named `bg/ecs-<task>`, a dated poc-log entry, no merge into `poc/modern-client`; client runs only under the playbook's locks and `bench.lock`. "Blocked" means another agent is working in those files right now.

| # | Task | Tier | Needs the client? | State |
|---|---|---|---|---|
| E1 | **Done (73d18d909): 112 cases.** **Unit tests for the ECS core:** `WorldMirror` (entity create/remove, serial reuse, item↔mobile re-key, equipment index), `EquipmentSlots` rebuild (layer order, PaperdollOrder, skips, mount), `AnimationGroupRules`/`AnimationTiming` edge cases, `ItemSpriteExtract` hue rules, `MobileInputRules`. Tests only, no product changes; any bug found is reported, not fixed | Haiku or Sonnet | No | Unblocked |
| E2 | **Done (e1a1511d2): `tests/ClassicUO.UnitTests/Ecs/EcsPacketCostBench.cs` (poc-log 2026-10-26). 0.02-0.26 us per packet, 0 bytes allocated by the mirror, flat from 500 to 8,000 mobiles.** **Mirror and push cost under packet storms:** a micro-benchmark (tests project or a bench console) of `WorldEcs.Sync`/`PushInputs` and the mirror's dirty tagging per packet type, e.g. 2,000 mobiles moving and changing equipment. Report µs per packet and allocations | Sonnet | No | Unblocked |
| E3 | **Done (c4870e7a4): plan entry `ecsdump:<hex serial>\|player\|nearest`, `EcsInspector.cs`.** **ECS inspector for debugging:** a DevBench command (behind `CUO_DEVTOOLS`) that dumps one serial's ECS entity (all components, `EquipmentSlots` with serials, inputs, animation state) next to the legacy object's values, to the log | Sonnet | One short smoke run | Unblocked |
| E4 | **`World.Update`'s mobile loop as a due-time system, plus the animation clock in the ECS** (W5 leftover; owner: deferred, not dropped). Prototype behind a setting, with a checker that compares it with the legacy loop every frame (like `CUO_MOBILE_INPUT_CHECK`). Draft quality is fine; it needs an audit before any merge | Sonnet (gpt-6-luna as a second opinion) | Yes, parity runs | Prototype done on bg/ecs-e4-update, setting `world_update_mobile_system` (off by default): `MobileClock` component (frame and idle due times, steps and handles flags) picks the due mobiles (about 4% of 1,985 per frame in the scene); the legacy `Mobile.Update` still does the work and the Mobile stays authoritative. `world_mobiles` 0.038 -> 0.020 ms at zoom 1 (-0.02 ms, TieredCompilation=0, in-session pairs). Checker `CUO_MOBILE_UPDATE_CHECK`: 0 differences over 24M clock comparisons and 23M skipped updates (large scene, standing and walking) and the w5push/w5items scripts. Needs an audit; the animation timer itself is not in the ECS yet; see poc-log "E4" |
| E5 | **Emission replay cost** (31% of per-mobile time with push on): profile `MobileFrameCache` replay and the ECS access path, then propose or prototype cheaper replay (e.g. contiguous instance spans, fewer component lookups) | Sonnet | Yes | Done on bg/ecs-e5-replay (cached entity with mirror epoch, in-place update, absolute replay buffer): mobile select+extract -0.05 ms at zoom 1 (208 -> 172 ns per mobile), parity and checkers 0. Audited (Codex P2, P3 fixed); see poc-log "E5" |
| E6 | **Effects from the ECS** (effects still go through the view capture) | Sonnet draft, strong review | Yes | Done on bg/ecs-e6-effects (setting `gpu_prepare_item_cache_effects`, default on). Measured first: capturing an effect is only ~0.09 us, so no ECS extract was built; the cost was that an effect on a tile kept its cached items out of the item cache (1.2-1.4 us per effect in the scene) and that a moving effect re-extracted its chunk's items every tile crossing. Effects are now ignored by the cache and walked alone in tile order on served tiles. Scene, NativeAOT, 3 paired rounds: 230 effects 1.376 -> 0.979 ms (0 effects 0.93-0.98), 54 moving 1.205 -> 1.001, 150 effects at zoom 2.5 1.522 -> 1.234; Britain, bank, Legacy unchanged. Checkers 0 (night and day). Needs an audit; see poc-log "E6" |
| E7 | **W4 leftover:** items on tiles with animated art, corpses or other unbaked objects are still walked | Strong model | Yes | Done on poc/ecs-e7-walk (setting `gpu_prepare_item_cache_mixed`, default on): such tiles are served from the cache and only their uncacheable items (and mobiles) are walked alone, in tile order. Large scene, NativeAOT, 3 paired rounds: Gpu world prepare 1.18 -> 0.87 ms at zoom 1, 1.75 -> 1.13 at zoom 2.5; items walked 2.8k/6.9k -> 0.18k/0.43k; Britain, bank unchanged; Legacy unchanged (large spot 4.40 -> 4.32, noise). Checkers 0. See poc-log "E7" |
| E8 | **Done (86c4a4ceb).** **Docs tidy:** condense the W0–W5 status blocks in this plan into a current-state summary plus links to poc-log, with no content loss | Haiku | No | Unblocked |
| E9 | **Done (ce386955f): seams and 3 test files.** **Test seams for what E1 couldn't reach:** a fake tiledata/equipconv source for `MobileAnimationSystem.Rebuild` (per-layer skips, mounted, hands, light-only items), a stub `Animations` for `AnimationGroupRules.Select`, and `ItemSpriteExtract`'s hue choice pulled out of `Emit` into a pure function. Product changes are seams only (no behaviour change), then tests | Sonnet | No | Unblocked after the Gpu override agent merges (it touches `ItemSpriteExtract`) |
| E10 | **The animation clock into the ECS** (W5 and E4 leftover): server action, frame, frame time and from-server are still read from the Mobile, and the animation timer (interval, repeat state) and the frame advance are still the Mobile's. Move them into ECS state pushed from their write sites (`SetAnimation`, `SetIdleAnimation`, `ProcessAnimation`, packet handlers), with a checker against the legacy values every frame. Also replace E4's use of `Dictionary`'s private version counter with an owned ordered mobile list. Behind a setting; the Mobile stays authoritative until the checker is 0 | Sonnet draft, Codex audit | Yes, parity runs | **Part done on bg/ecs-e10-clock (2026-10-30), needs a Codex audit.** Measured first: the four clock reads cost 1.3 ns per mobile from the Mobile (1.2 ns inside `MobileInputs`, 4.5 ns as a separate component), at most 0.0002 ms per frame in the 2k-mobile scene, and pushing every frame advance would cost more than that, so the clock is **not** moved (write sites listed in poc-log "E10"; it belongs with moving the advance itself, E4's "Left" item 1). Done: `MobileTable` is `World.Mobiles` and reproduces the old dictionary's order exactly (slot array plus LIFO free stack, `Mobile.Ordinal` = slot), replacing the `Dictionary._version` / `UnsafeAccessor` trick; checked against a real dictionary in unit tests (6,000 random operations) and live with a shadow dictionary under `CUO_MOBILE_UPDATE_CHECK` (0 differences; order, clocks and skipped updates 0 over the large scene, w5push and w5items); 582 tests, both builds |
| E11 | **Small ECS hygiene:** guard the ECS test driver (`-ecsdriver`, `EcsSessionDriver`) and its startup switch behind `CUO_DEVTOOLS` (E6 audit); measure and, if safe, remove the double `WorldEcs.Sync` per `SetInWorldTile` (perf-opportunities), using E2's bench. No behaviour change | Sonnet | One smoke run | Started 2026-10-30 |
| E12 | **Same-frame item checker:** `CUO_GPU_ITEM_CHECK` compares the served frame with a later reference frame, so a change between them (an expiring effect) can hide a difference, and it doesn't check effect geometry, blend or depth (E6 audit). Compare both paths against the same frame state and cover effect instances | Sonnet, Codex audit | Yes | Started 2026-10-30 |

Not for cheaper models: anything that changes draw order, the walk, or picking (W2's query half, W6); the parity rules there are subtle and have cost days before.

### W6: remove the legacy walk
- **Owner (2026-10-25): Legacy stays** for cross-platform reach (virtual machines and machines without Vulkan). So W6 needs either Legacy rewritten to draw from the ECS (a deferred project, started only when it blocks other important work) or a later decision to drop Legacy. Until then, keep the ECS extract renderer-neutral, so a Legacy-on-ECS renderer can consume it.
- Prerequisites:
  - picking, lights, foliage and dynamics have moved (W1–W3);
  - Legacy rendering is gone (SDL_GPU becomes a hard requirement after graphics-ownership Tier 3).
- Then delete `MeshLayer`/`ChunkMesh`, the Legacy parts of `AddTileToRenderList`, and the views' `Draw` bodies.

## Order

1. W0 first.
2. W1 in parallel with graphics-ownership Tier 1; they touch different files.
3. W2 and W3 after W1.
4. W4 only if needed.
5. W5 alongside, driven by crowd tests.
6. W6 last.

## Risks

| Risk | Mitigation |
|---|---|
| Picking regressions when the full walk goes | Targeted picking keeps `PixelPicker` and its pixel data; add a picking test per object type (land, stretched land, static, multi, item, mobile, corpse, effect) |
| Animation parity in W3 | Case-by-case port with golden screenshots; keep view capture as a runtime fallback until every case matches |
| Foliage and roof edge cases (houses, multis, customised houses) | Screenshot sets inside and around houses and multis, with the owner's placed house once verified live |
| Change-event bugs, stale `ScreenPos` | The `-ecscheck` consistency checker, extended to screen position and visibility |
