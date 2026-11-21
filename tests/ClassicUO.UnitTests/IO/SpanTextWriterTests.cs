using System;
using System.Text;
using ClassicUO.IO;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.IO
{
    // Bytes per char by encoding type (UTF-32 is 4: the reader's power-of-two length mask needs it), and the writer's
    // worst-case sizing, which counts exactly only when the worst case would not fit.
    public sealed class SpanTextWriterTests
    {
        [Fact]
        public void Byte_Length_Follows_The_Encoding_Type()
        {
            Encoding.Unicode.GetByteLengthForEncoding().Should().Be(2);
            Encoding.BigEndianUnicode.GetByteLengthForEncoding().Should().Be(2);
            Encoding.UTF32.GetByteLengthForEncoding().Should().Be(4);
            new UTF32Encoding(true, false).GetByteLengthForEncoding().Should().Be(4);
            Encoding.UTF8.GetByteLengthForEncoding().Should().Be(1);
            Encoding.ASCII.GetByteLengthForEncoding().Should().Be(1);
            Encoding.Latin1.GetByteLengthForEncoding().Should().Be(1);
        }

        [Fact]
        public void Exact_Size_Fixed_Buffer_Takes_A_Utf8_Write()
        {
            const string text = "héllo wörld";
            byte[] expected = Encoding.UTF8.GetBytes(text);
            Span<byte> buffer = stackalloc byte[expected.Length];
            var writer = new SpanWriter(buffer);

            writer.Write(text.AsSpan(), Encoding.UTF8);

            writer.Position.Should().Be(expected.Length);
            buffer.ToArray().Should().Equal(expected);
        }

        [Fact]
        public void Fixed_Length_Pads_Unicode_And_Ascii()
        {
            Span<byte> buffer = stackalloc byte[32];
            var writer = new SpanWriter(buffer);
            buffer.Fill(0xFF);

            writer.Write("ab".AsSpan(), Encoding.BigEndianUnicode, 5);
            writer.Position.Should().Be(10);
            buffer[..10].ToArray().Should().Equal(0, (byte)'a', 0, (byte)'b', 0, 0, 0, 0, 0, 0);

            writer.Write("xyz".AsSpan(), Encoding.ASCII, 6);
            writer.Position.Should().Be(16);
            buffer[10..16].ToArray().Should().Equal((byte)'x', (byte)'y', (byte)'z', 0, 0, 0);
        }

        [Fact]
        public void Empty_Text_Without_Padding_Writes_Nothing()
        {
            Span<byte> buffer = stackalloc byte[4];
            var writer = new SpanWriter(buffer);

            writer.Write(ReadOnlySpan<char>.Empty, Encoding.UTF8);

            writer.Position.Should().Be(0);
        }
    }
}
