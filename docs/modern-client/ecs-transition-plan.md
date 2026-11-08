# ECS Transition Plan: the ECS becomes the owner of world state

Drafted 2026-10-30 from a read-only review of `poc/modern-client` at 6d2159563, plus the unmerged branches `bg/ecs-e10-clock` (b49e1d8cf) and `bg/ecs-e12-checker` (no commits yet). The owner's goal is that the client moves fully onto the ECS. This plan says how the ECS goes from a mirror to the owner, in phases. Each phase has a switch, a checker and a size.

## 1. Where we are (facts this plan rests on)

**What the ECS holds today.** The ECS is the Friflo mirror (`src/ClassicUO.Ecs/WorldMirror.cs`, `Components.cs`).
- It holds only **items and mobiles**, keyed by serial. Effects, land, statics and multi components are not mirrored. A multi item gets `MultiTag`, but its components are not mirrored.
- Components:
  - `Serial`, `Graphic`, `Hue`, `Position`, `Facing`, `Flags`, `Amount`;
  - `EquippedOn` and `ContainedIn` (indexed by parent serial);
  - the tags `MobileTag`, `ItemTag`, `PlayerTag`, `OnGround`, `MultiTag`, `EquipmentDirty`.
- Mobiles also get `AnimationState`, `MobileRenderState`, `EquipmentSlots` and `MobileInputs`. With E4 on, they also get `MobileClock`.

**How changes reach it.** `World` and its GameObjects own the state. The mirror is fed by `WorldEcs.Sync` from these places:
- `GameObject.AddToTile`/`Park`;
- the `Entity.Direction` setter;
- `World.GetOrCreate*` and `RemoveItemFromContainer`;
- `CreatePlayer`;
- 10 sites in `PacketHandlers.cs`;
- one in `ContainerGump`.

`WorldEcs.PushInputs` and `PushClock` are called from the Mobile's setters, step-queue changes and `SetAnimation`.

The mirror's fields are plain public fields on `GameObject`/`Entity`/`Item`: `Graphic`, `Hue`, `X`, `Y`, `Z`, `Flags`, `Serial`, `Amount`, `Container`, `Layer`. Handlers write them directly and rely on a later `Sync`. A rough grep finds about 68 `.Graphic =` writes in 26 files, 125 `.Hue =` writes in 41 files, and 27 `.Flags =` writes. Most of these are UI controls, not world objects. The T0a census ([ecs-state-census.md](ecs-state-census.md)) replaced the rough grep with a compiler-derived list: **266 world write sites in 14 files** (`Graphic` 33, `Hue` 17, `Flags` 15, `X` and `Y` 14 each, `Z` 12; 121 of the 266 in `PacketHandlers.cs`), plus 32 on map and effect objects.

**Who reads the ECS.**
- The Gpu world: `MobileSpriteExtract` reads ECS state. `ItemSpriteExtract.Emit` still takes the `Item` GameObject.
- `MobileFrameCache`, the E4 clock system, and the checkers.
- Everything else reads GameObjects:
  - Legacy draws through `MobileView`/`ItemView`/`GameEffectView.Draw` from `RenderLists`;
  - picking uses the views' `CheckMouseSelection`;
  - about 37 UI files read `World.Items`/`Mobiles`/`Player`/`Get`;
  - about 69 files hold `Item`/`Mobile`/`Entity` references (about 360 declarations);
  - 64 sites use the `LinkedObject` child lists.

**The world update.**
- `World.Update`:
  - the mobile loop, or E4's due-time path behind `world_update_mobile_system` (merged, **off** by default);
  - the active-items list (`world_update_active_items`, on);
  - effects, text, the world-map pass;
  - then `Ecs.Update`.
- E10 (unmerged) replaces E4's `Dictionary._version` trick with an owned `MobileTable`. It measured that moving the four clock reads alone saves about 0.0002 ms per frame, so the clock should move only together with the frame advance.

**Tile lists.** `Chunk.AddGameObject` (Chunk.cs:190-369) keeps each tile's `TNext`/`TPrevious` list sorted by `PriorityZ`, which is computed **at insertion**. Equal priorities are broken by insertion history:
- land goes before every equal (`state 0`);
- non-preview multi components go before every equal non-land (`state 1`);
- everything else goes after all equals.

So within one `PriorityZ` the order is: lands (newest first), then multis (newest first), then all other objects (oldest first).
- That order sets the max-Z cut-off and the equal-depth painter order of both renderers.
- The Gpu item cache (W4/E7) and the effects rule (E6) replay it.
- E4 found that the order of `AddToTile` calls (update order, `ConfirmWalk` re-inserts) is observable.

**Checkers in place:**
- `-ecscheck N`: mirror fields, `MobileInputs`, `Mounted`, equipment order, animation;
- `CUO_ECS_PARITY`, `CUO_ANIM_SYNC`: extract vs view capture, including the lights rule (run both `light off` and `light 1E`);
- `CUO_GPU_ITEM_CHECK` (E12 is making it same-frame);
- `CUO_W5_CHECK`, `CUO_MOBILE_INPUT_CHECK`, `CUO_MOBILE_CACHE_CHECK`, `CUO_MOBILE_UPDATE_CHECK` (the E4 clock, skipped-is-no-op, order; E10 adds a table-vs-dictionary shadow);
- `CUO_PREP_CHECK`, `CUO_GPU_SKIP_CHECK`;
- `pickscan:CxR` (skip off vs on) and `CUO_BENCH_PICK`;
- `CUO_BENCH_SHOTS` (screenshots);
- the settle counters (`[settle]`);
- `CUO_RECYCLE_CHECK`.

Driver scripts: `ls_parity.txt`, `w5push.txt`, `w5items.txt`, `lswalk`, `travel`/`glide`.

**Constraints from the owner:**
- The static world stays out of the ECS (ecs-decision.md; README summary item 2).
- Legacy stays. Legacy-on-ECS is deferred until it blocks other work; meanwhile keep the extract renderer-neutral.
- No plugin API or third-party scripting in the target. The target is a standalone NativeAOT exe.
- Draw order and picking work is not for cheaper models (world-prepare-ecs-plan.md).

## 2. What "the ECS owns world state" means here (target)

1. **One writer.** Every change to a world object goes through one funnel, `WorldWriter`, which writes the ECS. Nothing else writes world-object state.
2. **The ECS is the reference.** Checkers compare everything else against it, not the other way round.
3. **GameObjects become thin handles.** `Item`/`Mobile` keep their identity (one object per entity, same lifetime) so the about 70 files that hold them keep compiling.
   - Their world-state members become properties that read ECS components through a cached `Entity` and the mirror epoch (the E5 pattern).
   - Only hot fields keep a write-through cache, until their last hot reader is gone.
4. **`World.Update` becomes an ordered list of systems** (our own thin scheduler, single-threaded, as ecs-decision.md says). It covers the clock, the frame advance, steps, the range sweep, active items, effects and the world-map pass.
5. **Spatial membership and tile order come from ECS changes.** A per-tile dynamic index is maintained by the funnel's place and move operations. It reproduces `AddGameObject`'s order exactly. Land, statics and multi components stay in the chunk's static list, and the walk merges the two.
6. **Both renderers draw dynamics from the ECS through one neutral extract.** Gpu does this today. Legacy gets a batcher sink in T7b.
7. **Out of scope by decision:**
   - land, statics and multi components (chunk data, ecs-decision.md);
   - player-session data that isn't a world object (stats, skills, buffs, abilities, OPL, journal), which stays in plain typed stores (see Q4).

## 3. Phases

Sizes are agent-days for one agent including its parity runs. Tiers:
- **S+C** = Sonnet draft, Codex gpt-6-astra audit (the `rf-agent task` recipe), Opus review at merge;
- **O** = Opus does it, with a Codex audit before merge.

The standard gate for every phase that changes behaviour:
- 580+ unit tests, in both `CuoDevTools` variants;
- `-ecscheck 120` at 0;
- `CUO_ECS_PARITY=1 CUO_ANIM_SYNC=1 CUO_GPU_ITEM_CHECK=1 CUO_W5_CHECK=1 CUO_MOBILE_INPUT_CHECK=1 CUO_MOBILE_CACHE_CHECK=1 CUO_MOBILE_UPDATE_CHECK=1` all at 0 over `ls_parity`, `w5push` and `w5items`, at day and night;
- `pickscan:8x6` at Britain and the bank, 0 different;
- `[settle]` unchanged;
- the phase's own new checker at 0;
- a paired NativeAOT timing at Britain, the bank, zoomed out and the large scene (zoom 1, 2.5, walking), in Legacy and Gpu.

A switch defaults on only after its gate holds and its audit is done. Its off path is deleted one phase later (see Q5).

