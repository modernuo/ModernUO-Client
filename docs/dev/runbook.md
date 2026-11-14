# Runbook: build, convert, run, test

This runbook covers building the ModernUO client, converting a UO install into an asset pack, running the client against a pack and a local ModernUO server, running the tests, and the switches and checkers used to verify changes. Paths use `<repo>` (the checkout), `<uo-install>` (a UO client folder) and `<pack>` (a converted pack folder).

Related docs: [pack-format.md](pack-format.md), [data-formats.md](data-formats.md), [bodies.md](bodies.md), [layer-rules.md](layer-rules.md).

## Prerequisites

- .NET 10 SDK. Every project targets `net10.0` and uses `LangVersion` preview. There is no `global.json`.
- Git submodules:

  ```
  git submodule update --init --recursive
  ```

  | Path | Upstream | Fork |
  |---|---|---|
  | `external/FNA` | `modernuo/FNA`, branch `modernuo/26.10-patched` | yes |
  | `external/SDL` | `modernuo/SDL`, branch `modernuo/3.4.18-patched` | yes |
  | `external/FileEmbed` | `SirCxyrtyx/FileEmbed` | no |

  FNA carries nested submodules (FNA3D among them; `tools/fna3d/build-fna3d.ps1` builds FNA3D from the `modernuo` fork), so the recursive flag is required. A fresh worktree has empty submodule folders until you run the command.
- Prebuilt native libraries are committed under `external/x64`, `external/lib64` and `external/osx`. The client build copies the set for the host OS next to the executable.
- A NativeAOT publish also needs a C++ toolchain (Visual Studio C++ tools on Windows) and the static `cuo_mp3` library (see below).

## Build the client

The solution `ClassicUO.sln` holds the `src/` projects, `tests/ClassicUO.UnitTests`, and the FNA and FileEmbed projects. It does not hold `poc/*` or `tests/Uo.Converter.Tests`. The client builds `poc/Uo.Render` through a project reference; the other `poc/` tools build from their own csproj.

```
dotnet build <repo>/src/ClassicUO.Client/ClassicUO.Client.csproj -c Release
dotnet build <repo>/src/ClassicUO.Client/ClassicUO.Client.csproj -c Debug
```

Output goes to `<repo>/bin/Release/` and `<repo>/bin/Debug/`. The executable is `cuo`.

### The `CuoDevTools` property

`src/Directory.Build.props` defines `CuoDevTools`. It defaults to `true` in every configuration. When true it adds the `CUO_DEVTOOLS` compile constant.

What `CUO_DEVTOOLS` includes:

- `DevBench` (scripted runs) and the per-phase timings of `FrameStats`.
- Runtime world-renderer switching and the `-ecsdriver` argument.
- The checkers `CUO_LAYER_CACHE_CHECK` and `CUO_FUNNEL_CHECK`.
- `MemLog` (`CUO_MEM_LOG`).

Code guarded by it is tagged `CLEANUP(release)`.

A client without dev tools:

```
dotnet build <repo>/src/ClassicUO.Client/ClassicUO.Client.csproj -c Release -p:CuoDevTools=false
```

In that build the DevBench, those checkers and the environment overrides do nothing. The settings switches still work. Build and test both variants for any change that touches guarded code.

### NativeAOT publish

`ClassicUO.Client.csproj` sets `PublishAot` to true. A standalone executable:

```
dotnet publish <repo>/src/ClassicUO.Client/ClassicUO.Client.csproj -c Release -r win-x64 -o <out>
```

Notes:

- `win-x64` publishes target x86-64-v3 (`IlcInstructionSet`): a CPU with AVX2, FMA and BMI2.
- On Windows, ILC finds the linker through Visual Studio. `tools/fps-test/publish-aot.ps1` wraps the command in `vcvars64` and adds `-p:IlcUseEnvironmentalTools=true`.
- The publish links the static MP3 decoder (`external/x64-static/cuo_mp3.lib`, `external/lib64-static/libcuo_mp3.a`, `external/osx-static/libcuo_mp3.a`). If the library is missing the publish fails with `CUOMP3`. Build it with `src/native/cuo_mp3/build-windows.sh`, `build-linux.sh` or `build-macos.sh`, or pass `-p:CuoMp3Optional=true` to publish without music.
- `-p:CuoMibcPath=<file.mibc>` applies a static PGO profile (opt-in; `tools/pgo/collect_mibc.py` collects one).
- `scripts/build-naot.sh` (run from `scripts/`) is the plugin-host layout. It publishes `ClassicUO.Bootstrap` and then the client as a shared library (`-p:NativeLib=Shared -p:OutputType=Library`) into `bin/dist`.

