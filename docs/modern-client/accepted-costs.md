# Accepted costs ledger

Every merged change with a measured or known cost in time or memory, kept for the final performance audit. Rule (owner, 2026-11-02): a cost can be fine when it buys a feature, correctness or the ECS architecture, but it is listed here, even at ~1 %. Add a row at every merge that accepts a cost; mark it **Revisited** when a later change removes it. Pools, caches, prefill and atlases with MB figures are also in [memory-footprint.md](memory-footprint.md).

Columns: change (commit), cost, what it buys, revisit idea. "After-T9 pass" means the single full timing pass planned once T9 settles the switches (per-switch timing was never run).

## Frame time

| Change | Cost | Buys | Revisit |
|---|---|---|---|
| Gradual ECS dual-path cost (quiet-1108, tip 957237f06 vs b 34fec0e52) | Gpu large scene frame +0.07-0.14 ms p50 (+6-8 %), mostly world_prepare (+7-9 %), small scenes equal. Legacy world_prepare +0.05-0.27 ms, update +0.004-0.03 ms, frame equal or better; scene_draw_ui +0.04, fx text +0.024 at `ls_z25`. Two bisect passes: steps of +0.02-0.05 ms, no single culprit | ECS conversion running beside the old paths | re-measure after T9 (owner, 2026-11-08) |
| Legacy world_prepare regression (3b2d360e5, de26150aa) | ~0.65 ms (+20 %) at the large spot remains (3.96 vs 3.29 ms NativeAOT), spread over many small additions to the shared tile walk; Legacy world draw +0.25 ms | - | a Legacy-only walk (~450 duplicated lines), with the Legacy-on-ECS project |
| `world_tile_index` default on (d54ab5109) | Legacy world_prepare +4-6 % at ls_z1 (bench-000416..421); Gpu and Legacy frame neutral | items, mobiles, effects only in the ECS-maintained tile index | after-T9 pass; Legacy walk's merge of list and index |
| T7b part 1 (extract split) | +24 ns per captured item in `Gather` (~1 % of Gpu world_prepare, large scene zoom 2.5) | extract reads the ECS, prerequisite for deleting caches | partly revisited (02c0ef387): `EcsRead` 30-40 to 16-22 ns |
| T4 thin handles | ECS read 9-25 ns vs 1-5 ns cached field; cold fields (status bar: 4 reads, +23-37 ns) read the ECS | one source of truth | keep hot fields cached until T9 |
| Friflo `GetComponent` (systemic) | 6-9 ns per read (4.0.0-preview.2); hottest reads use `EcsRead` (3.8-5.3 ns); `WorldMirror`/`WorldWriter` still call Friflo | - | stable Friflo release (docs/dev/ecs-friflo.md) |
| `ecs_small_fields` on (cc163f59b) | ~10-25 ns per getter read (corpse `UsedLayer`, light `LightID`, player race per static at chunk load); off: one test per getter | small Class A fields owned by the ECS | after-T9 pass; derive `MobileInputs` copies from the record (T9) |
| `ecs_item_offset` on (187b39519) | handle check + `EcsRead.TryGet` (~4-5 ns) per drawn item and per item pick | item boat offset owned by the ECS | partly revisited (b607aa19a `ReadRow`); open: "has offset" flag (perf-opportunities) |
| `ecs_boat_steps` on (48f7290f3) | per frame a key snapshot and a lookup per boat that ever queued a step | boat motion as component + system | after-T9 pass |
| `ecs_active_items_source` on (8e53a5453) | entity create/delete per list add/remove, per-frame query count, O(listed) scatter | active item list from the ECS | after-T9 pass |
| `ecs_step_mount_inputs` on (250070441) | one component read per step decision; slot rebuild after equipment change (not measured) | mount state for steps from the ECS | - |
| `ecs_effect_endpoints` on (f1774c07f) | ~10-15 ns per endpoint read; every object destroy walks live effects while any exist | effects reference ECS records | after-T9 pass; the destroy walk if many effects live |
| `ecs_effect_motion` on (d43602c52) | one player record read per effects pass | effect motion as `EffectSystem` steps | - |
| `ecs_effect_sprites` on (bc60ed53d) | per drawn effect: `EffectState` get, tiledata read, source lookup (estimated) | both renderers draw effects from the extract | delete `GameEffectView` draw once `legacy_ecs_sprites` is on |
| `ecs_item_draw_row` + `legacy_ecs_sprites` flip (owner, 2026-11-10; bench-000564) | one row write per item update (memory below); frame vs off: 0.992 / 0.986, world_draw 0.989 / 0.977 (ls_z1 / ls_z25), i.e. parity | Legacy draws from the ECS extract | goes with T9 view deletion |
| `legacy_ecs_sprites` mobile cache (e079d5368) | miss frames copy calls (~0.1 us per mobile on <= 10 % of frames) | Legacy mobiles replay batcher calls (~90 % of draws at the large scene) | share entries per (body, equipment, frame) |
| world index slices (`world_entity_index`, default on b96b6b956) | one array store per mirror upsert; one dictionary op per change in the bookkeeping dictionaries | `World.Get`/`GetItem`/`GetMobile`/`Holds` from the ECS index | slice 5: delete the dictionaries |
| perf-cpu-1109 (456bb0606) | `Items.Add`/`Remove` write two more fields; `Clear` walks items once (logout only) | `World.Holds` for an item is a field compare | goes with the item dictionary (slice 5) |
| T9 child-list slice B fix chain | attach-time `WouldCycle`/`SubtreeDeeperThan` walks and `ParentOf` (up to 3 probes per level); packets only | no packet can loop the container graph or crash | cache parent and subtree height per record |
| ecs-effects-own, tile-head, tile-index-2 | effect-destroy walk, ~1-2 ns Serial+kind check; one OR/AND-NOT per placement | O(1) tile-list head (the `TPrevious` walk was 7-8 % of Legacy britain_z); bit test per walked tile | after-T9 pass |
| decode-stall | two `SetThreadPriority` calls per blocking decode wait | 2 ms wait under load ~500 ms to ~4 ms p50 | - |
| perf-alloc-5 | none in time | refused walks no longer allocate | - |

