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
- Animation state, light, alpha and screen position are not yet in the ECS.

## Steps

Each step is behind a switch where it changes behaviour, and each must pass this gate:
- picking parity (`CUO_BENCH_PICK`);
- screenshot parity vs the previous step;
- the owner's FPS test;
- 188+ unit tests;
- the ECS consistency check (`-ecscheck`).

### W0: measure inside world prepare
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

### W3: animation state into the ECS; `DynamicSpriteExtract` replaces view capture
- **Components:**
  - `AnimationState`: action, direction, frame, time, mount, layer order;
  - `EquipmentSlots`: a compacted, ordered slot list rebuilt on equipment change, not walked over 23 layers each frame.
- **Systems** advance animation; the extract writes `SpriteInstance`s straight into `WorldRenderer.DynamicBuffer`.
- **Parity:** must match ClassicUO's animation selection exactly. The existing view code is the reference; port its rules case by case (mounts, sitting, gargoyle flying, partial hues, spectral) with screenshot tests per case.
- **Retire** the batcher capture hook for mobiles and items.
- **Effort:** 1–2 weeks. This is the largest step and the one ClassicUO 2.0 took with TinyEcs.

### W4: ground-item chunk baking (only if W0–W3 numbers ask for it)
- Bake rarely-changing ground items into the chunk instance buffer and re-bake the chunk on ECS change events. Mobiles stay per frame.
- **Effort:** 2–3 days. Counterpart of ClassicUO 2.0's `BakeChunkDynamics`.

### W5: the ECS becomes authoritative for the world update
- **Systems replace the per-object `Update` loops** in `World.Update`:
  - skip contained items;
  - keep active lists for animating items and mobiles;
  - compute HP percentage only on change.
- **The `perf-opportunities.md` update-path items get fixed here:** `WMapManager` per frame, `RootContainer`, LINQ `Reverse`, the 0x78 re-allocation, `ConfirmWalk` re-inserts.
- **Effort:** 1–2 weeks, piece by piece. Driven by the crowd numbers (the update loop is about 1 µs on the sparse shard, so it matters only in busy scenes).

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
