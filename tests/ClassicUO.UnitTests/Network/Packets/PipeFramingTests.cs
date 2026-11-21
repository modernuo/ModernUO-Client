using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.Game;
using ClassicUO.Network;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network.Packets
{
    // The receive pipe as a whole (socket reads -> Huffman -> queue -> framing -> dispatch) and the send queue: every packet
    // comes out whole, once, in order, however the stream is cut, and the send side keeps order and every byte under load.
    // These must pass unchanged when the queue or the reader is swapped (span_reader, net_ring).
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class PipeFramingTests : IDisposable
    {
        public void Dispose() => SpanBuffers.ModeOverride = null;

        public static IEnumerable<object[]> Modes() => PacketPipe.Modes();

        // ids the recorder takes over: 0xD6 and 0x1C variable, 0x73 (2), 0x22 (3), 0xA1 (9) fixed in CV_70130
        private static readonly byte[] Ids = { 0xD6, 0x1C, 0x73, 0x22, 0xA1 };

        private static (PacketPipe.Result r, List<byte[]> seen) Run(SpanBufferMode mode, PipeRead read, bool compressed, int splitAt, params byte[][] packets)
        {
            var seen = new List<byte[]>();

            var r = PacketPipe.Deliver(null, mode, read, compressed, h =>
            {
                foreach (byte id in Ids)
                {
                    h.Add(id, (World _, ref NetReader p) => seen.Add(p.Buffer.ToArray()));
                }
            }, splitAt, packets);

            return (r, seen);
        }

        private static byte[] Var(byte id, int length, int seed)
        {
            var p = new byte[length];
            new Random(seed).NextBytes(p);
            p[0] = id;
            p[1] = (byte)(length >> 8);
            p[2] = (byte)length;

            return p;
        }

        private static byte[] Fixed(byte id, int length, int seed)
        {
            var p = new byte[length];
            new Random(seed).NextBytes(p);
            p[0] = id;

            return p;
        }

        private static byte[][] Mixed(int count, int seed)
        {
            var rng = new Random(seed);
            var list = new List<byte[]>();

            for (int i = 0; i < count; i++)
            {
                list.Add(rng.Next(5) switch
                {
                    0 => Fixed(0x73, 2, i),
                    1 => Fixed(0x22, 3, i),
                    2 => Fixed(0xA1, 9, i),
                    3 => Var(0x1C, 3 + rng.Next(0, 200), i),
                    _ => Var(0xD6, 3 + rng.Next(0, 2000), i)
                });
            }

            return list.ToArray();
        }

        private static void ShouldSeeExactly(List<byte[]> seen, byte[][] sent)
        {
            seen.Count.Should().Be(sent.Length);

            for (int i = 0; i < sent.Length; i++)
            {
                seen[i].Should().Equal(sent[i], $"packet {i} (id 0x{sent[i][0]:X2}, {sent[i].Length} bytes)");
            }
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Many_Packets_Of_Every_Shape_Come_Out_Whole_And_In_Order(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            byte[][] sent = Mixed(read == PipeRead.Byte ? 300 : 2000, 11);
            var (r, seen) = Run(mode, read, compressed, -1, sent);

            ShouldSeeExactly(seen, sent);
            r.Disconnects.Should().Be(0);
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void Header_Only_And_Largest_Packets(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            // a header-only variable packet, a 64 KB - 1 packet (the largest a length field holds), a 1-byte-past-4-KB one
            byte[][] sent = { Var(0xD6, 3, 1), Var(0xD6, 0xFFFF, 2), Fixed(0x73, 2, 3), Var(0x1C, 4097, 4), Var(0xD6, 3, 5) };
            var (r, seen) = Run(mode, read, compressed, -1, sent);

            ShouldSeeExactly(seen, sent);
            r.Disconnects.Should().Be(0);
        }

        // every cut of a short stream: split ids, split length fields, split bodies, cuts right after a packet
        [Theory]
        [InlineData(SpanBufferMode.Old, false)]
        [InlineData(SpanBufferMode.Old, true)]
        [InlineData(SpanBufferMode.New, false)]
        [InlineData(SpanBufferMode.New, true)]
        public void A_Stream_Split_At_Every_Position_Gives_The_Same_Packets(SpanBufferMode mode, bool compressed)
        {
            byte[][] sent = { Var(0xD6, 7, 1), Fixed(0x73, 2, 2), Var(0x1C, 3, 3), Fixed(0xA1, 9, 4), Var(0xD6, 300, 5), Fixed(0x22, 3, 6) };
            int wire = compressed ? NetDrainTests.HuffmanEncode(sent.SelectMany(p => p).ToArray()).Length : sent.Sum(p => p.Length);

            for (int cut = 1; cut < wire; cut++)
            {
                var (r, seen) = Run(mode, PipeRead.Max, compressed, cut, sent);
                ShouldSeeExactly(seen, sent);
                r.Disconnects.Should().Be(0, $"cut at {cut}");
            }
        }

        [Theory]
        [MemberData(nameof(Modes))]
        public void A_Length_Below_The_Header_Drops_The_Connection(SpanBufferMode mode, PipeRead read, bool compressed)
        {
            byte[] bad = { 0xD6, 0x00, 0x02 };
            var (r, seen) = Run(mode, read, compressed, -1, Fixed(0x73, 2, 1), bad, Fixed(0x73, 2, 2));

            seen.Should().HaveCount(1);
            r.Disconnects.Should().Be(1);
        }

        [Theory]
        [InlineData(SpanBufferMode.Old)]
        [InlineData(SpanBufferMode.New)]
        public void A_Variable_Packet_Read_Past_Its_End(SpanBufferMode mode)
        {
            // 0xA1 handler (fixed 9) is not involved: a variable 0xD6 whose handler reads 8 bytes from a 4-byte body
            var seen = new List<uint>();
            byte[] shortPacket = Var(0xD6, 7, 1);

            var r = PacketPipe.Deliver(null, mode, PipeRead.Max, false, h =>
                h.Add(0xD6, (World _, ref NetReader p) =>
                {
                    seen.Add(p.ReadUInt32());
                    seen.Add(p.ReadUInt32());
                }), -1, shortPacket, Fixed(0x73, 2, 2));

            if (mode == SpanBufferMode.New)
            {
                r.Disconnects.Should().Be(1, "span_reader: a read past the end ends the connection");
                r.Reason.Should().Contain("0xD6");
            }
            else
            {
                r.Disconnects.Should().Be(0, "the old reader returns zeros past the end");
                seen.Should().HaveCount(2);
            }
        }

        // ---------------------------------------------------------------- send queue

        [Theory]
        [InlineData(SpanBufferMode.Old)]
        [InlineData(SpanBufferMode.New)]
        public void Sends_Keep_Order_And_Every_Byte_Past_Any_Queue_Size(SpanBufferMode mode)
        {
            SpanBuffers.ModeOverride = mode;
            var (client, socket) = OutgoingPacketTests.Connected();
            var expected = new List<byte>();
            var rng = new Random(5);

            // 1 MB without a flush (several times any queue or ring), then a flush
            for (int i = 0; i < 1000; i++)
            {
                byte[] p = Var(0xD6, 3 + rng.Next(0, 2000), i);
                expected.AddRange(p);
                client.Send(p);
            }

            client.Flush();
            socket.Bytes.Should().Equal(expected);

            // and interleaved with flushes
            socket.Bytes.Clear();
            expected.Clear();

            for (int i = 0; i < 200; i++)
            {
                byte[] p = Var(0xD6, 3 + rng.Next(0, 70000 - 3) % 0xFFFC, i);
                expected.AddRange(p);
                client.Send(p);

                if (i % 7 == 0)
                {
                    client.Flush();
                }
            }

            client.Flush();
            socket.Bytes.Should().Equal(expected);
        }
    }
}
