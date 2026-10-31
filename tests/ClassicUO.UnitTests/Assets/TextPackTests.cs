using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClassicUO.Assets;
using ClassicUO.IO;
using ClassicUO.Utility;
using FluentAssertions;
using Uo.Pack;
using Xunit;

namespace ClassicUO.UnitTests.Assets
{
    // Text in the pack (pack-format.md "Text: fonts, cliloc, speech", poc-log "Text in the pack"): the pack path must
    // give exactly what the MUL path gives, for every glyph of every font, every cliloc id of every language and every
    // speech entry; text rendered from either must be the same pixels.
    //
    // The pack with a text section: CUO_TEST_TEXT_PACK, else C:\Repositories\uo-packs\classic-1.0 once it has one. Tests
    // that need data they don't find return early.
    public class TextPackTests
    {
        private const string UoDir = @"C:\Ultima Online Classic";
        private const string DefaultPack = @"C:\Repositories\uo-packs\classic-1.0";

        private static string TextPackDir()
        {
            var env = Environment.GetEnvironmentVariable("CUO_TEST_TEXT_PACK");
            var dir = string.IsNullOrEmpty(env) ? DefaultPack : env;

            return File.Exists(Path.Combine(dir, TextConst.ClilocFile)) && Directory.Exists(UoDir) ? dir : null;
        }

        private static ClientVersion Version()
        {
            ClientVersionHelper.TryParseFromFile(Path.Combine(UoDir, "client.exe"), out var text);
            ClientVersionHelper.IsClientVersionValid(text, out var cv);

            return cv;
        }

        private static (UOFileManager Mul, UOFileManager Pack) Managers(PackAssets assets)
        {
            var cv = Version();

            return (new UOFileManager(cv, UoDir, assets), new UOFileManager(cv, UoDir, assets));
        }

        private static List<string> Languages(string packDir)
        {
            using var store = RecordStore.Open(Path.Combine(packDir, TextConst.ClilocFile), TextConst.MagicCliloc);

            return store.Records.Select(r => ClilocTable.UnpackLanguage(r.Meta0, r.Meta1)).ToList();
        }

        // ------------------------------------------------------------------ fonts

        [Fact]
        public void Every_Glyph_Of_Every_Font_Matches_The_Mul_Loader()
        {
            var dir = TextPackDir();

            if (dir == null)
            {
                return;
            }

            using var assets = PackAssets.Open(dir);
            var (mulFm, packFm) = Managers(assets);
            var mul = mulFm.Fonts;
            mul.UsePack = false;
            mul.Load();
            var pack = packFm.Fonts;
            pack.UsePack = true;
            pack.Load();

            pack.AsciiFromPack.Should().BeTrue();
            pack.UnicodeFromPack.Should().BeTrue();
            mul.AsciiFromPack.Should().BeFalse();
            pack.LoadedUnicodePages.Should().Be(0, "unicode pages are read on first use");
            pack.FontCount.Should().Be(mul.FontCount);

            int bad = 0, ascii = 0;

            for (byte f = 0; f < mul.FontCount; f++)
            {
                for (int j = 0; j < TextConst.AsciiGlyphs; j++)
                {
                    var a = mul.GetAsciiGlyph(f, j);
                    var b = pack.GetAsciiGlyph(f, j);
                    ascii++;

                    if (a.Width != b.Width || a.Height != b.Height || (a.Data == null) != (b.Data == null) || (a.Data != null && !a.Data.AsSpan().SequenceEqual(b.Data)))
                    {
                        bad++;
                    }
                }
            }

            long glyphs = 0;

            for (byte f = 0; f < TextConst.UnicodeFonts; f++)
            {
                pack.UnicodeFontExists(f).Should().Be(mul.UnicodeFontExists(f));

                for (int c = 0; c < 0x10000; c++)
                {
                    var a = mul.GetUnicodeGlyph(f, (char)c);
                    var b = pack.GetUnicodeGlyph(f, (char)c);

                    if (a.Data != null)
                    {
                        glyphs++;
                    }

                    if (a.OffsetX != b.OffsetX || a.OffsetY != b.OffsetY || a.Width != b.Width || a.Height != b.Height ||
                        (a.Data == null) != (b.Data == null) || (a.Data != null && !a.Data.AsSpan().SequenceEqual(b.Data)))
                    {
                        bad++;
                    }
                }
            }

            bad.Should().Be(0);
            ascii.Should().BeGreaterThan(2000);
            glyphs.Should().BeGreaterThan(10000);
        }

