# Pack Format 1.0

The authoritative specification of the asset pack the modern client loads instead of the UO client's MUL/UOP files. The converter (`poc/Uo.Converter`) writes packs, and `poc/Uo.Pack` holds the structs, readers and writers. History, measurements and rejected options are in [pack-format-v0.md](pack-format-v0.md); nothing there is needed to implement this spec.

For every content type described here, the client reads only the pack. MUL/UOP stay only for the content listed in "Not in the pack" at the end. Patch formats (`verdata.mul`, `mapdif*`, `mapdifl*`, `stadif*`, `stadifi*`, `stadifl*`) are never read. Shard owners merge patches into the base files before converting. The `map*x`/`statics*x`/`staidx*x` files (`--xfiles`) are base files and are supported.

## Conventions

- All integers are little-endian. All structs are packed (no padding beyond the explicit `pad` fields) and blittable.
- "Colour" and "16-bit colour" mean UO's 1555 layout: bits 0–4 blue, 5–9 green, 10–14 red, bit 15 alpha (1 = visible) where stated, otherwise as stored by the client.
- Ids index tables directly: a table's entry `i` is id `i`.
- **Canonical 5→8-bit expansion: bit replication, `e(c) = (c << 3) | (c >> 2)`**, for `c` in 0..31.
  - This is what the GPU returns when it samples `B5G5R5A1_UNORM`; measured on D3D12 and Vulkan.
  - Every CPU expansion in the pack and runtime uses it: `Uo.Pack.PackColor`, `PixelExpand`, and the Indexed8 palette lookup in the shader. With it, native 16-bit sampling, the RGBA8 expansion fallback and Indexed8 render **bit-identically**: 0 differing pixels at zoom 1 and 0.5, lit land included.
  - `v >> 3` inverts it exactly.
  - It differs from `round(c·255/31)` only for c = 3, 7, 24, 28 (by 1), and ClassicUO's `HuesHelper` table is `floor(c·255/31)`. Neither is used.
- An RGBA8 value stored as a `u32` has red in the low byte: `r | g << 8 | b << 16 | a << 24`.

## Layout

```
<pack>/
  manifest.json            pack version, source client, facets, files[{path, size, sha256}]
  tiledata.bin             compact tile data for the renderer (TLTD)
  tiledata_ext.bin         full tile data with names (TLDX)
  hues_raw.bin             hues.mul entries: 16-bit colours, TableStart/End, names (HERW); the RGBA8 hue LUT is built from it
  radarcol.bin             radar colours (RADC)
  multis.bin               multi component lists (MLTI)
  lights.bin               light textures (LGHT)
  tileart.bin              paperdoll appearances from tileart.uop (TART)
  equipconv.bin            Equipconv.def (EQCV)
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
  audio/
    sounds.bin             sound effects, raw 22,050 Hz mono 16-bit PCM: a record store (SNDS)
    music.bin              music, the original MP3 bytes and loop flags: a record store (MUSC)
```

`NNNN` is the zero-padded decimal page index (`page_0000.bin`). Page indices are contiguous from 0.

## FileHeader and versioning

Every binary file starts with a 16-byte `FileHeader`:

| Field | Type | Meaning |
|---|---|---|
| magic | u32 | file type (table below), the ASCII tag read as a little-endian u32 |
| version | u16 | **1** for format 1.0 |
| kind | u16 | compression of the rest of the file (see "Compression") |
| reserved | u64 | 0 |

| Magic | File |
|---|---|
| `PPAK` 0x4B415050 | atlas page |
| `SPRS` 0x53525053 | sprite table |
| `UORG` 0x47524F55 | map region |
| `TLTD` 0x44544C54 | tiledata.bin |
| `TLDX` 0x58444C54 | tiledata_ext.bin |
| `HERW` 0x57524548 | hues_raw.bin |
| `RADC` 0x43444152 | radarcol.bin |
| `MLTI` 0x49544C4D | multis.bin |
| `LGHT` 0x5448474C | lights.bin |
| `TART` 0x54524154 | tileart.bin |
| `EQCV` 0x56435145 | equipconv.bin |
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

Retired magic: `HUES` 0x53455548 (`hues.bin`, see "Hues"). Packs that still carry the file load as before; readers ignore it.

**Reader rule.** A reader accepts `version` 0 and 1 and rejects anything higher. Version 0 files come from pre-1.0 packs. Every version-0 file has the same layout as its version-1 counterpart, so no reader branches on the version. What version-0 packs lack:

- the game data tables (`tiledata_ext.bin`, `hues_raw.bin`, `radarcol.bin`, `multis.bin`, `lights.bin`, `tileart.bin`, `equipconv.bin`): a 1.0 client treats a pack without them as incomplete;
- converter-derived `packFlags` in tiledata, which may be 0 (see "NoDraw flags");
- the canonical hue LUT: version-0 `hues.bin` used ClassicUO's `floor` expansion, up to 1/255 darker per channel. `hues.bin` is retired and ignored (the LUT is built from `hues_raw.bin`), so this no longer matters to a reader.

`manifest.json`'s `version` is the pack's format version (1).

## Compression (`FileHeader.kind`)

Compression is chosen per file. Readers must accept any mix of kinds in one pack, and **raw is always valid** for any file: a shard may ship a fully raw pack for binary delta patching, since the manifest's sha256 identifies changed files.

In every kind, the *logical payload* is the bytes a raw file has after its `FileHeader`.

- **Kind 0, raw.** The payload follows the header as is.
- **Kind 1, Brotli.** The payload is one Brotli stream (written at quality 5).
- **Kind 2, chunked Brotli.** The payload is split into fixed-size chunks that compress independently, so several workers can decode one file and a reader can decode chunk 0 alone to get the leading headers:

  | Field | Type | Meaning |
  |---|---|---|
  | chunkBytes | u32 | uncompressed chunk size (1 MiB = 1,048,576 in the ship profile); the last chunk may be shorter |
  | logicalBytes | u64 | uncompressed payload size |
  | chunkCount | u32 | `ceil(logicalBytes / chunkBytes)` |
  | compressedSize | u32[chunkCount] | compressed size of each chunk; chunk offsets are prefix sums from the end of this table |
  | chunks | bytes | chunk `i` is an independent Brotli stream (quality 5, window 22) that decodes to logical bytes `[i·chunkBytes, (i+1)·chunkBytes)` |

  Decode each chunk straight into the destination buffer at its logical offset. Chunk 0 always contains the `PageHeader` (and for regions the `RegionHeader` and offset table). `Uo.Pack` provides `ChunkedBrotli.Decode` (single thread, in memory), `ChunkedBrotliStream` (sequential) and `ChunkedBrotliDecoder` (persistent worker pool, used by `AtlasPage.Read(path, span, decoder)`).

Which files use which kind is a profile choice (see "Ship profile"). Only pages and regions are compressed as whole files; all other files are Kind 0. Record stores (gumps, fonts, cliloc, sounds, music) are Kind 0 files whose records are compressed one by one (see "Record stores").

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

The mip chain follows: level 0 (side `s`), level 1 (`s/2`), and so on, back to back. A full chain ends at 64×64 (6 levels for 2048, 5 for 1024).

### Pixel formats

| Value | Name | Status | Texel |
|---|---|---|---|
| 2 | `B5G5R5A1` | **ship** (art, texmaps) | u16 colour; level of side `s` is `2·s²` bytes |
| 3 | `Indexed8` | **ship** (animations) | u8 palette index; level of side `s` is `s²` bytes |
| 0 | `Rgba8` | experimental | see the appendix |
| 1 | `Bc7` | experimental | see the appendix |

**B5G5R5A1.** Bits 0–4 B, 5–9 G, 10–14 R, 15 A: this is `SDL_GPU_TEXTUREFORMAT_B5G5R5A1_UNORM`, Vulkan's `A1R5G5B5_UNORM_PACK16`, Metal's `BGR5A1Unorm` (Apple GPUs) and D3D12's `B5G5R5A1_UNORM`, all with the same bit layout. mip0 is bit-exact to the client data: each visible texel is the source colour with A = 1, and transparent texels are 0. Lower mips are an alpha-aware box filter of the expanded RGBA8 image, each channel rounded back to 5 bits with `(v·31 + 127) / 255`, and A = 1 when the filtered alpha is ≥ 128. A GPU that can't sample 16-bit textures (WebGPU, Intel Macs) gets the page expanded to RGBA8 with the canonical expansion at upload time; this is a runtime fallback, not a pack format.