| Phase | What | Switch | New checker | Size | Tier |
|---|---|---|---|---|---|
| T0 | Census, guards, field encapsulation (no behaviour change) | none | write-site budget test | 3–4 | S+C |
| T1 | The mutation funnel: `WorldWriter`, dual write, the GameObject still read | none (same values) | `CUO_FUNNEL_CHECK`, `-ecscheck` v2 | 6–8 | S+C per packet group; O for the API |
| T2 | Container contents owned by the ECS (child order relation) | `ecs_read_contents` | `CUO_CONTENTS_CHECK` | 3–4 | S+C |
| T3 | Equipment owned by the ECS (`FindItemByLayer`, paperdoll, mount) | `ecs_read_equipment` | `CUO_EQUIP_READ_CHECK` | 3–4 | S+C |
| T4 | Thin handles: scalar state read from the ECS; caches only for hot fields | `ecs_owns_scalars` | `-ecscheck` v2 (cache vs ECS), `CUO_FUNNEL_CHECK` | 5–7 | O design, S+C bulk |
| T5 | `World.Update` as systems: E4 on, frame advance and timer in the ECS, range sweep, world-map pass, active items, scheduler | `world_update_mobile_system`, `world_anim_system`, `world_sweep_system` | `CUO_ANIM_ADVANCE_CHECK`, `CUO_SWEEP_CHECK` | 9–12 | O for the advance; S+C for the rest |
| T6 | Tile order from an ECS-maintained dynamic tile index; steps as a system | `world_tile_index`, `world_step_system` | `CUO_TILE_ORDER_CHECK`, `pickscan` with a setting toggle | 8–11 | O |
| T7 | Legacy: compatibility views (T1–T6), then Legacy-on-ECS dynamics through a neutral extract and a batcher sink | `legacy_ecs_sprites` | `CUO_LEGACY_ECS_PARITY` | 7–11 (T7b) | O |
| T8 | Effects as ECS entities | `ecs_effects` | `CUO_EFFECT_CHECK` | 4–6 | S+C draft, O review |
| T9 | Deletion | n/a | n/a | 3–5 | S+C |

Total: about 51–72 agent-days. With two or three agents in parallel (T2/T3 next to T5; T6 next to T4), that is about 4–6 calendar weeks.

### T0: census, guards, encapsulation (no behaviour change)

**T0a: state census.**
- Classify every member of `GameObject`, `Entity`, `Item`, `Mobile`, `PlayerMobile` and `MobileAnimation` as one of:
  - **A, world state** (moves to the ECS);
  - **B, derived** (recomputed from A, e.g. `HitsPercentage`, `IsMounted`, `MultiGraphic`);
  - **C, per-frame view scratch** (`RealScreenPosition`, `ScreenPositionGen`, `FrameInfo`, `AlphaHue`, `_surfaceOverheadCache*`, `UpdateStamp`);
  - **D, session or UI data** (PlayerMobile stats, skills, buffs, abilities, `AutoOpenedCorpses`, `Title`, OPL);
  - **E, structure** (`TNext`/`TPrevious`/`TileChunk`/`PendingBlock`, `LinkedObject` child lists, `Ordinal`).
- For every A field, list its write sites (file:line), and its readers grouped by hot (walk, views, extract) and cold (UI, managers).
- Output: a table in `docs/modern-client/` plus a generated list.

**T0b: write-site budget test.**
- An xUnit test that scans `src/ClassicUO.Client` for direct writes to A fields (regex over `.Graphic =`, `.Hue =`, `.X =` on `Item`/`Mobile`/`Entity` receivers, with an allowlist file).
- It fails when the count grows. During T1 the allowlist shrinks to the funnel alone.

**T0c: encapsulate the A fields as properties** (`Graphic`, `Hue`, `X`, `Y`, `Z`, `Flags`, `Amount`, `Container`, `Layer`, `Serial`, `Name`, `NotorietyFlag`, the animation fields).
- Do it on `Entity`/`Item`/`Mobile`. `GameObject`'s `X`/`Y`/`Z`/`Graphic`/`Hue` are also used by `Land`/`Static`/`Multi`; a property with a backing field inlines.
- Gate: tests, `-ecscheck`, and Legacy and Gpu paired AOT world prepare unchanged within noise (Legacy large spot about 4.3–4.4 ms).

Sizes: T0a 1–1.5, T0b 0.5, T0c 1.5–2.

**Status (2026-10-30):** T0a and T0b are done on `bg/ecs-t0-census`: [ecs-state-census.md](ecs-state-census.md) (the classification, the write sites, the readers, the reference holders) and `tests/ClassicUO.UnitTests/Ecs/WriteSiteBudgetTests.cs` with its allowlist `WriteSiteAllowlist.txt`. Two results change T0c and T1: `Offset` is a struct that is written member by member (`item.Offset.Z = `), and `DisplayedGraphic` has no world writer; see census section 9.

### T1: the mutation funnel (dual write)

**The writer.** Add `WorldWriter` (src/ClassicUO.Client/Game/Ecs/WorldWriter.cs) with operations that match packet meaning, not fields:
- `UpsertWorldItem` (0x1A, 0xF3), `PlaceInContainer` (0x25, 0x3C, with a child order stamp), `Equip` (0x2E, 0x78 contents);
- `UpsertMobile` (0x20, 0x77, 0x78, 0xD3), `MoveMobile`;
- `SetHits` (0xA1, 0x11, 0x2D), `SetMana`/`SetStamina`, `SetName` (0x98), `SetNotoriety`, `SetFlags`, `SetHue`, `SetGraphic`;
- `Rekey` (0xBB, the death re-key), `Remove` (0x1D, the range sweep);
- `SetAnimation` (0x6E, 0xE2, 0xBF.19, 0xAF).

**What each operation does:**
1. writes the ECS components (and their tags or indexes);
2. writes the GameObject properties through internal setters (the cache);
   `Offset` has four setters since T0c: `Offset.set` and the member-wise `OffsetX`/`OffsetY`/`OffsetZ` (they write `_offset.X/Y/Z` directly). The funnel and the `CUO_FUNNEL_CHECK` scope check must cover all four, or member-wise writes (`m.OffsetZ = 4`, `m.OffsetX += 1`) bypass it.
3. performs the side effects that `Sync`/`AddToTile` used to trigger (`EquipmentDirty`, `QueueItemUpdate` for corpses, tile placement).

**Rollout.**
- Convert the handlers group by group: world items; containers; equipment; mobiles; status and hits; animation; misc (spellbook, house, boat, the custom house). Each group is its own commit.
- `WorldEcs.Sync`/`PushInputs` stay as a fallback until the last group is done; then they become funnel internals.

**Checkers:**
- **`CUO_FUNNEL_CHECK=1`** (new, `CUO_DEVTOOLS` builds only): every A property setter checks `WorldWriter.InScope`. Writes outside it are counted and logged as `[funnel] direct write <field> at <caller>`, using a `CallerMemberName`/`CallerFilePath` pair on the setter overloads in dev builds.
- **`-ecscheck` v2:** add `Name`, `Hits`/`HitsMax`, `NotorietyFlag`, `Layer` for every item, `Price`, child order (see T2), and the animation timer fields once T5 lands.

**Gate:**
- the standard gate;
- funnel violations 0 over all driver scripts plus a new `uistate.txt` script: open the backpack, bank and corpse, drag between containers, split stacks, trade, buy from a vendor, open the spellbook, equip and unequip from the paperdoll, rename a pet;
- the E2 bench (`EcsPacketCostBench`) at or below its current cost plus 0.1 µs per packet, with no allocation.

**No switch.** Values are identical by construction, and the checkers prove it.

**Ordering note.** Handlers must keep calling into the funnel in the same order they mutate today. Tile insertion order is observable (section 1).

