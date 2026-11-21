using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using ClassicUO.Game;
using ClassicUO.Network;
using ClassicUO.Network.Encryption;
using ClassicUO.Network.Socket;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;
#if CUO_DEVTOOLS
using ClassicUO.Network.Harness;
#endif

namespace ClassicUO.UnitTests.Network
{
    // Short layouts (owner 2026-11-20): packets with version-dependent or optional fields take their layout from the
    // packet's length, not from the client version; a length no layout fits drops the connection (span_reader on). The
    // framing hands a handler only whole packets, so a packet cut by a read or by the decompression boundary gets the same
    // layout in every chunk mode and is never mistaken for a short one.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class PacketLayoutTests : IDisposable
    {
        private const ClientVersion OldVersion = ClientVersion.CV_6000;
        private const ClientVersion NewVersion = ClientVersion.CV_70331;

        public void Dispose() => SpanBuffers.ModeOverride = null;

        private sealed class Pkt
        {
            private readonly List<byte> _b = new();
            private readonly bool _variable;

            public Pkt(byte id, bool variable = true)
            {
                _variable = variable;
                _b.Add(id);

                if (variable)
                {
                    _b.Add(0);
                    _b.Add(0);
                }
            }

            public Pkt U8(int v)
            {
                _b.Add((byte)v);

                return this;
            }

            public Pkt U16(int v) => U8(v >> 8).U8(v);

            public Pkt U32(uint v) => U16((int)(v >> 16)).U16((int)v);

            public Pkt Zero(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    _b.Add(0);
                }

                return this;
            }

            public Pkt Ascii(string s, int n)
            {
                byte[] a = Encoding.ASCII.GetBytes(s);

                for (int i = 0; i < n; i++)
                {
                    _b.Add(i < a.Length ? a[i] : (byte)0);
                }

                return this;
            }

            public Pkt BigUniNull(string s)
            {
                foreach (char c in s)
                {
                    U16(c);
                }

                return U16(0);
            }

            public Pkt LittleUniNull(string s)
            {
                foreach (char c in s)
                {
                    U8(c).U8(c >> 8);
                }

                return U16(0);
            }

            public byte[] Build(int cut = 0)
            {
                byte[] p = _b.Take(_b.Count - cut).ToArray();

                if (_variable)
                {
                    p[1] = (byte)(p.Length >> 8);
                    p[2] = (byte)p.Length;
                }

                return p;
            }
        }

        private static Pkt ContainedEntry(Pkt p, uint serial, bool grid)
        {
            p.U32(serial).U16(0x0EED).U8(0).U16(5).U16(40).U16(50);

            if (grid)
            {
                p.U8(3);
            }

            return p.U32(0x4000_0001).U16(0x0481);
        }

        private static Pkt Contained(bool grid, int count)
        {
            var p = new Pkt(0x3C).U16(count);

            for (int i = 0; i < count; i++)
            {
                ContainedEntry(p, 0x4000_1000u + (uint)i, grid);
            }

            return p;
        }

        // 0x78 / 0xD3 with a worn list; entries: (graphic, hue present)
        private static Pkt Mobile(byte id, (int graphic, bool hue)[] worn, bool terminated)
        {
            var p = new Pkt(id).U32(0x0000_2000).U16(0x0190).U16(1000).U16(1000).U8(0).U8(2).U16(0x83EA).U8(0).U8(1);

            if (id == 0xD3)
            {
                p.Zero(6);
            }

            uint serial = 0x4000_2000;

            foreach ((int graphic, bool hue) in worn)
            {
                p.U32(serial++).U16(graphic).U8(5);

                if (hue)
                {
                    p.U16(0x0481);
                }
            }

            if (terminated)
            {
                p.U32(0);
            }

            return p;
        }

        private static Pkt BuffHead() =>
            new Pkt(0xDF).U32(0x0000_2000).U16(0x03F2).U16(1).U32(0).U16(0x03F2).U16(1).U32(0).U16(30).Zero(3).U32(1075655).U32(1075656);

        // ModernUO SendAddBuffPacket without and with args
        private static byte[] BuffNoArgs() => BuffHead().Zero(10).Build();

        private static Pkt BuffArgs() => BuffHead().U32(0).U16(1).U16(0).U8('\t').U8(0).LittleUniNull("abc").U16(1).U16(0);

