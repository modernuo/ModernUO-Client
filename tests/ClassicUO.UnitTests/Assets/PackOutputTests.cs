using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // Published packs are immutable (owner, 2026-10-31). Test packs hard-link files from a published pack and convert only
    // some sections, so every converter output must replace its directory entry instead of writing through the link, and
    // the converter refuses an output directory under a published packs root.
    public sealed class PackOutputTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"pack_output_{Guid.NewGuid():N}");

        public PackOutputTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        }

        [Fact]
        public void Writing_A_Hard_Linked_Output_Replaces_It_And_Leaves_The_Published_Link_Unchanged()
        {
            string published = Path.Combine(_dir, "published_hues_raw.bin"), output = Path.Combine(_dir, "hues_raw.bin");
            HueRawFile.Write(published, new HueEntry[8]);
            byte[] before = File.ReadAllBytes(published);
            HardLinks.Create(output, published);
            if (OperatingSystem.IsWindows()) HardLinks.Count(published).Should().Be(2);

            var e = new HueEntry[16];
            e[3].Colors[7] = 0x7FFF;
            HueRawFile.Write(output, e);

            File.ReadAllBytes(published).Should().Equal(before, "the published link must keep its content");
            if (OperatingSystem.IsWindows()) HardLinks.Count(published).Should().Be(1, "the output entry was replaced, not written through");
            var read = HueRawFile.Read(output);
            read.Length.Should().Be(16);
            read[3].Colors[7].Should().Be(0x7FFF);
            Directory.GetFiles(_dir, "*.tmp").Should().BeEmpty();
        }

        [Fact]
        public void Every_Writer_Kind_Breaks_The_Link()
        {
            // One writer of each shape: a table, a compressed page, a record store, the manifest (text) and a copy.
            void Check(string name, Action<string> write)
            {
                string published = Path.Combine(_dir, "pub_" + name), output = Path.Combine(_dir, name);
                File.WriteAllBytes(published, new byte[] { 1, 2, 3, 4 });
                HardLinks.Create(output, published);
                write(output);
                File.ReadAllBytes(published).Should().Equal(new byte[] { 1, 2, 3, 4 }, name);
                File.ReadAllBytes(output).Should().NotEqual(new byte[] { 1, 2, 3, 4 }, name);
            }

            Check("radarcol.bin", p => RadarColFile.Write(p, new ushort[] { 1, 2, 3 }));
            Check("page_0000.bin", p => AtlasPage.Write(p, 256, 1, PixelFormat.Rgba8, new byte[256 * 256 * 4], FileKind.Brotli));
            Check("page_0001.bin", p => AtlasPage.Write(p, 256, 1, PixelFormat.Rgba8, new byte[256 * 256 * 4], FileKind.ChunkedBrotli));
            Check("manifest.json", p => ManifestFileIo.Write(p, new Manifest()));
            Check("copy.bin", p => ReplacingFile.Copy(Path.Combine(_dir, "radarcol.bin"), p));
        }

        [Fact]
        public void A_Failed_Write_Leaves_The_Target_And_No_Temp_File()
        {
            string target = Path.Combine(_dir, "t.bin");
            File.WriteAllBytes(target, new byte[] { 9 });

            Action write = () =>
            {
                using var fs = ReplacingFile.Create(target);
                fs.WriteByte(1);
                throw new InvalidOperationException("boom");
            };

            write.Should().Throw<InvalidOperationException>();
            File.ReadAllBytes(target).Should().Equal(9);
            Directory.GetFiles(_dir).Should().ContainSingle();
        }

        [Fact]
        public void A_Published_Root_And_Everything_Under_It_Is_Refused()
        {
            string root = Path.Combine(_dir, "uo-packs");
            Directory.CreateDirectory(Path.Combine(root, "classic-1.0"));

            // Configured root (as --packs-root / UO_PACKS_ROOTS would pass it).
            string[] roots = { root };
            PublishedPacks.FindProtectedRoot(root, roots).Should().NotBeNull();
            PublishedPacks.FindProtectedRoot(Path.Combine(root, "classic-1.0"), roots).Should().NotBeNull();
            PublishedPacks.FindProtectedRoot(Path.Combine(root, "classic-1.0", "art"), roots).Should().NotBeNull();
            PublishedPacks.FindProtectedRoot(Path.Combine(root, "not-yet-created"), roots).Should().NotBeNull();
            PublishedPacks.FindProtectedRoot(Path.Combine(root, "classic-1.0", "..", "..", "scratch"), roots).Should().BeNull();
            PublishedPacks.FindProtectedRoot(root + "-scratch", roots).Should().BeNull();

            Action write = () => PublishedPacks.EnsureWritable(Path.Combine(root, "classic-1.0"), roots);
            write.Should().Throw<InvalidOperationException>().WithMessage("*published packs root*");

            // Detected root: an ancestor holding CURRENT that names one of its folders, with no root configured.
            string[] none = Array.Empty<string>();
            PublishedPacks.FindProtectedRoot(Path.Combine(root, "classic-1.0"), none).Should().BeNull();
            File.WriteAllText(Path.Combine(root, PublishedPacks.CurrentFile), "classic-1.0\r\n");
            PublishedPacks.FindProtectedRoot(Path.Combine(root, "classic-1.0"), none).Should().NotBeNull();
            PublishedPacks.FindProtectedRoot(Path.Combine(root, "classic-1.1-test"), none).Should().NotBeNull();
            PublishedPacks.FindProtectedRoot(Path.Combine(_dir, "scratch", "pack"), none).Should().BeNull();
        }

        [Fact]
        public void A_Junction_That_Resolves_Into_A_Published_Root_Is_Refused()
        {
            if (!OperatingSystem.IsWindows()) return; // junctions are Windows-only; the symlink test covers other systems

            string root = Path.Combine(_dir, "uo-packs");
            Directory.CreateDirectory(Path.Combine(root, "classic-1.0", "art"));
            string link = Path.Combine(_dir, "scratch-junction");
            Junctions.Create(link, Path.Combine(root, "classic-1.0"));
            new DirectoryInfo(link).LinkTarget.Should().NotBeNull();

            string[] roots = { root };
            PublishedPacks.FindProtectedRoot(link, roots).Should().NotBeNull();
            PublishedPacks.FindProtectedRoot(Path.Combine(link, "art"), roots).Should().NotBeNull();
            PublishedPacks.FindProtectedRoot(Path.Combine(_dir, "scratch"), roots).Should().BeNull();
            Directory.Delete(link); // removes the junction only
            Directory.Exists(Path.Combine(root, "classic-1.0", "art")).Should().BeTrue();
        }

        [SymlinkFact]
        public void A_Link_That_Resolves_Into_A_Published_Root_Is_Refused()
        {
            string root = Path.Combine(_dir, "uo-packs");
            Directory.CreateDirectory(Path.Combine(root, "classic-1.0"));
            string link = Path.Combine(_dir, "scratch-link");
            Directory.CreateSymbolicLink(link, Path.Combine(root, "classic-1.0"));
            string linkToRoot = Path.Combine(_dir, "root-link");
            Directory.CreateSymbolicLink(linkToRoot, root);

            string[] roots = { root };
            PublishedPacks.FindProtectedRoot(link, roots).Should().NotBeNull();
            PublishedPacks.FindProtectedRoot(Path.Combine(link, "art"), roots).Should().NotBeNull();
            PublishedPacks.FindProtectedRoot(Path.Combine(linkToRoot, "classic-1.0"), roots).Should().NotBeNull();

            // A configured root given through a link protects its target too.
            PublishedPacks.FindProtectedRoot(Path.Combine(root, "classic-1.0"), new[] { linkToRoot }).Should().NotBeNull();
        }

        // --- Codex audit round 2 ---

        /// <summary>A temp packs root detected by its CURRENT file (no process-wide configuration needed).</summary>
        private string DetectedRoot(out string versionArt)
        {
            string root = Path.Combine(_dir, "uo-packs");
            versionArt = Path.Combine(root, "classic-1.0", "art");
            Directory.CreateDirectory(versionArt);
            File.WriteAllText(Path.Combine(root, PublishedPacks.CurrentFile), "classic-1.0");
            File.WriteAllBytes(Path.Combine(versionArt, "page_0000.bin"), new byte[] { 7, 7, 7 });
            return root;
        }

        [Fact]
        public void R2_1_An_Output_Folder_Above_A_Published_Root_Is_Refused()
        {
            string root = Path.Combine(_dir, "assets", "published");
            Directory.CreateDirectory(Path.Combine(root, "classic-1.0"));
            string[] roots = { root };

            // A file may be written beside the root, but the parent may not be an output folder (a full conversion clears it).
            PublishedPacks.EnsureWritable(Path.Combine(_dir, "assets"), roots);
            Action asOutput = () => PublishedPacks.EnsureOutputDir(Path.Combine(_dir, "assets"), roots);
            asOutput.Should().Throw<InvalidOperationException>().WithMessage("*or above*");
            PublishedPacks.FindProtectedRoot(_dir, roots, orContains: true).Should().Be(root);
            PublishedPacks.FindProtectedRoot(Path.Combine(_dir, "assets", "scratch"), roots, orContains: true).Should().BeNull();
        }

        [Fact]
        public void R2_1_A_Full_Conversion_Deletes_Only_The_Files_Its_Previous_Manifest_Lists()
        {
            string outDir = Path.Combine(_dir, "out");
            Directory.CreateDirectory(Path.Combine(outDir, "art"));
            File.WriteAllBytes(Path.Combine(outDir, "a.bin"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(outDir, "art", "b.bin"), new byte[] { 2 });
            File.WriteAllBytes(Path.Combine(outDir, "art", ".b.bin.0123" + ".replacing.tmp"), new byte[] { 3 });
            File.WriteAllBytes(Path.Combine(outDir, "keep.txt"), new byte[] { 4 });
            var m = new Manifest();
            m.Files.Add(new ManifestFile { Path = "a.bin" });
            m.Files.Add(new ManifestFile { Path = "art/b.bin" });
            m.Files.Add(new ManifestFile { Path = "art/gone.bin" });
            ManifestFileIo.Write(Path.Combine(outDir, "manifest.json"), m);

            PackOutputFiles.ClearPreviousOutput(outDir);

            Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Should().ContainSingle().Which.Should().EndWith("keep.txt");

            // A non-empty folder that is no converter output (e.g. the parent of a packs root) is refused and untouched.
            string parent = Path.Combine(_dir, "assets");
            Directory.CreateDirectory(Path.Combine(parent, "published", "classic-1.0"));
            Action clearParent = () => PackOutputFiles.ClearPreviousOutput(parent);
            clearParent.Should().Throw<InvalidOperationException>().WithMessage("*not a converter output*");
            Directory.Exists(Path.Combine(parent, "published", "classic-1.0")).Should().BeTrue();

            // A manifest naming a file outside the folder is refused before anything is deleted.
            File.WriteAllBytes(Path.Combine(_dir, "outside.bin"), new byte[] { 5 });
            var bad = new Manifest();
            bad.Files.Add(new ManifestFile { Path = "../outside.bin" });
            ManifestFileIo.Write(Path.Combine(outDir, "manifest.json"), bad);
            Action clearBad = () => PackOutputFiles.ClearPreviousOutput(outDir);
            clearBad.Should().Throw<InvalidOperationException>().WithMessage("*outside the pack*");
            File.Exists(Path.Combine(_dir, "outside.bin")).Should().BeTrue();
        }

        [Fact]
        public void R2_2_A_Junction_Inside_An_Accepted_Output_Cannot_Reach_A_Published_Pack()
        {
            if (!OperatingSystem.IsWindows()) return; // junctions are Windows-only

            DetectedRoot(out string publishedArt);
            string outDir = Path.Combine(_dir, "scratch");
            Directory.CreateDirectory(outDir);
            Junctions.Create(Path.Combine(outDir, "art"), publishedArt);
            PublishedPacks.EnsureOutputDir(outDir); // the output folder itself is fine
            string viaJunction = Path.Combine(outDir, "art", "page_0000.bin");

            Action write = () => ReplacingFile.WriteAllBytes(viaJunction, new byte[] { 9 });
            write.Should().Throw<InvalidOperationException>().WithMessage("*published packs root*");
            Action delete = () => PackOutputFiles.Delete(viaJunction);
            delete.Should().Throw<InvalidOperationException>();
            Action deleteAll = () => PackOutputFiles.DeleteFiles(Path.Combine(outDir, "art"), "*.bin");
            deleteAll.Should().Throw<InvalidOperationException>();

            File.ReadAllBytes(Path.Combine(publishedArt, "page_0000.bin")).Should().Equal(7, 7, 7);
            Directory.GetFiles(publishedArt).Should().ContainSingle("no temp file was created in the published folder");
            Directory.Delete(Path.Combine(outDir, "art")); // removes the junction only
        }

        [Fact]
        public void R2_3_4_Pack_Writers_Have_No_Direct_File_Writes_And_Tools_Share_One_Root_Configuration()
        {
            string repo = ClassicUO.UnitTests.Ecs.WriteSiteScanner.FindRepoRoot();
            string poc = Path.Combine(repo, "poc");
            var raw = new System.Text.RegularExpressions.Regex(
                @"\bFile\.(Create|WriteAll\w*|AppendAll\w*|OpenWrite|Copy|Move|Delete)\s*\(|\bDirectory\.Delete\s*\(|FileMode\.(Create|CreateNew|Append|OpenOrCreate|Truncate)\b");
            string obj = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;
            var files = Directory.GetFiles(Path.Combine(poc, "Uo.Pack"), "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(Path.Combine(poc, "Uo.Converter"), "*.cs", SearchOption.AllDirectories))
                .Append(Path.Combine(poc, "Uo.Render.App", "Synthetic.cs"))
                .Where(f => !f.Contains(obj) && Path.GetFileName(f) != "PackOutput.cs");
            var hits = files.SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, l)))
                .Where(x => !x.l.TrimStart().StartsWith("//") && raw.IsMatch(x.l))
                .Select(x => $"{Path.GetRelativePath(repo, x.f)}:{x.i + 1}: {x.l.Trim()}").ToList();
            hits.Should().BeEmpty("pack and debug output goes through ReplacingFile / PackOutputFiles, which check the published roots");

            foreach (var program in new[] { Path.Combine(poc, "Uo.Converter", "Program.cs"), Path.Combine(poc, "Uo.Render.App", "Program.cs") })
            {
                string src = File.ReadAllText(program);
                src.Should().Contain("PublishedPacks.ConfigureFromArgs(args)", program);
                src.Should().NotContain(@"uo-packs\synthetic", program);
            }
            File.ReadAllText(Path.Combine(poc, "Uo.Render.App", "Synthetic.cs")).Should().Contain("Path.GetTempPath()").And.Contain("EnsureOutputDir");
            File.ReadAllText(Path.Combine(poc, "Uo.Render.App", "Program.cs")).Should().Contain("EnsureOutputDir(dst)");
        }

        [Fact]
        public void R2_4_Packs_Root_Arguments_Apply_Process_Wide_Wherever_They_Appear()
        {
            string root = Path.Combine(_dir, "custom-root"); // no CURRENT file: only the argument protects it
            Directory.CreateDirectory(Path.Combine(root, "v1"));
            try
            {
                var rest = PublishedPacks.ConfigureFromArgs(new[] { "--bench-chunked", "anim", Path.Combine(root, "v1"), PublishedPacks.RootsOption, root });
                rest.Should().Equal("--bench-chunked", "anim", Path.Combine(root, "v1"));
                PublishedPacks.ConfiguredRoots().Should().Contain(root);

                Action write = () => ReplacingFile.WriteAllBytes(Path.Combine(root, "v1", "page_0000.bin"), new byte[] { 1 });
                write.Should().Throw<InvalidOperationException>();
                Action output = () => PublishedPacks.EnsureOutputDir(Path.Combine(root, "v1"));
                output.Should().Throw<InvalidOperationException>();
                Directory.GetFiles(Path.Combine(root, "v1")).Should().BeEmpty();

                Action missing = () => PublishedPacks.ConfigureFromArgs(new[] { PublishedPacks.RootsOption });
                missing.Should().Throw<ArgumentException>();
            }
            finally
            {
                PublishedPacks.Configure(Array.Empty<string>());
            }
        }

        [Fact]
        public void R2_5_Path_Aliases_Of_A_Protected_Root_Are_Refused()
        {
            if (!OperatingSystem.IsWindows()) return; // the aliases are Windows path forms

            string root = Path.Combine(_dir, "uo-packs");
            string version = Path.Combine(root, "classic-1.0");
            Directory.CreateDirectory(Path.Combine(version, "art"));
            string[] roots = { root };

            PublishedPacks.FindProtectedRoot(@"\\?\" + version, roots).Should().Be(root);
            PublishedPacks.FindProtectedRoot(@"\\?\" + Path.Combine(version, "new"), roots).Should().Be(root);
            PublishedPacks.FindProtectedRoot(@"\\.\" + version, roots).Should().Be(root);
            PublishedPacks.FindProtectedRoot(version, new[] { @"\\?\" + root }).Should().NotBeNull();
            PublishedPacks.FindProtectedRoot(@"\\?\" + Path.Combine(_dir, "scratch"), roots).Should().BeNull();

            // Volume GUID spelling of the same folder.
            string volumeRoot = Path.GetPathRoot(version)!;
            var volume = new char[64];
            if (VolumeName.GetVolumeNameForVolumeMountPointW(volumeRoot, volume, volume.Length))
            {
                string guidPath = new string(volume).TrimEnd('\0') + version[volumeRoot.Length..];
                PublishedPacks.FindProtectedRoot(guidPath, roots).Should().Be(root);
            }

            // UNC spelling through the administrative share, when this machine exposes it.
            string drive = version[..1];
            string unc = $@"\\localhost\{drive}$" + version[2..];
            if (Directory.Exists(unc))
            {
                PublishedPacks.FindProtectedRoot(unc, roots).Should().Be(root);
                PublishedPacks.FindProtectedRoot(Path.Combine(unc, "art"), roots).Should().Be(root);
                // A root configured by its network spelling still protects the local path (file identity).
                string uncRoot = $@"\\localhost\{drive}$" + root[2..];
                PublishedPacks.FindProtectedRoot(Path.Combine(version, "art"), new[] { uncRoot }).Should().Be(uncRoot);
                PublishedPacks.FindProtectedRoot($@"\\localhost\{drive}$" + _dir[2..], roots).Should().BeNull();
            }

            // Unreachable network paths cannot be related to a root: fail closed.
            Action unreachable = () => PublishedPacks.EnsureWritable(@"\\no-such-host-cuo.invalid\share\pack", roots);
            unreachable.Should().Throw<InvalidOperationException>().WithMessage("*cannot establish*");
        }

        [Fact]
        public void R2_6_A_Volume_Root_Protects_Its_Descendants()
        {
            PublishedPacks.IsSameOrUnder(@"P:\version", @"P:\").Should().Be(OperatingSystem.IsWindows());
            PublishedPacks.IsSameOrUnder("/version", "/").Should().BeTrue();
            PublishedPacks.IsSameOrUnder(@"C:\packs-scratch", @"C:\packs").Should().BeFalse();

            string volumeRoot = Path.GetPathRoot(Path.GetFullPath(_dir))!;
            PublishedPacks.FindProtectedRoot(_dir, new[] { volumeRoot }).Should().Be(volumeRoot);
        }

        [Fact]
        public void R2_7_A_Failed_Commit_Keeps_The_Target_Leaves_No_Temp_File_And_Reports_The_Error()
        {
            if (!OperatingSystem.IsWindows()) return; // POSIX rename replaces an open file

            string target = Path.Combine(_dir, "page_0000.bin");
            File.WriteAllBytes(target, new byte[] { 1, 2 });
            Exception caught;
            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read)) // a running client's reader
            {
                Action write = () => ReplacingFile.WriteAllBytes(target, new byte[] { 3, 4, 5 });
                caught = write.Should().Throw<Exception>().Which;
            }
            (caught is IOException or UnauthorizedAccessException).Should().BeTrue(caught.ToString());
            File.ReadAllBytes(target).Should().Equal(1, 2);
            Directory.GetFiles(_dir).Should().ContainSingle("the temp file is removed after the failed move");
        }

        [Fact]
        public void R2_A_Junction_Loop_Fails_Closed()
        {
            if (!OperatingSystem.IsWindows()) return;

            string a = Path.Combine(_dir, "loop-a"), b = Path.Combine(_dir, "loop-b");
            Junctions.Create(a, b);
            Junctions.Create(b, a);
            string inside = Path.Combine(a, "pack");

            Action find = () => PublishedPacks.FindProtectedRoot(inside, Array.Empty<string>());
            find.Should().Throw<IOException>();
            Action resolve = () => PublishedPacks.Resolve(inside);
            resolve.Should().Throw<IOException>();
            Action write = () => ReplacingFile.WriteAllBytes(Path.Combine(inside, "x.bin"), new byte[] { 1 });
            write.Should().Throw<InvalidOperationException>().WithMessage("*cannot establish*");
            Directory.Delete(a);
            Directory.Delete(b);
        }
    }

    /// <summary>Skipped when this machine cannot create directory symlinks (Windows without Developer Mode or admin).</summary>
    public sealed class SymlinkFactAttribute : FactAttribute
    {
        public SymlinkFactAttribute()
        {
            string probe = Path.Combine(Path.GetTempPath(), $"symlink_probe_{Guid.NewGuid():N}");
            try
            {
                Directory.CreateSymbolicLink(probe, Path.GetTempPath());
                Directory.Delete(probe);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Skip = $"cannot create directory symlinks here: {e.Message}";
            }
        }
    }

    internal static class Junctions
    {
        /// <summary>Creates a directory junction (no privilege needed, unlike a symlink).</summary>
        public static void Create(string link, string target)
        {
            Directory.CreateDirectory(link);
            byte[] subst = System.Text.Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
            byte[] print = System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(target));
            int pathBytes = subst.Length + 2 + print.Length + 2;
            var buf = new byte[8 + 8 + pathBytes];
            var span = buf.AsSpan();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(span, 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)(8 + pathBytes));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[8..], 0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[10..], (ushort)subst.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[12..], (ushort)(subst.Length + 2));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(span[14..], (ushort)print.Length);
            subst.CopyTo(span[16..]);
            print.CopyTo(span[(16 + subst.Length + 2)..]);

            using var h = CreateFileW(link, 0x40000000 /* GENERIC_WRITE */, 0, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                                      0x02000000 | 0x00200000 /* BACKUP_SEMANTICS | OPEN_REPARSE_POINT */, IntPtr.Zero);
            if (h.IsInvalid) throw new IOException($"open {link} failed ({Marshal.GetLastPInvokeError()})");
            if (!DeviceIoControl(h, 0x000900A4 /* FSCTL_SET_REPARSE_POINT */, buf, buf.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new IOException($"junction {link} -> {target} failed ({Marshal.GetLastPInvokeError()})");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle h, uint code, byte[] inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr overlapped);
    }

    internal static class VolumeName
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, [Out] char[] volumeName, int length);
    }

    internal static class HardLinks
    {
        public static void Create(string link, string existing)
        {
            bool ok = OperatingSystem.IsWindows() ? CreateHardLinkW(link, existing, IntPtr.Zero) : link_unix(existing, link) == 0;
            if (!ok) throw new IOException($"hard link {link} -> {existing} failed ({Marshal.GetLastPInvokeError()})");
        }

        public static int Count(string path)
        {
            if (!OperatingSystem.IsWindows()) return -1;
            using var h = File.OpenHandle(path);
            if (!GetFileInformationByHandle(h, out var info)) throw new IOException("GetFileInformationByHandle");
            return (int)info.NumberOfLinks;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public long CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out ByHandleFileInformation info);

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        private static extern int link_unix(string oldpath, string newpath);
    }
}
