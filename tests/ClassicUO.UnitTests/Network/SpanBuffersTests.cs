using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
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
    [CollectionDefinition(nameof(SpanBuffersCollection), DisableParallelization = true)]
    public sealed class SpanBuffersCollection
    {
    }

    // span_reader (net-rework step 2): NetReader / NetWriter give the same values and bytes with the old StackDataReader /
    // StackDataWriter (off) and ModernUO's SpanReader / SpanWriter (on); the checker compares both; with the switch on a packet
    // read past its end drops the connection at dispatch.
    [Collection(nameof(SpanBuffersCollection))]
    public sealed class SpanBuffersTests : IDisposable
    {
        private static readonly SpanBufferMode[] AllModes =
            { SpanBufferMode.Old, SpanBufferMode.New, SpanBufferMode.CheckOld, SpanBufferMode.CheckNew };

        private readonly List<string> _mismatches = new();

        public SpanBuffersTests()
        {
#if CUO_DEVTOOLS
            SpanBuffers.MismatchObserver = _mismatches.Add;
#endif
        }

        public void Dispose()
        {
            SpanBuffers.ModeOverride = null;
#if CUO_DEVTOOLS
            SpanBuffers.MismatchObserver = null;
#endif
        }

        // every primitive, a skip and a seek, as a log of "value@position"
        private static List<string> ReadPrimitives(ReadOnlySpan<byte> data, SpanBufferMode mode)
        {
            var r = new NetReader(data, mode);
            var log = new List<string>();
            log.Add($"{r.ReadByte()}@{r.Position}");
            log.Add($"{r.ReadSByte()}@{r.Position}");
            log.Add($"{r.ReadBoolean()}@{r.Position}");
            log.Add($"{r.ReadUInt16()}@{r.Position}");
            log.Add($"{r.ReadInt16()}@{r.Position}");
            log.Add($"{r.ReadUInt32()}@{r.Position}");
            log.Add($"{r.ReadInt32()}@{r.Position}");
            log.Add($"{r.ReadUInt32LE()}@{r.Position}");
            log.Add($"{r.ReadUInt64()}@{r.Position}");
            r.Skip(3);
            log.Add($"{r.ReadByte()}@{r.Position} rem {r.Remaining} len {r.Length} [0]={r[0]}");
            r.Seek(2, SeekOrigin.Begin);
            log.Add($"{r.ReadUInt16()}@{r.Position}");
            r.Seek(-4, SeekOrigin.End);
            log.Add($"{r.ReadUInt32()}@{r.Position}");

            return log;
        }

        [Fact]
        public void Primitives_are_the_same_in_every_mode()
        {
            var bytes = new byte[40];
            new Random(7).NextBytes(bytes);
            var expected = ReadPrimitives(bytes, SpanBufferMode.Old);

            foreach (var mode in AllModes)
            {
                ReadPrimitives(bytes, mode).Should().Equal(expected, $"mode {mode}");
            }

            _mismatches.Should().BeEmpty();
        }

        public delegate string StringRead(ref NetReader r);

        // the string reads handlers use, on bytes with Cp1252 letters (0x80-0x9F), control chars, NULs and odd lengths
        public static IEnumerable<object[]> StringCases()
        {
            byte[] cp = { 0x41, 0x80, 0x8A, 0x9F, 0x07, 0x42, 0x00, 0x43, 0x44, 0x00 };
            byte[] beUni = Concat(Encoding.BigEndianUnicode.GetBytes("Hé\u0001llo￾!"), new byte[] { 0, 0, 0x41, 0x00 });
            byte[] leUni = Concat(Encoding.Unicode.GetBytes("Wörld\u0002x"), new byte[] { 0, 0, 0x42, 0x00, 0x43 });
            byte[] utf8 = Concat(Encoding.UTF8.GetBytes("añb\u0003€c"), new byte[] { 0, 0x44, 0x45 });

            yield return Case("Cp1252", cp, (ref NetReader r) => r.ReadCp1252() + "|" + r.ReadCp1252());
            yield return Case("Cp1252 unterminated", new byte[] { 0x91, 0x92, 0x41 }, (ref NetReader r) => r.ReadCp1252());
            yield return Case("Cp1252Safe", cp, (ref NetReader r) => r.ReadCp1252Safe());
            yield return Case("Cp1252 fixed", cp, (ref NetReader r) => r.ReadCp1252(4) + "|" + r.ReadCp1252(5));
            yield return Case("Cp1252Safe fixed", cp, (ref NetReader r) => r.ReadCp1252Safe(8));
            yield return Case("Cp1252 fixed 0", cp, (ref NetReader r) => r.ReadCp1252(0));
            yield return Case("BigUni", beUni, (ref NetReader r) => r.ReadBigUni() + "|" + r.ReadBigUni());
            yield return Case("BigUniSafe", beUni, (ref NetReader r) => r.ReadBigUniSafe());
            yield return Case("BigUni fixed", beUni, (ref NetReader r) => r.ReadBigUni(3) + "|" + r.ReadBigUni(2));
            yield return Case("BigUni odd rest", new byte[] { 0, 0x41, 0, 0x42, 0x43 }, (ref NetReader r) => r.ReadBigUni());
            yield return Case("LittleUni", leUni, (ref NetReader r) => r.ReadLittleUni() + "|" + r.ReadLittleUni());
            yield return Case("LittleUni fixed", leUni, (ref NetReader r) => r.ReadLittleUni(2) + "|" + r.ReadLittleUni(6));
            yield return Case("LittleUni span", leUni, (ref NetReader r) =>
            {
                Span<char> buf = stackalloc char[8];
                int n = r.ReadLittleUni(7, buf);

                return new string(buf.Slice(0, n));
            });
            yield return Case("UTF8", utf8, (ref NetReader r) => r.ReadUTF8() + "|" + r.ReadUTF8());
            yield return Case("UTF8Safe", utf8, (ref NetReader r) => r.ReadUTF8Safe());
            yield return Case("UTF8Safe fixed", utf8, (ref NetReader r) => r.ReadUTF8Safe(3) + "|" + r.ReadUTF8Safe(6));
        }

        private static object[] Case(string name, byte[] data, StringRead read) => new object[] { name, data, read };

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var r = new byte[a.Length + b.Length];
            a.CopyTo(r, 0);
            b.CopyTo(r, a.Length);

            return r;
        }

        [Theory]
        [MemberData(nameof(StringCases))]
        public void Strings_are_the_same_in_every_mode(string name, byte[] data, StringRead read)
        {
            var old = new NetReader(data, SpanBufferMode.Old);
            string expected = read(ref old) + $"@{old.Position}";

            foreach (var mode in AllModes)
            {
                var r = new NetReader(data, mode);
                (read(ref r) + $"@{r.Position}").Should().Be(expected, $"{name}, mode {mode}");
            }

            _mismatches.Should().BeEmpty();
        }

        [Fact]
        public void Cp1252_reads_are_not_latin1()
        {
            var r = new SpanReader(new byte[] { 0x80, 0x99, 0 });
            r.ReadCp1252().Should().Be("€™");
        }

        [Fact]
        public void Short_reads_give_zero_off_and_throw_on()
        {
            var bytes = new byte[] { 1, 2, 3 };

            var old = new NetReader(bytes, SpanBufferMode.Old);
            old.ReadUInt16().Should().Be(0x0102);
            old.ReadUInt16().Should().Be(0);
            old.ReadCp1252(10).Should().Be("\u0003");

            var threw = 0;

            try
            {
                var r = new NetReader(bytes, SpanBufferMode.New);
                r.ReadUInt16();
                r.ReadUInt16();
            }
            catch (EndOfStreamException)
            {
                threw++;
            }

            try
            {
                var r = new NetReader(bytes, SpanBufferMode.New);
                r.Skip(4);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw++;
            }

            try
            {
                var r = new NetReader(bytes, SpanBufferMode.New);
                r.ReadCp1252(10);
            }
            catch (EndOfStreamException)
            {
                threw++;
            }

            threw.Should().Be(3);
        }