**Status (2026-10-30):** first slice on `bg/ecs-t1-funnel` (566eff92a; poc-log "T1 slice 1"). Done: the API (`WorldWriter`, `World.Writer`, `InScope` with a ref-struct `Scope`, the hook fold in `WorldEcs`), `CUO_FUNNEL_CHECK` on all 43 A setters including the four `Offset` setters, and the world-items group (0x1A, 0xF3, the item branch of 0x1D). Write-site budget 326 → 313. Left: containers (0x25, 0x3C, 0x27's `AddItemToContainer`), equipment, mobiles, status and hits, animation, misc (spellbook, house, boat, custom house), `-ecscheck` v2, `uistate.txt`; the handoff `handoffs/t1-funnel.md` lists the steps. Round 2 (b90d02c6d, after the Codex audit; poc-log "Codex audit round 1") fixed the operation pattern every later group copies: resolve and reject foreign or stale objects first; compute the values (`ItemState`) with pure helpers, every read of the old state before the first write; write the ECS from the values (`WorldEcs.CommitItem`), then the cache from the same values; only then the helpers that read world state (`Item.RefreshGraphicState`); placement as its own operation when the handler has callbacks between writes and placement (`PlaceWorldItem`). Scopes are private to the operations and own only their subject; the fold is per instance and not reentrant; the scanner exempts the operations' bodies, not the file. Budget 313 → 312. Round 3 (345fc7223, after the Codex audit of round 2; poc-log "Codex audit round 2"): admission (no operation or fold open) before any side effect; results such as the new Container are computed, never read back after the operation's own unlink (`World.UnlinkFromContainer` writes no state); placement through a per-upsert token (`WorldWriter.Placement`); violations throw in CUO_DEVTOOLS builds and in release log once per kind and take the pre-T1 path; the scanner resolves backing fields by declaring type across partial declarations; `Mobile.Steps` is read-only (checked `AddStep`/`RemoveFirstStep`/`ReplaceStep`/`ClearStepQueue`). Budget 312 → 325 (the release fallback's writes in WorldWriter.cs). For T4: `Layer` for ground and contained items in the mirror schema, and defaults for new items before their first commit. Containers group done (36b46ef54: `PlaceInContainer` for 0x25, 0x3C's items and 0x27's deny path; 0x3C's bulk clear stays outside it). Round 4 (after the Codex audits of rounds 3 and 4; poc-log "Codex audit round 3", "Codex audit round 4"): the release fallbacks are exempt by name and pinned to their violation paths on syntax trees (budget 306); production code cannot re-enter the writer, so a nested operation is a violation that throws in CUO_DEVTOOLS builds and is rejected in release (no nested fallback). Measured on 36b46ef54 (`bench-000022`/`bench-000024`): funnel rows within the gate, 0 parity mismatches. Part 2 (`bg/ecs-t1-funnel-2`, poc-log "T1 funnel, part 2"): the Codex round-5 scanner gaps closed (conditional compilation, the walk-queue receiver, jumps past `Reject`); `ClearContainer` (0x3C's bulk clear, 0x24) and `ReturnHeldItem` (0x27's paperdoll, removal and terrain branches) are operations; budget 306 → 297. Part 3 (`bg/ecs-t1-funnel-3`, poc-log "T1 funnel, part 3"): equipment: `Equip` (0x2E), `ClearWornItems` and `WearItem` (0x78/0xD3's equipment list); 0x1D's equipped-item deletion was already `RemoveWorldItem`; budget −8 (sum 298 → 290). `WearItem` takes the handler's captured owner instance, validated against its own table (Codex audit: an entry under a death-rekeyed owner's serial must not change the owner); gates bench-000061/62 pass. Part 4 (`bg/ecs-t1-funnel-4`, poc-log "T1 funnel, part 4"): mobiles. Groundwork: the fold takes any entity (a folded mobile skips its Syncs and input/clock pushes), `WorldEcs.CommitMobile` + `CommitMobileInputs` (the derived step), `Mobile.PlanStep`/`ApplyStep` (EnqueueStep split into a pure plan and the mutation), `Mobile.BodyState`. Operations: `UpsertMobile` + `PlaceMobile` (UpdateGameObject's mobile branch: 0x77/0x78 for another mobile, the item packets for a mobile serial, 0xF6; the handler's captured `obj`), `SetNotoriety` (0x77, 0x78), `SetMobileBody` (the player's body of 0x20/0x77/0x78), `MoveMobile` (0x20's tile), `RemoveMobile` (0x1D for a mobile with its equipment). Budget 290 → 253. Gates bench-000065/66 pass (parity 0, no direct write from the converted handlers; the E2 ordering effect of part 3 confirmed as row placement). Part 5 (`bg/ecs-t1-funnel-5`, poc-log "T1 funnel, part 5"): EnterWorld (0x1B: `SetMobileGraphic`, `MoveMobile`, `SetMobileDirection`, the last unfolded for the door check), the walker (`StopMobile` for DenyWalk's clear, `MoveMobile` for its move, 0x21's direction, 0x22's notoriety) and `Mobile.ProcessSteps`' step completion (`CompleteStep`: folded for every mobile but the player; cheaper than before in the E2 row), and the range sweep (`SweepMobile`/`SweepItem`). Budget 253 → 239. Part 6 (`bg/ecs-t1-funnel-6`, poc-log "T1 funnel, part 6"): `PlaceMobile` folds again for every mobile but the player (the 0x77 E2 row's +0.1 µs came from part 4's audit fix unfolding it for all); status and hits (`SetHits`, `SetStatus`, `SetMobileStatus`, `SetMana`, `SetStamina`, `SetRace`, `SetHealthbar`: 0xA1–0xA3, 0x2D, 0x11, 0x16/0x17); the server's animations (`AnimateMobile`: 0x6E, 0xE2, 0xAF, 0xBF.19, 0xBF.2B; a funnel operation costs about 0.01 µs per call, so the per-frame animation would be inside the noise, but `ProcessAnimation` needs a split before it can be one); the boat (`CompleteBoatStep`, `MoveBoatPassenger`, `SetBoatStepTime`, `ResetBoatOffset`) and the spellbook (`FillSpellbook`). Budget 239 → 183. Part 7 (`bg/ecs-t1-funnel-7`, poc-log "T1 funnel, part 7"): the small writers (`SetName` for all 10 Name sites, `SetPrice`, `SetSpeedMode`, `SetWarMode`, `SetDead`, `PlaceCorpseEquipment`), the death re-key (`RekeyDeadMobile`; its ECS commit `OnSerialChanged` still reads the entity, so it runs after the cache writes: T4 needs a value-taking re-key), the `ProcessAnimation` split (`Mobile.Update`: `ProcessSteps` on its own, then `AdvanceAnimation` and, at a corpse animation's end, `RemoveDeadMobile`), the gate script `w5boat` (spellbook, boat steps and turns; ModernUO's TestScene cannot place either, `[self addtopack Spellbook` and `[self add SmallBoat` can). After the gate (bench-000084 pass; the boat script fixed and rerun, its boats deleted with `[Global Delete where SmallBoat` in a box) and the Codex audit (two P3 test gaps, fixed): the per-frame offset interpolation (`InterpolateStep` for ProcessSteps, `InterpolateBoat`, `FollowBoatComponents` and `FollowBoat` for the boat) and 0xF6's boat jump without smooth movement (`JumpBoat`, a branch part 6 had missed; the gate found it). Final gate bench-000091 passes, with the deferred boat reset seen live (w5boat with smooth boat movement). The split measured within noise on the large scene (bench-000085), so it stays. Budget 183 → 156. Part 8 (`bg/ecs-t1-funnel-8`, poc-log "T1 funnel, part 8"): the idle animation (`Mobile.PlanIdleAnimation` over `IIdleAnimationData`, then `IdleAnimation`), the footstep sound (`StepSound`), a corpse's frame (`AdvanceCorpseFrame`), a server animation a step ends (`EndServerAnimation`), the player's walk queue (`QueuePlayerStep`: admission, `CloseBank`, `PlanWalkStep`, the writes; the coalesced turn through `TurnPlayer`; after the Codex audit a walk from the player's position or direction callback joins the open unfolded operation on the player, the one documented exception to "operations do not nest"), logout's `World.Clear` removals (`ClearWorld`, a scope that owns the whole World) and the one gump-side write to a held object (`ContainerGump`'s clamp: `ClampContainerPosition`; the character-creation and race-change previews, which until the Codex audit lived in World's tables, are now detached: `Item.Create`, no table, the paperdoll draws the preview instance). All unfolded; the per-frame ones admit a stale instance of this World and reject another World's. Budget 156 → 138. Gate bench-000092 (pre-fix) and bench-000094 (after the Codex audit fixes, 54170da81) pass. Part 9 (`bg/ecs-t1-funnel-9`, poc-log "T1 funnel, part 9"): the value-taking re-key (`WorldEcs.CommitRekey` before the cache writes), the last removal sites (`RemoveBankContents` for `CloseBank`, `RemovePickedUpItem`, `ClearForMapChange`, `ClearForLogin`; the funnel check's caller now goes past the removal plumbing to the origin), `-ecscheck` v2 (mirror components `EntityName`, `Vitals`, `MobileNotoriety`, `ItemPrice`, `ItemLayer` committed by the operations that write them; the check compares them) and the child-order stamps (`ChildOrder`, a stamp per `PushToBack` of a held item; the check compares each parent's children in list order with the mirror's by stamp), and `uistate.txt` (with a trade partner client). Budget 138 → 138. Gate bench-000095 failed: every client crashed at login (the new components were missing from the NativeAOT schema) and the job exited 0. After the Codex audit (poc-log "T1 funnel, part 9", Codex audit): the schema registers every component, with a test; attachment in the mirror is `ItemData.Stamp` (0 = in no child list; a change of parent without a push detaches), so orphans and the spellbook's clear check clean, and v2 compares attachment, not the Container; the vendor sort is an operation (`SortShopContents`); Amount, Layer and the stamp are one item component (`ItemData`), which brought the 0x78 rows back within the E2 gate; the stamp is 64-bit; the map change's player writes are `MoveMobile`/`StopMobile`; the gate job fails on a crash, a timeout or a checker mismatch. Gate bench-000101 (a738a7c6d) passes: every checker clean, no direct-write site, uistate steps proven on both clients (bench-000098 to 100 fixed the script and the map change); E2 bench-000099: all 111 rows within +0.1 µs, bytes equal. Budget 138 → 138. Left: a client check of the two appearance gumps (not reachable by the driver), and the FrameStats series on a quiet machine (owner).

### T2: container contents owned by the ECS (first state to move)

**Why first:**
- Contained items are never on a tile, never drawn by the world, and never picked in the world.
- Their readers are gumps (`ContainerGump`, `GridLootGump`, `ItemGump`, counters, `SpellbookGump`, `TradingGump`, `ShopGump`), `FindItem*` and macros.
- The risk is confined to the UI, and they prove the read path and the ordered relation before anything visible moves.

**What changes:**
- Add a `ChildOrder` component (a monotonic `uint` stamp taken at every `PushToBack`), next to `ContainedIn`. Container order today is the `LinkedObject` list built by `PushToBack`, i.e. arrival order with a re-push moving an item to the back. It sets the icon overlap and the click order in container gumps.
- `WorldState.Contents(parent)` enumerates the `ContainedIn` index sorted by stamp. Use a per-parent cached sorted span, rebuilt on change. Friflo's index order isn't stable across removes.
- Readers move to `Contents(parent)` behind `ecs_read_contents`. The `LinkedObject` list for container children is still maintained (dual).

**Checker `CUO_CONTENTS_CHECK=1`:** at every read and every frame for open containers, the ECS enumeration must equal the linked list (serials and order).

**Gate:** the standard gate, `uistate.txt`, and container screenshots (`CUO_BENCH_SHOTS` on an open backpack and corpse) off vs on.

**Then:** default on; T9 deletes the container half of the linked list.

**Status (2026-10-31):** on `bg/ecs-t2-contents` (poc-log "T2: container contents owned by the ECS"). `WorldState.Contents(parent)` (`World.State`): the parent's attached children (`ItemData.Stamp != 0`; the stamp is part 9's child-order stamp, so no separate `ChildOrder` component) in stamp order, from a per-parent cached sorted array in `WorldMirror`, invalidated by every change of attachment and untracked until a parent is first read. The spellbook's detached spell entries are ECS state as a set (`SpellbookContent`, committed by `FillSpellbook` and cleared by `ClearContainer`); `FillSpellbook` hands its entry objects to `WorldState`, valid only while the set is unchanged. `CUO_CONTENTS_CHECK=1` compares every read and every open container gump per frame. The readers (container, grid loot, item, spellbook and trading gumps, the shop lists of 0x24/0x74, `FindItem*`, the counters, the macros' finders, Profile's gump save, the driver's `contents`) walk `WorldState.Children`, the ECS's children with `ecs_read_contents` on (default **off**); the `LinkedObject` list stays maintained. Not moved: `FindItemByLayer` and `MobileView`'s equipment checks (T3), the list's writers and the checkers (T9). Gate: off (bench-000104) and on (bench-000106) pass with 0 contents and 0 ECS mismatches; the shots are identical except an animated skull; E2 within the gate, the cached rows allocation-free against nothing-cached controls. Codex audit: the spell-entry aliasing (P1), the displaced re-key (P2) and the eager spell-entry drop (P2) fixed and tested; 45 mutations all fail a test. Budget 138 → 138. Left: the default flip (recommended on; the coordinator decides), then T3; T9 deletes the container half of the list. **Merged (751c016f1) and default on since 2026-11-01**; the off path and the checker stay for one phase.

