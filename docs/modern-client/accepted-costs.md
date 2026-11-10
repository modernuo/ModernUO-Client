# Accepted costs ledger

Every change merged with a measured or known cost in time or memory, kept for the final performance audit at the end of the PoC. Each cost is small on its own; added together they could lower the frame rate.

The owner's rule (2026-11-02): a cost can be fine when it buys a feature, correctness or the ECS architecture. It still has to be listed here, so the final audit can check each area and decide whether to revisit it. Agents and the coordinator add a row whenever a merge accepts a cost, including "within budget" ones, and mark a row **Revisited** when a later change removes it.

Columns: what costs, how much (measured or estimated, with the scene), what it buys, where to look first if it has to go.

## Frame time

| Merge | Cost | Buys | Revisit idea |
|---|---|---|---|
| T7b part 1 (extract split) | +24 ns per captured item in `Gather` (ECS read; micro-bench, 201 rounds): about 1 % of Gpu world_prepare at the large scene, zoom 2.5; negligible at Britain. Quiet timing parked. | Extract reads the ECS, not the GameObject: a prerequisite for deleting the caches in T9 | **Partly revisited (02c0ef387):** EcsRead halves it (30–40 → 16–22 ns per item over the old field reads) |
| T4 (thin handles) | An ECS read is 9–25 ns against 1–5 ns from the cached field. Hot fields stay cached; cold fields (e.g. a status bar's 4 reads, +23–37 ns) read the ECS at UI rates. | One source of truth for world state | Same helper; keep hot fields cached until then |
| Friflo `GetComponent` (systemic) | 6–9 ns per single read in 4.0.0-preview.2 (virtual heap); the "25 ns" was inside a larger record read. **Partly revisited (02c0ef387):** the hottest per-frame reads use `EcsRead` (3.8–5.3 ns; five reads through a row 24 → 12.6 ns). `WorldMirror`/`WorldWriter` still call Friflo. | — | A stable Friflo release (main has no virtual heap): see docs/dev/ecs-friflo.md |
| T9 child-list slice B fix chain | Attach-time walks: `WouldCycle` (parent chain) and `SubtreeDeeperThan` (moved subtree) on each attach packet; `WorldMirror.ParentOf` probes up to 3 components per level. Packets only, nothing per frame. | No server packet can loop the container graph or crash the client | Cache parent and subtree height per record |

## Memory

| Merge | Cost | Buys | Revisit idea |
|---|---|---|---|
| T4 (thin handles) | About +32 B per Item (the 16-byte Friflo handle plus fields): about 0.8 MB per client at 25k items | Handle-based ECS access | Shrinks once T9 deletes the cached fields |
| T6b (tile index) | `TNext`/`TPrevious` 16 B per item, mobile and effect, now always null | — (left over) | Move the links to a list-node base for Land/Static/Multi, or a side table |
| T9 child-list slice B | `LinkedObject` fields on Item/Mobile unused: 24 B per object, about 2–5 MB per client | — (left over) | **Revisited (linked-object, eb0f70ed0):** `LinkedObject` moved off `BaseGameObject`; about 1.5 MB per client saved (estimated) |
| EcsRead (02c0ef387) | Depends on Friflo 4.0.0-preview.2 internals (a field offset, `[UnsafeAccessor]` names) until the upgrade; the fallback branch costs +0.25 ns per row read at 25k entities; a thread-static scratch copy per special heap type per thread (none today) | Faster ECS reads | Delete or thin the helper at the Friflo upgrade |
| perf-11 | Corpse layer tables: 8 × `Item[256]`, about 17 KB per client; can keep destroyed corpse items alive until reused | Corpse layer reads without a scan (about 500 scans → 0 per frame in the corpse scene) | Cache serials instead of Items, or clear on destroy |

For pools, caches, prefill and atlases with their MB cost, see [memory-footprint.md](memory-footprint.md).

## 2026-11-07

| Change | Cost | Buys | Revisit |
|---|---|---|---|
| tile-head (1150d9b3a) | ~544 B per chunk (`_heads`, 64 refs), ≤ ~0.3 MB per client with the 300-chunk pool | O(1) tile-list head; the `TPrevious` walk was 7–8 % of Legacy britain_z samples | quiet timing parked (`measure/tile-head/`) |
| tile-index-2 (8b1f355cd) | 8 B per chunk index (occupied-cell mask); one OR/AND-NOT per placement | one bit test per walked tile instead of a call + loads | quiet timing parked (`measure/tile-index-2/`) |
| ecs-effects-own (endpoints, default off) | ~40 B per live effect (< 0.05 MB); with the switch on ~10–15 ns per endpoint read, ~25 ns per drawn effect with a source, a walk of live effects per object destroy, ~1–2 ns Serial+kind check | effects reference ECS records, not objects | phase 2 folds reads into one step per effect |
| ecs-item-offset (default off) | 12 B per item that ever rode a moving boat; ~4–5 ns per drawn item with the switch on | item offset owned by the ECS | read from the row Gather already holds |
| decode-stall (b-merge pending push) | two `SetThreadPriority` calls per blocking decode wait | 2 ms wait under load ~500 ms → ~4 ms p50 | — |

## 2026-11-08

| Change | Cost | Buys | Revisit |
|---|---|---|---|
| ecs-active-items (3d5bc9a90; `ecs_active_items_source`, default off) | item handle table and free list: 64 entries each to start, grown to the peak listed count (a few KB); one extra array read per entry in `Rebuild` when the switch is on | entries hold int handles, so removed items are not kept reachable | quiet timing parked |
| ecs-wave1c (3695805e2; `ecs_small_fields`, default off) | switch on: ~10–25 ns per getter read (estimated; e.g. corpse extract `UsedLayer` per corpse, the light pass `LightID` per light item); switch off: one extra test per getter; memory < 0.1 MB per client | small Class A fields (item LightID/IsDamageable/UsedLayer, mobile Race/SpeedMode/IsFemale/IsRenamable, player InWarMode) owned by the ECS | quiet timing never run per switch; covered by the after-T9 pass; derive `MobileInputs`' copies from the record once the caches go |
| perf-alloc-sites-2 (5c6b682a7) | reused text pixel buffer, up to 1 MB per client (game thread only) | text rasterizing no longer allocates a `uint[]` per string (steady main thread 40 → 0 B/frame p50; ls_walk allocation about halved) | shrink after a burst, or rasterize straight into the atlas |
| worldmap-texture (398510734) | none new: peak while a facet map loads is still ~235 MB per client (decoded image + texture); the texture is now freed on close (saves ~117.5 MB after closing). Codex P3 accepted: the `Log.Info` at WorldMapGump.cs:1563 sits before the ownership transfer, so a throwing logger would leak the new texture until finalization | no off-thread GPU create/dispose; fixes the reopen/close crash | move the log inside the cleanup `try` at the next touch |
| perf-alloc-5 | two `PathObject` lists kept at their peak size (pathfinder) | refused walks no longer allocate (handles-walk 420/464 → 4 B/frame) | trim after a long path |
| quiet-1108: gradual ECS dual-path cost (tip 957237f06 vs b 34fec0e52, before T5) | Gpu large scene: frame about +0.07–0.14 ms p50 (+6–8 %), mostly world_prepare (+7–9 %); small scenes equal. Legacy: world_prepare +0.05–0.27 ms and update +0.004–0.03 ms in the large scene, frame equal or better (draw got cheaper); a..tip also added scene_draw_ui +0.04 ms and fx text +0.024 ms at `ls_z25`. Bisected in two passes: small steps of +0.02–0.05 ms, no single culprit | the ECS conversion running beside the old paths (switches and checkers) | **re-measure after T9** with the switches settled (one full timing pass; owner, 2026-11-08) |
| legacy-sprites-cost (e079d5368; `legacy_ecs_sprites`, default off) | memory with the switch on: one `LegacyEntry[]` per drawn mobile in its `MobileFrameCache`, ~56 B × 12–24 calls (~1 KB), so ~2 MB per client at ~2,000 mobiles drawn, allocated when a mobile is first drawn (corpses window: one 67–120 KB frame per window, 19 B/frame average, p50 0); miss frames copy the calls (~0.1 µs per mobile on ≤ 10 % of frames). Codex P3 accepted: turning the switch off at runtime keeps surviving mobiles' peak-sized arrays until they leave (dev-only toggle, bounded per mobile). Nothing with the switch off | Legacy mobiles replay their batcher calls (~90 % of draws at the large scene) instead of select + resolve; expected 0.4–1 ms of the 1.38 ms ls_z25 world_draw rise (timing parked) | share entries per (body, equipment, frame); free the arrays when the switch goes off |
| tile-index-flip (d54ab5109; `world_tile_index` now default on) | Legacy world_prepare +4–6 % at ls_z1 (quiet-1108, bench-000416..421); Legacy frame and Gpu neutral. Memory: none new; the flip removes the "index kept for effects while off" overlap (~0.5–1.5 MB) | items, mobiles and effects live only in the ECS-maintained tile index; T9 can drop `TNext`/`TPrevious` from dynamics | re-time in the after-T9 pass; the Legacy walk's merge of list and index |

## 2026-11-09

ecs-flip-8 (ca89426bc) and ecs-flip-8-sprites (14efa571a) turned eight switches on by default. Their costs were accepted when each was built (default off, rows above where they existed); from now they apply to every client. None was timed per switch: the arms are in `measure/ecs-flip-8/job-timing-*-PARKED.sh` for the after-T9 pass.

| Change | Cost | Buys | Revisit |
|---|---|---|---|
| `ecs_item_offset` on (187b39519) | a handle check and `EcsRead.TryGet` (~4–5 ns) per drawn item per frame in `Gather` and per item pick; 12 B per item that ever rode a moving boat | the item's boat offset owned by the ECS | **partly revisited** (legacy-sprites-items, b607aa19a): `ItemOffsets.ReadRow` reads from the row `Gather` already holds |
| `ecs_boat_steps` on (48f7290f3) | per frame, a key snapshot and a lookup per boat that ever queued a step; ~90 B per moved boat plus 16 B per passenger slot | boat motion as an ECS component and system | after-T9 timing pass |
| `ecs_small_fields` on (cc163f59b) | an ECS read (~10–25 ns) instead of a field load for `UsedLayer` per corpse, `LightID` per light item and the player race per static at chunk load; `MobileTraits` 3 B and `ItemTraits` 2 B per record (< 0.1 MB at the large scene) | the small Class A fields owned by the ECS | after-T9 timing pass; derive `MobileInputs`' copies from the record (T9) |
| `ecs_active_items_source` on (8e53a5453) | an entity create/delete per list add/remove, a per-frame query count and an O(listed) scatter; Item +4 B and a handle table of a few KB | the active item list from the ECS, entries that do not keep removed items reachable | after-T9 timing pass |
| `ecs_step_mount_inputs` on (250070441) | one component read per step decision; a slot rebuild after an equipment change (no figure measured) | mount state for steps from the ECS | — |
| `ecs_effect_endpoints` on (f1774c07f) | ~40 B per live effect; ~10–15 ns per endpoint read; every object destroy walks the live effects while any exist | effects reference ECS records, not objects | after-T9 timing pass; the destroy walk if many effects live at once |
| `ecs_effect_motion` on (d43602c52) | +8 B per live effect; one player record read per effects pass (fewer ECS reads than endpoints alone) | effect motion as `EffectSystem` steps | — |
| `ecs_effect_sprites` on (bc60ed53d) | per drawn effect: one `EffectState` get, a tiledata read and the source lookup the view also does, instead of the view's draw (estimated); one `EffectSpriteExtract` and a 16-entry checker buffer per client | both renderers draw effects from the extract over `EffectState` | delete `GameEffectView`'s draw once `legacy_ecs_sprites` is on |
| t9-world-index slice 1 (a2ad0b43b; `world_entity_index`, default off) | the mirror's handle table, 8 B per entity id, always maintained (32 KB initial, ~0.1–0.4 MB at a large scene's peak id); one array store per mirror upsert in both switch states | `World.Get`/`GetItem`/`GetMobile` from the ECS serial index; the step towards deleting the `Items`/`Mobiles` tables | weigh against the `Items`/`Mobiles` tables' memory once they are deleted (later slices) |