        private static readonly string[] Samples =
        {
            "Hello, Britannia!",
            "You see: a bank box (37 items, 1,234 stones)",
            "The quick brown fox jumps over the lazy dog 0123456789 !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~",
            "Ünïcödé àéîõü ßøæ ÇÑ",
            "日本語のテキスト 中文字符 한국어 텍스트",
            "Line one\nLine two that is quite a bit longer than the first one, so it wraps",
            "\u0001\u001F\u007F\u00A0\uFFFF",
        };

        [Fact]
        public void Rendered_Text_Is_Identical_From_Pack_And_Mul()
        {
            var dir = TextPackDir();

            if (dir == null)
            {
                return;
            }

            using var assets = PackAssets.Open(dir);
            var (mulFm, packFm) = Managers(assets);

            foreach (var (fm, usePack) in new[] { (mulFm, false), (packFm, true) })
            {
                fm.Hues.Load();
                fm.Fonts.UsePack = usePack;
                fm.Fonts.Load();
            }

            int compared = 0;

            foreach (var s in Samples)
            {
                for (byte f = 0; f < TextConst.UnicodeFonts; f++)
                {
                    if (!mulFm.Fonts.UnicodeFontExists(f))
                    {
                        continue;
                    }

                    foreach (ushort flags in new ushort[] { 0, 0x0001, 0x0002, 0x0008, 0x0010, 0x0080 })
                    {
                        var a = mulFm.Fonts.GenerateUnicode(f, s, 0x0021, 30, 200, TEXT_ALIGN_TYPE.TS_LEFT, flags, false, 0);
                        var b = packFm.Fonts.GenerateUnicode(f, s, 0x0021, 30, 200, TEXT_ALIGN_TYPE.TS_LEFT, flags, false, 0);
                        Same(a, b).Should().BeTrue($"unicode font {f}, flags 0x{flags:X4}, '{s}'");
                        mulFm.Fonts.GetWidthUnicode(f, s).Should().Be(packFm.Fonts.GetWidthUnicode(f, s));
                        compared++;
                    }
                }

                for (byte f = 0; f < mulFm.Fonts.FontCount; f++)
                {
                    foreach (ushort hue in new ushort[] { 0, 0x0035, 0x0481 })
                    {
                        var a = mulFm.Fonts.GenerateASCII(f, s, hue, 200, TEXT_ALIGN_TYPE.TS_CENTER, 0, false, 0);
                        var b = packFm.Fonts.GenerateASCII(f, s, hue, 200, TEXT_ALIGN_TYPE.TS_CENTER, 0, false, 0);
                        Same(a, b).Should().BeTrue($"ASCII font {f}, hue 0x{hue:X4}, '{s}'");
                        mulFm.Fonts.GetWidthASCII(f, s).Should().Be(packFm.Fonts.GetWidthASCII(f, s));
                        compared++;
                    }
                }
            }

            compared.Should().BeGreaterThan(100);
        }

        private static bool Same(FontsLoader.FontInfo a, FontsLoader.FontInfo b) =>
            a.Width == b.Width && a.Height == b.Height && a.LineCount == b.LineCount && (a.Data == null) == (b.Data == null) &&
            (a.Data == null || a.Data.AsSpan().SequenceEqual(b.Data));

        [Fact]
        public void Glyph_Pages_Load_From_Many_Threads()
        {
            var dir = TextPackDir();

            if (dir == null)
            {
                return;
            }

            using var assets = PackAssets.Open(dir);
            var (mulFm, packFm) = Managers(assets);
            mulFm.Fonts.UsePack = false;
            mulFm.Fonts.Load();
            packFm.Fonts.Load();
            int bad = 0;

            foreach (var fonts in new[] { mulFm.Fonts, packFm.Fonts })
            {
                Parallel.For(0, 8, t =>
                {
                    var rng = new Random(t);

                    for (int i = 0; i < 20000; i++)
                    {
                        byte f = (byte)rng.Next(TextConst.UnicodeFonts);
                        char c = (char)rng.Next(0x10000);
                        var g = fonts.GetUnicodeGlyph(f, c);
                        var again = fonts.GetUnicodeGlyph(f, c);

                        if (!ReferenceEquals(g.Data, again.Data))
                        {
                            System.Threading.Interlocked.Increment(ref bad);
                        }
                    }
                });
            }

            bad.Should().Be(0);
        }