        private static Pkt CharList(bool newCities, bool flags, bool lastSlot)
        {
            var p = new Pkt(0xA9).U8(1).Ascii("Tester", 30).Zero(30).U8(2);

            for (int i = 0; i < 2; i++)
            {
                p.U8(i);

                if (newCities)
                {
                    p.Ascii("City", 32).Ascii("Inn", 32).U32(1000).U32(1000).U32(0).U32(1).U32(1075072).U32(0);
                }
                else
                {
                    p.Ascii("City", 31).Ascii("Inn", 31);
                }
            }

            if (flags)
            {
                p.U32(0x1A8);
            }

            if (lastSlot)
            {
                p.U16(0xFFFF);
            }

            return p;
        }

        private static Pkt Menu(int items)
        {
            var p = new Pkt(0x7C).U32(0x4000_3000).U16(7).U8(4).Ascii("Menu", 4).U8(items);

            for (int i = 0; i < items; i++)
            {
                p.U16(0x0EED).U16(0).U8(3).Ascii("Gp" + i, 3);
            }

            return p;
        }

        private static Pkt Popup(int mode) => new Pkt(0xBF).U16(0x14).U16(mode).U32(0x4000_4000);

        /// <summary>Each guarded packet with the layout its length must select (None: no layout fits).</summary>
        private static List<(byte[] Packet, int Layout)> Cases(ClientVersion version)
        {
            bool isNew = version >= ClientVersion.CV_70331;
            var list = new List<(byte[], int)>();

            // fixed lengths come from the table, which follows the version
            list.Add(isNew
                ? (new Pkt(0xBA, false).U8(1).U16(100).U16(200).U32(0x4000_0010).Build(), PacketLayouts.QuestArrowSerial)
                : (new Pkt(0xBA, false).U8(1).U16(100).U16(200).Build(), PacketLayouts.QuestArrowNoSerial));
            list.Add(isNew
                ? (ContainedEntry(new Pkt(0x25, false), 0x4000_0020, true).Build(), PacketLayouts.ContainedGrid)
                : (ContainedEntry(new Pkt(0x25, false), 0x4000_0020, false).Build(), PacketLayouts.ContainedNoGrid));
            list.Add(isNew
                ? (new Pkt(0xB9, false).U32(0x8000_801F).Build(), PacketLayouts.Features32)
                : (new Pkt(0xB9, false).U16(0x801F).Build(), PacketLayouts.Features16));

            byte[] ItemSA(bool longLayout)
            {
                var p = new Pkt(0xF3, false).U16(1).U8(0).U32(0x4000_0030).U16(0x0EED).U8(0).U16(1).U16(1).U16(100).U16(200).U8(0).U8(0).U16(0).U8(0);

                return (longLayout ? p.U16(0) : p).Build();
            }

            list.Add((ItemSA(isNew), isNew ? PacketLayouts.ItemSALong : PacketLayouts.ItemSAShort));

            // 0xF7: a list of F3 records, each with its id
            foreach (bool longLayout in new[] { false, true })
            {
                var f7 = new Pkt(0xF7).U16(2);
                byte[] rec = ItemSA(longLayout);

                for (int i = 0; i < 2; i++)
                {
                    foreach (byte b in rec)
                    {
                        f7.U8(b);
                    }
                }

                list.Add((f7.Build(), longLayout ? PacketLayouts.ItemSALong : PacketLayouts.ItemSAShort));
                list.Add((f7.Build(cut: 1), PacketLayouts.None));
            }

            // 0x3C: 19- or 20-byte items, whatever the version
            list.Add((Contained(false, 2).Build(), PacketLayouts.ContainedNoGrid));
            list.Add((Contained(true, 2).Build(), PacketLayouts.ContainedGrid));
            list.Add((Contained(true, 0).Build(), isNew ? PacketLayouts.ContainedGrid : PacketLayouts.ContainedNoGrid));
            list.Add((Contained(true, 2).Build(cut: 10), PacketLayouts.None));

            // 0x78 / 0xD3: hue per flag or always; terminated or not; both fit: the version decides
            foreach (byte id in new byte[] { 0x78, 0xD3 })
            {
                list.Add((Mobile(id, new[] { (0x1F03, false), (0x9517, true) }, true).Build(), PacketLayouts.WornFlagHue));
                list.Add((Mobile(id, new[] { (0x1F03, false), (0x9517, true) }, false).Build(), PacketLayouts.WornFlagHue));
                list.Add((Mobile(id, new[] { (0x1F03, true), (0x1517, true) }, true).Build(), PacketLayouts.WornAlwaysHue));
                list.Add((Mobile(id, new[] { (0x1F03, true), (0x1517, true) }, false).Build(), PacketLayouts.WornAlwaysHue));
                list.Add((Mobile(id, new[] { (0x9F03, true) }, true).Build(), isNew ? PacketLayouts.WornAlwaysHue : PacketLayouts.WornFlagHue));
                list.Add((Mobile(id, Array.Empty<(int, bool)>(), true).Build(), isNew ? PacketLayouts.WornAlwaysHue : PacketLayouts.WornFlagHue));
                list.Add((Mobile(id, new[] { (0x1F03, true) }, false).Build(cut: 1), PacketLayouts.None));
            }

            // 0xDF: one, two or three text blocks
            list.Add((BuffNoArgs(), 1));
            list.Add((BuffArgs().Build(), 2));
            list.Add((BuffArgs().U16(1).LittleUniNull("x").Build(), 3));
            list.Add((BuffArgs().U8(0).Build(), PacketLayouts.None));
            list.Add((BuffHead().Zero(4).Build(), PacketLayouts.None));

            // 0xF0: party (9-byte entries) and guild (10 with locations, 4 without); terminated or not
            list.Add((new Pkt(0xF0).U8(1).U32(0x0000_2001).U16(10).U16(20).U8(1).U32(0).Build(), PacketLayouts.KrriosValid));
            list.Add((new Pkt(0xF0).U8(1).U32(0x0000_2001).U16(10).U16(20).U8(1).Build(), PacketLayouts.KrriosValid));
            list.Add((new Pkt(0xF0).U8(2).U8(1).U32(0x0000_2001).U16(10).U16(20).U8(1).U8(90).U32(0).Build(), PacketLayouts.KrriosValid));
            list.Add((new Pkt(0xF0).U8(2).U8(0).U32(0x0000_2001).U32(0x0000_2002).Build(), PacketLayouts.KrriosValid));
            list.Add((new Pkt(0xF0).U8(0).Build(), PacketLayouts.KrriosValid));
            list.Add((new Pkt(0xF0).U8(1).U32(0x0000_2001).U16(10).Build(), PacketLayouts.None));

            // 0xA9: 63- or 89-byte cities, with or without the flags and the last-character word
            list.Add((CharList(true, true, true).Build(), PacketLayouts.CharListNewCities | PacketLayouts.CharListFlags));
            list.Add((CharList(false, true, false).Build(), PacketLayouts.CharListFlags));
            list.Add((CharList(false, false, false).Build(), 0));
            list.Add((CharList(true, false, false).Build(), PacketLayouts.CharListNewCities));
            list.Add((CharList(false, true, false).U8(0).Build(), PacketLayouts.None));

            // 0xB2 chat
            list.Add((new Pkt(0xB2).U16(0x03E8).Ascii("ENU", 4).BigUniNull("General").BigUniNull("0").Build(), PacketLayouts.ChatFull));
            list.Add((new Pkt(0xB2).U16(0x03E8).Ascii("ENU", 4).BigUniNull("General").Build(), PacketLayouts.ChatNoWord));
            list.Add((new Pkt(0xB2).U16(0x03E8).Build(), PacketLayouts.ChatNoText));
            list.Add((new Pkt(0xB2).U16(0x03EE).Ascii("ENU", 4).Build(), PacketLayouts.ChatNoWord));
            list.Add((new Pkt(0xB2).U16(0x0025).Ascii("ENU", 4).BigUniNull("1Tester").BigUniNull("hi").Build(), PacketLayouts.ChatFull));
            list.Add((new Pkt(0xB2).U16(0x03EB).U8(1).Build(), PacketLayouts.ChatFull));
            list.Add((new Pkt(0xB2).U16(0x03E9).U16(0x454E).Build(), PacketLayouts.None));
            list.Add((new Pkt(0xB2).U16(0x03EE).Ascii("ENU", 4).U8(0).Build(), PacketLayouts.None));

            // 0xBF.0x14 popup: the new layout; the old one with this client's optional words or only the color word
            list.Add((Popup(2).U8(2).U32(3006123).U16(1).U16(0).U32(3006145).U16(2).U16(1).Build(), PacketLayouts.PopupNew));
            list.Add((Popup(1).U8(2).U16(1).U16(6123).U16(0x20).U16(0x0481).U16(2).U16(6145).U16(0x84).U16(0).Build(), PacketLayouts.PopupOldClassic));
            list.Add((Popup(1).U8(2).U16(1).U16(6123).U16(0x24).U16(0x0481).U16(2).U16(6145).U16(0x24).U16(0x0482).Build(), PacketLayouts.PopupOldColor));
            list.Add((Popup(1).U8(2).U16(1).U16(6123).U16(0x20).Build(), PacketLayouts.None));
            list.Add((Popup(2).U8(2).U32(3006123).U16(1).U16(0).Build(), PacketLayouts.None));

            // 0x7C menu
            list.Add((Menu(0).Build(), PacketLayouts.MenuNoItems));
            list.Add((Menu(2).Build(), PacketLayouts.MenuItems));
            list.Add((Menu(2).Build(cut: 3), PacketLayouts.None));

            return list;
        }