## 2026-11-09 and 2026-11-10

| Change | Cost | Buys | Revisit |
|---|---|---|---|
| perf-cpu-1109 (456bb0606; `ItemTable` held flag, no switch) | three fields per `Item` (a reference, a uint, a bool): ~16 B per item with padding, ~0.3 MB per client at 20k items; `Items.Add`/`Remove` write two fields more; `Clear` walks the items once (logout only) | `World.Holds` for an item is a field compare, not a dictionary lookup, in the active item loop and the destroyed-item sweep | goes with the item dictionary in world index slice 5 |
| t9-world-index slice 3 part 2 (19e61bf18; `world_entity_index`, default off) | `Mobile.TableKey`: 4 B per mobile (< 0.02 MB at 2k mobiles); one reusable `List<Item>` in `WorldWriter` for re-keys; with the switch off each converted reader and `Holds` pay one or two flag tests | `World.Holds` from the tables' marks, no dictionary lookup per identity guard | goes with the dictionaries in slice 5 |
| t9-world-index slice 4 (2cfd8b244; `world_entity_index`, default off) | the ordered item table: ~12 B per slot (8 B `Item[]` + 4 B key array, grown by doubling, never shrunk until `Clear`) beside the dictionary, plus `Item.Ordinal` 4 B per item: ~0.5 MB at 25k items; a few more stores per Add/Remove | the update order carried by slots, so the item dictionary can go | slice 5: delete the dictionary walk, or keep the dictionary only as World's serial key |

