# Equipment layering rules as data (`layerrules.bin`)

This page describes `layerrules.bin`, the pack file that holds the equipment layering rules (worn-layer draw order, covering and gargoyle remaps) as data. It covers the binary layout, the JSON form a shard edits, how the converter writes and verifies the file, how the client evaluates it (including the per-mobile cache), and how the tests compare the default data with the former hard-coded rules. The file is part of the pack format; see [pack-format.md](pack-format.md) for the shared header, versioning and the other files.

## What the rules replace

Upstream ClassicUO hard-codes four things: the draw order of worn layers (`PaperdollOrder.Build`), the cloak move by facing direction (`ApplyDirectionCloak`), whether a layer is covered by another item (`MobileView.IsCovered`), and the gargoyle AnimID remaps (`FixGargoyleEquipments`, plus the paperdoll's shroud remap). `layerrules.bin` expresses all of them as a table of rules. A shard can replace the file to add rules for custom equipment.

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

Stage `layers` of `poc/Uo.Converter` writes `layerrules.bin` (`Stage("layerrules", ...)` in `poc/Uo.Converter/Program.cs`). It reads nothing from the install. A full conversion includes the stage. By default it writes `LayerRulesDefault.Create()`; with `--layer-rules <file.json>` it writes the rules from that JSON file.

| Flag | Effect |
|---|---|
| `--only layers` | Convert only this stage into an existing pack and keep every other file. |
| `--layer-rules <file.json>` | Write the rules from the JSON file (`LayerRulesJson.Load`) instead of the built-in rules. The file is read and checked before the converter clears or writes anything: an error prints `layer rules: <message>` naming the entry and stops the run with exit code 1, and the pack stays as it was. The flag is ignored with a warning when the run does not convert `layers`. |
| `--export-layer-rules <file.json>` | Write the built-in rules as JSON and exit. It needs no client directory or pack, and writes only that file. |

```
dotnet run -c Release --project poc/Uo.Converter -- --export-layer-rules rules.json
(edit rules.json)
dotnet run -c Release --project poc/Uo.Converter -- --uo <client dir> --out <pack dir> --only layers --layer-rules rules.json
```

The built-in rules export and convert back to the identical 3,055 bytes.

The verify step (`CheckLayerRules` in `poc/Uo.Converter/Verify.cs`) compares an existing `layerrules.bin` byte for byte with the JSON given by `--layer-rules`, or with the built-in rules when no JSON is given. It prints `EXACT`, or `MISMATCH` and fails verification. A pack built from a custom JSON therefore shows `MISMATCH` against the built-in rules: pass the same JSON to verify it, also with `--verify-only --only layers --layer-rules <file.json>`. A run with `--only` and without `layers` skips the check.

The pack has no entry in its required-file list for `layerrules.bin`: the file is optional.

## The JSON form

`LayerRulesJson` reads and writes the same data as the binary file. Comments (`//`) and trailing commas are allowed. All four top-level keys are required. An unknown key, a key given twice, an unknown name or a value out of range is an error that names the entry. The converter applies the same checks as the client's loader (see "Load-time checks"), so a file it writes always loads.

```jsonc
{
  "layerRules": 1,                      // format version
  "bodies": [ { "body": "0x0190", "group": "HumanMale" } ],
  "tables": [ ["Invalid", "Cloak", "Shirt" /* ... 25 layers */] ],
  "rules": [
    { "set": "world", "kind": "select", "table": 0, "when": [ { "anim": "Arms", "in": ["0x0210", "0x03B3"] } ] },
    { "set": "world", "kind": "reorder", "op": "moveTo", "layer": "Pants", "other": "Shirt", "when": [ { "present": "Shirt" } ] },
    { "set": "world", "kind": "cover", "layer": "Hair", "covered": true, "when": [ { "present": "Helmet" } ] },
    { "set": "world", "kind": "dirmove", "layer": "Cloak", "directions": [0], "op": "top", "when": [] },
    { "set": "world", "kind": "remap", "from": "0x01D5", "to": "0x0156", "when": [ { "flag": ["gargoyleDraw"] } ] }
  ]
}
```

- Graphics, AnimIDs and bodies are JSON numbers or strings, decimal (`"400"`) or hex (`"0x0190"`). A range is a string `"lo-hi"`, inclusive. Layers are names (`Invalid`, `OneHanded`, `TwoHanded`, `Shoes`, `Pants`, `Shirt`, `Helmet`, `Gloves`, `Ring`, `Talisman`, `Necklace`, `Hair`, `Waist`, `Torso`, `Bracelet`, `Face`, `Beard`, `Tunic`, `Earrings`, `Arms`, `Cloak`, `Backpack`, `Robe`, `Skirt`, `Legs`) or numbers 0 to 24. Exports use names and 4-digit hex.
- `bodies` map a body graphic to a group: `Other`, `HumanMale`, `HumanFemale`, `ElfMale`, `ElfFemale`, `GargoyleMale`, `GargoyleFemale`.
- `tables` are the base draw orders: 25 layers each, every layer once, at most 255 tables.
- Every rule has `set` (`"paperdoll"` or `"world"`), `kind`, and `when` (all conditions must hold; empty means always). Per kind: `select` takes `table`; `reorder` takes `op` (`moveTo`, `moveAfter`, `swapIfAfter`), `layer` and `other`; `cover` takes `layer` and `covered`; `dirmove` takes `layer`, `directions` (0 to 7, or `"other"` for 8 and up), `op` (`top`, `bottom`, `before`) and `anchor` for `before`; `remap` takes `from` and `to`.
- Conditions, each with an optional `"not": true`: `{ "present": layer }`, `{ "graphic": layer, "in": [...] }`, `{ "anim": layer, "in": [...] }`, `{ "flag": [...] }` with `female`, `gargoyleDraw` and `cv7000`, `{ "group": [...] }` and `{ "body": [...] }`. The `select`, `reorder` and `dirmove` rules see AnimIDs only (use `anim` or `present`). A `remap` may test only the wearer (`flag`, `group`, `body`).

## How the client evaluates it

`ClassicUO.Game.Data.LayerRuleEval` (`src/ClassicUO.Client/Game/Data/LayerRuleEval.cs`) wraps `Uo.Pack.LayerRules`, the allocation-free evaluator (`BuildOrder`, `ApplyDirection`, `IsCovered`, `Remap`). It loads the rules once:

1. If the pack has `layerrules.bin`, it reads it (`Source` = "pack").
2. If the file is missing, or unreadable (`InvalidDataException`, `IOException`, `EndOfStreamException`), it uses `LayerRulesDefault.Create()` (`Source` = "built-in") and logs an error in the unreadable case.

Callers: `MobileView` (draw and pick), `PaperDollInteractable`, `MobileAnimationSystem` and `MobileSpriteExtract` (ECS slots, with interned per-facing orders in `EquipDirOrders`), and `ItemView` / `ItemSpriteExtract` (corpses).

### The per-mobile cache

The world draw and pick do not evaluate the rules every frame. Each mobile's `WornCache` holds a `LayerDecision` (`LayerRuleEval.cs`, created at first use, 408 bytes per mobile) with the wearer's full world-set answer: the filtered base order, the order after the direction moves (filled lazily for each facing class 0 to 7, and one for 8 and up), the covered flag of every worn layer, and the remapped AnimID of every worn layer.

The cache key is everything the rules read: the rules instance, the wearer's subject (body and its group, and the female, gargoyle-draw and 7.0.0.0 flags), the worn slots' version, and the worn items' `Look` stamp (any write to a worn item's graphic or hue). A lookup that sees no equipment change anywhere in the mirror since the last check is a few compares; after a change somewhere, it re-reads the one `Look` stamp. A miss refills the answer from the rules. The facing only selects the direction class to read. The paperdoll and corpses are not cached.

## The hard-coded rules

The client always uses the rules data. T9 removed the `ecs_layer_rules` switch, its `CUO_ECS_LAYER_RULES` override and the `CUO_LAYER_RULES_CHECK` checker, along with the hard-coded code they compared against. The driver still accepts its `layerrules` command but ignores it. An old `settings.json` that still has the key loads as before.

The hard-coded code is kept, unchanged, as a test fixture: `tests/ClassicUO.UnitTests/Ecs/LegacyLayerRulesReference.cs` (`Build`, `ApplyDirectionCloak`, `IsCovered`, `FixGargoyleEquipment`, `RemapPaperdoll`). `LayerRulesTests` checks that the default data gives exactly the fixture's answers for every body group, every relevant combination of values and every direction. It also checks that every single-entry mutation of the default file is caught. `AnimationRulesTests`, `AnimationRulesEdgeTests` and `LayerRulesEcsTests` compare against the same fixture. Do not change the fixture to follow a data change: those tests exist to catch the difference.

The ECS slots keep one hard-coded fallback. If the table of interned per-facing orders (`EquipDirOrders`) is ever full, `AnimationRules.PaintOrder` moves the cloak by facing with `CloakAnchor`, as the classic client does.

