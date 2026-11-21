using System;
using System.Text;
using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;
using Uo.Buffers;

namespace ClassicUO.UnitTests.Network
{
    // The MegaCliloc path without a string per line and per argument must give the same text as the string versions.
    public class MegaClilocAllocFreeTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("h")]
        [InlineData("hello fans of ultima online")]
        [InlineData("  leading and  double  spaces ")]
        [InlineData("tab\tseparated\nlines x")]
        [InlineData("ALREADY UPPER and mixed cAsE")]
        [InlineData("1st 2nd élan ñandú")]
        public void Capitalize_In_Place_Matches_The_String_Version(string text)
        {
            char[] chars = text.ToCharArray();

            StringHelper.CapitalizeAllWords(chars.AsSpan());

            new string(chars).Should().Be(StringHelper.CapitalizeAllWords(text));
        }

        [Theory]
        [InlineData("x\ty", false)]
        [InlineData("x\ty", true)]
        [InlineData("", true)]
        [InlineData("#1042971\t5", true)]
        [InlineData("\tabc", false)]
        [InlineData("a\t\tb\t", true)]
        public void Builder_Text_Matches_The_String_Translate(string argument, bool capitalize)
        {
            var c = new ClilocLoader(null);

            ValueStringBuilder sb = c.TranslateToBuilder(77, argument.AsSpan(), capitalize);
            string viaBuilder = sb.ToString();
            sb.Dispose();

            viaBuilder.Should().Be(c.Translate(77, argument, capitalize));
        }

        [Theory]
        [InlineData("plain", 5, 0)]
        [InlineData("plain", 9, 0)]
        [InlineData("plain", 3, 0)]
        [InlineData("ab\0cd", 5, 0)]
        [InlineData("", 4, 0)]
        [InlineData("abc", 3, 1)]
        [InlineData("abcdef", 6, 0)]
        public void Span_Read_Matches_ReadUnicodeLE_String(string text, int length, int oddTrailingBytes)
        {
            byte[] data = new byte[Encoding.Unicode.GetByteCount(text) + oddTrailingBytes + 4];
            Encoding.Unicode.GetBytes(text, 0, text.Length, data, 0);
            if (oddTrailingBytes > 0)
            {
                data[Encoding.Unicode.GetByteCount(text)] = 0x41;
            }

            ReadOnlySpan<byte> view = data.AsSpan(0, Encoding.Unicode.GetByteCount(text) + oddTrailingBytes);

            var a = new StackDataReader(view);
            string expected = a.ReadUnicodeLE(length);

            var b = new StackDataReader(view);
            Span<char> buffer = stackalloc char[length + 1];
            int n = b.ReadUnicodeLE(length, buffer);

            new string(buffer.Slice(0, n)).Should().Be(expected);
            b.Position.Should().Be(a.Position);
        }
    }
}
