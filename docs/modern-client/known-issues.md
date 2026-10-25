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
