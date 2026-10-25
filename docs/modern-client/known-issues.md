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

## Gpu world renderer: raw pop-in on world entry and teleports (2026-10-24)

- **Seen by:** the owner, watching agent test runs. On first entering the world and after moving to a new location, things pop in quickly, land is partly black for a moment, and there are flashes that look like intermediate drawing.
- **Two causes in the code:**
  - **Legacy fallback while Gpu isn't ready.** `GameScene.cs:1065` draws the world with Legacy until `gpu.IsReady(_world.MapIndex)` is true, and also whenever the world RT isn't `PreserveContents`. So entering the world or changing facet shows a few Legacy frames, then switches renderers. Legacy and Gpu filter differently, so the switch is visible.
  - **Unsettled frames are presented.** `WorldRenderer.Settled` (`poc/Uo.Render/WorldRenderer.cs:216`) is false while chunks are pending or visible pages are missing, but only screenshots and tests wait for it. The live client presents those frames: chunks that aren't built yet show as gaps or black, and missing pages draw as empty or black texels.
- **Fix candidates:**
  - on world entry and teleport, hold the last frame (or a loading fade) until `Settled`, with a timeout;
  - don't draw a chunk until its pages are resident, or draw a low-detail placeholder (architecture-recommendations.md, item 5);
  - prefetch the destination area on teleport or map change, before the client moves the camera;
  - once Legacy is removed, drop the Legacy fallback; until then, hold the previous frame instead of drawing Legacy.
- **Verify:** record the screen during a login and a recall or `[go` teleport. Log `MissingVisible`/`PendingChunks` for each frame until `Settled`, and count the presented frames that weren't settled and the frames drawn by Legacy.
- **Related, from the pack-loading merge (df2fbf8c8):** a body seen for the first time decodes on a worker and draws nothing until its first direction is uploaded. A new equipment item on a human who is already visible can appear a frame or two after the body. Nobody has checked this by eye yet. `CUO_ANIM_SYNC=1` restores synchronous loading for comparison.

## Single frames with no baked land and statics, or scattered missing and wrong tiles, in a reinit stress run (2026-10-25)

- **Seen by:** the owner on the KamDos client, which ran the page-fault agent's mitigation build `pf/aot-sabc3` (uncommitted Tier 0 work). Recording: `Desktop\Recording 2026-10-25 000245.mp4`.
- **What happens:** the character stands still in a fully loaded house. In 5 of 234 recorded frames, either nearly all baked land and statics are black (dynamic items, mobiles and the UI still draw), or scattered tiles are missing or show the wrong texture.
- **Not seen** on the Kbatfour client (the pack-loading build): there, black appears only during teleports, which is the pop-in issue above.
- **Update (page-fault agent, 2026-10-25):** that recording was a stress run with `CUO_GPU_REINIT=1500`, which drops and recreates the GPU world pass and world RT every 1.5 s. The 5 bad frames match the 5 recreations, so the cause is very likely the test hook, not mitigations A/B/C. Not proven yet: compare normal runs (no reinit) of the mitigation and base builds for unsettled frames. Even so, a recreation should hold the last frame instead of showing a half-built world (see the pop-in entry above).
