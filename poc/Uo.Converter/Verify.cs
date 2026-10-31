using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ClassicUO.Assets;
using ClassicUO.Utility;
using Uo.Pack;

namespace Uo.Converter;

/// <summary>Reads the pack back with the Uo.Pack readers and checks it against itself and the source client.</summary>
public static class Verify
{
    private static int _errors;
    /// <summary>--only: check just these stages (plus the manifest); null = everything.</summary>
    public static HashSet<string>? Only;
    private static bool Want(string stage) => Only == null || Only.Contains(stage);
    /// <summary>False under --no-maps: the pack is not expected to hold the source's facets.</summary>
    public static bool Maps = true;

    /// <summary>The first failure messages (up to 100), for tests.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Failures = new();

    public static void Reset()
    {
        _errors = 0;
        Failures.Clear();
    }

    public static void Fail(string msg)
    {
        int n = Interlocked.Increment(ref _errors);
        if (n <= 100) Failures.Enqueue(msg);
        if (n <= 30) Console.WriteLine("  FAIL " + msg);
    }

    /// <summary>
    /// Every gump slot of the client's index (gump.def and loose files applied) against the pack: present or not, size,
    /// and the RGBA8 image with the alias hue, exactly as ClassicUO's GumpsLoader.GetGump produces it.
    /// </summary>
    public static void CheckGumps(UOFileManager fm, string outDir)
    {
        var loader = fm.Gumps;
        if (loader.File == null) loader.Load();
        using var store = RecordStore.Open(Path.Combine(outDir, StoreConst.GumpsFile), StoreConst.MagicGumps);
        if (((store.Header.Flags & StoreConst.GumpFlagUop) != 0) != loader.UseUOPGumps) Fail("gumps: UOP flag");
        int slots = loader.File!.Entries.Length, present = 0, bad = 0, hued = 0;
        var raw = new byte[1 << 20];
        var mine = new uint[1 << 16];
        for (int id = 0; id < slots; id++)
        {
            var g = loader.GetGump((uint)id);
            bool has = store.TryGet(id, out var slot, out var rec);
            if (g.Pixels.IsEmpty != !has) { if (bad++ < 5) Fail($"gump {id}: present {!g.Pixels.IsEmpty} vs pack {has}"); continue; }
            if (!has) continue;
            present++;
            if (rec.Meta0 != g.Width || rec.Meta1 != g.Height) { if (bad++ < 5) Fail($"gump {id}: size {rec.Meta0}x{rec.Meta1} vs {g.Width}x{g.Height}"); continue; }
            if (raw.Length < rec.RawBytes) raw = new byte[rec.RawBytes];
            if (mine.Length < g.Width * g.Height) mine = new uint[g.Width * g.Height];
            store.Read(rec, raw);
            ReadOnlySpan<ushort> shades = default;
            if (slot.Arg != 0 && slot.Arg < fm.Hues.HuesCount)
            {
                int h = slot.Arg - 1;
                var t = new ushort[32];
                for (int k = 0; k < 32; k++) t[k] = fm.Hues.HuesRange[h >> 3].Entries[h & 7].ColorTable[k];
                shades = t;
                hued++;
            }
            var dst = mine.AsSpan(0, g.Width * g.Height);
            if (!GumpCodec.Decode(rec.Meta2, raw.AsSpan(0, (int)rec.RawBytes), g.Width, g.Height, dst, GumpExperiments.Lut, GumpExperiments.Expand, shades)) { if (bad++ < 5) Fail($"gump {id}: decode"); continue; }
            if (!dst.SequenceEqual(g.Pixels)) { if (bad++ < 5) Fail($"gump {id}: pixels differ"); }
        }
        if (bad > 5) Fail($"gumps: {bad} mismatching ids in total");
        Console.WriteLine($"  gumps: {slots} slots checked, {present} present ({hued} hued), {store.Records.Length} records: presence, size and RGBA pixels {(bad == 0 ? "EXACT" : "MISMATCH")} vs GumpsLoader.GetGump");
    }

    public static bool Run(UOFileManager fm, string outDir, Dictionary<string, object> stats)
    {
        Reset();
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

        if (Want("anims") && File.Exists(Path.Combine(outDir, "anim", AnimTableFile.FileName))) CheckAnim(fm, Path.Combine(outDir, "anim"));
        if (Want("gumps") && File.Exists(Path.Combine(outDir, StoreConst.GumpsFile))) CheckGumps(fm, outDir);
        if (Want("fonts") && File.Exists(Path.Combine(outDir, TextConst.AsciiFontsFile))) TextConvert.VerifyFonts(fm, outDir);
        if (Want("cliloc") && File.Exists(Path.Combine(outDir, TextConst.ClilocFile))) TextConvert.VerifyCliloc(fm, outDir);
        if (Want("speech") && File.Exists(Path.Combine(outDir, TextConst.SpeechFile))) TextConvert.VerifySpeech(fm, outDir);
        // hues_raw.bin is the only hue source (the client builds the LUT from it; hues.bin is retired), so a pack without
        // it cannot load. Checked before the tiledata early return so --only hues cannot pass on a pack that lacks it.
        if (Want("hues") && !File.Exists(Path.Combine(outDir, TableConst.HueRawFile))) Fail($"missing {TableConst.HueRawFile} (the pack's only hue source)");
        // tiledata.bin is the only tile data file since pack 1.1; the converter deletes the retired tiledata_ext.bin.
        if (Want("tiledata") && File.Exists(Path.Combine(outDir, TableConst.TileDataExtFileRetired))) Fail($"retired {TableConst.TileDataExtFileRetired} still in the pack");
        // ...and a pack without it cannot load, even when the manifest does not list it
        if (Want("tiledata") && !File.Exists(Path.Combine(outDir, TableConst.TileDataFile))) Fail($"missing {TableConst.TileDataFile} (the pack's only tile data)");
        // Maps are checked whether or not the pack has tile data: --only maps permits a pack without it, and returning
        // early would let a narrow or facet-missing pack pass on manifest hashes alone.
        if (File.Exists(Path.Combine(outDir, TableConst.TileDataFile))) CheckTileDataAndAtlases(fm, outDir);
        CheckMaps(fm, outDir, manifest, stats);

        if (Want("art") && Directory.Exists(Path.Combine(outDir, "art"))) PageTimings(Path.Combine(outDir, "art"));
        if (Want("anims") && Directory.Exists(Path.Combine(outDir, "anim"))) PageTimings(Path.Combine(outDir, "anim"));
        if (Want("maps") && manifest.Facets.Count > 0) RegionTimings(outDir, manifest);
        Console.WriteLine(_errors == 0 ? "  all checks passed" : $"  {_errors} errors");
        return _errors == 0;
    }

