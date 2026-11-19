using System;
using System.Buffers;
using System.IO;
using System.IO.Compression;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Utility
{
    // The client inflates server data with ZlibInflater (the .NET 11 ZLibDecoder). The compressed forms in
    // zlib-vectors.txt were produced once by the reference zlib (Python zlib, levels 0/1/6/9) while the native wrapper
    // still existed, and that wrapper decoded each of them byte-identically to the payloads. Nothing in the client
    // compresses data for a server; the encoder here only builds extra round-trip inputs.
    public class ZlibCompatTests
    {
        public static System.Collections.Generic.IEnumerable<object[]> Cases() => ZlibPayloads.Cases();

        public static System.Collections.Generic.IEnumerable<object[]> Vectors()
        {
            foreach (string line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "zlib-vectors.txt")))
            {
                string[] f = line.Split(' ');
                yield return new object[] { int.Parse(f[0]), int.Parse(f[1]), f[2] };
            }
        }

        [Theory]
        [MemberData(nameof(Vectors))]
        public void ReferenceZlibVector_DecodesIdentically(int index, int level, string base64)
        {
            byte[] src = ZlibPayloads.All()[index];
            byte[] dst = new byte[src.Length];

            ZlibInflater.TryDecompress(Convert.FromBase64String(base64), dst, out int written).Should().BeTrue($"level {level}");
            written.Should().Be(src.Length);
            dst.Should().Equal(src);
        }

        [Fact]
        public void Vectors_CoverEveryPayloadAndLevel()
        {
            var seen = new System.Collections.Generic.HashSet<(int, int)>();

            foreach (object[] v in Vectors())
                seen.Add(((int)v[0], (int)v[1]));

            seen.Count.Should().Be(ZlibPayloads.All().Length * 4);
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void BclCompress_InflaterDecompress_Identical(int index)
        {
            byte[] src = ZlibPayloads.All()[index];
            byte[] comp = new byte[ZLibEncoder.GetMaxCompressedLength(src.Length)];
            ZLibEncoder.TryCompress(src, comp, out int cLen).Should().BeTrue();

            byte[] dst = new byte[src.Length];
            ZlibInflater.TryDecompress(comp.AsSpan(0, cLen), dst, out int written).Should().BeTrue();
            written.Should().Be(src.Length);
            dst.Should().Equal(src);
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void ZLibStream_InflaterDecompress_Identical(int index)
        {
            byte[] src = ZlibPayloads.All()[index];
            var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                z.Write(src);

            byte[] dst = new byte[src.Length];
            ZlibInflater.TryDecompress(ms.ToArray(), dst, out int written).Should().BeTrue();
            written.Should().Be(src.Length);
            dst.Should().Equal(src);
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void ReusedDecoder_AfterReset_Identical(int index)
        {
            byte[] src = ZlibPayloads.All()[index];
            byte[] comp = new byte[ZLibEncoder.GetMaxCompressedLength(src.Length)];
            ZLibEncoder.TryCompress(src, comp, out int cLen, 6).Should().BeTrue();

            using var dec = new ZLibDecoder();
            for (int pass = 0; pass < 3; pass++)
            {
                // One spare byte: the raw decoder reports DestinationTooSmall for an empty destination even for an
                // empty payload (ZlibInflater probes with one byte for that case).
                byte[] dst = new byte[src.Length + 1];
                dec.Decompress(comp.AsSpan(0, cLen), dst, out _, out int written).Should().Be(OperationStatus.Done);
                written.Should().Be(src.Length);
                dst.AsSpan(0, written).ToArray().Should().Equal(src);
                dec.Reset();
            }
        }
    }
}
