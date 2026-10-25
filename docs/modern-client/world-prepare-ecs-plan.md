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
- It mirrors `World` with 0 mismatches. `DynamicSpriteExtract` exists, but the GPU path doesn't use it yet; `WorldRenderer.DynamicBuffer` is where it would plug in.
- Mobile animation state is in the ECS (W3 groundwork, below); light, item/corpse sprites and screen position are not yet.

## Steps

Each step is behind a switch where it changes behaviour, and each must pass this gate:
- picking parity (`CUO_BENCH_PICK`);
- screenshot parity vs the previous step;
- the owner's FPS test;
- 188+ unit tests;
- the ECS consistency check (`-ecscheck`).

### W0: measure inside world prepare
**Status: done (2026-10-24).** Ranked table in classicuo-perf-baseline.md ("W0"); method in testing/world-prepare.md. At 4K zoomed out the tile walk is 76% and the Gpu hidden collect 17%. More than half of the walk is just visiting the objects (a bare list walk costs 2.2–2.4 ms), so baked tiles must not be visited at all. Nothing in world prepare is SIMD-friendly: it is linked lists and branchy per-object code.
- **Sub-phase timings** in `FrameStats`:
  - tile walk over land and statics;
  - dynamic objects;
  - alpha and foliage rules;
  - picking;
  - light gathering;
  - Gpu override collection;
  - view capture (split by item, mobile and effect);
  - render-list push.
- **Counts per frame:** tiles visited, objects visited, overrides, captures.
- **Spots:** Britain, the bank, zoomed out, 4K zoomed out, and the 120-NPC crowd.
- **Output:** a ranked table added to classicuo-perf-baseline.md. It decides the order of W1–W3.
- **Effort:** about 1 day.

### W1: stop walking baked land and statics every frame (Gpu mode)
**Status: core done (2026-10-24), setting `gpu_prepare_skip_baked`, default on.** Implemented in GameSceneGpuPrepare.cs:
- per-chunk tile masks: only tiles with non-baked objects or per-frame duties are walked;
- targeted picking: the tiles under the mouse are walked in full;
- screen-edge tiles get a replay of the walk's culling;
- a full walk for 12 alpha steps after max-Z, roof, season or fading changes and for new chunks, then alphas snap to their settled values;
- season statics are baked (`WorldRenderer.SetStaticRemap`).

4K zoomed out (JIT): world prepare 4.86 → 0.73 ms, 179 → 631 fps. Pixel and picking parity hold (classicuo-perf-baseline.md "W1").

Still open from the table below:
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

- **Result:** in Gpu mode the full tile walk runs only for dynamic objects.
- **Expected:** zoomed out at 4K, world prepare 3.6 ms → well under 1 ms (an estimate; W0 confirms the split).
- **Effort:** 3–5 days.

### W2: dynamic objects from ECS queries instead of tile lists
- **Components:**
  - `ScreenPos`, recomputed only on `Position` change or a camera move (Friflo change events, or a dirty tag);
  - `VisibleInView`, set by a chunk-range query.
