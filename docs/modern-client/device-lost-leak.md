# GPU device loss: the memory runaway and the 2026-10-24 page fault

Two separate problems. Sources are pinned to the versions we ship: SDL 3.4.19 (`SDL3.dll` says `SDL-3.4.19-6389a16`, so line numbers are for `src/gpu/vulkan/SDL_gpu_vulkan.c` at commit 6389a16), FNA3D 26.10 plus our local interop patch (`external/FNA/lib/FNA3D/src/FNA3D_Driver_SDL.c`), and FNA 26.10.

**Neither is filed upstream.** FNA3D does not accept AI-generated contributions, and the owner decides about SDL.

## A. After a device loss, SDL_GPU's Vulkan backend leaks every frame

### Summary (upstream-style, for SDL)

**Title:** Vulkan: after `VK_ERROR_DEVICE_LOST`, every frame allocates new command buffers, uniform buffers and cycled transfer buffers, and none is ever freed.

**Repro:**
1. Render with SDL_GPU on Vulkan, uncapped. Each frame maps a transfer buffer with `cycle = true`, uploads, draws, acquires the swapchain and submits.
2. Cause a TDR (a GPU page fault or hang, or `dxcap -forcetdr` as admin).
3. Keep calling the same API sequence, ignoring return values. FNA3D does exactly this.

**Expected:** Submit and acquire fail, and memory stays flat. Ideally the app can also query that the device is lost.

**Actual:** Private bytes grow by about 16 to 25 MiB per frame. Our case was 50 GB in about 3 s, until a 16 GB watchdog killed the process. SDL logs "Out of device-local memory, allocating textures on host-local memory!" and "Out of BAR memory, allocating uniform buffers on host-local memory". VRAM stays around 1 GB, so the growth is host memory.

### Why (code references)

- **Submit failure leaks the command buffer.** `VULKAN_Submit`: when `vkQueueSubmit` fails it unlocks and returns (10851–10859). The command buffer never reaches `submittedCommandBuffers` and never goes back to its pool, and the references it holds (buffers, textures, uniform buffers, fence) are never dropped.
- **A present failure parks the command buffer forever.** Here it is released into `submittedCommandBuffers` (10901), but cleanup only runs for command buffers whose fence reports `VK_SUCCESS` (10918, 10704). On a lost device `vkGetFenceStatus` returns `VK_ERROR_DEVICE_LOST`, so cleanup never runs.
  - `VULKAN_WaitForFences` bails out before its cleanup loop (10693).
  - `VULKAN_INTERNAL_CleanCommandBuffer` (10556), which returns uniform buffers to the pool and decrements resource refcounts, is therefore never reached again.
- **So every frame allocates fresh resources.**
  - Command buffers: the pool is empty, so `VULKAN_INTERNAL_GetInactiveCommandBufferFromPool` allocates a new one (9611).
  - Uniform buffers: `VULKAN_INTERNAL_AcquireUniformBufferFromPool` finds the pool empty and creates a new 32 KiB buffer (7752; `UNIFORM_BUFFER_SIZE` in SDL_sysgpu.h:35). Once BAR memory runs out, these come from host memory (warning at 2372).
  - Cycled buffers and transfer buffers: `VULKAN_MapTransferBuffer` / `PrepareBufferForWrite` find the active buffer still referenced and cycle (8829–8835, 6056). `VULKAN_INTERNAL_CycleActiveBuffer` then finds no buffer with refcount 0 and creates a new one (5965–6006).
  - Transfer buffers are dedicated allocations (7031), so each cycle is a new `vkAllocateMemory`.
  - Device-local allocations fall back to host-visible memory (2219, 2365).
- **No pacing.** The swapchain acquire waits on an in-flight fence that fails immediately (10179), so the loop runs as fast as the CPU can go.
- **No device-lost state.** SDL only uses `VK_ERROR_DEVICE_LOST` as a string (1262), and SDL_gpu.c has no device-lost API. An app can only see the text in `SDL_GetError()` (`CHECK_VULKAN_ERROR_AND_RETURN`, 1294–1303: "vkQueueSubmit VK_ERROR_DEVICE_LOST").

### FNA3D makes it worse (not a separate bug; it's the app side)

