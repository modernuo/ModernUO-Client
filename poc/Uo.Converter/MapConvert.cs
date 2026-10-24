using System.Runtime.InteropServices;
using ClassicUO.Assets;
using Uo.Pack;

namespace Uo.Converter;

public static class MapConvert
{
    public sealed record Stats(int Facet, int Width, int Height, int Regions, int Blocks, long Statics, long Bytes, double Seconds);

    private const int MapBlockBytes = 4 + 64 * 3; // header + MapCells[64]
    private const int StaticRecBytes = 7;          // StaticsBlock

    /// <summary>Facets the client actually has (a map file on disk, not ClassicUO's map1 := map0 fallback).</summary>
    public static IEnumerable<int> AvailableFacets(UOFileManager fm)
    {
        for (int i = 0; i < MapLoader.MAPS_COUNT; i++)
        {
            bool uop = File.Exists(fm.GetUOFilePath($"map{i}LegacyMUL.uop"));
            bool mul = File.Exists(fm.GetUOFilePath($"map{i}.mul"));
            if ((uop || mul) && (ConvertOptions.Facets == null || ConvertOptions.Facets.Contains(i))) yield return i;
        }
    }

    public static Stats Facet(UOFileManager fm, int facet, string outDir, bool useXFiles)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var maps = fm.Maps;
        maps.LoadMap(facet, useXFiles);
        var index = maps.BlockData[facet] ?? throw new InvalidOperationException($"facet {facet} has no block table");
        int bw = maps.MapBlocksSize[facet, 0], bh = maps.MapBlocksSize[facet, 1];
        var info = new FacetInfo
        {
            Index = facet, Width = maps.MapsDefaultSize[facet, 0], Height = maps.MapsDefaultSize[facet, 1],
            BlocksX = bw, BlocksY = bh, RegionBlocks = PackConst.RegionBlocks,
        };

        // The block table references a small set of files (map uop/mul, statics mul). Slurp each once so regions can be
        // read in parallel without touching ClassicUO's stateful readers.
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (ref readonly var im in index.AsSpan())
        {
            if (!im.IsValid()) continue;
            if (im.MapFile != null && !files.ContainsKey(im.MapFile.FilePath)) files[im.MapFile.FilePath] = File.ReadAllBytes(im.MapFile.FilePath);
            if (im.StaticFile != null && !files.ContainsKey(im.StaticFile.FilePath)) files[im.StaticFile.FilePath] = File.ReadAllBytes(im.StaticFile.FilePath);
        }

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
                    ref readonly var im = ref index[(bx0 + bx) * bh + (by0 + by)];
                    if (!im.IsValid() || im.MapFile == null) continue;
                    var mapBytes = files[im.MapFile.FilePath];
                    if (im.MapAddress + MapBlockBytes > (ulong)mapBytes.Length) continue;
                    var cells = mapBytes.AsSpan((int)im.MapAddress + 4, 64 * 3);
                    var land = new LandCell[PackConst.CellsPerBlock];
                    for (int c = 0; c < 64; c++)
                        land[c] = new LandCell { TileId = (ushort)((cells[c * 3] | (cells[c * 3 + 1] << 8)) & 0x3FFF), Z = (sbyte)cells[c * 3 + 2] };

                    tmp.Clear();
                    if (im.StaticFile != null && im.StaticAddress != 0 && im.StaticCount > 0)
                    {
                        var sb = files[im.StaticFile.FilePath];
                        long start = (long)im.StaticAddress, end = start + im.StaticCount * StaticRecBytes;
                        if (end <= sb.Length)
                        {
                            for (long p = start; p < end; p += StaticRecBytes)
                            {
                                ushort g = (ushort)(sb[p] | (sb[p + 1] << 8));
                                byte x = sb[p + 2], y = sb[p + 3];
                                // Same filter ClassicUO's Chunk.Load applies.
                                if (g == 0 || g == 0xFFFF || x > 7 || y > 7) continue;
                                tmp.Add(new StaticCell { Graphic = g, X = x, Y = y, Z = (sbyte)sb[p + 4], Hue = (ushort)(sb[p + 5] | (sb[p + 6] << 8)) });
                            }
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