    /// <summary>Tile data and hues against the source, then the tables and the art/texmap atlases (which need the pack's tile data).</summary>
    private static void CheckTileDataAndAtlases(UOFileManager fm, string outDir)
    {
        // Tile data and hues.
        var (land, statics) = TileDataFile.ReadCompact(Path.Combine(outDir, TableConst.TileDataFile));
        if (land.Length != fm.TileData.LandData.Length || statics.Length != fm.TileData.StaticData.Length) Fail("tiledata counts");
        for (int i = 0; i < Math.Min(land.Length, fm.TileData.LandData.Length); i++)
            if (land[i].Flags != (ulong)fm.TileData.LandData[i].Flags || land[i].TexId != fm.TileData.LandData[i].TexID) { Fail($"tiledata land {i}"); break; }
        for (int i = 0; i < Math.Min(statics.Length, fm.TileData.StaticData.Length); i++)
        {
            ref readonly var t = ref fm.TileData.StaticData[i];
            if (statics[i].Flags != (ulong)t.Flags || statics[i].Height != t.Height || statics[i].Layer != t.Layer) { Fail($"tiledata static {i}"); break; }
        }
        string hueNote = "hues: not checked";
        if (Want("hues") && File.Exists(Path.Combine(outDir, TableConst.HueRawFile)))
        {
            // The LUT the client builds from hues_raw.bin (HueLut) against the expansion done here by hand from HuesLoader.
            var hues = HueLut.Build(HueRawFile.Read(Path.Combine(outDir, TableConst.HueRawFile)));
            int hueCount = hues.Length / HueLut.Shades;
            if (hueCount != fm.Hues.HuesCount) Fail("hues count");
            int lutBad = 0;
            for (int i = 0; i < Math.Min(hueCount, fm.Hues.HuesCount); i++)
                for (int k = 0; k < HueLut.Shades; k++)
                {
                    int c = fm.Hues.HuesRange[i >> 3].Entries[i & 7].ColorTable[k];
                    int r = (c >> 10) & 0x1F, g = (c >> 5) & 0x1F, b = c & 0x1F;
                    uint want = 0xFF000000u | (uint)((b << 3 | b >> 2) << 16) | (uint)((g << 3 | g >> 2) << 8) | (uint)(r << 3 | r >> 2);
                    if (hues[i * HueLut.Shades + k] != want && lutBad++ == 0) Fail($"hue LUT {i}:{k}");
                }
            hueNote = $"hues: {hueCount}, LUT built from hues_raw.bin = bit-replicated (c<<3|c>>2) hues.mul {(lutBad == 0 ? "EXACT" : "MISMATCH")}";
        }
        Console.WriteLine($"  tiledata: {land.Length} land, {statics.Length} statics (flags, texId, height, layer exact); {hueNote}");
        CheckNoDraw(fm, land, statics);
        CheckTables(fm, outDir);

        if (Want("art")) CheckAtlas(fm, Path.Combine(outDir, "art"), "sprites.bin", tex: false, statics);
        if (Want("tex")) CheckAtlas(fm, Path.Combine(outDir, "tex"), "texmaps.bin", tex: true, statics);
    }

