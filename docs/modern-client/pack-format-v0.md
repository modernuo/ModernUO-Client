# Pack Format v0 (PoC)

The contract between the converter (`poc/Uo.Converter`) and the renderer (`poc/Uo.Render`). It's implemented in `poc/Uo.Pack` (structs, readers, writers). The goal is GPU-friendly and streaming-friendly data that keeps gameplay semantics: tile ids, z, flags.

All integers are little-endian. Every binary file starts with a 16-byte `FileHeader`: magic `u32`, version `u16`, kind `u16`, reserved `u64`.

## Layout

```
<pack>/
  manifest.json              version, source client path/version, created, files[{path,size,sha256}]
  tiledata.bin               LandTileData[0x4000], then StaticTileData[staticCount]
  hues.bin                   HueLut: count, then count × 32 × RGBA8 (row = hue, col = shade index 0..31)
  art/
    sprites.bin              SpriteEntry[] — index = sprite id (land: 0..0x3FFF, static: 0x4000 + graphic)
    page_NNNN.bin            one atlas page: PageHeader + RGBA8 mip chain (mip0 first)
  tex/
    texmaps.bin              SpriteEntry[] for texmaps (index = texmap id)
    page_NNNN.bin            texmap atlas pages (same page format)
  map/<facet>/
    facet.json               { width, height, blocksX, blocksY, regionBlocks }
    r_<rx>_<ry>.bin          a region of regionBlocks × regionBlocks map blocks (8×8 cells each)
```

## Atlas pages (`page_NNNN.bin`)

- `PageHeader`: width `u16`, height `u16`, mipCount `u16`, format `u16` (0 = RGBA8 unorm), dataBytes `u32`, reserved `u32`.
- v0 pages are 2048×2048 RGBA8 with a full mip chain down to 64×64 (6 levels).
- **Sprites carry a 2-pixel gutter** filled by edge extrusion, so bilinear sampling and zoom don't bleed between sprites.
- **A page is the unit of GPU residency and streaming.** The renderer maps logical pages to physical layers of a fixed-capacity `Texture2DArray` and evicts least-recently-used pages.
- v1: BC7 (desktop) and ASTC (mobile) via KTX2/Basis, chosen by `format`.

## `SpriteEntry` (20 bytes)

| Field | Type | Meaning |
|---|---|---|
| page | u16 | logical page index (0xFFFF = sprite absent) |
| x, y | u16 | top-left of the trimmed sprite in the page (inside the gutter) |
| w, h | u16 | trimmed size |
| trimX, trimY | i16 | trimmed rect offset inside the original image |
| origW, origH | u16 | original image size (UO draw anchoring uses this) |
| flags | u16 | bit0 = land art, bit1 = texmap, bit2 = has partial-hue pixels |

Art is converted from UO 16-bit 1555 to RGBA8. Fully transparent pixels get alpha 0. **Hues are applied in the shader** with ClassicUO's grayscale → hue LUT lookup, so pixel colors are stored unhued.

## Map regions (`r_<rx>_<ry>.bin`)

- `regionBlocks` = 64, so a region covers 64×64 blocks, i.e. 512×512 cells.
- `RegionHeader`: facet `u16`, rx `u16`, ry `u16`, blocksW `u16`, blocksH `u16`, reserved `u16`, then a block offset table `u32[blocksW*blocksH]` (byte offset from file start; 0 = empty).
- Block record:
  - `LandCell[64]` (row-major y*8+x): tileId `u16`, z `i8`, pad `u8`;
  - staticCount `u16`, pad `u16`;
  - `StaticCell[staticCount]`: graphic `u16`, x `u8`, y `u8`, z `i8`, pad `u8`, hue `u16`.
- The data is semantic, not pre-baked. The renderer bakes per-chunk instance buffers on worker threads when a region or block loads. This keeps walkability and collision data available for client-side prediction.
- **UltimaLive:** a live update just replaces one block record in memory and re-bakes that one chunk, a microsecond-scale job, with no format change.

## Tile data

