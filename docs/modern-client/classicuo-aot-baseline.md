# ClassicUO NativeAOT Baseline (Phase 4, step 2)

Recorded 2026-10-24 on `poc/modern-client`, with FNA 26.10 plus the SDL_GPU interop patch (`external/patches/`).

## Publish

The target is a standalone exe for win-x64. It doesn't use the shared-library or Bootstrap host, and it has no third-party plugins.

```bat
:: run inside a VS x64 developer environment (vcvars64.bat) with vswhere on PATH
dotnet publish src\ClassicUO.Client\ClassicUO.Client.csproj -c Release -r win-x64 ^
  -p:IlcUseEnvironmentalTools=true -p:TrimmerSingleWarn=false -o <out>
```

- **Output:** `cuo.exe` (about 15 MB) plus the natives from `external/x64` (SDL3, FNA3D, FAudio, libtheorafile, zlib). Copy the `Data` folder only if you want screenshots kept next to the exe.
- **No code changes were needed** to publish or run it.
- **Instruction set (step 7b):** win-x64 publishes now set `<IlcInstructionSet>x86-64-v3</IlcInstructionSet>` (AVX2, FMA, BMI1/2, LZCNT, MOVBE). The exe then needs a **Haswell (2013) or newer Intel, or an Excavator or newer AMD, CPU**; older CPUs fail at startup. The same setting is in `poc/Uo.Client` and `poc/Uo.Render.App`. Note the spelling: ILC rejects `x86-x64-v3`. Pass `-p:IlcInstructionSet=` to build for the default baseline ISA. Measured effect: classicuo-perf-baseline.md.

## Trim and AOT warnings (IL2xxx / IL3xxx)

`TrimmerSingleWarn=false` reports every warning separately instead of one per assembly. There are three in total (two unique):

| Assembly | Code | Kind | Where | Notes |
|---|---|---|---|---|
| FNA | IL2046 | Trim: `RequiresUnreferencedCode` mismatch on an override | `Microsoft.Xna.Framework.Design.MathTypeConverter.GetProperties` overrides `TypeConverter.GetProperties` | The XNA design-time TypeConverters (PropertyGrid support) are unused at runtime and harmless. Fix upstream by annotating the override, or ignore. |
| cuo (ClassicUO.Client) | IL3050 (×2, same site) | AOT: `RequiresDynamicCode` | `Game/UI/Gumps/ResizableJournal.cs:511`, `Enum.GetValues(typeof(MessageType))` | Trivial: replace with `Enum.GetValues<MessageType>()`. It works today, because NativeAOT pre-generates arrays for enums that are used. |

Nothing comes from ClassicUO.Assets, ClassicUO.Renderer, ClassicUO.Utility, ClassicUO.IO or MP3Sharp. `System.Text.Json` already goes through the source-generated `SettingsJsonContext`/profile contexts, and the P/Invokes are blittable or `LibraryImport`.

Unrelated build warnings:

- NU1902/NU1903: SixLabors.ImageSharp 3.1.11 has known moderate and high advisories. Bump it.
- About 60 ordinary CS warnings.

### Added by the ECS mirror work (`src/ClassicUO.Ecs`, in progress in the same tree)

When the final publish ran, the working tree already referenced Friflo.Engine.ECS 4.0.0-preview. It added 31 trim and AOT warnings:

| Assembly | Codes | Kind |
|---|---|---|
| Friflo.Json.Fliox (the serializer Friflo pulls in) | IL3050 ×9, IL2026, IL2055, IL2067, IL2070 ×3, IL2075 ×2, IL3000 ×2 | `MakeGenericType`, `Activator.CreateInstance` and reflection on unannotated `Type` values in its type mappers |
| Friflo.Engine.ECS | IL2066 ×3, IL2071 ×8, IL2090 | `MakeGenericType` for component indexes and relations, schema creation, `SimdUtils.GetFieldCountSoA` |

These match the earlier EcsBench finding: Friflo needs explicit type registration under AOT, and its JSON mapper should stay unused. They don't come from ClassicUO itself. The mirror didn't misbehave in the runs below (it ran without `-ecscheck`).

## Run against local ModernUO