    /// <summary>Map facets of the pack against the source files: the expected facets and sizes first (from the source
    /// alone), then every block of a MUL facet against <see cref="MulMapOracle"/>. Returns the number of failures.</summary>
    public static int CheckMaps(UOFileManager fm, string outDir, Manifest manifest, Dictionary<string, object> stats)
    {
        int before = _errors;
        // equal to the pack's (checked in Run); unloaded tile data (map-only checks in tests) skips the id range check
        int landCount = fm.TileData.LandData?.Length ?? int.MaxValue, staticCount = fm.TileData.StaticData?.Length ?? int.MaxValue;
        // Map regions. First the facets and sizes the source files call for (worked out without the pack), against the
        // manifest and each facet.json; blocks are compared only for a facet whose size matches.
        var expected = new Dictionary<int, SourceFacets.Expected>();
        if (Want("maps") && Maps)
        {
            foreach (var e in SourceFacets.For(fm, ConvertOptions.Facets))
            {
                expected[e.Facet] = e;
                if (e.Problem != null) Fail($"facet {e.Facet}: {e.Problem}");
                if (e.Note != null) Console.WriteLine($"  note: facet {e.Facet}: {e.Note}");
            }

            var inPack = manifest.Facets.Select(f => f.Index).ToHashSet();
            foreach (var e in expected.Values)
                if (!inPack.Contains(e.Facet)) Fail($"facet {e.Facet}: the source has {Path.GetFileName(e.MapPath)} but the pack has no facet {e.Facet}");
            foreach (var f in manifest.Facets)
                if (!expected.ContainsKey(f.Index) && (ConvertOptions.Facets == null || ConvertOptions.Facets.Contains(f.Index))) Fail($"facet {f.Index}: in the pack, but the source has no map for it");
            Console.WriteLine($"  facets expected from the source files: {string.Join(", ", expected.Values.Select(e => $"{e.Facet} {e.Width}x{e.Height} ({Path.GetFileName(e.MapPath)})"))}");
        }

        foreach (var facet in Want("maps") ? manifest.Facets : [])
        {
            // The manifest's index names the facet (and its directory): the expected size and the source the blocks are
            // compared with are chosen by it, never by facet.json, so a facet directory copied under another index (with its
            // own facet.json and region headers) cannot be checked against its original source and pass.
            int index = facet.Index;
            var dir = Path.Combine(outDir, "map", index.ToString());
            var fi = FacetFileIo.Read(dir);
            if (fi.Index != index) { Fail($"facet {index}: facet.json says facet {fi.Index}, the manifest and directory say {index}"); continue; }
            if (fi.BlocksX * 8 != fi.Width || fi.BlocksY * 8 != fi.Height) Fail($"facet {index} dims");
            if (fi.Width != facet.Width || fi.Height != facet.Height) Fail($"facet {index}: facet.json {fi.Width}x{fi.Height}, manifest {facet.Width}x{facet.Height}");
            bool sizeOk = true;
            if (expected.TryGetValue(index, out var want) && (want.Width != fi.Width || want.Height != fi.Height))
            {
                Fail($"facet {index}: pack {fi.Width}x{fi.Height}, the source {Path.GetFileName(want.MapPath)} holds {want.Width}x{want.Height}");
                sizeOk = false;
            }

            int rxc = (fi.BlocksX + fi.RegionBlocks - 1) / fi.RegionBlocks, ryc = (fi.BlocksY + fi.RegionBlocks - 1) / fi.RegionBlocks;
            long blocks = 0, st = 0, oracleChecked = 0, oracleBad = 0;
            var oracle = sizeOk && want != null && want.Problem == null ? MulMapOracle.TryOpen(fm, index, want.Width / 8, want.Height / 8) : null;
            int nreg = Math.Min(rxc * ryc, ConvertOptions.MaxRegions);
            bool partial = nreg != rxc * ryc;
            Parallel.For(0, nreg, ConvertOptions.Par, r =>
            {
                int rx = r % rxc, ry = r / rxc;
                var (h, b) = RegionFile.Read(PackConst.RegionPath(dir, rx, ry));
                if (h.Facet != index || h.Rx != rx || h.Ry != ry) Fail($"region header {index} {rx},{ry}");
                if (h.BlocksW != Math.Min(fi.RegionBlocks, fi.BlocksX - rx * fi.RegionBlocks) || h.BlocksH != Math.Min(fi.RegionBlocks, fi.BlocksY - ry * fi.RegionBlocks))
                    Fail($"region size {index} {rx},{ry}");
                long nb = 0, ns = 0;
                if (oracle != null)
                {
                    long bad = 0;
                    for (int by = 0; by < h.BlocksH; by++)
                        for (int bx = 0; bx < h.BlocksW; bx++)
                            if (!oracle.Matches((rx * fi.RegionBlocks + bx) * fi.BlocksY + ry * fi.RegionBlocks + by, in b[by * h.BlocksW + bx])) bad++;
                    Interlocked.Add(ref oracleChecked, h.BlocksW * h.BlocksH);
                    Interlocked.Add(ref oracleBad, bad);
                }
                foreach (var blk in b)
                {
                    if (blk.IsEmpty) continue;
                    nb++;
                    ns += blk.Statics.Length;
                    foreach (var s in blk.Statics)
                        if (s.X > 7 || s.Y > 7 || s.Graphic >= staticCount) { Fail($"static cell {index}"); break; }
                    foreach (var c in blk.Land)
                        if (c.TileId >= landCount) { Fail($"land cell {index}"); break; }
                }
                Interlocked.Add(ref blocks, nb);
                Interlocked.Add(ref st, ns);
            });
            if (!partial && blocks != (long)fi.BlocksX * fi.BlocksY) Fail($"facet {index}: {blocks} blocks, expected {fi.BlocksX * fi.BlocksY}");
            if (stats.TryGetValue($"map{index}", out var o) && o is MapConvert.Stats ms && ms.Statics != st) Fail($"facet {index} statics {st} != {ms.Statics}");
            Console.WriteLine($"  facet {index}: {fi.Width}x{fi.Height}, {nreg}/{rxc * ryc} regions, {blocks} blocks, {st} statics");
            if (oracle != null)
            {
                if (oracleBad != 0) Fail($"facet {index}: {oracleBad} of {oracleChecked} blocks differ from the MUL files + diffs");
                Console.WriteLine($"    vs independent read of {oracle.MapFile} (+ {oracle.LandPatches} land / {oracle.StaticPatches} static diff blocks as ModernUO applies them): {oracleChecked - oracleBad}/{oracleChecked} blocks EXACT");
            }
        }

        return _errors - before;
    }

