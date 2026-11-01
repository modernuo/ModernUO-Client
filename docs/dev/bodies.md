# Creature bodies: choosing the body set at import

This document is for shard admins. It explains how to choose which creature art a pack contains, with a `bodies.json` file passed to the converter. UO has older and newer art for some creatures (the Titan, the summoned daemon, the giant spider and others) and many creatures that exist only in later eras. The original client chose between them at login from the server's expansion flags. This client does not: the shard chooses its body set once, when the pack is converted, and the pack holds exactly that set. The converter is described in [data-formats.md](data-formats.md).

## Where the file lives

`bodies.json` is a plain JSON file. It can be anywhere. Pass it to the converter with `--body-choices`:

```
Uo.Converter --uo <uo-install> --out <pack-out> --body-choices bodies.json
```

- With no file, the defaults apply: preset `aos`, `additions` `standins`, `sequences` `none`, no overrides, no extras.
- The converter reads and checks the file before it clears or writes anything. A typo leaves the previous pack as it was.
- `--body-choices` takes effect only when the animations are converted in that run (a full run, or `--only anims`). Otherwise the converter ignores it and prints a warning.
- Changing the choices means rebuilding the animations: `--only anims --body-choices bodies.json`.
- The pack's `manifest.json` records the choices under `bodies`: resolver policy, preset, `forceOldAnimations`, additions mode, sequences mode, remodel overrides, extras, the path of the file as given, and the file's SHA-256. `--verify-only` checks the pack against this record.
- A run that does not rebuild the animations (an `--only` run without `anims`, `--no-anims`, `--verify-only`) first checks that the pack's recorded choices are ones this converter understands. If not, it stops before writing anything. Rebuilding the animations replaces the record.

## File format

Every key is optional.

```json
{
  "preset": "aos",
  "forceOldAnimations": false,
  "remodels": { "76": "old", "9": "new" },
  "additions": "standins",
  "sequences": "none",
  "extras": [ { "id": 2000, "from": "anim.mul:76" } ]
}
```

| Key | Values | Default |
|---|---|---|
| `preset` | `"t2a"`, `"uor"`, `"lbr"`, `"aos"` | `"aos"` |
| `forceOldAnimations` | `true`, `false` | `false` |
| `remodels` | object: body id to `"old"` or `"new"` | none |
| `additions` | `"standins"`, `"newest"` | `"standins"` |
| `sequences` | `"none"`, `"target"` | `"none"` |
| `extras` | array of `{ "id", "from", "mountHeight" }` | none |

- Comments (`//`) and trailing commas are allowed.
- An unknown key, an unknown value, a bad body id, or a key given twice (also inside one extra) stops the conversion with a message that names the entry and the reason.
- Body ids are decimal or `0x` hex, from 0 to 65534. In `remodels` they are the object keys (strings). In `extras`, `id` may be a number or a string.

## Two kinds of converted body

`Bodyconv.def` says that body N is really drawn from a later animation file (`anim2.mul` and up). The converter sorts those bodies into two kinds.

- **Remodel:** a body that has usable art of its own in `anim.mul` (walk and stand frames with visible pixels in every stored direction, read in the layout the body has in `anim.mul`) and a newer version through `Bodyconv.def`. `"old"` draws its own `anim.mul` art. `"new"` draws the converted art. The converter prints the list of remodels with each body's old and new source at the start of the animation stage. UOP bodies (the `mobtypes.txt` flag 0x10000) are never remodels: they always draw their UOP frames.
- **Addition:** every other converted body (the creatures added after the first expansion, for example the Dread Spider and the Giant Black Widow). An addition has no old art. Its only older look is a stand-in from `Body.def`, usually another body with a hue.

Usable art in a body's own `anim.mul` slot does not prove that it is an older version of the same creature. Ambiguous cases are decided in one table in the converter (`BodyPlan.Classification` in `poc/Uo.Converter/BodyChoices.cs`). It holds body 95 (the turkey), which is an addition: its `anim.mul` slot holds an unrelated creature. The converter also lists bodies that look like remodels because of the `mobtypes.txt` layout but have no walk and stand art in their own layout. They are additions too.

Bodies 9 (daemon), 26 and 28 (giant spider) have `Bodyconv.def` lines that are all `-1` in 7.x clients, but the original client still draws their `anim2.mul` art unless old animations are forced. The converter treats them as remodels with the targets 9 to `anim2.mul:7`, 26 to `anim2.mul:24` and 28 to `anim2.mul:5` (`BodyPlan.RevertedLbrRemodels`). They are added only when the install has `anim2.mul` and a `Bodyconv.def` line for the body.

## Eras and presets

Each conversion has an era, taken from its `Bodyconv.def` column:

| Column | File | Era |
|---|---|---|
| none | `anim.mul` | T2A/UOR |
| 1 | `anim2.mul` | LBR |
| 2 | `anim3.mul` | AOS |
| 3 | `anim4.mul` | SE |
| 4 | `anim5.mul` | ML |
| 5 and up | `anim6.mul` and up | later |

Columns above 9 are ignored, and so are columns whose animation file is not in the install.

| `preset` | Shows up to | Remodels | Additions (with `additions` = `standins`) |
|---|---|---|---|
| `t2a`, `uor` | T2A/UOR (identical) | all old | every addition draws its `Body.def` stand-in; an addition without one keeps its art |
| `lbr` | LBR | all old | additions from column 1 keep their art; later ones draw their stand-in |
| `aos` (default) | everything | all new | all keep their art |

