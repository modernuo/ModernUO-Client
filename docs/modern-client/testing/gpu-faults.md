# GPU faults: repro loop, fault detection and dump decoding

How to reproduce and measure the shared-device GPU page faults (NVIDIA `Error_DMA_PageFault`, a ROP write into Uo.Render's page atlas; see [device-lost-leak.md](../device-lost-leak.md) part B). Written 2026-10-24 by the Tier 0 page-fault agent, revised 2026-10-25. Since 2026-10-25 the client ships an SDL3.dll from our fork ([../sdl-fork.md](../sdl-fork.md)) that stops SDL from defragmenting the page atlases. To reproduce the fault you now need the stock allocator: the diagnostic SDL with `CUO_SDL_VARIANT` unset, or an upstream 3.4.19 DLL. Read [testing-playbook.md](../testing-playbook.md) first.

## What a fault looks like

- **Aftermath:** the Nsight Aftermath monitor (`nv-aftermath-monitor.exe`, which the owner keeps running) writes `C:\CrashDumps\aftermath\cuo-<pid>-<date>_<time>.nv-gpudmp` and **shows a popup on the owner's desktop**.
- **System log:** `nvlddmkm` event 153.
- **Our client:** it usually logs `GPU device lost (...): vkQueueSubmit VK_ERROR_DEVICE_LOST`, saves and exits with code `0xDEAD`. Not always: it can carry on and exit 0.
- **Other clients on the GPU** may also lose their device and exit `0xDEAD`, including other agents' clients.
- **The display does not blank.** The owner has never seen it do so.
- **Cap the faults you cause** (`--max-faults`): each one can kill other agents' clients and pops up on the owner's screen.

## The repro loop (`tools/gpu-fault-repro/repro.py`)

One run = log in with the Gpu world renderer, run a DevBench plan, exit. About 25 s per run with the default plan.

```
set CUO_TEST_PASSWORD=...        (or $env:CUO_TEST_PASSWORD in PowerShell; never on the client's command line)
python -I tools\gpu-fault-repro\repro.py --exe <your bin>\cuo.exe --tag base --out <scratchpad>\repro --runs 60 --max-faults 3 --env CUO_GPU_WATCH=1
python -I tools\gpu-fault-repro\repro.py --summarize <scratchpad>\repro\base.jsonl
```

- **Account:** `--account` (default `kbatman2`, the GPU-fault account). It writes the password **encrypted** into a throwaway `settings.json` the same way `ClassicUO.Utility.Crypter.Encrypt` does (`"1-"` + hex of the ASCII bytes XORed with `Environment.MachineName`), so the password never reaches a command line. `tools/fps-test/run-fps-test.ps1` does the same (`Protect-Password`).
- **Default plan:** `vsync:off;world:legacy;world:gpu;world:legacy;world:gpu;world:legacy;world:gpu` at `CUO_BENCH_SIZE=1920x1080`, with `CUO_FRAMESTATS=1`, `CUO_BENCH_WINDOWS=1`, `CUO_BENCH_EXIT=1`. DevBench applies the size, waits 5 s, then runs the plan; the first `world:legacy` is about 8 s into the run, the same point as the owner's FPS test.
- **Per run** it records, in `<tag>.jsonl` and `<tag>.log`:
  - the exit code, in game, and plan done;
  - **`fault`** (ours): exit `0xDEAD`, a "GPU device lost" line, or a new dump named with **our PID** (`cuo-<pid>-…`);
  - **`foreign`**: an `nvlddmkm`/`Display` System event or a new dump during the run that isn't ours. Other agents' clients fault too;
  - the event times, the new dump files, the binary and `build-info.txt`;
  - `[gpuwatch]` counters (diagnostic builds only) and `SDL diagnostic build` lines.
- **Logs:** the client log is kept for failed runs; `--keep-logs` keeps all. `{run}` in an `--env` value becomes the run number (for example `CUO_BENCH_SHOTS=<dir>\{run}`). `--world legacy` logs in on Legacy.
- **Attribute every fault by PID.**
  - A driver event during your run is not yours unless the dump's PID matches.
  - A foreign reset can also make your client exit `0xDEAD` with someone else's dump. Count that separately and say so.
  - Before blaming a build, check `tasklist` and the dump names.
- **Between runs** it waits 4 s (15 s after a fault) and deletes the temp profile folder, and the bin's `Data/` if it created one.
- **Builds:** publish your own NativeAOT build to the scratchpad with `tools/fps-test/publish-aot.ps1 -OutDir <scratchpad>\pf\<name>`, one folder per variant; keep the folder (its PDBs symbolise dumps). Never use the owner's `C:\Repositories\cuo-fps-test\bin` for experiments.

## Diagnostics in the client (diagnostic branch only)

These switches are **not** on `poc/modern-client`. They live on `poc/gpu-pagefault-diag` (2ad0e20ec plus 5793cd62a). `GpuWatchTemp` reads SDL 3.4.19 internals at fixed offsets, so it only works with that SDL. To use them, build from that branch, or cherry-pick its two commits onto a scratch branch.

| Variable | What it does |
|---|---|
| `CUO_GPU_WATCH=1` | `GpuWatchTemp`: after every present, reads SDL 3.4.19's Vulkan structs at fixed offsets for the art/anim atlases, their tail fillers and the world/light/UI RTs, and logs `[gpuwatch]` when the VulkanTexture or memory behind one changes. `NEWTEX` = a VulkanTexture never seen in that container before. For an atlas this is normally a **defrag move**; for an RT it can also be cycle-pool growth. The seen-set is keyed by container pointer, and SDL reuses container addresses after a recreation, so a "NEWTEX" right after `reinit` can be a new atlas at an old address. The diagnostic SDL's own `SDL diag: defrag move texture …` lines are the authoritative move count. `CYCLE` = cycling back to a known VulkanTexture: FNA3D cycles its RTs on every clearing render pass, so RTs log thousands of these per run, and they are **not** defragmentation. Offsets are only valid for SDL 3.4.19. |
| `CUO_GPU_REINIT=<ms>` | Stress (needs `CUO_GPU_WATCH=1`): in game, every `<ms>` drops the GPU world pass (atlases) and the world RT so both are recreated, as at login. About 2 atlas defrags per 40-s run on the baseline, versus about 0.3 per normal run. **It shows black or half-built frames by design** (the world and its RT are gone; the 2026-10-25 "tile corruption" recording of a sabc3 run was this). It caused no faults in 28 runs, so it is not a useful fault amplifier. |
| `CUO_DIAG_OFF=A,B,C` | Turns off the Tier 0 app mitigations (A: one world-RT usage per session; B: atlas tail filler; C: world-pass creation after present) |
| `CUO_DIAG_SWITCHSHOTS=<dir>`, `CUO_DIAG_SHOTFRAMES=1,2,4,…` | World-RT shots at these frame offsets after every switch to the Gpu renderer, plus a per-frame `[gpuwatch] swN+d` line (instances, chunks, uploads, missing pages). Each shot stalls the GPU for about 200 ms |
| `CUO_SIMULATE_DEVICE_LOST=<s>`, `CUO_DEVICE_LOST_NOEXIT=1` | Device-loss handler tests (no GPU reset). |

## Decoding dumps

```
"C:\Program Files\NVIDIA Corporation\Nsight Graphics 2026.3.1\host\windows-desktop-nomad-x64\nv-aftermath-format.exe" -j -p <the bin folder that ran> <dump> > dump.json
```

- Look at `Page fault info` (access type, client e.g. ROP, the faulting VA and the resource at it: size, layers, mips, create tick) and the `Aftermath markers` call stack (resolves to `FNA3D_SwapBuffers` ← `Game.RunLoop` with the right PDBs).
- `-p` must point at the exact build folder that produced the dump, which is why the loop records the binary per run.
- **Resource names:** SDL names Vulkan objects only on a debug-mode device, which FNA creates only in a Debug build (`GraphicsDevice.cs:562-568`). (`GpuInterop.SetName`, which names FNA's RTs in Debug builds, is on the diagnostic branch only.) **But on a debug-mode device SDL rejects Uo.Render's Indexed8 anim atlas** (`SDL_gpu.c:1284`: "usage cannot contain SAMPLER for textures with an integer format"). The GPU world pass can't be created: before the world-settle merge the client fell back to Legacy, and since then it exits `0xDEAF`. So **a Debug client cannot reproduce this fault** until that atlas stops asking for SAMPLER usage. To identify resources, use Release/AOT builds with the diagnostic SDL's defrag log, or `CUO_GPU_WATCH` on the diagnostic branch.

## Estimating a fault rate

- Faults are rare, so count runs, not hours. With k faults in n runs the rate is about k/n; for 0 faults in n runs the 95% upper bound is about 3/n.
- To claim a change helps, compare against the baseline rate p: the chance of 0 faults in n runs by luck is (1-p)^n. At p = 4% that is 30% for 30 runs, 9% for 60 and 2% for 100.
- Faults have so far happened only in runs where the art atlas was defragmented, so the atlas move count (diagnostic SDL lines, or `[gpuwatch] art atlas: NEWTEX`) is a much denser proxy than the fault count. Report both.
- **Concurrency changes the rate.** The 2.8% baseline (2 of 72) was measured with few other clients on the GPU. On 2026-10-25, another agent's clients on the plain `poc/modern-client` build faulted 3 times in 8 runs, all at login, while other clients ran. Report how many other clients were running.

## Testing a diagnostic SDL3.dll

- **Source:** our fork ([../sdl-fork.md](../sdl-fork.md)). Use branch `diag/defrag` on github.com/modernuo/SDL. It has the same base as production, and one DLL switches between variants with `CUO_SDL_VARIANT`:
  - unset: stock;
  - `dedicated`: the production patch;
  - `refguard`: defrag skips allocations with any referenced resource;
  - `nodefrag`.
  It logs `SDL diagnostic build v2: variant=…` once, and `SDL diag: defrag allocation …` / `SDL diag: defrag move texture WxHxLayers …` for every move. An atlas move is a texture with 16 or more layers.
- **Build:** in your scratchpad, with the same cmake line as `tools/sdl/build-sdl.ps1` (with `-NoCopy`, or point it at your clone).
- **Install:** copy your AOT bin folder and replace only `SDL3.dll`. Never put a diagnostic DLL in `external/x64` or the owner's bin.
- **Run both arms from the same DLL,** and confirm the variant line in every run's log (the loop records it in `sdl`).

## Cleanup

- Close every client you start (the loop does; check `tasklist | findstr cuo` after an interrupted run).
- The loop deletes its temp settings/profile folders; delete `%TEMP%\cuo-repro-*` left by a killed run.
- Leave dumps in `C:\CrashDumps\aftermath` (the owner reviews them) and list them in your report.
