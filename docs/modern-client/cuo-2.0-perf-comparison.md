# ClassicUO 2.0 vs our fork: where the FPS gap comes from

From a read-only investigation on 2026-10-24. 2.0's source isn't public. The evidence is name strings from the shipped `cuo.dll` and guest WASM (no decompiling), plus the deleted upstream branch `impl/ecs` (5a5cfd350, 2026-06-28). 745 of `impl/ecs`'s 902 type names appear in the shipped guest. Confidence labels: **[shipped]** means evidenced in the binary; **[impl/ecs only]** means it may not be in 2.0.

## Are the FPS numbers comparable?

Both count presented frames per second, but the tests weren't like for like.

- **Our counter:** `GameController.Draw` counts frames, and `Update` publishes the count once per ≥1 s window (GameController.cs:425-433, 511).
  - FPS is capped by our own limiter at `MAX_FPS=250`, using `Thread.Sleep(1)`; with ReduceFPSWhenInactive it drops to 217 ms per frame.
  - With VSync off, FNA3D uses `SDL_GPU_PRESENTMODE_IMMEDIATE`.
  - Only `CUO_FRAMESTATS=1` (DevBench) uncaps it and forces inactive throttling off, so the 600–700 readings were taken with it.
- **2.0's counter** [shipped]: an ECS `DebugGumpWindow` with legacy-style FPS strings. It has new `UncapFPS`, `Max FPS` and `FPSInactive` settings, and most likely counts guest `tick()`s, each ending in a present.
- **The conditions differed.** Our baseline used a 3840×2054 window with a 1600×1000 viewport at Britain, zoom 1. The owner's 2.0 run was another scene, window size and zoom.
- **Fair test:** same spot, window and zoom; 2.0 with Uncap FPS; ours with `CUO_FRAMESTATS=1`.

## What 2.0 does differently

- **Chunk meshes for land, statics and dynamics** [shipped; absent from impl/ecs]: `ChunkMeshPlugin`, `BakeChunkMeshes`, `BakeChunkDynamics`, `ChunkMeshCache`. They're on by default; `CUO_CHUNK_MESH[_LAND|_DYN]=0` turns them off.
  - This is probably 2.0's biggest world-side win. `impl/ecs`'s world render still walks every tile each frame.
  - Our fork already has land and statics chunk meshes, but not dynamics.
- **Native draw-state handling** [shipped]: the guest fills a binary command buffer and the Rust host replays it into wgpu, with arenas and a separate upload worker thread. In our client, FNA3D does the per-draw state tracking through C# P/Invoke calls.
- **No fewer full-screen passes** [shipped]: it renders offscreen and then runs an `fs_smooth` FXAA-style pass, so it saves no blit compared with us.
- **Pipelining: no evidence.** One guest `tick()` per host frame; apart from the upload worker there are no render-thread strings. The loop looks serial, but that's inferred, not proven.
- **UI caches** [shipped]: `UiPickCachePlugin`, `GumpPositionCachePlugin`, `UiTextMeasure`.
- **Allocation treated as a bug, with permanent probes** [shipped]: `EcPerfProbe`, `BatcherProbe` ("flushes/frame"), "gcPause=".
- **Systems instead of per-object Update** [both]. `ScreenPosition` is recomputed only when `WorldPosition` changes [impl/ecs only].

## Where our ~1.35 ms frame goes, from reading the code

- **Total `Draw()`:** about 0.53 ms, of which the scene draw is 0.34 ms. That leaves **about 0.8 ms in Update, the event pump and Present**.
- **`World.Update`** (World.cs:259-398) walks **every mobile and every item, including items inside containers**. Each item calls `Entity.Update`, and items also run `ProcessAnimation`.
- **`UIManager.Update`** sorts controls and recursively updates every gump child every frame.
- **Draw:** world RT, light RT, a full-window UI RT redraw, then `RenderTargets.Draw` (a tiled background plus three full-window quads).
- **Present (FNA3D):** waits on the frame-in-flight fence, acquires the swapchain, blits the faux backbuffer at window size (3840×2054 in the baseline), then submits.
- **Allocation:** 17–18 KB per frame.

## Recommendations, ranked

1. **Measure per phase** (integration plan step 7b), and run 2.0 under identical conditions.
2. **Uo.Render integration** replaces world prepare and draw, the counterpart of 2.0's chunk meshes plus host-side GPU.
3. **Event-driven world update through the ECS:**
   - skip contained items;
   - keep active lists for animating items and mobiles;
   - recompute screen position only when the position changes.
4. **UI:** cache the UI render target and redraw it only when it changes (dirty); add a hit-test cache. Porting to Clay isn't worth it now.
5. **Zero allocation per frame,** with probes kept permanently in the build.
6. **Present:** render straight into the swapchain to skip the faux backbuffer and composite. Low-to-medium gain at 4K; needs another FNA3D patch.
7. **Pipelining:** not yet. Revisit if Present or submit is still ≥30% of the frame after steps 2–4. SDL_GPU frames-in-flight already hide GPU latency.

Raw string extracts are in the session scratchpad (`cuo2/host.txt`, `guest.txt`, `ecs_types.txt`).