- `LandTileData` (12 bytes): flags `u64`, texId `u16`, packFlags `u16` (pad in v0).
- `StaticTileData` (12 bytes): flags `u64`, height `u8`, layer `u8`, packFlags `u16` (pad in v0).
- Flags keep UO's `TileFlag` bit values. `packFlags` is converter-derived, see "v1 NoDraw flags" below.

## Deliberately deferred to v1+

Gumps, animations, multis, sounds, fonts, normal and height maps, ASTC (mobile), and content-addressed CDN chunking. GPU texture compression (BC7) and compressed regions arrived in v1, see below. The manifest already carries per-file sha256.

## v1 additions

The v1 additions are backward compatible: `FileHeader.Version` stays 0, and a v0 pack is a valid v1 pack. Readers tell the new data apart by `PageHeader.Format` and `FileHeader.Kind`. The converter emits them with `--format bc7|b5g5r5a1`, `--compress-pages` and `--compress-regions`.

### BC7 atlas pages (`PixelFormat.Bc7 = 1`)

- `PageHeader.Format = 1`: each mip level is BC7 (BPTC, unorm, not sRGB) blocks of 16 bytes per 4×4 texels, row-major. Mips are stored back to back, mip0 first, as in RGBA8.
- A level of side `s` takes `ceil(s/4)² × 16` bytes. For a 2048 page that is 1 byte/texel, and `DataBytes` = 5,591,040 for 6 levels. Helpers: `AtlasPage.MipBytes/ChainBytes/MipOffset(size, mip, format)`.
- The mips are the converter's own alpha-aware box-filtered RGBA8 mips. Each one is encoded to BC7 separately, so mip content matches the RGBA8 pack before quantisation.
- **Alignment rule:** in a BC7 pack, every packed cell is aligned to 4 px and sized to a multiple of 4 on both axes. A cell is the sprite plus its gutter: 2 px on the left and top, and 2 px plus alignment padding on the right and bottom. That puts every cell's origin `(X − 2, Y − 2)` on a multiple of 4. Edge extrusion fills the whole cell, so at mip0 no 4×4 block holds texels from two sprites. At mip1 and below, blocks can span cells, which is the same bleed the RGBA8 mips already have. `SpriteEntry.X/Y` still point at the first sprite texel, inside the gutter.
- The converter encodes with BCnEncoder.Net (MIT) at `Fast`. Any block whose alpha-weighted error is below 28 dB PSNR is re-encoded at `Balanced`, and the better result is kept. This fixes the speckle the fast mode leaves on tiny high-contrast sprites. Fully transparent blocks reuse one pre-encoded block, and solid blocks are cached.
- Before encoding, the converter gives fully transparent texels in each 4×4 block the mean RGB of the block's visible texels; alpha stays 0. This keeps the encoder from spending endpoints on black, which otherwise crushes thin opaque features toward black. It also removes dark bilinear fringes. Renderers must not rely on transparent texels being RGB 0 in BC7 pages.
- RGBA8 packs use alignment 1, the v0 layout. A renderer must handle both formats per page, because a page carries its own format.

### 16-bit atlas pages (`PixelFormat.B5G5R5A1 = 2`)

- `PageHeader.Format = 2`: one little-endian `u16` per texel, with bits 0–4 B, 5–9 G, 10–14 R and 15 A. This is `SDL_GPU_TEXTUREFORMAT_B5G5R5A1_UNORM`, and it is UO's own 1555 colour layout. A level of side `s` takes `s² × 2` bytes, and a 2048 page with 6 levels is 11,182,080 bytes.
- **mip0 is bit-exact to the client data:** every visible texel carries the source 16-bit colour with A=1, and transparent texels are 0. The converter verifies this exactly, gutters included. Decoders should expand 5→8 bits with ClassicUO's table (`HuesHelper.Color16To32`: 0x00, 0x08, …, 0x83, 0x8B, …, 0xFF). It is close to, but not exactly, `round(c·255/31)`. The converter's `(v·31+127)/255` and `v >> 3` both invert that table exactly.
- Lower mips come from the RGBA8 alpha-aware mips. Each colour channel is rounded to 5 bits (`(v·31+127)/255`), and alpha is 1 when `a ≥ 128` (alpha-tested, no partial alpha).
- It is lossless for UO art, so hue and partial-hue behaviour, including the exact-gray test, are identical to the original client. It costs twice the bytes of BC7.
- Alignment is 1, as in RGBA8 (no blocks).

