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

### 2026-10-24 — CP3: renderer PoC done (e6c6bb043)

- **Run:** `poc/Uo.Render/bin/Release/net10.0/win-x64/Uo.Render.exe` (or `--pack`, `--at x y zoom`, `--bench`, `--screenshot`).
- **Architecture:**
  - one world draw call: 32-byte instances pulled from a storage buffer, chunks found by binary search over a visible-chunk table;
  - Texture2DArray of 24 layers with a GPU page table, LRU eviction and an upload budget of 2 pages per frame;
  - chunks baked on 8 workers into a sub-allocated persistent buffer;
  - ClassicUO PriorityZ/CalculateDepthZ ported to an integer depth key, with tie-breakers;
  - flat and stretched (texmap) land with ClassicUO normals and lighting;
  - normal and partial hue.
- **Verified:** screenshots of Britain at several zooms and of mountains (by eye); a magenta-clear coverage test; deterministic across streaming histories; AOT output pixel-identical to JIT.
- **Owner-reported issues:**
  - Black spots: transient, from chunks not yet streamed. No longer reproduces.
  - Flicker: depth-key ties across chunks (16 pairs in Britain, now 0), plus edge pop-in under CPU load. Fixed with prefetch bias, visible-first dispatch and eviction hysteresis.
- **Bench, under full CPU load (to rerun quiet):** about 0.27 ms CPU per frame (median), 0 GC, about 0.2 B allocated per frame. Rerun with `Uo.Render.exe --bench`.
- **Gaps:**
  - D3D12 only (the SDK dxc lacks SPIR-V);
  - not rendered yet: animated water, seasons, roof hiding, mobiles, items, multis;
  - NoDraw-by-name filter missing;
  - v1 (BC7/Brotli) packs not yet supported.

### 2026-10-24 — CP4: Uo.Net done (ba37bcbf8)

- **Run:** `poc\Uo.Net.Cli\scripts\start-modernuo.ps1`, then `dotnet run -c Release --project poc\Uo.Net.Cli -- --runs 3` with `UO_USER`/`UO_PASS` set. Stop the server with `stop-modernuo.ps1` (Ctrl+Break, which goes through ModernUO's normal shutdown and never force-kills).
- **Server:** the owner's existing build in `C:\Repositories\ModernUO\Distribution`, unchanged. Its autosave writes backups to `Distribution\Backups\Automatic` while it runs.
- **Verified live:** 3/3, 2/2 and 1/1 NativeAOT runs. Every run reached the world and got 24/24 moves acked with 0 rejects. World entry takes 72–157 ms. Move RTT on localhost: median 0.16–0.28 ms. No allocations per packet in steady state. Byte-by-byte reads reassemble correctly.
- **API for integration:**
  - `IGameClient`: `Connect`, `Poll(waitMs)`, `RequestMove(dir)` (returns the sequence number), `Say`, `Disconnect`.
  - Events arrive through `IWorldSink` as structs (mobile, item, equip, remove, move ack/reject with RTT, sequence reset, map).
  - Poll once per frame on one thread.
- **Quirks:**
  - Every 0xBD version request must be answered.
  - The game connection starts with the raw 4-byte key. All server traffic is Huffman-compressed; nothing from the client is.
  - The move sequence wraps 255→1 and resets to 0 on a 0x20 for the player, a map change, or a reject.
  - Steps take 400 ms walking, 200 ms running; at most 5 moves unacknowledged.
  - Idle timeout is 30 s, so the client pings every 10 s.
- **Gaps:**
  - 0xF8 character creation is untested live, to avoid writing to the owner's save.
  - About 15 packet ids are skipped using the length table.
  - Connect is synchronous.
  - The 1/256 case where the key's first byte is 0xEF isn't handled.

## Phase 3 plan (engine vertical slice)

1. **Converter:** finish v1 (BC7 + Brotli), add a NoDraw flag (tiledata name rule), then animation conversion (UOP AnimationFrame → atlas pages + frame tables).
2. **Renderer:**
   - v1 pack support (per-page BC7 format, worker-side Brotli);
   - Vulkan via SPIR-V (official DXC release);
   - mobiles and items as dynamic instances;
   - animation playback.
3. **Network and server:** a local ModernUO server (out-of-tree publish, no changes to the ModernUO repo) plus `poc/Uo.Net`, a minimal classic-protocol client (login, enter world, entity stream, walk requests).
4. **Integration** (`poc/Uo.Client`): Uo.Net → Friflo world (dynamic entities) → render extract → Uo.Render. Player movement with client-side prediction. This is the proof of the full stack.

## Next

- Review each agent's output and commit it.
- Run final Flecs.NET timing rounds when no builds are running, then write `docs/modern-client/ecs-decision.md`.
- Integrate the renderer with real packs, measure, write `docs/modern-client/renderer-decision.md`.
