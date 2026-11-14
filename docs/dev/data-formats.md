# Data formats and the import pipeline

This document describes how the client's data gets from a Ultima Online install to a pack, and what the converter (`poc/Uo.Converter`) reads, writes and checks today. It covers UOP and pre-UOP (MUL) installs, the content types and how each is converted, ids and validation, and the converter's command line. The binary layout of the pack is in [pack-format.md](pack-format.md). Creature body choices are in [bodies.md](bodies.md). The equipment layering rules are in [layer-rules.md](layer-rules.md).

## 1. Pipeline

```
UO install (UOP or MUL, plus .def / .txt files)
        |  Uo.Converter  (read, convert, verify)
        v
pack folder  (manifest.json + binary files)
        |
        v
client  (opens the pack read-only)
```

- One program, `poc/Uo.Converter`, reads the install, writes the pack and then verifies it by reading the pack back and comparing it with the install. There is no separate source tree and no separate build step.
- The pack layout and the readers and writers live in `poc/Uo.Pack`. The converter decodes the install through the frozen ClassicUO loaders in `tools/ClassicUO.Assets.Mul`, so the pack holds what the client would have resolved.
- The converter never writes to the install. The client does not read the pack's source install at run time for the sections the pack covers (see pack-format.md for what is still read from the install).
- Hard-coded per-tile draw rules (the `NoDraw` family) are applied once during conversion and stored as flags in `tiledata.bin` (`poc/Uo.Converter/TileRules.cs`). The equipment layering rules are written to `layerrules.bin` (layer-rules.md).
- The pack holds no data that can be derived from another pack file. For example the hue lookup table is built by the client from `hues_raw.bin`. The lower mip levels of art and texmap pages are stored although derived, because rebuilding them at load is expensive.

## 2. Supported installs

### 2.1 UOP and MUL

`IsUOPInstallation` is true when the client version is 7.0.0 or later and `MainMisc.uop` exists (`tools/ClassicUO.Assets.Mul/UOFileManager.cs`). On a UOP install the converter prefers the `*LegacyMUL.uop` archives (art, gumps, maps, sounds) and `MultiCollection.uop`, and reads `AnimationFrame*.uop` and `AnimationSequence.uop` for UOP animation bodies. Otherwise it reads the `.mul` and `.idx` pairs.

### 2.2 Client version

The oldest supported client is 4.0.11c. Older versions are refused. The version selects record layouts, so it must be right. The converter resolves it in this order (`poc/Uo.Converter/LegacyClientVersion.cs`):

1. `--client-version <x.y.z.w>` (or `x.y.za`, for example `4.0.11c`).
2. `--cuo-settings <settings.json>`: the `clientversion` string of a ClassicUO settings file.
3. `client.exe` in the install folder: the `VS_VERSION_INFO` resource, else the `sprintf("%d.%d.%d%s")` call that pre-resource clients use to build their version string.

If none gives a version, the converter stops and names both options. Letters count from `a` = 1 and are read only for versions before 5.0.7, so `4.0.11c` is 4.0.11.3.

Tile data has two record layouts: below 7.0.9.0, 32-bit flags with 26-byte land and 37-byte static records; from 7.0.9.0, 64-bit flags with 30-byte and 41-byte records. The converter checks that the size of `tiledata.mul` fits the layout the version selects (`ClientLoad.CheckTileDataLayout`). A size that fits both layouts is refused unless the version is given with `--client-version`; then the converter warns that the layout is unconfirmed.

### 2.3 Legacy (pre-UOP) layouts

