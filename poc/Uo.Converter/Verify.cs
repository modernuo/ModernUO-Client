using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ClassicUO.Assets;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>Reads the pack back with the Uo.Pack readers and checks it against itself and the source client.</summary>
public static class Verify
{
    private static int _errors;

    private static void Fail(string msg)
    {
        if (Interlocked.Increment(ref _errors) <= 30) Console.WriteLine("  FAIL " + msg);
    }

    public static bool Run(UOFileManager fm, string outDir, Dictionary<string, object> stats)
    {
        _errors = 0;
        var manifest = ManifestFileIo.Read(Path.Combine(outDir, "manifest.json"));
        Parallel.ForEach(manifest.Files, f =>
        {
            var p = Path.Combine(outDir, f.Path);
            if (!File.Exists(p)) { Fail($"missing {f.Path}"); return; }
            using var fs = File.OpenRead(p);
            if (fs.Length != f.Size) Fail($"size {f.Path}");
            if (Convert.ToHexStringLower(SHA256.HashData(fs)) != f.Sha256) Fail($"sha256 {f.Path}");
        });
        Console.WriteLine($"  manifest: {manifest.Files.Count} files, {manifest.Facets.Count} facets, hashes checked");

        // Tile data and hues.
        var (land, statics) = TileDataFile.Read(Path.Combine(outDir, "tiledata.bin"));
        if (land.Length != fm.TileData.LandData.Length || statics.Length != fm.TileData.StaticData.Length) Fail("tiledata counts");
        for (int i = 0; i < statics.Length; i++)
            if (statics[i].Flags != (ulong)fm.TileData.StaticData[i].Flags || statics[i].Height != fm.TileData.StaticData[i].Height) { Fail($"tiledata static {i}"); break; }
        var (hueCount, hues) = HueFile.Read(Path.Combine(outDir, "hues.bin"));
        if (hueCount != fm.Hues.HuesCount || hues.Any(h => (h >> 24) != 0xFF)) Fail("hues");
        Console.WriteLine($"  tiledata: {land.Length} land, {statics.Length} statics; hues: {hueCount}");

        CheckAtlas(fm, Path.Combine(outDir, "art"), "sprites.bin", tex: false, statics);
        CheckAtlas(fm, Path.Combine(outDir, "tex"), "texmaps.bin", tex: true, statics);

        // Map regions.
        foreach (var facet in manifest.Facets)
        {
            var dir = Path.Combine(outDir, "map", facet.Index.ToString());
            var fi = FacetFileIo.Read(dir);
            if (fi.BlocksX * 8 != fi.Width || fi.BlocksY * 8 != fi.Height) Fail($"facet {fi.Index} dims");
            int rxc = (fi.BlocksX + fi.RegionBlocks - 1) / fi.RegionBlocks, ryc = (fi.BlocksY + fi.RegionBlocks - 1) / fi.RegionBlocks;
            long blocks = 0, st = 0;
            Parallel.For(0, rxc * ryc, r =>
            {
                int rx = r % rxc, ry = r / rxc;
                var (h, b) = RegionFile.Read(PackConst.RegionPath(dir, rx, ry));
                if (h.Facet != fi.Index || h.Rx != rx || h.Ry != ry) Fail($"region header {fi.Index} {rx},{ry}");
                if (h.BlocksW != Math.Min(fi.RegionBlocks, fi.BlocksX - rx * fi.RegionBlocks) || h.BlocksH != Math.Min(fi.RegionBlocks, fi.BlocksY - ry * fi.RegionBlocks))
                    Fail($"region size {fi.Index} {rx},{ry}");
                long nb = 0, ns = 0;
                foreach (var blk in b)
                {
                    if (blk.IsEmpty) continue;
                    nb++;
                    ns += blk.Statics.Length;
                    foreach (var s in blk.Statics)
                        if (s.X > 7 || s.Y > 7 || s.Graphic >= statics.Length) { Fail($"static cell {fi.Index}"); break; }
                    foreach (var c in blk.Land)
                        if (c.TileId >= land.Length) { Fail($"land cell {fi.Index}"); break; }
                }
                Interlocked.Add(ref blocks, nb);
                Interlocked.Add(ref st, ns);
            });
            if (blocks != (long)fi.BlocksX * fi.BlocksY) Fail($"facet {fi.Index}: {blocks} blocks, expected {fi.BlocksX * fi.BlocksY}");
            if (stats.TryGetValue($"map{fi.Index}", out var o) && o is MapConvert.Stats ms && ms.Statics != st) Fail($"facet {fi.Index} statics {st} != {ms.Statics}");
            Console.WriteLine($"  facet {fi.Index}: {fi.Width}x{fi.Height}, {rxc * ryc} regions, {blocks} blocks, {st} statics");
        }

        Console.WriteLine(_errors == 0 ? "  all checks passed" : $"  {_errors} errors");
        return _errors == 0;
    }

