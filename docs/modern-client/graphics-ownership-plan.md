# Graphics Ownership Plan: easing ClassicUO off FNA graphics

Drafted 2026-10-24, while the device-lost investigation was still running. The plan **assumes case 3, possibly case 2**:
- **Case 3:** FNA3D assumes it is the device's only user.
- **Case 2:** sharing FNA's device breaks an SDL_GPU rule.

Revisit if the root cause turns out to be a pure SDL bug (case 1). Owning the device is still the better design then, but we would also need an SDL fix or workaround.

> **Status (owner decision, 2026-10-24): Tiers 1–4 are deferred.**
> - **Evidence:** it points mainly at SDL (case 1: leak after device loss, plus a suspected defragmenter page fault). The fix goes into our SDL fork (github.com/modernuo/SDL).
> - **Performance case:** weak. Owning the device saves about 0.2–0.25 ms per frame at 1080p and 0.5–0.6 ms at 4K, all fixed costs. FNA's draw work doesn't grow with entity count, because Uo.Render draws the world; busy scenes are dominated by world prepare, per-mobile view logic and the update loops (the ECS plan).
> - **Active:** Tier 0 and the batcher/UI items (B1–B9, all doable on FNA, including instancing).
> - **Revisit Tiers 1–4 if any of these happens:**
>   - FNA3D's assumptions prove to be part of the crash;
>   - the large-scale test (about 2,000 mobiles and 20,000 ground items, with W0 sub-phase timings) shows the FNA path is significant;
>   - the browser target becomes real.
> - **Shader tooling isn't a reason to migrate:** the world renderer already uses modern HLSL with DXC. Only FNA's two UI shaders go through MojoShader.

## Why

- **The fault:** an Aftermath dump (2026-10-24 21:36, `Error_DMA_PageFault`) shows FNA's end-of-frame command buffer (`GameController.EndDraw` → `Present` → FNA3D → SDL) doing a ROP write into memory that 17 ms earlier became Uo.Render's 256 MiB art page array.
- **Two timelines:** FNA keeps one command buffer open per frame. Uo.Render acquires and submits a second one mid-frame and creates resources then. Two owners of one device with overlapping command-buffer lifetimes is the hazard.
- **The fix:** one owner and one timeline. Owning the device also removes FNA's per-draw overhead, the faux-backbuffer blit and the local FNA3D patch, and lets Windows use D3D12 (MojoShader limits the shared device to Vulkan).

## Principles

- **The client keeps running at every tier,** behind a switch, so old and new can be compared in the same build.
- **Each tier has a gate:**
  - build, plus the 188 unit tests;
  - DevBench screenshot parity;
  - the picking test;
  - the owner's FPS test (`tools/fps-test`);
  - the renderer-flip stress test;
  - a forced-TDR check with the Aftermath monitor running.
- **Keep what FNA does well:** math types, used in 174 files, and audio (FAudio, 3 files) stay on FNA until a later, optional tier. Only graphics and the game loop move.
- **No call-site rewrites where a shim will do.** Most of the 67 graphics files should only change a `using`.

## What ClassicUO uses from FNA today (measured in `src/`)

