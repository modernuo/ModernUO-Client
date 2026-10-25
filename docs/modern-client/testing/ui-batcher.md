# UI and Batcher Testing (allocation, batcher state, UI parity)

Written 2026-10-24 by the B1–B5 agent (branch `poc/batcher-ui-alloc`). It covers what was used to measure and verify B1–B5: per-frame allocation, batcher counters, UI render-target parity and the vertex-generation micro-benchmark. Read [../testing-playbook.md](../testing-playbook.md) first. Its ground rules (account lock, `owner-hold`, memory cap, no password on the command line, cleanup) apply to everything here.

## What it tests and why

- **Per-frame allocation in steady state.** The UI draw path used to allocate about 27.6 KB per frame with four gumps open, mostly closures. After B1–B5 the target is roughly 0 B per frame with no gen0 GCs (measured: 70–250 B, gen0 0).
- **Batcher state work:** basic-effect `Pass.Apply()` calls, flushes and draw calls per frame. After B1: about 3 applies per frame in Gpu mode, down from about 16.
- **UI pixel parity** before and after a UI or batcher change. Any change to `RenderLists`, `GumpCommand`, a control's `AddToRenderLists` or `IGumpDeferredDraw.DrawDeferred` must keep the gump pixels identical.

## Scene: the `gumps` plan entry

The `gumps` entry opens the paperdoll, backpack, journal and status bar, then three seconds later places them at fixed spots: paperdoll (20,150), backpack (300,150), status (520,40), journal (500,260). Put it before the measured spots:

```
CUO_FRAMESTATS=1
CUO_BENCH_EXIT=1
CUO_BENCH_SIZE=1920x1080
CUO_BENCH_WINDOWS=3
CUO_BENCH_PLAN=vsync:off;world:gpu;gumps;britain:1495 1629:1;bank:1430 1690:1
CUO_BENCH_SHOTS=<scratch>\shots-<tag>        (optional: parity shots)
DOTNET_GCHeapHardLimit=0x200000000
```

- Run the client as described in the playbook: a throwaway `settings.json` (encrypted password) and `-profilespath`, `-autologin true -skiploginscreen -saveaccount false -fps 0 -vsync false -worldrenderer gpu -pack C:\Repositories\uo-packs\classic-1.0`.
- A fresh profile has no saved gumps, so the scene is the same on every run. The server's "reward items available" gump (top left, an HtmlControl) also appears, which is useful because it exercises `HtmlControl`.
- **Run time:** about 25 s to log in, then 1 + 3 + 2 s for the `gumps` entry, then 8 s streaming plus 15 s measuring per spot. Britain plus bank takes about 75 s in total.
- **Worktree agents:** the worktree guard refuses `pwsh` scripts, `cd` out of the worktree, `timeout`, and executables or paths it can't resolve.
  - What worked: `export VAR=...` lines, then the client exe by its literal absolute path, with all path arguments in forward-slash form (`C:/Users/.../settings.json`).

## UI parity: shots and how to compare them

- With `CUO_BENCH_SHOTS` set, every measured plan entry (and `shot:name`) saves:
  - `<entry>_<renderer>.png`: the world render target;
  - `_lights.png`: the light render target;
  - `_ui.png`: the UI render target (1280×720 logical at DPI 1.5);
  - all on the first frame of measuring.
- DevBench sets `HideScreenshotStoredInMessage` when shots are on, so the timestamped "screenshot stored in…" line stays out of the UI shot.
- **Compare:** a pixel diff (PIL) that prints the count and bounding box of differing pixels and writes a red overlay, then a side-by-side crop of any region that differs. The scripts used were `diff.py` (a, b, overlay out) and `side.py` (a, b, box, out) in the B1–B5 scratch folder; both are about 20 lines of PIL and easy to recreate.
- **Expected differences** (not regressions):
  - **Journal text:** journal lines show live server messages (NPC names spoken nearby, system messages, timestamps). They differ between runs. The journal's scroll flag appears only when the content scrolls, so it can differ too. The B1–B5 comparison had 18,641 differing pixels, all inside the journal text area (525,313–826,495).
  - **The world shot:** a few dozen pixels on an animated mobile, because of animation phase (89 px in B1–B5).
  - **The cursor:** the cursor is drawn into the UI target. It sits in the same place unless someone moves the mouse during the run.
- **Pass:** apart from the cases above, the paperdoll, backpack, status bar, reward gump, top bar, chat text box and journal frame are identical. Someone must **look at** the overlay and the crops, not just the counts.

## Allocation measurement

### FrameStats (every run)

- The `FRAMESTATS {json}` line per 5-s window carries:
  - `alloc_per_frame`: all-thread `GC.GetTotalAllocatedBytes` delta divided by frames;
  - `gc0`, `gc1`, `gc2`;
  - `gc_pause_ms`;
  - `counts.alloc_bytes`: main thread, per frame.
- The B1–B5 summariser (`summ.py <log>…`) averaged these per tag: fps, frame p50, draw, ui_draw, world_prepare, world_draw, composite, batcher_fna, alloc/f, gen0/s, pause/s, flushes and draw calls.
- **Pass:** no new steady-state allocation. As of B1–B5 that means under about 300 B per frame and 0 gen0 GCs per window, at both Britain and the bank, JIT and NativeAOT.

### EventPipe allocation-tick trace (to find who allocates)

- `dotnet-trace` is installed in `%USERPROFILE%\.dotnet\tools`. Launch the client under it, with the same environment variables as above:

```
dotnet-trace collect -o <scratch>/runs/<tag>.nettrace --providers Microsoft-Windows-DotNETRuntime:0x1:5 --show-child-io true -- <bin>/cuo.exe <client args>
```

