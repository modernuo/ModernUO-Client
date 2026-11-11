# CLAUDE.md

Guidance for coding agents working in this repository. Build, convert, run and test details are in [docs/dev/runbook.md](docs/dev/runbook.md).

## What this repo is

The ModernUO client, built on ClassicUO (a fork). It differs from upstream ClassicUO in three ways:

- Assets come from a converted pack, not from the UO client's MUL/UOP files.
- World state is mirrored in an ECS, and writes to it go through one funnel.
- The world can be drawn by a GPU renderer (SDL_GPU through FNA3D) as well as the original FNA batcher ("Legacy").

Target framework is `net10.0`. Submodules (`git submodule update --init --recursive`) are in `external/`.

## Layout

`src/`:

- `ClassicUO.Client`: the game (`cuo` executable): scenes, UI, network, packet handlers, world objects, `Game/Ecs` (the ECS world state and the `WorldWriter` funnel), `DevBench`.
- `ClassicUO.Assets`: asset loaders; reads the pack (`PackAssets`).
- `ClassicUO.Ecs`: ECS components, the world mirror and the mobile clock.
- `ClassicUO.Renderer`: the batcher, art, animation and font rendering.
- `ClassicUO.IO`: file readers, packet reader and writer, audio decoding.
- `ClassicUO.Utility`: shared helpers (logging, collections, client version, `Crypter`).
- `ClassicUO.Bootstrap`: plugin-host launcher.
- `native/cuo_mp3`: C source of the static MP3 decoder.

`poc/` (pack and renderer libraries and tools; not in `ClassicUO.sln`):

- `Uo.Pack`: pack format structs, readers and writers, layer rules data.
- `Uo.Converter`: converts a UO install into a pack and verifies it.
- `Uo.Render`: the GPU world renderer (SDL_GPU): page residency, chunk baking, shaders.
- `Uo.Render.App`: standalone viewer and test app for `Uo.Render`.
- `Uo.Net`: protocol client library; `Uo.Net.Cli`: headless test client.
- `Uo.Client`: standalone prototype client.

Other folders:

- `tests/`: `ClassicUO.UnitTests` (xUnit) and `Uo.Converter.Tests`.
- `tools/`: `ClassicUO.Assets.Mul` (MUL/UOP loaders, used by the converter and its tests only), scripts for benchmarking (`aot-bench`, `fps-test`, `pgo`), native builds (`fna3d`, `sdl`), and ECS analysis (`ecs-census`, `ecs-parity`).
- `research/`: experiments that do not ship (for example `ecs-bench`).
- `scripts/`: `build-naot.sh`.
- `docs/dev/`: developer docs.

`external/` submodules: `FNA` (fork: `modernuo/FNA`), `SDL` (fork: `modernuo/SDL`), `FileEmbed` (upstream `SirCxyrtyx/FileEmbed`). FNA3D is a nested submodule of FNA, also a `modernuo` fork. Prebuilt natives are committed in `external/x64`, `lib64` and `osx`.

## Build and test

```
dotnet build src/ClassicUO.Client/ClassicUO.Client.csproj -c Release
dotnet build src/ClassicUO.Client/ClassicUO.Client.csproj -c Release -p:CuoDevTools=false
dotnet test tests/ClassicUO.UnitTests -c Release
dotnet test tests/Uo.Converter.Tests -c Release
```

`CuoDevTools` defaults to true and defines `CUO_DEVTOOLS` (DevBench, runtime renderer switching, the `CUO_*_CHECK` checkers). Code under it is tagged `CLEANUP(release)`. A change that touches guarded code must build in both variants. Many verification steps need a UO install, a pack and a ModernUO server; if you cannot run them, say so in the PR instead of claiming a pass.

## Conventions

### World-state writes go through `WorldWriter`

`WorldWriter` (`src/ClassicUO.Client/Game/Ecs/WorldWriter.cs`) is the one writer of world-object state (items, mobiles, equipment, containers). Each operation matches a packet's meaning, not a field. It computes the new values, writes the ECS and the `GameObject` cache from the same values, then runs derived-state helpers. Operations do not nest. `FunnelCheck` (`CUO_FUNNEL_CHECK=1`) reports writes made outside it. Add new packet handling as a `WorldWriter` operation; do not set world fields from a handler.

### New ECS paths: switch plus checker

A new ECS read or update path lands behind a `settings.json` switch (`Configuration/Settings.cs`), default off at first, with a `CUO_*_CHECK` environment checker that compares the new path with the old one at every use and logs mismatches. Example: `ecs_item_draw_row` and `CUO_ITEM_DRAW_ROW_CHECK`. The old path and the checker stay until the switch has been default on for a while; then the old path, the switch and any checker that compares against the old path are deleted (an old `settings.json` that still names the key loads, since unknown keys are skipped). Checkers and `CUO_ECS_*` overrides are compiled only with `CUO_DEVTOOLS`. Add unit tests that exercise both switch states.

### The pack is the asset source

The client reads art, maps, tile data, hues, animations, gumps, fonts, text and audio from the pack. A few MUL/UOP fallbacks remain (gumps with `CUO_PACK_GUMPS=0`, fonts/cliloc/speech with `CUO_PACK_TEXT=0`, and the startup check for a UO folder); do not add new ones. A change to what the pack holds goes in `poc/Uo.Converter` and `poc/Uo.Pack`, and the same change updates [pack-format.md](docs/dev/pack-format.md) and, for table contents, [data-formats.md](docs/dev/data-formats.md). Body sets are chosen at import ([bodies.md](docs/dev/bodies.md)); equipment layering rules are pack data ([layer-rules.md](docs/dev/layer-rules.md)). The pack keeps no derived duplicates of its own data. Published packs are immutable; the converter writes new files by replacing, never in place.

### Class A fields: no new direct writes

Class A fields are the ECS-owned world-state fields of `GameObject`, `Entity`, `Item` and `Mobile` (graphic, hue, position, direction, flags, hits, amount, container, layer and so on). Do not add direct writes to them. `tests/ClassicUO.UnitTests/Ecs/WriteSiteBudgetTests.cs` counts the writes per file and field against `WriteSiteAllowlist.txt` and fails if the count grows or shrinks. Removing writes lowers the budget: regenerate with `CUO_WRITE_BUDGET_REGEN=1 dotnet test tests/ClassicUO.UnitTests --filter WriteSiteBudgetTests`. Regenerate only on purpose, and say so in the PR.

### Performance changes

A performance claim needs a paired comparison: a base build and a new build from the same tip, run interleaved, several runs each. Time with a NativeAOT build or with `DOTNET_TieredCompilation=0`. Compare CPU phases and per-frame allocations, with checkers off. Details are in the runbook.

### Docs and commits

Code commits do not touch docs. Changes under `docs/dev/` go in their own commits. Keep commits focused: one change per commit, a message that says what and why.

## Style

- C# code follows the existing file: 4-space indent, braces on their own lines, `_camelCase` private fields.
- Do not add comments that restate the code. Comments explain why, and name the switch or checker involved.
- No new allocations in per-frame paths; check FrameStats `alloc/frame` when you touch one.
- Do not commit local paths, account names, passwords or settings files. Passwords belong only encrypted in a throwaway `settings.json`.