| Area | Usage |
|---|---|
| Graphics types | 129 `Texture2D`, 112 `GraphicsDevice`, 12 `RenderTarget2D`, 20 `SurfaceFormat`, 23 `Viewport`, 128 Blend/Sampler/DepthStencil/Rasterizer state refs |
| Buffers and effects | `VertexBuffer`/`IndexBuffer` (dynamic and static), 10 `VertexDeclaration`, 7 `Effect`, 3 `BasicEffect` |
| Text | 27 `SpriteFont` (fonts are `.xnb` content files), 28 `SpriteBatch` refs |
| Batcher | `UltimaBatcher2D` (1551 lines); 113 `batcher.Draw*` calls; `MeshLayer` (chunk meshes); `ScissorStack`; `RenderTargets` (world, light, UI targets plus the composite) |
| Shaders | `IsometricWorld.fx` (hue modes, also used by the UI) and `xBR.fx`, compiled with fxc to MojoShader |
| Texture uploads | `SetData`/`GetData` in 14 files (atlases, fonts, map gumps, auras, picking) |
| Game loop and window | `GameController : Microsoft.Xna.Framework.Game`; `GraphicsDeviceManager` (back-buffer size, `ApplyChanges`, VSync, depth format); `Window` (Handle, ClientBounds, Title, resize events); `IsFixedTimeStep`, `TargetElapsedTime` |
| Input | 4 files. ClassicUO already reads SDL events itself; FNA's `Keyboard`/`Mouse` are thin wrappers |
| Audio | `SoundEffect`, `DynamicSoundEffectInstance` (3 files) |
| Plugin draw commands | `ClassicUO.Renderer/Batching/*` command list for plugins. Third-party plugins are ruled out (owner), so it can go |

## Tiers

### Tier 0: stabilise the shared device (now; overlaps the running agent)
- **Frame-boundary resources:** create and destroy Uo.Render's large resources only at frame boundaries, after FNA's `SwapBuffers` and before the next frame's FNA command buffer records anything.
- **No mid-session RT recreation:** stop recreating the world render target when the GPU renderer starts. Pick the RT usage once at startup instead of flipping `PreserveWorldContents`.
- **Device loss:** handle it with a clean exit, logging and an exit code. In progress (device-lost agent).
- **Debuggability:**
  - name Uo.Render resources (SDL texture/buffer name properties) so Aftermath dumps can identify them;
  - add debug groups around our passes.
- **Gate:** login stress under the Aftermath monitor (≥200 logins plus renderer starts), with no page faults; the flip stress test; the owner's FPS test.
- **Effort:** 1–2 days. **Perf:** unchanged from the 2026-10-24 results.

### Tier 1: a graphics seam inside ClassicUO, still backed by FNA (no behaviour change)
- **New library `ClassicUO.Gfx`.** It holds ClassicUO's own graphics API, shaped like the subset of XNA we use, so call sites keep compiling:
  - `GpuTexture` (named `Texture2D` in our namespace): `Width`/`Height`, `SetData`/`GetData`, `Dispose`, `IsDisposed`, `Bounds`.
  - `RenderTarget`.
  - State values as small structs or enums: `BlendMode`, `SamplerMode`, `StencilMode`, scissor.
  - `IGfxDevice`: create/destroy, upload, set target, viewport, clear, and draw primitives with a vertex-format id.
  - The text and font types.
- **The first backend, `FnaGfxDevice`, wraps FNA,** so screenshots stay byte-identical.
- **Port `UltimaBatcher2D`, `MeshLayer`, `RenderTargets`, `ScissorStack`, `TextureAtlas`, fonts and picking onto the seam.** The rest of the 67 files change their `using`.
- **Remove the plugin draw-command list** (`Batching/*`, `Plugin.ProcessDrawCmdList`).
- **Effects become seam-level shader programs** ("UO sprite" with hue modes, "xBR"). Implemented by the existing FNA effects for now.
- **Gate:** screenshot parity is byte-identical across login, world, gumps (paperdoll, backpack, journal, options), text, cursor and world map. The FPS test shows no regression.
- **Effort:** 4–6 days. Mostly mechanical; the risk is in fonts and the effect parameters. **Perf:** neutral (one extra indirection, negligible).

### Tier 2: our own device, frame loop and backend, side by side with FNA (`-gfx fna|sdlgpu`)
- **`SdlGpuGfxDevice`:** our SDL_GPU device, claiming the window. It reuses Uo.Render's device setup, its DXC shader pipeline (DXIL + SPIR-V, so D3D12 works too) and its upload code.
- **One command buffer per frame,** with every pass recorded in order:
  1. world (Uo.Render records into it; no second command buffer);
  2. lights;
  3. UI;
  4. composite straight into the swapchain image, with no faux backbuffer.