- Remodels count as AOS for the presets, whatever their column. That is why `lbr` shows every remodel old: most admins who want LBR do not want the remodelled daemon, spider and Titan. The constant is `BodyPlan.RemodelEra`.
- Before AOS each expansion was bought separately. When an account lacked an era, the original client drew creatures from later eras as hued older models from `Body.def`. The `standins` rule reproduces this for `t2a`, `uor` and `lbr`. With `aos` nothing falls back. An addition with no `Body.def` line keeps its art, so nothing becomes invisible.
- An addition with several columns uses the newest column that the preset's era shows.

### `additions`

- `standins` (default): the era rule above.
- `newest`: additions always draw their newest art (the last column), whatever the preset.

### `forceOldAnimations`

`true` turns every remodel old, on top of the preset. Additions are not affected (`BodyPlan.ForceOldReverts`).

### `remodels` (overrides)

`remodels` sets single bodies to `"old"` or `"new"` (`new` is the newest converted art). An override wins over the preset and over `forceOldAnimations`. Only remodel ids are accepted. An id is refused with a message if the body:

- draws UOP frames;
- has no `Bodyconv.def` conversion;
- is classified as an addition;
- has no usable `anim.mul` art of its own. If it has a `Body.def` stand-in, the message names it and points to `additions`.

### `sequences`

- `"none"` (default): MUL bodies use no `AnimationSequence.uop` entry, as ClassicUO.
- `"target"`: a converted body's actions follow the `AnimationSequence.uop` list of the converted art (daemon 9 drawn with `anim2.mul` body 7 follows the list of body 7), except where that list points at a group with no frames in the drawn file. This is checked per direction: a replacement is used only in the directions where it decodes to visible frames, and the other directions keep the action's own group. The converter prints a summary of the replaced groups.

## Related behaviour

These follow from the choices above and need no setting.

- **Stand-ins follow their target.** A `Body.def` or `Corpse.def` alias points at a body id, so it draws whatever that body resolved to. The converter stores the final target, so the client follows one hop.
- **Corpses.** The converter stores the effective corpse target: it follows `Corpse.def` while the body is a plain `anim.mul` body, and, for a body that stores no frames because it is drawn as its stand-in, it continues to that body's stand-in. A `Corpse.def` line that maps a body to itself keeps the body's own frames (with the line's hue; with hue 0 there is no corpse target). A cycle of `Corpse.def` lines and stand-ins stops the conversion with the chain in the message.
- **Rider seat on mounts.** The pack stores a rider seat for each mount body. An `aos` pack without overrides stores a relative shift of 0 everywhere, so riders sit as the client's mounts table says. When a mount body is drawn as a `Body.def` stand-in, its seat is absolute: the offset of the stand-in's own mount items (0 when none is listed). Any other change of drawn art (an old remodel, `forceOldAnimations`) is a relative shift: the difference of the `Bodyconv.def` mount heights. An extra's seat is relative and equals its own mount height.

## Extras: keep both variants

`extras` copies the frames of one variant into a new body id, so a shard can have, for example, both Titans: `{ "id": 2000, "from": "anim.mul:76" }` adds the old Titan as body 2000 while body 76 stays new.

- `from` is `<file>:<body>`, with `<file>` one of `anim.mul` or `anim2.mul` to `anim10.mul`. `anim.mul:76` is the own art of `anim.mul` body 76. `anim2.mul:7` is `anim2.mul` body 7 (the art that daemon 9 converts to). UOP frames cannot be copied. The extra always draws exactly that MUL source. The converter checks that it has walk and stand frames and that the file is in the install.
- The id must be unused. It must have no `mobtypes.txt` entry, no `Bodyconv.def` line, no `Body.def` or `Corpse.def` line, and no use as a stand-in. It must have no art of its own, must not be a UOP body, must not be another extra's id, and must not be an equipment animation id: no tiledata item may use it as its animation id, and it must not appear in `Equipconv.def` as a source or target. The converter names what it found. Choose an id beyond the end of `mobtypes.txt`.
- Kind and flags: the extra gets the kind (monster, animal, sea, human, equipment) and the `mobtypes.txt` flags of the body the variant belongs to. For `anim.mul:76` that is body 76, read in its own `anim.mul` layout. For `anim2.mul:7` it is the lowest non-UOP body that converts to it (9), else the lowest UOP one. The UOP flag is never inherited.
- Mount height: `"mountHeight": n` (a whole number from -128 to 127) sets it. Without it, the height is the `Bodyconv.def` mount height of the same body and column, or 0 for `anim.mul` sources. Set it when several bodies use the art with different heights.

## What a shard admin must do server-side

- The client uses body ids only. Whatever body id the server sends, the client draws the art the pack holds for that id. The server's expansion flags do not change which art is drawn.
- A new body id (an extra) needs a kind in ModernUO's `Data/bodyTable.cfg`. Without it, `Body.IsMonster` and `Body.IsHuman` treat the id as empty. For each extra the converter prints the line to add, for example `2000<TAB>Monster`, where the kind is `Monster`, `Sea`, `Animal`, `Human`, `Equipment` or `Empty`. Add the line by hand.
- Make the server's creature scripts use the ids the pack draws. For example, if the pack draws the old Titan only as extra 2000, a spawn must use body 2000 to show it.

## Packs from before `bodies.json`

A manifest without a `bodies` record belongs to a pack converted before `bodies.json` existed. It was built with the legacy rules, resolver policy 0: every `Bodyconv.def` column on, the last column wins, no conversion of bodies 9, 26 and 28, no sequences. `--verify-only` checks such a pack with those rules. Packs converted with the current converter record policy 1, even without a `bodies.json` file. A record with any other policy number is refused until the animations are rebuilt.

## Planned, not implemented

- ModernUO reading the same `bodies.json`, so the `bodyTable.cfg` lines no longer need to be added by hand.