        private static int Select(ReadOnlySpan<byte> packet, ClientVersion version) => packet[0] switch
        {
            0xBA => PacketLayouts.QuestArrow(packet),
            0x25 => PacketLayouts.ContainedItem(packet),
            0x3C => PacketLayouts.ContainedItems(packet, version),
            0x78 or 0xD3 => PacketLayouts.WornList(packet, version),
            0xB9 => PacketLayouts.LockedFeatures(packet),
            0xF3 or 0xF7 => PacketLayouts.ItemSA(packet, version),
            0xDF => PacketLayouts.BuffEntries(packet),
            0xF0 => PacketLayouts.Krrios(packet),
            0xA9 => PacketLayouts.CharacterList(packet, version),
            0xB2 => PacketLayouts.Chat(packet),
            0xBF => PacketLayouts.Popup(packet, 5),
            0x7C => PacketLayouts.Menu(packet),
            _ => throw new ArgumentOutOfRangeException(nameof(packet))
        };

        private static readonly byte[] GuardedIds = { 0xBA, 0x25, 0x3C, 0x78, 0xD3, 0xB9, 0xF3, 0xF7, 0xDF, 0xF0, 0xA9, 0xB2, 0xBF, 0x7C };

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Every_guarded_packet_selects_its_layout_from_its_length(bool newVersion)
        {
            ClientVersion version = newVersion ? NewVersion : OldVersion;
            var cases = Cases(version);
            cases.Select(c => c.Packet[0]).Distinct().OrderBy(b => b).Should().Equal(GuardedIds.OrderBy(b => b));

            for (int i = 0; i < cases.Count; i++)
            {
                Select(cases[i].Packet, version).Should().Be(cases[i].Layout, $"case {i}: packet 0x{cases[i].Packet[0]:X2} of {cases[i].Packet.Length} bytes");
            }
        }

