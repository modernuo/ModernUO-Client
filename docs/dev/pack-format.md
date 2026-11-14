# Pack format 1.x

This document specifies the asset pack that the ModernUO client loads instead of the UO client's MUL and UOP files. The converter (`poc/Uo.Converter`) writes packs, and `poc/Uo.Pack` holds the structs, readers and writers that both sides share; the client's loaders are in `src/ClassicUO.Assets/`. Where this text and the code disagree, the code wins. Related pages: [data-formats.md](data-formats.md) (source formats), [bodies.md](bodies.md) (body import choices), [layer-rules.md](layer-rules.md) (`layerrules.bin`), [runbook.md](runbook.md) (converting and running).

For every content type described here, the client reads only the pack. MUL and UOP stay only for the content listed in "Not in the pack". The client never reads a patch format. `verdata.mul` is not imported: shard owners merge it into the base files before converting. The converter merges the legacy map patches (`mapdif*`, `mapdifl*`, `stadif*`, `stadifi*`, `stadifl*`) into the maps at import. The `map*x`, `statics*x` and `staidx*x` files are not read; a shard that wants them renames them to the regular names before converting.

The format version is 1. `FileHeader.version` and the manifest `version` are both 1. The tile data layout (magic `TILE`) was introduced in revision 1.1, and `skills.bin` in revision 1.2. Revisions are marked by required files, not by the version number: a client accepts only packs that contain a `TILE` `tiledata.bin` and a `skills.bin`.

## Conventions

- All integers are little-endian. All structs are packed (no padding beyond the explicit `pad` fields) and blittable.
- "Colour" and "16-bit colour" mean UO's 1555 layout: bits 0-4 blue, 5-9 green, 10-14 red, bit 15 alpha (1 = visible) where stated, otherwise as stored by the client.
- Ids index tables directly: a table's entry `i` is id `i`.
- The canonical 5-to-8-bit expansion is bit replication, `e(c) = (c << 3) | (c >> 2)` for `c` in 0..31. It is what the GPU returns when it samples `B5G5R5A1_UNORM`. Every CPU expansion in the pack and the renderer uses it (`Uo.Pack.PackColor.Expand5`, `PackColor.ToRgb8`), so native 16-bit sampling, the RGBA8 fallback and Indexed8 rendering agree. `v >> 3` inverts it. ClassicUO's `HuesHelper` table (`floor(c*255/31)`) is a different expansion and is not used for pack data, except for gump drawing (see "Gumps").
- An RGBA8 value stored as a `u32` has red in the low byte: `r | g << 8 | b << 16 | a << 24`.

## Layout

```
<pack>/
  manifest.json            pack version, source client, facets, files[{path, size, sha256}], body choices
  tiledata.bin             tile data: every field, names and the draw flags (TILE); the only tile data file
  hues_raw.bin             hues.mul entries: 16-bit colours, TableStart/End, names (HERW); the RGBA8 hue LUT is built from it
  radarcol.bin             radar colours (RADC)
  multis.bin               multi component lists (MLTI)
  lights.bin               light textures (LGHT)
  tileart.bin              paperdoll appearances from tileart.uop (TART)
  equipconv.bin            Equipconv.def (EQCV)
  layerrules.bin           equipment layering rules (LAYR); optional, see layer-rules.md
  skills.bin               skills.mul + Skills.idx: a string-list store (SKLS); required since revision 1.2
  skillgrp.bin             skillgrp.mul: group names and per-skill group numbers (SKGR); optional
  multimap.bin             Multimap.rle and facet0*.mul: a record store of their RLE streams (MMAP); optional
  art/
    sprites.bin            SpriteEntry[] (SPRS), index = land id 0..0x3FFF, or 0x4000 + static graphic
    page_NNNN.bin          atlas pages (PPAK)
  tex/
    texmaps.bin            SpriteEntry[] (SPRS), index = texmap id
    page_NNNN.bin          atlas pages (PPAK)
  anim/
    anim.bin               body, direction and frame tables (ANIM)
    palettes.bin           Indexed8 palettes and the palette id of every frame (APAL)
    animdata.bin           animated static art (ANDT)
    page_NNNN.bin          atlas pages (PPAK)
  map/<facet>/
    facet.json             facet size and region grid
    r_<rx>_<ry>.bin        map region (UORG)
  gumps/
    gumps.bin              gump images, sizes and gump.def aliases: a record store (GUMP)
  text/
    fonts.bin              ASCII fonts: a record store, a record per font (FNTA)
    unifont.bin            unicode fonts: a record store, a record per 256-character page (FNTU)
    cliloc.bin             cliloc, every language: a record store, a record per language (CLIL)
    speech.bin             speech.mul keywords (SPCH)
    stringdict.bin         string_dictionary.uop: a string-list store (STRD); optional
  loose/
    <name>                 hand-edited text and def files, verbatim, lower-case names; optional
  audio/
    sounds.bin             sound effects, raw 22,050 Hz mono 16-bit PCM: a record store (SNDS)
    music.bin              music, the original MP3 bytes and loop flags: a record store (MUSC)
```

`NNNN` is the zero-padded decimal page index (`page_0000.bin`). Page indices are contiguous from 0.

`PackAssets.Open` (`src/ClassicUO.Assets/PackAssets.cs`) requires `manifest.json` and these files: `tiledata.bin`, `hues_raw.bin`, `radarcol.bin`, `multis.bin`, `lights.bin`, `tileart.bin`, `equipconv.bin`, `art/sprites.bin`, `tex/texmaps.bin`, `anim/anim.bin`, `anim/palettes.bin`, `anim/animdata.bin` and (revision 1.2) `skills.bin`, each with an "incomplete asset pack ... convert the client again with Uo.Converter" error. It rejects a manifest whose `version` is above 1. The gumps, text, audio, skill group, multimap, string dictionary and loose files are optional at open time; see each section for what happens without them.

## FileHeader and versioning

Every binary file starts with a 16-byte `FileHeader`:

| Field | Type | Meaning |
|---|---|---|
| magic | u32 | file type (table below), the ASCII tag read as a little-endian u32 |
| version | u16 | 1 |
| kind | u16 | compression of the rest of the file (see "Compression") |
| reserved | u64 | 0 |

| Magic | File |
|---|---|
| `PPAK` 0x4B415050 | atlas page |
| `SPRS` 0x53525053 | sprite table |
| `UORG` 0x47524F55 | map region |
| `TILE` 0x454C4954 | tiledata.bin |
| `HERW` 0x57524548 | hues_raw.bin |
| `RADC` 0x43444152 | radarcol.bin |
| `MLTI` 0x49544C4D | multis.bin |
| `LGHT` 0x5448474C | lights.bin |
| `TART` 0x54524154 | tileart.bin |
| `EQCV` 0x56435145 | equipconv.bin |
| `LAYR` 0x5259414C | layerrules.bin (optional) |
| `ANIM` 0x4D494E41 | anim/anim.bin |
| `APAL` 0x4C415041 | anim/palettes.bin |
| `ANDT` 0x54444E41 | anim/animdata.bin |
| `GUMP` 0x504D5547 | gumps/gumps.bin (a record store) |
| `FNTA` 0x41544E46 | text/fonts.bin (a record store) |
| `FNTU` 0x55544E46 | text/unifont.bin (a record store) |
| `CLIL` 0x4C494C43 | text/cliloc.bin (a record store) |
| `SPCH` 0x48435053 | text/speech.bin |
| `SNDS` 0x53444E53 | audio/sounds.bin (a record store) |
| `MUSC` 0x4353554D | audio/music.bin (a record store) |

Retired magics:

- `HUES` 0x53455548 (`hues.bin`, see "Hues"). Readers ignore the file.
- `TLTD` 0x44544C54 (the compact `tiledata.bin` of older packs) and `TLDX` 0x58444C54 (`tiledata_ext.bin`). The tile data reader rejects both with a "convert again" message.

**Reader rule.** A reader accepts `version` 0 and 1 and rejects anything higher (`PackConst.VersionSupported`). Version 0 files come from pre-1.0 packs and have the same layout as their version-1 counterparts, so no reader branches on the version. Version-0 packs lack the game data tables and the single `tiledata.bin`, so they do not load.

`manifest.json`'s `version` is the pack's format version (1).

