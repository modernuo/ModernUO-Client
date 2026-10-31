using System.Diagnostics;
using System.Security.Cryptography;
using ClassicUO.Assets;
using ClassicUO.Utility;
using Uo.Converter;
using Uo.Pack;

string uoPath = @"C:\Ultima Online Classic";
string outDir = ""; // required: published packs (C:\Repositories\uo-packs\*) are immutable, so there is no default under them
string? debugDir = null;
var packsRoots = PublishedPacks.ConfiguredRoots().ToList();
bool useX = false, maps = true, convert = true, anims = true, gumps = true, fonts = true, cliloc = true, speech = true, formatGiven = false, gumpReport = false;
string? gumpDef = null;
HashSet<string>? only = null; // --only: convert just these stages into an existing pack, keeping everything else
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--uo": uoPath = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--debug": debugDir = args[++i]; break;
        case "--packs-root": packsRoots.Add(args[++i]); break;
        case "--xfiles": useX = true; break;
        case "--no-maps": maps = false; break;
        case "--verify-only": convert = false; break;
        case "--only": only = args[++i].Split(',').Select(s => s.Trim().ToLowerInvariant()).ToHashSet(); break;
        case "--anims-only": only = ["anims"]; break;
        case "--no-anims": anims = false; break;
        case "--no-gumps": gumps = false; break;
        case "--no-fonts": fonts = false; break;
        case "--no-cliloc": cliloc = false; break;
        case "--no-speech": speech = false; break;
        case "--raw-text": TextConvert.Raw = true; break;
        case "--text-small": TextConvert.SmallRaw = int.Parse(args[++i]); break;
        case "--cliloc-brotli": TextConvert.ClilocBrotli = true; break;
        case "--cliloc-skip": foreach (var s in args[++i].Split(',')) TextConvert.ClilocSkip.Add(s.Trim()); break;
        case "--no-cliloc-overrides": TextConvert.ClilocOverridesOff = true; break;
        case "--gump-def": gumpDef = args[++i]; break;
        case "--gump-report": gumpReport = true; break;
        case "--raw-gumps": GumpConvert.Raw = true; break;
        case "--gump-small": GumpConvert.SmallRle = int.Parse(args[++i]); break;
        case "--bodies":
            AnimConvert.Bodies = args[++i].Split(',').Select(s => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(s[2..], 16) : int.Parse(s)).ToHashSet();
            break;
        case "--format":
            formatGiven = true;
            ConvertOptions.Format = args[++i].ToLowerInvariant() switch
            {
                "rgba8" => Uo.Pack.PixelFormat.Rgba8,
                "bc7" => Uo.Pack.PixelFormat.Bc7,
                "b5g5r5a1" => Uo.Pack.PixelFormat.B5G5R5A1,
                var f => throw new ArgumentException($"unknown format {f}"),
            };
            break;
        case "--bc7-quality": ConvertOptions.Bc7Quality = Enum.Parse<BCnEncoder.Encoder.CompressionQuality>(args[++i], ignoreCase: true); break;
        case "--compress-regions": ConvertOptions.CompressRegions = true; break;
        case "--raw-regions": ConvertOptions.CompressRegions = false; break;
        case "--compress-pages": ConvertOptions.CompressPages = true; ConvertOptions.ChunkedPages = false; break;
        case "--compress-pages=chunked": ConvertOptions.ChunkedPages = true; break;
        case "--raw-pages": ConvertOptions.CompressPages = false; ConvertOptions.ChunkedPages = false; break;
        case "--chunk-bytes": ConvertOptions.ChunkBytes = int.Parse(args[++i]); break;
        case "--page-size": ConvertOptions.PageSize = PageSide(args[++i]); break;
        case "--anim-page-size": ConvertOptions.AnimPageSizeOverride = PageSide(args[++i]); break;
        case "--anim-format":
            ConvertOptions.AnimIndexed = args[++i].ToLowerInvariant() switch { "indexed8" => true, "page" => false, var f => throw new ArgumentException($"unknown anim format {f}") };
            break;
        case "--indexed-mips": ConvertOptions.IndexedMips = args[++i].ToLowerInvariant() != "none"; break;
        case "--bench-chunked": return AnimExperiments.BenchChunked(args[++i], args.Length > i + 1 && !args[i + 1].StartsWith("--") ? args[++i] : null);
        case "--anim-report": return AnimExperiments.Report(args[++i]);
        case "--compare-indexed": return AnimExperiments.CompareIndexed(args[++i], args[++i]);
        case "--filter-ref": return AnimExperiments.FilterRef(args[++i], args[++i], args[++i]);
        case "--bc7-refine-db": ConvertOptions.Bc7RefineDb = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--threads": ConvertOptions.Threads = Math.Max(1, int.Parse(args[++i])); break;
        case "--pages":
        {
            var pr = args[++i].Split('-');
            ConvertOptions.Pages = (int.Parse(pr[0]), int.Parse(pr.Length > 1 ? pr[1] : pr[0]));
            break;
        }
        case "--facets": ConvertOptions.Facets = args[++i].Split(',').Select(int.Parse).ToHashSet(); break;
        case "--regions": ConvertOptions.MaxRegions = int.Parse(args[++i]); break;
        case "--bench-bc7": Bench.Bc7(args[++i]); return 0;
        default:
            Console.WriteLine("usage: Uo.Converter [--uo <client dir>] --out <pack dir> [--debug <png dir>] [--packs-root <published packs root>] [--format b5g5r5a1|rgba8|bc7] [--bc7-quality fast|balanced|bestquality] [--bc7-refine-db 28] [--raw-regions] [--raw-pages|--compress-pages[=chunked]] [--chunk-bytes n] [--page-size 2048] [--anim-page-size 1024] [--anim-format indexed8|page] [--indexed-mips none|b16] [--threads n] [--pages a-b] [--facets 0,1] [--regions n] [--xfiles] [--no-maps] [--no-anims] [--no-gumps] [--no-fonts] [--no-cliloc] [--no-speech] [--raw-gumps] [--raw-text] [--text-small n] [--cliloc-brotli] [--cliloc-skip ext,ext] [--no-cliloc-overrides] [--gump-small n] [--gump-def <file>] [--gump-report] [--only art,tex,hues,tiledata,data,gumps,fonts,cliloc,speech,maps,anims] [--anims-only] [--bodies 400,0xC8] [--verify-only]");
            return 1;
    }
}

