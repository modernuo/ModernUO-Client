using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using FluentAssertions;
using Uo.Pack;
using Xunit;
using Xunit.Abstractions;

namespace ClassicUO.UnitTests.Assets
{
    // One source of truth per pack file (owner, 2026-10-31): hues.bin is retired and the RGBA8 hue LUT is built from
    // hues_raw.bin at load (HueLut.Build). The pack test proves the change is pixel-neutral: the built LUT equals the
    // classic-1.0 hues.bin byte for byte; it is reported as skipped (not passed) when the pack is not on this machine.
    // The portable fixture below proves the expansion everywhere, CI included.
    public class HueLutTests
    {
        private const string PackDir = @"C:\Repositories\uo-packs\classic-1.0";

        private readonly ITestOutputHelper _output;

        public HueLutTests(ITestOutputHelper output) => _output = output;

        [FilesFact(PackDir, TableConst.HueRawFile, HueFile.LegacyFileName)]
        public void Built_Lut_Equals_Legacy_HuesBin_Byte_For_Byte()
        {
            string raw = Path.Combine(PackDir, TableConst.HueRawFile), legacy = Path.Combine(PackDir, HueFile.LegacyFileName);

            var entries = HueRawFile.Read(raw);
            var lut = HueLut.Build(entries);

            // The legacy file: FileHeader, count i32, count * 32 RGBA8. Compare everything after the header.
            byte[] file = File.ReadAllBytes(legacy);
            int hdr = StructIo.SizeOf<FileHeader>();
            BitConverter.ToInt32(file, hdr).Should().Be(entries.Length);
            file.Length.Should().Be(hdr + 4 + lut.Length * 4);
            file.AsSpan(hdr + 4).SequenceEqual(MemoryMarshal.AsBytes(lut.AsSpan())).Should().BeTrue();

            var (count, rgba) = HueFile.Read(legacy);
            count.Should().Be(entries.Length);
            rgba.Should().Equal(lut);

            // Load-time cost: the read + build the client does in HuesLoader.Load, and the build alone.
            const int runs = 200;
            var build = new double[runs];
            var load = new double[runs];
            for (int i = 0; i < runs; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                var e = HueRawFile.Read(raw);
                long t1 = Stopwatch.GetTimestamp();
                HueLut.Build(e);
                long t2 = Stopwatch.GetTimestamp();
                build[i] = (t2 - t1) * 1e6 / Stopwatch.Frequency;
                load[i] = (t2 - t0) * 1e6 / Stopwatch.Frequency;
            }

            Array.Sort(build);
            Array.Sort(load);
            _output.WriteLine($"HueLut.Build ({entries.Length} hues): max {build.Max():F1} us (incl. first, cold), median {build[runs / 2]:F1} us, p95 {build[runs * 95 / 100]:F1} us; " +
                              $"read hues_raw.bin + build: median {load[runs / 2]:F1} us, p95 {load[runs * 95 / 100]:F1} us ({runs} runs)");
        }

        // Portable fixture: three synthetic hues_raw.bin entries (colour k of entry e = k * 0x0C63 + e * 0x1F07, bit 15 set on
        // every fifth shade) and the LUT bytes the legacy hues.bin held for them, computed independently of HueLut
        // (R, G, B = v << 3 | v >> 2 of the 5-bit fields, then FF; one row of 32 texels per entry).
        private static readonly string[] FixtureRows =
        {
            "000000FF181818FF313131FF4A4A4AFF636363FF7B7B7BFF949494FFADADADFFC6C6C6FFDEDEDEFFF7F7F7FF101008FF292921FF424239FF5A5A52FF73736BFF" +
            "8C8C84FFA5A59CFFBDBDB5FFD6D6CEFFEFEFE7FF0800FFFF212110FF393929FF525242FF6B6B5AFF848473FF9C9C8CFFB5B5A5FFCECEBDFFE7E7D6FFFFFFEFFF",
            "39C639FF52DE52FF6BF76BFF8C0884FFA5219CFFBD39B5FFD652CEFFEF6BE7FF0084FFFF18A510FF31BD29FF4AD642FF63EF5AFF840073FF9C188CFFB531A5FF" +
            "CE4ABDFFE763D6FFFF7BEFFF109C00FF29B518FF42CE31FF5AE74AFF73FF63FF94107BFFAD2994FFC642ADFFDE5AC6FFF773DEFF088CF7FF21AD08FF39C621FF",
            "7B8473FF949C8CFFADB5A5FFC6CEBDFFDEE7D6FFF7FFEFFF101800FF293118FF424A31FF5A634AFF737B63FF8C947BFFA5AD94FFBDC6ADFFD6DEC6FFEFF7DEFF" +
            "0808F7FF212908FF394221FF525A39FF6B7352FF848C6BFF9CA584FFB5BD9CFFCED6B5FFE7EFCEFF0000E7FF1818FFFF313910FF4A5229FF636B42FF7B845AFF",
        };

