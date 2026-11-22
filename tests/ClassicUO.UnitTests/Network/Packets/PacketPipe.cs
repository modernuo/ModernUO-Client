using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using ClassicUO.Game;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Network.Socket;
using ClassicUO.Utility;

namespace ClassicUO.UnitTests.Network.Packets
{
    /// <summary>How the pipe's socket cuts the stream into reads.</summary>
    public enum PipeRead
    {
        /// <summary>One byte per read.</summary>
        Byte,

        /// <summary>Seeded random sizes from 1 to the read buffer.</summary>
        Random,

        /// <summary>Each read fills the read buffer.</summary>
        Max,

        /// <summary>net_ring: one byte per read into the 256 KB receive ring.</summary>
        RingByte,

        /// <summary>net_ring: random reads into the 256 KB receive ring.</summary>
        RingRandom,

        /// <summary>net_ring: full reads into the 256 KB receive ring.</summary>
        RingMax,

        /// <summary>net_ring: random reads into the smallest ring that holds the largest packet twice, so it wraps every few packets.</summary>
        TinyRing
    }

    /// <summary>
    /// Incoming packets through the whole receive path: a socket that cuts the stream as <see cref="PipeRead"/> says (Huffman
    /// encoded when <c>compressed</c>), NetClient.Drain and PacketHandlers.ParsePackets with the client's real handler table,
    /// on a test World. The same tests run in every read mode, compressed or not, and with span_reader off and on.
    /// </summary>
    internal static class PacketPipe
    {
        public static readonly ClientVersion Version = ClientVersion.CV_70130;

        public sealed class Result
        {
            public int Packets;
            public int Disconnects;
            public string Reason;

            /// <summary>What the handlers sent back through NetClient.Socket (the pipe's client while it delivers), flushed.</summary>
            public byte[] Sent = Array.Empty<byte>();

            /// <summary>The client received through the net_ring receive ring (false: the CircularBuffer path).</summary>
            public bool Ring;
        }

        private static readonly PropertyInfo StaticSocket = typeof(NetClient).GetProperty(nameof(NetClient.Socket))!;

        public static IEnumerable<object[]> Modes()
        {
            foreach (var mode in new[] { SpanBufferMode.Old, SpanBufferMode.New })
            foreach (var read in Enum.GetValues<PipeRead>())
            foreach (var compressed in new[] { false, true })
            {
                yield return new object[] { mode, read, compressed };
            }
        }

        private static readonly PacketHandlers.OnPacketBufferReader[] RealHandlers =
            (PacketHandlers.OnPacketBufferReader[])typeof(PacketHandlers)
                .GetField("_handlers", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(PacketHandlers.Handler)!;

        /// <summary>The real handler of <paramref name="id"/> (null when the client registers none).</summary>
        public static PacketHandlers.OnPacketBufferReader Real(byte id) => RealHandlers[id];

        public static Result Deliver(World world, SpanBufferMode mode, PipeRead read, bool compressed, params byte[][] packets) =>
            Deliver(world, mode, read, compressed, null, packets);

        public static Result Deliver(World world, SpanBufferMode mode, PipeRead read, bool compressed, Action<PacketHandlers> setup, params byte[][] packets) =>
            Deliver(world, mode, read, compressed, setup, -1, packets);

        /// <param name="splitAt">Wire position the first read ends at (-1: none); the read mode applies after it.</param>
        public static Result Deliver(World world, SpanBufferMode mode, PipeRead read, bool compressed, Action<PacketHandlers> setup, int splitAt, params byte[][] packets)
        {
            SpanBuffers.ModeOverride = mode;
            bool ring = read >= PipeRead.RingByte;
            NetRings.OnOverride = ring;
            NetRings.ReceiveSizeOverride = read == PipeRead.TinyRing ? TinySize(packets) : null;

            try
            {
                return DeliverOnce(world, read, compressed, setup, splitAt, packets);
            }
            finally
            {
                NetRings.OnOverride = null;
                NetRings.ReceiveSizeOverride = null;
            }
        }

        private static int TinySize(byte[][] packets)
        {
            int largest = 0;

            foreach (byte[] p in packets)
            {
                largest = Math.Max(largest, p.Length);
            }

            int size = NetRing.MinimumSize;

            // a partial packet plus one decoded slice (4 bytes per compressed byte) must fit
            while (size <= largest * 2 + 64)
            {
                size *= 2;
            }

            return size;
        }

        private static Result DeliverOnce(World world, PipeRead read, bool compressed, Action<PacketHandlers> setup, int splitAt, byte[][] packets)
        {
            byte[] plain = packets.SelectMany(p => p).ToArray();
            byte[] wire = compressed ? NetDrainTests.HuffmanEncode(plain) : plain;

            var client = new NetClient();
            // the packet lengths of the version the test seam runs (GameSeam.UseVersion), else 7.0.13
            client.Load(Client.Game?.UO?.Version ?? Version, EncryptionType.NONE);
            var socket = new ModeSocket(wire, read, seed: wire.Length, splitAt);
            client.AttachSocket(socket);

            if (compressed)
            {
                client.EnableCompression();
            }

            var result = new Result();
            client.Disconnected += (_, _) =>
            {
                result.Disconnects++;
                result.Reason = client.DisconnectReason;
            };

            var handlers = new PacketHandlers();

            for (int i = 0; i < 256; i++)
            {
                if (RealHandlers[i] != null)
                {
                    handlers.Add((byte)i, RealHandlers[i]);
                }
            }

            setup?.Invoke(handlers);
            int Sink(ArraySegment<byte> data) => handlers.ParsePackets(client, world, data);

            // handlers answer through the static NetClient.Socket: the pipe's client while it delivers
            object previous = StaticSocket.GetValue(null);
            StaticSocket.SetValue(null, client);

            try
            {
                for (int frame = 0; frame < 10_000_000 && !socket.Empty && result.Disconnects == 0; frame++)
                {
                    result.Packets += client.Drain(Sink, int.MaxValue, int.MaxValue, 0);
                }

                result.Packets += client.Drain(Sink, int.MaxValue, int.MaxValue, 0);
                client.Flush();
            }
            finally
            {
                StaticSocket.SetValue(null, previous);
            }

            result.Sent = socket.Sent.ToArray();
            result.Ring = client.ReceiveRing != null;

            return result;
        }

        private sealed class ModeSocket : SocketWrapper
        {
            private readonly byte[] _data;
            private readonly PipeRead _read;
            private readonly Random _rng;
            private int _pos;
            private bool _connected = true;

            private readonly int _splitAt;

            public ModeSocket(byte[] data, PipeRead read, int seed, int splitAt)
            {
                _splitAt = splitAt;
                _data = data;
                _read = read;
                _rng = new Random(seed);
            }

            public readonly List<byte> Sent = new();
            public bool Empty => _pos >= _data.Length;
            public override bool IsConnected => _connected;
            public override EndPoint LocalEndPoint => null;
            public override void Connect(Uri uri) { }
            public override void Send(byte[] buffer, int offset, int count) => Sent.AddRange(new ArraySegment<byte>(buffer, offset, count));
            public override void Disconnect() => _connected = false;
            public override void Dispose() => _connected = false;

            public override int Read(byte[] buffer)
            {
                if (!_connected || _pos >= _data.Length)
                {
                    return 0;
                }

                int want = _read switch
                {
                    PipeRead.Byte or PipeRead.RingByte => 1,
                    PipeRead.Random or PipeRead.RingRandom or PipeRead.TinyRing => _rng.Next(1, buffer.Length + 1),
                    _ => buffer.Length
                };
                if (_pos < _splitAt)
                {
                    want = Math.Min(want, _splitAt - _pos);
                }

                int n = Math.Min(want, _data.Length - _pos);
                Array.Copy(_data, _pos, buffer, 0, n);
                _pos += n;

                return n;
            }
        }
    }