    private static void CheckAtlas(UOFileManager fm, string dir, string table, bool tex, StaticTileData[] statics)
    {
        // Partial hue comes from tiledata (the sprite flag bit 2 that copied it is retired; packs before 1.1 still set it).
        bool Ph(int id) => !tex && id >= PackConst.StaticSpriteBase && id - PackConst.StaticSpriteBase < statics.Length
                           && (statics[id - PackConst.StaticSpriteBase].Flags & (ulong)TileFlag.PartialHue) != 0;
        int legacyPh = 0;
        const int G = PackConst.Gutter;
        int S = PageSide(dir);
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
            if ((e.Flags & SpriteFlags.RetiredPartialHue) != 0) legacyPh++;
            if ((e.Flags & ~(SpriteFlags.Land | SpriteFlags.Texmap | SpriteFlags.RetiredPartialHue)) != 0) Fail($"{table} {id}: unknown flags {e.Flags}");
            bool alias = !firstAtRect.TryAdd((e.Page, e.X, e.Y), id); // aliases are sampled too: verifies they really match
            // Sample: every 23rd id, all aliases, and every partial-hue sprite (for hue-fidelity metrics).
            if (alias || id % 23 == 0 || id == 0x4000 + 0x0E75 || id == 3 || Ph(id)) byPage[e.Page].Add(id);
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
            if (h.Width != S || h.Height != S || h.MipCount != ConvertOptions.MipsFor(S) || h.DataBytes != AtlasPage.ChainBytes(S, ConvertOptions.MipsFor(S), h.Format))
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
                    bool ph = Ph(id);
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
        if (legacyPh > 0) Console.WriteLine($"  {Path.GetFileName(dir)}/{table}: {legacyPh} entries carry the retired partial-hue bit (pack before 1.1; readers ignore it)");
    }

