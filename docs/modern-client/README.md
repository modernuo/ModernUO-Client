# Modern ModernUO Client — Research Notes

Research for rebuilding ClassicUO into the definitive ModernUO client (superseding `ModernUO-Client`).
Recorded 2026-10-23. Nothing here is a final decision unless it is listed under **Decisions so far**.

## Docs map

Which doc is authoritative for what. When two docs disagree, the one listed here wins; history docs are never the source of current facts.

**Current (authoritative):**

| Topic | Doc |
|---|---|
| Owner decisions | "Decisions so far" below; the running notes, merges and open questions are in [owner-review.md](owner-review.md) |
| Data formats: the pipeline (MUL/UOP import → source format → shipped pack), the source format, what tools read and write | [data-formats.md](data-formats.md) (entry point) |
| The shipped pack's binary layout | [pack-format.md](pack-format.md) |
| ECS: where world state is going, and the state census / write-site budget | [ecs-transition-plan.md](ecs-transition-plan.md), [ecs-state-census.md](ecs-state-census.md); library choice [ecs-decision.md](ecs-decision.md) |
| World prepare on baked GPU data and the ECS (W0–W6) | [world-prepare-ecs-plan.md](world-prepare-ecs-plan.md) |
| Testing: ground rules, accounts, locks, job runner, DevBench | [testing-playbook.md](testing-playbook.md); per-area procedures in `testing/` |
| Bugs | [known-issues.md](known-issues.md) |
| Performance backlog and verdicts | [perf-opportunities.md](perf-opportunities.md) |
| Memory per client: breakdown vs upstream, memory-for-performance trade-off log | [memory-footprint.md](memory-footprint.md) |
| Per-graphic records vs pre-packed pages for art and animations (measured; pages kept) | [records-eval.md](records-eval.md) |
| Graphics: FNA vs owning the device (Tiers 1–4 deferred), batcher/UI work | [graphics-ownership-plan.md](graphics-ownership-plan.md) |
| SDL3, FNA and FNA3D forks | [sdl-fork.md](sdl-fork.md), [fna-fork.md](fna-fork.md) |
| Developer tooling that release builds drop (`CUO_DEVTOOLS`) | [release-cleanup.md](release-cleanup.md) |
| Visual features after the ECS steps (lights, normals, shadows, particles) | [visual-features-roadmap.md](visual-features-roadmap.md) |
| What happened, in order | [poc-log.md](poc-log.md) (append-only; read the newest entries first when resuming) |

**`testing/`** (current): [world-prepare](testing/world-prepare.md), [world-update](testing/world-update.md), [world-settle](testing/world-settle.md), [ecs-animation](testing/ecs-animation.md), [large-scale](testing/large-scale.md), [gpu-faults](testing/gpu-faults.md), [ui-batcher](testing/ui-batcher.md), [pack-loading](testing/pack-loading.md), [renderer-selection](testing/renderer-selection.md), [aot-performance](testing/aot-performance.md) (also the NativeAOT publish facts).

**History** (kept for evidence and reasoning; dated, not current):

