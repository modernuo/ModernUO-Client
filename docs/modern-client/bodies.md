# Creature bodies: choosing the body set at import (`bodies.json`)

For shard admins. UO has older and newer art for some creatures (the Titan, the summoned daemon, the giant spider and so on), and many creatures that only exist in later eras. The original client picks between them at login from the server's expansion flags. Our client doesn't: **the shard picks its body set once, when the pack is built**, and the pack holds exactly that set. The client never remaps bodies at run time, and the body bits of the server's 0xB9 packet are ignored. Background and numbers: `cuo-poc-state/surveys/bodies.md`.

## Where it lives

A JSON file next to the shard's import settings, passed to the converter:

```
Uo.Converter --uo <client dir> --out <pack dir> --body-choices bodies.json
```

No file means the default (`aos`). The file is read and checked before the converter clears or writes anything, so a typo leaves the previous pack as it was. The pack's `manifest.json` records the choices under `bodies` (resolver policy, preset, flags, sequences, overrides, extras, and the file's SHA-256), so you can always see what a pack was built with, and `--verify-only` checks the pack against them. Changing the choices means rebuilding the animations (`--only anims`). Every other run (an `--only` run without anims, `--no-anims`, `--verify-only`) first checks that the pack's recorded choices are ones this converter understands, and stops before writing anything (output, manifest or debug files) if not; rebuilding the anims replaces the record.

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

Every key is optional. Comments (`//`) and trailing commas are allowed. An unknown key, value or body id, or a key given twice (also inside one extra), stops the import with a message that says which entry and why.

## Two kinds of converted body

- **Remodel:** a body with usable art of its own in `anim.mul` (walk and stand frames with visible pixels in every stored direction, read in the layout the body has in anim.mul) *and* a newer version through `Bodyconv.def`. "old" draws its own anim.mul art; "new" draws the converted art. On 7.0.103 there are 5: **9** (daemon), **10** (summoned daemon), **26**, **28** (giant spider) and **76** (Titan). The converter prints the list with each body's old and new source.
  - Usable art in a body's own slot doesn't prove it is an older version of that creature. Ambiguous cases are decided in one table (`BodyPlan.Classification`). Today it holds **95** (turkey), **not a remodel** (owner, 2026-10-31): its anim.mul slot holds an unrelated grey creature. 95 is an addition and always draws its converted art.
- **Addition:** every other converted body (Dread Spider 11, Giant Black Widow 157, the post-T2A creatures). It has no "old" art; the only alternative is a hued stand-in from `Body.def` (e.g. 11 drawn as spider 28 with hue 1401).
  - Not remodels, although the earlier importer counted them: 318 (Demon Knight), 319 (Mound of Maggots) and the mount ids 791, 793, 794, 796–799. Their older look is a `Body.def` stand-in (318 → 9, 319 → 51, 791 → 220 hue 11, …), not own art. 253, 689 and 704 have no older look at all. Read with today's `mobtypes.txt` layout, their anim.mul index points into other bodies' slots; in their own layout there is no walk or stand art.

UOP bodies (`mobtypes.txt` flag 0x10000) are never remodels: they always draw their UOP frames.

## Eras and presets

Each conversion has an era, from its `Bodyconv.def` column:

| Column | File | Era |
|---|---|---|
| (none) | anim.mul | T2A/UOR |
| 1 | anim2.mul | LBR |
| 2 | anim3.mul | AOS |
| 3 | anim4.mul | SE |
| 4 | anim5.mul | ML |
| 5+ | anim6.mul… | later |

| `preset` | Shows up to | Remodels | Additions (default `standins`) |
|---|---|---|---|
| `t2a`, `uor` | T2A/UOR | all old | every addition draws its Body.def stand-in; those without one keep their art |
| `lbr` | LBR | all old | LBR (column 1) additions keep their art; later ones draw their stand-in |
| `aos` (default) | everything | all new | all keep their art |

