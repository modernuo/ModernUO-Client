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
  hues.bin                 hue LUT, RGBA8 (HUES)
  hues_raw.bin             hues.mul entries: 16-bit colours, TableStart/End, names (HERW)
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
| `HUES` 0x53455548 | hues.bin |
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

**Reader rule.** A reader accepts `version` 0 and 1 and rejects anything higher. Version 0 files come from pre-1.0 packs. Every version-0 file has the same layout as its version-1 counterpart, so no reader branches on the version. What version-0 packs lack:

- the game data tables (`tiledata_ext.bin`, `hues_raw.bin`, `radarcol.bin`, `multis.bin`, `lights.bin`, `tileart.bin`, `equipconv.bin`): a 1.0 client treats a pack without them as incomplete;
- converter-derived `packFlags` in tiledata, which may be 0 (see "NoDraw flags");
- the canonical hue LUT: version-0 `hues.bin` used ClassicUO's `floor` expansion, up to 1/255 darker per channel.

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

Which files use which kind is a profile choice (see "Ship profile"). Only pages and regions are compressed as whole files; all other files are Kind 0. Record stores (gumps) are Kind 0 files whose records are compressed one by one (see "Record stores").

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

### `hues.bin`: RGBA8 LUT for the renderer

`FileHeader`, `count i32`, then `count × 32` RGBA8 `u32`. Row `i` is hue `i + 1` (UO hue 0 = unhued), column `s` is shade 0..31, alpha 0xFF. Each texel is the canonical expansion of `hues_raw.bin`'s colour.

**Change in 1.0:** the LUT is regenerated with the canonical bit-replication expansion (ClassicUO uses `floor`); version-0 packs have the floor values. The 5-bit colours are in `hues_raw.bin` for exact CPU work.

Shader hueing follows ClassicUO: the shade index is the texel's 5-bit red channel. A partial-hue sprite hues only exactly gray texels (`r5 == g5 == b5`, compared as 5-bit integers, no tolerance).

### `hues_raw.bin`: hues.mul, exactly

`FileHeader`, `count i32`, `HueEntry[count]`. `HueEntry` (88 bytes) is byte-identical to ClassicUO's `HuesBlock`: colours u16[32], tableStart u16, tableEnd u16, name u8[20] (raw bytes, NUL-padded). Entry `i` is hue `i + 1`. This serves CPU font and gump hueing (`GetColor16`, `GetUnicodeFontColor`, and similar). The 4-byte group headers of hues.mul are unused by ClassicUO and dropped.

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

Content that the client reads one item at a time and that is not sampled by the GPU from pack pages goes into a record store: one Kind 0 file per content type, read by offset through one file handle kept open. Gumps are the first; sounds, fonts and the other "Not in the pack" types are meant to use the same layout with their own magic, slot argument and record meta. Reader and writer: `Uo.Pack.RecordStore`, `RecordStoreWriter`.

| Part | Layout |
|---|---|
| `FileHeader` | magic = the content type, version 1, kind 0 |
| `StoreHeader` (16 bytes) | slotCount u32, recordCount u32, flags u32 (type-specific), reserved u32 |
| `StoreSlot[slotCount]` (8 bytes each) | record u32 (record index + 1; 0 = no such id), arg u16 (type-specific), reserved u16 |
| `StoreRecord[recordCount]` (24 bytes each) | offset u64 (from the start of the file), storedBytes u32, rawBytes u32, codec u16, meta0, meta1, meta2 u16 (type-specific) |
| payloads | back to back, each decodable on its own |

- Slot `i` is id `i`. Several ids may share one record (aliases, identical content).
- Codec 0: the payload as is (`storedBytes = rawBytes`). Codec 1: one Brotli stream (window 22) that decodes to `rawBytes` bytes. Readers reject other codecs.
- A reader reads the header, the slot table and the record table once (one read each) and validates them: every slot points into the record table, every record lies inside the file. A record is then one positional read plus its decode, from any thread, into the caller's buffer.
- No whole-file compression and no per-record files: a store is read by offset (the per-open cost of a file is 20–28 µs, L7), and a record decodes alone.

## Gumps (`gumps/gumps.bin`)

A record store with magic `GUMP` holding every gump id the client's `GumpsLoader` resolves, with `gump.def` and loose `Gumps/*.gump` files already applied. The client reads gumps only from here when the file exists (`GumpsLoader.FromPack`); without it, it reads gumpart MUL/UOP as before.

