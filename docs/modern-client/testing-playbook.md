# Testing Playbook (for agents and humans)

How to test the ClassicUO PoC client safely and repeatably. Started 2026-10-24 at the owner's request. **Read this before any task that runs the client.**

Area-specific procedures live in [`testing/`](testing/), one file per area, written by whoever developed them. Add or update your area's file when you build or change a test procedure.

## Ground rules

- **Credentials:**
  - Test accounts on the local ModernUO have password `123`. Their access is listed in "Accounts" below.
  - Never write credentials into repo files.
  - Never put the password on a command line; a watchdog logs full command lines. Write it **encrypted** into a throwaway `settings.json` with the client's `Crypter.Encrypt` (machine-name key), as `tools/fps-test/run-fps-test.ps1` and the device-lost harness do.
  - Mask passwords in every log you write.
- **One login per account at a time.** A second login kicks the first. Shared accounts use a lock file in the session scratchpad (`<account>.lock`, holding the agent name and an ISO time):
  - create it atomically before logging in;
  - if it exists and is under 15 minutes old, wait; if older, take it over;
  - delete it when your client exits;
  - **never log in while `<account>.owner-hold` exists:** the owner is using that account.
- **The owner may be at the machine:**
  - no `CUO_BENCH_TOPMOST` unless asked;
  - keep runs short;
  - close every process you start, even on errors;
  - the owner runs the final 5-minute FPS test (`tools/fps-test`) themselves, with the display on.
- **The server is shared:**
  - ModernUO runs from `C:\Repositories\ModernUO\Distribution`; never stop it while others use it;
  - if it's down, start it with `poc\Uo.Net.Cli\scripts\start-modernuo.ps1`;
  - stop it only with `stop-modernuo.ps1`, never force-kill;
  - clean up any NPCs or items you spawn, confirming the server's confirmation gumps.
- **Memory cap:** set `DOTNET_GCHeapHardLimit=0x200000000` (8 GB) on every dotnet or client process. A watchdog kills anything over 16 GB.
- **Leave no trace:**
  - delete temp profiles and settings;
  - no `Data/` folder in the repo root or any `bin`;
  - keep logs, shots and binaries in the session scratchpad, not the repo.
