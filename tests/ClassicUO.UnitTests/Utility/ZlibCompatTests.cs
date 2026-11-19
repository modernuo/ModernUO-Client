using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Utility
{
    // Compares the client's native zlib wrapper (ZLib.Decompress) with the .NET 11 span encoder/decoder.
    // Compressed bytes may differ between zlib and zlib-ng; only round-trips and cross-decoding must be exact.
    public class ZlibCompatTests
    {
        public static byte[][] Payloads()
        {
            var rnd = new Random(12345);

            byte[] Random(int n)
            {
                var b = new byte[n];
                rnd.NextBytes(b);
                return b;
            }

            // 0xD8 custom house: 5-byte entries (id u16 BE, x, y, z) as in the client's plane mode 0.
            var house = new byte[5 * 700];
            for (int i = 0; i < 700; i++)
            {
                int o = i * 5;
                ushort id = (ushort)(0x0500 + rnd.Next(0, 40));
                house[o] = (byte)(id >> 8);
                house[o + 1] = (byte)id;
                house[o + 2] = (byte)(i % 20 - 10);
                house[o + 3] = (byte)(i / 20 % 20 - 10);
                house[o + 4] = (byte)(i % 3 * 5);
            }

            string layout = string.Concat(
                System.Linq.Enumerable.Repeat("{ page 0 }{ resizepic 0 0 9200 300 200 }{ text 10 10 0 1 }{ button 20 150 4005 4007 1 0 1 }", 12));

            var repetitive = new byte[10_000];
            Array.Fill(repetitive, (byte)0x41);

            return new[]
            {
                Array.Empty<byte>(),
                new byte[] { 7 },
                Random(3),
                Random(4095),
                Random(65537),
                repetitive,
                house,
                System.Text.Encoding.UTF8.GetBytes(layout),
                System.Text.Encoding.BigEndianUnicode.GetBytes(layout),
            };
        }

        public static System.Collections.Generic.IEnumerable<object[]> Cases()
        {
            var p = Payloads();
            for (int i = 0; i < p.Length; i++)
                yield return new object[] { i };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void BclCompress_OldDecompress_Identical(int index)
        {
            byte[] src = Payloads()[index];
            byte[] comp = new byte[ZLibEncoder.GetMaxCompressedLength(src.Length)];
            ZLibEncoder.TryCompress(src, comp, out int cLen).Should().BeTrue();

            byte[] dst = new byte[src.Length];
            ZLib.Decompress(comp.AsSpan(0, cLen), dst).Should().Be(ZLib.ZLibError.Ok);
            dst.Should().Equal(src);
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void StreamCompress_BclDecompress_Identical(int index)
        {
            byte[] src = Payloads()[index];
            var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                z.Write(src);

            byte[] comp = ms.ToArray();
            // One spare byte: a zero-length destination makes the BCL decoder report DestinationTooSmall even for an
            // empty payload, so callers must never hand it an empty span.
            byte[] big = new byte[src.Length + 1];
            using var dec = new ZLibDecoder();
            dec.Decompress(comp, big, out int consumed, out int written).Should().Be(OperationStatus.Done);
            consumed.Should().Be(comp.Length);
            written.Should().Be(src.Length);
            big.AsSpan(0, written).ToArray().Should().Equal(src);
            byte[] dst = new byte[src.Length];

            ZLib.Decompress(comp, dst).Should().Be(ZLib.ZLibError.Ok);
            dst.Should().Equal(src);
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void ReusedDecoder_AfterReset_Identical(int index)
        {
            byte[] src = Payloads()[index];
            byte[] comp = new byte[ZLibEncoder.GetMaxCompressedLength(src.Length)];
            ZLibEncoder.TryCompress(src, comp, out int cLen, 6).Should().BeTrue();

            using var dec = new ZLibDecoder();
            for (int pass = 0; pass < 3; pass++)
            {
                byte[] dst = new byte[src.Length + 1];
                dec.Decompress(comp.AsSpan(0, cLen), dst, out _, out int written).Should().Be(OperationStatus.Done);
                written.Should().Be(src.Length);
                dst.AsSpan(0, written).ToArray().Should().Equal(src);
                dec.Reset();
            }
        }

        [Fact]
        public void Truncated_BothReportFailure()
        {
            byte[] src = Payloads()[6];
            byte[] comp = new byte[ZLibEncoder.GetMaxCompressedLength(src.Length)];
            ZLibEncoder.TryCompress(src, comp, out int cLen).Should().BeTrue();
            var cut = comp.AsSpan(0, cLen / 2);

            ZLib.Decompress(cut, new byte[src.Length]).Should().NotBe(ZLib.ZLibError.Ok);

            using var dec = new ZLibDecoder();
            dec.Decompress(cut, new byte[src.Length], out _, out _).Should().NotBe(OperationStatus.Done);
        }
    }
}