- **`StoreHeader.flags`** bit 0: converted from `gumpartLegacyMUL.uop` (ClassicUO's `UseUOPGumps`; the status gump's layout depends on it).
- **Slots:** `slotCount` = highest present id + 1 (69,986 for 7.0.103, whose UOP has gumps above 0xFFFF). `arg` = the gump.def alias hue (0 = none).
- **Records:** `meta0` = width, `meta1` = height, `meta2` = payload layout. The record table is the size table: width and height of any gump without decoding it.
- **Payload layouts** (`Uo.Pack.GumpCodec`), both lossless 16-bit:
  - 0, texels: `width·height` B5G5R5A1 `u16`, row-major. Texel 0 = transparent, else bit 15 set and bits 0–14 the source colour. Used with codec 1.
  - 1, run-length rows: `u32 rowStart[height]` (in 4-byte units from the start of this table), then `(u16 texel, u16 run)` pairs. Row `y`'s runs end where row `y+1`'s start (the last row's at the end of the payload); texels no run reaches are transparent. This is the client's own entry layout, re-encoded canonically (a run per change of texel, no trailing transparent run). Used with codec 0.
  - Any (layout, codec) pair is valid for a reader. Ship profile: an image whose run-length payload is at most 16 KiB (`--gump-small n`) is stored as layout 1, codec 0; every other image as layout 0, codec 1. `--raw-gumps` stores every image as layout 1, codec 0.
- **Lossless:** the stored texel is the source value's colour bits plus bit 15 = visible. ClassicUO's decode ignores bit 15 (`Color16To32`, `GetColor16`), so every image the client draws is reproduced exactly; hueing needs the exact 5-bit gray texels, which no lossy format keeps (see BC7 in the appendix).
- **Drawing a gump** (ClassicUO `GumpsLoader.GetGump`): each visible texel `v` becomes RGBA8 with ClassicUO's 5→8-bit table (`HuesHelper.Color16To32`, not the canonical expansion: gumps go through the CPU and the batcher's RGBA8 atlas, and parity is with the MUL path) and alpha 255. With a slot hue `h`, `1 ≤ h < hue count`, first `v = hues[h − 1].colors[(v >> 10) & 31]`, and the texel stays visible only if that is not 0 (`GetColor16`).
- **Converter resolution:** ids whose index entries are identical (gump.def copies the entry and sets a hue; UOP/MUL may alias too) share one record, and so do ids with identical decoded images. An id with a loose `Gumps/<id>.gump` file gets its own record from that file, with no hue, as the client's `ReadOurs`; a broken file falls back to the archive entry with its hue, as the client does. The client no longer reads loose gump files when the pack has gumps; re-run the converter (`--only gumps`, about 6 s) after changing them.

**Why per-gump records and not atlas pages** (`--gump-report`, 7.0.103, 4,438 gumps):

