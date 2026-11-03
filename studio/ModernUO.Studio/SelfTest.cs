using System.Security.Cryptography;
using ModernUO.Studio.Assets;
using Uo.Pack;

namespace ModernUO.Studio
{
    /// <summary>
    /// -selftest &lt;dir&gt;: the editing round trip without a mouse, for smoke tests. Raises a 5 x 5 patch and places a
    /// static at the map's centre tile, checks the reads, undo and redo, saves a new pack under &lt;dir&gt; and checks it
    /// on disk. Results go to the log as [selftest] lines; the edit stays applied so a -screenshot shows it.
    /// </summary>
    internal static class SelfTest
    {
        public static bool Run(StudioContext context, int facet, int x, int y, string dir)
        {
            var assets = context.Assets;
            var edits = assets.Edits;
            int failures = 0;

            void Check(bool ok, string what)
            {
                StudioLog.Info($"[selftest] {(ok ? "PASS" : "FAIL")} {what}");
                failures += ok ? 0 : 1;
            }

            var before = new MapTile();
            Check(MapData.TryGetTile(assets, facet, x, y, before), $"read tile {x}, {y}");
            sbyte z0 = before.LandZ;
            int statics0 = before.Statics.Count;

            // one change: a 5 x 5 patch raised by 10, and a robe on the centre tile
            var tx = edits.Begin("Self test", facet);

            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    if (tx.TryGet(x + dx, y + dy, out var land, out _))
                    {
                        tx.SetLand(x + dx, y + dy, land.TileId, (sbyte)(land.Z + 10));
                    }
                }
            }

            tx.TryGet(x, y, out _, out var centre);
            tx.SetStatics(x, y, centre.Append(new StaticCell { Graphic = 0x1F03, Z = (sbyte)(z0 + 10) }));
            var change = tx.Commit();
            Check(change is { Tiles: 25 }, $"one change of 25 tiles ({change?.Tiles})");

            var after = new MapTile();
            MapData.TryGetTile(assets, facet, x, y, after);
            Check(after.LandZ == z0 + 10 && after.Statics.Count == statics0 + 1, $"edited read: z {after.LandZ}, {after.Statics.Count} statics");

            edits.Undo();
            MapData.TryGetTile(assets, facet, x, y, after);
            Check(after.LandZ == z0 && after.Statics.Count == statics0, $"undo: z {after.LandZ}, {after.Statics.Count} statics");

            edits.Redo();
            MapData.TryGetTile(assets, facet, x, y, after);
            Check(after.LandZ == z0 + 10 && after.Statics.Count == statics0 + 1, $"redo: z {after.LandZ}, {after.Statics.Count} statics");

            // save, then read the new pack's region file back
            string target = Path.Combine(dir, $"selftest-{DateTime.Now:HHmmss}");
            int rb = assets.Files.Maps.RegionBlocks(facet);
            int rx = (x >> 3) / rb, ry = (y >> 3) / rb;
            string relative = $"map/{facet}/r_{rx}_{ry}.bin";
            string sourceRegion = assets.Pack.PathOf(relative);
            string sourceHash = Hash(sourceRegion);

            try
            {
                var result = PackSaver.Save(assets, target, _ => { });
                Check(result.Regions >= 1, $"saved {result.Root}: {result.Regions} region(s), {result.Linked:N0} linked, {result.Copied:N0} copied");

                var (header, blocks) = RegionFile.Read(Path.Combine(target, relative));
                var block = blocks[((y >> 3) % rb) * header.BlocksW + (x >> 3) % rb];
                var cell = block.Land[(y & 7) * 8 + (x & 7)];
                int onTile = block.Statics.Count(s => s.X == (x & 7) && s.Y == (y & 7));
                Check(cell.Z == z0 + 10 && onTile == statics0 + 1, $"saved region: z {cell.Z}, {onTile} statics");

                Check(Hash(sourceRegion) == sourceHash, "the source pack's region is unchanged");

                var manifest = ManifestFileIo.Read(Path.Combine(target, "manifest.json"));
                var entry = manifest.Files.First(f => f.Path == relative);
                Check(entry.Sha256 == Hash(Path.Combine(target, relative)), "the new manifest hashes the new region");

                using var reopened = ClassicUO.Assets.PackAssets.Open(target);
                Check(reopened.Manifest.Files.Count == assets.Manifest.Files.Count, "the new pack opens");
            }
            catch (Exception ex)
            {
                Check(false, $"save: {ex.Message}");
            }

            StudioLog.Info($"[selftest] {(failures == 0 ? "all passed" : $"{failures} failed")}");

            return failures == 0;
        }

        private static string Hash(string path)
        {
            using var fs = File.OpenRead(path);

            return Convert.ToHexStringLower(SHA256.HashData(fs));
        }
    }
}