    /// <summary>Format 1.0 game data tables against ClassicUO's own loaders, every record and field.</summary>
    private static void CheckTables(UOFileManager fm, string outDir)
    {
        string P(string f) => Path.Combine(outDir, f);
        if (StructIo.SizeOf<LandTileInfo>() != TileDataFile.LandEntryBytes || StructIo.SizeOf<StaticTileInfo>() != TileDataFile.StaticEntryBytes || StructIo.SizeOf<HueEntry>() != 88 || StructIo.SizeOf<MultiComponent>() != 10
            || StructIo.SizeOf<LightEntry>() != 8 || StructIo.SizeOf<TileArtAppearance>() != 8 || StructIo.SizeOf<EquipConvEntry>() != 10)
            Fail("table struct sizes");

        if (Want("tiledata") && File.Exists(P(TableConst.TileDataFile)))
        {
            var (l, st, names) = TileDataFile.Read(P(TableConst.TileDataFile));
            var ld = fm.TileData.LandData;
            var sd = fm.TileData.StaticData;
            if (l.Length != ld.Length || st.Length != sd.Length) Fail("tiledata counts (full)");
            int bad = 0;
            for (int i = 0; i < Math.Min(l.Length, ld.Length); i++)
                if ((l[i].Flags != (ulong)ld[i].Flags || l[i].TexId != ld[i].TexID || TileDataFile.Name(names, l[i].NameOffset, l[i].NameLength) != (ld[i].Name ?? "")) && bad++ == 0)
                    Fail($"tiledata land {i}");
            for (int i = 0; i < Math.Min(st.Length, sd.Length); i++)
            {
                ref readonly var t = ref sd[i];
                var e = st[i];
                if ((e.Flags != (ulong)t.Flags || e.Weight != t.Weight || e.Layer != t.Layer || e.Count != t.Count || e.AnimId != t.AnimID || e.Hue != t.Hue
                     || e.LightIndex != t.LightIndex || e.Height != t.Height || TileDataFile.Name(names, e.NameOffset, e.NameLength) != (t.Name ?? "")) && bad++ == 0)
                    Fail($"tiledata static {i}");
            }
            Console.WriteLine($"  tiledata (full): {l.Length} land, {st.Length} statics, every field and name {(bad == 0 ? "EXACT" : $"{bad} MISMATCHES")} vs TileDataLoader; names {names.Length} B");
        }

        if (Want("hues") && File.Exists(P(TableConst.HueRawFile)))
        {
            var raw = HueRawFile.Read(P(TableConst.HueRawFile));
            var groups = fm.Hues.HuesRange;
            int bad = 0;
            if (raw.Length != groups.Length * 8) Fail("hues_raw count");
            for (int i = 0; i < Math.Min(raw.Length, groups.Length * 8); i++)
            {
                var src = groups[i >> 3].Entries[i & 7];
                var e = raw[i];
                bool same = e.TableStart == src.TableStart && e.TableEnd == src.TableEnd;
                for (int c = 0; c < 32; c++) same &= e.Colors[c] == src.ColorTable[c];
                unsafe { for (int c = 0; c < 20; c++) same &= e.Name[c] == src.Name[c]; }
                if (!same && bad++ == 0) Fail($"hues_raw {i}");
            }
            var radar = RadarColFile.Read(P(TableConst.RadarColFile));
            bool radarOk = radar.AsSpan().SequenceEqual(fm.Hues.RadarCol);
            if (!radarOk) Fail("radarcol");
            Console.WriteLine($"  hues_raw: {raw.Length} entries (colours, TableStart/End, names) {(bad == 0 ? "EXACT" : $"{bad} MISMATCHES")} vs HuesLoader; radarcol: {radar.Length} colours {(radarOk ? "EXACT" : "MISMATCH")}");
        }

        if (!Want("data") || !File.Exists(P(TableConst.MultisFile))) return;
        using var own = new UOFileManager(fm.Version, fm.BasePath);

        {
            var (multis, comps) = MultiFile.Read(P(TableConst.MultisFile));
            var entries = TableConvert.MultiEntries(own.Multis);
            int bad = 0, n = 0, maxId = -1;
            for (int id = 0; id < entries.Length; id++)
            {
                if (!TableConvert.HasMulti(entries, id))
                {
                    if (id < multis.Length && multis[id].ComponentCount != 0 && bad++ == 0) Fail($"multi {id}: present in pack only");
                    continue;
                }
                maxId = id;
                var src = own.Multis.GetMultis((uint)id);
                var pk = MultiFile.Components(multis, comps, id);
                bool same = pk.Length == src.Count;
                for (int k = 0; same && k < pk.Length; k++)
                    same = pk[k].Graphic == src[k].ID && pk[k].X == src[k].X && pk[k].Y == src[k].Y && pk[k].Z == src[k].Z
                           && ((pk[k].Flags & MultiComponentFlags.Visible) != 0) == src[k].IsVisible;
                if (!same && bad++ == 0) Fail($"multi {id}");
                n += src.Count;
            }
            if (multis.Length != maxId + 1) Fail($"multis: {multis.Length} ids, expected {maxId + 1}");
            Console.WriteLine($"  multis: {multis.Length} ids, {n} components {(bad == 0 ? "EXACT" : $"{bad} MISMATCHES")} vs MultiLoader.GetMultis");
        }

        {
            var (lights, data) = LightFile.Read(P(TableConst.LightsFile));
            own.Lights.Load();
            int bad = 0;
            if (lights.Length != own.Lights.File.Entries.Length) Fail("lights count");
            for (int i = 0; i < Math.Min(lights.Length, own.Lights.File.Entries.Length); i++)
            {
                var src = own.Lights.GetLight((uint)i);
                var e = lights[i];
                bool same = e.Width == src.Width && e.Height == src.Height;
                for (int k = 0; same && k < src.Pixels.Length; k++) same = LightFile.ToRgba8(data[e.Offset + k]) == src.Pixels[k];
                if (!same && bad++ == 0) Fail($"light {i}");
            }
            Console.WriteLine($"  lights: {lights.Length} ids, {data.Length} texels {(bad == 0 ? "EXACT" : $"{bad} MISMATCHES")} vs LightsLoader.GetLight");
        }

        if (File.Exists(fm.GetUOFilePath("tileart.uop")))
        {
            var pack = TileArtFile.Read(P(TableConst.TileArtFile));
            own.TileArt.Load();
            int bad = 0, items = 0, rows = 0;
            for (int g = 0; g <= ushort.MaxValue; g++)
            {
                if (!own.TileArt.TryGetTileArtInfo((uint)g, out var info)) continue;
                if (info.Appearances.Count <= 1 || !info.Appearances.TryGetValue(0, out var map) || map.Count == 0) continue;
                items++;
                rows += map.Count;
                foreach (var body in map.Keys)
                {
                    bool a = info.TryGetAppearance(body, out var want);
                    bool b = TileArtFile.TryGetAppearance(pack, (ushort)g, (ushort)body, out var got);
                    if ((a != b || want != got) && bad++ == 0) Fail($"tileart {g} body {body}");
                }
            }
            if (rows != pack.Length) Fail($"tileart: {pack.Length} rows, expected {rows}");
            Console.WriteLine($"  tileart: {items} items, {pack.Length} appearances {(bad == 0 && rows == pack.Length ? "EXACT" : "MISMATCH")} vs TileArtInfo.TryGetAppearance");
        }

        {
            var pack = EquipConvFile.Read(P(TableConst.EquipConvFile));
            own.Animations.Load();
            int bad = 0, rows = 0;
            foreach (var (body, dict) in own.Animations.EquipConversions)
                foreach (var (graphic, d) in dict)
                {
                    rows++;
                    if ((!EquipConvFile.TryGet(pack, body, graphic, out var e) || e.NewGraphic != d.Graphic || e.Gump != d.Gump || e.Color != d.Color) && bad++ == 0)
                        Fail($"equipconv body {body} graphic {graphic}");
                }
            if (rows != pack.Length) Fail($"equipconv: {pack.Length} rows, expected {rows}");
            Console.WriteLine($"  equipconv: {pack.Length} rows {(bad == 0 && rows == pack.Length ? "EXACT" : "MISMATCH")} vs AnimationsLoader.EquipConversions");
        }
    }

    /// <summary>animdata.bin against AnimDataLoader.CalculateCurrentGraphic for every graphic whose record is in the file.</summary>
    private static void CheckAnimData(UOFileManager fm, AnimDataEntry[] pack)
    {
        using var own = new UOFileManager(fm.Version, fm.BasePath);
        own.AnimData.Load();
        var file = own.AnimData.AnimDataFile;
        if (file == null) { if (pack.Length != 0) Fail("animdata without animdata.mul"); return; }
        var byGraphic = pack.ToDictionary(e => (int)e.Graphic);
        int bad = 0, n = 0;
        for (int g = 0; g <= ushort.MaxValue; g++)
        {
            if (g * 68 + 4 * ((g >> 3) + 1) + 68 > file.Length) { if (byGraphic.ContainsKey(g) && bad++ == 0) Fail($"animdata {g}: past the end of animdata.mul"); continue; }
            var src = own.AnimData.CalculateCurrentGraphic((ushort)g);
            bool has = byGraphic.TryGetValue(g, out var e);
            if (src.FrameCount == 0) { if (has && bad++ == 0) Fail($"animdata {g}: listed without frames"); continue; }
            n++;
            bool same = has && e.FrameCount == src.FrameCount && e.FrameInterval == src.FrameInterval && e.FrameStart == src.FrameStart && e.Unknown == src.Unknown;
            unsafe { for (int k = 0; same && k < 64; k++) same = e.Frames[k] == src.FrameData[k]; }
            if (!same && bad++ == 0) Fail($"animdata {g}");
        }
        if (n != pack.Length) Fail($"animdata: {pack.Length} entries, expected {n}");
        Console.WriteLine($"  animdata: {pack.Length} entries {(bad == 0 && n == pack.Length ? "EXACT" : "MISMATCH")} vs AnimDataLoader.CalculateCurrentGraphic");
    }

