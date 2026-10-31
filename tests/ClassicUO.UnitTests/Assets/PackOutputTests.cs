using System;
using System.IO;
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