### T3: equipment owned by the ECS

**What changes:**
- Add `WornLayers` (an inline array of 0x1D layer serials) per mobile, maintained by `Equip`/`Remove`.
- `Mobile.FindItemByLayer`, `IsMounted` (already `EquipmentSlots.Mounted`), `GetAnimGroupInput` hands, abilities, paperdoll slots and `PaperdollOrder.BuildInWorld` read it behind `ecs_read_equipment`.
- Legacy `MobileView` reads it through `FindItemByLayer`. That is a compatibility view, and nothing in Legacy changes.

**Equipment that arrives before its mobile** (perf-opportunities, "Who-knows-what").
- Today the mirror holds the item under the parent serial but never attaches it.
- Keep that behaviour exactly: `WornLayers` is built only from attaches the legacy code performs.
- A fix (attach it when the parent arrives, or drop it) is a separate switch (Q6).

**Equipment order stamp (coordinator, 2026-10-31).** T1's `WorldWriter.ReturnHeldItem` commits twice on its paperdoll branch (the detached state, then the wearer) only to reproduce the old path's two Syncs, which move the item to the end of the mirror's equipment index. T3 replaces this double commit with an explicit equipment-order stamp (the child-list position written as a value in the one commit), and the index orders by the stamp. Until then the double commit stays.

**Checker `CUO_EQUIP_READ_CHECK=1`:** every `FindItemByLayer` call returns the same serial from both sources. Plus `-ecscheck`'s existing equipment order, mount and hands compare.

**Gate:** the standard gate, `w5push` (mount, dismount, equip changes), and paperdoll screenshots.