    /// <summary>Builds a packet the way the server writes it (big endian; a variable-length packet's length is patched in Build).</summary>
    internal sealed class PacketBuilder
    {
        private readonly List<byte> _b = new();
        private readonly bool _variable;

        public PacketBuilder(byte id, bool variable)
        {
            _variable = variable;
            _b.Add(id);

            if (variable)
            {
                _b.Add(0);
                _b.Add(0);
            }
        }

        public int Length => _b.Count;

        public PacketBuilder U8(int v)
        {
            _b.Add((byte)v);

            return this;
        }

        public PacketBuilder I8(int v) => U8((byte)(sbyte)v);
        public PacketBuilder U16(int v) => U8(v >> 8).U8(v);
        public PacketBuilder U32(uint v) => U16((int)(v >> 16)).U16((int)(v & 0xFFFF));

        public PacketBuilder Bytes(params byte[] bytes)
        {
            _b.AddRange(bytes);

            return this;
        }

        /// <summary>Cp1252 text, padded with zeros (or cut) to <paramref name="fixedLength"/>; -1 = the text and a terminating zero.</summary>
        public PacketBuilder Ascii(string s, int fixedLength = -1)
        {
            byte[] a = Encoding.Latin1.GetBytes(s);

            if (fixedLength < 0)
            {
                _b.AddRange(a);
                _b.Add(0);

                return this;
            }

            for (int i = 0; i < fixedLength; i++)
            {
                _b.Add(i < a.Length ? a[i] : (byte)0);
            }

            return this;
        }

        /// <summary>UTF-16 big endian, then a 2-byte zero when <paramref name="terminate"/>.</summary>
        public PacketBuilder UnicodeBE(string s, bool terminate = true)
        {
            _b.AddRange(Encoding.BigEndianUnicode.GetBytes(s));

            if (terminate)
            {
                _b.Add(0);
                _b.Add(0);
            }

            return this;
        }

        /// <summary>UTF-16 little endian, then a 2-byte zero when <paramref name="terminate"/>.</summary>
        public PacketBuilder UnicodeLE(string s, bool terminate = true)
        {
            _b.AddRange(Encoding.Unicode.GetBytes(s));

            if (terminate)
            {
                _b.Add(0);
                _b.Add(0);
            }

            return this;
        }

        public PacketBuilder Zero(int count)
        {
            for (int i = 0; i < count; i++)
            {
                _b.Add(0);
            }

            return this;
        }

        /// <summary>The bytes; <paramref name="cut"/> drops that many from the end after the length is set (a short packet).</summary>
        public byte[] Build(int cut = 0)
        {
            byte[] a = _b.ToArray();

            if (_variable)
            {
                int len = a.Length - cut;
                a[1] = (byte)(len >> 8);
                a[2] = (byte)len;
            }

            return a.AsSpan(0, a.Length - cut).ToArray();
        }
    }
}
