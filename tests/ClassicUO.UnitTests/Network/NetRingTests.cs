using System;
using System.Linq;
using System.Reflection;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network
{
    // net_ring (net-rework step 3): the double-mapped ring itself, on the platform the tests run on (Windows: the
    // placeholder path and the pre-1803 path; Linux: memfd; macOS: shm_open). Linux and macOS have not been run by the
    // owner yet: these tests are what covers them there.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class NetRingTests : IDisposable
    {
        public void Dispose() => NetRing.ForceLegacyWindowsPath = null;

        public static TheoryData<bool?> Paths() => OperatingSystem.IsWindows() ? new TheoryData<bool?> { false, true } : new TheoryData<bool?> { null };

        [Theory]
        [MemberData(nameof(Paths))]
        public void Both_Views_Show_The_Same_Bytes_Across_The_Wrap(bool? legacy)
        {
            NetRing.ForceLegacyWindowsPath = legacy;
            int size = Math.Max(NetRing.MinimumSize, 64 * 1024);
            NetRing.TryCreate(size, out NetRing ring, out string error).Should().BeTrue(error);

            using (ring)
            {
                var rng = new Random(3);
                var expected = new System.Collections.Generic.Queue<byte>();

                // write and read in odd chunks for several laps: every span is contiguous across the wrap
                for (int lap = 0; lap < 40; lap++)
                {
                    int n = rng.Next(1, ring.WritableBytes + 1);
                    var chunk = new byte[n];
                    rng.NextBytes(chunk);
                    chunk.CopyTo(ring.GetWriteSpan());
                    ring.CommitWrite(n);

                    foreach (byte b in chunk)
                    {
                        expected.Enqueue(b);
                    }

                    int take = rng.Next(0, ring.ReadableBytes + 1);
                    Span<byte> read = ring.GetReadSpan();
                    read.Length.Should().Be(expected.Count);

                    for (int i = 0; i < take; i++)
                    {
                        read[i].Should().Be(expected.Dequeue());
                    }

                    ring.CommitRead(take);
                }

                ring.GetReadSpan().ToArray().Should().Equal(expected);
            }
        }

        [Fact]
        public void Full_Empty_And_Discard()
        {
            NetRing.TryCreate(NetRing.MinimumSize, out NetRing ring, out string error).Should().BeTrue(error);

            using (ring)
            {
                ring.ReadableBytes.Should().Be(0);
                ring.WritableBytes.Should().Be(ring.Size - 1);
                ring.CommitWrite(ring.Size - 1);
                ring.WritableBytes.Should().Be(0);
                ((Action)(() => ring.CommitWrite(1))).Should().Throw<ArgumentOutOfRangeException>();
                ring.CommitRead(10);
                ring.DiscardReadable();
                ring.ReadableBytes.Should().Be(0);
                ((Action)(() => ring.CommitRead(1))).Should().Throw<ArgumentOutOfRangeException>();
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3000)]
        [InlineData(6 * 4096)]
        public void A_Bad_Size_Is_Refused_With_A_Reason(int size)
        {
            NetRing.TryCreate(size, out NetRing ring, out string error).Should().BeFalse();
            ring.Should().BeNull();
            error.Should().NotBeNullOrEmpty();
        }

        // The ring size rests on this: one 4 KB raw slice decodes to at most 16 KB, because the shortest code in the decode
        // tree is 2 bits (a symbol still pending from the slice before can end after 1 bit, which keeps it at 4 per byte).
        [Fact]
        public void Huffman_Decodes_At_Most_Four_Bytes_Per_Input_Byte()
        {
            int[] tree = (int[])typeof(Huffman).GetField("_decTree", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

            int Shortest(int node, int depth)
            {
                int best = int.MaxValue;

                for (int bit = 0; bit < 2; bit++)
                {
                    int next = tree[node * 2 + bit];
                    best = Math.Min(best, next <= 0 ? depth + 1 : Shortest(next, depth + 1));
                }

                return best;
            }

            Shortest(0, 0).Should().Be(2);

            // all-zero input is the shortest code (symbol 0x00, "00") four times per byte: exactly the bound
            var h = new Huffman();
            var dest = new byte[NetClient.SliceSize * 4 + 1];
            int size = dest.Length;
            h.Decompress(new byte[NetClient.SliceSize], dest, ref size).Should().BeTrue();
            size.Should().Be(NetClient.SliceSize * 4);
            NetRings.ReceiveSize.Should().BeGreaterThanOrEqualTo(0xFFFF + NetClient.SliceSize * 4 + 1, "one partial packet plus one decoded slice always fit");
        }
    }
}
