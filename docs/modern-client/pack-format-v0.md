# Pack Format v0 (PoC)

> **History.** The authoritative format 1.0 spec is [pack-format.md](pack-format.md). This file records how the format got there: v0/v1 additions, measurements and dead ends.

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
- **mip0 is bit-exact to the client data:** every visible texel carries the source 16-bit colour with A=1, and transparent texels are 0. The converter verifies this exactly, gutters included. **Canonical 5→8-bit expansion (superseded: format 1.0 uses bit replication `(c<<3)|(c>>2)`, see pack-format.md): `round(c·255/31)` = `(c·527 + 23) >> 6`.** This is the value a GPU produces when it samples `B5G5R5A1_UNORM` natively, so CPU expansion (`PixelExpand`, the fallback for GPUs without 16-bit sampling) uses the same formula. Unlit, unfiltered texels then render identically on both paths. Lit or filtered pixels can still differ by 1–2/255, because native sampling feeds the shader exactly `c/31` while an 8-bit texel is within half a step of it; GPU vendors differ by that much in filtering anyway. ClassicUO's `HuesHelper` table is `floor(c·255/31)`, at most 1 lower per channel, and is no longer used for expansion. Both `(v·31+127)/255` and `v >> 3` invert either expansion exactly.
- Lower mips come from the RGBA8 alpha-aware mips. Each colour channel is rounded to 5 bits (`(v·31+127)/255`), and alpha is 1 when `a ≥ 128` (alpha-tested, no partial alpha).
- It is lossless for UO art, so hue and partial-hue behaviour, including the exact-gray test, are identical to the original client. It costs twice the bytes of BC7.
- Alignment is 1, as in RGBA8 (no blocks).

### Per-file compression (`FileHeader.Kind`)

**Compression is chosen per file via `FileHeader.Kind`, and raw is always valid.** Readers must accept any mix of raw and compressed files within one pack. That lets shard owners ship fully raw packs for zip or binary-delta distribution, and it lets the default desktop profile ship compressed ones.

- **Pages** (`page_NNNN.bin`): with `Kind = 1`, everything after the FileHeader (`PageHeader` + chain) is one Brotli stream. `PageHeader.DataBytes` is the uncompressed chain size. `AtlasPage.Read` handles both kinds and decompresses straight into the caller's span; `AtlasPage.OpenChain` returns a stream positioned at mip0 for partial reads. Converter flag: `--compress-pages`. `Kind = 2` (chunked Brotli, below) is written with `--compress-pages=chunked [--chunk-bytes n]`; `AtlasPage.Read/OpenChain` and `RegionFile.Decompress` read it.
- **Regions** (`r_<rx>_<ry>.bin`): see below. Converter flag: `--compress-regions`.
- Other files (sprite tables, tiledata, hues) are small and always raw in v1.
- **Desktop default profile:** `--format b5g5r5a1 --compress-pages --compress-regions`.

### Compressed regions (`FileHeader.Kind = 1`)

- `Kind = 0`: v0 layout.
- `Kind = 1`: the 16-byte `FileHeader` is followed by **one Brotli stream** (written at quality 5). It decompresses to exactly the bytes a Kind=0 file has after its `FileHeader`: the `RegionHeader`, the offset table, and the block records.
- Block offsets are still relative to the start of the logical, uncompressed file, so they include the 16-byte header. The simplest reader rebuilds the logical file (the header plus the decompressed payload) and parses it as Kind 0. `RegionFile.Read/Parse` do this transparently. `RegionFile.Decompress(bytes)` returns the logical bytes as a valid Kind=0 file, with the Kind field reset to 0.

### Distribution and chunked compression (decided 2026-10-24; Kind=2 implemented)

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
   - **Implementation** (`poc/Uo.Pack/ChunkedBrotli.cs`): `ChunkedBrotli.Write` (q5, lgwin 22), `ChunkedBrotli.Decode` (single-threaded, in memory), `ChunkedBrotliStream` (sequential; used by `AtlasPage.OpenChain`), and `ChunkedBrotliDecoder(workers)`: persistent workers plus the caller pull chunks from a counter, `RandomAccess.Read` their bytes and decode straight into the caller's span. `AtlasPage.Read(path, span, decoder)` uses it; chunk 0 goes through a worker scratch buffer to split off the `PageHeader`. Steady state allocates only the `SafeFileHandle` (112 B per page read). Measured numbers are in "Animation storage investigation".
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

## Animation storage investigation (2026-10-24)

**Problem.** The 16-bit anim pages are 628 pages of 2048², 6 mips: 6.7 GiB raw, 1.1 GB Brotli. "Unpack to raw at install" works for art, tex and maps (435 MB), not for this. Three options were measured on the full 7.0.103 client (963 bodies, 993k frames, 951,693 packed rects), i9-12900K, `--threads 12`. Every number below comes from a full anim run per variant (about 1 min each), not from extrapolation, unless it says "est.".

