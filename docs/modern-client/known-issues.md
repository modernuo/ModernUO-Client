# Known Issues

Visual or behavioural defects found in the PoC client that aren't fixed yet. Add new ones here; move them to the poc-log when fixed.

## Gpu world renderer: grid lines between land tiles when zoomed out (2026-10-24) — fixed 2026-10-25, leftover below

- **Fixed** (seams agent, commit 115e93c71; poc-log "Land seams when zoomed out"): baked land, stretched land and 44×44 floor and water statics draw seam-free at zoom 1.5, 2 and 2.5 (`SampleTile` in sprite.hlsl).
- **Left over:** a few short dark ticks where shore statics meet open water at zoom 2. They were there before the fix too. They come from statics that don't fill 44×44 (shore pieces), which still take the generic trilinear alpha-test path.

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