    /// <summary>
    /// The stored draw flags (1) equal the converter's rules (<see cref="TileRules"/>) and (2) give, for every land and
    /// static id and both player races, the same draw decision as the oracle: a verbatim copy of the client code they
    /// replaced (Land.AllowedToDraw, GameObject.CanBeDrawn, Item.RefreshGraphicState's multi test), evaluated on the
    /// source client's tiledata. The client's unit test TileDrawOracleTests repeats (2) against the pack alone.
    /// </summary>
    private static void CheckNoDraw(UOFileManager fm, LandTileData[] land, StaticTileData[] statics)
    {
        var sd = fm.TileData.StaticData;
        int landNo = 0, landName = 0, statNo = 0, statGarg = 0, statName = 0, statMulti = 0, compared = 0, mismatches = 0;
        for (int i = 0; i < land.Length; i++)
        {
            if (land[i].PackFlags != TileRules.Land(i, fm.TileData.LandData[i])) { Fail($"land {i} pack flags"); break; }
            if ((land[i].PackFlags & TilePackFlags.NoDraw) != 0) landNo++;
            if ((land[i].PackFlags & TilePackFlags.NameNoDraw) != 0) landName++;
        }
        for (int i = 0; i < land.Length; i++, compared++)
            if (TileDraw.Land(land[i].PackFlags) != OracleLand((ushort)i) && mismatches++ == 0) Fail($"land {i}: draw decision differs from the oracle");
        for (int i = 0; i < statics.Length; i++)
        {
            var f = statics[i].PackFlags;
            if (f != TileRules.Static(i, sd, fm.Version)) { Fail($"static {i} pack flags"); break; }
            if ((f & TilePackFlags.NoDraw) != 0) statNo++;
            if ((f & TilePackFlags.NoDrawUnlessGargoyle) != 0) statGarg++;
            if ((f & TilePackFlags.NameNoDraw) != 0) statName++;
            if ((f & TilePackFlags.NoDrawAsMulti) != 0) statMulti++;
        }
        for (int i = 0; i < statics.Length; i++)
        {
            var f = statics[i].PackFlags;
            foreach (bool garg in (ReadOnlySpan<bool>)[false, true])
            {
                compared++;
                if (TileDraw.Static(f, garg) != OracleCanBeDrawn(sd, (ushort)i, garg, fm.Version) && mismatches++ == 0)
                    Fail($"static {i} (gargoyle {garg}): draw decision differs from the oracle");
            }
            compared++;
            if (TileDraw.MultiItem(f) != OracleMultiItem((ushort)i) && mismatches++ == 0) Fail($"static {i}: multi item draw decision differs from the oracle");
        }
        if ((land[2].PackFlags & TilePackFlags.NoDraw) == 0 || (land[3].PackFlags & TilePackFlags.NoDraw) != 0) Fail("land 2/3 NoDraw");
        if ((statics[0x0001].PackFlags & TilePackFlags.NoDraw) == 0 || (statics[0x21A0].PackFlags & TilePackFlags.NoDraw) == 0) Fail("static 0x1/0x21A0 NoDraw");
        Console.WriteLine($"  nodraw: land {landNo} NoDraw, {landName} named nodraw; statics {statNo} NoDraw, {statGarg} NoDrawUnlessGargoyle, " +
                          $"{statName} named nodraw, {statMulti} NoDrawAsMulti; oracle: {compared} decisions, {mismatches} mismatches");
    }

    // ---- The oracle: the client code the flags replaced, copied verbatim (only the data access is adapted). Keep it
    // unchanged: it defines "pixel-neutral" for the flags. The rules themselves live in TileRules.

    private static bool OracleLand(ushort graphic) => graphic > 2; // Land.Create / UpdateGraphicBySeason

    private static bool OracleMultiItem(ushort multiGraphic) => multiGraphic > 2; // Item.RefreshGraphicState

