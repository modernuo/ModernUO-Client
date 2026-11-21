using System;
using System.Collections.Generic;
using ClassicUO.IO;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.IO
{
    // StackDataWriter.WriteASCII and SpanWriter.WriteCp1252(Null) encode with StringHelper.GetBytesCp1252(Fixed) straight into
    // the buffer; the bytes are the ones the per-byte StringToCp1252Bytes loop wrote.
    public class Cp1252WriteTests
    {
        public static readonly string[] Strings =
        {
            null,
            "",
            "a",
            "Lord British",
            "€™Ÿ‚",                       // Cp1252 letters above U+00FF
            "\u0080\u009F",                // C1 controls: '?'
            "éÿñ",                    // Latin1
            "ĀĀ中",              // unmapped: '?'
            "x😀y",              // a surrogate pair: one '?'
            "\uD83D",                      // lone high surrogate
            "ab\uDE00",                    // lone low surrogate
            "\0in\0side",
            new string('w', 300),          // grows a small buffer
            new string('é', 120) + "😀" + new string('z', 150)
        };

        public static readonly int[] Lengths = { -1, 0, 1, 2, 3, 4, 30, 256, 400 };

        // The old loops (StackDataWriter.WriteASCII before the change).
        private static byte[] OldNull(string s)
        {
            var bytes = new List<byte>();

            if (!string.IsNullOrEmpty(s))
            {
                foreach (byte b in StringHelper.StringToCp1252Bytes(s))
                {
                    bytes.Add(b);
                }
            }

            bytes.Add(0);

            return bytes.ToArray();
        }

        private static byte[] OldFixed(string s, int length)
        {
            var bytes = new List<byte>();

            if (string.IsNullOrEmpty(s))
            {
                bytes.Add(0);
            }
            else
            {
                foreach (byte b in StringHelper.StringToCp1252Bytes(s, length))
                {
                    bytes.Add(b);
                }
            }

            if (length > -1)
            {
                while (bytes.Count < length)
                {
                    bytes.Add(0);
                }
            }

            return bytes.ToArray();
        }

        public static IEnumerable<object[]> Cases()
        {
            for (int i = 0; i < Strings.Length; i++)
            {
                foreach (int length in Lengths)
                {
                    yield return new object[] { i, length };
                }
            }
        }

        private static byte[] StackWrite(int index, int length, int capacity)
        {
            var w = new StackDataWriter(capacity);
            w.WriteUInt8(0xAA);

            if (length == int.MinValue)
            {
                w.WriteASCII(Strings[index]);
            }
            else
            {
                w.WriteASCII(Strings[index], length);
            }

            w.WriteUInt8(0xBB);
            byte[] bytes = w.BufferWritten.ToArray();
            w.Dispose();

            return bytes;
        }

        private static byte[] SpanWrite(int index, int length, int capacity)
        {
            var w = new SpanWriter(capacity, resize: true);
            w.Write((byte)0xAA);

            if (length == int.MinValue)
            {
                w.WriteCp1252Null(Strings[index]);
            }
            else
            {
                w.WriteCp1252(Strings[index], length);
            }

            w.Write((byte)0xBB);
            byte[] bytes = w.BufferWritten.ToArray();
            w.Dispose();

            return bytes;
        }

        private static byte[] Framed(byte[] body)
        {
            var bytes = new byte[body.Length + 2];
            bytes[0] = 0xAA;
            body.CopyTo(bytes, 1);
            bytes[^1] = 0xBB;

            return bytes;
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Fixed_length_writes_match_the_old_loop(int index, int length)
        {
            byte[] expected = Framed(OldFixed(Strings[index], length));

            foreach (int capacity in new[] { 1, 8, 1024 })
            {
                StackWrite(index, length, capacity).Should().Equal(expected, $"StackDataWriter, capacity {capacity}");
                SpanWrite(index, length, capacity).Should().Equal(expected, $"SpanWriter, capacity {capacity}");
            }
        }

        [Fact]
        public void Null_terminated_writes_match_the_old_loop()
        {
            for (int i = 0; i < Strings.Length; i++)
            {
                byte[] expected = Framed(OldNull(Strings[i]));

                foreach (int capacity in new[] { 1, 8, 1024 })
                {
                    StackWrite(i, int.MinValue, capacity).Should().Equal(expected, $"StackDataWriter, string {i}, capacity {capacity}");
                    SpanWrite(i, int.MinValue, capacity).Should().Equal(expected, $"SpanWriter, string {i}, capacity {capacity}");
                }
            }
        }

        [Fact]
        public void A_fixed_field_overwrites_after_a_seek_back()
        {
            // A builder that seeks back keeps its furthest position as the written length.
            var w = new StackDataWriter(16);
            w.WriteZero(10);
            w.Seek(2, System.IO.SeekOrigin.Begin);
            w.WriteASCII("abc", 4);
            w.Position.Should().Be(6);
            w.BytesWritten.Should().Be(10);
            w.BufferWritten.ToArray().Should().Equal(0, 0, (byte)'a', (byte)'b', (byte)'c', 0, 0, 0, 0, 0);
            w.Dispose();
        }

        [Fact]
        public void Writes_allocate_nothing()
        {
            string s = new string('q', 200);
            var stack = new StackDataWriter(512);
            var span = new SpanWriter(512);

            // warm up
            stack.WriteASCII(s);
            stack.WriteASCII(s, 30);
            span.WriteCp1252Null(s);
            span.WriteCp1252(s, 30);
            stack.Seek(0, System.IO.SeekOrigin.Begin);
            span.Seek(0, System.IO.SeekOrigin.Begin);

            long before = GC.GetAllocatedBytesForCurrentThread();
            stack.WriteASCII(s);
            stack.WriteASCII(s, 30);
            span.WriteCp1252Null(s);
            span.WriteCp1252(s, 30);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            stack.Dispose();
            span.Dispose();
            allocated.Should().Be(0);
        }
    }
}
