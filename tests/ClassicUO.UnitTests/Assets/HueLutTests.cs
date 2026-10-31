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
    // classic-1.0 hues.bin byte for byte. It skips when the pack is not on this machine.
    public class HueLutTests
    {
        private const string PackDir = @"C:\Repositories\uo-packs\classic-1.0";

        private readonly ITestOutputHelper _output;

        public HueLutTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Built_Lut_Equals_Legacy_HuesBin_Byte_For_Byte()
        {
            string raw = Path.Combine(PackDir, TableConst.HueRawFile), legacy = Path.Combine(PackDir, HueFile.LegacyFileName);

            if (!File.Exists(raw) || !File.Exists(legacy))
            {
                return;
            }

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
}
