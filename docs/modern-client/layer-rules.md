# Equipment layering rules (`layerrules.bin` from JSON)

For shard admins. The client draws a character's clothes and armour in a fixed back-to-front order, hides some layers under others (a closed robe hides the shirt, a hood hides the hair) and swaps some equipment art for gargoyles. ClassicUO hard-codes these rules. Our pack carries them as data, `layerrules.bin`, so a shard can add rules for its custom equipment without a client build. The file is binary; the converter reads and writes a JSON form of it. Format details of the binary: [pack-format.md](pack-format.md), "layerrules.bin".

The client uses the file only with the setting `ecs_layer_rules` on (default off while it is tested). Without a `layerrules.bin` in the pack it uses the built-in rules, which are the same as the converter's default file.

## Workflow

```
Uo.Converter --export-layer-rules rules.json                                  # today's rules, as a starting point
(edit rules.json)
Uo.Converter --uo <client dir> --out <pack dir> --only layers --layer-rules rules.json
```

- `--export-layer-rules` writes only that file and needs no client or pack.
- `--layer-rules` is read and checked before the converter clears or writes anything: a mistake stops the run with a message naming the entry, and the pack stays as it was. Without `--layer-rules` the converter writes the built-in rules.
- Verify (after a conversion, or `--verify-only --only layers --layer-rules rules.json`) compares the pack's `layerrules.bin` with the JSON (or with the built-in rules when no JSON is given) and prints `EXACT` or `MISMATCH`. A pack built from a custom JSON shows `MISMATCH` against the built-in rules: pass the JSON to verify it.
- The default exports and converts back byte for byte (3,055 bytes).

## The file

```jsonc
{
  "layerRules": 1,                      // format version
  "bodies": [ { "body": "0x0190", "group": "HumanMale" }, ... ],
  "tables": [ ["Invalid", "Cloak", "Shirt", ... 25 layers], ... ],
  "rules": [
    { "set": "world", "kind": "select", "table": 0, "when": [ { "anim": "Arms", "in": ["0x0210", "0x03B3"] } ] },
    { "set": "world", "kind": "reorder", "op": "moveTo", "layer": "Pants", "other": "Shirt", "when": [ { "present": "Shirt" }, { "anim": "Pants", "in": ["0x0398"] } ] },
    { "set": "world", "kind": "cover", "layer": "Hair", "covered": true, "when": [ { "present": "Helmet" }, { "graphic": "Helmet", "in": ["0xA42B-0xA42C"] } ] },
    { "set": "world", "kind": "dirmove", "layer": "Cloak", "directions": [0], "op": "top", "when": [] },
    { "set": "world", "kind": "remap", "from": "0x01D5", "to": "0x0156", "when": [ { "flag": ["gargoyleDraw"] } ] }
  ]
}
```

All four top-level keys are required. Comments (`//`) and trailing commas are allowed. An unknown key, a key given twice, an unknown name or a value out of range stops the import with a message that says which entry and why.

**Values.** Graphics, AnimIDs and bodies are JSON numbers or strings: decimal (`"400"`) or hex (`"0x0190"`). A range is a string `"lo-hi"` (inclusive, `"0x0399-0x039D"`). Layers are names (below) or numbers. Exports use names and 4-digit hex.

**Layers** (0..24): `Invalid`, `OneHanded`, `TwoHanded`, `Shoes`, `Pants`, `Shirt`, `Helmet`, `Gloves`, `Ring`, `Talisman`, `Necklace`, `Hair`, `Waist`, `Torso`, `Bracelet`, `Face`, `Beard`, `Tunic`, `Earrings`, `Arms`, `Cloak`, `Backpack`, `Robe`, `Skirt`, `Legs`.

**`bodies`**: body graphic to body group. Groups: `Other` (any unlisted body), `HumanMale`, `HumanFemale`, `ElfMale`, `ElfFemale`, `GargoyleMale`, `GargoyleFemale`. Rules can test the group (the default only uses it for the gargoyle exception of some hoods).

**`tables`**: base draw orders, back to front, 25 layers each, every layer exactly once. Up to 255 tables.

**`rules`**: every rule has a `set` (`"paperdoll"` or `"world"`: the two are separate lists), a `kind`, and optional conditions `when` (all must hold; none = always). The kinds:

| `kind` | Fields | Effect |
|---|---|---|
| `select` | `table` (index) | The first matching select picks the base table. Each set must end with a select without conditions (the default). |
| `reorder` | `op` (`moveTo`, `moveAfter`, `swapIfAfter`), `layer`, `other` | Every matching rule, in file order: `moveTo` puts `layer` at `other`'s index, `moveAfter` right after `other`, `swapIfAfter` swaps them when `other` comes first. |
| `cover` | `layer` (the target; up to 31), `covered` (`true`/`false`) | The first matching cover rule for the target decides whether it is hidden; no match = drawn. A `false` rule is an exception listed before a broader `true` one. |
| `dirmove` | `layer`, `directions` (facings 0..7, `"other"` = 8 and up), `op` (`top`, `bottom`, `before`), `anchor` (the layer for `before`) | World only, after the order: moves the layer for the listed facings (on top, at the back, or just below `anchor`; on top when the anchor isn't worn). The default moves the cloak. |
| `remap` | `from`, `to` (AnimIDs) | The first matching remap changes a drawn equipment AnimID (world: MobileView and the GPU slots; paperdoll: the gump, before the equip conversion). Its conditions may test only the wearer (`flag`, `group`, `body`), not other layers. |

**Conditions** (one test each, `"not": true` negates it):

| Condition | True when |
|---|---|
| `{ "present": "Shirt" }` | an item is worn on the layer |
| `{ "graphic": "Robe", "in": [...] }` | the item graphic on the layer is in one of the values or ranges (an empty layer reads 0) |
| `{ "anim": "Pants", "in": [...] }` | the same for the item's AnimID (tiledata). Order rules (`select`, `reorder`, `dirmove`) see AnimIDs only: use `anim` or `present` there |
| `{ "flag": ["female", "gargoyleDraw", "cv7000"] }` | any listed flag is set: `female` (the mobile's female flag), `gargoyleDraw` (drawn with the gargoyle equipment rules), `cv7000` (client 7.0.0.0 or later) |
| `{ "group": ["GargoyleMale-GargoyleFemale"] }` | the wearer's body group is in the ranges |
| `{ "body": ["0x02B6-0x02B7"] }` | the wearer's body graphic is in the ranges |

A layer-reading condition (`present`, `graphic`, `anim`) must name a layer below 25. The converter applies the same checks as the client's loader (table shape, layer bounds, kinds, a default select per set, at most 65,535 conditions and ranges), so a file it writes always loads.

## Examples

Hide the pants under thigh boots (graphics 0x2307 and 0x2308) in the world and the paperdoll: add, before each set's other `Pants` cover rules,

```json
{ "set": "world", "kind": "cover", "layer": "Pants", "covered": true, "when": [ { "graphic": "Shoes", "in": ["0x2307-0x2308"] } ] },
{ "set": "paperdoll", "kind": "cover", "layer": "Pants", "covered": true, "when": [ { "graphic": "Shoes", "in": ["0x2307-0x2308"] } ] }
```

Draw a custom gargoyle robe (AnimID 0x0600) with another AnimID (0x0601) for gargoyles:

```json
{ "set": "world", "kind": "remap", "from": "0x0600", "to": "0x0601", "when": [ { "flag": ["gargoyleDraw"] } ] }
```
