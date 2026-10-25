# Modern ModernUO Client — Research Notes

Research for rebuilding ClassicUO into the definitive ModernUO client (superseding `ModernUO-Client`).
Recorded 2026-10-23. Nothing here is a final decision unless it is listed under **Decisions so far**.

| Document | What it covers |
|---|---|
| [tinyecs-evaluation.md](tinyecs-evaluation.md) | Code review of Karasho's TinyEcs (core, Bevy layer, UI, WASM modding) |
| [ecs-benchmarks.md](ecs-benchmarks.md) | UO-shaped benchmark of TinyEcs vs Friflo.Engine.ECS vs Arch; native (C/Rust) ECS question |
| [classicuo-render-asset-pipeline.md](classicuo-render-asset-pipeline.md) | How ClassicUO draws and loads assets today, and where it is weak |
| [architecture-recommendations.md](architecture-recommendations.md) | Proposed architecture: world storage, renderer, converter/streaming, protocol, anti-cheat, UI, platforms |
| [graphics-api.md](graphics-api.md) | FNA vs SDL_GPU vs WebGPU — performance and porting effort |
| [testing-playbook.md](testing-playbook.md) | How to test the client safely: accounts, locks, DevBench plans, parity/picking/perf checks, GPU crash evidence |
| [visual-features-roadmap.md](visual-features-roadmap.md) | Particles, dynamic lighting, normal maps, shadows, post-processing; toggles/presets and server-driven effect packets |
| [world-prepare-ecs-plan.md](world-prepare-ecs-plan.md) | Moving ClassicUO's per-frame world walk onto baked GPU data and the ECS (W0–W6) |
| [graphics-ownership-plan.md](graphics-ownership-plan.md) | Tiered path off FNA graphics (Tiers 1–4 deferred), batcher/UI improvements, upstream contribution policy |
| [sdl-fork.md](sdl-fork.md) | Why we build SDL3 from our fork, the patch list, rebuilding and rebasing |
| [fna-fork.md](fna-fork.md) | Our FNA and FNA3D forks: the patches (interop, index-buffer rebind, freed render target), the branch rule, rebuilding |
| [owner-review.md](owner-review.md) | Running notes for the owner: decisions, merges with their evidence, open questions, the FPS test steps |
| [poc-log.md](poc-log.md) | The PoC's chronological log; read its newest entries first when resuming |
| [known-issues.md](known-issues.md) | Open and recently fixed bugs, with where to look |
| [perf-opportunities.md](perf-opportunities.md) | Measured performance opportunities and their verdicts |
| [fps-test-results-2026-10-25.md](fps-test-results-2026-10-25.md) | The owner's latest NativeAOT FPS test, compared with [10-24](fps-test-results-2026-10-24.md) |
| `testing/` | Per-area test procedures (world prepare, ECS animation, world settle, large scene, GPU faults, UI batcher, pack loading, AOT) |

Supporting material in this branch:

- `research/ecs-bench/` — the benchmark harness (expects `TinyECS`, `Friflo.Engine.ECS`, `Arch` cloned next to this repo).
- `research/SchedulerBatchOrderingRepro.cs` — xUnit repro of a TinyEcs scheduler ordering bug; drop into `TinyECS/tests/` to run.

## Executive summary

