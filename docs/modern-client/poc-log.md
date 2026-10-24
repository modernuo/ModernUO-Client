# PoC Log

Running checkpoint log for the modern-client proof of concept. **When resuming after a compaction or handoff, read this file first.**
Branch: `poc/modern-client` (based on `research/modern-client-evaluation`).

## Goals (from the owner, 2026-10-23)

1. Decide the ECS: Friflo vs TinyEcs vs Flecs.NET.
2. Decide the renderer stack.
3. Preliminary data format, converted from `C:\Ultima Online Classic`, that serves the GPU-driven renderer: texture-array pages, instancing, streaming/residency, zoom, HiDPI.
4. Then keep proving out the engine and tech stack.

UltimaLive support is optional if it costs performance. The v0 format supports it at block granularity: replace one block, re-bake one chunk.

## Layout

| Path | What |
|---|---|
| `poc/Uo.Pack` | Pack format v0: structs, readers, writers. **The contract.** Spec: `docs/modern-client/pack-format-v0.md` |
| `poc/Uo.Converter` | UO files → packs. Reuses `src/ClassicUO.Assets` (UOFileManager) for UOP/MUL/verdata reading |
| `poc/Uo.Render` | SDL3 + SDL_GPU renderer (D3D12 first; HLSL → DXIL via the Windows SDK's `dxc.exe`) |
| `C:\Repositories\uo-packs\` | Converter output (not committed; large) |
| `C:\Repositories\EcsBench` | ECS benchmark harness (copy in `research/ecs-bench`) |

## Checkpoints

### 2026-10-24 — CP1: format and scaffolding

- Research docs committed on `research/modern-client-evaluation` (29a19c82f).
- Pack format v0 spec plus the `Uo.Pack` library; builds.
- Launched subagents:
  - **Converter:** build `poc/Uo.Converter` and produce packs.
  - **Renderer:** build `poc/Uo.Render`, synthetic data first, then packs.
  - **Flecs.NET adapter** for EcsBench (checksums plus an AOT smoke test only; final timing runs are done by the main session when the machine is quiet).
  - **FNA 26.10 upgrade** in a worktree, branch `chore/fna-26.10` off `main`.

### 2026-10-24 — FNA 26.10 upgrade done (branch `chore/fna-26.10`, `41f3802e9`, not pushed)

- **Almost drop-in.** It compiles unchanged, but two runtime crashes needed fixes:
  - FNA now checks that a vertex type's size equals its stride. `PositionNormalTextureColor4` packs a whole quad, so `Batcher2D`/`MeshLayer` now pass its `VertexDeclaration` explicitly.
  - `AudioManager` probed audio hardware with sample rate 0; it now uses 22050.
- **Natives:** SDL3 3.2.23 → 3.4.19, plus FNA3D, FAudio and Theorafile, from FNA's fnalibs-dailies CI artifacts (2026-10-24). These are dailies, not a pinned release. Windows/Linux/macOS natives are replaced, but only Windows was launch-tested.
- 185/185 unit tests pass. The client reaches the login screen on the Vulkan driver.

### 2026-10-24 — Flecs.NET adapter added (EcsBench + `research/ecs-bench/FlecsAdapter.cs`)

- Flecs.NET.Release 4.0.3. Checksums match the other libraries under both JIT and AOT. 0 AOT warnings. `flecs.dll` adds about 700 KB (a static-link option exists).
- Preliminary, single runs on a busy machine: iteration ties Friflo. Per-entity operations (lookups, create/destroy, tag toggles) are 1.5–2× slower than Friflo, because each is a P/Invoke.
- Ergonomics: field-index access, several overlapping iteration styles, uncached-by-default queries, a fast create path that leaves memory uninitialized. Harder for developers new to ECS than Friflo.
- TODO: final 3-round timings once no builds are running.

### 2026-10-24 — Policy: `poc/modern-client` is the integration branch

The owner asked that everything definitive (e.g. the FNA 26.10 upgrade) land on `poc/modern-client`. `chore/fna-26.10` is merged (6e9675e6e).
**Pending:** run `git submodule update --init --recursive external/FNA` once the renderer agent finishes. The gitlink points at 26.10, but the checkout is still 25.09, kept deliberately so the SDL3-CS source doesn't change under the agent.

### 2026-10-24 — CP2: converter done (ca5f4d406)

- **Run:** `dotnet run -c Release --project poc/Uo.Converter`. Options: `--uo`, `--out`, `--debug`, `--xfiles`, `--no-maps`, `--verify-only`.
- **Performance:** full conversion of client 7.0.103 in about 4 s. Output is 1.1 GB: art 663 MB in 31 pages, texmaps 86 MB in 4 pages, maps 351 MB covering facets 0–5.
- **Verified:** sha256, sprite rects, flags, pixel equality on 6k sampled sprites, regions. Radar and sprite PNGs checked by eye.
- **Notes for the renderer:**
  - pixels are RGBA bytes;
  - land is a 44×44 diamond anchored at the tile's top-left;
  - statics draw at x − (OrigW/2 − 22), y − (OrigH − 44), then add Trim;
  - hue h is LUT row h−1, shade = r×31; PartialHue applies only to gray pixels.
- **Deviations from the spec:**
  - duplicate images share one atlas rect;
  - fully transparent art is marked absent;
  - `FacetFileIo` helper added; JSON is camelCase;
  - mapdif/stadif patches skipped (they're server-driven).
- **The ClassicUO loaders aren't thread-safe** (shared seek position). The converter uses one `UOFileManager` per worker.
- **v1 size win:** BC7 would cut the art from 663 MB to about 170 MB. Regions compress well too (Brotli).

## Next

- Review each agent's output and commit it.
- Run final Flecs.NET timing rounds when no builds are running, then write `docs/modern-client/ecs-decision.md`.
- Integrate the renderer with real packs, measure, write `docs/modern-client/renderer-decision.md`.