Experimental packs: `uo-packs/anim-exp-b16-1024`, `anim-exp-idx-2048`, `anim-exp-idx-1024` (pages Kind 1), `anim-exp-chunked` and `anim-exp-idx-2048-chunked` (the same pages re-encoded as Kind 2, 1 MiB chunks). Tools: `Uo.Converter --bench-chunked <anim dir> <out dir>`, `--anim-report <pack,pack>`, `--compare-indexed <indexed pack> <b16 pack>`, `--filter-ref <indexed pack> <b16 pack> <png dir>`.

### Experimental format: `PixelFormat.Indexed8 = 3` (anims only, `--anim-format indexed8`)

- **Source.** Confirmed in `AnimationsLoader`: MUL stores one 256 × u16 palette per stored (body, action, direction) block; UOP stores one per frame, in the 512 bytes before each frame. Transparency is run coverage: texels no run covers are transparent. In UOP, palette value 0 is also transparent (`alphaCheck`). In MUL it is opaque black, and MUL palettes often hold 256 distinct colours. So a source index can't double as "transparent".
- **Canonical palette.** Entry 0 = 0 (transparent). Then come the source palette's distinct visible colours, as B5G5R5A1 with A=1, in order of first appearance, restricted to colours that some frame drawn with that palette actually uses. The converter collects that usage in a pre-pass over all frames, keyed by palette content, so every user of a source palette gets the same canonical palette. This frees index 0 in every case: 0 of 951,693 frames overflow 255 colours. The per-frame fallback (palette = the frame's own used colours) is never hit.
- **Dedupe.** 553,140 source palette instances (MUL directions + UOP frames) are 38,056 distinct by content and 37,862 after canonicalisation. Palette ids fit u16.
- **`anim/palettes.bin`** (magic `APAL`): `paletteCount i32`, `frameCount i32`, `u32 FramePalette[frameCount]` (per `AnimFrameEntry`, `uint.MaxValue` = absent), then `u16 Palettes[paletteCount × 256]`. It is 22.3 MiB raw (18.5 MiB of palettes) and 5.8 MiB Brotli. Frames with identical pixels share one rect and one palette. `anim.bin` is unchanged.
- **Page.** `Format = 3`, `MipCount = 1`, one byte per texel. Cells use alignment 1, packing is identical to the 16-bit pack, and page N of an Indexed8 pack covers the same rects as page N of the 16-bit pack.
- **Gutter.** It holds **real neighbour indices**: the frame's own edge indices are extruded 2 px, exactly like the 16-bit pack. With the gutter, a bilinear footprint at mip0 never leaves the frame's cell, so all 4 taps use the same palette, the instance's own. Texels outside every cell are index 0.
- **Mips.** Variant (a) has none. Variant (b) (`--indexed-mips b16`, the default) adds `mips_NNNN.bin`: a normal B5G5R5A1 page of side S/2 that holds mips 1..n of the same page. It is byte-identical to the 16-bit pack's mips 1+.
- **Lossless, verified three ways:**
  1. `--compare-indexed` rebuilds `palette[index]` for every texel of all 628 pages (2.63 G texels, gutters included) and compares it with `classic-v1-16`'s B5G5R5A1 mip0: 0 mismatches and 0 stray indices. The sidecar matches mips 1+ on all 877 M texels. The 1024 run matches `anim-exp-b16-1024` the same way.
  2. The converter self-checks every page against its own B5G5R5A1 encoding: 0 mismatches.
  3. The verify step decodes about 112k frames with the ClassicUO loader and compares them to the Indexed8 pack: all bit-exact.

### Results

**Totals** (all 628 / 2652 pages; Kind 1 = Brotli q5; "+pal" adds palettes.bin):

| Variant | Pages | Packing (sprite / cell texels) | Raw (install, unpacked) | Kind 1 at rest | Kind 2, 1 MiB chunks |
|---|---|---|---|---|---|
| B5G5R5A1 2048, 6 mips (shipping) | 628 | 75.4% / 84.9% | 6,697 MiB | 1,108 MiB | 1,149 MiB (+3.7%) |
| B5G5R5A1 1024, 5 mips | 2,652 | 71.4% / 80.4% | 7,065 MiB | 1,127 MiB | ≈1,170 MiB (est.) |
| Indexed8 2048, (a) no mips | 628 | 75.4% / 84.9% | 2,512 + 22 pal | 621 + 6 | 630 (+1.5%) |
| Indexed8 2048, (b) + 16-bit mips | 628 + 628 | same | 4,185 + 22 | 978 + 6 | ≈ +1.5% (est.) |
| Indexed8 1024, (a) no mips | 2,652 | 71.4% / 80.4% | 2,652 + 22 | 628 + 6 | — |
| Indexed8 1024, (b) + 16-bit mips | 2,652 + 2,652 | same | 4,413 + 22 | 999 + 6 | — |

