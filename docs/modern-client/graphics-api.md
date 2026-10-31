# Graphics API: FNA vs SDL_GPU vs WebGPU

> **History.** Research of 2026-10-23/24. What was decided: FNA stays, running on SDL_GPU, and Uo.Render shares its device ([graphics-ownership-plan.md](graphics-ownership-plan.md): Tiers 1–4 deferred; [fna-fork.md](fna-fork.md); [sdl-fork.md](sdl-fork.md)).

## What ClassicUO runs on today

- **The stack:** FNA (the XNA 4 API) → FNA3D → a driver.
- **Drivers, in priority order** (`external/FNA/lib/FNA3D/src/FNA3D.c:42-52`):
  1. **SDL_GPU**: SDL3's GPU API, running on Vulkan, D3D12 or Metal;
  2. D3D11;
  3. OpenGL.
- So on current builds ClassicUO already renders through a modern backend by default. `FNA3D_FORCE_DRIVER` (set from settings in `Main.cs:209-214`) can pin OpenGL or Vulkan.

**The limit is FNA's API, not its backend.** XNA 4 is a DX9-era API:
- no compute shaders;
- no storage or structured buffers;
- no texture arrays;
- no indirect draws;
- shaders are DX9 `.fx` files translated by MojoShader (ps_3_0 limits).

The renderer described in [architecture-recommendations.md](architecture-recommendations.md#4-renderer-gpu-driven-25d) can't be written against XNA: instanced sprites from storage buffers, texture-array residency, compute particles, clustered lights. A faster backend under the same API doesn't change that.

## Performance

No benchmark yet; these are qualitative claims to verify in the prototype.

- **The GPU barely matters for sprites.** For a 2D/2.5D sprite workload the GPU draws a few hundred thousand textured quads per frame with little effort. Today's cost is on the CPU: 192-byte quads, a flush on every texture switch, a render-list rebuild every frame, and synchronous uploads.
- **Any modern API with instancing and texture arrays** cuts that to a handful of draw calls and a small per-frame instance upload. The gain comes from the **design**, not from the choice of API.
- **SDL_GPU and WebGPU (native wgpu/Dawn)** both sit on Vulkan, D3D12 or Metal. WebGPU adds validation and resource-tracking overhead per call, roughly D3D11-class CPU cost per draw. With a GPU-driven design (tens of draws per frame) the difference is negligible.
- **Raw Vulkan or D3D12** would be fastest per call and isn't worth it for this workload.

## Options

| Option | What you get | Work | Browser |
|---|---|---|---|
| Stay on FNA (XNA API), upgrade to the latest | Modern backends, same old feature set | Small (see the FNA upgrade branch) | No (FNA has no WebGPU or WASM target) |
| **SDL3 + SDL_GPU directly** | Compute, storage buffers, texture arrays, indirect draws; Vulkan/D3D12/Metal; same authors and ecosystem as FNA; SDL3-CS bindings already vendored under `external/FNA/lib/SDL3-CS` | New renderer: sprite batching/instancing, render targets, shaders (HLSL compiled to SPIR-V, DXIL and MSL via SDL_shadercross). Window, input, gamepad, IME and audio move to SDL3 directly (or FAudio). | Needs a second backend |
| WebGPU (wgpu-native or Dawn, via Silk.NET or similar bindings) | Same feature class; WGSL shaders; one API shape on desktop, mobile and browser | New renderer, same scope as SDL_GPU. .NET bindings are less proven. The browser needs a different binding layer (JS interop to `navigator.gpu`) even though the API and shaders are the same. | Yes, via a second binding layer |

**Recommendation:** SDL3 + SDL_GPU behind a thin `IRenderer` interface (device, buffers, textures, pipelines, passes).
- It's the closest step from FNA: same people, same SDL3 platform layer, bindings already in the repo.
- The team has no WebGPU experience, and SDL_GPU's model (command buffers, passes, pipelines) is the same shape as WebGPU's. A WebGPU backend for the browser can be added later behind the same interface.
- Write shaders in HLSL and cross-compile. For WebGPU, translate to WGSL (e.g. via SPIR-V with naga or Tint) when that backend arrives.

**Phasing:** the vertical-slice renderer can live next to FNA during migration. Keep FNA for the window, input and the old UI while the world renders through SDL_GPU into a texture FNA composites. Retire FNA once the UI moves.

## Upscaling (DLSS / FSR / XeSS)

- Temporal upscalers need jittered rendering, depth and motion vectors. They're built for 3D TAA and tend to smear crisp pixel art.
- The GPU-heavy parts of a 2.5D UO client would be lighting, shadows and particles at 4K. Those passes could render at reduced resolution with spatial upscaling (FSR1-style, plain shader code).
- DLSS needs D3D12 or Vulkan plus NVIDIA's native SDK (Streamline). Possible through SDL_GPU's underlying device, but a late, optional feature.
- The bigger "AI" win is **offline**: upscaling art in the converter for HiDPI.