| Content | Files read | Notes |
|---|---|---|
| Art, texmaps | `art.mul`/`artidx.mul`, `texmaps.mul`/`texidx.mul` | Same layout as UOP-era MUL. |
| Animations | `anim.idx/mul`, `anim2` to `anim5` (up to `anim10` for extras), `Bodyconv.def`, `Body.def`, `Corpse.def`, `mobtypes.txt` | No UOP animation files. |
| Gumps | `gumpart.mul`/`gumpidx.mul`, `gump.def` | |
| Hues, radar colours, lights | `hues.mul`, `radarcol.mul`, `light.mul`/`lightidx.mul` | |
| Tile data | `tiledata.mul`, pre-7.0.9 records | 0x4000 statics; padded to 0x10000 in the pack. |
| Multis | `multi.idx`/`multi.mul` | 12-byte components below 7.0.9, 16-byte from 7.0.9. |
| Maps | `map<N>.mul`, `staidx<N>.mul`, `statics<N>.mul`, map patch files | See 3.9. |
| Cliloc, speech, fonts | `Cliloc.*`, `speech.mul`, `fonts.mul`, `unifont*.mul` | |
| Sounds, music | `sound.mul`/`soundidx.mul`, `Sound.def`, `Music/Config.txt` before 4.0.11c | See 3.11. |
| Tile art appearances | none | `tileart.bin` is written empty. |
| Equipment conversions | `Equipconv.def` | |

`verdata.mul` is not applied. ClassicUO does not apply it either. If the file is present and larger than 4 bytes, the converter prints a warning. Merge it into the base files before converting.

## 3. Content types

The table lists what each stage reads and the pack file it writes. Layouts of the pack files are in pack-format.md. Every section is verified after conversion by reading the pack back and comparing it with the client loaders (see 6).

| Stage (`--only` name) | Reads | Writes |
|---|---|---|
| `art` | art archive, `art.def`, tile data (for table size) | `art/page_NNNN.bin`, `art/sprites.bin` |
| `tex` | `texmaps.mul`/`texidx.mul`, `TexTerr.def` | `tex/page_NNNN.bin`, `tex/texmaps.bin` |
| `anims` | animation files, def files, `mobtypes.txt`, `animdata.mul`, `bodies.json` | `anim/` (pages, `anim.bin`, `palettes.bin`, `animdata.bin`) |
| `tiledata` | `tiledata.mul` | `tiledata.bin` |
| `hues` | `hues.mul`, `radarcol.mul` | `hues_raw.bin`, `radarcol.bin` |
| `data` | multis, `light.mul`, `tileart.uop`, `Equipconv.def` | `multis.bin`, `lights.bin`, `tileart.bin`, `equipconv.bin` |
| `layers` | nothing from the install | `layerrules.bin` |
| `gumps` | gump archive, `gump.def`, loose `Gumps/*.gump` | `gumps/gumps.bin` |
| `fonts` | `fonts.mul`, `unifont*.mul` | `text/fonts.bin`, `text/unifont.bin` |
| `cliloc` | `Cliloc.*`, `Clilocs.txt` | `text/cliloc.bin` |
| `speech` | `speech.mul` | `text/speech.bin` |
| `sounds` | sound archive, `Sound.def`, loose `Sounds/<id>.wav` | `audio/sounds.bin` |
| `music` | `Music/Digital/Config.txt` and the MP3 files | `audio/music.bin` |
| `maps` | map, statics and patch files | `map/<facet>/facet.json`, `map/<facet>/r_<rx>_<ry>.bin` |

A full run also writes `manifest.json` (see 5).

### 3.1 Pixel conventions

Art, texmaps and gump pages are stored as `B5G5R5A1` by default (`--format`). Transparency is explicit in the pack: the alpha bit decides whether a texel is visible, and the converter reproduces ClassicUO's decode bit for bit. The stored value 0 is transparent in static art, gumps and UOP animations. In land art it is visible black inside the diamond. In texmaps it is visible black, because texmaps have no transparency. In MUL animations a written pixel whose palette colour is 0 is visible black. Animation pages are Indexed8 by default: index 0 is the only transparent entry, and every other palette entry has the alpha bit set (`--anim-format`). The exact rules are in pack-format.md, "Pixel formats".

### 3.2 Art (land and static)

- Reads `artLegacyMUL.uop` or `art.mul`/`artidx.mul`, with `art.def` fallbacks applied as ClassicUO applies them (`ClientLoad.ApplyArtDef`), and loose `Art/Statics/<id>.art` and `Art/Land/<id>.land` overrides.
- The sprite table is indexed by id: land ids 0 to 0x3FFF, then static ids at 0x4000 + id.
- Sprites are trimmed and packed into pages (2048 by 2048 by default, `--page-size`) with a full mip chain. Ids that share an index entry, and ids with identical pixels, trim and size, become aliases of the first id.
- An entry that exists but is fully transparent is stored as absent with its original size.

