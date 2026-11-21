// SPDX-License-Identifier: BSD-2-Clause
#if CUO_DEVTOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Network.Harness
{
    /// <summary>One record of a network capture (net-rework parity harness, CUO_NET_CAPTURE).</summary>
    internal enum CaptureKind : byte
    {
        Connect = 1,
        Inbound = 2,
        Outbound = 3,
        Disconnect = 4
    }

    internal struct CaptureRecord
    {
        public CaptureKind Kind;
        public int Conn;
        public uint Frame;
        public uint Ticks;

        /// <summary>Inbound: the slice was Huffman-compressed when read (the game server's stream).</summary>
        public bool Compressed;

        /// <summary>Inbound: flow packets (<see cref="NetCaptureFormat.IsGate"/>) the client had sent on this connection before the read.</summary>
        public int Gates;

        /// <summary>Outbound: the packet id; its bytes are not stored when <see cref="Redacted"/>.</summary>
        public byte Id;
        public int Length;
        public bool Redacted;

        /// <summary>Inbound: the bytes as read, after decryption, before Huffman. Outbound: the packet before encryption (empty when redacted).</summary>
        public byte[] Data;
    }

    /// <summary>
    /// The capture file. Inbound slices are stored as read from the socket after decryption and before Huffman decompression:
    /// decryption is keyed by the session (and a no-op on ModernUO), so a replay must not depend on it, while decompression
    /// and framing are the receive path the rework replaces and must run again on replay. Chunk boundaries are kept as read.
    /// Login packets that carry the account password (0x80, 0x91, 0xCF) are stored as id and length only.
    /// </summary>
    internal static class NetCaptureFormat
    {
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("CUONETC1");
        public const int Version = 1;

        /// <summary>Client packets whose bytes are never stored (account name and password).</summary>
        public static bool IsRedacted(byte id) => id == 0x80 || id == 0x91 || id == 0xCF;

        /// <summary>
        /// The client packets that move the login flow on (account login, server select, game login, character play or
        /// create, delete). A replay holds inbound data recorded after the n-th of these until the client has sent its n-th.
        /// </summary>
        public static bool IsGate(byte id) => id == 0x80 || id == 0xA0 || id == 0x91 || id == 0x5D || id == 0xF8 || id == 0x00 || id == 0x83;
    }

    internal sealed class CaptureWriter : IDisposable
    {
        private readonly BinaryWriter _w;

        public CaptureWriter(Stream stream, uint clientVersion)
        {
            _w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
            _w.Write(NetCaptureFormat.Magic);
            _w.Write(NetCaptureFormat.Version);
            _w.Write(clientVersion);
        }

        public void Connect(int conn) => Header(CaptureKind.Connect, conn, 0, 0);

        public void Disconnect(int conn, uint frame, uint ticks) => Header(CaptureKind.Disconnect, conn, frame, ticks);

        public void Inbound(int conn, uint frame, uint ticks, bool compressed, int gates, ReadOnlySpan<byte> data)
        {
            Header(CaptureKind.Inbound, conn, frame, ticks);
            _w.Write(compressed);
            _w.Write(gates);
            _w.Write(data.Length);
            _w.Write(data);
        }

        public void Outbound(int conn, uint frame, uint ticks, ReadOnlySpan<byte> packet)
        {
            byte id = packet[0];
            bool redacted = NetCaptureFormat.IsRedacted(id);
            Header(CaptureKind.Outbound, conn, frame, ticks);
            _w.Write(id);
            _w.Write(packet.Length);
            _w.Write(redacted);

            if (!redacted)
            {
                _w.Write(packet);
            }
        }

        private void Header(CaptureKind kind, int conn, uint frame, uint ticks)
        {
            _w.Write((byte)kind);
            _w.Write(conn);
            _w.Write(frame);
            _w.Write(ticks);
        }

        public void Flush() => _w.Flush();

        public void Dispose() => _w.Dispose();
    }

    internal sealed class Capture
    {
        public uint ClientVersion;
        public readonly List<CaptureRecord> Records = new List<CaptureRecord>();

        public static Capture Load(string path)
        {
            using var fs = File.OpenRead(path);

            return Read(fs);
        }

        public static Capture Read(Stream stream)
        {
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            byte[] magic = r.ReadBytes(NetCaptureFormat.Magic.Length);

            if (!magic.AsSpan().SequenceEqual(NetCaptureFormat.Magic))
            {
                throw new InvalidDataException("not a network capture (CUONETC1)");
            }

            int version = r.ReadInt32();

            if (version != NetCaptureFormat.Version)
            {
                throw new InvalidDataException($"capture version {version}, expected {NetCaptureFormat.Version}");
            }

            var cap = new Capture { ClientVersion = r.ReadUInt32() };

            while (true)
            {
                int kind = stream.ReadByte();

                if (kind < 0)
                {
                    break;
                }

                try
                {
                    var rec = new CaptureRecord { Kind = (CaptureKind)kind, Conn = r.ReadInt32(), Frame = r.ReadUInt32(), Ticks = r.ReadUInt32() };

                    switch (rec.Kind)
                    {
                        case CaptureKind.Inbound:
                            rec.Compressed = r.ReadBoolean();
                            rec.Gates = r.ReadInt32();
                            rec.Length = r.ReadInt32();
                            rec.Data = r.ReadBytes(rec.Length);

                            if (rec.Data.Length != rec.Length)
                            {
                                return cap; // a capture cut by a killed client: keep the complete records
                            }

                            break;

                        case CaptureKind.Outbound:
                            rec.Id = r.ReadByte();
                            rec.Length = r.ReadInt32();
                            rec.Redacted = r.ReadBoolean();
                            rec.Data = rec.Redacted ? Array.Empty<byte>() : r.ReadBytes(rec.Length);

                            break;

                        case CaptureKind.Connect:
                        case CaptureKind.Disconnect:
                            break;

                        default:
                            throw new InvalidDataException($"unknown capture record {kind}");
                    }

                    cap.Records.Add(rec);
                }
                catch (EndOfStreamException)
                {
                    break;
                }
            }

            return cap;
        }
    }

    /// <summary>
    /// CUO_NET_CAPTURE=&lt;file&gt;: records the inbound stream (see <see cref="NetCaptureFormat"/>) and the outbound packets of
    /// a live session. Developer-only; the file holds account data (character names, the relay key) and stays in scratch.
    /// Off (the variable unset) every hook is one static bool test.
    /// </summary>
    internal static class NetCapture
    {
        public static readonly bool On = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CUO_NET_CAPTURE")) && !NetReplay.Active;

        private static readonly object _lock = new object();
        private static CaptureWriter _writer;
        private static uint _clientVersion;
        private static int _conn = -1;
        private static int _gates;
        private static long _lastFlush;

        public static void Load(uint clientVersion) => _clientVersion = clientVersion;

        private static CaptureWriter Writer()
        {
            if (_writer == null)
            {
                string path = Environment.GetEnvironmentVariable("CUO_NET_CAPTURE");
                _writer = new CaptureWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16), _clientVersion);
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Close();
                Log.Info($"[net-capture] recording to {path}");
            }

            return _writer;
        }

        public static void Connect()
        {
            lock (_lock)
            {
                _conn++;
                _gates = 0;
                Writer().Connect(_conn);
            }
        }

        public static void Disconnect()
        {
            lock (_lock)
            {
                if (_writer != null)
                {
                    _writer.Disconnect(_conn, Time.Frame, Time.Ticks);
                }
            }
        }

        public static void Inbound(ReadOnlySpan<byte> data, bool compressed)
        {
            lock (_lock)
            {
                Writer().Inbound(_conn, Time.Frame, Time.Ticks, compressed, _gates, data);
                FlushSometimes();
            }
        }

        public static void Outbound(ReadOnlySpan<byte> packet)
        {
            lock (_lock)
            {
                Writer().Outbound(_conn, Time.Frame, Time.Ticks, packet);

                if (NetCaptureFormat.IsGate(packet[0]))
                {
                    _gates++;
                }
            }
        }

        private static void FlushSometimes()
        {
            long now = Environment.TickCount64;

            if (now - _lastFlush >= 1000)
            {
                _lastFlush = now;
                _writer.Flush();
            }
        }

        public static void Close()
        {
            lock (_lock)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }
}
#endif