- **Remodels count as AOS** for the presets, whatever their column (owner, 2026-10-31: most admins who want LBR don't want the remodelled daemon, spider and Titan). So `lbr` shows every remodel old. One constant (`BodyPlan.RemodelEra`) holds this.
- **Additions and stand-ins (historical behaviour).** Before AOS, each expansion was bought separately. When the server's flags said an account lacked an era (say T2A only), the client drew creatures added in later eras as hued older models from `Body.def`. From AOS on there was one install, so nothing falls back. The `standins` era rule (the default) reproduces this for `t2a`, `uor` and `lbr`; with `aos` nothing falls back. An addition with no `Body.def` line keeps its art, so nothing turns invisible.
- `additions`:
  - `standins` (default): the era rule above. An addition with several columns uses the newest one the preset's era shows.
  - `newest`: additions always draw their newest art (the last column), whatever the preset.
- `forceOldAnimations: true` turns **every** remodel old on top of the preset. Additions are not affected. This is the owner's reading of the original client's "old animations" switch and ModernUO's `expansion.forceOldAnimations`, still under investigation; it is one rule (`BodyPlan.ForceOldReverts`).
- `remodels` overrides single bodies: `"old"` or `"new"` (new = the newest converted art). It wins over the preset and `forceOldAnimations`. Only remodel ids are accepted; an addition's error names its stand-in.
- **Daemon 9, body 26, giant spider 28.** The 7.x `Bodyconv.def` has all `-1` lines for them, but the original client still draws their anim2 art (9 → anim2 7, 28 → anim2 5, 26 → anim2 24) unless old animations are forced. The importer treats them as remodels with those targets (a small built-in table, `BodyPlan.RevertedLbrRemodels`), so `aos` draws the anim2 art and `t2a`, `uor`, `lbr` or `forceOldAnimations` the anim.mul art. The owner places them at the end of LBR (`BodyPlan.RevertedEra`); as remodels they follow the remodel rule above.
- **Stand-ins follow their target.** A `Body.def` or `Corpse.def` alias (e.g. 43 → 9 hue 1152) points at a body id, so it draws whatever that body resolved to. Single hop; no chains at run time.
- **Mount heights (riders).** The client draws a rider at the Mounts table's offset for the mount item (set for the default art, with item-specific values such as 18 for the UOP tigers) plus the pack's **mount shift** for the mount body: the mount height of the art the pack draws (after its Body.def stand-in) minus that of the default art. The converter stores the shift per body (`AnimBodyEntry.MountShift`). An `aos` pack without overrides has no shifts, so riders sit exactly as in the table. `t2a` draws skeletal mount 793 as horse 200, so its rider moves +9 and sits like a horse's. An extra's shift is its own `mountHeight`. Packs from before the field read 0.
- **Corpses of stand-ins.** A body drawn as its Body.def stand-in stores no frames, so its corpse needs a target too: its `Corpse.def` line when it has one, else the live stand-in (t2a 191–195: the corpse of 192 is drawn as 228's death frames).
- **Actions (`sequences`).** `"none"` (default): MUL bodies use no `AnimationSequence.uop` entry, as ClassicUO. `"target"`: a converted body's actions follow the converted art's `AnimationSequence.uop` list (daemon 9 drawn with anim2 7 uses 7's list), except where that list points at a group with no frames in the drawn file. This is checked per direction: a replacement is used only in the directions where it decodes to visible frames; the others keep the action's own group. Opt-in until the owner confirms in game which list the OSI client uses. Recorded in the manifest.

## Extras: keep both variants

`extras` copies one variant's frames into a new body id, so a shard can have, say, both Titans: `{ "id": 2000, "from": "anim.mul:76" }` adds the old Titan as body 2000 while 76 stays new.

- `from` is `<file>:<body>`: `anim.mul:76` is anim.mul body 76's own art; `anim2.mul:7` is anim2 body 7 (the art daemon 9 converts to). UOP frames can't be copied. The extra always draws exactly that MUL source, and the importer checks it has walk and stand frames.
- The id must be unused: no `mobtypes.txt` entry, no `Bodyconv.def` line, no `Body.def`/`Corpse.def` line or stand-in use, no art of its own, not UOP, not another extra, and **not an equipment anim id**: no tiledata item's AnimID (e.g. 498 is ring 0x108A's) and no `Equipconv.def` source or target. The importer names what it hit. On 7.0.103, `mobtypes.txt` ends at 1973; ids 2000–2047 are free and keep the body table its usual size.
- Kind and flags: the extra gets the kind (monster/animal/sea/human/equipment) and `mobtypes.txt` flags of the body the variant belongs to: `anim.mul:76` → body 76, read in 76's own anim.mul layout; `anim2.mul:7` → the lowest non-UOP body converting to it (9), else the lowest UOP one. The UOP flag is never inherited.
- Mount height: `"mountHeight": n` (−128..127) sets it. Without it, it is the mount height of that same body's column (`anim2.mul:204` → −9, from 793), or 0 for `anim.mul` sources. Set it when several bodies use the art with different heights.
- **ModernUO:** the server only knows body ids. A new id needs a kind in ModernUO's `Data/bodyTable.cfg`, or `Body.IsMonster`/`IsHuman` treat it as empty. The importer prints the line to add for each extra, e.g. `2000	Monster`. ModernUO will later read the same `bodies.json`; until then, add the line by hand.

## Packs from before bodies.json

A manifest without a `bodies` record (packs up to classic-1.2) was built with the legacy rules, resolver policy 0: every `Bodyconv.def` column on, the last column wins, no 9/26/28 conversion, no sequences. `--verify-only` rebuilds such a pack with those rules. Packs built with bodies.json record policy 1.