### 3.3 Texmaps

- Reads `texmaps.mul`/`texidx.mul` and applies `TexTerr.def`. Textures are 64 by 64 or 128 by 128, without transparency.
- Packed like art into `tex/` pages with `tex/texmaps.bin`.

### 3.4 Animations

- Reads MUL animation files (`anim.mul` and `anim2` to `anim5`, with `Bodyconv.def` columns up to `anim10`), UOP animation frames, `Body.def`, `Corpse.def`, `mobtypes.txt`, `Anim1.def`/`Anim2.def`, `AnimationSequence.uop` and `Equipconv.def`.
- The pack stores the result as the client sees it: one body table indexed by the client-visible body id, with the conversion choices already applied. The shard admin picks the body set once, at conversion, with `--body-choices` (bodies.md).
- `Body.def` and `Corpse.def` are stored as alias fields on the body entry (the stand-in body and hue, and the corpse target), not as copies of frames.
- Identical frames are shared. Palettes are deduplicated into `anim/palettes.bin`. `animdata.mul` (animated statics) is written to `anim/animdata.bin`.
- Pages are 1024 by 1024 Indexed8 by default (`--anim-page-size`, `--anim-format`). With Indexed8, mips 1 and up are written to a `B5G5R5A1` sidecar (`mips_NNNN.bin`) only when `--indexed-mips b16` is given. By default no mips are written for animation pages.
- `--bodies <ids>` limits frame conversion to the listed bodies (decimal or `0x` hex, comma separated). Body table entries are still written for all bodies. This is a debugging aid.

### 3.5 Gumps

- Reads `gumpartLegacyMUL.uop` or `gumpart.mul`/`gumpidx.mul`, `gump.def` fallbacks and loose `Gumps/*.gump` files. `--gump-def <file>` reads the `gump.def` fallbacks from another file.
- Writes one record per distinct image to `gumps/gumps.bin`. Ids map to records, and a `gump.def` hue rides on the id's slot.
- Records are Brotli-compressed raw texels by default. An image whose run-length payload is at most 65,536 bytes is stored run-length and uncompressed (`--gump-small <n>`, 0 turns this off). `--raw-gumps` stores every record run-length and uncompressed.

### 3.6 Hues and radar colours

- `hues.mul` is written whole to `hues_raw.bin`: groups of 8 entries, each with 32 colours, a start, an end and a 20-byte name. Hue 0 means no hue.
- `radarcol.mul` is written to `radarcol.bin`.
- A stale `hues.bin` in the output folder is deleted.

### 3.7 Tile data

- `tiledata.bin` always holds 0x4000 land and 0x10000 static entries (`PackConst.LandTileCount`, `StaticTileCount`). A shorter source (a pre-7.0.9 install has 0x4000 statics) is padded with empty entries. A longer source is refused.
- Each record keeps flags (64-bit), texture id (land), weight, layer, count, animation id, hue, light index, height and name.
- The converter adds the pack flags `NoDraw`, `NoDrawUnlessGargoyle`, `NoDrawAsMulti` and `NameNoDraw` (`TileRules.cs`). They encode: land 0 to 2 never drawn; the static ids 0x0001, 0x21BC, 0xA1FE to 0xA201, 0x63D3 and 0x2198 to 0x21A4 never drawn; 0x9E4C, 0x9E64, 0x9E65 and 0x9E7D not drawn when they have the Background or Surface flag; names that start with `nodraw`; the `NoDiagonal` flag (not drawn, or not drawn unless the player is a gargoyle when the tile is animated); and the easel 0x0F65, which is drawn only for client versions before 6.0.14.4. The version in this rule is the version the converter resolved, not the client's run-time setting. The flags are plain data in the pack, so a shard can change them there.
- A stale `tiledata_ext.bin` in the output folder is deleted.

### 3.8 Multis