### Per-file compression (`FileHeader.Kind`)

**Compression is chosen per file via `FileHeader.Kind`, and raw is always valid.** Readers must accept any mix of raw and compressed files within one pack. That lets shard owners ship fully raw packs for zip or binary-delta distribution, and it lets the default desktop profile ship compressed ones.

- **Pages** (`page_NNNN.bin`): with `Kind = 1`, everything after the FileHeader (`PageHeader` + chain) is one Brotli stream. `PageHeader.DataBytes` is the uncompressed chain size. `AtlasPage.Read` handles both kinds and decompresses straight into the caller's span; `AtlasPage.OpenChain` returns a stream positioned at mip0 for partial reads. Converter flag: `--compress-pages`.
- **Regions** (`r_<rx>_<ry>.bin`): see below. Converter flag: `--compress-regions`.
- Other files (sprite tables, tiledata, hues) are small and always raw in v1.
- **Desktop default profile:** `--format b5g5r5a1 --compress-pages --compress-regions`.

### Compressed regions (`FileHeader.Kind = 1`)

- `Kind = 0`: v0 layout.
- `Kind = 1`: the 16-byte `FileHeader` is followed by **one Brotli stream** (written at quality 5). It decompresses to exactly the bytes a Kind=0 file has after its `FileHeader`: the `RegionHeader`, the offset table, and the block records.
- Block offsets are still relative to the start of the logical, uncompressed file, so they include the 16-byte header. The simplest reader rebuilds the logical file (the header plus the decompressed payload) and parses it as Kind 0. `RegionFile.Read/Parse` do this transparently. `RegionFile.Decompress(bytes)` returns the logical bytes as a valid Kind=0 file, with the Kind field reset to 0.

### Distribution and chunked compression (decided 2026-10-24; Kind=2 not implemented yet)

**Background measurements.** Measured on an i9-12900K with the 16-bit pack (`Uo.Render.exe --bench-formats`, 4K):
- One 2048² 16-bit page is 10.7 MiB raw and 1.4–4.6 MiB compressed with Brotli q5.
- `BrotliDecoder` decodes about 450–700 MB/s on one core. That is 14–32 ms per page, even for a nearly empty page, because the cost scales with output size.
- A raw page read takes about 3 ms on a worker.
- The render thread's upload cost per page is 0.3–0.4 ms in both cases.
- Frame-time p99 is identical for raw and Brotli packs. The decode cost shows up only as latency for pages that aren't resident yet (login, teleport): sprites appear late.

**Rules for implementers:**
1. **Brotli for transport only (desktop).** The CDN and bundles carry Kind=1 files. The installer or patcher rewrites each Kind=1 page to Kind=0 once, after verifying the sha256 of the *compressed* file the manifest names. The runtime then reads raw. Readers must still accept any kind, because raw packs are always valid.
2. **Chunked Brotli (`FileHeader.Kind = 2`) for compressed-at-rest platforms** (mobile app storage, browser OPFS/IndexedDB). The logical payload is the same as for Kind 0/1: every byte after the `FileHeader`. It is split into fixed-size chunks that compress independently, so N workers can decode one page at once and a reader can decode just the first chunk to get the `PageHeader`.

   | Field | Type | Meaning |
   |---|---|---|
   | `ChunkBytes` | u32 | uncompressed chunk size; 1 MiB (1,048,576) by default; the last chunk may be shorter |
   | `LogicalBytes` | u64 | total uncompressed payload size |
   | `ChunkCount` | u32 | `ceil(LogicalBytes / ChunkBytes)` |
   | `CompressedSize[ChunkCount]` | u32[] | compressed size of each chunk; offsets are prefix sums from the end of this table |
   | chunks | bytes | `ChunkCount` independent Brotli streams; chunk `i` decodes to logical bytes `[i·ChunkBytes, (i+1)·ChunkBytes)` |

   - Decode each chunk straight into the staging buffer at its logical offset, so no intermediate copy is needed. Chunk 0 contains the `PageHeader` (and for regions the `RegionHeader` and offset table).
   - With 8 workers, latency for a 10.7 MiB page drops from about 25 ms to about 3–4 ms.
   - The compression ratio cost of 1 MiB chunks is small, because Brotli's window is already limited (q5 uses lgwin 22, i.e. 4 MiB). Measure this when implementing.
   - The same layout works for regions. Region payloads are small (2–5 ms decode), so Kind 1 stays fine for them.
