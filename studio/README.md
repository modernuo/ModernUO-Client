# ModernUO Studio

Asset and map tooling for ModernUO packs: the UO Fiddler and CentrED jobs, on the ModernUO client's own loaders and renderer.

Studio is its own executable (`muostudio`). It references the client's libraries (`ClassicUO.Assets` for the pack, `ClassicUO.Renderer` for the atlases) and never the game client (`ClassicUO.Client`), so the folder can move to its own repository with those libraries as dependencies. It reads only packs made by `poc/Uo.Converter`; it never opens MUL or UOP files.

The UI is Dear ImGui (Hexa.NET.ImGui, docking branch) drawn through FNA on the same FNA3D device the client uses (SDL_GPU on Vulkan where available), with Studio's own theme and widgets in the ModernUO brand (see [UX.md](UX.md), "Visual language"). The fonts are embedded from `ModernUO.Studio/Fonts/`: Inter 4.1 and JetBrains Mono 2.304 (SIL Open Font License 1.1) and Lucide 1.67.0 icons (ISC), each with its licence file. The ModernUO logo in `ModernUO.Studio/Branding/` (from modernuo.com/branding) is the exe icon (`muostudio.ico`, 16 to 256 px), the window icon and the top bar mark.

## Build and run

```
dotnet build studio/ModernUO.Studio -c Release -p:DxcSpirv=<dxc>
bin/Studio/Release/net10.0/muostudio -pack <pack>
```

The map draws with the client's GPU world renderer (`poc/Uo.Render`) on FNA3D's SDL_GPU device, which is Vulkan on Windows and Linux. Its shaders are compiled at build time; the SPIR-V (Vulkan) ones need the official DXC release from `github.com/microsoft/DirectXShaderCompiler` (the Windows SDK's `dxc` has no SPIR-V back end). `-p:DxcSpirv=<dxc>` points at that release's `bin/x64/dxc.exe`; without it the build warns, the asset browsers still work and the Map panel says what is missing.

`-pack <dir>` opens a pack at startup; otherwise use File > Open Pack or the Start panel. For smoke tests: `-screenshot <file.png>` saves a frame and exits, after `-frames <n>` frames (default 90); `-layout browse|map` picks the workspace built when there is no saved layout; `-focus <panel>` and `-select <id>` bring a panel forward with an item selected; `-goto "x y"` centres the map on a tile and picks it. `studio.json` (recent packs, window size) and `studio_layout.ini` (the docking layout) are written next to the executable. Exports go to `export/` next to it unless `ExportDirectory` is set in `studio.json`.

## What is there

| Panel | Contents |
|---|---|
| Art | Land and static art in a virtualised grid; search by id or tiledata name, filter by tiledata flag; details with tiledata fields, a CPU hue preview (full and partial hue) and PNG export |
| Gumps | Every gump image, hue preview, PNG export |
| Hues | All hues with their 32 shades and names, shade values on hover, preview of a hue on any static or gump |
| TileData | Land and static tiledata as a table, filter by name, id and flag; double-click opens the art |
| Animations | Every body in the pack's resolved body set; actions, the eight directions (mirrored ones included), playback, frame strip |
| Multis | Multi components drawn as an isometric composite (pan, zoom, max z), component table |
| Texmaps | Land textures and the land tiles that use each one |
| Sounds & Music | Play and export sounds as WAV; export music as the stored MP3 |
| Cliloc | Search the string tables by number or text |
| Pack Info | Manifest, facets, files and how each section loaded |
| Map | A facet drawn by the client's GPU world renderer: drag or arrow keys to pan, wheel or +/- to zoom about the cursor, go to x y, facet switch, max z, roof hiding, tile grid; click picks a tile |
| Tile Inspector | The picked tile's land and statics (highest first) with z, height and hue, each linked to its art and tiledata; hovering a static highlights it on the map |
| Radar | A top-down overview of the facet from radarcol.bin, built on a worker; the map's view is outlined, and clicking or dragging moves the map |

View > Layout switches between the Browse workspace (browsers as tabs) and the Map workspace (the map in the centre, the tile inspector and radar on the right, art, tiledata, hues and multis as palettes on the left).

## Roadmap

1. Shell and asset viewers (done).
2. Map viewer (done): Uo.Render used directly on FNA's SDL_GPU device; the client's `GpuWorldRenderer` stays in the client, since its job is the game objects Studio does not have.
3. Map editing (CentrED): edits as operations with undo and redo; land draw, altitude raise/lower/smooth, static add/move/delete, random static brushes, area fill and copy; a new pack is written for the changed regions (published packs stay immutable).
4. Asset editing (UO Fiddler): import PNG art and gumps, tiledata, hue and multi editing, all written through `poc/Uo.Pack` into a new pack, with a pack diff view.
5. Collaboration: the edit operations over the network to a shared map server (possibly inside ModernUO), with accounts and region locks.