| Doc | What it records |
|---|---|
| [pack-format-v0.md](pack-format-v0.md) | How the pack format got to 1.0: measurements, dead ends, co-occurrence packing method |
| [architecture-recommendations.md](architecture-recommendations.md) | The 2026-10-23 architecture proposals |
| [classicuo-render-asset-pipeline.md](classicuo-render-asset-pipeline.md) | The fork's render and asset pipeline before the PoC |
| [classicuo-integration-plan.md](classicuo-integration-plan.md) | The Phase 4 integration plan (done) |
| [classicuo-perf-baseline.md](classicuo-perf-baseline.md) | Per-phase timing baseline of 2026-10-24 and the step 7b hooks |
| [fps-test-results-2026-10-25.md](fps-test-results-2026-10-25.md), [fps-test-results-2026-10-24.md](fps-test-results-2026-10-24.md) | The owner's FPS tests |
| [graphics-api.md](graphics-api.md) | FNA vs SDL_GPU vs WebGPU research |
| [device-lost-leak.md](device-lost-leak.md) | The device-loss leak and the page-fault investigation (fixed through the SDL fork) |
| [tinyecs-evaluation.md](tinyecs-evaluation.md), [ecs-benchmarks.md](ecs-benchmarks.md) | ECS library research behind ecs-decision.md |
| [cuo-2.0-perf-comparison.md](cuo-2.0-perf-comparison.md) | ClassicUO 2.0 vs this fork |
| [legacy-main-review.md](legacy-main-review.md) | What `modernuo/legacy-main` offers (loader work) |

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
- **Legacy on the ECS is a later project; Legacy stays (owner, 2026-10-25).** Removing Legacy isn't decided until we know how the client stays cross-platform, including virtual machines (VirtualBox, VMware, Hyper-V and cloud desktops often expose only D3D11 or OpenGL, no Vulkan), even if it runs poorly there. One later project is to rewrite Legacy to draw from the ECS like the Gpu world does. That's considerable work, so it's deferred until it blocks other important work (W6, removing the legacy walk, depends on it). Until then, keep the ECS extract renderer-neutral, and don't tie new world state more tightly to the GameObject views.
- **Update (owner, 2026-10-25): don't delete Legacy yet.** It may be the graceful-degradation path for low-end hardware (no Vulkan driver, old integrated GPUs). Deletion is deferred until the supported hardware range is decided, so Legacy's performance matters again (the 10-24 → 10-25 Legacy regression is being fixed). **Decided (owner, 2026-10-25):** choose at startup by capability. `world_renderer` is `auto` (default), `gpu` or `legacy`. Auto probes SDL_GPU/Vulkan and the Gpu world setup, and falls back to Legacy (FNA3D on D3D11 or OpenGL when there's no usable SDL_GPU device) with the reason logged and shown in the title. Never mid-session. Runtime switching stays for testing behind the `CUO_DEVTOOLS` compile constant, marked `CLEANUP(release)`, so release builds can drop it. Legacy's long-term form (D3D11/OpenGL via FNA only, further optimisation, or several fallback profiles) is its own project, deferred.
  - **Implemented 2026-10-25 (renderer selection; supersedes the "Implemented" list above where they differ):**
    - `world_renderer` in settings.json is `auto` (default), `gpu` or `legacy`, and is saved. `-worldrenderer auto|gpu|legacy` overrides it for one run without saving. The old "saved Legacy is ignored" rule is gone.
    - **Auto, before the device:** probes SDL_GPU on Vulkan with the properties FNA3D's SDL_GPU driver itself checks (SPIR-V only, since MojoShader gives FNA's effects only SPIR-V; hardware Vulkan). If it can't run, Legacy on FNA3D D3D11 (Windows, when a D3D11 hardware device exists) or OpenGL, unless the user forced an FNA3D driver.
    - **Auto, after the device:** with an SDL_GPU Vulkan device, the Gpu world pass is created at startup, before login. If that fails, or FNA3D isn't on SDL_GPU, Legacy for the session.
    - **Forced `gpu`** that can't run shows the error and exits with `0xDEAF`. **Forced `legacy`** runs on the device FNA3D picks by itself (SDL_GPU here), so parity compares the same device.
    - One `[renderer]` log line states the decision and its reason; the title keeps `[Legacy renderer]`. Device loss (`0xDEAD`) and a world-pass failure after startup (`0xDEAF`) are unchanged: never a mid-session switch.
    - Runtime switching (the `-worldrenderer` chat command, DevBench `world:`/`flip:`/pick, all of DevBench) is behind `CUO_DEVTOOLS`, on by default; `-p:CuoDevTools=false` drops it. See [release-cleanup.md](release-cleanup.md).
    - Test hooks and procedure: [testing-playbook.md](testing-playbook.md) "World renderer selection". What Legacy on D3D11/OpenGL looked like: [known-issues.md](known-issues.md).
- **The pack replaces MUL/UOP (2026-10-24).**
  - For every asset the pack format covers (art, texmaps, maps/statics, tiledata, hues, animations), the client loads only from the pack. There is no side-by-side MUL/UOP path, so we never support two formats. Those ClassicUO loaders get deleted once the pack path works.
  - MUL/UOP stay only for content the pack doesn't cover yet (sounds, music, skills, …; the list is in [pack-format.md](pack-format.md), "Not in the pack"). Each one moves into the pack over time. Multis, lights, radarcol, full tiledata, raw hues, tileart appearances and Equipconv.def joined the pack in format 1.0; gumps, fonts, cliloc and speech on 2026-10-30.
  - The converter is the only code that reads MUL/UOP for converted content.
- **Data pipeline (2026-10-31):** MUL/UOP are import-only. They are imported once into the **source format** (definitive, what admins and tools edit; UO's own uncompressed image layouts; one file or folder per editable thing), and an incremental, stable **build** writes the shipped pack, which is never hand-edited. Nothing is written back to MUL/UOP. ModernUO, CentrED# and UOFiddler move to the source format later. The pack keeps one source of truth per file (`hues.bin` removed; `tiledata.bin` merged into the full tiledata file; ClassicUO's hard-coded per-tile rules become build-written data); stored mip levels are the accepted exception. Spec: [data-formats.md](data-formats.md).
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

- **Several instances at once (2026-10-30):** players often run 3 or 4 clients on one machine, and issues have shown up with 3 or more. The client must run as many instances as a player wants without conflict:
  - nothing one instance writes may corrupt or block another (profiles, settings, logs, journal logs, screenshots, caches, packs);
  - shared inputs (the pack, the UO folder) open read-only with shared access, and regenerating a pack must never break running clients (write aside, swap atomically);
  - per-instance memory and GPU costs (pools, prefills, GC heaps, textures) stay sized for 4 instances on one machine;
  - in-process statics are fine across instances (each instance is its own process) but must still not assume one `World` per process.

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