- **Iteration:** ground items and mobiles iterate an ECS query over in-view entities, not every tile.
- **Capture stays at first:** item and mobile views still `Draw` under capture, but only for the entities the query returns.
- **Effort:** 3–4 days.
- **Status (2026-10-25, large-scale agent): the screen-position half is done; the ECS query is set aside.** Measured in the large-scale scene ([testing/large-scale.md](testing/large-scale.md): 1,984 mobiles and 19,476 ground items in range). Details in poc-log ("Large-scale scene").
  - **Screen position only on change:** a camera generation (bumped on each `UpdateDrawPosition`) replaces the per-frame recompute of every walked object in Gpu mode. Walk 0.84 → 0.65 ms in the scene. `CUO_PREP_CHECK=1` verifies it.
  - **Item picking:** a rectangle test before the art lookup (0.29 → 0.11 ms in the scene).
  - **Why the query waits:** the walk now costs about 31 ns per dynamic object, and the query would replace only the 9 ns visit. The rules (alpha and roof fades, the tile's max-Z cut-off, surface-overhead, behind-a-tree, handles) still read the GameObject, and the tile order sets both the max-Z cut-off and the equal-depth painter order the Gpu path reproduces. About 0.15 ms at 21k objects, nothing at normal scale, for a large parity risk. W4 is the bigger win (below).

### W3: animation state into the ECS; `DynamicSpriteExtract` replaces view capture
- **Components:**
  - `AnimationState`: action, direction, frame, time, mount, layer order;
  - `EquipmentSlots`: a compacted, ordered slot list rebuilt on equipment change, not walked over 23 layers each frame.
- **Systems** advance animation; the extract writes `SpriteInstance`s straight into `WorldRenderer.DynamicBuffer`.
- **Parity:** must match ClassicUO's animation selection exactly. The existing view code is the reference; port its rules case by case (mounts, sitting, gargoyle flying, partial hues, spectral) with screenshot tests per case.
- **Retire** the batcher capture hook for mobiles and items.
- **Effort:** 1–2 weeks. This is the largest step and the one ClassicUO 2.0 took with TinyEcs.
- **Status (2026-10-25): groundwork done for mobiles, parity proven; not yet the renderer's source.** Details in poc-log ("W3 groundwork"), test procedure in [testing/ecs-animation.md](testing/ecs-animation.md).
  - **Components:** `AnimationState`, `MobileRenderState` (override hue, alpha, step offset) and `EquipmentSlots` (rebuilt only on an `EquipmentDirty` tag that the mirror sets on equip, unequip, worn-item or body changes).
  - **Systems:** `MobileAnimationSystem`, then `MobileSpriteExtract`, which writes `SpriteInstance`s from the pack tables with no atlas and no batcher.
  - **Shared rules:** action selection (`AnimationGroupRules`, moved out of Mobile) and the frame step (`AnimationTiming`). They are shared with the legacy Mobile, not duplicated.
  - **Parity:** 0 mismatches against the capture in every case the harness drives: bank, walking/running, 120-NPC crowd, mounted humans, equipment changes, cloak facings, partial and spectral hues, hidden, gargoyle (both sexes, flying, per-layer actions), sitting, dead. JIT and NativeAOT.
  - **Cost:** the extract is 0.33 µs per mobile vs 1.5 µs for the capture.
- **Status (2026-10-25, later): the extract is the Gpu world's source for mobiles and art items** (setting `gpu_ecs_sprites`, default on; off = the capture). Details in poc-log ("W3: the ECS extract is the Gpu world's source").
  - Mobiles: the view's side effects are reproduced (FrameInfo, mirror bit, worn lights, aura). Art items: `ItemSpriteExtract` (hue rules, stack, animated art, light, picking offset).
  - Parity with `CUO_ANIM_SYNC=1`: 0 mismatches in bank, items, lights and 120-NPC crowd runs (5.2M mobile, 1.5M item comparisons), including the side effects. Screenshots and picking unchanged.
  - Crowd: mobile capture 104 → 45 µs/frame, world prepare 169 → 109 µs. Bank: mobiles 23 → 10 µs, items 6.7 → 2.0 µs.
  - **Next:**
    1. ~~Corpses and item multis (still captured), then retire the capture hook.~~ Done 2026-10-25 (integration agent): `ItemSpriteExtract` draws every world item, so the capture runs on the default path only for effects, statics/multi components the walk captures and shadow-only draws; `gpu_ecs_sprites off` keeps it for everything. 0 parity mismatches over about 900k corpse and 260k multi comparisons.
    2. Move the sampled inputs (steps, server action, war mode, item hue state) to the ECS with W5.

### W4: ground-item chunk baking (only if W0–W3 numbers ask for it)
- **The numbers ask for it (2026-10-25):** in the large-scale scene (Gpu, zoom 1) world prepare is 1.92 ms, of which the items' share of the walk is about 0.6 ms and the item extract 0.42 ms. Mobiles: 0.59 ms (extract) plus their walk share.
- **Suggested shape:** W1's skip machinery for settled ground items. Cache each chunk's item instances (from `ItemSpriteExtract`), re-extract a chunk only on an item change in it, and leave its tiles out of the walk masks. Keep in the walk: animated art, foliage items (behind-a-tree), corpses, multis, items with text or handles, the tiles under the mouse (picking), and everything during fades (W1's full-walk steps). Stamp `DrawnFrame` for cached items (overhead text and bars use it).
- Bake rarely-changing ground items into the chunk instance buffer and re-bake the chunk on ECS change events. Mobiles stay per frame.
- **Effort:** 2–3 days. Counterpart of ClassicUO 2.0's `BakeChunkDynamics`.
- **Status (2026-10-25, W4 agent): done, setting `gpu_prepare_item_cache`, default on; off = the old path.** Code: `GameSceneGpuItemCache.cs`. Details in poc-log ("W4: ground-item chunk cache"), procedure in [testing/world-prepare.md](testing/world-prepare.md).
  - **Cache:** per chunk (`Chunk.GpuItems`), the walk-order instances of every tile whose unbaked objects are only plain settled ground items (and mobiles), in world pixels so the camera doesn't matter. The served tiles leave the walk masks; a served tile's mobiles are walked alone.
  - **Invalidation:** objects joining or leaving a tile (`GameObject.TileChunk` → `Chunk.OnTileContentChanged`; mobiles don't re-extract), a per-item signature checked every frame (graphic, hue, amount, flags, position, alpha, text, handles, offset, tile), full-walk steps, a new GPU world pass, the setting and the dead-gray tint. It does not hook the ECS change events: the signature catches what they would, and item updates from the server always re-add the item to its tile.
  - **Kept in the walk:** animated, foliage, translucent, roof and internal items; corpses; multis; items with text or handles, still fading or above the max Z / ground Z; tiles with anything else unbaked; the tiles whose item art box is under the mouse; the selected container's tile; every tile during fades; all items while object handles show.
  - **Order:** served runs join the capture at the statics-list position where the walk would have queued them, so the dynamic buffer and equal-depth ties are unchanged; their lights are added at the same point. `DrawnFrame` is stamped.
  - **Result (scene, Gpu, alternated):** world prepare 1.67/2.01 → 1.32/1.45 ms at zoom 1, 2.19/2.53 → 1.80/1.97 at zoom 2.5, walking 1.46/1.78 → 1.25/1.44. Normal spots 2–12% less. `CUO_GPU_ITEM_CHECK`: 0 differences over 110M+ served-item comparisons.
  - **Left:** items on tiles with animated art, corpses or other unbaked objects (2.9k of 14.2k in the scene at zoom 1) are still walked and extracted; mobiles (0.55 ms extract in the scene) are W5's.

### W5: the ECS becomes authoritative for the world update
- **Systems replace the per-object `Update` loops** in `World.Update`:
  - skip contained items;
  - keep active lists for animating items and mobiles;
  - compute HP percentage only on change.
- **The `perf-opportunities.md` update-path items get fixed here:** `WMapManager` per frame, `RootContainer`, LINQ `Reverse`, the 0x78 re-allocation, `ConfirmWalk` re-inserts.
- **Effort:** 1–2 weeks, piece by piece. Driven by the crowd numbers (the update loop is about 1 µs on the sparse shard, so it matters only in busy scenes).
- **Status (2026-10-25): first slices and the pushed mobile inputs done, each behind a setting (default on).** Details in poc-log ("W5: first slices"), procedure in [testing/world-update.md](testing/world-update.md). In the large-scale scene (Gpu, in-session off/on pairs): world prepare 1.66 → 1.17–1.25 ms at zoom 1, 2.35–2.48 → 1.80 at zoom 2.5, 1.79–1.86 → 1.27–1.48 walking; Update 0.45 → 0.17–0.19 ms; frame 2.87 → 2.12–2.20 ms.
  1. **`world_update_active_items`:** `World.Update` visits only items whose `Update` does something (corpses, items whose handles just opened) plus the 50 ms range sweep; destroyed items leave `Items` via `Entity.Destroy`; the hits percentage is computed in the `Hits`/`HitsMax` setters. Item loop 0.28–0.33 → 0.015–0.02 ms (25,120 items).
  2. **`gpu_mobile_frame_cache`** (`MobileFrameCache`): selection and sprite instances reused while the sampled inputs, equipment version, seat tile, alpha and hues are unchanged; position, walking offset and depth reapplied. Mobile select + extract 0.71 → 0.35–0.37 ms at zoom 1 (1,611 mobiles, about 0.22 µs each), 1.01–1.05 → 0.54 at zoom 2.5.
  3. **`text_skip_expired`:** the overhead text pass skips mobiles and corpses whose texts all expired. 0.18 → 0.04 ms.
  4. **Update-path items:** `WMapManager` asked once per frame (done); LINQ `Reverse` in `FindNext` (done; per keypress, not per frame); `RootContainer` (only on facet change and O(n · depth) with depth 1–3: not a real problem); the 0x78 equipment re-creation and the `ConfirmWalk` re-insert (per packet or step, not per frame; changing them risks tooltip and equal-depth draw-order behaviour for no frame-time gain: left).
  5. **`mobile_push_inputs`** (2026-10-25, push-inputs agent; details in poc-log, "W5: mobile inputs pushed into the ECS"). A mobile's slow-changing inputs are now ECS state that their mutation sites push, instead of being read from the legacy Mobile every frame:
     - **`MobileInputs`** (new component, on every mobile entity): war mode, running, flying, dead, hidden, female, human/gargoyle body flags, has-steps and step-run, the animation body, the facing (`ProcessSteps`' rule), notoriety, the poisoned/paralyzed/yellow-hits/invulnerable bits, last step time and walking delay. `MobileInputRules.Compute` (the Mobile's own rules) runs on every push.
     - **`EquipmentSlots.Mounted`**: `Mobile.IsMounted`, computed when the slots are rebuilt (the mirror marks them dirty on every equip, unequip, worn-item change or removal).
     - **Pushes:** `WorldEcs.Sync` (graphic, flags and direction reach the ECS only there) and `WorldEcs.PushInputs` from the `IsFemale`, `IsRunning`, `NotorietyFlag`, `IsDead` and player `InWarMode` setters, `SetSAPoison`, `ClearSteps`, `EnqueueStep`, the player's `Walk` and `ProcessSteps`' step removal. They always run; the setting only picks the reader. A push from an object World no longer holds for its serial is ignored, and a new mobile entity starts from the new object's state.
     - **Reader:** `SamplePushed` reads only the animation clock that `World.Update` still advances (server action, frame, frame time, from-server), the mirror history, offsets, alpha and the per-frame view context. On a selection miss, the seat test reuses the known human/mounted/flying/action inputs unless the selection rolled a random action.
     - **Result:** paired in-session measurement (`CUO_MOBILE_INPUT_BENCH`, large scene): sample 37.4 → 10.4 ns per mobile, seat test 58 → 28 ns. That is about 0.045 ms per frame at zoom 1 (1,611 mobiles drawn) and 0.055 ms at zoom 2.5. Off/on windows were too noisy that day to show it reliably (another agent's client was running). The legacy sample was about 17% of the per-mobile cost, not 40%. The rest is the cached emission replay (about 31%), the ECS lookups and the inlined update path.
     - **Checks:** `CUO_MOBILE_INPUT_CHECK` (every pushed sample against the legacy one, every pushed seat test against `TryGetSittingInfo`) and `-ecscheck` (pushed inputs against a fresh computation, `Mounted` against `IsMounted`): 0 differences over 4.5M samples and 3M seat tests (mount, dismount, walking and running mounted, war mode, hidden, hue, frozen, blessed, sex and body changes, seats, gargoyle, flying, death and resurrection, 731 scene mobiles at login).
  - **Next:**
    - The remaining per-mobile cost is the cached emission replay and the ECS access path, not the Mobile reads.
    - The animation clock (server action, frame, frame time, from-server) is still read from the Mobile, because `World.Update`'s mobile loop advances it.
    - That loop (0.035–0.06 ms at 2k mobiles in the scene) would become a system over animating mobiles only with a due-time schedule. That means a push from every write of `LastAnimationChangeTime` and of the idle timer (`SetAnimation` from many packet handlers, `SetIdleAnimation`, `ProcessAnimation`), plus mobiles with steps and handles. Not done: at most about 0.03 ms in the busiest scene, for many more push sites.

### W6: remove the legacy walk
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
