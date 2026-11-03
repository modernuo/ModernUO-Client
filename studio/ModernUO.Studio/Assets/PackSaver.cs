using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Uo.Pack;

namespace ModernUO.Studio.Assets
{
    /// <summary>
    /// Saves the open pack with its edits as a new pack (published packs are immutable): every file is hard-linked from
    /// the source pack where the file system allows it (else copied), then the edited regions and the manifest are
    /// written through ReplacingFile, which replaces the link's directory entry and leaves the source untouched. This is
    /// the converter's "cp -al, then --only" recipe (docs/dev/runbook.md, "Reusing unchanged files").
    /// </summary>
    internal static class PackSaver
    {
        public sealed record Result(string Root, int Linked, int Copied, int Regions);

        public static Result Save(StudioAssets assets, string target, Action<string> progress)
        {
            target = Path.GetFullPath(target);
            string source = Path.GetFullPath(assets.Root);

            if (string.Equals(target.TrimEnd('\\', '/'), source.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("A pack is never saved over itself: choose a new folder.");
            }

            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                throw new IOException($"{target} is not empty: choose a new or empty folder.");
            }

            // refuses a folder under a published packs root (UO_PACKS_ROOTS and the defaults)
            PublishedPacks.EnsureOutputDir(target);
            Directory.CreateDirectory(target);

            // a fresh copy: the open pack keeps describing itself
            var manifest = ManifestFileIo.Read(Path.Combine(source, "manifest.json"));
            var files = manifest.Files.Select(f => f.Path).Append("manifest.json").ToList();
            int linked = 0, copied = 0;

            for (int i = 0; i < files.Count; i++)
            {
                if (i % 200 == 0)
                {
                    progress($"Linking files {i:N0} / {files.Count:N0}");
                }

                string from = Path.Combine(source, files[i]);
                string to = Path.Combine(target, files[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);

                if (TryHardLink(from, to))
                {
                    linked++;
                }
                else
                {
                    File.Copy(from, to);
                    copied++;
                }
            }

            // the edited regions, compressed like the converter's (RegionFile Kind 1), replacing their links
            var regions = assets.Edits.EditedRegions().ToList();
            var changed = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var (facet, rx, ry, header, blocks) in regions)
            {
                progress($"Writing region {facet}/{rx},{ry}");
                string relative = $"map/{facet}/r_{rx}_{ry}.bin";
                string path = Path.Combine(target, relative);
                RegionFile.Write(path, in header, blocks, compress: true);
                changed[relative] = path;
            }

            // the manifest describes the new files; everything else in it is the source pack's
            progress("Writing the manifest");

            foreach (var entry in manifest.Files.Where(f => changed.ContainsKey(f.Path)).ToList())
            {
                using var fs = File.OpenRead(changed[entry.Path]);
                entry.Size = fs.Length;
                entry.Sha256 = Convert.ToHexStringLower(SHA256.HashData(fs));
            }

            ManifestFileIo.Write(Path.Combine(target, "manifest.json"), manifest);
            progress("Saved");

            return new Result(target, linked, copied, regions.Count);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

        [DllImport("libc", SetLastError = true)]
        private static extern int link(string oldpath, string newpath);

        private static bool TryHardLink(string from, string to)
        {
            try
            {
                return OperatingSystem.IsWindows() ? CreateHardLinkW(to, from, IntPtr.Zero) : link(from, to) == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }
}
