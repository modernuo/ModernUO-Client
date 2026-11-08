# ECS state census (T0a) and the write-site budget (T0b)

Task 1 of `ecs-transition-plan.md` section 6. Taken 2026-10-30 on `bg/ecs-t0-census`, based on `poc/modern-client` at 3681f25b3. No product code changed. This file is the design input for T0c (encapsulation), T1 (the funnel) and T4 (thin handles).

It answers four questions:
1. What kind of state is every member of `GameObject`, `Entity`, `Item`, `Mobile`, `PlayerMobile` and `MobileAnimation`? (section 2)
2. Who writes each world-state field, and who reads it, hot or cold? (section 3)
3. Which methods write world state or the structure lists on the callers' behalf? (section 4)
4. Which objects hold `Item`/`Mobile`/`Entity`/`GameObject` references across frames? (section 6)

Section 7 and the budget test (section 8) say how complete the lists are and where the scanner is weak.

## 1. How the lists were made

The write and read lists are **compiler-derived**, not grep-derived. A grep cannot tell `item.X = 1` from `control.X = 1`, and `X` and `Y` have about 1,000 textual write hits in the client, almost all UI geometry.

Method (one-off, from the base commit; the sources were reverted afterwards):
1. In a scratch copy of the working tree, each A member was renamed at its declaration (`Graphic` to `Zz_Graphic`, and so on) in `GameObject.cs`, `Entity.cs`, `Item.cs`, `Mobile.cs` and `PlayerMobile.cs`. The 42 members of section 3 (41 A members and `Title`) were renamed in the first pass. The mutator methods, the structure members (`PriorityZ`, `TNext`, `TPrevious`, `TileChunk`, `PendingBlock`, `Steps`, `Items`) and the `LinkedObject` list API were renamed in a second pass (section 4).
2. `dotnet build src/ClassicUO.Client -c Release` was run. Every reference to a renamed member is a compile error with `file(line,col)` and the **static type of the receiver** (`'Item' does not contain a definition for 'Graphic'`), which resolves what a regex cannot. The first pass produced 2,576 member references and no unexplained errors that hid a reference.
3. Each reference was classified from the source text at the error column: assignment, compound assignment, `++`/`--`, `ref`/`out` argument, assignment to a member of the struct (`Offset.X = `), or a read. The `ref`/`out` and ternary-condition false positives were removed by hand.
4. Cross-check: the textual hits of `.Field op` over the whole client were compared with the compiler's write set. Every textual hit that is not in the write set is on a non-world receiver (UI controls, `Point`, `RealScreenPosition`, structs); they are listed in section 7 so a grep sample can be traced to a row here.

The tools (`instrument.py`, `analyze.py`, `classify.py`, `gen.py`) were scratch scripts and are not committed. To regenerate after the sources move on, repeat steps 1 to 3; the budget test (section 8) is the committed guard that keeps the list from going stale in the meantime.

**Conditional compilation.** The census was taken on the default build (`CuoDevTools=true`). Code under `#if !CUO_DEVTOOLS` is a subset, and the only other `#if` regions in the client (`DEV_BUILD`, `FEISTEL`, `NETFRAMEWORK`, `!DEBUG`) hold no world-state writes.

## 2. Classification of every member

Classes:
- **A** world state: moves to the ECS (sections 3 and 4 list its writers and readers).
- **B** derived: recomputed from A or from tile data; no storage needed.
- **C** per-frame view scratch: written by the render walk or the views; never persisted.
- **D** session or UI data: not world-object state (stays in plain typed stores, plan Q4).
- **E** structure: tile lists, child lists, recycler and table bookkeeping.

"hot/cold" is the access pattern of the **readers** (section 3 has the numbers). Methods are listed under "Behaviour" with the A fields they write; they carry no class of their own.

### 2.1 `BaseGameObject`, `GameObject` (and the `GameObject` part in `Views/View.cs`)

| Member | Class | Notes |
|---|---|---|
| `World` | E | owner reference, never changes |
| `RealScreenPosition` | C | set by the world prepare each frame (`UpdateRealScreenPosition`) |
| `ScreenPositionGen` | C | camera generation of the last prepare |
| `IsPositionChanged` | C | dirty flag for the text position; written by `UpdateScreenPosition`, `SetInWorldTile`, `AddMessage` |
| `IsDestroyed` | E | lifecycle flag (`Destroy`, `ResetForReuse`); T4 maps it to entity liveness |
| `TextContainer`, `InTextOwners` | D | overhead text list; `World.TextOwners` membership |
| `_averageOverTime` | D | the DPS meter (`AddDamage`) |
| `_textExpiredContainer`, `_textExpiredVersion` | C | cache for `TextAllExpired` |
| `FoliageIndex` | C | foliage transparency slot, set by the walk |
| `Graphic` | **A** hot | also read by `Land`/`Static`/`Multi`/effects (map objects, not ECS) |
| `Hue` | **A** hot | same |
| `X`, `Y`, `Z` | **A** hot | for a contained item `X`/`Y` are the container grid position, not a map position (T2) |
| `Offset` | **A** hot | interpolation offset of a walking mobile or moving boat part; B once steps are a system (T6c); an item's (the boat's, a deck item's) is the `ItemOffset` component with `ecs_item_offset` (2026-11-07); a boat's multi components keep the field (not world objects) |
| `PriorityZ` | E | tile order key, stored at insertion (`Chunk.AddGameObject`) |
| `TNext`, `TPrevious`, `TileChunk`, `PendingBlock` | E | tile list links, the chunk, the parked block (`Map.Park`) |
| `InRecycler`, `Incarnation` | E | `MapObjectRecycler` |
| `AlphaHue`, `InChunkMesh`, `MeshSpriteIndex`, `DrawnFrame`, `FrameInfo`, `IsFlipped` (`ViewFlipped`) | C | view scratch (`Views/View.cs`) |
| `AllowedToDraw` | C | assigned from `CanBeDrawn(Graphic)` by `CheckGraphicChange`, so B in spirit; kept as a view flag |
| `ObjectHandlesStatus` | D | UI toggle for the name-overhead handle; also read by `NeedsFrameUpdate` |
| `Distance` | B | from `X`/`Y`/last step and `World.RangeSize` |
| Behaviour | | `AddToTile` x3, `RemoveFromTile`, `UpdateScreenPosition`, `SetInWorldTile` (writes X, Y, Z), `Destroy` and `ResetForReuse` (write Graphic, Hue, Offset, X, Y, Z, PriorityZ), `UpdateRealScreenPosition(Only)`, `AddMessage` x3, `UpdateTextCoordsV`, `FixTextCoordinatesInScreen`, `TextAllExpired`, `AddDamage`, `GetCurrentDPS`, `GetScreenPosition`, `CanBeDrawn`, `CalculateDepthZ`, `GetOnScreenRectangle`, `TransparentTest`, the `Draw*` helpers |

### 2.2 `Entity`

