# Runbook: build, convert, run, test

This runbook covers building the ModernUO client, converting a UO install into an asset pack, running the client against a pack and a local ModernUO server, running the tests, and the switches and checkers used to verify changes. Paths use `<repo>` (the checkout), `<uo-install>` (a UO client folder) and `<pack>` (a converted pack folder).

Related docs: [pack-format.md](pack-format.md), [data-formats.md](data-formats.md), [bodies.md](bodies.md), [layer-rules.md](layer-rules.md).

## Prerequisites

- .NET 11 SDK (`global.json` pins 11.0.100, rolling forward to later feature bands). Every project targets `net11.0` and uses `LangVersion` preview; FNA, SDL3-CS and FileEmbed keep their own targets (net8.0, netstandard2.0).
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

The solution `ClassicUO.sln` holds every project: `src/`, `tests/`, the C# projects in `tools/`, and the FNA, SDL3-CS and FileEmbed projects from `external/`.

```
dotnet build <repo>/src/ClassicUO.Client/ClassicUO.Client.csproj -c Release
dotnet build <repo>/src/ClassicUO.Client/ClassicUO.Client.csproj -c Debug
```

Output goes to `<repo>/bin/Release/` and `<repo>/bin/Debug/`. The executable is `cuo`.

### The `CuoDevTools` property