- **Resource lifetime:** owned by us, deferred-destroyed after the frame's fence. Resource names and debug groups throughout.
- **Shaders:** port `IsometricWorld.fx` (the UI hue modes) and `xBR.fx` to HLSL, compiled by DXC alongside `sprite.hlsl`.
- **UI batcher backend:** a persistent upload ring buffer, batches keyed by texture, blend and scissor, one pipeline per blend mode, and a sampler cache.
- **Our own `GameHost`:** it replaces `Microsoft.Xna.Framework.Game` and `GraphicsDeviceManager` for both backends:
  - the SDL window and event pump, Update/Draw timing (fixed or variable step), window size, VSync, fullscreen, DPI, resize and minimise;
  - lost-device handling.

  FNA's `FrameworkDispatcher.Update` keeps running for audio.
- **Fonts:** decode the 8 `.xnb` SpriteFonts once (a small reader) or convert them into the pack. ClassicUO's own UO fonts are already CPU-rendered into textures.
- **Picking:** unchanged. It is CPU-side (`PixelPicker`, pack pixels).
- **Gate:**
  - screenshot parity of FNA vs SdlGpu within a small tolerance (filtering differences documented, hue maths exact);
  - the picking test;
  - the FPS test with both backends;
  - forced TDR and lost-device handling;
  - D3D12 and Vulkan both pass;
  - the login stress test with the Aftermath monitor.
- **Effort:** 1.5–2.5 weeks. The risks are UI parity (text, hue modes, xBR) and window and DPI edge cases.
- **Expected perf** (estimates from the measured phases): UI FNA overhead about 0.08–0.11 ms → about 0.02–0.04 ms per frame; faux blit plus composite about 0.15 ms at 1080p and 0.5 ms at 4K → mostly gone. That puts the Britain 1080p p50 at about 0.35–0.40 ms (now 0.52 ms) and 4K zoomed out at about 3.7 ms (now 4.2 ms). World prepare (CPU, 0.15–3.6 ms) is unchanged; it's the ECS track's job.

### Tier 3: SdlGpu becomes the default; FNA graphics removed
- **Switch the default** after the owner's FPS test passes on the SdlGpu backend.
- **Delete:** `FnaGfxDevice`, the MojoShader `.fx`/`.fxc` files and `fxc.exe`, `external/patches/fna3d-sdlgpu-interop.patch`, the self-built `FNA3D.dll`, `GpuInterop.cs` and the SDL3 extern-alias workaround.
- **Keep:** FNA referenced for math and audio only. FNA3D isn't loaded unless the audio path needs it (verify).
- **Gate:** the full test pass, plus a NativeAOT publish with no new trim warnings.
- **Effort:** 1–2 days.

### Tier 4 (optional, later): FNA removed entirely
- **Math:** move to `System.Numerics` plus small ClassicUO types (`Rectangle`, `Point`, `Color`). Mechanical but wide (174 files); do it with codemods in small batches.
- **Audio:** call FAudio directly, or SDL3 audio streams (3 files).
- **Effort:** 3–5 days. **Perf:** small, from `System.Numerics` SIMD in hot math.

## UltimaBatcher2D and UI improvements (worth doing at any tier)

These pay off on FNA today and carry over into the Tier 2 backend. They're also listed in perf-opportunities.md. Measured context: 16–25 batcher flushes per frame in Gpu mode; time inside FNA calls 0.08–0.11 ms at zoom 1; about 17.5 KB allocated per frame, about 90% of it in the UI draw path.

