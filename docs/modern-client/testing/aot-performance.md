# Testing: JIT vs NativeAOT performance

How to compare the JIT and NativeAOT builds fairly, how to collect and apply a static-PGO profile, and how to CPU-profile an AOT build without admin rights. Written 2026-10-25 with the AOT-gap investigation (results in [../perf-opportunities.md](../perf-opportunities.md) and the poc-log entry of the same day). Follow [../testing-playbook.md](../testing-playbook.md): encrypted password in a throwaway settings.json, the account lock protocol, no topmost window, cleanup.

## Builds

- **JIT:** `dotnet build src/ClassicUO.Client/ClassicUO.Client.csproj -c Release`, then **copy** `bin/Release/net10.0` to your scratch folder (later builds overwrite it). Delete `win-x64/` and `Data/` from the copy.
- **AOT:** publish to your scratch folder, never `C:\Repositories\cuo-fps-test\bin`:

  ```
  PATH=$PATH:"/c/Program Files (x86)/Microsoft Visual Studio/Installer"   # ILC finds link.exe through vswhere
  DOTNET_GCHeapHardLimit=0x200000000 dotnet publish src/ClassicUO.Client/ClassicUO.Client.csproj -c Release -r win-x64 \
      -p:TrimmerSingleWarn=false -o <scratch>/aot-<variant> [-p:OptimizationPreference=Speed] [-p:CuoMibcPath=<file.mibc>]
  ```

  - About 5 minutes per publish. Don't run benches while ILC is running.
  - ILC's incremental check covers the `.ilc.rsp`, so a changed property recompiles. A changed .mibc *at the same path* does not: delete `src/ClassicUO.Client/obj/Release/net10.0/win-x64/native` first.
  - `tools/fps-test/publish-aot.ps1 -OutDir <scratch>` does the same through vcvars, if you can run PowerShell.
  - **What a publish is:** a standalone win-x64 `cuo.exe` (no shared-library or Bootstrap host, no plugins) plus the natives from `external/x64` (SDL3, FNA3D, FAudio, libtheorafile, zlib). No code changes were needed to publish it (2026-10-24).
  - **Instruction set:** win-x64 publishes set `<IlcInstructionSet>x86-64-v3</IlcInstructionSet>` (AVX2, FMA, BMI1/2, LZCNT, MOVBE), as do `poc/Uo.Client` and `poc/Uo.Render.App`. The exe needs a Haswell (2013) or newer Intel or an Excavator or newer AMD CPU; older CPUs fail at startup. ILC rejects the misspelling `x86-x64-v3`. `-p:IlcInstructionSet=` builds for the default baseline ISA. The first JIT-vs-AOT comparison (2026-10-24, before this setting) had AOT about 10–15% slower on CPU; numbers since then are in [../perf-opportunities.md](../perf-opportunities.md) and the poc-log.

## Bench harness (`tools/aot-bench/`)

| Script | What it does |
|---|---|
| `bench.py --exe X --tag T --out F.jsonl [--plan P] [--sample S.txt]` | One client run: throwaway settings (password from `CUO_TEST_PASSWORD`, written encrypted), `CUO_FRAMESTATS=1`, `CUO_BENCH_SIZE=1920x1080`, Gpu world. Appends each `FRAMESTATS` window as a JSON line. Masks the password in the kept log, deletes the temp folder. User: `--user` or `CUO_TEST_USER`. |
| `rounds.py F.jsonl N name=exe ...` | N interleaved rounds (order reversed every other round), retrying a run that produced no windows. Takes the account lock `CUO_ACCOUNT_LOCK` around each run, honours `<account>.owner-hold`. `ROUNDS_SAMPLE_DIR` turns on the sampler. |
| `stats.py F.jsonl` | Per variant and spot: each run's median over its windows, then the median over runs (`STAT_AGG=mean|median|min`) with the stdev across runs. `STAT_IDX=1` uses the windows' p50 instead of avg. |
| `sampler.py` | The no-admin IP sampler `bench.py --sample` uses (below). |
| `startup.py N name=exe ...` | Time from process start to the first visible window, no login. |

Default plan: `vsync:off;world:gpu;britain:1495 1629:1;bank:1430 1690:1;zoomout:1495 1629:2.5`, 3 windows of 5 s per spot. One run takes about 90 s. 4 variants × 4 rounds takes 25–35 min, plus lock waits.

**`[go` needs staff.** A player account gets no teleport, and `here:0 0:1` entries teleport a staff account to 0,0 (an empty map): check the `[bench] … at x,y` lines in the kept logs before trusting a run.

## Fair comparison

- **Interleave** the variants (rounds.py does), so drifting machine load hits all of them.
- **At least 4 runs per variant.** With other agents' clients and builds running, run-to-run stdev of Draw was 0.05–0.3 ms, and FPS ranged 500–1,300 at the same spot. A difference inside the stdev is noise; say so.
- **Compare CPU phases, not FPS:** `draw`, `ui_draw`, `composite`, `world_prepare`, `world_draw`, `update` and `batcher_fna` (time inside FNA calls). Frame avg and Present follow GPU back-pressure and other clients. Use p50 (`STAT_IDX=1`) when load is uneven; it's less sensitive to preemption.
- **Watch the call counts** (`draw_calls`, `flushes`): they must match between variants, or the scene differs.
- Note the load: other `cuo.exe` processes (`tasklist`), builds, and GPU tools such as the Aftermath monitor (see below).

