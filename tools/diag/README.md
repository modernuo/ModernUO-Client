# Collecting a client diagnostics report

Use this branch's Release client with developer tools enabled (the default). No debugger or extra diagnostics switches are needed. `CUO_DIAG=1` enables the FrameStats phases, LoopStats, and extra probes. A build with `-p:CuoDevTools=false` cannot collect diagnostics.

From the repository root, build the JIT variant:

```bat
dotnet build src/ClassicUO.Client/ClassicUO.Client.csproj -c Release -p:CuoDevTools=true
tools\diag\run-diag.bat
```

`dotnet build` produces a JIT app even though the project sets `PublishAot=true`. The launcher defaults to `bin\Release\cuo.exe`; it sets the working directory to the binary folder so the normal settings/pack discovery works.

For a standalone NativeAOT variant, use the runbook's publish command (requires .NET 10 and Visual Studio C++ tools):

```bat
dotnet publish src/ClassicUO.Client/ClassicUO.Client.csproj -c Release -r win-x64 -p:CuoDevTools=true -o bin/diag-aot
tools\diag\run-diag.bat "%CD%\bin\diag-aot"
```

If ILC cannot find the linker, use a Visual Studio x64 Native Tools command prompt and add `-p:IlcUseEnvironmentalTools=true`. The Windows AOT target requires an AVX2/FMA/BMI2 capable CPU. The static MP3 library must be built as described in [the runbook](../../docs/dev/runbook.md); `-p:CuoMp3Optional=true` publishes without music if necessary.

The alternative plugin-host layout in the runbook is `cd scripts` then `bash build-naot.sh`; it publishes a bootstrap executable and a client shared library into `bin/dist`. For this comparison, use the standalone publish above so both variants start `cuo.exe` directly.

1. Set up the normal settings and pack in each binary folder, using identical renderer, FPS/vsync, window size, UI, character and server settings. Keep the client focused. For the reported uncapped case, use FPS 0 and vsync off.
2. Run the JIT variant for about **2 minutes standing still in the same spot**, then **1 minute walking**. Close the client normally to flush its final partial window.
3. Repeat with NativeAOT in the same spot and scenario. The report's `runtime` line confirms JIT versus NativeAOT; disregard the first warmup windows when comparing stationary throughput.
4. If you compare against another client, check that it presents every frame it draws. Some clients draw uncapped but present only at the display rate, which makes their FPS counter count draws, not presented frames. Note **your monitor refresh rate**.
5. Send both `Logs\diag-*.txt` files from the respective binary folders, with the approximate time walking started and your refresh rate. Filenames include local start time and PID, so simultaneous clients have separate files. Review environment/path information before sharing; password/token/secret-named environment values are redacted.

## Reading the report

- `runtime`, `env`, `native` and `display`: runtime/GC, inherited overrides, loaded FNA3D/SDL3 paths and versions, actual driver, pixel sizes, scaling, focus, refresh and settings. Display state is sampled at the first draw in each window. SDL has no public present-mode getter: `effective_inferred` uses the current FNA interval, mailbox hint and runtime present-mode support, following the committed native conversion. Other backends say unavailable.
- `window`: roughly five seconds, including FPS, update/draw/present counts, GC generations, pause delta, main-thread allocated bytes/frame and process-wide allocation (including the writer). A slow writer extends a window instead of blocking the game. `frame` measures present-to-present intervals; `update_interval` measures update-to-update cadence; `present` brackets `base.EndDraw()` and includes swapchain/driver waits. A present count is an EndDraw call, not proof of a displayed image.
- `phase`: existing FrameStats CPU splits, including `WorldPrepare` (FillGameObjectList), `Update`, `Draw`, UI, and the new UI-cache recording/comparison phases. Phases nest; do not sum them all. `counter`: sums over the window, including prep walks, upload counts/times, UI hits and glyph quads. Divide by draws for a per-draw count.
- `render_phase`: GPU-world streaming maintenance, chunk table, frame metadata (includes translucent sort), sort, and map/stage/copy upload encoding. These are CPU times, not GPU execution times. `render` sums ticks for timing metrics and bytes/calls for the explicitly named GPU buffer and streamed page texture uploads. GPU-world upload totals include buffer and texture calls after diagnostics starts; startup uploads before the first draw are excluded. FNA art/gump upload counts/times remain in the existing `counter` lines; their byte sizes are not included in GPU-world upload bytes.
- `drain`: socket bytes/packets, backlog and stop reasons (empty/byte cap/packet cap/time cap/closed). `[loop]` retains existing wait/cadence statistics.
- `ping`: raw Stopwatch timestamps for creation, enqueue, socket write start/end, reply's final-byte read slice, parser dequeue and handler. `frame_clock_ms` is the original raw UI sample; `displayed_ms` is its five-slot average. Subtract timestamps and divide by the header's frequency to get seconds. `max_*` and GC/pause deltas describe overlapping frame work; phase maxima include completed draw/present work while the ping is outstanding and the partial update through dispatch. Work after reply dispatch is excluded. `drain_stop`/backlog describe the drain dispatching this reply. Zero timestamps mean unobserved. Status records missing/replaced pings at reset/shutdown too. Plugin-injected or rewritten replies are marked `handler_without_network_provenance` and leave the network ping outstanding. `foreign_ping_sends_total` counts plugin-thread ping sends excluded from attribution; duplicate same-index main-thread sends mark the sample ambiguous. The protocol reuses five indices, so very late echoes after reuse cannot be uniquely identified.

Percentiles are upper bounds in 1 microsecond bins through 8191 us. The overflow bucket uses the observed maximum (conservative tails). Buffers are bounded; `ping_dropped` exposes overflow. The writer formats and flushes on its own thread. Diagnostics add CPU/memory overhead while enabled; compare like-for-like diagnostics runs and use the distributions to locate costs rather than treating instrumented FPS as a production benchmark.