if (outDir.Length == 0)
{
    Console.Error.WriteLine("--out <pack dir> is required");
    return 1;
}
debugDir ??= Path.Combine(Path.GetTempPath(), "uo-converter-debug", Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir))));
try
{
    // Published packs are immutable: never convert into one (a folder under a packs root, also through a junction or
    // symlink). --verify-only only reads the pack, so it may verify a published one; debug PNGs still go elsewhere.
    if (convert) PublishedPacks.EnsureWritable(outDir, packsRoots);
    PublishedPacks.EnsureWritable(debugDir, packsRoots);
}
catch (InvalidOperationException e)
{
    Console.Error.WriteLine(e.Message);
    return 1;
}

bool Want(string stage) => only != null ? only.Contains(stage) : stage switch { "maps" => maps, "anims" => anims, "gumps" => gumps, "fonts" => fonts, "cliloc" => cliloc, "speech" => speech, _ => true };
if (only != null && !formatGiven && File.Exists(PackConst.PagePath(Path.Combine(outDir, "art"), 0)))
{
    // Match the pack being extended: page format and compression come from its first art page.
    var p0 = PackConst.PagePath(Path.Combine(outDir, "art"), 0);
    using (var ps = AtlasPage.OpenChain(p0, out var ph)) ConvertOptions.Format = ph.Format;
    using var hs = File.OpenRead(p0);
    var kind = StructIo.ReadHeaderFull(hs, PackConst.MagicPage).Kind;
    ConvertOptions.ChunkedPages = kind == FileKind.ChunkedBrotli;
    ConvertOptions.CompressPages = kind == FileKind.Brotli;
}

var total = Stopwatch.StartNew();
if (convert && only == null && Directory.Exists(debugDir)) Directory.Delete(debugDir, true);
Directory.CreateDirectory(debugDir);
ConvertOptions.DebugDir = debugDir;
Console.WriteLine($"format {ConvertOptions.Format} (bc7 quality {ConvertOptions.Bc7Quality}), compress regions {ConvertOptions.CompressRegions}, compress pages {(ConvertOptions.ChunkedPages ? $"chunked ({ConvertOptions.ChunkBytes} B)" : ConvertOptions.CompressPages.ToString())}, page size {ConvertOptions.PageSize} (anims {ConvertOptions.AnimPageSize}{(ConvertOptions.AnimIndexed ? ", indexed8" + (ConvertOptions.IndexedMips ? " + b16 mips" : ", no mips") : "")}), threads {ConvertOptions.Threads}" +
                  (ConvertOptions.PartialPages ? $", pages {ConvertOptions.Pages.Lo}-{ConvertOptions.Pages.Hi}" : "") +
                  (ConvertOptions.Facets != null ? $", facets {string.Join(",", ConvertOptions.Facets)}" : "") +
                  (ConvertOptions.MaxRegions != int.MaxValue ? $", first {ConvertOptions.MaxRegions} regions" : "") +
                  (only != null ? $", only {string.Join(",", only)}" : "") +
                  (AnimConvert.Bodies != null ? $", bodies {string.Join(",", AnimConvert.Bodies)}" : ""));
T Stage<T>(string name, Func<T> f)
{
    var sw = Stopwatch.StartNew();
    var r = f();
    Console.WriteLine($"[{sw.Elapsed.TotalSeconds,7:F2}s] {name}: {r}");
    return r;
}

// Client version: same order as ClassicUO.Client (settings.json value, else the client.exe VS_VERSION_INFO).
if (!ClientVersionHelper.TryParseFromFile(Path.Combine(uoPath, "client.exe"), out var versionText)
    || !ClientVersionHelper.IsClientVersionValid(versionText, out var clientVersion))
    throw new InvalidOperationException($"cannot determine client version from {uoPath}\\client.exe");
