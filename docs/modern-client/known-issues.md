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
