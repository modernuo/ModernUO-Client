using System;
using System.IO;
using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // FontsLoader.ReusePixelBuffer (RenderedText's atlas path): the bitmap in the reused buffer must be the same pixels
    // as a fresh array, also after a bigger bitmap left other pixels in the buffer.
    public class FontPixelReuseTests
    {
        [Fact]
        public void Reused_Buffer_Gives_The_Same_Pixels_As_A_Fresh_Array()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "cuo-fontreuse-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);

            try
            {
                var fonts = LoadFonts(tmp);

                const string big = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

                foreach (ushort flags in new ushort[] { 0, 0x0001, 0x0008, 0x0001 | 0x0008, 0x0080 })
                {
                    foreach (string text in new[] { "A", "AA A", "A\nAA\nA" })
                    {
                        var freshU = fonts.GenerateUnicode(0, text, 0xFFFF, 0, 0, TEXT_ALIGN_TYPE.TS_LEFT, flags, false, 0);
                        var freshA = fonts.GenerateASCII(0, text, 0, 0, TEXT_ALIGN_TYPE.TS_LEFT, flags, false, 0);

                        fonts.ReusePixelBuffer = true;

                        try
                        {
                            var bigU = fonts.GenerateUnicode(0, big, 0xFFFF, 0, 30, TEXT_ALIGN_TYPE.TS_LEFT, flags, false, 0);
                            var reusedU = fonts.GenerateUnicode(0, text, 0xFFFF, 0, 0, TEXT_ALIGN_TYPE.TS_LEFT, flags, false, 0);
                            Same(freshU, reusedU, $"unicode '{text}' flags {flags}");
                            reusedU.Data.Should().BeSameAs(bigU.Data, "both bitmaps go into the reused buffer");

                            fonts.GenerateASCII(0, big, 0, 30, TEXT_ALIGN_TYPE.TS_LEFT, flags, false, 0);
                            var reusedA = fonts.GenerateASCII(0, text, 0, 0, TEXT_ALIGN_TYPE.TS_LEFT, flags, false, 0);
                            Same(freshA, reusedA, $"ascii '{text}' flags {flags}");
                        }
                        finally
                        {
                            fonts.ReusePixelBuffer = false;
                        }

                        fonts.GenerateUnicode(0, text, 0xFFFF, 0, 0, TEXT_ALIGN_TYPE.TS_LEFT, flags, false, 0).Data
                            .Should().NotBeSameAs(freshU.Data, "without the switch every bitmap is its own array");
                    }
                }

                fonts.ClearResources();
            }
            finally
            {
                Directory.Delete(tmp, true);
            }
        }

        private static FontsLoader LoadFonts(string tmp)
        {
            var ascii = new AsciiGlyph[224];
            ascii[33] = new AsciiGlyph(2, 3, new ushort[] { 0x7FFF, 0, 0x7FFF, 0x1234, 0x7FFF, 0 });
            var asciiFont = AsciiFontCodec.Encode(ascii);
            var aw = new RecordStoreWriter(1);
            aw.Slots[0] = new StoreSlot { Record = (uint)aw.Add(asciiFont, (uint)asciiFont.Length, StoreConst.CodecRaw, 224, 0) + 1 };
            var asciiPath = Path.Combine(tmp, "fonts.bin");
            aw.Write(asciiPath, TextConst.MagicFontsAscii, 0);

            var glyphs = new UnicodeGlyph[256];
            glyphs[65] = new UnicodeGlyph(0, 1, 8, 3, new byte[] { 0xFF, 0x81, 0x3C });
            var page = UnicodePageCodec.Encode(glyphs);
            var uw = new RecordStoreWriter(TextConst.UnicodeSlots);
            uw.Slots[0] = new StoreSlot { Record = (uint)uw.Add(page, (uint)page.Length, StoreConst.CodecRaw, 0, 0, 1) + 1 };
            var uniPath = Path.Combine(tmp, "unifont.bin");
            uw.Write(uniPath, TextConst.MagicFontsUnicode, 1);

            var fonts = new UOFileManager(ClientVersion.CV_7090, tmp, null).Fonts;
            fonts.LoadStores(asciiPath, uniPath);

            return fonts;
        }

        [Fact]
        public void Another_Thread_Gets_Its_Own_Array()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "cuo-fontreuse-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);

            try
            {
                var fonts = LoadFonts(tmp);
                fonts.ReusePixelBuffer = true;
                var mine = fonts.GenerateUnicode(0, "AA A", 0xFFFF, 0, 0, TEXT_ALIGN_TYPE.TS_LEFT, 0, false, 0);
                fonts.GenerateUnicode(0, "A", 0xFFFF, 0, 0, TEXT_ALIGN_TYPE.TS_LEFT, 0, false, 0).Data
                    .Should().BeSameAs(mine.Data, "the creating thread reuses the buffer");

                FontsLoader.FontInfo other = default;
                var t = new System.Threading.Thread(() =>
                {
                    other = fonts.GenerateUnicode(0, "AA A", 0xFFFF, 0, 0, TEXT_ALIGN_TYPE.TS_LEFT, 0, false, 0);
                });
                t.Start();
                t.Join();

                fonts.ReusePixelBuffer = false;
                other.Data.Should().NotBeNull();
                other.Data.Should().NotBeSameAs(mine.Data, "another thread must not write into the shared buffer");
                other.Data.Length.Should().Be(other.Width * other.Height);
                other.Data.AsSpan().SequenceEqual(fonts.GenerateUnicode(0, "AA A", 0xFFFF, 0, 0, TEXT_ALIGN_TYPE.TS_LEFT, 0, false, 0).Data).Should().BeTrue();
                fonts.ClearResources();
            }
            finally
            {
                Directory.Delete(tmp, true);
            }
        }

        private static void Same(FontsLoader.FontInfo fresh, FontsLoader.FontInfo reused, string what)
        {
            reused.Width.Should().Be(fresh.Width, what);
            reused.Height.Should().Be(fresh.Height, what);
            reused.LineCount.Should().Be(fresh.LineCount, what);
            fresh.Data.Should().NotBeNull(what);
            fresh.Data.Length.Should().Be(fresh.Width * fresh.Height, what);
            reused.Data.AsSpan(0, fresh.Width * fresh.Height).SequenceEqual(fresh.Data).Should().BeTrue(what);
        }
    }
}
