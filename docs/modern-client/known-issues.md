# Known Issues

Visual or behavioural defects found in the PoC client that aren't fixed yet. Add new ones here; move them to the poc-log when fixed.

## Gpu world renderer: grid lines between land tiles when zoomed out (2026-10-24)

- **Seen by:** the owner, fully zoomed out. Faint grid or diamond lines along tile edges on river water, some grass and dirt.
- **Gpu-only:** a side-by-side crop of the step 9 zoom-out shots (scratchpad `s9/shots/after1/zoomout_{legacy,gpu}.png`, 1280×720) shows faint diamond seams in the Gpu shot and none in Legacy. Legacy point-samples with no mips.
- **Likely cause:** linear filtering plus mipmaps sample across tile edges:
  - for land art diamonds, into the transparent pixels around the diamond;
  - for stretched or texture-mapped land, into neighbouring sprites on the atlas page.

  Averaged edge texels darken or tint the seams, which is most visible on flat textures such as water, grass and dirt.
- **Fix candidates, to be tested against Legacy shots at zoom-out and full zoom-out:**
  1. Clamp UVs to each sprite's own rect in the shader (half a texel inset per mip level).
  2. Alpha-aware mip generation in the converter (premultiplied or coverage-weighted), so transparent texels don't darken edges.
  3. A gutter or padding around sprites in the pack pages (pack-format change; edge-extend the pixels).
  4. For land specifically, sample at the mip level the zoom needs with clamping, or point-sample land at zoom ≤ 1.
- **Verify:** DevBench `CUO_BENCH_SHOTS` at zoom-out and full zoom-out over river, grass and dirt. Crop and compare Legacy vs Gpu, and look at the images.

## GPU engine resets take down every client on the GPU (2026-10-24)

- **What happens:** a page fault in one process causes an NVIDIA engine reset (nvlddmkm 153). Other clients running on the GPU at the time also get `VK_ERROR_DEVICE_LOST` and exit with 0xDEAD; the W1 agent's clients died at 22:34 and 23:18 from resets caused by repro runs.
- **What to do:** when attributing an Aftermath dump, match the dump's PID to the run. Keep deliberate repro runs to a minimum while other agents or the owner use the GPU.

## FNA "scissor rectangle is invalid" on the login gump right after a GPU reset (2026-10-24)

- **What happens:** one client started right after a reset threw `ArgumentException: The scissor rectangle is invalid` from `ScissorStack.PushScissors` ← `ResizePic` on the login gump. The same exception was also seen at 02:45 (Application log, id 1026), so it may not need a reset.
- **Likely cause:** a scissor rect larger than the render target while the window or back buffer has a transient size (0, or not yet resized).
- **Fix:** clamp scissor rects to the current target bounds in `ScissorStack`, and skip the UI draw when the target is empty.

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