## Compression (`FileHeader.kind`)

Compression is chosen per file. Readers accept any mix of kinds in one pack, and raw is always valid for any file. A shard may ship a fully raw pack for binary delta patching, since the manifest's sha256 identifies changed files.

In every kind, the logical payload is the bytes a raw file has after its `FileHeader`.

- **Kind 0, raw.** The payload follows the header as is.
- **Kind 1, Brotli.** The payload is one Brotli stream (quality 5).
- **Kind 2, chunked Brotli.** The payload is split into fixed-size chunks that compress independently, so several workers can decode one file and a reader can decode chunk 0 alone to get the leading headers:

  | Field | Type | Meaning |
  |---|---|---|
  | chunkBytes | u32 | uncompressed chunk size (1 MiB = 1,048,576 in the ship profile); the last chunk may be shorter |
  | logicalBytes | u64 | uncompressed payload size |
  | chunkCount | u32 | `ceil(logicalBytes / chunkBytes)` |
  | compressedSize | u32[chunkCount] | compressed size of each chunk; chunk offsets are prefix sums from the end of this table |
  | chunks | bytes | chunk `i` is an independent Brotli stream (quality 5, window 22) that decodes to logical bytes `[i*chunkBytes, (i+1)*chunkBytes)` |

  Decode each chunk straight into the destination buffer at its logical offset. Chunk 0 always contains the `PageHeader` (and for regions the `RegionHeader` and offset table). `Uo.Pack` provides `ChunkedBrotli.Decode` (single thread, in memory), `ChunkedBrotliStream` (sequential) and `ChunkedBrotliDecoder` (persistent worker pool, used by `AtlasPage.Read(path, span, decoder)`).

Only pages and regions are compressed as whole files; all other files are Kind 0. Record stores (gumps, fonts, cliloc, sounds, music) are Kind 0 files whose records are compressed one by one (see "Record stores"). Which kind pages and regions use is a profile choice (see "Ship profile").

## Atlas pages (`page_NNNN.bin`)

A page is the unit of GPU residency and streaming: the renderer maps logical pages onto layers of a fixed-capacity texture array and evicts least-recently-used pages.

`PageHeader` (16 bytes), the first bytes of the logical payload:

| Field | Type | Meaning |
|---|---|---|
| width, height | u16 | page side in texels (square; a power of two) |
| mipCount | u16 | levels stored, mip0 first |
| format | u16 | `PixelFormat` |
| dataBytes | u32 | size of the mip chain that follows (uncompressed) |
| reserved | u32 | 0 |

The mip chain follows: level 0 (side `s`), level 1 (`s/2`), and so on, back to back. A full chain ends at 64x64 (6 levels for 2048, 5 for 1024).

### Pixel formats

| Value | Name | Use | Texel |
|---|---|---|---|
| 2 | `B5G5R5A1` | art, texmaps | u16 colour; a level of side `s` is `2*s^2` bytes |
| 3 | `Indexed8` | animations | u8 palette index; a level of side `s` is `s^2` bytes |
| 0 | `Rgba8` | experimental, not shipped | 4 bytes per texel |
| 1 | `Bc7` | experimental, not shipped | 16 bytes per 4x4 block |

**B5G5R5A1.** Bits 0-4 B, 5-9 G, 10-14 R, 15 A. This matches `SDL_GPU_TEXTUREFORMAT_B5G5R5A1_UNORM` and the equivalent Vulkan, Metal and D3D12 formats. mip0 is bit-exact to the client data: each visible texel is the source colour with A = 1, and transparent texels are 0. Lower mips are an alpha-aware box filter of the expanded RGBA8 image, each channel rounded back to 5 bits with `(v*31 + 127) / 255`, and A = 1 when the filtered alpha is at least 128. A GPU that cannot sample 16-bit textures gets the page expanded to RGBA8 with the canonical expansion at upload time. This is a runtime fallback, not a pack format.

**Indexed8.** One byte per texel, an index into the palette of the frame that covers the texel (see "palettes.bin"). Index 0 is transparent. Pages are mip0 only (`mipCount` = 1); the shader filters by hand at mip0.

### Packing rules

- Every sprite has a 2-texel gutter on all sides, filled by extruding its edge texels (colours for B5G5R5A1, indices for Indexed8). A bilinear footprint at mip0 never leaves the sprite's cell, so for Indexed8 all four taps use the sprite's own palette.
- Texels outside every cell are 0.
- Sprites are trimmed to their visible bounding box; the sprite table records the trim.
- Identical sprites (same trimmed texels, trim and original size) share one rect.
- Cell alignment is 1 texel.

## Sprite tables (`art/sprites.bin`, `tex/texmaps.bin`)

`FileHeader`, `count i32`, then `SpriteEntry[count]`.

`SpriteEntry` (20 bytes):