- `SDLGPU_SwapBuffers` ignores a failed `SDL_WaitAndAcquireGPUSwapchainTexture` and just skips the blit (1143).
- `SDLGPU_INTERNAL_FlushCommands` ignores the return values of both `SDL_SubmitGPUCommandBuffer` calls (1013, 1023, 1027), then acquires new command buffers (977).
- `SDLGPU_SetBufferData` maps its 16 MiB upload transfer buffer (`TRANSFER_BUFFER_SIZE`, line 36) with `cycle = true` on the first upload after every flush: `transferCycle = bufferUploadBufferOffset == 0` (3409). After the loss, that is a new 16 MiB host allocation every frame. At about 1,000 frames/s this alone is about 16 GB/s, which matches the observed 50 GB in about 3 s.
- The only place that reports an error is `SDLGPU_INTERNAL_FlushUploadCommandsAndAcquireFence` (1001, used by `GetData` and other stall paths). FNA's `FNALoggerEXT.FNA3DLogError` then throws `InvalidOperationException` out of the native callback (FNA `src/FNALoggerEXT.cs:94–98`).
  - Under JIT, that is the "unhandled InvalidOperationException from FNA" of 2026-07-27.
  - Under NativeAOT, an exception crossing a reverse P/Invoke is a fail-fast (`c0000409`), which matches the 2026-08-11 crash of the stock client.

### Uo.Render's share

`Renderer.RenderFrame` maps its 8 MiB frame transfer buffer with `cycle = true` every frame, so after a loss Gpu mode leaks about 8 MiB per frame on top of FNA3D's 16 MiB. It counts failed acquires and submits (`SubmitFailures`).

### Verdict

- **Owner:** SDL_GPU's Vulkan backend. It never reclaims command buffers after `VK_ERROR_DEVICE_LOST` and has no lost state that stops cycling.
- **Amplifiers:** FNA3D (keeps rendering, never checks return values) and Uo.Render (8 MiB per frame).
- **ClassicUO's part:** it kept drawing. That is now fixed.

### Not reproduced deliberately

The plan was one forced TDR per mode with `CUO_DEVICE_LOST_NOEXIT=1`, sampling private bytes and taking a mid-runaway minidump. It was blocked because `dxcap -forcetdr` needs admin: "DXCap: force TDR failed: Please run as administrator." The finding above rests on the code paths, the owner's original numbers and the two SDL warnings.

The tooling is ready if the owner runs dxcap elevated:
- `CUO_DEVICE_LOST_NOEXIT=1` logs frames and private bytes every 250 ms after the loss.
- The scratch harness takes a VirtualQueryEx region histogram and a minidump when the process is 2 GiB over its pre-TDR size, then terminates it.

### What ClassicUO does now

See `src/ClassicUO.Client/GpuDeviceLost.cs`.
- **Detection:** after every present, `SDL_GetError()` is checked for `DEVICE_LOST` / `DEVICE_REMOVED` / `DEVICE_HUNG` / `DEVICE_RESET`. The raw pointer is scanned without allocating. A failed Uo.Render acquire or submit and the FNA3D log hook (an FNA error with device-lost text) are also detection points.
- **On detection:**
  - log the error;
  - save the profile, macros, info bars and settings, and disconnect, without GPU teardown;
  - show "The graphics driver reset (GPU device lost). ClassicUO will close." The box is skipped with `-nomessagebox`, under DevBench or with any `CUO_BENCH_*` set;
  - `Environment.Exit(0xDEAD)`.
- **Leak bound:** at most one or two frames leak before the exit.
- **Recovering** (recreating the device and reloading every texture) is not implemented. It's a possible future option.

## B. The 2026-10-24 21:36:29 reset: a GPU page fault, not a timeout

### What happened

- **When:** during `run-fps-test.ps1 -Windows 1` on the NativeAOT build (this branch plus the device-lost changes), about 8 s after start, at the plan's first `world:legacy` switch, the GPU faulted.
- **Not dxcap:** dxcap had been refused about 4 minutes earlier and caused no reset.
- **System log:** nvlddmkm event 153 at 21:36:29.328.
- **The client:** logged `GPU device lost (present): vkQueueSubmit VK_ERROR_DEVICE_LOST`, saved and exited with 0xDEAD within the same second. This was the first real-world run of the new handler.
- **The dump:** the Aftermath monitor wrote `C:\CrashDumps\aftermath\cuo-16164-20261024_213629.nv-gpudmp` (69,576 bytes). Decode it with `nv-aftermath-format -j -p C:\Repositories\cuo-fps-test\bin <dump>`.

What the dump shows:
- **Device state:** `Error_DMA_PageFault`, with an engine reset and no adapter reset.
- **Page fault:** a write by the ROP, graphics engine, "Illegal access of virtual address", at GPU VA 0x4354B0000.
- **Resource at that VA:**
  - 2048×2048, 24 layers, 12 mips, 268,566,528 bytes, base VA 0x425A00000, not destroyed, fully resident;
  - the fault is 250.7 MiB into it, in mip 0 of layer 22 or 23;
  - it was created 17 ms before the dump and 7.856 s after Aftermath was enabled.