- **Builds you publish go to your scratch folder,** never `C:\Repositories\cuo-fps-test\bin`. That's the owner's test build, republished only by the main session with `tools/fps-test/publish-aot.ps1`.
- **Denied tool calls:** report them and skip that step; no workarounds.
  - **Guard refusals count as denials (owner, 2026-10-25).** If the worktree guard refuses a command (e.g. nested shell text it can't inspect), don't put the same commands in a script file and run that, and don't route them through another interpreter. Run each step as its own plain inline command; if a step can only run in the refused form, skip it and report it.
  - Scripts you call directly for ordinary work (a Python helper that launches the bench harness, a build helper) are fine, as long as they aren't a way around a refusal.
  - Test-password files exist only for one session: create, use, delete.
- **Stay current with `poc/modern-client`.** Agents work on their own branch; the main session merges into `poc/modern-client` several times a day.
  - **When you start:** branch from the current tip of the local `poc/modern-client` and note the commit in your report.
  - **Before each measurement series and before final verification:** run `git log --oneline HEAD..poc/modern-client` (worktrees share branch refs, so the local branch is always the latest; no fetch needed). If anything new landed, merge it (`git merge poc/modern-client`; never rebase shared work), rebuild every test binary, and re-run base and new from the same tip. Never compare a build on an old base against one on a new base.
  - **When the coordinator says something merged** that touches your files or your measurements, merge it at your next clean point.
  - **Your report states** the `poc/modern-client` commit your final numbers were measured on.
  - **Test binaries** live in your scratch folder, one folder per build, each with `build-info.txt` (commit plus uncommitted state). Rebuild them after every merge; a stale binary is the most common source of "fixed but still broken" reports.

## Accounts (local ModernUO, 2026-10-24)

| Account | Access | Typical user |
|---|---|---|
| kbatman | Owner (the owner's main account) | Shared by agents via lock; the owner sometimes needs it, so honour `owner-hold` |
| kbatman2 | Owner | GPU fault and device-lost work |
| kbatman3 | Owner | ECS work |
| kbatman4 | Staff (verify) | AOT and pack-loading work (shared via lock) |

- **New accounts:** ModernUO auto-creates them on first login. The account limit per IP is 100 (`modernuo.json`).
- **New characters:** create them with `poc/Uo.Net.Cli`, which sends `0xF8`.

## Running the client

- **Build:** `dotnet build src/ClassicUO.Client/ClassicUO.Client.csproj -c Release`. NativeAOT: `dotnet publish` with your own `-o`, or `tools/fps-test/publish-aot.ps1` for the owner's build only.
- **Common arguments:**
  - `-settings <temp settings.json>` and `-profilespath <temp dir>`;
  - `-ip 127.0.0.1 -port 2593 -autologin true -skiploginscreen -saveaccount false`;
  - `-pack C:\Repositories\uo-packs\classic-1.0` (read-only; don't regenerate it);
  - `-worldrenderer auto|gpu|legacy` (this run only; see below), `-fps 0 -vsync false`, `-nomessagebox`.
- **Build flavours:** the default build has the developer tooling (`CUO_DEVTOOLS`: DevBench, runtime renderer switching). `-p:CuoDevTools=false` builds the release flavour without it; DevBench plans and `CUO_FRAMESTATS` do nothing there. See [release-cleanup.md](release-cleanup.md).

## World renderer selection (2026-10-25)

The client picks the world renderer once, at startup (`RendererSelection`/`RendererStartup`). It logs one line, which every run that depends on the renderer should check:

```
[renderer] auto: Gpu (SDL_GPU vulkan, NVIDIA GeForce RTX 3090 Ti)
[renderer] auto: Legacy (<reason>; FNA3D D3D11)
[renderer] legacy: Legacy (forced by settings.json; SDL_GPU vulkan, …)
[renderer] gpu: unavailable (<reason>; …)      -> error, exit 0xDEAF
```

A `Trace` line before it shows the setting, its source, the FNA3D driver and the SDL_GPU/Vulkan probe result.

| To get | Do |
|---|---|
| The machine's choice | `"world_renderer": "auto"` in settings.json, or leave it out (default) |
| Forced Gpu | `"world_renderer": "gpu"`, or `-worldrenderer gpu`. If the Gpu world can't run: error message, exit `0xDEAF` |
| Forced Legacy | `"world_renderer": "legacy"`, or `-worldrenderer legacy`. Runs on the device FNA3D picks by itself (SDL_GPU on this machine), so Legacy-vs-Gpu shots compare the same device |
| Legacy on D3D11 (auto's fallback when SDL_GPU/Vulkan can't run) | `CUO_RENDERER_PROBE_FAIL=device` with auto |
| Legacy on OpenGL (auto's fallback where D3D11 can't run) | `CUO_RENDERER_PROBE_FAIL=device` plus `CUO_RENDERER_LEGACY_DRIVER=OpenGL` |
| Legacy on the SDL_GPU device (the Gpu world pass failed at startup) | `CUO_RENDERER_PROBE_FAIL=world` with auto |
| A specific FNA3D driver | `FNA3D_FORCE_DRIVER=OpenGL\|D3D11\|Vulkan` or `"force_driver"` (1 OpenGL, 2 Vulkan). With OpenGL or D3D11, auto skips the probe and runs Legacy there; forced gpu exits |
| The integrated GPU (Intel UHD 770) | `FNA3D_PREFER_LOW_POWER=1` (SDL_GPU's low-power device preference) |

- `-worldrenderer` overrides settings.json for one run and is never saved. `world_renderer` in settings.json is saved.
- The decision is per session. Device loss and a world-pass failure after startup (a facet change) still exit (`0xDEAD`, `0xDEAF`); nothing switches to Legacy mid-session.
- Runtime switching (`world:`, `flip:`, `CUO_BENCH_PICK`, the `-worldrenderer` chat command) is dev tooling. In a session whose Gpu world isn't available (an auto fallback), switching to `gpu` is refused with a `[renderer] runtime switch to gpu refused` warning, and the run stays Legacy.
- **UO data:** `C:\Ultima Online Classic`, client version `7.0.103.0`.
- **Worktrees:** if `external/FNA` is empty, run `git submodule update --init external/FNA` plus its nested submodules. Never change the gitlink.

## DevBench: scripted runs (src/ClassicUO.Client/DevBench.cs)

Driven by environment variables. Read the header comment in DevBench.cs for the authoritative list.

| Variable | Purpose |
|---|---|
| `CUO_FRAMESTATS=1` | Uncapped FPS, inactive throttling off, per-phase timings every 5 s (FrameStats) |
| `CUO_BENCH_PLAN="entry;entry;…"` | A scripted sequence, measured for `CUO_BENCH_WINDOWS` (default 3) windows of 5 s per entry, then quits |
| `CUO_BENCH_SIZE=WxH` | Client-area size |
| `CUO_BENCH_SHOTS=dir` | Plan entries save the world render target (and UI and lights) as PNGs, for parity |
| `CUO_BENCH_PICK=1` | Hover-and-click picking test in both renderers |
| `CUO_BENCH_UI=1`, `CUO_BENCH_SPOTS`, `CUO_BENCH_GO`, `CUO_BENCH_EXIT` | UI tour, screenshot spots, go-to, walk-and-quit |
| `CUO_GPU_WATCH=1` | **Diagnostic branch `poc/gpu-pagefault-diag` only.** Logs when the SDL texture or memory behind the atlases and RTs changes. The RTs' thousands of `CYCLE` lines are FNA3D cycling on clearing passes, not defragmentation. See [testing/gpu-faults.md](testing/gpu-faults.md) |
| `CUO_SIMULATE_DEVICE_LOST=<s>`, `CUO_DEVICE_LOST_NOEXIT=1` | Device-loss handler test switches |
| `CUO_PREP_CHECK=1` | Verifies the world prepare's shortcuts (kept screen positions, item-picking rectangle) against the skipped work; logs `[prep-check]` every 5 s. Don't measure with it on |
| `CUO_GPU_ITEM_CHECK=1\|every` | Verifies the ground-item cache (W4, `gpu_prepare_item_cache`): every 30th (or every) frame walks all items and compares them with what the cache would serve; logs `[gpu-item-check]` every 5 s. See [testing/world-prepare.md](testing/world-prepare.md). Don't measure with it on |
| `CUO_W5_CHECK=1`, `CUO_MOBILE_CACHE_CHECK=1`, `CUO_MOBILE_INPUT_CHECK=1` | Verify W5's world update shortcuts: the active item list, hits percentage and skipped text layouts against the full per-frame work (`[w5-check]`), every reused mobile selection and sprite list against a fresh one (`[mobile-cache-check]`), and every sample from the pushed ECS inputs against the legacy one (`[mobile-input-check]`). `CUO_MOBILE_INPUT_BENCH=1` times the legacy and pushed samples side by side (`[mobile-input-bench]`). See [testing/world-update.md](testing/world-update.md). Don't measure with them on |
| `CUO_MOBILE_UPDATE_CHECK=1\|stats` | E4 (`world_update_mobile_system`, off by default; enable with `"world_update_mobile_system": true` in settings.json, plan `mobsys:on\|off` or driver `mobsys on\|off`): every frame each mobile's `MobileClock` against the Mobile, and every mobile the system skipped is updated the legacy way and must not change; `stats` only counts and times (`[mobile-update-check]`). Don't measure with `1`. See the poc-log entry "E4" |
| `CUO_UI_CACHE_CHECK=1\|N`, `CUO_UI_CACHE_FORCE=1` | B8: compare the UI target with a replay and a direct redraw every N frames (`[ui-cache-check]`); count every frame as changed (worst-case cost). See [testing/ui-batcher.md](testing/ui-batcher.md). Don't measure with the check on |
| `CUO_RECYCLE_CHECK=1\|reuse`, `CUO_RECYCLE=0` | Map object reuse (evicted chunks' land and statics reused by later loads): `1` never reuses but counts every use of a retired object and sweeps the tile lists; `reuse` checks with reuse on; logs `[recycle-check]` every 5 s (pass: all 0). `CUO_RECYCLE=0` turns reuse off. See [testing/pack-loading.md](testing/pack-loading.md) §5. Don't measure with the check on |
| `CUO_TILE_ORDER_CHECK=1\|every\|N` | T6a: every 30th (every, every Nth) frame, every tile of every loaded chunk must equal its stable sort by `TileOrder.Key` (stored PriorityZ, then land / state-1 multi / other, then insertion stamp); logs `[tile-order] tiles, objects, out of order` every 5 s with the objects per rank, and the first 25 offending tiles as warnings (pass: 0). See the poc-log entry "T6a". Don't measure with it on |
| `CUO_WORLD_HOLD=0`, `CUO_WORLD_SETTLE_LOG=1`, `CUO_BENCH_TPSHOTS=dir\|N` | World-settle switches: observe only (no hold), log each unsettled frame presented, per-frame shots after teleports. See [testing/world-settle.md](testing/world-settle.md) |

**Plan entries:**
- `name:x y:zoom` (go and measure), `travel`, `walk`, `lswalk` (the large-scale scene's lane, [testing/large-scale.md](testing/large-scale.md)), `say:text`;
- `vsync:on|off`, `world:gpu|legacy`, `size:WxH|max`, `gpuskip:on|off`, `itemcache:on|off`, `itemmixed:on|off` (E7);
- `activeitems:on|off`, `mobcache:on|off`, `textskip:on|off`, `pushinputs:on|off`, `w5:on|off` (W5's world update shortcuts, [testing/world-update.md](testing/world-update.md));
- `lsreinit` (`reinit` in the large-scale scene: the GPU world pass dropped every 2 s);
- `flip:N[:frames[:walk]]` (renderer toggle stress), `gumps` (opens paperdoll, backpack, journal and status at fixed spots);
- `healthbars` (bars for the player and the 3 nearest mobiles), `minimap` (opens the minimap gump), `uicache:on|off` (B8), plan mode `ui` (`name:ui[:zoom]`: a loop of scripted UI changes while measuring, [testing/ui-batcher.md](testing/ui-batcher.md));
- `effect:x y z:graphic[:blend]`, `moving:`, `shot:name`, `season:`, `itemhue:`, `ghost:on|off`, `chair:hex|off` (a client-side chair under the player), `water:on|off` (animated water), `outrange:on|off` (NoColorObjectsOutOfRange), `range:N` (the client view range, as the server's 0xC8) (local-only render checks);
- `ecsdump:<hex serial>|player|nearest` (logs `[ecsdump]` lines: that serial's ECS entity, every component, the equipment slots with serials, pushed vs recomputed inputs and animation state, each next to the legacy object's values; [testing/ecs-animation.md](testing/ecs-animation.md));
- `gumpok` (confirms a server confirmation gump).

## Standard checks

| Check | How | Pass |
|---|---|---|
| Build and unit tests | `dotnet test tests/ClassicUO.UnitTests -c Release` | All pass (465 on the B8 branch after the poc/modern-client bbd86eb37 merge, 2026-10-25) |
| World screenshot parity | `CUO_BENCH_SHOTS` at fixed spots (Britain, bank, zoomed out, night); compare before vs after or Legacy vs Gpu | Pixel diff is explained (filtering), and someone **looked at the images** |
| UI parity | `gumps` plus the `_ui.png` shots | Identical, apart from live journal text |
| **Legacy world (required for any change to the batcher, the asset loaders, world prepare or GameScene drawing)** | A pure-Legacy session (`-worldrenderer legacy`) with `CUO_BENCH_SHOTS` at a fixed spot, compared with the same spot on the previous build. Legacy is auto's low-end fallback and our parity yardstick and the batcher still draws the UI in Gpu mode | Same image apart from moving creatures, and someone **looked at it**. The B1 regression (2026-10-25: missing and wrong land/static tiles in every Legacy frame) passed every Gpu-only check for a day |
| Picking | `CUO_BENCH_PICK=1` | Same object selected in both renderers; clicks reach the server |
| Performance | `CUO_FRAMESTATS=1` with a plan; quick JIT runs only. Report Update, world prepare, world draw, Draw and Present separately | No regression beyond the run-to-run noise. Other clients running make timings noisy, so say so |
| Allocation | FrameStats `alloc/frame` and gen0 counts | No new per-frame allocation |
| GPU robustness | Renderer flips (`flip:`), simulated device loss, the repro loop with the Aftermath monitor ([testing/gpu-faults.md](testing/gpu-faults.md)) | No faults of ours, attributed by PID; device loss exits with code 0xDEAD |
| Legacy batcher state | Pure-Legacy run (`-worldrenderer legacy`) with `CUO_BENCH_SHOTS` at the house spot; see [testing/ui-batcher.md](testing/ui-batcher.md) | About 2% near-black pixels in the world shot (25–67% = the B1 stale-index corruption) |
| Uo.Render | `poc/Uo.Render.App --external-test` on Vulkan (and D3D12) | PASS |

## GPU crash evidence

- The NVIDIA Aftermath monitor writes dumps to `C:\CrashDumps\aftermath`.
- **Decode them without the GUI:** `"C:\Program Files\NVIDIA Corporation\Nsight Graphics 2026.3.1\host\windows-desktop-nomad-x64\nv-aftermath-format.exe" -j -p <build folder with PDBs> <dump>`.
- **Debug builds can't be used for the page fault.** SDL's debug-mode device names resources, but it also rejects Uo.Render's Indexed8 anim atlas (SAMPLER on an integer format), so the Gpu world can't start in a Debug build. Use Release/AOT builds, and record which binary each repro run used.
- **SDL3.dll** comes from our fork ([sdl-fork.md](sdl-fork.md)). The client logs `SDL3 SDL-3.4.18-… (modernuo modernuo/3.4.18-patched …)` at startup; check that line when a result depends on the allocator.
- Windows process dumps (LocalDumps) go to `C:\CrashDumps`.
- Driver resets show as System log `nvlddmkm` event 153.
- `dxcap -forcetdr` needs admin; don't use it.

## Area procedures (`testing/`)

Each area file covers:
- what it tests and why;
- the exact plan entries and environment variables used;
- the spots, accounts and scene setup it needs (spawned NPCs, open gumps), and how to clean up;
- what to look at in the results, known noise and false positives;
- how long a run takes.
