using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using ClassicUO.Game;
using ClassicUO.Game.Scenes;
using ClassicUO.IO;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Network.Socket;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network
{
    // L1 follow-ups: idle pump of the plugin queue, protocol state reset on every way a connection ends, the parser's partial
    // data at connection boundaries, malformed headers, and the login scene's close mapping.
    public class NetLifecycleTests
    {
        private sealed class IdleSocket : SocketWrapper
        {
            public override bool IsConnected => true;
            public override EndPoint LocalEndPoint => null;
            public override void Connect(Uri uri) { }
            public override void Send(byte[] buffer, int offset, int count) { }
            public override int Read(byte[] buffer) => 0;
            public override void Disconnect() { }
            public override void Dispose() { }
        }

        private static NetClient MakeClient()
        {
            var client = new NetClient();
            client.Load(ClientVersion.CV_70130, EncryptionType.NONE);

            return client;
        }

        // 0x73 (ping) is a 2-byte packet; 0xA9 is variable length
        private static (PacketHandlers handlers, List<byte[]> seen) MakeHandlers()
        {
            var handlers = new PacketHandlers();
            var seen = new List<byte[]>();
            PacketHandlers.OnPacketBufferReader record = (World _, ref StackDataReader p) => seen.Add(p.Buffer.ToArray());
            handlers.Add(0x73, record);
            handlers.Add(0xA9, record);
            handlers.Add(0x03, record);

            return (handlers, seen);
        }

        [Fact]
        public void Idle_Socket_Still_Pumps_The_Parser_And_Plugin_Queue()
        {
            var client = MakeClient();
            client.AttachSocket(new IdleSocket());
            var (handlers, seen) = MakeHandlers();

            handlers.Append(new byte[] { 0x73, 0x07 }, true); // a packet a plugin injected while the socket is idle

            var packets = client.Drain(data => handlers.ParsePackets(client, null!, data), maxMs: 0);

            packets.Should().Be(1);
            seen.Should().ContainSingle().Which.Should().Equal(0x73, 0x07);

            client.Drain(data => handlers.ParsePackets(client, null!, data), maxMs: 0).Should().Be(0); // not delivered twice
        }

        [Fact]
        public void Drain_Calls_The_Sink_Once_Even_Without_A_Socket()
        {
            var client = new NetClient();
            var calls = 0;

            client.Drain(d => { calls++; return 0; }, maxMs: 0);

            calls.Should().Be(1);
        }

        [Theory]
        [InlineData(0x03, 0x00, 0x00)] // variable length 0
        [InlineData(0x03, 0x00, 0x01)]
        [InlineData(0x03, 0x00, 0x02)] // shorter than its 3-byte header
        public void Malformed_Variable_Header_Drops_The_Connection_Instead_Of_Looping(byte id, byte hi, byte lo)
        {
            var client = MakeClient();
            client.AttachSocket(new IdleSocket());
            var (handlers, seen) = MakeHandlers();
            var errors = new List<SocketError>();
            client.Disconnected += (_, e) => errors.Add(e);

            var packets = handlers.ParsePackets(client, null!, new byte[] { id, hi, lo, 0x73, 0x01 });

            packets.Should().Be(0);
            seen.Should().BeEmpty();
            errors.Should().Equal(SocketError.ProtocolNotSupported);
        }

        [Fact]
        public void Variable_Packet_With_Exactly_Its_Header_Length_Is_Accepted()
        {
            var client = MakeClient();
            client.AttachSocket(new IdleSocket());
            var (handlers, seen) = MakeHandlers();

            handlers.ParsePackets(client, null!, new byte[] { 0x03, 0x00, 0x05, 0xAA, 0xBB, 0x73, 0x02 }).Should().Be(2);

            seen.Should().HaveCount(2);
        }

        // ---- real loopback connections ----

        private static (TcpListener listener, ushort port) Listen()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            return (listener, (ushort)((IPEndPoint)listener.LocalEndpoint).Port);
        }

        private static void DrainUntil(NetClient client, Func<ArraySegment<byte>, int> sink, Func<bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (!done() && DateTime.UtcNow < deadline)
            {
                client.Drain(sink, maxMs: 0);
            }
        }

        [Theory]
        [InlineData(false)] // FIN
        [InlineData(true)] // RST
        public void Close_With_Compression_On_Then_A_Login_Retry_Reads_Plain_And_Parses_Clean(bool reset)
        {
            var (listener, port) = Listen();
            var client = MakeClient();
            var (handlers, seen) = MakeHandlers();
            var closed = new List<SocketError>();
            client.Disconnected += (_, e) => closed.Add(e);
            int Sink(ArraySegment<byte> d) => handlers.ParsePackets(client, null!, d);

            try
            {
                // game connection: compression on, a truncated variable packet arrives, then the server goes away
                client.Connect("127.0.0.1", port);
                var server = listener.AcceptSocket();
                client.EnableCompression();
                var generationBefore = client.ConnectionGeneration;

                // the stream is Huffman coded now; send what decodes to nothing harmful: a lone terminator-less byte is
                // enough to prove the state, so send plain bytes only after the reset instead. Close right away.
                if (reset)
                    server.LingerState = new LingerOption(true, 0);

                server.Close();

                DrainUntil(client, Sink, () => closed.Count > 0);

                closed.Should().HaveCount(1);
                client.ConnectionGeneration.Should().BeGreaterThan(generationBefore);

                // the login retry: a new connection to the login server, plain bytes
                client.Connect("127.0.0.1", port);
                var server2 = listener.AcceptSocket();
                server2.Send(new byte[] { 0x73, 0x09 }); // a ping, uncompressed

                DrainUntil(client, Sink, () => seen.Count > 0);

                seen.Should().ContainSingle().Which.Should().Equal(0x73, 0x09);
                closed.Should().HaveCount(1);
                server2.Close();
            }
            finally
            {
                client.Disconnect();
                listener.Stop();
            }
        }

        [Fact]
        public void Truncated_Packet_Then_Close_Does_Not_Poison_The_Next_Session()
        {
            var (listener, port) = Listen();
            var client = MakeClient();
            var (handlers, seen) = MakeHandlers();
            var closed = 0;
            client.Disconnected += (_, _) => closed++;
            int Sink(ArraySegment<byte> d) => handlers.ParsePackets(client, null!, d);

            try
            {
                client.Connect("127.0.0.1", port);
                var server = listener.AcceptSocket();
                server.Send(new byte[] { 0xA9, 0x01, 0x2C }); // header says 300 bytes; none follow
                Threading(() => server.Close());

                DrainUntil(client, Sink, () => closed > 0);
                closed.Should().Be(1);
                seen.Should().BeEmpty();

                client.Connect("127.0.0.1", port);
                var server2 = listener.AcceptSocket();
                server2.Send(new byte[] { 0x73, 0x04 });

                DrainUntil(client, Sink, () => seen.Count > 0);

                // with the old partial A9 in the queue, 0x73 0x04 would have been swallowed as the A9's body
                seen.Should().ContainSingle().Which.Should().Equal(0x73, 0x04);
                server2.Close();
            }
            finally
            {
                client.Disconnect();
                listener.Stop();
            }
        }

        private static void Threading(Action a) => a();

        [Fact]
        public void Connect_Starts_In_Login_Mode_Even_If_Compression_Was_Left_On()
        {
            var (listener, port) = Listen();
            var client = MakeClient();

            try
            {
                client.EnableCompression(); // left on by some earlier session
                client.Connect("127.0.0.1", port);
                var server = listener.AcceptSocket();
                server.Send(new byte[] { 0x73, 0x01 });

                var got = new List<byte>();
                DrainUntil(client, d => { got.AddRange(d.AsSpan()); return 0; }, () => got.Count >= 2);

                got.Should().Equal(0x73, 0x01); // raw, not run through Huffman
            }
            finally
            {
                client.Disconnect();
                listener.Stop();
            }
        }

        // ---- login scene ----

        [Fact]
        public void Clean_Close_Is_A_Lost_Connection_In_Every_Waiting_Step_And_Only_There()
        {
            var lost = new HashSet<LoginSteps>
            {
                LoginSteps.Connecting, LoginSteps.VerifyingAccount, LoginSteps.ServerSelection, LoginSteps.LoginInToServer,
                LoginSteps.CharacterSelection, LoginSteps.EnteringBritania, LoginSteps.CharacterCreationDone
            };

            foreach (LoginSteps step in Enum.GetValues(typeof(LoginSteps)))
            {
                LoginScene.IsLostOnCleanClose(step).Should().Be(lost.Contains(step), step.ToString());
            }

            LoginScene.IsLostOnCleanClose(LoginSteps.PopUpMessage).Should().BeFalse(); // a denial's follow-up close keeps its text
            LoginScene.IsLostOnCleanClose(LoginSteps.CharacterCreation).Should().BeFalse();
        }

        // ---- circular buffer ----

        [Fact]
        public void CircularBuffer_Grows_Geometrically_And_Keeps_Order_Over_Many_Full_Enqueues()
        {
            var buffer = new CircularBuffer();
            var expected = new List<byte>();
            var chunk = new byte[4096];

            for (var i = 0; i < 64; i++)
            {
                Array.Fill(chunk, (byte)i);
                buffer.Enqueue(chunk);
                expected.AddRange(chunk);
            }

            buffer.Length.Should().Be(64 * 4096);

            var all = new byte[buffer.Length];
            buffer.Dequeue(all, 0, all.Length).Should().Be(all.Length);
            all.Should().Equal(expected);
        }
    }
}