`build/DevTools.props` defines `CuoDevTools`, once for `src/`, `tools/`, `research/` and `tests/` (each folder's `Directory.Build.props` imports it). It defaults to `true` in every configuration. When true it adds the `CUO_DEVTOOLS` compile constant.

What `CUO_DEVTOOLS` includes:

- `DevBench` (scripted runs) and the per-phase timings of `FrameStats`.
- Runtime world-renderer switching and the `-ecsdriver` argument.
- The checkers `CUO_LAYER_CACHE_CHECK` and `CUO_FUNNEL_CHECK`.
- `MemLog` (`CUO_MEM_LOG`).

Code that exists only for developers is guarded by `#if CUO_DEVTOOLS`; the guard is its only marker.

A client without dev tools:

```
dotnet build <repo>/src/ClassicUO.Client/ClassicUO.Client.csproj -c Release -p:CuoDevTools=false
```

In that build the DevBench, those checkers and the environment overrides do nothing. The settings switches still work. Build and test both variants for any change that touches guarded code.

The variant without dev tools builds into its own folders: `bin/Release-nodev` (or `bin/Debug-nodev`) for the client, `bin/nodev` and `obj/nodev` inside each project. The two variants never share an assembly. At startup the client compares its own setting with that of the `Uo.Render.dll` it loaded and stops with a "Mixed build" error if they differ.

### NativeAOT publish

`ClassicUO.Client.csproj` sets `PublishAot` to true. A standalone executable:

```
dotnet publish <repo>/src/ClassicUO.Client/ClassicUO.Client.csproj -c Release -r win-x64 -o <out>
```

Notes:

- `win-x64` publishes target x86-64-v3 (`IlcInstructionSet`): a CPU with AVX2, FMA and BMI2.
- On Windows, ILC finds the linker through Visual Studio. `tools/fps-test/publish-aot.ps1` (on `poc/modern-client` only) wraps the command in `vcvars64` and adds `-p:IlcUseEnvironmentalTools=true`.
- The publish links the static MP3 decoder (`external/x64-static/cuo_mp3.lib`, `external/lib64-static/libcuo_mp3.a`, `external/osx-static/libcuo_mp3.a`). If the library is missing the publish fails with `CUOMP3`. Build it with `src/native/cuo_mp3/build-windows.sh`, `build-linux.sh` or `build-macos.sh`, or pass `-p:CuoMp3Optional=true` to publish without music.
- `-p:CuoMibcPath=<file.mibc>` applies a static PGO profile (opt-in; `tools/pgo/collect_mibc.py` on `poc/modern-client` collects one).

### Shader toolchain

The Gpu world renderer's shaders are compiled ahead of time and committed: `src/Uo.Render/Shaders/compiled/` holds DXIL (D3D12) and SPIR-V (Vulkan) for each shader variant, and `Uo.Render` embeds them. A normal build or NativeAOT publish needs no shader compiler and no Windows SDK. The client's Gpu path always runs on Vulkan, so it needs the SPIR-V files. A build without them runs `auto` on the Compatibility renderer and logs why; `gpu` exits with an error.

You need DXC only when you change a shader source (`src/Uo.Render/Shaders/*.hlsl`) or the variant list (`ShaderVariant` items in `Uo.Render.csproj`). Two compilers are used:

- DXIL: the `dxc.exe` of the Windows SDK 10.0.26100.0 (`C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\dxc.exe`). Override with `-p:Dxc=<path>`.
- SPIR-V: the official DXC release v1.9.2609 from https://github.com/microsoft/DirectXShaderCompiler/releases (the SDK's dxc has no SPIR-V backend). Unpack it anywhere; the default path is `C:\Repositories\tools\dxc\v1.9.2609\bin\x64\dxc.exe`. Override with `-p:DxcSpirv=<path>`.

Regenerate:

```
dotnet build <repo>/src/Uo.Render/Uo.Render.csproj -c Release -p:CompileShaders=true
```

It rewrites the compiled files and `Shaders/compiled/<name>.hlsl.sha256`, the SHA-256 of each source with CR removed. The build fails if either compiler is missing. Commit the compiled files and hash files with the source change. `ShaderBundleTests` fails when a source no longer matches its stored hash, and when `Uo.Render` does not embed both formats.

The Legacy renderer's `src/ClassicUO.Renderer/shaders/IsometricWorld.fxc` is a separate, FNA effect. Regenerate it only with the repo-local `src/ClassicUO.Renderer/shaders/fxc.exe` (9.29.952.3111); the Windows Kits `fxc` produces different bytes even for the unchanged shader.

## Convert a UO install into a pack

The converter is `tools/Uo.Converter`. Full conversion:

```
dotnet run -c Release --project <repo>/tools/Uo.Converter -- --uo <uo-install> --out <pack>
```

- `--out` is required. There is no default output folder.
- `--uo` is the client folder (the one with `tiledata.mul`, `client.exe` and the art, map and sound files).
- The client version comes from, in order: `--client-version x.y.z.w` (the letter form such as `4.0.11c` also works), `--cuo-settings <settings.json>` (its `clientversion`), then `client.exe` (version resource, else the version format call of pre-resource clients). Pass `--client-version` when the exe has no readable version.
- Supported installs: 4.0.11c and later, MUL or UOP. Below 7.0.9 the converter uses the old tile data and multi layouts. `verdata.mul` is not applied; the converter prints a warning if one is present.
- The run ends with a verify step that reads the pack back and compares it with the loaders in `tools/ClassicUO.Assets.Mul`. The exit code is 0 on success, 2 when verify fails and 1 on argument errors.
- Debug PNGs go to a temp folder, or to `--debug <dir>`.

Other arguments (the full list is the usage line the converter prints on an unknown argument, in `tools/Uo.Converter/Program.cs`):

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
dotnet run -c Release --project <repo>/tools/Uo.Converter -- --uo <uo-install> --out <new-pack> --only gumps,layers
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

The client needs no UO directory. It takes the client version from `clientversion` if that is valid (sanitized), else from the pack manifest's `sourceClientVersion`, and sends that text to the server; the resolved version is not written back to `settings.json`. `ultimaonlinedirectory` is optional: when it is set and exists, it is the folder for the remaining loose reads (`Clilocs.txt`, `Sounds/<id>.wav`); otherwise the executable's folder is. Gumps, fonts, cliloc and speech come only from the pack: there is no MUL/UOP fallback, and a pack without `gumps/gumps.bin` or one of the `text/` files is refused. Skills, skill groups, world map images, the string dictionary and the hand-edited text/def files come from the pack (pack-format.md). A pack converted before revision 1.2 has no `skills.bin` and is refused: convert again.

Settings come from `settings.json` next to the executable, or from the file named by `-settings <file>`. Command-line arguments start with `-`, take one value (flags such as `-skiploginscreen` take none) and override the file. Relevant keys:

| `settings.json` key | Command line | Meaning |
|---|---|---|
| `ip`, `port` | `-ip`, `-port` | Server address; defaults `127.0.0.1` and `2593` |
| `clientversion` | `-clientversion` | Protocol version, for example `7.0.103.0` |
| `ultimaonlinedirectory` | `-uopath` | UO install folder |
| `pack` | `-pack` | Pack folder |
| `username`, `password` | `-username`, `-password` | Account; `saveaccount` must be true for the file to keep them |
| `autologin`, `saveaccount` | `-autologin true`, `-saveaccount false` | Log in at startup; do not store the account |
| `fps`, `vsync` | `-fps 0`, `-vsync false` | `0` is uncapped (the frame-limit slider's "Off" step in Options) |
| `world_renderer` | `-worldrenderer auto\|gpu\|compatibility` | See below |
| `window_display` | `-display <index\|name>` | The monitor the window opens on; see [Monitor and GPU](#monitor-and-gpu) |
| `profilespath` | `-profilespath <dir>` | Profile folder (never saved to the file) |
| | `-skiploginscreen`, `-nomessagebox`, `-packetlog` | Flags |

Example against a local ModernUO server on the default port:

```
<repo>/bin/Release/net11.0/cuo -settings <settings.json> -profilespath <profiles> \
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

### Monitor and GPU

`window_display` (or `-display`, which also writes the key) picks the monitor the window opens on: an SDL display index (0-based, in SDL's display order) or a display name, matched exactly first (ignoring case), then as part of a name, so `-display 1` and `-display "LG HDR"` both work. The window keeps its saved `window_position` when that lies on the chosen monitor and is centred on it otherwise; a maximized or borderless window then fills that monitor. An index or name that matches nothing opens on the primary. Without `window_display`, `window_position` alone decides, on whatever monitor holds it: positions left of or above the primary are negative and are kept (they were clamped to 0 before); a position on no monitor opens at the primary's top-left. The client saves the window position at exit.

At startup the client logs one `[display]` line: the display index, name, id and usable bounds, what chose it, the graphics adapter in use and its driver (FNA3D D3D11, OpenGL or SDLGPU with its backend), and `FNA3D_PREFER_LOW_POWER`. With FNA3D on D3D11 or OpenGL the adapter name is the one FNA3D reports (`D3D11 Adapter`, `OpenGL Renderer`); with SDL_GPU it is the SDL_GPU device name.

Rendering on an integrated GPU (a laptop or a desktop with a monitor on the integrated GPU's output):

- Set `FNA3D_PREFER_LOW_POWER=1` in the environment before starting the client. FNA3D's D3D11 driver then opens the low-power adapter, and the SDL_GPU (Vulkan) device is created with `preferlowpower` (the same hint is checked by the Gpu renderer probe, `RendererStartup.cs`). Check the adapter in the `[display]` line.
- If the driver still picks the discrete GPU, use the Windows per-app GPU preference: Settings > System > Display > Graphics, add `cuo.exe`, Options > Power saving. It applies at the next start.
- A window on a monitor wired to one GPU while another GPU renders works, but every presented frame is copied across adapters; time on the GPU that owns the monitor.

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

These `settings.json` keys select ECS, renderer, network, loop, asset and cache paths. Defaults are from `src/ClassicUO.Client/Configuration/Settings.cs`. A new ECS path lands with its switch off; the old path stays until the new one is the default and has been verified. Plain user preferences (login, window and reconnect options) are not listed.

Overrides (`CUO_<NAME>=1|0`) work only with `CuoDevTools=true`, and so do most checkers (`CUO_*_CHECK=1`; the next section marks which). A checker compares the new path with the old one at every use and logs mismatches (see the next section). "Read once" means at startup; the other switches are read at every use or every frame.

Switches that became permanent are removed with their off path, override and checker; an old `settings.json` that still names one loads, since unknown keys are skipped. Permanent since quiet-1123: `gpu_prepare_item_cache_gen` (the Gpu item cache trusts a tile while its chunk's render generation holds), `pick_skip_off_world` (no world pick tests while the mouse is off the world), `chunk_lights` (static and multi lights from the chunks' emitter lists into the ranked light list, bound 4096; `view_margin` is only the sprite margin), `ui_border_bake` (the composite copies a baked border and blends the UI only inside the viewport; FrameStats `ui_border_bake_px` counts the pixels baked per frame; one back-buffer-sized target, 8 MB at 1080p, 33 MB at 4K), `mesh_hue_on_change` and `mesh_vis_bits` (Legacy chunk meshes recompute a sprite's hue only on an input change and keep visibility as bits) and `font_layout_pool` (a text control's layout goes back to the font loader's node pool; `CUO_FONT_LAYOUT_CHECK=1` still guards it).

### Renderer and depth order

| Key | Default | Meaning |
|---|---|---|
| `world_renderer` | `auto` | World renderer: `auto`, `gpu`, `compatibility` (or `legacy`) |
| `gpu_force_point` | `false` | The Gpu world samples with the point sampler at every scale instead of sharp-bilinear. A diagnostic for tile grid lines; logged in `[render-info]` |
| `depth_model` | `false` | Per-pixel geometric depth by shape class instead of one depth key per sprite, so feet and large bodies stop going through walls. Both renderers. Env `CUO_DEPTH_MODEL=1\|0`; checker `CUO_CLIP_ORDER_CHECK=1` |
| `wet_surface_depth` | `false` | A mobile under a water surface (a wet static above its Z on its tile) is drawn submerged. With `depth_model`, wet flats drop 0.1 tile sums instead of 1.5, so the waterline is at the true height; without it, the mobile's strips all take +1, so no feet float under the cut. Env `CUO_WET_SURFACE_DEPTH=1\|0`; checker `CUO_WET_DEPTH_CHECK=1` |
| `mobile_wall_order` | `false` | Depth key mode only (`depth_model` off). A mobile's strips below its anchor row (feet, robe hem, hooves, a large body's legs) are capped one key below an object in front of it (one or two diagonals ahead, and while stepping, ahead of the tile it leaves): any static, multi piece or ground item over the strip's columns, unless it is flat (height 0), its top is at or under the mobile's Z, or the mobile could step onto it (the pathfinder's climb limit: first stair steps, altar blocks). Walls and doors count with the half of their art in front of the mobile and continue along their line as one segment. The upper strip's rows under a piece overhead on the mobile's own diagonal (bottom at or over Z + 16) drop behind it; shadows lose to every piece with height they overlap, behind the mobile too, but stay over the floors they lie on; a mobile standing on a staircase is drawn over the stair pieces it overlaps; the part of a sprite hanging over a side tile is covered by the pieces in front of that tile; all layers of a mobile share one cap. A mobile's scan is kept while its tile, step and the chunks' per-tile cap summaries are unchanged, and skipped when those summaries show nothing around it. Env `CUO_MOBILE_WALL_ORDER=1\|0`; checkers `CUO_MOBILE_WALL_ORDER_CHECK=1`, `CUO_MOBILE_WALL_ORDER_CACHE_CHECK=1` |
| `mobile_strip_order` | `false` | Depth key mode. A mobile's strip below its anchor row is split at the standing tile's bottom vertex (25 px under the anchor): the rows above it take + 0.75 instead of + 2, so they draw under a mobile one diagonal ahead (a horse's hooves no longer show through a player standing south or east of it) and still over the land and statics there; the rows further down keep + 2 (the land two diagonals ahead). `mobile_wall_order` caps and raises the near rows as the strip. No scan; an extra instance only for a layer whose lower strip crosses the split. Env `CUO_MOBILE_STRIP_ORDER=1\|0`; checker `CUO_MOBILE_STRIP_CHECK=1` |
| `walk_maxz_continue` | `false` | The world walk skips only an object over its max Z instead of ending the tile's walk there. The tile list is in PriorityZ order, not PriorityZ plus height, so the old rule dropped lower objects after a tall one. Env `CUO_WALK_MAXZ=1\|0`; checker `CUO_WALK_MAXZ_CHECK=1` |
| `item_prep_rows` | `false` | The world fill's item branch reads the graphic and flags from the item's packed `ItemDrawRow` (one record lookup) instead of the `Graphic` and `Flags` getters. Env `CUO_ITEM_PREP_ROWS=1\|0`; checker `CUO_ITEM_PREP_CHECK=1` |
| `item_pick_early_reject` | `false` | An art item's pick test rejects a mouse outside every box its pack sprite can be tested in, before it looks up the stored draw offset. Env `CUO_ITEM_PICK_EARLY_REJECT=1\|0`; checker `CUO_ITEM_PICK_CHECK=1` |
| `ui_gump_dirty` | `false` | The UI pass records its draws and leaves the UI targets untouched when they match the last frame's exactly; the cursor is drawn over a kept copy of the UI. Off: the UI is drawn every frame. Checkers `CUO_UI_DIRTY_CHECK=1`, `CUO_UI_CACHE_CHECK=1\|N` |
| `text_quad_cache` | `false` | A text control keeps the glyph quad runs of its last draw and reuses them while its layout, atlas, width, font, style, hue and cell are unchanged. Checker `CUO_TEXT_QUAD_CHECK=1` |
| `anim_frame_count_tables` | `false` | While the Gpu world draws, the animation advance reads a mobile's frame count from the pack's animation tables instead of the Legacy frame cache, which decodes and uploads the direction into an atlas the Gpu world never samples. Legacy keeps the cache read. Read once. Env `CUO_ANIM_FRAME_COUNT_TABLES=1\|0`; checker `CUO_ANIM_FRAME_COUNT_CHECK=1` |
| `step_drain` | `false` | A non-local mobile's step lasts its learned server pace (never longer than the table time; faster only when the pace is clearly faster), with a bounded drain of a real backlog, instead of the table time and the "mounted when steps are queued" guess. Env `CUO_STEP_DRAIN=1\|0`; checker `CUO_STEP_DRAIN_CHECK=1` |

### Lights

| Key | Default | Meaning |
|---|---|---|
| `view_margin` | `false` (High preset: on) | The sprite margin: the walk also reaches objects beside the view whose art reaches into it (the chunks' recorded art overhang), so trees, roofs and tall statics at the edges are drawn whole. Env `CUO_VIEW_MARGIN=1\|0`; checker `CUO_VIEW_MARGIN_CHECK=1` (sprites reaching the view that the walk did not draw) |
| `view_margin_walk` | `false` (High preset: on) | With `view_margin` on, the margin's tiles are walked only where an object can reach the view (the chunk's overhanging statics, mobiles, stretched land), and a margin object whose art box misses the view is left out; an item still fading in is kept so its fade completes. Cheaper than `view_margin` alone while walking or standing in a dense scene. Env `CUO_VIEW_MARGIN_WALK=1\|0` (dev builds); checker `CUO_VIEW_MARGIN_WALK_CHECK=1` (objects left out whose box reaches the view, and fading margin items left out) |
| `light_alpha_fade` | `false` | A surface glow (a lit window's beam: its light shape's class in the pack's `lightclass.bin`, or for a shape the table does not list the emitter's tiledata Wall/Window flags) is drawn at its emitter's drawn alpha: the circle of transparency (gradient and full) and the roof fade, so walls hidden by the circle leave no floating glows. Radial glows (lamps, torches, sconces, candles, braziers) and mobiles' lights keep full light. With `light_occlusion` on, an occluder's stencil mask is dithered to its drawn alpha (Bayer 4x4, `IsometricWorld.fx` `DITHER_MASK`) instead of cut at half opacity. Both renderers. Env `CUO_LIGHT_ALPHA_FADE=1\|0`; driver `lightalphafade on\|off`; checker `CUO_LIGHT_ALPHA_FADE_CHECK=1` |
| `light_range` | `false` | A light farther from the player than `light_range_tiles` (Chebyshev tiles; `0` = auto: the zoom-1 view's corner plus the largest halo plus the fade, 41 at 1920x1080) takes no slot and fades out over its last 2 tiles; the ranked list keeps at most `light_budget` lights (default 256, the best: in view first, then nearest the player) and, when the budget binds, its 16 lowest ranks fade by rank. Shares are recomputed each frame (no per-light state). Both renderers. Env `CUO_LIGHT_RANGE=1\|0`; driver `lightrange on\|off [tiles] [budget]`; checker `CUO_LIGHT_RANGE_CHECK=1` |
| `light_range_tiles`, `light_budget` | `0`, `256` | `light_range`'s range in tiles (`0` auto) and the most lights kept (`0` = 256, at most 4096) |
| `light_batch` | `false` (Medium preset: on) | With `light_occlusion` on, the light pass collects every light's occluders first, draws the lights with none in one batch without the stencil, then the occluded ones with their masks as before. Additive blending is order-free, so the light target is the same. Both renderers. Env `CUO_LIGHT_BATCH=1\|0`; driver `lightbatch on\|off`; checker `CUO_LIGHT_BATCH_CHECK=1` |
| `legacy_instanced_sprites` | `false` | Legacy renderer only. The world lists' sprites (not UI, text or gumps) go up as 44-byte instances that `IsometricWorld.fx` expands into the same quad the CPU builds (pixel-identical), instead of 192-byte quads, cutting the upload per sprite. Calls an instance cannot reproduce exactly (rotation, origin, geo mode, a capture) and devices without hardware instancing use the quad path. FrameStats keys `leg_inst_sprites`, `leg_quad_sprites`, `leg_path_switches`, `leg_inst_bytes`. Env `CUO_LEGACY_INSTANCED=1\|0`; driver `legacyinstanced on\|off`; checker `CUO_LEGACY_INSTANCED_CHECK=1` |
| `light_occlusion` | `false` (Medium preset: on) | A surface glow (a lit window's beam: the light shape's class in the pack's `lightclass.bin`, else the emitter's tiledata Wall/Window flags, as `light_alpha_fade` classifies them) is masked by the art of other statics, multi pieces and ground items that cover its light point and are drawn after it in the world draw (the occluders' art goes into the light target's stencil, per light). Its own emitter, and objects on its own tile or beside it on the same wall line, do not mask it. A lit window's glow is also hidden by wall pieces in front whose visible face turns away from it. Translucent and internal tiles, flat walkable surfaces (floors) and mobiles never occlude, and an object hides the glow only where the world draws it at least half opaque. Radial lights (candles, lamps, torches, braziers, fire fields, mobiles' lights) are never masked and get no occluder search or stencil work: the light map multiplies the frame, so they light the objects in front of them (the separate `light_occlusion_surface_only` switch was folded in; an old `settings.json` naming it still loads). Both renderers. Env `CUO_LIGHT_OCCLUSION=1\|0`; driver `lightocclusion on\|off`; checker `CUO_LIGHT_OCCLUSION_CHECK=1` compares each masked light's occluders with a scan of every tile of every drawn chunk, counts the radial lights with occluders left unmasked and reports a radial light drawn masked as a mismatch |

### Network

| Key | Default | Meaning |
|---|---|---|
| `span_reader` | `true` | Read packets with SpanReader and build them with SpanWriter. A packet a handler reads past its end ends the connection, logged with its bytes. Off: the old StackDataReader/Writer path. Read once at startup |
| `net_ring` | `false` | Decode received bytes into a double-mapped 256 KB ring per connection and frame and read packets in place; send through a bounded 128 KB ring that ends the connection when the socket takes nothing for 5 s. A platform that cannot map the ring keeps the old path (one log line). Read once at startup |
| `opl_request_budget` | `0` | Property-list requests (0xD6) leave at this many serials per second from a queue ordered hovered or in a gump first, then by distance from the player; requests for removed objects are dropped. 0: one list, 15 per frame. Read once. Env `CUO_OPL_REQUEST_BUDGET=<n>`; checker `CUO_OPL_SPREAD_CHECK=1` |

### Frame loop and present

| Key | Default | Meaning |
|---|---|---|
| `fixed_time_step` | `true` | Not read by the client: `GameController` sets `IsFixedTimeStep = false` whatever the key says. Only the `-fixed_time_step` command-line argument writes it |
| `skip_draw_when_minimized` | `true` | A minimized window skips world prepare, draw and present; packets, update and audio keep running. No checker: the draw is not run, so there is nothing to compare |
| `throttle_update_when_inactive` | `false` | An inactive (`ReduceFPSWhenInactive`) or minimized client sleeps 12 ms instead of 1 ms on frames that skip draw, so Update, input poll and packet drain run about 80 times a second instead of up to 1,000. A packet waits at most one sleep |
| `present_at_display_rate` | `true` | With the frame rate uncapped, vsync off and no inactive FPS reduction, skip Draw and Present until the display's next deadline. Explicit FPS caps and the inactive and minimized policies keep precedence. An unknown refresh rate uses the original path. Checker `CUO_PRESENT_CAP_CHECK=1` |
| `vsync_present_on_change` | `true` | With vsync on (no FPS cap, active window, event loop), the display-rate schedule and present on change run as with vsync off: unchanged frames skip Draw and Present, and the loop waits for the next change instead of blocking in Present at every vblank. Off: vsync draws every tick. Checker `CUO_VSYNC_PRESENT_CHECK=1` logs the presents it skips |
| `vsync_mailbox` | `false` | With vsync on, present in mailbox mode where supported. Off (since 2026-11-22): FIFO vsync with present on change is the default; mailbox at the display rate can show a frame twice or skip one as presents drift against the scanout. A saved value is kept |
| `motion_above_display_rate` | `false` | With present on change and a present that does not wait for the vblank (vsync off, or mailbox), frames while something moves present at three times the display rate; an idle screen still presents on change. Up to three times the draws while moving |
| `event_loop_max_wait_ms` | `16` | The event loop's longest wait without an Update, for timers it does not track (macros, audio streaming). 16 keeps Update at 60 Hz or more |
| `event_loop_idle_wait` | `true` | The event loop gathers the non-visual work Update polls (use queue, delayed click, music refill, driver waits, the ping) and, when none of it polls, waits until the next known deadline, at most `event_loop_idle_max_wait_ms`, instead of at most `event_loop_max_wait_ms`. Held input, a macro, pending decodes and OPL requests keep the normal cadence; out of game it stays. Checker `CUO_LOOP_IDLE_CHECK=1` reports deadlines served late |
| `event_loop_idle_max_wait_ms` | `250` | The longest idle wait of `event_loop_idle_wait` |
| `event_loop_macos` | `false` | macOS: wait between frames in SDL's Cocoa wait, ended by a kqueue over the socket and the cross-thread wake, instead of the legacy loop. Windows always uses its event loop and ignores the key. Env `CUO_EVENT_LOOP_MACOS=1\|0` |
| `event_loop_linux` | `false` | Linux: wait between frames in epoll (socket, wake eventfd, timerfd deadline, X11/Wayland display fd, SDL event watch) instead of the legacy loop's `Sleep(1)` and spin. Read once; the log names the waiter (`main loop: Event (epoll-x11)`). No effect on other OSes |
| `worldmap_idle_loop` | `false` | With the world map open, the loop polls at `event_loop_max_wait_ms` only while a map image loads; otherwise the gump's work is deadlines (the 250 ms party/guild query, the 1 s stale-entity prune), and packets and input wake it as before. Off: an open map keeps the loop at the `event_loop_max_wait_ms` cadence. Env `CUO_WORLDMAP_IDLE_LOOP=1\|0`; checker `CUO_WORLDMAP_IDLE_CHECK=1` |
| `asset_ready_wake` | `false` | A frame presented with a missing sprite or chunk while a load is pending is followed by a present as soon as a worker load completes (art, texmap, gump and animation decodes, Gpu page loads and chunk bakes wake the loop wait), with a 33 ms fallback deadline. Env `CUO_ASSET_READY_WAKE=1\|0`; checker `CUO_ASSET_WAKE_CHECK=1` counts completed loads still missing and presents later than one display interval after a completion |

### Assets and prefetch

| Key | Default | Meaning |
|---|---|---|
| `gump_decode_budget` | `false` | Count gump worker decodes in their own 32 MiB in-flight budget ([buffer-pools.md](buffer-pools.md)) |
| `chunk_ring_prefetch` | `false` | While the player moves, one chunk ring beyond the draw range on the side it moves to is requested ahead: the client's chunk loads (inside the chunk budget) and, on the Gpu world, its static art pages (only while no visible page waits, at most 2 pages a frame). Env `CUO_CHUNK_RING_PREFETCH=1\|0` |
| `asset_prefetch_on_packet` | `false` | The `WorldWriter` operations that place a mobile or item, change a body, graphic or hue, or equip request the new look's assets on packet arrival (the animation direction the draw will use, then the action's other directions on Gpu; mount and equipment; an item's art), at most 16 objects an Update, objects beyond the view range skipped. Env `CUO_ASSET_PREFETCH_ON_PACKET=1\|0`; checker `CUO_LOOK_PREFETCH_CHECK=1` measures packet-to-complete-draw latency per kind |
| `art_container_prefetch` | `false` | The static art of a container's contents and of the paperdoll's equipment slots decodes on the art workers before the gump builds its item controls (with a bounded batch wait), instead of one main-thread decode per sprite. Env `CUO_ART_CONTAINER_PREFETCH=1\|0` |
| `shared_cliloc` | `false` | Cliloc tables are read in place from read-only memory every instance shares: a raw record straight from the pack file, a compressed one from a per-user cache file it is decompressed into once (keyed by the pack's manifest hash). Replaces a heap copy per process. Read once. Env `CUO_SHARED_CLILOC=1\|0`; checker `CUO_SHARED_CLILOC_CHECK=1` compares every lookup with the heap path's |

### Memory and atlas LRU

| Key | Default | Meaning |
|---|---|---|
| `anim_atlas_lru` | `false` | The Legacy animation atlas (RGBA8 pages, 64 MiB each) frees its least recently used pages above a byte budget. The budget grows a page on thrash while memory allows and shrinks after minutes without (sooner when unfocused); evicted directions load again on use. Off: the atlas only grows. Read once. Env `CUO_ANIM_LRU=1\|0` and the `CUO_ANIM_LRU_*` sizes; checker `CUO_ANIM_LRU_CHECK=1` |
| `atlas_lru` | `false` | The Art, Gump, Texmap, Light and font glyph atlases free their least recently used pages above a budget per atlas, with the policy of `anim_atlas_lru`; evicted sprites load again on use, and the glyph caches drop the entries of a freed page. Off: the atlases only grow. Read once. Env `CUO_ATLAS_LRU=1\|0` and the `CUO_ATLAS_LRU_*` sizes; checker `CUO_ATLAS_LRU_CHECK=1` |

### Server gumps and text

| Key | Default | Meaning |
|---|---|---|
| `gump_layout_v2` | `false` | Server gumps (0xB0/0xDD) parse their layout in one pass over the string into token ranges and build the controls by entry kind, instead of two tokenizer passes that make a string per token and a list per entry. Env `CUO_GUMP_LAYOUT_V2=1\|0`; checker `CUO_GUMP_LAYOUT_CHECK=1` compares each parse and built tree with the old path |
| `gump_layout_cache` | `false` | Needs `gump_layout_v2`. The parsed layouts of the last 32 server gumps are kept by (gump id, layout), so a resend of the same layout skips the parse. Env `CUO_GUMP_LAYOUT_CACHE=1\|0`; checker `CUO_GUMP_LAYOUT_CHECK=1` checks every hit against the old tokenizer |
| `gump_resend_patch` | `false` | Needs `gump_layout_v2`. A server gump resent with the open gump's layout keeps its controls, and only the text lines that changed are patched in; typed text, checkbox and radio states, html scroll and the page survive the refresh (a text entry whose own line changed takes the server's value). Off: the gump is rebuilt. Env `CUO_GUMP_RESEND_PATCH=1\|0`; checker `CUO_GUMP_RESEND_CHECK=1` compares each patched gump with an old-path rebuild |
| `text_string_cache` | `true` | Server Cp1252 strings are decoded through a bounded cache keyed by their bytes (1024 sets of 2 ways, strings up to 64 bytes), so repeated names and lines allocate nothing. Read once on the game thread; other threads decode without it. Env `CUO_TEXT_STRING_CACHE=1\|0`; checker `CUO_TEXT_CACHE_CHECK=1` compares every cached string with a fresh decode |

## Environment variables

Checkers compare a new path with the old one and log mismatches. Do not measure performance with a checker on. Variables marked "Dev." work only with `CuoDevTools=true`; the others are read in every build. The overrides of settings switches (`CUO_<NAME>=1|0`) and the checkers named in the switch table are Dev. and are listed here too.

### Checkers

| Variable | Meaning |
|---|---|
| `CUO_LAYER_CACHE_CHECK=1\|N` | Dev. Compare cached `LayerDecision` entries with a fresh decision on every hit (`N`: every Nth hit); logs `[layers] cache checks` every 5 s |
| `CUO_MOBILE_UPDATE_CHECK=1\|stats` | Dev. Mobile clock and step system statistics (counts and times; `Mobile.Update` and its comparison are gone) |
| `CUO_FUNNEL_CHECK=1` | Dev. Report writes to the class A fields that keep a setter (X/Y/Z and the other caches) and the Mobile's record helpers made outside the `WorldWriter` funnel |
| `CUO_PREP_CHECK=1`, `CUO_GPU_ITEM_CHECK=1\|every`, `CUO_GPU_SKIP_CHECK` | Check the Gpu world prepare shortcuts against the full work |
| `CUO_W5_CHECK=1`, `CUO_MOBILE_CACHE_CHECK=1`, `CUO_MOBILE_INPUT_CHECK=1` | Check the active item list, the mobile frame cache and the pushed inputs |
| `CUO_ACTIVE_ITEMS_CHECK=1`, `CUO_ANIM_ADVANCE_CHECK=1` | Dev. Compare the active item list with the `ItemActive` tags every frame; compare a corpse's animation component with the Item's fields before each advance |
| `CUO_MOBILE_INPUT_BENCH=1` | Time the legacy and the pushed mobile input sample of the same mobile in the same frame (paired, 8 calls each, alternating which goes first); logged with the `[mobile-input-check]` timer |
| `CUO_UI_CACHE_CHECK=1\|N` | Compare the UI render cache's target with a replay of its recordings every N frames (default 30). `CUO_UI_CACHE_CHECK_DIR=<dir>` saves the first mismatches as PNGs. `CUO_UI_CACHE_FORCE=1` counts every frame as changed, to measure what the cache costs when the UI never repeats. Dev. `CUO_UI_DIRTY_CHECK=1` checks the gump recordings behind `ui_gump_dirty` |
| `CUO_TILE_ORDER_CHECK=1\|every\|N`, `CUO_RECYCLE_CHECK=1\|reuse` | Check the tile lists (order, links, no item, mobile or effect in a list), the dynamic tile index (entries valid and sorted, the merged walk in key order, every placed object reachable) and map object reuse |
| `CUO_CLIP_ORDER_CHECK=1` | Dev. Compare the two depth models (`depth_model` off and on) on the pairs a mobile forms with the land and statics around it: about every 2 s one Gpu frame is armed, a standing mobile's animation layers are sampled every 4 px, and pairs whose order changes are logged as `[clip-order]` lines with a summary |
| `CUO_HEALTH_LINES_CHECK=1`, `CUO_EFFECT_CHECK=1`, `CUO_PAPERDOLL_SLOTS_CHECK=1`, `CUO_PICK_RECORDS_CHECK=1`, `CUO_CONTAINER_CONTROLS_CHECK=1` | Dev. Compare the health line draws, the effect system, the paperdoll slots' prepared worn items (with `FindItemByLayer`), the record-served pick state, and a container's incremental item controls (with a full rebuild) with their older paths |
| `CUO_OPL_DEBOUNCE_CHECK=1`, `CUO_OPL_SPREAD_CHECK=1`, `CUO_OPL_SPREAD_CHECK_SECONDS=<s>` | Dev. Check the property-list request debounce and, with `opl_request_budget`, every send against the requests; the spread window is 120 s unless set |
| `CUO_ITEM_PREP_CHECK=1`, `CUO_ITEM_PICK_CHECK=1` | Dev. Checkers of `item_prep_rows` and `item_pick_early_reject` |
| `CUO_WALK_MAXZ_CHECK=1`, `CUO_WET_DEPTH_CHECK=1`, `CUO_MOBILE_WALL_ORDER_CHECK=1`, `CUO_MOBILE_WALL_ORDER_CACHE_CHECK=1` | Dev. Checkers of `walk_maxz_continue`, `wet_surface_depth` (logs every mobile found under a wet surface, with the rule it got) and `mobile_wall_order` (logs each strip the rule moves, or would move with the switch off, with the mobile, the wall and both keys; summary every 5 s; the cache check compares every kept or skipped scan with a fresh full scan and logs mismatches with the mobile, its tile and both wall lists) |
| `CUO_MOBILE_STRIP_CHECK=1` | Dev. Checker of `mobile_strip_order`: every split (with the switch off too) against the old add; MISMATCH when the near rows would draw later than before or, without a wall rule moving them, outside (0.5, 1); summary every 5 s with the switch state |
| `CUO_ANIM_FRAME_COUNT_CHECK=1`, `CUO_STEP_DRAIN_CHECK=1`, `CUO_LOOK_PREFETCH_CHECK=1` | Dev. Checkers of `anim_frame_count_tables`, `step_drain` and `asset_prefetch_on_packet` |
| `CUO_VIEW_MARGIN_CHECK=1`, `CUO_LIGHT_OCCLUSION_CHECK=1`, `CUO_LIGHT_ALPHA_FADE_CHECK=1` | Dev. Checkers of `view_margin`, `light_occlusion` and `light_alpha_fade` (`CUO_LIGHT_OCCLUSION_CHECK`'s summary also counts the radial lights with occluders left unmasked, and a radial light drawn masked is a mismatch; the last runs the unfaded path next to the new one: a radial glow faded, a glow faded at full alpha outside the circle, or a dithered mask set that lost a half-opaque occluder is a mismatch; summary every 5 s with the switch states and whether the pack has `lightclass.bin`) |
| `CUO_VIEW_MARGIN_WHATIF=1` | Dev. With FrameStats on, `view_margin_cull`, `view_margin_walk` and `view_margin_body` count what they would leave out while switched off (FrameStats `vm_margin_*`, `vm_ring_*`, `vm_body_mobiles_skipped`). Leave it unset in timing runs: the counting runs the switch's decision every frame |
| `CUO_LIGHT_RANGE_CHECK=1`, `CUO_LIGHT_BATCH_CHECK=1` | Dev. Checkers of `light_range` (every light pass: kept lights inside the range with the rule's share, the budget kept the best ranks and faded the lowest; with the switch off it counts the lights it would drop; summary every 5 s) and `light_batch` (once a second with `light_occlusion` on: renders the other path first, reads the light target back and compares it pixel by pixel with the active path, expected identical; also counts the non-overlapping sets the occluded lights would form, count only; summary every 5 s). On a compare frame the other light checkers count both renders |
| `CUO_LEGACY_INSTANCED_CHECK=1` | Dev. Checker of `legacy_instanced_sprites`: every call routed to the instance path also builds the old quad, expands the instance and compares the bytes (expected identical); logs at most 20 mismatches (texture size, source and destination rectangles, hue, both vertex sets) and a summary every 5 s with the checked and mismatch counts |
| `CUO_ASSET_WAKE_CHECK=1`, `CUO_LOOP_IDLE_CHECK=1`, `CUO_VSYNC_PRESENT_CHECK=1`, `CUO_PRESENT_CAP_CHECK=1`, `CUO_WORLDMAP_IDLE_CHECK=1` | Dev. Checkers of `asset_ready_wake`, `event_loop_idle_wait`, `vsync_present_on_change`, `present_at_display_rate` and `worldmap_idle_loop`; each logs a summary with the switch state every 5 s |
| `CUO_GUMP_LAYOUT_CHECK=1`, `CUO_GUMP_RESEND_CHECK=1`, `CUO_FONT_LAYOUT_CHECK=1`, `CUO_TEXT_QUAD_CHECK=1`, `CUO_TEXT_CACHE_CHECK=1`, `CUO_SHARED_CLILOC_CHECK=1` | Dev. Checkers of `gump_layout_v2` and `gump_layout_cache`, `gump_resend_patch`, the font layout pool (a layout reused while still held), `text_quad_cache`, `text_string_cache` and `shared_cliloc` |
| `CUO_ATLAS_LRU_CHECK=1`, `CUO_ANIM_LRU_CHECK=1` | Dev. Check reloads and freed pages of `atlas_lru` and `anim_atlas_lru` |
| `CUO_GUMP_BUDGET_CHECK=1` | Dev. Gump decode budget checker ([buffer-pools.md](buffer-pools.md)) |
| `CUO_SPAN_READER_CHECK=1`, `CUO_NET_RING_CHECK=1` | Dev. Run both readers and writers on every packet and log differences; frame every received slice with the old path too and compare, and shadow the sends |
| `CUO_MOTION_RATE_CHECK=1` | Dev. Run the display-rate present decision beside the one in force and log the draws only `motion_above_display_rate` made, draws closer than half the display period, and extra draws with the switch off |

### Settings overrides

| Variable | Meaning |
|---|---|
| `CUO_EVENT_LOOP_MACOS=1\|0` | Dev. Override `event_loop_macos` |
| `CUO_SPAN_READER=1\|0` | Dev. Override `span_reader` |
| `CUO_NET_RING=1\|0` | Dev. Override `net_ring` |
| `CUO_GUMP_DECODE_BUDGET=1\|0` | Dev. Override `gump_decode_budget` |
| `CUO_OPL_REQUEST_BUDGET=<n>` | Dev. Override `opl_request_budget` (0 turns it off) |
| `CUO_ANIM_LRU=1\|0`, `CUO_ANIM_LRU_BUDGET_MB`, `CUO_ANIM_LRU_FLOOR_MB`, `CUO_ANIM_LRU_CEILING_MB`, `CUO_ANIM_LRU_MIN_AGE_S`, `CUO_ANIM_LRU_THRASH_S`, `CUO_ANIM_LRU_SHRINK_S`, `CUO_ANIM_LRU_SHRINK_UNFOCUSED_S`, `CUO_ANIM_ATLAS_PAGE=<px>` | Dev. Override `anim_atlas_lru` and its policy. Defaults: budget 256 MiB, floor 128, ceiling 1024; a page is not freed within 45 s of use; thrash window 60 s; shrink after 180 s without thrash (45 s unfocused). `CUO_ANIM_ATLAS_PAGE` (1024 to 4096, default 4096) makes the pages smaller so eviction happens in a short scene |
| `CUO_ATLAS_LRU=1\|0`, `CUO_ATLAS_LRU_BUDGET_MB`, `CUO_ATLAS_LRU_FLOOR_MB`, `CUO_ATLAS_LRU_CEILING_MB`, `CUO_ATLAS_LRU_MIN_AGE_S`, `CUO_ATLAS_PAGE=<px>` | Dev. Override `atlas_lru` and the policy of every atlas it covers (the sizes then apply to each atlas). Per-atlas defaults, budget/floor/ceiling in MiB: art 256/128/1024, gump 192/128/768, texmap 64/32/256, light 32/16/64, font 32/16/128; times as for the animation atlas. `CUO_ATLAS_PAGE` (512 to 4096) sets the page edge, never below the largest sprite |

### Loop, present and frame rate

| Variable | Meaning |
|---|---|
| `CUO_FPS=<n>` | Dev. Frame rate cap for the run |
| `CUO_LOOP_LEGACY=1` | Dev. Use the legacy loop on Windows too, so its paths can be measured (they run off Windows and during a late-wake backoff) |
| `CUO_NET_CAP_BYTES=<n>`, `CUO_NET_CAP_PACKETS=<n>`, `CUO_NET_CAP_MS=<ms>` | Override the per-frame network drain caps (bytes read, packets handled, time) for measurements. `CUO_NET_CAP_BYTES=1` reads one 4 KB slice per frame, as before the drain loop |
| `CUO_IGNORE_FOCUS=1` | Dev. Count the window as focused whatever its real state, so the inactive and minimized throttles do not apply (as in an automated run) |
| `CUO_NET_TRACE=1` | Track per-frame network parse cost |
| `CUO_LOOP_STATS=1`, `CUO_LOOP_STATS_SEC=<s>` | Dev. Log a `[loop]` line (mode, ticks, draws, waits, wake reasons, late waits, process CPU ms/s) every 5 s or `<s>` |

### Assets, prefetch and cache

| Variable | Meaning |
|---|---|
| `CUO_ANIM_SYNC=1`, `CUO_ART_SYNC=1`, `CUO_CHUNK_SYNC=1`, `CUO_GUMP_SYNC=1` | Decode animations, art, map chunks or gumps synchronously |
| `CUO_PLACED_ART_SYNC=1` | Placements request no art (chunk art still does), so a counter run can compare the sync decodes with and without the placement requests |
| `CUO_ART_SYNC_LOG=1` | Log a short stack (`[artsync]` line) for each art decode the main thread does on first sight |
| `CUO_CHUNK_BUDGET_MS=<ms>` | Time per frame the client's chunk loads may use (default 2) |
| `CUO_DECODE_CAP_MB=<mb>` | The cap on bytes in flight in the shared art, texmap and animation decode budget (default 16; 0 means unlimited, the budget only counts) |
| `CUO_DECODE_WAIT_BOOST=0` | Block the main thread on a decode at its own priority instead of above normal. Above normal, the wake is not delayed behind other runnable threads when several clients run |
| `CUO_GPU_GROW=0` | Start the Gpu world's art layers, animation layers and instance buffers at their ceilings instead of growing on demand (the old fixed sizes, for comparison) |
| `CUO_WARMUP=0`, `CUO_WARMUP_GC=0\|2`, `CUO_WARMUP_CHUNKS=<n>` | The login prefill of the map pools and the deliberate collection after it. `CUO_WARMUP=0`: no prefill, no collection. `CUO_WARMUP_GC=0`: prefill without the collection; `2`: a full compacting gen2 instead of two gen1s. `CUO_WARMUP_CHUNKS`: chunks to prefill (default and maximum 300) |
| `CUO_RECYCLE=0`, `CUO_RECYCLE_QUARANTINE_MS=<ms>` | Turn map object reuse off; the time a recycled object waits before reuse (default 1000) |
| `CUO_WORLD_HOLD=0`, `CUO_WORLD_HOLD_TIMEOUT=<ms>`, `CUO_WORLD_SETTLE_LOG=1` | Gpu world settle gate. `CUO_WORLD_HOLD=0`: observe only, no frame is held across a discontinuity. The timeout of a hold defaults to 1500 ms. The log names what was missing in every presented frame whose view was not ready |
| `CUO_LEGACY_PREFETCH=obj,node,row` | Dev. Distances (0 to 64) the Legacy world draw prefetches ahead in the render list: an object's memory, an item's mirror node, its record (defaults 16, 8, 4; 0 turns a stage off) |
| `CUO_SHARED_CACHE_DIR=<dir>` | The root of the per-user cache (shared tables, disk cache). Default: `%LOCALAPPDATA%` on Windows, `$XDG_CACHE_HOME` or `~/.cache` on Linux, `~/Library/Caches` on macOS, with `ModernUO/cache` under it |
| `CUO_CONTAINER_INCREMENTAL=0` | Dev. Force the full rebuild of a container's item controls instead of reusing the controls of items shown before |
| `CUO_CLILOC_ID_TRACE=<file>` | Dev. Append every cliloc id looked up as an i32 to the file (an id stream for the shared-cliloc benchmark) |

### Diagnostics

| Variable | Meaning |
|---|---|
| `CUO_RENDERER_PROBE_FAIL=device\|world`, `CUO_RENDERER_LEGACY_DRIVER=OpenGL` | Force the renderer selection fallbacks for testing |
| `CUO_SIMULATE_DEVICE_LOST=<s>`, `CUO_DEVICE_LOST_NOEXIT=1` | Device-loss handler tests |
| `CUO_POOL_AUDIT=1` | Dev. Buffer pool audit ([buffer-pools.md](buffer-pools.md)) |
| `CUO_MEM_LOG=<seconds>` | Dev. Log the GC heap every N seconds |
| `CUO_MEM_REGIONS=<s>,<s>,...` | Dev, Windows. At those seconds after start, log every private allocation with 1 MiB or more committed: base, reserved, committed and protections |
| `CUO_LOAD_MEM=1` | Force a full collection before the load-time heap size is read, so the figure is what the loaders keep (measurements only) |
| `CUO_DIAG=1` | Dev. Diagnostic counters and probes in the client, the renderer and the batcher; keeps the FrameStats phase timings on |
| `CUO_FRAMESTATS=1` | Dev. Per-phase frame timings every 5 s (see DevBench). `CUO_FRAMESTATS_PHASES=0` turns the phase split off unless `CUO_DIAG=1`; `CUO_FRAMESTATS_ALLOC=1` adds allocation per phase; `CUO_FRAMESTATS_PREP=1` splits the tile walk into buckets (adds timestamp overhead, so compare with a run without it) |
| `CUO_GEOM_LOG=1`, `CUO_GEOM_SHOTS=<dir>` | Dev. Log each pass's target, device viewport, projection and camera next to the window, back buffer, DPI scale and composite rects whenever one of them changes (`[geom]` lines); with the directory set, a world shot is saved 30 frames after each change |
| `CUO_POPIN_SHOTS=<dir>\|<pairs>` | Dev. Save before and after screenshots around unsettled Gpu world frames into the directory (default 12 pairs) |
| `CUO_GOLDEN_DIR=<dir>` | Dev. Directory of golden captures (the world's dynamic objects in draw order with their draws, and the ECS state, as text), for comparing two builds. One directory per instance |
| `CUO_NET_CAPTURE=<file>` | Dev. Record the inbound stream and the outbound packets of a live session. The file holds account data; keep it out of the repository |
| `CUO_NET_REPLAY=<file>`, `CUO_NET_REPLAY_CHUNKS=recorded\|byte\|random\|max`, `CUO_NET_REPLAY_STEP_MS=<ms>` | Dev. Replay a capture instead of connecting: the frame clock advances a fixed step (default 16 ms) per Update, the drain caps are off, and `CUO_NET_REPLAY_CHUNKS` sets how the reads are cut. When the capture is used up and 120 more frames have run, the client exits |
| `CUO_NET_TRACE_OUT=<file>`, `CUO_NET_TRACE_OPS=1`, `CUO_NET_TRACE_CHECKPOINT=<n>` | Dev. Write a packet trace of a live session or a replay; `OPS=1` adds the world operations, and a checkpoint line comes every n packets (default 500) |

`CUO_MAIN_THREAD` is the name of the main thread in a debugger, not a variable.

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
| `CUO_BENCH_NOSHOT=1` | The screenshot a plan entry takes at its start is skipped, so its PNG encode does not land in the first measurement window |
| `CUO_BENCH_TOPMOST=1` | Keep the window on top and raised, so the compositor does not throttle the presents of an occluded window |
| `CUO_BENCH_TPSHOTS="<dir>\|<n>"` | After a teleport, save world shots into the directory on the following frames (default 8) |
| `CUO_BENCH_RELOG=<cycles>:<dwell seconds>` | Log in, stay in game for the dwell time, log out to the login scene (autologin logs in again), `cycles` times; logs `[relog]` lines (private bytes, collections, pool sizes) at each in-game moment, then quits |
| `CUO_BENCH_GUMPOPEN="name;name"` | The windows the `gumpopen` entry opens, one every 2.5 s, cold: `paperdoll`, `backpack`, `status`, `journal`, `worldmap`, `combatbook`, `spellbook`, or `pics:HEX,HEX` (a plain gump with those gump pictures). Default: paperdoll, spellbook, status, backpack, worldmap, combatbook, journal |
| `CUO_BENCH_BODIES_WRAP=1` | The `bodies` mode starts over after the last body instead of stopping (animation atlas gates: evicted pages load again) |
| `CUO_BENCH_UNEQUIP=1` | In the UI tour, pick up the player's robe, to test the unequip path |

Main plan entries: `name:x y:zoom` (go there and measure), `travel`, `walk`, `say:text`, `vsync:on|off`, `world:gpu|legacy`, `size:WxH`, `shot:name`, `gumps`, `ecsdump:<hex serial>|player|nearest`, `lightframes:N` (log N consecutive frames of the light pass: the offset its lights were placed at and the world's offset; `CUO_LIGHT_FRAMES=N` does it in every measured scene, `CUO_LIGHT_FRAMES_READBACK=0` skips the readbacks), `altlights:on|off`, and `on|off` entries that switch a setting for the rest of the plan. The entries of retired switches (`uicache`, `gpuskip`, `itemcache`, `itemmixed`, `fxserve`, `textskip`, `w5`) are skipped.

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
