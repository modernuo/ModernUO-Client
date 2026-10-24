# ClassicUO Integration Plan (Phase 4)

From a read-only spike on 2026-10-24. File:line references are against FNA3D 25.09; re-check them after the 26.10 submodule update.

## Decisions this plan rests on

- **Packs replace MUL/UOP** for every converted asset. There is no dual path (owner).
- **Renderer integration: share FNA3D's SDL_GPU device** (option a), enabled by a small FNA3D patch that we carry in the submodule (option d).
  - Our world pass renders into ClassicUO's `RenderTargets.WorldRenderTarget`. `RenderTargets.Draw` then composites world, lights and UI exactly as it does now.
  - Porting the UI off FNA (option b) was rejected for now: `UltimaBatcher2D` is 1551 lines, 63 files import XNA graphics, there are 169 `batcher.Draw*` calls, and plugins draw through FNA too.
- **Picking stays on the CPU.** ClassicUO already picks during list fill (`CheckMouseSelection` → `PixelCheck`), independent of the renderer. It needs CPU-side pixels or masks from the pack.
- **Overheads, health bars and text stay UI** (FNA, `UiRenderTarget`).
- **UltimaLive is deferred.** Runtime map edits clash with immutable region files. It needs an overlay design later, or it gets dropped; the owner already rated it optional.

## Device sharing: facts and gotchas

- **The interop isn't implemented.** `FNA3D_GetSysRendererEXT`/`CreateSysTextureEXT` exist (FNA3D_SysRenderer.h:139-148), but the SDL_GPU driver stubs them (FNA3D_Driver_SDL.c:4073-4087, "TODO"), and FNA's C# side doesn't bind them.
- **The patch:** fill the sdlgpu member with the `SDL_GPUDevice*`, and add `FNA3D_SDLGPU_GetTextureEXT(FNA3D_Texture*) → SDL_GPUTexture*`. Then add C# externs, and reach `Texture.texture` through `[UnsafeAccessor]`, which is AOT-safe.
- **FNA owns the window and swapchain** (claims it at :4269, acquires and blits at `SwapBuffers` :1076-1140). Our renderer must never claim the window or acquire the swapchain.
  - We record into our own command buffer and submit it before FNA's render command buffer, which FNA submits at `SwapBuffers`. Queue order then guarantees our world is done before FNA samples it.
  - Colour format = the render target's format (R8G8B8A8). We own the depth texture.
- **The driver is chosen at runtime.** MojoShader reports only SPIR-V unless SDL3_shadercross is present, so the shared device is probably **Vulkan** on Windows. Uo.Render must pick shaders from the device it is handed; it already builds both DXIL and SPIR-V.
  - If FNA3D falls back to D3D11/OpenGL, there is no sharing, so we keep the legacy renderer as the fallback.
- **Render-target lifetime:** `EnsureSize` disposes and recreates the world RT on resize, so re-fetch the texture pointer every frame.

## Seam in ClassicUO's world path

- **Today:** `GameScene.Draw` → `PrepareLightsRendering` → `DrawWorld`. `DrawWorld` runs `FillGameObjectList` → `AddTileToRenderList`, which owns the roof, foliage, circle-of-transparency and picking rules, then `RenderLists.DrawRenderLists`.
- **New:**
  - keep `FillGameObjectList`/`AddTileToRenderList` as the authority for visibility, alpha and picking;
  - land and statics come from pack regions via Uo.Render's ChunkStore, plus per-frame alpha and hidden overrides keyed by (chunk, sprite);
  - dynamic objects come from the ECS extract.
- **Minimum scope:**
  - land (including stretched), statics, multis as statics, items, mobiles with equipment and mounts;
  - partial, spectral and shadow hues; roof, foliage and CoT alpha; hover highlight.
- **Deferred:** these keep drawing through FNA into the same RT after our pass: lights, weather, effects, the multi-placement preview, the selection rectangle, the death-screen grayscale.
- **New `SpriteInstance` fields:** alpha, spectral, shadow, effect-hued, mirror, highlight hue.

## Pack completeness needed before the MUL loaders can go

- **tiledata:** name, weight, quantity, animID, light index, hue, stacking offset. There are 72 uses of LightIndex/AnimID/Weight, plus name uses.
- **hues:** the raw 16-bit colour tables and TableStart/End, for CPU font and gump hueing.
- **radarcol:** for the minimap and world map.
- **animdata:** `AnimatedStaticsManager` reads the raw file. The pack has `AnimDataEntry`; port the reader.
- **multis, light textures, tileart.uop:** either keep them on MUL for now or pack them.
- **CPU pixels for picking:** decode pack pages on the CPU, or ship mask sidecars.

## Phased steps

Each step keeps the client runnable. The new world renderer sits behind `WorldRenderer = Legacy|Gpu` (default Legacy) until step 10.

1. **FNA bump and patch.**
   - Run the submodule update to 26.10 and re-check the SDL_GPU internals, then apply the device/texture patch and the C# externs.
   - Verify: ClassicUO logs the driver and device pointer, `GetTextureEXT(WorldRenderTarget)` is non-null, and legacy rendering is unchanged.
2. **AOT baseline.**
   - Publish the current client under NativeAOT, log in to the local ModernUO, walk around.
   - Record the trim-warning list and the frame time.
3. **Pack completeness (converter and spec):** the tables listed above. Verify bit-exact against the MUL loaders.
4. **Pack-backed tiledata, hues and maps** behind the existing APIs (`TileDataLoader.LandData/StaticData`, hue texture, `Chunk.Load` reading `RegionFile`). Verify: a unit diff against MUL, then an in-game walk.
5. **Pack-backed Art, Texmap and Animations** for the legacy renderer and the UI: CPU-decode pages into FNA textures with pack UVs, and feed `PixelPicker`.
   - Verify: legacy screenshots match step 2 at fixed spots; picking and container icons work.
6. **Delete the MUL/UOP loaders**: Art, Texmaps, Animations (including the .def handling), AnimData, Verdata, and the MapLoader MUL and patch code.
7. **ECS mirror.**
   - Mirror World into Friflo, keyed by Serial. Hooks: `GetOrCreateItem/Mobile`, `RemoveItem/Mobile`, `Entity.Destroy`, `SetInWorldTile`, and the packet updates.
   - Add a debug consistency check.
   - Verify: 0 mismatches over a 10-minute session.
8. **Uo.Render as a library with an external device.** It takes FNA's device, target texture and format, and selects shaders by driver. It's fed by pack regions and the ECS extract.
   - Verify: Gpu matches Legacy in screenshots; one world draw call.
9. **Feature parity**, one subtask at a time with screenshot diffs: hue modes, alpha and roofs, highlight, shadows, mirror, effects, weather, lights.
10. **Performance proof under NativeAOT.**
    - Compare Legacy and Gpu in a busy town, at the Britain bank, zoomed out, and while travelling: frame time, world-prepare CPU, allocations.
    - Then make Gpu the default, and remove MeshLayer/ChunkMesh and the views' `Draw` bodies.

## NativeAOT status

- ClassicUO.Client.csproj already sets `PublishAot`. Upstream publishes it as a native shared library loaded by the Bootstrap host.
- This fork has never been AOT-published. Treat the IL2xxx/IL3xxx warnings as a backlog.
- C# plugins (Razor) need the Bootstrap host, and the owner rules out third-party plugins. So the target is a standalone AOT exe.