        [Fact]
        public void Variable_packets_ignore_the_version_unless_two_layouts_fit()
        {
            byte[] noGrid = Contained(false, 3).Build();
            PacketLayouts.ContainedItems(noGrid, NewVersion).Should().Be(PacketLayouts.ContainedNoGrid);
            PacketLayouts.ContainedItems(Contained(true, 3).Build(), OldVersion).Should().Be(PacketLayouts.ContainedGrid);

            byte[] both = Mobile(0x78, new[] { (0x9F03, true), (0x9517, true) }, true).Build();
            PacketLayouts.WornList(both, OldVersion).Should().Be(PacketLayouts.WornFlagHue);
            PacketLayouts.WornList(both, NewVersion).Should().Be(PacketLayouts.WornAlwaysHue);
        }

        // ------------------------------------------------------------------ dispatch: no layout drops the connection

        private sealed class ChunkedSocket : SocketWrapper
        {
            private readonly byte[] _data;
            private readonly int _chunk;
            private int _pos;
            private bool _connected = true;

            public ChunkedSocket(byte[] data, int chunk)
            {
                _data = data;
                _chunk = chunk;
            }

            public override bool IsConnected => _connected;
            public override EndPoint LocalEndPoint => null;
            public override void Connect(Uri uri) { }
            public override void Send(byte[] buffer, int offset, int count) { }
            public override void Disconnect() => _connected = false;
            public override void Dispose() => _connected = false;

            public override int Read(byte[] buffer)
            {
                if (!_connected || _pos >= _data.Length)
                {
                    return 0;
                }

                int n = Math.Min(Math.Min(_chunk, buffer.Length), _data.Length - _pos);
                Array.Copy(_data, _pos, buffer, 0, n);
                _pos += n;

                return n;
            }
        }