- Reads `MultiCollection.uop` on UOP installs, else `multi.idx`/`multi.mul`. Visibility comes from the MUL flags (visible when the flags are not 0) or from the UOP flags (visible when the flags are 0 or 0x100).
- Writes `multis.bin`: one entry per id and the components (graphic, x, y, z, visible flag) in stored order.

### 3.9 Maps and statics

- Reads `map<N>LegacyMUL.uop` (UOP install) or `map<N>.mul`, plus `staidx<N>.mul` and `statics<N>.mul`. The x files (`map<N>x`, `staidx<N>x`, `statics<N>x`) are never read. A shard that wants them renames them to the regular names before conversion.
- A facet is converted when its map file exists. `--facets 0,1` limits the facets and `--no-maps` skips maps.
- Below client 6.0.0.0, Trammel (facet 1) is read from `map0`, `staidx0` and `statics0`, even when `map1` exists, as ModernUO does. Its patch files are still `mapdif1`/`stadif1`. From 6.0.0.0 Trammel needs its own `map1`.
- Felucca and Trammel are 7168 or 6144 tiles wide, chosen from the size of the `map0.mul` file for MUL maps (`ClientLoad.ApplyLegacyMapRules`), not from the client version.
- Static index entries are read signed. An entry with a negative offset or a length of 0 or less is an empty block. An entry whose range lies outside the statics file is also read as empty. Offset 0 is valid. At most 1,024 statics are read per block. Statics with graphic 0 or 0xFFFF, or x or y above 7, are dropped.
- Map patches are merged into the maps during conversion, as ModernUO's `TileMatrixPatch` serves them. The land family (`mapdifl<N>.mul` and `mapdif<N>.mul`) is applied below 6.0.0.0. The statics family (`stadifl<N>.mul`, `stadifi<N>.mul` and `stadif<N>.mul`) is applied below 7.0.9.0. Each family is applied when all of its own files exist, independently of the other. Entries are applied in order, so later entries win. `--no-map-diffs` skips both families. The pack never holds a patch and the client never applies one.
- Output: per facet, `map/<facet>/facet.json` (index, width, height, block counts, region size) and region files `r_<rx>_<ry>.bin`. A region is 64 by 64 blocks (`PackConst.RegionBlocks`). Each block holds 64 land cells (tile id, z) and its statics list (graphic, x, y, z, hue). Regions are Brotli-compressed by default (`--raw-regions` turns that off).
- `--regions <n>` converts only the first `n` regions of each facet. It is a debugging aid.

### 3.10 Lights

- Reads `light.mul` and `lightidx.mul`. The texels are written as read to `lights.bin`, with each light's width and height from the index.

### 3.11 Sounds and music

- Sounds: `soundLegacyMUL.uop` on a UOP install, else `sound.mul` and `soundidx.mul`. `Sound.def` aliases are applied. Loose `Sounds/<id>.wav` files replace archive sounds. A loose file the client would refuse is skipped with a warning and the archive sound is used. Each distinct sound is one record in `audio/sounds.bin`: raw 22,050 Hz mono 16-bit PCM followed by the name. Aliases share a record.
- Music: `Music/Digital/Config.txt` (`Music/Config.txt` for clients before 4.0.11c) gives id, file name and loop flag. Without a config file the client's built-in table (ids 0 to 66) is used. Each distinct MP3 is one record in `audio/music.bin`: the original bytes followed by the file name. The loop flag is stored on the id's slot. An MP3 that fails validation is left out.
- `--no-sounds` and `--no-music` skip these stages.
- `--audio-report <pack>` loads the install and prints an audio report. It writes nothing and does not clear `--out`.

### 3.12 Text

