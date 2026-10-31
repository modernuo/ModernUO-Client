# ClassicUO Render and Asset Pipeline (as of this fork)

> **History.** The fork as it was on 2026-10-23, before the pack and the Gpu world renderer: the starting point the PoC set out to fix. The Legacy world renderer still draws roughly this way; asset loading no longer does (see [data-formats.md](data-formats.md) and [pack-format.md](pack-format.md)). Line numbers are of that date.

Paths are relative to `src/`. This fork includes a chunk-mesh path for land and statics that upstream ClassicUO may not have.

## 1. Frame structure and draw order

- **Game loop:** FNA `Game.Update`/`Draw` (`ClassicUO.Client/GameController.cs:381`, `:464`). The FPS cap is `SuppressDraw` plus `Thread.Sleep(1)` (`:421-443`).
- **Draw sequence:** lights render into a render target, then the world into another (`GameScene.cs:925`, `DrawWorld` `:953`), then the UI into its own target. `RenderTargets.Draw` composites them (`ClassicUO.Renderer/RenderTargets.cs:94`).
- **Render lists are rebuilt from scratch every frame.** `FillGameObjectList` (`GameScene.cs:548`) walks every visible 8×8 chunk, every cell and every object in it, then buckets them via `RenderLists.Add` (`RenderLists.cs:83`).
- **Ordering:** there's no per-frame CPU sort.
  - Each tile's object list is kept sorted by `PriorityZ` at insert time (`Chunk.cs:272-321`).
  - A float depth `(x+y) + (127+z)*0.01` (`GameObjects/Views/View.cs:35-83`) goes into vertex Z, and the hardware depth buffer resolves it.
  - Submission order is fixed: land, statics, animations, effects, transparent objects, then gumps (`RenderLists.cs:199-244`).
- **Per-object virtual `Draw`** remains for everything not meshed. Land and statics can be baked into per-chunk static vertex buffers (`ChunkMesh.cs`, rebuilt on `IsDirty`, `GameScene.cs:645`).

## 2. Batching (`ClassicUO.Renderer/Batcher2D.cs`)

- **Batch size:** `MAX_SPRITES = 0x800`, i.e. 2048 quads per flush (`:19`).
- **Vertex format:** `PositionNormalTextureColor4`, 48 bytes per vertex, so **192 bytes per quad**, with no instancing (`:1503-1532`).
- **Flushes:** every texture change binds a texture and issues a new `DrawIndexedPrimitives` (`:1279-1321`). Changes of blend, stencil, sampler, scissor or effect state also force a flush (`:1374-1432`).
- **No stats:** `FlushesDone`/`TextureSwitches` exist but nothing displays them.
- **Shader:** one uber-shader, `shaders/IsometricWorld.fx`, vs_3_0/ps_3_0.
  - Modes are an `if` chain on a per-vertex value.
  - Hue is a lookup into a 16×1024 hue texture, indexed by the grayscale value.
  - Lighting is a fake directional factor from per-vertex normals.

## 3. Textures

- **Atlases:** `TextureAtlas` uses StbRectPack.
  - 4096² RGBA8 pages for art, gumps and animations; 2048² for texmaps, lights and fonts.
  - Append-only: a new page when full (`TextureAtlas.cs:45-49`). **No eviction**, no free list, no mips, no compression, no gutters.
  - Without gutters, any zoom or non-integer DPI path will bleed between neighbouring sprites.
- **Uploads:** synchronous on the main thread at first draw. `Art.Get` decodes the sprite, then `AddSprite` calls `SetDataPointerEXT` per sprite (`Art.cs:44-103`, `TextureAtlas.cs:53-56`).

## 4. Asset IO and decoding

- **Files:** memory-mapped (`ClassicUO.IO/MMFileReader.cs:18-36`), with UOP and MUL readers.
- **Decoding is lazy and synchronous on the render thread.** Art allocates a new `uint[w*h]` per sprite (`ArtLoader.cs:235,304`). Animations decode every frame of a direction at once (`Animation.cs:228-386`), allocating `byte[]`/`uint[]` buffers (`AnimationsLoader.cs:1309,1485,1535`). Verdata patches go through `VerdataAnimationBlocks` (`:385-467`).
- **CPU-side caches are never evicted:** `_spriteInfos`, the `_cache[body][action][dir]` arrays, and the `PixelPicker` masks.
- **No background loading pipeline.** The only background work is UltimaLive's writer, the world-map loader task, and an optional mouse thread.

## 5. Map

- `Map.GetChunk2` loads a chunk **synchronously, mid-frame**, the first time it's touched (`Map.cs:58-99`). `chunk.Load` creates heap-allocated `Land`/`Static` objects per tile (`Chunk.cs:97-139`).
- Chunks unused for 3000 ms are released, at most 50 per pass (`Map.cs:240-270`). A chunk pool exists, but GameObjects are linked lists of heap objects.

## 6. Lighting, picking, post-processing

- **Lights:** additive light sprites into a screen-sized render target, then a multiply or "darkness" blend over the world (`GameScene.cs:1058-1139`, `RenderTargets.cs:126-133`).
- **Absent:** normal maps, shadow maps, per-pixel dynamic lighting. Shadows are flat black sprites.
- **Picking is CPU-side.** Each object tests the mouse during draw against run-length-encoded pixel masks (`PixelPicker.cs`), using a linear span walk, a `Dictionary<ulong,int>` and a `List<byte>`.
- **Post-processing:** only xBR scaling. Fractional DPI uses a linear filter on the composite.

## Top weaknesses a new client must fix

1. Synchronous decode and upload on the render thread: hitches the first time a sprite or animation appears.
2. Atlases never evict, are uncompressed RGBA8 with no mips or gutters, and memory grows forever. Unworkable for streaming or HiDPI.
3. The full render-list rebuild every frame, plus a virtual `Draw` per object.
4. 192-byte quads with no instancing, and a flush on every texture or state change.
5. A float depth hack that can't support proper 2.5D lighting, occlusion or alpha.
6. A ps_3_0 uber-shader built as an `if` chain.
7. CPU-side picking whose caches grow without bound.
8. Monolithic caches with no lifetime management.
9. No draw-call or batch statistics.
