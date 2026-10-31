# Data formats: import, source format, build, pack

The entry point for every question about the client's data: where it comes from, what admins and tools edit, and how it becomes the pack the client loads. Written 2026-10-31 from the owner's pipeline decisions (README, "Data pipeline"), the converter (`poc/Uo.Converter`), ClassicUO's loaders (`src/ClassicUO.Assets`) and the MUL readers of ModernUO, CentrED# and UOFiddler.

**Status:** the pipeline and the shipped pack exist (the converter does import and build in one step today). The source format and the separate build step are **specified here, not implemented**. Decisions still open are listed at the end with a recommendation each.

| Question | Authoritative doc |
|---|---|
| The pipeline, the source format, how a build maps source to pack, what each tool reads and writes | this file |
| The shipped pack's binary layout (headers, pages, sections, compression, manifest) | [pack-format.md](pack-format.md) |
| How the pack format got there (measurements, rejected options) | [pack-format-v0.md](pack-format-v0.md) (history) |

## 1. The pipeline

```
MUL/UOP + .def/.txt ──import (once)──▶ source tree ──build (incremental)──▶ shipped pack ──▶ client
  (legacy, read-only)                  (definitive;                         (optimised; never
                                        admins and tools edit it)            hand-edited)
```

- **Import** reads a UO client folder once and writes a source tree. It is the only code that reads MUL/UOP. It never writes MUL/UOP, and nothing in the pipeline goes back to them. Re-importing is possible (to pick up a newer EA client), but it produces a new tree that the admin merges; it never edits an existing tree in place (section 6.4).
- **The source tree is the definitive data.** Admins, the server and the tools read and write it. One file or folder per thing an admin edits.
- **Build** turns a source tree into a shipped pack. It is incremental and its packing is stable: a one-item or one-body change rebuilds in seconds and changes few pack files, so patches stay small (section 7).
- **The shipped pack** is what the client loads. It is optimised (atlas pages, Indexed8 animation pages, chunked Brotli, record stores) and never edited by hand. Its layout is [pack-format.md](pack-format.md).
- **Today:** `Uo.Converter` does import and build in one pass (MUL/UOP → pack), and the client still reads a few MUL/UOP files directly (pack-format.md, "Not in the pack"). The split is the next step: the converter's decoders become the importer, its page/record writers become the build.

### 1.1 Rules both ends follow

- **One source of truth.** The pack holds no data derivable from another pack file (owner, 2026-10-31): `hues.bin` is being removed (the client builds the hue LUT from `hues_raw.bin`, branch `poc/hues-single-source`), and `tiledata.bin` is being merged into the full tiledata file. The same rule applies to the source tree: a value lives in exactly one source file.
- **Accepted exception:** the lower mip levels of art and texmap pages are stored in the pack although they are derived, because rebuilding them at load is expensive. They are never stored in the source tree.
- **Rules are data.** ClassicUO's hard-coded per-tile and per-graphic rules (NoDraw lists, the `nodraw` name prefix, land 0–2, the easel version rule, `NoDiagonal`, and the others in section 5.6) move into the source tree as data. The build writes them into the pack; the client keeps no copy. Admins change behaviour by editing data, not code.
- **Lossless.** Source images are UO's own pixel layouts, uncompressed; the pack's mip 0 is bit-exact to the source (pack-format.md, "Pixel formats"). Nothing lossy anywhere in the chain (BC7 breaks hueing; README "Texture format").
- **No patch formats.** `verdata.mul`, `mapdif*`/`stadif*` are not imported (README, "No patch formats"). Shard owners merge patches into the base files before importing. (The upstream commit 7335535a1 that loads verdata-only animations is a Legacy-path fix and does not change this.)
- **Several clients at once.** The client opens the pack read-only with shared access; the build writes a new pack version aside and swaps it atomically (README, "Several instances at once"; the `uo-packs/CURRENT` scheme in the agent rules is the PoC form of this).

## 2. Source tree layout

One folder per content type, one file (or folder) per editable thing, ids in the file name. Ids are written in **lowercase hex, zero-padded to the width of their id space** (`0x0f3c` → `0f3c`), so a directory listing sorts by id and a file name never changes meaning.

```
<source>/
  source.json                        the tree's manifest (section 3.1)
  art/
    land/<id4>.tga                   land tile 0000..3fff, 44×44 diamond
    static/<id4>.tga                 static (item) art 0000..ffff (more if the id space allows)
    static/<id4>.json                optional per-graphic metadata (section 3.3) — usually absent
  tex/<id4>.tga                      texmap (64² or 128²)
  gumps/<id4>.tga                    gump image (+ <id4>.json when it has metadata)
  anim/
    <body4>/body.json                body metadata: type, flags, mount height, sources, sounds
    <body4>/<action2>/<dir>/<frame2>.tga   Indexed8 + palette; frame centre in the TGA origin fields
  hues/hues.json                     every hue: 32 colours, TableStart/End, name
  tiledata/
    land.csv                         land tile data, one row per id
    static.csv                       static tile data, one row per id
  multis/<id4>.csv                   components of one multi (one row per component)
  maps/<facet>/                      facet.json + map and statics data (section 4.9; format open, Q3)
  lights/<id2>.tga                   light textures
  sounds/<id4>.wav (+ sounds.csv)    PCM WAV + names
  music/<name>.<mp3|ogg> + music.csv
  text/
    cliloc/<lang>.tsv                one file per language: id <TAB> flag <TAB> text
    fonts/ascii/<n>/<glyph>.tga + font.json, fonts/unicode/<n>.json   ASCII glyph images; unicode fonts one file each
    speech.tsv                       keyword id <TAB> keyword
  tables/
    radarcol.csv                     radar colour per land/static id
    skills.csv, skillgroups.csv
    professions/                     prof.txt content as JSON (one file per profession)
    tileart/<id4>.json               tileart.uop appearances (only the ones present)
    equipconv.csv                    Equipconv.def
    animdata.csv                     animdata.mul (animated statics)
    mounts.csv, chairs.csv, containers.csv, …   client rules lifted out of code (5.6)
  worldmap/<facet>.tga               world-map images (Multimap.rle, facet*.mul)
```