- Fonts: `fonts.mul` (ASCII fonts, 224 glyphs each) to `text/fonts.bin`; `unifont*.mul` (1-bit glyphs) to `text/unifont.bin`, stored as 256-character pages that identical fonts share. A glyph whose data runs past the end of its file is stored as no glyph.
- Cliloc: every `Cliloc.<lang>` file in the install folder whose name has a 1 to 4 letter or digit extension and whose content is a cliloc table (BWT-compressed, or a raw table that adds up to the file size). `Cliloc.enu` is required. Two files for one language are an error. The shard's `Clilocs.txt`, if present in the install folder, is stored as an extra record in `text/cliloc.bin`; `--cliloc-overrides <file>` folds that file instead (it wins over the folder's, and must exist) for a shard that keeps it outside the install. `--no-cliloc-overrides` leaves both out, `--cliloc-skip ext,ext` skips extensions that are not languages, and `--cliloc-brotli` compresses the language records (they are stored raw by default).
- Speech: `speech.mul` to `text/speech.bin`.
- `--raw-text` stores every text record uncompressed. `--text-small <n>` sets the size below which a record is stored uncompressed (default 16,384 bytes).
- `--no-fonts`, `--no-cliloc` and `--no-speech` skip these stages.

### 3.13 Tile art, equipment conversions and layering rules

- `tileart.bin`: from `tileart.uop`, the subtype-0 appearance map of every item that has more than one appearance subtype. Written empty when the file is absent.
- `equipconv.bin`: from `Equipconv.def`. Later rows replace earlier rows for the same body and graphic.
- `layerrules.bin`: the client's built-in layering rules, written by the `layers` stage. It reads nothing from the install. See layer-rules.md.

### 3.14 Skills, skill groups, world map images, string dictionary, loose files

Stages `skills`, `multimap`, `stringdict` and `loose` (pack-format.md, "Skills, skill groups, multimap, string dictionary" and "Loose text files"). Apart from `skills`, each writes nothing, and removes a stale copy, when the install lacks the source; legacy (pre-UOP) installs have no `string_dictionary.uop` and usually no facet images.

- `skills.bin`: `skills.mul` + `Skills.idx` as the frozen `SkillsLoader` reads them (entries with a positive length, ASCII names with trailing NULs trimmed, the action flag). Required: the stage fails without them, and the client refuses a pack without `skills.bin`.
- `skillgrp.bin`: `skillgrp.mul`'s group names (ASCII, or UTF-16 after a −1 count marker) and its trailing i32 group numbers, read as the client read them (frozen `SkillGroupsMul`). A file the client could not read gets no table.
- `multimap.bin`: `Multimap.rle` and every `facet0*.mul`, byte for byte, in the client's order.
- `text/stringdict.bin`: `string_dictionary.uop`'s strings as the frozen `StringDictionaryLoader` decodes them.
- `loose/`: `Prof.txt`, `citytext.enu`, `stitchin.def`, `Anim1.def`, `Anim2.def` and the house customization tables (`walls`, `floors`, `doors`, `misc`, `stairs`, `teleprts`, `roof`, `suppinfo` `.txt`), copied verbatim under lower-case names.

### 3.15 Not converted

animinfo.mul and the client's built-in tables. A loose `Clilocs.txt` and loose `Sounds/<id>.wav` files in the loose-file folder are still read at load (see pack-format.md, "Not in the pack").

## 4. Ids, additions and validation

- Ids in the pack are the client-visible ids: land, static graphic, gump, texmap, hue, body (after `Bodyconv.def`), multi, sound, light and cliloc. A pack table's entry `i` is id `i`.
- Absent content is absent: an id with no art, gump or sound has no entry. There are no placeholders.
- Body ids can be added at conversion with `extras` in `bodies.json` (bodies.md). Other content is added by supplying the files in the install folder before converting; the converter has no other addition mechanism.
- Loose override files in the install folder win over the archives: `Art/Statics/<id>.art`, `Art/Land/<id>.land`, `Gumps/*.gump`, `Sounds/<id>.wav`, `Clilocs.txt`.
- Conversion checks, which stop the run with a message and exit code 1:
  - the output folder is a published pack folder (see 5);
  - the output folder is not empty and has no `manifest.json`;
  - the client version cannot be determined, is invalid, or is below 4.0.11c;
  - the `tiledata.mul` size does not fit the layout of the client version, or the source has more entries than the pack holds;
  - `bodies.json` is invalid (bodies.md).
- Verification (section 6) runs after every conversion and returns exit code 2 if any check fails.

## 5. Output folder and manifest