**Indexed8.** One byte per texel, an index into the palette of the frame that covers the texel (see "palettes.bin"). Index 0 is transparent. Pages are mip0 only (`mipCount` = 1); the shader filters by hand at mip0 (ClassicUO's minimum zoom is 0.5). The renderer samples an `R8_UINT` array and reads palettes from a storage buffer; see "Indexed8 rendering" below.

### Packing rules

- Every sprite has a **2-texel gutter** on all sides filled by extruding its edge texels (colours for B5G5R5A1, indices for Indexed8). A bilinear footprint at mip0 never leaves the sprite's cell, so for Indexed8 all four taps use the sprite's own palette.
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
| flags | u16 | bit 0 land art, bit 1 texmap, bit 2 has partial-hue pixels (the tile's `PartialHue` flag) |

- The art table has `0x4000 + staticCount` entries: land ids first, then statics at `0x4000 + graphic`. `art.def` and `TexTerr.def` substitutions are already applied.
- An id that exists in the client but is fully transparent is absent and keeps `origW/origH`.
- Pixels are stored unhued. Hues are applied in the shader (see "Hues").

## Map

### `map/<facet>/facet.json`

`{ "index", "width", "height", "blocksX", "blocksY", "regionBlocks" }`: facet size in cells and in 8×8-cell blocks, and the region side in blocks (64). Only facets with a map file on disk are converted.

### Regions (`r_<rx>_<ry>.bin`)

A region holds up to 64×64 blocks (512×512 cells). Edge regions are smaller.

Logical payload:

1. `RegionHeader` (12 bytes): facet, rx, ry, blocksW, blocksH, reserved (all u16).
2. `u32 blockOffset[blocksW·blocksH]`, row-major (`by·blocksW + bx`). Each is a byte offset **from the start of the logical file, including the 16-byte FileHeader**; 0 = empty block.
3. Block records:
   - `LandCell[64]`, row-major `y·8 + x`: tileId u16 (`& 0x3FFF` applied), z i8, pad u8;
   - staticCount u16, pad u16;
   - `StaticCell[staticCount]`: graphic u16, x u8, y u8 (0..7), z i8, pad u8, hue u16.

Statics with graphic 0 or 0xFFFF, or x/y > 7, are dropped (as ClassicUO's `Chunk.Load` does). For Kind 1 and 2, the simplest reader rebuilds the logical file (header plus decoded payload) and parses it as Kind 0 (`RegionFile.Parse`, `RegionFile.Decompress`).

The data is semantic: the renderer bakes per-chunk instance buffers on worker threads, and walkability/collision stay available for client-side prediction. An UltimaLive update replaces one block record in memory and re-bakes that chunk.

## Tile data

### `tiledata.bin`: compact, for the renderer

`FileHeader`, `landCount i32`, `staticCount i32`, `LandTileData[landCount]`, `StaticTileData[staticCount]`.

- `LandTileData` (12 bytes): flags u64, texId u16, packFlags u16.
- `StaticTileData` (12 bytes): flags u64, height u8, layer u8, packFlags u16.

Flags keep UO's `TileFlag` bit values (64-bit; pre-7.0.9.0 clients' 32-bit flags are widened). Values are after `art.def` substitution.

### `tiledata_ext.bin`: everything else

`FileHeader`, `landCount i32`, `staticCount i32`, `nameBytes i32`, `LandTileInfo[landCount]`, `StaticTileInfo[staticCount]`, then `nameBytes` of UTF-8 name table. Entries are parallel to `tiledata.bin`.

`LandTileInfo` (16 bytes): flags u64, nameOffset u32, nameLength u16, texId u16.

`StaticTileInfo` (28 bytes):

| Field | Type | Meaning |
|---|---|---|
| flags | u64 | `TileFlag` |
| count | i32 | quantity (ClassicUO `StaticTiles.Count`) |
| nameOffset, nameLength | u32, u16 | name in the name table |
| animId | u16 | animation body for wearables / animated items |
| hue | u16 | |
| lightIndex | u16 | light id for light sources (see `lights.bin`) |
| weight | u8 | |
| layer | u8 | equipment layer |
| height | u8 | |
| pad | u8 | |

A name is the UTF-8 encoding of ClassicUO's string: the 20-byte field decoded as UTF-8 with leading and trailing NULs trimmed. Length 0 = empty name. Duplicate names share bytes in the table.

### NoDraw flags (`TilePackFlags`, in `packFlags`)

ClassicUO hides some tiles in code rather than with tiledata flags. The converter evaluates that code once:

| Bit | Name | Meaning |
|---|---|---|
| 0 | `NoDraw` | ClassicUO never draws the tile. |
| 1 | `NoDrawUnlessGargoyle` | Static hidden unless the player is a gargoyle. |
| 2 | `NameNoDraw` | The name starts with `nodraw` (case-insensitive). Informational for land. |

- **Land** (`Land.AllowedToDraw`, graphic > 2): `NoDraw` for ids 0, 1 and 2. Season graphic swaps happen at runtime, so the renderer applies the `> 2` rule to the swapped graphic.
- **Statics** (static-data part of `GameObject.CanBeDrawn`, in this order):
  1. 0x0001, 0x21BC, 0xA1FE..0xA201: `NoDraw`.
  2. 0x9E4C, 0x9E64, 0x9E65, 0x9E7D: `NoDraw` only with `Background` or `Surface`.
  3. 0x63D3 and 0x2198..0x21A4: `NoDraw`.
  4. 0x0F65 (easel): drawn on clients older than 6.0.14.4.
  5. A name starting with `nodraw`: `NoDraw`.
  6. `NoDiagonal` (0x02000000): `NoDraw`, or `NoDrawUnlessGargoyle` with `Animation`.
- The flags apply to map statics and to items or multis that ClassicUO passes through `CanBeDrawn`. Multi components use their own `> 2` test.
- Version-0 packs may have 0 here, so a renderer that reads them keeps its own id rules as a fallback.

## Hues

`hues_raw.bin` is the pack's only hue file. The renderer's RGBA8 LUT is built from it at load.

### `hues_raw.bin`: hues.mul, exactly

`FileHeader`, `count i32`, `HueEntry[count]`. `HueEntry` (88 bytes) is byte-identical to ClassicUO's `HuesBlock`: colours u16[32], tableStart u16, tableEnd u16, name u8[20] (raw bytes, NUL-padded). Entry `i` is hue `i + 1`. This serves CPU font and gump hueing (`GetColor16`, `GetUnicodeFontColor`, and similar) and is the source of the renderer's LUT. The 4-byte group headers of hues.mul are unused by ClassicUO and dropped.

Readers treat the file as untrusted: `count` must be 0..65536 (hue ids are u16) and the entries must fit in the file.

### The RGBA8 LUT (built at load, not stored)

`count × 32` RGBA8 `u32`. Row `i` is hue `i + 1` (UO hue 0 = unhued), column `s` is shade 0..31. Texel `(i, s)` is the canonical expansion of entry `i`'s colour `s`: `e(r) | e(g) << 8 | e(b) << 16 | 0xFF << 24`, bit 15 ignored. `Uo.Pack.HueLut.Build` is the one implementation; the client (`HuesLoader`) and the poc renderer (`PackSet`) both call it. Building 3,000 hues takes about 0.15–0.2 ms (median; 0.3 ms with reading the file).

Shader hueing follows ClassicUO: the shade index is the texel's 5-bit red channel. A partial-hue sprite hues only exactly gray texels (`r5 == g5 == b5`, compared as 5-bit integers, no tolerance).

**Retired: `hues.bin`** (magic `HUES` 0x53455548; `count i32`, then the LUT above). Earlier packs stored the LUT as a file. It duplicated `hues_raw.bin`, and the owner decided (2026-10-31) on one source of truth per pack file: tools will be built around the pack files, and keeping two files in sync is error-prone. Space was not the reason. Packs made before the change (`classic-1.0`) still carry `hues.bin`; readers ignore it and the converter no longer writes it (an `--only hues` run deletes a stale copy from its output). The built LUT equals `classic-1.0`'s `hues.bin` byte for byte (test `HueLutTests`), so the change is pixel-neutral. Version-0 `hues.bin` files used ClassicUO's `floor` expansion; that difference is gone too, because the LUT is always built with the canonical expansion.

### `radarcol.bin`

`FileHeader`, `count i32`, `u16 colour[count]`: radarcol.mul as is. Index = land id (0..0x3FFF) or `0x4000 +` static graphic, as `HuesLoader.GetRadarColorData` indexes it. Used by the minimap and world map.

## Animations (`anim/`)

Mobiles, equipment and corpses. Frames are resolved at conversion time exactly as ClassicUO draws them, so the client needs no anim .def parsing for frame lookup.

### `anim.bin`

`FileHeader`, `bodyCount i32`, `dirCount i32`, `frameCount i32`, then `AnimBodyEntry[bodyCount]` (index = body id), `AnimDirEntry[dirCount]`, `AnimFrameEntry[frameCount]`. Reader: `AnimTableFile.Read`; `AnimTableFile.Frames(…, body, action, dir)` looks up one direction.

`AnimBodyEntry` (24 bytes):

| Field | Type | Meaning |
|---|---|---|
| firstDir | u32 | record of (action 0, dir 0); record (a, d) is at `firstDir + a·8 + d` |
| actionCount | u16 | 0 = no frames of its own; actions ≥ actionCount have none |
| type | u8 | ClassicUO `AnimationGroupsType`: 0 monster, 1 sea monster, 2 animal, 3 human, 4 equipment |
| source | u8 | bit 7 = `AnimationFrame<n+1>.uop`, else `anim<n+1>.mul` (n = low bits); 0xFF = none |
| flags | u32 | mobtypes.txt flags (ClassicUO `AnimationFlags`); bit 31 = listed in mobtypes |
| sourceBody | u16 | body id inside the source file (the Bodyconv.def target), else the body |
| aliasBody, aliasHue | u16, u16 | Body.def replacement, already chained (0xFFFF = none): draw aliasBody's entry, type and flags with aliasHue |
| corpseBody, corpseHue | u16, u16 | Corpse.def replacement for corpses (0xFFFF = none) |
| mountHeight | i8 | Bodyconv.def mounted height offset |
| pad | u8 | |

`AnimDirEntry` (8 bytes): firstFrame u32, frameCount u16 (empty frames included; 0 = no animation), storedDir u8, flags u8 (bit 0 = mirrored). There are 8 records per action, one per UO direction 0..7 (N, NE, E, SE, S, SW, W, NW). The client stores 5 directions: directions 3..7 are stored 0..4, and directions 2, 1, 0 reuse stored 1, 2, 3 mirrored (ClassicUO `GetAnimDirection`). Mirrored records point at the same frames.

`AnimFrameEntry` (24 bytes): a `SpriteEntry` (page, rect, trim; origW/origH = the frame's size; page 0xFFFF = empty frame), then centerX i16, centerY i16.

**Anchoring** (ClassicUO `MobileView`): from the mobile's tile screen position (x, y), with z applied as for statics, the anchor is `ax = x + 22`, `ay = y + 22 − 3`.
- Unmirrored: the frame's top-left is `(ax − centerX, ay − (origH + centerY))`; the trimmed rect is at `+ (trimX, trimY)`.
- Mirrored: the frame's left is `ax − (origW − centerX)`; draw the trimmed rect flipped horizontally at x offset `origW − trimX − w`, same y.

**Resolution done by the converter** (as ClassicUO's `AnimationsLoader.GetIndices` and `Animations.GetAnimationFrames`):
- Bodies 0..max(2047, highest id in mobtypes.txt and Bodyconv/Body/Corpse.def) are scanned.
- mobtypes.txt gives type and flags; flag 0x10000 means frames come from `AnimationFrame*.uop`.
- AnimationSequence.uop group replacement is applied: UOP action `a` holds what ClassicUO draws for `a`. The renderer must not remap UOP actions.
- Bodyconv.def is applied with every column enabled (all expansion flags, as a modern server sends).
- Body.def and Corpse.def stay as aliases and apply only to plain anim.mul bodies.
- Frame counts: MUL directions carry their own; for UOP, a group's frames split into `round(n / 5)` per direction (minimum 10 for equipment), with gaps filled by empty frames.
- Equipment uses the item's animId (tiledata) as the body. ClassicUO draws it only when that body's type is equipment or human, checked at runtime.

Frames are packed body → action → direction → frame, so one body's frames share few pages.

### `palettes.bin`

`FileHeader`, `paletteCount i32`, `frameCount i32`, `u32 framePalette[frameCount]` (palette id per `AnimFrameEntry`; `0xFFFFFFFF` = empty frame), then `u16 palettes[paletteCount × 256]` as B5G5R5A1.

- Entry 0 of every palette is 0 (transparent). The rest are the source palette's distinct visible colours that some frame drawn with it uses, in order of first appearance, with A = 1.
- Frames with identical texels share one rect and one palette. The table is small enough (≈18.5 MiB for 7.0.103) to keep resident as one storage buffer.

**Indexed8 rendering.**
- The CPU writes the frame's palette id into the instance when it fills the instance from the `AnimFrameEntry`.
- The shader computes `t = uv·S − 0.5`, `i = floor(t)`, `f = frac(t)`, does 4 index loads and 4 palette loads, and treats index 0 as transparent.
- Hue applies per tap, after the palette lookup and before blending: the partial-hue gray test is on the tap's 5-bit integers, and the shade index is `r5`.
- Then blend the visible taps (`rgb = Σwᵢ·rgbᵢ / Σwᵢ`, `a = Σwᵢ`), alpha-test at 0.5 and apply light.

### `animdata.bin`

`FileHeader`, `count i32`, `AnimDataEntry[count]` sorted by graphic. `AnimDataEntry` (70 bytes): graphic u16, frameCount u8, frameInterval u8, frameStart u8, unknown u8, frames i8[64] (graphic offsets: frame `k` draws `graphic + frames[k]`).

Only graphics whose animdata.mul record has `frameCount > 0` are listed; other records have no effect in ClassicUO's `AnimatedStaticsManager`. That manager animates every static that has the `Animation` tile flag and a record. It cycles `frames[0..frameCount)`, writes the current offset into the art's `AnimOffset`, and advances one frame every `frameInterval · 2 · ITEM_EFFECT_ANIMATION_DELAY` ms (`2 · ITEM_EFFECT_ANIMATION_DELAY` when `frameInterval` is 0). `frameStart` and `unknown` are stored but unused by ClassicUO.

## Game data tables

### `multis.bin`

`FileHeader`, `multiCount i32`, `componentCount i32`, `MultiEntry[multiCount]` (index = multi id), `MultiComponent[componentCount]`.

- `MultiEntry` (8 bytes): firstComponent u32, componentCount u32 (0 = no such multi).
- `MultiComponent` (10 bytes): graphic u16, x i16, y i16, z i16, flags u16 (bit 0 = visible).
- Source and semantics mirror `MultiLoader.GetMultis`. On UOP installations the source is `MultiCollection.uop`, and a component is visible when its flags are 0 or 0x100; otherwise multi.idx/mul, where any non-zero flags means visible. `multiCount` is the highest present id + 1.

### `lights.bin`

`FileHeader`, `count i32`, `dataBytes i32`, `LightEntry[count]` (index = light id, the tiledata `lightIndex`), then `dataBytes` of texels.

- `LightEntry` (8 bytes): offset u32 (into the texel block), width u16, height u16 (0×0 = no such light).
- Texels are light.mul bytes, one per texel, row-major. Values 0..31 are intensities; values above 31 are negative lights.
- ClassicUO folds them to `i = ~v & 31` and draws gray `i << 3` with alpha 0xFF, or transparent when `i = 0` (`LightFile.ToRgba8`).
- A small raw file keeps lights out of the atlas machinery; they are uploaded once as individual textures.

### `tileart.bin`

`FileHeader`, `count i32`, `TileArtAppearance[count]` sorted by (graphic, body). `TileArtAppearance` (8 bytes): graphic u16 (item), body u16 (mobile body), appearance u32.

This is the only part of tileart.uop ClassicUO uses: `PaperDollInteractable` asks `TileArtInfo.TryGetAppearance(body)` and, if the gump `MALE_GUMP_OFFSET + appearance` exists, draws it as the paperdoll gump. Only items whose tileart record has more than one appearance subtype are listed, with subtype 0's map. 7.0.103 has 20 such items and 84 rows.

### `equipconv.bin`

`FileHeader`, `count i32`, `EquipConvEntry[count]` sorted by (body, graphic), unique. `EquipConvEntry` (10 bytes): body u16, graphic u16 (item animId), newGraphic u16, gump u16, color u16.

The rows are Equipconv.def as ClassicUO resolves it in `ProcessEquipConvDef` (clients ≥ 3.0.0 only):
- gump 0 means `graphic`, and gump 0xFFFF or -1 means `newGraphic`;
- rows with gump > 0xFFFF are dropped;
- later rows replace earlier ones.

At runtime, an equipped item on body `body` with animId `graphic` is drawn as `newGraphic`, and the paperdoll uses `gump`.

## Record stores

Content that the client reads one item at a time and that is not sampled by the GPU from pack pages goes into a record store: one Kind 0 file per content type, read by offset through one file handle kept open. Gumps were the first; ASCII fonts (a record per font), unicode fonts (a record per 256-character page) and cliloc (a record per language, plus the shard's overrides) use it too, with their own magic, slot argument and record meta (see "Text"), and so do sound effects and music (see "Audio"). The other "Not in the pack" types are meant to follow. Reader and writer: `Uo.Pack.RecordStore`, `RecordStoreWriter`.

| Part | Layout |
|---|---|
| `FileHeader` | magic = the content type, version 1, kind 0 |
| `StoreHeader` (16 bytes) | slotCount u32, recordCount u32, flags u32 (type-specific), reserved u32 |
| `StoreSlot[slotCount]` (8 bytes each) | record u32 (record index + 1; 0 = no such id), arg u16 (type-specific), reserved u16 |
| `StoreRecord[recordCount]` (24 bytes each) | offset u64 (from the start of the file), storedBytes u32, rawBytes u32, codec u16, meta0, meta1, meta2 u16 (type-specific) |
| payloads | back to back, each decodable on its own |

- Slot `i` is id `i`. Several ids may share one record (aliases, identical content).
- Codec 0: the payload as is (`storedBytes = rawBytes`). Codec 1: one Brotli stream (window 22) that decodes to `rawBytes` bytes. Readers reject other codecs.
- A reader reads the header, the slot table and the record table once (one read each). A pack is shipped by shards, so the reader treats it as untrusted:
  - before allocating the tables it checks that they fit in the file (`slotCount ≤ 2²⁴`, `recordCount ≤ slotCount`, the tables end at or before the file's end);
  - every slot points into the record table;
  - every payload lies in the payload area: `payloadStart ≤ offset ≤ fileLength` and `storedBytes ≤ fileLength − offset` (no wrap-around);
  - codec 0 has `storedBytes = rawBytes`; other codecs are rejected.
  A failure rejects the whole file. A content type whose slot count is fixed (fonts) passes its bounds to `Open`, which checks them before allocating the tables. The content type then checks its own meta (for gumps, see below). A record is one positional read plus its decode, from any thread, into the caller's buffer.
- No whole-file compression and no per-record files: a store is read by offset (the per-open cost of a file is 20–28 µs, L7), and a record decodes alone.

## Gumps (`gumps/gumps.bin`)

A record store with magic `GUMP` holding every gump id the client's `GumpsLoader` resolves, with `gump.def` and loose `Gumps/*.gump` files already applied. The client reads gumps only from here when the file exists (`GumpsLoader.FromPack`); without it, it reads gumpart MUL/UOP as before.

- **`StoreHeader.flags`** bit 0: converted from `gumpartLegacyMUL.uop` (ClassicUO's `UseUOPGumps`; the status gump's layout depends on it).
- **Slots:** `slotCount` = highest present id + 1 (69,986 for 7.0.103, whose UOP has gumps above 0xFFFF). `arg` = the gump.def alias hue (0 = none).
- **Records:** `meta0` = width, `meta1` = height, `meta2` = payload layout. The record table is the size table: width and height of any gump without decoding it.
- **Payload layouts** (`Uo.Pack.GumpCodec`), both lossless 16-bit:
  - 0, texels: `width·height` B5G5R5A1 `u16`, row-major. Texel 0 = transparent, else bit 15 set and bits 0–14 the source colour. Used with codec 1.
  - 1, run-length rows: `u32 rowStart[height]` (in 4-byte units from the start of this table), then `(u16 texel, u16 run)` pairs. Row `y`'s runs end where row `y+1`'s start (the last row's at the end of the payload); texels no run reaches are transparent. This is the client's own entry layout, re-encoded canonically (a run per change of texel, no trailing transparent run). Used with codec 0.
  - Any (layout, codec) pair is valid for a reader. Ship profile: an image whose run-length payload is at most 64 KiB (`--gump-small n`) is stored as layout 1, codec 0; every other image as layout 0, codec 1. `--raw-gumps` stores every image as layout 1, codec 0.
- **Lossless:** the stored texel is the source value's colour bits plus bit 15 = visible. ClassicUO's decode ignores bit 15 (`Color16To32`, `GetColor16`), so every image the client draws is reproduced exactly; hueing needs the exact 5-bit gray texels, which no lossy format keeps (see BC7 in the appendix).
- **Drawing a gump** (ClassicUO `GumpsLoader.GetGump`): each visible texel `v` becomes RGBA8 with ClassicUO's 5→8-bit table (`HuesHelper.Color16To32`, not the canonical expansion: gumps go through the CPU and the batcher's RGBA8 atlas, and parity is with the MUL path) and alpha 255. With a slot hue `h`, `1 ≤ h < hue count`, first `v = hues[h − 1].colors[(v >> 10) & 31]`, and the texel stays visible only if that is not 0 (`GetColor16`).
- **Converter resolution:** ids whose index entries are identical (gump.def copies the entry and sets a hue; UOP/MUL may alias too) share one record, and so do ids with identical decoded images. An id with a loose `Gumps/<id>.gump` file gets its own record from that file, with no hue, as the client's `ReadOurs`; a broken file falls back to the archive entry with its hue, as the client does. The client no longer reads loose gump files when the pack has gumps; re-run the converter (`--only gumps`, about 6 s) after changing them.

**Record checks at load** (`GumpsLoader.CheckRecord`): width and height 1..4096 (the gump atlas); layout 0 with `rawBytes = width·height·2` exactly; layout 1 with `rawBytes` a multiple of 4, at least the row table (`4·height`) and at most `4·height + 4·width·height`; a Brotli record no larger than Brotli's bound for `rawBytes`. A record that fails, or whose payload later fails to decode (corrupt Brotli, malformed rows, I/O error), is invalid for the session: its ids are absent (`Exists`, `TryGetSize`, `GetGump`), the decode error is caught at the loader and logged once per record, and nothing retries it. `TextureAtlas.AddSprite` rejects a rect that can't fit an empty page.

**Client** (`GumpsLoader`, `Renderer.Gumps.Gump`):
- **Sizes and existence are synchronous and need no decode:** `TryGetSize`, `Exists` and `PixelCount` read the record table. Gump controls keep taking their size from `GetGump` at construction, so `GetGump` stays synchronous; existence checks that only asked `GetGump(id).Texture != null` (the paperdoll's gender fallback and tileart appearance probes) now use `Exists`.
- **Pixels:** a first `GetGump` decodes one record into a pooled buffer (no copies: the texel layout decodes in place) and uploads it. Decodes are thread-safe, so a window built from many gumps decodes them on workers in one batch first (`Gump.Prefetch`: server gump layouts' gumppic, button, checkbox, radio, gumppictiled and resizepic pieces; the paperdoll's body and equipment); the main thread then waits for the slowest decode instead of running each in turn. A batch holds at most 64 gumps and 64 MiB of decoded pixels, and a 20 ms deadline bounds queueing and waiting (one atlas upload in progress can overrun it); after a timeout a `GetGump` takes a finished worker result before decoding again. `Gump.Request` plus `DrainReady` (1 ms per frame, before `UIManager.Update`) serve ahead-of-time requests. `CUO_GUMP_SYNC=1` turns the worker paths off.
- **Hit-testing:** the picker mask is built from the decoded RGBA image at upload (`PixelPicker.SetTrimmed` over the whole image, the same mask as `Set`), so click-through on transparent texels is unchanged.
- `CUO_PACK_GUMPS=0` reads gumpart MUL/UOP even when the pack has gumps.

**Why per-gump records and not atlas pages** (`--gump-report`, 7.0.103, 4,438 gumps):

| Candidate | Size | Decode of a 670×670 gump (µs, JIT, median) |
|---|---|---|
| Client MUL/UOP entries (reference) | 136.9 MiB | 1,250–1,740 (the client's own RLE decode) |
| Atlas pages, 2048², no mips | 289 MiB raw (36 pages) | whole 1 MiB chunks: a 600-row gump touches 3 (2.4 MB) |
| Raw texels + Brotli (ship, images over 64 KiB) | 37.9 MiB | 1,800–2,150 (Brotli 1,750–2,000, expand 90) |
| Raw texels + zlib | 39.2 MiB | 1,220–1,990 |
| RLE + Brotli | 41.2 MiB | 2,650–3,250 |
| RLE uncompressed (ship for small images; all with `--raw-gumps`) | 133 MiB | 880–960 |

- Gumps are drawn through the CPU into the batcher's own RGBA8 atlas; nothing samples pack pages, so pages buy no GPU residency and cost gutters, chunk-granular reads (neighbours decode with every gump) and 2× the raw bytes.
- 66% of gump texels are transparent and the images are large (up to 781×800), so per-record compression does well: 280 MiB of raw texels → 37.3 MiB.
- Raw texels + Brotli is the smallest; it decodes without allocation (`BrotliDecoder.TryDecompress`) and straight into the pixel buffer: the 16-bit texels land in its upper half and expand in place, front to back. zlib decodes 30–40% faster but needs a stream object per decode (no span API in .NET 10). `--raw-gumps` gives the fastest decode at the source's size.
- A Brotli decode has a fixed cost that dominates small images, so small ones are stored run-length and uncompressed (every gump, best of 3, JIT):

  | Run-length payload | Gumps | RLE MiB | Texels + Brotli MiB | µs, texels + Brotli | µs, RLE |
  |---|---|---|---|---|---|
  | ≤ 1 KiB | 496 | 0.2 | 0.1 | 7.7 | 0.6 |
  | ≤ 4 KiB | 1,640 | 3.8 | 1.2 | 69.4 | 3.3 |
  | ≤ 16 KiB | 1,436 | 10.5 | 3.3 | 53.4 | 6.5 |
  | ≤ 64 KiB | 470 | 15.8 | 4.7 | 110.3 | 32.6 |
  | larger | 380 | 104.9 | 28.6 | 595.1 | 275.6 |

  The 64 KiB threshold stores 3,915 of the 4,290 records this way (37.9 → 58.2 MiB) and makes them decode in under 35 µs; 16 KiB would give 3,459 records and 47.5 MiB, but in the client the journal, status and spellbook opens were then 0.1–0.7 ms slower than the MUL path, and with 64 KiB they match it (poc-log "Gumps in the pack"). The 375 larger ones keep Brotli:
- Sizes for the 4,438 gumps: 4,290 records after sharing (16 gump.def/index aliases, 132 identical images), 58.2 MiB file in the ship profile, of which the tables are 0.6 MiB.

## Text: fonts, cliloc, speech (`text/`)

ASCII fonts (fonts.mul), unicode fonts (unifont*.mul), cliloc in every language the client folder has (Cliloc.*), and speech.mul's keyword table. The client reads each from here when its file exists (`FontsLoader.AsciiFromPack` / `UnicodeFromPack`, `ClilocLoader.FromPack`, `SpeechesLoader.FromPack`); without it, or when the file is invalid as a whole, it reads the MUL files as before and says so in the log. `CUO_PACK_TEXT=0` reads the MUL files even when the pack has text.

Every file is converted through a frozen copy of the client's loader (tools/ClassicUO.Assets.Mul: `FontsLoader`, `ClilocLoader`, `SpeechesLoader`), so the pack holds what the client resolved, quirks included: a unicode glyph whose width or height isn't positive is no glyph, an ASCII glyph past the end of fonts.mul has no texel array, unifont1 is unifont when unifont1.mul is missing, a cliloc text stops at its first NUL. One exception: a unicode glyph whose data runs past the end of its file (U+FFE0 in unifont3..12 of 7.0.103) made the client throw when drawn; the converter stores no glyph for it and lists it, and the client's MUL path treats it the same way. Cliloc languages are the extensions of the folder's `Cliloc.*` files found by content (see "Converter discovery" below); the folder's `Clilocs.txt` becomes the overrides record.

### `text/fonts.bin`: ASCII fonts

A record store with magic `FNTA` (0x41544E46).
- **Slots:** one per font; `slotCount` = the client's `FontCount` (10 for 7.0.103).
- **Records** (`Uo.Pack.AsciiFontCodec`): `meta0` = 224. Raw payload: `AsciiGlyphHeader[224]` (4 bytes: width u8, height u8, flags u8, pad u8; flag bit 0 = the glyph has a texel array), then for each glyph with bit 0, `width·height` u16 texels (fonts.mul's colours as stored), glyph after glyph. The header table is the size table. Glyph `i` is character `i + 32`; the client maps 0..31 to glyph 0.
- **Client:** a font's 224 glyphs are read and decoded on the font's first use (one positional read; `FontsLoader.AsciiRow`), instead of all fonts at startup.

### `text/unifont.bin`: unicode fonts

A record store with magic `FNTU` (0x55544E46).
- **`StoreHeader.flags`:** bit `i` = unicode font `i` exists (`UnicodeFontExists`), after the client's own rule that font 1 is font 0 when unifont1.mul is missing.
- **Slots:** `20 × 256`: slot `font·256 + page` holds characters `page·256 .. page·256 + 255` of the font. A page without glyphs has no record. Identical pages share one record (font 1's pages are font 0's when it is an alias).
- **Records** (`Uo.Pack.UnicodePageCodec`): `meta0` = font, `meta1` = page, `meta2` = glyphs in the page. Raw payload: `UnicodeGlyphHeader[256]` (8 bytes: offsetX, offsetY, width, height as i8, then dataOffset u32), then the glyphs' rows back to back. A glyph's rows are `height` rows of `(width − 1) / 8 + 1` bytes, 1 bit per pixel, most significant bit first: unifont's own layout, which the client draws from directly. `dataOffset` is from the end of the header table; `0xFFFFFFFF` = no glyph, with every other field 0. A present glyph has width and height > 0.
- **Sizes without decoding the glyphs:** the header table is the first 2 KiB of the raw payload and gives every glyph's offsets and size; with codec 0 it is the record's first 2 KiB on disk.
- **Client** (`FontsLoader.GetCharUni`): glyphs are held per font and per page (`font → 256 pages → 256 glyphs`), and a page is read on first use of any of its characters: one positional read, plus a Brotli decode for large pages. The `[20, 0x10000]` glyph table the client allocated at startup (21 MB) is gone. With MUL files, a page's 256 glyphs are read from the unifont file on first use the same way, and a missing glyph is no longer re-read on every lookup.

### `text/cliloc.bin`: cliloc

A record store with magic `CLIL` (0x4C494C43): one record per language, and at most one record of the shard's own strings (overrides).
- **Record meta:** `meta2` low byte = the record's kind: 0 = a language, 1 = overrides (the shard's own strings). Other `meta2` bits are invalid. Bit 8 was a block-compressed layout, measured and dropped before shipping (owner, 2026-10-31; poc-log "Text in the pack: audit fixes, overrides, block-compressed cliloc"): a store with a record that sets it is rejected as unsupported (logged as such), never read as flat. A language record holds its code in `meta0`/`meta1`: up to 4 lowercase ASCII letters or digits, one per byte, NUL-padded (`ClilocTable.PackLanguage`), from the file extension (`Cliloc.deu` → `deu`). An overrides record has `meta0 = meta1 = 0`.
- **Slots:** one per record, in the order converted: the languages (enu first), then the overrides record when there is one. 7.0.103 has `enu`, `chs`, `cht`, `deu`, `esp`, `fra`, `jpn`, `kor`.
- **Resolution** (`ClilocStore.Resolve`, shared by the client and the converter's verification): the slots are walked in order; a record no slot reaches is ignored. The store is unusable as a whole (the Cliloc.* files serve) when a record is reached by two slots, a `meta2` kind or bit is unknown (bit 8, the retired blocked layout, included), a language record has no valid code, a language code appears twice, an overrides record has a code, or there are two overrides records.
- **Records** (`Uo.Pack.ClilocTable`), one language file alone, with later duplicates resolved as the client resolves them (the last wins). Payload:

  | Part | Layout |
  |---|---|
  | header | count u32, blobBytes u32 |
  | ids | i32[count], strictly ascending (binary search) |
  | ends | u32[count]; string `i` is `blob[ends[i−1] .. ends[i])` (from 0 for `i` = 0) |
  | blob | UTF-8, the strings back to back in id order |

  A string is the UTF-8 encoding of the client's decoded text (`ReadUTF8(length)`: cut at the first NUL, invalid bytes as U+FFFD), so decoding it gives the same string. Ship profile: codec 0 (`--cliloc-brotli` stores the whole record as one Brotli stream).
- **Overrides record** (the shard's own strings): the converter folds the client folder's `Clilocs.txt` into a flat record of its own (`--no-cliloc-overrides` leaves it out), parsed by `ClilocOverrides.Parse`, the same rules the client uses for the loose file: one "number text" per line, a tab or the first run of spaces between them, blank lines and `#` lines skipped, a later line wins.
- **Lookups:** the client reads the language's record and, for any other language, enu's (it reads Cliloc.enu under every language), plus the overrides record. A lookup tries, in order: a loose `Clilocs.txt` in the client folder (still read when present, for customising and quick edits while developing), the pack's overrides record, the language, then enu. Without overrides that is the same result as the client's merged dictionary. A language the pack lacks rolls back to enu, as a missing Cliloc file did. When the pack's languages can't serve (no section, an unusable store, a corrupt language record), the Cliloc.* files serve, with the language resolved again for them (`Cliloc.<lang>`, else `Cliloc.enu`); a usable overrides record still applies over them.
- **Loading** builds a complete new state (tables, overrides, Clilocs.txt) and publishes it once; readers on other threads see the old set or the new one, and a load that fails (no Cliloc.enu, an unreadable file) keeps the old one. A reload also keeps the old state when the new one would be incomplete: another language without Cliloc.enu under it, or a `Clilocs.txt` that exists but can't be read (an absent one is fine: it is optional). The first load has nothing to keep, so it serves what it could read and logs the error.
- **Lazy strings:** the payload stays as one byte array; a string is created on its first lookup and cached in a per-table array (`Interlocked.CompareExchange`, so threads agree on one instance per id and plugins or workers may read cliloc). Nothing relied on interned strings: no caller compares cliloc strings by reference. The formatting (`Translate`: argument substitution, `~1_NAME~`, `#` cliloc references, capitalisation) is client code and is unchanged; it now looks numbers up through the same chain. With Cliloc.* files the client builds the same table from the file (one read, an index of slices, no strings) instead of a dictionary of 117,725 interned strings.
- **Converter discovery** (`ClilocDiscovery.Find`): files whose name starts with `Cliloc.` in any case; the extension (1–4 ASCII letters or digits) is the language code. Exports and backups beside them are told apart by content, not by name (`bak` is a language code too): a cliloc file is BWT-compressed (byte 3 = 0x8E) or a raw table whose records add up exactly to the file's end, so the test client's `CliLoc.csv` is skipped. `--cliloc-skip a,b` drops more by extension. No language left, no enu, or two files for one code (names differing only in case) fail the conversion. Verification discovers the set again on its own (a case-insensitive listing, each file walked with a BinaryReader and read by the frozen loader) and compares every string through `ClilocStore.Resolve` and `ReadTable`, as the client reads them.

### `text/speech.bin`: speech keywords

Not a record store (the client uses the whole table): `FileHeader` with magic `SPCH` (0x48435053), count u32, blobBytes u32, `SpeechRecord[count]` (8 bytes: id u16, pad u16, end u32), then the UTF-8 blob; entry `i`'s text is `blob[end[i−1] .. end[i])`. The entries are speech.mul's with a non-empty text, in file order. The client builds `SpeechEntry(id, text)` from each, as before, so keyword splitting and matching (`GetKeywords`, `IsMatch`) are unchanged.

### Untrusted input

- A store's extents and codecs are checked by `RecordStore.Open` (see "Record stores"); a store that fails is not used and the MUL files serve instead.
- **Font store topology,** checked from the header before the tables are allocated (`TextConst.OpenAsciiStore`, `OpenUnicodeStore`): unifont.bin has exactly 20 × 256 = 5,120 slots; fonts.bin has 1..256 (the client addresses a font by a byte). Anything else rejects the file as a whole and the MUL fonts serve.
- **Fonts:** a record must have a raw size between its header table and its largest possible size (all glyphs 255×255 for ASCII, 256 glyphs of 127×127 for a page) and a Brotli payload within Brotli's bound. At decode, an ASCII record must add up exactly (header table plus the texels it declares, no unknown flags) and a glyph without texels must be 0×0 (a size without texels would make the renderer read a null array); a page must have every glyph's rows inside the payload and absent glyphs all zero. A record that fails, or whose read or Brotli decode fails, is absent for the session (logged once, by the thread that marks it): its font has no glyphs, or its page has none. A unicode glyph's offsets may be anything an sbyte holds: the renderer clips every raster write on both sides (a negative `offsetX` at the start of a line used to index before the buffer).
- **First use:** one thread reads and decodes a page or an ASCII font; others that ask for it at the same time wait for that read instead of repeating it (striped locks; with MUL files the reads also share the file's seek position under one lock).
- **Cliloc:** a record must have a size up to 256 MiB that adds up exactly (`8 + 8·count + blobBytes`), strictly ascending ids and string ends that never decrease or pass the blob. A language record that fails makes the pack's languages unusable and the Cliloc.* files serve; an overrides record that fails is ignored (logged) and the languages still serve. Strings never throw on decode (invalid UTF-8 becomes U+FFFD, as with the files).
- **Speech:** the fixed header is read first; at most 2²⁰ entries and 64 MiB of text, and the sizes must add up to the file's exact length (wide arithmetic) before anything is allocated; the ends must not decrease or pass the blob. Else the file is not used and speech.mul serves.

## Audio (`audio/`)

Sound effects and music, as the client's `SoundsLoader` resolved them before the pack. The client reads audio **only** from here: `sound.mul`/`soundidx.mul`, `soundLegacyMUL.uop`, `Sound.def`, `Music/Digital/Config.txt` (or `Music/Config.txt`) and the Music MP3 files are the converter's input, and the client no longer opens them. A pack without `audio/sounds.bin` plays only loose `Sounds/<id>.wav` files, and one without `audio/music.bin` plays no music (each logged once at load). Owner decision: data-formats.md Q10.

Both stores put the audio first in the payload and the item's name after it (`meta2` bytes, UTF-8), so the audio starts at the payload's first byte and playing needs no copy. Ids are 0..0xFFFE (`AudioConst.MaxIds` = 0xFFFF slots at most; the client's arrays have 0xFFFF entries). Payloads need no alignment: a sound is read into a managed array and an MP3 into native memory, by copy.

### `audio/sounds.bin`: sound effects

A record store with magic `SNDS` (0x53444E53).
- **`StoreHeader.flags`** bit 0: converted from `soundLegacyMUL.uop` (else `sound.mul` + `soundidx.mul`). Informational.
- **Slots:** `slotCount` = highest present id + 1 (1,667 for 7.0.103, 1,488 for UOML). `arg` = 0 and `reserved` = 0; readers leave out a slot with either set. Ids that share an archive entry (`Sound.def` aliases) share one record.
- **Records:** codec 0 only, so playing needs no decode: the raw PCM goes straight to `DynamicSoundEffectInstance.SubmitBuffer`. `meta0` = sample rate, must be 22,050; `meta1` = channels, must be 1; `meta2` = name length, 0..40. Payload: `rawBytes − meta2` bytes of 16-bit signed little-endian PCM, a non-zero even length (whole samples: FNA refuses anything else), then the name. The name is the archive entry's 40-byte name field up to its first NUL (e.g. `sword1.wav`), or a loose file's name without the extension.
- **Converter resolution** (`Uo.Converter/AudioImport.cs`):
  - the archive the client picked: the UOP on UOP installations when it exists, else the MUL pair;
  - `Sound.def` applied as the client applied it: an id that already has data is skipped, each group entry with data replaces it (the last one wins), and `-1` clears it;
  - then the client folder's loose `Sounds/<id>.wav` files over it, through the client's own reader (`AudioConst.ReadWaveFile`): at most `MaxWaveFileBytes` (64 MiB + 1 MiB) checked before the read, chunk sizes compared by subtraction (a size past the end is clamped to the end, as ClassicUO did), 22,050 Hz mono 16-bit, and a non-empty even data chunk. A loose file that fails any of these falls back to the archive entry, in the converter as in the client, and is listed as a warning.
  - An archive entry of 40 bytes or less has no PCM and is absent (ClassicUO threw on one shorter than 40).
  - An entry whose PCM is not a whole number of 16-bit samples (odd length) is absent and listed as a warning: FNA would throw on it. Stock 7.0.103 has none.
- **Client** (`SoundsLoader.TryGetSound` via `AudioConst.ReadSound`): on the first play of an id, two positional reads: the PCM into an array of exactly its length, and the name. `DynamicSoundEffectInstance.SubmitBuffer(array, offset, count)` checks the whole array's length against the block alignment, not just `count`, so the name must not share the PCM's array (bench-000049 crashed on an 11-byte name after even PCM). `UOSound` plays that array. The renderer's `SoundCache` keeps played sounds within a budget: ids that share a record share one array, the distinct arrays stay within 64 MiB and at most 1,024 ids are cached, and the least recently played id that is not playing is evicted. `AudioManager` plays at most 32 sound effects and 64 MiB of PCM at once (FNA copies each playing buffer into native memory) and stops the oldest to make room.
- **Loose `Sounds/<id>.wav` files still win at runtime** (the folder is listed once at load), so a shard can try a sound without rebuilding the pack. The converter folds the same files into the pack, so a shipped pack needs none.

### `audio/music.bin`: music

A record store with magic `MUSC` (0x4353554D).
- **`StoreHeader.flags`** bit 0: the client folder had no `Config.txt`, so ClassicUO's built-in 67-entry table was used. Informational.
- **Slots:** one per music id of the table (`slotCount` = highest id + 1: 103 for 7.0.103). `arg` bit 0 = the id loops (`Config.txt`'s `loop`); other bits must be 0, and `reserved` must be 0 (readers leave out the slot otherwise). The loop flag belongs to the id, not the file: ids naming one file share its record (7.0.103: 102 ids, 90 files; UOML: 57 ids, 45 files).
- **Records:** codec 0 only. `meta0` = audio format, 1 = MP3 (the only one 1.0 knows); `meta1` = 0; `meta2` = name length, 0..255. Payload: the MP3 file's bytes exactly, ID3, Xing/Info and LAME frames included, then the file name (e.g. `Britain1.mp3`). dr_mp3 skips the encoder delay and trims the padding from those frames, so loops are gapless and the record needs no extra metadata beyond the loop flag.
- **Converter resolution:**
  - the table the client read: `Music/Digital/Config.txt` for 4.0.11c and later, `Music/Config.txt` before, else the built-in table;
  - each line is `id name[,loop]`: space, comma or tab separated, 2 or 3 fields, and a later line for an id wins;
  - the name resolves to a file as `GetTrueFileName` did (the first `*.mp3` under `Music/` matching `^name.mp3`, ignoring case), played from `Music/Digital/` when that folder exists, else from `Music/`;
  - an id whose file is missing, or whose bytes `Mp3Validator` refuses, is absent (and listed as a warning).
- **Deviation from ClassicUO, on purpose:** the client compared the version with `CV_4011C` (0x04000B63, the letter as ASCII), which no parsed 4.0.11x reaches ("4.0.11c" parses as 0x04000B02). So a 4.0.11c install (UOML) read the built-in table, whose names it lacks, and 57 of its 67 ids were silent. The converter compares with the parsed form (`AudioConvert.ParsedCV4011C`): UOML gets its own `Music/Digital/Config.txt` (57 ids, every one with a file).
- **Client** (`SoundsLoader.TryGetMusic`, `UOMusic`, `MusicStream`):
  - the slot gives the loop flag and the record; the name (for the log) is a small read of the record's tail;
  - on play, the music worker opens a positional stream over the record's MP3 bytes (`RecordStore.OpenRawStream`) and reads them into native memory, and `Mp3Validator` checks them before dr_mp3 opens them, as it did for files;
  - decoding stays on the worker thread, and looping seeks to frame 0 inside a buffer fill (gapless).

### Untrusted input

- `RecordStore.Open` checks the extents and codecs (see "Record stores"), and `AudioConst.OpenSoundStore` / `OpenMusicStore` limit the slot count to 1..0xFFFF before anything is allocated.
- **Overlap:** the client and the verify step reject a store whose payloads overlap (`AudioConst.CheckNoOverlap`). Ids share content through slots, never through records pointing into each other's bytes. The check sorts the records by offset and keeps the furthest end of all earlier non-empty payloads, so neither an empty record between two overlapping ones nor a short record inside a long one hides an overlap.
- A store that fails is not used. The failure is logged as an error, and there is no MUL fallback.
- **Records, checked at load** (`AudioConst.CheckSoundRecord`, `CheckMusicRecord`, `CheckMusicSlot`):
  - sounds: codec 0, 22,050 Hz, 1 channel, a name of at most 40 bytes, 2 bytes to 64 MiB of PCM in whole 16-bit samples (the largest stock sound is 21.2 MB). Before FNA sees a buffer, the loader checks it against FNA's `SubmitBuffer` rules (`AudioConst.SubmitError`) and `Sound.Play` checks again (`Sound.CanSubmit`); a buffer that fails is not played, never thrown. Loose WAVs with an odd or empty data chunk are ignored the same way;
  - music: codec 0, format 1, `meta1` 0, a name of at most 255 bytes, 1 byte to 32 MiB of MP3 (`Mp3Validator.MaxBytes`);
  - slots (`CheckSoundSlot`, `CheckMusicSlot`): see the table below.
  A record or slot that fails is absent for the session (logged once), and the others still play.
- **Which fields readers check.** Audio stores use the record-store layout; this is the full list for 1.0 readers:

  | field | sounds | music |
  |---|---|---|
  | `StoreHeader.slotCount` | rejected outside 1..0xFFFF (whole store) | same |
  | `StoreHeader.recordCount`, record extents, codec, `storedBytes = rawBytes` | rejected (whole store), as for every record store | same |
  | `StoreHeader.flags` | ignored (bit 0 is informational; unknown bits too) | ignored (same) |
  | `StoreHeader.reserved` | ignored | ignored |
  | `StoreSlot.record` | must point into the record table (whole store) | same |
  | `StoreSlot.arg` | must be 0, else the slot is absent | only bit 0 (loop) may be set, else the slot is absent |
  | `StoreSlot.reserved` | must be 0, else the slot is absent | must be 0, else the slot is absent |
  | `StoreRecord.meta0/1/2` and sizes | rate 22,050, 1 channel, name ≤ 40, PCM 2 B..64 MiB and even, else the record is absent | format 1, `meta1` 0, name ≤ 255, MP3 1 B..32 MiB, else the record is absent |
  | payload overlap | rejected (whole store) | rejected (whole store) |

  The ignored header fields are left for informational flags a later converter may add; anything that changes how a payload is read goes in a checked field (a new codec, `meta0` format or slot bit), which a 1.0 reader then refuses.
- **Loose WAVs** are untrusted too (`AudioConst.ReadWaveFile`, shared by the client and the converter): the file size is checked before the read, every chunk size is compared by subtraction (a 44-byte file declaring 0x7FFFFFF0 data bytes yields an empty, refused data chunk), and a bad file is logged and skipped, never thrown. A read that fails later (I/O, a truncated file) makes that record absent and is logged once.
- **MP3 bytes** are untrusted too. `Mp3Validator` (size, ID3v2 tag, a chain of valid Layer III frame headers) runs on every play before dr_mp3. A refused or undecodable track fails its `MusicStream` cleanly on the worker and is logged as skipped.

## Manifest (`manifest.json`)

camelCase JSON, read case-insensitively:

```json
{
  "version": 1,
  "sourcePath": "C:\\Ultima Online Classic",
  "sourceClientVersion": "7.0.103.0",
  "created": "2026-10-24T…Z",
  "facets": [ { "index": 0, "width": 7168, "height": 4096, "blocksX": 896, "blocksY": 512, "regionBlocks": 64 } ],
  "files": [ { "path": "art/page_0000.bin", "size": 123, "sha256": "…" } ]
}
```

`files` lists every file except the manifest, with paths relative to the pack root using `/`, sorted ordinally. The sha256 is of the file as stored, so it changes when a file is re-encoded to another kind. A patcher compares hashes to fetch only changed files.

## Ship profile

`dotnet run -c Release --project poc/Uo.Converter -- --out <pack dir>` with no format flags writes exactly this (`--out` is required; there is no default output folder):

| Content | Pages | Compression |
|---|---|---|
| Art, texmaps | B5G5R5A1, 2048², full mip chain (6 levels) | Kind 2, 1 MiB chunks |
| Animations | Indexed8, 1024², mip0 only, plus `palettes.bin` | Kind 2, 1 MiB chunks; the 16-byte PageHeader puts the last 16 bytes of a full page in a second, tiny chunk |
| Map regions | — | Kind 1 |
| Gumps | record store | Kind 0 file; images up to 64 KiB of run-length payload uncompressed, larger ones as texels with Brotli (quality 9) |
| Fonts | record stores | Kind 0 files; ASCII fonts and unicode pages up to 16 KiB raw uncompressed, larger ones Brotli (quality 9) |
| Cliloc | record store | Kind 0 file; language records uncompressed (one read at startup, no decode) |
| Sounds, music | record stores | Kind 0 files; records uncompressed (raw PCM; the original MP3 bytes) |
| Everything else | — | Kind 0 |

- No install-time unpacking is required. A patcher may still rewrite any file to Kind 0, since raw is always valid.
- Variant flags (experimental or for measurement):
  - formats: `--format rgba8|bc7`, `--anim-format page` (anims use `--format`), `--indexed-mips b16`;
  - page sizes: `--page-size n`, `--anim-page-size n`;
  - compression: `--raw-pages`, `--compress-pages` (Kind 1), `--chunk-bytes n`, `--raw-regions`, `--raw-gumps` (gump records uncompressed, run-length layout), `--raw-text` (font records uncompressed), `--text-small n` (font records up to n raw bytes stay uncompressed; 16384), `--cliloc-brotli` (cliloc records Brotli);
  - testing: `--gump-def <file>` (read this gump.def instead of the client's), `--gump-report` (the gump storage measurements above; writes nothing), `--audio-report <pack>` (load and first-read costs of the audio stores against the old loader; writes nothing; use with `--verify-only`).
  - `--client-version x.y.z.w` names the version of a client.exe without version info (UOML: `4.0.11c`). Interim form: poc/legacy-import brings the full resolution under the same flag.
- Stages: `--only art,tex,hues,tiledata,data,gumps,fonts,cliloc,speech,sounds,music,maps,anims` (into an existing pack), `--no-maps`, `--no-anims`, `--no-gumps`, `--no-fonts`, `--no-cliloc`, `--no-speech`, `--no-sounds`, `--no-music`, `--verify-only`. `sounds` writes `audio/sounds.bin`, `music` writes `audio/music.bin`. `fonts` writes `text/fonts.bin` and `text/unifont.bin`. `hues` writes `hues_raw.bin` and `radarcol.bin` (and deletes a stale `hues.bin` from the output), `tiledata` writes both tiledata files, and `data` writes multis, lights, tileart and equipconv. `animdata.bin` belongs to `anims`.
- **Outputs are replaced, never written in place.** Every pack file the converter writes (tables, pages, regions, record stores, text, anims, `facet.json`, `manifest.json`) goes to a temp file in the same folder (`.<name>.<guid>.replacing.tmp`), which is then moved over the target (`Uo.Pack.ReplacingFile`). Replacing the directory entry breaks a hard link instead of writing through it, so a test pack built by hard-linking a published pack's files (`cp -al`) and then converting `--only <sections>` leaves the published files untouched. A failed write deletes its temp file and leaves the target as it was; the manifest skips leftover temp files.
- **Published packs are refused as output.** Published versions are immutable. Every write (`Uo.Pack.ReplacingFile`, at create and again before the commit) and every delete (`Uo.Pack.PackOutputFiles`) checks its own folder, so a junction inside an accepted output cannot reach a published pack. The tools also refuse an output folder (`--out`, `--debug`, `--bench-chunked`'s out, `Uo.Render.App --make-synthetic` and `--debug-compress-pages`) that is, lies under, or contains a published packs root. Roots: `C:/Repositories/uo-packs`, `UO_PACKS_ROOTS` (`;`-separated), every `--packs-root <dir>` (read once, before any other argument, by `PublishedPacks.ConfigureFromArgs`), and any ancestor folder holding a `CURRENT` file that names one of its subfolders. Paths are compared in canonical form (Windows: the file system's final path of the nearest existing folder, so junctions, symlinks, subst drives, `\\?\` and `\\.\` prefixes and volume GUID paths collapse to one drive path); network paths are also compared by file identity (volume serial + file id) with every folder of each root. When the relationship cannot be established (a link loop, an unreachable share, a device path without a drive letter) the write is refused. `--verify-only` only reads, so it may verify a published pack. The default `--debug` folder is `%TEMP%/uo-converter-debug/<pack folder name>`; `--make-synthetic` defaults to `%TEMP%/uo-synthetic-pack`.
- **No recursive deletes.** A full conversion deletes only the files the previous output's `manifest.json` lists (plus crashed-writer temp files beside them and the manifest itself); a non-empty `--out` without a manifest is refused ("not a converter output"). `--only anims` deletes the `*.bin` files directly in `anim/`, and the debug folders lose only their `*.png` files. Each delete checks its folder like a write.
- **`--verify-only --only hues`** (and a full verify) fails a pack without `hues_raw.bin`, the only hue source. Likewise `--only sounds` / `--only music` (and a full verify, unless converted with `--no-sounds` / `--no-music`) fail a pack without `audio/sounds.bin` / `audio/music.bin`: the client has no other source.

**Verification.** Every conversion ends with a verify step that reads the pack back with the `Uo.Pack` readers. It compares against ClassicUO's own loaders:
- **Sampled.** Sprites are bit-exact including gutters. Indexed8 frames are bit-exact after the palette lookup.
- **Every record and field, exact:** tiledata (all fields and names), hues (raw entries, and the LUT built from them against an independent expansion of ClassicUO's entries), radarcol, animdata, multis, lights, tileart appearances and equipconv.
- **Every gump slot of the client's index (81,919 for 7.0.103), exact:** present or not, width and height, and the RGBA8 image with the alias hue, against the converter's copy of `GumpsLoader.GetGump`. The client test `GumpPackTests` repeats it against the client's own MUL/UOP loader.
- **Audio, exact, against the converter's copy of the client's `SoundsLoader`** (tools/ClassicUO.Assets.Mul): every sound id 0..0xFFFE (present or not; the array the client hands to FNA, decoded with the client's own `AudioConst.ReadSound` and checked against FNA's `SubmitBuffer` rules, then compared byte for byte, and the name) and every music id 0..0xFFFE (present or not, the loop flag, and the MP3 bytes against the file the client played, byte for byte); payloads must not overlap. The client test `AudioPackTests` runs the importer on synthetic MUL files and reads the result through the client's `SoundsLoader`.
- **Text, exact, against the converter's copies of the client loaders:** every ASCII glyph of every font and every unicode glyph of the 20 font slots × 65,536 characters (offsets, size, rows, existence); every (language, id) cliloc string, resolved as `ClilocLoader.Load(language)` resolves it (enu under the language); every speech entry (id, text, keywords, start and end flags). The client test `TextPackTests` repeats them against the client's own MUL path, plus rendered text.

## Reference sizes (7.0.103, ship profile)

| Content | Files | On disk |
|---|---|---|
| Art | 31 pages + sprites.bin | 67.6 MiB (330.6 MiB raw pages) |
| Texmaps | 4 pages + texmaps.bin | 16.5 MiB |
| Animations | 2,652 pages + anim.bin, palettes.bin, animdata.bin | 675.3 MiB (628.0 MiB pages; 2,652 MiB raw) |
| Maps | 6 facets, 297 regions | 57.3 MiB (349.7 MiB raw) |
| Tile data | tiledata.bin, tiledata_ext.bin | 0.9 + 2.1 MiB |
| Hues, radarcol | hues_raw.bin, radarcol.bin | 0.3 + 0.2 MiB (packs before 2026-10-31 also carry the retired 0.4 MiB hues.bin) |
| Multis, lights, tileart, equipconv | | 1.8 MiB, 2.8 MiB, 692 B, 1,060 B |
| Text | text/fonts.bin, unifont.bin, cliloc.bin, speech.bin | 23.4 MiB: 35 KiB + 7.5 MiB (763,025 glyphs in 866 page records, 2,171 pages shared) + 15.6 MiB (8 languages, 941,936 strings) + 141 KiB; 11.8 MiB with `--cliloc-brotli`; 62.5 MB of MUL files |
| Audio | audio/sounds.bin, audio/music.bin | 160.5 MiB (1,656 sound ids in 1,655 records, 160.4 MiB of PCM, tables 54.7 KiB) + 103.5 MiB (102 music ids, 90 MP3 files); the sources are `soundLegacyMUL.uop` (168.4 MB = 160.6 MiB) and 103.5 MiB of MP3s. UOML (MUL): 107.8 + 53.2 MiB |
| Gumps | gumps/gumps.bin | 58.2 MiB (123.5 MiB of payloads; 37.9 MiB with every record Brotli, `--gump-small 0`; 133.7 MiB with `--raw-gumps`); 4,438 gumps, 4,290 records |

Decode cost on one worker (i9-12900K): an anim page ≈2.6 ms, an art page ≈23 ms (≈4 ms with 8 workers on Kind 2), a region ≈2.4 ms.

## Not in the pack (stay on MUL/UOP for now)

| Content | ClassicUO loader | Notes |
|---|---|---|
| Skills, professions | `SkillsLoader`, `ProfessionLoader` | |
| World map images | `MultiMapLoader` (Multimap.rle, facet*.mul) | |
| Anim1.def / Anim2.def group replaces, animinfo.mul, sitting tables | `AnimationsLoader` and client tables | runtime fallbacks for mounts and missing groups |

Gumps moved into the pack on 2026-10-30 ("Gumps"); a pack without `gumps/gumps.bin` still works, and the client then reads gumpart MUL/UOP. Fonts, cliloc and speech followed the same day ("Text"); a pack without a `text/` file reads the MUL files for that content. Sounds and music followed on 2026-10-31 ("Audio"), with no MUL fallback. Clilocs.txt (a shard's own cliloc strings) stays a plain text file in the client folder, read at load on top of the pack's tables.

Picking needs CPU-side alpha. The client decodes pack pages on the CPU (B5G5R5A1 bit 15; Indexed8 index ≠ 0), and gump records likewise (the picker mask is built from the decoded image at upload). Mask sidecars are not part of 1.0.

## Appendix: experimental formats (not shipped)

Readers in `Uo.Pack` handle these, and the converter writes them on request. A 1.0 client need not support them.

- **`Rgba8` (format 0).** 4 bytes per texel, ClassicUO-expanded colours, alpha 0 or 255, box-filtered mips. Twice the size of 16-bit for no fidelity gain.
- **`Bc7` (format 1).** BPTC unorm, 16 bytes per 4×4 block. Cells are aligned to 4 texels and padded to multiples of 4. BC7 is rejected because lossy formats break hueing: 31.8% of the gray texels the partial-hue test needs stop being exactly gray. The tuning details are in pack-format-v0.md.
- **Indexed8 mip sidecar** (`--indexed-mips b16`): `anim/mips_NNNN.bin` is a B5G5R5A1 page of side S/2 holding mips 1+ of anim page NNNN. It is only needed for zoom below 0.5, and gives back about 60% of Indexed8's size saving.
- **Kind 1 pages** (`--compress-pages`): valid, but a page then decodes on one thread only.
- **B5G5R5A1 animations** (`--anim-format page`): lossless, but at 1024² about 2.7× Indexed8's raw bytes and 1.8× its compressed bytes.