        // ------------------------------------------------------------------ cliloc

        [Fact]
        public void Every_Cliloc_Id_Of_Every_Language_Matches_The_Mul_Loader()
        {
            var dir = TextPackDir();

            if (dir == null)
            {
                return;
            }

            using var assets = PackAssets.Open(dir);
            var languages = Languages(dir);
            languages.Should().Contain("enu");
            long checkedIds = 0;

            foreach (var lang in languages)
            {
                var (mulFm, packFm) = Managers(assets);
                var mul = mulFm.Clilocs;
                mul.UsePack = false;
                mul.Load(lang);
                var pack = packFm.Clilocs;
                pack.Load(lang);
                pack.FromPack.Should().BeTrue();
                mul.FromPack.Should().BeFalse();
                pack.LanguageTable.Materialized.Should().Be(0, "strings are created on first use");

                var ids = new HashSet<int>();

                foreach (var t in new[] { mul.LanguageTable, mul.EnglishTable, pack.LanguageTable, pack.EnglishTable })
                {
                    for (int i = 0; t != null && i < t.Count; i++)
                    {
                        ids.Add(t.IdAt(i));
                    }
                }

                int bad = 0;

                foreach (int id in ids)
                {
                    if (mul.GetString(id) != pack.GetString(id))
                    {
                        bad++;
                    }
                }

                // absent ids: the same fallback and missing text
                foreach (int id in new[] { -1, 0, 1, 499999, 3011033, int.MaxValue })
                {
                    if (!ids.Contains(id))
                    {
                        pack.GetString(id).Should().Be(mul.GetString(id));
                        pack.GetString(id, "fb").Should().Be("fb");
                    }
                }

                bad.Should().Be(0, $"language {lang}");
                checkedIds += ids.Count;
            }

            checkedIds.Should().BeGreaterThan(100000);
        }

        [Fact]
        public void Cliloc_Formatting_Is_Unchanged()
        {
            var dir = TextPackDir();

            if (dir == null)
            {
                return;
            }

            using var assets = PackAssets.Open(dir);
            var (mulFm, packFm) = Managers(assets);
            mulFm.Clilocs.UsePack = false;
            mulFm.Clilocs.Load("enu");
            packFm.Clilocs.Load("enu");
            var mul = mulFm.Clilocs;
            var pack = packFm.Clilocs;

            // every string with a ~n_NAME~ argument, with plain, #cliloc and numeric arguments
            var t = pack.LanguageTable;
            int n = 0;

            for (int i = 0; i < t.Count; i++)
            {
                int id = t.IdAt(i);
                var s = pack.GetString(id);

                if (!s.Contains('~'))
                {
                    continue;
                }

                foreach (var arg in new[] { "", "a\tb", "#1011000\t#3000000", "1042971\t5", "x\t\t#\t#abc" })
                {
                    pack.Translate(id, arg).Should().Be(mul.Translate(id, arg));
                    pack.Translate(id, arg, true).Should().Be(mul.Translate(id, arg, true));
                }

                n++;
            }

            n.Should().BeGreaterThan(1000);
            pack.GetString(1042971, true).Should().Be(mul.GetString(1042971, true));
        }

        [Fact]
        public void Cliloc_Strings_Are_Created_Once_From_Many_Threads()
        {
            var dir = TextPackDir();

            if (dir == null)
            {
                return;
            }

            using var assets = PackAssets.Open(dir);
            var (_, packFm) = Managers(assets);
            packFm.Clilocs.Load("enu");
            var t = packFm.Clilocs.LanguageTable;
            var seen = new string[t.Count];
            int bad = 0;

            Parallel.For(0, 8, k =>
            {
                for (int i = 0; i < t.Count; i++)
                {
                    int idx = (i * 7919 + k * 104729) % t.Count;
                    var s = packFm.Clilocs.GetString(t.IdAt(idx));
                    var prev = System.Threading.Interlocked.CompareExchange(ref seen[idx], s, null);

                    if (prev != null && !ReferenceEquals(prev, s))
                    {
                        System.Threading.Interlocked.Increment(ref bad);
                    }
                }
            });

            bad.Should().Be(0);
            t.Materialized.Should().Be(t.Count);
        }

        // ------------------------------------------------------------------ speech

