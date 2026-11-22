using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Network.Socket;
using ClassicUO.UnitTests.Network.Packets;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network
{
    // net_ring's bounded send ring: packets keep their order and every byte across inline flushes when the ring fills
    // between frames, and a socket that takes nothing ends the connection ("Send buffer exhausted") instead of growing.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class NetRingSendTests : IDisposable
    {
        public void Dispose()
        {
            NetRings.OnOverride = null;
            NetRings.SendSizeOverride = null;
#if CUO_DEVTOOLS
            NetRings.CheckOverride = null;
#endif
        }

        private sealed class Socket : SocketWrapper
        {
            public readonly List<byte> Bytes = new();
            public int Sends;
            public bool Stuck;
            private bool _connected = true;
            public override bool IsConnected => _connected;
            public override EndPoint LocalEndPoint => null;
            public override void Connect(Uri uri) { }
            public override int Read(byte[] buffer) => 0;
            public override void Disconnect() => _connected = false;
            public override void Dispose() => _connected = false;

            public override void Send(byte[] buffer, int offset, int count)
            {
                if (Stuck)
                {
                    throw new SocketException((int)SocketError.TimedOut);
                }

                Sends++;
                Bytes.AddRange(new ArraySegment<byte>(buffer, offset, count));
            }
        }

        private static (NetClient client, Socket socket) Connected(int sendSize)
        {
            NetRings.OnOverride = true;
            NetRings.SendSizeOverride = sendSize;
            var client = new NetClient();
            client.Load(ClientVersion.CV_70130, EncryptionType.NONE);
            var socket = new Socket();
            client.AttachSocket(socket);
            client.EndHandshake(client.BeginGameHandshake(0x0A0B0C0D));
            client.Flush();
            socket.Bytes.Clear();
            client.ReceiveRing.Should().NotBeNull("net_ring maps its rings on attach");

            return (client, socket);
        }

        private static byte[] Packet(int length, int seed)
        {
            var p = new byte[length];
            new Random(seed).NextBytes(p);
            p[0] = 0xD7;
            p[1] = (byte)(length >> 8);
            p[2] = (byte)length;

            return p;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Order_And_Bytes_Survive_Inline_Flushes(bool check)
        {
#if CUO_DEVTOOLS
            NetRings.CheckOverride = check;
            NetRings.ResetCheckForTests();
#endif
            int size = NetRing.MinimumSize;
            var (client, socket) = Connected(size);
            var rng = new Random(5);
            var sent = new List<byte[]>();
            int total = 0;

            // several rings' worth before one Flush: each send that does not fit flushes the ring first
            while (total < size * 4)
            {
                byte[] p = Packet(3 + rng.Next(0, Math.Min(size / 2, 3000)), sent.Count);
                sent.Add(p);
                total += p.Length;
                client.Send(p);
            }

            socket.Sends.Should().BeGreaterThan(0, "the ring filled and was flushed inline");
            client.Flush();

            socket.Bytes.Should().Equal(sent.SelectMany(p => p), "every byte, in send order");
            client.IsConnected.Should().BeTrue();
#if CUO_DEVTOOLS
            if (check)
            {
                NetRings.Mismatches.Should().Be(0);
                NetRings.CheckedSends.Should().BeGreaterThan(0);
            }
#endif
        }

        [Fact]
        public void A_Packet_Of_The_Ring_Capacity_Is_Sent()
        {
            int size = NetRing.MinimumSize;
            var (client, socket) = Connected(size);
            byte[] small = Packet(10, 1);
            byte[] whole = Packet(size - 1, 2); // the ring keeps one byte free

            client.Send(small);
            client.Send(whole);
            client.Flush();

            socket.Bytes.Should().Equal(small.Concat(whole));
            client.IsConnected.Should().BeTrue();
        }

        [Fact]
        public void A_Packet_Larger_Than_The_Ring_Ends_The_Connection()
        {
            // the client's send ring (128 KB) holds any packet (at most 64 KB); a test ring of one page does not
            int size = NetRing.MinimumSize;
            var (client, socket) = Connected(size);
            string reason = null;
            client.Disconnected += (_, _) => reason = client.DisconnectReason;

            client.Send(Packet(size, 3));
            client.Flush();

            reason.Should().Be("Send buffer exhausted");
            client.IsConnected.Should().BeFalse();
            socket.Bytes.Should().BeEmpty();
        }

        [Fact]
        public void A_Socket_That_Takes_Nothing_Ends_The_Connection()
        {
            int size = NetRing.MinimumSize;
            var (client, socket) = Connected(size);
            int disconnects = 0;
            SocketError error = SocketError.Success;
            client.Disconnected += (_, e) =>
            {
                disconnects++;
                error = e;
            };
            socket.Stuck = true;

            for (int i = 0; i < 4 * size / 1000; i++)
            {
                client.Send(Packet(1000, i));
            }

            client.Flush();

            disconnects.Should().Be(1);
            client.IsConnected.Should().BeFalse();
            error.Should().Be(SocketError.TimedOut, "the flush at the end of the frame hits the socket's timeout first");
            socket.Bytes.Should().BeEmpty();
        }
    }
}
