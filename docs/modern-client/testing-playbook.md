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
| `CUO_GPU_WATCH=1` | Logs GPU resource events (creation, cycling, defragmentation moves) |
| `CUO_SIMULATE_DEVICE_LOST=<s>`, `CUO_DEVICE_LOST_NOEXIT=1` | Device-loss handler test switches |

**Plan entries:**
- `name:x y:zoom` (go and measure), `travel`, `walk`, `say:text`;
- `vsync:on|off`, `world:gpu|legacy`, `size:WxH|max`;
- `flip:N[:frames[:walk]]` (renderer toggle stress), `gumps` (opens paperdoll, backpack, journal and status at fixed spots);
- `effect:`, `moving:`, `shot:name`, `season:`, `itemhue:`, `ghost:on|off` (local-only render checks);
- `gumpok` (confirms a server confirmation gump).

## Standard checks

| Check | How | Pass |
|---|---|---|
| Build and unit tests | `dotnet test tests/ClassicUO.UnitTests -c Release` | All pass (191 as of B1–B5) |
| World screenshot parity | `CUO_BENCH_SHOTS` at fixed spots (Britain, bank, zoomed out, night); compare before vs after or Legacy vs Gpu | Pixel diff is explained (filtering), and someone **looked at the images** |
| UI parity | `gumps` plus the `_ui.png` shots | Identical, apart from live journal text |
| Picking | `CUO_BENCH_PICK=1` | Same object selected in both renderers; clicks reach the server |
| Performance | `CUO_FRAMESTATS=1` with a plan; quick JIT runs only. Report Update, world prepare, world draw, Draw and Present separately | No regression beyond the run-to-run noise. Other clients running make timings noisy, so say so |
| Allocation | FrameStats `alloc/frame` and gen0 counts | No new per-frame allocation |
| GPU robustness | Renderer flips (`flip:`), simulated device loss, the repro loop with the Aftermath monitor | No faults; device loss exits with code 0xDEAD |
| Uo.Render | `poc/Uo.Render.App --external-test` on Vulkan (and D3D12) | PASS |

## GPU crash evidence

- The NVIDIA Aftermath monitor writes dumps to `C:\CrashDumps\aftermath`.
- **Decode them without the GUI:** `"C:\Program Files\NVIDIA Corporation\Nsight Graphics 2026.3.1\host\windows-desktop-nomad-x64\nv-aftermath-format.exe" -j -p <build folder with PDBs> <dump>`.
- Debug builds name Uo.Render textures in dumps; Release builds don't. Record which binary each repro run used.
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