**Chunk size** (16-bit 2048 pages, size vs Kind 1): 256 KiB +8.6%, 1 MiB +3.7%, 4 MiB +0.7%. Indexed8 pages: +3.1%, +1.5% and +0.0%.

**Decode latency per page** (warm cache, median of 32 pages × 5 runs). Kind 1 is an in-memory one-shot decode. Kind 2 is `AtlasPage.Read(path, span, decoder)`, including the file read.

| Page | Kind 1, 1 thread | Kind 2, 1 worker | 2 workers | 4 workers | 8 workers |
|---|---|---|---|---|---|
| 16-bit 2048 (10.7 MiB) | 14.8 ms | 17.9 ms | 10.0 ms | 5.7 ms | 3.9 ms |
| Indexed8 2048 (4 MiB) | 10.0 ms | 10.8 ms | 5.8 ms | 3.2 ms | 3.2 ms |
| 16-bit / Indexed8 1024 (2.7 / 1 MiB) | ≈4 / ≈2.5 ms (est.: decode cost scales with output size) | | | | |

A 1 MiB Indexed8 1024 page is a single 1 MiB chunk, so Kind 2 only helps it with 256 KiB chunks. In practice a creature needs several pages, and decoding those in parallel across pages needs no chunking.

**Bytes to show one creature.** These are pages touched by all frames of the listed actions in all 8 directions; "at rest" is the Kind 1 file bytes of those pages. Move+idle means: human = walk/run armed and unarmed plus stand (actions 0–4); monster = walk and stand (0, 1); animal = walk, run and stand (0–2). The monster set is ogre, zombie, daemon, orc, ettin, lich, skeleton, troll, dragon, horse, timber wolf and slime.

| Variant | Human 400, move+idle: pages / at rest / VRAM | Monster set, move+idle | Monster set, all actions |
|---|---|---|---|
| 16-bit 2048 | 1 / 2.2 MiB / 10.7 MiB | 12 / 21.9 / 128 MiB | 24 / 40.2 / 256 MiB |
| 16-bit 1024 | 1 / 0.5 / 2.7 | 17 / 8.1 / 45 | 72 / 29.0 / 192 |
| Indexed8 2048 (a) | 1 / 1.2 / 4.0 | 12 / 12.2 / 48 | 24 / 22.5 / 96 |
| Indexed8 2048 (b) | 1 / 1.9 / 6.7 | 12 / 19.3 / 80 | 24 / 35.5 / 160 |
| Indexed8 1024 (a) | 1 / 0.3 / 1.0 | 17 / 4.5 / 17 | 72 / 16.2 / 72 |
| Indexed8 1024 (b) | 1 / 0.5 / 1.7 | 17 / 7.1 / 28 | 72 / 25.7 / 120 |

- **Big bodies at 1024.** 1024 pages cost large bodies with many actions more pages: dragon all actions takes 7 → 27 pages and daemon 6 → 20. They still cost fewer bytes (dragon 74.6 → 71.9 MiB VRAM in 16-bit).
- **WebGPU fallback.** VRAM for the 16-bit rows doubles on the RGBA8 expansion path (WebGPU, Intel Macs). Indexed8 has no such fallback: it is R8_UINT everywhere.
- **Palettes in VRAM.** Each creature needs a handful of palettes, a few KiB. All 37,862 palettes take 18.5 MiB, small enough to keep resident as one storage buffer.

**Filtering reference** (`debug/anim/indexed_zoom150.png`, `indexed_zoom050.png`; body 400, walk, dir 4, frame 3). The panels are hardware-style bilinear on the 16-bit page, then manual 4-tap on Indexed8, then |diff|×8. Both go through the renderer's alpha test and un-premultiply.
- **Zoom 1.5:** identical (0 of 3,010 pixels differ). Manual filtering of Indexed8 at mip0 is the same image as hardware bilinear of the 16-bit page.
- **Zoom 0.5:** the 16-bit path uses mip1, and Indexed8 (a) filters mip0. 153 of 348 pixels differ, by at most 5/255. At exactly 0.5 a 4-tap mip0 sample is close to a 2×2 box. Below 0.5 variant (a) undersamples and shimmers; variant (b) (4th panel) is identical to the 16-bit path.

### Comparison

