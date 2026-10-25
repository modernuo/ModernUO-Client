# FPS test: Legacy vs Gpu world renderer

`run-fps-test.ps1` runs a fixed DevBench plan against the local ModernUO shard with the NativeAOT client and writes a side-by-side Legacy vs Gpu table. It takes about 5 minutes.

## Run it

1. Make sure ModernUO is running on 127.0.0.1:2593 and the test account is not logged in anywhere else.
2. Turn the display on and close or minimise anything heavy.
3. From a normal PowerShell window:

   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\Repositories\classicuo\tools\fps-test\run-fps-test.ps1
   ```

4. Type the account password at the masked prompt (the username defaults to `kbatman2`; use `-Username` to change it).
5. Don't touch the mouse or keyboard until it prints the results. The client window stays on top and closes itself at the end.

The results are written to `C:\Repositories\cuo-fps-test\results-<timestamp>.md` and also printed to the console. If the run fails, a client log with the password masked is saved next to it as `fps-test-failed-<timestamp>.log`. When the GPU driver logged errors during the run (for example nvlddmkm event 153, a driver reset that takes the client's graphics device with it), the failure message and the log list them. If the client itself detected the lost device, it saves, exits with code `0xDEAD` and the script reports "GPU device lost (driver reset)".

## What it does

- Checks that the client, the pack, the UO directory and the server are there. If the server isn't reachable, it says so and stops.
- Creates a throwaway settings and profile folder under `%TEMP%` and deletes it at the end.
- Sets `DOTNET_GCHeapHardLimit=0x200000000`, `CUO_FRAMESTATS=1` (per-phase timings), FPS uncapped, VSync off, reduce-FPS-when-inactive off, and `CUO_BENCH_TOPMOST=1`.
- Runs Legacy then Gpu at each spot, 3 windows of 5 s after 8 s of streaming:
  1. Britain (1495,1629), zoom 1, at 1920x1080
  2. Britain bank (1430,1690), zoom 1, at 1920x1080
  3. Zoomed out (zoom 2.5) at Britain, at 1920x1080
  4. A short walk near Britain (pathfinding back and forth), at 1920x1080
  5. Zoomed out at Britain in a maximised window (3840x2054 on a 4K display at 150%)
- Records FPS, frame avg/p50/p95/p99, Update, world prepare, world draw, Draw total, Present, allocations per frame and GC for each spot. It also records CPU, GPU, driver, display mode, power plan and the build commit.
- Always closes the client, also on error or Ctrl+C.

## Options

| Parameter | Default |
|---|---|
| `-Username` | `kbatman2` |
| `-Bin` | `C:\Repositories\cuo-fps-test\bin` (the published client) |
| `-ResultsDir` | `C:\Repositories\cuo-fps-test` |
| `-Pack` | `C:\Repositories\uo-packs\classic-1.0` |
| `-UoPath` / `-ClientVersion` | `C:\Ultima Online Classic` / `7.0.103.0` |
| `-Server` / `-Port` | `127.0.0.1` / `2593` |
| `-Size` / `-LargeSize` | `1920x1080` / `max` (a maximised window; or e.g. `3840x2160`) |
| `-Windows` | `3` (5-s measurement windows per spot) |

For unattended runs you can set the optional `CUO_FPS_TEST_PASSWORD` environment variable instead of typing the password. The password is only passed to the client process at runtime; it is never written to disk.

## Rebuilding the client

`publish-aot.ps1` publishes the current working tree as a NativeAOT exe (win-x64, `IlcInstructionSet=x86-64-v3`) into `C:\Repositories\cuo-fps-test\bin`. That folder includes the patched `FNA3D.dll` from `external\x64` and a `build-info.txt` with the commit, which the results file quotes. It needs the .NET SDK and the Visual Studio C++ tools.

```powershell
powershell -ExecutionPolicy Bypass -File C:\Repositories\classicuo\tools\fps-test\publish-aot.ps1
```

## Reading the numbers

- **Present above about 1,000 fps is bimodal:** a few percent of frames wait 4–5 ms on the swapchain. Frame avg, p99 and FPS include those waits.
- **Draw total, world prepare and world draw** are pure CPU numbers.
- **The Gpu row's "world draw"** includes `WorldRenderer.Prepare`/`Render` and the FNA overlay (effects, weather, selection).
- If a Gpu run shows **"drew no GPU world instances"** under Warnings, FNA3D was not on the SDL_GPU driver, so the client fell back to Legacy.