        // a valid 0x3C (grid), one that fits no layout, a valid one without the grid
        private static (int disconnects, List<int> handled, string reason) Dispatch(SpanBufferMode mode, int chunk)
        {
            SpanBuffers.ModeOverride = mode;
            byte[] stream = Contained(true, 2).Build().Concat(Contained(true, 2).Build(cut: 10)).Concat(Contained(false, 1).Build()).ToArray();
            var client = new NetClient();
            client.Load(NewVersion, EncryptionType.NONE);
            client.AttachSocket(new ChunkedSocket(stream, chunk));
            int disconnects = 0;
            client.Disconnected += (_, _) => disconnects++;

            var handled = new List<int>();
            var handlers = new PacketHandlers();
            handlers.Add(0x3C, (World _, ref NetReader p) =>
            {
                handled.Add(PacketLayouts.Resolve(ref p, PacketLayouts.ContainedItems(p.Buffer, NewVersion), PacketLayouts.ContainedGrid));
                p.ReadUInt16();
            });

            for (int i = 0; i < stream.Length + 2; i++)
            {
                client.Drain(data => handlers.ParsePackets(client, null!, data), 1 << 16, 1, maxMs: 0);
            }

            return (disconnects, handled, client.DisconnectReason);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(4096)]
        public void A_length_no_layout_fits_drops_the_connection_when_on(int chunk)
        {
            foreach (SpanBufferMode on in new[] { SpanBufferMode.New, SpanBufferMode.CheckNew })
            {
                var r = Dispatch(on, chunk);
                r.disconnects.Should().Be(1, $"{on}");
                r.handled.Should().Equal(new[] { PacketLayouts.ContainedGrid }, "the packet with no layout ends the stream");
                r.reason.Should().Contain("0x3C");
            }

            foreach (SpanBufferMode off in new[] { SpanBufferMode.Old, SpanBufferMode.CheckOld })
            {
                var r = Dispatch(off, chunk);
                r.disconnects.Should().Be(0, $"{off}");
                r.handled.Should().Equal(PacketLayouts.ContainedGrid, PacketLayouts.ContainedGrid, PacketLayouts.ContainedNoGrid);
                r.reason.Should().BeNull();
            }
        }

#if CUO_DEVTOOLS
        // ------------------------------------------------------------------ harness: every chunk mode, plain and compressed

        private static readonly ChunkMode[] Modes = { ChunkMode.Recorded, ChunkMode.Byte, ChunkMode.Random, ChunkMode.Max };

        private static Capture Build(ClientVersion version, Action<CaptureWriter> write)
        {
            var ms = new MemoryStream();

            using (var w = new CaptureWriter(ms, (uint)version))
            {
                write(w);
                w.Flush();
                ms = new MemoryStream(ms.ToArray());
            }

            return Capture.Read(ms);
        }

        private static List<(byte Id, int Length, int Layout)> Replay(Capture cap, ClientVersion version, ChunkMode mode, int seed)
        {
            var seen = new List<(byte, int, int)>();

            HeadlessReplay.Run(cap, mode, TextWriter.Null, seed, handlers =>
            {
                foreach (byte id in GuardedIds)
                {
                    handlers.Add(id, (World _, ref NetReader p) =>
                    {
                        if (p[0] == 0xBF && (p.Length < 5 || p[3] != 0 || p[4] != 0x14))
                        {
                            return;
                        }

                        seen.Add((p[0], p.Length, Select(p.Buffer, version)));
                    });
                }
            });

            return seen;
        }

        private static string Text(List<(byte Id, int Length, int Layout)> seen) =>
            string.Join(" | ", seen.Select(s => $"{s.Id:X2} {s.Length} {s.Layout}"));

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Guarded_packets_cut_anywhere_get_the_layout_of_their_full_length(bool newVersion, bool compressed)
        {
            ClientVersion version = newVersion ? NewVersion : OldVersion;
            var cases = Cases(version);
            var expected = cases.Select(c => (c.Packet[0], c.Packet.Length, c.Layout)).ToList();
            byte[] stream = NetReplayHarnessTests.Concat(cases.Select(c => c.Packet), compressed);

            // recorded reads cut every packet inside its header and its body
            var cuts = new List<int>();
            int at = 0;

            foreach (var c in cases)
            {
                cuts.Add(at + 1);
                cuts.Add(at + 2);
                cuts.Add(at + c.Packet.Length / 2);
                cuts.Add(at + c.Packet.Length - 1);
                at += c.Packet.Length;
            }

            if (compressed)
            {
                var rng = new Random(11);
                cuts = Enumerable.Range(0, 300).Select(_ => rng.Next(1, stream.Length)).ToList();
            }

            uint frame = 1;
            Capture cap = Build(version, w =>
            {
                w.Connect(0);
                NetReplayHarnessTests.AddStream(w, 0, stream, compressed, cuts, ref frame, framesPer: 2);
            });

            foreach (ChunkMode mode in Modes)
            {
                Text(Replay(cap, version, mode, 1234)).Should().Be(Text(expected), $"mode {mode}");
            }

            Text(Replay(cap, version, ChunkMode.Random, 77)).Should().Be(Text(expected), "another random cut");
        }
#endif
    }
}