## Memory (per client)

| Change | Cost | Buys | Revisit |
|---|---|---|---|
| T4 thin handles | ~+32 B per Item (16 B Friflo handle + fields), ~0.8 MB at 25k items | handle-based ECS access | shrinks when T9 deletes cached fields |
| `item_pool` (`bg/item-pool`, default off; cost applies once on) | pool cap 8,192 items: worst case ~2.3 MB of retired items (280 B each) + 128 KB queue array; items wait 2 s (quarantine) before reuse, so the pool also holds them ~2 s longer than before; +1 B per Item (`InItemPool`) | ls_walk item allocation (~4.3 MB per 15 s) and its gen0 share; alloc results pending | lower the cap to the measured peak; prefill at login |
| T6b tile index | `TNext`/`TPrevious` 16 B per item, mobile and effect, now always null (~0.5 MB at 30k) | - (left over) | goes with T9: list-object base class off dynamics (own slice after wave B; also folds in dead `Mobile.FindChair` branch) |
| tile-head (1150d9b3a) / tile-index-2 (8b1f355cd) | ~544 B per chunk (`_heads`), <= ~0.3 MB at 300 chunks; 8 B per chunk occupancy mask | O(1) head; bit test per tile | after-T9 pass |
| T9 child-list slice B | `LinkedObject` 24 B per object on Item/Mobile (2-5 MB) | - | revisited (eb0f70ed0): moved off `BaseGameObject`, ~1.5 MB saved |
| T3 equipment (0x78) | +8 B `Mobile.EquipCache` | layering cache without per-packet cost | T9 frees more |
| `EcsRead` (02c0ef387) | depends on Friflo 4.0.0-preview.2 internals (field offset, `[UnsafeAccessor]`); fallback +0.25 ns per row read at 25k entities | faster ECS reads | delete or thin at the Friflo upgrade |
| perf-11 corpse layer tables | 8 x `Item[256]`, ~17 KB; may keep destroyed corpse items alive until reused | ~500 scans to 0 per frame in the corpse scene | cache serials or clear on destroy |
| ecs-effects-own (endpoints) / `ecs_effect_motion` | ~40 B + 8 B per live effect (< 0.05 MB) | see above | - |
| ecs-item-offset | 12 B per item that ever rode a moving boat; `ecs_boat_steps` ~90 B per moved boat + 16 B per passenger slot | boat offset and motion in the ECS | - |
| `ecs_small_fields` | `MobileTraits` 3 B + `ItemTraits` 2 B per record (< 0.1 MB) | see above | - |
| `ecs_active_items_source` | item handle table + free list (a few KB), Item +4 B | removed items not kept reachable | - |
| `ecs_item_draw_row` | ~0.35 MB at 25k items | Legacy draw-row parity | - |
| `legacy_ecs_sprites` mobile cache | one `LegacyEntry[]` per drawn mobile, ~56 B x 12-24 calls (~1 KB): ~2 MB at ~2k mobiles, allocated at first draw (corpses window: one 67-120 KB frame, 19 B/frame avg). Switch off at runtime keeps arrays of surviving mobiles until they leave (dev-only toggle, Codex P3) | see above | free arrays when the switch goes off |
| `world_entity_index` | handle table 8 B per entity id (32 KB initial, ~0.1-0.4 MB peak); slice 3: `Mobile.TableKey` 4 B per mobile + one reusable `List<Item>` in `WorldWriter`; slice 4: ordered item table ~12 B per slot + `Item.Ordinal` 4 B (~0.5 MB at 25k items); bookkeeping dictionaries ~0.6 MB | identity, order and holds without the dictionaries | slice 5: drop the dictionary walk, decide the bookkeeping dictionaries |
| perf-cpu-1109 | ~16 B per Item for the held flag (reference, uint, bool), ~0.3 MB at 20k | field compare for `World.Holds` | goes with the item dictionary (slice 5) |
| perf-alloc-sites-2 (5c6b682a7) | reused text pixel buffer up to 1 MB (game thread) | steady main thread 40 to 0 B/frame p50; ls_walk allocation about halved | shrink after a burst or rasterize into the atlas |
| perf-alloc-5 | two `PathObject` lists kept at peak size | refused walks no longer allocate | trim after a long path |
| worldmap-texture (398510734) | none new: peak during a facet map load ~235 MB (image + texture); texture freed on close (saves ~117.5 MB) | no off-thread GPU create/dispose; fixes the reopen crash | move the `Log.Info` inside the cleanup `try` |
| Large-scale fixes (a122f2cc1) | Gpu capacity 131,072 sprites and 128 animation layers: +64 MB | no silently dropped sprites, no page flicker | - |
| Login warm-up (11174c3cf) | 22-31 ms and ~32 MB at login (chunk pool 300, land/static recycler pools) | natural collections in the first minute 4-7 to 0-1 | per-process only; see 4K pool caps (owner-review) |
| Cliloc uncompressed (owner, 2026-10-31) | +11.5 MiB on disk | startup load 1.6 vs 8.6 ms | `--cliloc-brotli` available |
| CUO_DIAG diagnostics (fbd7422c8) | only with CUO_DIAG=1: ~6.3 MiB (histogram windows, ping records) plus a writer thread; probe CPU unmeasured; off: static gates, collector compiled out without devtools | external comparison reports (present, ping timeline, phases, uploads) | sample probes if reports show the instrumentation skews results |
| present_at_display_rate (8b68f0495, default off) | picking/UI state set during draw lags up to one display period; with event_loop off the loop busy-polls (as uncapped does today) | ping = real round trip, no unseen frames | event_loop wake on socket data during Draw/Present |
| render_view_reuse + upload_skip_unchanged (ad77d6654, default off) | ~0.3 MiB per GPU client off; ~5.6 MiB on at starting sizes (incl. 1.0 MiB dynamic-page cache), bounded at ~13.4 MiB by the translucent capacity; byte compares per upload | 99.7 % of dynamic uploads skipped when quiet, no chunk-table rebuild or eviction scan | producer epochs instead of byte compares and page-key scans |
| text_quad_cache / ui_gump_dirty (5ead9ecae, default off) | mutation bookkeeping runs with the switches off; ~0.2 MB per 1,000 retained sprites, ~0.05-0.1 MB per 1,000 glyph runs | glyph quads reused while text is unchanged | atlas/page generation instead of per-glyph validation |