Console.WriteLine($"client {uoPath}  version {versionText} ({clientVersion})  out {outDir}");

var fm = Stage("load UOFileManager", () =>
{
    var m = ClientLoad.Open(clientVersion, uoPath);
    return m;
});
Console.WriteLine($"  uop={fm.IsUOPInstallation} artEntries={fm.Arts.File.Entries.Length} texEntries={fm.Texmaps.File.Entries.Length} " +
                  $"land={fm.TileData.LandData.Length} statics={fm.TileData.StaticData.Length} hues={fm.Hues.HuesCount}");

fm.Gumps.DefPath = gumpDef;
if (gumpReport) return GumpExperiments.Report(fm);

var stats = new Dictionary<string, object>();
if (convert)
{
    var oldManifest = Path.Combine(outDir, "manifest.json");
    var facets = only != null && !Want("maps") && File.Exists(oldManifest) ? ManifestFileIo.Read(oldManifest).Facets : new List<FacetInfo>();
    if (only == null && Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    Directory.CreateDirectory(outDir);

    if (Want("art")) stats["art"] = Stage("art", () => ArtConvert.Art(fm, outDir));
    if (Want("tex")) stats["tex"] = Stage("tex", () => ArtConvert.Tex(fm, outDir));
    if (Want("anims")) stats["anims"] = Stage("anims", () => AnimConvert.Run(fm, outDir));
    if (ConvertOptions.Format == Uo.Pack.PixelFormat.Bc7)
        Console.WriteLine($"  bc7: {PageCodec.RefineStats.Encoded} non-trivial blocks encoded, {PageCodec.RefineStats.Refined} re-encoded at Balanced (refine below {ConvertOptions.Bc7RefineDb} dB)");
    if (Want("tiledata"))
    {
        stats["tiledata"] = Stage("tiledata", () => ArtConvert.TileData(fm, outDir));
        Stage("tiledata (extended)", () => TableConvert.TileDataExt(fm, outDir));
    }
    if (Want("hues"))
    {
        stats["hues"] = Stage("hues (raw)", () => TableConvert.HuesRaw(fm, outDir));
        Stage("radarcol", () => TableConvert.RadarCol(fm, outDir));
    }
    if (Want("data"))
    {
        Stage("multis", () => TableConvert.Multis(fm, outDir));
        Stage("lights", () => TableConvert.Lights(fm, outDir));
        Stage("tileart", () => TableConvert.TileArt(fm, outDir));
        Stage("equipconv", () => TableConvert.EquipConv(fm, outDir));
    }
    if (Want("gumps")) stats["gumps"] = Stage("gumps", () => GumpConvert.Run(fm, outDir));
    if (Want("fonts")) stats["fonts"] = Stage("fonts", () => TextConvert.Fonts(fm, outDir));
    if (Want("cliloc")) stats["cliloc"] = Stage("cliloc", () => TextConvert.Cliloc(fm, outDir));
    if (Want("speech")) stats["speech"] = Stage("speech", () => TextConvert.Speech(fm, outDir));

    if (Want("maps"))
    {
        foreach (var f in MapConvert.AvailableFacets(fm))
        {
            var s = Stage($"map facet {f}", () => MapConvert.Facet(fm, f, outDir, useX));
            stats[$"map{f}"] = s;
            facets.Add(FacetFileIo.Read(Path.Combine(outDir, "map", f.ToString())));
        }
    }

    Stage("manifest", () =>
    {
        var files = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories)
            .Where(p => !p.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase) && !ReplacingFile.IsTempFile(p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var entries = new ManifestFile[files.Length];
        Parallel.For(0, files.Length, ConvertOptions.Par, i =>
        {
            using var fs = File.OpenRead(files[i]);
            entries[i] = new ManifestFile
            {
                Path = Path.GetRelativePath(outDir, files[i]).Replace('\\', '/'),
                Size = fs.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(fs)),
            };
        });
        var m = new Manifest
        {
            SourcePath = uoPath, SourceClientVersion = versionText, Created = DateTime.UtcNow, Facets = facets, Files = entries.ToList(),
        };
        ManifestFileIo.Write(Path.Combine(outDir, "manifest.json"), m);
        return $"{entries.Length} files, {entries.Sum(e => e.Size) / 1048576.0:F1} MiB";
    });
}

Verify.Only = only;
bool ok = Stage("verify (round-trip)", () => Verify.Run(fm, outDir, stats));
if (only == null || only.Contains("art")) Stage("debug PNGs", () => DebugOut.Run(fm, outDir, debugDir));
if (Directory.Exists(Path.Combine(outDir, "anim")) && (only == null || only.Contains("anims")))
    Stage("anim debug PNGs", () => AnimDebug.Run(outDir, Path.Combine(debugDir, "anim")));

Console.WriteLine($"[{total.Elapsed.TotalSeconds,7:F2}s] total — {(ok ? "OK" : "VERIFY FAILED")}");
return ok ? 0 : 2;

static int PageSide(string v)
{
    int s = int.Parse(v);
    if (s < 256 || s > 8192 || (s & (s - 1)) != 0) throw new ArgumentException($"page size {s}: power of two in 256..8192");
    return s;
}