| Candidate | Size | Decode of a 670×670 gump (µs, JIT, median) |
|---|---|---|
| Client MUL/UOP entries (reference) | 136.9 MiB | 1,250–1,740 (the client's own RLE decode) |
| Atlas pages, 2048², no mips | 289 MiB raw (36 pages) | whole 1 MiB chunks: a 600-row gump touches 3 (2.4 MB) |
| Raw texels + Brotli (ship, images over 16 KiB) | 37.9 MiB | 1,800–2,150 (Brotli 1,750–2,000, expand 90) |
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

  The 16 KiB threshold stores 3,459 of the 4,290 records this way (37.9 → 47.5 MiB) and makes them decode in under 7 µs. The 831 larger ones keep Brotli: there the decode is 0.1–2 ms either way, and the worker prefetch (see the poc-log entry "Gumps in the pack") takes it off the main thread.
- Sizes for the 4,438 gumps: 4,290 records after sharing (16 gump.def/index aliases, 132 identical images), 47.5 MiB file in the ship profile, of which the tables are 0.6 MiB.

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

`dotnet run -c Release --project poc/Uo.Converter` with no format flags writes exactly this:

| Content | Pages | Compression |
|---|---|---|
| Art, texmaps | B5G5R5A1, 2048², full mip chain (6 levels) | Kind 2, 1 MiB chunks |
| Animations | Indexed8, 1024², mip0 only, plus `palettes.bin` | Kind 2, 1 MiB chunks; the 16-byte PageHeader puts the last 16 bytes of a full page in a second, tiny chunk |
| Map regions | — | Kind 1 |
| Gumps | record store | Kind 0 file; images up to 16 KiB of run-length payload uncompressed, larger ones as texels with Brotli (quality 9) |
| Everything else | — | Kind 0 |

- No install-time unpacking is required. A patcher may still rewrite any file to Kind 0, since raw is always valid.
- Variant flags (experimental or for measurement):
  - formats: `--format rgba8|bc7`, `--anim-format page` (anims use `--format`), `--indexed-mips b16`;
  - page sizes: `--page-size n`, `--anim-page-size n`;
  - compression: `--raw-pages`, `--compress-pages` (Kind 1), `--chunk-bytes n`, `--raw-regions`, `--raw-gumps` (gump records uncompressed, run-length layout);
  - testing: `--gump-def <file>` (read this gump.def instead of the client's), `--gump-report` (the gump storage measurements above; writes nothing).
- Stages: `--only art,tex,hues,tiledata,data,gumps,maps,anims` (into an existing pack), `--no-maps`, `--no-anims`, `--no-gumps`, `--verify-only`. `hues` also writes `hues_raw.bin` and `radarcol.bin`, `tiledata` writes both tiledata files, and `data` writes multis, lights, tileart and equipconv. `animdata.bin` belongs to `anims`.

**Verification.** Every conversion ends with a verify step that reads the pack back with the `Uo.Pack` readers. It compares against ClassicUO's own loaders:
- **Sampled.** Sprites are bit-exact including gutters. Indexed8 frames are bit-exact after the palette lookup.
- **Every record and field, exact:** tiledata (all fields and names), hues (raw entries and the LUT), radarcol, animdata, multis, lights, tileart appearances and equipconv.
- **Every gump slot of the client's index (81,919 for 7.0.103), exact:** present or not, width and height, and the RGBA8 image with the alias hue, against the converter's copy of `GumpsLoader.GetGump`. The client test `GumpPackTests` repeats it against the client's own MUL/UOP loader.

## Reference sizes (7.0.103, ship profile)

| Content | Files | On disk |
|---|---|---|
| Art | 31 pages + sprites.bin | 67.6 MiB (330.6 MiB raw pages) |
| Texmaps | 4 pages + texmaps.bin | 16.5 MiB |
| Animations | 2,652 pages + anim.bin, palettes.bin, animdata.bin | 675.3 MiB (628.0 MiB pages; 2,652 MiB raw) |
| Maps | 6 facets, 297 regions | 57.3 MiB (349.7 MiB raw) |
| Tile data | tiledata.bin, tiledata_ext.bin | 0.9 + 2.1 MiB |
| Hues, radarcol | hues.bin, hues_raw.bin, radarcol.bin | 0.4 + 0.3 + 0.2 MiB |
| Multis, lights, tileart, equipconv | | 1.8 MiB, 2.8 MiB, 692 B, 1,060 B |
| Gumps | gumps/gumps.bin | 47.5 MiB (137.4 MiB of payloads; 37.9 MiB with every record Brotli, `--gump-small 0`; 133.7 MiB with `--raw-gumps`); 4,438 gumps, 4,290 records |

Decode cost on one worker (i9-12900K): an anim page ≈2.6 ms, an art page ≈23 ms (≈4 ms with 8 workers on Kind 2), a region ≈2.4 ms.

## Not in the pack (stay on MUL/UOP for now)

| Content | ClassicUO loader | Notes |
|---|---|---|
| Fonts | `FontsLoader` | next; a record store like gumps |
| Cliloc, speech | `ClilocLoader`, `SpeechesLoader` | |
| Sounds and music | `SoundsLoader` | |
| Skills, professions | `SkillsLoader`, `ProfessionLoader` | |
| World map images | `MultiMapLoader` (Multimap.rle, facet*.mul) | |
| Anim1.def / Anim2.def group replaces, animinfo.mul, sitting tables | `AnimationsLoader` and client tables | runtime fallbacks for mounts and missing groups |

Gumps moved into the pack on 2026-10-30 ("Gumps"); a pack without `gumps/gumps.bin` still works, and the client then reads gumpart MUL/UOP.

Picking needs CPU-side alpha. The client decodes pack pages on the CPU (B5G5R5A1 bit 15; Indexed8 index ≠ 0), and gump records likewise (the picker mask is built from the decoded image at upload). Mask sidecars are not part of 1.0.

## Appendix: experimental formats (not shipped)

Readers in `Uo.Pack` handle these, and the converter writes them on request. A 1.0 client need not support them.

- **`Rgba8` (format 0).** 4 bytes per texel, ClassicUO-expanded colours, alpha 0 or 255, box-filtered mips. Twice the size of 16-bit for no fidelity gain.
- **`Bc7` (format 1).** BPTC unorm, 16 bytes per 4×4 block. Cells are aligned to 4 texels and padded to multiples of 4. BC7 is rejected because lossy formats break hueing: 31.8% of the gray texels the partial-hue test needs stop being exactly gray. The tuning details are in pack-format-v0.md.
- **Indexed8 mip sidecar** (`--indexed-mips b16`): `anim/mips_NNNN.bin` is a B5G5R5A1 page of side S/2 holding mips 1+ of anim page NNNN. It is only needed for zoom below 0.5, and gives back about 60% of Indexed8's size saving.
- **Kind 1 pages** (`--compress-pages`): valid, but a page then decodes on one thread only.
- **B5G5R5A1 animations** (`--anim-format page`): lossless, but at 1024² about 2.7× Indexed8's raw bytes and 1.8× its compressed bytes.