- `--out <pack-out>` is required. There is no default.
- A full run (no `--only`) first removes the files the previous output's manifest lists, then writes the new pack. It refuses a non-empty folder that has no `manifest.json`, so it never deletes files it did not write. There are no recursive deletes.
- Files are written to a temporary name in the same folder and moved over the target, so a hard link in the output folder is replaced, not written through.
- Folders under a published packs root are immutable: the converter refuses to write to them (and to a folder that contains one). On Windows a developer-machine folder is built in as a default root (`PublishedPacks.DefaultRoot`). Add roots with `--packs-root <dir>` (repeatable) or the `UO_PACKS_ROOTS` environment variable (`;` separated). A folder whose parent holds a `CURRENT` file naming it is also protected. `--verify-only` only reads, so it may check a published pack.
- `manifest.json` lists every file with its size and SHA-256, plus the install path (`--uo`, made absolute), the client version text, the creation time, the facets, and the body choices record (`bodies`, see bodies.md).
- Debug PNGs of art, texmaps and animations are written to the folder given by `--debug`, by default `<temp>/uo-converter-debug/<out folder name>`.

## 6. Verification

After conversion, and alone with `--verify-only`, the converter reads the pack back with the `Uo.Pack` readers and checks it against itself and against the install through the client loaders (`poc/Uo.Converter/Verify.cs`). Checks include: art and texture pixels against the loaders; animation bodies, frames and aliases; every gump against `GumpsLoader`; every font glyph; every cliloc string in every language and the overrides record; speech entries; every sound and music id; tile data rules against an independent copy of the old client rules; `layerrules.bin` against the built-in rules; and each map facet. Map verification works out the expected facets and sizes from the source files, compares them with the manifest and each `facet.json`, and compares every block of a MUL facet with an independent read (`MulMapOracle.cs`). With `--only`, only the listed stages are verified. `--no-maps`, `--no-sounds` and `--no-music` remove those checks from a full run.

The exit code is 0 on success, 1 for a usage or input error, and 2 when verification fails.

## 7. Converter command line

```
Uo.Converter --uo <uo-install> --out <pack-out> [options]
```

`--uo` defaults to a folder that is hard-coded in `Program.cs`. Always pass it. `--out` is required.

### 7.1 Input and version

| Option | Meaning |
|---|---|
| `--uo <dir>` | The UO install folder. A relative path is made absolute first. |
| `--client-version <v>` | The client version, for example `7.0.103.0` or `4.0.11c`. Overrides detection. |
| `--cuo-settings <file>` | A ClassicUO `settings.json`; its `clientversion` is used. |
| `--no-map-diffs` | Do not merge `mapdif*`/`stadif*` patch files into the maps. |
| `--gump-def <file>` | Read `gump.def` fallbacks from this file. |

### 7.2 Choosing sections

| Option | Meaning |
|---|---|
| `--only a,b,c` | Convert only these stages into an existing pack and keep every other file. Names: `art`, `tex`, `hues`, `tiledata`, `data`, `layers`, `gumps`, `fonts`, `cliloc`, `speech`, `sounds`, `music`, `skills`, `multimap`, `stringdict`, `loose`, `maps`, `anims`. An unknown name is accepted and does nothing. |
| `--anims-only` | Same as `--only anims`. |
| `--no-maps`, `--no-anims`, `--no-gumps`, `--no-fonts`, `--no-cliloc`, `--no-speech`, `--no-sounds`, `--no-music` | Skip that stage in a full run. Ignored when `--only` is given. |
| `--verify-only` | Convert nothing; verify the pack in `--out`. |
| `--facets 0,1` | Convert only these facets. |
| `--body-choices <bodies.json>` | The shard's body choices (bodies.md). Used only when anims are converted; otherwise it is ignored with a warning. |
| `--bodies <ids>` | Limit animation frame conversion to these bodies (debugging). |

With `--only`, the converter does not clear the output folder. If `--format` is not given, it takes the page format and compression from the first existing art page, so the new sections match the pack being extended. A run that does not rebuild the anims keeps the previous manifest's `bodies` record. If maps are not part of an `--only` run, the previous manifest's facets are kept.

The `layers` stage is part of a full run but is not listed in the converter's built-in usage text.

### 7.3 Output and encoding

