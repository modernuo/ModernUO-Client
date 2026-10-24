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

GPU texture compression, gumps, animations, multis, sounds, fonts, normal and height maps, compressed regions, and content-addressed CDN chunking. The manifest already carries per-file sha256.
