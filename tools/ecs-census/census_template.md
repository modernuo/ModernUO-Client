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
| `Offset` | **A** hot | interpolation offset of a walking mobile or moving boat part; B once steps are a system (T6c) |
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
| `IsDamageable` | **A** cold | |
| `LightID` | **A** hot | the lights rule reads it |
| `UsedLayer` | **A** cold | corpse flag (running flag of the death direction) |
| `IsMulti` (`_isMulti`) | **A** cold | multi flag; the setter resets `MultiInfo`, `MultiDistanceBonus` |
| `DisplayedGraphic` (`_displayedGraphic`) | A cold | display override; **no world writer exists** (section 3: the only setters are on `ItemHold`, which has its own field) |
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
| `Race`, `IsFemale` | **A** cold | `IsFemale` is a pushed input (`MobileInputs`); both are also set by `CheckGraphicChange` |
| `IsRunning` | **A** hot | pushed input; written by the step system and the move packets |
| `NotorietyFlag` | **A** hot | pushed input (hue override); 69 reads |
| `IsDead` (`_isDead`) | **A** hot | pushed input; the getter also derives from `Graphic` |
| `InWarMode` | **A** | virtual: `Mobile` derives it from `Flags.WarMode` and ignores the setter; `PlayerMobile` stores `_inWarMode`; pushed input |
| `_isSA_Poisoned` (`SetSAPoison`, `SAPoisoned`) | **A** cold | pushed input |
| `Stamina`, `StaminaMax`, `Mana`, `ManaMax` | **A** cold | status packets |
| `IsRenamable` | **A** cold | |
| `SpeedMode` | **A** cold | player only in practice (0xBF.26) |
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

{{SUMMARY}}
What the table says:
- **266 world write sites in 14 files.** 121 are in `PacketHandlers.cs`, 66 inside the five entity classes (`GameObject` 12, `Mobile` 40, `Item` 8, `Entity` 4, `PlayerMobile` 2), 34 in the two character-creation gumps (detached objects, see below), 24 in `BoatMovingManager`, 12 in `DevBench`, 4 in `ContainerGump`, 3 in `GameScene` (the placement preview), 1 each in `World` and `CorpseManager`. A further 32 writes are on map and effect objects.
- {{READ_TOTAL}} references are reads, about {{RW_RATIO}} reads per world write. The hot readers are the views, the walk and the extract; the UI reads mostly `Name`, `Hits`, `HitsMax`, `NotorietyFlag`, `Graphic` and `Serial`.

**Detached objects.** These are `Item`/`Mobile` instances that are in no `World` dictionary and need no ECS entity:
- `CreateCharAppearanceGump._character` (a `PlayerMobile`) and its `CreateItem` items;
- `RaceChangeGump.fakeMobile`, `hair` and `beard`;
- `GameScene._multi` (the house placement preview, `Item.Create(_world, 0)`);
- the spellbook entries of 0xBF (`PacketHandlers.cs:4543-4548`: `Item.Create(world, cc)`, "FIXME: should i call Item.Create?").
Their writes (`CreateCharAppearanceGump` 248-344, 645-650, 752, 983-986; `RaceChangeGump` 338-505; `GameScene.cs` 1062-1064; `PacketHandlers.cs` 4545-4548) must either keep working on plain GameObjects or go through a funnel mode that does not mirror. T1 has to decide; the funnel must not create entities for them.

### 3.1 Write sites

{{WRITES}}
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

{{HANDLERS}}

### 3.3 Readers by file

{{READERS}}

## 4. Mutator methods, structure members and child lists

These do not write an A field by name at the call site, but they change world state or structure for the caller. Receiver types are from the compiler (second pass, section 1).

{{METHODS}}

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
| `GridLootGump._corpse` (`Item`), `InspectorGump._obj` (`GameObject`) | the object the gump shows | the gump's life | can outlive the corpse; the gump closes on `Destroy` of an opened `Item` |
| `CharCreationGump._character`, `CreateCharAppearanceGump._character`, `CreateCharTradeGump._character`, `RaceChangeGump` (`fakeMobile`, `hair`, `beard`, and the nested `playerMobile`, `hair`, `beard`) | detached preview objects | the gump's life | section 3 |
| `Pathfinder.PathObject.Object` | the tile object of a path node | one search | |
| `ItemHold` | copies of the dragged item's fields (`Graphic`, `Hue`, `Amount`, `Flags`, `Layer`, `X`, `Y`, `Z`, `Container`) | one drag | **a snapshot, not a reference**; read back into a new `Item` by `PacketHandlers.cs:1643-1650` |
| `TargetManager._targetCallback` (`Action<GameObject>`) | a closure over whatever the caller captured | until the target resolves | cannot be audited by type |

The lambdas that capture an `Item` or `Mobile` (gump event handlers, `QueueItemUpdate` callbacks) are not listed; they die with their gump. The owner-facing consequence for T4: the only long-lived holders that matter are `SelectedObject`, `GpuItemCache`, `Map._parked`, `GridLootGump`, `InspectorGump`, `House.Components` and the detached preview objects. Everything else is per-frame or serial-keyed.

## 7. Textual hits that are not world-object writes

A grep for `.Field =` over `src/ClassicUO.Client` for the 41 A names finds {{NONWORLD_COUNT}} more hits than the 298 compiler-confirmed sites (266 world + 32 map/effect) of section 3. All of them are on a receiver that is not an `Item`, `Mobile`, `PlayerMobile` or `Entity`: UI `Control`s (`X`, `Y`, `Hue`, `Graphic`, `Name`), `Point`/`Vector`/`Rectangle` values, `Land`/`Static`/`Multi`/effect objects, `RealScreenPosition.X/Y`, snapshot structs (`MirrorRecord`, `ItemHold`), and the like. They are listed so a sample can be traced. Format: `file`: `.Field` lines. A line that has both a world write and a non-world write of the same field is listed in section 3 and not here.

**Receiver-less hits are not listed.** A bare `X = x;` or `Hue = hue;` binds to a member of the class it is written in. About 1,600 such lines exist outside the world-object classes; they are constructor and object-initialiser assignments of UI controls (`Control.X`, `GumpPic.Graphic`, `Label.Hue`), data records and structs (`ItemHold`, `Step`, `ItemInside`, `Parked`, `CustomBuildObject`, `ContainerData`, `TargetManager`'s last-target info), and cannot be world-object writes. The bare writes that do bind to a world object are inside `GameObject`, `Entity`, `Item`, `Mobile`, `PlayerMobile` (and the map and effect classes), and the compiler found all of them: they are in section 3.1 with the receiver `this`. A few receiver-less lines in those files are **construction writes** (field initialisers, listed in section 3.1a) or initialisers of other types (the `Rectangle { X = minX }` at `Item.cs:298`); the scanner skips both (section 8).

{{NONWORLD}}

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
