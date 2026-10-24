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

**Done in format 1.0 (5cb439549):** tiledata_ext, hues_raw, radarcol, multis, lights, tileart, equipconv and animdata, each verified field-exact against ClassicUO's loaders. CPU pixels for picking come from decoding pack pages on the CPU; there are no mask sidecars. The original list follows for reference.

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
7b. **Per-phase timing hooks plus a quiet-machine baseline** (owner, 2026-10-24).
    - Time each frame phase behind DevBench's `CUO_FRAMESTATS`: packet processing, World.Update (split mobiles, items, effects and text), UI update, world prepare, world draw, lights, UI draw, RT composite, Present/SwapBuffers, and GC.
    - Record p50/p95/p99 for JIT and AOT at fixed spots.
    - **Remove the 250 FPS cap (owner):** make the client uncapped by default with an on/off VSync toggle; reuse ClassicUO's existing setting if it has one.
    - **Ground-item chunk baking:** consider it only if the timing shows the dynamic extract or upload is significant in busy scenes. It's the counterpart of 2.0's `BakeChunkDynamics`: bake rarely-changing ground items into the chunk buffer and re-bake on ECS change events. Mobiles stay in the per-frame extract.
    - Also count batcher flushes and draw calls per frame, and the time spent inside FNA per draw (state application, MojoShader uniforms, FNA3D calls). That shows whether a command-list GPU device would pay off. ClassicUO 2.0 records into an `IGpuDevice` command buffer that a native host replays (see cuo-2.0-perf-comparison.md). Long term, that device layer is also the path off FNA for the UI, and onto WebGPU.
    - These numbers rank what moves to the ECS first: the update loops that cost the most become ECS systems. They also decide whether pipelining is worth it, i.e. a render thread or overlapping the next frame's update with GPU submission, as ClassicUO 2.0's Rust host appears to do.
    - Context: ClassicUO 2.0 is built on TinyEcs throughout and is pipelined by its host. Our target is a similar ECS-first, pipelined architecture, reached incrementally.
8. **Uo.Render as a library with an external device.** It takes FNA's device, target texture and format, and selects shaders by driver. It's fed by pack regions and the ECS extract.
   - Verify: Gpu matches Legacy in screenshots; one world draw call.
9. **Feature parity**, one subtask at a time with screenshot diffs: hue modes, alpha and roofs, highlight, shadows, mirror, effects, weather, lights.
10. **Performance proof under NativeAOT.**
    - Compare Legacy and Gpu in a busy town, at the Britain bank, zoomed out, and while travelling: frame time, world-prepare CPU, allocations.
    - Then make Gpu the default, and remove MeshLayer/ChunkMesh and the views' `Draw` bodies.
    - **Measurement rules:**
      - Turn off ClassicUO's "reduce FPS when inactive" option; unfocused windows are throttled.
      - Uncap FPS and turn off v-sync.
      - Build NativeAOT with `IlcInstructionSet=x86-64-v3` (the i9-12900K has AVX2/FMA/BMI but no AVX-512). JIT already uses AVX2.
      - Report Update, world prepare, draw and Present separately. In the baseline, Update and Present dominate the 1.33 ms frame.
    - **Reference point:** the ClassicUO 2.0 beta (NativeAOT-LLVM WASM guest on a Rust/wgpu host) shows 1000+ FPS against our 600–700. Check first whether the two FPS counters are comparable.

## Pack loading in ClassicUO (steps 4–6, done)

- **Where the pack comes from:** `-pack <dir>` on the command line, or `"pack"` in settings.json. When neither is set, the client uses a `pack` folder next to `cuo.exe`, then one inside the UO directory. A missing or pre-1.0 pack stops startup with an error.
- **What still reads the UO directory:** gumps, fonts, cliloc, speech, sounds, skills, professions, multimap images, Anim1/Anim2.def, and the UOP/override plumbing (`ClassicUO.IO`) these use.
- **Layout:** `ClassicUO.Assets` references `poc/Uo.Pack`. `PackAssets` opens the pack, and `PackPages` decodes Kind 2 pages one 1 MiB chunk at a time into a bounded LRU. The loaders keep their public APIs; sprites are decoded to RGBA on the CPU and fed to ClassicUO's atlases and `PixelPicker`.
- **Removed:** verdata, map patches (mapdif/stadif; the 0xBF 0x18 packet is ignored), UltimaLive, the `use_verdata` and `maps_layouts` settings, and the `Art/Statics` and `Art/Land` loose-file override. The converter does not read those folders yet.
- **The converter's MUL readers:** a frozen copy of the old loaders lives in `tools/ClassicUO.Assets.Mul`, referenced only by `poc/Uo.Converter`.

## NativeAOT status

- ClassicUO.Client.csproj already sets `PublishAot`. Upstream publishes it as a native shared library loaded by the Bootstrap host.
- This fork has never been AOT-published. Treat the IL2xxx/IL3xxx warnings as a backlog.
- C# plugins (Razor) need the Bootstrap host, and the owner rules out third-party plugins. So the target is a standalone AOT exe.
