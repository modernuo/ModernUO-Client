<!-- TODO: refresh after the layer-rules doc update lands -->
# Equipment layering rules as data (`layerrules.bin`)

This page describes `layerrules.bin`, the pack file that holds the equipment layering rules (worn-layer draw order, covering and gargoyle remaps) as data. It covers the binary layout, how the converter writes the file, how the client evaluates it, and the setting and environment variables that control it. The file is part of the pack format; see [pack-format.md](pack-format.md) for the shared header, versioning and the other files.

## What the rules replace

ClassicUO hard-codes four things: the draw order of worn layers (`PaperdollOrder.Build`), the cloak move by facing direction (`ApplyDirectionCloak`), whether a layer is covered by another item (`MobileView.IsCovered`), and the gargoyle AnimID remaps (`FixGargoyleEquipments`, plus the paperdoll's shroud remap). `layerrules.bin` expresses all of them as a table of rules. A shard can replace the file to add rules for custom equipment.

## File layout

Magic `LAYR` (0x5259414C), version 1, kind 0 (`LayerRulesConst`, `poc/Uo.Pack/LayerRules.cs`). After the 16-byte `FileHeader`:

| Part | Layout |
|---|---|
| `LayerRulesHeader` | six i32: bodyCount, layerCount (must be 25), tableCount, ruleCount, condCount, rangeCount |
| `BodyGroupEntry[bodyCount]` | 4 bytes: body u16, group u8, pad u8. Groups: 0 other, 1/2 human male/female, 3/4 elf male/female, 5/6 gargoyle male/female. An unlisted body is group 0. The first entry for a body wins. |
| `u8 tables[tableCount][25]` | base draw orders, back to front. Entry 0 is the Invalid sentinel. Each layer appears once per table. |
| `LayerRule[ruleCount]` | 12 bytes: set u8, kind u8, layer u8, other u8, op u8, condCount u8, condStart u16, value u16, value2 u16 |
| `LayerCond[condCount]` | 8 bytes: kind u8, layer u8, negate u8, rangeCount u8, rangeStart u16, pad u16 |
| `LayerRange[rangeCount]` | 4 bytes: lo u16, hi u16 (inclusive) |

There is nothing after the last array. The reader refuses trailing bytes. Layers are the client's `Layer` byte values (0x01 OneHanded to 0x18 Legs).

### Rules

`set` 0 is the paperdoll, 1 is the world. All conditions of a rule must hold for it to match.

| Kind | Name | Fields | Meaning |
|---|---|---|---|
| 0 | Select | `value` = table index | Picks the base table. The first matching select of the set wins. The set's last select must have no conditions (the default). |
| 1 | Reorder | `layer` A, `other` B, `op` | Every matching rule, in file order, moves A relative to B. Op 0 MoveTo (A takes B's index), 1 MoveAfter (A right after B), 2 SwapIfAfter (swap if B comes first). |
| 2 | Cover | `layer` target, `other` 1 covered or 0 drawn | The first matching cover rule for the target layer decides. No match means drawn. |
| 3 | DirMove | `layer`, `value` direction mask, `op`, `other` | For facings in the mask (bit d for d 0..7, bit 8 for 8 and above), moves `layer`: op 0 to the top, 1 to the bottom, 2 just below `other` (on top when `other` is absent). Runs after the Backpack/Mount filter. |
| 4 | Remap | `value` AnimID, `value2` replacement | A drawn equipment AnimID `value` becomes `value2` (first match). Conditions may read the wearer only (kinds 3 to 5). |

### Conditions

| Kind | Name | Test |
|---|---|---|
| 0 | Present | an item is on `layer` |
| 1 | Graphic | the item's graphic on `layer` is in one of the ranges (an absent layer reads 0) |
| 2 | AnimId | the item's AnimID on `layer` is in one of the ranges (an absent layer reads 0) |
| 3 | Flag | `layer` is a mask over the caller's flags: 1 female, 2 gargoyle draw, 4 client 7.0.0.0 or later |
| 4 | Group | the wearer's body group is in the ranges |
| 5 | Body | the wearer's body graphic is in the ranges |

`negate` inverts the result. Select, Reorder and DirMove read the order key instead of graphics: the AnimID per layer, present when non-zero. The paperdoll's key includes the held item's preview.

### Load-time checks

The reader (`LayerRules.Read`, `LayerRules` constructor) refuses a file when:

- layerCount is not 25, bodyCount or ruleCount exceeds 2^20, tableCount exceeds 255, or condCount or rangeCount exceeds 0xFFFF;
- a table holds a layer twice or a layer of 25 or more;
- a rule has set above 1 or kind above 4, or its conditions or a condition's ranges run past their arrays;
- a Select names a table that does not exist, a Reorder uses a layer of 25 or more or an op above 2, a Cover has a target of 32 or more or `other` above 1, a DirMove has an op above 2;
- a Remap condition reads a layer (kind below 3);
- a Present, Graphic or AnimId condition names a layer of 25 or more;
- a set has no unconditional Select as its last Select.

The client then logs the error and uses the built-in rules.

## The default file

The default rules live in code: `Uo.Pack.LayerRulesDefault.Create()` (`poc/Uo.Pack/LayerRulesDefault.cs`). It builds 12 body entries, 3 base tables and 92 rules (per set: 5 selects, 9 reorders, 25 covers; world only: 3 direction moves and 10 gargoyle remaps; paperdoll only: 1 remap, the gargoyle ghost's shroud 0x3CA to 0x223 on client 7.0.0.0 or later for bodies 0x2B6 and 0x2B7). With 130 conditions and 187 ranges the file is 3,055 bytes. Corpses use the world set's order and direction moves with no flags, and no cover or remap pass.

## How the converter produces it

The file has no install input. Stage `layers` of `poc/Uo.Converter` writes `LayerRulesDefault.Create()` to `layerrules.bin` (`poc/Uo.Converter/Program.cs`, `Stage("layerrules", ...)`). A full conversion includes the stage. To add it to an existing pack:

```
dotnet run -c Release --project poc/Uo.Converter -- --out <pack dir> --only layers
```

The verify step (`CheckLayerRules` in `poc/Uo.Converter/Verify.cs`) compares an existing `layerrules.bin` byte for byte with the default. It prints `EXACT`, or `MISMATCH` and fails verification for a custom file. A run with `--only` and without `layers` skips the check.

The pack has no entry in its required-file list for `layerrules.bin`: the file is optional.

## How the client evaluates it

`ClassicUO.Game.Data.LayerRuleEval` (`src/ClassicUO.Client/Game/Data/LayerRuleEval.cs`) wraps `Uo.Pack.LayerRules`, the allocation-free evaluator (`BuildOrder`, `ApplyDirection`, `IsCovered`, `Remap`). It loads the rules once:

1. If the pack has `layerrules.bin`, it reads it (`Source` = "pack").
2. If the file is missing, or unreadable (`InvalidDataException`, `IOException`, `EndOfStreamException`), it uses `LayerRulesDefault.Create()` (`Source` = "built-in") and logs an error in the unreadable case.

Callers: `MobileView` (draw and pick), `PaperDollInteractable`, `MobileAnimationSystem` and `MobileSpriteExtract` (ECS slots, with interned per-facing orders in `EquipDirOrders`), and `ItemView` / `ItemSpriteExtract` (corpses).

## Switching it on

| Control | Name | Default | Notes |
|---|---|---|---|
| Setting | `ecs_layer_rules` (`Settings.GlobalSettings.EcsLayerRules`, `src/ClassicUO.Client/Configuration/Settings.cs`) | off | Key in `settings.json` next to the executable. |
| Environment | `CUO_ECS_LAYER_RULES` = `1` or `0` | unset | Overrides the setting. |
| Runtime command | ECS session driver `layerrules on` or `off` (`EcsSessionDriver.cs`) | none | Overrides both. Marks every mobile's equipment dirty so cached slots rebuild. |

Precedence, highest first: driver override, environment, setting (`LayerRuleEval.On`). With the switch off, the client uses the hard-coded `PaperdollOrder`, `IsCovered` and `FixGargoyleEquipments` code.

## The checker: `CUO_LAYER_RULES_CHECK`

Set `CUO_LAYER_RULES_CHECK=1` to compare the two implementations. It works only in builds with the `CUO_DEVTOOLS` define; other builds ignore it (`CheckEnabled` is a constant false). With it on, every order, cover and remap decision in the world, the paperdoll, corpses and the ECS slot rebuild is computed both ways. A difference logs `[layers] mismatch (...)` as a warning (the first 200 only) and counts in `LayerRuleEval.Mismatches`. While the counters change, a summary line `[layers] checks N mismatches M rules <source> ecs_layer_rules=on|off` is logged at most every 5 seconds. The check runs whether the switch is on or off.