**Status, T3a (2026-11-01):** on `bg/ecs-t3-equipment` (poc-log "T3a: equipment owned by the ECS"). WornLayers (layers 0x01..0x1D: the first attached item's serial by child-order stamp, a per-layer count, a version) lives in `EquipmentSlots.Worn` (an 11th mobile component made Friflo's record churn 6x slower), maintained by the mirror from attachment only (orphans stay unworn). The equipment-order stamp: `EquipmentSlots` is rebuilt from WornLayers, so nothing reads the index's order, and ReturnHeldItem's paperdoll branch commits once. `CUO_EQUIP_READ_CHECK=1` compares every `FindItemByLayer` on a mobile. `ecs_read_equipment` (default **off**) makes `FindItemByLayer` (and with it IsMounted, the hands, the abilities, the paperdoll, BuildInWorld, Legacy's MobileView) and MobileView's guards read the ECS through a per-mobile cache; an instance World does not hold reads its own list. Gates off (bench-000115) and on (bench-000116) pass with 0 equipment, contents and ECS mismatches; the paperdoll shots match (up to the reopen and the animated rider); E2's read rows are 49-56 % faster than the list walk; FrameStats within the noise. **Parked:** the maintenance costs 0x78 +0.11-0.36 us per packet (over the E2 gate) and 0x2E +0.03 us; +8 B per Mobile. The Codex audit's six findings are fixed and tested (poc-log, "Codex audit"), one of them a behaviour fix on the off path too: 0x2E now unlinks a kept backpack from its old wearer's list (a legacy ClassicUO list-corruption quirk). Left: the owner's call on the parked cost, the default flip, T3b (the layering rules below), T9.

**Equipment layering as data (owner, 2026-10-31).** The EA client hides and reorders worn layers with hard-coded logic that no data file records. ModernUO-Client issue #39 shows the bleed: a layer under the ringmail arms shows through in ClassicUO but not in the EA client. ClassicUO hard-codes its version of these rules:
- `MobileView.IsCovered`: shoes under pants/legs, pants under legs/skirt/robe, a tunic under a robe, open-robe graphics;
- `AnimationRules.IsCovered` (its ECS copy);
- the cloak-before-helmet anchoring;
- the tunic-over-robe (surcoat) reordering.

When T3 moves equipment into the ECS, it replaces these with a pack data file of layering rules:
- per layer and direction: draw order;
- per item (graphic or AnimID): which layers it hides, conditions on other worn items, and order overrides;
- separate tables for the paperdoll and the world, because the two use different gumps and art;
- the importer writes today's rules as the default file;
- shard admins add entries for custom equipment.

The rules must stay exactly equal to today's, so a checker compares the data-driven result with the hard-coded one over the parity scripts before the hard-coded copy goes. Issue #39's fix comes after that: find the EA rules it shows, and add them as data. Known cases:
- a layer under the ringmail arms shows at the shoulder;
- leggings show through thigh boots (owner, 2026-10-31). Today's rules only hide shoes under pants and legs, never the other way round.

**Rules that depend on race and sex (owner, 2026-10-31: check elves and gargoyles too).** The hard-coded logic already has some:
- **Gargoyles and women:** a second torso order, the "chest-under" variant (`PaperdollOrder.Build` with `altTorso`, which `PaperDollInteractable` sets for women and gargoyle bodies; `BuildInWorld` does the same in the world).
- **Hoods:** the hood rule in `MobileView.IsCovered` covers the head on every body except gargoyles (0x029A, 0x029B, 0x02B6, 0x02B7).
- **Gargoyle equipment remaps** (`FixGargoyleEquipments`, and its ECS copy `AnimationRules.FixGargoyleEquipment`): robe, death shroud and spellbooks draw other art on gargoyles.
- **Elves:** only their own paperdoll body gumps (0x025D/0x025E in `PaperDollInteractable`); no elf-specific covering rule was found in code.

So the data file is keyed by body group (human male/female, elf male/female, gargoyle male/female, and other bodies), not only by item. Gate for T3's layering work:
- paperdoll and world screenshots for each body group wearing the same reference outfits, compared with the EA client (the owner can supply EA shots);
- outfits that include the issue #39 cases (ringmail arms over a shirt, thigh boots over leggings), a robe with a hood, and a gargoyle robe and spellbook.

**Status, T3b (2026-11-01):** on `bg/ecs-t3-layering` (poc-log "T3b: equipment layering rules as data"). Today's rules are data (`layerrules.bin`, `LayerRulesDefault` the one source, the converter's `layers` stage; the pack publish is the coordinator's), evaluated behind `ecs_layer_rules` (default off) in MobileView, the paperdoll and MobileAnimationSystem; `CUO_LAYER_RULES_CHECK=1` compares every decision; 76 M-check exhaustive tests and 778 mutants (24 equivalent, listed). The Codex audit's six findings are fixed (poc-log, "Codex audit"): the ECS slots' direction moves, corpses and the paperdoll's gargoyle-ghost remap now come from the data too, and the checker compares final results. E2: the data decision was about 2x the hard-coded one before the fixes (+0.17 us per mobile, over the gate; paid only with the switch on); see the poc-log for the attempt and whether it is parked. Left: the EA comparison and issue #39's rules (data entries), the default flip, T9 (delete the hard-coded copy).

### T4: thin handles; the ECS owns scalar state

**What changes:**
- Behind `ecs_owns_scalars` (read once at startup into a static readonly bool), the getters of cold A fields read the ECS through the cached entity handle. `Mobile` already caches `EcsEntity`, `EcsEntitySerial` and `EcsEntityEpoch`; `Item` gets the same.
  - The cold fields: `Name`, `Hits`/`HitsMax`/`Mana`/`Stamina`, `NotorietyFlag`, `Amount`, `Price`, `Layer`, `Container`, `Flags`, `Direction`.
  - The cached field is written only by the funnel and becomes write-only. T9 deletes it.
- Hot fields (`X`, `Y`, `Z`, `Graphic`, `Hue`) keep their write-through cache: the walk, the views and `ItemSpriteExtract.Emit` read them per object per frame. E10 measured a separate-component lookup at about 1.1 ns per read and 4.5 ns for four, against 1.2 ns for a fetched copy.
- `-ecscheck` v2 flips direction: it compares each cache with the ECS and reports a stale cache.

**Gate:** the standard gate; `CUO_FUNNEL_CHECK` 0; the UI-heavy scripts; a paired AOT `world_prepare` and `update` within noise in both renderers.

**Checkpoint after T4 (owner, 2026-10-31): measure before going further.**
- **What to measure** (paired NativeAOT, base = the last tip before T1 vs T4 on), in both renderers, at Britain, the bank and the large scene (zoom 1, 2.5, walking):
  - frame time and world prepare;
  - the update phase;
  - per-client memory (resident and managed heap, per memory-footprint.md);
  - the E2 packet bench.
- **Expectation:** neutral to a small gain. The measured wins so far came from drawing from the ECS (W3/W5/E5–E7). Ownership phases mainly buy one source of truth and simpler code, and the dual-write period costs a little (e.g. 0x77 +0.05 µs per packet).
- **If T4 is neutral or better:** continue to T9.
- **If it regresses frame time or memory beyond noise:** stop, and bring the numbers to the owner before T5.

**Status, T4 (2026-11-01):** on `bg/ecs-t4-scalars` (39868757f; poc-log "T4: thin handles"). `ecs_owns_scalars` (default off) makes `Name`, `Hits`, `HitsMax`, `Stamina`, `StaminaMax`, `Mana`, `ManaMax` and `Price` read the record through `Entity.TryScalarEntity` (the handle moved to `Entity`, plus an identity-checked `EcsEntityHeld`); an entity without a held record reads its cache. `Flags`, `Direction`, `NotorietyFlag`, `Amount`, `Container` and `Layer` stay caches: they have per-object per-frame readers (`CUO_SCALAR_READS=1` measures it). The pools joined `Vitals`; `-ecscheck` v2 compares the caches and reports a stale cache with the switch on. Tests pass in both variants and with the switch on. Gates (bench-000150/151) and E2 (bench-000152) pending; the checkpoint series is parked (quiet machine).

### T5: `World.Update` as systems

**T5a: E4 default on.** Audit and merge E10 (the owned `MobileTable`), then switch `world_update_mobile_system` on. It saves 0.02–0.03 ms in the large scene, and it is the base for T5b.
- Size: 1. Tier: Codex audit, Opus merge.
- Owner review item 2 already proposes this.

**T5b: the animation timer and the frame advance in the ECS.**
- Add an `AnimationTimer` component holding: group, interval, repeat mode and count, forward, from-server, frame count, `AnimIndex`, `LastAnimationChangeTime`, idle due, `ExecuteAnimation`.
- `MobileAnimationAdvanceSystem` runs the due mobiles without steps entirely in the ECS:
  - `AnimationTiming.Advance` (already pure);
  - the frame count from `AnimationGroupRules.Select` over `MobileInputs` + `EquipmentSlots`, for **every** mobile, drawn or not (the legacy loop advances offscreen mobiles too);
  - finishing and wrapping;
  - dying-mobile corpse removal through the funnel.
- Mobiles with steps or handles still call `Mobile.Update` until T6.
- `SetAnimation`, `SetIdleAnimation` and the four packet sites (PacketHandlers 0x6E, 0xAF, 0xBF.19, the frame-count packet, 0xE2; line list in poc-log E10) write the component through the funnel.
- **Status (2026-11-01, `bg/ecs-t5b-anim`): mobiles done** (`world_anim_system`, default off; gates bench-000161/162 pending). Items (`Item.ProcessAnimation`) are deferred to a follow-up phase T5b-items. The timer fields stay a write-through cache on `Mobile`: `AnimIndex` and `LastAnimationChangeTime` are read per frame, so the getters are not switched to the ECS (coordinator accepted).
- **Status (2026-11-01, `bg/ecs-t5b-items`): items done** (`world_item_anim_system`, default off; separate switch; only corpses animate in `Item.ProcessAnimation`; gates bench-000184/185 pending). See the poc-log entry.
- `Mobile.AnimIndex` and the rest become ECS-backed properties. This is E10's "move the clock with the advance".
- **Risk:** `GetGroupForAnimation`'s random fidget picks consume the shared RNG. The system must run in table order (E10) and draw in the same sequence, or the checker flags differences.
- **Items share the same `Entity` fields** (`Item.ProcessAnimation`: corpses, animated items). Do mobiles first and items second, in the same phase.
- Switch `world_anim_system`.
- **Checker `CUO_ANIM_ADVANCE_CHECK=1`:** each frame, for every due mobile, run the legacy advance on a shadow copy of its timer with legacy inputs, and compare frame, due times, group, repeat state, and corpse removal. Plus the E4 clock, skipped-is-no-op and order checks, `CUO_ANIM_SYNC`, and `-ecscheck`.
- Size: 4–6. Tier: O.

**T5c: the range sweep and the world-map pass as systems.**
- `RangeSweepSystem`: every 50 ms, over `Position`, using the last step's tile for stepping mobiles (`GameObject.Distance`'s rule). It removes through the funnel.
- The world-map pass is computed from `MobileInputs.Notoriety` plus the party list.
- Switch `world_sweep_system`.
- **Checker `CUO_SWEEP_CHECK=1`:** the set the system removes equals the legacy `CheckToRemove`/`Distance > ClientViewRange` set computed over all objects in the same frame, and the `WMapManager.AddOrUpdate` calls are equal.
- Must not change edge behaviour (ghost objects, perf-opportunities "Edge reconciliation").
- Size: 1.5–2. Tier: S+C.
- **Status (2026-11-01, `bg/ecs-t5c-sweep`):** built behind `world_sweep_system` (default off) with `CUO_SWEEP_CHECK`; scope is the E4 mobile loop and both item loops; see the poc-log entry. Default on after the off/on gates are clean and a Codex audit.
- **Merged 2026-11-01** (gates bench-000156/157 PASS after the Codex P2 fixes); default off; the default flip waits for a quiet-machine update-phase timing (parked).

**T5d: active items and the scheduler.**
- Active items become a system over an `ItemActive` tag that replaces `_activeItems`.
- `WorldSystems` replaces `World.Update`'s body with ordered stages:
  1. chunk loading;
  2. `ObjectToRemove`;
  3. steps (T6);
  4. the mobile clock and advance;
  5. the range sweep;
  6. active items;
  7. effects (T8);
  8. world text;
  9. the world-map pass;
  10. ECS bookkeeping.
- Each stage keeps its `FrameStats` phase.
- **Status (2026-11-01, bg/ecs-t5d-scheduler):** stage runner done (refactor); steps, clock/advance, sweep and world-map pass are one Mobiles stage until T6. The active item list is behind world_active_items_system (default off) with CUO_ACTIVE_ITEMS_CHECK; the ItemActive tag mirrors the list, which still holds the visiting order (the tag is not yet the source). See the poc-log entry.
- Size: 2–3. Tier: S+C.

### T6: tile order from the ECS; steps as a system

**Does the full transition need W2's query half?**
- **No.** It needs tile membership and order to be **derived from ECS position changes**, not maintained by GameObject code.
- It does not need an ECS query to replace the walk. W2 measured that query at about 0.15 ms at 21k objects, with a large parity risk.
- Keep W2's query as an optional performance item for the server-update-range work.

**T6a: shadow index and the order proof.**
- Stamp a global monotonic `TileSeq` at every `AddGameObject`.
- Compute the predicted order key `(PriorityZ, rank, ±seq)`:
  - rank 0 is land, with newest first (`-seq`);
  - rank 1 is a non-preview multi component, newest first;
  - rank 2 is everything else, oldest first (`+seq`).
