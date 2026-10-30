using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ClassicUO.Network;
using ClassicUO.Network.Socket;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network
{
    // L1: NetClient.Drain (the per-frame drain loop) and TcpSocketWrapper.Read (server close while idle).
    public class NetDrainTests
    {
        private sealed class FakeSocket : SocketWrapper
        {
            private readonly Queue<byte[]> _pieces = new();
            private byte[] _current = Array.Empty<byte>();
            private int _pos;
            private bool _connected = true;

            public int Reads;
            public bool CloseWhenDrained;

            public override bool IsConnected => _connected;
            public override EndPoint LocalEndPoint => null;
            public override void Connect(Uri uri) { }
            public override void Send(byte[] buffer, int offset, int count) { }
            public override void Disconnect() => _connected = false;
            public override void Dispose() => _connected = false;

            // a piece is what one socket receive can return at most (a partial read)
            public void Enqueue(byte[] data, int pieceSize = int.MaxValue)
            {
                for (var i = 0; i < data.Length; i += pieceSize)
                {
                    _pieces.Enqueue(data.AsSpan(i, Math.Min(pieceSize, data.Length - i)).ToArray());
                }
            }

            public override int Read(byte[] buffer)
            {
                Reads++;

                if (!_connected)
                    return 0;

                if (_pos >= _current.Length)
                {
                    if (_pieces.Count == 0)
                    {
                        if (CloseWhenDrained)
                        {
                            InvokeOnDisconnected();
                            Disconnect();
                        }

                        return 0;
                    }

                    _current = _pieces.Dequeue();
                    _pos = 0;
                }

                var n = Math.Min(buffer.Length, _current.Length - _pos);
                Array.Copy(_current, _pos, buffer, 0, n);
                _pos += n;

                return n;
            }
        }

        private static byte[] Pattern(int length)
        {
            var data = new byte[length];

            for (var i = 0; i < length; i++)
            {
                data[i] = (byte)(i * 31 + (i >> 8));
            }

            return data;
        }

        private static (NetClient client, FakeSocket socket, List<byte> received, List<int> sliceSizes, Func<ArraySegment<byte>, int> sink, int[] disconnects) Make(int packetsPerSlice = 1)
        {
            var socket = new FakeSocket();
            var client = new NetClient();
            client.AttachSocket(socket);

            var received = new List<byte>();
            var sizes = new List<int>();
            var disconnects = new int[1];
            client.Disconnected += (_, _) => disconnects[0]++;

            return (client, socket, received, sizes, data =>
            {
                if (data.Count == 0) return 0; // the idle pump
                received.AddRange(data.AsSpan());
                sizes.Add(data.Count);

                return packetsPerSlice;
            }, disconnects);
        }

        [Fact]
        public void Drain_Delivers_Partial_Reads_In_Order()
        {
            var (client, socket, received, sizes, sink, _) = Make();
            var data = Pattern(10_000);
            socket.Enqueue(data, 1000); // ten 1000-byte partial reads

            client.Drain(sink, maxMs: 0);

            received.Should().Equal(data);
            sizes.Should().HaveCount(10).And.OnlyContain(s => s == 1000);
            client.LastDrainBytes.Should().Be(10_000);
            client.LastDrainSlices.Should().Be(10);
        }

        [Fact]
        public void Drain_Reads_At_Most_One_Slice_Per_Read()
        {
            var (client, socket, received, sizes, sink, _) = Make();
            var data = Pattern(NetClient.SliceSize * 2 + 100);
            socket.Enqueue(data);

            client.Drain(sink, maxMs: 0);

            received.Should().Equal(data);
            sizes.Should().Equal(NetClient.SliceSize, NetClient.SliceSize, 100);
        }

        [Fact]
        public void Drain_More_Than_64KB_Available_Arrives_In_Slices_Under_The_Huffman_Cap()
        {
            var (client, socket, received, sizes, sink, _) = Make();
            var data = Pattern(200_000);
            socket.Enqueue(data);

            client.Drain(sink, maxBytes: int.MaxValue, maxPackets: int.MaxValue, maxMs: 0);

            received.Should().Equal(data);
            sizes.Should().OnlyContain(s => s <= NetClient.SliceSize);
        }

        [Fact]
        public void Drain_Stops_At_The_Byte_Cap_And_Resumes_Next_Frame()
        {
            var (client, socket, received, _, sink, _) = Make();
            var data = Pattern(200_000);
            socket.Enqueue(data);

            client.Drain(sink, maxBytes: 64 * 1024, maxPackets: int.MaxValue, maxMs: 0);

            client.LastDrainBytes.Should().Be(64 * 1024);
            client.LastDrainSlices.Should().Be(16);
            received.Should().Equal(data.AsSpan(0, 64 * 1024).ToArray());

            // the cap is checked between slices, so it can be overshot by up to one slice
            client.Drain(sink, maxBytes: 100, maxPackets: int.MaxValue, maxMs: 0);
            client.LastDrainBytes.Should().Be(NetClient.SliceSize);

            while (client.Drain(sink, maxBytes: 64 * 1024, maxPackets: int.MaxValue, maxMs: 0) >= 0 && client.LastDrainBytes > 0)
            {
            }

            received.Should().Equal(data);
        }

        [Fact]
        public void Drain_Stops_At_The_Packet_Cap()
        {
            var (client, socket, _, sizes, sink, _) = Make(packetsPerSlice: 2);
            socket.Enqueue(Pattern(100_000));

            var packets = client.Drain(sink, maxBytes: int.MaxValue, maxPackets: 7, maxMs: 0);

            sizes.Should().HaveCount(4); // 2, 4, 6, 8 packets: stops after the slice that reaches the cap
            packets.Should().Be(8);
        }

        [Fact]
        public void Drain_Stops_At_The_Time_Budget_After_At_Least_One_Slice()
        {
            var (client, socket, _, sizes, _, _) = Make();
            socket.Enqueue(Pattern(100_000));

            int Slow(ArraySegment<byte> data)
            {
                sizes.Add(data.Count);
                System.Threading.Thread.Sleep(8);

                return 1;
            }

            client.Drain(Slow, maxBytes: int.MaxValue, maxPackets: int.MaxValue, maxMs: 1);

            sizes.Should().HaveCount(1); // one slice always, the budget (1 ms) is spent by the first

            sizes.Clear();
            client.Drain(Slow, maxBytes: int.MaxValue, maxPackets: int.MaxValue, maxMs: 0); // 0 = no budget
            client.LastDrainBytes.Should().Be(100_000 - NetClient.SliceSize);
        }

        [Fact]
        public void Drain_Returns_Zero_When_Nothing_Is_Ready()
        {
            var (client, socket, _, sizes, sink, disconnects) = Make();

            client.Drain(sink, maxMs: 0).Should().Be(0);

            sizes.Should().BeEmpty();
            socket.Reads.Should().Be(1);
            disconnects[0].Should().Be(0);
        }

        [Fact]
        public void Drain_Close_After_Data_Delivers_The_Data_Then_Raises_Disconnected_Once()
        {
            var (client, socket, received, _, sink, disconnects) = Make();
            var data = Pattern(9_000);
            socket.Enqueue(data);
            socket.CloseWhenDrained = true;

            client.Drain(sink, maxMs: 0);

            received.Should().Equal(data);
            disconnects[0].Should().Be(1);
            client.IsConnected.Should().BeFalse();

            client.Drain(sink, maxMs: 0).Should().Be(0); // nothing more, no second event
            disconnects[0].Should().Be(1);
        }

        [Fact]
        public void Drain_Close_Mid_Packet_Hands_Over_The_Partial_Bytes_And_Disconnects()
        {
            var (client, socket, received, _, sink, disconnects) = Make();
            var partial = new byte[] { 0xA9, 0x01, 0x2C }; // the head of a variable-length packet; its body never arrives
            socket.Enqueue(partial);
            socket.CloseWhenDrained = true;

            client.Drain(sink, maxMs: 0);

            received.Should().Equal(partial);
            disconnects[0].Should().Be(1);
        }

        [Fact]
        public void Drain_Close_While_Idle_Raises_Disconnected_In_One_Call()
        {
            var (client, socket, _, sizes, sink, disconnects) = Make();
            socket.CloseWhenDrained = true;

            client.Drain(sink, maxMs: 0).Should().Be(0);

            sizes.Should().BeEmpty();
            disconnects[0].Should().Be(1);
        }

        // ---- compressed stream ----

        private static byte[] HuffmanEncode(byte[] data)
        {
            var field = typeof(Huffman).GetField("_decTree", BindingFlags.NonPublic | BindingFlags.Static)!;
            var tree = (int[])field.GetValue(null)!;
            var codes = new Dictionary<int, (uint bits, int len)>();

            void Walk(int node, uint bits, int len)
            {
                // tree[node * 2] is taken on a 1 bit, tree[node * 2 + 1] on a 0 bit; a value <= 0 is a leaf
                for (var b = 1; b >= 0; b--)
                {
                    var next = tree[node * 2 + (b == 1 ? 0 : 1)];
                    var nb = (bits << 1) | (uint)b;

                    if (next <= 0)
                        codes[-next] = (nb, len + 1);
                    else
                        Walk(next, nb, len + 1);
                }
            }

            Walk(0, 0, 0);

            var output = new List<byte>();
            uint acc = 0;
            var accBits = 0;

            void Put((uint bits, int len) code)
            {
                for (var i = code.len - 1; i >= 0; i--)
                {
                    acc = (acc << 1) | ((code.bits >> i) & 1);

                    if (++accBits == 8)
                    {
                        output.Add((byte)acc);
                        acc = 0;
                        accBits = 0;
                    }
                }
            }

            foreach (var b in data)
                Put(codes[b]);

            Put(codes[256]); // terminator: the decoder skips the rest of the byte

            if (accBits > 0)
                output.Add((byte)(acc << (8 - accBits)));

            return output.ToArray();
        }

        [Fact]
        public void Drain_Decompresses_More_Than_64KB_Across_Slices_In_Order()
        {
            var (client, socket, received, _, sink, _) = Make();
            var plain = new byte[150_000];

            for (var i = 0; i < plain.Length; i++)
                plain[i] = (byte)(i % 7 == 0 ? 0 : (i % 5 == 0 ? 1 : 0)); // compresses well

            var compressed = HuffmanEncode(plain);
            compressed.Length.Should().BeLessThan(plain.Length / 2);

            client.EnableCompression();
            socket.Enqueue(compressed, 1500); // slices cut the code stream at arbitrary byte boundaries

            client.Drain(sink, maxBytes: int.MaxValue, maxPackets: int.MaxValue, maxMs: 0);

            received.Count.Should().BeGreaterThan(64 * 1024);
            received.Should().Equal(plain);
        }

        // ---- the real TCP wrapper ----

        private static (TcpListener listener, TcpSocketWrapper wrapper, System.Net.Sockets.Socket server, int[] disconnects, List<SocketError> errors) Connect()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var wrapper = new TcpSocketWrapper();
            var disconnects = new int[1];
            var errors = new List<SocketError>();
            wrapper.OnDisconnected += (_, _) => disconnects[0]++;
            wrapper.OnError += (_, e) => errors.Add(e);
            wrapper.Connect(new Uri($"tcp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"));

            var server = listener.AcceptSocket();

            return (listener, wrapper, server, disconnects, errors);
        }

        private static int ReadUntil(TcpSocketWrapper wrapper, byte[] buffer, Func<bool> done)
        {
            var total = 0;
            var deadline = DateTime.UtcNow.AddSeconds(5);

            while (!done() && DateTime.UtcNow < deadline)
            {
                total += wrapper.Read(buffer);
            }

            return total;
        }

        [Fact]
        public void Tcp_Server_Close_While_Idle_Raises_OnDisconnected()
        {
            var (listener, wrapper, server, disconnects, errors) = Connect();

            try
            {
                var buffer = new byte[NetClient.SliceSize];
                wrapper.Read(buffer).Should().Be(0); // idle and open: nothing, no event
                disconnects[0].Should().Be(0);

                server.Close();

                ReadUntil(wrapper, buffer, () => disconnects[0] > 0).Should().Be(0);

                disconnects[0].Should().Be(1);
                errors.Should().BeEmpty();
                wrapper.IsConnected.Should().BeFalse();
                wrapper.Read(buffer).Should().Be(0);
                disconnects[0].Should().Be(1);
            }
            finally
            {
                wrapper.Dispose();
                listener.Stop();
            }
        }

        [Fact]
        public void Tcp_Data_Then_Close_Delivers_All_The_Data_Before_Disconnecting()
        {
            var (listener, wrapper, server, disconnects, _) = Connect();

            try
            {
                var data = Pattern(30_000);
                server.Send(data);
                server.Shutdown(SocketShutdown.Both);
                server.Close();

                var buffer = new byte[NetClient.SliceSize];
                var got = new List<byte>();
                var deadline = DateTime.UtcNow.AddSeconds(5);

                while (disconnects[0] == 0 && DateTime.UtcNow < deadline)
                {
                    var n = wrapper.Read(buffer);
                    got.AddRange(buffer.AsSpan(0, n).ToArray());
                }

                got.Should().Equal(data);
                disconnects[0].Should().Be(1);
            }
            finally
            {
                wrapper.Dispose();
                listener.Stop();
            }
        }

        [Fact]
        public void Tcp_Reset_Raises_A_Disconnect_Or_Error()
        {
            var (listener, wrapper, server, disconnects, errors) = Connect();

            try
            {
                server.LingerState = new LingerOption(true, 0); // close sends RST
                server.Close();

                var buffer = new byte[NetClient.SliceSize];
                ReadUntil(wrapper, buffer, () => disconnects[0] + errors.Count > 0);

                (disconnects[0] + errors.Count).Should().Be(1);
                wrapper.IsConnected.Should().BeFalse();
            }
            finally
            {
                wrapper.Dispose();
                listener.Stop();
            }
        }

        [Fact]
        public void Tcp_Read_Returns_Partial_Data_Up_To_The_Buffer_Size()
        {
            var (listener, wrapper, server, _, _) = Connect();

            try
            {
                server.Send(Pattern(10_000));

                var buffer = new byte[NetClient.SliceSize];
                var total = 0;
                var deadline = DateTime.UtcNow.AddSeconds(5);

                while (total < 10_000 && DateTime.UtcNow < deadline)
                {
                    var n = wrapper.Read(buffer);
                    n.Should().BeLessThanOrEqualTo(buffer.Length);
                    total += n;
                }

                total.Should().Be(10_000);
            }
            finally
            {
                server.Dispose();
                wrapper.Dispose();
                listener.Stop();
            }
        }
    }
}