## Static PGO (opt-in)

1. Collect (about 3 minutes plus the build): `CUO_PGO_PASSWORD=<pw> python tools/pgo/collect_mibc.py --user <staff account> [--out <scratch>/cuo.mibc]`. Take the account lock yourself first.
   - It runs the JIT build through Britain and zoomed out, Gpu then Legacy, with an EventPipe trace (`DOTNET_EnableEventPipe`, `Microsoft-Windows-DotNETRuntime:0x1F000080018:5`, `DOTNET_ReadyToRun=0`).
   - It converts the trace with `dotnet-pgo create-mibc --compressed`. dotnet-pgo is not on nuget.org; the script installs `10.0.13-servicing.26504.104` from the dnceng `dotnet10-transport` feed into `%TEMP%\cuo-pgo-tools`.
   - Trace about 25 MiB, mibc about 260 KiB. Check it with `dotnet-pgo dump -i cuo.mibc -o dump.txt`: expect `EdgeIntCount` and `HandleHistogramTypes` entries for `UltimaBatcher2D`, `Control` and similar.
2. Apply: publish with `-p:CuoMibcPath=<file.mibc>`. The csproj adds it as a `<MibcFile>` item, which ILC gets as `--mibc:`. Check that `--mibc:` appears in `obj/Release/net10.0/win-x64/native/cuo.ilc.rsp`.
3. Measure it against the plain AOT build with rounds.py. Regenerate the profile after large code changes; ILC silently ignores methods that no longer match.

## CPU-profiling an AOT build (no admin)

ETW kernel sampling (wpr/xperf/PerfView) needs elevation; don't trigger UAC while the owner is at the machine. Instead use `rounds.py` with `ROUNDS_SAMPLE_DIR=<dir>`:

- `sampler.py` suspends the busiest thread (the game thread) about 1,000 times a second, reads RIP, and scans the stack for the first return address into `cuo.exe`. That gives a rough caller for native frames.
- After the run it symbolises with dbghelp from `C:\Program Files (x86)\Windows Kits\10\Debuggers\x64` (for symsrv), using `cuo.pdb` next to the exe and Microsoft's symbol server for system DLLs (cache in `symcache/` next to the script).
- The output is exclusive samples per function and per plan entry. AOT symbols read like `cuo_ClassicUO_Game_Scenes_GameScene__AddTileToRenderList`.
- JIT code shows as `?`. For JIT method names use `dotnet-trace collect --profile cpu-sampling`. The sampler is still useful there for the native share (driver, SDL3, FNA3D).
- Driver, SDL3 and FNA3D have no PDBs. Their names are the nearest export (`vkGetInstanceProcAddr`, `SDL_GetNaturalDisplayOrientation`, `MOJOSHADER_version`), so read them as "inside that DLL".
- Overhead: the sampled thread loses about 1–2%; don't mix sampled and unsampled runs in one comparison.

## Size, startup, warnings

- **Size:** `cuo.exe` size per variant (base 18.1 MB on 2026-10-25; PGO +0.7%, Speed +4%).
- **Startup:** `startup.py 5 …` (to the first window). JIT ≈ 2.0 s, AOT ≈ 1.5–1.6 s.
- **Trim/AOT warnings:** grep the publish log: `grep -o "warning IL[0-9]*: [^(]*" pub.log | sort -u`. Expected (2026-10-24 census):
  - FNA IL2046: `MathTypeConverter.GetProperties` overrides `TypeConverter.GetProperties` without the `RequiresUnreferencedCode` annotation. XNA design-time converters, unused at runtime.
  - cuo IL3050 (×2, one site): `Enum.GetValues(typeof(MessageType))` in `Game/UI/Gumps/ResizableJournal.cs`. Works under AOT; the fix is `Enum.GetValues<MessageType>()`.
  - The Friflo set (31): Friflo.Json.Fliox IL3050/IL2026/IL2055/IL2067/IL2070/IL2075/IL3000 and Friflo.Engine.ECS IL2066/IL2071/IL2090, from `MakeGenericType` and reflection. Friflo needs explicit type registration under AOT, and its JSON mapper must stay unused.
  - Nothing from ClassicUO.Assets, ClassicUO.Renderer, ClassicUO.Utility, ClassicUO.IO or MP3Sharp. Warnings are only printed when ILC actually runs, so an incremental publish shows none.

## Cleanup

- Close every client (`bench.py` posts WM_CLOSE, then kills after 15 s).
- If you abort a run, delete `%TEMP%\cuo-aotpgo-*` and `%TEMP%\cuo-pgo-*`, plus your stale lock file.
- Delete `Data/` from your scratch bins and `bin/Release/net10.0` (the client creates it on exit).
- Don't stop a rounds.py run with a task kill: its child client keeps running. Wait for it to finish, or close the client window first.
