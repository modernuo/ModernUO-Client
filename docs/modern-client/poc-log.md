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

### 2026-10-24 — Renderer formats + expansion kernel (8b4d73763, 2c72f83b8)

- **Formats:** Uo.Render reads RGBA8, BC7 and B5G5R5A1 packs (one format per pack, checked at startup). Brotli pages and regions are decoded on workers.
- **Bench** (`--bench-formats`, 4K):
  - 16-bit vs RGBA8: 0.17 vs 0.21 ms CPU per frame, 255 vs 511 MiB of page VRAM.
  - Raw vs Brotli 16-bit pages: identical frame times. Worker cost per page is 2.9 ms raw vs 25 ms decode.
  - This led to the decision: lossless everywhere, Brotli for transport only.
- **`PixelExpand`:** a portable SIMD (Vector256/128, so also WASM SIMD128) B5G5R5A1→RGBA8 kernel for GPUs that can't sample 16-bit. ClassicUO's 5→8-bit table equals `(c·1053)>>7`. 0 mismatches over all 65536 values. Per page: 1.2 ms with AVX2, 1.5 ms with Vector128 only, 20.8 ms with a scalar lookup table. WASM is unmeasured (needs the wasm-tools workload).

### 2026-10-24 — CP5: vertical slice done (2c6379030)

- **Run:** `poc/Uo.Client/bin/Release/net10.0/win-x64/Uo.Client.exe` (offline fake server by default), `--live --go "1495 1629"` with `UO_USER`/`UO_PASS`, `--test` for the prediction tests.
- **Frame loop, single-threaded:**
  1. input;
  2. `client.Poll()` writes to the Friflo world;
  3. `Walker.Update` predicts and sends move requests;
  4. the camera follows the interpolated player;
  5. map streaming and the static draw list;
  6. render extract: ECS → per-frame dynamic instance range;
  7. one depth-keyed draw call for statics and dynamics together.
- **Prediction:** a port of ClassicUO's CanWalk/CalculateNewZ against pack data. It handles pacing, the cap of 5 pending moves, turn-only requests, snap on reject, and reset on teleport.
- **Verified:**
  - Live against ModernUO (JIT and AOT): 0 rejects, predicted z matched the server on stairs and slopes, server items render.
  - Offline tests cover reject/snap, the pending cap and teleport.
  - 5000 mobiles on screen: 0.10 ms CPU per frame, 0.05 ms extract, 0 GC.
  - NativeAOT warnings come only from Friflo's own assemblies.
- **Gaps:**
  - Mobiles are placeholders until animations land (`IMobileSprites` seam).
  - Equipment isn't drawn, multis aren't rendered, and stacks have no offsets.
  - Walk-map region loads are synchronous: 3–6 ms, a possible hitch when crossing a region.

## Phase 3 plan (engine vertical slice)

1. **Converter:** finish v1 (BC7 + Brotli), add a NoDraw flag (tiledata name rule), then animation conversion (UOP AnimationFrame → atlas pages + frame tables).
2. **Renderer:**
   - v1 pack support (per-page BC7 format, worker-side Brotli);
   - Vulkan via SPIR-V (official DXC release);
   - mobiles and items as dynamic instances;
   - animation playback.
3. **Network and server:** a local ModernUO server (out-of-tree publish, no changes to the ModernUO repo) plus `poc/Uo.Net`, a minimal classic-protocol client (login, enter world, entity stream, walk requests).
4. **Integration** (`poc/Uo.Client`): Uo.Net → Friflo world (dynamic entities) → render extract → Uo.Render. Player movement with client-side prediction. This is the proof of the full stack.

### 2026-10-24 — CP6: animations + NoDraw in the converter (1c7c9a60c)

- **Coverage:** 963 bodies and 993k frames in pack pages. Mirrored directions reference the stored frames. Body.def, Bodyconv and Corpse.def are resolved at conversion time.
- **Verified:** 111k frames are bit-exact against the ClassicUO loader.
- **NoDraw:** it mirrors ClassicUO's `CanBeDrawn` static rules, which go beyond the name rule. Stored as `TilePackFlags`.
- **ClassicUO bug:** its animation loader crashes under parallel use (unpinned `Unsafe.AsPointer`, concurrent native zlib). The converter uses managed readers instead.
- **Size problem:** animations are 1.1 GB with Brotli but 6.7 GiB raw, so "raw at install" fails for them. The owner approved investigating:
  1. chunked Brotli (Kind=2);
  2. 1024² animation pages;
  3. prototyping an indexed 8-bit format plus palette, which stays lossless, with the shader doing a manual palette lookup and filtering.

### 2026-10-24 — CP7: ECS mirror in ClassicUO; animated mobiles; canonical expansion

- **ECS mirror (3c6335e77):**
  - New project `src/ClassicUO.Ecs` (Friflo). Every Item and Mobile in World is mirrored, keyed by serial.
  - Hooked at World create/remove/clear, Entity destroy and direction, GameObject tile/screen position, and the packet updates.
  - The `-ecscheck N` consistency check found 0 mismatches over 9,754 live checks: bank, equip, drop, 4 facets and logout, under both JIT and NativeAOT.
  - `DynamicSpriteExtract` costs 2–3 µs per frame.
  - In-process relog doesn't work, an existing ClassicUO networking issue. A stuck socket got localhost auto-denylisted, so the auto-denylist is now disabled on the local server.
- **Animated mobiles in the slice (55557c366):** Indexed8 plus the palette path in one draw call, with mounts and equipment. 5000 animated mobiles cost 0.37 ms CPU with 0 GC.
- **Canonical 5→8 expansion is bit replication `(c<<3)|(c>>2)` (136516a81)**, as measured from GPU sampling. Native 16-bit, the RGBA8 expansion and Indexed8 are now bit-identical: 0 px difference at zoom 1 and 0.5, on D3D12 and Vulkan.
- **Incident:** a leaking verification run reached 204 GB committed and starved the machine. Every agent process now runs with an 8 GB GC heap limit, and a watchdog kills any process above 16 GB.

### 2026-10-24 — Step 8a: Uo.Render library + external-device mode (c18e96a6b)

- **Project split:** `poc/Uo.Render` is now a library. The exe is `poc/Uo.Render.App` (**`Uo.Render.App.exe`**; older notes say `Uo.Render.exe`), with the same flags.
- **External mode:** `WorldRenderer.CreateExternal(device, format, pack, options)`, then each frame:
  1. `Prepare(camera)`;
  2. land/static overrides (alpha, hidden, highlight) and the dynamic span;
  3. `Render(target, w, h, format, load)`, which submits its own command buffer.
- **Verified:** `--external-test` simulates FNA (its own device, window and RT, blitted to the swapchain) and is pixel-identical to normal mode on D3D12 and Vulkan, including a resize.
- **Translucent sprites** use a second draw, so the world is 2 draw calls when anything is translucent.
- **Gaps:**
  - The world is drawn at submission time, so an FNA `Clear()` of the world RT in Gpu mode would wipe it.
  - The circle of transparency is per-sprite alpha only, not a shader circle.

### 2026-10-24 — CP8: ClassicUO is pack-only (f89c6107f)

- **Coverage:** art, texmaps, maps, tiledata, hues, radarcol, animations, animdata, multis, lights, tileart and equipconv load only from the pack, using `-pack <dir>`, `./pack`, or the UO dir.
- **Removed:** about 5.2k lines of MUL/UOP code, including UltimaLive, verdata and map patches. The old loaders are frozen in `tools/ClassicUO.Assets.Mul` for the converter.
- **Verified:**
  - every entry matches the MUL loaders exactly at 5 bits;
  - all 2.27M animation frames show 0 mismatches, apart from two old-loader bugs (UOP frame-0 gaps, MUL→UOP Body.def aliases) where the pack is now correct;
  - screenshots at 8 spots are within 1/255;
  - live checks: backpack, paperdoll, minimap, world map, lights, animated statics and picking;
  - NativeAOT runs on the pack.
- **Gaps:**
  - Loose `Art/` override folders aren't read by the converter yet.
  - Effects with no animdata lose their frame interval.
  - A placed house hasn't been seen live.

### 2026-10-24 — CP9: FrameStats, uncapped FPS, quiet baseline (941f3fc94)

- **FrameStats** (`CUO_FRAMESTATS`): per-phase avg/p50/p95/p99 including Present, GC and batcher counters, plus a `FRAMESTATS {json}` line. Overhead is about 1% when on and zero when off.
- **FPS and VSync:** uncapped by default, with a runtime VSync toggle and Options checkboxes. Reduce-FPS-when-inactive is off by default.
- **AOT:** `IlcInstructionSet=x86-64-v3` for win-x64 AOT. It made no measurable difference. AOT is still 15–20% slower than JIT on Draw/UI, probably because it lacks dynamic PGO.
- **Baseline** (idle machine, 1920×1080, JIT p50, ms):

  | Spot | Frame | Draw |
  |---|---|---|
  | Britain | 0.52 (1439 fps) | 0.33 |
  | Bank | 0.48 | — |
  | Zoomed out | 1.32 (746 fps) | 1.20 |

  - Update is about 0.012 ms.
  - At 3840×2054 the frame is 1.33 ms, mostly Present (0.45–0.53 ms) plus the composite. **The owner's 600–700 fps reading was largely window size, not Update.**
  - Zoomed out at 4K: 5.7 ms, 40.5k render-list entries and 1,041 draws.
- **Top costs:**
  1. World prepare + draw: 0.22 / 1.09 / 5.2 ms. Uo.Render replaces this.
  2. FNA per-batch overhead.
  3. Present + composite at 4K.
  4. UI allocations of 17.7 KB per frame, about 80% from closures.
- **Next:** step 8b, the Gpu world renderer in ClassicUO.

### 2026-10-24 — Step 8: ClassicUO renders its world with Uo.Render (uncommitted)

- **Switch:** `world_renderer` in settings.json (`legacy` default, `gpu`), CLI `-worldrenderer gpu|legacy`, in-game `-worldrenderer [gpu|legacy]` (no argument toggles). No SDL_GPU device from FNA3D, or a facet that fails to open: Legacy, with a log line.
- **Per frame (Gpu):** `GameScene.DrawWorldGpu`.
  1. `FillGameObjectList`/`AddTileToRenderList` run unchanged (visibility, alpha, picking). Where they decide to draw an object they stamp `DrawnFrame` and call `GpuOnDrawn`, which emits per-sprite overrides (alpha, out-of-range/dead tint) for land and statics. After each tile, `CollectGpuHidden` hides the baked land and statics the fill skipped (roofs, above max Z) where they would be on screen. The selected land/static gets a highlight override.
  2. Items, multis (meshed and not), mobiles and corpses: their views' `Draw` runs with `UltimaBatcher2D.Capture` set; `GpuWorldRenderer` maps each atlas sprite back to its pack sprite (`PackSpriteMap`, registered by `Art`/`Animations`) and writes a `SpriteInstance` (trim-clipped, mirrored, strips, shadows, hue mode, depth key from the draw's float depth). Animation selection, layering, mounts, hues and light gathering stay in the views. Statics whose drawn graphic differs from the pack's (season, animated, stumps) are hidden and captured; trees/foliage/rocks contribute only their shadow.
  3. FNA draws effects, object handles, the multi preview, weather and the selection rectangle into the world RT (LOAD). Then `WorldRenderer.Prepare` + `Render(world RT texture, CLEAR)` submits before FNA's frame, so the GPU runs the world first. The world RT is `PreserveContents` in Gpu mode so FNA's bind no longer clears it; FNA gets a fresh depth buffer.
- **Packs:** `PackSet` takes ClassicUO's loaded tables (`PackTables`: sprites, texmaps, hues, anim bodies/dirs/frames, about 55 MB not read twice) and `WithFacet` re-targets it; a facet change recreates the world pass. The ECS `DynamicSpriteExtract` is not used; `GpuWorldRenderer.DynamicBuffer` is the seam.
- **Parity** (world RT, 1280×720 logical at 1920×1080, Gpu vs Legacy in the same session): Britain 7.3% of pixels differ by >8 (0.5% after a 4×4 box filter), bank 16% (1.5%: two NPCs moved), zoom-in 8.8% (1.2%), zoom-out 75% (24%, mean 4.7). Nearly all of it is filtering: stretched land is point-sampled by Legacy and filtered by Uo.Render, and zoomed out Legacy aliases while Uo.Render uses mips. Light RTs are identical (night, 0x1A). A healer camp (multi), a 120-mobile crowd and Felucca render; the facet switch works.
- **Picking:** hover over a static, land, a ground item and the player picks the same object in both modes (2 s of samples each); clicks reach the server.
- **Perf** (JIT, 1920×1080, ms avg; Legacy → Gpu): world draw 0.12 → 0.06 (Britain), 0.57 → 0.09 (zoom-out), 2.57 → 0.19 (4K zoom-out); world prepare rises 0.03–0.1 (the collect and the views' capture moved there); Draw total 0.35 → 0.33, 1.33 → 0.92, 5.68 → 3.37. FNA draw calls 89 → 20 and 326 → 17; the world is 2 draws. Details: classicuo-perf-baseline.md ("Step 8").
- **Gaps:**
  - FNA-drawn world-space layers (effects, weather, multi preview) are not depth-tested against the GPU world: they draw on top.
  - Circle of transparency in shader mode (the default type) is not applied to baked statics; gradient mode works (CPU alpha).
  - Animated-water wobble, the sitting deformation and rotated sprites are dropped or drawn plain; land season graphics are not swapped.
  - The prepare phase is still ClassicUO's full walk (0.5 ms zoomed out, 2.9 ms at 4K): the next lever. Roof hiding means hundreds to thousands of overrides.
  - Item/mobile capture costs what drawing them did (120 mobiles: 0.2 ms); the ECS extract would remove the view logic.

### 2026-10-24 — Step 9: Gpu world parity for general play (uncommitted)

- **Shader-side z data:** baked land and statics now carry `FlagZTest`. Statics pack (u8)z, tiledata height and bits (roof, `Static._canBeTransparent`, no-circle) into the instance's free `Corners` word; flat land packs z and a land bit; stretched land already has its z in the top corner. New uniforms: `MaxZ`, `MaxGroundZ`, `ZFlags`, the circle of transparency (centre, radius, player z + 5) and `TintRow`.
- **1. Circle of transparency, shader mode** (`WorldRenderer.SetCircleOfTransparency`):
  - The shader runs `Static.TransparentTest` per baked static. Trees (a host `StaticNoCot` table from `StaticFilters.IsTree`) and foliage are exempt, as in `StaticView`. Captured sprites whose hue alpha is > 1 (multis, replaced statics) get `LightCot`.
  - An opaque instance is drawn by the opaque pass with the circle cut out. It is drawn again by the translucent pass for the faded ring only (0.85–1.0 r, alpha t³, the `IsometricWorld.fx` formula). The CPU lists ring candidates by scanning only the chunks inside the circle's bounding box: 8 instances at the bank. A translucent instance gets the whole effect in its own pass.
  - **Parity:** inside a 120 px circle around the player, standing against the bank's front wall, Gpu vs Legacy differ by more than 8 on 0.06–0.27% of pixels. I looked at the crops: the wall cut and the ring match. Zoomed out, the cut matches too; the rest of that view is the known mip-vs-alias difference.
- **2. Roof and max-Z hiding in the shader** (`SetZHiding(_maxZ, _maxGroundZ, _noDrawRoofs)`):
  - Hidden: land with z > max ground z, statics with z ≥ max z, and roofs when roofs are off.
  - Objects still fading out (or unmeshed land, which Legacy draws regardless) get a `ForceShow` override carrying their alpha. Fading back in uses the existing alpha overrides.
  - `CollectGpuHidden` skips what the shader already hides. It now tests land against the real 44×44 box: stretched tiles get a 32-z margin above and MinZ below. The old 108/64 px slack had produced most of the remaining overrides.
  - **Overrides:** bank 478 → 39, Britain 129 → 71, zoomed out 232 → 182. Bank world draw 0.082 → 0.057 ms; world prepare unchanged (0.130 → 0.127). Table below.
  - The baker also applies the converter's `NameNoDraw` flag, which is the tiledata "nodraw" name check.
- **3. Depth for FNA-drawn world-space layers** (chosen approach: capture into Uo.Render, so they share the world's depth key):
  - **Effects** go through `GpuWorldRenderer.DrawObject` like items. The rotated `Batcher2D.Draw` overload gained a capture hook (`ISpriteCapture.DrawRotated`). The instance rotates in the vertex shader (`LightRotated`, u16 angle in `Light` bits 16–31) about the untrimmed rect's corner, as Batcher2D does.
  - **Blends:** Normal is captured. Screen/ScreenMore are captured as additive (`LightAdditive`: premultiplied output with alpha 0, i.e. dst + src·a). Multiply, ScreenLess, NormalHalfTransparent, ShadowBlue and ScreenRed can't be expressed with the premultiplied blend, so they stay FNA draws over the world. So do sprites the capture can't map.
  - **Multi placement preview:** captured too, so it is depth-tested. Not screenshot-verified: it needs a scripted mouse.
  - **Overlays:** weather, the selection rectangle and object handles stay FNA overlays. Legacy draws them at max depth as well.
  - **Checked with client-side effects** (new DevBench `effect:`/`moving:` entries):
    - A fire column under the bank roof is hidden by the roof in both modes; step 8 drew it on top.
    - A Screen-blend sparkle looks the same in both.
    - A moving fireball's rotation matches Legacy.
- **4. Extras:**
  - **Seasonal land:** `WorldRenderer.SetLandRemap` (from `SeasonManager.GetLandSeasonGraphic`) is applied in the bake and rebakes via `ChunkStore.EvictAll`. A season change therefore re-streams the view for a few frames. Winter at Yew: 0.19% of pixels differ.
  - **Rotated sprites:** done (effects).
  - **Left as gaps:** the sitting deformation (needs per-vertex skew; there is no room in the 32-byte instance), the animated-water wobble (off by default) and other scaled sprites.
- **5. Hue modes** (local-only DevBench `itemhue:`/`ghost:` entries, both modes in one session; I looked at each crop):
  - Spectral (an item hued 0x4001) and partial (0x8021) match.
  - Mobile shadows are unchanged from step 8.
  - **Death screen gray:** 2.8% of pixels differ by more than 8 at zoom 1 (0.07% after the 4×4 box filter). The gray is now a `TintRow` uniform: it was about 2,800 per-sprite overrides at zoom 1, and zoomed out it would have overflowed the 8,192-entry pending list. Out-of-range tint stays per-object.
- **General parity** (unchanged from step 8, so nothing regressed):
  - Pixels differing by more than 8, then after the 4×4 box filter: Britain 7.2% / 0.4%, bank 16.1% / 0.9%, zoomed out 75% / 24%.
  - Most of it is filtering. Stretched land is point-sampled by Legacy and filtered by Uo.Render; zoomed out, Legacy aliases while Uo.Render uses mips.
- **Picking** (`CUO_BENCH_PICK`): static, land, ground item and player resolve to the same objects in both modes, and the clicks reach the server.
- **Perf** (JIT, 1920×1080, ms avg; before = HEAD 88f455e4f, mean of 2 runs; after = 1 run). Legacy's code is unchanged, so its rows show the run-to-run noise:

  | spot | mode | world prepare before → after | world draw before → after | Draw total before → after | overrides before → after |
  |---|---|---|---|---|---|
  | britain | legacy | 0.110 → 0.105 | 0.132 → 0.119 | 0.365 → 0.335 | — |
  | britain | gpu | 0.156 → 0.152 | 0.086 → 0.059 | 0.374 → 0.335 | 129 → 71 |
  | bank | gpu | 0.130 → 0.127 | 0.082 → 0.057 | 0.340 → 0.304 | 478 → 39 |
  | zoomout | legacy | 0.556 → 0.531 | 0.558 → 0.558 | 1.239 → 1.210 | — |
  | zoomout | gpu | 0.608 → 0.605 | 0.090 → 0.078 | 0.820 → 0.802 | 232 → 182 |

  - With the circle of transparency on (Gpu): bank front wall, prepare 0.140 and draw 0.057, with 8 ring instances; zoomed out, prepare 0.504 and draw 0.080.
  - FNA draw calls in Gpu mode are 23–24 (24–27 before), because effects left the FNA overlay.
- **Uo.Render.App `--external-test`:** passes on Vulkan and D3D12. The new uniforms default to off, so standalone and slice output is unchanged.
- **Server:** ModernUO received Ctrl+C at 07:44, not from this session. It was restarted with `start-modernuo.ps1` at 07:50 and left running.
- **Remaining gaps:**
  - The effect blends listed above.
  - The sitting deformation, the water wobble and other scaled sprites.
  - Translucent statics (tiledata Translucent → alpha 178) are still per-sprite overrides every frame.
  - The ring-candidate test uses unrotated rects.
  - A season change re-streams the view.
  - The gargoyle exception to NoDiagonal is not baked.
  - The multi preview has not been screenshot-verified.
  - The remaining zoomed-out overrides (182) are the fill's other skips, mostly land at the view edges.

### 2026-10-24 — Step 10 prep: Gpu is the default; one-command FPS test for the owner (uncommitted)

- **Default:** `world_renderer` now defaults to `gpu` (`Settings.cs`). `-worldrenderer legacy`, the in-game `-worldrenderer` toggle and the automatic fallback to Legacy when FNA3D isn't on SDL_GPU are unchanged. The legacy world path is still there; it goes after the owner's test. A settings.json that already saved `"world_renderer": "legacy"` keeps Legacy.
- **DevBench:** new plan entry `size:WxH|max` changes the client area mid-plan (`max` = maximised window), so the 1080p and large-window runs share one login.
- **NativeAOT publish:** `tools/fps-test/publish-aot.ps1` publishes win-x64 x86-64-v3 to `C:\Repositories\cuo-fps-test\bin`, outside the repo. The folder holds the patched FNA3D.dll from `external\x64` and a `build-info.txt` with the commit. It is 88 MB, of which cuo.exe is 17 MB (the rest is mostly pdbs).
- **The test:** `tools/fps-test/run-fps-test.ps1` (README beside it).
  - It prompts for the password (masked); `CUO_FPS_TEST_PASSWORD` is an optional override for unattended runs.
  - It checks the server (127.0.0.1:2593, ModernUO's configured listener), uses a throwaway settings/profile folder under %TEMP%, and sets the GC hard limit, FrameStats, uncapped FPS, VSync off, no inactive throttling and a topmost window.
  - It runs Legacy then Gpu at Britain, the bank, zoomed out (2.5) and a walk at 1920×1080, then zoomed out in a maximised window. That is 10 spots of 3×5 s.
  - It writes `C:\Repositories\cuo-fps-test\results-<timestamp>.md` (machine info, build, a summary table and a Legacy-vs-Gpu table per spot) and prints it.
  - It closes the client and cleans up in `finally`.
- **Dry run** (agent, AOT, Windows PowerShell 5.1): completed in 4.8 min, and the temp folder and `bin\Data` were removed. This time Present showed no 500 ms stalls (p99 of about 4.5–5 ms at 1080p, as in the CP9 baseline), so the numbers look sane:
  - FPS Legacy → Gpu: Britain 1370 → 1520, bank 1477 → 1515, zoomed out 690 → 944, walk 1425 → 1505, maximised (3840×2054) zoomed out 162 → 261.
  - Draw total: 0.42 → 0.34, 0.35 → 0.31, 1.31 → 0.92, 0.38 → 0.34 and 5.92 → 3.61 ms.
  - World prepare is 0.02–0.1 ms higher in Gpu mode, as in step 8.
  - The results file was deleted afterwards. The owner's desktop run is the step 10 verdict.

### 2026-10-24 — Step 10: the owner's FPS test hit a GPU driver reset (uncommitted)

- **What happened:** the owner's run (AOT bf274f4f9) died right after the first `world:legacy` switch. The System log has nvlddmkm event 153 (the NVIDIA driver reset the GPU for the process) in the same second. SDL then printed "Out of device-local memory" and "Out of BAR memory", and the client exited with no crash log and no WER entry.
- **Not reproduced.** All on the same desktop, driver 617.42, with no nvlddmkm event in any of these runs:
  - the AOT baseline: 6 runs of the start of the owner's plan, 3 runs of the full plan with the window on top as the script does, and 12 logins straight into Legacy;
  - the new DevBench `flip:N[:frames[:walk]]` entry, which toggles the world renderer every few frames. Each toggle recreates the world RT.
    - JIT: about 15,000 toggles (every frame, every 2 to 30 frames, with resizes, walking and teleports).
    - The republished AOT build: the same 14,000-toggle plan 4 times under validation (one run with sync validation).
- **Validation:** Khronos validation layer (nightly build in the scratchpad: `VK_LAYER_PATH` + `VK_INSTANCE_LAYERS=VK_LAYER_KHRONOS_validation`), plus sync validation and GPU-AV runs. There is no Vulkan SDK on the machine.
  - The only error: `VUID-VkPipelineRasterizationStateCreateInfo-depthClampEnable-00782` on Uo.Render's two pipelines. SDL maps `enable_depth_clip = false` to depth clamp, and FNA3D creates its device without the depthClamp feature.
  - **Fixed:** the pipelines now use depth clip. The vertex shader saturates depth, so the output is unchanged (`--external-test` is pixel-identical on Vulkan). Every later run had 0 validation errors and 0 sync hazards, toggles included.
- **The switch itself checks out:**
  - The old world RT is released through SDL, which keeps it alive until the command buffers that used it finish.
  - Uo.Render re-fetches the RT's texture every frame, and records and submits its command buffer in one call, so none is left open.
  - Its depth texture follows the RT's size.
  - SDL's Vulkan defragmenter can't move the staging buffers that PageResidency keeps mapped across frames: transfer buffers are dedicated allocations (checked with a scratch test).
- **Not specific to the Gpu renderer:** the machine logged the same event 153 on 2026-07-27 at 13:50:06. That is the same second the stock ClassicUO build of that day (FNA 25.09, FNA3D on SDL_GPU/Vulkan, no Uo.Render) created its first profile and `seasons.txt`, i.e. while it was logging in. Event 153 also appears on 2026-08-11.
- **After the reset:** SDL_GPU doesn't recover from a lost device. A failed submit leaks its command buffer and the references it holds, so every frame's cycled buffers and textures are new allocations. That fits the two SDL memory warnings: they come after the reset, not before it.
- **Diagnostics added:**
  - `Renderer.SubmitFailures` / `LastSubmitError`; the client logs the first failed world submit once.
  - `run-fps-test.ps1` puts GPU driver events (nvlddmkm, Display, …) logged during a failed run into the message and the log, and prints the exit code in hex.
- **Re-published** with `publish-aot.ps1`. `run-fps-test.ps1 -Windows 1` completed in 3.1 min (the results file was deleted). FPS Legacy → Gpu: Britain 1397 → 1366, bank 1442 → 1531, zoomed out 614 → 870, walk 1432 → 1461, maximised zoomed out 158 → 249.
- **Checks:** client Release build, 188 unit tests and `--external-test` on Vulkan (0 validation errors) all pass.
- **Still open:**
  - What triggers the reset.
  - How the client behaves after one. A forced TDR (`dxcap -forcetdr`) would show it, but that resets the display for the whole desktop, so it needs the owner's OK.

## Phase 4 plan (owner, 2026-10-24): bring it into ClassicUO

The owner accepts compressed packs on disk if decompression is fast.
1. **Finalize the file format.** Use the animation storage results, then consolidate v0+v1 into one final spec.
2. **Implement the pack format in the ClassicUO client** (src/). The pack *replaces* MUL/UOP for every converted asset; the owner revised this the same day, so there is no dual path.
3. **Add Friflo ECS side by side** with ClassicUO's World: mirror entities, then convert systems one at a time. The first is the render extract.
4. **Replace ClassicUO's world renderer** with the SDL_GPU renderer, committed on this branch. Prove there is no performance problem in general use cases, with NativeAOT on the desktop machine. The main risk is device sharing with FNA, which draws the UI; a read-only spike is investigating it.

The detailed steps are in [classicuo-integration-plan.md](classicuo-integration-plan.md).

### 2026-10-24 — Checkpoint: Phase 4 underway

- **Committed:**
  - Renderer RGBA8 fallback and Vulkan via SPIR-V (7af7a0ac2).
  - `PixelExpand` rounding like GPU UNORM sampling (bcd3e8e25).
  - Chunked Brotli Kind 2, page-size options and experimental Indexed8 animations (bc12d92ae).
  - The final pack profile decision (500dadfac): 16-bit art with Kind 2 at 1 MiB chunks; Indexed8 1024 animations with no mips.
- **Integration spike conclusion:** share FNA3D's SDL_GPU device through a small FNA3D patch; the world renders into `WorldRenderTarget`.
- **Agents in flight:**
  - FNA 26.10 submodule update, the FNA3D interop patch (with a self-built FNA3D.dll), and the ClassicUO NativeAOT baseline;
  - the ECS mirror in ClassicUO;
  - format 1.0: consolidated spec, ship-profile defaults, completeness tables (tiledata, raw hues, radarcol, multis, lights, equipconv), plus a full conversion into `uo-packs/classic-1.0`;
  - the renderer: Kind 2 pages, an Indexed8 animation path, and animated mobiles in Uo.Client.

### 2026-10-24 — Format 1.0, FNA patch, ClassicUO AOT baseline

- **Pack format 1.0 (5cb439549):**
  - `pack-format.md` is now the authoritative spec, and the converter's default output is the ship profile.
  - Full run 64 s, 825 MiB: art 68, tex 17, anims 675, maps 57, tables 8.
  - New tables are verified field-exact against ClassicUO's loaders.
- **FNA 26.10 checkout and FNA3D interop patch (1a984f8c6):**
  - FNA3D exposes the SDL_GPU device and native textures. The patch lives in `external/patches`, plus local-only submodule commits; FNA3D's policy forbids AI-generated upstream contributions.
  - `FNA3D.dll` is built by us for win-x64 only.
  - The shared device is **Vulkan**, because MojoShader offers only SPIR-V.
  - `WorldRenderTarget` exposes a non-null `SDL_GPUTexture*`.
- **ClassicUO NativeAOT baseline** (`classicuo-aot-baseline.md`):
  - It publishes as a standalone exe with only 3 trim warnings of its own.
  - Britain at zoom 1: JIT frame p50 1.33 ms, scene draw 0.32 ms; AOT 1.35–1.47 ms and 0.36 ms (10–15% slower).
  - 18 KB allocated per frame in both.
  - To try: `IlcInstructionSet=x86-x64-v3`.
- **In flight:**
  - the ECS mirror on account kbatman2;
  - the renderer: Kind 2, Indexed8 and animated mobiles in the slice;
  - pack-only assets in ClassicUO plus deleting the MUL loaders (steps 4–6).

## Next

- Review and commit each agent's output.
- Then:
  - pack-backed tiledata, hues and maps in ClassicUO (plan step 4);
  - pack-backed art, texmaps and animations (step 5);
  - delete the MUL loaders (step 6);
  - Uo.Render as a library on FNA's device (step 8).
- Final Flecs.NET timing rounds on a quiet machine, then mark ecs-decision.md accepted. Decide between the preview NuGet package and a pinned commit.
- Write renderer-decision.md.

## Step 10 — owner's NativeAOT FPS proof (2026-10-24)

The owner ran `tools/fps-test/run-fps-test.ps1` on the desktop with the display on (build 0684ab8d1, RTX 3090 Ti via Vulkan, 3840x2160 @ 143 Hz, DPI 1.5). Full results: [fps-test-results-2026-10-24.md](fps-test-results-2026-10-24.md).

| Spot | Legacy FPS | Gpu FPS | Change |
|---|---|---|---|
| Britain | 1234 | 1348 | +9% |
| Britain bank | 1298 | 1388 | +7% |
| Zoomed out | 539 | 772 | +43% |
| Walk | 1243 | 1375 | +11% |
| Zoomed out, 3840x2054 | 148 | 232 | +57% |

- **Gpu wins every spot,** with lower frame p50 and p95 everywhere and lower p99 in the heavy scenes. World draw drops 46–92%; FNA draw calls drop from 93–1048 to 25.
- **World prepare is 2–35% higher under Gpu** (override collection plus view capture). At 4K zoomed out it is 3.6 ms of the 4.1 ms Draw, so ClassicUO's per-tile walk is now the top cost. It is the first target of the improvement phase (ECS-driven visibility, screen positions recomputed only on change, no per-sprite capture for static ground items).
- Allocation is unchanged at about 17.5 KB per frame; the UI closure fixes in perf-opportunities.md remain.
- **The Phase 4 renderer goal is met:** no performance regression in general use under NativeAOT on this machine, and large gains in heavy scenes.

### 2026-10-24 — Device-lost handling

- **What FNA3D and SDL do on a lost device.** Details and line numbers: [device-lost-leak.md](device-lost-leak.md).
  - FNA3D's SDL_GPU driver ignores failed submits and swapchain acquires and keeps rendering. It reports an error only in its fence/stall path, and FNA then throws out of the native log callback. That is the stock client's 2026-07-27 InvalidOperationException and the 2026-08-11 NativeAOT fail-fast.
  - SDL 3.4.19's Vulkan backend never cleans a command buffer again after `VK_ERROR_DEVICE_LOST`. Each frame allocates a new command buffer, uniform buffers and cycled transfer buffers (FNA3D's 16 MiB upload buffer, Uo.Render's 8 MiB frame buffer).
  - The only signal is the text in `SDL_GetError()`.
- **Handler (`GpuDeviceLost`).**
  - **Detection:** after every present, SDL's error text is checked for `DEVICE_LOST`/`REMOVED`/`HUNG`/`RESET` (raw pointer, no allocation). A failed Uo.Render acquire or submit and an FNA error with device-lost text also count.
  - **Then:** log the error; save the profile, macros, info bars and settings and disconnect, without GPU teardown; show "The graphics driver reset (GPU device lost). ClassicUO will close." (skipped with `-nomessagebox`, DevBench or `CUO_BENCH_*`); exit with code `0xDEAD`.
  - **Diagnostics:** `CUO_DEVICE_LOST_NOEXIT=1` keeps running and logs frames and private bytes. `CUO_SIMULATE_DEVICE_LOST=<s>` injects the Vulkan error text, to test without a reset.
  - **Not done:** recovery (recreating the device).
- **run-fps-test.ps1** reports exit code 0xDEAD as "GPU device lost (driver reset)" with the driver events, and waits up to 5 s for the event to reach the log.
- **Forced TDR: blocked.** `dxcap -forcetdr` needs admin ("Please run as administrator"). It was tried once at 21:32:29 and caused no reset. Simulated losses passed for JIT and AOT in Gpu and Legacy mode, and with and without the message box: profile saved, exit 0xDEAD within a frame, memory flat.
- **A real reset during the run** (21:36:29, AOT `run-fps-test.ps1 -Windows 1`, at the first `world:legacy` switch):
  - The handler caught it (`vkQueueSubmit VK_ERROR_DEVICE_LOST`), saved and exited 0xDEAD in the same second.
  - The Aftermath monitor's dump `C:\CrashDumps\aftermath\cuo-16164-20261024_213629.nv-gpudmp` is a GPU page fault (`Error_DMA_PageFault`): a ROP write in FNA3D's SwapBuffers command buffer, at a VA inside a 2048×2048×24-layer, 12-mip texture created 17 ms earlier. That is a copy of Uo.Render's page atlas, which only SDL's Vulkan defragmenter creates. The atlas's 320 MiB allocation shares its 64 MiB tail with FNA's 3.75 MiB render targets, so it becomes a defrag candidate.
  - A forced defrag that moved the atlas did not fault, and an identical fps-test rerun did not reproduce it, so the trigger is not settled. The candidates are in device-lost-leak.md.
  - Uo.Render now names its textures; the names show in dumps only from a debug-mode device (Debug build).
- **Checks:** client Release build, 188 unit tests and `--external-test` on Vulkan pass. Re-published with `publish-aot.ps1`.

## B1–B5 — batcher state tracking and zero-allocation UI (2026-10-24)

Branch `poc/batcher-ui-alloc` (worktree, from 43dec9ae4): 99ced3184 and 2eedf61db (DevBench), 2202efb1f (B1), ff81fd332 (B2–B5 and the other allocations).

- **Scene:** the DevBench `gumps` plan entry opens the paperdoll, backpack, journal and status bar at fixed positions. Shots (`CUO_BENCH_SHOTS`) now also save the UI render target as `<entry>_<renderer>_ui.png`. Gpu world renderer, 1080p, Britain (1495,1629) and the bank (1430,1690). DevBench logs basic-effect applies per frame.
- **Allocation** (FrameStats, all threads):

| build | spot | alloc/frame | gen0/s | GC pause |
|---|---|---|---|---|
| JIT before | Britain / bank | 27.6 / 27.6 KB | 1.6–2.2 / 1.5–2.2 | 0.4–0.9 ms/s |
| JIT after | Britain / bank | 69–82 / 21–125 B | 0 / 0 | 0 |
| NativeAOT after | Britain / bank | 252 / 273 B | 0 / 0 | 0 |

- **Remaining allocations** (EventPipe AllocationTick, 14 s windows):
  - asset streaming: animation frames decoded when a mobile shows a new direction (`PackPages.DecodeChunk` byte[] and `PackSprites.DecodeIndexed` uint[], via `Animations.LoadDirection` from `Mobile.Draw` or `World.Update`);
  - the FrameStats report string every 5 s;
  - nothing else per frame.
- **Before, by source:**
  - UI closures and display classes (GumpPic, Button, GumpPicTiled, Checkbox, HitBox, ItemGump, Line) about 55%;
  - `Control.DrawDebug` display class about 30% (hoisted to method entry, so allocated even with debug off);
  - nested `new RenderLists()` about 5%;
  - the PaperDoll tooltip string about 3%;
  - then BorderControl, ScrollFlag, ScrollBar, AlphaBlendControl, SystemChatControl, Container/TextContainerGump, JournalGump, a new lights `BlendState` plus two lambdas every frame in GameScene, boxed dictionary enumerators in `UpdateTextServerEntities`, and DevBench's per-frame `Split`.
- **B1:** basic-effect `Pass.Apply()` calls drop from about 16 to 3 per frame (each flush and mesh run before; now once per `Begin` plus parameter changes). The batcher's FNA time and UI draw are about 10–15% lower in the cleanest A/B, but this session had three other ClassicUO clients running (other agents, then the owner). Frame times swung ±40% between runs, so read the timing columns as "no regression", not as a gain.
  - JIT before / after at Britain: UI draw 0.117 → 0.103 ms, batcher FNA 0.119 → 0.102 ms, Draw 0.477 → 0.400 ms. The bank run went the other way under load.
  - NativeAOT (after only) ran while the owner's launcher and two clients were active (142–498 fps), so its timings aren't comparable.
  - Flushes are unchanged (16) and draw calls 23–24.
- **UI parity:**
  - Before/after UI render-target shots match pixel for pixel except the journal text. Its content differs between runs (live server messages): checked the diff overlay and a side-by-side crop.
  - The paperdoll, backpack, status, reward HtmlControl, top bar, chat text box and journal frame and scroll flag are identical.
  - World shots differ only in 89 px on an animated mobile.
- **Tests:** 191 pass (188 plus 3 new: GumpCommand fields, deferred dispatch, zero-allocation queueing). The NativeAOT publish has no new trim warnings.
- **SIMD (owner question):** micro-benchmark (scratch `vbench`). Scalar vertex generation costs 8.5 ns/sprite, Vector128 7.3 ns (bit-exact), plus a 3.1 ns copy. A 32-byte B6 instance costs 3.5 ns plus a 0.5 ns copy. Not worth SIMD at UI sprite counts. B6 pays off only for thousands of batched sprites, and it needs instancing; reported, not started.
- **Files outside the batcher/UI that changed:**
  - GameController.cs (the device-loss agent's file): `SaveWorldShot` also saves the UI render target, +8 lines;
  - GameScene.cs: the lights statics, and the `UpdateTextServerEntities` signature next to `FillGameObjectList`;
  - none of GameSceneDrawingSorting.cs, GameSceneGpuWorld.cs or FrameStats.cs.

## W3: the ECS extract is the Gpu world's source for mobiles and art items (2026-10-25)

Branch `worktree-agent-ab8d28b4333136756`, started on 67141bf6e, merged with poc/modern-client 7d0a74a92; final numbers measured on that merge (base build = 7d0a74a92). Commits: 38c35fabd (mobiles), 21adf7688 (driver cleanup commands), 918786113 (art items), merge cc9bb72aa, docs. JIT Release, 1920×1080, kbatman, two other agents' clients on the GPU during most runs. Procedure: [testing/ecs-animation.md](testing/ecs-animation.md).

- **Switch:** setting `gpu_ecs_sprites` (default on), `-ecssprites [on|off]`, DevBench `ecssprites:on|off`, driver `ecssprites on|off`. Off = the view capture, unchanged.
- **Mobiles** (`GpuWorldRenderer.TryDrawMobileEcs`): `MobileAnimationSystem.Update` then `MobileSpriteExtract.Emit`, copied into the dynamic list. What `MobileView.Draw` did besides drawing is reproduced:
  - `FrameInfo` (picking, overhead text, health bars) from the extract's frame bounds;
  - the mirror bit, written back to the Mobile (`ViewFlipped`). The ECS now samples the bit from the Mobile, so both paths share one history and switching mid-session can't start from a stale bit;
  - worn lights: drawn light items (slot bit) at the frame position, light-only items (no equip AnimID, and any light on a non-human body) at the body anchor (`EquipmentSlots.LightItems`, up to 4);
  - the aura (`MobileView.DrawAura`, drawn through the batcher as the capture's fallback did).
- **Art items** (`ItemSpriteExtract`, `DrawItemEcs`): `ItemView.Draw` without the batcher for every item except corpses and multis (those keep the capture): stack copy, animated-art frame, field replacement, container/selection/out-of-range/dead/hidden hues, the light, and the `ArtEntry.Width/Height` draw offset that item picking reads. It samples the Item (until W5); the mirror entity isn't needed for art.
- **Parity harness:** on `CUO_ECS_PARITY` frames the view is drawn under capture as the reference, then the ECS output is diffed and drawn. New: `CompareViewEffects` (mirror, FrameInfo, lights as a set; ECS lights are added, compared, then truncated) and `OnItemCaptured` (instances, drawn result, light).
  - **`CUO_ANIM_SYNC=1` is required for parity now.** Without it the legacy loader decodes a body's first direction on a worker; until it lands the view draws nothing for it, while the extract already draws from the pack. That gave 12 first-sight mismatches in 40k at the bank (capture missing layers), all on the first frames after login or teleport.
  - **Sabotage check:** FrameInfo height +1 and drawn-light x +1 were flagged on every check (43k FrameInfo, 3.3k light mismatches; light-only and non-human lights correctly unaffected).

| Scenario (merged build, ECS on, parity every frame) | Mobile comparisons | Item comparisons | Lights compared | Mismatches |
|---|---|---|---|---|
| bank | 121k | 546k | 0 | 0 |
| items (lit candle, arrow stack, animated art, bandages; night) | 58k | 229k | 57k | 0 |
| lights (torch drawn, light-only 0x1647, non-human body 201; night) | 65k | 2.5k | 31k | 0 |
| 120-NPC crowd | 5.2M | 773k | 0 | 0 |
| cases, mixed (pre-merge build 38c35fabd, mobiles only) | 71k, 636k | – | 0 | 0 |

- **Screenshots** (`ecssprites:off` vs `on` in one session, and base 7d0a74a92 vs new): Britain, zoomed out and night (world and `_lights`) are identical off vs on; the bank differs only where an NPC walked between the two shots. Base vs new differs only in moving creatures (a dog, bank NPCs) and another character standing on the player, which the Legacy shots of both builds show the same way. Legacy world base vs new: the same regions only. UI (`gumps`): journal text only. I looked at every region.
- **Picking** (`CUO_BENCH_PICK=1`): the same static, land, item and mobile in Legacy and Gpu.

**Performance** (µs per frame, averages of two alternated runs per build, 2 windows per spot; the crowd alternates `ecssprites` in one session, 3 rounds, same NPC positions):

| Spot | Mobile capture base → new | Item capture base → new | World prepare base → new | World draw base → new |
|---|---|---|---|---|
| Britain (3–4 mobiles, ~70 items) | 9.4 → 7.1 | 8.7 → 3.5 | 112 → 90 | 84 → 74 |
| Bank (5 mobiles) | 23.1 → 10.3 | 6.7 → 2.0 | 147 → 105 | 93 → 81 |
| Zoomed out ×2.5 | 14.2 → 8.9 | 15.1 → 5.8 | 389 → 331 | 116 → 105 |
| 120-NPC crowd (~102 in view) | 103.8 → 45.3 | 7.2 → 7.9 (2 items) | 168.5 → 109.1 | 70 → 66 |

- The capture columns are the cleanest signal; world prepare and draw totals move 20–40% between runs with the other clients, so only the direction is meaningful there. Update is unchanged (19–66 µs, noise). Allocation: p50 136 B/frame in both builds; gen0 counts 0 in both; the 1–2 MB single-frame spikes occur in both modes.
- Per object (parity harness, bank): mobile 3.9 µs capture vs 0.6 + 0.8 µs sample+extract (cold, few mobiles); crowd 1.84 vs 0.24 + 0.38 µs; art item 0.16–0.18 vs 0.05 µs.
- **Not done:** corpses (CorpseView-like layer walk with its own hue rules and `CorpseManager` checks) and item multis still use the capture, so the capture hook can't be retired yet. W2 not started. NativeAOT not re-measured. The Tier 0 login mitigation C was not applied (no matching commit on `poc/gpu-pagefault`); no page faults in 25 runs.
- **Test-character cleanup:** the cases/mixed scripts kill and resurrect the character, which leaves a death robe worn and one in the backpack, and a cloak in the backpack. I removed them with the new driver commands (`equip`, `items`, `target item <serial>`); kbatman is back to hair, backpack and its earlier items. Every spawn was wiped (`mobiles=1` at the end of each script).

## W3 groundwork — mobile animation state in the ECS, sprite extract, capture parity (2026-10-25)

Worktree branch `worktree-agent-a30778aae8d7c73a8` (rebased on dd178a524). Not yet the renderer's source; the capture still feeds `DynamicBuffer`. Test procedure: [testing/ecs-animation.md](testing/ecs-animation.md).

- **Components** (`src/ClassicUO.Ecs/Components.cs`, all registered for NativeAOT):
  - `AnimationState`: the action-selection inputs (server action, war, walking/steps, mounted, flying, dead, hidden, female, gargoyle tests), the selected body/mount action, stored dir, facing, frame, mount body and offset, seat offsets, and the **persistent mirror bit**. `GetAnimDirection` leaves the mirror untouched for directions 3/7, so it is history, not a function of the direction.
  - `MobileRenderState`: override hue, alpha, step offset.
  - `EquipmentSlots`: up to 25 drawn layers in paint order (an `InlineArray`), plus the mount and the hand items. Built from PaperdollOrder; covered layers, light-only items and a dead mobile's hair are dropped; drawn bodies after the gargoyle remap and equipconv. Rebuilt only when the `EquipmentDirty` tag is set. `WorldMirror` sets it on a new mobile, an equip/unequip, any write to a worn item and a body change; `MobileAnimationSystem` sets it on a dead/female change.
- **Systems** (`src/ClassicUO.Client/Game/Ecs`):
  - `MobileAnimationSystem` samples the Mobile (steps, server action, frame, selection/target context; until W5), rebuilds dirty slots from the mirror's `EquippedOn` index, then selects body/mount/per-layer gargoyle actions, seat and direction as `MobileView.Draw` does.
  - `MobileSpriteExtract` writes `SpriteInstance`s straight from the pack's animation tables: shadows, mount, body, equipment with the facing-dependent cloak move, strips, trim, mirror, hue mode, depth key, Indexed8 palette.
- **Rules:**
  - `Mobile.GetGroupForAnimation` (about 1,300 lines of reverse-engineered tables) moved verbatim into `AnimationGroupRules.Select(in AnimGroupInput, …)`. Mobile delegates to it, so the legacy path and the ECS share one rule.
  - The frame step of `Mobile.ProcessAnimation` is the shared pure `AnimationTiming.Advance`.
  - `AnimationRules` ports the rest of MobileView: direction map, cloak order, `IsCovered`, gargoyle equipment fix, mount body/offset, layer hue, strips, override-hue precedence.
- **Parity harness** `EcsParity` (`CUO_ECS_PARITY=N`): per captured mobile, runs the ECS path with the same screen position/depth/offset and diffs all 32 bytes of every instance, in order. It logs decoded mismatches with the ECS state, and a summary with costs and per-case counters. Hook: about 12 lines in `GpuWorldRenderer.DrawObject`/`BeginFrame`.
  - A deliberately broken extract (strip depth) was flagged on every mobile.
  - **`-ecscheck`** now also compares `EquipmentSlots` with MobileView's layer walk (order, serials, mount, hands) and the action/frame with the Mobile.
- **Results:** 0 mismatches in all scenarios, under JIT and NativeAOT:
  - bank: 44k mobile comparisons;
  - the cases script (cloak at all facings, spectral, gargoyle 666/667, flying, sitting, dead): 61k;
  - mixed spawns (mounted savage riders, brigands, animals, monsters, hidden, hue changes, re-equip): 486k;
  - a 120-NPC crowd: 6.8M comparisons, 46M instances.
  - **Two divergences found and fixed** (both about the mirror bit's history):
    1. a death re-key (serial | 0x80000000) recreated the entity and lost the bit; `OnSerialChanged` now carries it over;
    2. `-ecscheck` ran selection for mobiles on frames the view didn't draw them; the check now restores the bit. In production the selection must run only for drawn mobiles (W2's visible set), as MobileView does.
  - **Intentional residuals:**
    - bodies whose selection calls `RandomHelper` (LowGroupExtended, server action 5/6) roll separately on each side (not seen);
    - the seated deformation isn't in either path (the capture draws it plain);
    - equipped multis use the item graphic for tiledata.
- **Cost** (JIT, harness timings): crowd (~103 mobiles in view) capture 70 µs/frame vs ECS sample+select 10.7 µs plus extract 15.6 µs; per mobile 1.5 vs 0.23 + 0.33 µs. The bank: 8–11 µs vs 1.7 + 1.9 µs.
  - **SIMD:** not attempted. At 0.33 µs per mobile (about 7 instances each), the instance stores are a small part; the cost is frame lookups and branches. Revisit with a 1,000+ mobile measurement.
- **Account:** `kbatman3` has Owner access, verified: `[go`, `[add`, `[set`, `[wipe` all worked. The character Kbatthree was created with Uo.Net.Cli. Spawns were made at Green Acres and wiped by every script.
- **Tests:** 222 pass (31 new in `Ecs/AnimationRulesTests.cs`: direction map and mirror persistence, cloak order vs PaperdollOrder, the arms table, mount offsets/bodies, strips, layer and override hue, covered layers, timing, dirty marking).
- **Files outside the ECS folders:**
  - GpuWorldRenderer.cs (harness hook, +13 lines);
  - Mobile.cs (`ProcessAnimation` uses `AnimationTiming`);
  - MobileAnimation.cs (group selection moved out, `GetAnimGroupInput` added).
  - None of GameSceneDrawingSorting.cs, GameSceneGpuWorld.cs, Batcher2D.cs, RenderLists.cs, the UI controls, GameController.cs or Uo.Render.
## W0 and W1: world prepare measured, baked tiles no longer walked (2026-10-24)

Branch `worktree-agent-af12d303f4d9e40e7`, on top of 43dec9ae4: W0 78021a764, W1 b63b5b1c6, plus docs. JIT measurements on the desktop with other agents' clients running. Details are in classicuo-perf-baseline.md ("W0", "W1"); the method is in testing/world-prepare.md.

- **W0:**
  - FrameStats splits world prepare into setup, tile walk, Gpu hidden collect, foliage, text and per-type capture, with per-frame counts.
  - `CUO_FRAMESTATS_PREP=1` (or `prepdetail:on`) adds exclusive walk buckets and a bare list walk.
  - **Finding:** at 4K zoomed out the walk is 76% and the hidden collect 17%. Visiting the 140k objects alone costs 2.2–2.4 ms, so the fix is to stop visiting baked tiles, not to make each visit cheaper. Nothing in it is SIMD-friendly.
- **W1** (`gpu_prepare_skip_baked`, default on; `-gpuskip`; DevBench `gpuskip:`):
  - **Per-chunk tile masks.** Tiles are walked only if they hold non-baked objects (skipping their baked ones), or baked objects with per-frame duties (translucent, light, unbaked season graphic, hidden land, above the walk's max Z, overhead text); those tiles are walked in full.
  - **Targeted picking:** the tiles under the mouse are walked in full.
  - **Screen edges:** a replay of the walk's culling hides exactly what it would.
  - **Fades:** a full walk runs for 12 alpha steps after max-Z, roof, season or fading changes and for chunks that are built or come back into view. Then baked alphas snap to their settled values.
  - **Season statics** are baked by Uo.Render (`SetStaticRemap`, keyed by the original graphic).
- **Result** (4K zoomed out): world prepare 4.86 → 0.73 ms, 179 → 631 fps. 1080p zoomed out: 1.14 → 0.23 ms, 627 → 1086 fps. Britain: 0.226 → 0.079 ms.
- **Parity:**
  - Screenshots with skipping off and on are identical apart from moving creatures and rain, at Britain, the bank (roof), the bank wall with the circle of transparency, Yew (foliage), and zoomed out at Britain and the bank. I looked at every differing region.
  - Base build vs W1 differs only in moving creatures.
  - `CUO_GPU_SKIP_CHECK=1`: 0 mismatches.
  - `CUO_BENCH_PICK`: the same objects in Legacy and Gpu.
  - `pickscan:8x6`: 144 points, 0 different.
- **Bug found and fixed during testing:** turning skipping off left skipped objects with stale screen positions, which culled land at the bottom of the view. Leaving skip mode now forces a position refresh.
- **Behaviour difference (minor):** a baked static in a visible chunk that never came within the walk's vertical range during its chunk's fade period appears at full alpha. Legacy would fade it in at the top or bottom edge as you walk.
- **Environment:**
  - The NVIDIA driver logged event 153 (GPU reset) five times in an hour (22:19, 22:34, 22:39, 22:40, 23:18 local). Two of them hit my clients: one 4 s after login, one in a 3840×2054 zoomed-out window. Both logged `VK_ERROR_DEVICE_LOST` and the client died.
  - One startup also failed with FNA's "scissor rectangle is invalid" exception in `ResizePic` on the login gump, right after a reset.
  - Two sessions were disconnected by another login on kbatman.
- **Still open:**
  - translucent statics as a baked alpha flag;
  - static lights cached per chunk;
  - foliage fading near the player only, and animated statics (trees and foliage are unmeshed, so still walked);
  - the out-of-range tint as a shader uniform;
  - the gradient circle-of-transparency mode;
  - a shader-side cull for the edge band;
  - the owner's AOT FPS test.

## Pack-loading hitches and waste (2026-10-24)

Branch `worktree-agent-a968cd66550e734a5`, merged into poc/modern-client.

Items from perf-opportunities.md "ClassicUO pack-loading path" and two "Uo.Render / slice" items. Method: [testing/pack-loading.md](testing/pack-loading.md). JIT Release, Gpu world renderer, 1920×1080, VSync off; other agents' clients were running, so frame and Present times are noisy.

**Changes.**
- **PackPages** reuses evicted 1 MiB chunk buffers and rents the compressed one; chunks decode outside the lock (thread-safe, parallel). Adds `IsResident` and `Touch`.
- **Picker masks and real-art bounds from the trim rect:** `PixelPicker.SetTrimmed` (Vector256/128 zero test, scalar fallback) and `PixelPicker.VisibleBounds`. Art decodes into a pooled buffer. The untrimmed RGBA image remains because the legacy atlas uploads it.
- **Animations:** until a body has a direction uploaded, an uncached direction decodes on the thread pool. Its frames carry size and centre but no texture, so callers draw nothing (as for an empty frame) and animation timing, frame counts and `GetAnimationDimensions` are unchanged. Uploads are budgeted at 2 ms per 8 ms. A body with something uploaded loads synchronously, so a visible mobile never blinks on a turn or a new action; the worker prefetches the action's other directions and actions 0–4. `AnimationExists` answers from the tables. `CUO_ANIM_SYNC=1` restores the old path. Equipment counts as a body: a new item on a visible human can appear a few milliseconds (a frame or two) after the body. Nobody has checked this by eye yet.
- **Palette cache** is an LRU (`LruCache`) instead of clearing all 8,192.
- **Regions:** `RegionFile.DecompressInto` decodes into a reusable buffer; MapLoader recycles evicted region buffers. `MapLoader.GetLandZ` caches the last block for `Map.GetTileZ`.
- **Uo.Render:** `AtlasPage.ReadHeader` (behind `PageFormats.ReadHeader`) decodes only the 16 PageHeader bytes; the Kind 2 page job passes its already-read ChunkedHeader to the decoder.
- **Bench:** FrameStats reports frame max, hitches over 8/16 ms, the same without Present, and Update+Draw only. DevBench adds `bodies`, `ltravel`, `glide` and `CUO_BENCH_NOSHOT=1`.

**Micro benchmarks** (second repetition):

| Operation | Before | After |
|---|---|---|
| Chunk decode (1 MiB, Kind 2) | 3.2–3.4 ms, 1.27 MB | 2.6–2.8 ms, 55–65 KB |
| Picker mask per sprite (6,000 preloaded) | 4.7 µs | 3.2 µs SIMD (4.7 µs scalar trimmed) |
| Real-art bounds per sprite | 4.8 µs | 0.4–1.0 µs |
| First sight of a direction, main thread | 3.4–4.0 ms p50, 11–14 ms max, 1.56 MB | 0.2 µs (worker does the 3.4–4 ms), 0.41 MB |
| Region load (60, 24-region LRU) | 4.0–4.2 ms, 3.9 MB | 3.0–3.3 ms, 1.1 MB average (0 with a warm pool) |
| Land z lookup (stretch) | 110–180 ns | 12 ns |
| Page header read | 2.8–4.6 ms | 0.36–0.39 ms |

**Travel and first-sight test**, Update+Draw only (the client's own work), summed over four 5 s windows per entry; two base and two new runs alternated:

| Entry | Base max (ms) | Base >8 / >16 ms | New max (ms) | New >8 / >16 ms |
|---|---|---|---|---|
| `travel` (`[go`, 7 towns) | 42.9–43.4 | 9–10 / 3–4 | 35.6–42.3 | 7–9 / 3–4 |
| `ltravel` (client-only teleports) | 5.1–6.2 | 0 / 0 | 4.4–5.2 | 0 / 0 |
| `glide` (25 tiles/s) | 11.5–18.6 | 1–15 / 0–5 | 10.7–11.6 | 1 / 0 |
| `bodies` (390 new bodies) | 24.4–25.3 | 43–76 / 4–6 | 4.3–8.2 | 0–1 / 0 |

- **Whole-frame numbers** (Present included) swing with the GPU load from other clients. Bodies frame max went from 24.5–25.4 to 32.9–48.6 ms, all in Present; p99 4.4–11.7 ms in both builds.
- **Bodies allocation** fell from 57–122 KB to 19 KB per frame, the baseline.
- **Teleports still cost one 25–40 ms frame:** about 75 `Chunk.Load` calls (5–14 ms), a region decode (3–9 ms) and the server's packet burst, in the same frame. A diagnostic build confirmed this split. Next steps are listed in perf-opportunities.md.
- **kbatman4 has staff access** (`[go` worked).
- **Checks:** client Release build; 199 unit tests (11 new pack-path parity tests); `CUO_BENCH_PICK=1` gave the same selections and pixel-pick results before and after in both renderers; Uo.Render.App `--external-test` on Vulkan passes.

## AOT vs JIT gap: static PGO and OptimizationPreference=Speed measured (2026-10-25)

Setup:
- Branch `worktree-agent-ae5b80ed17424bf2c`. Measured on base 2bddaf8dc, before the B1–B5/W1 merges; the commit is rebased onto fe5222a4d.
- 1920×1080, Gpu world, account kbatman4 (staff granted mid-session; verified by `[go` teleporting).
- Plan: Britain 1495,1629 z1, bank 1430,1690 z1, zoomed out z2.5; 3×5-s windows per spot.
- Runs: 4 interleaved rounds per variant, with other agents' clients running, so noisy. Medians of window p50 (ms) from the last, quietest set:

| spot | variant | draw | ui_draw | composite | world_prepare | batcher_fna |
|---|---|---|---|---|---|---|
| Britain | JIT | 0.395 | 0.076 | 0.050 | 0.171 | 0.081 |
| Britain | AOT | 0.431 | 0.095 | 0.060 | 0.174 | 0.099 |
| Britain | AOT+PGO | 0.426 | 0.091 | 0.059 | 0.172 | 0.096 |
| bank | JIT | 0.425 | 0.079 | 0.051 | 0.182 | 0.089 |
| bank | AOT | 0.412 | 0.091 | 0.060 | 0.151 | 0.101 |
| zoomed out | JIT | 1.280 | 0.088 | 0.051 | 0.986 | 0.088 |
| zoomed out | AOT | 1.236 | 0.105 | 0.061 | 0.935 | 0.106 |
| zoomed out | AOT+PGO | 1.476 | 0.114 | 0.062 | 1.131 | 0.110 |

Run-to-run stdev: Draw 0.02–0.3 ms; UI and composite 0.002–0.013 ms.

- **Static PGO** (.mibc from a JIT EventPipe trace, 2,178 type-histogram and 17,907 edge-count records): no gain. Britain and bank are within noise; zoomed out it is worse but noisy. Binary +0.7%. **Not kept**; opt-in via `-p:CuoMibcPath`.
- **`OptimizationPreference=Speed`:** no gain. In an earlier, noisier set it was within noise or worse (zoomed out Draw p50 1.63 vs 1.51 ms). Binary +4%. **Not kept.**
- **Where AOT still loses** (about 0.03 ms per frame, UI draw plus composite):
  - It is native time in FNA calls: `batcher_fna` is +0.015–0.018 ms with identical draw and flush counts.
  - A no-admin IP sampler (tools/aot-bench/sampler.py) shows 4–5% of the game thread's samples in ntdll `RtlpWalkFrameChain` / `RtlpLookupFunctionEntryForStackWalks` / `RtlpUnwindPrologue` under driver frames, against about 2% in JIT.
  - Suspected cause: stack captures by an injected GPU tool (nv-aftermath-monitor is running and `InProcessClient64.dll` is loaded). They would cost AOT more because they unwind through all AOT frames.
  - Managed hot spots are the same in both builds (AddTileToRenderList, FillGameObjectList, CollectGpuHidden, batcher SetVertex).
- **Startup** (to the first window): JIT 2.0 s, AOT 1.6 s, PGO 1.5 s, Speed 1.5 s.
- **Trim warnings:** no new ones (only the known FNA, cuo `Enum.GetValues` and Friflo warnings).
- **Next:**
  - re-measure on a machine with no GPU tools injected;
  - try `<DirectPInvoke>` for FNA3D and SDL3 with generated import libraries;
  - re-check on the current base, which now has the B1–B5 UI changes and W1.
- **Procedure:** testing/aot-performance.md. Tools: tools/aot-bench, tools/pgo/collect_mibc.py.
## World settle: hold the last frame on entry, teleports and recreation; Legacy is developer-only (2026-10-25)

Branch `worktree-agent-ae4318b953711686b`. Fixes the known issue "Gpu world renderer: raw pop-in on world entry and teleports". Test procedure: [testing/world-settle.md](testing/world-settle.md).

**Changes.**
- **`WorldSettleGate`** (GameScene, pure logic, 11 unit tests) detects a discontinuity:
  - the first world frame;
  - the player moving more than 8 tiles in one frame;
  - a facet change;
  - a new world-pass instance;
  - the first Gpu frame after Legacy frames.

  The gate then holds until `WorldRenderer.VisibleReady` or 1.5 s, whichever comes first. While holding:
  - the world and light targets keep the last presented frame;
  - the GPU world only streams: `WorldRenderer.Stream()` runs the copy pass and draws nothing;
  - GameScene's fill, picking and captures run as usual, and FNA's world-space draws go to the backbuffer, which the composite clears.

  If there is no frame to hold (login, or a recreated target), the world area is black and then fades in over 250 ms. Walking never holds.
- **Uo.Render:** `VisibleReady` is true when no unready chunk lies near the view (`ChunkStore.MissingNear`, the land footprint widened by the streaming's art margins) and no visible page or anim page is missing. It ignores the prefetch ring, unlike `Settled`.
- **No Legacy fallback.**
  - GameScene draws Legacy only when it is selected explicitly.
  - A failed GPU world setup (no SDL_GPU device, pack error, facet pass error) logs the cause, saves and exits with `0xDEAF` (`GpuDeviceLost.FailWorldSetup`), the same way device loss exits. No Legacy frame is drawn first.
- **Legacy is a developer comparison mode only.**
  - `world_renderer` is now runtime-only, set by `-worldrenderer`, the `-worldrenderer` command or DevBench `world:legacy`.
  - A saved `"world_renderer"` in settings.json is ignored with a warning and dropped on the next save.
  - In game the world target is always `PreserveContents`, so a switch back from Legacy holds Legacy's last frame.
- **DevBench additions:**
  - `reinit` / `reinitrt` plan modes;
  - `CUO_BENCH_TPSHOTS`;
  - `[settle]` counters per plan entry;
  - `CUO_WORLD_HOLD=0` (observe only, the baseline);
  - `CUO_WORLD_SETTLE_LOG=1`.
- **Teleport prefetch on the server packet:** not done. ClassicUO applies the 0x20/0x21 position in Update and draws the same frame, so there's no gap to prefetch in. The hold itself is what streams the destination before showing it.

**Numbers.** Hold off is the baseline; it presents every frame, like the base build. 1280×720 at 150% DPI, uncapped fps, other agents' clients on the GPU.

| Run | Hold off | Hold on |
|---|---|---|
| Login | 0 held, 7–10 unsettled frames presented, ready after 578–596 ms | 9–14 black frames held (460–615 ms), then fade-in; 0 unsettled presented |
| `ltravel`, per teleport (6 per run, 2 runs each) | 6–12 unsettled frames presented, ready after 16–51 ms | 4–16 frames held (15–60 ms), 0 unsettled presented |
| `reinit`, per recreation (5 per run) | 13–19 unsettled presented, ready after 52–65 ms | 19–39 frames held (50–65 ms), 0 unsettled |
| `reinitrt` (pass and RT dropped) | 14–18 unsettled presented | 15–29 frames held (black), 0 unsettled, then fade-in |
| `world:legacy` → `world:gpu` | Resume ready at once, 0 unsettled | 1 frame held, 0 unsettled |
| Unsettled frames while walking (both runs) | 5–20 | 8–13 |
| Legacy frames outside `world:legacy` | 0 | 0 |
| Timeouts | — | 0 in all clean runs; 1 in a `CUO_BENCH_TPSHOTS` run, where frames were 150 ms each |

- **At `-fps 60`, hold on:** a teleport holds 3–5 frames (46–82 ms), a recreation 8 frames (about 133 ms); 0 unsettled frames presented.
- **The walking unsettled frames** are single art pages first touched by a dynamic sprite, missing for about 10 ms at ~1000 fps. Walking doesn't hold, so they are equal with and without hold.

**FrameStats** (walk and ltravel, 5 s windows, alternating runs, noisy):

| | Hold off | Hold on |
|---|---|---|
| Walk fps | 692–1495 | 673–1568 |
| Walk frame p50 | 0.37–0.49 ms | 0.42–0.54 ms |
| Walk world_prepare avg | 0.049–0.071 ms | 0.052–0.073 ms |
| Walk alloc per frame | 1.2–3.0 KB | 0.8–2.8 KB |
| ltravel world_prepare avg | 0.047–0.071 ms | 0.043–0.074 ms |

No regression beyond the run-to-run noise. The gate costs one call and a few compares per frame.

**Visual evidence** (`CUO_BENCH_TPSHOTS`, world target per frame; scratchpad `settle\evidence\`):
- `holdoff_teleport_f04.png`: 4 frames after a teleport, black apart from the player.
- `holdoff_reinit_f04.png`: after a recreation, the dynamic items only, on black.
- `holdon_teleport_f04_held.png`: the previous town, byte-identical across the held frames.
- `holdon_teleport_f05_released.png`: the complete new town, no gaps.
- `holdon_reinitrt_fadein.png`: the black fade-in after the RT drop.
- ffmpeg gdigrab of the window didn't work on this machine (150% DPI and overlapping windows), so the evidence is in-client shots.

**Checks.**
- Client Release build: 0 errors.
- 244 unit tests: 233 before, plus 11 for `WorldSettleGateTests`.
- A saved `"world_renderer": "legacy"` is ignored with a warning; 0 Legacy frames.
- `FNA3D_FORCE_DRIVER=D3D11` exits with `0xDEAF`.

**Not verified:**
- a real facet change (no local facet hop; unit-tested, and `reinit` exercises the same recreation path);
- `CUO_BENCH_PICK` and Legacy parity shots: Legacy currently has an unrelated corruption regression being bisected by another agent;
- the page-fault branch's `CUO_GPU_REINIT`: it isn't on this branch. It drops the RT, so it should behave like `reinitrt`.
## Teleport frame: region prefetch and incremental chunk loads (2026-10-25)

Branch `worktree-agent-acd66bd79c6a500ba`, from poc/modern-client 7f25fa29d. Follows "Pack-loading hitches and waste": each teleport still cost one 25–40 ms frame. Method: [testing/pack-loading.md](testing/pack-loading.md) §3 and §4.

**Changes.**
- **MapLoader decodes regions on the thread pool** (`PrefetchRegion`, `PrefetchAround`, `IsBlockReady`, `TryGetLandZ`).
  - A lookup adopts a finished prefetch. If the prefetch is still decoding, the lookup waits for the rest of it instead of decoding again.
  - The cache and its pooled buffers are only changed under `_sync`, by lookups.
  - Prefetch triggers: the 0x1B and 0x20 handlers, and every frame for the regions within 104 tiles of the player (glide and walk).
- **Chunks load incrementally** (`Map.UpdateLoading`, called from `World.Update`; `ChunkLoadQueue`):
  - Order: nearest the player first. The player's own chunk loads as soon as its regions are decoded.
  - Budget: 2 ms per frame (`CUO_CHUNK_BUDGET_MS`), covering the load and the mesh build, with at least one chunk per frame. Nothing waits for a decode.
  - The draw walk uses `GetChunkForDraw`, a one-line hook in GameScene.cs.
  - `GameObject.AddToTile` parks objects whose chunk is not loaded (`Map.Park`). They join their tile when the chunk loads.
- **Rule for game logic:** `GetChunk`/`GetTile` with load = true, `GetMapZ` and `GetTileZ` still load synchronously. `Pathfinder` now loads chunks within the view range on demand; before, the draw walk had already loaded everything on screen.
- `CUO_CHUNK_SYNC=1` restores the old path.
- **FrameStats** has map-loading counters. **DevBench** has the `bwalk` entry, and `LocalTeleport` no longer waits for a decode.

**Travel and first-sight test**, Update+Draw only, summed over four 5 s windows per entry, base and new alternated (baseA, newA, baseB, newB).
- Both builds carry the uncommitted Tier 0 login mitigations A and C.
- Other clients on the GPU: none for baseA, newA and newB; one for baseB.
- Allocation is per frame, averaged over the entry's windows.

| Entry | Base max (ms) | Base >8 / >16 | Base alloc | New max (ms) | New >8 / >16 | New alloc |
|---|---|---|---|---|---|---|
| `travel` (`[go`, 7 towns) | 36.5–39.6 | 8–31 / 4–14 | 8.6–11.3 KB | 10.4–13.7 | 3 / 0 | 6.1–6.4 KB |
| `ltravel` (client-only teleports) | 7.9–42.3 | 0–22 / 0–10 | 2.1–3.4 KB | 5.5–13.0 | 0–1 / 0 | 2.3–2.7 KB |
| `glide` (25 tiles/s) | 11.0–11.8 | 1 / 0 | 1.7–2.6 KB | 10.3–10.5 | 1 / 0 | 1.9–2.2 KB |
| `bodies` (390 new bodies) | 3.9–29.9 | 0–9 / 0–7 | 2.6–2.9 KB | 5.0–5.5 | 0 / 0 | 3.6–4.4 KB |

- **Base teleport frames:** the worst is `world_prepare` 24–25 ms, which is chunk loads plus mesh builds in the draw walk.
- **New teleport frames:**
  - Chunk work is spread over frames: up to 81 chunks queued, `region_wait_us` 0 and no region decoded on the main thread.
  - The worst frames are the server's packet burst (`packets` 8.0 ms) and UI or GPU noise (`ui_draw` 13 ms in one window).
  - Map-loading frames peak at 8.3 ms. One chunk with many first-sight statics can take 6–10 ms to mesh, and the at-least-one rule lets it through. That is also glide's 10.3 ms frame, about the same as base.
- **Target not fully met.** Teleport frames are under 8 ms Update+Draw except for those two causes. perf-opportunities.md lists the next step: worker art decode before the mesh build.
- **Spikes seen in single windows of both builds:**
  - base `ltravel` 42 ms (`composite`) and base `bodies` 30 ms (`prep_walk`);
  - new `ltravel` 13 ms (`prep_cap_mobile`);
  - earlier new runs, without mitigations: `bodies` 28–34 ms (`update_other`).
  None of them involve chunk or region loading.
- Base allocation in `bodies` is 2.6–2.9 KB per frame against 3.6–4.4 KB for new, inside the run-to-run noise of earlier runs (1.6–8 KB). The region buffer pool and pooled chunk buffers are unchanged.

**Checks.**
- Client Release build: 0 errors.
- Unit tests: 240 pass, including 7 new in `ChunkLoadingTests`: queue order, budget, stale and sticky requests, no allocation, prefetch parity, land z across 40 regions.
- `bwalk` after a real teleport crossed the y 1536 region edge and the 40 → 30 slope with 0 server walk denials, and the client z equalled the land z at each leg end.
- The world screenshot after the walk matches `CUO_CHUNK_SYNC=1`.
- **Not run:** `CUO_BENCH_PICK=1`. The chunk lookup that picking uses is unchanged, but nobody has checked picking.
- **GPU faults:** 3 of the first 8 runs (without mitigations) hit the Tier 0 login page fault, one of them on the base build. None in the 6 runs with mitigations.


### 2026-10-25 — Legacy tile corruption: B1 batcher regression, fixed

- **Symptom:** the owner saw garbled, flickering world tiles on several clients: whole diamonds of land and statics missing, and square land patches. Mobiles, dynamic items and the UI were fine.
- **It was Legacy frames,** not the Gpu world pass:
  - world-RT shots: every Gpu frame was clean, and every Legacy frame was corrupt;
  - the Legacy frames came from the plans' `world:legacy` periods and, before the world-settle merge, the automatic Legacy fallback while the Gpu pass was being (re)created.
- **Bisect:** pure Legacy at the house spot, % near-black pixels of the world target. 3fc29d9d5 and 2eedf61db were clean (2%). B1 2202efb1f, and every later build, were corrupt (25–67%).
- **Cause:** B1 stopped applying the basic effect per mesh run. That apply was what made FNA3D rebind the pipeline and with it the index buffer. FNA3D doesn't rebind a cycled index buffer (known-issues), so each mesh layer drew with stale indices.
- **Fix** (`poc/b1-index-rebind`, on a6110f967): `IndexDataChanged()` after each layer's index upload.
  - Shots: 12 of 12 pure-Legacy and 39 of 39 flip-plan shots clean.
  - Effect applies: Gpu 2.0–2.7 per frame, unchanged. Legacy 7 → 56–63 per frame; batcher_fna 0.20–0.23 → 0.22–0.23 ms, within noise.
  - Unit tests: 244 pass.
  - Regression check: `testing/ui-batcher.md`.

### 2026-10-25 — Tier 0: the GPU page fault is fixed with an SDL fork patch

- **Cause:** every own `Error_DMA_PageFault` (4 in 90 baseline logins) followed SDL's Vulkan defragmenter copying Uo.Render's 256 MiB art atlas during FNA's swapchain submit. The atlas shares a 320 MiB allocation with FNA's render targets, and their churn gets it marked for defrag.
- **Fix:** SDL3 now comes from our fork, github.com/modernuo/SDL `modernuo/3.4.19` ([sdl-fork.md](sdl-fork.md)).
  - The patch gives images of 64 MiB or more a dedicated allocation, which is never defragmented.
  - It's the `external/SDL` submodule; the built DLL is committed, and `tools/sdl/build-sdl.ps1` rebuilds it.
  - The client logs `SDL3 SDL-3.4.19-b2a3160 (modernuo …)`.
- **Measured** ([device-lost-leak.md](device-lost-leak.md) part B):
  - dedicated: 0 atlas moves in 24 runs, against about 0.3 per run with stock SDL, while the render targets kept defragmenting;
  - shipped DLL on the merged tip: 220 runs on three merged tips (the last one 92dfb1fc3, poc/modern-client 3216539a7), 0 own faults, 0 atlas moves on the watched 100. Baseline was 4 in 90; the chance of 0 in 220 at that rate is about 0.005%.
  - Reference-guarded defrag blocked all defrag, so it told the hypotheses nothing.
- **App side:** A (session-wide world-RT usage) arrived with the settle merge. B (atlas tail filler) and C (world-pass creation after present) are dropped. They, the `GpuWatchTemp` watcher and `CUO_GPU_REINIT` are kept on `poc/gpu-pagefault-diag`.
- **Tools:**
  - `tools/gpu-fault-repro/repro.py`: the fault loop, with PID attribution;
  - `run-fps-test.ps1` writes the password encrypted into its temp settings.
- **Docs:** [testing/gpu-faults.md](testing/gpu-faults.md) (fixed: PID attribution, foreign faults, what a fault looks like, the Debug-build limitation, NEWTEX semantics).
- **Follow-ups (known-issues):**
  - FNA3D's `nextRenderPass*` dangling-reference hazard;
  - FNA3D's cycled-index-buffer rebind;
  - the owner's FPS test on the new DLL.

### 2026-10-25 — Land seams when zoomed out, and the scissor exception (seams agent)

Branch `worktree-agent-a2ed70cbca488756a`, started at d583f65f5 and merged up to 67141bf6e. Final numbers and shots were measured on that base. Fixes the known issues "grid lines between land tiles when zoomed out" and "scissor rectangle is invalid". Evidence is in the session scratchpad, `seams\evidence\`.

**Land seams** (115e93c71, sprite.hlsl only)
- **What the shots showed** (1280×720 world target, Britain 1495,1629, zoom 1.5, 2 and 2.5). There were dark diamond lines on dirt, grass, the static river water and stone floors. The river is static water tiles, not land, so statics had to be included.
- **Causes:**
  - The 0.5 alpha test ran on trilinear-filtered alpha. On a diamond edge, both neighbours could discard the pixel (a gap), or blend in the transparent black texels outside the diamond.
  - For stretched land, the mip footprint reached past the 2-texel gutter into neighbouring sprites.
- **Fix (`SampleTile`).** It applies only when zoomed out (`Sharp` off), to `FLAG_ZTEST` instances that are stretched or whose sprite fills 44×44: land, which is always an untrimmed 44×44 in the pack, plus floor and water statics.
  - **Coverage of flat tiles** comes from the mip0 texel under the fragment, Legacy's point-sampled diamond, which tiles exactly. A 1/64-texel bias makes pixel centres that land exactly on texel edges (scale 0.5) pick the same side in every tile. Without the bias, zoom 2 drew new lines along the shore.
  - **Colour stays trilinear** with the original gradients, so it uses the same mip levels as before: no new shimmer or blur. The sample position is clamped into the sprite's rect, inset by 1.5 s − 2 texels for the coarsest mip used.
  - **Flat tiles un-premultiply:** B5G5R5A1 texels are either opaque or 0, so filtered colour divided by filtered alpha is the average of the tile's own taps.
  - Zoom ≥ 1 (sharp path) is untouched. There are no pack or atlas changes; a gutter or pack change isn't needed.
- **Candidates rejected:**
  - Point-sampling land at zoom < 1: Legacy's look, but it aliases and shimmers.
  - Clamping colour into the diamond by an L1 inset: it needs about 2 s texels, and smears a 2–3 px band along every edge at zoom 2.5.
  - Alpha-aware mips or atlas gutters: the converter's mips are already alpha-aware, and they don't fix the alpha-test gaps.
- **Shots** (left to right: Legacy, Gpu before, Gpu after):
  - `z25_river_*`, `z25_dirt_grass_*`, `z25_floor_*`, `z20_shore_*`, `z15_dirt_grass_*`: the grid is gone, and the after shots match Legacy apart from the trilinear softness.
  - `z25_diff_before_after_x8.png`: the only differences are tile edges.
- **Left over:** a few short dark ticks where shore statics meet open water at zoom 2. They were there before the fix; those statics don't fill 44×44 and keep the generic path.

**Scissor exception** (b59735443)
- **Cause:** FNA only rejects a scissor rect with a negative x, y, width or height. `ScissorStack` intersected pushed rects with their parent but left the root rect unclipped, so a control partly off the top or left edge threw.
- **Fix:**
  - `ScissorStack.ClipToBounds` clips every rect to the bound render target, or to the back buffer when none is bound. An empty result isn't pushed.
  - `GameController.Draw` skips the scene UI, gumps and cursor when there is no UI target or the back buffer is 0 wide or high.
  - 11 unit tests (`ScissorStackTests`).
- **Not reproduced** with the old build: a 200×150 saved `window_size`, and 4 minimise, restore and 120×90 `SetWindowPos` cycles on the login screen. The only other code that sets FNA's `ScissorRectangle` is the plugin draw path (`Plugin.cs`).

**Tooling** (dda406a89): `Uo.Render.App --bench-fixed --at x y scale` holds one spot for GPU timing.

**Checks** (base 67141bf6e)
- Client Release build: 0 errors. 255 unit tests: 244 before, plus 11 new.
- **UI parity** (`gumps` and `_ui.png`): identical apart from the journal and world-save text.
- **Gpu world, Britain and bank at zoom 1:** identical apart from moving NPCs.
- **Legacy world** (Britain at zoom 1, and zoom 2.5): identical apart from the moving player and NPCs.
- **Uo.Render.App `--external-test`:** PASS on Vulkan and D3D12.
- **Performance** (2 or 3 other clients on the GPU the whole time, so very noisy):
  - client FrameStats, alternating runs: world_draw p50 at zoom 2.5 was 0.105–0.123 ms before and 0.103–0.115 ms after. The CPU side is unchanged by construction.
  - GPU frame time, `--bench-fixed` at 3840×2160 and scale 0.4, 5 alternating rounds: best medians 1.07 ms before and 1.11 ms after (+0.04 ms, about 3.5%, at 8.3 Mpx). Single runs varied from 1.0 to 6 ms, so this is within noise. An idle GPU wasn't available.

## W3 + teleport integration gate; corpses and multis from the ECS; settle follow-up (2026-10-25)

Branch `worktree-agent-add0b9e9bfc1fdafb`, started on poc/modern-client 3216539a7 (= the merged tip 28bb72308 + docs), merged with 08b952f3a (Tier 0 SDL fix) before the final measurements. JIT Release, 1920×1080. Scripts and logs: session scratchpad `integ\`.

**Phase 1, the merged tip (3216539a7): pass.** Parked objects (Map.Park) are off every tile, so neither the capture nor the extracts see them; their mirror entities are synced when parked and again when placed.

| Check | Result |
|---|---|
| ECS parity, `CUO_ANIM_SYNC=1`: bank / items / 120-NPC crowd | 0 mismatches (61k / 68k / 5.9M mobile and 374k / 265k / 798k item comparisons) |
| Parity during `travel`, `ltravel`, `travel` (60 teleports with holds, `-ecscheck 60`) | 0 mismatches (137k mobile, 2.15M item); 1,766 objects parked, 1,759 placed, 0 placed at a wrong tile or with a stale ECS position (diag build) |
| The same plan's `bodies` entry | 12,676 false mismatches: DevBench swapped the body without syncing the mirror. Fixed (af894ce94); 0 after |
| Teleport settle, 2 normal runs | 0 unsettled frames outside walking, 0 timeouts (25–26 episodes each) |
| Picking vs 7d0a74a92 | `CUO_BENCH_PICK` same objects in both renderers and both builds; `pickscan:8x6` 48 same, 0 different in both |
| Shots vs 7d0a74a92 (Gpu, Legacy, UI) at Britain, bank, zoom 2.5 | Gpu Britain/UI identical; bank: moving NPCs; Legacy: moving NPCs and rain streaks; UI: journal text. Looked at |
| 13-minute mixed session (travel, glide, walk, ltravel, bwalk) | 7,577 parked, 7,556 placed, 0 left parked (the rest moved or were deleted first); ECS count = World count in all 98 checks; private bytes 1,766 → 1,878 MB in the first 2 min, then 1,888–1,916 MB; 162 settle episodes, 0 timeouts |

**Phase 2.**
- **Corpses and item multis from `ItemSpriteExtract`** (83f8c7a6d): `EmitCorpse` ports `ItemView.DrawCorpse` (Corpse.def body, worn layers in PaperdollOrder order, equipconv, strips, out-of-range/dead/grid-loot/highlight hues, CorpseManager, the mirror bit written back for picking). Multis: no stack copy, no selection hue, the placement preview at half alpha. On the default path the capture now runs only for effects, captured statics/multi components and shadow-only draws; `gpu_ecs_sprites off` keeps it for everything.
  - Parity: 0 mismatches over about 900k corpse comparisons (6.4M instances) and 260k multi comparisons (new `tools/ecs-parity/corpses.txt`; corpses in the crowd from NPC fights). Sabotage (corpse strip depth) flagged 154k.
  - Found on the way: **items of a dying NPC** (re-keyed to serial | 0x80000000) were filed as contained, so the ECS dropped its hair for the first frames of the death animation (35 mismatches in 8 deaths). Fixed in the mirror (2f53f9a7f, unit test); 0 after.
- **Settle follow-up:** readiness is now `VisibleReady && Map.DrawPending == 0` (b7b2d739c), and object fades complete at once while the gate holds (1f70b5905). The slow shot run (`CUO_WORLD_HOLD_TIMEOUT=30000`) showed the hold releasing with 10–36 client chunks queued and buildings still translucent for several frames; with both changes every release has 0 queued chunks and the first frame is opaque. The fade part was the larger one: statics of a reloaded chunk start at alpha 0, queue or not.
  - Cost: teleport holds +1–2 ms each on average, login black screen about +100 ms (it now waits for the visible client chunks).

**Measurements** (base = 08b952f3a, new = the merged branch; alternated, no other clients on the GPU; µs per frame):

| Spot | world prepare base → new | world draw | Update | item capture | alloc/frame |
|---|---|---|---|---|---|
| Britain | 63.5, 65.6 → 58.1, 65.5 | 63–66 → 57–65 | 14.4–14.7 → 13.3–14.6 | 2.4 → 2.4 | 1.29–1.41 KB both |
| Bank | 74.5, 76.2 → 65.2, 75.6 | 66–69 → 56–66 | 15.3–15.5 → 13.2–15.1 | 1.6 → 1.6–1.7 | 1.17 KB both |
| Zoomed out ×2.5 | 250.8, 218.2 → 205.7, 222.0 | 80–86 → 75–81 | 14.5–16.7 → 14.0–14.6 | 3.3 → 3.4 | 1.29 KB both |
| 120-NPC crowd | 91.8, 91.9 → 87.3, 91.1 | 52–54 → 51–53 | 28.8–30.4 → 28.1–28.2 | 6.2–6.4 → 4.4–4.5 | 1.17 KB both |
| 8 corpses (one session, `ecssprites off` → `on`) | 42.4 → 34.0 | 44.5 → 43.7 | 19 | 12.5 → 7.7 | — |

No regression. The crowd's item capture fell because NPCs that kill each other leave corpses.

**Not done:** W2. The house placement preview (multi alpha 0.5) has no scenario; NativeAOT not re-measured.

## Large-scale scene; scale fixes; W2's screen-position half (2026-10-25)

Branch `worktree-agent-aa6a72ed78ad4937a`, started on poc/modern-client 507dc2995 (no merges needed; still current at the final measurements). JIT Release, 1920×1080, kbatman3, no other clients. Procedure and full tables: [testing/large-scale.md](testing/large-scale.md). Scripts and logs: session scratchpad `largescale\`.

**The scene** (`tools/large-scale/make_scene.py create|wipe`, `-ecsdriver` scripts): 1,984 frozen, blessed NPCs (mounted, equipped humans, animals, monsters, a dragon block; about 5,600 worn items) and 19,476 non-movable ground items (stacks, art, lit and animated art) on an empty field in Felucca's east strip at 5156 2150, with a walking lane (`lswalk`). Built with `[TileXYZ` (no targeting) in 95 s; wiped with `[wipe` in 30 s. Server: about 4% of one core while a client stands there. **Left in place** (frozen, cheap); the wipe script removes it.

**Found at scale and fixed:**
- **Sprites silently dropped** (6c7478749): the Gpu world's dynamic range held 32,768 instances; the scene needs about 33k zoomed out, so the north tip went missing. Now 131,072 (4 MiB), and an overflow is logged.
- **Anim page thrash** (same commit): 64 Indexed8 anim layers against an 82-page working set: pages evicted and re-uploaded every few frames, sprites missing meanwhile. Now 128 layers (+64 MiB).
- **FrameStats measured itself** (6ee6e8b09): two clock reads per captured object were about 0.33 ms per frame in the scene. The capture is now timed in runs of one object kind.
- **Overhead text positions** (59fd92aa7): every frame scanned all of World.Mobiles and World.Items (0.2 ms with 25k items); now only entities that got text (`World.TextOwners`).

**W2:** the screen-position half is done; the ECS in-view query is not, on purpose.
- **Screen position only on change** (b8fd7756f): with baked tiles skipped, every walked object's position was recomputed every frame (a skipped tile can miss camera moves). A camera generation bumped on each `UpdateDrawPosition` says which objects missed one. Walk 0.84 → 0.65 ms in the scene.
- **Item picking** (8428f0a9d): the mouse outside an item's art rectangle (from the ArtEntry offsets the view and the extract store) is rejected before the art lookup. Picking 0.29 → 0.11 ms per frame in the scene.
- **Why not the ECS query:** after these two, the walk costs about 31 ns per dynamic object (9 ns visiting, 15 ns rules, 5 ns picking). An ECS query would only replace the visit: the per-object rules (alpha and roof fades, the per-tile max-Z cut-off, surface-overhead, behind-a-tree, object handles) still read the GameObject, and the tile order decides both the max-Z cut-off and the painter order at equal depth that the Gpu path reproduces. Expected saving about 0.15 ms at 21k objects, nothing at normal scale, for a large parity risk. Item baking (W4) removes both the items' walk share and their extract (about 1.0 of the 1.9 ms world prepare in the scene).
- **`CUO_PREP_CHECK=1`** verifies both shortcuts against the skipped work: 0 wrong positions in 852M kept, 0 wrong item rejections in 487M, over walks, glides, teleports, zoom changes and pickscans.

**Hot spots in the scene** (new build, Gpu, zoom 1, 2.96 ms frame, 341 fps):

| # | Where | ms | Next step |
|---|---|---|---|
| 1 | World prepare: walk over 21k dynamic objects | 0.66 | W4 removes the items' share (about 90% of the objects) |
| 2 | World prepare: mobile animation + extract, 1.6k mobiles in view | 0.59 | W5 (the ECS samples the Mobile for its inputs; 0.36 µs per mobile) |
| 3 | World prepare: item extract, 14.6k items in view | 0.42 | W4 (bake settled ground items per chunk) |
| 4 | Update: `World.Update`'s item loop over 25k items | 0.20 of 0.32 | W5 (active lists; most items are worn or in packs) |
| 5 | World draw (GPU submit) | 0.31 | – |
| 6 | Overhead text positions (2k mobiles with incoming-name text) | 0.15 | Inherent while the text shows |

Legacy in the scene: 4.45 ms, of which world draw (the FNA batcher) 2.29 ms. Allocation per frame is flat with scene size (the same as Britain standing; 7–10 KB per frame while walking the lane, from objects leaving and entering range).

**Checks:** ECS parity in the scene with `CUO_ANIM_SYNC=1` (standing and walking the lane): 0 mismatches in 9.46M mobile and 75M item comparisons. `CUO_GPU_SKIP_CHECK`: 0. `pickscan:8x6` at the scene (zoom 1 and 2.5) and Britain: 0 different; `CUO_BENCH_PICK` base vs new: the same objects. Teleports Britain ↔ scene: 0 settle timeouts (the hold into the scene is up to 650 ms while 27k objects arrive). Shots base vs new, Gpu, Legacy and UI, at Britain, bank, zoom 2.5, the scene and its zoom-out: differences only on moving or animating creatures and the journal text; looked at. 263 unit tests pass.

**Not done:** W4, W5, NativeAOT measurements, the 120-NPC crowd and 4K re-runs (the scene covers both directions), night/lights in the scene (lit items were compared only by daylight, `lights=0`).

## W4: ground-item chunk cache (2026-10-25)

Branch `worktree-agent-abad0cde09b845af9`, started on poc/modern-client f793e1bb4 (still current at the final measurements). JIT Release, 1920×1080, kbatman3, no other clients. Scripts, logs and shots: session scratchpad `w4\`. Procedure: [testing/world-prepare.md](testing/world-prepare.md) ("The ground-item cache").

**Design** (`GameSceneGpuItemCache.cs`, setting `gpu_prepare_item_cache`, default on):
- **Per chunk** (`Chunk.GpuItems`): every tile in the walk mask whose unbaked objects are only cacheable ground items (plus mobiles) is extracted once with `ItemSpriteExtract`. Entries are in walk order with their instances in world pixels, the tile anchor (for the walk's culling), a picking box, the light and a signature.
- **Each frame,** before the chunk's walk: rebuild if dirty; serve the cached tiles minus those with an item art box under the mouse, the selected container's tile and tiles with a mobile the walk's max Z stops at. Served tiles leave the walk mask. When the tile loop reaches a served tile it checks each entry's signature (a changed item: walk the tile now, rebuild next frame), culls by the anchor, stamps `DrawnFrame` and queues a run at the statics list's current length. A served tile with mobiles walks its mobiles alone (`Chunk.GpuMobiles`, in tile-list order).
- **Capture:** `RenderLists.CaptureRenderList` appends each run where the walk would have queued its items, so the dynamic buffer's order (and equal-depth ties) is unchanged; lit entries call `AddLight` at that point.
- **Invalidation:** `GameObject.TileChunk` (set by `Chunk.AddGameObject`, cleared by `RemoveFromTile`, which now marks the chunk dirty; before, removals left W1's masks stale until something else re-added). Mobiles moving don't re-extract items. Also: any full-walk frame of the chunk, a new GPU world pass, the setting, `gpu_ecs_sprites`, the dead-gray tint; transient exclusions (text, fading in, handles) retry every 30 frames. No ECS change events: the signature catches what they would, and server item updates always re-add the item to its tile (0 tiles were ever caught by the signature in the tests).
- **Not cached:** corpses, multis, animated / foliage / translucent / roof / internal items, items with text or handles, offsets, not at full alpha, above the max Z or ground Z or too tall for the walk's max Z, tiles with any other unbaked object; nothing while object handles show or during W1's full-walk steps.
- **Checker** `CUO_GPU_ITEM_CHECK=1|every`: a check frame walks everything and compares what the cache would have served, item by item (order, instance bytes, light, the statics-list position), plus a hash of the other queued objects against the served frame before.

**Measurements** (base = f793e1bb4, new = 80a3db4f2; 2 alternated sessions × 2 windows each, ms per frame; GPU Present was noisy throughout, so frame/fps are a sanity check only):

| Spot | world prepare base → new | walk | item extract (walked items) | cache (prepare + capture) | mobile extract | objects walked | alloc/frame |
|---|---|---|---|---|---|---|---|
| Scene, zoom 1 | 1.67, 2.01 → 1.32, 1.45 | 0.52, 0.78 → 0.40, 0.44 | 0.44, 0.41 → 0.10, 0.11 | 0.04, 0.05 | 0.52–0.61 both | 21.1k → 8.8k | 0.9–5.6 KB both (arrival) |
| Scene, zoom 2.5 | 2.19, 2.53 → 1.80, 1.97 | 0.63, 0.91 → 0.51, 0.56 | 0.60, 0.56 → 0.22, 0.24 | 0.05 | 0.69–0.80 both | 21.5k → 9.0k | 0.87–0.89 KB both |
| Scene, walking the lane | 1.46, 1.78 → 1.25, 1.44 | 0.46, 0.70 → 0.39, 0.46 | 0.36, 0.35 → 0.12, 0.14 | 0.03 | 0.48–0.60 | 17k → 7.6k | 10–15 KB both |
| Scene, walking, zoom 2.5 | 1.91, 2.19 → 1.59, 1.75 | 0.58, 0.81 → 0.48, 0.53 | 0.47, 0.45 → 0.19, 0.21 | 0.03 | 0.61–0.70 | 17.4k → 7.8k | 10–13 KB both |
| Britain | 0.052, 0.054 → 0.044, 0.046 | 0.019 → 0.015 | 0.002 → 0 | 0.002 | – | 254 → 204 | 0.87 KB both |
| Bank | 0.061, 0.056 → 0.051, 0.053 | 0.020 → 0.017 | 0.001 → 0 | 0.001 | – | 260 → 222 | 0.75 KB both |
| Britain, zoom 2.5 | 0.195, 0.198 → 0.172, 0.180 | 0.077 → 0.068 | 0.003 → 0.001 | 0.005 | – | 1.17k → 1.12k | 0.34 KB both |
| Legacy scene (3 more alternated rounds) | 0.76–1.02 (avg 0.86) → 0.83–0.90 (avg 0.85) | | | | | | |
| Legacy Britain | 0.136–0.157 → 0.136–0.149 | | | | | | |

- An earlier in-session on/off series gave the same split (scene zoom 1: 1.69 → 1.20, 2.01 → 1.47 ms).
- Private bytes: peak 2.04–2.08 GB base, 2.08–2.16 GB new (within run-to-run spread; the cache holds about 12k entries and 26k instances in the scene, about 2 MB).
- In the scene 2.9k of 14.2k items stay walked at zoom 1: the tiles under the lit/animated art blocks. An animated item on top of 15 layers keeps its whole tile in the walk, to keep the statics list's order.

**Verification:**

| Check | Result |
|---|---|
| `CUO_GPU_ITEM_CHECK=every` in the scene (standing, `lswalk`, zoom 2.5, pickscans), Britain, bank, zoom 2.5, `travel`, `walk` | 0 differences in 155k checks, 110M cached-item comparisons (0 missing, extra, different or misplaced) |
| Item changes (Green Acres `[TileXYZ` blocks; `[area set hue`, `set amount` (stacks, coins), `increase x`/`y`, `delete`; `[add`; pick up and drop; walk; `[wipe`), `CUO_GPU_ITEM_CHECK=every` | Every change seen in the driver's `items` log; 380k check frames, **0 stale** (0 frames with differences). The same script with world shots cache-on vs cache-off after each step: 9 pairs pixel-identical |
| Other queued objects (mobiles walked alone on served tiles), check every 30th frame | Standing: every check frame's lists equal to the served frame before (scene, zoom 2.5, Britain, bank); walking: differences from movement only |
| ECS parity `CUO_ECS_PARITY=1 CUO_ANIM_SYNC=1` with the cache on (+ `CUO_GPU_ITEM_CHECK=1`): scene standing and walking (`ls_parity`), bank, items (night, lights), corpses (corpses, camp multi) | 0 mismatches (7.4M mobile and 10M item comparisons in the scene; 649k corpses); item check 0 |
| `CUO_GPU_SKIP_CHECK`, `CUO_PREP_CHECK` alongside | 0 unsettled / 0 drawn differently; 0 wrong positions / picks |
| Picking | `pickscan:8x6` Britain, bank, scene zoom 2.5: 0 different. Scene zoom 1: 1–4 different per scan in both base and new, each on an animating object (0x3996 art, dragons, mongbats). `CUO_BENCH_PICK` base vs new: same objects in both renderers |
| Shots base vs new (Gpu, Legacy, UI) at Britain, bank, zoom 2.5, the scene and its zoom-out | Britain, zoom 2.5: small moving critters only; bank: NPCs; scene: creatures and their overhead names; Legacy: NPCs, rain. Looked at. In-session cache on/off pairs at Britain and zoom 2.5: pixel-identical |
| Settle | 0 timeouts in every run (travel, scene ↔ Britain teleports, 21 GPU world recreations, facet changes) |
| `lsreinit` / `reinit` (21 recreations) with the item check | 0 differences, 0 timeouts |
| Facet change (`[set map trammel` / `felucca` on self) and chunk unload/reload (scene → Britain for 12 s → scene) | 0 differences, ECS mirror consistent |
| Unit tests | 263 pass |

**Not done:** NativeAOT build and measurements; 4K; the 120-NPC crowd; lit items in the scene by shot at night (their light data is compared by the checker, and the `items` parity run is at night); multis / player houses (none on the shard); hidden items (`[area set hidden` didn't change the flags, so the hidden-hue path had no change test).

## W5: first slices — active item updates, mobile frame cache, expired-text skip (2026-10-25)

Branch `worktree-agent-a869a121fcf3a9548`, started on poc/modern-client 48280a416 (still current at the final measurements). JIT Release, 1920×1080, kbatman3. **Another agent's client (kbatman2) ran during the measurements**, so base-vs-new sessions were noisy; the in-session off/on pairs (`w5:off|on`) are the numbers to trust. Procedure: [testing/world-update.md](testing/world-update.md). Scripts and logs: session scratchpad `w5\`.

**Profiled first** (EventPipe thread-time samples, scratchpad `w5tools\cpu`): of the 0.6 ms of mobile selection plus extract in the scene, selection was 43% (the seat lookup `TryGetSittingInfo` alone 15%), the extract 36% and the lookups 20%. The item loop is a dictionary walk in which `Item.Update` is a no-op for almost every item.

**Slices** (each behind a setting, default on; `w5:off` turns all three off):
1. `world_update_active_items` (073a27b81): per-frame item updates only for corpses and items whose handles just opened (listed through the ECS sync funnel and the handles code), the range removal and a safety sweep every 50 ms, destroyed items leave `Items` via `Entity.Destroy`; the hits percentage is computed in the `Hits`/`HitsMax` setters.
2. `gpu_mobile_frame_cache` (1f5e41261): `MobileFrameCache` reuses selection and instances while the sampled inputs, the equipment version (now a shared counter), the seat tile (`Chunk.ContentVersion`), alpha and hues are unchanged. Instances are stored relative to the draw origin with their depth recipe. Not reused with steps queued or after a random action roll.
3. `text_skip_expired` plus small items (8f28491f3): no text layout for entities whose texts all expired; `WMapManager.AcceptsLocalUpdates` once per frame; `FindNext` without LINQ. Plan/driver switches: a00b7c41d.

**In-session off → on** (scene, Gpu, ms per frame, 2 pairs each):

| Spot | frame | update | item loop | world prepare | mobile select+extract | text |
|---|---|---|---|---|---|---|
| Scene, zoom 1 | 2.87, 2.89 → 2.20, 2.12 | 0.45, 0.46 → 0.17, 0.19 | 0.29, 0.28 → 0.017, 0.015 | 1.66, 1.66 → 1.25, 1.17 | 0.71, 0.72 → 0.37, 0.35 | 0.18 → 0.045, 0.042 |
| Scene, zoom 2.5 | 3.83, 4.07 → 2.98, 3.06 | 0.50, 0.57 → 0.23, 0.24 | 0.30, 0.33 → 0.022, 0.024 | 2.35, 2.48 → 1.80, 1.80 | 1.01, 1.05 → 0.54, 0.54 | 0.21, 0.24 → 0.05 |
| Scene, walking | 3.59, 3.57 → 2.54, 2.90 | 0.60, 0.59 → 0.23, 0.30 | 0.31, 0.29 → 0.02 | 1.79, 1.86 → 1.27, 1.48 | 0.71, 0.78 → 0.38, 0.47 | 0.19, 0.21 → 0.07, 0.09 |
| 120-NPC crowd (~100 in view, fighting) | noisy | 0.042, 0.046 → 0.040, 0.032 | 0.015, 0.017 → 0.006, 0.001 | 0.075, 0.072 → 0.066, 0.054 | 0.040, 0.036 → 0.031, 0.023 | 0.007 → 0.003, 0.002 |

- **Base vs new sessions** (2 alternated rounds, noisy): scene zoom 1 world prepare 2.60, 2.31 → 1.62, 1.28; Britain 0.098, 0.074 → 0.065, 0.071; bank 0.082, 0.071 → 0.089, 0.098 (noise; item loop 0.037 → 0.003); Britain zoom 2.5 0.31, 0.23 → 0.32, 0.21. Allocation per frame unchanged (216–350 B standing, walking 6–15 KB both). Peak private bytes 1.97–1.99 GB base, 2.01–2.04 GB new (run-to-run spread; the caches hold about 60 KB at 2k mobiles).

**Verification** (new build, `CUO_ECS_PARITY=1 CUO_ANIM_SYNC=1 CUO_GPU_ITEM_CHECK=1 CUO_W5_CHECK=1 CUO_MOBILE_CACHE_CHECK=1`):

| Run | Parity | W5 checks |
|---|---|---|
| bank | 45k mobile, 18k item comparisons, 0 mismatches | 0 unlisted, 0 hits wrong, 0 destroyed left; 17k cached selections and emissions checked, 0 different |
| items (night, lights) | 74k / 47k, 0 | 0 / 0 / 0; 63k checked, 0 different |
| corpses | 256k mobile, 613k item (405k corpse, 57k multi), 0 | 405k corpse updates seen, 0 unlisted; 227k checked, 0 different |
| 120-NPC crowd | 7.5M mobile, 2.0M item (986k corpse), 0 | 1.24M corpse updates, 9.9M hits checks, 0 wrong; 7.1M checked, 0 different; 10.4M skipped text layouts, 0 would have moved a text |
| w5items (hits, handles, pack hue changes, corpses) | 0 | Hits 7/100 → 8% and 20/100 shown; 7 name gumps for handles; the bandage's hue in the backpack 0 → 0x21 → 0x59 |
| scene, `ls_parity`, earlier build of the same code | 12.9M mobile, 0 | 11.7M cached selections and 11.6M replays checked, 0 different |

| scene `ls_parity` (final build) | 8.3M mobile, 10.5M item, 0; item check 0 | 7.0M cached selections and 6.9M replays checked, 0 different; 16.3M skipped text layouts, 0 would have moved a text |
| mixed, cases (equipment changes, sitting, gargoyle, death, hidden) | 981k and 121k, 0 | 1.04M cache reuses checked, 0 different; 0 unlisted, 0 hits wrong |
| `tr:travel;bo:bodies;wk:walk` | 1.18M / 967k, 0 | 1.1M reuses checked, 0 different; settle: 9 episodes, 0 timeouts, legacy frames 0 |

- **Picking:** `CUO_BENCH_PICK` base vs new: the same objects in both renderers (clicks 0x400200B4 and the player). `pickscan:8x6` new build: Britain, bank, scene zoom 2.5: 0 different; scene zoom 1: 4 different (animating creatures, as with the base build).
- **Shots base vs new** (Gpu, Legacy, UI) at Britain, bank, zoom 2.5, the scene and its zoom-out: differences only on moving bank NPCs, the scene's flapping dragons and mongbats and other animating creatures, and the journal text. Looked at.
- **Unit tests:** 277 pass (new: `WorldUpdateActiveItemsTests`, `MobileFrameCacheTests`).
- **Not done:** NativeAOT; 4K; a base-vs-new series on a quiet machine; the next W5 step (push Sample's inputs into the ECS, see the plan).

## Forks on release tags, FNA3D fixes, owner FPS test, renderer and Legacy decisions (2026-10-25, main session)

- **SDL fork branch renamed to the owner's rule** (072ae4d88): `modernuo/3.4.18-patched` = tag `release-3.4.18` + our one patch (27ac7d26b). The old `modernuo/3.4.19` was a development snapshot (55 commits past 3.4.18) whose name looked like a release; it's deleted, with tag `archive/modernuo-3.4.19` keeping b2a3160aa reachable for older poc commits. Repro loop on the new DLL: 0 faults in 120 logins (118 in game). Rule: production fork branches are `modernuo/<release>-patched`, created from the upstream release tag, with one commit per patch on top (sdl-fork.md, fna-fork.md).
- **FNA and FNA3D forks** (072ae4d88; owner created github.com/modernuo/FNA and /FNA3D): both `modernuo/26.10-patched`. FNA3D = 26.10 + interop exports + **rebind the index buffer after a cycling upload** (the real B1 fix) + **drop a freed texture from the pending render pass** (Codex finding). FNA = 26.10 + C# bindings + `lib/FNA3D` → our fork. `tools/fna3d/build-fna3d.ps1` builds FNA3D.dll against our SDL (static CRT, `FNA3D.version`). The batcher workaround (`IndexDataChanged`) and `external/patches` are gone. A/B with the workaround removed, Legacy house spot: old FNA3D 11.7–55% near-black, ours 3.4–3.5% (6 shots each).
- **Codex audits before merging caches:** W4 (2 stale-state bugs: the highlighted container's tint baked into the cache, stale screen position for "open container near object") and W5 (active-item list kept destroyed items with the setting off). All fixed before the merges (d1cba86ea, 02ffc0b35). rf-agent's pinned harness version had to follow the installed 0.1.48.
- **Combined build check** (W5 + forks, 786d86c9c) in the large scene standing, zoomed out, walking and in Legacy: item cache 4.2 M compared, W5 631 M item checks, mobile cache 18.7 M reuses, 0 differences.
- **Owner FPS test on 072ae4d88** ([fps-test-results-2026-10-25.md](fps-test-results-2026-10-25.md)): Gpu +10% to ×3.1 over 10-24 (zoomed out 772 → 1395 fps, large window 232 → 710); allocation per frame down from about 17.5 KB to 0.1–1.4 KB in both renderers. **Legacy regressed** in its CPU phases (zoomed out world draw 0.636 → 1.004 ms, same draw calls); the 10-24 build predates B1, so B1 isn't the reason. Bisect agent running (scratch `legacybisect\`).
- **Owner decisions:** keep Legacy (possible low-end-hardware path) until the hardware range is decided, so its regression gets fixed; choose the renderer at startup by capability (`world_renderer` auto/gpu/legacy, Legacy via FNA3D D3D11/OpenGL when SDL_GPU/Vulkan or the Gpu world can't be created, never mid-session), with runtime switching behind `CUO_DEVTOOLS` tagged `CLEANUP(release)`; the Legacy project itself (D3D11/OpenGL, optimisation, fallback profiles) is deferred. Renderer-selection agent running (scratch `rendsel\`). libusb waits for a controller that needs it.
- **Large scene as server commands:** ModernUO local branch `modernuo-client/test-scene` (0036a232a deployed; 7d52e9b33, a cosmetic timing fix, not deployed) adds `[TestScene create|wipe|go|return|status`. The server was restarted by the main session with `poc/Uo.Net.Cli/scripts/stop-` and `start-modernuo.ps1` after an autosave, behind `server-restart.lock`; backups `Distribution\Assemblies\UOContent.*.pre-testscene`. The FPS test has spot 6 "Large scene" (spots 1–5 unchanged).
- **Worktree agents can't run PowerShell/cmd** (a worktree-isolation hook). Server restarts and real `run-fps-test.ps1` runs are done by the main session.

## Renderer chosen at startup by capability (2026-10-25)

Owner decision (README "Decisions so far"): `auto` / `gpu` / `legacy`, never a mid-session switch; runtime switching behind `CUO_DEVTOOLS`.

**As built:**
- `RendererSelection` holds the decision logic as pure functions; `RendererStartup` runs it.
  - **Setting:** `world_renderer` in settings.json is `auto` (default), `gpu` or `legacy`, and is saved. `-worldrenderer` overrides it for one run (`Settings.WorldRendererOverride`, never saved). The "saved Legacy is ignored" code is removed.
  - **Before the device** (Bootstrap, before `Client.Run`): SDL video is initialised for the probe only, then `SDL_GPUSupportsShaderFormats(SPIRV, "vulkan")` and `SDL_GPUSupportsProperties` with the properties FNA3D's SDL_GPU driver uses in `PrepareWindowAttributes` (SPIR-V only, hardware Vulkan, no optional features, `FNA3D_PREFER_LOW_POWER`). It passes: `SDL_GPU_DRIVER=vulkan` is set, unless already set. It fails under auto: `FNA3D_FORCE_DRIVER=D3D11` if `D3D11CreateDevice` (hardware, feature level 10.0+) works, else `OpenGL`. A user-forced driver (`force_driver`, `FNA3D_FORCE_DRIVER`) is kept; OpenGL/D3D11 skip the probe.
  - **After the device** (`GameController.LoadContent`, after `UO.Load`, before login): Gpu needs an SDL_GPU **vulkan** device (so FNA3D falling through to D3D11 is caught) and the Gpu world pass (`GpuWorldRenderer.TryCreate` + `TryEnsureFacet(0)`, kept for the session). Otherwise auto → Legacy for the session, forced gpu → `GpuDeviceLost.FailWorldSetup` (error, exit `0xDEAF`, as before).
  - **Forced legacy** keeps FNA3D's own driver order (SDL_GPU here), so parity compares the same device.
  - **Unchanged:** device loss after startup (`0xDEAD`) and a world-pass failure later, e.g. at a facet change (`EnsureFacet` → `FailWorldSetup`).
- **The decision is logged in one line,** e.g. `[renderer] auto: Gpu (SDL_GPU vulkan, NVIDIA GeForce RTX 3090 Ti)` or `[renderer] auto: Legacy (<reason>; FNA3D D3D11)`, with a Trace line before it (setting, source, driver, probe). The title marker is unchanged. `run-fps-test.ps1` prints the line and records it in the results file.
- **Test hooks:** `CUO_RENDERER_PROBE_FAIL=device|world`, `CUO_RENDERER_LEGACY_DRIVER=OpenGL`.
- **`CUO_DEVTOOLS`** (`src/Directory.Build.props`, on by default, off with `-p:CuoDevTools=false`) guards the `-worldrenderer` chat command, `RendererStartup.TrySwitch`, and all of DevBench (a stub replaces it). Runtime switches go through `TrySwitch`, which refuses `gpu` in a Legacy-fallback session. Tagged `CLEANUP(release)`; list in [release-cleanup.md](release-cleanup.md). Also fixed: Debug's `DefineConstants` was missing its `;` before `TRACE`.

**Verified** (base 8b35d1346 plus these commits, Release JIT, kbatman3, 1280×720; before the merge of e0d93d0a4):

| Check | Result |
|---|---|
| Auto on the RTX 3090 Ti | `[renderer] auto: Gpu (SDL_GPU vulkan, NVIDIA GeForce RTX 3090 Ti)` |
| Forced legacy / gpu from settings.json and from `-worldrenderer` | All 4 as expected. `legacy` in settings + `-worldrenderer gpu` ran Gpu and left `legacy` in the saved file; a file without the key saves `auto` |
| `PROBE_FAIL=device` | Legacy on FNA3D D3D11 (RTX): login, Britain/bank/house shots, gumps and UI shots, walk, picking (both clicks reached the server), `world:gpu` refused, 0 errors |
| Same, OpenGL (`CUO_RENDERER_LEGACY_DRIVER=OpenGL`) | Same results; OpenGL ran on the Intel UHD 770. User-forced `FNA3D_FORCE_DRIVER=OpenGL` with auto: Legacy without probing |
| `PROBE_FAIL=world` | Legacy on the SDL_GPU device: same checks pass |
| Forced gpu + `PROBE_FAIL=device` or `world` | Error logged, exit `0xDEAF` |
| Device loss after a Gpu startup (`CUO_SIMULATE_DEVICE_LOST=15`) | Exit `0xDEAD` |
| Gpu unchanged | Parity (`CUO_ANIM_SYNC=1 CUO_ECS_PARITY=1`): 0 mismatches; `CUO_GPU_ITEM_CHECK`: 0 differences; settle (walk, ltravel, flip, reinit): 0 timeouts, the 8 walking frames are the known leftover; Legacy house shot 1.83% near-black (all four Legacy devices 1.83–1.84%); the FPS-test plan's Legacy and Gpu entries both run; Gpu picking identical |
| `-p:CuoDevTools=false` | Builds; the switching strings (`flip done`, `[bench-pick]`, the command's messages, `CUO_BENCH_PLAN`, `TrySwitch`) are absent from cuo.dll; a run selects the renderer and ignores a bench plan |
| Unit tests | 307 pass (new: `RendererSelectionTests`, 30 cases) |
| Optional: Intel UHD 770 (`FNA3D_PREFER_LOW_POWER=1`) | Gpu works: 0 parity mismatches, correct shots, 250–300 fps (Present-bound) |

- **Legacy on D3D11/OpenGL:** nothing broken. The observations (OpenGL on the iGPU, D3D11's Present at 2.5× SDL_GPU's, no FNA3D fall-through past a forced driver) are in known-issues.md, as input for the deferred Legacy project.
- **Not verified:**
  - the full matrix re-run after merging e0d93d0a4 (both builds and the 307 tests pass there). **Done later the same day** on 3f1e70d53, with the message box and title looked at: see "Renderer selection: matrix re-run on the merged tip";
  - a real (not simulated) machine without Vulkan;
  - a world-pass failure at a facet change after a successful startup (code path unchanged, not exercised);
  - the message box (runs use `-nomessagebox`);
  - the window title by eye;
  - NativeAOT;
  - the owner's real 5-minute FPS test.

## W5: mobile inputs pushed into the ECS (2026-10-25)

Branch `worktree-agent-a97d49f3c0ed8a14c`, started on poc/modern-client 905daf319 (code as c6d1dfa49). Measured on 0b5aa5fb1 (docs-only commits after c6d1dfa49). JIT Release, kbatman3, Gpu. Setting `mobile_push_inputs` (default on). Switches: plan `pushinputs:on|off`, driver `pushinputs on|off`, both part of `w5:on|off`. Procedure: [testing/world-update.md](testing/world-update.md). Logs, shots and tools: session scratchpad `w5push\`.

**What moved.** These inputs of the mobile animation sample were read from the legacy Mobile every frame. They are now ECS state, pushed by their mutation sites:

| Input | ECS home | Mutation sites covered (each pushes) |
|---|---|---|
| Mounted (`IsMounted`: 2 equipment walks per call, called twice per sample) | `EquipmentSlots.Mounted`, set by `Rebuild` | The mirror's `EquipmentDirty`: mount item equipped or moved onto the rider (`UpsertItem`, new wearer), moved off (old wearer), graphic or hue change while worn, deleted (`Remove`), body change (`UpsertMobile`), new entity. Death re-key: the new entity starts dirty. Already covered by parity (`eq.MountItem` vs `FindItemByLayer`); `-ecscheck` now also compares `Mounted` with `IsMounted` |
| War mode | `MobileInputs.Flags` | NPCs: `Flags` via `Sync` (packet handlers 0x77/0x78/0x20 and `UpdateGameObject` end in `Sync`). Player: the `PlayerMobile.InWarMode` setter (0x72) |
| Hidden, flying (CV_7000 poisoned bit), paralyzed (Frozen), yellow hits, CV<7000 poisoned | `MobileInputs.Flags` / `HueBits` | `Flags` writes reach the ECS through `Sync`, which recomputes the inputs |
| Human / gargoyle body flags, animation body, dead body | `MobileInputs.Flags`, `Body` | `Graphic` writes reach the ECS through `Sync`. DevBench body swaps call `Sync` |
| Dead (server flag) | `MobileInputs.Flags` | `Mobile.IsDead` setter (0xBF 0x19 bonded-pet status) |
| Female | `MobileInputs.Flags` | `Mobile.IsFemale` setter (status packet, `CheckGraphicChange`; character creation and race-change preview mobiles aren't in World and are ignored) |
| SA poison (CV_7000+) | `MobileInputs.HueBits` | `SetSAPoison` (0x17) |
| Notoriety (hue looked up per frame) and invulnerable | `MobileInputs.Notoriety`, `HueBits` | `Mobile.NotorietyFlag` setter (0x77, 0x78, the player's 0x11) |
| Running | `MobileInputs.Flags` | `Mobile.IsRunning` setter (step removal, 0x77 resync) |
| Steps (has steps, front step run, facing) | `MobileInputs.Flags`, `Facing` | `EnqueueStep` (after its adds), `ClearSteps` (packet resync, `DenyWalk`, map change, `Destroy`), the player's `Walk` (after its add), `ProcessSteps`' step removal (after `RemoveFromFront`, and after `LastStepTime`). The `Direction` setter calls `Sync` |
| Walking (`IsWalking`: last step time and the player's or NPC delay) | `MobileInputs.LastStepTime`, `WalkingDelay` | `LastStepTime` writes for mobiles: `EnqueueStep`, `ProcessSteps`, `PlayerMobile.Walk`, all followed by a push. Boats write it for items only |
| Removal and reuse of serials | | `PushInputs` and `Sync` ignore an object World no longer holds for its serial; a new entity (`UpsertMobile`) gets fresh inputs from the new object; item↔mobile serial changes re-create the entity. Unit test: a replaced Mobile's setters don't reach its successor's entity |

**Still read from the Mobile every frame:** the animation clock `World.Update` advances (server action, frame, frame time, from-server), the mirror history bit, visibility, the walking offset, alpha, and the per-frame view context (selection, healthbar target, targeting, last attack, range, profile hues, the player's death).

**The seat test** (`TryGetSittingInfo`, on selection misses) also re-ran `IsMounted` and `GetAnimGroupInput`'s hand walks and the action selection. With the setting on, it takes human, mounted, flying and the action from the selection, unless that selection rolled a random action (then the legacy call runs and rolls as before).

**Verification:**

| Run | Result |
|---|---|
| `tools/ecs-parity/w5push.txt`, twice (the second logged in inside the large scene: 731 mobiles), checkers all on | `[mobile-input-check]` 1.1M and 2.54M pushed samples, 0 different; 2.1M seat tests, 0 different. Parity 1.2M and 2.6M mobile comparisons, 0 mismatches; cache check 0 different; `[w5-check]` 0; `-ecscheck` 0 mismatches (including pushed-vs-fresh inputs and `Mounted`). `mount` lines: the player mounted (ethereal), dismounted, remounted, dismounted, with ECS `Mounted` = `IsMounted` each time |
| `tools/ecs-parity/cases.txt` (cloak, hues, gargoyle bodies, flying, seated, death) with the input check | 997k samples and 911k seat tests, 0 different. Parity 988k, 0 (Sitting 16k, GargoyleBody 40k, Flying 11k, Dead 13k frames) |
| Shots, `pushinputs` off vs on, about 1 s apart (NPCs, hidden, hue, mounted, dismounted, seated, dead) | The player is identical in every pair (mounted, dismounted, re-hued, ghost). Differences only on NPCs that animated or wandered between the two shots. Looked at |
| Builds | Release and `-p:CuoDevTools=false` build |
| Unit tests | 321 pass (new: `MobileInputsTests`, 14) |

**Timing** (large scene, Gpu, 1920×1080):
- **Paired** (`CUO_MOBILE_INPUT_BENCH`, legacy and pushed sample of the same mobile in the same frame, 461M samples): 37.4 → 10.4 ns per mobile. Seat test 58 → 28 ns.
  - That is about 0.045 ms per frame at zoom 1 (1,611 mobiles drawn) and 0.055 ms at zoom 2.5 (1,985).
  - The legacy sample was about 17% of the 0.22 µs per mobile, not the 40% estimated earlier.
- **In-session off → on windows** (2 rounds × 2 windows): too noisy to show this reliably.
  - Another agent's client was running, and the same configuration moved 0.31–0.43 ms between windows; `world_mobiles` (untouched) moved with it.
  - First series, mobile select+extract (averages): 0.359 → 0.326 ms at zoom 1, 0.577 → 0.495 at zoom 2.5, 0.363 → 0.329 walking. Best windows: 0.310 → 0.246, 0.548 → 0.425, 0.293 → 0.270.
  - Second series (with the seat change): 0.369 → 0.298, 0.465 → 0.444, and walking 0.395 → 0.424 (noise).
  - World prepare and frame follow the same pattern: zoom 1 1.28 → 1.14–1.28 ms, frame 2.20–2.28 → 2.00–2.23 ms. `update` is unchanged (0.15–0.30 ms, noise).
- **Profile** (EventPipe thread time, push on, scene): the cached emission replay is 31% of the per-mobile time and `Select` misses 7.5%. The rest is the inlined update path and the ECS lookups.

**Not done:**
- `World.Update`'s mobile loop as a system over animating mobiles. It costs 0.035–0.06 ms at 2k mobiles. A due-time schedule would need a push from every `LastAnimationChangeTime` and idle-timer write (`SetAnimation` from many packet handlers, `SetIdleAnimation`, `ProcessAnimation`), plus mobiles with steps and handles, for at most about 0.03 ms.
- NativeAOT.
- A quiet-machine off/on series.
- An NPC dismount (the scenario's kill hit another NPC). The player's mount and dismount and NPCs spawning mounted were covered.

**Codex audit (merge with fixes), one P2, fixed:** `Mounted` came from the mirror's equipment index, which is keyed by the item's container. A mount equipped (0x2E) before its rider arrives is mirrored under the rider's serial, but legacy never attaches it to the rider's list (World.GetOrCreateMobile doesn't adopt earlier items), and it can outlive the rider and meet a later mobile with the same serial. Then `Mounted` was true while `IsMounted` was false. The bug was older than this patch; the patch made it visible. Now `Rebuild` computes `Mounted` from the mobile's own list (`MobileAnimationSystem.IsMounted`: first live mount-layer item, not a tiller, mount body). Every `PushToBack` attach in PacketHandlers (0x2E equip, 0x78 mobile equipment, container adds) and `RemoveItemFromContainer` is followed by `Sync`, and the mirror marks the old wearer dirty on any write to a worn item, so the slots are rebuilt whenever the list changes. Regression test: an orphan mount before its rider, a later attach (dirty, mounted), a tiller, and serial reuse.

**Coordination incident:** one profiling run (12:55:25–12:56:20) started a client while `legacyfix` held `bench.lock`: my wait loop timed out without the lock and the script carried on. The client was closed and `legacyfix`'s lock was left alone. Later runs use `round.py`, which exits if it can't take the lock.

## Gpu world parity gaps closed: effect blends, sitting deformation, animated water (2026-10-25)

Branch `worktree-agent-a1570509bc420675c`, on `poc/modern-client` 78061753b (measured on 18becdb4e; the newer commit is docs only). Closes the step 9 gaps "effect blends other than Normal/Screen, the sitting deformation, the water wobble and scaled sprites". All four were still open in the code.

**What changed:**
- **Effect blends** (`GpuWorldRenderer.CanCapture` now accepts every effect):
  - The instance's blend is a 3-bit code in `Light` (bits 10-11 plus `LightAdditive`): `InstanceBlend` Normal, Screen, Multiply, ScreenLess, NormalHalf, ShadowBlue. `HueMode` shrank to bits 8-9 (it only has 4 values).
  - The shader's output is already premultiplied (rgb·a, a), as IsometricWorld.fx's, so each blend is just GameEffect's FNA BlendState factors on a pipeline (alpha One/Zero, FNA's default). Normal and Screen keep the premultiplied pipeline. The four others are created with the world pass, so no pipeline is built mid-frame.
  - `WorldRenderer.BuildFrame` splits the sorted translucent list into runs of one blend, only on frames that have such an effect. `Renderer` draws one run per draw with `TransBase` moved to the run's start (SV_InstanceID restarts at 0; `first_instance` isn't portable with it). Frames without them are unchanged (one translucent draw).
  - ScreenRed (and unknown values) draw normally, as in GameEffectView. Before, they and the four blends were FNA draws over the world, not depth-tested.
- **Sitting deformation** (`SitDeform`, shared by the capture and the ECS extract):
  - `ISpriteCapture.DrawSitted` receives MobileView's section fractions; `MobileSpriteExtract` computes them with `SitDeform.Mod`, a port of `CalculateSitAnimation` (the body's waist/knee rows carried to its equipment). The parity harness therefore compares the fractions, and the geometry code is one copy.
  - `SitDeform.Split` replaces the plain frame with up to three sections as Batcher2D draws them: rows shifted 8 px toward the facing side, a sheared middle, the legs in place, each sampling its own (scaled) row range. Section edges are rounded to whole pixels.
  - New instance flag `FlagDeform` (Corners bits 16-23 = top-edge shear, 24-31 = source rows; the palette id stays in bits 0-15). The shader shears the quad and samples the source rows over its height.
  - The mobile frame cache now records depth kind/add for every instance a `Write` emits (it recorded only the last one).
- **Animated water** (profile `AnimatedWaterEffect`, off by default) — the only scaled world sprite:
  - New `LightWobble` bit (trim offset in Light bits 16-31) and `WaterScaleX/Y` uniforms (the former padding), set each frame from View.DrawStatic's `Time.Ticks` formula.
  - Baked: `WorldRenderer.SetWaterWobble` bakes a scaled copy of each wet flat land tile before the tile, with the same depth key and object key, so it wins the tie against its own tile, keeps the tile's order against everything else, and overrides reach both. A change rebakes (`EvictAll`, as seasons).
  - Captured: wet statics drawn by their view (animated ones) get the bit on their scaled draw.
  - Wet *statics* in the bake get no copy, matching Legacy, whose chunk mesh draws them plain.
- **DevBench:** `chair:hex|off` (a client-side chair item under the player), `water:on|off`.

**Evidence** (JIT Release, 1920×1080 window, 1280×720 world RT, kbatman4; Legacy and Gpu shots in one session):
- **Effects** (bags `0x0E75` as non-animated effects, one per blend, next to the player): Gpu and Legacy match in shape and colour for every blend. The remaining differences are under the Multiply/ScreenLess/NormalHalf effects where the stretched land beneath already differs (filtering, known), which those blends carry through. Animated fire columns per blend also look alike (their frames differ between the two shots). The world is 8 draws with 7 effects (runs), 2 without.
- **Sitting** (`chair:0B4F` and `0B51` at Britain): seated player Gpu vs Legacy at zoom 1: the second chair is identical; the first differs only on helmet/upper-body edge pixels (no pixel shift reduces it; the section edges are rounded). Before, the Gpu drew the seated frame undeformed.
- **Water** (Britain docks): Gpu with the option on vs off differs over the land water, and Gpu vs Legacy differs over exactly the same area (the wobble phase is time-based, so shots taken seconds apart can't match pixel for pixel). Static water (no wobble in either renderer) is identical. Looked at.
- **ECS parity** (`CUO_ANIM_SYNC=1`): `cases.txt` 194k mobile comparisons (12.8k CharSitting frames), `bank.txt` 61k, the sitting/water bench run 363k (24.6k CharSitting): 0 mismatches except two dock NPCs that mismatch in the base build as well (known-issues.md, "ECS parity: two Britain dock NPCs"). Items 0 mismatches in every run.
- **Standard shots, base vs new:** Gpu Britain 0.10%, bank 0.35%, zoom-out 0.03%; Legacy Britain 0.12%, bank 0.53%; UI 0.04%. Only moving creatures and journal times. Looked at.
- **Builds/tests:** Release and `-p:CuoDevTools=false` build; 322 unit tests pass.

**Timing** (JIT, ms averages, median of 3 windows, base 18becdb4e → new; one run each). The machine was noisy (several agents' clients and NativeAOT benches); base and new ran a few minutes apart, and the base run's frame times show heavy contention (zoom-out frame 2.70 vs 0.54). **Re-measure on a quiet machine.**

| Spot | world prepare | world draw |
|---|---|---|
| Britain (Gpu) | 0.062 → 0.081 | 0.071 → 0.095 |
| Bank (Gpu) | 0.062 → 0.055 | 0.068 → 0.063 |
| 7 effects at Britain (Gpu) | 0.054 → 0.058 | 0.096 → 0.082 |
| Zoom-out 2.5 (Gpu) | 0.466 → 0.189 | 0.204 → 0.088 |
| Britain (Legacy) | 0.274 → 0.171 | 0.257 → 0.212 |

- Nothing per frame scales with the view: blend runs are built only on frames with such an effect (a pass over the translucent list), the seated deformation is two extra instances per seated layer, and the wobble is two uniforms plus baked copies (per chunk, only with the option on). The Britain/zoom-out swings go both ways and exceed anything the change could cost; Legacy's code didn't change and moved as much.
- Sitting wasn't timed separately (the bench's chair was placed at the wrong spot in the timing run).

**Left:**
- Lightning (`LightningEffect`) draws gump art, which isn't in the pack, so it stays an FNA overlay (not depth-tested) with Additive blending and `SHADER_LIGHTS`, as before.
- The water check is visual only (no frozen phase); the effect check is on stretched land (a flat-land spot would give a cleaner diff).

**Codex audit fixes (merge with fixes, same day):**
- **P2, equal-depth effects across the pass split:** a Normal effect was drawn by the opaque pass and wrote depth, so a later Multiply/ScreenLess/NormalHalf/ShadowBlue effect at the same depth failed the strict depth test (Legacy: LessEqual plus painter order). Normal effects now carry `InstanceBlend.NormalOrdered`: the premultiplied pipeline with opaque output, but in the translucent pass, so every effect is ordered by depth key and then capture order there. Opaque world objects still write depth first, so an object captured after an effect at equal depth still wins, as in Legacy.
  - Checked: at Britain, Normal→ShadowBlue on one tile now shows ShadowBlue over the bag (before, the bag stayed plain), and Multiply→Normal and ScreenLess→Normal show the plain bag; Gpu and Legacy are identical over those bags. The Normal→Multiply pair on the test tile was hidden by the stairs in both renderers.
- **P2, animated wet statics on frames with animation offset 0:** with animated water on, animated wet statics always go through view capture (`CollectGpuStatic`). Legacy keeps animated statics out of its chunk mesh, so the view draws their water copy on every frame. Meshed wet statics are unchanged. Not checked live (no animated wet static at the test spots).
- **P3:** `Renderer.CreateExternal` disposes the renderer if a pipeline fails, and `Pipelines` releases the opaque pipeline if the translucent one fails. No failure injection.
- The 8,192-entry translucent list is a truncation risk for busy translucent or seated scenes (perf-opportunities.md, "GPU world parity").
- Re-verified: both build flavours, 322 tests, effect, sitting and water shots (water: same pattern as before), and a parity run with 413k mobile comparisons (24.6k CharSitting frames) and 474k item comparisons, with 0 mismatches except the known dock NPC.

## Legacy world prepare regression: split, partial fix, what's left (2026-10-25)

Branch `worktree-agent-a3e5dca4a0e9e616d` (legacyfix agent, kbatman2). Final numbers measured on `poc/modern-client` 47cb613c9 (merged), the earlier series on 905daf319/898d4c5c6. All at the large spot (`size:max`, 3840×2054, zoom 2.5 at 1495,1629), Legacy, ms/frame, per run the median (or minimum) over its six windows' p50s, then the median over runs. 613842720 ("p2", the last good merge) is the reference. **The machine was noisy all day** (Rider indexing, other projects' clients, an unrelated ClassicUO client 15:26–15:45, SentinelOne): the same binary moved by up to 1 ms between rounds, so only alternated rounds are compared.

**What the bisect missed: JIT timings are bimodal.** With tiered JIT, a run's world prepare sits in one of two modes about 1 ms apart and can switch mid-run (the same spot, `warm` vs `large`, in one process). A sampling profile of the tip shows the cause: FillGameObjectList inlines `Stopwatch.GetTimestamp` (W0's per-chunk stamps), a `SuppressGCTransition` P/Invoke with a GC poll after it, and runs in a code version where the sampler stops the thread there (99.7% of its samples in `PollGC`). The bisect's "+1 ms in 755c9599e" mixes this with real cost. `DOTNET_TieredCompilation=0` (every method fully optimised on first call, static readonly flags read from memory, as in NativeAOT) gives stable runs, so the JIT series below use it.

**Split (JIT, TC=0, stats on):**
- 613842720 → 78021a764 (W0): +0.26 ms; W0+W1 (b63b5b1c6): +0.2 ms more; later commits up to the tip: about +0.4 ms more.
- Stripping every FrameStats call from AddTileToRenderList and its helpers (experiment `nostats`): −0.55 ms at 905daf319, −0.4 at 898d4c5c6. A Legacy-only copy of the walk without W1's `skipBaked`/`forcePosition`/`stopAt`/PrepCheck parameters: only −0.15 more, so the per-object counters and detail checks are the walk's cost, not the method's size or the W1 parameters.
- NativeAOT with `CUO_FRAMESTATS_PHASES=0` (no measurement at all): p2 8.72 → tip 10.10 ms frame. So most of the NativeAOT regression is real code, not FrameStats overhead.
- An object-size experiment (p2 plus the 6 fields GameObject gained since, ~32 bytes): +0.06 ms, noise level.
- W0/W1 in NativeAOT (one alternated series, under heavy load): inconclusive.

**Fix (GameScene.cs, GameSceneDrawingSorting.cs):**
- AddTileToRenderList makes no FrameStats call per object. `objects` is a scene field and `pick_tests` a static, both added to FrameStats once per frame (`FlushWalkCounters`). The baked/unmeshed/dynamic split and the walk buckets are now detail-mode only and out of line (`DetailObject`, `DetailEnter`, `CheckMouseSelectionDetail`, all NoInlining). `FrameStats.Detail` and `PrepCheck.On` are read once per call.
- Legacy takes no timestamps per chunk: the whole chunk loop is one `prep_walk` stamp pair (it now includes chunk lookup, mesh build and reset). The Gpu world and detail mode keep the per-chunk split. Tiles are counted in a local and added once per frame.
- testing/world-prepare.md is updated (counters in detail mode, Legacy `prep_walk`).

**Results, NativeAOT (the shipping build), on 47cb613c9, 3 alternated rounds:**

| Legacy, large | p2 613842720 | tip 47cb613c9 | fix |
|---|---|---|---|
| world prepare (median) | 3.48 | 5.10 | 4.40 |
| world prepare (min window) | 3.34 | 4.98 | 4.32 |
| world draw | 4.14 | 4.43 | 4.27 |
| frame | 8.30 | 10.29 | 9.35 |

| Gpu, large | tip 47cb613c9 | fix |
|---|---|---|
| world prepare | 0.930 | 0.917 |
| prep_walk (min window) | 0.366 | 0.366 |
| world draw | 0.125 | 0.124 |
| frame | 1.547 | 1.514 |

**Results, JIT TC=0 (stats on), on 47cb613c9, 2 rounds:** world prepare p2 3.37, tip 4.57, fix 4.46 (min windows 3.31 / 4.42 / 4.27); world draw 3.29 / 3.66 / 3.64. An earlier 4-round series on 898d4c5c6: p2 3.70, tip 4.56, `nostats` 4.15, fix 4.29.

**World draw:** in NativeAOT, Legacy world draw is 4.14 (p2) vs 4.43 (tip), +0.1–0.3 ms, nowhere near the owner's 2.73 → 4.13 between the 10-24 and 10-25 FPS tests. Not reproduced on this machine; the earlier bisect agent didn't see it either. JIT world draw p2 vs tip: +0.2–0.35 ms (batcher_fna 1.24 → 1.42). That is batcher/FNA3D work, outside this change.

**Left:**
- **About 0.9 ms (+27%) of Legacy world prepare in NativeAOT** (0.9–1.1 ms in JIT TC=0) is still above 613842720, and it's real code (stats-off frame +1.4 ms). It isn't FrameStats, object size or W1's parameters. Not found today: needs a NativeAOT bisect over 613842720..tip on a quiet machine (the JIT bisect is unreliable, see above). Candidates by elimination: per-object work added after the merge in shared paths (CheckMouseSelection/PixelPicker/Art trim bounds, CalculateAlpha's settle snap, UpdateObjectHandles' item queue, the W4 per-tile serve/check branches in FillGameObjectList).
- Re-measure on a quiet machine: every table above, the W0/W1 split in NativeAOT, and the owner's world-draw rise (FPS test at 10-24 vs today's build).

**Verification:** both build flavours (Release, `-p:CuoDevTools=false`); 322 unit tests pass. Shots (NativeAOT, tip 898d4c5c6 vs fix, then the fix on 47cb613c9): house spot near-black 1.88% in all three Legacy house shots (clean); Legacy and Gpu world diffs only on walking NPCs in the house and one mobile at the large spot; UI diffs only in the journal text. Looked at.

## 2026-10-25: world-prepare-ecs-plan.md condensed (E8)

The W0–W5 status blocks in `docs/modern-client/world-prepare-ecs-plan.md` now give the goal, the current state and a link to the matching poc-log section. The background backlog, W6 and the order are unchanged. Every setting name, number and open item from the old blocks is kept in the plan.

## 2026-10-25 - E1: ECS core unit tests

Tests only, no product code changed. 322 -> 434 unit tests, all passing, no bugs found.
- WorldMirrorLifecycleTests (26 methods): create/update/remove and counts, serial reuse, item/mobile re-key, equipment and contents indexes (keyed by container, children before parent), EquipmentDirty on equip, unequip, wearer swap, layer/graphic/hue change, deletion, body change, dying-mobile key, Clear.
- AnimationRulesEdgeTests (37 methods): body ranges, ethereal mounts, IsMountedBy, LayerHue/OverrideHue precedence, Strips tiling, IsCovered per layer, AnimationTiming, PaperdollOrder.Build/Filter tables and reorder rules, AnimGroupInput.From.
- Not testable without the client file loaders: MobileAnimationSystem.Rebuild (reads Client.Game.UO.FileManager tiledata and equipconv), AnimationGroupRules.Select (needs an Animations loader), ItemSpriteExtract hue rules (inline in Emit, needs an Item and a PackSet). MobileInputRules was already covered by MobileInputsTests.

## Britain dock NPCs' parity mismatch: the Gpu world drew the cloak over the weapon (2026-10-25)

Branch `worktree-agent-ae6cfc1a46b55b33e`, started on `poc/modern-client` af171e7e8; fix in 328cae47f; verified on af171e7e8 + fix, then merged 6de44bab9 (tests and docs only). Was the known issue "ECS parity: two Britain dock NPCs mismatch (equipment order)". Account kbatman, JIT Release, 1280×720. Logs, packet logs and shots: session scratchpad `correct\`.

**Cause: the ECS was wrong; Legacy (MobileView) is the reference.**
- MobileView builds the full layer order (`PaperdollOrder.BuildInWorld`: base table, reorder rules, then `ApplyDirectionCloak` on the whole layer list, empty layers included) and skips empty layers while drawing. For facings other than north (0) and south (3), the cloak goes just before Helmet, which every table lists, so it lands under the weapons even when no helmet is worn.
- The ECS (`EquipmentSlots` + `AnimationRules.ApplyDirectionCloak`) moved the cloak on the drawn slots only. With no helmet drawn it found no Helmet and put the cloak last, over the weapon.
- The two NPCs are the only cloak + weapon + no-helmet humans in the parity scenes, which is why only they mismatched (every frame in which they face 1, 2, 4–7). Not a World-vs-mirror equipment index problem and not the virtual hair: both sides held the same items on the same layers.
- **Visible:** in a pure Legacy vs Gpu pair of the same frame (base build, pair 13 of `shots3-base`), Legacy draws the longsword's blade over the cloak's top corner; Gpu hides that part of the blade under the cloak. Only a few pixels where the two overlap, in back-facing poses. Looked at (`correct\p13_base_zoom.png`, `evidence_base_p15.png`).

**Fix (328cae47f):** `EquipmentSlots.CloakAnchor`, set by `Rebuild` through `AnimationRules.CloakAnchor`: the number of drawn non-cloak slots that come before Helmet in the full order. `ApplyDirectionCloak` inserts the cloak there for the "below the helmet" facings; the extract and `-ecscheck` both pass it. New test `Cloak_Order_Matches_MobileView_On_The_Drawn_Layers` compares the slot ordering with MobileView's full-order move for 8 layer sets (the dock NPCs' set, no helmet with one or two weapons, with a helmet, robe, cloak only, no cloak) in all 8 facings; the old code fails it.

**What the server sent (predicted from ModernUO, then checked in the client's packet log, `-packetlog`):**
- `OutgoingMobilePackets.SendMobileIncoming` (0x78) writes the mobile's `Items` in list order, skipping deleted, invisible and repeated layers, then the virtual hair (`HairSerial`, layer 11) and facial hair (`FacialHairSerial`, layer 16) if the mobile has them. `Mobile.HairSerial` takes `World.NewVirtual` (a counter from `World.MaxItemSerial`) on first use, so each mobile gets its own serial.
- Observed for 0x00037567 (and the same shape for 0x00037566): Shirt 0x1EFD, Pants 0x152E, Shoes 0x170B, Tunic 0x1541, Cloak 0x1515, OneHanded 0x0F61 (longsword), Backpack, then the virtual hair 0x7EEEF820 (0x00037566's is 0x7EEEF81F). No facial hair, no duplicate layers, no 0x2E for them, serials not shared between the two. Exactly as predicted. **No ModernUO issue.**

**Verification (after the fix):**

| Run | Result |
|---|---|
| Dock (`CUO_ECS_PARITY=1`, `CUO_ANIM_SYNC=1`, `CUO_GPU_ITEM_CHECK=1`, `-ecscheck 30`) | Base: 124,915 of 484,359 mobile comparisons mismatched, 3,869 `-ecscheck` lines per NPC (cloak and sword swapped). Fix: 558,973 comparisons, **0 mismatched**; `-ecscheck` 0; items 0; item cache 0 differences |
| `cases.txt` (copy without the death section, see below) | 138,886 mobile comparisons (15.3k CharSitting, 101.7k cloak frames), 0 mismatched; items 156,651, 0; `-ecscheck` 0; item cache 0 differences |
| `bank.txt` | 71,447 mobile comparisons, 0 mismatched; items 25,957, 0; item cache 0 differences |
| Legacy world (`-worldrenderer legacy`, house and Britain, base vs fix) | Britain identical; house differs only in one walking NPC (0.22% of pixels); near-black 3.28% vs 3.29% (853×480 shots). Looked at |
| Unit tests | 330 before the merge, 442 after (434 + 8 new) |

- **kbatman is the owner's character, so the cases run used a copy of `cases.txt`** (`correct\cases-kb.txt`) without `[mortal`/`[kill`/`[res`/`[immortal` (a death moves worn items and the pack into a corpse) and with cleanup at the end: the added cloak removed (`[remove` + `target worn 20`), `equip` showing only hair and backpack, and `[go 1495 1629`. Body, hue and flying were reset by the script as usual.

## Renderer selection: matrix re-run on the merged tip; message box and title looked at (2026-10-25)

Re-run of the matrix from "Renderer chosen at startup by capability", which hadn't been run since the merges. Builds of 3f1e70d53 (`poc/modern-client` 6de44bab9 + the cloak fix above), default flavour (`bin-dev`) and `-p:CuoDevTools=false` (`bin-nodev`; the dev strings `flip done`, `[bench-pick]`, `CUO_BENCH_PLAN` are absent from its cuo.dll). Release JIT, kbatman, 1280×720, other agents' clients running. Procedure: [testing/renderer-selection.md](testing/renderer-selection.md) (new). Logs and shots: session scratchpad `correct\rendsel\`.

| Case | Dev | Release flavour |
|---|---|---|
| auto → Gpu | Pass: `auto: Gpu (SDL_GPU vulkan, NVIDIA GeForce RTX 3090 Ti)` | Pass |
| Forced legacy, settings.json | Pass: `legacy: Legacy (forced by settings.json; SDL_GPU vulkan, …)` | Pass |
| Forced gpu, settings.json | Pass | (not run) |
| Forced legacy, `-worldrenderer` (gpu saved) | Pass: `forced by -worldrenderer` | (not run) |
| Forced gpu, `-worldrenderer` (legacy saved) | Pass; saved file still `legacy` | Pass; saved file still `legacy` |
| `PROBE_FAIL=device` → Legacy on D3D11 | Pass: `auto: Legacy (…; FNA3D D3D11)` on the RTX; full plan (Britain, gumps, bank, walk, house), `world:gpu` refused with the warning, picking (static, land, item, player selected; item and player clicked), 0 errors; house near-black 3.26% | Pass (`[renderer]` line, title, 0 errors) |
| Same on OpenGL (`CUO_RENDERER_LEGACY_DRIVER=OpenGL`) | Pass: `FNA3D OpenGL` on the Intel UHD 770; same checks; house 3.27% | Pass |
| `PROBE_FAIL=world` → Legacy on the SDL_GPU device | Pass: `auto: Legacy (…; SDL_GPU vulkan, …)`; same checks; house 3.28% | Pass |
| User-forced `FNA3D_FORCE_DRIVER=OpenGL` with auto | Pass: `auto: Legacy (FNA3D driver forced to OpenGL; …)` | (not run) |
| Forced gpu + `PROBE_FAIL=device` (settings) | Pass: `gpu: unavailable`, exit 0xDEAF | Pass |
| Forced gpu + `PROBE_FAIL=world` (`-worldrenderer`) | Pass: exit 0xDEAF | Pass |
| Message box (forced gpu failing, no plan, no `-nomessagebox`) | Shown, OK pressed, exit 0xDEAF (device and world) | Shown, exit 0xDEAF |

- The three house shots (853×480) match a pure-Legacy SDL_GPU house shot of the same size from today (3.28%), so no B1-style corruption on any device.
- Release-flavour sessions ignore the plan (no bench lines) and were closed after 35 s; they exit 0.

**What the owner hasn't seen yet (captured with `PrintWindow`):**
- **Message box** (Windows dialog, title "ClassicUO", red error icon, one OK button): "The GPU world renderer could not start (simulated (CUO_RENDERER_PROBE_FAIL=device)). ClassicUO will close." With a real failure the parentheses hold the probe's reason instead of "simulated (…)". Behind it, the client window is already up and showing the login screen.
- **Title:** `ClassicUO - 1.1.0.0` in Gpu mode; `ClassicUO [Legacy renderer] - 1.1.0.0` in Legacy, `Kamron - ClassicUO [Legacy renderer] - 1.1.0.0` once logged in. Same in both flavours.

**Wording that may confuse (suggestions; nothing changed):**
1. **The box gives no way out.** The log says `world_renderer "auto" or "legacy" in settings.json, or -worldrenderer, runs Legacy instead`; the box doesn't. A player who set `gpu` (or got a settings file with it) only learns that the game closes. Suggestion: "ClassicUO can't start the GPU renderer that is selected in settings.json (world_renderer: gpu). Reason: <reason>. Set world_renderer to "auto" to let ClassicUO choose a renderer this computer supports. ClassicUO will close."
2. **Nested parentheses and internal names:** "(simulated (CUO_RENDERER_PROBE_FAIL=device))" in tests; real reasons are probe texts (SDL/Vulkan wording). Put the reason on its own line after "Reason:".
3. **The window behind the box says "[Legacy renderer]"** while the box says the GPU renderer (which the user forced) failed and the game will close. The title flag follows `UseGpuWorldRenderer`, which is false at that point. Suggestion: no renderer tag in a session that is exiting, or set the title after the decision.
4. **"[Legacy renderer]" on an auto fallback.** The title code still treats Legacy as "a developer comparison mode", but it is now auto's normal choice on machines without Vulkan, so real players would see "Legacy renderer" in their title bar, which reads like an error or an outdated mode. Suggestion: keep the tag in the dev flavour only, or call it "Compatibility renderer" in the release flavour.
5. **"GPU world renderer" vs Legacy:** both draw on the GPU, so "GPU renderer" as the name of one of them is ambiguous to a player. Fine in logs; in user-facing text, "the new renderer" / "compatibility renderer" (or whatever names the owner picks) would read better.

## Per-sprite override cut: baked translucent alpha, out-of-range tint as a uniform (2026-10-25)

Branch `worktree-agent-ad5fb48ffeb803a6e` (override-cut agent, kbatman4), started on `poc/modern-client` af171e7e8, measured on 27e0ff95d (the later merges were docs and tests only). Closes the two "GPU world parity (step 9)" items in perf-opportunities.md: translucent statics and `NoColorObjectsOutOfRange` were one override per sprite per frame.

**What changed:**
- **Translucent statics** (Uo.Render):
  - `BakeJob` bakes tiledata Translucent (0x8) statics at alpha 178, ClassicUO's settled `ProcessAlpha` value, and records their chunk-local indices.
  - At upload, `ChunkStore` turns them into the translucent pass's sort keys (`TranslucentKeys(slot)`), and `BuildDrawList` lists the visible chunks that have any. `WorldRenderer.BuildFrame` copies those keys into the sorted list: per chunk, no per-instance work. Hidden and z-hidden ones collapse in the shader as before.
  - **An override's alpha now replaces the baked alpha** (sprite.hlsl). Before, it multiplied, which gave the same result while every bake was 255. `OverrideEntry.KeepAlpha` (alpha -1 in `SetLandOverride`/`SetStaticOverride`) keeps the baked alpha, for highlight-only overrides.
  - A fade still sends an override with the absolute `AlphaHue`. The override loop no longer lists a baked translucent instance, because its chunk's sub-list already does; the circle-of-transparency scan already skipped them.
  - Client: `GpuStaticOverride` compares `AlphaHue` with the baked alpha (`GpuBakedAlpha`, from the pack's tiledata of the baked graphic), so a settled translucent static needs nothing.
  - The Gpu prepare skip's walk reason "translucent" is gone. Reason 5 is now "translucent mismatch": the client and pack tiledata disagree (0 seen).
- **Out-of-range tint** (`NoColorObjectsOutOfRange`):
  - New uniforms `RangeTintRow`, `RangeX`, `RangeY` and `RangeDist` (`WorldRenderer.SetRangeTint`), set each frame from `World.RangeSize` and `ClientViewRange`.
  - The shader tints every FLAG_ZTEST instance whose tile's Chebyshev distance exceeds the range. The tint covers the whole sprite and wins over the death tint (`TintRow`); a highlight override wins over both, as in ApplyMeshHue/LandView/StaticView.
  - Tile per instance:
    - land: exactly from X/Y and z (stretched land: X/Y only);
    - statics: the tile's low bytes in Light bits 16-31 (`SpriteInstance.StaticTileBits`), compared modulo 256 (visible tiles are well within 128 of the player).
    - `SpriteInstance.Tile` and `WorldRenderer.RangeTinted` are the CPU mirrors.
  - Client: `GpuTintHue` is gone, so land and statics never get a tint override. The land fast path no longer excludes the option, and the Gpu prepare skip no longer turns off with it.
- **DevBench:** new plan entries `outrange:on|off` and `range:N` (sets `ClientViewRange` locally, as 0xC8 does).

**Where the two overrides used to be generated, and how each is handled now** (for the audit):

| Site (base) | Case | Now |
|---|---|---|
| `GameSceneGpuWorld.GpuStaticOverride`: meshed statics from `GpuOnDrawnSlowCore`; unmeshed baked statics from `CollectGpuStatic`, foliage included via `FinishGpuCollect` | Translucent: `AlphaHue` 178 ≠ 255 | Compared with the baked alpha (178): no override when settled. A fade sends the absolute alpha, which replaces the baked one |
| Same | Out of range: `GpuTintHue` gave the highlight hue `OUT_RANGE_COLOR` | No hue argument; shader uniform |
| `GpuOnDrawnSlowCore`, `case Land` | Out of range: `GpuTintHue(land)` | No hue |
| `GpuOnDrawn` fast-path condition `!_gpuTintOutOfRange` | With the option on, every land tile went to the slow path | Removed |
| `GameSceneGpuPrepare.GpuWalkReason` 5 | Translucent baked statics forced their tile into the full walk every frame, to send the override | Only on a client/pack tiledata mismatch |
| `GpuPrepareBegin` (`!profile.NoColorObjectsOutOfRange`) | The option disabled the whole prepare skip | Removed |
| `FinishGpuCollect` highlight | Passes `AlphaHue` explicitly | Unchanged. The explicit alpha replaces the baked one, so a translucent static keeps 178 under the highlight |
| Views drawn by capture (`GpuReplaceStatic`, multis, items, mobiles, effects) | Their own hue and alpha in the dynamic instance (never overrides) | Unchanged |

**Overrides per frame** (`gpu_overrides`, steady state; `override_calls` in brackets), base → new:

| Spot | Option off | Option on (`outrange:on`, range 24) |
|---|---|---|
| Britain 1495,1629, zoom 1 | 49 → 49 (59 → 58) | 331 → 49 (2,698 → 59) |
| Bank 1430,1690, zoom 1 | 24 → 24 | 316 → 24 (1,522 → 17) |
| Zoom 2.5 at Britain | 112 → 112 | 8,141 plus about 2,500 dropped → 112 (12,063 → 161) |
| Large window (3840×2054, zoom 2.5) | 115 → 108 | 8,094 plus dropped → 108 (40,569 → 811) |
| Blood pool, Trammel 956,700 (120 translucent statics) | 132 → 15 (516 → 49) | – |
| Large-scale scene, zoom 1 | 0 → 0 | – |

- **Translucent statics are rare in this tiledata:** blood, blood smears, webs and spilled flour. No windows or water carry the flag, and Britain has three. So with the option off, only such spots gain. The 7 fewer overrides at the large window are its translucent statics.
- **The base build was wrong zoomed out with the option on:** the pending list overflowed (2,500–3,750 dropped per frame), so water, trees and shore were drawn untinted. The new build matches Legacy. Gpu-vs-Legacy differing pixels went 41.6% → 23.8% at range 12 and 47.1% → 18.7% at range 5; the rest is the known zoomed-out filtering difference.

**Timing** (JIT Release, 1920×1080, ms averages; 2 alternated rounds, base then new, then new then base; mean of the rounds).
- The machine was noisy (three other agents' clients and builds), and round 2 ran with frame times about 0.2 ms lower overall.
- **Re-measure on a quiet machine:** Britain, bank and zoom-out with the option off, and the large window.

| Spot | World prepare, base → new | World draw, base → new | Frame, base → new |
|---|---|---|---|
| Britain | 0.048 → 0.047 | 0.070 → 0.069 | 0.745 → 0.741 |
| Bank | 0.054 → 0.053 | 0.069 → 0.077 | 0.740 → 0.742 |
| Zoom 2.5 | 0.182 → 0.187 | 0.074 → 0.073 | 0.754 → 0.755 |
| Large window | 0.720 → 0.700 | 0.127 → 0.120 | 1.763 → 1.724 |
| Large-scale scene, zoom 1 | 0.934 → 0.938 | 0.277 → 0.274 | 1.657 → 1.660 |
| Blood pool | 0.092 → 0.071 | 0.073 → 0.061 | 0.747 → 0.745 |
| Britain, option on | 0.204 → 0.047 | 0.093 → 0.061 | 0.759 → 0.737 |
| Bank, option on | 0.169 → 0.054 | 0.083 → 0.057 | 0.748 → 0.736 |
| Zoom 2.5, option on | 1.173 → 0.194 | 0.618 → 0.086 | 2.060 → 0.763 |
| Large window, option on | 4.651 → 0.712 | 0.690 → 0.121 | 5.799 → 1.720 |

- **Option off:** unchanged within noise. The bank's world draw went 0.079 → 0.071 in round 1 and 0.059 → 0.082 in round 2.
- **Option on:** the prepare skip stays on and the override resolve is gone. At the large window the frame goes from 5.8 to 1.7 ms.
- **Nothing new scales with the view per frame:** the translucent lists are built per chunk at upload and copied per visible chunk, and the range test is a few ALU operations in the vertex shader per baked instance.

**Parity** (one session per build, with Gpu and Legacy shots of each spot in the same session; all looked at):
- **Base vs new, Gpu:**
  - 0 differing pixels: Britain, Britain with the option on (two player positions), Britain at the end of the plan.
  - 0.00–0.47%, only moving NPCs and ghosts: bank, bank zoomed out, the bank wall with the circle of transparency, the blood pool (zoom 1, zoom 2, with the circle), the death screen with and without the option.
  - Zoomed out with the option on: the base build's untinted areas (above).
- **Gpu vs Legacy** (new / base): equal or closer everywhere.
  - Britain 7.20 / 7.20%, bank 16.0 / 16.1%, circle of transparency 15.5 / 15.4%.
  - Blood pool 0.70 / 0.70%; the translucent pentagram is identical.
  - Out of range at Britain 5.54 / 5.54%, and 5.65 / 5.65% after a 4-tile move: the range diamond follows the player, as in Legacy.
  - Death screen 0.19 / 0.36% with the option, 0.47 / 0.58% without.
- **Fades** (per-frame shots after client-side teleports with the settle hold off: `CUO_WORLD_HOLD=0`, `CUO_BENCH_TPSHOTS`, option on, range 10):
  - Walls fading in look the same in both builds.
  - Base drew out-of-range objects untinted for the first 2 frames after a teleport; new tints them from the first frame.
  - The settled frames are identical apart from rain.
- **Checkers:**
  - `CUO_GPU_SKIP_CHECK` over the whole shot plan (option on and off): 0 unsettled, 0 drawn differently, 0 "translucent mismatch".
  - `CUO_GPU_ITEM_CHECK`: 0 differences.
  - ECS parity (`cases.txt`, `CUO_ANIM_SYNC=1`, `CUO_GPU_ITEM_CHECK=1`): 189k mobile and 67k item comparisons, 0 mismatches. The run met neither of the two dock NPCs.
- **Builds and tests:** Release and `-p:CuoDevTools=false` build; all unit tests pass (322 before merging the E1 tests, 434 after).

**Codex audit (merge with fixes), four P2s, fixed in 57b1f117e:**
1. **Translucent capacity.** The chunk lists used up the 8,192 entries before overrides and z-hiding were evaluated, so hidden baked translucents could push out a visible fading wall, which the opaque pass also rejects.
   - Baked translucents are now listed after the override loop, and only when the frame shows them (an override can hide or force-show them; z-hidden ones are skipped).
   - The CPU list grows on demand, and the GPU frame table follows (`Renderer.EnsureTranslucentCapacity`): recreated at the next power of two, up to `Renderer.TranslucentLimit` = 262,144 entries (2 MB, within the 8 MB frame transfer buffer).
   - Only entries past that limit are dropped. Drops are counted in `OverridesDropped` and logged at most every 10 s; growth is logged.
   - Seen in the checks run: the large-scale scene grew the list to 16,384 on arrival, with no drops.
2. **Modulo-256 aliasing.** At 7680×4320, zoom 2.5, range 24, a visible static could wrap to a tile in range and stay untinted.
   - A static's tile is now estimated from its sprite rectangle (horizontal centre and bottom edge), then corrected to the packed low bytes. The shader and `SpriteInstance.Tile` use the same maths.
   - A bake-time self-check (`BakeJob.TileMismatches`) compares every baked land tile and static with its real tile, and the client warns if it's ever nonzero. It was 0 in every run.
3. **Uo.Client.** It drives `Renderer` directly, with no translucent pass, so baked blood and webs vanished there. A new `PackSet.BakeTranslucentAlpha` flag, set only by `WorldRenderer`, makes the alpha-178 bake opt-in; other consumers bake those statics opaque, as before.
4. **Item cache vs range tint.** Removing the skip exclusion turned on the ground-item cache with the option on, and cached items kept their old tint after a move or a 0xC8 range cut.
   - The item signature now includes `NoColorObjectsOutOfRange && Distance > ClientViewRange`. A flip sends the tile back to the walk on the same frame, and the chunk is re-extracted on the next one.
   - The option itself is part of the cache's generation key.
   - Keeping the cache costs less than turning it off with the option on. Measured standing at the large-scale scene centre (range 10, 2 rounds): world prepare 0.36 ms with the cache vs 0.63 ms without; 5,405 items served; frame 0.90 vs 1.08 ms.
   - The lane-walk windows saw few items (0.03 ms either way) and can't separate the two.

**Re-verification of the fixes and the first round's key claims** (on f9c5712fa = 57b1f117e plus `poc/modern-client` 56f8fdc4a; plain inline commands, one per run):
- Both build flavours; 442 unit tests pass.
- `CUO_GPU_SKIP_CHECK=1` and `CUO_GPU_ITEM_CHECK=1` with the option on: Britain and the bank at `range:5` and `range:18`, a walk, and the large-scale lane at `range:10` and `range:6`.
  - 32 check intervals: 0 unsettled, 0 drawn differently, 0 cache differences.
  - Range flips while walking were caught by the item signature (15–20 tiles per interval) and redrawn by the walk.
- **Shots, Gpu vs Legacy** (looked at):
  - Britain 7.20%, blood pool 0.70%, Britain with the option on (range 12) 5.54%: identical to the first round's numbers.
  - The zoomed-out large window (3840×2054 window, 2560×1369 world target), option on: 22.6% at range 12 and 21.2% at range 5, the known zoomed-out filtering difference. The in-range diamond and the gray area match Legacy.
  - New vs first-round Gpu shots: blood 1 px; Britain 0.12%, the player's idle frame.
- ECS parity (`cases.txt`, `CUO_ANIM_SYNC=1`, `CUO_GPU_ITEM_CHECK=1`): 201k mobile and 69k item comparisons, 0 mismatches.
- **Not run:** the Uo.Client offline smoke run. The worktree guard refused the single-command form (`Uo.Client.exe --pack C:\… --screenshots …`), and per the owner's rule it was skipped, not re-wrapped. Fix 3 is unverified at run time; by construction, Uo.Client's bake is the pre-cut bake.

**Left:**
- Captured views (wet animated statics, multis, items, mobiles, effects) keep their own hue and alpha paths (unchanged).
- The pending-override list (8,192 entries) still drops on overflow (counted and logged). The translucent list grows instead, up to 262,144 entries.

### 2026-10-25 — B8 and B9: UI render cache ready to merge, batch regrouping not worth it (batcher agent)

Branch `worktree-agent-a08462117e88b693a`, from 18becdb4e, merged up to poc/modern-client eaa489c92 (final numbers and checks on that merge, 587a27d24). Commits: 8abd49133 (B8), merges 4613e365b and 587a27d24, docs. B9 experiment: branch `poc/b9-batch-sort-experiment` (8dc645168), not merged. JIT Release, 1920×1080 at DPI 1.5, kbatman3. Procedure: [testing/ui-batcher.md](testing/ui-batcher.md), "UI render cache (B8)".

**B8 design** (setting `ui_render_cache`, default on; `uicache:on|off`, `-uicache`)
- **True dirty tracking was rejected.** Control draws read time, the mouse and world state everywhere, and some have side effects: `HealthBarGump` and `ContainerGump` set `SelectedObject.HealthbarObject`/`SelectedContainer` while drawing, and the world draw reads them the next frame. GameScene.DrawUI also runs `ProcessWorldText` and clears `SelectedObject.Object`.
- **So the UI pass still runs in full every frame.** The batcher records it instead of drawing (`Batcher2D.Recording.cs`). Each flush becomes a segment with its sprite range and every state its draw uses: blend, depth-stencil, sampler, scissor enable and rectangle, transform, viewport, brightlight, transparency circle, and whether a `Begin` ran since the last flush (the effect is applied again there, as before).
  - `StopRecording` compares the segments, the texture references and the vertex bytes with the last submission to the same target.
  - Same: the target, which keeps its contents (`PreserveContents`), is left alone. Changed: it's cleared and the segments are replayed with the same FNA calls `Flush` makes.
  - A custom effect, a mesh draw or a world offset during a recording aborts it: what was recorded is drawn, and the rest of the pass draws directly. The UI never does this.
- **Two stages** (GameControllerUiCache.cs):
  1. Scene UI and gumps go into a new UI cache target.
  2. The UI target gets an opaque 1:1 copy of it plus the cursor, recorded the same way. Moving the mouse over the world redraws only stage 2. When neither changed, no UI GPU pass runs at all.
- **What a recording can't see invalidates both:**
  - the targets being recreated (`RenderTargets.UiTargetsVersion`: resize, DPI, the setting toggled);
  - `GraphicsDevice.DeviceReset`;
  - the hue samplers bound in texture slots 1 and 2;
  - any texture upload. `TextureUploads.Changed()` follows every `SetData`/`SetDataPointerEXT` on a texture: atlases, the minimap writing into its atlas slot in place, the world map, multi maps, the aura, solid colours, sprite fonts, plugin textures, the screenshot texture. A unit test scans the sources for upload sites without it.
- **Dirty sources, the complete list:**
  - anything that changes a vertex, a texture reference or a segment state of the recorded UI pass;
  - a target recreated; a device reset; the hue sampler bindings; any texture upload;
  - a recording aborted;
  - the window too small to draw the UI (the uncached path runs and invalidates).
- **Fallbacks (drawn every frame):** none per control. A frame whose recording differs is drawn in full, so when the UI changes every frame, every frame is drawn. With the cache off, or no UI target, the old path runs unchanged.
- **Known limits:**
  - State objects (BlendState etc.) are compared by reference, so one mutated in place would go unseen. The UI uses the stock ones.
  - Texture contents changed outside our code (FNA internals) aren't tracked. The UI draws none.

**Results** (µs per frame, FrameStats averages of 3 windows; paperdoll, backpack, journal, status and 4 health bars; `off` and `on` alternate in one session, in both orders; the `ui` plan mode loops scripted UI changes):

| Run | UI pass off → on | UI flushes / draw calls off → on | Draw off → on | UI target kept |
|---|---|---|---|---|
| Static, 4613e365b, two orders | 100 / 97 → 54 / 54 | 7–8 / 17–18 → 0 / 0 | 295 / 292 → 248 / 251 | 100% |
| `ui` scene, 4613e365b, two orders | 104 / 104 → 61 / 61 | 7.4–8.4 / 18 → 0.4 / 0.6 | 307 / 311 → 261 / 264 | 90–93% (gumps target 99%+) |
| Static, final 587a27d24 | 103 → 52 | 7 / 17 → 0 / 0 | 323 → 260 | 100% |
| `ui` scene, final 587a27d24 | 110 → 72 | 7.4 / 18 → 0.4 / 0.6 | 358 → 328 | 90% |
| Every frame changed (`CUO_UI_CACHE_FORCE=1`) | 100 → 117 | 7 / 17 → 8 / 18 | 317 → 351 | 0% |

- **What's left when the UI is kept (about 50 µs)** is the gump tree walk (8–12 µs) and turning the queued commands into vertices, mostly text glyphs. The compare takes a few µs.
- **Worst case** (+17 µs): the copy draw, one more flush, and recording plus replay.
- **Noise:** the machine was noisy all session, with two or three other agents' clients and NativeAOT benches. The final-merge runs ran at 1,078–1,469 fps, against about 1,300–1,360 fps earlier. The off/on deltas within a session are consistent (−43 to −51 µs of UI pass); absolute values, Draw and fps aren't. **Re-measure on a quiet machine:** every row of the table. NativeAOT wasn't measured.

**Pixel identity**
- **Self-check** (`CUO_UI_CACHE_CHECK`): the UI target is read back and compared with a replay of the recordings (no copy) and with a second, direct UI pass, every 5th–10th frame.
  - The check plan covered the `ui` scene (hover, tooltip, journal moving, chat caret, a held item, health bar and status hits changing, zoom 1 and 1.25, the world map gump), a resize to 1600×900 and back, vsync on and off (device reset), and the `ui` scene again in Legacy.
  - Result: 12,940 checks (12,800 with the gumps target kept), with **0 replay and 0 direct mismatches**.
  - The shot run added 6,282 checks, including 20 renderer flips: 0 and 0.
- **Shots:**
  - UI target, cache off vs on in one session, moments apart: identical (0 px) at both moments.
  - New (cache on) vs the base build eaa489c92: only the journal text differs (live messages and times).
  - Gpu world: Britain is identical; the bank differs only where an NPC moved.
  - I looked at the overlays and crops.
- **Legacy (required):** pure Legacy at the house spot: 1.89% near-black in all three new runs and in the base run (clean). New vs base differs only on moving mobiles and on scattered single pixels, which two new runs also show between them. I looked at the images.
- **Tests:** 338 pass: 322 plus 16 new (recording equality, every segment state, −0 vs 0, and the texture-upload source scan).
  - `ChunkLoadingTests.Prefetched_Regions_Give_The_Same_Blocks_And_Never_Block_IsBlockReady` is flaky on the final merge: it failed 2 of 5 runs, with no code of this branch involved (chunk prefetch threading). Not looked into.
- **Builds:** Release and `-p:CuoDevTools=false` both build on the final merge.
- **Codex audit (merge with fixes), fixed:**
  - **P2:** the minimap rewrites its atlas slot while it draws, with the same texture, UVs and vertices, so the frame was accepted as Same and showed the old map for one frame. Each stage now also compares `TextureUploads.Epoch` after it draws with the frame's start value and redraws if it changed (`UiCacheState.Resolve`, unit-tested). The start value stays stored, so the next frame redraws too.
  - **P3:** segments record the full viewport (origin, size, depth range); replay applies it and restores the caller's.
  - **P3:** an aborted recording restores the caller's vertex and index buffer bindings after the replay (`DrawDirectIndexed` with mesh buffers already bound).
  - **Checked:** both flavours build; 465 tests pass. A check run with the minimap open while walking (Gpu, then Legacy) plus the `ui` scene: 11,839 checks, 0 replay and 0 direct mismatches. Legacy house shot 1.88% near-black.
  - Re-checked after merging poc/modern-client bbd86eb37 (11f41fd06): both flavours build, 465 tests pass, the minimap-walk check (Gpu and Legacy) found 0 and 0 in 8,406 checks, and the Legacy house shot is 1.88% near-black.
  - **Found on the way:** a minimap opened between `UIManager.Update` and drawing (DevBench's position in the frame) throws in `CreateMiniMapTexture` before its first `Update` (stock ClassicUO). The new DevBench `minimap` entry updates the gump once.
- **Later merge** 70e81df8f (poc/modern-client 56f8fdc4a: ECS tests and cloak order, no UI or batcher files): both flavours build, and 458 tests pass. No client runs were repeated on it.

**B9 (batch-key sorting): not merged**
- **Tried:**
  - An order-safe regrouping of each UI flush by texture. A sprite joins an earlier run of its texture only if its box overlaps none of the runs queued after that run. Pixels stay identical: overlapping pairs keep their order, and disjoint ones touch disjoint pixels.
  - Skipping `Set{BlendState,Stencil,Sampler}` flushes when the state doesn't change.
- **Measured** (same scene, cache off, one session): UI draw calls 17.6 → 14.6, but UI batcher time 50 → 66 µs and the UI pass 103 → 119 µs. The planning and copying cost more than the three draw calls saved. Skipping redundant states saved no flushes anywhere (13–14 per frame either way).
- With B8 on, B9 could only help frames whose UI changed, and even at zero cost it would save about 9 µs on them.

**Also**
- New FrameStats phases and counters: `ui_pass`, `ui_traverse`, `ui_fna`, `ui_flushes`, `ui_draw_calls`, `ui_cached`, `ui_untouched`.
- New DevBench tooling: `healthbars`, `uicache:`, plan mode `ui` (it moves the mouse through `Input.Mouse.Override`, so the OS cursor stays put), `CUO_UI_CACHE_CHECK[_DIR]` and `CUO_UI_CACHE_FORCE`.
- Health bar gumps allocate 0.8–1.4 KB per frame on the main thread, in both modes (perf-opportunities.md).
- **Files outside the batcher/UI that changed:**
  - texture upload sites, one `TextureUploads.Changed()` line each: Client.cs, AuraManager, MiniMapGump, WorldMapGump, MultiMap, Plugin.cs, Primitives2D, SolidColorTextureCache, SpriteFont, TextureAtlas;
  - `Input.Mouse.Override`;
  - Settings, CommandManager, FrameStats, DevBench.
  - GameScene.cs, GameSceneDrawingSorting.cs and the Gpu world renderer weren't touched.

## Legacy world prepare, part 2: the walk's codegen, not its work (2026-10-26)

Branch `legacy2-prepare` (legacy2 agent, kbatman2), commit 8b156011c on `poc/modern-client` bbd86eb37. All numbers NativeAOT, Legacy unless noted, the large spot (`size:max`, 3840x2054, zoom 2.5 at 1495,1629), ms/frame, per run the median of its window p50s, then the median over 3 alternated runs. The machine was quiet this evening (the same binary moved by under 0.1 ms between rounds); re-measure on a quiet machine anyway.

**Culprit: AddTileToRenderList's native code.** An experiment binary (`CUO_EXP`) ran 613842720's chunk loop and AddTileToRenderList (and the helpers in that file region) inside the tip: world prepare 3.32 vs the tip's own walk 4.20 (613842720: 3.24), so the regression was the walk's code, not the data, the callees or the object layout. Swapping halves: the tip's chunk loop with the old walk 3.61, the old loop with the tip's walk 4.12, so about 0.07 ms is the per-tile W4 branches and the rest the walk method. Disassembly (dbghelp + capstone on cuo.pdb): the tip's AddTileToRenderList zeroed 432 bytes of stack on every call (613842720: 112) and kept `profile`, `mesh` and its flags on the stack (273 stack references vs 122), because of the `PrepCheck.Sample ??= $"..."` string formatting in its body (a DefaultInterpolatedStringHandler and GC temporaries in the frame) and the W1 parameters live through the loop. It runs ~92,000 times a frame here.

**Fix (8b156011c):** the CUO_PREP_CHECK kept-position check moves to a NoInlining helper (`PrepCheckKeptPosition`); behaviour unchanged. The prologue's zeroing drops to 112 bytes.

| NativeAOT, 3 alternated rounds | 613842720 | base bbd86eb37 | fix 8b156011c |
|---|---|---|---|
| Legacy large: world prepare | 3.29 | 4.35 | 3.96 |
| Legacy large: world draw | 4.01 | 4.26 | 4.27 |
| Legacy large: frame | 7.84 | 9.19 | 8.77 |
| Legacy Britain 1080p: world prepare | 0.117 | 0.154 | 0.138 |
| Gpu large: world prepare / prep_walk | | 0.721 / 0.327 | 0.718 / 0.323 |
| Gpu large: frame | | 1.322 | 1.327 |
| Gpu Britain 1080p: world prepare | | 0.051 | 0.052 |

An earlier series on eaa489c92 (same change): 4.27 -> 3.88.

**Tried, not kept:**
- A second compiled shape of the walk for whole tiles (generic over a struct with a static abstract `Partial`, folding skipBaked/forcePosition/stopAt away; `profile` and `mesh` back in registers): another -0.03 ms median (-0.09 min window), within noise.
- Entity-only state off GameObject (the W5 text cache, InTextOwners, the DPS average) and a byte-sized ObjectHandlesStatus, so Land/Static hot fields sit where they did in 613842720: -0.05 ms, noise level.
- `CUO_CHUNK_SYNC=1` (no incremental chunk loads, objects allocated in walk order): -0.06 ms.

**Ruled out:** object layout and size (above), allocation order (above), picking/PixelPicker and Art trim bounds (same callees and data in the experiment), CalculateAlpha's snap, the item handle queue (not reached at this spot), FrameStats (stats-off frames show the same gap). A wall-clock suspend/stack-walk sampler (Python, dbghelp) was too intrusive to use (it doubled world draw); the experiment binary and the disassembly did the work.

**NativeAOT bisect (stats on, 2 rounds, world prepare):** W0/W1 755c9599e 4.08, df2fbf8c8 4.45, a6110f967 4.50, e9f2505c2 4.42, 28bb72308 4.07-4.21, cacc27d44 4.27, 08b952f3a 4.20, a122f2cc1 4.36, 786d86c9c 4.66, 47cb613c9 4.70. No single step: small cuts that grew the walk's frame. World draw rose 0.6 ms at e9f2505c2 (the B1 rebind fix, draw side) and fell again by 786d86c9c.

**Verification:** Release and `-p:CuoDevTools=false` build; 442 unit tests pass. Shots (NativeAOT, base vs fix): Legacy house shots near-black 1.89% (all three), Legacy world diffs only on the walking NPCs in the house and two mobiles at Britain; Gpu Britain identical, Gpu house diff only on the walking NPCs; UI diffs only in journal text. Looked at. Picking: the walk's pick path is unchanged code (no pickscan run).

**Left:** about 0.65 ms (+20%) of Legacy world prepare at the large spot is still above 613842720. The whole-tile shape and the per-tile W4 branches account for ~0.1 ms between them; the rest is spread over the walk's other additions (detail-mode branches and their bucket locals, `_walkObjects`, ScreenPositionGen). A Legacy-only copy of the 613842720 walk gets it all back but duplicates ~450 lines; not done. World draw is +0.25 ms over 613842720 (draw side, unrelated to this change). Re-measure every table on a quiet machine.

## E5: cheaper mobile emission replay (2026-10-26)

Branch `bg/ecs-e5-replay`, from poc/modern-client 755f4e119 (docs-only commits landed since). Draft quality, needs an audit before merge. JIT with `DOTNET_TieredCompilation=0`, kbatman3, Gpu, large scene, 1920x1080. Logs, EventPipe traces and tools: session scratchpad `e5\`.

**Profile (EventPipe, push on, base, inclusive share of `TryDrawMobileEcs`):** `MobileAnimationSystem.Update` 28.6% (`SelectCached` 5, `SamplePushed` 3.8, `Select` misses 3.4, seat test 3), cached `Emit` 26.8% (`Replay` 10.1), the serial dictionary lookup (`Mirror.TryGetEntity`) 15.1%, `Entity.GetComponent` calls 13.7% (about 15 per mobile; its `StructHeap<T>` casts are the `ChkCast*` 7%), full `Emit` on emission misses 6.3% + `Layer` 6%. Inlining hides the rest. A miss is about 50x a hit, and the standing scene misses about 2% of frames (a frame change every ~100 ms at ~500 fps), so misses cost about a tenth of the per-mobile time.

**What changed:**
1. **`Mobile.EcsEntity`/`EcsEntitySerial`:** the mirror entity found last frame is reused while `!IsNull` (the revision check catches deleted/recreated entities; a re-keyed mobile's old entity is removed, so it fails the same way) and the serial matches. Skips the dictionary lookup. Only entities with an `AnimationState` are cached.
2. **In-place update:** `TakeDirty` (removes `EquipmentDirty` first, since the tag move invalidates component refs) then `UpdateInPlace(..., ref AnimationState, ref MobileRenderState)`; the extract takes those refs (`Emit(e, in s, in rs, ...)`). Removes the two struct copies, two write-backs, the second `HasComponent`, the duplicate `GetComponent` calls in the renderer and `Emit` (about 15 lookups per mobile down to 8). `Update(world, m, e)` is a wrapper with the same behaviour; `EcsParity` still calls the old `Emit(e, x, y, depth, cache)`, which is a wrapper too.
3. **`MobileFrameCache.Abs`:** the last replay kept in absolute coordinates, tagged with (dx, dy, base depth, tile x). Same tag: no work. Same depth and tile x with other dx/dy (camera scroll, walking offset): X and Y shifted by the difference (integer adds, bit-exact). Otherwise rebuilt from the relative entries with the same depth-key expressions. The extract exposes `Abs` as a view (`Instances`) when nothing else was emitted since `Begin`, so the renderer's copy into the dynamic buffer is the only copy (before: entries -> `_out` -> dynamic).

**Invariants relied on:** the entity cache is valid iff alive and the serial matches; no code in `UpdateInPlace` changes the entity's archetype (Rebuild writes `EquipmentSlots` only); `Abs` is valid only for the entries it was built from (reset on every record and `Invalidate`); the view is read before the next `Begin`/`Emit` (the renderer copies it immediately); a second emit after a view first moves the view into `_out`.

**Tried and dropped:** a per-mobile memo of the last 12 emissions keyed by the AnimationState fields the extract reads, equipment version/slot groups, alpha, hues and pack (to turn the periodic frame-change misses into swaps). No measurable gain over the above (z1 0.295 vs 0.289 ms), more state to audit. Reverted.

**Result (paired, interleaved base/new, 3 rounds x 3 windows, `prep_cap_mobile` = mobile select + extract, ms; per mobile in brackets):**

| Spot | base | new | change |
|---|---|---|---|
| zoom 1 (1,611 mobiles) | 0.335 (208 ns) | 0.276 (172 ns) | -0.059 ms |
| zoom 2.5 (1,985) | 0.413 (208 ns) | 0.365 (184 ns) | -0.048 ms |
| walking the lane (~1,310) | 0.316 (242 ns) | 0.284 (217 ns) | -0.032 ms |

Two earlier paired series (2 and 3 rounds, same code or the dropped memo) gave -0.046/-0.040 (z1), -0.033/-0.008 (zo), -0.042/-0.035 (walking). So the gain is 0.03-0.06 ms per frame, right at the 0.05 ms bar. World prepare moved 1.29 -> 1.19 ms at z1 (within noise of the machine, which moved the same configuration by ±0.03 ms between runs).

**Checks (all 0):** `CUO_ECS_PARITY=1 CUO_ANIM_SYNC=1 CUO_GPU_ITEM_CHECK=1` + input/cache/w5 checks, large scene standing and walking (`ls_parity.txt`): 10.6M mobile comparisons, 0 mismatches, frameinfo/lights 0; mobile cache check 13.1M replays 0 different (without parity, so the real in-place path runs; parity mode uses the wrapper path); input check 13.8M samples 0 different; item check 0 differences; `-ecscheck` 0. `w5push.txt` (mount, dismount, seat, hidden, hue, death): parity 3.1M, 0 mismatches; non-parity run: 2.2M replays 0 different, 2.1M input samples 0. Unit tests 465 pass.

**Left / ideas:** the remaining per-mobile cost is the 7-8 component lookups (Friflo `GetComponent` casts), `SamplePushed`/`SelectOverrideHue` reading many Mobile fields, `SelectCached`'s 15-field key, and the 480 B per mobile copy into the dynamic buffer. A real step down needs a chunk-iterating system over mobile archetypes (E4's territory), not more micro-work here. NativeAOT not measured.

**E5 audit fixes (Codex, 2026-10-26), after merging poc/modern-client (E9):**
- **P2, cached entity revision rollover:** Friflo's revision is a short, so `IsNull` can pass for a stale handle after 65,536 deletions of one id. The cached handle is now tagged with `WorldMirror.Epoch`, bumped on every entity deletion (`Remove`, `Clear`); a mobile uses its handle only while the epoch and its serial are unchanged, else it repeats the dictionary lookup. Cost: two integer compares per mobile; every deletion makes all mobiles repeat one lookup that frame (deletions are per packet, not per frame). `IsNull` is no longer relied on.
- **P3, double replay of one cache in a Begin:** `Replay` now moves a pending view into `_out` before mutating `Abs`, and `Emit` does so before taking the checker's start index. New `MobileReplayTests` (3): same-tag no-op and dx/dy shift equal to a fresh rebuild, depth and tile-x changes rebuild, same cache replayed twice keeps the first emission.
- **Checks on the merged tree:** 544 unit tests pass (one unrelated ChunkLoading test failed once under load and passed on re-run). Non-parity large-scene run (standing, walking): cache check 12.7M replays 0 different, input check 13.4M samples 0 different, seat tests 12.1M 0 different.
- **Timing, zoom 1, 3 interleaved rounds, `prep_cap_mobile`:** base 0.345 ms (214 ns/mobile; one noisy run at 0.410) -> 0.243 ms (151 ns). The base binary is 755f4e119 (without the E9 seams, which don't touch the draw path), the new one is the merged tree.

## 2026-10-26 - E9: test seams for what E1 couldn't reach

Branch `bg/ecs-e9-seams`, started on `poc/modern-client` 755f4e119. Seams only in the product (no behaviour change), plus tests. Release and `-p:CuoDevTools=false` build; 541 unit tests pass.
- **`MobileAnimationSystem.Rebuild`:** the public `Rebuild(mobile, in s, owner)` now reads the tiledata array, the equipconv table and the 7.0.0.0 gate and calls an internal overload that takes them. Rebuild-time only, never per frame. Tests (`MobileRebuildTests`): empty set and versions, a slot's fields, partial hue and light bits, items without an AnimID (dropped, or lit if a light), the light-only cap, non-human bodies (lights only) and the Human flag, dead hair/beard and ghost bodies, covered shoes, hands, equipconv remap, gargoyle remap by client version, cloak anchor, the mirror's mount item, tiller and bodyless mount, `owner == null` fallback vs the Mobile's own list (both directions), and `IsMounted(Mobile, StaticTiles[])`.
- **`AnimationGroupRules.Select`:** now generic over `IAnimationSource` (struct type argument, so the JIT specialises it; `AnimationsSource` wraps the real `Animations` and the old signature forwards to it, no interface dispatch). The interface has the five table queries plus `IsReplacedObjectAnimation` (was a static call into `Mobile`, which reads the loaded anim1/anim2 tables). Tests (`AnimationGroupSelectTests`): humans, equipment and uop equipment idle/walk/run, war mode, hands, mounted, light in the off hand, gargoyle flight, steps, animals, sea monsters, monsters and their flags, body conversion, server actions and random rolls.
- **`ItemSpriteExtract` hue choice:** pulled out of `Emit` into `ChooseHue(in HueInput, out graphic, out hue, out partial)`. `Emit` fills the input (a few more field reads per item than before, all side-effect free; the selected-container and out-of-range tests now run for every item, not only when reached). Tests (`ItemHueTests`): field replacement, selected container, and the precedence highlight > out of range > dead gray > mouse selection > hidden, including the out-of-range tint.
- Found no product bug. Noted, not changed: `Rebuild` indexes `tiles[gfx[...]]` for hands and drawn layers without the `< tiles.Length` guard the mount lines use (fine with a real tiledata, which is sized for 16-bit graphics).

## ECS mirror cost under packet storms (E2) and the ECS inspector (E3) (2026-10-26)

Branch `bg/ecs-e2-e3` from `poc/modern-client` 755f4e119. E2 e1a1511d2, E3 c4870e7a4.

**E2 method:** `EcsPacketCostBench` (xunit, off unless `CUO_ECS_BENCH` is set; no new dependency, no BenchmarkDotNet in the repo). A legacy `World` with N mobiles each wearing 5 items plus a backpack; each scenario calls the hook the packet handler reaches, 100,000 packets after 20,000 warmup, `GC.GetAllocatedBytesForCurrentThread` around the loop. `DOTNET_TieredCompilation=0`, workstation GC, Release. Three runs, N = 500 / 2,000 / 8,000; run-to-run spread was about 5% (noisy machine, so read it as +-10%).

| Packet (hook) | us/packet at 2,000 mobiles | bytes/packet |
|---|---|---|
| Mobile move (x, y, dir + `Sync`) | 0.12 | 0 |
| `Sync`, nothing changed (mobile / worn item) | 0.06 / 0.05 | 0 |
| Mobile graphic change + `Sync` | 0.06 | 0 |
| `PushInputs` | 0.025 | 0 |
| Worn item hue change + `Sync` | 0.05 | 0 |
| Worn item to backpack and back (indexed component re-add) | 0.16 | 0 |
| Ground item add + delete (with the legacy object), per packet | 0.13 | 132 |
| Item into a container + out, per packet | 0.17 | 132 |
| Serial reuse (delete mobile, create the same serial), per packet | 0.22 | 208 |
| Mirror only: `UpsertItem` + `Remove`, per packet | 0.06 | 0 |
| Mirror only: `UpsertMobile` + `Remove`, per packet | 0.10-0.12 | 0 |

**Result: no problem found.** Every hook is 0.02-0.26 us, so a 2,000-packet storm costs under 0.5 ms; the cost does not grow with the entity count (500 -> 8,000 mobiles: 0.12 -> 0.12-0.13 us for a move, 0.22 -> 0.26 for serial reuse). The mirror allocates nothing per packet, creates and deletes included; the 132 and 208 bytes are the legacy `Item`/`Mobile` objects the scenario also creates. Caveats: the dirty tag is added once and stays (the bench never runs the animation system that clears it), so "marks equipment dirty" is the already-dirty path; the loop is single-threaded and cache-warm (8,000 mobiles = 40,000 items is the largest tried).

**E3:** DevBench plan entry `ecsdump:<hex serial>|player|nearest` (`Game/Ecs/EcsInspector.cs`, whole file behind `CUO_DEVTOOLS`). Smoke run on kbatman3 (account lock held, password in a throwaway encrypted settings.json, deleted after; Britain, Gpu, 960x720): `[ecsdump]` lines for the player (entity 1, two worn items, pushed inputs equal to the recomputed ones, slot 0 `item=0x401F0BEB layer=22 g=0x204F anim=0x03DB ... | legacy g=0x204F hue=0x054F layer=22 container=0x0004C222`) and for the nearest mobile (animation not yet built, `EquipmentDirty`, 7 indexed equipment items matching the legacy list). Format and use: [testing/ecs-animation.md](testing/ecs-animation.md).

**Verification:** `tests/ClassicUO.UnitTests` Release 466 passed (the bench is one of them and returns at once without the variable); client builds in Release and with `-p:CuoDevTools=false`. Submodules had to be initialised (`git submodule update --init --recursive`) in the fresh worktree.

## E4: World.Update's mobile loop as a due-time system, and the animation clock in the ECS (2026-10-26)

Branch `bg/ecs-e4-update`, from poc/modern-client 540157a5a. Draft quality, needs an audit before merge. Setting `world_update_mobile_system` (default **off**; also needs `world_update_active_items`, see invariants). JIT with `DOTNET_TieredCompilation=0`, kbatman3 (account lock), Gpu, 1920x1080, large scene. Logs and tools: session scratchpad `e4\`.

**1. What the loop does per mobile today** (`World.Update` -> `Mobile.Update`, every frame, every mobile, in dictionary order):

| Step | When it has an effect | Class |
|---|---|---|
| `Entity.Update`: `ObjectHandlesStatus == OPEN` -> DISPLAYING, name request, name gump | once, after `GameScene` sets OPEN | event (a flag) |
| `Entity.Update`: hits percentage | only with `world_update_active_items` off (the setters do it otherwise) | none when on |
| idle timer: `_lastAnimationIdleDelay < Ticks` -> `SetIdleAnimation` (random fidget action, re-arms 30-60 s) | when the timer expires | **due time** |
| `ProcessAnimation(true)` -> `ProcessSteps`: step interpolation, step completion, position, text coords, player walker bookkeeping | every frame while `Steps.Count != 0` | **active (every frame)** |
| `ProcessFootstepsSound` | every frame while it has steps | active |
| frame advance: `LastAnimationChangeTime >= Ticks` returns; else `GetGroupForAnimation`, frame lookup, `AnimationTiming.Advance`, `LastAnimationChangeTime = Ticks + delay` (80 ms plus the server interval), finish/wrap handling, dying-mobile corpse removal | when the frame timer expires (about every 83 ms for a standing mobile) | **due time** |
| gates inside it: `ExecuteAnimation` false (server froze the frame), `LastStepTime > Ticks - 150` with no steps | evaluated by Update itself; a mobile with `ExecuteAnimation == false` stays "due" every frame and does a cheap no-op call | - |
| `do_delete` (every 50 ms): `Distance > ClientViewRange` -> `RemoveMobile` | every 50 ms, needs the player position | periodic sweep |
| `IsDestroyed` -> queue for removal from `Mobiles` | after a destroy | event |
| world map pass (`wmap`): allies and party members -> `WMapManager.AddOrUpdate` | only while the map accepts updates (open, recent packet) | rare |

So in the scene (1,985 mobiles, all standing) the work is two timers per mobile and nothing else; `Update` was a no-op for about 96% of the calls.

**2. What changed** (all behind the setting):
- **`MobileClock`** (new component on every mobile entity, `Components.cs`): `AnimDue` (= `LastAnimationChangeTime`), `IdleDue` (= `_lastAnimationIdleDelay`), `Flags` (`Stepping` = `Steps.Count != 0`, `HandlesOpening` = `ObjectHandlesStatus == OPEN`). `MobileClockRules.IsDue(clock, now)` = `Flags != 0 || AnimDue < now || IdleDue < now`, the same comparisons `Update` makes (strict `<`).
- **`MobileClockSystem`** (`ClassicUO.Ecs`): iterates the mobile archetypes (`Serial` + `MobileClock` spans), appends the due serials to a list. Read-only: the updates run afterwards because `Update` can delete entities (corpse removal) and moves entities between archetypes (dirty tags).
- **`World.UpdateMobilesByClock`**: collect -> for each due serial `Mobiles.Get` -> the legacy `mob.Update()` -> `PushClock`. The rest of the old loop body is split out: the range removal is its own pass every 50 ms, destroyed mobiles come from `World.OnMobileDestroyed` (called by `Entity.Destroy`, removed from `Mobiles` only if the dictionary still holds that object), and the world-map block is a separate loop that runs only when `wmap` is true.
- **Who writes the clock** (everything else is read from the Mobile): `CalculateRandomIdleTime` (called by `SetAnimation`, `SetIdleAnimation`, step completion; `SetAnimation` writes `LastAnimationChangeTime` just before it, so one push carries both), `WriteInputs` (every step-queue change and entity creation, via the W5 `PushInputs`/`Sync` sites), `GameScene.UpdateObjectHandles` (NONE -> OPEN), and the system itself after each due `Update` (which rewrote the timers, may have emptied the queue or consumed the OPEN). The push goes through `WorldEcs.PushClock`, which ignores an object World no longer holds, like `PushInputs`.
- **The animation clock in the ECS: what moved and what did not.** The two due times are ECS state now and the schedule is read only from there. The per-action timer (interval, repeat mode and count, forward, from-server, group, frame count) stays in the Mobile: only `SetAnimation`/`SetIdleAnimation`/`ProcessAnimation` touch it, and the advance needs `frames.Length`, which comes from `GetGroupForAnimation` (equipment, war mode, mounts, random fidgets) and the animation tables, i.e. the whole legacy selection. Moving the advance itself into the ECS needs the selection to run against ECS state (the W3 `AnimationState` is only sampled for drawn mobiles) and is the next step, not part of this prototype. Mirror write-backs: none from the ECS to the Mobile; the Mobile is authoritative and every field the system reads is pushed from it.
- Also: `MobileClock` is added with `AddComponent` after `CreateEntity` (Friflo's `CreateEntity` takes at most 11 arguments) and registered in `EcsSchema` (without that registration the client throws "IComponent type not found" at startup although the unit tests pass). Plan entry `mobsys:on|off`, driver command `mobsys on|off`. `Mobile.Create` uses the constructor without timers, so a new mobile's clock is (0, 0) = due at once; its first `Update` arms both timers (the same as the legacy loop).

**Invariants (for the audit):**
1. A mobile that is not due has `Update` == no-op. Holds because every `Update` effect is gated by `Steps.Count != 0`, `OPEN`, `LastAnimationChangeTime < Ticks` or `_lastAnimationIdleDelay < Ticks`, and `Time.Ticks` is constant within a frame.
2. The clock equals the Mobile's values at the start of every frame. Every write of `LastAnimationChangeTime` (other than inside `Update`), `_lastAnimationIdleDelay` and the step queue is followed by a push; `Entity.Destroy` writes `LastAnimationChangeTime = 0` but removes the entity. A new write site without a push makes a mobile late or never due (the checker finds it). `ExecuteAnimation` is deliberately not in the clock (a public field written from two packet handlers).
3. Mobiles without a mirror entity are never updated. `Sync` creates the entity on `GetOrCreateMobile`; the checker verifies every live Mobile has one.
4. The system needs `world_update_active_items` (hits percentage in the setters, no per-frame refresh); otherwise `Entity.Update` is not a no-op for a quiet mobile and the system is not used. Turning the setting on mid-session first resyncs all clocks (the legacy loop advanced the timers without pushing them).
5. Order: see the audit fixes below (the due mobiles run in `Mobiles.Values` order; the first version of this invariant claimed order was unobservable, which was wrong).
6. Dictionary iteration order is no longer the update order (the order of the world-map calls is, in the pass that remains, still the dictionary's).

**3. Checker `CUO_MOBILE_UPDATE_CHECK=1`** (`"stats"` only counts and times the parts): at the start of every frame each live Mobile's clock is compared with the Mobile's values (`AnimDue`, `IdleDue`, flags; missing entities count); after the due updates every **skipped** mobile gets the legacy `Update` anyway and its state hash (frame, timers, group, repeat state, position, offset, steps, last step times, handles status, hits percentage, destroyed) must not change. The due mobiles ran the legacy code themselves, so skipped-is-a-no-op plus clock-equals-Mobile is the whole equivalence; running the legacy loop beside the system would apply every `Update` twice (it mutates). Logged every 5 s as `[mobile-update-check]`. The world-map pass is the old code in a separate loop and is not compared (the map was not open).

| Run (checks on, TieredCompilation=0) | Result |
|---|---|
| Large scene `ls_parity.txt` (standing, then the lane walked east, west, running), `CUO_ECS_PARITY=1 CUO_ANIM_SYNC=1 CUO_GPU_ITEM_CHECK=1 CUO_W5_CHECK=1 CUO_MOBILE_INPUT_CHECK=1`, `-ecscheck 120` | clocks checked 24.3M, **0 different**; skipped updates checked 23.3M, **0 changed state**; 69.3 due of 1,784 per frame; parity 10.2M mobile comparisons 0 mismatches; input check 9.4M samples and 449k seat tests 0 different; item check 0; `[w5-check]` 0; `-ecscheck` 0 |
| `w5push.txt` (mount, dismount, seat, hidden, hue, death, walking NPCs; plus `CUO_MOBILE_CACHE_CHECK=1`) | clocks 4.8M 0 different, skipped 4.5M 0 changed; parity 3.3M comparisons 0 mismatches (Mounted 534k, Hidden 16k, Dead 30k, Running 31k, Walking 105k mobile-frames); input check 3.1M samples and 2.6M seat tests 0 different |
| `w5items.txt` (corpses, object handles on mobiles) | clocks 457k 0 different, skipped 425k 0 changed; handles: 4 mobiles reached DISPLAYING; parity 0 |
| Control, same scene with the setting **off** | The item parity line shows `lights-mismatched=5705` (4,324 with the system on). It is in the item path, with the setting off, and the E5 runs had no lights (`lights=0`); not caused by this work, but unexplained: worth a look |

Unit tests: 552 pass (`MobileClockTests`, 6: the due rule, the system's pick over mobiles/items/tag moves/removal, pushes from creation, `SetAnimation`, the step queue, handles, replaced objects, resync). One `ChunkLoadingTests` failure appeared on two consecutive full runs after a build and passed on the next; unrelated (timing under load). Release and `-p:CuoDevTools=false` build.

**4. Measurement** (`mobsys:off` / `mobsys:on` plan entries in one session, interleaved, 3 rounds x 2 windows at zoom 1, 2 rounds at zoom 2.5 and `lswalk`; `world_mobiles` is World.Update's mobile phase, ms; other agents' short runs may have overlapped):

| Spot | off | on | change |
|---|---|---|---|
| zoom 1 (1,985 mobiles) | 0.038 | 0.020 | -0.018 |
| zoom 2.5 | 0.041 | 0.025 | -0.016 |
| walking the lane | 0.042 | 0.021 | -0.021 |

`update` (the whole World.Update) 0.126 -> 0.105, 0.151 -> 0.142, 0.162 -> 0.143 ms; frame 1.79 -> 1.76 ms at zoom 1 (inside the noise: the same configuration moved 0.03 ms between windows). Honest summary: about **0.02 ms per frame (45-50% of the loop, about 1% of the frame)** in the busiest scene, as the earlier estimate predicted (0.03 ms at most). Where the remaining 0.02 ms goes (stats run): the scan of 1,954 clocks 0.005 ms (2.5 ns per mobile), the 43 due updates 0.014 ms (about 325 ns each: two dictionary lookups, the legacy `Update` with its frame advance, the clock push), the 50 ms range pass amortised. That floor is real work (the frame advance), so the next step is moving the advance, not scanning faster. Sparse scenes (Britain, the bank) were not measured: the loop is a few mobiles there. NativeAOT not measured. The setting-off path adds only the always-on pushes (a few integer writes at existing push sites, one more lookup in `SetAnimation`/step completion); no base-vs-new binary pair was run for that.

**Left / ideas:** (1) move the per-action timer into the ECS (`AnimationTimer` is already a plain struct, `AnimationTiming.Advance` pure) once the frame count can be had from ECS state, so a due frame advance for standing mobiles needs no Mobile and no dictionary; (2) `ExecuteAnimation` into the clock flags; (3) the range sweep as a chunk pass over `Position` (needs the player's position and the step-end rule of `Distance`); (4) the world-map pass from `MobileInputs.Notoriety` + the party list instead of the full loop. For a networking upgrade that adds server-driven animation: every new write of the two due times must push, and the checker will say if one does not.

**E4 audit fixes (Codex, 2026-10-26), after merging poc/modern-client (62ac4bdc6, the parity lights fix):**
- **P2, update order (my invariant 5 was wrong):** the due list followed archetype order, which equipment tags reshuffle, and tile lists (`AddToTile`), picking ties and footstep sounds depend on the legacy order. Now the due mobiles run in exactly `Mobiles.Values` order: `World.SortInMobilesOrder` gives every Mobile an `Ordinal` (its position in the enumeration) and sorts the (about 40) due mobiles by it. The ordinals are rebuilt with one walk only when `Dictionary._version` (read with `UnsafeAccessor`, works in NativeAOT) changed: every insert (add, overwrite, resize) bumps it and only an insert can change the relative order of entries (a remove leaves a hole, the next add reuses the slot), so a quiet scene never walks the dictionary (52 rebuilds in a 20k-frame login-plus-scene run, 19 in another). Tests: `Due_Mobiles_Run_In_The_Dictionarys_Order_Through_Tag_Moves_And_Slot_Reuse` (200 rounds of random removes, slot-reusing adds, dirty-tag moves and random clocks against the legacy filter of `Values`), `Two_Mobiles_Arriving_In_The_Same_Frame_Keep_The_Dictionarys_Order_After_A_Tag_Move` (the scan order demonstrably differs from the dictionary's). The tile list and sound effects themselves need the map and audio, so the order is tested, and the checker below runs it live.
- **Checker sees order:** `CheckOrder` (under `CUO_MOBILE_UPDATE_CHECK=1`) builds a fresh enumeration of `Mobiles.Values` each frame and requires the due list to be a subsequence of it (`due-update order checked N, M out of order`). It checks the order of the updates, which is what determines the tile/audio effects; a state hash cannot see reordering, so the equivalence argument is now: skipped mobiles are no-ops (hash), clocks equal the Mobiles (compare), due mobiles run the legacy code in the legacy order (this check).
- **Setting off costs nothing:** `WorldEcs.ClockEnabled` (set each frame by `World.Update` from the two settings) gates `PushClock`, `WriteClock` (no lookups), the destroyed-mobile queue, and the component itself: `MobileClock` is no longer added at entity creation but by `WriteClock`/`ResyncClocks` when the system first runs. With the setting off the code paths are the old ones plus one bool test per push site. Turning it on builds all clocks from the Mobiles (resync).
- **Handles wording:** the clock equals the Mobile except that `HandlesOpening` may stay set after the scene (NONE) or the name gump (CLOSED) cancelled an OPEN: that costs one extra `Update`, which clears it, never a missed update. The checker allows exactly that.

Checks on the merged tree (kbatman3, TieredCompilation=0, `CUO_ECS_PARITY=1 CUO_ANIM_SYNC=1 CUO_GPU_ITEM_CHECK=1 CUO_W5_CHECK=1 CUO_MOBILE_INPUT_CHECK=1 CUO_MOBILE_CACHE_CHECK=1 CUO_MOBILE_UPDATE_CHECK=1`, `-ecscheck 120`; driver `light 1E` for night, `light off` for day):

| Run | Result |
|---|---|
| Large scene, night (`lights=485k`, `lights-skipped-full=4095`), standing then the lane walked and run | clocks 19.7M 0 different; skipped 18.9M 0 changed; **due-update order 805k checked, 0 out of order**; parity 8.4M mobile comparisons 0 mismatches, items 10.8M 0 (lights-mismatched 0); input check 7.7M 0; cache and item checks 0; `-ecscheck` 0 |
| Large scene, day (`light off`) | clocks 22.4M 0; skipped 21.4M 0; order 941k 0 out of order; parity 9.4M 0 mismatches; input check 8.7M 0 |
| `w5push.txt` (mount, dismount, seat, hidden, hue, death, wandering NPCs) | clocks 4.5M 0; skipped 4.2M 0; order 311k 0 out of order; parity 2.5M 0 mismatches; input check 2.5M and 2.1M seat tests 0 |

Unit tests: 560 pass. **Timing** (`DOTNET_TieredCompilation=0`, binaries from the merged tip: `bin-new3` with E4 and `bin-base` = poc/modern-client 62ac4bdc6, interleaved runs; `world_mobiles`, ms; rounds 0 and 2 were quiet, round 1 was disturbed by another process and doubled everything, so I use rounds 0 and 2):

| Spot | base | E4 off | E4 on | on vs off |
|---|---|---|---|---|
| zoom 1 | 0.045 / 0.052 | 0.044 / 0.048 | 0.025 / 0.028 | -0.019 / -0.020 |
| zoom 2.5 | 0.045 / 0.056 | 0.043 / 0.056 | 0.030 / 0.035 | -0.013 / -0.021 |
| walking | 0.039 (0.076 in the noisy round) / 0.052 | 0.041 / 0.054 | 0.025 / 0.032 | -0.016 / -0.022 |

Off equals base within the noise (the same configuration moved by 0.01 ms between rounds); on saves 0.013-0.022 ms, with the ordering sort and the occasional ordinal rebuild included (the order fix cost about 0.005 ms of the earlier 0.02 saving at zoom 1: 0.020 -> 0.025). The first batch of retries after the internet outage failed at startup with "BackBufferWidth must be greater than zero" (the display, not the code) and was redone.

### Parity `lights-mismatched` at night: a checker artifact (2026-10-26, branch `fix/ecs-lights-parity`, base 540157a5a)

**Question:** E4 saw `lights-mismatched` 4,324 / 5,705 (item path, `view=1 ecs=0`) in the large-scale parity run; E5 earlier saw `lights=0`.

**Cause (not a regression):**
- Same plan in both runs (`ls_parity.txt` identical). What differed was the time of day: `GameScene.UseLights` is `RealPersonal < RealOverall` (server light cycle), and `AddLight` returns at once when it is false. E5 ran by day, so `lights=0` meant no lights were ever exercised. E4 ran at dusk; my daytime run at 11:05 PDT had `lights=651`.
- `EcsParity` adds the ECS light after the view's, so the list holds both. `AddLight` stops at `LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT` (100): when the view's light was the 100th, the ECS add is refused (`view=1 ecs=0`). The logged items were just the last lights drawn each frame (candles 0x0A0F, 0x19AB lamps), at all alphas. The mobile-effects check had the same flaw (no hit here: mobiles' lights were 0 in this scene).

**Evidence:** the driver's `light 1E` / `light off` (custom light level) makes it deterministic. Same plan, same scene, the forced-night run on 540157a5a: `lights-mismatched=7511` of `lights=932080`; on 755f4e119 (E5's base): 7407 of 926138, so no regression and no bisect needed (E9/E2/E3/E5 did not change it). Forced day: `lights=0`, 0 mismatched.

**Fix:** `EcsParity.LightsFit(lightsBefore, viewEnd)` skips the light comparison when the doubled list would not fit under the cap (counted as `lights-skipped-full` in the summary, so the skip is visible). Forced night on the fixed build: `lights-mismatched=0`, `lights=931347`, `skipped-full=7531` (matches the unfixed 7511 within run noise), items/mobiles/effects mismatches 0. 5 unit tests in `ParityLightsFitTests`; 550 tests pass, default and `-p:CuoDevTools=false`.

**Note for future parity runs:** run both `light off` and `light 1E` at the start of the driver plan; a day-only run proves nothing about lights. Not covered: a real difference in light lists in the skipped (full-list) frames, which the checker cannot see.

## Teleport frames: first-sight chunk art decoded on workers (2026-10-26)

Branch `poc/teleport-art-worker`, from poc/modern-client 755f4e119, merged up to b59b602b3. Follows "Teleport frame: region prefetch and incremental chunk loads": a chunk whose mesh build hit many first-sight statics could take 6–10 ms (`chunk_mesh_us`), and the at-least-one-chunk rule let it through. Method: [testing/pack-loading.md](testing/pack-loading.md) §3 and §4. Scripts and logs: session scratchpad `tpart\`.

**Changes.**
- **`FirstSightDecoder`** (ClassicUO.Renderer) does what the animation path does for first-sight bodies, for single sprites:
  - `Request` (main thread) queues a decode once per sprite.
  - A thread-pool worker decodes into a pooled buffer and computes the visible bounds.
  - `Drain` (main thread) uploads finished decodes (atlas, picker mask, bounds) until a deadline, at least one per call.
- **`Art` and `Texmap`** have `RequestLand`, `RequestArt` and `Request`, plus `DrainReady`.
  - A synchronous lookup of a sprite whose decode is in flight still decodes on the spot, and the worker's result is dropped.
  - A failed worker decode is redone on the main thread.
- **`Map`:**
  - A queued chunk loads only when the land art, texmaps and static art its map block names (with the current season's graphics) are in the atlases. The check reads the block data; the first check requests all missing art, later checks stop at the first missing sprite.
  - `UpdateLoading` uploads finished art inside the same 2 ms chunk budget, before the chunk loads. The mesh build then reads no art for the first time.
  - A chunk that game logic loads before its art is ready (`GetChunk` with load = true, pathfinding) is marked `ArtPending`. The draw walk skips it and counts it in `DrawPending`, so the world-settle hold waits for it; `UpdateLoading` builds its mesh once its art is there.
- `CUO_ART_SYNC=1` restores the old path.
- **FrameStats counters:** `art_sync`, `art_sync_us`, `art_async`, `art_upload_us`, `art_pending`.
- **Thread safety:** the worker touches only:
  - the loaders' sprite tables (`ArtLoader._sprites`, `TexmapsLoader._sprites`), assigned once in `Load` and never changed;
  - the pack pages (`PackPages`, thread-safe; its 8-thread test exists);
  - `PackSprites`, `PixelExpand` and `PixelPicker.VisibleBounds`, which are pure;
  - `ArrayPool` and its own job object, handed back through a `ConcurrentQueue`.

  The sprite infos, the atlases, the picker, the bounds, the request flags and the job pool stay on the main thread. `Art.Reload` (options) while a decode is in flight only re-uploads the same image.

**Measurements.** NativeAOT plus `DOTNET_TieredCompilation=0`, Gpu world, 1920×1080, plan `travel;ltravel;glide`, 4 windows of 5 s per entry. Base and new were built from the same tip and alternated. Runs A–C were on 2faa743df (base 540157a5a) and D on a5c4bec3c (base b59b602b3). No other clients were running, except one during the Legacy base run. Update+Draw is summed over windows:

| Entry | Base U+D max (ms) | Base >8 / >16 | Base `chunk_mesh_us` max | New U+D max (ms) | New >8 / >16 | New `chunk_mesh_us` max |
|---|---|---|---|---|---|---|
| `travel` | 8.4–10.7 (one 42 ms `lights` outlier) | 1–3 / 0 | 6.7–7.0 ms | 8.0–9.0 | 1 / 0 | 0.65–0.77 ms |
| `ltravel` | 3.8–5.0 | 0 / 0 | 0.8–3.7 ms | 4.4–6.0 | 0 / 0 | 0.78–0.87 ms |
| `glide` | 10.8–12.5 | 1 / 0 | 9.8–11.5 ms | 3.7–4.5 | 0 / 0 | 0.30–2.8 ms |
| Legacy `glide` (one pair) | 10.6 | 1 / 0 | 9.8 ms | 4.0 | 0 / 0 | 0.40 ms |

- **Art moved off the main thread.** Meshes built by the loader spent 0 µs on art in a diagnostic build (`CUO_CHUNK_DIAG`, not committed). Worker uploads cost up to 1.3–2.0 ms in a frame (`art_upload_us`), inside the budget; up to 353 decodes are pending after a teleport.
- **What's left in the travel frames: GC pauses inside a chunk load.** `chunk_load_us` reaches 3–7 ms in both builds. Over the four series, every window with a load over 2.5 ms had a gen0/gen1 collection (15 of 15), and no window without one. A diagnostic put one chunk's `Chunk.Load` at 3.5–5.6 ms with no region wait or sync decode, and 0 parked objects. perf-opportunities.md lists the fix (pool `Land`/`Static`).
- **Server packet burst:** `packets` peaked at 1.1–1.3 ms per frame in these runs (once 4.2 ms in the 13-minute session), not the 8 ms seen on 2026-10-25. Out of scope; not map loading.
- **First-sight art not from chunk loads** still decodes in the draw walk: 11–25 decodes, 2.6–3.0 ms in one `travel` window per run (objects that aren't map statics).
- **Settle:**
  - **Holds:** every run had 0 timeouts and 0 unsettled frames in an episode. `travel` holds were 284–285 ms in total against 397–420 ms for base, including the login.
  - **Walking:** unsettled frames while walking stayed similar in `travel` (12–37 against 7–26). In `glide` they rose from 95–102 to 136–175 per run. All the added frames are "client chunks queued in view" (338 against 198 over three runs): a new column's chunk now waits for its art for a few frames at about 1,500 fps. In the Gpu world that only delays ground items and mobiles on those chunks; Legacy has no hold.

**Checks.**
- **Unit tests:** 564 pass on the final merge (560 on the tip, plus 4 new `FirstSightDecoderTests`):
  - each request decodes once, and uploads happen only in `Drain`, on the calling thread;
  - an expired deadline uploads exactly one;
  - a failed decode reaches the upload callback without pixels;
  - 1,500 pack art sprites decoded by the workers against a 3 MiB page cache match a sequential decode, bounds included.

  The `-p:CuoDevTools=false` build passes the same tests.
- **Legacy** (`-worldrenderer legacy`, base vs new): Britain shot 0.02% of pixels differ (>8), 2.2% near-black in both. Looked at: complete town, no missing tiles.
- **Picking:** `pickscan:8x6` at Britain, the bank and Moonglow on Felucca: 48 same, 0 different, in base and new.
- **Facet change** (`[self set map felucca` / `trammel`, kbatman4): `[settle] Facet` ready after 60–61 ms, 0 unsettled, 0 timeouts (base: 55–62 ms).
  - The same plan's walking-unsettled frames (740 base, 763 new) are Uo.Render chunks missing near the view during the pickscans, in both builds.
- **13-minute session** (new, `travel`, `glide`, `ltravel` and `bwalk` ×7): 178 settle episodes, 0 timeouts, 0 exceptions, 23 `bwalk` legs with 0 server denies.
  - Private bytes: 2,117 MB at 1 min, 2,134 MB at 3 min, then 2,134–2,139 MB to the end (flat).
- **Not run:** ECS parity with `light 1E` / `light off` (this change doesn't touch the ECS or lights); 4K; the owner's FPS test.

**Codex audit fixes** (0ec275e03; the audit found no race in the normal hand-off).
1. **Failed speculative decodes are terminal** (`FirstSightDecoder.IsFailed`).
   - The block scan asks for texmaps of flat land that the old path never decodes. A worker failure used to be retried synchronously in the upload, without a catch, so a missing or corrupt page could throw out of `UpdateLoading`.
   - Now readiness accepts a failed sprite and nothing retries it. A real lookup decodes it, or fails, exactly as before.
2. **The art readiness work is budgeted.**
   - A full block scan returns `ChunkLoadResult.Scanned`, which counts against the 2 ms budget like a load. A cold teleport's first scans therefore spread over frames.
   - Later checks resume at the block's first missing sprite (`ArtWaitTracker`), so they are O(1).
   - Loaded `ArtPending` chunks walk their tiles again only when art was uploaded since the last check.
3. **Generations.** Scan positions are keyed by `Art.Generation` (bumped by `Art.Reload`) and the season. A season change or atlas reload makes the next check a full scan that requests everything at once, instead of one sprite per round.
4. **Logout.** `GameScene.Unload` cancels pending decodes: finished buffers are returned at once, and workers still running return theirs when they finish (an epoch check).

Unit tests: 567 pass (both build variants) on b59b602b3. There are 7 new tests in total, covering:
- a failed decode never uploads or throws, isn't requested again and counts as ready;
- cancel drops finished and running decodes without uploading;
- full scans count against the budget and cheap checks don't;
- the tracker resumes, and rescans on a new generation.

**Re-measured on b59b602b3** (NativeAOT, TC=0, alternated base/new pairs E and F; one other client during baseE):

| Entry | Base U+D max | Base >8 | Base mesh max | New U+D max | New >8 | New mesh max |
|---|---|---|---|---|---|---|
| `travel` | 8.4–11.4 ms | 1–3 | 6.9 ms | 6.8–7.8 ms | 0 | 0.71–0.75 ms |
| `ltravel` | 4.8–5.1 ms | 0 | 0.8–1.0 ms | 5.2–5.8 ms | 0 | 0.80–0.83 ms |
| `glide` | 6.6–10.6 ms | 0–1 | 5.8–9.7 ms | 3.9–4.5 ms | 0 | 0.36–0.46 ms |

- The budgeted scans didn't cost the gain. The cold-teleport frame also improved: `travel` had no frame over 8 ms in either new run, and its worst frames are the GC-in-load ones (`scene_rest` 6.4–7.3 ms).
- **Settle:** 0 timeouts and 0 unsettled frames in any episode. Walking-unsettled in `glide`: 132–140 new against 77–92 base (art latency, as above).
- **Season change** (DevBench `season:winter`, then `ltravel`, `glide`, Britain, `season:summer`, `ltravel`, Britain): 20 episodes, 0 timeouts, 0 unsettled in an episode. After the change `art_pending` peaked at 74 and `art_upload_us` at 268 µs. Looked at the winter shot: snow land, bare trees, complete.
- **Facet change:** ready after 53–56 ms, 0 unsettled; `pickscan:8x6` 0 different at Britain and on Felucca.
- **Legacy** (`travel`, `glide`): glide U+D max 4.9 ms; the shot after the glide is complete (looked at).
- **Final tip** (5e3c58ff7, poc/modern-client ad378c0bc; 570 tests pass in both build variants). One more pair, baseG against newG:
  - `travel` 9.4 → 9.0 ms. New's one frame over 8 ms is a 5.6 ms mesh build with 0 art decodes, in a window with a gen1 GC: the GC landed in a mesh build this time.
  - `glide` 11.3 → 3.3 ms.
  - 0 timeouts in both.
- One `season` run hung at "Connecting" before login (server side, before any game code ran). It was killed at the 420 s timeout and rerun.

## E7: the item cache serves mixed tiles; uncacheable items walked alone (2026-10-26)

Branch `poc/ecs-e7-walk` (E7 agent, kbatman3), started on `poc/modern-client` 540157a5a, final numbers on 62ac4bdc6 (merged). Scratch: session scratchpad `e7\`.

**How much walk was left (measured first).** A ceiling build (`CUO_E7_EXP=1`: animated art treated as cacheable, so its tiles leave the walk entirely; wrong animation, timing only), JIT with `DOTNET_TieredCompilation=0`, 2 alternated sessions: large scene world prepare 1.08 -> 0.78 ms at zoom 1 (items walked and extracted 2,829 -> 0), 1.59 -> 0.97 at zoom 2.5 (6,912 -> 0). Britain, the bank and Britain zoom 2.5 walk 0-6 items: nothing to gain there. So the cost is real in busy scenes only, and worth taking there.

**Why those items were walked:** W4 left a tile in the walk when any unbaked object on it wasn't cacheable. In the scene the lit and animated art blocks (0x3996, fire 0x19AB) sit on top of 15 item layers and under the mobile block, so one animated item kept 15 items and 2 mobiles in the walk.

**Change** (`GameSceneGpuItemCache.cs`; setting `gpu_prepare_item_cache_mixed`, default on; plan entry `itemmixed:on|off`, driver command `itemmixed`):
- At extract time, an uncacheable object that is an item and can never end the tile's walk (its `PriorityZ` plus tiledata height is within the walk's max Z of 150, so `AddTileToRenderList` can't `return` at it) no longer blocks the tile. It is recorded as walked-alone, with the number of cached entries before it in the tile list. Anything else (unbaked land or statics, multi components, effects, a too-tall item) still keeps the tile in the walk. Transient reasons (text, fading in, offset, handles, the hovered container) become walked-alone too; the chunk still re-extracts after 30 frames so they get cached once settled.
- Serving a tile with walked-alone items (`GpuServeMixedTile`, out of line): in tile order, the cached entries before each such item join the capture at the statics list's current length, then the walk visits the item alone (`AddTileToRenderList(item, ..., stopAt: item.TNext)`), then the rest of the cached entries. The statics-list order, and so the dynamic buffer's order and equal-depth ties, is the full walk's.
- A tile that also holds mobiles leaves `walkTiles` and is walked in one pass over its list: mobiles and walked-alone items in tile order (a corpse and a mobile share the animations list, a fading item and a fading mobile the transparent list), cached items skipped. Tiles with a mobile the max Z stops at stay in the walk, as before.
- Validity: each walked-alone item must still be on that chunk and tile and not destroyed, else the tile is walked in full that frame and the chunk re-extracted (W4's fallback). Mouse art-box tiles, the hovered container's tile, fades and object handles behave as in W4.
- `AddTileToRenderList` and the chunk loop in `FillGameObjectList` are unchanged: no new code in the Legacy-shared walk.
- `CUO_GPU_ITEM_CHECK`: walked-alone items are not "extra"; a cached item's expected statics position is corrected by the walked-alone items of its tile queued before it (counted back in the statics list); the "other queued objects" hash keeps the walked-alone items. New counter `item_cache_walked`.

**Measurements** (NativeAOT, base 62ac4bdc6 vs new 9edb96343; 3 alternated rounds, each a Gpu session and a Legacy session; 1920x1080 at 1.5 DPI, 3 windows per spot; ms, means of the run averages). Other agents' clients and builds ran throughout; the per-run spread is in the last column.

| Spot | world prepare base -> new | prep_walk | item extract (walked items) | cached replay | objects walked | items walked / served / walked alone | runs (new) |
|---|---|---|---|---|---|---|---|
| Scene, zoom 1 (Gpu) | 1.175 -> 0.871 | 0.521 -> 0.408 | 0.193 -> 0.024 | 0.036 -> 0.047 | 8,678 -> 2,423 | 2,829 / 11,370 -> 177 / 14,022 / 417 | 0.97, 0.77, 0.87 |
| Scene, zoom 2.5 (Gpu) | 1.754 -> 1.133 | 0.669 -> 0.463 | 0.430 -> 0.043 | 0.039 -> 0.067 | 8,899 -> 2,419 | 6,912 / 12,564 -> 432 / 19,044 / 432 | 1.25, 1.02, 1.13 |
| Britain (Gpu) | 0.062 -> 0.062 | | | | 181 | | |
| Bank (Gpu) | 0.063 -> 0.063 | | | | 208 | | |
| Britain zoom 2.5 (Gpu) | 0.217 -> 0.226 | | | | 1,086 | | 0.23, 0.20, 0.25 |
| Legacy Britain | 0.173 -> 0.161 | | | | 8,149 | | |
| Legacy zoom 2.5 | 1.268 -> 1.108 | | | | 41,543 | | |
| Legacy large (`size:max`, zoom 2.5) | 4.397 -> 4.317 (p50 4.317 -> 4.239) | | | | 138,872 | | base 4.09-4.62, new 4.14-4.60 |

- Gpu frame in the scene: 2.13 -> 1.78 ms at zoom 1, 2.89 -> 2.19 at zoom 2.5.
- The items still walked in the scene (177 at zoom 1) are on tiles under the mouse, beside mobiles the max Z stops at, or at screen edges.
- **Legacy is unchanged** (no code in its path). Its large-spot numbers this afternoon were 4.3-4.4 ms for both builds, above the legacy2 agent's 3.96 from a quiet evening; paired, new is not worse.

**Verification:**

| Check | Result |
|---|---|
| `CUO_GPU_ITEM_CHECK=1`, `CUO_GPU_SKIP_CHECK=1`, `CUO_PREP_CHECK=1`: scene zoom 1 and 2.5, lane walks at zoom 1 and 2.5, mixed on/off, Britain, bank, Britain zoom 2.5 | Item check: 4,325 checks, 0 with differences, 26M cached items compared; "other queued objects" different only while walking (41). Prep check: 0 wrong. Skip check: 0 unsettled; 632 "drawn differently" on land at the top edge (anchor y -583) during the zoom-2.5 lane walk only, **in the base build too** (199 in the same plan): pre-existing, not E7's |
| `pickscan:8x6` (skipping off vs on): scene zoom 2.5, Britain, bank, Britain zoom 2.5 | 0 different in all four |
| ECS parity `CUO_ECS_PARITY=1 CUO_ANIM_SYNC=1 -ecscheck 120`, with the item and skip checks: `tools/ecs-parity/e7mixed.txt` (new: Green Acres tiles mixing arrows, bandages and apples with 0x3996, fire 0x19AB, a candle, a corpse with items; walking onto them) with `light 1E` and with `light off`; the scene (`[go`, walk and run the lane, `light 1E` then `off`) | 0 mismatches. Mixed, night: 248k item / 133k mobile comparisons (64k corpse), 137k lights, 0 lights mismatched. Mixed, `light off`: 226k / 124k. Scene: 5.5M item (walked items only) / 14.5M mobile, 787k lights (6.5k skipped-full). `-ecscheck` 0. Item check 0 differences in all three |
| Shots (looked at) | In-session mixed on/off: Britain identical; Green Acres differs only on the animating fire and 0x3996 and the player; the scene only on flapping creatures. Base vs new: Britain 0.18%, bank 0.47% (moving NPCs), zoom 2.5 0.03%. Gpu vs Legacy: Britain 7.2%, bank 16.2% (the known filtering), scene creatures. Legacy base vs new: Britain 0.07%, zoom 2.5 0.01% |
| Unit tests; build flavours | 550 pass; Release and `-p:CuoDevTools=false` build |

**Process notes:** an early compile check ran without bench.lock while another agent was measuring (my mistake). A build script I stopped kept running in the background (its children survived) and built in my worktree for a few minutes; I rebuilt every binary cleanly afterwards. One `taskkill` of an `ilc.exe` was denied; it was not mine.

**Left:** tiles under the mouse, with a mobile the max Z stops at, or with non-item unbaked objects (multi components, unmeshed statics, effects) are still walked in full. The pre-existing skip-check land difference at the zoom-2.5 top edge while walking is worth a look (W1's edge band).

**Codex audit (no rendering bug; one P2 checker defect), fixed in eed280e77:**
- **The defect:** the item check's placement test counted the walked-alone items before each cached item in the *reference* walk's statics list, so it never tested the split the serving path made. A tile served as C1, C2, W instead of C1, W, C2 passed it, and the "other queued objects" hash too (it leaves out cached items). `CUO_GPU_ITEM_CHECK=every` made every frame a check frame, so it never ran the serving path at all.
- **Fix:** while the check is on, the capture mixes every object it reaches, and every cached item the cache emits, into an order-sensitive hash (`RenderLists.CaptureOrderHash`: the dynamic buffer's order, cached runs where they were injected, walked-alone items and mobiles included). A check frame's hash must equal the served frame's just before it; logged as `capture order vs the served frame before: N same, M different`. `=every` now checks every other frame, so every check frame follows a served one. Unit tests (`CaptureOrderHashTests`): the audit's counterexample, plus a missing or extra object, hash differently.
- **The new check catches a broken split:** a temporary build that queued a mixed tile's cached entries after its walked-alone items (not committed) gave `capture order` 5,705 different of 12,672 in the scene with `=every`, while the old placement check still reported 0 differences. (A first broken build that queued them all *before* the walked-alone items was a no-op here: in the scene and in `e7mixed.txt` the uncacheable item is always last in its tile list.)
- **Re-verified on eed280e77** (JIT, `TieredCompilation=0`; item, skip and prep checks on): scene zoom 1 and 2.5 with `=1` (840 checks) and `=every` (8,029 checks, 110M cached-item comparisons); `e7mixed.txt` at `light 1E` and `light off` with `=every` and ECS parity. 0 item-check differences, 0 skip-check unsettled or drawn differently, 0 prep-check wrong; ECS parity 0 mismatches (night: 3.8M item / 959k mobile comparisons, 150k lights; `light off`: 523k / 130k, 0 lights); `-ecscheck` 0. Capture order: 0 different in every standing window. The only differences (2 in the scene, 14 and 19 in `e7mixed`) were frames while the teleport or zoom change was settling or while the driver was adding items and the brigand was walking up, which the reference walk can't match by design. 563 unit tests pass; Release and `-p:CuoDevTools=false` build. No serving code changed, so no timing rerun.

## E6: graphic effects no longer keep their tile's items out of the item cache (2026-10-26)

Branch `bg/ecs-e6-effects` (E6 agent, kbatman3), started on `poc/modern-client` 1333ccbbd, final numbers on c527bc21c (merged in). Scratch: session scratchpad `e6\`. Code: commit 047cc06a0 plus the merge.

**Measured first (JIT, `TieredCompilation=0`, in-session; new FrameStats phase `draw_fx` and counter `effects`).** The task assumed effects are mostly capture cost. They are not:
- **Capture:** the effects list drawn into the world pass costs about 0.07-0.09 us per effect (0.012 ms for 173 effects at Britain, 0.028 ms for 303 in the large scene). A mirror into the ECS and a per-frame extract could save a fraction of that, so **no ECS effect extract was built**: it would be a parity risk for under 0.02 ms at 300 effects.
- **The walk:** Britain, 173 effects: world prepare 0.059 -> 0.084 ms (walk +0.019). Large scene (`5156 2150`): 84 effects 0.85 -> 0.95 ms, 303 effects 0.85 -> 1.28 ms, i.e. 1.2-1.4 us per effect, 15x the capture. Cause: W4/E7's cache treats an effect on a tile as an unbaked non-item, so the tile is "walked in full" (about 17 objects: all its cached items and mobiles; `objects` 2,423 -> 7,900, items walked 177 -> 3,897). Second cause: `Chunk.OnTileContentChanged` marks the chunk's items dirty for anything that is not a Mobile, so every moving effect crossing a tile re-extracted the whole chunk's items (item cache builds 1 per frame with 54 moving effects, `prep: item cache` 0.007 -> 0.153 ms).

**Change** (`GameSceneGpuItemCache.cs`, `GameSceneGpuPrepare.cs`, `Chunk.cs`; setting `gpu_prepare_item_cache_effects`, default on; plan `fxserve:on|off`, driver `fxserve on|off`):
- `GameEffect`s are ignored by the cache build (neither a reason to walk the tile nor an entry) and don't set `Chunk.GpuItemsDirty` (`Chunk.GpuEffectsIgnored`, set per frame with the cache key; the setting is part of the cache generation).
- `RebuildGpuMask` adds `Chunk.GpuEffectMask` (tiles with an effect). It is rebuilt whenever an object joins or leaves a tile list (`GpuMaskDirty`, unchanged), so it is exact.
- A served tile with effects leaves `walkTiles` like a tile with walked-alone items and goes through `GpuServeMixedTile`'s single pass over its list: mobiles, effects and walked-alone items are walked in tile order (`AddTileToRenderList(obj, ..., stopAt: obj.TNext)`), cached entries queued around the walked-alone items as before.
- No ECS involvement; `AddTileToRenderList` and the chunk loop are unchanged, so Legacy is untouched (`GpuEffectMask` and the flag are only read by the Gpu prepare).

**Invariants (for the audit):**
1. An effect never ends a tile's walk: its case in `AddTileToRenderList` neither calls `CalculateObjectHeight` nor returns at the max Z, and the x cull (`break`) depends only on the tile (all objects of a tile share `RealScreenPosition.X`; an effect's `Offset` is not part of it).
2. An effect goes to the effects list (drawn after every other object, `DrawDeferredRenderLists`) or, when its alpha isn't full, to the transparent list. Its position relative to cached items therefore doesn't matter (they are opaque, in the statics list); only its order among the tile's other transparent objects does, which the single pass keeps. Across tiles the order is the loop's.
3. `ProcessAlpha` reads only the effect and its tiledata (plus the frame's fade flags); serving runs only when `_gpuSkipActive` (no fade steps, no fresh chunks), so it needs no state from earlier objects of the tile.
4. `GpuEffectMask` is only used when `_gpuItemKeyEffects`; with the setting off effects are uncacheable objects and dirty chunks, exactly as before.
5. A served tile that fails validation (`GpuItemTileValid`, `GpuItemWalkedValid`) is walked in full, effects included (the chunk loop's fallback), and a tile stays fully walked when under the mouse, in the selected container's chunk tile, with a stopping mobile, or in a fade.
6. Effects on tiles with no cacheable item are walked in full as before (the tile isn't in `cache.Tiles`).

**Measurements** (NativeAOT, base = this tip with the 3 feature files reverted to `poc/modern-client` = "base-aot", new = HEAD; 3 alternated rounds, Gpu plan 14 spots then Legacy plan; 1920x1080 at 1.5 DPI, 3 windows per spot; means of the run averages, ms; machine shared with other agents' builds and runs, per-run spread up to +-0.15 ms in the scene):

| Spot (fixed fire columns `3709`) | world prepare base -> new | objects walked | items extracted/walked | runs (new) |
|---|---|---|---|---|
| Scene zoom 1, 0 effects | 0.935 -> 0.982 (second block 0.934 -> 0.926) | 2,499 | 252 | 0.86, 0.84, 1.25 |
| Scene zoom 1, 82 effects | 1.133 -> 0.955 | 3,958 -> 2,599 | 1,371 -> 252 | 0.90, 0.89, 1.08 |
| Scene zoom 1, 230 effects | 1.376 -> 0.979 | 6,581 -> 2,792 | 3,168 -> 252 | 0.93, 0.89, 1.13 |
| Scene zoom 1, 54 moving effects (sustained) | 1.205 -> 1.001 | 3,299 -> 2,556 | 958 -> 252 | 0.93, 0.94, 1.13 |
| Scene zoom 2.5, 0 effects | 1.212 -> 1.197 | | | |
| Scene zoom 2.5, 150 effects | 1.522 -> 1.234 | 4,635 -> 2,659 | 2,498 -> 522 | 1.13, 1.11, 1.46 |
| Britain / bank, 0 effects | 0.067 -> 0.063 / 0.068 -> 0.065 | 196 / 197 | | |
| Britain 38 effects / bank 30 effects | 0.076 -> 0.070 / 0.073 -> 0.069 | 236 / 229 | | |
| Britain zoom 2.5, 0 / 100 effects | 0.234 -> 0.229 / 0.251 -> 0.245 | 1,094 / 1,199 | | |
| Legacy Britain / zoom 2.5 / large | 0.191 -> 0.150 / 1.112 -> 1.072 / 4.242 -> 4.395 (runs 4.06-4.58 vs 4.06-5.0) | | | noise; no Legacy code changed |

- Effect cost in the scene (new vs 0 effects, same build): 0.09 us (82), 0.0 (230; within noise), versus 1.2-1.4 us per effect before. The capture side (`draw_fx`) is unchanged: 0.009-0.020 ms.
- JIT in-session pairs (`fxserve:off` vs `on`, same session): 303 effects 1.192 -> 0.774 ms (0 effects 0.74), 54 moving 1.001 -> 0.777 (0 effects 0.71), item cache builds 1 per frame -> 0.

**Verification:**

| Check | Result |
|---|---|
| `CUO_GPU_ITEM_CHECK=every`, `CUO_GPU_SKIP_CHECK=1`, `CUO_PREP_CHECK=1`, scene zoom 1 and 2.5 with 120 fixed fire, all 7 blends (60 each), lightning (3), 60 moving effects | Item check 0 with differences over 13.9M cached-item comparisons; "other queued objects" (the hash includes the effects list: identity, alpha, order) and capture order 0 different in every standing window; only windows with moving effects or a spawn in them differ (227/950, 271/791 frames), as they must; skip check 0 unsettled, 0 drawn differently; prep check 0 wrong |
| The checker sees a broken serve | Build with the effect `AddTileToRenderList` call left out of `GpuServeMixedTile`: "other queued objects" 0 same, 998 different |
| ECS parity (`CUO_ECS_PARITY=1`, `CUO_ANIM_SYNC=1`, `-ecscheck 120`, item check `every`) with `tools/ecs-parity/e6fx.txt` (e7mixed's mixed tiles plus fixed effects in every blend, lightning, moving; fxserve on/off; walking), `light 1E` and `light off` | 0 mismatches. Night: 4.0M item / 994k mobile comparisons, 188k lights, 0 lights mismatched; day: 652k / 144k; `-ecscheck` 0; item check 0 differences (4,591 / 4,588 checks) |
| Pixels: non-animated bags (`0E75`, `0E76`) in every blend over item tiles, `fxserve:on` vs `off` in one session | Identical (0.000%, world and lights images), night and `light off` (looked at the day shot). With animated fire the shots differ by the animation alone (all of it) |
| Unit tests | 570 pass on the merged tip; Release and `-p:CuoDevTools=false` build |

**Limits / left:**
- Checker limit: the item check compares two different frames (a served one and the check frame after it), so an effect wrongly left out of the served frame could expire before the reference frame; and the hash covers the effects list (identity, alpha, order) but not an effect's geometry, blend or depth. The effect tooling (`BenchEffects`, the driver's `fx`/`fxserve`, `World.Effects`) is behind `CUO_DEVTOOLS`.
- Not an ECS effect extract (see measurements): capture is 0.09 us per effect. If a later scene has thousands of effects, a per-frame extract from `EffectManager` would need a sort key reproducing the walk's order (equal-depth effects are ordered by it).
- Effects still dirty `GpuMaskDirty` (mask rebuild of one chunk per tile crossing), as mobiles do.
- No unit tests for the mask/ignore rules: `Chunk` needs a `World`. The checkers above are the tests.
- The e6fx script's moving effects are one-shot (the driver doesn't `Tick`); the sustained moving case was measured with the plan entry.
- Effects with a translucent graphic (alpha 178, transparent list) were not found in a graphic that the pack has with that flag; the single-pass order logic covers them by construction but no run exercised one.
- Moving effects that start from a mobile (`SetSource(mobile)`) follow its tile: served tiles with mobiles and effects use the same pass; exercised by the walking part of `e6fx.txt` only partly.

## Chunk loads without allocation: evicted land and statics reused (2026-10-26)

Branch `poc/chunk-load-alloc` (chunk-alloc agent, kbatman4 and kbatman3), from poc/modern-client 24a7e1504; final numbers on 3f0edbc0f (merged). Follows "Teleport frames: first-sight chunk art decoded on workers", whose remaining travel frames were GC pauses inside chunk loads. Method: [testing/pack-loading.md](testing/pack-loading.md) §5. Scripts and logs: session scratchpad `chunkalloc\`.

**What was measured first** (new FrameStats counters `chunk_alloc`, `chunk_gc`, `chunk_gc_us`, window sums in the json, and a `[gc]` line per collection):
- A chunk load allocated **about 25 KB** (23–26 KB averaged over 550–830 loads per entry): 64 `Land` objects and one `Static` per map static. All of it survives, so every collection promoted it.
- The gen0 budget here is 15 MB. In base, collections are mostly gen1 and promote 9–24 MB each, with 3–11 ms pauses. Base collects about every 10–15 s while teleporting: 5–7 collections per 55 s after login.
- Heap dumps (JIT build, `dotnet-dump`, `dumpheap -stat -gen gen0|gen1`) showed two more allocators:
  - `MapLoader.StartPrefetch` allocated its `Task.Run` closure on entry, on every call (43k closures between two collections). It is called several times a frame and nearly always returns early.
  - `PaperDollGump`'s `ItemGumpFixed` queued a lambda per slot per frame.
  - What survives besides land and statics: the Legacy `MeshLayer` vertex arrays of new and growing chunks, Uo.Render bake arrays, and the server's objects after a `[go`.

**Why upstream's pools were commented out:** `git log -S` finds pools for Land, Static, Item, Mobile and Multi since 2019 (`QueuedPool<T>`, d9f332931). They were commented out together in upstream's "net8 porting" (0ad5ee274, 2024-01), which gave every GameObject a `World` constructor argument; `QueuedPool<T>` needs `new()`. That commit gives no lifetime reason. Two things the old pools did badly:
- They reset 3–6 fields per type (IsDestroyed, AlphaHue and a few others), not the whole object.
- They put an object back into the pool from `Destroy`, the moment it died, with nothing to protect anyone still holding it.

The same risk is real in this repo: 664bd36b7 fixed a chunk pooled while the map still owned it (UltimaLive reload).

**Design.** Options weighed:
- **A per-chunk struct-of-arrays land:** every tile list, picking, the mesh builder, the Gpu prepare and the ECS caches walk `GameObject` lists. A rewrite of the world model; not this task.
- **Reusing a chunk's own objects with the chunk:** chunks are reused for any block, and a pooled chunk can be handed out in the same frame it was retired, so lifetimes are no different from a pool. The statics count also varies per block.
- **Avoiding promotion without reuse:** .NET has no pretenuring for ordinary objects, and a larger gen0 budget only makes each collection bigger.
- **Chosen: a per-World recycler** (`MapObjectRecycler`) with the invariants below. Each one closes a way the old pools could fail.

**Lifetime invariants** (also in `MapObjectRecycler`'s header comment):
1. **Only `Chunk.Destroy` retires objects** (evictions from `Map.ClearUnusedBlocks`, `Map.Destroy` on facet change or logout). It retires an exact `Land` or `Static` that it found live in the chunk's tile lists, after `GameObject.Destroy` and after unlinking it (TPrevious, TNext and TileChunk null; `Retire` rejects anything else).
   - Land and statics are created only by `Chunk.Load` and live only in tile lists. Nothing else calls `Create`, and `ClearForReload` doesn't recycle.
   - Evicted chunks hold only land and statics (`HasNoExternalData`).
2. **Quarantine:** a retired object stays destroyed (IsDestroyed, Graphic 0, `InRecycler`) for at least 1 s (`CUO_RECYCLE_QUARANTINE_MS`) before `TakeLand`/`TakeStatic` hand it out, oldest first.
3. **Cross-frame holders are scrubbed on retire.**
   - The holder audit found one production holder that keeps a land or static across a frame boundary: `SelectedObject.Object`, set in the draw walk and read in the next Update. `ClearUnusedBlocks` runs at the top of that Update. `Chunk.Destroy` now nulls it, and `LastLeftDownObject`, when they hold a destroyed object.
   - Everything else that sees land and statics either finds them through the tile lists every frame, or keeps a per-frame list read only up to its count: render lists, the foliage array, the Gpu capture lists, `GpuWorldRenderer._object`, lights (values only), the mesh (no object references), the item cache and walked-alone sets (items only), the art wait tracker (keys), MobileFrameCache (chunk plus ContentVersion), text (`Destroy` clears the container and unlinks the texts; TextOwners holds entities only), the pathfinder (per call) and `TargetManager` (values).
   - `_prevMeshHighlight` (Legacy) is read and nulled in the next draw, but updates without draws can outlast the quarantine (`Time.Ticks` moves in Update), so it also keeps the object's `Incarnation` and skips the hue restore when the object was reused (Codex audit). `InspectorGump` keeps its object and compares `GameObject.Incarnation` too. DevBench's pick targets are dev-only.
4. **Reuse starts from the constructor's state.** `GameObject.ResetForReuse` (plus the Land and Static overrides) sets every instance field back to its initial value and bumps `Incarnation`; `Land.Create`/`Static.Create` then run as for a new object, season graphic included. A reflection unit test dirties every field of a Land and a Static and fails if any differs from a new object after the reset, so a field added later must be reset too.

`CUO_RECYCLE=0` turns reuse off. `CUO_RECYCLE_CHECK=1` never reuses but keeps retired objects marked and counts every later use: Legacy draw and picking, the Gpu capture, the selection holders, the mesh highlight and the inspector. It also sweeps every loaded chunk's tile lists every 64 frames. `=reuse` runs the same checks with reuse on.

**Other changes:**
- The prefetch closure moved out of line.
- The paperdoll slot draw is a `GumpCommand.Sprite`, which draws the same `batcher.Draw` call.
- New FrameStats counters: `chunk_reused`, `chunk_retired`, `chunk_mesh_alloc`, `chunk_new`.

**Measurements.** NativeAOT, `DOTNET_TieredCompilation=0`, Gpu world, 1920×1080, plan `travel;ltravel;glide`, 4 windows of 5 s. Base is poc/modern-client plus the counter commits only (branch `poc/chunk-load-alloc-base`). The first `travel` window, which holds the login warm-up, is shown apart. GC counts are gen0 (gen1); pauses are summed.

Final series, on 3f0edbc0f: three alternated pairs, D–F. The machine was quiet (no other client).

| | Base D/E/F | New D/E/F |
|---|---|---|
| bytes per chunk load | 24.1–25.4 KB | 0 (0.2 KB in `travel`: chunks loaded before the first eviction) |
| `chunk_load_us` max after login | 6.0, 6.3, 6.8 ms | 0.7–1.2 ms (one 5.1 ms: a GC inside the load) |
| collections after login (55 s) | 7, 5, 5 (25.7, 20.3, 20.4 ms of pauses) | 1, 2, 2 (4.0, 11.3, 12.4 ms) |
| `travel` w2–4 U+D max / >8 ms | 7.0–7.8 / 0 | 5.7–6.2 / 0 |
| `ltravel` U+D max | 4.2–6.9 | 2.8 (0 collections in all three) |
| `glide` U+D max / >8 ms | 3.7–4.4 / 0 | 2.1, 7.8, 8.5 / 0, 0, 1 |
| login window (`travel` w1) | 5.3–5.8, 1 collection | 6.0–10.7, 2 collections |

- The earlier series (A–C on c527bc21c, one other client running) agreed. After login, base had 4–7 collections per run, new 1, and new with `CUO_RECYCLE=0` 5. Reuse is what removes the collections; the two closures alone don't. `travel` U+D max was base 8.2–14.5 against new 4.1–8.5.
- **Every remaining collection in new's short runs is login warm-up.** At first the chunk pool and the recycler are empty, and the evictions that feed them start 3 s after a chunk was last drawn. The first teleports allocate about 150 new chunks, about 6 MB of land and statics, and their mesh arrays.
  - That gives two gen1 collections in the login window (4–8 ms) and, about 30 s later, one more compacting gen1 that promotes that warm-up (18–19 MB, 7–7.5 ms). In newE and newF it landed in `glide`, which is the 7.8 and 8.5 ms frames.
  - Base pays the same warm-up, but its collections are already every 10–15 s.
- **Long session** (13 minutes, 7× `travel`, `glide`, `ltravel`, `bwalk`; c527bc21c, one other client, run back to back):
  - base: 39 collections, 142 ms of pauses;
  - new: 6 collections, 25 ms (3–5 ms each after the warm-up, about one every 2 minutes);
  - frames over 8 ms after the first window: base 2, new 1.
  - Private bytes: new 2,100 MB at 1 min, 2,129 MB at 5 min, 2,145 MB from 10 min to the end (flat); base 2,143 → 2,168 MB.
  - Recycler cap: 19,200 land and 38,400 statics (300 chunks' worth).
  - 23 `bwalk` legs, 0 server denies, 0 exceptions, 178 settle episodes with 0 timeouts.
  - Re-run of new on 3f0edbc0f (quiet machine): 7 collections in all. Four are login warm-up in the first 2 entries; then 3 in 11 minutes (5.7, 2.9 and 4.1 ms). After the first window: 2 frames over 8 ms (max 8.8 ms), 19.5 ms of pauses. Private bytes 2,103 MB at 1 min, 2,140 MB at 3 min, 2,147–2,164 MB from 5 min, 2,152 MB at the end. 0 exceptions, 23 `bwalk` legs with 0 denies, 178 episodes with 0 timeouts.
- **Still allocating while teleporting:** the Legacy `MeshLayer` arrays of pooled chunks growing to a busier block (`chunk_mesh_alloc` 0.3–1 MB per 5 s, falling as capacities settle), and in `travel` the server's objects. A gen0 collection that promotes only 2–3 MB still pauses about 3 ms here. perf-opportunities.md lists what's left.

**Verification** (final tip, new binary):

| Check | Result |
|---|---|
| Settle | 0 timeouts and 0 unsettled frames in an episode in every run (pairs, checks, facet, season, long). Walking-unsettled in the pairs: 122–152 new against 120–128 base (the art-wait latency the art agent described) |
| `CUO_RECYCLE_CHECK=reuse` with `CUO_GPU_SKIP_CHECK=1`, `CUO_PREP_CHECK=1`, `CUO_GPU_ITEM_CHECK=1` (`travel`, `ltravel`, `glide`, Britain, the bank, Britain at zoom 2.5) | 264k retired, 260k reused, 0 uses after retire, 4,298 sweeps (75M objects), 0 bad. Skip check 222 checks, 0 unsettled, 0 drawn differently. Prep check 0 wrong. Item check 0 with differences; capture order 222 same |
| `CUO_RECYCLE_CHECK=1` (strict: never reuses, retired objects marked forever) | Gpu (`travel`, `ltravel`, `glide`): 248k retired, 0 uses. Legacy (`travel`, `glide`): 107k retired, 0 uses, 0 bad sweeps |
| Picking: `pickscan:8x6` at Britain, the bank and on Felucca and Trammel, with the reuse check | 48 same, 0 different, in all four (101k objects reused during the run) |
| Facet change (`[self set map felucca`, then `trammel`) | Ready after 56 and 52 ms, 0 unsettled |
| Season (`season:winter`, `ltravel`, `glide`, Britain, `season:summer`, `ltravel`, Britain; reuse check on) | 0 timeouts; 159k reused; 0 uses after retire. Looked at the winter shot (snow, bare trees, complete) and the summer shot (complete) |
| ECS parity: `CUO_ECS_PARITY=1 CUO_ANIM_SYNC=1 -ecscheck 120`, kbatman3, driver `chunkalloc\parity-travel.txt` (11 `[go` teleports between towns with walks: `light 1E`, then `light off`) with the reuse, item and skip checks | 0 mismatched: 234k item / 158k mobile comparisons, 7,380 lights, 0 lights mismatched, `-ecscheck` 0 in 772 checks. 24k reused, 0 uses after retire. Skip check 0, item check 0 |
| Legacy shots (`-worldrenderer legacy`, base vs new: Britain, after `travel`, after `glide`, Britain again) | 0.01–0.02% of pixels differ (>8); near-black 2.2–2.8% in both. Looked at: complete town, no missing or wrong tiles |
| Unit tests | 578 pass, in both build variants (`-p:CuoDevTools=false` too). 8 are new (`MapObjectRecyclerTests`): every field reset, the field check catches a missed field, quarantine and FIFO, time wrap, live, linked or other objects never pooled, disabled never reuses, a reused object comes back live with a new incarnation |

**Not done or noted:**
- **Known, pre-existing (not from this branch):** a second `Chunk.Destroy` on the same chunk can queue it in the chunk pool twice. The emptied tile lists stop a double retire of its objects.
- `pickscan` in a pure Legacy session (`gpu available: False`) waits until the run's timeout. It compares the two renderers, so it can't run there. Picking was checked in Gpu sessions; the Legacy strict check covered Legacy picking calls.
- No 4K run, and not the owner's FPS test.
- Process: two cherry-pick conflicts while building the base branch; one was committed with markers for a minute and amended before any build.

**Follow-ups:**
- The login warm-up collections: pre-fill the pools during the login hold, or trigger the collection inside a settle hold.
- Legacy `MeshLayer` growth (size the arrays from the block once, or skip the Legacy mesh in Gpu mode).
- Health bar gump allocations (already listed).

**Codex audit (no use-after-reuse found), fixed:**
- `[gc]` lines: the GC baseline is taken when a window starts (and at its first frame, with the window's gc counts), so an earlier collection is no longer logged under the next window's tag, and nothing is logged for a frame without one.
- The recycler's field test: every non-exempt field must really change when dirtied (strings, arrays and abstract `GameObject`-typed fields get non-null sentinels; an unsupported type fails explicitly), so a nullable cache left out of `ResetForReuse` fails. A new test checks that a reference field left out is caught.
- `_prevMeshHighlight` checks the object's incarnation (above).
- 579 unit tests pass in both build variants. One `CUO_RECYCLE_CHECK=reuse` pass (`travel` and `glide`, NativeAOT on 51c9333f3): Legacy, where the mesh highlight lives, 137k reused; Gpu, 128k reused. In both, 0 uses after retire, 0 bad sweeps and 0 exceptions; Gpu settle 0 timeouts and 0 unsettled frames in an episode. The `[gc]` lines now begin at the window's first collection (#9), not at one from login.

## E11: ECS test driver behind CUO_DEVTOOLS; one mirror Sync per SetInWorldTile (2026-10-30)

Base: poc/modern-client f2668520f. Branch `bg/ecs-e11-hygiene`.

**Driver guard.** `EcsSessionDriver.cs` (whole file), the `-ecsdriver` case in `Main.cs`, `CUOEnviroment.EcsDriverScript` and `WorldEcs.Driver` plus its load/attach block are now inside `#if CUO_DEVTOOLS`. `-p:CuoDevTools=false` builds with 0 errors and its `cuo.dll` holds neither `EcsSessionDriver` nor `DriverGump`. The driver uses no helper that only it needs (it calls existing World, FrameStats and Settings members; its `fx`/`fxserve` cases were already guarded), so nothing else moved. `-ecscheck` (the mirror consistency checker, `WorldEcs.RunCheck`) is left in every build: testing/ecs-animation.md uses it as the standing parity check and it costs nothing unless the switch is given; owner decision (2026-10-30): it stays in every build during the PoC and is to be compiled out of Release builds for release (release-cleanup.md records it).

**Double Sync.** `SetInWorldTile` ran `UpdateScreenPosition()` (which calls `World.Ecs.Sync`) and then `AddToTile`, which always ends in a `Sync` of its own (parked, chunkless and placed paths all do). `Sync` reads only the entity's fields (graphic, hue, x/y/z, direction, flags, amount, container, layer); `AddToTile`, `RemoveFromTile`, `Map.Park` and `Chunk.AddGameObject` change none of them, and `PlayerMobile.OnPositionChanged` (doors, corpses, plugin) does not read the mirror. So the first call wrote the values the second writes. It is redundant in every case, including a container-to-ground re-key (the caller clears `Container` before `SetInWorldTile`) and equipment/mobile index updates (they come from the second call's values). `SetInWorldTile` now sets `IsPositionChanged`, calls `OnPositionChanged` and `AddToTile`; every other `UpdateScreenPosition` caller keeps its Sync.

**Measured** (E2 bench, new row "mobile SetInWorldTile"; coordinates advance per visit of each mobile, so no call repeats its mobile's position; `DOTNET_TieredCompilation=0`, Release, 100k calls, noisy machine; us per call at N=500/2000/8000 mobiles; one base run between two new runs, same tip): base 0.121/0.125/0.134; new 0.068/0.068/0.091 and 0.071/0.069/0.091. About 40-45% less; 0 bytes either way. Not a per-frame cost, so no FPS effect expected.

**Tests.** New `SetInWorldTileSyncTests` (audit fixes): a mobile removed from the mirror is created by the placement; each move adds exactly one `MobileInputs.Pushes` (the old double Sync gives 2, verified: the test fails on the old code); `IsPositionChanged` is cleared then set again; a bag item dropped to the ground is re-keyed (`ParentKind.Contained` to `None`, gone from `GetContents(BAG)`). Not covered: the parked and loaded-chunk paths, because a `Map` needs the UO file manager and cannot be built in a unit test. 582 unit tests pass.

**Verification.** Default and `CuoDevTools=false` builds compile. Smoke run (tools/ecs-parity/run_parity.py, account kbatman3, a short script: `[go`, 4 steps, backpack, check, quit): driver loaded and ran in the default build, `-ecscheck 120` 0 mismatches (442 checks), exit 0.

## L7: pack page handles (2026-10-30)

Base: poc/modern-client fdfcc9813. Branch `poc/pack-handles`. Result: **measured, negligible gain, no cache added.** The only change is docs.

**Question.** `PackPages.DecodeChunk` (`src/ClassicUO.Assets/PackAssets.cs`) calls `File.OpenHandle` and disposes it for every chunk decode. Is a bounded `SafeFileHandle` cache worth it?

**Measurement.** A throwaway console bench (session scratchpad `packh\bench`, JIT Release, `DOTNET_TieredCompilation=0`; open and close cost is the same system calls on NativeAOT) over the real pack `classic-1.0`. Per kind (art, anim, tex), 3 repetitions: 2,000 `File.OpenHandle` plus dispose over the kind's 32 pages round robin, and about 300 chunk decodes at random pages and positions through the public `PackPages.Touch` with a 1 MiB cache, so nearly every touch decodes. Machine noisy (other agents' clients); the repetitions agree within 1 µs and a few percent.

| Kind | Open + close | Decode per 1 MiB chunk (includes the open) | Open's share |
|---|---|---|---|
| art | 20.1, 22.6, 27.0 µs | 2.14–2.17 ms | 0.9–1.3% |
| anim | 22.0, 28.1, 26.8 µs | 2.48–2.53 ms | 0.9–1.1% |
| tex | 23.1, 20.9, 21.6 µs | 2.83–2.87 ms | 0.7–0.8% |

So the open is about 1% of a decode, at the low end of the 20–100 µs estimate (Defender's scan on repeated opens of the same files is cheap here; the first open of each page is not repeated). The decodes run on the chunk workers, not the main thread, and chunk decodes are a small part of a teleport frame. At about 25 µs per chunk, a teleport that decodes a few hundred chunks saves about 10 ms of worker time in total, spread over several threads. That is below the noise of every in-client metric (chunk load max, teleport worst frame), so the paired NativeAOT travel and glide runs were not done: they could not resolve it.

**Other readers.** Uo.Render `PageResidency`/`PageJob.Run` opens once per whole atlas page load (multi-MiB read), as do `Uo.Pack` `PackReaders` and `PackTables` once per file or region. None opens per chunk, so none has the pattern. `GetPage` opens once per page, the first time only.

**Why not add it anyway.**
- A handle cache needs bounding, LRU eviction that waits for in-flight reads, and closing on `Clear`, logout, pack switch and dispose; that is real complexity and a concurrency surface for 1%.
- Open handles block replacing or deleting page files while the client runs (the pack is opened with the default share mode, which allows others to read but not delete or rename over an open file on Windows). Today a page file can be swapped between decodes. Nothing in the repo replaces pages at runtime (the converter writes a pack folder offline), but a future patcher or in-place converter run during development would hit it.
- The `Brotli` (kind 1) path wraps the handle in a `FileStream`, which takes ownership and closes it, so it would need a separate open anyway.

**Revisit if:** a profile of the main thread shows chunk decodes on it (a `ReadRect` miss) with the open visible, or the pack moves to many small files (gumps, fonts, cliloc) read per use, where the open could rival the read. Then cache handles per file with the requirements above.
## E10: the animation clock into the ECS (measured, not done) and an owned mobile order (2026-10-30)

Branch `bg/ecs-e10-clock`, from poc/modern-client f2668520f. Draft quality, needs a Codex audit before merge. JIT with `DOTNET_TieredCompilation=0`, kbatman3, Gpu, 1920x1080, large scene. Logs and tools: session scratchpad `e10\`. The machine was noisy (other projects running; `world_mobiles` for the same configuration moved from 0.053 to 0.089 ms between sessions), so only paired and in-session numbers are used.

**1. Measurement: moving the clock reads is not worth it, so step 2 was not built.**
- What `SamplePushed` reads from the Mobile for the clock: `AnimationGroupRaw`, `AnimIndex`, `LastAnimationChangeTime` (cast), `AnimationFromServer`. The same function already reads the Mobile for `ViewFlipped`, `AllowedToDraw`, `AlphaHue`, `Offset` and the view context, so the Mobile's cache line is touched anyway; moving the clock does not remove the Mobile from the path.
- Paired micro-bench (`CUO_MOBILE_INPUT_BENCH=1`, new `[mobile-clock-bench]`; 32 reads of each kind per sampled mobile per frame through the same non-inlined call, 917M reads, large scene, three spots):

| The four clock reads | ns per mobile |
|---|---|
| From the Mobile (today) | 1.30 |
| From the already fetched `MobileInputs` copy (the clock as part of that component) | 1.21 |
| From a fresh `GetComponent` lookup (a separate component) | 4.54 |

  The pushed sample as a whole is 14.6 ns (legacy 57 ns) in this session, so the clock is under 10% of it. Best case 0.1 ns x 1,985 mobiles = **0.0002 ms per frame**; as a separate component it would be 3 ns slower per mobile (+0.006 ms).
- The price on the other side: the frame advance would have to push the clock for every advancing mobile (about 43 per frame in the scene, each with an entity lookup, about 30-100 ns), plus pushes from the write sites below. That alone cancels the gain.
- E4 on/off, the mobile loop (`world_mobiles`, ms, in-session `mobsys:off`/`mobsys:on` pairs, 2 windows each): z1 0.053 -> 0.031 and walking 0.064 -> 0.041 (first session); in the paired runs below on the new build 0.069 -> 0.039, 0.056 -> 0.034 (z1), 0.059 -> 0.046, 0.056 -> 0.034 (zoom 2.5), 0.077 -> 0.042, 0.050 -> 0.031 (walking). About 0.02-0.03 ms, as E4 found. What remains there is real work (the due updates), not the clock reads.
- **Decision: stop after step 3.** If the advance is ever moved into the ECS (E4's "Left" item 1: the selection against ECS state, so a standing mobile's advance needs no Mobile), the clock goes with it in the same change; on its own it buys nothing.
- **Write sites of the clock, for whoever does that** (grep over `src`; the Mobile fields are `AnimIndex`, `AnimationFromServer`, `LastAnimationChangeTime`, `_animationGroup`, `AnimationFrameCount`, `_lastAnimationIdleDelay`, `ExecuteAnimation`):
  - `Mobile.SetAnimation` (Mobile.cs 465-474: group, `AnimIndex`, frame count, from-server, `LastAnimationChangeTime`), called from PacketHandlers 2429 (0x6E), 3753 (0xAF death animation, then `AnimIndex = 0`), 4501 (0xBF 0x19 frozen frame, then `ExecuteAnimation = false`, `AnimIndex`), 4726 (an animation packet with frame count, then `AnimIndex`, `ExecuteAnimation = false`), 5617 (0xE2).
  - `Mobile.SetIdleAnimation` (485-492) and `CalculateRandomIdleTime` (304), from `Update` (361) and step completion.
  - `Mobile.ProcessAnimation` (722 frame count, 737 `AnimIndex`, 751 `LastAnimationChangeTime`); `_animationGroup` also in `GetGroupForAnimation` (559-616, random fidget picks).
  - `Mobile` constructor (131), `Entity.Destroy` (197-198), and the commented-out pool reset in `Mobile`/`Item` (dead code).
  - `Item.ProcessAnimation` (540, 544) and `Item` 341 write the same `Entity` fields for items; a mobile-only component would not cover them.

**2. Step 3: an owned ordered mobile table replaces the `Dictionary._version` trick.**
- **`MobileTable`** (new, `GameObjects/MobileTable.cs`) is now `World.Mobiles` (was `Dictionary<uint, Mobile>`). It has the dictionary's API as the world used it (`Get`, `Contains`, `TryGetValue`, `Add(Mobile)`, indexer get/set, `Remove`, `Clear`, `Count`, `Values`) and enumerates in the dictionary's order: lookups go through an inner `Dictionary<uint, Mobile>`, the order lives in a slot array (`Mobile[]`) with a LIFO free-slot stack. Add takes the most recently freed slot else appends; setting an existing key keeps its slot; `Remove` frees the slot; `Clear` resets. `Mobile.Ordinal` is the slot, set on insert.
- `World.SortInMobilesOrder` is now a plain sort by `Ordinal`. No `UnsafeAccessor`, no version counter, no ordinal rebuild walk (E4's stats line had "ordinal rebuilds N"; it now reports `table-vs-dictionary differences`).
- `Values` is a struct enumerator (no allocation in `foreach`); removing during enumeration is allowed as before, adding during one is not supported (the dictionary threw).
- Cost: one array store per add/remove, `Ordinal` kept for free; lookups are the same dictionary lookup as before.

**Invariants for the audit:**
1. The table enumerates exactly like the `Dictionary<uint, Mobile>` it replaced under every operation sequence the world uses. This rests on .NET's dictionary behaviour: freed entries form a LIFO free list that inserts consume first, resize and overwrite keep positions, `Clear` resets. `MobileTableTests` check it against a real dictionary over 6,000 random operations (add including refused duplicates, remove, overwrite, the 0xBB serial change as remove + set under a new key, clear, removal during enumeration). Nothing else in the client depends on the dictionary's order any more, only on the table's own.
2. `Mobile.Ordinal` equals the mobile's slot while it is in the table; an overwrite gives the new Mobile the old slot. A Mobile that is not in the table has a stale ordinal: `SortInMobilesOrder` must only get mobiles fetched from `Mobiles` this frame (the due list does: `Mobiles.Get` of each due serial).
3. The key and `Mobile.Serial` can differ for a moment (0xBB: `Remove(old)`, then `Mobiles[new] = owner`, then `owner.Serial = new`): the table keys by the argument, never reads `Serial` except in `Add(Mobile)`.
4. With `CUO_MOBILE_UPDATE_CHECK`, `World` enables a shadow `Dictionary` that receives every operation (only possible on an empty table, so it is set in the constructor), and `VerifyAgainstShadow` compares the table's enumeration, count and every `Ordinal` with it each frame under the clock path. The old order check (`CheckOrder`: the due list is a subsequence of a fresh enumeration) still runs, now against the table's enumeration.
5. Single-threaded, like the world.

**Verification** (default build and `-p:CuoDevTools=false` both build; the second also logged in and ran `-ecscheck` in the scene, 0 mismatches):

| Run (kbatman3, TieredCompilation=0, `CUO_ECS_PARITY=1 CUO_ANIM_SYNC=1 CUO_GPU_ITEM_CHECK=1 CUO_W5_CHECK=1 CUO_MOBILE_INPUT_CHECK=1 CUO_MOBILE_CACHE_CHECK=1 CUO_MOBILE_UPDATE_CHECK=1`, `world_update_mobile_system` on, `-ecscheck 120`) | Result |
|---|---|
| Large scene, `ls_parity` (standing, then the lane walked and run), day | clocks 16.6M 0 different; skipped updates 15.8M 0 changed; due-update order 801k, 0 out of order; **table vs dictionary 0 differences**; parity 7.07M mobile comparisons 0 mismatches, items 2.93M 0; input check 6.5M samples and 5.9M seat tests 0 different; item check 0; `[w5-check]` 0; `-ecscheck` 0 |
| `w5push.txt` (mount, dismount, seat, hidden, hue, death, walking NPCs) | clocks 3.5M 0 different; skipped 3.3M 0; order 237k 0 out of order; table 0 differences; parity 2.32M 0 mismatches; input check 2.24M and 1.86M seat tests 0 |
| `w5items.txt` (corpses, object handles on mobiles) | clocks 386k 0; skipped 346k 0; order 40k 0; table 0; parity 395k 0 |

Not run: the night (`light 1E`) variant (the lights path is untouched), NativeAOT. The 0xBB serial change and the char-creation preview mobile (added to `World.Mobiles` by `CreateCharAppearanceGump`) were not exercised live; the unit test covers the first.

Unit tests: 582 pass (3 new in `MobileTableTests`). `ChunkLoadingTests.Prefetched_Regions_Give_The_Same_Blocks_And_Never_Block_IsBlockReady` failed on the first run after a build and passed on the rerun, as in E4 (timing under load, unrelated).

**Timing** (paired builds: `bin-base` = f2668520f plus the inert clock micro-bench, dictionary and `UnsafeAccessor`; `bin-new` = the table; `world_mobiles`, ms, 2 rounds, interleaved, round 1 / round 2; noisy machine):

| Spot | base | new, E4 off | new, E4 on |
|---|---|---|---|
| zoom 1 | 0.089 / 0.067 | 0.069 / 0.056 | 0.039 / 0.034 |
| zoom 2.5 | 0.094 / 0.066 | 0.059 / 0.056 | 0.046 / 0.034 |
| walking | 0.077 / 0.070 | 0.077 / 0.050 | 0.042 / 0.031 |

Off equals or beats base inside the noise (a slot-array walk instead of the dictionary's entries); on saves 0.02-0.03 ms over off, including the sort. The table's gain over the old trick is the removed rebuild walk (52 per login-plus-scene run, 19 in another, each a 2k-mobile walk); it does not show in the frame time.

**E10 audit fixes (Codex, 2026-10-30):**
- **Clock bench wording (P3):** `[mobile-clock-bench]` is a surrogate hot-read measurement, not a precise bound. Its "inputs" variants read `MobileInputs` fields (Body, Facing, LastStepTime, Notoriety), not real clock fields; the "separate component" variant is a second `GetComponent` of the existing `MobileInputs`; the read order is fixed (Mobile, inputs, lookup), and everything is cache-hot. It shows that a handful of field reads cost on the order of 1 ns per mobile, so the saving is far below what the frame-time noise can show. "Don't move the clock on its own" stands on that, and on the push cost of every frame advance.
- **P2, ownership:** a Mobile can sit in one `MobileTable` under one key only (`Mobile.OwnerTable`, set on insert and overwrite, cleared on remove, replace and `Clear`). A second key or table throws `InvalidOperationException` before any change; the same mobile under its own key is a no-op. This keeps `Ordinal` (the storage address) trustworthy. The audit's sequence (`t[1]=a; t[2]=a; Remove(1); Add(b); Remove(2)`) now throws at `t[2]=a`.
- **P2, enumeration:** an insert of a new key bumps a version that enumerators capture and check in `MoveNext` and `Reset`, throwing like the dictionary did. Removal, overwrite of an existing key, `Clear` and a refused duplicate `Add` do not invalidate.
- **P3, `FindNext`:** `AddRange(Mobiles.Values)` replaced by a concrete `foreach` with capacity reserved. No other `ToList`, LINQ or `AddRange` over `Mobiles.Values` exists (grep).
- **P3, checker:** the shadow comparison now runs every frame under `CUO_MOBILE_UPDATE_CHECK`, whichever mobile loop runs (E4 off included), counts `table checks`, and compares key-to-value mappings and `OwnerTable` as well as order and ordinals.
- **P3, tests (586 pass, 4 new):** ownership refusals (aliases, two tables, the audit sequence, release on remove/replace/Clear); Clear and remove-all then repopulate past the first array size against a dictionary; the full death re-key with `Serial` updated (also in the random model) and a re-key onto an existing key; enumeration with removal of an unvisited entry, overwrite, `Clear` and insert invalidation (and a refused duplicate not invalidating). The E4 random order test now takes its expected order from an independent `Dictionary` oracle fed the same operations.
- **Checks on the fix** (kbatman3, TieredCompilation=0): large scene `ls_parity` with `CUO_MOBILE_UPDATE_CHECK=1`, **E4 off**: table checks 22,375, 0 differences; parity 10.4M mobile comparisons 0 mismatches; `-ecscheck` 0. `w5push` with E4 on and all checkers: table checks 241,343, 0 differences; clocks 3.07M 0; skipped 2.84M 0; order 238k 0; input check 1.57M and 1.45M seat tests 0; parity 1.6M 0. Default and `-p:CuoDevTools=false` builds both build.

## T0a/b: ECS state census and a write-site budget test (2026-10-30)

Base: poc/modern-client 3681f25b3. Branch `bg/ecs-t0-census`. Task 1 of ecs-transition-plan.md section 6. No product code changed; no client run.

**Census.** [ecs-state-census.md](ecs-state-census.md) classifies every member of `GameObject`, `Entity`, `Item`, `Mobile` (with `MobileAnimation` and the views) and `PlayerMobile` as A (world state), B (derived), C (view scratch), D (session/UI) or E (structure), lists every write site of the 41 A fields (file:line, receiver type) and their readers split hot and cold, the mutator methods and the tile-list, child-list and `Steps` writers, and every holder of a world-object reference across frames. The lists are **compiler-derived**: each A member was renamed in a scratch copy and the errors of `dotnet build` give every reference with its receiver's static type (a grep cannot tell `item.X = 1` from `control.X = 1`; `X` and `Y` have about 1,000 textual hits). The sources were reverted afterwards. Every textual `.Field op` hit in the client (1,136) is either in the world write list or in the census' appendix of non-world hits; the audit script found 0 missing (it also sampled 20 random hits: all listed).

**Numbers.** 266 world write sites in 14 files (`Graphic` 33, `Hue` 17, `Flags` 15, `X` and `Y` 14 each, `Z` 12, `Offset` 25 member writes); 121 are in `PacketHandlers.cs` (31 in the shared `UpdateGameObject`), 66 inside the entity classes, 34 in the character-creation gumps (detached preview objects), 24 in `BoatMovingManager`, 12 in `DevBench`. 32 more writes are on map and effect objects. About 2,270 reads, 9 per write. Findings for T0c and T1 are in section 9 of the census: `Offset` is a struct written member by member; `DisplayedGraphic` has no world writer; five `Graphic` writes skip `CheckGraphicChange`; `BoatMovingManager` is a second position writer; `ContainerGump` writes contained items' `X`/`Y`.

**Budget test.** `tests/ClassicUO.UnitTests/Ecs/WriteSiteBudgetTests.cs` scans `src/ClassicUO.Client/**/*.cs` with a regex scanner (comments, strings and char literals blanked; the receiver type guessed per file from declarations, `var` producers, casts and call receivers) and compares counts per (file, field) with `WriteSiteAllowlist.txt` (130 pairs, 326 sites). It fails when a pair grows or a new one appears and names the new sites, and also when a count has shrunk (a downward ratchet, so a removed write cannot free budget for a new one; the allowlist is lowered in the same change; `CUO_WRITE_BUDGET_REGEN=1` rewrites the allowlist, `CUO_WRITE_BUDGET_DUMP=<file>` dumps the sites). The repository root is found by walking up from the test binary to the folder holding `src/ClassicUO.Client/ClassicUO.Client.csproj` and `tests/ClassicUO.UnitTests` (`CUO_REPO_ROOT` overrides); a sanity test fails if the scan finds fewer than 200 sites. Against the compiler list the scanner finds all 266 world sites and the 16 effect-object sites, plus 5 false positives (same-named locals of another type) and 6 `Steps` mutator calls; its limits are census section 8. A copy of the tree with a new `item.Hue = 1; m.X++;` made the test fail with those two sites named.

**Verification.** 623 tests pass in both variants (default and `-p:CuoDevTools=false`; 34 of them are new: the scanner's positive and negative cases, locals, bare writes, the allowlist round trip, the live scan). The Client builds in both variants with 0 errors. The new test reads sources only, so it behaves the same in both variants.

**Environment notes.** A fresh worktree needs `git submodule update --init --recursive` before it builds (FNA and its nested libraries are empty otherwise). About 70 minutes were spent waiting for `bench.lock`, which other agents kept re-taking within a second of each other; the census builds and test runs themselves took a few minutes.

**Audit fixes (Codex gate, 2026-10-30).** The scanner now also counts receiverless `++X`, `ref X` and `out X` (for example `Interlocked.Exchange(ref LastAnimationChangeTime, 0L)`), tuple deconstruction, and the 18 private A fields (animation timer, `_lastAnimationIdleDelay`, `_isSA_Poisoned`, the setters' backing fields); fixtures cover each form, `?.` assignment and the `out Direction d` type-name false positive. A stale budget now fails (the same-pair swap limit is documented). The sanity test became a discovery test (expected source files enumerated; no minimum counts). The census names construction writes (field initialisers, section 3.1a) separately from the 266 mutation count, and its claim about `?.` assignment is corrected. The census scripts are committed under `tools/ecs-census/` with a README (repo root through `CENSUS_ROOT`; the pipeline was re-run from the saved build logs and reproduced the document). Roslyn was considered and not used: a syntax-only scan has no receiver types, and a semantic one needs the whole client compiled inside a unit test (census section 8, limit 8).
## First-sight item and multi art on the art workers (2026-10-30)

Branch `poc/item-art-worker` (item art agent, kbatman4), from poc/modern-client 9312ec338, merged up to de98a6d1a; the final numbers were measured on 7cbda7d68. This follows "Teleport frames: first-sight chunk art decoded on workers", which left 11–25 main-thread decodes in one `travel` window per run, outside the map statics. Method: [testing/pack-loading.md](testing/pack-loading.md) §3, §4 and the new paragraph on item art. Scripts and logs are in the session scratchpad `itemart\`.

**What decoded on the main thread (measured first).** A JIT diagnostic build logged every main-thread art decode with its callers (`CUO_ART_SYNC_LOG=1`, now committed). It ran Gpu and Legacy sessions with `travel`, `glide`, the bank, the KamDos house and Britain:
- **Gpu, 130 decodes:**
  - 90 came from the **item pick test** (`Item.CheckMouseSelection` in the draw walk). A ground item that hasn't been drawn yet has no stored draw offset (`ArtEntry.Width/Height` 0,0), so the cheap reject was skipped and `GetArt` decoded the art of every new item, on or off screen. In the Gpu world nothing else needs these items' atlas art: the world draws them from the pack.
  - 39 came from **animated statics' frames** (`Static.Draw` through `GpuWorldRenderer.DrawObject`). The chunk scan asked for the graphic the block names; the view draws graphic + the frame offset.
  - 1 was the paperdoll's equipment icon (UI art, out of scope).
- **Legacy, 146 decodes:** the same split (111 pick, 34 animated static frames, 1 UI). The pick test runs before the draw, so the Legacy draw then finds the art in the atlas.
- **Multis:** none appeared in a measured window. Their art decoded during the settle hold of the teleport that brought them into view, which FrameStats windows don't count.
- **Corpses** draw from the animation path, which already decodes on workers.
- **Cost in NativeAOT** (the base runs below): 48–56 decodes and 0.9–1.3 ms per `travel` run, 26–29 decodes and 0.66–0.73 ms per `glide` run. The worst frame spent 0.21–0.27 ms decoding. The 2.6–3.0 ms quoted before came from JIT builds.

**Design.**
- **Items arrive after their chunk is built, so nothing waits for them.** A ground item (`Item`, on the ground, not a corpse or a multi) and a multi component (`Multi`: house or boat parts) request their art when they are placed on the map (`GameObject.AddToTile(x, y)`, parked or not, through `FirstSightArt.RequestPlaced`). The request covers the graphic the view draws (`DisplayedGraphic`: coin and stack variants) and every animation frame (AnimData offsets). A multi component also requests its new graphic after a season change. Hues don't matter: art is decoded unhued.
  - In practice the art is in the atlas before anything needs it. Items that arrive in a teleport's packet burst are parked until their chunk loads (Map.Park), and the chunk waits for its own art. Legacy, which draws items from the atlas, had 0 main-thread decodes after the change.
  - Where art is still pending, the old lookup still works: it decodes on the spot and the worker's result is dropped. Nothing draws a placeholder, and nothing new is held.
- **Pick test.** When an item has no stored draw offset yet, the test stores the one the view and `ItemSpriteExtract` store when they draw it, computed from the pack's sprite size (`OrigW/OrigH`). The reject then works from the first frame, 44×44 art included (its offset is 0,0).
  - An item outside the reject rectangle never needs its art. One the reject doesn't exclude is still tested against its exact pixels, decoding on the spot if it must, so selection is exactly as before: at most the few items under the mouse decode.
  - A rejected item whose art is missing (a graphic changed in place, an atlas reload) is requested from the workers then.
- **Animated statics:** the chunk scans (block scan and loaded-chunk walk) wait for the frame shown now and request every other frame in the full scan.
- **Settle hold:** unchanged and honest. Nothing new is skipped in the draw walk. The Gpu world draws ground items from the pack (`ItemSpriteExtract`); multi components still go through `Multi.Draw` (`GpuWorldRenderer.DrawObject`, whose direct extract handles only items and mobiles), so a component whose art is still pending decodes on the spot there, as before.
- **Audit lessons kept:**
  - A failed worker decode is terminal (`RequestArt` treats it as ready).
  - Requests cost O(1); the extra frame requests run only in the full block scan and in the loaded-chunk walk, which already runs only after uploads.
  - Uploads stay inside the 2 ms chunk budget (`DrainReady` in `Map.UpdateLoading`).
  - `GameScene.Unload`'s `CancelPending` covers these decodes.
  - The season generation already rescans statics; multi components re-request on season change.
- **Switches:**
  - `CUO_ART_SYNC=1` (or `CUO_CHUNK_SYNC=1`, which turns off the drain) turns all of this off, including the pack-sized pick reject: the old path.
  - FrameStats counter `art_obj`: placements whose art was requested.

**Measurements.** NativeAOT, `DOTNET_TieredCompilation=0`, Gpu world, 1920×1080, plan `house;bank;travel;ltravel;glide;far` (4 windows of 5 s). The character starts every run at Jhelom (the `far` entry), so the house and bank are cold teleports. Base is poc/modern-client de98a6d1a and new is 7cbda7d68, both built from the same tip, run alternated A–C: base, new, new, base, base, new. The machine was busy (eight agents share it; one other client during baseA). Main-thread art decodes come from FrameStats `art_sync`/`art_sync_us`, window sums:

| Entry | Base decodes / ms (per run) | New | Base U+D max (ms) / >8 | New U+D max / >8 |
|---|---|---|---|---|
| `travel` | 48–56 / 0.87–1.27 (worst frame 0.21–0.27 ms) | **0 / 0** | 4.9, 6.0, 10.2 / 0, 0, 1 | 5.1, 5.1, 5.9 / 0 |
| `ltravel` | 1–3 / 0.04–0.11 | **0** | 2.8–8.1 / 0–1 | 3.0–7.7 / 0 |
| `glide` | 26–29 / 0.66–0.73 | **0** | 2.1–4.3 / 0 | 2.2–4.7 / 0 |
| house, bank, Jhelom | 0 in the windows (decoded during the hold) | 0 | ≤ 2.1 | ≤ 2.3 |

- **Whole session, JIT diagnostic builds** (counting the login and the settle holds too, from the `[artsync]` lines):
  - Gpu: base 130 main-thread decodes, new 1 (the paperdoll icon);
  - Legacy: base 146, new 1.
- **Every frame over 7 ms in both builds is a GC**: the login warm-up's compacting gen1 (6.7–7.6 ms, `[gc]` lines), in `ltravel` or `travel`. Base's two frames over 8 ms (baseB `ltravel` 8.1, baseC `travel` 10.2) are both that collection; new had none over 8 ms in three runs. That depends on where the collection lands, not on this change.
- **Uploads:** `art_obj` was 446 requests over the three new runs (up to 67 placements in a frame after a teleport). `art_upload_us` peaked at 1.7–2.0 ms (base 1.0–2.0), inside the chunk budget as designed; `art_pending` peaked at 263–272 in both builds.
- **Settle:** every run had 0 timeouts and 0 unsettled frames in an episode. Walking-unsettled frames: base 113–147, new 113–143.

**Verification** (new binary unless noted):

| Check | Result |
|---|---|
| `CUO_GPU_SKIP_CHECK=1 CUO_PREP_CHECK=1 CUO_GPU_ITEM_CHECK=1` with `pickscan:8x6` at the house, the bank and Britain, plus `travel`, `ltravel`, `glide` and Britain at zoom 2.5 | Prep check: 0 wrong positions and **0 wrong item-pick rejects** (up to 187k rejects per window, now including first-sight items). Item check: 0 with differences. Skip check: 0 unsettled, 0 drawn differently. Pickscan: 48 same, 0 different at all three. Settle: 0 timeouts, 0 unsettled in an episode |
| Season (`season:winter`, `ltravel`, `glide`, Britain, the house, `season:summer`, Britain) and facet change (`[self set map felucca`, `trammel`, with `pickscan:8x6` on each) | 0 timeouts, 0 unsettled in an episode, 0 main-thread art decodes. Facet ready after 68 and 53 ms. Pickscan 48 same, 0 different on both facets. Looked at the shots: the winter house (snow, bare tree, house complete) and summer Britain (complete) |
| ECS parity: `CUO_ECS_PARITY=1 CUO_ANIM_SYNC=1 -ecscheck 120`, driver `parity-travel.txt` (`[go` teleports between towns with walks, `light 1E` then `light off`), plus the item and skip checks | Items: 343k compared, 0 mismatched, 3,516 lights with 0 mismatched. Mobiles: 181k, 0 mismatched. `-ecscheck` 0 mismatched in 23 checks. Item check 0 |
| Legacy (`-worldrenderer legacy`, base against new: the house, the bank, Britain, after `travel`, after `glide`, Britain again, Jhelom) | Identical (0.00% of pixels differ by more than 8) apart from the bank (0.42%) and Jhelom (0.12%). Looked at the bank: only NPC positions differ; the counters and furniture are identical. Near-black 1.2–2.4%, the same in both |
| `CUO_ART_SYNC=1` on the new binary, with the prep check | The old path: 579 main-thread decodes in `travel`, 0 worker uploads, 0 wrong rejects, 0 timeouts |
| Unit tests | 590 pass in both build variants (`-p:CuoDevTools=false` too); 626 after merging poc/modern-client 80ccb1a24 (the T0 write-site budget test passes). New: `ItemPickBoundsTests` decodes about 870 pack art sprites, including 200 that are 44×44, and checks that the reject from the pack size never drops a pixel the picker would hit, the stack's second sprite included |

- **A unit-test catch from the merge:** E11's `SetInWorldTileSyncTests` places items without a game. `FirstSightArt` now does nothing when no art is loaded (a null check, fixed after the measurements).
- `ChunkLoadingTests.Prefetched_Regions_Give_The_Same_Blocks_And_Never_Block_IsBlockReady` failed once while the machine was busy (2.65 ms against a 2 ms limit) and passed on reruns, as E10 saw.

**Not done or noted:**
- **No boats and few houses on the local server.** Multis were checked with KamDos (`house:1427 1691`) only.
- **Correction (Codex audit):** an earlier version of this entry said house components had no atlas art in the Gpu world. That was wrong: they reach `Multi.Draw` through `GpuWorldRenderer.DrawObject`, which decodes their art and stores the pick anchor on the art entry. The real change was a regression, fixed below: the prefetch could fill a component's pixel mask before its first draw had stored the anchor.
- **Placement requests sit outside the chunk budget, and the bytes of decodes in flight are capped by a safety net (DecodeBudget, 16 MiB; see the decode in-flight cap entry).** Requests are O(1) and the uploads are budgeted; a teleport into a town queues every new item's and component's decode at once, each holding its buffer until its upload. The feared ~76 MiB untrimmed worst case did not show up: the measured all-time peak in flight is about 7.3 MiB.
- **After `Art.Reload`**, multi components aren't re-requested. A Legacy draw then decodes their art on the spot, as before.
- **UI art** (gumps, the paperdoll's item icons) still decodes on the main thread.
- No 4K run, no 13-minute session, and not the owner's FPS test.

**Codex audit fixes** (no P0 or P1 found; object lifetime, worker safety, generations, item bounds and Gpu readiness checked out):
1. **Multi pick anchor (P2, a regression from this branch).** The prefetch filled a component's pixel mask before the first draw stored `ArtEntry.Width/Height`. Picking runs before drawing, and components the mesh build skips (`CHMOF_DONT_REMOVE`) had their first pick at anchor (0,0). The wooden wall 0x0006 (44×106) was 62 px off: false hits and misses. `MultiView.CheckMouseSelection` now tests the sprite `Multi.Draw` draws, with its anchor computed from the pack's size (`Multi.SpriteTopLeft`, the anchor `DrawStaticAnimated` stores). The same change fixes two older mismatches between pick and draw: the pick now uses the current animation frame (graphic + `AnimOffset`) and the boat's `Offset`. New test: wall 0x0006's drawn pixels all pick, where the old anchor missed most of them.
2. **A parked seasonal component missed a season change (P2, inherited).** `World.ChangeSeason` only visits loaded chunks, so a component parked on an unloaded chunk kept the old season's graphic. `Map.LoadChunk` now refreshes the seasonal graphic of each parked object it attaches (a no-op for items and mobiles), which also requests the new art.
3. **Logout race in `FirstSightDecoder` (P2, inherited).** A worker could pass its epoch check, then `Cancel` bumped the epoch and drained an empty queue, then the worker queued its buffer, which stayed held until the next session's `Drain`. The epoch check and the publish now run under a lock that `Cancel` takes too. New test at that boundary: `Cancel` waits for the publish and drains the job, so no buffer stays queued.
4. **The animated frame waited for (P3).** The chunk loader (in `World.Update`) read `AnimOffset` before `AnimatedStaticsManager.Process` advanced it, so the frame drawn could be the next one, still pending. `Process` now runs before `World.Update`.
5. **Docs:** the multi explanation and the two notes above.

**Checks on the fixes** (c68c63985, NativeAOT):
- **Unit tests:** 628 pass in both build variants. `ChunkLoadingTests.Prefetched_Regions…` hit its timing limit once more under load (2.8 ms) and passed on the rerun.
- **One run,** with `CUO_PREP_CHECK=1 CUO_GPU_ITEM_CHECK=1 CUO_GPU_SKIP_CHECK=1`: the house with `pickscan:8x6`, `travel`, `season:winter`, the house with `pickscan:8x6`, `glide`, `season:summer`, the house with `pickscan:8x6`, Jhelom.
  - Pickscan: 48 same, 0 different, all three times.
  - Prep check: 0 wrong. Item check: 0 with differences. Skip check: 0 drawn differently.
  - Settle: 0 timeouts, 0 unsettled in an episode. 0 main-thread art decodes in every window.
  - The worst frame (9.4 ms, `travel`) is the login warm-up's compacting gen1. The run had the checks on, so its frame times aren't a measurement.
  - The Jhelom entry also had one 7.5 ms frame, almost all of it `prep_foliage_finish` (7.06 ms), in its second window rather than at the teleport.
    - A pair with the checks off (same `house`, `travel`, `far` entries, base `bin-base` against `bin-fix`) had no such frame: Jhelom's worst frame was 1.45 and 1.48 ms in base, 1.38 and 1.67 ms in fix.
    - Over every run of this branch, `prep_foliage_finish` went above 1 ms only in the two runs with `CUO_PREP_CHECK`, `CUO_GPU_ITEM_CHECK` and `CUO_GPU_SKIP_CHECK` on (3.05 ms in `chk1`, 7.06 ms here); it never did in the 11 runs without them.
    - So it looks like checker overhead, which these phases include, not a product cost. Which check causes it wasn't narrowed down.
- **ECS parity after the frame reorder (fix 4),** with `tools/ecs-parity/e7mixed.txt` (kbatman3, Green Acres: ground items, animated statics, a corpse and mixed served tiles), extended to compare by day too (`parity`, a walk and `check` after `light off`). Run with `CUO_ECS_PARITY=1 CUO_ANIM_SYNC=1 -ecscheck 120`, the item and skip checks, on `bin-fix`:
  - items: 844k compared, 0 mismatched, 49,087 lights with 0 mismatched;
  - mobiles: 131k, 0 mismatched;
  - `-ecscheck`: 22 checks, 0 mismatched;
  - item check: 0 with differences; skip check: 0 drawn differently, 0 unsettled;
  - the spawned items and the brigand were wiped by the script.

## E4 default-on gate (2026-10-30)

Branch `bg/ecs-e4-gate`, from poc/modern-client b9f9419db (E10's `MobileTable` merged; no `src` change since 59e1c6da3). No code change. Account kbatman3 (the character-creation run used a new account, kbatman5), Gpu, 1920x1080 except where noted. Binaries: `bin-jit` (Release, `DOTNET_TieredCompilation=0`) and `bin-aot` (devtools NativeAOT publish), both from b9f9419db, in the session scratchpad `e4gate\`. `world_update_mobile_system` on (settings.json) in every checker run. Unit tests: 625 pass.

**Checkers, JIT** (`CUO_ECS_PARITY=1 CUO_ANIM_SYNC=1 CUO_GPU_ITEM_CHECK=1 CUO_MOBILE_UPDATE_CHECK=1 CUO_W5_CHECK=1 CUO_MOBILE_INPUT_CHECK=1 CUO_MOBILE_CACHE_CHECK=1 CUO_PREP_CHECK=1 CUO_GPU_SKIP_CHECK=1`, `-ecscheck 120`). Every failure count in every run below is 0 (clocks different, skipped updates that changed state, due updates out of order, table-vs-dictionary differences, parity mismatches for mobiles and items, input check and seat tests, item check, w5-check, mobile-cache-check, prep-check wrong, skip-check, `-ecscheck`):

| Run | table checks / clocks / skipped / order | parity mobiles / items | input samples / seat tests |
|---|---|---|---|
| large scene `ls_day` (`light off`; standing, lane walked and run) | 8,181 / 14.37M / 13.69M / 685k | 5.87M / 2.80M | 5.64M / 5.10M |
| large scene `ls_night` (`light 1E`; lights=312k exercised) | 8,101 / 14.45M / 13.74M / 710k | 5.76M / 2.72M | 5.63M / 5.09M |
| `w5push` day | 309,526 / 3.39M / 3.18M / 205k | 2.03M / 640k | 1.95M / 1.61M |
| `w5push` night | 343,321 / 1.80M / 1.71M / 89k | 1.33M / 391k | 1.24M / 937k |
| `w5items` day | 143,414 / 346k / 316k / 30k | 296k / 501k | 288k / 228k |
| `w5items` night | 117,661 / 286k / 264k / 22k | 251k / 435k | 240k / 193k |

The `w5push`/`w5items` scripts are the repo ones with `light off` or `light 1E` inserted after the first line; in Green Acres no lights are in range, so `lights=0` there (the night lights path is exercised by the large scene).

**NativeAOT** (devtools build, the same checkers; the devtools variant is needed for `mobsys:` and FrameStats, `CUO_MOBILE_UPDATE_CHECK` itself is not devtools-only): `ls_day`, `ls_night` and `w5push` day, all checkers 0 (table checks 20,757 / 7,841 / 252,356; clocks 11.8M / 13.8M / 2.35M; parity 4.90M / 5.43M / 1.47M mobile comparisons 0 mismatched; lights 302k and 331k at night and 6k in `w5push`).

**Character creation** (once, JIT build, checkers on, via synthetic mouse and keyboard input, `tools` scratch `cc.py`): a new account (kbatman5) logs in to the character-creation gumps, the preview mobile (human male) is on screen, name typed, Next, Warrior, New Haven, Next: the character is created and the world loads (Trammel 3503 2574). Afterwards 65,111 frames: clocks 186,940 checked 0 different, skipped 182,113 0 changed, order 4,827 0, **table checks 65,111, table-vs-dictionary differences 0**, input check 66,664 samples 0, parity 66,380 mobile comparisons 0 mismatched, `-ecscheck` 0 in 571 checks. `World.Update` does not run during character creation, so the preview mobile only exercises `MobileTable` add and the `LoginScene` clear; the first game frames show the table matches the shadow dictionary afterwards. **A character "Gatetest" now exists on account kbatman5** (left in place).

**Soak** (NativeAOT, E4 on, `CUO_MOBILE_UPDATE_CHECK=stats`, about 13 minutes of plan: large scene zoom 1 and 2.5, the lane walk, `travel` (server teleports Britain to the towns every 2 s), `glide`, `ltravel`, repeated; 126 FrameStats windows): no exception; `dynamic dropped 0` throughout; 840,963 frames, 10.07M clock pushes. Private bytes (sampled every 10 s): 1,915 MB at 1 min, 2,199 at 2, 2,276 at 3, then 2,277 (4), 2,286 (5), 2,294 (6), **2,347 (7), 2,345 (13)**; working set 1,269 MB at the end. Flat after the 3-minute warm-up: +50 MB from minute 4 to 7 (the first travel/glide caches), and 0 from minute 7 to 13. This is a 13-minute run, not a leak proof; the plan is sequential teleports plus the scene, not login-heavy crowds.

**Timing: setting off vs on** (NativeAOT devtools, in-session `mobsys:off`/`mobsys:on` alternation, 3 windows of 5 s per entry, each pair twice per session, 3 sessions, so n=6 per cell; ms; FrameStats on; machine noisy but the pairs are in-session; ranges are the 6 cell averages):

| Spot | `world_mobiles` off | on | `update` off | on | `frame` off | on |
|---|---|---|---|---|---|---|
| zoom 1 | 0.041 (0.036-0.043) | **0.020** (0.017-0.022) | 0.131 | 0.112 | 1.595 (1.53-1.65) | 1.601 (1.52-1.67) |
| zoom 2.5 | 0.044 (0.041-0.049) | **0.024** (0.021-0.027) | 0.153 | 0.126 | 2.079 (2.00-2.23) | 1.997 (1.89-2.09) |
| walking the lane | 0.040 (0.038-0.043) | **0.022** (0.021-0.023) | 0.163 | 0.141 | 1.935 (1.89-2.02) | 1.926 (1.87-1.99) |

On halves the mobile loop (about -0.02 ms) and takes 0.02-0.03 ms off `update`; the whole-frame difference is inside the noise (zoom 2.5 -0.08 ms, the others ±0.01), as E4 and E10 found. The frame's cost is the GPU world prepare, not this loop.

**Proposal.** Turn `world_update_mobile_system` on by default. All checkers were 0 over about 60M clock comparisons, 60M skipped-update comparisons, 3M due-update order checks and about 1.3M table-vs-dictionary comparisons, in JIT and NativeAOT, by day and night, in the large scene, with mounts, deaths and corpses (`w5push`, `w5items`), through the character-creation preview mobile, and over a 13-minute travel-and-crowd soak with flat memory; E4 passed its Codex audit and E10's table passed its own, and the unit tests pass (625). The gain is small (0.02 ms of the mobile loop, 0.02-0.03 ms of `update` in a 2,000-mobile scene, nothing measurable in the frame), so the case for the switch is not speed but that the ECS clock becomes the thing the world runs on, which E4's later steps (selection against ECS state, then a chunk-iterating system) need; the risk side is the due-time logic being the only reader of the clocks, so keep the checker (`CUO_MOBILE_UPDATE_CHECK`) in the standard gate and keep the off path for one phase, as the plan says. What the gate did not cover: a live 0xBB serial change (unit-tested only), a multi-hour session, and login-heavy crowds. If the owner wants those first, the switch can wait; I see no failing evidence against flipping it.

## Gumps in the pack (2026-10-30)

Branch `poc/pack-gumps`, from poc/modern-client fdfcc9813, merged up to 80ccb1a24 (timings on de98a6d1a; 80ccb1a24 adds only tests and tools). The first "Not in the pack" content type to move. Spec: pack-format.md "Record stores" and "Gumps"; procedures: testing/pack-loading.md §6. Scripts, packs and logs: session scratchpad `pgumps\`.

**Design (evidence in pack-format.md).**
- **Storage: a record store, not atlas pages.** `gumps/gumps.bin` is one Kind 0 file: header, a slot per id (record + gump.def hue), a record table (offset, sizes, codec, width, height, layout), then one independently decodable payload per distinct image. It is read by offset through one handle kept open (L7: a per-file open costs 20–28 µs). Gumps go through the CPU into the batcher's RGBA8 atlas, so pages would buy no GPU residency and would cost gutters, 1 MiB chunk reads (a 600-row gump touches 3 chunks) and 2× the raw bytes (289 MiB).
- **Lossless 16-bit:** texel = the source colour bits plus bit 15 = visible. ClassicUO's decode and `GetColor16` ignore bit 15, so every image and hue result is reproduced; the hue rule (shade = red5, visible only if the hued value is not 0) is the client's.
- **Compression, per record.** Images with at most 64 KiB of run-length payload (3,915 of 4,290 records) are stored as the client's run-length rows, uncompressed (decode 0.6–33 µs). The 375 larger ones are raw texels + Brotli q9, decoded in place into the pixel buffer (texels in its upper half, expanded front to back), with no other buffer. Per size bucket over every gump (`--gump-report`), Brotli has a fixed cost of 50–70 µs that dominates small images. A 16 KiB threshold (47.5 MiB) left the journal, status and spellbook opens 0.1–0.7 ms slower than MUL in the client; 64 KiB fixed that for 10.7 MiB.
- **Size table:** the record table. `TryGetSize`, `Exists` and `PixelCount` need no decode. Controls still size themselves from the synchronous `GetGump`; existence probes that decoded only to test `Texture != null` (paperdoll gender fallback, tileart appearance) now use `Exists`.
- **gump.def** and loose `Gumps/*.gump` files are resolved by the converter (a frozen copy of `GumpsLoader` in tools/ClassicUO.Assets.Mul): aliases share records and carry their hue on the slot. The 7.0.103 gump.def has 4 hued aliases (hue 35). With a gump section the runtime no longer reads loose gump files (as with art).
- **Hit-testing:** the picker mask is built from the decoded image at upload (`SetTrimmed` over the whole image, which equals `Set`'s mask, with SIMD).
- **Decode on workers:** decodes are thread-safe (positional reads, `BrotliDecoder.TryDecompress`, pooled buffers). Gump sizes come from the image synchronously and a window is built and drawn in the frame it opens, so a plain async request rarely beats the first `GetGump`. Instead, a window built from many gumps decodes them on workers in one batch and waits for the slowest (`Gump.Prefetch`: server gump layouts' gumppic, button, checkbox, radio, gumppictiled and resizepic pieces; the paperdoll's body and equipment). `Request` and `DrainReady` (1 ms per frame) remain for ahead-of-time requests. `CUO_GUMP_SYNC=1` turns the worker paths off; `CUO_PACK_GUMPS=0` reads MUL/UOP even with a gump section.
- **Fallback:** a pack without `gumps/gumps.bin` reads gumpart MUL/UOP exactly as before, and says so in the log.
- **Status gump:** a store flag keeps `UseUOPGumps` (the status gump's layout depends on it). 7.0.103's UOP has gumps above 0xFFFF (highest 69,985), so the slot table has 69,986 entries.

**Converter.** A `gumps` stage (`--only gumps`, `--no-gumps`, `--raw-gumps`, `--gump-small n`, `--gump-def <file>` for tests, `--gump-report`). 4,438 gumps → 4,290 records (16 index/gump.def aliases, 132 identical images shared) in 2.0 s on 12 threads; `--only gumps` with verify takes 3.9 s. Verify compares every one of the 81,919 index slots with the frozen loader's `GetGump`: presence, size, RGBA pixels with hue. EXACT for the ship profile, `--raw-gumps`, and a test gump.def (10 aliases, hued and chained, an out-of-range hue, a missing first group member, an alias over an existing gump).

**Sizes:** 58.2 MiB in the ship profile (123.5 MiB of payloads; tables 0.6 MiB); 37.9 MiB with every record Brotli (`--gump-small 0`); 133.7 MiB uncompressed (`--raw-gumps`). The source gumpartLegacyMUL.uop is 137.7 MiB.

**Measurements.** NativeAOT, Gpu world, 1920×1080, kbatman4 at Britain, plan mode `gumpopen` (each window opened once in a fresh client; logs the main-thread decode time, the batch prefetch wall time and the open call). Same binary, paired: pack (P), `CUO_PACK_GUMPS=0` (M, MUL/UOP), pack with `CUO_GUMP_SYNC=1` (S). Two runs each, interleaved, no other client running, 64 KiB threshold (runs P5/P6, M5/M6, S5/S6). Decode plus prefetch wall time, ms:

| Open | Gumps decoded | Pack | MUL/UOP | Pack, no workers |
|---|---|---|---|---|
| 6 large pictures (0x2C00, 0x067E–0x0682, 657–670²), a server-gump-style window | 6 | **3.0–3.4** (batch on workers) | 11.4–11.5 | 12.0–12.1 |
| Spellbook pictures (0x08AC, 0x08BA, 0x08BB) | 3 | 0.75–0.78 (batch) | 0.69 | 0.69–0.79 |
| Status (modern) | 3 | 0.72–0.78 | 0.64–0.77 | 0.80–0.88 |
| Backpack | 1 | 0.34 | 0.36–0.38 | 0.29 |
| Combat book | 34 | 1.09–1.12 | 1.00–1.03 | 1.04–1.15 |
| Journal | 10 | 0.41–0.42 | 0.43–0.44 | 0.41–0.44 |
| World map | 0 | open call 2.0–2.3 | open call 2.0–2.5 | open call 2.1–2.5 |

- **Frames over 8 ms:** the large-picture open costs 3 ms of decoding with the pack and 11.4 ms with MUL; only with MUL is that frame over 8 ms. Every other window costs under 1.2 ms of decoding either way. The windows that had a frame over 8 ms had it in every mode: the world map's first open (`lights`/`world_draw` 10–50 ms) and the end-of-plan picker check, not gump decoding. DevBench's own frame-to-frame "worst frame" showed 500 ms stalls in every mode (the OS not scheduling the window, as in pack-loading.md's noise notes), so it is not used.
- **The player's paperdoll** opens at login (the server's paperdoll packet), before any measurement window, so its first open isn't in the table. A request for another mobile's paperdoll got no reply within 1.5 s. The paperdoll path is covered by the parity checks, the screenshots and the prefetch code path (`PaperDollInteractable`).
- **Startup:** `GumpsLoader.Load` takes 0.5 ms and allocates 648 KB from the pack, against 25.5–26.2 ms and 13.8 MB from UOP (81,919 formatted names hashed, extras read across the 144 MB file). "Files loaded" was 130–143 ms in every mode and 131–154 ms in base (B1/B2): noise hides the 25 ms.
- **Memory:** private bytes sampled each second are dominated by GC timing (0.7–2.5 GB at exit within one mode); the peak was 1.95–2.68 GB in every mode and in base. No gump-related difference is measurable this way; the loader's own allocation above is the gump share.
- **The 16 KiB pack** (P3/P4 against M3/M4): journal 0.79–1.17 ms against 0.45–0.49, status 0.68–0.98 against 0.59–0.62. That is why the threshold is 64 KiB.

**Checks.**
- **Unit tests** (`GumpPackTests`, 7 tests; the data tests return early without `CUO_TEST_GUMP_PACK`): every gump id against the client's MUL/UOP `GumpsLoader` (presence, size table, pixels into a dirty pooled buffer, the UOP flag); 9 hue kinds on 40 gumps (valid, last, equal to the hue count, 0x8000, 0xFFFF); the custom gump.def pack end to end; picker masks (`SetTrimmed` equals `Set`) on 400 pack gumps; decodes from 8 threads; codec round trips (a 70,000-wide row; malformed data rejected); record store round trip, rejection of a bad magic and of a truncated file. Run against the ship, `--raw-gumps` and hued packs.
- **Screenshots** (`gumps;eqhue:0021,8481,0035,0480;s:1495 1629:1`, pack against `CUO_PACK_GUMPS=0`): the world shot has 0 differing pixels. In the UI shot 6,883 pixels differ, all inside the journal box, where the live timestamps read 13:21 against 13:22. Looked at both: paperdoll (robe hued 0x8481, partial hue), backpack, status, journal and top bar are identical.
- **Picking in the client:** `[bench-gumppick]` hashes each gump's picker on a 2-px grid (paperdoll, spellbook, both status gumps, backpack, 0x2C00, a resizepic corner); they are equal in pack and MUL runs. Click-through: a transparent paperdoll texel hits nothing and a visible one hits the GumpPic, in both modes.
- **Build variants and tests** (after the merge of 80ccb1a24): 632 unit tests pass in the default build and in `-p:CuoDevTools=false`, with the gump data tests run against the ship and hued packs (and the 7 gump tests against the `--raw-gumps` pack). T0's write-site budget caught the first version of `eqhue:` (a second `Hue` write in DevBench); it now shares `itemhue:`'s write site. `ChunkLoadingTests.Prefetched_Regions_Give_The_Same_Blocks_And_Never_Block_IsBlockReady` failed twice on a first run (3.07 ms against a 2 ms bound, a timing test on a loaded machine) and passed on reruns, as E10 saw.
- **Codex audit fixes** (packs are untrusted input):
  1. **Dimensions (P1):** each record is checked at load (`GumpsLoader.CheckRecord`): sides 1..4096, so `w·h` and byte counts can't overflow and every image fits the 4096² gump atlas. `TextureAtlas.AddSprite` now rejects a rect that can't fit an empty page instead of creating pages forever.
  2. **Raw sizes (P1):** layout 0 must be exactly `w·h·2`; layout 1 whole runs between the row table and one run per texel; a Brotli record no larger than Brotli's bound. Nothing is rented before these checks. `RecordStore.Read` refuses sizes above `int.MaxValue` and a short destination.
  3. **Store extents (P2):** the tables must fit in the file before they are allocated (a 32-byte file claiming 2²⁴ slots is rejected having allocated under 1 MiB); payloads must lie in `[payloadStart, fileLength]` and be compared without wrap-around.
  4. **Corrupt payloads (P2):** data and I/O errors are caught at the loader. The record becomes invalid for the session (logged once): `Exists`, sizes and `GetGump` treat its ids as absent, so the paperdoll's fallbacks still work and nothing retries the decode.
  5. **Converter dedup (P2):** the entry key now holds every field the decode reads (file, offset, lengths, compression, the index's width and height); only the hue is left out.
  6. **Prefetch bounds (P2):** the 20 ms deadline now covers queueing and draining; a batch holds at most 64 distinct gumps and 64 MiB of decoded pixels. One atlas upload in progress can overrun the deadline. After a timeout, `GetGump` takes a finished worker result before decoding again (at most one duplicate decode per batched gump, documented).
  7. **Tokenizing (P3):** a server gump's layout is tokenized once; the prefetch scan and the control loop share the tokens.
  - New tests: truncated, oversized and wrapped store tables; impossible record sizes and payloads (4097×1, 65535², a 1×1 record asking for 1 GiB, partial runs, unknown layout, more than Brotli's bound); a corrupt Brotli record and an invalid one in a real store (absent without throwing, decoded once, a good record next to them still decodes).
  - Checks after the fixes: the converter's output is byte-identical (same sha256 of gumps.bin) and verifies EXACT. 635 tests pass in both build variants; `ChunkLoadingTests` timing and allocation tests failed once each on a loaded machine and passed on reruns. One short NativeAOT pair (P7 pack, M7 MUL): large pictures 3.38 against 11.59 ms; spellbook 0.79/0.65, status 0.72/0.59, backpack 0.30/0.33, combat book 1.07/1.00, journal 0.40/0.51 ms. These are within the earlier spread, so nothing regressed. The shipped pack has 0 invalid records. Picking and click-through are unchanged. The one frame over 50 ms is the world map's first `world_draw`, in both modes.
- **Follow-up (not a defect today):** replacing the pack while the client runs isn't coordinated with the gump workers. `Gump.CancelPending` has no caller, and nothing switches packs at runtime. A runtime pack switch must cancel pending gump decodes and drop the atlas, as `GameScene.Unload` does for art.
- **Not done:** the main pack `C:\Repositories\uo-packs\classic-1.0` was not regenerated (read-only for agents): `Uo.Converter --only gumps --out <pack>` adds the section in about 4 s. Tests use `CUO_TEST_GUMP_PACK` until then. Not measured: the first open of the player's own paperdoll (opened by the server at login), Legacy world renderer (gumps are UI only; the batcher path is unchanged), 4K. Codex audit pending.
## Parity mismatches at login (bisect)

2026-10-30, `bg/ecs-parity-bisect`. **Not a regression: the T6a runs did not set `CUO_ANIM_SYNC=1`.** Every mismatch is the parity checker comparing a view that has not loaded a body's first-seen direction with an extract that draws it at once. The player at login and each spawn are first sights, which is why they were "all at the login frame or a spawn".

- **Mechanism.** `Animations.LoadDirection` (`src/ClassicUO.Renderer/Animations/Animation.cs:225`, `AsyncFirstSight` at `:199`) queues the first direction of a body on the thread pool and returns frames without textures, so `MobileView.Draw` emits nothing for it. The parity checker (`EcsParity.OnCaptured`, `Game/Ecs/EcsParity.cs`) then runs the ECS extract, which reads the pack tables directly and draws the layers at once. Result: capture count smaller than ECS count (capture=3 ecs=5 for the player at login, capture=2 ecs=8 for a spawned body, `first diff` at the first missing instance) and a `frameinfo` view-effect mismatch for the same mobile. The doc already says this ("Always set `CUO_ANIM_SYNC=1` for parity"); the T6a runs, and the base 7, did not. The worker finishes within a few frames, so the number is racy (2 to 3 first-sight mobiles per run).
- **All T6a logs fit it.** In `scratchpad/t6a/logs/{w5items,base_w5items,ls_parity,w5push}_gpu.log` every mismatch has capture < ecs and a first diff at the end of the capture side; none has a field difference with equal counts.
- **Bisect** (`w5items`, kbatman3, JIT Release, Gpu, `CUO_ECS_PARITY=1`; count = mismatched in the parity summary):

| Build | without `CUO_ANIM_SYNC` | with `CUO_ANIM_SYNC=1` |
|---|---|---|
| 8a2bcb876 (E6) | 23 (3 mobiles) | not run |
| 0069120eb^1 (before recycler) | 39 (4 mobiles) | not run |
| 0069120eb (recycler) | 26 (3 mobiles) | not run |
| 98654f96f^1 (before E11) | 11 (3 mobiles) | not run |
| 98654f96f (E11), fdd40344a (E10) | not run | not run |
| de98a6d1a (tip) | 22 (2 mobiles) | **0** (423k and 323k comparisons, two runs) |
| tip + checker fix (9dde20480) | **0** (236k and 272k comparisons, two runs) | n/a |

  Counts without the variable are 11 to 39 on every build, including the pre-recycler and pre-E11 ones, so no merge changed them beyond the race. The E11 and E10 rows and the older builds with the variable were dropped for time: the bench.lock queue made each run take 20 to 40 minutes and the answer did not depend on them. Earlier agents' 0 (E6 and before) is consistent: they set the variable.
- **Fix.** `EcsParity` now sets `Animations.AsyncFirstSight = false` when `CUO_ECS_PARITY` is on, so parity no longer depends on remembering the variable (harness-only; the normal render path is untouched). Verified by two runs without the variable: 0 mismatches.
- **Open point (not changed).** With `gpu_ecs_sprites` on, the extract draws a first-sight body at once while the legacy view skips it for a few frames. Not a Gpu bug (the body appears sooner), but Gpu and Legacy differ for those frames.

## Text in the pack (2026-10-30)

Branch `poc/pack-text`, from poc/modern-client 0acd0ef85. The next "Not in the pack" types after gumps: ASCII fonts (fonts.mul), unicode fonts (unifont*.mul), cliloc (every Cliloc.* language) and speech.mul. Spec: pack-format.md "Text: fonts, cliloc, speech"; procedures: testing/pack-loading.md §7. Scripts, packs, logs and shots: session scratchpad `ptext\`.

**Design (pack-format.md "Text").**
- **Record stores, as gumps** (`RecordStore` unchanged; nothing needed extending): `text/fonts.bin` (a record per ASCII font: a 224-glyph size table, then the 16-bit texels), `text/unifont.bin` (a record per 256-character page of a font: a 2 KiB table of every glyph's offsets and size, then unifont's own 1-bpp rows; the font-exists mask in the store flags), `text/cliloc.bin` (a record per language: ids sorted for binary search, string ends, one UTF-8 blob; the language code in the record meta). Speech is a plain table, `text/speech.bin` (ids, ends, UTF-8 blob), since the client uses all of it.
- **Unicode glyphs per page on first use.** The `[20, 0x10000]` glyph table (21 MB at startup) is gone: `font → 256 pages → 256 glyphs`, a page read on first use of any of its characters (one positional read; Brotli only for pages over 16 KiB). Identical pages share a record: 2,171 of the 3,037 non-empty font pages are shared (unifont1 and unifont2 repeat unifont, the CJK fonts repeat each other), so 44.9 MB of unifont*.mul become 7.5 MiB. With MUL files the client reads a page's glyphs the same way, under a lock (the MUL reader has a seek position); a missing glyph is no longer re-read on every lookup.
- **ASCII fonts per font on first use** (35 KiB, Brotli per font). On the MUL path fonts.mul is disposed after the copy (legacy-main-review task 2).
- **Cliloc: the blob plus an index, strings on first use.** The language's record and enu's (the client reads Cliloc.enu under every language) are read whole at load. A lookup tries Clilocs.txt, the language, then enu, which is what the old merged dictionary held. Strings are created on first lookup into a per-table array with `Interlocked.CompareExchange` (one instance per id across threads; plugins and workers may call it). Nothing compared cliloc strings by reference, so dropping `string.Intern` changes nothing. `Translate` (arguments, `~1_NAME~`, `#` references, capitalisation) is untouched apart from looking numbers up through the same chain. The MUL path builds the same table from the file: one read, and a slice per entry computed exactly as `ReadUTF8(length)` would (NUL cut, clipped tail, negative lengths; tested on random records), with no strings.
- **Ship profile:** cliloc records raw (15.6 MiB; `--cliloc-brotli` makes them 4.1 MiB but costs 7 ms at startup, see below); font pages raw up to 16 KiB and Brotli above (`--text-small`, `--raw-text`).
- **Untrusted input** (as the gump audit asked): store extents are checked by `RecordStore.Open`. A font record must be sized between its table and its largest possible payload, with a Brotli payload within Brotli's bound; at decode, an ASCII record must add up exactly and a page's glyph rows must all lie inside it. A bad record, a failed read or a corrupt Brotli stream makes that font or page absent for the session (logged once), never an exception. A cliloc record must add up exactly, with strictly ascending ids and monotonic ends (≤ 256 MiB); a bad one, or a store that fails to open, makes the client read the Cliloc.* files. Speech: bounded counts and sizes, else speech.mul.
- **Switches:** `CUO_PACK_TEXT=0` reads the MUL files with the same binary. A pack without a `text/` file reads that content from MUL and says so in the log.

**Converter.** Stages `fonts`, `cliloc`, `speech` (`--only fonts,cliloc,speech`, `--no-fonts`, `--no-cliloc`, `--no-speech`, `--raw-text`, `--text-small n`, `--cliloc-brotli`), through frozen copies of the three loaders in tools/ClassicUO.Assets.Mul (the font one trimmed to `Load`, `GetCharUni` and `LoadChar`; the others verbatim plus accessors). `--only fonts,cliloc,speech` takes 2.4 s with verify (fonts 0.9 s, cliloc 0.2 s, speech 0.02 s, verify 0.9 s). Verify, all EXACT: 10 ASCII fonts × 224 glyphs and 20 unicode font slots × 65,536 characters (763,025 glyphs) against `FontsLoader`; 8 languages, 941,936 (language, id) strings against `ClilocLoader.Load(language)` without Clilocs.txt; 6,116 speech entries (id, text, keywords, start and end flags) against `SpeechesLoader`.

**Found while converting.**
- **U+FFE0 in unifont3..12 points past the end of the file:** the client's `LoadChar` throws `EndOfStreamException` when it draws "￠" in those fonts. The converter stores no glyph (and lists them); the client's MUL path now treats it as no glyph too. This is the only behaviour change against the old client.
- **A `CliLoc.csv` export** sits next to the cliloc files in the test client folder. The converter skips Cliloc.* files whose extension is csv, txt, bak, old, tmp, xml or json.

**Sizes (7.0.103):** text/ is 23.4 MiB (fonts.bin 35 KiB, unifont.bin 7.5 MiB, cliloc.bin 15.6 MiB, speech.bin 141 KiB), against 62.5 MB of MUL files (fonts.mul 0.9, unifont*.mul 44.9, Cliloc.* 16.0, speech.mul 0.1). With `--cliloc-brotli` it is 11.8 MiB; with `--raw-text`, 26.3 MiB.

**Measurements.** NativeAOT, Gpu world, 1920×1080, kbatman4, no other client running during any run. Three modes, interleaved: P = this branch with the pack, M = this branch with `CUO_PACK_TEXT=0` (the same lazy structures, MUL files), B = base 0acd0ef85. Both binaries have the same `[load]` timing line, and `CUO_LOAD_MEM=1` makes the heap figure follow a full collection. Startup comes from `[load]`; ranges are over 4 runs each of P, M and B (2 at Britain, 2 in the large-scale scene) plus 1 run C (pack with Brotli cliloc):

| | P (pack) | M (MUL, new code) | B (base) | C (pack, cliloc Brotli) |
|---|---|---|---|---|
| All loaders | 60.6–70.8 ms | 94.6–104.6 ms | 125.0–138.1 ms | 68.8 ms |
| Allocated during load | 93.4 MB | 108.0 MB | 130.4 MB | 94.4 MB |
| GC heap after load | 64.7 MB | 65.9 MB | 92.5 MB | 65.7 MB |
| cliloc load | 1.5–1.8 ms, 4.7 MB | 8.3–9.0 ms, 18.5 MB | 22.7–26.9 ms, 21.2 MB | 8.6 ms, 5.7 MB |
| fonts load | 0.3 ms, 64 KB | 12.8–14.3 ms, 1.0 MB | 13.4–14.4 ms, 21.4 MB | 0.3 ms |
| speech load | 1.1–1.4 ms, 1.4 MB | 12.1–13.3 ms, 1.7 MB | 16.9–18.0 ms, 1.4 MB | 1.2 ms |

- The heap is 27.8 MB smaller than base (the 21 MB glyph table, and 117,725 strings plus their dictionary, against a 7 MB UTF-8 blob and index). Startup is 56–74 ms shorter (paired runs), of which the text loaders account for 50–56 ms. The MUL path with the new structures keeps the memory win (65.9 MB) but not the time: parsing fonts.mul, speech.mul and the BWT-compressed Cliloc.enu still costs 33–37 ms.
- **First use in a busy scene** (plan `say:[TestScene go;g:lsgumpopen;say:[TestScene return` with `CUO_BENCH_GUMPOPEN="journal;allnames;tooltips;help"`: the large-scale scene with 1,985 mobiles and 25,161 items; the journal, every name overhead, the tooltips of the 40 nearest objects, the server's help gump). The text first-use cost is tiny in every mode. Over the whole session (login, entering the scene, all four steps), 4 unicode pages are read, taking 0.063–0.081 ms in total with the pack and 0.080 ms from MUL; 6 ASCII fonts are read (pack only; MUL loads them all at startup); and 96–141 cliloc strings are created. Per step: the journal reads 1 page (0.011–0.019 ms); help reads 1 page (0.010–0.015 ms) and creates 12 strings; names and tooltips read and create nothing (their pages and strings were already in use). One MUL page read took 0.74 ms once (a Britain run; the others 0.013–0.020 ms), a cold read of the unifont file. The worst frames in the scene's steps (allnames 13.5–20.5 ms, tooltips 16.4–18.3 ms) are the scene's own frame cost and are the same in P, M and B.
- **Cliloc Brotli** (C) saves 11.5 MiB on disk and costs 7 ms at startup (8.6 against 1.6 ms), so the ship profile keeps cliloc raw.

**Checks.**
- **Screenshots** (plan `texts;shot:texts`, with a new `_screen.png`: the composited back buffer, including overhead text and the cursor tooltip). Britain, pack against `CUO_PACK_TEXT=0`: **0 differing pixels** of 2,073,600 (the journal with names, the help gump's cliloc html with links, the paperdoll, the top bar). The large-scale scene (1,985 names overhead, a tooltip "Robe / Weight: 3 Stones", the help gump, the journal): 70,216 pixels differ, all in animated mobiles (wing and stand frames at the shot's instant) and the journal's minute (18:47 against 18:48); every text pixel is the same. Looked at both pairs and the diff images.
- **Unit tests** (`TextPackTests`, 12 tests; the data tests use `CUO_TEST_TEXT_PACK`): every glyph of every font; text rendered by `GenerateUnicode` and `GenerateASCII` (all fonts, style flags and hues, 7 sample strings) and its widths; glyph pages loaded from 8 threads (both paths); every cliloc id of every language; `Translate` with 5 kinds of argument on every enu string that has an argument; strings created once from 8 threads; every speech entry, and `GetKeywords` on every keyword; plus the untrusted-input tests (malformed tables, pages and fonts; corrupt, truncated, oversized and bad-Brotli records absent without throwing; a truncated store rejected; the MUL slice rule against `ReadUTF8`). Run against the ship pack and the `--raw-text` pack.
- **Full suite:** 650 tests pass in the default build and in `-p:CuoDevTools=false` (`CUO_TEST_TEXT_PACK` = the ship pack; the gump tests ran against the main pack, which has gumps now). The write-site budget is unchanged.
- **Not done:** the main pack was not regenerated (read-only for agents): `Uo.Converter --only fonts,cliloc,speech --out <pack>` adds the section in about 2.5 s. Legacy world renderer runs (text is UI and overhead only; the screenshots used Gpu). Codex audit pending.
## L1: TCP receive drain loop and server-close detection (2026-10-30)

Branch `poc/net-drain` (base poc/modern-client fdfcc9813, merged up to 3681f25b3).

**Problem.** `TcpSocketWrapper.Read` returned 0 whenever `Available` was 0, so a server FIN while the client was idle was never noticed (stuck "Verifying Account"), and `GameController.Update` read at most one 4 KB slice per frame.

**Change.**
- `TcpSocketWrapper.Read`: `Socket.Poll(0, SelectRead)` then a non-blocking-style `Receive(..., out error)`. Readable with 0 bytes is a close (`OnDisconnected`), an error is `OnError`; data that arrived before the close is delivered first.
- `NetClient.Drain(sink, maxBytes, maxPackets, maxMs)`: reads 4 KB slices (Huffman output stays under the 64 KB buffer) and hands each decoded slice to the parser in order, until the socket is empty, a close was raised, or a cap is hit: 128 KB raw, 1500 packets, 4 ms per frame (checked between slices, always at least one slice). `CollectAvailableData` is kept as a single-slice read. Packet order and plugin handling are unchanged (same `ParsePackets` call, once per slice).
- `Huffman.Decompress` no longer clears the whole 64 KB output buffer per call (it would now run per slice).
- `LoginScene.OnNetClientDisconnected`: a clean server close (`Success`) during Connecting, VerifyingAccount, ServerSelection, LoginInToServer, CharacterSelection or EnteringBritania is now a lost connection (popup, reconnect if enabled). It was ignored before, so the FIN fix alone would still have left the screen stuck. After a login denial the step is already PopUpMessage, so the denial text is kept. `GameScene` shows `ConnectionReset` instead of "Success" and logs `Disconnected from the game server: <error>`.
- Diagnostics: FrameStats counters `net_avail`, `net_bytes`, `net_slices`, `net_packets`; `CUO_NET_TRACE=1` logs `[net]` lines per frame with data; `CUO_NET_CAP_BYTES/PACKETS/MS` override the caps (`CUO_NET_CAP_BYTES=1` = one slice per frame, the old behaviour, used as the base).

**Measured** (kbatman2, JIT, TieredCompilation=0, VSync on, 1920x1080, the same binary with `CUO_NET_CAP_BYTES=1` as base; 3 rounds interleaved; plan: Britain, `[TestScene go`, scene, `[TestScene return`; machine shared, other agents idle on the GPU, so only the ratios matter). Bursts are frames with more than 4 KB available.

| Burst | duration | frames | KB | packets | bytes per frame (avg / max) | drain ms sum / max | frames >8 ms / >16 ms |
|---|---|---|---|---|---|---|---|
| Teleport into the scene, base (one slice per frame) | 3734-3750 ms | 226-227 | 904-908 | 55k | 4096 / 4096 | 255-278 / 16-27 | 2-3 / 1 |
| same, drain with packet cap only (1500) | 578-610 ms | 30 | 811 | 51k | 27.7k / 53k | 236-258 / 40-48 | 5 / 2-3 |
| same, drain + 4 ms budget (the default) | 797-1000 ms | 49-58 | 822-827 | 52k | 14.6-17.2k / 28.7k | 235-316 / 15-20 | 2-5 / 0-1 |
| Return to Britain, base | 500 ms | 31 | 124 | 21.5k | 4096 / 4096 | 14-19 / 2-3 | 0 / 0 |
| same, new | 141-172 ms | 11-12 | 124-126 | 21.7k | 11.7k / 12.3k | 13-18 / 3-6 | 0 / 0 |

- `Available` after the teleport reaches 65,536 (the socket's receive window) from the first frame, so the base kept the window full for 3.7 s. The packet work is the same either way (about 250 ms of parsing in total, 5 us per packet); the drain just spends it in fewer frames. Without a time budget the first frames cost 40-48 ms; the 4 ms budget keeps the worst frame near the base's (15-20 ms, the first frame, which also pays one-off setup).
- Login: only 6 KB / 239 packets arrive (largest `Available` 2.4 KB), so login-to-world-ready is unchanged: `[settle] Entry: ready` 746-828 ms in every variant. L1 changes nothing at login except the close detection.
- Cross-facet `[settle] Facet: ready` times (134-682 ms) are unchanged by the drain (the hold ends when the GPU world is ready, not when the scene's objects have arrived).

**Tests.** `NetDrainTests` (15): partial reads in order, slice size, 200 KB in slices, byte cap and resume, packet cap, time budget, nothing ready, data then close, close mid-packet, close while idle, a Huffman stream over 64 KB decompressed cut at arbitrary slice boundaries, and four real loopback `TcpSocketWrapper` tests (idle close, data then close, reset, partial reads). Against the old `TcpSocketWrapper` the idle-close, reset and data-then-close loopback tests fail. Full `dotnet test`: 604 passed. Both builds (default and `-p:CuoDevTools=false`) build with 0 errors (the DevTools=false one was built, not run).

**Normal session** (`b1;travel;ltravel;scene;return;b2`, new default build): `[settle]` 0 unsettled frames in every Teleport/Facet episode, 0 timeouts; 12 unsettled frames "while walking", the known single-page misses of walking (world-settle.md), same as before.

**Server close, live** (kbatman2, through a TCP relay `127.0.0.1:2594 -> 2593` that rewrites the 0x8C redirect; the real ModernUO was not stopped, because `stop-modernuo.ps1` needs PowerShell, which this task bars, and force-killing a shared server is not allowed; the relay closes the client-side connections, which is what the client sees when the server closes):
- In game, relay FIN: `Disconnected from the game server: ConnectionReset` in the same second (log has 1 s resolution), message box path as for any disconnect.
- In game, relay RST: same line, same second.
- Login connection idle, relay FIN: `Disconnected` warning at once (LoginScene popup path). Whether the screen was server select or character select was not captured (no screenshot); the character-select-specific popup is covered by the same code path (`CharacterSelection` is in the list) but not seen.
- Not done: a real server stop/restart, NativeAOT, a screenshot of the popups.

**Process note.** One batch (10:17) built and ran a 19 s client without holding bench.lock because its lock helper file was missing and the script carried on; found and stopped within a minute. All later steps took the lock first and held it for one step each.

**L1 audit fixes (Codex, 2026-10-30).**
- **Idle pump (P1):** `Drain` now calls the sink once with an empty slice when no slice produced bytes, so `ParsePackets` (which also drains the plugin queue) runs every frame; it counts toward the frame's time budget. Test: a plugin-injected packet on an idle socket is handled once.
- **One termination path (P1):** `NetClient.Terminate(error)` is the only way a connection ends from the receive or send side (peer close, reset, socket error, bad Huffman stream, malformed header). It resets protocol state (compression off, Huffman, send queue, a new `ConnectionGeneration`) before closing the socket and raising `Disconnected`. `Connect` and `Disconnect` reset the same state, and events from a replaced socket are ignored. Tests (real loopback, FIN and RST): close with compression on, then a login retry reads plain bytes and parses cleanly; `Connect` starts in login mode even if compression was left on.
- **Parser queues (P1):** `PacketHandlers.ParsePackets` clears both queues when the connection generation changed (also mid-loop, for the 0x8C relay). Test through the real parser: `A9 01 2C`, FIN, reconnect, `73 04` is delivered as a ping, not swallowed as the old packet's body.
- **Malformed headers (P2):** a length smaller than its own header (`03 00 00`, `03 00 01`, `03 00 02`) logs, clears the queue and ends the connection (`ProtocolNotSupported`) instead of looping; a header-sized packet is accepted. Tests on the real parser.
- **Non-blocking read (P2):** `TcpSocketWrapper.Read` reads at most `Available` bytes (cannot block); when nothing is queued but the socket is readable, a one-byte probe runs with the socket switched to non-blocking for that call only (sends stay on the blocking `NetworkStream`). Partial writes and `WouldBlock` on send are untouched (not in scope; the socket stays blocking).
- **LoginScene (P2):** `CharacterCreationDone` added; the mapping is the testable `LoginScene.IsLostOnCleanClose`, tested over every `LoginSteps` value.
- **CircularBuffer:** growth is geometric (twice the capacity at least).
- `Plugin.ProcessRecvPacket` tolerates `Client.Game == null` (needed to run the real parser in tests).
- Verification: 616 tests pass (12 new in `NetLifecycleTests`); both builds 0 errors; the nodev build logged in.
- Normal session (`b1;travel;ltravel;scene;return;b2`, vsync off): 0 timeouts, 0 unsettled frames after a timeout or without hold, 8 while walking (known). A first try with VSync on ran at 3 fps (Present blocked about 1 s per frame, 4 settle timeouts, `packets` 0.08 ms): the machine's display, not the drain; not used.
- Relay close test with `reconnect` on: in game FIN and RST: `Disconnected from the game server: ConnectionReset`, then a retry that logs in again through the relay (new `Connecting to`, `Player created`) within the same second. Login connection idle, FIN: `Disconnected` warning, then the retry logs in. Reconnect after a close at character select is thus clean (compression and parser state reset).

**L1 second audit (Codex, 2026-10-30; mostly the WebSocket path, kept for a future browser build).**
- **Terminal events on the update thread:** socket callbacks (a WebSocket's receive task can raise them from any thread) now only record "closed with error X" against their own `Connection` (first report wins, so an exception followed by the end of the receive loop is one close). `Drain` first reads and parses everything that connection had queued, and only then runs `Terminate` (reset, close, `Disconnected`). A connection that was ended, replaced or explicitly disconnected is retired: its late callbacks are ignored and its reads return nothing, so old compressed bytes can't reach the plain parser and a late callback can't terminate the new socket. Huffman is only reset on the update thread. TCP behaves as before (its events come from `Read` on the update thread; a `Connect` failure now reaches `Disconnected` on the next `Drain` instead of inside `Connect`).
- **Plugin injections are tagged by generation:** `Append(data, true)` drops older injections and stamps the queue with the current generation; `ParsePackets` clears the plugin queue only when its stamp is older. A packet injected after `Connect` but before the next `ParsePackets` (e.g. from an outgoing hook during the 0x8C relay's login sends) is kept. Test calls the real `Connect` first.
- **Plugin queue is not budgeted:** the empty-slice pump parses the whole plugin queue in one go; the time budget covers network slices only. Fine for now (plugin traffic is small); revisit if a plugin injects bulk data.
- Tests: wrap-around growth of `CircularBuffer`; the header-only variable packet is now `03 00 03` (length 3), not 5; cross-thread close with compressed data queued; double report; late callback of a replaced connection; explicit `Disconnect` then a late close.

**L1 third audit (Codex, 2026-10-30).**
- **No lock held while dispatching:** `PacketHandlers` has one `_queueLock` guarding both queues and their generations; packets are dequeued into a local buffer under it and handlers/plugins run after it is released. Plugin injection (`Append`) is thread-safe by itself, so `Plugin.OnPluginRecv*` no longer take `lock(PacketHandlers.Handler)`; the only lock order is "caller's own lock, then the queue lock". This removes the deadlock between a background injector and a handler whose response runs an injecting outgoing hook.
- **Atomic generation handling:** comparing the injection's generation, dropping older plugin packets, stamping and enqueuing are one step (`AppendPlugin`); an injection from an older connection is discarded, and never clears newer packets. `ConnectionGeneration` is read/incremented with Volatile/Interlocked.
- **Final frame vs close:** `Drain` ends a connection only after an empty read made after it had seen the close (one extra read round after seeing it; the caps still apply, a capped frame carries on next frame).
- **Idempotent termination:** `Terminate(error, connection)` ignores a connection that was already ended or replaced, so a `ProtocolError` inside the parser pump followed by the Drain that saw the close fires `Disconnected` once.
- Tests (626 pass): background injector plus a hook-injecting handler (2000 parses, 20 s timeout), final frame after an empty read, stale-generation injection, malformed plugin bytes at close.

**L1 final audit (Codex, 2026-10-30).** `Drain` remembers the specific `Connection` whose close it saw (`closeSeen`), and an empty read ends a connection only when it comes from that same one; a connection replaced inside the sink (the 0x8C relay) starts over. Test: two `RacingSocket`s chained, replacement inside the first sink.

**Follow-ups (not fixed on this branch).**
- Pre-existing send-side race in `NetClient.Send`: encryption happens before the send lock, so concurrent plugin-thread and main-thread sends can encrypt in one order and be queued in another (a stream cipher then desyncs), and `ResetProtocolState` / `EnableCompression` clear `_sendStream` without taking its lock. The fix is to encrypt and enqueue under one lock, and to take that lock when clearing.
- Continuous background plugin injection can keep the (unbudgeted) plugin-queue pump running for a whole frame; if a plugin ever injects bulk data, give the plugin pass a packet or time cap too.

## Login warm-up collections: pools filled and one collection inside the login hold (2026-10-30)

Branch `poc/login-warmup-gc` (warmgc agent, kbatman3), from poc/modern-client 3685c30f0; final numbers on 80ccb1a24 (base: branch `poc/login-warmup-gc-base`, the same tip plus the measurement commits only). Follows "Chunk loads without allocation", whose remaining collections were all login warm-up. Scripts, logs and traces: session scratchpad `warmgc\`.

**New measurement: the session GC timeline.** The `[gc]` lines only cover plan windows, which start about 10 s after login. With `CUO_FRAMESTATS=1`, FrameStats now also logs from the first in-game frame:
- `[gcs] +12.34s #n genN ...`: every collection, with seconds since login, pause, promoted MB and the frame's time, marked `login` (before the first presented world frame), `held` (WorldSettleGate held the world that frame) or `play`;
- `[gcs] slow frame`: every frame over 8 ms, marked the same way. The frame after a window report is logged as such and not counted: the report itself takes 20–30 ms;
- `[gcs] summary +40s/+60s`: counts and pauses, play against held;
- `[gcs] pools`: chunks, lands and statics allocated and pooled, and the statics mesh capacities.

**What allocates after login** (base; dotnet-trace allocation ticks on a JIT build plus the pool lines):
- In the first 40 s, the client allocated about 155 MB. 125 MB of it is large-object-heap `byte[]`: region decompression in `MapLoader.LoadRegion` and Uo.Render `Region.Parse`, and pack page buffers. Large objects don't fill gen0.
- About 30 MB of small objects fill gen0 twice:
  - the Legacy `MeshLayer` arrays of new chunks (5.8 MB) and their growth (5.2 MB);
  - new `Land` and `Static` objects (4.7 MB);
  - chunks, tile arrays, list nodes and the recycler queues growing (the queues reach the large object heap);
  - Uo.Render bake arrays, misses in the art and texmap decode buffer pools, strings, and the server's items.
- Over the session: 225 chunks by the end of the login, 289 in all (none after 40 s), 19,584 lands and 16,387 statics. Statics mesh capacity after 2 minutes: 64 sprites: 15 chunks; 128: 128; 256: 143; 512: 3.
- Collections: in base, every one of the first 40 s is a gen1 that promotes 21–28 MB: one at login, two at about +19 s and a compacting one at +33–38 s (4–12 ms each). The warm-up is promoted again by the next gen1, which can fall anywhere: at +79 s in `glide`, compacting, 8.3 ms, a 10.0 ms frame (Pb0). That is the glide hitch the chunk-alloc agent saw.

**Fix: `MapWarmup`** (Game/Map/MapWarmup.cs).
- When World creates its first map (login, before the first world frame), it fills:
  - the chunk pool to 300 chunks, with the statics mesh layer pre-sized to 256 sprites (48 KB, below the large object heap);
  - the recycler to 19,200 lands and 16,800 statics (56 per chunk, the measured average), with both queues sized for a full pool.
- The sizes follow the chunk cache limit, not one viewport. The pool and the recycler already stop at `PREDICTABLE_CHUNKS` (300) and 64 lands per chunk, and the prefill fills them to those caps.
  - A 4K or far-zoomed-out view needs more chunks than the caps, before and after this change: the first 300 come from the prefill and the rest allocate as before. Raising the caps is a separate decision.
  - A small view (800×600) uses perhaps a quarter of them, and the rest stays in the pools.
- **The 32 MB up front is a deliberate trade:** prefill takes 22–31 ms and 32 MB at login, in exchange for a first minute that doesn't allocate while playing.
- Prefilled objects are new objects in the retired state (destroyed, `InRecycler`), stamped one quarantine ago so they can be handed out at once. They were never in a tile list, so nothing can hold them, and `Create` resets them as it resets any retired object.
- Then one deliberate collection, in a frame where the world isn't visible: two induced gen1s, so everything login allocated that's still alive lands in gen2 without marking all of gen2.
  - Gpu world: the last held frame of the login's settle hold, or a held frame 1.2 s in, before the hold's 1.5 s timeout.
  - Legacy: the first world frame, which is the login transition.
  - Cost: 4–12 ms (`[warmup] collected ...`).
- **Switches:** `CUO_WARMUP=0` restores the old behaviour; `CUO_WARMUP_GC=0` keeps the prefill and skips the collection; `CUO_WARMUP_GC=2` makes it a full compacting gen2; `CUO_WARMUP_CHUNKS=n` sets the chunk count.
- **Not done:** a collection in later teleport holds. .NET has no API for how full gen0 is, so there is no honest trigger. `DOTNET_GCgen0size` is ignored by NativeAOT: the gen0 budget stayed 15 MB (one run).

**Measurements.** NativeAOT, `DOTNET_TieredCompilation=0`, Gpu world, 1920×1080, plan `travel;ltravel;glide`, 4 windows each.
- Three alternated pairs (Pb0/Pn0, Pn1/Pb1, Pb2/Pn2), times from the first in-game frame.
- The machine was noisy: other agents' work caused 0.5 s stalls about every 2 s in the first four runs, and those runs' logins were slower. Frame counts over 8 ms are dominated by those stalls, so the collections are the measure.

| | Base Pb0 / Pb1 / Pb2 | New Pn0 / Pn1 / Pn2 |
|---|---|---|
| natural collections, 0–60 s after login | 7, 4, 4 | 4, 1, 0 (plus the 2 induced: 7.3, 11.6, 7.9 ms) |
| of these in play frames (after the 1st s) | 1 (gen0 8.5 ms, +5.6 s), 0, 0 | 1 (gen0 5.3 ms, +5.4 s), 0, 0 |
| promoted per collection after login | 21–28 MB (gen1) | 2–11 MB (gen0) |
| 60–100 s | gen1 in `glide` 8.3 ms (frame 10.0), gen1 3.1 ms, none | none, none, gen0 3.9 ms (6 MB) |
| chunks / lands allocated after login | 289 / 19,584 | 0 / 384 |

- **Pb0 and Pn0 ran in the noisiest period.** Each has 2–3 collections in login frames before the hold ended, and one gen0 at about +5.5 s while idle after login. On the quiet runs, new has 0–1 natural collections in the first minute instead of 4.
- **The one collection new still has** comes at about +31–36 s in `travel` (Pn1, and two earlier single runs). It is a gen0 of 3.5–5 ms that promotes about 10 MB: buffers rented from the art and texmap decode pools on first sight, Uo.Render bake arrays and the server's objects. Whether it lands in a held or a play frame is chance; it stays under 8 ms.
- **Long session** (13 minutes, 7× `travel`, `glide`, `ltravel`, `bwalk`; new only):
  - 4 natural collections after login (3 held, 1 play at 2.6 ms; max pause 4.6 ms; 13.6 ms of pauses in all), plus the 2 induced at login (7.6 ms). The chunk-alloc agent's re-run on its tip had 7 collections, 4 of them login warm-up.
  - Chunks allocated after login: 0. Statics mesh capacities at the end: 256: 278 chunks, 512: 22.
  - Private bytes: 2,147 MB at 1 min, 2,180 MB at 3 min, 2,186–2,192 MB from 5 min to the end (flat). That is about 35–40 MB above the chunk-alloc agent's 2,150 MB, on a different tip; the prefill is 32 MB of it.
  - 23 `bwalk` legs with 0 denies, 178 settle episodes with 0 timeouts, 0 exceptions. The machine was noisy: 49 play frames over 100 ms, none of them with a collection.

**Verification** (new binary on 80ccb1a24):

| Check | Result |
|---|---|
| `CUO_RECYCLE_CHECK=reuse` with `CUO_GPU_SKIP_CHECK=1`, `CUO_PREP_CHECK=1`, `CUO_GPU_ITEM_CHECK=1` (`travel`, `ltravel`, `glide`, Britain, the bank, zoom 2.5) | 271k retired, 300k reused (the prefilled ones included), 0 uses after retire, 4,216 sweeps (77M objects), 0 bad. Prep check 0 wrong; item check 237 checks, 0 with differences; skip check reasons only (light 6) |
| Legacy (`-worldrenderer legacy`, `travel`, `glide`) with `CUO_RECYCLE_CHECK=reuse` | 102k reused, 0 uses after retire, 0 bad sweeps; the collection ran in the first Legacy world frame (7.3 ms) |
| Settle | 0 timeouts in every run |
| Unit tests | 594 pass, default and `-p:CuoDevTools=false`. New tests: prefilled objects are retired and handed out at once, before later retirees; the prefill is capped and only tops up; a disabled recycler isn't prefilled; a prefilled object reset for reuse equals a new one (field check); prefilled chunks come from the pool with a pre-sized statics mesh |

**Not done:** no 4K run; no Legacy timing pair (the Legacy run is the reuse check only); not the owner's FPS test.

**Codex audit (602b071b7): no recycler-state or use-after-reuse issue; fixed:**
- **The login collection could stay armed into gameplay.** A login hold that timed out left it for the next teleport or facet hold, a switch to Legacy ran it during play, a disconnect left it pending, and with `CUO_WORLD_HOLD=0` it never ran.
  - It now belongs to the login's first world frames only. In the Gpu world it runs in a held frame of the login episode (`Entry`): the last one, or one at 1.2 s (at most 80% of the configured hold timeout).
  - A presented frame, a hold of another episode or `GameScene.Unload` cancels it (`[warmup] collection cancelled: ...`).
  - In Legacy it runs only in the first world frame.
  - With `CUO_WORLD_HOLD=0` nothing is held, so the first world frame cancels it.
- **`[gcs]` totals counted wrong when several collections fell in one frame.** The two induced gen1s counted as 2 collections but as one gen1 and one pause. Totals now come from deltas of `CollectionCount(0/1/2)` and `GetTotalPauseDuration()`; the per-collection detail is labelled "latest sampled".
- **The session baseline** is now taken at the end of the last frame before login, so collections in the first in-game frame count (Legacy's warm-up collection runs there).
- **`WorldShown` and the timeline reset when the session ends.** After a logout, login-screen frames no longer count as `play`, and a relogin starts a new timeline.
- **Pre-existing, follow-up for when several Worlds matter (not changed):**
  - The `Chunk` pool is static, while each chunk keeps its `World` (readonly), and `World.Clear` calls neither `Chunk.ClearPool` nor `MapRecycler.Clear`. A second World could get the first one's chunks.
  - The prefill makes that retention immediate: 300 chunks from login on.
  - Today there is one World per process.
- **Check after the fixes** (010a09866): 643 unit tests pass in both variants. One NativeAOT login run (`travel`, `glide`) logged the induced collection in the last held frame of the login hold (12.4 ms), shown as `2 collections (gen1+ 2), pause 12.32 ms, login`. The 4 collections of the first in-game frame are now counted. The +40 s summary (6 collections, 22.0 ms) equals the sum of the per-frame lines; 0 collections in play frames, 0 exceptions.



## T6a: one sort key reproduces every tile list (2026-10-30)

Base: poc/modern-client 3681f25b3. Branch `poc/ecs-t6a-tile-order`. No behaviour change. Logs and tools: session scratchpad `t6a\`.

**Result: proved for everything the runs reached, 0 out of order.** The key is (stored `PriorityZ`, rank, rank 2 ? +seq : −seq), with rank 0 land, rank 1 a `Multi` that `AddGameObject` inserts with `state` 1, rank 2 everything else. The real rules, read from `Chunk.AddGameObject` rather than the plan's summary:
- **Priority** is computed once, at insertion, and stored in `PriorityZ`. Nothing changes it while the object is in a list (only `Destroy` and `ResetForReuse` zero it, both after unlinking).
  - Land: Z−2, or AverageZ−2 when stretched. Mobile: Z+1. Corpse item: Z+1. GameEffect: Z+2. Multi with `CHMOF_GENERIC_INTERNAL`: Z−1, no tiledata terms.
  - Everything else (statics, other items, multi items with `MultiGraphic`'s tiledata, other multis, preview multis after a +1): Z, −1 if Background, +1 if Height ≠ 0, +1 if MultiMovable.
- **Tie placement:** the scan from the head stops at the first object with a higher priority, or at an equal one when the new object is land (`state` 0: before every equal), or at a non-land equal when it is a `state` 1 multi. Generic-internal is tested first, so generic-internal plus preview is still state 1; any other non-preview multi is state 1. Everything else, preview multis included (`state` 2), goes after all equals.
- **Every placement** goes through `AddGameObject` (`AddToTile`, `SetInWorldTile`, `Map.Park`'s deferred add, chunk loads), which first unlinks the object (`RemoveFromTile`), so a move or any re-add restamps it. `RemoveGameObject` is never called; removal is `RemoveFromTile`/`Destroy`, which only unlink.
- **Graphic changes** re-insert only where the code calls `SetInWorldTile`/`AddToTile` (0x1A world items do; season changes and `Graphic =` sites without a re-add do not). The stored priority then stays, and the key uses it, never the current graphic.
- So by induction each list is sorted by the key. The new `TileOrderTests` check the priorities, each rule, re-inserts, removal, a graphic change without a re-insert, and 3,000 random operations over land, statics, plain, preview and generic-internal multis, comparing the list with its stable sort after every step.

**What changed.**
- `GameObject.TileSeq` (uint; a ulong since the audit fixes below), stamped in `Chunk.AddGameObject` from the global `TileOrder.NextSeq` (pre-increment, so 0 is never a stamp). Reset in `GameObject.ResetForReuse`; the recycler's reflection test enforces it. `MapObjectRecycler.cs` itself is untouched.
- `Game/Map/TileOrder.cs`: `Rank`, `Key`, `CountOutOfOrder` and the checker.
  - `CUO_TILE_ORDER_CHECK=1` (every 30 frames), `every`, or `N`. From `GameScene.Update` (since the audit fixes: the start of the world prepare), every tile of every loaded chunk (a superset of the tiles either walk visits) must be non-decreasing in key, which is exactly "equals its stable sort".
  - Logs `[tile-order] tiles N, objects M, out of order K (tiles T), checks C; land, multi, other (preview multis)` every 5 s, and the first 25 offending tiles with each object's kind, rank, priority, Z, sequence and graphic.
- Test seam: `Chunk.StaticDataForTests` (null in the client), read instead of the loaded tiledata by `AddGameObject`'s default branch. Replaced in the audit fixes by `Chunk.StaticTileData`, with no per-insert branch.

**Runs** (JIT Release, kbatman3, `CUO_TILE_ORDER_CHECK=1 CUO_RECYCLE_CHECK=1`; drivers with `-ecscheck 120`, Gpu drivers with `CUO_ECS_PARITY=1`):

| Run | Renderer | Tiles checked | Objects checked | Out of order | Recycle check |
|---|---|---|---|---|---|
| Plan: Britain, `pickscan:8x6`, bank, `pickscan:8x6`, house spot at zoom 1 and 2.5, `travel` (real teleports), `ltravel`, `glide`, `lswalk` | Gpu | 72.7M | 136.0M | 0 | 195,752 retired, all 0 |
| Same plan without pickscan | Legacy | 48.6M | 90.6M | 0 | 198,824 retired, all 0 |
| `ls_parity` (large scene) | Gpu / Legacy | 1.07M / 1.09M | 12.8M / 13.5M | 0 / 0 | 0 / 0 |
| `w5push` | Gpu / Legacy | 10.7M / 11.4M | 11.5M / 11.4M | 0 / 0 | 0 / 0 |
| `w5items` | Gpu / Legacy | 4.30M / 4.27M | 4.32M / 4.28M | 0 / 0 | 0 / 0 |
| `corpses` (corpses, HealerCamp multi) | Gpu / Legacy | 4.57M / 4.20M | 4.90M / 5.18M | 0 / 0 | 0 / 0 |
| Rank counts, `=every`: house spot, Britain, bank, glide | Gpu | 931M | 1.72G (land 931M, other 794M, multi 0) | 0 | 0 |
| Rank counts, `=every`: `corpses` with the camp | Legacy | 200M | 309M (land 200M, multi 64.5M, other 44.3M) | 0 | 0 |

- `pickscan:8x6` at Britain and the bank: 48 same, 0 different each. `-ecscheck`: 0 mismatches in every run.
- **Gpu parity** showed 29–75 mobile mismatches per driver run, all at the login frame or a spawn (the capture has fewer layers than the ECS, e.g. 3 vs 5, plus FrameInfo). **Pre-existing:** base 3681f25b3 in the same `w5items` run had 7 of the same kind (the player at login, a spawned NPC). Not tile order. The 40 and 54 came from two runs that logged in inside the large scene (the previous run had left the character there).
- **Not exercised live:** custom-house components (generic-internal multis), house-customization preview multis (`GameCursor`) and boats. The "house" spot (1427 1691) had no multi in range (multi count 0), so the multi rank was exercised only by the HealerCamp (64.5M multi-object checks). The other kinds are covered by the unit tests only.

**Exceptions: none observed. Hazards for T6b, from the code:**
1. **Compute the key at insertion, not from current fields.**
   - `PriorityZ` comes from the graphic, Z and flags at that moment. The graphic (seasons, some `Graphic =` sites) and multi state can change later without a re-insert.
   - The checker recomputes the rank from the current `Multi.State`. That is only safe because no code sets or clears `CHMOF_PREVIEW`/`CHMOF_GENERIC_INTERNAL` on a placed multi between placements: `House.Generate(removePreview)` clears preview only after `SetInWorldTile`, and only on house components, which never carry it; the floor-plane multis get `GENERIC_INTERNAL` after `Add`, which is rank 1 either way.
   - T6b should store (priority, rank, seq) per entry.
2. **A stale head pointer** (fixed in the audit fixes below).
   - `RemoveFromTile` does not update `Chunk.Tiles[x, y]`, which stays the first object ever placed on the empty tile. The walk finds the head by following `TPrevious` from it.
   - Harmless while that object is the land, which never leaves. On a tile with no land (a block without map data), the first dynamic object leaving would orphan the rest of the list, and re-adding it takes the `o == obj` branch, which follows the `LinkedObject` container links (`Previous`/`Next`), not the tile links.
   - Not reached in any run. T6b's own storage should not reproduce it.
3. **Seq was a 32-bit counter.** About 250k stamps in a 4-minute travel session, so a wrap would take weeks, but a wrap reverses two equal-priority inserts (audit). Now 64-bit, with a struct comparator.
4. **Insertion order is the only tie-break between equal non-land, non-multi objects** (items, mobiles, effects, statics). T6b must take its stamp at the same point in update order as `AddGameObject` (E4's observation).

**Cost when unset.** NativeAOT, paired, checker unset, `world_prepare` in ms, 2 windows per spot. Series 1 (base/new/new/base) on 19d295c5a; series 2 (new/base/base/new) on 1b3518b55, which adds only checker code. The machine was noisy: base alone spread up to 25% between sessions.

| Spot | base (4 sessions) | new (4 sessions) |
|---|---|---|
| Gpu Britain | 0.071 0.064 0.069 0.066 | 0.063 0.068 0.068 0.073 |
| Gpu bank | 0.073 0.069 0.089 0.071 | 0.067 0.073 0.072 0.079 |
| Gpu zoom 2.5 | 0.252 0.249 0.321 0.254 | 0.235 0.241 0.231 0.288 |
| Gpu large scene | 0.934 1.052 0.967 1.146 | 1.120 1.001 0.989 1.147 |
| Legacy Britain | 0.189 0.207 0.218 0.309 | 0.207 0.209 0.208 0.292 |
| Legacy bank | 0.155 0.143 0.164 0.200 | 0.143 0.146 0.141 0.153 |
| Legacy zoom 2.5 | 1.365 1.300 1.447 1.603 | 1.392 1.423 1.306 1.344 |
| Legacy large scene | 1.283 1.230 1.362 1.398 | 1.224 1.219 1.200 1.197 |

No measured regression. With the variable unset, the change still costs one static `bool` test per frame and, per placement, a counter increment and a field store (the audit fixes add a cell store and remove the test-seam branch).

**Tests:** 599 pass in both `CuoDevTools` variants (10 new in `TileOrderTests`), and `-p:CuoDevTools=false` builds. `ChunkLoadingTests.Prefetched_Regions_...` failed once on the first run under load and passed on the rerun, as in E4 and E10.

### T6a audit fixes (Codex audit of 634cd24b9)

The audit confirmed the key matches normal insertion (priorities, equal-priority placement, no insertion bypass, no stamping off the main thread) but found the proof incomplete. Fixed in two commits:

**0259870f9, tile membership (pre-existing bug, behaviour change on landless tiles only).**
- The bug: `RemoveFromTile` never updated `Chunk.Tiles[x, y]`, the anchor the walk starts from. On a landless tile (`Chunk.Load` places nothing when `TryGetBlock` fails), removing the anchor A left `Tiles[x, y] == A` with its links cleared, so the rest of the list was unreachable. Re-adding A then took `AddGameObject`'s `o == obj` branch, which followed the `LinkedObject` container links (`Previous`/`Next`: for an effect, its EffectManager sibling) into another tile's list.
- The fix: `GameObject.TileCell` stores the cell, set with `TileChunk` in `AddGameObject` (X/Y can change before removal: `SetInWorldTile`). `RemoveFromTile` calls `Chunk.ReleaseAnchor`, which hands a departing anchor to `TPrevious ?? TNext`, or null. The `o == obj` branch and the unused `RemoveGameObject` are gone. Tiles with land are unchanged: the land is the anchor and never leaves.
- Tests: anchor removal on a landless tile, re-insertion with a stale container link (the old code put the object into the other tile's list), moving the anchor away and back, the stored cell surviving an X/Y change, and the random test now also uses a landless cell and checks membership both ways after every step.

**55b15f1ee, the checker and the key.**
- **64-bit sequence and a struct key.** `TileSeq`/`NextSeq` are `ulong`; `TileOrder.Key` returns `TileOrderKey` (priority, rank, seq) with a comparator (rank 2 oldest first, ranks 0 and 1 newest first). A 32-bit stamp wrapping between two equal-priority inserts reversed them. Tests cross the 2^32 boundary and check the comparator at the extremes.
- **Full traversal.** No 10,000-object cap: each list is walked once from its head, with Floyd cycle detection on `TPrevious` (head search) and `TNext`. A cycle, a broken back link (`TNext.TPrevious != o`) or an object whose stored chunk/cell is another tile fails the tile; nothing unchecked is reported as clean. The other direction: every item, mobile, house component and (dev builds, where `World.Effects` exists) effect whose `TileChunk` is set must be reachable from its stored tile's anchor. Not covered: land and statics that fell out of a list (they are only reachable through lists), the multi-placement preview item and the cursor's preview multis (not in any world collection).
- **Reports** are counted independently of the descent, capped at 25, and print a 16-object window around the first descent.
- **Where it runs.** It ran in `GameScene.Update` before `World.Update`, the boat updates and the rest of Update, so it did not see the state walked that frame. It now runs at the start of `FillGameObjectList` (both renderers), on the lists the walk then reads. Exception: a chunk loaded synchronously during the prepare itself (`CUO_CHUNK_SYNC=1`, or a `GetTile`/`GetChunk` with load during the walk) is not in that frame's sweep; it is checked from the next sampled frame on.
- **No test-seam branch.** `AddGameObject` reads `Chunk.StaticTileData`, assigned once in `Client.cs` right after `FileManager.Load` (`TileDataLoader.Load` is the only place that array is replaced); unit tests assign their own. The per-insert null test is gone. What placement still costs: the counter increment and the 8-byte stamp store, plus the cell store.

**Runs on 55b15f1ee** (JIT, kbatman3, `CUO_TILE_ORDER_CHECK=1 CUO_RECYCLE_CHECK=1`; the four drivers `ls_parity`, `w5push`, `w5items`, `corpses` in one session per renderer to save bench-queue turns, `-ecscheck 120`, Gpu with `CUO_ECS_PARITY=1`):

| Run | Tiles | Objects | Out of order / cycles / broken / foreign | Owned checked, unreachable | Recycle |
|---|---|---|---|---|---|
| Plan Gpu (Britain + pickscan, bank + pickscan, house spot, travel, ltravel, glide, lswalk) | 69.9M | 130.2M | 0 / 0 / 0 / 0 | 2.93M, 0 | all 0 |
| Plan Legacy (same, no pickscan) | 36.5M | 67.9M | 0 / 0 / 0 / 0 | 2.08M, 0 | all 0 |
| `ls_parity` Gpu | 0.97M | 8.03M | 0 / 0 / 0 / 0 | 7.05M, 0 | all 0 |
| Four drivers, Legacy | 30.4M | 39.6M (multi 453k) | 0 / 0 / 0 / 0 | 9.21M, 0 | all 0 |
| Four drivers, Gpu | 30.2M | 42.7M (multi 4.85M) | 0 / 0 / 0 / 0 | 12.5M, 0 | all 0 |

- `pickscan:8x6`: Britain and the bank 48 same, 0 different. `-ecscheck`: 0 mismatches. Parity items 4.34M, 0 mismatches.
- Gpu mobile parity: 190 (drivers) and 340 (`ls_parity`) mismatched mobile-frames, all in the first 6 s after login and at the arrival in the large scene (capture 3 layers vs ECS 10–11, FrameInfo). Base 3681f25b3 with the same `ls_parity` run: 64, at the same two moments, same mobiles' kind. The count depends on arrival timing. Cause (coordinator, later): these runs lacked `CUO_ANIM_SYNC=1`; not tile order. `CUO_ECS_PARITY` now forces synchronous loading itself (492e73c14).

**Chunk-load cost** (NativeAOT, checker unset, paired base 3681f25b3 / new 55b15f1ee, base-new-new-base, 3 windows of `travel` (real teleports), `ltravel` and `glide`; totals over the windows; noisy, other agents' clients running):

| Mode | base 1 | new 1 | new 2 | base 2 |
|---|---|---|---|---|
| travel, µs per chunk load (loads) | 41.7 (595) | 27.2 (595) | 27.3 (595) | 35.8 (595) |
| ltravel | 19.5 (605) | 19.3 (604) | 18.9 (604) | 19.7 (605) |
| glide | 23.8 (280) | 23.4 (328) | 23.0 (328) | 23.2 (329) |

No measured regression (travel's base rounds include GC-heavy frames: max 5.4 and 6.4 ms per frame vs 1.1–1.2 ms). The world prepare series above stands as "no measured regression", not "no cost".

**Tests:** 607 pass in both `CuoDevTools` variants (18 in `TileOrderTests`); both client flavours build. After merging poc/modern-client 80ccb1a24 (T0 census and write-site budget test): 643 pass in both variants, both flavours build.

### T6a re-audit fixes (Codex re-audit of ec4c1f8c1), commit c24d6cf71

The re-audit confirmed the five earlier points fixed and no regression from `ReleaseAnchor`. Four more:
1. **Movers kept lone objects on their old cell (pre-existing).** `Mobile.ProcessSteps` (step completion) and `BoatMovingManager` (the boat item and carried entities) updated X/Y and re-added only when the object had a tile-list neighbour (`TNext`/`TPrevious`). A lone object on a landless tile has none, so it stayed on the old cell with stale `TileChunk`/`TileCell`; a parked mover kept its old parked destination. They now test `GameObject.IsPlaced` (`TileChunk != null` or an active `PendingBlock`). On tiles with land this is the same test as before (the land is always a neighbour).
2. **The checker could miss its own anchor.** With `L.TPrevious = S` and `S.TNext = null`, head discovery returned S and only S was checked. Head discovery now counts `TPrevious` links whose target doesn't point back (broken links), and the forward walk must meet the anchor (`anchor missed`); both fail the tile. New: an object other than an effect whose X/Y is not its tile counts as `misplaced` and fails the tile, which catches point 1.
3. **Forward-only scans from `Tiles[x, y]` (pre-existing).** `Map.GetMapZ` and `Map.CalculateNearZ` walked forward from the anchor, which on a landless tile can be an interior node: a roof before it was missed. Both now go through `Map.ScanStart` (`Map.ScanMapZ`, `Map.FindNearRoof`, testable statics): on a tile with land they start at `Tiles[x, y]`, the land, exactly as before (owner policy: reproduce legacy behaviour, fix quirks behind their own switch); only when the anchor is not land (the landless case) they start at the head. A test pins the legacy behaviour: a roof static sorted before the land stays invisible to the roof scan and to `GetMapZ`'s static Z. Grep found no other forward-only scan from `Tiles[x, y]`: `MiniMapGump` walks to the tail and back (sees everything), and `GameScene`'s multi-placement reads only the anchor's coordinates.
   - **Proposed separate switch (not done): `tile_scan_from_head`.** On tiles with land, both scans skip everything sorted before the land: objects whose priority is below the land's (Z − 2, or AverageZ − 2 when stretched), i.e. statics, items and multis lower than the ground, which happens on slopes and stretched land. For `CalculateNearZ` (the walk's roof cut-off, within 6 of the player's tile Z) that means a roof piece just below a sloped land's priority is ignored when deciding whether the player is under a roof, so roofs could stay drawn at such tiles. For `GetMapZ`, the only caller uses the ground Z, so the skipped static Z has no effect today. Low priority: it needs a case found in the world (roof under a slope) before it's worth a switch; it would be checked with `CUO_BENCH_SHOTS` and pickscan with the switch off and on at houses on hills.
4. **Synchronously loaded chunks** during the prepare are not in that frame's sweep: qualified above rather than changed.

Tests: a lone mobile stepping on a landless tile (the checker flags the old behaviour as misplaced; source empty and destination holding it after the re-add), parked and unplaced `IsPlaced`, a boat's cargo item and rider alone on landless tiles following the boat, the missed-anchor corruption, and the roof scan (roof Z20, roof multi Z40, anchor Z50: at z 24 the scan finds the Z20 roof). Unit tests reach the chunk half of the movers only: `AddToTile(x, y)` needs a `Map`, which needs the UO file manager (E11).

**Runs on c24d6cf71** (JIT, kbatman3, `CUO_TILE_ORDER_CHECK=1 CUO_RECYCLE_CHECK=1`; drivers `ls_parity` + `w5push` + `w5items` + `corpses` in one session per renderer with `-ecscheck 120`, Gpu with `CUO_ECS_PARITY=1`):

| Run | Tiles | Objects | Out of order, cycles, broken, foreign, anchor missed, misplaced | Owned, unreachable | Recycle |
|---|---|---|---|---|---|
| Plan Gpu (with `pickscan:8x6` at Britain and the bank) | 80.2M | 147.4M | all 0 | 4.93M, 0 | all 0 |
| Plan Legacy | 54.8M | 99.3M | all 0 | 2.20M, 0 | all 0 |
| Drivers Legacy | 22.0M | 37.2M (multi 318k) | all 0 | 14.7M, 0 | all 0 |
| Drivers Gpu | 22.7M | 43.5M (multi 3.38M) | all 0 | 20.1M, 0 | all 0 |

Pickscan 48 same, 0 different at both spots. `-ecscheck` 0. Parity items 4.87M, 0 mismatches; mobiles 167 mismatched mobile-frames at login and scene arrival (no `CUO_ANIM_SYNC=1`, as above).

**Tests:** 648 in both `CuoDevTools` variants (23 in `TileOrderTests`; `ChunkLoadingTests.Prefetched_Regions_...` failed once on the first run under load and passed in the other variant); both client flavours build. After merging poc/modern-client (gumps in the pack, E4 gate, art workers): 661 tests, 660 pass in both variants; the one failure is `ChunkLoadingTests.Prefetched_Regions_...` under the full run's load, which passes alone (the known flaky test); both flavours build.

**Scan start kept at the land (commit 7d3a52afc, owner policy).** After the re-audit round, `Map.ScanStart` makes `GetMapZ` and `CalculateNearZ` start at `Tiles[x, y]` when it is the land, as legacy did, and at the head only when the anchor is not land (point 3 above, rewritten). Tests: 662 pass in both `CuoDevTools` variants; both flavours build. Runs on 7d3a52afc (JIT, kbatman3, `CUO_TILE_ORDER_CHECK=1 CUO_RECYCLE_CHECK=1`):
- Gpu, Britain and the bank with `pickscan:8x6`: 48 same, 0 different at each; tile order all 0 over 36.5M objects; recycle all 0.
- Legacy, Britain, the bank and the bank at zoom 2.5 (no pickscan): tile order all 0 over 31.0M objects; recycle all 0.
- **Mistake, 17:41:** a first Legacy run had `pickscan:8x6`. Pickscan waits for the Gpu prepare's skipping, which never activates in Legacy, so it hung and DevBench kept moving the OS cursor into the window until the owner closed the client. Pickscan is Gpu-only; the coordinator's fix (skip when the Gpu world isn't active, a capped wait, no OS cursor warps) is pending on poc/modern-client. A planned Legacy `CUO_BENCH_PICK` run was cancelled before it started.


## DevBench: the scripted mouse stops moving the OS cursor; pickscan can't hang on Legacy (2026-10-30)

Coordinator fix, d0865ba49. The owner's mouse was stuck in a client for minutes: T6a's `r4_pick_legacy` run put `pickscan:8x6` after `world:legacy`. Phase 1 of a pickscan point waits for `GameScene.IsGpuPrepareSkipping`, which never becomes true without the Gpu prepare, so the scan sat on point 0 and the plan never advanced. Meanwhile `HoldMouse` (and the same block in `UpdateUi`) called `SDL_WarpMouseInWindow` every frame.
- `HoldMouse` sets `Input.Mouse.Override` (cleared when `_mouse` is null) instead of warping; `UpdateUi` calls `HoldMouse`. `Mouse.Update` is the only reader of the OS cursor and returns `Override` when set, so selection, camera and tooltips see the scripted point. The B8 `ui` scene already used `Override`; `HoldMouse` never runs in the same frames.
- `pickscan:` is skipped with `[bench-scan] ... skipped: the Gpu world renderer isn't active` unless the Gpu world is active.
- A point that waits more than 600 frames for skipping abandons the scan (`[bench-scan] abandoned at point ...`) and releases the mouse (`EndPickScan`).
- Agent rule: never move or capture the OS cursor; a test that needs the real cursor is announced to the owner first; every client run has a hard timeout and every waiting plan step a cap.

Verified: both variants build; 681 tests (one earlier run hit the known flaky `ChunkLoadingTests.Prefetched_Regions...`; the rerun passed). Not run in a client yet: the next agent run with pick or pickscan confirms it.
## Send-side lock: encrypt and enqueue as one step (2026-10-30)

Branch `poc/net-send-lock` (send-race agent), from poc/modern-client 8f1c81a17. Fixes the "Pre-existing send-side race" follow-up of the L1 network drain entry.

**Bug.** `NetClient.Send` encrypted the packet (advancing the stream cipher) before taking the lock that guards the send queue, so two senders (main thread and a plugin thread) could encrypt in one order and queue in the other, and the server then decrypted garbage. `ResetProtocolState` and `EnableCompression` also cleared `_sendStream` (and flipped `_isCompressionEnabled`) without the lock, so a reset could tear the queue under a concurrent `Enqueue` or in the middle of `ProcessSend`'s drain loop.

**Fix (`NetClient.cs`).** One `_sendLock` guards `_sendStream`, the send-side read of `_isCompressionEnabled`, the send-side cipher state (`Encryption.Encrypt`) and the send statistics. `Send` encrypts, enqueues and counts inside it; `ProcessSend` drains inside it (as before); `ResetProtocolState` and `EnableCompression` take it to flip the mode and clear the queue. No allocation, and still one lock acquire per send (the encryption moved inside the lock that was already taken).

**Audit.**
- Send entry points: main-thread `OutgoingPackets.Send_*` -> `NetClient.Send`; plugin threads via `Plugin.OnPluginSend` / `OnPluginSend_new` (ignorePlugin = true) -> `NetClient.Send`; the only flush is `GameController` -> `NetClient.Flush` -> `ProcessSend` (update thread). The TCP path is synchronous; the WebSocket path used to be asynchronous (see the audit follow-up below). Plugin hooks (`Plugin.ProcessSendPacket`, `PluginHost.PacketOut`) run before the lock, so no callback runs under it.
- Send-side resets: `ResetProtocolState` (called from `Connect`, `Disconnect`, `Terminate`) and `EnableCompression`: both take `_sendLock`. `Terminate` is called from the `catch` blocks of `ProcessSend`, outside the lock; `Disconnected` handlers run with no lock held. The lock order is "caller's own lock, then `_sendLock`", and `_sendLock` is never held while taking another lock or calling out, so no deadlock with the update thread, the parser queue lock or the plugin queue.
- Not under it: `_huffman` and the Huffman/Decrypt receive side (update thread only); `Connection` reports are interlocked.
- Remaining cost: `ProcessSend` holds the lock while `socket.Send` blocks (unchanged), so a plugin send waits for a slow socket write.

**Tests** (`NetSendLockTests`, 3; 684 in the full run pass): 6 threads x 3000 framed packets through the real Twofish/MD5 game cipher, a receiver with its own cipher instance decrypts the socket bytes and checks every frame, its fill bytes and per-thread order; a reset requested from another thread while a flush is between 4096-byte chunks must wait (10000 of 10000 bytes arrive); resets, sends and flushes in parallel leave only whole, ordered, non-duplicated frames and nothing stale after a final reset. Against the old `NetClient.cs` all 3 fail in 3 of 3 runs (cipher desync, torn frames, 4096 of 10000 bytes, `IndexOutOfRange` inside the circular buffer); with the fix 3/3 pass in 5 of 5 runs (about 0.9 s). Both builds (default and `-p:CuoDevTools=false`): 0 errors. No client login smoke run (optional, skipped; the per-frame path is unchanged).

**Audit follow-up (Codex, 2026-10-30): connection transitions, handshake, WebSocket writer.** Commit after 156d7a183.
- **Send admission is per connection (P1).** `NetClient` has a send epoch (bumped under `_sendLock` by every `ResetProtocolState`, so by `Connect`, `Disconnect` and `Terminate`). `Send` reads the epoch before the plugin hooks and drops the packet under the lock if it changed, so a plugin send that began on the old connection can't land, encrypted with the old login-cipher state, in the next connection's queue ahead of its seed.
- **Handshake ordering (P1).** A reset also closes sends. `BeginLoginHandshake(address)` / `BeginGameHandshake(seed)` initialise the cipher (and for the game server switch to game mode and clear the queue) under `_sendLock` and make the calling thread the only sender; `EndHandshake(token)` reopens sends (a stale token, from a connection replaced since, does nothing). `LoginScene.OnNetClientConnected` and `HandleRelayServerPacket` use them around the seed and the first / second login packet, with try/finally. Other threads' sends in between are dropped, not queued; plugin callbacks still never run under the lock. A `NetClient` that never connected starts open. The three cipher objects in `Encryption.cs` are static, so the protocol assumes one `NetClient` (`NetClient.Socket`); noted in the code.
- **WebSocket writer (P2).** `WebSocketWrapper.Send` no longer fires `async void SendCopyAsync`. `OrderedAsyncWriter` (new) is one ordered writer per connection: `Send` copies into a pooled buffer and enqueues; a single loop awaits each `SendAsync` in turn (no overlap), and the first failure goes through `InvokeOnError`, i.e. the connection's close path (`Terminate` on the next drain), with the rest of the queue dropped. `Disconnect` / `Dispose` stop accepting writes and let the queue finish.
- **Statistics (P3).** `Statistics.Reset()` (all sites, via `ResetStatistics`) and `Statistics.Update()` (in `Flush`) run under `_sendLock`, so the send counters are one consistent snapshot and a reset can't race an increment.
- **Tests** (`NetSendLockTests`, 12; forced interleavings, no sleeps hoping to overlap; every wait bounded, workers joined in finally; test seam `NetClient.BeforeSendLock`, null in production): reset during a flush blocks on the lock (the test waits for the reset thread to be blocked, then asserts all 10000 bytes arrived); the reset storm now also asserts resets ran and that a quiet tail of 50 frames arrives (the stream can't be vacuously empty); a plugin send paused after its hooks while `Disconnect` + new socket + login handshake + seed run is dropped; nothing is queued ahead of the game seed and plugin sends during the handshake are dropped, the post-handshake one is not; sends stay closed after a reset until `EndHandshake`, and a stale token doesn't reopen; send statistics; ordered writer (no overlap, order kept, failure reported once and the rest dropped). Against the previous commit's `NetClient` (with the new API shimmed as no-ops so the tests compile) the three admission / handshake tests fail; the two reset tests already pass there (they test the earlier fix) and fail on the original code as before.
- **Verification.** Both builds 0 errors; `dotnet test`: 691 tests, all pass in the last full run except the known flaky `ChunkLoadingTests.Prefetched_Regions…` in an earlier run (timing, unrelated). Login smoke (default build, JIT, kbatman3, `tools/ecs-parity/run_parity.py` with a throwaway encrypted settings file, `--timeout 120`, no cursor use): two consecutive client runs each logged in through the login server and the game-server relay, entered the world and walked (`go`, `walk` x4 incl. run), 270 ECS checks, 0 mismatches, 0 disconnects, clean exit 0. The WebSocket path was not run against a server (no WS shard here); only the writer is unit tested.

**Second audit follow-up (Codex, 2026-10-30).** Targeted fixes on top of ee2fcd212.
- **Epoch first (P1).** `Send` now reads the send epoch before the `IsConnected` check, so a sender paused there can't adopt the replacement connection (Codex saw `01-02-03-04-EE`). Validation under `_sendLock` is unchanged.
- **Handshake: defer, don't drop (P2).** A send made while sends are closed (another thread, or a plugin's outgoing hook injecting from inside the hook of the login packet: detected with a per-thread "inside the outgoing hooks" depth) is held in a small list (cap 256) and queued, in order and encrypted in the then-current mode, by `EndHandshake`; a reset discards it. So seed, 0x91, then the injected packet. A plugin that suppresses 0x91 and forwards a replacement from inside the hook gets the replacement queued right after the seed; one that rewrites the packet in place is accepted as the handshake packet (same call). Only a stale epoch (and the 256 overflow) drops now.
- **Counters and logging (P3).** `DroppedStaleSends`, `DeferredSends`, `DroppedDeferredOverflow`; a drop logs a rate-limited warning (one per second) outside the lock. The packet log line is written when a packet is admitted (under the lock, only if the log is on), no longer before admission.
- **Writer (P2).** `OrderedAsyncWriter`: the first failure closes admission and the loop discards the queue and finishes on its own (`Completion` completes without `Complete()`); a queued-byte cap (4 MB) fails the connection through the same path instead of dropping encrypted bytes. `WebSocketWrapper.Disconnect` always cleans up (once): stops writes, waits at most 2 s for the queue, closes the socket (2 s bound), then cancels and disposes; `Dispose` cancels a stalled send and disposes the transport at once.
- **Tests** (`NetSendLockTests`, 14): every worker runs in a `Scope` (exceptions come back through `Join`, all workers released and joined in a finally); the reset storm forces resets while senders are live, checks the whole captured stream before the tail, and requires frames to have arrived; stale-send tests pause the sender both inside `IsConnected` and before the lock; handshake tests go through the outgoing-hook seam (`NetClient.TestOutgoingHook`, null in production): injected packet, suppress and forward, rewrite in place; writer: failure without `Complete()`, byte cap, stalled shutdown by cancellation. Against ee2fcd212 (new surface shimmed) 8 tests fail (handshake / injection / held sends, the writer failure and cap, and the two stale-send tests, those two only through the new drop counter; Codex reproduced the IsConnected case directly). The WebSocket retirement itself (`RetireAsync`) is not unit tested (needs a server).
- **Verification.** Both builds 0 errors; 695 unit tests pass; the two-run login smoke (default build, kbatman3, `--timeout 120`) again: login, game-server relay, walk, 270 ECS checks, 0 mismatches, no disconnects, no "Send dropped" lines, exit 0.


## Flaky prefetch test (2026-10-30)

Base: poc/modern-client 4f84e8a71. Branch `poc/fix-flaky-prefetch-test`. Test only; no product code changed.

**Cause.** `Prefetched_Regions_Give_The_Same_Blocks_And_Never_Block_IsBlockReady` bounded every `IsBlockReady` call at 2.0 ms. In a loop of full-suite runs under bench.lock (one run per acquisition, old build) run 5 of 5 failed with `Expected value to be less than 2.0, but found 2.8137`, 0.44 s into the run: the first call in the process pays JIT and thread-pool start-up while other test classes start in parallel. It was not the `ready.Should().BeFalse()` assertion, and `PrefetchEnabled` is only written by this test (default true), so shared static state was not involved. No product race found in `MapLoader` prefetch.

**Fix.** The no-decode-on-the-caller guarantee is now asserted by counters after every call (`SyncLoads == 0`, `PrefetchWaits == 0`; `ready == false` whenever the call started a prefetch), and the wall time is bounded on the median of the five calls (10 ms) instead of each call. `PrefetchEnabled` is restored in `finally`.


## T0c: class-A fields become properties (2026-10-30)

Branch `bg/ecs-t0c-properties`, base `0acd0ef85`.

- **What changed** (commit 2edbcceb4). The class-A members of `GameObject`, `Entity`, `Item` and `Mobile` (the public fields listed in the census) are now auto-properties, so T1 can later move the setters behind the funnel without touching call sites. `Offset` is a `Vector3` property with `OffsetX`/`OffsetY`/`OffsetZ` accessors that write `_offset.X/Y/Z` directly; the 28 member-wise writes (`m.Offset.Z = 4`) in `BoatMovingManager`, `MovingEffect`, `DragEffect` and `Mobile` use them. The dead `DisplayedGraphic` setter and its storage are gone. `WriteSiteBudgetTests` counts `OffsetX/Y/Z` as `Offset`.
- **Write-site budget:** unchanged, 326 sites in 130 file/member pairs (the Codex replay; confirmed by the test, see below).
- **Codex audit:** no runtime regression found (references, struct mutation, reflection, codegen and the allowlist all checked). One P2: `tools/ecs-census/instrument.py` still matched field declarations, so 31 of its 43 patterns printed `NO MATCH` and the regenerated census silently dropped those members; `names.py` lacked `OffsetX/Y/Z`.
- **Fix.** `instrument.py` now builds its declaration patterns from member names and matches fields and properties alike, adds the three Offset accessors (46 declarations) and exits with an error, writing nothing, if any is unmatched or ambiguous. `names.py` has `OffsetX/Y/Z`; `analyze.py` folds them into `Offset` and `classify.py` classifies their writes as `struct-member`; `gen.py` and `audit.py` account for the folding. The transition plan (T1) now says the funnel must cover `Offset.set` and the three accessors.
- **Census regeneration check.** Ran the documented workflow on the T0c head with the new tools (46 of 46 declarations matched) and on its parent with the old tools (43 of 43), same method. Compiler-derived data is identical: pass 1 2595 member references (read 2296, assign 262, struct-member 28, compound 9), pass 2 409 references; per (member, kind) counts identical for all 86 pairs; the writes table (266 world writes), readers, summary, handler and method sections are identical except for line numbers that T0c shifted. Only the textual non-world section (section 7) differs: 933 hits in both, but 114 files instead of 115, and the `.X`/`.Y`/`.Z` hits that were really `Offset.X =` in `DragEffect`, `MovingEffect`, `Mobile` etc. now appear as `.OffsetX/Y/Z`. The committed census document was not regenerated (it has drifted from the tree for unrelated reasons, e.g. `DevBench.cs` and `Multi.cs` hits).
- **Timing.** NativeAOT, base and new interleaved, 3 runs each, 2 windows per run (tags `leg_brit`, `leg_ls`, `gpu_brit`, `gpu_ls`; `scratchpad/t0c/res.jsonl`). Mean update and world_prepare are within noise either way (about 8%): world_prepare base/new in ms: leg_brit 0.137/0.139, leg_ls 1.030/1.031, gpu_brit 0.054/0.052, gpu_ls 0.722/0.737. The only extra hitches in new come from one window with a 1.5 s pump stall, i.e. machine noise. No new client runs.

## Decode in-flight byte cap (2026-10-30)

Branch `poc/decode-inflight-cap`, base poc/modern-client c6bd97205. `DecodeBudget` (src/ClassicUO.Renderer): a FIFO byte budget for art/texmap decodes (FirstSightDecoder, bytes = pixels*4 at request time) and animation direction jobs (sum of frame W*H*4); gumps are not capped. (Since the Codex audit round below: the bytes are the pooled capacity, gumps bypass the budget, and cancellation releases without a drain.) Submit/Release on the main thread, never blocks, nothing is dropped (waiting items start as bytes are released); a single item above the cap is admitted alone. `CUO_DECODE_CAP_MB` overrides (0 = unlimited, counts only). FrameStats `decode_inflight_kb`, `decode_waiting`; DevBench prints `[decode-budget]` peaks per plan entry (holds included). 9 unit tests (DecodeBudgetTests; an earlier version of this entry said ten). 18 after the audit round below.

**Measured peaks** (AOT, plan world:gpu, house, bank, travel, ltravel, ls, brit, far, ls2; peak in flight per entry, KiB): cap 0 runs: house 5825 and 7508, travel 3782 and 3872, bank 766/883, far 0/571, others 0. 8 MiB run: house 7445 (cap not reached, 0 waiting). 4 MiB run: house 4095 (held at the cap), travel 2663. All-time peak uncapped about 7.3 MiB (7.4-7.5 MiB at worst), not the ~76 MiB feared.

**Choice: 16 MiB, as a safety net, not a throttle.** The measurements show the cap is not needed for performance: at 8 MiB (above every peak) nothing waited and hold ms equal cap 0; at 4 MiB the house peak was cut to 4 MiB and the settle hold was not longer (hold ms total 188 vs 255 / 212 for cap 0 / 8 MiB, within the run-to-run spread; unsettled frames came from animation pages, not from waiting art). 16 MiB is about 2x the largest observed peak, so it never delays art in the measured plans, and it still bounds a pathological case (a very large teleport or a mod with huge art) at a fixed ceiling instead of an unbounded one. Kept for that bound only.

**Paired run** (AOT, same binary for both arms; base = `CUO_DECODE_CAP_MB=0`, new = the 16 MiB default; 3 runs each, interleaved base,new,new,base,base,new; plan world:gpu, house, bank, travel, ltravel, ls, brit, far, ls2; job 20261030-192626292040, status ok, 990 s). The machine was noisy, so read the spread, not the means.

| metric (per full plan) | base (cap 0), 3 runs | new (16 MiB), 3 runs |
|---|---|---|
| waiting (any entry) | 0, 0, 0 | 0, 0, 0 |
| all-time peak in flight, KiB | 7432, 7488, 7390 | 7451, 7321, 7413 |
| travel entry peak in flight / art_pending | 3872 / 272 in all 3 | 3872 / 272 in all 3 |
| hold ms total | 371, 403, 378 (mean 384, spread 32) | 365, 384, 372 (mean 374, spread 19) |
| held frames | 166, 179, 152 | 146, 158, 165 |
| unsettled frames | 44, 46, 30 (mean 40) | 27, 52, 20 (mean 33) |
| travel upload us | 1814, 1134, 2017 (mean 1655) | 2040, 3411, 5476 (mean 3642) |

Result: the cap never engaged. Waiting stayed 0 on every entry of every run, and the in-flight peaks match between arms (the all-time peak, about 7.3-7.5 MiB, is under half the cap). Hold ms total and held frames are inside the run-to-run spread (base 371-403 against new 365-384), and unsettled frames vary more between repeats of one arm (base 30-46, new 20-52) than between arms. The one number that looks different is the travel entry's upload time (new mean 3.6 ms against 1.7 ms), but it is a single entry, the new repeats alone span 2.0-5.5 ms against 1.1-2.0 ms for base, so I read it as noise (the U+D frame-time column has similar outliers in both arms, e.g. far 41.07 in q1b, travel 19.47 in q2b). Three repeats cannot rule out a small upload-time effect; a longer series would be needed if it matters. Conclusion: no measurable difference at 16 MiB, as expected for a safety net.

**Correction (Codex audit round 1).** Both arms ran the same binary, so the bookkeeping this branch adds runs in both: the `Release` inside the timed art/texmap drain (lock, subtraction, queue check), the animation completion wrapper, and FrameStats' locked peak sample. It therefore does not explain the travel upload difference (3.6 against 1.7 ms) between the arms; with an empty wait queue the 16 MiB arm adds only admission comparisons at submission, outside the upload timing. That bookkeeping is a real cost against base c6bd97205, but it was not measured (the pair compared cap 0 and 16 MiB of one binary, not the branch against c6bd97205). The series was not extended.
**Verification.** Release build and `-p:CuoDevTools=false` build 0 errors; 728 unit tests pass at the final head.

**Codex audit round 1 (2026-10-30).** Codex (gpt-6-astra) reviewed 5806f5067 against c6bd97205 and found three issues; all re-verified against the code and fixed in dd37d8586 (after merging poc/modern-client f912272b8).
1. **High, fixed: cancellation stranded charges.** `FirstSightDecoder.Cancel` released only jobs already in `_ready`; a worker finishing after the cancel returned its pixels but kept its bytes, the decoder's budget waiters stayed queued (and were later started stale, keeping their charge), and nothing recovered without another drain of that decoder. Animations had no retirement path at all. Fix: each submission carries an exactly-once release token (`DecodeBudget.Item`: waiting, admitted, released; `Release(item)` gives the bytes back once and returns false afterwards). `DecodeQueue<T>` (new, used by both `FirstSightDecoder` and `Animations`) owns the epoch, the publish step and cancellation: `Cancel` bumps the epoch, takes the owner's waiting items out of the budget (`CancelWaiting`, which then admits other owners' waiters that now fit) and retires them, and drains the finished ones; a worker that publishes after a cancel returns its buffers and releases its bytes itself, before the enqueue that ends its use of the item, so a later drain only does the bookkeeping and its release is a no-op. `Animations.CancelPending` (called at GameScene teardown next to art and texmaps) retires cancelled directions and clears their cache slot so they load again on next use. Remaining by design: a cancelled job still counts in `Pending` / `PendingDirections` until the owner's next drain (no bytes or buffers held).
2. **Medium, fixed: gumps entered the shared FIFO.** Requests without `bytesOf` now start outside the budget (`StartUncounted`): not counted, never queued, nothing to release, so `Gump.Prefetch` can no longer spend its 20 ms waiting for art or animation releases. FIFO order among counted requests is unchanged.
3. **Medium, fixed by reserving capacity: the budget counted logical payload.** Chosen: reserve what the pool hands out, because it is cheap (one power-of-two round-up per buffer at request time, `DecodeBudget.PooledBytes`) and makes the cap bound held memory, not pixels. `ArrayPool<uint>.Shared.Rent(n)` returns max(16, next power of two) elements for every pooled size; a test pins `PooledBytes(n) == Rent(n).Length * 4` for sizes from 1 to 2^20+1. Art and texmap reserve `PooledBytes(pixels)`, animation directions the sum over frames (each frame is rented separately). The deliberate oversized-item exception remains (one item may exceed the cap alone). The reported peaks above (7.3-7.5 MiB) are logical payload; with capacity accounting the peak can only grow, by less than 2x per buffer for buffers of 8 elements or more (so below about 15 MiB, under the 16 MiB default); tiny requests can grow by more, because the pool's minimum is 16 elements (a 1-element request holds 64 bytes). Measured (job bench-000009, 2 runs of the same plan, cap 0, AOT of dd37d8586, capacity accounting): the all-time peak in flight was 7520 KiB in run c0a (reached during world prepare, before the first per-entry line; per entry after that: house 3580, travel 4904, bank 536, far 916) and 9384 KiB in run c0b (house; bank 1216, travel 4904, ltravel 1076, far 916). Both are below 12 MiB, so 16 MiB stays the default.
- **Upload-time note:** corrected above (same binary in both arms).
- **Tests** (DecodeBudgetTests, 18; was 9): the budget tests use the item API; new deterministic tests with the injectable starter (a list the test runs by hand, no thread pool): release is exactly once (also after the item is reused), `CancelWaiting` removes only that owner's waiters and starts what then fits, pooled bytes match the pool, cancel with no later drain (the audit's cap-64 scenario: nothing charged, nothing waiting, the waiter never started, buffer returned), cancel mid-decode with one admitted and one waiting (the worker releases its own bytes; the later drain does not release again; another decoder's charge is untouched), a cancelled worker frees room for another decoder's waiter without any drain, a gump is never blocked by an art waiter, and the animation lifecycle on the `DecodeQueue` Animations uses (complete; cancel with one finished, one running, one waiting; the running worker releasing after the cancel; cancel between decode and publish). The thread-pool cancellation test no longer drains after the cancel (the old one did, which hid the bug). With the stale-publish release and `CancelWaiting` disabled, 6 of the 18 fail. `Animations` itself is not instantiated in tests (it needs a GraphicsDevice); its glue is the same `DecodeQueue` calls.
- **Verification.** Both builds (default and `-p:CuoDevTools=false`) 0 errors; `dotnet test`: 738 pass; DecodeBudgetTests + FirstSightDecoderTests (24) 5 of 5 runs pass.

### Codex audit round 2 (2026-10-31)

Codex (gpt-6-astra) re-audited dd37d8586: the round-1 fixes (exactly-once release, cancellation, gump bypass, pooled-capacity accounting) hold; it traced publication against cancellation, waiter admission against cancellation, lock order, relog cache replacement and the PooledBytes formula (matches the .NET 10 SharedArrayPool) and found no race. It found two animation cleanup defects, both older than this branch; both re-verified and fixed here because they make the budget's buffer accounting wrong.

1. **Medium, fixed: a `DecodeFrame` throw lost the frames already rented** (worker loop in `DirectionJob.Execute`: the rentals sat in a local array assigned to the job only after every frame succeeded). The job now owns a `FrameBuffers` from construction; its `Decode` loop returns and clears every partial rental before rethrowing, and the worker still publishes the job so the main thread decodes the frames (fallback).
2. **Medium, fixed: an `Upload` throw on frame k leaked frames k..n, and the partial direction stayed cached** (`Animations.CompleteFrames`). `FrameBuffers.Upload` returns each buffer right after its upload, clearing the slot, and returns every remaining slot in a finally; `Complete`'s finally then releases the bytes, so buffers are back before the reservation is. On the exception path the incomplete cache entry is forgotten (`ForgetCached`, same rule as `Retire`: only if the slot still holds that job's sprites), so the direction loads again.
3. **Caveat noted (round-1 text amended above):** the "less than 2x per buffer" bound does not hold for tiny requests (16-element pool minimum).
4. **Follow-up, not fixed (pre-existing):** the stale-drain path `FirstSightDecoder.cs:141` resets `_state` to `StateNone` unconditionally for a job requested before a Cancel; the intended duplicate request needs an overlap Codex could not reach through supported calls (cancelled admitted jobs keep `StateRequested` until retirement, removed waiters never run), and it found no worsening on this branch. Left as is.

**Tests** (742 total; was 738): `FrameBuffersTests` (4) drive the production decode and completion loops (`FrameBuffers.Decode` / `Upload`, the code `Animations` calls) over the real `DecodeBudget` and `DecodeQueue` with a counting pool: a throw on the third frame after two successes returns all 3 rentals once, a later Cancel returns nothing more, budget 0; a failed worker falls back to main-thread decode and the pool returns equal rentals; an upload throw on frame 1 of 4 returns all 4 buffers exactly once (1 after its upload, 3 in the finally), budget 0, later Cancel changes nothing; the normal path returns each once. `Animations` itself needs a GraphicsDevice and cannot be built in tests, hence the extracted helper. **Verification:** both builds 0 errors; decoder tests (DecodeBudget, FrameBuffers, FirstSightDecoder: 28) 5 of 5 runs pass.

**Pooled capacity peak (bench-000009), measured:** 7520 KiB (run c0a) and 9384 KiB (run c0b, house entry; 9.2 MiB), with no waiting and no cap engaged (cap 0). Coordinator rule: above 12 MiB raise the default to 32 MiB; the peak is below it, so `DefaultCapBytes` stays 16 MiB (about 1.7x headroom over the worst run). Memory cost, stated honestly: the cap is a ceiling, not a held allocation. In practice the decode path holds the measured peak, at most about 9.2 MiB per client at once, and that memory is rented from `ArrayPool` and returned as decodes finish; nothing is preallocated for the cap. At 16 MiB the ceiling bounds the worst case at 16 MiB of in-flight decode buffers per client. It is a safety net that should never engage in normal play. Noise: two runs differ by 1.9 MiB (house is larger in c0b), so the peak varies run to run with decode timing.


## Chunk pool per World (2026-10-30)

Base: poc/modern-client c6bd97205. Branch `poc/chunk-pool-per-world`.

**What changed.** `Game/Map/ChunkPool.cs` (instance, owned by `World.ChunkPool`, capped at `PREDICTABLE_CHUNKS`, refuses a chunk whose `Chunk.Owner` is another World). `Chunk`'s static `_pool`/`PooledCount`/`ClearPool` are gone; `Chunk.Create/Destroy/Prefill` go through `world.ChunkPool`. `MapObjectRecycler` takes an owner World (Retire/Prefill ignore other-World objects). `World.ReleasePools()` (from `Client.Unload`) clears both pools. `World.WarmupDone` replaces the static `MapWarmup._prefilled`: per World and per login (`World.Clear` re-arms it); the deliberate GC collection still arms once per process.

**Findings.**
- Production has one World (`Client.cs`), and relog is `World.Clear` on the same World, so the static pool already persisted across relogs (bounded at 300 chunks). The stale-`_world` defect only hit several Worlds (tests), and the static pool would also have kept an old World alive.
- Fail-on-base evidence: two temporary tests on c6bd97205 both FAILED (`Assert.Same(w2, owner)`): a second World's `Chunk.Create` returned a chunk whose `_world` was the first World, and a chunk destroyed by w1 was handed to w2. Both pass on the branch (`WorldPoolOwnershipTests`, 8 tests).
- Land/static recycler was already per World (`World.MapRecycler`); only the chunk pool was static.

**Measurement: relog series** (NativeAOT, `CUO_BENCH_RELOG=3:60`: 3 logins x 60 s, kbatman2, order b1,n1,n2,b2, one job `chunk-pool-relog`, 734 s, status ok). Relog really happened in all four runs: `[relog] in game` x3, `[relog] end of dwell` x3, `[relog] done`, `[gcs] session ended` x2 (the first logout's session is not logged separately).

| run | private MB at end of dwell (login 1/2/3) | heap MB | collections gen0/1/2 at end of login 1 -> login 3 | chunk pool end of dwell | `[warmup] pools filled` |
|---|---|---|---|---|---|
| base b1 | 1904.2 / 1906.1 / 1904.6 | 453.7 | 11/9/3 -> 11/9/3 | 275 | (no log line; prefill once per process) |
| new n1 | 1926.0 / 1928.4 / 1927.0 | 470.1 | 11/9/3 -> 11/9/3 | 275 | login 1: 300 chunks, 19200 lands, 16800 statics, 15.8 ms; logins 2, 3: 0 chunks (pool still full), 0.0 ms |
| new n2 | 1911.5 / 1913.0 / 1911.6 | 458.8 | 11/9/3 -> 11/9/3 | 275 | login 1: 20.6 ms; logins 2, 3: 0.0 ms |
| base b2 | 1883.1 / 1885.5 / 1883.7 | 425.6 | 11/9/3 -> 11/9/3 | 274-275 | (none) |

- Collections: 9/7/3 at "in game" login 1 (startup), +2 gen0 and +2 gen1 inside the first 60 s (includes the deliberate warm-up collection), 0 in logins 2 and 3. Identical in base and new, all four runs.
- Growth across logins: none. Private MB moves +0.7 to +2.4 MB between login 1 and 2 and returns by login 3; heap and collection counts are exactly flat. Base and new alike.
- Pool sizes (after Clear, at "in game" login 2/3): chunks 281, lands 17984, statics 15065; at end of dwell 275 / 17600 / 14648. Same in base and new.
- Noise between repeats: base b1 vs b2 differ by 21 MB private (1.1%) and 28 MB heap; new n1 vs n2 differ by 14.5 MB private and 11 MB heap. Pause totals ranged 47-94 ms across runs with no pattern. New reads about 25 MB higher in private than base on average (both new runs above both base runs), which is about one repeat-noise width; two runs per side cannot separate it. Nothing in the code adds memory (same caps, same pool contents), so I take it as machine noise; a longer interleaved series would be needed to prove it.
- New's warm-up per login is a top-up: with the pool still full from the previous session it fills 0 chunks, so a relog costs nothing extra. The pool is only re-filled when `ReleasePools` ran (client unload) or a new World is built.

**Verification.** Both builds (`-c Release`, `-p:CuoDevTools=false`) 0 errors; `dotnet test -c Release` 728 tests pass after merging poc/modern-client f912272b8. DevBench got `CUO_BENCH_RELOG=cycles:dwellSec` (Socket.Disconnect + new LoginScene with SkipLoginScreen so autologin logs in again).

### Codex audit round 1 (chunk pool per World, 2026-10-30)

All four findings re-verified against the code; none changed the one-World relog/unload result above.
- **#1 static `Map._terrainChunks` (pre-existing): fixed.** The array is now an instance `readonly` field, allocated per Map at its `BlocksCount` (the `block >= _terrainChunks.Length` check in `GetChunk2` is gone: the length equals `BlocksCount`). Hot path: static array access became an instance field access, no new indirection. Cost: every `new Map` allocates its own array (up to ~3.6 MB for a 458k-block map) instead of reusing the static one; this happens per login and per map change, and in `WorldMapGump.ChangeMap`, which builds a second Map only for `Index`/`GetTileZ` (the old code shared the main map's chunk slots there by accident). Test seam `internal Map(World, index, blocksCount)`; test `Two_Maps_Keep_Their_Own_Chunks` (a larger second map does not replace or orphan the first). `_blockAccessList` stays static (scratch cleared per call, main thread only).
- **#2 duplicate returns (pre-existing): fixed.** `Chunk.InPool` (set/cleared by `ChunkPool`); `Chunk.Destroy` returns at once when pooled and `CanAdd` refuses a chunk already in the pool. `MapObjectRecycler.Retire` returns when `InRecycler` is set. Tests: `A_Double_Destroy_Does_Not_Alias_Two_Creates`, `A_Recycler_Retires_An_Object_Only_Once`.
- **#3 `ReleasePools` not terminal: fixed.** `ChunkPool.Close()` / `MapObjectRecycler.Close()` clear and then refuse returns and prefill; a chunk destroyed after release clears its mesh (`Mesh.Clear`) instead of re-pooling. Test `A_Destroy_After_ReleasePools_Is_Not_Pooled`.
- **#4 warm-up test with `CUO_WARMUP_CHUNKS=0`: fixed.** The test returns early when `MapWarmup.Chunks <= 0`. The WorldPoolOwnership tests pass with `CUO_WARMUP_CHUNKS=0`.

Verification after the round: both builds 0 errors; `dotnet test -c Release` 732 pass.

### Codex audit round 2 (chunk pool per World, 2026-10-30)

Both findings re-verified against the code (no gump-created chunk leak or height-lookup change was found).
- **P2, a ~3.5 MiB LOH slot array per `new Map`: fixed.** Round 1's per-instance array made every login, facet change and `WorldMapGump.ChangeMap` allocate `BlocksCount` references (the old static array was reused).
  - The gump no longer builds a `Map`: it holds a `FacetView` (facet index + `GetTileZ` forwarding to `MapLoader.GetLandZ`, exactly what `Map.GetTileZ` did) and `Sextant.Format/FormatString` gained facet-index overloads (they only read `Index`).
  - Live maps reuse storage owned by the World: `World.RentChunkSlots` hands out the spare array if it is large enough (taking it out of the World, so ownership is exclusive while a Map is active), else allocates; `Map.Destroy` clears the array and returns it via `ReturnChunkSlots` (once per Map, so a stale second `Destroy` cannot clear a reused array). `ReleasePools` drops the spare. Two simultaneous Maps never share an array.
  - Tests: successive maps on one World get the same reference (and cleared slots); a still-active map's array is never handed out; a too-small spare is not reused; `WorldMapGump` has no `Map` field; `Two_Maps_Keep_Their_Own_Chunks` still passes.
- **P3, `InPool` set before `Enqueue` succeeds: fixed.** `ChunkPool.Add` now enqueues first and sets `InPool` after.

Verification: both builds 0 errors; `dotnet test -c Release` 736 pass (732 + 4 new).
## Text in the pack: audit fixes, overrides, block-compressed cliloc (2026-10-30)

Branch `poc/pack-text` (follows "Text in the pack" above; poc/modern-client merged in at 5e0356fee). Codex audit of the first commit: nine findings (coordinator scratchpad `astra-ptext/out.md`), all fixed; then the owner's two requests. Spec: pack-format.md "Text" (cliloc and untrusted-input parts rewritten). Scratch: session scratchpad `ptext2\` (packs, `run.py`, `job1.sh`, benchmark tables).

**Audit fixes.**
1. **(P1) Accepted font records crashed text generation.** `GeneratePixelsUnicode` now clips every raster write on the left as well as the right (the main raster and the solid, black-border and underline passes): a glyph with `offsetX = −2` at the start of a line indexed `pData[-1]`. ASCII: a glyph with a size but no texel array is now malformed (`AsciiFontCodec.RawSize` returns −1, the font is absent; `Encode` refuses it); the client only ever has 0×0 glyphs without texels. Tests render the accepted records (`GenerateUnicode` with every style flag, three alignments and five widths; `RenderSingleGlyphUnicode`/`ASCII`, the atlas path), not only decode them: offsets −128..127, 127×127 glyphs, a 255×255 ASCII glyph.
2. **(P2) Speech** reads its fixed header through a FileStream, checks the counts, the caps and that the sizes add up to the file's exact length (64-bit arithmetic), and only then allocates. A 1 GiB file with a valid header is refused with under 1 MB allocated (test).
3. **(P2) Font store topology** is checked from the store header before the tables are allocated (`RecordStore.Open(path, magic, minSlots, maxSlots)`): unifont.bin must have exactly 5,120 slots, fonts.bin 1..256. A wrong topology fails the file as a whole, so the MUL fonts serve (the audit's 32-byte zero-slot FNTU with every font flagged present is refused).
4. **(P2) A corrupt pack language no longer blocks the loose files.** Loading is one `Build`: the pack serves when its store opens and resolves and its language records parse; otherwise the Cliloc.* files serve, with the language resolved again for them (`Cliloc.<lang>`, else `Cliloc.enu`). The audit's case (pack `deu` corrupt, no Cliloc.deu, Cliloc.enu present) now loads English from the file (test).
5. **(P2) DevBench `texts`** holds its tooltip hover through `Input.Mouse.Override` only (poc/modern-client's `HoldMouse`) and releases it (`_textHover`, `_mouse`, `Override` cleared) one second after the next shot, or 10 s after the step, whichever comes first.
6. **(P2) Converter language discovery** (`ClilocDiscovery.Find`, in Uo.Pack so it is unit-tested): the `Cliloc.` prefix in any case; exports told apart by content (BWT-compressed, or a raw table whose records add up exactly to the file's end), not by an extension blacklist, so `Cliloc.bak` (Bashkir) is a language and `CliLoc.csv` is skipped; `--cliloc-skip a,b` is the explicit list. No language, no enu, or two files for one code fail the conversion. Verification discovers the set again on its own (a case-insensitive listing, a BinaryReader walk, the frozen loader) and compares it with the pack's.
7. **(P3) Verification goes through the runtime rules:** `ClilocStore.Resolve` (slot order; a record reached twice, an unknown meta2, a missing or duplicate language code, or two overrides records reject the store) and `ClilocStore.ReadTable`, the code the client runs.
8. **(P3) Reload** builds a complete state (tables, packed overrides, Clilocs.txt) and publishes it once; a failed load (Cliloc.enu gone, an unreadable file) keeps the old state. A reader thread saw no missing string across 50 reloads and a failing one (test).
9. **(P3) First use is coordinated:** a unicode page or an ASCII font loads under a striped lock (64 stripes) with a recheck, so concurrent callers wait for the one read instead of decoding (or rereading the MUL glyphs) again; a bad record is claimed with `Interlocked.Exchange`, so it is logged once. With MUL files, page reads still share the file's seek position under `_mulLock` (UOFile has one position), so a cold read of another page can still make the main thread wait for it.

**Overrides (owner request).** The converter folds the client folder's `Clilocs.txt` into `text/cliloc.bin` as an overrides record: the same ids/ends/blob payload as a language, `meta2` kind 1, no language code, its own slot after the languages (`--no-cliloc-overrides` leaves it out). Client and converter parse the text with one function (`ClilocOverrides.Parse`). A lookup tries a loose `Clilocs.txt` (still read when present, for customising and quick dev edits), the packed overrides, the language, then enu. A corrupt overrides record is ignored and logged; the languages still serve. The overrides record also applies when the pack's languages can't serve and the Cliloc.* files do. Tests: the precedence of each layer (loose > packed > language > enu, with and without the loose file, under enu and deu); corrupt overrides records (malformed, too small, corrupt Brotli: absent, never thrown); stores with two overrides records or one with a language code (rejected as a whole). The test client folder has no Clilocs.txt, so the scratch pack has no overrides record.

**Block-compressed cliloc (owner request: measurement only; not the default).** *Owner decision 2026-10-31: cliloc ships uncompressed (flat); the blocked layout is removed (see "Codex audit round 2" below). The measurements stay here for the record.* `--cliloc-blocks` stores each language with its ids and ends raw and its UTF-8 blob cut at string ends into Brotli blocks of about 64 KiB (`--cliloc-block-bytes`), each decoded on the first lookup inside it (once per block, under the table's lock); `meta2` bit 8 marks the layout. zstd is not a dependency of the solution, so only Brotli was measured (no dependency added). Method: `ClilocBlockBench` (unit benchmark, `DOTNET_TieredCompilation=0`) runs the client's load path (open, resolve, read and parse enu; deu over enu), median of 15 after 3 warm-ups, two series (ranges below); first-lookup cost per block = one id per block, timed alone.

| cliloc.bin (8 languages) | size | enu load | deu + enu load | allocated at load (enu) | held (enu) | first lookup in a block |
|---|---|---|---|---|---|---|
| flat, raw (ship) | 15.56 MiB | 1.52–1.68 ms | 1.77–1.99 ms | 4.60 MiB | 3.70 MiB | — |
| flat, whole-record Brotli (`--cliloc-brotli`) | 4.07 MiB | 8.45–9.21 ms | 9.96–10.76 ms | 4.60 MiB | 3.70 MiB | — |
| blocked, 32 KiB (90 enu blocks) | 6.61 MiB | 0.66–0.73 ms | 0.92 ms | 2.73 MiB | 1.83 MiB | median 96–103 µs |
| **blocked, 64 KiB (45 enu blocks)** | 6.41 MiB | 0.54 ms | 0.63–0.64 ms | 2.69 MiB | 1.79 MiB | median 156–170 µs, max 214–231 µs |
| blocked, 128 KiB (23 enu blocks) | 6.24 MiB | 0.55–0.62 ms | 0.62–0.71 ms | 2.66 MiB | 1.76 MiB | median 314–323 µs, max 443–584 µs |

"Held" is the payload kept (index plus text, compressed when blocked); every layout also allocates its string cache (0.9 MiB for enu's 117,725 ids). With every block decoded, a blocked table holds 4.6 MiB (the decoded blocks plus the compressed copy). The 32 KiB row's max (2.3 ms) was the series' first block decode paying JIT, not the block. Busy scene: see below.

**Checks.**
- **Converter** (`--only fonts,cliloc,speech` into the scratch pack): fonts (763,025 glyphs), cliloc (8 languages, 941,936 strings) and speech (6,116 entries) EXACT; `CliLoc.csv` skipped by content. `--cliloc-blocks` (211 blocks), 32 and 128 KiB blocks and `--cliloc-brotli` also EXACT through the runtime reader.
- **Unit tests:** 746 pass in the default build and with `-p:CuoDevTools=false` (`CUO_TEST_TEXT_PACK` = the scratch pack). New: `TextPackAuditTests` (13 tests: the cases above, all synthetic) and `ClilocBlockBench` (returns at once without `CUO_CLILOC_BENCH`). The existing `TextPackTests` pass unchanged on the data.
- **Client runs, first job (`bench-000002`, `ptext2/job1.sh`): no pack text was tested.** `run.py` read `TP_PACK` but never used it: settings.json and `-pack` named `C:\Repositories\uo-packs\classic-1.0`, which has no `text/` section. Every one of the seven runs logged "the pack has no cliloc section" / "no font section" and loaded cliloc, fonts and speech from the MUL files (`cliloc blocks decoded 0 (enu 0 of 0) (fonts MUL, cliloc MUL)` in the busy-scene runs, blocked and flat alike). So the run says nothing about pack fidelity or blocks touched. The client's fallback did not hide anything: a pack store that fails to open already logs at Error, and none did. What the job does show, all on MUL text: the font clipping fix leaves real text unchanged (the old-binary comparison differs only in the world viewport, a different scene state, and the journal lines; the top bar, paperdoll and help gump text match pixel for pixel, also in the UI shot). The 36,024-pixel "pack vs `CUO_PACK_TEXT=0`" difference is live content: the second run's journal has an extra "Carson: Carson the innkeeper" line that shifts the lines below it. The job's "failed" status was the runner's out.log bug, not a client failure (all seven runs exited 0). `run.py` now passes `TP_PACK` (scratch fix); the rerun is job `bench-000004` (`ptext3/job2.sh`), results below.
- **Client runs, second job (`bench-000004`, `ptext3/job2.sh`, 7 runs, status ok, 282 s): pack text confirmed loaded, fidelity clean.** Every run except SM logged `Asset pack: ...\ptext2\pack` (flat) or `pack-blk` (blocked), `Cliloc loaded ... (pack, 'enu': 117725 strings)`, `Fonts loaded ... (ASCII pack, unicode pack)`, `Speech: pack, 6116 entries`; the busy-scene lines end `(fonts pack, cliloc pack)`, `pack True`. SM logged `CUO_PACK_TEXT=0; reading Cliloc.* files` / fonts / speech.mul (MUL, 117,725 strings). The one Warning in every pack run, `Cliloc: the pack has no 'ivl'; rolled back to enu`, was read here as the expected fallback of an enu-only test pack; that was wrong: `ivl` is the invariant culture's code (the build uses invariant globalization), not a language, so every unset-language user got it. Fixed in round 2 (#6 below). No Error lines in any log.
  - **Fidelity verdicts (PNGs and diffs looked at).** Pack vs MUL text, same binary: `diff-PM` 0 of 2,073,600 px, `diff-PM-ui` 0 of 921,600 px, so byte-identical rendering of the journal, top bar, paperdoll and help gump text (the previous job's 36,024 px was the extra live journal line; this pair has none). `diff-P-prevM` (this pack shot vs bench-000002's MUL shot) 33,347 px, bounding box x 2..460, y 916..1079: all in the journal strip at the bottom left (extra "Carson: Carson the innkeeper" lines shifting the journal), nothing in the top bar, paperdoll or help gump: live content only. `diff-P-old-ui` (vs the old binary's UI shot) 531 px, bounding box x 45..76, y 621..714 of the 1280x720 UI shot: the journal timestamp column ("20:01" clock text differs between runs), live content only. No static-text pixel difference anywhere.
  - **Load, brit scene (SP vs SM, cliloc/fonts/speech):** pack 1.5 ms / 4,716 KB, 0.2 ms / 66 KB, 1.2 ms vs MUL 8.7 ms / 18,485 KB, 14.0 ms / 965 KB, 13.1 ms; whole `[load]` 67.5 ms / 95,644 KB (GC heap 76,099 KB after) vs 99.3 ms / 110,587 KB (91,518 KB after).
  - **Busy scene (lsgumpopen: journal, allnames, 40 tooltips, help gump), blocked vs flat, two runs each (noise small: load 67.9-71.0 ms):**

    | | blocked (LB1, LB2) | flat (LP1, LP2) |
    |---|---|---|
    | cliloc load | 0.9-1.0 ms | 1.5 ms |
    | cliloc allocated | 2,760 KB | 4,716 KB |
    | `[load]` total | 67.9 / 68.7 ms, 93.8 MB | 70.2 / 71.0 ms, 95.6 MB |
    | GC heap after load | 64,439 KB | 66,267 KB |
    | blocks touched, journal step | 17 of 45 (enu) | n/a (whole table decoded) |
    | blocks touched, after help gump | 19 of 45 | n/a |
    | ms on first use | 4.639 / 4.563 ms (17 blocks, about 0.27 ms each, includes first-call JIT); help gump added 2 blocks for 0.354 / 0.338 ms (about 0.17 ms each) | 0 |
    | cliloc strings created | 129, then 141 | 129, then 141 |
    | worst frame in the steps | 6.06 / 6.19 ms (journal), 18.3 / 14.5 (allnames), 26.3 / 17.3 (tooltips), 7.2 / 5.0 (help) | 9.35 / 4.79, 27.5 / 14.1, 25.4 / 17.5, 32.4 / 9.3 |

    All first-use block decodes happen in the journal step (the journal's system lines and names); allnames, tooltips and the help gump touched no new block beyond two. Worst frames are dominated by tooltip/allnames work and are no worse blocked than flat. The cost of the blocked layout is therefore a one-time ~4.6-5.0 ms spread over the first journal and help use, and 19 of 45 blocks (42%) decoded after the whole busy scene. SLB (blocked pack, large-scale spot with the texts plan) loaded the same (cliloc 1.0 ms / 2,760 KB, `[load]` 66.3 ms) but the `texts` plan does not print a blocks-touched counter, so no count there; its texts shot is in `shots-LB` (not diffed).
  - **Recommendation for the owner.** Ship the pack text (it is pixel-identical to MUL, loads cliloc 6x faster and 4x smaller than the MUL reader, fonts 70x faster, speech 11x faster). Between flat and blocked: blocked saves about 1.9 MB of heap at load (2,760 vs 4,716 KB allocated, and 1.8 MB less GC heap) and loads cliloc faster (1.0 vs 1.5 ms), and after a busy scene still holds 26 of 45 blocks undecoded; the price is about 4.6-5.0 ms of first-use decoding on the calling thread (the main thread for UI text), spread over a few frames, with no frame-time regression seen. The measured difference is small in absolute terms (about 2 MB); choose blocked if memory per instance matters (several clients at once), flat if the simplest code path matters. Whole-record Brotli stays the smallest on disk (4.07 MiB) but costs 8.5-9 ms at startup, so it is not recommended.

### Codex audit round 2 (coordinator scratchpad `astra-ptext2/out.md`) and the owner's cliloc decision (2026-10-31)

**Owner decision: cliloc ships uncompressed; the block-compressed layout is dropped.** Removed: `--cliloc-blocks` / `--cliloc-block-bytes` (converter), `ClilocTable.ToBlocked` / `TryParseBlocked` and the lazy block reader (`Block`, `BlocksLoaded`, `BlockCount`, `StatBlockLoads/Ticks`, `Broken`, `TryUtf8At`), the DevBench text-stats "cliloc blocks decoded" counter, the converter's "blocked layout" verification note, `ClilocBlockBench` (it existed only to compare layouts) and the blocked round-trip test. Kept: the flat layout, the opt-in whole-record `--cliloc-brotli`, the overrides record. `meta2` bit 8 is now `ClilocStore.RetiredLayoutBlocked`: `Resolve` throws "record N uses the block-compressed layout (meta2 bit 8), which is no longer supported; rebuild the pack's text with a current converter" (the client logs it at Error and reads the Cliloc.* files), and `ReadTable` refuses such a record on its own, so it is never read as flat (test: a flat payload behind the bit, alone and as deu under a valid enu). pack-format.md and testing/pack-loading.md no longer describe the layout.

| # | Finding | Disposition |
|---|---|---|
| 1 | P2: blocked cliloc has no total decoded-size cap | Moot, blocked layout removed. |
| 2 | P2: reload can publish an incomplete state | **Fixed.** `Build` tells an absent `Clilocs.txt` (optional: published without it) from one that exists but can't be read (`ReadOurs(out failed)`, logged at Error); a non-English load from the files needs `Cliloc.enu` under it. On a reload either failure aborts publication and the old state stays ("keeping the strings loaded before"). The first load has nothing to keep, so it serves what it read and logs the Error (the alternative, no strings at all, is worse). Tests: deu over enu, `Cliloc.enu` deleted, reload keeps English-only ids (and a fresh first load serves deu alone); `Clilocs.txt` held exclusively during a reload keeps the old overrides and the old language strings, and a deleted one then publishes without overrides. The pack's overrides record that fails to parse is still ignored (round 1, judged sound). |
| 3 | P2: a corrupt block gives order-dependent fallback, no diagnostic | Moot, blocked layout removed. |
| 4 | P3: the converter can emit blocks its reader rejects | Moot, blocked layout removed. |
| 5 | P3: the `texts` mouse override survives a disconnect | **Fixed.** `UpdatePlan` ends the hover (`EndTextHover`: `_textHover`, `_mouse`, `Input.Mouse.Override` cleared) before the out-of-game return, when the game was left or the deadline passed. |
| 6 | P3: `ivl` is the invariant-culture sentinel | **Fixed at startup.** `Main.cs` normalises an unset language, an OS language that reads as `IVL` (invariant globalization) or `ZZZ` (unknown), and a stored `IVL` (earlier runs saved it) to `ENU`, so settings, the cliloc loader and the language sent in packets (`GameActions`, unicode prompt response) all get `ENU`; logged at Info ("language: not set and the OS language is not available; using ENU"). The loader's Warning stays for a language explicitly set but missing. Upstream ClassicUO (`origin/main`) has the same code and `InvariantGlobalization` since 1598d1df4 (2024-08-15), so it sends `IVL` too. Not added (proposal): under invariant globalization the real UI language is still available from Win32 `GetUserDefaultUILanguage` (an LCID, mapped to the UO codes ENU/DEU/FRA/ESP/JPN/KOR/CHT/CHS/RUS/PTB/ITA by primary language). |

**Checks.** Both builds 0 errors (default and `-p:CuoDevTools=false`), converter builds. Unit tests 748 pass (746 − `ClilocBlockBench` − the blocked round-trip test + 4: retired-layout rejection, the two reload cases, the language normalisation).

**Main pack regenerated, client check (bench-000012).** The converter was run in place on `C:/Repositories/uo-packs/classic-1.0` (`--only fonts,cliloc,speech`, flat cliloc), inside the job so no client read the pack meanwhile; the previous manifest is kept at `C:/Repositories/uo-packs/classic-1.0/manifest.json.2026-10-31.bak`. Verification: fonts EXACT (10 ASCII fonts x 224 glyphs, 20 unicode fonts, 763,025 glyphs), cliloc EXACT (8 languages enu/chs/cht/deu/esp/fra/jpn/kor, 941,936 (language, id) strings), speech EXACT (6,116 entries); round-trip verify True. New `text/` sizes: cliloc.bin 16,320,984 B (15.56 MiB, flat), unifont.bin 7,888,086 B, fonts.bin 35,403 B, speech.bin 144,189 B. Client logs: SP (pack text) read cliloc ('enu', 117,725 strings, 1.2 ms), fonts (ASCII pack, unicode pack, 0.1 ms) and speech (pack, 6,116 entries) from the pack; SM (CUO_PACK_TEXT=0) read the MUL files (cliloc 8.8 ms, fonts 13.8 ms, speech 13.0 ms). Both log "language: not set and the OS language is not available; using ENU" at Info; no `ivl` Warning. Pixel diff pack vs MUL: 35,078 px (screen) and 13,090 px (UI), all inside the journal area (bounding box x 67..458, y 916..1079). Top bar, paperdoll, name plate and help-gump text are identical. The journal difference is live content: the MUL shot received an extra "Carson: Carson the innkeeper" line, which scrolled the journal by one line; the lines both shots share ("status of a young player for 28 more hours", Kbatfour [Young], "The chat system has been disabled") render the same text, only offset vertically. No static-text difference.

### Codex audit round 3 (coordinator scratchpad `astra-ptext3/out.md`) (2026-10-31)

One P2, fixed: `ReadOurs` treated `File.Exists == false` as "Clilocs.txt absent", but `File.Exists` is false for access errors too, so an unreadable file was dropped silently (no Error on first load, and a reload published without it). It now reads directly; only `FileNotFoundException` / `DirectoryNotFoundException` mean absent, any other I/O or access failure logs at Error and sets `failed`, so a reload keeps the old state. New test `Clilocs_Txt_Missing_Is_Absent_And_An_Unreadable_Path_Is_A_Failure` (a directory named Clilocs.txt, no ACLs). Unit tests 749 pass; everything else in the round was clean.

## Memory per client: palettes, facet switch, grow on demand (2026-10-31)

Branch `poc/mem-reduce` from poc/modern-client 2ced75119. Follows "Memory footprint per client: first pass" (branch `poc/mem-footprint`, `docs/modern-client/memory-footprint.md` there) and the owner's decisions of 2026-10-31: players run several clients at once, so per-client memory matters. Three commits, one per change. Rendering is unchanged: no shader, bake or sampling change; layer indices and instance offsets survive every growth step.

**1. Anim palettes: no CPU copy (71b1a6f46).**
- Readers of `PackSet.Palettes` (18.5 MiB `ushort[]`, kept for the pack's lifetime): only `Renderer.CreatePaletteBuffer` (the GPU upload) and a log line in the standalone `Uo.Client` (palette count). Re-upload paths: every renderer re-creation (facet change before change 2, DevBench `reinit`, startup) goes through a new `Renderer`, so the upload must be repeatable, but it needs no long-lived copy.
- Now `PackSet` keeps only `PalettePath`, `PaletteCount` and `PaletteOffset`; the renderer reads palettes.bin straight into the mapped transfer buffer (no managed array at all, not even a transient one). `PackSet.ReadPalettes()` reads a CPU copy for a future reader that wants one.
- The frame → palette table (`uint[]`, 4.8 MiB) was read twice: by `AnimationsLoader` and by `PackSet`. The client now passes the loader's table through `PackTables.FramePalette`.

**2. Gpu world: facet switch in place (5e0948ad4).**
- Before: the startup probe built the world pass for map 0; the login on Trammel disposed it and built map 1 (renderer, page arrays, instance buffer, chunk store, page-job buffers, dynamic array), leaving about 34 MiB of dead LOH and re-allocating every GPU array. Every in-game facet change did the same.
- Now `WorldRenderer.ChangeFacet(pack)` swaps the `PackSet` (same pack, `WithFacet`), drops the chunks and regions (`ChunkStore.ChangeFacet`), and keeps the device resources, the resident pages, the workers and every CPU array. Bakes and region loads in flight are discarded when they finish; their regions are orphaned, not pooled, so no worker writes into a region the new facet reuses. A failed switch falls back to the old rebuild.
- The world object now survives a facet change, so the client's two caches keyed on it (GPU item cache, static season remap) also key on `world.Pack`. `WorldSettleGate` already treats a facet change as a discontinuity (hold). The new pack's bake switches (water wobble, remaps) start fresh, exactly as with a rebuilt world.

**3. Grow on demand (3615c22ca).**
- Art page array 12 → 24 layers, anim layers 32 → 64 → 128, instance buffer 256k → 512k → 1M → 2M instances. The ceilings are the old fixed sizes. `CUO_GPU_GROW=0` starts at the ceilings (A/B).
- Policy (`Uo.Render/GrowthPolicy.cs`, unit-tested): never at the ceiling; at once when starved (a loaded page with no free layer, or an instance allocation that failed last frame), so growth always beats eviction below the ceiling; at 75% occupancy during a hold (the settle gate's stream-only frames) or a camera jump; otherwise after 120 frames at 75%.
- A step creates the larger texture or buffer and copies the old contents GPU-side at the start of the frame's copy pass, before that frame's uploads (`SDL_CopyGPUTextureToTexture` per resident layer and mip; `SDL_CopyGPUBufferToBuffer`), then releases the old one (SDL frees it after the frames in flight). Same command buffer, same layer indices and offsets: nothing is dropped or flickers.
- Each step logs `GPU world: grown at frame N: art layers a/24 (k steps, last x->y t ms, max, total); anim layers ...; instances ...` (main-thread time: create + record; the GPU copy runs inside that frame). The `GPU world:` trace line now ends with `instances used u/c`.

**Memory trade-off log (per client).** Computed from the sizes; the measured numbers come from job bench-000035 (pending).

| item | before | after | saving | what it costs |
|---|---|---|---|---|
| anim palettes CPU copy (managed LOH, resident) | 18.5 MiB | 0 | 18.5 MiB | each new renderer reads 18.5 MiB from palettes.bin (once per session now, see 2) |
| frame → palette table, second copy (managed LOH, resident) | 4.8 MiB | 0 | 4.8 MiB | none (shared with AnimationsLoader) |
| second world pass at login (dead managed LOH until a gen2; nothing collects the LOH after login) | ~34 MiB | 0 | ~34 MiB | none; facet changes also keep their resident pages (no re-upload) |
| GPU re-allocation at login and facet change (commit churn) | ~525 MiB freed and re-allocated | 0 | churn only | none |
| art page array (GPU commit / VRAM, never resident) | 256 MiB | 128 MiB | 128 MiB | one growth step to 24 layers once a session passes 9 resident pages; during that frame both arrays exist (384 MiB transient) |
| anim page array (GPU) | 128 MiB | 32 MiB | 96 MiB | the large scene (82 pages) takes two steps (64, 128); old + new exist for one frame each |
| instance buffer (GPU) | 64 MiB | 8 MiB | 56 MiB | steps only past 192k used instances (the dynamic range reserves 128k); normal sessions bake about 4.3k |
| CPU instance mirror (managed, partly resident) | 18 MiB | 9 MiB | 9 MiB commit | resized (existing doubling) if the allocator grows past 256k |

- **Totals:** about 32 MiB less live managed heap (palettes, frame table, mirror) plus about 34 MiB of dead LOH avoided at login: up to about 66 MiB of Task Manager "Memory" per client. GPU commit and VRAM: 280 MiB less per client at start (448 → 168 MiB), about 1.1 GB across four clients; sessions that need more grow back towards today's sizes.
- **Growth stalls:** pending (bench-000035, the `GPU world: grown` lines in new.log).