        [Fact]
        public void Every_Speech_Entry_Matches_The_Mul_Loader()
        {
            var dir = TextPackDir();

            if (dir == null)
            {
                return;
            }

            using var assets = PackAssets.Open(dir);
            var (mulFm, packFm) = Managers(assets);
            mulFm.Speeches.UsePack = false;
            mulFm.Speeches.Load();
            packFm.Speeches.Load();
            packFm.Speeches.FromPack.Should().BeTrue();
            var a = mulFm.Speeches.Entries;
            var b = packFm.Speeches.Entries;
            b.Length.Should().Be(a.Length);
            a.Length.Should().BeGreaterThan(100);

            for (int i = 0; i < a.Length; i++)
            {
                b[i].KeywordID.Should().Be(a[i].KeywordID);
                b[i].CheckStart.Should().Be(a[i].CheckStart);
                b[i].CheckEnd.Should().Be(a[i].CheckEnd);
                b[i].Keywords.Should().Equal(a[i].Keywords);
            }

            // matching: every keyword alone, in a sentence, and some phrases
            var inputs = new List<string> { "bank", "  vendor buy  ", "i wish to lock this down", "guards!", "BANKING", "hello there", "train magery", "" };

            foreach (var e in a)
            {
                foreach (var k in e.Keywords)
                {
                    inputs.Add(k);
                    inputs.Add($"hey {k}!");
                }
            }

            foreach (var s in inputs)
            {
                var ka = mulFm.Speeches.GetKeywords(s).Select(x => x.KeywordID).ToArray();
                var kb = packFm.Speeches.GetKeywords(s).Select(x => x.KeywordID).ToArray();
                kb.Should().Equal(ka, s);
            }
        }

        // ------------------------------------------------------------------ untrusted input (no data needed)

        [Fact]
        public void Cliloc_Table_Rejects_Malformed_Payloads()
        {
            var good = ClilocTable.Build(new List<(int, byte[])> { (5, Encoding.UTF8.GetBytes("five")), (1, Encoding.UTF8.GetBytes("one")), (9, Array.Empty<byte>()) });
            var t = ClilocTable.TryParse((byte[])good.Clone());
            t.Should().NotBeNull();
            t.Count.Should().Be(3);
            t.TryGetString(1, out var s1).Should().BeTrue();
            s1.Should().Be("one");
            t.TryGetString(9, out var s9).Should().BeTrue();
            s9.Should().Be("");
            t.TryGetString(2, out _).Should().BeFalse();

            ClilocTable.TryParse(Array.Empty<byte>()).Should().BeNull();
            ClilocTable.TryParse(good.AsSpan(0, good.Length - 1).ToArray()).Should().BeNull("truncated");

            var huge = (byte[])good.Clone();
            BitConverter.GetBytes(int.MaxValue).CopyTo(huge, 0);
            ClilocTable.TryParse(huge).Should().BeNull("a count that doesn't fit");

            var unsorted = (byte[])good.Clone();
            BitConverter.GetBytes(7).CopyTo(unsorted, 8); // ids 7, 5, 9
            ClilocTable.TryParse(unsorted).Should().BeNull("ids not ascending");

            var badEnd = (byte[])good.Clone();
            BitConverter.GetBytes(100u).CopyTo(badEnd, 8 + 12); // first end past the blob
            ClilocTable.TryParse(badEnd).Should().BeNull("an end past the blob");

            // invalid UTF-8 decodes with replacement characters, as the MUL path does
            var bin = ClilocTable.TryParse(ClilocTable.Build(new List<(int, byte[])> { (1, new byte[] { 0xFF, 0xC3 }) }));
            bin.TryGetString(1, out var r).Should().BeTrue();
            r.Should().Be(Encoding.UTF8.GetString(new byte[] { 0xFF, 0xC3 }));

            ClilocTable.UnpackLanguage(0x6E65, 0x0075).Should().Be("enu");
            ClilocTable.UnpackLanguage(0x2F65, 0x0075).Should().BeNull();
            ClilocTable.UnpackLanguage(0x0065, 0x0075).Should().BeNull("a gap");
        }

