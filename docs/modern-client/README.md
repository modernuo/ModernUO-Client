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

- **Protocol:** our own lean, delta-based protocol. No full-state mirroring. ModernUO will be changed to match.
- **Prediction / rollback:** wanted where possible (client-side movement prediction at minimum), without giving up ModernUO-style low GC churn.
- **Rendering scope:** 2.5D with existing assets: dynamic lights, normal maps, shadows, zoom, HiDPI, particles. No camera rotation (a locked-camera 3D engine with new assets is out of scope).
- **Assets:** shard owners run our converter tools and ship converted assets via CDN or bundle.
- **No automation, no third-party scripting** (no Lua, IronPython, componentize-dotnet, plugin API). Assistant features are built in as first-class features. Anti-cheat matters.
- **UI:** classic gumps must work; newer interfaces (client-side UI driven by data/actions over the wire) are wanted. Controllers and localization are wanted.
- **Platforms:** desktop first, targeting NativeAOT. Keep the browser (WASM) and mobile possible without rebuilding the project. Steam Deck desirable.
- **One process = one session.** No multiboxing.
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
- **Page packing by co-occurrence is planned for v2:** sprites that appear together go on the same pages. The method is in pack-format-v0.md.
- **Compression is per file** (`FileHeader.Kind`: 0 = raw, 1 = Brotli), and **raw packs are always valid**: shard owners may ship large uncompressed packs for easy binary delta patching, since the manifest's per-file sha256 identifies what changed.
- **UltimaLive:** optional; drop it if it costs performance. Format v0 supports it cheaply at block granularity.
- **Team:** a few engineers or engineering-adjacent people with AI assistance; nobody has ECS experience.

## Open items

- Upgrade FNA 25.09 → 26.10 (separate branch).
- Optionally add Flecs.NET (C ECS) to the benchmark.
- Prototype a vertical slice: converter → packs → instanced sprite renderer drawing one facet region, measured against ClassicUO.
