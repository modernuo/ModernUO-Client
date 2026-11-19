using System;
using System.IO.Compression;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Utility
{
    public class ZlibInflaterTests
    {
        private static byte[] Compress(byte[] src)
        {
            byte[] comp = new byte[ZLibEncoder.GetMaxCompressedLength(src.Length)];
            ZLibEncoder.TryCompress(src, comp, out int n).Should().BeTrue();
            return comp.AsSpan(0, n).ToArray();
        }

        [Theory]
        [MemberData(nameof(ZlibCompatTests.Cases), MemberType = typeof(ZlibCompatTests))]
        public void RoundTrip_ExactSizedDestination(int index)
        {
            byte[] src = ZlibCompatTests.Payloads()[index];
            byte[] dst = new byte[src.Length];

            ZlibInflater.TryDecompress(Compress(src), dst, out int written).Should().BeTrue();
            written.Should().Be(src.Length);
            dst.Should().Equal(src);
        }

        [Fact]
        public void DestinationTooSmall_Fails()
        {
            byte[] src = ZlibCompatTests.Payloads()[6];

            ZlibInflater.TryDecompress(Compress(src), new byte[src.Length - 1], out _).Should().BeFalse();
        }

        [Fact]
        public void LargerDestination_ReportsWritten()
        {
            byte[] src = ZlibCompatTests.Payloads()[7];
            byte[] dst = new byte[src.Length + 50];

            ZlibInflater.TryDecompress(Compress(src), dst, out int written).Should().BeTrue();
            written.Should().Be(src.Length);
        }

        [Fact]
        public void TruncatedAndGarbage_Fail_ThenDecoderStillWorks()
        {
            byte[] src = ZlibCompatTests.Payloads()[6];
            byte[] comp = Compress(src);
            byte[] dst = new byte[src.Length];

            ZlibInflater.TryDecompress(comp.AsSpan(0, comp.Length / 2), dst, out _).Should().BeFalse();
            ZlibInflater.TryDecompress(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, dst, out _).Should().BeFalse();
            ZlibInflater.TryDecompress(ReadOnlySpan<byte>.Empty, dst, out _).Should().BeFalse();

            ZlibInflater.TryDecompress(comp, dst, out int written).Should().BeTrue();
            written.Should().Be(src.Length);
            dst.Should().Equal(src);
        }

        [Fact]
        public void EmptyPayload_EmptyDestination_Succeeds_NonEmptyPayload_Fails()
        {
            ZlibInflater.TryDecompress(Compress(Array.Empty<byte>()), Span<byte>.Empty, out int written).Should().BeTrue();
            written.Should().Be(0);

            ZlibInflater.TryDecompress(Compress(new byte[] { 1 }), Span<byte>.Empty, out _).Should().BeFalse();
        }

        [Fact]
        public void TrailingBytesAfterStream_AreIgnored()
        {
            byte[] src = ZlibCompatTests.Payloads()[7];
            byte[] comp = Compress(src);
            byte[] padded = new byte[comp.Length + 9];
            comp.CopyTo(padded, 0);
            byte[] dst = new byte[src.Length];

            ZlibInflater.TryDecompress(padded, dst, out _).Should().BeTrue();
            dst.Should().Equal(src);
        }

        [Fact]
        public void SteadyState_DoesNotAllocate()
        {
            byte[] src = ZlibCompatTests.Payloads()[6];
            byte[] comp = Compress(src);
            byte[] dst = new byte[src.Length];

            ZlibInflater.TryDecompress(comp, dst, out _);

            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < 100; i++)
            {
                ZlibInflater.TryDecompress(comp, dst, out _);
            }

            (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(1024);
        }
    }
}
