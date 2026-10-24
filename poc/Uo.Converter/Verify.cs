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
        Parallel.ForEach(manifest.Files, ConvertOptions.Par, f =>
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
            int nreg = Math.Min(rxc * ryc, ConvertOptions.MaxRegions);
            bool partial = nreg != rxc * ryc;
            Parallel.For(0, nreg, ConvertOptions.Par, r =>
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
            if (!partial && blocks != (long)fi.BlocksX * fi.BlocksY) Fail($"facet {fi.Index}: {blocks} blocks, expected {fi.BlocksX * fi.BlocksY}");
            if (stats.TryGetValue($"map{fi.Index}", out var o) && o is MapConvert.Stats ms && ms.Statics != st) Fail($"facet {fi.Index} statics {st} != {ms.Statics}");
            Console.WriteLine($"  facet {fi.Index}: {fi.Width}x{fi.Height}, {nreg}/{rxc * ryc} regions, {blocks} blocks, {st} statics");
        }

        PageTimings(Path.Combine(outDir, "art"));
        RegionTimings(outDir, manifest);
        Console.WriteLine(_errors == 0 ? "  all checks passed" : $"  {_errors} errors");
        return _errors == 0;
    }

    private static void CheckAtlas(UOFileManager fm, string dir, string table, bool tex, StaticTileData[] statics)
    {
        const int S = PackConst.PageSize, G = PackConst.Gutter;
        var entries = SpriteTable.Read(Path.Combine(dir, table));
        int pageCount = 0;
        foreach (var en in entries) if (en.Present) pageCount = Math.Max(pageCount, en.Page + 1);
        if (!ConvertOptions.PartialPages && Directory.GetFiles(dir, "page_*.bin").Length != pageCount) Fail($"{dir}: page file count");
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
            // Sample: every 23rd id, all aliases, and every partial-hue sprite (for hue-fidelity metrics).
            if (alias || id % 23 == 0 || id == 0x4000 + 0x0E75 || id == 3 || (e.Flags & SpriteFlags.PartialHue) != 0) byPage[e.Page].Add(id);
        }

        int sampled = 0;
        PixelFormat fmt = PixelFormat.Rgba8;
        // BC7 quality accumulators: squared error on RGB where source alpha > 0, on alpha everywhere in the sprite rect.
        double seRgb = 0, seA = 0; long nRgb = 0, nA = 0, cutoutFlips = 0; double minSpritePsnr = double.MaxValue; int minSpriteId = -1;
        // Lossy-format fidelity: max per-channel error (visible texels), alpha-test flips, partial-hue gray preservation.
        int maxErrRgb = 0, maxErrA = 0; long opaque = 0, opaqueLost = 0, clear = 0, clearGained = 0;
        long phSprites = 0, phGray = 0, phGrayBroken = 0, phColor = 0, phColorToGray = 0;
        var gate = new object();
        using var workers = new ThreadLocal<WorkerReaders>(() => new WorkerReaders(fm), trackAllValues: true);
        Parallel.For(0, pageCount, ConvertOptions.Par, p =>
        {
            if (!ConvertOptions.PageSelected(p)) return;
            var px = PageCodec.ReadMip(PackConst.PagePath(dir, p), 0, out var h);
            fmt = h.Format;
            if (h.Width != S || h.Height != S || h.MipCount != PackConst.PageMipCount || h.DataBytes != AtlasPage.ChainBytes(S, PackConst.PageMipCount, h.Format))
                Fail($"{dir} page {p} header");
            foreach (var id in byPage[p])
            {
                var e = entries[id];
                uint[] src; int sw, sh;
                if (tex) { var t = workers.Value!.Fm.Texmaps.GetTexmap((uint)id); src = t.Pixels.ToArray(); sw = t.Width; sh = t.Height; }
                else { var a = workers.Value!.Fm.Arts.GetArt((uint)id); src = a.Pixels.ToArray(); sw = a.Width; sh = a.Height; }
                if (sw != e.OrigW || sh != e.OrigH) { Fail($"{dir} {id}: orig size {sw}x{sh} vs {e.OrigW}x{e.OrigH}"); continue; }
                if (h.Format == PixelFormat.Bc7)
                {
                    if ((e.X - G) % 4 != 0 || (e.Y - G) % 4 != 0) Fail($"{dir} {id}: cell not 4-aligned");
                    double sr = 0, sa = 0; long nr = 0, na = 0, flips = 0;
                    int mRgb = 0, mA = 0; long op = 0, opL = 0, cl = 0, clG = 0, gray = 0, grayB = 0, col = 0, colG = 0;
                    bool ph = (e.Flags & SpriteFlags.PartialHue) != 0;
                    for (int y = 0; y < e.H; y++)
                        for (int x = 0; x < e.W; x++)
                        {
                            uint a = src[(e.TrimY + y) * sw + e.TrimX + x], b = px[(e.Y + y) * S + e.X + x];
                            int da = (int)(a >> 24) - (int)(b >> 24);
                            sa += da * da; na++;
                            mA = Math.Max(mA, Math.Abs(da));
                            if (((a >> 24) >= 128) != ((b >> 24) >= 128)) flips++;
                            if ((a >> 24) == 0) { cl++; if ((b >> 24) >= 128) clG++; continue; }
                            op++; if ((b >> 24) < 128) opL++;
                            for (int sh2 = 0; sh2 < 24; sh2 += 8) { int d = (int)((a >> sh2) & 0xFF) - (int)((b >> sh2) & 0xFF); sr += d * d; mRgb = Math.Max(mRgb, Math.Abs(d)); }
                            nr += 3;
                            if (ph)
                            {
                                bool ga = (a & 0xFF) == ((a >> 8) & 0xFF) && (a & 0xFF) == ((a >> 16) & 0xFF);
                                bool gb = (b & 0xFF) == ((b >> 8) & 0xFF) && (b & 0xFF) == ((b >> 16) & 0xFF);
                                if (ga) { gray++; if (!gb) grayB++; } else { col++; if (gb) colG++; }
                            }
                        }
                    lock (gate)
                    {
                        maxErrRgb = Math.Max(maxErrRgb, mRgb); maxErrA = Math.Max(maxErrA, mA);
                        opaque += op; opaqueLost += opL; clear += cl; clearGained += clG;
                        if (ph) { phSprites++; phGray += gray; phGrayBroken += grayB; phColor += col; phColorToGray += colG; }
                        seRgb += sr; nRgb += nr; seA += sa; nA += na; cutoutFlips += flips;
                        double ps = Psnr(sr, nr);
                        if (ps < minSpritePsnr) { minSpritePsnr = ps; minSpriteId = id; }
                    }
                    Interlocked.Increment(ref sampled);
                    continue;
                }
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
        if (fmt == PixelFormat.Bc7)
            Console.WriteLine($"  {Path.GetFileName(dir)}/{table}: {entries.Length} entries, {present} present, {pageCount} BC7 pages, {sampled} sprites vs source: " +
                              $"PSNR rgb {Psnr(seRgb, nRgb):F2} dB, alpha {Psnr(seA, nA):F2} dB, worst sprite 0x{minSpriteId:X} {minSpritePsnr:F2} dB, " +
                              $"alpha-cutout flips {cutoutFlips}/{nA} px ({100.0 * cutoutFlips / Math.Max(1, nA):F3}%)\n" +
                              $"    max abs error: rgb {maxErrRgb}, alpha {maxErrA}; alpha-test (0.5) flips: opaque->clear {opaqueLost}/{opaque} ({100.0 * opaqueLost / Math.Max(1, opaque):F4}%), " +
                              $"clear->opaque {clearGained}/{clear} ({100.0 * clearGained / Math.Max(1, clear):F4}%)\n" +
                              $"    partial-hue ({phSprites} sprites): gray px no longer gray {phGrayBroken}/{phGray} ({100.0 * phGrayBroken / Math.Max(1, phGray):F2}%), " +
                              $"coloured px turned gray {phColorToGray}/{phColor} ({100.0 * phColorToGray / Math.Max(1, phColor):F2}%)");
        else if (fmt == PixelFormat.B5G5R5A1)
            Console.WriteLine($"  {Path.GetFileName(dir)}/{table}: {entries.Length} entries, {present} present, {pageCount} B5G5R5A1 pages, {sampled} sprites BIT-EXACT vs source 16-bit colour (incl. gutters)");
        else
            Console.WriteLine($"  {Path.GetFileName(dir)}/{table}: {entries.Length} entries, {present} present, {pageCount} pages, {sampled} sprites pixel-verified against source");
    }

    private static double Psnr(double se, long n) => n == 0 || se == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (se / n));

    /// <summary>Single-thread per-page load timing: open, Brotli-decompress if Kind 1, read the full chain into a buffer.</summary>
    public static void PageTimings(string dir)
    {
        var files = Directory.GetFiles(dir, "page_*.bin");
        if (files.Length == 0) return;
        var buf = new byte[AtlasPage.ChainBytes(PackConst.PageSize, PackConst.PageMipCount)];
        var t = new List<double>(); long disk = 0, raw = 0; int compressed = 0;
        var sw = new System.Diagnostics.Stopwatch();
        foreach (var f in files)
        {
            sw.Restart();
            var h = AtlasPage.Read(f, buf);
            t.Add(sw.Elapsed.TotalMilliseconds);
            disk += new FileInfo(f).Length; raw += h.DataBytes + 32;
            using var fs = File.OpenRead(f);
            if (StructIo.ReadHeaderFull(fs, PackConst.MagicPage).Kind == FileKind.Brotli) compressed++;
        }
        t.Sort();
        Console.WriteLine($"  {Path.GetFileName(dir)} pages: {files.Length} ({compressed} Brotli), {disk / 1048576.0:F1} MiB on disk, {raw / 1048576.0:F1} MiB raw; " +
                          $"single-thread load (read+decompress) median {t[t.Count / 2]:F1} ms, max {t[^1]:F1} ms");
    }

    /// <summary>Serial per-region timing (warm cache): read file, Brotli-decompress (Kind 1), parse blocks.</summary>
    public static void RegionTimings(string outDir, Manifest manifest)
    {
        var read = new List<double>(); var dec = new List<double>(); var parse = new List<double>(); var fileSizes = new List<long>(); var rawSizes = new List<long>();
        var sw = new System.Diagnostics.Stopwatch();
        foreach (var facet in manifest.Facets)
        {
            var dir = Path.Combine(outDir, "map", facet.Index.ToString());
            foreach (var path in Directory.GetFiles(dir, "r_*.bin"))
            {
                sw.Restart(); var bytes = File.ReadAllBytes(path); read.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart(); var raw = RegionFile.Decompress(bytes); dec.Add(sw.Elapsed.TotalMilliseconds);
                sw.Restart(); RegionFile.Parse(raw); parse.Add(sw.Elapsed.TotalMilliseconds);
                fileSizes.Add(bytes.Length); rawSizes.Add(raw.Length);
            }
        }
        static double Med(List<double> l) { l.Sort(); return l[l.Count / 2]; }
        static long MedL(List<long> l) { l.Sort(); return l[l.Count / 2]; }
        Console.WriteLine($"  regions: {read.Count} files, {fileSizes.Sum() / 1048576.0:F1} MiB on disk ({rawSizes.Sum() / 1048576.0:F1} MiB raw); " +
                          $"median file {MedL(fileSizes) / 1024.0:F0} KiB (raw {MedL(rawSizes) / 1024.0:F0} KiB); " +
                          $"median read {Med(read):F2} ms, decompress {Med(dec):F2} ms, parse {Med(parse):F2} ms; max decompress {dec.Max():F2} ms");
    }
}