| Field | Type | Meaning |
|---|---|---|
| page | u16 | page index; 0xFFFF = absent |
| x, y | u16 | top-left of the trimmed sprite in the page (inside the gutter) |
| w, h | u16 | trimmed size |
| trimX, trimY | i16 | offset of the trimmed rect inside the original image |
| origW, origH | u16 | original image size, used for UO draw anchoring |
| flags | u16 | bit 0 land art, bit 1 texmap. Bit 2 is retired (it once copied the tile's `PartialHue` flag, which readers now take from `tiledata.bin`); old packs may set it and readers ignore it |

- The art table has `0x4000 + staticCount` entries: land ids first, then statics at `0x4000 + graphic`. `art.def` and `TexTerr.def` substitutions are already applied.
- An id that exists in the client but is fully transparent is absent and keeps `origW` and `origH`.
- Pixels are stored unhued. Hues are applied in the shader (see "Hues").

## Map

### `map/<facet>/facet.json`

`{ "index", "width", "height", "blocksX", "blocksY", "regionBlocks" }`: facet size in cells and in 8x8-cell blocks, and the region side in blocks (64). Only facets with a map file on disk are converted. The same records appear in the manifest's `facets` array.

### Regions (`r_<rx>_<ry>.bin`)

A region holds up to 64x64 blocks (512x512 cells). Edge regions are smaller.

Logical payload:

1. `RegionHeader` (12 bytes): facet, rx, ry, blocksW, blocksH, reserved (all u16).
2. `u32 blockOffset[blocksW*blocksH]`, row-major (`by*blocksW + bx`). Each is a byte offset from the start of the logical file, including the 16-byte FileHeader; 0 = empty block.
3. Block records:
   - `LandCell[64]`, row-major `y*8 + x`: tileId u16 (`& 0x3FFF` applied), z i8, pad u8;
   - staticCount u16, pad u16;
   - `StaticCell[staticCount]`: graphic u16, x u8, y u8 (0..7), z i8, pad u8, hue u16.

Statics with graphic 0 or 0xFFFF, or x or y above 7, are dropped (as ClassicUO's `Chunk.Load` does). For Kind 1 and 2, the simplest reader rebuilds the logical file (header plus decoded payload) and parses it as Kind 0 (`RegionFile.Parse`, `RegionFile.Decompress`).

The data is semantic: the renderer bakes per-chunk instance buffers on worker threads, and walkability and collision stay available for client-side prediction. An UltimaLive update replaces one block record in memory and re-bakes that chunk.

## Tile data

### `tiledata.bin`

The pack's only tile data file. `FileHeader` (magic `TILE`, Kind 0), `landCount i32`, `staticCount i32`, `nameBytes i32`, `LandTileInfo[landCount]`, `StaticTileInfo[staticCount]`, then `nameBytes` of UTF-8 name table (`TileDataFile`, `poc/Uo.Pack/PackTables.cs`).

**Standardized counts.** Every pack has exactly `landCount` = 0x4000 and `staticCount` = 0x10000, whatever the source install (`PackConst.LandTileCount`, `PackConst.StaticTileCount`). A pre-7.0.9 install has only 0x4000 statics; the converter pads the rest with empty entries (no name, no flags, every field 0), and their `packFlags` come from the same rules as a modern install's empty entries. Any u16 graphic a server sends therefore indexes inside the table, and the client needs no bounds check per site. A source with more entries than the format holds is refused, not truncated. The writer and the reader both require the exact counts.

`LandTileInfo` (20 bytes): flags u64, nameOffset u32, nameLength u16, texId u16, packFlags u16, pad u16.

`StaticTileInfo` (32 bytes):

| Field | Type | Meaning |
|---|---|---|
| flags | u64 | `TileFlag` |
| count | i32 | quantity (ClassicUO `StaticTiles.Count`) |
| nameOffset, nameLength | u32, u16 | name in the name table |
| animId | u16 | animation body for wearables and animated items |
| hue | u16 | |
| lightIndex | u16 | light id for light sources (see `lights.bin`) |
| weight | u8 | |
| layer | u8 | equipment layer |
| height | u8 | |
| pad | u8 | |
| packFlags | u16 | draw flags (`TilePackFlags`, below) |
| pad2 | u16 | |

- Flags keep UO's `TileFlag` bit values (64-bit; the 32-bit flags of pre-7.0.9.0 clients are widened). Values are after `art.def` substitution.
- A name is the UTF-8 encoding of ClassicUO's string: the 20-byte field decoded as UTF-8 with leading and trailing NULs trimmed. Length 0 = empty name. Duplicate names share bytes in the table.
- **Untrusted input.** `TileDataFile.Read` checks the magic, the version and Kind 0; `landCount` = 0x4000 and `staticCount` = 0x10000 exactly; `nameBytes` in 0..5,242,880 (`MaxNameTableBytes`, every entry with a longest name); the file size equal to what the counts give (before allocating); and every name reference inside the table and at most 64 bytes long (`MaxNameBytes`). The references are checked before the name table is allocated. The converter refuses to write a longer name.
- **Names are decoded once per distinct (offset, length)** (`TileDataFile.DecodeNames`): entries that share a reference share the string. With the caps, decoded names stay small even for a hostile file.
- **The renderer's compact arrays** (`LandTileData`: flags u64, texId u16, packFlags u16; `StaticTileData`: flags u64, height u8, layer u8, packFlags u16; 12 bytes each) are built at load by `TileDataFile.Compact`. They are not stored.

### NoDraw flags (`TilePackFlags`, in `packFlags`)

ClassicUO used to hide some tiles in code rather than with tiledata flags. The converter (`poc/Uo.Converter/TileRules.cs`, the only code that knows these rules) evaluates that code once and stores the result. Clients and the renderer read only the flags (`Uo.Pack.TileDraw`), so a shard admin hides or shows a tile by editing its flag, with no code change.

| Bit | Name | Meaning |
|---|---|---|
| 0 | `NoDraw` | Never drawn: land, or a static, item, multi component or effect graphic. |
| 1 | `NoDrawUnlessGargoyle` | Static graphic hidden unless the player is a gargoyle. |
| 2 | `NameNoDraw` | Informational: the name starts with `nodraw` (case-insensitive). Nothing reads it; for statics the converter folds it into `NoDraw`. |
| 3 | `NoDrawAsMulti` | A multi item whose display graphic (the multi's first component) is this static is not drawn. |

What the converter writes (`TileRules.Land`, `TileRules.Static`):

- **Land:** `NoDraw` for ids 0, 1 and 2.
- **Statics**, in this order:
  1. 0x0001, 0x21BC, 0xA1FE..0xA201: `NoDraw`.
  2. 0x9E4C, 0x9E64, 0x9E65, 0x9E7D: `NoDraw` only with `Background` or `Surface`.
  3. 0x63D3 and 0x2198..0x21A4: `NoDraw`.
  4. 0x0F65 (easel) on a source client older than 6.0.14.4: no `NoDraw`, whatever its other flags.
  5. A name starting with `nodraw`: `NoDraw`.
  6. `NoDiagonal` (0x02000000): `NoDraw`, or `NoDrawUnlessGargoyle` when the tile also has `Animation`.
- **Multi items:** `NoDrawAsMulti` for statics 0, 1 and 2.

At runtime the season swap stays in code (the flag is read for the swapped graphic), and so does the gargoyle exception: `TileDraw.Static(flags, playerIsGargoyle)`. There is no fallback for packs without the flags: a pack without a `TILE` `tiledata.bin` is rejected.

The easel's visibility is whatever the pack says. The converter applies the 6.0.14.4 rule once, with the source client's version, and the client never consults its version setting for it. To change it, edit the `NoDraw` bit in `packFlags` of static 0x0F65 and rebuild or patch the pack.

## Hues

`hues_raw.bin` is the pack's only hue file. The renderer's RGBA8 LUT is built from it at load.

### `hues_raw.bin`

`FileHeader`, `count i32`, `HueEntry[count]`. `HueEntry` (88 bytes) is byte-identical to ClassicUO's `HuesBlock`: colours u16[32], tableStart u16, tableEnd u16, name u8[20] (raw bytes, NUL-padded). Entry `i` is hue `i + 1`. The file serves CPU font and gump hueing and is the source of the renderer's LUT. The 4-byte group headers of hues.mul are unused by ClassicUO and dropped. The converter writes `groups * 8` entries.

Readers treat the file as untrusted: `count` must be 0..65536 (`HueRawFile.MaxEntries`; hue ids are u16) and the entries must fit in the file.

### The RGBA8 LUT (built at load, not stored)

`count * 32` RGBA8 `u32`. Row `i` is hue `i + 1` (UO hue 0 = unhued), column `s` is shade 0..31. Texel `(i, s)` is the canonical expansion of entry `i`'s colour `s`: `e(r) | e(g) << 8 | e(b) << 16 | 0xFF << 24`, bit 15 ignored. `Uo.Pack.HueLut.Build` is the one implementation; the client (`HuesLoader`) and the poc renderer both call it.

Shader hueing follows ClassicUO: the shade index is the texel's 5-bit red channel. A partial-hue sprite hues only exactly gray texels (`r5 == g5 == b5`, compared as 5-bit integers, no tolerance).

**Retired: `hues.bin`** (magic `HUES`; `count i32`, then the LUT). Older packs stored the LUT as a file. Readers ignore it and the converter no longer writes it (a run of the `hues` stage deletes a stale copy from its output).

### `radarcol.bin`

`FileHeader`, `count i32`, `u16 colour[count]`: radarcol.mul as is. Index = land id (0..0x3FFF) or `0x4000 +` static graphic, as `HuesLoader` indexes it. Used by the minimap and world map.

## Animations (`anim/`)

Mobiles, equipment and corpses. Frames are resolved at conversion time exactly as ClassicUO draws them, so the client needs no anim .def parsing for frame lookup.

### `anim.bin`

`FileHeader`, `bodyCount i32`, `dirCount i32`, `frameCount i32`, then `AnimBodyEntry[bodyCount]` (index = body id), `AnimDirEntry[dirCount]`, `AnimFrameEntry[frameCount]`. Reader: `AnimTableFile.Read`; `AnimTableFile.Frames(..., body, action, dir)` looks up one direction.

`AnimBodyEntry` (24 bytes):

| Field | Type | Meaning |
|---|---|---|
| firstDir | u32 | record of (action 0, dir 0); record (a, d) is at `firstDir + a*8 + d` |
| actionCount | u16 | 0 = no frames of its own; actions at or above `actionCount` have none |
| type | u8 | ClassicUO `AnimationGroupsType`: 0 monster, 1 sea monster, 2 animal, 3 human, 4 equipment (5 is defined as unknown) |
| source | u8 | bit 7 = `AnimationFrame<n+1>.uop`, else `anim<n+1>.mul` (n = low 7 bits); 0xFF = none |
| flags | u32 | mobtypes.txt flags (ClassicUO `AnimationFlags`); bit 31 = listed in mobtypes; bit 30 (`MountAbsoluteFlag`, 0x40000000) is pack-only, see `mountShift` |
| sourceBody | u16 | body id inside the source file (the Bodyconv.def target), else the body |
| aliasBody, aliasHue | u16, u16 | Body.def replacement, already chained (0xFFFF = none): draw aliasBody's entry, type and flags with aliasHue |
| corpseBody, corpseHue | u16, u16 | Corpse.def replacement for corpses (0xFFFF = none) |
| mountHeight | i8 | Bodyconv.def mounted height offset of this entry's own conversion (informational) |
| mountShift | i8 | rider seat for this body as a mount. Without the absolute flag: a shift added to the client's per-item Mounts table offset (0 in a default pack). With bit 30 of `flags` set: the rider offset itself, for every item |

`AnimDirEntry` (8 bytes): firstFrame u32, frameCount u16 (empty frames included; 0 = no animation), storedDir u8, flags u8 (bit 0 = mirrored). There are 8 records per action, one per UO direction 0..7 (N, NE, E, SE, S, SW, W, NW). The client stores 5 directions: directions 3..7 are stored 0..4, and directions 2, 1, 0 reuse stored 1, 2, 3 mirrored (ClassicUO `GetAnimDirection`; `AnimConst.MapDirection`). Mirrored records point at the same frames.

`AnimFrameEntry` (24 bytes): a `SpriteEntry` (page, rect, trim; origW and origH = the frame's size; page 0xFFFF = empty frame), then centerX i16, centerY i16.

**Anchoring** (ClassicUO `MobileView`): from the mobile's tile screen position (x, y), with z applied as for statics, the anchor is `ax = x + 22`, `ay = y + 22 - 3`.

- Unmirrored: the frame's top-left is `(ax - centerX, ay - (origH + centerY))`; the trimmed rect is at `+ (trimX, trimY)`.
- Mirrored: the frame's left is `ax - (origW - centerX)`; draw the trimmed rect flipped horizontally at x offset `origW - trimX - w`, same y.

**Resolution done by the converter** (as ClassicUO's `AnimationsLoader.GetIndices` and `Animations.GetAnimationFrames`):

- Bodies are scanned up to the larger of the minimum body count and the highest id in mobtypes.txt and Bodyconv/Body/Corpse.def (`AnimConvert.BodyCount`).
- mobtypes.txt gives type and flags; flag 0x10000 means frames come from `AnimationFrame*.uop`.
- AnimationSequence.uop group replacement is applied: UOP action `a` holds what ClassicUO draws for `a`. The renderer must not remap UOP actions.
- **Bodyconv.def is applied per the import choices** (`bodies.json`, see [bodies.md](bodies.md)): the pack holds exactly one resolved body set, and the client does no body remapping at run time. The manifest's `bodies` record holds the choices (policy, preset, flags, remodels, extras, bodies.json SHA-256); `--verify-only` rebuilds the plan from it.
- A converted MUL body's actions can follow the converted target's AnimationSequence.uop entry (`bodies.json` `sequences: "target"`).
- **Extras** (`bodies.json` `extras`): a new, unused body id whose entry copies another variant's frames, with the type and mobtypes flags of the body that variant belongs to.
- Body.def and Corpse.def stay as aliases (single hop for the client: the converter stores the final target) and apply only to plain anim.mul bodies.
- Frame counts: MUL directions carry their own; for UOP, a group's frames split into `round(n / 5)` per direction (minimum 10 for equipment), with gaps filled by empty frames.
- Equipment uses the item's animId (tiledata) as the body. ClassicUO draws it only when that body's type is equipment or human, checked at runtime.

Frames are packed body, action, direction, frame, so one body's frames share few pages.

### `palettes.bin`

`FileHeader`, `paletteCount i32`, `frameCount i32`, `u32 framePalette[frameCount]` (palette id per `AnimFrameEntry`; `0xFFFFFFFF` = empty frame), then `u16 palettes[paletteCount * 256]` as B5G5R5A1.

- Entry 0 of every palette is 0 (transparent). The rest are the source palette's distinct visible colours that some frame drawn with it uses, in order of first appearance, with A = 1.
- Frames with identical texels share one rect and one palette. The table is small enough (about 18.5 MiB for a stock 7.0.x client) to keep resident as one storage buffer.
- **Untrusted input** (`AnimPaletteFile.ReadLayout`): `paletteCount` in 0..65,536 (`MaxPalettes`, 32 MiB of palette data), `frameCount` in 0..2^24 (`MaxFrames`), the frame table plus palette data inside the file, and every frame's palette id below `paletteCount` or `0xFFFFFFFF`. The client also refuses a `palettes.bin` whose frame count differs from `anim.bin`'s.

**Indexed8 rendering.**

- The CPU writes the frame's palette id into the instance when it fills the instance from the `AnimFrameEntry`.
- The shader computes `t = uv*S - 0.5`, `i = floor(t)`, `f = frac(t)`, does 4 index loads and 4 palette loads, and treats index 0 as transparent.
- Hue applies per tap, after the palette lookup and before blending: the partial-hue gray test is on the tap's 5-bit integers, and the shade index is `r5`.
- Then blend the visible taps (`rgb = sum(w_i * rgb_i) / sum(w_i)`, `a = sum(w_i)`), alpha-test at 0.5 and apply light.

### `animdata.bin`

`FileHeader`, `count i32`, `AnimDataEntry[count]` sorted by graphic. `AnimDataEntry` (70 bytes): graphic u16, frameCount u8, frameInterval u8, frameStart u8, unknown u8, frames i8[64] (graphic offsets: frame `k` draws `graphic + frames[k]`).

Only graphics whose animdata.mul record has `frameCount > 0` are listed. ClassicUO's `AnimatedStaticsManager` animates every static that has the `Animation` tile flag and a record: it cycles `frames[0..frameCount)` and advances one frame every `frameInterval * 2 * ITEM_EFFECT_ANIMATION_DELAY` ms (`2 * ITEM_EFFECT_ANIMATION_DELAY` when `frameInterval` is 0). `frameStart` and `unknown` are stored but unused.

## Game data tables

### `multis.bin`

`FileHeader`, `multiCount i32`, `componentCount i32`, `MultiEntry[multiCount]` (index = multi id), `MultiComponent[componentCount]`.

- `MultiEntry` (8 bytes): firstComponent u32, componentCount u32 (0 = no such multi).
- `MultiComponent` (10 bytes): graphic u16, x i16, y i16, z i16, flags u16 (bit 0 = visible).
- Source and semantics mirror `MultiLoader.GetMultis`. On UOP installations the source is `MultiCollection.uop`, and a component is visible when its flags are 0 or 0x100; otherwise multi.idx/mul, where any non-zero flags means visible. `multiCount` is the highest present id + 1.

### `lights.bin`

`FileHeader`, `count i32`, `dataBytes i32`, `LightEntry[count]` (index = light id, the tiledata `lightIndex`), then `dataBytes` of texels.

- `LightEntry` (8 bytes): offset u32 (into the texel block), width u16, height u16 (0x0 = no such light).
- Texels are light.mul bytes, one per texel, row-major. Values 0..31 are intensities; values above 31 are negative lights.
- ClassicUO folds them to `i = ~v & 31` and draws gray `i << 3` with alpha 0xFF, or transparent when `i = 0` (`LightFile.ToRgba8`).
- Lights are uploaded once as individual textures, not through the atlas.

### `tileart.bin`

`FileHeader`, `count i32`, `TileArtAppearance[count]` sorted by (graphic, body). `TileArtAppearance` (8 bytes): graphic u16 (item), body u16 (mobile body), appearance u32.

This is the only part of tileart.uop ClassicUO uses: `PaperDollInteractable` asks `TileArtInfo.TryGetAppearance(body)` and, if the gump `MALE_GUMP_OFFSET + appearance` exists, draws it as the paperdoll gump. Only items whose tileart record has more than one appearance subtype are listed, with subtype 0's map.

### `equipconv.bin`

`FileHeader`, `count i32`, `EquipConvEntry[count]` sorted by (body, graphic), unique. `EquipConvEntry` (10 bytes): body u16, graphic u16 (item animId), newGraphic u16, gump u16, color u16.

The rows are Equipconv.def as ClassicUO resolves it in `ProcessEquipConvDef` (clients 3.0.0 and later only):

- gump 0 means `graphic`, and gump 0xFFFF or -1 means `newGraphic`;
- rows with gump above 0xFFFF are dropped;
- later rows replace earlier ones.

At runtime, an equipped item on body `body` with animId `graphic` is drawn as `newGraphic`, and the paperdoll uses `gump`.

### `layerrules.bin` (optional)

ClassicUO's hard-coded equipment layering (the draw order, the cloak move by facing, the covering rules and the gargoyle remaps) as data: magic `LAYR`, a `LayerRulesHeader` of six i32 counts, then body groups, base draw-order tables, rules, conditions and ranges. The converter writes it from `Uo.Pack.LayerRulesDefault.Create()`, or from a JSON file given with `--layer-rules` (stage `layers`, no install data), and a client whose pack has no such file uses the same `Create()`. The client reads it while the `ecs_layer_rules` setting is on (the default). The full layout, the evaluation rules, the setting and the checker are in [layer-rules.md](layer-rules.md).

## Skills, skill groups, multimap, string dictionary

The last binary files the client read from the UO folder. `skills.bin` is required: every supported install, from 4.0.11c on, has skills.mul and Skills.idx, so the converter fails without them and the client refuses a pack without `skills.bin`. The others are optional: the converter writes one only when the install has the source file (a stale copy from an earlier run is removed), and a client whose pack lacks it behaves as it did without the file. Reader and writer: `poc/Uo.Pack/MiscData.cs`.

A **string-list store** is a record store (below) with exactly one slot, whose record is: count u32, blobBytes u32, `StringListRecord[count]` (8 bytes: value u32, end u32), then the UTF-8 blob; entry `i`'s text is `blob[end[i-1] .. end[i])`. The reader caps it at 2^20 entries and 64 MiB of text and requires `8 + 8*count + blobBytes` to equal the record's raw size, with ends that never decrease or pass the blob.

| File | Magic | Content |
|---|---|---|
| `skills.bin` | `SKLS` | String-list store, record raw. One entry per skill in the client's order (Skills.idx entries with a positive length, in index order); value bit 0 = the skill has a use button. Required (revision 1.2). |
| `skillgrp.bin` | `SKGR` | Not a store: `FileHeader`, nameCount u32, assignmentCount u32, blobBytes u32, u32 nameEnd[nameCount], i32 assignment[assignmentCount], UTF-8 blob. The names are skillgrp.mul's groups 1..count−1 (ASCII or, after a −1 marker, UTF-16 in the file); the assignments are the file's trailing i32 group numbers, unchecked. The client builds the groups from them as it built them from the file: group 0 is its own "Miscellaneous", a number past the last group skips the entry, a negative one makes it use its built-in groups. A skillgrp.mul the client could not read (count 0 or negative, a name or number past the end of the file) gets no table, so the client uses the built-in groups as before. |
| `multimap.bin` | `MMAP` | Record store, 1..256 slots. Slot 0 is Multimap.rle; slot 1 + n is the n-th facet0*.mul file in ordinal path order (the order the client indexed them, so a missing facet02.mul shifts the later ones). Records are the files' bytes, raw up to 16 KiB, else Brotli (quality 9), at most 64 MiB decoded each (`MiscConst.MaxMultiMapRecordBytes`; the stock facet01.mul is 12 MB): the converter refuses a larger file, and the client refuses a record that declares more before allocating. The client decodes a record when the server asks for a map (packets 0x90/0xF5) and keeps nothing; a facet image needs slot 0, as before. |
| `text/stringdict.bin` | `STRD` | String-list store, record Brotli. string_dictionary.uop's strings in index order, value 0. Absent for installs without the file (pre-UOP). |

## Loose text files (`loose/`)

Text and def files shards edit by hand are copied byte for byte into `loose/` under lower-case names: `prof.txt`, `citytext.enu`, `stitchin.def`, `anim1.def`, `anim2.def`, `walls.txt`, `floors.txt`, `doors.txt`, `misc.txt`, `stairs.txt`, `teleprts.txt`, `roof.txt`, `suppinfo.txt` (`MiscConst.LooseFiles`; the source is found case-insensitively, files_override applied, at most 16 MiB). A file the install lacks is not in the pack. The client finds them through one lookup, `UOFileManager.GetLooseFilePath`: a `files_override` entry wins; else, when the pack has a `loose/` folder, the pack's copy (absent means the file is absent); a pack without `loose/` (converted before it existed) reads the loose-file folder as before. The client parses them with the same code as before, so a shard can edit the pack copy directly; the manifest hash then no longer matches until the manifest is rewritten.

## Record stores

Content that the client reads one item at a time, and that the GPU does not sample from pack pages, goes into a record store: one Kind 0 file per content type, read by offset through one file handle kept open. Gumps, ASCII fonts, unicode fonts, cliloc, sound effects, music, the multimap and the string lists use it, each with its own magic, slot argument and record meta. Reader and writer: `Uo.Pack.RecordStore`, `RecordStoreWriter` (`poc/Uo.Pack/RecordStore.cs`).

| Part | Layout |
|---|---|
| `FileHeader` | magic = the content type, version 1, kind 0 |
| `StoreHeader` (16 bytes) | slotCount u32, recordCount u32, flags u32 (type-specific), reserved u32 |
| `StoreSlot[slotCount]` (8 bytes each) | record u32 (record index + 1; 0 = no such id), arg u16 (type-specific), reserved u16 |
| `StoreRecord[recordCount]` (24 bytes each) | offset u64 (from the start of the file), storedBytes u32, rawBytes u32, codec u16, meta0, meta1, meta2 u16 (type-specific) |
| payloads | back to back, each decodable on its own |

- Slot `i` is id `i`. Several ids may share one record (aliases, identical content).
- Codec 0: the payload as is (`storedBytes = rawBytes`). Codec 1: one Brotli stream (window 22) that decodes to `rawBytes` bytes. Readers reject other codecs.
- A reader reads the header, the slot table and the record table once. Because a shard ships the pack, the reader treats it as untrusted (`RecordStore.Open`):
  - before allocating the tables it checks that they fit in the file (`slotCount` at most 2^24, `recordCount` at most `slotCount`, the tables end at or before the file's end);
  - every slot points into the record table;
  - every payload lies in the payload area: `payloadStart <= offset <= fileLength` and `storedBytes <= fileLength - offset` (no wrap-around);
  - codec 0 has `storedBytes = rawBytes`; other codecs are rejected.

  A failure rejects the whole file. A content type whose slot count is fixed passes its bounds to `Open`, which checks them before allocating the tables. The content type then checks its own meta. A record is one positional read plus its decode, from any thread, into the caller's buffer.
- There is no whole-file compression and there are no per-record files: a store is read by offset, and a record decodes alone.

## Gumps (`gumps/gumps.bin`)

A record store with magic `GUMP` holding every gump id the client's `GumpsLoader` resolves, with `gump.def` and loose `Gumps/*.gump` files already applied. The client reads gumps only from here when the file exists (`GumpsLoader.FromPack`); without it, it reads gumpart MUL/UOP. `CUO_PACK_GUMPS=0` reads gumpart MUL/UOP even when the pack has gumps.

- **`StoreHeader.flags`** bit 0: converted from `gumpartLegacyMUL.uop` (ClassicUO's `UseUOPGumps`; the status gump's layout depends on it).
- **Slots:** `slotCount` = highest present id + 1. `arg` = the gump.def alias hue (0 = none).
- **Records:** `meta0` = width, `meta1` = height, `meta2` = payload layout. The record table is the size table: the width and height of any gump are known without decoding it.
- **Payload layouts** (`Uo.Pack.GumpCodec`), both lossless 16-bit:
  - 0, texels: `width*height` B5G5R5A1 `u16`, row-major. Texel 0 = transparent, else bit 15 set and bits 0-14 the source colour. Used with codec 1.
  - 1, run-length rows: `u32 rowStart[height]` (in 4-byte units from the start of this table), then `(u16 texel, u16 run)` pairs. Row `y`'s runs end where row `y+1`'s start (the last row's at the end of the payload); texels no run reaches are transparent. This is the client's own entry layout, re-encoded canonically (a run per change of texel, no trailing transparent run). Used with codec 0.
  - Any (layout, codec) pair is valid for a reader. Ship profile: an image whose run-length payload is at most 64 KiB (`--gump-small n`, default 65,536) is stored as layout 1, codec 0; every other image as layout 0, codec 1 (Brotli quality 9). `--raw-gumps` stores every image as layout 1, codec 0.
- **Lossless:** the stored texel is the source value's colour bits plus bit 15 = visible. ClassicUO's decode ignores bit 15, so every image the client draws is reproduced exactly. Hueing needs the exact 5-bit gray texels, which a lossy format would not keep.
- **Drawing a gump** (ClassicUO `GumpsLoader.GetGump`): each visible texel `v` becomes RGBA8 with ClassicUO's 5-to-8-bit table (`HuesHelper.Color16To32`, not the canonical expansion: gumps go through the CPU and the batcher's RGBA8 atlas, and parity is with the MUL path) and alpha 255. With a slot hue `h`, `1 <= h < hue count`, first `v = hues[h - 1].colors[(v >> 10) & 31]`, and the texel stays visible only if that is not 0.
- **Converter resolution:** ids whose index entries are identical (gump.def copies the entry and sets a hue; UOP and MUL may alias too) share one record, and so do ids with identical decoded images. An id with a loose `Gumps/<id>.gump` file gets its own record from that file, with no hue; a broken file falls back to the archive entry with its hue. The client does not read loose gump files when the pack has gumps; re-run the converter (`--only gumps`) after changing them.

**Record checks at load** (`GumpsLoader.CheckRecord`): width and height 1..4096; layout 0 with `rawBytes = width*height*2` exactly; layout 1 with `rawBytes` a multiple of 4, at least the row table (`4*height`) and at most `4*height + 4*width*height`; any other layout is invalid; a Brotli record no larger than Brotli's bound for `rawBytes`. A record that fails, or whose payload later fails to decode, is invalid for the session: its ids are absent, the error is logged once per record, and nothing retries it.

**Client use** (`GumpsLoader`):

- Sizes and existence need no decode: `TryGetSize`, `Exists` and `PixelCount` read the record table.
- A first `GetGump` decodes one record into a pooled buffer and uploads it. Decodes are thread-safe, so windows built from many gumps decode them on workers in one batch (`Gump.Prefetch`).
- The picker mask is built from the decoded RGBA image at upload, so click-through on transparent texels is unchanged.

## Text: fonts, cliloc, speech (`text/`)

ASCII fonts (fonts.mul), unicode fonts (unifont*.mul), cliloc in every language the client folder has (Cliloc.*), and speech.mul's keyword table. The client reads each from here when its file exists (`FontsLoader`, `ClilocLoader`, `SpeechesLoader`); without it, or when the file is invalid as a whole, it reads the MUL files and says so in the log. `CUO_PACK_TEXT=0` reads the MUL files even when the pack has text.

The converter produces each file through a frozen copy of the client's loader, so the pack holds what the client resolved, quirks included: a unicode glyph whose width or height is not positive is no glyph, an ASCII glyph past the end of fonts.mul has no texel array, unifont1 is unifont when unifont1.mul is missing, a cliloc text stops at its first NUL. A unicode glyph whose data runs past the end of its file gets no glyph and is listed by the converter. The folder's `Clilocs.txt` becomes the overrides record.

### `text/fonts.bin`: ASCII fonts

A record store with magic `FNTA` (0x41544E46).

- **Slots:** one per font; `slotCount` is the client's font count (10 for a stock client), 1..256.
- **Records** (`Uo.Pack.AsciiFontCodec`): `meta0` = 224. Raw payload: `AsciiGlyphHeader[224]` (4 bytes: width u8, height u8, flags u8, pad u8; flag bit 0 = the glyph has a texel array), then for each glyph with bit 0, `width*height` u16 texels (fonts.mul's colours as stored), glyph after glyph. The header table is the size table. Glyph `i` is character `i + 32`; the client maps 0..31 to glyph 0.
- **Client:** a font's 224 glyphs are read and decoded on the font's first use (one positional read).

### `text/unifont.bin`: unicode fonts

A record store with magic `FNTU` (0x55544E46).

- **`StoreHeader.flags`:** bit `i` = unicode font `i` exists, after the client's own rule that font 1 is font 0 when unifont1.mul is missing.
- **Slots:** exactly `20 * 256`: slot `font*256 + page` holds characters `page*256 .. page*256 + 255` of the font. A page without glyphs has no record. Identical pages share one record.
- **Records** (`Uo.Pack.UnicodePageCodec`): `meta0` = font, `meta1` = page, `meta2` = glyphs in the page. Raw payload: `UnicodeGlyphHeader[256]` (8 bytes: offsetX, offsetY, width, height as i8, then dataOffset u32), then the glyphs' rows back to back. A glyph's rows are `height` rows of `(width - 1) / 8 + 1` bytes, 1 bit per pixel, most significant bit first: unifont's own layout. `dataOffset` is from the end of the header table; `0xFFFFFFFF` = no glyph, with every other field 0. A present glyph has width and height above 0.
- The header table is the first 2 KiB of the raw payload and gives every glyph's offsets and size.
- **Client:** glyphs are held per font and per page, and a page is read on first use of any of its characters.

### `text/cliloc.bin`: cliloc

A record store with magic `CLIL` (0x4C494C43): one record per language, and at most one record of the shard's own strings (overrides).

- **Record meta:** `meta2` low byte = the record's kind: 0 = a language, 1 = overrides. Other `meta2` bits are invalid; bit 8 was a retired block-compressed layout and a store with a record that sets it is rejected as unsupported. A language record holds its code in `meta0` and `meta1`: up to 4 lowercase ASCII letters or digits, one per byte, NUL-padded (`ClilocTable.PackLanguage`), from the file extension (`Cliloc.deu` gives `deu`). An overrides record has `meta0 = meta1 = 0`.
- **Slots:** one per record, in the order converted: the languages (enu first), then the overrides record when there is one.
- **Resolution** (`ClilocStore.Resolve`, shared by the client and the converter's verification): the slots are walked in order; a record no slot reaches is ignored. The store is unusable as a whole (the Cliloc.* files serve) when a record is reached by two slots, a `meta2` kind or bit is unknown, a language record has no valid code, a language code appears twice, an overrides record has a code, or there are two overrides records.
- **Records** (`Uo.Pack.ClilocTable`), one language file alone, with later duplicates resolved as the client resolves them (the last wins). Payload:

  | Part | Layout |
  |---|---|
  | header | count u32, blobBytes u32 |
  | ids | i32[count], strictly ascending (binary search) |
  | ends | u32[count]; string `i` is `blob[ends[i-1] .. ends[i])` (from 0 for `i` = 0) |
  | blob | UTF-8, the strings back to back in id order |

  A string is the UTF-8 encoding of the client's decoded text (cut at the first NUL, invalid bytes as U+FFFD). Ship profile: codec 0 (`--cliloc-brotli` stores the whole record as one Brotli stream).
- **Overrides record:** the converter folds the client folder's `Clilocs.txt`, or the file named by `--cliloc-overrides`, into a flat record of its own (`--no-cliloc-overrides` leaves it out), parsed by `ClilocOverrides.Parse`, the same rules the client uses for the loose file: one "number text" per line, a tab or the first run of spaces between them, blank lines and `#` lines skipped, a later line wins.
- **Lookups:** the client reads the language's record and, for any other language, enu's, plus the overrides record. A lookup tries, in order: a loose `Clilocs.txt` in the client folder, the pack's overrides record, the language, then enu.
- **Converter discovery** (`ClilocDiscovery.Find`): files whose name starts with `Cliloc.` in any case; the extension (1-4 ASCII letters or digits) is the language code. Exports and backups beside them are told apart by content, not by name: a cliloc file is BWT-compressed (byte 3 = 0x8E) or a raw table whose records add up exactly to the file's end. `--cliloc-skip a,b` drops more by extension. No language left, no enu, or two files for one code (names differing only in case) fail the conversion.

### `text/speech.bin`: speech keywords

Not a record store (the client uses the whole table): `FileHeader` with magic `SPCH` (0x48435053), count u32, blobBytes u32, `SpeechRecord[count]` (8 bytes: id u16, pad u16, end u32), then the UTF-8 blob; entry `i`'s text is `blob[end[i-1] .. end[i])`. The entries are speech.mul's with a non-empty text, in file order.

### Untrusted input (text)

- A store's extents and codecs are checked by `RecordStore.Open`; a store that fails is not used and the MUL files serve instead.
- **Font store topology** (`TextConst.OpenAsciiStore`, `OpenUnicodeStore`): unifont.bin has exactly 5,120 slots; fonts.bin has 1..256. Anything else rejects the file as a whole.
- **Fonts:** an ASCII record must add up exactly (header table plus the texels it declares, flags only bit 0, pad 0) and a glyph without texels must be 0x0; its raw size is at most `224*4 + 224*255*255*2`. A unicode page payload is at most `2048 + 256*16*127` bytes, every glyph's rows must lie inside the payload, and absent glyphs are all zero. A record that fails, or whose read or decode fails, is absent for the session (logged once). Unicode glyph offsets may be anything an sbyte holds; the renderer clips every raster write.
- **Cliloc:** a record's raw size is at most 256 MiB and adds up exactly (`8 + 8*count + blobBytes`), with strictly ascending ids and string ends that never decrease or pass the blob. A language record that fails makes the pack's languages unusable and the Cliloc.* files serve; an overrides record that fails is ignored (logged) and the languages still serve.
- **Speech:** at most 2^20 entries and 64 MiB of text, and the sizes must add up to the file's exact length before anything is allocated; the ends must not decrease or pass the blob. Otherwise the file is not used and speech.mul serves.

## Audio (`audio/`)

Sound effects and music. The client reads audio only from here: `sound.mul`, `soundidx.mul`, `soundLegacyMUL.uop`, `Sound.def`, `Music/Digital/Config.txt` (or `Music/Config.txt`) and the Music MP3 files are the converter's input, and the client does not open them. A pack without `audio/sounds.bin` plays only loose `Sounds/<id>.wav` files, and one without `audio/music.bin` plays no music.

Both stores put the audio first in the payload and the item's name after it (`meta2` bytes, UTF-8), so the audio starts at the payload's first byte. Slot counts are 1..0xFFFF (`AudioConst.MaxIds`), so ids are 0..0xFFFE. Payloads need no alignment.

### `audio/sounds.bin`: sound effects

A record store with magic `SNDS` (0x53444E53).

- **`StoreHeader.flags`** bit 0: converted from `soundLegacyMUL.uop` (else `sound.mul` + `soundidx.mul`). Informational.
- **Slots:** `slotCount` = highest present id + 1. `arg` = 0 and `reserved` = 0; readers leave out a slot with either set. Ids that share an archive entry (`Sound.def` aliases) share one record.
- **Records:** codec 0 only, so playing needs no decode. `meta0` = sample rate, must be 22,050; `meta1` = channels, must be 1; `meta2` = name length, 0..40. Payload: `rawBytes - meta2` bytes of 16-bit signed little-endian PCM, a non-zero even length (whole samples), then the name. The name is the archive entry's 40-byte name field up to its first NUL (for example `sword1.wav`), or a loose file's name without the extension.
- **Converter resolution** (`poc/Uo.Converter/AudioImport.cs`):
  - the archive the client picked: the UOP on UOP installations when it exists, else the MUL pair;
  - `Sound.def` applied as the client applied it: an id that already has data is skipped, each group entry with data replaces it (the last one wins), and `-1` clears it;
  - then the client folder's loose `Sounds/<id>.wav` files over it, through `AudioConst.ReadWaveFile`: at most `MaxWaveFileBytes` (64 MiB + 1 MiB) checked before the read, chunk sizes compared by subtraction, 22,050 Hz mono 16-bit, and a non-empty even data chunk. A loose file that fails any of these falls back to the archive entry and is listed as a warning;
  - an archive entry of 40 bytes or less has no PCM and is absent;
  - an entry whose PCM is not a whole number of 16-bit samples is absent and listed as a warning.
- **Client:** on the first play of an id, two positional reads: the PCM into an array of exactly its length, and the name. The name must not share the PCM's array, because the audio backend checks the whole array's length against the block alignment.
- **Loose `Sounds/<id>.wav` files still win at runtime**, so a shard can try a sound without rebuilding the pack. The converter folds the same files into the pack, so a shipped pack needs none.

### `audio/music.bin`: music

A record store with magic `MUSC` (0x4353554D).

- **`StoreHeader.flags`** bit 0: the client folder had no `Config.txt`, so ClassicUO's built-in table was used. Informational.
- **Slots:** one per music id of the table (`slotCount` = highest id + 1). `arg` bit 0 = the id loops (`Config.txt`'s `loop`); other bits must be 0, and `reserved` must be 0 (readers leave out the slot otherwise). The loop flag belongs to the id, not the file: ids naming one file share its record.
- **Records:** codec 0 only. `meta0` = audio format, 1 = MP3 (the only one a 1.x reader accepts); `meta1` = 0; `meta2` = name length, 0..255. Payload: the MP3 file's bytes exactly, ID3, Xing/Info and LAME frames included, then the file name (for example `Britain1.mp3`). The decoder skips the encoder delay and trims the padding from those frames, so loops are gapless.
- **Converter resolution:**
  - the table the client read: `Music/Digital/Config.txt` for 4.0.11c and later, `Music/Config.txt` before, else the built-in table;
  - each line is `id name[,loop]`: space, comma or tab separated, 2 or 3 fields, and a later line for an id wins;
  - the name resolves to a file as `GetTrueFileName` did (the first `*.mp3` under `Music/` matching `^name.mp3`, ignoring case), played from `Music/Digital/` when that folder exists, else from `Music/`;
  - an id whose file is missing, or whose bytes `Mp3Validator` refuses, is absent (and listed as a warning).
- **Client:** the slot gives the loop flag and the record. On play, the music worker opens a positional stream over the record's MP3 bytes (`RecordStore.OpenRawStream`), and `Mp3Validator` checks them before the decoder opens them.

### Untrusted input (audio)

- `RecordStore.Open` checks the extents and codecs, and `AudioConst.OpenSoundStore` / `OpenMusicStore` limit the slot count to 1..0xFFFF before anything is allocated.
- **Overlap:** the client and the verify step reject a store whose payloads overlap (`AudioConst.CheckNoOverlap`). Ids share content through slots, never through records pointing into each other's bytes. The check sorts the records by offset and keeps the furthest end of all earlier non-empty payloads.
- A store that fails is not used. The failure is logged as an error, and there is no MUL fallback.
- **Records, checked at load** (`AudioConst.CheckSoundRecord`, `CheckMusicRecord`):
  - sounds: codec 0, 22,050 Hz, 1 channel, a name of at most 40 bytes, 2 bytes to 64 MiB of PCM in whole 16-bit samples;
  - music: codec 0, format 1, `meta1` 0, a name of at most 255 bytes, 1 byte to 32 MiB of MP3 (`Mp3Validator.MaxBytes`).
  A record or slot that fails is absent for the session (logged once), and the others still play.
- **Which fields readers check:**

  | field | sounds | music |
  |---|---|---|
  | `StoreHeader.slotCount` | rejected outside 1..0xFFFF (whole store) | same |
  | `StoreHeader.recordCount`, record extents, codec, `storedBytes = rawBytes` | rejected (whole store), as for every record store | same |
  | `StoreHeader.flags`, `StoreHeader.reserved` | ignored | ignored |
  | `StoreSlot.record` | must point into the record table (whole store) | same |
  | `StoreSlot.arg` | must be 0, else the slot is absent | only bit 0 (loop) may be set, else the slot is absent |
  | `StoreSlot.reserved` | must be 0, else the slot is absent | must be 0, else the slot is absent |
  | `StoreRecord.meta0/1/2` and sizes | rate 22,050, 1 channel, name at most 40, PCM 2 B..64 MiB and even, else the record is absent | format 1, `meta1` 0, name at most 255, MP3 1 B..32 MiB, else the record is absent |
  | payload overlap | rejected (whole store) | rejected (whole store) |

  The ignored header fields are left for informational flags a later converter may add. Anything that changes how a payload is read goes in a checked field (a new codec, `meta0` format or slot bit), which a current reader then refuses.
- **Loose WAVs** are untrusted too (`AudioConst.ReadWaveFile`, shared by the client and the converter): the file size is checked before the read, every chunk size is compared by subtraction, and a bad file is logged and skipped, never thrown.
- **MP3 bytes** are untrusted too. `Mp3Validator` (size, ID3v2 tag, a chain of valid Layer III frame headers) runs on every play before the decoder.

## Manifest (`manifest.json`)

camelCase JSON, read case-insensitively (`Uo.Pack.Manifest`):

```json
{
  "version": 1,
  "sourcePath": "<path of the source client folder>",
  "sourceClientVersion": "7.0.103.0",
  "created": "<UTC timestamp>",
  "facets": [ { "index": 0, "width": 7168, "height": 4096, "blocksX": 896, "blocksY": 512, "regionBlocks": 64 } ],
  "files": [ { "path": "art/page_0000.bin", "size": 123, "sha256": "..." } ],
  "bodies": { "policy": 1, "preset": "aos", "forceOldAnimations": false, "additions": "standins", "sequences": "none", "remodels": [], "extras": [], "source": "default", "sha256": "" }
}
```

`files` lists every file except the manifest, with paths relative to the pack root using `/`, sorted ordinally. The sha256 is lowercase hex of the file as stored, so it changes when a file is re-encoded to another kind. A patcher compares hashes to fetch only changed files. `bodies` records the body import choices that `anim/` was built with (see [bodies.md](bodies.md)); it is `null` in packs from before them, and a run that does not rebuild `anim/` keeps the old record.

## Ship profile

`dotnet run -c Release --project poc/Uo.Converter -- --out <pack dir>` with no format flags writes exactly this (`--out` is required; there is no default output folder):

| Content | Pages | Compression |
|---|---|---|
| Art, texmaps | B5G5R5A1, 2048x2048, full mip chain (6 levels) | Kind 2, 1 MiB chunks |
| Animations | Indexed8, 1024x1024, mip0 only, plus `palettes.bin` | Kind 2, 1 MiB chunks |
| Map regions | n/a | Kind 1 |
| Gumps | record store | Kind 0 file; images up to 64 KiB of run-length payload uncompressed, larger ones as texels with Brotli (quality 9) |
| Fonts | record stores | Kind 0 files; ASCII fonts and unicode pages up to 16 KiB raw uncompressed, larger ones Brotli (quality 9) |
| Cliloc | record store | Kind 0 file; language records uncompressed |
| Sounds, music | record stores | Kind 0 files; records uncompressed (raw PCM; the original MP3 bytes) |
| Multimap, string dictionary | record stores | Kind 0 files; records over 16 KiB Brotli (quality 9); skills raw |
| Loose files | n/a | verbatim copies |
| Everything else | n/a | Kind 0 |

- No install-time unpacking is required. A patcher may rewrite any file to Kind 0, since raw is always valid.
- Compression and page flags: `--raw-pages`, `--compress-pages` (Kind 1), `--chunk-bytes n`, `--raw-regions`, `--page-size n`, `--anim-page-size n`, `--raw-gumps`, `--gump-small n`, `--raw-text`, `--text-small n`, `--cliloc-brotli`. Experimental page formats (`--format rgba8|bc7`, `--anim-format page`) exist for measurement and are not part of the format.
- Stages: `--only art,tex,hues,tiledata,data,layers,gumps,fonts,cliloc,speech,sounds,music,skills,multimap,stringdict,loose,maps,anims` (into an existing pack), `--no-maps`, `--no-anims`, `--no-gumps`, `--no-fonts`, `--no-cliloc`, `--no-speech`, `--no-sounds`, `--no-music`, `--verify-only`. `hues` writes `hues_raw.bin` and `radarcol.bin` (and deletes a stale `hues.bin`); `tiledata` writes `tiledata.bin` (and deletes a stale `tiledata_ext.bin`); `data` writes multis, lights, tileart and equipconv; `layers` writes `layerrules.bin`; `fonts` writes `text/fonts.bin` and `text/unifont.bin`; `sounds` and `music` write the audio stores; `skills` writes `skills.bin` and `skillgrp.bin`; `multimap`, `stringdict` and `loose` write the files of their names; `animdata.bin` belongs to `anims`.
- **Outputs are replaced, never written in place.** Every pack file the converter writes goes to a temp file in the same folder (`.<name>.<guid>.replacing.tmp`), which is then moved over the target (`Uo.Pack.ReplacingFile`). Replacing the directory entry breaks a hard link instead of writing through it, so a test pack built by hard-linking a published pack's files and then converting `--only <stages>` leaves the published files untouched. A failed write deletes its temp file and leaves the target as it was; the manifest skips leftover temp files.
- **Published packs are refused as output.** The converter and the pack tools refuse an output folder that is, lies under, or contains a published-packs root (`Uo.Pack.PublishedPacks`). Roots are a built-in default root, the `UO_PACKS_ROOTS` environment variable (`;`-separated), every `--packs-root <dir>` argument, and any ancestor folder holding a `CURRENT` file that names one of its subfolders. Paths are compared in canonical form (symlinks, junctions and drive aliases collapse to one path). When the relationship cannot be established, the write is refused. `--verify-only` only reads, so it may verify a published pack.
- **No recursive deletes.** A full conversion deletes only the files the previous output's `manifest.json` lists (plus crashed-writer temp files beside them and the manifest itself); a non-empty `--out` without a manifest is refused. `--only anims` deletes the `*.bin` files directly in `anim/`.
- **`--verify-only --only hues`** (and a full verify) fails a pack without `hues_raw.bin`. Likewise `--only sounds` and `--only music` (and a full verify, unless converted with `--no-sounds` or `--no-music`) fail a pack without the matching audio store: the client has no other source.

**Verification.** Every conversion ends with a verify step that reads the pack back with the `Uo.Pack` readers and compares against ClassicUO's own loaders:

- **Sampled:** sprites are bit-exact including gutters; Indexed8 frames are bit-exact after the palette lookup.
- **Every record and field, exact:** tiledata (all fields and names, and the draw flags against the converter's rules and a copy of the old client code), hues (raw entries, and the LUT built from them), radarcol, animdata, multis, lights, tileart, equipconv, and `layerrules.bin` against the built-in default.
- **Every gump slot**, exact: present or not, width and height, and the RGBA8 image with the alias hue.
- **Audio, exact:** every sound id and music id (present or not, the bytes, the name and the loop flag); payloads must not overlap.
- **Text, exact:** every ASCII glyph of every font, every unicode glyph of the 20 font slots, every (language, id) cliloc string, and every speech entry.
- **Skills, skill groups, multimap, string dictionary, loose files, exact:** every skill (name, action flag, order) against the frozen SkillsLoader; skillgrp.bin's names and numbers against a frozen copy of the client's skillgrp.mul reads; every multimap slot's bytes against the file the frozen MultiMapLoader opened; every string against the frozen StringDictionaryLoader; every loose file byte for byte. Each also checks that the pack lacks the file when the install does.

## Reference sizes (stock 7.0.x client, ship profile)

Approximate, from one stock install; they vary with the install.

| Content | On disk |
|---|---|
| Art (31 pages + sprites.bin) | about 68 MiB (331 MiB as raw pages) |
| Texmaps (4 pages) | about 16.5 MiB |
| Animations (about 2,650 pages + tables) | about 675 MiB (2.6 GiB as raw pages) |
| Maps (6 facets, 297 regions) | about 57 MiB (350 MiB raw) |
| `tiledata.bin` | about 2.4 MiB |
| `hues_raw.bin`, `radarcol.bin` | about 0.3 MiB and 0.2 MiB |
| Gumps | about 58 MiB |
| Text | about 23 MiB (eight cliloc languages) |
| Audio | about 160 MiB sounds, about 104 MiB music |
| Multimap (rle + 6 facet images) | about 9.1 MiB (30 MiB as files) |
| Skills, skill groups, string dictionary, loose files | about 1 KiB, 0.3 KiB, 0.35 MiB, 0.35 MiB |

Decode cost on one worker (desktop CPU): an anim page about 2.6 ms, an art page about 23 ms (about 4 ms with 8 workers on Kind 2), a region about 2.4 ms.

## Not in the pack (stay on MUL/UOP)

| Content | ClassicUO loader | Notes |
|---|---|---|
| animinfo.mul, sitting tables | `AnimationsLoader` and client tables | runtime fallbacks for mounts and missing groups |
| Loose `Sounds/<id>.wav`, `Clilocs.txt` | `SoundsLoader`, `ClilocLoader` | read from the loose-file folder at load when present (see below) |

`Clilocs.txt` (a shard's own cliloc strings) is folded into the pack's overrides record at conversion; a loose `Clilocs.txt` in the client folder is still read at load and wins over the packed record, so a shard can ship an edit without a new pack.

Picking needs CPU-side alpha. The client decodes pack pages on the CPU (B5G5R5A1 bit 15; Indexed8 index other than 0), and gump records likewise (the picker mask is built from the decoded image at upload). Mask sidecars are not part of the format.

Experimental, not shipped: the `Rgba8` and `Bc7` page formats, and Kind 1 pages (valid, but a page then decodes on one thread only).