- **Server:** the owner's `C:\Repositories\ModernUO\Distribution`, started with `poc\Uo.Net.Cli\scripts\start-modernuo.ps1`.
- **Login:** account `kbatman` with the existing character, using a scratch settings/profile directory outside the repo. The password was given on the command line only.
- **Both builds** reached the world, ran `[go 1495 1629`, walked, and took in-game screenshots (`GetBackBufferData`, so this also exercises SDL_GPU readback). No crashes, and no missing-metadata or `MissingMethodException`s in the AOT log.
- **Driver:** `driver=vulkan shaders=SPIRV` in both builds, and `GpuInterop.GetTexture(WorldRenderTarget)` returned non-null.
- **Screenshots:** `C:\Repositories\uo-packs\debug\cuo\` (`jit-*.png`, `aot-*.png`, `step1-login-sdlgpu.png`).

## Frame time: JIT vs AOT

**How it was measured:**

- An opt-in hook, `src/ClassicUO.Client/DevBench.cs`, enabled by environment variables:
  - `CUO_FRAMESTATS=1` uncaps the frame limiter, both the 250 FPS cap and the inactive-window 5 FPS cap. VSync is already off.
  - `CUO_BENCH_GO="1495 1629"` sends `[go`, sets zoom 1 and waits 8 s.
  - `CUO_BENCH_EXIT=1` walks, takes screenshots and quits.
- After the wait it logs six 5-second windows with these timings:
  - *frame*: the wall time between `Draw` calls, which includes Update and Present;
  - *scene draw cpu*: `GameScene.Draw`, i.e. world prepare (`FillGameObjectList`), draw and lights;
  - *Draw() cpu*: the whole `GameController.Draw` before Present.
- **Setup:** Britain 1495,1629,10, zoom 1, a 1600×1000 game viewport, a 3840×2054 window, Vulkan, RTX 3090 Ti.
- **Builds:** Release JIT (`bin/Release/net10.0/cuo.exe`, tiered PGO on by default) vs the NativeAOT exe built from the same tree.
- **Caveat:** two runs each, on a shared machine with other agents active, so the noisy windows (CPU contention) are excluded.

| | frame p50 | FPS | scene draw cpu avg / p50 / p99 | Draw() cpu avg / p50 | alloc/frame | gen0 GCs per 5 s |
|---|---|---|---|---|---|---|
| **JIT** | 1.32–1.34 ms | 700–720 | 0.34 / 0.32 / 0.57–0.70 ms | 0.53 / 0.50–0.52 ms | 18.2 KB | 4 |
| **AOT** | 1.35–1.47 ms | 680–714 | 0.38 / 0.36–0.37 / 0.58–0.79 ms | 0.60 / 0.57–0.60 ms | 17.1 KB | 3–4 |

**Findings:**

- **AOT is about 10–15% slower on CPU than JIT here.** Startup is faster, but steady-state code is not: JIT has Dynamic PGO and targets the host ISA (AVX2), while ILC's default target is a conservative x64 baseline.
  - Try `<IlcInstructionSet>x86-64-v3</IlcInstructionSet>` (or `native` for local runs) before comparing renderers in step 10. Done in step 7b; see the Publish section.
- **The frame is dominated by everything outside Draw.** Of about 1.35 ms, Update, input and Present take about 0.8 ms. World prepare plus draw is only about 0.35 ms at this spot and size.
  - The legacy renderer is cheap on a quiet street. Step 10 needs busier scenes (the bank, zoomed out, travelling) to show differences.
- **Steady-state allocation is about 17–18 KB per frame** (≈ 12 MB/s at 700 FPS, 4 gen0 GCs per 5 s), on both runtimes. That's a separate backlog item.

## Reproduce

Run the bench in a scratch settings/profile directory. Pass the password at runtime only.

```powershell
$env:CUO_FRAMESTATS='1'; $env:CUO_BENCH_GO='1495 1629'; $env:CUO_BENCH_EXIT='1'
cuo.exe -settings <scratch>\settings.json -profilespath <scratch>\profiles -username kbatman -password <pw> `
        -autologin true -skiploginscreen -saveaccount false
```

The game window size comes from the character profile (`game_window_size` was set to 1600×1000 in the scratch profile). Timings go to stdout as `[bench]` lines.