        [Fact]
        public void Synthetic_Raw_Entries_Expand_To_The_Expected_Lut_Bytes()
        {
            var e = new HueEntry[FixtureRows.Length];
            for (int i = 0; i < e.Length; i++)
            {
                for (int k = 0; k < HueLut.Shades; k++)
                {
                    e[i].Colors[k] = (ushort)((k * 0x0C63 + i * 0x1F07 + (k % 5 == 0 ? 0x8000 : 0)) & 0xFFFF);
                }
            }

            // Through the file format, as the client loads it.
            string path = Path.Combine(Path.GetTempPath(), $"hues_raw_{Guid.NewGuid():N}.bin");
            HueEntry[] read;
            try
            {
                HueRawFile.Write(path, e);
                read = HueRawFile.Read(path);
            }
            finally
            {
                File.Delete(path);
            }

            var lut = HueLut.Build(read);
            byte[] expected = Convert.FromHexString(string.Concat(FixtureRows));
            lut.Length.Should().Be(FixtureRows.Length * HueLut.Shades);
            MemoryMarshal.AsBytes(lut.AsSpan()).ToArray().Should().Equal(expected);
        }

        [Fact]
        public void Build_Uses_Bit_Replication_Alpha_FF_And_Ignores_Bit_15()
        {
            var e = new HueEntry[2];
            e[0].Colors[0] = 0x0000;
            e[0].Colors[1] = 0x7FFF;
            e[0].Colors[2] = 0xFFFF;              // bit 15 set: ignored
            e[0].Colors[3] = 0x1F << 10;          // pure red, 5 bits
            e[0].Colors[4] = 0x10 << 5;           // green 16 -> 0x84
            e[1].Colors[31] = 0x01;               // blue 1 -> 0x08

            var lut = HueLut.Build(e);

            lut.Length.Should().Be(2 * HueLut.Shades);
            lut[0].Should().Be(0xFF000000u);
            lut[1].Should().Be(0xFFFFFFFFu);
            lut[2].Should().Be(0xFFFFFFFFu);
            lut[3].Should().Be(0xFF0000FFu);      // R in the low byte
            lut[4].Should().Be(0xFF008400u);
            lut[HueLut.Shades + 31].Should().Be(0xFF080000u);
            lut[5].Should().Be(0xFF000000u);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(3001)]                         // more entries than the file holds
        [InlineData(HueRawFile.MaxEntries + 1)]
        [InlineData(int.MaxValue)]
        public void Read_Rejects_Bad_Counts(int count)
        {
            string path = Path.Combine(Path.GetTempPath(), $"hues_raw_{Guid.NewGuid():N}.bin");

            try
            {
                HueRawFile.Write(path, new HueEntry[3000]);

                using (var fs = File.OpenWrite(path))
                {
                    fs.Position = StructIo.SizeOf<FileHeader>();
                    fs.Write(BitConverter.GetBytes(count));
                }

                Action read = () => HueRawFile.Read(path);
                read.Should().Throw<InvalidDataException>();
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>A fact that is reported as skipped when its integration data (files under <c>dir</c>) is not on this machine.</summary>
    public sealed class FilesFactAttribute : FactAttribute
    {
        public FilesFactAttribute(string dir, params string[] files)
        {
            var missing = files.Select(f => Path.Combine(dir, f)).FirstOrDefault(p => !File.Exists(p));
            if (missing != null)
            {
                Skip = $"integration data not on this machine: {missing}";
            }
        }
    }
}