### Shader toolchain

The Gpu world renderer's shaders are compiled ahead of time and committed: `poc/Uo.Render/Shaders/compiled/` holds DXIL (D3D12) and SPIR-V (Vulkan) for each shader variant, and `Uo.Render` embeds them. A normal build or NativeAOT publish needs no shader compiler and no Windows SDK. The client's Gpu path always runs on Vulkan, so it needs the SPIR-V files. A build without them runs `auto` on the Compatibility renderer and logs why; `gpu` exits with an error.

You need DXC only when you change a shader source (`poc/Uo.Render/Shaders/*.hlsl`) or the variant list (`ShaderVariant` items in `Uo.Render.csproj`). Two compilers are used:

- DXIL: the `dxc.exe` of the Windows SDK 10.0.26100.0 (`C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\dxc.exe`). Override with `-p:Dxc=<path>`.
- SPIR-V: the official DXC release v1.9.2609 from https://github.com/microsoft/DirectXShaderCompiler/releases (the SDK's dxc has no SPIR-V backend). Unpack it anywhere; the default path is `C:\Repositories\tools\dxc\v1.9.2609\bin\x64\dxc.exe`. Override with `-p:DxcSpirv=<path>`.

Regenerate:

```
dotnet build <repo>/poc/Uo.Render/Uo.Render.csproj -c Release -p:CompileShaders=true
```

It rewrites the compiled files and `Shaders/compiled/<name>.hlsl.sha256`, the SHA-256 of each source with CR removed. The build fails if either compiler is missing. Commit the compiled files and hash files with the source change. `ShaderBundleTests` fails when a source no longer matches its stored hash, and when `Uo.Render` does not embed both formats.

## Convert a UO install into a pack

The converter is `poc/Uo.Converter`. Full conversion:

```
dotnet run -c Release --project <repo>/poc/Uo.Converter -- --uo <uo-install> --out <pack>
```

- `--out` is required. There is no default output folder.
- `--uo` is the client folder (the one with `tiledata.mul`, `client.exe` and the art, map and sound files).
- The client version comes from, in order: `--client-version x.y.z.w` (the letter form such as `4.0.11c` also works), `--cuo-settings <settings.json>` (its `clientversion`), then `client.exe` (version resource, else the version format call of pre-resource clients). Pass `--client-version` when the exe has no readable version.
- Supported installs: 4.0.11c and later, MUL or UOP. Below 7.0.9 the converter uses the old tile data and multi layouts. `verdata.mul` is not applied; the converter prints a warning if one is present.
- The run ends with a verify step that reads the pack back and compares it with the loaders in `tools/ClassicUO.Assets.Mul`. The exit code is 0 on success, 2 when verify fails and 1 on argument errors.
- Debug PNGs go to a temp folder, or to `--debug <dir>`.

Other arguments (the full list is the usage line the converter prints on an unknown argument, in `poc/Uo.Converter/Program.cs`):

| Argument | Meaning |
|---|---|
| `--only <a,b,...>` | Convert only these sections into an existing `--out` and keep everything else |
| `--no-maps`, `--no-anims`, `--no-gumps`, `--no-fonts`, `--no-cliloc`, `--no-speech`, `--no-sounds`, `--no-music` | Skip one section in a full run |
| `--body-choices <bodies.json>` | The shard's body set, read when anims are converted (see [bodies.md](bodies.md)) |
| `--bodies 400,0xC8` | Convert only these body ids (debugging) |
| `--verify-only` | Read and verify an existing pack; write nothing |
| `--format b5g5r5a1\|rgba8\|bc7`, `--page-size`, `--anim-page-size`, `--raw-pages`, `--compress-pages` | Experimental page format and compression variants. The defaults are the ship profile in [pack-format.md](pack-format.md) |
| `--threads n`, `--facets 0,1`, `--pages a-b`, `--regions n` | Limit threads or the converted subset (debugging) |
| `--gump-def <file>` | Read this `gump.def` instead of the install's |
| `--gump-report`, `--audio-report <pack>` | Read-only reports; they write nothing |
| `--packs-root <dir>` | Add a protected packs root (see below); may appear anywhere in the arguments |