3. **Residency is the real mobile lever, not lossy compression.** 24 resident layers of 16-bit 2048² pages take 255 MiB of video memory. Mobile should use fewer layers or 1024² pages. The format allows any page size, but the converter still needs a `--page-size` option.

### v2: page packing by co-occurrence (planned)

**Problem.** Today sprites are packed into pages roughly in id order, so one visible sprite pulls in a whole 10.7 MiB page. Britain touches 9 of the 35 pages.

**Goal.** Put sprites that appear on screen together on the same pages, so a screen touches few pages and walking rarely crosses into a new one.

**How to determine co-occurrence** (all offline, in the converter, from map data):
1. **Sample windows.** Tile every facet into screen-sized windows, e.g. 32×32 tiles, which is about a 1080p view at zoom 1, overlapping by half. For each window, record the set of land, texmap and static ids it uses, with multis expanded to their components. Each window is one "scene".
2. **Weight by frequency.** Ids used in more than ~25% of windows (common grass, dirt, walls) are **global**. Pack them first into a small number of pages that stay resident permanently. They co-occur with everything, so grouping them elsewhere would only add noise.
3. **Cluster the rest.** Use a cheap ordering instead of solving a graph partition:
   - give each remaining id a feature, the centroid of the windows it appears in;
   - order all ids along a Hilbert curve over the facets laid side by side (facet-major), using those centroids;
   - fill pages in that order.

   Nearby content, such as a town's buildings and decor, lands on the same pages. Optionally improve the order with a few passes of greedy swaps that reduce the metric below.
4. **Non-map content** isn't in the map, so it uses heuristics. Item graphics seen only from the network (the server spawns them) are grouped by tiledata category and id range. Animations are grouped per body: all actions and directions of one body together, popular bodies first (from mobtypes and server spawn data if available).
5. **Metric.** Report **average and p95 pages touched per window** and **page-change rate along random walks**, before and after. The converter prints these so a shard can see the effect of its custom art. This is the acceptance test for the v2 packer.

The page format doesn't change. Only the sprite-to-page assignment changes, which the sprite table already records, so v2 packs stay readable by v1 renderers.

### v1 NoDraw flags (`TilePackFlags`, in `packFlags`)

ClassicUO hides some tiles with code rather than tiledata flags. The converter evaluates that code once and stores the result. A v0 pack has 0 here, so a renderer that honours the flags should keep its own id rules as a fallback.

| Bit | Name | Meaning |
|---|---|---|
| 0 | `NoDraw` | ClassicUO never draws the tile. |
| 1 | `NoDrawUnlessGargoyle` | Static hidden unless the player is a gargoyle. |
| 2 | `NameNoDraw` | The tiledata name starts with `nodraw` (case-insensitive). For land this is informational only. |

- **Land** (`Land.AllowedToDraw = graphic > 2`): `NoDraw` for ids 0, 1 and 2. ClassicUO never checks land names. In 7.0.103 the only land named `NODRAW` is 0x0002, which the id rule already covers. 0x1DB..0x1E0 are `NoName`/`dirt` and drawn normally. 0x1AE..0x1B5 and 0x1DB appear only in ClassicUO's Pathfinder (walkability), not in drawing. Season graphic swaps are runtime, so the renderer applies the `> 2` rule to the swapped graphic.
- **Statics** (static-data part of `GameObject.CanBeDrawn`, evaluated in this order):
  - 0x0001, 0x21BC and 0xA1FE..0xA201: `NoDraw`.
  - 0x9E4C, 0x9E64, 0x9E65 and 0x9E7D: `NoDraw` only with Background or Surface. The name is not checked.
  - 0x63D3, and 0x2198..0x21A4: `NoDraw`.
  - 0x0F65 (easel) is drawn on clients older than 6.0.14.4.
  - A name starting with `nodraw` gives `NoDraw`.
  - TileFlag `NoDiagonal` (0x02000000) gives `NoDraw`, or `NoDrawUnlessGargoyle` when the tile also has `Animation`.