| Member | Class | Notes |
|---|---|---|
| `Serial` | **A** cold (identity) | the key of the mirror; written by `Create` and by the 0xBB re-key (`owner.Serial = serial`) |
| `Name` | **A** cold | |
| `Flags` | **A** hot | the mirror's `Flags` component; `IsHidden`, `IsParalyzed` and others derive from it |
| `Direction` (`_direction`) | **A** hot | setter calls `OnDirectionChanged` and `World.Ecs.Sync` |
| `Hits`, `HitsMax` (`_hits`, `_hitsMax`) | **A** cold | setters refresh `HitsPercentage` |
| `HitsPercentage` | B | `HitsPercent(Hits, HitsMax)`; its only writer is `UpdateHits`, which only `RefreshHitsPercentage` calls (section 4) |
| `AnimIndex` | **A** hot | animation frame; written by the animation advance (`ProcessAnimation`) and three packet handlers |
| `ExecuteAnimation` | **A** hot | |
| `LastAnimationChangeTime` | **A** hot | animation clock (E4's `MobileClock` mirrors it) |
| `LastStepTime` | **A** hot | step clock; `IsWalking` derives from it |
| `IsClicked` | D | UI click flag |
| `HitsRequest` | D | status-bar request state machine |
| `_hitsPercText` | C | static cache of rendered percentage texts |
| `IsHidden`, `Exists`, `HitsTexture` | B | |
| Behaviour | | `FixHue` (writes Hue), `UpdateHits`, `CheckGraphicChange` (virtual), `Update`, `Destroy` (writes AnimIndex, LastAnimationChangeTime), `FindItem`, `GetItemByGraphic`, `FindItemByLayer` (walk the `Items` child list), operators, `ProcessAnimation`, `GetGraphicForAnimation` (abstract) |

### 2.3 `Item` (and `Views/ItemView.cs`)

| Member | Class | Notes |
|---|---|---|
| `Amount` | **A** cold | also the corpse's body graphic (`GetGraphicForAnimation`) |
| `Container` | **A** cold | serial of the parent item or mobile, `0xFFFF_FFFF` on the ground |
| `Layer` | **A** cold | for a corpse it is the death direction (`ProcessAnimation`) |
| `Price` | **A** cold | vendor list |
| `IsDamageable` | **A** cold | wave 1C: the record's `ItemTraits` (`ecs_small_fields`) |
| `LightID` | **A** hot | the lights rule reads it (`CUO_ECS_PARITY`); wave 1C: `ItemTraits` (`ecs_small_fields`) |
| `UsedLayer` | **A** cold | corpse flag (running flag of the death direction); wave 1C: `ItemTraits` (`ecs_small_fields`) |
| `IsMulti` (`_isMulti`) | **A** cold | multi flag; the setter resets `MultiInfo`, `MultiDistanceBonus`. The record's `MultiTag` (every item commit); the getter stays a write-through cache: `ItemData` reads it per drawn item per frame (wave 1C) |
| `DisplayedGraphic` | B | wave 1C: a computed getter (coin stack and multi graphic from `Graphic`, `Amount`, `IsMulti`), no field and no setter; 15 readers |
| `MultiGraphic`, `MultiDistanceBonus`, `MultiInfo` | B | derived by `LoadMulti` from `Graphic` and the multi data; `WantUpdateMulti` is the lazy-load flag |
| `WantUpdateMulti` | E | lazy-load flag for `LoadMulti` |
| `Opened` | D | the container gump is open |
| `InUpdateList` | E | membership of `World._activeItems` (`QueueItemUpdate`) |
| `NeedsFrameUpdate`, `IsCoin`, `IsLocked`, `IsCorpse`, `OnGround`, `RootContainer`, `ItemData`, `IsLootable` | B | |
| `_equipConvData` (view) | C | static cache |
| Behaviour | | `Create`, `Destroy`, `LoadMulti` (writes MultiGraphic, MultiInfo, MultiDistanceBonus, builds `Multi` objects), `CheckGraphicChange` (writes AnimIndex, UsedLayer, Layer, Direction for a corpse, AllowedToDraw), `Update`, `ProcessAnimation` (writes AnimIndex, LastAnimationChangeTime), `GetGraphicForAnimation`, `UpdateTextCoordsV`, `GetTotalAmount` |

### 2.4 `Mobile` (and `MobileAnimation.cs`, `Views/MobileView.cs`)

| Member | Class | Notes |
|---|---|---|
| `Race`, `IsFemale` | **A** cold | `IsFemale` is a pushed input (`MobileInputs`); both are also set by `CheckGraphicChange`; wave 1C: the record's `MobileTraits` (`ecs_small_fields`) |
| `IsRunning` | **A** hot | pushed input; written by the step system and the move packets |
| `NotorietyFlag` | **A** hot | pushed input (hue override); 69 reads |
| `IsDead` (`_isDead`) | **A** hot | pushed input; the getter also derives from `Graphic` |
| `InWarMode` | **A** | virtual: `Mobile` derives it from `Flags.WarMode` and ignores the setter; `PlayerMobile` stores `_inWarMode`; pushed input; wave 1C: the player's is the `MobileTraits` WarMode bit (`ecs_small_fields`) |
| `_isSA_Poisoned` (`SetSAPoison`, `SAPoisoned`) | **A** cold | pushed input |
| `Stamina`, `StaminaMax`, `Mana`, `ManaMax` | **A** cold | status packets |
| `IsRenamable` | **A** cold | wave 1C: `MobileTraits` (`ecs_small_fields`) |
| `SpeedMode` | **A** cold | player only in practice (0xBF.26); wave 1C: `MobileTraits` (`ecs_small_fields`) |
| `Steps` | **A** hot | `Deque<Step>` of the walk queue; mutated by `EnqueueStep`, `ClearSteps`, `ProcessSteps` and the `Steps.*` calls of section 4; becomes the `Steps` component (T6c) |
| `AnimationFrameCount`, `AnimationFromServer` | **A** hot | animation timer fields |
| `_animationGroup`, `_animationInterval`, `_animationRepeat`, `_animationRepeateMode`, `_animationRepeatModeCount`, `_isAnimationForwardDirection` | **A** hot | animation timer fields, private; written only inside `Mobile` (section 5) |
| `_lastAnimationIdleDelay` (`IdleDue`) | **A** hot | idle due time; `CalculateRandomIdleTime` draws from the shared RNG |
| `LastStepSoundTime`, `StepSoundOffset` | **A** hot | footstep clock; written by `ProcessFootstepsSound` |
| `Title` | D | display title from the name packet; one writer (PacketHandlers.cs:3079) |
| `FrameCache` | C | `MobileFrameCache`, the Gpu world's reused instances |
| `EcsEntity`, `EcsEntitySerial`, `EcsEntityEpoch` | E | cached ECS handle (E5) |
| `Ordinal`, `OwnerTable` | E | slot in `MobileTable` (E10) |
| `UpdateStamp` | C | E4 checker |
| `_surfaceOverheadCache`, `_surfaceOverheadCacheX`, `_surfaceOverheadCacheY`, `_surfaceOverheadCacheMaxZ` | C | per-mobile cache of the overhead surface test |
| `_animationIdle` (static) | B | fidget table |
| `IsParalyzed`, `IsYellowHits`, `IsPoisoned`, `IgnoreCharacters`, `IsFlying`, `IsHuman`, `IsGargoyle`, `IsMounted`, `IsDrivingBoat`, `IsWalking`, `SAPoisoned`, `IdleDue` | B | from `Flags`, `Graphic`, the equipment (`FindItemByLayer`) and the clocks |
| `MobileAnimation.cs` (all members) | B | `GetGraphicForAnimation` (= `MobileInputRules.AnimationBody(Graphic)`), `GetDirectionForAnimation`, `GetGroupForAnimation`, `GetAnimGroupInput`, `AnimationGroupRaw` (reads `_animationGroup`), `IsReplacedObjectAnimation`, `GetReplacedObjectAnimation`, `GetObjectNewAnimation` and its 12 `Type_N` helpers, `TestStepNoChangeDirection`: pure functions of A state; `AnimationGroupRules` already holds the ECS-side copy |
| `MobileView.cs` statics (`SIT_OFFSET_Y`, `_equipConvData`, `_characterFrame*`, `_startCharacter*`) | C | draw scratch |
| Behaviour | | `Update`, `SetAnimation`, `SetIdleAnimation` (write the whole animation timer), `ProcessAnimation` (writes AnimIndex, AnimationFrameCount, the repeat fields, LastAnimationChangeTime), `ProcessSteps` (writes X, Y, Z, Direction, IsRunning, Offset, LastStepTime, calls `AddToTile`), `EnqueueStep`, `ClearSteps`, `CheckGraphicChange` (writes IsFemale, Race), `Destroy`, `TryGetSittingInfo`, `GetSecureTradeBox`, `PushInputs`, `PushClock`, `UpdateStateHash` |

### 2.5 `PlayerMobile`

| Member | Class | Notes |
|---|---|---|
| `Strength`, `Dexterity`, `Intelligence`, `StrengthIncrease`, `DexterityIncrease`, `IntelligenceIncrease`, `StrLock`, `DexLock`, `IntLock`, `StatsCap`, `Luck`, `Gold`, `Weight`, `WeightMax`, `Followers`, `FollowersMax`, `TithingPoints`, `DeathScreenTimer` | D | status packet data |
| `ColdResistance`, `EnergyResistance`, `FireResistance`, `PhysicalResistance`, `PoisonResistance` and the five `Max*Resistence` fields | D | |
| `DamageIncrease`, `DamageMin`, `DamageMax`, `DefenseChanceIncrease`, `MaxDefenseChanceIncrease`, `EnhancePotions`, `FasterCasting`, `FasterCastRecovery`, `HitChanceIncrease`, `HitPointsIncrease`, `HitPointsRegeneration`, `LowerManaCost`, `LowerReagentCost`, `ManaIncrease`, `ManaRegeneration`, `MaxHitPointsIncrease`, `MaxManaIncrease`, `MaxStaminaIncrease`, `ReflectPhysicalDamage`, `SpellDamageIncrease`, `StaminaIncrease`, `StaminaRegeneration`, `SwingSpeedIncrease` | D | |
| `Skills`, `BuffIcons` (`_buffIcons`), `PrimaryAbility`, `SecondaryAbility`, `Abilities` | D | |
| `AutoOpenedCorpses`, `ManualOpenedCorpses` | D | |
| `Walker`, `Pathfinder` | D | session objects (walk bookkeeping, path search) |
| `_serverDirection`, `_serverDirectionInitialized` | D | the direction last sent in a walk packet |
| `InWarMode` (override, `_inWarMode`) | **A** | see 2.4 |
| `IsWalking` (override) | B | uses `PLAYER_WALKING_DELAY` |
| Behaviour | | `FindBandage`, `FindItemByGraphic`, `FindItemByCliloc`, `FindPreferredItemByCliloc`, `GetTotalAmountOfItem` (walk child lists), `AddBuff`, `IsBuffIconExists`, `RemoveBuff`, `UpdateAbilities`, `OnPositionChanged`, `TryOpenCorpses`, `OnDirectionChanged`, `TryOpenDoors`, `Destroy`, `CloseBank`, `CloseRangedGumps`, `Walk` (writes `Direction`, `LastStepTime`, enqueues steps) , `SyncServerDirection` |

Counts: 41 A members are instrumented in section 3 (`Serial` and `Name` count as A although cold; `DisplayedGraphic` has no writer), plus `Steps`, `_lastAnimationIdleDelay` and the six private animation fields, which have no external writer by name. `PlayerMobile` has about 60 D members and one A override (`InWarMode`). The tables above are exhaustive for fields and properties; methods are grouped under "Behaviour".

## 3. World-state (A) fields: writers and readers

Counts are references found by the compiler (section 1). **World writes** are writes on `Item`, `Mobile`, `PlayerMobile`, `Entity`, and bare writes inside those classes and `GameObject`; **map/effect writes** are writes on `Land`, `Static`, `Multi` and effects, which are not ECS state. Reads are counted on any receiver type (so `Graphic` reads include `Land.Graphic`). Reader areas:
- **hot**: `Game/GameObjects/Views`, `Game/Scenes` (the walk, the prepare, picking), `Game/Map`, `Game/Ecs` (mirror, extract, systems), the entity classes themselves (update code) and `World.cs`;
- **network**: `Network/*` (packet handlers; per packet, not per frame);
- **cold**: `Game/UI` (gumps, controls), `Game/Managers`, `GameActions`, `Pathfinder`, `SelectedObject`, `GameCursor`, everything else;
- **dev**: `DevBench.cs`, the `Ecs` checkers and drivers, `PrepCheck`, `WorldSettleGate`.

| Field | Owner | World writes | Map/effect writes | Reads: hot (views / scenes / map / ecs / entity / world) | Reads: network | Reads: cold (ui / managers / other) | Reads: dev/checkers |
|---|---|---:|---:|---:|---:|---:|---:|
| `Graphic` | GameObject | 33 | 9 | 211 | 20 | 71 | 24 |
| `Hue` | GameObject | 17 | 7 | 36 | 9 | 23 | 7 |
| `X` | GameObject | 14 | 2 | 127 | 16 | 86 | 38 |
| `Y` | GameObject | 14 | 2 | 124 | 14 | 87 | 38 |
| `Z` | GameObject | 12 | 2 | 144 | 11 | 59 | 26 |
| `Offset` | GameObject | 25 | 10 | 85 | 0 | 17 | 0 |
| `Direction` | Entity | 10 | 0 | 20 | 4 | 3 | 2 |
| `Flags` | Entity | 15 | 0 | 16 | 4 | 5 | 3 |
| `Serial` | Entity | 5 | 0 | 149 | 31 | 75 | 39 |
| `Name` | Entity | 12 | 0 | 6 | 13 | 47 | 3 |
| `Hits` | Entity | 5 | 0 | 7 | 0 | 16 | 2 |
| `HitsMax` | Entity | 3 | 0 | 8 | 0 | 22 | 2 |
| `Amount` | Item | 7 | 0 | 34 | 3 | 20 | 4 |
| `Container` | Item | 9 | 0 | 18 | 16 | 14 | 3 |
| `Layer` | Item | 9 | 0 | 14 | 19 | 7 | 5 |
| `Price` | Item | 1 | 0 | 0 | 1 | 1 | 0 |
| `IsDamageable` | Item | 1 | 0 | 1 | 0 | 1 | 0 |
| `LightID` | Item | 1 | 0 | 1 | 0 | 0 | 0 |
| `UsedLayer` | Item | 2 | 0 | 4 | 0 | 0 | 0 |
| `DisplayedGraphic` | Item | 0 | 0 | 8 | 0 | 15 | 2 |
| `IsMulti` | Item | 3 | 0 | 25 | 3 | 6 | 4 |
| `AnimIndex` | Entity | 9 | 0 | 16 | 1 | 4 | 3 |
| `ExecuteAnimation` | Entity | 2 | 0 | 3 | 0 | 0 | 0 |
| `LastAnimationChangeTime` | Entity | 5 | 0 | 10 | 0 | 0 | 1 |
| `LastStepTime` | Entity | 5 | 0 | 6 | 0 | 1 | 0 |
| `AnimationFrameCount` | Mobile | 3 | 0 | 2 | 0 | 0 | 0 |
| `AnimationFromServer` | Mobile | 2 | 0 | 7 | 0 | 0 | 0 |
| `LastStepSoundTime` | Mobile | 1 | 0 | 2 | 0 | 0 | 0 |
| `StepSoundOffset` | Mobile | 1 | 0 | 2 | 0 | 0 | 0 |
| `Race` | Mobile | 9 | 0 | 1 | 1 | 9 | 0 |
| `SpeedMode` | Mobile | 1 | 0 | 7 | 0 | 1 | 0 |
| `Stamina` | Mobile | 3 | 0 | 1 | 0 | 10 | 0 |
| `StaminaMax` | Mobile | 3 | 0 | 0 | 0 | 10 | 0 |
| `Mana` | Mobile | 3 | 0 | 0 | 0 | 9 | 0 |
| `ManaMax` | Mobile | 3 | 0 | 0 | 0 | 9 | 0 |
| `IsRenamable` | Mobile | 1 | 0 | 3 | 0 | 9 | 0 |
| `IsFemale` | Mobile | 9 | 0 | 4 | 0 | 9 | 1 |
| `IsRunning` | Mobile | 3 | 0 | 3 | 0 | 0 | 1 |
| `NotorietyFlag` | Mobile | 3 | 0 | 21 | 0 | 45 | 2 |
| `IsDead` | Mobile | 1 | 0 | 31 | 8 | 14 | 2 |
| `InWarMode` | Mobile | 1 | 0 | 7 | 1 | 17 | 1 |
| **total** | | **266** | **32** | | | | |

What the table says:
- **266 world write sites in 14 files.** 121 are in `PacketHandlers.cs`, 66 inside the five entity classes (`GameObject` 12, `Mobile` 40, `Item` 8, `Entity` 4, `PlayerMobile` 2), 34 in the two character-creation gumps (detached objects, see below), 24 in `BoatMovingManager`, 12 in `DevBench`, 4 in `ContainerGump`, 3 in `GameScene` (the placement preview), 1 each in `World` and `CorpseManager`. A further 32 writes are on map and effect objects.
- 2,274 references are reads, about 9 reads per world write. The hot readers are the views, the walk and the extract; the UI reads mostly `Name`, `Hits`, `HitsMax`, `NotorietyFlag`, `Graphic` and `Serial`.

**Detached objects.** These are `Item`/`Mobile` instances that are in no `World` dictionary and need no ECS entity:
- `CreateCharAppearanceGump._character` (a `PlayerMobile`) and its `CreateItem` items;
- `RaceChangeGump.fakeMobile`, `hair` and `beard`;
- `GameScene._multi` (the house placement preview, `Item.Create(_world, 0)`);
- the spellbook entries of 0xBF (`PacketHandlers.cs:4543-4548`: `Item.Create(world, cc)`, "FIXME: should i call Item.Create?").
Their writes (`CreateCharAppearanceGump` 248-344, 645-650, 752, 983-986; `RaceChangeGump` 338-505; `GameScene.cs` 1062-1064; `PacketHandlers.cs` 4545-4548) must either keep working on plain GameObjects or go through a funnel mode that does not mirror. T1 has to decide; the funnel must not create entities for them.

### 3.1 Write sites

Sites are `file:line`, relative to `src/ClassicUO.Client/`. `(T)` is the receiver's static type; a bare name inside the class is the class itself. `=` plain assignment, `op=` compound (`|=`, `&=`, `+=`), `.sub=` assignment to a member of the struct (`Offset.X = `).

**`Graphic`** (GameObject): 33 world-object write site(s), 9 on map/effect objects (not ECS)
- `DevBench.cs`: 1248 (PlayerMobile), 1309 (PlayerMobile), 1609 (PlayerMobile), 1614 (PlayerMobile), 1750 (Item)
- `Game/GameObjects/GameObject.cs`: 473 (this), 503 (this)
- `Game/Scenes/GameScene.cs`: 1062 (Item)
- `Game/UI/Gumps/CharCreation/CreateCharAppearanceGump.cs`: 263 (PlayerMobile), 272 (PlayerMobile), 287 (PlayerMobile), 306 (PlayerMobile), 320 (PlayerMobile), 983 (Item)
- `Game/UI/Gumps/RaceChangeGump.cs`: 353 (PlayerMobile), 357 (PlayerMobile), 485 (Item), 492 (Item), 504 (Item)
- `Network/PacketHandlers.cs`: 892 (PlayerMobile), 1643 (Item), 1835 (Item), 2821 (Mobile), 2855 (PlayerMobile), 2925 (Item), 4524 (Item), 4546 (Item), 6097 (Item), 6246 (Mobile), 6305 (Item), 6311 (Item), 6366 (Mobile), 6455 (PlayerMobile)
- map/effect: `Game/GameObjects/DragEffect.cs`: 59 (this)
- map/effect: `Game/GameObjects/GameEffect.cs`: 20 (this)
- map/effect: `Game/GameObjects/Land.cs`: 58 (this), 91 (this)
- map/effect: `Game/GameObjects/Multi.cs`: 49 (this), 75 (this)
- map/effect: `Game/GameObjects/Static.cs`: 55 (this), 82 (this), 87 (this)

**`Hue`** (GameObject): 17 world-object write site(s), 7 on map/effect objects (not ECS)
- `DevBench.cs`: 1635 (Item)
- `Game/GameObjects/Entity.cs`: 112 (this)
- `Game/GameObjects/GameObject.cs`: 469 (this), 504 (this)
- `Game/Scenes/GameScene.cs`: 1063 (Item)
- `Game/UI/Gumps/CharCreation/CreateCharAppearanceGump.cs`: 344 (PlayerMobile), 645 (Item), 650 (PlayerMobile), 984 (Item)
- `Game/UI/Gumps/RaceChangeGump.cs`: 422 (Item), 425 (Item), 428 (PlayerMobile), 478 (PlayerMobile), 486 (Item), 493 (Item), 505 (Item)
- `Network/PacketHandlers.cs`: 1644 (Item)
- map/effect: `Game/GameCursor.cs`: 295 (Multi)
- map/effect: `Game/GameObjects/DragEffect.cs`: 58 (this)
- map/effect: `Game/GameObjects/GameEffect.cs`: 21 (this)
- map/effect: `Game/GameObjects/House.cs`: 54 (Multi), 129 (Multi)
- map/effect: `Game/GameObjects/Item.cs`: 271 (Multi)
- map/effect: `Game/GameObjects/Static.cs`: 56 (this)

**`X`** (GameObject): 14 world-object write site(s), 2 on map/effect objects (not ECS)
- `DevBench.cs`: 1752 (Item)
- `Game/GameObjects/GameObject.cs`: 211 (this), 509 (this)
- `Game/GameObjects/Mobile.cs`: 897 (this)
- `Game/Managers/BoatMovingManager.cs`: 267 (Item), 372 (Entity)
- `Game/UI/Gumps/ContainerGump.cs`: 643 (Item), 647 (Item)
- `Network/PacketHandlers.cs`: 1648 (Item), 6101 (Item), 6250 (Mobile), 6314 (Item), 6347 (Mobile), 6357 (Mobile)
- map/effect: `Game/Map/Chunk.cs`: 154 (Land), 168 (Static)

**`Y`** (GameObject): 14 world-object write site(s), 2 on map/effect objects (not ECS)
- `DevBench.cs`: 1753 (Item)
- `Game/GameObjects/GameObject.cs`: 212 (this), 509 (this)
- `Game/GameObjects/Mobile.cs`: 898 (this)
- `Game/Managers/BoatMovingManager.cs`: 268 (Item), 373 (Entity)
- `Game/UI/Gumps/ContainerGump.cs`: 652 (Item), 656 (Item)
- `Network/PacketHandlers.cs`: 1649 (Item), 6102 (Item), 6251 (Mobile), 6315 (Item), 6348 (Mobile), 6358 (Mobile)
- map/effect: `Game/Map/Chunk.cs`: 155 (Land), 169 (Static)

**`Z`** (GameObject): 12 world-object write site(s), 2 on map/effect objects (not ECS)
- `DevBench.cs`: 1754 (Item)
- `Game/GameObjects/GameObject.cs`: 213 (this), 510 (this)
- `Game/GameObjects/Mobile.cs`: 899 (this)
- `Game/Managers/BoatMovingManager.cs`: 269 (Item), 374 (Entity)
- `Network/PacketHandlers.cs`: 1650 (Item), 6103 (Item), 6252 (Mobile), 6316 (Item), 6349 (Mobile), 6359 (Mobile)
- map/effect: `Game/Map/Chunk.cs`: 156 (Land), 170 (Static)

**`Offset`** (GameObject): 25 world-object write site(s), 10 on map/effect objects (not ECS)
- `Game/GameObjects/GameObject.cs`: 470 (this), 505 (this)
- `Game/GameObjects/Mobile.cs`: 373 (this), 840 (this .sub=), 842 (this .sub=), 843 (this .sub=), 903 (this .sub=), 904 (this .sub=), 905 (this .sub=)
- `Game/Managers/BoatMovingManager.cs`: 133 (Item .sub=), 134 (Item .sub=), 135 (Item .sub=), 152 (Entity .sub=), 153 (Entity .sub=), 154 (Entity .sub=), 249 (Item .sub=), 251 (Item .sub=), 252 (Item .sub=), 272 (Item .sub=), 273 (Item .sub=), 274 (Item .sub=), 377 (Entity .sub=), 378 (Entity .sub=), 379 (Entity .sub=), 390 (Entity)
- map/effect: `Game/GameObjects/DragEffect.cs`: 69 (this .sub=), 70 (this .sub=)
- map/effect: `Game/GameObjects/FixedEffect.cs`: 68 (this)
- map/effect: `Game/GameObjects/House.cs`: 120 (Multi)
- map/effect: `Game/GameObjects/MovingEffect.cs`: 38 (this .sub=), 153 (this .sub=), 154 (this .sub=), 157 (this .sub=), 158 (this .sub=)
- map/effect: `Game/Managers/BoatMovingManager.cs`: 304 (Multi)

**`Direction`** (Entity): 10 world-object write site(s)
- `Game/GameObjects/Item.cs`: 346 (this op=)
- `Game/GameObjects/Mobile.cs`: 901 (this)
- `Game/GameObjects/PlayerMobile.cs`: 633 (this)
- `Network/PacketHandlers.cs`: 905 (PlayerMobile), 1196 (PlayerMobile), 6248 (Mobile), 6333 (Item), 6350 (Mobile), 6360 (Mobile), 6456 (PlayerMobile)

**`Flags`** (Entity): 15 world-object write site(s)
- `Game/UI/Gumps/CharCreation/CreateCharAppearanceGump.cs`: 253 (PlayerMobile op=), 257 (PlayerMobile op=)
- `Game/UI/Gumps/RaceChangeGump.cs`: 343 (PlayerMobile op=), 347 (PlayerMobile op=)
- `Network/PacketHandlers.cs`: 746 (Mobile op=), 757 (Mobile op=), 765 (Mobile op=), 769 (Mobile op=), 1646 (Item), 2820 (Mobile), 2858 (PlayerMobile), 6253 (Mobile), 6332 (Item), 6368 (Mobile), 6458 (PlayerMobile)

**`Serial`** (Entity): 5 world-object write site(s)
- `Game/GameObjects/Entity.cs`: 30 (this)
- `Game/GameObjects/Item.cs`: 185 (this)
- `Game/GameObjects/Mobile.cs`: 278 (this)
- `Network/PacketHandlers.cs`: 3732 (Mobile), 4545 (Item)

**`Name`** (Entity): 12 world-object write site(s)
- `Game/UI/Gumps/CharCreation/CreateCharAppearanceGump.cs`: 752 (PlayerMobile)
- `Network/PacketHandlers.cs`: 499 (Entity), 1011 (Entity), 2761 (Item), 2765 (Item), 2773 (Item), 2777 (Item), 3307 (Entity), 3685 (Entity), 4241 (Item), 4841 (Entity), 5057 (Entity)

**`Hits`** (Entity): 5 world-object write site(s)
- `DevBench.cs`: 709 (PlayerMobile), 796 (PlayerMobile)
- `Network/PacketHandlers.cs`: 500 (Entity), 1781 (Entity), 3421 (Entity)

**`HitsMax`** (Entity): 3 world-object write site(s)
- `Network/PacketHandlers.cs`: 501 (Entity), 1780 (Entity), 3420 (Entity)

**`Amount`** (Item): 7 world-object write site(s)
- `DevBench.cs`: 1751 (Item)
- `Network/PacketHandlers.cs`: 1645 (Item), 1839 (Item), 2927 (Item), 4547 (Item), 6099 (Item), 6331 (Item)

**`Container`** (Item): 9 world-object write site(s)
- `Game/UI/Gumps/CharCreation/CreateCharAppearanceGump.cs`: 986 (Item)
- `Game/World.cs`: 1022 (Item)
- `Network/PacketHandlers.cs`: 1663 (Item), 1837 (Item), 2929 (Item), 3144 (Item), 3727 (Item), 4548 (Item), 6110 (Item)

**`Layer`** (Item): 9 world-object write site(s)
- `Game/GameObjects/Item.cs`: 353 (this)
- `Game/Managers/CorpseManager.cs`: 48 (Item)
- `Game/UI/Gumps/CharCreation/CreateCharAppearanceGump.cs`: 985 (Item)
- `Game/UI/Gumps/RaceChangeGump.cs`: 506 (Item)
- `Network/PacketHandlers.cs`: 1647 (Item), 1836 (Item), 2930 (Item), 3145 (Item), 6321 (Item)

**`Price`** (Item): 1 world-object write site(s)
- `Network/PacketHandlers.cs`: 2755 (Item)

**`IsDamageable`** (Item): 1 world-object write site(s)
- `Network/PacketHandlers.cs`: 6309 (Item)

**`LightID`** (Item): 1 world-object write site(s)
- `Network/PacketHandlers.cs`: 6317 (Item)

**`UsedLayer`** (Item): 2 world-object write site(s)
- `Game/GameObjects/Item.cs`: 345 (this), 350 (this)

**`DisplayedGraphic`** (Item): no writes outside the declaring class' own setter body.

**`IsMulti`** (Item): 3 world-object write site(s)
- `Game/Scenes/GameScene.cs`: 1064 (Item)
- `Network/PacketHandlers.cs`: 6298 (Item), 6310 (Item)

**`AnimIndex`** (Entity): 9 world-object write site(s)
- `Game/GameObjects/Entity.cs`: 197 (this)
- `Game/GameObjects/Item.cs`: 341 (this), 540 (this)
- `Game/GameObjects/Mobile.cs`: 469 (this), 488 (this), 740 (this)
- `Network/PacketHandlers.cs`: 3754 (Mobile), 4505 (Mobile), 4727 (Mobile)

**`ExecuteAnimation`** (Entity): 2 world-object write site(s)
- `Network/PacketHandlers.cs`: 4504 (Mobile), 4728 (Mobile)

**`LastAnimationChangeTime`** (Entity): 5 world-object write site(s)
- `Game/GameObjects/Entity.cs`: 198 (this)
- `Game/GameObjects/Item.cs`: 544 (this)
- `Game/GameObjects/Mobile.cs`: 134 (this), 477 (this), 754 (this)

**`LastStepTime`** (Entity): 5 world-object write site(s)
- `Game/GameObjects/Mobile.cs`: 398 (this), 929 (this)
- `Game/GameObjects/PlayerMobile.cs`: 648 (this)
- `Game/Managers/BoatMovingManager.cs`: 104 (Item), 296 (Item)

**`AnimationFrameCount`** (Mobile): 3 world-object write site(s)
- `Game/GameObjects/Mobile.cs`: 471 (this), 489 (this), 725 (this)

**`AnimationFromServer`** (Mobile): 2 world-object write site(s)
- `Game/GameObjects/Mobile.cs`: 476 (this), 495 (this)

**`LastStepSoundTime`** (Mobile): 1 world-object write site(s)
- `Game/GameObjects/Mobile.cs`: 674 (this)

**`StepSoundOffset`** (Mobile): 1 world-object write site(s)
- `Game/GameObjects/Mobile.cs`: 671 (this)

**`Race`** (Mobile): 9 world-object write site(s)
- `Game/GameObjects/Mobile.cs`: 1091 (this), 1100 (this), 1108 (this), 1116 (this), 1124 (this), 1132 (this)
- `Game/UI/Gumps/CharCreation/CreateCharAppearanceGump.cs`: 248 (PlayerMobile)
- `Game/UI/Gumps/RaceChangeGump.cs`: 338 (PlayerMobile)
- `Network/PacketHandlers.cs`: 624 (PlayerMobile)

**`SpeedMode`** (Mobile): 1 world-object write site(s)
- `Network/PacketHandlers.cs`: 4705 (PlayerMobile)

**`Stamina`** (Mobile): 3 world-object write site(s)
- `Network/PacketHandlers.cs`: 536 (PlayerMobile), 1800 (Mobile), 3462 (Mobile)

**`StaminaMax`** (Mobile): 3 world-object write site(s)
- `Network/PacketHandlers.cs`: 537 (PlayerMobile), 1799 (Mobile), 3461 (Mobile)

**`Mana`** (Mobile): 3 world-object write site(s)
- `Network/PacketHandlers.cs`: 538 (PlayerMobile), 1798 (Mobile), 3444 (Mobile)

**`ManaMax`** (Mobile): 3 world-object write site(s)
- `Network/PacketHandlers.cs`: 539 (PlayerMobile), 1797 (Mobile), 3443 (Mobile)

**`IsRenamable`** (Mobile): 1 world-object write site(s)
- `Network/PacketHandlers.cs`: 517 (Mobile)

**`IsFemale`** (Mobile): 9 world-object write site(s)
- `Game/GameObjects/Mobile.cs`: 1090 (this), 1099 (this), 1107 (this), 1115 (this), 1123 (this), 1131 (this)
- `Game/UI/Gumps/CharCreation/CreateCharAppearanceGump.cs`: 249 (PlayerMobile)
- `Game/UI/Gumps/RaceChangeGump.cs`: 339 (PlayerMobile)
- `Network/PacketHandlers.cs`: 522 (Mobile)

**`IsRunning`** (Mobile): 3 world-object write site(s)
- `Game/GameObjects/Mobile.cs`: 902 (this)
- `Network/PacketHandlers.cs`: 6351 (Mobile), 6361 (Mobile)

**`NotorietyFlag`** (Mobile): 3 world-object write site(s)
- `Network/PacketHandlers.cs`: 1216 (PlayerMobile), 2816 (Mobile), 2893 (Mobile)

**`IsDead`** (Mobile): 1 world-object write site(s)
- `Network/PacketHandlers.cs`: 4459 (Mobile)

**`InWarMode`** (Mobile): 1 world-object write site(s)
- `Network/PacketHandlers.cs`: 2674 (PlayerMobile)

**Struct-member writes (`Offset`).** `Offset` is a `Vector3`; `item.Offset.Z = ` writes one member of it. The sites are listed with `.sub=` above. A property that returns the struct by value cannot be written this way, so T0c has to expose `Offset` as a field, a `ref` return or three scalar properties.

**Writes inside the declaring class** (`Entity.cs`, `Item.cs`, `Mobile.cs`, `GameObject.cs`, `PlayerMobile.cs`) are listed above as `this`. The setters of `Hits`, `HitsMax`, `Direction`, `IsFemale`, `IsRunning`, `NotorietyFlag`, `IsDead`, `InWarMode` and `IsMulti` already run code (the mirror push, `RefreshHitsPercentage`); T0c must keep those side effects when it makes the rest of the fields properties.

### 3.1a Construction writes

The 266 above are the section-3 mutation count: assignments, compound assignments and `Offset` member writes found by the compiler. **Construction writes** are field initialisers; they run once per object, are not counted by the compiler list or the budget, and the ECS component defaults must match them:

| Initialiser | Value |
|---|---|
| `Entity.cs:34 ExecuteAnimation` | `true` |
| `Item.cs:169 Container` | `0xFFFF_FFFF` (on the ground) |
| `Item.cs:180 WantUpdateMulti` (E) | `true` |
| `Mobile.cs:126 _animationGroup` | `0xFF` |
| `Mobile.cs:129-130 _animationRepeateMode`, `_animationRepeatModeCount` | `1` |
| `Mobile.cs:267 SpeedMode` | `CharacterSpeedType.Normal` |
| `GameObject.cs:76 FoliageIndex` (C), `PendingBlock` (E) | `-1` |

Constructor bodies that assign (`Entity` sets `Serial`; `Mobile` sets `LastAnimationChangeTime` and calls `CalculateRandomIdleTime`) are ordinary writes and are in the lists above.

### 3.2 World write sites in `PacketHandlers.cs`, by handler

The 121 packet sites by handler (the T1 packet groups map onto these rows). `UpdateGameObject` is the shared helper behind the item and mobile update packets.

| Handler | Packets | Sites | Lines | Fields written |
|---|---|---:|---|---|
| `CharacterStatus` | 0x11 | 10 | 499-624 | Name, Hits, HitsMax, IsRenamable, IsFemale, Stamina, StaminaMax, Mana, ManaMax, Race |
| `NewHealthbarUpdate` | 0x16, 0x17 | 4 | 746-769 | Flags |
| `EnterWorld` | 0x1b | 2 | 892-905 | Graphic, Direction |
| `Talk` | 0x1c | 1 | 1011 | Name |
| `DenyWalk` | 0x21 | 1 | 1196 | Direction |
| `ConfirmWalk` | 0x22 | 1 | 1216 | NotorietyFlag |
| `DenyMoveItem` | 0x27 | 9 | 1643-1663 | Graphic, Hue, Amount, Flags, Layer, X, Y, Z, Container |
| `MobileAttributes` | 0x2d | 6 | 1780-1800 | HitsMax, Hits, ManaMax, Mana, StaminaMax, Stamina |
| `EquipItem` | 0x2e | 4 | 1835-1839 | Graphic, Layer, Container, Amount |
| `Warmode` | 0x72 | 1 | 2674 | InWarMode |
| `BuyList` | 0x74 | 5 | 2755-2777 | Price, Name |
| `UpdateCharacter` | 0x77, 0xd2 | 3 | 2816-2821 | NotorietyFlag, Flags, Graphic |
| `UpdateObject` | 0x78, 0xd3 | 7 | 2855-2930 | Graphic, Flags, NotorietyFlag, Amount, Container, Layer |
| `CorpseEquipment` | 0x89 | 2 | 3144-3145 | Container, Layer |
| `UpdateName` | 0x98 | 1 | 3307 | Name |
| `UpdateHitpoints` | 0xa1 | 2 | 3420-3421 | HitsMax, Hits |
| `UpdateMana` | 0xa2 | 2 | 3443-3444 | ManaMax, Mana |
| `UpdateStamina` | 0xa3 | 2 | 3461-3462 | StaminaMax, Stamina |
| `UnicodeTalk` | 0xae | 1 | 3685 | Name |
| `DisplayDeath` | 0xaf | 3 | 3727-3754 | Container, Serial, AnimIndex |
| `ExtendedCommand` | 0xbf | 12 | 4241-4728 | Name, IsDead, ExecuteAnimation, AnimIndex, Graphic, Serial, Amount, Container, SpeedMode |
| `DisplayClilocString` | 0xc1, 0xcc | 1 | 4841 | Name |
| `MegaCliloc` | 0xd6 | 1 | 5057 | Name |
| `AddItemToContainer` | called by UpdateContainedItem (0x25), DenyMoveItem (0x27, line 1622) and UpdateContainedItems (0x3c) | 6 | 6097-6110 | Graphic, Amount, X, Y, Z, Container |
| `UpdateGameObject` | shared by 0x1a, 0x77, 0xd2, 0x78, 0xd3, 0xf3, 0xf6 (UpdateItem, UpdateCharacter, UpdateObject, UpdateItemSA, BoatMoving) | 31 | 6246-6368 | Graphic, Direction, X, Y, Z, Flags, IsMulti, IsDamageable, LightID, Layer, Amount, IsRunning |
| `UpdatePlayer` | 0x20 | 3 | 6455-6458 | Graphic, Direction, Flags |
| **total** | | **121** | | |

### 3.3 Readers by file

Reads of each A field per file, grouped hot (per-frame, per-object code) and cold. Counts are references, a line with two is counted twice.

**`Graphic`**: 326 reads
- hot, ecs (mirror, extract, systems) (21): `ItemSpriteExtract.cs` 1, `MobileAnimationSystem.cs` 7, `MobileInputRules.cs` 1, `WorldEcs.cs` 12
- hot, entity classes (update) (98): `DragEffect.cs` 1, `Entity.cs` 3, `EntityTextContainer.cs` 1, `GameEffect.cs` 3, `GameObject.cs` 1, `Item.cs` 15, `Land.cs` 2, `LightningEffect.cs` 1, `Mobile.cs` 28, `MobileAnimation.cs` 27, `Multi.cs` 3, `PlayerMobile.cs` 10, `Static.cs` 3
- hot, views (44): `GameEffectView.cs` 1, `ItemView.cs` 10, `LandView.cs` 3, `MobileView.cs` 25, `MultiView.cs` 3, `StaticView.cs` 2
- hot, map/chunks (18): `Chunk.cs` 1, `ChunkMesh.cs` 10, `Map.cs` 6, `MapObjectRecycler.cs` 1
- hot, scenes (walk, prepare, pick) (28): `GameScene.cs` 3, `GameSceneDrawingSorting.cs` 9, `GameSceneGpuItemCache.cs` 4, `GameSceneGpuPrepare.cs` 4, `GameSceneGpuWorld.cs` 2, `GameSceneInputHandler.cs` 6
- hot, world (update, lookup) (2): `World.cs` 2
- net, network (packet handlers) (20): `OutgoingPackets.cs` 2, `PacketHandlers.cs` 18
- cold, managers/actions (26): `ItemHold.cs` 2, `CommandManager.cs` 1, `HouseCustomizationManager.cs` 13, `TargetManager.cs` 5, `Pathfinder.cs` 5
- cold, ui (gumps, controls) (44): `PaperDollInteractable.cs` 15, `ContainerGump.cs` 3, `DebugGump.cs` 5, `HouseCustomizationGump.cs` 1, `InspectorGump.cs` 1, `MiniMapGump.cs` 1, `NameOverheadGump.cs` 2, `RaceChangeGump.cs` 15, `SpellbookGump.cs` 1
- cold, other (1): `UoAssist.cs` 1
- dev, dev/checkers (24): `DevBench.cs` 13, `EcsInspector.cs` 3, `EcsParity.cs` 3, `EcsSessionDriver.cs` 5

**`Hue`**: 75 reads
- hot, ecs (mirror, extract, systems) (9): `ItemSpriteExtract.cs` 3, `WorldEcs.cs` 6
- hot, entity classes (update) (11): `DragEffect.cs` 1, `Entity.cs` 5, `GameEffect.cs` 1, `House.cs` 1, `Item.cs` 2, `PlayerMobile.cs` 1
- hot, views (10): `GameEffectView.cs` 1, `ItemView.cs` 3, `LandView.cs` 1, `LightningEffectView.cs` 1, `MobileView.cs` 2, `MultiView.cs` 1, `StaticView.cs` 1
- hot, map/chunks (3): `ChunkMesh.cs` 3
- hot, scenes (walk, prepare, pick) (3): `GameSceneDrawingSorting.cs` 1, `GameSceneGpuItemCache.cs` 2
- net, network (packet handlers) (9): `OutgoingPackets.cs` 5, `PacketHandlers.cs` 4
- cold, managers/actions (2): `ItemHold.cs` 1, `CommandManager.cs` 1
- cold, ui (gumps, controls) (21): `PaperDollInteractable.cs` 4, `ColorPickerGump.cs` 2, `ContainerGump.cs` 2, `GridLootGump.cs` 2, `InspectorGump.cs` 1, `MiniMapGump.cs` 2, `PaperdollGump.cs` 2, `RaceChangeGump.cs` 4, `TradingGump.cs` 2
- dev, dev/checkers (7): `DevBench.cs` 1, `EcsInspector.cs` 2, `EcsParity.cs` 2, `EcsSessionDriver.cs` 2

**`X`**: 267 reads
- hot, ecs (mirror, extract, systems) (11): `ItemSpriteExtract.cs` 2, `MobileAnimationSystem.cs` 1, `WorldEcs.cs` 8
- hot, entity classes (update) (29): `FixedEffect.cs` 1, `GameEffect.cs` 4, `GameObject.cs` 7, `House.cs` 2, `Item.cs` 2, `LightningEffect.cs` 1, `Mobile.cs` 5, `MobileAnimation.cs` 1, `MovingEffect.cs` 2, `PlayerMobile.cs` 4
- hot, views (1): `View.cs` 1
- hot, map/chunks (7): `ChunkMesh.cs` 4, `MapObjectRecycler.cs` 3
- hot, scenes (walk, prepare, pick) (73): `GameScene.cs` 13, `GameSceneDrawingSorting.cs` 15, `GameSceneGpuItemCache.cs` 11, `GameSceneGpuPrepare.cs` 8, `GameSceneGpuWorld.cs` 16, `GameSceneInputHandler.cs` 9, `GpuWorldRenderer.cs` 1
- hot, world (update, lookup) (6): `World.cs` 6
- net, network (packet handlers) (16): `PacketHandlers.cs` 15, `Plugin.cs` 1
- cold, managers/actions (58): `GameCursor.cs` 3, `ItemHold.cs` 1, `AudioManager.cs` 1, `BoatMovingManager.cs` 3, `HouseCustomizationManager.cs` 39, `HouseManager.cs` 5, `TargetManager.cs` 4, `Pathfinder.cs` 2
- cold, ui (gumps, controls) (25): `ContainerGump.cs` 6, `DebugGump.cs` 2, `InspectorGump.cs` 1, `MiniMapGump.cs` 3, `NameOverheadGump.cs` 1, `PaperdollGump.cs` 1, `QuestArrowGump.cs` 2, `TradingGump.cs` 2, `WorldMapGump.cs` 7
- cold, other (3): `UoAssist.cs` 2, `Weather.cs` 1
- dev, dev/checkers (38): `DevBench.cs` 19, `BenchEffects.cs` 2, `EcsInspector.cs` 1, `EcsParity.cs` 2, `EcsSessionDriver.cs` 14

**`Y`**: 263 reads
- hot, ecs (mirror, extract, systems) (9): `MobileAnimationSystem.cs` 1, `WorldEcs.cs` 8
- hot, entity classes (update) (29): `FixedEffect.cs` 1, `GameEffect.cs` 4, `GameObject.cs` 7, `House.cs` 2, `Item.cs` 2, `LightningEffect.cs` 1, `Mobile.cs` 5, `MobileAnimation.cs` 1, `MovingEffect.cs` 2, `PlayerMobile.cs` 4
- hot, views (1): `View.cs` 1
- hot, map/chunks (7): `ChunkMesh.cs` 4, `MapObjectRecycler.cs` 3
- hot, scenes (walk, prepare, pick) (72): `GameScene.cs` 13, `GameSceneDrawingSorting.cs` 15, `GameSceneGpuItemCache.cs` 11, `GameSceneGpuPrepare.cs` 8, `GameSceneGpuWorld.cs` 16, `GameSceneInputHandler.cs` 9
- hot, world (update, lookup) (6): `World.cs` 6
- net, network (packet handlers) (14): `PacketHandlers.cs` 13, `Plugin.cs` 1
- cold, managers/actions (59): `GameCursor.cs` 3, `ItemHold.cs` 1, `AudioManager.cs` 1, `BoatMovingManager.cs` 3, `HouseCustomizationManager.cs` 40, `HouseManager.cs` 5, `TargetManager.cs` 4, `Pathfinder.cs` 2
- cold, ui (gumps, controls) (25): `ContainerGump.cs` 6, `DebugGump.cs` 2, `InspectorGump.cs` 1, `MiniMapGump.cs` 3, `NameOverheadGump.cs` 1, `PaperdollGump.cs` 1, `QuestArrowGump.cs` 2, `TradingGump.cs` 2, `WorldMapGump.cs` 7
- cold, other (3): `UoAssist.cs` 2, `Weather.cs` 1
- dev, dev/checkers (38): `DevBench.cs` 19, `BenchEffects.cs` 2, `EcsInspector.cs` 1, `EcsParity.cs` 2, `EcsSessionDriver.cs` 14

**`Z`**: 240 reads
- hot, ecs (mirror, extract, systems) (7): `MobileAnimationSystem.cs` 1, `WorldEcs.cs` 6
- hot, entity classes (update) (28): `FixedEffect.cs` 1, `GameEffect.cs` 4, `GameObject.cs` 2, `House.cs` 5, `Item.cs` 1, `Land.cs` 1, `LightningEffect.cs` 1, `Mobile.cs` 6, `MovingEffect.cs` 2, `PlayerMobile.cs` 5
- hot, views (8): `LandView.cs` 2, `MultiView.cs` 3, `StaticView.cs` 3
- hot, map/chunks (10): `Chunk.cs` 1, `ChunkMesh.cs` 4, `Map.cs` 5
- hot, scenes (walk, prepare, pick) (90): `GameScene.cs` 11, `GameSceneDrawingSorting.cs` 26, `GameSceneGpuItemCache.cs` 8, `GameSceneGpuPrepare.cs` 17, `GameSceneGpuWorld.cs` 17, `GameSceneInputHandler.cs` 8, `RenderLists.cs` 3
- hot, world (update, lookup) (1): `World.cs` 1
- net, network (packet handlers) (11): `PacketHandlers.cs` 10, `Plugin.cs` 1
- cold, managers/actions (52): `GameCursor.cs` 2, `ItemHold.cs` 1, `BoatMovingManager.cs` 3, `HouseCustomizationManager.cs` 35, `TargetManager.cs` 5, `Pathfinder.cs` 6
- cold, ui (gumps, controls) (6): `DebugGump.cs` 2, `InspectorGump.cs` 1, `NameOverheadGump.cs` 1, `QuestArrowGump.cs` 1, `WorldMapGump.cs` 1
- cold, other (1): `UoAssist.cs` 1
- dev, dev/checkers (26): `DevBench.cs` 19, `BenchEffects.cs` 1, `EcsInspector.cs` 1, `EcsParity.cs` 2, `EcsSessionDriver.cs` 3

**`Offset`**: 102 reads
- hot, ecs (mirror, extract, systems) (9): `ItemSpriteExtract.cs` 3, `MobileAnimationSystem.cs` 6
- hot, entity classes (update) (23): `DragEffect.cs` 2, `EntityTextContainer.cs` 3, `FixedEffect.cs` 1, `GameObject.cs` 6, `Item.cs` 3, `Mobile.cs` 6, `MovingEffect.cs` 2
- hot, views (42): `GameEffectView.cs` 3, `ItemView.cs` 6, `MobileView.cs` 9, `MultiView.cs` 3, `View.cs` 21
- hot, scenes (walk, prepare, pick) (11): `GameScene.cs` 3, `GameSceneDrawingSorting.cs` 3, `GameSceneGpuItemCache.cs` 2, `GameSceneInputHandler.cs` 3
- cold, managers/actions (5): `BoatMovingManager.cs` 2, `HealthLinesManager.cs` 3
- cold, ui (gumps, controls) (12): `NameOverheadGump.cs` 9, `QuestArrowGump.cs` 3

**`Direction`**: 29 reads
- hot, ecs (mirror, extract, systems) (7): `MobileInputRules.cs` 1, `WorldEcs.cs` 6
- hot, entity classes (update) (9): `Item.cs` 2, `Mobile.cs` 3, `MobileAnimation.cs` 1, `PlayerMobile.cs` 3
- hot, views (2): `ItemView.cs` 1, `MobileView.cs` 1
- hot, scenes (walk, prepare, pick) (2): `GameScene.cs` 1, `GameSceneInputHandler.cs` 1
- net, network (packet handlers) (4): `PacketHandlers.cs` 4
- cold, managers/actions (1): `BoatMovingManager.cs` 1
- cold, ui (gumps, controls) (2): `InspectorGump.cs` 2
- dev, dev/checkers (2): `EcsInspector.cs` 1, `EcsParity.cs` 1

**`Flags`**: 28 reads
- hot, ecs (mirror, extract, systems) (7): `MobileInputRules.cs` 1, `WorldEcs.cs` 6
- hot, entity classes (update) (8): `Entity.cs` 1, `Item.cs` 1, `Mobile.cs` 6
- hot, scenes (walk, prepare, pick) (1): `GameSceneGpuItemCache.cs` 1
- net, network (packet handlers) (4): `OutgoingPackets.cs` 2, `PacketHandlers.cs` 2
- cold, managers/actions (1): `ItemHold.cs` 1
- cold, ui (gumps, controls) (4): `DebugGump.cs` 2, `InspectorGump.cs` 2
- dev, dev/checkers (3): `EcsInspector.cs` 1, `EcsSessionDriver.cs` 2

**`Serial`**: 294 reads
- hot, ecs (mirror, extract, systems) (45): `ItemSpriteExtract.cs` 2, `MobileAnimationSystem.cs` 11, `WorldEcs.cs` 32
- hot, entity classes (update) (46): `Entity.cs` 8, `EntityCollection.cs` 2, `Item.cs` 12, `Mobile.cs` 13, `MobileTable.cs` 3, `PlayerMobile.cs` 8
- hot, views (7): `ItemView.cs` 4, `MobileView.cs` 3
- hot, scenes (walk, prepare, pick) (22): `GameScene.cs` 5, `GameSceneDrawingSorting.cs` 2, `GameSceneGpuItemCache.cs` 6, `GameSceneInputHandler.cs` 6, `GpuWorldRenderer.cs` 3
- hot, world (update, lookup) (29): `World.cs` 29
- net, network (packet handlers) (31): `OutgoingPackets.cs` 18, `PacketHandlers.cs` 13
- cold, other (2): `Profile.cs` 1, `UoAssist.cs` 1
- cold, managers/actions (31): `GameActions.cs` 8, `ItemHold.cs` 1, `HealthLinesManager.cs` 4, `IgnoreManager.cs` 2, `MacroManager.cs` 9, `NameOverHeadManager.cs` 2, `TargetManager.cs` 4, `WorldTextManager.cs` 1
- cold, ui (gumps, controls) (42): `ItemGump.cs` 1, `PaperDollInteractable.cs` 4, `ContainerGump.cs` 6, `DebugGump.cs` 2, `HealthBarGump.cs` 4, `InspectorGump.cs` 2, `NameOverheadGump.cs` 2, `OptionsGump.cs` 1, `PaperdollGump.cs` 10, `RaceChangeGump.cs` 2, `SystemChatControl.cs` 3, `TradingGump.cs` 4, `WorldMapGump.cs` 1
- dev, dev/checkers (39): `DevBench.cs` 12, `EcsInspector.cs` 3, `EcsParity.cs` 8, `EcsSessionDriver.cs` 16

**`Name`**: 69 reads
- hot, entity classes (update) (1): `PlayerMobile.cs` 1
- hot, scenes (walk, prepare, pick) (1): `LoginScene.cs` 1
- hot, world (update, lookup) (4): `World.cs` 4
- net, network (packet handlers) (13): `OutgoingPackets.cs` 1, `PacketHandlers.cs` 12
- cold, managers/actions (9): `GameActions.cs` 1, `IgnoreManager.cs` 1, `MacroManager.cs` 2, `MessageManager.cs` 2, `PartyManager.cs` 1, `WorldMapEntityManager.cs` 2
- cold, ui (gumps, controls) (38): `BulletinBoardGump.cs` 2, `CreateCharAppearanceGump.cs` 2, `GridLootGump.cs` 2, `HealthBarGump.cs` 8, `InfoBarGump.cs` 1, `InspectorGump.cs` 1, `NameOverheadGump.cs` 2, `PartyInviteGump.cs` 4, `StatusGump.cs` 8, `TradingGump.cs` 2, `WorldMapGump.cs` 6
- dev, dev/checkers (3): `EcsSessionDriver.cs` 3

**`Hits`**: 25 reads
- hot, entity classes (update) (1): `Mobile.cs` 1
- hot, world (update, lookup) (6): `World.cs` 6
- cold, managers/actions (2): `HealthLinesManager.cs` 2
- cold, ui (gumps, controls) (11): `HealthBarGump.cs` 2, `InfoBarGump.cs` 2, `InspectorGump.cs` 2, `StatusGump.cs` 4, `WorldMapGump.cs` 1
- cold, other (3): `UoAssist.cs` 3
- dev, dev/checkers (2): `DevBench.cs` 1, `EcsSessionDriver.cs` 1

**`HitsMax`**: 32 reads
- hot, entity classes (update) (1): `Mobile.cs` 1
- hot, world (update, lookup) (7): `World.cs` 7
- cold, managers/actions (3): `HealthLinesManager.cs` 3
- cold, ui (gumps, controls) (15): `HealthBarGump.cs` 6, `InfoBarGump.cs` 2, `InspectorGump.cs` 2, `StatusGump.cs` 4, `WorldMapGump.cs` 1
- cold, other (4): `UoAssist.cs` 4
- dev, dev/checkers (2): `DevBench.cs` 1, `EcsSessionDriver.cs` 1

**`Amount`**: 61 reads
- hot, ecs (mirror, extract, systems) (5): `ItemSpriteExtract.cs` 2, `WorldEcs.cs` 3
- hot, entity classes (update) (5): `Item.cs` 4, `PlayerMobile.cs` 1
- hot, views (23): `ItemView.cs` 23
- hot, scenes (walk, prepare, pick) (1): `GameSceneGpuItemCache.cs` 1
- net, network (packet handlers) (3): `PacketHandlers.cs` 3
- cold, managers/actions (4): `GameActions.cs` 2, `ItemHold.cs` 1, `TargetManager.cs` 1
- cold, ui (gumps, controls) (16): `ItemGump.cs` 4, `ContainerGump.cs` 1, `DebugGump.cs` 1, `GridLootGump.cs` 2, `InspectorGump.cs` 1, `NameOverheadGump.cs` 3, `SpellbookGump.cs` 1, `SplitMenuGump.cs` 3
- dev, dev/checkers (4): `EcsInspector.cs` 1, `EcsParity.cs` 1, `EcsSessionDriver.cs` 2

**`Container`**: 51 reads
- hot, ecs (mirror, extract, systems) (7): `WorldEcs.cs` 7
- hot, entity classes (update) (9): `Entity.cs` 2, `GameObject.cs` 2, `Item.cs` 5
- hot, world (update, lookup) (2): `World.cs` 2
- net, network (packet handlers) (16): `PacketHandlers.cs` 16
- cold, other (2): `Profile.cs` 2
- cold, managers/actions (8): `ItemHold.cs` 1, `ContainerManager.cs` 3, `MessageManager.cs` 4
- cold, ui (gumps, controls) (4): `ContainerGump.cs` 1, `HealthBarGump.cs` 2, `InspectorGump.cs` 1
- dev, dev/checkers (3): `EcsInspector.cs` 2, `EcsSessionDriver.cs` 1

**`Layer`**: 45 reads
- hot, ecs (mirror, extract, systems) (4): `ItemSpriteExtract.cs` 1, `WorldEcs.cs` 3
- hot, entity classes (update) (6): `Entity.cs` 1, `Item.cs` 2, `Mobile.cs` 1, `PlayerMobile.cs` 2
- hot, views (2): `ItemView.cs` 2
- hot, world (update, lookup) (2): `World.cs` 2
- net, network (packet handlers) (19): `PacketHandlers.cs` 19
- cold, managers/actions (1): `ItemHold.cs` 1
- cold, ui (gumps, controls) (6): `ContainerGump.cs` 1, `HealthBarGump.cs` 2, `InspectorGump.cs` 1, `PaperdollGump.cs` 2
- dev, dev/checkers (5): `EcsInspector.cs` 3, `EcsSessionDriver.cs` 2

**`Price`**: 2 reads
- net, network (packet handlers) (1): `PacketHandlers.cs` 1
- cold, ui (gumps, controls) (1): `InspectorGump.cs` 1

**`IsDamageable`**: 2 reads
- hot, scenes (walk, prepare, pick) (1): `GameSceneInputHandler.cs` 1
- cold, ui (gumps, controls) (1): `NameOverheadGump.cs` 1

**`LightID`**: 1 reads
- hot, scenes (walk, prepare, pick) (1): `GameScene.cs` 1

**`UsedLayer`**: 4 reads
- hot, ecs (mirror, extract, systems) (1): `ItemSpriteExtract.cs` 1
- hot, entity classes (update) (1): `Item.cs` 1
- hot, views (2): `ItemView.cs` 2

**`DisplayedGraphic`**: 25 reads
- hot, ecs (mirror, extract, systems) (1): `ItemSpriteExtract.cs` 1
- hot, views (6): `ItemView.cs` 6
- hot, scenes (walk, prepare, pick) (1): `GameSceneGpuItemCache.cs` 1
- cold, managers/actions (2): `ItemHold.cs` 1, `NameOverHeadManager.cs` 1
- cold, ui (gumps, controls) (13): `ContainerGump.cs` 3, `GridLootGump.cs` 2, `NameOverheadGump.cs` 1, `PaperdollGump.cs` 3, `TradingGump.cs` 4
- dev, dev/checkers (2): `DevBench.cs` 1, `EcsParity.cs` 1

**`IsMulti`**: 38 reads
- hot, ecs (mirror, extract, systems) (6): `ItemSpriteExtract.cs` 3, `WorldEcs.cs` 3
- hot, entity classes (update) (3): `Item.cs` 3
- hot, views (5): `ItemView.cs` 5
- hot, map/chunks (1): `Chunk.cs` 1
- hot, scenes (walk, prepare, pick) (5): `GameSceneDrawingSorting.cs` 3, `GameSceneGpuItemCache.cs` 2
- hot, world (update, lookup) (5): `World.cs` 5
- net, network (packet handlers) (3): `PacketHandlers.cs` 3
- cold, managers/actions (3): `GameActions.cs` 1, `Pathfinder.cs` 2
- cold, ui (gumps, controls) (2): `ItemGump.cs` 1, `InspectorGump.cs` 1
- cold, other (1): `UoAssist.cs` 1
- dev, dev/checkers (4): `DevBench.cs` 2, `EcsInspector.cs` 1, `EcsParity.cs` 1

**`AnimIndex`**: 24 reads
- hot, ecs (mirror, extract, systems) (7): `ItemSpriteExtract.cs` 2, `MobileAnimationSystem.cs` 3, `WorldEcs.cs` 2
- hot, entity classes (update) (5): `EntityTextContainer.cs` 1, `Item.cs` 1, `Mobile.cs` 3
- hot, views (4): `ItemView.cs` 2, `MobileView.cs` 2
- net, network (packet handlers) (1): `PacketHandlers.cs` 1
- cold, managers/actions (2): `HealthLinesManager.cs` 2
- cold, ui (gumps, controls) (2): `NameOverheadGump.cs` 2
- dev, dev/checkers (3): `EcsInspector.cs` 2, `EcsParity.cs` 1

**`ExecuteAnimation`**: 3 reads
- hot, entity classes (update) (3): `Item.cs` 1, `Mobile.cs` 2

**`LastAnimationChangeTime`**: 11 reads
- hot, ecs (mirror, extract, systems) (7): `MobileAnimationSystem.cs` 3, `WorldEcs.cs` 4
- hot, entity classes (update) (3): `Item.cs` 1, `Mobile.cs` 2
- dev, dev/checkers (1): `EcsInspector.cs` 1

**`LastStepTime`**: 7 reads
- hot, ecs (mirror, extract, systems) (1): `MobileInputRules.cs` 1
- hot, entity classes (update) (5): `Mobile.cs` 4, `PlayerMobile.cs` 1
- cold, managers/actions (1): `BoatMovingManager.cs` 1

**`AnimationFrameCount`**: 2 reads
- hot, entity classes (update) (2): `Mobile.cs` 2

**`AnimationFromServer`**: 7 reads
- hot, ecs (mirror, extract, systems) (3): `MobileAnimationSystem.cs` 3
- hot, entity classes (update) (4): `Mobile.cs` 3, `MobileAnimation.cs` 1

**`LastStepSoundTime`**: 2 reads
- hot, entity classes (update) (2): `Mobile.cs` 2

**`StepSoundOffset`**: 2 reads
- hot, entity classes (update) (2): `Mobile.cs` 2

**`Race`**: 11 reads
- hot, entity classes (update) (1): `GameObject.cs` 1
- net, network (packet handlers) (1): `OutgoingPackets.cs` 1
- cold, managers/actions (1): `MacroManager.cs` 1
- cold, ui (gumps, controls) (8): `CreateCharAppearanceGump.cs` 1, `CreateCharTradeGump.cs` 2, `InspectorGump.cs` 1, `RacialAbilitiesBookGump.cs` 3, `RacialAbilityButton.cs` 1

**`SpeedMode`**: 8 reads
- hot, entity classes (update) (7): `Mobile.cs` 2, `PlayerMobile.cs` 5
- cold, ui (gumps, controls) (1): `InspectorGump.cs` 1

**`Stamina`**: 11 reads
- hot, entity classes (update) (1): `PlayerMobile.cs` 1
- cold, managers/actions (1): `Pathfinder.cs` 1
- cold, ui (gumps, controls) (9): `HealthBarGump.cs` 2, `InfoBarGump.cs` 2, `InspectorGump.cs` 1, `StatusGump.cs` 4

**`StaminaMax`**: 10 reads
- cold, managers/actions (1): `Pathfinder.cs` 1
- cold, ui (gumps, controls) (9): `HealthBarGump.cs` 2, `InfoBarGump.cs` 2, `InspectorGump.cs` 1, `StatusGump.cs` 4

**`Mana`**: 9 reads
- cold, ui (gumps, controls) (9): `HealthBarGump.cs` 2, `InfoBarGump.cs` 2, `InspectorGump.cs` 1, `StatusGump.cs` 4

**`ManaMax`**: 9 reads
- cold, ui (gumps, controls) (9): `HealthBarGump.cs` 2, `InfoBarGump.cs` 2, `InspectorGump.cs` 1, `StatusGump.cs` 4

**`IsRenamable`**: 12 reads
- hot, scenes (walk, prepare, pick) (1): `GameSceneInputHandler.cs` 1
- hot, world (update, lookup) (2): `World.cs` 2
- cold, ui (gumps, controls) (9): `HealthBarGump.cs` 8, `InspectorGump.cs` 1

**`IsFemale`**: 14 reads
- hot, ecs (mirror, extract, systems) (3): `MobileAnimationSystem.cs` 1, `MobileInputRules.cs` 1, `WorldEcs.cs` 1
- hot, views (1): `MobileView.cs` 1
- cold, ui (gumps, controls) (9): `PaperDollInteractable.cs` 4, `RaceChangeGump.cs` 3, `StatusGump.cs` 2
- dev, dev/checkers (1): `EcsInspector.cs` 1

**`IsRunning`**: 4 reads
- hot, ecs (mirror, extract, systems) (2): `MobileAnimationSystem.cs` 1, `MobileInputRules.cs` 1
- hot, entity classes (update) (1): `MobileAnimation.cs` 1
- dev, dev/checkers (1): `EcsInspector.cs` 1

**`NotorietyFlag`**: 68 reads
- hot, ecs (mirror, extract, systems) (3): `MobileAnimationSystem.cs` 2, `MobileInputRules.cs` 1
- hot, views (5): `MobileView.cs` 5
- hot, scenes (walk, prepare, pick) (1): `GameSceneInputHandler.cs` 1
- hot, world (update, lookup) (12): `World.cs` 12
- cold, managers/actions (19): `GameActions.cs` 3, `HealthLinesManager.cs` 8, `MacroManager.cs` 1, `NameOverHeadManager.cs` 1, `TargetManager.cs` 6
- cold, ui (gumps, controls) (26): `DebugGump.cs` 1, `HealthBarGump.cs` 15, `InfoBarGump.cs` 1, `InspectorGump.cs` 1, `MiniMapGump.cs` 1, `NameOverheadGump.cs` 4, `WorldMapGump.cs` 2, `Tooltip.cs` 1
- dev, dev/checkers (2): `EcsInspector.cs` 1, `EcsSessionDriver.cs` 1

**`IsDead`**: 55 reads
- hot, ecs (mirror, extract, systems) (9): `ItemSpriteExtract.cs` 2, `MobileAnimationSystem.cs` 5, `MobileInputRules.cs` 1, `WorldEcs.cs` 1
- hot, entity classes (update) (5): `DragEffect.cs` 1, `Mobile.cs` 1, `MobileAnimation.cs` 1, `PlayerMobile.cs` 2
- hot, views (12): `GameEffectView.cs` 1, `ItemView.cs` 2, `LandView.cs` 1, `LightningEffectView.cs` 1, `MobileView.cs` 5, `MultiView.cs` 1, `StaticView.cs` 1
- hot, scenes (walk, prepare, pick) (5): `GameScene.cs` 2, `GameSceneDrawingSorting.cs` 1, `GameSceneGpuItemCache.cs` 1, `GameSceneGpuWorld.cs` 1
- net, network (packet handlers) (8): `PacketHandlers.cs` 8
- cold, managers/actions (5): `GameActions.cs` 2, `HealthLinesManager.cs` 1, `Pathfinder.cs` 2
- cold, ui (gumps, controls) (9): `PaperDollInteractable.cs` 1, `HealthBarGump.cs` 7, `InspectorGump.cs` 1
- dev, dev/checkers (2): `DevBench.cs` 1, `EcsInspector.cs` 1

**`InWarMode`**: 26 reads
- hot, ecs (mirror, extract, systems) (2): `MobileAnimationSystem.cs` 1, `MobileInputRules.cs` 1
- hot, entity classes (update) (3): `Mobile.cs` 2, `MobileAnimation.cs` 1
- hot, scenes (walk, prepare, pick) (2): `GameSceneInputHandler.cs` 2
- net, network (packet handlers) (1): `PacketHandlers.cs` 1
- cold, managers/actions (6): `GameActions.cs` 2, `GameCursor.cs` 3, `AuraManager.cs` 1
- cold, ui (gumps, controls) (11): `HealthBarGump.cs` 7, `NameOverheadGump.cs` 1, `PaperdollGump.cs` 3
- dev, dev/checkers (1): `EcsSessionDriver.cs` 1


## 4. Mutator methods, structure members and child lists

These do not write an A field by name at the call site, but they change world state or structure for the caller. Receiver types are from the compiler (second pass, section 1).

### 4.1 Methods that write world state

Call sites outside the declaring class are listed with their receiver type; calls inside `GameObject`/`Entity`/`Item`/`Mobile`/`PlayerMobile` are listed as `this`. Declarations are not listed.

**`SetInWorldTile`**: 21 call site(s); writes X, Y, Z, then `AddToTile` (-> mirror Sync)
- `DevBench.cs`: 112 (PlayerMobile), 132 (PlayerMobile), 1756 (Item)
- `Game/GameCursor.cs`: 327 (Multi)
- `Game/GameObjects/FixedEffect.cs`: 73 ((this: FixedEffect.cs))
- `Game/GameObjects/GameEffect.cs`: 151 ((this: GameEffect.cs)), 158 ((this: GameEffect.cs))
- `Game/GameObjects/House.cs`: 58 (Multi), 119 (Multi)
- `Game/GameObjects/Item.cs`: 277 (Multi)
- `Game/GameObjects/LightningEffect.cs`: 49 ((this: LightningEffect.cs))
- `Game/Managers/WalkerManager.cs`: 123 (PlayerMobile)
- `Game/Scenes/GameScene.cs`: 1102 (Item), 1110 (Multi)
- `Game/World.cs`: 181 (PlayerMobile)
- `Network/PacketHandlers.cs`: 904 (PlayerMobile), 1683 (Item), 5832 (Item), 6391 (Mobile), 6421 (Item), 6494 (PlayerMobile)

**`AddToTile`**: 10 call site(s); tile insertion (-> `Chunk.AddGameObject`, or `Map.Park` for an unloaded chunk) then mirror Sync
- `Game/GameObjects/GameObject.cs`: 115 (this), 132 (this), 219 (this)
- `Game/GameObjects/Mobile.cs`: 926 (this)
- `Game/GameObjects/PlayerMobile.cs`: 696 (this)
- `Game/Managers/BoatMovingManager.cs`: 281 (Item), 383 (Entity)
- `Game/Managers/HouseCustomizationManager.cs`: 342 (Multi)
- `Game/Map/Map.cs`: 180 (GameObject)
- `Network/PacketHandlers.cs`: 1219 (PlayerMobile)

**`RemoveFromTile`**: 7 call site(s); tile removal; clears `TileChunk`, `PendingBlock`, `TNext`, `TPrevious`
- `Game/GameActions.cs`: 498 (Item)
- `Game/GameObjects/GameObject.cs`: 140 (this), 463 (this)
- `Game/GameObjects/House.cs`: 136 (Multi)
- `Game/Map/Chunk.cs`: 193 (GameObject)
- `Game/Map/Map.cs`: 270 (GameObject)
- `Game/World.cs`: 1027 (Item)

**`UpdateScreenPosition`**: 5 call site(s); sets `IsPositionChanged`, mirror Sync
- `Game/GameObjects/Mobile.cs`: 917 (this)
- `Game/Managers/BoatMovingManager.cs`: 270 (Item), 375 (Entity)
- `Game/Map/Chunk.cs`: 157 (Land), 171 (Static)

**`FixHue`**: 9 call site(s); writes Hue (masks the server hue)
- `Network/PacketHandlers.cs`: 1838 (Item), 2823 (Mobile), 2857 (PlayerMobile), 2926 (Item), 6100 (Item), 6249 (Mobile), 6324 (Item), 6367 (Mobile), 6457 (PlayerMobile)

**`UpdateHits`**: 1 call site(s); writes `HitsPercentage` (and its text) from a percentage
- `Game/GameObjects/Entity.cs`: 187 (this)

**`SetSAPoison`**: 2 call site(s); writes `_isSA_Poisoned`, pushes `MobileInputs`
- `Network/PacketHandlers.cs`: 742 (Mobile), 753 (Mobile)

**`SetAnimation`**: 10 call site(s); writes the whole animation timer (AnimIndex, AnimationFrameCount, `_animation*`, LastAnimationChangeTime, idle due)
- `Game/GameObjects/Mobile.cs`: 395 (this), 623 (this), 732 (this), 776 (this)
- `Game/GameObjects/PlayerMobile.cs`: 645 (this)
- `Network/PacketHandlers.cs`: 2429 (Mobile), 3753 (Mobile), 4501 (Mobile), 4726 (Mobile), 5617 (Mobile)

**`SetIdleAnimation`**: 1 call site(s); writes the animation timer for an idle fidget (draws the shared RNG)
- `Game/GameObjects/Mobile.cs`: 364 (this)

**`EnqueueStep`**: 1 call site(s); appends to `Steps`, writes LastStepTime, may call `SetAnimation`
- `Network/PacketHandlers.cs`: 6355 (Mobile)

**`ClearSteps`**: 5 call site(s); clears `Steps`, zeroes `Offset`, pushes `MobileInputs`
- `Game/GameObjects/Mobile.cs`: 1143 (this)
- `Game/Managers/WalkerManager.cs`: 113 (PlayerMobile)
- `Game/World.cs`: 182 (PlayerMobile)
- `Network/PacketHandlers.cs`: 6352 (Mobile), 6362 (Mobile)

**`ProcessSteps`**: 6 call site(s); pops/advances `Steps`, writes X, Y, Z, Direction, IsRunning, Offset, LastStepTime, calls `AddToTile`
- `Game/Ecs/MobileAnimationSystem.cs`: 291 (Mobile)
- `Game/GameObjects/Mobile.cs`: 681 (this), 912 (this)
- `Game/GameObjects/Views/MobileView.cs`: 143 (this), 258 (this), 1012 (this)

### 4.2 Tile-list structure (E): who writes it

**`PriorityZ`**: 3 write(s), 10 read(s)
- write `Game/GameObjects/GameObject.cs`: 467 (this), 506 (this)
- write `Game/Map/Chunk.cs`: 300 (GameObject)
- reads: `View.cs` 1, `Chunk.cs` 1, `GameSceneDrawingSorting.cs` 1, `GameSceneGpuItemCache.cs` 3, `GameSceneGpuPrepare.cs` 3, `InspectorGump.cs` 1

**`TNext`**: 9 write(s), 51 read(s)
- write `Game/GameObjects/GameObject.cs`: 172 (this), 507 (this)
- write `Game/Map/Chunk.cs`: 306 (GameObject), 355 (GameObject), 356 (GameObject), 365 (GameObject), 403 (GameObject), 439 (GameObject), 529 (GameObject)
- reads: `DevBench.cs` 1, `GameObject.cs` 3, `Mobile.cs` 2, `BoatMovingManager.cs` 2, `TargetManager.cs` 1, `Chunk.cs` 9, `ChunkMesh.cs` 2, `Map.cs` 3, `MapObjectRecycler.cs` 4, `Pathfinder.cs` 1, `GameScene.cs` 3, `GameSceneDrawingSorting.cs` 5, `GameSceneGpuItemCache.cs` 7, `GameSceneGpuPrepare.cs` 4, `GameSceneGpuWorld.cs` 1, `MiniMapGump.cs` 2, `World.cs` 1

**`TPrevious`**: 10 write(s), 19 read(s)
- write `Game/GameObjects/GameObject.cs`: 173 (this), 507 (this)
- write `Game/Map/Chunk.cs`: 305 (GameObject), 353 (GameObject), 360 (GameObject), 366 (GameObject), 367 (GameObject), 402 (GameObject), 438 (GameObject), 528 (GameObject)
- reads: `GameObject.cs` 3, `Mobile.cs` 3, `BoatMovingManager.cs` 2, `Chunk.cs` 7, `MapObjectRecycler.cs` 1, `Pathfinder.cs` 2, `MiniMapGump.cs` 1

**`TileChunk`**: 6 write(s), 11 read(s)
- write `Game/GameObjects/GameObject.cs`: 159 (this), 508 (this)
- write `Game/Map/Chunk.cs`: 195 (GameObject), 378 (GameObject), 440 (GameObject), 530 (GameObject)
- reads: `MobileAnimationSystem.cs` 2, `GameObject.cs` 2, `Chunk.cs` 1, `MapObjectRecycler.cs` 2, `GameSceneGpuItemCache.cs` 4

**`PendingBlock`**: 4 write(s), 3 read(s)
- write `Game/GameObjects/GameObject.cs`: 174 (this), 511 (this)
- write `Game/Map/Map.cs`: 271 (GameObject), 760 (GameObject)
- reads: `Map.cs` 3

### 4.3 `Mobile.Steps` (the walk queue)

32 references: 15 call a mutating or ref-returning method (`Back()` and `Front()` return `ref Step`, so they can write), 17 read (`Count`, indexer, enumeration).

- mutating `Game/Ecs/MobileInputRules.cs`: 132 (`Front`, Mobile)
- mutating `Game/GameObjects/GameObject.cs`: 59 (`Back`, Mobile)
- mutating `Game/GameObjects/Mobile.cs`: 372 (`Clear`, this), 413 (`AddToBack`, this), 421 (`AddToBack`, this), 431 (`AddToBack`, this), 450 (`Back`, this), 647 (`Back`, this), 764 (`Front`, this), 906 (`RemoveFromFront`, this)
- mutating `Game/GameObjects/MobileAnimation.cs`: 24 (`Front`, this), 68 (`Front`, this), 225 (`Front`, this)
- mutating `Game/GameObjects/PlayerMobile.cs`: 565 (`Back`, this), 665 (`AddToBack`, this)
- reading: `MobileInputRules.cs` 1, `WorldEcs.cs` 2, `GameObject.cs` 1, `Mobile.cs` 8, `MobileAnimation.cs` 3, `PlayerMobile.cs` 1, `PacketHandlers.cs` 1

### 4.4 Child lists (`Items` and the `LinkedObject` API) on world objects

`Items` is the head of an entity's child list: a mobile's equipment, an item's contents. Writers of the list are the `LinkedObject` methods; readers walk `Items`/`Next`. Receivers that are not `Item`/`Mobile`/`PlayerMobile`/`Entity`/`GameObject` (`TextContainer`, `EffectManager`, `MacroManager`, `Macro`, `MacroObject`) are other lists and are not in the ECS plan.

**`PushToBack`**: 21 call site(s) on world objects
- `Game/UI/Gumps/CharCreation/CreateCharAppearanceGump.cs`: 267 (PlayerMobile), 274 (PlayerMobile), 278 (PlayerMobile), 282 (PlayerMobile), 289 (PlayerMobile), 293 (PlayerMobile), 297 (PlayerMobile), 308 (PlayerMobile), 312 (PlayerMobile), 316 (PlayerMobile), 322 (PlayerMobile), 326 (PlayerMobile), 330 (PlayerMobile), 353 (PlayerMobile), 361 (PlayerMobile)
- `Network/PacketHandlers.cs`: 1662 (Entity), 1843 (Entity), 2934 (Entity), 3146 (Entity), 4549 (Item), 6113 (Entity)

**`Remove`**: 4 call site(s) on world objects
- `Game/UI/Gumps/CharCreation/CreateCharAppearanceGump.cs`: 971 (PlayerMobile), 982 (PlayerMobile)
- `Game/World.cs`: 1019 (Entity)
- `Network/PacketHandlers.cs`: 1139 (Entity)

**`Clear`**: 4 call site(s) on world objects
- `Game/GameObjects/GameObject.cs`: 462 (this)
- `Game/UI/Gumps/CharCreation/CreateCharAppearanceGump.cs`: 247 (PlayerMobile)
- `Game/UI/Gumps/RaceChangeGump.cs`: 337 (PlayerMobile)
- `Network/PacketHandlers.cs`: 4525 (Item)

**`SortContents`**: 1 call site(s) on world objects
- `Network/PacketHandlers.cs`: 2733 (Item)

**`Items`** (the child list head): 3 write(s), 38 read(s) on world objects
- write `Game/GameObjects/GameObject.cs:493` (GameObject)
- write `Game/GameObjects/PlayerMobile.cs:412` (Item)
- write `Network/PacketHandlers.cs:6533` (Entity)
- reads by file: `Profile.cs` 1, `DevBench.cs` 2, `EcsInspector.cs` 1, `EcsSessionDriver.cs` 2, `WorldEcs.cs` 1, `Entity.cs` 5, `GameObject.cs` 3, `Item.cs` 2, `Mobile.cs` 2, `PlayerMobile.cs` 4, `CreateCharAppearanceGump.cs` 1, `ContainerGump.cs` 2, `GridLootGump.cs` 1, `RaceChangeGump.cs` 1, `SpellbookGump.cs` 1, `TradingGump.cs` 2, `World.cs` 2, `PacketHandlers.cs` 5


## 5. Private A state (writes inside `Mobile` and `Entity`)

These have no external writers; they are written only by the methods named. Line numbers are in `Mobile.cs` unless noted.

| Field | Write sites |
|---|---|
| `_animationGroup` | 126 (initialiser), 468 (`SetAnimation`), 562, 566, 576, 580, 591, 600, 604, 619 (`SetIdleAnimation`) |
| `_animationInterval` | 470 (`SetAnimation`), 490 (`SetIdleAnimation`) |
| `_animationRepeat` | 474, 494, 728 (`ProcessAnimation`, from the E4 `AnimationTiming`) |
| `_animationRepeateMode`, `_animationRepeatModeCount` | 129-130 (initialisers), 472-473, 491-492, 726-727 |
| `_isAnimationForwardDirection` | 475, 493 |
| `_lastAnimationIdleDelay` | 307 (`CalculateRandomIdleTime`, called from the constructor, `SetAnimation`, `SetIdleAnimation`) |
| `_isDead` | 160 (the `IsDead` setter) |
| `_isSA_Poisoned` | 300 (`SetSAPoison`) |
| `_isFemale`, `_isRunning`, `_notorietyFlag` | 233, 243, 253 (their setters) |
| `_inWarMode` (`PlayerMobile.cs`) | 42 (the `InWarMode` setter) |
| `_direction`, `_hits`, `_hitsMax` (`Entity.cs`) | 76, 46, 56 (their setters) |
| `_isMulti`, `_displayedGraphic` (`Item.cs`) | 122, 110 (their setters) |

`Mobile.GetEndPosition` and `UpdateStateHash` only read these.

## 6. Who holds references across frames

A handle that outlives its entity must read as destroyed (plan section 4; the epoch check of E5 gives that). Everything below keeps an `Item`/`Mobile`/`Entity`/`GameObject` reference longer than one call. Gumps and managers not listed here (`HealthBarGump`, `PaperDollGump`, `ContainerGump`, `StatusGump`, `NameOverheadGump`, `TradingGump`, `PartyGump`, `WorldMapGump`, `MiniMapGump`, `QuestArrowGump`, `CounterBarGump`, `TargetManager`, `PartyManager`, `JournalManager`, `DelayedObjectClickManager`, `UoAssist`, `MacroManager`, `UseItemQueue`, `World.LastObject`) keep a **serial** and look the object up at each use.

| Holder | What it holds | Lifetime | Notes |
|---|---|---|---|
| `World.Player` | the `PlayerMobile` | the session | replaced on login; `CreatePlayer` |
| `World.Items`, `World.Mobiles` (`MobileTable`) | every `Item`, every `Mobile` | until destroyed | the lookup the UI and handlers use |
| `World.TextOwners` | entities with overhead text | until the text expires | `GameObject.InTextOwners` |
| `World._activeItems`, `_destroyedItems`, `_destroyedMobiles`, `_dueObjects`, `_scanItems`, `_scanMobiles` | per-frame work lists | cleared each frame or pass | scratch lists in `World.Update` |
| `SelectedObject.Object`, `LastLeftDownObject` (`BaseGameObject`), `HealthbarObject` (`Entity`), `SelectedContainer`, `CorpseObject` (`Item`) | the picked object | until the next pick | can point at a destroyed object; callers check `IsDestroyed` |
| `GameScene._multi` | the placement-preview `Item` | while a multi is being targeted | detached (section 3) |
| `GameScene._prevMeshHighlight` (`GameObject`) | the highlighted static | until the highlight changes | |
| `GameSceneGpuItemCache` (`GpuItemCache.Entry.Item`, `Walked.Item`, `_gpuSelContainer`) | the settled ground `Item`s of each chunk | until the chunk's tile content changes | W4/E7 cache; invalidated by `OnTileContentChanged` |
| `GpuWorldRenderer._object` (`GameObject`) | the object being extracted | one extract call | |
| `RenderLists._tiles`, `_stretchedTiles`, `_statics`, `_animations`, `_effects`, `_transparentObjects` | the frame's draw lists | one frame | |
| `GameSceneGpuWorld._gpuMeshMultis`, `_gpuStatics`, `_gpuFoliage`; `Chunk.GpuMobiles` | per-chunk Gpu lists | until the next prepare | |
| `GameSceneDrawingSorting._foliages` | foliage objects of the frame | one frame | |
| `Map._parked` (`Parked.Obj`) | objects waiting for their chunk to load | until the chunk loads | `Map.Park`; holds across frames, keyed by block |
| `Chunk.Tiles`, `TNext`/`TPrevious` | the tile lists (every dynamic object on a tile) | until removed | E: T6 |
| `MapObjectRecycler` (`Retired<Land>`, `Retired<Static>`) | retired map objects | until reused | `Incarnation` tells a holder it was reused |
| `House.Components` (`List<Multi>`), `GameCursor._temp` | multi components | while the house or preview exists | map objects |
| `GameEffect.Source`, `Target` (`GameObject`), `DragEffect` | the effect's endpoints | the effect's life | effects are not ECS yet (T8) |
| `TextObject.Owner`, `TextContainer.Parent` (`EntityTextContainer`), `MessageEventArgs.Parent` | the entity a text belongs to | the text's life | |
| `Mobile.FrameCache`, `EcsEntity`, `EcsEntitySerial`, `EcsEntityEpoch` | the Gpu instances and the cached ECS handle | the mobile's life | E: handles |
| `MobileTable._slots`, `_byKey`, `_shadow` | every `Mobile` | until removed | E10 (`World.Mobiles` is a `MobileTable` on this base) |
| `WalkerManager._player` | the `PlayerMobile` | the session | |
| `InspectorGump._obj` (`GameObject`) | the land, static or detached preview the gump shows | the gump's life | gump-handles: a world entity is held by serial (`EntityRef`) and looked up at Dump; `GridLootGump` holds its corpse by serial too |
| `CharCreationGump._character`, `CreateCharAppearanceGump._character`, `CreateCharTradeGump._character`, `RaceChangeGump` (`fakeMobile`, `hair`, `beard`, and the nested `playerMobile`, `hair`, `beard`) | detached preview objects | the gump's life | section 3 |
| `Pathfinder.PathObject.Object` | the tile object of a path node | one search | |
| `ItemHold` | copies of the dragged item's fields (`Graphic`, `Hue`, `Amount`, `Flags`, `Layer`, `X`, `Y`, `Z`, `Container`) | one drag | **a snapshot, not a reference**; read back into a new `Item` by `PacketHandlers.cs:1643-1650` |
| `TargetManager._targetCallback` (`Action<GameObject>`) | a closure over whatever the caller captured | until the target resolves | cannot be audited by type |

The lambdas that capture an `Item` or `Mobile` (gump event handlers, `QueueItemUpdate` callbacks) are not listed; they die with their gump. The owner-facing consequence for T4: the only long-lived holders that matter are `SelectedObject`, `GpuItemCache`, `Map._parked`, `GridLootGump`, `InspectorGump`, `House.Components` and the detached preview objects. Everything else is per-frame or serial-keyed.

## 7. Textual hits that are not world-object writes

A grep for `.Field =` over `src/ClassicUO.Client` for the 41 A names finds 933 more hits than the 298 compiler-confirmed sites (266 world + 32 map/effect) of section 3. All of them are on a receiver that is not an `Item`, `Mobile`, `PlayerMobile` or `Entity`: UI `Control`s (`X`, `Y`, `Hue`, `Graphic`, `Name`), `Point`/`Vector`/`Rectangle` values, `Land`/`Static`/`Multi`/effect objects, `RealScreenPosition.X/Y`, snapshot structs (`MirrorRecord`, `ItemHold`), and the like. They are listed so a sample can be traced. Format: `file`: `.Field` lines. A line that has both a world write and a non-world write of the same field is listed in section 3 and not here.

**Receiver-less hits are not listed.** A bare `X = x;` or `Hue = hue;` binds to a member of the class it is written in. About 1,600 such lines exist outside the world-object classes; they are constructor and object-initialiser assignments of UI controls (`Control.X`, `GumpPic.Graphic`, `Label.Hue`), data records and structs (`ItemHold`, `Step`, `ItemInside`, `Parked`, `CustomBuildObject`, `ContainerData`, `TargetManager`'s last-target info), and cannot be world-object writes. The bare writes that do bind to a world object are inside `GameObject`, `Entity`, `Item`, `Mobile`, `PlayerMobile` (and the map and effect classes), and the compiler found all of them: they are in section 3.1 with the receiver `this`. A few receiver-less lines in those files are **construction writes** (field initialisers, listed in section 3.1a) or initialisers of other types (the `Rectangle { X = minX }` at `Item.cs:298`); the scanner skips both (section 8).

- `Configuration/Json/FNAPointJsonConverter.cs`: `.X` 34, 96; `.Y` 50, 112
- `Configuration/Profile.cs`: `.X` 667, 747; `.Y` 668, 748
- `DevBench.cs`: `.X` 103, 692, 1666, 1667, 1668, 1669; `.Y` 104, 1666, 1667, 1668, 1669
- `Game/Ecs/ItemSpriteExtract.cs`: `.Z` 113; `.X` 451; `.Y` 452
- `Game/Ecs/MobileAnimationSystem.cs`: `.Flags` 124, 153, 286, 326, 409, 834; `.Graphic` 156, 665; `.X` 161; `.Y` 162; `.Z` 163; `.Hue` 667; `.Layer` 668
- `Game/Ecs/MobileInputRules.cs`: `.Flags` 100; `.LastStepTime` 105
- `Game/Ecs/MobileSpriteExtract.cs`: `.X` 461, 479, 533, 579, 751; `.Y` 462, 480, 534, 580, 752
- `Game/Ecs/SitDeform.cs`: `.X` 53, 57, 62, 66, 199; `.Y` 72, 76, 95, 99, 200; `.Z` 105, 109, 114, 118
- `Game/Ecs/WorldEcs.cs`: `.Flags` 200, 373, 628
- `Game/GameCursor.cs`: `.Y` 402, 468, 512; `.X` 467, 511
- `Game/GameObjects/DragEffect.cs`: `.X` 69; `.Y` 70
- `Game/GameObjects/EntityTextContainer.cs`: `.X` 157, 160, 190, 199, 228; `.Y` 158, 161, 191, 214, 229
- `Game/GameObjects/GameObject.cs`: `.X` 194, 202, 301, 318, 361, 366; `.Y` 195, 203, 299, 302, 319, 374, 379
- `Game/GameObjects/Item.cs`: `.Y` 446, 449, 466, 487; `.X` 448, 465, 486
- `Game/GameObjects/Land.cs`: `.X` 191, 204, 208, 217, 221, 231, 235, 245, 249; `.Y` 192, 205, 209, 218, 222, 232, 236, 246, 250; `.Z` 193, 206, 210, 219, 223, 233, 237, 247, 251
- `Game/GameObjects/Mobile.cs`: `.X` 408, 416, 426, 842, 903, 1042, 1075; `.Y` 409, 417, 427, 843, 904, 1019, 1023, 1043, 1048, 1060, 1076; `.Z` 410, 418, 428, 840, 905; `.Direction` 411, 419, 429
- `Game/GameObjects/MovingEffect.cs`: `.X` 38, 99, 153, 157; `.Y` 100, 154, 158
- `Game/GameObjects/PlayerMobile.cs`: `.Direction` 656; `.X` 658; `.Y` 659; `.Z` 660
- `Game/GameObjects/RenderedText.cs`: `.Hue` 234; `.Y` 456, 460, 464
- `Game/GameObjects/Views/ItemView.cs`: `.Z` 148; `.Y` 441, 442, 492, 494, 562; `.X` 491, 493, 561
- `Game/GameObjects/Views/LandView.cs`: `.X` 49, 56; `.Y` 50, 57; `.Z` 61
- `Game/GameObjects/Views/LightningEffectView.cs`: `.Y` 28
- `Game/GameObjects/Views/MobileView.cs`: `.X` 38, 475, 842, 906, 916, 920, 925, 929, 993, 997; `.Y` 39, 71, 83, 88, 476, 825, 826, 847, 907, 935, 939, 958, 962, 992, 994, 998, 1078; `.Z` 908, 968, 972, 977, 981
- `Game/GameObjects/Views/MultiView.cs`: `.Z` 88; `.X` 134; `.Y` 135
- `Game/GameObjects/Views/StaticView.cs`: `.X` 127; `.Y` 128
- `Game/GameObjects/Views/View.cs`: `.X` 99; `.Y` 100
- `Game/Managers/AnchorManager.cs`: `.X` 362; `.Y` 364
- `Game/Managers/AnimatedStaticsManager.cs`: `.AnimIndex` 69, 99
- `Game/Managers/AudioManager.cs`: `.X` 106, 154; `.Y` 107, 155
- `Game/Managers/BoatMovingManager.cs`: `.Serial` 110; `.X` 114, 133, 152, 196, 251, 272, 377; `.Y` 115, 134, 153, 197, 252, 273, 378; `.Z` 116, 135, 154, 198, 249, 274, 379
- `Game/Managers/HealthLinesManager.cs`: `.X` 81, 122, 155, 158, 319, 362; `.Y` 82, 110, 114, 118, 123, 130, 159
- `Game/Managers/HouseCustomizationManager.cs`: `.X` 98, 100; `.Y` 99, 101, 943
- `Game/Managers/JournalManager.cs`: `.Hue` 39; `.Name` 40
- `Game/Managers/MessageManager.cs`: `.X` 224; `.Y` 225; `.Hue` 356, 379
- `Game/Managers/ObjectPropertiesListManager.cs`: `.Serial` 26; `.Name` 28
- `Game/Managers/PartyManager.cs`: `.Serial` 66
- `Game/Managers/SkillsGroupManager.cs`: `.Name` 232, 302, 319, 362, 382, 418, 432, 448, 489
- `Game/Managers/UIManager.cs`: `.X` 699; `.Y` 700
- `Game/Managers/WalkerManager.cs`: `.X` 120, 157, 162; `.Y` 121, 158, 163
- `Game/Managers/WorldMapEntityManager.cs`: `.X` 145; `.Y` 146; `.Name` 154
- `Game/Map/Chunk.cs`: `.X` 103; `.Y` 104
- `Game/Map/ChunkMesh.cs`: `.X` 307, 314, 464, 467, 470, 473, 482, 484, 486, 488; `.Y` 308, 315, 465, 468, 471, 474, 483, 485, 487, 489; `.Z` 319, 466, 469, 472, 475, 491, 491, 491, 491
- `Game/Map/Map.cs`: `.X` 147; `.Y` 148
- `Game/Pathfinder.cs`: `.Direction` 700, 766; `.X` 701, 767, 877, 975, 977; `.Y` 702, 768, 878, 976, 978; `.Z` 703, 769, 879
- `Game/Scenes/GameScene.cs`: `.X` 106, 641, 1145, 1428; `.Y` 107, 647, 1146, 1429; `.Z` 1416, 1512
- `Game/Scenes/GameSceneDrawingSorting.cs`: `.X` 323, 1462, 1468, 1487, 1489, 1492, 1494, 1497, 1500; `.Y` 324, 1463, 1469, 1488, 1490, 1493, 1495, 1498, 1501
- `Game/Scenes/GameSceneInputHandler.cs`: `.X` 130, 131, 135, 148, 185, 187, 193, 231, 258, 356; `.Y` 140, 141, 145, 149, 186, 188, 194, 232, 259, 357
- `Game/Scenes/GpuWorldRenderer.cs`: `.X` 779; `.Y` 780
- `Game/UI/Controls/ContextMenuControl.cs`: `.Y` 119, 254, 259, 299; `.X` 298
- `Game/UI/Controls/Control.cs`: `.X` 225; `.Y` 226
- `Game/UI/Controls/DataBox.cs`: `.Y` 28
- `Game/UI/Controls/ExpandableScroll.cs`: `.X` 99, 99, 103, 229, 242, 249; `.Y` 100, 100, 208, 214, 230, 233, 233, 237, 243, 250; `.Hue` 137, 137, 137, 137
- `Game/UI/Controls/HtmlControl.cs`: `.Hue` 138, 142
- `Game/UI/Controls/ItemGump.cs`: `.X` 150, 253; `.Y` 151, 254
- `Game/UI/Controls/Label.cs`: `.Hue` 76
- `Game/UI/Controls/NiceButton.cs`: `.Y` 49
- `Game/UI/Controls/ScrollBar.cs`: `.X` 51, 233; `.Y` 52, 234, 245, 253
- `Game/UI/Controls/ScrollFlag.cs`: `.X` 136; `.Y` 137, 141, 145
- `Game/UI/Controls/StbTextBox.cs`: `.Hue` 205, 206
- `Game/UI/Gumps/AnchorableGump.cs`: `.X` 146; `.Y` 147; `.Z` 193
- `Game/UI/Gumps/BuffGump.cs`: `.X` 74, 80, 86, 93, 110, 154, 160, 166, 172, 202, 206, 208; `.Y` 75, 81, 87, 94, 111, 155, 161, 167, 173, 203, 207, 209; `.Graphic` 109
- `Game/UI/Gumps/BulletinBoardGump.cs`: `.Y` 383, 388, 393
- `Game/UI/Gumps/CharCreation/CreateCharAppearanceGump.cs`: `.IsFemale` 221, 679, 688; `.Race` 222, 696, 719, 738; `.Hue` 378, 383, 388, 1091
- `Game/UI/Gumps/CharCreation/CreateCharCityGump.cs`: `.X` 307
- `Game/UI/Gumps/ChatGump.cs`: `.Hue` 460
- `Game/UI/Gumps/ColorPickerGump.cs`: `.Hue` 67, 78
- `Game/UI/Gumps/CombatBookGump.cs`: `.Hue` 369, 373, 386, 390; `.Graphic` 378, 395
- `Game/UI/Gumps/ContainerGump.cs`: `.Graphic` 124, 495; `.Hue` 194; `.X` 373, 606; `.Y` 374, 607
- `Game/UI/Gumps/CounterBarGump.CounterItem.cs`: `.Hue` 347, 352; `.Y` 479, 504, 505; `.X` 498, 499
- `Game/UI/Gumps/CounterBarGump.cs`: `.X` 239; `.Y` 240
- `Game/UI/Gumps/CreditsGump.cs`: `.X` 53; `.Y` 54, 63
- `Game/UI/Gumps/DebugGump.cs`: `.X` 237, 245; `.Y` 238, 246
- `Game/UI/Gumps/GridLootGump.cs`: `.X` 162, 179, 241, 374, 376, 378, 380, 510, 511; `.Y` 242, 375, 377, 379, 381, 457, 516, 517; `.Z` 571, 584
- `Game/UI/Gumps/Gump.cs`: `.X` 77, 110, 120; `.Y` 78, 115, 125
- `Game/UI/Gumps/HealthBarGump.cs`: `.Hue` 422, 438, 447, 579, 588, 592, 600, 1180, 1180, 1630, 1648, 1657, 1662, 1666, 1666, 1746, 1750, 1750, 1772, 1779, 1786, 1800, 1814; `.Graphic` 1790, 1804, 1818, 1872
- `Game/UI/Gumps/HouseCustomizationGump.cs`: `.Graphic` 221; `.Hue` 634, 641; `.X` 639
- `Game/UI/Gumps/InfoBarGump.cs`: `.X` 110; `.Hue` 172, 176
- `Game/UI/Gumps/JournalGump.cs`: `.Hue` 207; `.Graphic` 219; `.X` 223, 227, 488; `.Y` 262
- `Game/UI/Gumps/LocationGoGump.cs`: `.X` 131; `.Y` 132; `.Hue` 160
- `Game/UI/Gumps/Login/CharacterSelectionGump.cs`: `.Hue` 262, 330
- `Game/UI/Gumps/Login/LoginGump.cs`: `.Hue` 525, 530, 537, 542, 636, 637
- `Game/UI/Gumps/Login/ServerSelectionGump.cs`: `.Y` 178
- `Game/UI/Gumps/MacroButtonGump.cs`: `.Y` 66; `.Hue` 74, 81
- `Game/UI/Gumps/MapGump.cs`: `.X` 85, 157, 161; `.Y` 86, 166, 170; `.Hue` 424, 428
- `Game/UI/Gumps/MenuGump.cs`: `.X` 230
- `Game/UI/Gumps/MessageBoxGump.cs`: `.X` 90, 106, 107, 108, 242
- `Game/UI/Gumps/MiniMapGump.cs`: `.X` 263, 265; `.Y` 264, 266; `.Z` 305
- `Game/UI/Gumps/NameOverheadGump.cs`: `.X` 495; `.Y` 497; `.Hue` 540, 540, 547, 547
- `Game/UI/Gumps/NetworkStatsGump.cs`: `.X` 122, 126, 130, 134, 176, 184; `.Y` 137, 177, 185
- `Game/UI/Gumps/OptionsGump.cs`: `.Y` 685, 907, 1109, 1366, 1755, 1814, 1895, 1959, 2137, 2248, 3347, 3399, 4548, 4788, 4815; `.X` 2136, 2247, 3346, 3398, 4552, 4740, 4786, 4814; `.Hue` 2685, 2687, 2689, 2691, 3667, 3668, 3669, 3761, 3770, 3788, 3789, 3790, 3791, 3792, 3793, 3794, 3795, 3811, 3812, 3813, 3814, 3815, 3816, 3822, 3823, 3824
- `Game/UI/Gumps/PaperdollGump.cs`: `.Graphic` 63; `.X` 277, 815, 820, 821; `.Y` 816, 826, 827
- `Game/UI/Gumps/ProfileGump.cs`: `.Graphic` 144; `.X` 148, 152; `.Y` 170
- `Game/UI/Gumps/QuestArrowGump.cs`: `.Graphic` 75; `.X` 143; `.Y` 144; `.Hue` 176
- `Game/UI/Gumps/RaceChangeGump.cs`: `.Hue` 601
- `Game/UI/Gumps/ResizableGump.cs`: `.X` 69, 120, 161, 180; `.Y` 70, 125, 162, 181
- `Game/UI/Gumps/ResizableJournal.cs`: `.Hue` 65; `.X` 68, 146, 155, 160, 168, 199; `.Y` 69, 156, 161, 169, 203
- `Game/UI/Gumps/ShopGump.cs`: `.Y` 99, 106, 129, 141, 406, 407, 408, 414, 415, 415, 416, 417, 421, 864, 865; `.X` 197, 858, 859; `.Amount` 505, 526, 538, 540, 553, 570, 572; `.Hue` 733
- `Game/UI/Gumps/SkillButtonGump.cs`: `.Y` 84
- `Game/UI/Gumps/SkillGumpAdvanced.cs`: `.Graphic` 142, 326, 333, 340; `.X` 143, 279, 294, 297, 300; `.Y` 144
- `Game/UI/Gumps/SpellbookGump.cs`: `.Graphic` 65; `.X` 1454, 1459; `.Y` 1455, 1460
- `Game/UI/Gumps/StandardSkillsGump.cs`: `.Graphic` 165; `.X` 169, 173, 633, 820, 909; `.Y` 285, 286, 288, 289, 290, 291, 704; `.Name` 644
- `Game/UI/Gumps/StatusGump.cs`: `.X` 135, 190, 553, 1298; `.Y` 136, 191, 554, 1299; `.Graphic` 163, 229, 253, 277, 594, 615, 636
- `Game/UI/Gumps/SystemChatControl.cs`: `.Hue` 113, 148, 149; `.X` 442, 447, 455; `.Y` 443, 448, 456
- `Game/UI/Gumps/TradingGump.cs`: `.X` 200, 255; `.Y` 201, 256
- `Game/UI/Gumps/UseAbilityButtonGump.cs`: `.Hue` 93, 97
- `Game/UI/Gumps/UseSpellButtonGump.cs`: `.Hue` 49; `.X` 96; `.Y` 97
- `Game/UI/Gumps/WorldMapGump.cs`: `.X` 365, 1844, 1896, 2329, 2337, 2342, 2375, 2379, 2394, 2407, 2482, 2558, 2565, 2569, 2654, 2704, 2829, 2837, 2842, 2876, 2880, 2895, 3020, 3042, 3081, 3085, 3090, 3108, 3113, 3123, 3183; `.Y` 366, 1845, 1897, 2330, 2347, 2352, 2384, 2388, 2395, 2408, 2440, 2483, 2559, 2574, 2578, 2655, 2705, 2830, 2847, 2852, 2885, 2889, 2896, 2923, 3021, 3043, 3081, 3086, 3091, 3109, 3118, 3128, 3184; `.Name` 2090, 2171; `.Z` 2409
- `Game/UI/Gumps/WorldViewportGump.cs`: `.X` 138, 165, 170, 184, 195, 213, 227, 235, 241, 318; `.Y` 139, 175, 180, 185, 196, 214, 228, 236, 246, 319
- `Game/UI/Tooltip.cs`: `.Hue` 120
- `Game/Weather.cs`: `.X` 182, 336, 418, 458, 461; `.Y` 183, 337, 419, 459, 462
- `Game/World.cs`: `.Graphic` 193; `.X` 877; `.Y` 878
- `GameController.cs`: `.X` 408, 774; `.Y` 409, 775; `.Graphic` 911
- `Input/Mouse.cs`: `.X` 131, 137, 142; `.Y` 132, 138, 144
- `Main.cs`: `.Name` 51
- `Network/PacketHandlers.cs`: `.X` 906, 5851, 6447; `.Y` 907, 5852, 6448; `.Name` 3300; `.Hue` 4679, 4684, 7100

## 8. The write-site budget test (T0b)

`tests/ClassicUO.UnitTests/Ecs/WriteSiteBudgetTests.cs` scans every `.cs` file under `src/ClassicUO.Client` for direct writes to the A fields and compares the count per (file, field) with `tests/ClassicUO.UnitTests/Ecs/WriteSiteAllowlist.txt`. It fails when any pair grows or a new pair appears, and prints the new sites. It also fails when a count has shrunk (a downward ratchet): the allowlist must be lowered in the same change (`CUO_WRITE_BUDGET_REGEN=1 dotnet test --filter WriteSiteBudgetTests` rewrites it), so a removed write cannot free room for a new write in the same file and field. One limit remains: a removal and an addition in the same (file, field) pair in one change cancel out, because the budget is a count per pair; review such a change by reading the diff.

What it counts, as direct writes:
- `recv.Field op` where `op` is `=`, `+=`, `-=`, `*=`, `/=`, `%=`, `&=`, `|=`, `^=`, `<<=`, `>>=`, `??=`, `++` or `--`;
- `++recv.Field`, `--recv.Field`, `ref recv.Field`, `out recv.Field` (not `ref cond ? ... : ...`);
- `recv.Offset.X op` and the other members of `Offset`;
- `recv.Steps.AddToBack(...)`, `AddToFront`, `RemoveFromBack`, `RemoveFromFront`, `Clear`, `Insert`, `RemoveAt` (field `Steps`);
- inside the GameObject classes (`Game/GameObjects/*.cs` except the text, table and house helpers), `Field op` and `Steps.<mutator>(` with no receiver (a write to `this`), but not a declaration with an initialiser (`ushort Graphic = 1;`) and not an object-initialiser entry (`new Foo { X = 1 }`); in the effect classes only the `GameObject` fields (`Graphic`, `Hue`, `X`, `Y`, `Z`, `Offset`), because `GameEffect` has an `AnimIndex` of its own.

The fields are the 41 A members of section 3, `Steps`, and the 18 private A fields of section 5 (the animation timer, `_lastAnimationIdleDelay`, `_isSA_Poisoned` and the setters' backing fields), which only the classes themselves can write. Method calls that write state (`SetInWorldTile`, `SetAnimation`, `FixHue`, section 4) are not counted; they are the funnel's own entry points. A first run on this tree finds 326 sites in 130 (file, field) pairs: the 266 world sites, 16 on effect objects, 6 `Steps` calls (they are in 4.3), 33 writes to private A state (section 5) and 5 false positives (limit 1). Construction writes (field initialisers, section 3.1a) are not counted.

A separate test checks discovery only: the expected source files (`PacketHandlers.cs`, `World.cs`, the five entity class files) must be enumerated and nothing from `obj` or `bin`; it does not look at write counts, so a successful migration cannot fail it. The repository root is found by walking up from the test binary (and the working directory) to the folder that has both `src/ClassicUO.Client/ClassicUO.Client.csproj` and `tests/ClassicUO.UnitTests`; `CUO_REPO_ROOT` overrides it. It reads sources only, so it behaves the same in both `CuoDevTools` variants.

The scanner's own unit tests cover compound assignment, `++`, `ref`/`out`, casts, call receivers, comments, string and char literals (regular, verbatim, interpolated), `==`/`!=`/`<=`/`>=`/`=>`, object initialisers, `var` and `foreach` locals, receiverless `++X`, `ref X` and `out X` (for example `Interlocked.Exchange(ref LastAnimationChangeTime, 0L)`), tuple deconstruction, null-conditional assignment, the private animation and pushed-input fields, and the allowlist format. Fixtures replace fixed minimum counts.

**Scanner limits (regex, no compiler):**
1. **Receiver type is guessed.** A receiver counts as a world object when its last name is declared with `Item`, `Mobile`, `Entity`, `PlayerMobile` or `GameObject` (fields, locals, parameters, `foreach`, pattern variables, `Item[]`, `List<Item>`, comma lists), or is initialised from a producer (`World.Player`, `Items.Get`, `Mobiles.Get`, `World.Get`, `GetOrCreateItem`/`Mobile`, `Item.Create`, `new Item(`, `FindItem*`, ...), or is a chain ending in a member that some class declares with a world type (`SelectedObject.HealthbarObject`, `_world.Player`), or is a cast (`((Item)o).X`), or is a call to one of those producers. Declarations are matched per file, **not per scope**: a name that is a world type in one method counts as a world type in every method of the file. This over-counts (five known false positives: `MovingEffect.cs` 99-100, `BoatMovingManager.cs` 196-198, where a local struct has the same name as an `Item` local elsewhere in the file) and never under-counts for the names it knows.
2. **It misses** a write through a receiver whose type it cannot infer: a lambda parameter (`items.ForEach(i => i.Hue = 0)`), a `var` initialised from something that is not a listed producer (`var it = Helper();`), a `dynamic`, a method call that returns a world object other than the listed ones, a deconstruction into a list that mixes more than one tuple level (`((a.X, a.Y), z) = ...`), an object initialiser on a world type (`new Item(w) { Graphic = 1 }`), and a `Steps` mutator reached through a method call (`GetMobile().Steps.Clear()` is found only when the producer is listed). None of these occurs in the tree today (checked against the compiler list). A funnel call (`WorldWriter...`) is invisible to it, by design.
3. **A line is not a statement.** The scanner is line-agnostic (it works on the stripped text), but a bare `Field op` in a GameObject class is judged by what precedes it (`;`, `{`, `}`, `)`, `else`, start of text are statements; a type name is a declaration; `,` is an initialiser entry) and by what follows it (the first top-level `,` or `}` makes it an initialiser entry, `;` a statement). Pathological formatting can fool both.
4. **Strings and comments** are blanked first (regular, verbatim, interpolated, char literals, `//` and `/* */`). Raw string literals (`"""`) are treated as ordinary strings, which is good enough for blanking; there are none in the client. `#if` is not evaluated: code in inactive regions is scanned like the rest (there are none with writes).
5. **Counts are per (file, field), not per line.** Moving a write within a file does not change the budget. Moving it to another file raises the budget there and the test fails until the allowlist is regenerated; say why in the commit.
6. **It scans `src/ClassicUO.Client` only.** `src/ClassicUO.Ecs` (the mirror's own writes) and the tests are not scanned.
7. **Generated code, partial classes in other projects** are not seen.
8. **Why not Roslyn.** A syntax-only scan (Microsoft.CodeAnalysis.CSharp) would remove the formatting and initialiser guesses (limit 3) and parse deconstructions exactly, but it still has no types: a receiver's type needs a semantic model, which means compiling the whole client with all its references (FNA, SDL, the other projects) inside a unit test. That costs far more than it buys, so the type inference of limit 1 stays regex-based. T1 can replace the budget with the compile-time version of the same idea: make the A fields properties with an internal setter reached only by the funnel, and the compiler becomes the guard.

Against the compiler-derived list of section 3 the scanner finds every one of the 266 world sites and the 16 effect-object sites (the 16 `Land`/`Static`/`Multi` writes are not counted: those are not world types), plus the five false positives of limit 1. It was compared after the allowlist was generated, with `CUO_WRITE_BUDGET_DUMP`. The comparison is a one-off (`CUO_WRITE_BUDGET_DUMP=<file>` writes the scanner's sites as `file:line:field:receiver:kind` so it can be repeated).

## 9. Observations for T0c and T1

1. **One file is the writer.** `Network/PacketHandlers.cs` holds 121 of the 266 world write sites. `UpdateGameObject` alone has 31 (the shared helper of 0x1A, 0xF3, 0x77, 0xD2, 0x78, 0xD3 and 0xF6); `ExtendedCommand` (0xBF) has 12, `CharacterStatus` (0x11) 10, `DenyMoveItem` (0x27) 9, `UpdateObject` 7, `AddItemToContainer` and `MobileAttributes` 6 each. T1's packet groups (plan section 3, T1) map onto the rows of section 3.2.
2. **The step system is already the single writer of position for walking mobiles**: `Mobile.ProcessSteps` writes `X`, `Y`, `Z`, `Direction`, `IsRunning` and `Offset` (Mobile.cs:897-905, 840-843). `BoatMovingManager` is the second position writer: it writes `X`, `Y`, `Z`, `Offset` and `LastStepTime` on boat `Item`s (104, 133-135, 249-252, 267-274, 296) and on the `Entity`s the boat carries (152-154, 372-379, 390), and calls `AddToTile` and `UpdateScreenPosition` itself (270, 281, 375, 383), without `SetInWorldTile`. T1 must route it, and T6c's step system must not assume `ProcessSteps` is the only mover.
3. **`ContainerGump` writes `item.X`/`item.Y`** (ContainerGump.cs:643-656): it clamps a contained item's grid position to the gump's bounds when it draws. `X`/`Y` are container coordinates for a contained item, and the UI writes them back; the ECS needs a separate component (or the funnel needs to know the item is contained), and this write needs a funnel call or must be removed (clamp on read).
4. **`PacketHandlers.cs:1643-1650` writes eight fields in a row** from `ItemHold` (`Graphic`, `Hue`, `Amount`, `Flags`, `Layer`, `X`, `Y`, `Z`): `DenyMoveItem` (0x27) re-creating the dragged item when the server refuses the move. It is one funnel operation (`UpsertWorldItem` with an explicit field set), not eight.
5. **`DisplayedGraphic` has no world writer.** The setter is dead on `Item`; `ItemHold` has its own field with the same name. T0c can remove the setter (the getter stays B).
6. **Eight handlers follow a `Graphic` write with `CheckGraphicChange`; five `Graphic` writes do not.** The calls are PacketHandlers.cs 893, 1651, 2822, 2856, 2932, 6098, 6247 and 6334, `GameScene.cs` 1103 and `DevBench.cs` 1755. For a `Mobile` it rewrites `IsFemale` and `Race` (Mobile.cs:1083-1136); for an `Item` it sets `AllowedToDraw` and, for a corpse, `AnimIndex`, `UsedLayer`, `Layer` and `Direction`. The writes without it are `EquipItem` 1835, the spellbook 4524 and spell item 4546 (0xBF), the second mobile branch of `UpdateGameObject` 6366 and `UpdatePlayer` 6455; the character-creation gumps set `IsFemale` and `Race` by hand. The funnel's `SetGraphic` must keep exactly this split (plan Q6: parity first, fixes behind their own switch).
7. **`Entity.Destroy` writes `AnimIndex` and `LastAnimationChangeTime`; `GameObject.Destroy` writes `Graphic`, `Hue` and `Offset`; `ResetForReuse` writes those and `X`, `Y`, `Z`.** After T4 these are the "clear the cache" step and need no funnel.
8. **The death re-key writes `Serial` and every child's `Container` outside `World`** (`DisplayDeath`, 0xAF, PacketHandlers.cs:3727-3732): `world.Mobiles.Remove(owner.Serial)`, then `Container = serial | 0x80000000` on each child, then `Mobiles[serial] = owner`, `owner.Serial = serial`, `Ecs.OnSerialChanged`. `Serial` has only five world writers (two are constructors/factories, `Item.Create` and `Mobile.Create`; one is the 0xBF spellbook item at 4545), which makes the funnel's `Rekey` a small operation.
9. **Reads of `X`/`Y`/`Z`/`Graphic`/`Serial` dominate** (each is read 240 to 330 times, half or more of them in hot code). These are the fields T4 must keep as write-through caches. The cold fields (`Name`, `Hits`, `HitsMax`, `Amount`, `Price`, `Container`, `Layer`, `Flags`, `Direction`) have 2 to 70 readers each, mostly UI and handlers, and move to ECS reads cheaply.
10. **Only `Direction` syncs the mirror from its setter.** Six other setters push `MobileInputs` (`IsFemale`, `IsRunning`, `NotorietyFlag`, `IsDead`, `InWarMode`, `SetSAPoison`). Every other A write relies on a later explicit `Ecs.Sync` (23 call sites: 9 in `PacketHandlers.cs`, 5 in `DevBench.cs`, 4 in `World.cs`, 3 in `GameObject.cs`, 1 each in `Entity.cs` and `ContainerGump.cs`) or on `AddToTile`. That gap is what the 266 sites of section 3.1 and the funnel of T1 close.