`--only` section names: `art`, `tex`, `hues`, `tiledata`, `data`, `layers`, `gumps`, `fonts`, `cliloc`, `speech`, `sounds`, `music`, `maps`, `anims`. `--anims-only` is shorthand for `--only anims`. `data` writes multis, lights, tileart and equipconv. `layers` writes `layerrules.bin` from the built-in rules. (The printed usage line omits `layers`; the code accepts it.)

An `--only` run matches the page format of the pack it extends (taken from the first art page) unless `--format` is given, and it does not clear the output folder. A full run deletes only the files listed in the previous output's `manifest.json` and refuses a non-empty folder that is not a converter output.

### bodies.json

`--body-choices <bodies.json>` is the shard admin's choice of animation bodies, made once at import. Keys: `preset` (`t2a`, `uor`, `lbr`, `aos`), `forceOldAnimations`, `remodels`, `additions` (`standins`, `newest`), `sequences` (`none`, `target`) and `extras`. The pack holds exactly the resolved set; the client does no body remapping. Without the argument the default choices apply. The choices are recorded in the manifest, so `--verify-only` and later `--only` runs that do not rebuild anims reuse them. Details: [bodies.md](bodies.md).

### Pre-UOP (legacy) installs

Point `--uo` at the pre-UOP folder (MUL files only, down to 4.0.11c). Give `--client-version` if `client.exe` has no readable version. No other flag is needed.

### Reusing unchanged files

When only some sections change, build the new pack from a hard-linked copy of the old one, then run `--only` for the changed sections:

```
cp -al <old-pack> <new-pack>
dotnet run -c Release --project <repo>/poc/Uo.Converter -- --uo <uo-install> --out <new-pack> --only gumps,layers
```

The converter never writes a pack file in place. It writes a temp file in the same folder and moves it over the target, which replaces the directory entry and leaves the hard-linked source untouched. It also drops stale files that an older pack linked in (`hues.bin`, `tiledata_ext.bin`) so the manifest stays correct. The manifest is rewritten with new hashes.

### Protected pack folders

Packs under a configured packs root are immutable: the converter refuses to write or delete there. The roots are the `UO_PACKS_ROOTS` environment variable (separated by `;`), any `--packs-root <dir>` arguments, and on Windows a built-in default root. Convert into a folder outside every root. `--verify-only` may read a protected pack.

## Run the client against a pack

The client reads its assets from the pack. It finds the pack folder in this order:

1. `-pack <dir>` on the command line, or `"pack"` in `settings.json`.
2. A `pack` folder next to the executable that holds a `manifest.json`.
3. A `pack` folder in the UO directory that holds a `manifest.json`.

It stops with an error if `manifest.json` is missing or the pack format is newer than the client supports.

The client needs no UO directory. It takes the client version from `clientversion` if that is valid (sanitized), else from the pack manifest's `sourceClientVersion`, and sends that text to the server; the resolved version is not written back to `settings.json`. `ultimaonlinedirectory` is optional: when it is set and exists, it is the folder for the remaining loose reads (`Clilocs.txt`, `Sounds/<id>.wav`) and for the `CUO_PACK_GUMPS=0` / `CUO_PACK_TEXT=0` fallbacks; otherwise the executable's folder is. Skills, skill groups, world map images, the string dictionary and the hand-edited text/def files come from the pack (pack-format.md). A pack converted before revision 1.2 has no `skills.bin` and is refused: convert again.

Settings come from `settings.json` next to the executable, or from the file named by `-settings <file>`. Command-line arguments start with `-`, take one value (flags such as `-skiploginscreen` take none) and override the file. Relevant keys:

| `settings.json` key | Command line | Meaning |
|---|---|---|
| `ip`, `port` | `-ip`, `-port` | Server address; defaults `127.0.0.1` and `2593` |
| `clientversion` | `-clientversion` | Protocol version, for example `7.0.103.0` |
| `ultimaonlinedirectory` | `-uopath` | UO install folder |
| `pack` | `-pack` | Pack folder |
| `username`, `password` | `-username`, `-password` | Account; `saveaccount` must be true for the file to keep them |
| `autologin`, `saveaccount` | `-autologin true`, `-saveaccount false` | Log in at startup; do not store the account |
| `fps`, `vsync` | `-fps 0`, `-vsync false` | `0` is uncapped |
| `world_renderer` | `-worldrenderer auto\|gpu\|compatibility` | See below |
| `profilespath` | `-profilespath <dir>` | Profile folder (never saved to the file) |
| | `-skiploginscreen`, `-nomessagebox`, `-packetlog` | Flags |

Example against a local ModernUO server on the default port:

```
<repo>/bin/Release/net10.0/cuo -settings <settings.json> -profilespath <profiles> \
    -ip 127.0.0.1 -port 2593 -autologin true -skiploginscreen -saveaccount false \
    -pack <pack> -username <account>
```

The client stores a password in `settings.json` as an encrypted string (`Crypter.Encrypt`, a machine-specific key). Write the encrypted `<password>` into a throwaway `settings.json` instead of passing it on the command line. `-password <password>` is encrypted on read but is visible in the process list. `-password_enc` takes an already encrypted value. ModernUO creates an account on first login by default.

### Running several instances

Several clients can run on one machine against one install and one pack.

- **The pack** is opened read-only with `FileShare.Read`, so any number of clients share it. Do not regenerate a pack in place while clients run (on Windows the rename over an open page fails); publish a new pack folder and start new clients with `-pack`.
- **Files are written atomically** (temp file, then rename; `AtomicFile`), so no instance ever reads a half-written `settings.json`, `profile.json`, `gumps.xml`, `macros.xml` or world map image. A save waits at most about 200 ms for another instance's read (Windows), then is dropped and logged; the next save retries. A file that failed to load is copied to `<name>.bak` before the next save replaces it.
- **Shared files are last-writer-wins.** Two instances logged in as the same character share one profile folder and the last one to save wins; the same holds for `globalprofile.json`, `lastcharacter.json` and `settings.json`. There is no locking. For independent state give each instance its own `-settings <file>` and `-profilespath <dir>`.
- **Logs, journals, screenshots and crash dumps** carry the process id in their name (`2026-11-10_01-02-03_<pid>_packets.log`), so instances started in the same second do not share a file.
- **Memory** is per process: plan on roughly 0.7-0.9 GB working set per Legacy client and about 0.25 GB more with the Gpu world (see `memory-footprint.md`). `DOTNET_GCHeapHardLimit` applies to each process separately.

### World renderer

`world_renderer` (or `-worldrenderer`, which wins for one run and is not saved) is `auto`, `gpu` or `compatibility` (`legacy` is still accepted). Players set it in Options (Video): Automatic (recommended), GPU or Compatibility; it takes effect on the next start. The choice is made once at startup and logged as a `[renderer]` line. `auto` picks the Gpu renderer when SDL_GPU on Vulkan works and the Compatibility (FNA batcher, code name Legacy) renderer otherwise. `gpu` exits with an error if the Gpu world cannot run. The window title shows `[Compatibility renderer]` in dev builds only.

## Unit tests

```
dotnet test <repo>/tests/ClassicUO.UnitTests -c Release
dotnet test <repo>/tests/Uo.Converter.Tests -c Release
```

Both projects use xUnit and FluentAssertions. `ClassicUO.UnitTests` references the client project. Filter with `--filter <ClassName>`.

Tests that need a pack or an install skip themselves when it is not available:

| Variable | Used by |
|---|---|
| `CUO_TEST_PACK` | Pack-reading tests such as `TileDrawOracleTests` |
| `CUO_TEST_GUMP_PACK`, `CUO_TEST_TEXT_PACK` | `GumpPackTests`, `TextPackTests` (a pack with a gump or text section) |
| `CUO_TEST_GUMP_DEF`, `CUO_TEST_GUMP_DEF_PACK` | Gump alias test (a `gump.def` and a pack converted with it) |
| `CUO_LEGACY_UO`, `CUO_LEGACY_PACK` | Pre-UOP import tests (an install with `map0.mul`; a pack converted from it) |
| `CUO_CLASSIC_UO` | `Uo.Converter.Tests` body tests (an install with `anim.mul` and `Bodyconv.def`) |
| `CUO_REPO_ROOT` | `WriteSiteBudgetTests`, to name the repo root explicitly |