| | 16-bit 2048 (today) | 16-bit 1024 | Indexed8 1024 (a) | Indexed8 1024 (b) |
|---|---|---|---|---|
| At rest (all anims) | 1.11 GB | 1.13 GB | 0.63 GB | 1.0 GB |
| Install, raw | 6.7 GiB | 7.1 GiB | 2.6 GiB | 4.4 GiB |
| Show one human (at rest / VRAM) | 2.2 / 10.7 MiB | 0.5 / 2.7 MiB | 0.3 / 1.0 MiB | 0.5 / 1.7 MiB |
| Decode one page, 1 thread | 15 ms | ≈4 ms | ≈2.5 ms | ≈2.5 + 1.5 ms |
| Shader / runtime complexity | none (exists) | none (page size is data) | new path: R8_UINT array, palette buffer, manual 4-tap, per-instance palette id; zoom < 0.5 aliases | (a) plus a second texture array for mips and LOD switching |
| Hue fidelity | exact (5-bit gray test on texels) | exact | exact, and hue can apply per tap before blending | same as (a) at zoom ≥ 1; filtered mips as today |

### Recommendation

1. **Use 1024² anim pages now** (`--anim-page-size 1024`, art stays 2048). This is a converter flag with no runtime cost.
   - Showing one creature costs 4× fewer bytes: a human takes 0.5 MiB at rest and 2.7 MiB of VRAM, instead of 2.2 and 10.7.
   - The monster set needs 45 instead of 128 MiB of VRAM.
   - The total grows by 5% raw and 2% at rest, and page count rises 4.2×. A 1024 page is still a fine streaming unit.
2. **Adopt Indexed8 (variant a, no stored mips) as the anim format once the renderer path exists.** It is lossless (verified bit-exact on every texel) and halves the bytes again: 0.63 GB at rest and 2.6 GiB raw for all anims, 1 MiB of VRAM per resident 1024 page, and no RGBA8 fallback doubling on WebGPU. Its cost is the shader work below and a 22 MiB palette table (5.8 MiB Brotli). Clamp anim zoom-out filtering to mip0. If zoom below 0.5 becomes a requirement, build mips at load time rather than shipping variant (b), which gives back 60% of the saving at rest.
3. **Keep anims compressed at rest** (owner: acceptable if decode is fast). Install-time unpacking still writes 2.6 GiB. Decode an Indexed8 1024 page on one worker (≈2.5 ms) and decode a creature's pages in parallel. Kind 2 with 1 MiB chunks pays off for 2048 pages (15 → 4 ms with 8 workers, +1.5–3.7% size) and for browser/mobile single-page latency. With 1024 Indexed8 pages it needs 256 KiB chunks (+3.1%). Kind 1 plus parallelism across pages is enough on desktop.

### Indexed8 shader design note (for the renderer task)

- **Resources.** An `R8_UINT` `Texture2DArray` holds anim pages (same residency manager; 1 MiB per 1024 layer). Palettes go in one storage buffer: `palettes.bin`'s u16 table uploaded as-is, read through a `ByteAddressBuffer`, with entry `(palId × 256 + idx) × 2`. All palettes fit (18.5 MiB). A per-body subset can be streamed later if that matters.
- **Palette id is a per-instance field, not a frame-table lookup.** The CPU already reads the `AnimFrameEntry` to fill the instance (uv, w, h). It reads `FramePalette[frame]` at the same time and writes the u16 into the instance, for example in the `corners` word, which only stretched land uses, flagged `FLAG_INDEXED`. The GPU then does no extra indirection and needs no frame id. The gutter guarantees that every tap of a mip0 footprint lies in the frame's own cell, so one palette per instance is always correct.
- **Filter.** Compute the texel position `t = uv·S − 0.5`, `i = floor(t)`, `f = frac(t)`. Do 4 `Load`s of the index, then 4 palette loads. A tap with index 0 is transparent (weight goes to alpha only). For sharp-bilinear at zoom ≥ 1, reuse today's `off` remap on `f`.
- **Hue after the palette lookup, per tap, before blending.** Unpack each tap's 5-bit RGB as integers. The partial-hue gray test is exact (`r5 == g5 == b5`, no tolerance), and the hue row's shade index is `r5`, as ClassicUO does it. Then blend the hued taps: `rgb = Σ wᵢ·rgbᵢ / Σ wᵢ` over visible taps, `a = Σ wᵢ` over visible taps. Alpha-test at 0.5 and multiply by light. This is more correct than today's filter-then-hue, which hues blended (non-gray) edge texels inconsistently. It needs no un-premultiply step.
- **Zoom < 1.** Use the same 4 taps at mip0. At 0.5 the reference differs from mip1 by at most 5/255. Lower zooms alias.
- **Cost.** 4 integer loads + 4 buffer loads + 4 hue loads per hued fragment, versus 1 filtered sample + 1 hue load today. Mobiles are a small share of fragments.