- `PriorityZ` is the value **stored at insertion**: an item whose graphic changes without a re-insert keeps its old slot.
- **Checker `CUO_TILE_ORDER_CHECK=1`:** on every walked tile, the linked list must equal its sort by that key.
- This is the first task in section 6.
- Size: 2. Tier: O.
- **Result (2026-10-30, branch `poc/ecs-t6a-tile-order`, poc-log "T6a"): 0 out of order.** About 290M tile-list objects checked in Legacy and Gpu (plan spots, pickscan, travel, ltravel, glide, lswalk, `ls_parity`, `w5push`, `w5items`, `corpses` with a camp multi), plus 2G with the check on every frame. Unit tests cover each insertion rule and 3,000 random operations.
  - Rank 1 is any multi that is generic-internal, or not a preview (generic-internal wins over preview). Rank 2 includes preview multis.
  - Not exercised live: custom-house components, the customization preview and boats (unit tests only).
  - What T6b must keep: store (priority, rank, seq) at insertion, never recompute them from current fields (graphic and multi state change without a re-insert); stamp at the same point in update order as `AddGameObject`; compare with `TileOrderKey` (a struct: priority, rank, 64-bit seq; newest first for ranks 0 and 1).
  - Audit fixes (same branch): the sequence is 64-bit (a 32-bit wrap reversed two equal inserts); `GameObject.TileCell` stores the cell, and `RemoveFromTile` hands a departing `Chunk.Tiles` anchor to a neighbour, so landless tiles no longer lose their list or follow container links into another tile; the checker walks lists in full with cycle detection and checks membership both ways.
  - Re-audit fixes: movers (step completion, boats) re-add on placement ownership (`GameObject.IsPlaced`), not on having a list neighbour; `GetMapZ`/`CalculateNearZ` scan from the head on landless tiles only (from the land, as before, elsewhere); the checker also fails a tile whose anchor isn't in the walked list or that holds an object whose X/Y is elsewhere. T6b's index must keep these semantics: membership follows the stored cell, and a move re-files every placed object.

**T6b: `DynamicTileIndex`.**
- A per-chunk, per-tile small sorted array of `(key, entity id, GameObject handle)` for items, mobiles and (after T8) effects.
- The funnel's place, move and remove operations maintain it, and so does `Map.Park`'s deferred add.
- The static linked list keeps land, statics and multi components.
- The walk (`AddTileToRenderList` and `GameSceneGpuPrepare`'s masked walk) merges the static list and the dynamic array by key.
- Item cache served runs (W4/E7) and effects-alone (E6) iterate the merged order.
- Switch `world_tile_index` applies to both renderers at once.
- **Checkers:**
  - `CUO_TILE_ORDER_CHECK` extended: the merged order equals the legacy list while both are maintained;
  - a new `pickscan:8x6:world_tile_index` mode: the same grid, comparing the selection with the setting off and on instead of skipping off and on;
  - `CUO_BENCH_PICK` base vs new, in Legacy and Gpu;
  - `CUO_BENCH_SHOTS` at Britain, the bank, zoomed out, and inside and around houses, multis and the owner's custom house, in both renderers;
  - `CUO_GPU_ITEM_CHECK` (after E12), `CUO_PREP_CHECK`, settle, `travel`/`glide` and teleport.
- Size: 4–6. Tier: O.
- **Status (2026-11-01, branch `bg/ecs-t6b-tileindex`, poc-log "T6b"): built, default off; client gates submitted.** Both are maintained: items and mobiles stay in the tile list too, and with the switch on the walks skip them in the list and take them from the index (`TileWalk`). Dropping them from the list needs the list's other readers ported first (the follow-up the poc-log entry lists). **Part 3 (2026-11-02, branch `bg/ecs-t6b3-listdrop`, poc-log "T6b part 3"): built, default off; client gates submitted.** With the switch on the list drops items, mobiles and effects (the index is their only home); the anchor is always a land/static/multi object; `CUO_TILE_ORDER_CHECK` keeps a shadow list in the legacy order and compares against it. T9 can then remove `TNext`/`TPrevious` from dynamics. Effects join through `DynamicTileIndex.Holds` (T8).

**T6c: steps as a system.**
- `ProcessSteps` (interpolation offset, step completion, position, `AddToTile`, the player's walker bookkeeping) becomes `StepSystem` over a `Steps` component (a fixed ring of up to `MAX_STEP_COUNT`).
- Step completion moves the entity through the funnel, which updates the index at the same point in update order. That preserves the `ConfirmWalk` re-insert rule.
- Footstep sounds stay ordered by update order.
- Switch `world_step_system`.
- **Checker:** E4's skipped-is-no-op hash, extended to positions, offsets and step queues; `CUO_TILE_ORDER_CHECK`; `CUO_PREP_CHECK` (screen positions).
- Size: 2–3. Tier: O.
- **Status (2026-11-01, branch `bg/ecs-t6c-steps`, poc-log "T6c"): built, default off; client gates submitted (bench-000191..194).** `StepSystem` over the `Steps` component runs where ProcessSteps ran in `Mobile.Update` (table order kept). The offset interpolation is the system's; `Mobile.Offset`, the queue and `LastStepTime` stay a write-through cache for the views (hot-field rule). Checker `CUO_STEP_CHECK=1`. The mount inputs are still read from the Mobile.

### T7: Legacy

**Where full transition touches Legacy**, exactly:

| Legacy surface | Reads today | Change needed | When |
|---|---|---|---|
| Dynamic draw: `MobileView.Draw`, `ItemView.Draw`, `GameEffectView.Draw`, called from `RenderLists` (RenderLists.cs:447, :567) | GameObject fields, `FindItemByLayer`, the Mobile's animation fields | T1–T6: none (compatibility views). T7b: the neutral extract and a batcher sink | T7b |
| Dynamic picking: `MobileView`/`ItemView.CheckMouseSelection` | the `FrameInfo` and draw rect set during `Draw` | T7b: from the extract's side effects (W3 already reproduces FrameInfo, the mirror bit and the item picking offset) | T7b |
| The walk's tile iteration (`AddTileToRenderList`, shared) | the `TNext` list | T6b merge, in both renderers at once | T6 |
| Legacy-only render-list rules (alpha, foliage, max-Z, roof) | GameObject properties | none (compatibility views); hot fields cached | never forced |
| Land, statics, multi components, `MeshLayer`/`ChunkMesh` | chunk GameObjects | none (outside the ECS by decision) | never |
| Overheads, health bars, text (UI) | GameObjects | through the handles, like the UI | T4 |

**T7a, compatibility views (the default through T6).**
- Legacy keeps calling `Draw` on the GameObject. The GameObject reads ECS-backed properties, and the hot fields keep their write-through cache.
- Cost: the funnel's second write per change, plus one cached-entity lookup per cold read.
- Budget: Legacy world prepare plus Update within +3% of the T0 numbers at the large spot. Measure it at every phase.

**T7b, Legacy-on-ECS dynamics.**
- The least-risk "Legacy-on-ECS renderer" is not a new renderer. It is a second sink for the extract the Gpu path already trusts.
- **Split the extract** into two halves:
  - **select**, which produces a renderer-neutral `DynamicSprite` record: sprite kind (art or animation); art id or (body, group, dir, frame, palette); position; depth; hue mode and hue; alpha; flip; deform; light; picking rect;
  - **resolve**, which maps it to a backend: Gpu resolves to pack page and UV (`SpriteInstance`, as today); Legacy resolves to the FNA atlas texture and rect through the existing pack-backed `Art`/`Animations` loaders.
  - `ItemSpriteExtract.Emit` must also stop taking `Item`: it reads ECS components plus the cached handles.
  - This is the "keep the extract renderer-neutral" rule made concrete.
- **Hook point.** The Legacy walk calls the sink at the same render-list position where it calls `view.Draw` now, so painter order cannot change.
- **Picking.** Legacy picking reads the sink's recorded rects and `FrameInfo`.
- Switch `legacy_ecs_sprites`, like `gpu_ecs_sprites`.
- **Checker `CUO_LEGACY_ECS_PARITY=1`:**
  - capture the view's batcher calls (texture, source rect, destination, hue vector, depth, flip, sitting sections) and compare them with the sink's calls for every drawn dynamic, at day and night;
  - `pickscan:8x6:legacy_ecs_sprites` in Legacy;
  - Legacy screenshot parity off vs on (`-worldrenderer legacy`).
- Start condition: when T4's caches are to be deleted (T9), or when Item/Mobile slimming needs the views gone. That is the "blocks other work" trigger in the owner's rule (see Q2).
- Size: 7–11. Tier: O.

**Not proposed.** A full Legacy renderer that draws `SpriteInstance`s with pack pages on FNA (instanced, with an Indexed8 palette shader on D3D11/OpenGL through MojoShader). It duplicates the Gpu renderer on the weakest backends, and its parity surface is the whole world, not just dynamics.

### T8: effects as ECS entities

- `GameEffect`, `MovingEffect`, `FixedEffect`, `DragEffect` and `LightningEffect` are created by packets (0x70, 0xC0, 0xC7) and advanced by `_effectManager.Update`.
- What changes:
  - an `EffectState` component (kind, graphic, source and target, animation, blend, duration, position);
  - `EffectSystem` advances them;
  - their tile placement goes through `DynamicTileIndex` (priority +2);
  - the extract emits them (E6 found capture costs about 0.09 µs, so this is for ownership, not speed).
- Switch `ecs_effects`.
- **Checker `CUO_EFFECT_CHECK=1`:** per frame, compare each effect's position, frame, alive state and tile with a shadow legacy effect.
- Gate: E12's same-frame effect geometry, blend and depth check; screenshots with spell effects at night.
- Lightning stays an FNA overlay.
- Size: 4–6. Tier: S+C draft, O review.
- **Before effect motion moves to the ECS (phase 2):** an item's boat offset is ECS-owned (owner, 2026-11-07; branch `bg/ecs-item-offset`, poc-log "ecs_item_offset"): the `ItemOffset` component, written by the boat operations, switch `ecs_item_offset`, checker `CUO_ITEM_OFFSET_CHECK`. Phase 2 reads it for an effect whose source or target is an item on a boat (`FixedEffect` copies its source's `Offset` today).
- **Phase 2 (2026-11-07, branch `bg/ecs-effect-motion`, poc-log "T8 ownership phase 2"): built, default off.** The kinds' motion runs as `EffectSystem` steps over `EffectState` (switch `ecs_effect_motion`, needs `ecs_effects` and `ecs_effect_endpoints`; checker `CUO_EFFECT_MOTION_CHECK` replays the legacy motion at every update; item sources' offsets from `ItemOffset`). The effect's fields are a write-through cache for the views until phase 3 moves the extract.