        [Fact]
        public void Cliloc_Mul_Index_Matches_ReadUtf8()
        {
            // the MUL path's slice rule against StackDataReader.ReadUTF8, on records with NULs, short tails and
            // negative lengths
            var rng = new Random(3);

            for (int round = 0; round < 500; round++)
            {
                var buf = new byte[rng.Next(1, 64)];
                rng.NextBytes(buf);

                for (int i = 0; i < buf.Length; i++)
                {
                    if (rng.Next(6) == 0)
                    {
                        buf[i] = 0;
                    }
                }

                int start = rng.Next(buf.Length);
                short length = (short)rng.Next(-3, 70);
                var a = new StackDataReader(buf);
                a.Skip(start);
                string want = a.ReadUTF8(length);
                var b = new StackDataReader(buf);
                b.Skip(start);
                var (s, l) = ClilocLoader.Utf8Slice(ref b, buf, length);
                Encoding.UTF8.GetString(buf, s, l).Should().Be(want);
                b.Position.Should().Be(a.Position);
            }
        }

        [Fact]
        public void Font_Codecs_Round_Trip_And_Reject_Malformed_Pages()
        {
            var glyphs = new UnicodeGlyph[256];
            glyphs[65] = new UnicodeGlyph(1, -2, 9, 3, new byte[] { 1, 2, 3, 4, 5, 6 });
            glyphs[255] = new UnicodeGlyph(0, 0, 1, 1, new byte[] { 0x80 });
            var page = UnicodePageCodec.Encode(glyphs);
            UnicodePageCodec.Validate(page).Should().Be(2);
            var h = UnicodePageCodec.Header(page, 65);
            UnicodePageCodec.Bits(page, h).ToArray().Should().Equal(1, 2, 3, 4, 5, 6);

            UnicodePageCodec.Validate(page.AsSpan(0, page.Length - 1)).Should().Be(-1, "truncated rows");
            UnicodePageCodec.Validate(page.AsSpan(0, 100)).Should().Be(-1, "short table");
            var bad = (byte[])page.Clone();
            BitConverter.GetBytes(0x7FFFFFF0u).CopyTo(bad, 65 * 8 + 4);
            UnicodePageCodec.Validate(bad).Should().Be(-1, "offset past the end");
            bad = (byte[])page.Clone();
            bad[3 * 8 + 2] = 1; // an absent glyph with a width
            UnicodePageCodec.Validate(bad).Should().Be(-1);
            bad = (byte[])page.Clone();
            bad[65 * 8 + 2] = 0x80; // negative width on a present glyph
            UnicodePageCodec.Validate(bad).Should().Be(-1);

            var ascii = new AsciiGlyph[224];
            ascii[0] = new AsciiGlyph(2, 1, new ushort[] { 0x7FFF, 0 });
            ascii[1] = new AsciiGlyph(0, 0, Array.Empty<ushort>());
            ascii[2] = new AsciiGlyph(0, 0, null);
            var font = AsciiFontCodec.Encode(ascii);
            var back = new AsciiGlyph[224];
            AsciiFontCodec.TryDecode(font, back).Should().BeTrue();
            back[0].Texels.Should().Equal((ushort)0x7FFF, (ushort)0);
            back[1].Texels.Should().NotBeNull().And.BeEmpty();
            back[2].Texels.Should().BeNull();
            AsciiFontCodec.TryDecode(font.AsSpan(0, font.Length - 1), back).Should().BeFalse();
            var badFont = (byte[])font.Clone();
            badFont[5 * 4 + 2] = 4; // unknown flag
            AsciiFontCodec.TryDecode(badFont, back).Should().BeFalse();
        }

        [Fact]
        public void Corrupt_Font_Records_Are_Absent_Not_Thrown()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "cuo-text-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);