1. **ECS library choice is not performance-decisive.** All three C# ECS libraries iterate at the memory-bandwidth limit (~1.4 ns/entity) with ~zero steady-state allocation. Friflo leads on structural changes, lookups and parallel iteration, and leads every NativeAOT benchmark (it needs explicit type registration under AOT); TinyEcs uniquely has per-row change detection plus a Bevy-style app layer and UI; Arch is the leanest in memory.
2. **The static world (≈99% of objects) should not be ECS entities.** Land/statics/multis belong in chunk-packed arrays baked into persistent GPU buffers — the natural unit for streaming. The ECS (or typed stores) only holds dynamic objects (items, mobiles, effects, lights): thousands, not hundreds of thousands.
3. **The real wins are in rendering and asset loading.** ClassicUO decodes and uploads synchronously on the render thread, never evicts atlases, rebuilds render lists each frame and breaks batches on every texture switch. A GPU-driven, instanced, texture-array renderer fed by an offline converter + streaming residency manager fixes all of these.
4. **The asset converter is the first deliverable**; streaming, the renderer and CDN distribution all depend on its pack format.
5. **Graphics API:** FNA 25.x already runs on SDL_GPU (Vulkan/D3D12/Metal), but the XNA API cannot expose compute, storage buffers or texture arrays. Recommended path: SDL3 + SDL_GPU directly behind a thin renderer interface, with a WebGPU backend later for the browser. See [graphics-api.md](graphics-api.md).

## Decisions so far (from the project owner)

- **SIMD where it measurably helps (2026-10-24):**
  - Use the cross-platform `Vector128`/`Vector256` APIs, not x86-only intrinsics, so the same code runs on ARM NEON and WASM SIMD128.
  - Always keep a scalar fallback, with a test proving identical output (the `PixelExpand` pattern).
  - Keep SIMD only when a benchmark shows a real gain.
  - **Where it fits best:**
    - ECS packed-array loops: visibility, screen position, animation timers;
    - writing 32-byte GPU instances: one 256-bit store each;
    - pixel-row scans: Indexed8 → picking masks, sprite bounds;
    - pixel expansion.
  - **Prefer moving less data over vectorising wide data:** the compact 32-byte UI instance (batcher item B6) before SIMD on the 192-byte vertex path.
  - NativeAOT targets x86-64-v3 (AVX2), so `Vector256` runs at full speed.
- **Protocol:** our own lean, delta-based protocol. No full-state mirroring. ModernUO will be changed to match.
- **Prediction / rollback:** wanted where possible (client-side movement prediction at minimum), without giving up ModernUO-style low GC churn.
- **Rendering scope:** 2.5D with existing assets: dynamic lights, normal maps, shadows, zoom, HiDPI, particles. No camera rotation (a locked-camera 3D engine with new assets is out of scope).
- **Assets:** shard owners run our converter tools and ship converted assets via CDN or bundle.
- **No automation, no third-party scripting** (no Lua, IronPython, componentize-dotnet, plugin API). Assistant features are built in as first-class features. Anti-cheat matters.
- **UI:** classic gumps must work; newer interfaces (client-side UI driven by data/actions over the wire) are wanted. Controllers and localization are wanted.
- **Platforms:** desktop first, targeting NativeAOT. Keep the browser (WASM) and mobile possible without rebuilding the project. Steam Deck desirable.
- **One process = one session.** No multiboxing.
- **Legacy world renderer is a developer comparison mode only (2026-10-24).**
  - No automatic fallback to Legacy. While the GPU world isn't ready (login, facet change), hold the last frame or fade. If GPU setup fails, show an error and exit, the same way device loss does.
  - Keep Legacy only as a developer comparison mode, mainly to catch Gpu bugs (parity shots, picking checks, A/B FPS). It is not a normal player setting.
  - **Implemented 2026-10-25 (world settle):**
    - The world renderer is chosen only for the current run. Legacy needs `-worldrenderer legacy`, the `-worldrenderer` in-game command or DevBench `world:legacy`.
    - A `"world_renderer"` saved in settings.json is ignored with a warning and dropped on the next save.
    - A failed GPU world setup exits with `0xDEAF`.
    - The hold is `WorldSettleGate`: see [testing/world-settle.md](testing/world-settle.md).
  - Delete the Legacy world path once the parity tooling compares against saved reference shots instead of a live Legacy render.