### T9: deletion (after each switch has been on by default for one phase)

**Delete early, path by path (owner, 2026-10-31).** Don't wait for the end. When a path's ECS version has held its gate, been on by default for one phase, and had its Codex audit, delete that path's non-ECS version in the next phase. That keeps the period with two models as short as possible.

One limit: a write path can only go once nothing reads the old field. The T1 dual writes keep the legacy cache current because T1–T3 still read it. Those writes go when T4 moves the reads, for cold fields, and after T7b for hot ones.

Candidates as of 2026-10-31: E4's legacy mobile loop and its shadow dictionary (on by default since 6b26dd773, eligible after one more phase), and the `*PreT1` release fallbacks once T1 is complete.


- `WorldEcs.Sync`, `PushInputs` and `PushClock` as separate entry points (they become funnel internals), and the mirror-direction `-ecscheck` (replaced by the cache check, then removed with the caches).
- The cold A fields' caches on `Entity`/`Item`/`Mobile`/`PlayerMobile` (T4). The hot caches only after T7b. **Done (2026-11-07, `bg/cold-caches`):** Name, Hits, HitsMax, the pools and Price are record-only, `ecs_owns_scalars` is gone, the funnel's hits operations set `HitsPercentage`, and a record replaced by the other kind under one serial starts without them (owner decision; poc-log "T9: the cold scalar caches deleted").
- The `LinkedObject` child lists for containers (T2) and equipment (T3). `Entity.Items` becomes an ECS enumeration.
- `TNext`/`TPrevious`/`TileChunk` for dynamic objects (T6). They stay for land and statics.
- `World.Items`/`World.Mobiles` dictionaries become the mirror's serial index plus E10's ordered table, which update order needs.
- The legacy mobile loop, `UpdateAllItems`, E4's shadow dictionary, and the off paths of:
  - `world_update_active_items`, `mobile_push_inputs`, `world_update_mobile_system`, `gpu_mobile_frame_cache` (if still kept as a switch);
  - the `gpu_ecs_sprites` capture path for dynamics;
  - `legacy_ecs_sprites` off, then the bodies of `MobileView.Draw`, `ItemView.Draw` and `GameEffectView.Draw` and the batcher capture hook for dynamics;
  - the effect views after T8.
- Checkers that compare against deleted code (`CUO_ANIM_SYNC`'s view capture, `CUO_LEGACY_ECS_PARITY`, `CUO_CONTENTS_CHECK`, `CUO_EQUIP_READ_CHECK`). Before deleting them, save golden captures and screenshots so parity can still be checked against references (the owner's "saved reference shots" rule).
- Everything marked `CLEANUP(release)` that these phases touch (release-cleanup.md).

## 4. UI and gump readers

- Most gumps hold a **serial** (`LocalSerial`) and look the object up each update:
  - `HealthBarGump`, `PaperDollGump`, `ContainerGump`, `StatusGump`, `NameOverheadGump`, `TradingGump`, `PartyGump`, `WorldMapGump`, `MiniMapGump`, `QuestArrowGump`, `CounterBarGump`.
  - That fits the ECS: the lookup becomes the mirror's serial index. They keep getting a handle object through T4, so they need no edits until T9.
- **Readers that walk child lists** (`ContainerGump`:548/710, `GridLootGump`, `SpellbookGump`, counters, `FindItem*`) move in T2 and T3.
- **`TargetManager`, `PartyManager` and `JournalManager`** store serials and strings, not GameObject references, and are unaffected.
- **Managers that hold object references across frames** (`SelectedObject`, `MapObjectRecycler` holders, `DelayedObjectClickManager`, `UoAssist`) need an audit in T0a. A handle that outlives its entity must read as destroyed, which the epoch check gives.
- **Stats and skills on `PlayerMobile`** are session data, not world-object state. Keep them in a typed `PlayerState` store (Q4). Moving them into ECS components buys nothing.
- **Gump tooltips for unknown serials** (owner requirement, perf-opportunities): OPL stays keyed by serial alone, independent of entity existence. No phase may tie tooltip storage to the ECS entity.

## 5. Plugins

The C ABI (`src/ClassicUO.Client/Network/Plugin.cs`) exposes:
- packet recv/send hooks, and injection both ways (`OnPluginRecv`/`OnPluginSend`);
- `GetPlayerPosition` (reads `World.Player.X/Y/Z`), `RequestMove` (`Player.Walk`), `OnPlayerPositionChanged`;
- hotkeys, mouse, `ProcessDrawCmdList`, `WndProc`, the window title, `Tick`.

**No GameObject crosses the ABI.**
- Injected packets go through `PacketHandlers`, so they go through the funnel and keep working.
- `GetPlayerPosition` reads the handle's hot fields.

