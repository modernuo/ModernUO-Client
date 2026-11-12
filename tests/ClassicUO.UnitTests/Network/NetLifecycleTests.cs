using System;
using System.Collections.Generic;
using System.Linq;
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

            handlers.ParsePackets(client, null!, new byte[] { 0x03, 0x00, 0x03, 0x73, 0x02 }).Should().Be(2); // length 3 = the header alone

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

        // ---- terminal events are handled on the update thread, after the connection's data ----

        private sealed class QueueSocket : SocketWrapper
        {
            private readonly object _lock = new();
            private byte[] _data = Array.Empty<byte>();
            private int _pos;

            public override bool IsConnected => true;
            public override EndPoint LocalEndPoint => null;
            public override void Connect(Uri uri) { }
            public override void Send(byte[] buffer, int offset, int count) { }
            public override void Disconnect() { }
            public override void Dispose() { }

            public void Queue(byte[] data)
            {
                lock (_lock)
                {
                    var rest = _data.AsSpan(_pos).ToArray();
                    _data = rest.Concat(data).ToArray();
                    _pos = 0;
                }
            }

            public void RaiseClose() => InvokeOnDisconnected();
            public void RaiseError(SocketError e) => InvokeOnError(e);

            public override int Read(byte[] buffer)
            {
                lock (_lock)
                {
                    var n = Math.Min(buffer.Length, _data.Length - _pos);
                    Array.Copy(_data, _pos, buffer, 0, n);
                    _pos += n;

                    return n;
                }
            }
        }

        [Fact]
        public void Close_Reported_From_Another_Thread_Still_Delivers_The_Queued_Compressed_Data_First()
        {
            var client = MakeClient();
            var socket = new QueueSocket();
            client.AttachSocket(socket);
            client.EnableCompression();

            var plain = new byte[20_000];

            for (var i = 0; i < plain.Length; i++)
                plain[i] = (byte)(i % 3 == 0 ? 1 : 0);

            socket.Queue(NetDrainTests.HuffmanEncode(plain)); // a game frame the receive task already queued
            System.Threading.Tasks.Task.Run(socket.RaiseClose).Wait(); // ... and then it saw the close, off the update thread

            var order = new List<string>();
            var got = new List<byte>();
            client.Disconnected += (_, _) => order.Add("closed");

            client.Drain(d => { if (d.Count > 0) { got.AddRange(d.AsSpan()); order.Add("data"); } return 0; }, maxMs: 0);

            got.Should().Equal(plain); // decoded as game data, not handed over after compression was reset
            order.Should().Equal(Enumerable.Repeat("data", order.Count - 1).Append("closed"));
            order.Count(o => o == "closed").Should().Be(1);
        }

        [Fact]
        public void Two_Reports_Of_One_Close_Are_One_Disconnected()
        {
            var client = MakeClient();
            var socket = new QueueSocket();
            client.AttachSocket(socket);
            var closed = new List<SocketError>();
            client.Disconnected += (_, e) => closed.Add(e);

            socket.RaiseError(SocketError.SocketError); // the receive exception ...
            socket.RaiseError(SocketError.ConnectionReset); // ... and then the end of the receive loop
            socket.RaiseClose();

            closed.Should().BeEmpty(); // nothing happens off the update thread

            client.Drain(d => 0, maxMs: 0);
            client.Drain(d => 0, maxMs: 0);

            closed.Should().Equal(SocketError.SocketError);
        }

        [Fact]
        public void A_Late_Callback_Of_A_Replaced_Connection_Does_Not_Kill_The_New_One()
        {
            var client = MakeClient();
            var old = new QueueSocket();
            client.AttachSocket(old);
            var replacement = new QueueSocket();
            client.AttachSocket(replacement); // the update thread replaced the connection ...
            var closed = 0;
            client.Disconnected += (_, _) => closed++;

            old.RaiseClose(); // ... then the old socket's task reports its close
            old.RaiseError(SocketError.ConnectionReset);
            replacement.Queue(new byte[] { 0x73, 0x01 });

            var got = new List<byte>();
            client.Drain(d => { got.AddRange(d.AsSpan()); return 0; }, maxMs: 0);

            closed.Should().Be(0);
            got.Should().Equal(0x73, 0x01);
        }

        [Fact]
        public void An_Explicit_Disconnect_Ignores_The_Sockets_Late_Close_Report()
        {
            var client = MakeClient();
            var socket = new QueueSocket();
            client.AttachSocket(socket);
            var closed = 0;
            client.Disconnected += (_, _) => closed++;

            client.Disconnect();
            socket.RaiseClose(); // a late close report arriving after our own Close
            client.Drain(d => 0, maxMs: 0);

            closed.Should().Be(0);
        }

        // ---- plugin injections are tagged with the connection they were made under ----

        [Fact]
        public void Plugin_Packet_Injected_After_Connect_Is_Kept_And_One_Injected_Before_Is_Dropped()
        {
            var (listener, port) = Listen();
            var client = MakeClient();
            var (handlers, seen) = MakeHandlers();

            try
            {
                client.Connect("127.0.0.1", port);
                var server = listener.AcceptSocket();

                handlers.Append(new byte[] { 0x73, 0x01 }, true, client); // injected under the first connection
                client.Disconnect();

                client.Connect("127.0.0.1", port); // new generation ...
                var server2 = listener.AcceptSocket();
                handlers.Append(new byte[] { 0x73, 0x02 }, true, client); // ... injected before the next ParsePackets (a login send hook)

                handlers.ParsePackets(client, null!, Span<byte>.Empty).Should().Be(1);

                seen.Should().ContainSingle().Which.Should().Equal(0x73, 0x02);
                server.Close();
                server2.Close();
            }
            finally
            {
                client.Disconnect();
                listener.Stop();
            }
        }

        // ---- circular buffer ----

        [Fact]
        public void CircularBuffer_Growth_While_Wrapped_Keeps_Order()
        {
            var buffer = new CircularBuffer(4096);
            var expected = new List<byte>();
            byte next = 0;

            byte[] Chunk(int n)
            {
                var c = new byte[n];

                for (var i = 0; i < n; i++)
                    c[i] = next++;

                expected.AddRange(c);

                return c;
            }

            buffer.Enqueue(Chunk(3000));
            var sink = new byte[2500];
            buffer.Dequeue(sink, 0, 2500);
            expected.RemoveRange(0, 2500); // head is now deep into the array
            buffer.Enqueue(Chunk(3000)); // tail wraps around
            buffer.Enqueue(Chunk(9000)); // forces growth while the data wraps

            var all = new byte[buffer.Length];
            buffer.Dequeue(all, 0, all.Length).Should().Be(all.Length);
            all.Should().Equal(expected);
        }

        // ---- third audit: locks, the final frame, stale injections, double termination ----

        [Fact]
        public void Handler_That_Triggers_An_Injecting_Hook_Does_Not_Deadlock_With_A_Background_Injector()
        {
            var client = MakeClient();
            client.AttachSocket(new IdleSocket());
            var handlers = new PacketHandlers();
            var handled = 0;
            var gate = new object(); // stands in for a plugin host's own lock (what Plugin used to take on the Handler)

            // a handler that "sends a response": its outgoing hook takes the plugin lock and injects
            handlers.Add(0x73, (World _, ref StackDataReader p) =>
            {
                System.Threading.Interlocked.Increment(ref handled);

                lock (gate)
                {
                    handlers.Append(new byte[] { 0x04, 0x00 }, true, client); // a 2-byte packet nobody handles
                }
            });

            var stop = false;
            var injector = System.Threading.Tasks.Task.Run(() =>
            {
                while (!System.Threading.Volatile.Read(ref stop))
                {
                    lock (gate)
                    {
                        handlers.Append(new byte[] { 0x73, 0x01 }, true, client);
                    }

                    System.Threading.Thread.Sleep(1);
                }
            });

            var parser = System.Threading.Tasks.Task.Run(() =>
            {
                for (var i = 0; i < 2000; i++)
                {
                    handlers.ParsePackets(client, null!, new byte[] { 0x73, 0x01 });
                }
            });

            var finished = parser.Wait(TimeSpan.FromSeconds(20));
            System.Threading.Volatile.Write(ref stop, true);
            injector.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

            finished.Should().BeTrue("a handler dispatched with a queue lock held would deadlock against the injector");
            handled.Should().BeGreaterThanOrEqualTo(2000);
        }

        [Fact]
        public void A_Frame_Enqueued_Just_After_An_Empty_Read_And_Before_The_Close_Is_Not_Lost()
        {
            var client = MakeClient();
            var socket = new RacingSocket();
            client.AttachSocket(socket);
            var got = new List<byte>();
            var order = new List<string>();
            client.Disconnected += (_, _) => order.Add("closed");

            client.Drain(d => { if (d.Count > 0) { got.AddRange(d.AsSpan()); order.Add("data"); } return 0; }, maxMs: 0);

            got.Should().Equal(0x73, 0x07);
            order.Should().Equal("data", "closed");
        }

        // The first read finds nothing; right after it the receive task enqueues its last frame and reports the close.
        private sealed class RacingSocket : SocketWrapper
        {
            private readonly byte[] _final;
            private int _reads;
            private byte[] _frame = Array.Empty<byte>();
            private int _pos;

            public RacingSocket(byte[] final = null) => _final = final ?? new byte[] { 0x73, 0x07 };

            public override bool IsConnected => true;
            public override EndPoint LocalEndPoint => null;
            public override void Connect(Uri uri) { }
            public override void Send(byte[] buffer, int offset, int count) { }
            public override void Disconnect() { }
            public override void Dispose() { }

            public override int Read(byte[] buffer)
            {
                if (_reads++ == 0)
                {
                    _frame = _final;
                    InvokeOnDisconnected(); // frame enqueued, then the close reported, both after the empty read above
                    return 0;
                }

                var n = Math.Min(buffer.Length, _frame.Length - _pos);
                Array.Copy(_frame, _pos, buffer, 0, n);
                _pos += n;

                return n;
            }
        }

        [Fact]
        public void Plugin_Injection_From_An_Older_Connection_Is_Dropped_Without_Touching_Newer_Packets()
        {
            var client = MakeClient();
            client.AttachSocket(new IdleSocket());
            var (handlers, seen) = MakeHandlers();

            var oldGeneration = client.ConnectionGeneration;
            client.Disconnect(); // reconnect: a new generation
            var newGeneration = client.ConnectionGeneration;
            newGeneration.Should().BeGreaterThan(oldGeneration);

            handlers.ParsePackets(client, null!, Span<byte>.Empty); // the parser has moved to the new generation
            handlers.AppendPlugin(new byte[] { 0x73, 0x02 }, newGeneration); // a new-connection injection
            handlers.AppendPlugin(new byte[] { 0x73, 0x01 }, oldGeneration); // a slow injector that sampled the old generation

            handlers.ParsePackets(client, null!, Span<byte>.Empty).Should().Be(1);

            seen.Should().ContainSingle().Which.Should().Equal(0x73, 0x02);
        }

        [Fact]
        public void Malformed_Plugin_Bytes_At_Close_Give_One_Disconnected()
        {
            var client = MakeClient();
            var socket = new QueueSocket();
            client.AttachSocket(socket);
            var (handlers, seen) = MakeHandlers();
            var closed = new List<SocketError>();
            client.Disconnected += (_, e) => closed.Add(e);

            handlers.Append(new byte[] { 0x03, 0x00, 0x00 }, true, client); // malformed, in the plugin queue
            socket.RaiseClose();

            client.Drain(d => handlers.ParsePackets(client, null!, d), maxMs: 0);
            client.Drain(d => handlers.ParsePackets(client, null!, d), maxMs: 0);

            closed.Should().HaveCount(1);
            closed[0].Should().Be(SocketError.ProtocolNotSupported);
        }

        [Fact]
        public void A_Close_Seen_On_One_Connection_Does_Not_End_The_Connection_That_Replaced_It()
        {
            var client = MakeClient();
            var a = new RacingSocket(new byte[] { 0x73, 0x01 });
            var b = new RacingSocket(new byte[] { 0x73, 0x02 });
            client.AttachSocket(a);
            var got = new List<byte>();
            var closed = 0;
            client.Disconnected += (_, _) => closed++;
            var replaced = false;

            client.Drain(d =>
            {
                if (d.Count == 0)
                    return 0;

                got.AddRange(d.AsSpan());

                if (!replaced)
                {
                    replaced = true;
                    client.AttachSocket(b); // A's final frame was the 0x8C: the handler moves to connection B
                }

                return 0;
            }, maxMs: 0);

            got.Should().Equal(0x73, 0x01, 0x73, 0x02); // B's final frame was read before B was ended
            closed.Should().Be(1);
        }
    }
}