- `0x1:5` means the GC keyword at verbose level, which includes `GCAllocationTick`: one sample per roughly 100 KB allocated, with a managed stack. That costs little; FPS stays within noise.
- `--show-child-io true` lets you redirect the client's log to a file as usual.
- **Analyse** with a small TraceEvent console app (package `Microsoft.Diagnostics.Tracing.TraceEvent` 3.1.16). The B1–B5 one is `alloc2/` in the scratch folder.
  - It opens the trace with `TraceLog.CreateFromEventPipeDataFile`.
  - It walks `GCAllocationTickTraceData` events and prints a per-second histogram (KB).
  - For a `fromSec toSec` window, it prints totals per type and per stack: the allocating `System.*` frame, then the first N non-System frames.
  - Usage: `alloc.exe <trace> [fromSec toSec] [depth]`.
- **Pick the window** from the histogram: login is a 170–230 MB spike in seconds 1–2, the measurement windows are flat, and exit (saving) is a spike at the end.
  - In the B1–B5 runs, Britain measured at about 27–40 s and the bank at about 50–67 s of trace time.
  - Allocation-tick samples are coarse. After a fix, a 14-s window should hold only a handful of samples.
- **How to read the stacks:**
  - A `<>c__DisplayClassNN_0` type is a closure's captured-variable class. C# allocates it at **method entry**, even on paths that never create the lambda; that was `Control.DrawDebug`'s 30%.
  - `Func<UltimaBatcher2D,bool>` is the delegate itself.
  - `Enumerator[...]` under a method taking `IEnumerable<T>` means a boxed struct enumerator.
- **Known remaining allocations** (fine to leave):
  - Asset streaming: animation frames decoded when a mobile shows a new direction (`PackPages.DecodeChunk` byte[] and `PackSprites.DecodeIndexed` uint[], from `Animations.LoadDirection`). Event-driven, not per frame.
  - `FrameStats.Report`'s StringBuilder, every 5 s.
  - Saving at exit.

## Batcher counters

- **Flushes and draw calls:** FrameStats `counts.flushes` and `counts.draw_calls`, per frame. These are the FNA batches (UI plus dynamic sprites in Gpu mode).
  - Gpu mode with the `gumps` scene: 16 flushes and 23–24 draw calls. A UI change that adds texture switches or flushes shows up here.
- **FNA time:** the `batcher_fna` phase. It covers time inside the FNA calls in `Flush` and `DrawDirectIndexed`.
- **Effect applies:** `UltimaBatcher2D.EffectApplies` is a running total that is always counted. DevBench's `[bench] <entry> done …` log line ends with `N effect applies/frame` for that plan entry.
  - Gpu mode: 3.0 per frame (world, UI and composite `Begin`s), sometimes 3.3 when the brightlight or radius changes.
  - A jump back towards the flush count means something invalidates `_effectDirty` too often. Check `Begin`, custom-effect draws, and `SetBrightlight` or `SetCircleOfTransparencyRadius` being called with changing values.

## Vertex-generation micro-benchmark

- The scratch console app `vbench/` (net10.0, Release, `AllowUnsafeBlocks`) compares, over 2,048 sprites × 20,000 rounds:
  - the batcher's scalar `SetVertex` (192 bytes per sprite);
  - a `Vector128` version of the same layout, with a bit-exact parity check against the scalar output;
  - a 32-byte instance write (the B6 format);
  - the upload copy for 192 vs 32 bytes.
- Run it with `dotnet run -c Release --project <scratch>/vbench/vbench.csproj` (about 20 s). Read the last of the three warm-up rounds.
- **B1–B5 result:**

| Variant | ns/sprite |
|---|---|
| Scalar 192 B | 8.5 |
| Vector128 192 B (parity 0 diffs) | 7.3 |
| Instance 32 B | 3.5 |
| Copy 192 B | 3.1 |
| Copy 32 B | 0.5 |

- At UI sprite counts (hundreds per frame) every variant costs a few µs per frame. Re-run it before proposing SIMD or B6. Per the owner, any SIMD must use cross-platform `Vector128`/`Vector256`, keep a scalar fallback, and have a parity test.

## Noise and caveats

- **Other clients make timings unreliable.** In the B1–B5 session, two or three other ClassicUO clients and later the owner's launcher were running. Frame times swung ±40% between identical runs, and a NativeAOT run measured 142–498 fps.
  - Allocation counts, GC counts, flushes, draw calls and applies are deterministic and fine to compare.
  - For timings: run before and after back to back, note the other processes (`tasklist | grep -i cuo`), and treat a gain below the run-to-run spread as "no regression".
- **Frame avg and p99 are distorted above about 1,000 fps** by Present back-pressure (see classicuo-perf-baseline.md). Compare `ui_draw`, `batcher_fna`, `draw` and frame p50.
- **Keep a before build:** copy `bin/Release/net10.0` to the scratch folder (e.g. `bin-base`) before changing code, so you can re-run "before" later without checking out old commits.
- **NativeAOT:** publish with your own `-o` in the scratch folder. Outside `publish-aot.ps1`, the ILCompiler link step needs `vswhere.exe` on `PATH`: add `C:\Program Files (x86)\Microsoft Visual Studio\Installer`.

## Cleanup

- Delete the temp settings and profile folder after each run.
- Delete `Data/` from the bin you ran **and from your current directory**. `TakeScreenshot` (called by every plan spot) writes `Data/Client/Screenshots` relative to the executable path, and in a worktree that ended up in the worktree root.
- Delete `<account>.lock` as soon as the client exits, and check that no client you started is still running.
- Keep traces (5–7 MB each), logs, shots and copied bins in the scratch folder only.