- **Absent file = no entry.** A missing `art/static/1234.tga` means static 0x1234 has no art (ClassicUO's "invalid index"). There are no empty placeholder files.
- **Aliases are data, not copies.** Many UO ids share one image (the converter dedupes by index entry and by pixels, ArtConvert.cs:56-64). In the source tree an alias is a one-line JSON stub: `art/static/1235.json` with `{"aliasOf": "1234"}` and no `.tga`. The build resolves aliases; the importer writes them where the MUL index entries were shared (not where pixels merely happen to match, so that editing one never edits the other by surprise).
- **Large tables stay one file per table** (tiledata, hues, cliloc per language, radarcol): each is one thing an admin edits, and per-row files would mean 0x14000 files for tiledata. Their text formats are line-oriented so diffs and merges stay per row.

## 3. Metadata

### 3.1 `source.json`

```json
{
  "format": "uo-source",
  "version": 1,
  "name": "My Shard",
  "idSpace": { "land": 16384, "static": 65536, "gump": 65536, "body": 4096, "multi": 8704, "hue": 3000 },
  "client": { "era": "7.0.103", "features": ["hs-tiledata", "uop", "post-hs-multis"] },
  "imported": { "from": "C:/UO", "clientVersion": "7.0.103.0", "at": "2026-10-31T12:00:00Z", "importer": "uo-import 1.0" }
}
```

- `version` is the source-format version (section 8). Tools refuse a higher major version.
- `idSpace` fixes the id limits the tree uses. The importer fills it from the client (tiledata size decides 0x4000/0x8000/0x10000 statics, as ModernUO's `TileData.cs:314-328` does). Raising a limit is an explicit edit, validated against the consumers (ModernUO caps items at 0x10000 today).
- `client.features` records format-relevant facts the build needs (e.g. the post-HS 16-byte multi component format; the easel rule's client version, 5.6).
- `imported` is provenance only. Nothing reads it to decide behaviour.

### 3.2 Text format

Proposed (open question Q1): **JSON for structured records, CSV/TSV for large uniform tables.** JSON: `source.json`, `body.json`, hues, rules, per-graphic metadata. CSV (RFC 4180, UTF-8, header row, one row per id): tiledata, radarcol, multis, skills, equipconv, sound names. TSV for cliloc and speech (text may contain commas and quotes; a TSV row is `id<TAB>text` with `\t`, `\n`, `\\` escaped).

- Numbers: ids and graphics in hex strings with no prefix in file names and CSV id columns (`0f3c`), `0x`-prefixed in JSON values (`"0x0f3c"`); other numbers decimal.
- Flags: named, not bit masks. Tiledata's `flags` column is a `|`-separated list of names (`Impassable|Surface`), with the names of ModernUO's and ClassicUO's `TileFlag` (bits 33–64 as `HS33`…`HS64` until named). Unknown names fail validation.
- Every file is UTF-8 without BOM, `\n` line endings, sorted by id, so diffs are minimal and tools that rewrite a file produce the same bytes for the same data (determinism matters for section 7).

### 3.3 Per-graphic metadata

Most graphics need none: their tiledata row carries the gameplay data and the image carries the pixels. A `<id>.json` next to an image holds only what doesn't fit either:

- `aliasOf` (2);
- `origin` overrides for art that ClassicUO draws offset (none known today; reserved);
- gumps: `hueable`, the `gump.def` fallback (`{"fallback": "0x1234", "hue": 0}`) when the gump itself is absent.

## 4. Content types

For each type: what the importer reads (and the ClassicUO/converter code whose decode it reuses), the source file, the build output (pack section; layout in pack-format.md), and validation.

### 4.1 Images in general

**Container: uncompressed TGA** (open question Q2), because TGA's two uncompressed image types are exactly UO's layouts and every image editor opens them:

| UO content | TGA type | Pixel layout |
|---|---|---|
| Art (land, static), texmaps, gumps, ASCII font glyphs, world-map images | 2 (true colour), 16 bpp, descriptor alpha bits = 1 | `A1R5G5B5` little-endian: bits 0–4 blue, 5–9 green, 10–14 red, bit 15 alpha. **Identical to UO's 1555 and to the pack's `B5G5R5A1`.** |
| Animation frames | 1 (colour-mapped), 8 bpp index, colour map of 256 × 16-bit entries | index → 1555 palette entry |

- Rows top-to-bottom (descriptor bit 5 set), no RLE (types 9/10 are rejected), no image ID, no developer area. A TGA 2.0 footer is allowed and ignored.
- The TGA `x/y origin` fields carry the animation frame centre (4.4); they are 0 for other content.
- Lights are 8-bit intensity in light.mul; see 4.10.

#### Transparency is explicit

UO's own data has no alpha bit that the client trusts. Transparency is implied, and differently per content type: in static art, gumps, lights and UOP animations a colour value of **0 is transparent**, so a visible pure black can't exist there and artists use near-black (e.g. 0x0421) instead; in land art, texmaps and MUL animations 0 is **opaque black**. The source format drops these conventions:

- **16-bit images: the A bit decides.** `A = 0` is transparent (the colour bits must then be 0: canonical transparent texel `0x0000`). `A = 1` is visible, whatever the colour. **`0x8000` is visible pure black.**
- **Indexed8 frames: index 0 is transparent**, and every other palette entry must have `A = 1`. Visible black is any entry `0x8000` other than index 0. (Reserving index 0 keeps the shader's test one compare, the same as the pack's `palettes.bin` today, pack-format.md "palettes.bin". A second transparent entry would buy nothing.)
- **Importing (legacy-exact).** The importer reproduces ClassicUO's decode bit for bit (the frozen MUL loaders are in `tools/ClassicUO.Assets.Mul/`, "T" below; the converter decodes through them and `PageCodec.EncodeB5G5R5A1` writes A from the decoded alpha, PageCodec.cs:199-201):

  | Content | Stored 0 becomes | Other value `c` | Where |
  |---|---|---|---|
  | Static art | `0x0000` (inside a run as well as in the gaps between runs) | `c \| 0x8000` | T/ArtLoader.cs:331-336 |
  | Land art | `0x8000` inside the diamond (visible black); the corners outside the diamond `0x0000` | `c \| 0x8000` | T/ArtLoader.cs:228-262 |
  | Texmaps | `0x8000` (visible black; texmaps have no transparency) | `c \| 0x8000` | T/TexmapsLoader.cs:88 |
  | Gumps | `0x0000` | `c \| 0x8000` | T/GumpsLoader.cs:437-445; pack `Decode16`, GumpsLoader.cs:389 |
  | Lights | 0 (transparent) | intensity (values > 0x1F map to `~v & 0x1F`) | T/LightsLoader.cs:58-61 |
  | MUL animations | a written pixel whose palette colour is 0 is **visible black** (`alphaCheck = false`); unwritten pixels index 0 | palette entry `c \| 0x8000` | T/AnimationsLoader.cs:1508, :1573; AnimConvert.cs:302, :548 |
  | UOP animations | palette colour 0 → index 0 (transparent) | palette entry `c \| 0x8000` | T/AnimationsLoader.cs:1446; AnimConvert.cs:543 |

  So an imported tree already contains visible pure black where the legacy client draws it (land, texmaps, MUL animations), and the build of an imported tree is pixel-identical to today's pack. The pack already carries those blacks as `0x8000` texels and palette entries.
- **Drawing visible black.** The pack's `B5G5R5A1` pages already carry A per texel, and the shader and picker already decide visibility from A (Indexed8: from index ≠ 0; pack-format.md "Not in the pack", picking). So `0x8000` in the source is `0x8000` on the page and draws black with no client change. The one place that must change is any code path that still tests "colour == 0" instead of A: the gump record decoder (pack-format.md "Gumps": "texel 0 is transparent, otherwise bit 15 is set") and the run-length gump rows must carry A explicitly; the build and the client must not re-derive A from colour.
- **Hueing.** The hue replaces a texel's colour with one of the hue's 32 shades, chosen by the texel's red (gray) level; a partial hue does this only for gray texels (r = g = b: `IsometricWorld.fx:127`, `poc/Uo.Render/Shaders/sprite.hlsl:536-548`, CPU `GetPartialHueColor` for ASCII fonts). Visible black (0,0,0) is gray, so under a full or partial hue it becomes the hue's darkest shade (shade 0), just as ClassicUO's near-black grays (0x0421 → shade 1) do today, and as MUL animations' visible black already does. An artist who wants black to stay black under a partial hue uses a non-gray near-black (e.g. 0x0001), the same trick as today. **Gumps differ:** the gump hue mode skips the hue for texels with red < 0.02 (`IsometricWorld.fx:119-124`), so visible black in a hued gump stays black. The CPU gump path's rule "a hued texel stays visible only if the hue result is not 0" (pack-format.md "Gumps"; T/GumpsLoader.cs:437-440 applies the hue only to non-zero values) becomes "visibility comes from A, never from the hued colour". Recorded as open question Q4.
- **Exporting to PNG** (a tool feature): A → alpha 0/255. **Importing from PNG**: alpha < 128 → `0x0000`, else `A = 1` with the colour quantised by `v >> 3` (the exact inverse of the canonical expansion, pack-format.md "Conventions"). UOFiddler's BMP import treats 0x8000 and 0xFFFF as transparent (`Utils.ConvertBmp`); under this format that rule belongs only to BMP import, never to the source.

### 4.2 Art (land and static)

- **Import:** `art.mul`/`artidx.mul` or `artLegacyMUL.uop`, with `art.def` fallbacks applied by the loader (the converter reads through ClassicUO's loader, ArtConvert.cs:47-108). Land: 2,024 bytes, 1,012 texels laid into the 44×44 diamond in rows of 2, 4 … 44 … 4, 2 (T/ArtLoader.cs:197-264). Static: `u32 flags, i16 w, i16 h`, `h` row offsets, then per row runs of `xSkip, runLength, texels` ended by `(0,0)` (:266-351). Loose overrides `Art/Statics/<id>.art` and `Art/Land/<id>.land` win over the archive (:82-190). In the importer, land and static art are one id space (land 0..0x3FFF, static 0x4000 + id), split into two folders here.
- **Source:** `art/land/<id>.tga` (always 44×44, texels outside the diamond `0x0000`) and `art/static/<id>.tga` (the graphic's own width and height, **untrimmed**: the importer keeps the stored size because the draw position depends on it).
- **Build:** trim, dedupe by content, pack into 2048² B5G5R5A1 pages with a 2-texel gutter and the full mip chain, sprite table `art/sprites.bin` (index = land id, or 0x4000 + static id). Shipped layout "today's pages, under evaluation" (section 7.3).
- **Validation:** land exactly 44×44 with nothing visible outside the diamond; static ≤ the RLE limits ClassicUO and UOFiddler impose (`Art.ValidateStaticSize`, 65,535 ushorts) only if a MUL export is ever wanted (it isn't); width, height ≤ 1024.
- **`art.def`** (fallback id per missing id) (`index {a,b,…}`: an empty entry takes the first non-empty alternative, and blank tiledata is copied from it; T/UOFileManager.cs:151-221) is resolved at import: the fallback becomes an alias stub (2), and copied tiledata becomes a real row. It is not kept as a separate file.

### 4.3 Texmaps

- **Import:** `texmaps.mul`/`texidx.mul`, `TexTerr.def` applied (ArtConvert.cs:151-195). 64×64 (length 0x2000) or 128×128 (0x8000) raw 16-bit, no transparency (T/TexmapsLoader.cs:21-98). `TexTerr.def` (`index {a,…}`) overwrites unconditionally; the importer resolves it into alias stubs.
- **Source:** `tex/<id>.tga`, 64² or 128². Land tiles point at them through tiledata's `texId`.
- **Build:** `tex/texmaps.bin` + pages, same path as art.
- **Validation:** square, 64 or 128.

### 4.4 Animations

UO stores animations by **file** (anim.mul, anim2–5, AnimationFrame1..N.uop) and then remaps bodies through several `.def`/`.txt` files. The source format stores the **result**: one folder per body id as the client sees it, with every remap resolved except the ones that are genuinely runtime choices.

- **Import:** ClassicUO's frame decode of `anim*.idx/mul` and `AnimationFrame*.uop` (MUL frames: 256-entry 16-bit palette, frame header `centreX, centreY, width, height`, then run lines; UOP: per-frame palette and header in the frame blob), `AnimationSequence.uop`, `Body.def`, `Bodyconv.def`, `Corpse.def`, `mobtypes.txt`, `Anim1.def`/`Anim2.def`, `animdata.mul`, and verdata animation patches are not imported (1.1). Offsets: people `(id-400)*175+35000` blocks of 35 groups, monsters `id*110` (22 groups), animals `(id-200)*65+22000` (13), sea monsters the monster offset with 13 groups; MUL run headers are `u32` (`len = h & 0xFFF`, signed 10-bit x and y), ended by `0x7FFF7FFF`; UOP frame ids are 1-based across five directions (T/AnimationsLoader.cs:471-560, 968-1008, 1270-1585).
- **Source:**

  ```
  anim/<body>/body.json
  anim/<body>/<action>/<dir>/<frame>.tga     Indexed8, 256-entry 1555 colour map, origin = frame centre
  ```

  - `<action>`: the animation group number (two digits; the group sets differ by body type: monster 0..21, animal 0..12, people 0..34, plus UOP groups).
  - `<dir>`: 0..4, the five stored directions (UO mirrors the other three at draw time; 5..7 are never stored).
  - One palette per frame, inside the TGA. The importer writes the source palette of the body/action as UO stores it (MUL: one palette per direction block; UOP: per frame), so the per-frame copies are identical within a direction. The build dedupes palettes (the pack's `palettes.bin` holds 37.9k deduplicated palettes).
- **`body.json`:**

  ```json
  {
    "type": "monster | sea_monster | animal | human | equipment",
    "flags": ["UseUopAnimation", "..."],
    "source": { "file": 2, "body": 0x0123 },
    "mountHeight": 0,
    "fallback": { "body": 0x00c8, "hue": 0 },
    "corpse":   { "body": 0x00c9, "hue": 0 },
    "groupReplace": { "21": 18 },
    "sounds": { "idle": 0, "attack": 0 }
  }
  ```

  - `type` and `flags` come from `mobtypes.txt` (type plus flag hex).
  - `source` is provenance: which anim file and file-local body the frames came from (`Bodyconv.def` resolution). Read-only for tools; the build ignores it.
  - `mountHeight` is the converter's resolved value (pack-format.md "anim.bin": `mountHeight`).
  - `fallback` is `Body.def` (`orig {new} hue`): draw that body when this one has no frames. `corpse` is `Corpse.def`. Both are **kept as data**, not baked into copies, because they name another editable body (editing the target must change both). The pack keeps them as chained alias fields today (pack-format.md "anim.bin").
  - `groupReplace` is `Anim1.def`/`Anim2.def` and the AnimationSequence replacement for this body (pack-format.md says AnimationSequence replacement is baked in today; the source keeps it as data so admins can change it).
- **What is baked at import and what stays data:**

| UO file | Meaning | Source format |
|---|---|---|
| `Bodyconv.def` | body N of anim.mul is really in animK.mul as body M | **resolved**: frames live under the client-visible body; `source` records where from |
| `Body.def` | body with no frames draws another body (+hue) | `fallback` (data) |
| `Corpse.def` | corpse body remap | `corpse` (data) |
| `mobtypes.txt` | body type and flags | `type`, `flags` |
| `AnimationSequence.uop` | per-body group replacements, mount info (UOP) | `groupReplace`, `mountHeight` |
| `Anim1.def`, `Anim2.def` | group fallbacks | `groupReplace` |
| `Equipconv.def` | gump/anim for equipment on another body | `tables/equipconv.csv` (4.12) |
| `animdata.mul` | **art** animation frames (not mobiles) | tiledata-adjacent: `tables/animdata.csv` (frame graphics, interval, start delay) |
| `animinfo.mul`, the sitting tables | runtime fallbacks | `tables/` data (5.6) |

- **Build:** Indexed8 1024² pages (mip 0 only), `anim.bin` (body, action, direction, frame tables), `palettes.bin` (deduplicated), `animdata.bin`. Packing order "body → action → direction → frame" (pack-format.md). Shipped layout "today's pages, under evaluation" (7.3).
- **Validation:** frame index 0 transparent and other entries A = 1; directions 0..4 only; frame count per direction equal across directions of an action (a renderer assumption; warn, don't fail); `fallback`/`corpse` targets exist and contain no cycle; group numbers valid for `type`.

### 4.5 Gumps

- **Import:** `gumpartLegacyMUL.uop` or `gumpart.mul`/`gumpidx.mul` (per row a `i32` offset table, rows of `u16 colour, u16 run` pairs; UOP entries may be zlib or zlib+BWT, with `u32 w, h` in front; T/GumpsLoader.cs:50-77, 416-472), `gump.def` fallbacks (`in {a,b,…} hue`, only when `in` is empty), loose `Gumps/*.gump` overrides (GumpConvert; pack-format.md "Gumps").
- **Source:** `gumps/<id>.tga`, 16-bit. `gump.def` entries become `<id>.json` with `fallback` + `hue` when the gump itself is absent.
- **Build:** the gump record store (`gumps/gumps.bin`), small gumps Brotli, large ones as run-length rows.
- **Validation:** width and height ≤ 0xFFFF (the idx limit; a larger limit is a pack-side decision), A/colour rule (4.1).

### 4.6 Hues

- **Import:** `hues.mul`: groups of 8 entries; each entry has 32 colour16 shades, `TableStart`, `TableEnd`, a 20-byte name.
- **Source:** `hues/hues.json`: an array, index = hue id − 1 (hue 0 means "no hue" and is not stored), each `{ "id": 1, "name": "…", "colors": ["0x0000", …32], "start": "0x…", "end": "0x…" }`. One file: admins edit hues as a table, and a hue's id is its position.
- **Build:** `hues_raw.bin` exactly (pack-format.md "hues_raw.bin"). The client builds the RGBA8 LUT at load (canonical bit replication). `hues.bin` is gone (branch `poc/hues-single-source`).
- **Validation:** exactly 32 colours each; ids contiguous from 1; count ≤ `idSpace.hue`.

### 4.7 Tile data

- **Import:** `tiledata.mul` (old: 0x4000 statics, 32-bit flags; 7.0.0: 0x8000; 7.0.9+: 64-bit flags, 0x10000; detection by file size as ModernUO does).
- **Source:** `tiledata/land.csv` (`id, name, flags, texId`) and `tiledata/static.csv` (`id, name, flags, weight, layer, count, animId, hue, lightIndex, height, …` — every field ClassicUO, ModernUO and CentrED# read, so import is lossless; the record is `flags, weight, layer, count, animId, hue, lightIndex, height, name`, T/TileDataLoader.cs:25-79). Rule flags the build used to derive at runtime are columns too (5.6).
- **Build:** one full tiledata file (`tiledata_ext.bin` today, to become the only one); the client derives its compact arrays at load. `NoDraw` flags are written from the data, not from code.
- **Validation:** id ranges, known flag names, `texId` refers to an existing texmap or 0, `animId` refers to an existing body or 0.

### 4.8 Multis

- **Import:** `MultiCollection.uop` (preferred) or `multi.idx`/`multi.mul` (12-byte pre-HS or 16-byte post-HS components, chosen as ModernUO does: client ≥ 7.0.9.0 or the setting), plus `housing.bin` (UOP; read by ModernUO, not by ClassicUO). ClassicUO: a MUL component is visible iff `flags != 0`, a UOP one iff flags is 0 or 0x100; up to 0x2200 multis (T/MultiLoader.cs:61-110). The importer stores the flags as read and the visibility rule as data (a `visible` column), so the build doesn't need to know which file a multi came from.
- **Source:** `multis/<id>.csv`: `graphic, x, y, z, flags` per component, in stored order (order matters for draw ties). Cliloc blocks of MultiCollection entries are kept in `<id>.json` only if non-empty.
- **Build:** `multis.bin`.
- **Validation:** graphic exists in `art/static`; flags known.

### 4.9 Maps and statics

- **Import:** `map<N>.mul`/`map<N>LegacyMUL.uop`, `staidx<N>.mul` + `statics<N>.mul` (and the `x` variants, `--xfiles`), facet sizes from the client era. Patches are not read (1.1).
- **Source:** `maps/<facet>/facet.json` (`id, name, width, height, blockSize`) plus the tile data. The storage of the tile data is open (Q3). The recommendation: **block files of the same 8×8 cells, grouped per region**: `maps/<facet>/<rx>_<ry>.bin`, each a 64×64-block (512×512-tile) region holding, per block, the 64 land cells (`tileId u16, z i8`) and the statics list (`graphic u16, x u8, y u8, z i8, hue u16`), uncompressed, little-endian. It is the MUL block model (what CentrED#, ModernUO and the converter all already speak), cut into files small enough to edit, diff, lock and rebuild independently.
- **Build:** `map/<facet>/facet.json` and Kind 1 region files (pack-format.md "Map"). Region-aligned source files map 1:1 to pack region files, so editing a region rebuilds one pack file.
- **Validation:** land ids exist; statics graphics exist; z in −128..127; block counts match `facet.json`.

### 4.10 Lights

- **Import:** `light.mul`/`lightidx.mul` (8-bit per texel, size from the idx `extra`; 100 lights). ClassicUO maps a value above 0x1F to `~v & 0x1F`, draws 0 as transparent and v as gray `v << 3` (T/LightsLoader.cs:32-71).
- **Source:** `lights/<id>.tga`, TGA type 3 (8-bit gray), holding the **normalised intensity 0..31** (the `~v & 0x1F` mapping applied at import, so the file means what it draws); 0 is transparent. The checker rejects values above 31.
- **Build:** `lights.bin`.

### 4.11 Sounds and music

- **Import:** `sound.mul`/`soundidx.mul` or `soundLegacyMUL.uop` (each entry: 40-byte name, then 16-bit mono PCM 22,050 Hz; `SoundsLoader.cs:44-67`), `Sound.def` remaps (alias an empty entry; -1 clears), loose `Sounds/<id>.wav` overrides; music: the client's `Music/Digital/*.mp3` and `Music/Digital/Config.txt` (id, name, loop flag).
- **Source:** `sounds/<id>.wav` (PCM WAV with the same sample format, so import is lossless and every audio tool opens it) and `sounds/sounds.csv` (`id, name`; `Sound.def` remaps as `aliasOf` rows). Music: the files as-is plus `music/music.csv` (`id, file, loop`).
- **Build:** not in the pack yet ("next; a record store like gumps", pack-format.md). Music stays files.

### 4.12 Text and small tables

| Content | Import from | Source | Build |
|---|---|---|---|
| Cliloc | `Cliloc.<lang>` (every language; the whole file BWT-compressed when byte 3 is 0x8E; entries `i32 id, u8 flag, u16 len, UTF-8`; T/ClilocLoader.cs:170-195) | `text/cliloc/<lang>.tsv` (`id<TAB>flag<TAB>text`) | `text/cliloc.bin` |
| Shard cliloc additions | `Clilocs.txt` | merged into the TSV (an override is just an edited row) | — (the client stops reading `Clilocs.txt` once the build covers it) |
| ASCII fonts | `fonts.mul` (10 fonts, 224 glyphs, 16-bit pixels) | `text/fonts/ascii/<n>/<glyph2>.tga` + `font.json` (header byte) | `text/fonts.bin` |
| Unicode fonts | `unifont*.mul` (1-bit glyphs with offsets) | `text/fonts/unicode/<n>.json` (see below) | `text/unifont.bin` |
| Speech | `speech.mul` (big-endian `u16 id, u16 len`, UTF-8 text) | `text/speech.tsv` | `text/speech.bin` |
| Radar colours | `radarcol.mul` | `tables/radarcol.csv` (`kind, id, colour`) | `radarcol.bin` |
| Skills | `skills.mul`/`skills.idx`, `skillgrp.mul` | `tables/skills.csv`, `tables/skillgroups.csv` | not in the pack yet |
| Professions | `prof.txt` | `tables/professions/<name>.json` | not in the pack yet |
| Tileart appearances | `tileart.uop` | `tables/tileart/<id>.json` | `tileart.bin` |
| Equipconv | `Equipconv.def` | `tables/equipconv.csv` | `equipconv.bin` |
| Art animations | `animdata.mul` | `tables/animdata.csv` | `animdata.bin` |
| World map | `Multimap.rle`, `facet0*.mul` | `worldmap/<facet>.tga` | not in the pack yet |
| Stitchin | `stitchin.def` (`Stitchin.cs:17`) | `tables/stitchin.json` | not in the pack yet |
| Login city texts | `citytext.enu` (`LoginScene.cs:816`) | `text/citytext/<lang>.tsv` | not in the pack yet |
| Client tables | `Data/Client/*.txt` (cave, vegetation, tree, chair, lights, lightshaders, buff, seasons, containers) | `tables/` (5.6) | not in the pack yet |

Unicode font glyphs are 1-bit (`unifont{,1..19}.mul`: offset table by code point, then `offsetX, offsetY, w, h` and 1-bit rows; font 1 falls back to font 0). Store each font as one `text/fonts/unicode/<n>.json` with one entry per code point (`offsetX, offsetY, width, height, rows` as hex strings). One file per font keeps 65,536 tiny glyphs out of the file system.

## 5. Ids, overrides, additions and rules

### 5.1 Ids

- Ids are the client-visible ids: land, static graphic, gump, texmap, hue, body (after `Bodyconv.def`), multi, sound, light, cliloc. They are what the server sends, so they never change on rebuild.
- A pack table's entry `i` is id `i` (pack-format.md "Conventions"); the source tree's file name is the id. There is no second numbering.

### 5.2 Additions

Adding content is adding a file: `art/static/<newid>.tga` plus its `tiledata/static.csv` row; `anim/<newbody>/…` plus `body.json`. Within `idSpace` nothing else is needed. Raising `idSpace` is a deliberate edit (3.1).

### 5.3 Overrides and layers

A shard's tree is one tree: an override is an edit of the file. To keep an EA-imported base separable from shard changes (for re-imports and for sharing), a tree may declare **layers** in `source.json`: `"layers": ["../uo-base-7.0.103", "."]`. Later layers win per file (per row for tables: a layer's `static.csv` holds only the rows it changes; a row with `deleted=1` removes an id). The build sees the merged view. Open question Q5 asks whether layers are wanted in v1; the recommendation is yes, at file and row granularity only, because it makes EA re-imports and shard updates a three-way merge instead of a hand diff.

### 5.4 Deletions

Delete the file (or, in a layer, a `deleted` stub/row). The build drops the id; validation reports dangling references (tiledata `animId`, multis, `fallback`, `aliasOf`).

### 5.5 Validation

`uo-build --check` (or the build's first phase) checks every rule stated per type in section 4, plus:

- file names parse as ids within `idSpace`; no file outside the known layout (warn);
- every reference resolves (aliases, fallbacks, texIds, animIds, multi graphics, map tiles), and alias/fallback chains have no cycles;
- images: TGA type and depth as specified, no RLE, transparency rule (4.1), size limits;
- tables: header row matches the schema version, ids sorted and unique, known flag names;
- the same rules for every layer's merged view.

Errors stop the build; warnings are printed with the file and row. Tools (Fiddler, CentrED#) run the same checker on save, shipped as a library (section 9).

### 5.6 Hard-coded rules that become data

ClassicUO decides many per-tile and per-graphic behaviours in code. Under the owner's rule (1.1) each becomes source data that the build writes into the pack. Known so far:

| Rule today (code) | Source data |
|---|---|
| Land 0, 1, 2 never drawn; statics 0x1, 0x21BC, 0xA1FE–0xA201, 0x63D3, 0x2198–0x21A4 never drawn; 0x9E4C/0x9E64/0x9E65/0x9E7D not drawn with Background or Surface (converter ArtConvert.cs:223-245) | tiledata column `noDraw` (bool) set by the importer |
| Names starting `nodraw` | the importer sets `noDraw` from the name once; afterwards the name no longer matters |
| The easel 0x0F65 drawn only before client 6.0.14.4 | the importer sets `noDraw` by `imported.clientVersion` |
| `NoDiagonal` → NoDraw, or NoDraw unless gargoyle when animated | tiledata column `noDraw` = `always` / `unlessGargoyle` |
| Vegetation, tree/stump ("hatched") and cave lists (`StaticFilters.cs:63-111`; already seeded into editable `Data/Client/{vegetation,tree,cave}.txt`) | tiledata columns `vegetation`, `tree` (`none`/`tree`/`stump`), `cave` |
| Rocks (`IsRock`) and field ranges: fire, paralyze, energy, poison, wall of stone (`StaticFilters.cs:276-329`) | tiledata column `kind` (`rock`, `field:fire`, …) |
| Foliage groups that fade together (`TreeUnion` ranges, `GameSceneDrawingSorting.cs:21-31`) | tiledata column `fadeGroup` |
| Items that emit light without the LightSource flag (0x3E02–0x3E0B, 0x3914–0x3929) and the field hues (`ItemView.cs:29-131`) | tiledata `lightIndex`/flag and a `hue` per field kind in `tables/fields.csv` |
| Light colours and shaders per item (`Data/Client/lights.txt`, `lightshaders.txt`; `LightColors.cs:336-441`) | `tables/lights.csv`, `tables/lightshaders.csv` |
| Mount item → body and rider offset (`Mounts.cs`, duplicated in `poc/Uo.Client/Animation.cs:78-101`), ethereal and kirin specials, the boat-tiller exception (`Item.cs:394-403`, `AnimationRules.cs:62-90`) | `tables/mounts.csv` (`item, body, riderOffsetY`) |
| Body classes: human, gargoyle, dead and ghost remaps (`AnimationRules.cs:18-32`, `ItemView.cs:189-198`), GM and ghost bodies in the pathfinder (`Pathfinder.cs:71-249`) | `body.json` `flags` (`human`, `gargoyle`, `dead:<living body>`, `gm`) |
| Bodyconv mount-height offsets (hard-coded per column and body, T/AnimationsLoader.cs:609-713) | `body.json` `mountHeight` (resolved at import) |
| Gargoyle equipment remap (`AnimationRules.cs:142-158`), equipment hide/show rules per layer (`AnimationRules.cs:247-590`), weapon-hand anim tables (`AnimationGroupRules.cs:88-124`) | `tables/equipment-rules.json` |
| Chairs and sitting (`ChairTable.cs`, `chair.txt`), containers (`containers.txt`), buffs (`buff.txt`), seasons (`seasons.txt`) | `tables/chairs.csv`, `containers.csv`, `buffs.csv`, `seasons.json` |
| Default music table (`SoundsLoader.cs:143-209`, used when `Config.txt` is absent) | `music/music.csv` |
| Custom-house hue 0x2B (`MultiView.cs:52`) | `tables/client.json` |

The importer is the only code that knows these rules; it writes them as data once. After import, an admin flips `noDraw` per tile. The client's editable `Data/Client/*.txt` files (cave, vegetation, tree, chair, lights, lightshaders, buff, seasons, containers) are already data; they move into the source tree and the pack so a shard ships one set, and the client stops seeding them from code. Movement rules (the pathfinder's id lists) also matter to the server and must match it; Q6 covers how the server reads the same data. This list was sampled by grep and is not complete: `Mobile.cs`, `MobileView.cs`, `GameScene.cs` and `World.cs` were not read in full.

## 6. Tools and the server

### 6.1 What each reads and writes today

| | Reads | Writes |
|---|---|---|
| **ModernUO** (server) | `tiledata.mul` (name, flags, height, weight, layer, count, animId, value; flags 64-bit; ≤ 0x10000 items; `TileData.cs:303-383`), `map*.mul`/`map*LegacyMUL.uop`, `staidx*`/`statics*` (`TileMatrix.cs:105-153`), `mapdif`/`stadif` patches (`TileMatrixPatch.cs`), `MultiCollection.uop` or `multi.idx/mul` (`MultiData.cs`), `art.mul`/UOP for item bounds only (`ArtData.cs`), `cliloc.*`, `prof.txt`; located by `Core.FindDataFile` over `dataDirectories` | nothing |
| **CentrED#** server | `map*.mul` + `staidx*.mul` + `statics*.mul` (or `map*LegacyMUL.uop`, uncompressed entries, in place), `tiledata.mul`, `radarcol.mul`, `hues.mul` | the same map/statics files **in place** (`FileShare.None`), backups by copy |
| **CentrED#** editor | ClassicUO's `UOFileManager`: art, hues, tiledata, texmaps, animdata, lights, multis | JSON profiles/brushes of its own; PNG/BMP renders |
| **UOFiddler** | almost every MUL/UOP (Ultima SDK, `Files.cs:67-192`), verdata | MUL for art, gumps, texmaps, lights, hues, tiledata, multis, skills, sounds, animdata, radarcol, speech, cliloc, fonts, anim.mul; UOP via the UopPacker plugin; CSV/PNG/BMP/TIFF import/export |

### 6.2 What each will read and write

| | Reads | Writes |
|---|---|---|
| **Client** | the shipped pack only (no MUL/UOP, no source tree) | nothing (profiles and settings are its own) |
| **Build** (`uo-build`) | the source tree | a pack version (aside, then swapped) |
| **Importer** (`uo-import`) | MUL/UOP/.def | a new source tree (or a layer) |
| **ModernUO** | the source tree's `tiledata/*.csv`, `multis/`, `maps/` (land + statics), `text/cliloc/`, `tables/professions/`, the static art **bounds** (`ArtData.cs` today; computed from the image's visible texels by `Uo.Source`, or a build-written bounds table; Q6) | nothing; map edits come from CentrED# |
| **CentrED#** server | `maps/<facet>/` region files, `tiledata/`, `tables/radarcol.csv`, `hues/` | `maps/<facet>/` region files (one file per edited region; atomic replace) |
| **CentrED#** editor | the shipped pack for drawing (it already uses ClassicUO's loaders, which become pack readers), or the source tree for art not yet built | nothing |
| **UOFiddler** | the source tree | the source tree (images as TGA, tables as CSV/JSON); PNG/BMP import/export stays a tool feature; MUL export is not required |

Patch formats disappear: ModernUO's `TileMatrixPatch` has nothing to read once maps come from the source tree.

### 6.3 How the server consumes data (Q6)

Recommendation: the server reads the **source tree directly** (tiledata CSV, multis, map regions, cliloc), through a small shared reader library (`Uo.Source`), not the shipped pack. Reasons: the server needs gameplay data (flags, heights, z, components), not pages; it runs on Linux without the pack; and server and client then agree because both come from the same tree. Load time: CSV parsing of 0x14000 tiledata rows is milliseconds; map regions are raw binary. If startup matters, the server may cache a binary form keyed by the source files' hashes (as its step cache already keys on tiledata.mul).

### 6.4 Re-import

A new EA client means a new import into a **fresh** tree (or base layer); the admin merges. The importer never overwrites an edited tree. With layers (5.3) the merge is: replace the base layer, keep the shard layer, review conflicts the checker reports (rows the shard changed that the base also changed).

## 7. The build step

### 7.1 Mapping

| Source | Pack (pack-format.md section) |
|---|---|
| `art/land`, `art/static` (+ aliases) | `art/sprites.bin`, `art/page_NNNN.bin` ("Atlas pages", "Sprite tables") |
| `tex/` | `tex/texmaps.bin`, `tex/page_NNNN.bin` |
| `anim/` | `anim/anim.bin`, `anim/palettes.bin`, Indexed8 pages ("Animations") |
| `tables/animdata.csv` | `animdata.bin` |
| `gumps/` | `gumps/gumps.bin` ("Gumps") |
| `hues/hues.json` | `hues_raw.bin` ("Hues") |
| `tiledata/` | the full tiledata file ("Tile data"; `tiledata.bin` merged into it) |
| `tables/radarcol.csv` | `radarcol.bin` |
| `multis/` | `multis.bin` |
| `lights/` | `lights.bin` |
| `tables/tileart/`, `tables/equipconv.csv` | `tileart.bin`, `equipconv.bin` |
| `text/` | `text/fonts.bin`, `unifont.bin`, `cliloc.bin`, `speech.bin` ("Text") |
| `maps/<facet>/` | `map/<facet>/facet.json`, `r_<rx>_<ry>.bin` ("Map") |
| `sounds/`, `music/`, `tables/skills*`, `professions/`, `worldmap/` | not in the pack yet; the client reads them from MUL until each gets a pack section (then from the pack) |
| `source.json` | `manifest.json` provenance |

### 7.2 Incremental and stable

- **Inputs are hashed per file.** The build keeps a build cache (`<pack>.build/`, never shipped): for every source file its content hash, and for every pack output the set of source files and the hash of the inputs that produced it. A build recomputes only outputs whose input set changed.
- **Stable placement.** Art and texmaps today are packed in one pass, so one changed sprite can move others and rewrite many pages. The build keeps the previous placement (sprite → page, rect) in the build cache and **places changed or new sprites into free space or a new page**, leaving every other sprite where it was. Pages with freed space are compacted only on an explicit `--repack` (a release-time choice that produces a large patch once). Animations: each body's frames go on pages owned by that body (today's "body → action → direction → frame" order already groups them), so a one-body change rewrites that body's pages and its `anim.bin` entries.
- **Record stores** (gumps, text, sounds later) append changed records and rewrite the index; a `--repack` reclaims space.
- **Tables** (tiledata, hues, multis, radarcol) are small and rewritten whole when any row changes.
- **Map regions** are 1:1 with source region files.
- **Deterministic output:** the same source bytes give the same pack bytes (sorted inputs, no timestamps in files except `manifest.json`'s creation time, parallel work written in id order). Then the manifest's per-file sha256 identifies exactly what a patch must carry.
- **Target:** a one-item or one-body change rebuilds in seconds and touches the changed pages plus their table files.

### 7.3 Shipped art and animation layout: today's pages, under evaluation

The pack ships art and texmaps as pre-packed 2048² B5G5R5A1 pages and animations as pre-packed 1024² Indexed8 pages. A separate agent (branch `poc/records-eval`) is measuring whether animations and art should instead ship as **per-graphic records** that the client packs into GPU pages at runtime. If that wins, the build writes records (like gumps) instead of pages, incremental builds and patches get simpler (7.2's placement cache disappears), and mip levels move to runtime or per record. The source format is unaffected either way. Until that result, the shipped layout is today's pages.

## 8. Versioning

- **Source format:** `source.json` `version` (an integer, bumped on any incompatible layout or schema change). Table files carry their schema in the header row, so a new optional column is a compatible change; tools ignore columns they don't know and preserve them on rewrite. A tool refuses a newer major version than it knows.
- **Pack:** `FileHeader.version` per file and the manifest's pack version (pack-format.md "FileHeader and versioning"). The build writes the current pack format; the client refuses packs it doesn't understand.
- **Pack versions on disk:** `<packs>/<name>-<ver>/` with a `CURRENT` pointer, immutable once published (the PoC's `uo-packs` scheme).

## 9. Implementation notes

- **Libraries:** `Uo.Source` (read/write/validate the source tree; used by the build, the importer, UOFiddler, CentrED# and ModernUO), `Uo.Pack` (pack structs, readers, writers; exists), `Uo.Import` (the converter's MUL/UOP decoders), `Uo.Build` (the converter's page and record writers).
- **Order of work:** (1) `Uo.Source` with the image and table codecs plus the checker; (2) the importer writing a tree from today's converter decoders, verified by building that tree and comparing to today's pack byte for byte (except intended changes); (3) the incremental build; (4) the client drops its remaining MUL reads as sections arrive; (5) the tools.
- **Verification:** import → build must equal today's converter output (same pixels, same tables); a round trip `tree → build → (no decompile)` is not needed because the tree is definitive.

## 10. Owner decisions (2026-10-31)

The owner answered the ten open questions of the first draft. The rest of this spec follows these answers; where a section still says "recommendation", the decision below wins.

**Q1. Metadata text format: decided.** JSON for structured records, CSV/TSV for large uniform tables (3.2).

**Q2. Image container: decided, TGA.** TGA type 2 (16-bit A1R5G5B5), type 1 (8-bit indexed with a 16-bit colour map) and, for lights, type 3 (8-bit gray). These are UO's layouts byte for byte, and common editors open them. **Tooling handles other TGA variants instead of only rejecting them:** for each one, convert it when that is exact (e.g. RLE to uncompressed, or 24/32-bit whose colours are exactly representable in 5-5-5 with a 1-bit alpha), convert best-effort with a report of every lossy pixel when it isn't (opt-in), or fail with a message that says which variant was found, which file and how to fix it. The build and the checker share this code.

**Q3. Maps for CentrED#: open, pending a CentrED# survey.** The owner knows little about CentrED# internals. It has a server, with a ModernUO-compatible module, for concurrent collaborative editing. A survey of `C:\Repositories\centredsharp` will report how it reads, writes and caches map and statics data. The owner is open to a third format next to the source and the shipped pack: **a CentrED working format**, if that avoids thrashing many small files when maps and statics are edited live. The source map layout (4.9) is provisional until the survey is in.

**Q4. Transparency and visible black: decided as recommended.** Explicit A everywhere; Indexed8 index 0 is the only transparent entry; the gump CPU rule becomes A-based; a parity run over all hued stock gumps confirms that no stock content relies on a hued-to-zero hole.

**Q5. Layers (base + shard overrides) in v1: decided, yes** (file- and row-level). Separate known issue, not a format question: ClassicUO doesn't draw paperdoll "bleed" correctly. With a robe on, the black halo around armour arm pieces disappears; the halo probably exists to cover another layering oddity. It needs its own investigation (known-issues.md).

**Q6. Server consumption: decided, the source tree** (through `Uo.Source`). The server never needs GPU-shaped data.

**Q7. Versioning: decided as recommended** (8).

**Q8. Where the source tree lives: decided as recommended.** Design for Git + LFS, line-oriented sorted text, and a build that doesn't need Git.

**Q9. Animation frames: decided, one TGA per frame.** Revisit only if file counts hurt on real trees.

**Q10. Sounds and music: open, pending an audio survey.** The owner asked about OGG. Note that Ogg **Vorbis** is lossy; the lossless options are WAV (PCM) and FLAC (about half of WAV's size, also often inside an Ogg container). Also, UO's music already ships as MP3 (lossy), so re-encoding it would lose quality and should be avoided. The survey compares decode cost, latency, memory, FNA/FAudio/SDL support and disk size for WAV, FLAC and Ogg Vorbis/Opus, for both sound effects (short, many, latency-sensitive) and music (long, streamed), and recommends a source format and a shipped format for each.
