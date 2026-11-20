using ClassicUO.Assets;
using ClassicUO.Utility;
using FluentAssertions;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // ASCII fonts are indexed by Cp1252 byte; text arrives Cp1252-decoded, so the glyph lookup maps each char back to its byte.
    public class AsciiGlyphIndexTests
    {
        [Fact]
        public void Every_Printable_Cp1252_Byte_Indexes_Its_Own_Glyph()
        {
            for (int b = 0x20; b <= 0xFF; b++)
            {
                string decoded = StringHelper.Cp1252ToString(new[] { (byte)b });

                decoded.Should().HaveLength(1);
                FontsLoader.GetASCIIIndex(decoded[0]).Should().Be(b - 0x20, $"byte 0x{b:X2} ('{decoded}')");
            }
        }

        [Fact]
        public void Moon_Phase_Bytes_Round_Trip()
        {
            for (int b = 0x80; b <= 0x87; b++)
            {
                char c = StringHelper.Cp1252ToString(new[] { (byte)b })[0];
                Cp1252.ToGlyphByte(c).Should().Be((byte)b);
            }
        }

        [Fact]
        public void Typographic_Chars_Do_Not_Truncate()
        {
            FontsLoader.GetASCIIIndex('\u2026').Should().Be(0x85 - 0x20, "… is 0x85, not '&' (0x26)");
            FontsLoader.GetASCIIIndex('\u2122').Should().Be(0x99 - 0x20);
            FontsLoader.GetASCIIIndex('\u4E2D').Should().Be('?' - 0x20, "outside Cp1252");
            FontsLoader.GetASCIIIndex('\x07').Should().Be(0, "control chars index glyph 0 as before");
            FontsLoader.GetASCIIIndex('A').Should().Be('A' - 0x20);
        }
    }
}