Notable test classes:

| Class | Covers |
|---|---|
| `Ecs/ClassAMemberTests` | Fails when a record-only world-state field gets a setter or a backing field, or a cache field loses its setter |
| `Ecs/WriteSiteBudgetTests` | Fails when direct writes to the world-state fields that keep a setter grow or shrink against `Ecs/WriteSiteAllowlist.txt` |
| `Ecs/WorldWriterTests`, `Ecs/WorldWriterMobileTests*` | The `WorldWriter` operations and their ECS mirror |
| `Ecs/WorldState*Tests` | ECS readers and their checkers (contents, equipment) |
| `Ecs/LayerRules*Tests` | The equipment layering rules |
| `Ecs/MobileClockTests`, `Ecs/MobileFrameCacheTests` | The mobile update clock and the frame cache |
| `Assets/PackOutputTests`, `Assets/PackLoadingTests` | Pack writing (never in place) and loading |
| `RendererSelectionTests` | World renderer selection |
| `Uo.Converter.Tests`: `BodyChoicesTests`, `LegacyImportTests` | `bodies.json` resolution and legacy install import |

### The write-site budget

`WriteSiteBudgetTests` scans `src/ClassicUO.Client` with a regex scanner. It compares the count of direct writes to the class A (ECS-owned) fields that still have a setter (X/Y/Z, `Serial`, the Mobile's notoriety, input and step-sound fields, an item's animation fields and `IsMulti`, the Mobile's step-queue methods), per file and field, with `tests/ClassicUO.UnitTests/Ecs/WriteSiteAllowlist.txt`. The count may only go down. The test fails on a new write, and also when the allowlist is above the real count. Regenerate the allowlist only on purpose, and say why in the PR:

```
CUO_WRITE_BUDGET_REGEN=1 dotnet test <repo>/tests/ClassicUO.UnitTests -c Release --filter WriteSiteBudgetTests
```

`CUO_WRITE_BUDGET_DUMP=<file>` writes every found site as `file:line:field:receiver:kind`.

## Settings switches

These `settings.json` keys select ECS and renderer paths. Defaults are from `src/ClassicUO.Client/Configuration/Settings.cs`. A new ECS path lands with its switch off; the old path stays until the new one is the default and has been verified.

| Key | Default | Meaning |
|---|---|---|
| `world_renderer` | `auto` | World renderer: `auto`, `gpu`, `compatibility` (or `legacy`) |
| `text_skip_expired` | `true` | Skip text layout for mobiles and corpses whose overhead texts all expired |
| `gpu_prepare_skip_baked` | `true` | Gpu world prepare skips baked land and statics |
| `gpu_prepare_item_cache`, `gpu_prepare_item_cache_mixed`, `gpu_prepare_item_cache_effects` | `true` | Cache settled ground-item sprites per chunk (and mixed tiles, and tiles with effects) |
| `ui_render_cache` | `true` | Record UI draws and keep the UI target when unchanged |

## Environment variables

Checkers compare a new path with the old one and log mismatches. Do not measure performance with a checker on. Variables marked "dev" work only with `CuoDevTools=true`.

| Variable | Meaning |
|---|---|
| `CUO_LAYER_CACHE_CHECK=1\|N` | Dev. Compare cached `LayerDecision` entries with a fresh decision on every hit (`N`: every Nth hit); logs `[layers] cache checks` every 5 s |
| `CUO_MOBILE_UPDATE_CHECK=1\|stats` | Dev. Mobile clock and step system statistics (counts and times; `Mobile.Update` and its comparison are gone) |
| `CUO_FUNNEL_CHECK=1` | Dev. Report writes to the class A fields that keep a setter (X/Y/Z and the other caches) and the Mobile's record helpers made outside the `WorldWriter` funnel |
| `CUO_PREP_CHECK=1`, `CUO_GPU_ITEM_CHECK=1\|every`, `CUO_GPU_SKIP_CHECK` | Check the Gpu world prepare shortcuts against the full work |
| `CUO_W5_CHECK=1`, `CUO_MOBILE_CACHE_CHECK=1`, `CUO_MOBILE_INPUT_CHECK=1` | Check the active item list, the mobile frame cache and the pushed inputs |
| `CUO_UI_CACHE_CHECK=1\|N` | Compare the UI target with a replay every N frames |
| `CUO_TILE_ORDER_CHECK=1\|every\|N`, `CUO_RECYCLE_CHECK=1\|reuse` | Check the tile lists (order, links, no item, mobile or effect in a list), the dynamic tile index (entries valid and sorted, the merged walk in key order, every placed object reachable) and map object reuse |
| `CUO_PACK_TEXT=0`, `CUO_PACK_GUMPS=0` | Read fonts, cliloc, speech or gumps from MUL/UOP files instead of the pack (for comparison) |
| `CUO_ANIM_SYNC=1`, `CUO_ART_SYNC=1`, `CUO_CHUNK_SYNC=1` | Decode animations, art or map chunks synchronously |
| `CUO_RENDERER_PROBE_FAIL=device\|world`, `CUO_RENDERER_LEGACY_DRIVER=OpenGL` | Force the renderer selection fallbacks for testing |
| `CUO_SIMULATE_DEVICE_LOST=<s>`, `CUO_DEVICE_LOST_NOEXIT=1` | Device-loss handler tests |
| `CUO_MEM_LOG=<seconds>` | Dev. Log the GC heap every N seconds |
| `CUO_NET_TRACE=1` | Track per-frame network parse cost |
| `CUO_FRAMESTATS=1` | Dev. Per-phase frame timings every 5 s (see DevBench) |

DevBench variables are in the next section.

## DevBench

`DevBench` (`src/ClassicUO.Client/DevBench.cs`) is a scripted run driver, compiled only with `CuoDevTools=true`. It is driven by environment variables and runs once the client is in game. The header comment of the file is the authoritative list of variables and plan entries.

| Variable | Purpose |
|---|---|
| `CUO_FRAMESTATS=1` | Uncapped frame rate, per-phase timings (update, world prepare, world draw, draw, present) every 5 s |
| `CUO_BENCH_PLAN="entry;entry;..."` | Measure each entry for `CUO_BENCH_WINDOWS` windows of 5 s (default 3), then quit |
| `CUO_BENCH_SIZE=WxH` | Client area size |
| `CUO_BENCH_SHOTS=<dir>` | Plan entries save the world render target (and UI) as PNGs |
| `CUO_BENCH_PICK=1` | Hover-and-click picking test |
| `CUO_BENCH_UI=1`, `CUO_BENCH_SPOTS="x y;x y"`, `CUO_BENCH_GO="x y"`, `CUO_BENCH_EXIT=1` | UI tour, screenshot spots, go to a spot, walk and quit |

Main plan entries: `name:x y:zoom` (go there and measure), `travel`, `walk`, `say:text`, `vsync:on|off`, `world:gpu|legacy`, `size:WxH`, `shot:name`, `gumps`, `ecsdump:<hex serial>|player|nearest`, and `on|off` entries that switch a setting for the rest of the plan (`gpuskip`, `itemcache`, `textskip`, `uicache`, `w5`).

Typical run:

```
CUO_FRAMESTATS=1 CUO_BENCH_PLAN="vsync:off;world:gpu;spot:1495 1629:1" CUO_BENCH_SIZE=1920x1080 <cuo> <arguments as above>
```

Go-to and teleport entries need an account with staff access on the shard.

## Perf gate expectations

- Compare a base build and a new build made from the same tip: the base is the commit the change started from, and the new build adds only the change. Never compare builds from different bases.
- Run the variants interleaved (base, new, base, new, ...), at least four runs each, on the same machine under similar load. A difference inside the run-to-run spread is noise.
- For timing series use a NativeAOT build, or the JIT build with `DOTNET_TieredCompilation=0`. A tiered JIT run is for quick checks only.
- Compare CPU phases (`update`, `world_prepare`, `world_draw`, `draw`) and call counts, not only FPS. Report allocations per frame.
- Turn every checker off when measuring.
- For a switch change, run both settings states (off and on). When guarded code changed, run both `CuoDevTools` variants.
