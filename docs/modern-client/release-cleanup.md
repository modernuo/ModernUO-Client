# Release cleanup: developer tooling behind `CUO_DEVTOOLS`

Started 2026-10-25 (renderer-selection agent), from the owner's decision that runtime renderer switching stays for testing but must be removable for release builds.

## The constant

- `CUO_DEVTOOLS` is a C# compile constant, defined in `src/Directory.Build.props` from the MSBuild property `CuoDevTools`.
- It's **on by default for every configuration** on the PoC branch (Debug and Release).
- A release build turns it off with one property:

  ```
  dotnet build src/ClassicUO.Client/ClassicUO.Client.csproj -c Release -p:CuoDevTools=false
  dotnet publish ... -p:CuoDevTools=false
  ```

- Every guarded block is tagged `// CLEANUP(release):`. To list them, search for that tag: `git grep -n "CLEANUP(release)"`.
- Both builds must compile. Check `-p:CuoDevTools=false` whenever you add or move guarded code.
- `tools/fps-test` (the owner's FPS test) needs runtime switching and DevBench, so `publish-aot.ps1` publishes a dev-tools build (the default). Don't pass `-p:CuoDevTools=false` there.

## What it guards

| Feature | Where | Without `CUO_DEVTOOLS` |
|---|---|---|
| The `-worldrenderer [gpu\|legacy]` in-game command (mid-session switch) | `Game/Managers/CommandManager.cs` | Command not registered |
| `RendererStartup.TrySwitch`, the one entry point for runtime renderer switches (refuses `gpu` in a session without a usable Gpu world) | `RendererStartup.cs` | Not compiled |
| **All of DevBench**: `CUO_BENCH_*` scripted runs, plan entries (`world:`, `flip:`, `gpuskip:`, `reinit`, …), `CUO_BENCH_PICK` (switches Legacy → Gpu), `CUO_BENCH_UI`/`SPOTS`/`GO`/`EXIT`, shots, `CUO_FRAMESTATS` | `DevBench.cs` | A stub `DevBench` (`Enabled` and `FrameStats` false, empty `Update`) is compiled instead, so `FrameStats` and every bench hook in GameController, GpuWorldRenderer and GpuDeviceLost stay off |

**Why all of DevBench:** it was a clean cut. Outside DevBench.cs only 11 references exist, all reading `DevBench.Enabled`, `DevBench.FrameStats` or calling `DevBench.Update`, so a three-member stub covers them. Its renderer switches (`world:`, `flip:`, the pick test) can't be separated from the scripts that use them anyway.

## Not guarded (yet)

These are dev-only too, but they're inert unless an environment variable or a plan turns them on, and they're interleaved with product code. Candidates for the release pass:

- `FrameStats` (the phase timers stay compiled, but off: `On` is false without DevBench).
- The parity and checker hooks: `CUO_ECS_PARITY`, `CUO_GPU_ITEM_CHECK`, `CUO_PREP_CHECK`, `CUO_W5_CHECK`, `CUO_MOBILE_CACHE_CHECK`, `-ecscheck`, `-ecsdriver`.
- The test switches: `CUO_SIMULATE_DEVICE_LOST`, `CUO_DEVICE_LOST_NOEXIT`, `CUO_RENDERER_PROBE_FAIL`, `CUO_RENDERER_LEGACY_DRIVER`, `CUO_WORLD_HOLD*`, `CUO_WORLD_SETTLE_LOG`.
- `GameController.DebugRecreateGpuWorld` (only DevBench calls it).
- Chat commands that flip renderer options for A/B runs (`-gpuskip`, `-itemcache`, …). They don't switch the renderer, but they aren't player settings either.

## Verified

- 2026-10-25: `dotnet build -c Release -p:CuoDevTools=false` builds. In that build's `cuo.dll`, the strings of the switching code (`[bench] flip done`, `[bench-pick]`, the `-worldrenderer` command's messages, `CUO_BENCH_PLAN`, the `TrySwitch` method) are absent; the dev build has them.