**What breaks:**
- In-process C# assistants loaded through `ClassicUO.Bootstrap` that reach into ClassicUO internals with reflection (field names on `World`, `Item`, `Mobile`, gumps). They break at T4/T9 when fields become properties or disappear, as they already do between ClassicUO versions.
- Packet-only assistants (Razor's own world model) keep working until the plugin loader itself is removed.
- The owner has already ruled out a plugin API, and the target is a standalone AOT exe. Recommendation: keep the C ABI working through the transition at no extra cost, and make no promise of reflection compatibility (Q3).

## 6. First tasks that can start now (no owner input needed)

**Task 1: T0a + T0b, state census and write-site budget test** (S+C, 1.5–2 days, no client run).
- **Goal:** the classification table (A–E) for every member of `GameObject`, `Entity`, `Item`, `Mobile`, `PlayerMobile` and `MobileAnimation`; every write site of each A field (file:line); readers split hot and cold; managers that hold references across frames.
- **The test:** an xUnit test that counts direct A-field writes outside an allowlist and fails on growth.
- **Files:**
  - read `src/ClassicUO.Client/Game/GameObjects/*.cs`, `Game/World.cs`, `Network/PacketHandlers.cs`, `Game/Ecs/WorldEcs.cs`, `Game/UI/**`, `Game/Managers/**`;
  - write `docs/modern-client/ecs-state-census.md`, `tests/ClassicUO.UnitTests/Ecs/WriteSiteBudgetTests.cs` and an allowlist file.
- **Checkers:** the new test passes on the current tree; no product change, so tests pass in both `CuoDevTools` variants.
- **Gate:** Codex audit of the census for completeness. It samples 20 random write sites by grep and confirms each is listed.

**Task 2: T6a, tile-order proof** (O, about 2 days, client runs).
- **Goal:** prove or refute that one sort key reproduces every tile list.
- **Changes:**
  - add `uint TileSeq` on `GameObject`, stamped from a global counter in `Chunk.AddGameObject`;
  - add `TileOrderKey(obj)`: rank 0 land (newest first), rank 1 `Multi` with state 1 (newest first), rank 2 everything else (oldest first), all within `PriorityZ` as stored;
  - add the checker `CUO_TILE_ORDER_CHECK=1`: every N frames, and on every tile the walk visits, the list equals its stable sort by the key. Log `[tile-order] tiles N, objects M, out of order K` and the first 25 offenders, with kinds, priorities and sequence numbers.
  - The field must also be reset in `MapObjectRecycler` (its field-reset test enforces that).
- **Files:** `src/ClassicUO.Client/Game/Map/Chunk.cs`, `Game/GameObjects/GameObject.cs`, a new `Game/Map/TileOrder.cs`, `Game/Map/MapObjectRecycler.cs`, unit tests for the insertion rules (land, multi, preview multi, equal priorities, re-insert, graphic change without re-insert).
- **Runs:** `ls_parity`, `lswalk`, `w5push`, `w5items`, `travel`/`glide` and teleports, a house and multi spot, Britain and the bank, in Legacy and Gpu, with `CUO_RECYCLE_CHECK=1`.
- **Gate:** 0 out of order, or a written list of the exceptions with their cause. That list decides T6b's design. No behaviour change: pickscan and the screenshots are unchanged by construction, but run pickscan once anyway.

**Task 3: E10 audit and merge, then the E4 default-on proposal** (Codex audit, then Opus merge, about 1 day).
- **Goal:** merge `bg/ecs-e10-clock` (the owned `MobileTable`) after a Codex gpt-6-astra audit of its five invariants, especially enumeration equivalence with `Dictionary` and the 0xBB re-key.
- Then run the E4-on gate on the merged tip, and write the default-on proposal into owner-review.md. Turning it on stays the owner's call: owner-review item 2 says "I'll propose".
- **Files:** `src/ClassicUO.Client/Game/GameObjects/MobileTable.cs`, `Game/World.cs`, `tests/.../MobileTableTests.cs`.
- **Checkers:** `CUO_MOBILE_UPDATE_CHECK=1` (clocks, skipped, order, table-vs-dictionary) plus the standard gate set, at day and night; NativeAOT once, since E10 didn't run it; the char-creation preview mobile path once.
- **Gate:** all checkers 0, 582+ tests pass, and `world_mobiles` off equals base within noise.

Task 1 and Task 2 touch different files and can run in parallel. Task 3 touches `World.cs`, which Task 1 only reads.

## 7. Memory and performance expectations, and what to measure

**Expectations:**
- **ECS footprint today.** An item entity is about 20 bytes of components plus a Friflo entity node, an index entry and the serial dictionary entry, roughly 60–80 B in total. A mobile adds `EquipmentSlots` (25 slots × 16 B plus extras, about 0.45 KB), `AnimationState`, `MobileInputs`, `MobileRenderState` and `MobileClock`, about 0.55 KB in total. In the large scene (25k items, 2k mobiles) that is about 3 MB, next to the GameObjects it duplicates.
- **During T1–T6** memory grows slightly: a dual write per change, child-order stamps and the tile index, about 16 B per dynamic object.
- **After T9** every item and mobile loses its cold fields and its child and tile links. Expect a net saving of a few hundred bytes per object. Only measurement will say how much.
- **Packet path.** The funnel replaces `Sync`, which was a second pass, so the cost per packet should stay within E2's 0.02–0.26 µs. Budget: +0.1 µs and 0 B.
- **Frame:**
  - T5a saves 0.02–0.03 ms in the large scene;
  - T5b should remove most of E4's remaining 0.014 ms of due updates (about 325 ns each, mostly the legacy `Update` and dictionary lookups);
  - T6b is neutral to slightly positive in the walk (contiguous dynamic arrays instead of pointer chasing; W0 found the bare list walk at 2.2–2.4 ms at 4K);
  - T4 should be neutral in Gpu, and must stay within +3% in Legacy.
  - None of this is a big frame win at normal scale. The value is the architecture, and scaling when mobile counts grow (perf-opportunities, "World update as ECS systems").

**Measurements per phase.** All paired, NativeAOT plus JIT with TieredCompilation=0, interleaved, quiet machine:
1. Managed heap after login and in the large scene: `GC.GetGCMemoryInfo().HeapSizeBytes` after a forced full collection, from a new DevBench `mem` plan entry. Also Friflo store memory (archetype chunk capacity × component sizes) and counts per archetype. Report bytes per item and per mobile.
2. `FrameStats` phases: `world_mobiles`, `world_items`, `world_effects_text`, `world_ecs`, `world_prepare` and its sub-phases, draw and frame. Spots: Britain, the bank, zoomed out at 1080p and 4K, the large scene at zoom 1, 2.5 and walking, in Legacy and Gpu.
3. Packet cost: E2's `EcsPacketCostBench`, extended to the funnel's operations, including 2,000 mobiles moving and changing equipment. µs per packet and allocated bytes.
4. Allocation per frame (0 in steady state) and GC count over the 13-minute session (6 collections, 25 ms today).
5. Teleport worst frame and `travel`/`glide` Update+Draw max, to catch funnel or index cost on bulk arrival.

## 8. Risks

| Risk | Where | Mitigation |
|---|---|---|
| Tile order differs (max-Z cut-off, equal-depth painter order) | T6 | T6a proves the key before any switch; the shadow index plus `CUO_TILE_ORDER_CHECK` while both exist; pickscan with a setting toggle; screenshots in houses and multis; Opus only |
| Picking regressions | T6, T7b | Picking stays the same `PixelPicker` code; Legacy picking reads the same FrameInfo the W3 extract already reproduces; `CUO_BENCH_PICK` base vs new in both renderers |
| A missed write site (stale ECS or stale cache) | T1, T4 | `CUO_FUNNEL_CHECK`, the write-site budget test, `-ecscheck` v2 in both directions |
| Update order changes observable effects (tile insertion, footsteps, RNG fidgets) | T5, T6c | E10's owned table; systems run in table order; the order checker; the RNG sequence covered by `CUO_ANIM_ADVANCE_CHECK` |
| Friflo pitfalls: structural changes during iteration, indexed components needing a re-add, unregistered components under AOT, 11-argument `CreateEntity` | all | Collect then mutate (the E4 pattern) or command buffers; register every component in `EcsSchema` (an E4 startup failure taught this); an AOT smoke run per phase |
| Handles outliving entities (serial reuse, the 0xBB re-key, death re-key, recycler) | T4 | The epoch check (E5); a destroyed handle reads defaults and `IsDestroyed`; `CUO_RECYCLE_CHECK` |
| Legacy slows down through compatibility views | T4–T6 | Hot-field caches; a +3% budget measured every phase; T7b removes the views' reads |
| Edge-of-range ghosts or lost mobiles | T5c | The sweep is shadow-checked against the legacy set; no edge-rule change without its own switch (Q6) |
| Container order or click order changes in gumps | T2 | The child-order stamp; `CUO_CONTENTS_CHECK`; screenshots of containers |
| Checkers deleted with the legacy code they compare against | T9 | Golden captures and reference screenshots saved before deletion |
| Long dual-state period costs memory and confusion | T1–T7 | Each switch's off path is deleted one phase after it defaults on |
| Parallel agents collide in `GameObject`/`World`/`PacketHandlers` | all | Phases are serialised in those files; T2/T3/T5c/T8 can run beside T4/T6 because they touch different files |

## 9. Owner questions (each with a recommended answer)

1. **Scope of "fully on the ECS":** does it include land, statics and multi components?
   *Recommended:* no. Keep the decision in ecs-decision.md: the static world stays in chunk data and GPU buffers. "Fully on the ECS" means every dynamic world object (items, mobiles, effects) and the world update.
2. **Legacy:** compatibility views through T6, then T7b (Legacy-on-ECS for dynamics through the shared neutral extract and a batcher sink), with Legacy keeping its own land and static path.
   *Recommended:* approve T7b as the point where "it blocks other work" applies. Deleting the GameObject caches (T9) needs it. The alternative is keeping the hot caches and the views indefinitely, which means dual state.
3. **Plugins:**
   *Recommended:* keep the C ABI (packets, player position, `RequestMove`) working through the transition. Make no reflection-compatibility promise for in-process assistants. Remove the loader with the release cleanup, as already decided (no plugin API).
4. **Player session data** (stats, skills, buffs, abilities, OPL, journal):
   *Recommended:* a plain typed `PlayerState` store, not ECS components. The ECS holds world objects.
5. **Switch policy:**
   *Recommended:* default a switch on when its checkers are 0 over the standard scripts and a Codex audit is done; delete its off path one phase later. Turn E4 on after E10 merges (Task 3).
6. **Parity or fixes:** when the transition exposes legacy quirks, should it keep them?
   - Examples: equipment arriving before its parent is never attached; the 0x78 re-creation drops OPL; RNG-order fidgets; edge ghosts.
   - *Recommended:* reproduce legacy behaviour exactly in each phase, and fix quirks separately behind their own switches with their own checkers.
7. **The end state of `Item`/`Mobile`:** thin handle classes (one per entity) or no classes (struct handles everywhere)?
   *Recommended:* thin handle classes. Removing the classes means editing about 70 files and about 360 declarations for little gain. Revisit after T9.
8. **W2's ECS query** (in-view iteration):
   *Recommended:* not part of the transition. T6's index preserves tile order. Pick the query up only with the server-update-range work if a range-bounded walk needs it.
9. **Effects (T8):**
   *Recommended:* move them, but last. It's for ownership consistency, not speed (E6). Lightning stays an overlay.

## 10. Order and parallelism

1. Now: Task 1 (T0a/b), Task 2 (T6a) and Task 3 (E10 merge, E4-on proposal).
2. Then T0c, then T1, group by group. T5c can run beside T1, since it lives in `World.Update` and the sweep.
3. Then T2 and T3 in parallel (UI readers), and T5b (Opus) in parallel with them.
4. Then T4. T6b/T6c follow T4 and T5b (Opus, serial in the walk files).
5. Then T8, then T7b once Q2 is answered, then T9 in slices after each switch's grace phase.