- **Update (owner, 2026-10-25): don't delete Legacy yet.** It may be the graceful-degradation path for low-end hardware (no Vulkan driver, old integrated GPUs). Deletion is deferred until the supported hardware range is decided, so Legacy's performance matters again (the 10-24 → 10-25 Legacy regression is being fixed). **Decided (owner, 2026-10-25):** choose at startup by capability. `world_renderer` is `auto` (default), `gpu` or `legacy`. Auto probes SDL_GPU/Vulkan and the Gpu world setup, and falls back to Legacy (FNA3D on D3D11 or OpenGL when there's no usable SDL_GPU device) with the reason logged and shown in the title. Never mid-session. Runtime switching stays for testing behind the `CUO_DEVTOOLS` compile constant, marked `CLEANUP(release)`, so release builds can drop it. Legacy's long-term form (D3D11/OpenGL via FNA only, further optimisation, or several fallback profiles) is its own project, deferred.
- **The pack replaces MUL/UOP (2026-10-24).**
  - For every asset the pack format covers (art, texmaps, maps/statics, tiledata, hues, animations), the client loads only from the pack. There is no side-by-side MUL/UOP path, so we never support two formats. Those ClassicUO loaders get deleted once the pack path works.
  - MUL/UOP stay only for content the pack doesn't cover yet (gumps, fonts, cliloc, sounds, …; the list is in [pack-format.md](pack-format.md)). Each one moves into the pack over time. Multis, lights, radarcol, full tiledata, raw hues, tileart appearances and Equipconv.def joined the pack in format 1.0.
  - The converter is the only code that reads MUL/UOP for converted content.
- **No patch formats:** verdata.mul and mapdif/stadif are never supported. Shard owners who use them must merge the patches into the base files with their own tooling before running our converter (2026-10-24).
- **Texture format (2026-10-24, revised the same day):** **16-bit lossless (`B5G5R5A1`) on every platform**, bit-exact to the source art.
  - **Why not lossy:** lossy formats can't hue correctly. BC7 broke 31.8% of the gray pixels that partial hue depends on, and the best shader tolerance still left 3,981 of 27,287 hued pixels wrong. MobileUO shows phones handle 16-bit UO art fine.
  - **Status of lossy:** BC7 support stays in the converter and renderer as an experiment, but it is not a shipping profile.
  - **Platform renderer profiles are deferred (owner, 2026-10-24).** They are runtime choices behind an existing seam and don't change the pack format or the architecture.
    - **The seam:** `PageFormats` maps the pack format to the GPU format, and `PageResidency` checks support at startup.
    - **Unsupported 16-bit:** when the GPU can't sample 16-bit, the worker expands the page to RGBA8 in the staging buffer. This fallback is small and lands in the PoC to prove the path. Unsupported cases: WebGPU, which has no 16-bit packed formats, and Intel Macs, where Metal has none.
    - **Native 16-bit is expected elsewhere:** Vulkan's mandatory `A1R5G5B5_UNORM_PACK16` has the same bit layout, Apple-GPU Metal has `BGR5A1Unorm`, and D3D12 has it too.
    - **Web alternative:** `r16uint` pages with manual filtering in the shader, if RGBA8's 2× memory is too much there.
    - **Mobile memory** is a residency setting (layer count, page size), not lossy compression.
- **Brotli is for transport (2026-10-24).** Packs are downloaded Brotli-compressed. The installer or patcher unpacks pages to raw once, so runtime page loads are plain reads.
  - Measured with 16-bit pages: about 3 ms per raw page versus about 25 ms to decode Brotli, on a worker thread. The render thread's cost is the same either way (0.3–0.4 ms per page upload).
  - On disk the 16-bit pack is 435 MB raw versus 142 MB compressed.
  - Platforms that must keep pages compressed at rest (mobile storage, browser storage) use **chunked Brotli**: independent ~1 MiB streams that workers decode in parallel. See "Distribution and chunked compression" in pack-format-v0.md.
  - **Superseded the same day:** the owner accepts compressed-at-rest when decode is fast, and raw animations would be 6.7 GiB. See the final profile below.
- **Final pack profile, format 1.0 (2026-10-24).** Based on the animation storage investigation in pack-format-v0.md. The authoritative spec is [pack-format.md](pack-format.md), and the converter's default output is this profile.
  - **Art and texmaps:** B5G5R5A1, 2048² pages, full mip chain, **chunked Brotli (Kind 2), 1 MiB chunks**. Decode takes about 4 ms per page with 8 workers.
  - **Animations:** **Indexed8, 1024² pages, no mips**, plus `palettes.bin` (37.9k deduplicated 256-entry B5G5R5A1 palettes, 6 MB compressed). Also Kind 2 (one chunk per page at 1 MiB).
    - Lossless: 0 mismatches against the 16-bit pack.
    - Size: 0.63 GB compressed, 2.6 GiB raw.
    - About 0.3 MB on disk and 1 MiB of VRAM for a human's walk, run and stand.
    - No mips are needed: ClassicUO's minimum zoom is 0.5, and at 0.5 the shader's manual bilinear filter is within 5/255 of the 16-bit mip.
    - The shader does the palette lookup and filtering by hand: an R8_UINT array, palettes in a storage buffer, the palette id per instance, and hue applied after the lookup.
  - **Regions:** Kind 1 (small, 2–5 ms to decode).
  - **No install-time unpacking is required.** Raw is still valid for any file, for delta patching.
  - The ship profile has no BC7/ASTC/RGBA8 pages. The RGBA8 expansion stays a runtime fallback for 16-bit pages only (WebGPU). Indexed8 needs no fallback.
- **Page packing by co-occurrence is planned for v2:** sprites that appear together go on the same pages. The method is in pack-format-v0.md.
- **Compression is per file** (`FileHeader.Kind`: 0 = raw, 1 = Brotli), and **raw packs are always valid**: shard owners may ship large uncompressed packs for easy binary delta patching, since the manifest's per-file sha256 identifies what changed.
- **UltimaLive:** optional; drop it if it costs performance. Format v0 supports it cheaply at block granularity.
- **Team:** a few engineers or engineering-adjacent people with AI assistance; nobody has ECS experience.
- **SDL3 comes from our fork, github.com/modernuo/SDL (2026-10-25):**
  - The fork's branch `modernuo/3.4.18-patched` is the SDL 3.4.18 release tag plus our patches (one commit each; owner rule, see sdl-fork.md). It's the `external/SDL` submodule, and its build is committed as `external/x64/SDL3.dll`.
  - The first patch gives Vulkan images of 64 MiB or more dedicated allocations, which stops the page-atlas defragmentation behind our GPU page faults.
  - Agents may push branches to the fork. Upstream libsdl-org gets no AI-written issues or PRs, not even drafts.
  - See [sdl-fork.md](sdl-fork.md).

## Open items

- ~~Upgrade FNA 25.09 → 26.10~~ (done, merged to `poc/modern-client`; the submodule checkout is still pending).
- ~~Add Flecs.NET (C ECS) to the benchmark~~ (adapter done; final quiet-machine timings still owed).
- ~~Prototype a vertical slice~~ (done: CP5 in poc-log.md).

### WASM checks (deferred; not a primary concern yet)

Things to measure when the browser build starts:

- **`PixelExpand` under the AOT build with SIMD.** Confirm `Vector128.IsHardwareAccelerated` is true and the per-page time beats the scalar paths.
  - Native results: 1.5 ms with Vector128, 5.7 ms scalar arithmetic, 20.8 ms scalar lookup table. The SIMD path only uses ops SIMD128 has directly: 16-bit multiply, shifts, and/or, and widen (`extend_low/high`).
  - **Watch the interpreted (non-AOT) mode:** Vector128 may not be accelerated there. If so, the kernel falls back to the scalar arithmetic path, which still beat the lookup table natively. Measure it anyway.
  - If WASM somehow favours the table, swap the scalar fallback. The output is identical either way.
- **Brotli decode speed** in WASM (`BrotliDecoder` is managed or native depending on the build). Size the chunked-Brotli (`Kind=2`) chunk count and worker count from it.
- **Worker threads:** whether the browser build gets real threads (SharedArrayBuffer needs cross-origin isolation). Without them, page decode and expansion run on the main thread and need time-slicing.
- **WebGPU limits:** the maximum texture array layers, the cost of 2048² RGBA8 pages, and the resident layer count that fits the browser's memory budget. The `r16uint` with manual filtering alternative applies if RGBA8's 2× memory doesn't fit.
- **GC and allocation:** confirm the 0-allocation frame loop holds in the browser build.

### WASM profile constraints (lessons from ClassicUO 2.0, 2026-10-24)

ClassicUO 2.0 ships C# compiled with **NativeAOT-LLVM** (dotnet/runtimelab, experimental) into a WASI 0.2 component, run by a native Rust host (wasmtime + wgpu). A browser host also exists. Details: [cuo-2.0-perf-comparison.md](cuo-2.0-perf-comparison.md) and branch `research/cuo-2.0-beta-wasm`. A WASM profile of our client must respect the same limits. Design for them now where it's cheap:

- **Assume single-threaded game code.** 2.0 dropped its parallel ECS scheduler and `ThreadingMode` for wasip2. In the WASM profile:
  - page decode, chunk baking and Kind 2 chunk decode need a single-threaded, time-sliced path (a per-frame budget), with no blocking waits;
  - Friflo parallel jobs stay off;
  - native worker threads remain a desktop optimisation.
- **Cross the host boundary rarely.** Host calls are expensive in WASM, so 2.0 batches all rendering into one binary command buffer per frame (`IGpuDevice` → `gpu-execute`), which the host replays.
  - Our renderer interface should **record, then submit once**. Uo.Render's world pass already does this (instance buffer plus one draw). The UI batcher must move to the same model.
  - Network, audio, clipboard and file I/O also become coarse host calls.
- **The host owns I/O.**
  - Sockets: browsers have no raw TCP. 2.0's host ships a WebSocket client, so our own protocol needs a **WebSocket transport** next to TCP.
  - Files: preopened directories or OPFS. Packs arrive over HTTP, and Kind 2 chunks and per-file sha256 suit range or partial fetches and caching.
- **GPU (WebGPU):**
  - no 16-bit packed formats, so 16-bit pages need the `PixelExpand` fallback (or `r16uint` plus manual filtering). Indexed8 animations (`r8uint`) work as-is;
  - shaders are WGSL. 2.0 hand-ported its `.fx` shaders to WGSL; we'd translate our HLSL via SPIR-V with naga or tint;
  - check storage-buffer-in-vertex-stage limits (compatibility mode can report 0);
  - uploads go through the host. 2.0 uses an upload worker and arenas on the host side.
  - **SDL_GPU WebGPU backend** ([libsdl-org/SDL#16020](https://github.com/libsdl-org/SDL/pull/16020), checked 2026-10-24): an experimental, open draft. It's Dawn only (wgpu-native support was dropped), consumes WGSL (SPIR-V→WGSL via shadercross is planned), and is browser-first with native kept for debugging. Buffer and texture cycling and mipmap generation are missing; it isn't thread-safe and still leaks. An FNA trace run had 11 of 21 traces working. If it matures, our SDL_GPU renderer could reach the browser without a separate WebGPU backend. ClassicUO 2.0 doesn't use it: its host is Rust wasmtime + wgpu. Re-check before starting the browser profile.
- **Compiler choice:** NativeAOT-LLVM (2.0's choice; fastest code, but experimental) vs Mono-WASM AOT (supported). Re-evaluate when the browser build starts.
- **No plugins.** 2.0 loads WASM-component mods; our owner rules out third-party scripting, so this doesn't apply.
