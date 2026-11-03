# ModernUO Studio

Asset and map tooling for ModernUO packs: the UO Fiddler and CentrED jobs, on the ModernUO client's own loaders and renderer.

Studio is its own executable (`muostudio`). It references the client's libraries (`ClassicUO.Assets` for the pack, `ClassicUO.Renderer` for the atlases) and never the game client (`ClassicUO.Client`), so the folder can move to its own repository with those libraries as dependencies. It reads only packs made by `poc/Uo.Converter`; it never opens MUL or UOP files.

The UI is Dear ImGui (Hexa.NET.ImGui, docking branch) drawn through FNA on the same FNA3D device the client uses (SDL_GPU on Vulkan where available).

## Build and run

```
dotnet build studio/ModernUO.Studio -c Release
bin/Studio/Release/net10.0/muostudio -pack <pack>
```

`-pack <dir>` opens a pack at startup; otherwise use File > Open Pack or the Start panel. `-screenshot <file.png>` saves a frame after a short warm-up and exits (smoke tests). `studio.json` (recent packs, window size) and `studio_layout.ini` (the docking layout) are written next to the executable. Exports go to `export/` next to it unless `ExportDirectory` is set in `studio.json`.

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

## Roadmap

1. Shell and asset viewers (this milestone).
2. Map viewer: move `GpuWorldRenderer`'s Uo.Render glue out of `ClassicUO.Client` into a library, then a facet view with free camera, radar overview, go-to, tile and static inspector, z and layer filters.
3. Map editing (CentrED): edits as operations with undo and redo; land draw, altitude raise/lower/smooth, static add/move/delete, random static brushes, area fill and copy; a new pack is written for the changed regions (published packs stay immutable).
4. Asset editing (UO Fiddler): import PNG art and gumps, tiledata, hue and multi editing, all written through `poc/Uo.Pack` into a new pack, with a pack diff view.
5. Collaboration: the edit operations over the network to a shared map server (possibly inside ModernUO), with accounts and region locks.
