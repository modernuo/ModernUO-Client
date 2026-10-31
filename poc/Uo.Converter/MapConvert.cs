using System.Runtime.InteropServices;
using ClassicUO.Assets;
using ClassicUO.Utility;
using Uo.Pack;

namespace Uo.Converter;

public static class MapConvert
{
    public sealed record Stats(int Facet, int Width, int Height, int Regions, int Blocks, long Statics, long Bytes, double Seconds);

    private const int MapBlockBytes = 4 + 64 * 3; // header + MapCells[64]
    private const int StaticRecBytes = 7;          // StaticsBlock

    /// <summary>Facets the client actually has: those whose source files <see cref="FacetSources.Resolve"/> finds (a map
    /// file on disk; below 6.0.0.0 Trammel is map0, as ModernUO's <c>maps.enablePre6000Trammel</c>). ClassicUO's
    /// unconditional map1 := map0 fallback is not followed for newer clients.</summary>
    public static IEnumerable<int> AvailableFacets(UOFileManager fm)
    {
        for (int i = 0; i < MapLoader.MAPS_COUNT; i++)
            if (FacetSources.Resolve(fm, i) != null && (ConvertOptions.Facets == null || ConvertOptions.Facets.Contains(i))) yield return i;
    }

    public static Stats Facet(UOFileManager fm, int facet, string outDir)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var src = FacetSources.Resolve(fm, facet) ?? throw new InvalidOperationException($"facet {facet} has no map file");
        int bw = fm.Maps.MapsDefaultSize[facet, 0] >> 3, bh = fm.Maps.MapsDefaultSize[facet, 1] >> 3;
        // Base files first, then the diffs on top.
        var table = FacetTable.Build(fm, src, bw, bh, ClientLoad.MapDiffs);
        Console.WriteLine($"  facet {facet}: {Path.GetFileName(src.Map)}{(src.FileIndex != facet ? $" (file index {src.FileIndex})" : "")}, " +
                          $"{(src.StaIdx != null ? $"{Path.GetFileName(src.StaIdx)} + {Path.GetFileName(src.Statics)}" : "no statics")}" +
                          (table.LandPatches + table.StaticPatches > 0 ? $"; diffs applied as ModernUO serves them: {table.LandPatches} land blocks, {table.StaticPatches} static blocks" : "") +
                          (table.BadStaticEntries > 0 ? $"; {table.BadStaticEntries} static index entries out of range (read as empty)" : ""));
        var info = new FacetInfo
        {
            Index = facet, Width = fm.Maps.MapsDefaultSize[facet, 0], Height = fm.Maps.MapsDefaultSize[facet, 1],
            BlocksX = bw, BlocksY = bh, RegionBlocks = PackConst.RegionBlocks,
        };

        var dir = Path.Combine(outDir, "map", facet.ToString());
        Directory.CreateDirectory(dir);
        FacetFileIo.Write(dir, info);

        int rxCount = (bw + PackConst.RegionBlocks - 1) / PackConst.RegionBlocks;
        int ryCount = (bh + PackConst.RegionBlocks - 1) / PackConst.RegionBlocks;
        long statics = 0, bytes = 0;
        int blocksWritten = 0;

        Parallel.For(0, Math.Min(rxCount * ryCount, ConvertOptions.MaxRegions), ConvertOptions.Par, r =>
        {
            int rx = r % rxCount, ry = r / rxCount;
            int bx0 = rx * PackConst.RegionBlocks, by0 = ry * PackConst.RegionBlocks;
            int w = Math.Min(PackConst.RegionBlocks, bw - bx0), h = Math.Min(PackConst.RegionBlocks, bh - by0);
            var blocks = new BlockData[w * h];
            long st = 0;
            int nb = 0;
            var tmp = new List<StaticCell>(64);
            for (int by = 0; by < h; by++)
                for (int bx = 0; bx < w; bx++)
                {
                    // UO block numbering is column-major: block = x * blocksY + y.
                    int id = (bx0 + bx) * bh + (by0 + by);
                    if (table.LandSrc[id] == FacetTable.None) continue;
                    var cells = table.Sources[table.LandSrc[id]].AsSpan((int)table.LandOff[id] + 4, 64 * 3);
                    var land = new LandCell[PackConst.CellsPerBlock];
                    for (int c = 0; c < 64; c++)
                        land[c] = new LandCell { TileId = (ushort)((cells[c * 3] | (cells[c * 3 + 1] << 8)) & 0x3FFF), Z = (sbyte)cells[c * 3 + 2] };

                    tmp.Clear();
                    if (table.StaSrc[id] != FacetTable.None) // offset 0 is valid; the range was checked when the table was built
                    {
                        var sb = table.Sources[table.StaSrc[id]];
                        int start = table.StaOff[id], end = start + table.StaCount[id] * StaticRecBytes;
                        for (int p = start; p < end; p += StaticRecBytes)
                        {
                            ushort g = (ushort)(sb[p] | (sb[p + 1] << 8));
                            byte x = sb[p + 2], y = sb[p + 3];
                            // Same filter ClassicUO's Chunk.Load applies.
                            if (g == 0 || g == 0xFFFF || x > 7 || y > 7) continue;
                            tmp.Add(new StaticCell { Graphic = g, X = x, Y = y, Z = (sbyte)sb[p + 4], Hue = (ushort)(sb[p + 5] | (sb[p + 6] << 8)) });
                        }
                    }
                    blocks[by * w + bx] = new BlockData(land, tmp.ToArray());
                    st += tmp.Count;
                    nb++;
                }

            var header = new RegionHeader { Facet = (ushort)facet, Rx = (ushort)rx, Ry = (ushort)ry, BlocksW = (ushort)w, BlocksH = (ushort)h };
            var path = PackConst.RegionPath(dir, rx, ry);
            RegionFile.Write(path, in header, blocks, ConvertOptions.CompressRegions);
            Interlocked.Add(ref statics, st);
            Interlocked.Add(ref blocksWritten, nb);
            Interlocked.Add(ref bytes, new FileInfo(path).Length);
        });

        return new Stats(facet, info.Width, info.Height, Math.Min(rxCount * ryCount, ConvertOptions.MaxRegions), blocksWritten, statics, bytes, sw.Elapsed.TotalSeconds);
    }
}
