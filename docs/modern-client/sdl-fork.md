# SDL3 fork (github.com/modernuo/SDL)

The client ships an SDL3.dll built from our fork of SDL, not an upstream release binary. Written 2026-10-25 by the Tier 0 page-fault agent; the owner approved the fork the same day.

## Why we fork

- **The fault:** with SDL 3.4.19's Vulkan allocator, Uo.Render's 256 MiB art page atlas shares a 320 MiB memory allocation with FNA's render targets. The render targets' churn fragments that allocation. SDL then defragments it, copying the whole atlas inside FNA's swapchain submit.
- **What it caused:** every NVIDIA `Error_DMA_PageFault` we attributed to our client followed such a copy. That came to about 3% of logins, and a reset can take down every client on the GPU. See [device-lost-leak.md](device-lost-leak.md) part B and [testing/gpu-faults.md](testing/gpu-faults.md).
- **Why not fix it from the app:** SDL has no API to opt a texture out of sub-allocation or defragmentation. The app-side workarounds only reduce the odds:
  - a filler texture in the atlas's tail;
  - creating resources at frame boundaries.
- **Contribution policy:** upstream SDL refuses AI-generated contributions. So this patch lives in our fork only. Don't open issues or PRs upstream, and don't draft them.

## Branches

| Branch | Base | Contents |
|---|---|---|
| `modernuo/3.4.19` | `release-3.4.x` at c17029909, the source of the 3.4.19 DLL we shipped before ("SDL-3.4.19-6389a16"; its `SDL_gpu_vulkan.c` is byte-identical) | The production patches below. `external/SDL` is pinned here |
| `diag/defrag` | the same c17029909 | Diagnostic only: allocator variants behind `CUO_SDL_VARIANT` (`dedicated`, `refguard`, `nodefrag`) and logging of every defrag move (`SDL diag: defrag …`). Never ship it |

The fork's `main` is untouched.

## Patches on `modernuo/3.4.19`

1. **b2a3160aa: Vulkan images of 64 MiB or more get a dedicated memory allocation.**
   - **Change:** `VULKAN_INTERNAL_BindMemoryForImage` passes `dedicated = size >= LARGE_ALLOCATION_INCREMENT` instead of `false`. The allocation is then exactly the image's size, with no free region, so `VULKAN_INTERNAL_MarkAllocationsForDefrag` never selects it.
   - **What it affects:** our art atlas (256 MiB) and anim atlas (64 MiB). Nothing else we create is that large: render targets are 3.75–8 MiB.
   - **Cost:** one extra `vkAllocateMemory` per large image, and no packing of other resources into those images' tails (up to 64 MiB of tail that render targets used to share). Render targets still sub-allocate and still defragment among themselves.
   - **Measured:** see [device-lost-leak.md](device-lost-leak.md) part B, "Results". In short: 0 atlas copies and 0 own faults.

## Using it in the client

- `external/SDL` is a git submodule of the fork, branch `modernuo/3.4.19`.
- The built `external/x64/SDL3.dll` is committed, so normal builds and `tools/fps-test/publish-aot.ps1` need no C toolchain.
- `external/x64/SDL3.version` records:
  - the fork commit;
  - the `SDL_VENDOR_INFO` string;
  - the build date;
  - the DLL's SHA-256.
- **At runtime** the client logs `SDL3 SDL-3.4.19-b2a3160 (modernuo modernuo/3.4.19 b2a3160aa)` at startup, from `SDL_GetRevision()`. `publish-aot.ps1` adds the same vendor string to `build-info.txt`. The old upstream DLL reads `SDL-3.4.19-6389a16` with no vendor part.

## Rebuilding

```
git submodule update --init external/SDL
powershell -ExecutionPolicy Bypass -File tools\sdl\build-sdl.ps1
```

- **Needs:** CMake and the Visual Studio C++ tools (found with vswhere).
- **Build settings:** Release, NMake, MSVC x64, `SDL_LIBC=OFF`. Like the DLL it replaced, it has no CRT dependency (no VCRUNTIME140), so it runs without the VC++ redistributable.
- The build folder is `%TEMP%\cuo-sdl-build`.
- **Output:** the script copies the DLL to `external/x64/SDL3.dll` and rewrites `SDL3.version`. Commit both together with the submodule pointer.
- **Not identical to the old upstream binary:** that one also referenced `libusb-1.0.dll` (HIDAPI over libusb, loaded only if present). Ours is built without libusb support, which only matters for exotic HID controllers.

## Rebasing onto a newer SDL

1. In a clone of the fork, fetch upstream's release branch:
   ```
   git fetch https://github.com/libsdl-org/SDL.git release-3.4.x
   ```
   Fetch by URL, without adding a remote.
2. Create `modernuo/<version>` from the new release commit.
3. Cherry-pick the patches listed above. Check that `VULKAN_INTERNAL_BindMemoryForImage` still calls `BindResourceMemory` with a `dedicated` flag, and that `LARGE_ALLOCATION_INCREMENT` still exists. If upstream gains its own dedicated-allocation rule, drop the patch.
4. Push the branch to the fork, point `external/SDL` at it (`.gitmodules` `branch =`), and run `tools/sdl/build-sdl.ps1`.
5. Verify before committing the DLL:
   - `poc/Uo.Render.App --external-test --driver vulkan` and `--driver direct3d12` pass;
   - the client logs the new `SDL3 …` line in both Gpu and Legacy;
   - a short repro loop with the diagnostic branch rebased too shows no atlas defrag moves ([testing/gpu-faults.md](testing/gpu-faults.md)).
6. Keep `diag/defrag` rebased alongside if you still need it. It is a separate branch and is never merged into the production branch.
