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

- `LandTileData` (12 bytes): flags `u64`, texId `u16`, pad `u16`.
- `StaticTileData` (12 bytes): flags `u64`, height `u8`, layer `u8`, pad `u16`.
- Flags keep UO's `TileFlag` bit values.

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

### Patch formats: unsupported by design

`verdata.mul` and `mapdif*/mapdifl*/stadif*/stadifi*/stadifl*` are never read. Shard owners who rely on patches must merge them into the base files with their own tools before converting. The `map*x`/`statics*x`/`staidx*x` variants (`--xfiles`) are base files, not patches, and are supported.

### JSON

`manifest.json` and `facet.json` use camelCase property names and are read case-insensitively (`ManifestFileIo`, `FacetFileIo`).