| Option | Default | Meaning |
|---|---|---|
| `--out <dir>` | (required) | Output folder. |
| `--packs-root <dir>` | | Add a protected published-packs root. Repeatable, may appear anywhere. |
| `--debug <dir>` | `<temp>/uo-converter-debug/<out name>` | Folder for debug PNGs. |
| `--format b5g5r5a1\|rgba8\|bc7` | `b5g5r5a1` | Art, texmap and gump page pixel format. |
| `--bc7-quality fast\|balanced\|bestquality` | `fast` | Encoder quality when the format is `bc7`. |
| `--bc7-refine-db <n>` | `28` | With `fast`, re-encode blocks below this PSNR at `balanced` (0 is off). |
| `--page-size <n>` | `2048` | Art and texmap page side. A power of two from 256 to 8192. |
| `--anim-page-size <n>` | `1024` | Animation page side. Same limits. |
| `--anim-format indexed8\|page` | `indexed8` | `page` uses `--format` for animations instead. |
| `--indexed-mips none\|b16` | `none` | With Indexed8, write a `B5G5R5A1` mip sidecar or none. |
| `--compress-pages=chunked` / `--compress-pages` / `--raw-pages` | chunked | Chunked Brotli pages, whole-file Brotli pages, or raw pages. |
| `--chunk-bytes <n>` | 1 MiB | Chunk size for chunked pages. |
| `--compress-regions` / `--raw-regions` | compressed | Map region compression. |
| `--raw-gumps`, `--gump-small <n>` | | Gump record encoding (3.5). |
| `--raw-text`, `--text-small <n>`, `--cliloc-brotli`, `--cliloc-skip <exts>`, `--no-cliloc-overrides`, `--cliloc-overrides <file>` | | Text record encoding and cliloc discovery (3.12). |
| `--threads <n>` | half the logical cores | Worker thread cap. |
| `--pages a-b` | all | Encode, write and verify only atlas pages `a` to `b` (packing is still global). |
| `--regions <n>` | all | Convert only the first `n` map regions of each facet. |

### 7.4 Reports and experiments

`--gump-report` and `--audio-report <pack>` load the install, print a report and exit without creating, clearing or writing anything. The options `--anim-report`, `--compare-bodies`, `--compare-indexed`, `--filter-ref`, `--bench-chunked` and `--bench-bc7` are measurement tools for converter development and are not part of normal use.

### 7.5 Examples

Full conversion of a UOP install with the default body set:

```
Uo.Converter --uo <uo-install> --out <pack-out>
```

Legacy install where the version cannot be read from `client.exe`:

```
Uo.Converter --uo <uo-install> --out <pack-out> --client-version 4.0.11c
```

Rebuild only the animations with the shard's body choices:

```
Uo.Converter --uo <uo-install> --out <pack-out> --only anims --body-choices bodies.json
```

Rebuild only sounds and music into an existing pack:

```
Uo.Converter --uo <uo-install> --out <pack-out> --only sounds,music
```

Verify an existing pack without converting:

```
Uo.Converter --uo <uo-install> --out <pack-out> --verify-only
```

## 8. Tests

Tests are in `tests/Uo.Converter.Tests/`. `LegacyImportTests.cs` covers client version detection, both tile data layouts, 12- and 16-byte multi components, map patches for each family alone, signed static index entries, Trammel below 6.0, the x files being ignored, and map verification. `BodyChoicesTests.cs` covers `bodies.json` parsing, presets, remodels, extras, mount seats, corpse targets and manifest records. No Ultima Online data is committed; the tests use synthetic files, and tests that need real data read the install path from the `CUO_CLASSIC_UO` or `CUO_LEGACY_UO` environment variables.

## 9. Planned, not implemented

- A source tree (images and tables as editable files) and a separate import step that writes it.
- An incremental build that turns a source tree into a pack and keeps packing stable between builds.
- Layered source trees (a base layer plus shard overrides).
- A shared source-tree reader library for the server and for editing tools.
- Pack sections for skills, professions, world map images and the `Data/Client/*.txt` tables.
- Moving more hard-coded client rules (mounts, chairs, equipment rules, field and foliage lists) into pack data.
