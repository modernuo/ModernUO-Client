using System.Diagnostics;
using System.Security.Cryptography;
using ClassicUO.Assets;
using ClassicUO.Utility;
using Uo.Converter;
using Uo.Pack;

string uoPath = @"C:\Ultima Online Classic";
string outDir = @"C:\Repositories\uo-packs\classic";
string debugDir = @"C:\Repositories\uo-packs\debug";
bool useX = false, maps = true, convert = true;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--uo": uoPath = args[++i]; break;
        case "--out": outDir = args[++i]; break;
        case "--debug": debugDir = args[++i]; break;
        case "--xfiles": useX = true; break;
        case "--no-maps": maps = false; break;
        case "--verify-only": convert = false; break;
        default:
            Console.WriteLine("usage: Uo.Converter [--uo <client dir>] [--out <pack dir>] [--debug <png dir>] [--xfiles] [--no-maps] [--verify-only]");
            return 1;
    }
}

var total = Stopwatch.StartNew();
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
    if (ClientLoad.HasVerdataPatches(m)) Console.WriteLine("  WARNING: verdata.mul has patches; they are NOT applied by the converter");
    return m;
});
Console.WriteLine($"  uop={fm.IsUOPInstallation} artEntries={fm.Arts.File.Entries.Length} texEntries={fm.Texmaps.File.Entries.Length} " +
                  $"land={fm.TileData.LandData.Length} statics={fm.TileData.StaticData.Length} hues={fm.Hues.HuesCount} verdata={(fm.Verdata.File?.Length ?? 0)}");

var stats = new Dictionary<string, object>();
if (convert)
{
    if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
    Directory.CreateDirectory(outDir);

    stats["art"] = Stage("art", () => ArtConvert.Art(fm, outDir));
    stats["tex"] = Stage("tex", () => ArtConvert.Tex(fm, outDir));
    stats["hues"] = Stage("hues", () => ArtConvert.Hues(fm, outDir));
    stats["tiledata"] = Stage("tiledata", () => ArtConvert.TileData(fm, outDir));

    var facets = new List<FacetInfo>();
    if (maps)
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
            .Where(p => !p.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var entries = new ManifestFile[files.Length];
        Parallel.For(0, files.Length, i =>
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

bool ok = Stage("verify (round-trip)", () => Verify.Run(fm, outDir, stats));
Stage("debug PNGs", () => DebugOut.Run(fm, outDir, debugDir));

Console.WriteLine($"[{total.Elapsed.TotalSeconds,7:F2}s] total — {(ok ? "OK" : "VERIFY FAILED")}");
return ok ? 0 : 2;
