#if CUO_DEVTOOLS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClassicUO.Network.Harness;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Network
{
    // net-rework step 1: the parity harness. A capture replayed headless through NetClient.Drain and
    // PacketHandlers.ParsePackets must give the same trace whatever the read sizes (as recorded, 1 byte, random, the full
    // read buffer), and that trace must list exactly the packets the stream holds.
    public class NetReplayHarnessTests
    {
        private const uint Version = (uint)ClientVersion.CV_70130;

        private static readonly ChunkMode[] Modes = { ChunkMode.Recorded, ChunkMode.Byte, ChunkMode.Random, ChunkMode.Max };

        private static byte[] Var(byte id, int length, Random rng)
        {
            var p = new byte[length];
            rng.NextBytes(p);
            p[0] = id;
            p[1] = (byte)(length >> 8);
            p[2] = (byte)length;

            return p;
        }

        private static byte[] Fixed(byte id, int length, Random rng)
        {
            var p = new byte[length];
            rng.NextBytes(p);
            p[0] = id;

            return p;
        }

        // the stream cut into recorded reads at the given offsets; reads are grouped into frames of framesPer reads
        internal static void AddStream(CaptureWriter w, int conn, byte[] stream, bool compressed, IEnumerable<int> cuts, ref uint frame, int framesPer = 1)
        {
            int prev = 0, n = 0;

            foreach (int cut in cuts.Where(c => c > 0 && c < stream.Length).Distinct().OrderBy(c => c).Append(stream.Length))
            {
                w.Inbound(conn, frame, frame * 16, compressed, 0, stream.AsSpan(prev, cut - prev));
                prev = cut;

                if (++n % framesPer == 0)
                {
                    frame++;
                }
            }

            frame++;
        }

        internal static byte[] Concat(IEnumerable<byte[]> packets, bool compressed) =>
            packets.SelectMany(p => compressed ? NetDrainTests.HuffmanEncode(p) : p).ToArray();

        private static Capture Build(Action<CaptureWriter> write)
        {
            var ms = new MemoryStream();

            using (var w = new CaptureWriter(ms, Version))
            {
                write(w);
                w.Flush();
                ms = new MemoryStream(ms.ToArray());
            }

            return Capture.Read(ms);
        }

        private static string Trace(Capture cap, ChunkMode mode, int seed = 1234)
        {
            var sw = new StringWriter();
            HeadlessReplay.Run(cap, mode, sw, seed);

            // the header names the mode; the rest must match across modes
            return string.Join("\n", sw.ToString().Split('\n').Where(l => !l.StartsWith("#")));
        }

        private static List<(byte Id, int Len, string Hash)> Packets(string trace) =>
            trace.Split('\n').Where(l => l.StartsWith("P ")).Select(l => l.Split(' '))
                .Select(f => (Convert.ToByte(f[2], 16), int.Parse(f[3]), f[4])).ToList();

        private static void ShouldReplayExactly(Capture cap, IReadOnlyList<byte[]> packets)
        {
            string reference = Trace(cap, ChunkMode.Recorded);
            var seen = Packets(reference);
            seen.Should().HaveCount(packets.Count);

            for (int i = 0; i < packets.Count; i++)
            {
                seen[i].Id.Should().Be(packets[i][0], $"packet {i}");
                seen[i].Len.Should().Be(packets[i].Length, $"packet {i}");
                seen[i].Hash.Should().Be(NetTrace.Hash(packets[i]).ToString("X16"), $"packet {i}");
            }

            foreach (ChunkMode mode in Modes)
            {
                Trace(cap, mode).Should().Be(reference, $"mode {mode}");
            }

            Trace(cap, ChunkMode.Random, seed: 99).Should().Be(reference, "another random cut");
        }

        // every framing edge in one stream: a variable-length header cut after its id and after its first length byte, a
        // body cut, a header-only packet (length 3), fixed packets cut, a 65535-byte packet
        private static List<byte[]> EdgePackets()
        {
            var rng = new Random(7);

            return new List<byte[]>
            {
                Var(0xD6, 40, rng),
                Var(0xD6, 3, rng),          // zero-length payload
                Fixed(0x1D, 5, rng),
                Var(0x11, 70, rng),
                Fixed(0x73, 2, rng),
                Var(0xC1, 3, rng),          // zero-length payload
                Var(0x11, 0xFFFF, rng),     // the largest length a header can carry
                Fixed(0x77, 17, rng),
                Var(0xD6, 96, rng)
            };
        }

        [Fact]
        public void Edge_Stream_Cut_At_Every_Header_Byte_Replays_Exactly()
        {
            List<byte[]> packets = EdgePackets();
            byte[] stream = Concat(packets, false);
            var cuts = new List<int>();
            int at = 0;

            foreach (byte[] p in packets)
            {
                cuts.Add(at + 1); // after the id
                cuts.Add(at + 2); // inside the length
                cuts.Add(at + Math.Min(3, p.Length));
                at += p.Length;
            }

            uint frame = 1;
            Capture cap = Build(w =>
            {
                w.Connect(0);
                AddStream(w, 0, stream, false, cuts, ref frame);
            });

            ShouldReplayExactly(cap, packets);
        }

        [Fact]
        public void Edge_Stream_Compressed_Cut_Anywhere_Replays_Exactly()
        {
            List<byte[]> packets = EdgePackets();
            byte[] stream = Concat(packets, true);
            var rng = new Random(3);
            uint frame = 1;

            Capture cap = Build(w =>
            {
                w.Connect(0);
                AddStream(w, 0, stream, true, Enumerable.Range(0, 200).Select(_ => rng.Next(1, stream.Length)), ref frame, framesPer: 3);
            });

            ShouldReplayExactly(cap, packets);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void D6_Burst_Replays_Exactly(bool compressed)
        {
            var rng = new Random(11);
            var packets = new List<byte[]>();

            for (int i = 0; i < 6000; i++)
            {
                packets.Add(i % 50 == 49 ? Fixed(0x73, 2, rng) : Var(0xD6, rng.Next(41, 97), rng));
            }

            byte[] stream = Concat(packets, compressed);
            uint frame = 1;

            // reads of the socket's 4 KB slice, a frame each, as a capture of the large scene's arrival has them
            Capture cap = Build(w =>
            {
                w.Connect(0);
                AddStream(w, 0, stream, compressed, Enumerable.Range(1, stream.Length / 4096).Select(i => i * 4096), ref frame);
            });

            ShouldReplayExactly(cap, packets);
        }

        [Fact]
        public void Login_Then_Game_Connection_Replays_Both_Streams()
        {
            var rng = new Random(5);
            var login = new List<byte[]> { Var(0xA8, 46, rng), Fixed(0x8C, 11, rng) };
            var game = new List<byte[]> { Var(0xA9, 300, rng), Fixed(0x1B, 37, rng), Var(0xD6, 50, rng), Fixed(0x73, 2, rng) };
            uint frame = 1;

            Capture cap = Build(w =>
            {
                w.Connect(0);
                w.Outbound(0, frame, 0, new byte[] { 0x80, 1, 2, 3 });
                AddStream(w, 0, Concat(login, false), false, new[] { 2, 30 }, ref frame);
                w.Disconnect(0, frame, 0);
                w.Connect(1);
                AddStream(w, 1, Concat(game, true), true, new[] { 1, 2, 3, 100, 250 }, ref frame);
            });

            ShouldReplayExactly(cap, login.Concat(game).ToList());
        }

        [Fact]
        public void A_Partial_Packet_Left_At_A_Connection_End_Is_Not_Glued_To_The_Next()
        {
            var rng = new Random(9);
            byte[] cut = Var(0xD6, 60, rng);
            byte[] next = Fixed(0x1D, 5, rng);
            uint frame = 1;

            Capture cap = Build(w =>
            {
                w.Connect(0);
                w.Inbound(0, frame++, 0, false, 0, cut.AsSpan(0, 20));
                w.Connect(1);
                w.Inbound(1, frame++, 0, false, 0, next);
            });

            foreach (ChunkMode mode in Modes)
            {
                Packets(Trace(cap, mode)).Select(p => p.Id).Should().Equal(new byte[] { 0x1D }, $"mode {mode}");
            }
        }

        [Fact]
        public void Capture_Round_Trips_And_Redacts_Login_Packets()
        {
            byte[] secret = Encoding.ASCII.GetBytes("hunter2-password");
            var login = new byte[62];
            login[0] = 0x80;
            secret.CopyTo(login, 31);
            var gameLogin = new byte[65];
            gameLogin[0] = 0x91;
            secret.CopyTo(gameLogin, 35);
            var ms = new MemoryStream();

            using (var w = new CaptureWriter(ms, Version))
            {
                w.Connect(0);
                w.Outbound(0, 1, 16, login);
                w.Inbound(0, 2, 32, false, 1, new byte[] { 0xA8, 0, 6, 1, 2, 3 });
                w.Outbound(0, 3, 48, new byte[] { 0xA0, 0, 0 });
                w.Connect(1);
                w.Outbound(1, 4, 64, gameLogin);
                w.Inbound(1, 5, 80, true, 1, new byte[] { 9, 8, 7 });
                w.Flush();
                byte[] file = ms.ToArray();

                file.AsSpan().IndexOf(secret).Should().Be(-1, "the password never reaches the file");

                Capture cap = Capture.Read(new MemoryStream(file));
                cap.ClientVersion.Should().Be(Version);
                cap.Records.Select(r => r.Kind).Should().Equal(CaptureKind.Connect, CaptureKind.Outbound, CaptureKind.Inbound, CaptureKind.Outbound,
                    CaptureKind.Connect, CaptureKind.Outbound, CaptureKind.Inbound);
                cap.Records[1].Should().Match<CaptureRecord>(r => r.Id == 0x80 && r.Length == 62 && r.Redacted && r.Data.Length == 0);
                cap.Records[2].Data.Should().Equal(0xA8, 0, 6, 1, 2, 3);
                cap.Records[2].Gates.Should().Be(1);
                cap.Records[3].Data.Should().Equal(0xA0, 0, 0);
                cap.Records[6].Should().Match<CaptureRecord>(r => r.Conn == 1 && r.Compressed && r.Frame == 5 && r.Ticks == 80);

                // a capture cut inside its last record (a killed client) keeps the complete records
                Capture.Read(new MemoryStream(file.AsSpan(0, file.Length - 2).ToArray())).Records.Should().HaveCount(6);
            }
        }

        [Fact]
        public void Schedule_Holds_A_Group_Until_Its_Flow_Packets_And_Gap()
        {
            uint frame = 1;
            Capture cap = Build(w =>
            {
                w.Connect(0);
                w.Inbound(0, 1, 1000, false, 0, new byte[] { 1 });
                w.Inbound(0, 1, 1000, false, 0, new byte[] { 2 });
                w.Inbound(0, 2, 1100, false, 1, new byte[] { 3 }); // after the client's first flow packet, 100 ms later
                w.Inbound(0, 3, 1150, false, 2, new byte[] { 4 }); // never gets its flow packet
                frame++;
            });

            var socket = new ReplaySocket(ChunkMode.Max, 1);
            socket.Connect(null);
            bool connected = false;
            int gates = 0;
            var schedule = new ReplaySchedule(cap) { GateTimeoutMs = 500 };
            ReplaySocket SocketFor(int c) => connected ? socket : null;

            schedule.Advance(0, SocketFor, _ => gates).Should().Be(0, "the connection is not open");
            connected = true;
            schedule.Advance(16, SocketFor, _ => gates).Should().Be(2, "the first frame group is released whole");
            schedule.Advance(32, SocketFor, _ => gates).Should().Be(0, "no flow packet yet");
            gates = 1;
            schedule.Advance(48, SocketFor, _ => gates).Should().Be(0, "100 ms have not passed");
            schedule.Advance(116, SocketFor, _ => gates).Should().Be(1);
            schedule.Advance(200, SocketFor, _ => gates).Should().Be(0, "waits for its flow packet");
            schedule.Advance(699, SocketFor, _ => gates).Should().Be(0);
            schedule.Advance(700, SocketFor, _ => gates).Should().Be(1, "released after the flow timeout");
            schedule.GateTimeouts.Should().Be(1);
            schedule.Done.Should().BeTrue();

            var buf = new byte[8];
            socket.Read(buf).Should().Be(4);
            buf.Take(4).Should().Equal(1, 2, 3, 4);
        }

        // A real capture (CUO_NET_CAPTURE from a client job) replayed in every mode; the traces go to CUO_NET_REPLAY_OUT.
        // Does nothing unless CUO_NET_REPLAY_FILE is set:
        //   CUO_NET_REPLAY_FILE=<capture> CUO_NET_REPLAY_OUT=<dir> dotnet test tests/ClassicUO.UnitTests -c Release --filter Recorded_Capture
        [Fact]
        public void Recorded_Capture_Replays_The_Same_In_Every_Mode()
        {
            string path = Environment.GetEnvironmentVariable("CUO_NET_REPLAY_FILE");

            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            string outDir = Environment.GetEnvironmentVariable("CUO_NET_REPLAY_OUT") ?? Path.GetTempPath();
            Directory.CreateDirectory(outDir);
            Capture cap = Capture.Load(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string reference = null;

            foreach (ChunkMode mode in Modes.Append(ChunkMode.Recorded))
            {
                var sw = new StringWriter();
                HeadlessReplay.Run(cap, mode, sw);
                string text = sw.ToString();
                File.WriteAllText(Path.Combine(outDir, $"{name}.headless.{mode.ToString().ToLowerInvariant()}.trace"), text);
                string body = string.Join("\n", text.Split('\n').Where(l => !l.StartsWith("#")));
                reference ??= body;
                body.Should().Be(reference, $"mode {mode} (diff the .trace files with tools/net-replay/nettrace_diff.py)");
            }
        }
    }
}
#endif