            try
            {
                // ASCII: font 0 good, font 1 a truncated payload (header claims texels it doesn't have)
                var ascii = new AsciiGlyph[224];
                ascii[33] = new AsciiGlyph(2, 2, new ushort[] { 1, 2, 3, 4 });
                var good = AsciiFontCodec.Encode(ascii);
                var aw = new RecordStoreWriter(2);
                aw.Slots[0] = new StoreSlot { Record = (uint)aw.Add(good, (uint)good.Length, StoreConst.CodecRaw, 224, 0) + 1 };
                var trunc = good.AsSpan(0, good.Length - 2).ToArray();
                aw.Slots[1] = new StoreSlot { Record = (uint)aw.Add(trunc, (uint)trunc.Length, StoreConst.CodecRaw, 224, 0) + 1 };
                var asciiPath = Path.Combine(tmp, "fonts.bin");
                aw.Write(asciiPath, TextConst.MagicFontsAscii, 0);

                // unicode font 0: page 0 good, page 1 malformed, page 2 corrupt Brotli, page 3 a 1 GiB raw size
                var glyphs = new UnicodeGlyph[256];
                glyphs[65] = new UnicodeGlyph(0, 1, 8, 2, new byte[] { 0xFF, 0x81 });
                var page = UnicodePageCodec.Encode(glyphs);
                var malformed = (byte[])page.Clone();
                BitConverter.GetBytes(0x10000u).CopyTo(malformed, 65 * 8 + 4);
                var uw = new RecordStoreWriter(TextConst.UnicodeSlots);
                uw.Slots[0] = new StoreSlot { Record = (uint)uw.Add(page, (uint)page.Length, StoreConst.CodecRaw, 0, 0, 1) + 1 };
                uw.Slots[1] = new StoreSlot { Record = (uint)uw.Add(malformed, (uint)malformed.Length, StoreConst.CodecRaw, 0, 1, 1) + 1 };
                uw.Slots[2] = new StoreSlot { Record = (uint)uw.Add(new byte[] { 1, 2, 3, 4, 5 }, (uint)page.Length, StoreConst.CodecBrotli, 0, 2, 1) + 1 };
                uw.Slots[3] = new StoreSlot { Record = (uint)uw.Add(new byte[] { 1, 2, 3 }, 1u << 30, StoreConst.CodecBrotli, 0, 3, 1) + 1 };
                var uniPath = Path.Combine(tmp, "unifont.bin");
                uw.Write(uniPath, TextConst.MagicFontsUnicode, 1);

                var fm = new UOFileManager(ClientVersion.CV_7090, tmp, null);
                var fonts = fm.Fonts;
                fonts.LoadStores(asciiPath, uniPath);

                fonts.FontCount.Should().Be(2);
                fonts.GetAsciiGlyph(0, 33).Data.Should().Equal((ushort)1, (ushort)2, (ushort)3, (ushort)4);
                fonts.GetAsciiGlyph(1, 33).Width.Should().Be(0, "a corrupt font is empty");
                fonts.GetUnicodeGlyph(0, 'A').Data.Should().Equal((byte)0xFF, (byte)0x81);

                foreach (int p in new[] { 1, 2, 3 })
                {
                    for (int k = 0; k < 2; k++)
                    {
                        fonts.GetUnicodeGlyph(0, (char)(p * 256 + 65)).Data.Should().BeNull();
                    }
                }

                fonts.UnicodeFontExists(0).Should().BeTrue();
                fonts.UnicodeFontExists(1).Should().BeFalse();
                fonts.GetWidthUnicode(0, "A\u0141\u0241").Should().Be(fonts.GetWidthUnicode(0, "A"));
                fonts.ClearResources();

                // a store whose tables run past the file: rejected as a whole
                var broken = Path.Combine(tmp, "broken.bin");
                File.WriteAllBytes(broken, File.ReadAllBytes(uniPath).AsSpan(0, 100).ToArray());
                Action open = () => fonts.LoadStores(asciiPath, broken);
                open.Should().Throw<InvalidDataException>();
            }
            finally
            {
                Directory.Delete(tmp, true);
            }
        }

        [Fact]
        public void Speech_Table_Round_Trips_And_Rejects_Malformed_Files()
        {
            var tmp = Path.GetTempFileName();

            try
            {
                SpeechFile.Write(tmp, new List<(ushort, byte[])> { (3, Encoding.UTF8.GetBytes("*bank*")), (0x10, Encoding.UTF8.GetBytes("vendor buy")) });
                var r = SpeechFile.Read(tmp);
                r.Should().Equal(((ushort)3, "*bank*"), ((ushort)0x10, "vendor buy"));

                var bytes = File.ReadAllBytes(tmp);
                File.WriteAllBytes(tmp, bytes.AsSpan(0, bytes.Length - 1).ToArray());
                Action trunc = () => SpeechFile.Read(tmp);
                trunc.Should().Throw<InvalidDataException>();

                var badEnd = (byte[])bytes.Clone();
                BitConverter.GetBytes(1000u).CopyTo(badEnd, 16 + 8 + 4);
                File.WriteAllBytes(tmp, badEnd);
                Action end = () => SpeechFile.Read(tmp);
                end.Should().Throw<InvalidDataException>();

                var huge = (byte[])bytes.Clone();
                BitConverter.GetBytes(uint.MaxValue).CopyTo(huge, 16);
                File.WriteAllBytes(tmp, huge);
                Action count = () => SpeechFile.Read(tmp);
                count.Should().Throw<InvalidDataException>();
            }
            finally
            {
                File.Delete(tmp);
            }
        }
    }
}
