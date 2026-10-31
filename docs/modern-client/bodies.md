# Creature bodies: choosing the body set at import (`bodies.json`)

For shard admins. UO has older and newer art for some creatures (the Titan, the summoned daemon, the mounts and so on). The original client picks between them at login from the server's expansion flags. Our client doesn't: **the shard picks its body set once, when the pack is built**, and the pack holds exactly that set. The client never remaps bodies at run time, and the body bits of the server's 0xB9 packet are ignored. Background and numbers: `cuo-poc-state/surveys/bodies.md`.

## Where it lives

A JSON file next to the shard's import settings, passed to the converter:

```
Uo.Converter --uo <client dir> --out <pack dir> --body-choices bodies.json
```

No file means the default (`aos`). The pack's `manifest.json` records the choices under `bodies` (preset, flags, overrides, extras, and the file's SHA-256), so you can always see what a pack was built with, and `--verify-only` checks the pack against them. Changing the choices means rebuilding the animations (`--only anims`).

```json
{
  "preset": "aos",
  "forceOldAnimations": false,
  "remodels": { "76": "old", "9": "new" },
  "additions": "newest",
  "extras": [ { "id": 2000, "from": "anim.mul:76" } ]
}
```

Every key is optional. Comments (`//`) and trailing commas are allowed. An unknown key, value or body id stops the import with a message that says which entry and why.

## Two kinds of converted body

- **Remodel:** a body that has its own art in `anim.mul` *and* a newer version through `Bodyconv.def`. "old" draws its own anim.mul art; "new" draws the converted art. On 7.0.103 there are 17: 9 (daemon), 10 (summoned daemon), 26, 28 (giant spider), 76 (Titan), 318 (Demon Knight), 319 (Mound of Maggots), the mounts 791, 793, 794, 796–799, and a few more the converter lists.
- **Addition:** a converted body with no anim.mul art of its own (Dread Spider 11, Giant Black Widow 157, the post-T2A creatures). It has no "old" art; the only alternative is a hued stand-in from `Body.def` (e.g. 11 drawn as spider 28 with hue 1401).

UOP bodies (`mobtypes.txt` flag 0x10000) are never remodels: they always draw their UOP frames.

## Presets

| `preset` | Remodels | Who wants it |
|---|---|---|
| `t2a`, `uor` | all old | the classic T2A/UOR look |
| `lbr` | the column-1 (anim2, LBR) remodels new, the others old | an LBR-era shard |
| `aos` (default) | all new | AOS and later; most shards |

- `forceOldAnimations: true` turns every column-1 remodel old on top of the preset (the "old animations" switch of the original client; ModernUO's `expansion.forceOldAnimations` means the same).
- `remodels` overrides single bodies: `"old"` or `"new"` (new = the newest converted art). Only remodel ids are accepted.
- `additions`:
  - `newest` (default): additions always keep their art, so nothing turns invisible or into a stand-in.
  - `standins`: an addition whose conversion the preset turns off draws its `Body.def` stand-in (the strict T2A-client look); additions without one keep their art. With `aos` this changes nothing.
- **Daemon 9, body 26, giant spider 28.** The 7.x `Bodyconv.def` has all `-1` lines for them, but the original client still draws their anim2 art (9 → anim2 7, 28 → anim2 5, 26 → anim2 24) unless old animations are forced. The importer treats them as column-1 remodels with those targets (a small built-in table, `BodyPlan.RevertedLbrRemodels`), so `aos` draws the anim2 art and `t2a`, `uor` or `forceOldAnimations` the anim.mul art.
- **Stand-ins follow their target.** A `Body.def` or `Corpse.def` alias (e.g. 43 → 9 hue 1152) points at a body id, so it draws whatever that body resolved to. Single hop; no chains at run time.
- **Mount heights** follow the chosen column automatically.
- **Actions.** A converted body's actions follow the converted art's `AnimationSequence.uop` list (daemon 9 drawn as anim2 body 7 uses 7's list).

## Extras: keep both variants

`extras` copies one variant's frames into a new body id, so a shard can have, say, both Titans: `{ "id": 2000, "from": "anim.mul:76" }` adds the old Titan as body 2000 while 76 stays new.

- `from` is `<file>:<body>`: `anim.mul:76` is anim.mul body 76's own art; `anim2.mul:7` is anim2 body 7 (the art daemon 9 converts to). UOP frames can't be copied.
- The id must be unused: no `mobtypes.txt` entry, no `Bodyconv.def` line, no `Body.def`/`Corpse.def` line or stand-in use, no art of its own, not UOP, not another extra. The importer says which of these it hit. On 7.0.103, `mobtypes.txt` ends at 1973; ids 2000–2047 are free and keep the body table its usual size.
- The extra gets the kind (monster/animal/sea/human/equipment), `mobtypes.txt` flags and mount height of the body the variant belongs to (anim.mul:76 → body 76; anim2.mul:7 → the lowest body converting to it, 9).
- **ModernUO:** the server only knows body ids. A new id needs a kind in ModernUO's `Data/bodyTable.cfg`, or `Body.IsMonster`/`IsHuman` treat it as empty. The importer prints the line to add for each extra, e.g. `2000	Monster`. ModernUO will later read the same `bodies.json`; until then, add the line by hand.