- **Nsight's hint:** a new resource took the place of a destroyed resource that was still in use.
- **Executing work** (automatic marker call stack, symbolised with the AOT pdbs): `FNA3D_SwapBuffers` ← `GameController.EndDraw` ← `Game.RunLoop`. That is FNA3D's whole-frame render command buffer, not Uo.Render's.

### What the resource is

- **Shape:** it matches Uo.Render's art page atlas (`PageResidency`: B5G5R5A1 2048 px, 24 layers, full mip chain, SAMPLER only; 256.12 MiB as SDL reports it).
- **Not the login atlas:** the atlas Uo.Render creates sits about 1 s after device creation, but this resource was created at the switch. Uo.Render recreates its atlas only on a facet change, which didn't happen.
- **SDL's defragmenter:** the only code that creates an identical texture by itself is `VULKAN_INTERNAL_DefragmentMemory` (10981).
  - It creates a new texture from the container's create info, copies every subresource with `vkCmdCopyImage`, repoints the container and releases the old texture.
  - It runs inside `VULKAN_Submit` for the swapchain command buffer (10820), and FNA3D's SwapBuffers submit is exactly that command buffer.

### Why the atlas becomes a defrag candidate

- **The atlas allocation has a tail:** SDL rounds allocations over 2 MiB up to 64 MiB steps (2084–2087), so the 256.12 MiB atlas gets a 320 MiB allocation with a 64 MiB free tail.
- **RTs land in the tail:**
  - FNA's render targets are 3.75 MiB each (world, UI and light, plus the extra textures FNA3D cycles on clear). That is over the 2 MiB small-allocation threshold, so they are placed in large allocations, including that tail.
  - A temporary watcher read SDL's internal texture structs from C# and logged the light RT at offset 309 MiB of the atlas's allocation.
- **Defrag marks it:** RT churn leaves the allocation with more than one free region. `VULKAN_INTERNAL_MarkAllocationsForDefrag` (1369) marks every such allocation whenever a new allocation is needed and none fits (2077–2082). The following swapchain submits move each marked allocation's contents, a 256 MiB atlas copy included.

### What is proven and what isn't

- **Defrag alone did not fault.** A forced defrag in Gpu mode (12 probe RTs with 6 released to make holes, then a 384 MiB texture to force a new allocation) moved the atlas the same way: new VulkanTexture, new allocation, offset 0. There was no fault, no driver event and no dump (21:54:33, JIT, Aftermath running).
- **The run doesn't repeat on demand:** an identical rerun of the fps-test (21:45:28–21:48:33, AOT) completed without a fault.
- **SDL's lifetime rules look sound:**
  - texture lifetimes are refcounted when commands are recorded (`TRACK_RESOURCE`, 2391), not at submit;
  - destroys wait for refcount 0 (10471);
  - the framebuffer cache drops entries when a texture is destroyed (3112, 3134).
  So no plain path was found that frees memory still referenced by FNA3D's open command buffer.
- **Candidates still open:**
  1. Defrag moving the atlas while other defragged resources in the same allocation (RT cycle textures) are written in the same submission. The copies are recorded after the frame's draws, but a driver-side problem with a 256 MiB, 24-layer `vkCmdCopyImage` on the graphics queue hasn't been ruled out.
  2. FNA3D's lazily begun render pass. `SDLGPU_INTERNAL_FreeTextureHandle` (2568) clears `boundRenderTargets` but not `nextRenderPassColorAttachments` / `nextRenderPassDepthStencilAttachment`. A render target disposed while it is the pending target of a deferred clear would be used after its SDL texture was released and freed. ClassicUO's EnsureSize disposes at the start of Draw, while the pending target is the faux backbuffer, so no path was found.
  3. The world RT recreation at the switch. The watcher saw it at later switches, but not at the first one in that run.

### Next steps

- **Readable dumps need a debug-mode device.**
  - Uo.Render now names its textures (`Renderer.CreateNamedTexture`: "Uo.Render pages …", depth, hues). SDL applies names and debug groups only when the device is in debug mode (4305, 5597, 5949).
  - FNA creates the FNA3D device in debug mode only in DEBUG builds (`GraphicsDevice.cs:562–568`), so a Debug client build is needed for named resources.
  - Still to verify: whether `SDL_PushGPUDebugGroup` labels turn into Aftermath markers on Vulkan.
- **Mitigations to try if the fault returns (none applied):**
  - Keep the atlas's allocation free of other resources so it is never marked for defrag. For example, Uo.Render could fill the tail with one long-lived texture it owns, or size the atlas to an exact multiple of 64 MiB.
  - Or pre-create FNA's render targets at fixed sizes before the atlas exists, to reduce RT churn in large allocations.
