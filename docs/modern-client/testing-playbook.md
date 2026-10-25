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
  - `-worldrenderer gpu|legacy`, `-fps 0 -vsync false`, `-nomessagebox`.
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
| `CUO_WORLD_HOLD=0`, `CUO_WORLD_SETTLE_LOG=1`, `CUO_BENCH_TPSHOTS=dir\|N` | World-settle switches: observe only (no hold), log each unsettled frame presented, per-frame shots after teleports. See [testing/world-settle.md](testing/world-settle.md) |

**Plan entries:**
- `name:x y:zoom` (go and measure), `travel`, `walk`, `lswalk` (the large-scale scene's lane, [testing/large-scale.md](testing/large-scale.md)), `say:text`;
- `vsync:on|off`, `world:gpu|legacy`, `size:WxH|max`, `gpuskip:on|off`, `itemcache:on|off`;
- `lsreinit` (`reinit` in the large-scale scene: the GPU world pass dropped every 2 s);
- `flip:N[:frames[:walk]]` (renderer toggle stress), `gumps` (opens paperdoll, backpack, journal and status at fixed spots);
- `effect:`, `moving:`, `shot:name`, `season:`, `itemhue:`, `ghost:on|off` (local-only render checks);
- `gumpok` (confirms a server confirmation gump).

## Standard checks

| Check | How | Pass |
|---|---|---|
| Build and unit tests | `dotnet test tests/ClassicUO.UnitTests -c Release` | All pass (244 on poc/modern-client a6110f967) |
| World screenshot parity | `CUO_BENCH_SHOTS` at fixed spots (Britain, bank, zoomed out, night); compare before vs after or Legacy vs Gpu | Pixel diff is explained (filtering), and someone **looked at the images** |
| UI parity | `gumps` plus the `_ui.png` shots | Identical, apart from live journal text |
| **Legacy world (required for any change to the batcher, the asset loaders, world prepare or GameScene drawing)** | A pure-Legacy session (`-worldrenderer legacy`) with `CUO_BENCH_SHOTS` at a fixed spot, compared with the same spot on the previous build. Legacy is developer-only now, but it is our parity yardstick and the batcher still draws the UI in Gpu mode | Same image apart from moving creatures, and someone **looked at it**. The B1 regression (2026-10-25: missing and wrong land/static tiles in every Legacy frame) passed every Gpu-only check for a day |
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