| # | Change | Where | Why |
|---|---|---|---|
| B1 | Dirty-track matrices, viewport and effect parameters; call `Pass.Apply()` only on a state change, not every flush | `Batcher2D.ApplyStates`, `DrawDirectIndexed` | `Effect.INTERNAL_applyEffect` 9.4% and `ApplyStates` 8.1% of CPU samples (step 7b) |
| B2 | Replace per-draw closures with struct commands in pooled lists | `RenderLists.AddGumpWithAtlas/NoAtlas`; `GumpPic`, `GumpPicTiled`, `Button`, `HtmlControl.AddToRenderLists` | About 60% of per-frame allocation |
| B3 | Early-return before the closure in `Control.DrawDebug` | Control.cs:328 | About 20% of per-frame allocation, even with debug off |
| B4 | Reuse a cached `RenderLists` per control | `ResizePic`, `StbTextBox` render lambdas | A new `RenderLists` (seven `List<>`s) every frame |
| B5 | Set the empty-slot tooltip only when the slot becomes empty | `PaperDollGump.EquipmentSlot.Update` | String building plus `SetTooltip` every frame |
| B6 | Compact sprite format: a 32-byte instance (rect, UV, hue, flags) expanded by the vertex shader, instead of 4 × 48-byte vertices (192 bytes per sprite) | `PositionNormalTextureColor4`, batcher flush | About 6× less vertex upload and CPU writing. On FNA it needs instancing support; natural in the Tier 2 backend |
| B7 | Upload ring: write sprites straight into a persistent mapped buffer (no-overwrite ring) instead of a CPU array copied with `SetData` | `_vertexBuffer` | Removes a copy per flush. Tier 2 backend |
| B8 | Cache the UI render target and redraw it only when dirty | GameController.cs UI pass | UI redraw is skipped on most frames when nothing changed. Needs dirty tracking in `UIManager` |
| B9 | Batch-key sorting: atlas id, blend, scissor | Batcher flush | Fewer flushes when gumps interleave atlases |

- **B1–B5 are cheap and FNA-independent:** start now.
- **B6–B7 are Tier 2 work** (or a Tier 1 seam feature). B8 and B9 can come after B2.

## Order relative to other work

1. Tier 0 now; it overlaps the device-lost agent.
2. Tier 1 can run in parallel with the ECS and world-prepare work, since they touch different files.
3. Tier 2 next, then Tier 3 once the owner's FPS test passes.
4. Tier 4 whenever convenient.

The seam from Tier 1 is also where a WebGPU backend plugs in later.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| UI pixel parity (text, hue modes, xBR) | Byte-identical gate in Tier 1; per-shader golden images before porting in Tier 2 |
| Window, DPI or fullscreen regressions when replacing FNA's `Game` | `GameHost` used by both backends from Tier 2, so regressions show in both and can be bisected |
| Hidden FNA behaviour (SamplerState defaults, premultiplied alpha, `SetData` row pitch) | Write down each FNA default as the seam is built, with a unit test per state mapping |
| Plugin users | Out of scope (owner); the Batching command list is removed in Tier 1 |
| SDL-level bug (if root cause is case 1) | Own lifetime management in Tier 2 (deferred destroy after the fence); keep the Aftermath stress gate; patch or report SDL if confirmed |

## Upstream contributions (SDL, FNA3D)

- **Both projects refuse AI-generated contributions.**
  - SDL's `AGENTS.md` (since April 2026) says generative AI "should not be used in any way when contributing to SDL", including bug reports and pull requests.
  - FNA3D has an equivalent rule.
- **So agents never draft upstream issues or patches** for either project, not even as a starting point.
- **Any upstream report or fix is written by the owner personally.** It is verified against upstream source on their own, not pasted or paraphrased from our docs.
- **Our own patches live in our forks** (`ModernUO/SDL` once created, and the local FNA3D patch). They are pinned by submodule and dropped when upstream ships its own fix.
- **Bugs found so far:**
  - SDL Vulkan never releases command buffers after `VK_ERROR_DEVICE_LOST` (device-lost-leak.md).
  - A suspected defragmenter page fault, still under investigation.