- 7.0.103 counts: land 3 `NoDraw`; statics 31 `NoDraw`, 1 `NoDrawUnlessGargoyle`, 20 named `nodraw`.
- These flags apply to map statics and to items or multis that ClassicUO passes through `CanBeDrawn`. Multi components use their own `> 2` test.

## v1 animations

Mobiles, equipment and corpses are body animations. The converter emits them only with the `anims` stage (on by default, `--no-anims` to skip, `--only anims` / `--anims-only` to add them to an existing pack). This section covers that output.

```
<pack>/anim/
  anim.bin          AnimTable: body table, direction records, frame records
  animdata.bin      animdata.mul records (animated static art)
  page_NNNN.bin     atlas pages, same page format as art (PixelFormat and Kind per page)
```

**Pages** use the art page format unchanged: 2048², 6 mips, 2 px extruded gutter, `PixelFormat` as chosen by `--format` (BC7 cells 4-aligned), and Brotli per file with `--compress-pages`. B5G5R5A1 mip0 is bit-exact to the client's 16-bit colours. Pixels are unhued; hues apply in the shader as for art. Frames are packed in body → action → direction → frame order, so one body's frames share a few pages. Identical frames (by trimmed pixels and trim) share one rect.

**`anim.bin`** = `FileHeader` (magic `ANIM`), `bodyCount i32`, `dirCount i32`, `frameCount i32`, then `AnimBodyEntry[bodyCount]` (index = body id), `AnimDirEntry[dirCount]` and `AnimFrameEntry[frameCount]`. The reader is `AnimTableFile.Read`, and `AnimTableFile.Frames(…, body, action, dir)` looks up one direction.

`AnimBodyEntry` (24 bytes):

| Field | Type | Meaning |
|---|---|---|
| firstDir | u32 | Record of (action 0, dir 0). Record (a, d) is at `firstDir + a*8 + d`. |
| actionCount | u16 | 0 = the body has no frames of its own. Actions ≥ actionCount have no frames. |
| type | u8 | ClassicUO `AnimationGroupsType`: 0 monster, 1 sea monster, 2 animal, 3 human, 4 equipment. |
| source | u8 | bit 7 = `AnimationFrame<n+1>.uop`, else `anim<n+1>.mul` (n = low bits). 0xFF = none. |
| flags | u32 | mobtypes.txt flags (ClassicUO `AnimationFlags`, e.g. 0x10000 UOP). Bit 31 = listed in mobtypes. |
| sourceBody | u16 | Body id inside the source file (the Bodyconv.def target), else the body itself. |
| aliasBody, aliasHue | u16, u16 | Body.def replacement, already chained (0xFFFF = none). Draw aliasBody's entry, its type and its flags instead, with aliasHue. |
| corpseBody, corpseHue | u16, u16 | Corpse.def replacement for corpses (0xFFFF = none). |
| mountHeight | i8 | Bodyconv.def mounted height offset. |
| pad | u8 | |

`AnimDirEntry` (8 bytes): firstFrame `u32`, frameCount `u16` (absent frames included; 0 = no animation), storedDir `u8`, flags `u8` (bit0 = mirrored). There are **8 records per action, one per UO direction** 0..7 (N, NE, E, SE, S, SW, W, NW). The client stores only 5 directions. Directions 3..7 are stored directions 0..4. Directions 2, 1 and 0 reuse stored 1, 2 and 3 with the mirrored flag (ClassicUO `GetAnimDirection`). Mirrored records point at the same frame range, so no pixels are duplicated.