    private static bool OracleCanBeDrawn(StaticTiles[] staticData, ushort g, bool playerIsGargoyle, ClientVersion version)
    {
        switch (g)
        {
            case 0x0001:
            case 0x21BC:
            case 0xA1FE:
            case 0xA1FF:
            case 0xA200:
            case 0xA201:
                return false;

            case 0x9E4C:
            case 0x9E64:
            case 0x9E65:
            case 0x9E7D:
                ref var data = ref staticData[g];

                return !data.IsBackground && !data.IsSurface;
        }

        if (g != 0x63D3)
        {
            if (g >= 0x2198 && g <= 0x21A4)
            {
                return false;
            }

            if (g == 0x0F65 && version < ClientVersion.CV_60144)
            {
                return true;
            }

            if (g < staticData.Length)
            {
                ref var data = ref staticData[g];

                if (!string.IsNullOrEmpty(data.Name) && data.Name.StartsWith("nodraw", StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!data.IsNoDiagonal || data.IsAnimated && playerIsGargoyle)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Animation table structure, then a sample of stored directions decoded again with a fresh ClassicUO loader and
    /// compared frame by frame: count, size, center, and pixels (exact for RGBA8/B5G5R5A1, PSNR for BC7).
    /// </summary>
    private static void CheckAnim(UOFileManager fm, string dir)
    {
        int S = PageSide(dir);
        if (StructIo.SizeOf<AnimBodyEntry>() != 24 || StructIo.SizeOf<AnimDirEntry>() != 8 || StructIo.SizeOf<AnimFrameEntry>() != 24 || StructIo.SizeOf<AnimDataEntry>() != 70)
            Fail("anim struct sizes");
        var (bodies, dirs, frames) = AnimTableFile.Read(Path.Combine(dir, AnimTableFile.FileName));
        var animData = AnimDataFile.Read(Path.Combine(dir, AnimDataFile.FileName));
        CheckAnimData(fm, animData);
        int pageCount = 0;
        foreach (var f in frames) if (f.Sprite.Present) pageCount = Math.Max(pageCount, f.Sprite.Page + 1);
        if (Directory.GetFiles(dir, "page_*.bin").Length != pageCount) Fail($"anim page files != {pageCount}");
        foreach (var f in frames)
        {
            var e = f.Sprite;
            if (!e.Present) continue;
            if (e.W == 0 || e.H == 0 || e.X < PackConst.Gutter || e.Y < PackConst.Gutter || e.X + e.W + PackConst.Gutter > S || e.Y + e.H + PackConst.Gutter > S) { Fail("anim frame rect"); break; }
            if (e.TrimX < 0 || e.TrimY < 0 || e.TrimX + e.W > e.OrigW || e.TrimY + e.H > e.OrigH) { Fail("anim frame trim"); break; }
        }

        // Structure, and the sample: about 1 in 9 distinct frame ranges, plus every range of the debug bodies.
        var debugBodies = new HashSet<int>(AnimDebug.Bodies);
        var samples = new List<(int Body, int Action, int Dir, AnimDirEntry E)>();
        var sampledRanges = new HashSet<(uint, ushort)>();
        int withFrames = 0;
        for (int b = 0; b < bodies.Length; b++)
        {
            var be = bodies[b];
            if (be.AliasBody != AnimConst.NoBody && be.AliasBody >= bodies.Length) Fail($"body {b}: alias {be.AliasBody} out of table");
            if (be.CorpseBody != AnimConst.NoBody && be.CorpseBody >= bodies.Length) Fail($"body {b}: corpse {be.CorpseBody} out of table");
            if (!be.HasFrames) continue;
            withFrames++;
            if (be.FirstDir + be.ActionCount * AnimConst.Directions > dirs.Length) { Fail($"body {b}: dir range"); continue; }
            for (int a = 0; a < be.ActionCount; a++)
                for (int d = 0; d < AnimConst.Directions; d++)
                {
                    var de = dirs[be.FirstDir + a * AnimConst.Directions + d];
                    var (sd, mir) = AnimConst.MapDirection(d);
                    if (de.StoredDir != sd || ((de.Flags & AnimDirFlags.Mirrored) != 0) != mir) Fail($"body {b} action {a} dir {d}: direction mapping");
                    var st = dirs[be.FirstDir + a * AnimConst.Directions + sd + 3]; // directions 3..7 are stored 0..4, unmirrored
                    if (st.FirstFrame != de.FirstFrame || st.FrameCount != de.FrameCount) Fail($"body {b} action {a} dir {d}: mirror target");
                    if (de.FirstFrame + de.FrameCount > frames.Length) { Fail($"body {b}: frame range"); continue; }
                    if (mir) continue;
                    bool pick = debugBodies.Contains(b) || (uint)HashCode.Combine(de.FirstFrame, de.FrameCount) % 9 == 0;
                    if (pick && sampledRanges.Add((de.FirstFrame, de.FrameCount))) samples.Add((b, a, sd, de));
                }
        }
        // Each sampled direction is checked with the page of its first present frame loaded; frames on other pages get size/center checks.
        var groups = samples.GroupBy(s =>
        {
            for (int i = 0; i < s.E.FrameCount; i++) if (frames[s.E.FirstFrame + i].Sprite.Present) return (int)frames[s.E.FirstFrame + i].Sprite.Page;
            return -1;
        }).ToArray();

        var conv = AnimConvert.ReadBodyConv(fm);
        long checkedFrames = 0, exact = 0, crossPage = 0, absent = 0;
        double se = 0; long nRgb = 0, flips = 0, nPx = 0;
        PixelFormat fmt = PixelFormat.Rgba8;
        var gate = new object();
        using var workers = new ThreadLocal<(AnimWorker W, Dictionary<int, AnimConvert.BodyMeta> M)>(() => (new AnimWorker(fm), new()), trackAllValues: true);
        // Serial: ClassicUO's ReadMULAnimationFrames is not GC-safe under concurrent allocation (see AnimConvert.ReadMul).
        Parallel.ForEach(groups, new ParallelOptions { MaxDegreeOfParallelism = 1 }, g =>
        {
            var (w, metas) = workers.Value;
            uint[]? px = null;
            if (g.Key >= 0) { px = PageCodec.ReadMip(PackConst.PagePath(dir, g.Key), 0, out var h); fmt = h.Format; }
            long ck = 0, ex = 0, cp = 0, ab = 0, fl = 0, np = 0, nr = 0; double sse = 0;
            foreach (var (b, a, sd, de) in g)
            {
                if (!metas.TryGetValue(b, out var m)) metas[b] = m = AnimConvert.Meta(w.Loader, fm.Version, b, conv);
                var job = AnimConvert.Job(w.Loader, m, a, sd);
                var src = job == null ? default : AnimConvert.Read(w.Loader, job);
                if (src.Length != de.FrameCount) { Fail($"anim body {b} action {a} dir {sd}: {de.FrameCount} frames, source {src.Length}"); continue; }
                for (int i = 0; i < src.Length; i++)
                {
                    ref var sf = ref src[i];
                    var fe = frames[de.FirstFrame + i];
                    var e = fe.Sprite;
                    ck++;
                    if (fe.CenterX != sf.CenterX || fe.CenterY != sf.CenterY || e.OrigW != Math.Max((short)0, sf.Width) || e.OrigH != Math.Max((short)0, sf.Height))
                    { Fail($"anim body {b} action {a} dir {sd} frame {i}: center/size"); continue; }
                    int sw = sf.Width, sh = sf.Height;
                    var spx = sw > 0 && sh > 0 && sf.Pixels != null ? sf.Pixels.AsSpan(0, sw * sh) : default;
                    if (!e.Present)
                    {
                        foreach (var v in spx) if ((v >> 24) != 0) { Fail($"anim body {b} action {a} dir {sd} frame {i}: visible frame marked absent"); break; }
                        ab++;
                        continue;
                    }
                    if (e.Page != g.Key) { cp++; continue; }
                    bool same = true;
                    for (int y = 0; y < sh; y++)
                        for (int x = 0; x < sw; x++)
                        {
                            uint s = spx[y * sw + x];
                            bool inside = x >= e.TrimX && x < e.TrimX + e.W && y >= e.TrimY && y < e.TrimY + e.H;
                            if (!inside) { if ((s >> 24) != 0) same = false; continue; }
                            uint p = px![(e.Y + y - e.TrimY) * S + e.X + x - e.TrimX];
                            if (fmt != PixelFormat.Bc7) { if (p != s) same = false; continue; }
                            np++;
                            if (((s >> 24) >= 128) != ((p >> 24) >= 128)) fl++;
                            if ((s >> 24) == 0) continue;
                            for (int k = 0; k < 24; k += 8) { int dd = (int)((s >> k) & 0xFF) - (int)((p >> k) & 0xFF); sse += dd * dd; }
                            nr += 3;
                        }
                    if (!same) { Fail($"anim body {b} action {a} dir {sd} frame {i}: pixel mismatch ({fmt})"); continue; }
                    if (fmt != PixelFormat.Bc7) ex++;
                }
            }
            lock (gate) { checkedFrames += ck; exact += ex; crossPage += cp; absent += ab; flips += fl; nPx += np; nRgb += nr; se += sse; }
        });
        foreach (var w in workers.Values) w.W.Dispose();
        int aliases = bodies.Count(x => x.AliasBody != AnimConst.NoBody), corpses = bodies.Count(x => x.CorpseBody != AnimConst.NoBody);
        Console.WriteLine($"  anim: {bodies.Length} bodies ({withFrames} with frames, {aliases} Body.def, {corpses} Corpse.def), {dirs.Length} dir records, {frames.Length} frames, {pageCount} pages, {animData.Length} animdata entries");
        if (fmt == PixelFormat.Bc7)
            Console.WriteLine($"  anim sample vs ClassicUO loader: {samples.Count} directions, {checkedFrames} frames ({absent} absent, {crossPage} on another page: size/center only); " +
                              $"BC7 PSNR rgb {Psnr(se, nRgb):F2} dB, alpha-test flips {flips}/{nPx} ({100.0 * flips / Math.Max(1, nPx):F3}%)");
        else
            Console.WriteLine($"  anim sample vs ClassicUO loader: {samples.Count} directions, {checkedFrames} frames: {exact} BIT-EXACT ({fmt}), {absent} absent, {crossPage} on another page (size/center only)");
    }

    /// <summary>Side of the pack's pages in <paramref name="dir"/> (from page 0's header), else the default.</summary>
    public static int PageSide(string dir)
    {
        var p0 = PackConst.PagePath(dir, 0);
        if (!File.Exists(p0)) return PackConst.PageSize;
        using var s = AtlasPage.OpenChain(p0, out var h);
        return h.Width;
    }

    private static double Psnr(double se, long n) => n == 0 || se == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / (se / n));

    /// <summary>Single-thread per-page load timing: open, Brotli-decompress if Kind 1, read the full chain into a buffer.</summary>
    public static void PageTimings(string dir)
    {
        var files = Directory.GetFiles(dir, "page_*.bin");
        if (files.Length == 0) return;
        var buf = new byte[AtlasPage.ChainBytes(Math.Max(PackConst.PageSize, PageSide(dir)), PackConst.PageMipCount)];
        var t = new List<double>(); long disk = 0, raw = 0; int compressed = 0;
        var sw = new System.Diagnostics.Stopwatch();
        foreach (var f in files)
        {
            sw.Restart();
            var h = AtlasPage.Read(f, buf);
            t.Add(sw.Elapsed.TotalMilliseconds);
            disk += new FileInfo(f).Length; raw += h.DataBytes + 32;
            using var fs = File.OpenRead(f);
            if (StructIo.ReadHeaderFull(fs, PackConst.MagicPage).Kind != FileKind.Raw) compressed++;
        }
        t.Sort();
        Console.WriteLine($"  {Path.GetFileName(dir)} pages: {files.Length} ({compressed} compressed), {disk / 1048576.0:F1} MiB on disk, {raw / 1048576.0:F1} MiB raw; " +
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
