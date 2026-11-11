using System;
using System.Collections.Generic;
using System.Net;
using ClassicUO.Game;
using ClassicUO.IO;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Network.Socket;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network
{
    // Packet framing from the socket to the handlers: NetClient.Drain (slices, caps, Huffman) feeding
    // PacketHandlers.ParsePackets (the receive queue, variable-length headers, packets split across slices and frames).
    // Large-scene runs log many "need more data" / "Invalid ID: D6 | off: 1 | len: -1 | stream.pos: 2" lines while
    // 0xD6 floods arrive; these tests check that every split of such a stream still yields exactly the sent packets.
    public class NetFramingTests
    {
        private sealed class PieceSocket : SocketWrapper
        {
            private readonly Queue<byte[]> _pieces = new();
            private byte[] _current = Array.Empty<byte>();
            private int _pos;
            private bool _connected = true;

            public override bool IsConnected => _connected;
            public override EndPoint LocalEndPoint => null;
            public override void Connect(Uri uri) { }
            public override void Send(byte[] buffer, int offset, int count) { }
            public override void Disconnect() => _connected = false;
            public override void Dispose() => _connected = false;

            public bool Empty => _pos >= _current.Length && _pieces.Count == 0;

            public void Enqueue(ReadOnlySpan<byte> piece)
            {
                if (!piece.IsEmpty)
                    _pieces.Enqueue(piece.ToArray());
            }

            // one receive returns at most what is left of the current piece (a partial read)
            public override int Read(byte[] buffer)
            {
                if (!_connected)
                    return 0;

                if (_pos >= _current.Length)
                {
                    if (_pieces.Count == 0)
                        return 0;

                    _current = _pieces.Dequeue();
                    _pos = 0;
                }

                var n = Math.Min(buffer.Length, _current.Length - _pos);
                Array.Copy(_current, _pos, buffer, 0, n);
                _pos += n;

                return n;
            }
        }

        private sealed class Rig
        {
            public readonly NetClient Client = new();
            public readonly PieceSocket Socket = new();
            public readonly PacketHandlers Handlers = new();
            public readonly List<byte[]> Seen = new();
            public int Disconnects;

            public Rig(bool compressed)
            {
                Client.Load(ClientVersion.CV_70130, EncryptionType.NONE);
                Client.AttachSocket(Socket);
                Client.Disconnected += (_, _) => Disconnects++;

                PacketHandlers.OnPacketBufferReader record = (World _, ref StackDataReader p) => Seen.Add(p.Buffer.ToArray());

                foreach (var id in new byte[] { 0xD6, 0xC1, 0x11, 0x78, 0x1D, 0x73, 0x77 })
                    Handlers.Add(id, record);

                if (compressed)
                    Client.EnableCompression();
            }

            public int Sink(ArraySegment<byte> data) => Handlers.ParsePackets(Client, null!, data);

            // frames until the socket is empty, each with the given caps
            public void DrainAll(int maxBytes, int maxPackets)
            {
                for (var frame = 0; frame < 1_000_000; frame++)
                {
                    Client.Drain(Sink, maxBytes, maxPackets, maxMs: 0);

                    if (Socket.Empty)
                    {
                        Client.Drain(Sink, maxBytes, maxPackets, maxMs: 0); // the parser's last pump

                        return;
                    }
                }

                throw new InvalidOperationException("drain never emptied the socket");
            }
        }

        private static byte[] VarPacket(byte id, int length, Random rng)
        {
            var p = new byte[length];
            rng.NextBytes(p);
            p[0] = id;
            p[1] = (byte)(length >> 8);
            p[2] = (byte)length;

            return p;
        }

        private static byte[] FixedPacket(byte id, int length, Random rng)
        {
            var p = new byte[length];
            rng.NextBytes(p);
            p[0] = id;

            return p;
        }

        // A recorded-like 0xD6 flood: MegaCliloc responses of 41..96 bytes, interleaved with the other packets the
        // large scene's arrival sends (0xC1 cliloc messages, 0x11 status, 0x78 mobiles, 0x1D deletes, 0x77 moves,
        // 0x73 pings). Payload bytes are random, so packet ids and header-looking bytes appear inside bodies too.
        private static List<byte[]> Flood(int count, int seed)
        {
            var rng = new Random(seed);
            var packets = new List<byte[]>(count);

            for (var i = 0; i < count; i++)
            {
                packets.Add((i % 9) switch
                {
                    0 => VarPacket(0xC1, 66 + rng.Next(31), rng),
                    1 => VarPacket(0x11, 43, rng),
                    2 => VarPacket(0x78, 19 + rng.Next(80), rng),
                    3 => FixedPacket(0x1D, 5, rng),
                    4 => FixedPacket(0x77, 17, rng),
                    5 => FixedPacket(0x73, 2, rng),
                    _ => VarPacket(0xD6, 41 + rng.Next(56), rng),
                });
            }

            return packets;
        }

        private static byte[] Concat(List<byte[]> packets, bool compressed)
        {
            var stream = new List<byte>();

            // the server compresses each packet on its own and ends it with the terminator code (byte aligned)
            foreach (var p in packets)
                stream.AddRange(compressed ? NetDrainTests.HuffmanEncode(p) : p);

            return stream.ToArray();
        }

        private static void ShouldSeeExactly(Rig rig, List<byte[]> packets, string because)
        {
            rig.Seen.Should().HaveCount(packets.Count, because);

            for (var i = 0; i < packets.Count; i++)
                rig.Seen[i].Should().Equal(packets[i], $"packet {i} ({because})");

            rig.Disconnects.Should().Be(0, because);
        }

        [Fact]
        public void Table_Lengths_Match_The_Test_Stream()
        {
            var table = new PacketsTable(ClientVersion.CV_70130);

            table.GetPacketLength(0xD6).Should().Be(-1);
            table.GetPacketLength(0xC1).Should().Be(-1);
            table.GetPacketLength(0x11).Should().Be(-1);
            table.GetPacketLength(0x78).Should().Be(-1);
            table.GetPacketLength(0x1D).Should().Be(5);
            table.GetPacketLength(0x77).Should().Be(17);
            table.GetPacketLength(0x73).Should().Be(2);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Every_Two_Way_Split_Yields_The_Same_Packets(bool compressed)
        {
            var packets = Flood(12, seed: 1);
            var stream = Concat(packets, compressed);

            for (var cut = 1; cut < stream.Length; cut++)
            {
                var rig = new Rig(compressed);
                rig.Socket.Enqueue(stream.AsSpan(0, cut));
                rig.Client.Drain(rig.Sink, maxMs: 0); // the first part arrives in one frame, the rest in the next
                rig.Socket.Enqueue(stream.AsSpan(cut));
                rig.DrainAll(NetClient.DefaultFrameByteCap, NetClient.DefaultFramePacketCap);

                ShouldSeeExactly(rig, packets, $"cut at {cut}");
                rig.Handlers.PartialPacketWaits.Should().BeLessThanOrEqualTo(1, $"cut at {cut}");
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Every_Piece_Size_Yields_The_Same_Packets(bool compressed)
        {
            var packets = Flood(40, seed: 2);
            var stream = Concat(packets, compressed);

            for (var piece = 1; piece <= 200; piece++)
            {
                var rig = new Rig(compressed);

                for (var i = 0; i < stream.Length; i += piece)
                    rig.Socket.Enqueue(stream.AsSpan(i, Math.Min(piece, stream.Length - i)));

                // one receive per frame: every piece boundary is also a Drain boundary
                rig.DrainAll(maxBytes: 1, maxPackets: int.MaxValue);

                ShouldSeeExactly(rig, packets, $"pieces of {piece}");
            }
        }

        // A variable-length packet whose 3-byte header is cut after 1 or 2 bytes is an ordinary partial packet, like one
        // cut inside its body: it waits for more data. It used to be reported as "Invalid ID: D6 | off: 1 | len: -1",
        // which read as a corrupt stream in the large-scene logs.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(40)]
        public void A_Cut_Variable_Length_Packet_Waits_As_A_Partial_Packet(int cut)
        {
            var rig = new Rig(compressed: false);
            var packet = VarPacket(0xD6, 41, new Random(5));

            rig.Handlers.ParsePackets(rig.Client, null!, packet.AsSpan(0, cut)).Should().Be(0);
            rig.Handlers.PartialPacketWaits.Should().Be(1);

            rig.Handlers.ParsePackets(rig.Client, null!, packet.AsSpan(cut)).Should().Be(1);
            rig.Seen.Should().ContainSingle().Which.Should().Equal(packet);
            rig.Handlers.PartialPacketWaits.Should().Be(1);
            rig.Disconnects.Should().Be(0);
        }

        [Theory]
        [InlineData(false, 128 * 1024, 1500)]
        [InlineData(true, 128 * 1024, 1500)]
        [InlineData(false, 4096, 1)]
        [InlineData(true, 4096, 1)]
        [InlineData(false, 1, 1)]
        [InlineData(true, 1, 1)]
        public void A_Large_Flood_Across_Drain_Caps_Yields_The_Same_Packets(bool compressed, int maxBytes, int maxPackets)
        {
            // about 700 KB raw: several 128 KB frames, many 4 KB slices, packets cut at slice ends
            var packets = Flood(12_000, seed: 3);
            var stream = Concat(packets, compressed);
            var rig = new Rig(compressed);

            // the kernel hands back odd-sized reads
            var rng = new Random(4);

            for (var i = 0; i < stream.Length;)
            {
                var n = Math.Min(1 + rng.Next(9000), stream.Length - i);
                rig.Socket.Enqueue(stream.AsSpan(i, n));
                i += n;
            }

            rig.DrainAll(maxBytes, maxPackets);

            ShouldSeeExactly(rig, packets, $"caps {maxBytes} B / {maxPackets} packets");
        }
    }
}