#if CUO_DEVTOOLS
        [Fact]
        public void Checker_logs_a_short_read_and_keeps_the_switch_reader()
        {
            var bytes = new byte[] { 0x77, 2, 3 };

            var r = new NetReader(bytes, SpanBufferMode.CheckOld);
            r.ReadByte().Should().Be(0x77);
            r.ReadUInt16().Should().Be(0x0203);
            r.Seek(1, SeekOrigin.Begin);
            r.ReadUInt32().Should().Be(0, "the old reader decides with the switch off (a short read gives 0 and does not move)");
            r.ReadByte().Should().Be(2, "after the first difference only the old reader reads");
            _mismatches.Should().ContainSingle().Which.Should().Contain("0x77").And.Contain("ReadUInt32").And.Contain("throws");

            _mismatches.Clear();
            var n = new NetReader(bytes, SpanBufferMode.CheckNew);
            n.ReadByte();
            var thrown = false;

            try
            {
                n.ReadUInt32();
            }
            catch (EndOfStreamException)
            {
                thrown = true;
            }

            thrown.Should().BeTrue("with the switch on the new reader decides");
            _mismatches.Should().ContainSingle();
        }
#endif

        private static byte[] BuildPacket(SpanBufferMode mode)
        {
            var w = new NetWriter(8, mode); // grows: the packet is longer than its first capacity
            w.Write((byte)0xAD);
            w.WriteZero(2);
            w.Write((sbyte)-2);
            w.Write(true);
            w.Write((ushort)0xBEEF);
            w.Write(0xDEADBEEFu);
            w.Write(0x0102030405060708UL);
            w.Write(new byte[] { 9, 8, 7 });
            w.WriteZero(0);
            w.WriteZero(-3);
            w.WriteCp1252Null("A€™é");
            w.WriteCp1252Null(null);
            w.WriteCp1252("toolongname", 4);
            w.WriteCp1252("ab", 6);
            w.WriteCp1252("", 5);
            w.WriteCp1252(null, -1);
            w.WriteBigUniNull("héllo");
            w.WriteBigUniNull(null);
            w.WriteBigUni("abcdef", 4);
            w.WriteBigUni("ab", 4);
            w.WriteLittleUniNull("wörld");
            w.WriteLittleUni("xyz", 5);
            w.WriteUTF8("añb", -1);
            w.WriteUTF8("añb€", 6);
            w.WriteUTF8("ñññ", 2);
            w.Seek(1, SeekOrigin.Begin);
            w.Write((ushort)w.BytesWritten);
            byte[] bytes = w.BufferWritten.ToArray();
            w.Dispose();

            return bytes;
        }

        [Fact]
        public void Writers_lay_out_the_same_bytes_in_every_mode()
        {
            byte[] expected = BuildPacket(SpanBufferMode.Old);
            expected.Length.Should().BeGreaterThan(80);
            (expected[1] << 8 | expected[2]).Should().Be(expected.Length);

            foreach (var mode in AllModes)
            {
                BuildPacket(mode).Should().Equal(expected, $"mode {mode}");
            }

            _mismatches.Should().BeEmpty();
        }

        private sealed class OneShotSocket : SocketWrapper
        {
            private byte[] _data;
            private bool _connected = true;

            public OneShotSocket(byte[] data) => _data = data;

            public override bool IsConnected => _connected;
            public override EndPoint LocalEndPoint => null;
            public override void Connect(Uri uri) { }
            public override void Send(byte[] buffer, int offset, int count) { }
            public override void Disconnect() => _connected = false;
            public override void Dispose() => _connected = false;

            public override int Read(byte[] buffer)
            {
                if (!_connected || _data == null)
                {
                    return 0;
                }

                int n = _data.Length;
                Array.Copy(_data, buffer, n);
                _data = null;

                return n;
            }
        }

        // 0x73 (2 bytes) whose handler reads 4 bytes past its id, then a 0x73 that is read correctly
        private static (int disconnects, List<int> handled, string reason) DispatchShortPacket(SpanBufferMode mode)
        {
            SpanBuffers.ModeOverride = mode;
            var client = new NetClient();
            client.Load(ClientVersion.CV_70130, EncryptionType.NONE);
            client.AttachSocket(new OneShotSocket(new byte[] { 0x73, 0x05, 0x73, 0x06 }));
            int disconnects = 0;
            client.Disconnected += (_, _) => disconnects++;

            var handled = new List<int>();
            var handlers = new PacketHandlers();
            handlers.Add(0x73, (World _, ref NetReader p) =>
            {
                byte idx = p.ReadByte();

                if (idx == 5)
                {
                    p.ReadUInt32(); // past the end
                }

                handled.Add(idx);
            });

            client.Drain(data => handlers.ParsePackets(client, null!, data), 1 << 16, 100, maxMs: 0);

            return (disconnects, handled, client.DisconnectReason);
        }

        [Fact]
        public void A_packet_read_past_its_end_drops_the_connection_when_on()
        {
            var on = DispatchShortPacket(SpanBufferMode.New);
            on.disconnects.Should().Be(1);
            on.handled.Should().BeEmpty("the failed packet is not finished and nothing after it is dispatched");
            on.reason.Should().Contain("0x73").And.Contain("2 bytes");

            var off = DispatchShortPacket(SpanBufferMode.Old);
            off.disconnects.Should().Be(0);
            off.handled.Should().Equal(5, 6);
            off.reason.Should().BeNull();
        }

#if CUO_DEVTOOLS
        [Fact]
        public void The_checker_with_the_switch_on_drops_the_connection_too()
        {
            var check = DispatchShortPacket(SpanBufferMode.CheckNew);
            check.disconnects.Should().Be(1);
            _mismatches.Should().ContainSingle().Which.Should().Contain("throws");

            _mismatches.Clear();
            var checkOff = DispatchShortPacket(SpanBufferMode.CheckOld);
            checkOff.disconnects.Should().Be(0);
            checkOff.handled.Should().Equal(5, 6);
            _mismatches.Should().ContainSingle();
        }
#endif
    }
}
