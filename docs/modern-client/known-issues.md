# Known Issues

Visual or behavioural defects found in the PoC client that aren't fixed yet. Add new ones here; move them to the poc-log when fixed.

## Ghost mobiles and items at the update-range edge (known before the PoC; recorded 2026-10-25)

Rarely, a mobile or item the server has stopped tracking stays in the client and stands idle forever ("ghosting"), or a mobile at the edge of the update range disappears although it should stay. It is more likely with large viewports, older RunUO-based servers, and houses and boats (they use a larger update range, so a house can arrive without its door). The cause is either the server's create/remove messages around the range edge, or the client's handling of their order and its own out-of-range cleanup. Not reproduced yet. Fixing it is the first part of range-based viewport streaming (perf-opportunities.md, "Server update range vs. large viewports"): repro, audit ModernUO's edge handling, then clean up the client. Deferred. Related: equipment updates for a mobile the client no longer holds, and gump tooltips for items the client never saw (both in that perf-opportunities section).

## Legacy on FNA3D D3D11 and OpenGL: observations for the deferred Legacy project (2026-10-25)

Auto's fallback (no usable SDL_GPU/Vulkan) runs Legacy on FNA3D's D3D11 driver, or OpenGL. Neither had been run for a while. Tested with `CUO_RENDERER_PROBE_FAIL=device` (plus `CUO_RENDERER_LEGACY_DRIVER=OpenGL`), Release JIT, 1280×720, display off, other agents' clients running. Nothing below blocks a session; none of it was fixed.