    private static void CheckAtlas(UOFileManager fm, string dir, string table, bool tex, StaticTileData[] statics)
    {
        const int S = PackConst.PageSize, G = PackConst.Gutter;
        var entries = SpriteTable.Read(Path.Combine(dir, table));
        int pageCount = Directory.GetFiles(dir, "page_*.bin").Length;
        int present = 0;
        var byPage = new List<int>[pageCount];
        for (int p = 0; p < pageCount; p++) byPage[p] = new();
        var firstAtRect = new Dictionary<(int, int, int), int>();
        for (int id = 0; id < entries.Length; id++)
        {
            var e = entries[id];
            if (!e.Present) continue;
            present++;
            if (e.Page >= pageCount) { Fail($"{table} {id}: page {e.Page}"); continue; }
            if (e.W == 0 || e.H == 0 || e.X < G || e.Y < G || e.X + e.W + G > S || e.Y + e.H + G > S) Fail($"{table} {id}: rect");
            if (e.TrimX < 0 || e.TrimY < 0 || e.TrimX + e.W > e.OrigW || e.TrimY + e.H > e.OrigH) Fail($"{table} {id}: trim");
            bool isLand = (e.Flags & SpriteFlags.Land) != 0;
            if (!tex && isLand != (id < PackConst.StaticSpriteBase)) Fail($"{table} {id}: land flag");
            if (tex != ((e.Flags & SpriteFlags.Texmap) != 0)) Fail($"{table} {id}: tex flag");
            if (!tex && id >= PackConst.StaticSpriteBase)
            {
                bool ph = (statics[id - PackConst.StaticSpriteBase].Flags & (ulong)TileFlag.PartialHue) != 0;
                if (ph != ((e.Flags & SpriteFlags.PartialHue) != 0)) Fail($"{table} {id}: partial hue flag");
            }
            bool alias = !firstAtRect.TryAdd((e.Page, e.X, e.Y), id); // aliases are sampled too: verifies they really match
            if (alias || id % 23 == 0 || id == 0x4000 + 0x0E75 || id == 3) byPage[e.Page].Add(id);
        }

        int sampled = 0;
        using var workers = new ThreadLocal<WorkerReaders>(() => new WorkerReaders(fm), trackAllValues: true);
        var buf = new ThreadLocal<byte[]>(() => new byte[AtlasPage.ChainBytes(S, PackConst.PageMipCount)]);
        Parallel.For(0, pageCount, p =>
        {
            var h = AtlasPage.Read(PackConst.PagePath(dir, p), buf.Value!);
            if (h.Width != S || h.Height != S || h.MipCount != PackConst.PageMipCount || h.DataBytes != AtlasPage.ChainBytes(S, PackConst.PageMipCount))
                Fail($"{dir} page {p} header");
            var px = MemoryMarshal.Cast<byte, uint>(buf.Value.AsSpan(0, S * S * 4));
            foreach (var id in byPage[p])
            {
                var e = entries[id];
                uint[] src; int sw, sh;
                if (tex) { var t = workers.Value!.Fm.Texmaps.GetTexmap((uint)id); src = t.Pixels.ToArray(); sw = t.Width; sh = t.Height; }
                else { var a = workers.Value!.Fm.Arts.GetArt((uint)id); src = a.Pixels.ToArray(); sw = a.Width; sh = a.Height; }
                if (sw != e.OrigW || sh != e.OrigH) { Fail($"{dir} {id}: orig size {sw}x{sh} vs {e.OrigW}x{e.OrigH}"); continue; }
                for (int y = 0; y < sh; y++)
                    for (int x = 0; x < sw; x++)
                    {
                        uint s = src[y * sw + x];
                        bool inside = x >= e.TrimX && x < e.TrimX + e.W && y >= e.TrimY && y < e.TrimY + e.H;
                        if (!inside) { if ((s >> 24) != 0) { Fail($"{dir} {id}: opaque pixel trimmed"); goto next; } continue; }
                        if (px[(e.Y + y - e.TrimY) * S + e.X + x - e.TrimX] != s) { Fail($"{dir} {id}: pixel mismatch"); goto next; }
                    }
                // gutter = extruded edge
                for (int y = 0; y < e.H; y++)
                {
                    int row = (e.Y + y) * S;
                    if (px[row + e.X - 1] != px[row + e.X] || px[row + e.X - 2] != px[row + e.X] ||
                        px[row + e.X + e.W] != px[row + e.X + e.W - 1] || px[row + e.X + e.W + 1] != px[row + e.X + e.W - 1])
                    { Fail($"{dir} {id}: gutter"); goto next; }
                }
                for (int x = -G; x < e.W + G; x++)
                {
                    if (px[(e.Y - 1) * S + e.X + x] != px[e.Y * S + e.X + x] || px[(e.Y + e.H) * S + e.X + x] != px[(e.Y + e.H - 1) * S + e.X + x])
                    { Fail($"{dir} {id}: gutter v"); goto next; }
                }
                Interlocked.Increment(ref sampled);
            next:;
            }
        });
        foreach (var w in workers.Values) w.Dispose();
        Console.WriteLine($"  {Path.GetFileName(dir)}/{table}: {entries.Length} entries, {present} present, {pageCount} pages, {sampled} sprites pixel-verified against source");
    }
}