`AnimFrameEntry` (24 bytes): a `SpriteEntry` (page, rect, trim; origW/origH = the client frame's size; page 0xFFFF = empty frame), then centerX `i16`, centerY `i16`, the client frame's centre.

**Anchoring** (ClassicUO `MobileView`): take the mobile's tile screen position (x, y), with z applied as for statics, and set the anchor to `ax = x + 22`, `ay = y + 22 − 3`.
- Unmirrored: the frame's top-left is `(ax − centerX, ay − (origH + centerY))`. The trimmed rect sits at `+ (trimX, trimY)`.
- Mirrored: the frame's left is `ax − (origW − centerX)`. Draw the trimmed rect flipped horizontally at x offset `origW − trimX − w`. Its y is the same as unmirrored.

**Resolution done at conversion time** (as ClassicUO's `AnimationsLoader.GetIndices` + `Animations.GetAnimationFrames`):
- Bodies 0..max(2047, highest id in mobtypes/Bodyconv/Body/Corpse.def) are scanned.
- **mobtypes.txt** gives the type and flags. Flag 0x10000 means UOP: frames come from AnimationFrame*.uop.
- **AnimationSequence.uop** group replacement is already applied. Action `a` of a UOP body holds exactly what ClassicUO draws for `a`, including its double application of the replacement. The renderer must not remap UOP actions.
- **Bodyconv.def** is resolved with every column enabled (all expansion flags on, as a modern server sends at login). The body's frames are the converted ones, and `sourceBody`/`source` record where they came from.
- **Body.def / Corpse.def** stay as aliases, because the original body still has its own frames (corpses use them when there is no Corpse.def entry). As in ClassicUO, they only apply to plain anim.mul bodies, never to UOP or Bodyconv bodies.
- Equipment layers use the item's animation id as the body. ClassicUO draws equipment only when that body's type is equipment or human, and it does that check at runtime.
- **verdata.mul is never read** (see below).

**Frame counts.** MUL directions carry their own frame count. For UOP, ClassicUO splits a group's frames into `round(n / 5)` per direction, with a minimum of 10 for the equipment type, and fills gaps in frame ids with empty frames. The table stores the result. Empty frames (zero size, or fully transparent) have page 0xFFFF, keep their size and centre, and count toward frameCount.

**`animdata.bin`** = `FileHeader` (magic `ANDT`), `count i32`, then `AnimDataEntry[count]` (70 bytes, sorted by graphic, only graphics with frames): graphic `u16`, frameCount `u8`, frameInterval `u8`, frameStart `u8`, unknown `u8`, frames `i8[64]` (graphic offsets). The reader is `AnimDataFile.Read`.

**Decoding and verification.** The converter decodes frames with a managed port of ClassicUO's `ReadUOPAnimationFrames`/`ReadMULAnimationFrames`. The loader can't run on parallel workers: its MUL path reads through a pointer to an unpinned array, and concurrent native zlib calls crash. Malformed blocks, which would crash or hang the loader, are stored as empty. The verify step decodes a sample (about 1 in 9 distinct directions, plus every direction of the debug bodies) with the real ClassicUO loader, serially, and compares frame count, size, centre and pixels. Pixels must be bit-exact for RGBA8 and B5G5R5A1; BC7 reports PSNR instead.

Not converted: gump paperdoll art, Equipconv.def (runtime: equipment graphic swaps per body, from the server item graphic), Anim1.def/Anim2.def group replaces (used by ClassicUO's mount and group fallbacks at runtime), sitting tables, and animinfo.mul.

### Patch formats: unsupported by design

`verdata.mul` and `mapdif*/mapdifl*/stadif*/stadifi*/stadifl*` are never read. Shard owners who rely on patches must merge them into the base files with their own tools before converting. The `map*x`/`statics*x`/`staidx*x` variants (`--xfiles`) are base files, not patches, and are supported.

### JSON

`manifest.json` and `facet.json` use camelCase property names and are read case-insensitively (`ManifestFileIo`, `FacetFileIo`).