- **Nothing broken.** On both drivers: login, world shots (Britain, bank, house), gumps and UI shots, a walk, and `CUO_BENCH_PICK` (static, land, item and player selected; both clicks reached the server). The house spot is 1.83% (D3D11) and 1.84% (OpenGL) near-black, the same as Legacy on SDL_GPU (1.83–1.84%). Shots differ from Legacy on the SDL_GPU device in 0.3–1.7% of pixels: moving NPCs and journal text. No errors or FNA warnings in the logs. Looked at.
- **OpenGL lands on the Intel UHD 770, not the RTX 3090 Ti** (`OpenGL Renderer: Intel(R) UHD Graphics 770`). D3D11 and SDL_GPU both pick the RTX. So the OpenGL numbers are integrated-GPU numbers. A Legacy project that keeps OpenGL needs adapter selection there (or must accept the OS's choice).
- **Speed (Britain, ms per frame, means):**

  | Device | fps | Draw | batcher FNA calls | Present | Frame |
  |---|---|---|---|---|---|
  | Legacy on SDL_GPU (RTX) | 869 | 0.32 | 0.12 | 0.80 | 1.15 |
  | Legacy on D3D11 (RTX) | 407 | 0.37 | 0.04 | 2.01 | 2.46 |
  | Legacy on OpenGL (UHD 770) | 258 | 0.98 | 0.55 | 2.79 | 3.87 |
  | Gpu on SDL_GPU (UHD 770, `FNA3D_PREFER_LOW_POWER=1`) | 262 | 0.36 | | 3.39 | 3.82 |

  - On the same GPU, D3D11 spends 2.5× longer in Present than SDL_GPU. One suspect, not tested: `Main.cs` sets `FNA3D_D3D11_FORCE_BITBLT=1` (a Windows 11 scaling workaround), which uses the BitBlt presentation model. Present times with the display off aren't reliable, so measure with the display on before acting on this.
  - D3D11's CPU side is the cheapest of the three (0.04 ms in FNA calls).
- **FNA3D doesn't fall through a forced driver.** Auto forces `FNA3D_FORCE_DRIVER=D3D11` when a hardware D3D11 device (feature level 10.0+) can be created, the same check FNA3D's D3D11 driver makes. FNA3D also needs MojoShader's D3D11 shader compiler (d3dcompiler). If that's missing, FNA3D fails instead of trying OpenGL, and the client can't start. Very unlikely on Windows 10/11; a fallback-profile project should handle it.
- **Gpu mode on the Intel UHD 770 works** (optional check): `[renderer] auto: Gpu (SDL_GPU vulkan, Intel(R) UHD Graphics 770)`, 0 parity mismatches over 15,795 frames (Britain, bank, walk, house), correct shots, 250–300 fps, bound by Present. So on this iGPU the Gpu path is at least as fast as Legacy.

## Gpu world renderer: grid lines between land tiles when zoomed out (2026-10-24) — fixed 2026-10-25, leftover below

- **Fixed** (seams agent, commit 115e93c71; poc-log "Land seams when zoomed out"): baked land, stretched land and 44×44 floor and water statics draw seam-free at zoom 1.5, 2 and 2.5 (`SampleTile` in sprite.hlsl).
- **Confirmed by the owner (2026-10-25):** no grid marks fully zoomed out in any of the day's runs, including the FPS test at 3840x2054.
- **Left over:** a few short dark ticks where shore statics meet open water at zoom 2. They were there before the fix too. They come from statics that don't fill 44×44 (shore pieces), which still take the generic trilinear alpha-test path.
- **Where to look (for the owner's manual check, end of milestone):** stand at Britain 1495 1629, Gpu mode, zoom 2. Ticks were found in the seams agent's shots (Gpu darker than Legacy over water) along two shorelines: **east, about x 1519–1523, y 1598–1619**, and **west, about x 1456–1458, y 1631–1640** (Trammel/Felucca coordinates; ±1–2 tiles, converted from screen position without the water/player height difference). Legacy shows none there.

## GPU engine resets take down every client on the GPU (2026-10-24)

- **What happens:** a page fault in one process causes an NVIDIA engine reset (nvlddmkm 153). Other clients running on the GPU at the time also get `VK_ERROR_DEVICE_LOST` and exit with 0xDEAD; the W1 agent's clients died at 22:34 and 23:18 from resets caused by repro runs.
- **What to do:** when attributing an Aftermath dump, match the dump's PID to the run. Keep deliberate repro runs to a minimum while other agents or the owner use the GPU.
- **Update 2026-10-25:** the page faults our client caused came from SDL defragmenting Uo.Render's page atlas. Since the SDL fork's dedicated large-image allocations ([sdl-fork.md](sdl-fork.md)), the atlas is never moved. A build without that DLL (an upstream SDL3.dll, or a branch from before the fork) still faults at about 3% of logins, or more with other clients on the GPU.

## FNA "scissor rectangle is invalid" on the login gump right after a GPU reset (2026-10-24) — fixed 2026-10-25, not reproduced

- **Fixed** (seams agent, commit b59735443; poc-log "Scissor exception"): `ScissorStack` clips every pushed rect to the bound render target or back buffer. FNA only rejects negative x, y, width or height, and only the root rect of a clip stack was unclipped, so the cause was a control partly off the top or left edge. `GameController.Draw` also skips the UI when there's no UI target or the back buffer is 0 wide or high. 11 unit tests.
- **Not reproduced:** a tiny saved `window_size`, and minimise, restore and 120×90 `SetWindowPos` cycles on the login screen, didn't throw with the old build. If it shows up again, look for another caller that sets FNA's `ScissorRectangle` directly.

## Gpu world renderer: raw pop-in on world entry and teleports (2026-10-24) — fixed 2026-10-25, leftovers below

- **Fixed (world-settle agent; poc-log "World settle"):**
  - The Legacy fallback is gone.
  - After a login, a teleport (more than 8 tiles in one frame), a facet change, a world-pass recreation or a switch back from Legacy, the last world frame is held while the GPU world streams, until its view is ready (`WorldRenderer.VisibleReady`) or 1.5 s pass. With no frame to hold, the world area is black and then fades in.
  - Measured: 0 unsettled frames presented with hold on, against 6–19 per teleport or recreation before. No Legacy frames.
  - Test procedure: [testing/world-settle.md](testing/world-settle.md).
- **Left over:**
  - **Walking:** a dynamic sprite whose art page isn't resident yet is still presented for about 10 ms (one page, 5–20 frames per 10 s walk at ~1000 fps). Walking deliberately never holds; fix it on the streaming side by prefetching item and mobile art pages.
  - **What a held frame shows:** the old place, while the UI, overhead text, health bars and the mouse picking already follow the new position. That lasts 3–5 frames at 60 fps after a teleport.
  - **Not detected by the hold:** stale override state. W1's suspected `gpu_prepare_skip_baked` flicker after a recreation would draw wrong tiles while `VisibleReady` is true. The hold only covers missing chunks and pages.
  - **No live facet-change test:** only unit-tested, and `reinit` exercises the same recreation path.
  - **Prefetch on the teleport packet:** not done. The packet is applied in the same frame that draws, so there is no gap to use.
- **Related, from the pack-loading merge (df2fbf8c8):** a body seen for the first time decodes on a worker and draws nothing until its first direction is uploaded. A new equipment item on a human who is already visible can appear a frame or two after the body. Nobody has checked this by eye yet. `CUO_ANIM_SYNC=1` restores synchronous loading for comparison.

## Single frames with no baked land and statics, or scattered missing and wrong tiles, in a reinit stress run (2026-10-25)

- **Seen by:** the owner on the KamDos client, which ran the page-fault agent's mitigation build `pf/aot-sabc3` (uncommitted Tier 0 work). Recording: `Desktop\Recording 2026-10-25 000245.mp4`.
- **What happens:** the character stands still in a fully loaded house. In 5 of 234 recorded frames, either nearly all baked land and statics are black (dynamic items, mobiles and the UI still draw), or scattered tiles are missing or show the wrong texture.
- **Not seen** on the Kbatfour client (the pack-loading build): there, black appears only during teleports, which is the pop-in issue above.
- **Update (page-fault agent, 2026-10-25):** that recording was a stress run with `CUO_GPU_REINIT=1500`, which drops and recreates the GPU world pass and world RT every 1.5 s. The 5 bad frames match the 5 recreations, so the cause is very likely the test hook, not mitigations A/B/C. Not proven yet: compare normal runs (no reinit) of the mitigation and base builds for unsettled frames. Even so, a recreation should hold the last frame instead of showing a half-built world (see the pop-in entry above).
- **Update (world-settle agent, 2026-10-25):** recreations now hold the last frame (DevBench `reinit`: 0 unsettled frames presented, against 13–19 before). `CUO_GPU_REINIT` also drops the world RT, so there's nothing to hold: expect black and a 250 ms fade-in per recreation (like DevBench `reinitrt`), not a half-built world.
- **Resolved (page-fault agent, 2026-10-25):**
  - The scattered missing and wrong tiles in the later normal (no reinit) runs were **Legacy frames**, broken by B1. See the FNA3D index-buffer entry below, worked around on `poc/modern-client` and since fixed in our FNA3D fork.
  - World-RT shots of the Gpu pass were clean: 150 or more shots from 1 to 1024 frames after each switch, with A/B/C on and off. So were the Legacy frames once the B1 fix was in.
  - The black frames in the reinit run are the stress hook, by design.

## FNA3D: a disposed render target can stay the pending render-pass target (2026-10-25) — fixed 2026-10-25 in our FNA3D fork

- **What:** FNA3D's `SDLGPU_INTERNAL_FreeTextureHandle` (`FNA3D_Driver_SDL.c` ~2568) clears `boundRenderTargets`, but not `nextRenderPassColorAttachments` or `nextRenderPassDepthStencilAttachment`. `SDLGPU_INTERNAL_BeginRenderPass` (~823) later dereferences them.
- **Risk:** a render target disposed while it is the pending target of a deferred render pass (for example a clear that hasn't started its pass yet) would be used after its SDL texture was released. That is a use-after-free that could fault on the GPU without any defragmentation.
- **Status:** no ClassicUO path that does this was found. `RenderTargets.EnsureSize` disposes at the start of Draw, while the pending target is the backbuffer. The page faults are explained by atlas defragmentation (device-lost-leak.md part B). Lower likelihood; noted by an independent audit.
- **Fixed (2026-10-25)** in github.com/modernuo/FNA3D `modernuo/26.10-patched` (19e5188): a freed pending color target sends the pass back to the backbuffer, a freed pending depth target is dropped. See [fna-fork.md](fna-fork.md). Not reproducible in the client, so the evidence is the code change plus the usual regression checks.

## FNA3D: a cycled index buffer isn't rebound (2026-10-25) — fixed 2026-10-25 in our FNA3D fork

- **What:** `SetIndexBufferData` with Discard cycles the SDL buffer, but `SDLGPU_INTERNAL_BindDeferredState` skips `SDL_BindGPUIndexBuffer` when the handle is unchanged, so the open render pass keeps the old buffer and later draws read old indices. This was the cause of the B1 Legacy corruption (missing and wrong land and static tiles).
- **Was worked around** in the batcher (b62406b8e: `Batcher2D.IndexDataChanged` forced an effect re-apply after each mesh-layer index upload).
- **Fixed** in github.com/modernuo/FNA3D `modernuo/26.10-patched` (e3bd4f2): a cycling upload of the bound index buffer clears the binding, so the next draw rebinds it. The batcher workaround is removed. See [fna-fork.md](fna-fork.md).
