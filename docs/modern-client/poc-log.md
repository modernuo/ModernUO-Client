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
